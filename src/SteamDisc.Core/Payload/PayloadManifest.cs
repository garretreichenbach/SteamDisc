using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Core.Payload;

/// <summary>
/// <c>payload.json</c> — the root manifest sitting in the disc root. This is the contract
/// between the Builder and the disc Runtime, and the one file the Runtime must be able to
/// read before it knows anything else.
/// </summary>
public sealed class PayloadManifest
{
    /// <summary>
    /// Bumped only for breaking changes. The Runtime refuses anything above
    /// <see cref="SupportedFormatVersion"/> with a clear message rather than guessing.
    /// </summary>
    public const int SupportedFormatVersion = 1;

    public const string FileName = "payload.json";

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = SupportedFormatVersion;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    /// <summary>Folder name under <c>steamapps/common</c> that the payload extracts to.</summary>
    [JsonPropertyName("installDir")]
    public string InstallDir { get; set; } = string.Empty;

    [JsonPropertyName("buildId")]
    public long BuildId { get; set; }

    /// <summary>Uncompressed size of the game folder, used for free-space checks and estimates.</summary>
    [JsonPropertyName("sizeOnDisk")]
    public long SizeOnDisk { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Tool version that authored the disc, for support triage.</summary>
    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = "SteamDisc";

    [JsonPropertyName("disc")]
    public DiscDescriptor Disc { get; set; } = new();

    [JsonPropertyName("archive")]
    public ArchiveDescriptor Archive { get; set; } = new();

    /// <summary>Relative path of the transplanted <c>appmanifest_&lt;appid&gt;.acf</c>.</summary>
    [JsonPropertyName("appManifestPath")]
    public string AppManifestPath { get; set; } = string.Empty;

    /// <summary>Relative path of the theme folder, or null for the built-in default theme.</summary>
    [JsonPropertyName("themePath")]
    public string? ThemePath { get; set; }

    [JsonPropertyName("prerequisites")]
    public List<PrerequisiteDescriptor> Prerequisites { get; set; } = new();

    [JsonPropertyName("postInstall")]
    public PostInstallOptions PostInstall { get; set; } = new();

    /// <summary>
    /// Authoring-time warnings surfaced to the user before they commit to an install —
    /// third-party DRM, live-service titles, anything that will phone home regardless.
    /// </summary>
    [JsonPropertyName("advisories")]
    public List<Advisory> Advisories { get; set; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
    }

    public static PayloadManifest Parse(string json)
        => JsonSerializer.Deserialize<PayloadManifest>(json, SerializerOptions)
           ?? throw new InvalidDataException("payload.json deserialised to null.");

    public static PayloadManifest Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not a valid payload manifest: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Structural checks that must pass before the Runtime acts on a manifest. Returns the
    /// problems found rather than throwing, so the UI can show all of them at once.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (FormatVersion <= 0)
        {
            problems.Add("formatVersion must be positive.");
        }
        else if (FormatVersion > SupportedFormatVersion)
        {
            problems.Add(
                $"This disc was authored with payload format v{FormatVersion}, but this installer " +
                $"understands up to v{SupportedFormatVersion}. Use a newer installer.");
        }

        if (AppId == 0)
        {
            problems.Add("appId must be set.");
        }

        if (string.IsNullOrWhiteSpace(InstallDir))
        {
            problems.Add("installDir must be set.");
        }
        else if (InstallDir.Intersect(Path.GetInvalidFileNameChars()).Any() ||
                 InstallDir is "." or ".." ||
                 Path.IsPathRooted(InstallDir))
        {
            // installDir is joined onto the user's library path; refuse anything that could escape it.
            problems.Add($"installDir '{InstallDir}' is not a plain folder name.");
        }

        if (SizeOnDisk < 0)
        {
            problems.Add("sizeOnDisk cannot be negative.");
        }

        problems.AddRange(Disc.Validate());
        problems.AddRange(Archive.Validate());

        return problems;
    }
}

/// <summary>Where this disc sits in a multi-disc set.</summary>
public sealed class DiscDescriptor
{
    [JsonPropertyName("number")]
    public int Number { get; set; } = 1;

    [JsonPropertyName("of")]
    public int Of { get; set; } = 1;

    /// <summary>
    /// Identifies the set. The Runtime refuses a swapped-in disc whose set id does not match,
    /// which is exactly the failure mode Steam's own multi-disc restore is known for.
    /// </summary>
    [JsonPropertyName("setId")]
    public string SetId { get; set; } = Guid.NewGuid().ToString("D");

    /// <summary>Human-readable label burned onto the disc, e.g. "Portal 2 — Disc 1 of 2".</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    public bool IsSingleDisc => Of <= 1;

    public IEnumerable<string> Validate()
    {
        if (Number < 1)
        {
            yield return "disc.number must be 1 or greater.";
        }

        if (Of < 1)
        {
            yield return "disc.of must be 1 or greater.";
        }

        if (Number > Of)
        {
            yield return $"disc.number ({Number}) cannot exceed disc.of ({Of}).";
        }

        if (!Guid.TryParse(SetId, out _))
        {
            yield return "disc.setId must be a GUID.";
        }
    }
}

/// <summary>How the game folder was packed.</summary>
public sealed class ArchiveDescriptor
{
    /// <summary>Engine id — see <c>SteamDisc.Core.Archive.ArchiveFormats</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "sdz";

    /// <summary>Base name of the volume files relative to the disc root, without the volume suffix.</summary>
    [JsonPropertyName("baseName")]
    public string BaseName { get; set; } = "data/payload";

    /// <summary>Every volume in the set, across every disc, in order.</summary>
    [JsonPropertyName("volumes")]
    public List<VolumeDescriptor> Volumes { get; set; } = new();

    /// <summary>Relative path of the sha256 sidecar covering the volumes.</summary>
    [JsonPropertyName("hashFile")]
    public string? HashFile { get; set; }

    /// <summary>Total compressed size across all volumes.</summary>
    [JsonPropertyName("compressedSize")]
    public long CompressedSize { get; set; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Format))
        {
            yield return "archive.format must be set.";
        }

        if (Volumes.Count == 0)
        {
            yield return "archive.volumes must list at least one volume.";
        }

        for (var i = 0; i < Volumes.Count; i++)
        {
            if (Volumes[i].Index != i + 1)
            {
                yield return $"archive.volumes[{i}] has index {Volumes[i].Index}; volumes must be numbered from 1 in order.";
            }

            if (string.IsNullOrWhiteSpace(Volumes[i].Path))
            {
                yield return $"archive.volumes[{i}] has no path.";
            }
        }
    }
}

/// <summary>One split volume of the payload archive.</summary>
public sealed class VolumeDescriptor
{
    /// <summary>1-based volume number.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Path relative to the disc root, e.g. <c>data/payload.sdz.001</c>.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>Which disc of the set carries this volume.</summary>
    [JsonPropertyName("disc")]
    public int Disc { get; set; } = 1;
}

/// <summary>A redistributable to run before the game is considered installed.</summary>
public sealed class PrerequisiteDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Path relative to the extracted game folder, e.g. <c>_CommonRedist/vcredist/...</c>.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public string Args { get; set; } = "/quiet /norestart";

    /// <summary>Exit codes to treat as success beyond 0 (3010 = success, reboot required).</summary>
    [JsonPropertyName("successExitCodes")]
    public List<int> SuccessExitCodes { get; set; } = new() { 0, 1638, 3010 };

    /// <summary>Windows-only by nature; kept explicit so other platforms skip rather than fail.</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows";
}

/// <summary>What the Runtime does once files are in place.</summary>
public sealed class PostInstallOptions
{
    /// <summary>
    /// Path C: ask Steam to verify the install afterwards. Slower, but self-healing when the
    /// transplanted manifest is imperfect.
    /// </summary>
    [JsonPropertyName("validate")]
    public bool Validate { get; set; }

    [JsonPropertyName("launch")]
    public bool Launch { get; set; } = true;

    /// <summary>Offer to restart the Steam client so it picks up the new manifest.</summary>
    [JsonPropertyName("restartSteam")]
    public bool RestartSteam { get; set; } = true;
}

public enum AdvisorySeverity
{
    Info,
    Warning,
}

/// <summary>A caveat detected at authoring time and shown before install.</summary>
public sealed class Advisory
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public AdvisorySeverity Severity { get; set; } = AdvisorySeverity.Info;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    public static Advisory Warning(string code, string message)
        => new() { Code = code, Severity = AdvisorySeverity.Warning, Message = message };

    public static Advisory Info(string code, string message)
        => new() { Code = code, Severity = AdvisorySeverity.Info, Message = message };
}
