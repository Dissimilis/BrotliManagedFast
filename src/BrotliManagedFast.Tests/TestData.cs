using System.Text.RegularExpressions;

namespace BrotliManagedFast.Tests;

/// <summary>Access to the vendored reference test vectors.</summary>
public static class TestVectors
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>All (compressedFile, originalFile) pairs.</summary>
    public static IEnumerable<object[]> Pairs()
    {
        foreach (string file in Directory.GetFiles(Dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            var m = Regex.Match(name, @"^(.*)\.compressed(\.\d+)?$");
            if (!m.Success) continue;
            string original = Path.Combine(Dir, m.Groups[1].Value);
            if (!File.Exists(original)) continue;
            yield return new object[] { name };
        }
    }

    public static (byte[] compressed, byte[] original) Load(string compressedName)
    {
        var m = Regex.Match(compressedName, @"^(.*)\.compressed(\.\d+)?$");
        return (File.ReadAllBytes(Path.Combine(Dir, compressedName)), File.ReadAllBytes(Path.Combine(Dir, m.Groups[1].Value)));
    }

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Dir, name));
}

/// <summary>Deterministic synthetic inputs for roundtrip tests.</summary>
public static class Corpus
{
    public static byte[] Random(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    /// <summary>Compressible pseudo-text: words drawn from a small vocabulary.</summary>
    public static byte[] Text(int length, int seed)
    {
        string[] words = { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "brotli", "compression", "window", "stream", "metablock", "huffman", "context", "distance", "\n" };
        var rng = new Random(seed);
        var sb = new System.Text.StringBuilder(length + 16);
        while (sb.Length < length)
        {
            sb.Append(words[rng.Next(words.Length)]);
            sb.Append(' ');
        }
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    /// <summary>Binary-ish data: little-endian 32-bit integers with slowly varying values.</summary>
    public static byte[] Ints(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        int v = 0;
        for (int i = 0; i + 4 <= length; i += 4)
        {
            v += rng.Next(-5, 6);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i), v);
        }
        return data;
    }

    public static byte[] Zeros(int length) => new byte[length];

    public static IEnumerable<(string name, byte[] data)> Standard()
    {
        yield return ("empty", Array.Empty<byte>());
        yield return ("one", new byte[] { 42 });
        yield return ("two", new byte[] { 1, 2 });
        yield return ("zeros-1k", Zeros(1024));
        yield return ("zeros-100k", Zeros(100_000));
        yield return ("random-1k", Random(1024, 1));
        yield return ("random-64k", Random(65536, 2));
        yield return ("text-1k", Text(1024, 3));
        yield return ("text-100k", Text(100_000, 4));
        yield return ("text-300k", Text(300_000, 5));
        yield return ("ints-64k", Ints(65536, 6));
        yield return ("alice", TestVectors.Read("alice29.txt"));
        yield return ("maps", TestVectors.Read("mapsdatazrh"));
    }
}
