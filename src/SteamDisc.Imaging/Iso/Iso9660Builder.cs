using System.Buffers.Binary;
using System.Text;
using SteamDisc.Core.Progress;

namespace SteamDisc.Imaging.Iso;

/// <summary>Options for <see cref="Iso9660Builder"/>.</summary>
/// <param name="VolumeLabel">Volume identifier. Uppercased and truncated to 32 characters.</param>
/// <param name="Publisher">Publisher field, shown by some disc utilities.</param>
/// <param name="ApplicationId">Application identifier field.</param>
public sealed record IsoBuildOptions(
    string VolumeLabel,
    string Publisher = "SteamDisc",
    string ApplicationId = "SteamDisc");

/// <summary>
/// Writes an ISO 9660 image with a Joliet supplementary tree.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than pulled from a library because the format is small, stable and
/// forty years old, while an image writer is on the critical path of the one deliverable the
/// project actually produces.
/// </para>
/// <para>
/// Deliberately not UDF. UDF's reason to exist here would be files of 4 GB and over, and the
/// payload archive already splits into volumes well below that limit for exactly this reason —
/// which leaves ISO 9660 + Joliet, readable by every OS and every drive, as the better trade.
/// A file at or over 4 GiB is rejected with an explanation rather than silently truncated.
/// </para>
/// </remarks>
public sealed class Iso9660Builder
{
    /// <summary>ISO 9660 records extents as a 32-bit byte count, so this is a hard ceiling.</summary>
    public const long MaxFileSize = uint.MaxValue;

    private const int SystemAreaSectors = 16;

    public async Task<IsoBuildResult> BuildAsync(
        string sourceDirectory,
        string outputPath,
        IsoBuildOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"'{sourceDirectory}' does not exist.");
        }

        var tracker = new ProgressTracker(progress, OperationPhase.BuildingImage);

        var root = IsoTreeBuilder.FromDirectory(sourceDirectory);
        AssignIdentifiers(root);

        foreach (var file in IsoTreeBuilder.EnumerateFiles(root))
        {
            if (file.Length >= MaxFileSize)
            {
                throw new NotSupportedException(
                    $"'{file.Name}' is {file.Length:N0} bytes. ISO 9660 cannot record a file of 4 GiB or more. " +
                    "Reduce the payload volume size so every file stays below that limit.");
            }
        }

        var layout = ComputeLayout(root);
        tracker.SetTotals(layout.TotalSectors * (long)IsoWriter.SectorSize);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var output = new FileStream(
                         outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            await WriteImageAsync(output, root, layout, options, tracker, cancellationToken).ConfigureAwait(false);
        }

        tracker.Finish();

        return new IsoBuildResult(
            outputPath,
            layout.TotalSectors * (long)IsoWriter.SectorSize,
            IsoTreeBuilder.EnumerateFiles(root).Count(),
            IsoTreeBuilder.EnumerateDirectoriesBreadthFirst(root).Count);
    }

    /// <summary>
    /// Predicts the image size without writing it, so the Builder can tell a user their
    /// selection will not fit before spending twenty minutes proving it.
    /// </summary>
    public long EstimateSize(string sourceDirectory)
    {
        var root = IsoTreeBuilder.FromDirectory(sourceDirectory);
        AssignIdentifiers(root);
        return ComputeLayout(root).TotalSectors * (long)IsoWriter.SectorSize;
    }

    /// <summary>Assigns unique ISO and Joliet identifiers within each directory.</summary>
    private static void AssignIdentifiers(IsoNode root)
    {
        foreach (var directory in IsoTreeBuilder.EnumerateDirectoriesBreadthFirst(root))
        {
            var usedIso = new HashSet<string>(StringComparer.Ordinal);
            var usedJoliet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in directory.Children)
            {
                var suffix = 0;
                string identifier;
                do
                {
                    identifier = child.IsDirectory
                        ? IsoNames.ToIsoDirectoryName(child.Name, suffix)
                        : IsoNames.ToIsoFileName(child.Name, suffix);
                    suffix++;
                }
                while (!usedIso.Add(identifier));

                child.IsoIdentifier = identifier;

                var joliet = IsoNames.ToJolietName(child.Name);
                var jolietSuffix = 1;
                while (!usedJoliet.Add(joliet))
                {
                    var stem = Path.GetFileNameWithoutExtension(child.Name);
                    var extension = Path.GetExtension(child.Name);
                    joliet = IsoNames.ToJolietName($"{stem}~{jolietSuffix}{extension}");
                    jolietSuffix++;
                }

                child.JolietIdentifier = joliet;
            }

            // Directory records must appear in identifier order in both trees.
            directory.Children.Sort((a, b) => IsoNames.CompareIsoIdentifiers(a.IsoIdentifier, b.IsoIdentifier));
        }
    }

    private static IsoLayout ComputeLayout(IsoNode root)
    {
        var directories = IsoTreeBuilder.EnumerateDirectoriesBreadthFirst(root);
        for (var i = 0; i < directories.Count; i++)
        {
            directories[i].PathTableIndex = (ushort)(i + 1);
        }

        foreach (var node in directories)
        {
            node.IsoDirectoryLength = (uint)MeasureDirectoryExtent(node, joliet: false);
            node.JolietDirectoryLength = (uint)MeasureDirectoryExtent(node, joliet: true);
        }

        var isoPathTableSize = MeasurePathTable(directories, joliet: false);
        var jolietPathTableSize = MeasurePathTable(directories, joliet: true);

        // Sector 16 primary descriptor, 17 Joliet descriptor, 18 terminator.
        var sector = (uint)SystemAreaSectors + 3;

        var isoPathTableL = sector;
        sector += IsoWriter.SectorsFor(isoPathTableSize);
        var isoPathTableM = sector;
        sector += IsoWriter.SectorsFor(isoPathTableSize);
        var jolietPathTableL = sector;
        sector += IsoWriter.SectorsFor(jolietPathTableSize);
        var jolietPathTableM = sector;
        sector += IsoWriter.SectorsFor(jolietPathTableSize);

        foreach (var node in directories)
        {
            node.IsoExtent = sector;
            sector += IsoWriter.SectorsFor(node.IsoDirectoryLength);
        }

        foreach (var node in directories)
        {
            node.JolietExtent = sector;
            sector += IsoWriter.SectorsFor(node.JolietDirectoryLength);
        }

        // File data is shared by both trees, so each file gets one extent.
        foreach (var file in IsoTreeBuilder.EnumerateFiles(root))
        {
            file.IsoExtent = sector;
            file.JolietExtent = sector;
            sector += IsoWriter.SectorsFor(file.Length);
        }

        return new IsoLayout(
            directories,
            isoPathTableSize,
            jolietPathTableSize,
            isoPathTableL,
            isoPathTableM,
            jolietPathTableL,
            jolietPathTableM,
            sector);
    }

    private static int MeasureDirectoryExtent(IsoNode node, bool joliet)
    {
        // "." and ".." are single-byte identifiers, so both records are 34 bytes.
        var length = 34 + 34;
        var sectorRemaining = IsoWriter.SectorSize - length;

        foreach (var child in node.Children)
        {
            var recordLength = DirectoryRecordLength(child, joliet);
            if (recordLength > sectorRemaining)
            {
                // A directory record may not straddle a sector boundary.
                length += sectorRemaining;
                sectorRemaining = IsoWriter.SectorSize;
            }

            length += recordLength;
            sectorRemaining -= recordLength;
        }

        return length;
    }

    private static int DirectoryRecordLength(IsoNode node, bool joliet)
    {
        var identifierLength = joliet
            ? Encoding.BigEndianUnicode.GetByteCount(node.JolietIdentifier)
            : node.IsoIdentifier.Length;

        var length = 33 + identifierLength;
        if (length % 2 != 0)
        {
            length++;
        }

        return length;
    }

    private static int MeasurePathTable(IReadOnlyList<IsoNode> directories, bool joliet)
    {
        var total = 0;
        foreach (var node in directories)
        {
            var identifierLength = node.Parent is null
                ? 1
                : joliet
                    ? Encoding.BigEndianUnicode.GetByteCount(node.JolietIdentifier)
                    : node.IsoIdentifier.Length;

            var length = 8 + identifierLength;
            if (length % 2 != 0)
            {
                length++;
            }

            total += length;
        }

        return total;
    }

    private static async Task WriteImageAsync(
        Stream output,
        IsoNode root,
        IsoLayout layout,
        IsoBuildOptions options,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var sector = new byte[IsoWriter.SectorSize];
        var now = DateTime.UtcNow;

        // System area: 16 empty sectors, where a boot record would live.
        for (var i = 0; i < SystemAreaSectors; i++)
        {
            Array.Clear(sector);
            await WriteSectorAsync(output, sector, tracker, cancellationToken).ConfigureAwait(false);
        }

        Array.Clear(sector);
        WriteVolumeDescriptor(sector, root, layout, options, now, joliet: false);
        await WriteSectorAsync(output, sector, tracker, cancellationToken).ConfigureAwait(false);

        Array.Clear(sector);
        WriteVolumeDescriptor(sector, root, layout, options, now, joliet: true);
        await WriteSectorAsync(output, sector, tracker, cancellationToken).ConfigureAwait(false);

        Array.Clear(sector);
        sector[0] = 255; // volume descriptor set terminator
        Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
        sector[6] = 1;
        await WriteSectorAsync(output, sector, tracker, cancellationToken).ConfigureAwait(false);

        await WritePathTableAsync(output, layout, joliet: false, bigEndian: false, tracker, cancellationToken).ConfigureAwait(false);
        await WritePathTableAsync(output, layout, joliet: false, bigEndian: true, tracker, cancellationToken).ConfigureAwait(false);
        await WritePathTableAsync(output, layout, joliet: true, bigEndian: false, tracker, cancellationToken).ConfigureAwait(false);
        await WritePathTableAsync(output, layout, joliet: true, bigEndian: true, tracker, cancellationToken).ConfigureAwait(false);

        foreach (var node in layout.Directories)
        {
            await WriteDirectoryExtentAsync(output, node, joliet: false, tracker, cancellationToken).ConfigureAwait(false);
        }

        foreach (var node in layout.Directories)
        {
            await WriteDirectoryExtentAsync(output, node, joliet: true, tracker, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in IsoTreeBuilder.EnumerateFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.SetCurrentItem(file.Name);
            await WriteFileDataAsync(output, file, tracker, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteVolumeDescriptor(
        Span<byte> sector,
        IsoNode root,
        IsoLayout layout,
        IsoBuildOptions options,
        DateTime now,
        bool joliet)
    {
        sector[0] = joliet ? (byte)2 : (byte)1;
        IsoWriter.WriteAscii(sector[1..6], "CD001");
        sector[6] = 1;
        sector[7] = 0;

        if (joliet)
        {
            IsoWriter.WriteUcs2(sector[8..40], "SteamDisc");
            IsoWriter.WriteUcs2(sector[40..72], options.VolumeLabel);
        }
        else
        {
            IsoWriter.WriteAscii(sector[8..40], "SteamDisc");
            IsoWriter.WriteAscii(sector[40..72], SanitiseLabel(options.VolumeLabel));
        }

        IsoWriter.WriteBothEndian32(sector[80..88], layout.TotalSectors);

        if (joliet)
        {
            // Escape sequence %/E — UCS-2 level 3.
            sector[88] = 0x25;
            sector[89] = 0x2F;
            sector[90] = 0x45;
        }

        IsoWriter.WriteBothEndian16(sector[120..124], 1); // volume set size
        IsoWriter.WriteBothEndian16(sector[124..128], 1); // volume sequence number
        IsoWriter.WriteBothEndian16(sector[128..132], IsoWriter.SectorSize);

        var pathTableSize = joliet ? layout.JolietPathTableSize : layout.IsoPathTableSize;
        IsoWriter.WriteBothEndian32(sector[132..140], (uint)pathTableSize);

        BinaryPrimitives.WriteUInt32LittleEndian(
            sector[140..144], joliet ? layout.JolietPathTableL : layout.IsoPathTableL);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[144..148], 0);
        BinaryPrimitives.WriteUInt32BigEndian(
            sector[148..152], joliet ? layout.JolietPathTableM : layout.IsoPathTableM);
        BinaryPrimitives.WriteUInt32BigEndian(sector[152..156], 0);

        WriteRootDirectoryRecord(sector[156..190], root, joliet);

        // Text fields are ASCII in the primary descriptor and UCS-2 in the Joliet one.
        WriteText(sector[190..318], string.Empty, joliet); // volume set identifier
        WriteText(sector[318..446], options.Publisher, joliet);
        WriteText(sector[446..574], options.Publisher, joliet); // data preparer
        WriteText(sector[574..702], options.ApplicationId, joliet);
        WriteText(sector[702..739], string.Empty, joliet); // copyright file
        WriteText(sector[739..776], string.Empty, joliet); // abstract file
        WriteText(sector[776..813], string.Empty, joliet); // bibliographic file

        IsoWriter.WriteLongDateTime(sector[813..830], now);
        IsoWriter.WriteLongDateTime(sector[830..847], now);
        IsoWriter.WriteUnsetLongDateTime(sector[847..864]); // expiration
        IsoWriter.WriteLongDateTime(sector[864..881], now); // effective

        sector[881] = 1; // file structure version
    }

    private static void WriteText(Span<byte> destination, string value, bool joliet)
    {
        if (joliet)
        {
            IsoWriter.WriteUcs2(destination, value);
        }
        else
        {
            IsoWriter.WriteAscii(destination, value);
        }
    }

    private static void WriteRootDirectoryRecord(Span<byte> destination, IsoNode root, bool joliet)
    {
        destination.Clear();
        destination[0] = 34;
        destination[1] = 0;
        IsoWriter.WriteBothEndian32(destination[2..10], joliet ? root.JolietExtent : root.IsoExtent);
        IsoWriter.WriteBothEndian32(
            destination[10..18], joliet ? root.JolietDirectoryLength : root.IsoDirectoryLength);
        IsoWriter.WriteShortDateTime(destination[18..25], root.LastWriteUtc);
        destination[25] = 0x02; // directory
        destination[26] = 0;
        destination[27] = 0;
        IsoWriter.WriteBothEndian16(destination[28..32], 1);
        destination[32] = 1;
        destination[33] = 0; // identifier for "."
    }

    private static async Task WritePathTableAsync(
        Stream output,
        IsoLayout layout,
        bool joliet,
        bool bigEndian,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var size = joliet ? layout.JolietPathTableSize : layout.IsoPathTableSize;
        var buffer = new byte[IsoWriter.RoundUpToSector(size)];
        var offset = 0;

        foreach (var node in layout.Directories)
        {
            byte[] identifier;
            if (node.Parent is null)
            {
                identifier = new byte[] { 0 };
            }
            else
            {
                identifier = joliet
                    ? Encoding.BigEndianUnicode.GetBytes(node.JolietIdentifier)
                    : Encoding.ASCII.GetBytes(node.IsoIdentifier);
            }

            buffer[offset] = (byte)identifier.Length;
            buffer[offset + 1] = 0;

            var extent = joliet ? node.JolietExtent : node.IsoExtent;
            var parentIndex = node.Parent?.PathTableIndex ?? 1;

            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 2), extent);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 6), parentIndex);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 2), extent);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 6), parentIndex);
            }

            identifier.CopyTo(buffer, offset + 8);
            offset += 8 + identifier.Length;
            if (offset % 2 != 0)
            {
                offset++;
            }
        }

        await WriteBufferAsync(output, buffer, tracker, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteDirectoryExtentAsync(
        Stream output,
        IsoNode node,
        bool joliet,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var length = joliet ? node.JolietDirectoryLength : node.IsoDirectoryLength;
        var buffer = new byte[IsoWriter.RoundUpToSector(length)];
        var offset = 0;

        WriteSpecialRecord(buffer.AsSpan(offset), node, joliet, self: true);
        offset += 34;

        WriteSpecialRecord(buffer.AsSpan(offset), node.Parent ?? node, joliet, self: false);
        offset += 34;

        foreach (var child in node.Children)
        {
            var recordLength = DirectoryRecordLength(child, joliet);
            var sectorRemaining = IsoWriter.SectorSize - (offset % IsoWriter.SectorSize);
            if (recordLength > sectorRemaining)
            {
                offset += sectorRemaining;
            }

            WriteChildRecord(buffer.AsSpan(offset, recordLength), child, joliet);
            offset += recordLength;
        }

        await WriteBufferAsync(output, buffer, tracker, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteSpecialRecord(Span<byte> destination, IsoNode node, bool joliet, bool self)
    {
        destination[..34].Clear();
        destination[0] = 34;
        destination[1] = 0;
        IsoWriter.WriteBothEndian32(destination[2..10], joliet ? node.JolietExtent : node.IsoExtent);
        IsoWriter.WriteBothEndian32(
            destination[10..18], joliet ? node.JolietDirectoryLength : node.IsoDirectoryLength);
        IsoWriter.WriteShortDateTime(destination[18..25], node.LastWriteUtc);
        destination[25] = 0x02;
        destination[26] = 0;
        destination[27] = 0;
        IsoWriter.WriteBothEndian16(destination[28..32], 1);
        destination[32] = 1;
        destination[33] = self ? (byte)0x00 : (byte)0x01;
    }

    private static void WriteChildRecord(Span<byte> destination, IsoNode node, bool joliet)
    {
        destination.Clear();

        var identifier = joliet
            ? Encoding.BigEndianUnicode.GetBytes(node.JolietIdentifier)
            : Encoding.ASCII.GetBytes(node.IsoIdentifier);

        destination[0] = (byte)destination.Length;
        destination[1] = 0;
        IsoWriter.WriteBothEndian32(destination[2..10], joliet ? node.JolietExtent : node.IsoExtent);
        IsoWriter.WriteBothEndian32(
            destination[10..18],
            node.IsDirectory
                ? (joliet ? node.JolietDirectoryLength : node.IsoDirectoryLength)
                : (uint)node.Length);
        IsoWriter.WriteShortDateTime(destination[18..25], node.LastWriteUtc);
        destination[25] = node.IsDirectory ? (byte)0x02 : (byte)0x00;
        destination[26] = 0;
        destination[27] = 0;
        IsoWriter.WriteBothEndian16(destination[28..32], 1);
        destination[32] = (byte)identifier.Length;
        identifier.CopyTo(destination[33..]);
    }

    private static async Task WriteFileDataAsync(
        Stream output,
        IsoNode file,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long written = 0;

        await using (var input = new FileStream(
                         file.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true))
        {
            while (written < file.Length)
            {
                var toRead = (int)Math.Min(buffer.Length, file.Length - written);
                var read = await input.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                tracker.AddBytes(read);
            }
        }

        if (written != file.Length)
        {
            throw new IOException(
                $"'{file.SourcePath}' changed size while the image was being written " +
                $"({written} of {file.Length} bytes read).");
        }

        // Pad the extent out to a sector boundary.
        var padding = (int)(IsoWriter.RoundUpToSector(file.Length) - file.Length);
        if (padding > 0)
        {
            var zeros = new byte[padding];
            await output.WriteAsync(zeros, cancellationToken).ConfigureAwait(false);
            tracker.AddBytes(padding);
        }
    }

    private static async Task WriteSectorAsync(
        Stream output,
        byte[] sector,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(sector, cancellationToken).ConfigureAwait(false);
        tracker.AddBytes(sector.Length);
    }

    private static async Task WriteBufferAsync(
        Stream output,
        byte[] buffer,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        tracker.AddBytes(buffer.Length);
    }

    private static string SanitiseLabel(string label)
    {
        var builder = new StringBuilder(label.Length);
        foreach (var c in label.ToUpperInvariant())
        {
            builder.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_');
        }

        return builder.Length == 0 ? "STEAMDISC" : builder.ToString();
    }

    private sealed record IsoLayout(
        IReadOnlyList<IsoNode> Directories,
        int IsoPathTableSize,
        int JolietPathTableSize,
        uint IsoPathTableL,
        uint IsoPathTableM,
        uint JolietPathTableL,
        uint JolietPathTableM,
        uint TotalSectors);
}

/// <param name="Path">Where the image was written.</param>
/// <param name="SizeBytes">Image size.</param>
/// <param name="FileCount">Files included.</param>
/// <param name="DirectoryCount">Directories included, including the root.</param>
public sealed record IsoBuildResult(string Path, long SizeBytes, int FileCount, int DirectoryCount);
