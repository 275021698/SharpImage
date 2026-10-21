using X265 = SharpImage.Formats.Hevc.X265;
using X265Hbd = SharpImage.Formats.Hevc.X265.Hbd;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The x265 port's SIMD primitives against its scalar ports of x265's C (which the encoder's byte-identical output was
/// verified with): every result must be identical, for the 8-bit and the 10-bit (HIGH_BIT_DEPTH) builds.
/// </summary>
public sealed class X265PrimitivesTests
{
    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task Dct_SimdMatchesScalar(int depth)
    {
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported) return;
        await Assert.That(DctMismatches(depth)).IsEqualTo(0);
    }

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task Idct_SimdMatchesScalar(int depth)
    {
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported) return;
        await Assert.That(IdctMismatches(depth)).IsEqualTo(0);
    }

    private static unsafe int DctMismatches(int depth)
    {
        var rnd = new Random(depth);
        int max = (1 << depth) - 1, mismatches = 0;
        short* res = stackalloc short[32 * 40];
        short* a = stackalloc short[32 * 32];
        short* b = stackalloc short[32 * 32];
        for (int iter = 0; iter < 400; iter++)
        {
            int sizeIdx = iter & 3, n = 4 << sizeIdx;
            // residuals of every kind: full range, small, flat, extremes
            int kind = (iter >> 2) % 4;
            int amp = kind switch { 0 => max, 1 => 16, 2 => 0, _ => max };
            for (int i = 0; i < 32 * 40; i++)
                res[i] = (short)(kind == 3 ? ((i & 1) == 0 ? max : -max) : rnd.Next(-amp, amp + 1));
            if (depth == 8)
            {
                X265.X265Primitives.DctSimd(sizeIdx, res, a, 40);
                X265.X265Primitives.DctScalar(sizeIdx, res, b, 40);
            }
            else
            {
                X265Hbd.X265Primitives.DctSimd(sizeIdx, res, a, 40);
                X265Hbd.X265Primitives.DctScalar(sizeIdx, res, b, 40);
            }

            for (int i = 0; i < n * n; i++) if (a[i] != b[i]) mismatches++;
        }

        return mismatches;
    }

    private static unsafe int IdctMismatches(int depth)
    {
        var rnd = new Random(depth + 100);
        int mismatches = 0;
        short* coef = stackalloc short[32 * 32];
        short* a = stackalloc short[32 * 40];
        short* b = stackalloc short[32 * 40];
        for (int iter = 0; iter < 400; iter++)
        {
            int sizeIdx = iter & 3, n = 4 << sizeIdx;
            int kind = (iter >> 2) % 4;
            for (int i = 0; i < n * n; i++)
            {
                coef[i] = kind switch
                {
                    0 => (short)rnd.Next(short.MinValue, short.MaxValue + 1),   // saturating sums
                    1 => (short)rnd.Next(-64, 65),
                    2 => (short)(rnd.Next(8) == 0 ? rnd.Next(-2000, 2001) : 0),   // sparse
                    _ => (short)(i == 0 ? rnd.Next(-32768, 32768) : 0),
                };
            }

            if (depth == 8)
            {
                X265.X265Primitives.IdctSimd(sizeIdx, coef, a, 40);
                X265.X265Primitives.IdctScalar(sizeIdx, coef, b, 40);
            }
            else
            {
                X265Hbd.X265Primitives.IdctSimd(sizeIdx, coef, a, 40);
                X265Hbd.X265Primitives.IdctScalar(sizeIdx, coef, b, 40);
            }

            for (int r = 0; r < n; r++)
                for (int x = 0; x < n; x++)
                    if (a[(r * 40) + x] != b[(r * 40) + x]) mismatches++;
        }

        return mismatches;
    }
}
