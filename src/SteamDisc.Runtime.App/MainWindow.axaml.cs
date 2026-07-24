using Avalonia.Controls;
using SteamDisc.Core.Theming;

namespace SteamDisc.Runtime.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Wires the installer view model into the skin host.</summary>
    public void Bind(InstallerController controller)
    {
        DataContext = controller;
        Host.Installer = controller;
        Host.Layout = controller.SkinLayout;
        controller.AttachWindow(this);
    }

    /// <summary>Switches the rendered layout once the disc's theme has loaded.</summary>
    public void SetLayout(ThemeLayout layout) => Host.Layout = layout;
}
