using System;
using Xunit;

namespace BrotliManagedFast.Tests;

public class DictionaryBoundaryTests
{
    /// <summary>A match found in the dictionary must stop at the dictionary's end even when the stream continues it.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(11)]
    public void DictionaryMatchStopsAtDictionaryEnd(int quality)
    {
        byte[] dictionary = new byte[64];
        for (int i = 0; i < dictionary.Length; i++) dictionary[i] = (byte)('a' + i % 26);
        // The input repeats the dictionary tail and then continues with the dictionary head, so a naive
        // match runs straight through the dictionary end into the stream.
        byte[] input = new byte[300];
        for (int i = 0; i < input.Length; i++) input[i] = dictionary[(dictionary.Length - 12 + i) % dictionary.Length];
        var options = new BrotliCompressionOptions { Quality = quality, WindowLog = 22, Dictionary = BrotliDictionary.Create(dictionary) };
        byte[] compressed = BrotliEncoder.Compress(input, options);
        byte[] decoded = BrotliDecoder.Decompress(compressed, new BrotliDecompressionOptions { Dictionary = BrotliDictionary.Create(dictionary) });
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void ConcatenableWithDictionaryIsRejected()
    {
        var options = new BrotliCompressionOptions { Concatenable = true, Dictionary = BrotliDictionary.Create(new byte[16]) };
        Assert.Throws<ArgumentException>(() => BrotliEncoder.Compress(new byte[100], options));
    }
}
