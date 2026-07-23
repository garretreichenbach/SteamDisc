using SteamDisc.Art;
using SteamDisc.Art.Providers;

namespace SteamDisc.Tests;

public class ArtRankingTests
{
    private static ArtResolver Resolver() => new(new IArtProvider[]
    {
        new LocalArtProvider(),
        new SteamCdnArtProvider(),
    });

    private static ArtCandidate Candidate(string provider, int? width, int? height, ArtKind kind = ArtKind.Cover)
        => new(provider, kind, $"https://example/{provider}-{width}x{height}", width, height);

    [Fact]
    public void A_correctly_shaped_candidate_beats_one_of_unknown_shape()
    {
        // The failure this guards against is subtle and print-visible: an asset whose
        // dimensions the provider did not report used to score as a perfect aspect match and
        // win over the higher-resolution art that was actually the right shape.
        var requirement = new ArtRequirement("cover", new[] { ArtKind.Cover }, 600.0 / 900);

        var ranked = Resolver().Rank(
            new[]
            {
                Candidate("steam-cdn", null, null),
                Candidate("steam-cdn", 1200, 1800),
            },
            requirement);

        Assert.Equal(1200, ranked[0].Width);
    }

    [Fact]
    public void Among_equally_shaped_candidates_the_larger_one_wins()
    {
        var requirement = new ArtRequirement("cover", new[] { ArtKind.Cover }, 600.0 / 900);

        var ranked = Resolver().Rank(
            new[]
            {
                Candidate("steam-cdn", 600, 900),
                Candidate("steam-cdn", 1200, 1800),
            },
            requirement);

        Assert.Equal(1200, ranked[0].Width);
    }

    [Fact]
    public void A_badly_shaped_candidate_loses_to_one_of_unknown_shape()
    {
        var requirement = new ArtRequirement("cover", new[] { ArtKind.Cover }, 600.0 / 900);

        var ranked = Resolver().Rank(
            new[]
            {
                Candidate("steam-cdn", 1920, 620),  // very wide; wrong for a portrait slot
                Candidate("steam-cdn", null, null),
            },
            requirement);

        Assert.Null(ranked[0].Width);
    }

    [Fact]
    public void Local_files_outrank_the_online_providers()
    {
        // A user who supplied art meant it.
        var requirement = new ArtRequirement("cover", new[] { ArtKind.Cover }, 600.0 / 900);

        var ranked = Resolver().Rank(
            new[]
            {
                Candidate("steam-cdn", 1200, 1800),
                Candidate("local", 600, 900),
            },
            requirement);

        Assert.Equal("local", ranked[0].ProviderId);
    }

    [Fact]
    public void Earlier_kinds_in_a_requirement_outrank_later_ones()
    {
        var requirement = new ArtRequirement("background", new[] { ArtKind.Hero, ArtKind.Capsule });

        var ranked = Resolver().Rank(
            new[]
            {
                Candidate("steam-cdn", 616, 353, ArtKind.Capsule),
                Candidate("steam-cdn", 1920, 620, ArtKind.Hero),
            },
            requirement);

        Assert.Equal(ArtKind.Hero, ranked[0].Kind);
    }

    [Fact]
    public void Aspect_penalty_is_symmetric_between_too_wide_and_too_tall()
    {
        var tooWide = ArtResolver.AspectPenalty(Candidate("x", 200, 100), 1.0);
        var tooTall = ArtResolver.AspectPenalty(Candidate("x", 100, 200), 1.0);

        Assert.Equal(tooWide, tooTall, 6);
    }

    [Fact]
    public void No_preferred_aspect_means_no_penalty_at_all()
    {
        Assert.Equal(0, ArtResolver.AspectPenalty(Candidate("x", null, null), null));
        Assert.Equal(0, ArtResolver.AspectPenalty(Candidate("x", 100, 200), null));
    }
}

public class SteamGridDbParsingTests
{
    [Fact]
    public void Reads_the_v2_response_shape()
    {
        const string json = """
            {
              "success": true,
              "data": [
                {
                  "id": 1,
                  "url": "https://cdn2.steamgriddb.com/grid/abc.png",
                  "thumb": "https://cdn2.steamgriddb.com/thumb/abc.png",
                  "width": 600,
                  "height": 900,
                  "style": "alternate",
                  "author": { "name": "someone" }
                }
              ]
            }
            """;

        var candidate = Assert.Single(
            SteamGridDbArtProvider.ParseResponse(json, "steamgriddb", ArtKind.Cover, 20));

        Assert.Equal("https://cdn2.steamgriddb.com/grid/abc.png", candidate.Url);
        Assert.Equal(600, candidate.Width);
        Assert.Equal(900, candidate.Height);
        Assert.Equal("alternate", candidate.Style);
        Assert.Equal("someone", candidate.Author);
    }

    [Fact]
    public void Skips_entries_without_a_url_rather_than_failing()
    {
        const string json = """
            { "success": true, "data": [ { "id": 1 }, { "id": 2, "url": "https://example/b.png" } ] }
            """;

        var candidate = Assert.Single(
            SteamGridDbArtProvider.ParseResponse(json, "steamgriddb", ArtKind.Cover, 20));

        Assert.Equal("https://example/b.png", candidate.Url);
    }

    [Fact]
    public void An_empty_result_set_is_not_an_error()
    {
        Assert.Empty(SteamGridDbArtProvider.ParseResponse(
            """{ "success": true, "data": [] }""", "steamgriddb", ArtKind.Cover, 20));
    }

    [Fact]
    public void Honours_the_result_limit()
    {
        const string json = """
            {
              "data": [
                { "url": "https://example/1.png" },
                { "url": "https://example/2.png" },
                { "url": "https://example/3.png" }
              ]
            }
            """;

        Assert.Equal(2, SteamGridDbArtProvider.ParseResponse(json, "steamgriddb", ArtKind.Cover, 2).Count);
    }

    [Fact]
    public void Is_unconfigured_without_an_api_key()
    {
        Assert.False(new SteamGridDbArtProvider(apiKey: string.Empty).IsConfigured);
        Assert.True(new SteamGridDbArtProvider(apiKey: "abc123").IsConfigured);
    }
}

public class ArtCacheTests
{
    [Fact]
    public void Stores_content_once_regardless_of_how_often_it_is_fetched()
    {
        using var temp = new TempDirectory();
        var cache = new ArtCache(temp.CreateSubdirectory("cache"));

        var bytes = TestData.Incompressible(4096, seed: 7);

        var first = cache.Store(bytes, ".png");
        var second = cache.Store(bytes, ".png");

        Assert.Equal(first, second);
        Assert.Equal(bytes, File.ReadAllBytes(first));
    }

    [Fact]
    public void Different_content_lands_in_different_files()
    {
        using var temp = new TempDirectory();
        var cache = new ArtCache(temp.CreateSubdirectory("cache"));

        Assert.NotEqual(
            cache.Store(TestData.Incompressible(1024, 1), ".png"),
            cache.Store(TestData.Incompressible(1024, 2), ".png"));
    }

    [Fact]
    public void Records_provenance_so_a_rebuild_is_reproducible()
    {
        using var temp = new TempDirectory();
        var cache = new ArtCache(temp.CreateSubdirectory("cache"));
        var bytes = TestData.Incompressible(2048, 3);
        var path = cache.Store(bytes, ".png");

        var sidecar = new ArtSidecar { AppId = 620, Title = "Portal 2" };
        sidecar.Record("cover", new ArtAsset(
            new ArtCandidate("steam-cdn", ArtKind.Cover, "https://example/cover.png", 600, 900),
            path,
            cache.HashOf(bytes)));

        var file = temp.Combine(ArtSidecar.FileName);
        sidecar.Save(file);

        var reloaded = ArtSidecar.Load(file);
        var entry = Assert.Single(reloaded.Entries);

        Assert.Equal("cover", entry.Slot);
        Assert.Equal("steam-cdn", entry.Provider);
        Assert.Equal("https://example/cover.png", entry.SourceUrl);
        Assert.Equal(cache.HashOf(bytes), entry.ContentHash);
    }

    [Fact]
    public void Recording_the_same_slot_twice_replaces_rather_than_duplicates()
    {
        var sidecar = new ArtSidecar();
        var candidate = new ArtCandidate("steam-cdn", ArtKind.Cover, "https://example/a.png");

        sidecar.Record("cover", new ArtAsset(candidate, "/tmp/a.png", "aaa"));
        sidecar.Record("cover", new ArtAsset(candidate with { Url = "https://example/b.png" }, "/tmp/b.png", "bbb"));

        var entry = Assert.Single(sidecar.Entries);
        Assert.Equal("bbb", entry.ContentHash);
    }
}
