// SIMD forms of x265's DCT / DST and their inverses (the same integer sums as the C butterflies, so the coefficients
// and residuals are bit-identical). No transposes: the forward transform's first pass takes each residual row's
// butterfly sums two at a time (pmaddwd against a table of output coefficient pairs), its second pass runs down the
// columns of that result (two rows interleaved against a broadcast coefficient pair); the inverse runs down the
// coefficient columns first (even / odd rows for the output butterfly, skipping the all-zero rows), then across the
// rows of that result (skipping the all-zero columns).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

using static X265Const;

internal static unsafe partial class X265Primitives
{
    private static readonly short[] s_dst4Matrix = { 29, 55, 74, 84, 74, 74, 0, -74, 84, -29, -74, 55, 55, -84, 74, -29 };

    // per size (4, 8, 16, 32; [0] also holds the DST): the tables of TransformTables
    private static readonly TransformTables[] s_tx = { new(X265Tables.T4, 4), new(X265Tables.T8, 8), new(X265Tables.T16, 16), new(X265Tables.T32, 32) };
    private static readonly TransformTables s_dstTx = new(s_dst4Matrix, 4);

    private static readonly Vector128<byte> RevShorts = Vector128.Create((byte)14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1);
    private static readonly Vector128<byte> EvenOddShorts = Vector128.Create((byte)0, 1, 4, 5, 8, 9, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15);

    /// <summary>
    /// Coefficient tables of an n-point transform with matrix T (T[k][j], k the frequency), each entry a pair of 16-bit
    /// coefficients for pmaddwd. Forward pass 1 (n &gt;= 8): fe / fo [m][v] the pairs (T[k][2m], T[k][2m + 1]) of the
    /// even (k = 2i) / odd (k = 2i + 1) outputs i = 4v..4v + 3 (n = 32: fe split into fee (k = 4i) and feo (k = 4i + 2)
    /// over the second butterfly level); n = 4: f01 / f23 the pairs of columns 0, 1 / 2, 3 for the 4 outputs.
    /// Forward pass 2: f2[k][m] the pair (T[k][2m], T[k][2m + 1]) in all 4 lanes. Inverse pass 1: ie / io [b][m] the
    /// pair (T[4m][b], T[4m + 2][b]) / (T[4m + 1][b], T[4m + 3][b]) in all lanes (n = 4: i4[b][p] the rows 2p, 2p + 1).
    /// Inverse pass 2: ge / go [m][v] those pairs for the outputs b = 4v..4v + 3 (n = 4: g01 / g23 the rows 0, 1 / 2, 3).
    /// </summary>
    private sealed class TransformTables
    {
        public readonly short* fe, fo, fee, feo, f2, ie, io, ge, go;

        public TransformTables(short[] t, int n)
        {
            short T(int k, int j) => t[(k * n) + j];
            short* Alloc(int pairs) => (short*)NativeMemory.AlignedAlloc((nuint)(pairs * 4), 64);

            // forward pass 1
            if (n == 4)
            {
                fe = Alloc(8);
                for (int k = 0; k < 4; k++)
                {
                    fe[2 * k] = T(k, 0); fe[(2 * k) + 1] = T(k, 1);
                    fe[8 + (2 * k)] = T(k, 2); fe[8 + (2 * k) + 1] = T(k, 3);
                }
            }
            else if (n == 32)
            {
                fee = Alloc(4 * 8);   // [m < 4][v < 2][4 pairs]
                feo = Alloc(4 * 8);
                for (int m = 0; m < 4; m++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int at = (((m * 2) + (i >> 2)) * 8) + (2 * (i & 3));
                        fee[at] = T(4 * i, 2 * m); fee[at + 1] = T(4 * i, (2 * m) + 1);
                        feo[at] = T((4 * i) + 2, 2 * m); feo[at + 1] = T((4 * i) + 2, (2 * m) + 1);
                    }
                }

                fo = Alloc(8 * 16);   // [m < 8][v < 4][4 pairs]
                for (int m = 0; m < 8; m++)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int at = (((m * 4) + (i >> 2)) * 8) + (2 * (i & 3));
                        fo[at] = T((2 * i) + 1, 2 * m); fo[at + 1] = T((2 * i) + 1, (2 * m) + 1);
                    }
                }
            }
            else
            {
                fe = Alloc(n / 4 * n / 2);   // [m < n / 4][v < n / 8][4 pairs]
                fo = Alloc(n / 4 * n / 2);
                for (int m = 0; m < n / 4; m++)
                {
                    for (int i = 0; i < n / 2; i++)
                    {
                        int at = (((m * (n / 8)) + (i >> 2)) * 8) + (2 * (i & 3));
                        fe[at] = T(2 * i, 2 * m); fe[at + 1] = T(2 * i, (2 * m) + 1);
                        fo[at] = T((2 * i) + 1, 2 * m); fo[at + 1] = T((2 * i) + 1, (2 * m) + 1);
                    }
                }
            }

            // forward pass 2
            f2 = Alloc(n * (n / 2) * 4);
            for (int k = 0; k < n; k++)
            {
                for (int m = 0; m < n / 2; m++)
                {
                    for (int q = 0; q < 4; q++)
                    {
                        int at = (((k * (n / 2)) + m) * 8) + (2 * q);
                        f2[at] = T(k, 2 * m); f2[at + 1] = T(k, (2 * m) + 1);
                    }
                }
            }

            // inverse pass 1
            if (n == 4)
            {
                ie = Alloc(4 * 2 * 4);   // [b < 4][p < 2] replicated
                for (int b = 0; b < 4; b++)
                {
                    for (int p = 0; p < 2; p++)
                    {
                        for (int q = 0; q < 4; q++)
                        {
                            int at = (((b * 2) + p) * 8) + (2 * q);
                            ie[at] = T(2 * p, b); ie[at + 1] = T((2 * p) + 1, b);
                        }
                    }
                }

                ge = Alloc(8);
                for (int b = 0; b < 4; b++)
                {
                    ge[2 * b] = T(0, b); ge[(2 * b) + 1] = T(1, b);
                    ge[8 + (2 * b)] = T(2, b); ge[8 + (2 * b) + 1] = T(3, b);
                }

                return;
            }

            ie = Alloc(n / 2 * n / 4 * 4);   // [b < n / 2][m < n / 4] replicated
            io = Alloc(n / 2 * n / 4 * 4);
            for (int b = 0; b < n / 2; b++)
            {
                for (int m = 0; m < n / 4; m++)
                {
                    for (int q = 0; q < 4; q++)
                    {
                        int at = (((b * (n / 4)) + m) * 8) + (2 * q);
                        ie[at] = T(4 * m, b); ie[at + 1] = T((4 * m) + 2, b);
                        io[at] = T((4 * m) + 1, b); io[at + 1] = T((4 * m) + 3, b);
                    }
                }
            }

            // inverse pass 2
            ge = Alloc(n / 4 * n / 2);   // [m < n / 4][v < n / 8][4 pairs]
            go = Alloc(n / 4 * n / 2);
            for (int m = 0; m < n / 4; m++)
            {
                for (int b = 0; b < n / 2; b++)
                {
                    int at = (((m * (n / 8)) + (b >> 2)) * 8) + (2 * (b & 3));
                    ge[at] = T(4 * m, b); ge[at + 1] = T((4 * m) + 2, b);
                    go[at] = T((4 * m) + 1, b); go[at + 1] = T((4 * m) + 3, b);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MAdd(Vector128<short> a, short* b) => Sse2.MultiplyAddAdjacent(a, Sse2.LoadAlignedVector128(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> MAdd(Vector128<int> a, short* b) => Sse2.MultiplyAddAdjacent(a.AsInt16(), Sse2.LoadAlignedVector128(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> RoundPack(Vector128<int> a, Vector128<int> b, Vector128<int> add, byte shift) =>
        Sse2.PackSignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(a, add), shift), Sse2.ShiftRightArithmetic(Sse2.Add(b, add), shift));

    // ---- forward ----

    /// <summary>primitives.cu[sizeIdx].dct (n = 4 &lt;&lt; sizeIdx; SSSE3).</summary>
    internal static void DctSimd(int sizeIdx, short* src, short* dst, long srcStride)
    {
        int shift1 = sizeIdx + 1 + X265_DEPTH - 8, shift2 = sizeIdx + 8;
        if (sizeIdx == 0)
        {
            Forward4(s_tx[0], src, srcStride, dst, shift1, shift2);
            return;
        }

        if (sizeIdx == 1)
        {
            Forward8(s_tx[1], src, srcStride, dst, shift1, shift2);
            return;
        }

        short* rows = stackalloc short[32 * 32];
        var add1 = Vector128.Create(1 << (shift1 - 1));
        switch (sizeIdx)
        {
            case 2: ForwardRows16(s_tx[2], src, srcStride, rows, add1, (byte)shift1); break;
            default: ForwardRows32(s_tx[3], src, srcStride, rows, add1, (byte)shift1); break;
        }

        ForwardColumns(s_tx[sizeIdx], 4 << sizeIdx, rows, dst, Vector128.Create(1 << (shift2 - 1)), (byte)shift2);
    }

    /// <summary>primitives.dst4x4 (SSSE3).</summary>
    internal static void Dst4Simd(short* src, short* dst, long srcStride) => Forward4(s_dstTx, src, srcStride, dst, 1 + X265_DEPTH - 8, 8);

    // 4x4: rows r: y[r][k] = sum_j T[k][j] x[r][j]; then out[k][c] = sum_r T[k][r] y[r][c] (rows interleaved in pairs)
    private static void Forward4(TransformTables t, short* src, long srcStride, short* dst, int shift1, int shift2)
    {
        var add1 = Vector128.Create(1 << (shift1 - 1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> RowPass(short* row, TransformTables tt)
        {
            var x = Vector128.CreateScalar(*(long*)row).AsInt32();
            return Sse2.Add(MAdd(Sse2.Shuffle(x, 0x00), tt.fe), MAdd(Sse2.Shuffle(x, 0x55), tt.fe + 8));
        }

        var y01 = RoundPack(RowPass(src, t), RowPass(src + srcStride, t), add1, (byte)shift1);
        var y23 = RoundPack(RowPass(src + (2 * srcStride), t), RowPass(src + (3 * srcStride), t), add1, (byte)shift1);
        var i01 = Sse2.UnpackLow(y01, Sse2.UnpackHigh(y01.AsInt64(), y01.AsInt64()).AsInt16());
        var i23 = Sse2.UnpackLow(y23, Sse2.UnpackHigh(y23.AsInt64(), y23.AsInt64()).AsInt16());
        var add2 = Vector128.Create(1 << (shift2 - 1));
        short* f2 = t.f2;   // [k][m < 2]
        var z0 = Sse2.Add(MAdd(i01, f2), MAdd(i23, f2 + 8));
        var z1 = Sse2.Add(MAdd(i01, f2 + 16), MAdd(i23, f2 + 24));
        var z2 = Sse2.Add(MAdd(i01, f2 + 32), MAdd(i23, f2 + 40));
        var z3 = Sse2.Add(MAdd(i01, f2 + 48), MAdd(i23, f2 + 56));
        Sse2.Store(dst, RoundPack(z0, z1, add2, (byte)shift2));
        Sse2.Store(dst + 8, RoundPack(z2, z3, add2, (byte)shift2));
    }

    // 8x8 in registers. Rows: E = x[j] + x[7 - j], O = x[j] - x[7 - j]; outputs 2i from E, 2i + 1 from O, interleaved
    // back to frequency order. Columns: the rows interleaved in pairs against each frequency's coefficient pairs.
    private static void Forward8(TransformTables t, short* src, long srcStride, short* dst, int shift1, int shift2)
    {
        var add1 = Vector128.Create(1 << (shift1 - 1));
        short* fe = t.fe, fo = t.fo;
        byte sh1 = (byte)shift1;
        var y0 = Row8(src, fe, fo, add1, sh1); var y1 = Row8(src + srcStride, fe, fo, add1, sh1);
        var y2 = Row8(src + (2 * srcStride), fe, fo, add1, sh1); var y3 = Row8(src + (3 * srcStride), fe, fo, add1, sh1);
        var y4 = Row8(src + (4 * srcStride), fe, fo, add1, sh1); var y5 = Row8(src + (5 * srcStride), fe, fo, add1, sh1);
        var y6 = Row8(src + (6 * srcStride), fe, fo, add1, sh1); var y7 = Row8(src + (7 * srcStride), fe, fo, add1, sh1);
        var i0l = Sse2.UnpackLow(y0, y1); var i0h = Sse2.UnpackHigh(y0, y1);
        var i1l = Sse2.UnpackLow(y2, y3); var i1h = Sse2.UnpackHigh(y2, y3);
        var i2l = Sse2.UnpackLow(y4, y5); var i2h = Sse2.UnpackHigh(y4, y5);
        var i3l = Sse2.UnpackLow(y6, y7); var i3h = Sse2.UnpackHigh(y6, y7);
        var add2 = Vector128.Create(1 << (shift2 - 1));
        short* c = t.f2;   // [k][m < 4]
        for (int k = 0; k < 8; k++, c += 32, dst += 8)
        {
            var lo = Sse2.Add(Sse2.Add(MAdd(i0l, c), MAdd(i1l, c + 8)), Sse2.Add(MAdd(i2l, c + 16), MAdd(i3l, c + 24)));
            var hi = Sse2.Add(Sse2.Add(MAdd(i0h, c), MAdd(i1h, c + 8)), Sse2.Add(MAdd(i2h, c + 16), MAdd(i3h, c + 24)));
            Sse2.Store(dst, RoundPack(lo, hi, add2, (byte)shift2));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Row8(short* x, short* fe, short* fo, Vector128<int> add, byte shift)
    {
        var v = Sse2.LoadVector128(x);
        var rev = Ssse3.Shuffle(v.AsByte(), RevShorts).AsInt16();
        var e = Sse2.Add(v, rev).AsInt32();
        var o = Sse2.Subtract(v, rev).AsInt32();
        var ev = Sse2.Add(MAdd(Sse2.Shuffle(e, 0x00), fe), MAdd(Sse2.Shuffle(e, 0x55), fe + 8));
        var od = Sse2.Add(MAdd(Sse2.Shuffle(o, 0x00), fo), MAdd(Sse2.Shuffle(o, 0x55), fo + 8));
        var p = RoundPack(ev, od, add, shift);
        return Sse2.UnpackLow(p, Sse2.UnpackHigh(p.AsInt64(), p.AsInt64()).AsInt16());
    }

    private static void ForwardRows16(TransformTables t, short* src, long srcStride, short* rows, Vector128<int> add, byte shift)
    {
        short* fe = t.fe, fo = t.fo;   // [m < 4][v < 2]
        for (int r = 0; r < 16; r++)
        {
            short* x = src + (r * srcStride);
            var x0 = Sse2.LoadVector128(x);
            var rev = Ssse3.Shuffle(Sse2.LoadVector128(x + 8).AsByte(), RevShorts).AsInt16();
            var e = Sse2.Add(x0, rev).AsInt32();
            var o = Sse2.Subtract(x0, rev).AsInt32();
            var e0 = Sse2.Shuffle(e, 0x00); var e1 = Sse2.Shuffle(e, 0x55); var e2 = Sse2.Shuffle(e, 0xAA); var e3 = Sse2.Shuffle(e, 0xFF);
            var o0 = Sse2.Shuffle(o, 0x00); var o1 = Sse2.Shuffle(o, 0x55); var o2 = Sse2.Shuffle(o, 0xAA); var o3 = Sse2.Shuffle(o, 0xFF);
            var ev0 = Sse2.Add(Sse2.Add(MAdd(e0, fe), MAdd(e1, fe + 16)), Sse2.Add(MAdd(e2, fe + 32), MAdd(e3, fe + 48)));
            var ev1 = Sse2.Add(Sse2.Add(MAdd(e0, fe + 8), MAdd(e1, fe + 24)), Sse2.Add(MAdd(e2, fe + 40), MAdd(e3, fe + 56)));
            var od0 = Sse2.Add(Sse2.Add(MAdd(o0, fo), MAdd(o1, fo + 16)), Sse2.Add(MAdd(o2, fo + 32), MAdd(o3, fo + 48)));
            var od1 = Sse2.Add(Sse2.Add(MAdd(o0, fo + 8), MAdd(o1, fo + 24)), Sse2.Add(MAdd(o2, fo + 40), MAdd(o3, fo + 56)));
            var pe = RoundPack(ev0, ev1, add, shift);
            var po = RoundPack(od0, od1, add, shift);
            Sse2.Store(rows + (r * 16), Sse2.UnpackLow(pe, po));
            Sse2.Store(rows + (r * 16) + 8, Sse2.UnpackHigh(pe, po));
        }
    }

    // 32 points: E / O, then EE = E[j] + E[15 - j] (outputs 4i), EO = E[j] - E[15 - j] (outputs 4i + 2)
    private static void ForwardRows32(TransformTables t, short* src, long srcStride, short* rows, Vector128<int> add, byte shift)
    {
        short* fee = t.fee, feo = t.feo, fo = t.fo;
        Vector128<int>* ob = stackalloc Vector128<int>[8];
        for (int r = 0; r < 32; r++)
        {
            short* x = src + (r * srcStride);
            var x0 = Sse2.LoadVector128(x);
            var x1 = Sse2.LoadVector128(x + 8);
            var r2 = Ssse3.Shuffle(Sse2.LoadVector128(x + 16).AsByte(), RevShorts).AsInt16();
            var r3 = Ssse3.Shuffle(Sse2.LoadVector128(x + 24).AsByte(), RevShorts).AsInt16();
            var e0 = Sse2.Add(x0, r3);
            var e1 = Sse2.Add(x1, r2);
            var o0 = Sse2.Subtract(x0, r3).AsInt32();
            var o1 = Sse2.Subtract(x1, r2).AsInt32();
            var re1 = Ssse3.Shuffle(e1.AsByte(), RevShorts).AsInt16();
            var ee = Sse2.Add(e0, re1).AsInt32();
            var eo = Sse2.Subtract(e0, re1).AsInt32();

            var b0 = Sse2.Shuffle(ee, 0x00); var b1 = Sse2.Shuffle(ee, 0x55); var b2 = Sse2.Shuffle(ee, 0xAA); var b3 = Sse2.Shuffle(ee, 0xFF);
            var ee0 = Sse2.Add(Sse2.Add(MAdd(b0, fee), MAdd(b1, fee + 16)), Sse2.Add(MAdd(b2, fee + 32), MAdd(b3, fee + 48)));
            var ee1 = Sse2.Add(Sse2.Add(MAdd(b0, fee + 8), MAdd(b1, fee + 24)), Sse2.Add(MAdd(b2, fee + 40), MAdd(b3, fee + 56)));
            b0 = Sse2.Shuffle(eo, 0x00); b1 = Sse2.Shuffle(eo, 0x55); b2 = Sse2.Shuffle(eo, 0xAA); b3 = Sse2.Shuffle(eo, 0xFF);
            var eo0 = Sse2.Add(Sse2.Add(MAdd(b0, feo), MAdd(b1, feo + 16)), Sse2.Add(MAdd(b2, feo + 32), MAdd(b3, feo + 48)));
            var eo1 = Sse2.Add(Sse2.Add(MAdd(b0, feo + 8), MAdd(b1, feo + 24)), Sse2.Add(MAdd(b2, feo + 40), MAdd(b3, feo + 56)));

            // odd outputs: 8 pairs of O, 4 vectors of 4 outputs
            ob[0] = Sse2.Shuffle(o0, 0x00); ob[1] = Sse2.Shuffle(o0, 0x55); ob[2] = Sse2.Shuffle(o0, 0xAA); ob[3] = Sse2.Shuffle(o0, 0xFF);
            ob[4] = Sse2.Shuffle(o1, 0x00); ob[5] = Sse2.Shuffle(o1, 0x55); ob[6] = Sse2.Shuffle(o1, 0xAA); ob[7] = Sse2.Shuffle(o1, 0xFF);
            var od0 = Vector128<int>.Zero; var od1 = Vector128<int>.Zero; var od2 = Vector128<int>.Zero; var od3 = Vector128<int>.Zero;
            short* f = fo;
            for (int m = 0; m < 8; m++, f += 32)
            {
                var bm = ob[m];
                od0 = Sse2.Add(od0, MAdd(bm, f));
                od1 = Sse2.Add(od1, MAdd(bm, f + 8));
                od2 = Sse2.Add(od2, MAdd(bm, f + 16));
                od3 = Sse2.Add(od3, MAdd(bm, f + 24));
            }

            // frequency order: evens [ee0 eo0 ee1 eo1 ...], then evens / odds interleaved
            var pee = RoundPack(ee0, ee1, add, shift);
            var peo = RoundPack(eo0, eo1, add, shift);
            var ev0 = Sse2.UnpackLow(pee, peo);
            var ev1 = Sse2.UnpackHigh(pee, peo);
            var po0 = RoundPack(od0, od1, add, shift);
            var po1 = RoundPack(od2, od3, add, shift);
            short* row = rows + (r * 32);
            Sse2.Store(row, Sse2.UnpackLow(ev0, po0));
            Sse2.Store(row + 8, Sse2.UnpackHigh(ev0, po0));
            Sse2.Store(row + 16, Sse2.UnpackLow(ev1, po1));
            Sse2.Store(row + 24, Sse2.UnpackHigh(ev1, po1));
        }
    }

    // second pass: out[k][c] = sum_r T[k][r] y[r][c], 8 columns x 2 frequencies at a time, rows interleaved in pairs
    private static void ForwardColumns(TransformTables t, int n, short* rows, short* dst, Vector128<int> add, byte shift)
    {
        int half = n >> 1, chunks = n >> 2;
        Vector128<short>* il = stackalloc Vector128<short>[16 * 8];   // [m < n / 2][chunk < n / 4]
        for (int m = 0; m < half; m++)
        {
            short* a = rows + (2 * m * n);
            for (int c = 0; c < n; c += 8)
            {
                var ra = Sse2.LoadVector128(a + c);
                var rb = Sse2.LoadVector128(a + n + c);
                il[(m * chunks) + (c >> 2)] = Sse2.UnpackLow(ra, rb);
                il[(m * chunks) + (c >> 2) + 1] = Sse2.UnpackHigh(ra, rb);
            }
        }

        if (Avx2.IsSupported && n >= 16)
        {
            ForwardColumnsAvx2(t, n, il, dst, Vector256.Create(add.ToScalar()), shift);
            return;
        }

        for (int v = 0; v < chunks; v += 2)
        {
            for (int k = 0; k < n; k += 2)
            {
                var a00 = Vector128<int>.Zero; var a01 = Vector128<int>.Zero; var a10 = Vector128<int>.Zero; var a11 = Vector128<int>.Zero;
                short* c0 = t.f2 + (k * half * 8);
                short* c1 = c0 + (half * 8);
                Vector128<short>* p = il + v;
                for (int m = 0; m < half; m++, p += chunks, c0 += 8, c1 += 8)
                {
                    var i0 = p[0];
                    var i1 = p[1];
                    a00 = Sse2.Add(a00, MAdd(i0, c0));
                    a01 = Sse2.Add(a01, MAdd(i1, c0));
                    a10 = Sse2.Add(a10, MAdd(i0, c1));
                    a11 = Sse2.Add(a11, MAdd(i1, c1));
                }

                Sse2.Store(dst + (k * n) + (v * 4), RoundPack(a00, a01, add, shift));
                Sse2.Store(dst + ((k + 1) * n) + (v * 4), RoundPack(a10, a11, add, shift));
            }
        }
    }

    // the same with 8 interleaved column pairs per vector: chunk pairs (2w, 2w + 1) side by side
    private static void ForwardColumnsAvx2(TransformTables t, int n, Vector128<short>* il, short* dst, Vector256<int> add, byte shift)
    {
        int half = n >> 1, chunks = n >> 2;
        for (int v = 0; v < chunks; v += 4)
        {
            for (int k = 0; k < n; k += 2)
            {
                var a00 = Vector256<int>.Zero; var a01 = Vector256<int>.Zero; var a10 = Vector256<int>.Zero; var a11 = Vector256<int>.Zero;
                short* c0 = t.f2 + (k * half * 8);
                short* c1 = c0 + (half * 8);
                Vector128<short>* p = il + v;
                for (int m = 0; m < half; m++, p += chunks, c0 += 8, c1 += 8)
                {
                    // lanes: [chunk v, chunk v + 2] and [chunk v + 1, chunk v + 3], so the packs below come out in order
                    var i0 = Vector256.Create(p[0], p[2]);
                    var i1 = Vector256.Create(p[1], p[3]);
                    var w0 = Vector256.Create(Sse2.LoadAlignedVector128(c0));
                    var w1 = Vector256.Create(Sse2.LoadAlignedVector128(c1));
                    a00 = Avx2.Add(a00, Avx2.MultiplyAddAdjacent(i0, w0));
                    a01 = Avx2.Add(a01, Avx2.MultiplyAddAdjacent(i1, w0));
                    a10 = Avx2.Add(a10, Avx2.MultiplyAddAdjacent(i0, w1));
                    a11 = Avx2.Add(a11, Avx2.MultiplyAddAdjacent(i1, w1));
                }

                var r0 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(a00, add), shift), Avx2.ShiftRightArithmetic(Avx2.Add(a01, add), shift));
                var r1 = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(Avx2.Add(a10, add), shift), Avx2.ShiftRightArithmetic(Avx2.Add(a11, add), shift));
                Avx.Store(dst + (k * n) + (v * 4), r0);
                Avx.Store(dst + ((k + 1) * n) + (v * 4), r1);
            }
        }
    }

    // ---- inverse ----

    /// <summary>primitives.cu[sizeIdx].idct (n = 4 &lt;&lt; sizeIdx; SSSE3).</summary>
    internal static void IdctSimd(int sizeIdx, short* src, short* dst, long dstStride)
    {
        const int shift2 = 12 - (X265_DEPTH - 8);
        if (sizeIdx == 0)
        {
            Inverse4(s_tx[0], src, dst, dstStride, shift2);
            return;
        }

        if (sizeIdx == 1)
        {
            Inverse8(s_tx[1], src, dst, dstStride, shift2);
            return;
        }

        int n = 4 << sizeIdx;
        // extent of the nonzero coefficients: the last nonzero row, and the last nonzero column
        int lastRow = -1;
        Vector128<short>* cols = stackalloc Vector128<short>[4];
        for (int w = 0; w < (n >> 3); w++) cols[w] = Vector128<short>.Zero;
        for (int r = 0; r < n; r++)
        {
            var any = Vector128<short>.Zero;
            for (int w = 0; w < (n >> 3); w++)
            {
                var v = Sse2.LoadVector128(src + (r * n) + (8 * w));
                cols[w] = Sse2.Or(cols[w], v);
                any = Sse2.Or(any, v);
            }

            if (any != Vector128<short>.Zero)
            {
                lastRow = r;
            }
        }

        int lastCol = 0;
        for (int w = (n >> 3) - 1; w >= 0; w--)
        {
            uint nz = ~(uint)Sse2.MoveMask(Sse2.CompareEqual(cols[w], Vector128<short>.Zero).AsByte()) & 0xFFFF;
            if (nz != 0)
            {
                lastCol = (8 * w) + ((31 - System.Numerics.BitOperations.LeadingZeroCount(nz)) >> 1);
                break;
            }
        }

        short* tmp = stackalloc short[32 * 32];
        InverseColumns(s_tx[sizeIdx], n, src, tmp, lastRow);
        InverseRows(s_tx[sizeIdx], n, tmp, dst, dstStride, lastCol, shift2);
    }

    /// <summary>primitives.idst4x4 (SSSE3).</summary>
    internal static void Idst4Simd(short* src, short* dst, long dstStride) => Inverse4(s_dstTx, src, dst, dstStride, 12 - (X265_DEPTH - 8));

    // 4x4: columns c: tmp[c][b] = sum_n T[n][b] x[n][c] (rows interleaved in pairs, one vector per b over the columns);
    // then out[c][b] = sum_n T[n][b] tmp[n][c] (that vector's pairs broadcast against the coefficients of the 4 outputs)
    private static void Inverse4(TransformTables t, short* src, short* dst, long dstStride, int shift2)
    {
        var a = Sse2.LoadVector128(src);
        var b = Sse2.LoadVector128(src + 8);
        var i01 = Sse2.UnpackLow(a, Sse2.UnpackHigh(a.AsInt64(), a.AsInt64()).AsInt16());
        var i23 = Sse2.UnpackLow(b, Sse2.UnpackHigh(b.AsInt64(), b.AsInt64()).AsInt16());
        short* ie = t.ie;   // [b][p]
        var add1 = Vector128.Create(64);
        var v0 = Sse2.Add(MAdd(i01, ie), MAdd(i23, ie + 8));
        var v1 = Sse2.Add(MAdd(i01, ie + 16), MAdd(i23, ie + 24));
        var v2 = Sse2.Add(MAdd(i01, ie + 32), MAdd(i23, ie + 40));
        var v3 = Sse2.Add(MAdd(i01, ie + 48), MAdd(i23, ie + 56));
        var s01 = RoundPack(v0, v1, add1, 7).AsInt32();   // [tmp[.][0], tmp[.][1]]: pairs (tmp[0][c], tmp[1][c]), (tmp[2][c], tmp[3][c])
        var s23 = RoundPack(v2, v3, add1, 7).AsInt32();
        var add2 = Vector128.Create(1 << (shift2 - 1));
        short* g = t.ge;
        var w0 = Sse2.Add(MAdd(Sse2.Shuffle(s01, 0x00), g), MAdd(Sse2.Shuffle(s01, 0x55), g + 8));
        var w1 = Sse2.Add(MAdd(Sse2.Shuffle(s01, 0xAA), g), MAdd(Sse2.Shuffle(s01, 0xFF), g + 8));
        var w2 = Sse2.Add(MAdd(Sse2.Shuffle(s23, 0x00), g), MAdd(Sse2.Shuffle(s23, 0x55), g + 8));
        var w3 = Sse2.Add(MAdd(Sse2.Shuffle(s23, 0xAA), g), MAdd(Sse2.Shuffle(s23, 0xFF), g + 8));
        var o01 = RoundPack(w0, w1, add2, (byte)shift2).AsInt64();
        var o23 = RoundPack(w2, w3, add2, (byte)shift2).AsInt64();
        *(long*)dst = o01.ToScalar();
        *(long*)(dst + dstStride) = Sse2.UnpackHigh(o01, o01).ToScalar();
        *(long*)(dst + (2 * dstStride)) = o23.ToScalar();
        *(long*)(dst + (3 * dstStride)) = Sse2.UnpackHigh(o23, o23).ToScalar();
    }

    // 8x8 in registers: InverseColumns / InverseRows unrolled (no extent checks)
    private static void Inverse8(TransformTables t, short* src, short* dst, long dstStride, int shift2)
    {
        var p0 = Ssse3.Shuffle(Sse2.LoadVector128(src).AsByte(), EvenOddShorts).AsInt16();
        var p1 = Ssse3.Shuffle(Sse2.LoadVector128(src + 8).AsByte(), EvenOddShorts).AsInt16();
        var p2 = Ssse3.Shuffle(Sse2.LoadVector128(src + 16).AsByte(), EvenOddShorts).AsInt16();
        var p3 = Ssse3.Shuffle(Sse2.LoadVector128(src + 24).AsByte(), EvenOddShorts).AsInt16();
        var p4 = Ssse3.Shuffle(Sse2.LoadVector128(src + 32).AsByte(), EvenOddShorts).AsInt16();
        var p5 = Ssse3.Shuffle(Sse2.LoadVector128(src + 40).AsByte(), EvenOddShorts).AsInt16();
        var p6 = Ssse3.Shuffle(Sse2.LoadVector128(src + 48).AsByte(), EvenOddShorts).AsInt16();
        var p7 = Ssse3.Shuffle(Sse2.LoadVector128(src + 56).AsByte(), EvenOddShorts).AsInt16();
        var e0l = Sse2.UnpackLow(p0, p2); var e0h = Sse2.UnpackHigh(p0, p2);
        var e1l = Sse2.UnpackLow(p4, p6); var e1h = Sse2.UnpackHigh(p4, p6);
        var o0l = Sse2.UnpackLow(p1, p3); var o0h = Sse2.UnpackHigh(p1, p3);
        var o1l = Sse2.UnpackLow(p5, p7); var o1h = Sse2.UnpackHigh(p5, p7);
        var add1 = Vector128.Create(64);
        Vector128<short>* s = stackalloc Vector128<short>[8];
        short* ce = t.ie, co = t.io;   // [b < 4][m < 2]
        for (int b = 0; b < 4; b++, ce += 16, co += 16)
        {
            var el = Sse2.Add(MAdd(e0l, ce), MAdd(e1l, ce + 8));
            var eh = Sse2.Add(MAdd(e0h, ce), MAdd(e1h, ce + 8));
            var ol = Sse2.Add(MAdd(o0l, co), MAdd(o1l, co + 8));
            var oh = Sse2.Add(MAdd(o0h, co), MAdd(o1h, co + 8));
            s[b] = RoundPack(Sse2.Add(el, ol), Sse2.Add(eh, oh), add1, 7);
            s[7 - b] = RoundPack(Sse2.Subtract(el, ol), Sse2.Subtract(eh, oh), add1, 7);
        }

        var add2 = Vector128.Create(1 << (shift2 - 1));
        short* ge = t.ge, go = t.go;   // [m < 2]
        for (int r = 0; r < 8; r++, dst += dstStride)
        {
            var v = s[r].AsInt32();
            var e = Sse2.Add(MAdd(Sse2.Shuffle(v, 0x00), ge), MAdd(Sse2.Shuffle(v, 0x55), ge + 8));
            var o = Sse2.Add(MAdd(Sse2.Shuffle(v, 0xAA), go), MAdd(Sse2.Shuffle(v, 0xFF), go + 8));
            Sse2.Store(dst, RoundPack(Sse2.Add(e, o), Sse2.Shuffle(Sse2.Subtract(e, o), 0x1B), add2, (byte)shift2));
        }
    }

    // a coefficient row with its columns reordered evens, then odds (so the second pass's butterfly pairs are adjacent)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PermuteRow(short* row, int n, Vector128<short>* outp)
    {
        if (n == 8)
        {
            outp[0] = Ssse3.Shuffle(Sse2.LoadVector128(row).AsByte(), EvenOddShorts).AsInt16();
            return;
        }

        int h = n >> 4;
        for (int i = 0; i < h; i++)
        {
            var p0 = Ssse3.Shuffle(Sse2.LoadVector128(row + (16 * i)).AsByte(), EvenOddShorts).AsInt64();
            var p1 = Ssse3.Shuffle(Sse2.LoadVector128(row + (16 * i) + 8).AsByte(), EvenOddShorts).AsInt64();
            outp[i] = Sse2.UnpackLow(p0, p1).AsInt16();
            outp[h + i] = Sse2.UnpackHigh(p0, p1).AsInt16();
        }
    }

    // first pass: for each output b < n / 2 and 8 (permuted) columns at a time, E from the even coefficient rows
    // (pairs 4m, 4m + 2), O from the odd ones (4m + 1, 4m + 3); tmp row b = E + O, row n - 1 - b = E - O
    private static void InverseColumns(TransformTables t, int n, short* src, short* tmp, int lastRow)
    {
        int vecs = n >> 3, chunks = n >> 2, quarter = n >> 2;
        int mE = lastRow < 0 ? 0 : (lastRow >> 2) + 1;
        int mO = lastRow < 1 ? 0 : ((lastRow - 1) >> 2) + 1;
        Vector128<short>* pr = stackalloc Vector128<short>[4 * 4];   // 4 permuted rows
        Vector128<short>* ie = stackalloc Vector128<short>[8 * 8];   // [m][chunk]
        Vector128<short>* io = stackalloc Vector128<short>[8 * 8];
        int mMax = Math.Max(mE, mO);
        for (int m = 0; m < mMax; m++)
        {
            for (int q = 0; q < 4; q++)
            {
                int r = (4 * m) + q;
                if (r <= lastRow)
                {
                    PermuteRow(src + (r * n), n, pr + (q * 4));
                }
                else
                {
                    for (int w = 0; w < vecs; w++) pr[(q * 4) + w] = Vector128<short>.Zero;
                }
            }

            for (int w = 0; w < vecs; w++)
            {
                ie[(m * chunks) + (2 * w)] = Sse2.UnpackLow(pr[w], pr[8 + w]);
                ie[(m * chunks) + (2 * w) + 1] = Sse2.UnpackHigh(pr[w], pr[8 + w]);
                io[(m * chunks) + (2 * w)] = Sse2.UnpackLow(pr[4 + w], pr[12 + w]);
                io[(m * chunks) + (2 * w) + 1] = Sse2.UnpackHigh(pr[4 + w], pr[12 + w]);
            }
        }

        var add = Vector128.Create(64);
        int half = n >> 1;
        for (int b = 0; b < half; b++)
        {
            short* ce = t.ie + (b * quarter * 8);
            short* co = t.io + (b * quarter * 8);
            for (int v = 0; v < chunks; v += 2)
            {
                var e0 = Vector128<int>.Zero; var e1 = Vector128<int>.Zero; var o0 = Vector128<int>.Zero; var o1 = Vector128<int>.Zero;
                for (int m = 0; m < mE; m++)
                {
                    e0 = Sse2.Add(e0, MAdd(ie[(m * chunks) + v], ce + (m * 8)));
                    e1 = Sse2.Add(e1, MAdd(ie[(m * chunks) + v + 1], ce + (m * 8)));
                }

                for (int m = 0; m < mO; m++)
                {
                    o0 = Sse2.Add(o0, MAdd(io[(m * chunks) + v], co + (m * 8)));
                    o1 = Sse2.Add(o1, MAdd(io[(m * chunks) + v + 1], co + (m * 8)));
                }

                Sse2.Store(tmp + (b * n) + (v * 4), RoundPack(Sse2.Add(e0, o0), Sse2.Add(e1, o1), add, 7));
                Sse2.Store(tmp + ((n - 1 - b) * n) + (v * 4), RoundPack(Sse2.Subtract(e0, o0), Sse2.Subtract(e1, o1), add, 7));
            }
        }
    }

    // second pass: each tmp row (columns permuted evens | odds) to an output row: its pairs broadcast against the
    // coefficients of outputs b < n / 2 (E from the even pairs, O from the odd), b and n - 1 - b from E + O and E - O
    private static void InverseRows(TransformTables t, int n, short* tmp, short* dst, long dstStride, int lastCol, int shift)
    {
        int quarter = n >> 2, outChunks = n >> 3;
        int cE = (lastCol >> 2) + 1;
        int cO = lastCol < 1 ? 0 : ((lastCol - 1) >> 2) + 1;
        var add = Vector128.Create(1 << (shift - 1));
        byte sh = (byte)shift;
        for (int r = 0; r < n; r++)
        {
            int* row = (int*)(tmp + (r * n));
            short* d = dst + (r * dstStride);
            if (outChunks == 1)
            {
                var e = Vector128<int>.Zero;
                var o = Vector128<int>.Zero;
                for (int m = 0; m < cE; m++) e = Sse2.Add(e, MAdd(Vector128.Create(row[m]), t.ge + (m * 8)));
                for (int m = 0; m < cO; m++) o = Sse2.Add(o, MAdd(Vector128.Create(row[quarter + m]), t.go + (m * 8)));
                Sse2.Store(d, RoundPack(Sse2.Add(e, o), Sse2.Shuffle(Sse2.Subtract(e, o), 0x1B), add, sh));
            }
            else if (outChunks == 2)
            {
                var e0 = Vector128<int>.Zero; var e1 = Vector128<int>.Zero; var o0 = Vector128<int>.Zero; var o1 = Vector128<int>.Zero;
                short* g = t.ge;
                for (int m = 0; m < cE; m++, g += 16)
                {
                    var b = Vector128.Create(row[m]);
                    e0 = Sse2.Add(e0, MAdd(b, g));
                    e1 = Sse2.Add(e1, MAdd(b, g + 8));
                }

                g = t.go;
                for (int m = 0; m < cO; m++, g += 16)
                {
                    var b = Vector128.Create(row[quarter + m]);
                    o0 = Sse2.Add(o0, MAdd(b, g));
                    o1 = Sse2.Add(o1, MAdd(b, g + 8));
                }

                Sse2.Store(d, RoundPack(Sse2.Add(e0, o0), Sse2.Add(e1, o1), add, sh));
                Sse2.Store(d + 8, RoundPack(Sse2.Shuffle(Sse2.Subtract(e1, o1), 0x1B), Sse2.Shuffle(Sse2.Subtract(e0, o0), 0x1B), add, sh));
            }
            else
            {
                var e0 = Vector128<int>.Zero; var e1 = Vector128<int>.Zero; var e2 = Vector128<int>.Zero; var e3 = Vector128<int>.Zero;
                var o0 = Vector128<int>.Zero; var o1 = Vector128<int>.Zero; var o2 = Vector128<int>.Zero; var o3 = Vector128<int>.Zero;
                short* g = t.ge;
                for (int m = 0; m < cE; m++, g += 32)
                {
                    var b = Vector128.Create(row[m]);
                    e0 = Sse2.Add(e0, MAdd(b, g));
                    e1 = Sse2.Add(e1, MAdd(b, g + 8));
                    e2 = Sse2.Add(e2, MAdd(b, g + 16));
                    e3 = Sse2.Add(e3, MAdd(b, g + 24));
                }

                g = t.go;
                for (int m = 0; m < cO; m++, g += 32)
                {
                    var b = Vector128.Create(row[quarter + m]);
                    o0 = Sse2.Add(o0, MAdd(b, g));
                    o1 = Sse2.Add(o1, MAdd(b, g + 8));
                    o2 = Sse2.Add(o2, MAdd(b, g + 16));
                    o3 = Sse2.Add(o3, MAdd(b, g + 24));
                }

                Sse2.Store(d, RoundPack(Sse2.Add(e0, o0), Sse2.Add(e1, o1), add, sh));
                Sse2.Store(d + 8, RoundPack(Sse2.Add(e2, o2), Sse2.Add(e3, o3), add, sh));
                Sse2.Store(d + 16, RoundPack(Sse2.Shuffle(Sse2.Subtract(e3, o3), 0x1B), Sse2.Shuffle(Sse2.Subtract(e2, o2), 0x1B), add, sh));
                Sse2.Store(d + 24, RoundPack(Sse2.Shuffle(Sse2.Subtract(e1, o1), 0x1B), Sse2.Shuffle(Sse2.Subtract(e0, o0), 0x1B), add, sh));
            }
        }
    }
}
