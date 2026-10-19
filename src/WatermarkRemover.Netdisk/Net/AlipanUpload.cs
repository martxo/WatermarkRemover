namespace WatermarkRemover.Netdisk.Net;

/// <summary>One chunk of a 分片上传 transfer.</summary>
internal readonly record struct AlipanPart(int PartNumber, long Offset, long Length);

/// <summary>
/// Part layout for 分片上传. Pure arithmetic so the chunk math (and the progress numbers derived
/// from it) can be unit-tested without touching the network.
/// </summary>
internal static class AlipanUpload
{
    /// <summary>4 MB keeps part counts modest for typical scan PDFs while staying well under limits.</summary>
    internal const long DefaultPartSize = 4 * 1024 * 1024;

    /// <summary>Splits <paramref name="totalSize"/> into numbered parts; a zero-byte file still gets one empty part.</summary>
    internal static IReadOnlyList<AlipanPart> PlanParts(long totalSize, long partSize)
    {
        if (partSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partSize), "分片大小必须为正数。");
        }

        var parts = new List<AlipanPart>();
        if (totalSize <= 0)
        {
            parts.Add(new AlipanPart(1, 0, 0));
            return parts;
        }

        long offset = 0;
        var number = 1;
        while (offset < totalSize)
        {
            var length = Math.Min(partSize, totalSize - offset);
            parts.Add(new AlipanPart(number++, offset, length));
            offset += length;
        }

        return parts;
    }
}
