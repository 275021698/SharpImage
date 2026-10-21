using System.Buffers;
using SharpImage.Image;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    /// <summary>
    /// The planes of a decoded HEVC picture (samples as 16-bit values whatever the depth), for the transformations
    /// libheif applies before its colour conversion.
    /// </summary>
    private sealed class YCbCrPlanes
    {
        public int W, H, Cw, Ch, BitDepth;
        public Hevc.HevcChromaFormat Chroma;
        public ushort[] Y = [];
        public ushort[]? Cb, Cr;
        public (int Cp, int Tc, int Mc, bool Full) Nclx;

        public int ShiftH => Chroma is Hevc.HevcChromaFormat.Chroma420 or Hevc.HevcChromaFormat.Chroma422 ? 1 : 0;

        public int ShiftV => Chroma == Hevc.HevcChromaFormat.Chroma420 ? 1 : 0;

        public static YCbCrPlanes FromDecoded(Hevc.DecodedVideoFrame yuv, Hevc.HevcChromaFormat chroma, int bitDepth)
        {
            var p = new YCbCrPlanes { W = yuv.Width, H = yuv.Height, BitDepth = bitDepth, Chroma = chroma };
            p.Y = Read(yuv.YPlane.Span, yuv.YStride, p.W, p.H, bitDepth);
            if (chroma != Hevc.HevcChromaFormat.Monochrome)
            {
                p.Cw = (p.W + (1 << p.ShiftH) - 1) >> p.ShiftH;
                p.Ch = (p.H + (1 << p.ShiftV) - 1) >> p.ShiftV;
                p.Cb = Read(yuv.UPlane.Span, yuv.UStride, p.Cw, p.Ch, bitDepth);
                p.Cr = Read(yuv.VPlane.Span, yuv.VStride, p.Cw, p.Ch, bitDepth);
            }

            return p;
        }

        private static ushort[] Read(ReadOnlySpan<byte> src, int stride, int w, int h, int bitDepth)
        {
            var dst = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    dst[(y * w) + x] = bitDepth > 8 ? (ushort)(src[(y * stride) + (2 * x)] | (src[(y * stride) + (2 * x) + 1] << 8)) : src[(y * stride) + x];
                }
            }

            return dst;
        }

        // A frame for ConvertHevcPicture (its buffer rented, as the decoder's).
        public Hevc.DecodedVideoFrame ToDecoded()
        {
            int bps = BitDepth > 8 ? 2 : 1;
            int ySize = W * H * bps, cSize = Cw * Ch * bps;
            byte[] buf = ArrayPool<byte>.Shared.Rent(ySize + (2 * cSize) + 1);
            Write(Y, W, H, buf, 0, bps);
            if (Cb != null)
            {
                Write(Cb, Cw, Ch, buf, ySize, bps);
                Write(Cr!, Cw, Ch, buf, ySize + cSize, bps);
            }

            var format = (Chroma, BitDepth > 8) switch
            {
                (Hevc.HevcChromaFormat.Chroma420, false) => Hevc.PixelFormat.Yuv420P,
                (Hevc.HevcChromaFormat.Chroma420, true) => Hevc.PixelFormat.Yuv420P10,
                (Hevc.HevcChromaFormat.Chroma422, false) => Hevc.PixelFormat.Yuv422P,
                (Hevc.HevcChromaFormat.Chroma422, true) => Hevc.PixelFormat.Yuv422P10,
                (_, false) => Hevc.PixelFormat.Yuv444P,
                _ => Hevc.PixelFormat.Yuv444P10,
            };
            return new Hevc.DecodedVideoFrame(W, H, format, 0, buf, 0, W * bps, ySize, Math.Max(1, Cw) * bps, ySize + cSize, Math.Max(1, Cw) * bps);
        }

        private static void Write(ushort[] src, int w, int h, byte[] dst, int off, int bps)
        {
            for (int i = 0; i < w * h; i++)
            {
                if (bps == 1)
                {
                    dst[off + i] = (byte)src[i];
                }
                else
                {
                    dst[off + (2 * i)] = (byte)src[i];
                    dst[off + (2 * i) + 1] = (byte)(src[i] >> 8);
                }
            }
        }
    }

    // The grid's planes: each tile's planes at its offset, clipped to the output size (libheif's grid composition).
    private static YCbCrPlanes ComposePlanes(YCbCrPlanes[] tiles, int cols, int outW, int outH)
    {
        var t0 = tiles[0];
        var p = new YCbCrPlanes { W = outW, H = outH, BitDepth = t0.BitDepth, Chroma = t0.Chroma };
        p.Y = new ushort[outW * outH];
        if (t0.Cb != null)
        {
            p.Cw = (outW + (1 << p.ShiftH) - 1) >> p.ShiftH;
            p.Ch = (outH + (1 << p.ShiftV) - 1) >> p.ShiftV;
            p.Cb = new ushort[p.Cw * p.Ch];
            p.Cr = new ushort[p.Cw * p.Ch];
        }

        for (int i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            int x0 = (i % cols) * t0.W, y0 = (i / cols) * t0.H;
            Blit(t.Y, t.W, t.H, p.Y, outW, outH, x0, y0);
            if (p.Cb != null)
            {
                Blit(t.Cb!, t.Cw, t.Ch, p.Cb, p.Cw, p.Ch, x0 >> p.ShiftH, y0 >> p.ShiftV);
                Blit(t.Cr!, t.Cw, t.Ch, p.Cr!, p.Cw, p.Ch, x0 >> p.ShiftH, y0 >> p.ShiftV);
            }
        }

        return p;

        static void Blit(ushort[] src, int sw, int sh, ushort[] dst, int dw, int dh, int x0, int y0)
        {
            int w = Math.Min(sw, dw - x0), h = Math.Min(sh, dh - y0);
            for (int y = 0; y < h; y++) Array.Copy(src, y * sw, dst, ((y0 + y) * dw) + x0, w);
        }
    }

    // Whether the primary's transformations (clap, irot, imir) make libheif convert its HEVC picture to 4:4:4: from
    // the first tile's hvcC chroma format (and the matrix: the bilinear conversion needs a non-identity one)
    private static bool HevcTransformsNeed444(HeifContainer c, int pid, int tile0, int outW, int outH)
    {
        var data = c.Data;
        bool hasClap = c.Property(pid, "clap") is { Len: >= 32 };
        int? irot = c.Property(pid, "irot") is { Len: >= 1 } ir ? data[ir.Off] & 3 : null;
        int? imir = c.Property(pid, "imir") is { Len: >= 1 } im ? data[im.Off] & 1 : null;
        if (!hasClap && irot is null or 0 && imir is null) return false;
        byte[]? hvcC = c.Property(tile0, "hvcC") is { } hp ? data.AsSpan(hp.Off, hp.Len).ToArray() : null;
        if (hvcC == null || hvcC.Length < 23) return false;
        var chroma = (Hevc.HevcChromaFormat)(hvcC[16] & 3);
        if (Nclx(c, pid) is { Mc: 0 }) return false;
        int w = outW, h = outH;
        if (w <= 0 || h <= 0)
        {
            if (c.Ispe(tile0) is not { } ispe) return false;
            (w, h) = ispe;
        }

        (int X, int Y, int W, int H)? crop = null;
        if (c.Property(pid, "clap") is { Len: >= 32 } cp)
        {
            var clap = new uint[8];
            for (int k = 0; k < 8; k++) clap[k] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cp.Off + (4 * k)));
            crop = CropRectFromCleanAperture(clap, w, h);
        }

        return HeifTransformNeeds444(chroma, w, h, crop, irot, imir);
    }

    // Whether libheif converts the decoded image to YCbCr 4:4:4 (bilinear chroma) for one of the transformations
    // (HeifPixelImage::crop / rotate_ccw / mirror_inplace): a step that would split the subsampled chroma.
    private static bool HeifTransformNeeds444(Hevc.HevcChromaFormat chroma, int w, int h, (int X, int Y, int W, int H)? crop, int? irot, int? imir)
    {
        if (chroma is not (Hevc.HevcChromaFormat.Chroma420 or Hevc.HevcChromaFormat.Chroma422)) return false;
        bool is420 = chroma == Hevc.HevcChromaFormat.Chroma420;
        if (crop is { } c)
        {
            if ((c.X & 1) == 1 || (is420 && (c.Y & 1) == 1)) return true;
            (w, h) = (c.W, c.H);
        }

        int angle = irot is 1 or 2 or 3 ? irot.Value * 90 : 0;
        if (angle != 0)
        {
            bool oddW = (w & 1) == 1, oddH = (h & 1) == 1;
            if (is420 ? (angle == 90 && oddW) || (angle == 180 && (oddW || oddH)) || (angle == 270 && oddH)
                      : angle is 90 or 270 || (angle == 180 && oddH))
            {
                return true;
            }

            if (angle != 180) (w, h) = (h, w);
        }

        if (imir is 0 or 1)
        {
            if (is420 ? (w & 1) == 1 || (h & 1) == 1 : imir == 1 && (w & 1) == 1) return true;
        }

        return false;
    }

    // libheif's decode-time transformations on the planes: clap crop, irot (anti-clockwise), imir, converting to 4:4:4
    // (Op_YCbCr420 / 422_bilinear_to_YCbCr444) where a step needs it, as ImageItem::decode_image does.
    private static YCbCrPlanes TransformPlanesLikeLibheif(YCbCrPlanes p, (int X, int Y, int W, int H)? crop, int? irot, int? imir)
    {
        if (crop is { } c && (c.X != 0 || c.Y != 0 || c.W != p.W || c.H != p.H))
        {
            if (p.Chroma is Hevc.HevcChromaFormat.Chroma420 or Hevc.HevcChromaFormat.Chroma422
                && ((c.X & 1) == 1 || (p.Chroma == Hevc.HevcChromaFormat.Chroma420 && (c.Y & 1) == 1)))
            {
                p = UpsampleTo444(p);
            }

            p = CropPlanes(p, c.X, c.Y, c.X + c.W - 1, c.Y + c.H - 1);
        }

        int angle = irot is 1 or 2 or 3 ? irot.Value * 90 : 0;
        if (angle != 0)
        {
            bool oddW = (p.W & 1) == 1, oddH = (p.H & 1) == 1;
            bool need = p.Chroma == Hevc.HevcChromaFormat.Chroma420 ? (angle == 90 && oddW) || (angle == 180 && (oddW || oddH)) || (angle == 270 && oddH)
                : p.Chroma == Hevc.HevcChromaFormat.Chroma422 && (angle is 90 or 270 || (angle == 180 && oddH));
            if (need) p = UpsampleTo444(p);
            p = RotatePlanes(p, angle);
        }

        if (imir is 0 or 1)
        {
            bool horizontal = imir == 1;
            bool need = p.Chroma == Hevc.HevcChromaFormat.Chroma420 ? (p.W & 1) == 1 || (p.H & 1) == 1
                : p.Chroma == Hevc.HevcChromaFormat.Chroma422 && horizontal && (p.W & 1) == 1;
            if (need) p = UpsampleTo444(p);
            MirrorPlanes(p, horizontal);
        }

        return p;
    }

    // HeifPixelImage::crop: each plane from left >> shift to right >> shift (right rounded down: a coordinate)
    private static YCbCrPlanes CropPlanes(YCbCrPlanes p, int left, int top, int right, int bottom)
    {
        var o = new YCbCrPlanes { W = right - left + 1, H = bottom - top + 1, BitDepth = p.BitDepth, Chroma = p.Chroma, Nclx = p.Nclx };
        o.Y = CropPlane(p.Y, p.W, left, top, right, bottom);
        if (p.Cb != null)
        {
            int sh = p.ShiftH, sv = p.ShiftV;
            int cl = left >> sh, cr = right >> sh, ct = top >> sv, cb = bottom >> sv;
            o.Cw = cr - cl + 1;
            o.Ch = cb - ct + 1;
            o.Cb = CropPlane(p.Cb, p.Cw, cl, ct, cr, cb);
            o.Cr = CropPlane(p.Cr!, p.Cw, cl, ct, cr, cb);
        }

        return o;

        static ushort[] CropPlane(ushort[] src, int sw, int l, int t, int r, int b)
        {
            int w = r - l + 1, h = b - t + 1;
            var dst = new ushort[w * h];
            for (int y = 0; y < h; y++) Array.Copy(src, ((t + y) * sw) + l, dst, y * w, w);
            return dst;
        }
    }

    // ComponentStorage::rotate_ccw of every plane (its own size)
    private static YCbCrPlanes RotatePlanes(YCbCrPlanes p, int angle)
    {
        var o = new YCbCrPlanes { BitDepth = p.BitDepth, Chroma = p.Chroma, Nclx = p.Nclx };
        (o.W, o.H) = angle == 180 ? (p.W, p.H) : (p.H, p.W);
        o.Y = RotatePlane(p.Y, p.W, p.H, angle);
        if (p.Cb != null)
        {
            (o.Cw, o.Ch) = angle == 180 ? (p.Cw, p.Ch) : (p.Ch, p.Cw);
            o.Cb = RotatePlane(p.Cb, p.Cw, p.Ch, angle);
            o.Cr = RotatePlane(p.Cr!, p.Cw, p.Ch, angle);
        }

        return o;

        static ushort[] RotatePlane(ushort[] src, int w, int h, int angle)
        {
            var dst = new ushort[w * h];
            if (angle == 270)
            {
                for (int x = 0; x < h; x++)
                    for (int y = 0; y < w; y++)
                        dst[(y * h) + x] = src[((h - 1 - x) * w) + y];
            }
            else if (angle == 180)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        dst[(y * w) + x] = src[((h - 1 - y) * w) + (w - 1 - x)];
            }
            else
            {
                for (int x = 0; x < h; x++)
                    for (int y = 0; y < w; y++)
                        dst[(y * h) + x] = src[(x * w) + (w - 1 - y)];
            }

            return dst;
        }
    }

    // ComponentStorage::mirror_inplace of every plane: horizontal swaps columns, vertical rows
    private static void MirrorPlanes(YCbCrPlanes p, bool horizontal)
    {
        MirrorPlane(p.Y, p.W, p.H, horizontal);
        if (p.Cb != null)
        {
            MirrorPlane(p.Cb, p.Cw, p.Ch, horizontal);
            MirrorPlane(p.Cr!, p.Cw, p.Ch, horizontal);
        }

        static void MirrorPlane(ushort[] d, int w, int h, bool horizontal)
        {
            if (horizontal)
            {
                for (int y = 0; y < h; y++) d.AsSpan(y * w, w).Reverse();
            }
            else
            {
                for (int y = 0; y < h / 2; y++)
                    for (int x = 0; x < w; x++)
                        (d[(y * w) + x], d[((h - 1 - y) * w) + x]) = (d[((h - 1 - y) * w) + x], d[(y * w) + x]);
            }
        }
    }

    // Op_YCbCr420_bilinear_to_YCbCr444 / Op_YCbCr422_bilinear_to_YCbCr444
    private static YCbCrPlanes UpsampleTo444(YCbCrPlanes p)
    {
        var o = new YCbCrPlanes { W = p.W, H = p.H, Cw = p.W, Ch = p.H, BitDepth = p.BitDepth, Chroma = Hevc.HevcChromaFormat.Chroma444, Y = p.Y, Nclx = p.Nclx };
        bool is420 = p.Chroma == Hevc.HevcChromaFormat.Chroma420;
        o.Cb = is420 ? Upsample420(p.Cb!, p.Cw, p.W, p.H) : Upsample422(p.Cb!, p.Cw, p.W, p.H);
        o.Cr = is420 ? Upsample420(p.Cr!, p.Cw, p.W, p.H) : Upsample422(p.Cr!, p.Cw, p.W, p.H);
        return o;

        static ushort Mix31(int a, int b) => (ushort)(((3 * a) + b + 2) / 4);

        // upsample_chroma_plane_bilinear: borders from the edge chroma (3/4, 1/4 between neighbours), the inner 2x2
        // groups with the 9/3/3/1 weights
        static ushort[] Upsample420(ushort[] cin, int cs, int width, int height)
        {
            var o = new ushort[width * height];
            void Row(int inRow, int outRow)
            {
                o[outRow * width] = cin[inRow * cs];
                for (int cx = 0; cx < (width - 1) / 2; cx++)
                {
                    int a = cin[(inRow * cs) + cx], b = cin[(inRow * cs) + cx + 1];
                    o[(outRow * width) + (2 * cx) + 1] = Mix31(a, b);
                    o[(outRow * width) + (2 * cx) + 2] = Mix31(b, a);
                }

                if (width % 2 == 0) o[(outRow * width) + width - 1] = cin[(inRow * cs) + (width / 2) - 1];
            }

            void Column(int inCol, int outCol)
            {
                for (int cy = 0; cy < (height - 1) / 2; cy++)
                {
                    int a = cin[(cy * cs) + inCol], b = cin[((cy + 1) * cs) + inCol];
                    o[(((2 * cy) + 1) * width) + outCol] = Mix31(a, b);
                    o[(((2 * cy) + 2) * width) + outCol] = Mix31(b, a);
                }
            }

            Row(0, 0);
            Column(0, 0);
            if (width % 2 == 0) Column((width / 2) - 1, width - 1);
            if (height % 2 == 0) Row((height / 2) - 1, height - 1);
            for (int y = 1; y < height - 1; y += 2)
            {
                for (int x = 1; x < width - 1; x += 2)
                {
                    int cx = x / 2, cy = y / 2;
                    int c00 = cin[(cy * cs) + cx], c01 = cin[(cy * cs) + cx + 1];
                    int c10 = cin[((cy + 1) * cs) + cx], c11 = cin[((cy + 1) * cs) + cx + 1];
                    o[(y * width) + x] = (ushort)(((c00 * 9) + (c01 * 3) + (c10 * 3) + c11 + 8) / 16);
                    o[(y * width) + x + 1] = (ushort)(((c00 * 3) + (c01 * 9) + c10 + (c11 * 3) + 8) / 16);
                    o[((y + 1) * width) + x] = (ushort)(((c00 * 3) + c01 + (c10 * 9) + (c11 * 3) + 8) / 16);
                    o[((y + 1) * width) + x + 1] = (ushort)((c00 + (c01 * 3) + (c10 * 3) + (c11 * 9) + 8) / 16);
                }
            }

            return o;
        }

        static ushort[] Upsample422(ushort[] cin, int cs, int width, int height)
        {
            var o = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                o[y * width] = cin[y * cs];
                if (width % 2 == 0) o[(y * width) + width - 1] = cin[(y * cs) + (width / 2) - 1];
                for (int x = 1; x < width - 1; x += 2)
                {
                    int cx = x / 2;
                    int a = cin[(y * cs) + cx], b = cin[(y * cs) + cx + 1];
                    o[(y * width) + x] = Mix31(a, b);
                    o[(y * width) + x + 1] = Mix31(b, a);
                }
            }

            return o;
        }
    }
}
