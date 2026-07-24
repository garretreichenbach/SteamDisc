namespace SteamDisc.Builder.App.Services;

/// <summary>
/// Finds a published runtime <c>Setup.exe</c> to stamp onto a disc, so the common case needs no
/// manual pointing. Best-effort: the authoring GUI offers a "Locate…" override when this misses.
/// </summary>
public static class RuntimeLocator
{
    public static string? Locate()
    {
        var root = FindRepoRoot(new DirectoryInfo(AppContext.BaseDirectory));
        if (root is null)
        {
            return null;
        }

        var appBin = Path.Combine(root.FullName, "src", "SteamDisc.Runtime.App", "bin");
        if (!Directory.Exists(appBin))
        {
            return null;
        }

        // Prefer a published, single-file build; then Release; then whatever is newest.
        return Directory.EnumerateFiles(appBin, "Setup.exe", SearchOption.AllDirectories)
            .OrderByDescending(p => p.Contains("publish", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Contains("Release", StringComparison.OrdinalIgnoreCase))
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
