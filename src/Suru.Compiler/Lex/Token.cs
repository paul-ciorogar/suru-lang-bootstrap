namespace Suru.Compiler.Lex;

public record Token(TokenKind Kind, string Text, int Line, int Column);
