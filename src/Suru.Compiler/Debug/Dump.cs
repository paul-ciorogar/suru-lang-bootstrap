namespace Suru.Compiler.Debug;

/// <summary>
/// The compiler stages that can dump their output.
/// <para>
/// Compilers debug by dumping the whole intermediate form after each stage — you
/// diff what the program looked like before and after a stage — rather than by
/// narrating events the way an application log does. The dumps are always
/// compiled in and off by default, because the person who needs them is usually
/// holding a release build.
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
