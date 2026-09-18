using System;
using System.Buffers;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast;

/// <summary>Options controlling a <see cref="BrotliDecoder"/> or a decompressing <c>BrotliStream</c>.</summary>
public sealed class BrotliDecompressionOptions
{
    internal static readonly BrotliDecompressionOptions Default = new BrotliDecompressionOptions();

    private int _maxWindowLog = Constants.MaxWindowBits;
    private long _maxOutputLength = long.MaxValue;

    /// <summary>
    /// Largest sliding-window size (log2) the decoder accepts, 10..30. The default of 24 accepts every
    /// RFC 7932 stream. Values 25..30 additionally enable the Large Window extension; streams that
    /// declare a larger window than this fail with <see cref="BrotliDecoderError.WindowTooLarge"/>.
    /// This bounds the sliding window, not total memory: the ring buffer is padded, a pool may hand
    /// back a larger buffer, and the prefix-code tables are extra.
    /// </summary>
    public int MaxWindowLog
    {
        get => _maxWindowLog;
        set
        {
            if (value < Constants.MinWindowBits || value > Constants.LargeMaxWindowBits)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxWindowLog must be between 10 and 30.");
            _maxWindowLog = value;
        }
    }

    /// <summary>Maximum number of decompressed bytes to produce before failing with <see cref="BrotliDecoderError.OutputLimitExceeded"/>.</summary>
    public long MaxOutputLength
    {
        get => _maxOutputLength;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _maxOutputLength = value;
        }
    }

    /// <summary>
    /// When true, a completed stream followed by further input bytes is reported as
    /// <see cref="BrotliDecoderError.TrailingData"/> instead of <see cref="OperationStatus.Done"/>.
    /// </summary>
    public bool RejectTrailingData { get; set; }

    /// <summary>Optional prefix dictionary that must match the one used for compression.</summary>
    public BrotliDictionary? Dictionary { get; set; }

    /// <summary>Pool used for the sliding window buffer. Defaults to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public ArrayPool<byte>? Pool { get; set; }
}
