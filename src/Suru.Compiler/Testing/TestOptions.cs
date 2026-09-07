namespace Suru.Compiler.Testing;

/// <summary>
/// The two deadlines a test run answers to.
/// <para>
/// Two rather than one because "the binary never connected" and "the program hung" are
/// different failures with different causes — a shim that failed to link versus a body that
/// never ended — and collapsing them into one number means every diagnostic has to hedge.
/// </para>
/// <para>
/// The defaults are named here and nowhere else, so the CLI's usage text quotes them rather
/// than repeating them.
/// </para>
/// </summary>
/// <param name="Connect">
/// How long the child has to reach the channel. Short on purpose: connecting is the first
/// thing the shim's constructor does, so a child that has not managed it is not slow, it is
/// broken.
/// </param>
/// <param name="Run">
/// How long the child has to finish once it has connected. Generous on purpose, and
/// configurable, because an integration test is allowed to take a second or several.
/// </param>
public sealed record TestOptions(TimeSpan Connect, TimeSpan Run)
{
    public static TestOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1000));
}
