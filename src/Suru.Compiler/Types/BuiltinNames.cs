namespace Suru.Compiler.Types;

// Single source of truth for Suru-level built-in names.
// Use these constants instead of inline string literals so that a rename only
// needs one edit here (plus the four suru_*.ll runtime modules for type tags).
internal static class BuiltinNames
{
    // ── Built-in types ────────────────────────────────────────────────────────
    public const string Void    = "void";
    public const string Bool    = "Bool";
    public const string Int32   = "Int32";
    public const string Int64   = "Int64";
    public const string Float64 = "Float64";
    public const string String  = "String";
    public const string Array   = "Array";

    // ── Built-in functions ────────────────────────────────────────────────────
    public const string Main       = "main";
    public const string PrintLn    = "printLn";
    public const string PrintError = "printError";
    public const string Exit       = "exit";
    public const string Clone      = "clone";
    public const string Drop       = "drop";
    public const string ReadFile   = "readFile";
    public const string WriteFile  = "writeFile";
}
