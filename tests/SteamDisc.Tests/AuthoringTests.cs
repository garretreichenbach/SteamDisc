using SteamDisc.Authoring;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;
using SteamDisc.Imaging;
using SteamDisc.Install;

namespace SteamDisc.Tests;

public class DiscSpanPlannerTests
{
    private static VolumeDescriptor Volume(int index, long size)
        => new() { Index = index, Path = $"data/payload.sdz.{index:000}", Size = size };

    [Fact]
    public void A_payload_that_fits_stays_on_one_disc()
    {
        var volumes = Enumerable.Range(1, 4).Select(i => Volume(i, 1_000_000_000)).ToList();

        var plan = DiscSpanPlanner.Assign(volumes, OpticalMedium.BluRay);

        Assert.Equal(1, plan.DiscCount);
        Assert.All(plan.Volumes, v => Assert.Equal(1, v.Disc));
    }

    [Fact]
    public void Volumes_spill_onto_further_discs_in_order()
    {
        // 30 GB across 2 GB volumes needs two BD-Rs.
        var volumes = Enumerable.Range(1, 15).Select(i => Volume(i, 2_000_000_000)).ToList();

        var plan = DiscSpanPlanner.Assign(volumes, OpticalMedium.BluRay);

        Assert.True(plan.DiscCount >= 2);
        Assert.Equal(1, plan.Volumes[0].Disc);

        // Disc assignment must be monotonic, or the runtime would ask for disc 2, then 1, then 2.
        for (var i = 1; i < plan.Volumes.Count; i++)
        {
            Assert.True(plan.Volumes[i].Disc >= plan.Volumes[i - 1].Disc);
        }

        for (var disc = 1; disc <= plan.DiscCount; disc++)
        {
            Assert.True(
                plan.BytesOnDisc(disc) <= plan.UsableBytesPerDisc,
                $"disc {disc} holds {plan.BytesOnDisc(disc)} bytes, over the {plan.UsableBytesPerDisc} limit");
        }
    }

    [Fact]
    public void A_volume_too_large_for_the_medium_is_an_error_not_a_silent_overflow()
    {
        var volumes = new List<VolumeDescriptor> { Volume(1, 10_000_000_000) };

        var exception = Assert.Throws<InvalidOperationException>(
            () => DiscSpanPlanner.Assign(volumes, OpticalMedium.Dvd));

        Assert.Contains("does not fit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chosen_volume_sizes_stay_under_the_iso_file_size_limit()
    {
        foreach (var medium in OpticalMedium.All)
        {
            var size = DiscSpanPlanner.ChooseVolumeSize(medium);

            Assert.True(size > 0);
            Assert.True(
                size < 4L * 1024 * 1024 * 1024,
                $"{medium.Id} chose a {size} byte volume, which ISO 9660 cannot record");
        }
    }

    [Fact]
    public void Disc_count_can_be_estimated_before_packing()
    {
        var usable = OpticalMedium.BluRay.UsableForPayload(DiscSpanPlanner.PerDiscOverheadBytes);

        Assert.Equal(1, DiscSpanPlanner.EstimateDiscCount(usable - 1, OpticalMedium.BluRay));
        Assert.Equal(2, DiscSpanPlanner.EstimateDiscCount(usable + 1, OpticalMedium.BluRay));
    }
}

public class PrerequisiteScannerTests
{
    [Fact]
    public void Finds_redistributables_and_gives_them_silent_switches()
    {
        using var temp = new TempDirectory();
        var game = temp.CreateSubdirectory("game");

        temp.WriteFile("game/_CommonRedist/vcredist/2019/VC_redist.x64.exe", "x");
        temp.WriteFile("game/_CommonRedist/DirectX/Jun2010/DXSETUP.exe", "x");
        temp.WriteFile("game/game.exe", "x");

        var found = PrerequisiteScanner.Scan(game);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.Path.EndsWith("VC_redist.x64.exe", StringComparison.Ordinal));
        Assert.All(found, p => Assert.False(string.IsNullOrWhiteSpace(p.Args)));

        var vcredist = found.First(p => p.Path.Contains("vcredist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/quiet", vcredist.Args, StringComparison.Ordinal);
        Assert.Contains("x64", vcredist.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_game_with_no_redist_folder_yields_nothing()
    {
        using var temp = new TempDirectory();
        var game = temp.CreateSubdirectory("game");
        temp.WriteFile("game/game.exe", "x");

        Assert.Empty(PrerequisiteScanner.Scan(game));
    }

    [Fact]
    public void Argument_splitting_honours_quotes()
    {
        var arguments = PrerequisiteRunner.SplitArguments("/quiet /norestart /log \"C:\\Program Files\\log.txt\"");

        Assert.Equal(new[] { "/quiet", "/norestart", "/log", @"C:\Program Files\log.txt" }, arguments);
    }

    [Fact]
    public void Empty_arguments_split_to_nothing()
    {
        Assert.Empty(PrerequisiteRunner.SplitArguments(null));
        Assert.Empty(PrerequisiteRunner.SplitArguments("   "));
    }
}

public class DrmDetectorTests
{
    [Fact]
    public void Flags_a_third_party_launcher()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var app = steam.InstallGame(appId: 900, name: "Ubi Game", installDir: "Ubi Game");

        File.WriteAllText(Path.Combine(app.InstallPath, "UbisoftGameLauncherInstaller.exe"), "x");

        var advisories = DrmDetector.Inspect(new InstalledApp(app.Manifest, app.Library, app.ManifestPath));

        Assert.Contains(advisories, a => a.Code == "drm.ubisoft" && a.Severity == AdvisorySeverity.Warning);
    }

    [Fact]
    public void Flags_anti_cheat_that_installs_a_service()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var app = steam.InstallGame(appId: 901, name: "MP Game", installDir: "MP Game");

        Directory.CreateDirectory(Path.Combine(app.InstallPath, "EasyAntiCheat"));
        File.WriteAllText(Path.Combine(app.InstallPath, "EasyAntiCheat", "EasyAntiCheat_Setup.exe"), "x");

        var advisories = DrmDetector.Inspect(new InstalledApp(app.Manifest, app.Library, app.ManifestPath));

        Assert.Contains(advisories, a => a.Code == "anticheat.easy");
    }

    [Fact]
    public void A_plain_old_single_player_game_gets_no_warnings()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var app = steam.InstallGame();

        var advisories = DrmDetector.Inspect(new InstalledApp(app.Manifest, app.Library, app.ManifestPath));

        Assert.DoesNotContain(advisories, a => a.Severity == AdvisorySeverity.Warning);
    }

    [Fact]
    public void Flags_a_manifest_that_would_make_steam_re_download()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        var app = steam.InstallGame(appId: 902, name: "No Build", installDir: "No Build", buildId: 0);

        var advisories = DrmDetector.Inspect(new InstalledApp(app.Manifest, app.Library, app.ManifestPath));

        Assert.Contains(advisories, a => a.Code == "manifest.nobuildid");
    }
}

public class SteamLayoutTests
{
    [Fact]
    public void Finds_libraries_declared_in_libraryfolders_vdf()
    {
        using var temp = new TempDirectory();
        var extra = temp.CreateSubdirectory("extra-library");
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"), FakeSteam.LocalSteamId, extra);

        var libraries = steam.Installation.GetLibraries();

        Assert.Equal(2, libraries.Count);
        Assert.Contains(libraries, l => l.Path == Path.GetFullPath(extra));
    }

    [Fact]
    public void Reads_the_signed_in_account()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));

        var user = steam.Installation.GetMostRecentUser();

        Assert.NotNull(user);
        Assert.Equal(FakeSteam.LocalSteamId, user!.Value.SteamId64);
        Assert.Equal("Tester", user.Value.DisplayName);
    }

    [Fact]
    public void Enumerates_installed_apps_across_libraries()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        steam.InstallGame(620, "Portal 2", "Portal 2");
        steam.InstallGame(400, "Portal", "Portal");

        var apps = steam.Installation.GetInstalledApps();

        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.AppId == 620 && a.IsFullyInstalled);
    }

    [Fact]
    public void Locator_accepts_an_explicit_override()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));

        var located = SteamLocator.Locate(steam.Root);

        Assert.NotNull(located);
        Assert.Equal(Path.GetFullPath(steam.Root), located!.RootPath);
    }

    [Fact]
    public void Locator_rejects_a_folder_that_is_not_a_steam_root()
    {
        using var temp = new TempDirectory();

        Assert.Null(SteamLocator.Locate(temp.CreateSubdirectory("random")));
    }

    [Fact]
    public void Not_found_exception_lists_where_it_looked()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<SteamNotFoundException>(
            () => SteamLocator.LocateRequired(temp.CreateSubdirectory("random")));

        Assert.Contains("STEAMDISC_STEAM_PATH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Game_catalog_reports_suitability_from_the_last_update()
    {
        using var temp = new TempDirectory();
        var steam = FakeSteam.Create(temp.CreateSubdirectory("steam"));
        steam.InstallGame();

        var game = Assert.Single(new GameCatalog(steam.Installation).List());

        // FakeSteam backdates the last update by two years, which is the ideal case.
        Assert.Equal(GameSuitability.Ideal, game.Suitability);
        Assert.True(game.MeasuredSize > 0);
    }
}
