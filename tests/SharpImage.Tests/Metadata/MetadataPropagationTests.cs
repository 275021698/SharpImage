using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Formats.Jxl;
using SharpImage.Formats.Jxl.Lj;
using SharpImage.Image;
using SharpImage.Metadata;

namespace SharpImage.Tests.Metadata;

/// <summary>
/// The colour description (ICC profile / CICP), EXIF and XMP of an image survive conversion: every writer that can
/// store them does, the readers bring them back, transforms keep them, and formats that cannot describe a colour space
/// get the samples converted to sRGB.
/// </summary>
public class MetadataPropagationTests
{
    private static readonly string IccDir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "cms", "icc");
    private const string XmpPacket = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>";

    private static ImageFrame Gradient(int w = 24, int h = 16, bool alpha = false)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, alpha);
        int nc = img.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * nc] = (ushort)(x * 65535 / (w - 1));
                row[(x * nc) + 1] = (ushort)(y * 65535 / (h - 1));
                row[(x * nc) + 2] = 0x6000;
                if (alpha) row[(x * nc) + 3] = (ushort)(65535 - (x * 1000));
            }
        }

        return img;
    }

    private static ExifProfile Exif(bool littleEndian = true, ushort orientation = 1)
    {
        var p = new ExifProfile { IsLittleEndian = littleEndian };
        byte[] make = System.Text.Encoding.ASCII.GetBytes("TestCam\0");
        p.SetTag(new ExifEntry { Tag = ExifTag.Make, DataType = ExifDataType.Ascii, Count = (uint)make.Length, Value = make });
        p.SetTag(new ExifEntry
        {
            Tag = ExifTag.Orientation, DataType = ExifDataType.Short, Count = 1,
            Value = littleEndian ? [(byte)orientation, 0] : [0, (byte)orientation],
        });
        byte[] date = System.Text.Encoding.ASCII.GetBytes("2026:10:21 12:00:00\0");
        p.ExifTags.Add(new ExifEntry { Tag = 0x9003, DataType = ExifDataType.Ascii, Count = (uint)date.Length, Value = date });
        p.GpsTags.Add(new ExifEntry { Tag = 0x0001, DataType = ExifDataType.Ascii, Count = 2, Value = "N\0"u8.ToArray() });
        return p;
    }

    private static ImageFrame Tagged(string iccFile = "DisplayP3.icc", bool alpha = false, ushort orientation = 1)
    {
        var img = Gradient(alpha: alpha);
        img.IccProfile = File.ReadAllBytes(Path.Combine(IccDir, iccFile));
        img.Metadata.ExifProfile = Exif(orientation: orientation);
        img.Metadata.Xmp = XmpPacket;
        return img;
    }

    private static async Task AssertCarried(ImageFrame src, ImageFrame got, bool exifIfdTags = true)
    {
        await Assert.That(got.IccProfile).IsNotNull();
        await Assert.That(got.IccProfile!.AsSpan().SequenceEqual(src.IccProfile)).IsTrue();
        await Assert.That(got.Metadata.Xmp).IsEqualTo(XmpPacket);
        var exif = got.Metadata.ExifProfile;
        await Assert.That(exif).IsNotNull();
        await Assert.That(exif!.GetTag(ExifTag.Make)?.GetString()).IsEqualTo("TestCam");
        if (exifIfdTags)
        {
            await Assert.That(exif.GetTag(0x9003)?.GetString()).IsEqualTo("2026:10:21 12:00:00");
            await Assert.That(exif.GpsTags.Count).IsEqualTo(1);
        }
    }

    // R = G = B through an RGB profile renders as the gray value through the gray profile (to sRGB, by the CMS).
    private static async Task AssertNeutralEquivalent(byte[] grayIcc, byte[] rgbIcc)
    {
        await Assert.That(System.Text.Encoding.ASCII.GetString(rgbIcc, 16, 4)).IsEqualTo("RGB ");
        var tg = LjCms.Transform.Create(LjCms.FromIcc(grayIcc)!, LjCe.Srgb(true), 10000f)!;   // PQ: 1.0 = 10000 nits, as in ICC
        var tc = LjCms.Transform.Create(LjCms.FromIcc(rgbIcc)!, LjCe.Srgb(false), 10000f)!;
        float[] g = new float[33], rgb = new float[99], og = new float[33], orgb = new float[99];
        for (int i = 0; i <= 32; i++) g[i] = rgb[3 * i] = rgb[(3 * i) + 1] = rgb[(3 * i) + 2] = i / 32f;
        tg.Run(g, og, 33);
        tc.Run(rgb, orgb, 33);
        double max = 0;
        for (int i = 0; i <= 32; i++)
            for (int c = 0; c < 3; c++) max = Math.Max(max, Math.Abs(orgb[(3 * i) + c] - og[i]));
        await Assert.That(max).IsLessThan(0.002);
    }

    // ---- WebP ----

    [Test]
    public async Task WebP_Lossless_CarriesIccExifXmp()
    {
        using var src = Tagged(alpha: true);
        byte[] webp = FormatRegistry.Encode(src, ImageFileFormat.WebP);
        // libwebp mux layout: VP8X (ICC | alpha | EXIF | XMP), ICCP right after it
        await Assert.That(System.Text.Encoding.ASCII.GetString(webp, 12, 4)).IsEqualTo("VP8X");
        await Assert.That((int)webp[20]).IsEqualTo(0x20 | 0x10 | 0x08 | 0x04);
        await Assert.That(System.Text.Encoding.ASCII.GetString(webp, 30, 4)).IsEqualTo("ICCP");
        await Assert.That(BitConverter.ToUInt32(webp, 4)).IsEqualTo((uint)(webp.Length - 8));
        using var got = FormatRegistry.Decode(webp, ImageFileFormat.WebP);
        await AssertCarried(src, got);
        // the pixels are untouched (lossless, no colour conversion)
        await Assert.That(got.GetPixelRow(5)[9 * 4] >> 8).IsEqualTo(src.GetPixelRow(5)[9 * 4] >> 8);
    }

    [Test]
    public async Task WebP_Lossy_CarriesIccExifXmp()
    {
        using var src = Tagged();
        using var ms = new MemoryStream();
        WebpCoder.Write(src, ms, new SharpImage.Formats.WebP.WebpLossyOptions { Quality = 80 });
        byte[] webp = ms.ToArray();
        await Assert.That((int)webp[20]).IsEqualTo(0x20 | 0x08 | 0x04);
        using var got = FormatRegistry.Decode(webp, ImageFileFormat.WebP);
        await AssertCarried(src, got);
    }

    [Test]
    public async Task WebP_Animation_CarriesFirstFrameIcc()
    {
        using var seq = new ImageSequence();
        var f0 = Tagged();
        var f1 = Gradient();
        f1.GetPixelRowForWrite(3)[6] = 1234;   // a different frame (identical ones are merged)
        f0.Delay = f1.Delay = 10;
        seq.AddFrame(f0);
        seq.AddFrame(f1);
        using var ms = new MemoryStream();
        WebpCoder.WriteSequence(seq, ms);
        byte[] webp = ms.ToArray();
        await Assert.That((int)webp[20] & 0x22).IsEqualTo(0x22);   // animation + ICC
        using var back = WebpCoder.ReadSequence(new MemoryStream(webp));
        await Assert.That(back.Count).IsEqualTo(2);
        await AssertCarried(f0, back[0]);
    }

    [Test]
    public async Task WebP_GrayProfile_BecomesRgbProfile()
    {
        // WebP stores RGB: a gray PQ profile becomes the RGB profile with the same curve and white (neutral axis).
        var gray = new ImageFrame();
        gray.Initialize(8, 8, ColorspaceType.Gray, false);
        for (int y = 0; y < 8; y++) gray.GetPixelRowForWrite(y).Fill((ushort)(y * 8000));
        gray.IccProfile = File.ReadAllBytes(Path.Combine(IccDir, "Gra_D65_Rel_PeQ.icc"));
        byte[] webp = FormatRegistry.Encode(gray, ImageFileFormat.WebP);
        using var got = FormatRegistry.Decode(webp, ImageFileFormat.WebP);
        await Assert.That(got.IccProfile).IsNotNull();
        await AssertNeutralEquivalent(gray.IccProfile, got.IccProfile!);
        gray.Dispose();
    }

    // ---- TIFF ----

    [Test]
    public async Task Tiff_CarriesIccExifXmp()
    {
        using var src = Tagged(alpha: true, orientation: 6);
        byte[] tiff = FormatRegistry.Encode(src, ImageFileFormat.Tiff);
        using var got = FormatRegistry.Decode(tiff, ImageFileFormat.Tiff);
        await AssertCarried(src, got);
        await Assert.That(got.Orientation).IsEqualTo(OrientationType.RightTop);
        await Assert.That(got.GetPixelRow(7)[3 * 4]).IsEqualTo(src.GetPixelRow(7)[3 * 4]);
    }

    [Test]
    public async Task Tiff_BigEndianExif_IsSwapped()
    {
        using var src = Gradient();
        src.Metadata.ExifProfile = Exif(littleEndian: false, orientation: 8);
        using var got = FormatRegistry.Decode(FormatRegistry.Encode(src, ImageFileFormat.Tiff), ImageFileFormat.Tiff);
        await Assert.That(ExifParser.OrientationOf(got.Metadata.ExifProfile)).IsEqualTo(OrientationType.LeftBottom);
        await Assert.That(got.Metadata.ExifProfile!.GetTag(0x9003)?.GetString()).IsEqualTo("2026:10:21 12:00:00");
    }

    [Test]
    public async Task Tiff_GrayAndGrayAlpha_RoundTrip()
    {
        foreach (bool alpha in new[] { false, true })
        {
            var gray = new ImageFrame();
            gray.Initialize(9, 5, ColorspaceType.Gray, alpha);
            int nc = gray.NumberOfChannels;
            for (int y = 0; y < 5; y++)
            {
                var row = gray.GetPixelRowForWrite(y);
                for (int x = 0; x < 9; x++)
                {
                    row[x * nc] = (ushort)((x * 7000) + y);
                    if (alpha) row[(x * nc) + 1] = (ushort)(60000 - (x * 5000));
                }
            }

            byte[] tiff = FormatRegistry.Encode(gray, ImageFileFormat.Tiff);
            using var got = FormatRegistry.Decode(tiff, ImageFileFormat.Tiff);
            await Assert.That(got.HasAlpha).IsEqualTo(alpha);
            ushort[] g = got.GetPixelRow(3).ToArray(), s = gray.GetPixelRow(3).ToArray();
            int gnc = got.NumberOfChannels;
            for (int x = 0; x < 9; x++)
            {
                await Assert.That(g[x * gnc]).IsEqualTo(s[x * nc]);
                await Assert.That(g[(x * gnc) + 2]).IsEqualTo(s[x * nc]);
                if (alpha) await Assert.That(g[(x * gnc) + 3]).IsEqualTo(s[(x * nc) + 1]);
            }

            gray.Dispose();
        }
    }

    [Test]
    public async Task GrayProfile_OnExpandedGray_BecomesRgbProfile()
    {
        // Readers expand gray to RGB; a gray profile on those samples is written as its RGB equivalent (PNG would
        // be invalid with a GRAY iCCP on RGB data).
        using var src = Gradient();
        src.IccProfile = File.ReadAllBytes(Path.Combine(IccDir, "Gra_D65_Rel_PeQ.icc"));
        using var got = FormatRegistry.Decode(FormatRegistry.Encode(src, ImageFileFormat.Png), ImageFileFormat.Png);
        await AssertNeutralEquivalent(src.IccProfile, got.IccProfile!);
        await Assert.That(got.GetPixelRow(4)[9 * 3]).IsEqualTo(src.GetPixelRow(4)[9 * 3]);
    }

    // ---- HEIC ----

    [Test]
    public async Task Heic_CarriesIccExifXmpAndOrientation()
    {
        using var src = Tagged(orientation: 6);
        src.Orientation = OrientationType.RightTop;
        using var got = FormatRegistry.Decode(FormatRegistry.Encode(src, ImageFileFormat.Heic), ImageFileFormat.Heic);
        await Assert.That(got.IccProfile!.AsSpan().SequenceEqual(src.IccProfile)).IsTrue();
        await Assert.That(got.Metadata.Xmp).IsEqualTo(XmpPacket);
        await Assert.That(got.Metadata.ExifProfile?.GetTag(ExifTag.Make)?.GetString()).IsEqualTo("TestCam");
        // irot: displayed rotated a quarter turn clockwise
        await Assert.That(got.Columns).IsEqualTo(src.Rows);
        await Assert.That(got.Rows).IsEqualTo(src.Columns);
    }

    // ---- PNG ----

    [Test]
    public async Task Png_CicpRoundTrips_WithProfile()
    {
        using var src = Gradient();
        src.Metadata.Cicp = new CicpInfo(9, 16, 0, true);   // BT.2100 PQ
        byte[] png = FormatRegistry.Encode(src, ImageFileFormat.Png);
        using var got = FormatRegistry.Decode(png, ImageFileFormat.Png);
        await Assert.That(got.Metadata.Cicp).IsEqualTo(src.Metadata.Cicp);
        // iCCP too (libjxl's profile for the encoding), for readers without cICP
        await Assert.That(got.IccProfile is { } icc && LjCms.FromIcc(icc) is { } e && LjCe.TfIsPq(e)).IsTrue();
    }

    [Test]
    public async Task Png_CarriesIccExifXmp()
    {
        using var src = Tagged(orientation: 3);
        using var got = FormatRegistry.Decode(FormatRegistry.Encode(src, ImageFileFormat.Png), ImageFileFormat.Png);
        await AssertCarried(src, got);
        await Assert.That(got.Orientation).IsEqualTo(OrientationType.BottomRight);
    }

    [Test]
    public async Task Jxl_CarriesIccExifXmp_OrientationInHeader()
    {
        using var src = Tagged(orientation: 6);
        src.Orientation = OrientationType.RightTop;
        foreach (bool lossless in new[] { true, false })
        {
            byte[] jxl = lossless ? JxlCoder.Encode(src) : JxlCoder.EncodeLossy(src, 90, effort: 3);
            // a container: Exif and xml boxes (Brotli-compressed, as cjxl writes them) before the codestream
            await Assert.That(jxl.AsSpan(0, 12).SequenceEqual(new byte[] { 0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10 })).IsTrue();
            using var got = JxlCoder.Decode(jxl);
            if (lossless)
            {
                await AssertCarried(src, got);
            }
            else
            {
                // lossy (XYB): the P3 profile is coded as the enumerated encoding (libjxl DecideIfWantICC)
                await Assert.That(ColorDescription.Resolve(got).Cicp?.ColorPrimaries).IsEqualTo(12);
                await Assert.That(got.Metadata.Xmp).IsEqualTo(XmpPacket);
                await Assert.That(got.Metadata.ExifProfile?.GetTag(ExifTag.Make)?.GetString()).IsEqualTo("TestCam");
            }

            // the decoder applies the header orientation; the Exif tag is reset so it is not applied twice
            await Assert.That(got.Columns).IsEqualTo(src.Rows);
            await Assert.That(ExifParser.OrientationOf(got.Metadata.ExifProfile)).IsEqualTo(OrientationType.TopLeft);
        }
    }

    // ---- formats without colour description ----

    [Test]
    public async Task Bmp_And_Gif_GetSrgbSamples()
    {
        using var src = Gradient();
        src.IccProfile = File.ReadAllBytes(Path.Combine(IccDir, "Adobe98.icc"));
        using ImageFrame expected = ColorDescription.ToSrgb(src)!;
        await Assert.That(expected).IsNotNull();
        foreach (var format in new[] { ImageFileFormat.Bmp, ImageFileFormat.Gif })
        {
            using var got = FormatRegistry.Decode(FormatRegistry.Encode(src, format), format);
            await Assert.That(got.IccProfile).IsNull();
            int gnc = got.NumberOfChannels, enc = expected.NumberOfChannels;
            int maxDiff = 0, moved = 0;
            for (int y = 0; y < (int)src.Rows; y++)
            {
                var g = got.GetPixelRow(y);
                var e = expected.GetPixelRow(y);
                var s = src.GetPixelRow(y);
                for (int x = 0; x < (int)src.Columns; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        maxDiff = Math.Max(maxDiff, Math.Abs((g[(x * gnc) + c] >> 8) - (e[(x * enc) + c] >> 8)));
                        moved = Math.Max(moved, Math.Abs((s[(x * 3) + c] >> 8) - (e[(x * enc) + c] >> 8)));
                    }
                }
            }

            await Assert.That(moved).IsGreaterThan(8);   // Adobe RGB really differs from sRGB here
            await Assert.That(maxDiff).IsLessThanOrEqualTo(format == ImageFileFormat.Gif ? 24 : 1);
        }
    }

    [Test]
    public async Task Cmyk_WithProfile_RendersThroughCms()
    {
        string cmykJxl = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "cms", "cmyk_layers.jxl");
        byte[]? cmykIcc = LjDecoder.Decode(JxlCoder.FindCodestream(File.ReadAllBytes(cmykJxl)), 1).Icc;
        await Assert.That(cmykIcc).IsNotNull();

        var cmyk = new ImageFrame();
        cmyk.Initialize(4, 1, ColorspaceType.CMYK, false);
        ushort[] inks = [0, 0, 0, 0, 65535, 0, 0, 0, 0, 30000, 50000, 0, 10000, 10000, 10000, 40000];
        inks.CopyTo(cmyk.GetPixelRowForWrite(0));
        cmyk.IccProfile = cmykIcc;
        using var got = FormatRegistry.Decode(FormatRegistry.Encode(cmyk, ImageFileFormat.Png), ImageFileFormat.Png);

        LjCms.Transform t = LjCms.Transform.Create(LjCms.FromIcc(cmykIcc!)!, LjCe.Srgb(false), 255f)!;
        float[] input = inks.Select(v => 1f - (v / 65535f)).ToArray();
        float[] output = new float[12];
        t.Run(input, output, 4);
        ushort[] row = got.GetPixelRow(0).ToArray();
        int nc = got.NumberOfChannels;
        bool differsFromNaive = false;
        for (int x = 0; x < 4; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                int want = (int)MathF.Round(Math.Clamp(output[(x * 3) + c], 0f, 1f) * 255f);
                await Assert.That(Math.Abs((row[(x * nc) + c] >> 8) - want)).IsLessThanOrEqualTo(1);
                int naive = (int)((65535L - inks[(x * 4) + c]) * (65535L - inks[(x * 4) + 3]) / 65535 >> 8);
                differsFromNaive |= Math.Abs(naive - want) > 8;
            }
        }

        await Assert.That(differsFromNaive).IsTrue();
        cmyk.Dispose();
    }

    // ---- transforms ----

    [Test]
    public async Task DecodedExif_AutoOrient_WritesResetOrientation()
    {
        // A decoded EXIF block keeps its raw bytes for exact round trips; resetting the orientation must reach them,
        // or the next writer stores the old tag and viewers rotate the upright pixels again.
        using var src = Tagged(orientation: 6);
        src.Orientation = OrientationType.RightTop;
        using var jpg = FormatRegistry.Decode(FormatRegistry.Encode(src, ImageFileFormat.Jpeg), ImageFileFormat.Jpeg);
        await Assert.That(jpg.Metadata.ExifProfile!.RawData).IsNotNull();
        using var upright = SharpImage.Transform.Geometry.AutoOrient(jpg);
        foreach (var format in new[] { ImageFileFormat.Png, ImageFileFormat.Jpeg, ImageFileFormat.WebP, ImageFileFormat.Tiff })
        {
            using var got = FormatRegistry.Decode(FormatRegistry.Encode(upright, format), format);
            await Assert.That(ExifParser.OrientationOf(got.Metadata.ExifProfile)).IsEqualTo(OrientationType.TopLeft);
            await Assert.That(got.Metadata.ExifProfile!.GetTag(0x9003)?.GetString()).IsEqualTo("2026:10:21 12:00:00");
            await Assert.That(got.Columns).IsEqualTo(src.Rows);
        }
    }


    [Test]
    public async Task Resize_And_AutoOrient_KeepMetadata()
    {
        using var src = Tagged(orientation: 6);
        src.Orientation = OrientationType.RightTop;
        using var resized = SharpImage.Transform.Resize.Apply(src, 12, 8);
        await AssertCarried(src, resized);
        await Assert.That(ExifParser.OrientationOf(resized.Metadata.ExifProfile)).IsEqualTo(OrientationType.RightTop);

        using var oriented = SharpImage.Transform.Geometry.AutoOrient(src);
        await AssertCarried(src, oriented);
        await Assert.That(oriented.Columns).IsEqualTo(src.Rows);
        await Assert.That(ExifParser.OrientationOf(oriented.Metadata.ExifProfile)).IsEqualTo(OrientationType.TopLeft);
    }
}
