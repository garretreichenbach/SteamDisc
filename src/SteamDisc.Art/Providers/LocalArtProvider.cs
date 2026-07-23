namespace SteamDisc.Art.Providers;

/// <summary>
/// Serves artwork the user already has — a folder they point at, a file they dragged in.
/// </summary>
/// <remarks>
/// Listed first in the resolver's fallback chain on purpose. Power users mostly bring their
/// own art, and the plan is explicit that this path must feel as fast as the online providers;
/// it does, because there is no network involved at all.
/// </remarks>
public sealed class LocalArtProvider : IArtProvider
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

    private readonly List<string> _searchDirectories = new();
    private readonly ArtCache _cache;

    public LocalArtProvider(IEnumerable<string>? searchDirectories = null, ArtCache? cache = null)
    {
        if (searchDirectories is not null)
        {
            _searchDirectories.AddRange(searchDirectories);
        }

        _cache = cache ?? new ArtCache();
    }

    public string Id => "local";

    public string DisplayName => "Local files";

    public bool IsConfigured => true;

    public IReadOnlyCollection<ArtKind> SupportedKinds { get; } = Enum.GetValues<ArtKind>();

    public IReadOnlyList<string> SearchDirectories => _searchDirectories;

    public void AddSearchDirectory(string path)
    {
        if (Directory.Exists(path) && !_searchDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _searchDirectories.Add(path);
        }
    }

    /// <summary>
    /// Matches on file name. A file called <c>620-cover.png</c> or <c>Portal 2 hero.jpg</c>
    /// finds its way to the right slot without any metadata.
    /// </summary>
    public Task<IReadOnlyList<ArtCandidate>> SearchAsync(
        ArtQuery query,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ArtCandidate>();
        var kindName = query.Kind.ToString().ToLowerInvariant();
        var appId = query.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var directory in _searchDirectories.Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var matchesApp = query.AppId != 0 && name.Contains(appId, StringComparison.Ordinal);
                var matchesTitle = !string.IsNullOrWhiteSpace(query.Title) &&
                                   name.Contains(Normalise(query.Title), StringComparison.Ordinal);
                var matchesKind = name.Contains(kindName, StringComparison.Ordinal);

                if (!matchesApp && !matchesTitle && !matchesKind)
                {
                    continue;
                }

                var size = Core.Images.RasterImage.ReadSize(file);
                results.Add(new ArtCandidate(
                    Id,
                    query.Kind,
                    file,
                    size?.Width,
                    size?.Height,
                    Author: null,
                    Style: matchesKind ? kindName : null));

                if (results.Count >= query.Limit)
                {
                    break;
                }
            }
        }

        // Files that name the kind explicitly are the strongest signal, so surface them first.
        results.Sort((a, b) => (b.Style is null ? 0 : 1).CompareTo(a.Style is null ? 0 : 1));
        return Task.FromResult<IReadOnlyList<ArtCandidate>>(results);
    }

    /// <summary>Copies a local file into the cache so its content hash is recorded like any other.</summary>
    public Task<ArtAsset> FetchAsync(ArtCandidate candidate, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!File.Exists(candidate.Url))
        {
            throw new FileNotFoundException("Local artwork not found.", candidate.Url);
        }

        var bytes = File.ReadAllBytes(candidate.Url);
        var path = _cache.Store(bytes, Path.GetExtension(candidate.Url));
        return Task.FromResult(new ArtAsset(candidate, path, _cache.HashOf(bytes)));
    }

    /// <summary>Turns an arbitrary file path into a candidate, for drag-and-drop and clipboard paste.</summary>
    public ArtCandidate CandidateForFile(string path, ArtKind kind)
    {
        var size = Core.Images.RasterImage.ReadSize(path);
        return new ArtCandidate(Id, kind, Path.GetFullPath(path), size?.Width, size?.Height);
    }

    private static string Normalise(string value)
        => new(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
}
