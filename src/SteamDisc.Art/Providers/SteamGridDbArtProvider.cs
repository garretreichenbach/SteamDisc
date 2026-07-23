using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SteamDisc.Art.Providers;

/// <summary>
/// Fetches community artwork from SteamGridDB.
/// </summary>
/// <remarks>
/// <para>
/// The API is v2 at <c>https://www.steamgriddb.com/api/v2</c>, keyed by a personal API key
/// generated from account preferences. The plan noted an existing C# client worth adopting
/// rather than rolling our own HTTP layer; against that, the surface actually needed here is
/// four GET endpoints returning the same shape, and Core deliberately carries no third-party
/// dependencies so the disc runtime stays small and auditable. Sixty lines of
/// <see cref="HttpClient"/> is the cheaper side of that trade.
/// </para>
/// <para>
/// The maintainers have signalled a v3 with no date attached, which is why the JSON shape is
/// read defensively and the provider abstraction stays thin enough to swap wholesale.
/// </para>
/// </remarks>
public sealed class SteamGridDbArtProvider : IArtProvider
{
    public const string ApiRoot = "https://www.steamgriddb.com/api/v2";

    /// <summary>Environment variable read when no key is passed explicitly.</summary>
    public const string ApiKeyVariable = "STEAMGRIDDB_API_KEY";

    private readonly HttpClient _http;
    private readonly ArtCache _cache;
    private readonly string? _apiKey;

    public SteamGridDbArtProvider(string? apiKey = null, HttpClient? http = null, ArtCache? cache = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyVariable);
        _http = http ?? SteamCdnArtProvider.CreateDefaultClient();
        _cache = cache ?? new ArtCache();
    }

    public string Id => "steamgriddb";

    public string DisplayName => "SteamGridDB (community art)";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public IReadOnlyCollection<ArtKind> SupportedKinds { get; } = new[]
    {
        ArtKind.Cover, ArtKind.Capsule, ArtKind.Hero, ArtKind.Logo, ArtKind.Icon,
    };

    public async Task<IReadOnlyList<ArtCandidate>> SearchAsync(
        ArtQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                $"SteamGridDB needs an API key. Generate one at steamgriddb.com under Preferences → API " +
                $"and set {ApiKeyVariable}, or pass it to the Builder.");
        }

        if (query.AppId == 0)
        {
            return Array.Empty<ArtCandidate>();
        }

        var endpoint = EndpointFor(query.Kind);
        if (endpoint is null)
        {
            return Array.Empty<ArtCandidate>();
        }

        var url = string.Create(CultureInfo.InvariantCulture, $"{ApiRoot}/{endpoint}/steam/{query.AppId}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A title simply having no community art is the common case, not an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Array.Empty<ArtCandidate>();
            }

            throw new HttpRequestException(
                $"SteamGridDB returned {(int)response.StatusCode} {response.ReasonPhrase} for {url}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse(json, Id, query.Kind, query.Limit);
    }

    /// <summary>Reads the v2 response shape defensively, skipping anything unexpected.</summary>
    internal static IReadOnlyList<ArtCandidate> ParseResponse(string json, string providerId, ArtKind kind, int limit)
    {
        var results = new List<ArtCandidate>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (results.Count >= limit)
            {
                break;
            }

            if (!item.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            results.Add(new ArtCandidate(
                providerId,
                kind,
                urlElement.GetString()!,
                ReadInt(item, "width"),
                ReadInt(item, "height"),
                ReadAuthor(item),
                ReadString(item, "style"),
                ReadString(item, "thumb")));
        }

        return results;
    }

    private static string? EndpointFor(ArtKind kind) => kind switch
    {
        ArtKind.Cover or ArtKind.Capsule => "grids",
        ArtKind.Hero => "heroes",
        ArtKind.Logo => "logos",
        ArtKind.Icon => "icons",
        _ => null,
    };

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadAuthor(JsonElement element)
        => element.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
            ? ReadString(author, "name")
            : null;

    public async Task<ArtAsset> FetchAsync(ArtCandidate candidate, CancellationToken cancellationToken = default)
    {
        var bytes = await _http.GetByteArrayAsync(candidate.Url, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(new Uri(candidate.Url).AbsolutePath);
        var path = _cache.Store(bytes, extension);
        return new ArtAsset(candidate, path, _cache.HashOf(bytes));
    }
}
