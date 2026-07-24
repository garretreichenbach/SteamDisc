using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDisc.Authoring;

namespace SteamDisc.Builder.App.ViewModels;

/// <summary>What the file-selection window hands back when the user clicks Done.</summary>
public sealed record FileSelectionResult(IReadOnlyList<string> Exclusions, long ExcludedBytes, int ExcludedCount);

/// <summary>
/// Backs the "Edit files" window: a checkbox tree of the game folder, seeded from the heuristic
/// suggestions, with the running total of what will be left off the disc.
/// </summary>
public sealed partial class FileSelectionViewModel : ObservableObject
{
    private readonly FileSelectionNodeViewModel _root;
    private readonly GameCandidate _game;

    public FileSelectionViewModel(GameCandidate game, SelectionNode tree, IReadOnlyCollection<string>? existing)
    {
        _game = game;
        HeaderTitle = $"{game.Name} — files to include";
        _root = new FileSelectionNodeViewModel(tree, parent: null, onUserChange: UpdateSummary);
        Roots = _root.Children;

        // Reopening with prior choices: those already fold in the heuristics from the first pass,
        // so they win outright — start everything on, then drop what was excluded before.
        if (existing is { Count: > 0 })
        {
            _root.SetAll(true);
            _root.ApplyExclusions(new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase));
            _root.RecomputeSubtree();
        }

        UpdateSummary();
    }

    public string HeaderTitle { get; }

    public ObservableCollection<FileSelectionNodeViewModel> Roots { get; }

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Transient confirmation after a save, e.g. "Saved: Portal 2.selection.json".</summary>
    [ObservableProperty]
    private string _saveHint = string.Empty;

    /// <summary>Set to true only when the user confirms; the caller ignores the result otherwise.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Default file name offered by the save picker.</summary>
    public string SuggestedFileName => Sanitise(_game.Name) + SelectionManifest.Extension;

    /// <summary>Raised when the user asks to save; the window supplies a path via the save picker.</summary>
    public event Func<string, Task<string?>>? SaveRequested;

    public FileSelectionResult Result
    {
        get
        {
            var exclusions = new List<string>();
            _root.CollectExclusions(exclusions);
            return new FileSelectionResult(exclusions, _root.ExcludedBytes(), exclusions.Count);
        }
    }

    /// <summary>Raised when the user clicks Done or Cancel so the window can close itself.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void ResetToSuggested()
    {
        _root.ApplyDefault();
        UpdateSummary();
    }

    [RelayCommand]
    private void SelectAll()
    {
        _root.SetAll(true);
        UpdateSummary();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SaveRequested is null)
        {
            return;
        }

        var path = await SaveRequested.Invoke(SuggestedFileName);
        if (string.IsNullOrEmpty(path))
        {
            return; // Picker cancelled.
        }

        try
        {
            BuildSelectionManifest().Save(path);
            SaveHint = "Saved: " + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveHint = "Could not save: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Done()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Captures the current tree state as a selection file. Top-level kept content is listed as an
    /// inventory, and every fully-dropped subtree — top-level or nested — is recorded as excluded,
    /// so the file's own <c>DeriveExclusions</c> reproduces exactly what this window would pack.
    /// </summary>
    private SelectionManifest BuildSelectionManifest()
    {
        var entries = new List<SelectionEntry>();

        foreach (var top in _root.Children.Where(n => n.IsChecked != false))
        {
            entries.Add(EntryFor(top, include: true));
        }

        var excluded = new List<FileSelectionNodeViewModel>();
        _root.CollectExcludedNodes(excluded);
        foreach (var node in excluded)
        {
            entries.Add(EntryFor(node, include: false));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new SelectionManifest
        {
            AppId = _game.AppId,
            Title = _game.Name,
            InstallDir = _game.InstallDir,
            BuildId = _game.BuildId,
            CreatedBy = $"SteamDisc {typeof(SelectionManifest).Assembly.GetName().Version}",
            Entries = entries,
        };
    }

    private static SelectionEntry EntryFor(FileSelectionNodeViewModel node, bool include) => new()
    {
        Path = node.Path,
        Kind = node.IsFolder ? SelectionEntryKind.Folder : SelectionEntryKind.File,
        Size = node.Size,
        Include = include,
        Reason = node.Reason,
    };

    private static string Sanitise(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private void UpdateSummary()
    {
        SaveHint = string.Empty; // The selection moved on; a prior "Saved" line is now stale.
        var excludedBytes = _root.ExcludedBytes();
        if (excludedBytes <= 0)
        {
            Summary = $"All files included — {FileSelectionNodeViewModel.FormatBytes(_root.Size)} on disc.";
            return;
        }

        var exclusions = new List<string>();
        _root.CollectExclusions(exclusions);
        var kept = _root.Size - excludedBytes;

        Summary =
            $"Excluding {exclusions.Count} item(s), {FileSelectionNodeViewModel.FormatBytes(excludedBytes)} — " +
            $"{FileSelectionNodeViewModel.FormatBytes(kept)} will go on the disc. " +
            "Steam still lists these as installed, so a 'verify integrity' may re-download them.";
    }
}
