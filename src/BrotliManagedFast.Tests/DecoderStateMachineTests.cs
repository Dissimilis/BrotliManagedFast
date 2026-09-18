using System.Buffers;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Tests;

/// <summary>
/// Exercises the resumable decoder state machine: byte-at-a-time feeding, zero-length calls,
/// truncation at every offset, output-length limits, window-log limits and metadata metablocks.
/// </summary>
public class DecoderStateMachineTests
{
    // ---- minimal LSB-first bit writer, used to hand-build metadata / empty / uncompressed metablocks ----
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private ulong _acc;
        private int _accBits;

        public void WriteBits(int count, uint value)
        {
            _acc |= (ulong)value << _accBits;
            _accBits += count;
            while (_accBits >= 8)
            {
                _bytes.Add((byte)_acc);
                _acc >>= 8;
                _accBits -= 8;
            }
        }

        public byte[] Finish()
        {
            if (_accBits > 0) _bytes.Add((byte)_acc);
            return _bytes.ToArray();
        }
    }

    // Builds: WBITS header (window=22, encoded as the reference does: 0 -> 16 default; here we use the
    // simplest non-default encoding "0100001" for 22 per RFC 7932 Table; to keep this robust we instead
    // use window 16 which is the plain default WBITS=0 => 1 bit "0").
    private static byte[] BuildStream(Action<BitWriter> body)
    {
        var w = new BitWriter();
        w.WriteBits(1, 0); // WBITS: 1-bit "0" => window bits 16 (default)
        body(w);
        return w.Finish();
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] EncodeSimple(byte[] data, int quality = 5, int window = 16)
        => BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = window });

    private static byte[] NativeReference(byte[] data, int quality, int window)
    {
        // Cross-check that our encoder output is a well-formed brotli stream via the platform decoder.
        using var dec = new SysBrotli.BrotliDecoder();
        var dst = new byte[data.Length + 16];
        var compressed = EncodeSimple(data, quality, window);
        var status = dec.Decompress(compressed, dst, out int consumed, out int written);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(compressed.Length, consumed);
        Assert.True(data.AsSpan().SequenceEqual(dst.AsSpan(0, written)));
        return compressed;
    }

    // ------------------------------------------------------------------ 1. byte-at-a-time / 1-byte destination

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 16)]
    [InlineData(5, 16)]
    [InlineData(9, 22)]
    public void ByteAtATimeInputAndOneByteOutput_MatchesWholeBufferDecode(int quality, int window)
    {
        byte[] data = Corpus.Text(5000, 7);
        byte[] compressed = EncodeSimple(data, quality, window);

        byte[] whole = BrotliDecoder.Decompress(compressed);
        Assert.True(data.AsSpan().SequenceEqual(whole));

        using var decoder = new BrotliDecoder();
        var output = new List<byte>();
        int offset = 0;
        var oneByteOut = new byte[1];
        int guard = 0;
        while (true)
        {
            int srcLen = offset < compressed.Length ? 1 : 0;
            ReadOnlySpan<byte> src = compressed.AsSpan(offset, srcLen);
            bool isFinal = offset >= compressed.Length;
            var status = decoder.Decompress(src, oneByteOut, out int consumed, out int written, isFinalBlock: isFinal);
            Assert.InRange(consumed, 0, src.Length);
            Assert.InRange(written, 0, oneByteOut.Length);
            if (written > 0) output.Add(oneByteOut[0]);
            offset += consumed;

            if (status == OperationStatus.Done) break;
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.True(++guard < compressed.Length * 8 + 10_000, "decoder made no progress; possible infinite loop");
        }
        Assert.True(data.AsSpan().SequenceEqual(output.ToArray()));
    }

    // ------------------------------------------------------------------ 2. zero-length source / destination at arbitrary points

    [Fact]
    public void ZeroLengthCallsInterleaved_MakeProgressAndPreserveData()
    {
        byte[] data = Corpus.Text(4000, 11);
        byte[] compressed = EncodeSimple(data, 4, 16);

        using var decoder = new BrotliDecoder();
        var output = new List<byte>();
        int offset = 0;
        int step = 0;
        int guard = 0;
        var buf16 = new byte[16];
        var zeroOut = Array.Empty<byte>();
        while (true)
        {
            step++;
            // Alternate: zero-length source, zero-length dest, and normal 16-byte chunks.
            bool zeroSrc = step % 3 == 0;
            bool zeroDst = step % 5 == 0;
            int srcLen = zeroSrc ? 0 : Math.Min(16, compressed.Length - offset);
            ReadOnlySpan<byte> src = compressed.AsSpan(offset, srcLen);
            Span<byte> dst = zeroDst ? zeroOut : buf16;
            bool isFinal = !zeroSrc && offset + srcLen >= compressed.Length;
            var status = decoder.Decompress(src, dst, out int consumed, out int written, isFinalBlock: isFinal);
            Assert.InRange(consumed, 0, src.Length);
            Assert.InRange(written, 0, dst.Length);
            if (written > 0) output.AddRange(buf16.AsSpan(0, written).ToArray());
            offset += consumed;

            if (status == OperationStatus.Done) break;
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.True(++guard < 100_000, "decoder made no progress on zero-length calls");
        }
        Assert.True(data.AsSpan().SequenceEqual(output.ToArray()));
    }

    // ------------------------------------------------------------------ 3. TotalBytesWritten / IsFinished bookkeeping

    [Fact]
    public void TotalBytesWrittenAndIsFinished_TrackAcrossCallsAndReset()
    {
        byte[] data = Corpus.Text(3000, 3);
        byte[] compressed = EncodeSimple(data, 5, 16);

        using var decoder = new BrotliDecoder();
        long lastTotal = 0;
        int offset = 0;
        var buf = new byte[37];
        while (true)
        {
            Assert.False(decoder.IsFinished);
            int srcLen = Math.Min(23, compressed.Length - offset);
            var status = decoder.Decompress(compressed.AsSpan(offset, srcLen), buf, out int consumed, out int written, isFinalBlock: offset + srcLen >= compressed.Length);
            offset += consumed;
            Assert.True(decoder.TotalBytesWritten >= lastTotal);
            lastTotal = decoder.TotalBytesWritten;
            if (status == OperationStatus.Done) break;
        }
        Assert.Equal(data.Length, lastTotal);
        Assert.True(decoder.IsFinished);

        decoder.Reset();
        Assert.False(decoder.IsFinished);
        Assert.Equal(0, decoder.TotalBytesWritten);

        // Decoder must be reusable after Reset.
        int offset2 = 0;
        var buf2 = new byte[4096];
        var status2 = decoder.Decompress(compressed.AsSpan(offset2), buf2, out int consumed2, out int written2, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status2);
        Assert.True(data.AsSpan().SequenceEqual(buf2.AsSpan(0, written2)));
        Assert.True(decoder.IsFinished);
        Assert.Equal(data.Length, decoder.TotalBytesWritten);
    }

    // ------------------------------------------------------------------ 4. MaxOutputLength enforced cumulatively

    [Fact]
    public void MaxOutputLength_OneLessThanActual_NeverExceedsLimitCumulatively()
    {
        byte[] data = Corpus.Text(10_000, 9);
        byte[] compressed = EncodeSimple(data, 5, 18);
        long limit = data.Length - 1; // one less than the real output size

        var options = new BrotliDecompressionOptions { MaxOutputLength = limit };
        using var decoder = new BrotliDecoder(options);
        long totalWritten = 0;
        int offset = 0;
        var buf = new byte[97];
        bool hitLimit = false;
        while (true)
        {
            int srcLen = Math.Min(31, compressed.Length - offset);
            var status = decoder.Decompress(compressed.AsSpan(offset, srcLen), buf, out int consumed, out int written, isFinalBlock: offset + srcLen >= compressed.Length);
            offset += consumed;
            totalWritten += written;
            Assert.True(totalWritten <= limit, $"cumulative output {totalWritten} exceeded MaxOutputLength {limit}");
            if (status == OperationStatus.Done)
            {
                // Only reachable if the whole stream fits under the limit (delta made data shorter than actual? no: delta<=1 so should not happen here)
                break;
            }
            if (status == OperationStatus.InvalidData)
            {
                Assert.Equal(BrotliDecoderError.OutputLimitExceeded, decoder.LastError);
                hitLimit = true;
                break;
            }
        }
        Assert.True(hitLimit, "expected OutputLimitExceeded before exhausting output beyond the limit");
    }

    [Fact]
    public void MaxOutputLength_ExactSize_SucceedsAndEqualsLimit()
    {
        byte[] data = Corpus.Text(2000, 4);
        byte[] compressed = EncodeSimple(data, 5, 16);
        var options = new BrotliDecompressionOptions { MaxOutputLength = data.Length };
        var dst = new byte[data.Length];
        using var decoder = new BrotliDecoder(options);
        var status = decoder.Decompress(compressed, dst, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length, written);
        Assert.True(data.AsSpan().SequenceEqual(dst));
    }

    [Fact]
    public void MaxOutputLength_Zero_FailsImmediatelyOnNonEmptyStream()
    {
        byte[] data = Corpus.Text(500, 2);
        byte[] compressed = EncodeSimple(data, 5, 16);
        var options = new BrotliDecompressionOptions { MaxOutputLength = 0 };
        using var decoder = new BrotliDecoder(options);
        var dst = new byte[100];
        var status = decoder.Decompress(compressed, dst, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(0, written);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.OutputLimitExceeded, decoder.LastError);
    }

    // ------------------------------------------------------------------ 5. Truncation at every byte offset

    [Fact]
    public void TruncationAtEveryOffset_NeverThrowsNeverReturnsDone()
    {
        byte[] data = Corpus.Text(1500, 5);
        byte[] compressed = EncodeSimple(data, 5, 16);
        Assert.True(compressed.Length > 8, "need a multi-byte stream for meaningful truncation coverage");

        for (int cut = 0; cut < compressed.Length; cut++)
        {
            byte[] truncated = compressed.AsSpan(0, cut).ToArray();
            using var decoder = new BrotliDecoder();
            var dst = new byte[data.Length + 16];

            // First without isFinalBlock: must never claim Done on a proper prefix shorter than the whole stream,
            // and must never throw.
            OperationStatus status;
            try
            {
                status = decoder.Decompress(truncated, dst, out int consumed, out int written, isFinalBlock: false);
            }
            catch (Exception ex)
            {
                Assert.Fail($"cut={cut}: decoder threw {ex}");
                return;
            }
            Assert.NotEqual(OperationStatus.Done, status);

            // Then with isFinalBlock=true on a fresh decoder: must be InvalidData (typed error), never throw, never Done.
            using var decoder2 = new BrotliDecoder();
            OperationStatus status2;
            try
            {
                status2 = decoder2.Decompress(truncated, dst, out int consumed2, out int written2, isFinalBlock: true);
            }
            catch (Exception ex)
            {
                Assert.Fail($"cut={cut} (final): decoder threw {ex}");
                return;
            }
            Assert.NotEqual(OperationStatus.Done, status2);
            Assert.Equal(OperationStatus.InvalidData, status2);
        }
    }

    // ------------------------------------------------------------------ 6. isFinalBlock=false then true with no further input

    [Fact]
    public void IsFinalBlockFalseThenTrueWithNoFurtherInput_CompletesOrErrorsTyped()
    {
        byte[] data = Corpus.Text(2000, 6);
        byte[] compressed = EncodeSimple(data, 5, 16);

        using var decoder = new BrotliDecoder();
        var dst = new byte[data.Length + 16];

        // Feed the whole thing but claim it's not final.
        var status1 = decoder.Decompress(compressed, dst, out int consumed1, out int written1, isFinalBlock: false);
        // A complete stream should report Done even when isFinalBlock is false.
        if (status1 == OperationStatus.Done)
        {
            Assert.Equal(data.Length, written1);
            Assert.True(data.AsSpan().SequenceEqual(dst.AsSpan(0, written1)));
            return;
        }
        Assert.Equal(OperationStatus.NeedMoreData, status1);

        // Now call again with empty input and isFinalBlock=true: no further input is available,
        // so this must resolve to a typed truncation error, not throw, not silently return Done.
        var status2 = decoder.Decompress(ReadOnlySpan<byte>.Empty, dst, out int consumed2, out int written2, isFinalBlock: true);
        Assert.Equal(0, consumed2);
        Assert.NotEqual(OperationStatus.Done, status2);
        Assert.Equal(OperationStatus.InvalidData, status2);
        Assert.Equal(BrotliDecoderError.TruncatedInput, decoder.LastError);
    }

    // ------------------------------------------------------------------ 7. Window log limits

    [Fact]
    public void WindowLargerThanMaxWindowLog_FailsWithTypedError()
    {
        byte[] data = Corpus.Text(2000, 8);
        // The encoder auto-shrinks the window to fit SizeHint (defaulted to source.Length by Compress()),
        // so a small buffer would not actually be encoded with a 22-bit window. Force SizeHint up so the
        // requested WindowLog is written to the header as-is.
        var encOptions = new BrotliCompressionOptions { Quality = 5, WindowLog = 22, SizeHint = 1L << 22 };
        byte[] compressed = BrotliEncoder.Compress(data, encOptions);

        var options = new BrotliDecompressionOptions { MaxWindowLog = 18 };
        using var decoder = new BrotliDecoder(options);
        var dst = new byte[data.Length + 16];
        var status = decoder.Decompress(compressed, dst, out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.WindowTooLarge, decoder.LastError);
    }

    [Fact]
    public void WindowExactlyEqualToMaxWindowLog_Succeeds()
    {
        byte[] data = Corpus.Text(2000, 8);
        var encOptions = new BrotliCompressionOptions { Quality = 5, WindowLog = 22, SizeHint = 1L << 22 };
        byte[] compressed = BrotliEncoder.Compress(data, encOptions);

        var options = new BrotliDecompressionOptions { MaxWindowLog = 22 };
        using var decoder = new BrotliDecoder(options);
        var dst = new byte[data.Length + 16];
        var status = decoder.Decompress(compressed, dst, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length, written);
        Assert.True(data.AsSpan().SequenceEqual(dst.AsSpan(0, written)));
    }

    // ------------------------------------------------------------------ 8. Metadata / uncompressed metablocks, mixed
    //
    // Each metadata or uncompressed metablock byte-aligns itself before its raw payload bytes
    // (DecoderCore.cs around the JumpToByteBoundary call, state MetablockHeaderSubstate). That means every
    // block's header-bits-plus-payload unit can be built independently with its own BitWriter (whose
    // Finish() flushes any partial trailing byte as zero-padded, which is exactly the required alignment)
    // and the units concatenated: the only bits that must NOT be separately aligned are the WBITS bits,
    // which share the first block's BitWriter so they stay contiguous with its header.

    private static byte[] BuildMetadataBlock(byte[] payload, bool includeWindowBitsPrefix)
    {
        var w = new BitWriter();
        if (includeWindowBitsPrefix) w.WriteBits(1, 0); // WBITS -> window 16 (default)
        w.WriteBits(1, 0); // ISLAST = 0
        w.WriteBits(2, 3); // MNIBBLES marker == 3 -> metadata block
        w.WriteBits(1, 0); // reserved bit
        int lenBytes = payload.Length == 0 ? 0 : (payload.Length <= 0xFF ? 1 : payload.Length <= 0xFFFF ? 2 : 3);
        w.WriteBits(2, (uint)lenBytes);
        if (lenBytes > 0)
        {
            int len = payload.Length - 1; // encoded length is size-1
            for (int i = 0; i < lenBytes; i++) w.WriteBits(8, (uint)((len >> (i * 8)) & 0xFF));
        }
        byte[] header = w.Finish(); // byte-aligned by construction (Finish pads the trailing partial byte with zero bits)
        var result = new byte[header.Length + payload.Length];
        header.CopyTo(result, 0);
        payload.CopyTo(result, header.Length);
        return result;
    }

    /// <summary>
    /// Builds a non-final uncompressed metablock. Per DecodeMetablockLength, ISUNCOMPRESSED is only read
    /// (and can only be true) for a non-final metablock; a final metablock is always treated as
    /// Huffman-compressed, so this helper never builds a final block.
    /// </summary>
    private static byte[] BuildUncompressedBlock(byte[] data)
    {
        var w = new BitWriter();
        w.WriteBits(1, 0); // ISLAST = 0
        w.WriteBits(2, 0); // MNIBBLES = 4 (bits=0 -> 0+4)
        int mlen = data.Length - 1; // encoded length is size-1
        for (int i = 0; i < 4; i++) w.WriteBits(4, (uint)((mlen >> (i * 4)) & 0xF));
        w.WriteBits(1, 1); // ISUNCOMPRESSED = 1
        byte[] header = w.Finish();
        var result = new byte[header.Length + data.Length];
        header.CopyTo(result, 0);
        data.CopyTo(result, header.Length);
        return result;
    }

    private static byte[] BuildFinalEmptyMetablock()
    {
        // ISLAST=1, ISLASTEMPTY=1: the terminator used by real encoders. Ends the stream with no data.
        var w = new BitWriter();
        w.WriteBits(1, 1);
        w.WriteBits(1, 1);
        return w.Finish();
    }

    /// <summary>WBITS + metadata block + non-final uncompressed block(s) carrying all the real data + terminator.</summary>
    private static byte[] BuildMixedMetadataStream(byte[] metadataPayload, byte[] realData)
    {
        int half = realData.Length / 2;
        byte[] firstMeta = BuildMetadataBlock(metadataPayload, includeWindowBitsPrefix: true);
        byte[] firstData = BuildUncompressedBlock(realData.AsSpan(0, half).ToArray());
        byte[] secondMeta = BuildMetadataBlock(metadataPayload, includeWindowBitsPrefix: false);
        byte[] secondData = BuildUncompressedBlock(realData.AsSpan(half).ToArray());
        byte[] terminator = BuildFinalEmptyMetablock();

        var stream = new byte[firstMeta.Length + firstData.Length + secondMeta.Length + secondData.Length + terminator.Length];
        int o = 0;
        firstMeta.CopyTo(stream, o); o += firstMeta.Length;
        firstData.CopyTo(stream, o); o += firstData.Length;
        secondMeta.CopyTo(stream, o); o += secondMeta.Length;
        secondData.CopyTo(stream, o); o += secondData.Length;
        terminator.CopyTo(stream, o);
        return stream;
    }

    [Fact]
    public void MetadataAndUncompressedMetablocksMixed_SkipCorrectlyAndProduceOnlyRealData()
    {
        byte[] metadataPayload = new byte[] { 1, 2, 3, 4, 5 };
        byte[] realData = Corpus.Random(64, 15);
        byte[] stream = BuildMixedMetadataStream(metadataPayload, realData);

        // Sanity: native decoder must also accept and skip the metadata/empty blocks identically.
        using var nat = new SysBrotli.BrotliDecoder();
        var natDst = new byte[realData.Length + 16];
        var natStatus = nat.Decompress(stream, natDst, out int natConsumed, out int natWritten);
        Assert.Equal(OperationStatus.Done, natStatus);
        Assert.Equal(stream.Length, natConsumed);
        Assert.True(realData.AsSpan().SequenceEqual(natDst.AsSpan(0, natWritten)));

        using var decoder = new BrotliDecoder();
        var dst = new byte[realData.Length + 16];
        var status = decoder.Decompress(stream, dst, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(stream.Length, consumed);
        Assert.True(realData.AsSpan().SequenceEqual(dst.AsSpan(0, written)));
    }

    [Fact]
    public void MetadataMetablockFedByteAtATime_NeverThrowsAndEventuallyProducesRealData()
    {
        byte[] metadataPayload = Corpus.Random(300, 21); // spans multiple bytes of length field
        byte[] realData = Corpus.Text(200, 23);
        byte[] stream = BuildMixedMetadataStream(metadataPayload, realData);

        using var decoder = new BrotliDecoder();
        var output = new List<byte>();
        int offset = 0;
        var buf = new byte[8];
        int guard = 0;
        while (true)
        {
            int srcLen = offset < stream.Length ? 1 : 0;
            bool isFinal = offset >= stream.Length;
            var status = decoder.Decompress(stream.AsSpan(offset, srcLen), buf, out int consumed, out int written, isFinalBlock: isFinal);
            offset += consumed;
            if (written > 0) output.AddRange(buf.AsSpan(0, written).ToArray());
            if (status == OperationStatus.Done) break;
            Assert.NotEqual(OperationStatus.InvalidData, status);
            Assert.True(++guard < stream.Length * 8 + 10_000, "no progress while feeding metadata byte-at-a-time");
        }
        Assert.True(realData.AsSpan().SequenceEqual(output.ToArray()));
    }
}
