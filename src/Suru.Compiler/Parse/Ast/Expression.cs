namespace Suru.Compiler.Parse.Ast;

public abstract class Expression { }

public sealed class BoolLiteral(bool value) : Expression
{
    public bool Value { get; } = value;
}

public sealed class IntLiteral(long value) : Expression
{
    public long Value { get; } = value;
}

public sealed class FloatLiteral(double value) : Expression
{
    public double Value { get; } = value;
}

public sealed class CallExpression(string name, IReadOnlyList<Expression> args) : Expression
{
    public string Name { get; } = name;
    public IReadOnlyList<Expression> Args { get; } = args;
}
