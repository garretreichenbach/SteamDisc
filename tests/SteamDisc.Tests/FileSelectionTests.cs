using SteamDisc.Authoring;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Payload;
using SteamDisc.Imaging;

namespace SteamDisc.Tests;

public class FileSelectionTests
{
    private static GameCandidate InstallWithExtras(FakeSteam steam)
    {
        var app = steam.InstallGame();

        // A language pack and an optional extra, alongside the standard game files.
        WriteFile(Path.Combine(app.InstallPath, "german", "strings.txt"), "guten tag");
        WriteFile(Path.Combine(app.InstallPath, "Soundtrack", "01.flac"), "la la");

        return new GameCatalog(steam.Installation).Find(app.AppId)
               ?? throw new InvalidOperationException("game vanished after install");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Build_keeps_game_files_and_flags_language_and_optional_content()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var selection = SelectionManifest.Build(game);

        // The core folder Steam laid down stays in.
        var data = Assert.Single(selection.Entries, e => e.Path == "data");
        Assert.True(data.Include);
        Assert.Null(data.Reason);

        var german = Assert.Single(selection.Entries, e => e.Path == "german");
        Assert.False(german.Include);
        Assert.Contains("language", german.Reason!, StringComparison.OrdinalIgnoreCase);

        var soundtrack = Assert.Single(selection.Entries, e => e.Path == "Soundtrack");
        Assert.False(soundtrack.Include);
        Assert.Contains("optional", soundtrack.Reason!, StringComparison.OrdinalIgnoreCase);

        // _CommonRedist carries prerequisites the runtime executes — it must not be dropped.
        var redist = Assert.Single(selection.Entries, e => e.Path == "_CommonRedist");
        Assert.True(redist.Include);
    }

    [Fact]
    public void Include_all_keeps_everything()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var selection = SelectionManifest.Build(game, applyHeuristics: false);

        Assert.All(selection.Entries, e => Assert.True(e.Include));
        Assert.Empty(selection.DeriveExclusions());
    }

    [Fact]
    public void DeriveExclusions_returns_the_dropped_paths()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var exclusions = SelectionManifest.Build(game).DeriveExclusions();

        Assert.Contains("german", exclusions);
        Assert.Contains("Soundtrack", exclusions);
        Assert.DoesNotContain("data", exclusions);
    }

    [Fact]
    public void Selection_round_trips_through_json()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);
        var original = SelectionManifest.Build(game);

        var reloaded = SelectionManifest.Parse(original.ToJson());

        Assert.Equal(original.AppId, reloaded.AppId);
        Assert.Equal(original.BuildId, reloaded.BuildId);
        Assert.Equal(original.Entries.Count, reloaded.Entries.Count);
        Assert.Equal(original.DeriveExclusions(), reloaded.DeriveExclusions());

        var germanFolder = Assert.Single(reloaded.Entries, e => e.Path == "german");
        Assert.Equal(SelectionEntryKind.Folder, germanFolder.Kind);
    }

    [Fact]
    public void CheckAgainst_rejects_a_selection_for_another_game()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var selection = SelectionManifest.Build(game);
        selection.AppId = 999;

        var problems = selection.CheckAgainst(game);

        Assert.Contains(problems, p => p.Contains("app 999", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckAgainst_warns_when_the_build_moved_on()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var selection = SelectionManifest.Build(game);
        selection.BuildId += 1;

        Assert.Contains(selection.CheckAgainst(game), p => p.Contains("build", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CheckAgainst_flags_content_added_since_authoring()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);
        var selection = SelectionManifest.Build(game);

        // Something new lands in the folder after the selection was written.
        WriteFile(Path.Combine(game.InstallPath, "dlc_new", "extra.pak"), "new");

        Assert.Contains(
            selection.CheckAgainst(game),
            p => p.Contains("New content", StringComparison.OrdinalIgnoreCase) &&
                 p.Contains("dlc_new", StringComparison.Ordinal));
    }

    [Fact]
    public void Heuristics_do_not_flag_ordinary_folders()
    {
        Assert.Null(SelectionHeuristics.Classify("data"));
        Assert.Null(SelectionHeuristics.Classify("bin"));
        Assert.Null(SelectionHeuristics.Classify("english"));
        Assert.NotNull(SelectionHeuristics.Classify("french"));
        Assert.NotNull(SelectionHeuristics.Classify("Soundtrack"));
    }

    [Fact]
    public void Tree_mirrors_the_folder_and_rolls_up_sizes()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var root = SelectionTree.Build(game);

        // Root size is the sum of its children, which is the whole folder.
        Assert.Equal(root.Children.Sum(c => c.Size), root.Size);

        var data = Assert.Single(root.Children, c => c.Name == "data");
        Assert.True(data.IsFolder);
        Assert.True(data.Size > 0);
        Assert.NotEmpty(data.Children);
        Assert.Equal(data.Children.Sum(c => c.Size), data.Size);
    }

    [Fact]
    public void Tree_propagates_a_heuristic_default_down_a_flagged_subtree()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var root = SelectionTree.Build(game);

        var german = Assert.Single(root.Children, c => c.Name == "german");
        Assert.False(german.DefaultInclude);
        Assert.NotNull(german.Reason);
        Assert.All(german.Children, child => Assert.False(child.DefaultInclude));

        // Reasons are a top-level annotation; children carry the decision but not the label.
        Assert.All(german.Children, child => Assert.Null(child.Reason));

        var data = Assert.Single(root.Children, c => c.Name == "data");
        Assert.True(data.DefaultInclude);
        Assert.All(data.Children, child => Assert.True(child.DefaultInclude));
    }

    [Fact]
    public void Tree_paths_are_relative_and_forward_slashed()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var game = InstallWithExtras(steam);

        var root = SelectionTree.Build(game);
        var data = Assert.Single(root.Children, c => c.Name == "data");
        var nested = data.Children.First(c => c.IsFolder);

        Assert.Equal("data", data.Path);
        Assert.StartsWith("data/", nested.Path, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', nested.Path);
    }
}

public class VersionLabelTests
{
    [Fact]
    public void Version_round_trips_through_payload_json()
    {
        var manifest = new PayloadManifest { AppId = 620, Version = "v1.2" };

        var reloaded = PayloadManifest.Parse(manifest.ToJson());

        Assert.Equal("v1.2", reloaded.Version);
    }

    [Fact]
    public void A_manifest_without_a_version_stays_null()
    {
        var reloaded = PayloadManifest.Parse(new PayloadManifest { AppId = 620 }.ToJson());

        Assert.Null(reloaded.Version);
    }

    [Fact]
    public async Task Packaging_writes_the_version_label_onto_the_disc()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var installed = steam.InstallGame();
        var game = new GameCatalog(steam.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game,
            temp.CreateSubdirectory("staging"),
            OpticalMedium.BluRay)
        {
            Compression = ArchiveCompression.Store,
            VersionLabel = "  GOTY Edition  ",
        });

        var manifest = PayloadManifest.Load(Path.Combine(package.DiscRoots[0], PayloadManifest.FileName));

        // Trimmed, and carried through to the disc the runtime reads.
        Assert.Equal("GOTY Edition", manifest.Version);
    }

    [Fact]
    public async Task A_blank_version_label_is_stored_as_null_not_empty()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var installed = steam.InstallGame();
        var game = new GameCatalog(steam.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game,
            temp.CreateSubdirectory("staging"),
            OpticalMedium.BluRay)
        {
            Compression = ArchiveCompression.Store,
            VersionLabel = "   ",
        });

        var manifest = PayloadManifest.Load(Path.Combine(package.DiscRoots[0], PayloadManifest.FileName));

        Assert.Null(manifest.Version);
    }
}
