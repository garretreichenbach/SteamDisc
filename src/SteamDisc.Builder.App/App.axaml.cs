using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SteamDisc.Builder.App.Services;
using SteamDisc.Builder.App.ViewModels;
using SteamDisc.Core.Diagnostics;

namespace SteamDisc.Builder.App;

public partial class App : Application
{
    private FileLogger? _logger;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _logger = new FileLogger(FileLogger.DefaultLogPath("builder-gui"));

            var viewModel = new MainWindowViewModel(_logger);
            var window = new MainWindow { DataContext = viewModel };
            viewModel.AttachStorage(new StorageService(window));
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => _logger?.Dispose();
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
