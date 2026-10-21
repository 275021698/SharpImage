using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// HEIC output that libheif rejected or got wrong: odd widths / heights (the 4:2:0 conformance window can only crop in
/// 2-pixel steps, so the coded size is even and a 'clap' box crops it, as libheif writes it), transparency (an alpha
/// auxiliary item) and gray + alpha sources (alpha was read as green).
/// </summary>
public sealed class HeicEncoderTests
{
    private static ImageFrame Make(int w, int h, bool gray, bool alpha)
    {
        var f = new ImageFrame();
        f.Initialize(w, h, gray ? ColorspaceType.Gray : ColorspaceType.SRGB, alpha);
        int ch = f.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = f.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                    row[x * ch + c] = alpha && c == ch - 1
                        ? (ushort)(x < w / 2 ? 0 : 65535)
                        : (ushort)(((x * 255 / Math.Max(1, w - 1)) ^ (c * 60)) * 257);
        }
        return f;
    }

    private static int FindBox(byte[] d, string type)
    {
        for (int i = 4; i + 4 <= d.Length; i++)
            if (d[i] == type[0] && d[i + 1] == type[1] && d[i + 2] == type[2] && d[i + 3] == type[3]) return i;
        return -1;
    }

    [Test]
    [Arguments(899, 696, false, false)]
    [Arguments(131, 97, true, false)]
    [Arguments(3, 2, false, true)]
    [Arguments(100, 75, true, true)]
    [Arguments(64, 48, false, true)]
    public async Task OddSizesAndAlpha_DecodeAtTheSourceSize(int w, int h, bool gray, bool alpha)
    {
        using var src = Make(w, h, gray, alpha);
        byte[] heic = FormatRegistry.Encode(src, ImageFileFormat.Heic);

        // the coded size (ispe) is even and at least 64 (as libheif's x265 plugin pads); a source of another size is
        // cropped by clap
        int ispe = FindBox(heic, "ispe");
        int iw = (heic[ispe + 8] << 24) | (heic[ispe + 9] << 16) | (heic[ispe + 10] << 8) | heic[ispe + 11];
        int ih = (heic[ispe + 12] << 24) | (heic[ispe + 13] << 16) | (heic[ispe + 14] << 8) | heic[ispe + 15];
        await Assert.That(iw % 2 == 0 && ih % 2 == 0 && iw >= 64 && ih >= 64).IsTrue();
        await Assert.That(FindBox(heic, "clap") >= 0).IsEqualTo(iw != w || ih != h);
        await Assert.That(FindBox(heic, "auxC") >= 0).IsEqualTo(alpha);

        using var back = FormatRegistry.Read(heic);
        await Assert.That((int)back.Columns).IsEqualTo(w);
        await Assert.That((int)back.Rows).IsEqualTo(h);
        await Assert.That(back.HasAlpha).IsEqualTo(alpha);
        if (alpha)
        {
            int bc = back.NumberOfChannels, wrong = 0;
            for (int y = 0; y < h; y++)
            {
                var row = back.GetPixelRow(y);
                for (int x = 0; x < w; x++)
                {
                    int a = row[x * bc + bc - 1] >> 8;
                    if (x < w / 2 - 1 && a > 8) wrong++;
                    if (x > w / 2 && a < 247) wrong++;
                }
            }
            await Assert.That(wrong).IsEqualTo(0);
        }
    }
}
