// SIMD versions of the hottest render-pipeline stages. Every lane performs exactly the scalar stage's arithmetic
// (fused multiply-adds where the scalar code fuses), so results are bit-identical; they are used only when the
// CPU has AVX2 and FMA, otherwise the scalar stages run.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjSimd
{
    public static bool Enabled => Avx2.IsSupported && Fma.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Ld(float[] a, int i) => Vector256.LoadUnsafe(ref a[0], (nuint)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void St(Vector256<float> v, float[] a, int i) => v.StoreUnsafe(ref a[0], (nuint)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> AbsDiff(Vector256<float> a, Vector256<float> b) => Vector256.Abs(a - b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Weight(Vector256<float> sad, Vector256<float> invSigma) =>
        Avx.Max(Fma.MultiplyAdd(sad, invSigma, Vector256.Create(1.0f)), Vector256<float>.Zero);

    private static readonly (int Dy, int Dx)[] SadsOff = [(-2, 0), (-1, -1), (-1, 0), (-1, 1), (0, -2), (0, -1), (0, 1), (0, 2), (1, -1), (1, 0), (1, 1), (2, 0)];
    private static readonly (int Dy, int Dx)[] PlusOff = [(0, 0), (-1, 0), (0, -1), (1, 0), (0, 1)];

    /// <summary>EPF stage <paramref name="pass"/> over one row. ra/ro: [channel][row index 0..2*border] arrays and offsets of x = 0.</summary>
    public static void EpfRow(int pass, float[][][] ra, int[][] ro, float[] ox, int oxo, float[] oy, int oyo, float[] ob, int obo, int xsize, int xpos,
        float[] sigmaData, int rowSigma, float sm, float bsm, float[] channelScale, bool borderRow)
    {
        const float minSigma = -3.90524291751269967465540850526868f;
        int br = pass == 0 ? 3 : pass == 1 ? 2 : 1;
        Vector256<float> smCenter = Vector256.Create(bsm, sm, sm, sm, sm, sm, sm, bsm);
        Vector256<float> smBorder = Vector256.Create(bsm);
        Vector256<float> vsm = borderRow ? smBorder : smCenter;
        Vector256<float> one = Vector256.Create(1.0f);
        Span<Vector256<float>> sads = stackalloc Vector256<float>[12];
        for (int x = 0; x < xsize; x += 8)
        {
            int bx = (x + xpos + 16) / 8;
            float rs = sigmaData[rowSigma + bx];
            if (rs < minSigma)
            {
                St(Ld(ra[0][br], ro[0][br] + x), ox, oxo + x);
                St(Ld(ra[1][br], ro[1][br] + x), oy, oyo + x);
                St(Ld(ra[2][br], ro[2][br] + x), ob, obo + x);
                continue;
            }

            Vector256<float> invSigma = Vector256.Create(rs) * vsm;
            Vector256<float> X = Ld(ra[0][br], ro[0][br] + x), Y = Ld(ra[1][br], ro[1][br] + x), B = Ld(ra[2][br], ro[2][br] + x);
            Vector256<float> w = one;
            if (pass == 0)
            {
                sads.Clear();
                for (int c = 0; c < 3; c++)
                {
                    Epf0Sads(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(ra[c][0]), ro[c], br, x, Vector256.Create(channelScale[c]), sads);
                }

                ref float b0 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(ra[0][0]);
                ref float b1 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(ra[1][0]);
                ref float b2 = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(ra[2][0]);
                for (int i = 0; i < 12; i++)
                {
                    int row = br + SadsOff[i].Dy, xx = x + SadsOff[i].Dx;
                    Vector256<float> weight = Weight(sads[i], invSigma);
                    w += weight;
                    X = Fma.MultiplyAdd(weight, Vector256.LoadUnsafe(ref b0, (nuint)(ro[0][row] + xx)), X);
                    Y = Fma.MultiplyAdd(weight, Vector256.LoadUnsafe(ref b1, (nuint)(ro[1][row] + xx)), Y);
                    B = Fma.MultiplyAdd(weight, Vector256.LoadUnsafe(ref b2, (nuint)(ro[2][row] + xx)), B);
                }
            }
            else if (pass == 1)
            {
                Vector256<float> sad0 = Vector256<float>.Zero, sad1 = sad0, sad2 = sad0, sad3 = sad0;
                for (int c = 0; c < 3; c++)
                {
                    float[] rm2 = ra[c][0], rm1 = ra[c][1], r0 = ra[c][2], rp1 = ra[c][3], rp2 = ra[c][4];
                    int om2 = ro[c][0] + x, om1 = ro[c][1] + x, o0 = ro[c][2] + x, op1 = ro[c][3] + x, op2 = ro[c][4] + x;
                    Vector256<float> p20 = Ld(rm2, om2), p21 = Ld(rm1, om1);
                    Vector256<float> sad0c = AbsDiff(p20, p21);
                    Vector256<float> p11 = Ld(rm1, om1 - 1);
                    Vector256<float> sad1c = AbsDiff(p11, p21);
                    Vector256<float> p31 = Ld(rm1, om1 + 1);
                    Vector256<float> sad2c = AbsDiff(p31, p21);
                    Vector256<float> p02 = Ld(r0, o0 - 2), p12 = Ld(r0, o0 - 1);
                    sad1c += AbsDiff(p02, p12);
                    sad0c += AbsDiff(p11, p12);
                    Vector256<float> p22 = Ld(r0, o0);
                    Vector256<float> t = AbsDiff(p12, p22);
                    sad1c += t;
                    sad2c += t;
                    t = AbsDiff(p22, p21);
                    Vector256<float> sad3c = t;
                    sad0c += t;
                    Vector256<float> p32 = Ld(r0, o0 + 1);
                    sad0c += AbsDiff(p31, p32);
                    t = AbsDiff(p22, p32);
                    sad1c += t;
                    sad2c += t;
                    Vector256<float> p42 = Ld(r0, o0 + 2);
                    sad2c += AbsDiff(p42, p32);
                    Vector256<float> p13 = Ld(rp1, op1 - 1);
                    sad3c += AbsDiff(p13, p12);
                    Vector256<float> p23 = Ld(rp1, op1);
                    t = AbsDiff(p22, p23);
                    sad0c += t;
                    sad3c += t;
                    sad1c += AbsDiff(p13, p23);
                    Vector256<float> p33 = Ld(rp1, op1 + 1);
                    sad2c += AbsDiff(p33, p23);
                    sad3c += AbsDiff(p33, p32);
                    Vector256<float> p24 = Ld(rp2, op2);
                    sad3c += AbsDiff(p24, p23);
                    Vector256<float> scale = Vector256.Create(channelScale[c]);
                    sad0 = Fma.MultiplyAdd(sad0c, scale, sad0);
                    sad1 = Fma.MultiplyAdd(sad1c, scale, sad1);
                    sad2 = Fma.MultiplyAdd(sad2c, scale, sad2);
                    sad3 = Fma.MultiplyAdd(sad3c, scale, sad3);
                }

                AddPixel(ra, ro, br - 1, x, sad0, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br, x - 1, sad1, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br, x + 1, sad2, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel(ra, ro, br + 1, x, sad3, invSigma, ref X, ref Y, ref B, ref w);
            }
            else
            {
                Vector256<float> xc = X, yc = Y, bc = B;
                Vector256<float> s0 = Vector256.Create(channelScale[0]), s1 = Vector256.Create(channelScale[1]), s2 = Vector256.Create(channelScale[2]);
                AddPixel2(ra, ro, br - 1, x, xc, yc, bc, s0, s1, s2, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br, x - 1, xc, yc, bc, s0, s1, s2, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br, x + 1, xc, yc, bc, s0, s1, s2, invSigma, ref X, ref Y, ref B, ref w);
                AddPixel2(ra, ro, br + 1, x, xc, yc, bc, s0, s1, s2, invSigma, ref X, ref Y, ref B, ref w);
            }

            Vector256<float> invW = one / w;
            St(X * invW, ox, oxo + x);
            St(Y * invW, oy, oyo + x);
            St(B * invW, ob, obo + x);
        }
    }

    // Generated: EPF0 SADs for one channel, every neighbourhood sample loaded once.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Epf0Sads(ref float b, int[] oc, int br, int x, Vector256<float> scale, Span<Vector256<float>> sads)
    {
        int rm3 = oc[br + (-3)] + x;
        int rm2 = oc[br + (-2)] + x;
        int rm1 = oc[br + (-1)] + x;
        int rp0 = oc[br + (0)] + x;
        int rp1 = oc[br + (1)] + x;
        int rp2 = oc[br + (2)] + x;
        int rp3 = oc[br + (3)] + x;
        Vector256<float> pm3p0 = Vector256.LoadUnsafe(ref b, (nuint)(rm3 + (0)));
        Vector256<float> pm2m1 = Vector256.LoadUnsafe(ref b, (nuint)(rm2 + (-1)));
        Vector256<float> pm2p0 = Vector256.LoadUnsafe(ref b, (nuint)(rm2 + (0)));
        Vector256<float> pm2p1 = Vector256.LoadUnsafe(ref b, (nuint)(rm2 + (1)));
        Vector256<float> pm1m2 = Vector256.LoadUnsafe(ref b, (nuint)(rm1 + (-2)));
        Vector256<float> pm1m1 = Vector256.LoadUnsafe(ref b, (nuint)(rm1 + (-1)));
        Vector256<float> pm1p0 = Vector256.LoadUnsafe(ref b, (nuint)(rm1 + (0)));
        Vector256<float> pm1p1 = Vector256.LoadUnsafe(ref b, (nuint)(rm1 + (1)));
        Vector256<float> pm1p2 = Vector256.LoadUnsafe(ref b, (nuint)(rm1 + (2)));
        Vector256<float> pp0m3 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (-3)));
        Vector256<float> pp0m2 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (-2)));
        Vector256<float> pp0m1 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (-1)));
        Vector256<float> pp0p0 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (0)));
        Vector256<float> pp0p1 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (1)));
        Vector256<float> pp0p2 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (2)));
        Vector256<float> pp0p3 = Vector256.LoadUnsafe(ref b, (nuint)(rp0 + (3)));
        Vector256<float> pp1m2 = Vector256.LoadUnsafe(ref b, (nuint)(rp1 + (-2)));
        Vector256<float> pp1m1 = Vector256.LoadUnsafe(ref b, (nuint)(rp1 + (-1)));
        Vector256<float> pp1p0 = Vector256.LoadUnsafe(ref b, (nuint)(rp1 + (0)));
        Vector256<float> pp1p1 = Vector256.LoadUnsafe(ref b, (nuint)(rp1 + (1)));
        Vector256<float> pp1p2 = Vector256.LoadUnsafe(ref b, (nuint)(rp1 + (2)));
        Vector256<float> pp2m1 = Vector256.LoadUnsafe(ref b, (nuint)(rp2 + (-1)));
        Vector256<float> pp2p0 = Vector256.LoadUnsafe(ref b, (nuint)(rp2 + (0)));
        Vector256<float> pp2p1 = Vector256.LoadUnsafe(ref b, (nuint)(rp2 + (1)));
        Vector256<float> pp3p0 = Vector256.LoadUnsafe(ref b, (nuint)(rp3 + (0)));
        sads[0] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pm2p0) + AbsDiff(pm1p0, pm3p0)) + AbsDiff(pp0m1, pm2m1)) + AbsDiff(pp1p0, pm1p0)) + AbsDiff(pp0p1, pm2p1)), scale, sads[0]);
        sads[1] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pm1m1) + AbsDiff(pm1p0, pm2m1)) + AbsDiff(pp0m1, pm1m2)) + AbsDiff(pp1p0, pp0m1)) + AbsDiff(pp0p1, pm1p0)), scale, sads[1]);
        sads[2] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pm1p0) + AbsDiff(pm1p0, pm2p0)) + AbsDiff(pp0m1, pm1m1)) + AbsDiff(pp1p0, pp0p0)) + AbsDiff(pp0p1, pm1p1)), scale, sads[2]);
        sads[3] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pm1p1) + AbsDiff(pm1p0, pm2p1)) + AbsDiff(pp0m1, pm1p0)) + AbsDiff(pp1p0, pp0p1)) + AbsDiff(pp0p1, pm1p2)), scale, sads[3]);
        sads[4] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp0m2) + AbsDiff(pm1p0, pm1m2)) + AbsDiff(pp0m1, pp0m3)) + AbsDiff(pp1p0, pp1m2)) + AbsDiff(pp0p1, pp0m1)), scale, sads[4]);
        sads[5] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp0m1) + AbsDiff(pm1p0, pm1m1)) + AbsDiff(pp0m1, pp0m2)) + AbsDiff(pp1p0, pp1m1)) + AbsDiff(pp0p1, pp0p0)), scale, sads[5]);
        sads[6] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp0p1) + AbsDiff(pm1p0, pm1p1)) + AbsDiff(pp0m1, pp0p0)) + AbsDiff(pp1p0, pp1p1)) + AbsDiff(pp0p1, pp0p2)), scale, sads[6]);
        sads[7] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp0p2) + AbsDiff(pm1p0, pm1p2)) + AbsDiff(pp0m1, pp0p1)) + AbsDiff(pp1p0, pp1p2)) + AbsDiff(pp0p1, pp0p3)), scale, sads[7]);
        sads[8] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp1m1) + AbsDiff(pm1p0, pp0m1)) + AbsDiff(pp0m1, pp1m2)) + AbsDiff(pp1p0, pp2m1)) + AbsDiff(pp0p1, pp1p0)), scale, sads[8]);
        sads[9] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp1p0) + AbsDiff(pm1p0, pp0p0)) + AbsDiff(pp0m1, pp1m1)) + AbsDiff(pp1p0, pp2p0)) + AbsDiff(pp0p1, pp1p1)), scale, sads[9]);
        sads[10] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp1p1) + AbsDiff(pm1p0, pp0p1)) + AbsDiff(pp0m1, pp1p0)) + AbsDiff(pp1p0, pp2p1)) + AbsDiff(pp0p1, pp1p2)), scale, sads[10]);
        sads[11] = Fma.MultiplyAdd(((((AbsDiff(pp0p0, pp2p0) + AbsDiff(pm1p0, pp1p0)) + AbsDiff(pp0m1, pp2m1)) + AbsDiff(pp1p0, pp3p0)) + AbsDiff(pp0p1, pp2p1)), scale, sads[11]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPixel(float[][][] ra, int[][] ro, int row, int x, Vector256<float> sad, Vector256<float> invSigma, ref Vector256<float> X,
        ref Vector256<float> Y, ref Vector256<float> B, ref Vector256<float> w)
    {
        Vector256<float> weight = Weight(sad, invSigma);
        w += weight;
        X = Fma.MultiplyAdd(weight, Ld(ra[0][row], ro[0][row] + x), X);
        Y = Fma.MultiplyAdd(weight, Ld(ra[1][row], ro[1][row] + x), Y);
        B = Fma.MultiplyAdd(weight, Ld(ra[2][row], ro[2][row] + x), B);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPixel2(float[][][] ra, int[][] ro, int row, int x, Vector256<float> rx, Vector256<float> ry, Vector256<float> rb,
        Vector256<float> s0, Vector256<float> s1, Vector256<float> s2, Vector256<float> invSigma, ref Vector256<float> X, ref Vector256<float> Y,
        ref Vector256<float> B, ref Vector256<float> w)
    {
        Vector256<float> cx = Ld(ra[0][row], ro[0][row] + x), cy = Ld(ra[1][row], ro[1][row] + x), cb = Ld(ra[2][row], ro[2][row] + x);
        Vector256<float> sad = AbsDiff(cx, rx) * s0;
        sad = Fma.MultiplyAdd(AbsDiff(cy, ry), s1, sad);
        sad = Fma.MultiplyAdd(AbsDiff(cb, rb), s2, sad);
        Vector256<float> weight = Weight(sad, invSigma);
        w += weight;
        X = Fma.MultiplyAdd(weight, cx, X);
        Y = Fma.MultiplyAdd(weight, cy, Y);
        B = Fma.MultiplyAdd(weight, cb, B);
    }

    /// <summary>Gaborish over one row and channel.</summary>
    public static void GaborishRow(float[] t, int to, float[] m, int mo, float[] b, int bo, float[] o, int oo, int xsize, float w0, float w1, float w2)
    {
        Vector256<float> vw0 = Vector256.Create(w0), vw1 = Vector256.Create(w1), vw2 = Vector256.Create(w2);
        for (int x = 0; x < xsize; x += 8)
        {
            Vector256<float> sum0 = Ld(m, mo + x);
            Vector256<float> sum1 = (Ld(m, mo + x - 1) + Ld(m, mo + x + 1)) + (Ld(t, to + x) + Ld(b, bo + x));
            Vector256<float> sum2 = (Ld(t, to + x - 1) + Ld(t, to + x + 1)) + (Ld(b, bo + x - 1) + Ld(b, bo + x + 1));
            St(Fma.MultiplyAdd(sum2, vw2, Fma.MultiplyAdd(sum1, vw1, sum0 * vw0)), o, oo + x);
        }
    }

    /// <summary>XybToRgb over one row (in place).</summary>
    public static void XybRow(float[] a0, int o0, float[] a1, int o1, float[] a2, int o2, int xsize, LjOpsinParams p)
    {
        float[] m = p.InverseOpsinMatrix;
        Vector256<float> c0 = Vector256.Create(p.OpsinBiasesCbrt[0]), c1 = Vector256.Create(p.OpsinBiasesCbrt[1]), c2 = Vector256.Create(p.OpsinBiasesCbrt[2]);
        Vector256<float> nb0 = Vector256.Create(p.OpsinBiases[0]), nb1 = Vector256.Create(p.OpsinBiases[1]), nb2 = Vector256.Create(p.OpsinBiases[2]);
        Vector256<float> m0 = Vector256.Create(m[0]), m1 = Vector256.Create(m[1]), m2 = Vector256.Create(m[2]), m3 = Vector256.Create(m[3]),
            m4 = Vector256.Create(m[4]), m5 = Vector256.Create(m[5]), m6 = Vector256.Create(m[6]), m7 = Vector256.Create(m[7]), m8 = Vector256.Create(m[8]);
        for (int x = 0; x < xsize; x += 8)
        {
            Vector256<float> ox = Ld(a0, o0 + x), oy = Ld(a1, o1 + x), ob = Ld(a2, o2 + x);
            Vector256<float> gr = (oy + ox) - c0;
            Vector256<float> gg = (oy - ox) - c1;
            Vector256<float> gb = ob - c2;
            Vector256<float> mr = Fma.MultiplyAdd(gr * gr, gr, nb0);
            Vector256<float> mg = Fma.MultiplyAdd(gg * gg, gg, nb1);
            Vector256<float> mb = Fma.MultiplyAdd(gb * gb, gb, nb2);
            Vector256<float> r = m0 * mr, g = m3 * mr, b = m6 * mr;
            r = Fma.MultiplyAdd(m1, mg, r);
            g = Fma.MultiplyAdd(m4, mg, g);
            b = Fma.MultiplyAdd(m7, mg, b);
            r = Fma.MultiplyAdd(m2, mb, r);
            g = Fma.MultiplyAdd(m5, mb, g);
            b = Fma.MultiplyAdd(m8, mb, b);
            St(r, a0, o0 + x);
            St(g, a1, o1 + x);
            St(b, a2, o2 + x);
        }
    }

    private static readonly float[] SrgbP = [-5.135152395e-04f, 5.287254571e-03f, 3.903842876e-01f, 1.474205315e+00f, 7.352629620e-01f];
    private static readonly float[] SrgbQ = [1.004519624e-02f, 3.036675394e-01f, 1.340816930e+00f, 9.258482155e-01f, 2.424867759e-02f];

    /// <summary>TF_SRGB::EncodedFromDisplay (high precision) over one row in place.</summary>
    public static void SrgbFromLinearRow(float[] a, int o, int xsize)
    {
        Vector256<float> signMask = Vector256.Create(-0.0f);
        Vector256<float> k1292 = Vector256.Create(12.92f), thresh = Vector256.Create(0.0031308f);
        for (int x = 0; x < xsize; x += 8)
        {
            Vector256<float> v = Ld(a, o + x);
            Vector256<float> sign = v & signMask;
            Vector256<float> ax = Vector256.AndNot(v, signMask);
            Vector256<float> linear = ax * k1292;
            Vector256<float> s = Avx.Sqrt(ax);
            Vector256<float> yp = Vector256.Create(SrgbP[4]), yq = Vector256.Create(SrgbQ[4]);
            yp = Fma.MultiplyAdd(yp, s, Vector256.Create(SrgbP[3]));
            yq = Fma.MultiplyAdd(yq, s, Vector256.Create(SrgbQ[3]));
            yp = Fma.MultiplyAdd(yp, s, Vector256.Create(SrgbP[2]));
            yq = Fma.MultiplyAdd(yq, s, Vector256.Create(SrgbQ[2]));
            yp = Fma.MultiplyAdd(yp, s, Vector256.Create(SrgbP[1]));
            yq = Fma.MultiplyAdd(yq, s, Vector256.Create(SrgbQ[1]));
            yp = Fma.MultiplyAdd(yp, s, Vector256.Create(SrgbP[0]));
            yq = Fma.MultiplyAdd(yq, s, Vector256.Create(SrgbQ[0]));
            Vector256<float> poly = yp / yq;
            Vector256<float> mag = Vector256.ConditionalSelect(Vector256.GreaterThan(ax, thresh), poly, linear);
            St(Vector256.AndNot(mag, signMask) | sign, a, o + x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> FastLog2f(Vector256<float> x)
    {
        Vector256<int> xBits = x.AsInt32();
        Vector256<int> expBits = xBits - Vector256.Create(0x3f2aaaab);
        Vector256<int> expShifted = Avx2.ShiftRightArithmetic(expBits, 23);
        Vector256<float> mantissa = (xBits - Avx2.ShiftLeftLogical(expShifted, 23)).AsSingle();
        Vector256<float> expVal = Avx.ConvertToVector256Single(expShifted);
        Vector256<float> m = mantissa - Vector256.Create(1.0f);
        Vector256<float> yp = Fma.MultiplyAdd(Vector256.Create(7.4245873327820566E-01f), m, Vector256.Create(1.4287160470083755E+00f));
        Vector256<float> yq = Fma.MultiplyAdd(Vector256.Create(1.7409343003366853E-01f), m, Vector256.Create(1.0096718572241148E+00f));
        yp = Fma.MultiplyAdd(yp, m, Vector256.Create(-1.8503833400518310E-06f));
        yq = Fma.MultiplyAdd(yq, m, Vector256.Create(9.9032814277590719E-01f));
        return (yp / yq) + expVal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> FastPow2f(Vector256<float> x)
    {
        Vector256<float> floorx = Avx.Floor(x);
        Vector256<float> exp = Avx2.ShiftLeftLogical(Avx.ConvertToVector256Int32WithTruncation(floorx) + Vector256.Create(127), 23).AsSingle();
        Vector256<float> frac = x - floorx;
        Vector256<float> num = frac + Vector256.Create(1.01749063e+01f);
        num = Fma.MultiplyAdd(num, frac, Vector256.Create(4.88687798e+01f));
        num = Fma.MultiplyAdd(num, frac, Vector256.Create(9.85506591e+01f));
        num *= exp;
        Vector256<float> den = Fma.MultiplyAdd(frac, Vector256.Create(2.10242958e-01f), Vector256.Create(-2.22328856e-02f));
        den = Fma.MultiplyAdd(den, frac, Vector256.Create(-1.94414990e+01f));
        den = Fma.MultiplyAdd(den, frac, Vector256.Create(9.85506633e+01f));
        return num / den;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> FastPowf(Vector256<float> b, Vector256<float> e) => FastPow2f(FastLog2f(b) * e);

    /// <summary>TF_709::EncodedFromDisplay over one row in place.</summary>
    public static void Rec709FromLinearRow(float[] a, int o, int xsize)
    {
        Vector256<float> mulLow = Vector256.Create((float)4.5), mulHi = Vector256.Create((float)1.099), powHi = Vector256.Create((float)0.45);
        Vector256<float> sub = Vector256.Create((float)-0.099), thresh = Vector256.Create((float)0.018);
        for (int x = 0; x < xsize; x += 8)
        {
            Vector256<float> v = Ld(a, o + x);
            Vector256<float> low = mulLow * v;
            Vector256<float> hi = Fma.MultiplyAdd(mulHi, FastPowf(v, powHi), sub);
            St(Vector256.ConditionalSelect(Vector256.LessThanOrEqual(v, thresh), low, hi), a, o + x);
        }
    }

    /// <summary>OpGamma over one row in place.</summary>
    public static void GammaFromLinearRow(float[] a, int o, int xsize, float inverseGamma)
    {
        Vector256<float> g = Vector256.Create(inverseGamma), tiny = Vector256.Create(1e-5f);
        for (int x = 0; x < xsize; x += 8)
        {
            Vector256<float> v = Ld(a, o + x);
            Vector256<float> r = FastPowf(v, g);
            St(Vector256.ConditionalSelect(Vector256.LessThanOrEqual(v, tiny), Vector256<float>.Zero, r), a, o + x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> AdjustQuantBias(Vector256<int> qi, float biasC, Vector256<float> b3)
    {
        Vector256<float> quant = Avx.ConvertToVector256Single(qi);
        Vector256<float> signMask = Vector256.Create(-0.0f);
        Vector256<float> sign = quant & signMask;
        Vector256<float> absQuant = Vector256.AndNot(quant, signMask);
        Vector256<float> is01 = Vector256.LessThan(absQuant, Vector256.Create(1.125f));
        Vector256<float> not0 = Vector256.GreaterThan(absQuant, Vector256<float>.Zero);
        Vector256<float> oneBias = not0 & (Vector256.Create(biasC) ^ sign);
        Vector256<float> bias = Fma.MultiplyAddNegated(b3, Avx.Reciprocal(quant), quant);
        return Vector256.ConditionalSelect(is01, oneBias, bias);
    }

    /// <summary>DequantBlock (dec_group.cc) for `count` coefficients, 8 at a time.</summary>
    public static void DequantBlock(float[] table, int mOff, int size, int count, float sdx, float sdy, float sdb, int[] qblock, float[] block,
        float[] biases, float xCcMul, float bCcMul)
    {
        Vector256<float> vx = Vector256.Create(sdx), vy = Vector256.Create(sdy), vb = Vector256.Create(sdb);
        Vector256<float> xcc = Vector256.Create(xCcMul), bcc = Vector256.Create(bCcMul), b3 = Vector256.Create(biases[3]);
        for (int k = 0; k < count; k += 8)
        {
            Vector256<float> xMul = Ld(table, mOff + k) * vx;
            Vector256<float> yMul = Ld(table, mOff + size + k) * vy;
            Vector256<float> bMul = Ld(table, mOff + (2 * size) + k) * vb;
            Vector256<float> dqXcc = AdjustQuantBias(Vector256.LoadUnsafe(ref qblock[0], (nuint)k), biases[0], b3) * xMul;
            Vector256<float> dqY = AdjustQuantBias(Vector256.LoadUnsafe(ref qblock[0], (nuint)(size + k)), biases[1], b3) * yMul;
            Vector256<float> dqBcc = AdjustQuantBias(Vector256.LoadUnsafe(ref qblock[0], (nuint)((2 * size) + k)), biases[2], b3) * bMul;
            St(Fma.MultiplyAdd(xcc, dqY, dqXcc), block, k);
            St(dqY, block, size + k);
            St(Fma.MultiplyAdd(bcc, dqY, dqBcc), block, (2 * size) + k);
        }
    }
}
