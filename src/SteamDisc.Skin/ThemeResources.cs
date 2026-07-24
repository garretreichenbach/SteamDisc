using Avalonia.Media;
using Avalonia.Media.Imaging;
using SteamDisc.Core.Theming;

namespace SteamDisc.Skin;

/// <summary>The theme's colours resolved to Avalonia brushes, ready to bind.</summary>
/// <param name="Accent">Primary action colour — the Install button, progress fill.</param>
/// <param name="Background">Window background, behind the art.</param>
/// <param name="Surface">Panels and cards raised above the background.</param>
/// <param name="Text">Body and heading text.</param>
/// <param name="TextMuted">Secondary text — detail lines, hints.</param>
/// <param name="Error">Failure states.</param>
/// <param name="Scrim">A translucent wash over full-bleed art so text stays readable.</param>
public sealed record SkinPalette(
    IBrush Accent,
    IBrush AccentHover,
    IBrush AccentPressed,
    IBrush Background,
    IBrush Surface,
    IBrush Text,
    IBrush TextMuted,
    IBrush Error,
    IBrush Scrim);

/// <summary>
/// Bridges a Core theme to the Avalonia types the skin views bind against.
/// </summary>
/// <remarks>
/// It works from a <see cref="ThemeDefinition"/> — the colours and strings — rather than a
/// folder-bound <see cref="Theme"/>, because both a disc's theme and the Builder's built-in
/// choices are the same definition type, and neither the palette nor the copy needs the folder.
/// Artwork paths, which do need the folder, are resolved by the caller and passed in as bitmaps.
/// Fonts fall back to the app default for now — the built-in themes all ask for the system font.
/// </remarks>
public static class ThemeResources
{
    /// <summary>
    /// Theme-strings key the game's description rides in, so it travels on the disc with the rest
    /// of the skin and the runtime picks it up with no extra plumbing.
    /// </summary>
    public const string GameDescriptionKey = "gameDescription";

    /// <summary>Theme-strings key for the optional "may need updates" caution.</summary>
    public const string UpdateNoticeKey = "updateNotice";

    private static readonly ThemeColor DefaultAccent = new(0xFF, 0x66, 0x00, 0xFF);
    private static readonly ThemeColor DefaultBackground = new(0x10, 0x10, 0x14, 0xFF);
    private static readonly ThemeColor DefaultSurface = new(0x1B, 0x1B, 0x22, 0xFF);
    private static readonly ThemeColor DefaultText = new(0xEA, 0xEA, 0xEA, 0xFF);
    private static readonly ThemeColor DefaultTextMuted = new(0x9A, 0x9A, 0xA2, 0xFF);
    private static readonly ThemeColor DefaultError = new(0xE5, 0x48, 0x4D, 0xFF);

    public static SkinPalette PaletteFor(ThemeDefinition definition)
    {
        var colors = definition.Colors;
        var background = ThemeColor.Parse(colors.Background, DefaultBackground);
        var accent = ThemeColor.Parse(colors.Accent, DefaultAccent);

        return new SkinPalette(
            Accent: ToBrush(accent),
            AccentHover: ToBrush(Shade(accent, 0.18)),
            AccentPressed: ToBrush(Shade(accent, -0.18)),
            Background: ToBrush(background),
            Surface: ToBrush(ThemeColor.Parse(colors.Surface, DefaultSurface)),
            Text: ToBrush(ThemeColor.Parse(colors.Text, DefaultText)),
            TextMuted: ToBrush(ThemeColor.Parse(colors.TextMuted, DefaultTextMuted)),
            Error: ToBrush(ThemeColor.Parse(colors.Error, DefaultError)),
            Scrim: ScrimFor(background));
    }

    /// <summary>Copies a theme's palette and strings onto a view model.</summary>
    /// <param name="tokens">Placeholder values for string substitution — <c>title</c> and the like.</param>
    public static void Apply(
        SkinnedInstallerViewModel viewModel,
        ThemeDefinition definition,
        IReadOnlyDictionary<string, string> tokens)
    {
        var palette = PaletteFor(definition);
        viewModel.AccentBrush = palette.Accent;
        viewModel.AccentHoverBrush = palette.AccentHover;
        viewModel.AccentPressedBrush = palette.AccentPressed;
        viewModel.BackgroundBrush = palette.Background;
        viewModel.SurfaceBrush = palette.Surface;
        viewModel.TextBrush = palette.Text;
        viewModel.TextMutedBrush = palette.TextMuted;
        viewModel.ErrorBrush = palette.Error;
        viewModel.ScrimBrush = palette.Scrim;

        // The description is game metadata carried in the theme strings, not a themed UI string,
        // so it is read directly rather than through the defaults-backed lookup.
        viewModel.Description = definition.Strings.TryGetValue(GameDescriptionKey, out var description)
            ? description
            : string.Empty;

        viewModel.UpdateNotice = definition.Strings.TryGetValue(UpdateNoticeKey, out var notice)
            ? notice
            : string.Empty;

        viewModel.WelcomeBody = Localise(definition, ThemeStrings.WelcomeBody, tokens);
        viewModel.PrimaryButtonText = Localise(definition, ThemeStrings.InstallButton, tokens);
        viewModel.ExitButtonText = Localise(definition, ThemeStrings.ExitButton, tokens);
        viewModel.CancelButtonText = Localise(definition, ThemeStrings.CancelButton, tokens);
        viewModel.CompleteHeading = Localise(definition, ThemeStrings.CompleteHeading, tokens);
        viewModel.CompleteBody = Localise(definition, ThemeStrings.CompleteBody, tokens);
        viewModel.ErrorHeading = Localise(definition, ThemeStrings.ErrorHeading, tokens);
    }

    /// <summary>
    /// Loads an image from a path into a bitmap, or returns null when the path is empty, missing,
    /// or not a decodable image. A broken skin must never stop the installer from rendering.
    /// </summary>
    public static Bitmap? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Localise(ThemeDefinition definition, string key, IReadOnlyDictionary<string, string> tokens)
        => ThemeStrings.Format(definition.GetString(key), tokens);

    private static SolidColorBrush ToBrush(ThemeColor color)
        => new(Color.FromArgb(color.A, color.R, color.G, color.B));

    /// <summary>Lightens (positive amount) or darkens (negative) a colour, for hover states.</summary>
    private static ThemeColor Shade(ThemeColor color, double amount)
    {
        static byte Channel(byte value, double amount) => (byte)Math.Clamp(
            amount >= 0 ? value + ((255 - value) * amount) : value * (1 + amount), 0, 255);

        return new ThemeColor(
            Channel(color.R, amount),
            Channel(color.G, amount),
            Channel(color.B, amount),
            color.A);
    }

    /// <summary>A ~70% wash in the background colour, for legibility over full-bleed art.</summary>
    private static SolidColorBrush ScrimFor(ThemeColor background)
        => new(Color.FromArgb(0xB4, background.R, background.G, background.B));
}
