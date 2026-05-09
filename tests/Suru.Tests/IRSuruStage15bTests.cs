namespace Suru.Tests;

// Stage 15b — Sum Type Context & Registration.
//
// Compiles and runs the sum-type-context-test fixture, which exercises
// addHSumType / lookupSumType / variantIdx / isVariant directly against a
// manually built HeapCodegenContext.  Every test case prints "PASS: ..." or
// "FAIL: ..." so a single assertion on the output catches all regressions.
[Collection("IntegrationIR")]
public class IRSuruStage15bTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("sum-type-context-test");
    private bool _testPassed;

    [Fact]
    public void SumTypeContextHelpersWork()
    {
        var output = Run(_exe);
        Assert.DoesNotContain("FAIL:", output);
        Assert.Contains("PASS: lookupSumType finds Shape",         output);
        Assert.Contains("PASS: Shape has 2 variants",              output);
        Assert.Contains("PASS: lookupSumType missing returns empty", output);
        Assert.Contains("PASS: variantIdx Circle=0",               output);
        Assert.Contains("PASS: variantIdx Square=1",               output);
        Assert.Contains("PASS: variantIdx unknown=-1",             output);
        Assert.Contains("PASS: isVariant Circle=true",             output);
        Assert.Contains("PASS: isVariant Square=true",             output);
        Assert.Contains("PASS: isVariant Triangle=false",          output);
        Assert.Contains("PASS: isVariant Shape=false",             output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("sum-type-context-test");
    }
}
