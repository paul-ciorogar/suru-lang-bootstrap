namespace Suru.Tests;

[Collection("Integration")]
public class ComparisonTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("comparisons");

    [Fact]
    public void Comparisons_PrintsExpectedOutput()
        // lt: 3<5=true, 5<3=false
        // gt: 5>3=true, 3>5=false
        // lte: 5<=5=true, 4<=5=true, 6<=5=false
        // gte: 5>=5=true, 6>=5=true, 4>=5=false
        // ord: 'A'=65, '0'=48
        => Assert.Equal("true\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\nfalse\n65\n48\n", Run(_exe));
}
