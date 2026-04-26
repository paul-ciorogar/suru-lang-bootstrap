using Suru.Compiler.Types;

namespace Suru.Compiler.Parse.Ast;

public sealed class FieldAccessExpression(Expression receiver, string fieldName) : Expression
{
    public Expression Receiver { get; } = receiver;
    public string FieldName { get; } = fieldName;
    // Set by SemanticAnalyzer; used by IRCodeGenerator for EmitFromI64 cast.
    public SuruType? ResolvedType { get; set; }
}
