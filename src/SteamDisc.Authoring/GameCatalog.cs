using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;

namespace SteamDisc.Authoring;

/// <summary>An installed game, with everything the Builder needs to judge its suitability.</summary>
public sealed class GameCandidate
{
    public GameCandidate(InstalledApp app, long measuredSize, IReadOnlyList<Advisory> advisories)
    {
        App = app;
        MeasuredSize = measuredSize;
        Advisories = advisories;
    }

    public InstalledApp App { get; }

    public uint AppId => App.AppId;

    public string Name => App.Manifest.Name;

    public string InstallDir => App.Manifest.InstallDir;

    public string InstallPath => App.InstallPath;

    /// <summary>Size on disk as measured by walking the folder, not as claimed by the manifest.</summary>
    public long MeasuredSize { get; }

    public long ManifestSize => App.Manifest.SizeOnDisk;

    public DateTimeOffset LastUpdated => App.Manifest.LastUpdated;

    public long BuildId => App.Manifest.BuildId;

    public IReadOnlyList<Advisory> Advisories { get; }

    public bool HasWarnings => Advisories.Any(a => a.Severity == AdvisorySeverity.Warning);

    /// <summary>
    /// A rough "will this disc actually work offline?" verdict, driven by the advisories.
    /// Deliberately advisory rather than a gate: the user owns the game and the call.
    /// </summary>
    public GameSuitability Suitability => HasWarnings
        ? GameSuitability.Caveats
        : DateTimeOffset.UtcNow - LastUpdated > TimeSpan.FromDays(365)
            ? GameSuitability.Ideal
            : GameSuitability.Good;

    public override string ToString() => $"{Name} ({AppId})";
}

public enum GameSuitability
{
    /// <summary>Hasn't been patched in a year — the sweet spot for physical media.</summary>
    Ideal,

    Good,

    /// <summary>Will work, but something will phone home or patch. See the advisories.</summary>
    Caveats,
}

/// <summary>Enumerates installed games and annotates them with authoring advice.</summary>
public sealed class GameCatalog
{
    private readonly SteamInstallation _steam;

    public GameCatalog(SteamInstallation steam) => _steam = steam;

    /// <summary>
    /// Lists installed apps. Measuring folder sizes walks the whole library, so it is opt-in;
    /// the manifest's own figure is close enough for a list view.
    /// </summary>
    public IReadOnlyList<GameCandidate> List(bool measureSizes = false)
    {
        var candidates = new List<GameCandidate>();

        foreach (var app in _steam.GetInstalledApps())
        {
            if (!app.InstallPathExists)
            {
                continue;
            }

            var size = measureSizes ? MeasureDirectory(app.InstallPath) : app.Manifest.SizeOnDisk;
            candidates.Add(new GameCandidate(app, size, DrmDetector.Inspect(app)));
        }

        return candidates;
    }

    public GameCandidate? Find(uint appId)
    {
        foreach (var library in _steam.GetLibraries())
        {
            if (library.FindApp(appId) is { } app && app.InstallPathExists)
            {
                return new GameCandidate(app, MeasureDirectory(app.InstallPath), DrmDetector.Inspect(app));
            }
        }

        return null;
    }

    /// <summary>Finds a game by name, case-insensitively, preferring an exact match.</summary>
    public IReadOnlyList<GameCandidate> Search(string query)
    {
        var all = List();
        var exact = all
            .Where(c => string.Equals(c.Name, query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return exact.Count > 0
            ? exact
            : all.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static long MeasureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or is locked should not abort the measurement.
            }
        }

        return total;
    }
}
