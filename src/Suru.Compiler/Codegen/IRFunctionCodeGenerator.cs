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
            var retSuruType = FnReturnSuruType(fn);
            var retLlvmType = fn.ReturnType.Name == "void" ? "void" : LlvmType(retSuruType);
            var paramTypes  = string.Join(", ", fn.Parameters.Select(p =>
                LlvmType(SuruTypeFromAnnotation(p.TypeAnnotation))));
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
            _vars["args"] = ("%args.addr", new SuruType.ArrayType(SuruType.String));
            _argvVars.Add("args");
        }
        else
        {
            var retSuruType = FnReturnSuruType(fn);
            var retLlvmType = retSuruType is SuruType.VoidType ? "ptr" : LlvmType(retSuruType);
            _currentFnReturnLlvmType = retLlvmType;
            _currentFnReturnSuruType = retSuruType;
            _currentFnReturnTypeName = fn.ReturnType.Name;

            var paramStr = string.Join(", ", fn.Parameters.Select(p =>
            {
                var pType = SuruTypeFromAnnotation(p.TypeAnnotation);
                return $"{LlvmType(pType)} %{p.Name}";
            }));
            _funcs.AppendLine($"define {retLlvmType} @{fn.Name}({paramStr}) {{");
            _funcs.AppendLine("entry:");

            foreach (var p in fn.Parameters)
            {
                var pType    = SuruTypeFromAnnotation(p.TypeAnnotation);
                var llvmT    = LlvmType(pType);
                var allocPtr = $"%{p.Name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca {llvmT}");
                _funcs.AppendLine($"  store {llvmT} %{p.Name}, ptr {allocPtr}");
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
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {DefaultReturnValue(_currentFnReturnSuruType)}");
        }

        _funcs.AppendLine("}");
        _funcs.AppendLine();
        _currentFnReturnTypeName = null;
    }

    // ─── Statement emission ──────────────────────────────────────────────────

    private void EmitStmt(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement { Expression: MatchExpression matchStmt }:
                EmitMatchAsStatement(matchStmt);
                break;

            case ExpressionStatement { Expression: CallExpression { Name: "printLn", Args: [var arg] } }:
                var (pval, ptype) = EmitValue(arg);
                _runtimeDecls.AddSuruPrintln();
                var printPtr = IsScalar(ptype) ? BoxValue(pval, ptype) : pval;
                _funcs.AppendLine($"  call void @suru_println(ptr {printPtr})");
                break;

            case ExpressionStatement { Expression: CallExpression { Name: "printError", Args: [var errArg] } }:
                var (errVal, errType) = EmitValue(errArg);
                _runtimeDecls.AddSuruPrintError();
                var errPtr = IsScalar(errType) ? BoxValue(errVal, errType) : errVal;
                _funcs.AppendLine($"  call void @suru_printerror(ptr {errPtr})");
                break;

            case ExpressionStatement { Expression: CallExpression { Name: "exit", Args: [var codeExpr] } }:
                _externals.AddExit();
                var (codeVal, codeType) = EmitValue(codeExpr);
                string exitArg;
                if (codeType is SuruType.Int32Type)
                {
                    exitArg = codeVal;
                }
                else if (IsScalar(codeType))
                {
                    var code32 = NextTmp();
                    _funcs.AppendLine($"  {code32} = trunc i64 {codeVal} to i32");
                    exitArg = code32;
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

            // let name NamedType: { fields } — thread type name into EmitStructLiteral.
            case LetStatement { Name: var name, Value: StructLiteralExpression sl, TypeAnnotation: var slAnn }:
                var (slVal, slType) = EmitStructLiteral(sl, slAnn.Name);
                var slPtr = $"%{name}.addr";
                _funcs.AppendLine($"  {slPtr} = alloca ptr");
                _funcs.AppendLine($"  store ptr {slVal}, ptr {slPtr}");
                _vars[name] = (slPtr, slType);
                break;

            // let name TypeAnnotation: expr — scalars use raw alloca types.
            case LetStatement { Name: var name, Value: var valExpr, TypeAnnotation: var ann }:
                var annType = SuruTypeFromAnnotation(ann);
                if (valExpr is FieldAccessExpression { ResolvedType: null } faLet)
                    faLet.ResolvedType = annType;
                var (letVal, letType) = EmitValue(valExpr);
                // Annotation-guided coercion: e.g. `let x Int64: arr.at(0)` returns (ptr, NamedType).
                if (IsScalar(annType) && !IsScalar(letType))
                {
                    letVal  = UnboxScalar(letVal, annType);
                    letType = annType;
                }
                // Int32 annotation coerces raw i64 to raw i32.
                if (ann.Name == "Int32" && letType is SuruType.Int64Type)
                {
                    if (valExpr is IntLiteral intLit)
                    {
                        letVal  = intLit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        letType = SuruType.Int32;
                    }
                    else
                    {
                        var i32t = NextTmp();
                        _funcs.AppendLine($"  {i32t} = trunc i64 {letVal} to i32");
                        letVal  = i32t;
                        letType = SuruType.Int32;
                    }
                }
                var llvmT    = LlvmType(letType);
                var allocPtr = $"%{name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca {llvmT}");
                _funcs.AppendLine($"  store {llvmT} {letVal}, ptr {allocPtr}");
                _vars[name] = (allocPtr, annType);
                break;

            // return { fields } — thread the declared return type name into EmitStructLiteral.
            case ReturnStatement { Value: StructLiteralExpression retSl }:
                var (retSlVal, _) = EmitStructLiteral(retSl, _currentFnReturnTypeName);
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {retSlVal}");
                _blockOpen = false;
                break;

            // return expr — coerce dynamic ptr to raw scalar if function declares scalar return.
            case ReturnStatement { Value: var retExpr }:
                if (retExpr is FieldAccessExpression { ResolvedType: null } faRet)
                    faRet.ResolvedType = _currentFnReturnSuruType;
                string retVal;
                if (retExpr is null)
                {
                    retVal = DefaultReturnValue(_currentFnReturnSuruType);
                }
                else
                {
                    var (rv, rvType) = EmitValue(retExpr);
                    retVal = IsScalar(_currentFnReturnSuruType) && !IsScalar(rvType)
                        ? UnboxScalar(rv, _currentFnReturnSuruType)
                        : rv;
                }
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {retVal}");
                _blockOpen = false;
                break;

            case WhileStatement { Condition: var whileCond, Body: var whileBody }:
                var wn = _whileCounter++;
                _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_cond_{wn}:");
                var (condVal2, condType2) = EmitValue(whileCond);
                var condI1 = IsScalar(condType2) ? condVal2 : UnboxBool(condVal2);
                _funcs.AppendLine($"  br i1 {condI1}, label %while_body_{wn}, label %while_after_{wn}");
                _funcs.AppendLine($"while_body_{wn}:");
                foreach (var bodyStmt in whileBody)
                    EmitStmt(bodyStmt);
                if (_blockOpen)
                    _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_after_{wn}:");
                _blockOpen = true;
                break;

            case AssignmentStatement { Name: var assignName, Value: var assignExpr }:
                var (assignPtrAddr, assignVarType) = _vars[assignName];
                var (assignVal, assignExprType)    = EmitValue(assignExpr);
                var storeVal  = IsScalar(assignVarType) && !IsScalar(assignExprType)
                    ? UnboxScalar(assignVal, assignVarType)
                    : assignVal;
                var storeLlvmT = LlvmType(assignVarType);
                _funcs.AppendLine($"  store {storeLlvmT} {storeVal}, ptr {assignPtrAddr}");
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
