using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

/// <summary>
/// HEIC output byte-identical to libheif 1.23.6's heif-enc with x265 (TestAssets/heic_heifenc: <c>heif-enc in -q Q -o
/// out.heic</c>, x265 4.2+59 C build, identical to the x265 in pillow_heif 1.8.1): PNG input through the RGB -> YCbCr
/// pixel path (gray, alpha, ICC, odd and small sizes with clap; 16-bit PNGs at 10 bits, against heif-enc linked with
/// x265's HIGH_BIT_DEPTH build), JPEG input through the raw YCbCr planes (Exif
/// orientation as irot/imir, ICC, Exif + XMP items; 4:4:4 / 4:2:2 through libheif's conversions, other samplings
/// through libjpeg's full-size output, CMYK as heif-enc converts it).
/// </summary>
public sealed class HeicHeifEncParityTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "heic_heifenc", name);

    [Test]
    [Arguments("rgb_odd.png", 50)]
    [Arguments("rgb_tiny.png", 50)]
    [Arguments("rgba.png", 50)]
    [Arguments("rgba.png", 90)]
    [Arguments("gray.png", 50)]
    [Arguments("graya.png", 50)]
    [Arguments("rgb_icc.png", 50)]
    [Arguments("photo640.png", 75)]
    [Arguments("rgb16_odd.png", 50)]
    [Arguments("rgba16.png", 50)]
    [Arguments("grayd16_odd.png", 50)]
    public async Task Png_MatchesHeifEnc(string input, int quality)
    {
        using var image = FormatRegistry.Read(Asset(input));
        byte[] heic = HeifCoder.EncodeHeic(image, new HeicEncodeOptions { Quality = quality });
        byte[] reference = File.ReadAllBytes(Asset($"{Path.GetFileNameWithoutExtension(input)}_q{quality}.heic"));
        await Assert.That(heic.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    [Arguments("o5.jpg", 50)]
    [Arguments("o8.jpg", 50)]
    [Arguments("xmp.jpg", 50)]
    [Arguments("xmp.jpg", 90)]
    [Arguments("gray_jpeg.jpg", 50)]
    [Arguments("icc.jpg", 50)]
    [Arguments("s444.jpg", 50)]
    [Arguments("s422.jpg", 50)]
    [Arguments("s440.jpg", 50)]
    [Arguments("s411.jpg", 50)]
    [Arguments("smix.jpg", 50)]
    [Arguments("cmyk.jpg", 50)]
    public async Task Jpeg_MatchesHeifEnc(string input, int quality)
    {
        byte[] heic = HeifCoder.EncodeHeicFromJpeg(File.ReadAllBytes(Asset(input)), new HeicEncodeOptions { Quality = quality });
        byte[] reference = File.ReadAllBytes(Asset($"{Path.GetFileNameWithoutExtension(input)}_q{quality}.heic"));
        await Assert.That(heic.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    // Decoding as heif-dec does (libheif 1.23.6 + libde265; SHA-256 of its PNG's samples at 16 bits): libheif's
    // colour conversions (8-bit fixed point, deeper float, 4:0:0 copied), its 16-bit scaling, alpha, irot, and a
    // picture the SPS holds back for reordering (libheif_example.heic), a 512x512 grid (tiles decoded in parallel).
    [Test]
    [Arguments("rgba_q50.heic", "0f0ff5966b6fe80ee68f8c8239d06ce0b7e88da1491dff301c912aca991cd065")]
    [Arguments("gray_q50.heic", "c0cec6846e9138b8c74f543d409ad9e6795a0ffd0b6a2f8af436a832700bc379")]
    [Arguments("graya_q50.heic", "dec6524c6f69afae95617439c259c352d64cc76f5049153e870da43e52da3af7")]
    [Arguments("rgb_odd_q50.heic", "71657224984e6e5ca495ed0d4186d0a0129ee429b114ccb64ffbec1f312852a6")]
    [Arguments("rgb16_odd_q50.heic", "f038ef100c2c3475ce1fdffa38e269eac86de620a23cb1ef366dbb3d5a6cd0fb")]
    [Arguments("rgba16_q50.heic", "dbce35416e4a3610fc28b07f92837c4fbeca34ba86ac5c57534a4f44c172bb3e")]
    [Arguments("grayd16_odd_q50.heic", "9db51eb4919b20cc89c6dcba6acebd3785af00ab0c78cad64c1f7b7b3a1061a0")]
    [Arguments("xmp_q50.heic", "6c62a72a3bcf1e6763f22b448fac0792626e8855d1e1230c81bd3c9af32835ff")]
    [Arguments("libheif_example.heic", "7f8009bb1558f4a35e6134318f7a17aa9a1c214706790ed0b973065925923d7a")]
    [Arguments("grid512_q50.heic", "55e7a097ff20ac9c5361c91d627ee5d7deb579c6835983c1583cffba00b295da")]
    public async Task Decode_MatchesHeifDec(string file, string sha256)
    {
        using var image = FormatRegistry.Read(Asset(file));
        int w = (int)image.Columns, h = (int)image.Rows;
        var samples = new byte[w * h * image.NumberOfChannels * 2];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            foreach (ushort v in image.GetPixelRow(y))
            {
                samples[o++] = (byte)v;
                samples[o++] = (byte)(v >> 8);
            }
        }

        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(samples)).ToLowerInvariant();
        await Assert.That(hash).IsEqualTo(sha256);
    }

    // heif-enc reads a 16-bit gray + alpha PNG as RGBA (4 samples per pixel from 2-sample rows: garbage); SharpImage codes
    // it as it codes 8-bit gray + alpha and 16-bit gray: a 10-bit monochrome item with a 10-bit alpha item.
    [Test]
    public async Task DeepGrayAlpha_IsTenBitMonochromeWithAlpha()
    {
        const int w = 96, h = 70;
        using var src = new SharpImage.Image.ImageFrame();
        src.Initialize(w, h, SharpImage.Core.ColorspaceType.Gray, true);
        int ch = src.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < ch - 1; c++) row[(x * ch) + c] = (ushort)((x * 600) + (y * 37) + 5);
                row[(x * ch) + ch - 1] = (ushort)(x < w / 2 ? 0 : 65535);
            }
        }

        byte[] heic = HeifCoder.EncodeHeic(src);
        int pixi = heic.AsSpan().IndexOf("pixi"u8);
        await Assert.That(heic[pixi + 8]).IsEqualTo((byte)1);    // one channel
        await Assert.That(heic[pixi + 9]).IsEqualTo((byte)10);   // of 10 bits
        await Assert.That(heic.AsSpan().IndexOf("auxC"u8) > 0).IsTrue();

        using var back = FormatRegistry.Read(heic);
        await Assert.That(back.HasAlpha).IsTrue();
        int bc = back.NumberOfChannels, wrong = 0;
        for (int y = 0; y < h; y++)
        {
            var row = back.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int a = row[(x * bc) + bc - 1] >> 8;
                if ((x < (w / 2) - 1 && a > 8) || (x > w / 2 && a < 247)) wrong++;
                if (Math.Abs(row[x * bc] - (int)src.GetPixelRow(y)[x * ch]) > 2048) wrong++;
            }
        }

        await Assert.That(wrong).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultQualityIsHeifEncDefault()
    {
        using var image = FormatRegistry.Read(Asset("rgba.png"));
        byte[] heic = FormatRegistry.Encode(image, ImageFileFormat.Heic);
        await Assert.That(heic.AsSpan().SequenceEqual(File.ReadAllBytes(Asset("rgba_q50.heic")))).IsTrue();
    }
}
