// JPEG XL (JXL) format coder.
//
// DECODE: Formats.Jxl.Lj.LjDecoder, a line-by-line port of libjxl 0.12's decoder (Modular and VarDCT, every
// AC strategy, patches, splines, noise, EPF/Gaborish, upsampling, blending/animation, reference frames). Its
// float output is bit-identical to djxl on 39 of the 40 libjxl conformance files (the 40th is CMYK, which djxl
// renders through a CMS). JPEG reconstruction (ReconstructJpeg) rebuilds the original JPEG bytes from a file
// with a jbrd box. DecodePreview still uses the older Modular-only decoder for truncated input.
//
// ENCODE: a real, from-scratch pure-C# lossless encoder (see Formats.Jxl.JxlEncoder). It emits a
// standard bare codestream - 8-bit sRGB, a Modular frame with the YCoCg RCT, the self-correcting
// weighted predictor and prefix (Huffman) entropy coding, tiled into groups for large images. Output
// is verified pixel-exact against the reference decoders (libjxl and jxl-oxide), not just round-tripped.
// EncodeFromJpeg is libjxl's lossless JPEG recompression, byte-identical to cjxl.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;

namespace SharpImage.Formats;

/// <summary>Speed vs size presets for lossless JPEG XL encoding. Every level is lossless; higher levels search more
/// coding choices for a smaller file. <see cref="Balanced"/> (the default) matches libjxl's default effort (7) for size
/// and speed; <see cref="Maximum"/> searches exhaustively.</summary>
public enum JxlLosslessEffort
{
    /// <summary>A fixed context tree, no search: roughly libjxl effort 3.</summary>
    Turbo,

    /// <summary>A small learned tree: roughly libjxl effort 5.</summary>
    Fast,

    /// <summary>The recommended default: a learned tree over libjxl effort 7's choices.</summary>
    Balanced,

    /// <summary>Exhaustive: several trees, weighted-predictor modes, predictors and both entropy coders.</summary>
    Maximum,
}

public static class JxlCoder
{
    // Container signature (ISOBMFF-style).
    private static ReadOnlySpan<byte> ContainerSignature => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        if (data[0] == 0xFF && data[1] == 0x0A)
        {
            return true; // bare codestream
        }

        return data.Length >= 12 && data[..12].SequenceEqual(ContainerSignature);
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid JPEG XL file");
        }

        Jxl.Lj.LjDecodedImage img = DecodeLj(data, 1);
        ImageFrame frame = BuildFrame(img, img.Frames[0]);
        AttachBoxMetadata(data, frame);
        return frame;
    }

    /// <summary>
    /// Rebuilds the original JPEG file from a JPEG XL file made by lossless JPEG recompression (cjxl's default for
    /// JPEG input, or <see cref="EncodeFromJpeg"/>). Returns null when the file carries no JPEG reconstruction data.
    /// </summary>
    public static byte[]? ReconstructJpeg(byte[] data) => Jxl.Lj.LjJpegRecon.Reconstruct(data);

    // Pixel output: XYB images are rendered to their original colour space when SharpImage has one for it (sRGB,
    // Display P3, Adobe RGB, linear, gray) and to sRGB otherwise - an XYB image with an ICC profile included, since
    // without a CMS libjxl can only render enumerated spaces. Non-XYB images come out as stored, ICC attached.
    private static Jxl.Lj.LjDecodedImage DecodeLj(byte[] data, int maxFrames)
    {
        byte[] cs = FindCodestream(data);
        try
        {
            return Jxl.Lj.LjDecoder.Decode(cs, maxFrames, preferSrgb: true);
        }
        catch (InvalidDataException)
        {
            return Jxl.Lj.LjDecoder.Decode(cs, maxFrames, preferSrgb: false);
        }
    }

    /// <summary>
    /// Encodes an image as a lossless JPEG XL codestream (Modular mode: clamped-gradient prediction +
    /// prefix entropy coding). Produces a standard bare codestream that libjxl / jxl-oxide decode.
    /// </summary>
    public static byte[] Encode(ImageFrame image) => Jxl.JxlEncoder.EncodeLossless(image, JxlLosslessEffort.Balanced);

    /// <summary>
    /// Encodes an image as a lossless JPEG XL codestream at the given effort (every level is lossless; higher levels
    /// search more for a smaller file).
    /// </summary>
    public static byte[] Encode(ImageFrame image, JxlLosslessEffort effort) => Jxl.JxlEncoder.EncodeLossless(image, effort);

    /// <summary>
    /// Losslessly recompresses a JPEG into a JPEG XL file, exactly as libjxl's <c>cjxl</c> does by default for JPEG
    /// input: the JPEG's own DCT coefficients become the JXL image and a jbrd box keeps everything needed to
    /// rebuild the original file byte for byte (<c>djxl image.jxl image.jpg</c>). The output is byte-identical to
    /// cjxl 0.12 at its default settings. Throws <see cref="NotSupportedException"/> for a JPEG that cannot be
    /// recompressed (arithmetic coding, CMYK, 12-bit, unusual sampling, corrupt data); encode its decoded pixels
    /// instead, as cjxl does.
    /// </summary>
    public static byte[] EncodeFromJpeg(byte[] jpeg) => Jxl.Lj.LjJpegTranscoder.Transcode(jpeg);

    /// <summary>
    /// Encodes an animated image sequence as a single multi-frame lossless JPEG XL codestream. Each frame
    /// carries its own duration (from <see cref="ImageFrame.Delay"/>, centiseconds); the metadata holds the
    /// loop count. A one-frame sequence produces an ordinary still image.
    /// </summary>
    public static byte[] EncodeAnimation(ImageSequence sequence) => Jxl.JxlEncoder.EncodeSequence(sequence);

    /// <summary>Decodes a (possibly animated) JPEG XL codestream into an image sequence, one frame per JXL
    /// frame with its per-frame <see cref="ImageFrame.Delay"/> (centiseconds) and the loop count.</summary>
    public static ImageSequence DecodeAnimation(byte[] data)
    {
        Jxl.Lj.LjDecodedImage img = DecodeLj(data, int.MaxValue);
        Jxl.Lj.LjAnimationHeader? anim = img.State.Meta.HaveAnimation ? img.State.Meta.Animation : null;
        var seq = new ImageSequence { LoopCount = anim != null ? (int)anim.NumLoops : 0 };
        foreach (Jxl.Lj.LjDecodedFrame fr in img.Frames)
        {
            ImageFrame frame = BuildFrame(img, fr);
            if (anim != null && anim.TpsNumerator > 0)
            {
                frame.Delay = (int)((long)fr.Duration * 100 * anim.TpsDenominator / anim.TpsNumerator);
            }

            seq.AddFrame(frame);
        }

        return seq;
    }

    /// <summary>
    /// Encodes an image as a lossy JPEG XL codestream (XYB VarDCT). <paramref name="quality"/> is a
    /// JPEG-style value in [1,100] (higher = better). <paramref name="effort"/> in [1,9] trades speed for
    /// quality/size (1-3 = fast single-pass at any size; 7 = default; higher runs more SSIMULACRA2-guided
    /// block-refinement roundtrips over a larger pixel budget). <paramref name="bits"/> is the output bit
    /// depth (8 default; 16 keeps full precision for a 16-bit source, avoiding 8-bit banding). Any positive
    /// size is supported: the frame is tiled into 2048x2048 LF groups and 256px coding groups.
    /// </summary>
    public static byte[] EncodeLossy(ImageFrame image, int quality = 75, int effort = 7, int bits = 8)
    {
        if (!Jxl.JxlEncoder.CanEncodeVarDct(image))
        {
            return Jxl.JxlEncoder.EncodeLossless(image);
        }

        float distance = Jxl.JxlEncoder.DistanceFromQuality(quality);
        effort = Math.Clamp(effort, 1, 9);

        // 16-bit output (opt-in): the XYB pipeline is float, so this just keeps full input precision + declares
        // 16-bit. Uses the fast single-pass path (the block-refined roundtrip's metric assumes 8-bit).
        if (bits >= 16)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance, null, 16);
        }

        // Wide-gamut (non-sRGB primaries): the XYB transform converts to/from sRGB primaries, but the
        // block-refined roundtrip's SSIMULACRA2 metric assumes an sRGB pixel space, so use single-pass.
        if (image.Colorspace != ColorspaceType.SRGB)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance);
        }

        // Effort/speed dial. The block-refined path runs several encode->decode->SSIMULACRA2 roundtrips to
        // close the coarse-quant "cliff"; its cost scales with pixels x iterations, so higher effort buys
        // more refinement iterations and a larger pixel budget below which it's used (single-pass above it,
        // which is fast at any size). effort<=3 is always the fast single-pass path.
        long pixels = (long)image.Columns * image.Rows;
        long budget = effort switch
        {
            <= 3 => 0,
            4 => 500_000,
            5 => 1_000_000,
            6 => 1_500_000,
            7 => 2_000_000,   // default: block-refined up to ~2 MP (matches the prior default)
            8 => 4_000_000,
            _ => long.MaxValue,
        };

        if (pixels > budget)
        {
            return Jxl.JxlEncoder.EncodeVarDct(image, distance);
        }

        int iters = Math.Clamp(effort - 2, 2, 7); // effort 4 -> 2 refinement iterations, effort 9 -> 7
        return Jxl.JxlEncoder.EncodeVarDctBlockRefined(image, distance, iters);
    }

    /// <summary>
    /// Decodes a possibly-truncated JPEG XL codestream into a best-effort preview. For a progressive /
    /// multi-section VarDCT frame, any section not yet fully arrived is treated as zero (the guarantee is
    /// section-granular — an ANS stream can't be safely decoded from a partial tail), so a prefix renders a
    /// DC-only, then partial-pass, image that sharpens as whole sections arrive, and the complete buffer
    /// reconstructs exactly. The signature, header and TOC must be present. A single-section (non-progressive)
    /// frame can only be previewed once fully received.
    /// </summary>
    public static ImageFrame DecodePreview(byte[] partialData)
    {
        if (partialData.Length < 2 || partialData[0] != 0xFF || partialData[1] != 0x0A)
        {
            // Only bare codestreams (0xFF 0x0A) are supported for preview; a truncated container box header
            // may not even locate the codestream.
            throw new InvalidDataException("Preview decode requires a bare JPEG XL codestream (0xFF 0x0A).");
        }

        Jxl.JxlModularResult result = Jxl.JxlFrame.DecodeModularCodestream(partialData, allowTruncated: true);
        return BuildFrame(result);
    }

    private static ImageFrame BuildFrame(Jxl.Lj.LjDecodedImage img, Jxl.Lj.LjDecodedFrame fr)
    {
        bool gray = img.IsGray;
        int alphaEc = img.AlphaChannel;
        bool hasAlpha = alphaEc >= 0;
        var frame = new ImageFrame();
        Jxl.Lj.LjColorEncoding enc = img.DataEncoding;
        ColorspaceType space = Jxl.Lj.LjDecoder.MapColorspace(enc) ?? (gray ? ColorspaceType.Gray : ColorspaceType.SRGB);
        frame.Initialize(fr.Width, fr.Height, space, hasAlpha);
        if (enc.WantIcc && enc.Icc != null)
        {
            frame.IccProfile = enc.Icc;
        }

        frame.Orientation = OrientationType.TopLeft;
        int nc = frame.NumberOfChannels;
        int colorOut = gray ? 1 : 3;
        Jxl.Lj.LjImageF? alpha = hasAlpha ? fr.Planes[3 + alphaEc] : null;
        bool premultiplied = hasAlpha && img.State.Meta.ExtraChannelInfo[alphaEc].AlphaAssociated;
        for (int y = 0; y < fr.Height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < fr.Width; x++)
            {
                int off = x * nc;
                float a = alpha != null ? alpha.Data[alpha.Row(y) + x] : 1f;
                float inv = premultiplied ? 1.0f / MathF.Max(1.0f / (1 << 24), a) : 1f;
                for (int c = 0; c < colorOut; c++)
                {
                    Jxl.Lj.LjImageF p = fr.Planes[c];
                    row[off + c] = ToQuantum(p.Data[p.Row(y) + x] * inv);
                }

                if (hasAlpha)
                {
                    row[off + nc - 1] = ToQuantum(a);
                }
            }
        }

        return frame;
    }

    private static ushort ToQuantum(float v)
    {
        float c = v > 0 ? (v < 1 ? v : 1) : 0;
        return (ushort)MathF.Round(c * 65535f, MidpointRounding.ToEven);
    }

    private static void AttachBoxMetadata(byte[] data, ImageFrame frame)
    {
        if (data.Length < 2 || (data[0] == 0xFF && data[1] == 0x0A))
        {
            return;
        }

        try
        {
            Dictionary<string, List<byte[]>> boxes = Jxl.Lj.LjJpegRecon.ParseBoxes(data);
            if (boxes.TryGetValue("Exif", out List<byte[]>? exif) && exif[0].Length > 4)
            {
                uint tiffOff = BinaryPrimitives.ReadUInt32BigEndian(exif[0]);
                if (4 + tiffOff < exif[0].Length)
                {
                    frame.Metadata.ExifProfile = SharpImage.Metadata.ExifParser.ParseFromTiff(exif[0].AsSpan(4 + (int)tiffOff));
                }
            }

            if (boxes.TryGetValue("xml ", out List<byte[]>? xmp))
            {
                frame.Metadata.Xmp = System.Text.Encoding.UTF8.GetString(xmp[0]);
            }
        }
        catch (InvalidDataException)
        {
            // Metadata is optional; the pixels are already decoded.
        }
    }

    private static ImageFrame BuildFrame(Jxl.JxlModularResult r)
    {
        int w = r.Width;
        int h = r.Height;
        var frame = new ImageFrame();
        frame.Initialize(w, h, r.Colorspace, r.HasAlpha);
        frame.IccProfile = r.IccProfile;
        int frameChannels = frame.NumberOfChannels;
        int nb = r.NumChannels;

        for (int y = 0; y < h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int pix = (y * w) + x;
                int off = x * frameChannels;
                int maxV = (1 << r.Bps) - 1;
                if (nb == 1)
                {
                    ushort g = Quantum.ScaleFromDepth((uint)Math.Clamp(r.Channels[0].Px[pix], 0, maxV), r.Bps);
                    row[off] = g;
                    if (frameChannels >= 3)
                    {
                        row[off + 1] = g;
                        row[off + 2] = g;
                    }
                }
                else
                {
                    int m = Math.Min(nb, frameChannels);
                    for (int c = 0; c < m; c++)
                    {
                        row[off + c] = Quantum.ScaleFromDepth((uint)Math.Clamp(r.Channels[c].Px[pix], 0, maxV), r.Bps);
                    }
                }
            }
        }

        return frame;
    }

    internal static byte[] FindCodestream(byte[] data)
    {
        if (data[0] == 0xFF && data[1] == 0x0A)
        {
            return data;
        }

        // Parse container boxes: the codestream is one jxlc box, or split over jxlp boxes (each starting with a
        // 4-byte part index) that are concatenated in file order — cjxl splits it whenever it writes jbrd / Exif.
        int pos = 0;
        List<byte[]>? parts = null;
        while (pos + 8 <= data.Length)
        {
            long boxLen = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string boxType = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int headerSize = 8;

            if (boxLen == 1 && pos + 16 <= data.Length)
            {
                headerSize = 16;
                boxLen = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
            }
            else if (boxLen == 0)
            {
                boxLen = data.Length - pos;
            }

            if (boxLen < headerSize || pos + boxLen > data.Length)
            {
                throw new InvalidDataException("Truncated JXL container box");
            }

            if (boxType == "jxlc")
            {
                return data.AsSpan(pos + headerSize, (int)boxLen - headerSize).ToArray();
            }

            if (boxType == "jxlp" && boxLen >= headerSize + 4)
            {
                (parts ??= []).Add(data.AsSpan(pos + headerSize + 4, (int)boxLen - headerSize - 4).ToArray());
                if ((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + headerSize)) & 0x80000000u) != 0)
                {
                    break;   // the last part
                }
            }

            pos += (int)boxLen;
        }

        if (parts != null)
        {
            return parts.Count == 1 ? parts[0] : parts.SelectMany(p => p).ToArray();
        }

        throw new InvalidDataException("No codestream found in JXL container");
    }
}
