using System.Globalization;
using System.Net;

namespace SteamDisc.Art.Providers;

/// <summary>
/// Fetches Valve's own artwork from the Steam CDN. No API key needed.
/// </summary>
/// <remarks>
/// <para>
/// The plan flags CDN path drift as a real risk worth a spike (S4): assets used to live under
/// one host and path convention, newer titles have moved some of them to a different one, and
/// neither is documented or guaranteed.
/// </para>
/// <para>
/// So this provider hard-codes nothing beyond a list of <em>candidates</em> per asset kind and
/// probes them in order, taking the first that responds. A path that disappears costs one
/// failed request and a fallback rather than a broken feature, and adding a newly discovered
/// convention is a one-line change to the table below.
/// </para>
/// </remarks>
public sealed class SteamCdnArtProvider : IArtProvider
{
    private const string CloudflareHost = "https://cdn.cloudflare.steamstatic.com/steam/apps";
    private const string AkamaiHost = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";
    private const string LegacyHost = "https://steamcdn-a.akamaihd.net/steam/apps";

    private readonly HttpClient _http;
    private readonly ArtCache _cache;

    public SteamCdnArtProvider(HttpClient? http = null, ArtCache? cache = null)
    {
        _http = http ?? CreateDefaultClient();
        _cache = cache ?? new ArtCache();
    }

    public string Id => "steam-cdn";

    public string DisplayName => "Steam (official art)";

    public bool IsConfigured => true;

    public IReadOnlyCollection<ArtKind> SupportedKinds { get; } = new[]
    {
        ArtKind.Header, ArtKind.Capsule, ArtKind.Cover, ArtKind.Hero, ArtKind.Logo,
    };

    /// <summary>
    /// File names Valve uses per asset kind, most preferred first, with the <em>nominal</em>
    /// pixel size the name implies.
    /// </summary>
    /// <remarks>
    /// The sizes are ranking hints only and are not to be trusted as fact: Portal 2, for one,
    /// serves a 600×900 image from its <c>_2x</c> path. Real dimensions are read from the bytes
    /// once a candidate is fetched, and that is what any quality warning is based on.
    /// </remarks>
    private static IReadOnlyList<CdnAsset> FilesFor(ArtKind kind) => kind switch
    {
        ArtKind.Header => new CdnAsset[] { new("header.jpg", 460, 215) },
        ArtKind.Capsule => new CdnAsset[]
        {
            new("capsule_616x353.jpg", 616, 353),
            new("capsule_467x181.jpg", 467, 181),
            new("header.jpg", 460, 215),
        },
        ArtKind.Cover => new CdnAsset[]
        {
            new("library_600x900_2x.jpg", 1200, 1800),
            new("library_600x900.jpg", 600, 900),
            new("portrait.png"),
        },
        ArtKind.Hero => new CdnAsset[]
        {
            new("library_hero_2x.jpg", 3840, 1240),
            new("library_hero.jpg", 1920, 620),
            new("page_bg_generated_v6b.jpg"),
        },
        ArtKind.Logo => new CdnAsset[]
        {
            new("logo_2x.png"),
            new("logo.png"),
            new("library_logo.png"),
        },
        _ => Array.Empty<CdnAsset>(),
    };

    private static IReadOnlyList<string> Hosts => new[] { CloudflareHost, AkamaiHost, LegacyHost };

    public async Task<IReadOnlyList<ArtCandidate>> SearchAsync(
        ArtQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.AppId == 0)
        {
            return Array.Empty<ArtCandidate>();
        }

        var results = new List<ArtCandidate>();

        foreach (var (file, width, height) in FilesFor(query.Kind))
        {
            foreach (var host in Hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url = string.Create(CultureInfo.InvariantCulture, $"{host}/{query.AppId}/{file}");
                if (!await ExistsAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                results.Add(new ArtCandidate(Id, query.Kind, url, width, height, "Valve", Path.GetFileNameWithoutExtension(file)));

                // One host per file name is enough; move on to the next preference.
                break;
            }

            if (results.Count >= query.Limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<ArtAsset> FetchAsync(ArtCandidate candidate, CancellationToken cancellationToken = default)
    {
        var bytes = await _http.GetByteArrayAsync(candidate.Url, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(new Uri(candidate.Url).AbsolutePath);
        var path = _cache.Store(bytes, extension);

        // Replace the nominal size with what actually arrived, so the recorded provenance and
        // any print-quality warning describe the real image rather than the path's promise.
        var actual = Core.Images.RasterImage.ReadSize(path);
        var corrected = actual is { } size
            ? candidate with { Width = size.Width, Height = size.Height }
            : candidate;

        return new ArtAsset(corrected, path, _cache.HashOf(bytes));
    }

    /// <summary>
    /// Probes a URL with a HEAD request, falling back to a ranged GET because some CDN edges
    /// answer HEAD with 403 while serving the same URL happily.
    /// </summary>
    private async Task<bool> ExistsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(head, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden))
            {
                return false;
            }

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var ranged = await _http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            return ranged.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <param name="File">Asset file name under the app's CDN folder.</param>
    /// <param name="Width">Nominal pixel width, when the name implies a fixed size.</param>
    /// <param name="Height">Nominal pixel height, when the name implies a fixed size.</param>
    private readonly record struct CdnAsset(string File, int? Width = null, int? Height = null);

    internal static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDisc/0.1 (+https://github.com/)");
        return client;
    }
}
