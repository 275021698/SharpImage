// Unified format detection and I/O registry.
// Provides automatic format detection from file headers and extensions,
// and a single API to read/write any supported format.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>
/// Identifies an image file format.
/// </summary>
public enum ImageFileFormat
{
    Unknown,
    Bmp,
    Tga,
    Pnm,
    Gif,
    Jpeg,
    Png,
    Tiff,
    WebP,
    Qoi,
    Ico,
    Hdr,
    Psd,
    Dds,
    Svg,
    Farbfeld,
    Wbmp,
    Pcx,
    Xbm,
    Xpm,
    Dpx,
    Fits,
    Cin,
    Raw,
    Dicom,
    Jpeg2000,
    JpegXl,

    /// <summary>SharpImage's earlier private lossless JPEG recompression container (magic "SJXL"), which
    /// QuickFixMyPics 1.0.10-1.0.11 wrote with a .jxl extension. Read-only: it decodes to the exact source JPEG's
    /// pixels. <see cref="JpegXlLossless.Encode(byte[])"/> now writes standard JPEG XL instead.</summary>
    JpegXlLossless,
    Avif,
    Heic,
    Exr,
    Sgi,
    Pix,
    Sun,
    Pdf,
    CameraRaw,

    /// <summary>YUV4MPEG2 raw planar video / stills (<see cref="Y4mCoder"/>).</summary>
    Y4m
}

/// <summary>
/// Central registry for image format detection and I/O operations. Detects format from file header bytes or file
/// extension.
/// </summary>
public static class FormatRegistry
{
    /// <summary>
    /// Detect image format from the first bytes of file data.
    /// </summary>
    public static ImageFileFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return ImageFileFormat.Unknown;
        }

        // Check signatures in order of specificity
        if (PngCoder.CanDecode(data))
        {
            return ImageFileFormat.Png;
        }

        if (JpegCoder.CanDecode(data))
        {
            return ImageFileFormat.Jpeg;
        }

        if (GifCoder.CanDecode(data))
        {
            return ImageFileFormat.Gif;
        }

        if (WebpCoder.CanDecode(data))
        {
            return ImageFileFormat.WebP;
        }

        // Camera raw formats MUST be checked before TIFF — many raw formats are TIFF-based
        if (CameraRawCoder.CanDecode(data))
        {
            return ImageFileFormat.CameraRaw;
        }

        if (TiffCoder.CanDecode(data))
        {
            return ImageFileFormat.Tiff;
        }

        if (BmpCoder.CanDecode(data))
        {
            return ImageFileFormat.Bmp;
        }

        if (PsdCoder.CanDecode(data))
        {
            return ImageFileFormat.Psd;
        }

        if (DdsCoder.CanDecode(data))
        {
            return ImageFileFormat.Dds;
        }

        if (QoiCoder.CanDecode(data))
        {
            return ImageFileFormat.Qoi;
        }

        if (HdrCoder.CanDecode(data))
        {
            return ImageFileFormat.Hdr;
        }

        if (IcoCoder.CanDecode(data))
        {
            return ImageFileFormat.Ico;
        }

        if (SvgCoder.CanDecode(data))
        {
            return ImageFileFormat.Svg;
        }

        if (FarbfeldCoder.CanDecode(data))
        {
            return ImageFileFormat.Farbfeld;
        }

        if (DpxCoder.CanDecode(data))
        {
            return ImageFileFormat.Dpx;
        }

        if (FitsCoder.CanDecode(data))
        {
            return ImageFileFormat.Fits;
        }

        if (CinCoder.CanDecode(data))
        {
            return ImageFileFormat.Cin;
        }

        if (DicomCoder.CanDecode(data))
        {
            return ImageFileFormat.Dicom;
        }

        if (Jpeg2000Coder.CanDecode(data))
        {
            return ImageFileFormat.Jpeg2000;
        }

        if (JpegXlLossless.CanDecode(data))
        {
            return ImageFileFormat.JpegXlLossless; // SharpImage's byte-exact JPEG recompression container
        }

        if (JxlCoder.CanDecode(data))
        {
            return ImageFileFormat.JpegXl;
        }

        if (HeifCoder.CanDecode(data))
        {
            return ImageFileFormat.Avif; // AVIF/HEIC both detected here
        }

        if (ExrCoder.CanDecode(data))
        {
            return ImageFileFormat.Exr;
        }

        if (SgiCoder.CanDecode(data))
        {
            return ImageFileFormat.Sgi;
        }

        if (SunCoder.CanDecode(data))
        {
            return ImageFileFormat.Sun;
        }

        if (PcxCoder.CanDecode(data))
        {
            return ImageFileFormat.Pcx;
        }

        if (XbmCoder.CanDecode(data))
        {
            return ImageFileFormat.Xbm;
        }

        if (XpmCoder.CanDecode(data))
        {
            return ImageFileFormat.Xpm;
        }

        // PNM and Y4M have real signatures; TGA has none (a header-shape guess), so it must not shadow them.
        if (PnmCoder.CanDecode(data))
        {
            return ImageFileFormat.Pnm;
        }

        if (Y4mCoder.CanDecode(data))
        {
            return ImageFileFormat.Y4m;
        }

        if (TgaCoder.CanDecode(data))
        {
            return ImageFileFormat.Tga;
        }
        // WBMP has weak signature (0x00 0x00), check last
        if (WbmpCoder.CanDecode(data))
        {
            return ImageFileFormat.Wbmp;
        }

        return ImageFileFormat.Unknown;
    }

    /// <summary>
    /// Detect image format from file extension.
    /// </summary>
    public static ImageFileFormat DetectFromExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bmp" => ImageFileFormat.Bmp,
            ".tga" => ImageFileFormat.Tga,
            ".pnm" or ".ppm" or ".pgm" or ".pbm" => ImageFileFormat.Pnm,
            ".y4m" => ImageFileFormat.Y4m,
            ".gif" => ImageFileFormat.Gif,
            ".jpg" or ".jpeg" => ImageFileFormat.Jpeg,
            ".png" => ImageFileFormat.Png,
            ".tif" or ".tiff" => ImageFileFormat.Tiff,
            ".webp" => ImageFileFormat.WebP,
            ".qoi" => ImageFileFormat.Qoi,
            ".ico" => ImageFileFormat.Ico,
            ".hdr" or ".pic" => ImageFileFormat.Hdr,
            ".psd" => ImageFileFormat.Psd,
            ".dds" => ImageFileFormat.Dds,
            ".svg" => ImageFileFormat.Svg,
            ".ff" or ".farbfeld" => ImageFileFormat.Farbfeld,
            ".wbmp" => ImageFileFormat.Wbmp,
            ".pcx" => ImageFileFormat.Pcx,
            ".xbm" => ImageFileFormat.Xbm,
            ".xpm" => ImageFileFormat.Xpm,
            ".dpx" => ImageFileFormat.Dpx,
            ".fits" or ".fit" or ".fts" => ImageFileFormat.Fits,
            ".cin" or ".cineon" => ImageFileFormat.Cin,
            ".raw" => ImageFileFormat.Raw,
            ".dcm" or ".dicom" => ImageFileFormat.Dicom,
            ".jp2" or ".j2k" or ".j2c" or ".jpc" => ImageFileFormat.Jpeg2000,
            ".jxl" => ImageFileFormat.JpegXl,
            ".avif" => ImageFileFormat.Avif,
            ".heic" or ".heif" => ImageFileFormat.Heic,
            ".exr" => ImageFileFormat.Exr,
            ".sgi" or ".rgb" or ".rgba" or ".bw" => ImageFileFormat.Sgi,
            ".pix" or ".alias" => ImageFileFormat.Pix,
            ".sun" or ".ras" => ImageFileFormat.Sun,
            ".pdf" => ImageFileFormat.Pdf,
            // Camera raw extensions
            ".cr2" or ".cr3" or ".crw" => ImageFileFormat.CameraRaw,
            ".nef" or ".nrw" => ImageFileFormat.CameraRaw,
            ".arw" or ".sr2" or ".srf" => ImageFileFormat.CameraRaw,
            ".dng" => ImageFileFormat.CameraRaw,
            ".orf" => ImageFileFormat.CameraRaw,
            ".rw2" => ImageFileFormat.CameraRaw,
            ".raf" => ImageFileFormat.CameraRaw,
            ".pef" => ImageFileFormat.CameraRaw,
            ".srw" => ImageFileFormat.CameraRaw,
            ".3fr" => ImageFileFormat.CameraRaw,
            ".iiq" => ImageFileFormat.CameraRaw,
            ".x3f" => ImageFileFormat.CameraRaw,
            ".dcr" or ".k25" or ".kdc" => ImageFileFormat.CameraRaw,
            ".mdc" => ImageFileFormat.CameraRaw,
            ".mef" => ImageFileFormat.CameraRaw,
            ".mos" => ImageFileFormat.CameraRaw,
            ".mrw" => ImageFileFormat.CameraRaw,
            ".rmf" => ImageFileFormat.CameraRaw,
            ".rwl" => ImageFileFormat.CameraRaw,
            ".erf" => ImageFileFormat.CameraRaw,
            ".fff" => ImageFileFormat.CameraRaw,
            ".sti" => ImageFileFormat.CameraRaw,
            _ => ImageFileFormat.Unknown
        };
    }

    /// <summary>
    /// Read an image from file, auto-detecting format from header.
    /// </summary>
    public static ImageFrame Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var format = DetectFormat(data);

        if (format == ImageFileFormat.Unknown)
        {
            format = DetectFromExtension(path);
        }

        return Decode(data, format);
    }

    /// <summary>
    /// Read an image from byte data with auto-detected format.
    /// </summary>
    public static ImageFrame Read(byte[] data)
    {
        var format = DetectFormat(data);
        return Decode(data, format);
    }

    /// <summary>
    /// Write an image to file, detecting format from extension.
    /// </summary>
    public static void Write(ImageFrame image, string path, int quality = 100)
    {
        var format = DetectFromExtension(path);
        byte[] data = quality < 100 && format is ImageFileFormat.JpegXl
            ? JxlCoder.EncodeLossy(image, quality)
            : Encode(image, format);
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Decode image data in a specific format.
    /// </summary>
    public static ImageFrame Decode(byte[] data, ImageFileFormat format)
    {
        return format switch
        {
            ImageFileFormat.Png => PngCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Jpeg => JpegCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Gif => GifCoder.Read(new MemoryStream(data)),
            ImageFileFormat.WebP => WebpRead(data),
            ImageFileFormat.Tiff => TiffCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Bmp => BmpCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Tga => TgaCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Pnm => PnmCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Psd => PsdCoder.Decode(data),
            ImageFileFormat.Dds => DdsCoder.Decode(data),
            ImageFileFormat.Qoi => QoiCoder.Decode(data),
            ImageFileFormat.Hdr => HdrCoder.Decode(data),
            ImageFileFormat.Ico => IcoCoder.Decode(data),
            ImageFileFormat.Svg => SvgCoder.Decode(data),
            ImageFileFormat.Farbfeld => FarbfeldCoder.Decode(data),
            ImageFileFormat.Wbmp => WbmpCoder.Decode(data),
            ImageFileFormat.Pcx => PcxCoder.Decode(data),
            ImageFileFormat.Xbm => XbmCoder.Decode(data),
            ImageFileFormat.Xpm => XpmCoder.Decode(data),
            ImageFileFormat.Dpx => DpxCoder.Decode(data),
            ImageFileFormat.Fits => FitsCoder.Decode(data),
            ImageFileFormat.Cin => CinCoder.Decode(data),
            ImageFileFormat.Dicom => DicomCoder.Decode(data),
            ImageFileFormat.Jpeg2000 => Jpeg2000Coder.Decode(data),
            ImageFileFormat.JpegXl => JxlCoder.Decode(data),
            ImageFileFormat.JpegXlLossless => JpegCoder.Read(new MemoryStream(JpegXlLossless.Decode(data))),
            ImageFileFormat.Avif or ImageFileFormat.Heic => HeifCoder.Decode(data),
            ImageFileFormat.Exr => ExrCoder.Decode(data),
            ImageFileFormat.Sgi => SgiCoder.Decode(data),
            ImageFileFormat.Pix => PixCoder.Decode(data),
            ImageFileFormat.Sun => SunCoder.Decode(data),
            ImageFileFormat.CameraRaw => CameraRawCoder.Decode(data),
            ImageFileFormat.Y4m => Y4mCoder.Read(data),
            _ => throw new NotSupportedException($"Unsupported image format: {format}")
        };
    }

    /// <summary>
    /// Encode an image into a specific format.
    /// </summary>
    public static byte[] Encode(ImageFrame image, ImageFileFormat format)
    {
        // CMYK frames (CMYK / YCCK JPEGs hold ink amounts) go to every writer but JPEG's as sRGB: the writers read
        // the first three channels as R, G, B, which turned CMYK into a colour negative.
        if (image.Colorspace == ColorspaceType.CMYK && format != ImageFileFormat.Jpeg)
        {
            using var rgb = CmykToSrgb(image);
            return Encode(rgb, format);
        }

        // A format that cannot describe the samples' colour space (no ICC profile / CICP) gets them in sRGB, converted
        // through the colour management system, so wide-gamut, HDR or profiled images keep their colours; so does any
        // format for a profile that cannot be attached to the samples (a gray LUT profile on gray expanded to RGB).
        // JPEG XL keeps any profile of gray content as is.
        if ((!CarriesColorDescription(format) || (format != ImageFileFormat.JpegXl && ColorDescription.NeedsConversion(image, rgbOutput: format is ImageFileFormat.WebP or ImageFileFormat.Heic))) &&
            ColorDescription.ToSrgb(image) is { } srgb)
        {
            using (srgb)
            {
                return Encode(srgb, format);
            }
        }

        return format switch
        {
            ImageFileFormat.Png => EncodeToStream(image, (img, s) => PngCoder.Write(img, s)),
            ImageFileFormat.Jpeg => EncodeToStream(image, (img, s) => JpegCoder.Write(img, s)),
            ImageFileFormat.Gif => EncodeToStream(image, (img, s) => GifCoder.Write(img, s)),
            ImageFileFormat.WebP => EncodeToStream(image, (img, s) => WebpCoder.Write(img, s)),
            ImageFileFormat.Tiff => EncodeToStream(image, (img, s) => TiffCoder.Write(img, s)),
            ImageFileFormat.Bmp => EncodeToStream(image, (img, s) => BmpCoder.Write(img, s)),
            ImageFileFormat.Tga => EncodeToStream(image, (img, s) => TgaCoder.Write(img, s)),
            ImageFileFormat.Pnm => EncodeToStream(image, (img, s) => PnmCoder.Write(img, s)),
            ImageFileFormat.Psd => PsdCoder.Encode(image),
            ImageFileFormat.Dds => DdsCoder.Encode(image),
            ImageFileFormat.Qoi => QoiCoder.Encode(image),
            ImageFileFormat.Hdr => HdrCoder.Encode(image),
            ImageFileFormat.Ico => IcoCoder.Encode(image),
            ImageFileFormat.Svg => SvgCoder.Encode(image),
            ImageFileFormat.Farbfeld => FarbfeldCoder.Encode(image),
            ImageFileFormat.Wbmp => WbmpCoder.Encode(image),
            ImageFileFormat.Pcx => PcxCoder.Encode(image),
            ImageFileFormat.Xbm => XbmCoder.Encode(image),
            ImageFileFormat.Xpm => XpmCoder.Encode(image),
            ImageFileFormat.Dpx => DpxCoder.Encode(image),
            ImageFileFormat.Fits => FitsCoder.Encode(image),
            ImageFileFormat.Cin => CinCoder.Encode(image),
            ImageFileFormat.Dicom => DicomCoder.Encode(image),
            ImageFileFormat.Jpeg2000 => Jpeg2000Coder.Encode(image),
            ImageFileFormat.JpegXl => JxlCoder.Encode(image),
            ImageFileFormat.Avif => HeifCoder.EncodeAvif(image), // avifenc's defaults (quality 60, speed 6)
            ImageFileFormat.Heic => HeifCoder.Encode(image, HeifContainerType.Heic),
            ImageFileFormat.Exr => ExrCoder.Encode(image),
            ImageFileFormat.Sgi => SgiCoder.Encode(image),
            ImageFileFormat.Pix => PixCoder.Encode(image),
            ImageFileFormat.Sun => SunCoder.Encode(image),
            ImageFileFormat.Pdf => PdfCoder.Encode(image),
            ImageFileFormat.CameraRaw => CameraRawCoder.Encode(image),
            ImageFileFormat.Y4m => EncodeToStream(image, (img, st) => Y4mCoder.Write(img, st)),
            _ => throw new NotSupportedException($"Unsupported image format: {format}")
        };
    }

    private static byte[] EncodeToStream(ImageFrame image, Action<ImageFrame, Stream> writer)
    {
        using var ms = new MemoryStream();
        writer(image, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Read an animated/multi-frame image as a sequence of frames.
    /// </summary>
    public static ImageSequence ReadSequence(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var format = DetectFormat(data);
        if (format == ImageFileFormat.Unknown)
            format = DetectFromExtension(path);

        return format switch
        {
            ImageFileFormat.Gif => GifCoder.ReadSequence(new MemoryStream(data)),
            ImageFileFormat.WebP => WebpCoder.ReadSequence(new MemoryStream(data)),
            ImageFileFormat.Avif => HeifCoder.DecodeSequence(data),
            ImageFileFormat.Y4m => Y4mCoder.ReadSequence(data),
            _ => throw new NotSupportedException($"Multi-frame read not supported for: {format}")
        };
    }

    /// <summary>
    /// Write a multi-frame image sequence to file.
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, string path)
    {
        var format = DetectFromExtension(path);

        switch (format)
        {
            case ImageFileFormat.Gif:
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    GifCoder.WriteSequence(sequence, stream);
                break;
            case ImageFileFormat.WebP:
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    WebpCoder.WriteSequence(sequence, stream);
                break;
            case ImageFileFormat.Avif:
                File.WriteAllBytes(path, HeifCoder.EncodeAvifSequence(sequence));
                break;
            case ImageFileFormat.Y4m:
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    Y4mCoder.WriteSequence(sequence, stream);
                break;
            default:
                throw new NotSupportedException($"Multi-frame write not supported for: {format}");
        }
    }

    private static ImageFrame WebpRead(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return WebpCoder.Read(ms);
    }

    // Formats whose writers carry an ICC profile and/or CICP tags for the samples.
    private static bool CarriesColorDescription(ImageFileFormat format) => format is ImageFileFormat.Png or ImageFileFormat.Jpeg or
        ImageFileFormat.WebP or ImageFileFormat.Tiff or ImageFileFormat.JpegXl or ImageFileFormat.Avif or ImageFileFormat.Heic;

    /// <summary>
    /// CMYK ink amounts to sRGB: through the image's CMYK ICC profile with the colour management system (skcms, as
    /// libjxl renders CMYK), else with the device formula ImageMagick and Pillow use when no colour profile is applied,
    /// R = (1 - C)(1 - K) and likewise for G / B. Alpha is kept.
    /// </summary>
    internal static ImageFrame CmykToSrgb(ImageFrame cmyk)
    {
        int w = (int)cmyk.Columns, h = (int)cmyk.Rows, sc = cmyk.NumberOfChannels;
        var rgb = new ImageFrame();
        rgb.Initialize(w, h, ColorspaceType.SRGB, cmyk.HasAlpha);
        int dc = rgb.NumberOfChannels;
        Jxl.Lj.LjCms.Transform? cms = null;
        if (cmyk.IccProfile is { Length: > 0 } icc && Jxl.Lj.LjCms.FromIcc(icc) is { Cmyk: true } profile)
        {
            cms = Jxl.Lj.LjCms.Transform.Create(profile, Jxl.Lj.LjCe.Srgb(false), 255f);
        }

        var src = new float[w * 4];
        var dst = new float[w * 3];
        for (int y = 0; y < h; y++)
        {
            var s = cmyk.GetPixelRow(y);
            var d = rgb.GetPixelRowForWrite(y);
            if (cms != null)
            {
                // The CMS takes CMYK as JPEG XL stores it: 0 = full ink, 1 = none.
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 4; c++)
                        src[(x * 4) + c] = 1f - (s[(x * sc) + c] * (1f / 65535f));
                }

                cms.Run(src, dst, w);
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < 3; c++)
                        d[(x * dc) + c] = (ushort)MathF.Round(Math.Clamp(dst[(x * 3) + c], 0f, 1f) * 65535f, MidpointRounding.ToEven);
                    if (cmyk.HasAlpha) d[(x * dc) + 3] = s[(x * sc) + sc - 1];
                }

                continue;
            }

            for (int x = 0; x < w; x++)
            {
                int so = x * sc, dofs = x * dc;
                uint k = 65535u - s[so + 3];
                for (int c = 0; c < 3; c++)
                    d[dofs + c] = (ushort)(((65535u - s[so + c]) * k + 32767) / 65535);
                if (cmyk.HasAlpha) d[dofs + 3] = s[so + sc - 1];
            }
        }
        // keep Exif / XMP / IPTC (and orientation); a CMYK profile no longer describes the data
        rgb.Metadata = new SharpImage.Metadata.ImageMetadata
        {
            ExifProfile = cmyk.Metadata.ExifProfile, Xmp = cmyk.Metadata.Xmp, IptcProfile = cmyk.Metadata.IptcProfile,
            PixelAspectRatio = cmyk.Metadata.PixelAspectRatio,
        };
        rgb.Orientation = cmyk.Orientation;
        return rgb;
    }
}
