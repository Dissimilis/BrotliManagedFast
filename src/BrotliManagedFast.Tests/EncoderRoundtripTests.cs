using System.Buffers;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Tests;

/// <summary>Our encoder's output must decode with the native decoder and with our own decoder.</summary>
public class EncoderRoundtripTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var (name, _) in Corpus.Standard())
        {
            foreach (int quality in new[] { 0, 1, 2, 4, 6, 9, 10, 11 })
            {
                foreach (int window in new[] { 10, 16, 22 })
                {
                    yield return new object[] { name, quality, window };
                }
            }
        }
    }

    private static byte[] Data(string name) => Corpus.Standard().First(c => c.name == name).data;

    public static byte[] NativeDecompress(byte[] compressed, int expectedLength)
    {
        using var dec = new SysBrotli.BrotliDecoder();
        var dst = new byte[expectedLength + 16];
        var status = dec.Decompress(compressed, dst, out int consumed, out int written);
        if (status != OperationStatus.Done) throw new InvalidOperationException($"native decode failed: {status}");
        if (consumed != compressed.Length) throw new InvalidOperationException("native decoder did not consume all input");
        return dst.AsSpan(0, written).ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NativeDecoderAcceptsOurOutput(string name, int quality, int window)
    {
        byte[] data = Data(name);
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = window });
        byte[] back = NativeDecompress(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back), $"native roundtrip mismatch {name} q{quality} w{window}");

        byte[] ours = BrotliDecoder.Decompress(compressed);
        Assert.True(data.AsSpan().SequenceEqual(ours), $"own roundtrip mismatch {name} q{quality} w{window}");
    }

    [Fact]
    public void TinyInputsProduceMinimalStreams()
    {
        byte[] empty = BrotliEncoder.Compress(Array.Empty<byte>());
        Assert.Single(empty);
        Assert.Empty(NativeDecompress(empty, 0));

        foreach (int n in new[] { 1, 2, 3, 5, 10, 33, 100 })
        {
            byte[] data = Corpus.Random(n, n);
            byte[] compressed = BrotliEncoder.Compress(data);
            Assert.True(compressed.Length <= BrotliEncoder.GetMaxCompressedLength(n), $"n={n}: {compressed.Length} > bound");
            Assert.True(data.AsSpan().SequenceEqual(NativeDecompress(compressed, n)));
        }
    }

    [Fact]
    public void IncompressibleInputStaysWithinBound()
    {
        foreach (int n in new[] { 1000, 20_000, 100_000, 300_000 })
        {
            byte[] data = Corpus.Random(n, n);
            foreach (int q in new[] { 0, 4, 9, 11 })
            {
                byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = q });
                Assert.True(compressed.Length <= BrotliEncoder.GetMaxCompressedLength(n), $"n={n} q{q}: {compressed.Length} > {BrotliEncoder.GetMaxCompressedLength(n)}");
                Assert.True(data.AsSpan().SequenceEqual(NativeDecompress(compressed, n)));
            }
        }
    }

    [Fact]
    public void StreamingWithSmallBuffersAndFlushes() => StreamWithFlushes(5);

    /// <summary>Quality 11 emits its metablocks through a different path, so it repeats the flush torture test.</summary>
    [Fact]
    public void StreamingWithSmallBuffersAndFlushesQuality11() => StreamWithFlushes(11);

    private static void StreamWithFlushes(int quality)
    {
        byte[] data = Corpus.Text(250_000, 11);
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = quality, WindowLog = 18 });
        var output = new MemoryStream();
        var dst = new byte[37];
        int pos = 0;
        var rng = new Random(5);
        while (pos < data.Length)
        {
            int take = Math.Min(rng.Next(1, 3000), data.Length - pos);
            bool final = pos + take == data.Length;
            var src = data.AsSpan(pos, take);
            for (;;)
            {
                var st = enc.Compress(src, dst, out int consumed, out int written, final);
                output.Write(dst, 0, written);
                src = src.Slice(consumed);
                if (st == OperationStatus.Done) break;
                if (st == OperationStatus.NeedMoreData) { Assert.True(src.IsEmpty); break; }
                Assert.Equal(OperationStatus.DestinationTooSmall, st);
            }
            pos += take;
            if (rng.Next(4) == 0 && pos < data.Length)
            {
                OperationStatus fs;
                do
                {
                    fs = enc.Flush(dst, out int w);
                    output.Write(dst, 0, w);
                } while (fs == OperationStatus.DestinationTooSmall);
                Assert.Equal(OperationStatus.Done, fs);
            }
        }
        byte[] compressed = output.ToArray();
        Assert.True(data.AsSpan().SequenceEqual(NativeDecompress(compressed, data.Length)));
        Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(compressed)));
    }

    [Fact]
    public void FlushMakesDataDecodableImmediately()
    {
        byte[] part1 = Corpus.Text(5000, 1);
        byte[] part2 = Corpus.Text(5000, 2);
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = 4 });
        var dst = new byte[20_000];
        Assert.Equal(OperationStatus.NeedMoreData, enc.Compress(part1, dst, out _, out int w1, isFinalBlock: false));
        Assert.Equal(OperationStatus.Done, enc.Flush(dst.AsSpan(w1), out int w2));
        // Decoding what we have so far must yield part1 completely (stream not finished).
        using var dec = new BrotliDecoder(null);
        var outBuf = new byte[part1.Length + 100];
        var st = dec.Decompress(dst.AsSpan(0, w1 + w2), outBuf, out int consumed, out int written, isFinalBlock: false);
        Assert.Equal(OperationStatus.NeedMoreData, st);
        Assert.Equal(part1.Length, written);
        Assert.True(part1.AsSpan().SequenceEqual(outBuf.AsSpan(0, written)));
        Assert.Equal(w1 + w2, consumed);

        Assert.Equal(OperationStatus.Done, enc.Compress(part2, dst.AsSpan(w1 + w2), out _, out int w3, isFinalBlock: true));
        byte[] all = part1.Concat(part2).ToArray();
        Assert.True(all.AsSpan().SequenceEqual(NativeDecompress(dst.AsSpan(0, w1 + w2 + w3).ToArray(), all.Length)));
    }

    [Fact]
    public void DoubleFlushIsIdempotent()
    {
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = 4 });
        var dst = new byte[1000];
        enc.Compress(Corpus.Text(100, 1), dst, out _, out int w1, isFinalBlock: false);
        enc.Flush(dst.AsSpan(w1), out int w2);
        enc.Flush(dst.AsSpan(w1 + w2), out int w3);
        Assert.Equal(0, w3);
        enc.Compress(ReadOnlySpan<byte>.Empty, dst.AsSpan(w1 + w2), out _, out int w4, isFinalBlock: true);
        byte[] back = NativeDecompress(dst.AsSpan(0, w1 + w2 + w4).ToArray(), 100);
        Assert.Equal(100, back.Length);
    }


    [Fact]
    public void RatioIsReasonableOnText()
    {
        byte[] data = TestVectors.Read("alice29.txt");
        byte[] q1 = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 1 });
        byte[] q4 = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 4 });
        byte[] q9 = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 9 });
        Assert.True(q1.Length < data.Length / 2, $"q1 {q1.Length}");
        Assert.True(q4.Length <= q1.Length, $"q4 {q4.Length} > q1 {q1.Length}");
        Assert.True(q9.Length <= q4.Length, $"q9 {q9.Length} > q4 {q4.Length}");
        Assert.True(q9.Length < data.Length * 0.45, $"q9 {q9.Length}");
    }
}
