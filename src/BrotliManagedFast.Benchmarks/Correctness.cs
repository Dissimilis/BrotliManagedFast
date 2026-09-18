using System.Buffers;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Benchmarks;

/// <summary>
/// Correctness gate that runs before every benchmark session.
///
/// A decoder that skips a bounds check, or an encoder that emits a shorter but wrong bit pattern, still
/// finishes at full speed, and a faster wrong answer looks exactly like a win. This aborts the run rather
/// than reporting numbers for a broken codec.
///
/// Two anchors: the reference test vector for a literal-heavy stream and the native System.IO.Compression
/// codec, which is libbrotli. Every corpus file round-trips ours-to-native and native-to-ours at the
/// benchmarked qualities, and the chunked incremental path must agree with the one-shot path.
/// </summary>
internal static class Correctness
{
    public static void Run()
    {
        int checks = 0;
        checks += CheckReferenceVector();
        foreach (var (name, data) in BenchData.Corpus)
        {
            foreach (int quality in BenchData.Qualities)
            {
                checks += CheckRoundTrips(name, data, quality);
            }
        }
        Console.WriteLine($"Correctness gate passed: {checks:N0} checks.");
    }

    private static int CheckReferenceVector()
    {
        byte[] compressed = File.ReadAllBytes(Path.Combine(BenchData.Dir, "alice29.txt.compressed"));
        byte[] expected = File.ReadAllBytes(Path.Combine(BenchData.Dir, "alice29.txt"));
        byte[] actual = BrotliDecoder.Decompress(compressed);
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("CORRECTNESS FAILURE: reference vector alice29.txt.compressed did not decode bit-exactly.");
        return 1;
    }

    private static int CheckRoundTrips(string name, byte[] data, int quality)
    {
        int checks = 0;

        // Ours -> native.
        byte[] ours = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = quality, WindowLog = 22, SizeHint = data.Length });
        byte[] nativeBack = NativeDecompress(ours, data.Length, name, quality);
        if (!data.AsSpan().SequenceEqual(nativeBack))
            throw new InvalidOperationException($"CORRECTNESS FAILURE: native decoder disagrees with our encoder on {name} q{quality}.");
        checks++;

        // Native -> ours, one shot.
        byte[] native = NativeCompress(data, quality);
        byte[] oursBack = BrotliDecoder.Decompress(native);
        if (!data.AsSpan().SequenceEqual(oursBack))
            throw new InvalidOperationException($"CORRECTNESS FAILURE: our decoder disagrees with the native encoder on {name} q{quality}.");
        checks++;

        // Native -> ours, awkward chunking through the incremental API.
        using (var dec = new BrotliDecoder(null))
        {
            var output = new byte[data.Length];
            int inPos = 0, outPos = 0;
            OperationStatus status;
            do
            {
                int take = Math.Min(4093, native.Length - inPos);
                status = dec.Decompress(native.AsSpan(inPos, take), output.AsSpan(outPos, Math.Min(7001, output.Length - outPos)), out int consumed, out int written, inPos + take >= native.Length);
                inPos += consumed;
                outPos += written;
                if (status == OperationStatus.InvalidData) throw new InvalidOperationException($"CORRECTNESS FAILURE: chunked decode failed on {name} q{quality}: {dec.LastError}");
            } while (status != OperationStatus.Done);
            if (outPos != data.Length || !data.AsSpan().SequenceEqual(output))
                throw new InvalidOperationException($"CORRECTNESS FAILURE: chunked decode produced wrong bytes on {name} q{quality}.");
        }
        checks++;

        // Ours -> ours.
        byte[] selfBack = BrotliDecoder.Decompress(ours);
        if (!data.AsSpan().SequenceEqual(selfBack))
            throw new InvalidOperationException($"CORRECTNESS FAILURE: self round-trip failed on {name} q{quality}.");
        checks++;

        return checks;
    }

    public static byte[] NativeCompress(byte[] data, int quality)
    {
        using var enc = new SysBrotli.BrotliEncoder(quality, 22);
        var dst = new byte[SysBrotli.BrotliEncoder.GetMaxCompressedLength(data.Length) + data.Length / 8 + 64];
        var status = enc.Compress(data, dst, out int consumed, out int written, isFinalBlock: true);
        if (status != OperationStatus.Done || consumed != data.Length) throw new InvalidOperationException($"native compress failed: {status}");
        return dst.AsSpan(0, written).ToArray();
    }

    private static byte[] NativeDecompress(byte[] compressed, int expectedLength, string name, int quality)
    {
        using var dec = new SysBrotli.BrotliDecoder();
        var dst = new byte[expectedLength + 16];
        var status = dec.Decompress(compressed, dst, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != compressed.Length)
            throw new InvalidOperationException($"CORRECTNESS FAILURE: native decoder rejected our stream for {name} q{quality}: {status}");
        return dst.AsSpan(0, written).ToArray();
    }
}
