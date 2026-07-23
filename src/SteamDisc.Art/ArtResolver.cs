using SteamDisc.Art.Providers;
using SteamDisc.Core.Diagnostics;

namespace SteamDisc.Art;

/// <param name="Slot">Theme or cover slot being filled, e.g. "background", "cover", "logo".</param>
/// <param name="Kinds">Art kinds to try, in order of preference.</param>
/// <param name="PreferredAspect">Aspect ratio to rank candidates against, when known.</param>
public sealed record ArtRequirement(string Slot, IReadOnlyList<ArtKind> Kinds, double? PreferredAspect = null);

/// <summary>
/// Resolves the artwork a disc needs by trying providers in order and picking the best
/// candidate for each slot.
/// </summary>
/// <remarks>
/// The ordering logic follows the prior art the plan points at (boppreh/steamgrid): try local
/// files first because a user who supplied art meant it, then Valve's own assets, then the
/// community database. Within a provider's results, prefer the candidate whose aspect ratio is
/// closest to what the slot wants and, among equals, the larger image — a cover printed at
/// 300 DPI needs the pixels.
/// </remarks>
public sealed class ArtResolver
{
    private readonly IReadOnlyList<IArtProvider> _providers;
    private readonly ISteamDiscLogger _logger;

    public ArtResolver(IReadOnlyList<IArtProvider> providers, ISteamDiscLogger? logger = null)
    {
        _providers = providers;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Providers in default preference order, skipping any that are not configured.</summary>
    public static ArtResolver CreateDefault(
        string? steamGridDbApiKey = null,
        IEnumerable<string>? localSearchDirectories = null,
        ArtCache? cache = null,
        ISteamDiscLogger? logger = null)
    {
        cache ??= new ArtCache();

        var providers = new List<IArtProvider>
        {
            new LocalArtProvider(localSearchDirectories, cache),
            new SteamCdnArtProvider(cache: cache),
        };

        var steamGridDb = new SteamGridDbArtProvider(steamGridDbApiKey, cache: cache);
        if (steamGridDb.IsConfigured)
        {
            providers.Add(steamGridDb);
        }

        return new ArtResolver(providers, logger);
    }

    public IReadOnlyList<IArtProvider> Providers => _providers;

    /// <summary>The slots a disc theme normally needs, with sensible kind preferences.</summary>
    public static IReadOnlyList<ArtRequirement> DefaultThemeRequirements { get; } = new[]
    {
        new ArtRequirement("background", new[] { ArtKind.Hero, ArtKind.Capsule, ArtKind.Header }, 1920.0 / 620),
        new ArtRequirement("cover", new[] { ArtKind.Cover, ArtKind.Capsule }, 600.0 / 900),
        new ArtRequirement("logo", new[] { ArtKind.Logo }),
    };

    /// <summary>Searches every configured provider for one requirement, best candidates first.</summary>
    public async Task<IReadOnlyList<ArtCandidate>> SearchAsync(
        uint appId,
        string? title,
        ArtRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<ArtCandidate>();

        foreach (var provider in _providers.Where(p => p.IsConfigured))
        {
            foreach (var kind in requirement.Kinds.Where(k => provider.SupportedKinds.Contains(k)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var found = await provider
                        .SearchAsync(new ArtQuery(appId, title, kind), cancellationToken)
                        .ConfigureAwait(false);
                    candidates.AddRange(found);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    // One provider being down must not stop the others from answering.
                    _logger.Warn($"{provider.DisplayName} failed for {kind}: {ex.Message}");
                }
            }
        }

        return Rank(candidates, requirement);
    }

    /// <summary>
    /// Fills every requirement, taking the top-ranked candidate for each. Slots with no
    /// candidate are simply absent from the result, which the theme writer handles.
    /// </summary>
    /// <remarks>
    /// Slots are filled with <em>distinct</em> artwork where possible. Resolving each slot
    /// independently would hand the same top-ranked image to the front and the back of a case,
    /// which is not what a cover looks like; when a slot's best candidate is already spoken
    /// for, the next-best unused one is taken instead. Reuse only happens when there is
    /// genuinely nothing else.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, ArtAsset>> ResolveAsync(
        uint appId,
        string? title,
        IReadOnlyList<ArtRequirement>? requirements = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ArtAsset>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements ?? DefaultThemeRequirements)
        {
            var candidates = await SearchAsync(appId, title, requirement, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                _logger.Info($"No artwork found for '{requirement.Slot}'.");
                continue;
            }

            var chosen = candidates.FirstOrDefault(c => !used.Contains(c.Url));
            if (chosen is null)
            {
                chosen = candidates[0];
                _logger.Info(
                    $"'{requirement.Slot}' reuses artwork already assigned to another slot; " +
                    "no alternative was available.");
            }

            var provider = _providers.First(p => p.Id == chosen.ProviderId);
            try
            {
                result[requirement.Slot] = await provider.FetchAsync(chosen, cancellationToken).ConfigureAwait(false);
                used.Add(chosen.Url);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _logger.Warn($"Could not fetch artwork for '{requirement.Slot}': {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Orders candidates by provider preference, then by how well the aspect ratio matches,
    /// then by resolution.
    /// </summary>
    internal IReadOnlyList<ArtCandidate> Rank(IReadOnlyList<ArtCandidate> candidates, ArtRequirement requirement)
    {
        var providerOrder = _providers
            .Select((p, index) => (p.Id, index))
            .ToDictionary(x => x.Id, x => x.index, StringComparer.OrdinalIgnoreCase);

        var kindOrder = requirement.Kinds
            .Select((k, index) => (k, index))
            .ToDictionary(x => x.k, x => x.index);

        return candidates
            .OrderBy(c => providerOrder.GetValueOrDefault(c.ProviderId, int.MaxValue))
            .ThenBy(c => kindOrder.GetValueOrDefault(c.Kind, int.MaxValue))
            .ThenBy(c => AspectPenalty(c, requirement.PreferredAspect))
            .ThenByDescending(c => (long)(c.Width ?? 0) * (c.Height ?? 0))
            .ToList();
    }

    /// <summary>
    /// Penalty for how far a candidate's shape is from what the slot wants. A candidate whose
    /// dimensions the provider did not report is charged a modest penalty rather than nothing:
    /// scoring "unknown" as a perfect match would let an unlabelled asset beat a correctly
    /// shaped, higher-resolution one, which is exactly backwards for print.
    /// </summary>
    internal const double UnknownAspectPenalty = 0.25;

    internal static double AspectPenalty(ArtCandidate candidate, double? preferred)
    {
        if (preferred is not { } target || target <= 0)
        {
            return 0;
        }

        if (candidate.AspectRatio is not { } actual)
        {
            return UnknownAspectPenalty;
        }

        // Compare in log space so 2:1 too wide and 2:1 too tall are penalised equally.
        return Math.Abs(Math.Log(actual / target));
    }
}
