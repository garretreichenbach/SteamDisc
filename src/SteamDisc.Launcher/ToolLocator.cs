namespace SteamDisc.Launcher;

/// <summary>
/// Finds the sibling tools the launcher hands off to — the authoring GUI and the disc installer.
/// </summary>
/// <remarks>
/// Works both in a packaged release (the exes sit next to the launcher) and in a dev tree (the
/// build outputs under <c>src/…/bin</c>), so the same launcher runs from either.
/// </remarks>
public static class ToolLocator
{
    public static string? LocateAuthor()
        => LocateBeside("SteamDisc.Author.exe", "Author")
           ?? LocateBeside("SteamDisc.Builder.App.exe", "Author")
           ?? LocateInRepo("SteamDisc.Builder.App", "SteamDisc.Author.exe", "SteamDisc.Builder.App.exe");

    public static string? LocateInstaller()
        => LocateBeside("Setup.exe", "Installer")
           ?? LocateInRepo("SteamDisc.Runtime.App", "Setup.exe");

    private static string? LocateBeside(string fileName, string subfolder)
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, fileName), Path.Combine(baseDir, subfolder, fileName) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? LocateInRepo(string projectName, params string[] fileNames)
    {
        var root = FindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory));
        if (root is null)
        {
            return null;
        }

        var bin = Path.Combine(root.FullName, "src", projectName, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        return fileNames
            .SelectMany(name => Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories))
            .OrderByDescending(p => p.Contains("publish", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static DirectoryInfo? FindRepoRoot(DirectoryInfo? start)
    {
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SteamDisc.sln")))
            {
                return directory;
            }
        }

        return null;
    }
}
