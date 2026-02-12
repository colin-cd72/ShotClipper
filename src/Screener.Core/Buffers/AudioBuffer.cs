using System.Buffers;

namespace Screener.Core.Buffers;

/// <summary>
/// Circular buffer for audio samples.
/// </summary>
public sealed class AudioRingBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private readonly object _lock = new();
    private int _writePos;
    private int _readPos;
    private int _available;
    private bool _disposed;

    public int Capacity => _capacity;
    public int Available
    {
        get { lock (_lock) return _available; }
    }

    public AudioRingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <summary>
    /// Write audio samples to the buffer.
    /// </summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) return 0;

        lock (_lock)
        {
            var toWrite = Math.Min(data.Length, _capacity - _available);
            if (toWrite == 0) return 0;

            var firstChunk = Math.Min(toWrite, _capacity - _writePos);
            data[..firstChunk].CopyTo(_buffer.AsSpan(_writePos, firstChunk));

            if (toWrite > firstChunk)
            {
                data[firstChunk..toWrite].CopyTo(_buffer.AsSpan(0, toWrite - firstChunk));
            }

            _writePos = (_writePos + toWrite) % _capacity;
            _available += toWrite;

            return toWrite;
        }
    }

    /// <summary>
    /// Read audio samples from the buffer.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (_disposed) return 0;

        lock (_lock)
        {
            var toRead = Math.Min(destination.Length, _available);
            if (toRead == 0) return 0;

            var firstChunk = Math.Min(toRead, _capacity - _readPos);
            _buffer.AsSpan(_readPos, firstChunk).CopyTo(destination[..firstChunk]);

            if (toRead > firstChunk)
            {
                _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            }

            _readPos = (_readPos + toRead) % _capacity;
            _available -= toRead;

            return toRead;
        }
    }

    /// <summary>
    /// Peek at samples without consuming them.
    /// </summary>
    public int Peek(Span<byte> destination)
    {
        if (_disposed) return 0;

        lock (_lock)
        {
            var toPeek = Math.Min(destination.Length, _available);
            if (toPeek == 0) return 0;

            var firstChunk = Math.Min(toPeek, _capacity - _readPos);
            _buffer.AsSpan(_readPos, firstChunk).CopyTo(destination[..firstChunk]);

            if (toPeek > firstChunk)
            {
                _buffer.AsSpan(0, toPeek - firstChunk).CopyTo(destination[firstChunk..]);
            }

            return toPeek;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _available = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}
