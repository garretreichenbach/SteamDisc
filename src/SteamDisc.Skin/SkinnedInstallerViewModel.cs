using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamDisc.Skin;

/// <summary>The screen a skinned installer is showing.</summary>
public enum InstallerStage
{
    /// <summary>The opening splash: title, art, and the Install button.</summary>
    Welcome,

    /// <summary>Extraction is running; the progress panel is up.</summary>
    Installing,

    /// <summary>The game installed; offer Play/Exit.</summary>
    Complete,

    /// <summary>Something went wrong; show the message and a way out.</summary>
    Failed,
}

/// <summary>
/// Everything the skinned installer views bind to, independent of where the data comes from.
/// </summary>
/// <remarks>
/// The Runtime supplies a live implementation driving <c>InstallEngine</c>; the Builder supplies
/// a static preview. Because both derive from this one type, the Builder's preview pane renders
/// the exact same control the disc will — the skin is authored against what it actually becomes.
/// </remarks>
public abstract partial class SkinnedInstallerViewModel : ObservableObject
{
    // --- Identity and copy -------------------------------------------------
    [ObservableProperty]
    private string _gameTitle = "Game";

    [ObservableProperty]
    private string _welcomeBody = string.Empty;

    /// <summary>The game's own blurb, shown on the welcome screen. Empty hides it.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    // --- Artwork (null degrades to the theme's colours alone) --------------
    [ObservableProperty]
    private IImage? _backgroundImage;

    [ObservableProperty]
    private IImage? _logoImage;

    [ObservableProperty]
    private IImage? _coverImage;

    // --- Theme-derived brushes and fonts -----------------------------------
    [ObservableProperty]
    private IBrush _accentBrush = Brushes.OrangeRed;

    [ObservableProperty]
    private IBrush _backgroundBrush = Brushes.Black;

    [ObservableProperty]
    private IBrush _surfaceBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x22));

    [ObservableProperty]
    private IBrush _textBrush = Brushes.White;

    [ObservableProperty]
    private IBrush _textMutedBrush = Brushes.Gray;

    [ObservableProperty]
    private IBrush _errorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    /// <summary>A translucent wash over the background art so text stays legible.</summary>
    [ObservableProperty]
    private IBrush _scrimBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x14));

    [ObservableProperty]
    private FontFamily _headingFont = FontFamily.Default;

    [ObservableProperty]
    private FontFamily _bodyFont = FontFamily.Default;

    // --- Stage machine -----------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private InstallerStage _stage = InstallerStage.Welcome;

    public bool IsWelcome => Stage == InstallerStage.Welcome;

    public bool IsInstalling => Stage == InstallerStage.Installing;

    public bool IsComplete => Stage == InstallerStage.Complete;

    public bool IsFailed => Stage == InstallerStage.Failed;

    // --- Progress ----------------------------------------------------------
    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string _phaseText = string.Empty;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private string _throughputText = string.Empty;

    // --- Preflight detail --------------------------------------------------
    [ObservableProperty]
    private string _installTargetText = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _freeText = string.Empty;

    [ObservableProperty]
    private string _completeHeading = "Installation complete";

    [ObservableProperty]
    private string _completeBody = string.Empty;

    [ObservableProperty]
    private string _errorHeading = "Installation failed";

    [ObservableProperty]
    private string _errorBody = string.Empty;

    // --- Buttons -----------------------------------------------------------
    [ObservableProperty]
    private string _primaryButtonText = "Install";

    [ObservableProperty]
    private bool _isPrimaryEnabled = true;

    [ObservableProperty]
    private string _exitButtonText = "Exit";

    [ObservableProperty]
    private string _cancelButtonText = "Cancel";

    // --- Steam store link --------------------------------------------------
    [ObservableProperty]
    private bool _showStoreButton;

    [ObservableProperty]
    private string _storeButtonText = "View on Steam";

    /// <summary>The game's Steam store page, opened by <see cref="StoreCommand"/>.</summary>
    [ObservableProperty]
    private string? _storeUrl;

    /// <summary>Non-fatal problems surfaced before or during the install.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    // --- Library choice ----------------------------------------------------
    // Libraries are represented as display strings so the skin stays decoupled from Steam types;
    // the front-end keeps the mapping back to the real library.
    [ObservableProperty]
    private bool _showLibraryChooser;

    public ObservableCollection<string> LibraryOptions { get; } = new();

    [ObservableProperty]
    private string? _selectedLibraryOption;

    // --- Commands (each front-end wires its own behaviour) -----------------
    public abstract ICommand PrimaryCommand { get; }

    public abstract ICommand ExitCommand { get; }

    public abstract ICommand CancelCommand { get; }

    /// <summary>Opens the game's Steam store page.</summary>
    public abstract ICommand StoreCommand { get; }
}
