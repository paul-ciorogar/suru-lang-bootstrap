namespace Suru.Compiler.Lex;


internal sealed class Tokens(Lexer lexer)
{
    private Token _current = lexer.NextToken();
    private readonly Queue<Token> _lookahead = new();

    internal string SourcePath => lexer.SourcePath;

    /// <summary>
    /// The token most recently moved past, or <c>null</c> before the first
    /// <see cref="Next"/>. It is what a caller checks to find out where the construct it
    /// just parsed actually ended — the parser uses it to hold a test directive to one line.
    /// </summary>
    internal Token? LastConsumed { get; private set; }

    internal Token Current()
    {
        return _current;
    }

    internal void Next()
    {
        LastConsumed = _current;
        _current = _lookahead.Count > 0 ? _lookahead.Dequeue() : lexer.NextToken();
    }

    internal Token Peek()
    {
        return PeekN(1);
    }

    /// <summary>Token <paramref name="n"/> positions past <see cref="Current"/>; n = 0 is the current token.</summary>
    internal Token PeekN(int n)
    {
        if (n <= 0)
            return _current;

        while (_lookahead.Count < n)
            _lookahead.Enqueue(lexer.NextToken());

        return _lookahead.ElementAt(n - 1);
    }

    /// <summary>
    /// Discards the source text from the cursor to the end of the line without lexing it,
    /// then loads the first token of the next line.
    /// <para>
    /// This exists for the compiler-written annotation on a <c>#view</c> or <c>#assert</c>
    /// line, which is output rather than source and need not be a legal token at all:
    /// <c>fail, got 2</c> is not an expression, and a large <c>f64</c> prints as
    /// <c>1e+20</c>, which the literal scanner rejects. Discarding is only possible while
    /// nothing has been buffered past the cursor, since a buffered token has already been
    /// lexed — an invariant of the call sites, not something a program can violate.
    /// </para>
    /// </summary>
    internal void SkipRestOfLine()
    {
        if (_lookahead.Count > 0)
            throw new InvalidOperationException(
                "cannot discard a line that has already been lexed into lookahead");

        lexer.SkipRestOfLine();
        Next();
    }
}
