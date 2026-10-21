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

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task PixelCosts_SimdMatchScalar(int depth)
    {
        if (!System.Runtime.Intrinsics.X86.Sse2.IsSupported) return;
        await Assert.That(depth == 8 ? PixelCostMismatches8() : PixelCostMismatches10()).IsEqualTo(0);
    }

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task NQuant_SimdMatchesScalar(int depth)
    {
        await Assert.That(NQuantMismatches(depth)).IsEqualTo(0);
    }

    [Test]
    public async Task ResidualAndReconstruction_SimdMatchScalar()
    {
        await Assert.That(ResidualMismatches()).IsEqualTo(0);
    }

    private static unsafe int ResidualMismatches()
    {
        var rnd = new Random(3);
        int bad = 0;
        byte* f8 = stackalloc byte[32 * 40];
        byte* p8 = stackalloc byte[32 * 40];
        ushort* f16 = stackalloc ushort[32 * 40];
        ushort* p16 = stackalloc ushort[32 * 40];
        short* r = stackalloc short[32 * 40];
        short* ra = stackalloc short[32 * 40];
        short* rb = stackalloc short[32 * 40];
        byte* o8a = stackalloc byte[32 * 40];
        byte* o8b = stackalloc byte[32 * 40];
        ushort* o16a = stackalloc ushort[32 * 40];
        ushort* o16b = stackalloc ushort[32 * 40];
        for (int iter = 0; iter < 400; iter++)
        {
            int n = 4 << (iter & 3);
            for (int i = 0; i < 32 * 40; i++)
            {
                f8[i] = (byte)rnd.Next(256); p8[i] = (byte)rnd.Next(256);
                f16[i] = (ushort)rnd.Next(1024); p16[i] = (ushort)rnd.Next(1024);
                r[i] = (iter & 4) != 0 ? (short)rnd.Next(short.MinValue, short.MaxValue + 1) : (short)rnd.Next(-1100, 1101);
            }

            X265.X265Primitives.GetResidual(f8, p8, ra, 40, n);
            X265.X265Primitives.GetResidualScalar(f8, p8, rb, 40, n);
            bad += Diff(ra, rb, n);
            X265.X265Primitives.CopyPS(ra, 40, f8, 40, n);
            X265.X265Primitives.CopyPSScalar(rb, 40, f8, 40, n);
            bad += Diff(ra, rb, n);
            X265.X265Primitives.AddPS(o8a, 40, p8, r, 40, 40, n);
            X265.X265Primitives.AddPSScalar(o8b, 40, p8, r, 40, 40, n);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (o8a[(y * 40) + x] != o8b[(y * 40) + x]) bad++;

            X265Hbd.X265Primitives.GetResidual(f16, p16, ra, 40, n);
            X265Hbd.X265Primitives.GetResidualScalar(f16, p16, rb, 40, n);
            bad += Diff(ra, rb, n);
            X265Hbd.X265Primitives.CopyPS(ra, 40, f16, 40, n);
            X265Hbd.X265Primitives.CopyPSScalar(rb, 40, f16, 40, n);
            bad += Diff(ra, rb, n);
            X265Hbd.X265Primitives.AddPS(o16a, 40, p16, r, 40, 40, n);
            X265Hbd.X265Primitives.AddPSScalar(o16b, 40, p16, r, 40, 40, n);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (o16a[(y * 40) + x] != o16b[(y * 40) + x]) bad++;
        }

        return bad;

        static int Diff(short* a, short* b, int n)
        {
            int d = 0;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (a[(y * 40) + x] != b[(y * 40) + x]) d++;
            return d;
        }
    }

    // random, smooth and extreme (checkerboard 0 / max, the 8-bit SA8D lane overflow) blocks
    private static void Fill<T>(Span<T> p, Random rnd, int kind, int max) where T : System.Numerics.INumber<T>
    {
        for (int i = 0; i < p.Length; i++)
        {
            int v = kind switch
            {
                0 => rnd.Next(max + 1),
                1 => (i % 48) * max / 48,
                2 => ((i + (i / 48)) & 1) != 0 ? max : 0,
                _ => rnd.Next(2) == 0 ? max : 0,
            };
            p[i] = T.CreateTruncating(v);
        }
    }

    private static unsafe int PixelCostMismatches8()
    {
        var rnd = new Random(8);
        int bad = 0;
        byte* a = stackalloc byte[48 * 48];
        byte* b = stackalloc byte[48 * 48];
        for (int iter = 0; iter < 2000; iter++)
        {
            Fill(new Span<byte>(a, 48 * 48), rnd, iter % 4, 255);
            Fill(new Span<byte>(b, 48 * 48), rnd, (iter / 4) % 4 == 0 ? 0 : (iter + 1) % 4, 255);
            if ((iter & 7) == 7) new Span<byte>(b, 48 * 48).Clear();   // a block against zero (psy cost)
            int o = rnd.Next(8);
            if (X265.X265Primitives.Sa8d8x8Raw(a + o, 48, b, 48) != X265.X265Primitives.Sa8d8x8RawScalar(a + o, 48, b, 48)) bad++;
            if (X265.X265Primitives.Satd4x4(a + o, 48, b, 48) != X265.X265Primitives.Satd4x4Scalar(a + o, 48, b, 48)) bad++;
            int n = 4 << (iter & 3);
            if (X265.X265Primitives.SsePP(a + o, 48, b, 48, n, n) != X265.X265Primitives.SsePPScalar(a + o, 48, b, 48, n, n)) bad++;
        }

        return bad;
    }

    private static unsafe int PixelCostMismatches10()
    {
        var rnd = new Random(10);
        int bad = 0;
        ushort* a = stackalloc ushort[48 * 48];
        ushort* b = stackalloc ushort[48 * 48];
        for (int iter = 0; iter < 2000; iter++)
        {
            Fill(new Span<ushort>(a, 48 * 48), rnd, iter % 4, 1023);
            Fill(new Span<ushort>(b, 48 * 48), rnd, (iter / 4) % 4 == 0 ? 0 : (iter + 1) % 4, 1023);
            if ((iter & 7) == 7) new Span<ushort>(b, 48 * 48).Clear();
            int o = rnd.Next(8);
            if (X265Hbd.X265Primitives.Sa8d8x8Raw(a + o, 48, b, 48) != X265Hbd.X265Primitives.Sa8d8x8RawScalar(a + o, 48, b, 48)) bad++;
            if (X265Hbd.X265Primitives.Satd4x4(a + o, 48, b, 48) != X265Hbd.X265Primitives.Satd4x4Scalar(a + o, 48, b, 48)) bad++;
            int n = 4 << (iter & 3);
            if (X265Hbd.X265Primitives.SsePP(a + o, 48, b, 48, n, n) != X265Hbd.X265Primitives.SsePPScalar(a + o, 48, b, 48, n, n)) bad++;
        }

        return bad;
    }

    private static unsafe int NQuantMismatches(int depth)
    {
        var rnd = new Random(depth + 7);
        int bad = 0;
        short* coef = stackalloc short[1024];
        int* q = stackalloc int[1024];
        short* oa = stackalloc short[1024];
        short* ob = stackalloc short[1024];
        for (int iter = 0; iter < 300; iter++)
        {
            int n = 16 << (2 * (iter & 3));   // 4x4 .. 32x32
            int qBits = 14 + rnd.Next(14);
            int scale = new[] { 26214, 23302, 20560, 18396, 16384, 14564 }[rnd.Next(6)] << rnd.Next(3);
            for (int i = 0; i < n; i++)
            {
                coef[i] = (iter % 3) switch
                {
                    0 => (short)rnd.Next(short.MinValue, short.MaxValue + 1),
                    1 => (short)rnd.Next(-300, 301),
                    _ => (short)(rnd.Next(2) == 0 ? short.MinValue : short.MaxValue),   // levels past int16
                };
                q[i] = scale;
            }

            int add = (1 << (qBits - 1)) - rnd.Next(1 << 10);
            uint sa, sb;
            if (depth == 8)
            {
                sa = X265.X265Primitives.NQuant(coef, q, oa, qBits, add, n);
                sb = X265.X265Primitives.NQuantScalar(coef, q, ob, qBits, add, n);
            }
            else
            {
                sa = X265Hbd.X265Primitives.NQuant(coef, q, oa, qBits, add, n);
                sb = X265Hbd.X265Primitives.NQuantScalar(coef, q, ob, qBits, add, n);
            }

            if (sa != sb) bad++;
            for (int i = 0; i < n; i++) if (oa[i] != ob[i]) bad++;
        }

        return bad;
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
