using System.Collections.Concurrent;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<CompiledFixtures> { }

public sealed class CompiledFixtures : IDisposable
{
    private readonly string _buildRoot =
        Path.Combine(Path.GetTempPath(), "suru-tests", Guid.NewGuid().ToString("N"));

    private readonly ConcurrentDictionary<string, string> _executables = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _errors = new();

    public string GetExecutable(string name)
        => _executables.GetOrAdd(name, Compile);

    /// <summary>Compiles a fixture that is expected to fail, returning its errors.</summary>
    public IReadOnlyList<string> GetErrors(string name)
        => _errors.GetOrAdd(name, CompileExpectingFailure);

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
