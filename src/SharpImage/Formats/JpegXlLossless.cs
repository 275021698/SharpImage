// Lossless JPEG <-> JXL transcoding (JPEG recompression).
//
// Encode writes a standard JPEG XL file: libjxl's lossless JPEG recompression (the JPEG's DCT coefficients as a
// VarDCT frame plus a jbrd box), byte-identical to cjxl 0.12 at its default effort, which djxl or any JPEG XL
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

/// <summary>Speed vs compression presets for JPEG recompression. Every level is byte-exact; higher levels
/// search more coding candidates for a smaller container at the cost of more CPU. <see cref="Balanced"/> is
/// the recommended default; <see cref="Turbo"/> is a single pass at roughly libjxl's speed for a little size.</summary>
public enum JpegRecompressionEffort
{
    /// <summary>Single pass — roughly libjxl's speed, a little larger than <see cref="Balanced"/>.</summary>
    Turbo = Jxl.JxlEncoder.JpegEffortTurbo,

    /// <summary>A quick best-of; between <see cref="Turbo"/> and <see cref="Balanced"/>.</summary>
    Fast = Jxl.JxlEncoder.JpegEffortFast,

    /// <summary>The recommended default: full best-of over predictors, scan orders and LZ77.</summary>
    Balanced = Jxl.JxlEncoder.JpegEffortDefault,

    /// <summary>Exhaustive: also tries the weighted-DC predictor, longer LZ77 and prefix codes.</summary>
    Maximum = Jxl.JxlEncoder.JpegEffortMax,
}

public static class JpegXlLossless
{
    private const uint Magic = 0x534A584C; // "SJXL"

    /// <summary>Detects SharpImage's earlier private lossless-JPEG container ("SJXL").</summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    /// <summary>Effort presets (speed vs compression) as raw levels; prefer <see cref="JpegRecompressionEffort"/>.</summary>
    public const int EffortTurbo = Jxl.JxlEncoder.JpegEffortTurbo;
    public const int EffortFast = Jxl.JxlEncoder.JpegEffortFast;
    public const int EffortDefault = Jxl.JxlEncoder.JpegEffortDefault;
    public const int EffortMax = Jxl.JxlEncoder.JpegEffortMax;

    /// <summary>Losslessly recompresses a JPEG into a JPEG XL file (see <see cref="JxlCoder.EncodeFromJpeg"/>).</summary>
    public static byte[] Encode(byte[] jpeg) => Encode(jpeg, EffortDefault);

    /// <summary>Losslessly recompresses a JPEG into a JPEG XL file. Every effort currently produces cjxl's default
    /// (effort 7) output.</summary>
    public static byte[] Encode(byte[] jpeg, JpegRecompressionEffort effort) => Encode(jpeg, (int)effort);

    /// <summary>Losslessly recompresses a JPEG into a standard JPEG XL file that reconstructs it byte for byte.
    /// Throws <see cref="NotSupportedException"/> for a JPEG libjxl cannot recompress (arithmetic coding, CMYK,
    /// 12-bit, unusual sampling) or one whose reconstruction would not be exact.</summary>
    /// <param name="jpeg">The source JPEG bytes.</param>
    /// <param name="effort">Compression effort 1-9; every level currently produces cjxl's default output.</param>
    public static byte[] Encode(byte[] jpeg, int effort)
    {
        byte[] jxl = JxlCoder.EncodeFromJpeg(jpeg);

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
