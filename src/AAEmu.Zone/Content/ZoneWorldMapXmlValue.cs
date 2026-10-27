using System.Globalization;

namespace AAEmu.Zone.Content;

/// <summary>
/// CXmlNode's present-attribute conversions: MSVCR100 atoi (365943A0/36594460/365944A0)
/// and atof followed by a float conversion (36594360). Missing attributes are handled by
/// the caller. The native C locale recognizes ASCII whitespace and decimal prefixes.
/// </summary>
internal static class ZoneWorldMapXmlValue
{
    internal static int Int32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = SkipWhitespace(text);
        var negative = index < text.Length && text[index] == '-';
        if (index < text.Length && text[index] is '+' or '-') index++;
        var limit = negative ? 2147483648u : int.MaxValue;
        uint value = 0;
        while (index < text.Length && IsDigit(text[index]))
        {
            var digit = (uint)(text[index++] - '0');
            // MSVCR100 atoi saturates signed overflow rather than wrapping low bits.
            if (value > (limit - digit) / 10) return negative ? int.MinValue : int.MaxValue;
            value = value * 10 + digit;
        }
        return negative ? unchecked(-(int)value) : (int)value;
    }

    internal static float Single(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = SkipWhitespace(text);
        var index = start;
        if (index < text.Length && text[index] is '+' or '-') index++;
        var digits = 0;
        while (index < text.Length && IsDigit(text[index])) { index++; digits++; }
        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && IsDigit(text[index])) { index++; digits++; }
        }
        // This CRT does not accept textual NaN/Infinity, hexadecimal numbers, or a bare sign.
        if (digits == 0) return 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponent = index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            var exponentDigits = index;
            while (index < text.Length && IsDigit(text[index])) index++;
            if (index == exponentDigits) index = exponent;
        }
        // The intermediate double is observable at float rounding boundaries. Do not use
        // float.Parse: "1.0000000596046448" becomes 0x3F800000 through native atof.
        return double.TryParse(text.AsSpan(start, index - start), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? (float)value : 0;
    }

    private static int SkipWhitespace(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f') index++;
        return index;
    }

    private static bool IsDigit(char value) => value is >= '0' and <= '9';
}
