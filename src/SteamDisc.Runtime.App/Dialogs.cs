using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SteamDisc.Install;

namespace SteamDisc.Runtime.App;

/// <summary>Small modal dialogs the install host needs — kept in code to avoid extra XAML.</summary>
internal static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window owner, string question)
    {
        var result = false;

        var yes = new Button { Content = "Yes", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = "No", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        panel.Children.Add(buttons);

        var dialog = CreateDialog("SteamDisc", panel);
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> RequestDiscAsync(Window owner, DiscRequest request)
    {
        string? chosen = null;

        var message = request.Reason is { Length: > 0 } reason
            ? reason
            : $"Please insert disc {request.DiscNumber} of {request.DiscCount} for {request.Title}.";

        var browse = new Button { Content = "Browse to disc…", MinWidth = 130 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(browse);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        panel.Children.Add(buttons);

        var dialog = CreateDialog("Insert disc", panel);

        browse.Click += async (_, _) =>
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select the folder for disc {request.DiscNumber}",
                AllowMultiple = false,
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                chosen = path;
                dialog.Close();
            }
        };
        cancel.Click += (_, _) => { chosen = null; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return chosen;
    }

    private static Window CreateDialog(string title, Control content) => new()
    {
        Title = title,
        Content = content,
        SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        ShowInTaskbar = false,
    };
}
