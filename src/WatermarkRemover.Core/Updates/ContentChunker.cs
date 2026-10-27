using System.Security.Cryptography;

namespace WatermarkRemover.Core.Updates;

/// <summary>One content-defined chunk: where it sits in the file and its SHA256.</summary>
public sealed record ContentChunk(int Offset, int Length, string Sha256);

/// <summary>
/// Content-defined chunking for the cloud update repository. Boundaries come from a gear rolling
/// hash, so inserting or changing bytes only shifts the chunks around the edit — everything after
/// the change re-aligns at the next boundary. A release therefore uploads (and clients download)
/// only the chunks that actually changed, even though the payload is one large single-file exe.
/// </summary>
public static class ContentChunker
{
    /// <summary>Lower bound of one chunk; avoids a blast of tiny chunks on degenerate input.</summary>
    public const int MinChunkSize = 256 * 1024;

    /// <summary>Average target: a boundary occurs with probability 1/2^20 per byte.</summary>
    public const int TargetChunkSize = 1024 * 1024;

    /// <summary>Upper bound; guarantees progress even when no hash boundary appears.</summary>
    public const int MaxChunkSize = 4 * 1024 * 1024;

    private const ulong BoundaryMask = (1UL << 20) - 1;

    /// <summary>Deterministic gear table (SplitMix64, fixed seed) so every client chunks identically.</summary>
    private static readonly ulong[] Gear = BuildGearTable();

    /// <summary>Splits a file into chunks; the concatenation of all chunks is the original bytes.</summary>
    public static List<ContentChunk> Split(ReadOnlySpan<byte> data)
    {
        var chunks = new List<ContentChunk>();
        int offset = 0;
        while (offset < data.Length)
        {
            int length = NextBoundary(data[offset..]);
            chunks.Add(new ContentChunk(
                offset,
                length,
                Convert.ToHexString(SHA256.HashData(data.Slice(offset, length))).ToLowerInvariant()));
            offset += length;
        }

        return chunks;
    }

    /// <summary>
    /// Chooses the length of the next chunk, starting at <paramref name="data"/>[0]. Returns the
    /// remaining length when the tail is shorter than the minimum.
    /// </summary>
    internal static int NextBoundary(ReadOnlySpan<byte> data)
    {
        if (data.Length <= MinChunkSize) return data.Length;

        ulong hash = 0;
        int limit = Math.Min(data.Length, MaxChunkSize);
        for (int i = 0; i < limit; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if (i + 1 >= MinChunkSize && (hash & BoundaryMask) == 0)
            {
                return i + 1;
            }
        }

        return limit;
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }

        return table;
    }
}
