using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDisc.Art;
using SteamDisc.Art.Providers;
using SteamDisc.Authoring;
using SteamDisc.Builder.App.Services;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;
using SteamDisc.Imaging;
using SteamDisc.Imaging.Iso;
using SteamDisc.Skin;

namespace SteamDisc.Builder.App.ViewModels;

/// <summary>A named theme the Builder offers, wrapping its built-in id.</summary>
public sealed record ThemeOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One installed game, prepared for the list.</summary>
public sealed class GameItem
{
    public GameItem(GameCandidate candidate)
    {
        Candidate = candidate;
        var size = candidate.ManifestSize > 0 ? FormatBytes(candidate.ManifestSize) : "size unknown";
        SubtitleText = $"AppID {candidate.AppId} · {size}";
        SuitabilityLabel = candidate.Suitability.ToString();
        SuitabilityBrush = candidate.Suitability switch
        {
            GameSuitability.Ideal => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
            GameSuitability.Good => new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)),
            _ => new SolidColorBrush(Color.FromRgb(0xF5, 0xB3, 0x2B)),
        };
    }

    public GameCandidate Candidate { get; }

    public string Name => Candidate.Name;

    public string SubtitleText { get; }

    public string SuitabilityLabel { get; }

    public IBrush SuitabilityBrush { get; }

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
}

/// <summary>
/// The authoring GUI: choose a game, dress the disc, and run the pipeline to an ISO and a burn.
/// A thin view over the same engines the CLI drives.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ISteamDiscLogger _logger;
    private readonly ArtCache _artCache = new();
    private readonly SteamCdnArtProvider _cdn;
    private readonly SteamStore _steamStore = new();
    private readonly List<GameItem> _allGames = new();
    private uint _descriptionFetchedFor;

    private SteamInstallation? _steam;
    private PackageResult? _lastResult;
    private readonly List<string> _builtIsos = new();
    private string? _autoSuggestedOutput;

    public MainWindowViewModel(ISteamDiscLogger logger)
    {
        _logger = logger;
        _cdn = new SteamCdnArtProvider(cache: _artCache);

        Background = new ArtSlotViewModel(this, "background", "Background",
            new[] { ArtKind.Hero, ArtKind.Capsule, ArtKind.Header }, supportsCdn: true);
        Logo = new ArtSlotViewModel(this, "logo", "Logo", new[] { ArtKind.Logo }, supportsCdn: true);
        Cover = new ArtSlotViewModel(this, "cover", "Cover", new[] { ArtKind.Cover, ArtKind.Capsule }, supportsCdn: true);
        Icon = new ArtSlotViewModel(this, "icon", "Icon", Array.Empty<ArtKind>(), supportsCdn: false);
        ArtSlots = new ObservableCollection<ArtSlotViewModel> { Background, Logo, Cover, Icon };

        _runtimeExePath = RuntimeLocator.Locate();
        UpdateRuntimeStatus();
        RefreshPreview();
    }

    /// <summary>The app version, e.g. "v0.1.0", for the header.</summary>
    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "v0.1.0";

    /// <summary>Set by the window once it exists, before any picker is used.</summary>
    public StorageService Storage { get; private set; } = null!;

    public void AttachStorage(StorageService storage) => Storage = storage;

    // --- Game list ---------------------------------------------------------
    public ObservableCollection<GameItem> Games { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private GameItem? _selectedGame;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public uint CurrentAppId => SelectedGame?.Candidate.AppId ?? 0;

    public string? CurrentTitle => SelectedGame?.Candidate.Name;

    // --- Package options ---------------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private string _outputFolder = string.Empty;

    public IReadOnlyList<OpticalMedium> Media { get; } = OpticalMedium.All;

    [ObservableProperty]
    private OpticalMedium _selectedMedium = OpticalMedium.BluRay;

    public Array CompressionOptions { get; } = Enum.GetValues(typeof(ArchiveCompression));

    [ObservableProperty]
    private ArchiveCompression _selectedCompression = ArchiveCompression.Fast;

    public IReadOnlyList<string> FormatOptions { get; } = new[] { ArchiveFormats.Sdz, ArchiveFormats.SevenZip };

    [ObservableProperty]
    private string _selectedFormat = ArchiveFormats.Sdz;

    public IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption("classic", "Classic — retail splash"),
        new ThemeOption("modern", "Modern — cover card"),
    };

    [ObservableProperty]
    private ThemeOption? _selectedTheme;

    [ObservableProperty]
    private bool _writeHashes = true;

    [ObservableProperty]
    private string? _runtimeExePath;

    /// <summary>When the stamped runtime was built, and whether it looks stale.</summary>
    [ObservableProperty]
    private string _runtimeStatusText = string.Empty;

    /// <summary>The blurb shown on the installer, seeded from Steam but editable.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Whether to print the "may need updates" caution on the installer.</summary>
    [ObservableProperty]
    private bool _showUpdateNotice;

    [ObservableProperty]
    private string _updateNoticeText = "Game may require updates via the Steam client after installing.";

    /// <summary>Approximate on-disc size for the current game, compression, and media.</summary>
    [ObservableProperty]
    private string _estimateText = string.Empty;

    // --- Artwork -----------------------------------------------------------
    public ObservableCollection<ArtSlotViewModel> ArtSlots { get; }

    public ArtSlotViewModel Background { get; }

    public ArtSlotViewModel Logo { get; }

    public ArtSlotViewModel Cover { get; }

    public ArtSlotViewModel Icon { get; }

    // --- Live preview ------------------------------------------------------
    public PreviewInstallerViewModel Preview { get; } = new();

    [ObservableProperty]
    private ThemeLayout _previewLayout = ThemeLayout.ClassicSplash;

    // --- Pipeline state ----------------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildIsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(BurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoFetchAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildIsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestInstallCommand))]
    private bool _canBuildIso;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BurnCommand))]
    private bool _canBurn;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _progressPhase = string.Empty;

    [ObservableProperty]
    private string _progressStatus = string.Empty;

    [ObservableProperty]
    private string _progressEta = string.Empty;

    [ObservableProperty]
    private string _statusText = "Locating Steam…";

    // --- Startup -----------------------------------------------------------
    public async Task InitializeAsync()
    {
        SelectedTheme = Themes[0];

        try
        {
            _steam = await Task.Run(() => SteamLocator.Locate(null));
        }
        catch (Exception ex)
        {
            _logger.Warn("Steam location failed: " + ex.Message);
        }

        if (_steam is null)
        {
            StatusText = "Steam was not found on this machine. Install Steam and reopen the tool.";
            return;
        }

        try
        {
            var games = await Task.Run(() => new GameCatalog(_steam).List());
            _allGames.Clear();
            _allGames.AddRange(games
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GameItem(g)));
            ApplyFilter();
            StatusText = $"{_allGames.Count} installed game(s) found. Choose one to begin.";
        }
        catch (Exception ex)
        {
            _logger.Error("Listing games failed.", ex);
            StatusText = "Could not list installed games: " + ex.Message;
        }
    }

    // --- Reactions ---------------------------------------------------------
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedThemeChanged(ThemeOption? value) => RefreshPreview();

    partial void OnDescriptionChanged(string value) => RefreshPreview();

    partial void OnRuntimeExePathChanged(string? value) => UpdateRuntimeStatus();

    /// <summary>
    /// Reports how old the runtime being stamped onto discs is. A stale Setup.exe is otherwise
    /// invisible: the preview renders current code while the disc carries an older installer.
    /// </summary>
    private void UpdateRuntimeStatus()
    {
        if (string.IsNullOrWhiteSpace(RuntimeExePath) || !File.Exists(RuntimeExePath))
        {
            RuntimeStatusText = string.Empty;
            return;
        }

        var built = File.GetLastWriteTime(RuntimeExePath);
        var thisTool = Environment.ProcessPath is { Length: > 0 } self && File.Exists(self)
            ? File.GetLastWriteTime(self)
            : built;

        RuntimeStatusText = built < thisTool.AddMinutes(-5)
            ? $"⚠ Built {built:g} — older than this tool. Re-publish SteamDisc.Runtime.App, " +
              "then rebuild, or the disc will carry an out-of-date installer."
            : $"Built {built:g}";
    }

    partial void OnShowUpdateNoticeChanged(bool value) => RefreshPreview();

    partial void OnUpdateNoticeTextChanged(string value) => RefreshPreview();

    partial void OnSelectedCompressionChanged(ArchiveCompression value) => UpdateEstimate();

    partial void OnSelectedMediumChanged(OpticalMedium value) => UpdateEstimate();

    partial void OnSelectedGameChanged(GameItem? value)
    {
        if (value is not null)
        {
            SuggestOutputFolder(value);

            // Art is per-game; start fresh so a leftover cover doesn't ride onto the next disc.
            foreach (var slot in ArtSlots)
            {
                slot.ClearCommand.Execute(null);
            }

            // Reset the blurb and pull the game's Steam description as a starting point.
            Description = string.Empty;
            _ = FetchDescriptionAsync();

            // Default the update caution on for games the catalog flags as often-patched.
            ShowUpdateNotice = value.Candidate.HasWarnings ||
                               value.Candidate.Suitability == GameSuitability.Caveats;
        }
        else
        {
            Description = string.Empty;
        }

        UpdateEstimate();
        RefreshPreview();
    }

    private async Task FetchDescriptionAsync()
    {
        var appId = CurrentAppId;
        if (appId == 0 || appId == _descriptionFetchedFor)
        {
            return;
        }

        try
        {
            var text = await _steamStore.GetShortDescriptionAsync(appId);

            // Only apply if the user hasn't moved on or typed their own blurb in the meantime.
            if (text is { Length: > 0 } && CurrentAppId == appId && string.IsNullOrWhiteSpace(Description))
            {
                _descriptionFetchedFor = appId;
                Description = text;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Warn("Could not fetch the Steam description: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefetchDescriptionAsync()
    {
        _descriptionFetchedFor = 0;
        Description = string.Empty;
        await FetchDescriptionAsync();
    }

    private void UpdateEstimate()
    {
        if (SelectedGame is not { } game || game.Candidate.ManifestSize <= 0)
        {
            EstimateText = string.Empty;
            return;
        }

        var uncompressed = game.Candidate.ManifestSize;

        // Deliberately pessimistic. Game data is largely pre-compressed already — a real build
        // measured 0.83 at Maximum — so an optimistic ratio promises a fit that only fails once
        // the disc is burned. Predicting one disc too many is much the cheaper mistake.
        var ratio = SelectedCompression switch
        {
            ArchiveCompression.Store => 1.00,
            ArchiveCompression.Fast => 0.95,
            ArchiveCompression.Balanced => 0.90,
            ArchiveCompression.Maximum => 0.85,
            _ => 0.95,
        };

        var estimated = (long)(uncompressed * ratio);

        // Ask the planner rather than dividing by raw capacity: it reserves the same per-disc
        // overhead the build will (runtime, theme, ISO structures), so the two agree.
        var discs = DiscSpanPlanner.EstimateDiscCount(estimated, SelectedMedium);
        var fit = SelectedMedium.CapacityBytes == long.MaxValue
            ? "single image"
            : discs <= 1
                ? $"should fit one {SelectedMedium.Name}"
                : $"about {discs} × {SelectedMedium.Name}";

        // Decimal units here, so the figure is directly comparable to the media capacity.
        EstimateText =
            $"≈ {FormatDecimalBytes(estimated)} on disc (rough estimate from " +
            $"{FormatDecimalBytes(uncompressed)}) · {fit}";
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        Games.Clear();

        foreach (var game in _allGames)
        {
            if (query.Length == 0 || game.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Games.Add(game);
            }
        }
    }

    private void SuggestOutputFolder(GameItem game)
    {
        // Only replace a suggestion of our own; never stomp a folder the user typed.
        if (!string.IsNullOrEmpty(OutputFolder) && OutputFolder != _autoSuggestedOutput)
        {
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var root = string.IsNullOrEmpty(desktop) ? Environment.CurrentDirectory : desktop;
        _autoSuggestedOutput = Path.Combine(root, "SteamDisc", Sanitise(game.Name));
        OutputFolder = _autoSuggestedOutput;
    }

    /// <summary>Called by a slot when its image changes, to repaint the preview.</summary>
    public void OnArtChanged() => RefreshPreview();

    private void RefreshPreview()
    {
        var definition = ResolveThemeDefinition();
        var title = CurrentTitle ?? "Your Game";
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["title"] = title };

        Preview.GameTitle = title;
        ThemeResources.Apply(Preview, definition, tokens);

        // Apply seeds Description from the theme; the Builder's own field takes precedence.
        Preview.Description = Description;
        Preview.UpdateNotice = ShowUpdateNotice ? UpdateNoticeText.Trim() : string.Empty;
        Preview.ShowStoreButton = CurrentAppId != 0;
        Preview.StoreUrl = CurrentAppId != 0 ? $"https://store.steampowered.com/app/{CurrentAppId}/" : null;

        Preview.BackgroundImage = ThemeResources.LoadBitmap(Background.SourcePath);
        Preview.LogoImage = ThemeResources.LoadBitmap(Logo.SourcePath);
        Preview.CoverImage = ThemeResources.LoadBitmap(Cover.SourcePath);

        Preview.InstallTargetText = "Install to:  your Steam library";
        Preview.SizeText = SelectedGame is { } game && game.Candidate.ManifestSize > 0
            ? "Install size:  " + GameItem.FormatBytes(game.Candidate.ManifestSize)
            : string.Empty;
        Preview.FreeText = string.Empty;
        Preview.Stage = InstallerStage.Welcome;

        PreviewLayout = definition.Layout;
    }

    private ThemeDefinition ResolveThemeDefinition()
        => BuiltInThemes.Get(SelectedTheme?.Id ?? "classic") ?? BuiltInThemes.ValveRetail2011();

    // --- Steam CDN art -----------------------------------------------------
    /// <summary>Fetches the first available candidate across a slot's preferred kinds.</summary>
    public async Task<ArtAsset?> FetchFromCdnAsync(IReadOnlyList<ArtKind> kinds)
    {
        var appId = CurrentAppId;
        var title = CurrentTitle;

        foreach (var kind in kinds.Where(k => _cdn.SupportedKinds.Contains(k)))
        {
            var found = await _cdn.SearchAsync(new ArtQuery(appId, title, kind)).ConfigureAwait(true);
            if (found.Count > 0)
            {
                return await _cdn.FetchAsync(found[0]).ConfigureAwait(true);
            }
        }

        return null;
    }

    [RelayCommand(CanExecute = nameof(CanFetchArt))]
    private async Task AutoFetchAllAsync()
    {
        if (SelectedGame is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Fetching artwork from Steam…";
        try
        {
            var resolver = ArtResolver.CreateDefault(cache: _artCache, logger: _logger);
            var assets = await resolver.ResolveAsync(CurrentAppId, CurrentTitle).ConfigureAwait(true);

            foreach (var slot in ArtSlots)
            {
                if (assets.TryGetValue(slot.Slot, out var asset))
                {
                    slot.SetSource(asset.LocalPath);
                }
            }

            // Steam has no stable public icon path, so default the icon to the logo.
            if (Icon.SourcePath is null && Logo.SourcePath is { } logoPath)
            {
                Icon.SetSource(logoPath);
            }

            StatusText = assets.Count > 0
                ? $"Fetched artwork for {assets.Count} slot(s)."
                : "No Steam artwork was found for this game.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            StatusText = "Could not reach the Steam CDN.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanFetchArt => !IsBusy;

    // --- Pipeline ----------------------------------------------------------
    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var folder = await Storage.PickFolderAsync("Choose where to build the disc");
        if (folder is not null)
        {
            _autoSuggestedOutput = null;
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private async Task LocateRuntimeAsync()
    {
        var exe = await Storage.PickExecutableAsync();
        if (exe is not null)
        {
            RuntimeExePath = exe;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (SelectedGame is null || string.IsNullOrWhiteSpace(OutputFolder))
        {
            return;
        }

        IsBusy = true;
        CanBuildIso = false;
        CanBurn = false;
        _builtIsos.Clear();
        ResetProgress("Packaging…");

        var theme = ResolveThemeDefinition();
        if (!string.IsNullOrWhiteSpace(Description))
        {
            theme.Strings[ThemeResources.GameDescriptionKey] = Description.Trim();
        }

        if (ShowUpdateNotice && !string.IsNullOrWhiteSpace(UpdateNoticeText))
        {
            theme.Strings[ThemeResources.UpdateNoticeKey] = UpdateNoticeText.Trim();
        }

        var request = new PackageRequest(SelectedGame.Candidate, OutputFolder, SelectedMedium)
        {
            Compression = SelectedCompression,
            ArchiveFormat = SelectedFormat,
            Theme = theme,
            Artwork = CollectArtwork(),
            RuntimeExecutablePath = string.IsNullOrWhiteSpace(RuntimeExePath) ? null : RuntimeExePath,
            WriteHashSidecar = WriteHashes,
        };

        var progress = new Progress<OperationProgress>(OnProgress);
        try
        {
            var result = await Task.Run(() => new PackageBuilder(logger: _logger).BuildAsync(request, progress));
            _lastResult = result;
            StatusText = DescribeResult(result);
            CanBuildIso = true;
        }
        catch (Exception ex)
        {
            _logger.Error("Build failed.", ex);
            StatusText = "Build failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ClearProgress();
        }
    }

    private bool CanBuild => !IsBusy && SelectedGame is not null && !string.IsNullOrWhiteSpace(OutputFolder);

    [RelayCommand(CanExecute = nameof(CanMakeIso))]
    private async Task BuildIsoAsync()
    {
        if (_lastResult is null)
        {
            return;
        }

        IsBusy = true;
        _builtIsos.Clear();
        ResetProgress("Building image…");

        try
        {
            var builder = new Iso9660Builder();
            foreach (var discRoot in _lastResult.DiscRoots)
            {
                var trimmed = Path.TrimEndingDirectorySeparator(discRoot);
                var output = trimmed + ".iso";
                var label = ReadDiscLabel(discRoot);
                var progress = new Progress<OperationProgress>(OnProgress);
                var result = await Task.Run(() =>
                    builder.BuildAsync(discRoot, output, new IsoBuildOptions(label), progress));
                _builtIsos.Add(result.Path);
            }

            StatusText = "ISO(s) written:\n  " + string.Join("\n  ", _builtIsos);
            CanBurn = _builtIsos.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.Error("ISO build failed.", ex);
            StatusText = "ISO build failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ClearProgress();
        }
    }

    private bool CanMakeIso => !IsBusy && CanBuildIso;

    /// <summary>
    /// Runs the disc's own Setup.exe against the staging folder, so the skin, preflight and
    /// install can be checked before anything is burned to a coaster.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestInstall))]
    private void TestInstall()
    {
        if (_lastResult is null)
        {
            return;
        }

        var discRoot = _lastResult.DiscRoots[0];
        var setup = Path.Combine(discRoot, "Setup.exe");

        if (!File.Exists(setup))
        {
            if (string.IsNullOrWhiteSpace(RuntimeExePath) || !File.Exists(RuntimeExePath))
            {
                StatusText = "This disc has no Setup.exe. Publish SteamDisc.Runtime.App and locate it first.";
                return;
            }

            setup = RuntimeExePath;
        }

        try
        {
            var info = new ProcessStartInfo(setup)
            {
                UseShellExecute = false,
                WorkingDirectory = discRoot,
            };
            info.ArgumentList.Add(discRoot);
            Process.Start(info);

            StatusText =
                "Launched the disc installer against:" + Environment.NewLine +
                "  " + discRoot + Environment.NewLine + Environment.NewLine +
                "This installs for real — it is the same Setup.exe the burned disc will carry.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not start the installer: " + ex.Message;
        }
    }

    private bool CanTestInstall => !IsBusy && CanBuildIso;

    [RelayCommand(CanExecute = nameof(CanStartBurn))]
    private async Task BurnAsync()
    {
        var burners = DiscBurners.Discover(null);
        if (burners.Count == 0)
        {
            StatusText = "No disc burner was found. On Windows the built-in Disc Image Burner handles ISOs.";
            return;
        }

        var burner = burners[0];
        IsBusy = true;
        try
        {
            for (var i = 0; i < _builtIsos.Count; i++)
            {
                var iso = _builtIsos[i];
                var proceed = await Storage.ConfirmAsync(
                    "Burn disc",
                    $"Insert a blank disc for disc {i + 1} of {_builtIsos.Count} and click Yes to hand it to " +
                    $"{burner.Name}:\n\n{Path.GetFileName(iso)}");

                if (!proceed)
                {
                    StatusText += "\nBurn cancelled.";
                    break;
                }

                var result = await burner.BurnAsync(iso);
                StatusText += "\n" + result.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Burn failed.", ex);
            StatusText += "\nBurn failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartBurn => !IsBusy && CanBurn;

    private IReadOnlyDictionary<string, string>? CollectArtwork()
    {
        var artwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in ArtSlots)
        {
            if (slot.SourcePath is { Length: > 0 } path)
            {
                artwork[slot.Slot] = path;
            }
        }

        // No icon supplied but a logo is: default the icon to the logo, as there is no CDN icon.
        if (!artwork.ContainsKey(Icon.Slot) && artwork.TryGetValue(Logo.Slot, out var logoPath))
        {
            artwork[Icon.Slot] = logoPath;
        }

        return artwork.Count > 0 ? artwork : null;
    }

    private void OnProgress(OperationProgress progress)
    {
        ProgressPhase = progress.Phase.ToString();
        ProgressStatus = progress.CurrentItem ?? string.Empty;

        if (progress.Fraction is { } fraction)
        {
            IsProgressIndeterminate = false;
            ProgressValue = fraction;
        }
        else
        {
            IsProgressIndeterminate = true;
        }

        ProgressEta = progress.EstimatedRemaining is { } eta
            ? "about " + FormatDuration(eta) + " left"
            : string.Empty;
    }

    private void ResetProgress(string phase)
    {
        ProgressPhase = phase;
        ProgressStatus = string.Empty;
        ProgressEta = string.Empty;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
    }

    private void ClearProgress()
    {
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressPhase = string.Empty;
        ProgressStatus = string.Empty;
        ProgressEta = string.Empty;
    }

    private static string ReadDiscLabel(string discRoot)
    {
        var manifestPath = Path.Combine(discRoot, PayloadManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(discRoot));
        }

        var manifest = PayloadManifest.Load(manifestPath);
        return manifest.Disc.Label ?? manifest.Title;
    }

    private static string DescribeResult(PackageResult result)
    {
        var lines = new List<string>
        {
            $"Packaged in {FormatDuration(result.Duration)}.",
            $"{GameItem.FormatBytes(result.UncompressedBytes)} → {GameItem.FormatBytes(result.CompressedBytes)} " +
            $"({result.CompressionRatio:P0} of original)",
            $"Discs: {result.Plan.DiscCount} × {result.Manifest.Disc.Label ?? result.Manifest.Title}",
        };

        for (var disc = 1; disc <= result.Plan.DiscCount; disc++)
        {
            lines.Add($"  disc {disc}: {result.DiscRoots[disc - 1]}");
        }

        lines.Add(string.Empty);
        lines.Add("Ready to build an ISO.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Formats in decimal GB, the way optical media are labelled — so "5.2 GB" against a
    /// "4.7 GB" disc reads correctly. Elsewhere sizes use the binary units Explorer shows.
    /// </summary>
    private static string FormatDecimalBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
