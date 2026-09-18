using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BrotliManagedFast;
using Xunit;

namespace BrotliManagedFast.Tests;

public class FeatureInteractionTests
{
    private static byte[] Text(int length, int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder(length);
        string[] words = { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "brotli", "compress", "managed", "window" };
        while (sb.Length < length)
        {
            sb.Append(words[rng.Next(words.Length)]);
            sb.Append(' ');
        }
        return Encoding.UTF8.GetBytes(sb.ToString(0, length));
    }

    private static byte[] Random(int length, int seed)
    {
        var buf = new byte[length];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    // ---------- Concatenation ----------

    [Fact]
    public void ConcatFragmentOrderPreserved()
    {
        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 20, Concatenable = true };
        byte[] a = BrotliEncoder.Compress(Encoding.UTF8.GetBytes("AAAA-fragment-one-"), opts);
        byte[] b = BrotliEncoder.Compress(Encoding.UTF8.GetBytes("BBBB-fragment-two-"), opts);
        byte[] joined = BrotliConcat.Concatenate(a, b, a);
        byte[] decoded = BrotliDecoder.Decompress(joined);
        string text = Encoding.UTF8.GetString(decoded);
        Assert.Equal("AAAA-fragment-one-BBBB-fragment-two-AAAA-fragment-one-", text);
    }

    [Fact]
    public void ConcatManySmallFragments()
    {
        var opts = new BrotliCompressionOptions { Quality = 3, WindowLog = 18, Concatenable = true };
        var fragments = new ReadOnlyMemory<byte>[50];
        var expected = new StringBuilder();
        for (int i = 0; i < fragments.Length; i++)
        {
            string s = $"chunk-{i:D3}-";
            expected.Append(s);
            fragments[i] = BrotliEncoder.Compress(Encoding.UTF8.GetBytes(s), opts);
        }
        byte[] joined = BrotliConcat.Concatenate(fragments);
        byte[] decoded = BrotliDecoder.Decompress(joined);
        Assert.Equal(expected.ToString(), Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void ConcatWildlyDifferentSizedFragments()
    {
        var opts = new BrotliCompressionOptions { Quality = 4, WindowLog = 20, Concatenable = true };
        byte[] tiny = BrotliEncoder.Compress(new byte[] { 1 }, opts);
        byte[] big = BrotliEncoder.Compress(Text(500_000, 11), opts);
        byte[] empty = BrotliEncoder.Compress(Array.Empty<byte>(), opts);
        byte[] joined = BrotliConcat.Concatenate(tiny, big, empty);
        byte[] decoded = BrotliDecoder.Decompress(joined);
        byte[] expected = new byte[] { 1 }.Concat(Text(500_000, 11)).ToArray();
        Assert.True(decoded.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void ConcatOfAlreadyConcatenatedResult()
    {
        // A stream produced by joining fragments (with a terminator) is a complete, ordinary stream:
        // it cannot itself be re-joined as a fragment (no window header re-use guarantee needed here,
        // just verify a full decode of a nested join still round-trips through Finish/Concatenate paths).
        var opts = new BrotliCompressionOptions { Quality = 4, WindowLog = 19, Concatenable = true };
        byte[] a = BrotliEncoder.Compress(Encoding.UTF8.GetBytes("part-A"), opts);
        byte[] b = BrotliEncoder.Compress(Encoding.UTF8.GetBytes("part-B"), opts);
        byte[] ab = BrotliConcat.Concatenate(a, b);
        byte[] decodedAb = BrotliDecoder.Decompress(ab);
        Assert.Equal("part-Apart-B", Encoding.UTF8.GetString(decodedAb));

        byte[] c = BrotliEncoder.Compress(Encoding.UTF8.GetBytes("part-C"), opts);
        byte[] abc = BrotliConcat.Concatenate(a, b, c);
        byte[] decodedAbc = BrotliDecoder.Decompress(abc);
        Assert.Equal("part-Apart-Bpart-C", Encoding.UTF8.GetString(decodedAbc));
    }

    [Fact]
    public void ConcatFragmentsAtDifferentQualitiesSameWindow()
    {
        byte[] a = BrotliEncoder.Compress(Text(2000, 1), new BrotliCompressionOptions { Quality = 0, WindowLog = 20, Concatenable = true });
        byte[] b = BrotliEncoder.Compress(Text(2000, 2), new BrotliCompressionOptions { Quality = 9, WindowLog = 20, Concatenable = true });
        byte[] c = BrotliEncoder.Compress(Text(2000, 3), new BrotliCompressionOptions { Quality = 11, WindowLog = 20, Concatenable = true });
        byte[] joined = BrotliConcat.Concatenate(a, b, c);
        byte[] decoded = BrotliDecoder.Decompress(joined);
        byte[] expected = Text(2000, 1).Concat(Text(2000, 2)).Concat(Text(2000, 3)).ToArray();
        Assert.True(decoded.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void ConcatRejectsMismatchedLargeWindowFlag()
    {
        // Same numeric window bits are impossible to alias between normal/large encoding for >24,
        // but check the large-window boundary itself is still enforced across fragments.
        byte[] a = BrotliEncoder.Compress(Text(1000, 5), new BrotliCompressionOptions { WindowLog = 24, Concatenable = true });
        byte[] b = BrotliEncoder.Compress(Text(1000, 6), new BrotliCompressionOptions { WindowLog = 25, LargeWindow = true, Concatenable = true });
        Assert.Throws<ArgumentException>(() => BrotliConcat.Concatenate(a, b));
    }

    // ---------- Large Window ----------

    [Fact]
    public void LargeWindowWithConcatenation()
    {
        var opts = new BrotliCompressionOptions { Quality = 6, WindowLog = 26, LargeWindow = true, Concatenable = true };
        byte[] block = Text(1_500_000, 21);
        byte[] a = BrotliEncoder.Compress(block, opts);
        byte[] b = BrotliEncoder.Compress(Text(1000, 22), opts);
        byte[] joined = BrotliConcat.Concatenate(a, b);
        byte[] decoded = BrotliDecoder.Decompress(joined, new BrotliDecompressionOptions { MaxWindowLog = 30 });
        byte[] expected = block.Concat(Text(1000, 22)).ToArray();
        Assert.True(decoded.AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void LargeWindowRejectedByLowerMaxWindowLog()
    {
        byte[] compressed = BrotliEncoder.Compress(Text(2_000_000, 23), new BrotliCompressionOptions { Quality = 4, WindowLog = 26, LargeWindow = true });
        var decoder = new BrotliDecoder(new BrotliDecompressionOptions { MaxWindowLog = 24 });
        var dest = new byte[4 << 20];
        var status = decoder.Decompress(compressed, dest, out _, out _, isFinalBlock: true);
        Assert.NotEqual(System.Buffers.OperationStatus.Done, status);
    }

    [Fact]
    public void LargeWindowWithPrefixDictionary()
    {
        byte[] dictBytes = Random(30_000, 30);
        var dict = BrotliDictionary.Create(dictBytes);
        byte[] input = dictBytes.AsSpan(5_000, 4_000).ToArray().Concat(Text(2_000_000, 24)).ToArray();
        var opts = new BrotliCompressionOptions { Quality = 6, WindowLog = 27, LargeWindow = true, Dictionary = dict };
        byte[] compressed = BrotliEncoder.Compress(input, opts);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = dict, MaxWindowLog = 30 });
        Assert.True(decoded.AsSpan().SequenceEqual(input));
    }

    [Fact]
    public void LargeWindowThroughStreamingApi()
    {
        byte[] data = Text(3_000_000, 25);
        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 28, LargeWindow = true };
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, opts, leaveOpen: true))
        {
            bs.Write(data, 0, data.Length);
        }
        ms.Position = 0;
        using var outMs = new MemoryStream();
        using (var decodeStream = new BrotliStream(ms, new BrotliDecompressionOptions { MaxWindowLog = 30 }, leaveOpen: true))
        {
            decodeStream.CopyTo(outMs);
        }
        Assert.True(outMs.ToArray().AsSpan().SequenceEqual(data));
    }

    // ---------- Prefix dictionaries ----------

    [Fact]
    public async Task SharedDictionaryAcrossConcurrentThreads()
    {
        byte[] dictBytes = Random(20_000, 40);
        var dict = BrotliDictionary.Create(dictBytes);
        int threadCount = 8;
        var exceptions = new List<Exception>();
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int seed = t;
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        byte[] input = dictBytes.AsSpan(1000 * (seed + 1), 2000).ToArray().Concat(Text(3000, seed * 100 + i)).ToArray();
                        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 18, Dictionary = dict };
                        byte[] compressed = BrotliEncoder.Compress(input, opts);
                        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = dict });
                        Assert.True(decoded.AsSpan().SequenceEqual(input));
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) { exceptions.Add(ex); }
                }
            });
        }
        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void DictionaryCopyUnaffectedByCallerMutation()
    {
        byte[] dictBytes = Random(5000, 41);
        byte[] originalCopy = dictBytes.ToArray();
        var dict = BrotliDictionary.Create(dictBytes);
        // Mutate the caller's array after Create; the dictionary must be unaffected.
        for (int i = 0; i < dictBytes.Length; i++) dictBytes[i] ^= 0xFF;

        byte[] input = originalCopy.AsSpan(1000, 2000).ToArray().Concat(Text(1000, 42)).ToArray();
        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 16, Dictionary = dict };
        byte[] compressed = BrotliEncoder.Compress(input, opts);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = dict });
        Assert.True(decoded.AsSpan().SequenceEqual(input));
        // Also confirm the dictionary's own exposed bytes still match the original, unmutated copy.
        Assert.True(dict.Span.SequenceEqual(originalCopy));
    }

    [Fact]
    public void DecodingWithoutDictionaryWhenOneWasUsedFails()
    {
        byte[] dictBytes = Random(8000, 43);
        var dict = BrotliDictionary.Create(dictBytes);
        byte[] input = dictBytes.AsSpan(2000, 3000).ToArray().Concat(Text(500, 44)).ToArray();
        byte[] compressed = BrotliEncoder.Compress(input, new BrotliCompressionOptions { Quality = 6, WindowLog = 16, Dictionary = dict });

        // Decoding without the dictionary must not silently succeed with wrong output; either it
        // fails outright, or it produces different bytes than the original input.
        try
        {
            byte[] decoded = BrotliDecoder.Decompress(compressed);
            Assert.False(decoded.AsSpan().SequenceEqual(input));
        }
        catch (InvalidDataException)
        {
            // acceptable: corrupt-data failure
        }
    }

    [Fact]
    public void DecodingWithDifferentDictionarySameLengthFails()
    {
        byte[] dictBytes = Random(8000, 45);
        byte[] otherDictBytes = Random(8000, 46);
        var dict = BrotliDictionary.Create(dictBytes);
        var otherDict = BrotliDictionary.Create(otherDictBytes);
        byte[] input = dictBytes.AsSpan(2000, 3000).ToArray().Concat(Text(500, 47)).ToArray();
        byte[] compressed = BrotliEncoder.Compress(input, new BrotliCompressionOptions { Quality = 6, WindowLog = 16, Dictionary = dict });

        try
        {
            byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = otherDict });
            Assert.False(decoded.AsSpan().SequenceEqual(input));
        }
        catch (InvalidDataException)
        {
            // acceptable: corrupt-data failure
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void DictionaryRoundtripsAcrossQualities(int quality)
    {
        byte[] dictBytes = Random(10_000, 48 + quality);
        var dict = BrotliDictionary.Create(dictBytes);
        byte[] input = dictBytes.AsSpan(3000, 4000).ToArray().Concat(Text(2000, 49 + quality)).ToArray();
        var opts = new BrotliCompressionOptions { Quality = quality, WindowLog = 18, Dictionary = dict };
        byte[] compressed = BrotliEncoder.Compress(input, opts);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = dict });
        Assert.True(decoded.AsSpan().SequenceEqual(input));
    }

    // ---------- Parallel compression ----------

    [Fact]
    public void ParallelOutputDecodesIdenticallyAcrossDegrees()
    {
        byte[] data = Text(3_000_000, 60);
        byte[]? reference = null;
        foreach (int degree in new[] { 1, 2, 4, 8 })
        {
            byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 4 }, chunkSize: 1 << 19, maxDegreeOfParallelism: degree);
            byte[] decoded = BrotliDecoder.Decompress(compressed);
            Assert.True(decoded.AsSpan().SequenceEqual(data), $"degree={degree}");
            reference ??= decoded;
            Assert.True(decoded.AsSpan().SequenceEqual(reference));
        }
    }

    [Theory]
    [InlineData(1 << 16)]
    [InlineData(1 << 17)]
    [InlineData(1 << 19)]
    [InlineData(1 << 21)]
    public void ParallelOutputDecodesIdenticallyAcrossChunkSizes(int chunkSize)
    {
        byte[] data = Text(2_500_000, 61);
        byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 3 }, chunkSize: chunkSize, maxDegreeOfParallelism: 4);
        byte[] decoded = BrotliDecoder.Decompress(compressed);
        Assert.True(decoded.AsSpan().SequenceEqual(data));
    }
    [Fact]
    public void ParallelCompressionRefusesAPrefixDictionaryUpFront()
    {
        // Chunks are compressed independently, so a prefix dictionary could only apply to the first one.
        // The refusal is immediate and the same at any input size, rather than surfacing from the workers.
        byte[] dictionaryBytes = Random(20_000, 70);
        var dictionary = BrotliDictionary.Create(dictionaryBytes);
        byte[] data = Text(3_000_000, 71);
        var options = new BrotliCompressionOptions { Quality = 4, Dictionary = dictionary };

        Assert.Throws<ArgumentException>(() => BrotliParallel.Compress(data, options, chunkSize: 1 << 20, maxDegreeOfParallelism: 4));
    }

    // ---------- System.IO.Compression interop for ordinary windows ----------

    [Fact]
    public void ConcatenatedStreamDecodesWithSystemIoCompression()
    {
        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 20, Concatenable = true };
        byte[] a = BrotliEncoder.Compress(Text(50_000, 80), opts);
        byte[] b = BrotliEncoder.Compress(Text(50_000, 81), opts);
        byte[] joined = BrotliConcat.Concatenate(a, b);

        using var input = new MemoryStream(joined);
        using var brotli = new System.IO.Compression.BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);

        byte[] expected = Text(50_000, 80).Concat(Text(50_000, 81)).ToArray();
        Assert.True(output.ToArray().AsSpan().SequenceEqual(expected));
    }

    [Fact]
    public void ParallelOutputDecodesWithSystemIoCompression()
    {
        byte[] data = Text(2_000_000, 82);
        byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 4 }, chunkSize: 1 << 19, maxDegreeOfParallelism: 4);

        using var input = new MemoryStream(compressed);
        using var brotli = new System.IO.Compression.BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);

        Assert.True(output.ToArray().AsSpan().SequenceEqual(data));
    }
}
