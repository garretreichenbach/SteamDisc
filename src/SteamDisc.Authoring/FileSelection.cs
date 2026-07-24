using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Authoring;

/// <summary>
/// A hand-editable record of which parts of a game folder to pack — the authoring artifact
/// behind "let me choose what goes on the disc".
/// </summary>
/// <remarks>
/// The file stores <em>decisions</em>, not a full inventory: an entry per top-level folder and
/// loose file, each kept or dropped. That keeps it small, readable, and a natural fit for a
/// checkbox tree in the GUI, which builds the tree live from the folder and uses these entries
/// only to seed the checkboxes. A user can add finer-grained entries by hand — a nested path
/// works exactly as a top-level one, because the archive engines treat every excluded path as a
/// prefix.
///
/// The honest caveat that rides with this feature: the transplanted <c>appmanifest.acf</c> still
/// tells Steam the depots are fully installed. Dropping files Steam expects means a Steam
/// "verify integrity" can pull them back. Whole optional/language content is the safe thing to
/// drop; cutting into a depot's core files is the user's call, made with eyes open.
/// </remarks>
public sealed class SelectionManifest
{
    public const int SupportedFormatVersion = 1;

    /// <summary>Conventional file name, e.g. <c>Portal 2.selection.json</c> — but any path works.</summary>
    public const string Extension = ".selection.json";

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = SupportedFormatVersion;

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Folder under <c>steamapps/common</c> these paths are relative to.</summary>
    [JsonPropertyName("installDir")]
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>Build the selection was authored against, so a stale one can be spotted.</summary>
    [JsonPropertyName("buildId")]
    public long BuildId { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "SteamDisc";

    [JsonPropertyName("entries")]
    public List<SelectionEntry> Entries { get; set; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Surveys a game folder and produces a selection with heuristic defaults: everything kept,
    /// except top-level content that looks like an optional extra or a non-English language pack.
    /// </summary>
    public static SelectionManifest Build(GameCandidate game, bool applyHeuristics = true)
    {
        var root = game.InstallPath;
        var entries = new List<SelectionEntry>();

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var (size, files) = Measure(directory);
            var reason = applyHeuristics ? SelectionHeuristics.Classify(name) : null;

            entries.Add(new SelectionEntry
            {
                Path = name,
                Kind = SelectionEntryKind.Folder,
                Size = size,
                FileCount = files,
                Include = reason is null,
                Reason = reason,
            });
        }

        foreach (var file in Directory.EnumerateFiles(root).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var reason = applyHeuristics ? SelectionHeuristics.Classify(name) : null;

            entries.Add(new SelectionEntry
            {
                Path = name,
                Kind = SelectionEntryKind.File,
                Size = SafeLength(file),
                FileCount = 1,
                Include = reason is null,
                Reason = reason,
            });
        }

        return new SelectionManifest
        {
            AppId = game.AppId,
            Title = game.Name,
            InstallDir = game.InstallDir,
            BuildId = game.BuildId,
            CreatedBy = $"SteamDisc {typeof(SelectionManifest).Assembly.GetName().Version}",
            Entries = entries,
        };
    }

    /// <summary>Relative paths to leave out of the archive — feeds <c>ExcludeRelativePaths</c>.</summary>
    public IReadOnlyList<string> DeriveExclusions()
        => Entries
            .Where(e => !e.Include)
            .Select(e => e.Path.Replace('\\', '/').Trim('/'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<SelectionEntry> Excluded => Entries.Where(e => !e.Include).ToList();

    public long ExcludedBytes => Entries.Where(e => !e.Include).Sum(e => e.Size);

    public long IncludedBytes => Entries.Where(e => e.Include).Sum(e => e.Size);

    /// <summary>
    /// Checks a loaded selection against the game it will be packed from, returning problems
    /// worth surfacing before a 40-minute pack — a mismatched app, a stale build, or content
    /// added since the selection was authored (which would be packed as included, unreviewed).
    /// </summary>
    public IReadOnlyList<string> CheckAgainst(GameCandidate game)
    {
        var problems = new List<string>();

        if (AppId != game.AppId)
        {
            problems.Add($"Selection is for app {AppId} ({Title}), but the game is {game.AppId} ({game.Name}).");
            return problems; // Everything below assumes the same game.
        }

        if (BuildId != game.BuildId)
        {
            problems.Add(
                $"Selection was authored for build {BuildId}, but the game is now build {game.BuildId}. " +
                "Re-run 'select' to re-check what is optional.");
        }

        var known = new HashSet<string>(
            Entries.Select(e => e.Path.Replace('\\', '/').Trim('/').Split('/')[0]),
            StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(game.InstallPath))
        {
            var current = Directory
                .EnumerateFileSystemEntries(game.InstallPath)
                .Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 } && !known.Contains(n!))
                .ToList();

            if (current.Count > 0)
            {
                problems.Add(
                    "New content since this selection was authored, which will be packed as included: " +
                    string.Join(", ", current) + ".");
            }
        }

        return problems;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson());
    }

    public static SelectionManifest Parse(string json)
        => JsonSerializer.Deserialize<SelectionManifest>(json, SerializerOptions)
           ?? throw new InvalidDataException("Selection file deserialised to null.");

    public static SelectionManifest Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not a valid selection file: {ex.Message}", ex);
        }
    }

    private static (long Size, int Files) Measure(string directory)
    {
        long size = 0;
        var files = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            size += SafeLength(file);
            files++;
        }

        return (size, files);
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

public enum SelectionEntryKind
{
    File,
    Folder,
}

/// <summary>One keep-or-drop decision, addressing a file or a folder by path relative to the game root.</summary>
public sealed class SelectionEntry
{
    /// <summary>Forward-slash path relative to the game folder. A folder path drops its whole subtree.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public SelectionEntryKind Kind { get; set; } = SelectionEntryKind.Folder;

    /// <summary>Total bytes this entry represents — for the "space saved" figure and the tree UI.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    /// <summary>True to pack it, false to leave it off the disc.</summary>
    [JsonPropertyName("include")]
    public bool Include { get; set; } = true;

    /// <summary>Why the default is what it is, e.g. "non-English language pack (heuristic)".</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Name-based guesses at what is optional in a game folder. Deliberately conservative and always
/// marked "(heuristic)": these are suggestions the author reviews, not facts. Depot-accurate
/// classification (reading Steam's depot config and content manifests) is the tier-2 upgrade that
/// would replace these, feeding the same <see cref="SelectionManifest"/>.
/// </summary>
public static class SelectionHeuristics
{
    /// <summary>Steam language folder tokens. A top-level folder named for one of these, other than
    /// English, is very likely a language depot that not every user needs.</summary>
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "arabic", "bulgarian", "schinese", "tchinese", "chinese", "czech", "danish", "dutch",
        "finnish", "french", "german", "greek", "hungarian", "indonesian", "italian", "japanese",
        "koreana", "korean", "latam", "norwegian", "polish", "portuguese", "brazilian",
        "romanian", "russian", "spanish", "swedish", "thai", "turkish", "ukrainian", "vietnamese",
    };

    /// <summary>Fragments that suggest bundled extras rather than the game itself. Kept short to
    /// avoid false positives on core folders; generic words like "server" and "tools" are left out
    /// on purpose, since plenty of games ship those as required content.</summary>
    private static readonly string[] OptionalMarkers =
    {
        "soundtrack", "ost", "artbook", "art book", "artwork", "wallpaper", "bonus content",
        "digital deluxe", "making of", "behindthescenes", "behind the scenes",
    };

    /// <summary>Returns a human reason if the name looks non-default, or null to keep it by default.</summary>
    public static string? Classify(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();

        if (Languages.Contains(normalized) && !normalized.Equals("english", StringComparison.Ordinal))
        {
            return $"'{name}' looks like a non-English language pack (heuristic)";
        }

        foreach (var marker in OptionalMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return $"'{name}' looks like optional content (heuristic)";
            }
        }

        return null;
    }
}
