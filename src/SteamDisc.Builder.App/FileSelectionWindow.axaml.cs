using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SteamDisc.Builder.App.ViewModels;

namespace SteamDisc.Builder.App;

public partial class FileSelectionWindow : Window
{
    public FileSelectionWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is FileSelectionViewModel vm)
        {
            vm.CloseRequested += CloseSelf;
            vm.SaveRequested += PickSavePathAsync;
        }
    }

    private void CloseSelf() => Close();

    /// <summary>Asks the user where to save the selection file, returning the chosen path or null.</summary>
    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save selection",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("SteamDisc selection") { Patterns = new[] { "*.selection.json", "*.json" } },
            },
        });

        return file?.TryGetLocalPath();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is FileSelectionViewModel vm)
        {
            vm.CloseRequested -= CloseSelf;
            vm.SaveRequested -= PickSavePathAsync;
        }

        base.OnClosed(e);
    }
}
