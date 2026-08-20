namespace Suru.Compiler.Parse.Ast;

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    And,
    Or,
}

public enum UnaryOperator
{
    Negate,
    Not,
}

/// <summary>
/// The source spelling of each operator. Diagnostics and the AST dump both go
/// through here, so an operator is named the same way everywhere it appears.
/// </summary>
public static class Operators
{
    public static string Text(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Remainder => "%",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        BinaryOperator.GreaterOrEqual => ">=",
        BinaryOperator.And => "and",
        BinaryOperator.Or => "or",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static string Text(UnaryOperator op) => op switch
    {
        UnaryOperator.Negate => "-",
        UnaryOperator.Not => "not",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}
