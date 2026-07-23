using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SteamDisc.Core.Steam;

/// <summary>Finds the Steam client installation on the current machine.</summary>
/// <remarks>
/// Windows is the target for v1, but the Builder is useful anywhere a Steam library is
/// mounted, and macOS/Linux support costs only a handful of extra candidate paths.
/// </remarks>
public static class SteamLocator
{
    /// <summary>
    /// Returns the Steam installation, or <see langword="null"/> when none can be found.
    /// </summary>
    /// <param name="overridePath">
    /// An explicit root supplied by the user (a CLI flag or settings entry), which always wins.
    /// </param>
    public static SteamInstallation? Locate(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return LooksLikeSteamRoot(overridePath) ? new SteamInstallation(overridePath) : null;
        }

        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (LooksLikeSteamRoot(candidate))
            {
                return new SteamInstallation(candidate);
            }
        }

        return null;
    }

    /// <summary>Like <see cref="Locate"/>, but throws a diagnosable error instead of returning null.</summary>
    public static SteamInstallation LocateRequired(string? overridePath = null)
        => Locate(overridePath) ?? throw new SteamNotFoundException(EnumerateCandidatePaths().ToList());

    /// <summary>
    /// A directory counts as a Steam root when it has a <c>steamapps</c> folder. We do not
    /// require the executable: a library-only folder copied to another machine is still worth
    /// reading from in the Builder.
    /// </summary>
    public static bool LooksLikeSteamRoot(string path)
    {
        try
        {
            return Directory.Exists(Path.Combine(path, "steamapps"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Candidate roots in priority order, for both probing and error messages.</summary>
    public static IEnumerable<string> EnumerateCandidatePaths()
    {
        if (Environment.GetEnvironmentVariable("STEAMDISC_STEAM_PATH") is { Length: > 0 } fromEnvironment)
        {
            yield return fromEnvironment;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var path in EnumerateWindowsRegistryPaths())
            {
                yield return path;
            }

            foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432" })
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } programFiles)
                {
                    yield return Path.Combine(programFiles, "Steam");
                }
            }

            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
            yield break;
        }

        // Linux, including the Steam Deck's layout.
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateWindowsRegistryPaths()
    {
        // SteamPath is per-user and reflects the running client; InstallPath is machine-wide.
        foreach (var (hive, subKey, valueName) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            string? value = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(subKey);
                value = key?.GetValue(valueName) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A locked-down machine is not a reason to fail; fall through to the path probes.
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                // The registry stores forward slashes in SteamPath.
                yield return value.Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }
}

/// <summary>Thrown when no Steam installation can be located.</summary>
public sealed class SteamNotFoundException : Exception
{
    public SteamNotFoundException(IReadOnlyList<string> probedPaths)
        : base(BuildMessage(probedPaths))
    {
        ProbedPaths = probedPaths;
    }

    public IReadOnlyList<string> ProbedPaths { get; }

    private static string BuildMessage(IReadOnlyList<string> probedPaths)
    {
        var paths = probedPaths.Count == 0
            ? "  (no candidate paths on this platform)"
            : string.Join(Environment.NewLine, probedPaths.Select(p => "  " + p));

        return "Could not find a Steam installation. Probed:" + Environment.NewLine + paths +
               Environment.NewLine +
               "Set STEAMDISC_STEAM_PATH or pass an explicit --steam-path to override.";
    }
}
