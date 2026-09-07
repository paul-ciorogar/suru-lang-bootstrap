using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Testing;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    private readonly Module _module;

    /// <summary>
    /// Which build this is. The directives themselves are already gone from a production
    /// module by the time codegen runs, since the lexer never handed them over — but the test
    /// channel's runtime declarations must appear in a test module and only there, and that is
    /// a decision this stage has to be able to make for itself.
    /// </summary>
    private readonly BuildMode _mode;

    /// <summary>
    /// The value <c>main</c> returns, and the <c>exit</c> a <c>run-finished</c> frame carries.
    /// One constant so the two cannot drift the day a program can fail.
    /// </summary>
    private const int MainExitCode = 0;

    private readonly LLVMModuleRef _llvmModule;
    private readonly LLVMBuilderRef _builder;

    /// <summary>
    /// Positioned at <c>main</c>'s entry block and never moved. Every stack slot is built
    /// through it, so a binding's slot belongs to the frame however deeply branched the
    /// statement that declares it is.
    /// </summary>
    private readonly LLVMBuilderRef _allocas;

    private readonly LLVMTypeRef _printfType;
    private readonly LLVMValueRef _printfFn;

    /// <summary>
    /// The test channel's runtime, declared in <see cref="BuildMode.Test"/> and left null in a
    /// production module — which is the whole reason this stage is told its mode. The shim's
    /// own definitions live in <c>runtime/suru_rt.c</c>, linked in by the same decision.
    /// </summary>
    private readonly LLVMTypeRef _frameType;
    private readonly LLVMValueRef _frameBeginFn;
    private readonly LLVMValueRef _frameEndFn;
    private readonly LLVMTypeRef _fieldType;
    private readonly LLVMValueRef _fieldFn;

    private readonly Dictionary<string, LLVMValueRef> _strings = [];

    /// <summary>
    /// The stack slot behind each binding, with the type to load it back through. Scoped
    /// the same way the analyzer scopes types, so a shadowing binding gets its own slot
    /// and the outer one comes back when the block ends.
    /// </summary>
    private readonly ScopeStack<(LLVMValueRef Slot, LLVMTypeRef Type)> _scopes = new();

    private CodeGenerator(Module module, BuildMode mode)
    {
        _module = module;
        _mode = mode;
        _llvmModule = LLVMModuleRef.CreateWithName("suru");
        _builder = LLVMBuilderRef.Create(_llvmModule.Context);
        _allocas = LLVMBuilderRef.Create(_llvmModule.Context);

        // Declare printf: i32 (ptr, ...) — printLn's, in both modes.
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        _printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        _printfFn = _llvmModule.AddFunction("printf", _printfType);

        if (mode == BuildMode.Test)
        {
            // void suru_frame_begin(void) / void suru_frame_end(void)
            _frameType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
            _frameBeginFn = _llvmModule.AddFunction("suru_frame_begin", _frameType);
            _frameEndFn = _llvmModule.AddFunction("suru_frame_end", _frameType);

            // void suru_field(const char *key, const char *format, ...) — printf-shaped on
            // purpose, so it takes the same format strings printLn already uses.
            _fieldType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [ptrType, ptrType], true);
            _fieldFn = _llvmModule.AddFunction("suru_field", _fieldType);
        }
    }

    public static LLVMModuleRef Generate(Module module, BuildMode mode)
    {
        var generator = new CodeGenerator(module, mode);
        return generator._Generate();
    }

    private LLVMModuleRef _Generate()
    {
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = _llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        // Two blocks rather than one: 'entry' is the frame and holds nothing but allocas,
        // 'body' holds the code. Keeping them apart is what lets _allocas sit at the end of
        // 'entry' for the whole run without later code getting in ahead of it — which one
        // block could not promise the moment a branch terminates it.
        var entry = mainFn.AppendBasicBlock("entry");
        var body = mainFn.AppendBasicBlock("body");

        _allocas.PositionAtEnd(entry);
        _builder.PositionAtEnd(body);

        // Announced at the top of 'body' rather than 'entry', which holds nothing but allocas.
        if (_mode == BuildMode.Test)
            WriteFrame(Frame.RunStarted, ("run", "%d", Int32(0)));

        foreach (var statement in _module.Statements)
            EmitStatement(statement);

        // Emitted in whichever block the builder ended in — after a trailing 'if' that is
        // 'if.end', the one block on the path that reaches the return. That is the point of
        // it: its absence is how the driver tells a run that died from a run that simply
        // never reached a directive.
        if (_mode == BuildMode.Test)
            WriteFrame(Frame.RunFinished, ("run", "%d", Int32(0)), ("exit", "%d", Int32(MainExitCode)));

        _builder.BuildRet(Int32(MainExitCode));

        // Terminated last, once every slot is in.
        _allocas.BuildBr(body);

        _allocas.Dispose();
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
            case BlockStatement block:
                // Still purely lexical: no branch and no new basic block, only a scope. An
                // arm gets its blocks from the 'if', not from the block that is its body.
                _scopes.EnterNew();
                foreach (var inner in block.Statements)
                    EmitStatement(inner);
                _scopes.Exit();
                break;
            case IfStatement branch:
                EmitIf(branch);
                break;
            case MockDirective mock:
                // Emitted exactly as the assignment it is; only reaching codegen at all is
                // what makes it a test-build feature.
                _builder.BuildStore(EmitExpr(mock.Value), Variable(mock.Name).Slot);
                break;
            case ViewDirective view:
                EmitView(view);
                break;
            case AssertDirective assert:
                EmitAssert(assert);
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
    /// The first construct to emit more than one basic block. The condition is already an
    /// <c>i1</c> — <c>bool</c> maps to <see cref="LLVMTypeRef.Int1"/> and a comparison yields
    /// one — so it feeds the branch with nothing in between.
    /// <para>
    /// With no <c>else</c> the false edge goes straight to <c>if.end</c>: there is no
    /// alternative to run, only somewhere to be afterwards, which is why the block exists in
    /// both shapes and is named for continuing rather than for merging.
    /// </para>
    /// <para>
    /// Both arms are branched to the end unconditionally, because nothing can leave one early:
    /// there is no <c>return</c>, no <c>break</c> and no diverging call. So <c>if.end</c>
    /// always has a predecessor and neither arm can already be terminated. That is the
    /// assumption to revisit the day a statement can leave a block.
    /// </para>
    /// </summary>
    private void EmitIf(IfStatement branch)
    {
        // The condition is emitted first, so the function is read back from where emission
        // actually ended up rather than from where it started — which will matter the day an
        // expression can move the insert point, as a short-circuiting 'and' would.
        var condition = EmitExpr(branch.Condition);
        var function = _builder.InsertBlock.Parent;

        var otherwise = branch.Else;

        // Appended in source order so the IR reads in it. Not an LLVMBasicBlockRef? for the
        // absent arm: the type converts implicitly from a raw pointer, so a null literal
        // would quietly become a non-null nullable holding a null handle.
        var thenBlock = function.AppendBasicBlock("if.then");
        var elseBlock = otherwise is null ? default : function.AppendBasicBlock("if.else");
        var endBlock = function.AppendBasicBlock("if.end");

        _builder.BuildCondBr(condition, thenBlock, otherwise is null ? endBlock : elseBlock);

        _builder.PositionAtEnd(thenBlock);
        EmitStatement(branch.Then);
        _builder.BuildBr(endBlock);

        // 'else if' arrives here as an IfStatement and needs no case of its own: it appends
        // its own blocks, leaves the builder at its own end, and the branch below terminates
        // that block rather than this one.
        if (otherwise is not null)
        {
            _builder.PositionAtEnd(elseBlock);
            EmitStatement(otherwise);
            _builder.BuildBr(endBlock);
        }

        _builder.PositionAtEnd(endBlock);
    }

    /// <summary>
    /// The slot goes in the entry block and the store stays where the binding sits: a frame
    /// slot belongs to the frame, and it is the store — not the alloca — that carries the
    /// binding's position, so initialisation stays in order. A binding that shadows another is
    /// still simply a second alloca, which LLVM gives its own name.
    /// </summary>
    private void EmitLet(LetStatement let)
    {
        var value = EmitExpr(let.Value);
        var type = LlvmType(let.Value.Type
            ?? throw new CodegenException($"binding '{let.Name}' at {let.Position} was never typed"));

        var slot = _allocas.BuildAlloca(type, let.Name);
        _builder.BuildStore(value, slot);
        _scopes.Declare(let.Name, (slot, type));
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
        _scopes.TryLookup(name, out var variable)
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
        var (format, argument) = Render(EmitExpr(arg), TypeOf(arg));
        Printf(format + "\n", argument);
    }

    private void EmitView(ViewDirective view)
    {
        var (format, argument) = Render(EmitExpr(view.Subject), TypeOf(view.Subject));

        WriteFrame(Frame.View,
            ("id", "%d", Int32(view.Id)),
            ("value", format, argument));
    }

    /// <summary>
    /// Compares the two operands and reports the outcome along with both values, so the
    /// driver can say what was expected and what turned up without evaluating anything
    /// itself. The comparison is the one the program computes, not a comparison of the
    /// printed text — <c>%g</c> rounds, and two <c>f64</c>s that print alike need not be
    /// equal.
    /// </summary>
    private void EmitAssert(AssertDirective assert)
    {
        var type = TypeOf(assert.Actual);
        var actual = EmitExpr(assert.Actual);
        var expected = EmitExpr(assert.Expected);

        var equal = type == SuruType.F64
            ? _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, actual, expected)
            : _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, actual, expected);

        // The wire spells an outcome 'pass' or 'fail', so the word is picked at runtime the
        // same way Render picks 'true' or 'false' for a bool.
        var outcome = _builder.BuildSelect(equal, String("pass"), String("fail"));

        var (format, actualArgument) = Render(actual, type);
        var (_, expectedArgument) = Render(expected, type);

        WriteFrame(Frame.Assert,
            ("id", "%d", Int32(assert.Id)),
            ("outcome", "%s", outcome),
            ("actual", format, actualArgument),
            ("expected", format, expectedArgument));
    }

    /// <summary>
    /// The printf specifier for a value of the given type, and the argument to pass with it.
    /// One source of truth for how a value is rendered, so <c>printLn</c> and the test
    /// records cannot disagree about what a value looks like.
    /// </summary>
    private (string Format, LLVMValueRef Argument) Render(LLVMValueRef value, SuruType type)
    {
        // printf has no bool conversion, so pick the literal text at runtime.
        if (type == SuruType.Bool)
            return ("%s", _builder.BuildSelect(value, String("true"), String("false")));
        if (type == SuruType.I64)
            return ("%lld", value);
        if (type == SuruType.F64)
            return ("%g", value);

        throw new CodegenException($"cannot print a value of type '{type}'");
    }

    /// <summary>
    /// Writes one frame down the test channel: <c>begin</c>, a field per entry, <c>end</c>.
    /// <para>
    /// The <c>kind</c> field is written here rather than by the shim, which has no idea what
    /// a kind is — unlike <see cref="FrameWriter"/>, whose callers do not have to say. It goes
    /// through the same <c>%s</c> path as any other value, so a kind is never a format string.
    /// </para>
    /// <para>
    /// Separate from <see cref="Printf"/>, which is <c>printLn</c>'s alone: a frame goes to the
    /// harness and the program's own output goes to whoever ran it, and the two now travel on
    /// different file descriptors entirely.
    /// </para>
    /// </summary>
    private void WriteFrame(
        string kind, params (string Key, string Format, LLVMValueRef Argument)[] fields)
    {
        if (_mode != BuildMode.Test)
            throw new CodegenException($"a '{kind}' frame has no place in a production build");

        // The empty argument list is typed explicitly for the same reason Printf's is: a
        // collection expression cannot choose between BuildCall2's array and span overloads.
        LLVMValueRef[] none = [];

        _builder.BuildCall2(_frameType, _frameBeginFn, none, "");

        Field(Frame.KindKey, "%s", String(kind));
        foreach (var (key, format, argument) in fields)
            Field(key, format, argument);

        _builder.BuildCall2(_frameType, _frameEndFn, none, "");
    }

    private void Field(string key, string format, LLVMValueRef value)
    {
        LLVMValueRef[] arguments = [String(key), String(format), value];
        _builder.BuildCall2(_fieldType, _fieldFn, arguments, "");
    }

    private static LLVMValueRef Int32(int value) =>
        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)value, false);

    private static SuruType TypeOf(Expression expression) =>
        expression.Type
            ?? throw new CodegenException($"expression at {expression.Position} was never typed");

    private void Printf(string format, params LLVMValueRef[] values)
    {
        // The argument array is built and typed explicitly: BuildCall2 also has a
        // ReadOnlySpan overload, and a collection expression cannot choose between them.
        LLVMValueRef[] arguments = [String(format), .. values];
        _builder.BuildCall2(_printfType, _printfFn, arguments, "");
    }

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
