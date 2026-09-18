using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BrotliManagedFast.Tests;

/// <summary>
/// Contracts the public API promises in its documentation: argument checking, reuse through Reset, the
/// buffer-writer overloads, a caller-supplied pool, and the asynchronous stream paths. These are the parts a
/// caller reaches for that the format-level tests never touch.
/// </summary>
public class PublicApiContractTests
{
    private static byte[] Sample(int length = 200_000, int seed = 11)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        // Compressible but not trivial: runs of text-like bytes with occasional noise.
        for (int i = 0; i < length; i++)
        {
            data[i] = (i % 97 == 0) ? (byte)rng.Next(256) : (byte)("the quick brown fox "[i % 20]);
        }
        return data;
    }

    // ---- argument validation -------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(int.MaxValue)]
    public void QualityOutsideZeroToElevenIsRejected(int quality)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliCompressionOptions { Quality = quality });
    }

    [Theory]
    [InlineData(9)]
    [InlineData(31)]
    [InlineData(-1)]
    public void WindowLogOutsideTenToThirtyIsRejected(int windowLog)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliCompressionOptions { WindowLog = windowLog });
    }

    [Fact]
    public void WindowAboveTwentyFourNeedsLargeWindowOptIn()
    {
        var options = new BrotliCompressionOptions { WindowLog = 26 };
        // The option itself is in range; the combination is what fails, and it fails when it is used.
        Assert.Throws<ArgumentOutOfRangeException>(() => BrotliEncoder.Compress(Sample(64), options));
    }

    [Fact]
    public void NegativeSizeHintIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrotliCompressionOptions { SizeHint = -1 });
    }

    [Fact]
    public void ConcatenableWithADictionaryIsRejectedOnUse()
    {
        var options = new BrotliCompressionOptions
        {
            Concatenable = true,
            Dictionary = BrotliDictionary.Create(new byte[] { 1, 2, 3, 4 }),
        };
        Assert.Throws<ArgumentException>(() => BrotliEncoder.Compress(Sample(64), options));
    }

    [Fact]
    public void StreamConstructorsRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new BrotliStream(null!, CompressionMode.Compress));
        Assert.Throws<ArgumentNullException>(() => new BrotliStream(new MemoryStream(), (BrotliCompressionOptions)null!));
        Assert.Throws<ArgumentNullException>(() => new BrotliStream(new MemoryStream(), (BrotliDecompressionOptions)null!));
    }

    [Fact]
    public void StreamRefusesTheDirectionItWasNotOpenedFor()
    {
        using var backing = new MemoryStream();
        using (var compressing = new BrotliStream(backing, CompressionMode.Compress, leaveOpen: true))
        {
            Assert.False(compressing.CanRead);
            Assert.True(compressing.CanWrite);
            Assert.Throws<InvalidOperationException>(() => compressing.Read(new byte[16], 0, 16));
        }

        backing.Position = 0;
        using var decompressing = new BrotliStream(backing, CompressionMode.Decompress);
        Assert.True(decompressing.CanRead);
        Assert.False(decompressing.CanWrite);
        Assert.Throws<InvalidOperationException>(() => decompressing.Write(new byte[16], 0, 16));
    }

    [Fact]
    public void StreamsAreNotSeekable()
    {
        using var backing = new MemoryStream();
        using var s = new BrotliStream(backing, CompressionMode.Compress);
        Assert.False(s.CanSeek);
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
        Assert.Throws<NotSupportedException>(() => _ = s.Length);
    }

    // ---- reuse through Reset -------------------------------------------------------------------

    [Fact]
    public void EncoderResetProducesTheSameBytesAsAFreshEncoder()
    {
        byte[] first = Sample(50_000, seed: 1);
        byte[] second = Sample(70_000, seed: 2);
        var options = new BrotliCompressionOptions { Quality = 6, WindowLog = 20 };

        var reusedEncoder = new BrotliEncoder(options);
        CompressAll(ref reusedEncoder, first);
        reusedEncoder.Reset();
        byte[] reused = CompressAll(ref reusedEncoder, second);
        reusedEncoder.Dispose();

        var freshEncoder = new BrotliEncoder(options);
        byte[] fresh = CompressAll(ref freshEncoder, second);
        freshEncoder.Dispose();

        Assert.Equal(fresh, reused);
        Assert.Equal(second, BrotliDecoder.Decompress(reused, new BrotliDecompressionOptions { MaxOutputLength = second.Length }));
    }

    [Fact]
    public void DecoderResetAllowsASecondStream()
    {
        byte[] first = Sample(30_000, seed: 3);
        byte[] second = Sample(40_000, seed: 4);
        byte[] firstCompressed = BrotliEncoder.Compress(first);
        byte[] secondCompressed = BrotliEncoder.Compress(second);

        using var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxOutputLength = 1 << 20 });
        var buffer = new byte[1 << 20];

        Assert.Equal(OperationStatus.Done, decoder.Decompress(firstCompressed, buffer, out _, out int written1, isFinalBlock: true));
        Assert.Equal(first, buffer.AsSpan(0, written1).ToArray());

        decoder.Reset();

        Assert.Equal(OperationStatus.Done, decoder.Decompress(secondCompressed, buffer, out _, out int written2, isFinalBlock: true));
        Assert.Equal(second, buffer.AsSpan(0, written2).ToArray());
    }

    [Fact]
    public void DecoderIsUsableAgainAfterCorruptInput()
    {
        byte[] data = Sample(20_000, seed: 5);
        byte[] good = BrotliEncoder.Compress(data);
        byte[] corrupt = (byte[])good.Clone();
        for (int i = 1; i < corrupt.Length; i += 7) corrupt[i] ^= 0xA5;

        using var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxOutputLength = 1 << 20 });
        var buffer = new byte[1 << 20];

        OperationStatus bad = decoder.Decompress(corrupt, buffer, out _, out _, isFinalBlock: true);
        // Either it rejected the stream or it happened to stay valid; the point is what Reset does next.
        if (bad == OperationStatus.InvalidData) Assert.NotEqual(BrotliDecoderError.None, decoder.LastError);

        decoder.Reset();

        Assert.Equal(OperationStatus.Done, decoder.Decompress(good, buffer, out _, out int written, isFinalBlock: true));
        Assert.Equal(data, buffer.AsSpan(0, written).ToArray());
        Assert.Equal(BrotliDecoderError.None, decoder.LastError);
    }

    private static byte[] CompressAll(ref BrotliEncoder encoder, byte[] data)
    {
        var output = new ArrayBufferWriter<byte>();
        var chunk = new byte[4096];
        int consumedTotal = 0;
        while (true)
        {
            bool last = consumedTotal == data.Length;
            OperationStatus status = encoder.Compress(data.AsSpan(consumedTotal), chunk, out int consumed, out int written, isFinalBlock: last);
            consumedTotal += consumed;
            output.Write(chunk.AsSpan(0, written));
            if (last && status == OperationStatus.Done) break;
            Assert.NotEqual(OperationStatus.InvalidData, status);
        }
        return output.WrittenSpan.ToArray();
    }

    // ---- IBufferWriter overloads ---------------------------------------------------------------

    [Fact]
    public void BufferWriterOverloadsRoundTrip()
    {
        byte[] data = Sample(120_000, seed: 6);

        var compressed = new ArrayBufferWriter<byte>();
        BrotliEncoder.Compress(data, compressed, new BrotliCompressionOptions { Quality = 7 });
        Assert.True(compressed.WrittenCount > 0);
        Assert.True(compressed.WrittenCount < data.Length);

        var restored = new ArrayBufferWriter<byte>();
        BrotliDecoder.Decompress(compressed.WrittenSpan, restored, new BrotliDecompressionOptions { MaxOutputLength = data.Length });

        Assert.Equal(data.Length, restored.WrittenCount);
        Assert.Equal(data, restored.WrittenSpan.ToArray());
    }

    [Fact]
    public void ParallelBufferWriterOverloadRoundTrips()
    {
        byte[] data = Sample(3 << 20, seed: 7);
        var output = new ArrayBufferWriter<byte>();
        BrotliParallel.Compress(data, output, new BrotliCompressionOptions { Quality = 5 }, chunkSize: 1 << 20, maxDegreeOfParallelism: 3);
        byte[] back = BrotliDecoder.Decompress(output.WrittenSpan, new BrotliDecompressionOptions { MaxOutputLength = data.Length });
        Assert.Equal(data, back);
    }

    // ---- caller-supplied pool ------------------------------------------------------------------

    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _inner = ArrayPool<byte>.Shared;
        public int Rented;
        public int Returned;

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Rented);
            return _inner.Rent(minimumLength);
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Interlocked.Increment(ref Returned);
            _inner.Return(array, clearArray);
        }
    }

    [Fact]
    public void ACallerSuppliedPoolIsUsedAndFullyReturned()
    {
        byte[] data = Sample(150_000, seed: 8);
        var encodePool = new CountingPool();
        var decodePool = new CountingPool();

        var encoder = new BrotliEncoder(new BrotliCompressionOptions { Quality = 6, Pool = encodePool });
        byte[] compressed = CompressAll(ref encoder, data);
        encoder.Dispose();

        var buffer = new byte[data.Length];
        using (var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxOutputLength = data.Length, Pool = decodePool }))
        {
            Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, buffer, out _, out int written, isFinalBlock: true));
            Assert.Equal(data.Length, written);
        }

        Assert.True(encodePool.Rented > 0, "the encoder ignored the supplied pool");
        Assert.True(decodePool.Rented > 0, "the decoder ignored the supplied pool");
        Assert.Equal(encodePool.Rented, encodePool.Returned);
        Assert.Equal(decodePool.Rented, decodePool.Returned);
        Assert.Equal(data, buffer);
    }

    // ---- asynchronous stream paths -------------------------------------------------------------

    [Fact]
    public async Task AsyncRoundTripThroughTheStream()
    {
        byte[] data = Sample(300_000, seed: 9);

        var compressed = new MemoryStream();
        await using (var writer = new BrotliStream(compressed, new BrotliCompressionOptions { Quality = 5 }, leaveOpen: true))
        {
            // Several writes so the encoder buffers across calls.
            for (int offset = 0; offset < data.Length; offset += 50_000)
            {
                int count = Math.Min(50_000, data.Length - offset);
                await writer.WriteAsync(data.AsMemory(offset, count));
            }
            await writer.FlushAsync();
        }

        compressed.Position = 0;
        var restored = new MemoryStream();
        await using (var reader = new BrotliStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            var buffer = new byte[7_777];   // deliberately not a round number
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory());
                if (read == 0) break;
                restored.Write(buffer, 0, read);
            }
        }

        Assert.Equal(data, restored.ToArray());
    }

    [Fact]
    public async Task AsyncByteArrayOverloadsRoundTrip()
    {
        byte[] data = Sample(80_000, seed: 10);

        var compressed = new MemoryStream();
        using (var writer = new BrotliStream(compressed, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true))
        {
            await writer.WriteAsync(data, 0, data.Length, CancellationToken.None);
        }

        compressed.Position = 0;
        var restored = new MemoryStream();
        using (var reader = new BrotliStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            var buffer = new byte[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)) > 0)
            {
                restored.Write(buffer, 0, read);
            }
        }

        Assert.Equal(data, restored.ToArray());
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenStopsTheStream()
    {
        byte[] data = Sample(60_000, seed: 12);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var compressed = new MemoryStream();
        var writer = new BrotliStream(compressed, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.WriteAsync(data.AsMemory(), cts.Token));
        writer.Dispose();

        byte[] valid = BrotliEncoder.Compress(data);
        var source = new MemoryStream(valid);
        var reader = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        var readBuffer = new byte[1024];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await reader.ReadAsync(readBuffer.AsMemory(), cts.Token));
        reader.Dispose();
    }

    [Fact]
    public async Task DisposeAsyncFinishesTheStream()
    {
        byte[] data = Sample(40_000, seed: 13);
        var compressed = new MemoryStream();
        var writer = new BrotliStream(compressed, new BrotliCompressionOptions { Quality = 6 }, leaveOpen: true);
        await writer.WriteAsync(data.AsMemory());
        await writer.DisposeAsync();

        // Disposal, not an explicit flush, is what must have written the final block.
        byte[] back = BrotliDecoder.Decompress(compressed.ToArray(), new BrotliDecompressionOptions { MaxOutputLength = data.Length });
        Assert.Equal(data, back);
    }

    // ---- one-shot helpers and bounds ------------------------------------------------------------

    [Fact]
    public void TryCompressFitsInTheAdvertisedMaximumAndFailsCleanlyBelowIt()
    {
        byte[] data = Sample(90_000, seed: 14);
        int max = BrotliEncoder.GetMaxCompressedLength(data.Length);

        var exact = new byte[max];
        Assert.True(BrotliEncoder.TryCompress(data, exact, out int written));
        Assert.InRange(written, 1, max);
        Assert.Equal(data, BrotliDecoder.Decompress(exact.AsSpan(0, written), new BrotliDecompressionOptions { MaxOutputLength = data.Length }));

        // One byte short of what this input actually needs: report failure, do not throw.
        var tooSmall = new byte[written - 1];
        Assert.False(BrotliEncoder.TryCompress(data, tooSmall, out int partial));
        Assert.Equal(0, partial);
    }

    [Fact]
    public void GetMaxCompressedLengthCoversIncompressibleInput()
    {
        var rng = new Random(15);
        foreach (int size in new[] { 0, 1, 2, 1024, 70_000 })
        {
            var noise = new byte[size];
            rng.NextBytes(noise);
            int max = BrotliEncoder.GetMaxCompressedLength(size);
            var buffer = new byte[max];
            Assert.True(BrotliEncoder.TryCompress(noise, buffer, out int written), $"size {size} did not fit its advertised maximum");
            Assert.True(written <= max);
        }
    }

    // ---- modes, qualities and windows ------------------------------------------------------------

    [Theory]
    [InlineData(BrotliEncoderMode.Generic)]
    [InlineData(BrotliEncoderMode.Text)]
    [InlineData(BrotliEncoderMode.Font)]
    public void EveryModeRoundTripsAndTheNativeDecoderAgrees(BrotliEncoderMode mode)
    {
        byte[] data = Sample(60_000, seed: 16);
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 9, Mode = mode });
        Assert.Equal(data, BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length }));
        Assert.Equal(data, EncoderRoundtripTests.NativeDecompress(compressed, data.Length));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(24)]
    public void EveryQualityRoundTripsAtTheWindowExtremes(int windowLog)
    {
        byte[] data = Sample(70_000, seed: 17);
        for (int quality = 0; quality <= 11; quality++)
        {
            byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = windowLog });
            byte[] back = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length });
            Assert.True(data.AsSpan().SequenceEqual(back), $"quality {quality} at window {windowLog} did not round-trip");
        }
    }

    // ---- concatenation and parallel edges --------------------------------------------------------

    [Fact]
    public void ConcatenatingOneFragmentGivesAValidStream()
    {
        byte[] data = Sample(10_000, seed: 18);
        byte[] fragment = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 5, Concatenable = true });
        byte[] whole = BrotliConcat.Concatenate(fragment);
        Assert.Equal(data, BrotliDecoder.Decompress(whole, new BrotliDecompressionOptions { MaxOutputLength = data.Length }));
    }

    [Fact]
    public void ConcatenatingEmptyFragmentsGivesAnEmptyStream()
    {
        var options = new BrotliCompressionOptions { Quality = 5, Concatenable = true };
        byte[] a = BrotliEncoder.Compress(ReadOnlySpan<byte>.Empty, options);
        byte[] b = BrotliEncoder.Compress(ReadOnlySpan<byte>.Empty, options);
        byte[] whole = BrotliConcat.Concatenate(a, b);
        Assert.Empty(BrotliDecoder.Decompress(whole, new BrotliDecompressionOptions { MaxOutputLength = 16 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ParallelCompressionHonoursItsDegreeOfParallelism(int degree)
    {
        byte[] data = Sample(2 << 20, seed: 19);
        byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 4 }, chunkSize: 512 << 10, maxDegreeOfParallelism: degree);
        Assert.Equal(data, BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length }));
    }

    [Fact]
    public void ParallelCompressionHandlesInputSmallerThanOneChunk()
    {
        byte[] data = Sample(1000, seed: 20);
        byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 6 }, chunkSize: 1 << 20);
        Assert.Equal(data, BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length }));
        Assert.Equal(data, EncoderRoundtripTests.NativeDecompress(compressed, data.Length));
    }

    [Fact]
    public void ParallelCompressionHandlesEmptyInput()
    {
        byte[] compressed = BrotliParallel.Compress(ReadOnlyMemory<byte>.Empty, new BrotliCompressionOptions { Quality = 6 });
        Assert.Empty(BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = 16 }));
    }

    // ---- dictionaries ----------------------------------------------------------------------------

    [Fact]
    public void DecodingWithTheWrongDictionaryFailsRatherThanMisdecodes()
    {
        byte[] data = Sample(20_000, seed: 21);
        BrotliDictionary right = BrotliDictionary.Create(System.Text.Encoding.UTF8.GetBytes("the quick brown fox jumps over"));
        BrotliDictionary wrong = BrotliDictionary.Create(System.Text.Encoding.UTF8.GetBytes("something else entirely here!!"));

        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 9, Dictionary = right });

        byte[] correct = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length, Dictionary = right });
        Assert.Equal(data, correct);

        // A different dictionary of the same length must not silently produce the original bytes.
        var buffer = new byte[data.Length];
        using var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxOutputLength = data.Length, Dictionary = wrong });
        OperationStatus status = decoder.Decompress(compressed, buffer, out _, out int written, isFinalBlock: true);
        bool sameBytes = status == OperationStatus.Done && written == data.Length && data.AsSpan().SequenceEqual(buffer);
        Assert.False(sameBytes, "decoding with the wrong dictionary returned the original bytes");
    }

    [Fact]
    public void AnEmptyDictionaryBehavesLikeNoDictionary()
    {
        byte[] data = Sample(5_000, seed: 22);
        var options = new BrotliCompressionOptions { Quality = 5, Dictionary = BrotliDictionary.Create(ReadOnlySpan<byte>.Empty) };
        byte[] compressed = BrotliEncoder.Compress(data, options);
        Assert.Equal(data, BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = data.Length }));
    }

    // ---- disposal --------------------------------------------------------------------------------

    [Fact]
    public void DisposeReleasesBuffersAndLeavesTheCodecReusable()
    {
        // Dispose and Reset are the same operation here: both hand the pooled buffers back, and the next call
        // starts a new stream. A caller who disposes and then keeps going gets a fresh stream, not a fault.
        byte[] data = Sample(4_000, seed: 23);

        var encoder = new BrotliEncoder(new BrotliCompressionOptions { Quality = 5 });
        byte[] before = CompressAll(ref encoder, data);
        encoder.Dispose();
        byte[] after = CompressAll(ref encoder, data);
        encoder.Dispose();
        Assert.Equal(before, after);

        byte[] compressed = BrotliEncoder.Compress(data);
        var buffer = new byte[data.Length];
        var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxOutputLength = data.Length });
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, buffer, out _, out int written, isFinalBlock: true));
        decoder.Dispose();
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, buffer, out _, out int again, isFinalBlock: true));
        decoder.Dispose();
        Assert.Equal(written, again);
    }

    [Fact]
    public void AnEncoderPassedByValueIsASeparateEncoder()
    {
        // The encoder is a struct with lazily created state, so a copy does not share the original's stream.
        // This is the same shape as the runtime's own BrotliEncoder; the test pins it so it cannot drift.
        byte[] data = Sample(4_000, seed: 24);
        var encoder = new BrotliEncoder(new BrotliCompressionOptions { Quality = 5 });
        byte[] viaRef = CompressAll(ref encoder, data);
        encoder.Dispose();

        var second = new BrotliEncoder(new BrotliCompressionOptions { Quality = 5 });
        var copy = second;
        byte[] viaCopy = CompressAll(ref copy, data);
        copy.Dispose();
        second.Dispose();

        Assert.Equal(viaRef, viaCopy);
    }

    // ---- defects found by auditing the public surface ------------------------------------------

    [Fact]
    public void ADefaultConstructedEncoderUsesTheDefaultOptions()
    {
        // A struct's default value has to work; it used to dereference its missing options.
        var encoder = default(BrotliEncoder);
        var destination = new byte[64];
        Assert.Equal(OperationStatus.Done, encoder.Compress(new byte[] { 42 }, destination, out _, out int written, isFinalBlock: true));
        encoder.Dispose();
        Assert.Equal(new byte[] { 42 }, BrotliDecoder.Decompress(destination.AsSpan(0, written)));

        var decoder = default(BrotliDecoder);
        byte[] compressed = BrotliEncoder.Compress(new byte[] { 42 });
        Assert.Equal(OperationStatus.Done, decoder.Decompress(compressed, destination, out _, out int back, isFinalBlock: true));
        decoder.Dispose();
        Assert.Equal(new byte[] { 42 }, destination.AsSpan(0, back).ToArray());
    }

    /// <summary>A base stream that hands out a fixed script of chunks and can fail on demand.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private readonly int _failBeforeChunk;
        private int _served;
        public ScriptedStream(int failBeforeChunk, params byte[][] chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
            _failBeforeChunk = failBeforeChunk;
        }
        public int ReadsServed => _served;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served == _failBeforeChunk) { _served++; throw new IOException("scripted failure"); }
            if (_chunks.Count == 0) return 0;
            byte[] chunk = _chunks.Dequeue();
            _served++;
            int n = Math.Min(count, chunk.Length);
            Array.Copy(chunk, 0, buffer, offset, n);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AFailedRefillDoesNotReplayAlreadyConsumedInput()
    {
        // The refill used to reset the read position while leaving the old length in place, so a read that
        // threw left the bytes it had already consumed described as unread.
        byte[] data = Sample(5_000, seed: 25);
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 5 });
        int split = compressed.Length / 2;

        var source = new ScriptedStream(
            failBeforeChunk: 1,
            compressed.AsSpan(0, split).ToArray(),
            compressed.AsSpan(split).ToArray());

        using var stream = new BrotliStream(source, new BrotliDecompressionOptions { MaxOutputLength = data.Length });
        var buffer = new byte[1024];
        // The failure surfaces on whichever read needs the second chunk.
        Assert.Throws<IOException>(() =>
        {
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
        });

        // Whatever the stream does next, it must not decode the first half twice.
        try
        {
            var rest = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) rest.Write(buffer, 0, read);
            Assert.True(rest.Length <= data.Length, "the stream produced more bytes than the input held");
        }
        catch (InvalidDataException)
        {
            // Refusing to continue after an I/O failure is acceptable; silently duplicating input is not.
        }
    }

    [Fact]
    public void TrailingDataIsRejectedEvenWhenItArrivesInALaterRead()
    {
        // 0x3B is a complete empty stream; the 0xFF after it is trailing rubbish the option promises to catch.
        var source = new ScriptedStream(failBeforeChunk: -1, new byte[] { 0x3B }, new byte[] { 0xFF });
        using var stream = new BrotliStream(source, new BrotliDecompressionOptions { RejectTrailingData = true, MaxOutputLength = 64 });
        var buffer = new byte[16];
        Assert.Throws<InvalidDataException>(() =>
        {
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
        });
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public void ParallelCompressionKeepsItsContractAcrossTheChunkBoundary(int size)
    {
        byte[] data = Sample(size, seed: 26);

        // Asking for a fragment gives a fragment at every size, not a finished stream above the boundary.
        byte[] fragment = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 4, Concatenable = true }, chunkSize: 65536, maxDegreeOfParallelism: 2);
        byte[] finished = BrotliConcat.Finish(fragment);
        Assert.Equal(data, BrotliDecoder.Decompress(finished, new BrotliDecompressionOptions { MaxOutputLength = size }));

        // A prefix dictionary is refused at every size, rather than working below the boundary and throwing above it.
        var withDictionary = new BrotliCompressionOptions { Quality = 4, Dictionary = BrotliDictionary.Create(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) };
        Assert.Throws<ArgumentException>(() => BrotliParallel.Compress(data, withDictionary, chunkSize: 65536, maxDegreeOfParallelism: 2));
    }

    [Fact]
    public void JoinedFragmentsCanBeJoinedAgain()
    {
        var options = new BrotliCompressionOptions { Quality = 5, Concatenable = true };
        byte[] a = BrotliEncoder.Compress(System.Text.Encoding.UTF8.GetBytes("first part, "), options);
        byte[] b = BrotliEncoder.Compress(System.Text.Encoding.UTF8.GetBytes("second part, "), options);
        byte[] c = BrotliEncoder.Compress(System.Text.Encoding.UTF8.GetBytes("third part"), options);

        var partial = new ArrayBufferWriter<byte>();
        BrotliConcat.Join(partial, new[] { (ReadOnlyMemory<byte>)a, b });
        byte[] whole = BrotliConcat.Concatenate(partial.WrittenMemory, c);

        byte[] back = BrotliDecoder.Decompress(whole, new BrotliDecompressionOptions { MaxOutputLength = 128 });
        Assert.Equal("first part, second part, third part", System.Text.Encoding.UTF8.GetString(back));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var encoder = new BrotliEncoder(new BrotliCompressionOptions());
        encoder.Dispose();
        encoder.Dispose();

        var decoder = new BrotliDecoder(new BrotliDecompressionOptions());
        decoder.Dispose();
        decoder.Dispose();
    }
}
