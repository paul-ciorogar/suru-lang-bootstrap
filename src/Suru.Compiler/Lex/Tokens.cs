namespace Suru.Compiler.Lex;


internal sealed class Tokens(Lexer lexer)
{
    private Token _current = lexer.NextToken();
    private readonly Queue<Token> _lookahead = new();

    internal string SourcePath => lexer.SourcePath;

    internal Token Current()
    {
        return _current;
    }

    internal void Next()
    {
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
}
