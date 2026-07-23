using System.Diagnostics;

namespace SteamDisc.Core.Steam;

/// <summary>
/// Answers "is the Steam client running right now?", which determines whether a freshly
/// dropped <c>appmanifest</c> will be noticed (spike S2).
/// </summary>
public static class SteamClientState
{
    private static readonly string[] ProcessNames = { "steam", "steamwebhelper", "Steam" };

    /// <summary>True when a Steam client process appears to be running.</summary>
    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
            {
                continue;
            }

            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
