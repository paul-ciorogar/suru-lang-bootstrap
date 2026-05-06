using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Match emission ───────────────────────────────────────────────────────

    private void EmitMatchAsStatement(MatchExpression match)
    {
        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            EmitMatchArmBodyAsStatement(patternArms[i].Body);
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            EmitMatchArmBodyAsStatement(wildcardArm.Body);
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
    }

    private void EmitMatchArmBodyAsStatement(Expression body)
    {
        switch (body)
        {
            // Nested match used as a statement arm — emit as statement so no result alloca is created.
            // EmitValue would call EmitMatchAsExpression, producing a typed alloca that may mismatch
            // when arm bodies have incompatible types (e.g. Struct vs Bool from array.add).
            case MatchExpression nestedMatch:
                EmitMatchAsStatement(nestedMatch);
                break;
            case CallExpression { Name: "printLn", Args: [var arg] }:
                var (v, vt) = EmitValue(arg);
                _runtimeDecls.AddSuruPrintln();
                var printP = IsScalar(vt) ? BoxValue(v, vt) : v;
                _funcs.AppendLine($"  call void @suru_println(ptr {printP})");
                break;
            default:
                EmitValue(body);
                break;
        }
    }

    // Match-as-expression: alloca uses the raw LLVM type of the result (scalar or ptr).
    // Critical: result alloca is emitted BEFORE EmitMatchTestChain so it dominates all arm blocks.
    private (string val, SuruType type) EmitMatchAsExpression(MatchExpression match)
    {
        var firstArm    = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        var resultType  = PeekType(firstArm.Body);
        var resultLlvmT = LlvmType(resultType);
        var resultPtr   = $"%match_result_{_matchCounter}";
        _funcs.AppendLine($"  {resultPtr} = alloca {resultLlvmT}");

        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            var (armVal, _) = EmitValue(patternArms[i].Body);
            _funcs.AppendLine($"  store {resultLlvmT} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            var (armVal, _) = EmitValue(wildcardArm.Body);
            _funcs.AppendLine($"  store {resultLlvmT} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
        var loadTmp = NextTmp();
        _funcs.AppendLine($"  {loadTmp} = load {resultLlvmT}, ptr {resultPtr}");
        return (loadTmp, resultType);
    }

    private SuruType PeekType(Expression expr) => expr switch
    {
        BoolLiteral                   => SuruType.Bool,
        IntLiteral                    => SuruType.Int64,
        FloatLiteral                  => SuruType.Float64,
        StringLiteralExpression       => SuruType.String,
        ArrayLiteralExpression        => SuruType.Array,
        StructLiteralExpression       => SuruType.Struct,
        FieldAccessExpression fa      => fa.ResolvedType ?? SuruType.Struct,
        CallExpression { Name: "clone", Args: [var carg] } => PeekType(carg),
        UnaryExpression               => SuruType.Bool,
        BinaryExpression              => SuruType.Bool,
        VariableReferenceExpression v =>
            _vars.TryGetValue(v.Name, out var ve) ? ve.type : _globalVars[v.Name].Type,
        MatchExpression match         => PeekMatchType(match),
        MethodCallExpression m        => PeekMethodType(m),
        CallExpression { Name: "readFile" } => SuruType.String,
        CallExpression c when _userFunctions.ContainsKey(c.Name)
                                      => _userFunctions[c.Name].ReturnType,
        _ => throw new NotSupportedException($"IR codegen: cannot peek type of {expr.GetType().Name}"),
    };

    private SuruType PeekMatchType(MatchExpression match)
    {
        var first = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        return PeekType(first.Body);
    }

    private SuruType PeekMethodType(MethodCallExpression m)
    {
        if (m.Receiver is VariableReferenceExpression { Name: var nsName2 } &&
            _module.Namespaces.Contains(nsName2))
            return _userFunctions[$"{nsName2}.{m.MethodName}"].ReturnType;

        if (m.Receiver is VariableReferenceExpression { Name: var typeName }
            && typeName is "Int32" or "Int64" or "Float64" or "Bool" or "String")
            return SuruTypeFromAnnotation(new TypeAnnotation(typeName));

        // Array methods: no element type tracking; at() returns Struct (generic ptr).
        if (m.Receiver is VariableReferenceExpression rv2 &&
            _vars.TryGetValue(rv2.Name, out var rv2Entry) && rv2Entry.type == SuruType.Array)
        {
            return m.MethodName switch
            {
                "len"   => SuruType.Int64,
                "at"    => SuruType.Struct,   // element type unknown at compile time
                "set"   => SuruType.Bool,
                "add"   => SuruType.Bool,
                "slice" => SuruType.Array,
                _ => throw new NotSupportedException($"IR codegen: cannot peek type for Array.{m.MethodName}"),
            };
        }

        return m.MethodName switch
        {
            "add" or "take" or "multiply" or "split" or "invert" => PeekType(m.Receiver),
            "lt" or "gt" or "lte" or "gte" or "equals"           => SuruType.Bool,
            "compare" or "ord" or "len"                           => SuruType.Int64,
            "toString" or "at" or "append" or "slice"             => SuruType.String,
            _ => throw new NotSupportedException($"IR codegen: cannot peek type for method '{m.MethodName}'"),
        };
    }

    // Match test chain: scalars arrive as raw values from EmitValue — no UnboxScalar needed
    // for known scalar types. Struct-typed conditions (dynamic) still need unboxing.
    private (List<MatchArm> PatternArms, MatchArm? WildcardArm, int N) EmitMatchTestChain(
        MatchExpression match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        int n = _matchCounter++;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);
        var missLabel   = wildcardArm != null ? $"match_wildcard_{n}" : $"match_merge_{n}";

        // Known scalars are already raw; Struct is a box ptr that must be unboxed.
        string rawCond;
        if (condType == SuruType.String)
            rawCond = condVal;
        else if (IsScalar(condType))
            rawCond = condVal;   // already raw i64/i1/double
        else
            rawCond = UnboxInt64(condVal);   // Struct → unbox as i64

        for (int i = 0; i < patternArms.Count; i++)
        {
            var cmpTmp = NextTmp();

            if (condType == SuruType.String)
            {
                _externals.AddStrcmp();
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                var condData    = EmitExtractStringData(rawCond);
                var patternData = EmitExtractStringData(patternVal);
                var strcmpTmp   = NextTmp();
                _funcs.AppendLine($"  {strcmpTmp} = call i32 @strcmp(ptr {condData}, ptr {patternData})");
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i32 {strcmpTmp}, 0");
            }
            else if (condType == SuruType.Float64)
            {
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                // FloatLiteral and Float64 var refs are already raw double.
                _funcs.AppendLine($"  {cmpTmp} = fcmp oeq double {rawCond}, {patternVal}");
            }
            else if (condType == SuruType.Struct)
            {
                // Struct-typed condition treated as i64; patterns are Int64 literals (raw i64).
                var (patternVal, patType) = EmitValue(patternArms[i].Pattern!);
                var rawPat = IsScalar(patType) ? patternVal : UnboxInt64(patternVal);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i64 {rawCond}, {rawPat}");
            }
            else
            {
                // Bool/Int32/Int64: both sides are already raw.
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq {LlvmType(condType)} {rawCond}, {patternVal}");
            }

            var nextLabel = (i + 1 < patternArms.Count)
                ? $"match_test_{n}_{i + 1}"
                : missLabel;

            _funcs.AppendLine($"  br i1 {cmpTmp}, label %match_arm_{n}_{i}, label %{nextLabel}");

            if (i + 1 < patternArms.Count)
                _funcs.AppendLine($"match_test_{n}_{i + 1}:");
        }

        if (patternArms.Count == 0)
            _funcs.AppendLine($"  br label %{missLabel}");

        return (patternArms, wildcardArm, n);
    }
}
