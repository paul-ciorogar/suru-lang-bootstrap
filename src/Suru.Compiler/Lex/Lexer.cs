
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

            // Future token kinds will be added here.
            break;
        }

        return new Token(TokenKind.Eof, "", _line, _column);
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
