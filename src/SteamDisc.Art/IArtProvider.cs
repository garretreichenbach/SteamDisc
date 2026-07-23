namespace SteamDisc.Art;

/// <summary>The kinds of artwork the tool knows how to ask for.</summary>
public enum ArtKind
{
    /// <summary>Wide store header, roughly 460×215.</summary>
    Header,

    /// <summary>Wide capsule, roughly 616×353. Good for splash backgrounds.</summary>
    Capsule,

    /// <summary>Portrait library art, roughly 600×900. The natural cover shape.</summary>
    Cover,

    /// <summary>Very wide library hero. Good for full-bleed backgrounds.</summary>
    Hero,

    /// <summary>Transparent title logo.</summary>
    Logo,

    /// <summary>Small square icon.</summary>
    Icon,
}

/// <param name="AppId">Steam app id, when known.</param>
/// <param name="Title">Title, for providers that match on name.</param>
/// <param name="Kind">What kind of art is wanted.</param>
/// <param name="Limit">Maximum results.</param>
public sealed record ArtQuery(uint AppId, string? Title, ArtKind Kind, int Limit = 20);

/// <param name="ProviderId">Which provider produced this.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Url">Where to fetch it from — an http(s) URL or a local path.</param>
/// <param name="Width">Pixel width, when the provider reports one.</param>
/// <param name="Height">Pixel height, when the provider reports one.</param>
/// <param name="Author">Who made it, for community sources.</param>
/// <param name="Style">Provider-specific style tag, e.g. "alternate", "white_logo".</param>
/// <param name="ThumbnailUrl">A smaller preview, when available.</param>
public sealed record ArtCandidate(
    string ProviderId,
    ArtKind Kind,
    string Url,
    int? Width = null,
    int? Height = null,
    string? Author = null,
    string? Style = null,
    string? ThumbnailUrl = null)
{
    public double? AspectRatio => Width is > 0 && Height is > 0 ? (double)Width / Height : null;

    public override string ToString()
        => $"{ProviderId} {Kind} {Width}×{Height}{(Style is null ? string.Empty : " " + Style)}";
}

/// <param name="Candidate">What was fetched.</param>
/// <param name="LocalPath">Where the bytes now live on disk.</param>
/// <param name="ContentHash">SHA-256 of the file, which is also its cache key.</param>
public sealed record ArtAsset(ArtCandidate Candidate, string LocalPath, string ContentHash);

/// <summary>
/// A source of artwork. Kept deliberately narrow — search, then fetch — so the Builder's art
/// picker never has to know which provider it is talking to.
/// </summary>
public interface IArtProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>False when the provider needs configuration it does not have, such as an API key.</summary>
    bool IsConfigured { get; }

    /// <summary>Kinds this provider can supply.</summary>
    IReadOnlyCollection<ArtKind> SupportedKinds { get; }

    Task<IReadOnlyList<ArtCandidate>> SearchAsync(ArtQuery query, CancellationToken cancellationToken = default);

    /// <summary>Downloads or copies a candidate into the cache and returns where it landed.</summary>
    Task<ArtAsset> FetchAsync(ArtCandidate candidate, CancellationToken cancellationToken = default);
}
