using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SteamDisc.Core.Diagnostics;

namespace SteamDisc.Runtime.App;

public partial class App : Application
{
    private FileLogger? _logger;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = RuntimeArgs.Parse(desktop.Args ?? Array.Empty<string>());
            _logger = new FileLogger(args.LogPath ?? FileLogger.DefaultLogPath("runtime-gui"));
            _logger.Info($"SteamDisc runtime (GUI) starting. Disc root: '{args.DiscRoot}'.");

            var controller = new InstallerController(args, _logger);
            var window = new MainWindow();
            window.Bind(controller);
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => _logger?.Dispose();

            // Prepare once the window exists, so failures can render in the skin rather than crash.
            window.Opened += async (_, _) => await controller.LoadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
