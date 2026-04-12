namespace Suru.Compiler.Lex;

public sealed class Tokens(Lexer lexer, string sourcePath)
{
    private Token _current = lexer.NextToken();
    private readonly List<Token> _buffer = new List<Token>();
    private int _idx;

    public string SourcePath { get; internal set; } = sourcePath;

    internal Token Current()
    {
        return _current;
    }

    private bool BufferHasTokens()
    {
        return _buffer.Count > 0 && _idx < _buffer.Count;
    }

    private Token? NextTokenFromBuffer()
    {
        if (BufferHasTokens())
        {
            var current = _buffer[_idx];
            _idx++;

            if (_idx >= _buffer.Count)
            {
                _idx = 0;
                _buffer.Clear();
            }

            return current;
        }

        return null;
    }

    internal void Next()
    {
        _current = NextTokenFromBuffer() ?? lexer.NextToken();
    }

    internal Token Peek()
    {
        _buffer.Add(_current);
        var peek = lexer.NextToken();
        _buffer.Add(peek);
        return peek;
    }

    internal Token PeekN(int n)
    {
        _buffer.Add(_current);

        Token peek = _current;

        while (n > 0)
        {
            peek = lexer.NextToken();
            _buffer.Add(peek);
            n--;
        }

        return peek;
    }
}