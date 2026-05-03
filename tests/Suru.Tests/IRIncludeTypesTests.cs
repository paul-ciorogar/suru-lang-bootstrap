namespace Suru.Tests;

// Verifies that type declarations from an included file are available in the importing module.
//
// lib.suru defines `type Point: { x Int64, y Int64 }` and a function that returns one.
// main.suru includes lib.suru and uses `Point` directly as a type annotation — without redefining
// it — which requires ResolveIncludes to merge TypeDeclaration nodes into the merged module.
[Collection("IntegrationIR")]
public class IRIncludeTypesTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("include-types-test");
    private bool _testPassed;

    [Fact]
    public void IncludeTypes_CrossModuleTypeUsable()
    {
        Assert.Equal("10\n20\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("include-types-test"); }
}
