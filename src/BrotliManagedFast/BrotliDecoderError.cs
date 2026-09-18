namespace BrotliManagedFast;

/// <summary>Detailed reason for the last <see cref="System.Buffers.OperationStatus.InvalidData"/> result of a decoder.</summary>
public enum BrotliDecoderError
{
    /// <summary>No error.</summary>
    None = 0,
    /// <summary>Invalid window-bits header (including a Large Window header when Large Window decoding is not enabled).</summary>
    WindowBits,
    /// <summary>A reserved bit was set.</summary>
    Reserved,
    /// <summary>Metablock length has a redundant leading zero nibble.</summary>
    ExuberantNibble,
    /// <summary>Metadata block length has a redundant leading zero byte.</summary>
    ExuberantMetaNibble,
    /// <summary>A simple prefix code lists a symbol outside the alphabet.</summary>
    SimpleHuffmanAlphabet,
    /// <summary>A simple prefix code lists the same symbol twice.</summary>
    SimpleHuffmanSame,
    /// <summary>The code-length code is not a complete prefix code.</summary>
    CodeLengthSpace,
    /// <summary>A prefix code is over- or under-subscribed.</summary>
    HuffmanSpace,
    /// <summary>A context-map run-length exceeds the map size.</summary>
    ContextMapRepeat,
    /// <summary>Metablock contents did not match the declared length.</summary>
    BlockLength,
    /// <summary>Invalid static-dictionary transform index.</summary>
    Transform,
    /// <summary>Invalid static-dictionary reference.</summary>
    Dictionary,
    /// <summary>Non-zero padding bits before a byte boundary.</summary>
    Padding,
    /// <summary>Backward distance larger than allowed.</summary>
    Distance,
    /// <summary>Block switch command in a metablock with a single block type.</summary>
    BlockSwitch,
    /// <summary>The input ended before the stream was complete (only reported when the caller signals final input).</summary>
    TruncatedInput,
    /// <summary>Bytes remained after the end of the stream and <see cref="BrotliDecompressionOptions.RejectTrailingData"/> was set.</summary>
    TrailingData,
    /// <summary>Decompressed output would exceed <see cref="BrotliDecompressionOptions.MaxOutputLength"/>.</summary>
    OutputLimitExceeded,
    /// <summary>The stream declares a window larger than <see cref="BrotliDecompressionOptions.MaxWindowLog"/> allows.</summary>
    WindowTooLarge,
    /// <summary>A prefix dictionary reference was out of range.</summary>
    PrefixDictionary,
    /// <summary>The decoder was used after an error or after completion without <c>Reset</c>.</summary>
    InvalidState,
}
