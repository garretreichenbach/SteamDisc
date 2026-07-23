using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Covers;

/// <summary>The physical thing a template prints onto.</summary>
public enum CoverMedia
{
    /// <summary>Printed directly onto a 120 mm disc, or onto a circular label.</summary>
    DiscLabel,

    /// <summary>Standard CD jewel case — separate front booklet and back tray card.</summary>
    JewelCase,

    /// <summary>DVD keep case wrap: back, spine and front in one sheet.</summary>
    DvdCase,

    /// <summary>Blu-ray keep case wrap.</summary>
    BluRayCase,

    /// <summary>A single inner panel, such as a Blu-ray case insert.</summary>
    Insert,

    /// <summary>Anything imported that does not map onto the above.</summary>
    Custom,
}

/// <summary>How art is fitted into a slot.</summary>
public enum SlotFit
{
    /// <summary>Fill the slot, cropping the overflow. The right default for key art.</summary>
    Cover,

    /// <summary>Fit entirely inside the slot, leaving bars. The right default for logos.</summary>
    Contain,

    /// <summary>Ignore the aspect ratio and stretch. Rarely what anyone wants.</summary>
    Stretch,
}

/// <summary>A region of a template that art or text goes into, in page coordinates.</summary>
public sealed class CoverSlot
{
    /// <summary>Stable id used to bind artwork, e.g. <c>front</c>, <c>spine</c>, <c>back</c>, <c>label</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Position and size in millimetres from the top-left of the page.</summary>
    [JsonPropertyName("bounds")]
    public RectMm Bounds { get; set; }

    [JsonPropertyName("fit")]
    public SlotFit Fit { get; set; } = SlotFit.Cover;

    /// <summary>Clockwise rotation in degrees. Spines are usually 90 or 270.</summary>
    [JsonPropertyName("rotation")]
    public double Rotation { get; set; }

    /// <summary>Ideal aspect ratio, used to pick the best matching art from a provider.</summary>
    [JsonPropertyName("preferredAspect")]
    public double? PreferredAspect { get; set; }

    /// <summary>True for a circular slot, i.e. a disc label.</summary>
    [JsonPropertyName("circular")]
    public bool Circular { get; set; }

    /// <summary>Inner hole diameter in millimetres, for circular slots.</summary>
    [JsonPropertyName("innerDiameter")]
    public double InnerDiameter { get; set; }
}

/// <summary>A text field baked into the template, such as a title or a spine caption.</summary>
public sealed class CoverTextField
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("bounds")]
    public RectMm Bounds { get; set; }

    /// <summary>Default text; <c>{title}</c> and <c>{appId}</c> are substituted at render time.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("fontSize")]
    public double FontSize { get; set; } = 12;

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFF";

    [JsonPropertyName("rotation")]
    public double Rotation { get; set; }

    [JsonPropertyName("align")]
    public string Align { get; set; } = "left";
}

/// <summary>
/// A printable cover template: a page, a trim box within it, and the slots art drops into.
/// </summary>
/// <remarks>
/// <para>
/// Geometry is page-first rather than trim-first, because that is the shape real templates
/// come in. A downloaded design is typically a full US Letter sheet at 300 DPI with the cover
/// inset in the middle, surrounded by registration marks, a site credit and legal text that
/// its licence requires be left intact. Modelling only the trim area could not represent that.
/// </para>
/// <para>
/// An <see cref="OverlayPath"/> composites the template's own artwork on top of the user's, so
/// a downloaded design keeps its frame, branding and legal text while the key art underneath
/// is the user's own.
/// </para>
/// </remarks>
public sealed class CoverTemplate
{
    public const string FileName = "template.json";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Design family, e.g. "Steam", "Classic", "Modern". Used to group the picker.</summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = "Custom";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>Where the template came from, kept so a printed cover can be attributed.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Licence or usage terms the template came with, shown before printing.</summary>
    [JsonPropertyName("terms")]
    public string? Terms { get; set; }

    [JsonPropertyName("media")]
    public CoverMedia Media { get; set; } = CoverMedia.Custom;

    /// <summary>The whole printed sheet.</summary>
    [JsonPropertyName("page")]
    public SizeMm Page { get; set; }

    /// <summary>Where the finished cover is cut out of the page.</summary>
    [JsonPropertyName("trim")]
    public RectMm Trim { get; set; }

    /// <summary>
    /// How far artwork extends beyond <see cref="Trim"/>, so a slightly off cut does not
    /// expose white paper at the edge.
    /// </summary>
    [JsonPropertyName("bleed")]
    public double Bleed { get; set; } = 3;

    /// <summary>
    /// Whether to draw our own crop marks. False for templates that already carry
    /// registration marks of their own — two sets would be worse than none.
    /// </summary>
    [JsonPropertyName("drawCropMarks")]
    public bool DrawCropMarks { get; set; } = true;

    /// <summary>Resolution the template artwork was authored at, when known.</summary>
    [JsonPropertyName("sourceDpi")]
    public double? SourceDpi { get; set; }

    [JsonPropertyName("slots")]
    public List<CoverSlot> Slots { get; set; } = new();

    [JsonPropertyName("textFields")]
    public List<CoverTextField> TextFields { get; set; } = new();

    /// <summary>Template artwork composited over the user's art, relative to the template folder.</summary>
    [JsonPropertyName("overlay")]
    public string? OverlayPath { get; set; }

    /// <summary>Artwork composited underneath everything, relative to the template folder.</summary>
    [JsonPropertyName("background")]
    public string? BackgroundPath { get; set; }

    /// <summary>Folder the template was loaded from; not serialised.</summary>
    [JsonIgnore]
    public string? RootPath { get; set; }

    /// <summary>Trim box grown by the bleed on every side — the area art should cover.</summary>
    [JsonIgnore]
    public RectMm BleedBox => Trim.Inflate(Bleed);

    public CoverSlot? FindSlot(string id)
        => Slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public string? ResolveAsset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || RootPath is null)
        {
            return null;
        }

        var combined = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        var root = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        return combined.StartsWith(root, StringComparison.Ordinal) && File.Exists(combined) ? combined : null;
    }

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static CoverTemplate Load(string path)
    {
        var template = JsonSerializer.Deserialize<CoverTemplate>(File.ReadAllText(path), SerializerOptions)
                       ?? throw new InvalidDataException($"'{path}' is not a valid cover template.");
        template.RootPath = Path.GetDirectoryName(Path.GetFullPath(path));
        return template;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson());
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            problems.Add("Template has no id.");
        }

        if (Page.Width <= 0 || Page.Height <= 0)
        {
            problems.Add("Template page size must be positive.");
        }

        if (Trim.Width <= 0 || Trim.Height <= 0)
        {
            problems.Add("Template trim size must be positive.");
        }

        if (Trim.X < -0.01 || Trim.Y < -0.01 ||
            Trim.Right > Page.Width + 0.01 || Trim.Bottom > Page.Height + 0.01)
        {
            problems.Add("The trim box falls outside the page.");
        }

        if (Bleed < 0)
        {
            problems.Add("Bleed cannot be negative.");
        }

        foreach (var slot in Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Id))
            {
                problems.Add("A slot has no id.");
            }

            if (slot.Bounds.Width <= 0 || slot.Bounds.Height <= 0)
            {
                problems.Add($"Slot '{slot.Id}' has a zero or negative size.");
            }
        }

        if (Slots.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Slots.Count)
        {
            problems.Add("Slot ids must be unique.");
        }

        return problems;
    }
}
