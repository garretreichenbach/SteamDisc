using System.Text;

namespace SteamDisc.Core.Vdf;

/// <summary>
/// Parser for Valve's text KeyValues format — the format of <c>appmanifest_*.acf</c>,
/// <c>libraryfolders.vdf</c>, <c>loginusers.vdf</c> and friends.
/// </summary>
/// <remarks>
/// Deliberately permissive: real Steam files in the wild contain unquoted tokens, C++ style
/// comments, platform conditionals and <c>#base</c> directives. Failing to open a user's
/// library because of a stray comment would be a bad trade.
/// </remarks>
public static class VdfTextReader
{
    public static KvNode ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    public static KvNode Parse(Stream stream)
    {
        // Steam writes UTF-8; detectEncodingFromByteOrderMarks copes with the occasional BOM.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses a document and returns its single root node. Documents with several top-level
    /// nodes are wrapped in a synthetic root named <c>""</c>.
    /// </summary>
    public static KvNode Parse(string text)
    {
        var roots = ParseAll(text);
        return roots.Count switch
        {
            0 => KvNode.Object(string.Empty),
            1 => roots[0],
            _ => WrapRoots(roots),
        };
    }

    /// <summary>Parses a document that may legitimately contain several top-level nodes.</summary>
    public static IReadOnlyList<KvNode> ParseAll(string text)
    {
        var lexer = new Lexer(text);
        var roots = new List<KvNode>();

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                break;
            }

            if (token.Kind != TokenKind.String)
            {
                throw new VdfSyntaxException($"Expected a key but found '{token.Text}'", token.Line, token.Column);
            }

            roots.Add(ParseStatement(lexer, token));
        }

        return roots;
    }

    private static KvNode WrapRoots(IReadOnlyList<KvNode> roots)
    {
        var wrapper = KvNode.Object(string.Empty);
        foreach (var root in roots)
        {
            wrapper.Add(root);
        }

        return wrapper;
    }

    private static KvNode ParseStatement(Lexer lexer, Token keyToken)
    {
        string? condition = null;
        var next = lexer.Next();

        if (next.Kind == TokenKind.Conditional)
        {
            condition = next.Text;
            next = lexer.Next();
        }

        KvNode node;
        switch (next.Kind)
        {
            case TokenKind.String:
                node = KvNode.Leaf(keyToken.Text, next.Text);
                // A trailing conditional binds to the statement: "key" "value" [$WIN32]
                if (lexer.Peek().Kind == TokenKind.Conditional)
                {
                    condition = lexer.Next().Text;
                }

                break;

            case TokenKind.ObjectStart:
                node = KvNode.Object(keyToken.Text);
                ParseObjectBody(lexer, node);
                break;

            case TokenKind.End:
                throw new VdfSyntaxException(
                    $"Key '{keyToken.Text}' has no value", keyToken.Line, keyToken.Column);

            default:
                throw new VdfSyntaxException(
                    $"Expected a value or '{{' after key '{keyToken.Text}' but found '{next.Text}'",
                    next.Line,
                    next.Column);
        }

        node.Condition = condition;
        return node;
    }

    private static void ParseObjectBody(Lexer lexer, KvNode parent)
    {
        while (true)
        {
            var token = lexer.Next();
            switch (token.Kind)
            {
                case TokenKind.ObjectEnd:
                    return;

                case TokenKind.End:
                    throw new VdfSyntaxException($"Unterminated object '{parent.Key}'", token.Line, token.Column);

                case TokenKind.String:
                    parent.Add(ParseStatement(lexer, token));
                    break;

                default:
                    throw new VdfSyntaxException(
                        $"Unexpected '{token.Text}' inside object '{parent.Key}'", token.Line, token.Column);
            }
        }
    }

    private enum TokenKind
    {
        End,
        String,
        ObjectStart,
        ObjectEnd,
        Conditional,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    private sealed class Lexer
    {
        private readonly string _text;
        private int _position;
        private int _line = 1;
        private int _column = 1;
        private Token? _peeked;

        public Lexer(string text) => _text = text;

        public Token Peek() => _peeked ??= Read();

        public Token Next()
        {
            if (_peeked is { } peeked)
            {
                _peeked = null;
                return peeked;
            }

            return Read();
        }

        private Token Read()
        {
            SkipTrivia();

            if (_position >= _text.Length)
            {
                return new Token(TokenKind.End, "<end of file>", _line, _column);
            }

            var line = _line;
            var column = _column;
            var c = _text[_position];

            switch (c)
            {
                case '{':
                    Advance();
                    return new Token(TokenKind.ObjectStart, "{", line, column);
                case '}':
                    Advance();
                    return new Token(TokenKind.ObjectEnd, "}", line, column);
                case '"':
                    return new Token(TokenKind.String, ReadQuoted(), line, column);
                case '[':
                    return new Token(TokenKind.Conditional, ReadConditional(), line, column);
                default:
                    return new Token(TokenKind.String, ReadBare(), line, column);
            }
        }

        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (char.IsWhiteSpace(c))
                {
                    Advance();
                    continue;
                }

                // Both // and the rarer /* */ appear in hand-edited Steam config files.
                if (c == '/' && _position + 1 < _text.Length)
                {
                    if (_text[_position + 1] == '/')
                    {
                        while (_position < _text.Length && _text[_position] is not ('\n' or '\r'))
                        {
                            Advance();
                        }

                        continue;
                    }

                    if (_text[_position + 1] == '*')
                    {
                        Advance();
                        Advance();
                        while (_position < _text.Length &&
                               !(_text[_position] == '*' && _position + 1 < _text.Length && _text[_position + 1] == '/'))
                        {
                            Advance();
                        }

                        if (_position < _text.Length)
                        {
                            Advance();
                            Advance();
                        }

                        continue;
                    }
                }

                return;
            }
        }

        private string ReadQuoted()
        {
            var startLine = _line;
            var startColumn = _column;
            Advance(); // opening quote

            var builder = new StringBuilder();
            while (true)
            {
                if (_position >= _text.Length)
                {
                    throw new VdfSyntaxException("Unterminated quoted string", startLine, startColumn);
                }

                var c = _text[_position];
                if (c == '"')
                {
                    Advance();
                    return builder.ToString();
                }

                if (c == '\\' && _position + 1 < _text.Length)
                {
                    Advance();
                    var escape = _text[_position];
                    Advance();
                    switch (escape)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case 'v': builder.Append('\v'); break;
                        case '\\': builder.Append('\\'); break;
                        case '"': builder.Append('"'); break;
                        default:
                            // Unrecognised escapes are kept verbatim rather than dropped, so a
                            // Windows path written without doubled separators still survives.
                            builder.Append('\\').Append(escape);
                            break;
                    }

                    continue;
                }

                builder.Append(c);
                Advance();
            }
        }

        private string ReadBare()
        {
            var start = _position;
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (char.IsWhiteSpace(c) || c is '"' or '{' or '}')
                {
                    break;
                }

                Advance();
            }

            return _text[start.._position];
        }

        private string ReadConditional()
        {
            Advance(); // '['
            var start = _position;
            while (_position < _text.Length && _text[_position] != ']')
            {
                Advance();
            }

            var value = _text[start.._position];
            if (_position < _text.Length)
            {
                Advance(); // ']'
            }

            return value;
        }

        private void Advance()
        {
            if (_text[_position] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _position++;
        }
    }
}
