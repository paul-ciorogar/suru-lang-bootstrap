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

/// <summary>A use of a name bound by a <see cref="LetStatement"/>.</summary>
public sealed class IdentifierExpression(SourcePosition position, string name) : Expression(position)
{
    public string Name { get; } = name;
}

/// <summary>
/// Two operands and an operator. There is no precedence: the parser folds every
/// binary operator left to right, so the position is the left operand's.
/// </summary>
public sealed class BinaryExpression(
    SourcePosition position, BinaryOperator op, Expression left, Expression right) : Expression(position)
{
    public BinaryOperator Operator { get; } = op;
    public Expression Left { get; } = left;
    public Expression Right { get; } = right;
}

public sealed class UnaryExpression(SourcePosition position, UnaryOperator op, Expression operand)
    : Expression(position)
{
    public UnaryOperator Operator { get; } = op;
    public Expression Operand { get; } = operand;
}
