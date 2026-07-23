using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Covers;

/// <summary>
/// A cover in progress: which template, which art in which slot, and what the text says.
/// </summary>
/// <remarks>
/// Saved next to the disc staging folder so a cover can be reprinted, tweaked or handed to a
/// print shop long after the disc was burned — which is rather the point of making physical
/// media in the first place.
/// </remarks>
public sealed class CoverProject
{
    public const string FileName = "cover.json";

    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    /// <summary>Slot id to image path. Paths may be absolute or relative to the project file.</summary>
    [JsonPropertyName("artwork")]
    public Dictionary<string, string> Artwork { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overrides for the template's text fields, by field id.</summary>
    [JsonPropertyName("text")]
    public Dictionary<string, string> Text { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Draw slot outlines and labels, for checking a layout before printing for real.</summary>
    [JsonPropertyName("proofMode")]
    public bool ProofMode { get; set; }

    /// <summary>Include crop marks outside the trim box.</summary>
    [JsonPropertyName("cropMarks")]
    public bool CropMarks { get; set; } = true;

    [JsonIgnore]
    public string? RootPath { get; set; }

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson());
        RootPath = directory;
    }

    public static CoverProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<CoverProject>(File.ReadAllText(path), SerializerOptions)
                      ?? throw new InvalidDataException($"'{path}' is not a valid cover project.");
        project.RootPath = Path.GetDirectoryName(Path.GetFullPath(path));
        return project;
    }

    /// <summary>Resolves a slot's artwork path against the project folder.</summary>
    public string? ResolveArtwork(string slotId)
    {
        if (!Artwork.TryGetValue(slotId, out var path) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        if (RootPath is null)
        {
            return File.Exists(path) ? path : null;
        }

        var combined = Path.GetFullPath(Path.Combine(RootPath, path));
        return File.Exists(combined) ? combined : null;
    }

    /// <summary>Placeholder values available to template text fields.</summary>
    public IReadOnlyDictionary<string, string> BuildTokens() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = Title,
        ["appId"] = AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["year"] = DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
