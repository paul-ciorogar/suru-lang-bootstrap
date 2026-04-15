namespace Suru.Compiler.Parse.Ast;

public sealed class ArrayLiteralExpression(IReadOnlyList<Expression> elements) : Expression
{
    public IReadOnlyList<Expression> Elements { get; } = elements;
}
