namespace Suru.Compiler.Lex;

public record Token
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }

    public Token(TokenKind kind, string text, int line, int column)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
    }

    public Token(TokenKind kind, int line, int column)
    {
        Kind = kind;
        Line = line;
        Column = column;
        Text = String.Empty;
    }
}

