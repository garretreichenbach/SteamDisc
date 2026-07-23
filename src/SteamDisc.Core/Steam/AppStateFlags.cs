namespace SteamDisc.Core.Steam;

/// <summary>
/// Bit flags for <c>AppState.StateFlags</c> in an <c>appmanifest_*.acf</c>.
/// </summary>
/// <remarks>
/// Community-documented (SteamKit / SteamRE), not an official contract — treat as the best
/// available model and re-verify against a live client (spike S1) before relying on an
/// individual bit. Note that these are <em>flags</em>: the values quoted in circulation as
/// magic numbers decompose predictably, e.g. 1026 is
/// <see cref="UpdateStarted"/> | <see cref="UpdateRequired"/> and 6 is
/// <see cref="FullyInstalled"/> | <see cref="UpdateRequired"/>.
/// </remarks>
[Flags]
public enum AppStateFlags
{
    Invalid = 0,
    Uninstalled = 1 << 0,
    UpdateRequired = 1 << 1,
    FullyInstalled = 1 << 2,
    Encrypted = 1 << 3,
    Locked = 1 << 4,
    FilesMissing = 1 << 5,
    AppRunning = 1 << 6,
    FilesCorrupt = 1 << 7,
    UpdateRunning = 1 << 8,
    UpdatePaused = 1 << 9,
    UpdateStarted = 1 << 10,
    Uninstalling = 1 << 11,
    BackupRunning = 1 << 12,
    Reconfiguring = 1 << 16,
    Validating = 1 << 17,
    AddingFiles = 1 << 18,
    Preallocating = 1 << 19,
    Downloading = 1 << 20,
    Staging = 1 << 21,
    Committing = 1 << 22,
    UpdateStopping = 1 << 23,
}

/// <summary>Well-known <see cref="AppStateFlags"/> combinations used by the install engine.</summary>
public static class AppStateFlagPresets
{
    /// <summary>What a healthy, up-to-date install looks like. This is what Path B writes.</summary>
    public const AppStateFlags Installed = AppStateFlags.FullyInstalled;

    /// <summary>
    /// Installed, but Steam should re-check it. Used by the Path C hybrid so the client
    /// self-heals an imperfect manifest instead of trusting ours blindly.
    /// </summary>
    public const AppStateFlags InstalledNeedsValidation = AppStateFlags.FullyInstalled | AppStateFlags.UpdateRequired;

    /// <summary>Queued for update — the value quoted as "1026" in most community write-ups.</summary>
    public const AppStateFlags UpdateQueued = AppStateFlags.UpdateStarted | AppStateFlags.UpdateRequired;
}
