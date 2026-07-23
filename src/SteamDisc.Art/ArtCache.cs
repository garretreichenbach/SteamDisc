using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Art;

/// <summary>
/// Content-addressed cache for downloaded artwork.
/// </summary>
/// <remarks>
/// Keyed by the hash of the bytes rather than the URL, so the same image fetched from two
/// providers is stored once, and so a rebuild of the same disc is reproducible. A sidecar
/// records where each file came from, which is what makes "rebuild this disc a year from now"
/// a real possibility rather than a hope.
/// </remarks>
public sealed class ArtCache
{
    private readonly string _root;

    public ArtCache(string? root = null)
    {
        _root = root ?? DefaultRoot;
        Directory.CreateDirectory(_root);
    }

    public static string DefaultRoot
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                appData = Path.GetTempPath();
            }

            return Path.Combine(appData, "SteamDisc", "art-cache");
        }
    }

    public string Root => _root;

    /// <summary>
    /// Stores bytes under their content hash, returning the path. Storing the same content
    /// twice is a no-op.
    /// </summary>
    public string Store(byte[] content, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var path = PathFor(hash, extension);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return path;
    }

    public string HashOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>Looks up cached content by hash, or returns null.</summary>
    public string? Find(string hash, string extension)
    {
        var path = PathFor(hash, extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Two-character fan-out, so a large cache does not become one enormous directory.</summary>
    private string PathFor(string hash, string extension)
        => Path.Combine(_root, hash[..2], hash + NormaliseExtension(extension));

    private static string NormaliseExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".bin";
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    public long TotalBytes()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

    public void Clear()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        Directory.CreateDirectory(_root);
    }
}

/// <summary>
/// <c>art.json</c> — records which artwork went into a disc and where it came from, so a
/// rebuild produces the same cover rather than whatever the provider is serving that week.
/// </summary>
public sealed class ArtSidecar
{
    public const string FileName = "art.json";

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<ArtSidecarEntry> Entries { get; set; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    public static ArtSidecar Load(string path)
        => JsonSerializer.Deserialize<ArtSidecar>(File.ReadAllText(path), SerializerOptions)
           ?? throw new InvalidDataException($"'{path}' is not a valid art sidecar.");

    public void Record(string slot, ArtAsset asset)
    {
        Entries.RemoveAll(e => string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase));
        Entries.Add(new ArtSidecarEntry
        {
            Slot = slot,
            Kind = asset.Candidate.Kind,
            Provider = asset.Candidate.ProviderId,
            SourceUrl = asset.Candidate.Url,
            ContentHash = asset.ContentHash,
            Author = asset.Candidate.Author,
            FetchedUtc = DateTimeOffset.UtcNow,
        });
    }
}

public sealed class ArtSidecarEntry
{
    /// <summary>Theme or cover slot this art fills, e.g. "background", "cover", "front".</summary>
    [JsonPropertyName("slot")]
    public string Slot { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public ArtKind Kind { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("fetchedUtc")]
    public DateTimeOffset FetchedUtc { get; set; }
}
