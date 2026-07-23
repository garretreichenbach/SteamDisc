using System.Globalization;
using System.IO.Compression;
using System.Text;
using SteamDisc.Core.Images;

namespace SteamDisc.Covers.Pdf;

/// <summary>
/// A very small PDF writer: enough to place images and text at exact physical positions on a
/// page of an exact physical size.
/// </summary>
/// <remarks>
/// PDF is the right output for a printable cover — it is the only common format that carries
/// real-world dimensions, so a print shop gets 273 mm rather than "some number of pixels at
/// some assumed DPI". Writing it directly avoids both a rendering dependency and any
/// resampling of the user's artwork: images go in exactly as they came out of the art
/// provider.
/// </remarks>
public sealed class PdfDocument
{
    private readonly List<byte[]> _objects = new();
    private readonly List<PdfPage> _pages = new();

    /// <summary>Adds a page sized in millimetres.</summary>
    public PdfPage AddPage(SizeMm size)
    {
        var page = new PdfPage(size);
        _pages.Add(page);
        return page;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(stream);
    }

    public void Write(Stream output)
    {
        _objects.Clear();

        // Object 1 is the catalog and object 2 the page tree; both are reserved up front so
        // pages can reference the tree before it is written.
        var catalogId = Reserve();
        var pagesId = Reserve();
        var fontId = Reserve();

        Set(fontId, Latin1(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        var pageIds = new List<int>();

        foreach (var page in _pages)
        {
            var resourceEntries = new StringBuilder();
            var imageEntries = new StringBuilder();

            foreach (var placement in page.Images)
            {
                var imageId = WriteImage(placement.Image);
                imageEntries.Append(CultureInfo.InvariantCulture, $"/{placement.ResourceName} {imageId} 0 R ");
            }

            if (imageEntries.Length > 0)
            {
                resourceEntries.Append("/XObject << ").Append(imageEntries).Append(">> ");
            }

            resourceEntries.Append(CultureInfo.InvariantCulture, $"/Font << /F1 {fontId} 0 R >>");

            var contentBytes = Latin1(page.BuildContentStream());
            var contentId = Reserve();
            Set(contentId, Stream(
                $"<< /Length {contentBytes.Length} >>",
                contentBytes));

            var pageId = Reserve();
            var widthPoints = PrintUnits.MmToPoints(page.Size.Width);
            var heightPoints = PrintUnits.MmToPoints(page.Size.Height);

            Set(pageId, Latin1(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {widthPoints:0.####} {heightPoints:0.####}] " +
                $"/Resources << {resourceEntries} >> /Contents {contentId} 0 R >>")));

            pageIds.Add(pageId);
        }

        var kids = string.Join(" ", pageIds.Select(id => $"{id} 0 R"));
        Set(pagesId, Latin1($"<< /Type /Pages /Count {pageIds.Count} /Kids [{kids}] >>"));
        Set(catalogId, Latin1($"<< /Type /Catalog /Pages {pagesId} 0 R >>"));

        WriteFile(output);
    }

    private int WriteImage(RasterImage image)
    {
        int? maskId = null;

        if (image.SoftMask is { } mask)
        {
            var maskData = Deflate(mask);
            maskId = Reserve();
            Set(maskId.Value, Stream(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} " +
                    $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {maskData.Length} >>"),
                maskData));
        }

        var dictionary = new StringBuilder();
        dictionary.Append(CultureInfo.InvariantCulture,
            $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} ");

        switch (image.ColorSpace)
        {
            case RasterColorSpace.Indexed:
                var palette = image.Palette!;
                var paletteId = Reserve();
                Set(paletteId, Stream($"<< /Length {palette.Length} >>", palette));
                dictionary.Append(CultureInfo.InvariantCulture,
                    $"/ColorSpace [/Indexed /DeviceRGB {(palette.Length / 3) - 1} {paletteId} 0 R] ");
                break;

            case RasterColorSpace.Gray:
                dictionary.Append("/ColorSpace /DeviceGray ");
                break;

            case RasterColorSpace.Cmyk:
                dictionary.Append("/ColorSpace /DeviceCMYK ");
                if (image.InvertedCmyk)
                {
                    // Adobe writes four-channel JPEGs inverted; reverse it in the Decode array
                    // so the samples themselves are still passed through untouched.
                    dictionary.Append("/Decode [1 0 1 0 1 0 1 0] ");
                }

                break;

            default:
                dictionary.Append("/ColorSpace /DeviceRGB ");
                break;
        }

        dictionary.Append(CultureInfo.InvariantCulture, $"/BitsPerComponent {image.BitsPerComponent} ");

        byte[] data;
        if (image.Format == RasterFormat.Jpeg)
        {
            dictionary.Append("/Filter /DCTDecode ");
            data = image.Data;
        }
        else if (image.DataIsDeflate)
        {
            // The PNG's own zlib stream, replayed through PDF's PNG predictor.
            dictionary.Append(CultureInfo.InvariantCulture,
                $"/Filter /FlateDecode /DecodeParms << /Predictor 15 /Colors {image.Components} " +
                $"/BitsPerComponent {image.BitsPerComponent} /Columns {image.Width} >> ");
            data = image.Data;
        }
        else
        {
            dictionary.Append("/Filter /FlateDecode ");
            data = Deflate(image.Data);
        }

        if (maskId is { } id)
        {
            dictionary.Append(CultureInfo.InvariantCulture, $"/SMask {id} 0 R ");
        }

        dictionary.Append(CultureInfo.InvariantCulture, $"/Length {data.Length} >>");

        var objectId = Reserve();
        Set(objectId, Stream(dictionary.ToString(), data));
        return objectId;
    }

    private void WriteFile(Stream output)
    {
        var offsets = new long[_objects.Count + 1];

        using var writer = new BinaryWriter(output, Encoding.Latin1, leaveOpen: true);
        writer.Write(Latin1("%PDF-1.7\n"));
        // A binary comment marks the file as containing binary data, per the spec's advice.
        writer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        for (var i = 0; i < _objects.Count; i++)
        {
            offsets[i + 1] = output.Position;
            writer.Write(Latin1($"{i + 1} 0 obj\n"));
            writer.Write(_objects[i]);
            writer.Write(Latin1("\nendobj\n"));
        }

        var xrefOffset = output.Position;
        writer.Write(Latin1($"xref\n0 {_objects.Count + 1}\n"));
        writer.Write(Latin1("0000000000 65535 f \n"));
        for (var i = 1; i <= _objects.Count; i++)
        {
            writer.Write(Latin1($"{offsets[i]:D10} 00000 n \n"));
        }

        writer.Write(Latin1(
            $"trailer\n<< /Size {_objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n"));
    }

    private int Reserve()
    {
        _objects.Add(Array.Empty<byte>());
        return _objects.Count;
    }

    private void Set(int id, byte[] content) => _objects[id - 1] = content;

    private static byte[] Stream(string dictionary, byte[] data)
    {
        using var buffer = new MemoryStream();
        var header = Latin1(dictionary + "\nstream\n");
        buffer.Write(header);
        buffer.Write(data);
        buffer.Write(Latin1("\nendstream"));
        return buffer.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    internal static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
}
