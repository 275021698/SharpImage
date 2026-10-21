using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Lossless JPEG -> JPEG XL recompression must produce exactly what libjxl's cjxl 0.12 writes for the same JPEG
/// (its default effort 7, brotli effort 9). The reference files were made with <c>cjxl -q 100 x.jpg x.jxl</c>, and
/// libjxl's djxl rebuilds each source JPEG byte for byte from them. The cases cover 4:2:0, 4:2:2 and 4:4:4
/// (chroma-from-luma), progressive, grayscale, RGB JPEGs, restart markers, ICC / Exif / XMP, a 1x1 image and frames
/// with several DC groups.
/// </summary>
public sealed class JxlJpegRecompressTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestAssets", "jxl", "jpegrecompress");

    [Test]
    [Arguments("p420")]
    [Arguments("p444odd")]
    [Arguments("prog")]
    [Arguments("gray")]
    [Arguments("rgb")]
    [Arguments("restart")]
    [Arguments("meta")]
    [Arguments("bigicc")]
    [Arguments("multigroup422")]
    [Arguments("multigroup444")]
    [Arguments("tiny")]
    [Arguments("noiseprog")]
    public async Task EncodeFromJpeg_MatchesCjxl(string name)
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Dir, name + ".jpg"));
        byte[] expected = File.ReadAllBytes(Path.Combine(Dir, name + ".cjxl.jxl"));
        byte[] actual = JxlCoder.EncodeFromJpeg(jpeg);
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    public static IEnumerable<(string, int)> EffortCases()
    {
        foreach (string name in new[] { "p444odd", "p420", "gray", "prog" })
        {
            foreach (int effort in new[] { 1, 3, 4, 5, 6, 8, 9, 10 })
            {
                yield return (name, effort);
            }
        }
    }

    // cjxl -e N --allow_expert_options x.jpg: every effort level is matched byte for byte (4:4:4 exercises the learned
    // chroma-from-luma and DC trees of efforts 8-10, gray and progressive the rest).
    [Test]
    [MethodDataSource(nameof(EffortCases))]
    public async Task EncodeFromJpeg_MatchesCjxlAtEffort(string name, int effort)
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(Dir, name + ".jpg"));
        byte[] expected = File.ReadAllBytes(Path.Combine(Dir, $"{name}.e{effort}.cjxl.jxl"));
        byte[] actual = JxlCoder.EncodeFromJpeg(jpeg, effort);
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task EncodeFromJpeg_RefusesCmyk()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sanity", "cmyk_adobe.jpg"));
        await Assert.That(() => JxlCoder.EncodeFromJpeg(jpeg)).Throws<NotSupportedException>();
    }
}
