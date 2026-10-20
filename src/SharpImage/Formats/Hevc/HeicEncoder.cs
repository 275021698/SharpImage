// Assembles a complete HEIC (HEVC still image in ISOBMFF/HEIF) from an RGB frame: pads to the CTU
// grid, converts RGB->YUV 4:2:0 (full-range BT.601), runs the HEVC intra encoder, and muxes the
// coded slice + VPS/SPS/PPS into ftyp/meta/mdat boxes that SharpImage's own HeifCoder (and other
// HEIF readers) can decode.
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImage.Formats.Hevc;

internal static class HeicEncoder
{
    /// <summary>
    /// Encodes 8-bit RGB (row-major, channels: r,g,b[,a]) into a HEIC file. With 4 channels the alpha plane is a second,
    /// hidden HEVC item linked to the colour item as its alpha auxiliary image ('auxl' + auxC), as libheif writes it.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int channels, int qp, bool signDataHiding)
    {
        int pw = (width + 31) & ~31;
        int ph = (height + 31) & ~31;

        // RGB -> YUV 4:2:0, full-range BT.601 (JPEG), edge-replicated into the padded area.
        var luma = new byte[pw * ph];
        var cbFull = new int[pw * ph];
        var crFull = new int[pw * ph];
        byte[]? alpha = channels == 4 ? new byte[pw * ph] : null;
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, height - 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(x, width - 1);
                int o = ((sy * width) + sx) * channels;
                int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                luma[(y * pw) + x] = (byte)Clamp8(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);
                cbFull[(y * pw) + x] = Clamp8((((-11056 * r) - (21712 * g) + (32768 * b) + 8388608) >> 16));
                crFull[(y * pw) + x] = Clamp8((((32768 * r) - (27440 * g) - (5328 * b) + 8388608) >> 16));
                if (alpha != null) alpha[(y * pw) + x] = rgb[o + 3];
            }
        }

        int cw = pw / 2, chh = ph / 2;
        var cb = new byte[cw * chh];
        var cr = new byte[cw * chh];
        for (int y = 0; y < chh; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int x2 = x * 2, y2 = y * 2;
                int sumCb = cbFull[(y2 * pw) + x2] + cbFull[(y2 * pw) + x2 + 1] + cbFull[((y2 + 1) * pw) + x2] + cbFull[((y2 + 1) * pw) + x2 + 1];
                int sumCr = crFull[(y2 * pw) + x2] + crFull[(y2 * pw) + x2 + 1] + crFull[((y2 + 1) * pw) + x2] + crFull[((y2 + 1) * pw) + x2 + 1];
                cb[(y * cw) + x] = (byte)((sumCb + 2) >> 2);
                cr[(y * cw) + x] = (byte)((sumCr + 2) >> 2);
            }
        }

        // 4:2:0 can only crop (conformance window) in 2-pixel steps: the coded picture is the even-rounded size, and
        // an odd width / height is cropped by a 'clap' box (as libheif writes it).
        int evenW = (width + 1) & ~1, evenH = (height + 1) & ~1;
        var (hvcC, sample) = EncodeItem(luma, cb, cr, pw, ph, evenW, evenH, qp, signDataHiding);
        byte[]? alphaHvcC = null, alphaSample = null;
        if (alpha != null)
        {
            // the alpha plane is the luma of a 4:2:0 picture with neutral chroma
            var neutral = new byte[cw * chh];
            Array.Fill(neutral, (byte)128);
            (alphaHvcC, alphaSample) = EncodeItem(alpha, neutral, (byte[])neutral.Clone(), pw, ph, evenW, evenH, qp, signDataHiding);
        }

        return BuildIsoBmff(width, height, evenW, evenH, hvcC, sample, alphaHvcC, alphaSample);
    }

    /// <summary>One HEVC still: its hvcC configuration and the 4-byte-length-prefixed slice NAL (the item's data).</summary>
    private static (byte[] HvcC, byte[] Sample) EncodeItem(byte[] luma, byte[] cb, byte[] cr, int pw, int ph, int codedW,
        int codedH, int qp, bool signDataHiding)
    {
        var enc = new HevcIntraFrameEncoder(luma, cb, cr, pw, ph, qp, signDataHiding);
        byte[] cabac = enc.EncodeSliceData();

        var hdr = new HevcBitWriter();
        HevcStreamWriter.WriteSliceHeader(hdr);
        byte[] sliceRbsp = Concat(hdr.ToArray(), cabac);
        byte[] sliceNal = HevcStreamWriter.WrapNal(HevcStreamWriter.NalIdrWRadl, sliceRbsp);

        // Parameter sets (NAL units, for the hvcC arrays).
        byte[] vps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalVps, HevcStreamWriter.BuildVps());
        byte[] sps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalSps, HevcStreamWriter.BuildSps(pw, ph, pw - codedW, ph - codedH));
        byte[] pps = HevcStreamWriter.WrapNal(HevcStreamWriter.NalPps, HevcStreamWriter.BuildPps(qp - 26, signDataHiding));

        // item data = 4-byte-length-prefixed slice NAL.
        var sample = new List<byte>();
        AppendU32(sample, (uint)sliceNal.Length);
        sample.AddRange(sliceNal);
        return (BuildHvcC(vps, sps, pps), sample.ToArray());
    }

    private static byte[] BuildHvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        var c = new List<byte>();
        c.Add(1);           // configurationVersion
        c.Add(0x01);        // profile_space(0)|tier(0)|profile_idc(1)
        AppendU32(c, 0x60000000); // general_profile_compatibility_flags
        for (int i = 0; i < 6; i++) c.Add(0); // general_constraint_indicator_flags (48 bits)
        c.Add(153);         // general_level_idc = 5.1
        c.Add(0xF0); c.Add(0x00); // reserved(4=1)|min_spatial_segmentation_idc(12)=0
        c.Add(0xFC);        // reserved(6=1)|parallelismType(2)=0
        c.Add(0xFD);        // reserved(6=1)|chromaFormat(2)=1 (4:2:0)
        c.Add(0xF8);        // reserved(5=1)|bitDepthLumaMinus8(3)=0
        c.Add(0xF8);        // reserved(5=1)|bitDepthChromaMinus8(3)=0
        c.Add(0); c.Add(0); // avgFrameRate
        c.Add(0x0F);        // constantFrameRate(0)|numTemporalLayers(1)|temporalIdNested(1)|lengthSizeMinusOne(3)
        c.Add(3);           // numOfArrays

        AppendHvcCArray(c, HevcStreamWriter.NalVps, vps);
        AppendHvcCArray(c, HevcStreamWriter.NalSps, sps);
        AppendHvcCArray(c, HevcStreamWriter.NalPps, pps);
        return c.ToArray();
    }

    private static void AppendHvcCArray(List<byte> c, int nalType, byte[] nal)
    {
        c.Add((byte)(0x80 | (nalType & 0x3F))); // array_completeness=1 | reserved | NAL_unit_type
        c.Add(0); c.Add(1);                     // numNalus = 1
        c.Add((byte)(nal.Length >> 8)); c.Add((byte)(nal.Length & 0xFF));
        c.AddRange(nal);
    }

    private static byte[] BuildIsoBmff(int width, int height, int codedW, int codedH, byte[] hvcC, byte[] sample,
        byte[]? alphaHvcC, byte[]? alphaSample)
    {
        bool hasAlpha = alphaHvcC != null;
        byte[] ftyp = Box("ftyp", Concat(Fourcc("heic"), U32(0), Fourcc("mif1"), Fourcc("heic")));

        // property container: ipco { hvcC, ispe, colr[, clap][, alpha hvcC, auxC] }
        byte[] ispe = FullBox("ispe", 0, 0, Concat(U32((uint)codedW), U32((uint)codedH)));
        byte[] colr = Box("colr", Concat(Fourcc("nclx"), U16(1), U16(13), U16(6), new byte[] { 0x80 })); // BT.601, full range
        bool crop = codedW != width || codedH != height;
        // clap: clean aperture width/height and the offset of its centre from the coded picture's centre (all as
        // fractions N/D); dropping the last column / row moves the centre by -1/2.
        byte[] clap = Box("clap", Concat(U32((uint)width), U32(1), U32((uint)height), U32(1),
            U32((uint)(width - codedW)), U32(2), U32((uint)(height - codedH)), U32(2)));
        var props = new List<byte[]> { Box("hvcC", hvcC), ispe, colr };
        if (crop) props.Add(clap);
        int alphaHvcCIndex = 0, auxCIndex = 0;
        if (hasAlpha)
        {
            props.Add(Box("hvcC", alphaHvcC!));
            alphaHvcCIndex = props.Count;
            props.Add(FullBox("auxC", 0, 0, Concat(System.Text.Encoding.ASCII.GetBytes("urn:mpeg:hevc:2015:auxid:1"), new byte[] { 0 })));
            auxCIndex = props.Count;
        }
        byte[] ipco = Box("ipco", Concat(props.ToArray()));

        // ipma: item 1 -> hvcC (essential), ispe, colr[, clap (essential)];
        //       item 2 (alpha) -> its hvcC (essential), ispe[, clap (essential)], auxC (essential)
        var e1 = new List<byte> { 0x81, 0x02, 0x03 };
        if (crop) e1.Add(0x84);
        var ipmaEntries = new List<byte[]> { Concat(U16(1), new[] { (byte)e1.Count }, e1.ToArray()) };
        if (hasAlpha)
        {
            var e2 = new List<byte> { (byte)(0x80 | alphaHvcCIndex), 0x02 };
            if (crop) e2.Add(0x84);
            e2.Add((byte)(0x80 | auxCIndex));
            ipmaEntries.Add(Concat(U16(2), new[] { (byte)e2.Count }, e2.ToArray()));
        }
        byte[] ipma = FullBox("ipma", 0, 0, Concat(U32((uint)ipmaEntries.Count), Concat(ipmaEntries.ToArray())));
        byte[] iprp = Box("iprp", Concat(ipco, ipma));

        byte[] hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Fourcc("pict"), U32(0), U32(0), U32(0), new byte[] { 0 }));
        byte[] pitm = FullBox("pitm", 0, 0, U16(1));
        // iinf { infe (item 1, hvc1)[, infe (item 2, hvc1, hidden)] }
        byte[] infe1 = FullBox("infe", 2, 0, Concat(U16(1), U16(0), Fourcc("hvc1"), new byte[] { 0 }));
        byte[] iinf = hasAlpha
            ? FullBox("iinf", 0, 0, Concat(U16(2), infe1, FullBox("infe", 2, 1, Concat(U16(2), U16(0), Fourcc("hvc1"), new byte[] { 0 }))))
            : FullBox("iinf", 0, 0, Concat(U16(1), infe1));
        // iref: item 2 is the alpha auxiliary image of item 1
        byte[] iref = hasAlpha ? FullBox("iref", 0, 0, Box("auxl", Concat(U16(2), U16(1), U16(1)))) : Array.Empty<byte>();

        byte[] mdatPayload = hasAlpha ? Concat(sample, alphaSample!) : sample;

        // iloc (version 1, offset_size 4, length_size 4, no base offset): one extent per item in mdat. The meta size
        // does not depend on the offsets, so lay it out once with zeros, then again with the real offsets.
        byte[] BuildMeta(uint dataOffset)
        {
            var items = new List<byte[]> { IlocItem(1, dataOffset, (uint)sample.Length) };
            if (hasAlpha) items.Add(IlocItem(2, dataOffset + (uint)sample.Length, (uint)alphaSample!.Length));
            byte[] iloc = FullBox("iloc", 1, 0, Concat(new byte[] { 0x44, 0x00 }, U16(items.Count), Concat(items.ToArray())));
            return FullBox("meta", 0, 0, Concat(hdlr, pitm, iinf, iref, iprp, iloc));
        }

        byte[] meta = BuildMeta(0);
        uint mdatPayloadOffset = (uint)(ftyp.Length + meta.Length + 8);
        meta = BuildMeta(mdatPayloadOffset);
        return Concat(ftyp, meta, Box("mdat", mdatPayload));
    }

    private static byte[] IlocItem(int itemId, uint offset, uint length) =>
        Concat(U16(itemId), U16(0) /* construction_method 0 */, U16(0) /* data_reference_index */, U16(1) /* extents */,
            U32(offset), U32(length));

    // ---- box helpers ----
    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Fourcc(type), payload);

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
        => Box(type, Concat(new byte[] { version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags }, payload));

    private static byte[] Fourcc(string s) => new[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] };

    private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static void AppendU32(List<byte> l, uint v)
    {
        l.Add((byte)(v >> 24));
        l.Add((byte)(v >> 16));
        l.Add((byte)(v >> 8));
        l.Add((byte)v);
    }

    private static void WriteU32(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v >> 24);
        buf[pos + 1] = (byte)(v >> 16);
        buf[pos + 2] = (byte)(v >> 8);
        buf[pos + 3] = (byte)v;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (byte[] p in parts) len += p.Length;
        var outp = new byte[len];
        int o = 0;
        foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }

    private static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
}
