namespace Suru.Compiler.Debug;

/// <summary>
/// The compiler stages that can dump their output.
/// <para>
/// A compiler is debugged by diffing whole intermediate forms before and after a stage, not
/// by logging events. Dumps are always compiled in and off by default, because the person who
/// needs them is usually holding a release build.
/// </para>
/// </summary>
[Flags]
public enum Dump
{
    None = 0,

    /// <summary>The token stream, in source order.</summary>
    Tokens = 1 << 0,

    /// <summary>The AST as the parser produced it, before any types exist.</summary>
    Ast = 1 << 1,

    /// <summary>The same AST after semantic analysis, with the resolved type on every expression.</summary>
    TypedAst = 1 << 2,

    /// <summary>The generated LLVM IR, in LLVM's textual form.</summary>
    Llvm = 1 << 3,

    All = Tokens | Ast | TypedAst | Llvm,
}
