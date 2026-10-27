namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// The byte-oriented Data syntax consumed by native Zone commands. A space nests
/// values; a comma restores the current list's parent. Lines use indentation.
/// This is not a shell command parser. Results own their bytes and child lists.
/// </summary>
public sealed class GmCommandData
{
    private readonly byte[] _value;
    private readonly List<GmCommandData> _children = [];
    public ReadOnlySpan<byte> Value => _value;
    public IReadOnlyList<GmCommandData> Children { get; }

    private GmCommandData(byte[] value)
    {
        _value = value;
        Children = _children.AsReadOnly();
    }

    // XlAtoI64 (33020EB0), with decimal prefix parsing and wrapping arithmetic.
    // XlAtoI32 has the same low 32 bits, including on overflow.
    public long Int64Value
    {
        get
        {
            var index = 0;
            while (index < _value.Length && _value[index] is 9 or 10 or 11 or 12 or 13 or 32) index++;
            var negative = index < _value.Length && _value[index] == '-';
            if (index < _value.Length && _value[index] is (byte)'+' or (byte)'-') index++;
            long value = 0;
            while (index < _value.Length)
            {
                var digit = _value[index];
                // Native indexes 3316E2F0 with a signed char. Its preceding
                // 128 bytes are zero, so every high byte contributes digit zero.
                if (digit < 128 && digit is not (>= (byte)'0' and <= (byte)'9')) break;
                value = unchecked(value * 10 + (digit >= 128 ? 0 : digit - '0'));
                index++;
            }
            return negative ? unchecked(-value) : value;
        }
    }

    public int Int32Value => unchecked((int)Int64Value);

    public static bool TryRead(ReadOnlySpan<byte> parameters, out GmCommandData? result)
    {
        if (parameters.Length > GmCommand.MaximumParameterBytes)
            throw new ArgumentOutOfRangeException(nameof(parameters));
        // OnGmCommand takes strnlen before Data::Load, even though wire strings
        // retain their full byte count. Data::Load then skips an optional UTF-8 BOM.
        var zero = parameters.IndexOf((byte)0);
        if (zero >= 0) parameters = parameters[..zero];
        return TryReadDocument(parameters, out result);
    }

    /// <summary>Data::Load's explicit-length file/document boundary, independent of command wire limits.</summary>
    public static bool TryReadDocument(ReadOnlySpan<byte> document, out GmCommandData? result)
    {
        if (document.StartsWith("\uFEFF"u8)) document = document[3..];
        var parser = new Parser(document.ToArray());
        result = parser.Read();
        return result is not null;
    }

    // Data::Load 330061D0 and helpers 33004860..33006100. Comments and metadata
    // affect syntax/indentation but are not command arguments.
    private sealed class Parser(byte[] input)
    {
        private readonly GmCommandData _root = new([]);
        // Native leaves unused indentation slots uninitialized, and block values
        // can read them before the current line stores its indent. Zero initialization
        // gives those otherwise undefined cases deterministic behavior per command.
        private readonly int[] _indents = new int[128];
        private readonly GmCommandData?[] _lineNodes = new GmCommandData?[128];
        private GmCommandData _parent = null!;
        private int _offset;
        private int _current = -1;
        private int _lineLevel;
        private int _parentheses;
        private bool _tabsForbidden = true;
        private bool _failed;

        public GmCommandData? Read()
        {
            _indents[0] = -1;
            _lineNodes[0] = _root;
            Advance();
            ReadLine();
            while (_current >= 0 && !_failed)
            {
                ReadNewline();
                // Native 33006100 tests unread bytes after taking lookahead.
                // A final one-byte line with no terminator is consequently skipped.
                if (_offset == input.Length) break;
                ReadLine();
            }
            return _failed ? null : _root;
        }

        private void Fail() { _failed = true; _current = -1; }

        private void Advance()
        {
            if (_failed || _offset == input.Length) { _current = -1; return; }
            _current = input[_offset++];
            if (_current == '\r' && _offset < input.Length && input[_offset] == '\n') _offset++;
            if (_current == '\t' && _tabsForbidden) Fail();
        }

        private int SkipWhitespace()
        {
            var count = 0;
            while (_current is ' ' or '\t' || (_parentheses != 0 && _current is '\r' or '\n'))
            {
                Advance();
                count++;
            }
            return count;
        }

        private void ReadNewline()
        {
            if (_current == '\r')
            {
                Advance();
                if (_current == '\n') Advance();
            }
            else if (_current == '\n') Advance();
            else Fail();
        }

        private void ReadLine()
        {
            if (_lineLevel >= 127) { Fail(); return; }
            var previousLevel = _lineLevel;
            var indent = SkipWhitespace();
            if (_indents[_lineLevel] < indent) _lineLevel++;
            else while (indent <= _indents[_lineLevel - 1]) _lineLevel--;
            _parent = _lineNodes[_lineLevel - 1]!;
            var first = ReadList();
            SkipWhitespace();
            if (first is null) _lineLevel = previousLevel;
            else
            {
                _indents[_lineLevel] = indent;
                _lineNodes[_lineLevel] = first;
            }
        }

        private GmCommandData? ReadList()
        {
            var parent = _parent;
            var first = ReadNode();
            SkipWhitespace();
            while (_current >= 0 && _current != ')' && (_parentheses != 0 || _current is not ('\r' or '\n')))
            {
                if (_current == ',') { Advance(); _parent = parent; }
                SkipWhitespace();
                ReadNode();
                SkipWhitespace();
            }
            return first;
        }

        private GmCommandData? ReadNode()
        {
            if (_current != '(') return ReadValue();
            Advance();
            _parentheses++;
            SkipWhitespace();
            var first = ReadList();
            SkipWhitespace();
            _parentheses--;
            if (_current == ')') Advance();
            else Fail();
            return first;
        }

        private GmCommandData? ReadValue()
        {
            if (_current == '#')
            {
                Advance();
                if (_current == '?')
                {
                    Advance();
                    var parent = _parent;
                    _parent = new([]);
                    ReadList();
                    _parent = parent;
                    if (_lineLevel >= 2) Fail();
                }
                else while (_current > 0 && _current is not ('\r' or '\n')) Advance();
                return null;
            }

            List<byte> bytes = [];
            var quoted = _current is '\'' or '"';
            if (quoted)
            {
                var quote = _current;
                var forbidTabs = _tabsForbidden;
                _tabsForbidden = false;
                Advance();
                while (_current >= 0 && _current != quote)
                {
                    if (_current == '\\')
                    {
                        Advance();
                        if (_current is not ('\'' or '"' or '\\')) bytes.Add((byte)'\\');
                        bytes.Add(unchecked((byte)_current));
                        Advance();
                    }
                    else if (_current is '\r' or '\n') { ReadNewline(); bytes.Add((byte)'\n'); }
                    else { bytes.Add((byte)_current); Advance(); }
                }
                if (_current != quote) Fail();
                Advance();
                _tabsForbidden = forbidTabs;
            }
            else if (_current == '\\') ReadBlock(bytes);
            else if (_current >= 0 && _current < 32 && _current is not ('\t' or '\r' or '\n')) Fail();
            else
            {
                while (_current > 32 && _current is not ('(' or ')' or ','))
                {
                    bytes.Add((byte)_current);
                    Advance();
                }
            }

            if (bytes.Count == 0 && !quoted) return null;
            var value = new GmCommandData(bytes.ToArray());
            _parent._children.Add(value);
            _parent = value;
            return value;
        }

        private void ReadBlock(List<byte> bytes)
        {
            Advance();
            SkipWhitespace();
            var saved = (_current, _offset);
            ReadNewline();
            var parentIndent = _indents[_lineLevel];
            var baseIndent = SkipWhitespace();
            var indent = baseIndent;
            while (_current >= 0 && (parentIndent < indent || _current is '\r' or '\n'))
            {
                for (var index = baseIndent; index < indent; index++) bytes.Add((byte)' ');
                while (_current > 0 && _current is not ('\r' or '\n'))
                {
                    bytes.Add((byte)_current);
                    Advance();
                }
                saved = (_current, _offset);
                ReadNewline();
                indent = SkipWhitespace();
                bytes.Add((byte)'\n');
            }
            (_current, _offset) = saved;
        }
    }
}
