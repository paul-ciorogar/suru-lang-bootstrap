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
    // TODO: refactor this to create an instance of AstPrinter
    // var printer = new AstPrinter(module);
    // return printer.ToString() or printer.ToStringWithTypes();
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
                // Only a kind other than Plain is rendered: the whole parser test layer then
                // asserts on the kind without a hand-written assertion, and the detail appears
                // exactly on the blocks it distinguishes rather than on every block there is.
                AppendNode(output, depth, "BlockStatement", block.Position,
                    block.Kind == ScopeKind.Plain ? "" : block.Kind.ToString().ToLowerInvariant());
                foreach (var inner in block.Statements)
                    AppendStatement(output, inner, depth + 1, withTypes);
                break;
            // Condition, then-arm and else-arm as unlabelled children in source order. An
            // 'else if' shows up as a nested IfStatement one level in, which is what tells it
            // apart from an 'if' inside the then-arm — that one sits under a BlockStatement.
            case IfStatement branch:
                AppendNode(output, depth, "IfStatement", branch.Position);
                AppendExpression(output, branch.Condition, depth + 1, withTypes);
                AppendStatement(output, branch.Then, depth + 1, withTypes);
                if (branch.Else is not null)
                    AppendStatement(output, branch.Else, depth + 1, withTypes);
                break;
            // Condition then body, the same unlabelled-children shape as an 'if'.
            case WhileStatement loop:
                AppendNode(output, depth, "WhileStatement", loop.Position);
                AppendExpression(output, loop.Condition, depth + 1, withTypes);
                AppendStatement(output, loop.Body, depth + 1, withTypes);
                break;
            // Leaves: neither carries an operand, and which loop it leaves is nesting, which
            // the tree already shows.
            case BreakStatement:
                AppendNode(output, depth, "BreakStatement", statement.Position);
                break;
            case ContinueStatement:
                AppendNode(output, depth, "ContinueStatement", statement.Position);
                break;
            // Parameters as children ahead of the body, which renders through the block case
            // above and so carries its 'function' kind. The declaration and each parameter take
            // a resolved type the way an expression does, so both dumps still diff line for line.
            case FunctionDeclaration function:
                AppendNode(output, depth, "FunctionDeclaration", function.Position,
                    $"{function.Name} {function.ReturnTypeName}", TypeSuffix(function.ReturnType, withTypes));
                foreach (var parameter in function.Parameters)
                    AppendNode(output, depth + 1, "Parameter", parameter.Position,
                        $"{parameter.Name} {parameter.TypeName}", TypeSuffix(parameter.Type, withTypes));
                AppendStatement(output, function.Body, depth + 1, withTypes);
                break;
            // A bare 'return' is a leaf; one with a value has it as its only child.
            case ReturnStatement ret:
                AppendNode(output, depth, "ReturnStatement", ret.Position);
                if (ret.Value is not null)
                    AppendExpression(output, ret.Value, depth + 1, withTypes);
                break;
            case MockDirective mock:
                AppendNode(output, depth, "MockDirective", mock.Position, mock.Name);
                AppendExpression(output, mock.Value, depth + 1, withTypes);
                break;
            case ViewDirective view:
                AppendNode(output, depth, "ViewDirective", view.Position, $"#{view.Id}");
                AppendExpression(output, view.Subject, depth + 1, withTypes);
                break;
            case AssertDirective assert:
                AppendNode(output, depth, "AssertDirective", assert.Position, $"#{assert.Id}");
                AppendExpression(output, assert.Actual, depth + 1, withTypes);
                AppendExpression(output, assert.Expected, depth + 1, withTypes);
                break;
            default:
                AppendNode(output, depth, statement.GetType().Name, statement.Position);
                break;
        }
    }

    private static void AppendExpression(StringBuilder output, Expression expression, int depth, bool withTypes)
    {
        var type = TypeSuffix(expression.Type, withTypes);

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

    private static string TypeSuffix(SuruType? type, bool withTypes) =>
        withTypes ? $" : {type?.ToString() ?? "?"}" : "";

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Round-trippable, so a float in the dump is the exact value codegen will emit.</summary>
    private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
