namespace Suru.Compiler.Codegen;

/// <summary>
/// Thrown when codegen meets something the semantic analyzer should have
/// rejected or annotated. Reaching this is a compiler bug, not a user error.
/// </summary>
public sealed class CodegenException(string message) : Exception(message);
