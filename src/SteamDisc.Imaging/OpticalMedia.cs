using System.Globalization;

namespace SteamDisc.Imaging;

/// <summary>Capacities of the optical media the Builder can target.</summary>
/// <remarks>
/// Values are the usable data capacity as reported by drives, not the marketing figure.
/// The plan's arithmetic depends on these being honest: a spanning plan that overflows by
/// 40 MB is discovered at burn time, which is the worst possible moment.
/// </remarks>
public sealed record OpticalMedium(string Id, string Name, long CapacityBytes)
{
    public static OpticalMedium Cd => new("cd", "CD-R (700 MB)", 703_512_576L);

    public static OpticalMedium Dvd => new("dvd", "DVD±R (4.7 GB)", 4_700_372_992L);

    public static OpticalMedium DvdDl => new("dvd-dl", "DVD±R DL (8.5 GB)", 8_547_991_552L);

    public static OpticalMedium BluRay => new("bd-r", "BD-R (25 GB)", 25_025_314_816L);

    public static OpticalMedium BluRayDl => new("bd-r-dl", "BD-R DL (50 GB)", 50_050_629_632L);

    public static OpticalMedium BluRayXl100 => new("bd-r-xl", "BD-R XL (100 GB)", 100_103_356_416L);

    public static OpticalMedium BluRayXl128 => new("bd-r-xl-128", "BD-R XL (128 GB)", 128_001_769_472L);

    /// <summary>A virtual medium for "one image, however large", used for ISO-only output.</summary>
    public static OpticalMedium Unlimited => new("unlimited", "No limit (image only)", long.MaxValue);

    public static IReadOnlyList<OpticalMedium> All { get; } = new[]
    {
        Cd, Dvd, DvdDl, BluRay, BluRayDl, BluRayXl100, BluRayXl128, Unlimited,
    };

    public static OpticalMedium? Find(string id)
        => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Space left for payload volumes once the fixed disc overhead is accounted for — the
    /// runtime executable, theme, manifests and the ISO's own structures.
    /// </summary>
    public long UsableForPayload(long overheadBytes)
        => CapacityBytes == long.MaxValue ? long.MaxValue : Math.Max(0, CapacityBytes - overheadBytes);

    public override string ToString()
        => CapacityBytes == long.MaxValue
            ? Name
            : string.Create(CultureInfo.InvariantCulture, $"{Name} — {CapacityBytes:N0} bytes");
}
