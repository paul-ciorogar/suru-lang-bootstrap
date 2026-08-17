namespace Suru.Compiler.Parse.Ast;

public abstract class Expression(SourcePosition position)
{
    public SourcePosition Position { get; } = position;

    /// <summary>Filled in by the semantic analyzer; null until then, or if analysis failed.</summary>
    public SuruType? Type { get; internal set; }
}

public sealed class BoolLiteral(SourcePosition position, bool value) : Expression(position)
{
    public bool Value { get; } = value;
}

public sealed class IntLiteral(SourcePosition position, long value) : Expression(position)
{
    public long Value { get; } = value;
}

public sealed class FloatLiteral(SourcePosition position, double value) : Expression(position)
{
    public double Value { get; } = value;
}

public sealed class CallExpression(SourcePosition position, string name, IReadOnlyList<Expression> args)
    : Expression(position)
{
    public string Name { get; } = name;
    public IReadOnlyList<Expression> Args { get; } = args;
}
