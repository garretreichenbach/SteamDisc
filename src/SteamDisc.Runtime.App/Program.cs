using Avalonia;

namespace SteamDisc.Runtime.App;

/// <summary>
/// The graphical disc runtime — this is what ships as <c>Setup.exe</c> in the disc root.
/// </summary>
/// <remarks>
/// It reads <c>payload.json</c> from the folder it lives in, skins itself from the disc's theme,
/// and drives the same <see cref="Install.InstallEngine"/> the console runtime uses. If a GUI
/// genuinely cannot start, the console runtime remains as the fallback.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("The graphical installer could not start: " + ex.Message);
            Console.Error.WriteLine("Run the console SteamDisc runtime from this disc instead.");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
