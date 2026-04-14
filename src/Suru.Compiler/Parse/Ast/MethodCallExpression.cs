namespace Suru.Compiler.Parse.Ast;

public sealed class MethodCallExpression(Expression receiver, string methodName, IReadOnlyList<Expression> args) : Expression
{
    public Expression Receiver { get; } = receiver;
    public string MethodName { get; } = methodName;
    public IReadOnlyList<Expression> Args { get; } = args;
}
