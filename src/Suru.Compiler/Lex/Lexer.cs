
namespace Suru.Compiler.Lex;

public sealed class Lexer(string source)
{
    private int _pos;
    private int _line = 1;
    private int _column = 1;

    /// <summary>
    /// Returns every token in <paramref name="source"/> in order, ending with a single
    /// <see cref="TokenKind.Eof"/> token. Throws on unrecognised characters.
    /// </summary>
    public static IEnumerable<Token> Tokenize(string source)
    {
        var lexer = new Lexer(source);
        Token t;
        do
        {
            t = lexer.NextToken();
            yield return t;
        } while (t.Kind != TokenKind.Eof);
    }

    /// <summary>Scans and returns the next token from the source.</summary>
    public Token NextToken()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];

            if (char.IsWhiteSpace(c))
            {
                AdvanceWhitespace();
                continue;
            }

            if (char.IsLetter(c) || c == '_')
                return ReadIdentifierOrKeyword();

            if (char.IsDigit(c))
                return ReadNumber();

            int startLine = _line, startCol = _column;
            switch (c)
            {
                case '(': Advance(); return new Token(TokenKind.LeftParen, startLine, startCol);
                case ')': Advance(); return new Token(TokenKind.RightParen, startLine, startCol);
                case ',': Advance(); return new Token(TokenKind.Comma, startLine, startCol);
                case '.': Advance(); return new Token(TokenKind.Dot, startLine, startCol);
                case ':': Advance(); return new Token(TokenKind.Colon, startLine, startCol);
                case '{': Advance(); return new Token(TokenKind.LeftBrace, startLine, startCol);
                case '}': Advance(); return new Token(TokenKind.RightBrace, startLine, startCol);
                case '[': Advance(); return new Token(TokenKind.LeftBracket, startLine, startCol);
                case ']': Advance(); return new Token(TokenKind.RightBracket, startLine, startCol);
                case '"': return ReadString();
                case '-': Advance(); return new Token(TokenKind.Minus, startLine, startCol);
                case '<': Advance(); return new Token(TokenKind.LessThan, startLine, startCol);
                case '>': Advance(); return new Token(TokenKind.GreaterThan, startLine, startCol);
                case '/' when _pos + 1 < source.Length && source[_pos + 1] == '/':
                    while (_pos < source.Length && source[_pos] != '\n')
                        Advance();
                    break;
                default:
                    throw new Exception($"Unexpected character '{c}' at {_line}:{_column}");
            }
        }

        return new Token(TokenKind.Eof, "", _line, _column);
    }

    private Token ReadIdentifierOrKeyword()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            Advance();
        var text = source[start.._pos];
        var kind = text switch
        {
            "true"  => TokenKind.True,
            "false" => TokenKind.False,
            "let"   => TokenKind.Let,
            "not"   => TokenKind.Not,
            "and"   => TokenKind.And,
            "or"    => TokenKind.Or,
            "match"  => TokenKind.Match,
            "_"      => TokenKind.Wildcard,
            "fn"     => TokenKind.Fn,
            "return" => TokenKind.Return,
            "void"   => TokenKind.Void,
            "while"   => TokenKind.While,
            "include" => TokenKind.Include,
            "type"    => TokenKind.Type,
            "as"      => TokenKind.As,
            _         => TokenKind.Identifier,
        };
        return new Token(kind, text, startLine, startCol);
    }

    private Token ReadString()
    {
        int startLine = _line, startCol = _column;
        Advance(); // consume opening '"'
        var sb = new System.Text.StringBuilder();
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length)
            {
                Advance(); // consume '\'
                char escaped = source[_pos];
                Advance();
                sb.Append(escaped switch
                {
                    'n'  => '\n',
                    't'  => '\t',
                    '\\' => '\\',
                    '"'  => '"',
                    _    => throw new Exception($"Unknown escape sequence '\\{escaped}' at {_line}:{_column}"),
                });
            }
            else
            {
                sb.Append(source[_pos]);
                Advance();
            }
        }
        if (_pos >= source.Length)
            throw new Exception($"Unterminated string literal at {startLine}:{startCol}");
        Advance(); // consume closing '"'
        return new Token(TokenKind.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private Token ReadNumber()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        while (_pos < source.Length && char.IsDigit(source[_pos]))
            Advance();
        if (_pos < source.Length && source[_pos] == '.' && _pos + 1 < source.Length && char.IsDigit(source[_pos + 1]))
        {
            Advance(); // consume '.'
            while (_pos < source.Length && char.IsDigit(source[_pos]))
                Advance();
            return new Token(TokenKind.FloatLiteral, source[start.._pos], startLine, startCol);
        }
        return new Token(TokenKind.IntLiteral, source[start.._pos], startLine, startCol);
    }

    private void Advance()
    {
        _column++;
        _pos++;
    }

    private void AdvanceWhitespace()
    {
        if (source[_pos] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _pos++;
    }
}
