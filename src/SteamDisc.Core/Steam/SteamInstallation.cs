using SteamDisc.Core.Vdf;

namespace SteamDisc.Core.Steam;

/// <summary>A located Steam client installation and the state hanging off it.</summary>
public sealed class SteamInstallation
{
    public SteamInstallation(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    /// <summary>Steam's install root — the directory containing <c>steamapps</c> and <c>config</c>.</summary>
    public string RootPath { get; }

    public string ConfigPath => Path.Combine(RootPath, "config");

    public string SteamAppsPath => Path.Combine(RootPath, "steamapps");

    /// <summary>Path to the client executable, or <see langword="null"/> when it cannot be found.</summary>
    public string? ClientExecutablePath
    {
        get
        {
            foreach (var candidate in new[] { "steam.exe", "steam.sh", "steam" })
            {
                var path = Path.Combine(RootPath, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Enumerates every library folder registered with this client, starting with the one
    /// inside the Steam install itself.
    /// </summary>
    public IReadOnlyList<SteamLibrary> GetLibraries()
    {
        var libraries = new List<SteamLibrary>();
        var seen = new HashSet<string>(PathComparer);

        void TryAdd(string path)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full) && Directory.Exists(Path.Combine(full, "steamapps")))
            {
                libraries.Add(new SteamLibrary(full));
            }
        }

        TryAdd(RootPath);

        foreach (var path in ReadLibraryFolderPaths())
        {
            TryAdd(path);
        }

        return libraries;
    }

    /// <summary>
    /// Parses <c>config/libraryfolders.vdf</c>, coping with both the modern schema (numbered
    /// objects with a <c>path</c> key) and the legacy one (numbered keys whose value is the path).
    /// </summary>
    public IReadOnlyList<string> ReadLibraryFolderPaths()
    {
        var paths = new List<string>();

        foreach (var file in new[]
                 {
                     Path.Combine(ConfigPath, "libraryfolders.vdf"),
                     Path.Combine(SteamAppsPath, "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            KvNode root;
            try
            {
                root = VdfTextReader.ParseFile(file);
            }
            catch (VdfSyntaxException)
            {
                continue;
            }

            var container = string.Equals(root.Key, "libraryfolders", StringComparison.OrdinalIgnoreCase)
                ? root
                : root.Find("libraryfolders") ?? root;

            foreach (var child in container.Children)
            {
                // Keys are ordinals ("0", "1", ...). Older files also carry
                // "TimeNextStatsReport"/"ContentStatsID" siblings, which are not libraries.
                if (!int.TryParse(child.Key, out _))
                {
                    continue;
                }

                var path = child.IsObject ? child.GetString("path") : child.Value;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            break;
        }

        return paths;
    }

    /// <summary>Every app installed across every library of this client.</summary>
    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        var apps = new List<InstalledApp>();
        foreach (var library in GetLibraries())
        {
            apps.AddRange(library.GetInstalledApps());
        }

        return apps;
    }

    /// <summary>
    /// Accounts that have logged in on this machine, newest first. The install engine needs
    /// this because <c>LastOwner</c> in a transplanted manifest must name the local account,
    /// not whoever authored the disc.
    /// </summary>
    public IReadOnlyList<SteamUser> GetKnownUsers()
    {
        var file = Path.Combine(ConfigPath, "loginusers.vdf");
        if (!File.Exists(file))
        {
            return Array.Empty<SteamUser>();
        }

        KvNode root;
        try
        {
            root = VdfTextReader.ParseFile(file);
        }
        catch (VdfSyntaxException)
        {
            return Array.Empty<SteamUser>();
        }

        var container = string.Equals(root.Key, "users", StringComparison.OrdinalIgnoreCase)
            ? root
            : root.Find("users") ?? root;

        var users = new List<SteamUser>();
        foreach (var child in container.Children)
        {
            if (!child.IsObject || !ulong.TryParse(child.Key, out var steamId))
            {
                continue;
            }

            users.Add(new SteamUser(
                steamId,
                child.GetString("AccountName") ?? string.Empty,
                child.GetString("PersonaName") ?? string.Empty,
                child.GetString("MostRecent") == "1",
                child.GetInt64("Timestamp")));
        }

        return users
            .OrderByDescending(u => u.MostRecent)
            .ThenByDescending(u => u.Timestamp)
            .ToList();
    }

    /// <summary>The account most likely to be the one currently signed in, if any.</summary>
    public SteamUser? GetMostRecentUser() => GetKnownUsers().FirstOrDefault();

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public override string ToString() => RootPath;
}

/// <summary>An account recorded in <c>config/loginusers.vdf</c>.</summary>
public readonly record struct SteamUser(
    ulong SteamId64,
    string AccountName,
    string PersonaName,
    bool MostRecent,
    long Timestamp)
{
    public string DisplayName => string.IsNullOrEmpty(PersonaName) ? AccountName : PersonaName;
}
