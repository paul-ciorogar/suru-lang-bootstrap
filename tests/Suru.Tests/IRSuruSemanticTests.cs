namespace Suru.Tests;

// Verifies Stage-13a and Stage-13b milestones for the Suru semantic analyzer
// written in Suru itself (tests/fixtures/suru-semantic/).
//
// Stage 13a (suru-semantic.suru): scope-chain data structures — AnalyzerState,
// Scope, SymbolEntry, FunctionSig, AnalysisError, and helpers pushScope/popScope/
// declareSymbol/lookupSymbol/existsInCurrentScope/addError.
//
// Stage 13b (suru-semantic-passes.suru): declaration pre-passes — resolveTypeName,
// collectTypeDeclarations (pass 1), collectFunctionDeclarations (pass 2), runPrePasses.
//
// main.suru runs all unit tests and prints "PASS: <name>" or "FAIL: <name>" for each.
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
            $"Some semantic tests failed:\n{string.Join("\n", failLines)}");

        // Stage 13a — scope-chain helpers
        Assert.Contains(lines, l => l.Contains("PASS: push_pop_roundtrip"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_finds_nearest"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_walks_parent"));
        Assert.Contains(lines, l => l.Contains("PASS: lookup_returns_empty"));
        Assert.Contains(lines, l => l.Contains("PASS: add_error"));

        // Stage 13b — declaration pre-passes
        Assert.Contains(lines, l => l.Contains("PASS: resolveTypeName_builtin"));
        Assert.Contains(lines, l => l.Contains("PASS: resolveTypeName_user_declared"));
        Assert.Contains(lines, l => l.Contains("PASS: collectTypeDecls_registers"));
        Assert.Contains(lines, l => l.Contains("PASS: collectTypeDecls_duplicate"));
        Assert.Contains(lines, l => l.Contains("PASS: collectFnDecls_registers"));
        Assert.Contains(lines, l => l.Contains("PASS: collectFnDecls_duplicate"));
        Assert.Contains(lines, l => l.Contains("PASS: collectFnDecls_unknown_param"));
        Assert.Contains(lines, l => l.Contains("PASS: runPrePasses_cross_pass"));

        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("suru-semantic"); }
}
