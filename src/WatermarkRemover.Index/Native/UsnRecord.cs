using System.Buffers.Binary;
using System.Text;

namespace WatermarkRemover.Index.Native;

/// <summary>
/// One USN_RECORD_V2 entry — the record shape both FSCTL_ENUM_USN_DATA (full MFT walk) and
/// FSCTL_READ_USN_JOURNAL (live changes) hand back.
///
/// Header layout (60 bytes, name offset/length are byte offsets from the record start):
///   0 RecordLength u32 | 4 Major u16 | 6 Minor u16 | 8 FileId u64 | 16 ParentId u64
///   24 Usn i64 | 32 TimeStamp i64 | 40 Reason u32 | 44 SourceInfo u32 | 48 SecurityId u32
///   52 Attributes u32 | 56 NameLength u16 (bytes) | 58 NameOffset u16 (bytes)
/// </summary>
public readonly record struct UsnRecord(
    ulong FileId,
    ulong ParentFileId,
    long Usn,
    long TimeStamp,
    uint Reason,
    uint Attributes,
    string Name)
{
    public bool IsFolder => (Attributes & UsnRecordParser.FileAttributeDirectory) != 0;
}

public static class UsnRecordParser
{
    public const int HeaderSize = 60;
    public const uint FileAttributeDirectory = 0x00000010;

    // USN_REASON_* bits we act on. Everything maps the same subset onto its index.
    public const uint ReasonFileCreate = 0x00000100;
    public const uint ReasonFileDelete = 0x00000200;
    public const uint ReasonRenameOldName = 0x00001000;
    public const uint ReasonRenameNewName = 0x00002000;

    /// <summary>
    /// Parses one record from <paramref name="buffer"/> and returns its aligned length, or 0 when
    /// the buffer is too short / malformed. Records using 128-bit file ids (V3+) are skipped:
    /// their length is still returned so the caller can advance cleanly.
    /// </summary>
    public static int TryParse(ReadOnlySpan<byte> buffer, out UsnRecord record)
    {
        record = default;
        if (buffer.Length < HeaderSize) return 0;

        int recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (recordLength < HeaderSize || recordLength > buffer.Length) return 0;

        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        if (major != 2) return recordLength;

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[56..]);
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[58..]);
        if (nameLength <= 0 || nameOffset + nameLength > recordLength) return recordLength;

        record = new UsnRecord(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[52..]),
            Encoding.Unicode.GetString(buffer[nameOffset..(nameOffset + nameLength)]));

        return recordLength;
    }

    /// <summary>Parses every record in a buffer, starting after an optional leading payload.</summary>
    public static void ParseAll(ReadOnlySpan<byte> buffer, List<UsnRecord> into)
    {
        int offset = 0;
        while (offset + HeaderSize <= buffer.Length)
        {
            int length = TryParse(buffer[offset..], out UsnRecord record);
            if (length <= 0) break;
            if (record.Name is not null) into.Add(record);
            offset += length;
        }
    }

    private static int Align8(int value) => (value + 7) & ~7;

    /// <summary>Builds a synthetic V2 record; used by tests instead of a live volume.</summary>
    internal static byte[] BuildForTest(
        ulong fileId, ulong parentId, string name, uint attributes,
        uint reason = ReasonFileCreate, long usn = 0)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int recordLength = Align8(HeaderSize + nameBytes.Length);
        var buffer = new byte[recordLength];

        BinaryPrimitives.WriteInt32LittleEndian(buffer, recordLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), parentId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(58), HeaderSize);
        nameBytes.CopyTo(buffer, HeaderSize);

        return buffer;
    }
}
