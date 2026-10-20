// Plain image planes for the libjxl decoder port (image.h Plane<T> / Image3): row-major with a stride, no alignment
// requirements. Rows are addressed as (array, offset) pairs so stages can read their borders at negative offsets.
using System;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjImageF
{
    public int XSize, YSize, Stride;
    public float[] Data;

    public LjImageF(int xsize, int ysize)
    {
        XSize = xsize;
        YSize = ysize;
        Stride = Math.Max(xsize, 1);
        Data = new float[(long)Stride * Math.Max(ysize, 1)];
    }

    public int Row(int y) => y * Stride;

    public float this[int x, int y]
    {
        get => Data[(y * Stride) + x];
        set => Data[(y * Stride) + x] = value;
    }

    /// <summary>Plane::ShrinkTo: only the logical size changes.</summary>
    public void ShrinkTo(int xsize, int ysize)
    {
        if (xsize > Stride || (long)ysize * Stride > Data.Length)
        {
            throw new InvalidOperationException("ShrinkTo beyond allocation");
        }

        XSize = xsize;
        YSize = ysize;
    }

    public LjImageF Clone()
    {
        var c = new LjImageF(XSize, YSize) { Stride = Stride, Data = (float[])Data.Clone() };
        return c;
    }
}

internal sealed class LjImage3F
{
    public LjImageF[] Planes;

    public LjImage3F(int xsize, int ysize) => Planes = [new LjImageF(xsize, ysize), new LjImageF(xsize, ysize), new LjImageF(xsize, ysize)];

    public LjImage3F(LjImageF a, LjImageF b, LjImageF c) => Planes = [a, b, c];

    public int XSize => Planes[0].XSize;

    public int YSize => Planes[0].YSize;

    public LjImageF Plane(int c) => Planes[c];
}

internal sealed class LjImageI
{
    public int XSize, YSize, Stride;
    public int[] Data;

    public LjImageI(int xsize, int ysize)
    {
        XSize = xsize;
        YSize = ysize;
        Stride = Math.Max(xsize, 1);
        Data = new int[(long)Stride * Math.Max(ysize, 1)];
    }

    public int Row(int y) => y * Stride;
}

internal sealed class LjImageB
{
    public int XSize, YSize, Stride;
    public byte[] Data;

    public LjImageB(int xsize, int ysize)
    {
        XSize = xsize;
        YSize = ysize;
        Stride = Math.Max(xsize, 1);
        Data = new byte[(long)Stride * Math.Max(ysize, 1)];
    }

    public int Row(int y) => y * Stride;
}

internal sealed class LjImageSB
{
    public int XSize, YSize, Stride;
    public sbyte[] Data;

    public LjImageSB(int xsize, int ysize)
    {
        XSize = xsize;
        YSize = ysize;
        Stride = Math.Max(xsize, 1);
        Data = new sbyte[(long)Stride * Math.Max(ysize, 1)];
    }

    public int Row(int y) => y * Stride;
}
