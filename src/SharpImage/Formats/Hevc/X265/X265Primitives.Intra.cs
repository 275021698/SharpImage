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
    private static readonly Vector128<byte> Pairs4x2 = Vector128.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 8, 9, 9, 10, 10, 11, 11, 12);
    private static readonly Vector128<byte> Pairs8 = Vector128.Create((byte)0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8);

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

        byte* refBuf = stackalloc byte[3 * 32 + 32];
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

    // the vertical frames of a neighbour buffer: as it is, and with its left and above sides swapped (16 bytes padding)
    private static void AngularFrames(int size, byte* src, byte* vert, byte* hor)
    {
        int width2 = size << 1, n = (4 * size) + 1;
        Buffer.MemoryCopy(src, vert, n, n);
        hor[0] = src[0];
        Buffer.MemoryCopy(src + width2 + 1, hor + 1, width2, width2);
        Buffer.MemoryCopy(src + 1, hor + width2 + 1, width2, width2);
    }

    /// <summary>all_angs_pred for 8-bit pixels: modes 2..34, horizontal modes stored transposed.</summary>
    private static void AllAngsPred8(int log2Size, byte* dest, byte* refPix, byte* filtPix, int bLuma)
    {
        int size = 1 << log2Size;
        byte* frames = stackalloc byte[4 * 160];   // [unfiltered vert, unfiltered hor, filtered vert, filtered hor]
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
