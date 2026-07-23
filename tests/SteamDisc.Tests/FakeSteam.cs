using SteamDisc.Core.Protocol;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Vdf;
using SteamDisc.Install;

namespace SteamDisc.Tests;

/// <summary>
/// Builds a Steam installation on disk that is real enough for the locator, the library
/// enumerator and the install engine to work against.
/// </summary>
/// <remarks>
/// The alternative would be mocking <c>SteamInstallation</c>, which would test the mock rather
/// than the VDF parsing, path handling and manifest placement that actually carry the risk
/// here. Building the folder layout is cheap and exercises the real code.
/// </remarks>
internal sealed class FakeSteam
{
    private FakeSteam(string root) => Root = root;

    public string Root { get; }

    public string SteamAppsPath => Path.Combine(Root, "steamapps");

    public string ConfigPath => Path.Combine(Root, "config");

    public SteamInstallation Installation => new(Root);

    public const ulong AuthoringSteamId = 76561198000000000;

    public const ulong LocalSteamId = 76561198111111111;

    /// <summary>Creates a Steam root with a signed-in user and no games.</summary>
    public static FakeSteam Create(string root, ulong steamId = LocalSteamId, params string[] extraLibraryPaths)
    {
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common"));
        Directory.CreateDirectory(Path.Combine(root, "config"));

        // A stub executable, so ClientExecutablePath resolves the way it would on a real machine.
        File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "steam.exe" : "steam.sh"), string.Empty);

        var libraryFolders = KvNode.Object("libraryfolders");
        var index = 0;

        void AddLibrary(string path)
        {
            var entry = libraryFolders.Add(KvNode.Object(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            entry.SetString("path", path);
            entry.SetString("label", string.Empty);
            entry.GetOrAddObject("apps");
            index++;
        }

        AddLibrary(root);
        foreach (var extra in extraLibraryPaths)
        {
            Directory.CreateDirectory(Path.Combine(extra, "steamapps", "common"));
            AddLibrary(extra);
        }

        VdfTextWriter.WriteFile(Path.Combine(root, "config", "libraryfolders.vdf"), libraryFolders);

        var users = KvNode.Object("users");
        var user = users.Add(KvNode.Object(steamId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        user.SetString("AccountName", "tester");
        user.SetString("PersonaName", "Tester");
        user.SetString("MostRecent", "1");
        user.SetInt64("Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        VdfTextWriter.WriteFile(Path.Combine(root, "config", "loginusers.vdf"), users);

        return new FakeSteam(root);
    }

    /// <summary>Installs a game: a folder of files plus a plausible, complete app manifest.</summary>
    public InstalledApp InstallGame(
        uint appId = 620,
        string name = "Portal 2",
        string installDir = "Portal 2",
        long buildId = 7415828,
        int seed = 1)
    {
        var installPath = Path.Combine(SteamAppsPath, "common", installDir);
        var size = TestData.CreateGameFolder(installPath, seed);

        var manifest = AppManifest.CreateMinimal(appId, name, installDir);
        manifest.BuildId = buildId;
        manifest.TargetBuildId = buildId;
        manifest.SizeOnDisk = size;
        manifest.LastOwner = AuthoringSteamId;
        manifest.LastUpdated = DateTimeOffset.UtcNow.AddYears(-2);
        manifest.LauncherPath = @"D:\SomeOtherMachine\Steam\steam.exe";

        var depots = manifest.Root.GetOrAddObject("InstalledDepots");
        var depot = depots.Add(KvNode.Object((appId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        depot.SetString("manifest", "5106065882179239892");
        depot.SetInt64("size", size);

        var manifestPath = Path.Combine(SteamAppsPath, AppManifest.FileNameFor(appId));
        manifest.Save(manifestPath);

        return new InstalledApp(AppManifest.Load(manifestPath), new SteamLibrary(Root), manifestPath);
    }
}

/// <summary>Records process launches instead of performing them.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<ProcessLaunch> _launches = new();

    public IReadOnlyList<ProcessLaunch> Launches => _launches;

    public int ExitCode { get; set; }

    public void Start(ProcessLaunch launch) => _launches.Add(launch);

    public Task<ProcessResult> RunAsync(ProcessLaunch launch, CancellationToken cancellationToken = default)
    {
        _launches.Add(launch);
        return Task.FromResult(new ProcessResult(ExitCode, string.Empty, string.Empty));
    }

    public bool Launched(string fragment)
        => _launches.Any(l =>
            l.FileName.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            l.Arguments.Any(a => a.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>An install host that answers scripted responses.</summary>
internal sealed class ScriptedInstallHost : IInstallHost
{
    private readonly Queue<string?> _discResponses = new();
    private readonly List<string> _warnings = new();
    private readonly List<DiscRequest> _discRequests = new();

    public ScriptedInstallHost(bool confirmAnswer = false) => ConfirmAnswer = confirmAnswer;

    public bool ConfirmAnswer { get; set; }

    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<DiscRequest> DiscRequests => _discRequests;

    public void EnqueueDisc(string? path) => _discResponses.Enqueue(path);

    public Task<string?> RequestDiscAsync(DiscRequest request, CancellationToken cancellationToken)
    {
        _discRequests.Add(request);
        return Task.FromResult(_discResponses.Count > 0 ? _discResponses.Dequeue() : null);
    }

    public void ReportWarning(string message) => _warnings.Add(message);

    public Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
        => Task.FromResult(ConfirmAnswer);
}
