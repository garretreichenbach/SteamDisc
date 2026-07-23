using System.Text;

namespace SteamDisc.Imaging;

/// <summary>
/// Writes the <c>autorun.inf</c> that sits in the disc root.
/// </summary>
/// <remarks>
/// Windows has not silently executed autorun payloads from removable media since Windows 7,
/// and optical media gets an AutoPlay <em>prompt</em> rather than automatic execution
/// (project plan, spike S3). So this file's real job is to give that prompt a proper name and
/// icon, and to make the "Run Setup.exe" entry the obvious choice — the UX is designed around
/// a prompt, not around auto-execution.
/// </remarks>
public static class AutorunFile
{
    public const string FileName = "autorun.inf";

    /// <param name="title">Shown in the AutoPlay dialog and as the drive label in Explorer.</param>
    /// <param name="executable">Disc-relative path of the runtime, e.g. <c>Setup.exe</c>.</param>
    /// <param name="iconPath">Disc-relative path of an .ico, or null.</param>
    public static string Build(string title, string executable = "Setup.exe", string? iconPath = null)
    {
        var builder = new StringBuilder();
        builder.Append("[autorun]\r\n");
        builder.Append("open=").Append(executable).Append("\r\n");
        builder.Append("shellexecute=").Append(executable).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            builder.Append("icon=").Append(iconPath).Append("\r\n");
        }

        builder.Append("label=").Append(Sanitise(title)).Append("\r\n");
        builder.Append("action=Install ").Append(Sanitise(title)).Append("\r\n");
        builder.Append("\r\n");
        builder.Append("[Content]\r\n");
        builder.Append("MusicFiles=false\r\n");
        builder.Append("PictureFiles=false\r\n");
        builder.Append("VideoFiles=false\r\n");
        return builder.ToString();
    }

    public static void Write(string discRoot, string title, string executable = "Setup.exe", string? iconPath = null)
    {
        // ASCII: the file is read by a very old part of Windows, and a BOM confuses it.
        File.WriteAllText(
            Path.Combine(discRoot, FileName),
            Build(title, executable, iconPath),
            Encoding.ASCII);
    }

    private static string Sanitise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            // Keep it to printable ASCII; the INF parser predates Unicode.
            builder.Append(c is >= ' ' and <= '~' and not ('=' or ';' or '[' or ']') ? c : ' ');
        }

        return builder.ToString().Trim();
    }
}
