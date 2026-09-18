![BrotliManagedFast](https://raw.githubusercontent.com/Dissimilis/BrotliManagedFast/main/img/logo.png)

# BrotliManagedFast

Brotli (RFC 7932) compression and decompression in pure managed C#. No native library, no P/Invoke, no package dependencies on .NET 10. Targets .NET 10 and .NET Standard 2.0, so it works on Blazor WebAssembly, Native AOT, Unity and Mono, where the `BrotliStream` built into .NET is unavailable or has to ship a native binary.

[![NuGet](https://img.shields.io/nuget/v/BrotliManagedFast.svg)](https://www.nuget.org/packages/BrotliManagedFast)
[![NuGet Downloads](https://img.shields.io/nuget/dt/BrotliManagedFast.svg)](https://www.nuget.org/packages/BrotliManagedFast)
[![CI](https://github.com/Dissimilis/BrotliManagedFast/actions/workflows/ci.yml/badge.svg)](https://github.com/Dissimilis/BrotliManagedFast/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-donate-yellow.svg)](https://buymeacoffee.com/dissimilis)

## Features

- Decoder for every RFC 7932 stream; encoder with qualities 0 to 11. Ordinary streams read back with the native .NET decoder and the reference `brotli` tool
- Streaming `OperationStatus` API that works with one-byte buffers, `Flush`, and a `BrotliStream` with sync and async methods
- Large Window streams, raw prefix dictionaries, concatenable fragments and multi-threaded compression
- Output-length and window limits, truncation and trailing-data detection, and a `LastError` value on the incremental API where `BrotliStream` throws
- Pooled buffers, `IBufferWriter<byte>` output, reusable encoder and decoder state

## Performance

Measured at matching quality settings against the native `BrotliStream` in .NET 10, on a corpus of English text, JSON and a binary map tile.

Decompression is faster than native in most of those cases, by the widest margin on the map tile, and faster than BrotliSharpLib throughout.

Compression is smaller than native's at quality 1 on every file, on the text files at quality 4, and on four of the five at quality 11. Quality 9 is the one that is also faster than native. The rest buy their density with time. Quality 4 is the default; 9 is the dense end of the fast range and 11 the smallest output.

The [Building](#building) section has the command to reproduce all of it on your own hardware.

## Installation

```
dotnet add package BrotliManagedFast
```

## Quick start

```csharp
using BrotliManagedFast;

byte[] compressed = BrotliEncoder.Compress(data, new BrotliCompressionOptions { Quality = 6 });
byte[] original = BrotliDecoder.Decompress(compressed);

using (var output = new BrotliStream(File.Create("archive.tar.br"), new BrotliCompressionOptions()))
    input.CopyTo(output);
using (var reader = new BrotliStream(File.OpenRead("archive.tar.br"), new BrotliDecompressionOptions()))
    reader.CopyTo(destination);
```

## Usage

Decoding with limits. A decompression bomb stops at the output limit, a stream needing a larger window is refused, and corrupt input reports why:

```csharp
var options = new BrotliDecompressionOptions
{
    MaxOutputLength = 256 << 20,
    MaxWindowLog = 22,
    RejectTrailingData = true,
};
using var decoder = new BrotliDecoder(options);
OperationStatus status = decoder.Decompress(source, destination, out int consumed, out int written, isFinalBlock: true);
if (status == OperationStatus.InvalidData)
    Console.WriteLine(decoder.LastError);
```

Encoding in pieces. `Flush` emits everything buffered so far, so a decoder reading along can produce every byte given to the encoder; the stream itself ends with the final block:

```csharp
using var encoder = new BrotliEncoder(new BrotliCompressionOptions { Quality = 5, WindowLog = 20 });
encoder.Compress(firstMessage, buffer, out _, out int n1, isFinalBlock: false);
encoder.Flush(buffer.AsSpan(n1), out int n2);
encoder.Compress(lastMessage, buffer.AsSpan(n1 + n2), out _, out int n3, isFinalBlock: true);
```

Prefix dictionary. Both sides start from the same bytes, so data resembling the dictionary compresses smaller. This is the dictionary of `brotli -D`; streams interoperate with the reference tool:

```csharp
BrotliDictionary dictionary = BrotliDictionary.Create(File.ReadAllBytes("common-headers.bin"));
byte[] small = BrotliEncoder.Compress(message, new BrotliCompressionOptions { Dictionary = dictionary });
byte[] back = BrotliDecoder.Decompress(small, new BrotliDecompressionOptions { Dictionary = dictionary });
```

Large Window. Windows above 16 MiB produce Large Window streams, which the reference tool and this library read and the built-in .NET decoder does not:

```csharp
var options = new BrotliCompressionOptions { WindowLog = 26, LargeWindow = true };
byte[] compressed = BrotliEncoder.Compress(hugeInput, options);
byte[] back = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { MaxWindowLog = 30 });
```

Concatenation and multi-threaded compression. Fragments written with `Concatenable` and the same window settings join in any order into one valid stream; `BrotliParallel` compresses chunks on several threads this way:

```csharp
var options = new BrotliCompressionOptions { Quality = 6, WindowLog = 22, Concatenable = true };
byte[] part1 = BrotliEncoder.Compress(a, options);
byte[] part2 = BrotliEncoder.Compress(b, options);
byte[] whole = BrotliConcat.Concatenate(part1, part2);

byte[] fast = BrotliParallel.Compress(largeInput, new BrotliCompressionOptions { Quality = 6 }, chunkSize: 4 << 20, maxDegreeOfParallelism: -1);
```

Command line:

```
dotnet tool install -g BrotliManagedFast.Cli
brotli-managed -q 9 file.tar
brotli-managed -d file.tar.br
```

## API

| Type | Description |
|------|-------------|
| `BrotliEncoder` | Incremental encoder: `Compress`, `Flush`, `Reset`; static `Compress`, `TryCompress`, `GetMaxCompressedLength`. |
| `BrotliDecoder` | Incremental decoder: `Decompress`, `LastError`, `Reset`; static `Decompress`, `TryDecompress`. |
| `BrotliCompressionOptions` | `Quality` (0-11, default 4), `WindowLog` (10-24, up to 30 with `LargeWindow`), `Mode`, `SizeHint`, `Concatenable`, `Dictionary`, `Pool`. |
| `BrotliDecompressionOptions` | `MaxWindowLog`, `MaxOutputLength`, `RejectTrailingData`, `Dictionary`, `Pool`. |
| `BrotliDecoderError` | Why the last result was `InvalidData`. |
| `BrotliDictionary` | Prepared prefix dictionary, immutable and shareable. |
| `BrotliStream` | `Stream` over the codec, sync and async; throws `InvalidDataException` on corrupt input. |
| `BrotliConcat`, `BrotliParallel` | Join concatenable fragments; compress chunks on several threads. |

## Building

```bash
dotnet build BrotliManagedFast.sln -c Release
dotnet test src/BrotliManagedFast.Tests -c Release
dotnet run --project src/BrotliManagedFast.Benchmarks -c Release -- --competitive --report --ratio
dotnet pack src/BrotliManagedFast -c Release
```

Requires the .NET 10 SDK. The tests use the native .NET codec as the oracle.

## Acknowledgments

- [google/brotli](https://github.com/google/brotli), the reference implementation, for the format, the static dictionary and transform tables, and the test vectors (MIT)
- [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) by Jyrki Alakuijala and Zoltán Szabadka
- [BrotliSharpLib](https://github.com/master131/BrotliSharpLib) by master131, the first managed port
