using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// Encodes a JPEG file as HEIC the way libheif's <c>heif-enc</c> reads JPEG input: a 4:2:0 YCbCr or greyscale JPEG's
    /// decoded planes are coded as they are (libjpeg-turbo accurate IDCT, no colour conversion, no colour description
    /// in the HEVC VUI); the ICC profile (libjpeg read_icc_profile), the first Exif and the first XMP segment are kept,
    /// and the Exif orientation becomes irot/imir (the Exif itself is left unchanged). Other JPEGs go through the pixel
    /// path (<see cref="EncodeHeic"/>).
    /// </summary>
    public static byte[] EncodeHeicFromJpeg(byte[] jpeg, HeicEncodeOptions? options = null)
    {
        options ??= new HeicEncodeOptions();
        var segs = JpegCoder.ScanSegments(jpeg, 0);
        byte[]? exif = null, xmp = null;
        ReadOnlySpan<byte> xmpTag = "http://ns.adobe.com/xap/1.0/\0"u8;
        foreach (var (marker, off, len) in segs)
        {
            if (marker != 0xE1) continue;
            if (exif == null && len >= 6 && jpeg.AsSpan(off, 6).SequenceEqual("Exif\0\0"u8)) exif = jpeg.AsSpan(off + 6, len - 6).ToArray();
            else if (xmp == null && len >= xmpTag.Length && jpeg.AsSpan(off, xmpTag.Length).SequenceEqual(xmpTag))
                xmp = jpeg.AsSpan(off + xmpTag.Length, len - xmpTag.Length).ToArray();
        }

        byte[]? icc = ReadJpegIcc(jpeg, segs);
        int orientation = exif != null ? ExifOrientation(exif) : 1;
        if (orientation is < 1 or > 8) orientation = 1;

        var raw = JpegCoder.ReadRawYuv(jpeg);
        int q = Math.Clamp(options.Quality, 0, 100);
        if (raw != null)
        {
            bool mono = raw.Layout == Av1.Av1PixelLayout.I400;
            int chroma = raw.Layout switch
            {
                Av1.Av1PixelLayout.I400 => 0,
                Av1.Av1PixelLayout.I420 => 1,
                Av1.Av1PixelLayout.I422 => 2,
                _ => 3,
            };
            return Hevc.HeicEncoder.EncodeYuv(raw.Planes[0], mono ? null : raw.Planes[1], mono ? null : raw.Planes[2], chroma,
                null, raw.Width, raw.Height, colorDescription: false, q, options.Qp, 1, 13, icc, exif, xmp, orientation);
        }

        // other YCbCr samplings: libjpeg's full-size YCbCr output (its upsamplers), chroma point-sampled at even positions
        var comps = JpegCoder.ReadComponents(jpeg);
        if (comps != null && comps.Planes.Length == 3 && !comps.IsRgb)
        {
            int w = comps.Width, h = comps.Height;
            var full = new byte[3][];
            for (int ci = 0; ci < 3; ci++) full[ci] = JpegCoder.Upsample(comps, ci) ?? throw new NotSupportedException("JPEG sampling not supported.");
            int cw = (w + 1) / 2, chh = (h + 1) / 2;
            var cb = new byte[cw * chh];
            var cr = new byte[cw * chh];
            for (int cy = 0; cy < chh; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    cb[(cy * cw) + cx] = full[1][(cy * 2 * w) + (cx * 2)];
                    cr[(cy * cw) + cx] = full[2][(cy * 2 * w) + (cx * 2)];
                }
            }

            return Hevc.HeicEncoder.EncodeYuv(full[0], cb, cr, 1, null, w, h, colorDescription: false, q, options.Qp, 1, 13, icc, exif,
                xmp, orientation);
        }

        using var image = JpegCoder.ReadLibjpegExact(jpeg);
        if (image.Colorspace == SharpImage.Core.ColorspaceType.CMYK)
        {
            // heif-enc's CMYK / YCCK reading: libjpeg's CMYK samples (Adobe-inverted, as stored) to RGB as C*K/255 etc.
            // A CMYK ICC profile cannot describe the RGB result and is dropped (heif-enc copies it into colr).
            bool adobe = false;
            foreach (var (marker, off, len) in segs)
            {
                if (marker == 0xEE && len >= 12 && jpeg.AsSpan(off, 5).SequenceEqual("Adobe"u8)) adobe = true;
            }

            int w = (int)image.Columns, h = (int)image.Rows, nch = image.NumberOfChannels;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                var row = image.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    int o = x * nch, d = ((y * w) + x) * 3;
                    int k = Raw(row[o + 3], adobe);
                    rgb[d] = (byte)(Raw(row[o], adobe) * k / 255);
                    rgb[d + 1] = (byte)(Raw(row[o + 1], adobe) * k / 255);
                    rgb[d + 2] = (byte)(Raw(row[o + 2], adobe) * k / 255);
                }
            }

            bool cmykIcc = icc is { Length: >= 20 } && icc.AsSpan(16, 4).SequenceEqual("CMYK"u8);
            return Hevc.HeicEncoder.Encode(rgb, w, h, 3, q, options.Qp, 1, 13, cmykIcc ? null : icc, exif, xmp, orientation);
        }

        return EncodeHeic(image, options);

        // libjpeg's CMYK sample from the frame's ink amount (Adobe files store inverted samples)
        static int Raw(ushort ink, bool adobe)
        {
            int v = SharpImage.Core.Quantum.ScaleToByte(ink);
            return adobe ? 255 - v : v;
        }
    }

    // libjpeg's read_icc_profile (as heif-enc's ReadICCProfileFromJPEG): the APP2 ICC_PROFILE chunks in sequence-number
    // order; any inconsistency (count mismatch, bad / duplicate / missing sequence numbers) means no profile.
    private static byte[]? ReadJpegIcc(byte[] jpeg, List<(int Marker, int Offset, int Length)> segs)
    {
        ReadOnlySpan<byte> tag = "ICC_PROFILE\0"u8;
        int count = 0;
        var parts = new (int Offset, int Length)?[256];
        foreach (var (marker, off, len) in segs)
        {
            if (marker != 0xE2 || len < 14 || !jpeg.AsSpan(off, 12).SequenceEqual(tag)) continue;
            int n = jpeg[off + 13], seq = jpeg[off + 12];
            if (count == 0) count = n;
            else if (count != n) return null;
            if (seq <= 0 || seq > count || parts[seq] != null) return null;
            parts[seq] = (off + 14, len - 14);
        }

        if (count == 0) return null;
        int total = 0;
        for (int s = 1; s <= count; s++)
        {
            if (parts[s] == null) return null;
            total += parts[s]!.Value.Length;
        }

        if (total <= 0) return null;
        var icc = new byte[total];
        int o = 0;
        for (int s = 1; s <= count; s++)
        {
            var (po, pl) = parts[s]!.Value;
            Buffer.BlockCopy(jpeg, po, icc, o, pl);
            o += pl;
        }

        return icc;
    }

    // libheif heifio read_exif_orientation_tag: the Orientation tag (a single SHORT) found in IFD0, the Exif IFD or the
    // following IFDs (depth-limited), 1 if absent.
    private static int ExifOrientation(byte[] exif)
    {
        uint size = (uint)exif.Length;
        if (size < 8 || (exif[0] != 'I' && exif[0] != 'M') || (exif[1] != 'I' && exif[1] != 'M')) return 1;
        bool le = exif[0] == 'I';

        uint Read16(uint pos) => pos + 2 > size ? 0u : le ? (uint)(exif[pos] | (exif[pos + 1] << 8)) : (uint)((exif[pos] << 8) | exif[pos + 1]);
        uint Read32(uint pos) => pos + 4 > size ? 0u : le
            ? (uint)(exif[pos] | (exif[pos + 1] << 8) | (exif[pos + 2] << 16) | (exif[pos + 3] << 24))
            : (uint)((exif[pos] << 24) | (exif[pos + 1] << 16) | (exif[pos + 2] << 8) | exif[pos + 3]);

        uint Find(uint offset, int depth)
        {
            if (depth > 5 || offset == 0 || size < 6 || size - 2 - 4 < offset) return 0;
            uint cnt = Read16(offset);
            uint tableSize = 2 + (cnt * 12) + 4;
            if (tableSize > size || size - tableSize < offset) return 0;
            for (uint i = 0; i < cnt; i++)
            {
                uint tag = Read16(offset + 2 + (i * 12));
                if (tag == 0x0112) return offset + 2 + (i * 12);
                if (tag == 0x8769)
                {
                    uint sub = Find(Read32(offset + 2 + (i * 12) + 8), depth + 1);
                    if (sub != 0) return sub;
                }
            }

            return Find(Read32(offset + 2 + (cnt * 12)), depth + 1);
        }

        uint p = Find(Read32(4), 1);
        if (p == 0) return 1;
        return Read16(p + 2) == 3 && Read32(p + 4) == 1 ? (int)Read16(p + 8) : 1;
    }
}
