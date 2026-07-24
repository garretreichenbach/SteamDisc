using Avalonia;
using Avalonia.Controls;
using SteamDisc.Core.Theming;
using SteamDisc.Skin.Views;

namespace SteamDisc.Skin;

/// <summary>
/// Hosts the skinned installer, choosing the layout the theme asks for.
/// </summary>
/// <remarks>
/// Both front-ends drive this the same way — the Runtime with a live install view model, the
/// Builder with a static preview — so a disc looks in the authoring tool exactly as it will when
/// it boots. <see cref="ThemeLayout.MultiGameMenu"/> falls back to the splash layout: payloads
/// carry a single game, so the compilation grid has nothing to show yet.
/// </remarks>
public sealed class SkinHost : ContentControl
{
    public static readonly StyledProperty<ThemeLayout> LayoutProperty =
        AvaloniaProperty.Register<SkinHost, ThemeLayout>(nameof(Layout));

    public static readonly StyledProperty<SkinnedInstallerViewModel?> InstallerProperty =
        AvaloniaProperty.Register<SkinHost, SkinnedInstallerViewModel?>(nameof(Installer));

    public ThemeLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public SkinnedInstallerViewModel? Installer
    {
        get => GetValue(InstallerProperty);
        set => SetValue(InstallerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayoutProperty)
        {
            Rebuild();
        }
        else if (change.Property == InstallerProperty && Content is Control existing)
        {
            // Same layout, new (or first) view model: just rebind, no need to rebuild the view.
            existing.DataContext = Installer;
        }
        else if (change.Property == InstallerProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Control view = Layout == ThemeLayout.ModernCard
            ? new ModernCardView()
            : new ClassicSplashView();

        view.DataContext = Installer;
        Content = view;
    }
}
