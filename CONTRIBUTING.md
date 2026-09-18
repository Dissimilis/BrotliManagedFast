# Contributing

Bug reports and pull requests are welcome.

## Building and testing

Requires the .NET 10 SDK (`global.json` pins the version).

```bash
dotnet build BrotliManagedFast.sln -c Release
dotnet test src/BrotliManagedFast.Tests -c Release
```

The tests use the native `System.IO.Compression` Brotli as an oracle in both directions: every stream this
library produces must decode with the native decoder, and every stream the native encoder produces, at every
quality and window size, must decode bit-exactly here. A few interoperability tests also use the `brotli`
command-line binary (shipped with Git for Windows) for Large Window and raw-dictionary streams; they are skipped
when it is absent. A change that makes any of these fail is a bug in the change until proven otherwise.

## Performance changes

Include measurements with performance changes. The benchmark project times this library against the native codec and BrotliSharpLib in
one session:

```bash
dotnet run --project src/BrotliManagedFast.Benchmarks -c Release -- --competitive --report --ratio
```

Absolute times from different sessions are not comparable; only ratios inside one run are, so a performance
change needs a before and an after measured back to back on the same machine, repeated once. Please include
those numbers in the pull request and say which machine produced them. Every benchmark session first runs a
correctness gate; a faster wrong answer is not an improvement.

## Code

- Public API changes need tests for the one-shot, incremental (`OperationStatus`) and `Stream` shapes.
- Format-level changes (anything that touches what bits are written or how bits are read) need a test against the
  reference vectors or the native oracle, not just a self round-trip.
- Comments say why, and cite the RFC section or the measurement when the reason is a number.
- No new runtime dependencies on .NET 10. Build-time packages are fine with `PrivateAssets="all"`.
