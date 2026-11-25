namespace Suru.Compiler.Testing;

/// <summary>
/// The test channel failed, and this says how.
/// <para>
/// Every one carries a whole sentence meant to be shown as it is, because the failure they
/// exist to prevent is the shim's own default: records dropped silently, every directive
/// annotated <c>undefined</c>, and nothing said about why.
/// </para>
/// <para>
/// A run that throws one of these leaves the source file untouched. That is deliberate — a
/// channel that broke has no results, and writing "no results" into every directive's line
/// would be the compiler stating as fact something it does not know.
/// </para>
/// </summary>
public sealed class TestChannelException(string message) : Exception(message);
