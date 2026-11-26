using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Testing;
using Suru.Lib;

namespace Suru.Compiler.Codegen;

public sealed partial class CodeGenerator
{
    private readonly Module _module;

    /// <summary>
    /// Which build this is. A production module has no directives left to emit, but only a test
    /// module may declare the test channel's runtime, and that is this stage's decision.
    /// </summary>
    private readonly BuildMode _mode;

    /// <summary>
    /// The value <c>main</c> returns, and the <c>exit</c> a <c>run-finished</c> frame carries.
    /// One constant so the two cannot drift the day a program can fail.
    /// </summary>
    private const int MainExitCode = 0;

    /// <summary>
    /// Suru's entry point: the name the analyzer reserves, and the function the C <c>main</c>
    /// calls when a file declares it.
    /// </summary>
    private const string Main = "main";

    /// <summary>
    /// Suru's <c>main</c>'s symbol. No Suru or C identifier can contain the dot, so it never
    /// collides with the C <c>main</c>, and it reads like <c>outer.helper</c>.
    /// </summary>
    private const string MainSymbol = "suru.main";

    private readonly LLVMModuleRef _llvmModule;

    private readonly LLVMTypeRef _printfType;
    private readonly LLVMValueRef _printfFn;

    /// <summary>
    /// The test channel's runtime: declared in <see cref="BuildMode.Test"/>, null otherwise. The
    /// definitions are in <c>runtime/suru_rt.c</c>, which only a test build links.
    /// </summary>
    private readonly LLVMTypeRef _frameType;
    private readonly LLVMValueRef _frameBeginFn;
    private readonly LLVMValueRef _frameEndFn;
    private readonly LLVMTypeRef _fieldType;
    private readonly LLVMValueRef _fieldFn;

    /// <summary>
    /// Interned string constants, per module rather than per function, so the same text is one
    /// global wherever it is used.
    /// </summary>
    private readonly Dictionary<string, LLVMValueRef> _strings = [];

    /// <summary>
    /// What each name means here, scoped exactly as the analyzer scopes it, so a shadowing binding
    /// gets its own slot and the outer one comes back when the block ends.
    /// <para>
    /// A loop scope also carries the targets of <c>break</c> and <c>continue</c>, so loop nesting
    /// is not tracked a second time.
    /// </para>
    /// </summary>
    private readonly ScopeStack<
        Binding,
        (LLVMBasicBlockRef Continue, LLVMBasicBlockRef Break)> _scopes = new();

    /// <summary>
    /// The functions being emitted, outermost first. They qualify a nested function's symbol, so
    /// <c>outer.helper</c> says in the IR where it lives.
    /// </summary>
    private readonly List<string> _enclosingFunctions = [];

    private CodeGenerator(Module module, BuildMode mode)
    {
        _module = module;
        _mode = mode;
        _llvmModule = LLVMModuleRef.CreateWithName("suru");

        // printf is printLn's, so every build needs it.
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        _printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        _printfFn = _llvmModule.AddFunction("printf", _printfType);

        if (mode == BuildMode.Test)
        {
            // void suru_frame_begin(void) / void suru_frame_end(void)
            _frameType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
            _frameBeginFn = _llvmModule.AddFunction("suru_frame_begin", _frameType);
            _frameEndFn = _llvmModule.AddFunction("suru_frame_end", _frameType);

            // printf-shaped on purpose, so it takes the same format strings printLn uses.
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

        // Declared first, so the IR opens with the program's entry point. What sets it apart
        // from other functions is only the run frames and the exit code.
        using var main = new FunctionEmitter(this, mainFn);

        // In 'body', because 'entry' is reserved for allocas.
        if (_mode == BuildMode.Test)
            main.WriteFrame(Frame.RunStarted, ("run", "%d", Int32(0)));

        // Before any body, so every function sees every constant wherever it is written. The
        // analyzer has already rejected every file-level statement but 'let' and 'fn'.
        foreach (var let in _module.Statements.OfType<LetStatement>())
            _scopes.Declare(let.Name, new ConstantSymbol(Literal(let.Value)));

        // The file's scope is never entered, so nothing else would emit its functions.
        EmitFunctions(_module.Statements);

        if (_scopes.LookupFunction(Main) is Ok<Binding, Unbound>(FunctionSymbol entry))
            main.CallVoid(entry);

        // 'run-finished' is how the driver tells a run that died from one that never reached a
        // directive. It needs no terminator guard: the wrapper holds no user code, so nothing
        // can have left this block already.
        if (_mode == BuildMode.Test)
            main.WriteFrame(Frame.RunFinished, ("run", "%d", Int32(0)), ("exit", "%d", Int32(MainExitCode)));

        main.Return(Int32(MainExitCode));
        main.Finish();

        return _llvmModule;
    }

    /// <summary>
    /// The innermost enclosing loop's exits. Finding none is a compiler bug, not a program error:
    /// the analyzer rejects a <c>break</c> or <c>continue</c> outside a loop.
    /// </summary>
    private (LLVMBasicBlockRef Continue, LLVMBasicBlockRef Break) Loop(string keyword) =>
        _scopes.TryFindEnclosing(ScopeKind.Loop, out var loop)
            ? loop
            : throw new CodegenException($"'{keyword}' outside a loop reached codegen");

    /// <summary>
    /// What a name read as a value means: a slot to load from, or a constant's immediate.
    /// </summary>
    private Binding Value(string name) =>
        _scopes.LookupVariable(name).UnwrapOrElse(_ =>
            throw new CodegenException($"unknown variable '{name}'"));

    /// <summary>
    /// A literal's LLVM immediate. Static because a constant's value is built outside any
    /// function, and shared with expression emission so there is one way a literal becomes LLVM.
    /// </summary>
    private static LLVMValueRef Literal(Expression literal) => literal switch
    {
        BoolLiteral b => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b.Value ? 1ul : 0ul, false),
        IntLiteral i => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true),
        FloatLiteral f => LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value),
        _ => throw new CodegenException($"'{literal.GetType().Name}' is not a literal"),
    };

    private VariableSlot Variable(string name) =>
        _scopes.LookupVariable(name).UnwrapOrElse(_ =>
            throw new CodegenException($"unknown variable '{name}'")) as VariableSlot
        ?? throw new CodegenException($"'{name}' is not a variable");

    private FunctionSymbol Function(string name) =>
        _scopes.LookupFunction(name).UnwrapOrElse(_ =>
            throw new CodegenException($"unknown function '{name}'")) as FunctionSymbol
        ?? throw new CodegenException($"'{name}' is not a function");

    /// <summary>
    /// Gives every function in a statement list its LLVM function and its body, ahead of the
    /// list's statements.
    /// <para>
    /// Exists so a call can name any function in scope — itself, a sibling, one declared further
    /// down — the codegen counterpart of the analyzer's signature pre-pass. It runs at scope entry
    /// rather than where the declaration is written because a <c>fn</c> after a <c>return</c> is
    /// unreachable code, which is never emitted, and would otherwise be left without a body.
    /// </para>
    /// </summary>
    private void EmitFunctions(IReadOnlyList<Statement> statements)
    {
        List<(FunctionDeclaration Declaration, FunctionSymbol Symbol)> declared = [];
        foreach (var declaration in statements.OfType<FunctionDeclaration>())
            declared.Add((declaration, DeclareFunction(declaration)));

        foreach (var (declaration, symbol) in declared)
            EmitFunction(declaration, symbol);
    }

    /// <summary>
    /// The LLVM function for a declaration, bound under its name in the current scope.
    /// <para>
    /// Internal linkage, because nothing outside the module calls a Suru function, and so a Suru
    /// name can never collide with libc's — <c>printf</c> among them. Symbol names need not be
    /// unique: two blocks may each declare a <c>helper</c>, since a call resolves by scope, not
    /// by symbol.
    /// </para>
    /// </summary>
    private FunctionSymbol DeclareFunction(FunctionDeclaration declaration)
    {
        var returnType = LlvmReturnType(declaration.ReturnType
            ?? throw new CodegenException($"function '{declaration.Name}' at {declaration.Position} was never typed"));

        LLVMTypeRef[] parameterTypes = [.. declaration.Parameters.Select(parameter =>
            LlvmType(parameter.Type
                ?? throw new CodegenException($"parameter '{parameter.Name}' at {parameter.Position} was never typed")))];

        var type = LLVMTypeRef.CreateFunction(returnType, parameterTypes);
        // Only the file-level 'main' gets here: the analyzer rejects one anywhere else.
        var name = declaration.Name == Main
            ? MainSymbol
            : string.Join('.', [.. _enclosingFunctions, declaration.Name]);

        var function = _llvmModule.AddFunction(name, type);
        function.Linkage = LLVMLinkage.LLVMInternalLinkage;

        // Named after the source, so the IR reads.
        for (var i = 0; i < declaration.Parameters.Count; i++)
            function.GetParam((uint)i).Name = declaration.Parameters[i].Name;

        var symbol = new FunctionSymbol(function, type);
        _scopes.Declare(declaration.Name, symbol);
        return symbol;
    }

    private void EmitFunction(FunctionDeclaration declaration, FunctionSymbol symbol)
    {
        _enclosingFunctions.Add(declaration.Name);

        using (var emitter = new FunctionEmitter(this, symbol.Function))
        {
            emitter.EmitBody(declaration);
            emitter.Finish();
        }

        _enclosingFunctions.RemoveAt(_enclosingFunctions.Count - 1);
    }

    /// <summary>
    /// A return type: the one place <c>void</c> has a representation. Kept apart from
    /// <see cref="LlvmType"/>, which still refuses <c>void</c>, because a slot of no type is a bug.
    /// </summary>
    private static LLVMTypeRef LlvmReturnType(SuruType type) =>
        type == SuruType.Void ? LLVMTypeRef.Void : LlvmType(type);

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
    /// A string constant, interned so the same text is one global.
    /// <para>
    /// Takes the caller's builder because LLVM only creates the global through a positioned
    /// builder, and no builder belongs to the module. An unpositioned one is an LLVM assertion,
    /// not a C# exception.
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
