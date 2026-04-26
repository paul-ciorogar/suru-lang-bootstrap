using Suru.Compiler;

if (args.Length < 2 || args[0] != "build")
{
    Console.Error.WriteLine("Usage: suru build <file.suru>");
    return 1;
}

var sourcePath = Path.GetFullPath(args[1]);
var buildDir = Path.Combine(Path.GetDirectoryName(sourcePath)!, "build");

var compiler = new Compiler(sourcePath);
var result = compiler.CompileIR(buildDir);

if (!result.Success)
{
    foreach (var error in result.Errors)
        Console.Error.WriteLine($"error: {error}");
    return 1;
}

Console.WriteLine($"Built: {result.OutputPath}");
return 0;