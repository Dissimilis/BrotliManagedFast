using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrotliManagedFast.Internal;

/// <summary>
/// Finds static-dictionary words (with the RFC 7932 transforms the reference encoder searches) at a position.
/// The lookup table is built once from the dictionary data: 32768 buckets over a 15-bit hash of the word's
/// first four bytes, each listing (length, transform, index) entries (the reference's static_dict_lut).
/// </summary>
internal static class StaticDictionaryMatcher
{
    public const int MaxMatchLength = 37;
    public const uint InvalidMatch = 0xFFFFFFF;
    private const int NumBuckets = 32768;
    private const int NumItems = 31705;
    private const uint HashMul32 = 0x1E35A7BD;
    private const int TransformIdentity = 0;
    private const int TransformUppercaseFirst = 10;
    private const int TransformUppercaseAll = 11;
    /// <summary>Transform ids of the OmitLast(N) transforms, 6 bits per N (the reference's kCutoffTransforms).</summary>
    private const ulong CutoffTransforms = 0x071B520ADA2D3200UL;

    private struct DictWord
    {
        public byte Len;        // bit 7 marks the end of a bucket
        public byte Transform;
        public ushort Idx;
    }

    private static readonly byte[] Data = StaticDictionary.Data.ToArray();
    private static readonly byte[] SizeBits = StaticDictionary.SizeBitsByLength.ToArray();
    private static readonly int[] Offsets = StaticDictionary.OffsetsByLength.ToArray();
    private static readonly object Gate = new();
    private static ushort[]? s_buckets;
    private static DictWord[]? s_words;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash15(ReadOnlySpan<byte> data)
    {
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(data) * HashMul32;
        return h >> (32 - 15);
    }

    private static void EnsureTables()
    {
        if (s_words != null) return;
        lock (Gate)
        {
            if (s_words != null) return;
            Build(out ushort[] buckets, out DictWord[] words);
            s_buckets = buckets;
            s_words = words;
        }
    }

    /// <summary>Builds the bucket table: identity entries for every word, then UppercaseFirst and UppercaseAll entries whose transformed form is not itself a word.</summary>
    private static void Build(out ushort[] buckets, out DictWord[] words)
    {
        var slots = new DictWord[NumItems];
        var heads = new ushort[NumBuckets];
        var counts = new ushort[NumBuckets];
        var prev = new ushort[NumItems];
        int nextSlot = 0;
        Span<byte> transformed = stackalloc byte[24];
        Span<byte> transformedOther = stackalloc byte[24];
        for (int l = 4; l <= 24; ++l)
        {
            int n = 1 << SizeBits[l];
            int wordsOffset = Offsets[l];
            for (int i = 0; i < n; ++i)
            {
                ReadOnlySpan<byte> word = Data.AsSpan(wordsOffset + l * i, l);
                uint key = Hash15(word);
                slots[nextSlot] = new DictWord { Len = (byte)l, Transform = TransformIdentity, Idx = (ushort)i };
                prev[nextSlot] = heads[key];
                heads[key] = (ushort)nextSlot;
                counts[key]++;
                ++nextSlot;
            }
            for (int i = 0; i < n; ++i)
            {
                ReadOnlySpan<byte> word = Data.AsSpan(wordsOffset + l * i, l);
                if (word[0] < 'a' || word[0] > 'z') continue;
                word.CopyTo(transformed);
                transformed[0] = (byte)(transformed[0] - 32);
                uint key = Hash15(transformed);
                uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(transformed) & ~0x20202020u;
                bool found = false;
                int curr = heads[key];
                while (curr != 0)
                {
                    if (slots[curr].Len != l) break;
                    ReadOnlySpan<byte> other = Data.AsSpan(wordsOffset + l * slots[curr].Idx, l);
                    uint otherPrefix = BinaryPrimitives.ReadUInt32LittleEndian(other) & ~0x20202020u;
                    if (prefix == otherPrefix && transformed.Slice(0, l).SequenceEqual(other))
                    {
                        found = true;
                        break;
                    }
                    curr = prev[curr];
                }
                if (found) continue;
                slots[nextSlot] = new DictWord { Len = (byte)l, Transform = TransformUppercaseFirst, Idx = (ushort)i };
                prev[nextSlot] = heads[key];
                heads[key] = (ushort)nextSlot;
                counts[key]++;
                ++nextSlot;
            }
            for (int i = 0; i < n; ++i)
            {
                ReadOnlySpan<byte> word = Data.AsSpan(wordsOffset + l * i, l);
                bool isAscii = true;
                bool hasLower = false;
                for (int k = 0; k < l; ++k)
                {
                    if (word[k] >= 128) isAscii = false;
                    if (k > 0 && word[k] >= 'a' && word[k] <= 'z') hasLower = true;
                }
                if (!isAscii || !hasLower) continue;
                word.CopyTo(transformed);
                uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(transformed) & ~0x20202020u;
                for (int k = 0; k < l; ++k)
                {
                    if (transformed[k] >= 'a' && transformed[k] <= 'z') transformed[k] = (byte)(transformed[k] - 32);
                }
                uint key = Hash15(transformed);
                bool found = false;
                int curr = heads[key];
                while (curr != 0)
                {
                    if (slots[curr].Len != l) break;
                    ReadOnlySpan<byte> other = Data.AsSpan(wordsOffset + l * slots[curr].Idx, l);
                    uint otherPrefix = BinaryPrimitives.ReadUInt32LittleEndian(other) & ~0x20202020u;
                    if (prefix == otherPrefix)
                    {
                        if (slots[curr].Transform == TransformIdentity)
                        {
                            if (transformed.Slice(0, l).SequenceEqual(other)) { found = true; break; }
                        }
                        else if (slots[curr].Transform == TransformUppercaseFirst)
                        {
                            if (transformed[0] == other[0] - 32 && transformed.Slice(1, l - 1).SequenceEqual(other.Slice(1))) { found = true; break; }
                        }
                        else
                        {
                            for (int k = 0; k < l; ++k)
                            {
                                transformedOther[k] = other[k] >= 'a' && other[k] <= 'z' ? (byte)(other[k] - 32) : other[k];
                            }
                            if (transformed.Slice(0, l).SequenceEqual(transformedOther.Slice(0, l))) { found = true; break; }
                        }
                    }
                    curr = prev[curr];
                }
                if (found) continue;
                slots[nextSlot] = new DictWord { Len = (byte)l, Transform = TransformUppercaseAll, Idx = (ushort)i };
                prev[nextSlot] = heads[key];
                heads[key] = (ushort)nextSlot;
                counts[key]++;
                ++nextSlot;
            }
        }
        if (nextSlot != NumItems - 1) throw new InvalidOperationException("Static dictionary lookup table size mismatch.");
        buckets = new ushort[NumBuckets];
        words = new DictWord[NumItems];
        int pos = 1;   // slot 0 unused so that offsets start from 1
        for (int i = 0; i < NumBuckets; ++i)
        {
            int numWords = counts[i];
            if (numWords == 0)
            {
                buckets[i] = 0;
                continue;
            }
            buckets[i] = (ushort)pos;
            int curr = heads[i];
            pos += numWords;
            for (int k = 0; k < numWords; ++k)
            {
                words[pos - 1 - k] = slots[curr];
                curr = prev[curr];
            }
            words[pos - 1].Len |= 0x80;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddMatch(int distance, int len, int lenCode, Span<uint> matches)
    {
        uint match = (uint)((distance << 5) + lenCode);
        if (match < matches[len]) matches[len] = match;
    }

    private static int MatchLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int limit)
    {
        int i = 0;
        while (i < limit && a[i] == b[i]) i++;
        return i;
    }

    private static int DictMatchLength(ReadOnlySpan<byte> data, int id, int len, int maxlen)
    {
        int offset = Offsets[len] + len * id;
        return MatchLength(Data.AsSpan(offset, len), data, Math.Min(len, maxlen));
    }

    private static bool IsMatch(DictWord w, ReadOnlySpan<byte> data, int maxLength)
    {
        int len = w.Len;
        if (len > maxLength) return false;
        int offset = Offsets[len] + len * w.Idx;
        ReadOnlySpan<byte> dict = Data.AsSpan(offset, len);
        if (w.Transform == TransformIdentity) return dict.SequenceEqual(data.Slice(0, len));
        if (w.Transform == TransformUppercaseFirst)
        {
            return dict[0] >= 'a' && dict[0] <= 'z' && (dict[0] ^ 32) == data[0] && dict.Slice(1).SequenceEqual(data.Slice(1, len - 1));
        }
        for (int i = 0; i < len; ++i)
        {
            if (dict[i] >= 'a' && dict[i] <= 'z')
            {
                if ((dict[i] ^ 32) != data[i]) return false;
            }
            else if (dict[i] != data[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Fills <paramref name="matches"/>[len] (38 entries, initialised to <see cref="InvalidMatch"/>) with the
    /// smallest (word index &lt;&lt; 5 | length code) for every match of that length, for lengths from
    /// <paramref name="minLength"/> to <paramref name="maxLength"/>. Returns whether anything was found.
    /// <paramref name="data"/> must expose at least <paramref name="maxLength"/> bytes.
    /// </summary>
    public static bool FindAllMatches(ReadOnlySpan<byte> data, int minLength, int maxLength, Span<uint> matches)
    {
        EnsureTables();
        ushort[] buckets = s_buckets!;
        DictWord[] words = s_words!;
        bool hasFoundMatch = false;
        {
            int offset = buckets[Hash15(data)];
            bool end = offset == 0;
            while (!end)
            {
                DictWord w = words[offset++];
                int l = w.Len & 0x1F;
                int n = 1 << SizeBits[l];
                int id = w.Idx;
                end = (w.Len & 0x80) != 0;
                w.Len = (byte)l;
                if (w.Transform == 0)
                {
                    int matchlen = DictMatchLength(data, id, l, maxLength);
                    if (matchlen == l)
                    {
                        AddMatch(id, l, l, matches);
                        hasFoundMatch = true;
                    }
                    if (matchlen >= l - 1)
                    {
                        AddMatch(id + 12 * n, l - 1, l, matches);
                        if (l + 2 < maxLength && data[l - 1] == 'i' && data[l] == 'n' && data[l + 1] == 'g' && data[l + 2] == ' ')
                        {
                            AddMatch(id + 49 * n, l + 3, l, matches);
                        }
                        hasFoundMatch = true;
                    }
                    int minlen = minLength;
                    if (l > 9) minlen = Math.Max(minlen, l - 9);
                    int maxlen = Math.Min(matchlen, l - 2);
                    for (int len = minlen; len <= maxlen; ++len)
                    {
                        int cut = l - len;
                        int transformId = (cut << 2) + (int)((CutoffTransforms >> (cut * 6)) & 0x3F);
                        AddMatch(id + transformId * n, len, l, matches);
                        hasFoundMatch = true;
                    }
                    if (matchlen < l || l + 6 >= maxLength) continue;
                    ReadOnlySpan<byte> s = data.Slice(l);
                    if (s[0] == ' ')
                    {
                        AddMatch(id + n, l + 1, l, matches);
                        if (s[1] == 'a')
                        {
                            if (s[2] == ' ') AddMatch(id + 28 * n, l + 3, l, matches);
                            else if (s[2] == 's') { if (s[3] == ' ') AddMatch(id + 46 * n, l + 4, l, matches); }
                            else if (s[2] == 't') { if (s[3] == ' ') AddMatch(id + 60 * n, l + 4, l, matches); }
                            else if (s[2] == 'n') { if (s[3] == 'd' && s[4] == ' ') AddMatch(id + 10 * n, l + 5, l, matches); }
                        }
                        else if (s[1] == 'b')
                        {
                            if (s[2] == 'y' && s[3] == ' ') AddMatch(id + 38 * n, l + 4, l, matches);
                        }
                        else if (s[1] == 'i')
                        {
                            if (s[2] == 'n') { if (s[3] == ' ') AddMatch(id + 16 * n, l + 4, l, matches); }
                            else if (s[2] == 's') { if (s[3] == ' ') AddMatch(id + 47 * n, l + 4, l, matches); }
                        }
                        else if (s[1] == 'f')
                        {
                            if (s[2] == 'o') { if (s[3] == 'r' && s[4] == ' ') AddMatch(id + 25 * n, l + 5, l, matches); }
                            else if (s[2] == 'r') { if (s[3] == 'o' && s[4] == 'm' && s[5] == ' ') AddMatch(id + 37 * n, l + 6, l, matches); }
                        }
                        else if (s[1] == 'o')
                        {
                            if (s[2] == 'f') { if (s[3] == ' ') AddMatch(id + 8 * n, l + 4, l, matches); }
                            else if (s[2] == 'n') { if (s[3] == ' ') AddMatch(id + 45 * n, l + 4, l, matches); }
                        }
                        else if (s[1] == 'n')
                        {
                            if (s[2] == 'o' && s[3] == 't' && s[4] == ' ') AddMatch(id + 80 * n, l + 5, l, matches);
                        }
                        else if (s[1] == 't')
                        {
                            if (s[2] == 'h')
                            {
                                if (s[3] == 'e') { if (s[4] == ' ') AddMatch(id + 5 * n, l + 5, l, matches); }
                                else if (s[3] == 'a') { if (s[4] == 't' && s[5] == ' ') AddMatch(id + 29 * n, l + 6, l, matches); }
                            }
                            else if (s[2] == 'o') { if (s[3] == ' ') AddMatch(id + 17 * n, l + 4, l, matches); }
                        }
                        else if (s[1] == 'w')
                        {
                            if (s[2] == 'i' && s[3] == 't' && s[4] == 'h' && s[5] == ' ') AddMatch(id + 35 * n, l + 6, l, matches);
                        }
                    }
                    else if (s[0] == '"')
                    {
                        AddMatch(id + 19 * n, l + 1, l, matches);
                        if (s[1] == '>') AddMatch(id + 21 * n, l + 2, l, matches);
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + 20 * n, l + 1, l, matches);
                        if (s[1] == ' ')
                        {
                            AddMatch(id + 31 * n, l + 2, l, matches);
                            if (s[2] == 'T' && s[3] == 'h')
                            {
                                if (s[4] == 'e') { if (s[5] == ' ') AddMatch(id + 43 * n, l + 6, l, matches); }
                                else if (s[4] == 'i') { if (s[5] == 's' && s[6] == ' ') AddMatch(id + 75 * n, l + 7, l, matches); }
                            }
                        }
                    }
                    else if (s[0] == ',')
                    {
                        AddMatch(id + 76 * n, l + 1, l, matches);
                        if (s[1] == ' ') AddMatch(id + 14 * n, l + 2, l, matches);
                    }
                    else if (s[0] == '\n')
                    {
                        AddMatch(id + 22 * n, l + 1, l, matches);
                        if (s[1] == '\t') AddMatch(id + 50 * n, l + 2, l, matches);
                    }
                    else if (s[0] == ']') AddMatch(id + 24 * n, l + 1, l, matches);
                    else if (s[0] == '\'') AddMatch(id + 36 * n, l + 1, l, matches);
                    else if (s[0] == ':') AddMatch(id + 51 * n, l + 1, l, matches);
                    else if (s[0] == '(') AddMatch(id + 57 * n, l + 1, l, matches);
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"') AddMatch(id + 70 * n, l + 2, l, matches);
                        else if (s[1] == '\'') AddMatch(id + 86 * n, l + 2, l, matches);
                    }
                    else if (s[0] == 'a')
                    {
                        if (s[1] == 'l' && s[2] == ' ') AddMatch(id + 84 * n, l + 3, l, matches);
                    }
                    else if (s[0] == 'e')
                    {
                        if (s[1] == 'd') { if (s[2] == ' ') AddMatch(id + 53 * n, l + 3, l, matches); }
                        else if (s[1] == 'r') { if (s[2] == ' ') AddMatch(id + 82 * n, l + 3, l, matches); }
                        else if (s[1] == 's') { if (s[2] == 't' && s[3] == ' ') AddMatch(id + 95 * n, l + 4, l, matches); }
                    }
                    else if (s[0] == 'f')
                    {
                        if (s[1] == 'u' && s[2] == 'l' && s[3] == ' ') AddMatch(id + 90 * n, l + 4, l, matches);
                    }
                    else if (s[0] == 'i')
                    {
                        if (s[1] == 'v') { if (s[2] == 'e' && s[3] == ' ') AddMatch(id + 92 * n, l + 4, l, matches); }
                        else if (s[1] == 'z') { if (s[2] == 'e' && s[3] == ' ') AddMatch(id + 100 * n, l + 4, l, matches); }
                    }
                    else if (s[0] == 'l')
                    {
                        if (s[1] == 'e') { if (s[2] == 's' && s[3] == 's' && s[4] == ' ') AddMatch(id + 93 * n, l + 5, l, matches); }
                        else if (s[1] == 'y') { if (s[2] == ' ') AddMatch(id + 61 * n, l + 3, l, matches); }
                    }
                    else if (s[0] == 'o')
                    {
                        if (s[1] == 'u' && s[2] == 's' && s[3] == ' ') AddMatch(id + 106 * n, l + 4, l, matches);
                    }
                }
                else
                {
                    bool isAllCaps = w.Transform != TransformUppercaseFirst;
                    if (!IsMatch(w, data, maxLength)) continue;
                    AddMatch(id + (isAllCaps ? 44 : 9) * n, l, l, matches);
                    hasFoundMatch = true;
                    if (l + 1 >= maxLength) continue;
                    ReadOnlySpan<byte> s = data.Slice(l);
                    if (s[0] == ' ') AddMatch(id + (isAllCaps ? 68 : 4) * n, l + 1, l, matches);
                    else if (s[0] == '"')
                    {
                        AddMatch(id + (isAllCaps ? 87 : 66) * n, l + 1, l, matches);
                        if (s[1] == '>') AddMatch(id + (isAllCaps ? 97 : 69) * n, l + 2, l, matches);
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + (isAllCaps ? 101 : 79) * n, l + 1, l, matches);
                        if (s[1] == ' ') AddMatch(id + (isAllCaps ? 114 : 88) * n, l + 2, l, matches);
                    }
                    else if (s[0] == ',')
                    {
                        AddMatch(id + (isAllCaps ? 112 : 99) * n, l + 1, l, matches);
                        if (s[1] == ' ') AddMatch(id + (isAllCaps ? 107 : 58) * n, l + 2, l, matches);
                    }
                    else if (s[0] == '\'') AddMatch(id + (isAllCaps ? 94 : 74) * n, l + 1, l, matches);
                    else if (s[0] == '(') AddMatch(id + (isAllCaps ? 113 : 78) * n, l + 1, l, matches);
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"') AddMatch(id + (isAllCaps ? 105 : 104) * n, l + 2, l, matches);
                        else if (s[1] == '\'') AddMatch(id + (isAllCaps ? 116 : 108) * n, l + 2, l, matches);
                    }
                }
            }
        }
        // Transforms with prefixes " " and "."
        if (maxLength >= 5 && (data[0] == ' ' || data[0] == '.'))
        {
            bool isSpace = data[0] == ' ';
            int offset = buckets[Hash15(data.Slice(1))];
            bool end = offset == 0;
            while (!end)
            {
                DictWord w = words[offset++];
                int l = w.Len & 0x1F;
                int n = 1 << SizeBits[l];
                int id = w.Idx;
                end = (w.Len & 0x80) != 0;
                w.Len = (byte)l;
                if (w.Transform == 0)
                {
                    if (!IsMatch(w, data.Slice(1), maxLength - 1)) continue;
                    AddMatch(id + (isSpace ? 6 : 32) * n, l + 1, l, matches);
                    hasFoundMatch = true;
                    if (l + 2 >= maxLength) continue;
                    ReadOnlySpan<byte> s = data.Slice(l + 1);
                    if (s[0] == ' ') AddMatch(id + (isSpace ? 2 : 77) * n, l + 2, l, matches);
                    else if (s[0] == '(') AddMatch(id + (isSpace ? 89 : 67) * n, l + 2, l, matches);
                    else if (isSpace)
                    {
                        if (s[0] == ',')
                        {
                            AddMatch(id + 103 * n, l + 2, l, matches);
                            if (s[1] == ' ') AddMatch(id + 33 * n, l + 3, l, matches);
                        }
                        else if (s[0] == '.')
                        {
                            AddMatch(id + 71 * n, l + 2, l, matches);
                            if (s[1] == ' ') AddMatch(id + 52 * n, l + 3, l, matches);
                        }
                        else if (s[0] == '=')
                        {
                            if (s[1] == '"') AddMatch(id + 81 * n, l + 3, l, matches);
                            else if (s[1] == '\'') AddMatch(id + 98 * n, l + 3, l, matches);
                        }
                    }
                }
                else if (isSpace)
                {
                    bool isAllCaps = w.Transform != TransformUppercaseFirst;
                    if (!IsMatch(w, data.Slice(1), maxLength - 1)) continue;
                    AddMatch(id + (isAllCaps ? 85 : 30) * n, l + 1, l, matches);
                    hasFoundMatch = true;
                    if (l + 2 >= maxLength) continue;
                    ReadOnlySpan<byte> s = data.Slice(l + 1);
                    if (s[0] == ' ') AddMatch(id + (isAllCaps ? 83 : 15) * n, l + 2, l, matches);
                    else if (s[0] == ',')
                    {
                        if (!isAllCaps) AddMatch(id + 109 * n, l + 2, l, matches);
                        if (s[1] == ' ') AddMatch(id + (isAllCaps ? 111 : 65) * n, l + 3, l, matches);
                    }
                    else if (s[0] == '.')
                    {
                        AddMatch(id + (isAllCaps ? 115 : 96) * n, l + 2, l, matches);
                        if (s[1] == ' ') AddMatch(id + (isAllCaps ? 117 : 91) * n, l + 3, l, matches);
                    }
                    else if (s[0] == '=')
                    {
                        if (s[1] == '"') AddMatch(id + (isAllCaps ? 110 : 118) * n, l + 3, l, matches);
                        else if (s[1] == '\'') AddMatch(id + (isAllCaps ? 119 : 120) * n, l + 3, l, matches);
                    }
                }
            }
        }
        if (maxLength >= 6)
        {
            // Transforms with prefixes "e ", "s ", ", " and "\xC2\xA0"
            if ((data[1] == ' ' && (data[0] == 'e' || data[0] == 's' || data[0] == ',')) || (data[0] == 0xC2 && data[1] == 0xA0))
            {
                int offset = buckets[Hash15(data.Slice(2))];
                bool end = offset == 0;
                while (!end)
                {
                    DictWord w = words[offset++];
                    int l = w.Len & 0x1F;
                    int n = 1 << SizeBits[l];
                    int id = w.Idx;
                    end = (w.Len & 0x80) != 0;
                    w.Len = (byte)l;
                    if (w.Transform == 0 && IsMatch(w, data.Slice(2), maxLength - 2))
                    {
                        if (data[0] == 0xC2)
                        {
                            AddMatch(id + 102 * n, l + 2, l, matches);
                            hasFoundMatch = true;
                        }
                        else if (l + 2 < maxLength && data[l + 2] == ' ')
                        {
                            int t = data[0] == 'e' ? 18 : (data[0] == 's' ? 7 : 13);
                            AddMatch(id + t * n, l + 3, l, matches);
                            hasFoundMatch = true;
                        }
                    }
                }
            }
        }
        if (maxLength >= 9)
        {
            // Transforms with prefixes " the " and ".com/"
            if ((data[0] == ' ' && data[1] == 't' && data[2] == 'h' && data[3] == 'e' && data[4] == ' ') ||
                (data[0] == '.' && data[1] == 'c' && data[2] == 'o' && data[3] == 'm' && data[4] == '/'))
            {
                int offset = buckets[Hash15(data.Slice(5))];
                bool end = offset == 0;
                while (!end)
                {
                    DictWord w = words[offset++];
                    int l = w.Len & 0x1F;
                    int n = 1 << SizeBits[l];
                    int id = w.Idx;
                    end = (w.Len & 0x80) != 0;
                    w.Len = (byte)l;
                    if (w.Transform == 0 && IsMatch(w, data.Slice(5), maxLength - 5))
                    {
                        AddMatch(id + (data[0] == ' ' ? 41 : 72) * n, l + 5, l, matches);
                        hasFoundMatch = true;
                        if (l + 5 < maxLength)
                        {
                            ReadOnlySpan<byte> s = data.Slice(l + 5);
                            if (data[0] == ' ')
                            {
                                if (l + 8 < maxLength && s[0] == ' ' && s[1] == 'o' && s[2] == 'f' && s[3] == ' ')
                                {
                                    AddMatch(id + 62 * n, l + 9, l, matches);
                                    if (l + 12 < maxLength && s[4] == 't' && s[5] == 'h' && s[6] == 'e' && s[7] == ' ')
                                    {
                                        AddMatch(id + 73 * n, l + 13, l, matches);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return hasFoundMatch;
    }
}
