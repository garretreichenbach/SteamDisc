using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SteamDisc.Skin;

namespace SteamDisc.Builder.App.ViewModels;

/// <summary>
/// A non-interactive installer view model for the Builder's preview pane.
/// </summary>
/// <remarks>
/// It carries the same data the real installer would at its Welcome screen, so the authoring tool
/// shows the disc as it will actually appear. Its commands do nothing — the preview is a picture,
/// not a working installer.
/// </remarks>
public sealed class PreviewInstallerViewModel : SkinnedInstallerViewModel
{
    private static readonly ICommand NoOp = new RelayCommand(() => { });

    public override ICommand PrimaryCommand => NoOp;

    public override ICommand ExitCommand => NoOp;

    public override ICommand CancelCommand => NoOp;

    public override ICommand StoreCommand => NoOp;
}
