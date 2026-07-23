using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;

namespace SteamDisc.Authoring;

/// <summary>
/// Looks for the things that will make a burned disc misbehave, and says so plainly.
/// </summary>
/// <remarks>
/// The plan is explicit that third-party DRM and live-service titles should be flagged rather
/// than pretended away. Detection is by file-system fingerprint — a third-party launcher or
/// anti-cheat leaves obvious traces in a game folder — which is imperfect but honest, and
/// costs nothing but a directory walk. Every finding says what will actually happen to the
/// user, not just what was found.
/// </remarks>
public static class DrmDetector
{
    private sealed record Fingerprint(string Code, string Message, params string[] Markers);

    private static readonly Fingerprint[] Fingerprints =
    {
        new(
            "drm.ubisoft",
            "This game uses Ubisoft Connect. It will require an internet connection and a Ubisoft " +
            "account on first launch, regardless of what is on the disc.",
            "__Installer/UPlayInstaller.exe",
            "UbisoftGameLauncherInstaller.exe",
            "uplay_install.exe"),
        new(
            "drm.ea",
            "This game uses the EA app or Origin. It will contact EA on first launch and may " +
            "download its own updates.",
            "__Installer/EAAppInstaller.exe",
            "EAAppInstaller.exe",
            "OriginSetup.exe",
            "Support/EACoreIni"),
        new(
            "drm.epic",
            "This game bundles the Epic Online Services SDK and is likely to contact Epic on launch.",
            "EOSSDK-Win64-Shipping.dll",
            "EOSSDK-Win32-Shipping.dll"),
        new(
            "drm.rockstar",
            "This game uses the Rockstar Games Launcher, which will install itself and go online.",
            "Launcher.exe/RockstarService.exe",
            "RockstarService.exe"),
        new(
            "anticheat.easy",
            "This game uses EasyAntiCheat, which installs a system service and updates itself online.",
            "EasyAntiCheat/EasyAntiCheat_Setup.exe",
            "EasyAntiCheat_Setup.exe"),
        new(
            "anticheat.battleye",
            "This game uses BattlEye, which installs a system service and updates itself online.",
            "BattlEye/BEService.exe",
            "BEService.exe"),
        new(
            "launcher.bethesda",
            "This game ships a Bethesda launcher that may try to update itself.",
            "BethesdaNetLauncher.exe"),
    };

    /// <summary>Inspects an installed app and returns advisories to embed in the payload.</summary>
    public static IReadOnlyList<Advisory> Inspect(InstalledApp app)
    {
        var advisories = new List<Advisory>();

        if (!app.InstallPathExists)
        {
            return advisories;
        }

        var present = BuildFileIndex(app.InstallPath);

        foreach (var fingerprint in Fingerprints)
        {
            if (fingerprint.Markers.Any(marker => present.Contains(NormaliseMarker(marker))))
            {
                advisories.Add(Advisory.Warning(fingerprint.Code, fingerprint.Message));
            }
        }

        // A title patched in the last month is very likely to patch again the moment it runs.
        var age = DateTimeOffset.UtcNow - app.Manifest.LastUpdated;
        if (age < TimeSpan.FromDays(30) && app.Manifest.LastUpdated.ToUnixTimeSeconds() > 0)
        {
            advisories.Add(Advisory.Info(
                "updates.recent",
                $"This game was last updated {FormatAge(age)} ago. Expect Steam to download a patch " +
                "soon after installing from disc."));
        }

        if (!app.IsFullyInstalled)
        {
            advisories.Add(Advisory.Warning(
                "install.incomplete",
                "Steam does not consider this install complete. Let it finish updating before authoring a disc, " +
                "or the disc will carry a half-patched game."));
        }

        if (app.Manifest.BuildId <= 0)
        {
            advisories.Add(Advisory.Warning(
                "manifest.nobuildid",
                "This game's manifest has no build id, so Steam will not recognise the installed files " +
                "and will re-download the game."));
        }

        if (app.Manifest.InstalledDepots.Count == 0)
        {
            advisories.Add(Advisory.Warning(
                "manifest.nodepots",
                "This game's manifest lists no depots, so Steam cannot match the files on disc to a known " +
                "build and will re-download the game."));
        }

        return advisories;
    }

    /// <summary>
    /// Indexes the shallow part of the tree — markers all live at or near the top, and walking
    /// a 40 GB game folder to find a launcher stub is a poor trade.
    /// </summary>
    private static HashSet<string> BuildFileIndex(string root)
    {
        var index = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Scan(string directory, int depth)
        {
            if (depth > 3)
            {
                return;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                var relative = NormaliseMarker(Path.GetRelativePath(root, entry));
                index.Add(relative);
                index.Add(NormaliseMarker(Path.GetFileName(entry)));

                if (Directory.Exists(entry))
                {
                    Scan(entry, depth + 1);
                }
            }
        }

        Scan(root, 0);
        return index;
    }

    private static string NormaliseMarker(string value)
        => value.Replace('\\', '/').Trim('/');

    private static string FormatAge(TimeSpan age) => age.TotalDays switch
    {
        < 2 => "a day",
        < 14 => $"{(int)age.TotalDays} days",
        < 60 => $"{(int)(age.TotalDays / 7)} weeks",
        _ => $"{(int)(age.TotalDays / 30)} months",
    };
}
