using System.Globalization;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Tracing.Sdk.Canonicalize;

/// <summary>
/// Canonical XML via Exclusive C14N 1.0 without comments
/// (http://www.w3.org/2001/10/xml-exc-c14n#), producing the same bytes as the
/// PHP SDK's <c>DOMDocument::C14N(true)</c> on a document loaded with
/// <c>preserveWhiteSpace = false</c>. Attribute/namespace ordering and
/// insignificant whitespace are normalized; prefixes stay significant.
/// </summary>
/// <remarks>
/// Serialization is .NET's <see cref="XmlDsigExcC14NTransform"/>. Around it:
/// <list type="bullet">
/// <item>bytes are decoded like an XML parser would (BOM, then the declared
/// encoding, UTF-8 by default), as libxml does in the PHP SDK;</item>
/// <item>the XML declaration is dropped before parsing, since
/// <see cref="XmlReader"/> refuses version="1.1" and C14N drops it anyway;</item>
/// <item>whitespace is removed by the same rules libxml uses for
/// <c>preserveWhiteSpace = false</c>, so the two SDKs agree;</item>
/// <item>namespace URIs must be absolute RFC 3986 URIs, as libxml's C14N
/// requires, and are written with "&amp;" unescaped, as libxml writes them.</item>
/// </list>
/// DTDs are ignored, never processed: a reference to any entity beyond the
/// five predefined ones is a parse error, so untrusted input can never
/// trigger XXE (file disclosure / SSRF).
/// </remarks>
public sealed class XmlCanonicalizer : ICanonicalizer
{
    private static readonly Regex XmlDeclaration = new(
        @"^<\?xml\s+version\s*=\s*(['""])1\.[0-9]+\1(?:\s+encoding\s*=\s*(['""])[A-Za-z][A-Za-z0-9._-]*\2)?(?:\s+standalone\s*=\s*(['""])(?:yes|no)\3)?\s*\?>",
        RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredEncoding = new(
        @"^<\?xml[^>]*?\bencoding\s*=\s*[""']([A-Za-z][A-Za-z0-9._-]*)[""']",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// An absolute URI as libxml 2.15's RFC 3986 parser (uri.c) accepts it,
    /// which is what its C14N requires of every namespace declaration. Two
    /// libxml leniencies are kept: anything goes between the brackets of an
    /// IP literal, and a fragment may contain "[" and "]". A ":" after the host
    /// must be followed by a port number.
    /// </summary>
    private static readonly Regex NamespaceUri = BuildNamespaceUriPattern();

    public byte[] Canonicalize(byte[] rawData)
    {
        var (source, crBefore) = NormalizeSource(Decode(rawData));
        source = BlankOutXmlDeclaration(source);

        var (document, textPositions) = Parse(source);
        var root = document.DocumentElement
            ?? throw new CanonicalizationException("Invalid XML payload: document has no root element");

        AssertAbsoluteNamespaces(root);
        new BlankTextRemover(source, crBefore, textPositions).Remove(root, BlankTextRemover.SpaceInherit);

        var transform = new XmlDsigExcC14NTransform(includeComments: false);
        transform.LoadInput(document);

        using var output = (Stream)transform.GetOutput(typeof(Stream));
        using var reader = new StreamReader(output, new UTF8Encoding(false, true));

        return new UTF8Encoding(false, true).GetBytes(UnescapeNamespaceAmpersands(reader.ReadToEnd()));
    }

    private static Regex BuildNamespaceUriPattern()
    {
        const string hex = "%[0-9A-Fa-f]{2}";
        const string pchar = $@"(?:[A-Za-z0-9\-._~!$&'()*+,;=:@]|{hex})";
        const string segment = pchar + "*";
        const string userinfo = $@"(?:[A-Za-z0-9\-._~!$&'()*+,;=:]|{hex})*@";
        const string host = $@"(?:\[[^\]]*\]|(?:[A-Za-z0-9\-._~!$&'()*+,;=]|{hex})*)";
        const string authority = $"(?:{userinfo})?{host}(?::[0-9]+)?";
        const string hierPart = $"(?://{authority}(?:/{segment})*|/(?:{pchar}+(?:/{segment})*)?|{pchar}+(?:/{segment})*|)";
        const string query = $@"(?:\?(?:{pchar}|[/?])*)?";
        const string fragment = $@"(?:#(?:{pchar}|[/?\[\]])*)?";

        return new Regex($"^[A-Za-z][A-Za-z0-9+.-]*:{hierPart}{query}{fragment}$", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// libxml writes namespace declaration values unescaped, and "&amp;" is the
    /// only character a valid namespace URI can hold that C14N would otherwise
    /// escape. Match it so the bytes, and so the hashes, agree with the PHP SDK.
    /// </summary>
    /// <remarks>
    /// Canonical output is regular enough to scan: text never holds a raw "&lt;",
    /// a processing instruction runs to the first "?&gt;", and inside a tag every
    /// attribute is <c> name="value"</c> with no raw quote in the value.
    /// </remarks>
    private static string UnescapeNamespaceAmpersands(string canonical)
    {
        if (!canonical.Contains("&amp;", StringComparison.Ordinal))
        {
            return canonical;
        }

        var output = new StringBuilder(canonical.Length);
        var i = 0;

        while (i < canonical.Length)
        {
            var open = canonical.IndexOf('<', i);

            if (open < 0)
            {
                output.Append(canonical, i, canonical.Length - i);
                break;
            }

            output.Append(canonical, i, open - i);

            if (canonical[open + 1] == '?')
            {
                var end = canonical.IndexOf("?>", open, StringComparison.Ordinal) + 2;
                output.Append(canonical, open, end - open);
                i = end;
                continue;
            }

            var j = open + 1;

            while (canonical[j] != ' ' && canonical[j] != '>')
            {
                j++;
            }

            output.Append(canonical, open, j - open);

            while (canonical[j] == ' ')
            {
                var equals = canonical.IndexOf("=\"", j, StringComparison.Ordinal);
                var closeQuote = canonical.IndexOf('"', equals + 2);
                var name = canonical[(j + 1)..equals];
                var value = canonical[(equals + 2)..closeQuote];

                if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    value = value.Replace("&amp;", "&", StringComparison.Ordinal);
                }

                output.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
                j = closeQuote + 1;
            }

            output.Append('>');
            i = j + 1;
        }

        return output.ToString();
    }

    /// <summary>Decode bytes the way an XML parser would: BOM, then declared encoding.</summary>
    private static string Decode(byte[] rawData)
    {
        Encoding encoding;
        var offset = 0;

        if (rawData is [0xEF, 0xBB, 0xBF, ..])
        {
            encoding = new UTF8Encoding(false, true);
            offset = 3;
        }
        else if (rawData is [0xFE, 0xFF, ..])
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
        }
        else if (rawData is [0xFF, 0xFE, ..])
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
        }
        else
        {
            var head = Encoding.Latin1.GetString(rawData, 0, Math.Min(rawData.Length, 256));
            var declared = DeclaredEncoding.Match(head);
            encoding = new UTF8Encoding(false, true);

            if (declared.Success)
            {
                try
                {
                    encoding = Encoding.GetEncoding(declared.Groups[1].Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                }
                catch (ArgumentException e)
                {
                    throw new CanonicalizationException($"Invalid XML payload: unsupported encoding \"{declared.Groups[1].Value}\"", e);
                }
            }
        }

        try
        {
            return encoding.GetString(rawData, offset, rawData.Length - offset);
        }
        catch (DecoderFallbackException e)
        {
            throw new CanonicalizationException($"Invalid XML payload: input is not valid {encoding.WebName}", e);
        }
    }

    /// <summary>
    /// XML 1.0 section 2.11 line-ending normalization, with a record of where
    /// the original had a carriage return: libxml splits character data there,
    /// which affects which whitespace it keeps.
    /// </summary>
    private static (string Source, HashSet<int> CrBefore) NormalizeSource(string text)
    {
        var crBefore = new HashSet<int>();
        var normalized = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                crBefore.Add(normalized.Length);
                normalized.Append('\n');

                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else
            {
                normalized.Append(text[i]);
            }
        }

        return (normalized.ToString(), crBefore);
    }

    /// <summary>
    /// Replace a well-formed XML declaration with spaces (keeping every other
    /// character's line and column) so XmlReader never sees it. It carries no
    /// content: C14N drops it, and the encoding was already applied.
    /// </summary>
    private static string BlankOutXmlDeclaration(string source)
    {
        var match = XmlDeclaration.Match(source);

        if (!match.Success)
        {
            return source;
        }

        var blank = Regex.Replace(match.Value, "[^\n]", " ");

        return blank + source[match.Length..];
    }

    /// <summary>
    /// Parse into an XmlDocument by hand, rather than XmlDocument.Load, to
    /// keep each text run's source position for the whitespace pass. Adjacent
    /// text and whitespace nodes are merged into one text node, as in libxml.
    /// </summary>
    private static (XmlDocument Document, Dictionary<XmlText, (int Line, int Column)> TextPositions) Parse(string source)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var positions = new Dictionary<XmlText, (int, int)>(ReferenceEqualityComparer.Instance);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = false,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(source), settings);
            var lineInfo = (IXmlLineInfo)reader;
            var parents = new Stack<XmlNode>();
            parents.Push(document);
            XmlText? openText = null;

            while (reader.Read())
            {
                var parent = parents.Peek();

                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    // Whitespace outside the root element is not part of the document.
                    if (parent == document)
                    {
                        continue;
                    }

                    if (openText is not null)
                    {
                        openText.AppendData(reader.Value);
                    }
                    else
                    {
                        openText = document.CreateTextNode(reader.Value);
                        positions[openText] = (lineInfo.LineNumber, lineInfo.LinePosition);
                        parent.AppendChild(openText);
                    }

                    continue;
                }

                openText = null;

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var element = document.CreateElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                        var isEmpty = reader.IsEmptyElement;

                        while (reader.MoveToNextAttribute())
                        {
                            var attribute = document.CreateAttribute(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                            attribute.Value = reader.Value;
                            element.Attributes.Append(attribute);
                        }

                        parent.AppendChild(element);

                        if (!isEmpty)
                        {
                            parents.Push(element);
                        }

                        break;

                    case XmlNodeType.EndElement:
                        parents.Pop();
                        break;

                    case XmlNodeType.CDATA:
                        parent.AppendChild(document.CreateCDataSection(reader.Value));
                        break;

                    case XmlNodeType.Comment:
                        // Dropped by C14N, but still a child for the whitespace rules.
                        parent.AppendChild(document.CreateComment(reader.Value));
                        break;

                    case XmlNodeType.ProcessingInstruction:
                        parent.AppendChild(document.CreateProcessingInstruction(reader.Name, reader.Value));
                        break;

                    // XmlDeclaration and DocumentType carry nothing C14N keeps.
                }
            }
        }
        catch (XmlException e)
        {
            throw new CanonicalizationException("Invalid XML payload: " + e.Message, e);
        }

        return (document, positions);
    }

    /// <summary>
    /// Exclusive C14N in libxml (and so in the PHP SDK) refuses a namespace
    /// declaration whose value is not an absolute RFC 3986 URI — relative, or
    /// holding spaces, non-ASCII, or other characters a URI cannot — even on
    /// declarations the output never uses. An empty value is fine.
    /// </summary>
    private static void AssertAbsoluteNamespaces(XmlElement element)
    {
        foreach (XmlAttribute attribute in element.Attributes)
        {
            var isDeclaration = attribute.Name == "xmlns" || attribute.Prefix == "xmlns";

            if (isDeclaration && attribute.Value.Length > 0 && !NamespaceUri.IsMatch(attribute.Value))
            {
                throw new CanonicalizationException($"Invalid XML payload: namespace URI \"{attribute.Value}\" is not an absolute URI and cannot be canonicalized");
            }
        }

        foreach (var child in element.ChildNodes.OfType<XmlElement>())
        {
            AssertAbsoluteNamespaces(child);
        }
    }

    /// <summary>
    /// Drops whitespace exactly as libxml 2.15 does for a document loaded with
    /// <c>preserveWhiteSpace = false</c> (XML_PARSE_NOBLANKS), because the PHP
    /// SDK's hashes come from that parser. Libxml decides while it streams, one
    /// chunk of literal character data at a time, looking only at what came
    /// before:
    /// <list type="bullet">
    /// <item>chunks are split at <c>&amp;...;</c> references and at carriage
    /// returns; the text a reference produces is always kept and never counts
    /// as literal;</item>
    /// <item>an element starts in its parent's state (a parent already in
    /// "mixed" state gives its child a fresh one), overridden by xml:space;</item>
    /// <item>a chunk is kept when the state is "preserve" or "mixed", when it
    /// holds anything but whitespace, when it is not followed by <c>&lt;</c> or a
    /// carriage return, when it is directly followed by the end tag of an
    /// element with no children yet (<c>&lt;a&gt; &lt;/a&gt;</c>), or when the
    /// element's last or first child so far is a text node;</item>
    /// <item>keeping any chunk while the state is "inherit" switches it to
    /// "mixed", so every later chunk in that element is kept too.</item>
    /// </list>
    /// DTD element declarations, which libxml would also consult, are not read.
    /// </summary>
    private sealed class BlankTextRemover(
        string source,
        HashSet<int> crBefore,
        Dictionary<XmlText, (int Line, int Column)> textPositions)
    {
        // libxml's per-element whitespace state (parser.c, ctxt->space).
        internal const int SpaceInherit = -1;
        private const int SpaceDefault = 0;
        private const int SpacePreserve = 1;
        private const int SpaceMixed = -2;

        private readonly List<int> _lineOffsets = LineOffsets(source);

        public void Remove(XmlElement element, int parentState)
        {
            var state = parentState == SpaceMixed ? SpaceInherit : parentState;

            switch (element.GetAttribute("xml:space"))
            {
                case "default":
                    state = SpaceDefault;
                    break;
                case "preserve":
                    state = SpacePreserve;
                    break;
            }

            var kept = new List<XmlNode>();

            foreach (var child in element.ChildNodes.Cast<XmlNode>().ToList())
            {
                if (child is not XmlText text)
                {
                    kept.Add(child);

                    if (child is XmlElement childElement)
                    {
                        Remove(childElement, state);
                    }

                    continue;
                }

                var data = new StringBuilder();
                var hasText = false;

                foreach (var piece in Pieces(text))
                {
                    var keep = true;

                    if (piece.Literal)
                    {
                        keep = !IsIgnorable(piece, state, kept, hasText);

                        if (keep && state == SpaceInherit)
                        {
                            state = SpaceMixed;
                        }
                    }

                    if (keep)
                    {
                        data.Append(piece.Text);
                        hasText = true;
                    }
                }

                if (hasText)
                {
                    text.Data = data.ToString();
                    kept.Add(text);
                }
                else
                {
                    element.RemoveChild(text);
                }
            }
        }

        private static bool IsIgnorable(Piece chunk, int state, List<XmlNode> kept, bool pendingText)
        {
            if (state is SpacePreserve or SpaceMixed || !IsBlank(chunk.Text))
            {
                return false;
            }

            if (chunk.Next is not ('<' or '\r'))
            {
                return false;
            }

            // Text from earlier pieces of this same node is already a child in libxml.
            var hasChildren = kept.Count > 0 || pendingText;

            if (!hasChildren && chunk.Next == '<' && chunk.AfterNext == '/')
            {
                return false;
            }

            var lastIsText = pendingText || (kept.Count > 0 && kept[^1] is XmlText);
            var firstIsText = kept.Count > 0 ? kept[0] is XmlText : pendingText;

            return !lastIsText && !firstIsText;
        }

        /// <summary>
        /// Split a text node back into the pieces libxml saw: literal chunks,
        /// each with the raw character that followed it, and reference text.
        /// </summary>
        private List<Piece> Pieces(XmlText node)
        {
            var (line, column) = textPositions[node];
            var start = _lineOffsets[line - 1] + column - 1;
            var end = source.IndexOf('<', start);
            var pieces = new List<Piece>();
            var chunkStart = start;

            void Flush(int at, char next)
            {
                if (at > chunkStart)
                {
                    var afterNext = at + 1 < source.Length ? source[at + 1] : '\0';
                    pieces.Add(new Piece(true, source[chunkStart..at], next, afterNext));
                }
            }

            for (var i = start; i < end; i++)
            {
                if (crBefore.Contains(i) && i > chunkStart)
                {
                    Flush(i, '\r');
                    chunkStart = i;
                }
                else if (source[i] == '&')
                {
                    Flush(i, '&');
                    var close = source.IndexOf(';', i);
                    pieces.Add(new Piece(false, DecodeReference(source[(i + 1)..close]), '\0', '\0'));
                    chunkStart = close + 1;
                    i = close;
                }
            }

            Flush(end, '<');

            return pieces;
        }

        private static string DecodeReference(string name) => name switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            _ when name.StartsWith("#x", StringComparison.OrdinalIgnoreCase) =>
                char.ConvertFromUtf32(int.Parse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            // XmlReader has already rejected every entity but the predefined ones.
            _ => char.ConvertFromUtf32(int.Parse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture)),
        };

        private static bool IsBlank(string text) => text.Length > 0 && text.All(c => c is ' ' or '\t' or '\n' or '\r');

        private static List<int> LineOffsets(string source)
        {
            var offsets = new List<int> { 0 };

            for (var i = source.IndexOf('\n'); i != -1; i = source.IndexOf('\n', i + 1))
            {
                offsets.Add(i + 1);
            }

            return offsets;
        }

        private readonly record struct Piece(bool Literal, string Text, char Next, char AfterNext);
    }
}
