using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SteamDisc.Launcher.Updates;

namespace SteamDisc.Launcher;

public partial class MainWindow : Window
{
    private UpdateInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            VersionLabel.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        Opened += async (_, _) => await CheckForUpdatesAsync();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var author = ToolLocator.LocateAuthor();
        if (author is null)
        {
            ShowStatus("Could not find the authoring app. Build or publish SteamDisc.Builder.App.");
            return;
        }

        Launch(author);
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        var setup = ToolLocator.LocateInstaller();
        if (setup is null)
        {
            ShowStatus("Could not find Setup.exe. Publish SteamDisc.Runtime.App first.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the SteamDisc folder or drive to install from",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        Launch(setup, path);
    }

    /// <summary>
    /// Looks for a newer published release. Silent when there is none, when the network is
    /// unavailable, or when running from a dev tree rather than an installed bundle.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (!UpdateChecker.IsPackagedBundle)
        {
            return;
        }

        var update = await new UpdateChecker().CheckAsync();
        if (update is null)
        {
            return;
        }

        _availableUpdate = update;
        UpdateTitle.Text = $"Update available — {update.TagName}";
        UpdateDetail.Text = $"You have v{UpdateChecker.CurrentVersion}. SteamDisc will update and restart itself.";
        UpdateBanner.IsVisible = true;
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var updater = new Updater();
            UpdateDetail.Text = "Downloading…";
            var progress = new Progress<double>(f => UpdateDetail.Text = $"Downloading… {f:P0}");

            var archive = await updater.DownloadAsync(_availableUpdate, progress);

            UpdateDetail.Text = "Installing and restarting…";
            updater.ApplyAndRestart(archive);

            // Exit so the swap script can replace the files it just copied in behind us.
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            UpdateDetail.Text = "Update failed: " + ex.Message;
            UpdateButton.IsEnabled = true;
        }
    }

    private void Launch(string exePath, string? argument = null)
    {
        try
        {
            var info = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };

            if (argument is not null)
            {
                info.ArgumentList.Add(argument);
            }

            Process.Start(info);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not start the tool: " + ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        Status.Text = message;
        Status.IsVisible = true;
    }
}
