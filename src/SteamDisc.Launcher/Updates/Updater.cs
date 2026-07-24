using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace SteamDisc.Launcher.Updates;

/// <summary>
/// Downloads a release archive and swaps it over the running install.
/// </summary>
/// <remarks>
/// A process cannot replace its own executable while it is running, so the last step hands off
/// to a tiny throwaway script: it waits for us to exit, copies the new files over the install
/// folder, relaunches the launcher, and deletes itself. Everything is staged in a temp folder
/// first, so a failed download never touches a working install.
/// </remarks>
public sealed class Updater
{
    private readonly HttpClient _http;

    public Updater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDisc-Updater");
        }
    }

    /// <summary>Downloads the release archive, reporting 0..1 progress when the size is known.</summary>
    public async Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stagingRoot = CreateStagingRoot();
        var archivePath = Path.Combine(stagingRoot, update.AssetName);

        using var response = await _http
            .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(archivePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (total is > 0)
            {
                progress?.Report(Math.Clamp((double)written / total.Value, 0, 1));
            }
        }

        return archivePath;
    }

    /// <summary>
    /// Extracts the archive, then starts the swap script and returns. The caller should exit
    /// immediately afterwards so the script can replace the files.
    /// </summary>
    public void ApplyAndRestart(string archivePath)
    {
        var stagingRoot = Path.GetDirectoryName(archivePath)
                          ?? throw new InvalidOperationException("Downloaded archive has no folder.");

        var payload = Path.Combine(stagingRoot, "payload");
        Directory.CreateDirectory(payload);
        Extract(archivePath, payload);

        // Bundles carry a single top-level folder; step into it so we copy the files, not the folder.
        var root = payload;
        if (Directory.GetFiles(payload).Length == 0 && Directory.GetDirectories(payload) is { Length: 1 } only)
        {
            root = only[0];
        }

        var installDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var script = OperatingSystem.IsWindows()
            ? WriteWindowsScript(stagingRoot, root, installDirectory)
            : WriteUnixScript(stagingRoot, root, installDirectory);

        StartDetached(script);
    }

    private static void Extract(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static string CreateStagingRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "SteamDisc-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteWindowsScript(string stagingRoot, string payload, string installDirectory)
    {
        var script = Path.Combine(stagingRoot, "apply-update.cmd");
        var launcher = Path.Combine(installDirectory, "SteamDisc.exe");

        // robocopy retries cover the moment between our exit and the files unlocking.
        // The trailing (goto) trick lets the script delete itself once it is done.
        var lines = new[]
        {
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            $"robocopy \"{payload}\" \"{installDirectory}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
            $"start \"\" \"{launcher}\"",
            $"rmdir /s /q \"{payload}\" 2>nul",
            "(goto) 2>nul & del \"%~f0\"",
        };

        File.WriteAllLines(script, lines);
        return script;
    }

    private static string WriteUnixScript(string stagingRoot, string payload, string installDirectory)
    {
        var script = Path.Combine(stagingRoot, "apply-update.sh");
        var launcher = Path.Combine(installDirectory, "SteamDisc");

        var lines = new[]
        {
            "#!/bin/sh",
            "sleep 2",
            $"cp -R \"{payload}/.\" \"{installDirectory}/\"",
            $"chmod +x \"{installDirectory}/SteamDisc\" \"{installDirectory}/SteamDisc.Author\" \"{installDirectory}/Setup\" 2>/dev/null",
            $"\"{launcher}\" &",
            $"rm -rf \"{stagingRoot}\"",
        };

        File.WriteAllLines(script, lines);
        TryMakeExecutable(script);
        return script;
    }

    private static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Best effort: /bin/sh is invoked explicitly below, so the bit is not strictly needed.
        }
    }

    private static void StartDetached(string script)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            : new ProcessStartInfo("/bin/sh", $"\"{script}\"");

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WorkingDirectory = Path.GetTempPath();

        Process.Start(info);
    }
}
