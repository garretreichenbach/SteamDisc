namespace SteamDisc.Core.Vdf;

/// <summary>
/// A node in a Valve KeyValues document. A node is either a <em>leaf</em> (it has a
/// <see cref="Value"/>) or an <em>object</em> (it has <see cref="Children"/>).
/// </summary>
/// <remarks>
/// Document order and duplicate keys are both preserved, because round-tripping an
/// <c>appmanifest_*.acf</c> byte-for-byte is a hard requirement: anything we fail to
/// understand must survive being read and written back out.
/// </remarks>
public sealed class KvNode
{
    private readonly List<KvNode> _children = new();

    private KvNode(string key, string? value, bool isObject)
    {
        Key = key;
        Value = value;
        IsObject = isObject;
    }

    /// <summary>Creates a leaf node holding a string value.</summary>
    public static KvNode Leaf(string key, string value) => new(key, value, isObject: false);

    /// <summary>Creates an object node that can hold children.</summary>
    public static KvNode Object(string key) => new(key, null, isObject: true);

    public string Key { get; set; }

    /// <summary>The leaf value, or <see langword="null"/> for object nodes.</summary>
    public string? Value { get; set; }

    /// <summary>True when this node holds children rather than a value.</summary>
    public bool IsObject { get; }

    /// <summary>
    /// An optional platform conditional such as <c>$WIN32</c>, written back as <c>[$WIN32]</c>.
    /// Steam uses these in some config files; we never evaluate them, only preserve them.
    /// </summary>
    public string? Condition { get; set; }

    public IReadOnlyList<KvNode> Children => _children;

    /// <summary>First child with the given key, or <see langword="null"/>. Key comparison is case-insensitive.</summary>
    public KvNode? this[string key] => Find(key);

    public KvNode? Find(string key)
    {
        foreach (var child in _children)
        {
            if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    public IEnumerable<KvNode> FindAll(string key)
        => _children.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public KvNode Add(KvNode child)
    {
        EnsureObject();
        _children.Add(child);
        return child;
    }

    public bool Remove(string key)
    {
        var removed = false;
        for (var i = _children.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_children[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                _children.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>Reads a leaf value by key, or <paramref name="fallback"/> when absent.</summary>
    public string? GetString(string key, string? fallback = null) => Find(key)?.Value ?? fallback;

    public long GetInt64(string key, long fallback = 0)
        => long.TryParse(GetString(key), out var value) ? value : fallback;

    public ulong GetUInt64(string key, ulong fallback = 0)
        => ulong.TryParse(GetString(key), out var value) ? value : fallback;

    public uint GetUInt32(string key, uint fallback = 0)
        => uint.TryParse(GetString(key), out var value) ? value : fallback;

    /// <summary>
    /// Sets a leaf value, updating the first existing node with that key so its position in
    /// the document is retained, or appending a new node when there is none.
    /// </summary>
    public KvNode SetString(string key, string value)
    {
        EnsureObject();
        var existing = Find(key);
        if (existing is { IsObject: false })
        {
            existing.Value = value;
            return existing;
        }

        if (existing is not null)
        {
            // Key exists but as an object; replace it in place rather than emitting a duplicate.
            var index = _children.IndexOf(existing);
            var replacement = Leaf(existing.Key, value);
            _children[index] = replacement;
            return replacement;
        }

        return Add(Leaf(key, value));
    }

    public KvNode SetInt64(string key, long value)
        => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public KvNode SetUInt64(string key, ulong value)
        => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Returns the child object with this key, creating it if it does not exist.</summary>
    public KvNode GetOrAddObject(string key)
    {
        var existing = Find(key);
        if (existing is { IsObject: true })
        {
            return existing;
        }

        if (existing is not null)
        {
            var index = _children.IndexOf(existing);
            var replacement = Object(existing.Key);
            _children[index] = replacement;
            return replacement;
        }

        return Add(Object(key));
    }

    /// <summary>Deep copy, so a source manifest can be transplanted without mutating the original.</summary>
    public KvNode Clone()
    {
        var copy = IsObject ? Object(Key) : Leaf(Key, Value ?? string.Empty);
        copy.Condition = Condition;
        foreach (var child in _children)
        {
            copy._children.Add(child.Clone());
        }

        return copy;
    }

    private void EnsureObject()
    {
        if (!IsObject)
        {
            throw new InvalidOperationException($"KeyValues node '{Key}' is a value, not an object.");
        }
    }

    public override string ToString() => IsObject ? $"{Key} {{ {_children.Count} }}" : $"{Key} = {Value}";
}
