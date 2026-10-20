using SharpImage.Formats;
using SharpImage.Formats.WebP;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The WebP decoder against libwebp 1.6.0: VP8L (libwebp-encoded golden files, packed palettes, tiny and thin
/// images), lossy VP8 with an ALPH plane (dwebp's decode) and animations composited like WebPAnimDecoder (every
/// blend / dispose combination).
/// </summary>
public sealed class WebpDecoderTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "webp_golden");

    private static uint[] ToArgb(ImageFrame img)
    {
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        var argb = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                uint r = (uint)(row[x * ch] >> 8), g = (uint)(row[x * ch + 1] >> 8), b = (uint)(row[x * ch + 2] >> 8);
                uint a = img.HasAlpha ? (uint)(row[x * ch + ch - 1] >> 8) : 255u;
                argb[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
        return argb;
    }

    [Test]
    [Arguments("pal2")]
    [Arguments("pal16")]
    [Arguments("pal200")]
    [Arguments("transparent")]
    [Arguments("gradient_alpha")]
    [Arguments("tiny3x2")]
    [Arguments("thin")]
    [Arguments("noise")]
    public async Task Lossless_LibwebpFiles_DecodeToTheSourcePixels(string name)
    {
        // exact (transparent RGB kept) encodes decode to exactly the source; without -exact libwebp is free to
        // rewrite the RGB of fully transparent pixels, so those compare by alpha only
        using var src = FormatRegistry.Read(Path.Combine(Dir, name + ".png"));
        var expected = ToArgb(src);
        foreach (var tag in new[] { "ll_exact", "ll", "ll_m0", "ll_m6" })
        {
            using var dec = FormatRegistry.Read(File.ReadAllBytes(Path.Combine(Dir, $"{name}_{tag}.webp")));
            var got = ToArgb(dec);
            int bad = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                bool transparent = (expected[i] >> 24) == 0;
                if (tag == "ll_exact" || !transparent ? got[i] != expected[i] : (got[i] >> 24) != 0) bad++;
            }
            await Assert.That(bad).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Lossless_OurEncoder_RoundTrips()
    {
        foreach (var name in new[] { "pal16", "transparent", "gradient_alpha", "noise", "thin" })
        {
            using var src = FormatRegistry.Read(Path.Combine(Dir, name + ".png"));
            var argb = ToArgb(src);
            var original = (uint[])argb.Clone();
            byte[] webp = Vp8lEncoder.EncodeImage(argb, (int)src.Columns, (int)src.Rows, new WebpLosslessOptions { Exact = true });
            using var back = FormatRegistry.Read(webp);
            await Assert.That(ToArgb(back).AsSpan().SequenceEqual(original)).IsTrue();
        }
    }

    [Test]
    public async Task LossyWithAlpha_MatchesDwebp()
    {
        using var dec = FormatRegistry.Read(File.ReadAllBytes(Path.Combine(Dir, "lossy_alpha.webp")));
        using var reference = FormatRegistry.Read(Path.Combine(Dir, "lossy_alpha.ref.png"));
        await Assert.That(ToArgb(dec).AsSpan().SequenceEqual(ToArgb(reference))).IsTrue();
    }

    [Test]
    [Arguments("anim_blend")]
    [Arguments("anim_lossy_alpha")]
    public async Task Animation_FramesMatchWebPAnimDecoder(string name)
    {
        var seq = WebpCoder.ReadSequence(new MemoryStream(File.ReadAllBytes(Path.Combine(Dir, name + ".webp"))));
        using var strip = FormatRegistry.Read(Path.Combine(Dir, name + ".frames.png"));
        var expected = ToArgb(strip);
        int w = seq.CanvasWidth, h = seq.CanvasHeight;
        await Assert.That(seq.Count * h).IsEqualTo((int)strip.Rows);
        for (int f = 0; f < seq.Count; f++)
        {
            var got = ToArgb(seq[f]);
            await Assert.That(got.AsSpan().SequenceEqual(expected.AsSpan(f * w * h, w * h))).IsTrue();
        }
        // a single-image read of an animated file is its first composited frame
        using var first = FormatRegistry.Read(File.ReadAllBytes(Path.Combine(Dir, name + ".webp")));
        await Assert.That(ToArgb(first).AsSpan().SequenceEqual(expected.AsSpan(0, w * h))).IsTrue();
    }
}
