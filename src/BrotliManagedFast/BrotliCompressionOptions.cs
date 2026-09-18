using System;
using System.Buffers;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast;

/// <summary>Content hint that tunes the encoder's heuristics.</summary>
public enum BrotliEncoderMode
{
    /// <summary>No assumptions about the input.</summary>
    Generic = 0,
    /// <summary>UTF-8 text.</summary>
    Text = 1,
    /// <summary>WOFF 2.0 font data.</summary>
    Font = 2,
}

/// <summary>Options controlling a <see cref="BrotliEncoder"/> or a compressing <c>BrotliStream</c>.</summary>
public sealed class BrotliCompressionOptions
{
    /// <summary>Lowest quality (fastest).</summary>
    public const int MinQuality = 0;
    /// <summary>Highest quality (slowest, best ratio).</summary>
    public const int MaxQuality = 11;
    /// <summary>Default quality; the format's own default.</summary>
    public const int DefaultQuality = 4;
    /// <summary>Default window (log2).</summary>
    public const int DefaultWindowLog = 22;

    internal static readonly BrotliCompressionOptions Default = new BrotliCompressionOptions();

    private int _quality = DefaultQuality;
    private int _windowLog = DefaultWindowLog;
    private long _sizeHint;

    /// <summary>Compression quality 0..11.</summary>
    public int Quality
    {
        get => _quality;
        set
        {
            if (value < MinQuality || value > MaxQuality) throw new ArgumentOutOfRangeException(nameof(value), "Quality must be between 0 and 11.");
            _quality = value;
        }
    }

    /// <summary>
    /// Sliding window size (log2), 10..24, or up to 30 when <see cref="LargeWindow"/> is set.
    /// Larger windows improve ratio on large inputs and cost memory on both sides.
    /// </summary>
    public int WindowLog
    {
        get => _windowLog;
        set
        {
            if (value < Constants.MinWindowBits || value > Constants.LargeMaxWindowBits) throw new ArgumentOutOfRangeException(nameof(value), "WindowLog must be between 10 and 30.");
            _windowLog = value;
        }
    }

    /// <summary>
    /// Enables the Large Window extension (window 25..30). Output is not decodable by RFC 7932-only decoders,
    /// including <c>System.IO.Compression</c>. Required when <see cref="WindowLog"/> exceeds 24.
    /// </summary>
    public bool LargeWindow { get; set; }

    /// <summary>Content hint.</summary>
    public BrotliEncoderMode Mode { get; set; } = BrotliEncoderMode.Generic;

    /// <summary>Expected total input length, or 0 if unknown. Lets the encoder pick a smaller window for small inputs.</summary>
    public long SizeHint
    {
        get => _sizeHint;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _sizeHint = value;
        }
    }

    /// <summary>
    /// Produces a position-independent fragment (no end-of-stream marker) that <see cref="BrotliConcat"/> can join
    /// with other fragments made with the same window settings. Slightly reduces compression.
    /// </summary>
    public bool Concatenable { get; set; }

    /// <summary>Optional prefix dictionary; the decoder must use the same one.</summary>
    public BrotliDictionary? Dictionary { get; set; }

    /// <summary>Pool for internal buffers. Defaults to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public ArrayPool<byte>? Pool { get; set; }

    internal BrotliCompressionOptions Clone() => (BrotliCompressionOptions)MemberwiseClone();

    internal void Validate()
    {
        if (_windowLog > Constants.MaxWindowBits && !LargeWindow) throw new ArgumentOutOfRangeException(nameof(WindowLog), "WindowLog above 24 requires LargeWindow = true.");
        if (Concatenable && Dictionary is not null) throw new ArgumentException("Concatenable output cannot be combined with a prefix dictionary: dictionary distances depend on the position in the joined stream.", nameof(Dictionary));
    }
}
