namespace Suru.Compiler.Lex;

public enum TokenKind
{
    // Sentinels / identifiers
    Eof,
    Identifier,

    // Literals
    True,
    False,
    IntLiteral,
    FloatLiteral,
    StringLiteral,

    // Punctuation
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    Comma,
    Dot,
    Colon,
    Minus,
    // Used only in type annotation positions (Array<T>); not emitted for comparisons.
    LessThan,
    GreaterThan,

    // Keywords
    Let,
    Not,
    And,
    Or,
    Match,
    Wildcard,
    Fn,
    Return,
    Void,
    While,
    Include,
    Type,
}
