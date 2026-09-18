using System;

namespace BrotliManagedFast.Internal;

/// <summary>RFC 7932 constants and small static tables shared by the encoder and decoder.</summary>
internal static class Constants
{
    public const int MinWindowBits = 10;
    public const int MaxWindowBits = 24;
    public const int LargeMinWindowBits = 10;
    public const int LargeMaxWindowBits = 30;
    public const int WindowGap = 16;

    public const int NumLiteralSymbols = 256;
    public const int NumCommandSymbols = 704;
    public const int NumBlockLengthSymbols = 26;
    public const int MaxBlockTypes = 256;
    public const int MaxBlockTypeSymbols = MaxBlockTypes + 2;
    public const int MaxContextMapSymbols = MaxBlockTypes + 16; // 272
    public const int ContextMapMaxRle = 16;

    public const int NumDistanceShortCodes = 16;
    public const int MaxNPostfix = 3;
    public const int MaxNDirect = 120;
    public const int MaxDistanceBits = 24;
    public const int LargeMaxDistanceBits = 62;
    /// <summary>Largest distance the decoder accepts (matches reference BROTLI_MAX_ALLOWED_DISTANCE).</summary>
    public const int MaxAllowedDistance = 0x7FFFFFFC;
    /// <summary>Largest distance representable with the regular 24-bit alphabet.</summary>
    public const int MaxDistance = 0x3FFFFFC;

    public const int LiteralContextBits = 6;
    public const int DistanceContextBits = 2;

    public const int CodeLengthCodes = 18;
    public const int RepeatPreviousCodeLength = 16;
    public const int RepeatZeroCodeLength = 17;
    public const int InitialRepeatedCodeLength = 8;
    public const int HuffmanMaxCodeLength = 15;
    public const int HuffmanMaxCodeLengthCodeLength = 5;
    public const int HuffmanTableBits = 8;
    public const int HuffmanTableMask = 0xFF;

    /// <summary>Extra slots needed beyond the alphabet size for second-level Huffman tables.</summary>
    /// <summary>Widest root a group tree may use (chosen per group from its first tree's code lengths).</summary>
    public const int HuffmanTableMaxBits = 10;
    public const int HuffmanTableSlack = 376 + (1 << HuffmanTableMaxBits) - 256;
    public const int HuffmanMaxSize26 = 396;
    public const int HuffmanMaxSize258 = 632;
    public const int HuffmanMaxSize272 = 646;

    /// <summary>Largest metablock length (1 &lt;&lt; 24).</summary>
    public const int MaxMetablockSize = 1 << 24;
    /// <summary>Block length cap used to initialise block counters (2^28, larger than any block).</summary>
    public const int BlockSizeCap = 1 << 28;

    public const int MinDictionaryWordLength = 4;
    public const int MaxDictionaryWordLength = 24;

    public static int DistanceAlphabetSize(int nPostfix, int nDirect, int maxDistBits)
        => NumDistanceShortCodes + nDirect + (maxDistBits << (nPostfix + 1));

    /// <summary>Order in which code-length code lengths are stored (RFC 7932 3.5).</summary>
    public static ReadOnlySpan<byte> CodeLengthCodeOrder => new byte[]
    {
        1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };

    /// <summary>Static prefix code for code-length code lengths: bit length by 4-bit peek.</summary>
    public static ReadOnlySpan<byte> CodeLengthPrefixLength => new byte[]
    {
        2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4,
    };

    /// <summary>Static prefix code for code-length code lengths: value by 4-bit peek.</summary>
    public static ReadOnlySpan<byte> CodeLengthPrefixValue => new byte[]
    {
        0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5,
    };

    /// <summary>Encoder side: code (bit pattern, LSB first) for each code-length-code length 0..5.</summary>
    public static ReadOnlySpan<byte> CodeLengthCodeSymbols => new byte[] { 0, 7, 3, 2, 1, 15 };
    public static ReadOnlySpan<byte> CodeLengthCodeBitLengths => new byte[] { 2, 4, 3, 2, 2, 4 };

    /// <summary>Block length prefix code: offset for each of the 26 symbols.</summary>
    public static ReadOnlySpan<int> BlockLengthOffset => new int[]
    {
        1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305,
        369, 497, 753, 1265, 2289, 4337, 8433, 16625,
    };

    /// <summary>Block length prefix code: extra bits for each of the 26 symbols.</summary>
    public static ReadOnlySpan<byte> BlockLengthExtraBits => new byte[]
    {
        2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24,
    };

    /// <summary>Insert length codes: extra bits (24 codes).</summary>
    public static ReadOnlySpan<byte> InsertLengthExtraBits => new byte[]
    {
        0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24,
    };

    /// <summary>Insert length codes: base value (24 codes).</summary>
    public static ReadOnlySpan<int> InsertLengthBase => new int[]
    {
        0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594,
    };

    /// <summary>Copy length codes: extra bits (24 codes).</summary>
    public static ReadOnlySpan<byte> CopyLengthExtraBits => new byte[]
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24,
    };

    /// <summary>Copy length codes: base value (24 codes).</summary>
    public static ReadOnlySpan<int> CopyLengthBase => new int[]
    {
        2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118,
    };

    /// <summary>
    /// Maps the top 5 bits of an insert-and-copy command symbol (symbol &gt;&gt; 6, 0..10) to
    /// (insertCodeHigh &lt;&lt; 3) | copyCodeHigh, per RFC 7932 section 5 table.
    /// </summary>
    public static ReadOnlySpan<byte> CommandCellPos => new byte[] { 0, 1, 0, 1, 8, 9, 2, 16, 10, 17, 18 };
}

/// <summary>Per-symbol lookup for the 704 insert-and-copy command symbols.</summary>
internal static class CommandLut
{
    public readonly struct Entry
    {
        public readonly byte InsertExtraBits;
        public readonly byte CopyExtraBits;
        public readonly sbyte DistanceCode;   // 0 => implicit "last distance", -1 => read from stream
        public readonly byte Context;         // distance context 0..3
        public readonly ushort InsertOffset;
        public readonly ushort CopyOffset;

        public Entry(byte insExtra, byte copyExtra, sbyte distCode, byte ctx, ushort insOff, ushort copyOff)
        {
            InsertExtraBits = insExtra; CopyExtraBits = copyExtra; DistanceCode = distCode;
            Context = ctx; InsertOffset = insOff; CopyOffset = copyOff;
        }
    }

    public static readonly Entry[] Table = Build();

    private static Entry[] Build()
    {
        var table = new Entry[Constants.NumCommandSymbols];
        var insExtra = Constants.InsertLengthExtraBits;
        var copyExtra = Constants.CopyLengthExtraBits;
        var insBase = Constants.InsertLengthBase;
        var copyBase = Constants.CopyLengthBase;
        var cellPos = Constants.CommandCellPos;
        for (int symbol = 0; symbol < Constants.NumCommandSymbols; symbol++)
        {
            int cellIdx = symbol >> 6;
            int cp = cellPos[cellIdx];
            int copyCode = ((cp << 3) & 0x18) + (symbol & 0x7);
            int insertCode = (cp & 0x18) + ((symbol >> 3) & 0x7);
            int copyOff = copyBase[copyCode];
            byte ctx = (byte)(copyOff > 4 ? 3 : copyOff - 2);
            table[symbol] = new Entry(insExtra[insertCode], copyExtra[copyCode], (sbyte)(cellIdx >= 2 ? -1 : 0),
                ctx, (ushort)insBase[insertCode], (ushort)copyOff);
        }
        return table;
    }
}
