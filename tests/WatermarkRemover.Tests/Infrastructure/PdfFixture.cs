using System.Text;

namespace WatermarkRemover.Tests.Infrastructure;

/// <summary>
/// Builds byte-exact PDF fixtures by hand. Fixtures deliberately avoid the PDF library used by
/// production code, so tests can never be "correct" merely because both sides share a bug.
/// </summary>
public sealed class PdfFixture
{
    private readonly List<(string Name, byte[] Body)> _objects = new();

    /// <summary>
    /// Mirrors a CamScanner export: one large scanned image, one small bottom-right badge and a
    /// link annotation that sits exactly on the badge.
    /// </summary>
    public static PdfFixture CamScannerLike() =>
        new PdfFixture()
            .WithMediaBox(0, 0, 842, 595)
            .WithFormXObject("Im1")
            .WithFormXObject("Im2")
            .WithContent(
                "q\n" +
                "q 720.4683 0 0 543 60.76585 52 cm /Im1 Do Q\n" +
                "q 104 0 0 30 723 10 cm /Im2 Do Q\n" +
                "Q\n")
            .WithLinkAnnotation(723, 10, 827, 40, "https://v3.camscanner.com/user/download");

    public PdfFixture WithMediaBox(double x1, double y1, double x2, double y2)
    {
        _mediaBox = (x1, y1, x2, y2);
        return this;
    }

    public PdfFixture WithContent(string content)
    {
        _content = content;
        return this;
    }

    public PdfFixture WithFormXObject(string name)
    {
        _formNames.Add(name);
        return this;
    }

    public PdfFixture WithLinkAnnotation(double x1, double y1, double x2, double y2, string? uri = null)
    {
        _annotations.Add((x1, y1, x2, y2, uri));
        return this;
    }

    private (double X1, double Y1, double X2, double Y2) _mediaBox = (0, 0, 612, 792);
    private string _content = "q Q\n";
    private readonly List<string> _formNames = new();
    private readonly List<(double X1, double Y1, double X2, double Y2, string? Uri)> _annotations = new();

    /// <summary>Serialises the fixture to a complete, valid PDF file.</summary>
    public byte[] Build()
    {
        // Object numbering: 1=Catalog 2=Pages 3=Page 4=Resources 5=Contents 6..=XObjects 7..=Annots
        var objects = new List<string>();

        var xobjectEntries = new StringBuilder();
        var xobjectRefs = new List<int>();
        int nextId = 6;
        foreach (string name in _formNames)
        {
            xobjectEntries.Append($"/{name} {nextId} 0 R ");
            xobjectRefs.Add(nextId);
            nextId++;
        }

        var annotRefs = new List<int>();
        foreach (var _ in _annotations) annotRefs.Add(nextId++);

        string annotsArray = annotRefs.Count > 0
            ? "[" + string.Join(" ", annotRefs.Select(id => $"{id} 0 R")) + "]"
            : "";

        objects.Add($"<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        objects.Add($"<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                 // 2
        objects.Add(
            "<< /Type /Page /Parent 2 0 R " +
            $"/MediaBox [{_mediaBox.X1} {_mediaBox.Y1} {_mediaBox.X2} {_mediaBox.Y2}] " +
            "/Resources 4 0 R /Contents 5 0 R" +
            (annotsArray.Length > 0 ? $" /Annots {annotsArray}" : "") +
            " >>");                                                                               // 3
        objects.Add($"<< /XObject << {xobjectEntries}>> >>");                                     // 4
        objects.Add($"stream:{_content}");                                                        // 5

        int id = 6;
        foreach (string _ in _formNames)
        {
            objects.Add("form:" + id);
            id++;
        }

        foreach (var (x1, y1, x2, y2, uri) in _annotations)
        {
            string uriPart = uri == null ? "" : $" /A << /S /URI /URI ({uri}) >>";
            objects.Add($"<< /Type /Annot /Subtype /Link /Rect [{x1} {y1} {x2} {y2}] /Border [0 0 0]{uriPart} >>");
        }

        return Serialise(objects);
    }

    private static byte[] Serialise(List<string> objects)
    {
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        Write("%PDF-1.4\n%âãÏÓ\n");

        var offsets = new int[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = (int)ms.Length;
            string body = objects[i];
            Write($"{i + 1} 0 obj\n");

            if (body.StartsWith("stream:", StringComparison.Ordinal))
            {
                string payload = body["stream:".Length..];
                byte[] data = Encoding.Latin1.GetBytes(payload);
                Write($"<< /Length {data.Length} >>\nstream\n");
                ms.Write(data, 0, data.Length);
                Write("\nendstream\n");
            }
            else if (body.StartsWith("form:", StringComparison.Ordinal))
            {
                // Form XObject: unit square, empty painting program.
                byte[] data = Encoding.Latin1.GetBytes("");
                Write("<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 1 1] /Resources << >> /Length 0 >>\n");
                Write("stream\n");
                ms.Write(data, 0, data.Length);
                Write("endstream\n");
            }
            else
            {
                Write(body);
                Write("\n");
            }

            Write("endobj\n");
        }

        int xrefOffset = (int)ms.Length;
        Write($"xref\n0 {objects.Count + 1}\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
