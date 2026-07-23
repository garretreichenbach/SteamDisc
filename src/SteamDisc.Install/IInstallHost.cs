using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;

namespace SteamDisc.Install;

/// <summary>
/// Everything the install engine needs from a user interface.
/// </summary>
/// <remarks>
/// The engine never touches a console, a window or a message box directly. That is what lets
/// the same engine drive the console runtime, the skinned Avalonia runtime and the test suite
/// without change — and it is why disc swapping, which is fundamentally a request to a human,
/// can be expressed as an ordinary await.
/// </remarks>
public interface IInstallHost
{
    /// <summary>
    /// Asks for the disc carrying <paramref name="request"/> and returns the folder it is
    /// mounted at, or null to abort. Implementations are expected to block until the user acts.
    /// </summary>
    Task<string?> RequestDiscAsync(DiscRequest request, CancellationToken cancellationToken);

    /// <summary>Reports a non-fatal problem the user should know about.</summary>
    void ReportWarning(string message);

    /// <summary>
    /// Asks a yes/no question — e.g. "restart Steam now?". Returning false must always be safe.
    /// </summary>
    Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken);
}

/// <param name="DiscNumber">Which disc of the set is needed.</param>
/// <param name="DiscCount">How many discs the set has.</param>
/// <param name="SetId">Set identity the inserted disc must match.</param>
/// <param name="Title">Game title, for the prompt.</param>
/// <param name="Reason">Why the disc is being asked for, when it is not simply the next one.</param>
public sealed record DiscRequest(
    int DiscNumber,
    int DiscCount,
    string SetId,
    string Title,
    string? Reason = null);

/// <summary>A host that answers no to everything. Useful for unattended runs and tests.</summary>
public sealed class UnattendedInstallHost : IInstallHost
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings => _warnings;

    public Task<string?> RequestDiscAsync(DiscRequest request, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public void ReportWarning(string message) => _warnings.Add(message);

    public Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <param name="Manifest">The payload manifest read from the disc root.</param>
/// <param name="DiscRoot">Folder holding <c>payload.json</c> — a mounted disc or a staging folder.</param>
/// <param name="TargetLibrary">Steam library to install into.</param>
/// <param name="Steam">The located Steam installation.</param>
public sealed record InstallRequest(
    PayloadManifest Manifest,
    string DiscRoot,
    SteamLibrary TargetLibrary,
    SteamInstallation Steam)
{
    /// <summary>Verify each file's SHA-256 while extracting. Costs time; catches a bad disc.</summary>
    public bool VerifyHashes { get; init; } = true;

    /// <summary>Verify volume hashes against the sidecar before extracting anything.</summary>
    public bool VerifyVolumesUpFront { get; init; }

    /// <summary>Run <c>_CommonRedist</c> installers after extraction. Windows only.</summary>
    public bool RunPrerequisites { get; init; } = true;

    /// <summary>Override the payload's own post-install choices.</summary>
    public bool? Launch { get; init; }

    public bool? ValidateAfterInstall { get; init; }

    /// <summary>Auto-update behaviour to force on the installed app.</summary>
    public AutoUpdateBehavior AutoUpdate { get; init; } = AutoUpdateBehavior.Unchanged;

    /// <summary>Continue an interrupted install rather than starting over.</summary>
    public bool AllowResume { get; init; } = true;
}

public enum InstallOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

/// <param name="Outcome">How the install ended.</param>
/// <param name="InstallPath">Where the game was written.</param>
/// <param name="ManifestPath">Where the app manifest was written.</param>
/// <param name="BytesWritten">Uncompressed bytes extracted.</param>
/// <param name="Duration">Wall-clock time.</param>
/// <param name="Warnings">Non-fatal problems encountered.</param>
/// <param name="Error">The failure, when <paramref name="Outcome"/> is Failed.</param>
public sealed record InstallResult(
    InstallOutcome Outcome,
    string? InstallPath,
    string? ManifestPath,
    long BytesWritten,
    TimeSpan Duration,
    IReadOnlyList<string> Warnings,
    Exception? Error = null)
{
    public bool Succeeded => Outcome == InstallOutcome.Succeeded;
}
