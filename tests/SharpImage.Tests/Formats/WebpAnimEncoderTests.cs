using SharpImage.Formats;
using SharpImage.Formats.WebP;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The animated WebP encoder is a port of libwebp 1.6.0's WebPAnimEncoder (anim_encode.c + mux assembly): the golden
/// files are libwebp's output for the same frames and options (_refs/webpanimtwin) and must match byte for byte —
/// sub-frame rectangles, dispose / blend choices, key-frames, skipped duplicate frames, mixed lossy / lossless and the
/// single-frame optimisation.
/// </summary>
public sealed class WebpAnimEncoderTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "webp_golden");

    private static readonly Dictionary<string, (int W, int H, int[] Durations)> Animations = new()
    {
        ["sprite"] = (96, 64, [80, 80, 80, 80, 80, 80, 80, 80]),
        ["transp"] = (80, 60, [60, 60, 120, 60, 60, 60, 200]),
        ["dups"] = (33, 21, [100, 50, 50, 100, 300]),
        ["single"] = (64, 48, [100]),
    };

    // the options _refs/webpanimgolden.py gave libwebp for each tag
    private static WebpAnimationOptions Options(string tag) => tag switch
    {
        "ll" => new(),
        "ly" => new() { Lossless = false },
        "llk" => new() { Kmin = 9, Kmax = 17 },
        "lyk" => new() { Lossless = false, Quality = 50, Kmin = 3, Kmax = 5 },
        "mix" => new() { Lossless = false, Kmin = 0, Kmax = 0, AllowMixed = true },
        "min" => new() { Quality = 50, Method = 2, Kmin = 0, Kmax = 0, MinimizeSize = true, LoopCount = 3, BackgroundColor = 0xff102030 },
        _ => throw new ArgumentException(tag),
    };

    public static IEnumerable<(string Name, string Tag)> Cases()
    {
        foreach (var n in new[] { "sprite", "transp", "dups", "single" })
            foreach (var t in new[] { "ll", "ly", "llk", "lyk", "mix", "min" })
                yield return (n, t);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Encode_MatchesLibwebp_ByteForByte(string name, string tag)
    {
        var (w, h, durations) = Animations[name];
        using var strip = FormatRegistry.Read(Path.Combine(Dir, name + ".anim.png"));
        int ch = strip.NumberOfChannels;
        var enc = new WebpAnimEncoder(w, h, Options(tag));
        int timestamp = 0;
        for (int f = 0; f < durations.Length; f++)
        {
            var argb = new uint[w * h];
            for (int y = 0; y < h; y++)
            {
                var row = strip.GetPixelRow(f * h + y);
                for (int x = 0; x < w; x++)
                {
                    uint r = (uint)(row[x * ch] >> 8), g = (uint)(row[x * ch + 1] >> 8), b = (uint)(row[x * ch + 2] >> 8);
                    uint a = strip.HasAlpha ? (uint)(row[x * ch + ch - 1] >> 8) : 255u;
                    argb[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            enc.Add(argb, timestamp);
            timestamp += durations[f];
        }
        enc.Add(null, timestamp);
        byte[] ours = enc.Assemble();
        byte[] reference = File.ReadAllBytes(Path.Combine(Dir, $"{name}_{tag}.anim.webp"));
        await Assert.That(ours.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task WriteSequence_RoundTripsThroughTheDecoder()
    {
        // what the animated file shows: every composited frame equals the source frame (lossless)
        var src = WebpCoder.ReadSequence(new MemoryStream(File.ReadAllBytes(Path.Combine(Dir, "anim_blend.webp"))));
        using var ms = new MemoryStream();
        WebpCoder.WriteSequence(src, ms);
        var back = WebpCoder.ReadSequence(new MemoryStream(ms.ToArray()));
        await Assert.That(back.Count).IsEqualTo(src.Count);
        for (int f = 0; f < src.Count; f++)
        {
            int w = (int)src[f].Columns, h = (int)src[f].Rows, bad = 0;
            for (int y = 0; y < h; y++)
            {
                var a = src[f].GetPixelRow(y);
                var b = back[f].GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    // fully transparent pixels may lose their RGB (cwebp's default, without -exact)
                    if (a[x * 4 + 3] == 0) { if (b[x * 4 + 3] != 0) bad++; continue; }
                    for (int c = 0; c < 4; c++) if (a[x * 4 + c] != b[x * 4 + c]) bad++;
                }
            }
            await Assert.That(bad).IsEqualTo(0);
        }
    }
}
