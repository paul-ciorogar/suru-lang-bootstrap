using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Function emission ───────────────────────────────────────────────────

    private void EmitFunction(FunctionDeclaration fn)
    {
        if (_module.ExternalFunctions.TryGetValue(fn.Name, out var originalName))
        {
            // External functions (from include): all params and return are ptr.
            var retLlvmType = fn.ReturnType.Name == "void" ? "void" : "ptr";
            var paramTypes  = string.Join(", ", fn.Parameters.Select(_ => "ptr"));
            _funcs.AppendLine($"declare {retLlvmType} @{originalName}({paramTypes})");
            return;
        }

        _blockOpen = true;
        _vars      = new();
        _argvVars  = new();

        if (fn.Name == "main")
        {
            _currentFnReturnLlvmType = "i64";
            _currentFnReturnSuruType = SuruType.Int64;
            _funcs.AppendLine("define internal i64 @suru_main(ptr %args) {");
            _funcs.AppendLine("entry:");
            _funcs.AppendLine("  %args.addr = alloca ptr");
            _funcs.AppendLine("  store ptr %args, ptr %args.addr");
            _vars["args"] = ("%args.addr", SuruType.Array);
            _argvVars.Add("args");
        }
        else
        {
            var retSuruType = FnReturnSuruType(fn);
            var retLlvmType = fn.ReturnType.Name == "void" ? "void" : "ptr";
            _currentFnReturnLlvmType = retLlvmType;
            _currentFnReturnSuruType = retSuruType;

            // All parameters are `ptr` in the universal tagged-pointer system.
            var paramStr = string.Join(", ", fn.Parameters.Select(p => $"ptr %{p.Name}"));
            _funcs.AppendLine($"define ptr @{fn.Name}({paramStr}) {{");
            _funcs.AppendLine("entry:");

            foreach (var p in fn.Parameters)
            {
                var pType    = SuruTypeFromAnnotation(p.TypeAnnotation);
                var allocPtr = $"%{p.Name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca ptr");
                _funcs.AppendLine($"  store ptr %{p.Name}, ptr {allocPtr}");
                _vars[p.Name] = (allocPtr, pType);
            }
        }

        foreach (var stmt in fn.Body)
            EmitStmt(stmt);

        if (_blockOpen)
        {
            if (fn.Name == "main")
                _funcs.AppendLine("  ret i64 0");
            else
                _funcs.AppendLine("  ret ptr null");
        }

        _funcs.AppendLine("}");
        _funcs.AppendLine();
    }

    // ─── Statement emission ──────────────────────────────────────────────────

    private void EmitStmt(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement { Expression: MatchExpression matchStmt }:
                EmitMatchAsStatement(matchStmt);
                break;

            // printLn(expr) — dispatch to suru_println which reads type_tag at offset 0.
            case ExpressionStatement { Expression: CallExpression { Name: "printLn", Args: [var arg] } }:
                var (pval, _) = EmitValue(arg);
                _runtimeDecls.AddSuruPrintln();
                _funcs.AppendLine($"  call void @suru_println(ptr {pval})");
                break;

            // printError(expr) — dispatch to suru_printerror (writes to stderr).
            case ExpressionStatement { Expression: CallExpression { Name: "printError", Args: [var errArg] } }:
                var (errVal, _) = EmitValue(errArg);
                _runtimeDecls.AddSuruPrintError();
                _funcs.AppendLine($"  call void @suru_printerror(ptr {errVal})");
                break;

            // exit(code) — unbox the Int64/Int32 Box ptr, trunc to i32, call @exit.
            case ExpressionStatement { Expression: CallExpression { Name: "exit", Args: [var codeExpr] } }:
                _externals.AddExit();
                var (codeVal, codeType) = EmitValue(codeExpr);
                string exitArg;
                if (codeType == SuruType.Int32)
                {
                    exitArg = UnboxInt32(codeVal);
                }
                else
                {
                    var rawI64 = UnboxInt64(codeVal);
                    var code32 = NextTmp();
                    _funcs.AppendLine($"  {code32} = trunc i64 {rawI64} to i32");
                    exitArg = code32;
                }
                _funcs.AppendLine($"  call void @exit(i32 {exitArg})");
                _funcs.AppendLine("  unreachable");
                _funcs.AppendLine($"dead_{_tmp}:");
                _blockOpen = true;
                break;

            case ExpressionStatement { Expression: CallExpression { Name: "writeFile", Args: [var wfPath, var wfContent] } }:
                EmitWriteFile(wfPath, wfContent);
                break;

            // let name TypeAnnotation: expr — every alloca is `ptr`.
            case LetStatement { Name: var name, Value: var valExpr, TypeAnnotation: var ann }:
                if (valExpr is FieldAccessExpression { ResolvedType: null } faLet)
                    faLet.ResolvedType = SuruTypeFromAnnotation(ann);
                var (letVal, letType) = EmitValue(valExpr);
                // Int32 annotation coerces an Int64 Box to an Int32 Box.
                if (ann.Name == "Int32" && letType == SuruType.Int64)
                {
                    if (valExpr is IntLiteral intLit)
                    {
                        letVal  = BoxInt32(intLit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        letType = SuruType.Int32;
                    }
                    else
                    {
                        var rawI = UnboxInt64(letVal);
                        var i32t = NextTmp();
                        _funcs.AppendLine($"  {i32t} = trunc i64 {rawI} to i32");
                        letVal  = BoxInt32(i32t);
                        letType = SuruType.Int32;
                    }
                }
                var allocPtr = $"%{name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca ptr");
                _funcs.AppendLine($"  store ptr {letVal}, ptr {allocPtr}");
                _vars[name] = (allocPtr, letType);
                break;

            // return expr — use _currentFnReturnLlvmType so `ret` matches the definition.
            case ReturnStatement { Value: var retExpr }:
                if (retExpr is FieldAccessExpression { ResolvedType: null } faRet)
                    faRet.ResolvedType = _currentFnReturnSuruType;
                var retVal = retExpr is null ? "null" : EmitValue(retExpr).Item1;
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {retVal}");
                _blockOpen = false;
                break;

            // while cond { body } — unbox condition to i1 before branching.
            case WhileStatement { Condition: var whileCond, Body: var whileBody }:
                var wn = _whileCounter++;
                _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_cond_{wn}:");
                var (condVal2, _) = EmitValue(whileCond);
                var condI1 = UnboxBool(condVal2);
                _funcs.AppendLine($"  br i1 {condI1}, label %while_body_{wn}, label %while_after_{wn}");
                _funcs.AppendLine($"while_body_{wn}:");
                foreach (var bodyStmt in whileBody)
                    EmitStmt(bodyStmt);
                if (_blockOpen)
                    _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_after_{wn}:");
                _blockOpen = true;
                break;

            // name: expr — store new value (always ptr) into existing alloca.
            case AssignmentStatement { Name: var assignName, Value: var assignExpr }:
                var (assignVal, _) = EmitValue(assignExpr);
                var (assignPtrAddr, _) = _vars[assignName];
                _funcs.AppendLine($"  store ptr {assignVal}, ptr {assignPtrAddr}");
                break;

            case FieldAssignmentStatement fieldAssign:
                EmitFieldAssignment(fieldAssign);
                break;

            case ExpressionStatement { Expression: var sideEffectExpr }:
                EmitValue(sideEffectExpr);
                break;

            default:
                throw new NotSupportedException($"IR codegen: unsupported statement {stmt.GetType().Name}");
        }
    }

    // ─── @main wrapper ───────────────────────────────────────────────────────

    // Emits a C-ABI `int main(int argc, char** argv)` that builds a %suru.String
    // wrapping argv (type_tag=6 since it uses the String header layout), then calls
    // suru_main and returns its exit code.
    private void EmitMainWrapper(StringBuilder sb)
    {
        sb.AppendLine("define i32 @main(i32 %argc, ptr %argv) {");
        sb.AppendLine("entry:");
        sb.AppendLine("  %seq      = call ptr @malloc(i64 24)");
        sb.AppendLine("  %tag_gep  = getelementptr %suru.String, ptr %seq, i32 0, i32 0");
        sb.AppendLine("  store i64 6, ptr %tag_gep");
        sb.AppendLine("  %len_gep  = getelementptr %suru.String, ptr %seq, i32 0, i32 1");
        sb.AppendLine("  %argc64   = sext i32 %argc to i64");
        sb.AppendLine("  store i64 %argc64, ptr %len_gep");
        sb.AppendLine("  %data_gep = getelementptr %suru.String, ptr %seq, i32 0, i32 2");
        sb.AppendLine("  store ptr %argv, ptr %data_gep");
        sb.AppendLine("  %suru_ret = call i64 @suru_main(ptr %seq)");
        sb.AppendLine("  %ret32    = trunc i64 %suru_ret to i32");
        sb.AppendLine("  ret i32 %ret32");
        sb.AppendLine("}");
    }
}
