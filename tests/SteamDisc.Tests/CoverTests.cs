using System.IO.Compression;
using System.Text;
using SteamDisc.Core.Images;
using SteamDisc.Covers;
using SteamDisc.Covers.Pdf;

namespace SteamDisc.Tests;

public class CoverTemplateTests
{
    [Fact]
    public void Built_in_templates_are_all_valid()
    {
        foreach (var template in CoverTemplateCatalog.BuiltIn)
        {
            Assert.Empty(template.Validate());
        }
    }

    [Fact]
    public void Case_wraps_add_up_to_their_trim_width()
    {
        foreach (var template in new[] { CoverTemplateCatalog.SgcDvdCase(), CoverTemplateCatalog.SgcBluRayCase() })
        {
            var slots = template.Slots.OrderBy(s => s.Bounds.X).ToList();
            var total = slots.Sum(s => s.Bounds.Width);

            // Back + spine + front must exactly cover the wrap, or the spine lands off-centre
            // and the whole cover looks wrong in the case.
            Assert.Equal(template.Trim.Width, total, 3);
            Assert.Equal(template.Trim.X, slots[0].Bounds.X, 3);
            Assert.Equal(template.Trim.Right, slots[^1].Bounds.Right, 3);
        }
    }

    [Fact]
    public void Slots_within_a_template_do_not_overlap()
    {
        foreach (var template in CoverTemplateCatalog.BuiltIn)
        {
            var slots = template.Slots.OrderBy(s => s.Bounds.X).ToList();
            for (var i = 1; i < slots.Count; i++)
            {
                Assert.True(
                    slots[i].Bounds.X >= slots[i - 1].Bounds.Right - 0.001,
                    $"{template.Id}: '{slots[i].Id}' overlaps '{slots[i - 1].Id}'");
            }
        }
    }

    [Fact]
    public void The_trim_box_sits_inside_the_page()
    {
        foreach (var template in CoverTemplateCatalog.BuiltIn)
        {
            Assert.True(template.Trim.X >= -0.001, template.Id);
            Assert.True(template.Trim.Y >= -0.001, template.Id);
            Assert.True(template.Trim.Right <= template.Page.Width + 0.001, template.Id);
            Assert.True(template.Trim.Bottom <= template.Page.Height + 0.001, template.Id);
        }
    }

    [Fact]
    public void Steam_layouts_match_their_source_sheets_at_300_dpi()
    {
        // The geometry was measured from the published 300 DPI sheets; if these drift, an
        // imported design will no longer line up with the slots underneath it.
        foreach (var (id, width, height) in new[]
                 {
                     ("sgc-dvd", 3300, 2550),
                     ("sgc-bluray", 3300, 2550),
                     ("sgc-disc", 1800, 1800),
                     ("sgc-jewel-front", 1800, 1800),
                     ("sgc-jewel-back", 1950, 1950),
                 })
        {
            var template = CoverTemplateCatalog.BuiltIn.Single(t => t.Id == id);

            Assert.Equal(width, PrintUnits.MmToPixels(template.Page.Width, 300), 0);
            Assert.Equal(height, PrintUnits.MmToPixels(template.Page.Height, 300), 0);
        }
    }

    [Fact]
    public void Steam_case_wraps_carry_their_measured_spine_widths()
    {
        Assert.Equal(14.0, CoverTemplateCatalog.SgcDvdCase().FindSlot("spine")!.Bounds.Width, 2);
        Assert.Equal(11.0, CoverTemplateCatalog.SgcBluRayCase().FindSlot("spine")!.Bounds.Width, 2);
    }

    [Fact]
    public void Steam_layouts_do_not_add_a_second_set_of_crop_marks()
    {
        foreach (var template in CoverTemplateCatalog.BuiltIn.Where(t => t.Family == "Steam"))
        {
            Assert.False(template.DrawCropMarks, $"{template.Id} would double up on registration marks");
        }
    }

    [Fact]
    public void Round_trips_through_json()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("template.json");

        var original = CoverTemplateCatalog.SgcDvdCase();
        original.Save(path);

        var reloaded = CoverTemplate.Load(path);

        Assert.Equal(original.Id, reloaded.Id);
        Assert.Equal(original.Media, reloaded.Media);
        Assert.Equal(original.Trim, reloaded.Trim);
        Assert.Equal(original.Page, reloaded.Page);
        Assert.Equal(original.Slots.Count, reloaded.Slots.Count);
        Assert.Equal(original.Slots[0].Bounds, reloaded.Slots[0].Bounds);
    }

    [Fact]
    public void Unit_conversions_are_exact_at_the_reference_points()
    {
        // 1 inch is 72 points and 25.4 mm; anything else means printed covers come out wrong.
        Assert.Equal(72, PrintUnits.MmToPoints(25.4), 6);
        Assert.Equal(25.4, PrintUnits.PointsToMm(72), 6);
        Assert.Equal(300, PrintUnits.MmToPixels(25.4, 300), 6);
        Assert.Equal(300, PrintUnits.EffectiveDpi(300, 25.4), 6);
    }
}

public class CoverRenderingTests
{
    [Fact]
    public void Renders_a_pdf_with_the_right_physical_page_size()
    {
        using var temp = new TempDirectory();
        var template = CoverTemplateCatalog.BlankBluRayCase();
        var project = new CoverProject { TemplateId = template.Id, Title = "Portal 2", AppId = 620 };

        var output = temp.Combine("cover.pdf");
        var result = new CoverRenderer().Render(project, template, output);

        Assert.True(File.Exists(output));

        var text = File.ReadAllText(output, Encoding.Latin1);
        Assert.StartsWith("%PDF-1.7", text, StringComparison.Ordinal);
        Assert.Contains("%%EOF", text, StringComparison.Ordinal);

        // MediaBox is in points; 279 mm x 180 mm for a Blu-ray wrap plus 3 mm bleed each side.
        var expectedWidth = PrintUnits.MmToPoints(template.Page.Width);
        var expectedHeight = PrintUnits.MmToPoints(template.Page.Height);
        Assert.Contains(
            $"/MediaBox [0 0 {expectedWidth:0.####} {expectedHeight:0.####}]",
            text,
            StringComparison.Ordinal);

        Assert.Equal(template.Page, result.PageSize);
    }

    [Fact]
    public void Embeds_a_png_and_warns_when_it_is_too_low_resolution()
    {
        using var temp = new TempDirectory();

        // 200x300 across a 130 mm panel is far under 300 DPI.
        var art = temp.Combine("cover.png");
        File.WriteAllBytes(art, MakePng(200, 300));

        var template = CoverTemplateCatalog.SgcBluRayCase();
        var project = new CoverProject { TemplateId = template.Id, Title = "Test" };
        project.Artwork["front"] = art;

        var output = temp.Combine("cover.pdf");
        var result = new CoverRenderer().Render(project, template, output);

        var bytes = File.ReadAllBytes(output);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Subtype /Image", text, StringComparison.Ordinal);
        Assert.Contains("/Width 200", text, StringComparison.Ordinal);
        Assert.Contains("/Height 300", text, StringComparison.Ordinal);

        // A PNG without alpha rides in as its own zlib stream via the PDF predictor.
        Assert.Contains("/Predictor 15", text, StringComparison.Ordinal);

        Assert.Contains(result.Warnings, w => w.Contains("DPI", StringComparison.Ordinal));
    }

    [Fact]
    public void Embeds_a_jpeg_without_re_encoding_it()
    {
        using var temp = new TempDirectory();
        var art = temp.Combine("hero.jpg");
        var jpeg = MakeJpegHeader(1920, 620);
        File.WriteAllBytes(art, jpeg);

        var template = CoverTemplateCatalog.SgcBluRayCase();
        var project = new CoverProject { TemplateId = template.Id, Title = "Test" };
        project.Artwork["back"] = art;

        var output = temp.Combine("cover.pdf");
        new CoverRenderer().Render(project, template, output);

        var text = File.ReadAllText(output, Encoding.Latin1);

        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
        Assert.Contains("/Width 1920", text, StringComparison.Ordinal);
        Assert.Contains("/Height 620", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Substitutes_the_title_into_template_text()
    {
        using var temp = new TempDirectory();
        var template = CoverTemplateCatalog.SgcDvdCase();
        var project = new CoverProject { TemplateId = template.Id, Title = "Portal 2" };

        var output = temp.Combine("cover.pdf");
        new CoverRenderer().Render(project, template, output);

        var text = File.ReadAllText(output, Encoding.Latin1);
        Assert.Contains("(Portal 2) Tj", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_artwork_is_a_warning_not_a_failure()
    {
        using var temp = new TempDirectory();
        var template = CoverTemplateCatalog.SgcDiscLabel();
        var project = new CoverProject { TemplateId = template.Id, Title = "Test" };
        project.Artwork["label"] = "/definitely/not/here.png";

        var result = new CoverRenderer().Render(project, template, temp.Combine("label.pdf"));

        Assert.Contains(result.Warnings, w => w.Contains("could not be found", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public void Cover_fit_fills_the_slot_and_contain_fits_inside_it()
    {
        var image = MakeRasterImage(200, 100); // 2:1
        var destination = new RectMm(0, 0, 100, 100); // 1:1

        var cover = PdfPage.ComputePlacement(image, destination, SlotFit.Cover);
        Assert.True(cover.Width >= destination.Width - 0.001);
        Assert.True(cover.Height >= destination.Height - 0.001);

        var contain = PdfPage.ComputePlacement(image, destination, SlotFit.Contain);
        Assert.True(contain.Width <= destination.Width + 0.001);
        Assert.True(contain.Height <= destination.Height + 0.001);

        // Either way the aspect ratio is preserved.
        Assert.Equal(2.0, cover.Width / cover.Height, 3);
        Assert.Equal(2.0, contain.Width / contain.Height, 3);
    }

    [Fact]
    public void Inspect_reports_empty_slots_before_anything_is_printed()
    {
        var template = CoverTemplateCatalog.SgcBluRayCase();
        var project = new CoverProject { TemplateId = template.Id, Title = "Test" };

        var warnings = CoverRenderer.Inspect(project, template);

        Assert.Equal(template.Slots.Count, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("no artwork", w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds a real, valid greyscale PNG so the reader is exercised, not stubbed.</summary>
    internal static byte[] MakePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 0;  // greyscale
        WriteChunk(output, "IHDR", header);

        // One filter byte plus one sample per pixel, per row.
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                raw[(y * (width + 1)) + 1 + x] = (byte)((x + y) % 256);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes.Concat(data).ToArray());
        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>A JPEG with a real SOF0 header; enough for dimension reading and embedding.</summary>
    internal static byte[] MakeJpegHeader(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI

        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)(width & 0xFF));
        bytes.Add(3); // components
        for (var i = 0; i < 3; i++)
        {
            bytes.AddRange(new byte[] { (byte)(i + 1), 0x11, 0x00 });
        }

        bytes.AddRange(new byte[] { 0xFF, 0xD9 }); // EOI
        return bytes.ToArray();
    }

    private static RasterImage MakeRasterImage(int width, int height)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("scratch.png");
        File.WriteAllBytes(path, MakePng(width, height));
        return RasterImage.Load(path);
    }
}

public class TemplateImportTests
{
    [Fact]
    public void Imports_downloaded_artwork_as_an_overlay_template()
    {
        using var temp = new TempDirectory();
        var download = temp.Combine("steam-bluray-template.png");
        File.WriteAllBytes(download, CoverRenderingTests.MakePng(3300, 2100));

        var library = temp.CreateSubdirectory("templates");
        var result = TemplatePackImporter.ImportArtwork(
            download, library, media: null, attribution: "steamgamecovers.com", family: "Steam");

        Assert.Equal(CoverMedia.BluRayCase, result.Template.Media);
        Assert.Equal("Steam", result.Template.Family);
        Assert.Equal("steamgamecovers.com", result.Template.Source);
        Assert.NotNull(result.Template.OverlayPath);
        Assert.True(File.Exists(Path.Combine(result.Directory, result.Template.OverlayPath!)));
        Assert.True(File.Exists(Path.Combine(result.Directory, CoverTemplate.FileName)));

        // The import should say what it inferred, so a wrong guess is visible.
        Assert.Contains(result.Notes, n => n.Contains("DPI", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("official-dvd-case", CoverMedia.DvdCase)]
    [InlineData("Steam Blu-ray Case", CoverMedia.BluRayCase)]
    [InlineData("classic_jewel_case", CoverMedia.JewelCase)]
    [InlineData("modern-disc-label", CoverMedia.DiscLabel)]
    public void Infers_the_case_type_from_a_downloaded_file_name(string fileName, CoverMedia expected)
    {
        Assert.Equal(expected, TemplatePackImporter.InferMedia(fileName));
    }

    [Fact]
    public void Explains_why_a_psd_cannot_be_used()
    {
        using var temp = new TempDirectory();
        var psd = temp.WriteFile("template.psd", "not really a psd");

        var exception = Assert.Throws<NotSupportedException>(
            () => TemplatePackImporter.ImportArtwork(psd, temp.CreateSubdirectory("templates")));

        Assert.Contains("PNG or JPG", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Imports_a_whole_pack_folder()
    {
        using var temp = new TempDirectory();
        var pack = temp.CreateSubdirectory("pack");

        foreach (var name in new[] { "dvd-case.png", "bluray-case.png", "disc-label.png" })
        {
            File.WriteAllBytes(Path.Combine(pack, name), CoverRenderingTests.MakePng(600, 400));
        }

        File.WriteAllText(Path.Combine(pack, "readme.txt"), "credits");

        var results = TemplatePackImporter.ImportPack(pack, temp.CreateSubdirectory("templates"), "Some Author");

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("Some Author", r.Template.Author));
        Assert.Contains(results, r => r.Template.Media == CoverMedia.DiscLabel);
    }

    [Fact]
    public void An_imported_layout_replaces_its_built_in_entry_and_gains_artwork()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateSubdirectory("templates");
        var download = temp.Combine("SGC_AMARY_CASE_2018.png");
        File.WriteAllBytes(download, CoverRenderingTests.MakePng(3300, 2550));

        TemplatePackImporter.ImportArtwork(download, library, attribution: "steamgamecovers.com");

        var discovered = CoverTemplateCatalog.Discover(library);

        // The picker should not offer the same layout twice once its design is available.
        Assert.Equal(CoverTemplateCatalog.BuiltIn.Count, discovered.Count);

        var dvd = discovered.Single(x => x.Id == "sgc-dvd");
        Assert.NotNull(dvd.OverlayPath);
        Assert.NotNull(dvd.ResolveAsset(dvd.OverlayPath));
        Assert.Equal("steamgamecovers.com", dvd.Source);
    }

    [Fact]
    public void An_unrecognised_design_is_imported_alongside_the_built_ins()
    {
        using var temp = new TempDirectory();
        var library = temp.CreateSubdirectory("templates");
        var download = temp.Combine("somebodys-custom-dvd-design.png");
        File.WriteAllBytes(download, CoverRenderingTests.MakePng(3300, 2550));

        TemplatePackImporter.ImportArtwork(download, library, family: "Custom pack");

        var discovered = CoverTemplateCatalog.Discover(library);

        Assert.True(discovered.Count > CoverTemplateCatalog.BuiltIn.Count);
        Assert.Contains(discovered, x => x.Family == "Custom pack");
    }

    [Fact]
    public void A_pack_with_nothing_usable_says_so()
    {
        using var temp = new TempDirectory();
        var pack = temp.CreateSubdirectory("pack");
        File.WriteAllText(Path.Combine(pack, "template.psd"), "x");

        var exception = Assert.Throws<InvalidOperationException>(
            () => TemplatePackImporter.ImportPack(pack, temp.CreateSubdirectory("templates")));

        Assert.Contains("flatten", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
