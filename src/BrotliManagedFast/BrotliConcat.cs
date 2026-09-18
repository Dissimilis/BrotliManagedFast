using System;
using System.Buffers;
using System.Collections.Generic;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast;

/// <summary>
/// Joins fragments produced with <see cref="BrotliCompressionOptions.Concatenable"/> into one valid stream.
/// Every fragment must use the same window settings.
/// </summary>
public static class BrotliConcat
{
    /// <summary>Byte holding the final "last, empty" metablock that terminates a concatenated stream.</summary>
    public const byte Terminator = 0x03;

    /// <summary>Parses the window header of a stream and returns (windowBits, largeWindow, headerBits).</summary>
    internal static bool TryParseHeader(ReadOnlySpan<byte> stream, out int windowBits, out bool largeWindow, out int headerBits)
    {
        windowBits = 0;
        largeWindow = false;
        headerBits = 0;
        if (stream.Length == 0) return false;
        ulong acc = 0;
        int n = Math.Min(3, stream.Length);
        for (int i = 0; i < n; i++) acc |= (ulong)stream[i] << (8 * i);
        int availableBits = n * 8;
        int pos = 0;
        uint Read(int bits) { uint v = (uint)((acc >> pos) & ((1UL << bits) - 1)); pos += bits; return v; }
        if (Read(1) == 0) { windowBits = 16; headerBits = pos; return true; }
        uint b = Read(3);
        if (b != 0) { windowBits = 17 + (int)b; headerBits = pos; return true; }
        b = Read(3);
        if (b == 1)
        {
            if (pos + 7 > availableBits) return false;
            if (Read(1) != 0) return false;
            windowBits = (int)Read(6);
            largeWindow = true;
            headerBits = pos;
            return windowBits >= Constants.LargeMinWindowBits && windowBits <= Constants.LargeMaxWindowBits;
        }
        if (b != 0) { windowBits = 8 + (int)b; headerBits = pos; return true; }
        windowBits = 17;
        headerBits = pos;
        return true;
    }

    /// <summary>Number of leading bytes of a concatenable fragment that hold the header (window bits + alignment block).</summary>
    internal static int HeaderByteLength(ReadOnlySpan<byte> fragment, out int windowBits, out bool largeWindow)
    {
        if (!TryParseHeader(fragment, out windowBits, out largeWindow, out int headerBits))
            throw new ArgumentException("Fragment does not start with a valid Brotli window header.");
        // Header bits + empty metadata block (1 + 2 + 1 + 2 = 6 bits), padded to a byte boundary.
        return (headerBits + 6 + 7) >> 3;
    }

    /// <summary>Concatenates fragments into <paramref name="output"/> and finishes the stream.</summary>
    public static void Concatenate(IBufferWriter<byte> output, IReadOnlyList<ReadOnlyMemory<byte>> fragments)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (fragments is null) throw new ArgumentNullException(nameof(fragments));
        if (fragments.Count == 0)
        {
            output.GetSpan(1)[0] = 0x3B; // empty stream (window 22, last-empty)
            output.Advance(1);
            return;
        }
        Join(output, fragments);
        Span<byte> terminator = output.GetSpan(1);
        terminator[0] = Terminator;
        output.Advance(1);
    }

    /// <summary>
    /// Joins fragments into <paramref name="output"/> without finishing the stream, so the result is itself a
    /// concatenable fragment. Append <see cref="Terminator"/>, or pass it to <c>Concatenate</c> again,
    /// to complete it.
    /// </summary>
    public static void Join(IBufferWriter<byte> output, IReadOnlyList<ReadOnlyMemory<byte>> fragments)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (fragments is null) throw new ArgumentNullException(nameof(fragments));
        if (fragments.Count == 0) return;
        int firstWindow = -1;
        bool firstLarge = false;
        for (int i = 0; i < fragments.Count; i++)
        {
            ReadOnlySpan<byte> frag = fragments[i].Span;
            int header = HeaderByteLength(frag, out int wbits, out bool large);
            if (i == 0)
            {
                firstWindow = wbits;
                firstLarge = large;
            }
            else if (wbits != firstWindow || large != firstLarge)
            {
                throw new ArgumentException($"Fragment {i} uses window {wbits} but the first fragment uses {firstWindow}.");
            }
            ReadOnlySpan<byte> body = i == 0 ? frag : frag.Slice(Math.Min(header, frag.Length));
            Span<byte> dst = output.GetSpan(body.Length);
            body.CopyTo(dst);
            output.Advance(body.Length);
        }
    }

    /// <summary>Concatenates fragments into a new array.</summary>
    public static byte[] Concatenate(params ReadOnlyMemory<byte>[] fragments)
    {
        if (fragments is null) throw new ArgumentNullException(nameof(fragments));
        long total = 1;
        foreach (var f in fragments) total += f.Length;
        using var w = new PooledBufferWriter((int)Math.Min(total, int.MaxValue - 64));
        Concatenate(w, fragments);
        return w.ToArray();
    }

    /// <summary>Turns a single concatenable fragment into a complete stream by appending the terminator.</summary>
    public static byte[] Finish(ReadOnlySpan<byte> fragment)
    {
        var result = new byte[fragment.Length + 1];
        fragment.CopyTo(result);
        result[fragment.Length] = Terminator;
        return result;
    }
}
