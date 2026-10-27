using AAEmu.Zone.Combat;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Ai;

/// <summary>Owned AI lookup samples, their native interpolation and the Scripts/AI/lookup.xml loader.</summary>
public sealed class ZoneAiLookupTable
{
    private float[] _values = [];
    public IReadOnlyList<float> Values { get; private set; } = Array.Empty<float>();

    // 3104AFC8 -> 31080740: replace the owned array, including an empty table.
    public void SetValues(ReadOnlySpan<float> values)
    {
        _values = values.ToArray();
        Values = Array.AsReadOnly(_values);
    }

    // 3104AFE8. Preserve the two arithmetic clamps; min/max changes non-finite behavior.
    public float Evaluate(float input)
    {
        var clamped = ((input + 1f) - MathF.Abs(input - 1f)) * 0.5f;
        clamped = ((MathF.Abs(clamped - 0f) + clamped) + 0f) * 0.5f;
        if (_values.Length < 2)
        {
            var inverse = 1f - clamped;
            return inverse * inverse;
        }
        // 310807C0: the native signed conversion maps unordered/out-of-range indices to INT_MIN.
        var position = clamped * (_values.Length - 1);
        var index = ZoneStatMath.ToInt32(MathF.Floor(position));
        if (index < 0) return _values[0] / 100f;
        if (index >= _values.Length - 1) return _values[^1] / 100f;
        var interpolated = ((_values[index + 1] - _values[index]) * (position - index)) + _values[index];
        return interpolated / 100f;
    }

    /// <summary>Reads the configured game source; missing or invalid XML preserves the previous table.</summary>
    public bool Load(IZoneScriptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var file = source.Read("Scripts/AI/lookup.xml");
            return file is not null && LoadXml(file.Bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // 3053E9A0: only the first Worksheet/Table and each row's logical first column contribute samples.
    public bool LoadXml(ReadOnlyMemory<byte> bytes)
    {
        if (!ZoneAiLookupXml.TryRead(bytes, out var document)) return false;
        var sheet = Child(document, "Worksheet");
        var table = Child(sheet, "Table");
        if (table is null) return false;
        var values = new List<float>();
        foreach (var row in table.Children)
        {
            if (!SameName(row.Name, "Row")) continue;
            var column = 1;
            foreach (var cell in row.Children)
            {
                if (SameName(cell.Name, "Cell"))
                {
                    var index = cell.Attributes.FirstOrDefault(attribute => SameName(attribute.Key, "ss:Index"));
                    if (index.Key is not null && TryIntegerPrefix(index.Value, out var parsed)) column = parsed;
                    var data = Child(cell, "Data");
                    if (data is not null && column == 1)
                    {
                        // CXmlNode's content excludes child-element contents. A failed sscanf leaves zero.
                        values.Add(FloatPrefix(data.Content));
                        break;
                    }
                }
                column = unchecked(column + 1);
            }
        }
        SetValues(values.ToArray());
        return true;
    }

    // CXmlNode 36592E60 compares ASCII letters without case, retaining prefixes literally.
    private static bool SameName(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i]; var b = right[i];
            if (a is >= 'A' and <= 'Z') a = (char)(a + 32);
            if (b is >= 'A' and <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static ZoneAiLookupXml.Node? Child(ZoneAiLookupXml.Node? parent, string name) =>
        parent?.Children.FirstOrDefault(child => SameName(child.Name, name));

    private static int SkipSpace(string text)
    {
        var index = 0;
        while (index < text.Length && (text[index] == ' ' || text[index] is >= '\t' and <= '\r')) index++;
        return index;
    }

    // MSVCR100 sscanf("%d") accepts a decimal prefix and wraps the 32-bit accumulator.
    private static bool TryIntegerPrefix(string text, out int value)
    {
        var index = SkipSpace(text); var negative = false;
        if (index < text.Length && text[index] is '+' or '-') negative = text[index++] == '-';
        var start = index; value = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
            value = unchecked(value * 10 + (text[index++] - '0'));
        if (negative) value = unchecked(-value);
        return index != start;
    }

    // MSVCR100 sscanf("%f") is decimal-prefix based: nan/inf fail, incomplete exponents keep the mantissa.
    private static float FloatPrefix(string text)
    {
        var index = SkipSpace(text); var start = index;
        if (index < text.Length && text[index] is '+' or '-') index++;
        var digits = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9') { index++; digits++; }
        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && text[index] is >= '0' and <= '9') { index++; digits++; }
        }
        if (digits == 0) return 0;
        var end = index;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (index < text.Length && text[index] is '+' or '-') index++;
            var exponent = index;
            while (index < text.Length && text[index] is >= '0' and <= '9') index++;
            if (index != exponent) end = index;
        }
        return ZoneLegacyFloat.ParseDecimal(text.AsSpan(start, end - start));
    }
}
