using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using SteamDisc.Art;
using SteamDisc.Skin;

namespace SteamDisc.Builder.App.ViewModels;

/// <summary>
/// One artwork slot on a disc — background, logo, cover, or icon — with the two ways to fill it:
/// fetch Valve's own art from the Steam CDN, or upload your own file. No editor, by design.
/// </summary>
public sealed partial class ArtSlotViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public ArtSlotViewModel(
        MainWindowViewModel owner,
        string slot,
        string displayName,
        IReadOnlyList<ArtKind> kinds,
        bool supportsCdn)
    {
        _owner = owner;
        Slot = slot;
        DisplayName = displayName;
        Kinds = kinds;
        SupportsCdn = supportsCdn;
    }

    /// <summary>The theme asset slot this fills: <c>background</c>, <c>logo</c>, <c>cover</c>, <c>icon</c>.</summary>
    public string Slot { get; }

    public string DisplayName { get; }

    /// <summary>Art kinds to try on the CDN, in order of preference.</summary>
    public IReadOnlyList<ArtKind> Kinds { get; }

    /// <summary>False for slots the Steam CDN has no source for (the icon), which hides its button.</summary>
    public bool SupportsCdn { get; }

    /// <summary>The chosen source image on disk, or null when the slot is empty.</summary>
    [ObservableProperty]
    private string? _sourcePath;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task FetchFromSteamAsync()
    {
        if (_owner.CurrentAppId == 0)
        {
            Status = "Pick a game first.";
            return;
        }

        IsBusy = true;
        Status = "Fetching from Steam…";
        try
        {
            var asset = await _owner.FetchFromCdnAsync(Kinds);
            if (asset is null)
            {
                Status = "No Steam art for this slot.";
            }
            else
            {
                SetSource(asset.LocalPath);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Status = "Could not reach Steam.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var path = await _owner.Storage.PickImageAsync();
        if (path is not null)
        {
            SetSource(path);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        SourcePath = null;
        Thumbnail?.Dispose();
        Thumbnail = null;
        Status = string.Empty;
        _owner.OnArtChanged();
    }

    /// <summary>Points the slot at a file and refreshes its thumbnail and the live preview.</summary>
    public void SetSource(string path)
    {
        SourcePath = path;
        Thumbnail?.Dispose();
        Thumbnail = ThemeResources.LoadBitmap(path);
        Status = Thumbnail is null ? "Not a usable image." : Path.GetFileName(path);
        _owner.OnArtChanged();
    }
}
