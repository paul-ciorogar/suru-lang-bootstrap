namespace Suru.Compiler.Parse.Ast;

public enum BinaryOp { And, Or }

public sealed class BinaryExpression(Expression left, BinaryOp op, Expression right) : Expression
{
    public Expression Left { get; } = left;
    public BinaryOp Op { get; } = op;
    public Expression Right { get; } = right;
}
