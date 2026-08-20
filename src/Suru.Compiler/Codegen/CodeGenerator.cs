using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    private readonly Module _module;
    private readonly LLVMModuleRef _llvmModule;
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMTypeRef _printfType;
    private readonly LLVMValueRef _printfFn;
    private readonly Dictionary<string, LLVMValueRef> _strings = [];

    /// <summary>The stack slot behind each binding, with the type to load it back through.</summary>
    private readonly Dictionary<string, (LLVMValueRef Slot, LLVMTypeRef Type)> _variables = [];

    private CodeGenerator(Module module)
    {
        _module = module;
        _llvmModule = LLVMModuleRef.CreateWithName("suru");
        _builder = LLVMBuilderRef.Create(_llvmModule.Context);

        // Declare printf: i32 (ptr, ...)
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        _printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        _printfFn = _llvmModule.AddFunction("printf", _printfType);
    }

    public static LLVMModuleRef Generate(Module module)
    {
        var generator = new CodeGenerator(module);
        return generator._Generate();
    }

    private LLVMModuleRef _Generate()
    {
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = _llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        _builder.PositionAtEnd(mainFn.AppendBasicBlock("entry"));

        foreach (var statement in _module.Statements)
            EmitStatement(statement);

        _builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        _builder.Dispose();

        return _llvmModule;
    }

    private void EmitStatement(Statement statement)
    {
        switch (statement)
        {
            case ExpressionStatement exprStmt:
                EmitExpr(exprStmt.Expression);
                break;
            case LetStatement let:
                EmitLet(let);
                break;
            case AssignmentStatement assignment:
                _builder.BuildStore(EmitExpr(assignment.Value), Variable(assignment.Name).Slot);
                break;
            default:
                throw new CodegenException($"cannot emit statement '{statement.GetType().Name}'");
        }
    }

    /// <summary>
    /// Emits an expression and returns its value, or a null value reference for
    /// expressions of type <see cref="SuruType.Void"/>.
    /// </summary>
    private LLVMValueRef EmitExpr(Expression expression)
    {
        switch (expression)
        {
            case BoolLiteral b:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b.Value ? 1ul : 0ul, false);
            case IntLiteral i:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true);
            case FloatLiteral f:
                return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value);
            case CallExpression { Name: "printLn", Args.Count: 1 } call:
                EmitPrintLn(call.Args[0]);
                return default;
            case IdentifierExpression identifier:
                var variable = Variable(identifier.Name);
                return _builder.BuildLoad2(variable.Type, variable.Slot, identifier.Name);
            case BinaryExpression binary:
                return EmitBinary(binary);
            case UnaryExpression unary:
                return EmitUnary(unary);
            default:
                throw new CodegenException($"cannot emit expression '{expression.GetType().Name}'");
        }
    }

    /// <summary>
    /// The slot is allocated where the binding sits. Everything is one straight-line
    /// <c>main</c>, so there is no loop for the alloca to run inside; hoisting to the
    /// entry block becomes necessary when control flow arrives.
    /// </summary>
    private void EmitLet(LetStatement let)
    {
        var value = EmitExpr(let.Value);
        var type = LlvmType(let.Value.Type
            ?? throw new CodegenException($"binding '{let.Name}' at {let.Position} was never typed"));

        var slot = _builder.BuildAlloca(type, let.Name);
        _builder.BuildStore(value, slot);
        _variables[let.Name] = (slot, type);
    }

    /// <summary>
    /// Both operands are evaluated: no expression can have a side effect yet, so
    /// <c>and</c> and <c>or</c> need no branching. Short-circuiting arrives with
    /// user-defined functions.
    /// </summary>
    private LLVMValueRef EmitBinary(BinaryExpression binary)
    {
        var left = EmitExpr(binary.Left);
        var right = EmitExpr(binary.Right);
        var operandType = binary.Left.Type
            ?? throw new CodegenException($"operand at {binary.Left.Position} was never typed");

        if (operandType == SuruType.F64)
            return EmitFloatBinary(binary.Operator, left, right);

        return binary.Operator switch
        {
            BinaryOperator.Add => _builder.BuildAdd(left, right),
            BinaryOperator.Subtract => _builder.BuildSub(left, right),
            BinaryOperator.Multiply => _builder.BuildMul(left, right),
            // Integer division truncates toward zero and yields an integer.
            BinaryOperator.Divide => _builder.BuildSDiv(left, right),
            BinaryOperator.Remainder => _builder.BuildSRem(left, right),
            BinaryOperator.And => _builder.BuildAnd(left, right),
            BinaryOperator.Or => _builder.BuildOr(left, right),
            _ => _builder.BuildICmp(IntPredicate(binary.Operator), left, right),
        };
    }

    private LLVMValueRef EmitFloatBinary(BinaryOperator op, LLVMValueRef left, LLVMValueRef right) =>
        op switch
        {
            BinaryOperator.Add => _builder.BuildFAdd(left, right),
            BinaryOperator.Subtract => _builder.BuildFSub(left, right),
            BinaryOperator.Multiply => _builder.BuildFMul(left, right),
            BinaryOperator.Divide => _builder.BuildFDiv(left, right),
            BinaryOperator.Remainder => _builder.BuildFRem(left, right),
            BinaryOperator.Equal => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
            BinaryOperator.NotEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right),
            BinaryOperator.Less => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
            BinaryOperator.LessOrEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
            BinaryOperator.Greater => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
            BinaryOperator.GreaterOrEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
            _ => throw new CodegenException($"cannot apply operator '{Operators.Text(op)}' to 'f64'"),
        };

    private static LLVMIntPredicate IntPredicate(BinaryOperator op) => op switch
    {
        BinaryOperator.Equal => LLVMIntPredicate.LLVMIntEQ,
        BinaryOperator.NotEqual => LLVMIntPredicate.LLVMIntNE,
        BinaryOperator.Less => LLVMIntPredicate.LLVMIntSLT,
        BinaryOperator.LessOrEqual => LLVMIntPredicate.LLVMIntSLE,
        BinaryOperator.Greater => LLVMIntPredicate.LLVMIntSGT,
        BinaryOperator.GreaterOrEqual => LLVMIntPredicate.LLVMIntSGE,
        _ => throw new CodegenException($"cannot apply operator '{Operators.Text(op)}' to an integer"),
    };

    private LLVMValueRef EmitUnary(UnaryExpression unary)
    {
        var operand = EmitExpr(unary.Operand);
        return unary.Operator switch
        {
            UnaryOperator.Not => _builder.BuildNot(operand),
            _ when unary.Operand.Type == SuruType.F64 => _builder.BuildFNeg(operand),
            _ => _builder.BuildNeg(operand),
        };
    }

    private (LLVMValueRef Slot, LLVMTypeRef Type) Variable(string name) =>
        _variables.TryGetValue(name, out var variable)
            ? variable
            : throw new CodegenException($"unknown variable '{name}'");

    private static LLVMTypeRef LlvmType(SuruType type)
    {
        if (type == SuruType.Bool) return LLVMTypeRef.Int1;
        if (type == SuruType.I64) return LLVMTypeRef.Int64;
        if (type == SuruType.F64) return LLVMTypeRef.Double;
        throw new CodegenException($"type '{type}' has no representation");
    }

    private void EmitPrintLn(Expression arg)
    {
        var value = EmitExpr(arg);
        var type = arg.Type
            ?? throw new CodegenException($"argument at {arg.Position} was never typed");

        if (type == SuruType.Bool)
        {
            // printf has no bool conversion, so pick the literal text at runtime.
            var text = _builder.BuildSelect(value, String("true"), String("false"));
            Printf("%s\n", text);
        }
        else if (type == SuruType.I64)
        {
            Printf("%lld\n", value);
        }
        else if (type == SuruType.F64)
        {
            Printf("%g\n", value);
        }
        else
        {
            throw new CodegenException($"cannot print a value of type '{type}'");
        }
    }

    private void Printf(string format, LLVMValueRef value) =>
        _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { String(format), value }, "");

    /// <summary>Interns a global string constant so repeated literals share one global.</summary>
    private LLVMValueRef String(string value)
    {
        if (_strings.TryGetValue(value, out var existing))
            return existing;

        var global = _builder.BuildGlobalStringPtr(value, "");
        _strings[value] = global;
        return global;
    }
}
