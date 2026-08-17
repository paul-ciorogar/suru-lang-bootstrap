namespace Suru.Compiler;

/// <summary>
/// A resolved type. 
/// </summary>
public sealed record SuruType(string Name)
{
    public static readonly SuruType Bool = new("bool");
    public static readonly SuruType I64 = new("i64");
    public static readonly SuruType F64 = new("f64");

    /// <summary>The type of an expression that yields no value, such as a `printLn` call.</summary>
    public static readonly SuruType Void = new("void");

    public override string ToString() => Name;
}
