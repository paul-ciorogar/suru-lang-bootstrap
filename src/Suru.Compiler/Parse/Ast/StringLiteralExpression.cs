namespace Suru.Compiler.Parse.Ast;

public sealed class StringLiteralExpression(string value) : Expression
{
    public string Value { get; } = value;
}
