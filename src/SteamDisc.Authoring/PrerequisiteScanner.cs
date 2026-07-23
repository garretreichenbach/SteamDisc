using SteamDisc.Core.Payload;

namespace SteamDisc.Authoring;

/// <summary>
/// Finds the redistributables inside a game folder that Steam would normally run on install.
/// </summary>
/// <remarks>
/// Steam keeps these under <c>_CommonRedist</c> by convention, laid out as
/// <c>_CommonRedist/&lt;package&gt;/&lt;version&gt;/&lt;installer&gt;</c>. Detecting them at
/// authoring time means the disc carries an explicit list rather than the runtime guessing,
/// and the user can see and edit what will be executed on their machine before it happens.
/// </remarks>
public static class PrerequisiteScanner
{
    private const string RedistFolder = "_CommonRedist";

    /// <summary>Silent-install switches by package family, keyed on a folder-name fragment.</summary>
    private static readonly (string Marker, string Args)[] KnownArguments =
    {
        ("vcredist", "/quiet /norestart"),
        ("dotnet", "/q /norestart"),
        ("dxsetup", "/silent"),
        ("directx", "/silent"),
        ("xna", "/q"),
        ("physx", "/quiet /norestart"),
        ("openal", "/silent"),
        ("uplay", "/S"),
    };

    public static IReadOnlyList<PrerequisiteDescriptor> Scan(string installRoot)
    {
        var redistRoot = Path.Combine(installRoot, RedistFolder);
        if (!Directory.Exists(redistRoot))
        {
            return Array.Empty<PrerequisiteDescriptor>();
        }

        var results = new List<PrerequisiteDescriptor>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(redistRoot, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<PrerequisiteDescriptor>();
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(installRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var lower = relative.ToLowerInvariant();

            var args = KnownArguments.FirstOrDefault(k => lower.Contains(k.Marker, StringComparison.Ordinal)).Args
                       ?? (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
                           ? "/quiet /norestart"
                           : "/quiet /norestart");

            results.Add(new PrerequisiteDescriptor
            {
                Name = DescribeName(relative),
                Path = relative,
                Args = args,
                Platform = "windows",
            });
        }

        return results;
    }

    /// <summary>Turns <c>_CommonRedist/vcredist/2019/VC_redist.x64.exe</c> into "vcredist 2019 (x64)".</summary>
    private static string DescribeName(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var meaningful = segments.Skip(1).Take(2).ToArray(); // skip "_CommonRedist"

        var name = meaningful.Length > 0 ? string.Join(' ', meaningful) : Path.GetFileName(relativePath);

        var fileName = Path.GetFileNameWithoutExtension(relativePath).ToLowerInvariant();
        if (fileName.Contains("x64", StringComparison.Ordinal))
        {
            name += " (x64)";
        }
        else if (fileName.Contains("x86", StringComparison.Ordinal))
        {
            name += " (x86)";
        }

        return name;
    }
}
