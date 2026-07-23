using SteamDisc.Core.Steam;

namespace SteamDisc.Tests;

public class AppManifestTests
{
    private const string SourceManifest = """
        "AppState"
        {
        	"appid"		"620"
        	"Universe"		"1"
        	"LauncherPath"		"D:\\AuthoringMachine\\Steam\\steam.exe"
        	"name"		"Portal 2"
        	"StateFlags"		"4"
        	"installdir"		"Portal 2"
        	"LastUpdated"		"1690000000"
        	"SizeOnDisk"		"14041947036"
        	"StagingSize"		"12345"
        	"buildid"		"7415828"
        	"LastOwner"		"76561198000000000"
        	"UpdateResult"		"2"
        	"BytesToDownload"		"999"
        	"BytesDownloaded"		"111"
        	"BytesToStage"		"222"
        	"BytesStaged"		"333"
        	"TargetBuildID"		"7500000"
        	"AutoUpdateBehavior"		"0"
        	"ScheduledAutoUpdate"		"1700000000"
        	"InstalledDepots"
        	{
        		"621"
        		{
        			"manifest"		"5106065882179239892"
        			"size"		"10334085297"
        		}
        	}
        }
        """;

    [Fact]
    public void Reads_typed_fields()
    {
        var manifest = AppManifest.Parse(SourceManifest);

        Assert.Equal(620u, manifest.AppId);
        Assert.Equal("Portal 2", manifest.Name);
        Assert.Equal("Portal 2", manifest.InstallDir);
        Assert.Equal(7415828, manifest.BuildId);
        Assert.Equal(14041947036, manifest.SizeOnDisk);
        Assert.Equal(76561198000000000UL, manifest.LastOwner);
        Assert.Equal(AppStateFlags.FullyInstalled, manifest.StateFlags);

        var depot = Assert.Single(manifest.InstalledDepots);
        Assert.Equal(621u, depot.DepotId);
        Assert.Equal("5106065882179239892", depot.ManifestId);
    }

    [Fact]
    public void Transplant_rewrites_machine_specific_fields()
    {
        var source = AppManifest.Parse(SourceManifest);

        var prepared = AppManifestTransplant.Prepare(source, new TransplantOptions(
            LocalSteamId: 76561198999999999,
            LauncherPath: @"C:\Program Files (x86)\Steam\steam.exe"));

        Assert.Equal(76561198999999999UL, prepared.LastOwner);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", prepared.LauncherPath);
        Assert.Equal(AppStateFlags.FullyInstalled, prepared.StateFlags);

        // Every transfer counter must be zeroed, or the client resumes a download that is
        // not happening.
        Assert.Equal(0, prepared.Root.GetInt64("BytesToDownload"));
        Assert.Equal(0, prepared.Root.GetInt64("BytesDownloaded"));
        Assert.Equal(0, prepared.Root.GetInt64("BytesToStage"));
        Assert.Equal(0, prepared.Root.GetInt64("BytesStaged"));
        Assert.Equal(0, prepared.Root.GetInt64("StagingSize"));
        Assert.Equal(0, prepared.Root.GetInt64("UpdateResult"));
        Assert.Equal(0, prepared.Root.GetInt64("ScheduledAutoUpdate"));
    }

    [Fact]
    public void Transplant_leaves_content_identifying_fields_alone()
    {
        var source = AppManifest.Parse(SourceManifest);
        var prepared = AppManifestTransplant.Prepare(source, new TransplantOptions(LocalSteamId: 1));

        // These are what stop Steam re-downloading; touching any of them defeats the point.
        Assert.Equal(7415828, prepared.BuildId);
        Assert.Equal("Portal 2", prepared.InstallDir);
        Assert.Equal("5106065882179239892", prepared.InstalledDepots[0].ManifestId);
        Assert.Equal(10334085297, prepared.InstalledDepots[0].Size);
    }

    [Fact]
    public void Transplant_aligns_target_build_with_build_id()
    {
        var source = AppManifest.Parse(SourceManifest);
        Assert.NotEqual(source.BuildId, source.TargetBuildId);

        var prepared = AppManifestTransplant.Prepare(source, new TransplantOptions());

        Assert.Equal(prepared.BuildId, prepared.TargetBuildId);
    }

    [Fact]
    public void Transplant_does_not_mutate_the_source()
    {
        var source = AppManifest.Parse(SourceManifest);
        var before = source.ToVdf();

        AppManifestTransplant.Prepare(source, new TransplantOptions(LocalSteamId: 42, RequestValidation: true));

        Assert.Equal(before, source.ToVdf());
    }

    [Fact]
    public void Transplant_can_request_validation()
    {
        var prepared = AppManifestTransplant.Prepare(
            AppManifest.Parse(SourceManifest),
            new TransplantOptions(RequestValidation: true));

        Assert.True(prepared.StateFlags.HasFlag(AppStateFlags.FullyInstalled));
        Assert.True(prepared.StateFlags.HasFlag(AppStateFlags.UpdateRequired));
    }

    [Fact]
    public void Transplant_drops_a_foreign_launcher_path_when_none_is_supplied()
    {
        var prepared = AppManifestTransplant.Prepare(
            AppManifest.Parse(SourceManifest),
            new TransplantOptions(LauncherPath: null));

        Assert.Null(prepared.LauncherPath);
    }

    [Fact]
    public void Audit_is_clean_for_a_healthy_manifest()
    {
        var prepared = AppManifestTransplant.Prepare(
            AppManifest.Parse(SourceManifest),
            new TransplantOptions(LocalSteamId: 1));

        Assert.Empty(AppManifestTransplant.Audit(prepared));
    }

    [Fact]
    public void Audit_flags_a_missing_build_id()
    {
        var manifest = AppManifest.Parse(SourceManifest);
        manifest.BuildId = 0;
        manifest.TargetBuildId = 0;

        var problems = AppManifestTransplant.Audit(manifest);

        Assert.Contains(problems, p => p.Contains("buildid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Audit_flags_missing_depots()
    {
        var manifest = AppManifest.Parse(SourceManifest);
        manifest.Root.Remove("InstalledDepots");

        var problems = AppManifestTransplant.Audit(manifest);

        Assert.Contains(problems, p => p.Contains("depot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Audit_flags_a_target_build_mismatch()
    {
        var manifest = AppManifest.Parse(SourceManifest);

        var problems = AppManifestTransplant.Audit(manifest);

        Assert.Contains(problems, p => p.Contains("TargetBuildID", StringComparison.Ordinal));
    }

    [Fact]
    public void State_flag_values_decompose_as_documented()
    {
        // The community write-ups quote these as magic numbers; they are ordinary flag unions,
        // and the code should agree with the numbers people will see in real manifests.
        Assert.Equal(4, (int)AppStateFlagPresets.Installed);
        Assert.Equal(6, (int)AppStateFlagPresets.InstalledNeedsValidation);
        Assert.Equal(1026, (int)AppStateFlagPresets.UpdateQueued);
    }

    [Fact]
    public void File_name_matches_steams_convention()
    {
        Assert.Equal("appmanifest_620.acf", AppManifest.FileNameFor(620));
    }

    [Fact]
    public void Saves_and_reloads_from_disk()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "appmanifest_620.acf");

        var manifest = AppManifest.Parse(SourceManifest);
        manifest.Save(path);

        var reloaded = AppManifest.Load(path);

        Assert.Equal(manifest.ToVdf(), reloaded.ToVdf());
    }
}
