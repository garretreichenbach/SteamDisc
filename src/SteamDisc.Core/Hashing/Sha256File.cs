using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SteamDisc.Core.Hashing;

/// <summary>
/// Reads and writes <c>sha256sum</c>-format sidecars — the <c>data/payload.sha256</c> that
/// travels on the disc. The format is deliberately the standard one so a suspicious disc can
/// be checked with <c>sha256sum -c</c> without our tooling.
/// </summary>
public static class Sha256File
{
    /// <summary>Computes the SHA-256 of a file as lowercase hex.</summary>
    public static async Task<string> ComputeAsync(
        string path,
        Action<long>? onBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        return await ComputeAsync(stream, onBytesRead, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeAsync(
        Stream stream,
        Action<long>? onBytesRead = null,
        CancellationToken cancellationToken = default)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            onBytesRead?.Invoke(read);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Serialises entries in <c>sha256sum</c> format: hash, two spaces, path.</summary>
    public static string Format(IEnumerable<Sha256Entry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Hash).Append("  ").Append(entry.Path.Replace('\\', '/')).Append('\n');
        }

        return builder.ToString();
    }

    public static void Write(string path, IEnumerable<Sha256Entry> entries)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Format(entries), new UTF8Encoding(false));
    }

    public static IReadOnlyList<Sha256Entry> Parse(string text)
    {
        var entries = new List<Sha256Entry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var hash = line[..separator];
            // sha256sum writes "hash  path" for text mode and "hash *path" for binary mode.
            var path = line[separator..].TrimStart(' ', '*');
            if (hash.Length == 64 && path.Length > 0)
            {
                entries.Add(new Sha256Entry(hash.ToLowerInvariant(), path));
            }
        }

        return entries;
    }

    public static IReadOnlyList<Sha256Entry> Read(string path) => Parse(File.ReadAllText(path));
}

/// <param name="Hash">Lowercase hex SHA-256.</param>
/// <param name="Path">Path relative to the sidecar's base directory, using forward slashes.</param>
public readonly record struct Sha256Entry(string Hash, string Path)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Hash}  {Path}");
}
