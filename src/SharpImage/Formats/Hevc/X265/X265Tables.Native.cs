// Native copies of the tables read per coefficient / per partition in the hot loops (no bounds checks there).
using System.Runtime.InteropServices;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

internal static unsafe partial class X265Tables
{
    internal static readonly uint* EntropyStateBitsPtr = Native(EntropyStateBits);
    internal static readonly uint* EntropyBitsPtr = Native(EntropyBits);
    internal static readonly byte* NextStatePtr = Native(NextState);
    internal static readonly byte* GoRiceRangePtr = Native(GoRiceRange);
    internal static readonly byte* LastCoeffTablePtr = Native(LastCoeffTable);
    internal static readonly byte* ZscanToPelXPtr = Native(ZscanToPelX);
    internal static readonly byte* ZscanToPelYPtr = Native(ZscanToPelY);
    internal static readonly int* ZscanToRasterPtr = Native(ZscanToRaster);
    internal static readonly int* RasterToZscanPtr = Native(RasterToZscan);

    private static T* Native<T>(T[] a) where T : unmanaged
    {
        T* p = (T*)NativeMemory.Alloc((nuint)(a.Length * sizeof(T)));
        a.AsSpan().CopyTo(new System.Span<T>(p, a.Length));
        return p;
    }
}
