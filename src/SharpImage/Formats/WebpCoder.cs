using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// WebP format reader and writer: VP8L (lossless) and VP8 (lossy, with an ALPH alpha plane), stills and animations.
/// The decoders and the two encoders are line-by-line ports of libwebp 1.6.0 (Formats/WebP); the encoders produce the
/// same bytes as libwebp's WebPEncode (cwebp) for the same pixels and options.
/// </summary>
public static class WebpCoder
{
    /// <summary>
    /// Detect WebP format by RIFF+WEBP signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 12 &&
        data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
        data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

    private const int MaxWebpDimension = 16384;

    // ═══════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════

    public static ImageFrame Read(Stream stream)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        if (data.Length < 12)
        {
            throw new InvalidDataException("WebP file too small.");
        }

        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
        {
            throw new InvalidDataException("Missing RIFF signature.");
        }

        if (data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
        {
            throw new InvalidDataException("Missing WEBP signature.");
        }

        int offset = 12;
        byte[]? vp8lData = null;
        byte[]? vp8Data = null;
        byte[]? alphData = null;
        bool isAnimation = false;

        while (offset + 8 <= data.Length)
        {
            uint chunkSize = BitConverter.ToUInt32(data, offset + 4);
            int safeSize = (int)Math.Min(chunkSize, data.Length - offset - 8);
            int chunkStart = offset + 8;
            byte c0 = data[offset], c1 = data[offset + 1], c2 = data[offset + 2], c3 = data[offset + 3];

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'L')
            {
                vp8lData = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, vp8lData, 0, safeSize);
            }
            else if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == ' ')
            {
                vp8Data = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, vp8Data, 0, safeSize);
            }
            else if (c0 == 'A' && c1 == 'L' && c2 == 'P' && c3 == 'H')
            {
                alphData = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, alphData, 0, safeSize);
            }
            else if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'X' && safeSize >= 10)
            {
                isAnimation = (data[chunkStart] & 0x02) != 0;
            }

            offset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - offset));
            if ((chunkSize & 1) != 0)
            {
                offset++;
            }
        }

        if (vp8lData != null)
        {
            return DecodeVp8l(vp8lData);
        }

        if (vp8Data != null)
        {
            return DecodeVp8Lossy(vp8Data, alphData);
        }

        // An animated WebP: the first frame, composited onto the canvas (WebPAnimDecoder).
        if (isAnimation)
        {
            var sequence = ReadSequence(new MemoryStream(data));
            if (sequence.Count > 0) return sequence[0];
        }
        throw new InvalidDataException("No VP8 or VP8L chunk found in WebP file.");
    }

    /// <summary>
    /// Writes an image as WebP: quality &gt;= 100 is lossless with cwebp's <c>-lossless</c> defaults, otherwise lossy
    /// like <c>cwebp -q quality</c> (every other setting at cwebp's defaults).
    /// </summary>
    public static void Write(ImageFrame image, Stream stream, int quality = 100)
    {
        if (quality >= 100)
            Write(image, stream, new WebP.WebpLosslessOptions());
        else
            Write(image, stream, new WebP.WebpLossyOptions { Quality = Math.Max(quality, 0) });
    }

    /// <summary>Writes an image as lossy WebP (VP8, plus an ALPH chunk when it has transparency) — byte-identical to
    /// libwebp's WebPEncode / cwebp with the same options.</summary>
    public static void Write(ImageFrame image, Stream stream, WebP.WebpLossyOptions options)
    {
        byte[] webp = WebP.Vp8Encoder.Encode(ImageFrameToArgb(image), (int)image.Columns, (int)image.Rows, options);
        stream.Write(webp);
    }

    /// <summary>Writes an image as lossless WebP (VP8L) — byte-identical to libwebp's WebPEncode / cwebp -lossless with
    /// the same options.</summary>
    public static void Write(ImageFrame image, Stream stream, WebP.WebpLosslessOptions options)
    {
        byte[] webp = WebP.Vp8lEncoder.EncodeImage(ImageFrameToArgb(image), (int)image.Columns, (int)image.Rows, options);
        stream.Write(webp);
    }
    /// <summary>
    /// Read an animated WebP file into an ImageSequence.
    /// If the file is a single-frame WebP, returns a sequence with one frame.
    /// </summary>
    public static ImageSequence ReadSequence(Stream stream)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        if (data.Length < 12 ||
            data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
            data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
            throw new InvalidDataException("Invalid WebP file.");

        var sequence = new ImageSequence { FormatName = "WEBP" };
        int offset = 12;
        bool isAnimation = false;
        int canvasWidth = 0, canvasHeight = 0;

        // First pass: look for VP8X to detect animation
        int firstPassOffset = 12;
        while (firstPassOffset + 8 <= data.Length)
        {
            byte c0 = data[firstPassOffset], c1 = data[firstPassOffset + 1];
            byte c2 = data[firstPassOffset + 2], c3 = data[firstPassOffset + 3];
            uint chunkSize = BitConverter.ToUInt32(data, firstPassOffset + 4);
            int safeSize = (int)Math.Min(chunkSize, data.Length - firstPassOffset - 8);

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'X' && safeSize >= 10)
            {
                byte flags = data[firstPassOffset + 8];
                isAnimation = (flags & 0x02) != 0;
                canvasWidth = Read24LE(data, firstPassOffset + 12) + 1;
                canvasHeight = Read24LE(data, firstPassOffset + 15) + 1;
                break;
            }

            firstPassOffset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - firstPassOffset));
            if ((chunkSize & 1) != 0) firstPassOffset++;
        }

        if (!isAnimation)
        {
            // Not animated — decode as single frame
            var frame = Read(new MemoryStream(data));
            frame.Delay = 0;
            sequence.AddFrame(frame);
            sequence.CanvasWidth = (int)frame.Columns;
            sequence.CanvasHeight = (int)frame.Rows;
            return sequence;
        }

        sequence.CanvasWidth = canvasWidth;
        sequence.CanvasHeight = canvasHeight;

        // Decode every ANMF frame, then composite them the way libwebp's WebPAnimDecoder does.
        var animFrames = ReadAnimationFrames(data, out int loopCount);
        sequence.LoopCount = loopCount;
        var canvases = WebP.WebpAnimDecoder.Composite(animFrames, canvasWidth, canvasHeight);
        for (int f = 0; f < canvases.Count; f++)
        {
            var frame = ArgbToImageFrame(canvases[f], canvasWidth, canvasHeight, true);
            // Convert duration from ms to centiseconds (like GIF)
            frame.Delay = Math.Max(1, animFrames[f].DurationMs / 10);
            frame.DisposeMethod = DisposeType.None;   // frames are already composited onto the full canvas
            sequence.AddFrame(frame);
        }

        if (sequence.Count == 0)
            throw new InvalidDataException("No animation frames found in animated WebP.");

        return sequence;
    }

    /// <summary>The ANMF frames of an animated WebP (each with an optional ALPH + VP8, or VP8L), decoded to ARGB.</summary>
    private static List<WebP.WebpAnimFrame> ReadAnimationFrames(byte[] data, out int loopCount)
    {
        var frames = new List<WebP.WebpAnimFrame>();
        loopCount = 0;
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            byte c0 = data[offset], c1 = data[offset + 1], c2 = data[offset + 2], c3 = data[offset + 3];
            uint chunkSize = BitConverter.ToUInt32(data, offset + 4);
            int safeSize = (int)Math.Min(chunkSize, (uint)(data.Length - offset - 8));
            int chunkStart = offset + 8;
            if (c0 == 'A' && c1 == 'N' && c2 == 'I' && c3 == 'M' && safeSize >= 6)
            {
                loopCount = BitConverter.ToUInt16(data, chunkStart + 4);
            }
            else if (c0 == 'A' && c1 == 'N' && c2 == 'M' && c3 == 'F' && safeSize >= 16)
            {
                var af = new WebP.WebpAnimFrame
                {
                    X = Read24LE(data, chunkStart) * 2,
                    Y = Read24LE(data, chunkStart + 3) * 2,
                    Width = Read24LE(data, chunkStart + 6) + 1,
                    Height = Read24LE(data, chunkStart + 9) + 1,
                    DurationMs = Read24LE(data, chunkStart + 12),
                };
                byte frameFlags = data[chunkStart + 15];
                af.DisposeToBackground = (frameFlags & 0x01) != 0;
                af.Blend = (frameFlags & 0x02) == 0;

                int subOffset = chunkStart + 16;
                int subRemaining = safeSize - 16;
                byte[]? frameAlpha = null;
                ImageFrame? decoded = null;
                while (subRemaining >= 8)
                {
                    byte s0 = data[subOffset], s1 = data[subOffset + 1], s2 = data[subOffset + 2], s3 = data[subOffset + 3];
                    uint subSize = BitConverter.ToUInt32(data, subOffset + 4);
                    int subDataSize = (int)Math.Min(subSize, (uint)(subRemaining - 8));
                    byte[] subData = new byte[subDataSize];
                    Buffer.BlockCopy(data, subOffset + 8, subData, 0, subDataSize);
                    if (s0 == 'A' && s1 == 'L' && s2 == 'P' && s3 == 'H')
                    {
                        frameAlpha = subData;
                    }
                    else if (s0 == 'V' && s1 == 'P' && s2 == '8' && s3 == 'L')
                    {
                        af.Argb = WebP.Vp8lDecoder.Decode(subData, 0, subData.Length, out int w, out int h, out bool alpha);
                        af.HasAlpha = alpha;
                        if (w != af.Width || h != af.Height) throw new InvalidDataException("ANMF frame size mismatch.");
                        break;
                    }
                    else if (s0 == 'V' && s1 == 'P' && s2 == '8' && s3 == ' ')
                    {
                        decoded = DecodeVp8Lossy(subData, frameAlpha);
                        af.HasAlpha = frameAlpha != null;
                        break;
                    }
                    int advance = 8 + subDataSize + (subDataSize & 1);
                    subOffset += advance;
                    subRemaining -= advance;
                }
                if (decoded != null)
                {
                    if (decoded.Columns != af.Width || decoded.Rows != af.Height) throw new InvalidDataException("ANMF frame size mismatch.");
                    af.Argb = ImageFrameToArgb(decoded);
                }
                if (af.Argb.Length == 0) throw new InvalidDataException("ANMF frame without image data.");
                frames.Add(af);
            }
            offset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - offset - 8));
            if ((chunkSize & 1) != 0) offset++;
        }
        if (frames.Count == 0) throw new InvalidDataException("No animation frames found in animated WebP.");
        return frames;
    }

    private static uint[] ImageFrameToArgb(ImageFrame img)
    {
        int w = (int)img.Columns, h = (int)img.Rows, ch = img.NumberOfChannels;
        var argb = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                uint r = (uint)(row[x * ch] >> 8), g = (uint)(row[x * ch + 1] >> 8), b = (uint)(row[x * ch + 2] >> 8);
                uint a = img.HasAlpha ? (uint)(row[x * ch + ch - 1] >> 8) : 255u;
                argb[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
        return argb;
    }

    /// <summary>
    /// Writes an ImageSequence as an animated WebP (VP8X + ANIM + one full-canvas ANMF per frame). quality &gt;= 100
    /// codes every frame losslessly, otherwise lossy at that quality (with an ALPH chunk for transparent frames).
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, Stream stream, int quality = 100)
    {
        if (sequence.Count == 0)
            throw new ArgumentException("Sequence has no frames.");

        if (sequence.Count == 1)
        {
            Write(sequence[0], stream, quality);
            return;
        }

        // Each frame's image chunks (ALPH + "VP8 ", or "VP8L"): a still encoding minus its RIFF / VP8X headers.
        var frameChunks = new List<byte[]>();
        bool anyAlpha = false;
        for (int i = 0; i < sequence.Count; i++)
        {
            var frame = sequence[i];
            using var ms = new MemoryStream();
            Write(frame, ms, quality);
            byte[] file = ms.ToArray();
            int start = 12;
            if (file.AsSpan(12, 4).SequenceEqual("VP8X"u8)) start += 8 + 10;
            frameChunks.Add(file[start..]);
            if (frame.HasAlpha) anyAlpha = true;
        }

        int canvasWidth = sequence.CanvasWidth > 0 ? sequence.CanvasWidth : (int)sequence[0].Columns;
        int canvasHeight = sequence.CanvasHeight > 0 ? sequence.CanvasHeight : (int)sequence[0].Rows;

        // Build the RIFF payload
        using var payloadMs = new MemoryStream();
        using (var bw = new BinaryWriter(payloadMs, System.Text.Encoding.ASCII, true))
        {
            // VP8X chunk (extended format)
            bw.Write("VP8X"u8);
            bw.Write((uint)10);
            byte vp8xFlags = 0x02; // animation flag
            if (anyAlpha) vp8xFlags |= 0x10; // alpha flag
            bw.Write(vp8xFlags);
            bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); // reserved
            Write24LE(bw, canvasWidth - 1);
            Write24LE(bw, canvasHeight - 1);

            // ANIM chunk
            bw.Write("ANIM"u8);
            bw.Write((uint)6);
            bw.Write((uint)0); // background color (BGRA, transparent)
            bw.Write((ushort)sequence.LoopCount);

            // ANMF chunks
            for (int i = 0; i < sequence.Count; i++)
            {
                var frame = sequence[i];
                int frameWidth = (int)frame.Columns;
                int frameHeight = (int)frame.Rows;
                int durationMs = frame.Delay * 10; // centiseconds to ms
                if (durationMs < 1) durationMs = 100; // default 100ms

                byte[] subChunk = frameChunks[i];
                int anmfPayloadSize = 16 + subChunk.Length;

                bw.Write("ANMF"u8);
                bw.Write((uint)anmfPayloadSize);
                Write24LE(bw, 0); // frame X / 2
                Write24LE(bw, 0); // frame Y / 2
                Write24LE(bw, frameWidth - 1);
                Write24LE(bw, frameHeight - 1);
                Write24LE(bw, durationMs);
                // bit 1 set: do not blend (each frame is a full replacement); bit 0: dispose to background
                byte flags = 0x02;
                if (frame.DisposeMethod == DisposeType.Background) flags |= 0x01;
                bw.Write(flags);
                bw.Write(subChunk);

                if ((anmfPayloadSize & 1) != 0) bw.Write((byte)0);
            }
        }

        byte[] riffPayload = payloadMs.ToArray();
        int riffSize = 4 + riffPayload.Length; // "WEBP" + payload

        using var finalBw = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        finalBw.Write("RIFF"u8);
        finalBw.Write((uint)riffSize);
        finalBw.Write("WEBP"u8);
        finalBw.Write(riffPayload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Read24LE(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write24LE(BinaryWriter bw, int value)
    {
        bw.Write((byte)(value & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
    }

    private static ImageFrame DecodeVp8l(byte[] data)
    {
        // libwebp 1.6.0 vp8l_dec.c semantics (Formats/WebP/Vp8lDecoder.cs)
        uint[] pixels = WebP.Vp8lDecoder.Decode(data, 0, data.Length, out int width, out int height, out bool alphaUsed);
        if (width > MaxWebpDimension || height > MaxWebpDimension)
        {
            throw new InvalidDataException("WebP image dimensions exceed maximum.");
        }
        return ArgbToImageFrame(pixels, width, height, alphaUsed);
    }

    private static ImageFrame DecodeVp8Lossy(byte[] data, byte[]? alphData)
    {
        return Vp8LossyCodec.Decode(data, alphData);
    }

    private static ImageFrame ArgbToImageFrame(uint[] argb, int width, int height, bool hasAlpha)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        frame.Compression = CompressionType.WebP;
        int channels = frame.NumberOfChannels;

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                uint p = argb[y * width + x];
                int off = x * channels;
                row[off] = Quantum.ScaleFromByte((byte)((p >> 16) & 0xFF));     // R
                row[off + 1] = Quantum.ScaleFromByte((byte)((p >> 8) & 0xFF));  // G
                row[off + 2] = Quantum.ScaleFromByte((byte)(p & 0xFF));         // B
                if (hasAlpha)
                {
                    row[off + 3] = Quantum.ScaleFromByte((byte)(p >> 24));       // A
                }
            }
        }
        return frame;
    }
}
