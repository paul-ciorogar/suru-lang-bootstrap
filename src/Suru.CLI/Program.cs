using Suru.Compiler;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;

// ─── Argument parsing ─────────────────────────────────────────────────────────

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var command = args[0];

// Commands that operate on a source file require exactly one file argument.
if (command is "build" or "lex" or "parse" or "ir")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"error: '{command}' requires a file argument");
        Console.Error.WriteLine($"Usage: suru {command} <file.suru>");
        return 1;
    }
}

var sourcePath = args.Length >= 2 ? Path.GetFullPath(args[1]) : string.Empty;

return command switch
{
    "build" => RunBuild(sourcePath),
    "lex"   => RunLex(sourcePath),
    "parse" => RunParse(sourcePath),
    "ir"    => RunIr(sourcePath),
    _       => UnknownCommand(command),
};

// ─── build ────────────────────────────────────────────────────────────────────

// Runs the full pipeline and writes a native executable next to the source file.
static int RunBuild(string sourcePath)
{
    var buildDir = Path.Combine(Path.GetDirectoryName(sourcePath)!, "build");
    var result = new Compiler(sourcePath).CompileIR(buildDir);
    if (!result.Success)
    {
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        return 1;
    }
    Console.WriteLine($"Built: {result.OutputPath}");
    return 0;
}

// ─── lex ──────────────────────────────────────────────────────────────────────

// Tokenises the source file and prints one token per line:
//   <line>:<col>  <Kind>  <text>
// Useful for checking that the lexer recognises all tokens in a source file.
static int RunLex(string sourcePath)
{
    var result = new Compiler(sourcePath).LexFile();
    if (!result.Success)
    {
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    foreach (var token in result.Value!)
    {
        // Right-align line, left-align column; pad the kind name to 16 chars so
        // columns are visually scannable even for long kind names.
        var loc  = $"{token.Line,4}:{token.Column,-3}";
        var kind = token.Kind.ToString().PadRight(16);
        var text = string.IsNullOrEmpty(token.Text) ? string.Empty : $"  {token.Text}";
        Console.WriteLine($"{loc}  {kind}{text}");
    }
    return 0;
}

// ─── parse ────────────────────────────────────────────────────────────────────

// Parses the source file (including include resolution) and prints the AST as an
// indented tree. Each node kind appears on its own line; child nodes are indented
// by two spaces. Leaf values (names, literals) are enclosed in [square brackets].
static int RunParse(string sourcePath)
{
    var result = new Compiler(sourcePath).ParseFile();
    if (!result.Success)
    {
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    Console.Write(AstPrinter.Print(result.Value!));
    return 0;
}

// ─── ir ───────────────────────────────────────────────────────────────────────

// Runs lex → parse → semantic analysis → IR codegen and prints the LLVM IR text
// to stdout. No files are written and clang is not invoked. Useful for inspecting
// or diffing generated IR without triggering a full build.
static int RunIr(string sourcePath)
{
    var result = new Compiler(sourcePath).GenerateIr();
    if (!result.Success)
    {
        foreach (var error in result.Errors)
            Console.Error.WriteLine($"error: {error}");
        return 1;
    }

    Console.Write(result.Value);
    return 0;
}

// ─── helpers ──────────────────────────────────────────────────────────────────

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: suru <command> <file.suru>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Commands:");
    Console.Error.WriteLine("  build <file>   Compile to a native executable (written to build/ next to the source)");
    Console.Error.WriteLine("  lex   <file>   Print all tokens produced by the lexer, one per line");
    Console.Error.WriteLine("  parse <file>   Print the parsed AST as an indented tree (includes are expanded)");
    Console.Error.WriteLine("  ir    <file>   Print the generated LLVM IR without invoking clang");
}
