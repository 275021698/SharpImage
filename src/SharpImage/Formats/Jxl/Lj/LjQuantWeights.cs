// libjxl 0.12 dequantization matrices (quant_weights.h/.cc): the default library, every encoding mode, bitstream
// decoding and the weight computation. Line-by-line port; SIMD paths use fused multiply-add as libjxl's AVX2 build.
using System;
using System.IO;

namespace SharpImage.Formats.Jxl.Lj;

internal sealed class LjQuantEncoding
{
    public const int ModeLibrary = 0, ModeId = 1, ModeDct2 = 2, ModeDct4 = 3, ModeDct4x8 = 4, ModeAfv = 5, ModeDct = 6, ModeRaw = 7;

    public int Mode = ModeLibrary;
    public int Predefined;
    public float[,] IdWeights = new float[3, 3];
    public float[,] Dct2Weights = new float[3, 6];
    public float[,] Dct4Multipliers = new float[3, 2];
    public float[] Dct4x8Multipliers = new float[3];
    public float[,] AfvWeights = new float[3, 9];
    public int NumDistanceBands;
    public float[,] DistanceBands = new float[3, 17];
    public int NumDistanceBandsAfv4x4;
    public float[,] DistanceBandsAfv4x4 = new float[3, 17];
    public int[]? QTable;
    public float QTableDen = 1f / (8 * 255);

    public static LjQuantEncoding Dct(float[,] bands, int num) => new() { Mode = ModeDct, DistanceBands = Pad(bands), NumDistanceBands = num };

    private static float[,] Pad(float[,] b)
    {
        var r = new float[3, 17];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < b.GetLength(1); i++)
            {
                r[c, i] = b[c, i];
            }
        }

        return r;
    }

    // DequantMatricesLibraryDef.
    private static float V(double v) => (float)v;

    public static readonly LjQuantEncoding[] Library = BuildLibrary();

    private static LjQuantEncoding[] BuildLibrary()
    {
        float[,] dct4x4Bands = { { V(2200.0), V(0.0), V(0.0), V(0.0) }, { V(392.0), V(0.0), V(0.0), V(0.0) }, { V(112.0), V(-0.25), V(-0.25), V(-0.5) } };
        float[,] dct4x8Bands =
        {
            { V(2198.050556016380522), V(-0.96269623020744692), V(-0.76194253026666783), V(-0.6551140670773547) },
            { V(764.3655248643528689), V(-0.92630200888366945), V(-0.9675229603596517), V(-0.27845290869168118) },
            { V(527.107573587542228), V(-1.4594385811273854), V(-1.450082094097871593), V(-1.5843722511996204) },
        };
        float[,] big0 =
        {
            { V(-1.025), V(-0.78), V(-0.65012), V(-0.19041574084286472), V(-0.20819395464), V(-0.421064), V(-0.32733845535848671) },
            { V(-0.3041958212306401), V(-0.3633036457487539), V(-0.35660379990111464), V(-0.3443074455424403), V(-0.33699592683512467), V(-0.30180866526242109), V(-0.27321683125358037) },
            { V(-1.2), V(-1.2), V(-0.8), V(-0.7), V(-0.7), V(-0.4), V(-0.5) },
        };
        LjQuantEncoding Big(double x, double y, double b)
        {
            var bands = new float[3, 8];
            bands[0, 0] = V(x);
            bands[1, 0] = V(y);
            bands[2, 0] = V(b);
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 7; i++)
                {
                    bands[c, i + 1] = big0[c, i];
                }
            }

            return Dct(bands, 8);
        }

        var lib = new LjQuantEncoding[17];
        lib[0] = Dct(new float[,] { { V(3150.0), V(0.0), V(-0.4), V(-0.4), V(-0.4), V(-2.0) }, { V(560.0), V(0.0), V(-0.3), V(-0.3), V(-0.3), V(-0.3) }, { V(512.0), V(-2.0), V(-1.0), V(0.0), V(-1.0), V(-2.0) } }, 6);
        lib[1] = new LjQuantEncoding { Mode = ModeId, IdWeights = new float[,] { { V(280.0), V(3160.0), V(3160.0) }, { V(60.0), V(864.0), V(864.0) }, { V(18.0), V(200.0), V(200.0) } } };
        lib[2] = new LjQuantEncoding
        {
            Mode = ModeDct2,
            Dct2Weights = new float[,]
            {
                { V(3840.0), V(2560.0), V(1280.0), V(640.0), V(480.0), V(300.0) },
                { V(960.0), V(640.0), V(320.0), V(180.0), V(140.0), V(120.0) },
                { V(640.0), V(320.0), V(128.0), V(64.0), V(32.0), V(16.0) },
            },
        };
        lib[3] = new LjQuantEncoding
        {
            Mode = ModeDct4, DistanceBands = Pad(dct4x4Bands), NumDistanceBands = 4,
            Dct4Multipliers = new float[,] { { 1, 1 }, { 1, 1 }, { 1, 1 } },
        };
        lib[4] = Dct(new float[,]
        {
            { V(8996.8725711814115328), V(-1.3000777393353804), V(-0.49424529824571225), V(-0.439093774457103443), V(-0.6350101832695744), V(-0.90177264050827612), V(-1.6162099239887414) },
            { V(3191.48366296844234752), V(-0.67424582104194355), V(-0.80745813428471001), V(-0.44925837484843441), V(-0.35865440981033403), V(-0.31322389111877305), V(-0.37615025315725483) },
            { V(1157.50408145487200256), V(-2.0531423165804414), V(-1.4), V(-0.50687130033378396), V(-0.42708730624733904), V(-1.4856834539296244), V(-4.9209142884401604) },
        }, 7);
        lib[5] = Dct(new float[,]
        {
            { V(15718.40830982518931456), V(-1.025), V(-0.98), V(-0.9012), V(-0.4), V(-0.48819395464), V(-0.421064), V(-0.27) },
            { V(7305.7636810695983104), V(-0.8041958212306401), V(-0.7633036457487539), V(-0.55660379990111464), V(-0.49785304658857626), V(-0.43699592683512467), V(-0.40180866526242109), V(-0.27321683125358037) },
            { V(3803.53173721215041536), V(-3.060733579805728), V(-2.0413270132490346), V(-2.0235650159727417), V(-0.5495389509954993), V(-0.4), V(-0.4), V(-0.3) },
        }, 8);
        lib[6] = Dct(new float[,]
        {
            { V(7240.7734393502), V(-0.7), V(-0.7), V(-0.2), V(-0.2), V(-0.2), V(-0.5) },
            { V(1448.15468787004), V(-0.5), V(-0.5), V(-0.5), V(-0.2), V(-0.2), V(-0.2) },
            { V(506.854140754517), V(-1.4), V(-0.2), V(-0.5), V(-0.5), V(-1.5), V(-3.6) },
        }, 7);
        lib[7] = Dct(new float[,]
        {
            { V(16283.2494710648897), V(-1.7812845336559429), V(-1.6309059012653515), V(-1.0382179034313539), V(-0.85), V(-0.7), V(-0.9), V(-1.2360638576849587) },
            { V(5089.15750884921511936), V(-0.320049391452786891), V(-0.35362849922161446), V(-0.30340000000000003), V(-0.61), V(-0.5), V(-0.5), V(-0.6) },
            { V(3397.77603275308720128), V(-0.321327362693153371), V(-0.34507619223117997), V(-0.70340000000000003), V(-0.9), V(-1.0), V(-1.0), V(-1.1754605576265209) },
        }, 8);
        lib[8] = Dct(new float[,]
        {
            { V(13844.97076442300573), V(-0.97113799999999995), V(-0.658), V(-0.42026), V(-0.22712), V(-0.2206), V(-0.226), V(-0.6) },
            { V(4798.964084220744293), V(-0.61125308982767057), V(-0.83770786552491361), V(-0.79014862079498627), V(-0.2692727459704829), V(-0.38272769465388551), V(-0.22924222653091453), V(-0.20719098826199578) },
            { V(1807.236946760964614), V(-1.2), V(-1.2), V(-0.7), V(-0.7), V(-0.7), V(-0.4), V(-0.5) },
        }, 8);
        lib[9] = new LjQuantEncoding { Mode = ModeDct4x8, DistanceBands = Pad(dct4x8Bands), NumDistanceBands = 4, Dct4x8Multipliers = [1, 1, 1] };
        lib[10] = new LjQuantEncoding
        {
            Mode = ModeAfv, DistanceBands = Pad(dct4x8Bands), NumDistanceBands = 4, DistanceBandsAfv4x4 = Pad(dct4x4Bands), NumDistanceBandsAfv4x4 = 4,
            AfvWeights = new float[,]
            {
                { V(3072.0), V(3072.0), V(256.0), V(256.0), V(256.0), V(414.0), V(0.0), V(0.0), V(0.0) },
                { V(1024.0), V(1024.0), V(50), V(50), V(50), V(58.0), V(0.0), V(0.0), V(0.0) },
                { V(384.0), V(384.0), V(12.0), V(12.0), V(12.0), V(22.0), V(-0.25), V(-0.25), V(-0.25) },
            },
        };
        lib[11] = Big(0.9 * 26629.073922049845, 0.9 * 9311.3238710010046, 0.9 * 4992.2486445538634);
        lib[12] = Big(0.65 * 23629.073922049845, 0.65 * 8611.3238710010046, 0.65 * 4492.2486445538634);
        lib[13] = Big(1.8 * 26629.073922049845, 1.8 * 9311.3238710010046, 1.8 * 4992.2486445538634);
        lib[14] = Big(1.3 * 23629.073922049845, 1.3 * 8611.3238710010046, 1.3 * 4492.2486445538634);
        lib[15] = Big(3.6 * 26629.073922049845, 3.6 * 9311.3238710010046, 3.6 * 4992.2486445538634);
        lib[16] = Big(2.6 * 23629.073922049845, 2.6 * 8611.3238710010046, 2.6 * 4492.2486445538634);
        return lib;
    }
}

internal sealed class LjDequantMatrices
{
    public const int NumQuantTables = 17;
    public static readonly int[] RequiredSizeX = [1, 1, 1, 1, 2, 4, 1, 1, 2, 1, 1, 8, 4, 16, 8, 32, 16];
    public static readonly int[] RequiredSizeY = [1, 1, 1, 1, 2, 4, 2, 4, 4, 1, 1, 8, 8, 16, 16, 32, 32];

    /// <summary>kAcStrategyToQuantTableMap.</summary>
    public static readonly int[] AcStrategyToQuantTable = [0, 1, 2, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 10, 11, 12, 12, 13, 14, 14, 15, 16, 16];

    private const int TotalTableSize = 2056 * 64 * 3;
    private static readonly float[] KInvDcQuant = [4096.0f, 512.0f, 256.0f];

    public LjQuantEncoding[] Encodings = new LjQuantEncoding[NumQuantTables];
    public float[] DcQuant = [1.0f / 4096.0f, 1.0f / 512.0f, 1.0f / 256.0f];
    public float[] InvDcQuant = [4096.0f, 512.0f, 256.0f];
    private readonly float[] table = new float[TotalTableSize];
    private readonly float[] invTable = new float[TotalTableSize];
    private readonly int[] tableOffsets = new int[27 * 3];
    private uint computedMask;

    public LjDequantMatrices()
    {
        for (int i = 0; i < NumQuantTables; i++)
        {
            Encodings[i] = new LjQuantEncoding { Mode = LjQuantEncoding.ModeLibrary };
        }

        var offsets = new int[NumQuantTables * 3];
        int pos = 0;
        for (int i = 0; i < NumQuantTables; i++)
        {
            int num = RequiredSizeX[i] * RequiredSizeY[i] * 64;
            for (int c = 0; c < 3; c++)
            {
                offsets[(3 * i) + c] = pos + (c * num);
            }

            pos += 3 * num;
        }

        for (int i = 0; i < 27; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                tableOffsets[(i * 3) + c] = offsets[(AcStrategyToQuantTable[i] * 3) + c];
            }
        }
    }

    public float[] Table => table;

    public float[] InvTable => invTable;

    /// <summary>Offset of the dequant matrix for a raw AC strategy and channel.</summary>
    public int MatrixOffset(int rawStrategy, int c) => tableOffsets[(rawStrategy * 3) + c];

    private static Exception Fail(string m) => new InvalidDataException("JXL quant: " + m);

    public void DecodeDc(LjBitReader br)
    {
        bool allDefault = br.ReadBits(1) != 0;
        if (!allDefault)
        {
            for (int c = 0; c < 3; c++)
            {
                DcQuant[c] = LjFields.F16ToFloat((uint)br.ReadBits(16)) * (1.0f / 128.0f);
                if (DcQuant[c] < 1e-8f)
                {
                    throw Fail("Invalid dc_quant");
                }

                InvDcQuant[c] = 1.0f / DcQuant[c];
            }
        }
    }

    private static void DecodeDctParams(LjBitReader br, float[,] bands, out int num)
    {
        num = (int)br.ReadBits(4) + 1;
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < num; i++)
            {
                bands[c, i] = LjFields.F16ToFloat((uint)br.ReadBits(16));
            }

            if (bands[c, 0] < 1e-8f)
            {
                throw Fail("Distance band seed is too small");
            }

            bands[c, 0] *= 64.0f;
        }
    }

    /// <summary>DequantMatrices::Decode. <paramref name="decodeRaw"/> decodes a RAW table's modular stream.</summary>
    public void Decode(LjBitReader br, Func<int, int, int, LjBitReader, int[]> decodeRaw)
    {
        bool allDefault = br.ReadBits(1) != 0;
        for (int i = 0; i < NumQuantTables; i++)
        {
            Encodings[i] = new LjQuantEncoding { Mode = LjQuantEncoding.ModeLibrary };
        }

        if (!allDefault)
        {
            for (int i = 0; i < NumQuantTables; i++)
            {
                LjQuantEncoding e = Encodings[i];
                int reqX = RequiredSizeX[i], reqY = RequiredSizeY[i];
                int requiredSize = reqX * reqY;
                int mode = (int)br.ReadBits(3);
                switch (mode)
                {
                    case LjQuantEncoding.ModeLibrary:
                        e.Predefined = 0;
                        break;
                    case LjQuantEncoding.ModeId:
                        if (requiredSize != 1)
                        {
                            throw Fail("Invalid mode");
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                e.IdWeights[c, k] = LjFields.F16ToFloat((uint)br.ReadBits(16));
                                if (MathF.Abs(e.IdWeights[c, k]) < 1e-8f)
                                {
                                    throw Fail("ID Quantizer is too small");
                                }

                                e.IdWeights[c, k] *= 64;
                            }
                        }

                        break;
                    case LjQuantEncoding.ModeDct2:
                        if (requiredSize != 1)
                        {
                            throw Fail("Invalid mode");
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            for (int k = 0; k < 6; k++)
                            {
                                e.Dct2Weights[c, k] = LjFields.F16ToFloat((uint)br.ReadBits(16));
                                if (MathF.Abs(e.Dct2Weights[c, k]) < 1e-8f)
                                {
                                    throw Fail("Quantizer is too small");
                                }

                                e.Dct2Weights[c, k] *= 64;
                            }
                        }

                        break;
                    case LjQuantEncoding.ModeDct4x8:
                        if (requiredSize != 1)
                        {
                            throw Fail("Invalid mode");
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            e.Dct4x8Multipliers[c] = LjFields.F16ToFloat((uint)br.ReadBits(16));
                            if (MathF.Abs(e.Dct4x8Multipliers[c]) < 1e-8f)
                            {
                                throw Fail("DCT4X8 multiplier is too small");
                            }
                        }

                        DecodeDctParams(br, e.DistanceBands, out e.NumDistanceBands);
                        break;
                    case LjQuantEncoding.ModeDct4:
                        if (requiredSize != 1)
                        {
                            throw Fail("Invalid mode");
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            for (int k = 0; k < 2; k++)
                            {
                                e.Dct4Multipliers[c, k] = LjFields.F16ToFloat((uint)br.ReadBits(16));
                                if (MathF.Abs(e.Dct4Multipliers[c, k]) < 1e-8f)
                                {
                                    throw Fail("DCT4 multiplier is too small");
                                }
                            }
                        }

                        DecodeDctParams(br, e.DistanceBands, out e.NumDistanceBands);
                        break;
                    case LjQuantEncoding.ModeAfv:
                        if (requiredSize != 1)
                        {
                            throw Fail("Invalid mode");
                        }

                        for (int c = 0; c < 3; c++)
                        {
                            for (int k = 0; k < 9; k++)
                            {
                                e.AfvWeights[c, k] = LjFields.F16ToFloat((uint)br.ReadBits(16));
                            }

                            for (int k = 0; k < 6; k++)
                            {
                                e.AfvWeights[c, k] *= 64;
                            }
                        }

                        DecodeDctParams(br, e.DistanceBands, out e.NumDistanceBands);
                        DecodeDctParams(br, e.DistanceBandsAfv4x4, out e.NumDistanceBandsAfv4x4);
                        break;
                    case LjQuantEncoding.ModeDct:
                        DecodeDctParams(br, e.DistanceBands, out e.NumDistanceBands);
                        break;
                    case LjQuantEncoding.ModeRaw:
                        e.QTableDen = LjFields.F16ToFloat((uint)br.ReadBits(16));
                        if (e.QTableDen < 1e-8f)
                        {
                            throw Fail("Invalid qtable_den: value too small");
                        }

                        e.QTable = decodeRaw(reqX * 8, reqY * 8, i, br);
                        foreach (int q in e.QTable)
                        {
                            if (q <= 0)
                            {
                                throw Fail("Invalid raw quantization table");
                            }
                        }

                        break;
                    default:
                        throw Fail("Invalid quantization table encoding");
                }

                e.Mode = mode;
            }
        }

        computedMask = 0;
    }

    private static float Mult(float v) => v > 0.0f ? 1.0f + v : 1.0f / (1.0f - v);

    private static float Interpolate(float pos, float max, float[] array, int len)
    {
        float scaledPos = pos * (len - 1) / max;
        int idx = (int)scaledPos;
        if (idx + 1 >= len)
        {
            throw Fail("Interpolate out of range");
        }

        float a = array[idx], b = array[idx + 1];
        return a * LjMath.FastPowf(b / a, scaledPos - idx, true);
    }

    private static void GetQuantWeights(int rows, int cols, float[,] distanceBands, int numBands, float[] output, int outOff)
    {
        const float kSqrt2 = 1.41421356237f;
        for (int c = 0; c < 3; c++)
        {
            var bands = new float[17];
            bands[0] = distanceBands[c, 0];
            if (bands[0] < 1e-8f)
            {
                throw Fail("Invalid distance bands");
            }

            for (int i = 1; i < numBands; i++)
            {
                bands[i] = bands[i - 1] * Mult(distanceBands[c, i]);
                if (bands[i] < 1e-8f)
                {
                    throw Fail("Invalid distance bands");
                }
            }

            float scale = (numBands - 1) / (kSqrt2 + 1e-6f);
            float rcpcol = scale / (cols - 1);
            float rcprow = scale / (rows - 1);
            for (int y = 0; y < rows; y++)
            {
                float dy = y * rcprow;
                float dy2 = dy * dy;
                for (int x = 0; x < cols; x++)
                {
                    float dx = (x + 0f) * rcpcol;
                    float scaledDistance = MathF.Sqrt(MathF.FusedMultiplyAdd(dx, dx, dy2));
                    float weight;
                    if (numBands == 1)
                    {
                        weight = bands[0];
                    }
                    else
                    {
                        // InterpolateVec (AVX2): idx = truncation, frac, gather, a * FastPowf(b / a, frac).
                        int idx = (int)scaledDistance;
                        float frac = scaledDistance - idx;
                        float a = bands[idx], b = bands[idx + 1];
                        weight = a * LjMath.FastPowf(b / a, frac, true);
                    }

                    output[outOff + (c * cols * rows) + (y * cols) + x] = weight;
                }
            }
        }
    }

    private static readonly float[] AfvFreqs =
    [
        0xBAD, 0xBAD, 0.8517778890324296f, 5.37778436506804f, 0xBAD, 0xBAD, 4.734747904497923f, 5.449245381693219f,
        1.6598270267479331f, 4, 7.275749096817861f, 10.423227632456525f, 2.662932286148962f, 7.630657783650829f, 8.962388608184032f, 12.97166202570235f,
    ];

    private void ComputeQuantTable(LjQuantEncoding e, int kind, ref int pos)
    {
        const int n = 8;
        int wrows = 8 * RequiredSizeX[kind];
        int wcols = 8 * RequiredSizeY[kind];
        int num = wrows * wcols;
        var weights = new float[3 * num];
        switch (e.Mode)
        {
            case LjQuantEncoding.ModeId:
                for (int c = 0; c < 3; c++)
                {
                    for (int i = 0; i < 64; i++)
                    {
                        weights[(64 * c) + i] = e.IdWeights[c, 0];
                    }

                    weights[(64 * c) + 1] = e.IdWeights[c, 1];
                    weights[(64 * c) + 8] = e.IdWeights[c, 1];
                    weights[(64 * c) + 9] = e.IdWeights[c, 2];
                }

                break;
            case LjQuantEncoding.ModeDct2:
                for (int c = 0; c < 3; c++)
                {
                    int start = c * 64;
                    weights[start] = 0xBAD;
                    weights[start + 1] = weights[start + 8] = e.Dct2Weights[c, 0];
                    weights[start + 9] = e.Dct2Weights[c, 1];
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            weights[start + (y * 8) + x + 2] = e.Dct2Weights[c, 2];
                            weights[start + ((y + 2) * 8) + x] = e.Dct2Weights[c, 2];
                        }
                    }

                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            weights[start + ((y + 2) * 8) + x + 2] = e.Dct2Weights[c, 3];
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            weights[start + (y * 8) + x + 4] = e.Dct2Weights[c, 4];
                            weights[start + ((y + 4) * 8) + x] = e.Dct2Weights[c, 4];
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            weights[start + ((y + 4) * 8) + x + 4] = e.Dct2Weights[c, 5];
                        }
                    }
                }

                break;
            case LjQuantEncoding.ModeDct4:
            {
                var w44 = new float[3 * 16];
                GetQuantWeights(4, 4, e.DistanceBands, e.NumDistanceBands, w44, 0);
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            weights[(c * num) + (y * 8) + x] = w44[(c * 16) + ((y / 2) * 4) + (x / 2)];
                        }
                    }

                    weights[(c * num) + 1] /= e.Dct4Multipliers[c, 0];
                    weights[(c * num) + n] /= e.Dct4Multipliers[c, 0];
                    weights[(c * num) + n + 1] /= e.Dct4Multipliers[c, 1];
                }

                break;
            }

            case LjQuantEncoding.ModeDct4x8:
            {
                var w48 = new float[3 * 32];
                GetQuantWeights(4, 8, e.DistanceBands, e.NumDistanceBands, w48, 0);
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            weights[(c * num) + (y * 8) + x] = w48[(c * 32) + ((y / 2) * 8) + x];
                        }
                    }

                    weights[(c * num) + n] /= e.Dct4x8Multipliers[c];
                }

                break;
            }

            case LjQuantEncoding.ModeDct:
                GetQuantWeights(wrows, wcols, e.DistanceBands, e.NumDistanceBands, weights, 0);
                break;
            case LjQuantEncoding.ModeRaw:
                if (e.QTable == null || e.QTable.Length != 3 * num)
                {
                    throw Fail("Invalid table encoding");
                }

                for (int i = 0; i < 3 * num; i++)
                {
                    weights[i] = 1.0f / (e.QTableDen * e.QTable[i]);
                }

                break;
            case LjQuantEncoding.ModeAfv:
            {
                var w48 = new float[3 * 32];
                GetQuantWeights(4, 8, e.DistanceBands, e.NumDistanceBands, w48, 0);
                var w44 = new float[3 * 16];
                GetQuantWeights(4, 4, e.DistanceBandsAfv4x4, e.NumDistanceBandsAfv4x4, w44, 0);
                const float lo = 0.8517778890324296f;
                const float hi = 12.97166202570235f - lo + 1e-6f;
                for (int c = 0; c < 3; c++)
                {
                    var bands = new float[4];
                    bands[0] = e.AfvWeights[c, 5];
                    if (bands[0] < 1e-8f)
                    {
                        throw Fail("Invalid AFV bands");
                    }

                    for (int i = 1; i < 4; i++)
                    {
                        bands[i] = bands[i - 1] * Mult(e.AfvWeights[c, i + 5]);
                        if (bands[i] < 1e-8f)
                        {
                            throw Fail("Invalid AFV bands");
                        }
                    }

                    int start = c * 64;
                    void Set(int x, int y, float v) => weights[start + (y * 8) + x] = v;
                    weights[start] = 1;
                    Set(0, 1, e.AfvWeights[c, 0]);
                    Set(1, 0, e.AfvWeights[c, 1]);
                    Set(0, 2, e.AfvWeights[c, 2]);
                    Set(2, 0, e.AfvWeights[c, 3]);
                    Set(2, 2, e.AfvWeights[c, 4]);
                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            if (x < 2 && y < 2)
                            {
                                continue;
                            }

                            Set(2 * x, 2 * y, Interpolate(AfvFreqs[(y * 4) + x] - lo, hi, bands, 4));
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            if (x == 0 && y == 0)
                            {
                                continue;
                            }

                            weights[(c * num) + (((2 * y) + 1) * 8) + x] = w48[(c * 32) + (y * 8) + x];
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            if (x == 0 && y == 0)
                            {
                                continue;
                            }

                            weights[(c * num) + (2 * y * 8) + (2 * x) + 1] = w44[(c * 16) + (y * 4) + x];
                        }
                    }
                }

                break;
            }

            default:
                throw Fail("Library encoding must be resolved by the caller");
        }

        int prevPos = pos;
        for (int i = 0; i < num * 3; i++)
        {
            float inv = weights[i];
            if (inv >= 1.0f / 1e-8f || inv < 1e-8f)
            {
                throw Fail("Invalid quantization table");
            }

            table[pos + i] = 1.0f / inv;
            invTable[pos + i] = inv;
        }

        pos += 3 * num;
        int xs = RequiredSizeX[kind], ys = RequiredSizeY[kind];
        if (ys > xs)
        {
            (xs, ys) = (ys, xs);
        }

        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < ys; y++)
            {
                for (int x = 0; x < xs; x++)
                {
                    invTable[prevPos + (c * ys * xs * 64) + (y * 8 * xs) + x] = 0;
                }
            }
        }
    }

    public void EnsureComputed(uint acsMask)
    {
        var offsets = new int[(NumQuantTables * 3) + 1];
        int pos = 0;
        for (int i = 0; i < NumQuantTables; i++)
        {
            int num = RequiredSizeX[i] * RequiredSizeY[i] * 64;
            for (int c = 0; c < 3; c++)
            {
                offsets[(3 * i) + c] = pos + (c * num);
            }

            pos += 3 * num;
        }

        offsets[NumQuantTables * 3] = pos;
        uint kindMask = 0;
        for (int i = 0; i < 27; i++)
        {
            if ((acsMask & (1u << i)) != 0)
            {
                kindMask |= 1u << AcStrategyToQuantTable[i];
            }
        }

        uint computedKindMask = 0;
        for (int i = 0; i < 27; i++)
        {
            if ((computedMask & (1u << i)) != 0)
            {
                computedKindMask |= 1u << AcStrategyToQuantTable[i];
            }
        }

        for (int t = 0; t < NumQuantTables; t++)
        {
            if (((1u << t) & computedKindMask) != 0 || ((1u << t) & ~kindMask) != 0)
            {
                continue;
            }

            int offset = offsets[t * 3];
            LjQuantEncoding e = Encodings[t].Mode == LjQuantEncoding.ModeLibrary ? LjQuantEncoding.Library[t] : Encodings[t];
            ComputeQuantTable(e, t, ref offset);
        }

        computedMask |= acsMask;
    }
}
