using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Shared edit plumbing for the PDF page tools (rotate / delete / insert): copy the original,
/// apply one edit to the copy, write it to a side file and swap it over the original only after
/// the save succeeded — an interrupted edit can never leave a truncated PDF behind, and a PDF
/// open in WPS never blocks reading it.
/// </summary>
internal static class PdfDocumentEditor
{
    /// <summary>
    /// Applies one edit and returns the path holding the result. Normally that is
    /// <paramref name="pdfPath"/> itself; when the original is locked (a viewer holds it) and
    /// <paramref name="keepCopyOnLock"/> is true, the result is saved beside it as
    /// "原名 (后缀).pdf" instead of failing. Other edits throw a readable lock message.
    /// </summary>
    internal static string EditDocument(
        string pdfPath,
        Action<PdfDocument> edit,
        bool keepCopyOnLock = false,
        string fallbackSuffix = "已编辑")
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(pdfPath));

        PdfSharpRuntime.EnsureInitialized();

        // Named only when a lock message needs it: querying the Restart Manager costs a few ms
        // and the happy path must stay untouched.
        string? holders = null;
        string LockHolders() => holders ??= FileLockInspector.Describe(pdfPath);

        bool locked = IsLockedForWrite(pdfPath);
        if (locked && !keepCopyOnLock)
        {
            throw new IOException(
                $"PDF 正在被其他程序占用（{LockHolders()}），无法保存修改。请关闭后重试，原文件未改动。");
        }

        // A viewer's read lock cannot block reading, so a locked target is edited as a private
        // copy; an unlocked target is edited directly (no needless copy of a large PDF).
        string? sourceCopyPath = null;
        string editSourcePath = pdfPath;
        string tempPath = pdfPath + ".edit.tmp";
        if (locked)
        {
            sourceCopyPath = pdfPath + ".edit.src.tmp";
            try
            {
                CopyShared(pdfPath, sourceCopyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"PDF 正在被其他程序占用（{LockHolders()}），无法读取。请关闭后重试，原文件未改动。", ex);
            }

            editSourcePath = sourceCopyPath;
        }

        try
        {
            PdfDocument? doc = null;
            try
            {
                try
                {
                    doc = PdfReader.Open(editSourcePath, PdfDocumentOpenMode.Modify);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"PDF 正在被其他程序占用（{LockHolders()}），无法打开编辑。请关闭后重试，原文件未改动。", ex);
                }

                edit(doc);
                doc.Save(tempPath);
            }
            finally
            {
                (doc as IDisposable)?.Dispose();
            }
        }
        finally
        {
            if (sourceCopyPath is not null) TryDelete(sourceCopyPath);
        }

        try
        {
            // Swap only now that the side file is complete and the original is released.
            File.Move(tempPath, pdfPath, overwrite: true);
            return pdfPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!keepCopyOnLock)
            {
                throw new IOException(
                    $"PDF 正在被其他程序占用（{LockHolders()}），无法保存修改。请关闭后重试，原文件未改动。", ex);
            }

            string fallbackPath = BuildFallbackPath(pdfPath, fallbackSuffix);
            File.Move(tempPath, fallbackPath);
            return fallbackPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>True when another handle prevents an in-place write (viewer open / read-only).</summary>
    private static bool IsLockedForWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Copies with full sharing so a viewer's read lock cannot block the edit.</summary>
    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>Unique sibling path such as "扫描件 (已插入).pdf".</summary>
    private static string BuildFallbackPath(string pdfPath, string suffix)
    {
        string directory = Path.GetDirectoryName(pdfPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        string extension = Path.GetExtension(pdfPath);
        for (var i = 1; ; i++)
        {
            string candidate = i == 1
                ? Path.Combine(directory, $"{name} ({suffix}){extension}")
                : Path.Combine(directory, $"{name} ({suffix} {i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover side file is harmless; the PDF itself is already intact.
        }
    }
}
