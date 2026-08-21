namespace Suru.Compiler.Lex;

public enum TokenKind
{
    Eof,
    Identifier,
    True,
    False,
    Let,
    And,
    Or,
    Not,
    IntLiteral,
    FloatLiteral,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    Comma,
    Colon,
    /// <summary>Opens a test directive. Only ever produced in <see cref="BuildMode.Test"/>.</summary>
    Hash,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}
