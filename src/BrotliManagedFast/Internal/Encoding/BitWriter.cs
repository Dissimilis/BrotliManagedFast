using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrotliManagedFast.Internal;

/// <summary>
/// LSB-first bit writer into a pooled, growable byte array. Bits are appended at <see cref="BitPosition"/>.
/// Invariant: the byte containing the write position is zero above the position. Every write stores eight
/// bytes (the merged current byte followed by the new bits and zeros), so the storage beyond the position
/// need not be clean and pooled memory is used without clearing.
/// </summary>
internal sealed class BitWriter : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _storage;
    private long _bitPos;

    public BitWriter(ArrayPool<byte> pool, int initialCapacity)
    {
        _pool = pool;
        _storage = pool.Rent(Math.Max(16, initialCapacity));
        _storage[0] = 0;
    }

    public void Dispose()
    {
        if (_storage.Length != 0)
        {
            _pool.Return(_storage);
            _storage = Array.Empty<byte>();
        }
    }

    public byte[] Storage => _storage;
    public long BitPosition => _bitPos;
    public int ByteLength => (int)((_bitPos + 7) >> 3);

    /// <summary>Resets to empty with <paramref name="pendingBits"/> (0..7) of carried partial byte <paramref name="pendingByte"/>.</summary>
    public void Reset(uint pendingByte, int pendingBits)
    {
        _storage[0] = (byte)pendingByte;
        _bitPos = pendingBits;
    }

    public void Reset()
    {
        _storage[0] = 0;
        _bitPos = 0;
    }

    /// <summary>Ensures room for at least <paramref name="bytes"/> more bytes beyond the current position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reserve(int bytes)
    {
        long needed = (_bitPos >> 3) + bytes + 16;
        if (needed > _storage.Length) Grow(needed);
    }

    private void Grow(long needed)
    {
        long size = Math.Max(needed, (long)_storage.Length * 2);
        if (size > int.MaxValue - 64) size = int.MaxValue - 64;
        byte[] nb = _pool.Rent((int)size);
        Buffer.BlockCopy(_storage, 0, nb, 0, ByteLength + 1 <= _storage.Length ? ByteLength + 1 : _storage.Length);
        _pool.Return(_storage);
        _storage = nb;
    }

    /// <summary>
    /// Writes <paramref name="count"/> literal codes from <paramref name="codes"/> (entries are (depth &lt;&lt; 16) | bits),
    /// keeping the write position in locals.
    /// </summary>
    public void WriteLiterals(byte[] src, int start, int count, uint[] codes)
    {
        Reserve(count * 2 + 16);
        long bitPos = _bitPos;
        ref byte storage = ref MemoryMarshal.GetReference(_storage.AsSpan());
        ref byte input = ref MemoryMarshal.GetReference(src.AsSpan());
        ref uint code = ref MemoryMarshal.GetReference(codes.AsSpan());
        for (int j = 0; j < count; j++)
        {
            uint c = Unsafe.Add(ref code, Unsafe.Add(ref input, start + j));
            int n = (int)(c >> 16);
            ref byte dst = ref Unsafe.Add(ref storage, (int)(bitPos >> 3));
            ulong v = ((ulong)(c & 0xFFFF) << (int)(bitPos & 7)) | dst;
            if (BitConverter.IsLittleEndian)
            {
                Unsafe.WriteUnaligned(ref dst, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(_storage.AsSpan((int)(bitPos >> 3), 8), v);
            }
            bitPos += n;
        }
        _bitPos = bitPos;
    }

    private static void WriteLittleEndian64(ref byte dst, ulong v)
    {
        for (int i = 0; i < 8; i++) Unsafe.Add(ref dst, i) = (byte)(v >> (8 * i));
    }

    /// <summary>Sets the write position after a run of unchecked writes.</summary>
    public void SetBitPosition(long bitPos) => _bitPos = bitPos;

    /// <summary>
    /// Unchecked append used by the metablock body: the caller has reserved enough room (<see cref="Reserve"/>) and
    /// <paramref name="value"/> has no bits set above <paramref name="nBits"/> (0..56). Returns the new position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Put(ref byte storage, long bitPos, int nBits, ulong value)
    {
        ref byte dst = ref Unsafe.Add(ref storage, (int)(bitPos >> 3));
        ulong v = (value << (int)(bitPos & 7)) | dst;
        Store64(ref dst, v);
        return bitPos + nBits;
    }

    /// <summary>
    /// Unchecked literal run: <paramref name="count"/> codes from <paramref name="codes"/> ((depth &lt;&lt; 16) | bits).
    /// Bits are gathered in a register and flushed four bytes at a time, so consecutive literals do not
    /// read back the byte the previous store just wrote.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PutLiterals(ref byte storage, long bitPos, ref byte input, int start, int count, ref uint codes)
    {
        int bytePos = (int)(bitPos >> 3);
        int bits = (int)(bitPos & 7);
        ulong acc = Unsafe.Add(ref storage, bytePos);   // the partial byte; zero above the position by invariant
        for (int j = 0; j < count; j++)
        {
            uint c = Unsafe.Add(ref codes, Unsafe.Add(ref input, start + j));
            acc |= (ulong)(c & 0xFFFF) << bits;
            bits += (int)(c >> 16);
            if (bits >= 32)
            {
                Store64(ref Unsafe.Add(ref storage, bytePos), acc);
                bytePos += 4;
                acc >>= 32;
                bits -= 32;
            }
        }
        Store64(ref Unsafe.Add(ref storage, bytePos), acc);
        return ((long)bytePos << 3) + bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store64(ref byte dst, ulong v)
    {
        if (BitConverter.IsLittleEndian)
        {
            Unsafe.WriteUnaligned(ref dst, v);
        }
        else
        {
            WriteLittleEndian64(ref dst, v);
        }
    }

    /// <summary>Writes the low <paramref name="nBits"/> bits of <paramref name="value"/> (nBits 0..56).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(int nBits, ulong value)
    {
        if (nBits == 0) return;
        long bytePos = _bitPos >> 3;
        if (bytePos + 9 > _storage.Length) Grow(bytePos + 9);
        int shift = (int)(_bitPos & 7);
        // Merge into 8 bytes with one store; the region beyond the current position is zero by invariant.
        ulong v = (value & ((1UL << nBits) - 1)) << shift;
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(_storage.AsSpan()), (int)bytePos);
        v |= dst;
        if (BitConverter.IsLittleEndian)
        {
            Unsafe.WriteUnaligned(ref dst, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_storage.AsSpan((int)bytePos, 8), v);
        }
        _bitPos += nBits;
    }

    /// <summary>Moves to the next byte boundary (padding with zero bits).</summary>
    public void JumpToByteBoundary()
    {
        _bitPos = (_bitPos + 7) & ~7L;
        if ((_bitPos >> 3) >= _storage.Length) Grow((_bitPos >> 3) + 16);
        _storage[_bitPos >> 3] = 0;
    }

    /// <summary>Appends raw bytes; requires byte alignment.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        long bytePos = _bitPos >> 3;
        if (bytePos + bytes.Length + 16 > _storage.Length) Grow(bytePos + bytes.Length + 16);
        bytes.CopyTo(_storage.AsSpan((int)bytePos));
        _bitPos += (long)bytes.Length << 3;
        _storage[_bitPos >> 3] = 0;
    }

    /// <summary>Rewinds to an earlier bit position (used for the uncompressed fallback).</summary>
    public void Truncate(long bitPos)
    {
        int bytePos = (int)(bitPos >> 3);
        int shift = (int)(bitPos & 7);
        _storage[bytePos] &= (byte)((1 << shift) - 1);
        int end = Math.Min(_storage.Length, ByteLength + 1);
        if (end > bytePos + 1) Array.Clear(_storage, bytePos + 1, end - bytePos - 1);
        _bitPos = bitPos;
    }
}
