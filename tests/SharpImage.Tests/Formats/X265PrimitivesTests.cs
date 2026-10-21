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
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported) return;
        await Assert.That(DctMismatches(depth)).IsEqualTo(0);
    }

    [Test]
    [Arguments(8)]
    [Arguments(10)]
    public async Task Idct_SimdMatchesScalar(int depth)
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported) return;
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

    [Test]
    public async Task CoefficientScans_SimdMatchScalar()
    {
        await Assert.That(CoefficientScanMismatches()).IsEqualTo(0);
    }

    // count_nonzero and findPosFirstLast (every scan type; odd / even levels, extremes)
    private static unsafe int CoefficientScanMismatches()
    {
        var rnd = new Random(7);
        int bad = 0;
        short* c = stackalloc short[32 * 32];
        short* dqA = stackalloc short[32 * 32];
        short* dqB = stackalloc short[32 * 32];
        for (int iter = 0; iter < 20000; iter++)
        {
            int density = 1 + (iter % 7);
            for (int i = 0; i < 32 * 32; i++)
            {
                int v = rnd.Next(density * 2) == 0 ? rnd.Next(-5, 6) : 0;
                if (v != 0 && rnd.Next(16) == 0) v = rnd.Next(2) == 0 ? short.MaxValue : short.MinValue;
                c[i] = (short)v;
            }

            int n = 16 << (2 * (iter & 3));
            if (X265.X265Primitives.CountNonzero(c, n) != X265.X265Primitives.CountNonzeroScalar(c, n)) bad++;
            int scale = (iter & 1) != 0 ? 72 << 8 : (40 + (iter % 33)) << (iter % 9);
            int shift = 1 + (iter % 12);
            X265.X265Primitives.DequantNormal(c, dqA, n, scale, shift);
            X265.X265Primitives.DequantNormalScalar(c, dqB, n, scale, shift);
            for (int i = 0; i < n; i++) if (dqA[i] != dqB[i]) bad++;
            ushort* scan4 = X265.X265Scan.Scan4x4[iter % 3];
            long trSize = 4 << (iter & 3);
            if (X265.X265Primitives.FindPosFirstLast(c + 4, trSize, scan4) != X265.X265Primitives.FindPosFirstLastScalar(c + 4, trSize, scan4)) bad++;
        }

        return bad;
    }

    [Test]
    public async Task ScanPosLast_SimdMatchesScalar()
    {
        await Assert.That(ScanPosLastMismatches()).IsEqualTo(0);
    }

    [Test]
    public async Task AllAngles_SimdMatchesScalar()
    {
        await Assert.That(AllAnglesMismatches()).IsEqualTo(0);
    }

    // random and extreme neighbours, every size, with and without the edge filter
    private static unsafe int AllAnglesMismatches()
    {
        var rnd = new Random(6);
        int bad = 0;
        byte* refPix = stackalloc byte[258 + 16];
        byte* filtPix = stackalloc byte[258 + 16];
        byte* a = stackalloc byte[33 * 32 * 32];
        byte* b = stackalloc byte[33 * 32 * 32];
        for (int iter = 0; iter < 800; iter++)
        {
            int log2 = 2 + (iter & 3), size = 1 << log2;
            int kind = (iter >> 2) % 3;
            for (int i = 0; i < 258 + 16; i++)
            {
                refPix[i] = (byte)(kind == 0 ? rnd.Next(256) : kind == 1 ? (rnd.Next(2) == 0 ? 0 : 255) : 128 + (i % 7));
                filtPix[i] = (byte)rnd.Next(256);
            }

            int bLuma = (iter >> 3) & 1;
            X265.X265Primitives.AllAngsPred(log2, a, refPix, filtPix, bLuma);
            X265.X265Primitives.AllAngsPredScalar(log2, b, refPix, filtPix, bLuma);
            for (int i = 0; i < 33 * size * size; i++) if (a[i] != b[i]) bad++;

            // single modes into a strided block
            for (int mode = 2; mode <= 34; mode++)
            {
                X265.X265Primitives.IntraPredAng(size, a, 40, refPix, mode, bLuma);
                X265.X265Primitives.IntraPredAngScalar(size, b, 40, refPix, mode, bLuma);
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (a[(y * 40) + x] != b[(y * 40) + x]) bad++;
            }
        }

        return bad;
    }

    // every scan type and size, sparse to dense blocks with extreme levels
    private static unsafe int ScanPosLastMismatches()
    {
        var rnd = new Random(5);
        int bad = 0;
        short* coeff = stackalloc short[32 * 32];
        byte* numA = stackalloc byte[64]; byte* numB = stackalloc byte[64];
        ushort* signA = stackalloc ushort[64]; ushort* signB = stackalloc ushort[64];
        ushort* flagA = stackalloc ushort[64]; ushort* flagB = stackalloc ushort[64];
        for (int iter = 0; iter < 20000; iter++)
        {
            int log2 = 2 + (iter & 3), size = 1 << log2;
            int scanType = log2 <= 3 ? (iter >> 2) % 3 : 0;
            int density = 1 + ((iter >> 4) % 6);
            int numSig = 0;
            for (int i = 0; i < size * size; i++)
            {
                int v = rnd.Next(density * 4) == 0 ? rnd.Next(-3, 4) : 0;
                if (v != 0 && rnd.Next(8) == 0) v = rnd.Next(2) == 0 ? short.MaxValue : short.MinValue;
                coeff[i] = (short)v;
                if (v != 0) numSig++;
            }

            if (numSig == 0)
            {
                coeff[rnd.Next(size * size)] = 1;
                numSig = 1;
            }

            ushort* scan = X265.X265Scan.ScanOrder[(scanType * 4) + log2 - 2];
            int a = X265.X265Primitives.ScanPosLast(scan, coeff, signA, flagA, numA, numSig, scanType, size);
            int b = X265.X265Primitives.ScanPosLastScalar(scan, coeff, signB, flagB, numB, numSig);
            if (a != b) bad++;
            for (int g = 0; g < 64; g++)
            {
                if (numA[g] != numB[g] || signA[g] != signB[g] || flagA[g] != flagB[g]) bad++;
            }
        }

        return bad;
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
        byte* pa = stackalloc byte[32 * 32];
        byte* pb = stackalloc byte[32 * 32];
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
            int sizeIdx = iter & 3;
            if (sizeIdx < 3 && X265.X265Primitives.PsyCostPP(sizeIdx, a + o, 48, b, 48) != X265.X265Primitives.PsyCostPPScalar(sizeIdx, a + o, 48, b, 48)) bad++;
            int log2 = 2 + (iter & 3);
            X265.X265Primitives.PlanarPred(log2, pa, 32, a + o);
            X265.X265Primitives.PlanarPredScalar(log2, pb, 32, a + o);
            for (int i = 0; i < 32 * (1 << log2); i++) if (pa[i] != pb[i]) bad++;
            if (X265.X265Primitives.Sad(a + o, 48, b, 48, n, n) != X265.X265Primitives.SadScalar(a + o, 48, b, 48, n, n)) bad++;
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
            if (X265Hbd.X265Primitives.Sad(a + o, 48, b, 48, n, n) != X265Hbd.X265Primitives.SadScalar(a + o, 48, b, 48, n, n)) bad++;
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
        for (int iter = 0; iter < 4000; iter++)
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
            if (sizeIdx == 0)
            {
                if (depth == 8)
                {
                    X265.X265Primitives.Dst4Simd(res, a, 40);
                    X265.X265Primitives.Dst4Scalar(res, b, 40);
                }
                else
                {
                    X265Hbd.X265Primitives.Dst4Simd(res, a, 40);
                    X265Hbd.X265Primitives.Dst4Scalar(res, b, 40);
                }

                for (int i = 0; i < 16; i++) if (a[i] != b[i]) mismatches++;
            }
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
        for (int iter = 0; iter < 4000; iter++)
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
            if (sizeIdx == 0)
            {
                if (depth == 8)
                {
                    X265.X265Primitives.Idst4Simd(coef, a, 40);
                    X265.X265Primitives.Idst4Scalar(coef, b, 40);
                }
                else
                {
                    X265Hbd.X265Primitives.Idst4Simd(coef, a, 40);
                    X265Hbd.X265Primitives.Idst4Scalar(coef, b, 40);
                }

                for (int r = 0; r < 4; r++)
                    for (int x = 0; x < 4; x++)
                        if (a[(r * 40) + x] != b[(r * 40) + x]) mismatches++;
            }
        }

        return mismatches;
    }
}
