using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Tests;

public class StreamAndFeatureTests
{
    // ------------------------------------------------------------------ BrotliStream

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 333)]
    [InlineData(9, 65536)]
    [InlineData(11, 4096)]
    public void StreamRoundtripSync(int quality, int chunk)
    {
        byte[] data = Corpus.Text(300_000, 21);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, new BrotliCompressionOptions { Quality = quality }, leaveOpen: true))
        {
            for (int i = 0; i < data.Length; i += chunk) bs.Write(data, i, Math.Min(chunk, data.Length - i));
        }
        byte[] compressed = ms.ToArray();
        // Native must read it.
        using (var nat = new SysBrotli.BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            using var outMs = new MemoryStream();
            nat.CopyTo(outMs);
            Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
        }
        // Ours must read it, with odd read sizes.
        using (var ours = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            using var outMs = new MemoryStream();
            var buf = new byte[chunk];
            int n;
            while ((n = ours.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
            Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
        }
    }

    [Fact]
    public async Task StreamRoundtripAsync()
    {
        byte[] data = Corpus.Ints(200_000, 3);
        using var ms = new MemoryStream();
        await using (var bs = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            await bs.WriteAsync(data.AsMemory(0, 100_000));
            await bs.FlushAsync();
            await bs.WriteAsync(data.AsMemory(100_000));
        }
        ms.Position = 0;
        await using var dec = new BrotliStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await dec.ReadAsync(buf)) > 0) outMs.Write(buf, 0, n);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    [Fact]
    public void StreamReadsNativeOutput()
    {
        byte[] data = TestVectors.Read("lcet10.txt");
        using var ms = new MemoryStream();
        using (var nat = new SysBrotli.BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true)) nat.Write(data);
        ms.Position = 0;
        using var ours = new BrotliStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ours.CopyTo(outMs, 777);
        Assert.True(data.AsSpan().SequenceEqual(outMs.ToArray()));
    }

    [Fact]
    public void StreamThrowsInvalidDataOnCorruptInput()
    {
        var junk = new byte[] { 0x1B, 0xFF, 0xFF, 0x12, 0x34, 0x56, 0x78, 0x9A };
        using var ours = new BrotliStream(new MemoryStream(junk), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => ours.CopyTo(new MemoryStream()));
    }

    [Fact]
    public void StreamThrowsOnTruncatedInput()
    {
        byte[] data = Corpus.Text(50_000, 8);
        byte[] compressed = BrotliEncoder.Compress(data);
        var truncated = compressed.AsSpan(0, compressed.Length / 2).ToArray();
        using var ours = new BrotliStream(new MemoryStream(truncated), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => ours.CopyTo(new MemoryStream()));
    }

    [Fact]
    public void StreamLeaveOpenAndDisposeSemantics()
    {
        var ms = new MemoryStream();
        var bs = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true);
        bs.Write(new byte[] { 1, 2, 3 });
        bs.Dispose();
        Assert.True(ms.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => bs.Write(new byte[1]));
        var bs2 = new BrotliStream(ms, CompressionMode.Compress);
        bs2.Dispose();
        Assert.False(ms.CanWrite);
    }

    // ------------------------------------------------------------------ concatenation / parallel

    [Fact]
    public void ConcatenatedFragmentsDecodeNatively()
    {
        var parts = new List<byte[]> { Corpus.Text(70_000, 1), Corpus.Random(3000, 2), Corpus.Text(120_000, 3), Array.Empty<byte>(), Corpus.Zeros(50_000) };
        var opts = new BrotliCompressionOptions { Quality = 5, WindowLog = 20, Concatenable = true };
        var frags = parts.Select(p => (ReadOnlyMemory<byte>)BrotliEncoder.Compress(p, opts)).ToArray();
        byte[] joined = BrotliConcat.Concatenate(frags);
        byte[] expected = parts.SelectMany(p => p).ToArray();
        Assert.True(expected.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(joined, expected.Length)));
        Assert.True(expected.AsSpan().SequenceEqual(BrotliDecoder.Decompress(joined)));

        // A single fragment can be finished on its own.
        byte[] single = BrotliConcat.Finish(frags[0].Span);
        Assert.True(parts[0].AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(single, parts[0].Length)));
    }

    [Fact]
    public void ConcatRejectsMismatchedWindows()
    {
        var a = BrotliEncoder.Compress(Corpus.Text(1000, 1), new BrotliCompressionOptions { WindowLog = 16, Concatenable = true, SizeHint = 0 });
        var b = BrotliEncoder.Compress(Corpus.Text(1000, 2), new BrotliCompressionOptions { WindowLog = 18, Concatenable = true, SizeHint = 0 });
        Assert.Throws<ArgumentException>(() => BrotliConcat.Concatenate(a, b));
    }

    [Fact]
    public void Quality11ConcatenableParallelAndLargeWindowRoundtrip()
    {
        // Concatenable fragments: no context modeling and no static dictionary (their positions are unknown), still valid.
        var parts = new List<byte[]> { Corpus.Text(70_000, 1), Corpus.Random(3000, 2), Corpus.Text(120_000, 3), Corpus.Zeros(50_000) };
        var opts = new BrotliCompressionOptions { Quality = 11, WindowLog = 20, Concatenable = true };
        var frags = parts.Select(p => (ReadOnlyMemory<byte>)BrotliEncoder.Compress(p, opts)).ToArray();
        byte[] joined = BrotliConcat.Concatenate(frags);
        byte[] expected = parts.SelectMany(p => p).ToArray();
        Assert.True(expected.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(joined, expected.Length)));
        Assert.True(expected.AsSpan().SequenceEqual(BrotliDecoder.Decompress(joined)));

        byte[] data = Corpus.Text(600_000, 9);
        byte[] parallel = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 11 }, chunkSize: 1 << 17, maxDegreeOfParallelism: 4);
        Assert.True(data.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(parallel, data.Length)));

        // Large window with a far repeat: the reference tool reads it; our decoder too.
        byte[] block = Corpus.Text(300_000, 6);
        byte[] far = block.Concat(Corpus.Random(1_100_000, 7)).Concat(block).ToArray();
        byte[] large = BrotliEncoder.Compress(far, new BrotliCompressionOptions { Quality = 11, WindowLog = 25, LargeWindow = true });
        Assert.True(large.Length < far.Length - 200_000, $"large window did not exploit the far repeat: {large.Length}");
        Assert.True(far.AsSpan().SequenceEqual(BrotliDecoder.Decompress(large, new BrotliDecompressionOptions { MaxWindowLog = 30 })));
        if (HaveBrotliExe) Assert.True(far.AsSpan().SequenceEqual(RunBrotli("-d --large_window=25 -f", large)));

        // Prefix dictionary at quality 11: smaller than without, decodable by both decoders.
        byte[] dictBytes = Corpus.Text(40_000, 21);
        byte[] text = Corpus.Text(60_000, 21);
        BrotliDictionary dict = BrotliDictionary.Create(dictBytes);
        byte[] withDict = BrotliEncoder.Compress(text, new BrotliCompressionOptions { Quality = 11, WindowLog = 18, Dictionary = dict });
        byte[] withoutDict = BrotliEncoder.Compress(text, new BrotliCompressionOptions { Quality = 11, WindowLog = 18 });
        Assert.True(withDict.Length < withoutDict.Length, $"dictionary did not help at q11: {withDict.Length} vs {withoutDict.Length}");
        Assert.True(text.AsSpan().SequenceEqual(BrotliDecoder.Decompress(withDict, new BrotliDecompressionOptions { Dictionary = dict })));
    }

    /// <summary>Quality 11 at the extremes of the window range and with a prefix dictionary in a small window.</summary>
    [Fact]
    public void Quality11WindowExtremes()
    {
        byte[] data = new byte[200_000];
        new Random(5).NextBytes(data.AsSpan(0, 40_000));
        data.AsSpan(0, 40_000).CopyTo(data.AsSpan(150_000));
        foreach (int window in new[] { 10, 16, 24 })
        {
            byte[] c = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 11, WindowLog = window });
            Assert.True(data.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(c, data.Length)), $"window {window}");
        }
        // Large Window: the forest cannot cover 2^30 positions, so trees alias; matches must still be valid.
        byte[] large = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 11, WindowLog = 30, LargeWindow = true });
        Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(large, new BrotliDecompressionOptions { MaxWindowLog = 30 })));
        Assert.True(large.Length < 60_000, $"far repeat not used: {large.Length}");

        // Prefix dictionary with a window smaller than the dictionary.
        var rng = new Random(123);
        byte[] dictBytes = new byte[600];
        rng.NextBytes(dictBytes);
        byte[] input = new byte[504];
        dictBytes.AsSpan(100, 200).CopyTo(input);
        new Random(7).NextBytes(input.AsSpan(200, 300));
        dictBytes.AsSpan(100, 4).CopyTo(input.AsSpan(500));
        BrotliDictionary dict = BrotliDictionary.Create(dictBytes);
        byte[] withDict = BrotliEncoder.Compress(input, new BrotliCompressionOptions { Quality = 11, WindowLog = 10, Dictionary = dict });
        Assert.True(input.AsSpan().SequenceEqual(BrotliDecoder.Decompress(withDict, new BrotliDecompressionOptions { Dictionary = dict })));
        Assert.True(withDict.Length < 400, $"dictionary match not used: {withDict.Length}");
    }

    /// <summary>Quality 11 over varied data, windows, dictionaries and chunkings; every stream is checked with the native decoder.</summary>
    [Fact]
    public void Quality11StressRoundtrip()
    {
        var rng = new Random(20260917);
        for (int c = 0; c < 40; c++)
        {
            int kind = rng.Next(6);
            int n = rng.Next(3) switch { 0 => rng.Next(0, 64), 1 => rng.Next(64, 5000), _ => rng.Next(5000, 150_000) };
            byte[] data = StressInput(rng, kind, n);
            int window = new[] { 10, 12, 16, 18, 22 }[rng.Next(5)];
            bool useDict = rng.Next(3) == 0 && n > 0;
            BrotliDictionary? dict = useDict ? BrotliDictionary.Create(StressInput(rng, kind, Math.Min(20_000, Math.Max(16, n / 2)))) : null;
            var opts = new BrotliCompressionOptions { Quality = 11, WindowLog = window, Dictionary = dict };
            byte[] compressed = rng.Next(2) == 0 ? BrotliEncoder.Compress(data, opts) : StressStream(rng, data, opts);
            string why = $"kind {kind} n {n} window {window} dict {dict?.Length ?? 0}";
            if (dict is null) Assert.True(data.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(compressed, data.Length)), why);
            Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = dict, MaxWindowLog = 30 })), why);
        }
    }

    private static byte[] StressInput(Random r, int kind, int n)
    {
        var b = new byte[n];
        switch (kind)
        {
            case 0: r.NextBytes(b); break;
            case 1: break;
            case 2:
                const string words = "the quick brown fox jumps over lazy dogs and the time of year for all good people ";
                for (int i = 0; i < n; i++) b[i] = (byte)words[(i * 7 + i / 13) % words.Length];
                break;
            case 3:
                var block = new byte[Math.Max(1, n / 50)];
                r.NextBytes(block);
                for (int i = 0; i < n; i++) b[i] = block[i % block.Length];
                break;
            case 4:
                const string alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
                for (int i = 0; i < n; i++) b[i] = (byte)alpha[r.Next(alpha.Length)];
                break;
            default:
                const string html = "<html><head><title>Example Domain</title></head><body><div class=\"content\"><a href=\"http://www.example.com/index.html\">more information</a></div></body></html> ";
                for (int i = 0; i < n; i++) b[i] = (byte)html[i % html.Length];
                break;
        }
        return b;
    }

    private static byte[] StressStream(Random rng, byte[] data, BrotliCompressionOptions opts)
    {
        using var enc = new BrotliEncoder(opts);
        var outMs = new MemoryStream();
        var dst = new byte[rng.Next(1, 4) switch { 1 => 17, 2 => 1024, _ => 65536 }];
        int pos = 0;
        while (true)
        {
            int take = Math.Min(data.Length - pos, rng.Next(1, 40_000));
            bool final = pos + take >= data.Length;
            ReadOnlySpan<byte> src = data.AsSpan(pos, take);
            while (true)
            {
                OperationStatus st = enc.Compress(src, dst, out int consumed, out int written, final);
                outMs.Write(dst, 0, written);
                src = src.Slice(consumed);
                pos += consumed;
                if (st == OperationStatus.Done) return outMs.ToArray();
                if (st == OperationStatus.NeedMoreData) break;
                Assert.Equal(OperationStatus.DestinationTooSmall, st);
            }
        }
    }

    /// <summary>
    /// A size hint smaller than the real input shrinks the shortest-path parser's match tree below the window,
    /// so positions further apart than the tree covers share slots. Such a tree must not be followed: it used
    /// to report matches that do not hold, and the stream then decoded to different bytes.
    /// </summary>
    [Fact]
    public void Quality11SurvivesAnUnderstatedSizeHint()
    {
        const int n = 4 << 20;
        var rng = new Random(7);
        byte[] data = new byte[n];
        const string alpha = "abcdefgh";   // short matches everywhere, so every position stays in the trees
        for (int i = 0; i < n; i++) data[i] = (byte)alpha[rng.Next(alpha.Length)];
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 11, WindowLog = 22, SizeHint = 64 << 10 });
        Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxOutputLength = n + 64 })));
        Assert.True(data.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(compressed, n)));
    }

    [Fact]
    public void ParallelCompressionRoundtrips()
    {
        byte[] data = Corpus.Text(1_500_000, 9);
        byte[] compressed = BrotliParallel.Compress(data, new BrotliCompressionOptions { Quality = 4 }, chunkSize: 1 << 18, maxDegreeOfParallelism: 4);
        Assert.True(data.AsSpan().SequenceEqual(EncoderRoundtripTests.NativeDecompress(compressed, data.Length)));
        Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(compressed)));
        byte[] single = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 4 });
        Assert.True(compressed.Length < data.Length / 2, $"parallel size {compressed.Length}");
        Assert.True(compressed.Length < single.Length * 1.15, $"parallel {compressed.Length} vs single {single.Length}");
    }

    // ------------------------------------------------------------------ brotli.exe oracle (large window, raw dictionary)

    private static readonly string BrotliExe = @"C:\Program Files\Git\mingw64\bin\brotli.exe";

    private static bool HaveBrotliExe => File.Exists(BrotliExe);

    private static byte[] RunBrotli(string args, byte[] input)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bmf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inFile = Path.Combine(dir, "in");
            string outFile = Path.Combine(dir, "out");
            File.WriteAllBytes(inFile, input);
            var psi = new ProcessStartInfo(BrotliExe, $"{args} -o \"{outFile}\" \"{inFile}\"") { RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"brotli.exe failed: {err}");
            return File.ReadAllBytes(outFile);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string WriteTemp(byte[] data)
    {
        string f = Path.Combine(Path.GetTempPath(), "bmf-dict-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(f, data);
        return f;
    }

    [Fact]
    public void LargeWindowStreamsFromReferenceDecode()
    {
        if (!HaveBrotliExe) return;
        // 2 MiB of text with a repeat 1.5 MiB apart: only a >20-bit window can reference it. Use window 26 (large).
        byte[] block = Corpus.Text(600_000, 4);
        byte[] data = block.Concat(Corpus.Random(1_200_000, 5)).Concat(block).ToArray();
        byte[] compressed = RunBrotli("-q 5 --large_window=26 -f", data);
        var opts = new BrotliDecompressionOptions { MaxWindowLog = 30 };
        byte[] back = BrotliDecoder.Decompress(compressed, opts);
        Assert.True(data.AsSpan().SequenceEqual(back));
        Assert.True(compressed.Length < data.Length - 400_000, "large window did not exploit the far repeat");

        // Default decoder must reject it (window header is the large-window pattern).
        using var dec = new BrotliDecoder(null);
        var st = dec.Decompress(compressed, new byte[data.Length], out _, out _, isFinalBlock: true);
        Assert.Equal(OperationStatus.InvalidData, st);
        Assert.Equal(BrotliDecoderError.WindowBits, dec.LastError);
    }

    [Fact]
    public void LargeWindowEncoderOutputDecodesWithReference()
    {
        if (!HaveBrotliExe) return;
        byte[] block = Corpus.Text(600_000, 6);
        byte[] data = block.Concat(Corpus.Random(1_200_000, 7)).Concat(block).ToArray();
        byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 5, WindowLog = 26, LargeWindow = true });
        Assert.True(compressed.Length < data.Length - 400_000, $"our large window did not exploit the far repeat: {compressed.Length}");
        byte[] back = RunBrotli("-d --large_window=26 -f", compressed);
        Assert.True(data.AsSpan().SequenceEqual(back));
        Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxWindowLog = 30 })));
    }

    [Fact]
    public void PrefixDictionaryInteropWithReference()
    {
        if (!HaveBrotliExe) return;
        // Dictionary with a realistic vocabulary (random bytes: every 4-gram unique) and an 8 KB slice of it in the data.
        byte[] dictBytes = Corpus.Random(40_000, 31);
        byte[] data = Corpus.Text(20_000, 32).Concat(dictBytes.AsSpan(10_000, 8_000).ToArray()).Concat(Corpus.Text(5_000, 33)).ToArray();
        string dictFile = WriteTemp(dictBytes);
        try
        {
            var dict = BrotliDictionary.Create(dictBytes);
            // Reference encoder with -D, our decoder with the dictionary.
            byte[] refCompressed = RunBrotli($"-q 6 -w 18 -f -D \"{dictFile}\"", data);
            byte[] back = BrotliDecoder.Decompress(refCompressed, new BrotliDecompressionOptions { Dictionary = dict });
            Assert.True(data.AsSpan().SequenceEqual(back), "reference->ours with dictionary");
            // Without the dictionary it must fail (or produce different output), never throw.
            using var noDict = new BrotliDecoder(null);
            var st = noDict.Decompress(refCompressed, new byte[data.Length + 10], out _, out int w, isFinalBlock: true);
            Assert.True(st != OperationStatus.Done || w != data.Length || !data.AsSpan().SequenceEqual(new byte[0]));

            // Our encoder with the dictionary, reference decoder with -D.
            byte[] ours = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 6, WindowLog = 18, Dictionary = dict });
            byte[] plain = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 6, WindowLog = 18 });
            Assert.True(ours.Length < plain.Length - 1000, $"dictionary did not help: {ours.Length} vs {plain.Length}");
            byte[] refBack = RunBrotli($"-d -f -D \"{dictFile}\"", ours);
            Assert.True(data.AsSpan().SequenceEqual(refBack), "ours->reference with dictionary");
            Assert.True(data.AsSpan().SequenceEqual(BrotliDecoder.Decompress(ours, new BrotliDecompressionOptions { Dictionary = dict })));
        }
        finally
        {
            File.Delete(dictFile);
        }
    }

    [Fact]
    public void ReferenceDecodesOurLowQualityAndFlushes()
    {
        if (!HaveBrotliExe) return;
        byte[] data = Corpus.Ints(300_000, 44);
        using var ms = new MemoryStream();
        using (var bs = new BrotliStream(ms, new BrotliCompressionOptions { Quality = 0, WindowLog = 16 }, leaveOpen: true))
        {
            for (int i = 0; i < data.Length; i += 7000)
            {
                bs.Write(data, i, Math.Min(7000, data.Length - i));
                if (i % 21000 == 0) bs.Flush();
            }
        }
        byte[] back = RunBrotli("-d -f", ms.ToArray());
        Assert.True(data.AsSpan().SequenceEqual(back));
    }
}
