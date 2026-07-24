using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace SteamDisc.Builder.App.Services;

/// <summary>
/// Wraps the window's storage provider so the view models can open pickers and ask yes/no
/// questions without holding a reference to a <see cref="Window"/> themselves.
/// </summary>
public sealed class StorageService
{
    private readonly Window _owner;

    public StorageService(Window owner) => _owner = owner;

    public async Task<string?> PickImageAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExecutableAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate the runtime Setup.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } },
            },
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;

        var yes = new Button { Content = "Yes", MinWidth = 90 };
        var no = new Button { Content = "No", MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        await dialog.ShowDialog(_owner);
        return result;
    }
}
