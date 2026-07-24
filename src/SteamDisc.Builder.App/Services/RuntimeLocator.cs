namespace SteamDisc.Builder.App.Services;

/// <summary>
/// Finds a published runtime <c>Setup.exe</c> to stamp onto a disc, so the common case needs no
/// manual pointing. Best-effort: the authoring GUI offers a "Locate…" override when this misses.
/// </summary>
public static class RuntimeLocator
{
    public static string? Locate()
    {
        // Packaged layout: Setup.exe sits beside the authoring app (or in an Installer subfolder).
        var baseDir = AppContext.BaseDirectory;
        foreach (var beside in new[] { Path.Combine(baseDir, "Setup.exe"), Path.Combine(baseDir, "Installer", "Setup.exe") })
        {
            if (File.Exists(beside))
            {
                return beside;
            }
        }

        var root = FindRepoRoot(new DirectoryInfo(baseDir));
        if (root is null)
        {
            return null;
        }

        var appBin = Path.Combine(root.FullName, "src", "SteamDisc.Runtime.App", "bin");
        if (!Directory.Exists(appBin))
        {
            return null;
        }

        // A disc's Setup.exe has to run on a machine with no .NET, and only the publish output is
        // self-contained — so published builds win over plain build output. Within them the
        // newest wins: ranking every publish above every build let one stale publish keep winning
        // forever and get stamped onto every disc, while the Builder's preview showed current code.
        var candidates = Directory.EnumerateFiles(appBin, "Setup.exe", SearchOption.AllDirectories).ToList();
        var published = candidates
            .Where(p => p.Contains("publish", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (published.Count > 0 ? published : candidates)
            .OrderByDescending(File.GetLastWriteTimeUtc)
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
