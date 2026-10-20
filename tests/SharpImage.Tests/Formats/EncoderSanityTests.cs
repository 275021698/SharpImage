using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Conversion defects found by decoding SharpImage's output with the reference decoders (libpng, libwebp, libjxl,
/// libavif, libheif, giflib, Pillow): CMYK frames written as a colour negative, gray / gray + alpha frames crashing
/// the WebP writer, BMP dropping alpha, PNG dropping 16-bit precision and the GIF writer's first-256-colours palette.
/// </summary>
public sealed class EncoderSanityTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "sanity");

    private static ImageFrame Gradient(int w, int h, ColorspaceType cs, bool alpha, bool deep)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, cs, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    int v = alpha && c == ch - 1 ? x * 65535 / (w - 1)
                        : c == 0 ? x * 65535 / (w - 1) : c == 1 ? y * 65535 / (h - 1) : (x + y) * 65535 / (w + h - 2);
                    row[x * ch + c] = (ushort)(deep ? v : (v >> 8) * 257);
                }
        }
        return f;
    }

    private static double Psnr(ImageFrame a, ImageFrame b)
    {
        double se = 0;
        long n = 0;
        int ac = a.NumberOfChannels, bc = b.NumberOfChannels;
        for (int y = 0; y < a.Rows; y++)
        {
            var ra = a.GetPixelRow(y);
            var rb = b.GetPixelRow(y);
            for (int x = 0; x < a.Columns; x++)
                for (int c = 0; c < 3; c++)
                {
                    double d = (ra[x * ac + Math.Min(c, ac - 1 - (a.HasAlpha ? 1 : 0))] >> 8) - (rb[x * bc + Math.Min(c, bc - 1 - (b.HasAlpha ? 1 : 0))] >> 8);
                    se += d * d;
                    n++;
                }
        }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    [Test]
    [Arguments(ImageFileFormat.Png)]
    [Arguments(ImageFileFormat.WebP)]
    [Arguments(ImageFileFormat.Tiff)]
    [Arguments(ImageFileFormat.Bmp)]
    [Arguments(ImageFileFormat.Avif)]
    [Arguments(ImageFileFormat.Heic)]
    public async Task CmykJpeg_ConvertsToPositiveRgb(ImageFileFormat format)
    {
        // an Adobe (inverted) CMYK JPEG of a gradient whose top-left pixel is black
        using var cmyk = FormatRegistry.Read(Path.Combine(Dir, "cmyk_adobe.jpg"));
        await Assert.That(cmyk.Colorspace).IsEqualTo(ColorspaceType.CMYK);
        using var back = FormatRegistry.Read(FormatRegistry.Encode(cmyk, format));
        int r = back.GetPixelRow(0)[0] >> 8, g = back.GetPixelRow(0)[1] >> 8;
        await Assert.That(r).IsLessThan(40);   // black, not white
        await Assert.That(g).IsLessThan(40);
    }

    [Test]
    public async Task CmykJpeg_ToAvifDirectPath_IsRefusedLikeAvifenc()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Dir, "cmyk_adobe.jpg"));
        await Assert.That(() => HeifCoder.EncodeAvifFromJpeg(jpeg)).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WebP_GrayAndGrayAlphaFrames_Encode(bool alpha)
    {
        using var f = Gradient(57, 33, ColorspaceType.Gray, alpha, false);
        using var back = FormatRegistry.Read(FormatRegistry.Encode(f, ImageFileFormat.WebP));
        await Assert.That(Psnr(f, back)).IsEqualTo(99.0);
        await Assert.That(back.HasAlpha).IsEqualTo(alpha);
    }

    [Test]
    public async Task Bmp_KeepsAlpha()
    {
        using var f = Gradient(40, 20, ColorspaceType.SRGB, true, false);
        using var back = FormatRegistry.Read(FormatRegistry.Encode(f, ImageFileFormat.Bmp));
        await Assert.That(back.HasAlpha).IsTrue();
        int left = back.GetPixelRow(5)[3] >> 8, right = back.GetPixelRow(5)[39 * back.NumberOfChannels + 3] >> 8;
        await Assert.That(left).IsEqualTo(0);
        await Assert.That(right).IsEqualTo(255);
    }

    [Test]
    public async Task Png_KeepsSixteenBits()
    {
        using var f = Gradient(300, 7, ColorspaceType.SRGB, true, true);
        using var back = FormatRegistry.Read(FormatRegistry.Encode(f, ImageFileFormat.Png));
        int diff = 0;
        for (int y = 0; y < 7; y++)
        {
            var a = f.GetPixelRow(y);
            var b = back.GetPixelRow(y);
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
        }
        await Assert.That(diff).IsEqualTo(0);
    }

    [Test]
    public async Task Gif_ManyColours_GetARealPalette()
    {
        // a smooth 2-D gradient has thousands of colours; the old writer kept the first 256 (one row's worth)
        using var f = Gradient(160, 120, ColorspaceType.SRGB, false, false);
        using var back = FormatRegistry.Read(FormatRegistry.Encode(f, ImageFileFormat.Gif));
        await Assert.That(Psnr(f, back)).IsGreaterThan(30.0);
    }
}
