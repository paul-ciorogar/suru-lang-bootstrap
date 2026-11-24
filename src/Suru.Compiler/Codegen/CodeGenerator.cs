using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Testing;
using Suru.Lib;

namespace Suru.Compiler.Codegen;

public sealed partial class CodeGenerator
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

    /// <summary>
    /// Every global string constant, shared by all the functions in the module — a global is
    /// module-scope, so two functions printing the same format reuse one.
    /// </summary>
    private readonly Dictionary<string, LLVMValueRef> _strings = [];

    /// <summary>
    /// The stack slot behind each binding, with the type to load it back through. Scoped
    /// the same way the analyzer scopes types, so a shadowing binding gets its own slot
    /// and the outer one comes back when the block ends.
    /// <para>
    /// A loop scope carries where a <c>break</c> and a <c>continue</c> go as its payload, so this
    /// stage keeps no second copy of a nesting it is already tracking: the search that finds a
    /// <c>break</c> its target is the same walk that finds an identifier its slot. Neither
    /// statement takes a label, so the innermost enclosing loop is the only one either can name —
    /// which is what the innermost-first walk already means.
    /// </para>
    /// </summary>
    private readonly ScopeStack<
        VariableSlot,
        (LLVMBasicBlockRef Continue, LLVMBasicBlockRef Break)> _scopes = new();

    private CodeGenerator(Module module, BuildMode mode)
    {
        _module = module;
        _mode = mode;
        _llvmModule = LLVMModuleRef.CreateWithName("suru");

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

        // 'main' is emitted through an emitter like any other function would be, and first, so
        // its 'entry' is the first one in the module. What is special about it is only what
        // follows: the run frames and the exit code.
        using var main = new FunctionEmitter(this, mainFn);

        // Announced at the top of 'body' rather than 'entry', which holds nothing but allocas.
        if (_mode == BuildMode.Test)
            main.WriteFrame(Frame.RunStarted, ("run", "%d", Int32(0)));

        main.EmitStatements(_module.Statements);

        // Emitted in whichever block the builder ended in — after a trailing 'if' that is
        // 'if.end', after a trailing 'while' that is 'while.end', the one block on the path
        // that reaches the return. That is the point of it: its absence is how the driver
        // tells a run that died from a run that simply never reached a directive.
        //
        // Unguarded, unlike the branches inside a construct: only 'break', 'continue' and
        // 'return' can leave a block terminated, and semantic analysis has already rejected
        // each of them at the top level. So the top level is never the terminated block.
        if (_mode == BuildMode.Test)
            main.WriteFrame(Frame.RunFinished, ("run", "%d", Int32(0)), ("exit", "%d", Int32(MainExitCode)));

        main.Return(Int32(MainExitCode));
        main.Finish();

        return _llvmModule;
    }

    /// <summary>
    /// The innermost enclosing loop's two exits — the exact counterpart of <see cref="Variable"/>
    /// one method down: the same walk over the same structure, one resolving a <c>break</c> and
    /// the other a name. Finding nothing means semantic analysis let a <c>break</c> through from
    /// outside a loop, which is a compiler bug rather than a program error and is reported as one.
    /// </summary>
    private (LLVMBasicBlockRef Continue, LLVMBasicBlockRef Break) Loop(string keyword) =>
        _scopes.TryFindEnclosing(ScopeKind.Loop, out var loop)
            ? loop
            : throw new CodegenException($"'{keyword}' outside a loop reached codegen");

    private VariableSlot Variable(string name) =>
        _scopes.LookupVariable(name).UnwrapOrElse(_ =>
            throw new CodegenException($"unknown variable '{name}'"));

    private static LLVMTypeRef LlvmType(SuruType type)
    {
        if (type == SuruType.Bool) return LLVMTypeRef.Int1;
        if (type == SuruType.I64) return LLVMTypeRef.Int64;
        if (type == SuruType.F64) return LLVMTypeRef.Double;
        throw new CodegenException($"type '{type}' has no representation");
    }

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

    private static LLVMValueRef Int32(int value) =>
        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)value, false);

    private static SuruType TypeOf(Expression expression) =>
        expression.Type
            ?? throw new CodegenException($"expression at {expression.Position} was never typed");

    /// <summary>
    /// Interns a global string constant so repeated literals share one global.
    /// <para>
    /// The cache is the module's, but the builder is the caller's: <c>BuildGlobalStringPtr</c>
    /// needs a builder with a position, and with one emitter per function there is no builder
    /// that belongs to the module. Passing one that was never positioned is an LLVM assertion,
    /// not a C# exception, which is why every emitter goes through its own.
    /// </para>
    /// </summary>
    private LLVMValueRef String(LLVMBuilderRef builder, string value)
    {
        if (_strings.TryGetValue(value, out var existing))
            return existing;

        var global = builder.BuildGlobalStringPtr(value, "");
        _strings[value] = global;
        return global;
    }
}
