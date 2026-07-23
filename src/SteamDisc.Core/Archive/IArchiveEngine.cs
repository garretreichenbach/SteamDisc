using SteamDisc.Core.Progress;

namespace SteamDisc.Core.Archive;

/// <summary>Known archive format identifiers, as written into <c>payload.json</c>.</summary>
public static class ArchiveFormats
{
    /// <summary>SteamDisc's own container. The default, and the only one we can repair.</summary>
    public const string Sdz = "sdz";

    /// <summary>7-Zip, via an external <c>7z</c> executable. Better ratio, external dependency.</summary>
    public const string SevenZip = "7z";
}

public enum ArchiveCompression
{
    /// <summary>No compression. Fastest, and near-optimal for game data that is already packed.</summary>
    Store,

    /// <summary>Fast deflate. The default: meaningful savings without dominating build time.</summary>
    Fast,

    Balanced,

    /// <summary>Smallest output, substantially slower. Worth it when it saves a whole disc.</summary>
    Maximum,
}

/// <param name="SourceDirectory">Folder to pack — normally <c>steamapps/common/&lt;installdir&gt;</c>.</param>
/// <param name="OutputDirectory">Where volume files are written.</param>
/// <param name="BaseName">Volume base name, e.g. <c>payload</c>, giving <c>payload.sdz.001</c>.</param>
/// <param name="VolumeSize">Maximum bytes per volume.</param>
/// <param name="Compression">Compression effort.</param>
/// <param name="ExcludeRelativePaths">Relative paths to omit, compared case-insensitively.</param>
public sealed record ArchiveCreateRequest(
    string SourceDirectory,
    string OutputDirectory,
    string BaseName,
    long VolumeSize,
    ArchiveCompression Compression = ArchiveCompression.Fast,
    IReadOnlyCollection<string>? ExcludeRelativePaths = null);

/// <param name="VolumePaths">Volume files written, in order.</param>
/// <param name="UncompressedBytes">Total size of the packed content.</param>
/// <param name="CompressedBytes">Total size of the volumes.</param>
/// <param name="FileCount">Number of files packed.</param>
public sealed record ArchiveCreateResult(
    IReadOnlyList<string> VolumePaths,
    long UncompressedBytes,
    long CompressedBytes,
    int FileCount)
{
    public double CompressionRatio => UncompressedBytes > 0 ? (double)CompressedBytes / UncompressedBytes : 1d;
}

/// <param name="Volumes">Where volumes come from — a folder, or an optical drive with swaps.</param>
/// <param name="DestinationDirectory">Folder to extract into; created if absent.</param>
/// <param name="VerifyHashes">Check each file's recorded SHA-256 as it is written.</param>
/// <param name="Overwrite">Replace files that already exist at the destination.</param>
public sealed record ArchiveExtractRequest(
    IVolumeSource Volumes,
    string DestinationDirectory,
    bool VerifyHashes = true,
    bool Overwrite = true);

/// <param name="FileCount">Files extracted.</param>
/// <param name="UncompressedBytes">Bytes written to disk.</param>
public sealed record ArchiveExtractResult(int FileCount, long UncompressedBytes);

/// <summary>Packs and unpacks the payload. Implementations own their on-disc format entirely.</summary>
public interface IArchiveEngine
{
    /// <summary>Identifier written to <c>payload.json</c>; see <see cref="ArchiveFormats"/>.</summary>
    string FormatId { get; }

    /// <summary>File extension for volumes, without a leading dot, e.g. <c>sdz</c>.</summary>
    string VolumeExtension { get; }

    /// <summary>
    /// False when the engine needs something this machine lacks — an external <c>7z</c>, say.
    /// Checked before authoring so the failure is a clear message, not a mid-build crash.
    /// </summary>
    bool IsAvailable { get; }

    Task<ArchiveCreateResult> CreateAsync(
        ArchiveCreateRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ArchiveExtractResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an archive is malformed, truncated, or fails verification.</summary>
public sealed class ArchiveIntegrityException : Exception
{
    public ArchiveIntegrityException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Resolves a format id to an engine.</summary>
public sealed class ArchiveEngineRegistry
{
    private readonly List<IArchiveEngine> _engines;

    public ArchiveEngineRegistry(params IArchiveEngine[] engines) => _engines = engines.ToList();

    /// <summary>Registry with every engine this build knows about.</summary>
    public static ArchiveEngineRegistry CreateDefault()
        => new(new SdzArchiveEngine(), new SevenZipArchiveEngine());

    public IReadOnlyList<IArchiveEngine> Engines => _engines;

    public IArchiveEngine? Find(string formatId)
        => _engines.FirstOrDefault(e => string.Equals(e.FormatId, formatId, StringComparison.OrdinalIgnoreCase));

    public IArchiveEngine Require(string formatId)
    {
        var engine = Find(formatId)
                     ?? throw new NotSupportedException(
                         $"Unknown archive format '{formatId}'. This build supports: " +
                         string.Join(", ", _engines.Select(e => e.FormatId)) + ".");

        if (!engine.IsAvailable)
        {
            throw new NotSupportedException(
                $"Archive format '{formatId}' is not usable on this machine. " +
                (formatId == ArchiveFormats.SevenZip
                    ? "Install 7-Zip and make sure '7z' is on PATH."
                    : "The engine reported itself unavailable."));
        }

        return engine;
    }
}
