using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

/// <summary>
/// HEIC output byte-identical to libheif 1.23.6's heif-enc with x265 (TestAssets/heic_heifenc: <c>heif-enc in -q Q -o
/// out.heic</c>, x265 4.2+59 C build, identical to the x265 in pillow_heif 1.8.1): PNG input through the RGB -> YCbCr
/// pixel path (gray, alpha, ICC, odd and small sizes with clap), JPEG input through the raw YCbCr planes (Exif
/// orientation as irot/imir, ICC, Exif + XMP items).
/// </summary>
public sealed class HeicHeifEncParityTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", "heic_heifenc", name);

    [Test]
    [Arguments("rgb_odd.png", 50)]
    [Arguments("rgb_tiny.png", 50)]
    [Arguments("rgba.png", 50)]
    [Arguments("rgba.png", 90)]
    [Arguments("gray.png", 50)]
    [Arguments("graya.png", 50)]
    [Arguments("rgb_icc.png", 50)]
    [Arguments("photo640.png", 75)]
    public async Task Png_MatchesHeifEnc(string input, int quality)
    {
        using var image = FormatRegistry.Read(Asset(input));
        byte[] heic = HeifCoder.EncodeHeic(image, new HeicEncodeOptions { Quality = quality });
        byte[] reference = File.ReadAllBytes(Asset($"{Path.GetFileNameWithoutExtension(input)}_q{quality}.heic"));
        await Assert.That(heic.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    [Arguments("o5.jpg", 50)]
    [Arguments("o8.jpg", 50)]
    [Arguments("xmp.jpg", 50)]
    [Arguments("xmp.jpg", 90)]
    [Arguments("gray_jpeg.jpg", 50)]
    [Arguments("icc.jpg", 50)]
    public async Task Jpeg_MatchesHeifEnc(string input, int quality)
    {
        byte[] heic = HeifCoder.EncodeHeicFromJpeg(File.ReadAllBytes(Asset(input)), new HeicEncodeOptions { Quality = quality });
        byte[] reference = File.ReadAllBytes(Asset($"{Path.GetFileNameWithoutExtension(input)}_q{quality}.heic"));
        await Assert.That(heic.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task DefaultQualityIsHeifEncDefault()
    {
        using var image = FormatRegistry.Read(Asset("rgba.png"));
        byte[] heic = FormatRegistry.Encode(image, ImageFileFormat.Heic);
        await Assert.That(heic.AsSpan().SequenceEqual(File.ReadAllBytes(Asset("rgba_q50.heic")))).IsTrue();
    }
}
