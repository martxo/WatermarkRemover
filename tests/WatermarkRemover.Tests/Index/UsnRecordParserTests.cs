using System.Buffers.Binary;
using WatermarkRemover.Index.Native;

namespace WatermarkRemover.Tests.Index;

/// <summary>
/// USN parsing is the only place where raw MFT bytes enter the index, so the header offsets are
/// pinned against synthetic V2 records (a live volume needs an elevated token).
/// </summary>
public class UsnRecordParserTests
{
    [Fact]
    public void Parses_a_single_v2_record()
    {
        byte[] buffer = UsnRecordParser.BuildForTest(
            0x1_0000_0005UL, 5, "报告.docx", 0x20, UsnRecordParser.ReasonFileCreate, usn: 1234);

        Assert.NotEqual(0, UsnRecordParser.TryParse(buffer, out UsnRecord record));
        Assert.Equal(0x1_0000_0005UL, record.FileId);
        Assert.Equal(5UL, record.ParentFileId);
        Assert.Equal("报告.docx", record.Name);
        Assert.Equal(1234L, record.Usn);
        Assert.Equal(0x20U, record.Attributes);
        Assert.False(record.IsFolder);
        Assert.True((record.Reason & UsnRecordParser.ReasonFileCreate) != 0);
    }

    [Fact]
    public void Parses_a_concatenated_batch_and_skips_padding()
    {
        byte[] first = UsnRecordParser.BuildForTest(100, 5, "a.txt", 0x20);
        byte[] second = UsnRecordParser.BuildForTest(101, 5, "folder", 0x10);
        byte[] buffer = first.Concat(second).ToArray();

        var records = new List<UsnRecord>();
        UsnRecordParser.ParseAll(buffer, records);

        Assert.Equal(2, records.Count);
        Assert.Equal("a.txt", records[0].Name);
        Assert.True(records[1].IsFolder);
    }

    [Fact]
    public void A_short_buffer_is_not_a_record()
    {
        Assert.Equal(0, UsnRecordParser.TryParse(new byte[UsnRecordParser.HeaderSize - 1], out _));
        Assert.Equal(0, UsnRecordParser.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    [Fact]
    public void A_record_claiming_more_bytes_than_available_is_rejected()
    {
        byte[] buffer = UsnRecordParser.BuildForTest(100, 5, "a.txt", 0x20);

        Assert.Equal(0, UsnRecordParser.TryParse(buffer.AsSpan(0, buffer.Length - 8), out _));
    }

    [Fact]
    public void A_v3_record_is_skipped_but_still_advances_by_its_length()
    {
        byte[] v2 = UsnRecordParser.BuildForTest(100, 5, "a.txt", 0x20);
        byte[] v3 = (byte[])v2.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(v3, v2.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(v3.AsSpan(4), 3);
        byte[] buffer = v3.Concat(v2).ToArray();

        var records = new List<UsnRecord>();
        UsnRecordParser.ParseAll(buffer, records);

        Assert.Single(records);
        Assert.Equal("a.txt", records[0].Name);
    }
}
