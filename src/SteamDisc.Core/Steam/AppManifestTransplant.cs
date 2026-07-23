namespace SteamDisc.Core.Steam;

/// <summary>How aggressively the installed app should be kept up to date afterwards.</summary>
public enum AutoUpdateBehavior
{
    /// <summary>Leave whatever the source manifest said.</summary>
    Unchanged = -1,

    AlwaysKeepUpdated = 0,

    /// <summary>Only update when launched. Sensible for a title installed from physical media.</summary>
    OnlyOnLaunch = 1,

    HighPriority = 2,
}

/// <param name="LocalSteamId">
/// SteamID64 of the account installing. Written to <c>LastOwner</c>; leaving the authoring
/// machine's account there is a good way to confuse the client.
/// </param>
/// <param name="LauncherPath">Local Steam executable path, or null to drop the key.</param>
/// <param name="RequestValidation">
/// Path C: mark the app as needing verification so Steam re-checks our work locally.
/// </param>
/// <param name="AutoUpdate">Auto-update behaviour to force, if any.</param>
/// <param name="SizeOnDisk">Measured size of what was actually extracted, when known.</param>
public sealed record TransplantOptions(
    ulong? LocalSteamId = null,
    string? LauncherPath = null,
    bool RequestValidation = false,
    AutoUpdateBehavior AutoUpdate = AutoUpdateBehavior.Unchanged,
    long? SizeOnDisk = null);

/// <summary>
/// Rewrites a manifest captured on the authoring machine so it describes a fresh install on
/// this one.
/// </summary>
/// <remarks>
/// The plan's mitigation for ACF fidelity is to transplant a real manifest rather than
/// synthesise one, and this is where that happens. The rule followed throughout: touch only
/// the fields that are about <em>this machine</em> or <em>this install event</em>, and leave
/// everything describing the <em>content</em> — build id, depot manifest ids, depot sizes —
/// exactly as the source had it. Those content fields are what stop Steam deciding an update
/// is due, so every one of them is off limits.
/// </remarks>
public static class AppManifestTransplant
{
    /// <summary>Fields rewritten for the target machine. Documented so the intent is auditable.</summary>
    public static IReadOnlyList<string> RewrittenFields { get; } = new[]
    {
        "StateFlags",
        "LastOwner",
        "LauncherPath",
        "LastUpdated",
        "UpdateResult",
        "StagingSize",
        "BytesToDownload",
        "BytesDownloaded",
        "BytesToStage",
        "BytesStaged",
        "ScheduledAutoUpdate",
        "TargetBuildID",
    };

    /// <summary>Returns a transplanted copy; the source manifest is not modified.</summary>
    public static AppManifest Prepare(AppManifest source, TransplantOptions options)
    {
        var manifest = source.Clone();
        var root = manifest.Root;

        root.SetInt64(
            "StateFlags",
            (long)(options.RequestValidation
                ? AppStateFlagPresets.InstalledNeedsValidation
                : AppStateFlagPresets.Installed));

        if (options.LocalSteamId is { } steamId && steamId != 0)
        {
            root.SetUInt64("LastOwner", steamId);
        }

        if (options.LauncherPath is { Length: > 0 } launcher)
        {
            root.SetString("LauncherPath", launcher);
        }
        else
        {
            // A launcher path pointing at the authoring machine's drive letter is worse than none.
            root.Remove("LauncherPath");
        }

        root.SetInt64("LastUpdated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Nothing is in flight: zero every transfer counter so the client does not believe it
        // was interrupted mid-download and try to resume one.
        root.SetInt64("UpdateResult", 0);
        root.SetInt64("StagingSize", 0);
        root.SetInt64("BytesToDownload", 0);
        root.SetInt64("BytesDownloaded", 0);
        root.SetInt64("BytesToStage", 0);
        root.SetInt64("BytesStaged", 0);
        root.SetInt64("ScheduledAutoUpdate", 0);

        // TargetBuildID must agree with buildid, or the client sees a pending update to itself.
        root.SetInt64("TargetBuildID", manifest.BuildId);

        if (options.AutoUpdate != AutoUpdateBehavior.Unchanged)
        {
            root.SetInt64("AutoUpdateBehavior", (long)options.AutoUpdate);
        }

        if (options.SizeOnDisk is { } size and > 0)
        {
            root.SetInt64("SizeOnDisk", size);
        }

        return manifest;
    }

    /// <summary>
    /// Checks a manifest for the things that would make Steam re-download the game, which is
    /// the failure mode the whole approach exists to avoid. Returns human-readable problems.
    /// </summary>
    public static IReadOnlyList<string> Audit(AppManifest manifest)
    {
        var problems = new List<string>();

        if (manifest.AppId == 0)
        {
            problems.Add("appid is missing or zero.");
        }

        if (string.IsNullOrWhiteSpace(manifest.InstallDir))
        {
            problems.Add("installdir is missing.");
        }

        if (manifest.BuildId <= 0)
        {
            problems.Add(
                "buildid is missing or zero. Steam will treat the install as unknown and re-download it.");
        }

        if (manifest.TargetBuildId != 0 && manifest.TargetBuildId != manifest.BuildId)
        {
            problems.Add(
                $"TargetBuildID ({manifest.TargetBuildId}) differs from buildid ({manifest.BuildId}); " +
                "Steam reads that as an update in progress.");
        }

        var depots = manifest.InstalledDepots;
        if (depots.Count == 0)
        {
            problems.Add(
                "InstalledDepots is empty. Without depot manifest ids Steam cannot match the files " +
                "on disk to a known build and will re-download.");
        }
        else
        {
            foreach (var depot in depots)
            {
                if (string.IsNullOrWhiteSpace(depot.ManifestId) || depot.ManifestId == "0")
                {
                    problems.Add($"Depot {depot.DepotId} has no manifest id.");
                }
            }
        }

        if (!manifest.StateFlags.HasFlag(AppStateFlags.FullyInstalled))
        {
            problems.Add($"StateFlags is {(int)manifest.StateFlags}, which does not include FullyInstalled.");
        }

        return problems;
    }
}
