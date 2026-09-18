using System.Diagnostics;
using System.IO.Compression;
using BrotliManagedFast;

namespace BrotliManagedFast.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"brotli-managed: {ex.Message}");
            return 1;
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine(
            """
            Usage: brotli-managed [OPTIONS] [FILE]
              -d, --decompress        decompress instead of compress
              -q, --quality=NUM       compression quality 0-11 (default 4)
              -w, --lgwin=NUM         window size log2, 10-24 (default 22)
                  --large-window=NUM  Large Window Brotli, window log2 10-30 (not RFC 7932 compatible)
              -D, --dictionary=FILE   raw prefix dictionary (both sides must use it)
              -j, --threads=NUM       parallel compression with NUM threads (chunks of --chunk bytes)
                  --chunk=NUM         chunk size for parallel mode (default 4194304)
              -o, --output=FILE       output file (default: FILE.br / FILE without .br; '-' for stdout)
              -c, --stdout            write to stdout
              -f, --force             overwrite output
              -v, --verbose           print size and timing to stderr
              -h, --help              this help
            With no FILE, or when FILE is '-', standard input is used.
            """);
    }

    private static int Run(string[] args)
    {
        bool decompress = false, toStdout = false, force = false, verbose = false;
        int quality = BrotliCompressionOptions.DefaultQuality;
        int window = BrotliCompressionOptions.DefaultWindowLog;
        int largeWindow = 0;
        int threads = 1;
        int chunk = BrotliParallel.DefaultChunkSize;
        string? dictFile = null, output = null, input = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Value(string name)
            {
                int eq = a.IndexOf('=');
                if (eq >= 0) return a.Substring(eq + 1);
                if (i + 1 < args.Length) return args[++i];
                throw new ArgumentException($"Missing value for {name}.");
            }
            switch (a)
            {
                case "-h": case "--help": Usage(); return 0;
                case "-d": case "--decompress": decompress = true; break;
                case "-c": case "--stdout": toStdout = true; break;
                case "-f": case "--force": force = true; break;
                case "-v": case "--verbose": verbose = true; break;
                case "-q": case var _ when a.StartsWith("--quality", StringComparison.Ordinal): quality = int.Parse(Value("quality")!); break;
                case "-w": case var _ when a.StartsWith("--lgwin", StringComparison.Ordinal): window = int.Parse(Value("lgwin")!); break;
                case var _ when a.StartsWith("--large-window", StringComparison.Ordinal): largeWindow = int.Parse(Value("large-window")!); break;
                case "-D": case var _ when a.StartsWith("--dictionary", StringComparison.Ordinal): dictFile = Value("dictionary"); break;
                case "-j": case var _ when a.StartsWith("--threads", StringComparison.Ordinal): threads = int.Parse(Value("threads")!); break;
                case var _ when a.StartsWith("--chunk", StringComparison.Ordinal): chunk = int.Parse(Value("chunk")!); break;
                case "-o": case var _ when a.StartsWith("--output", StringComparison.Ordinal): output = Value("output"); break;
                case "-":
                    input = "-"; break;
                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"Unknown option {a}.");
                    if (input != null) throw new ArgumentException("Only one input file is supported.");
                    input = a;
                    break;
            }
        }

        input ??= "-";
        if (output == null)
        {
            if (toStdout || input == "-") output = "-";
            else if (decompress) output = input.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? input.Substring(0, input.Length - 3) : input + ".out";
            else output = input + ".br";
        }
        if (output != "-" && File.Exists(output) && !force) throw new IOException($"Output file '{output}' exists; use -f to overwrite.");

        BrotliDictionary? dict = dictFile != null ? BrotliDictionary.Create(File.ReadAllBytes(dictFile)) : null;
        var sw = Stopwatch.StartNew();
        long inBytes, outBytes;

        using Stream inStream = input == "-" ? Console.OpenStandardInput() : File.OpenRead(input);
        using Stream outStream = output == "-" ? Console.OpenStandardOutput() : File.Create(output);

        if (decompress)
        {
            var opts = new BrotliDecompressionOptions { Dictionary = dict };
            if (largeWindow != 0) opts.MaxWindowLog = largeWindow;
            using var bs = new BrotliStream(inStream, opts, leaveOpen: true);
            var counting = new CountingStream(outStream);
            bs.CopyTo(counting, 1 << 16);
            inBytes = inStream.CanSeek ? inStream.Length : -1;
            outBytes = counting.Count;
        }
        else
        {
            var opts = new BrotliCompressionOptions { Quality = quality, WindowLog = largeWindow != 0 ? largeWindow : window, LargeWindow = largeWindow != 0, Dictionary = dict };
            if (threads > 1)
            {
                using var msIn = new MemoryStream();
                inStream.CopyTo(msIn);
                byte[] data = msIn.ToArray();
                opts.SizeHint = data.Length;
                byte[] compressed = BrotliParallel.Compress(data, opts, chunk, threads);
                outStream.Write(compressed, 0, compressed.Length);
                inBytes = data.Length;
                outBytes = compressed.Length;
            }
            else
            {
                if (inStream.CanSeek) opts.SizeHint = inStream.Length;
                var counting = new CountingStream(outStream);
                using (var bs = new BrotliStream(counting, opts, leaveOpen: true))
                {
                    inStream.CopyTo(bs, 1 << 16);
                }
                inBytes = inStream.CanSeek ? inStream.Length : -1;
                outBytes = counting.Count;
            }
        }
        sw.Stop();
        if (verbose)
        {
            string ratio = inBytes > 0 && outBytes > 0 ? (decompress ? $"{100.0 * inBytes / outBytes:F1}%" : $"{100.0 * outBytes / inBytes:F1}%") : "?";
            Console.Error.WriteLine($"{(decompress ? "decompressed" : "compressed")} {inBytes} -> {outBytes} bytes ({ratio}) in {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        return 0;
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long Count { get; private set; }
        public CountingStream(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); Count += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { _inner.Write(buffer); Count += buffer.Length; }
    }
}
