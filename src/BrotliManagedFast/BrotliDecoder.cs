using System;
using System.Buffers;
using System.IO;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast;

/// <summary>
/// Incremental Brotli decoder. Mirrors the shape of <c>System.IO.Compression.BrotliDecoder</c>:
/// feed input in any chunking and drain output in any chunking; the decoder never throws for bad data.
/// </summary>
public struct BrotliDecoder : IDisposable
{
    private DecoderCore? _core;
    private readonly BrotliDecompressionOptions? _options;
    private bool _rejectTrailing;

    /// <summary>Creates a decoder with the given options (or defaults).</summary>
    public BrotliDecoder(BrotliDecompressionOptions? options)
    {
        _options = options;
        _core = null;
        _rejectTrailing = options?.RejectTrailingData ?? false;
    }

    private DecoderCore Core
    {
        get
        {
            if (_core == null)
            {
                _core = new DecoderCore(_options);
                _rejectTrailing = _options?.RejectTrailingData ?? false;
            }
            return _core;
        }
    }

    /// <summary>Detailed reason for the last <see cref="OperationStatus.InvalidData"/> result.</summary>
    public BrotliDecoderError LastError => _core?.LastError ?? BrotliDecoderError.None;
    internal string DebugState => _core?.DebugState ?? "";

    /// <summary>Total decompressed bytes produced since construction or the last <see cref="Reset"/>.</summary>
    public long TotalBytesWritten => _core?.TotalBytesWritten ?? 0;

    /// <summary>True once the end of the stream has been decoded and all output delivered.</summary>
    public bool IsFinished => _core?.IsFinished ?? false;

    /// <summary>Returns the decoder to its initial state so it can decode another stream.</summary>
    public void Reset() => _core?.Reset();

    /// <summary>Releases pooled buffers. The decoder must not be used afterwards.</summary>
    public void Dispose()
    {
        _core?.Dispose();
        _core = null;
    }

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>.
    /// Returns <see cref="OperationStatus.Done"/> when the stream is complete, <see cref="OperationStatus.NeedMoreData"/>
    /// when more input is required, <see cref="OperationStatus.DestinationTooSmall"/> when output space ran out,
    /// or <see cref="OperationStatus.InvalidData"/> (see <see cref="LastError"/>).
    /// </summary>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten)
        => Decompress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock: false);

    /// <summary>
    /// Same as <see cref="Decompress(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>, but when
    /// <paramref name="isFinalBlock"/> is true a result that would need more input is reported as
    /// <see cref="OperationStatus.InvalidData"/> with <see cref="BrotliDecoderError.TruncatedInput"/>.
    /// </summary>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        DecoderCore core = Core;
        DecodeResult r = core.Decompress(source, destination, out bytesConsumed, out bytesWritten);
        switch (r)
        {
            case DecodeResult.Success:
                if (_rejectTrailing && bytesConsumed < source.Length)
                {
                    core.MarkError(BrotliDecoderError.TrailingData);
                    return OperationStatus.InvalidData;
                }
                return OperationStatus.Done;
            case DecodeResult.NeedsMoreOutput:
                return OperationStatus.DestinationTooSmall;
            case DecodeResult.NeedsMoreInput:
                if (isFinalBlock)
                {
                    core.MarkError(BrotliDecoderError.TruncatedInput);
                    return OperationStatus.InvalidData;
                }
                return OperationStatus.NeedMoreData;
            default:
                return OperationStatus.InvalidData;
        }
    }

    /// <summary>One-shot decompression into a caller-provided buffer. Returns false if the buffer is too small or the data is invalid.</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        => TryDecompress(source, destination, out bytesWritten, null);

    /// <summary>One-shot decompression into a caller-provided buffer with options.</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, BrotliDecompressionOptions? options)
    {
        using var decoder = new BrotliDecoder(options);
        OperationStatus status = decoder.Decompress(source, destination, out _, out bytesWritten, isFinalBlock: true);
        return status == OperationStatus.Done;
    }

    /// <summary>Decompresses a complete stream into a new array. Throws <see cref="InvalidDataException"/> for corrupt input.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> source, BrotliDecompressionOptions? options = null)
    {
        using var writer = new PooledBufferWriter(Math.Max(64, Math.Min(source.Length * 4, 1 << 20)));
        Decompress(source, writer, options);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Decompresses a complete stream into <paramref name="output"/>. Throws <see cref="InvalidDataException"/> for corrupt input.</summary>
    public static void Decompress(ReadOnlySpan<byte> source, IBufferWriter<byte> output, BrotliDecompressionOptions? options = null)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        using var decoder = new BrotliDecoder(options);
        int offset = 0;
        for (;;)
        {
            Span<byte> dst = output.GetSpan(Math.Max(256, Math.Min(1 << 16, source.Length - offset + 1)));
            OperationStatus status = decoder.Decompress(source.Slice(offset), dst, out int consumed, out int written, isFinalBlock: true);
            output.Advance(written);
            offset += consumed;
            switch (status)
            {
                case OperationStatus.Done:
                    return;
                case OperationStatus.DestinationTooSmall:
                    continue;
                case OperationStatus.NeedMoreData:
                    throw new InvalidDataException("Truncated Brotli stream.");
                default:
                    throw new InvalidDataException($"Invalid Brotli stream: {decoder.LastError}.");
            }
        }
    }
}
