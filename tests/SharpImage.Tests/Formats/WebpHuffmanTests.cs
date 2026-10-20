using SharpImage.Formats;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>VP8L Huffman codes must be complete (Kraft sum exactly 1) and at most 15 bits: libwebp rejects anything
/// else. Skewed counts (a few dominant colours, a long tail) used to come out under-full after length limiting.</summary>
public sealed class WebpHuffmanTests
{
    private static long Kraft(int[] lengths, int maxLength)
    {
        long k = 0;
        foreach (int l in lengths)
        {
            if (l > 0)
            {
                k += 1L << (maxLength - l);
            }
        }
        return k;
    }

    [Test]
    public async Task CodeLengths_AreCompleteAndLimited_ForDeepTrees()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            int size = trial % 3 == 0 ? 19 : trial % 3 == 1 ? 256 : 280;
            int limit = size == 19 ? 7 : 15;
            var f = new long[size];
            int used = rng.Next(2, size + 1);
            long w = 1;
            for (int i = 0; i < used; i++)
            {
                // Fibonacci-like growth forces optimal depths far past the limit
                int s = rng.Next(size);
                f[s] += trial % 2 == 0 ? w : rng.Next(1, 1000);
                w = Math.Min(w * 2 + 1, long.MaxValue / 4);
            }
            int[] lengths = WebpCoder.ComputeCodeLengths(f, size, limit);
            int nonZero = lengths.Count(l => l > 0);
            await Assert.That(lengths.Max()).IsLessThanOrEqualTo(limit);
            if (nonZero > 1)
            {
                await Assert.That(Kraft(lengths, limit)).IsEqualTo(1L << limit);
            }
            for (int i = 0; i < size; i++)
            {
                await Assert.That(lengths[i] > 0).IsEqualTo(f[i] > 0 || (nonZero == 1 && lengths[i] == 1));
            }
        }
    }

    [Test]
    public async Task Lossless_ManyColoursSkewed_RoundTrips()
    {
        // a screenshot-like image: a few flat colours covering most pixels plus a long tail of rare colours
        const int w = 300, h = 200;
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, true);
        var rng = new Random(3);
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int r = rng.Next(100);
            byte v = r < 70 ? (byte)30 : r < 85 ? (byte)200 : (byte)rng.Next(256);
            px[i * 4] = v;
            px[i * 4 + 1] = (byte)(r < 90 ? v : rng.Next(256));
            px[i * 4 + 2] = (byte)(r < 95 ? 255 - v : rng.Next(256));
            px[i * 4 + 3] = (byte)(r < 97 ? 255 : rng.Next(256));
        }
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    row[x * 4 + c] = (ushort)(px[(y * w + x) * 4 + c] * 257);
                }
            }
        }
        using var ms = new MemoryStream();
        WebpCoder.Write(img, ms);
        ms.Position = 0;
        var back = WebpCoder.Read(ms);
        int bad = 0;
        for (int y = 0; y < h; y++)
        {
            var row = back.GetPixelRow(y);
            for (int x = 0; x < w * 4; x++)
            {
                if (row[x] >> 8 != px[y * w * 4 + x])
                {
                    bad++;
                }
            }
        }
        await Assert.That(bad).IsEqualTo(0);
    }
}
