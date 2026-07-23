using SteamDisc.Core.Payload;
using SteamDisc.Imaging;

namespace SteamDisc.Authoring;

/// <summary>
/// Decides how many discs a payload needs and which volumes go on each.
/// </summary>
/// <remarks>
/// Spanning is a v1 feature rather than a v2 nicety, because plenty of modern titles exceed
/// even BD-R XL. The plan is computed before anything is compressed so the Builder can tell
/// the user "this will be three discs" while they can still change their mind about the
/// compression level.
/// </remarks>
public static class DiscSpanPlanner
{
    /// <summary>
    /// Fixed cost per disc: the runtime executable, theme, manifests and the image's own
    /// structures. Generous on purpose — running out of room at burn time is far worse than
    /// leaving a few hundred megabytes unused.
    /// </summary>
    public const long PerDiscOverheadBytes = 192L * 1024 * 1024;

    /// <summary>Chooses a volume size that divides evenly into the medium.</summary>
    /// <remarks>
    /// Volumes are also capped below 4 GiB so the ISO 9660 file size limit is never in play,
    /// which is what lets the imaging layer skip UDF entirely.
    /// </remarks>
    public static long ChooseVolumeSize(OpticalMedium medium, long requestedVolumeSize = 0)
    {
        const long maxVolumeSize = (4L * 1024 * 1024 * 1024) - (64L * 1024 * 1024);

        if (requestedVolumeSize > 0)
        {
            return Math.Min(requestedVolumeSize, maxVolumeSize);
        }

        if (medium.CapacityBytes == long.MaxValue)
        {
            return Math.Min(Core.Archive.SdzFormat.DefaultVolumeSize, maxVolumeSize);
        }

        var usable = medium.UsableForPayload(PerDiscOverheadBytes);

        // Aim for a handful of volumes per disc: small enough that a damaged volume costs
        // little, large enough that per-volume overhead stays irrelevant.
        var target = usable / 8;
        return Math.Clamp(target, 256L * 1024 * 1024, maxVolumeSize);
    }

    /// <summary>Assigns already-created volumes to discs, filling each disc before starting the next.</summary>
    public static DiscSpanPlan Assign(IReadOnlyList<VolumeDescriptor> volumes, OpticalMedium medium)
    {
        var capacity = medium.UsableForPayload(PerDiscOverheadBytes);
        var assignments = new List<VolumeDescriptor>(volumes.Count);

        var disc = 1;
        long used = 0;

        foreach (var volume in volumes)
        {
            if (volume.Size > capacity)
            {
                throw new InvalidOperationException(
                    $"Volume {volume.Index} is {volume.Size:N0} bytes, which does not fit on {medium.Name} " +
                    $"({capacity:N0} bytes usable). Choose a smaller volume size or larger media.");
            }

            if (used + volume.Size > capacity && used > 0)
            {
                disc++;
                used = 0;
            }

            volume.Disc = disc;
            used += volume.Size;
            assignments.Add(volume);
        }

        return new DiscSpanPlan(disc, assignments, medium, capacity);
    }

    /// <summary>Predicts the disc count from an estimated compressed size, before packing.</summary>
    public static int EstimateDiscCount(long estimatedCompressedBytes, OpticalMedium medium)
    {
        if (medium.CapacityBytes == long.MaxValue)
        {
            return 1;
        }

        var capacity = medium.UsableForPayload(PerDiscOverheadBytes);
        return capacity <= 0 ? 1 : (int)Math.Max(1, Math.Ceiling(estimatedCompressedBytes / (double)capacity));
    }
}

/// <param name="DiscCount">Number of discs required.</param>
/// <param name="Volumes">Volumes with their <c>Disc</c> assigned.</param>
/// <param name="Medium">The medium planned for.</param>
/// <param name="UsableBytesPerDisc">Payload capacity per disc after overhead.</param>
public sealed record DiscSpanPlan(
    int DiscCount,
    IReadOnlyList<VolumeDescriptor> Volumes,
    OpticalMedium Medium,
    long UsableBytesPerDisc)
{
    public IEnumerable<IGrouping<int, VolumeDescriptor>> ByDisc() => Volumes.GroupBy(v => v.Disc);

    public long BytesOnDisc(int discNumber) => Volumes.Where(v => v.Disc == discNumber).Sum(v => v.Size);
}
