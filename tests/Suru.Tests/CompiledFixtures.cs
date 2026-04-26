using System.Collections.Concurrent;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<CompiledFixtures> { }

// IntegrationIR is kept as an alias so existing [Collection("IntegrationIR")] tests compile
// without change — both collections share the same fixture instance pool via separate types.
[CollectionDefinition("IntegrationIR")]
public class IntegrationIRCollection : ICollectionFixture<CompiledFixturesIR> { }

public sealed class CompiledFixtures : IDisposable
{
    private readonly string _buildRoot =
        Path.Combine(Path.GetTempPath(), "suru-tests", Guid.NewGuid().ToString("N"));

    private readonly ConcurrentDictionary<string, string> _executables = new();

    public string GetExecutable(string name)
        => _executables.GetOrAdd(name, Compile);

    public void Dispose()
    {
        if (Directory.Exists(_buildRoot))
            Directory.Delete(_buildRoot, recursive: true);
    }

    private string Compile(string name)
    {
        var sourcePath = FindFixturePath(name);
        var buildDir = Path.Combine(_buildRoot, name);

        var result = new SuruCompiler(sourcePath).CompileIR(buildDir);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Fixture '{name}' failed to compile:\n{string.Join("\n", result.Errors)}");

        return result.OutputPath!;
    }

    private static string FindFixturePath(string name)
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

public sealed class CompiledFixturesIR : IDisposable
{
    private readonly string _buildRoot =
        Path.Combine(Path.GetTempPath(), "suru-tests-ir", Guid.NewGuid().ToString("N"));

    private readonly ConcurrentDictionary<string, string> _executables = new();

    public string GetExecutable(string name)
        => _executables.GetOrAdd(name, Compile);

    public void Dispose()
    {
        if (Directory.Exists(_buildRoot))
            Directory.Delete(_buildRoot, recursive: true);
    }

    private string Compile(string name)
    {
        var sourcePath = FindFixturePath(name);
        var buildDir = Path.Combine(_buildRoot, name);

        var result = new SuruCompiler(sourcePath).CompileIR(buildDir);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Fixture '{name}' failed IR compile:\n{string.Join("\n", result.Errors)}");

        return result.OutputPath!;
    }

    private static string FindFixturePath(string name)
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
