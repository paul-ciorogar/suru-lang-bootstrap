using LLVMSharp.Interop;

namespace Suru.Compiler.Codegen;

/// <summary>
/// A binding's stack slot and the type to load it back through — what this stage keeps in its
/// <see cref="ScopeStack{TEntry, TScope}"/>, and the counterpart of the analyzer's variable
/// binding.
/// <para>
/// It does not survive a function boundary, for the same reason the analyzer's does not: a body
/// cannot see its caller's locals, so it can never be handed a slot in the caller's frame.
/// </para>
/// </summary>
public sealed record VariableSlot(LLVMValueRef Slot, LLVMTypeRef Type) : IScopeEntry
{
    public bool SurvivesFunctionBoundary => false;
}
