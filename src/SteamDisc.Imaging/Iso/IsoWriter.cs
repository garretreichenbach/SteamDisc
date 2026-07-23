using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SteamDisc.Imaging.Iso;

/// <summary>Primitive writers shared by the volume descriptors, path tables and directory records.</summary>
internal static class IsoWriter
{
    public const int SectorSize = 2048;

    /// <summary>ISO 9660 stores most integers twice, little-endian then big-endian.</summary>
    public static void WriteBothEndian32(Span<byte> destination, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], value);
    }

    public static void WriteBothEndian16(Span<byte> destination, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], value);
    }

    /// <summary>Writes a space-padded ASCII field of a fixed width.</summary>
    public static void WriteAscii(Span<byte> destination, string value)
    {
        destination.Fill((byte)' ');
        var bytes = Encoding.ASCII.GetBytes(value);
        var length = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, length).CopyTo(destination);
    }

    /// <summary>Writes a space-padded UCS-2 big-endian field, as Joliet descriptors use.</summary>
    public static void WriteUcs2(Span<byte> destination, string value)
    {
        // Pad with UCS-2 spaces (0x00 0x20), not raw 0x20 bytes.
        for (var i = 0; i + 1 < destination.Length; i += 2)
        {
            destination[i] = 0x00;
            destination[i + 1] = 0x20;
        }

        var bytes = Encoding.BigEndianUnicode.GetBytes(value);
        var length = Math.Min(bytes.Length, destination.Length & ~1);
        bytes.AsSpan(0, length).CopyTo(destination);
    }

    /// <summary>The 17-byte "digits" timestamp used by volume descriptors.</summary>
    public static void WriteLongDateTime(Span<byte> destination, DateTime utc)
    {
        var text = utc.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture);
        Encoding.ASCII.GetBytes(text).CopyTo(destination);
        destination[16] = 0; // GMT offset in 15-minute intervals
    }

    /// <summary>The 17-byte all-zero timestamp meaning "not specified".</summary>
    public static void WriteUnsetLongDateTime(Span<byte> destination)
    {
        for (var i = 0; i < 16; i++)
        {
            destination[i] = (byte)'0';
        }

        destination[16] = 0;
    }

    /// <summary>The 7-byte binary timestamp used by directory records.</summary>
    public static void WriteShortDateTime(Span<byte> destination, DateTime utc)
    {
        var year = Math.Clamp(utc.Year, 1900, 2155);
        destination[0] = (byte)(year - 1900);
        destination[1] = (byte)utc.Month;
        destination[2] = (byte)utc.Day;
        destination[3] = (byte)utc.Hour;
        destination[4] = (byte)utc.Minute;
        destination[5] = (byte)utc.Second;
        destination[6] = 0; // GMT
    }

    public static uint SectorsFor(long bytes) => (uint)((bytes + SectorSize - 1) / SectorSize);

    public static long RoundUpToSector(long bytes) => SectorsFor(bytes) * (long)SectorSize;
}
