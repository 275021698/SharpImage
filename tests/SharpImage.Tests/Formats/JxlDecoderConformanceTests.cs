using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using SharpImage.Formats;
using SharpImage.Formats.Jxl.Lj;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The libjxl-port decoder against libjxl itself: files from the libjxl conformance suite (BSD licence, see
/// TestAssets/jxl/conformance/LICENSE) must decode to exactly the float samples djxl 0.12 writes (all displayed
/// frames, colour and extra channels, compared through a SHA-256 of djxl's .npy payload), and lossless JPEG
/// recompressions made by cjxl must rebuild the original JPEG byte for byte.
/// </summary>
public class JxlDecoderConformanceTests
{
    private static readonly string ConfDir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "conformance");
    private static readonly string RecompressDir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "jpegrecompress");

    public static IEnumerable<string> ConformanceCases() =>
        File.ReadAllLines(Path.Combine(ConfDir, "expected.txt")).Where(l => l.Length > 0).Select(l => l.Split(' ')[0]);

    [Test]
    [MethodDataSource(nameof(ConformanceCases))]
    public async Task Decodes_BitIdenticalTo_Djxl(string name)
    {
        string[] f = File.ReadAllLines(Path.Combine(ConfDir, "expected.txt")).First(l => l.StartsWith(name + " ")).Split(' ');
        int frames = int.Parse(f[1]), h = int.Parse(f[2]), w = int.Parse(f[3]), nc = int.Parse(f[4]);
        byte[] cs = JxlCoder.FindCodestream(File.ReadAllBytes(Path.Combine(ConfDir, name + ".jxl")));
        LjDecodedImage img = LjDecoder.Decode(cs);
        int colors = img.IsGray ? 1 : 3;
        await Assert.That(img.Frames.Count).IsEqualTo(frames);
        await Assert.That(colors + img.State.Meta.ExtraChannelInfo.Count).IsEqualTo(nc);
        var bytes = new byte[(long)frames * h * w * nc * 4];
        int pos = 0;
        foreach (LjDecodedFrame fr in img.Frames)
        {
            await Assert.That(fr.Width).IsEqualTo(w);
            await Assert.That(fr.Height).IsEqualTo(h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < nc; c++)
                    {
                        LjImageF p = fr.Planes[c < colors ? c : 3 + (c - colors)];
                        BitConverter.TryWriteBytes(bytes.AsSpan(pos), p.Data[p.Row(y) + x]);
                        pos += 4;
                    }
                }
            }
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash == f[5])
        {
            return;
        }

        // libjxl itself is not bit-reproducible across machines: VarDCT dequantization uses the CPU's approximate
        // reciprocal (rcpps, vendor-specific) and custom XYB parameters go through the C library's cbrtf. The
        // expected hashes come from djxl on Windows on an AMD Zen 5. Where both agree with that machine any
        // difference is a regression; elsewhere compare per-channel sums closely.
        await Assert.That(OnReferencePlatform()).IsFalse();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        for (int c = 0; c < nc; c++)
        {
            double sum = 0;
            for (int i = c; i < floats.Length; i += nc)
            {
                sum += floats[i];
            }

            double expected = double.Parse(f[6 + c], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(Math.Abs(sum - expected)).IsLessThanOrEqualTo(1e-5 * Math.Max(1.0, Math.Abs(expected)));
        }
    }

    private static bool OnReferencePlatform()
    {
        // The MSVC runtime's cbrtf (which djxl links) is not correctly rounded; .NET's MathF.Cbrt reaches the host's.
        if (BitConverter.SingleToUInt32Bits(MathF.Cbrt(-2.5f)) != 0xBFADB904u || BitConverter.SingleToUInt32Bits(MathF.Cbrt(7.75f)) != 0x3FFD4E18u)
        {
            return false;
        }

        if (!System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            return false;
        }

        float[] probe = [3f, 7f, -5f, 123.456f, 1000.5f, 0.3f];
        uint[] reference = [0x3EAAA800, 0x3E124000, 0xBE4CC000, 0x3C04B000, 0x3A830000, 0x40555000];
        for (int i = 0; i < probe.Length; i++)
        {
            float r = System.Runtime.Intrinsics.X86.Sse.ReciprocalScalar(System.Runtime.Intrinsics.Vector128.CreateScalarUnsafe(probe[i])).ToScalar();
            if (BitConverter.SingleToUInt32Bits(r) != reference[i])
            {
                return false;
            }
        }

        return true;
    }

    public static IEnumerable<string> RecompressCases() =>
        Directory.GetFiles(RecompressDir, "*.cjxl.jxl").Select(p => Path.GetFileName(p)[..^".cjxl.jxl".Length]);

    [Test]
    [MethodDataSource(nameof(RecompressCases))]
    public async Task ReconstructsOriginalJpeg_FromCjxlFile(string name)
    {
        byte[] jxl = File.ReadAllBytes(Path.Combine(RecompressDir, name + ".cjxl.jxl"));
        byte[] jpeg = File.ReadAllBytes(Path.Combine(RecompressDir, name + ".jpg"));
        byte[]? rebuilt = JxlCoder.ReconstructJpeg(jxl);
        await Assert.That(rebuilt).IsNotNull();
        await Assert.That(rebuilt!.AsSpan().SequenceEqual(jpeg)).IsTrue();
    }

    [Test]
    public async Task OpsinBiasCbrt_MatchesLibjxl()
    {
        // libjxl's cbrtf of the default opsin bias (its MSVC runtime is one ulp off the correctly rounded value).
        float c = LjDec.CbrtF(-0.0037930732552754493f);
        await Assert.That(BitConverter.SingleToUInt32Bits(c)).IsEqualTo(0xBE1FB276u);
    }

    [Test]
    public async Task ReconstructJpeg_ReturnsNull_WithoutJbrd()
    {
        byte[] jxl = File.ReadAllBytes(Path.Combine(ConfDir, "grayscale.jxl"));
        await Assert.That(JxlCoder.ReconstructJpeg(jxl)).IsNull();
    }

    [Test]
    public async Task DecodedRecompressedJpeg_MatchesItsPixels()
    {
        // A recompressed JPEG decodes (as JXL) close to the JPEG's own decode: same DCT coefficients, libjxl's IDCT.
        byte[] jxl = File.ReadAllBytes(Path.Combine(RecompressDir, "p420.cjxl.jxl"));
        using var fromJxl = JxlCoder.Decode(jxl);
        using var fromJpeg = FormatRegistry.Read(Path.Combine(RecompressDir, "p420.jpg"));
        await Assert.That(fromJxl.Columns).IsEqualTo(fromJpeg.Columns);
        await Assert.That(fromJxl.Rows).IsEqualTo(fromJpeg.Rows);
        double sum = 0;
        long n = 0;
        for (int y = 0; y < fromJxl.Rows; y++)
        {
            var a = fromJxl.GetPixelRow(y);
            var b = fromJpeg.GetPixelRow(y);
            for (int i = 0; i < (int)fromJxl.Columns * 3; i++)
            {
                double d = (a[i] - b[i]) / 257.0;
                sum += d * d;
                n++;
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-9, sum / n));
        await Assert.That(psnr).IsGreaterThan(40);
    }
}
