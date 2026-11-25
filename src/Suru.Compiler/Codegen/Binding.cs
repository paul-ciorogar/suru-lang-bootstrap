using LLVMSharp.Interop;

namespace Suru.Compiler.Codegen;

/// <summary>
/// What a name means to this stage — the counterpart of the analyzer's bindings. Variables and
/// functions share one namespace, so they share one <see cref="ScopeStack{TEntry, TScope}"/>.
/// </summary>
public abstract record Binding : IScopeEntry
{
    public abstract bool SurvivesFunctionBoundary { get; }
}

/// <summary>
/// A <c>let</c>'s or a parameter's stack slot and the type to load it back through.
/// <para>
/// It does not survive a function boundary, for the same reason the analyzer's does not: a body
/// cannot see its caller's locals, so it can never be handed a slot in the caller's frame.
/// </para>
/// </summary>
public sealed record VariableSlot(LLVMValueRef Slot, LLVMTypeRef Type) : Binding
{
    public override bool SurvivesFunctionBoundary => false;
}

/// <summary>
/// An <c>fn</c>: the LLVM function and the function type a call is built through. It survives a
/// function boundary, because recursion and a call to a sibling resolve from inside a body to a
/// name declared outside it.
/// </summary>
public sealed record FunctionSymbol(LLVMValueRef Function, LLVMTypeRef Type) : Binding
{
    public override bool SurvivesFunctionBoundary => true;
}
