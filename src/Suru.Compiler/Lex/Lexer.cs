
namespace Suru.Compiler.Lex;

public sealed class Lexer(string source, string sourcePath)
{
    private int _pos;
    private int _line = 1;
    private int _column = 1;

    public string SourcePath => sourcePath;

    internal Token NextToken()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];

            if (char.IsWhiteSpace(c))
            {
                AdvanceWhitespace();
                continue;
            }

            if (char.IsLetter(c))
                return ReadIdentifierOrKeyword();

            if (char.IsDigit(c))
                return ReadNumber();

            if (c == '/' && Peek(1) == '/')
            {
                SkipLineComment();
                continue;
            }

            int startLine = _line, startCol = _column;
            switch (c)
            {
                case '(': Advance(); return new Token(TokenKind.LeftParen, startLine, startCol);
                case ')': Advance(); return new Token(TokenKind.RightParen, startLine, startCol);
                case ',': Advance(); return new Token(TokenKind.Comma, startLine, startCol);
                default:
                    throw new LexException($"{sourcePath}({_line},{_column}): unexpected character '{c}'");
            }
        }

        return new Token(TokenKind.Eof, "", _line, _column);
    }

    private Token ReadIdentifierOrKeyword()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
            Advance();
        var text = source[start.._pos];
        var kind = text switch
        {
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            _ => TokenKind.Identifier,
        };
        return new Token(kind, text, startLine, startCol);
    }

    private Token ReadNumber()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        while (char.IsDigit(Peek()))
            Advance();
        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            Advance(); // consume '.'
            while (char.IsDigit(Peek()))
                Advance();
            return new Token(TokenKind.FloatLiteral, source[start.._pos], startLine, startCol);
        }
        return new Token(TokenKind.IntLiteral, source[start.._pos], startLine, startCol);
    }

    /// <summary>
    /// Consumes <c>//</c> and everything up to — but not including — the newline, so the
    /// newline itself still goes through <see cref="AdvanceWhitespace"/> and keeps the
    /// line counter right. A comment at the end of the file just runs into EOF.
    /// </summary>
    private void SkipLineComment()
    {
        while (_pos < source.Length && Peek() != '\n')
        {
            Advance();
        }
    }

    /// <summary>
    /// The character <paramref name="offset"/> positions ahead of the cursor, or
    /// <c>'\0'</c> past the end of the source — no token may contain a NUL, so every
    /// caller stops on it the same way it stops on a character it does not accept.
    /// </summary>
    private char Peek(int offset = 0) =>
        _pos + offset < source.Length ? source[_pos + offset] : '\0';

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
