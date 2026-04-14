namespace Suru.Compiler.Parse.Ast;

public enum UnaryOp { Not }

public sealed class UnaryExpression(UnaryOp op, Expression operand) : Expression
{
    public UnaryOp Op { get; } = op;
    public Expression Operand { get; } = operand;
}
