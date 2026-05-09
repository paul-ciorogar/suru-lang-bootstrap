using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Match emission ───────────────────────────────────────────────────────

    // Narrow the condition variable to a specific variant type for the duration of emit.
    // Returns the original type if narrowing was applied (caller must restore), null otherwise.
    private SuruType? NarrowCondVar(string? condVarName, string? variantName)
    {
        if (condVarName == null || variantName == null) return null;
        if (!_vars.TryGetValue(condVarName, out var entry)) return null;
        if (entry.type is not SuruType.SumType) return null;
        _vars[condVarName] = (entry.ptr, new SuruType.NamedType(variantName));
        return entry.type;
    }

    private void RestoreCondVar(string? condVarName, SuruType? savedType)
    {
        if (condVarName == null || savedType == null) return;
        if (!_vars.TryGetValue(condVarName, out var entry)) return;
        _vars[condVarName] = (entry.ptr, savedType);
    }

    private void EmitMatchAsStatement(MatchExpression match)
    {
        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);
        var condVarName = (match.Condition as VariableReferenceExpression)?.Name;

        for (int i = 0; i < patternArms.Count; i++)
        {
            var variantName = (patternArms[i].Pattern as VariableReferenceExpression)?.Name;
            var savedType   = NarrowCondVar(condVarName, variantName);
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            EmitMatchArmBodyAsStatement(patternArms[i].Body);
            RestoreCondVar(condVarName, savedType);
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
            case MatchExpression nestedMatch:
                EmitMatchAsStatement(nestedMatch);
                break;
            // `_: {}` in a MatchExpression arm — empty struct literal means no-op.
            case StructLiteralExpression { Fields: { Count: 0 } }:
                break;
            case CallExpression { Name: BuiltinNames.PrintLn, Args: [var arg] }:
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
        var condVarName = (match.Condition as VariableReferenceExpression)?.Name;

        for (int i = 0; i < patternArms.Count; i++)
        {
            var variantName = (patternArms[i].Pattern as VariableReferenceExpression)?.Name;
            var savedType   = NarrowCondVar(condVarName, variantName);
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            var (armVal, _) = EmitMatchArmValue(patternArms[i].Body);
            RestoreCondVar(condVarName, savedType);
            _funcs.AppendLine($"  store {resultLlvmT} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            var (armVal, _) = EmitMatchArmValue(wildcardArm.Body);
            _funcs.AppendLine($"  store {resultLlvmT} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
        var loadTmp = NextTmp();
        _funcs.AppendLine($"  {loadTmp} = load {resultLlvmT}, ptr {resultPtr}");
        return (loadTmp, resultType);
    }

    // Struct literal arm bodies (e.g. `true: { field: [] }`) need the enclosing
    // function's return type so EmitStructLiteral can look up the declaration and
    // propagate element types to any empty array fields.
    private (string val, SuruType type) EmitMatchArmValue(Expression body) =>
        body is StructLiteralExpression sl
            ? EmitStructLiteral(sl, _currentFnReturnTypeName)
            : EmitValue(body);

    private SuruType PeekType(Expression expr) => expr switch
    {
        BoolLiteral                   => SuruType.Bool,
        IntLiteral                    => SuruType.Int64,
        FloatLiteral                  => SuruType.Float64,
        StringLiteralExpression       => SuruType.String,
        ArrayLiteralExpression        => new SuruType.ArrayType(SuruType.Int64),   // best-effort
        StructLiteralExpression       => new SuruType.NamedType(""),
        FieldAccessExpression fa      => fa.ResolvedType ?? new SuruType.NamedType(""),
        CallExpression { Name: BuiltinNames.Clone, Args: [var carg] } => PeekType(carg),
        UnaryExpression               => SuruType.Bool,
        BinaryExpression              => SuruType.Bool,
        VariableReferenceExpression v =>
            _vars.TryGetValue(v.Name, out var ve) ? ve.type
            : _globalVars.TryGetValue(v.Name, out var gve) ? gve.Type
            : throw new InvalidOperationException($"IR codegen: undefined variable '{v.Name}' in match pattern"),
        MatchExpression match         => PeekMatchType(match),
        MethodCallExpression m        => PeekMethodType(m),
        CallExpression { Name: BuiltinNames.ReadFile } => SuruType.String,
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
            _module.Aliases.Resolve(nsName2) is { } nsPath2)
        {
            var fnDecl = _module.ExternalDeclarationRegistry.LookupFunction(nsPath2, m.MethodName)
                ?? throw new InvalidOperationException($"IR codegen: undefined namespace function '{nsName2}.{m.MethodName}'");
            return FnReturnSuruType(fnDecl);
        }

        if (m.Receiver is VariableReferenceExpression { Name: var typeName }
            && typeName is BuiltinNames.Int32 or BuiltinNames.Int64 or BuiltinNames.Float64 or BuiltinNames.Bool or BuiltinNames.String)
            return SuruTypeFromAnnotation(new TypeAnnotation(typeName));

        if (m.Receiver is VariableReferenceExpression rv2 &&
            _vars.TryGetValue(rv2.Name, out var rv2Entry) && rv2Entry.type is SuruType.ArrayType at2)
        {
            return m.MethodName switch
            {
                "len"   => SuruType.Int64,
                "at"    => at2.Element,
                "set"   => SuruType.Bool,
                "add"   => SuruType.Bool,
                "slice" => at2,
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

    // Match test chain for expression-context match (MatchExpression).
    private (List<MatchArm> PatternArms, MatchArm? WildcardArm, int N) EmitMatchTestChain(
        MatchExpression match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        int n = _matchCounter++;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);
        var missLabel   = wildcardArm != null ? $"match_wildcard_{n}" : $"match_merge_{n}";

        EmitPatternComparisons(condVal, condType, patternArms.Select(a => a.Pattern).ToList(), missLabel, n);

        return (patternArms, wildcardArm, n);
    }

    // Match test chain for statement-context match (MatchStatement).
    private (List<MatchStatementArm> PatternArms, MatchStatementArm? WildcardArm, int N)
        EmitMatchStatementTestChain(MatchStatement match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        int n = _matchCounter++;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);
        var missLabel   = wildcardArm != null ? $"match_wildcard_{n}" : $"match_merge_{n}";

        EmitPatternComparisons(condVal, condType, patternArms.Select(a => a.Pattern).ToList(), missLabel, n);

        return (patternArms, wildcardArm, n);
    }

    // Emits the icmp/fcmp/strcmp/variant-tag comparison chain for a list of pattern expressions.
    // Each comparison branches to match_arm_{n}_{i} on match or to the next test/missLabel on miss.
    // For sum-type / variant conditions, extracts the variant tag once before the loop and
    // compares it against each arm's variant index (never calls EmitValue on variant-name patterns).
    private void EmitPatternComparisons(
        string condVal, SuruType condType,
        IReadOnlyList<Expression?> patterns,
        string missLabel, int n)
    {
        // Detect variant/sum-type condition and extract tag.
        SumTypeDeclaration? sumParent = null;
        string rawCond;

        if (condType is SuruType.NamedType nt && IsVariant(nt.Name))
        {
            sumParent = FindParentSumType(nt.Name)!;
            _runtimeDecls.AddVariantTag();
            var tagTmp = NextTmp();
            _funcs.AppendLine($"  {tagTmp} = call i64 @suru_variant_tag(ptr {condVal})");
            rawCond = tagTmp;
        }
        else if (condType is SuruType.SumType sumt && _module.SumTypeDeclarations.TryGetValue(sumt.Name, out var sd))
        {
            sumParent = sd;
            _runtimeDecls.AddVariantTag();
            var tagTmp = NextTmp();
            _funcs.AppendLine($"  {tagTmp} = call i64 @suru_variant_tag(ptr {condVal})");
            rawCond = tagTmp;
        }
        else if (condType is SuruType.StringType)
            rawCond = condVal;
        else if (IsScalar(condType))
            rawCond = condVal;
        else
            rawCond = UnboxInt64(condVal);   // NamedType (non-variant) → unbox as i64

        for (int i = 0; i < patterns.Count; i++)
        {
            var cmpTmp = NextTmp();

            if (sumParent != null)
            {
                // Pattern is a variant name identifier — never call EmitValue; the name is
                // not a variable but a type constructor.  Compare extracted tag to the index.
                var variantName = ((VariableReferenceExpression)patterns[i]!).Name;
                var variantIdx  = GetVariantIndex(variantName, sumParent);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i64 {rawCond}, {variantIdx}");
            }
            else if (condType is SuruType.StringType)
            {
                _externals.AddStrcmp();
                var (patternVal, _) = EmitValue(patterns[i]!);
                var condData    = EmitExtractStringData(rawCond);
                var patternData = EmitExtractStringData(patternVal);
                var strcmpTmp   = NextTmp();
                _funcs.AppendLine($"  {strcmpTmp} = call i32 @strcmp(ptr {condData}, ptr {patternData})");
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i32 {strcmpTmp}, 0");
            }
            else if (condType is SuruType.Float64Type)
            {
                var (patternVal, _) = EmitValue(patterns[i]!);
                _funcs.AppendLine($"  {cmpTmp} = fcmp oeq double {rawCond}, {patternVal}");
            }
            else if (condType is SuruType.NamedType)
            {
                var (patternVal, patType) = EmitValue(patterns[i]!);
                var rawPat = IsScalar(patType) ? patternVal : UnboxInt64(patternVal);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i64 {rawCond}, {rawPat}");
            }
            else
            {
                var (patternVal, _) = EmitValue(patterns[i]!);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq {LlvmType(condType)} {rawCond}, {patternVal}");
            }

            var nextLabel = (i + 1 < patterns.Count)
                ? $"match_test_{n}_{i + 1}"
                : missLabel;

            _funcs.AppendLine($"  br i1 {cmpTmp}, label %match_arm_{n}_{i}, label %{nextLabel}");

            if (i + 1 < patterns.Count)
                _funcs.AppendLine($"match_test_{n}_{i + 1}:");
        }

        if (patterns.Count == 0)
            _funcs.AppendLine($"  br label %{missLabel}");
    }

    // Emits a MatchStatement: arms carry block bodies (IReadOnlyList<Statement>).
    // _blockOpen is checked before each merge branch so arms ending with return/exit
    // do not produce a double-terminator in the IR.
    internal void EmitMatchStatement(MatchStatement stmt)
    {
        var (patternArms, wildcardArm, n) = EmitMatchStatementTestChain(stmt);
        var condVarName = (stmt.Condition as VariableReferenceExpression)?.Name;

        for (int i = 0; i < patternArms.Count; i++)
        {
            var variantName = (patternArms[i].Pattern as VariableReferenceExpression)?.Name;
            var savedType   = NarrowCondVar(condVarName, variantName);
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            _blockOpen = true;
            foreach (var s in patternArms[i].Body)
                EmitStmt(s);
            RestoreCondVar(condVarName, savedType);
            if (_blockOpen)
                _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            _blockOpen = true;
            foreach (var s in wildcardArm.Body)
                EmitStmt(s);
            if (_blockOpen)
                _funcs.AppendLine($"  br label %match_merge_{n}");
        }
        else
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
        _blockOpen = true;
    }
}
