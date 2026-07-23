using System.Globalization;

namespace SteamDisc.Covers;

/// <summary>
/// Print geometry in millimetres. Everything in the cover system is expressed in real-world
/// units and only converted to pixels or PDF points at the very edge, because a cover that is
/// 3 mm too wide is a cover that does not fit the case.
/// </summary>
public readonly record struct SizeMm(double Width, double Height)
{
    public static SizeMm Zero => new(0, 0);

    public double Area => Width * Height;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Width:0.##} × {Height:0.##} mm");
}

/// <summary>A rectangle in millimetres, measured from the top-left of the page.</summary>
public readonly record struct RectMm(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public SizeMm Size => new(Width, Height);

    public double AspectRatio => Height > 0 ? Width / Height : 0;

    /// <summary>Grows the rectangle by <paramref name="amount"/> on every side.</summary>
    public RectMm Inflate(double amount)
        => new(X - amount, Y - amount, Width + (amount * 2), Height + (amount * 2));

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##}) {Width:0.##} × {Height:0.##} mm");
}

/// <summary>Unit conversions. PDF works in points; printers and templates work in millimetres.</summary>
public static class PrintUnits
{
    public const double PointsPerInch = 72.0;

    public const double MillimetresPerInch = 25.4;

    public static double MmToPoints(double mm) => mm / MillimetresPerInch * PointsPerInch;

    public static double PointsToMm(double points) => points / PointsPerInch * MillimetresPerInch;

    public static double MmToPixels(double mm, double dpi) => mm / MillimetresPerInch * dpi;

    public static double PixelsToMm(double pixels, double dpi) => pixels / dpi * MillimetresPerInch;

    /// <summary>
    /// The effective resolution an image would be printed at in a given slot. Below roughly
    /// 200 DPI a cover looks soft; the Builder uses this to warn before anything is printed.
    /// </summary>
    public static double EffectiveDpi(int pixels, double millimetres)
        => millimetres > 0 ? pixels / (millimetres / MillimetresPerInch) : 0;
}
