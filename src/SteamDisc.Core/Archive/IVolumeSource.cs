namespace SteamDisc.Core.Archive;

/// <summary>
/// Supplies archive volumes to the reader on demand.
/// </summary>
/// <remarks>
/// This is the seam that makes disc spanning fall out of the design rather than being bolted
/// on: when the reader runs off the end of volume N it simply asks for volume N+1, and an
/// implementation backed by optical media takes that as its cue to prompt for the next disc.
/// </remarks>
public interface IVolumeSource
{
    /// <summary>Number of volumes in the set.</summary>
    int VolumeCount { get; }

    /// <summary>
    /// Opens volume <paramref name="index"/> (1-based) for reading, blocking as long as it
    /// takes — including waiting for a human to insert a disc.
    /// </summary>
    Task<Stream> OpenVolumeAsync(int index, CancellationToken cancellationToken = default);
}

/// <summary>A volume source over files already present on disk.</summary>
public sealed class FileVolumeSource : IVolumeSource
{
    private readonly IReadOnlyList<string> _paths;

    public FileVolumeSource(IReadOnlyList<string> paths) => _paths = paths;

    public int VolumeCount => _paths.Count;

    public IReadOnlyList<string> Paths => _paths;

    public Task<Stream> OpenVolumeAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 1 || index > _paths.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Volume {index} is outside the set of {_paths.Count}.");
        }

        var path = _paths[index - 1];
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Archive volume {index} is missing.", path);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        return Task.FromResult(stream);
    }
}
