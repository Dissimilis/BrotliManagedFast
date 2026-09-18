using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET7_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace BrotliManagedFast.Internal;

internal enum DecodeResult
{
    Success,
    NeedsMoreInput,
    NeedsMoreOutput,
    Error,
}

/// <summary>
/// Resumable RFC 7932 decoder. A port of the reference state machine in "safe" mode: every bit read can
/// fail for lack of input and the state records enough to continue on the next call.
/// </summary>
internal sealed class DecoderCore
{
    private enum State
    {
        Uninited,
        LargeWindowBits,
        Initialize,
        MetablockBegin,
        MetablockHeader,
        MetablockHeader2,
        ContextModes,
        CommandBegin,
        CommandInner,
        CommandPostDecodeLiterals,
        CommandPostWrapCopy,
        Uncompressed,
        Metadata,
        CommandInnerWrite,
        CommandPostWrite1,
        CommandPostWrite2,
        BeforeCompressedMetablockHeader,
        HuffmanCode0,
        HuffmanCode1,
        HuffmanCode2,
        HuffmanCode3,
        ContextMap1,
        ContextMap2,
        TreeGroup,
        BeforeCompressedMetablockBody,
        MetablockDone,
        Done,
        Failed,
    }

    private enum MetablockHeaderSubstate { None, Empty, Nibbles, Size, Uncompressed, Reserved, Bytes, Metadata }
    private enum Uint8Substate { None, Short, Long }
    private enum HuffmanSubstate { None, SimpleSize, SimpleRead, SimpleBuild, Complex, LengthSymbols }
    private enum TreeGroupSubstate { None, Loop }
    private enum ContextMapSubstate { None, ReadPrefix, Huffman, Decode, Transform }
    private enum BlockLengthSubstate { None, Suffix }

    private const int RingBufferSlack = 64;

    // ---- configuration
    private readonly int _maxWindowBits;
    private readonly bool _allowLargeWindow;
    private readonly long _maxOutputLength;
    private readonly byte[]? _prefixDictionary;
    private readonly int _prefixDictionarySize;
    private readonly ArrayPool<byte> _pool;

    // ---- bit reader carry (at most 7 bits between calls)
    private ulong _acc;
    private int _accBits;

    // ---- stream state
    private State _state = State.Uninited;
    private BrotliDecoderError _error;
    private int _windowBits;
    private bool _largeWindow;
    private int _maxBackwardDistance;
    private int _maxDistance;

    // ---- ring buffer
    private byte[]? _ringBuffer;
    private int _ringBufferSize;
    private int _ringBufferMask;
    private int _newRingBufferSize;
    private int _pos;
    private long _partialPosOut;
    private long _rbRoundtrips;
    private bool _shouldWrapRingBuffer;

    // ---- metablock
    private int _metaBlockRemainingLen;
    private bool _isLastMetablock;
    private bool _isUncompressed;
    private bool _isMetadata;
    private int _loopCounter;
    private readonly int[] _numBlockTypes = new int[3];
    private readonly int[] _blockLength = new int[3];
    private readonly int[] _blockTypeRb = new int[6];
    private readonly uint[] _blockTypeTrees = new uint[3 * Constants.HuffmanMaxSize258];
    private readonly uint[] _blockLenTrees = new uint[3 * Constants.HuffmanMaxSize26];
    private int _distancePostfixBits;
    private int _numDirectDistanceCodes;
    private byte[] _contextModes = new byte[Constants.MaxBlockTypes];
    private byte[] _contextMap = Array.Empty<byte>();
    private byte[] _distContextMap = Array.Empty<byte>();
    private bool _contextMapRented;
    private bool _distContextMapRented;
    private int _numLiteralHtrees;
    private int _numDistHtrees;
    private readonly uint[] _trivialLiteralContexts = new uint[8];
    private readonly HuffmanGroup _literalGroup = new HuffmanGroup();
    private readonly HuffmanGroup _insertCopyGroup = new HuffmanGroup();
    private readonly HuffmanGroup _distanceGroup = new HuffmanGroup();
    private readonly byte[] _distExtraBits = new byte[1200];
    private readonly int[] _distOffset = new int[1200];

    // ---- command decoding
    private bool _trivialLiteralContext;
    private int _literalHtreeOffset;
    /// <summary>
    /// Tree table offset per literal context, for the current literal block type: the context map lookup and
    /// the tree offset lookup folded into one. Refilled whenever the literal block type changes, and only
    /// after the tree group has been read and any rebuild has run, since a stale offset would be read
    /// unchecked in the fast loop.
    /// </summary>
    private readonly int[] _literalCtxTree = new int[1 << Constants.LiteralContextBits];
    private int _contextLookupOffset;
    private int _htreeCommandOffset;
    private int _distContextMapSliceOffset;
    private int _distHtreeIndex;
    private int _distanceContext;
    private int _distanceCode;
    private int _copyLength;
    private readonly int[] _distRb = new int[4];
    private int _distRbIdx;
    private BlockLengthSubstate _blockLengthSubstate;
    private int _blockLengthIndex;

    // ---- prefix dictionary copy in progress
    private int _dictCopyOffset;
    private int _dictCopyRemaining;

    // ---- header parsing arena
    private MetablockHeaderSubstate _mbSubstate;
    private Uint8Substate _uint8Substate;
    private int _sizeNibbles;
    private HuffmanSubstate _huffSubstate;
    private TreeGroupSubstate _treeGroupSubstate;
    private ContextMapSubstate _ctxMapSubstate;
    private int _subLoopCounter;
    private int _symbol;
    private int _repeat;
    private uint _space;
    private int _prevCodeLen;
    private int _repeatCodeLen;
    private readonly byte[] _codeLengthCodeLengths = new byte[Constants.CodeLengthCodes];
    private readonly int[] _codeLengthHisto = new int[Constants.HuffmanMaxCodeLength + 1];
    private readonly uint[] _clcTable = new uint[1 << Constants.HuffmanMaxCodeLengthCodeLength];
    private readonly ushort[] _simpleSymbols = new ushort[4];
    private readonly byte[] _codeLengths = new byte[1200];
    private readonly ushort[] _sortedSymbols = new ushort[1200];
    private readonly uint[] _contextMapTable = new uint[Constants.HuffmanMaxSize272];
    private int _htreeIndex;
    private int _nextTableOffset;
    private int _contextIndex;
    private int _maxRunLengthPrefix;
    private int _ctxCode;
    private int _mtfUpperBound;
    private readonly byte[] _mtf = new byte[256 + 4];
    private int _varLenValue;
    private int _numHtreesTmp;

    private sealed class HuffmanGroup
    {
        public uint[] Codes = Array.Empty<uint>();
        public int[] HtreeOffsets = Array.Empty<int>();
        /// <summary>Root width of every tree in the group (8, or 10 when the group chose it after reading all trees).</summary>
        public int RootBits = HuffmanTable.RootBits;
        /// <summary>Whether the group may rebuild its tables with the 10-bit root (literals and commands).</summary>
        public bool Adaptive;
        // Per tree, for the rebuild: symbols by length (or the 1-4 symbols of a simple code), the length
        // histogram, the simple-code symbol count minus one (or -1 for a complex code), the long-code mass.
        public ushort[] SavedSymbols = Array.Empty<ushort>();
        public int[] SavedCounts = Array.Empty<int>();
        public int[] SavedKind = Array.Empty<int>();
        public int[] LongMass = Array.Empty<int>();
        public int AlphabetSizeMax;
        public int AlphabetSizeLimit;
        public int NumHtrees;

        public void Init(int alphabetSizeMax, int alphabetSizeLimit, int numHtrees)
        {
            AlphabetSizeMax = alphabetSizeMax;
            AlphabetSizeLimit = alphabetSizeLimit;
            NumHtrees = numHtrees;
            int maxTableSize = alphabetSizeLimit + Constants.HuffmanTableSlack;
            int needed = numHtrees * maxTableSize;
            // Rented, never zeroed. Nothing here is read before it is written: a tree's table is read only
            // through its own offset and only up to the size the build reported, and the context maps below
            // are either filled completely or cleared explicitly. This is the same situation as reusing one
            // decoder through Reset(), which the suite already covers.
            Grow(ref Codes, needed, ref _codesRented);
            Grow(ref HtreeOffsets, numHtrees, ref _offsetsRented);
            if (Adaptive)
            {
                Grow(ref SavedSymbols, numHtrees * alphabetSizeLimit, ref _symbolsRented);
                Grow(ref SavedCounts, numHtrees * (Constants.HuffmanMaxCodeLength + 1), ref _countsRented);
                Grow(ref SavedKind, numHtrees, ref _kindRented);
                Grow(ref LongMass, numHtrees, ref _massRented);
            }
        }

        private bool _codesRented, _offsetsRented, _symbolsRented, _countsRented, _kindRented, _massRented;

        private static void Grow<T>(ref T[] array, int needed, ref bool rented)
        {
            if (array.Length >= needed) return;
            if (rented) ArrayPool<T>.Shared.Return(array);
            array = ArrayPool<T>.Shared.Rent(needed);
            rented = true;
        }

        private static void Release<T>(ref T[] array, ref bool rented)
        {
            if (!rented) return;
            ArrayPool<T>.Shared.Return(array);
            array = Array.Empty<T>();
            rented = false;
        }

        /// <summary>Returns every rented table; the group is usable again afterwards, Init re-rents.</summary>
        public void ReleaseBuffers()
        {
            Release(ref Codes, ref _codesRented);
            Release(ref HtreeOffsets, ref _offsetsRented);
            Release(ref SavedSymbols, ref _symbolsRented);
            Release(ref SavedCounts, ref _countsRented);
            Release(ref SavedKind, ref _kindRented);
            Release(ref LongMass, ref _massRented);
        }

        /// <summary>Rebuilds every table with the width chosen from the saved trees; returns the total size.</summary>
        public int Rebuild()
        {
            int offset = 0;
            Span<int> count = stackalloc int[Constants.HuffmanMaxCodeLength + 1];
            for (int t = 0; t < NumHtrees; t++)
            {
                Span<ushort> symbols = SavedSymbols.AsSpan(t * AlphabetSizeLimit, AlphabetSizeLimit);
                int size;
                if (SavedKind[t] >= 0)
                {
                    size = HuffmanTable.BuildSimpleTable(Codes.AsSpan(offset), RootBits, symbols, SavedKind[t]);
                }
                else
                {
                    SavedCounts.AsSpan(t * (Constants.HuffmanMaxCodeLength + 1), Constants.HuffmanMaxCodeLength + 1).CopyTo(count);
                    size = HuffmanTable.BuildTable(Codes.AsSpan(offset), RootBits, symbols, count);
                }
                HtreeOffsets[t] = offset;
                offset += size;
            }
            return offset;
        }
    }

    public DecoderCore(BrotliDecompressionOptions? options)
    {
        options ??= BrotliDecompressionOptions.Default;
        _maxWindowBits = options.MaxWindowLog;
        _allowLargeWindow = options.MaxWindowLog > Constants.MaxWindowBits;
        _maxOutputLength = options.MaxOutputLength;
        _pool = options.Pool ?? ArrayPool<byte>.Shared;
        if (options.Dictionary is { Length: > 0 } dict)
        {
            _prefixDictionary = dict.Bytes;
            _prefixDictionarySize = dict.Length;
        }
        Reset();
    }

    public BrotliDecoderError LastError => _error;
    internal string DebugState => $"state={_state} accBits={_accBits} mb={_mbSubstate} huff={_huffSubstate} ctx={_ctxMapSubstate} bl={_blockLengthSubstate} loop={_loopCounter} sub={_subLoopCounter} pos={_pos} rbs={_ringBufferSize} mbrem={_metaBlockRemainingLen} blk=[{_blockLength[0]},{_blockLength[1]},{_blockLength[2]}] dist={_distanceCode} copy={_copyLength}";
    public long TotalBytesWritten => _partialPosOut;
    public bool IsFinished => _state == State.Done && !HasMoreOutput;
    public bool IsFailed => _state == State.Failed;
    public bool IsUsed => _state != State.Uninited || _accBits != 0;

    private bool HasMoreOutput => _ringBuffer != null && UnwrittenBytes(false) != 0;

    public void Reset()
    {
        if (_ringBuffer != null)
        {
            _pool.Return(_ringBuffer);
            _ringBuffer = null;
        }
        _acc = 0;
        _accBits = 0;
        _state = State.Uninited;
        _error = BrotliDecoderError.None;
        _windowBits = 0;
        _largeWindow = false;
        _ringBufferSize = 0;
        _ringBufferMask = 0;
        _newRingBufferSize = 0;
        _pos = 0;
        _partialPosOut = 0;
        _rbRoundtrips = 0;
        _shouldWrapRingBuffer = false;
        _maxDistance = 0;
        _distRbIdx = 0;
        _distRb[0] = 16; _distRb[1] = 15; _distRb[2] = 11; _distRb[3] = 4;
        _mtfUpperBound = 63;
        _mbSubstate = MetablockHeaderSubstate.None;
        _uint8Substate = Uint8Substate.None;
        _huffSubstate = HuffmanSubstate.None;
        _treeGroupSubstate = TreeGroupSubstate.None;
        _ctxMapSubstate = ContextMapSubstate.None;
        _blockLengthSubstate = BlockLengthSubstate.None;
        _dictCopyRemaining = 0;
        _metaBlockRemainingLen = 0;
        _tailLength = 0;
    }

    public void Dispose()
    {
        if (_ringBuffer != null)
        {
            _pool.Return(_ringBuffer);
            _ringBuffer = null;
        }
        _literalGroup.ReleaseBuffers();
        _insertCopyGroup.ReleaseBuffers();
        _distanceGroup.ReleaseBuffers();
        if (_contextMapRented)
        {
            ArrayPool<byte>.Shared.Return(_contextMap);
            _contextMap = Array.Empty<byte>();
            _contextMapRented = false;
        }
        if (_distContextMapRented)
        {
            ArrayPool<byte>.Shared.Return(_distContextMap);
            _distContextMap = Array.Empty<byte>();
            _distContextMapRented = false;
        }
        _state = State.Failed;
        _error = BrotliDecoderError.InvalidState;
    }

    private DecodeResult Fail(BrotliDecoderError error)
    {
        _error = error;
        _state = State.Failed;
        return DecodeResult.Error;
    }

    /// <summary>Marks the decoder failed with a caller-detected error (truncation, trailing data).</summary>
    public void MarkError(BrotliDecoderError error)
    {
        _error = error;
        _state = State.Failed;
    }

    // ------------------------------------------------------------------ entry point

    // Tail buffer: bytes handed to us that could not complete a multi-part read. They count as
    // consumed for the caller and are replayed (one new byte at a time) until the read completes.
    private readonly byte[] _tail = new byte[64];
    private int _tailLength;

    /// <summary>
    /// Decodes as much as possible. Input is always fully consumed when more input is needed (a small
    /// internal buffer holds partial reads); otherwise whole unread bytes are handed back via
    /// <paramref name="bytesConsumed"/>. At most 7 bits are carried in the accumulator between calls.
    /// </summary>
    public DecodeResult Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten)
    {
        // The destination length is a hint for the ring-buffer size: a caller that offers room for the whole
        // output gets a ring buffer sized once instead of a chain of doublings, each copying the data so far.
        if (output.Length > _outputHint) _outputHint = output.Length;
        bytesConsumed = 0;
        bytesWritten = 0;
        if (_state == State.Failed)
        {
            if (_error == BrotliDecoderError.None) _error = BrotliDecoderError.InvalidState;
            return DecodeResult.Error;
        }

        int inputPos = 0;
        DecodeResult result;

        // Phase 1: replay the tail buffer, topping it up one byte at a time from the new input.
        while (_tailLength > 0)
        {
            var tr = new BitReader(_tail.AsSpan(0, _tailLength), _acc, _accBits, 0);
            result = Run(ref tr, output, ref bytesWritten);
            if (result == DecodeResult.Error)
            {
                _tailLength = 0;
                _acc = 0;
                _accBits = 0;
                bytesConsumed = input.Length;
                return result;
            }
            if (result == DecodeResult.NeedsMoreInput)
            {
                if (tr.Pos == _tailLength)
                {
                    // Tail fully absorbed into the accumulator; continue with the real input.
                    _acc = tr.Acc;
                    _accBits = tr.AccBits;
                    _tailLength = 0;
                    break;
                }
                // Partial read rolled back. Add one more byte and retry.
                _acc = tr.Acc;
                _accBits = tr.AccBits;
                CompactTail(tr.Pos);
                if (inputPos < input.Length)
                {
                    _tail[_tailLength++] = input[inputPos++];
                    continue;
                }
                bytesConsumed = inputPos;
                return DecodeResult.NeedsMoreInput;
            }
            // Success or needs-more-output while reading from the tail.
            tr.Unload();
            _acc = tr.Acc;
            _accBits = tr.AccBits;
            CompactTail(tr.Pos);
            bytesConsumed = inputPos;
            return result;
        }

        // Phase 2: read from the caller's input.
        var br = new BitReader(input.Slice(inputPos), _acc, _accBits, 0);
        result = Run(ref br, output, ref bytesWritten);

        if (result == DecodeResult.Error)
        {
            _acc = 0;
            _accBits = 0;
            bytesConsumed = input.Length;
            return result;
        }
        if (result == DecodeResult.NeedsMoreInput)
        {
            _acc = br.Acc;
            _accBits = br.AccBits;
            int remaining = input.Length - inputPos - br.Pos;
            if (remaining > _tail.Length)
            {
                // Cannot happen: a rolled-back read leaves at most a few bytes unread.
                throw new InvalidOperationException("Decoder tail buffer overflow.");
            }
            input.Slice(inputPos + br.Pos, remaining).CopyTo(_tail);
            _tailLength = remaining;
            bytesConsumed = input.Length;
            return result;
        }
        br.Unload();
        _acc = br.Acc;
        _accBits = br.AccBits;
        bytesConsumed = inputPos + br.Pos;
        return result;
    }

    private void CompactTail(int consumed)
    {
        if (consumed <= 0) return;
        int rest = _tailLength - consumed;
        if (rest > 0) Buffer.BlockCopy(_tail, consumed, _tail, 0, rest);
        _tailLength = rest;
    }

    private DecodeResult Run(ref BitReader br, Span<byte> output, ref int bytesWritten)
    {
        DecodeResult result = DecodeResult.Success;
        for (;;)
        {
            if (result != DecodeResult.Success)
            {
                if (result == DecodeResult.NeedsMoreInput)
                {
                    if (_ringBuffer != null)
                    {
                        DecodeResult r = WriteRingBuffer(output, ref bytesWritten, force: true);
                        if (r == DecodeResult.Error) return r;
                    }
                }
                return result;
            }

            switch (_state)
            {
                case State.Uninited:
                    result = DecodeWindowBits(ref br);
                    if (result != DecodeResult.Success) break;
                    if (_largeWindow)
                    {
                        _state = State.LargeWindowBits;
                        break;
                    }
                    _state = State.Initialize;
                    break;

                case State.LargeWindowBits:
                {
                    if (!br.TryReadBits(6, out uint bits))
                    {
                        result = DecodeResult.NeedsMoreInput;
                        break;
                    }
                    _windowBits = (int)(bits & 63);
                    if (_windowBits < Constants.LargeMinWindowBits || _windowBits > Constants.LargeMaxWindowBits)
                    {
                        result = Fail(BrotliDecoderError.WindowBits);
                        break;
                    }
                    _state = State.Initialize;
                    goto case State.Initialize;
                }

                case State.Initialize:
                    if (_windowBits > _maxWindowBits)
                    {
                        result = Fail(BrotliDecoderError.WindowTooLarge);
                        break;
                    }
                    _maxBackwardDistance = (1 << _windowBits) - Constants.WindowGap;
                    _state = State.MetablockBegin;
                    goto case State.MetablockBegin;

                case State.MetablockBegin:
                    MetablockBegin();
                    _state = State.MetablockHeader;
                    goto case State.MetablockHeader;

                case State.MetablockHeader:
                    result = DecodeMetablockLength(ref br);
                    if (result != DecodeResult.Success) break;
                    if (_isMetadata || _isUncompressed)
                    {
                        if (!br.JumpToByteBoundary())
                        {
                            result = Fail(BrotliDecoderError.Padding);
                            break;
                        }
                        br.Unload();
                    }
                    if (_isMetadata)
                    {
                        _state = State.Metadata;
                        break;
                    }
                    if (_metaBlockRemainingLen == 0)
                    {
                        _state = State.MetablockDone;
                        break;
                    }
                    if (_partialPosOut + UnwrittenBytes(false) + _metaBlockRemainingLen > _maxOutputLength)
                    {
                        result = Fail(BrotliDecoderError.OutputLimitExceeded);
                        break;
                    }
                    CalculateRingBufferSize();
                    if (_isUncompressed)
                    {
                        _state = State.Uncompressed;
                        break;
                    }
                    _state = State.BeforeCompressedMetablockHeader;
                    goto case State.BeforeCompressedMetablockHeader;

                case State.BeforeCompressedMetablockHeader:
                    _loopCounter = 0;
                    _subLoopCounter = 0;
                    _huffSubstate = HuffmanSubstate.None;
                    _treeGroupSubstate = TreeGroupSubstate.None;
                    _ctxMapSubstate = ContextMapSubstate.None;
                    _state = State.HuffmanCode0;
                    goto case State.HuffmanCode0;

                case State.HuffmanCode0:
                    if (_loopCounter >= 3)
                    {
                        _state = State.MetablockHeader2;
                        break;
                    }
                    result = DecodeVarLenUint8(ref br, out int nbt);
                    if (result != DecodeResult.Success) break;
                    _numBlockTypes[_loopCounter] = nbt + 1;
                    if (_numBlockTypes[_loopCounter] < 2)
                    {
                        _loopCounter++;
                        break;
                    }
                    _state = State.HuffmanCode1;
                    goto case State.HuffmanCode1;

                case State.HuffmanCode1:
                {
                    int alphabetSize = _numBlockTypes[_loopCounter] + 2;
                    int treeOffset = _loopCounter * Constants.HuffmanMaxSize258;
                    result = ReadHuffmanCode(ref br, alphabetSize, alphabetSize, _blockTypeTrees, treeOffset, out _);
                    if (result != DecodeResult.Success) break;
                    _state = State.HuffmanCode2;
                    goto case State.HuffmanCode2;
                }

                case State.HuffmanCode2:
                {
                    int alphabetSize = Constants.NumBlockLengthSymbols;
                    int treeOffset = _loopCounter * Constants.HuffmanMaxSize26;
                    result = ReadHuffmanCode(ref br, alphabetSize, alphabetSize, _blockLenTrees, treeOffset, out _);
                    if (result != DecodeResult.Success) break;
                    _state = State.HuffmanCode3;
                    goto case State.HuffmanCode3;
                }

                case State.HuffmanCode3:
                {
                    int treeOffset = _loopCounter * Constants.HuffmanMaxSize26;
                    if (!TryReadBlockLength(ref br, _blockLenTrees, treeOffset, out _blockLength[_loopCounter]))
                    {
                        result = DecodeResult.NeedsMoreInput;
                        break;
                    }
                    _loopCounter++;
                    _state = State.HuffmanCode0;
                    break;
                }

                case State.Uncompressed:
                    result = CopyUncompressedBlockToOutput(ref br, output, ref bytesWritten);
                    if (result != DecodeResult.Success) break;
                    _state = State.MetablockDone;
                    break;

                case State.Metadata:
                    result = SkipMetadataBlock(ref br);
                    if (result != DecodeResult.Success) break;
                    _state = State.MetablockDone;
                    break;

                case State.MetablockHeader2:
                {
                    if (!br.TryReadBits(6, out uint bits))
                    {
                        result = DecodeResult.NeedsMoreInput;
                        break;
                    }
                    _distancePostfixBits = (int)(bits & 3);
                    bits >>= 2;
                    _numDirectDistanceCodes = (int)(bits << _distancePostfixBits);
                    _loopCounter = 0;
                    _state = State.ContextModes;
                    goto case State.ContextModes;
                }

                case State.ContextModes:
                    result = ReadContextModes(ref br);
                    if (result != DecodeResult.Success) break;
                    _state = State.ContextMap1;
                    goto case State.ContextMap1;

                case State.ContextMap1:
                    result = DecodeContextMap(ref br, _numBlockTypes[0] << Constants.LiteralContextBits, ref _contextMap, ref _contextMapRented, out _numLiteralHtrees);
                    if (result != DecodeResult.Success) break;
                    DetectTrivialLiteralBlockTypes();
                    _state = State.ContextMap2;
                    goto case State.ContextMap2;

                case State.ContextMap2:
                {
                    int npostfix = _distancePostfixBits;
                    int ndirect = _numDirectDistanceCodes;
                    int distanceAlphabetSizeMax = Constants.DistanceAlphabetSize(npostfix, ndirect, Constants.MaxDistanceBits);
                    int distanceAlphabetSizeLimit = distanceAlphabetSizeMax;
                    if (_largeWindow)
                    {
                        distanceAlphabetSizeMax = Constants.DistanceAlphabetSize(npostfix, ndirect, Constants.LargeMaxDistanceBits);
                        distanceAlphabetSizeLimit = CalculateDistanceAlphabetLimit(Constants.MaxAllowedDistance, npostfix, ndirect);
                    }
                    result = DecodeContextMap(ref br, _numBlockTypes[2] << Constants.DistanceContextBits, ref _distContextMap, ref _distContextMapRented, out _numDistHtrees);
                    if (result != DecodeResult.Success) break;
                    _literalGroup.Adaptive = true;
                    _literalGroup.Init(Constants.NumLiteralSymbols, Constants.NumLiteralSymbols, _numLiteralHtrees);
                    _insertCopyGroup.Adaptive = true;
                    _insertCopyGroup.Init(Constants.NumCommandSymbols, Constants.NumCommandSymbols, _numBlockTypes[1]);
                    _distanceGroup.Init(distanceAlphabetSizeMax, distanceAlphabetSizeLimit, _numDistHtrees);
                    _loopCounter = 0;
                    _state = State.TreeGroup;
                    goto case State.TreeGroup;
                }

                case State.TreeGroup:
                {
                    HuffmanGroup group = _loopCounter switch
                    {
                        0 => _literalGroup,
                        1 => _insertCopyGroup,
                        _ => _distanceGroup,
                    };
                    result = HuffmanTreeGroupDecode(ref br, group);
                    if (result != DecodeResult.Success) break;
                    _loopCounter++;
                    if (_loopCounter < 3) break;
                    _state = State.BeforeCompressedMetablockBody;
                    goto case State.BeforeCompressedMetablockBody;
                }

                case State.BeforeCompressedMetablockBody:
                    PrepareLiteralDecoding();
                    _distContextMapSliceOffset = 0;
                    _htreeCommandOffset = _insertCopyGroup.HtreeOffsets[0];
                    EnsureRingBuffer();
                    CalculateDistanceLut();
                    _state = State.CommandBegin;
                    goto case State.CommandBegin;

                case State.CommandBegin:
                case State.CommandInner:
                case State.CommandPostDecodeLiterals:
                case State.CommandPostWrapCopy:
                    result = ProcessCommands(ref br);
                    break;

                case State.CommandInnerWrite:
                case State.CommandPostWrite1:
                case State.CommandPostWrite2:
                    result = WriteRingBuffer(output, ref bytesWritten, force: false);
                    if (result != DecodeResult.Success) break;
                    WrapRingBuffer();
                    if (_ringBufferSize == 1 << _windowBits)
                    {
                        _maxDistance = _maxBackwardDistance;
                    }
                    if (_state == State.CommandPostWrite1)
                    {
                        if (_dictCopyRemaining != 0)
                        {
                            _pos += CopyFromPrefixDictionary(_pos);
                            if (_pos >= _ringBufferSize) continue;
                        }
                        _state = _metaBlockRemainingLen == 0 ? State.MetablockDone : State.CommandBegin;
                        break;
                    }
                    else if (_state == State.CommandPostWrite2)
                    {
                        _state = State.CommandPostWrapCopy;
                    }
                    else
                    {
                        if (_loopCounter == 0)
                        {
                            _state = _metaBlockRemainingLen == 0 ? State.MetablockDone : State.CommandPostDecodeLiterals;
                            break;
                        }
                        _state = State.CommandInner;
                    }
                    break;

                case State.MetablockDone:
                    if (_metaBlockRemainingLen < 0)
                    {
                        result = Fail(BrotliDecoderError.BlockLength);
                        break;
                    }
                    if (!_isLastMetablock)
                    {
                        _state = State.MetablockBegin;
                        break;
                    }
                    if (!br.JumpToByteBoundary())
                    {
                        result = Fail(BrotliDecoderError.Padding);
                        break;
                    }
                    _state = State.Done;
                    goto case State.Done;

                case State.Done:
                    if (_ringBuffer != null)
                    {
                        result = WriteRingBuffer(output, ref bytesWritten, force: true);
                        if (result != DecodeResult.Success) break;
                    }
                    return DecodeResult.Success;

                case State.Failed:
                    return DecodeResult.Error;

                default:
                    return Fail(BrotliDecoderError.InvalidState);
            }
        }
    }

    // ------------------------------------------------------------------ header pieces

    private DecodeResult DecodeWindowBits(ref BitReader br)
    {
        // Reads 1..8 bits; the reference requires the whole header be available (it reads unsafely).
        br.Save(out BitReaderState saved);
        bool large = _allowLargeWindow;
        _largeWindow = false;
        if (!br.TryReadBits(1, out uint n)) goto more;
        if (n == 0)
        {
            _windowBits = 16;
            return DecodeResult.Success;
        }
        if (!br.TryReadBits(3, out n)) goto more;
        if (n != 0)
        {
            _windowBits = (int)(17 + n);
            return DecodeResult.Success;
        }
        if (!br.TryReadBits(3, out n)) goto more;
        if (n == 1)
        {
            if (large)
            {
                if (!br.TryReadBits(1, out n)) goto more;
                if (n == 1) return Fail(BrotliDecoderError.WindowBits);
                _largeWindow = true;
                return DecodeResult.Success;
            }
            return Fail(BrotliDecoderError.WindowBits);
        }
        if (n != 0)
        {
            _windowBits = (int)(8 + n);
            return DecodeResult.Success;
        }
        _windowBits = 17;
        return DecodeResult.Success;
    more:
        br.Restore(saved);
        return DecodeResult.NeedsMoreInput;
    }

    private void MetablockBegin()
    {
        _metaBlockRemainingLen = 0;
        _blockLength[0] = Constants.BlockSizeCap;
        _blockLength[1] = Constants.BlockSizeCap;
        _blockLength[2] = Constants.BlockSizeCap;
        _numBlockTypes[0] = 1;
        _numBlockTypes[1] = 1;
        _numBlockTypes[2] = 1;
        _blockTypeRb[0] = 1;
        _blockTypeRb[1] = 0;
        _blockTypeRb[2] = 1;
        _blockTypeRb[3] = 0;
        _blockTypeRb[4] = 1;
        _blockTypeRb[5] = 0;
        _distHtreeIndex = 0;
        _mbSubstate = MetablockHeaderSubstate.None;
    }

    private DecodeResult DecodeVarLenUint8(ref BitReader br, out int value)
    {
        uint bits;
        switch (_uint8Substate)
        {
            case Uint8Substate.None:
                if (!br.TryReadBits(1, out bits))
                {
                    value = 0;
                    return DecodeResult.NeedsMoreInput;
                }
                if (bits == 0)
                {
                    value = 0;
                    return DecodeResult.Success;
                }
                goto case Uint8Substate.Short;

            case Uint8Substate.Short:
                if (!br.TryReadBits(3, out bits))
                {
                    _uint8Substate = Uint8Substate.Short;
                    value = 0;
                    return DecodeResult.NeedsMoreInput;
                }
                if (bits == 0)
                {
                    value = 1;
                    _uint8Substate = Uint8Substate.None;
                    return DecodeResult.Success;
                }
                _varLenValue = (int)bits;
                goto case Uint8Substate.Long;

            case Uint8Substate.Long:
                if (!br.TryReadBits(_varLenValue, out bits))
                {
                    _uint8Substate = Uint8Substate.Long;
                    value = 0;
                    return DecodeResult.NeedsMoreInput;
                }
                value = (1 << _varLenValue) + (int)bits;
                _uint8Substate = Uint8Substate.None;
                return DecodeResult.Success;

            default:
                value = 0;
                return Fail(BrotliDecoderError.InvalidState);
        }
    }

    private DecodeResult DecodeMetablockLength(ref BitReader br)
    {
        uint bits;
        for (;;)
        {
            switch (_mbSubstate)
            {
                case MetablockHeaderSubstate.None:
                    if (!br.TryReadBits(1, out bits)) return DecodeResult.NeedsMoreInput;
                    _isLastMetablock = bits != 0;
                    _metaBlockRemainingLen = 0;
                    _isUncompressed = false;
                    _isMetadata = false;
                    if (!_isLastMetablock)
                    {
                        _mbSubstate = MetablockHeaderSubstate.Nibbles;
                        break;
                    }
                    _mbSubstate = MetablockHeaderSubstate.Empty;
                    goto case MetablockHeaderSubstate.Empty;

                case MetablockHeaderSubstate.Empty:
                    if (!br.TryReadBits(1, out bits)) return DecodeResult.NeedsMoreInput;
                    if (bits != 0)
                    {
                        _mbSubstate = MetablockHeaderSubstate.None;
                        return DecodeResult.Success;
                    }
                    _mbSubstate = MetablockHeaderSubstate.Nibbles;
                    goto case MetablockHeaderSubstate.Nibbles;

                case MetablockHeaderSubstate.Nibbles:
                    if (!br.TryReadBits(2, out bits)) return DecodeResult.NeedsMoreInput;
                    _sizeNibbles = (int)bits + 4;
                    _loopCounter = 0;
                    if (bits == 3)
                    {
                        _isMetadata = true;
                        _mbSubstate = MetablockHeaderSubstate.Reserved;
                        break;
                    }
                    _mbSubstate = MetablockHeaderSubstate.Size;
                    goto case MetablockHeaderSubstate.Size;

                case MetablockHeaderSubstate.Size:
                {
                    int i = _loopCounter;
                    for (; i < _sizeNibbles; ++i)
                    {
                        if (!br.TryReadBits(4, out bits))
                        {
                            _loopCounter = i;
                            return DecodeResult.NeedsMoreInput;
                        }
                        if (i + 1 == _sizeNibbles && _sizeNibbles > 4 && bits == 0)
                        {
                            return Fail(BrotliDecoderError.ExuberantNibble);
                        }
                        _metaBlockRemainingLen |= (int)(bits << (i * 4));
                    }
                    _mbSubstate = MetablockHeaderSubstate.Uncompressed;
                    goto case MetablockHeaderSubstate.Uncompressed;
                }

                case MetablockHeaderSubstate.Uncompressed:
                    if (!_isLastMetablock)
                    {
                        if (!br.TryReadBits(1, out bits)) return DecodeResult.NeedsMoreInput;
                        _isUncompressed = bits != 0;
                    }
                    ++_metaBlockRemainingLen;
                    _mbSubstate = MetablockHeaderSubstate.None;
                    return DecodeResult.Success;

                case MetablockHeaderSubstate.Reserved:
                    if (!br.TryReadBits(1, out bits)) return DecodeResult.NeedsMoreInput;
                    if (bits != 0) return Fail(BrotliDecoderError.Reserved);
                    _mbSubstate = MetablockHeaderSubstate.Bytes;
                    goto case MetablockHeaderSubstate.Bytes;

                case MetablockHeaderSubstate.Bytes:
                    if (!br.TryReadBits(2, out bits)) return DecodeResult.NeedsMoreInput;
                    if (bits == 0)
                    {
                        _mbSubstate = MetablockHeaderSubstate.None;
                        return DecodeResult.Success;
                    }
                    _sizeNibbles = (int)bits;
                    _mbSubstate = MetablockHeaderSubstate.Metadata;
                    goto case MetablockHeaderSubstate.Metadata;

                case MetablockHeaderSubstate.Metadata:
                {
                    int i = _loopCounter;
                    for (; i < _sizeNibbles; ++i)
                    {
                        if (!br.TryReadBits(8, out bits))
                        {
                            _loopCounter = i;
                            return DecodeResult.NeedsMoreInput;
                        }
                        if (i + 1 == _sizeNibbles && _sizeNibbles > 1 && bits == 0)
                        {
                            return Fail(BrotliDecoderError.ExuberantMetaNibble);
                        }
                        _metaBlockRemainingLen |= (int)(bits << (i * 8));
                    }
                    ++_metaBlockRemainingLen;
                    _mbSubstate = MetablockHeaderSubstate.None;
                    return DecodeResult.Success;
                }

                default:
                    return Fail(BrotliDecoderError.InvalidState);
            }
        }
    }

    // ------------------------------------------------------------------ prefix codes

    private DecodeResult ReadSimpleHuffmanSymbols(ref BitReader br, int alphabetSizeMax, int alphabetSizeLimit)
    {
        int maxBits = Log2Floor((uint)(alphabetSizeMax - 1));
        int i = _subLoopCounter;
        int numSymbols = _symbol;
        while (i <= numSymbols)
        {
            if (!br.TryReadBits(maxBits, out uint v))
            {
                _subLoopCounter = i;
                _huffSubstate = HuffmanSubstate.SimpleRead;
                return DecodeResult.NeedsMoreInput;
            }
            if (v >= (uint)alphabetSizeLimit) return Fail(BrotliDecoderError.SimpleHuffmanAlphabet);
            _simpleSymbols[i] = (ushort)v;
            ++i;
        }
        for (i = 0; i < numSymbols; ++i)
        {
            for (int k = i + 1; k <= numSymbols; ++k)
            {
                if (_simpleSymbols[i] == _simpleSymbols[k]) return Fail(BrotliDecoderError.SimpleHuffmanSame);
            }
        }
        return DecodeResult.Success;
    }

    private static int Log2Floor(uint x)
    {
        int result = 0;
        while (x != 0)
        {
            x >>= 1;
            ++result;
        }
        return result;
    }

    private void ProcessSingleCodeLength(int codeLen)
    {
        _repeat = 0;
        if (codeLen != 0)
        {
            _codeLengths[_symbol] = (byte)codeLen;
            _prevCodeLen = codeLen;
            _space -= 32768u >> codeLen;
            _codeLengthHisto[codeLen]++;
        }
        _symbol++;
    }

    private void ProcessRepeatedCodeLength(int codeLen, int repeatDelta, int alphabetSize)
    {
        int extraBits = 3;
        int newLen = 0;
        if (codeLen == Constants.RepeatPreviousCodeLength)
        {
            newLen = _prevCodeLen;
            extraBits = 2;
        }
        if (_repeatCodeLen != newLen)
        {
            _repeat = 0;
            _repeatCodeLen = newLen;
        }
        int oldRepeat = _repeat;
        if (_repeat > 0)
        {
            _repeat -= 2;
            _repeat <<= extraBits;
        }
        _repeat += repeatDelta + 3;
        repeatDelta = _repeat - oldRepeat;
        if (_symbol + repeatDelta > alphabetSize)
        {
            _symbol = alphabetSize;
            _space = 0xFFFFF;
            return;
        }
        if (_repeatCodeLen != 0)
        {
            int last = _symbol + repeatDelta;
            for (int s = _symbol; s < last; s++) _codeLengths[s] = (byte)_repeatCodeLen;
            _symbol = last;
            _space -= (uint)repeatDelta << (15 - _repeatCodeLen);
            _codeLengthHisto[_repeatCodeLen] += repeatDelta;
        }
        else
        {
            _symbol += repeatDelta;
        }
    }

    private DecodeResult ReadSymbolCodeLengths(ref BitReader br, int alphabetSize)
    {
        while (_symbol < alphabetSize && _space > 0)
        {
            int available = br.PeekBitsLenient(out uint bits);
            uint p = _clcTable[(int)(bits & ((1u << Constants.HuffmanMaxCodeLengthCodeLength) - 1))];
            int pBits = (int)(p >> 16);
            if (pBits > available) return DecodeResult.NeedsMoreInput;
            int codeLen = (int)(p & 0xFFFF);
            if (codeLen < Constants.RepeatPreviousCodeLength)
            {
                br.DropBits(pBits);
                ProcessSingleCodeLength(codeLen);
            }
            else
            {
                int extraBits = codeLen - 14;
                int repeatDelta = (int)((bits >> pBits) & ((1u << extraBits) - 1));
                if (available < pBits + extraBits) return DecodeResult.NeedsMoreInput;
                br.DropBits(pBits + extraBits);
                ProcessRepeatedCodeLength(codeLen, repeatDelta, alphabetSize);
            }
        }
        return DecodeResult.Success;
    }

    private DecodeResult ReadCodeLengthCodeLengths(ref BitReader br)
    {
        int numCodes = _repeat;
        uint space = _space;
        int i = _subLoopCounter;
        ReadOnlySpan<byte> order = Constants.CodeLengthCodeOrder;
        ReadOnlySpan<byte> prefixLength = Constants.CodeLengthPrefixLength;
        ReadOnlySpan<byte> prefixValue = Constants.CodeLengthPrefixValue;
        for (; i < Constants.CodeLengthCodes; ++i)
        {
            int codeLenIdx = order[i];
            int available = br.PeekBitsLenient(out uint bits);
            uint ix = bits & 0xF;
            if (prefixLength[(int)ix] > available)
            {
                _subLoopCounter = i;
                _repeat = numCodes;
                _space = space;
                _huffSubstate = HuffmanSubstate.Complex;
                return DecodeResult.NeedsMoreInput;
            }
            int v = prefixValue[(int)ix];
            br.DropBits(prefixLength[(int)ix]);
            _codeLengthCodeLengths[codeLenIdx] = (byte)v;
            if (v != 0)
            {
                space -= 32u >> v;
                ++numCodes;
                ++_codeLengthHisto[v];
                if (space - 1u >= 32u)
                {
                    break;
                }
            }
        }
        if (!(numCodes == 1 || space == 0)) return Fail(BrotliDecoderError.CodeLengthSpace);
        return DecodeResult.Success;
    }

    /// <summary>Reads a prefix code into <paramref name="table"/> at <paramref name="tableOffset"/>.</summary>
    // saveTo/saveIndex: an adaptive group records the tree so it can rebuild it with another root width.
    private DecodeResult ReadHuffmanCode(ref BitReader br, int alphabetSizeMax, int alphabetSizeLimit, uint[] table, int tableOffset, out int tableSize, HuffmanGroup? saveTo = null, int saveIndex = 0)
    {
        tableSize = 0;
        for (;;)
        {
            switch (_huffSubstate)
            {
                case HuffmanSubstate.None:
                {
                    if (!br.TryReadBits(2, out uint v)) return DecodeResult.NeedsMoreInput;
                    _subLoopCounter = (int)v;
                    if (_subLoopCounter != 1)
                    {
                        _space = 32;
                        _repeat = 0;
                        Array.Clear(_codeLengthHisto, 0, _codeLengthHisto.Length);
                        Array.Clear(_codeLengthCodeLengths, 0, _codeLengthCodeLengths.Length);
                        _huffSubstate = HuffmanSubstate.Complex;
                        continue;
                    }
                    goto case HuffmanSubstate.SimpleSize;
                }

                case HuffmanSubstate.SimpleSize:
                {
                    if (!br.TryReadBits(2, out uint v))
                    {
                        _huffSubstate = HuffmanSubstate.SimpleSize;
                        return DecodeResult.NeedsMoreInput;
                    }
                    _symbol = (int)v;
                    _subLoopCounter = 0;
                    goto case HuffmanSubstate.SimpleRead;
                }

                case HuffmanSubstate.SimpleRead:
                {
                    DecodeResult r = ReadSimpleHuffmanSymbols(ref br, alphabetSizeMax, alphabetSizeLimit);
                    if (r != DecodeResult.Success) return r;
                    goto case HuffmanSubstate.SimpleBuild;
                }

                case HuffmanSubstate.SimpleBuild:
                {
                    if (_symbol == 3)
                    {
                        if (!br.TryReadBits(1, out uint bits))
                        {
                            _huffSubstate = HuffmanSubstate.SimpleBuild;
                            return DecodeResult.NeedsMoreInput;
                        }
                        _symbol += (int)bits;
                    }
                    if (saveTo != null)
                    {
                        _simpleSymbols.AsSpan(0, 4).CopyTo(saveTo.SavedSymbols.AsSpan(saveIndex * saveTo.AlphabetSizeLimit));
                        saveTo.SavedKind[saveIndex] = _symbol;
                        saveTo.LongMass[saveIndex] = 0;
                    }
                    tableSize = HuffmanTable.BuildSimpleTable(table.AsSpan(tableOffset), HuffmanTable.RootBits, _simpleSymbols, _symbol);
                    _huffSubstate = HuffmanSubstate.None;
                    return DecodeResult.Success;
                }

                case HuffmanSubstate.Complex:
                {
                    DecodeResult r = ReadCodeLengthCodeLengths(ref br);
                    if (r != DecodeResult.Success) return r;
                    HuffmanTable.BuildCodeLengthsTable(_clcTable, _codeLengthCodeLengths, _codeLengthHisto);
                    Array.Clear(_codeLengthHisto, 0, _codeLengthHisto.Length);
                    Array.Clear(_codeLengths, 0, alphabetSizeLimit);
                    _symbol = 0;
                    _prevCodeLen = Constants.InitialRepeatedCodeLength;
                    _repeat = 0;
                    _repeatCodeLen = 0;
                    _space = 32768;
                    _huffSubstate = HuffmanSubstate.LengthSymbols;
                    goto case HuffmanSubstate.LengthSymbols;
                }

                case HuffmanSubstate.LengthSymbols:
                {
                    DecodeResult r = ReadSymbolCodeLengths(ref br, alphabetSizeLimit);
                    if (r != DecodeResult.Success) return r;
                    if (_space != 0) return Fail(BrotliDecoderError.HuffmanSpace);
                    // Counting sort of symbols by (length, symbol).
                    Span<int> offsets = stackalloc int[Constants.HuffmanMaxCodeLength + 2];
                    int acc = 0;
                    for (int len = 1; len <= Constants.HuffmanMaxCodeLength; len++)
                    {
                        offsets[len] = acc;
                        acc += _codeLengthHisto[len];
                    }
                    for (int s = 0; s < alphabetSizeLimit; s++)
                    {
                        int len = _codeLengths[s];
                        if (len != 0) _sortedSymbols[offsets[len]++] = (ushort)s;
                    }
                    Span<int> count = stackalloc int[Constants.HuffmanMaxCodeLength + 1];
                    for (int len = 0; len <= Constants.HuffmanMaxCodeLength; len++) count[len] = _codeLengthHisto[len];
                    if (saveTo != null)
                    {
                        _sortedSymbols.AsSpan(0, acc).CopyTo(saveTo.SavedSymbols.AsSpan(saveIndex * saveTo.AlphabetSizeLimit));
                        count.CopyTo(saveTo.SavedCounts.AsSpan(saveIndex * (Constants.HuffmanMaxCodeLength + 1)));
                        saveTo.SavedKind[saveIndex] = -1;
                        saveTo.LongMass[saveIndex] = HuffmanTable.LongMass(count);
                    }
                    tableSize = HuffmanTable.BuildTable(table.AsSpan(tableOffset), HuffmanTable.RootBits, _sortedSymbols, count);
                    _huffSubstate = HuffmanSubstate.None;
                    return DecodeResult.Success;
                }

                default:
                    return Fail(BrotliDecoderError.InvalidState);
            }
        }
    }

    private bool TryReadBlockLength(ref BitReader br, uint[] table, int tableOffset, out int result)
    {
        int index;
        if (_blockLengthSubstate == BlockLengthSubstate.None)
        {
            if (!br.TryReadSymbol(table, tableOffset, HuffmanTable.RootBits, out index))
            {
                result = 0;
                return false;
            }
        }
        else
        {
            index = _blockLengthIndex;
        }
        int nbits = Constants.BlockLengthExtraBits[index];
        int offset = Constants.BlockLengthOffset[index];
        if (!br.TryReadBits(nbits, out uint bits))
        {
            // Keep the symbol so the suffix can be resumed (mirrors reference behaviour).
            _blockLengthIndex = index;
            _blockLengthSubstate = BlockLengthSubstate.Suffix;
            result = 0;
            return false;
        }
        result = offset + (int)bits;
        _blockLengthSubstate = BlockLengthSubstate.None;
        return true;
    }

    private void InverseMoveToFrontTransform(byte[] v, int vLen)
    {
        int upperBound = _mtfUpperBound;
        byte[] mtf = _mtf; // mtf[1..] holds the list; mtf[0] is a scratch slot so index -1 is addressable.
        int init = (upperBound + 1) * 4;
        if (init > 256) init = 256;
        for (int i = 0; i < init; i++) mtf[1 + i] = (byte)i;
        upperBound = 0;
        for (int i = 0; i < vLen; ++i)
        {
            int index = v[i];
            byte value = mtf[1 + index];
            upperBound |= v[i];
            v[i] = value;
            mtf[0] = value;
            do
            {
                index--;
                mtf[1 + index + 1] = mtf[1 + index];
            } while (index >= 0);
        }
        _mtfUpperBound = upperBound >> 2;
    }

    private DecodeResult HuffmanTreeGroupDecode(ref BitReader br, HuffmanGroup group)
    {
        if (_treeGroupSubstate != TreeGroupSubstate.Loop)
        {
            _nextTableOffset = 0;
            _htreeIndex = 0;
            _treeGroupSubstate = TreeGroupSubstate.Loop;
        }
        while (_htreeIndex < group.NumHtrees)
        {
            DecodeResult r = ReadHuffmanCode(ref br, group.AlphabetSizeMax, group.AlphabetSizeLimit, group.Codes, _nextTableOffset, out int tableSize, group.Adaptive ? group : null, _htreeIndex);
            if (r != DecodeResult.Success) return r;
            group.HtreeOffsets[_htreeIndex] = _nextTableOffset;
            _nextTableOffset += tableSize;
            ++_htreeIndex;
        }
        // Width for the whole group (the hot loop is specialised on it): rebuild with the 10-bit root when the
        // saved trees say it pays.
        group.RootBits = HuffmanTable.RootBits;
        if (group.Adaptive)
        {
            int maxLongMass = 0;
            for (int t = 0; t < group.NumHtrees; t++) maxLongMass = Math.Max(maxLongMass, group.LongMass[t]);
            int width = HuffmanTable.ChooseGroupRootBits(maxLongMass, group.NumHtrees);
            if (width != HuffmanTable.RootBits)
            {
                group.RootBits = width;
                group.Rebuild();
            }
        }
        _treeGroupSubstate = TreeGroupSubstate.None;
        return DecodeResult.Success;
    }

    private DecodeResult DecodeContextMap(ref BitReader br, int contextMapSize, ref byte[] contextMap, ref bool rented, out int numHtrees)
    {
        numHtrees = _numHtreesTmp;
        switch (_ctxMapSubstate)
        {
            case ContextMapSubstate.None:
            {
                DecodeResult r = DecodeVarLenUint8(ref br, out int n);
                if (r != DecodeResult.Success) return r;
                numHtrees = n + 1;
                _numHtreesTmp = numHtrees;
                _contextIndex = 0;
                if (contextMap.Length < contextMapSize)
                {
                    if (rented) ArrayPool<byte>.Shared.Return(contextMap);
                    contextMap = ArrayPool<byte>.Shared.Rent(contextMapSize);
                    rented = true;
                }
                if (numHtrees <= 1)
                {
                    Array.Clear(contextMap, 0, contextMapSize);
                    return DecodeResult.Success;
                }
                _ctxMapSubstate = ContextMapSubstate.ReadPrefix;
                goto case ContextMapSubstate.ReadPrefix;
            }

            case ContextMapSubstate.ReadPrefix:
            {
                if (!br.TryPeekBits(5, out uint bits)) return DecodeResult.NeedsMoreInput;
                if ((bits & 1) != 0)
                {
                    _maxRunLengthPrefix = (int)(bits >> 1) + 1;
                    br.DropBits(5);
                }
                else
                {
                    _maxRunLengthPrefix = 0;
                    br.DropBits(1);
                }
                _ctxMapSubstate = ContextMapSubstate.Huffman;
                goto case ContextMapSubstate.Huffman;
            }

            case ContextMapSubstate.Huffman:
            {
                int alphabetSize = numHtrees + _maxRunLengthPrefix;
                DecodeResult r = ReadHuffmanCode(ref br, alphabetSize, alphabetSize, _contextMapTable, 0, out _);
                if (r != DecodeResult.Success) return r;
                _ctxCode = 0xFFFF;
                _ctxMapSubstate = ContextMapSubstate.Decode;
                goto case ContextMapSubstate.Decode;
            }

            case ContextMapSubstate.Decode:
            {
                int contextIndex = _contextIndex;
                int maxRunLengthPrefix = _maxRunLengthPrefix;
                int code = _ctxCode;
                bool skipPreamble = code != 0xFFFF;
                while (contextIndex < contextMapSize || skipPreamble)
                {
                    if (!skipPreamble)
                    {
                        if (!br.TryReadSymbol(_contextMapTable, 0, HuffmanTable.RootBits, out code))
                        {
                            _ctxCode = 0xFFFF;
                            _contextIndex = contextIndex;
                            return DecodeResult.NeedsMoreInput;
                        }
                        if (code == 0)
                        {
                            contextMap[contextIndex++] = 0;
                            continue;
                        }
                        if (code > maxRunLengthPrefix)
                        {
                            contextMap[contextIndex++] = (byte)(code - maxRunLengthPrefix);
                            continue;
                        }
                    }
                    else
                    {
                        skipPreamble = false;
                    }
                    if (!br.TryReadBits(code, out uint reps))
                    {
                        _ctxCode = code;
                        _contextIndex = contextIndex;
                        return DecodeResult.NeedsMoreInput;
                    }
                    int repsCount = (int)reps + (1 << code);
                    if (contextIndex + repsCount > contextMapSize) return Fail(BrotliDecoderError.ContextMapRepeat);
                    do
                    {
                        contextMap[contextIndex++] = 0;
                    } while (--repsCount != 0);
                }
                _contextIndex = contextIndex;
                _ctxMapSubstate = ContextMapSubstate.Transform;
                goto case ContextMapSubstate.Transform;
            }

            case ContextMapSubstate.Transform:
            {
                if (!br.TryReadBits(1, out uint bits))
                {
                    _ctxMapSubstate = ContextMapSubstate.Transform;
                    return DecodeResult.NeedsMoreInput;
                }
                if (bits != 0) InverseMoveToFrontTransform(contextMap, contextMapSize);
                _ctxMapSubstate = ContextMapSubstate.None;
                return DecodeResult.Success;
            }

            default:
                return Fail(BrotliDecoderError.InvalidState);
        }
    }

    private DecodeResult ReadContextModes(ref BitReader br)
    {
        int i = _loopCounter;
        while (i < _numBlockTypes[0])
        {
            if (!br.TryReadBits(2, out uint bits))
            {
                _loopCounter = i;
                return DecodeResult.NeedsMoreInput;
            }
            _contextModes[i] = (byte)bits;
            i++;
        }
        return DecodeResult.Success;
    }

    private void DetectTrivialLiteralBlockTypes()
    {
        for (int i = 0; i < 8; ++i) _trivialLiteralContexts[i] = 0;
        for (int i = 0; i < _numBlockTypes[0]; i++)
        {
            int offset = i << Constants.LiteralContextBits;
            int error = 0;
            int sample = _contextMap[offset];
            for (int j = 0; j < (1 << Constants.LiteralContextBits); j++)
            {
                error |= _contextMap[offset + j] ^ sample;
            }
            if (error == 0) _trivialLiteralContexts[i >> 5] |= 1u << (i & 31);
        }
    }

    private void PrepareLiteralDecoding()
    {
        int blockType = _blockTypeRb[1];
        int contextOffset = blockType << Constants.LiteralContextBits;
        uint trivial = _trivialLiteralContexts[blockType >> 5];
        _trivialLiteralContext = ((trivial >> (blockType & 31)) & 1) != 0;
        _literalHtreeOffset = _literalGroup.HtreeOffsets[_contextMap[contextOffset]];
        if (!_trivialLiteralContext)
        {
            int[] offsets = _literalGroup.HtreeOffsets;
            for (int c = 0; c < 1 << Constants.LiteralContextBits; ++c)
            {
                _literalCtxTree[c] = offsets[_contextMap[contextOffset + c]];
            }
        }
        int contextMode = _contextModes[blockType] & 3;
        _contextLookupOffset = contextMode << 9;
    }

    private static int CalculateDistanceAlphabetLimit(int maxDistance, int npostfix, int ndirect)
    {
        // Port of BrotliCalculateDistanceCodeLimit().max_alphabet_size.
        if (maxDistance <= ndirect)
        {
            return maxDistance + Constants.NumDistanceShortCodes;
        }
        uint forbiddenDistance = (uint)maxDistance + 1;
        uint offset = forbiddenDistance - (uint)ndirect - 1;
        uint ndistbits = 0;
        uint postfix = (1u << npostfix) - 1;
        offset = (offset >> npostfix) + 4;
        uint tmp = offset / 2;
        while (tmp != 0) { ndistbits++; tmp >>= 1; }
        ndistbits--;
        uint half = (offset >> (int)ndistbits) & 1;
        uint group = ((ndistbits - 1) << 1) | half;
        if (group == 0)
        {
            return ndirect + Constants.NumDistanceShortCodes;
        }
        group--;
        return (int)(((group << npostfix) | postfix) + (uint)ndirect + Constants.NumDistanceShortCodes + 1);
    }

    private void CalculateDistanceLut()
    {
        int npostfix = _distancePostfixBits;
        int ndirect = _numDirectDistanceCodes;
        int alphabetSizeLimit = _distanceGroup.AlphabetSizeLimit;
        int postfix = 1 << npostfix;
        int bits = 1;
        int half = 0;
        int i = Constants.NumDistanceShortCodes;
        for (int j = 0; j < ndirect; ++j)
        {
            _distExtraBits[i] = 0;
            _distOffset[i] = j + 1;
            ++i;
        }
        while (i < alphabetSizeLimit)
        {
            int base_ = ndirect + ((((2 + half) << bits) - 4) << npostfix) + 1;
            for (int j = 0; j < postfix && i < _distExtraBits.Length; ++j)
            {
                _distExtraBits[i] = (byte)bits;
                _distOffset[i] = base_ + j;
                ++i;
            }
            bits += half;
            half ^= 1;
        }
    }

    // ------------------------------------------------------------------ block switches

    private DecodeResult DecodeBlockTypeAndLength(ref BitReader br, int treeType)
    {
        int maxBlockType = _numBlockTypes[treeType];
        int typeTreeOffset = treeType * Constants.HuffmanMaxSize258;
        int lenTreeOffset = treeType * Constants.HuffmanMaxSize26;
        if (maxBlockType <= 1) return Fail(BrotliDecoderError.BlockSwitch);

        br.Save(out BitReaderState memento);
        if (!br.TryReadSymbol(_blockTypeTrees, typeTreeOffset, HuffmanTable.RootBits, out int blockType)) return DecodeResult.NeedsMoreInput;
        if (!TryReadBlockLength(ref br, _blockLenTrees, lenTreeOffset, out _blockLength[treeType]))
        {
            _blockLengthSubstate = BlockLengthSubstate.None;
            br.Restore(memento);
            return DecodeResult.NeedsMoreInput;
        }

        int rb = treeType * 2;
        if (blockType == 1)
        {
            blockType = _blockTypeRb[rb + 1] + 1;
        }
        else if (blockType == 0)
        {
            blockType = _blockTypeRb[rb];
        }
        else
        {
            blockType -= 2;
        }
        if (blockType >= maxBlockType) blockType -= maxBlockType;
        _blockTypeRb[rb] = _blockTypeRb[rb + 1];
        _blockTypeRb[rb + 1] = blockType;
        return DecodeResult.Success;
    }

    private DecodeResult DecodeLiteralBlockSwitch(ref BitReader br)
    {
        DecodeResult r = DecodeBlockTypeAndLength(ref br, 0);
        if (r != DecodeResult.Success) return r;
        PrepareLiteralDecoding();
        return DecodeResult.Success;
    }

    private DecodeResult DecodeCommandBlockSwitch(ref BitReader br)
    {
        DecodeResult r = DecodeBlockTypeAndLength(ref br, 1);
        if (r != DecodeResult.Success) return r;
        _htreeCommandOffset = _insertCopyGroup.HtreeOffsets[_blockTypeRb[3]];
        return DecodeResult.Success;
    }

    private DecodeResult DecodeDistanceBlockSwitch(ref BitReader br)
    {
        DecodeResult r = DecodeBlockTypeAndLength(ref br, 2);
        if (r != DecodeResult.Success) return r;
        _distContextMapSliceOffset = _blockTypeRb[5] << Constants.DistanceContextBits;
        _distHtreeIndex = _distContextMap[_distContextMapSliceOffset + _distanceContext];
        return DecodeResult.Success;
    }

    // ------------------------------------------------------------------ ring buffer / output

    private long UnwrittenBytes(bool wrap)
    {
        long pos = wrap && _pos > _ringBufferSize ? _ringBufferSize : _pos;
        long partialPosRb = _rbRoundtrips * _ringBufferSize + pos;
        return partialPosRb - _partialPosOut;
    }

    private DecodeResult WriteRingBuffer(Span<byte> output, ref int bytesWritten, bool force)
    {
        byte[] rb = _ringBuffer!;
        int start = (int)(_partialPosOut & _ringBufferMask);
        long toWrite = UnwrittenBytes(true);
        int available = output.Length - bytesWritten;
        int numWritten = (int)Math.Min(available, toWrite);
        if (_metaBlockRemainingLen < 0) return Fail(BrotliDecoderError.BlockLength);
        if (numWritten > 0)
        {
            rb.AsSpan(start, numWritten).CopyTo(output.Slice(bytesWritten));
            bytesWritten += numWritten;
            _partialPosOut += numWritten;
        }
        if (numWritten < toWrite)
        {
            if (_ringBufferSize == (1 << _windowBits) || force)
            {
                return DecodeResult.NeedsMoreOutput;
            }
            return DecodeResult.Success;
        }
        if (_ringBufferSize == (1 << _windowBits) && _pos >= _ringBufferSize)
        {
            _pos -= _ringBufferSize;
            _rbRoundtrips++;
            _shouldWrapRingBuffer = _pos != 0;
        }
        return DecodeResult.Success;
    }

    private void WrapRingBuffer()
    {
        if (_shouldWrapRingBuffer)
        {
            Buffer.BlockCopy(_ringBuffer!, _ringBufferSize, _ringBuffer!, 0, _pos);
            _shouldWrapRingBuffer = false;
        }
    }

    private void EnsureRingBuffer()
    {
        if (_ringBufferSize == _newRingBufferSize && _ringBuffer != null) return;
        byte[] old = _ringBuffer!;
        byte[] nb = _pool.Rent(_newRingBufferSize + RingBufferSlack);
        nb[_newRingBufferSize - 2] = 0;
        nb[_newRingBufferSize - 1] = 0;
        if (old != null)
        {
            Buffer.BlockCopy(old, 0, nb, 0, _pos);
            _pool.Return(old);
        }
        _ringBuffer = nb;
        _ringBufferSize = _newRingBufferSize;
        _ringBufferMask = _newRingBufferSize - 1;
    }

    private int _outputHint;

    private void CalculateRingBufferSize()
    {
        int windowSize = 1 << _windowBits;
        int newRingBufferSize = windowSize;
        int minSize = _ringBufferSize != 0 ? _ringBufferSize : 1024;
        if (_ringBufferSize == windowSize) return;
        if (_isMetadata) return;
        long outputSize = _ringBuffer == null ? 0 : _pos;
        outputSize += _metaBlockRemainingLen;
        if (outputSize < _outputHint) outputSize = _outputHint;
        if (minSize < outputSize) minSize = (int)Math.Min(outputSize, windowSize);
        while ((newRingBufferSize >> 1) >= minSize) newRingBufferSize >>= 1;
        _newRingBufferSize = newRingBufferSize;
    }

    private DecodeResult SkipMetadataBlock(ref BitReader br)
    {
        if (_metaBlockRemainingLen == 0) return DecodeResult.Success;
        // Accumulator is byte-aligned and unloaded here: read raw bytes.
        int n = br.SkipBytes(_metaBlockRemainingLen);
        _metaBlockRemainingLen -= n;
        if (_metaBlockRemainingLen == 0) return DecodeResult.Success;
        return DecodeResult.NeedsMoreInput;
    }

    private DecodeResult CopyUncompressedBlockToOutput(ref BitReader br, Span<byte> output, ref int bytesWritten)
    {
        EnsureRingBuffer();
        for (;;)
        {
            // Accumulator is byte-aligned and unloaded: raw bytes come straight from the input.
            int nbytes = Math.Min(br.RemainingBytes, _metaBlockRemainingLen);
            if (_pos + nbytes > _ringBufferSize) nbytes = _ringBufferSize - _pos;
            if (nbytes > 0)
            {
                br.CopyBytes(_ringBuffer.AsSpan(_pos, nbytes), nbytes);
                _pos += nbytes;
                _metaBlockRemainingLen -= nbytes;
            }
            if (_pos < 1 << _windowBits)
            {
                if (_metaBlockRemainingLen == 0) return DecodeResult.Success;
                return DecodeResult.NeedsMoreInput;
            }
            DecodeResult r = WriteRingBuffer(output, ref bytesWritten, force: false);
            if (r != DecodeResult.Success) return r;
            if (_ringBufferSize == 1 << _windowBits) _maxDistance = _maxBackwardDistance;
        }
    }

    // ------------------------------------------------------------------ distance decoding

    private void TakeDistanceFromRingBuffer()
    {
        int offset = _distanceCode - 3;
        if (_distanceCode <= 3)
        {
            _distanceContext = 1 >> _distanceCode;
            _distanceCode = _distRb[(_distRbIdx - offset) & 3];
            _distRbIdx -= _distanceContext;
        }
        else
        {
            int indexDelta = 3;
            int base_ = _distanceCode - 10;
            if (_distanceCode < 10)
            {
                base_ = _distanceCode - 4;
            }
            else
            {
                indexDelta = 2;
            }
            int delta = ((0x605142 >> (4 * base_)) & 0xF) - 3;
            _distanceCode = _distRb[(_distRbIdx + indexDelta) & 3] + delta;
            if (_distanceCode <= 0)
            {
                _distanceCode = 0x7FFFFFFF;
            }
        }
    }

    private bool TryReadDistance(ref BitReader br)
    {
        br.Save(out BitReaderState memento);
        int treeOffset = _distanceGroup.HtreeOffsets[_distHtreeIndex];
        if (!br.TryReadSymbol(_distanceGroup.Codes, treeOffset, _distanceGroup.RootBits, out int code)) return false;
        --_blockLength[2];
        _distanceContext = 0;
        if ((code & ~0xF) == 0)
        {
            _distanceCode = code;
            TakeDistanceFromRingBuffer();
            return true;
        }
        if (!br.TryReadBits(_distExtraBits[code], out uint bits))
        {
            ++_blockLength[2];
            br.Restore(memento);
            return false;
        }
        _distanceCode = (int)((uint)_distOffset[code] + (bits << _distancePostfixBits));
        return true;
    }

    private bool TryReadCommand(ref BitReader br, out int insertLength)
    {
        br.Save(out BitReaderState memento);
        insertLength = 0;
        if (!br.TryReadSymbol(_insertCopyGroup.Codes, _htreeCommandOffset, _insertCopyGroup.RootBits, out int cmdCode)) return false;
        CommandLut.Entry v = CommandLut.Table[cmdCode];
        _distanceCode = v.DistanceCode;
        _distanceContext = v.Context;
        _distHtreeIndex = _distContextMap[_distContextMapSliceOffset + _distanceContext];
        insertLength = v.InsertOffset;
        if (!br.TryReadBits(v.InsertExtraBits, out uint insertExtra) || !br.TryReadBits(v.CopyExtraBits, out uint copyExtra))
        {
            br.Restore(memento);
            return false;
        }
        _copyLength = (int)copyExtra + v.CopyOffset;
        --_blockLength[1];
        insertLength += (int)insertExtra;
        return true;
    }

    // ------------------------------------------------------------------ command loop

    /// <summary>Unread input bytes required before a command or distance is read without checks.</summary>
    private const int FastCommandBytes = 32;

    /// <summary>
    /// Copies <paramref name="len"/> bytes from <paramref name="pos"/> - <paramref name="distance"/> to <paramref name="pos"/>
    /// inside the ring buffer with 16-byte stores. May write up to 15 bytes past the end of the copy; the caller
    /// guarantees that the copy plus 16 bytes stays below the ring-buffer size (those bytes are outside the
    /// reachable window, see WindowGap) and that the source does not wrap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyWithin(ref byte rb, int pos, int distance, int len)
    {
#if NET7_0_OR_GREATER
        ref byte dst = ref Unsafe.Add(ref rb, pos);
        ref byte src = ref Unsafe.Add(ref rb, pos - distance);
        if (distance >= 16)
        {
            int k = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, k), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref src, k)));
                k += 16;
            } while (k < len);
            return;
        }
        // Short distance: establish a 32-byte periodic region with stores stepping by the distance, then
        // continue with 16-byte chunks from a period-aligned offset at least 16 bytes back.
        int written = 0;
        while (written < len && written < 32)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, written), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref dst, written - distance)));
            written += distance;
        }
        if (written >= len) return;
        int delta = (32 / distance) * distance;
        do
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, written), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref dst, written - delta)));
            written += 16;
        } while (written < len);
#else
        for (int k = 0; k < len; k++)
        {
            Unsafe.Add(ref rb, pos + k) = Unsafe.Add(ref rb, pos + k - distance);
        }
#endif
    }

    // ------------------------------------------------------------------ fast command loop

    /// <summary>Unread input bytes required to run a whole command (symbol, extras, distance) without checks.</summary>
    private const int FastLoopGuard = 64;

    // Fast-loop bit window: |bitbuf| holds the next stream bit at bit 0 and |bitsleft| valid bits; the bits
    // above |bitsleft| are whatever the last refill loaded (they are the following stream bytes, so a later
    // refill OR-ing the same bytes in again is harmless). Refill is branch-free: it always loads eight
    // bytes, advances the input by the whole bytes that fit and leaves 56 to 63 valid bits.

    /// <summary>Loads the window from the reader. The reader never holds garbage above its count, but may hold 64 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadWindow(ref BitReader br, out ulong bitbuf, out int bitsleft, out int inPos)
    {
        bitbuf = br.Acc;
        bitsleft = br.AccBits;
        inPos = br.Pos;
        if (bitsleft == 64)
        {
            // A full accumulator holds exactly eight whole input bytes: hand the last one back so the
            // window never exceeds 63 bits (the refill shifts by bitsleft).
            bitsleft = 56;
            bitbuf &= (1UL << 56) - 1;
            inPos--;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreWindow(ref BitReader br, ulong bitbuf, int bitsleft, int inPos)
    {
        br.SetState(bitbuf & ((1UL << bitsleft) - 1), bitsleft, inPos);
    }

    /// <summary>Branch-free refill to at least 56 bits. Requires eight readable bytes at <paramref name="inPos"/> and bitsleft &lt;= 63.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill(ref byte input, ref ulong bitbuf, ref int bitsleft, ref int inPos)
    {
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, inPos));
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        bitbuf |= v << bitsleft;
        inPos += (63 - bitsleft) >> 3;
        bitsleft |= 56;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeSymbol(ref uint root, int rootBits, uint rootMask, ref ulong bitbuf, ref int bitsleft)
    {
        uint bits = (uint)bitbuf;
        uint e = Unsafe.Add(ref root, (int)(bits & rootMask));
        int len = (int)(e >> 16);
        if (len > rootBits)
        {
            int nbits = len - rootBits;
            e = Unsafe.Add(ref root, (int)(bits & rootMask) + (int)(e & 0xFFFF) + (int)((bits >> rootBits) & ((1u << nbits) - 1)));
            len = rootBits + (int)(e >> 16);
        }
        bitbuf >>= len;
        bitsleft -= len;
        return (int)(e & 0xFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadBits(int n, ref ulong bitbuf, ref int bitsleft)
    {
        uint value = (uint)(bitbuf & ((1UL << n) - 1));
        bitbuf >>= n;
        bitsleft -= n;
        return value;
    }

    /// <summary>
    /// Runs commands with every piece of state in locals while the input is plentiful and nothing unusual
    /// (block switch, ring-buffer wrap, dictionary word, input tail) happens. Persists the state and returns
    /// when it cannot continue; the checked loop then resumes from <see cref="_state"/>.
    /// Entered only in <see cref="State.CommandBegin"/>.
    /// </summary>
    private DecodeResult ProcessCommandsFast<TLit, TCmd>(ref BitReader br)
        where TLit : struct, IRootWidth
        where TCmd : struct, IRootWidth
    {
        int litRootBits = default(TLit).Bits;
        uint litRootMask = (1u << litRootBits) - 1;
        int cmdRootBits = default(TCmd).Bits;
        uint cmdRootMask = (1u << cmdRootBits) - 1;
        byte[] rbArray = _ringBuffer!;
        ref byte rb = ref rbArray[0];
        ref byte input = ref br.InputRef;
        int inputLen = br.Pos + br.RemainingBytes;
        LoadWindow(ref br, out ulong bitbuf, out int bitsleft, out int inPos);
        int pos = _pos;
        int ringBufferSize = _ringBufferSize;
        int ringBufferMask = _ringBufferMask;
        int blockLen0 = _blockLength[0];
        int blockLen1 = _blockLength[1];
        int blockLen2 = _blockLength[2];
        int mbRemaining = _metaBlockRemainingLen;
        int maxDistance = _maxDistance;
        int maxBackward = _maxBackwardDistance;
        int distRbIdx = _distRbIdx;
        int[] distRb = _distRb;
        ref uint literalCodes = ref ArrayRef(_literalGroup.Codes);
        ref uint commandRoot = ref Unsafe.Add(ref ArrayRef(_insertCopyGroup.Codes), _htreeCommandOffset);
        ref uint distanceCodes = ref ArrayRef(_distanceGroup.Codes);
        ref int distHtrees = ref ArrayRef(_distanceGroup.HtreeOffsets);
        ref byte distCtxMap = ref Unsafe.Add(ref ArrayRef(_distContextMap), _distContextMapSliceOffset);
        ref byte distExtraBits = ref ArrayRef(_distExtraBits);
        ref int distOffset = ref ArrayRef(_distOffset);
        ref CommandLut.Entry cmdLut = ref ArrayRef(CommandLut.Table);
        int postfixBits = _distancePostfixBits;
        bool trivial = _trivialLiteralContext;
        ref uint literalRoot = ref Unsafe.Add(ref literalCodes, _literalHtreeOffset);
        ref byte lut1 = ref Unsafe.Add(ref MemoryMarshal.GetReference(ContextLookup.Table), _contextLookupOffset);
        ref byte lut2 = ref Unsafe.Add(ref lut1, 256);
        ref int litCtxTree = ref ArrayRef(_literalCtxTree);
        int i = 0;
        int distanceCode = 0;
        int distanceContext = 0;
        int copyLength = 0;
        int distHtreeIndex = _distHtreeIndex;
        State exitState;

        for (;;)
        {
            // ---- command
            if (inputLen - inPos < FastLoopGuard || blockLen1 == 0)
            {
                exitState = State.CommandBegin;
                break;
            }
            // One unconditional refill covers the command symbol (15 bits) and the insert extra bits (24);
            // the copy extra bits need a second one only after a long symbol plus long insert.
            Refill(ref input, ref bitbuf, ref bitsleft, ref inPos);
            int cmdCode = DecodeSymbol(ref commandRoot, cmdRootBits, cmdRootMask, ref bitbuf, ref bitsleft);
            ref CommandLut.Entry v = ref Unsafe.Add(ref cmdLut, cmdCode);
            distanceCode = v.DistanceCode;
            distanceContext = v.Context;
            distHtreeIndex = Unsafe.Add(ref distCtxMap, distanceContext);
            i = v.InsertOffset + (int)ReadBits(v.InsertExtraBits, ref bitbuf, ref bitsleft);
            if (bitsleft < 24) Refill(ref input, ref bitbuf, ref bitsleft, ref inPos);
            copyLength = v.CopyOffset + (int)ReadBits(v.CopyExtraBits, ref bitbuf, ref bitsleft);
            --blockLen1;

            // ---- literals
            if (i != 0)
            {
                mbRemaining -= i;
                int steps = i;
                if (blockLen0 < steps) steps = blockLen0;
                if (ringBufferSize - pos < steps) steps = ringBufferSize - pos;
                // At most 15 bits per literal; 16 bytes stay unread so the literal refills and the unconditional
                // distance refill after the run (eight bytes each) never pass the input.
                int inputSteps = (inputLen - inPos - 16) >> 1;
                if (inputSteps < steps) steps = inputSteps;
                if (steps > 0)
                {
                    int end = pos + steps;
                    if (trivial)
                    {
                        do
                        {
                            if (bitsleft < 16) Refill(ref input, ref bitbuf, ref bitsleft, ref inPos);
                            Unsafe.Add(ref rb, pos) = (byte)DecodeSymbol(ref literalRoot, litRootBits, litRootMask, ref bitbuf, ref bitsleft);
                            pos++;
                        } while (pos < end);
                    }
                    else
                    {
                        byte p1 = Unsafe.Add(ref rb, (pos - 1) & ringBufferMask);
                        byte p2 = Unsafe.Add(ref rb, (pos - 2) & ringBufferMask);
                        do
                        {
                            int context = Unsafe.Add(ref lut1, p1) | Unsafe.Add(ref lut2, p2);
                            ref uint root = ref Unsafe.Add(ref literalCodes, Unsafe.Add(ref litCtxTree, context));
                            if (bitsleft < 16) Refill(ref input, ref bitbuf, ref bitsleft, ref inPos);
                            p2 = p1;
                            p1 = (byte)DecodeSymbol(ref root, litRootBits, litRootMask, ref bitbuf, ref bitsleft);
                            Unsafe.Add(ref rb, pos) = p1;
                            pos++;
                        } while (pos < end);
                    }
                    blockLen0 -= steps;
                    i -= steps;
                }
                if (pos == ringBufferSize)
                {
                    exitState = State.CommandInnerWrite;
                    break;
                }
                if (i != 0)
                {
                    // Literal block switch or thin input: the checked loop finishes this command.
                    exitState = State.CommandInner;
                    break;
                }
                if (mbRemaining <= 0)
                {
                    exitState = State.MetablockDone;
                    break;
                }
            }

            // ---- distance
            if (distanceCode >= 0)
            {
                distanceContext = distanceCode != 0 ? 0 : 1;
                --distRbIdx;
                distanceCode = distRb[distRbIdx & 3];
            }
            else
            {
                if (blockLen2 == 0)
                {
                    exitState = State.CommandPostDecodeLiterals;
                    break;
                }
                // Unconditional: 56 bits cover the distance symbol (15) and its extra bits (24 plus postfix).
                Refill(ref input, ref bitbuf, ref bitsleft, ref inPos);
                int code = DecodeSymbol(ref Unsafe.Add(ref distanceCodes, Unsafe.Add(ref distHtrees, distHtreeIndex)), HuffmanTable.RootBits, Constants.HuffmanTableMask, ref bitbuf, ref bitsleft);
                --blockLen2;
                distanceContext = 0;
                if ((code & ~0xF) == 0)
                {
                    // Short code: reuse or tweak a recent distance (RFC 7932 section 4).
                    if (code <= 3)
                    {
                        distanceContext = 1 >> code;
                        distanceCode = distRb[(distRbIdx - (code - 3)) & 3];
                        distRbIdx -= distanceContext;
                    }
                    else
                    {
                        int indexDelta = 3;
                        int base_ = code - 10;
                        if (code < 10) base_ = code - 4; else indexDelta = 2;
                        int delta = ((0x605142 >> (4 * base_)) & 0xF) - 3;
                        distanceCode = distRb[(distRbIdx + indexDelta) & 3] + delta;
                        if (distanceCode <= 0) distanceCode = 0x7FFFFFFF;
                    }
                }
                else
                {
                    int nbits = Unsafe.Add(ref distExtraBits, code);
                    distanceCode = (int)((uint)Unsafe.Add(ref distOffset, code) + (ReadBits(nbits, ref bitbuf, ref bitsleft) << postfixBits));
                }
            }
            if (maxDistance != maxBackward)
            {
                maxDistance = pos < maxBackward ? pos : maxBackward;
            }
            i = copyLength;

            // ---- copy
            if (distanceCode > maxDistance)
            {
                // Dictionary reference: rare, let the field-based code handle it.
                _pos = pos;
                _loopCounter = i;
                _distanceCode = distanceCode;
                _distanceContext = distanceContext;
                _copyLength = copyLength;
                _distHtreeIndex = distHtreeIndex;
                _blockLength[0] = blockLen0;
                _blockLength[1] = blockLen1;
                _blockLength[2] = blockLen2;
                _metaBlockRemainingLen = mbRemaining;
                _maxDistance = maxDistance;
                _distRbIdx = distRbIdx;
                StoreWindow(ref br, bitbuf, bitsleft, inPos);
                DecodeResult r = CopyDictionaryReference();
                if (r != DecodeResult.Success) return r;
                if (_state != State.CommandBegin) return DecodeResult.Success;
                pos = _pos;
                mbRemaining = _metaBlockRemainingLen;
                distRbIdx = _distRbIdx;
                LoadWindow(ref br, out bitbuf, out bitsleft, out inPos);
                continue;
            }
            {
                int distance = distanceCode;
                int srcStart = (pos - distance) & ringBufferMask;
                int dstEnd = pos + i;
                distRb[distRbIdx & 3] = distance;
                ++distRbIdx;
                mbRemaining -= i;
                if (dstEnd + 16 <= ringBufferSize && srcStart + i + 16 <= ringBufferSize && srcStart < pos)
                {
                    CopyWithin(ref rb, pos, distance, i);
                    pos = dstEnd;
                }
                else
                {
                    distanceCode = distance;
                    exitState = State.CommandPostWrapCopy;
                    break;
                }
            }
            if (mbRemaining <= 0)
            {
                exitState = State.MetablockDone;
                break;
            }
        }

        _state = exitState;
        _pos = pos;
        _loopCounter = i;
        _distanceCode = distanceCode;
        _distanceContext = distanceContext;
        _copyLength = copyLength;
        _distHtreeIndex = distHtreeIndex;
        _blockLength[0] = blockLen0;
        _blockLength[1] = blockLen1;
        _blockLength[2] = blockLen2;
        _metaBlockRemainingLen = mbRemaining;
        _maxDistance = maxDistance;
        _distRbIdx = distRbIdx;
        StoreWindow(ref br, bitbuf, bitsleft, inPos);
        return DecodeResult.Success;
    }

    /// <summary>
    /// Static-dictionary or prefix-dictionary reference for the command described by the fields.
    /// Leaves <see cref="_state"/> at <see cref="State.CommandBegin"/> when the loop may continue, or at a
    /// write state when the ring buffer filled up.
    /// </summary>
    private DecodeResult CopyDictionaryReference()
    {
        int pos = _pos;
        int i = _loopCounter;
        byte[] rbArray = _ringBuffer!;
        if (_distanceCode > Constants.MaxAllowedDistance)
        {
            return Fail(BrotliDecoderError.Distance);
        }
        if ((uint)(_distanceCode - _maxDistance) - 1u < (uint)_prefixDictionarySize)
        {
            int address = _prefixDictionarySize - (_distanceCode - _maxDistance);
            if (i > _prefixDictionarySize - address) return Fail(BrotliDecoderError.PrefixDictionary);
            _distRb[_distRbIdx & 3] = _distanceCode;
            ++_distRbIdx;
            _metaBlockRemainingLen -= i;
            _dictCopyOffset = address;
            _dictCopyRemaining = i;
            pos += CopyFromPrefixDictionary(pos);
            _pos = pos;
            if (pos >= _ringBufferSize)
            {
                _state = State.CommandPostWrite1;
                return DecodeResult.Success;
            }
        }
        else if (i >= Constants.MinDictionaryWordLength && i <= Constants.MaxDictionaryWordLength)
        {
            int offset = StaticDictionary.OffsetsByLength[i];
            int shift = StaticDictionary.SizeBitsByLength[i];
            int address = _distanceCode - _maxDistance - 1 - _prefixDictionarySize;
            int mask = (1 << shift) - 1;
            int wordIdx = address & mask;
            int transformIdx = address >> shift;
            _distRbIdx += _distanceContext;
            offset += wordIdx * i;
            if (shift == 0) return Fail(BrotliDecoderError.Dictionary);
            if (transformIdx >= Transforms.Count) return Fail(BrotliDecoderError.Transform);
            ReadOnlySpan<byte> word = StaticDictionary.Data.Slice(offset, i);
            int len = i;
            if (transformIdx == Transforms.IdentityCutOff)
            {
                word.CopyTo(rbArray.AsSpan(pos));
            }
            else
            {
                len = Transforms.TransformDictionaryWord(rbArray.AsSpan(pos), word, i, transformIdx);
                if (len == 0 && _distanceCode <= 120) return Fail(BrotliDecoderError.Transform);
            }
            pos += len;
            _metaBlockRemainingLen -= len;
            _pos = pos;
            if (pos >= _ringBufferSize)
            {
                _state = State.CommandPostWrite1;
                return DecodeResult.Success;
            }
        }
        else
        {
            return Fail(BrotliDecoderError.Dictionary);
        }
        _state = _metaBlockRemainingLen <= 0 ? State.MetablockDone : State.CommandBegin;
        return DecodeResult.Success;
    }

    private DecodeResult ProcessCommands(ref BitReader br)
    {
        int pos = _pos;
        int i = _loopCounter;
        DecodeResult result = DecodeResult.Success;
        byte[] rbArray = _ringBuffer!;
        ref byte rb = ref rbArray[0];
        ref uint literalCodes = ref ArrayRef(_literalGroup.Codes);
        ref byte contextLut = ref MemoryMarshal.GetReference(ContextLookup.Table);
        int ringBufferSize = _ringBufferSize;
        int ringBufferMask = _ringBufferMask;

    Dispatch:
        if (_state == State.CommandBegin && _blockLength[1] != 0 && br.RemainingBytes >= FastLoopGuard)
        {
            _pos = pos;
            DecodeResult fr = _literalGroup.RootBits == Constants.HuffmanTableMaxBits
                ? (_insertCopyGroup.RootBits == Constants.HuffmanTableMaxBits ? ProcessCommandsFast<Root10, Root10>(ref br) : ProcessCommandsFast<Root10, Root8>(ref br))
                : (_insertCopyGroup.RootBits == Constants.HuffmanTableMaxBits ? ProcessCommandsFast<Root8, Root10>(ref br) : ProcessCommandsFast<Root8, Root8>(ref br));
            if (fr != DecodeResult.Success) return fr;
            pos = _pos;
            i = _loopCounter;
            if (_state != State.CommandBegin && _state != State.CommandInner
                && _state != State.CommandPostDecodeLiterals && _state != State.CommandPostWrapCopy)
            {
                return DecodeResult.Success;
            }
        }
        if (_state == State.CommandBegin) goto CommandBegin;
        if (_state == State.CommandInner) goto CommandInner;
        if (_state == State.CommandPostDecodeLiterals) goto CommandPostDecodeLiterals;
        if (_state == State.CommandPostWrapCopy) goto CommandPostWrapCopy;
        return Fail(BrotliDecoderError.InvalidState);

    CommandBegin:
        _state = State.CommandBegin;
        if (_blockLength[1] == 0)
        {
            DecodeResult r = DecodeCommandBlockSwitch(ref br);
            if (r != DecodeResult.Success) { result = r; goto SaveStateAndReturn; }
            goto Dispatch;
        }
        if (br.RemainingBytes >= FastLoopGuard) goto Dispatch;
        if (br.RemainingBytes >= FastCommandBytes)
        {
            ReadCommandFast(ref br, out i);
        }
        else if (!TryReadCommand(ref br, out i))
        {
            result = DecodeResult.NeedsMoreInput;
            goto SaveStateAndReturn;
        }
        if (i == 0) goto CommandPostDecodeLiterals;
        _metaBlockRemainingLen -= i;

    CommandInner:
        _state = State.CommandInner;
        if (_trivialLiteralContext)
        {
            int htreeOffset = _literalHtreeOffset;
            // Unchecked stretch: bounded by the command, the literal block, the ring buffer and the input
            // (15 bits per symbol, 8 bytes kept in reserve for the fill).
            int steps = Math.Min(i, Math.Min(_blockLength[0], ringBufferSize - pos));
            int inputSteps = (int)Math.Min((long)(br.RemainingBytes - 8) * 8 / 15, int.MaxValue);
            if (inputSteps < steps) steps = inputSteps;
            if (steps > 0)
            {
                pos = DecodeLiteralsFast(ref br, ref literalCodes, htreeOffset, _literalGroup.RootBits, ref rb, pos, steps);
                _blockLength[0] -= steps;
                i -= steps;
                if (pos == ringBufferSize)
                {
                    _state = State.CommandInnerWrite;
                    goto SaveStateAndReturn;
                }
                if (i == 0) goto LiteralsDone;
            }
            do
            {
                if (_blockLength[0] == 0) goto NextLiteralBlock;
                if (!br.TryReadSymbol(_literalGroup.Codes, htreeOffset, _literalGroup.RootBits, out int literal))
                {
                    result = DecodeResult.NeedsMoreInput;
                    goto SaveStateAndReturn;
                }
                Unsafe.Add(ref rb, pos) = (byte)literal;
                --_blockLength[0];
                ++pos;
                if (pos == ringBufferSize)
                {
                    _state = State.CommandInnerWrite;
                    --i;
                    goto SaveStateAndReturn;
                }
            } while (--i != 0);
        }
        else
        {
            byte p1 = Unsafe.Add(ref rb, (pos - 1) & ringBufferMask);
            byte p2 = Unsafe.Add(ref rb, (pos - 2) & ringBufferMask);
            ref byte lut1 = ref Unsafe.Add(ref contextLut, _contextLookupOffset);
            ref byte lut2 = ref Unsafe.Add(ref contextLut, _contextLookupOffset + 256);
            ref int ctxTree = ref ArrayRef(_literalCtxTree);
            int steps = Math.Min(i, Math.Min(_blockLength[0], ringBufferSize - pos));
            int inputSteps = (int)Math.Min((long)(br.RemainingBytes - 8) * 8 / 15, int.MaxValue);
            if (inputSteps < steps) steps = inputSteps;
            if (steps > 0)
            {
                pos = DecodeContextLiteralsFast(ref br, ref literalCodes, ref ctxTree, _literalGroup.RootBits, ref lut1, ref lut2, ref rb, pos, steps, ref p1, ref p2);
                _blockLength[0] -= steps;
                i -= steps;
                if (pos == ringBufferSize)
                {
                    _state = State.CommandInnerWrite;
                    goto SaveStateAndReturn;
                }
                if (i == 0) goto LiteralsDone;
            }
            do
            {
                if (_blockLength[0] == 0) goto NextLiteralBlock;
                int context = Unsafe.Add(ref lut1, p1) | Unsafe.Add(ref lut2, p2);
                int htree = Unsafe.Add(ref ctxTree, context);
                p2 = p1;
                if (!br.TryReadSymbol(_literalGroup.Codes, htree, _literalGroup.RootBits, out int literal))
                {
                    result = DecodeResult.NeedsMoreInput;
                    goto SaveStateAndReturn;
                }
                p1 = (byte)literal;
                Unsafe.Add(ref rb, pos) = p1;
                --_blockLength[0];
                ++pos;
                if (pos == ringBufferSize)
                {
                    _state = State.CommandInnerWrite;
                    --i;
                    goto SaveStateAndReturn;
                }
            } while (--i != 0);
        }
    LiteralsDone:
        if (_metaBlockRemainingLen <= 0)
        {
            _state = State.MetablockDone;
            goto SaveStateAndReturn;
        }

    CommandPostDecodeLiterals:
        _state = State.CommandPostDecodeLiterals;
        if (_distanceCode >= 0)
        {
            _distanceContext = _distanceCode != 0 ? 0 : 1;
            --_distRbIdx;
            _distanceCode = _distRb[_distRbIdx & 3];
        }
        else
        {
            if (_blockLength[2] == 0)
            {
                DecodeResult r = DecodeDistanceBlockSwitch(ref br);
                if (r != DecodeResult.Success) { result = r; goto SaveStateAndReturn; }
            }
            if (br.RemainingBytes >= FastCommandBytes)
            {
                ReadDistanceFast(ref br);
            }
            else if (!TryReadDistance(ref br))
            {
                result = DecodeResult.NeedsMoreInput;
                goto SaveStateAndReturn;
            }
        }
        if (_maxDistance != _maxBackwardDistance)
        {
            _maxDistance = pos < _maxBackwardDistance ? pos : _maxBackwardDistance;
        }
        i = _copyLength;
        if (_distanceCode > _maxDistance)
        {
            if (_distanceCode > Constants.MaxAllowedDistance)
            {
                return Fail(BrotliDecoderError.Distance);
            }
            if ((uint)(_distanceCode - _maxDistance) - 1u < (uint)_prefixDictionarySize)
            {
                int address = _prefixDictionarySize - (_distanceCode - _maxDistance);
                if (i > _prefixDictionarySize - address) return Fail(BrotliDecoderError.PrefixDictionary);
                _distRb[_distRbIdx & 3] = _distanceCode;
                ++_distRbIdx;
                _metaBlockRemainingLen -= i;
                _dictCopyOffset = address;
                _dictCopyRemaining = i;
                pos += CopyFromPrefixDictionary(pos);
                if (pos >= ringBufferSize)
                {
                    _state = State.CommandPostWrite1;
                    goto SaveStateAndReturn;
                }
            }
            else if (i >= Constants.MinDictionaryWordLength && i <= Constants.MaxDictionaryWordLength)
            {
                int offset = StaticDictionary.OffsetsByLength[i];
                int shift = StaticDictionary.SizeBitsByLength[i];
                int address = _distanceCode - _maxDistance - 1 - _prefixDictionarySize;
                int mask = (1 << shift) - 1;
                int wordIdx = address & mask;
                int transformIdx = address >> shift;
                _distRbIdx += _distanceContext;
                offset += wordIdx * i;
                if (shift == 0) return Fail(BrotliDecoderError.Dictionary);
                if (transformIdx < Transforms.Count)
                {
                    ReadOnlySpan<byte> word = StaticDictionary.Data.Slice(offset, i);
                    int len = i;
                    if (transformIdx == Transforms.IdentityCutOff)
                    {
                        word.CopyTo(rbArray.AsSpan(pos));
                    }
                    else
                    {
                        len = Transforms.TransformDictionaryWord(rbArray.AsSpan(pos), word, i, transformIdx);
                        if (len == 0 && _distanceCode <= 120) return Fail(BrotliDecoderError.Transform);
                    }
                    pos += len;
                    _metaBlockRemainingLen -= len;
                    if (pos >= ringBufferSize)
                    {
                        _state = State.CommandPostWrite1;
                        goto SaveStateAndReturn;
                    }
                }
                else
                {
                    return Fail(BrotliDecoderError.Transform);
                }
            }
            else
            {
                return Fail(BrotliDecoderError.Dictionary);
            }
        }
        else
        {
            int distance = _distanceCode;
            int srcStart = (pos - distance) & ringBufferMask;
            int dstEnd = pos + i;
            int srcEnd = srcStart + i;
            _distRb[_distRbIdx & 3] = distance;
            ++_distRbIdx;
            _metaBlockRemainingLen -= i;
            if (dstEnd + 16 <= ringBufferSize && srcEnd + 16 <= ringBufferSize && srcStart < pos)
            {
                // Neither region wraps and the source lies behind the destination: vector copy.
                CopyWithin(ref rb, pos, distance, i);
                pos = dstEnd;
            }
            else
            {
                goto CommandPostWrapCopy;
            }
        }
        if (_metaBlockRemainingLen <= 0)
        {
            _state = State.MetablockDone;
            goto SaveStateAndReturn;
        }
        _state = State.CommandBegin;
        goto Dispatch;

    CommandPostWrapCopy:
        {
            _state = State.CommandPostWrapCopy;
            int wrapGuard = ringBufferSize - pos;
            int distance = _distanceCode;
            while (--i >= 0)
            {
                Unsafe.Add(ref rb, pos) = Unsafe.Add(ref rb, (pos - distance) & ringBufferMask);
                ++pos;
                if (--wrapGuard == 0)
                {
                    _state = State.CommandPostWrite2;
                    goto SaveStateAndReturn;
                }
            }
        }
        if (_metaBlockRemainingLen <= 0)
        {
            _state = State.MetablockDone;
            goto SaveStateAndReturn;
        }
        _state = State.CommandBegin;
        goto Dispatch;

    NextLiteralBlock:
        {
            DecodeResult r = DecodeLiteralBlockSwitch(ref br);
            if (r != DecodeResult.Success) { result = r; goto SaveStateAndReturn; }
            goto CommandInner;
        }

    SaveStateAndReturn:
        _pos = pos;
        _loopCounter = i;
        return result;
    }


    /// <summary>
    /// Decodes <paramref name="count"/> literals with one prefix code, keeping the bit-reader state in locals
    /// (fields of a by-ref struct are not enregistered). Requires enough input for the count (see caller).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeLiteralsFast(ref BitReader br, ref uint table, int tableOffset, int rootBits, ref byte rb, int pos, int count)
    {
        ulong acc = br.Acc;
        int accBits = br.AccBits;
        int inPos = br.Pos;
        ref byte input = ref br.InputRef;
        ref uint root = ref Unsafe.Add(ref table, tableOffset);
        uint rootMask = (1u << rootBits) - 1;
        int end = pos + count;
        do
        {
            if (accBits < 16)
            {
                ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, inPos));
                int bytes = (63 - accBits) >> 3;
                acc |= (v & ((1UL << (bytes << 3)) - 1)) << accBits;
                accBits += bytes << 3;
                inPos += bytes;
            }
            uint bits = (uint)acc;
            uint e = Unsafe.Add(ref root, (int)(bits & rootMask));
            int len = (int)(e >> 16);
            if (len > rootBits)
            {
                int nbits = len - rootBits;
                e = Unsafe.Add(ref root, (int)(bits & rootMask) + (int)(e & 0xFFFF) + (int)((bits >> rootBits) & ((1u << nbits) - 1)));
                len = rootBits + (int)(e >> 16);
            }
            acc >>= len;
            accBits -= len;
            Unsafe.Add(ref rb, pos) = (byte)e;
            pos++;
        } while (pos < end);
        br.SetState(acc, accBits, inPos);
        return pos;
    }

    /// <summary>Context-modelled variant of <see cref="DecodeLiteralsFast"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeContextLiteralsFast(ref BitReader br, ref uint table, ref int ctxTree, int rootBits, ref byte lut1, ref byte lut2, ref byte rb, int pos, int count, ref byte p1Ref, ref byte p2Ref)
    {
        uint rootMask = (1u << rootBits) - 1;
        ulong acc = br.Acc;
        int accBits = br.AccBits;
        int inPos = br.Pos;
        ref byte input = ref br.InputRef;
        byte p1 = p1Ref;
        byte p2 = p2Ref;
        int end = pos + count;
        do
        {
            int context = Unsafe.Add(ref lut1, p1) | Unsafe.Add(ref lut2, p2);
            ref uint root = ref Unsafe.Add(ref table, Unsafe.Add(ref ctxTree, context));
            if (accBits < 16)
            {
                ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, inPos));
                int bytes = (63 - accBits) >> 3;
                acc |= (v & ((1UL << (bytes << 3)) - 1)) << accBits;
                accBits += bytes << 3;
                inPos += bytes;
            }
            uint bits = (uint)acc;
            uint e = Unsafe.Add(ref root, (int)(bits & rootMask));
            int len = (int)(e >> 16);
            if (len > rootBits)
            {
                int nbits = len - rootBits;
                e = Unsafe.Add(ref root, (int)(bits & rootMask) + (int)(e & 0xFFFF) + (int)((bits >> rootBits) & ((1u << nbits) - 1)));
                len = rootBits + (int)(e >> 16);
            }
            acc >>= len;
            accBits -= len;
            p2 = p1;
            p1 = (byte)e;
            Unsafe.Add(ref rb, pos) = p1;
            pos++;
        } while (pos < end);
        br.SetState(acc, accBits, inPos);
        p1Ref = p1;
        p2Ref = p2;
        return pos;
    }

    /// <summary>Unchecked variant of <see cref="TryReadCommand"/>; requires <see cref="FastCommandBytes"/> unread bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReadCommandFast(ref BitReader br, out int insertLength)
    {
        int cmdCode = br.ReadSymbolFast(ref ArrayRef(_insertCopyGroup.Codes), _htreeCommandOffset, _insertCopyGroup.RootBits);
        ref CommandLut.Entry v = ref CommandLut.Table[cmdCode];
        _distanceCode = v.DistanceCode;
        _distanceContext = v.Context;
        _distHtreeIndex = _distContextMap[_distContextMapSliceOffset + _distanceContext];
        uint insertExtra = br.ReadBitsFast(v.InsertExtraBits);
        uint copyExtra = br.ReadBitsFast(v.CopyExtraBits);
        _copyLength = (int)copyExtra + v.CopyOffset;
        --_blockLength[1];
        insertLength = v.InsertOffset + (int)insertExtra;
    }

    /// <summary>Unchecked variant of <see cref="TryReadDistance"/>; requires <see cref="FastCommandBytes"/> unread bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReadDistanceFast(ref BitReader br)
    {
        int code = br.ReadSymbolFast(ref ArrayRef(_distanceGroup.Codes), _distanceGroup.HtreeOffsets[_distHtreeIndex], _distanceGroup.RootBits);
        --_blockLength[2];
        _distanceContext = 0;
        if ((code & ~0xF) == 0)
        {
            _distanceCode = code;
            TakeDistanceFromRingBuffer();
            return;
        }
        uint bits = br.ReadBitsFast(_distExtraBits[code]);
        _distanceCode = (int)((uint)_distOffset[code] + (bits << _distancePostfixBits));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T ArrayRef<T>(T[] array)
    {
#if NET5_0_OR_GREATER
        return ref MemoryMarshal.GetArrayDataReference(array);
#else
        return ref MemoryMarshal.GetReference(array.AsSpan());
#endif
    }

    private int CopyFromPrefixDictionary(int pos)
    {
        int origPos = pos;
        byte[] dict = _prefixDictionary!;
        while (_dictCopyRemaining != 0)
        {
            int space = _ringBufferSize - pos;
            int length = Math.Min(_dictCopyRemaining, space);
            Buffer.BlockCopy(dict, _dictCopyOffset, _ringBuffer!, pos, length);
            pos += length;
            _dictCopyOffset += length;
            _dictCopyRemaining -= length;
            if (pos == _ringBufferSize) break;
        }
        return pos - origPos;
    }
}
