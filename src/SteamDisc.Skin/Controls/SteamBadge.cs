using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SteamDisc.Skin.Controls;

/// <summary>
/// A small "STEAM" badge — the Steam mark plus wordmark — so a disc's installer reads at a glance
/// as belonging to Steam.
/// </summary>
/// <remarks>
/// The mark is Valve's Steam logo, drawn from its vector path and tinted with the theme's colour
/// (via <see cref="Tint"/>) so it stays crisp at any size and sits on any skin. It is a plain
/// identifier that the disc installs into Steam, not a claim of affiliation with Valve.
/// </remarks>
public sealed class SteamBadge : Control
{
    private const double Gap = 7;

    // The white Steam mark from the official icon (viewBox 0 0 64 64).
    private const string MarkPath =
        "M30.31 23.985l.003.158-7.83 11.375c-1.268-.058-2.54.165-3.748.662a8.14 8.14 0 0 0-1.498.8" +
        "L.042 29.893s-.398 6.546 1.26 11.424l12.156 5.016c.6 2.728 2.48 5.12 5.242 6.27a8.88 8.88 0 0 0 " +
        "11.603-4.782 8.89 8.89 0 0 0 .684-3.656L42.18 36.16l.275.005c6.705 0 12.155-5.466 12.155-12.18s" +
        "-5.44-12.16-12.155-12.174c-6.702 0-12.155 5.46-12.155 12.174zm-1.88 23.05c-1.454 3.5-5.466 5.147" +
        "-8.953 3.694a6.84 6.84 0 0 1-3.524-3.362l3.957 1.64a5.04 5.04 0 0 0 6.591-2.719 5.05 5.05 0 0 0" +
        "-2.715-6.601l-4.1-1.695c1.578-.6 3.372-.62 5.05.077 1.7.703 3 2.027 3.696 3.72s.692 3.56-.01 5.246" +
        "M42.466 32.1a8.12 8.12 0 0 1-8.098-8.113 8.12 8.12 0 0 1 8.098-8.111 8.12 8.12 0 0 1 8.1 8.111 8.12 " +
        "8.12 0 0 1-8.1 8.113m-6.068-8.126a6.09 6.09 0 0 1 6.08-6.095c3.355 0 6.084 2.73 6.084 6.095a6.09 " +
        "6.09 0 0 1-6.084 6.093 6.09 6.09 0 0 1-6.081-6.093z";

    private static readonly Geometry? Mark = TryParse(MarkPath);
    private static readonly Rect MarkBounds = Mark?.Bounds ?? new Rect(0, 0, 1, 1);

    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<SteamBadge, IBrush?>(nameof(Tint), Brushes.Gray);

    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<SteamBadge, double>(nameof(GlyphSize), 20d);

    public static readonly StyledProperty<double> LabelSizeProperty =
        AvaloniaProperty.Register<SteamBadge, double>(nameof(LabelSize), 12d);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SteamBadge, string>(nameof(Label), "STEAM");

    static SteamBadge()
    {
        AffectsRender<SteamBadge>(TintProperty, GlyphSizeProperty, LabelSizeProperty, LabelProperty);
        AffectsMeasure<SteamBadge>(GlyphSizeProperty, LabelSizeProperty, LabelProperty);
    }

    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public double LabelSize
    {
        get => GetValue(LabelSizeProperty);
        set => SetValue(LabelSizeProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private double GlyphWidth => MarkBounds.Height > 0 ? GlyphSize * (MarkBounds.Width / MarkBounds.Height) : GlyphSize;

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = BuildText();
        var width = GlyphWidth + (string.IsNullOrEmpty(Label) ? 0 : Gap + text.Width);
        return new Size(width, Math.Max(GlyphSize, text.Height));
    }

    public override void Render(DrawingContext context)
    {
        var tint = Tint ?? Brushes.Gray;

        if (Mark is not null && MarkBounds.Height > 0)
        {
            var scale = GlyphSize / MarkBounds.Height;
            var top = (Bounds.Height - GlyphSize) / 2;
            var transform =
                Matrix.CreateTranslation(-MarkBounds.X, -MarkBounds.Y) *
                Matrix.CreateScale(scale, scale) *
                Matrix.CreateTranslation(0, top);

            using (context.PushTransform(transform))
            {
                context.DrawGeometry(tint, null, Mark);
            }
        }

        if (!string.IsNullOrEmpty(Label))
        {
            var text = BuildText();
            context.DrawText(text, new Point(GlyphWidth + Gap, (Bounds.Height - text.Height) / 2));
        }
    }

    private FormattedText BuildText()
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        return new FormattedText(
            Label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            LabelSize,
            Tint ?? Brushes.Gray);
    }

    private static Geometry? TryParse(string path)
    {
        try
        {
            return Geometry.Parse(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
