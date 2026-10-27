using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AAEmu.Zone.Ai;

// The lookup loader uses the same literal names and direct content in text CXmlNode and CBinaryXmlNode.
internal static class ZoneAiLookupXml
{
    internal sealed class Node(string name, string content, IReadOnlyList<KeyValuePair<string, string>> attributes, IReadOnlyList<Node> children)
    {
        public string Name { get; } = name;
        public string Content { get; internal set; } = content;
        public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; } = attributes;
        public IReadOnlyList<Node> Children { get; } = children;
    }

    public static bool TryRead(ReadOnlyMemory<byte> bytes, out Node? root)
    {
        root = null;
        if (bytes.Length >= 44 && bytes.Span[..8].SequenceEqual("CryXmlB\0"u8))
            return TryReadBinary(bytes.Span, out root);
        try
        {
            var source = PreserveCharacterBoundaries(DecodeText(bytes.Span), out var skippedDtd);
            using var raw = new XmlTextReader(new StringReader(source))
            {
                Namespaces = false,
                Normalization = true,
                WhitespaceHandling = WhitespaceHandling.All,
                EntityHandling = EntityHandling.ExpandCharEntities,
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null
            };
            // The legacy wrapper supplies DTD default attributes while retaining unresolved entity
            // events and literal, namespace-disabled names. XmlReader.Create merges these events.
#pragma warning disable CS0618
            using var reader = new XmlValidatingReader(raw)
            {
                ValidationType = ValidationType.None,
                EntityHandling = EntityHandling.ExpandCharEntities,
                XmlResolver = null!
            };
#pragma warning restore CS0618
            var stack = new Stack<Node>();
            var entities = new HashSet<string>(StringComparer.Ordinal);
            var standalone = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        standalone = reader.GetAttribute("standalone") == "yes";
                        break;
                    case XmlNodeType.DocumentType:
                        skippedDtd |= reader.GetAttribute("SYSTEM") is not null || reader.GetAttribute("PUBLIC") is not null;
                        ReadEntityNames(reader.Value, entities);
                        break;
                    case XmlNodeType.Element:
                    {
                        var attributes = new List<KeyValuePair<string, string>>();
                        if (reader.MoveToFirstAttribute())
                        {
                            do { attributes.Add(new(reader.Name, reader.Value)); } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        var node = new Node(reader.Name, "", attributes, new List<Node>());
                        if (stack.TryPeek(out var parent)) ((List<Node>)parent.Children).Add(node);
                        else root = node;
                        if (!reader.IsEmptyElement) stack.Push(node);
                        break;
                    }
                    case XmlNodeType.EndElement:
                        stack.Pop();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (stack.TryPeek(out var current)) AppendContent(current, reader.Value);
                        break;
                    case XmlNodeType.EntityReference:
                        // Expat does not load external DTDs or parameter entities. Undeclared entities are
                        // skipped when such a subset was skipped, unless standalone="yes" requires an error.
                        if (!skippedDtd || standalone || entities.Contains(reader.Name)) reader.ResolveEntity();
                        break;
                }
            }
            return root is not null;
        }
        catch (Exception error) when (error is XmlException or DecoderFallbackException)
        {
            root = null;
            return false;
        }
    }

    // SetupParser 36594DF0 sets the protocol encoding to UTF-8, ignoring XML encoding declarations.
    // Expat still recognizes a UTF-16 BOM or the initial '<' byte pattern without a BOM.
    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding = new UTF8Encoding(false, true);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            bytes = bytes[2..];
        }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            bytes = bytes[2..];
        }
        else if (bytes.StartsWith(new byte[] { 0, (byte)'<' })) encoding = new UnicodeEncoding(true, false, true);
        else if (bytes.StartsWith(new byte[] { (byte)'<', 0 })) encoding = new UnicodeEncoding(false, false, true);
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        return encoding.GetString(bytes);
    }

    // 36524FE0 -> 36593AB0: discard each all-whitespace callback, then append direct text.
    // Expat emits line endings separately even inside CDATA and internal entity replacements.
    private static void AppendContent(Node node, string value)
    {
        foreach (var chunk in value.Split(['\r', '\n']))
            if (chunk.Any(character => character is not (' ' or '\t' or '\r' or '\n')))
                node.Content += chunk;
    }

    // XmlTextReader combines character references with adjacent text. Inert processing instructions
    // preserve the native callback boundaries without changing element names, attributes or DTD values.
    private static string PreserveCharacterBoundaries(string source, out bool skippedDtd)
    {
        skippedDtd = false;
        var output = new StringBuilder(source.Length);
        var entities = new HashSet<string>(["amp", "lt", "gt", "apos", "quot"], StringComparer.Ordinal);
        var standalone = false;
        var offset = 0;
        while (offset < source.Length)
        {
            if (source[offset] == '<')
            {
                var end = MarkupEnd(source, offset);
                var markup = source[offset..end];
                if (markup.StartsWith("<?xml", StringComparison.Ordinal) && markup.Length > 5 &&
                    markup[5] is ' ' or '\t' or '\r' or '\n')
                {
                    standalone = Regex.IsMatch(markup, "standalone\\s*=\\s*(['\"])yes\\1", RegexOptions.CultureInvariant);
                    // This Expat accepts a 1.1 declaration while retaining its XML 1.0 parser rules.
                    // XmlTextReader rejects that declaration, so normalize the proven accepted version.
                    markup = Regex.Replace(markup, "\\bversion\\s*=\\s*(['\"])1\\.1\\1", "version='1.0'", RegexOptions.CultureInvariant);
                }
                if (markup.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
                {
                    markup = SkipParameterSubset(markup, ref skippedDtd);
                    skippedDtd |= Regex.IsMatch(markup, "^<!DOCTYPE\\s+[^\\s>]+\\s+(SYSTEM|PUBLIC)\\s", RegexOptions.CultureInvariant);
                    var subset = markup.IndexOf('[');
                    if (subset >= 0) ReadEntityNames(markup[(subset + 1)..], entities);
                }
                if (skippedDtd && !standalone && !markup.StartsWith("<![CDATA[", StringComparison.Ordinal) &&
                    !markup.StartsWith("<!--", StringComparison.Ordinal) && !markup.StartsWith("<?", StringComparison.Ordinal))
                    markup = RemoveUnknownReferences(markup, entities);
                output.Append(markup);
                offset = end;
            }
            else if (source[offset] == '&')
            {
                var end = source.IndexOf(';', offset);
                if (end < 0) { output.Append(source.AsSpan(offset)); break; }
                output.Append("<?zone-lookup-boundary?>");
                var reference = source[(offset + 1)..end];
                if (!reference.StartsWith('#')) VerifyEntityName(reference);
                if (!skippedDtd || standalone || reference.StartsWith('#') || entities.Contains(reference))
                    output.Append(source.AsSpan(offset, end - offset + 1));
                output.Append("<?zone-lookup-boundary?>");
                offset = end + 1;
            }
            else output.Append(source[offset++]);
        }
        return output.ToString();
    }

    private static string RemoveUnknownReferences(string source, HashSet<string> entities)
    {
        var result = new StringBuilder(source.Length);
        for (var offset = 0; offset < source.Length;)
        {
            var remaining = source.AsSpan(offset);
            // Entity replacement strings can contain markup. Its CDATA is literal even when an
            // external DTD was skipped; rewriting an apparent reference here would change a sample.
            if (remaining.StartsWith("<![CDATA[") || remaining.StartsWith("<!--") || remaining.StartsWith("<?"))
            {
                var end = MarkupEnd(source, offset);
                result.Append(source.AsSpan(offset, end - offset));
                offset = end;
            }
            else if (source[offset] == '&')
            {
                var end = source.IndexOf(';', offset);
                if (end < 0) { result.Append(source.AsSpan(offset)); break; }
                var name = source[(offset + 1)..end];
                if (!name.StartsWith('#')) VerifyEntityName(name);
                if (name.StartsWith('#') || entities.Contains(name)) result.Append(source.AsSpan(offset, end - offset + 1));
                offset = end + 1;
            }
            else result.Append(source[offset++]);
        }
        return result.ToString();
    }

    private static int MarkupEnd(string source, int start)
    {
        var marker = source.AsSpan(start);
        var closing = marker.StartsWith("<!--") ? "-->" : marker.StartsWith("<![CDATA[") ? "]]>" : marker.StartsWith("<?") ? "?>" : null;
        if (closing is not null)
        {
            var end = source.IndexOf(closing, start + 2, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + closing.Length;
        }
        var quote = '\0'; var subset = 0;
        for (var i = start + 1; i < source.Length; i++)
        {
            var character = source[i];
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (source.AsSpan(i).StartsWith("<!--") || source.AsSpan(i).StartsWith("<?")) { i = MarkupEnd(source, i) - 1; continue; }
            if (character is '\'' or '"') quote = character;
            else if (character == '[') subset++;
            else if (character == ']') subset--;
            else if (character == '>' && subset == 0) return i + 1;
        }
        return source.Length;
    }

    private static string SkipParameterSubset(string declaration, ref bool skipped)
    {
        var quote = '\0'; var inSubset = false;
        for (var i = 9; i < declaration.Length; i++)
        {
            var character = declaration[i];
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (declaration.AsSpan(i).StartsWith("<!--") || declaration.AsSpan(i).StartsWith("<?")) { i = MarkupEnd(declaration, i) - 1; continue; }
            if (character is '\'' or '"') quote = character;
            else if (character == '[') inSubset = true;
            else if (character == '%' && inSubset && i + 1 < declaration.Length && !char.IsWhiteSpace(declaration[i + 1]))
            {
                // Expat's default parameter-entity mode stops processing the remaining internal subset.
                // It still checks the syntax of skipped declarations; truncation alone would accept
                // a malformed suffix and replace a previously valid lookup table.
                ValidateSkippedSubset(declaration, i);
                skipped = true;
                return declaration[..i] + "]>";
            }
        }
        return declaration;
    }

    private static void ValidateSkippedSubset(string declaration, int ignoredStart)
    {
        var masked = declaration.ToCharArray();
        var quote = '\0';
        for (var i = 0; i < declaration.Length; i++)
        {
            var character = declaration[i];
            if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
            if (declaration.AsSpan(i).StartsWith("<!--") || declaration.AsSpan(i).StartsWith("<?")) { i = MarkupEnd(declaration, i) - 1; continue; }
            if (character is '\'' or '"') quote = character;
            else if (character == '%' && i + 1 < declaration.Length && !char.IsWhiteSpace(declaration[i + 1]))
            {
                var end = declaration.IndexOf(';', i);
                if (end < 0) throw new XmlException("Unterminated parameter entity reference.");
                var name = declaration[(i + 1)..end];
                VerifyEntityName(name);
                while (i <= end) masked[i++] = ' ';
                i--;
            }
        }
        // The ignored suffix must remain syntactically valid, but its attribute defaults do not
        // resolve general entities. XmlTextReader would resolve and reject an undeclared default.
        var suffix = RemoveUnknownReferences(new string(masked, ignoredStart, masked.Length - ignoredStart),
            new HashSet<string>(["amp", "lt", "gt", "apos", "quot"], StringComparer.Ordinal));
        var validation = new string(masked, 0, ignoredStart) + suffix + "<zone-lookup-validation/>";
        using var reader = new XmlTextReader(new StringReader(validation))
        {
            Namespaces = false,
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null
        };
        while (reader.Read()) { }
    }

    private static void ReadEntityNames(string subset, HashSet<string> names)
    {
        for (var i = 0; i < subset.Length; i++)
        {
            if (subset[i] != '<') continue;
            if (subset.AsSpan(i).StartsWith("<!ENTITY"))
            {
                var start = i + 8;
                while (start < subset.Length && char.IsWhiteSpace(subset[start])) start++;
                var end = start;
                while (end < subset.Length && !char.IsWhiteSpace(subset[end])) end++;
                if (end > start && subset[start] != '%') names.Add(subset[start..end]);
            }
            i = MarkupEnd(subset, i) - 1;
        }
    }

    private static void VerifyEntityName(string name)
    {
        if (name.Length == 0) throw new XmlException("An entity reference must have a name.");
        XmlConvert.VerifyName(name);
    }

    // 365954A0/36591B30: header, node records, child indices, attribute pairs, and string data.
    // Invalid offsets and indices are rejected instead of reproducing native out-of-bounds accesses.
    private static bool TryReadBinary(ReadOnlySpan<byte> bytes, out Node? root)
    {
        root = null;
        var size = U32(bytes, 8); var nodesOffset = U32(bytes, 12); var nodeCount = U32(bytes, 16);
        var attributesOffset = U32(bytes, 20); var attributeCount = U32(bytes, 24);
        var childrenOffset = U32(bytes, 28); var childCount = U32(bytes, 32);
        var stringsOffset = U32(bytes, 36); var stringsSize = U32(bytes, 40);
        if (size > bytes.Length || nodeCount == 0 || nodesOffset < 44 ||
            (ulong)nodesOffset + nodeCount * 28UL > childrenOffset ||
            (ulong)childrenOffset + childCount * 4UL > attributesOffset ||
            (ulong)attributesOffset + attributeCount * 8UL > stringsOffset ||
            (ulong)stringsOffset + stringsSize > size) return false;
        var strings = bytes.Slice((int)stringsOffset, (int)stringsSize);
        var nodes = new Node[(int)nodeCount];
        for (var i = 0; i < nodes.Length; i++)
        {
            var record = (int)nodesOffset + i * 28;
            var firstAttribute = U32(bytes, record + 16);
            var count = U16(bytes, record + 8);
            if ((ulong)firstAttribute + count > attributeCount ||
                !TryString(strings, U32(bytes, record), out var name) ||
                !TryString(strings, U32(bytes, record + 4), out var content)) return false;
            var attributes = new KeyValuePair<string, string>[count];
            for (var j = 0; j < attributes.Length; j++)
            {
                var attribute = (int)attributesOffset + ((int)firstAttribute + j) * 8;
                if (!TryString(strings, U32(bytes, attribute), out var key) ||
                    !TryString(strings, U32(bytes, attribute + 4), out var value)) return false;
                attributes[j] = new(key, value);
            }
            nodes[i] = new(name, content, attributes, new List<Node>());
        }
        for (var i = 0; i < nodes.Length; i++)
        {
            var record = (int)nodesOffset + i * 28;
            var firstChild = U32(bytes, record + 20); var count = U16(bytes, record + 10);
            if ((ulong)firstChild + count > childCount) return false;
            for (var j = 0; j < count; j++)
            {
                var child = U32(bytes, (int)childrenOffset + ((int)firstChild + j) * 4);
                if (child >= nodeCount) return false;
                ((List<Node>)nodes[i].Children).Add(nodes[child]);
            }
        }
        // Lookup traversal has a fixed depth and never follows parent indices. Keep shared child nodes
        // as shared nodes; no recursive materialization is required, including for unusual native graphs.
        root = nodes[0];
        return true;
    }

    private static bool TryString(ReadOnlySpan<byte> strings, uint offset, out string value)
    {
        value = "";
        if (offset >= strings.Length) return false;
        var remaining = strings[(int)offset..]; var end = remaining.IndexOf((byte)0);
        if (end < 0) return false;
        value = Encoding.UTF8.GetString(remaining[..end]);
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
}
