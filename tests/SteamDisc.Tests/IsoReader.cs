using System.Buffers.Binary;
using System.Text;

namespace SteamDisc.Tests;

/// <summary>
/// A read-only ISO 9660 parser used to verify what <c>Iso9660Builder</c> writes.
/// </summary>
/// <remarks>
/// Written independently of the writer on purpose. A test that reuses the writer's own layout
/// code proves only that it is self-consistent; this one starts from the descriptors on disc
/// and walks the structures the way a drive would, so a wrong extent or a mis-sized directory
/// shows up as a failure rather than as agreement.
/// </remarks>
internal sealed class IsoReader : IDisposable
{
    private const int SectorSize = 2048;

    private readonly FileStream _stream;

    public IsoReader(string path)
    {
        _stream = File.OpenRead(path);
        ReadVolumeDescriptors();
    }

    public string VolumeLabel { get; private set; } = string.Empty;

    /// <summary>Volume size in sectors, as declared by the primary descriptor.</summary>
    public uint DeclaredSectors { get; private set; }

    public bool HasJoliet { get; private set; }

    private uint _primaryRootExtent;
    private uint _primaryRootLength;
    private uint _jolietRootExtent;
    private uint _jolietRootLength;

    private void ReadVolumeDescriptors()
    {
        for (var sector = 16; sector < 32; sector++)
        {
            var data = ReadSector(sector);
            var type = data[0];
            var identifier = Encoding.ASCII.GetString(data, 1, 5);

            if (identifier != "CD001")
            {
                throw new InvalidDataException($"Sector {sector} is not a volume descriptor.");
            }

            if (type == 255)
            {
                break;
            }

            if (type == 1)
            {
                VolumeLabel = Encoding.ASCII.GetString(data, 40, 32).TrimEnd(' ');
                DeclaredSectors = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
                _primaryRootExtent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(156 + 2));
                _primaryRootLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(156 + 10));
            }
            else if (type == 2)
            {
                // Only UCS-2 escape sequences mark a Joliet descriptor.
                if (data[88] == 0x25 && data[89] == 0x2F && data[90] is 0x40 or 0x43 or 0x45)
                {
                    HasJoliet = true;
                    _jolietRootExtent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(156 + 2));
                    _jolietRootLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(156 + 10));
                }
            }
        }

        if (_primaryRootExtent == 0)
        {
            throw new InvalidDataException("No primary volume descriptor found.");
        }
    }

    /// <summary>Every file in the image, keyed by its Joliet path with forward slashes.</summary>
    public IReadOnlyDictionary<string, byte[]> ReadAllFiles(bool joliet = true)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var extent = joliet ? _jolietRootExtent : _primaryRootExtent;
        var length = joliet ? _jolietRootLength : _primaryRootLength;

        Walk(extent, length, string.Empty, joliet, files, out _);
        return files;
    }

    /// <summary>Every directory path in the image.</summary>
    public IReadOnlyList<string> ReadAllDirectories(bool joliet = true)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var directories = new List<string>();
        var extent = joliet ? _jolietRootExtent : _primaryRootExtent;
        var length = joliet ? _jolietRootLength : _primaryRootLength;

        Walk(extent, length, string.Empty, joliet, files, out _, directories);
        return directories;
    }

    private void Walk(
        uint extent,
        uint length,
        string prefix,
        bool joliet,
        Dictionary<string, byte[]> files,
        out int count,
        List<string>? directories = null)
    {
        count = 0;
        var buffer = ReadRange(extent, length);
        var offset = 0;

        while (offset < buffer.Length)
        {
            var recordLength = buffer[offset];

            if (recordLength == 0)
            {
                // Zero padding runs to the end of the sector; resume at the next one.
                var next = ((offset / SectorSize) + 1) * SectorSize;
                if (next >= buffer.Length)
                {
                    break;
                }

                offset = next;
                continue;
            }

            var childExtent = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 2));
            var childLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 10));
            var flags = buffer[offset + 25];
            var nameLength = buffer[offset + 32];
            var nameBytes = buffer.AsSpan(offset + 33, nameLength);

            // "." and ".." use single-byte identifiers 0x00 and 0x01.
            var isSpecial = nameLength == 1 && (nameBytes[0] == 0 || nameBytes[0] == 1);

            if (!isSpecial)
            {
                var name = joliet
                    ? Encoding.BigEndianUnicode.GetString(nameBytes)
                    : Encoding.ASCII.GetString(nameBytes);

                // Files carry a ";1" version suffix in the primary tree.
                var separator = name.IndexOf(';', StringComparison.Ordinal);
                if (separator > 0)
                {
                    name = name[..separator];
                }

                var path = prefix.Length == 0 ? name : prefix + "/" + name;

                if ((flags & 0x02) != 0)
                {
                    directories?.Add(path);
                    Walk(childExtent, childLength, path, joliet, files, out _, directories);
                }
                else
                {
                    files[path] = ReadRange(childExtent, childLength);
                    count++;
                }
            }

            offset += recordLength;
        }
    }

    private byte[] ReadSector(int sector) => ReadRange((uint)sector, SectorSize);

    private byte[] ReadRange(uint sector, uint length)
    {
        var buffer = new byte[length];
        _stream.Position = (long)sector * SectorSize;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose() => _stream.Dispose();
}
