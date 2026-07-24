namespace SteamDisc.Core.Theming;

/// <summary>
/// The themes shipped with the Builder, as starting points a user can copy and edit.
/// </summary>
/// <remarks>
/// These are definitions only — no artwork. The art comes from whatever the user picked in the
/// art picker, which is why a theme folder is written at build time rather than shipped whole.
/// </remarks>
public static class BuiltInThemes
{
    public static ThemeDefinition ValveRetail2011() => new()
    {
        Name = "Valve Retail 2011",
        Author = "SteamDisc",
        Layout = ThemeLayout.ClassicSplash,
        Colors = new ThemeColors
        {
            Accent = "#FF6600",
            Background = "#101014",
            Text = "#EAEAEA",
            TextMuted = "#9A9AA2",
            Surface = "#1B1B22",
        },
        Strings =
        {
            [ThemeStrings.InstallButton] = "Install",
            [ThemeStrings.PlayButton] = "Play",
            [ThemeStrings.WelcomeBody] = "{title} will be added to your Steam library.",
        },
    };

    public static ThemeDefinition ModernCard() => new()
    {
        Name = "Modern Card",
        Author = "SteamDisc",
        Layout = ThemeLayout.ModernCard,
        Colors = new ThemeColors
        {
            Accent = "#4C9AFF",
            Background = "#0B0D10",
            Text = "#F2F4F7",
            TextMuted = "#8A9099",
            Surface = "#161A20",
        },
    };

    public static ThemeDefinition Compilation() => new()
    {
        Name = "Compilation",
        Author = "SteamDisc",
        Layout = ThemeLayout.MultiGameMenu,
        Colors = new ThemeColors
        {
            Accent = "#C8A24A",
            Background = "#14110D",
            Text = "#F0E9DC",
            TextMuted = "#9C9385",
            Surface = "#1F1A14",
        },
        Strings =
        {
            [ThemeStrings.WelcomeHeading] = "Choose a title",
        },
    };

    /// <summary>Every built-in theme, keyed by a stable id usable on a command line.</summary>
    public static IReadOnlyDictionary<string, Func<ThemeDefinition>> All { get; } =
        new Dictionary<string, Func<ThemeDefinition>>(StringComparer.OrdinalIgnoreCase)
        {
            ["classic"] = ValveRetail2011,
            ["modern"] = ModernCard,
            ["compilation"] = Compilation,
        };

    public static ThemeDefinition? Get(string id)
        => All.TryGetValue(id, out var factory) ? factory() : null;

    /// <summary>
    /// Writes a theme folder: the definition plus whatever art the caller supplies, copied in
    /// under the names the definition expects.
    /// </summary>
    /// <param name="artwork">Maps an asset slot ("background", "logo", "cover") to a source file.</param>
    public static void WriteThemeFolder(
        ThemeDefinition definition,
        string destination,
        IReadOnlyDictionary<string, string>? artwork = null)
    {
        Directory.CreateDirectory(destination);

        if (artwork is not null)
        {
            foreach (var (slot, sourcePath) in artwork)
            {
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var extension = Path.GetExtension(sourcePath);
                var fileName = slot.ToLowerInvariant() + extension;
                File.Copy(sourcePath, Path.Combine(destination, fileName), overwrite: true);

                switch (slot.ToLowerInvariant())
                {
                    case "background":
                        definition.Assets.Background = fileName;
                        break;
                    case "logo":
                        definition.Assets.Logo = fileName;
                        break;
                    case "cover":
                        definition.Assets.Cover = fileName;
                        break;
                    case "icon":
                        definition.Assets.Icon = fileName;
                        break;
                }
            }
        }

        // Drop asset references the folder cannot satisfy, so the runtime does not log
        // "missing asset" for slots the author never intended to fill.
        var root = destination;
        definition.Assets.Background = KeepIfPresent(root, definition.Assets.Background);
        definition.Assets.Logo = KeepIfPresent(root, definition.Assets.Logo);
        definition.Assets.Cover = KeepIfPresent(root, definition.Assets.Cover);
        definition.Assets.Icon = KeepIfPresent(root, definition.Assets.Icon);

        File.WriteAllText(Path.Combine(destination, ThemeDefinition.FileName), definition.ToJson());
    }

    private static string? KeepIfPresent(string root, string? relative)
        => !string.IsNullOrWhiteSpace(relative) && File.Exists(Path.Combine(root, relative)) ? relative : null;
}
