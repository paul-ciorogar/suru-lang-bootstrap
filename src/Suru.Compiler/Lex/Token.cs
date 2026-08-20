namespace Suru.Compiler.Lex;

public record Token
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }

    /// <summary>
    /// The decoded magnitude of an <see cref="TokenKind.IntLiteral"/>. The lexer decodes it
    /// because it is the stage that knows the base and can report a bad digit against a
    /// position; <see cref="Text"/> stays the raw lexeme so dumps and diagnostics still show
    /// what was written.
    /// <para>
    /// It is unsigned and reaches one past <see cref="long.MaxValue"/> because a leading '-'
    /// is folded into the literal by the parser, and 9223372036854775808 is in range only
    /// once it has been: the lexer cannot tell the two cases apart, so the parser makes the
    /// range check.
    /// </para>
    /// </summary>
    public ulong IntMagnitude { get; }

    /// <summary>The decoded value of a <see cref="TokenKind.FloatLiteral"/>.</summary>
    public double FloatValue { get; }

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

    public Token(string text, ulong magnitude, int line, int column)
        : this(TokenKind.IntLiteral, text, line, column) => IntMagnitude = magnitude;

    public Token(string text, double value, int line, int column)
        : this(TokenKind.FloatLiteral, text, line, column) => FloatValue = value;
}

