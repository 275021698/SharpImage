// SIMD angular intra prediction for 8-bit pixels (SSSE3): each row is the two-tap interpolation
// ((32 - f) * ref[x] + f * ref[x + 1] + 16) >> 5 as pmaddubsw on interleaved reference pairs (f = 0 gives the copy
// exactly), so the predictions are bit-identical to x265's C.
#if !HIGH_BIT_DEPTH
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc.X265;

using static X265Const;

internal static unsafe partial class X265Primitives
{
    private struct AngRefBuf
    {
        public fixed byte v[3 * 32 + 32];
    }

    private struct AngFrames
    {
        public fixed byte v[4 * 160];
    }

    private struct AngBlock
    {
        public fixed byte frame[160];
        public fixed byte pred[32 * 32];
    }

    /// <summary>intra_pred[dirMode] for an angular mode, 8-bit pixels.</summary>
    private static void IntraPredAng8(int width, byte* dst, long dstStride, byte* srcPix, int dirMode, int bFilter)
    {
        bool horMode = dirMode < 18;
        int angleOffset = horMode ? 10 - dirMode : dirMode - 26;
        int angle = AngleTable[8 + angleOffset];
        int invAngle = angle < 0 ? InvAngleTable[-angleOffset - 1] : 0;
        if (!horMode)
        {
            AngularVertical8(width, srcPix, angle, invAngle, dst, dstStride, bFilter != 0);
            return;
        }

        AngBlock b;
        AngularFrames(width, srcPix, b.pred, b.frame);   // (the vertical frame copy lands in pred, overwritten below)
        AngularVertical8(width, b.frame, angle, invAngle, b.pred, width, bFilter != 0);
        TransposeBytes(b.pred, width, dst, dstStride);
    }

    private static Vector128<byte> Transpose4x4Bytes { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15); }

    // dst[x][y] = src[y][x] for an n x n block of bytes (src rows of n bytes)
    private static void TransposeBytes(byte* src, int n, byte* dst, long dstStride)
    {
        if (n == 4)
        {
            var t = Ssse3.Shuffle(Sse2.LoadVector128(src), Transpose4x4Bytes).AsUInt32();
            *(uint*)dst = t.ToScalar();
            *(uint*)(dst + dstStride) = t.GetElement(1);
            *(uint*)(dst + (2 * dstStride)) = t.GetElement(2);
            *(uint*)(dst + (3 * dstStride)) = t.GetElement(3);
            return;
        }

        for (int by = 0; by < n; by += 8)
        {
            for (int bx = 0; bx < n; bx += 8)
            {
                byte* s0 = src + (by * n) + bx;
                var a0 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)s0).AsByte(), Vector128.CreateScalarUnsafe(*(ulong*)(s0 + n)).AsByte());
                var a1 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (2 * n))).AsByte(), Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (3 * n))).AsByte());
                var a2 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (4 * n))).AsByte(), Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (5 * n))).AsByte());
                var a3 = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (6 * n))).AsByte(), Vector128.CreateScalarUnsafe(*(ulong*)(s0 + (7 * n))).AsByte());
                var b0 = Sse2.UnpackLow(a0.AsUInt16(), a1.AsUInt16());
                var b1 = Sse2.UnpackHigh(a0.AsUInt16(), a1.AsUInt16());
                var b2 = Sse2.UnpackLow(a2.AsUInt16(), a3.AsUInt16());
                var b3 = Sse2.UnpackHigh(a2.AsUInt16(), a3.AsUInt16());
                var c0 = Sse2.UnpackLow(b0.AsUInt32(), b2.AsUInt32()).AsUInt64();
                var c1 = Sse2.UnpackHigh(b0.AsUInt32(), b2.AsUInt32()).AsUInt64();
                var c2 = Sse2.UnpackLow(b1.AsUInt32(), b3.AsUInt32()).AsUInt64();
                var c3 = Sse2.UnpackHigh(b1.AsUInt32(), b3.AsUInt32()).AsUInt64();
                byte* d0 = dst + (bx * dstStride) + by;
                *(ulong*)d0 = c0.ToScalar();
                *(ulong*)(d0 + dstStride) = c0.GetElement(1);
                *(ulong*)(d0 + (2 * dstStride)) = c1.ToScalar();
                *(ulong*)(d0 + (3 * dstStride)) = c1.GetElement(1);
                *(ulong*)(d0 + (4 * dstStride)) = c2.ToScalar();
                *(ulong*)(d0 + (5 * dstStride)) = c2.GetElement(1);
                *(ulong*)(d0 + (6 * dstStride)) = c3.ToScalar();
                *(ulong*)(d0 + (7 * dstStride)) = c3.GetElement(1);
            }
        }
    }

    private static Vector128<byte> Pairs4x2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Vector128.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 8, 9, 9, 10, 10, 11, 11, 12); }
    private static Vector128<byte> Pairs8 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Vector128.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8); }

    // the pmaddubsw weights (32 - f, f) of every byte pair
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> AngWeights(int f) => Vector128.Create((short)((f << 8) | (32 - f))).AsSByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> AngRound(Vector128<short> v) => Sse2.ShiftRightArithmetic(Sse2.Add(v, Vector128.Create((short)16)), 5);

    /// <summary>
    /// One angular mode in the vertical frame (frame: [0] the corner, [1..2n] the main side, [2n + 1..4n] the other;
    /// for a horizontal mode the left samples are the main side and the output is the transposed prediction), with 16
    /// bytes of readable padding after it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AngularVertical8(int size, byte* frame, int angle, int invAngle, byte* dst, long dstStride, bool edgeFilter)
    {
        int width2 = size << 1;
        if (angle == 0)
        {
            var row = Sse2.LoadVector128(frame + 1);
            for (int y = 0; y < size; y++)
            {
                if (size == 4) *(uint*)(dst + (y * dstStride)) = row.AsUInt32().ToScalar();
                else if (size == 8) *(ulong*)(dst + (y * dstStride)) = row.AsUInt64().ToScalar();
                else
                {
                    Sse2.Store(dst + (y * dstStride), row);
                    if (size == 32) Sse2.Store(dst + (y * dstStride) + 16, Sse2.LoadVector128(frame + 17));
                }
            }

            if (edgeFilter)
            {
                int topLeft = frame[0], top = frame[1];
                for (int y = 0; y < size; y++)
                {
                    dst[y * dstStride] = ClipPixel(top + ((frame[width2 + 1 + y] - topLeft) >> 1));
                }
            }

            return;
        }

        AngRefBuf rb;
        byte* refBuf = rb.v;
        byte* refp;
        if (angle < 0)
        {
            int nbProjected = -((size * angle) >> 5) - 1;
            byte* refPix = refBuf + nbProjected + 1;
            int invAngleSum = 128;
            for (int i = 0; i < nbProjected; i++)
            {
                invAngleSum += invAngle;
                refPix[-2 - i] = frame[width2 + (invAngleSum >> 8)];
            }

            // corner and main side (the 16-byte copies run past width + 1; only those bytes are read)
            Sse2.Store(refPix - 1, Sse2.LoadVector128(frame));
            if (size >= 16) Sse2.Store(refPix + 15, Sse2.LoadVector128(frame + 16));
            if (size == 32) Sse2.Store(refPix + 31, Sse2.LoadVector128(frame + 32));
            refp = refPix;
        }
        else
        {
            refp = frame + 1;
        }

        int angleSum = 0;
        if (size == 4)
        {
            for (int y = 0; y < 4; y += 2)
            {
                angleSum += angle;
                int o0 = angleSum >> 5, f0 = angleSum & 31;
                angleSum += angle;
                int o1 = angleSum >> 5, f1 = angleSum & 31;
                var src = Sse2.UnpackLow(Vector128.CreateScalarUnsafe(*(ulong*)(refp + o0)), Vector128.CreateScalarUnsafe(*(ulong*)(refp + o1))).AsByte();
                var w = Sse2.UnpackLow(AngWeights(f0).AsInt64(), AngWeights(f1).AsInt64()).AsSByte();
                var v = AngRound(Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(src, Pairs4x2), w));
                var p = Sse2.PackUnsignedSaturate(v, v).AsUInt32();
                *(uint*)(dst + (y * dstStride)) = p.ToScalar();
                *(uint*)(dst + ((y + 1) * dstStride)) = p.GetElement(1);
            }
        }
        else if (size == 8)
        {
            for (int y = 0; y < 8; y++)
            {
                angleSum += angle;
                var v = AngRound(Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(Sse2.LoadVector128(refp + (angleSum >> 5)), Pairs8), AngWeights(angleSum & 31)));
                *(ulong*)(dst + (y * dstStride)) = Sse2.PackUnsignedSaturate(v, v).AsUInt64().ToScalar();
            }
        }
        else
        {
            for (int y = 0; y < size; y++)
            {
                angleSum += angle;
                byte* r = refp + (angleSum >> 5);
                var w = AngWeights(angleSum & 31);
                for (int x = 0; x < size; x += 16)
                {
                    var a = Sse2.LoadVector128(r + x);
                    var b = Sse2.LoadVector128(r + x + 1);
                    var lo = AngRound(Ssse3.MultiplyAddAdjacent(Sse2.UnpackLow(a, b), w));
                    var hi = AngRound(Ssse3.MultiplyAddAdjacent(Sse2.UnpackHigh(a, b), w));
                    Sse2.Store(dst + (y * dstStride) + x, Sse2.PackUnsignedSaturate(lo, hi));
                }
            }
        }
    }

    // the vertical frames of a neighbour buffer: as it is, and with its left and above sides swapped (copied in 16-byte
    // chunks: the neighbour buffers and the frames have 16 bytes of padding)
    private static void AngularFrames(int size, byte* src, byte* vert, byte* hor)
    {
        int width2 = size << 1, n = (4 * size) + 1;
        for (int i = 0; i < n; i += 16)
        {
            Sse2.Store(vert + i, Sse2.LoadVector128(src + i));
        }

        byte corner = src[0];
        for (int i = 0; i < width2; i += 16)
        {
            Sse2.Store(hor + 1 + i, Sse2.LoadVector128(src + width2 + 1 + i));
        }

        for (int i = 0; i < width2; i += 16)
        {
            Sse2.Store(hor + width2 + 1 + i, Sse2.LoadVector128(src + 1 + i));
        }

        hor[0] = corner;
    }

    // the 4x4 frame with its sides swapped from the vertical one (a = frame[0..15], b = frame[1..16]): corner, the 8
    // left samples, the first 7 above
    private static Vector128<byte> HorFrame4A { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Vector128.Create((byte)0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1, 2, 3, 4, 5, 6, 7); }
    private static Vector128<byte> HorFrame4B { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Vector128.Create((byte)0x80, 8, 9, 10, 11, 12, 13, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80); }

    // all_angs_pred for 4x4 (never filtered): a mode's block is one vector, the two-tap pairs of rows 0-1 and 2-3 gathered
    // from the 16-byte frame by pshufb (AllAngs4Index, x265's reference projection included) and weighted by pmaddubsw
    private static void AllAngsPred4(byte* dest, byte* refPix, bool edgeFilter)
    {
        var vert = Sse2.LoadVector128(refPix);
        var hor = Sse2.Or(Ssse3.Shuffle(vert, HorFrame4A), Ssse3.Shuffle(Sse2.LoadVector128(refPix + 1), HorFrame4B));
        byte* index = X265Tables.Ptr(AllAngs4Index);
        sbyte* weights = (sbyte*)X265Tables.Ptr(AllAngs4Weights);
        var round = Vector128.Create((short)16);
        for (int m = 0; m < 33; m++)
        {
            var frame = m < 16 ? hor : vert;   // modes 2..17 are horizontal
            var r01 = Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(frame, Sse2.LoadVector128(index + (m * 32))), Sse2.LoadVector128(weights + (m * 32)));
            var r23 = Ssse3.MultiplyAddAdjacent(Ssse3.Shuffle(frame, Sse2.LoadVector128(index + (m * 32) + 16)), Sse2.LoadVector128(weights + (m * 32) + 16));
            Sse2.Store(dest + (m * 16), Sse2.PackUnsignedSaturate(Sse2.ShiftRightArithmetic(Sse2.Add(r01, round), 5), Sse2.ShiftRightArithmetic(Sse2.Add(r23, round), 5)));
        }

        if (edgeFilter)
        {
            // modes 10 and 26: column 0 from the side samples (in the swapped frame for mode 10)
            int topLeft = refPix[0];
            for (int y = 0; y < 4; y++)
            {
                dest[(8 * 16) + (y * 4)] = ClipPixel(refPix[9] + ((refPix[1 + y] - topLeft) >> 1));
                dest[(24 * 16) + (y * 4)] = ClipPixel(refPix[1] + ((refPix[9 + y] - topLeft) >> 1));
            }
        }
    }

    private static ReadOnlySpan<byte> AllAngs4Index =>
    [
        2, 2, 3, 3, 4, 4, 5, 5, 3, 3, 4, 4, 5, 5, 6, 6, 4, 4, 5, 5, 6, 6, 7, 7, 5, 5, 6, 6, 7, 7, 8, 8,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7, 4, 5, 5, 6, 6, 7, 7, 8,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5,
        1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 12, 0, 0, 1, 1, 2, 2, 3,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3,
        0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3, 12, 10, 10, 0, 0, 1, 1, 2,
        0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3, 11, 10, 10, 0, 0, 1, 1, 2,
        0, 1, 1, 2, 2, 3, 3, 4, 9, 0, 0, 1, 1, 2, 2, 3, 10, 9, 9, 0, 0, 1, 1, 2, 12, 10, 10, 9, 9, 0, 0, 1,
        0, 0, 1, 1, 2, 2, 3, 3, 9, 9, 0, 0, 1, 1, 2, 2, 10, 10, 9, 9, 0, 0, 1, 1, 11, 11, 10, 10, 9, 9, 0, 0,
        0, 1, 1, 2, 2, 3, 3, 4, 9, 0, 0, 1, 1, 2, 2, 3, 10, 9, 9, 0, 0, 1, 1, 2, 12, 10, 10, 9, 9, 0, 0, 1,
        0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3, 11, 10, 10, 0, 0, 1, 1, 2,
        0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3, 12, 10, 10, 0, 0, 1, 1, 2,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 10, 0, 0, 1, 1, 2, 2, 3, 10, 0, 0, 1, 1, 2, 2, 3,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 12, 0, 0, 1, 1, 2, 2, 3,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4,
        0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4, 0, 1, 1, 2, 2, 3, 3, 4,
        1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4, 1, 1, 2, 2, 3, 3, 4, 4,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6,
        1, 2, 2, 3, 3, 4, 4, 5, 1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7,
        1, 2, 2, 3, 3, 4, 4, 5, 2, 3, 3, 4, 4, 5, 5, 6, 3, 4, 4, 5, 5, 6, 6, 7, 4, 5, 5, 6, 6, 7, 7, 8,
        2, 2, 3, 3, 4, 4, 5, 5, 3, 3, 4, 4, 5, 5, 6, 6, 4, 4, 5, 5, 6, 6, 7, 7, 5, 5, 6, 6, 7, 7, 8, 8,
    ];

    private static ReadOnlySpan<byte> AllAngs4Weights =>
    [
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0,
        6, 26, 6, 26, 6, 26, 6, 26, 12, 20, 12, 20, 12, 20, 12, 20, 18, 14, 18, 14, 18, 14, 18, 14, 24, 8, 24, 8, 24, 8, 24, 8,
        11, 21, 11, 21, 11, 21, 11, 21, 22, 10, 22, 10, 22, 10, 22, 10, 1, 31, 1, 31, 1, 31, 1, 31, 12, 20, 12, 20, 12, 20, 12, 20,
        15, 17, 15, 17, 15, 17, 15, 17, 30, 2, 30, 2, 30, 2, 30, 2, 13, 19, 13, 19, 13, 19, 13, 19, 28, 4, 28, 4, 28, 4, 28, 4,
        19, 13, 19, 13, 19, 13, 19, 13, 6, 26, 6, 26, 6, 26, 6, 26, 25, 7, 25, 7, 25, 7, 25, 7, 12, 20, 12, 20, 12, 20, 12, 20,
        23, 9, 23, 9, 23, 9, 23, 9, 14, 18, 14, 18, 14, 18, 14, 18, 5, 27, 5, 27, 5, 27, 5, 27, 28, 4, 28, 4, 28, 4, 28, 4,
        27, 5, 27, 5, 27, 5, 27, 5, 22, 10, 22, 10, 22, 10, 22, 10, 17, 15, 17, 15, 17, 15, 17, 15, 12, 20, 12, 20, 12, 20, 12, 20,
        30, 2, 30, 2, 30, 2, 30, 2, 28, 4, 28, 4, 28, 4, 28, 4, 26, 6, 26, 6, 26, 6, 26, 6, 24, 8, 24, 8, 24, 8, 24, 8,
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0,
        2, 30, 2, 30, 2, 30, 2, 30, 4, 28, 4, 28, 4, 28, 4, 28, 6, 26, 6, 26, 6, 26, 6, 26, 8, 24, 8, 24, 8, 24, 8, 24,
        5, 27, 5, 27, 5, 27, 5, 27, 10, 22, 10, 22, 10, 22, 10, 22, 15, 17, 15, 17, 15, 17, 15, 17, 20, 12, 20, 12, 20, 12, 20, 12,
        9, 23, 9, 23, 9, 23, 9, 23, 18, 14, 18, 14, 18, 14, 18, 14, 27, 5, 27, 5, 27, 5, 27, 5, 4, 28, 4, 28, 4, 28, 4, 28,
        13, 19, 13, 19, 13, 19, 13, 19, 26, 6, 26, 6, 26, 6, 26, 6, 7, 25, 7, 25, 7, 25, 7, 25, 20, 12, 20, 12, 20, 12, 20, 12,
        17, 15, 17, 15, 17, 15, 17, 15, 2, 30, 2, 30, 2, 30, 2, 30, 19, 13, 19, 13, 19, 13, 19, 13, 4, 28, 4, 28, 4, 28, 4, 28,
        21, 11, 21, 11, 21, 11, 21, 11, 10, 22, 10, 22, 10, 22, 10, 22, 31, 1, 31, 1, 31, 1, 31, 1, 20, 12, 20, 12, 20, 12, 20, 12,
        26, 6, 26, 6, 26, 6, 26, 6, 20, 12, 20, 12, 20, 12, 20, 12, 14, 18, 14, 18, 14, 18, 14, 18, 8, 24, 8, 24, 8, 24, 8, 24,
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0,
        26, 6, 26, 6, 26, 6, 26, 6, 20, 12, 20, 12, 20, 12, 20, 12, 14, 18, 14, 18, 14, 18, 14, 18, 8, 24, 8, 24, 8, 24, 8, 24,
        21, 11, 21, 11, 21, 11, 21, 11, 10, 22, 10, 22, 10, 22, 10, 22, 31, 1, 31, 1, 31, 1, 31, 1, 20, 12, 20, 12, 20, 12, 20, 12,
        17, 15, 17, 15, 17, 15, 17, 15, 2, 30, 2, 30, 2, 30, 2, 30, 19, 13, 19, 13, 19, 13, 19, 13, 4, 28, 4, 28, 4, 28, 4, 28,
        13, 19, 13, 19, 13, 19, 13, 19, 26, 6, 26, 6, 26, 6, 26, 6, 7, 25, 7, 25, 7, 25, 7, 25, 20, 12, 20, 12, 20, 12, 20, 12,
        9, 23, 9, 23, 9, 23, 9, 23, 18, 14, 18, 14, 18, 14, 18, 14, 27, 5, 27, 5, 27, 5, 27, 5, 4, 28, 4, 28, 4, 28, 4, 28,
        5, 27, 5, 27, 5, 27, 5, 27, 10, 22, 10, 22, 10, 22, 10, 22, 15, 17, 15, 17, 15, 17, 15, 17, 20, 12, 20, 12, 20, 12, 20, 12,
        2, 30, 2, 30, 2, 30, 2, 30, 4, 28, 4, 28, 4, 28, 4, 28, 6, 26, 6, 26, 6, 26, 6, 26, 8, 24, 8, 24, 8, 24, 8, 24,
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0,
        30, 2, 30, 2, 30, 2, 30, 2, 28, 4, 28, 4, 28, 4, 28, 4, 26, 6, 26, 6, 26, 6, 26, 6, 24, 8, 24, 8, 24, 8, 24, 8,
        27, 5, 27, 5, 27, 5, 27, 5, 22, 10, 22, 10, 22, 10, 22, 10, 17, 15, 17, 15, 17, 15, 17, 15, 12, 20, 12, 20, 12, 20, 12, 20,
        23, 9, 23, 9, 23, 9, 23, 9, 14, 18, 14, 18, 14, 18, 14, 18, 5, 27, 5, 27, 5, 27, 5, 27, 28, 4, 28, 4, 28, 4, 28, 4,
        19, 13, 19, 13, 19, 13, 19, 13, 6, 26, 6, 26, 6, 26, 6, 26, 25, 7, 25, 7, 25, 7, 25, 7, 12, 20, 12, 20, 12, 20, 12, 20,
        15, 17, 15, 17, 15, 17, 15, 17, 30, 2, 30, 2, 30, 2, 30, 2, 13, 19, 13, 19, 13, 19, 13, 19, 28, 4, 28, 4, 28, 4, 28, 4,
        11, 21, 11, 21, 11, 21, 11, 21, 22, 10, 22, 10, 22, 10, 22, 10, 1, 31, 1, 31, 1, 31, 1, 31, 12, 20, 12, 20, 12, 20, 12, 20,
        6, 26, 6, 26, 6, 26, 6, 26, 12, 20, 12, 20, 12, 20, 12, 20, 18, 14, 18, 14, 18, 14, 18, 14, 24, 8, 24, 8, 24, 8, 24, 8,
        32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0, 32, 0,
    ];


    /// <summary>all_angs_pred for 8-bit pixels: modes 2..34, horizontal modes stored transposed.</summary>
    private static void AllAngsPred8(int log2Size, byte* dest, byte* refPix, byte* filtPix, int bLuma)
    {
        if (log2Size == 2)
        {
            AllAngsPred4(dest, refPix, bLuma != 0);
            return;
        }

        int size = 1 << log2Size;
        AngFrames fr;
        byte* frames = fr.v;   // [unfiltered vert, unfiltered hor, filtered vert, filtered hor]
        AngularFrames(size, refPix, frames, frames + 160);
        AngularFrames(size, filtPix, frames + 320, frames + 480);
        for (int mode = 2; mode <= 34; mode++)
        {
            bool horMode = mode < 18;
            byte* frame = frames + ((X265Tables.IntraFilterFlags[mode] & size) != 0 ? 320 : 0) + (horMode ? 160 : 0);
            int angleOffset = horMode ? 10 - mode : mode - 26;
            int angle = AngleTable[8 + angleOffset];
            int invAngle = angle < 0 ? InvAngleTable[-angleOffset - 1] : 0;
            AngularVertical8(size, frame, angle, invAngle, dest + ((mode - 2) << (log2Size * 2)), size, bLuma != 0);
        }

    }
}
#endif
