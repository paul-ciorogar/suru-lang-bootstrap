namespace Suru.Compiler;

/// <summary>
/// Tracks which source files have been visited during include resolution,
/// distinguishing two different states that require different responses:
///
///   Active   — the file is currently on the recursive call stack.
///              Encountering it again means a circular include (A → B → A).
///
///   Resolved — the file was fully processed by a previous call.
///              Encountering it again means a diamond include (B and C both
///              include D); the content should be skipped but the namespace
///              alias must still be registered.
///
/// Enter/Exit must be called symmetrically around each recursive Resolve call.
/// </summary>
internal sealed class IncludeGraph
{
    // All files that have been entered (whether still active or fully resolved).
    // Initialized with the root source path so the root cannot include itself.
    private readonly HashSet<string> _resolved;

    // Files whose recursive resolution is currently on the call stack.
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    internal IncludeGraph(string rootSourcePath)
    {
        _resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootSourcePath };
    }

    /// <summary>True if <paramref name="absolutePath"/> is currently being resolved
    /// somewhere above us on the call stack — a circular include.</summary>
    internal bool IsActive(string absolutePath) => _active.Contains(absolutePath);

    /// <summary>True if <paramref name="absolutePath"/> was already fully resolved
    /// by an earlier branch — a diamond include.</summary>
    internal bool IsResolved(string absolutePath) => _resolved.Contains(absolutePath);

    /// <summary>
    /// Marks <paramref name="absolutePath"/> as active (resolution in progress).
    /// Must be paired with a corresponding <see cref="Exit"/> call.
    /// </summary>
    internal void Enter(string absolutePath)
    {
        _active.Add(absolutePath);
        _resolved.Add(absolutePath);
    }

    /// <summary>Removes <paramref name="absolutePath"/> from the active set,
    /// leaving it in the resolved set.</summary>
    internal void Exit(string absolutePath) => _active.Remove(absolutePath);
}
