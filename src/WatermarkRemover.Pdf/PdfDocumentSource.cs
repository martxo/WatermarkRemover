using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.Annotations;
using PdfSharpCore.Pdf.IO;
using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Pdf;

/// <summary>
/// PdfSharpCore-backed implementation of <see cref="IDocumentSource"/>. This is the only place in
/// the solution that talks to a PDF library, so swapping libraries or adding formats stays local.
/// </summary>
/// <remarks>
/// Content streams are read through the page's real indirect stream objects (never through
/// <c>CreateSingleContent</c>, which synthesises a detached object). Edits are written straight to
/// the same indirect streams, so they survive saving and round-tripping.
/// </remarks>
public sealed class PdfDocumentSource : IDocumentSource
{
    private static readonly string[] Extensions = { ".pdf" };

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public RawDocument Extract(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input path must not be empty.", nameof(inputPath));

        PdfDocument? doc = null;
        try
        {
            doc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
            var pages = new List<RawPage>(doc.PageCount);

            for (int i = 0; i < doc.PageCount; i++)
            {
                PdfPage page = doc.Pages[i];
                pages.Add(new RawPage(
                    index: i,
                    mediaBox: ToRectD(page.MediaBox),
                    contentStream: ReadContentBytes(page),
                    xObjectNames: ReadXObjectNames(page),
                    annotations: ReadAnnotations(page)));
            }

            return new RawDocument(pages);
        }
        finally
        {
            (doc as IDisposable)?.Dispose();
        }
    }

    public void Apply(string inputPath, string outputPath, IReadOnlyList<PageEdit> edits)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));
        if (edits.Count == 0)
            throw new ArgumentException("At least one page edit is required.", nameof(edits));

        PdfDocument? doc = null;
        try
        {
            doc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
            var orphanCandidates = new List<PdfObject>();

            foreach (var edit in edits)
            {
                if (edit.PageIndex < 0 || edit.PageIndex >= doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(edits), $"Page index {edit.PageIndex} is out of range.");

                PdfPage page = doc.Pages[edit.PageIndex];
                WriteContentBytes(page, edit.NewContentStream);
                RemoveResources(page, edit.RemovedResourceNames, orphanCandidates);
                RemoveAnnotations(page, edit.RemovedAnnotationIndices, orphanCandidates);
            }

            PruneOrphans(doc, orphanCandidates);
            doc.Save(outputPath);
        }
        finally
        {
            (doc as IDisposable)?.Dispose();
        }
    }

    // ---- extraction ----

    private static RectD ToRectD(PdfRectangle r) =>
        new(r.X1, r.Y1, r.X2 - r.X1, r.Y2 - r.Y1);

    private static byte[] ReadContentBytes(PdfPage page)
    {
        using var ms = new MemoryStream();
        foreach (PdfContent content in page.Contents)
        {
            PdfDictionary.PdfStream? stream = content.Stream;
            if (stream == null) continue;
            byte[]? data = stream.UnfilteredValue ?? stream.Value;
            if (data == null || data.Length == 0) continue;
            ms.Write(data, 0, data.Length);
            // Separate streams so operators can never fuse across a boundary.
            ms.WriteByte((byte)'\n');
        }

        return ms.ToArray();
    }

    private static IReadOnlyList<string> ReadXObjectNames(PdfPage page)
    {
        var names = new List<string>();
        PdfDictionary? xobjects = page.Resources.Elements["/XObject"] as PdfDictionary;
        if (xobjects == null) return names;

        foreach (PdfName name in xobjects.Elements.KeyNames)
            names.Add(TrimSlash(name.Value));

        return names;
    }

    private static IReadOnlyList<RawAnnotation> ReadAnnotations(PdfPage page)
    {
        var result = new List<RawAnnotation>();
        PdfAnnotations annotations = page.Annotations;

        for (int i = 0; i < annotations.Count; i++)
        {
            PdfAnnotation annotation = annotations[i];
            PdfRectangle? rect = annotation.Elements.GetRectangle("/Rect");
            if (rect == null) continue;

            string? uri = null;
            if (annotation.Elements.GetObject("/A") is PdfDictionary action)
                uri = action.Elements.GetString("/URI");

            result.Add(new RawAnnotation(i, ToRectD(rect), uri));
        }

        return result;
    }

    // ---- mutation ----

    private static void WriteContentBytes(PdfPage page, byte[] bytes)
    {
        bool written = false;
        foreach (PdfContent content in page.Contents)
        {
            PdfDictionary.PdfStream? stream = content.Stream;
            if (stream == null) continue;
            stream.Value = written ? Array.Empty<byte>() : bytes;
            written = true;
        }

        if (!written)
            throw new InvalidOperationException("Page has no content stream to write to.");
    }

    private static void RemoveResources(
        PdfPage page,
        IReadOnlyList<string> resourceNames,
        List<PdfObject> orphanCandidates)
    {
        if (resourceNames.Count == 0) return;

        PdfDictionary? xobjects = page.Resources.Elements["/XObject"] as PdfDictionary;
        if (xobjects == null) return;

        foreach (string name in resourceNames)
        {
            string key = "/" + TrimSlash(name);
            if (!xobjects.Elements.ContainsKey(key)) continue;

            PdfObject? target = xobjects.Elements.GetObject(key);
            xobjects.Elements.Remove(key);
            if (target != null) orphanCandidates.Add(target);
        }
    }

    private static void RemoveAnnotations(
        PdfPage page,
        IReadOnlyList<int> annotationIndices,
        List<PdfObject> orphanCandidates)
    {
        if (annotationIndices.Count == 0) return;

        PdfAnnotations annotations = page.Annotations;
        foreach (int index in annotationIndices.Distinct().OrderByDescending(i => i))
        {
            if (index < 0 || index >= annotations.Count) continue;

            PdfAnnotation annotation = annotations[index];
            CollectAnnotationReferents(annotation, orphanCandidates);
            annotations.Remove(annotation);
        }
    }

    private static void CollectAnnotationReferents(PdfAnnotation annotation, List<PdfObject> orphanCandidates)
    {
        AddReferent(annotation.Elements.GetObject("/AP"), orphanCandidates);
        AddReferent(annotation.Elements.GetObject("/A"), orphanCandidates);

        if (annotation.Elements.GetObject("/AP") is PdfDictionary appearance)
        {
            foreach (PdfName name in appearance.Elements.KeyNames)
                AddReferent(appearance.Elements.GetObject(name.Value), orphanCandidates);
        }
    }

    private static void AddReferent(PdfObject? obj, List<PdfObject> orphanCandidates)
    {
        if (obj == null || !obj.IsIndirect) return;
        if (!orphanCandidates.Any(o => ReferenceEquals(o, obj)))
            orphanCandidates.Add(obj);
    }

    /// <summary>
    /// Drops objects that are no longer reachable after the edits. Shared resources (for example a
    /// colour profile used by the page image) stay because they remain reachable from the page tree.
    /// </summary>
    private static void PruneOrphans(PdfDocument doc, List<PdfObject> orphanCandidates)
    {
        if (orphanCandidates.Count == 0) return;

        var reachable = CollectReachableIds(doc);

        foreach (PdfObject orphan in orphanCandidates.Distinct())
        {
            PdfObjectID id = PdfInternals.GetObjectID(orphan);
            if (id.ObjectNumber == 0) continue;
            if (reachable.Contains(id.ObjectNumber)) continue;

            try
            {
                doc.Internals.RemoveObject(orphan);
            }
            catch (Exception)
            {
                // An object the library refuses to drop is simply left in place; the watermark is
                // already gone from the page, so this can never corrupt the result.
            }
        }
    }

    private static HashSet<int> CollectReachableIds(PdfDocument doc)
    {
        var reachable = new HashSet<int>();
        var queue = new Queue<PdfObject>();

        PdfObject? catalog = doc.Internals.Catalog;
        if (catalog != null) queue.Enqueue(catalog);

        while (queue.Count > 0)
        {
            PdfObject current = queue.Dequeue();
            int id = PdfInternals.GetObjectID(current).ObjectNumber;
            if (id != 0 && !reachable.Add(id)) continue;

            switch (current)
            {
                case PdfDictionary dict:
                    foreach (PdfItem item in dict.Elements.Values)
                        EnqueueItem(item, queue);
                    break;

                case PdfArray array:
                    for (int i = 0; i < array.Count(); i++)
                        EnqueueItem(array.Elements[i], queue);
                    break;
            }
        }

        return reachable;
    }

    private static void EnqueueItem(PdfItem? item, Queue<PdfObject> queue)
    {
        switch (item)
        {
            case PdfReference reference:
                if (reference.Value != null) queue.Enqueue(reference.Value);
                break;

            case PdfObject obj:
                queue.Enqueue(obj);
                break;
        }
    }

    private static string TrimSlash(string name) =>
        name.StartsWith('/') ? name[1..] : name;
}
