using System;
using System.Buffers;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast;

/// <summary>
/// Incremental Brotli encoder. Mirrors the shape of <c>System.IO.Compression.BrotliEncoder</c>.
/// </summary>
public struct BrotliEncoder : IDisposable
{
    private EncoderCore? _core;
    private readonly BrotliCompressionOptions? _options;

    /// <summary>Creates an encoder with explicit quality (0..11) and window (10..24).</summary>
    public BrotliEncoder(int quality, int window)
        : this(new BrotliCompressionOptions { Quality = quality, WindowLog = window })
    {
    }

    /// <summary>Creates an encoder from options. A default-constructed value uses the default options.</summary>
    public BrotliEncoder(BrotliCompressionOptions? options)
    {
        _options = (options ?? BrotliCompressionOptions.Default).Clone();
        _options.Validate();
        _core = null;
    }

    // A default-constructed value has no options: give it the defaults rather than a null reference,
    // which is what the decoder does and what a struct's default value has to tolerate.
    private EncoderCore Core => _core ??= new EncoderCore(_options ?? BrotliCompressionOptions.Default);

    /// <summary>Releases pooled buffers.</summary>
    public void Dispose()
    {
        _core?.Dispose();
        _core = null;
    }

    /// <summary>Resets the encoder so it can produce another stream with the same options.</summary>
    public void Reset()
    {
        _core?.Dispose();
        _core = null;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>. With <paramref name="isFinalBlock"/> the
    /// stream is finished once all input is consumed. Returns <see cref="OperationStatus.Done"/> when the stream is
    /// complete and fully written, <see cref="OperationStatus.DestinationTooSmall"/> when output remains, or
    /// <see cref="OperationStatus.NeedMoreData"/> when all input was consumed and the stream is not final.
    /// </summary>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
        => Core.Compress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);

    /// <summary>
    /// Emits all buffered input so that a decoder can produce every byte given so far, byte aligned.
    /// Returns <see cref="OperationStatus.Done"/> when everything was written, else <see cref="OperationStatus.DestinationTooSmall"/>.
    /// </summary>
    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
        => Core.Flush(destination, out bytesWritten);

    /// <summary>Upper bound on the compressed size of <paramref name="inputSize"/> bytes for one-shot compression (reference formula).</summary>
    public static int GetMaxCompressedLength(int inputSize)
    {
        if (inputSize < 0) throw new ArgumentOutOfRangeException(nameof(inputSize));
        if (inputSize == 0) return 2;
        long numLargeBlocks = inputSize >> 14;
        long overhead = 2 + (4 * numLargeBlocks) + 3 + 1;
        long result = inputSize + overhead;
        if (result > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(inputSize));
        return (int)result;
    }

    /// <summary>One-shot compression with default options. Returns false, with <paramref name="bytesWritten"/> zero, if <paramref name="destination"/> is too small.</summary>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        => TryCompress(source, destination, out bytesWritten, null);

    /// <summary>One-shot compression with explicit quality and window.</summary>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, int quality, int window)
        => TryCompress(source, destination, out bytesWritten, new BrotliCompressionOptions { Quality = quality, WindowLog = window });

    /// <summary>One-shot compression with options.</summary>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, BrotliCompressionOptions? options)
    {
        options = (options ?? BrotliCompressionOptions.Default).Clone();
        if (options.SizeHint == 0) options.SizeHint = source.Length;
        using var enc = new BrotliEncoder(options);
        OperationStatus status = enc.Compress(source, destination, out int consumed, out bytesWritten, isFinalBlock: true);
        if (status != OperationStatus.Done || consumed != source.Length)
        {
            // A partial stream is not a usable result, so do not leave a count behind that a caller might trust.
            bytesWritten = 0;
            return false;
        }
        return true;
    }

    /// <summary>Compresses <paramref name="source"/> into a new array.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> source, BrotliCompressionOptions? options = null)
    {
        using var writer = new PooledBufferWriter(Math.Max(256, source.Length / 2));
        Compress(source, writer, options);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Compresses <paramref name="source"/> into <paramref name="output"/>.</summary>
    public static void Compress(ReadOnlySpan<byte> source, IBufferWriter<byte> output, BrotliCompressionOptions? options = null)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        options = (options ?? BrotliCompressionOptions.Default).Clone();
        if (options.SizeHint == 0) options.SizeHint = source.Length;
        using var enc = new BrotliEncoder(options);
        int offset = 0;
        for (;;)
        {
            Span<byte> dst = output.GetSpan(Math.Max(256, Math.Min(1 << 16, source.Length - offset + 16)));
            OperationStatus status = enc.Compress(source.Slice(offset), dst, out int consumed, out int written, isFinalBlock: true);
            output.Advance(written);
            offset += consumed;
            if (status == OperationStatus.Done) return;
            if (status == OperationStatus.DestinationTooSmall) continue;
            if (status == OperationStatus.NeedMoreData && offset == source.Length) continue;
            throw new InvalidOperationException($"Unexpected encoder status {status}.");
        }
    }
}
