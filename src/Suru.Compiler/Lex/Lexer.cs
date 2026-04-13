
namespace Suru.Compiler.Lex;

public sealed class Lexer(string source)
{
    private int _pos;
    private int _line = 1;
    private int _column = 1;

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

            int startLine = _line, startCol = _column;
            switch (c)
            {
                case '(': Advance(); return new Token(TokenKind.LeftParen, startLine, startCol);
                case ')': Advance(); return new Token(TokenKind.RightParen, startLine, startCol);
                case ',': Advance(); return new Token(TokenKind.Comma, startLine, startCol);
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
