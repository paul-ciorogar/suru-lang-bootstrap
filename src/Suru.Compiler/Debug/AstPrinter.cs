using System.Globalization;
using System.Text;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Debug;

/// <summary>
/// Renders a <see cref="Module"/> as an indented tree.
/// <para>
/// Semantic analysis annotates the AST in place, so the same printer serves both
/// the after-parse and after-analysis dumps: with <c>withTypes</c> every
/// expression carries its resolved type (<c>?</c> when it has none), and the two
/// dumps diff line for line.
/// </para>
/// </summary>
public static class AstPrinter
{
    public static string Print(Module module, bool withTypes = false)
    {
        var output = new StringBuilder();
        output.Append("Module ").Append(module.SourcePath).Append('\n');

        foreach (var statement in module.Statements)
            AppendStatement(output, statement, depth: 1, withTypes);

        return output.ToString();
    }

    private static void AppendStatement(StringBuilder output, Statement statement, int depth, bool withTypes)
    {
        switch (statement)
        {
            case ExpressionStatement expressionStatement:
                AppendNode(output, depth, "ExpressionStatement", statement.Position);
                AppendExpression(output, expressionStatement.Expression, depth + 1, withTypes);
                break;
            case LetStatement let:
                AppendNode(output, depth, "LetStatement", let.Position, $"{let.Name} {let.TypeName}");
                AppendExpression(output, let.Value, depth + 1, withTypes);
                break;
            case AssignmentStatement assignment:
                AppendNode(output, depth, "AssignmentStatement", assignment.Position, assignment.Name);
                AppendExpression(output, assignment.Value, depth + 1, withTypes);
                break;
            case BlockStatement block:
                AppendNode(output, depth, "BlockStatement", block.Position);
                foreach (var inner in block.Statements)
                    AppendStatement(output, inner, depth + 1, withTypes);
                break;
            default:
                AppendNode(output, depth, statement.GetType().Name, statement.Position);
                break;
        }
    }

    private static void AppendExpression(StringBuilder output, Expression expression, int depth, bool withTypes)
    {
        var type = withTypes ? $" : {expression.Type?.ToString() ?? "?"}" : "";

        switch (expression)
        {
            case BoolLiteral literal:
                AppendNode(output, depth, "BoolLiteral", literal.Position, literal.Value ? "true" : "false", type);
                break;
            case IntLiteral literal:
                AppendNode(output, depth, "IntLiteral", literal.Position, Text(literal.Value), type);
                break;
            case FloatLiteral literal:
                AppendNode(output, depth, "FloatLiteral", literal.Position, Text(literal.Value), type);
                break;
            case CallExpression call:
                AppendNode(output, depth, "CallExpression", call.Position, call.Name, type);
                foreach (var argument in call.Args)
                    AppendExpression(output, argument, depth + 1, withTypes);
                break;
            case IdentifierExpression identifier:
                AppendNode(output, depth, "IdentifierExpression", identifier.Position, identifier.Name, type);
                break;
            case BinaryExpression binary:
                AppendNode(output, depth, "BinaryExpression", binary.Position, Operators.Text(binary.Operator), type);
                AppendExpression(output, binary.Left, depth + 1, withTypes);
                AppendExpression(output, binary.Right, depth + 1, withTypes);
                break;
            case UnaryExpression unary:
                AppendNode(output, depth, "UnaryExpression", unary.Position, Operators.Text(unary.Operator), type);
                AppendExpression(output, unary.Operand, depth + 1, withTypes);
                break;
            default:
                AppendNode(output, depth, expression.GetType().Name, expression.Position, type: type);
                break;
        }
    }

    private static void AppendNode(
        StringBuilder output, int depth, string node, SourcePosition position, string detail = "", string type = "")
    {
        output.Append(' ', depth * 2).Append(node).Append(' ').Append(position);
        if (detail.Length > 0)
            output.Append(' ').Append(detail);
        output.Append(type).Append('\n');
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Round-trippable, so a float in the dump is the exact value codegen will emit.</summary>
    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
