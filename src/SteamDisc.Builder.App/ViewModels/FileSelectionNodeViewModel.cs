using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamDisc.Authoring;

namespace SteamDisc.Builder.App.ViewModels;

/// <summary>
/// One node in the file-selection tree. Wraps a <see cref="SelectionNode"/> and adds a tri-state
/// checkbox that cascades down to its children and rolls up from them: check a folder and its whole
/// subtree follows; check some-but-not-all of a folder's children and the folder shows indeterminate.
/// </summary>
public sealed partial class FileSelectionNodeViewModel : ObservableObject
{
    private readonly FileSelectionNodeViewModel? _parent;
    private readonly Action? _onUserChange;
    private readonly bool _defaultInclude;

    // Set while we are propagating a change, so cascade/rollup edits don't re-enter the handler.
    private bool _suppress;

    public FileSelectionNodeViewModel(SelectionNode node, FileSelectionNodeViewModel? parent, Action? onUserChange)
    {
        _parent = parent;
        _onUserChange = onUserChange;
        _defaultInclude = node.DefaultInclude;

        Path = node.Path;
        Name = node.Name;
        IsFolder = node.IsFolder;
        Size = node.Size;
        SizeText = FormatBytes(node.Size);
        Reason = node.Reason;

        Children = new ObservableCollection<FileSelectionNodeViewModel>(
            node.Children.Select(c => new FileSelectionNodeViewModel(c, this, onUserChange)));

        // Defaults propagate uniformly through a subtree, so a folder always agrees with its
        // children at first — no rollup needed until the user changes something.
        _isChecked = node.DefaultInclude;
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsFolder { get; }

    public long Size { get; }

    public string SizeText { get; }

    public string? Reason { get; }

    public bool HasReason => !string.IsNullOrEmpty(Reason);

    public ObservableCollection<FileSelectionNodeViewModel> Children { get; }

    /// <summary>Null when this folder is partially selected; true kept, false dropped.</summary>
    [ObservableProperty]
    private bool? _isChecked = true;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppress)
        {
            return;
        }

        if (value is bool concrete)
        {
            CascadeToChildren(concrete);
        }

        _parent?.RollUpFromChildren();
        _onUserChange?.Invoke();
    }

    private void CascadeToChildren(bool value)
    {
        foreach (var child in Children)
        {
            child._suppress = true;
            child.IsChecked = value;
            child._suppress = false;
            child.CascadeToChildren(value);
        }
    }

    private void RollUpFromChildren()
    {
        if (Children.Count > 0)
        {
            var allOn = Children.All(c => c.IsChecked == true);
            var allOff = Children.All(c => c.IsChecked == false);

            _suppress = true;
            IsChecked = allOn ? true : allOff ? false : null;
            _suppress = false;
        }

        _parent?.RollUpFromChildren();
    }

    /// <summary>Restores the heuristic default across this subtree (the "Reset to suggested" action).</summary>
    public void ApplyDefault()
    {
        _suppress = true;
        IsChecked = _defaultInclude;
        _suppress = false;

        foreach (var child in Children)
        {
            child.ApplyDefault();
        }
    }

    /// <summary>Forces this subtree fully on or off, without firing per-node change callbacks.</summary>
    public void SetAll(bool value)
    {
        _suppress = true;
        IsChecked = value;
        _suppress = false;

        foreach (var child in Children)
        {
            child.SetAll(value);
        }
    }

    /// <summary>Drops this node (and subtree) if its path is in the set — used to reopen with prior choices.</summary>
    public void ApplyExclusions(ISet<string> excluded)
    {
        if (excluded.Contains(Path))
        {
            SetAll(false);
            return;
        }

        foreach (var child in Children)
        {
            child.ApplyExclusions(excluded);
        }
    }

    /// <summary>Recomputes folder states bottom-up after a bulk change; call on the root.</summary>
    public void RecomputeSubtree()
    {
        if (Children.Count == 0)
        {
            return;
        }

        foreach (var child in Children)
        {
            child.RecomputeSubtree();
        }

        var allOn = Children.All(c => c.IsChecked == true);
        var allOff = Children.All(c => c.IsChecked == false);

        _suppress = true;
        IsChecked = allOn ? true : allOff ? false : null;
        _suppress = false;
    }

    /// <summary>The largest fully-dropped subtrees, as exclusion paths for the archive engine.</summary>
    public void CollectExclusions(List<string> into)
    {
        if (IsChecked == true)
        {
            return;
        }

        if (IsChecked == false)
        {
            if (Path.Length > 0)
            {
                into.Add(Path);
            }

            return;
        }

        foreach (var child in Children)
        {
            child.CollectExclusions(into);
        }
    }

    /// <summary>The largest fully-dropped subtrees as nodes, so a saved file can record their sizes.</summary>
    public void CollectExcludedNodes(List<FileSelectionNodeViewModel> into)
    {
        if (IsChecked == true)
        {
            return;
        }

        if (IsChecked == false)
        {
            if (Path.Length > 0)
            {
                into.Add(this);
            }

            return;
        }

        foreach (var child in Children)
        {
            child.CollectExcludedNodes(into);
        }
    }

    /// <summary>Bytes under this node that are currently dropped.</summary>
    public long ExcludedBytes()
    {
        if (IsChecked == true)
        {
            return 0;
        }

        if (IsChecked == false)
        {
            return Size;
        }

        return Children.Sum(c => c.ExcludedBytes());
    }

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
