using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET7_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
#if !NETSTANDARD2_0
using System.Numerics;
#endif

namespace BrotliManagedFast.Internal;

/// <summary>Per-quality tuning knobs for the match finder.</summary>
internal readonly struct QualityParams
{
    public readonly int HashBits;
    public readonly int ChainDepth;
    public readonly bool Lazy;
    public readonly int NiceLength;   // stop searching when a match this long is found
    public readonly int BlockBits;    // metablock size (log2)
    public readonly int HashStep;     // insert every n-th position during skipped copies (1 = all)
    public readonly int BucketBits;   // bucket hasher (qualities 3+): log2 of the number of buckets
    public readonly int SlotBits;     // bucket hasher: log2 of the positions remembered per bucket
    public readonly int Sweep;        // quick hasher (quality 4): neighbouring keys probed (reference BUCKET_SWEEP)
    public readonly bool Zopfli;      // qualities 10-11: binary-tree hasher, shortest-path parse, block splitting and context modeling
    public readonly int ZopfliLen;    // copy length above which only the longest match is considered
    public readonly int ZopfliCandidates;   // command start positions expanded per position
    public readonly int ZopfliPasses; // shortest-path passes (the second uses the first pass's statistics)

    public QualityParams(int hashBits, int chainDepth, bool lazy, int niceLength, int blockBits, int hashStep, int bucketBits = 0, int slotBits = 0, int sweep = 0,
        bool zopfli = false, int zopfliLen = 0, int zopfliCandidates = 0, int zopfliPasses = 0)
    {
        HashBits = hashBits; ChainDepth = chainDepth; Lazy = lazy; NiceLength = niceLength; BlockBits = blockBits; HashStep = hashStep;
        BucketBits = bucketBits; SlotBits = slotBits; Sweep = sweep;
        Zopfli = zopfli; ZopfliLen = zopfliLen; ZopfliCandidates = zopfliCandidates; ZopfliPasses = zopfliPasses;
    }

    public bool UseBuckets => BucketBits != 0;
    /// <summary>Direct-mapped multi-probe table (the reference's H4), used at quality 4.</summary>
    public bool Quick => Sweep != 0;

    // Qualities 0-2: single-probe greedy parser (6-byte hash). 4: H4-style direct-mapped table probed at four
    // neighbouring keys (6-byte hash, 18 bits). 3 and 5-10: bucket hasher with 2^SlotBits recent positions per
    // bucket (4-byte hash) and lazy matching. Bucket memory is 4 << (BucketBits + SlotBits) bytes.
    public static QualityParams For(int quality) => quality switch
    {
        0 => new QualityParams(15, 1, false, 16, 16, 2),
        1 => new QualityParams(16, 2, false, 24, 18, 1),
        2 => new QualityParams(17, 4, false, 32, 16, 1),
        3 => new QualityParams(17, 8, false, 48, 16, 1, 16, 1),
        4 => new QualityParams(18, 16, true, 64, 16, 1, sweep: 4),
        5 => new QualityParams(17, 32, true, 96, 16, 1, 16, 3),
        6 => new QualityParams(18, 48, true, 128, 16, 1, 17, 3),
        7 => new QualityParams(18, 64, true, 192, 16, 1, 17, 4),
        8 => new QualityParams(18, 128, true, 256, 17, 1, 17, 5),
        9 => new QualityParams(18, 256, true, 512, 18, 1, 15, 8),
        // Qualities 10 and 11 run the shortest-path parser over a binary-tree hasher and split the metablock
        // into blocks with per-context histograms; 10 expands one command start per position and takes the
        // longest match above 150 bytes, 11 expands five and runs a second pass over the first pass's statistics.
        10 => new QualityParams(17, 0, false, 0, 22, 1, zopfli: true, zopfliLen: 150, zopfliCandidates: 1, zopfliPasses: 1),
        _ => new QualityParams(17, 0, false, 0, 22, 1, zopfli: true, zopfliLen: 325, zopfliCandidates: 5, zopfliPasses: 3),
    };
}

/// <summary>One insert-and-copy command produced by the match finder, with its prefix codes precomputed.</summary>
internal struct Command
{
    public int InsertLength;
    /// <summary>Copy length in the low 25 bits; for static-dictionary references the high 7 bits hold (length code - length) in two's complement.</summary>
    public int CopyLength;
    /// <summary>Insert-and-copy command symbol (0..703).</summary>
    public ushort CmdPrefix;
    /// <summary>Distance symbol in the low 10 bits, number of extra bits in the high bits.</summary>
    public ushort DistPrefix;
    /// <summary>Distance extra bits.</summary>
    public uint DistExtra;

    /// <summary>Bytes the command copies.</summary>
    public int CopyLen => CopyLength & 0x1FFFFFF;

    /// <summary>The copy length the length code describes (differs from <see cref="CopyLen"/> only for dictionary words).</summary>
    public int CopyLenCode
    {
        get
        {
            int modifier = (int)((uint)CopyLength >> 25);
            int delta = (sbyte)(byte)(modifier | ((modifier & 0x40) << 1));
            return (CopyLength & 0x1FFFFFF) + delta;
        }
    }
}

/// <summary>
/// Streaming RFC 7932 encoder: accumulates input in a sliding buffer, finds matches with hash chains,
/// and emits one metablock per block (or per flush) with one prefix code per alphabet.
/// </summary>
internal sealed class EncoderCore : IDisposable, IZopfliSink
{

    private const int MinMatch = 4;
    /// <summary>Bytes hashed by the greedy parser (the reference's fast paths hash 5 or 6); the tag check stays 4 bytes.</summary>
    private const int GreedyHashBytes = 6;
    private const ulong GreedyHashMul = 0x1FE35A7BD3579BD3UL; // reference kHashMul64
    /// <summary>Positions inserted per match in the greedy parser; 16 keeps the ratio, 3 (the reference's scheme) loses 0.5-0.9 points with a 4-byte hash.</summary>
    private const int InsertLimit = 8;
    private const int MaxCopyLength = (1 << 24) + 2117; // largest copy length code range (see RFC 7932 5)

    private readonly BrotliCompressionOptions _options;
    private readonly QualityParams _q;
    private readonly int _windowBits;
    private readonly bool _largeWindow;
    private readonly int _blockSize;
    private readonly int _maxBackward;
    private readonly bool _concatenable;
    private readonly ArrayPool<byte> _pool;

    // Sliding input buffer: bytes [_dictLength .. ) are stream data; a prefix dictionary occupies [0, _dictLength).
    private byte[] _buf;
    private int _bufLen;          // bytes currently in _buf (including dictionary prefix)
    private long _bufBase;        // virtual position of _buf[0] (dictionary starts at -dictLength)
    private int _dictLength;
    private long _streamPos;      // virtual position of the next byte to be processed (start of pending block)
    private long _inputEnd;       // virtual position of the end of buffered input
    private long _matchStart;     // virtual position from which matches may be searched (window / fragment start)

    // Qualities 0-2: one head entry per hash. Qualities 3+: buckets of recent positions.
    private int[] _head;
    private readonly int _hashShift;
    private int[] _buckets;
    private ushort[] _bucketNum;
    private readonly int _slotBits;
    private readonly int _slotMask;
    // Prefix dictionary positions get their own tables so stream positions cannot evict them:
    // a head table for the greedy parser and buckets for the bucket hasher.
    private int[] _dictHead;
    private int[] _dictBuckets;
    private ushort[] _dictBucketNum;

    private readonly int[] _distCache = { 4, 11, 15, 16 };
    /// <summary>
    /// The distances the bucket parser tries before searching the hash buckets. A match at one of these costs a
    /// short distance code of a few bits instead of a full one, which is worth more than a slightly longer match
    /// found elsewhere. Sized by <see cref="_numDistCandidates"/>; the reference's PrepareDistanceCache fills the
    /// entries past the fourth with the neighbours of the first two.
    /// </summary>
    private readonly int[] _distCandidates = new int[16];
    private int _numDistCandidates;
    private int _distPushed;

    private readonly int _quality;
    private readonly long _sizeHint;

    // Quality 11
    private readonly ZopfliParser? _zopfli;
    private readonly MetaBlockSplit? _mb;
    private readonly MetaBlockSplit? _greedyMb;

    private Command[] _commands;
    private int _numCommands;

    private readonly BitWriter _writer;
    private uint _pendingByte;
    private int _pendingBits;
    private bool _headerWritten;
    private bool _finished;

    // Output drain
    private byte[]? _out;
    private int _outPos;
    private int _outLen;

    private uint[] _litHisto = new uint[Constants.NumLiteralSymbols];
    private uint[] _litHisto4 = new uint[4 * Constants.NumLiteralSymbols];   // four stripes, merged per block
    private uint[] _cmdHisto = new uint[Constants.NumCommandSymbols];
    private uint[] _distHisto = new uint[Constants.DistanceAlphabetSize(0, 0, Constants.LargeMaxDistanceBits)];
    private uint[] _litCode = new uint[Constants.NumLiteralSymbols];       // (depth << 16) | bits
    private uint[] _cmdCode = new uint[Constants.NumCommandSymbols];
    private uint[] _distCode = new uint[Constants.DistanceAlphabetSize(0, 0, Constants.LargeMaxDistanceBits)];
    private byte[] _litDepth = new byte[Constants.NumLiteralSymbols];
    private ushort[] _litBits = new ushort[Constants.NumLiteralSymbols];
    private byte[] _cmdDepth = new byte[Constants.NumCommandSymbols];
    private ushort[] _cmdBits = new ushort[Constants.NumCommandSymbols];
    private byte[] _distDepth = new byte[Constants.DistanceAlphabetSize(0, 0, Constants.LargeMaxDistanceBits)];
    private ushort[] _distBits = new ushort[Constants.DistanceAlphabetSize(0, 0, Constants.LargeMaxDistanceBits)];

    public EncoderCore(BrotliCompressionOptions options)
    {
        options.Validate();
        _options = options;
        _q = QualityParams.For(options.Quality);
        _quality = options.Quality;
        // Only the four cached distances, and only from quality 5. The reference also tries each one give or
        // take 1, 2 and 3 at the higher qualities, measured here as a further 100 bytes on the corpus and 692 on
        // a JSON sample for about half as much encode time again. Quality 3 keeps two positions per bucket, so
        // four extra probes nearly double its search for a gain it does not need.
        _numDistCandidates = _q.UseBuckets && options.Quality >= 5 ? 4 : 0;
        PrepareDistanceCandidates();
        _sizeHint = options.SizeHint;
        _largeWindow = options.LargeWindow && options.WindowLog > Constants.MaxWindowBits;
        int wbits = options.WindowLog;
        if (options.SizeHint > 0 && !_largeWindow && !options.Concatenable)
        {
            // Shrink the window to the smallest that still covers the whole input (not in concatenable mode: all fragments must agree).
            int needed = Constants.MinWindowBits;
            while (needed < wbits && (1L << needed) - Constants.WindowGap < options.SizeHint) needed++;
            wbits = needed;
        }
        _windowBits = wbits;
        int blockBits = Math.Min(_q.BlockBits, Math.Max(Constants.MinWindowBits, _windowBits));
        _blockSize = 1 << blockBits;
        _maxBackward = (int)Math.Min((1L << _windowBits) - Constants.WindowGap, int.MaxValue / 2);
        _concatenable = options.Concatenable;
        _pool = options.Pool ?? ArrayPool<byte>.Shared;
        // Sized so a compressed block never grows the storage; the pool hands back dirty memory, which the
        // writer tolerates (every store zeroes the bytes ahead of the write position).
        _writer = new BitWriter(_pool, _blockSize + (_blockSize >> 2) + 1024);

        _dictLength = options.Dictionary?.Length ?? 0;
        long windowBytes = Math.Min(1L << _windowBits, 1L << 24);
        long cap = _dictLength + windowBytes + _blockSize * 2L + 64;
        _buf = _pool.Rent((int)Math.Min(cap, int.MaxValue - 64));
        if (_dictLength > 0)
        {
            options.Dictionary!.Span.CopyTo(_buf);
        }
        _bufLen = _dictLength;
        _bufBase = -_dictLength;
        _streamPos = 0;
        _inputEnd = 0;
        _matchStart = -_dictLength;

        if (_q.UseBuckets)
        {
            _head = Array.Empty<int>();
            _hashShift = 32 - _q.BucketBits;
            _slotBits = _q.SlotBits;
            _slotMask = (1 << _q.SlotBits) - 1;
            _buckets = ArrayPool<int>.Shared.Rent(1 << (_q.BucketBits + _q.SlotBits));   // contents never read past _bucketNum
            _bucketNum = ArrayPool<ushort>.Shared.Rent(1 << _q.BucketBits);
            Array.Clear(_bucketNum, 0, 1 << _q.BucketBits);
        }
        else
        {
            _hashShift = 32 - _q.HashBits;
            // Rented (not zeroed) and filled once; a fresh array would be zeroed and then filled.
            _head = ArrayPool<int>.Shared.Rent(1 << _q.HashBits);
            _head.AsSpan(0, 1 << _q.HashBits).Fill(int.MinValue);
            _buckets = Array.Empty<int>();
            _bucketNum = Array.Empty<ushort>();
        }
        _commands = ArrayPool<Command>.Shared.Rent((_blockSize >> 2) + 16);
        if (_q.Zopfli)
        {
            // The match tree covers the window, or the announced input when that is smaller (a concatenable
            // fragment keeps the window of the whole stream but only ever matches inside itself).
            int treeBits = _windowBits;
            if (options.SizeHint > 0)
            {
                long positions = options.SizeHint + _dictLength;
                int hintBits = Constants.MinWindowBits;
                while (hintBits < treeBits && (1L << hintBits) < positions) hintBits++;
                treeBits = hintBits;
            }
            _zopfli = new ZopfliParser(treeBits, Constants.DistanceAlphabetSize(0, 0, _largeWindow ? Constants.LargeMaxDistanceBits : Constants.MaxDistanceBits),
                DistanceParams.Create(0, 0, _largeWindow).MaxDistance, _q.ZopfliLen, _q.ZopfliCandidates, _q.ZopfliPasses);
            _mb = new MetaBlockSplit();
        }
        else if (options.Quality >= 8)
        {
            // One-pass block splitting, at the dense end of the fast tier only. It buys 0.2 to 1.6 percent at
            // every quality but costs 13 to 87 percent of the encode time, which is worth paying only where
            // there is time to spare: qualities 8 and 9 run at a fifth to a half of the native encoder's time,
            // while quality 4, the default, is already at parity with it and ahead of it on ratio.
            _greedyMb = new MetaBlockSplit();
        }
        // Make the prefix dictionary findable: the most recent dictionary position per hash.
        _dictHead = Array.Empty<int>();
        _dictBuckets = Array.Empty<int>();
        _dictBucketNum = Array.Empty<ushort>();
        if (_dictLength >= MinMatch)
        {
            if (_q.UseBuckets)
            {
                _dictBuckets = new int[_buckets.Length];
                _dictBucketNum = new ushort[1 << _q.BucketBits];
                for (long p = -_dictLength; p <= -MinMatch; p++)
                {
                    int h = (int)(Hash4(_buf, Index(p)) >> _hashShift);
                    ushort n = _dictBucketNum[h];
                    _dictBuckets[(h << _slotBits) + (n & _slotMask)] = (int)p;
                    _dictBucketNum[h] = (ushort)(n + 1);
                }
            }
            else
            {
                _dictHead = new int[1 << _q.HashBits];
                for (int k = 0; k < _dictHead.Length; k++) _dictHead[k] = int.MinValue;
                ref byte dictRef = ref MemoryMarshal.GetReference(_buf.AsSpan());
                int shift64 = 64 - _q.HashBits;
                for (long p = -_dictLength; p <= -MinMatch; p++)
                {
                    ulong w = WordAt(ref dictRef, Index(p), _dictLength);
                    _dictHead[_q.Quick ? HashQuick(w, shift64) : HashGreedy(w, shift64)] = (int)p;
                }
            }
        }
    }

    public int WindowBits => _windowBits;
    internal string DebugState => $"bufBase={_bufBase} bufLen={_bufLen} cap={_buf.Length} streamPos={_streamPos} inputEnd={_inputEnd} block={_blockSize} maxBackward={_maxBackward} dict={_dictLength}";
    public bool IsFinished => _finished && _outLen == 0;

    public void Dispose()
    {
        _zopfli?.ReleaseBuffers();
        if (_buf.Length != 0)
        {
            _pool.Return(_buf);
            _buf = Array.Empty<byte>();
        }
        if (_commands.Length != 0)
        {
            ArrayPool<Command>.Shared.Return(_commands);
            _commands = Array.Empty<Command>();
        }
        if (_buckets.Length != 0)
        {
            ArrayPool<int>.Shared.Return(_buckets);
            _buckets = Array.Empty<int>();
        }
        if (_bucketNum.Length != 0)
        {
            ArrayPool<ushort>.Shared.Return(_bucketNum);
            _bucketNum = Array.Empty<ushort>();
        }
        if (_head.Length != 0)
        {
            ArrayPool<int>.Shared.Return(_head);
            _head = Array.Empty<int>();
        }
        _writer.Dispose();
    }

    // ------------------------------------------------------------------ public streaming API

    /// <summary>
    /// Consumes input and produces output. Returns Done when finished and drained, DestinationTooSmall when
    /// output remains, NeedMoreData when all input was consumed and more may follow.
    /// </summary>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock)
    {
        bytesConsumed = 0;
        bytesWritten = 0;
        if (_finished)
        {
            bytesWritten = Drain(destination);
            return _outLen == 0 ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
        }
        for (;;)
        {
            if (_outLen != 0)
            {
                bytesWritten += Drain(destination.Slice(bytesWritten));
                if (_outLen != 0) return OperationStatus.DestinationTooSmall;
            }
            // Take input until a block is full.
            int pending = (int)(_inputEnd - _streamPos);
            int room = _blockSize - pending;
            if (room > 0 && bytesConsumed < source.Length)
            {
                int take = Math.Min(room, source.Length - bytesConsumed);
                Append(source.Slice(bytesConsumed, take));
                bytesConsumed += take;
                pending += take;
            }
            bool sourceDone = bytesConsumed == source.Length;
            if (pending >= _blockSize)
            {
                EmitBlock(isLast: false, flush: false);
                continue;
            }
            if (!sourceDone) continue;
            if (isFinalBlock)
            {
                EmitBlock(isLast: true, flush: false);
                _finished = true;
                bytesWritten += Drain(destination.Slice(bytesWritten));
                return _outLen == 0 ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
            }
            return OperationStatus.NeedMoreData;
        }
    }

    /// <summary>Emits all buffered input as a metablock plus byte-alignment so the decoder can output everything so far.</summary>
    public OperationStatus Flush(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (_finished)
        {
            bytesWritten = Drain(destination);
            return _outLen == 0 ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
        }
        if (_outLen != 0)
        {
            bytesWritten = Drain(destination);
            if (_outLen != 0) return OperationStatus.DestinationTooSmall;
        }
        EmitBlock(isLast: false, flush: true);
        bytesWritten += Drain(destination.Slice(bytesWritten));
        return _outLen == 0 ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
    }

    private int Drain(Span<byte> destination)
    {
        if (_outLen == 0) return 0;
        int n = Math.Min(_outLen, destination.Length);
        _out.AsSpan(_outPos, n).CopyTo(destination);
        _outPos += n;
        _outLen -= n;
        return n;
    }

    // ------------------------------------------------------------------ input buffer

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_bufLen + data.Length > _buf.Length)
        {
            Slide(data.Length);
        }
        data.CopyTo(_buf.AsSpan(_bufLen));
        _bufLen += data.Length;
        _inputEnd += data.Length;
    }

    /// <summary>Drops bytes older than the window from the front of the buffer (the dictionary prefix goes with them).</summary>
    private void Slide(int incoming)
    {
        long keepFrom = Math.Max(_bufBase, _streamPos - _maxBackward);
        int drop = (int)(keepFrom - _bufBase);
        if (drop <= 0)
        {
            GrowBuffer(_bufLen + incoming);
            return;
        }
        int remaining = _bufLen - drop;
        Buffer.BlockCopy(_buf, drop, _buf, 0, remaining);
        _bufLen = remaining;
        _bufBase += drop;
        if (_bufBase >= 0 && _dictHead.Length + _dictBuckets.Length != 0)
        {
            // The dictionary prefix has left the buffer: forget its tables. Their negative positions could
            // otherwise wrap to positive indexes once the base passes 2 GiB.
            _dictHead = Array.Empty<int>();
            _dictBuckets = Array.Empty<int>();
            _dictBucketNum = Array.Empty<ushort>();
        }
        // Positions are stored as int. Rebase every virtual position to the buffer start before the base can
        // approach 2^31 (so stores never truncate), and clear head entries that now point below the buffer:
        // the parsers read live head entries without further checks.
        long shift = _bufBase >= 1L << 30 ? _bufBase : 0;
        if (_head.Length != 0)
        {
            int n = 1 << _q.HashBits;
            for (int i = 0; i < n; i++)
            {
                long e = _head[i];
                _head[i] = e == int.MinValue || e < _bufBase ? int.MinValue : (int)(e - shift);
            }
        }
        if (shift != 0)
        {
            _zopfli?.Hasher.Reset();
            if (_buckets.Length != 0)
            {
                int n = 1 << (_q.BucketBits + _q.SlotBits);
                for (int i = 0; i < n; i++)
                {
                    long e = _buckets[i];
                    _buckets[i] = e < shift ? int.MinValue : (int)(e - shift);
                }
            }
            _streamPos -= shift;
            _inputEnd -= shift;
            _matchStart -= shift;
            _bufBase = 0;
        }
        if (_bufLen + incoming > _buf.Length) GrowBuffer(_bufLen + incoming);
    }

    private void GrowBuffer(int needed)
    {
        byte[] nb = _pool.Rent(Math.Max(needed + 64, _buf.Length * 2));
        Buffer.BlockCopy(_buf, 0, nb, 0, _bufLen);
        _pool.Return(_buf);
        _buf = nb;
    }

    private int Index(long virtualPos) => (int)(virtualPos - _bufBase);

    // ------------------------------------------------------------------ match finding

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Read32(byte[] b, int i)
    {
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(b.AsSpan()), i));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash4(byte[] b, int i) => Read32(b, i) * 0x1E35A7BDu;

    /// <summary>Hash of the low <see cref="GreedyHashBytes"/> bytes of <paramref name="w"/> into 64 - <paramref name="shift64"/> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashGreedy(ulong w, int shift64) => (int)(((w << (64 - 8 * GreedyHashBytes)) * GreedyHashMul) >> shift64);

    /// <summary>Hash of the low 5 bytes of <paramref name="w"/> for the quick table (reference H4 HASH_LEN 5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashQuick(ulong w, int shift64) => (int)(((w << (64 - 8 * 6)) * GreedyHashMul) >> shift64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Read64(ref byte bufRef, int i)
    {
        ulong w = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, i));
        return BitConverter.IsLittleEndian ? w : BinaryPrimitives.ReverseEndianness(w);
    }

    /// <summary>Little-endian 8-byte word at <paramref name="i"/>, or the 4-byte word zero-extended when fewer than 8 bytes remain before <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WordAt(ref byte bufRef, int i, int limit)
    {
        if (i + 8 <= limit)
        {
            ulong w = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, i));
            return BitConverter.IsLittleEndian ? w : BinaryPrimitives.ReverseEndianness(w);
        }
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bufRef, i));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertBucket(int h, long vpos)
    {
        ushort n = _bucketNum[h];
        _buckets[(h << _slotBits) + (n & _slotMask)] = (int)vpos;
        _bucketNum[h] = (ushort)(n + 1);
    }

    /// <summary>
    /// Greedy parser for the fast qualities: one hash-table probe per position, 4-byte tag check, no chains,
    /// no lazy matching, and skipping acceleration through incompressible stretches.
    /// </summary>
    private void FindCommandsGreedy(long end)
    {
        _numCommands = 0;
        long pos = _streamPos;
        long insertStart = pos;
        long bufBase = _bufBase;
        byte[] buf = _buf;
        ref byte bufRef = ref MemoryMarshal.GetReference(buf.AsSpan());
        ref int head = ref MemoryMarshal.GetReference(_head.AsSpan());
        int shift = _hashShift;
        long minCand = Math.Max(_matchStart, 0);
        long maxBackward = _maxBackward;
        bool haveDict = _dictHead.Length != 0;
        int shift64 = 64 - _q.HashBits;
        int endIdx = (int)(end - bufBase);
        int interiorEnd = endIdx - 8;     // idx <= interiorEnd: eight readable bytes, one unaligned load
        int skip = 32;
        int idx = (int)(pos - bufBase);
        long c;
        int ci;
        // Interior loop. The word of the next probe position is loaded before the candidate is checked, so
        // the table load of the next iteration does not wait on this iteration's compare (reference trawl loop).
        if (idx <= interiorEnd)
        {
            ulong w = Read64(ref bufRef, idx);
            int h = HashGreedy(w, shift64);
            for (;;)
            {
                uint cur = (uint)w;
                ref int slot = ref Unsafe.Add(ref head, h);
                int cand = slot;
                slot = (int)pos;
                int nextIdx = idx + (skip++ >> 5);
                ulong wNext = 0;
                int hNext = 0;
                if (nextIdx <= interiorEnd)
                {
                    // Hash the next probe now and touch its slot, so the table load of the next iteration is
                    // in flight while this candidate is compared.
                    wNext = Read64(ref bufRef, nextIdx);
                    hNext = HashGreedy(wNext, shift64);
#if NET7_0_OR_GREATER
                    if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
                    {
                        unsafe { System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref head, hNext))); }
                    }
#endif
                }
                // 1. The table candidate. Live entries are stream positions inside the buffer (Slide clears the
                //    rest), so the only remaining condition is the window distance, tested after the tag matched.
                c = cand;
                if (cand != int.MinValue)
                {
                    ci = (int)(c - bufBase);
                    uint tag = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bufRef, ci));
                    if (!BitConverter.IsLittleEndian) tag = BinaryPrimitives.ReverseEndianness(tag);
                    if (tag == cur && pos - c <= maxBackward) goto Match;
                }
                // 2. The prefix dictionary.
                if (haveDict)
                {
                    int dc = _dictHead[h];
                    if (dc != int.MinValue && Index(dc) >= 0 && Read32(buf, Index(dc)) == cur)
                    {
                        c = dc;
                        ci = Index(dc);
                        goto Match;
                    }
                }
                // Miss: the reference ramp, one byte per lookup for 32 lookups, then two, and so on.
                if (nextIdx > interiorEnd) { idx = nextIdx; pos = idx + bufBase; break; }
                idx = nextIdx;
                pos = idx + bufBase;
                w = wNext;
                h = hNext;
                continue;
            Match:
                {
                    int maxLen = (int)Math.Min(end - pos, MaxCopyLength);
                    if (c < 0) maxLen = (int)Math.Min(maxLen, -c);   // a dictionary match cannot run past the dictionary
                    int len = MinMatch + CommonLength(ci + MinMatch, idx + MinMatch, maxLen - MinMatch);
                    int dist = DistanceFor(pos, c);
                    AddCommand((int)(insertStart - bufBase), (int)(pos - insertStart), len, dist);
                    skip = 32;
                    // Insert the first positions of the match (bounded): the next iteration probes pos + len
                    // directly, so a continuing repeat costs one lookup.
                    int limit = Math.Min(len, InsertLimit);
                    int k = 1;
                    int fastLimit = Math.Min(limit, interiorEnd - idx + 1);   // idx + k <= interiorEnd
                    for (; k < fastLimit; k++)
                    {
                        Unsafe.Add(ref head, HashGreedy(Read64(ref bufRef, idx + k), shift64)) = (int)(pos + k);
                    }
                    for (; k < limit; k++)
                    {
                        if (idx + k + MinMatch <= endIdx) Unsafe.Add(ref head, HashGreedy(WordAt(ref bufRef, idx + k, endIdx), shift64)) = (int)(pos + k);
                    }
                    pos += len;
                    idx += len;
                    insertStart = pos;
                    if (idx > interiorEnd) break;
                    w = Read64(ref bufRef, idx);
                    h = HashGreedy(w, shift64);
                }
            }
        }
        // Tail: fewer than eight bytes left at the probe position.
        while (pos + MinMatch <= end)
        {
            idx = (int)(pos - bufBase);
            ulong w = WordAt(ref bufRef, idx, endIdx);
            uint cur = (uint)w;
            int h = HashGreedy(w, shift64);
            ref int slot = ref Unsafe.Add(ref head, h);
            int cand = slot;
            slot = (int)pos;
            c = cand;
            if (cand != int.MinValue)
            {
                ci = (int)(c - bufBase);
                uint tag = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref bufRef, ci));
                if (!BitConverter.IsLittleEndian) tag = BinaryPrimitives.ReverseEndianness(tag);
                if (tag == cur && pos - c <= maxBackward) goto TailMatch;
            }
            if (haveDict)
            {
                int dc = _dictHead[h];
                if (dc != int.MinValue && Index(dc) >= 0 && Read32(buf, Index(dc)) == cur)
                {
                    c = dc;
                    ci = Index(dc);
                    goto TailMatch;
                }
            }
            pos += skip++ >> 5;
            continue;
        TailMatch:
            {
                int maxLen = (int)Math.Min(end - pos, MaxCopyLength);
                if (c < 0) maxLen = (int)Math.Min(maxLen, -c);
                int len = MinMatch + CommonLength(ci + MinMatch, idx + MinMatch, maxLen - MinMatch);
                AddCommand((int)(insertStart - bufBase), (int)(pos - insertStart), len, DistanceFor(pos, c));
                skip = 32;
                int limit = Math.Min(len, InsertLimit);
                for (int k = 1; k < limit; k++)
                {
                    if (idx + k + MinMatch <= endIdx) Unsafe.Add(ref head, HashGreedy(WordAt(ref bufRef, idx + k, endIdx), shift64)) = (int)(pos + k);
                }
                pos += len;
                insertStart = pos;
            }
        }
        if (insertStart < end)
        {
            AddCommand((int)(insertStart - bufBase), (int)(end - insertStart), 0, 0);
        }
    }

    /// <summary>Length of the common prefix of buf[a..] and buf[b..], at most <paramref name="max"/>; 8 bytes per step.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CommonLength(int a, int b, int max)
    {
        ref byte x = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buf.AsSpan()), a);
        ref byte y = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buf.AsSpan()), b);
        int i = 0;
        int limit8 = max - 8;
#if NET7_0_OR_GREATER
        // Most matches end within the first eight bytes; longer ones are compared 32 bytes at a time.
        if (Vector256.IsHardwareAccelerated && i <= limit8)
        {
            ulong d0 = Unsafe.ReadUnaligned<ulong>(ref x) ^ Unsafe.ReadUnaligned<ulong>(ref y);
            if (d0 != 0) return BitConverter.IsLittleEndian ? TrailingZeroCount(d0) >> 3 : LeadingZeroCount(d0) >> 3;
            i = 8;
            while (i + 32 <= max)
            {
                Vector256<byte> vx = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref x, i));
                Vector256<byte> vy = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref y, i));
                uint eq = Vector256.Equals(vx, vy).ExtractMostSignificantBits();
                if (eq != uint.MaxValue) return i + BitOperations.TrailingZeroCount(~eq);
                i += 32;
            }
        }
        else if (Vector128.IsHardwareAccelerated && i <= limit8)
        {
            // ARM64 (AdvSimd): 16 bytes per step; a mismatch is located with two 8-byte lanes, since
            // ExtractMostSignificantBits is not a single instruction there.
            ulong d0 = Unsafe.ReadUnaligned<ulong>(ref x) ^ Unsafe.ReadUnaligned<ulong>(ref y);
            if (d0 != 0) return BitConverter.IsLittleEndian ? TrailingZeroCount(d0) >> 3 : LeadingZeroCount(d0) >> 3;
            i = 8;
            while (i + 16 <= max)
            {
                Vector128<byte> d = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref x, i)) ^ Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref y, i));
                if (d != Vector128<byte>.Zero)
                {
                    ulong lo = d.AsUInt64().GetElement(0);
                    if (lo != 0) return i + (BitConverter.IsLittleEndian ? TrailingZeroCount(lo) >> 3 : LeadingZeroCount(lo) >> 3);
                    ulong hi = d.AsUInt64().GetElement(1);
                    return i + 8 + (BitConverter.IsLittleEndian ? TrailingZeroCount(hi) >> 3 : LeadingZeroCount(hi) >> 3);
                }
                i += 16;
            }
        }
#endif
        while (i <= limit8)
        {
            ulong diff = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, i)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, i));
            if (diff != 0)
            {
                int matched = BitConverter.IsLittleEndian ? TrailingZeroCount(diff) >> 3 : LeadingZeroCount(diff) >> 3;
                return i + matched;
            }
            i += 8;
        }
        while (i < max && Unsafe.Add(ref x, i) == Unsafe.Add(ref y, i)) i++;
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TrailingZeroCount(ulong v)
    {
#if NETSTANDARD2_0
        int n = 0;
        while ((v & 1) == 0) { v >>= 1; n++; }
        return n;
#else
        return BitOperations.TrailingZeroCount(v);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeroCount(ulong v)
    {
#if NETSTANDARD2_0
        int n = 0;
        while ((v & 0x8000000000000000UL) == 0) { v <<= 1; n++; }
        return n;
#else
        return BitOperations.LeadingZeroCount(v);
#endif
    }

    /// <summary>Distance of virtual candidate position <paramref name="cand"/> as seen by the decoder at <paramref name="vpos"/>.</summary>
    private int DistanceFor(long vpos, long cand)
    {
        if (cand >= 0) return (int)(vpos - cand);
        // Dictionary byte: the prefix dictionary floats at max_distance = min(pos, window - 16).
        long maxDistance = Math.Min(vpos, _maxBackward);
        return (int)(maxDistance - cand);
    }

    private bool FindMatch(long vpos, int maxLen, out int bestLen, out int bestDist, out int bestScore, out int hash)
    {
        bestLen = 0;
        bestDist = 0;
        bestScore = MinScore;
        hash = -1;
        if (maxLen < MinMatch) return false;
        byte[] buf = _buf;
        int idx = Index(vpos);
        uint cur = Read32(buf, idx);
        int h = (int)((cur * 0x1E35A7BDu) >> _hashShift);
        hash = h;
        long minCand = vpos - _maxBackward;
        ref byte bufRef0 = ref MemoryMarshal.GetReference(buf.AsSpan());
        // The cached distances first: they code in a few bits, so they win over longer matches found by hash.
        for (int i = 0; i < _numDistCandidates; ++i)
        {
            int backward = _distCandidates[i];
            long prev = vpos - backward;
            if (backward <= 0 || prev < 0 || prev >= vpos || backward > _maxBackward) continue;
            int pi = Index(prev);
            if (pi < 0) continue;
            // A match that already reaches the end of the buffer cannot be beaten, and the byte-at-bestLen
            // test below would read one past the last valid byte.
            if (bestLen >= maxLen) break;
            if (Unsafe.Add(ref bufRef0, pi + bestLen) != Unsafe.Add(ref bufRef0, idx + bestLen)) continue;
            if (Read32(buf, pi) != cur) continue;
            int clen = MinMatch + CommonLength(pi + MinMatch, idx + MinMatch, maxLen - MinMatch);
            // No distance-bit penalty here, plus the reference's bonus, minus a penalty for the later slots.
            int cscore = 135 * clen + 15;
            if (i != 0) cscore -= 39 + ((0x1CA10 >> (i & 0xE)) & 0xE);
            if (cscore > bestScore)
            {
                bestScore = cscore;
                bestLen = clen;
                bestDist = backward;
            }
        }
        int n = _bucketNum[h];
        int slots = Math.Min(n, 1 << _slotBits);
        ref int bucket = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buckets.AsSpan()), h << _slotBits);
        ref byte bufRef = ref bufRef0;
        // Newest-first: the byte-at-bestLen filter below relies on the nearest equal-length candidate coming first.
        for (int k = 1; k <= slots; k++)
        {
            long c = Unsafe.Add(ref bucket, (n - k) & _slotMask);
            if (c >= vpos) continue;
            if (c < minCand && c >= 0) break;          // entries only get older from here
            int ci = Index(c);
            if (ci < 0) break;
            if (bestLen >= maxLen) break;
            if (Unsafe.Add(ref bufRef, ci + bestLen) != Unsafe.Add(ref bufRef, idx + bestLen)) continue;
            if (Read32(buf, ci) != cur) continue;
            int len = MinMatch + CommonLength(ci + MinMatch, idx + MinMatch, maxLen - MinMatch);
            int distance = DistanceFor(vpos, c);
            if (distance <= 0 || distance > Constants.MaxAllowedDistance) continue;
            int score = Score(len, distance);
            if (score > bestScore)
            {
                bestScore = score;
                bestLen = len;
                bestDist = distance;
                if (len >= _q.NiceLength) break;
            }
        }
        if (_dictBuckets.Length != 0) ProbeDictionary(h, idx, cur, maxLen, vpos, ref bestLen, ref bestDist, ref bestScore);
        return bestLen != 0;
    }

    /// <summary>Sweeps the prefix-dictionary bucket for hash <paramref name="h"/>.</summary>
    private void ProbeDictionary(int h, int idx, uint cur, int maxLen, long vpos, ref int bestLen, ref int bestDist, ref int bestScore)
    {
        byte[] buf = _buf;
        int n = _dictBucketNum[h];
        int slots = Math.Min(n, 1 << _slotBits);
        int bucketBase = h << _slotBits;
        for (int k = 1; k <= slots; k++)
        {
            int cand = _dictBuckets[bucketBase + ((n - k) & _slotMask)];
            int ci = Index(cand);
            if (ci < 0) return;
            int dictMax = Math.Min(maxLen, -cand);   // a dictionary match cannot run past the dictionary
            if (bestLen >= dictMax) continue;
            if (Read32(buf, ci) != cur) continue;
            if (buf[ci + bestLen] != buf[idx + bestLen]) continue;
            int len = MinMatch + CommonLength(ci + MinMatch, idx + MinMatch, dictMax - MinMatch);
            int distance = DistanceFor(vpos, cand);
            if (distance <= 0 || distance > Constants.MaxAllowedDistance) continue;
            int score = Score(len, distance);
            if (score > bestScore)
            {
                bestScore = score;
                bestLen = len;
                bestDist = distance;
                if (len >= _q.NiceLength) return;
            }
        }
    }

    /// <summary>Reference scoring: 135 per matched byte, 30 per distance bit (hash.h BackwardReferenceScore).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Score(int len, int distance) => 135 * len - 30 * BitOperations.Log2((uint)distance);

    /// <summary>Matches must score above this to be taken (hash.h kMinScore, relative to the score base): a
    /// 4-byte match further than about 2^14 bytes costs more than its literals.</summary>
    private const int MinScore = 100;


    /// <summary>Returns the short distance code for <paramref name="distance"/> if one applies (0..15), else -1.</summary>
    private int ShortCode(int distance)
    {
        int usable = _concatenable ? Math.Min(_distPushed, 4) : 4;
        int[] c = _distCache;
        if (usable >= 1 && distance == c[0]) return 0;
        if (usable >= 2 && distance == c[1]) return 1;
        if (usable >= 3 && distance == c[2]) return 2;
        if (usable >= 4 && distance == c[3]) return 3;
        if (usable >= 1)
        {
            int d = distance - c[0];
            if (d == -1) return 4;
            if (d == 1) return 5;
            if (d == -2) return 6;
            if (d == 2) return 7;
            if (d == -3) return 8;
            if (d == 3) return 9;
        }
        if (usable >= 2)
        {
            int d = distance - c[1];
            if (d == -1) return 10;
            if (d == 1) return 11;
            if (d == -2) return 12;
            if (d == 2) return 13;
            if (d == -3) return 14;
            if (d == 3) return 15;
        }
        return -1;
    }

    private void PushDistance(int distance)
    {
        _distCache[3] = _distCache[2];
        _distCache[2] = _distCache[1];
        _distCache[1] = _distCache[0];
        _distCache[0] = distance;
        _distPushed++;
        PrepareDistanceCandidates();
    }

    /// <summary>Port of PrepareDistanceCache: the four cached distances, then the near neighbours of the first two.</summary>
    private void PrepareDistanceCandidates()
    {
        int n = _numDistCandidates;
        if (n == 0) return;
        int[] c = _distCandidates;
        c[0] = _distCache[0]; c[1] = _distCache[1]; c[2] = _distCache[2]; c[3] = _distCache[3];
        if (n > 4)
        {
            int last = c[0];
            c[4] = last - 1; c[5] = last + 1;
            c[6] = last - 2; c[7] = last + 2;
            c[8] = last - 3; c[9] = last + 3;
            if (n > 10)
            {
                int prev = c[1];
                c[10] = prev - 1; c[11] = prev + 1;
                c[12] = prev - 2; c[13] = prev + 2;
                c[14] = prev - 3; c[15] = prev + 3;
            }
        }
    }

    /// <summary>Records a command and counts its symbols; <paramref name="literalIdx"/> is the buffer index of the literal run.</summary>
    private void AddCommand(int literalIdx, int insertLength, int copyLength, int distance)
    {
        if (_numCommands == _commands.Length) Array.Resize(ref _commands, _commands.Length * 2);
        if (insertLength != 0)
        {
            ref byte lit = ref Unsafe.Add(ref MemoryMarshal.GetReference(_buf.AsSpan()), literalIdx);
            ref uint h0 = ref MemoryMarshal.GetReference(_litHisto4.AsSpan());
            ref uint h1 = ref Unsafe.Add(ref h0, 256);
            ref uint h2 = ref Unsafe.Add(ref h0, 512);
            ref uint h3 = ref Unsafe.Add(ref h0, 768);
            int j = 0;
            for (; j + 4 <= insertLength; j += 4)
            {
                Unsafe.Add(ref h0, Unsafe.Add(ref lit, j))++;
                Unsafe.Add(ref h1, Unsafe.Add(ref lit, j + 1))++;
                Unsafe.Add(ref h2, Unsafe.Add(ref lit, j + 2))++;
                Unsafe.Add(ref h3, Unsafe.Add(ref lit, j + 3))++;
            }
            for (; j < insertLength; j++) Unsafe.Add(ref h0, Unsafe.Add(ref lit, j))++;
        }
        int code;
        if (copyLength == 0)
        {
            code = 0; // unused
        }
        else
        {
            int sc = ShortCode(distance);
            if (sc >= 0)
            {
                code = sc;
                if (sc != 0) PushDistance(distance);
            }
            else
            {
                code = distance + Constants.NumDistanceShortCodes - 1;
                PushDistance(distance);
            }
        }
        int copyCode;
        bool implicitDistance = false;
        ushort distPrefix = 0;
        uint distExtra = 0;
        if (copyLength == 0)
        {
            copyCode = GetCopyLengthCode(4);
        }
        else
        {
            copyCode = GetCopyLengthCode(copyLength);
            implicitDistance = code == 0;
            PrefixEncodeCopyDistance(code, out int dcode, out int nExtra, out distExtra);
            distPrefix = (ushort)(dcode | (nExtra << 10));
        }
        int cmdCode = CombineLengthCodes(GetInsertLengthCode(insertLength), copyCode, implicitDistance);
        _cmdHisto[cmdCode]++;
        if (copyLength != 0 && cmdCode >= 128) _distHisto[distPrefix & 0x3FF]++;
        _commands[_numCommands++] = new Command
        {
            InsertLength = insertLength, CopyLength = copyLength,
            CmdPrefix = (ushort)cmdCode, DistPrefix = distPrefix, DistExtra = distExtra,
        };
    }

    /// <summary>Parses [_streamPos, end) into commands with the bucket hasher and lazy matching.</summary>
    private void FindCommands(long end)
    {
        _numCommands = 0;
        long pos = _streamPos;
        long insertStart = pos;
        bool lazy = _q.Lazy;
        int nice = _q.NiceLength;
        // Reference random-data heuristics: after a spree of misses, probe every second byte, then every fourth.
        int spree = _q.NiceLength >= 512 ? 512 : 64;   // LiteralSpreeLengthForSparseSearch: 512 for quality 9+
        long applyRandom = pos + spree;
        while (pos < end)
        {
            int maxLen = (int)Math.Min(end - pos, MaxCopyLength);
            if (!FindMatch(pos, maxLen, out int len, out int dist, out int score, out int h))
            {
                if (h >= 0) InsertBucket(h, pos);
                pos++;
                if (pos > applyRandom)
                {
                    int step = pos > applyRandom + 4 * spree ? 4 : 2;
                    long jump = Math.Min(pos + 4 * step, end - MinMatch);
                    for (; pos < jump; pos += step) InsertBucket((int)(Hash4(_buf, Index(pos)) >> _hashShift), pos);
                }
                continue;
            }
            InsertBucket(h, pos);
            if (lazy && len < 16 && pos + 1 < end)
            {
                int maxLen2 = (int)Math.Min(end - pos - 1, MaxCopyLength);
                if (FindMatch(pos + 1, maxLen2, out int len2, out int dist2, out int score2, out int h2) && score2 >= score + 175)
                {
                    // Emit the current byte as a literal and take the better match at pos + 1.
                    InsertBucket(h2, pos + 1);
                    pos++;
                    len = len2;
                    dist = dist2;
                }
            }
            applyRandom = pos + 2 * len + spree;
            AddCommand(Index(insertStart), (int)(pos - insertStart), len, dist);
            // Hash the covered positions so later repeats of the match are found (bounded for long matches).
            int limit = Math.Min(len, nice);
            for (int k = 1; k < limit; k++)
            {
                long p = pos + k;
                if (p + MinMatch <= end) InsertBucket((int)(Hash4(_buf, Index(p)) >> _hashShift), p);
            }
            pos += len;
            insertStart = pos;
        }
        if (insertStart < end)
        {
            AddCommand(Index(insertStart), (int)(end - insertStart), 0, 0);
        }
    }

    /// <summary>
    /// Quality 4 parser modelled on the reference's H4 hasher: a direct-mapped table probed at four neighbouring
    /// keys (6-byte hash), the last distance probed first, one-step lazy matching up to four times, every
    /// position of a match stored, and the reference scoring with its acceptance threshold. Every invariant
    /// lives in a local; the search is inlined.
    /// </summary>
    private void FindCommandsQuick(long end)
    {
        _numCommands = 0;
        long pos = _streamPos;
        long insertStart = pos;
        long bufBase = _bufBase;
        int endIdx = (int)(end - bufBase);
        ref byte bufRef = ref MemoryMarshal.GetReference(_buf.AsSpan());
        ref int head = ref MemoryMarshal.GetReference(_head.AsSpan());
        int mask = (1 << _q.HashBits) - 1;
        int shift64 = 64 - _q.HashBits;
        long minCand = Math.Max(_matchStart, 0);
        long maxBackward = _maxBackward;
        bool haveDict = _dictHead.Length != 0;
        int lastDist = _distCache[0];
        long applyRandom = pos + 64;      // reference LiteralSpreeLengthForSparseSearch for quality < 9
        while (pos + MinMatch <= end)
        {
            int maxLen = (int)(end - pos);   // blocks are far below MaxCopyLength
            QuickSearch(ref bufRef, ref head, mask, shift64, minCand, maxBackward, bufBase, haveDict, lastDist, pos, maxLen, 0, endIdx, out int len, out int dist, out int score);
            if (score > MinScore)
            {
                for (int delayed = 0; delayed < 4 && pos + 1 + MinMatch <= end; delayed++)
                {
                    int maxLen2 = maxLen - 1;
                    QuickSearch(ref bufRef, ref head, mask, shift64, minCand, maxBackward, bufBase, haveDict, lastDist, pos + 1, maxLen2, Math.Min(len - 1, maxLen2), endIdx, out int len2, out int dist2, out int score2);
                    if (score2 < score + 175) break;
                    // One literal now, the better match from the next byte.
                    pos++;
                    maxLen = maxLen2;
                    len = len2;
                    dist = dist2;
                    score = score2;
                }
                applyRandom = pos + 2 * len + 64;
                AddCommand((int)(insertStart - bufBase), (int)(pos - insertStart), len, dist);
                lastDist = dist;
                // Store the covered positions (from +2; +0 and +1 were stored by the searches), skipping the
                // start of a run shorter than the match so RLE data does not poison the table.
                long rangeStart = pos + 2;
                long rangeEnd = Math.Min(pos + len, end - MinMatch + 1);
                if (dist < (len >> 2)) rangeStart = Math.Min(rangeEnd, Math.Max(rangeStart, pos + len - ((long)dist << 2)));
                for (long q = rangeStart; q < rangeEnd; q++)
                {
                    int key = HashQuick(WordAt(ref bufRef, (int)(q - bufBase), endIdx), shift64);
                    Unsafe.Add(ref head, (key + ((int)q & 24)) & mask) = (int)q;
                }
                pos += len;
                insertStart = pos;
            }
            else
            {
                pos++;
                if (pos > applyRandom)
                {
                    // Sparse search through a literal spree (reference random-data heuristics): every second
                    // byte after 64 misses, every fourth after 320, storing the visited positions.
                    int step = pos > applyRandom + 4 * 64 ? 4 : 2;
                    long jump = Math.Min(pos + 4 * step, end - MinMatch);
                    for (; pos < jump; pos += step)
                    {
                        int key = HashQuick(WordAt(ref bufRef, (int)(pos - bufBase), endIdx), shift64);
                        Unsafe.Add(ref head, (key + ((int)pos & 24)) & mask) = (int)pos;
                    }
                }
            }
        }
        if (insertStart < end)
        {
            AddCommand((int)(insertStart - bufBase), (int)(end - insertStart), 0, 0);
        }
    }

    /// <summary>
    /// One search of the quick table at <paramref name="vpos"/>; only matches longer than <paramref name="bestLenIn"/>
    /// are considered. Stores the position afterwards (reference FindLongestMatch order).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void QuickSearch(ref byte bufRef, ref int head, int mask, int shift64, long minCand, long maxBackward, long bufBase, bool haveDict, int lastDist,
        long vpos, int maxLen, int bestLenIn, int endIdx, out int outLen, out int outDist, out int outScore)
    {
        outLen = 0;
        outDist = 0;
        outScore = MinScore;
        int idx = (int)(vpos - bufBase);
        ulong w = WordAt(ref bufRef, idx, endIdx);
        int key = HashQuick(w, shift64);
        int storeSlot = (key + ((int)vpos & 24)) & mask;
        if (bestLenIn >= maxLen)
        {
            Unsafe.Add(ref head, storeSlot) = (int)vpos;
            return;
        }
        int bestLen = bestLenIn;
        byte cmp = Unsafe.Add(ref bufRef, idx + bestLen);
        int bestScore = MinScore;
        // 1. The last distance (short distance code, hot source).
        long c = vpos - lastDist;
        if (c >= minCand && lastDist <= maxBackward)
        {
            int ci = (int)(c - bufBase);
            if (Unsafe.Add(ref bufRef, ci + bestLen) == cmp)
            {
                int len = CommonLength(ci, idx, maxLen);
                if (len >= MinMatch)
                {
                    int score = 135 * len + 15;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        outLen = len;
                        outDist = lastDist;
                        outScore = score;
                        bestLen = len;
                        if (len < maxLen) cmp = Unsafe.Add(ref bufRef, idx + len);
                    }
                }
            }
        }
        // 2. The four neighbouring keys.
        for (int i = 0; i < 4; i++)
        {
            int cand = Unsafe.Add(ref head, (key + (i << 3)) & mask);
            if (cand == int.MinValue) continue;
            int ci = (int)(cand - bufBase);   // live entries are readable (Slide clears the rest)
            if (bestLen >= maxLen) break;
            if (Unsafe.Add(ref bufRef, ci + bestLen) != cmp) continue;
            int len = CommonLength(ci, idx, maxLen);
            if (len < MinMatch) continue;
            c = cand;
            if (vpos - c > maxBackward) continue;
            int distance = (int)(vpos - c);
            int score = Score(len, distance);
            if (score > bestScore)
            {
                bestScore = score;
                outLen = len;
                outDist = distance;
                outScore = score;
                bestLen = len;
                if (len < maxLen) cmp = Unsafe.Add(ref bufRef, idx + len);
            }
        }
        // 3. The prefix dictionary, when nothing was found (cold).
        if (bestScore == MinScore && haveDict) QuickDictionary(key, idx, bestLen, cmp, maxLen, vpos, out outLen, out outDist, out outScore);
        Unsafe.Add(ref head, storeSlot) = (int)vpos;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QuickDictionary(int key, int idx, int bestLen, byte cmp, int maxLen, long vpos, out int outLen, out int outDist, out int outScore)
    {
        outLen = 0;
        outDist = 0;
        outScore = MinScore;
        int dc = _dictHead[key];
        if (dc == int.MinValue || Index(dc) < 0) return;
        int ci = Index(dc);
        maxLen = Math.Min(maxLen, -dc);   // a dictionary match cannot run past the dictionary
        if (bestLen >= maxLen || _buf[ci + bestLen] != cmp) return;
        int len = CommonLength(ci, idx, maxLen);
        if (len < MinMatch) return;
        int distance = DistanceFor(vpos, dc);
        int score = Score(len, distance);
        if (score > MinScore)
        {
            outLen = len;
            outDist = distance;
            outScore = score;
        }
    }

    // ------------------------------------------------------------------ metablock emission

    private void WriteStreamHeader()
    {
        if (_largeWindow)
        {
            _writer.WriteBits(14, (ulong)(((_windowBits & 0x3F) << 8) | 0x11));
        }
        else if (_windowBits == 16)
        {
            _writer.WriteBits(1, 0);
        }
        else if (_windowBits == 17)
        {
            _writer.WriteBits(7, 1);
        }
        else if (_windowBits > 17)
        {
            _writer.WriteBits(4, (ulong)(((_windowBits - 17) << 1) | 1));
        }
        else
        {
            _writer.WriteBits(7, (ulong)(((_windowBits - 8) << 4) | 1));
        }
        if (_concatenable)
        {
            // Empty metadata block so that fragment data starts at a byte boundary.
            WriteEmptyMetadataBlock();
        }
    }

    private void WriteEmptyMetadataBlock()
    {
        _writer.WriteBits(1, 0);   // ISLAST
        _writer.WriteBits(2, 3);   // MNIBBLES = 0 -> metadata
        _writer.WriteBits(1, 0);   // reserved
        _writer.WriteBits(2, 0);   // MSKIPBYTES = 0
        _writer.JumpToByteBoundary();
    }

    private static void EncodeMlen(int length, out ulong bits, out int numBits, out ulong nibblesBits)
    {
        int lg = length == 1 ? 1 : BitOperations.Log2((uint)(length - 1)) + 1;
        int mnibbles = (lg < 16 ? 16 : lg + 3) / 4;
        nibblesBits = (ulong)(mnibbles - 4);
        numBits = mnibbles * 4;
        bits = (ulong)(length - 1);
    }

    private void StoreCompressedMetaBlockHeader(bool isLast, int length)
    {
        _writer.WriteBits(1, isLast ? 1UL : 0UL);
        if (isLast) _writer.WriteBits(1, 0); // ISEMPTY
        EncodeMlen(length, out ulong lenBits, out int nLenBits, out ulong nibblesBits);
        _writer.WriteBits(2, nibblesBits);
        _writer.WriteBits(nLenBits, lenBits);
        if (!isLast) _writer.WriteBits(1, 0); // ISUNCOMPRESSED
    }

    private void StoreUncompressedMetaBlock(ReadOnlySpan<byte> data, bool isLast)
    {
        _writer.WriteBits(1, 0);
        EncodeMlen(data.Length, out ulong lenBits, out int nLenBits, out ulong nibblesBits);
        _writer.WriteBits(2, nibblesBits);
        _writer.WriteBits(nLenBits, lenBits);
        _writer.WriteBits(1, 1);
        _writer.JumpToByteBoundary();
        _writer.WriteBytes(data);
        if (isLast) WriteLastEmptyBlock();
    }

    private void WriteLastEmptyBlock()
    {
        _writer.WriteBits(1, 1); // ISLAST
        _writer.WriteBits(1, 1); // ISLASTEMPTY
        _writer.JumpToByteBoundary();
    }

    private static int GetInsertLengthCode(int insertlen)
    {
        if (insertlen < 6) return insertlen;
        if (insertlen < 130)
        {
            int nbits = BitOperations.Log2((uint)(insertlen - 2)) - 1;
            return (nbits << 1) + ((insertlen - 2) >> nbits) + 2;
        }
        if (insertlen < 2114) return BitOperations.Log2((uint)(insertlen - 66)) + 10;
        if (insertlen < 6210) return 21;
        if (insertlen < 22594) return 22;
        return 23;
    }

    private static int GetCopyLengthCode(int copylen)
    {
        if (copylen < 10) return copylen - 2;
        if (copylen < 134)
        {
            int nbits = BitOperations.Log2((uint)(copylen - 6)) - 1;
            return (nbits << 1) + ((copylen - 6) >> nbits) + 4;
        }
        if (copylen < 2118) return BitOperations.Log2((uint)(copylen - 70)) + 12;
        return 23;
    }

    private static int CombineLengthCodes(int inscode, int copycode, bool useLastDistance)
    {
        int bits64 = (copycode & 0x7) | ((inscode & 0x7) << 3);
        if (useLastDistance && inscode < 8 && copycode < 16)
        {
            return copycode < 8 ? bits64 : bits64 | 64;
        }
        int offset = 2 * ((copycode >> 3) + 3 * (inscode >> 3));
        offset = (offset << 5) + 0x40 + ((0x520D40 >> offset) & 0xC0);
        return offset | bits64;
    }

    /// <summary>Distance symbol and extra bits for NPOSTFIX = 0, NDIRECT = 0.</summary>
    private static void PrefixEncodeCopyDistance(int distanceCode, out int code, out int nExtra, out uint extra)
    {
        if (distanceCode < Constants.NumDistanceShortCodes)
        {
            code = distanceCode;
            nExtra = 0;
            extra = 0;
            return;
        }
        long dist = (1L << 2) + (distanceCode - Constants.NumDistanceShortCodes);
        int bucket = BitOperations.Log2((ulong)dist) - 1;
        long prefix = (dist >> bucket) & 1;
        long offset = (2 + prefix) << bucket;
        int nbits = bucket;
        code = (int)(Constants.NumDistanceShortCodes + (2 * (nbits - 1) + prefix));
        nExtra = nbits;
        extra = (uint)(dist - offset);
    }

    private void EmitBlock(bool isLast, bool flush)
    {
        int length = (int)(_inputEnd - _streamPos);
        _writer.Reset(_pendingByte, _pendingBits);
        if (!_headerWritten)
        {
            WriteStreamHeader();
            _headerWritten = true;
        }
        if (length > 0)
        {
            EmitDataMetablock(length, isLast && !_concatenable);
        }
        else if (isLast && !_concatenable)
        {
            WriteLastEmptyBlock();
        }
        if (flush || (isLast && _concatenable))
        {
            if ((_writer.BitPosition & 7) != 0) WriteEmptyMetadataBlock();
        }
        // Everything up to the last whole byte is output; the partial byte is carried.
        long bitPos = _writer.BitPosition;
        int wholeBytes = (int)(bitPos >> 3);
        _pendingBits = (int)(bitPos & 7);
        _pendingByte = _pendingBits != 0 ? _writer.Storage[wholeBytes] : 0u;
        if (isLast && _pendingBits != 0)
        {
            // Last block is byte aligned by construction; defensive.
            wholeBytes++;
            _pendingBits = 0;
            _pendingByte = 0;
        }
        _out = _writer.Storage;
        _outPos = 0;
        _outLen = wholeBytes;
    }

    private void EmitDataMetablock(int length, bool isLast)
    {
        if (_q.Zopfli)
        {
            EmitZopfliMetablock(length, isLast);
            return;
        }
        long start = _streamPos;
        long end = _streamPos + length;
        int saved0 = _distCache[0], saved1 = _distCache[1], saved2 = _distCache[2], saved3 = _distCache[3];
        int savedPushed = _distPushed;
        long startBit = _writer.BitPosition;

        // Histograms are counted by AddCommand during the parse (literals in four stripes, merged here).
        Array.Clear(_litHisto4, 0, _litHisto4.Length);
        Array.Clear(_cmdHisto, 0, _cmdHisto.Length);
        Array.Clear(_distHisto, 0, _distHisto.Length);
        if (_q.UseBuckets) FindCommands(end);
        else if (_q.Quick) FindCommandsQuick(end);
        else FindCommandsGreedy(end);
        if (_mb != null)
        {
            StoreSplitMetablock(start, end, length, isLast, saved0, saved1, saved2, saved3, savedPushed, startBit);
            return;
        }
        if (_greedyMb != null)
        {
            StoreGreedyMetablock(start, end, length, isLast, saved0, saved1, saved2, saved3, savedPushed, startBit);
            return;
        }
        StoreTrivialMetablock(start, end, length, isLast, saved0, saved1, saved2, saved3, savedPushed, startBit);
    }

    /// <summary>
    /// Stores [start, end) as a block-split metablock built in one pass, with a static literal context map when
    /// the data justifies one. This is what the fast tier owes its ratio to in the reference encoder.
    /// </summary>
    private void StoreGreedyMetablock(long start, long end, int length, bool isLast, int saved0, int saved1, int saved2, int saved3, int savedPushed, long startBit)
    {
        // No distance-parameter search here: the reference only pays for it in the shortest-path tier, and on a
        // fast parse it costs several times what the whole metablock build does.
        DistanceParams dist = DistanceParams.Create(0, 0, _largeWindow);
        int startIdx = Index(start);
        int contextMode = MetaBlockBuilder.ChooseContextMode(_buf, startIdx, length);
        byte prev1 = start >= 1 ? _buf[startIdx - 1] : (byte)0;
        byte prev2 = start >= 2 ? _buf[startIdx - 2] : (byte)0;
        // A concatenable fragment cannot know the bytes before it, so it cannot model literal contexts.
        uint[]? staticMap = null;
        int numContexts = _concatenable ? 1 : GreedyMetaBlock.ChooseContexts(_buf, startIdx, length, _quality, _sizeHint, out staticMap);
        if (numContexts == 1) staticMap = null;
        _greedyMb!.Reset();
        GreedyMetaBlock.Build(_buf, startIdx, prev1, prev2, contextMode, numContexts, staticMap, dist.AlphabetSizeLimit,
            _commands, _numCommands, _greedyMb);
        MetaBlockBuilder.OptimizeHistograms(dist.AlphabetSizeLimit, _greedyMb);

        StoreCompressedMetaBlockHeader(isLast, length);
        MetaBlockBuilder.Store(_buf, startIdx, prev1, prev2, dist, contextMode, _commands, _numCommands, _greedyMb, _writer);
        if (isLast) _writer.JumpToByteBoundary();

        long compressedBits = _writer.BitPosition - startBit;
        if ((compressedBits + 7) / 8 > length + 4 && length <= Constants.MaxMetablockSize)
        {
            _writer.Truncate(startBit);
            StoreUncompressedMetaBlock(_buf.AsSpan(startIdx, length), isLast);
            _distCache[0] = saved0; _distCache[1] = saved1; _distCache[2] = saved2; _distCache[3] = saved3;
            PrepareDistanceCandidates();
            _distPushed = savedPushed;
        }
        _streamPos = end;
    }

    /// <summary>Stores [start, end) as one metablock with one block type and one prefix code per alphabet, from the histograms counted during the parse.</summary>
    private void StoreTrivialMetablock(long start, long end, int length, bool isLast, int saved0, int saved1, int saved2, int saved3, int savedPushed, long startBit)
    {
        for (int k = 0; k < Constants.NumLiteralSymbols; k++)
        {
            _litHisto[k] = _litHisto4[k] + _litHisto4[256 + k] + _litHisto4[512 + k] + _litHisto4[768 + k];
        }
        int pos;
        int distAlphabet = Constants.DistanceAlphabetSize(0, 0, _largeWindow ? Constants.LargeMaxDistanceBits : Constants.MaxDistanceBits);

        StoreCompressedMetaBlockHeader(isLast, length);
        _writer.WriteBits(13, 0); // 1 block type each, NPOSTFIX 0, NDIRECT 0, context mode 0, 1 tree each
        HuffmanEncoder.BuildAndStoreHuffmanTree(_litHisto, Constants.NumLiteralSymbols, Constants.NumLiteralSymbols, _litDepth, _litBits, _writer);
        HuffmanEncoder.BuildAndStoreHuffmanTree(_cmdHisto, Constants.NumCommandSymbols, Constants.NumCommandSymbols, _cmdDepth, _cmdBits, _writer);
        HuffmanEncoder.BuildAndStoreHuffmanTree(_distHisto, distAlphabet, distAlphabet, _distDepth, _distBits, _writer);
        for (int k = 0; k < Constants.NumLiteralSymbols; k++) _litCode[k] = ((uint)_litDepth[k] << 16) | _litBits[k];
        for (int k = 0; k < Constants.NumCommandSymbols; k++) _cmdCode[k] = ((uint)_cmdDepth[k] << 16) | _cmdBits[k];
        for (int k = 0; k < distAlphabet; k++) _distCode[k] = ((uint)_distDepth[k] << 16) | _distBits[k];

        // Exact size of the body from the histograms: if the raw block wins, skip writing the body at all
        // (the same rule as the check after emission, so the output is unchanged).
        {
            // Extra bits are a function of the prefix code, so they come from the histograms as well.
            long bodyBits = 0;
            for (int k = 0; k < Constants.NumLiteralSymbols; k++) bodyBits += (long)_litHisto[k] * _litDepth[k];
            for (int k = 0; k < Constants.NumCommandSymbols; k++)
            {
                uint n = _cmdHisto[k];
                if (n != 0)
                {
                    ref readonly CommandLut.Entry lut = ref CommandLut.Table[k];
                    bodyBits += (long)n * (_cmdDepth[k] + lut.InsertExtraBits + lut.CopyExtraBits);
                }
            }
            for (int k = 0; k < distAlphabet; k++)
            {
                uint n = _distHisto[k];
                if (n != 0) bodyBits += (long)n * (_distDepth[k] + (k < Constants.NumDistanceShortCodes ? 0 : ((k - Constants.NumDistanceShortCodes) >> 1) + 1));
            }
            long totalBits = _writer.BitPosition - startBit + bodyBits;
            if (isLast) totalBits = (totalBits + 7) & ~7L;
            if ((totalBits + 7) / 8 > length + 4 && length <= Constants.MaxMetablockSize)
            {
                _writer.Truncate(startBit);
                StoreUncompressedMetaBlock(_buf.AsSpan(Index(start), length), isLast);
                _distCache[0] = saved0; _distCache[1] = saved1; _distCache[2] = saved2; _distCache[3] = saved3;
            PrepareDistanceCandidates();
                _distPushed = savedPushed;
                _streamPos = end;
                return;
            }
        }

        // Data, written with the unchecked path. Bound per command: prefix (15) + extras (48) + distance
        // prefix (15) + distance extras (30) bits = 14 bytes; per literal 15 bits = 2 bytes; plus slack.
        pos = Index(start);
        _writer.Reserve(_numCommands * 14 + length * 2 + 64);
        {
            ref byte storage = ref MemoryMarshal.GetReference(_writer.Storage.AsSpan());
            ref byte bufRef = ref MemoryMarshal.GetReference(_buf.AsSpan());
            ref uint litCode = ref MemoryMarshal.GetReference(_litCode.AsSpan());
            ref uint cmdCodes = ref MemoryMarshal.GetReference(_cmdCode.AsSpan());
            ref uint distCodes = ref MemoryMarshal.GetReference(_distCode.AsSpan());
            ref Command commands = ref MemoryMarshal.GetReference(_commands.AsSpan());
            long bitPos = _writer.BitPosition;
            int numCommands = _numCommands;
            for (int c = 0; c < numCommands; c++)
            {
                ref Command cmd = ref Unsafe.Add(ref commands, c);
                int cmdCode = cmd.CmdPrefix;
                ref readonly CommandLut.Entry lut = ref CommandLut.Table[cmdCode];
                uint cc = Unsafe.Add(ref cmdCodes, cmdCode);
                bitPos = BitWriter.Put(ref storage, bitPos, (int)(cc >> 16), cc & 0xFFFF);
                int copyLen = cmd.CopyLength == 0 ? 4 : cmd.CopyLength;
                ulong insExtra = (ulong)(cmd.InsertLength - lut.InsertOffset);
                ulong copyExtra = (ulong)(copyLen - lut.CopyOffset);
                bitPos = BitWriter.Put(ref storage, bitPos, lut.InsertExtraBits + lut.CopyExtraBits, (copyExtra << lut.InsertExtraBits) | insExtra);
                if (cmd.InsertLength != 0) bitPos = BitWriter.PutLiterals(ref storage, bitPos, ref bufRef, pos, cmd.InsertLength, ref litCode);
                pos += cmd.InsertLength;
                if (cmd.CopyLength != 0 && cmdCode >= 128)
                {
                    // Distance prefix and its extra bits in one write (at most 15 + 30 bits).
                    uint dc = Unsafe.Add(ref distCodes, cmd.DistPrefix & 0x3FF);
                    int depth = (int)(dc >> 16);
                    bitPos = BitWriter.Put(ref storage, bitPos, depth + (cmd.DistPrefix >> 10), ((ulong)cmd.DistExtra << depth) | (dc & 0xFFFF));
                }
                pos += cmd.CopyLength;
            }
            _writer.SetBitPosition(bitPos);
        }
        if (isLast) _writer.JumpToByteBoundary();

        // Uncompressed fallback: if we expanded the data, rewrite as a raw block.
        long compressedBits = _writer.BitPosition - startBit;
        if ((compressedBits + 7) / 8 > length + 4 && length <= Constants.MaxMetablockSize)
        {
            _writer.Truncate(startBit);
            StoreUncompressedMetaBlock(_buf.AsSpan(Index(start), length), isLast);
            // The decoder's distance cache is untouched by an uncompressed block: restore ours.
            _distCache[0] = saved0; _distCache[1] = saved1; _distCache[2] = saved2; _distCache[3] = saved3;
            PrepareDistanceCandidates();
            _distPushed = savedPushed;
        }

        _streamPos = end;
    }

    // ------------------------------------------------------------------ quality 11

    /// <summary>Records a command of the quality-11 parser; the parser maintains the distance cache itself.</summary>
    public void AddZopfliCommand(int literalIdx, int insertLength, int copyLength, int copyLengthCode, int distCode, int distance, bool isDictionary)
    {
        if (_numCommands == _commands.Length) Array.Resize(ref _commands, _commands.Length * 2);
        int insCode = GetInsertLengthCode(insertLength);
        if (copyLength == 0)
        {
            _commands[_numCommands++] = new Command
            {
                InsertLength = insertLength, CopyLength = 0,
                CmdPrefix = (ushort)CombineLengthCodes(insCode, GetCopyLengthCode(4), false), DistPrefix = 0, DistExtra = 0,
            };
            return;
        }
        PrefixEncodeCopyDistance(distCode, out int dcode, out int nExtra, out uint extra);
        if (distCode != 0 && !isDictionary) _distPushed++;
        int delta = copyLengthCode - copyLength;
        _commands[_numCommands++] = new Command
        {
            InsertLength = insertLength, CopyLength = copyLength | ((delta & 0x7F) << 25),
            CmdPrefix = (ushort)CombineLengthCodes(insCode, GetCopyLengthCode(copyLengthCode), distCode == 0),
            DistPrefix = (ushort)(dcode | (nExtra << 10)), DistExtra = extra,
        };
    }

    /// <summary>Stores the positions just before <paramref name="position"/> that the previous chunk could not (the tree needs 128 bytes of lookahead).</summary>
    private void StitchHasher(long position, long dataEnd)
    {
        BinaryTreeHasher hasher = _zopfli!.Hasher;
        long iStart = Math.Max(_matchStart, position - (BinaryTreeHasher.MaxTreeCompLength - 1));
        long iEnd = Math.Min(position, dataEnd - BinaryTreeHasher.MaxTreeCompLength + 1);
        for (long i = iStart; i < iEnd; ++i)
        {
            // The buffer keeps _maxBackward bytes before the block, not the whole window: bound by what is present.
            long maxBackward = Math.Min(Math.Min(_maxBackward + Constants.WindowGap - 1 - Math.Max(Constants.WindowGap - 1, position - i), i - _matchStart), i - _bufBase);
            if (maxBackward > 0) hasher.Store(_buf, _bufBase, i, maxBackward);
        }
    }

    private void EmitZopfliMetablock(int length, bool isLast)
    {
        long start = _streamPos;
        long end = start + length;
        int saved0 = _distCache[0], saved1 = _distCache[1], saved2 = _distCache[2], saved3 = _distCache[3];
        int savedPushed = _distPushed;
        long startBit = _writer.BitPosition;
        ZopfliParser z = _zopfli!;
        _numCommands = 0;

        // Concatenable fragments: cache entries this encoder did not push are unknown and must never be referenced.
        int usable = _concatenable ? Math.Min(_distPushed, 4) : 4;
        for (int k = usable; k < 4; k++) _distCache[k] = int.MaxValue / 2;
        PrepareDistanceCandidates();
        if (start == 0 && _dictLength > 0)
        {
            // Make the prefix dictionary findable (its last 127 positions are stored by the stitch below).
            // The last 127 dictionary positions belong to the stitch below: storing a position twice would
            // re-root its bucket out of order and cut the tree.
            long dictStoreEnd = Math.Min(Math.Min(0, end - BinaryTreeHasher.MaxTreeCompLength + 1), -(BinaryTreeHasher.MaxTreeCompLength - 1));
            for (long i = -_dictLength; i < dictStoreEnd; ++i) z.Hasher.Store(_buf, _bufBase, i, Math.Min(_maxBackward, i - _matchStart));
        }
        int chunk = 1 << Math.Min(18, Math.Max(16, _windowBits));
        int lastInsertLen = 0;
        for (long p = start; p < end; p += chunk)
        {
            int n = (int)Math.Min(chunk, end - p);
            StitchHasher(p, end);
            // A concatenable fragment cannot know its position in the final stream, which static-dictionary distances depend on.
            lastInsertLen = z.Parse(_buf, _bufBase, p, n, _maxBackward, _matchStart, _concatenable ? 0 : _dictLength, !_concatenable, _distCache, lastInsertLen, this);
        }
        if (lastInsertLen > 0) AddZopfliCommand(Index(end - lastInsertLen), lastInsertLen, 0, 0, 0, 0, false);

        StoreSplitMetablock(start, end, length, isLast, saved0, saved1, saved2, saved3, savedPushed, startBit);
    }

    /// <summary>Stores [start, end) with block splitting, context modeling and clustered histograms.</summary>
    private void StoreSplitMetablock(long start, long end, int length, bool isLast, int saved0, int saved1, int saved2, int saved3, int savedPushed, long startBit)
    {
        DistanceParams orig = DistanceParams.Create(0, 0, _largeWindow);
        DistanceParams dist = MetaBlockBuilder.ChooseDistanceParams(_commands, _numCommands, orig, _largeWindow);
        int startIdx = Index(start);
        int contextMode = MetaBlockBuilder.ChooseContextMode(_buf, startIdx, length);
        // The decoder's context bytes at the start of the stream are zero (the prefix dictionary does not count).
        byte prev1 = start >= 1 ? _buf[startIdx - 1] : (byte)0;
        byte prev2 = start >= 2 ? _buf[startIdx - 2] : (byte)0;
        // A concatenable fragment cannot know the bytes before it: no literal context modeling.
        bool contextModeling = !_concatenable;
        MetaBlockBuilder.Build(_buf, startIdx, prev1, prev2, _commands, _numCommands, contextMode, contextModeling, dist, _mb!);
        MetaBlockBuilder.OptimizeHistograms(dist.AlphabetSizeLimit, _mb!);

        StoreCompressedMetaBlockHeader(isLast, length);
        MetaBlockBuilder.Store(_buf, startIdx, prev1, prev2, dist, contextMode, _commands, _numCommands, _mb!, _writer);
        if (isLast) _writer.JumpToByteBoundary();

        long compressedBits = _writer.BitPosition - startBit;
        if ((compressedBits + 7) / 8 > length + 4 && length <= Constants.MaxMetablockSize)
        {
            _writer.Truncate(startBit);
            StoreUncompressedMetaBlock(_buf.AsSpan(startIdx, length), isLast);
            _distCache[0] = saved0; _distCache[1] = saved1; _distCache[2] = saved2; _distCache[3] = saved3;
            PrepareDistanceCandidates();
            _distPushed = savedPushed;
        }
        _streamPos = end;
    }
}
