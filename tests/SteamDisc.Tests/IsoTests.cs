using SteamDisc.Imaging;
using SteamDisc.Imaging.Iso;

namespace SteamDisc.Tests;

public class IsoTests
{
    [Fact]
    public async Task Writes_an_image_a_reader_can_walk()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        TestData.CreateGameFolder(source);

        var iso = temp.Combine("out.iso");
        var result = await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("PORTAL 2"));

        Assert.True(File.Exists(iso));
        Assert.Equal(new FileInfo(iso).Length, result.SizeBytes);

        using var reader = new IsoReader(iso);

        Assert.True(reader.HasJoliet);
        Assert.Equal("PORTAL_2", reader.VolumeLabel);
        Assert.Equal(result.SizeBytes, reader.DeclaredSectors * 2048L);
    }

    [Fact]
    public async Task Preserves_file_contents_byte_for_byte()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        TestData.CreateGameFolder(source);

        var iso = temp.Combine("out.iso");
        await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        using var reader = new IsoReader(iso);
        var files = reader.ReadAllFiles();

        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
            Assert.True(files.ContainsKey(relative), $"'{relative}' is missing from the image.");
            Assert.Equal(File.ReadAllBytes(path), files[relative]);
        }
    }

    [Fact]
    public async Task Joliet_keeps_the_real_file_names()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        await File.WriteAllTextAsync(Path.Combine(source, "payload.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(source, "appmanifest_620.acf"), "x");
        await File.WriteAllTextAsync(Path.Combine(source, "data", "payload.sdz.001"), "y");

        var iso = temp.Combine("out.iso");
        await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        using var reader = new IsoReader(iso);
        var joliet = reader.ReadAllFiles(joliet: true);

        // Lower case, dots and long names all survive in the Joliet tree — which is the tree
        // Windows reads, and the runtime looks for exactly these names.
        Assert.Contains("payload.json", joliet.Keys);
        Assert.Contains("appmanifest_620.acf", joliet.Keys);
        Assert.Contains("data/payload.sdz.001", joliet.Keys);
    }

    [Fact]
    public async Task Primary_tree_uses_conformant_identifiers()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        await File.WriteAllTextAsync(Path.Combine(source, "payload.json"), "{}");

        var iso = temp.Combine("out.iso");
        await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        using var reader = new IsoReader(iso);
        var primary = reader.ReadAllFiles(joliet: false);

        var name = Assert.Single(primary.Keys);
        Assert.All(name, c => Assert.True(
            c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.',
            $"'{c}' is not a legal ISO 9660 identifier character."));
    }

    [Fact]
    public async Task Preserves_empty_directories()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        Directory.CreateDirectory(Path.Combine(source, "theme"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "theme", "theme.json"), "{}");

        var iso = temp.Combine("out.iso");
        await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        using var reader = new IsoReader(iso);
        var directories = reader.ReadAllDirectories();

        Assert.Contains("empty", directories);
        Assert.Contains("theme", directories);
    }

    [Fact]
    public async Task Handles_a_directory_large_enough_to_span_sectors()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");

        // Enough entries that the directory extent needs more than one sector, which is where
        // the "records must not straddle a sector boundary" rule starts to matter.
        for (var i = 0; i < 120; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(source, $"file-with-a-longish-name-{i:000}.bin"),
                $"contents {i}");
        }

        var iso = temp.Combine("out.iso");
        await new Iso9660Builder().BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        using var reader = new IsoReader(iso);
        var files = reader.ReadAllFiles();

        Assert.Equal(120, files.Count);
        Assert.Equal("contents 42", System.Text.Encoding.UTF8.GetString(files["file-with-a-longish-name-042.bin"]));
    }

    [Fact]
    public async Task Estimate_matches_the_image_actually_written()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateSubdirectory("disc");
        TestData.CreateGameFolder(source);

        var builder = new Iso9660Builder();
        var estimate = builder.EstimateSize(source);

        var iso = temp.Combine("out.iso");
        var result = await builder.BuildAsync(source, iso, new IsoBuildOptions("TEST"));

        // The estimate drives the "will this fit on the disc?" check, so it has to be exact,
        // not approximate.
        Assert.Equal(estimate, result.SizeBytes);
    }

    [Fact]
    public void Media_capacities_are_ordered_and_distinct()
    {
        var real = OpticalMedium.All.Where(m => m.CapacityBytes != long.MaxValue).ToList();

        Assert.Equal(real.Count, real.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(OpticalMedium.BluRay.CapacityBytes < OpticalMedium.BluRayDl.CapacityBytes);
        Assert.True(OpticalMedium.BluRayDl.CapacityBytes < OpticalMedium.BluRayXl100.CapacityBytes);
    }

    [Fact]
    public void Autorun_file_names_the_runtime_and_the_title()
    {
        var content = AutorunFile.Build("Portal 2", "Setup.exe");

        Assert.Contains("[autorun]", content, StringComparison.Ordinal);
        Assert.Contains("open=Setup.exe", content, StringComparison.Ordinal);
        Assert.Contains("label=Portal 2", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Autorun_file_strips_characters_the_inf_parser_cannot_take()
    {
        var content = AutorunFile.Build("Weird [Title] = Name;");

        Assert.DoesNotContain("[Title]", content, StringComparison.Ordinal);
        Assert.Contains("label=Weird", content, StringComparison.Ordinal);
    }
}
