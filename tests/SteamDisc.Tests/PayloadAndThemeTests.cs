using SteamDisc.Core.Payload;
using SteamDisc.Core.Theming;

namespace SteamDisc.Tests;

public class PayloadManifestTests
{
    private static PayloadManifest Valid() => new()
    {
        Title = "Portal 2",
        AppId = 620,
        InstallDir = "Portal 2",
        BuildId = 7415828,
        SizeOnDisk = 14041947036,
        AppManifestPath = "appmanifest_620.acf",
        Archive = new ArchiveDescriptor
        {
            Format = "sdz",
            Volumes =
            {
                new VolumeDescriptor { Index = 1, Path = "data/payload.sdz.001", Size = 100 },
            },
        },
    };

    [Fact]
    public void Round_trips_through_json()
    {
        var original = Valid();
        var reloaded = PayloadManifest.Parse(original.ToJson());

        Assert.Equal(original.Title, reloaded.Title);
        Assert.Equal(original.AppId, reloaded.AppId);
        Assert.Equal(original.BuildId, reloaded.BuildId);
        Assert.Equal(original.Disc.SetId, reloaded.Disc.SetId);
        Assert.Equal(original.Archive.Volumes[0].Path, reloaded.Archive.Volumes[0].Path);
    }

    [Fact]
    public void A_well_formed_manifest_validates()
    {
        Assert.Empty(Valid().Validate());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("..")]
    public void Rejects_an_install_dir_that_is_not_a_plain_folder_name(string installDir)
    {
        // installDir is joined onto the user's library path, so anything path-like is a way
        // for a hostile disc to write outside it.
        var manifest = Valid();
        manifest.InstallDir = installDir;

        Assert.Contains(manifest.Validate(), p => p.Contains("installDir", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_future_format_version_with_an_actionable_message()
    {
        var manifest = Valid();
        manifest.FormatVersion = PayloadManifest.SupportedFormatVersion + 1;

        var problem = Assert.Single(manifest.Validate());
        Assert.Contains("newer installer", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_out_of_order_volumes()
    {
        var manifest = Valid();
        manifest.Archive.Volumes.Add(new VolumeDescriptor { Index = 5, Path = "data/payload.sdz.005" });

        Assert.Contains(manifest.Validate(), p => p.Contains("numbered from 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_disc_number_beyond_the_set_size()
    {
        var manifest = Valid();
        manifest.Disc.Number = 3;
        manifest.Disc.Of = 2;

        Assert.Contains(manifest.Validate(), p => p.Contains("cannot exceed", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_disc_sets_are_recognised()
    {
        Assert.True(Valid().Disc.IsSingleDisc);
    }
}

public class ThemeTests
{
    [Fact]
    public void Built_in_themes_all_validate_as_definitions()
    {
        foreach (var (id, factory) in BuiltInThemes.All)
        {
            var definition = factory();
            Assert.False(string.IsNullOrWhiteSpace(definition.Name), $"theme '{id}' has no name");
            Assert.True(ThemeColor.TryParse(definition.Colors.Accent, out _), $"theme '{id}' has a bad accent");
        }
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var original = BuiltInThemes.ValveRetail2011();
        var reloaded = ThemeDefinition.Parse(original.ToJson());

        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(original.Layout, reloaded.Layout);
        Assert.Equal(original.Colors.Accent, reloaded.Colors.Accent);
        Assert.Equal(original.GetString(ThemeStrings.InstallButton), reloaded.GetString(ThemeStrings.InstallButton));
    }

    [Fact]
    public void Missing_strings_fall_back_to_the_defaults()
    {
        var definition = new ThemeDefinition();

        Assert.Equal("Install", definition.GetString(ThemeStrings.InstallButton));
        Assert.Equal("Play", definition.GetString(ThemeStrings.PlayButton));
    }

    [Fact]
    public void Placeholders_are_substituted_and_unknown_ones_are_left_visible()
    {
        var values = new Dictionary<string, string> { ["title"] = "Portal 2" };

        Assert.Equal("Installing Portal 2…", ThemeStrings.Format("Installing {title}…", values));

        // A typo should show up as text rather than silently vanishing.
        Assert.Equal("Hello {nope}", ThemeStrings.Format("Hello {nope}", values));
    }

    [Theory]
    [InlineData("#FF6600", 0xFF, 0x66, 0x00, 0xFF)]
    [InlineData("#f60", 0xFF, 0x66, 0x00, 0xFF)]
    [InlineData("#11223344", 0x11, 0x22, 0x33, 0x44)]
    [InlineData("FF6600", 0xFF, 0x66, 0x00, 0xFF)]
    public void Parses_colours(string input, byte r, byte g, byte b, byte a)
    {
        Assert.True(ThemeColor.TryParse(input, out var color));
        Assert.Equal(new ThemeColor(r, g, b, a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("#12345")]
    [InlineData(null)]
    public void Rejects_bad_colours(string? input)
    {
        Assert.False(ThemeColor.TryParse(input, out _));
    }

    [Fact]
    public void A_broken_theme_folder_degrades_to_the_default_rather_than_failing()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateSubdirectory("theme");
        File.WriteAllText(Path.Combine(folder, ThemeDefinition.FileName), "{ this is not json");

        var theme = Theme.LoadOrDefault(folder, out var error);

        Assert.NotNull(error);
        Assert.Equal(Theme.Default.Name, theme.Name);
    }

    [Fact]
    public void Asset_paths_cannot_escape_the_theme_folder()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateSubdirectory("theme");
        File.WriteAllText(Path.Combine(temp.Path, "outside.png"), "x");

        var definition = new ThemeDefinition();
        definition.Assets.Background = "../outside.png";
        File.WriteAllText(Path.Combine(folder, ThemeDefinition.FileName), definition.ToJson());

        var theme = Theme.Load(folder);

        Assert.Null(theme.BackgroundPath);
    }

    [Fact]
    public void Writing_a_theme_folder_copies_art_and_drops_unfilled_slots()
    {
        using var temp = new TempDirectory();
        var artwork = temp.WriteFile("art/hero.png", "not really a png");
        var destination = temp.Combine("out-theme");

        BuiltInThemes.WriteThemeFolder(
            BuiltInThemes.ValveRetail2011(),
            destination,
            new Dictionary<string, string> { ["background"] = artwork });

        var theme = Theme.Load(destination);

        Assert.Equal("background.png", theme.Definition.Assets.Background);
        Assert.NotNull(theme.BackgroundPath);

        // No logo was supplied, so the theme should not claim to have one.
        Assert.Null(theme.Definition.Assets.Logo);
        Assert.Empty(theme.Validate());
    }
}
