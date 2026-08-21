using System.Collections.Concurrent;
using Suru.Compiler.Testing;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

/// <summary>
/// A fixture put through <c>suru test</c>, with the source as the run left it and as a
/// second run left it — the pair is what proves annotating is idempotent.
/// </summary>
public sealed record FixtureTestRun(TestResult Result, string Source, string SourceAfterRerun);

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<CompiledFixtures> { }

public sealed class CompiledFixtures : IDisposable
{
    private readonly string _buildRoot =
        Path.Combine(Path.GetTempPath(), "suru-tests", Guid.NewGuid().ToString("N"));

    private readonly ConcurrentDictionary<string, string> _executables = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _errors = new();
    private readonly ConcurrentDictionary<string, FixtureTestRun> _testRuns = new();

    public string GetExecutable(string name)
        => _executables.GetOrAdd(name, Compile);

    /// <summary>Compiles a fixture that is expected to fail, returning its errors.</summary>
    public IReadOnlyList<string> GetErrors(string name)
        => _errors.GetOrAdd(name, CompileExpectingFailure);

    /// <summary>
    /// Runs a fixture in test mode. A test run rewrites the source it was given, so the
    /// fixture is copied into the temp build root first and the copy is what gets annotated
    /// — the file under version control is never touched.
    /// </summary>
    public FixtureTestRun GetTestRun(string name)
        => _testRuns.GetOrAdd(name, RunTests);

    public void Dispose()
    {
        if (Directory.Exists(_buildRoot))
            Directory.Delete(_buildRoot, recursive: true);
    }

    private string Compile(string name)
    {
        var sourcePath = FixturePath(name);
        var buildDir = Path.Combine(_buildRoot, name);

        var result = new SuruCompiler(sourcePath).Compile(buildDir);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Fixture '{name}' failed to compile:\n{string.Join("\n", result.Errors)}");

        return result.OutputPath!;
    }

    private IReadOnlyList<string> CompileExpectingFailure(string name)
    {
        var sourcePath = FixturePath(name);
        var buildDir = Path.Combine(_buildRoot, name);

        var result = new SuruCompiler(sourcePath).Compile(buildDir);
        if (result.Success)
            throw new InvalidOperationException(
                $"Fixture '{name}' was expected to fail, but compiled successfully.");

        return result.Errors;
    }

    private FixtureTestRun RunTests(string name)
    {
        var buildDir = Path.Combine(_buildRoot, name + "-test");
        Directory.CreateDirectory(buildDir);

        var sourcePath = Path.Combine(buildDir, "main.suru");
        File.Copy(FixturePath(name), sourcePath);

        var compiler = new SuruCompiler(sourcePath);
        var result = compiler.Test(buildDir);
        if (result.Errors.Count > 0)
            throw new InvalidOperationException(
                $"Fixture '{name}' failed to compile:\n{string.Join("\n", result.Errors)}");

        var annotated = File.ReadAllText(sourcePath);

        // The second run reads back what the first one wrote, which is the only way to find
        // out whether an annotated directive still parses and lands in the same place.
        _ = compiler.Test(buildDir);

        return new FixtureTestRun(result, annotated, File.ReadAllText(sourcePath));
    }

    /// <summary>Locates a fixture's source, for tests that drive the compiler themselves.</summary>
    public static string FixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", name, "main.suru");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            $"Fixture '{name}' not found under any ancestor of {AppContext.BaseDirectory}");
    }
}
