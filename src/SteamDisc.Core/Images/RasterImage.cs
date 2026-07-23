using System.Buffers.Binary;
using System.IO.Compression;

namespace SteamDisc.Core.Images;

public enum RasterFormat
{
    Png,
    Jpeg,
}

public enum RasterColorSpace
{
    Gray,
    Rgb,
    Indexed,

    /// <summary>Four-channel process colour, as print-oriented JPEGs often use.</summary>
    Cmyk,
}

/// <summary>
/// A raster image prepared for embedding in a PDF.
/// </summary>
/// <remarks>
/// <para>
/// No image library is used, and no pixels are decoded unless they have to be. Two facts make
/// that possible:
/// </para>
/// <list type="bullet">
///   <item>
///     JPEG data can be handed to a PDF verbatim as a <c>DCTDecode</c> stream, so a photo
///     round-trips at full quality with no re-encoding at all.
///   </item>
///   <item>
///     A PNG's <c>IDAT</c> is zlib-compressed data with PNG row filters, which is exactly what
///     PDF's <c>FlateDecode</c> with <c>/Predictor 15</c> expects. So a non-transparent PNG is
///     also a straight pass-through.
///   </item>
/// </list>
/// <para>
/// Only PNGs with an alpha channel need real decoding, because PDF keeps transparency in a
/// separate soft mask and the samples have to be split apart.
/// </para>
/// </remarks>
public sealed class RasterImage
{
    private RasterImage(
        RasterFormat format,
        int width,
        int height,
        int bitsPerComponent,
        RasterColorSpace colorSpace,
        byte[] data,
        bool dataIsDeflate,
        byte[]? palette,
        byte[]? softMask)
    {
        Format = format;
        Width = width;
        Height = height;
        BitsPerComponent = bitsPerComponent;
        ColorSpace = colorSpace;
        Data = data;
        DataIsDeflate = dataIsDeflate;
        Palette = palette;
        SoftMask = softMask;
    }

    public RasterFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    public int BitsPerComponent { get; }

    public RasterColorSpace ColorSpace { get; }

    /// <summary>Stream bytes for the PDF image object.</summary>
    public byte[] Data { get; }

    /// <summary>
    /// True when <see cref="Data"/> is a zlib stream carrying PNG-filtered rows, which the
    /// PDF writer emits with a predictor. False means raw JPEG or already-unfiltered samples.
    /// </summary>
    public bool DataIsDeflate { get; }

    /// <summary>RGB palette for indexed images, three bytes per entry.</summary>
    public byte[]? Palette { get; }

    /// <summary>8-bit alpha, one byte per pixel, when the source had transparency.</summary>
    public byte[]? SoftMask { get; }

    /// <summary>
    /// True for an Adobe-produced CMYK JPEG, whose samples are stored inverted. PDF reverses
    /// that with a Decode array rather than by rewriting the pixels.
    /// </summary>
    public bool InvertedCmyk { get; private set; }

    public int Components => ColorSpace switch
    {
        RasterColorSpace.Gray => 1,
        RasterColorSpace.Rgb => 3,
        RasterColorSpace.Cmyk => 4,
        _ => 1,
    };

    public double AspectRatio => Height > 0 ? (double)Width / Height : 0;

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg";
    }

    public static RasterImage Load(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
        {
            return LoadPng(bytes, path);
        }

        if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return LoadJpeg(bytes, path);
        }

        throw new NotSupportedException(
            $"'{Path.GetFileName(path)}' is not a PNG or JPEG. Convert it and try again.");
    }

    /// <summary>Reads only the dimensions, for aspect checks and DPI warnings.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < 24)
            {
                return null;
            }

            if (header[0] == 0x89 && header[1] == 'P')
            {
                return (
                    BinaryPrimitives.ReadInt32BigEndian(header[16..]),
                    BinaryPrimitives.ReadInt32BigEndian(header[20..]));
            }

            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                var image = LoadJpeg(File.ReadAllBytes(path), path);
                return (image.Width, image.Height);
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException)
        {
            return null;
        }

        return null;
    }

    private static RasterImage LoadJpeg(byte[] bytes, string path)
    {
        // Walk the marker segments to find a start-of-frame, which carries the real dimensions.
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            offset += 2;

            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));

            // SOF0..SOF15, excluding the non-frame markers in that range.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var precision = bytes[offset + 2];
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5));
                var components = bytes[offset + 7];

                var colorSpace = components switch
                {
                    1 => RasterColorSpace.Gray,
                    3 => RasterColorSpace.Rgb,
                    // Print-oriented sources, including plenty of downloaded cover art, are
                    // CMYK. PDF carries those natively, so there is no reason to refuse them.
                    4 => RasterColorSpace.Cmyk,
                    _ => throw new NotSupportedException(
                        $"'{Path.GetFileName(path)}' is a {components}-component JPEG, which is not supported."),
                };

                return new RasterImage(
                    RasterFormat.Jpeg, width, height, precision, colorSpace, bytes, false, null, null)
                {
                    InvertedCmyk = colorSpace == RasterColorSpace.Cmyk && HasAdobeMarker(bytes),
                };
            }

            offset += length;
        }

        throw new InvalidDataException($"'{Path.GetFileName(path)}' has no JPEG frame header.");
    }

    /// <summary>
    /// Looks for the Adobe APP14 marker, which is what signals that a four-channel JPEG's
    /// samples are stored inverted.
    /// </summary>
    private static bool HasAdobeMarker(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            offset += 2;

            if (marker is 0xD8 or 0x01 or 0xFF || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (marker == 0xDA || offset + 2 > bytes.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));

            if (marker == 0xEE && length >= 7 && offset + 7 <= bytes.Length)
            {
                return System.Text.Encoding.ASCII.GetString(bytes, offset + 2, 5) == "Adobe";
            }

            offset += length;
        }

        return false;
    }

    private static RasterImage LoadPng(byte[] bytes, string path)
    {
        var chunks = ReadPngChunks(bytes);

        var header = chunks.FirstOrDefault(c => c.Type == "IHDR")
                     ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' has no IHDR chunk.");

        var width = BinaryPrimitives.ReadInt32BigEndian(header.Data);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(4));
        var bitDepth = header.Data[8];
        var colorType = header.Data[9];
        var interlace = header.Data[12];

        if (interlace != 0)
        {
            throw new NotSupportedException(
                $"'{Path.GetFileName(path)}' is an interlaced PNG, which is not supported. Re-save it without interlacing.");
        }

        var idat = ConcatenateChunks(chunks, "IDAT");
        if (idat.Length == 0)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no image data.");
        }

        var hasAlpha = colorType is 4 or 6;
        var transparency = chunks.FirstOrDefault(c => c.Type == "tRNS");

        if (!hasAlpha && transparency is null)
        {
            // Pass-through: PDF's FlateDecode with a PNG predictor understands this as-is.
            var colorSpace = colorType switch
            {
                0 => RasterColorSpace.Gray,
                2 => RasterColorSpace.Rgb,
                3 => RasterColorSpace.Indexed,
                _ => throw new NotSupportedException($"Unsupported PNG colour type {colorType}."),
            };

            var palette = colorType == 3
                ? chunks.FirstOrDefault(c => c.Type == "PLTE")?.Data
                  ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is indexed but has no palette.")
                : null;

            return new RasterImage(
                RasterFormat.Png, width, height, bitDepth, colorSpace, idat, true, palette, null);
        }

        // Alpha present: decode so the colour samples and the mask can be separated.
        var decoded = PngDecoder.Decode(idat, width, height, bitDepth, colorType, chunks);
        return new RasterImage(
            RasterFormat.Png,
            width,
            height,
            8,
            decoded.IsGray ? RasterColorSpace.Gray : RasterColorSpace.Rgb,
            decoded.Color,
            false,
            null,
            decoded.Alpha);
    }

    internal static List<PngChunk> ReadPngChunks(byte[] bytes)
    {
        var chunks = new List<PngChunk>();
        var offset = 8;

        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset));
            if (length < 0 || offset + 12 + length > bytes.Length)
            {
                break;
            }

            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = new byte[length];
            Array.Copy(bytes, offset + 8, data, 0, length);
            chunks.Add(new PngChunk(type, data));

            offset += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    private static byte[] ConcatenateChunks(IEnumerable<PngChunk> chunks, string type)
    {
        var matching = chunks.Where(c => c.Type == type).ToList();
        if (matching.Count == 0)
        {
            return Array.Empty<byte>();
        }

        if (matching.Count == 1)
        {
            return matching[0].Data;
        }

        var result = new byte[matching.Sum(c => c.Data.Length)];
        var offset = 0;
        foreach (var chunk in matching)
        {
            chunk.Data.CopyTo(result, offset);
            offset += chunk.Data.Length;
        }

        return result;
    }

    internal sealed record PngChunk(string Type, byte[] Data);
}

/// <summary>Minimal PNG decoder, used only when an image has an alpha channel.</summary>
internal static class PngDecoder
{
    public static (byte[] Color, byte[] Alpha, bool IsGray) Decode(
        byte[] zlibData,
        int width,
        int height,
        int bitDepth,
        int colorType,
        List<RasterImage.PngChunk> chunks)
    {
        if (bitDepth != 8)
        {
            throw new NotSupportedException(
                $"PNGs with transparency must be 8 bits per channel; this one is {bitDepth}.");
        }

        _ = chunks;

        var samplesPerPixel = colorType switch
        {
            4 => 2, // gray + alpha
            6 => 4, // RGB + alpha
            _ => throw new NotSupportedException($"Unsupported PNG colour type {colorType} in the alpha path."),
        };

        var raw = Inflate(zlibData, height * ((width * samplesPerPixel) + 1));
        var stride = width * samplesPerPixel;

        var isGray = colorType == 4;
        var colorComponents = isGray ? 1 : 3;
        var color = new byte[width * height * colorComponents];
        var alpha = new byte[width * height];

        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            var filter = raw[rowStart];
            Array.Copy(raw, rowStart + 1, current, 0, stride);

            Unfilter(filter, current, previous, samplesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var source = x * samplesPerPixel;
                var pixel = (y * width) + x;

                if (isGray)
                {
                    color[pixel] = current[source];
                    alpha[pixel] = current[source + 1];
                }
                else
                {
                    color[(pixel * 3) + 0] = current[source + 0];
                    color[(pixel * 3) + 1] = current[source + 1];
                    color[(pixel * 3) + 2] = current[source + 2];
                    alpha[pixel] = current[source + 3];
                }
            }

            (previous, current) = (current, previous);
        }

        return (color, alpha, isGray);
    }

    /// <summary>Inflates a zlib stream by skipping its two-byte header.</summary>
    private static byte[] Inflate(byte[] zlibData, int expectedLength)
    {
        using var input = new MemoryStream(zlibData, 2, zlibData.Length - 2, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        deflate.CopyTo(output);

        var result = output.ToArray();
        if (result.Length < expectedLength)
        {
            throw new InvalidDataException(
                $"PNG data expanded to {result.Length} bytes but {expectedLength} were expected.");
        }

        return result;
    }

    private static void Unfilter(byte filter, byte[] current, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                break;

            case 1: // Sub
                for (var i = bytesPerPixel; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + current[i - bytesPerPixel]);
                }

                break;

            case 2: // Up
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + previous[i]);
                }

                break;

            case 3: // Average
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) / 2));
                }

                break;

            case 4: // Paeth
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : (byte)0;
                    var up = previous[i];
                    var upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;
                    current[i] = (byte)(current[i] + Paeth(left, up, upperLeft));
                }

                break;

            default:
                throw new InvalidDataException($"Unknown PNG row filter {filter}.");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
