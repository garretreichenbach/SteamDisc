using System.Globalization;
using SteamDisc.Core.Steam;

namespace SteamDisc.Core.Protocol;

/// <summary>
/// Drives the Steam client through its executable's command line.
/// </summary>
/// <remarks>
/// This is the Path A fallback described in the project plan: point Steam at a folder holding
/// a <c>sku.sis</c> and let its own restore wizard do the work. It is officially sanctioned but
/// opaque and historically flaky, so Path B (own archive + manifest injection) is the default
/// and this exists for the cases where Path B is refused.
/// </remarks>
public sealed class SteamCliDriver
{
    private readonly SteamInstallation _installation;
    private readonly IProcessRunner _runner;

    public SteamCliDriver(SteamInstallation installation, IProcessRunner? runner = null)
    {
        _installation = installation;
        _runner = runner ?? SystemProcessRunner.Instance;
    }

    /// <summary>True when a client executable was found and the CLI can be used at all.</summary>
    public bool IsAvailable => _installation.ClientExecutablePath is not null;

    /// <summary>
    /// Launches Steam's native backup-restore flow against <paramref name="backupFolder"/>,
    /// which must contain a <c>sku.sis</c> alongside the <c>.csd</c>/<c>.csm</c> containers.
    /// </summary>
    public void StartNativeRestore(string backupFolder)
    {
        if (!File.Exists(Path.Combine(backupFolder, "sku.sis")))
        {
            throw new FileNotFoundException(
                "A native Steam restore needs a sku.sis in the backup folder.",
                Path.Combine(backupFolder, "sku.sis"));
        }

        _runner.Start(new ProcessLaunch(RequireExecutable(), new[] { "-install", backupFolder }));
    }

    /// <summary>Launches an app without going through the protocol handler.</summary>
    public void LaunchApp(uint appId)
        => _runner.Start(new ProcessLaunch(
            RequireExecutable(),
            new[] { "-applaunch", appId.ToString(CultureInfo.InvariantCulture) }));

    /// <summary>
    /// Asks the client to shut down. The install engine offers this because a restart is the
    /// one reliable way to make Steam re-read manifests it did not write (spike S2).
    /// </summary>
    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var result = await _runner
            .RunAsync(
                new ProcessLaunch(RequireExecutable(), new[] { "-shutdown" }, Timeout: TimeSpan.FromMinutes(1)),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>Starts the client if it is not already running.</summary>
    public void Start() => _runner.Start(new ProcessLaunch(RequireExecutable(), Array.Empty<string>()));

    private string RequireExecutable()
        => _installation.ClientExecutablePath
           ?? throw new InvalidOperationException($"No Steam executable found under '{_installation.RootPath}'.");
}
