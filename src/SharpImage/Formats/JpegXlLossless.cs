// Lossless JPEG <-> JXL transcoding (JPEG recompression).
//
// Encode writes a standard JPEG XL file: libjxl's lossless JPEG recompression (the JPEG's DCT coefficients as a
// VarDCT frame plus a jbrd box), byte-identical to cjxl 0.12 at the same effort, which djxl or any JPEG XL
// decoder with reconstruction support turns back into the original JPEG byte for byte.
//
// Decode rebuilds the original JPEG from such a file, and also from SharpImage's earlier private container
// (magic "SJXL"), which QuickFixMyPics 1.0.10-1.0.11 wrote with a .jxl extension; those files stay readable.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>Speed vs compression presets for JPEG recompression, as cjxl effort levels. Every level is lossless
/// (the JPEG is rebuilt byte for byte); higher levels search more for a smaller file. <see cref="Balanced"/> is
/// cjxl's default.</summary>
public enum JpegRecompressionEffort
{
    /// <summary>cjxl -e 1: fastest.</summary>
    Turbo = 1,

    /// <summary>cjxl -e 3.</summary>
    Fast = 3,

    /// <summary>cjxl -e 7, its default.</summary>
    Balanced = 7,

    /// <summary>cjxl -e 9: learned trees for DC and chroma-from-luma, optimal LZ77, best clustering.</summary>
    Maximum = 9,
}

public static class JpegXlLossless
{
    private const uint Magic = 0x534A584C; // "SJXL"

    /// <summary>Detects SharpImage's earlier private lossless-JPEG container ("SJXL").</summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    /// <summary>Effort presets as cjxl effort levels; any level 1-10 may be passed.</summary>
    public const int EffortTurbo = (int)JpegRecompressionEffort.Turbo;
    public const int EffortFast = (int)JpegRecompressionEffort.Fast;
    public const int EffortDefault = (int)JpegRecompressionEffort.Balanced;
    public const int EffortMax = (int)JpegRecompressionEffort.Maximum;

    /// <summary>Losslessly recompresses a JPEG into a JPEG XL file (see <see cref="JxlCoder.EncodeFromJpeg"/>).</summary>
    public static byte[] Encode(byte[] jpeg) => Encode(jpeg, EffortDefault);

    /// <summary>Losslessly recompresses a JPEG into a JPEG XL file at a preset effort.</summary>
    public static byte[] Encode(byte[] jpeg, JpegRecompressionEffort effort) => Encode(jpeg, (int)effort);

    /// <summary>Losslessly recompresses a JPEG into a standard JPEG XL file that reconstructs it byte for byte.
    /// Throws <see cref="NotSupportedException"/> for a JPEG libjxl cannot recompress (arithmetic coding, CMYK,
    /// 12-bit, unusual sampling) or one whose reconstruction would not be exact.</summary>
    /// <param name="jpeg">The source JPEG bytes.</param>
    /// <param name="effort">cjxl effort level 1-10 (7 is cjxl's default); the output is byte-identical to
    /// <c>cjxl -e effort</c>.</param>
    public static byte[] Encode(byte[] jpeg, int effort)
    {
        byte[] jxl = JxlCoder.EncodeFromJpeg(jpeg, effort);

        // Self-certify: only return a file that reconstructs the source JPEG byte-for-byte.
        byte[]? check = JxlCoder.ReconstructJpeg(jxl);
        if (check == null || !check.AsSpan().SequenceEqual(jpeg))
        {
            throw new NotSupportedException("JPEG could not be losslessly recompressed (byte-exact reconstruction failed).");
        }

        return jxl;
    }

    /// <summary>Reconstructs the exact original JPEG bytes from a recompressed JPEG XL file, or from SharpImage's
    /// earlier "SJXL" container.</summary>
    public static byte[] Decode(byte[] container)
    {
        if (!CanDecode(container))
        {
            return JxlCoder.ReconstructJpeg(container) ?? throw new InvalidDataException("The JPEG XL file has no JPEG reconstruction data.");
        }

        var ms = new MemoryStream(container);
        var r = new BinaryReader(ms);
        if (BinaryPrimitives.ReverseEndianness(r.ReadUInt32()) != Magic)
        {
            throw new InvalidDataException("Not a SharpImage lossless-JPEG container.");
        }

        var d = new JpegDctData
        {
            HeaderBytes = ReadCompressedBlob(r),
        };
        d.TrailingBytes = r.ReadBoolean() ? new byte[] { 0xFF, 0xD9 } : ReadCompressedBlob(r);

        // Re-derive geometry / sampling / per-component sampling+table indices / block grid from the header.
        JpegCoder.PopulateDctMetadata(d);

        d.Progressive = r.ReadBoolean();
        if (d.Progressive)
        {
            int scanCount = r.ReadInt32();
            d.Scans = new System.Collections.Generic.List<JpegScan>(scanCount);
            for (int si = 0; si < scanCount; si++)
            {
                var s = new JpegScan
                {
                    Ss = r.ReadInt32(),
                    Se = r.ReadInt32(),
                    Ah = r.ReadInt32(),
                    Al = r.ReadInt32(),
                    RestartInterval = r.ReadInt32(),
                };
                int compCount = r.ReadInt32();
                s.ComponentIndices = new int[compCount];
                s.CompDcTable = new int[compCount];
                s.CompAcTable = new int[compCount];
                for (int i = 0; i < compCount; i++)
                {
                    s.ComponentIndices[i] = r.ReadInt32();
                    s.CompDcTable[i] = r.ReadInt32();
                    s.CompAcTable[i] = r.ReadInt32();
                }

                s.Prefix = ReadCompressedBlob(r);
                d.Scans.Add(s);
            }
        }

        byte[] coeff = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position)); // rest of the stream
        Jxl.JxlEncoder.DecodeJpegCoefficients(coeff, d);
        return JpegCoder.RebuildJpeg(d);
    }

    private static int ReadVarint(BinaryReader r)
    {
        int v = 0, shift = 0;
        while (true)
        {
            byte b = r.ReadByte();
            v |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }

            shift += 7;
        }
    }

    private static byte[] ReadBlob(BinaryReader r) => r.ReadBytes(ReadVarint(r));

    private static byte[] ReadCompressedBlob(BinaryReader r)
    {
        byte flag = r.ReadByte();
        if (flag == 0)
        {
            return r.ReadBytes(ReadVarint(r)); // stored raw
        }

        int origLen = ReadVarint(r);
        int payloadLen = ReadVarint(r);
        byte[] payload = r.ReadBytes(payloadLen);
        var outBuf = new byte[origLen];
        System.IO.Compression.BrotliDecoder.TryDecompress(payload, outBuf, out int decoded);
        if (decoded != origLen)
        {
            throw new InvalidDataException("Corrupt compressed blob in SharpImage lossless-JPEG container.");
        }

        return outBuf;
    }
}
