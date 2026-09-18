using System.Buffers;

namespace BrotliManagedFast.Tests;

public class DecoderVectorTests
{
    [Theory]
    [MemberData(nameof(TestVectors.Pairs), MemberType = typeof(TestVectors))]
    public void OneShotDecodeMatchesReference(string compressedName)
    {
        var (compressed, original) = TestVectors.Load(compressedName);
        byte[] actual = BrotliDecoder.Decompress(compressed);
        Assert.Equal(original.Length, actual.Length);
        Assert.True(original.AsSpan().SequenceEqual(actual), $"content mismatch for {compressedName}");
    }

    [Theory]
    [MemberData(nameof(TestVectors.Pairs), MemberType = typeof(TestVectors))]
    public void TryDecompressExactBuffer(string compressedName)
    {
        var (compressed, original) = TestVectors.Load(compressedName);
        var dst = new byte[original.Length];
        Assert.True(BrotliDecoder.TryDecompress(compressed, dst, out int written));
        Assert.Equal(original.Length, written);
        Assert.True(original.AsSpan().SequenceEqual(dst));
    }

    [Theory]
    [MemberData(nameof(TestVectors.Pairs), MemberType = typeof(TestVectors))]
    public void StreamingWithSmallChunksMatchesReference(string compressedName)
    {
        var (compressed, original) = TestVectors.Load(compressedName);
        // Vary chunk sizes; include 1-byte input and small output windows.
        foreach ((int inChunk, int outChunk) in new[] { (1, 1), (1, 7), (3, 1), (17, 5), (256, 64), (1 << 20, 1) })
        {
            if (compressed.Length > 20_000 && (inChunk == 1 || outChunk == 1))
            {
                // Keep runtime reasonable for the larger vectors.
                if (inChunk == 1 && outChunk == 1) continue;
            }
            byte[] actual = DecodeHelpers.DecodeChunked(compressed, inChunk, outChunk, out OperationStatus status, out BrotliDecoderError err);
            Assert.True(status == OperationStatus.Done, $"{compressedName} in={inChunk} out={outChunk}: {status} {err}");
            Assert.True(original.AsSpan().SequenceEqual(actual), $"content mismatch for {compressedName} in={inChunk} out={outChunk}");
        }
    }

    [Fact]
    public void EmptyStreamDecodesToEmpty()
    {
        Assert.Empty(BrotliDecoder.Decompress(new byte[] { 0x3B }));
        Assert.Empty(BrotliDecoder.Decompress(new byte[] { 0x06 }));
    }

    [Fact]
    public void TrailingDataIsReportedThroughBytesConsumed()
    {
        var (compressed, original) = TestVectors.Load("quickfox.compressed");
        byte[] withTrailer = compressed.Concat(new byte[] { 1, 2, 3 }).ToArray();
        using var dec = new BrotliDecoder(null);
        var dst = new byte[original.Length + 16];
        var status = dec.Decompress(withTrailer, dst, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(compressed.Length, consumed);
        Assert.Equal(original.Length, written);

        using var strict = new BrotliDecoder(new BrotliDecompressionOptions { RejectTrailingData = true });
        status = strict.Decompress(withTrailer, dst, out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.TrailingData, strict.LastError);
    }

    [Fact]
    public void TruncatedInputIsReportedWhenFinal()
    {
        var (compressed, original) = TestVectors.Load("alice29.txt.compressed");
        byte[] truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();
        using var dec = new BrotliDecoder(null);
        var dst = new byte[original.Length];
        var status = dec.Decompress(truncated, dst, out _, out _, isFinalBlock: false);
        Assert.Equal(OperationStatus.NeedMoreData, status);
        using var dec2 = new BrotliDecoder(null);
        status = dec2.Decompress(truncated, dst, out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.TruncatedInput, dec2.LastError);
    }

    [Fact]
    public void OutputLimitIsEnforced()
    {
        var (compressed, original) = TestVectors.Load("alice29.txt.compressed");
        var opts = new BrotliDecompressionOptions { MaxOutputLength = original.Length - 1 };
        using var dec = new BrotliDecoder(opts);
        var dst = new byte[original.Length];
        var status = dec.Decompress(compressed, dst, out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.OutputLimitExceeded, dec.LastError);

        var ok = new BrotliDecompressionOptions { MaxOutputLength = original.Length };
        Assert.True(BrotliDecoder.TryDecompress(compressed, dst, out int written, ok));
        Assert.Equal(original.Length, written);
    }

    [Fact]
    public void WindowLimitIsEnforced()
    {
        // alice29 vector was produced with a 22-bit window.
        var (compressed, original) = TestVectors.Load("alice29.txt.compressed");
        var dst = new byte[original.Length];
        using var dec = new BrotliDecoder(new BrotliDecompressionOptions { MaxWindowLog = 16 });
        var status = dec.Decompress(compressed, dst, out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(BrotliDecoderError.WindowTooLarge, dec.LastError);
    }

    [Fact]
    public void GarbageInputNeverThrows()
    {
        var rng = new Random(1234);
        var dst = new byte[1 << 16];
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(1, 64)];
            rng.NextBytes(junk);
            using var dec = new BrotliDecoder(null);
            var status = dec.Decompress(junk, dst, out _, out _, isFinalBlock: true);
            Assert.True(status == OperationStatus.Done || status == OperationStatus.InvalidData || status == OperationStatus.DestinationTooSmall);
        }
    }

    [Fact]
    public void MutatedVectorsNeverThrow()
    {
        var rng = new Random(99);
        var dst = new byte[1 << 20];
        foreach (string name in new[] { "quickfox.compressed", "monkey.compressed", "ukkonooa.compressed", "x.compressed", "cp852-utf8.compressed", "asyoulik.txt.compressed" })
        {
            var (compressed, _) = TestVectors.Load(name);
            for (int i = 0; i < 300; i++)
            {
                var mutated = (byte[])compressed.Clone();
                int flips = rng.Next(1, 4);
                for (int f = 0; f < flips; f++) mutated[rng.Next(mutated.Length)] ^= (byte)(1 << rng.Next(8));
                using var dec = new BrotliDecoder(null);
                var status = dec.Decompress(mutated, dst, out _, out _, isFinalBlock: true);
                Assert.True(status == OperationStatus.Done || status == OperationStatus.InvalidData || status == OperationStatus.DestinationTooSmall, $"{name} #{i}: {status}");
            }
        }
    }
}

public static class DecodeHelpers
{
    /// <summary>Feeds input in chunks of <paramref name="inChunk"/> and drains output in chunks of <paramref name="outChunk"/>.</summary>
    public static byte[] DecodeChunked(ReadOnlySpan<byte> compressed, int inChunk, int outChunk, out OperationStatus finalStatus, out BrotliDecoderError error, BrotliDecompressionOptions? options = null)
    {
        using var dec = new BrotliDecoder(options);
        var output = new MemoryStream();
        var outBuf = new byte[outChunk];
        int inPos = 0;
        finalStatus = OperationStatus.NeedMoreData;
        int guard = 0;
        while (true)
        {
            int take = Math.Min(inChunk, compressed.Length - inPos);
            bool final = inPos + take >= compressed.Length;
            finalStatus = dec.Decompress(compressed.Slice(inPos, take), outBuf, out int consumed, out int written, final);
            inPos += consumed;
            output.Write(outBuf, 0, written);
            if (finalStatus == OperationStatus.Done || finalStatus == OperationStatus.InvalidData) break;
            if (finalStatus == OperationStatus.NeedMoreData && inPos >= compressed.Length && consumed == 0 && written == 0)
            {
                if (++guard > 4) break; // no progress
            }
            else
            {
                guard = 0;
            }
        }
        error = dec.LastError;
        return output.ToArray();
    }
}
