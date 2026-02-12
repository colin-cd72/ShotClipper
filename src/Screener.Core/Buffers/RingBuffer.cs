using System.Buffers;
using System.Collections.Concurrent;

namespace Screener.Core.Buffers;

/// <summary>
/// Thread-safe ring buffer for video frames with zero-copy semantics.
/// </summary>
public sealed class FrameRingBuffer : IDisposable
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<PooledFrame> _frames = new();
    private readonly SemaphoreSlim _frameAvailable = new(0);
    private readonly VideoFramePool _framePool;
    private int _count;
    private bool _disposed;

    public int Count => _count;
    public int Capacity => _capacity;

    public FrameRingBuffer(int capacity, int maxFrameSize)
    {
        _capacity = capacity;
        _framePool = new VideoFramePool(capacity * 2, maxFrameSize);
    }

    /// <summary>
    /// Rent a frame buffer from the pool.
    /// </summary>
    public PooledFrame RentFrame(int size) => _framePool.Rent(size);

    /// <summary>
    /// Publish a frame to all consumers.
    /// </summary>
    public void Publish(PooledFrame frame)
    {
        if (_disposed) return;

        _frames.Enqueue(frame);
        Interlocked.Increment(ref _count);

        // Trim old frames if over capacity
        while (_count > _capacity && _frames.TryDequeue(out var oldFrame))
        {
            Interlocked.Decrement(ref _count);
            oldFrame.Dispose();
        }

        _frameAvailable.Release();
    }

    /// <summary>
    /// Wait for and dequeue the next frame.
    /// </summary>
    public async Task<PooledFrame?> DequeueAsync(CancellationToken ct = default)
    {
        try
        {
            await _frameAvailable.WaitAsync(ct);

            if (_frames.TryDequeue(out var frame))
            {
                Interlocked.Decrement(ref _count);
                return frame;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>
    /// Try to dequeue without waiting.
    /// </summary>
    public bool TryDequeue(out PooledFrame? frame)
    {
        if (_frames.TryDequeue(out frame))
        {
            Interlocked.Decrement(ref _count);
            _frameAvailable.Wait(0); // Decrement semaphore
            return true;
        }
        frame = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_frames.TryDequeue(out var frame))
        {
            frame.Dispose();
        }

        _frameAvailable.Dispose();
        _framePool.Dispose();
    }
}

/// <summary>
/// Pool of reusable video frame buffers to minimize GC pressure.
/// </summary>
public sealed class VideoFramePool : IDisposable
{
    private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private readonly ConcurrentBag<PooledFrame> _available = new();
    private readonly int _maxFrameSize;
    private readonly int _maxPooled;
    private int _created;
    private bool _disposed;

    public VideoFramePool(int maxPooled, int maxFrameSize)
    {
        _maxPooled = maxPooled;
        _maxFrameSize = maxFrameSize;
    }

    public PooledFrame Rent(int size)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoFramePool));

        // Try to get from pool
        if (_available.TryTake(out var frame) && frame.Capacity >= size)
        {
            frame.Reset(size);
            return frame;
        }

        // Create new
        Interlocked.Increment(ref _created);
        var buffer = _arrayPool.Rent(Math.Max(size, _maxFrameSize));
        return new PooledFrame(buffer, size, this);
    }

    internal void Return(PooledFrame frame)
    {
        if (_disposed || _available.Count >= _maxPooled)
        {
            _arrayPool.Return(frame.Buffer);
            return;
        }

        _available.Add(frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_available.TryTake(out var frame))
        {
            _arrayPool.Return(frame.Buffer);
        }
    }
}

/// <summary>
/// A pooled frame buffer that returns to the pool when disposed.
/// </summary>
public sealed class PooledFrame : IDisposable
{
    private readonly VideoFramePool _pool;
    private int _length;
    private bool _disposed;

    public byte[] Buffer { get; }
    public int Capacity => Buffer.Length;
    public int Length => _length;
    public Memory<byte> Memory => Buffer.AsMemory(0, _length);
    public Span<byte> Span => Buffer.AsSpan(0, _length);
    public ReadOnlyMemory<byte> ReadOnlyMemory => Memory;

    // Frame metadata
    public long FrameNumber { get; set; }
    public TimeSpan Timestamp { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int RowBytes { get; set; }

    internal PooledFrame(byte[] buffer, int length, VideoFramePool pool)
    {
        Buffer = buffer;
        _length = length;
        _pool = pool;
    }

    internal void Reset(int length)
    {
        _length = length;
        _disposed = false;
        FrameNumber = 0;
        Timestamp = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Return(this);
    }
}
