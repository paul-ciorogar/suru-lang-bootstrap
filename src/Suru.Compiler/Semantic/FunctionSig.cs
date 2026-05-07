using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

// Resolved signature for a user-defined function.
// ReturnType is null for void functions.
internal sealed record FunctionSig(
    IReadOnlyList<SuruType> ParamTypes,
    SuruType?               ReturnType);
