namespace SteamDisc.Core.Archive;

/// <summary>
/// On-disc layout of the SteamDisc archive container.
/// </summary>
/// <remarks>
/// <para>
/// The plan's whole argument for Path B is that we own the format end to end, so this is
/// documented rather than merely implemented. Everything is little-endian, and strings are
/// UTF-8 with a 7-bit-encoded length prefix (<see cref="System.IO.BinaryWriter.Write(string)"/>).
/// </para>
/// <para>
/// The stream is written straight through with no seeking, because it is split across volumes
/// that may end up on different physical discs. Two consequences shape the format:
/// </para>
/// <list type="bullet">
///   <item>
///     File data is framed as independent chunks, each with its own length, so nothing needs
///     to be known before it is written. A compressed length never has to be back-patched.
///   </item>
///   <item>
///     Each file's SHA-256 lands in a trailer after its data, not a header, since it is
///     computed while streaming.
///   </item>
/// </list>
/// <para>
/// Chunk-level framing also means a damaged region on one disc costs one chunk, and the
/// reader can say exactly which file and offset went bad instead of "archive corrupt".
/// </para>
/// <code>
/// Header
///   char[4]  magic          "SDZ1"
///   byte     formatVersion  1
///   byte     flags          reserved, 0
///   int64    uncompressedBytes   total across all entries (progress hint)
///   int32    entryCount
///
/// Entry, repeated entryCount times
///   byte     recordType     1 = file, 2 = directory
///   string   path           relative, '/' separated, never rooted and never containing ".."
///   -- directory ends here --
///   int64    originalSize   size hint from the scan
///   int64    lastWriteUtc   DateTime ticks, UTC
///   Chunk*                  zero or more, terminated by a zero storedLength
///   int64    actualSize     authoritative byte count
///   byte[32] sha256         of the uncompressed content
///
/// Chunk
///   int32    storedLength   0 terminates the chunk list
///   int32    originalLength
///   byte     compression    0 = raw, 1 = deflate
///   byte[storedLength]
///
/// Footer
///   byte     recordType     0xFF
///   char[4]  magic          "SDZE"
/// </code>
/// </remarks>
public static class SdzFormat
{
    public static ReadOnlySpan<byte> Magic => "SDZ1"u8;

    public static ReadOnlySpan<byte> EndMagic => "SDZE"u8;

    public const byte FormatVersion = 1;

    /// <summary>Uncompressed bytes per chunk. Also the granularity of progress and of damage.</summary>
    public const int ChunkSize = 1024 * 1024;

    public const byte RecordFile = 1;
    public const byte RecordDirectory = 2;
    public const byte RecordEnd = 0xFF;

    public const byte ChunkRaw = 0;
    public const byte ChunkDeflate = 1;

    /// <summary>Default volume size: 2 GiB minus a little, keeping volumes inside the ISO 9660 file size limit.</summary>
    public const long DefaultVolumeSize = (2L * 1024 * 1024 * 1024) - (16L * 1024 * 1024);

    /// <summary>Builds the conventional volume file name, e.g. <c>payload.sdz.001</c>.</summary>
    public static string VolumeFileName(string baseName, string extension, int index)
        => $"{baseName}.{extension}.{index:000}";
}
