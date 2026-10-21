// Assembles a complete HEIC exactly as libheif 1.23.6 + x265 (heif-enc) writes it: libheif's RGB -> YCbCr conversion
// (matrix 6, full range, 'average' 4:2:0 chroma), its x265 plugin's padding and CTU choice, the x265 port
// (X265Encoder), and libheif's HEIF container (brands, property order and deduplication, iloc/iinf/iref).
using System;
using System.Collections.Generic;
using System.Text;
using SharpImage.Formats.Hevc.X265;

namespace SharpImage.Formats.Hevc;

internal static class HeicEncoder
{
    /// <summary>
    /// Encodes 8-bit samples (row-major; channels 1 = gray, 2 = gray + alpha, 3 = RGB, 4 = RGBA) into a HEIC file.
    /// Gray is coded as a monochrome (4:0:0) item and RGB as YCbCr 4:2:0; alpha is a second monochrome item linked to
    /// the colour item as its alpha auxiliary image ('auxl' + auxC).
    /// </summary>
    /// <param name="quality">libheif quality 0..100 (heif-enc -q; x265 CRF = (100 - quality) / 2).</param>
    /// <param name="constantQp">x265 constant QP instead of CRF, or null.</param>
    /// <param name="colorPrimaries">CICP colour primaries of the samples (colr nclx and the HEVC VUI).</param>
    /// <param name="transferCharacteristics">CICP transfer characteristics of the samples.</param>
    /// <param name="icc">An ICC profile describing the samples (colr prof, in place of colr nclx), or null.</param>
    /// <param name="exif">EXIF (raw TIFF data, or anything that contains the TIFF header) as an 'Exif' item, or null.</param>
    /// <param name="xmp">An XMP packet (a 'mime' item, application/rdf+xml), or null.</param>
    /// <param name="orientation">EXIF orientation 1..8, stored as irot/imir.</param>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, int quality, int? constantQp,
        int colorPrimaries = 1, int transferCharacteristics = 13, byte[]? icc = null, byte[]? exif = null, byte[]? xmp = null,
        int orientation = 1)
    {
        bool gray = channels <= 2;
        bool hasAlpha = channels is 2 or 4;
        byte[] y;
        byte[]? cb = null, cr = null, alpha = null;
        if (gray)
        {
            y = new byte[width * height];
            for (int i = 0; i < y.Length; i++) y[i] = pixels[i * channels];
        }
        else
        {
            (y, cb, cr) = RgbToYCbCr420(pixels, width, height, channels);
        }

        if (hasAlpha)
        {
            alpha = new byte[width * height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = pixels[(i * channels) + channels - 1];
        }

        // libheif's RGB -> YCbCr conversion attaches the output nclx, which the x265 plugin writes into the VUI; a gray
        // image is passed through without one
        return EncodeYuv(y, cb, cr, gray ? 0 : 1, alpha, width, height, colorDescription: !gray, quality, constantQp,
            colorPrimaries, transferCharacteristics, icc, exif, xmp, orientation);
    }

    /// <summary>
    /// Encodes 8-bit planes (tightly packed: luma width x height, chroma at the chroma format's size; chromaFormat
    /// 0 = 4:0:0, 1 = 4:2:0) as libheif encodes a YCbCr / monochrome image. <paramref name="colorDescription"/>: whether
    /// the image carries an nclx (written to the HEVC VUI; libheif's own conversions attach one, a JPEG's raw planes
    /// and gray images have none).
    /// </summary>
    public static byte[] EncodeYuv(byte[] y, byte[]? cb, byte[]? cr, int chromaFormat, byte[]? alpha, int width, int height,
        bool colorDescription, int quality, int? constantQp, int colorPrimaries = 1, int transferCharacteristics = 13,
        byte[]? icc = null, byte[]? exif = null, byte[]? xmp = null, int orientation = 1)
    {
        // libheif's x265 plugin codes 4:2:0: libheif's colour conversion pipeline brings 4:4:4 there with its averaging
        // filter and 4:2:2 through RGB, and the converted image carries the output nclx
        if (chromaFormat == 3)
        {
            (cb, cr) = YCbCr444To420(cb!, cr!, width, height);
            chromaFormat = 1;
            colorDescription = true;
        }
        else if (chromaFormat == 2)
        {
            (y, cb, cr) = YCbCr422To420ViaRgb(y, cb!, cr!, width, height);
            chromaFormat = 1;
            colorDescription = true;
        }

        if (chromaFormat is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(chromaFormat));
        return EncodePlanes(y, cb, cr, chromaFormat == 0, alpha, width, height, 8, colorDescription, quality, constantQp,
            colorPrimaries, transferCharacteristics, icc, exif, xmp, orientation, Encode8);

        static List<(int Type, byte[] Payload)> Encode8(byte[] y, byte[]? u, byte[]? v, int pw, int ph, PictureSettings s)
            => X265Encoder.EncodePicture(y, u, v, pw, ph, pw, pw >> 1, new X265EncodeSettings
            {
                Quality = s.Quality, ConstantQp = s.ConstantQp, CtuSize = s.CtuSize, Monochrome = s.Monochrome,
                WriteColorDescription = s.ColorDescription, ColorPrimaries = s.ColorPrimaries,
                TransferCharacteristics = s.TransferCharacteristics, MatrixCoefficients = 6,
            });
    }

    /// <summary>
    /// Encodes 16-bit samples (row-major; channels 1 = gray, 2 = gray + alpha, 3 = RGB, 4 = RGBA) at 10 bits, as
    /// heif-enc encodes a 16-bit PNG: the samples reduced to 10 bits (<c>&gt;&gt; 6</c>), RGB through libheif's
    /// Op_RRGGBBxx_HDR_to_YCbCr420, x265's 10-bit (HIGH_BIT_DEPTH) build with the main10-intra profile.
    /// </summary>
    public static byte[] Encode10(ReadOnlySpan<ushort> pixels, int width, int height, int channels, int quality, int? constantQp,
        int colorPrimaries = 1, int transferCharacteristics = 13, byte[]? icc = null, byte[]? exif = null, byte[]? xmp = null,
        int orientation = 1)
    {
        bool gray = channels <= 2;
        bool hasAlpha = channels is 2 or 4;
        ushort[] y;
        ushort[]? cb = null, cr = null, alpha = null;
        if (gray)
        {
            y = new ushort[width * height];
            for (int i = 0; i < y.Length; i++) y[i] = (ushort)(pixels[i * channels] >> 6);
        }
        else
        {
            (y, cb, cr) = RgbToYCbCr420Hdr(pixels, width, height, channels);
        }

        if (hasAlpha)
        {
            alpha = new ushort[width * height];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = (ushort)(pixels[(i * channels) + channels - 1] >> 6);
        }

        return EncodePlanes(y, cb, cr, gray, alpha, width, height, 10, colorDescription: !gray, quality, constantQp,
            colorPrimaries, transferCharacteristics, icc, exif, xmp, orientation, Encode10Bit);

        static List<(int Type, byte[] Payload)> Encode10Bit(ushort[] y, ushort[]? u, ushort[]? v, int pw, int ph, PictureSettings s)
            => X265.Hbd.X265Encoder.EncodePicture(y, u, v, pw, ph, pw, pw >> 1, new X265.Hbd.X265EncodeSettings
            {
                Quality = s.Quality, ConstantQp = s.ConstantQp, CtuSize = s.CtuSize, Monochrome = s.Monochrome,
                WriteColorDescription = s.ColorDescription, ColorPrimaries = s.ColorPrimaries,
                TransferCharacteristics = s.TransferCharacteristics, MatrixCoefficients = 6,
            });
    }

    // What libheif's x265 plugin sets per picture.
    private readonly record struct PictureSettings(int Quality, int? ConstantQp, int CtuSize, bool Monochrome, bool ColorDescription,
        int ColorPrimaries, int TransferCharacteristics);

    // The colour item (4:0:0 / 4:2:0 planes) and the alpha item through x265 as libheif's plugin drives it, then the container.
    private static byte[] EncodePlanes<T>(T[] y, T[]? cb, T[]? cr, bool mono, T[]? alpha, int width, int height, int bitDepth,
        bool colorDescription, int quality, int? constantQp, int colorPrimaries, int transferCharacteristics, byte[]? icc,
        byte[]? exif, byte[]? xmp, int orientation, Func<T[], T[]?, T[]?, int, int, PictureSettings, List<(int Type, byte[] Payload)>> encode)
    {
        // libheif's x265 plugin: the picture is the rounded size (even, at least 64), edge-replicated, and the CTU size
        // halves from 64 while the image is smaller than it
        int pw = RoundedSize(width), ph = RoundedSize(height);
        int ctu = 64;
        while (ctu > 16 && (width < ctu || height < ctu)) ctu >>= 1;

        var settings = new PictureSettings(quality, constantQp, ctu, mono, colorDescription, colorPrimaries, transferCharacteristics);
        T[] py = Pad(y, width, height, pw, ph);
        List<(int Type, byte[] Payload)> colorNals;
        if (mono)
        {
            colorNals = encode(py, null, null, pw, ph, settings);
        }
        else
        {
            int cw = (width + 1) >> 1, chh = (height + 1) >> 1;
            colorNals = encode(py, Pad(cb!, cw, chh, pw >> 1, ph >> 1), Pad(cr!, cw, chh, pw >> 1, ph >> 1), pw, ph, settings);
        }

        List<(int Type, byte[] Payload)>? alphaNals = null;
        if (alpha != null)
        {
            alphaNals = encode(Pad(alpha, width, height, pw, ph), null, null, pw, ph,
                settings with { Monochrome = true, ColorDescription = false, ColorPrimaries = 1, TransferCharacteristics = 13 });
        }

        // MIAF 7.3.6.7 (without Amd2): a YCbCr image whose size is not a multiple of the chroma subsampling is not MIAF
        bool miaf = mono || ((width & 1) == 0 && (height & 1) == 0);
        return BuildHeif(width, height, mono, bitDepth, miaf, colorNals, alphaNals, icc, colorPrimaries, transferCharacteristics,
            exif, xmp, orientation);
    }

    private static int RoundedSize(int s) => Math.Max((s + 1) & ~1, 64);

    // A tightly packed w x h plane, edge-replicated to pw x ph.
    private static T[] Pad<T>(T[] src, int w, int h, int pw, int ph)
    {
        var p = new T[pw * ph];
        for (int r = 0; r < h; r++)
        {
            int o = r * pw;
            Array.Copy(src, r * w, p, o, w);
            T last = p[o + w - 1];
            for (int x = w; x < pw; x++) p[o + x] = last;
        }

        for (int r = h; r < ph; r++) Array.Copy(p, (h - 1) * pw, p, r * pw, pw);
        return p;
    }

    /// <summary>
    /// libheif's Op_RRGGBBxx_HDR_to_YCbCr420 (BT.601 matrix, full range, float coefficients): luma per pixel, chroma from
    /// the float mean of each 2x2 block (edge samples repeated at odd edges), of the 10-bit samples.
    /// </summary>
    private static (ushort[] Y, ushort[] Cb, ushort[] Cr) RgbToYCbCr420Hdr(ReadOnlySpan<ushort> rgb, int w, int h, int channels)
    {
        const float kr = 0.299f, kb = 0.114f;
        float kg = 1f - kr - kb;
        float c10 = -kr / (1f - kb) / 2f, c11 = -kg / (1f - kb) / 2f, c12 = 0.5f;
        float c20 = 0.5f, c21 = -kg / (1f - kr) / 2f, c22 = -kb / (1f - kr) / 2f;
        const int fullRange = 1023;
        const float halfRange = 512;

        var y = new ushort[w * h];
        for (int i = 0; i < y.Length; i++)
        {
            float r = rgb[i * channels] >> 6, g = rgb[(i * channels) + 1] >> 6, b = rgb[(i * channels) + 2] >> 6;
            y[i] = ClipU16((r * kr) + (g * kg) + (b * kb), fullRange);
        }

        int cw = (w + 1) / 2, chh = (h + 1) / 2;
        var cb = new ushort[cw * chh];
        var cr = new ushort[cw * chh];
        for (int yy = 0; yy < h; yy += 2)
        {
            int y2 = yy + 1 < h ? yy + 1 : yy;
            for (int x = 0; x < w; x += 2)
            {
                int x2 = x + 1 < w ? x + 1 : x;
                int p00 = ((yy * w) + x) * channels, p01 = ((yy * w) + x2) * channels;
                int p10 = ((y2 * w) + x) * channels, p11 = ((y2 * w) + x2) * channels;
                float r = rgb[p00] >> 6, g = rgb[p00 + 1] >> 6, b = rgb[p00 + 2] >> 6;
                r += rgb[p01] >> 6; g += rgb[p01 + 1] >> 6; b += rgb[p01 + 2] >> 6;
                r += rgb[p10] >> 6; g += rgb[p10 + 1] >> 6; b += rgb[p10 + 2] >> 6;
                r += rgb[p11] >> 6; g += rgb[p11 + 1] >> 6; b += rgb[p11 + 2] >> 6;
                r *= 0.25f; g *= 0.25f; b *= 0.25f;
                cb[((yy / 2) * cw) + (x / 2)] = ClipU16(halfRange + ((r * c10) + (g * c11) + (b * c12)), fullRange);
                cr[((yy / 2) * cw) + (x / 2)] = ClipU16(halfRange + ((r * c20) + (g * c21) + (b * c22)), fullRange);
            }
        }

        return (y, cb, cr);
    }

    // clip_f_u16: (int32_t)(v + 0.5f), clipped to 0..max
    private static ushort ClipU16(float v, int max)
    {
        int x = (int)(v + 0.5f);
        return (ushort)(x < 0 ? 0 : x > max ? max : x);
    }

    /// <summary>
    /// libheif's Op_RGB24_32_to_YCbCr (BT.601 matrix, full range, float coefficients) with 'average' 4:2:0 chroma
    /// (the integer mean of each 2x2 block, or of the 2 / 1 samples at an odd edge).
    /// </summary>
    private static (byte[] Y, byte[] Cb, byte[] Cr) RgbToYCbCr420(ReadOnlySpan<byte> rgb, int w, int h, int channels)
    {
        const float kr = 0.299f, kb = 0.114f;
        float kg = 1f - kr - kb;
        float c00 = kr, c01 = kg, c02 = kb;
        float c10 = -kr / (1f - kb) / 2f, c11 = -kg / (1f - kb) / 2f, c12 = 0.5f;
        float c20 = 0.5f, c21 = -kg / (1f - kr) / 2f, c22 = -kb / (1f - kr) / 2f;

        var y = new byte[w * h];
        for (int r = 0; r < h; r++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((r * w) + x) * channels;
                float fr = rgb[o], fg = rgb[o + 1], fb = rgb[o + 2];
                y[(r * w) + x] = ClipToByte((fr * c00) + (fg * c01) + (fb * c02));
            }
        }

        int cw = (w + 1) / 2, chh = (h + 1) / 2;
        var cb = new byte[cw * chh];
        var cr = new byte[cw * chh];
        for (int cy = 0; cy < chh; cy++)
        {
            int y0 = cy * 2, y1 = Math.Min(y0 + 1, h - 1);
            int ny = y1 > y0 ? 2 : 1;
            for (int cx = 0; cx < cw; cx++)
            {
                int x0 = cx * 2, x1 = Math.Min(x0 + 1, w - 1);
                int nx = x1 > x0 ? 2 : 1;
                int o00 = ((y0 * w) + x0) * channels, o01 = ((y0 * w) + x1) * channels;
                int o10 = ((y1 * w) + x0) * channels, o11 = ((y1 * w) + x1) * channels;
                int ar, ag, ab;
                if (nx == 2 && ny == 2)
                {
                    ar = (rgb[o00] + rgb[o01] + rgb[o10] + rgb[o11]) / 4;
                    ag = (rgb[o00 + 1] + rgb[o01 + 1] + rgb[o10 + 1] + rgb[o11 + 1]) / 4;
                    ab = (rgb[o00 + 2] + rgb[o01 + 2] + rgb[o10 + 2] + rgb[o11 + 2]) / 4;
                }
                else if (nx == 2)
                {
                    ar = (rgb[o00] + rgb[o01]) / 2;
                    ag = (rgb[o00 + 1] + rgb[o01 + 1]) / 2;
                    ab = (rgb[o00 + 2] + rgb[o01 + 2]) / 2;
                }
                else if (ny == 2)
                {
                    ar = (rgb[o00] + rgb[o10]) / 2;
                    ag = (rgb[o00 + 1] + rgb[o10 + 1]) / 2;
                    ab = (rgb[o00 + 2] + rgb[o10 + 2]) / 2;
                }
                else
                {
                    ar = rgb[o00];
                    ag = rgb[o00 + 1];
                    ab = rgb[o00 + 2];
                }

                float fr = ar, fg = ag, fb = ab;
                cb[(cy * cw) + cx] = ClipToByte((fr * c10) + (fg * c11) + (fb * c12) + 128f);
                cr[(cy * cw) + cx] = ClipToByte((fr * c20) + (fg * c21) + (fb * c22) + 128f);
            }
        }

        return (y, cb, cr);
    }

    /// <summary>libheif's Op_YCbCr444_to_YCbCr420_average: the rounded mean of each 2x2 block (of the 2 samples at an
    /// odd edge, the sample itself at an odd corner).</summary>
    private static (byte[] Cb, byte[] Cr) YCbCr444To420(byte[] cb, byte[] cr, int w, int h)
    {
        int cw = (w + 1) / 2, chh = (h + 1) / 2;
        var ocb = new byte[cw * chh];
        var ocr = new byte[cw * chh];
        foreach (var (src, dst) in new[] { (cb, ocb), (cr, ocr) })
        {
            if ((h & 1) != 0)
            {
                for (int x = 0; x < w - 1; x += 2) dst[((chh - 1) * cw) + (x / 2)] = (byte)((src[((h - 1) * w) + x] + src[((h - 1) * w) + x + 1] + 1) / 2);
            }

            if ((w & 1) != 0)
            {
                for (int y = 0; y < h - 1; y += 2) dst[((y / 2) * cw) + cw - 1] = (byte)((src[(y * w) + w - 1] + src[((y + 1) * w) + w - 1] + 1) / 2);
            }

            if ((w & 1) != 0 && (h & 1) != 0) dst[((chh - 1) * cw) + cw - 1] = src[((h - 1) * w) + w - 1];

            for (int y = 0; y < h - 1; y += 2)
            {
                for (int x = 0; x < w - 1; x += 2)
                {
                    int o = (y * w) + x;
                    dst[((y / 2) * cw) + (x / 2)] = (byte)((src[o] + src[o + 1] + src[o + w] + src[o + w + 1] + 2) / 4);
                }
            }
        }

        return (ocb, ocr);
    }

    /// <summary>
    /// libheif's 4:2:2 -> 4:2:0 pipeline: Op_YCbCr_to_RGB (nearest-neighbour chroma, the default BT.601 coefficients of
    /// an image without nclx, full range) to planar RGB, then Op_RGB_to_YCbCr (matrix 6, full range; chroma from the
    /// float mean of each 2x2 RGB block, the edge samples repeated at odd edges).
    /// </summary>
    private static (byte[] Y, byte[] Cb, byte[] Cr) YCbCr422To420ViaRgb(byte[] y, byte[] cb, byte[] cr, int w, int h)
    {
        const float rCr = 1.402f, gCb = -0.344136f, gCr = -0.714136f, bCb = 1.772f;
        int cw422 = (w + 1) / 2;
        var r = new byte[w * h];
        var g = new byte[w * h];
        var b = new byte[w * h];
        for (int yy = 0; yy < h; yy++)
        {
            for (int x = 0; x < w; x++)
            {
                float fy = y[(yy * w) + x];
                float fcb = cb[(yy * cw422) + (x >> 1)] - 128;
                float fcr = cr[(yy * cw422) + (x >> 1)] - 128;
                int i = (yy * w) + x;
                r[i] = ClipToByte(fy + (rCr * fcr));
                g[i] = ClipToByte(fy + (gCb * fcb) + (gCr * fcr));
                b[i] = ClipToByte(fy + (bCb * fcb));
            }
        }

        const float kr = 0.299f, kb = 0.114f;
        float kg = 1f - kr - kb;
        float c10 = -kr / (1f - kb) / 2f, c11 = -kg / (1f - kb) / 2f, c12 = 0.5f;
        float c20 = 0.5f, c21 = -kg / (1f - kr) / 2f, c22 = -kb / (1f - kr) / 2f;
        var oy = new byte[w * h];
        for (int i = 0; i < oy.Length; i++)
        {
            float fr = r[i], fg = g[i], fb = b[i];
            oy[i] = ClipToByte((fr * kr) + (fg * kg) + (fb * kb));
        }

        int cw = (w + 1) / 2, chh = (h + 1) / 2;
        var ocb = new byte[cw * chh];
        var ocr = new byte[cw * chh];
        for (int yy = 0; yy < h; yy += 2)
        {
            int y2 = yy + 1 < h ? yy + 1 : yy;
            for (int x = 0; x < w; x += 2)
            {
                int x2 = x + 1 < w ? x + 1 : x;
                int p00 = (yy * w) + x, p01 = (yy * w) + x2, p10 = (y2 * w) + x, p11 = (y2 * w) + x2;
                float fr = r[p00], fg = g[p00], fb = b[p00];
                fr += r[p01]; fg += g[p01]; fb += b[p01];
                fr += r[p10]; fg += g[p10]; fb += b[p10];
                fr += r[p11]; fg += g[p11]; fb += b[p11];
                fr *= 0.25f; fg *= 0.25f; fb *= 0.25f;
                ocb[((yy / 2) * cw) + (x / 2)] = ClipToByte((fr * c10) + (fg * c11) + (fb * c12) + 128f);
                ocr[((yy / 2) * cw) + (x / 2)] = ClipToByte((fr * c20) + (fg * c21) + (fb * c22) + 128f);
            }
        }

        return (oy, ocb, ocr);
    }

    // (int32_t)(v + 0.5f), clipped to 0..255
    private static byte ClipToByte(float v)
    {
        int x = (int)(v + 0.5f);
        return (byte)(x < 0 ? 0 : x > 255 ? 255 : x);
    }

    // ---- libheif's HEIF container ----

    private sealed class Item
    {
        public int Id;
        public string Type = "hvc1";
        public bool Hidden;
        public string? ContentType;
        public byte[] Data = Array.Empty<byte>();
        public readonly List<(bool Essential, int Index)> Props = new();
    }

    private static byte[] BuildHeif(int width, int height, bool gray, int bitDepth, bool miaf, List<(int Type, byte[] Payload)> colorNals,
        List<(int Type, byte[] Payload)>? alphaNals, byte[]? icc, int colorPrimaries, int transferCharacteristics,
        byte[]? exif, byte[]? xmp, int orientation)
    {
        var ipco = new List<byte[]>();
        var items = new List<Item>();
        var irefs = new List<byte[]>();

        // ipco find_or_append_child_box: an identical property is shared
        int FindOrAppend(byte[] box)
        {
            for (int i = 0; i < ipco.Count; i++)
            {
                if (ipco[i].AsSpan().SequenceEqual(box)) return i + 1;
            }

            ipco.Add(box);
            return ipco.Count;
        }

        (int? irot, int? imir) = orientation switch
        {
            2 => ((int?)null, (int?)1),
            3 => (2, null),
            4 => (null, 0),
            5 => (3, 1),
            6 => (3, null),
            7 => (3, 0),
            8 => (1, null),
            _ => (null, null),
        };

        // ImageItem::encode_to_item: hvcC, colr, ispe, clap, pixi, then the orientation properties
        Item EncodeItem(List<(int Type, byte[] Payload)> nals, bool alpha, bool mono)
        {
            var (hvcC, data, codedW, codedH) = HvcCAndData(nals);
            var item = new Item { Id = items.Count + 1, Data = data };
            items.Add(item);
            item.Props.Add((true, FindOrAppend(Box("hvcC", hvcC))));
            if (!alpha)
            {
                if (icc != null) item.Props.Add((false, FindOrAppend(Box("colr", Concat(Fourcc("prof"), icc)))));
                else item.Props.Add((false, FindOrAppend(Box("colr", Concat(Fourcc("nclx"), U16(colorPrimaries), U16(transferCharacteristics), U16(6), new byte[] { 0x80 })))));
            }

            item.Props.Add((false, FindOrAppend(FullBox("ispe", 0, 0, Concat(U32((uint)codedW), U32((uint)codedH))))));
            if (codedW != width || codedH != height)
            {
                item.Props.Add((true, FindOrAppend(Box("clap", Concat(U32((uint)width), U32(1), U32((uint)height), U32(1),
                    U32((uint)-(codedW - width)), U32(2), U32((uint)-(codedH - height)), U32(2))))));
            }

            byte bd = (byte)bitDepth;
            item.Props.Add((false, FindOrAppend(FullBox("pixi", 0, 0, mono ? new byte[] { 1, bd } : new byte[] { 3, bd, bd, bd }))));
            if (irot != null) item.Props.Add((true, FindOrAppend(Box("irot", new[] { (byte)irot.Value }))));
            if (imir != null) item.Props.Add((true, FindOrAppend(Box("imir", new[] { (byte)imir.Value }))));
            return item;
        }

        Item main = EncodeItem(colorNals, alpha: false, mono: gray);
        int brandProfile = HvcCBrand(ipco[main.Props[0].Index - 1]);

        if (alphaNals != null)
        {
            Item alpha = EncodeItem(alphaNals, alpha: true, mono: true);
            irefs.Add(Box("auxl", Concat(U16(alpha.Id), U16(1), U16(main.Id))));
            alpha.Props.Add((true, FindOrAppend(FullBox("auxC", 0, 0, Concat(Encoding.ASCII.GetBytes("urn:mpeg:hevc:2015:auxid:1"), new byte[] { 0 })))));
        }

        if (exif != null)
        {
            // HeifContext::add_exif_metadata: the data is preceded by the offset of its TIFF header
            uint offset = 0;
            while (offset + 4 < (uint)exif.Length)
            {
                var m = exif.AsSpan((int)offset, 4);
                if (m.SequenceEqual("MM\0*"u8) || m.SequenceEqual("II*\0"u8)) break;
                offset++;
            }

            if (offset < (uint)exif.Length)
            {
                var item = new Item { Id = items.Count + 1, Type = "Exif", Hidden = true, Data = Concat(U32(offset), exif) };
                items.Add(item);
                irefs.Add(Box("cdsc", Concat(U16(item.Id), U16(1), U16(main.Id))));
            }
        }

        if (xmp != null)
        {
            var item = new Item { Id = items.Count + 1, Type = "mime", Hidden = true, ContentType = "application/rdf+xml", Data = xmp };
            items.Add(item);
            irefs.Add(Box("cdsc", Concat(U16(item.Id), U16(1), U16(main.Id))));
        }

        // ftyp (brands.cc compute_compatible_brands): mif1, the primary image's brand, miaf
        string? brand = brandProfile switch { 1 => "heic", 2 => "heix", _ => null };
        var compat = new List<byte[]> { Fourcc("mif1") };
        if (brand != null) compat.Add(Fourcc(brand));
        if (miaf) compat.Add(Fourcc("miaf"));
        byte[] ftyp = Box("ftyp", Concat(Fourcc(brand ?? "mif1"), U32(0), Concat(compat.ToArray())));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0), new byte[] { 0 }));
        byte[] pitm = FullBox("pitm", 0, 0, U16(main.Id));

        var infes = new List<byte[]>();
        foreach (var it in items)
        {
            byte[] body = Concat(U16(it.Id), U16(0), Fourcc(it.Type), new byte[] { 0 });
            if (it.ContentType != null) body = Concat(body, Encoding.ASCII.GetBytes(it.ContentType), new byte[] { 0, 0 });
            infes.Add(FullBox("infe", 2, it.Hidden ? 1u : 0u, body));
        }

        byte[] iinf = FullBox("iinf", 0, 0, Concat(U16(infes.Count), Concat(infes.ToArray())));

        // ipma: each item's descriptive properties before its transformative ones (Box_ipma::sort_properties)
        bool wideIndex = ipco.Count > 127;
        var ipmaEntries = new List<byte[]>();
        foreach (var it in items)
        {
            if (it.Props.Count == 0) continue;
            var sorted = new List<(bool Essential, int Index)>();
            foreach (var p in it.Props) if (!IsTransformative(ipco[p.Index - 1])) sorted.Add(p);
            foreach (var p in it.Props) if (IsTransformative(ipco[p.Index - 1])) sorted.Add(p);
            var e = new List<byte>();
            e.AddRange(U16(it.Id));
            e.Add((byte)sorted.Count);
            foreach (var (ess, idx) in sorted)
            {
                if (wideIndex) e.AddRange(U16((ess ? 0x8000 : 0) | idx));
                else e.Add((byte)((ess ? 0x80 : 0) | idx));
            }

            ipmaEntries.Add(e.ToArray());
        }

        byte[] ipma = FullBox("ipma", 0, wideIndex ? 1u : 0u, Concat(U32((uint)ipmaEntries.Count), Concat(ipmaEntries.ToArray())));
        byte[] iprp = Box("iprp", Concat(Box("ipco", Concat(ipco.ToArray())), ipma));
        byte[] iref = irefs.Count > 0 ? FullBox("iref", 0, 0, Concat(irefs.ToArray())) : Array.Empty<byte>();

        // iloc version 0, offset/length/base_offset sizes 4: each item one extent at offset 0 from its base offset
        // (its absolute position in mdat, in item order). The meta size does not depend on the offsets.
        byte[] BuildMeta(uint dataStart)
        {
            var entries = new List<byte[]>();
            uint pos = dataStart;
            foreach (var it in items)
            {
                entries.Add(Concat(U16(it.Id), U16(0), U32(pos), U16(1), U32(0), U32((uint)it.Data.Length)));
                pos += (uint)it.Data.Length;
            }

            byte[] iloc = FullBox("iloc", 0, 0, Concat(new byte[] { 0x44, 0x40 }, U16(entries.Count), Concat(entries.ToArray())));
            return FullBox("meta", 0, 0, Concat(hdlr, iloc, iinf, pitm, iprp, iref));
        }

        byte[] meta = BuildMeta(0);
        meta = BuildMeta((uint)(ftyp.Length + meta.Length + 8));
        var mdatParts = new List<byte[]>();
        foreach (var it in items) mdatParts.Add(it.Data);
        return Concat(ftyp, meta, Box("mdat", Concat(mdatParts.ToArray())));
    }

    private static bool IsTransformative(byte[] box)
    {
        string t = Encoding.ASCII.GetString(box, 4, 4);
        return t is "clap" or "irot" or "imir";
    }

    // ImageItem_HEVC::get_compatible_brand from the hvcC box: 1 = heic (Main / Main Still Picture compatible),
    // 2 = heix (Main 10 / RExt), 0 = none.
    private static int HvcCBrand(byte[] hvcCBox)
    {
        int idc = hvcCBox[9] & 0x1F;
        uint flags = (uint)((hvcCBox[10] << 24) | (hvcCBox[11] << 16) | (hvcCBox[12] << 8) | hvcCBox[13]);
        bool Compatible(int p) => idc == p || ((flags >> (31 - p)) & 1) != 0;
        if (Compatible(1) || Compatible(3)) return 1;
        if (Compatible(2) || Compatible(4)) return 2;
        return 0;
    }

    /// <summary>
    /// libheif's hevc_enc: the parameter sets go into the hvcC record (configuration parsed from the SPS), every other
    /// NAL unit into the item data with a 4-byte length; also the coded size (the SPS size minus its conformance window).
    /// </summary>
    private static (byte[] HvcC, byte[] Data, int Width, int Height) HvcCAndData(List<(int Type, byte[] Payload)> nals)
    {
        var arrays = new List<(int Type, List<byte[]> Units)>();
        var data = new List<byte>();
        byte[]? sps = null;
        foreach (var (type, nal) in nals)
        {
            if (type is 32 or 33 or 34)
            {
                if (type == 33) sps = nal;
                int a = arrays.FindIndex(x => x.Type == type);
                if (a < 0) arrays.Add((type, new List<byte[]> { nal }));
                else arrays[a].Units.Add(nal);
            }
            else
            {
                data.AddRange(U32((uint)nal.Length));
                data.AddRange(nal);
            }
        }

        if (sps == null) throw new InvalidOperationException("HEVC encoder produced no SPS.");
        var c = ParseSps(sps);
        var h = new List<byte>
        {
            1,
            (byte)((c.ProfileSpace << 6) | (c.Tier << 5) | c.ProfileIdc),
        };
        h.AddRange(U32(c.CompatFlags));
        h.AddRange(c.Constraint);
        h.Add(c.Level);
        h.AddRange(U16(0xF000));                    // min_spatial_segmentation_idc 0
        h.Add(0xFC);                                // parallelismType 0
        h.Add((byte)(0xFC | c.ChromaFormat));
        h.Add((byte)(0xF8 | (c.BitDepthLuma - 8)));
        h.Add((byte)(0xF8 | (c.BitDepthChroma - 8)));
        h.AddRange(U16(0));                         // avgFrameRate
        h.Add((byte)((1 << 3) | (c.TemporalIdNested << 2) | 3)); // 1 temporal layer, 4-byte NAL lengths
        h.Add((byte)arrays.Count);
        foreach (var (type, units) in arrays)
        {
            h.Add((byte)((1 << 6) | type));         // libheif writes array_completeness at bit 6
            h.AddRange(U16(units.Count));
            foreach (var u in units)
            {
                h.AddRange(U16(u.Length));
                h.AddRange(u);
            }
        }

        return (h.ToArray(), data.ToArray(), c.Width, c.Height);
    }

    private sealed class SpsConfig
    {
        public int ProfileSpace, Tier, ProfileIdc, TemporalIdNested, ChromaFormat, BitDepthLuma, BitDepthChroma, Width, Height;
        public uint CompatFlags;
        public byte[] Constraint = new byte[6];
        public byte Level;
    }

    // parse_sps_for_hvcC_configuration
    private static SpsConfig ParseSps(byte[] nal)
    {
        // remove start-code emulation prevention bytes
        var rb = new List<byte>(nal.Length);
        for (int i = 0; i < nal.Length; i++)
        {
            if (i + 2 < nal.Length && nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] == 3)
            {
                rb.Add(0);
                rb.Add(0);
                i += 2;
            }
            else
            {
                rb.Add(nal[i]);
            }
        }

        var r = new SpsReader(rb.ToArray());
        var c = new SpsConfig();
        r.Skip(16 + 4);
        int maxSubLayersMinus1 = r.Bits(3);
        c.TemporalIdNested = r.Bits(1);
        c.ProfileSpace = r.Bits(2);
        c.Tier = r.Bits(1);
        c.ProfileIdc = r.Bits(5);
        c.CompatFlags = (uint)((r.Bits(16) << 16) | r.Bits(16));
        for (int i = 0; i < 6; i++) c.Constraint[i] = (byte)r.Bits(8);
        c.Level = (byte)r.Bits(8);
        var profilePresent = new bool[maxSubLayersMinus1];
        var levelPresent = new bool[maxSubLayersMinus1];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = r.Bits(1) != 0;
            levelPresent[i] = r.Bits(1) != 0;
        }

        if (maxSubLayersMinus1 > 0)
        {
            for (int i = maxSubLayersMinus1; i < 8; i++) r.Skip(2);
        }

        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) r.Skip(88);
            if (levelPresent[i]) r.Skip(8);
        }

        r.Uvlc();                                   // sps_seq_parameter_set_id
        c.ChromaFormat = (int)r.Uvlc();
        if (c.ChromaFormat == 3) r.Skip(1);
        c.Width = (int)r.Uvlc();
        c.Height = (int)r.Uvlc();
        if (r.Bits(1) != 0)
        {
            int left = (int)r.Uvlc(), right = (int)r.Uvlc(), top = (int)r.Uvlc(), bottom = (int)r.Uvlc();
            int subH = c.ChromaFormat is 1 or 2 ? 2 : 1, subV = c.ChromaFormat == 1 ? 2 : 1;
            c.Width -= subH * (left + right);
            c.Height -= subV * (top + bottom);
        }

        c.BitDepthLuma = (int)r.Uvlc() + 8;
        c.BitDepthChroma = (int)r.Uvlc() + 8;
        return c;
    }

    private sealed class SpsReader(byte[] d)
    {
        private int pos;

        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, pos++)
            {
                int b = (pos >> 3) < d.Length ? (d[pos >> 3] >> (7 - (pos & 7))) & 1 : 0;
                v = (v << 1) | b;
            }

            return v;
        }

        public void Skip(int n) => pos += n;

        public uint Uvlc()
        {
            int zeros = 0;
            while (Bits(1) == 0)
            {
                if (++zeros > 31) throw new InvalidOperationException("Invalid HEVC SPS.");
            }

            return zeros == 0 ? 0u : (uint)((1L << zeros) - 1 + Bits(zeros));
        }
    }

    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Fourcc(type), payload);

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
        => Box(type, Concat(new[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] Fourcc(string s) => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] Concat(params byte[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var r = new byte[n];
        int o = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, r, o, p.Length);
            o += p.Length;
        }

        return r;
    }
}
