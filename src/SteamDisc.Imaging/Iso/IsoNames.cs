using System.Text;

namespace SteamDisc.Imaging.Iso;

/// <summary>
/// Name mangling for the two directory trees an image carries.
/// </summary>
/// <remarks>
/// ISO 9660 identifiers are restricted to A–Z, 0–9 and underscore, and files carry a
/// <c>;1</c> version suffix. Joliet then supplies the real names in UCS-2, and that is the
/// tree Windows actually reads. Both trees describe the same file data, so a disc stays
/// readable on anything that can mount ISO 9660 at all.
/// </remarks>
internal static class IsoNames
{
    /// <summary>ISO 9660 Level 2 allows 31 characters, including the version suffix.</summary>
    public const int MaxIsoIdentifierLength = 31;

    /// <summary>Joliet allows 64 UCS-2 characters per component.</summary>
    public const int MaxJolietLength = 64;

    public static string ToIsoFileName(string name, int uniqueSuffix)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        var cleanStem = Sanitise(stem);
        var cleanExtension = Sanitise(extension.TrimStart('.'));

        if (cleanStem.Length == 0)
        {
            cleanStem = "FILE";
        }

        // Reserve room for the ";1" version and, when needed, a "~N" disambiguator.
        var suffix = uniqueSuffix > 0 ? "~" + uniqueSuffix.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var budget = MaxIsoIdentifierLength - 2 - suffix.Length - (cleanExtension.Length > 0 ? cleanExtension.Length + 1 : 0);
        budget = Math.Max(1, budget);

        if (cleanStem.Length > budget)
        {
            cleanStem = cleanStem[..budget];
        }

        var result = cleanStem + suffix;
        if (cleanExtension.Length > 0)
        {
            if (cleanExtension.Length > 3 + 12)
            {
                cleanExtension = cleanExtension[..3];
            }

            result += "." + cleanExtension;
        }

        return result + ";1";
    }

    public static string ToIsoDirectoryName(string name, int uniqueSuffix)
    {
        var clean = Sanitise(name);
        if (clean.Length == 0)
        {
            clean = "DIR";
        }

        var suffix = uniqueSuffix > 0 ? "~" + uniqueSuffix.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var budget = Math.Max(1, MaxIsoIdentifierLength - suffix.Length);
        if (clean.Length > budget)
        {
            clean = clean[..budget];
        }

        return clean + suffix;
    }

    public static string ToJolietName(string name)
        => name.Length <= MaxJolietLength ? name : name[..MaxJolietLength];

    /// <summary>Uppercases and replaces every character ISO 9660 does not allow.</summary>
    private static string Sanitise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.ToUpperInvariant())
        {
            builder.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_');
        }

        return builder.ToString();
    }

    /// <summary>ISO 9660 sorts identifiers as byte strings padded with spaces.</summary>
    public static int CompareIsoIdentifiers(string left, string right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < left.Length ? left[i] : ' ';
            var b = i < right.Length ? right[i] : ' ';
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>Joliet sorts on the big-endian UCS-2 encoding of the name.</summary>
    public static int CompareJolietIdentifiers(string left, string right)
    {
        var a = Encoding.BigEndianUnicode.GetBytes(left);
        var b = Encoding.BigEndianUnicode.GetBytes(right);
        var length = Math.Min(a.Length, b.Length);

        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] < b[i] ? -1 : 1;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
