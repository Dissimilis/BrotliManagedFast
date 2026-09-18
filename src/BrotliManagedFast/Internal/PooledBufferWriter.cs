using System;
using System.Buffers;

namespace BrotliManagedFast;

/// <summary>Growable <see cref="IBufferWriter{T}"/> backed by pooled arrays.</summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = 4096, ArrayPool<byte>? pool = null)
    {
        _pool = pool ?? ArrayPool<byte>.Shared;
        _buffer = _pool.Rent(Math.Max(16, initialCapacity));
    }

    public int WrittenCount => _written;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint <= 0) sizeHint = 256;
        int free = _buffer.Length - _written;
        if (free >= sizeHint) return;
        long needed = (long)_written + sizeHint;
        long newSize = Math.Max(needed, (long)_buffer.Length * 2);
        if (newSize > int.MaxValue - 64) newSize = int.MaxValue - 64;
        byte[] nb = _pool.Rent((int)newSize);
        Buffer.BlockCopy(_buffer, 0, nb, 0, _written);
        _pool.Return(_buffer);
        _buffer = nb;
    }

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Clear() => _written = 0;

    public void Dispose()
    {
        if (_buffer.Length != 0)
        {
            _pool.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
        _written = 0;
    }
}
