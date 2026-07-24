using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamDisc.Builder.App.Services;

/// <summary>
/// Reads a game's short description from Steam's public store API, to seed the disc's blurb.
/// </summary>
/// <remarks>
/// The <c>appdetails</c> endpoint needs no key. It is best-effort: a game with no store page, a
/// rate-limit, or an offline machine simply yields null, and the user types their own description.
/// </remarks>
public sealed partial class SteamStore
{
    private readonly HttpClient _http;

    public SteamStore(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDisc/0.1 (+https://github.com/)");
    }

    public async Task<string?> GetShortDescriptionAsync(uint appId, CancellationToken cancellationToken = default)
    {
        if (appId == 0)
        {
            return null;
        }

        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic&l=english";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(true);

        if (!document.RootElement.TryGetProperty(appId.ToString(), out var entry) ||
            !entry.TryGetProperty("success", out var success) || !success.GetBoolean() ||
            !entry.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("short_description", out var description))
        {
            return null;
        }

        var text = description.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // short_description is usually plain, but strip any stray tags and decode entities.
        var stripped = TagPattern().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(stripped).Trim();
    }

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();
}
