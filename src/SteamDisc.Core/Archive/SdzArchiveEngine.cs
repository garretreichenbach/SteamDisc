using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SteamDisc.Core.Progress;

namespace SteamDisc.Core.Archive;

/// <summary>
/// Reads and writes the SteamDisc container described by <see cref="SdzFormat"/>.
/// </summary>
/// <remarks>
/// No external tooling and no third-party libraries: the disc runtime has to unpack this on a
/// machine that has nothing installed, so the decoder is deliberately small enough to audit.
/// </remarks>
public sealed class SdzArchiveEngine : IArchiveEngine
{
    private static readonly UTF8Encoding PathEncoding = new(encoderShouldEmitUTF8Identifier: false);

    public string FormatId => ArchiveFormats.Sdz;

    public string VolumeExtension => "sdz";

    public bool IsAvailable => true;

    public async Task<ArchiveCreateResult> CreateAsync(
        ArchiveCreateRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tracker = new ProgressTracker(progress, OperationPhase.Scanning);
        var source = new DirectoryInfo(request.SourceDirectory);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"Nothing to pack: '{request.SourceDirectory}' does not exist.");
        }

        var plan = ScanSource(source, request.ExcludeRelativePaths);
        tracker.SetTotals(plan.TotalBytes, plan.Files.Count);
        tracker.SetPhase(OperationPhase.Compressing);

        Directory.CreateDirectory(request.OutputDirectory);
        var volumePaths = new List<string>();

        // Scoped so the last volume is closed — and its length settled — before it is measured.
        await using (var volumeWriter = new VolumeWriterStream(
                         index =>
                         {
                             var path = Path.Combine(
                                 request.OutputDirectory,
                                 SdzFormat.VolumeFileName(request.BaseName, VolumeExtension, index));
                             volumePaths.Add(path);
                             return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
                         },
                         request.VolumeSize))
        {
            var writer = new BinaryWriter(volumeWriter, PathEncoding, leaveOpen: true);
            WriteHeader(writer, plan);

            foreach (var directory in plan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(SdzFormat.RecordDirectory);
                writer.Write(directory);
            }

            var level = ToCompressionLevel(request.Compression);

            // Deflate is the expensive, per-chunk-independent part of packing, so it runs on the
            // thread pool while reads, SHA, and writes stay sequential. The window bounds how many
            // chunks may be in flight — and therefore memory — not just how many cores are busy.
            // Store does no CPU work, so it stays fully sequential and skips the pool hand-off.
            var chunkWindow = level == CompressionLevel.NoCompression
                ? 1
                : Math.Max(2, Environment.ProcessorCount + 1);

            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracker.SetCurrentItem(file.RelativePath);
                await WriteFileEntryAsync(writer, file, level, chunkWindow, tracker, cancellationToken)
                    .ConfigureAwait(false);
                tracker.CompleteItem();
            }

            writer.Write(SdzFormat.RecordEnd);
            writer.Write(SdzFormat.EndMagic);
            writer.Flush();
        }

        tracker.Finish();

        var compressed = volumePaths.Sum(p => new FileInfo(p).Length);
        return new ArchiveCreateResult(volumePaths, plan.TotalBytes, compressed, plan.Files.Count);
    }

    public async Task<ArchiveExtractResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tracker = new ProgressTracker(progress, OperationPhase.Extracting);
        var destination = Path.GetFullPath(request.DestinationDirectory);
        Directory.CreateDirectory(destination);

        await using var reader = new VolumeReaderStream(request.Volumes);
        var header = await ReadHeaderAsync(reader, cancellationToken).ConfigureAwait(false);
        tracker.SetTotals(header.UncompressedBytes, header.EntryCount);

        // Reads stay sequential — the volume stream is seek-free and may span physical discs —
        // but inflate is per-chunk-independent CPU work, so it runs on the pool. Raw chunks
        // carry no CPU cost and skip the hand-off, so an all-Store archive extracts sequentially.
        var chunkWindow = Math.Max(2, Environment.ProcessorCount + 1);

        var files = 0;
        long bytes = 0;

        for (var i = 0; i < header.EntryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recordType = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
            switch (recordType)
            {
                case SdzFormat.RecordDirectory:
                {
                    var relative = await ReadStringAsync(reader, cancellationToken).ConfigureAwait(false);
                    Directory.CreateDirectory(ResolveSafePath(destination, relative));
                    tracker.CompleteItem();
                    break;
                }

                case SdzFormat.RecordFile:
                {
                    var written = await ExtractFileAsync(
                        reader, destination, request, chunkWindow, tracker, cancellationToken).ConfigureAwait(false);
                    files++;
                    bytes += written;
                    tracker.CompleteItem();
                    break;
                }

                case SdzFormat.RecordEnd:
                    throw new ArchiveIntegrityException(
                        $"Archive ended after {i} of {header.EntryCount} entries. The disc set is incomplete.");

                default:
                    throw new ArchiveIntegrityException(
                        $"Unknown record type 0x{recordType:X2} at entry {i}. The archive is damaged.");
            }
        }

        var terminator = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
        if (terminator != SdzFormat.RecordEnd)
        {
            throw new ArchiveIntegrityException("Archive is missing its end record.");
        }

        var endMagic = new byte[4];
        await reader.ReadExactlyOrThrowAsync(endMagic, cancellationToken).ConfigureAwait(false);
        if (!endMagic.AsSpan().SequenceEqual(SdzFormat.EndMagic))
        {
            throw new ArchiveIntegrityException("Archive end marker is wrong. The last volume may be truncated.");
        }

        tracker.Finish();
        return new ArchiveExtractResult(files, bytes);
    }

    /// <summary>Lists the contents of an archive without extracting it.</summary>
    public async Task<IReadOnlyList<SdzEntryInfo>> ListAsync(
        IVolumeSource volumes,
        CancellationToken cancellationToken = default)
    {
        await using var reader = new VolumeReaderStream(volumes);
        var header = await ReadHeaderAsync(reader, cancellationToken).ConfigureAwait(false);
        var entries = new List<SdzEntryInfo>(header.EntryCount);

        for (var i = 0; i < header.EntryCount; i++)
        {
            var recordType = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
            if (recordType == SdzFormat.RecordDirectory)
            {
                entries.Add(new SdzEntryInfo(
                    await ReadStringAsync(reader, cancellationToken).ConfigureAwait(false),
                    IsDirectory: true,
                    0,
                    default,
                    null));
                continue;
            }

            if (recordType != SdzFormat.RecordFile)
            {
                throw new ArchiveIntegrityException($"Unknown record type 0x{recordType:X2} at entry {i}.");
            }

            var path = await ReadStringAsync(reader, cancellationToken).ConfigureAwait(false);
            var size = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);
            var lastWriteTicks = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);

            await SkipChunksAsync(reader, cancellationToken).ConfigureAwait(false);

            var actualSize = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);
            var hash = new byte[32];
            await reader.ReadExactlyOrThrowAsync(hash, cancellationToken).ConfigureAwait(false);

            entries.Add(new SdzEntryInfo(
                path,
                IsDirectory: false,
                actualSize == 0 ? size : actualSize,
                new DateTime(lastWriteTicks, DateTimeKind.Utc),
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        return entries;
    }

    private static SourcePlan ScanSource(DirectoryInfo source, IReadOnlyCollection<string>? excludes)
    {
        var excluded = new HashSet<string>(
            excludes ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var directories = new List<string>();
        var files = new List<SourceFile>();
        long total = 0;

        foreach (var directory in source.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            var relative = ToArchivePath(Path.GetRelativePath(source.FullName, directory.FullName));
            if (IsExcluded(relative, excluded))
            {
                continue;
            }

            directories.Add(relative);
        }

        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = ToArchivePath(Path.GetRelativePath(source.FullName, file.FullName));
            if (IsExcluded(relative, excluded))
            {
                continue;
            }

            files.Add(new SourceFile(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
            total += file.Length;
        }

        // Deterministic order keeps two builds of the same folder byte-identical, which makes
        // "did this disc change?" answerable by hashing.
        directories.Sort(StringComparer.Ordinal);
        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new SourcePlan(directories, files, total);
    }

    private static bool IsExcluded(string relativePath, HashSet<string> excluded)
    {
        if (excluded.Count == 0)
        {
            return false;
        }

        if (excluded.Contains(relativePath))
        {
            return true;
        }

        // Excluding a folder excludes everything beneath it.
        foreach (var candidate in excluded)
        {
            if (relativePath.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteHeader(BinaryWriter writer, SourcePlan plan)
    {
        writer.Write(SdzFormat.Magic);
        writer.Write(SdzFormat.FormatVersion);
        writer.Write((byte)0);
        writer.Write(plan.TotalBytes);
        writer.Write(plan.Directories.Count + plan.Files.Count);
    }

    private static async Task WriteFileEntryAsync(
        BinaryWriter writer,
        SourceFile file,
        CompressionLevel level,
        int chunkWindow,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        writer.Write(SdzFormat.RecordFile);
        writer.Write(file.RelativePath);
        writer.Write(file.Length);
        writer.Write(file.LastWriteUtc.Ticks);
        writer.Flush();

        using var sha = SHA256.Create();
        var pool = ArrayPool<byte>.Shared;

        // Chunks are read in order, but their deflate work runs concurrently. Completed chunks
        // wait in this FIFO so they are written back in exactly the order they were read — the
        // stream is seek-free, and byte-identical output is what makes "did this disc change?"
        // answerable by hashing. The window caps how far read can run ahead of write.
        var inFlight = new Queue<PendingChunk>(chunkWindow);
        long actual = 0;

        // Drains one completed chunk from the head of the queue and writes it in order.
        async Task WriteHeadChunkAsync()
        {
            var pending = inFlight.Dequeue();
            var result = await pending.Work.ConfigureAwait(false);

            writer.Write(result.StoredLength);
            writer.Write(result.OriginalLength);
            writer.Write(result.Compression);
            writer.Write(result.Stored.AsSpan(0, result.StoredLength));
            tracker.AddBytes(result.OriginalLength);

            pool.Return(pending.RawBuffer);
        }

        try
        {
            await using var input = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

            while (true)
            {
                var raw = pool.Rent(SdzFormat.ChunkSize);
                var read = await ReadChunkAsync(input, raw, SdzFormat.ChunkSize, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    pool.Return(raw);
                    break;
                }

                // SHA is a single running hash over the file, so it has to advance in read order.
                // It is cheap next to deflate, so keeping it on this thread costs little.
                sha.TransformBlock(raw, 0, read, null, 0);
                actual += read;

                var work = chunkWindow == 1
                    ? Task.FromResult(CompressChunk(raw, read, level))
                    : Task.Run(() => CompressChunk(raw, read, level), cancellationToken);
                inFlight.Enqueue(new PendingChunk(work, raw));

                if (inFlight.Count >= chunkWindow)
                {
                    await WriteHeadChunkAsync().ConfigureAwait(false);
                }
            }

            while (inFlight.Count > 0)
            {
                await WriteHeadChunkAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // On failure, hand every still-pending buffer back so a mid-build error does not
            // bleed the pool. Awaiting the faulted tasks here would just rethrow, so observe
            // them without propagating a second exception.
            while (inFlight.Count > 0)
            {
                var pending = inFlight.Dequeue();
                _ = pending.Work.ContinueWith(
                    static t => _ = t.Exception, TaskScheduler.Default);
                pool.Return(pending.RawBuffer);
            }

            throw;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        writer.Write(0); // chunk terminator
        writer.Write(actual);
        writer.Write(sha.Hash!);
        writer.Flush();
    }

    private static async Task<int> ReadChunkAsync(
        Stream input, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Compresses one chunk, off the calling thread. Pure and self-contained — it touches no
    /// shared state — so any number of these run in parallel safely.
    /// </summary>
    private static ChunkResult CompressChunk(byte[] raw, int length, CompressionLevel level)
    {
        if (level != CompressionLevel.NoCompression)
        {
            var compressed = Deflate(raw.AsSpan(0, length), level);
            // Only keep the compressed form when it actually helps. Game data is largely
            // pre-compressed, so a per-chunk decision beats a per-archive one.
            if (compressed.Length < length)
            {
                return new ChunkResult(compressed, compressed.Length, length, SdzFormat.ChunkDeflate);
            }
        }

        // Stored raw: the payload is the source buffer itself, so nothing extra is allocated.
        return new ChunkResult(raw, length, length, SdzFormat.ChunkRaw);
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream(data.Length);
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static async Task<SdzHeader> ReadHeaderAsync(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        await reader.ReadExactlyOrThrowAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(SdzFormat.Magic))
        {
            throw new ArchiveIntegrityException(
                "This is not a SteamDisc archive (bad magic). Check that the first volume is the .001 file.");
        }

        var version = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
        if (version != SdzFormat.FormatVersion)
        {
            throw new ArchiveIntegrityException(
                $"Archive format v{version} is newer than this installer understands (v{SdzFormat.FormatVersion}).");
        }

        await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false); // flags, reserved

        var uncompressed = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);
        var entryCount = await ReadInt32Async(reader, cancellationToken).ConfigureAwait(false);
        if (entryCount < 0)
        {
            throw new ArchiveIntegrityException($"Archive declares {entryCount} entries.");
        }

        return new SdzHeader(uncompressed, entryCount);
    }

    private static async Task<long> ExtractFileAsync(
        VolumeReaderStream reader,
        string destinationRoot,
        ArchiveExtractRequest request,
        int chunkWindow,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var relative = await ReadStringAsync(reader, cancellationToken).ConfigureAwait(false);
        var declaredSize = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);
        var lastWriteTicks = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);

        tracker.SetCurrentItem(relative);

        var target = ResolveSafePath(destinationRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target) && !request.Overwrite)
        {
            throw new IOException($"'{target}' already exists and overwriting was not permitted.");
        }

        // Write to a sidecar first so an interrupted install never leaves a half-written game
        // file that looks complete to Steam.
        var temporary = target + ".sdpart";
        using var sha = SHA256.Create();
        long written = 0;

        await using (var output = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
        {
            if (declaredSize > 0)
            {
                // Pre-sizing avoids fragmenting large game files across a slow write.
                try
                {
                    output.SetLength(declaredSize);
                    output.Position = 0;
                }
                catch (IOException)
                {
                    // Out of space, or a filesystem that will not preallocate; the write below
                    // will surface the real error with better context.
                }
            }

            var pool = ArrayPool<byte>.Shared;

            // Stored chunks are read in order, but their inflate work runs concurrently. Results
            // are drained from the head of this FIFO so bytes are written — and hashed — in the
            // original order, which the running SHA and the sequential output stream both require.
            var inFlight = new Queue<PendingInflate>(chunkWindow);

            // Drains one completed chunk from the head of the queue: writes it, hashes it, and
            // returns its buffers to the pool.
            async Task WriteHeadChunkAsync()
            {
                var pending = inFlight.Dequeue();
                var plain = await pending.Work.ConfigureAwait(false);

                await output.WriteAsync(plain.AsMemory(0, pending.OriginalLength), cancellationToken)
                    .ConfigureAwait(false);
                sha.TransformBlock(plain, 0, pending.OriginalLength, null, 0);

                written += pending.OriginalLength;
                tracker.AddBytes(pending.OriginalLength);

                pool.Return(pending.StoredBuffer);
                if (!pending.PlainIsStored)
                {
                    pool.Return(plain);
                }
            }

            try
            {
                while (true)
                {
                    var storedLength = await ReadInt32Async(reader, cancellationToken).ConfigureAwait(false);
                    if (storedLength == 0)
                    {
                        break;
                    }

                    if (storedLength < 0 || storedLength > SdzFormat.ChunkSize * 4)
                    {
                        throw new ArchiveIntegrityException(
                            $"Chunk length {storedLength} in '{relative}' is out of range; the volume is damaged.");
                    }

                    var originalLength = await ReadInt32Async(reader, cancellationToken).ConfigureAwait(false);
                    if (originalLength < 0 || originalLength > SdzFormat.ChunkSize)
                    {
                        throw new ArchiveIntegrityException(
                            $"Chunk size {originalLength} in '{relative}' is out of range; the volume is damaged.");
                    }

                    var compression = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);

                    var storedBuffer = pool.Rent(storedLength);
                    await reader
                        .ReadExactlyOrThrowAsync(storedBuffer.AsMemory(0, storedLength), cancellationToken)
                        .ConfigureAwait(false);

                    Task<byte[]> work;
                    bool plainIsStored;
                    if (compression == SdzFormat.ChunkDeflate)
                    {
                        plainIsStored = false;
                        var sLen = storedLength;
                        var oLen = originalLength;
                        var buffer = storedBuffer;
                        work = Task.Run(() => InflateChunk(buffer, sLen, oLen, relative), cancellationToken);
                    }
                    else if (compression == SdzFormat.ChunkRaw)
                    {
                        if (storedLength != originalLength)
                        {
                            pool.Return(storedBuffer);
                            throw new ArchiveIntegrityException(
                                $"Raw chunk in '{relative}' declares {storedLength} stored and {originalLength} original bytes.");
                        }

                        // The plaintext is the stored bytes verbatim; no CPU work, no extra buffer.
                        plainIsStored = true;
                        work = Task.FromResult(storedBuffer);
                    }
                    else
                    {
                        pool.Return(storedBuffer);
                        throw new ArchiveIntegrityException(
                            $"Unknown chunk compression 0x{compression:X2} in '{relative}'.");
                    }

                    inFlight.Enqueue(new PendingInflate(work, storedBuffer, originalLength, plainIsStored));

                    if (inFlight.Count >= chunkWindow)
                    {
                        await WriteHeadChunkAsync().ConfigureAwait(false);
                    }
                }

                while (inFlight.Count > 0)
                {
                    await WriteHeadChunkAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Return every still-pending buffer so a damaged volume mid-file does not bleed
                // the pool. Observe the faulted tasks without rethrowing a second exception.
                while (inFlight.Count > 0)
                {
                    var pending = inFlight.Dequeue();
                    _ = pending.Work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    pool.Return(pending.StoredBuffer);
                }

                throw;
            }

            // Trim any preallocated tail that the actual content did not fill.
            if (output.Length != written)
            {
                output.SetLength(written);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        var actualSize = await ReadInt64Async(reader, cancellationToken).ConfigureAwait(false);
        var expectedHash = new byte[32];
        await reader.ReadExactlyOrThrowAsync(expectedHash, cancellationToken).ConfigureAwait(false);

        if (actualSize != written)
        {
            File.Delete(temporary);
            throw new ArchiveIntegrityException(
                $"'{relative}' should be {actualSize} bytes but {written} were recovered.");
        }

        if (request.VerifyHashes && !sha.Hash!.AsSpan().SequenceEqual(expectedHash))
        {
            File.Delete(temporary);
            throw new ArchiveIntegrityException(
                $"'{relative}' failed its SHA-256 check. The disc may be scratched or the drive is misreading it.");
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(temporary, target);

        try
        {
            File.SetLastWriteTimeUtc(target, new DateTime(lastWriteTicks, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Timestamps are cosmetic; never fail an install over one.
        }

        return written;
    }

    /// <summary>
    /// Inflates one chunk, off the calling thread. Pure and self-contained apart from renting its
    /// output from the shared pool, so any number of these run in parallel safely. The caller
    /// returns the rented buffer once the chunk has been written.
    /// </summary>
    private static byte[] InflateChunk(byte[] stored, int storedLength, int originalLength, string relativePath)
    {
        var destination = ArrayPool<byte>.Shared.Rent(originalLength);
        try
        {
            using var input = new MemoryStream(stored, 0, storedLength, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            var total = 0;
            while (total < originalLength)
            {
                var read = deflate.Read(destination, total, originalLength - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != originalLength)
            {
                throw new ArchiveIntegrityException(
                    $"A compressed chunk of '{relativePath}' expanded to {total} bytes instead of {originalLength}.");
            }

            return destination;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(destination);
            throw;
        }
    }

    private static async Task SkipChunksAsync(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var scratch = Array.Empty<byte>();
        while (true)
        {
            var storedLength = await ReadInt32Async(reader, cancellationToken).ConfigureAwait(false);
            if (storedLength == 0)
            {
                return;
            }

            await ReadInt32Async(reader, cancellationToken).ConfigureAwait(false);
            await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);

            if (scratch.Length < storedLength)
            {
                scratch = new byte[storedLength];
            }

            await reader
                .ReadExactlyOrThrowAsync(scratch.AsMemory(0, storedLength), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<byte> ReadByteAsync(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await reader.ReadExactlyOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private static async Task<int> ReadInt32Async(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await reader.ReadExactlyOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<long> ReadInt64Async(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];
        await reader.ReadExactlyOrThrowAsync(buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    /// <summary>Reads a <see cref="BinaryWriter"/>-style 7-bit length-prefixed UTF-8 string.</summary>
    private static async Task<string> ReadStringAsync(VolumeReaderStream reader, CancellationToken cancellationToken)
    {
        var length = 0;
        var shift = 0;
        while (true)
        {
            if (shift > 4 * 7)
            {
                throw new ArchiveIntegrityException("Malformed string length in archive.");
            }

            var b = await ReadByteAsync(reader, cancellationToken).ConfigureAwait(false);
            length |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        if (length is < 0 or > 64 * 1024)
        {
            throw new ArchiveIntegrityException($"String length {length} in archive is out of range.");
        }

        var bytes = new byte[length];
        await reader.ReadExactlyOrThrowAsync(bytes, cancellationToken).ConfigureAwait(false);
        return PathEncoding.GetString(bytes);
    }

    internal static string ToArchivePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    /// <summary>
    /// Maps an archive-relative path onto the destination, refusing anything that would escape
    /// it. An installer that runs from a stranger's disc must not be talked into writing outside
    /// the target library.
    /// </summary>
    internal static string ResolveSafePath(string destinationRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArchiveIntegrityException("Archive contains an entry with an empty path.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArchiveIntegrityException($"Archive entry '{relativePath}' is an absolute path.");
        }

        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "..")
            {
                throw new ArchiveIntegrityException($"Archive entry '{relativePath}' tries to escape the target folder.");
            }
        }

        var combined = Path.GetFullPath(Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(destinationRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal) &&
            !string.Equals(combined, root, StringComparison.Ordinal))
        {
            throw new ArchiveIntegrityException($"Archive entry '{relativePath}' resolves outside the target folder.");
        }

        return combined;
    }

    private static CompressionLevel ToCompressionLevel(ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.Store => CompressionLevel.NoCompression,
        ArchiveCompression.Fast => CompressionLevel.Fastest,
        ArchiveCompression.Balanced => CompressionLevel.Optimal,
        ArchiveCompression.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Fastest,
    };

    private readonly record struct SdzHeader(long UncompressedBytes, int EntryCount);

    private sealed record SourcePlan(
        IReadOnlyList<string> Directories,
        IReadOnlyList<SourceFile> Files,
        long TotalBytes);

    private readonly record struct SourceFile(
        string FullPath,
        string RelativePath,
        long Length,
        DateTime LastWriteUtc);

    /// <summary>The framed result of compressing one chunk, ready to write.</summary>
    /// <param name="Stored">Bytes to write — either a fresh deflate buffer or the raw chunk.</param>
    /// <param name="StoredLength">Valid byte count within <paramref name="Stored"/>.</param>
    /// <param name="OriginalLength">Uncompressed length, written to the chunk header.</param>
    /// <param name="Compression">Chunk compression flag; see <see cref="SdzFormat"/>.</param>
    private readonly record struct ChunkResult(
        byte[] Stored,
        int StoredLength,
        int OriginalLength,
        byte Compression);

    /// <param name="Work">The in-flight compression, produced by <c>CompressChunk</c>.</param>
    /// <param name="RawBuffer">The pooled read buffer to return once the chunk is written.</param>
    private readonly record struct PendingChunk(Task<ChunkResult> Work, byte[] RawBuffer);

    /// <param name="Work">The in-flight inflate, yielding the plaintext buffer.</param>
    /// <param name="StoredBuffer">The pooled buffer holding the stored (compressed) chunk.</param>
    /// <param name="OriginalLength">Uncompressed byte count to write and hash.</param>
    /// <param name="PlainIsStored">True for raw chunks, where the plaintext is the stored buffer.</param>
    private readonly record struct PendingInflate(
        Task<byte[]> Work,
        byte[] StoredBuffer,
        int OriginalLength,
        bool PlainIsStored);
}

/// <param name="Path">Archive-relative path.</param>
/// <param name="IsDirectory">True for directory records.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
/// <param name="LastWriteUtc">Last write time recorded at authoring.</param>
/// <param name="Sha256">Lowercase hex content hash, or null for directories.</param>
public readonly record struct SdzEntryInfo(
    string Path,
    bool IsDirectory,
    long Size,
    DateTime LastWriteUtc,
    string? Sha256)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{(IsDirectory ? "D" : "F")} {Size,14:N0}  {Path}");
}
