using Avalonia;

namespace SteamDisc.Builder.App;

/// <summary>The authoring GUI — the graphical counterpart of the SteamDisc CLI.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
