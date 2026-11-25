namespace Suru.Compiler.Lex;


internal sealed class Tokens(Lexer lexer)
{
    private Token _current = lexer.NextToken();
    private readonly Queue<Token> _lookahead = new();

    internal string SourcePath => lexer.SourcePath;

    /// <summary>
    /// The token most recently moved past, or <c>null</c> before the first <see cref="Next"/>:
    /// where the construct just parsed actually ended, which is what holds a test directive to
    /// one line.
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
    /// Consumes the token at the cursor and the rest of its line, without lexing the rest.
    /// <para>
    /// A directive needs it: the text after its terminator is output, not source —
    /// <c>fail, got 2</c> is not an expression, and a large <c>f64</c> prints as <c>1e+20</c>,
    /// which the literal scanner rejects — so it must never be lexed at all.
    /// </para>
    /// <para>
    /// Impossible once anything is buffered past the cursor, since that has already been lexed.
    /// That is an invariant of the parser's call sites, not something a program can violate.
    /// </para>
    /// </summary>
    internal void DiscardRestOfLine()
    {
        if (_lookahead.Count > 0)
            throw new InvalidOperationException(
                "cannot discard text that has already been lexed into lookahead");

        lexer.SkipRestOfLine();
        Next();
    }
}
