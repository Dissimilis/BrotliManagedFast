using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace BrotliManagedFast;

/// <summary>
/// Stream wrapper around <see cref="BrotliEncoder"/> / <see cref="BrotliDecoder"/>, shaped like
/// <c>System.IO.Compression.BrotliStream</c>. Corrupt input throws <see cref="InvalidDataException"/>.
/// </summary>
public sealed class BrotliStream : Stream
{
    private const int DefaultBufferSize = 1 << 16;

    private Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly CompressionMode _mode;
    private BrotliEncoder _encoder;
    private BrotliDecoder _decoder;
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _bufferPos;
    private int _bufferLen;
    private bool _inputEnded;
    private bool _decoderDone;
    private readonly bool _rejectTrailingData;
    private bool _wroteFinal;
    private bool _disposed;
    private int _activeAsync;

    /// <summary>Creates a compressing (quality 4) or decompressing stream.</summary>
    public BrotliStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        : this(stream, mode, mode == CompressionMode.Compress ? new BrotliCompressionOptions() : null, null, leaveOpen)
    {
    }

    /// <summary>Creates a compressing stream with a <see cref="CompressionLevel"/> (Fastest = 1, Optimal = 4, SmallestSize = 11).</summary>
    public BrotliStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen = false)
        : this(stream, CompressionMode.Compress, new BrotliCompressionOptions { Quality = QualityFor(compressionLevel) }, null, leaveOpen)
    {
    }

    /// <summary>Creates a compressing stream with explicit options.</summary>
    public BrotliStream(Stream stream, BrotliCompressionOptions options, bool leaveOpen = false)
        : this(stream, CompressionMode.Compress, options ?? throw new ArgumentNullException(nameof(options)), null, leaveOpen)
    {
    }

    /// <summary>Creates a decompressing stream with explicit options.</summary>
    public BrotliStream(Stream stream, BrotliDecompressionOptions options, bool leaveOpen = false)
        : this(stream, CompressionMode.Decompress, null, options ?? throw new ArgumentNullException(nameof(options)), leaveOpen)
    {
    }

    private BrotliStream(Stream stream, CompressionMode mode, BrotliCompressionOptions? compressionOptions, BrotliDecompressionOptions? decompressionOptions, bool leaveOpen)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _mode = mode;
        _leaveOpen = leaveOpen;
        switch (mode)
        {
            case CompressionMode.Compress:
                if (!stream.CanWrite) throw new ArgumentException("Stream is not writable.", nameof(stream));
                _encoder = new BrotliEncoder(compressionOptions);
                _pool = compressionOptions?.Pool ?? ArrayPool<byte>.Shared;
                break;
            case CompressionMode.Decompress:
                if (!stream.CanRead) throw new ArgumentException("Stream is not readable.", nameof(stream));
                _decoder = new BrotliDecoder(decompressionOptions);
                _rejectTrailingData = decompressionOptions?.RejectTrailingData ?? false;
                _pool = decompressionOptions?.Pool ?? ArrayPool<byte>.Shared;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        _buffer = _pool.Rent(DefaultBufferSize);
    }

    private static int QualityFor(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.Optimal => BrotliCompressionOptions.DefaultQuality,
#if !NETSTANDARD2_0
        CompressionLevel.SmallestSize => 11,
#endif
        _ => (int)level >= 0 && (int)level <= BrotliCompressionOptions.MaxQuality ? (int)level : throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>The underlying stream.</summary>
    public Stream BaseStream => _stream ?? throw new ObjectDisposedException(nameof(BrotliStream));

    public override bool CanRead => _mode == CompressionMode.Decompress && _stream != null && _stream.CanRead;
    public override bool CanWrite => _mode == CompressionMode.Compress && _stream != null && _stream.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnsureNotDisposed()
    {
        if (_disposed || _stream == null) throw new ObjectDisposedException(nameof(BrotliStream));
    }

    private void EnsureDecompress()
    {
        EnsureNotDisposed();
        if (_mode != CompressionMode.Decompress) throw new InvalidOperationException("Stream is not in decompress mode.");
    }

    private void EnsureCompress()
    {
        EnsureNotDisposed();
        if (_mode != CompressionMode.Compress) throw new InvalidOperationException("Stream is not in compress mode.");
    }

    private void EnterAsync()
    {
        if (Interlocked.Exchange(ref _activeAsync, 1) != 0) throw new InvalidOperationException("Only one asynchronous operation at a time is allowed.");
    }

    private void ExitAsync() => Volatile.Write(ref _activeAsync, 0);

    // ------------------------------------------------------------------ read

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateArgs(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

#if NETSTANDARD2_0
    public int Read(Span<byte> destination)
#else
    public override int Read(Span<byte> destination)
#endif
    {
        EnsureDecompress();
        int total = 0;
        while (destination.Length > 0)
        {
            if (_decoderDone) break;
            OperationStatus status = _decoder.Decompress(_buffer.AsSpan(_bufferPos, _bufferLen - _bufferPos), destination, out int consumed, out int written, _inputEnded);
            _bufferPos += consumed;
            destination = destination.Slice(written);
            total += written;
            if (status == OperationStatus.Done)
            {
                _decoderDone = true;
                CheckForTrailingData();
                break;
            }
            if (status == OperationStatus.InvalidData) throw new InvalidDataException($"Invalid Brotli stream: {_decoder.LastError}.");
            if (status == OperationStatus.DestinationTooSmall)
            {
                if (destination.Length == 0) break;
                continue;
            }
            // NeedMoreData
            if (written > 0 && total > 0 && _bufferPos >= _bufferLen && !_inputEnded)
            {
                // Return what we have rather than blocking on the base stream.
                break;
            }
            if (!FillInputBuffer())
            {
                if (_inputEnded && total == 0)
                {
                    // Let the decoder report truncation with the final flag.
                    status = _decoder.Decompress(ReadOnlySpan<byte>.Empty, destination, out _, out int w2, isFinalBlock: true);
                    total += w2;
                    if (status == OperationStatus.InvalidData) throw new InvalidDataException($"Invalid Brotli stream: {_decoder.LastError}.");
                    if (status == OperationStatus.Done) _decoderDone = true;
                }
                break;
            }
        }
        return total;
    }

    /// <summary>
    /// The decoder only sees trailing bytes that happen to sit in the same buffer as the end of the stream.
    /// When the caller asked for strict detection, look past the end: whatever is left unread in the buffer,
    /// and failing that one more read of the base stream.
    /// </summary>
    private void CheckForTrailingData()
    {
        if (!_rejectTrailingData) return;
        if (_bufferPos < _bufferLen) throw new InvalidDataException($"Invalid Brotli stream: {BrotliDecoderError.TrailingData}.");
        if (_inputEnded) return;
        int extra = _stream!.Read(_buffer, 0, _buffer.Length);
        if (extra > 0) throw new InvalidDataException($"Invalid Brotli stream: {BrotliDecoderError.TrailingData}.");
        _inputEnded = true;
    }

    private bool FillInputBuffer()
    {
        if (_inputEnded) return false;
        if (_bufferPos < _bufferLen) return true;
        _bufferPos = 0;
        _bufferLen = 0;   // nothing in the buffer is valid until the read returns; a throwing read must not replay it
        int read = _stream!.Read(_buffer, 0, _buffer.Length);
        if (read <= 0)
        {
            _inputEnded = true;
            return false;
        }
        _bufferLen = read;
        return true;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateArgs(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

#if NETSTANDARD2_0
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
#else
    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
#endif
    {
        EnsureDecompress();
        cancellationToken.ThrowIfCancellationRequested();
        EnterAsync();
        try
        {
            int total = 0;
            while (destination.Length > 0 && !_decoderDone)
            {
                OperationStatus status = _decoder.Decompress(_buffer.AsSpan(_bufferPos, _bufferLen - _bufferPos), destination.Span, out int consumed, out int written, _inputEnded);
                _bufferPos += consumed;
                destination = destination.Slice(written);
                total += written;
                if (status == OperationStatus.Done)
                {
                    _decoderDone = true;
                    if (_rejectTrailingData)
                    {
                        if (_bufferPos < _bufferLen) throw new InvalidDataException($"Invalid Brotli stream: {BrotliDecoderError.TrailingData}.");
                        if (!_inputEnded)
                        {
#if NETSTANDARD2_0
                            int extra = await _stream!.ReadAsync(_buffer, 0, _buffer.Length, cancellationToken).ConfigureAwait(false);
#else
                            int extra = await _stream!.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken).ConfigureAwait(false);
#endif
                            if (extra > 0) throw new InvalidDataException($"Invalid Brotli stream: {BrotliDecoderError.TrailingData}.");
                            _inputEnded = true;
                        }
                    }
                    break;
                }
                if (status == OperationStatus.InvalidData) throw new InvalidDataException($"Invalid Brotli stream: {_decoder.LastError}.");
                if (status == OperationStatus.DestinationTooSmall) { if (destination.Length == 0) break; continue; }
                if (written > 0 && total > 0 && _bufferPos >= _bufferLen && !_inputEnded) break;
                if (_inputEnded)
                {
                    if (total == 0)
                    {
                        status = _decoder.Decompress(ReadOnlySpan<byte>.Empty, destination.Span, out _, out int w2, isFinalBlock: true);
                        total += w2;
                        if (status == OperationStatus.InvalidData) throw new InvalidDataException($"Invalid Brotli stream: {_decoder.LastError}.");
                        if (status == OperationStatus.Done) _decoderDone = true;
                    }
                    break;
                }
                if (_bufferPos >= _bufferLen)
                {
                    _bufferPos = 0;
                    // Clear the length before awaiting: if the read is cancelled or faults, the bytes already
                    // consumed must not stay described as valid, or the next call would feed them again.
                    _bufferLen = 0;
#if NETSTANDARD2_0
                    int read = await _stream!.ReadAsync(_buffer, 0, _buffer.Length, cancellationToken).ConfigureAwait(false);
#else
                    int read = await _stream!.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken).ConfigureAwait(false);
#endif
                    if (read <= 0)
                    {
                        _inputEnded = true;
                    }
                    else
                    {
                        _bufferLen = read;
                    }
                }
            }
            return total;
        }
        finally
        {
            ExitAsync();
        }
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    // ------------------------------------------------------------------ write

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateArgs(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

#if NETSTANDARD2_0
    public void Write(ReadOnlySpan<byte> source)
#else
    public override void Write(ReadOnlySpan<byte> source)
#endif
    {
        EnsureCompress();
        while (true)
        {
            OperationStatus status = _encoder.Compress(source, _buffer, out int consumed, out int written, isFinalBlock: false);
            source = source.Slice(consumed);
            if (written > 0) _stream!.Write(_buffer, 0, written);
            if (status == OperationStatus.NeedMoreData && source.IsEmpty) return;
            if (status == OperationStatus.DestinationTooSmall || !source.IsEmpty) continue;
            return;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateArgs(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

#if NETSTANDARD2_0
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
#else
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
#endif
    {
        EnsureCompress();
        cancellationToken.ThrowIfCancellationRequested();
        EnterAsync();
        try
        {
            while (true)
            {
                OperationStatus status = _encoder.Compress(source.Span, _buffer, out int consumed, out int written, isFinalBlock: false);
                source = source.Slice(consumed);
                if (written > 0)
                {
#if NETSTANDARD2_0
                    await _stream!.WriteAsync(_buffer, 0, written, cancellationToken).ConfigureAwait(false);
#else
                    await _stream!.WriteAsync(_buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
#endif
                }
                if (status == OperationStatus.NeedMoreData && source.IsEmpty) return;
                if (status == OperationStatus.DestinationTooSmall || !source.IsEmpty) continue;
                return;
            }
        }
        finally
        {
            ExitAsync();
        }
    }

    public override void WriteByte(byte value)
    {
        ReadOnlySpan<byte> one = stackalloc byte[] { value };
        Write(one);
    }

    /// <summary>Emits all buffered data so a reader can decode everything written so far, then flushes the base stream.</summary>
    public override void Flush()
    {
        EnsureNotDisposed();
        if (_mode != CompressionMode.Compress) return;
        if (_wroteFinal) return;
        OperationStatus status;
        do
        {
            status = _encoder.Flush(_buffer, out int written);
            if (written > 0) _stream!.Write(_buffer, 0, written);
        } while (status == OperationStatus.DestinationTooSmall);
        _stream!.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        if (_mode != CompressionMode.Compress || _wroteFinal) return;
        cancellationToken.ThrowIfCancellationRequested();
        EnterAsync();
        try
        {
            OperationStatus status;
            do
            {
                status = _encoder.Flush(_buffer, out int written);
                if (written > 0)
                {
#if NETSTANDARD2_0
                    await _stream!.WriteAsync(_buffer, 0, written, cancellationToken).ConfigureAwait(false);
#else
                    await _stream!.WriteAsync(_buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
#endif
                }
            } while (status == OperationStatus.DestinationTooSmall);
            await _stream!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitAsync();
        }
    }

    private void WriteFinal()
    {
        if (_wroteFinal) return;
        _wroteFinal = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _buffer, out _, out int written, isFinalBlock: true);
            if (written > 0) _stream!.Write(_buffer, 0, written);
        } while (status == OperationStatus.DestinationTooSmall);
    }

    private async ValueTask WriteFinalAsync()
    {
        if (_wroteFinal) return;
        _wroteFinal = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _buffer, out _, out int written, isFinalBlock: true);
            if (written > 0)
            {
#if NETSTANDARD2_0
                await _stream!.WriteAsync(_buffer, 0, written).ConfigureAwait(false);
#else
                await _stream!.WriteAsync(_buffer.AsMemory(0, written)).ConfigureAwait(false);
#endif
            }
        } while (status == OperationStatus.DestinationTooSmall);
    }

    // ------------------------------------------------------------------ dispose

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && !_disposed && _stream != null)
            {
                if (_mode == CompressionMode.Compress) WriteFinal();
            }
        }
        finally
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    if (disposing && !_leaveOpen) _stream?.Dispose();
                }
                finally
                {
                    _stream = null;
                    _encoder.Dispose();
                    _decoder.Dispose();
                    if (_buffer.Length != 0)
                    {
                        _pool.Return(_buffer);
                        _buffer = Array.Empty<byte>();
                    }
                }
            }
            base.Dispose(disposing);
        }
    }

#if NETSTANDARD2_0
    public async ValueTask DisposeAsync()
#else
    public override async ValueTask DisposeAsync()
#endif
    {
        if (_disposed || _stream == null)
        {
            return;
        }
        try
        {
            if (_mode == CompressionMode.Compress) await WriteFinalAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            try
            {
                if (!_leaveOpen)
                {
#if NETSTANDARD2_0
                    _stream.Dispose();
#else
                    await _stream.DisposeAsync().ConfigureAwait(false);
#endif
                }
            }
            finally
            {
                _stream = null;
                _encoder.Dispose();
                _decoder.Dispose();
                if (_buffer.Length != 0)
                {
                    _pool.Return(_buffer);
                    _buffer = Array.Empty<byte>();
                }
            }
        }
    }

    private static void ValidateArgs(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (buffer.Length - offset < count) throw new ArgumentException("Offset and count exceed the buffer length.");
    }
}
