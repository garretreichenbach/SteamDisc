using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Protocol;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;
using SteamDisc.Install;
using SteamDisc.Skin;

namespace SteamDisc.Runtime.App;

/// <summary>
/// Drives a live install and exposes it to the skin — the graphical counterpart of the console
/// runtime's <c>RuntimeProgram</c>, over the same <see cref="InstallEngine"/>.
/// </summary>
public sealed class InstallerController : SkinnedInstallerViewModel
{
    private readonly RuntimeArgs _args;
    private readonly ISteamDiscLogger _logger;
    private readonly AvaloniaInstallHost _host;
    private readonly InstallEngine _engine;
    private readonly Dictionary<string, SteamLibrary> _librariesByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    private Window? _window;
    private PayloadManifest? _manifest;
    private SteamInstallation? _steam;
    private Theme _theme = Theme.Default;
    private CancellationTokenSource? _cts;
    private bool _canProceed;

    public InstallerController(RuntimeArgs args, ISteamDiscLogger logger)
    {
        _args = args;
        _logger = logger;
        _host = new AvaloniaInstallHost(() => _window, AddWarning);
        _engine = new InstallEngine(_host, logger);

        PrimaryCommand = new AsyncRelayCommand(OnPrimaryAsync);
        ExitCommand = new RelayCommand(OnExit);
        CancelCommand = new RelayCommand(OnCancel);
        StoreCommand = new RelayCommand(OpenStorePage);
    }

    public override ICommand PrimaryCommand { get; }

    public override ICommand ExitCommand { get; }

    public override ICommand CancelCommand { get; }

    public override ICommand StoreCommand { get; }

    /// <summary>The layout the disc's theme asks the skin host to render.</summary>
    public ThemeLayout SkinLayout => _theme.Layout;

    public void AttachWindow(Window window) => _window = window;

    /// <summary>
    /// Reads the disc, applies the skin, finds Steam, and runs preflight — everything before the
    /// user presses Install. Any failure lands on the Failed screen rather than crashing.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            var manifestPath = Path.Combine(_args.DiscRoot, PayloadManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                Fail("No disc found",
                    $"This folder has no {PayloadManifest.FileName}. Run Setup from a SteamDisc disc.");
                return;
            }

            _manifest = PayloadManifest.Load(manifestPath);

            var themeFolder = _manifest.ThemePath is { Length: > 0 } themePath
                ? Path.Combine(_args.DiscRoot, themePath)
                : null;
            _theme = Theme.LoadOrDefault(themeFolder, out var themeError);
            if (themeError is not null)
            {
                _logger.Warn("Theme fell back to the default: " + themeError);
            }

            GameTitle = _manifest.Title;
            StoreUrl = $"https://store.steampowered.com/app/{_manifest.AppId}/";
            ShowStoreButton = _manifest.AppId != 0;

            // The disc carries defaults; the person installing gets the final say on both.
            VerifyAfterInstall = _manifest.PostInstall.Validate;
            LaunchWhenFinished = _manifest.PostInstall.Launch;
            _tokens["title"] = _manifest.Title;
            _tokens["disc"] = _manifest.Disc.Number.ToString(CultureInfo.InvariantCulture);
            _tokens["discCount"] = _manifest.Disc.Of.ToString(CultureInfo.InvariantCulture);

            ThemeResources.Apply(this, _theme.Definition, _tokens);
            if (_window is not null)
            {
                _window.Title = $"{_manifest.Title} — Setup";
            }

            (_window as MainWindow)?.SetLayout(_theme.Layout);

            // Decode art off the UI thread, then assign on it.
            var backgroundPath = _theme.BackgroundPath;
            var logoPath = _theme.LogoPath;
            var coverPath = _theme.CoverPath;
            var art = await Task.Run(() => (
                Background: ThemeResources.LoadBitmap(backgroundPath),
                Logo: ThemeResources.LoadBitmap(logoPath),
                Cover: ThemeResources.LoadBitmap(coverPath)));
            BackgroundImage = art.Background;
            LogoImage = art.Logo;
            CoverImage = art.Cover;

            _steam = await Task.Run(() => SteamLocator.Locate(_args.SteamPath));
            if (_steam is null)
            {
                Fail("Steam not found",
                    "Steam could not be found on this machine. Install Steam, sign in, then run Setup again.");
                return;
            }

            var libraries = _steam.GetLibraries();
            if (libraries.Count == 0)
            {
                Fail("No Steam library", $"No Steam libraries were found under '{_steam.RootPath}'.");
                return;
            }

            BuildLibraryOptions(libraries);
            RunPreflight();
            Stage = InstallerStage.Welcome;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to prepare the installer.", ex);
            Fail("Could not read this disc", ex.Message);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // The base view model owns SelectedLibraryOption, so its change hook lives here.
        if (e.PropertyName == nameof(SelectedLibraryOption))
        {
            RunPreflight();
        }
    }

    private void BuildLibraryOptions(IReadOnlyList<SteamLibrary> libraries)
    {
        _librariesByLabel.Clear();
        LibraryOptions.Clear();

        var labels = new List<string>();
        foreach (var library in libraries)
        {
            var free = library.GetAvailableFreeBytes();
            var label = free is { } bytes
                ? $"{library.Path}  ({FormatBytes(bytes)} free)"
                : library.Path;

            // Guard against two libraries producing the same label.
            var unique = label;
            var suffix = 2;
            while (_librariesByLabel.ContainsKey(unique))
            {
                unique = $"{label} ({suffix++})";
            }

            _librariesByLabel[unique] = library;
            LibraryOptions.Add(unique);
            labels.Add(unique);
        }

        ShowLibraryChooser = libraries.Count > 1;

        // Honour an explicit --library; otherwise prefer a library with room to spare.
        var needed = _manifest?.SizeOnDisk ?? 0;
        var bestIndex = 0;
        var bestFree = -1L;
        for (var i = 0; i < libraries.Count; i++)
        {
            var free = libraries[i].GetAvailableFreeBytes() ?? long.MaxValue;
            if (free > needed && free > bestFree)
            {
                bestFree = free;
                bestIndex = i;
            }
        }

        SelectedLibraryOption = labels[bestIndex];
    }

    private void RunPreflight()
    {
        if (_steam is null || _manifest is null || SelectedLibraryOption is null ||
            !_librariesByLabel.TryGetValue(SelectedLibraryOption, out var library))
        {
            return;
        }

        var request = new InstallRequest(_manifest, _args.DiscRoot, library, _steam);

        InstallPreflight preflight;
        try
        {
            preflight = _engine.Preflight(request);
        }
        catch (Exception ex)
        {
            Fail("Cannot prepare the install", ex.Message);
            return;
        }

        InstallTargetText = "Install to:  " + preflight.InstallPath;
        SizeText = "Install size:  " + FormatBytes(preflight.RequiredBytes);
        FreeText = preflight.AvailableBytes is { } free ? "Free space:  " + FormatBytes(free) : string.Empty;

        Warnings.Clear();
        foreach (var warning in preflight.Warnings)
        {
            Warnings.Add(warning);
        }

        // The engine only looks in the target library. Steam tracks one install location per
        // app, so a copy sitting in a *different* library matters just as much - say so plainly
        // and relabel the action rather than quietly making a second copy.
        var installed = _steam.GetInstalledApps().FirstOrDefault(a => a.AppId == _manifest.AppId);
        if (installed is not null)
        {
            var elsewhere = !string.Equals(
                installed.Library.Path, library.Path, StringComparison.OrdinalIgnoreCase);

            Warnings.Insert(0, elsewhere
                ? $"{installed.Manifest.Name} is already installed in another library " +
                  $"({installed.InstallPath}). Steam keeps one location per game — install to that " +
                  "library, or uninstall it first."
                : $"{installed.Manifest.Name} is already installed here ({installed.InstallPath}). " +
                  "Installing will overwrite it.");

            PrimaryButtonText = "Reinstall";
        }
        else
        {
            PrimaryButtonText = _theme.String(ThemeStrings.InstallButton, _tokens);
        }

        _canProceed = preflight.CanProceed;
        if (!preflight.CanProceed)
        {
            foreach (var error in preflight.Errors)
            {
                Warnings.Add("Cannot install: " + error);
            }
        }

        IsPrimaryEnabled = preflight.CanProceed;
    }

    private async Task OnPrimaryAsync()
    {
        switch (Stage)
        {
            case InstallerStage.Welcome:
                await StartInstallAsync();
                break;
            case InstallerStage.Complete:
                LaunchGame();
                OnExit();
                break;
        }
    }

    private async Task StartInstallAsync()
    {
        if (!_canProceed || _steam is null || _manifest is null || SelectedLibraryOption is null ||
            !_librariesByLabel.TryGetValue(SelectedLibraryOption, out var library))
        {
            return;
        }

        Stage = InstallerStage.Installing;
        IsIndeterminate = true;
        ProgressFraction = 0;
        PhaseText = _theme.String(ThemeStrings.Installing, _tokens);
        StatusLine = string.Empty;
        EtaText = string.Empty;
        ThroughputText = string.Empty;

        _cts = new CancellationTokenSource();

        // Both post-install choices belong to whoever is installing, so both come from the
        // welcome screen's checkboxes rather than from anything baked into the disc.
        var request = new InstallRequest(_manifest, _args.DiscRoot, library, _steam)
        {
            ValidateAfterInstall = VerifyAfterInstall,
            Launch = LaunchWhenFinished,
        };
        var progress = new Progress<OperationProgress>(OnProgress);

        InstallResult result;
        try
        {
            result = await Task.Run(() => _engine.InstallAsync(request, progress, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Error("Install threw.", ex);
            Fail(_theme.String(ThemeStrings.ErrorHeading, _tokens), ex.Message);
            return;
        }

        switch (result.Outcome)
        {
            case InstallOutcome.Succeeded:
                Warnings.Clear();
                foreach (var warning in result.Warnings)
                {
                    Warnings.Add(warning);
                }

                CompleteHeading = _theme.String(ThemeStrings.CompleteHeading, _tokens);
                CompleteBody = _theme.String(ThemeStrings.CompleteBody, _tokens);
                PrimaryButtonText = _theme.String(ThemeStrings.PlayButton, _tokens);
                IsPrimaryEnabled = true;
                Stage = InstallerStage.Complete;
                break;

            case InstallOutcome.Cancelled:
                Stage = InstallerStage.Welcome;
                IsPrimaryEnabled = _canProceed;
                break;

            default:
                Fail(
                    _theme.String(ThemeStrings.ErrorHeading, _tokens),
                    result.Error?.Message ?? "Unknown error. See the log for detail.");
                break;
        }
    }

    private void OnProgress(OperationProgress progress)
    {
        PhaseText = DescribePhase(progress.Phase);
        StatusLine = progress.CurrentItem ?? string.Empty;

        if (progress.Fraction is { } fraction)
        {
            IsIndeterminate = false;
            ProgressFraction = fraction;
        }
        else
        {
            IsIndeterminate = true;
        }

        EtaText = progress.EstimatedRemaining is { } eta ? "About " + FormatDuration(eta) + " left" : string.Empty;
        ThroughputText = progress.BytesPerSecond is > 0 and { } rate ? FormatBytes((long)rate) + "/s" : string.Empty;
    }

    private void LaunchGame()
    {
        if (_manifest is null)
        {
            return;
        }

        try
        {
            new SteamProtocolDriver(SystemProcessRunner.Instance).Launch(_manifest.AppId);
        }
        catch (Exception ex)
        {
            _logger.Warn("Could not launch the game: " + ex.Message);
        }
    }

    private void OpenStorePage()
    {
        if (string.IsNullOrEmpty(StoreUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(StoreUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.Warn("Could not open the store page: " + ex.Message);
        }
    }

    private void OnCancel() => _cts?.Cancel();

    private void OnExit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            _window?.Close();
        }
    }

    private void AddWarning(string message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Warnings.Add(message);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Warnings.Add(message));
        }
    }

    private void Fail(string heading, string body)
    {
        ErrorHeading = heading;
        ErrorBody = body;
        Stage = InstallerStage.Failed;
    }

    private static string DescribePhase(OperationPhase phase) => phase switch
    {
        OperationPhase.Preparing => "Preparing…",
        OperationPhase.Scanning => "Scanning…",
        OperationPhase.Hashing => "Checking files…",
        OperationPhase.Compressing => "Compressing…",
        OperationPhase.Extracting => "Installing files…",
        OperationPhase.Verifying => "Verifying files…",
        OperationPhase.WritingManifest => "Registering with Steam…",
        OperationPhase.RunningPrerequisites => "Installing prerequisites…",
        OperationPhase.BuildingImage => "Building image…",
        OperationPhase.Burning => "Burning…",
        OperationPhase.Finishing => "Finishing…",
        _ => "Working…",
    };

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
