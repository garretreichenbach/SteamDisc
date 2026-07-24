using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SteamDisc.Launcher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            VersionLabel.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }
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
