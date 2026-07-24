namespace SteamDisc.Authoring;

/// <summary>
/// A lightweight, UI-free tree of a game folder — the data behind the Builder's file-selection
/// window. It carries just enough per node (path, size, and the heuristic default) for a checkbox
/// tree to render and for exclusions to be derived, without any view type leaking into Authoring.
/// </summary>
/// <remarks>
/// Built live from the folder each time, so it always reflects what is actually on disk. The
/// heuristic default (kept or dropped) is decided at the top level by <see cref="SelectionHeuristics"/>
/// and propagated down, so a folder and its whole subtree start in agreement — mixed states only
/// appear once the user edits one.
/// </remarks>
public sealed class SelectionNode
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>Total bytes under this node (its own size for a file).</summary>
    public long Size { get; set; }

    /// <summary>What the heuristics suggest: true to keep, false to drop.</summary>
    public bool DefaultInclude { get; init; } = true;

    /// <summary>Why a node is flagged, e.g. "non-English language pack (heuristic)". Top level only.</summary>
    public string? Reason { get; init; }

    public List<SelectionNode> Children { get; } = new();
}

public static class SelectionTree
{
    /// <summary>Walks a game's install folder into a tree with heuristic defaults applied.</summary>
    public static SelectionNode Build(GameCandidate game)
    {
        var root = new SelectionNode
        {
            Path = string.Empty,
            Name = game.InstallDir,
            IsFolder = true,
        };

        Populate(game.InstallPath, string.Empty, root, topLevel: true, inheritedInclude: true);
        root.Size = root.Children.Sum(c => c.Size);
        return root;
    }

    private static void Populate(
        string absoluteDir,
        string relativeDir,
        SelectionNode parent,
        bool topLevel,
        bool inheritedInclude)
    {
        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(absoluteDir).OrderBy(d => d, StringComparer.Ordinal);
            files = Directory.EnumerateFiles(absoluteDir).OrderBy(f => f, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = System.IO.Path.GetFileName(directory);
            var relative = Combine(relativeDir, name);
            var reason = topLevel ? SelectionHeuristics.Classify(name) : null;
            var include = topLevel ? reason is null : inheritedInclude;

            var node = new SelectionNode
            {
                Path = relative,
                Name = name,
                IsFolder = true,
                DefaultInclude = include,
                Reason = reason,
            };

            Populate(directory, relative, node, topLevel: false, inheritedInclude: include);
            node.Size = node.Children.Sum(c => c.Size);
            parent.Children.Add(node);
        }

        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            var relative = Combine(relativeDir, name);
            var reason = topLevel ? SelectionHeuristics.Classify(name) : null;
            var include = topLevel ? reason is null : inheritedInclude;

            parent.Children.Add(new SelectionNode
            {
                Path = relative,
                Name = name,
                IsFolder = false,
                Size = SafeLength(file),
                DefaultInclude = include,
                Reason = reason,
            });
        }
    }

    private static string Combine(string parent, string name)
        => parent.Length == 0 ? name : parent + "/" + name;

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
