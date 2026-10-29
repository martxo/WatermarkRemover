using System.Buffers;
using System.IO;
using BitMiracle.LibTiff.Classic;
using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Embeds bilevel (engine 黑白) frames the way the target's official software does: a raw
/// CCITT Group 4 (T.6) image XObject. G4 is lossless and compresses text at ~10:1, so the
/// PDF stays crisp AND small — re-encoding bi-level pixels as JPEG (the previous path)
/// bloats them ~7x and smears glyph edges. The image is embedded directly as
/// /CCITTFaxDecode stream data, never decoded back to a bitmap in between.
/// </summary>
public static class CcittG4Image
{
    /// <summary>
    /// Detects a pure bilevel BGRA frame and G4-compresses it. Returns false for anything
    /// with gray/colour pixels — the caller then falls back to the regular image path.
    /// The detection is per-pixel (not settings-driven) so WIA-fallback frames that happen
    /// to be colour are never smuggled into a fax-encoded page.
    /// </summary>
    public static bool TryEncode(RasterImage image, out byte[] ccittStream)
    {
        ArgumentNullException.ThrowIfNull(image);

        var packed = TryPackBilevel(image);
        if (packed is null)
        {
            ccittStream = [];
            return false;
        }

        ccittStream = CompressG4(packed, image.Width, image.Height);
        return true;
    }

    /// <summary>
    /// Packs BGRA pixels to one-bit rows (MSB first, bit 1 = white) and verifies the frame is
    /// strictly bilevel. Returns null when any pixel violates B&amp;W (including alpha).
    /// </summary>
    internal static byte[]? TryPackBilevel(RasterImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var data = image.Bgra;
        var rowBytes = (width + 7) / 8;
        var packed = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            var srcRow = y * width * 4;
            var dstRow = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var o = srcRow + x * 4;
                var b = data[o];
                if (data[o + 3] != 255 || data[o + 1] != b || data[o + 2] != b)
                    return null;

                if (b == 255)
                {
                    packed[dstRow + x / 8] |= (byte)(0x80 >> (x & 7));
                }
                else if (b != 0)
                {
                    return null;
                }
            }
        }

        return packed;
    }

    /// <summary>
    /// CCITT G4 (T.6, K=-1) compression through LibTiff.NET. The strip is written into an
    /// in-memory TIFF and read back as raw fax data — the TIFF container itself is discarded.
    /// </summary>
    internal static byte[] CompressG4(byte[] packed, int width, int height)
    {
        var buffer = new MemoryStream();
        var writer = Tiff.ClientOpen("ccitt-g4", "w", buffer, new MemoryStreamTiffStream());
        if (writer is null)
            throw new InvalidOperationException("无法初始化 CCITT G4 编码器。");

        try
        {
            writer.SetField(TiffTag.IMAGEWIDTH, width);
            writer.SetField(TiffTag.IMAGELENGTH, height);
            writer.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
            writer.SetField(TiffTag.BITSPERSAMPLE, 1);
            writer.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            writer.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
            writer.SetField(TiffTag.ROWSPERSTRIP, height);
            writer.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);

            // The fax codec reads bit 0 as white regardless of photometric; the PDF flavour of
            // CCITTFaxDecode (BlackIs1=false) decodes bit 1 as white — feed the complement.
            var complement = ArrayPool<byte>.Shared.Rent(packed.Length);
            try
            {
                for (var i = 0; i < packed.Length; i++)
                    complement[i] = (byte)~packed[i];
                if (writer.WriteEncodedStrip(0, complement, packed.Length) == 0)
                    throw new InvalidOperationException("CCITT G4 编码失败。");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(complement);
            }
        }
        finally
        {
            writer.Dispose();
        }

        // Reopen the finished TIFF and pull the raw compressed strip out of the container.
        buffer.Position = 0;
        var reader = Tiff.ClientOpen("ccitt-g4", "r", buffer, new MemoryStreamTiffStream());
        if (reader is null)
            throw new InvalidOperationException("无法回读 CCITT G4 中间数据。");

        try
        {
            var counts = reader.GetField(TiffTag.STRIPBYTECOUNTS);
            var countField = counts is null ? null : counts[0].Value;
            var stripBytes = countField switch
            {
                Array a when a.Length > 0 => Convert.ToInt32(a.GetValue(0)),
                _ => 0,
            };
            if (stripBytes <= 0)
                throw new InvalidOperationException("CCITT G4 编码结果为空。");

            var strip = new byte[stripBytes];
            var read = reader.ReadRawStrip(0, strip, 0, stripBytes);
            if (read != stripBytes)
                throw new InvalidOperationException($"CCITT G4 条带读取不完整（{read}/{stripBytes}）。");
            return strip;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>Routes Tiff.ClientOpen I/O into a MemoryStream (no temp files).</summary>
    private sealed class MemoryStreamTiffStream : TiffStream
    {
        public override int Read(object client, byte[] buffer, int offset, int size) =>
            ((MemoryStream)client).Read(buffer, offset, size);

        public override void Write(object client, byte[] buffer, int offset, int size) =>
            ((MemoryStream)client).Write(buffer, offset, size);

        public override long Seek(object client, long offset, SeekOrigin origin) =>
            ((MemoryStream)client).Seek(offset, origin);

        public override void Close(object client)
        {
        }

        public override long Size(object client) => ((MemoryStream)client).Length;
    }
}
