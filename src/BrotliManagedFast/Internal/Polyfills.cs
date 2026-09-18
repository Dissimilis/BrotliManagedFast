// Compatibility shims so the same sources compile for netstandard2.0.
#if NETSTANDARD2_0
using System;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class MaybeNullWhenAttribute : Attribute
    {
        public MaybeNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }
}

namespace BrotliManagedFast
{
    internal static class BitOperations
    {
        private static ReadOnlySpan<byte> Log2DeBruijn => new byte[]
        {
            00, 09, 01, 10, 13, 21, 02, 29, 11, 14, 16, 18, 22, 25, 03, 30,
            08, 12, 20, 28, 15, 17, 24, 07, 19, 27, 23, 06, 26, 05, 04, 31,
        };

        /// <summary>Floor(log2(value)); returns 0 for value 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Log2(uint value)
        {
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return Log2DeBruijn[(int)((value * 0x07C4ACDDu) >> 27)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Log2(ulong value)
        {
            uint hi = (uint)(value >> 32);
            return hi != 0 ? 32 + Log2(hi) : Log2((uint)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LeadingZeroCount(uint value) => value == 0 ? 32 : 31 - Log2(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TrailingZeroCount(uint value)
        {
            if (value == 0) return 32;
            int n = 0;
            while ((value & 1) == 0) { value >>= 1; n++; }
            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint RoundUpToPowerOf2(uint value)
        {
            --value;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }

    internal static class MathCompat
    {
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
    }
}
#else
namespace BrotliManagedFast
{
    internal static class MathCompat
    {
        public static int Clamp(int value, int min, int max) => System.Math.Clamp(value, min, max);
        public static long Clamp(long value, long min, long max) => System.Math.Clamp(value, min, max);
    }
}
#endif
