using System.Text;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Parse;

/// <summary>
/// Renders a <see cref="Module"/> as an indented text tree for human inspection.
/// Every node kind is written on its own line; child nodes are indented by two spaces
/// relative to their parent. Leaf values (literals, names) appear in square brackets.
///
/// Example output for <c>fn add(a Int64, b Int64) Int64 { return a.add(b) }</c>:
/// <code>
///   FunctionDeclaration [add](a : Int64, b : Int64) -> Int64
///     ReturnStatement
///       MethodCall [add]
///         VariableRef [a]
///         VariableRef [b]
/// </code>
/// </summary>
public static class AstPrinter
{
    /// <summary>
    /// Produces the full indented tree for <paramref name="module"/> as a string.
    /// The root line identifies the source file; top-level statements follow.
    /// </summary>
    public static string Print(Module module)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Module [{module.SourcePath}]");
        foreach (var stmt in module.Statements)
            PrintStatement(sb, "  ", stmt);
        return sb.ToString();
    }

    // ─── Statements ──────────────────────────────────────────────────────────

    private static void PrintStatement(StringBuilder sb, string indent, Statement stmt)
    {
        switch (stmt)
        {
            case FunctionDeclaration fn:
            {
                var ps = string.Join(", ", fn.Parameters.Select(p => $"{p.Name} : {p.TypeAnnotation}"));
                sb.AppendLine($"{indent}FunctionDeclaration [{fn.Name}]({ps}) -> {fn.ReturnType}");
                foreach (var s in fn.Body)
                    PrintStatement(sb, indent + "  ", s);
                break;
            }

            case LetStatement let:
                sb.AppendLine($"{indent}LetStatement [{let.Name}] : {let.TypeAnnotation}");
                PrintExpression(sb, indent + "  ", let.Value);
                break;

            case AssignmentStatement assign:
                sb.AppendLine($"{indent}AssignmentStatement [{assign.Name}]");
                PrintExpression(sb, indent + "  ", assign.Value);
                break;

            case FieldAssignmentStatement fa:
                sb.AppendLine($"{indent}FieldAssignment [.{fa.FieldName}]");
                PrintExpression(sb, indent + "  ", fa.Receiver);
                PrintExpression(sb, indent + "  ", fa.Value);
                break;

            case ReturnStatement ret:
                sb.AppendLine($"{indent}ReturnStatement");
                if (ret.Value is not null)
                    PrintExpression(sb, indent + "  ", ret.Value);
                break;

            case WhileStatement wh:
                sb.AppendLine($"{indent}WhileStatement");
                PrintExpression(sb, indent + "  ", wh.Condition);
                foreach (var s in wh.Body)
                    PrintStatement(sb, indent + "  ", s);
                break;

            case IncludeDirective inc:
                sb.AppendLine($"{indent}IncludeDirective [\"{inc.Path}\"] as [{inc.NamespaceName}]");
                break;

            case TypeDeclaration td:
                sb.AppendLine($"{indent}TypeDeclaration [{td.Name}]");
                foreach (var (field, type) in td.Fields)
                    sb.AppendLine($"{indent}  Field [{field}] Type [{type}]");
                break;

            case ExpressionStatement es:
                sb.AppendLine($"{indent}ExpressionStatement");
                PrintExpression(sb, indent + "  ", es.Expression);
                break;

            default:
                sb.AppendLine($"{indent}{stmt.GetType().Name}");
                break;
        }
    }

    // ─── Expressions ─────────────────────────────────────────────────────────

    private static void PrintExpression(StringBuilder sb, string indent, Expression expr)
    {
        switch (expr)
        {
            case BoolLiteral b:
                sb.AppendLine($"{indent}BoolLiteral [{b.Value.ToString().ToLower()}]");
                break;

            case IntLiteral i:
                sb.AppendLine($"{indent}IntLiteral [{i.Value}]");
                break;

            case FloatLiteral f:
                sb.AppendLine($"{indent}FloatLiteral [{f.Value}]");
                break;

            case StringLiteralExpression s:
                // Escape newlines/tabs so the tree stays single-line per node.
                var escaped = s.Value.Replace("\n", "\\n").Replace("\t", "\\t");
                sb.AppendLine($"{indent}StringLiteral [\"{escaped}\"]");
                break;

            case VariableReferenceExpression v:
                sb.AppendLine($"{indent}VariableRef [{v.Name}]");
                break;

            case CallExpression call:
                sb.AppendLine($"{indent}CallExpression [{call.Name}]");
                foreach (var arg in call.Args)
                    PrintExpression(sb, indent + "  ", arg);
                break;

            case MethodCallExpression mc:
                sb.AppendLine($"{indent}MethodCall [.{mc.MethodName}]");
                PrintExpression(sb, indent + "  ", mc.Receiver);
                foreach (var arg in mc.Args)
                    PrintExpression(sb, indent + "  ", arg);
                break;

            case FieldAccessExpression fa:
                sb.AppendLine($"{indent}FieldAccess [.{fa.FieldName}]");
                PrintExpression(sb, indent + "  ", fa.Receiver);
                break;

            case ArrayLiteralExpression arr:
                sb.AppendLine($"{indent}ArrayLiteral [{arr.Elements.Count} elements]");
                foreach (var el in arr.Elements)
                    PrintExpression(sb, indent + "  ", el);
                break;

            case StructLiteralExpression st:
                sb.AppendLine($"{indent}StructLiteral [{st.Fields.Count} fields]");
                foreach (var (name, typeAnn, val) in st.Fields)
                {
                    sb.AppendLine($"{indent}  Field [{name} {typeAnn}]");
                    PrintExpression(sb, indent + "    ", val);
                }
                break;

            case MatchExpression mx:
                sb.AppendLine($"{indent}MatchExpression [{mx.Arms.Count} arms]");
                PrintExpression(sb, indent + "  ", mx.Condition);
                foreach (var arm in mx.Arms)
                {
                    var patLabel = arm.Pattern is null ? "_" : ArmPatternLabel(arm.Pattern);
                    sb.AppendLine($"{indent}  Arm [{patLabel}]");
                    PrintExpression(sb, indent + "    ", arm.Body);
                }
                break;

            case UnaryExpression un:
                sb.AppendLine($"{indent}UnaryExpression [{un.Op}]");
                PrintExpression(sb, indent + "  ", un.Operand);
                break;

            case BinaryExpression bin:
                sb.AppendLine($"{indent}BinaryExpression [{bin.Op}]");
                PrintExpression(sb, indent + "  ", bin.Left);
                PrintExpression(sb, indent + "  ", bin.Right);
                break;

            default:
                sb.AppendLine($"{indent}{expr.GetType().Name}");
                break;
        }
    }

    // Returns a compact label for a match-arm pattern expression (used only in headings,
    // not recursed into, so the tree doesn't grow a full sub-tree for each pattern).
    private static string ArmPatternLabel(Expression pattern) => pattern switch
    {
        BoolLiteral b   => b.Value.ToString().ToLower(),
        IntLiteral i    => i.Value.ToString(),
        FloatLiteral f  => f.Value.ToString(),
        StringLiteralExpression s => $"\"{s.Value}\"",
        _               => pattern.GetType().Name,
    };
}
