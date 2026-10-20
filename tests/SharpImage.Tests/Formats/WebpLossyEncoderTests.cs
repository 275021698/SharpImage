using SharpImage.Formats;
using SharpImage.Formats.WebP;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The WebP lossy (VP8 + ALPH) encoder is a line-by-line port of libwebp 1.6.0: the golden files are libwebp's
/// WebPEncode of the same RGBA (written by the _refs/webptwin harness) and must match byte for byte — every method,
/// the alpha options, the RD / trellis levels, autofilter, simple filter, token partitions and the PSNR search.
/// </summary>
public sealed class WebpLossyEncoderTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "webp_golden");

    private static (uint[] Argb, int W, int H) LoadArgb(string png)
    {
        using var img = FormatRegistry.Read(Path.Combine(Dir, png));
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
        return (argb, w, h);
    }

    // the options _refs/webpgolden.py gave libwebp for each tag
    private static WebpLossyOptions Options(string tag) => tag switch
    {
        "ly" => new(),
        "ly_m0" => new() { Method = 0 },
        "ly_m2" => new() { Quality = 50, Method = 2 },
        "ly_m3" => new() { Method = 3 },
        "ly_m5" => new() { Quality = 80, Method = 5 },
        "ly_m6" => new()
        {
            Quality = 90, Method = 6, Exact = true, AlphaQuality = 50, AlphaFiltering = 2, AlphaCompression = 1,
            SnsStrength = 80, FilterStrength = 20, Segments = 1, Pass = 3, Preprocessing = 1,
        },
        "ly_q10" => new()
        {
            Quality = 10, AlphaQuality = 30, AlphaFiltering = 0, AlphaCompression = 0, SnsStrength = 0,
            FilterStrength = 0, Segments = 2, Pass = 2, Preprocessing = 3,
        },
        "ly_af" => new() { Autofilter = true },
        "ly_simple" => new() { Quality = 70, FilterType = 0, FilterSharpness = 3 },
        "ly_parts" => new() { Method = 2, Partitions = 2 },
        "ly_psnr" => new() { Pass = 4, TargetPsnr = 42 },
        _ => throw new ArgumentException(tag),
    };

    public static IEnumerable<(string Name, string Tag)> Cases()
    {
        string[] names = ["pal2", "pal16", "pal200", "transparent", "gradient_alpha", "tiny3x2", "thin", "noise", "crop_odd"];
        string[] tags = ["ly", "ly_m0", "ly_m2", "ly_m3", "ly_m5", "ly_m6", "ly_q10", "ly_af", "ly_simple", "ly_parts", "ly_psnr"];
        foreach (var n in names)
            foreach (var t in tags)
                yield return (n, t);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Encode_MatchesLibwebp_ByteForByte(string name, string tag)
    {
        var (argb, w, h) = LoadArgb(name + ".png");
        byte[] ours = Vp8Encoder.Encode(argb, w, h, Options(tag));
        byte[] reference = File.ReadAllBytes(Path.Combine(Dir, $"{name}_{tag}.webp"));
        await Assert.That(ours.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task WebpCoderWrite_Lossy_IsTheLibwebpFile()
    {
        using var img = FormatRegistry.Read(Path.Combine(Dir, "transparent.png"));
        using var ms = new MemoryStream();
        WebpCoder.Write(img, ms, 75);
        byte[] reference = File.ReadAllBytes(Path.Combine(Dir, "transparent_ly.webp"));
        await Assert.That(ms.ToArray().AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task WebpCoderWrite_DefaultIsLossless_LikeCwebpLossless()
    {
        using var img = FormatRegistry.Read(Path.Combine(Dir, "gradient_alpha.png"));
        using var ms = new MemoryStream();
        WebpCoder.Write(img, ms);
        byte[] reference = File.ReadAllBytes(Path.Combine(Dir, "gradient_alpha_ll.webp"));
        await Assert.That(ms.ToArray().AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task Lossy_DecodesCloseToTheSource()
    {
        // what a viewer shows: our own decoder on our own lossy file, against the source pixels
        var (argb, w, h) = LoadArgb("gradient_alpha.png");
        byte[] webp = Vp8Encoder.Encode((uint[])argb.Clone(), w, h, new WebpLossyOptions { Quality = 90 });
        using var back = FormatRegistry.Read(webp);
        double se = 0;
        int alphaBad = 0;
        for (int y = 0; y < h; y++)
        {
            var row = back.GetPixelRow(y);
            int ch = back.NumberOfChannels;
            for (int x = 0; x < w; x++)
            {
                uint p = argb[y * w + x];
                for (int c = 0; c < 3; c++)
                {
                    double d = (row[x * ch + c] >> 8) - (int)((p >> (16 - 8 * c)) & 0xff);
                    se += d * d;
                }
                if (row[x * ch + 3] >> 8 != p >> 24) alphaBad++;
            }
        }
        double psnr = 10 * Math.Log10(255.0 * 255.0 / (se / (3.0 * w * h)));
        await Assert.That(psnr).IsGreaterThan(30);
        await Assert.That(alphaBad).IsEqualTo(0);   // alpha_quality 100: the ALPH plane is lossless
    }

    [Test]
    public async Task Sequence_LossyFramesCarryAlpha_AndRoundTrip()
    {
        var seq = WebpCoder.ReadSequence(new MemoryStream(File.ReadAllBytes(Path.Combine(Dir, "anim_lossy_alpha.webp"))));
        using var ms = new MemoryStream();
        WebpCoder.WriteSequence(seq, ms, 100);
        var back = WebpCoder.ReadSequence(new MemoryStream(ms.ToArray()));
        await Assert.That(back.Count).IsEqualTo(seq.Count);
        for (int f = 0; f < seq.Count; f++)
        {
            int w = (int)seq[f].Columns, h = (int)seq[f].Rows, bad = 0;
            for (int y = 0; y < h; y++)
            {
                var a = seq[f].GetPixelRow(y);
                var b = back[f].GetPixelRow(y);
                for (int x = 0; x < w * 4; x++)
                {
                    // fully transparent pixels may lose their RGB (cwebp's default, without -exact)
                    bool transparent = a[(x / 4) * 4 + 3] == 0;
                    if (!transparent && a[x] != b[x]) bad++;
                    if (transparent && b[(x / 4) * 4 + 3] != 0) bad++;
                }
            }
            await Assert.That(bad).IsEqualTo(0);
        }
    }
}
