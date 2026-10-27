using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>MSVCR100's decimal-to-float path used by the native lookup loader's sscanf("%f").</summary>
internal static class ZoneLegacyFloat
{
    private readonly record struct Extended(UInt128 Mantissa, int Exponent);
    private static readonly Dictionary<int, Extended> Powers = CreatePowers();

    // Input is the recognized decimal prefix, without leading whitespace or an incomplete exponent.
    public static float ParseDecimal(ReadOnlySpan<char> text)
    {
        var negative = text[0] == '-';
        var position = text[0] is '+' or '-' ? 1 : 0;
        Span<byte> digits = stackalloc byte[25];
        var count = 0;
        var exponent = 0;
        var fractional = false;
        var significant = false;
        for (; position < text.Length && text[position] is not ('e' or 'E'); position++)
        {
            var character = text[position];
            if (character == '.') { fractional = true; continue; }
            if (!significant && character == '0')
            {
                if (fractional) exponent--;
                continue;
            }
            significant = true;
            if (count < digits.Length)
            {
                digits[count++] = (byte)(character - '0');
                if (fractional) exponent--;
            }
            else if (!fractional) exponent++;
        }
        if (position < text.Length)
        {
            position++;
            var exponentNegative = text[position] == '-';
            if (text[position] is '+' or '-') position++;
            var explicitExponent = 0;
            for (; position < text.Length; position++)
                explicitExponent = Math.Min(5201, explicitExponent * 10 + text[position] - '0');
            exponent = unchecked(exponent + (exponentNegative ? -explicitExponent : explicitExponent));
        }
        if (count > 24)
        {
            // 797AD6B6 reads the retained 24th digit, not the discarded 25th digit.
            // A retained nine becomes ten; the following integer accumulation carries it normally.
            if (digits[23] >= 5) digits[23]++;
            count = 24;
            exponent++;
        }
        while (count != 0 && digits[count - 1] == 0) { count--; exponent++; }
        var sign = negative ? 0x80000000u : 0u;
        if (count == 0 || exponent < -100) return BitConverter.UInt32BitsToSingle(sign);
        if (exponent > 100) return BitConverter.UInt32BitsToSingle(sign | 0x7f800000u);
        UInt128 integer = 0;
        for (var index = 0; index < count; index++) integer = integer * 10 + digits[index];
        var bits = BitLength(integer);
        var value = new Extended(integer << (80 - bits), bits - 1);
        if (exponent != 0)
        {
            // 797AD73E clears the extra sixteen mantissa bits before decimal scaling.
            value = value with { Mantissa = value.Mantissa & ~((UInt128)0xffff) };
            var exponentSign = exponent < 0 ? -1 : 1;
            var remaining = Math.Abs(exponent);
            for (var scale = 1; remaining != 0; scale *= 8, remaining >>= 3)
            {
                var digit = remaining & 7;
                if (digit != 0) value = Multiply(value, Powers[exponentSign * scale * digit]);
            }
        }
        return BitConverter.UInt32BitsToSingle(sign | ToSingleBits(value));
    }

    private static int BitLength(UInt128 value) => (value >> 64) != 0
        ? BitOperations.Log2((ulong)(value >> 64)) + 65 : BitOperations.Log2((ulong)value) + 1;

    private static Extended Multiply(Extended left, Extended right)
    {
        // 797AD83D accumulates only the upper convolution terms of the five 16-bit limbs.
        // Including carries from the omitted lower terms changes native rounding at float boundaries.
        UInt128 product = 0;
        for (var i = 0; i < 5; i++)
            for (var j = 4 - i; j < 5; j++)
                product += ((left.Mantissa >> (16 * i)) & 0xffff) *
                    ((right.Mantissa >> (16 * j)) & 0xffff) << (16 * (i + j - 4));
        var exponent = left.Exponent + right.Exponent + 1;
        while (product < ((UInt128)1 << 95)) { product <<= 1; exponent--; }
        var mantissa = product >> 16;
        var tail = (uint)(product & 0xffff);
        if (tail > 0x8000 || tail == 0x8000 && (mantissa & 1) != 0) mantissa++;
        if (mantissa >= ((UInt128)1 << 80)) { mantissa >>= 1; exponent++; }
        return new(mantissa, exponent);
    }

    private static uint ToSingleBits(Extended value)
    {
        if (value.Exponent > 127) return 0x7f800000u;
        if (value.Exponent < -150) return 0;
        var shift = value.Exponent < -126 ? -value.Exponent - 70 : 56;
        var significand = value.Mantissa >> shift;
        var remainder = value.Mantissa & (((UInt128)1 << shift) - 1);
        // 797ACCCE rounds normal exact halves toward zero. The subnormal branch rounds with
        // one extra precision bit before dropping that bit at 797ACF78: its cutoff is 3/4.
        var cutoff = value.Exponent < -126 ? (UInt128)3 << (shift - 2) : (UInt128)1 << (shift - 1);
        if (remainder > cutoff) significand++;
        if (value.Exponent < -126) return (uint)significand;
        var exponent = value.Exponent;
        if (significand >= ((UInt128)1 << 24)) { significand >>= 1; exponent++; }
        if (exponent > 127) return 0x7f800000u;
        return (uint)((exponent + 127) << 23) | ((uint)significand & 0x7fffff);
    }

    private static Dictionary<int, Extended> CreatePowers()
    {
        var powers = new Dictionary<int, Extended>();
        // Generate the exact 80-bit powers represented by native tables 797E3C40/797E3DA0.
        // Their separately rounded 64-bit fields are undone by 797AD790 before multiplication.
        foreach (var scale in new[] { 1, 8, 64 })
            for (var digit = 1; digit <= 7; digit++)
            {
                var exponent = scale * digit;
                var integer = BigInteger.Pow(10, exponent);
                var bits = (int)integer.GetBitLength();
                var positive = bits <= 80 ? integer << (80 - bits) :
                    RoundEven(integer, BigInteger.One << (bits - 80));
                powers[exponent] = new((UInt128)positive, bits - 1);
                powers[-exponent] = new((UInt128)RoundEven(BigInteger.One << (79 + bits), integer), -bits);
            }
        return powers;
    }

    private static BigInteger RoundEven(BigInteger numerator, BigInteger denominator)
    {
        var value = BigInteger.DivRem(numerator, denominator, out var remainder);
        var comparison = (remainder * 2).CompareTo(denominator);
        return comparison > 0 || comparison == 0 && !value.IsEven ? value + 1 : value;
    }
}
