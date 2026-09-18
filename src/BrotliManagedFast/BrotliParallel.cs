using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace BrotliManagedFast;

/// <summary>
/// Multi-threaded compression: the input is split into chunks, each chunk is compressed as a concatenable
/// fragment on its own thread, and the fragments are joined. Chunks do not reference each other, so the ratio
/// is slightly worse than single-threaded compression; larger chunks reduce that cost.
/// </summary>
public static class BrotliParallel
{
    /// <summary>Default chunk size (4 MiB).</summary>
    public const int DefaultChunkSize = 1 << 22;

    /// <summary>Compresses <paramref name="source"/> using up to <paramref name="maxDegreeOfParallelism"/> threads.</summary>
    public static byte[] Compress(ReadOnlyMemory<byte> source, BrotliCompressionOptions? options = null, int chunkSize = DefaultChunkSize, int maxDegreeOfParallelism = -1)
    {
        using var w = new PooledBufferWriter(Math.Max(256, source.Length / 2));
        Compress(source, w, options, chunkSize, maxDegreeOfParallelism);
        return w.ToArray();
    }

    /// <summary>Compresses <paramref name="source"/> into <paramref name="output"/> using multiple threads.</summary>
    public static void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> output, BrotliCompressionOptions? options = null, int chunkSize = DefaultChunkSize, int maxDegreeOfParallelism = -1)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (chunkSize < 1 << 16) throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be at least 64 KiB.");
        options = (options ?? BrotliCompressionOptions.Default).Clone();
        options.Validate();
        if (source.Length <= chunkSize || maxDegreeOfParallelism == 1)
        {
            BrotliEncoder.Compress(source.Span, output, options);
            return;
        }
        options.Concatenable = true;
        // Every fragment keeps the stream's window (they must agree), but its tables only need to cover a chunk.
        options.SizeHint = chunkSize;
        int chunks = (int)((source.Length + chunkSize - 1L) / chunkSize);
        var fragments = new ReadOnlyMemory<byte>[chunks];
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism <= 0 ? Environment.ProcessorCount : maxDegreeOfParallelism };
        Parallel.For(0, chunks, po, i =>
        {
            int start = i * chunkSize;
            int len = Math.Min(chunkSize, source.Length - start);
            fragments[i] = BrotliEncoder.Compress(source.Span.Slice(start, len), options);
        });
        BrotliConcat.Concatenate(output, fragments);
    }
}
