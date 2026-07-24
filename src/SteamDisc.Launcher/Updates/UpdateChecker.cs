using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SteamDisc.Launcher.Updates;

/// <param name="Version">The released version.</param>
/// <param name="TagName">Its git tag, e.g. <c>v1.0.0</c>.</param>
/// <param name="AssetName">The archive built for this platform.</param>
/// <param name="DownloadUrl">Where to fetch that archive.</param>
/// <param name="ReleaseUrl">The release page, for "what's new".</param>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    string ReleaseUrl);

/// <summary>
/// Asks GitHub whether a newer SteamDisc has been published, and which archive suits this machine.
/// </summary>
/// <remarks>
/// Uses the <c>releases/latest</c> endpoint, which deliberately ignores drafts and pre-releases —
/// so the draft the release workflow opens stays invisible until it is actually published.
/// Every failure path returns null: a missing network or a rate-limit must never block the app.
/// </remarks>
public sealed class UpdateChecker
{
    public const string Repository = "garretreichenbach/SteamDisc";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            // GitHub rejects API calls without a User-Agent.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDisc-Updater");
        }
    }

    public static Version CurrentVersion => Normalise(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The runtime identifier this build was published for, e.g. <c>win-x64</c>.</summary>
    public static string CurrentRid
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };

            if (OperatingSystem.IsWindows())
            {
                return $"win-{architecture}";
            }

            return OperatingSystem.IsMacOS() ? $"osx-{architecture}" : $"linux-{architecture}";
        }
    }

    /// <summary>
    /// True when the launcher is running from a released bundle rather than a dev build tree.
    /// Updating in place only makes sense for the former.
    /// </summary>
    public static bool IsPackagedBundle
    {
        get
        {
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "SteamDisc.Author" + suffix));
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Repository}/releases/latest";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { Length: > 0 } tag ||
                !TryParseVersion(tag, out var latest) ||
                latest <= CurrentVersion)
            {
                return null;
            }

            var releaseUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var rid = CurrentRid;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameElement) &&
                    asset.TryGetProperty("browser_download_url", out var urlElement) &&
                    nameElement.GetString() is { Length: > 0 } name &&
                    urlElement.GetString() is { Length: > 0 } downloadUrl &&
                    name.Contains($"-{rid}.", StringComparison.OrdinalIgnoreCase) &&
                    (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)))
                {
                    return new UpdateInfo(latest, tag, name, downloadUrl, releaseUrl);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    internal static bool TryParseVersion(string tag, out Version version)
    {
        if (Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
        {
            version = Normalise(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Flattens to major.minor.patch so 1.2.3 and 1.2.3.0 compare equal.</summary>
    private static Version Normalise(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
