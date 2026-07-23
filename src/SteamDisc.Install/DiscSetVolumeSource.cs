using SteamDisc.Core.Archive;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;

namespace SteamDisc.Install;

/// <summary>
/// Supplies archive volumes from a disc set, prompting for a swap when the next volume lives
/// on a disc that is not currently in the drive.
/// </summary>
/// <remarks>
/// Every swap re-reads the candidate disc's own <c>payload.json</c> and checks its
/// <c>setId</c> and disc number before trusting a single byte from it. The plan calls out
/// Steam's own multi-disc bug — where later discs are silently ignored and the content is
/// re-downloaded instead — and the defence against the general class of that problem is to
/// never assume the disc in the drive is the one that was asked for.
/// </remarks>
public sealed class DiscSetVolumeSource : IVolumeSource
{
    private readonly PayloadManifest _manifest;
    private readonly IInstallHost _host;
    private readonly ISteamDiscLogger _logger;
    private readonly Dictionary<int, string> _discRoots = new();

    public DiscSetVolumeSource(
        PayloadManifest manifest,
        string initialDiscRoot,
        IInstallHost host,
        ISteamDiscLogger? logger = null)
    {
        _manifest = manifest;
        _host = host;
        _logger = logger ?? NullLogger.Instance;
        _discRoots[manifest.Disc.Number] = initialDiscRoot;
    }

    public int VolumeCount => _manifest.Archive.Volumes.Count;

    /// <summary>Raised when a disc swap is about to be requested, for progress UI.</summary>
    public event Action<int>? DiscSwapRequired;

    public async Task<Stream> OpenVolumeAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 1 || index > _manifest.Archive.Volumes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"Volume {index} is outside this set of {_manifest.Archive.Volumes.Count}.");
        }

        var volume = _manifest.Archive.Volumes[index - 1];
        var root = await ResolveDiscRootAsync(volume.Disc, cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(root, volume.Path.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Volume {index} ({volume.Path}) is missing from disc {volume.Disc}.", path);
        }

        if (volume.Size > 0)
        {
            var actual = new FileInfo(path).Length;
            if (actual != volume.Size)
            {
                throw new ArchiveIntegrityException(
                    $"Volume {index} on disc {volume.Disc} is {actual} bytes but should be {volume.Size}. " +
                    "The disc may be damaged or is from a different build.");
            }
        }

        _logger.Debug($"Opening volume {index} from disc {volume.Disc} at '{path}'.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
    }

    private async Task<string> ResolveDiscRootAsync(int discNumber, CancellationToken cancellationToken)
    {
        if (_discRoots.TryGetValue(discNumber, out var known) && IsDiscStillPresent(known, discNumber))
        {
            return known;
        }

        _discRoots.Remove(discNumber);
        DiscSwapRequired?.Invoke(discNumber);

        string? reason = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new DiscRequest(
                discNumber,
                _manifest.Disc.Of,
                _manifest.Disc.SetId,
                _manifest.Title,
                reason);

            var candidate = await _host.RequestDiscAsync(request, cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                throw new OperationCanceledException($"Disc {discNumber} was not provided.");
            }

            var verification = VerifyDisc(candidate, discNumber);
            if (verification is null)
            {
                _discRoots[discNumber] = candidate;
                _logger.Info($"Disc {discNumber} accepted at '{candidate}'.");
                return candidate;
            }

            _logger.Warn($"Rejected disc at '{candidate}': {verification}");
            reason = verification;
        }
    }

    /// <summary>Returns null when the disc is the right one, or a reason to show the user.</summary>
    private string? VerifyDisc(string root, int expectedDiscNumber)
    {
        var manifestPath = Path.Combine(root, PayloadManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return $"There is no {PayloadManifest.FileName} on that disc.";
        }

        PayloadManifest candidate;
        try
        {
            candidate = PayloadManifest.Load(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return $"That disc's {PayloadManifest.FileName} could not be read: {ex.Message}";
        }

        if (!string.Equals(candidate.Disc.SetId, _manifest.Disc.SetId, StringComparison.OrdinalIgnoreCase))
        {
            return $"That disc belongs to a different set ({candidate.Title}).";
        }

        if (candidate.Disc.Number != expectedDiscNumber)
        {
            return $"That is disc {candidate.Disc.Number}; disc {expectedDiscNumber} is needed.";
        }

        return null;
    }

    /// <summary>
    /// Cheap re-check that the media we cached a path for is still mounted. Optical drives get
    /// opened by hand mid-install more often than one would like.
    /// </summary>
    private static bool IsDiscStillPresent(string root, int discNumber)
    {
        _ = discNumber;
        return File.Exists(Path.Combine(root, PayloadManifest.FileName));
    }
}
