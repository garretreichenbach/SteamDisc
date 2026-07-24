using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SteamDisc.Skin.Controls;

/// <summary>
/// A circular progress indicator drawn directly, so it needs no template and no animation clock.
/// </summary>
/// <remarks>
/// The Modern Card layout's identity is this ring. It is rendered with a plain
/// <see cref="DrawingContext"/> rather than a stack of shapes: a track ellipse plus a single arc
/// swept to <see cref="Value"/>. Indeterminate state shows a fixed quarter arc — honest enough as
/// "busy" without pulling in an animation just for the brief Preparing phase.
/// </remarks>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(RingThickness), 12);

    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(RingBrush), Brushes.OrangeRed);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsIndeterminate));

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(
            ValueProperty,
            RingThicknessProperty,
            RingBrushProperty,
            TrackBrushProperty,
            IsIndeterminateProperty);
    }

    /// <summary>Progress in 0..1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var extent = Math.Min(Bounds.Width, Bounds.Height);
        var thickness = RingThickness;
        if (extent <= thickness)
        {
            return;
        }

        var radius = (extent - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        }

        var fraction = IsIndeterminate ? 0.25 : Math.Clamp(Value, 0, 1);
        if (fraction <= 0 || RingBrush is not { } ring)
        {
            return;
        }

        if (fraction >= 1)
        {
            context.DrawEllipse(null, new Pen(ring, thickness), center, radius, radius);
            return;
        }

        const double start = -Math.PI / 2; // 12 o'clock
        var sweep = fraction * 2 * Math.PI;
        var startPoint = new Point(center.X + (radius * Math.Cos(start)), center.Y + (radius * Math.Sin(start)));
        var endAngle = start + sweep;
        var endPoint = new Point(center.X + (radius * Math.Cos(endAngle)), center.Y + (radius * Math.Sin(endAngle)));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(startPoint, isFilled: false);
            ctx.ArcTo(endPoint, new Size(radius, radius), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(ring, thickness) { LineCap = PenLineCap.Round }, geometry);
    }
}
