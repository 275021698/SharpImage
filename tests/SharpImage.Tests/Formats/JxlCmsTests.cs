using System.Security.Cryptography;
using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Formats;
using SharpImage.Formats.Jxl.Lj;

namespace SharpImage.Tests.Formats;

/// <summary>
/// The libjxl-port colour management (skcms + libjxl's CMS) against djxl 0.12: CMYK images rendered to sRGB the way djxl
/// does by default, and images with ICC profiles (TRC curves, LUT tables, Lab PCS, CICP, XYB) or enumerated encodings
/// converted to other colour spaces (djxl --color_space) must give exactly djxl's float samples. ICC profiles created for
/// enumerated encodings must be byte-identical to libjxl's. The test ICC profiles are libjxl-generated (CC0), Google's
/// sRGB_Calibrated_Heterogeneous and CC0 Rec2020_HLG_cicp from the skcms corpus, and Adobe RGB / Adobe gray / SWOP.
/// </summary>
public class JxlCmsTests
{
    private static readonly string CmsDir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "cms");

    public static IEnumerable<string> Cases() =>
        File.ReadAllLines(Path.Combine(CmsDir, "expected.txt")).Where(l => l.Length > 0).Select(l => l.Split(' ')[0] + " " + l.Split(' ')[1]);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Converts_BitIdenticalTo_Djxl(string key)
    {
        string[] f = File.ReadAllLines(Path.Combine(CmsDir, "expected.txt")).First(l => l.StartsWith(key + " ")).Split(' ');
        int w = int.Parse(f[2]), h = int.Parse(f[3]), nc = int.Parse(f[4]);
        byte[] cs = JxlCoder.FindCodestream(File.ReadAllBytes(Path.Combine(CmsDir, f[0])));
        LjDecodedImage img = f[1] == "-"
            ? LjDecoder.Decode(cs, 1, cmykToSrgb: true)
            : LjDecoder.Decode(cs, 1, cmsOutput: LjCms.ParseDescription(f[1]) ?? throw new InvalidDataException(f[1]));
        LjDecodedFrame fr = img.Frames[0];
        await Assert.That(fr.Width).IsEqualTo(w);
        await Assert.That(fr.Height).IsEqualTo(h);
        var bytes = new byte[(long)w * h * nc * 4];
        int pos = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < nc; c++)
                {
                    LjImageF p = fr.Planes[c];
                    BitConverter.TryWriteBytes(bytes.AsSpan(pos), p.Data[p.Row(y) + x]);
                    pos += 4;
                }
            }
        }

        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == f[5])
        {
            return;
        }

        // djxl's libm (powf etc.) is the Windows C runtime; elsewhere compare per-channel sums closely.
        await Assert.That(JxlDecoderConformanceTests.OnReferencePlatform() && OperatingSystem.IsWindows()).IsFalse();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        for (int c = 0; c < nc; c++)
        {
            double sum = 0;
            for (int i = c; i < floats.Length; i += nc)
            {
                if (!float.IsNaN(floats[i]))
                {
                    sum += floats[i];
                }
            }

            double expected = double.Parse(f[6 + c], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(Math.Abs(sum - expected)).IsLessThanOrEqualTo(1e-5 * Math.Max(1.0, Math.Abs(expected)));
        }
    }

    public static IEnumerable<string> IccCases() =>
        Directory.GetFiles(Path.Combine(CmsDir, "icc"), "*.icc").Select(p => Path.GetFileNameWithoutExtension(p).Replace('~', ';'));

    [Test]
    [MethodDataSource(nameof(IccCases))]
    public async Task CreatesIccProfile_ByteIdenticalTo_Libjxl(string description)
    {
        byte[] expected = File.ReadAllBytes(Path.Combine(CmsDir, "icc", description.Replace(';', '~') + ".icc"));
        LjColorEncoding c = LjCms.ParseDescription(description) ?? throw new InvalidDataException(description);
        byte[]? icc = LjCms.MaybeCreateProfile(LjCms.ToExternal(c));
        await Assert.That(icc).IsNotNull();
        await Assert.That(icc!.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task CmykJxl_DecodesToSrgb()
    {
        // JxlCoder renders CMYK through the profile (as djxl does for formats without CMYK) instead of showing raw CMY.
        using ImageFrame frame = JxlCoder.Decode(File.ReadAllBytes(Path.Combine(CmsDir, "cmyk_layers.jxl")));
        await Assert.That(frame.Colorspace).IsEqualTo(ColorspaceType.SRGB);
        await Assert.That(frame.IccProfile).IsNull();
        await Assert.That(frame.Columns).IsEqualTo(512L);

        LjDecodedImage img = LjDecoder.Decode(JxlCoder.FindCodestream(File.ReadAllBytes(Path.Combine(CmsDir, "cmyk_layers.jxl"))), 1, cmykToSrgb: true);
        LjImageF r = img.Frames[0].Planes[0];
        var row = frame.GetPixelRow(100);
        float v = Math.Clamp(r.Data[r.Row(100) + 200], 0f, 1f);
        await Assert.That((double)Math.Abs(row[200 * frame.NumberOfChannels] - (v * 65535))).IsLessThanOrEqualTo(1.0);
    }
}
