namespace Suru.Tests;

[Collection("Integration")]
public class FileIoTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _readExe  = fixtures.GetExecutable("file_io");
    private readonly string _writeExe = fixtures.GetExecutable("file_io_write");
    private readonly string _exitExe  = fixtures.GetExecutable("exit_test");
    private bool _testPassed;

    [Fact]
    public void ReadFile_EchoesFileContent()
    {
        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, "hello from file");
            Assert.Equal("hello from file\n", Run(_readExe, tmpPath));
        }
        finally
        {
            File.Delete(tmpPath);
        }
        _testPassed = true;
    }

    [Fact]
    public void WriteFile_CopiesContent()
    {
        var inPath  = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(inPath, "suru stage 7");
            Run(_writeExe, inPath, outPath);
            Assert.Equal("suru stage 7", File.ReadAllText(outPath));
        }
        finally
        {
            File.Delete(inPath);
            File.Delete(outPath);
        }
        _testPassed = true;
    }

    [Fact]
    public void Exit_ReturnsCode()
    {
        Assert.Equal(42, RunGetExitCode(_exitExe));
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
        {
            fixtures.RecordFailure("file_io");
            fixtures.RecordFailure("file_io_write");
            fixtures.RecordFailure("exit_test");
        }
    }
}
