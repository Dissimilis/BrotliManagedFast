using System;

namespace BrotliManagedFast.Internal;

internal static partial class StaticDictionary
{
    public const int DataLength = 122784;

    /// <summary>Log2 of the number of words for each word length 0..31 (0 = no words of that length).</summary>
    public static ReadOnlySpan<byte> SizeBitsByLength => new byte[]
    {
        0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8,
        7, 7, 8, 7, 7, 6, 6, 5, 5, 0, 0, 0, 0, 0, 0, 0,
    };

    /// <summary>Byte offset of the first word of each length 0..31.</summary>
    public static ReadOnlySpan<int> OffsetsByLength => new int[]
    {
        0, 0, 0, 0, 0, 4096, 9216, 21504, 35840, 44032, 53248, 63488, 74752, 87040, 93696, 100864,
        104704, 106752, 108928, 113536, 115968, 118528, 119872, 121280, 122016, 122784, 122784, 122784,
        122784, 122784, 122784, 122784,
    };
}

internal static partial class Transforms
{
    public const int Identity = 0;
    public const int OmitLast1 = 1;
    public const int OmitLast9 = 9;
    public const int UppercaseFirst = 10;
    public const int UppercaseAll = 11;
    public const int OmitFirst1 = 12;
    public const int OmitFirst9 = 20;

    /// <summary>Transform index whose result is the identity (no prefix/suffix); reference cutOffTransforms[0].</summary>
    public const int IdentityCutOff = 0;

    private static int ToUpperCase(Span<byte> p)
    {
        if (p[0] < 0xC0)
        {
            if (p[0] >= (byte)'a' && p[0] <= (byte)'z') p[0] ^= 32;
            return 1;
        }
        if (p[0] < 0xE0)
        {
            p[1] ^= 32;
            return 2;
        }
        p[2] ^= 5;
        return 3;
    }

    /// <summary>
    /// Applies transform <paramref name="transformIdx"/> to the dictionary word <paramref name="word"/>
    /// writing the result to <paramref name="dst"/>. Returns the number of bytes written.
    /// <paramref name="dst"/> must have room for at least len + 13 bytes (max prefix + suffix).
    /// </summary>
    public static int TransformDictionaryWord(Span<byte> dst, ReadOnlySpan<byte> word, int len, int transformIdx)
    {
        ReadOnlySpan<byte> data = Data;
        ReadOnlySpan<byte> ps = PrefixSuffix;
        ReadOnlySpan<ushort> map = PrefixSuffixMap;
        int prefixIdx = map[data[transformIdx * 3]];
        int type = data[transformIdx * 3 + 1];
        int suffixIdx = map[data[transformIdx * 3 + 2]];

        int idx = 0;
        int prefixLen = ps[prefixIdx++];
        while (prefixLen-- > 0) dst[idx++] = ps[prefixIdx++];

        int i = 0;
        int wordStart = 0;
        if (type <= OmitLast9)
        {
            len -= type;
        }
        else if (type >= OmitFirst1 && type <= OmitFirst9)
        {
            int skip = type - (OmitFirst1 - 1);
            wordStart += skip;
            len -= skip;
        }
        while (i < len) dst[idx++] = word[wordStart + i++];
        if (type == UppercaseFirst)
        {
            if (len > 0) ToUpperCase(dst.Slice(idx - len));
        }
        else if (type == UppercaseAll)
        {
            int p = idx - len;
            int remaining = len;
            while (remaining > 0)
            {
                int step = ToUpperCase(dst.Slice(p));
                p += step;
                remaining -= step;
            }
        }

        int suffixLen = ps[suffixIdx++];
        while (suffixLen-- > 0) dst[idx++] = ps[suffixIdx++];
        return idx;
    }
}
