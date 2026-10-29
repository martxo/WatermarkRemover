using System.IO;
using System.Security.Cryptography;

namespace WatermarkRemover.Pdf.Preview;

/// <summary>
/// A PDF file that moves while it is open: remembers a rename-proof stamp (size + creation time +
/// first-KiB hash) so the document can be found again after the user renames it, and reports the
/// new path through <see cref="PathChanged"/> instead of the UI knowing about file IO.
///
/// Edits call <see cref="EnsureReadable"/> first: it resolves a rename and fails with a message
/// the user can act on, rather than a raw path error.
/// </summary>
public sealed class PdfDocumentFile
{
    private readonly Action<string>? _pathChanged;
    private readonly Action<string>? _debug;
    private readonly Action<string>? _info;

    private string _path;

    /// <summary>Size as last seen — renaming changes neither size nor creation time.</summary>
    private long _knownLength = -1;
    private DateTime _knownCreationUtc;

    /// <summary>SHA-256 of the file's first KiB. Size and creation time can collide (a temp
    /// directory full of same-batch scans); header bytes do not: a rename keeps them, a different
    /// file almost surely has different ones.</summary>
    private byte[]? _fingerprint;

    public PdfDocumentFile(
        string path,
        Action<string>? pathChanged = null,
        Action<string>? debug = null,
        Action<string>? info = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("PDF path must not be empty.", nameof(path));

        _path = path;
        _pathChanged = pathChanged;
        _debug = debug;
        _info = info;
    }

    /// <summary>Current path; changes when the file is renamed or relocated to a locked copy.</summary>
    public string Path => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Raised with the new path after a rename was tracked down or an edit fell back
    /// to a copy beside a locked original.</summary>
    public event EventHandler<string>? PathChanged;

    /// <summary>Resolves a renamed file, then fails with a readable message when it is gone.</summary>
    public void EnsureReadable()
    {
        if (!TryRelocate())
            throw new FileNotFoundException("文件已被移动或重命名，且无法自动找回。", _path);
        RememberStamp();
    }

    /// <summary>Points the session at the fallback copy written when the original was locked.</summary>
    public void RelocateTo(string newPath)
    {
        if (string.Equals(newPath, _path, StringComparison.OrdinalIgnoreCase)) return;

        _path = newPath;
        _knownLength = -1;
        _knownCreationUtc = default;
        _fingerprint = null;
        NotifyPathChanged();
    }

    /// <summary>
    /// Finds the PDF again after the user renamed or moved it while the strip was open. A rename
    /// changes neither size nor creation time, so the remembered stamp identifies the file in its
    /// (unchanged) directory — but only when the match is unique: guessing between several
    /// same-shaped files would risk editing the wrong document.
    /// </summary>
    public bool TryRelocate()
    {
        if (File.Exists(_path)) return true;

        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (directory is null || !Directory.Exists(directory) || _knownLength < 0 || _fingerprint is null)
        {
            _debug?.Invoke($"预览无法跟踪改名文件：{System.IO.Path.GetFileName(_path)} 目录不可用或缺少文件指纹");
            return false;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "*.pdf")
                .Where(p => !string.Equals(p, _path, StringComparison.OrdinalIgnoreCase))
                .Where(p =>
                {
                    try
                    {
                        var info = new FileInfo(p);
                        return info.Length == _knownLength && info.CreationTimeUtc == _knownCreationUtc;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                })
                .Where(HeadBytesMatch) // the stamp narrows the field; content picks the file
                .ToArray();
        }
        catch (IOException ex)
        {
            _debug?.Invoke($"预览无法跟踪改名文件：{System.IO.Path.GetFileName(_path)} 枚举失败（{ex.Message}）");
            return false;
        }

        if (candidates.Length != 1)
        {
            _debug?.Invoke($"预览无法跟踪改名文件：{System.IO.Path.GetFileName(_path)} 候选 {candidates.Length} 个，无法唯一确定");
            return false;
        }

        string oldName = System.IO.Path.GetFileName(_path);
        _path = candidates[0];
        _info?.Invoke($"预览已跟踪改名文件：{oldName} → {System.IO.Path.GetFileName(_path)}，候选 {candidates.Length} 个");
        NotifyPathChanged();
        return true;
    }

    /// <summary>Records the stamp a future rename is matched against; called after every
    /// successful read or write, since edits change the size (pass force to re-hash then).</summary>
    public void RememberStamp(bool force = false)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var info = new FileInfo(_path);
            if (!force && info.Length == _knownLength && _fingerprint is not null) return;
            _knownLength = info.Length;
            _knownCreationUtc = info.CreationTimeUtc;
            using var stream = File.OpenRead(_path);
            Span<byte> head = stackalloc byte[1024];
            int read = stream.Read(head);
            _fingerprint = SHA256.HashData(head[..read]);
        }
        catch (IOException)
        {
            // A stamp we could not refresh only makes rename tracking weaker, never the document.
        }
    }

    private bool HeadBytesMatch(string path)
    {
        if (_fingerprint is null) return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[1024];
            int read = stream.Read(head);
            return SHA256.HashData(head[..read]).AsSpan().SequenceEqual(_fingerprint);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void NotifyPathChanged()
    {
        PathChanged?.Invoke(this, _path);
        _pathChanged?.Invoke(_path);
    }
}
