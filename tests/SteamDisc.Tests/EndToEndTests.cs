using SteamDisc.Authoring;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;
using SteamDisc.Imaging;
using SteamDisc.Imaging.Iso;
using SteamDisc.Install;

namespace SteamDisc.Tests;

/// <summary>
/// The project plan's v1 success criterion, minus the physical disc: author a game into disc
/// staging folders, then install it into a clean Steam library and check that what lands there
/// is what Steam expects to see.
/// </summary>
/// <remarks>
/// This is the M2 + M3 gate. Everything else in the suite tests a component; this tests the
/// claim the project is actually making.
/// </remarks>
public class EndToEndTests
{
    [Fact]
    public async Task Packages_a_game_and_installs_it_into_a_clean_library()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();

        var game = new GameCatalog(authoring.Installation).Find(installed.AppId);
        Assert.NotNull(game);

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game!,
            temp.CreateSubdirectory("staging"),
            OpticalMedium.BluRay)
        {
            Compression = ArchiveCompression.Fast,
        });

        var discRoot = Assert.Single(package.DiscRoots);

        // The disc must be self-describing: manifest, captured ACF, theme, autorun, payload.
        Assert.True(File.Exists(Path.Combine(discRoot, PayloadManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(discRoot, "appmanifest_620.acf")));
        Assert.True(File.Exists(Path.Combine(discRoot, "theme", "theme.json")));
        Assert.True(File.Exists(Path.Combine(discRoot, "autorun.inf")));
        Assert.True(File.Exists(Path.Combine(discRoot, "data", "payload.sha256")));

        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));
        var manifest = PayloadManifest.Load(Path.Combine(discRoot, PayloadManifest.FileName));

        var processRunner = new FakeProcessRunner();
        var host = new ScriptedInstallHost();
        var engine = new InstallEngine(host, processRunner: processRunner);

        var library = target.Installation.GetLibraries()[0];
        var result = await engine.InstallAsync(
            new InstallRequest(manifest, discRoot, library, target.Installation)
            {
                RunPrerequisites = false,
                VerifyVolumesUpFront = true,
            });

        Assert.True(result.Succeeded, result.Error?.ToString());

        // Every game file must have arrived intact.
        ArchiveTests.AssertDirectoriesMatch(
            installed.InstallPath,
            Path.Combine(library.CommonPath, "Portal 2"));

        // And Steam must find a manifest that describes it correctly.
        var written = AppManifest.Load(library.ManifestPath(620));
        Assert.Equal(620u, written.AppId);
        Assert.Equal("Portal 2", written.InstallDir);
        Assert.Equal(7415828, written.BuildId);
        Assert.Equal(written.BuildId, written.TargetBuildId);
        Assert.Equal(AppStateFlags.FullyInstalled, written.StateFlags);
        Assert.Single(written.InstalledDepots);
        Assert.Equal("5106065882179239892", written.InstalledDepots[0].ManifestId);

        // Ownership must be rewritten to the installing account, not the authoring one.
        Assert.Equal(FakeSteam.LocalSteamId, written.LastOwner);

        Assert.Empty(AppManifestTransplant.Audit(written));

        // And the game should have been handed to Steam to launch.
        Assert.True(processRunner.Launched("steam://run/620"));
    }

    [Fact]
    public async Task Spans_several_discs_and_prompts_for_each_swap()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        // A tiny medium forces the payload across several discs without needing a real one.
        var tinyMedium = new OpticalMedium("tiny", "Tiny test medium", DiscSpanPlanner.PerDiscOverheadBytes + (200 * 1024));

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game,
            temp.CreateSubdirectory("staging"),
            tinyMedium)
        {
            Compression = ArchiveCompression.Store,
            VolumeSize = 64 * 1024,
        });

        Assert.True(package.Plan.DiscCount > 1, $"expected a multi-disc set, got {package.Plan.DiscCount}");
        Assert.Equal(package.Plan.DiscCount, package.DiscRoots.Count);

        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));
        var manifest = PayloadManifest.Load(Path.Combine(package.DiscRoots[0], PayloadManifest.FileName));

        var host = new ScriptedInstallHost();
        for (var disc = 2; disc <= package.Plan.DiscCount; disc++)
        {
            host.EnqueueDisc(package.DiscRoots[disc - 1]);
        }

        var engine = new InstallEngine(host, processRunner: new FakeProcessRunner());
        var library = target.Installation.GetLibraries()[0];

        var result = await engine.InstallAsync(
            new InstallRequest(manifest, package.DiscRoots[0], library, target.Installation)
            {
                RunPrerequisites = false,
            });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(package.Plan.DiscCount - 1, host.DiscRequests.Count);
        Assert.Equal(2, host.DiscRequests[0].DiscNumber);

        ArchiveTests.AssertDirectoriesMatch(
            installed.InstallPath,
            Path.Combine(library.CommonPath, "Portal 2"));
    }

    [Fact]
    public async Task Refuses_a_disc_from_a_different_set()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var tinyMedium = new OpticalMedium("tiny", "Tiny", DiscSpanPlanner.PerDiscOverheadBytes + (200 * 1024));

        var first = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging-a"), tinyMedium)
        {
            Compression = ArchiveCompression.Store,
            VolumeSize = 64 * 1024,
        });

        // A second build of the same game is a different set: same title, different setId.
        var second = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging-b"), tinyMedium)
        {
            Compression = ArchiveCompression.Store,
            VolumeSize = 64 * 1024,
        });

        Assert.True(first.Plan.DiscCount > 1);

        var manifest = PayloadManifest.Load(Path.Combine(first.DiscRoots[0], PayloadManifest.FileName));

        var host = new ScriptedInstallHost();
        host.EnqueueDisc(second.DiscRoots[1]);  // wrong set
        host.EnqueueDisc(first.DiscRoots[1]);   // correct one

        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));
        var engine = new InstallEngine(host, processRunner: new FakeProcessRunner());

        var result = await engine.InstallAsync(
            new InstallRequest(
                manifest,
                first.DiscRoots[0],
                target.Installation.GetLibraries()[0],
                target.Installation)
            {
                RunPrerequisites = false,
            });

        // The mismatched disc must be rejected and re-requested, not read.
        Assert.True(host.DiscRequests.Count >= 2);
        Assert.Contains(host.DiscRequests, r => r.Reason?.Contains("different set", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEqual(InstallOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Preflight_refuses_when_the_target_is_not_a_steam_library()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging"), OpticalMedium.BluRay));

        var manifest = PayloadManifest.Load(Path.Combine(package.DiscRoots[0], PayloadManifest.FileName));
        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));

        var engine = new InstallEngine(new ScriptedInstallHost(), processRunner: new FakeProcessRunner());
        var preflight = engine.Preflight(new InstallRequest(
            manifest,
            package.DiscRoots[0],
            new SteamLibrary(temp.CreateSubdirectory("not-a-library")),
            target.Installation));

        Assert.False(preflight.CanProceed);
        Assert.Contains(preflight.Errors, e => e.Contains("Steam library", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_missing_volume_fails_before_anything_is_registered()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging"), OpticalMedium.BluRay));

        var discRoot = package.DiscRoots[0];
        var manifest = PayloadManifest.Load(Path.Combine(discRoot, PayloadManifest.FileName));

        File.Delete(Path.Combine(discRoot, manifest.Archive.Volumes[0].Path.Replace('/', Path.DirectorySeparatorChar)));

        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));
        var library = target.Installation.GetLibraries()[0];

        var engine = new InstallEngine(new ScriptedInstallHost(), processRunner: new FakeProcessRunner());
        var result = await engine.InstallAsync(
            new InstallRequest(manifest, discRoot, library, target.Installation) { RunPrerequisites = false });

        Assert.Equal(InstallOutcome.Failed, result.Outcome);

        // Nothing may be registered with Steam on a failed install, or the client will try to
        // "repair" a game that was never written by downloading all of it.
        Assert.False(File.Exists(library.ManifestPath(620)));
    }

    [Fact]
    public async Task Warns_when_the_disc_carries_no_captured_manifest()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging"), OpticalMedium.BluRay));

        var discRoot = package.DiscRoots[0];
        File.Delete(Path.Combine(discRoot, "appmanifest_620.acf"));

        var manifest = PayloadManifest.Load(Path.Combine(discRoot, PayloadManifest.FileName));
        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));

        var engine = new InstallEngine(new ScriptedInstallHost(), processRunner: new FakeProcessRunner());
        var result = await engine.InstallAsync(new InstallRequest(
            manifest, discRoot, target.Installation.GetLibraries()[0], target.Installation)
        {
            RunPrerequisites = false,
        });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Contains(result.Warnings, w => w.Contains("re-download", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_mode_asks_steam_to_verify_instead_of_launching()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging"), OpticalMedium.BluRay));

        var discRoot = package.DiscRoots[0];
        var manifest = PayloadManifest.Load(Path.Combine(discRoot, PayloadManifest.FileName));
        var target = FakeSteam.Create(temp.CreateSubdirectory("target-steam"));

        var processRunner = new FakeProcessRunner();
        var engine = new InstallEngine(new ScriptedInstallHost(), processRunner: processRunner);
        var library = target.Installation.GetLibraries()[0];

        var result = await engine.InstallAsync(new InstallRequest(
            manifest, discRoot, library, target.Installation)
        {
            RunPrerequisites = false,
            ValidateAfterInstall = true,
        });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(processRunner.Launched("steam://validate/620"));
        Assert.False(processRunner.Launched("steam://run/620"));

        // Path C flags the app for verification rather than claiming it is known-good.
        var written = AppManifest.Load(library.ManifestPath(620));
        Assert.True(written.StateFlags.HasFlag(AppStateFlags.UpdateRequired));
    }

    [Fact]
    public async Task Packaged_disc_survives_being_turned_into_an_iso_and_read_back()
    {
        using var temp = new TempDirectory();

        var authoring = FakeSteam.Create(temp.CreateSubdirectory("source-steam"));
        var installed = authoring.InstallGame();
        var game = new GameCatalog(authoring.Installation).Find(installed.AppId)!;

        var package = await new PackageBuilder().BuildAsync(new PackageRequest(
            game, temp.CreateSubdirectory("staging"), OpticalMedium.BluRay));

        var iso = temp.Combine("disc.iso");
        await new Iso9660Builder().BuildAsync(
            package.DiscRoots[0], iso, new IsoBuildOptions(game.Name));

        using var reader = new IsoReader(iso);
        var files = reader.ReadAllFiles();

        Assert.Contains("payload.json", files.Keys);
        Assert.Contains("appmanifest_620.acf", files.Keys);
        Assert.Contains("autorun.inf", files.Keys);
        Assert.Contains(files.Keys, k => k.StartsWith("data/payload.sdz.", StringComparison.Ordinal));
        Assert.Contains("theme/theme.json", files.Keys);

        // The payload volume on the image must be identical to the one on disk, or an install
        // from the burned disc reads different bytes than the one from the staging folder.
        var volumeKey = files.Keys.First(k => k.StartsWith("data/payload.sdz.", StringComparison.Ordinal));
        var onDisk = await File.ReadAllBytesAsync(
            Path.Combine(package.DiscRoots[0], volumeKey.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(onDisk, files[volumeKey]);
    }
}
