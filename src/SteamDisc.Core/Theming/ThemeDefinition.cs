using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDisc.Core.Theming;

/// <summary>Built-in layouts a theme can select.</summary>
public enum ThemeLayout
{
    /// <summary>Full-bleed key art, logo, Install/Play/Exit. The 2000s retail look.</summary>
    ClassicSplash,

    /// <summary>Cover art card, progress ring, minimal chrome.</summary>
    ModernCard,

    /// <summary>Grid of covers with per-title install state, for compilation discs.</summary>
    MultiGameMenu,
}

/// <summary>
/// <c>theme.json</c> — the skin contract.
/// </summary>
/// <remarks>
/// Themes live as loose folders on the disc rather than being compiled in, so a burned disc
/// can be re-skinned by hand without rebuilding it. That means every path here is resolved at
/// runtime and every field must have a sane fallback: a theme that references a missing font
/// should look plain, never fail to boot.
/// </remarks>
public sealed class ThemeDefinition
{
    public const string FileName = "theme.json";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("layout")]
    public ThemeLayout Layout { get; set; } = ThemeLayout.ClassicSplash;

    [JsonPropertyName("colors")]
    public ThemeColors Colors { get; set; } = new();

    [JsonPropertyName("fonts")]
    public ThemeFonts Fonts { get; set; } = new();

    [JsonPropertyName("assets")]
    public ThemeAssets Assets { get; set; } = new();

    [JsonPropertyName("audio")]
    public ThemeAudio Audio { get; set; } = new();

    /// <summary>
    /// Overridable UI strings. Missing keys fall back to <see cref="ThemeStrings.Defaults"/>,
    /// so a theme only has to state what it wants to change.
    /// </summary>
    [JsonPropertyName("strings")]
    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static ThemeDefinition Parse(string json)
        => JsonSerializer.Deserialize<ThemeDefinition>(json, SerializerOptions)
           ?? throw new InvalidDataException("theme.json deserialised to null.");

    public string GetString(string key) => Strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
        ? value
        : ThemeStrings.Defaults.GetValueOrDefault(key, key);
}

/// <summary>Theme palette. Values are <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
public sealed class ThemeColors
{
    [JsonPropertyName("accent")]
    public string Accent { get; set; } = "#FF6600";

    [JsonPropertyName("bg")]
    public string Background { get; set; } = "#101014";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "#EAEAEA";

    [JsonPropertyName("textMuted")]
    public string TextMuted { get; set; } = "#9A9AA2";

    [JsonPropertyName("surface")]
    public string Surface { get; set; } = "#1B1B22";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "#E5484D";
}

public sealed class ThemeFonts
{
    /// <summary>Relative path to a font file, or the literal "system".</summary>
    [JsonPropertyName("heading")]
    public string Heading { get; set; } = "system";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "system";
}

/// <summary>
/// Artwork paths, relative to the theme folder.
/// </summary>
/// <remarks>
/// These default to <see langword="null"/> rather than to conventional file names, and that
/// matters: null is serialised by omission, so a non-null default would come back to life
/// every time a theme without a logo was read, leaving the runtime hunting for a file the
/// author never intended. A theme that leaves a slot unset instead picks up a conventionally
/// named file if one is present — see <c>Theme.ResolveConventional</c>.
/// </remarks>
public sealed class ThemeAssets
{
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

public sealed class ThemeAudio
{
    [JsonPropertyName("onLaunch")]
    public string? OnLaunch { get; set; }

    [JsonPropertyName("onComplete")]
    public string? OnComplete { get; set; }

    [JsonPropertyName("onError")]
    public string? OnError { get; set; }
}

/// <summary>Keys for <see cref="ThemeDefinition.Strings"/> and their English defaults.</summary>
public static class ThemeStrings
{
    public const string InstallButton = "installButton";
    public const string PlayButton = "playButton";
    public const string ExitButton = "exitButton";
    public const string CancelButton = "cancelButton";
    public const string WelcomeHeading = "welcomeHeading";
    public const string WelcomeBody = "welcomeBody";
    public const string ChooseLibrary = "chooseLibrary";
    public const string Installing = "installing";
    public const string Verifying = "verifying";
    public const string InsertNextDisc = "insertNextDisc";
    public const string CompleteHeading = "completeHeading";
    public const string CompleteBody = "completeBody";
    public const string ErrorHeading = "errorHeading";

    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [InstallButton] = "Install",
            [PlayButton] = "Play",
            [ExitButton] = "Exit",
            [CancelButton] = "Cancel",
            [WelcomeHeading] = "{title}",
            [WelcomeBody] = "This disc will install {title} into your Steam library.",
            [ChooseLibrary] = "Install to",
            [Installing] = "Installing {title}…",
            [Verifying] = "Verifying files…",
            [InsertNextDisc] = "Please insert disc {disc} of {discCount}.",
            [CompleteHeading] = "Installation complete",
            [CompleteBody] = "{title} is ready to play.",
            [ErrorHeading] = "Installation failed",
        };

    /// <summary>
    /// Substitutes <c>{placeholder}</c> tokens. Unknown placeholders are left alone so a
    /// typo in a hand-edited theme shows up as text rather than vanishing.
    /// </summary>
    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        var builder = new System.Text.StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                builder.Append(template[i..]);
                break;
            }

            var key = template[(i + 1)..close];
            builder.Append(values.TryGetValue(key, out var value) ? value : template[i..(close + 1)]);
            i = close;
        }

        return builder.ToString();
    }
}

/// <summary>An RGBA colour parsed from a theme.</summary>
public readonly record struct ThemeColor(byte R, byte G, byte B, byte A)
{
    public static ThemeColor Parse(string? value, ThemeColor fallback)
        => TryParse(value, out var color) ? color : fallback;

    public static bool TryParse(string? value, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('#');
        if (text.Length == 3)
        {
            // #RGB shorthand.
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        }

        if (text.Length is not (6 or 8))
        {
            return false;
        }

        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return false;
        }

        color = text.Length == 6
            ? new ThemeColor((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed, 0xFF)
            : new ThemeColor((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}" + (A == 0xFF ? string.Empty : $"{A:X2}");

    public override string ToString() => ToHex();
}
