namespace Suru.Tests;

// Verifies the Stage-13a milestone: scope-chain data structures for the Suru semantic analyzer,
// written in Suru itself (tests/fixtures/suru-semantic/main.suru).
//
// ── What the fixture does ────────────────────────────────────────────────────
//
// suru-semantic defines AnalyzerState, Scope, SymbolEntry, FunctionSig, and
// AnalysisError types along with helpers: pushScope, popScope, declareSymbol,
// lookupSymbol, existsInCurrentScope, and addError.
//
// main.suru runs five unit tests and prints "PASS: <name>" or "FAIL: <name>"
// for each, then exits 0.
[Collection("IntegrationIR")]
public class IRSuruSemanticTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("suru-semantic");
    private bool _testPassed;

    [Fact]
    public void Semantic_DataStructures_AllPass()
    {
        var output = Run(_exe);
        var lines  = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var failLines = lines.Where(l => l.StartsWith("FAIL:")).ToArray();
        Assert.True(failLines.Length == 0,
            $"Some scope-chain tests failed:\n{string.Join("\n", failLines)}");

        Assert.Contains(lines, l => l.Contains("PASS: push_pop_roundtrip"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_finds_nearest"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_walks_parent"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_returns_empty"));
        Assert.Contains(lines, l => l.Contains("PASS: add_error"));

        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("suru-semantic"); }
}
