using System;
using System.Buffers;
using System.Linq;
using Xunit;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Tests;

/// <summary>Cross-cutting matrix of encoder options and internal buffer boundaries.</summary>
public class EncoderOptionMatrixTests
{
    private static byte[] Roundtrip(byte[] compressed, int expectedLength)
    {
        byte[] native = EncoderRoundtripTests.NativeDecompress(compressed, expectedLength);
        byte[] ours = BrotliDecoder.Decompress(compressed);
        Assert.True(native.AsSpan().SequenceEqual(ours), "native and our decoder disagree");
        return native;
    }

    public static IEnumerable<object[]> SizeBoundaryCases()
    {
        int[] sizes = { 0, 1, 2, 3, 4, 63, 64, 65, 65535, 65536, 65537 };
        int[] qualities = { 0, 1, 2, 3, 4, 5, 6, 9, 10, 11 };
        int[] windows = { 10, 16, 22, 24 };
        foreach (int q in qualities)
        {
            foreach (int w in windows)
            {
                foreach (int n in sizes)
                {
                    yield return new object[] { q, w, n };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SizeBoundaryCases))]
    public void QualityWindowSizeMatrixRoundtrips(int quality, int window, int size)
    {
        byte[] data = Corpus.Text(size, quality * 1000 + window * 10 + size);
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = window });
        byte[] back = Roundtrip(compressed, size);
        Assert.True(data.AsSpan().SequenceEqual(back), $"q{quality} w{window} n{size}");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(4, 16)]
    [InlineData(9, 22)]
    [InlineData(11, 24)]
    public void InputSlightlyLargerThanWindowRoundtrips(int quality, int window)
    {
        int windowSize = 1 << window;
        int n = windowSize + 37;
        byte[] data = Corpus.Text(n, quality + window);
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = window });
        byte[] back = Roundtrip(compressed, n);
        Assert.True(data.AsSpan().SequenceEqual(back));
    }

    public static IEnumerable<object[]> SizeHintQualities()
    {
        // Quality 11's understated-hint case is already covered by StreamAndFeatureTests.Quality11SurvivesAnUnderstatedSizeHint.
        foreach (int q in new[] { 0, 1, 4, 9, 10 }) yield return new object[] { q };
    }

    [Theory]
    [MemberData(nameof(SizeHintQualities))]
    public void SizeHintVariantsNeverCorruptTheStream(int quality)
    {
        byte[] data = Corpus.Text(20_000, quality + 7);
        long[] hints = { 0, 1, data.Length, 5, data.Length * 10L, long.MaxValue };
        foreach (long hint in hints)
        {
            var options = new BrotliCompressionOptions { Quality = quality, WindowLog = 20, SizeHint = hint };
            byte[] compressed = BrotliEncoder.Compress(data, options);
            byte[] back = Roundtrip(compressed, data.Length);
            Assert.True(data.AsSpan().SequenceEqual(back), $"q{quality} hint {hint}");
        }
    }

    [Fact]
    public void DestinationOneByteAtATimeStillRoundtrips()
    {
        byte[] data = Corpus.Text(4000, 42);
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = 4, WindowLog = 18 });
        var outMs = new MemoryStream();
        var one = new byte[1];
        int pos = 0;
        while (pos < data.Length)
        {
            bool final = false; // feed whole buffer at once, but drain one byte of destination per call
            var st = enc.Compress(data.AsSpan(pos), one, out int consumed, out int written, final);
            outMs.Write(one, 0, written);
            pos += consumed;
            Assert.True(st == OperationStatus.DestinationTooSmall || st == OperationStatus.NeedMoreData);
        }
        for (;;)
        {
            var st = enc.Compress(ReadOnlySpan<byte>.Empty, one, out _, out int written, isFinalBlock: true);
            outMs.Write(one, 0, written);
            if (st == OperationStatus.Done) break;
            Assert.Equal(OperationStatus.DestinationTooSmall, st);
        }
        byte[] compressed = outMs.ToArray();
        byte[] back = Roundtrip(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back));
    }

    [Fact]
    public void SourceOneByteAtATimeWithFlushMatchesSingleShot()
    {
        byte[] data = Corpus.Text(3000, 43);
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = 4, WindowLog = 18 });
        var outMs = new MemoryStream();
        var dst = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            bool final = i == data.Length - 1;
            var src = data.AsSpan(i, 1);
            for (;;)
            {
                var st = enc.Compress(src, dst, out int consumed, out int written, final);
                outMs.Write(dst, 0, written);
                src = src.Slice(consumed);
                if (st == OperationStatus.Done || st == OperationStatus.NeedMoreData) break;
                Assert.Equal(OperationStatus.DestinationTooSmall, st);
            }
            if (i % 7 == 0 && !final)
            {
                OperationStatus fs;
                do
                {
                    fs = enc.Flush(dst, out int w);
                    outMs.Write(dst, 0, w);
                } while (fs == OperationStatus.DestinationTooSmall);
                Assert.Equal(OperationStatus.Done, fs);
            }
        }
        byte[] compressed = outMs.ToArray();
        byte[] back = Roundtrip(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back));
    }

    [Fact]
    public void FlushAtVeryStartTwiceInARowAndBeforeFinal()
    {
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = 4, WindowLog = 18 });
        var dst = new byte[4096];
        var outMs = new MemoryStream();

        // Flush before any input.
        var fs0 = enc.Flush(dst, out int w0);
        Assert.Equal(OperationStatus.Done, fs0);
        outMs.Write(dst, 0, w0);

        // Flush again immediately (twice in a row).
        var fs1 = enc.Flush(dst, out int w1);
        Assert.Equal(OperationStatus.Done, fs1);
        outMs.Write(dst, 0, w1);

        byte[] data = Corpus.Text(2000, 44);
        Assert.Equal(OperationStatus.NeedMoreData, enc.Compress(data, dst, out int consumed, out int written, isFinalBlock: false));
        Assert.Equal(data.Length, consumed);
        outMs.Write(dst, 0, written);

        // Flush right before the final (empty) block.
        var fs2 = enc.Flush(dst, out int w2);
        Assert.Equal(OperationStatus.Done, fs2);
        outMs.Write(dst, 0, w2);

        Assert.Equal(OperationStatus.Done, enc.Compress(ReadOnlySpan<byte>.Empty, dst, out _, out int wf, isFinalBlock: true));
        outMs.Write(dst, 0, wf);

        byte[] compressed = outMs.ToArray();
        byte[] back = Roundtrip(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back));
    }

    public static IEnumerable<object[]> DictionarySizeCases()
    {
        foreach (int q in new[] { 0, 4, 9, 11 })
        {
            yield return new object[] { q, 0 };
            yield return new object[] { q, 1 };
            yield return new object[] { q, 1 << 16 }; // window (16) size exactly
            yield return new object[] { q, (1 << 16) + 500 }; // larger than the window
        }
    }

    [Theory]
    [MemberData(nameof(DictionarySizeCases))]
    public void PrefixDictionarySizesRoundtrip(int quality, int dictLength)
    {
        byte[] dictionary = Corpus.Text(Math.Max(dictLength, 0), 1000 + dictLength);
        if (dictionary.Length != dictLength) dictionary = dictionary.Take(dictLength).ToArray();
        byte[] data = Corpus.Text(3000, quality + dictLength + 5);
        var options = new BrotliCompressionOptions { Quality = quality, WindowLog = 16, Dictionary = BrotliDictionary.Create(dictionary) };
        byte[] compressed = BrotliEncoder.Compress(data, options);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = BrotliDictionary.Create(dictionary) });
        Assert.True(data.AsSpan().SequenceEqual(decoded), $"q{quality} dictLen{dictLength}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(11)]
    public void DataEntirelyADictionarySubstringRoundtrips(int quality)
    {
        byte[] dictionary = Corpus.Text(5000, 77);
        // The whole input is a contiguous slice of the dictionary.
        byte[] data = dictionary.AsSpan(1234, 2000).ToArray();
        var options = new BrotliCompressionOptions { Quality = quality, WindowLog = 18, Dictionary = BrotliDictionary.Create(dictionary) };
        byte[] compressed = BrotliEncoder.Compress(data, options);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = BrotliDictionary.Create(dictionary) });
        Assert.True(data.AsSpan().SequenceEqual(decoded));
    }

    public static IEnumerable<object[]> ModeCases()
    {
        foreach (var mode in new[] { BrotliEncoderMode.Generic, BrotliEncoderMode.Text, BrotliEncoderMode.Font })
        {
            yield return new object[] { mode, "utf8text" };
            yield return new object[] { mode, "binary" };
            yield return new object[] { mode, "fontlike" };
        }
    }

    private static byte[] ModeData(string kind)
    {
        return kind switch
        {
            "utf8text" => System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Žąsis skrenda į pietus. The quick brown fox. éèê ", 300))),
            "binary" => Corpus.Random(20_000, 5),
            "fontlike" => Corpus.Ints(20_000, 9),
            _ => throw new ArgumentException(kind),
        };
    }

    [Theory]
    [MemberData(nameof(ModeCases))]
    public void EncoderModeAcrossDataKindsRoundtrips(BrotliEncoderMode mode, string kind)
    {
        byte[] data = ModeData(kind);
        var options = new BrotliCompressionOptions { Quality = 9, WindowLog = 20, Mode = mode };
        byte[] compressed = BrotliEncoder.Compress(data, options);
        byte[] back = Roundtrip(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back), $"mode {mode} kind {kind}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void MaxCompressedLengthBoundHoldsForIncompressibleInputAtEveryQuality(int quality)
    {
        byte[] data = Corpus.Random(50_000, 1000 + quality);
        int bound = BrotliEncoder.GetMaxCompressedLength(data.Length);
        byte[] dst = new byte[bound];
        bool ok = BrotliEncoder.TryCompress(data, dst, out int written, quality, 22);
        Assert.True(ok, $"q{quality}: TryCompress failed with destination sized exactly to GetMaxCompressedLength ({bound})");
        Assert.True(written <= bound, $"q{quality}: wrote {written} > bound {bound}");
        byte[] compressed = dst.AsSpan(0, written).ToArray();
        byte[] back = Roundtrip(compressed, data.Length);
        Assert.True(data.AsSpan().SequenceEqual(back));
    }
}
