// Port of x265 common/bitstream.{h,cpp} (Bitstream, SyntaxElementWriter) and encoder/nal.cpp (NALList::serialize,
// serializeSubstreams): RBSP bit writing, emulation prevention and Annex B start codes.
using System;

#if HIGH_BIT_DEPTH
namespace SharpImage.Formats.Hevc.X265.Hbd;
#else
namespace SharpImage.Formats.Hevc.X265;
#endif

/// <summary>class Bitstream: an RBSP byte FIFO with a partial byte.</summary>
internal sealed class X265Bitstream
{
    private byte[] fifo = new byte[1000];
    private uint byteOccupancy;
    private uint partialByteBits;
    private byte partialByte;

    public void ResetBits()
    {
        partialByteBits = byteOccupancy = 0;
        partialByte = 0;
    }

    public uint GetNumberOfWrittenBytes() => byteOccupancy;

    public uint GetNumberOfWrittenBits() => (byteOccupancy * 8) + partialByteBits;

    public ReadOnlySpan<byte> Fifo => fifo.AsSpan(0, (int)byteOccupancy);

    public void CopyBits(X265Bitstream stream)
    {
        partialByteBits = stream.partialByteBits;
        byteOccupancy = stream.byteOccupancy;
        partialByte = stream.partialByte;
    }

    private void PushBack(byte val)
    {
        if (byteOccupancy >= fifo.Length)
        {
            Array.Resize(ref fifo, fifo.Length * 2);
        }

        fifo[byteOccupancy++] = val;
    }

    public void Write(uint val, uint numBits)
    {
        uint totalPartialBits = partialByteBits + numBits;
        uint nextPartialBits = totalPartialBits & 7;
        byte nextHeldByte = (byte)(val << (int)(8 - nextPartialBits));
        uint writeBytes = totalPartialBits >> 3;

        if (writeBytes != 0)
        {
            // topword aligns m_partialByte with the msb of val
            uint topword = (numBits - nextPartialBits) & ~7u;
            uint writeBits = (topword < 32 ? (uint)partialByte << (int)topword : 0) | (val >> (int)nextPartialBits);
            switch (writeBytes)
            {
                case 4: PushBack((byte)(writeBits >> 24)); goto case 3;
                case 3: PushBack((byte)(writeBits >> 16)); goto case 2;
                case 2: PushBack((byte)(writeBits >> 8)); goto case 1;
                case 1: PushBack((byte)writeBits); break;
            }

            partialByte = nextHeldByte;
            partialByteBits = nextPartialBits;
        }
        else
        {
            partialByte |= nextHeldByte;
            partialByteBits = nextPartialBits;
        }
    }

    /// <summary>Only CABAC calls writeByte; the FIFO is then byte aligned.</summary>
    public void WriteByte(uint val) => PushBack((byte)val);

    public void WriteAlignOne()
    {
        uint numBits = (8 - partialByteBits) & 0x7;
        Write((1u << (int)numBits) - 1, numBits);
    }

    public void WriteAlignZero()
    {
        if (partialByteBits != 0)
        {
            PushBack(partialByte);
            partialByte = 0;
            partialByteBits = 0;
        }
    }

    /// <summary>Insert a 1 bit, then pad to byte alignment with zeros.</summary>
    public void WriteByteAlignment()
    {
        Write(1, 1);
        WriteAlignZero();
    }

    // SyntaxElementWriter
    public void WriteCode(uint code, uint length) => Write(code, length);

    public void WriteFlag(bool flag) => Write(flag ? 1u : 0u, 1);

    public void WriteUvlc(uint code)
    {
        ++code;
        int idx = X265Const.Bsr(code);
        uint length = ((uint)idx * 2) + 1;
        Write(0, length >> 1);
        Write(code, (length + 1) >> 1);
    }

    public void WriteSvlc(int code)
    {
        uint ucode = code <= 0 ? (uint)(-code) << 1 : ((uint)code << 1) - 1;
        WriteUvlc(ucode);
    }
}

/// <summary>class NALList (encoder/nal.cpp): Annex B NAL units with emulation prevention.</summary>
internal sealed class X265NalList
{
    public readonly System.Collections.Generic.List<(int Type, byte[] Data)> Nals = new();
    private byte[] extraBuffer = Array.Empty<byte>();
    private int extraOccupancy;

    /// <summary>NALList::serialize: start code (4 bytes for the first NAL and VPS / SPS / PPS, else 3), the 16-bit
    /// header, the emulation-prevented payload, then the substreams escaped by serializeSubstreams.</summary>
    public void Serialize(int nalUnitType, X265Bitstream bs, int layerId = 0, int temporalID = 1)
    {
        ReadOnlySpan<byte> bpayload = bs.Fifo;
        uint payloadSize = (uint)bpayload.Length;
        var o = new byte[4 + 2 + payloadSize + (payloadSize >> 1) + extraOccupancy + 1];
        int bytes = 0;
        bool unspecified = nalUnitType == 62;
        if (Nals.Count == 0 || nalUnitType == 32 || nalUnitType == 33 || nalUnitType == 34 || unspecified)
        {
            o[bytes++] = 0;
        }

        o[bytes++] = 0;
        o[bytes++] = 0;
        o[bytes++] = 1;

        // forbidden_zero_bit, nal_unit_type, nuh_layer_id (x265 writes layerId << 3 into the second byte), temporal id
        o[bytes++] = (byte)(nalUnitType << 1);
        o[bytes++] = (byte)((layerId << 3) | temporalID);

        // 7.4.1: no 0x000000 / 0x000001 / 0x000002 at a byte-aligned position (x265 checks the three bytes written
        // so far, one payload byte behind)
        for (uint i = 0; i < payloadSize; i++)
        {
            if (i > 2 && o[bytes - 2] == 0 && o[bytes - 3] == 0 && o[bytes - 1] <= 0x03 && !unspecified)
            {
                o[bytes] = o[bytes - 1];
                o[bytes - 1] = 0x03;
                bytes++;
            }

            o[bytes++] = bpayload[(int)i];
        }

        if (extraOccupancy != 0)
        {
            // these bytes were escaped by serializeSubstreams
            Array.Copy(extraBuffer, 0, o, bytes, extraOccupancy);
            bytes += extraOccupancy;
            extraOccupancy = 0;
        }

        // 7.4.1.1: an RBSP ending in 0x00 gets a final 0x03
        if (o[bytes - 1] == 0)
        {
            o[bytes++] = 0x03;
        }

        Nals.Add((nalUnitType, o.AsSpan(0, bytes).ToArray()));
    }

    /// <summary>NALList::serializeSubstreams: concatenate the WPP substreams, inserting emulation prevention bytes
    /// (counted in the entry points), and return the largest substream size.</summary>
    public uint SerializeSubstreams(uint[] streamSizeBytes, uint streamCount, X265Bitstream[] streams)
    {
        uint maxStreamSize = 0;
        uint estSize = 0;
        for (uint s = 0; s < streamCount; s++)
        {
            estSize += streams[s].GetNumberOfWrittenBytes();
        }

        estSize += estSize >> 1;
        if (extraBuffer.Length < estSize)
        {
            extraBuffer = new byte[estSize];
        }

        uint bytes = 0;
        byte[] outp = extraBuffer;
        for (uint s = 0; s < streamCount; s++)
        {
            X265Bitstream stream = streams[s];
            uint inSize = stream.GetNumberOfWrittenBytes();
            ReadOnlySpan<byte> inBytes = stream.Fifo;
            uint prevBufSize = bytes;

            if (inSize != 0)
            {
                for (uint i = 0; i < inSize; i++)
                {
                    if (bytes >= 2 && outp[bytes - 2] == 0 && outp[bytes - 1] == 0 && inBytes[(int)i] <= 3)
                    {
                        // inject 0x03 to prevent emulating a start code
                        if (bytes >= outp.Length) Array.Resize(ref outp, outp.Length * 2 + 16);
                        outp[bytes++] = 3;
                    }

                    if (bytes >= outp.Length) Array.Resize(ref outp, outp.Length * 2 + 16);
                    outp[bytes++] = inBytes[(int)i];
                }
            }

            if (s < streamCount - 1)
            {
                streamSizeBytes[s] = bytes - prevBufSize;
                if (streamSizeBytes[s] > maxStreamSize)
                {
                    maxStreamSize = streamSizeBytes[s];
                }
            }
        }

        extraBuffer = outp;
        extraOccupancy = (int)bytes;
        return maxStreamSize;
    }
}
