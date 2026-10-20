// libjxl's field serialization (fields.h / fields.cc / enc_fields.cc): every header bundle is described once by a
// VisitFields method that a visitor walks to write, read or test all-default. Ported so headers come out exactly as
// libjxl writes them, including the all_default shortcuts and U32 selector choices.
using System;

namespace SharpImage.Formats.Jxl.Lj;

internal readonly struct LjU32
{
    public readonly bool Direct;
    public readonly uint Value;   // the direct value, or the offset
    public readonly int Bits;

    private LjU32(bool direct, uint value, int bits)
    {
        Direct = direct;
        Value = value;
        Bits = bits;
    }

    public static LjU32 Val(uint v) => new(true, v, 0);

    public static LjU32 BitsOnly(int bits) => new(false, 0, bits);

    public static LjU32 BitsOffset(int bits, uint offset) => new(false, offset, bits);
}

internal readonly struct LjU32Enc
{
    public readonly LjU32 D0, D1, D2, D3;

    public LjU32Enc(LjU32 d0, LjU32 d1, LjU32 d2, LjU32 d3)
    {
        D0 = d0;
        D1 = d1;
        D2 = d2;
        D3 = d3;
    }

    public LjU32 Get(int s) => s switch { 0 => D0, 1 => D1, 2 => D2, _ => D3 };
}

internal static class LjFields
{
    public static readonly LjU32Enc EnumEnc = new(LjU32.Val(0), LjU32.Val(1), LjU32.BitsOffset(4, 2), LjU32.BitsOffset(6, 18));

    // U32Coder::ChooseSelector: a direct match wins outright, otherwise the fewest total bits (first on ties).
    public static void ChooseSelector(LjU32Enc enc, uint value, out int selector, out int totalBits)
    {
        selector = 0;
        totalBits = 64;
        for (int s = 0; s < 4; s++)
        {
            LjU32 d = enc.Get(s);
            if (d.Direct)
            {
                if (d.Value == value)
                {
                    selector = s;
                    totalBits = 2;
                    return;
                }

                continue;
            }

            if (value < d.Value || (ulong)value >= d.Value + (1UL << d.Bits))
            {
                continue;
            }

            if (2 + d.Bits < totalBits)
            {
                selector = s;
                totalBits = 2 + d.Bits;
            }
        }

        if (totalBits == 64)
        {
            throw new InvalidOperationException($"No feasible U32 selector for {value}");
        }
    }

    public static void WriteU32(LjBitWriter w, uint value, LjU32 d0, LjU32 d1, LjU32 d2, LjU32 d3) =>
        WriteU32(w, new LjU32Enc(d0, d1, d2, d3), value);

    public static void WriteU32(LjBitWriter w, LjU32Enc enc, uint value)
    {
        ChooseSelector(enc, value, out int s, out _);
        w.Write(2, (ulong)s);
        LjU32 d = enc.Get(s);
        if (!d.Direct)
        {
            w.Write(d.Bits, value - d.Value);
        }
    }

    public static int U32Bits(uint value, LjU32 d0, LjU32 d1, LjU32 d2, LjU32 d3)
    {
        ChooseSelector(new LjU32Enc(d0, d1, d2, d3), value, out _, out int bits);
        return bits;
    }

    public static void WriteU64(LjBitWriter w, ulong value)
    {
        if (value == 0)
        {
            w.Write(2, 0);
        }
        else if (value <= 16)
        {
            w.Write(2, 1);
            w.Write(4, value - 1);
        }
        else if (value <= 272)
        {
            w.Write(2, 2);
            w.Write(8, value - 17);
        }
        else
        {
            w.Write(2, 3);
            w.Write(12, value & 4095);
            value >>= 12;
            int shift = 12;
            while (value > 0 && shift < 60)
            {
                w.Write(1, 1);
                w.Write(8, value & 255);
                value >>= 8;
                shift += 8;
            }

            if (value > 0)
            {
                w.Write(1, 1);
                w.Write(4, value & 15);
            }
            else
            {
                w.Write(1, 0);
            }
        }
    }

    public static void WriteF16(LjBitWriter w, float value)
    {
        uint bits32 = BitConverter.SingleToUInt32Bits(value);
        uint sign = bits32 >> 31;
        uint biasedExp32 = (bits32 >> 23) & 0xFF;
        uint mantissa32 = bits32 & 0x7FFFFF;
        int exp = (int)biasedExp32 - 127;
        if (exp > 15)
        {
            throw new InvalidOperationException("F16 value too big to encode");
        }

        if (exp < -24)
        {
            w.Write(16, 0);
            return;
        }

        uint biasedExp16, mantissa16;
        if (exp < -14)
        {
            biasedExp16 = 0;
            uint subExp = (uint)(-14 - exp);
            mantissa16 = (1u << (int)(10 - subExp)) + (mantissa32 >> (int)(13 + subExp));
        }
        else
        {
            biasedExp16 = (uint)(exp + 15);
            mantissa16 = mantissa32 >> 13;
        }

        w.Write(16, (sign << 15) | (biasedExp16 << 10) | mantissa16);
    }

    public static float F16ToFloat(uint bits16)
    {
        uint sign = bits16 >> 15;
        uint biasedExp = (bits16 >> 10) & 0x1F;
        uint mantissa = bits16 & 0x3FF;
        if (biasedExp == 31)
        {
            throw new InvalidOperationException("F16 infinity or NaN are not supported");
        }

        if (biasedExp == 0)
        {
            float v = (1.0f / 16384) * (mantissa * (1.0f / 1024));
            return sign != 0 ? -v : v;
        }

        uint bits32 = (sign << 31) | ((biasedExp + (127 - 15)) << 23) | (mantissa << (23 - 10));
        return BitConverter.UInt32BitsToSingle(bits32);
    }

    /// <summary>Bundle::Write.</summary>
    public static void Write(ILjBundle bundle, LjBitWriter w) => bundle.VisitFields(new LjWriteVisitor(w));

    /// <summary>Bundle::AllDefault.</summary>
    public static bool AllDefault(ILjBundle bundle)
    {
        var v = new LjAllDefaultVisitor();
        bundle.VisitFields(v);
        return v.Result;
    }

    /// <summary>Bundle::Read.</summary>
    public static void Read(ILjBundle bundle, Func<int, uint> readBits)
    {
        LjSetDefault(bundle);
        bundle.VisitFields(new LjReadVisitor(readBits));
    }

    /// <summary>Bundle::Read from a bit reader.</summary>
    public static void ReadInto(ILjBundle bundle, LjBitReader br) => Read(bundle, br.AsReadFunc());

    /// <summary>Bundle::Init / SetDefault: every field takes its default value.</summary>
    public static void LjSetDefault(ILjBundle bundle) => bundle.VisitFields(new LjSetDefaultVisitor());
}

internal interface ILjBundle
{
    void VisitFields(LjVisitor v);
}

internal abstract class LjVisitor
{
    public abstract void Bits(int bits, uint def, ref uint value);

    public abstract void U32(LjU32Enc enc, uint def, ref uint value);

    public void U32(LjU32 d0, LjU32 d1, LjU32 d2, LjU32 d3, uint def, ref uint value) => U32(new LjU32Enc(d0, d1, d2, d3), def, ref value);

    public abstract void U64(ulong def, ref ulong value);

    public abstract void F16(float def, ref float value);

    public virtual void Bool(bool def, ref bool value)
    {
        uint b = value ? 1u : 0u;
        Bits(1, def ? 1u : 0u, ref b);
        value = b == 1;
    }

    public void Enum(uint def, ref uint value) => U32(LjFields.EnumEnc, def, ref value);

    public virtual bool Conditional(bool condition) => condition;

    /// <summary>Returns true when the bundle is all-default and the rest of its fields must be skipped.</summary>
    public virtual bool AllDefault(ILjBundle bundle, ref bool allDefault)
    {
        Bool(true, ref allDefault);
        return allDefault;
    }

    public virtual void SetDefault(ILjBundle bundle) => LjFields.LjSetDefault(bundle);

    public virtual void VisitNested(ILjBundle bundle) => bundle.VisitFields(this);

    public virtual bool IsReading => false;

    public virtual void BeginExtensions(ref ulong extensions) => U64(0, ref extensions);

    public virtual void EndExtensions()
    {
    }
}

internal sealed class LjWriteVisitor : LjVisitor
{
    private readonly LjBitWriter w;

    public LjWriteVisitor(LjBitWriter w) => this.w = w;

    public override void Bits(int bits, uint def, ref uint value)
    {
        if (bits < 32 && value >= (1UL << bits))
        {
            throw new InvalidOperationException($"Value {value} too large for {bits} bits");
        }

        w.Write(bits, value);
    }

    public override void U32(LjU32Enc enc, uint def, ref uint value) => LjFields.WriteU32(w, enc, value);

    public override void U64(ulong def, ref ulong value) => LjFields.WriteU64(w, value);

    public override void F16(float def, ref float value) => LjFields.WriteF16(w, value);

    // CanEncodeVisitor sets all_default from Bundle::AllDefault before the write visitor emits it.
    public override bool AllDefault(ILjBundle bundle, ref bool allDefault)
    {
        allDefault = LjFields.AllDefault(bundle);
        Bool(true, ref allDefault);
        return allDefault;
    }

    public override void BeginExtensions(ref ulong extensions)
    {
        if (extensions != 0)
        {
            throw new NotSupportedException("Bundle extensions are not written by this encoder");
        }

        base.BeginExtensions(ref extensions);
    }
}

internal sealed class LjAllDefaultVisitor : LjVisitor
{
    public bool Result = true;

    public override void Bits(int bits, uint def, ref uint value) => Result &= value == def;

    public override void U32(LjU32Enc enc, uint def, ref uint value) => Result &= value == def;

    public override void U64(ulong def, ref ulong value) => Result &= value == def;

    public override void F16(float def, ref float value) => Result &= MathF.Abs(value - def) < 1E-6f;

    public override bool AllDefault(ILjBundle bundle, ref bool allDefault) => false;
}

internal sealed class LjSetDefaultVisitor : LjVisitor
{
    public override void Bits(int bits, uint def, ref uint value) => value = def;

    public override void U32(LjU32Enc enc, uint def, ref uint value) => value = def;

    public override void U64(ulong def, ref ulong value) => value = def;

    public override void F16(float def, ref float value) => value = def;

    public override bool AllDefault(ILjBundle bundle, ref bool allDefault)
    {
        allDefault = true;
        return false;
    }

    public override void SetDefault(ILjBundle bundle)
    {
    }

    // Defaults are visited unconditionally so fields behind any condition are initialized too.
    public override bool Conditional(bool condition) => true;

    public override void BeginExtensions(ref ulong extensions) => extensions = 0;
}

internal sealed class LjReadVisitor : LjVisitor
{
    private readonly Func<int, uint> read;
    private ulong pendingExtensions;

    public LjReadVisitor(Func<int, uint> read) => this.read = read;

    public override bool IsReading => true;

    public override void Bits(int bits, uint def, ref uint value) => value = bits == 0 ? 0 : read(bits);

    public override void U32(LjU32Enc enc, uint def, ref uint value)
    {
        LjU32 d = enc.Get((int)read(2));
        value = d.Direct ? d.Value : (d.Bits == 0 ? 0 : read(d.Bits)) + d.Value;
    }

    public override void U64(ulong def, ref ulong value)
    {
        uint selector = read(2);
        if (selector == 0)
        {
            value = 0;
            return;
        }

        if (selector == 1)
        {
            value = 1 + read(4);
            return;
        }

        if (selector == 2)
        {
            value = 17 + read(8);
            return;
        }

        ulong result = read(12);
        int shift = 12;
        while (read(1) != 0)
        {
            if (shift == 60)
            {
                result |= (ulong)read(4) << shift;
                break;
            }

            result |= (ulong)read(8) << shift;
            shift += 8;
        }

        value = result;
    }

    public override void F16(float def, ref float value) => value = LjFields.F16ToFloat(read(16));

    public override void BeginExtensions(ref ulong extensions)
    {
        U64(0, ref extensions);
        pendingExtensions = extensions;
    }

    public override void EndExtensions()
    {
        if (pendingExtensions == 0)
        {
            return;
        }

        // Skip the extension payload: one U64 bit count per set extension bit.
        ulong total = 0;
        for (ulong ext = pendingExtensions; ext != 0; ext &= ext - 1)
        {
            ulong n = 0;
            U64(0, ref n);
            total += n;
        }

        for (ulong i = 0; i < total; i++)
        {
            read(1);
        }

        pendingExtensions = 0;
    }
}
