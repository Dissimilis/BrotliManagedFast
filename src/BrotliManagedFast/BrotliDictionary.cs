using System;

namespace BrotliManagedFast;

/// <summary>
/// A prepared LZ77 prefix dictionary: raw bytes that both the encoder and decoder treat as data
/// preceding the stream. Immutable and safe to share between any number of encoder and decoder instances.
/// </summary>
public sealed class BrotliDictionary
{
    /// <summary>Largest supported raw dictionary size (1 GiB, matching the reference implementation).</summary>
    public const int MaxLength = 1 << 30;

    private readonly byte[] _data;

    private BrotliDictionary(byte[] data)
    {
        _data = data;
    }

    /// <summary>Creates a dictionary from a copy of <paramref name="data"/>.</summary>
    public static BrotliDictionary Create(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxLength) throw new ArgumentOutOfRangeException(nameof(data), "Dictionary is too large.");
        return new BrotliDictionary(data.ToArray());
    }

    /// <summary>Number of bytes in the dictionary.</summary>
    public int Length => _data.Length;

    /// <summary>The dictionary bytes.</summary>
    public ReadOnlySpan<byte> Span => _data;

    internal byte[] Bytes => _data;
}
