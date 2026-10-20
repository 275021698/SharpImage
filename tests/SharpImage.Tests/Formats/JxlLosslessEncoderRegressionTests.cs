using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Lossless JPEG XL encodes that libjxl rejected (found by fuzzing against libjxl 0.12): a single-leaf tree with ANS
/// coding wrote a context map the bitstream does not have, 16-bit tree split values above 2^15 were written as raw
/// prefix symbols, and gray + alpha images crashed. SharpImage's decoder rejects the same streams libjxl does, so it
/// checks the round trip here.
/// </summary>
public sealed class JxlLosslessEncoderRegressionTests
{
    private static ImageFrame LoadRaw(string name)
    {
        using var br = new BinaryReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", name)));
        int w = br.ReadInt32(), h = br.ReadInt32(), ch = br.ReadInt32();
        bool alpha = br.ReadBoolean();
        var f = new ImageFrame();
        f.Initialize(w, h, ColorspaceType.SRGB, alpha);
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < row.Length; x++) row[x] = br.ReadUInt16();
        }
        return f;
    }

    private static async Task AssertRoundTrip(ImageFrame src, JxlLosslessEffort effort)
    {
        byte[] jxl = JxlCoder.Encode(src, effort);
        using var back = FormatRegistry.Read(jxl);
        int w = (int)src.Columns, h = (int)src.Rows, sc = src.NumberOfChannels, bc = back.NumberOfChannels;
        int colors = sc - (src.HasAlpha ? 1 : 0);
        int bad = 0;
        for (int y = 0; y < h; y++)
        {
            var a = src.GetPixelRow(y);
            var b = back.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    ushort sv = a[x * sc + (colors == 1 ? 0 : c)];
                    ushort bv = b[x * bc + (bc - (back.HasAlpha ? 1 : 0) == 1 ? 0 : c)];
                    if (sv != bv) bad++;
                }
                if (src.HasAlpha && a[x * sc + sc - 1] != b[x * bc + bc - 1]) bad++;
            }
        }
        await Assert.That(bad).IsEqualTo(0);
    }

    [Test]
    [Arguments(JxlLosslessEffort.Turbo)]
    [Arguments(JxlLosslessEffort.Fast)]
    [Arguments(JxlLosslessEffort.Balanced)]
    [Arguments(JxlLosslessEffort.Maximum)]
    public async Task SingleLeafTreeWithAns_RoundTrips(JxlLosslessEffort effort)
    {
        using var f = LoadRaw("singleleaf_ans_100x74.raw");
        await AssertRoundTrip(f, effort);
    }

    [Test]
    public async Task RandomImages_AllChannelLayoutsAndDepths_RoundTrip()
    {
        var rng = new Random(11);
        for (int i = 0; i < 24; i++)
        {
            int w = rng.Next(1, 90), h = rng.Next(1, 70);
            bool gray = i % 3 == 0, alpha = i % 2 == 0, deep = i % 4 < 2;
            using var f = new ImageFrame();
            f.Initialize(w, h, gray ? ColorspaceType.Gray : ColorspaceType.SRGB, alpha);
            int ch = f.NumberOfChannels;
            for (int y = 0; y < h; y++)
            {
                var row = f.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        int v = (i % 5) switch
                        {
                            0 => rng.Next(65536),
                            1 => (x * 65535 / Math.Max(1, w - 1) + c * 9000) & 0xffff,
                            2 => (int)(32768 + 30000 * Math.Sin(x * 0.07 + c) * Math.Cos(y * 0.05)),
                            3 => ((x ^ y) * 997 * (c + 1)) & 0xffff,
                            _ => 40000,
                        };
                        if (!deep) v = (v >> 8) * 257;
                        row[x * ch + c] = (ushort)v;
                    }
            }
            await AssertRoundTrip(f, (JxlLosslessEffort)(i % 4));
        }
    }
}
