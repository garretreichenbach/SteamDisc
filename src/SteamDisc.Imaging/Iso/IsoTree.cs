namespace SteamDisc.Imaging.Iso;

/// <summary>A node in the image's directory tree, carrying both name spellings.</summary>
internal sealed class IsoNode
{
    private IsoNode(string name, bool isDirectory, string? sourcePath, long length)
    {
        Name = name;
        IsDirectory = isDirectory;
        SourcePath = sourcePath;
        Length = length;
    }

    public static IsoNode Directory(string name) => new(name, isDirectory: true, sourcePath: null, length: 0);

    public static IsoNode File(string name, string sourcePath, long length)
        => new(name, isDirectory: false, sourcePath, length);

    /// <summary>The original on-disk name.</summary>
    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>Where the file's bytes come from; null for directories.</summary>
    public string? SourcePath { get; }

    public long Length { get; set; }

    public List<IsoNode> Children { get; } = new();

    public IsoNode? Parent { get; set; }

    /// <summary>ISO 9660 identifier, assigned during layout.</summary>
    public string IsoIdentifier { get; set; } = string.Empty;

    /// <summary>Joliet identifier, assigned during layout.</summary>
    public string JolietIdentifier { get; set; } = string.Empty;

    /// <summary>First sector of this node's data (a directory extent, or file content).</summary>
    public uint IsoExtent { get; set; }

    public uint JolietExtent { get; set; }

    /// <summary>Byte length of the directory extent in the primary tree.</summary>
    public uint IsoDirectoryLength { get; set; }

    public uint JolietDirectoryLength { get; set; }

    /// <summary>1-based index in the path table, assigned during layout.</summary>
    public ushort PathTableIndex { get; set; }

    public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;

    public IEnumerable<IsoNode> Directories => Children.Where(c => c.IsDirectory);

    public IEnumerable<IsoNode> Files => Children.Where(c => !c.IsDirectory);

    public override string ToString() => IsDirectory ? Name + "/" : Name;
}

internal static class IsoTreeBuilder
{
    /// <summary>Builds a tree from a staging folder, skipping nothing and following no links.</summary>
    public static IsoNode FromDirectory(string path)
    {
        var root = IsoNode.Directory(string.Empty);
        Populate(root, new DirectoryInfo(path));
        return root;
    }

    private static void Populate(IsoNode node, DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var childNode = IsoNode.Directory(child.Name);
            childNode.Parent = node;
            childNode.LastWriteUtc = child.LastWriteTimeUtc;
            node.Children.Add(childNode);
            Populate(childNode, child);
        }

        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var childNode = IsoNode.File(file.Name, file.FullName, file.Length);
            childNode.Parent = node;
            childNode.LastWriteUtc = file.LastWriteTimeUtc;
            node.Children.Add(childNode);
        }
    }

    /// <summary>Every directory, breadth-first — the order both path tables require.</summary>
    public static List<IsoNode> EnumerateDirectoriesBreadthFirst(IsoNode root)
    {
        var result = new List<IsoNode> { root };
        for (var i = 0; i < result.Count; i++)
        {
            result.AddRange(result[i].Directories);
        }

        return result;
    }

    public static IEnumerable<IsoNode> EnumerateFiles(IsoNode root)
    {
        foreach (var directory in EnumerateDirectoriesBreadthFirst(root))
        {
            foreach (var file in directory.Files)
            {
                yield return file;
            }
        }
    }
}
