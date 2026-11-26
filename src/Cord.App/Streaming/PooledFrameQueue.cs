using System.Buffers;
using System.Diagnostics;

namespace Cord.App.Streaming;

internal readonly record struct PooledVideoFrame(byte[] Buffer, int Length, byte[]? Header, long EnqueuedTimestamp)
{
    public void Return()
    {
        if (Buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// Bounded drop-oldest queue backed by <see cref="ArrayPool{T}.Shared"/> so hot-path video
/// delivery never allocates per frame and dropped frames always return their pooled buffers.
/// </summary>
internal sealed class PooledFrameQueue : IDisposable
{
    private readonly Queue<PooledVideoFrame> _queue;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;
    private bool _disposed;

    public PooledFrameQueue(int capacity = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _queue = new Queue<PooledVideoFrame>(capacity);
    }

    public void EnqueueCopy(ReadOnlySpan<byte> data, byte[]? header)
    {
        var rented = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(rented);
        var item = new PooledVideoFrame(rented, data.Length, header, Stopwatch.GetTimestamp());

        var signaled = false;
        PooledVideoFrame dropped = default;
        lock (_lock)
        {
            if (_completed || _disposed)
            {
                item.Return();
                return;
            }

            if (_queue.Count == _capacity)
            {
                dropped = _queue.Dequeue();
            }
            else
            {
                signaled = true;
            }

            _queue.Enqueue(item);
        }

        if (dropped.Buffer is not null)
            dropped.Return();

        if (signaled)
            _signal.Release();
    }

    public bool TryDequeue(out PooledVideoFrame frame)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                frame = _queue.Dequeue();
                return true;
            }
        }

        frame = default;
        return false;
    }

    public bool TryDequeueWait(out PooledVideoFrame frame, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryDequeue(out frame))
                return true;

            lock (_lock)
            {
                if (_completed || _disposed)
                {
                    frame = default;
                    return false;
                }
            }

            _signal.Wait(cancellationToken);
        }
    }

    public void Complete()
    {
        List<PooledVideoFrame>? remaining = null;
        var shouldRelease = false;
        lock (_lock)
        {
            if (_completed)
                return;
            _completed = true;
            shouldRelease = true;
            if (_queue.Count > 0)
            {
                remaining = [.. _queue];
                _queue.Clear();
            }
        }

        if (remaining is not null)
        {
            foreach (var item in remaining)
                item.Return();
        }

        if (shouldRelease)
            _signal.Release();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Complete();
        _disposed = true;
        _signal.Dispose();
    }
}
