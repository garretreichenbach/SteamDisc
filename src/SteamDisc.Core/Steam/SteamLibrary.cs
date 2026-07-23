using System.Globalization;

namespace SteamDisc.Core.Steam;

/// <summary>A Steam library folder — the thing that contains <c>steamapps</c>.</summary>
public sealed class SteamLibrary
{
    public SteamLibrary(string path) => Path = System.IO.Path.GetFullPath(path);

    /// <summary>The library root, i.e. the parent of <c>steamapps</c>.</summary>
    public string Path { get; }

    public string SteamAppsPath => System.IO.Path.Combine(Path, "steamapps");

    /// <summary>Where game payloads are extracted to.</summary>
    public string CommonPath => System.IO.Path.Combine(SteamAppsPath, "common");

    public string DownloadingPath => System.IO.Path.Combine(SteamAppsPath, "downloading");

    public string ManifestPath(uint appId)
        => System.IO.Path.Combine(SteamAppsPath, AppManifest.FileNameFor(appId));

    public string InstallPath(string installDir) => System.IO.Path.Combine(CommonPath, installDir);

    public bool Exists => Directory.Exists(SteamAppsPath);

    /// <summary>Free space on the volume backing this library, or <see langword="null"/> if unknown.</summary>
    public long? GetAvailableFreeBytes()
    {
        try
        {
            return new DriveInfo(System.IO.Path.GetPathRoot(Path) ?? Path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads every <c>appmanifest_*.acf</c> in this library.</summary>
    public IReadOnlyList<InstalledApp> GetInstalledApps()
    {
        if (!Exists)
        {
            return Array.Empty<InstalledApp>();
        }

        var apps = new List<InstalledApp>();
        foreach (var file in Directory.EnumerateFiles(SteamAppsPath, "appmanifest_*.acf"))
        {
            AppManifest manifest;
            try
            {
                manifest = AppManifest.Load(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Vdf.VdfSyntaxException)
            {
                // A single unreadable manifest must not hide the rest of the library.
                continue;
            }

            apps.Add(new InstalledApp(manifest, this, file));
        }

        return apps.OrderBy(a => a.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public InstalledApp? FindApp(uint appId)
    {
        var file = ManifestPath(appId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return new InstalledApp(AppManifest.Load(file), this, file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Vdf.VdfSyntaxException)
        {
            return null;
        }
    }

    public override string ToString() => Path;
}

/// <summary>An app installed in a specific library.</summary>
public sealed class InstalledApp
{
    public InstalledApp(AppManifest manifest, SteamLibrary library, string manifestPath)
    {
        Manifest = manifest;
        Library = library;
        ManifestPath = manifestPath;
    }

    public AppManifest Manifest { get; }

    public SteamLibrary Library { get; }

    public string ManifestPath { get; }

    public uint AppId => Manifest.AppId;

    /// <summary>Absolute path of the game's folder under <c>steamapps/common</c>.</summary>
    public string InstallPath => Library.InstallPath(Manifest.InstallDir);

    public bool InstallPathExists => Directory.Exists(InstallPath);

    /// <summary>
    /// True when Steam considers the install complete and not pending an update — the only
    /// state from which authoring a disc makes sense.
    /// </summary>
    public bool IsFullyInstalled =>
        Manifest.StateFlags.HasFlag(AppStateFlags.FullyInstalled) &&
        !Manifest.StateFlags.HasFlag(AppStateFlags.UpdateRequired);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Manifest.Name} ({AppId})");
}
