using System.Globalization;

namespace SteamDisc.Core.Protocol;

/// <summary>
/// Drives the Steam client through <c>steam://</c> URLs — the only supported way to ask a
/// running client to do something.
/// </summary>
public sealed class SteamProtocolDriver
{
    private readonly IProcessRunner _runner;

    public SteamProtocolDriver(IProcessRunner? runner = null)
        => _runner = runner ?? SystemProcessRunner.Instance;

    public static string RunUri(uint appId) => Uri("run", appId);

    /// <summary>
    /// Asks Steam to verify the local files of an app. Path C leans on this: write a manifest
    /// flagged as needing validation, then let the client reconcile it against the depots.
    /// </summary>
    public static string ValidateUri(uint appId) => Uri("validate", appId);

    public static string InstallUri(uint appId) => Uri("install", appId);

    public static string StoreUri(uint appId) => Uri("store", appId);

    /// <summary>Opens the app's properties page — useful for a "something went wrong" escape hatch.</summary>
    public static string PropertiesUri(uint appId) => Uri("gameproperties", appId);

    private static string Uri(string verb, uint appId)
        => string.Create(CultureInfo.InvariantCulture, $"steam://{verb}/{appId}");

    public void Launch(uint appId) => Open(RunUri(appId));

    public void Validate(uint appId) => Open(ValidateUri(appId));

    /// <summary>
    /// Opens a <c>steam://</c> (or any) URI using the platform's handler. Windows resolves the
    /// protocol through the registry; macOS and Linux need an explicit opener because
    /// <see cref="System.Diagnostics.ProcessStartInfo.UseShellExecute"/> does not cover URIs there.
    /// </summary>
    public void Open(string uri)
    {
        if (OperatingSystem.IsWindows())
        {
            _runner.Start(new ProcessLaunch(uri, Array.Empty<string>(), UseShellExecute: true));
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            _runner.Start(ProcessLaunch.Of("/usr/bin/open", uri));
            return;
        }

        _runner.Start(ProcessLaunch.Of("xdg-open", uri));
    }
}
