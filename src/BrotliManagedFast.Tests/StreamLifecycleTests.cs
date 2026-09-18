using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BrotliManagedFast.Tests;

public class StreamLifecycleTests
{
    // ------------------------------------------------------------------ helper streams

    /// <summary>Wraps a MemoryStream but returns at most N bytes per Read/ReadAsync call.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _maxChunk;
        public int ReadCalls;
        public int WriteCalls;

        public ChunkedStream(byte[] data, int maxChunk)
        {
            _inner = new MemoryStream(data);
            _maxChunk = maxChunk;
        }

        public ChunkedStream(int maxChunk)
        {
            _inner = new MemoryStream();
            _maxChunk = maxChunk;
        }

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return _inner.Read(buffer, offset, Math.Min(count, _maxChunk));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return _inner.ReadAsync(buffer, offset, Math.Min(count, _maxChunk), cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            int off = offset;
            int remaining = count;
            while (remaining > 0)
            {
                int n = Math.Min(remaining, _maxChunk);
                _inner.Write(buffer, off, n);
                off += n;
                remaining -= n;
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteCalls++;
            int off = offset;
            int remaining = count;
            while (remaining > 0)
            {
                int n = Math.Min(remaining, _maxChunk);
                await _inner.WriteAsync(buffer, off, n, cancellationToken).ConfigureAwait(false);
                off += n;
                remaining -= n;
            }
        }
    }

    /// <summary>A stream whose synchronous Read/Write/Flush throw; only the async members work (and complete asynchronously via a Task.Yield).</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public int AsyncReadCalls;
        public int AsyncWriteCalls;

        public byte[] ToArray() => _inner.ToArray();
        public void SetReadSource(byte[] data)
        {
            _inner.Position = 0;
            _inner.SetLength(0);
            _inner.Write(data, 0, data.Length);
            _inner.Position = 0;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync Read must not be called.");
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync Write must not be called.");
        public override void Flush() => throw new InvalidOperationException("Sync Flush must not be called.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncReadCalls++;
            await Task.Yield();
            return _inner.Read(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncWriteCalls++;
            await Task.Yield();
            _inner.Write(buffer, offset, count);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
        }
    }

    /// <summary>A stream whose first WriteAsync call blocks on a gate the test controls, guaranteeing a deterministic overlap window.</summary>
    private sealed class GatedOnceStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;

        public Task FirstCallEntered => _entered.Task;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseGate() => _gate.TrySetResult();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                _entered.TrySetResult();
                await _gate.Task.ConfigureAwait(false);
            }
            _inner.Write(buffer, offset, count);
        }
    }

    private enum ThrowPoint { Write, Flush, DisposeSync, DisposeAsync }

    /// <summary>A MemoryStream-backed stream that throws a sentinel IOException at a chosen point.</summary>
    private sealed class ThrowingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly ThrowPoint _throwAt;
        public readonly IOException Sentinel = new IOException("sentinel-failure");
        public int DisposeCalls;
        public int DisposeAsyncCalls;

        public ThrowingStream(ThrowPoint throwAt) { _throwAt = throwAt; }
        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_throwAt == ThrowPoint.Write) throw Sentinel;
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_throwAt == ThrowPoint.Write) throw Sentinel;
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override void Flush()
        {
            if (_throwAt == ThrowPoint.Flush) throw Sentinel;
            _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_throwAt == ThrowPoint.Flush) throw Sentinel;
            return _inner.FlushAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            if (_throwAt == ThrowPoint.DisposeSync) throw Sentinel;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeAsyncCalls++;
            if (_throwAt == ThrowPoint.DisposeAsync) throw Sentinel;
            return base.DisposeAsync();
        }
    }

    private const int TimeoutMs = 20_000;

    // ------------------------------------------------------------------ chunked base stream, sync and async

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void RoundtripThroughTinyChunkedBaseStream_Sync(int chunk)
    {
        byte[] data = Corpus.Text(300_000, 5);
        var writeBase = new ChunkedStream(chunk);
        using (var bs = new BrotliStream(writeBase, CompressionMode.Compress, leaveOpen: true))
        {
            bs.Write(data, 0, data.Length);
        }
        byte[] compressed = writeBase.ToArray();

        var readBase = new ChunkedStream(compressed, chunk);
        using var decStream = new BrotliStream(readBase, CompressionMode.Decompress, leaveOpen: true);
        using var outMs = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = decStream.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task RoundtripThroughTinyChunkedBaseStream_Async(int chunk)
    {
        byte[] data = Corpus.Ints(200_000, 9);
        var writeBase = new ChunkedStream(chunk);
        await using (var bs = new BrotliStream(writeBase, CompressionMode.Compress, leaveOpen: true))
        {
            await bs.WriteAsync(data.AsMemory()).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }
        byte[] compressed = writeBase.ToArray();

        var readBase = new ChunkedStream(compressed, chunk);
        await using var decStream = new BrotliStream(readBase, CompressionMode.Decompress, leaveOpen: true);
        using var outMs = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await decStream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs))) > 0) outMs.Write(buf, 0, n);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    // ------------------------------------------------------------------ async must never fall back to sync

    [Fact]
    public async Task AsyncPathNeverCallsSynchronousMembersOfBaseStream()
    {
        byte[] data = Corpus.Text(50_000, 11);
        var writeBase = new AsyncOnlyStream();
        await using (var bs = new BrotliStream(writeBase, CompressionMode.Compress, leaveOpen: true))
        {
            await bs.WriteAsync(data.AsMemory()).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await bs.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }
        Assert.True(writeBase.AsyncWriteCalls > 0);
        byte[] compressed = writeBase.ToArray();

        var readBase = new AsyncOnlyStream();
        readBase.SetReadSource(compressed);
        await using var decStream = new BrotliStream(readBase, CompressionMode.Decompress, leaveOpen: true);
        using var outMs = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await decStream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs))) > 0) outMs.Write(buf, 0, n);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
        Assert.True(readBase.AsyncReadCalls > 0);
    }

    // ------------------------------------------------------------------ overlapping async operations

    [Fact]
    public async Task OverlappingAsyncWritesOnSameStream_SecondFailsCleanly_StreamStillUsableAfter()
    {
        var gated = new GatedOnceStream();
        var bs = new BrotliStream(gated, CompressionMode.Compress, leaveOpen: true);
        try
        {
            // Large incompressible payload: forces the encoder to emit output (and hence await the base
            // stream's WriteAsync, which the gate holds open) before the call can complete.
            byte[] chunk1 = Corpus.Random(300_000, 1);
            byte[] chunk2 = Corpus.Text(20_000, 2);

            Task first = bs.WriteAsync(chunk1.AsMemory()).AsTask();
            await gated.FirstCallEntered.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.False(first.IsCompleted);

            // Second call while the first is still in flight (blocked on the gate).
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await bs.WriteAsync(chunk2.AsMemory()).AsTask());

            gated.ReleaseGate();
            await first.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            // Stream must still be usable: write more and finish successfully.
            await bs.WriteAsync(chunk2.AsMemory()).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await bs.FlushAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        }
        finally
        {
            await bs.DisposeAsync();
        }
    }

    [Fact]
    public async Task OverlappingAsyncReadsOnSameStream_SecondFailsCleanly()
    {
        byte[] data = Corpus.Text(100_000, 3);
        using var ms = new MemoryStream();
        using (var enc = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true)) enc.Write(data, 0, data.Length);
        ms.Position = 0;

        var readBase = new AsyncOnlyStream();
        readBase.SetReadSource(ms.ToArray());
        await using var dec = new BrotliStream(readBase, CompressionMode.Decompress, leaveOpen: true);
        var buf1 = new byte[4096];
        var buf2 = new byte[4096];

        Task<int> first = dec.ReadAsync(buf1).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await dec.ReadAsync(buf2).AsTask());
        int n1 = await first.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.True(n1 > 0);

        // still usable afterward
        int n2 = await dec.ReadAsync(buf2).AsTask().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.True(n2 >= 0);
    }

    // ------------------------------------------------------------------ IOException propagation

    [Fact]
    public async Task WriteAsync_PropagatesBaseStreamIOException_ThenDisposalDoesNotSurfaceADifferentError()
    {
        // Once the base stream has rejected a write, the stream remembers it: disposal releases resources and
        // does not retry the final block, which could only raise a second, later exception over the first.
        var throwing = new ThrowingStream(ThrowPoint.Write);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: true);
        byte[] data = Corpus.Text(500_000, 4);
        var ex = await Assert.ThrowsAsync<IOException>(async () => await bs.WriteAsync(data.AsMemory()).AsTask());
        Assert.Same(throwing.Sentinel, ex);

        await bs.DisposeAsync();

        // leaveOpen is honoured, and nothing further was written to the broken stream.
        Assert.Equal(0, throwing.DisposeAsyncCalls);
    }

    [Fact]
    public async Task FlushAsync_PropagatesBaseStreamIOException()
    {
        var throwing = new ThrowingStream(ThrowPoint.Flush);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: true);
        bs.Write(new byte[] { 1, 2, 3 }, 0, 3);
        var ex = await Assert.ThrowsAsync<IOException>(async () => await bs.FlushAsync());
        Assert.Same(throwing.Sentinel, ex);
        await bs.DisposeAsync();
    }

    [Fact]
    public void Flush_PropagatesBaseStreamIOException()
    {
        var throwing = new ThrowingStream(ThrowPoint.Flush);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: true);
        bs.Write(new byte[] { 1, 2, 3 }, 0, 3);
        var ex = Assert.Throws<IOException>(() => bs.Flush());
        Assert.Same(throwing.Sentinel, ex);
        bs.Dispose();
    }

    // ------------------------------------------------------------------ DisposeAsync failure modes

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_WhenFinalWriteThrows_LeaveOpenHonored(bool leaveOpen)
    {
        var throwing = new ThrowingStream(ThrowPoint.Write);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: leaveOpen);
        var ex = await Assert.ThrowsAsync<IOException>(async () => await bs.DisposeAsync());
        Assert.Same(throwing.Sentinel, ex);
        // Dispose of the base stream is decided by leaveOpen regardless of the write failure.
        Assert.Equal(leaveOpen ? 0 : 1, throwing.DisposeAsyncCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_WhenBaseStreamDisposeAsyncThrows_LeaveOpenHonored(bool leaveOpen)
    {
        var throwing = new ThrowingStream(ThrowPoint.DisposeAsync);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: leaveOpen);
        bs.Write(new byte[] { 9, 9, 9 }, 0, 3);
        if (leaveOpen)
        {
            // Base stream's DisposeAsync must not even be invoked, so no exception here.
            await bs.DisposeAsync();
            Assert.Equal(0, throwing.DisposeAsyncCalls);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<IOException>(async () => await bs.DisposeAsync());
            Assert.Same(throwing.Sentinel, ex);
            Assert.Equal(1, throwing.DisposeAsyncCalls);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_WhenFinalWriteThrows_LeaveOpenHonored(bool leaveOpen)
    {
        var throwing = new ThrowingStream(ThrowPoint.Write);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: leaveOpen);
        var ex = Assert.Throws<IOException>(() => bs.Dispose());
        Assert.Same(throwing.Sentinel, ex);
        Assert.Equal(leaveOpen ? 0 : 1, throwing.DisposeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_WhenBaseStreamDisposeThrows_LeaveOpenHonored(bool leaveOpen)
    {
        var throwing = new ThrowingStream(ThrowPoint.DisposeSync);
        var bs = new BrotliStream(throwing, CompressionMode.Compress, leaveOpen: leaveOpen);
        bs.Write(new byte[] { 9, 9, 9 }, 0, 3);
        if (leaveOpen)
        {
            bs.Dispose();
            Assert.Equal(0, throwing.DisposeCalls);
        }
        else
        {
            var ex = Assert.Throws<IOException>(() => bs.Dispose());
            Assert.Same(throwing.Sentinel, ex);
            Assert.Equal(1, throwing.DisposeCalls);
        }
    }

    // ------------------------------------------------------------------ post-disposal behavior

    [Fact]
    public void AfterSyncDispose_StreamRejectsEverythingConsistently()
    {
        var ms = new MemoryStream();
        var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true);
        bs.Write(new byte[] { 1 }, 0, 1);
        bs.Dispose();

        Assert.False(bs.CanRead);
        Assert.False(bs.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => bs.Write(new byte[] { 1 }, 0, 1));
        Assert.Throws<ObjectDisposedException>(() => bs.Read(new byte[1], 0, 1));
        Assert.Throws<ObjectDisposedException>(() => bs.Flush());
        Assert.Throws<ObjectDisposedException>(() => _ = bs.BaseStream);
        // Second dispose must be a no-op, not throw.
        bs.Dispose();
    }

    [Fact]
    public async Task AfterAsyncDispose_StreamRejectsEverythingConsistently()
    {
        var ms = new MemoryStream();
        var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true);
        await bs.WriteAsync(new byte[] { 1 }.AsMemory());
        await bs.DisposeAsync();

        Assert.False(bs.CanRead);
        Assert.False(bs.CanWrite);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await bs.WriteAsync(new byte[] { 1 }.AsMemory()).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await bs.ReadAsync(new byte[1].AsMemory()).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await bs.FlushAsync());
        Assert.Throws<ObjectDisposedException>(() => _ = bs.BaseStream);
        // Second DisposeAsync must be a no-op, not throw.
        await bs.DisposeAsync();
    }

    // ------------------------------------------------------------------ flush-then-decode-partial before dispose

    [Fact]
    public void FlushThenDecodeSoFar_BeforeDispose_ThenWriteMoreAndFinish()
    {
        byte[] first = Corpus.Text(60_000, 1);
        byte[] second = Corpus.Text(40_000, 2);
        using var ms = new MemoryStream();
        using var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true);

        bs.Write(first, 0, first.Length);
        bs.Flush();

        byte[] partial = ms.ToArray();
        Assert.True(partial.Length > 0);

        // Decode exactly the bytes flushed so far from an independent copy: everything written so far must come back.
        // Use a single bounded Read (not CopyTo/a loop-to-EOF): the underlying bytes are a valid but non-final
        // Brotli stream, so a second Read that hits the base stream's EOF would be mis-treated as the final block
        // and throw TruncatedInput -- that is expected given Stream's Read-returns-0-means-EOF contract, not a
        // BrotliStream defect, so the test must not drive the decode past what was actually flushed.
        using var partialStream = new BrotliStream(new MemoryStream(partial), CompressionMode.Decompress);
        var recovered = new byte[first.Length];
        int got = partialStream.Read(recovered, 0, recovered.Length);
        Assert.Equal(first.Length, got);
        Assert.True(first.AsSpan().SequenceEqual(recovered));

        // Now write the rest and finish.
        bs.Write(second, 0, second.Length);
        bs.Dispose();

        byte[] full = ms.ToArray();
        using var finalStream = new BrotliStream(new MemoryStream(full), CompressionMode.Decompress);
        using var finalOut = new MemoryStream();
        finalStream.CopyTo(finalOut);
        var expected = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, expected, 0, first.Length);
        Buffer.BlockCopy(second, 0, expected, first.Length, second.Length);
        Assert.True(expected.AsSpan().SequenceEqual(finalOut.ToArray()));
    }

    // ------------------------------------------------------------------ zero-length ops, ReadByte/WriteByte, EOF behavior

    [Fact]
    public void ZeroLengthReadAndWrite_AreNoOps()
    {
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            bs.Write(Array.Empty<byte>(), 0, 0);
            bs.Write(new byte[] { 1, 2, 3 }, 0, 3);
            bs.Write(Array.Empty<byte>(), 0, 0);
        }
        ms.Position = 0;
        using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        Assert.Equal(0, dec.Read(new byte[4], 0, 0));
        var buf = new byte[16];
        int n = dec.Read(buf, 0, buf.Length);
        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 1, 2, 3 }, buf[..3]);
    }

    [Fact]
    public void ReadByteAndWriteByte_HandleBoundaryValues()
    {
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            bs.WriteByte(0);
            bs.WriteByte(255);
            bs.WriteByte(42);
        }
        ms.Position = 0;
        using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        Assert.Equal(0, dec.ReadByte());
        Assert.Equal(255, dec.ReadByte());
        Assert.Equal(42, dec.ReadByte());
        Assert.Equal(-1, dec.ReadByte());
    }

    [Fact]
    public void RepeatedReadsAfterEof_ReturnZeroWithoutTouchingBaseStreamAgain()
    {
        byte[] data = Corpus.Text(1000, 1);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true)) bs.Write(data, 0, data.Length);
        byte[] compressed = ms.ToArray();

        var countingBase = new ChunkedStream(compressed, compressed.Length);
        using var dec = new BrotliStream(countingBase, CompressionMode.Decompress, leaveOpen: true);
        var buf = new byte[4096];
        int total = 0;
        int n;
        while ((n = dec.Read(buf, 0, buf.Length)) > 0) total += n;
        Assert.Equal(data.Length, total);

        int callsAtEof = countingBase.ReadCalls;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, dec.Read(buf, 0, buf.Length));
        }
        Assert.Equal(callsAtEof, countingBase.ReadCalls);
    }

    // ------------------------------------------------------------------ CopyTo / CopyToAsync

    [Fact]
    public void CopyTo_DecompressesFully()
    {
        byte[] data = Corpus.Text(200_000, 7);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true)) bs.Write(data, 0, data.Length);
        ms.Position = 0;
        using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs, 4096);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    [Fact]
    public async Task CopyToAsync_DecompressesFully()
    {
        byte[] data = Corpus.Ints(200_000, 8);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true)) bs.Write(data, 0, data.Length);
        ms.Position = 0;
        await using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        await dec.CopyToAsync(outMs, 4096).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    [Fact]
    public void CopyTo_FromArbitraryStreamIntoCompressingStream()
    {
        byte[] data = Corpus.Text(150_000, 13);
        using var src = new MemoryStream(data);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            src.CopyTo(bs, 8192);
        }
        ms.Position = 0;
        using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }
}
