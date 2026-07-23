namespace SteamDisc.Core.Theming;

/// <summary>
/// A theme definition bound to the folder it was loaded from, so asset paths can be resolved.
/// </summary>
public sealed class Theme
{
    private Theme(ThemeDefinition definition, string? rootPath)
    {
        Definition = definition;
        RootPath = rootPath;
    }

    public ThemeDefinition Definition { get; }

    /// <summary>Folder the theme was loaded from, or null for the built-in default.</summary>
    public string? RootPath { get; }

    public string Name => Definition.Name;

    public ThemeLayout Layout => Definition.Layout;

    /// <summary>The theme used when a disc carries none, or when its theme fails to load.</summary>
    public static Theme Default { get; } = new(new ThemeDefinition(), rootPath: null);

    public static Theme Load(string folder)
    {
        var file = Path.Combine(folder, ThemeDefinition.FileName);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"No {ThemeDefinition.FileName} in '{folder}'.", file);
        }

        return new Theme(ThemeDefinition.Parse(File.ReadAllText(file)), Path.GetFullPath(folder));
    }

    /// <summary>
    /// Loads a theme, falling back to <see cref="Default"/> on any failure. The runtime uses
    /// this: a broken skin must degrade to a plain installer, never block one.
    /// </summary>
    public static Theme LoadOrDefault(string? folder, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Default;
        }

        try
        {
            return Load(folder);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            error = ex.Message;
            return Default;
        }
    }

    /// <summary>
    /// Resolves a theme-relative asset path to an absolute one, or null when it is unset or
    /// missing on disk. Paths that try to escape the theme folder are rejected.
    /// </summary>
    public string? ResolveAsset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || RootPath is null)
        {
            return null;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return File.Exists(relativePath) ? relativePath : null;
        }

        var combined = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        var root = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(combined) ? combined : null;
    }

    public string? BackgroundPath => ResolveAsset(Definition.Assets.Background) ?? ResolveConventional("background");

    public string? LogoPath => ResolveAsset(Definition.Assets.Logo) ?? ResolveConventional("logo");

    public string? CoverPath => ResolveAsset(Definition.Assets.Cover) ?? ResolveConventional("cover");

    /// <summary>
    /// Finds a conventionally named asset when the theme does not name one explicitly, so a
    /// hand-edited disc can gain a background simply by dropping <c>background.png</c> into the
    /// theme folder.
    /// </summary>
    private string? ResolveConventional(string slot)
    {
        if (RootPath is null)
        {
            return null;
        }

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var candidate = Path.Combine(RootPath, slot + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public ThemeColor Accent => ThemeColor.Parse(Definition.Colors.Accent, new ThemeColor(0xFF, 0x66, 0x00, 0xFF));

    public ThemeColor Background => ThemeColor.Parse(Definition.Colors.Background, new ThemeColor(0x10, 0x10, 0x14, 0xFF));

    public ThemeColor Text => ThemeColor.Parse(Definition.Colors.Text, new ThemeColor(0xEA, 0xEA, 0xEA, 0xFF));

    public ThemeColor TextMuted => ThemeColor.Parse(Definition.Colors.TextMuted, new ThemeColor(0x9A, 0x9A, 0xA2, 0xFF));

    public ThemeColor Error => ThemeColor.Parse(Definition.Colors.Error, new ThemeColor(0xE5, 0x48, 0x4D, 0xFF));

    /// <summary>Looks up a UI string and substitutes placeholders.</summary>
    public string String(string key, IReadOnlyDictionary<string, string>? values = null)
    {
        var template = Definition.GetString(key);
        return values is null ? template : ThemeStrings.Format(template, values);
    }

    /// <summary>Problems that would make this theme look wrong, for the Builder's preview pane.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Definition.Name))
        {
            problems.Add("Theme has no name.");
        }

        foreach (var (label, value) in new[]
                 {
                     ("colors.accent", Definition.Colors.Accent),
                     ("colors.bg", Definition.Colors.Background),
                     ("colors.text", Definition.Colors.Text),
                 })
        {
            if (!ThemeColor.TryParse(value, out _))
            {
                problems.Add($"{label} is not a valid colour: '{value}'.");
            }
        }

        foreach (var (label, value) in new[]
                 {
                     ("assets.background", Definition.Assets.Background),
                     ("assets.logo", Definition.Assets.Logo),
                     ("assets.cover", Definition.Assets.Cover),
                 })
        {
            if (!string.IsNullOrWhiteSpace(value) && ResolveAsset(value) is null)
            {
                problems.Add($"{label} points at '{value}', which is not in the theme folder.");
            }
        }

        return problems;
    }
}
