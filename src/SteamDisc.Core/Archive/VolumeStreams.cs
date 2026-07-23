using System.Globalization;

namespace SteamDisc.Core.Archive;

/// <summary>
/// Presents a set of fixed-size volume files as one continuous writable stream, rolling to
/// the next file when the current one reaches <see cref="VolumeSize"/>.
/// </summary>
/// <remarks>
/// A single logical record may straddle a volume boundary. That is intentional: forcing
/// records to align would waste space and, worse, make the split depend on the data, which
/// would stop us from targeting an exact disc capacity.
/// </remarks>
public sealed class VolumeWriterStream : Stream
{
    private readonly Func<int, Stream> _volumeFactory;
    private readonly List<VolumeInfo> _volumes = new();

    private Stream? _current;
    private long _currentLength;
    private long _totalWritten;
    private bool _disposed;

    /// <param name="volumeFactory">Creates the writable stream for a 1-based volume index.</param>
    /// <param name="volumeSize">Maximum bytes per volume. Use <see cref="long.MaxValue"/> for one volume.</param>
    public VolumeWriterStream(Func<int, Stream> volumeFactory, long volumeSize)
    {
        if (volumeSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volumeSize), volumeSize, "Volume size must be positive.");
        }

        _volumeFactory = volumeFactory;
        VolumeSize = volumeSize;
    }

    public long VolumeSize { get; }

    /// <summary>Volumes created so far, in order. Complete only after the stream is closed.</summary>
    public IReadOnlyList<VolumeInfo> Volumes => _volumes;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _totalWritten;

    public override long Position
    {
        get => _totalWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            EnsureCurrent();

            var room = (int)Math.Min(buffer.Length, VolumeSize - _currentLength);
            if (room <= 0)
            {
                RollVolume();
                continue;
            }

            _current!.Write(buffer[..room]);
            _currentLength += room;
            _totalWritten += room;
            buffer = buffer[room..];
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> single = stackalloc byte[1];
        single[0] = value;
        Write(single);
    }

    public override void Flush() => _current?.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnsureCurrent()
    {
        if (_current is null)
        {
            RollVolume();
        }
    }

    private void RollVolume()
    {
        CloseCurrent();
        var index = _volumes.Count + 1;
        _current = _volumeFactory(index);
        _currentLength = 0;
        _volumes.Add(new VolumeInfo(index, 0));
    }

    private void CloseCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _current.Flush();
        _current.Dispose();
        _current = null;
        _volumes[^1] = _volumes[^1] with { Size = _currentLength };
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            CloseCurrent();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    /// <param name="Index">1-based volume number.</param>
    /// <param name="Size">Bytes written to this volume.</param>
    public readonly record struct VolumeInfo(int Index, long Size);
}

/// <summary>
/// Presents a set of volume files as one continuous readable stream, pulling the next volume
/// from an <see cref="IVolumeSource"/> as it is needed.
/// </summary>
public sealed class VolumeReaderStream : Stream
{
    private readonly IVolumeSource _source;

    private Stream? _current;
    private int _currentIndex;
    private long _position;
    private bool _disposed;

    public VolumeReaderStream(IVolumeSource source) => _source = source;

    /// <summary>Raised just before a new volume is opened, for progress and disc-swap UI.</summary>
    public event Action<int>? VolumeOpening;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is null)
            {
                if (!await AdvanceAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }

            var read = await _current!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _position += read;
                return read;
            }

            // Current volume exhausted — continue into the next one.
            _current.Dispose();
            _current = null;

            if (_currentIndex >= _source.VolumeCount)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or throws. Volume boundaries mean a single
    /// read is routinely short even mid-file, so every structured read must go through this.
    /// </summary>
    public async Task ReadExactlyOrThrowAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Archive ended after {_position} bytes while {buffer.Length - total} more were expected. The disc set may be incomplete or a volume may be damaged."));
            }

            total += read;
        }
    }

    private async Task<bool> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (_currentIndex >= _source.VolumeCount)
        {
            return false;
        }

        _currentIndex++;
        VolumeOpening?.Invoke(_currentIndex);
        _current = await _source.OpenVolumeAsync(_currentIndex, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _current?.Dispose();
            _current = null;
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
