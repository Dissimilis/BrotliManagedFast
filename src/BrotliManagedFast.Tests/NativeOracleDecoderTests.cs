using System.Buffers;
using SysBrotli = System.IO.Compression;

namespace BrotliManagedFast.Tests;

/// <summary>Native encoder (System.IO.Compression) produces streams; our decoder must decode them bit-exactly.</summary>
public class NativeOracleDecoderTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var (name, _) in Corpus.Standard())
        {
            foreach (int quality in new[] { 0, 1, 2, 4, 5, 6, 9, 11 })
            {
                foreach (int window in new[] { 10, 16, 22, 24 })
                {
                    yield return new object[] { name, quality, window };
                }
            }
        }
    }

    private static byte[] Data(string name) => Corpus.Standard().First(c => c.name == name).data;

    public static byte[] NativeCompress(byte[] data, int quality, int window)
    {
        using var enc = new SysBrotli.BrotliEncoder(quality, window);
        var dst = new byte[SysBrotli.BrotliEncoder.GetMaxCompressedLength(data.Length) + 64 + data.Length / 8];
        var status = enc.Compress(data, dst, out int consumed, out int written, isFinalBlock: true);
        if (status != OperationStatus.Done || consumed != data.Length) throw new InvalidOperationException($"native compress failed: {status}");
        return dst.AsSpan(0, written).ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesNativeOutput(string name, int quality, int window)
    {
        byte[] data = Data(name);
        byte[] compressed = NativeCompress(data, quality, window);
        byte[] actual = BrotliDecoder.Decompress(compressed);
        Assert.True(data.AsSpan().SequenceEqual(actual), $"mismatch {name} q{quality} w{window}");

        // Also streaming with awkward chunking.
        byte[] chunked = DecodeHelpers.DecodeChunked(compressed, 13, 31, out var status, out var err);
        Assert.True(status == OperationStatus.Done, $"{name} q{quality} w{window}: {status} {err}");
        Assert.True(data.AsSpan().SequenceEqual(chunked));
    }

    [Fact]
    public void DecodesNativeStreamWithFlushes()
    {
        // Native BrotliStream with periodic Flush() produces multiple metablocks + padding blocks.
        byte[] data = Corpus.Text(200_000, 77);
        using var ms = new MemoryStream();
        using (var bs = new SysBrotli.BrotliStream(ms, SysBrotli.CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int i = 0; i < data.Length; i += 1000)
            {
                bs.Write(data, i, Math.Min(1000, data.Length - i));
                bs.Flush();
            }
        }
        byte[] actual = BrotliDecoder.Decompress(ms.ToArray());
        Assert.True(data.AsSpan().SequenceEqual(actual));
    }
}
