// Pointers to the tables read per coefficient / per partition in the hot loops (no bounds checks there).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

internal static unsafe partial class X265Tables
{
    // pointers into the tables' RVA data (no bounds checks, and constant addresses: no static-constructor check, which
    // Native AOT would otherwise do on every access)
    internal static uint* EntropyStateBitsPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(EntropyStateBits); }
    internal static uint* EntropyBitsPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(EntropyBits); }
    internal static byte* NextStatePtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(NextState); }
    internal static byte* GoRiceRangePtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(GoRiceRange); }
    internal static byte* LastCoeffTablePtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(LastCoeffTable); }
    internal static byte* ZscanToPelXPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(ZscanToPelX); }
    internal static byte* ZscanToPelYPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(ZscanToPelY); }
    internal static int* ZscanToRasterPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(ZscanToRaster); }
    internal static int* RasterToZscanPtr { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Ptr(RasterToZscan); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T* Ptr<T>(ReadOnlySpan<T> rva) where T : unmanaged => (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(rva));
}
