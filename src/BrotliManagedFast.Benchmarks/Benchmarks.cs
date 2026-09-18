using System.Buffers;
using BenchmarkDotNet.Attributes;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Benchmarks;

/// <summary>Corpus shared across the suite: three reference texts, a binary map tile and generated JSON.</summary>
public static class BenchData
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>Qualities benchmarked: 1 (fast web), 4 (the default), 9 (near-best of the everyday range).</summary>
    public static readonly int[] Qualities = { 1, 4, 9, 11 };

    public static readonly (string name, byte[] data)[] Corpus =
    {
        ("alice29.txt", File.ReadAllBytes(Path.Combine(Dir, "alice29.txt"))),
        ("lcet10.txt", File.ReadAllBytes(Path.Combine(Dir, "lcet10.txt"))),
        ("plrabn12.txt", File.ReadAllBytes(Path.Combine(Dir, "plrabn12.txt"))),
        ("mapsdatazrh", File.ReadAllBytes(Path.Combine(Dir, "mapsdatazrh"))),
        ("json-1mb", Json(1 << 20)),
    };

    public static byte[] Get(string name) => Corpus.First(c => c.name == name).data;

    private static byte[] Json(int size)
    {
        var rng = new Random(42);
        var sb = new System.Text.StringBuilder(size + 256);
        sb.Append('[');
        int id = 1000;
        while (sb.Length < size)
        {
            sb.Append($"{{\"id\":{id++},\"name\":\"user{rng.Next(100000)}\",\"active\":{(rng.Next(2) == 0 ? "true" : "false")},\"score\":{rng.NextDouble() * 100:F3},\"tags\":[\"alpha\",\"beta\",\"gamma\"],\"ts\":\"2026-09-16T12:{rng.Next(60):D2}:{rng.Next(60):D2}Z\"}},");
        }
        sb.Length = size - 1;
        sb.Append(']');
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>
/// Where we stand against the native libbrotli behind System.IO.Compression and against BrotliSharpLib,
/// the other managed codec. Decode cases all decode the native encoder's output at that quality, so the
/// three decoders see identical input.
/// </summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    [Params("alice29.txt", "lcet10.txt", "plrabn12.txt", "mapsdatazrh", "json-1mb")]
    public string File = "lcet10.txt";

    [Params(1, 4, 9, 11)]
    public int Quality;

    private byte[] _compressed = Array.Empty<byte>();
    private byte[] _output = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        byte[] data = BenchData.Get(File);
        _compressed = Correctness.NativeCompress(data, Quality);
        _output = new byte[data.Length];
    }

    [Benchmark(Baseline = true, Description = "Native (System.IO.Compression)")]
    public int Native()
    {
        using var dec = new SysBrotli.BrotliDecoder();
        dec.Decompress(_compressed, _output, out _, out int written);
        return written;
    }

    [Benchmark(Description = "BrotliManagedFast (this library)")]
    public int Managed()
    {
        using var dec = new BrotliDecoder(null);
        dec.Decompress(_compressed, _output, out _, out int written, isFinalBlock: true);
        return written;
    }

    [Benchmark(Description = "BrotliSharpLib 0.3.3")]
    public int SharpLib()
    {
        return global::BrotliSharpLib.Brotli.DecompressBuffer(_compressed, 0, _compressed.Length).Length;
    }
}

[MemoryDiagnoser]
public class EncodeBenchmarks
{
    [Params("alice29.txt", "lcet10.txt", "plrabn12.txt", "mapsdatazrh", "json-1mb")]
    public string File = "lcet10.txt";

    [Params(1, 4, 9, 11)]
    public int Quality;

    private byte[] _data = Array.Empty<byte>();
    private byte[] _output = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        _data = BenchData.Get(File);
        _output = new byte[SysBrotli.BrotliEncoder.GetMaxCompressedLength(_data.Length) + _data.Length / 8 + 64];
    }

    [Benchmark(Baseline = true, Description = "Native (System.IO.Compression)")]
    public int Native()
    {
        using var enc = new SysBrotli.BrotliEncoder(Quality, 22);
        enc.Compress(_data, _output, out _, out int written, isFinalBlock: true);
        return written;
    }

    [Benchmark(Description = "BrotliManagedFast (this library)")]
    public int Managed()
    {
        using var enc = new BrotliEncoder(new BrotliCompressionOptions { Quality = Quality, WindowLog = 22, SizeHint = _data.Length });
        enc.Compress(_data, _output, out _, out int written, isFinalBlock: true);
        return written;
    }

    [Benchmark(Description = "BrotliSharpLib 0.3.3")]
    public int SharpLib()
    {
        return global::BrotliSharpLib.Brotli.CompressBuffer(_data, 0, _data.Length, Quality, 22).Length;
    }
}

/// <summary>Compressed size per implementation, the other half of a compressor's story. Not timed.</summary>
public static class RatioReport
{
    public static void Print()
    {
        Console.WriteLine();
        Console.WriteLine("Compressed size as a percentage of the input (window 22), with exact byte counts:");
        Console.WriteLine("a tenth of a percent is 400 bytes on this corpus, so the percentages alone hide");
        Console.WriteLine("differences that matter.");
        Console.WriteLine();
        Console.WriteLine("| File | Quality | Native | BrotliManagedFast | BrotliSharpLib | Ours - native |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (name, data) in BenchData.Corpus)
        {
            foreach (int q in BenchData.Qualities)
            {
                int native = Correctness.NativeCompress(data, q).Length;
                int ours = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = q, WindowLog = 22, SizeHint = data.Length }).Length;
                int sharp = global::BrotliSharpLib.Brotli.CompressBuffer(data, 0, data.Length, q, 22).Length;
                Console.WriteLine($"| {name} | {q} | {100.0 * native / data.Length:F1}% ({native}) | {100.0 * ours / data.Length:F1}% ({ours}) | {100.0 * sharp / data.Length:F1}% ({sharp}) | {ours - native:+#;-#;0} |");
            }
        }
        Console.WriteLine();
    }
}
