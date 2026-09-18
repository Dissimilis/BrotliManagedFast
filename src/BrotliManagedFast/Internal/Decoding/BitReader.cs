using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrotliManagedFast.Internal;

/// <summary>Saved position of a <see cref="BitReader"/> so a multi-part read can be rolled back.</summary>
internal struct BitReaderState
{
    public ulong Acc;
    public int AccBits;
    public int Pos;
}

/// <summary>
/// LSB-first bit reader over a span. Holds up to 64 bits in an accumulator; bytes are pulled from the
/// input lazily. All reads are "try" reads that fail without side effects when input runs out.
/// </summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _input;
    private ulong _acc;
    private int _accBits;
    private int _pos;

    public BitReader(ReadOnlySpan<byte> input, ulong acc, int accBits, int pos)
    {
        _input = input;
        _acc = acc;
        _accBits = accBits;
        _pos = pos;
    }

    public ulong Acc => _acc;
    public int AccBits => _accBits;
    public int Pos => _pos;

    /// <summary>Reference to the first input byte, for hot loops that keep the reader state in locals.</summary>
    public ref byte InputRef => ref MemoryMarshal.GetReference(_input);

    /// <summary>Stores reader state produced by a hot loop that worked on locals.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetState(ulong acc, int accBits, int pos)
    {
        _acc = acc;
        _accBits = accBits;
        _pos = pos;
    }

    /// <summary>Bytes of input not yet pulled into the accumulator.</summary>
    public int RemainingBytes => _input.Length - _pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Save(out BitReaderState s)
    {
        s.Acc = _acc; s.AccBits = _accBits; s.Pos = _pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Restore(in BitReaderState s)
    {
        _acc = s.Acc; _accBits = s.AccBits; _pos = s.Pos;
    }

    /// <summary>Pulls as many whole bytes as fit into the accumulator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Fill()
    {
        if (_accBits > 56) return;
        int avail = _input.Length - _pos;
        if (avail >= 8)
        {
            // Load 8 bytes and keep only the bytes that fit.
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_input.Slice(_pos));
            int bytes = (64 - _accBits) >> 3;
            if (bytes == 8)
            {
                _acc = v;
                _accBits = 64;
                _pos += 8;
                return;
            }
            // Bits above _accBits are always zero, so a masked OR keeps that invariant.
            _acc |= (v & ((1UL << (bytes << 3)) - 1)) << _accBits;
            _accBits += bytes << 3;
            _pos += bytes;
            return;
        }
        while (avail > 0 && _accBits <= 56)
        {
            _acc |= (ulong)_input[_pos++] << _accBits;
            _accBits += 8;
            avail--;
        }
    }

    /// <summary>Number of bits available in the accumulator plus unread input.</summary>
    public long TotalAvailableBits => _accBits + ((long)(_input.Length - _pos) << 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeekBits(int n, out uint value)
    {
        if (_accBits < n) Fill();
        if (_accBits < n)
        {
            value = 0;
            return false;
        }
        value = (uint)(_acc & ((1UL << n) - 1));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DropBits(int n)
    {
        _acc >>= n;
        _accBits -= n;
    }

    /// <summary>Reads 0..32 bits. Returns false (no side effects) if not enough input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadBits(int n, out uint value)
    {
        if (n == 0)
        {
            value = 0;
            return true;
        }
        if (_accBits < n) Fill();
        if (_accBits < n)
        {
            value = 0;
            return false;
        }
        value = (uint)(_acc & ((1UL << n) - 1));
        _acc >>= n;
        _accBits -= n;
        return true;
    }

    /// <summary>Peeks up to 16 bits without failing; missing bits read as zero. Returns available bit count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekBitsLenient(out uint value)
    {
        if (_accBits < 16) Fill();
        value = (uint)(_acc & 0xFFFF);
        return _accBits;
    }

    /// <summary>
    /// Decodes one symbol using a two-level table with a <paramref name="rootBits"/>-bit root at <paramref name="table"/>[<paramref name="offset"/>...].
    /// Returns false (no side effects) if there is not enough input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadSymbol(ReadOnlySpan<uint> table, int offset, int rootBits, out int symbol)
    {
        uint rootMask = (1u << rootBits) - 1;
        int available = PeekBitsLenient(out uint bits);
        uint entry = table[offset + (int)(bits & rootMask)];
        int len = (int)(entry >> 16);
        if (len <= rootBits)
        {
            if (len > available)
            {
                symbol = 0;
                return false;
            }
            DropBits(len);
            symbol = (int)(entry & 0xFFFF);
            return true;
        }
        if (available <= rootBits)
        {
            symbol = 0;
            return false;
        }
        int nbits = len - rootBits;
        int idx = offset + (int)(bits & rootMask) + (int)(entry & 0xFFFF)
                  + (int)((bits >> rootBits) & ((1u << nbits) - 1));
        uint entry2 = table[idx];
        int len2 = (int)(entry2 >> 16);
        if (rootBits + len2 > available)
        {
            symbol = 0;
            return false;
        }
        DropBits(rootBits + len2);
        symbol = (int)(entry2 & 0xFFFF);
        return true;
    }


    // ------------------------------------------------------------------ unchecked fast path
    // Callers guarantee enough unread input (see DecoderCore fast-path guards); these skip every
    // availability test. All keep the invariant that bits above _accBits are zero.

    /// <summary>Tops the accumulator up to at least 56 bits. Requires at least 8 unread input bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FillFast()
    {
        if (_accBits > 56) return;
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_input), _pos));
        // The accumulator is little-endian by definition of the format; the test folds away at jit time.
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        int bytes = (63 - _accBits) >> 3;
        _acc |= (v & ((1UL << (bytes << 3)) - 1)) << _accBits;
        _accBits += bytes << 3;
        _pos += bytes;
    }

    /// <summary>Reads n bits (n up to 32), refilling first if needed; requires 8 unread input bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBitsFast(int n)
    {
        if (_accBits < n) FillFast();
        uint value = (uint)(_acc & ((1UL << n) - 1));
        _acc >>= n;
        _accBits -= n;
        return value;
    }

    /// <summary>Decodes one symbol, refilling first if fewer than 16 bits are buffered; requires 8 unread input bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadSymbolFast(ref uint table, int offset, int rootBits)
    {
        if (_accBits < 16) FillFast();
        uint bits = (uint)_acc;
        ref uint entry = ref Unsafe.Add(ref table, offset + (int)(bits & ((1u << rootBits) - 1)));
        uint e = entry;
        int len = (int)(e >> 16);
        if (len > rootBits)
        {
            int nbits = len - rootBits;
            e = Unsafe.Add(ref entry, (int)(e & 0xFFFF) + (int)((bits >> rootBits) & ((1u << nbits) - 1)));
            len = rootBits + (int)(e >> 16);
        }
        _acc >>= len;
        _accBits -= len;
        return (int)(e & 0xFFFF);
    }

    /// <summary>
    /// Drops padding bits up to the next byte boundary. Returns false if any dropped bit was non-zero.
    /// Requires that fewer than 8 bits be pending before a byte boundary (always true).
    /// </summary>
    public bool JumpToByteBoundary()
    {
        int pad = _accBits & 7;
        if (pad == 0) return true;
        uint padBits = (uint)(_acc & ((1UL << pad) - 1));
        DropBits(pad);
        return padBits == 0;
    }

    /// <summary>Returns whole unread bytes in the accumulator back to the input. Requires byte alignment.</summary>
    public void Unload()
    {
        int wholeBytes = _accBits >> 3;
        _pos -= wholeBytes;
        _accBits -= wholeBytes << 3;
        _acc &= (1UL << _accBits) - 1;
    }

    /// <summary>Copies up to <paramref name="count"/> bytes of raw input. Requires the accumulator to be byte aligned and empty.</summary>
    public int CopyBytes(Span<byte> dst, int count)
    {
        int n = Math.Min(count, Math.Min(dst.Length, _input.Length - _pos));
        _input.Slice(_pos, n).CopyTo(dst);
        _pos += n;
        return n;
    }

    /// <summary>Skips up to <paramref name="count"/> raw bytes. Requires the accumulator to be empty.</summary>
    public int SkipBytes(int count)
    {
        int n = Math.Min(count, _input.Length - _pos);
        _pos += n;
        return n;
    }

    public ReadOnlySpan<byte> PeekRaw(int count)
    {
        int n = Math.Min(count, _input.Length - _pos);
        return _input.Slice(_pos, n);
    }
}
