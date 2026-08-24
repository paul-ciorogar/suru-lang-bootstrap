namespace Suru.Compiler.Testing;

/// <summary>
/// The test channel said something that is not a frame.
/// <para>
/// Thrown rather than skipped, and never resynchronised past: a length that does not agree with
/// the bytes around it means the reader no longer knows where anything starts, and guessing is
/// exactly the failure framing exists to prevent. The driver catches this and reports it as a
/// run failure of its own, the way <c>Compiler.Build</c> catches a <c>ParseException</c>.
/// </para>
/// </summary>
public sealed class FrameException(string message) : Exception(message);
