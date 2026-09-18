# Security Policy

## Reporting a vulnerability

Open a private security advisory from the repository's Security tab rather than a public issue. Include how to
reproduce it and what the impact is. Fixes go to the latest release.

## What the codec does about hostile input

This implementation has not been independently audited.

A decompressor is a classic attack surface: crafted input can try to make it read out of bounds, allocate
without limit, or produce output without limit. This one:

- Reports malformed input rather than failing on it: the incremental decoder returns `OperationStatus.InvalidData` with a `BrotliDecoderError` reason, and the one-shot helpers and `BrotliStream` throw `InvalidDataException`. The test suite fuzzes mutated streams.
- Bounds the sliding window with `MaxWindowLog`, 16 MiB by default, and rejects streams declaring a larger one before allocating. Total memory is somewhat higher: the ring buffer is padded, pooled buffers may be larger than requested, and the prefix-code tables are extra.
- Bounds output with `BrotliDecompressionOptions.MaxOutputLength`, checked before each metablock is decoded, so a decompression bomb stops at the limit rather than after it.
- Uses unchecked memory access in the hot paths of both the decoder and the encoder, guarded by bounds and format checks made before those paths are entered. Each site says in a comment what keeps it in range.
- Depends on nothing at runtime on .NET 10 (`System.Memory` and `System.Threading.Tasks.Extensions` on `netstandard2.0`).

Brotli is a compression format, not a cryptographic one: it provides no integrity or authenticity. Authenticate compressed data separately when it comes from an untrusted source.
