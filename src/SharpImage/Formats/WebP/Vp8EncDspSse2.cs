using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.WebP;

/// <summary>
/// libwebp 1.6.0 src/dsp/enc_sse2.c kernels (the ones cwebp runs on x86-64), ported with .NET SSE2 intrinsics. They
/// compute the same integers as the C versions in Vp8EncDsp.cs, which remain the fallback where SSE2 is missing.
/// </summary>
internal static partial class Vp8EncDsp
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadL64(byte[] a, int o) =>
        Vector128.CreateScalar(Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), o))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load32(byte[] a, int o) =>
        Vector128.CreateScalar(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), o))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load128(byte[] a, int o) =>
        Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), o));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreL64(byte[] a, int o, Vector128<byte> v) =>
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), o), v.AsInt64().ToScalar());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store32(byte[] a, int o, int v) =>
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), o), v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadS(ReadOnlySpan<short> s, int o) =>
        Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref MemoryMarshal.GetReference(s), o)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreS(Span<short> s, int o, Vector128<short> v) =>
        Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref MemoryMarshal.GetReference(s), o)), v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadU16(ushort[] s, int o) =>
        Unsafe.ReadUnaligned<Vector128<short>>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), o)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> LoadU32(uint[] s, int o) =>
        Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), o)));

    private static byte Shuf(int a, int b, int c, int d) => (byte)((a << 6) | (b << 4) | (c << 2) | d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HSum(Vector128<int> v) => v.GetElement(3) + v.GetElement(2) + v.GetElement(1) + v.GetElement(0);

    // VP8Transpose_2_4x4_16b (common_sse2.h)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose2x4x4(Vector128<short> in0, Vector128<short> in1, Vector128<short> in2,
        Vector128<short> in3, out Vector128<short> out0, out Vector128<short> out1, out Vector128<short> out2,
        out Vector128<short> out3)
    {
        var t00 = Sse2.UnpackLow(in0, in1);
        var t01 = Sse2.UnpackLow(in2, in3);
        var t02 = Sse2.UnpackHigh(in0, in1);
        var t03 = Sse2.UnpackHigh(in2, in3);
        var t10 = Sse2.UnpackLow(t00.AsInt32(), t01.AsInt32());
        var t11 = Sse2.UnpackLow(t02.AsInt32(), t03.AsInt32());
        var t12 = Sse2.UnpackHigh(t00.AsInt32(), t01.AsInt32());
        var t13 = Sse2.UnpackHigh(t02.AsInt32(), t03.AsInt32());
        out0 = Sse2.UnpackLow(t10.AsInt64(), t11.AsInt64()).AsInt16();
        out1 = Sse2.UnpackHigh(t10.AsInt64(), t11.AsInt64()).AsInt16();
        out2 = Sse2.UnpackLow(t12.AsInt64(), t13.AsInt64()).AsInt16();
        out3 = Sse2.UnpackHigh(t12.AsInt64(), t13.AsInt64()).AsInt16();
    }

    // ---- inverse transform ---------------------------------------------------------------------------------------------

    private static void ITransformOneSse2(byte[] refBuf, int r, ReadOnlySpan<short> input, byte[] dst, int d)
    {
        var k1k2 = Vector128.Create((short)20091, 20091, 20091, 20091, -30068, -30068, -30068, -30068);
        var k2k1 = Vector128.Create((short)-30068, -30068, -30068, -30068, 20091, 20091, 20091, 20091);
        var zeroFour = Vector128.Create((short)4, 4, 4, 4, 0, 0, 0, 0);
        Vector128<short> T01, T23;
        var in01 = LoadS(input, 0);
        var in23 = LoadS(input, 8);
        {
            var in1 = Sse2.UnpackHigh(in01.AsInt64(), in01.AsInt64()).AsInt16();
            var in3 = Sse2.UnpackHigh(in23.AsInt64(), in23.AsInt64()).AsInt16();
            var aD3 = Sse2.Add(in01, in23);
            var bC3 = Sse2.Subtract(in01, in23);
            var c1d1 = Sse2.MultiplyHigh(in1, k2k1);
            var c2d2 = Sse2.MultiplyHigh(in3, k1k2);
            var c3 = Sse2.UnpackHigh(bC3.AsInt64(), bC3.AsInt64()).AsInt16();
            var c4 = Sse2.Subtract(c1d1, c2d2);
            var c = Sse2.Add(c3, c4);
            var d4u = Sse2.Add(c1d1, c2d2);
            var du = Sse2.Add(aD3, d4u);
            var dd = Sse2.UnpackHigh(du.AsInt64(), du.AsInt64()).AsInt16();
            var combAb = Sse2.UnpackLow(aD3.AsInt64(), bC3.AsInt64()).AsInt16();
            var combDc = Sse2.UnpackLow(dd.AsInt64(), c.AsInt64()).AsInt16();
            var tmp01 = Sse2.Add(combAb, combDc);
            var tmp32 = Sse2.Subtract(combAb, combDc);
            var tmp23 = Sse2.Shuffle(tmp32.AsInt32(), Shuf(1, 0, 3, 2)).AsInt16();
            var transpose0 = Sse2.UnpackLow(tmp01, tmp23);
            var transpose1 = Sse2.UnpackHigh(tmp01, tmp23);
            T01 = Sse2.UnpackLow(transpose0, transpose1);
            T23 = Sse2.UnpackHigh(transpose0, transpose1);
        }
        {
            var T1 = Sse2.UnpackHigh(T01.AsInt64(), T01.AsInt64()).AsInt16();
            var T3 = Sse2.UnpackHigh(T23.AsInt64(), T23.AsInt64()).AsInt16();
            var dc = Sse2.Add(T01, zeroFour);
            var aD3 = Sse2.Add(dc, T23);
            var bC3 = Sse2.Subtract(dc, T23);
            var c1d1 = Sse2.MultiplyHigh(T1, k2k1);
            var c2d2 = Sse2.MultiplyHigh(T3, k1k2);
            var c3 = Sse2.UnpackHigh(bC3.AsInt64(), bC3.AsInt64()).AsInt16();
            var c4 = Sse2.Subtract(c1d1, c2d2);
            var c = Sse2.Add(c3, c4);
            var d4u = Sse2.Add(c1d1, c2d2);
            var du = Sse2.Add(aD3, d4u);
            var dd = Sse2.UnpackHigh(du.AsInt64(), du.AsInt64()).AsInt16();
            var combAb = Sse2.UnpackLow(aD3.AsInt64(), bC3.AsInt64()).AsInt16();
            var combDc = Sse2.UnpackLow(dd.AsInt64(), c.AsInt64()).AsInt16();
            var tmp01 = Sse2.Add(combAb, combDc);
            var tmp32 = Sse2.Subtract(combAb, combDc);
            var tmp23 = Sse2.Shuffle(tmp32.AsInt32(), Shuf(1, 0, 3, 2)).AsInt16();
            var shifted01 = Sse2.ShiftRightArithmetic(tmp01, 3);
            var shifted23 = Sse2.ShiftRightArithmetic(tmp23, 3);
            var transpose0 = Sse2.UnpackLow(shifted01, shifted23);
            var transpose1 = Sse2.UnpackHigh(shifted01, shifted23);
            T01 = Sse2.UnpackLow(transpose0, transpose1);
            T23 = Sse2.UnpackHigh(transpose0, transpose1);
        }
        {
            var zero = Vector128<byte>.Zero;
            var ref01 = Sse2.UnpackLow(Load32(refBuf, r + 0 * Bps).AsInt32(), Load32(refBuf, r + 1 * Bps).AsInt32()).AsByte();
            var ref23 = Sse2.UnpackLow(Load32(refBuf, r + 2 * Bps).AsInt32(), Load32(refBuf, r + 3 * Bps).AsInt32()).AsByte();
            var r01 = Sse2.Add(Sse2.UnpackLow(ref01, zero).AsInt16(), T01);
            var r23 = Sse2.Add(Sse2.UnpackLow(ref23, zero).AsInt16(), T23);
            var ref0123 = Sse2.PackUnsignedSaturate(r01, r23).AsInt32();
            Store32(dst, d + 0 * Bps, ref0123.GetElement(0));
            Store32(dst, d + 1 * Bps, ref0123.GetElement(1));
            Store32(dst, d + 2 * Bps, ref0123.GetElement(2));
            Store32(dst, d + 3 * Bps, ref0123.GetElement(3));
        }
    }

    private static void ITransformTwoSse2(byte[] refBuf, int r, ReadOnlySpan<short> input, byte[] dst, int d)
    {
        var k1 = Vector128.Create((short)20091);
        var k2 = Vector128.Create((short)-30068);
        Vector128<short> T0, T1, T2, T3;
        Vector128<short> in0, in1, in2, in3;
        {
            var tmp0 = LoadS(input, 0);
            var tmp1 = LoadS(input, 8);
            var tmp2 = LoadS(input, 16);
            var tmp3 = LoadS(input, 24);
            in0 = Sse2.UnpackLow(tmp0.AsInt64(), tmp2.AsInt64()).AsInt16();
            in1 = Sse2.UnpackHigh(tmp0.AsInt64(), tmp2.AsInt64()).AsInt16();
            in2 = Sse2.UnpackLow(tmp1.AsInt64(), tmp3.AsInt64()).AsInt16();
            in3 = Sse2.UnpackHigh(tmp1.AsInt64(), tmp3.AsInt64()).AsInt16();
        }
        {
            var a = Sse2.Add(in0, in2);
            var b = Sse2.Subtract(in0, in2);
            var c1 = Sse2.MultiplyHigh(in1, k2);
            var c2 = Sse2.MultiplyHigh(in3, k1);
            var c3 = Sse2.Subtract(in1, in3);
            var c4 = Sse2.Subtract(c1, c2);
            var c = Sse2.Add(c3, c4);
            var d1 = Sse2.MultiplyHigh(in1, k1);
            var d2 = Sse2.MultiplyHigh(in3, k2);
            var d3 = Sse2.Add(in1, in3);
            var d4 = Sse2.Add(d1, d2);
            var dd = Sse2.Add(d3, d4);
            Transpose2x4x4(Sse2.Add(a, dd), Sse2.Add(b, c), Sse2.Subtract(b, c), Sse2.Subtract(a, dd),
                out T0, out T1, out T2, out T3);
        }
        {
            var four = Vector128.Create((short)4);
            var dc = Sse2.Add(T0, four);
            var a = Sse2.Add(dc, T2);
            var b = Sse2.Subtract(dc, T2);
            var c1 = Sse2.MultiplyHigh(T1, k2);
            var c2 = Sse2.MultiplyHigh(T3, k1);
            var c3 = Sse2.Subtract(T1, T3);
            var c4 = Sse2.Subtract(c1, c2);
            var c = Sse2.Add(c3, c4);
            var d1 = Sse2.MultiplyHigh(T1, k1);
            var d2 = Sse2.MultiplyHigh(T3, k2);
            var d3 = Sse2.Add(T1, T3);
            var d4 = Sse2.Add(d1, d2);
            var dd = Sse2.Add(d3, d4);
            Transpose2x4x4(Sse2.ShiftRightArithmetic(Sse2.Add(a, dd), 3), Sse2.ShiftRightArithmetic(Sse2.Add(b, c), 3),
                Sse2.ShiftRightArithmetic(Sse2.Subtract(b, c), 3), Sse2.ShiftRightArithmetic(Sse2.Subtract(a, dd), 3),
                out T0, out T1, out T2, out T3);
        }
        {
            var zero = Vector128<byte>.Zero;
            var ref0 = Sse2.Add(Sse2.UnpackLow(LoadL64(refBuf, r + 0 * Bps), zero).AsInt16(), T0);
            var ref1 = Sse2.Add(Sse2.UnpackLow(LoadL64(refBuf, r + 1 * Bps), zero).AsInt16(), T1);
            var ref2 = Sse2.Add(Sse2.UnpackLow(LoadL64(refBuf, r + 2 * Bps), zero).AsInt16(), T2);
            var ref3 = Sse2.Add(Sse2.UnpackLow(LoadL64(refBuf, r + 3 * Bps), zero).AsInt16(), T3);
            StoreL64(dst, d + 0 * Bps, Sse2.PackUnsignedSaturate(ref0, ref0));
            StoreL64(dst, d + 1 * Bps, Sse2.PackUnsignedSaturate(ref1, ref1));
            StoreL64(dst, d + 2 * Bps, Sse2.PackUnsignedSaturate(ref2, ref2));
            StoreL64(dst, d + 3 * Bps, Sse2.PackUnsignedSaturate(ref3, ref3));
        }
    }

    // ---- forward transform ---------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FTransformPass1Sse2(Vector128<short> in01, Vector128<short> in23, out Vector128<short> out01,
        out Vector128<short> out32)
    {
        var k937 = Vector128.Create(937);
        var k1812 = Vector128.Create(1812);
        var k88p = Vector128.Create((short)8);
        var k88m = Vector128.Create((short)8, -8, 8, -8, 8, -8, 8, -8);
        var k5352_2217p = Vector128.Create((short)5352, 2217, 5352, 2217, 5352, 2217, 5352, 2217);
        var k5352_2217m = Vector128.Create((short)2217, -5352, 2217, -5352, 2217, -5352, 2217, -5352);

        var shuf01p = Sse2.ShuffleHigh(in01, Shuf(2, 3, 0, 1));
        var shuf23p = Sse2.ShuffleHigh(in23, Shuf(2, 3, 0, 1));
        var s01 = Sse2.UnpackLow(shuf01p.AsInt64(), shuf23p.AsInt64()).AsInt16();
        var s32 = Sse2.UnpackHigh(shuf01p.AsInt64(), shuf23p.AsInt64()).AsInt16();
        var a01 = Sse2.Add(s01, s32);
        var a32 = Sse2.Subtract(s01, s32);

        var tmp0 = Sse2.MultiplyAddAdjacent(a01, k88p);  // [ (a0 + a1) << 3, ... ]
        var tmp2 = Sse2.MultiplyAddAdjacent(a01, k88m);  // [ (a0 - a1) << 3, ... ]
        var tmp11 = Sse2.MultiplyAddAdjacent(a32, k5352_2217p);
        var tmp31 = Sse2.MultiplyAddAdjacent(a32, k5352_2217m);
        var tmp12 = Sse2.Add(tmp11, k1812);
        var tmp32 = Sse2.Add(tmp31, k937);
        var tmp1 = Sse2.ShiftRightArithmetic(tmp12, 9);
        var tmp3 = Sse2.ShiftRightArithmetic(tmp32, 9);
        var s03 = Sse2.PackSignedSaturate(tmp0, tmp2);
        var s12 = Sse2.PackSignedSaturate(tmp1, tmp3);
        var sLo = Sse2.UnpackLow(s03, s12);   // 0 1 0 1 0 1...
        var sHi = Sse2.UnpackHigh(s03, s12);  // 2 3 2 3 2 3
        var v23 = Sse2.UnpackHigh(sLo.AsInt32(), sHi.AsInt32());
        out01 = Sse2.UnpackLow(sLo.AsInt32(), sHi.AsInt32()).AsInt16();
        out32 = Sse2.Shuffle(v23, Shuf(1, 0, 3, 2)).AsInt16();  // 3 2 3 2 3 2..
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FTransformPass2Sse2(Vector128<short> v01, Vector128<short> v32, Span<short> output, int o)
    {
        var zero = Vector128<short>.Zero;
        var seven = Vector128.Create((short)7);
        var k5352_2217 = Vector128.Create((short)2217, 5352, 2217, 5352, 2217, 5352, 2217, 5352);
        var k2217_5352 = Vector128.Create((short)-5352, 2217, -5352, 2217, -5352, 2217, -5352, 2217);
        var k12000PlusOne = Vector128.Create(12000 + (1 << 16));
        var k51000 = Vector128.Create(51000);

        // Same operations are done on the (0,3) and (1,2) pairs.
        // a3 = v0 - v3
        // a2 = v1 - v2
        var a32 = Sse2.Subtract(v01, v32);
        var a22 = Sse2.UnpackHigh(a32.AsInt64(), a32.AsInt64()).AsInt16();

        var b23 = Sse2.UnpackLow(a22, a32);
        var c1 = Sse2.MultiplyAddAdjacent(b23, k5352_2217);
        var c3 = Sse2.MultiplyAddAdjacent(b23, k2217_5352);
        var d1 = Sse2.Add(c1, k12000PlusOne);
        var d3 = Sse2.Add(c3, k51000);
        var e1 = Sse2.ShiftRightArithmetic(d1, 16);
        var e3 = Sse2.ShiftRightArithmetic(d3, 16);
        var f1 = Sse2.PackSignedSaturate(e1, e1);
        var f3 = Sse2.PackSignedSaturate(e3, e3);
        // g1 = f1 + (a3 != 0);
        var g1 = Sse2.Add(f1, Sse2.CompareEqual(a32, zero));

        // a0 = v0 + v3
        // a1 = v1 + v2
        var a01 = Sse2.Add(v01, v32);
        var a01Plus7 = Sse2.Add(a01, seven);
        var a11 = Sse2.UnpackHigh(a01.AsInt64(), a01.AsInt64()).AsInt16();
        var c0 = Sse2.Add(a01Plus7, a11);
        var c2 = Sse2.Subtract(a01Plus7, a11);
        // d0 = (a0 + a1 + 7) >> 4;
        // d2 = (a0 - a1 + 7) >> 4;
        var d0 = Sse2.ShiftRightArithmetic(c0, 4);
        var d2 = Sse2.ShiftRightArithmetic(c2, 4);

        StoreS(output, o + 0, Sse2.UnpackLow(d0.AsInt64(), g1.AsInt64()).AsInt16());
        StoreS(output, o + 8, Sse2.UnpackLow(d2.AsInt64(), f3.AsInt64()).AsInt16());
    }

    private static void FTransformSse2(byte[] src, int s, byte[] refBuf, int r, Span<short> output)
    {
        var zero = Vector128<byte>.Zero;
        // Load src.
        var src0 = LoadL64(src, s + 0 * Bps);
        var src1 = LoadL64(src, s + 1 * Bps);
        var src2 = LoadL64(src, s + 2 * Bps);
        var src3 = LoadL64(src, s + 3 * Bps);
        // 00 01 02 03 *
        // 10 11 12 13 *
        // 20 21 22 23 *
        // 30 31 32 33 *
        // Shuffle.
        var srcA = Sse2.UnpackLow(src0.AsInt16(), src1.AsInt16()).AsByte();
        var srcB = Sse2.UnpackLow(src2.AsInt16(), src3.AsInt16()).AsByte();
        // Load ref.
        var ref0 = LoadL64(refBuf, r + 0 * Bps);
        var ref1 = LoadL64(refBuf, r + 1 * Bps);
        var ref2 = LoadL64(refBuf, r + 2 * Bps);
        var ref3 = LoadL64(refBuf, r + 3 * Bps);
        var refA = Sse2.UnpackLow(ref0.AsInt16(), ref1.AsInt16()).AsByte();
        var refB = Sse2.UnpackLow(ref2.AsInt16(), ref3.AsInt16()).AsByte();
        // Convert both to 16 bit, compute the difference.
        var row01 = Sse2.Subtract(Sse2.UnpackLow(srcA, zero).AsInt16(), Sse2.UnpackLow(refA, zero).AsInt16());
        var row23 = Sse2.Subtract(Sse2.UnpackLow(srcB, zero).AsInt16(), Sse2.UnpackLow(refB, zero).AsInt16());

        FTransformPass1Sse2(row01, row23, out var v01, out var v32);
        FTransformPass2Sse2(v01, v32, output, 0);
    }

    private static void FTransform2Sse2(byte[] src, int s, byte[] refBuf, int r, Span<short> output)
    {
        var zero = Vector128<byte>.Zero;
        // Load src and ref, convert to 16b, compute difference -> 00 01 02 03  00' 01' 02' 03'
        var diff0 = Sse2.Subtract(Sse2.UnpackLow(LoadL64(src, s + 0 * Bps), zero).AsInt16(),
            Sse2.UnpackLow(LoadL64(refBuf, r + 0 * Bps), zero).AsInt16());
        var diff1 = Sse2.Subtract(Sse2.UnpackLow(LoadL64(src, s + 1 * Bps), zero).AsInt16(),
            Sse2.UnpackLow(LoadL64(refBuf, r + 1 * Bps), zero).AsInt16());
        var diff2 = Sse2.Subtract(Sse2.UnpackLow(LoadL64(src, s + 2 * Bps), zero).AsInt16(),
            Sse2.UnpackLow(LoadL64(refBuf, r + 2 * Bps), zero).AsInt16());
        var diff3 = Sse2.Subtract(Sse2.UnpackLow(LoadL64(src, s + 3 * Bps), zero).AsInt16(),
            Sse2.UnpackLow(LoadL64(refBuf, r + 3 * Bps), zero).AsInt16());

        // Unpack and shuffle
        var shuf01l = Sse2.UnpackLow(diff0.AsInt32(), diff1.AsInt32()).AsInt16();
        var shuf23l = Sse2.UnpackLow(diff2.AsInt32(), diff3.AsInt32()).AsInt16();
        var shuf01h = Sse2.UnpackHigh(diff0.AsInt32(), diff1.AsInt32()).AsInt16();
        var shuf23h = Sse2.UnpackHigh(diff2.AsInt32(), diff3.AsInt32()).AsInt16();

        FTransformPass1Sse2(shuf01l, shuf23l, out var v01l, out var v32l);
        FTransformPass1Sse2(shuf01h, shuf23h, out var v01h, out var v32h);

        FTransformPass2Sse2(v01l, v32l, output, 0);
        FTransformPass2Sse2(v01h, v32h, output, 16);
    }

    // ---- metrics -------------------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SubtractAndAccumulateSse2(Vector128<byte> a, Vector128<byte> b)
    {
        // take abs(a-b) in 8b
        var aB = Sse2.SubtractSaturate(a, b);
        var bA = Sse2.SubtractSaturate(b, a);
        var absAB = Sse2.Or(aB, bA);
        // zero-extend to 16b
        var zero = Vector128<byte>.Zero;
        var c0 = Sse2.UnpackLow(absAB, zero).AsInt16();
        var c1 = Sse2.UnpackHigh(absAB, zero).AsInt16();
        // multiply with self
        return Sse2.Add(Sse2.MultiplyAddAdjacent(c0, c0), Sse2.MultiplyAddAdjacent(c1, c1));
    }

    private static int Sse16xNSse2(byte[] a, int ao, byte[] b, int bo, int numPairs)
    {
        var sum = Vector128<int>.Zero;
        for (int i = 0; i < numPairs; ++i)
        {
            var a0 = Load128(a, ao + Bps * 0);
            var b0 = Load128(b, bo + Bps * 0);
            var a1 = Load128(a, ao + Bps * 1);
            var b1 = Load128(b, bo + Bps * 1);
            var sum1 = SubtractAndAccumulateSse2(a0, b0);
            var sum2 = SubtractAndAccumulateSse2(a1, b1);
            sum = Sse2.Add(sum, Sse2.Add(sum1, sum2));
            ao += 2 * Bps;
            bo += 2 * Bps;
        }
        return HSum(sum);
    }

    private static int Sse8x8Sse2(byte[] a, int ao, byte[] b, int bo)
    {
        var zero = Vector128<byte>.Zero;
        int numPairs = 4;
        var sum = Vector128<int>.Zero;
        while (numPairs-- > 0)
        {
            var a0 = Sse2.UnpackLow(LoadL64(a, ao + Bps * 0), zero).AsInt16();
            var a1 = Sse2.UnpackLow(LoadL64(a, ao + Bps * 1), zero).AsInt16();
            var b0 = Sse2.UnpackLow(LoadL64(b, bo + Bps * 0), zero).AsInt16();
            var b1 = Sse2.UnpackLow(LoadL64(b, bo + Bps * 1), zero).AsInt16();
            var c0 = Sse2.SubtractSaturate(a0, b0);
            var c1 = Sse2.SubtractSaturate(a1, b1);
            var d0 = Sse2.MultiplyAddAdjacent(c0, c0);
            var d1 = Sse2.MultiplyAddAdjacent(c1, c1);
            sum = Sse2.Add(sum, Sse2.Add(d0, d1));
            ao += 2 * Bps;
            bo += 2 * Bps;
        }
        return HSum(sum);
    }

    private static int Sse4x4Sse2(byte[] a, int ao, byte[] b, int bo)
    {
        var zero = Vector128<byte>.Zero;
        var a01 = Sse2.UnpackLow(LoadL64(a, ao + Bps * 0).AsInt32(), LoadL64(a, ao + Bps * 1).AsInt32()).AsByte();
        var a23 = Sse2.UnpackLow(LoadL64(a, ao + Bps * 2).AsInt32(), LoadL64(a, ao + Bps * 3).AsInt32()).AsByte();
        var b01 = Sse2.UnpackLow(LoadL64(b, bo + Bps * 0).AsInt32(), LoadL64(b, bo + Bps * 1).AsInt32()).AsByte();
        var b23 = Sse2.UnpackLow(LoadL64(b, bo + Bps * 2).AsInt32(), LoadL64(b, bo + Bps * 3).AsInt32()).AsByte();
        var d0 = Sse2.SubtractSaturate(Sse2.UnpackLow(a01, zero).AsInt16(), Sse2.UnpackLow(b01, zero).AsInt16());
        var d1 = Sse2.SubtractSaturate(Sse2.UnpackLow(a23, zero).AsInt16(), Sse2.UnpackLow(b23, zero).AsInt16());
        var sum = Sse2.Add(Sse2.MultiplyAddAdjacent(d0, d0), Sse2.MultiplyAddAdjacent(d1, d1));
        return HSum(sum);
    }

    // ---- texture distortion --------------------------------------------------------------------------------------------

    private static int TTransformSse2(byte[] inA, int ao, byte[] inB, int bo, ushort[] w)
    {
        var zero = Vector128<byte>.Zero;
        Vector128<short> tmp0, tmp1, tmp2, tmp3;

        // Load and combine inputs.
        {
            var inAB0 = Sse2.UnpackLow(LoadL64(inA, ao + Bps * 0).AsInt32(), LoadL64(inB, bo + Bps * 0).AsInt32()).AsByte();
            var inAB1 = Sse2.UnpackLow(LoadL64(inA, ao + Bps * 1).AsInt32(), LoadL64(inB, bo + Bps * 1).AsInt32()).AsByte();
            var inAB2 = Sse2.UnpackLow(LoadL64(inA, ao + Bps * 2).AsInt32(), LoadL64(inB, bo + Bps * 2).AsInt32()).AsByte();
            var inAB3 = Sse2.UnpackLow(LoadL64(inA, ao + Bps * 3).AsInt32(), LoadL64(inB, bo + Bps * 3).AsInt32()).AsByte();
            tmp0 = Sse2.UnpackLow(inAB0, zero).AsInt16();
            tmp1 = Sse2.UnpackLow(inAB1, zero).AsInt16();
            tmp2 = Sse2.UnpackLow(inAB2, zero).AsInt16();
            tmp3 = Sse2.UnpackLow(inAB3, zero).AsInt16();
            // a00 a01 a02 a03   b00 b01 b02 b03
            // a10 a11 a12 a13   b10 b11 b12 b13
            // a20 a21 a22 a23   b20 b21 b22 b23
            // a30 a31 a32 a33   b30 b31 b32 b33
        }

        // Vertical pass first to avoid a transpose (vertical and horizontal passes
        // are commutative because w/kWeightY is symmetric) and subsequent transpose.
        {
            // Calculate a and b (two 4x4 at once).
            var a0 = Sse2.Add(tmp0, tmp2);
            var a1 = Sse2.Add(tmp1, tmp3);
            var a2 = Sse2.Subtract(tmp1, tmp3);
            var a3 = Sse2.Subtract(tmp0, tmp2);
            var b0 = Sse2.Add(a0, a1);
            var b1 = Sse2.Add(a3, a2);
            var b2 = Sse2.Subtract(a3, a2);
            var b3 = Sse2.Subtract(a0, a1);
            // Transpose the two 4x4.
            Transpose2x4x4(b0, b1, b2, b3, out tmp0, out tmp1, out tmp2, out tmp3);
        }

        // Horizontal pass and difference of weighted sums.
        {
            // Load all inputs.
            var w0 = LoadU16(w, 0);
            var w8 = LoadU16(w, 8);

            // Calculate a and b (two 4x4 at once).
            var a0 = Sse2.Add(tmp0, tmp2);
            var a1 = Sse2.Add(tmp1, tmp3);
            var a2 = Sse2.Subtract(tmp1, tmp3);
            var a3 = Sse2.Subtract(tmp0, tmp2);
            var b0 = Sse2.Add(a0, a1);
            var b1 = Sse2.Add(a3, a2);
            var b2 = Sse2.Subtract(a3, a2);
            var b3 = Sse2.Subtract(a0, a1);

            // Separate the transforms of inA and inB.
            var Ab0 = Sse2.UnpackLow(b0.AsInt64(), b1.AsInt64()).AsInt16();
            var Ab2 = Sse2.UnpackLow(b2.AsInt64(), b3.AsInt64()).AsInt16();
            var Bb0 = Sse2.UnpackHigh(b0.AsInt64(), b1.AsInt64()).AsInt16();
            var Bb2 = Sse2.UnpackHigh(b2.AsInt64(), b3.AsInt64()).AsInt16();

            var zero16 = Vector128<short>.Zero;
            Ab0 = Sse2.Max(Ab0, Sse2.Subtract(zero16, Ab0));   // abs(v), 16b
            Ab2 = Sse2.Max(Ab2, Sse2.Subtract(zero16, Ab2));
            Bb0 = Sse2.Max(Bb0, Sse2.Subtract(zero16, Bb0));
            Bb2 = Sse2.Max(Bb2, Sse2.Subtract(zero16, Bb2));

            // weighted sums
            var A = Sse2.Add(Sse2.MultiplyAddAdjacent(Ab0, w0), Sse2.MultiplyAddAdjacent(Ab2, w8));
            var B = Sse2.Add(Sse2.MultiplyAddAdjacent(Bb0, w0), Sse2.MultiplyAddAdjacent(Bb2, w8));

            // difference of weighted sums
            return HSum(Sse2.Subtract(A, B));
        }
    }

    // ---- quantization --------------------------------------------------------------------------------------------------

    private static int DoQuantizeBlockSse2(Span<short> input, Span<short> output, ushort[]? sharpen, Vp8Matrix mtx)
    {
        var maxCoeff2047 = Vector128.Create((short)MaxLevel);
        var zero = Vector128<short>.Zero;
        var in0 = LoadS(input, 0);
        var in8 = LoadS(input, 8);
        var iq0 = LoadU16(mtx.IQ, 0);
        var iq8 = LoadU16(mtx.IQ, 8);
        var q0 = LoadU16(mtx.Q, 0);
        var q8 = LoadU16(mtx.Q, 8);

        // extract sign(in)  (0x0000 if positive, 0xffff if negative)
        var sign0 = Sse2.CompareGreaterThan(zero, in0);
        var sign8 = Sse2.CompareGreaterThan(zero, in8);

        // coeff = abs(in) = (in ^ sign) - sign
        var coeff0 = Sse2.Subtract(Sse2.Xor(in0, sign0), sign0);
        var coeff8 = Sse2.Subtract(Sse2.Xor(in8, sign8), sign8);

        // coeff = abs(in) + sharpen
        if (sharpen != null)
        {
            coeff0 = Sse2.Add(coeff0, LoadU16(sharpen, 0));
            coeff8 = Sse2.Add(coeff8, LoadU16(sharpen, 8));
        }

        Vector128<short> out0, out8;
        // out = (coeff * iQ + B) >> QFIX
        {
            // doing calculations with 32b precision (QFIX=17)
            // out = (coeff * iQ)
            var coeffIQ0H = Sse2.MultiplyHigh(coeff0.AsUInt16(), iq0.AsUInt16()).AsInt16();
            var coeffIQ0L = Sse2.MultiplyLow(coeff0, iq0);
            var coeffIQ8H = Sse2.MultiplyHigh(coeff8.AsUInt16(), iq8.AsUInt16()).AsInt16();
            var coeffIQ8L = Sse2.MultiplyLow(coeff8, iq8);
            var out00 = Sse2.UnpackLow(coeffIQ0L, coeffIQ0H).AsInt32();
            var out04 = Sse2.UnpackHigh(coeffIQ0L, coeffIQ0H).AsInt32();
            var out08 = Sse2.UnpackLow(coeffIQ8L, coeffIQ8H).AsInt32();
            var out12 = Sse2.UnpackHigh(coeffIQ8L, coeffIQ8H).AsInt32();
            // out = (coeff * iQ + B)
            out00 = Sse2.Add(out00, LoadU32(mtx.Bias, 0));
            out04 = Sse2.Add(out04, LoadU32(mtx.Bias, 4));
            out08 = Sse2.Add(out08, LoadU32(mtx.Bias, 8));
            out12 = Sse2.Add(out12, LoadU32(mtx.Bias, 12));
            // out = QUANTDIV(coeff, iQ, B, QFIX)
            out00 = Sse2.ShiftRightArithmetic(out00, QFix);
            out04 = Sse2.ShiftRightArithmetic(out04, QFix);
            out08 = Sse2.ShiftRightArithmetic(out08, QFix);
            out12 = Sse2.ShiftRightArithmetic(out12, QFix);

            // pack result as 16b
            out0 = Sse2.PackSignedSaturate(out00, out04);
            out8 = Sse2.PackSignedSaturate(out08, out12);

            // if (coeff > 2047) coeff = 2047
            out0 = Sse2.Min(out0, maxCoeff2047);
            out8 = Sse2.Min(out8, maxCoeff2047);
        }

        // get sign back (if (sign[j]) out_n = -out_n)
        out0 = Sse2.Subtract(Sse2.Xor(out0, sign0), sign0);
        out8 = Sse2.Subtract(Sse2.Xor(out8, sign8), sign8);

        // in = out * Q
        StoreS(input, 0, Sse2.MultiplyLow(out0, q0));
        StoreS(input, 8, Sse2.MultiplyLow(out8, q8));

        // zigzag the output before storing it.
        //
        // The zigzag pattern can almost be reproduced with a small sequence of
        // shuffles. After it, we only need to swap the 7th (ending up in third
        // position instead of twelfth) and 8th values.
        Vector128<short> packedOut;
        {
            var outZ0 = Sse2.ShuffleHigh(out0, Shuf(2, 1, 3, 0));
            outZ0 = Sse2.Shuffle(outZ0.AsInt32(), Shuf(3, 1, 2, 0)).AsInt16();
            outZ0 = Sse2.ShuffleHigh(outZ0, Shuf(3, 1, 0, 2));
            var outZ8 = Sse2.ShuffleLow(out8, Shuf(3, 0, 2, 1));
            outZ8 = Sse2.Shuffle(outZ8.AsInt32(), Shuf(3, 1, 2, 0)).AsInt16();
            outZ8 = Sse2.ShuffleLow(outZ8, Shuf(1, 3, 2, 0));
            StoreS(output, 0, outZ0);
            StoreS(output, 8, outZ8);
            packedOut = Sse2.PackSignedSaturate(outZ0, outZ8).AsInt16();
        }
        {
            short outZ12 = output[12];
            short outZ3 = output[3];
            output[3] = outZ12;
            output[12] = outZ3;
        }

        // detect if all 'out' values are zeroes or not
        return Sse2.MoveMask(Sse2.CompareEqual(packedOut.AsByte(), Vector128<byte>.Zero)) != 0xffff ? 1 : 0;
    }
}
