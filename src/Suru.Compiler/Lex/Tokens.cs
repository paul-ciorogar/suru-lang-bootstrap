namespace Suru.Compiler.Lex;

// Single-token lookahead wrapper around Lexer with an optional multi-token replay buffer.
//
// Normal flow: Current() returns the current token; Advance() pulls the next one from the lexer.
//
// Lookahead flow: Peek()/PeekN(n) snapshot the current token plus n future tokens into _buffer,
// then return the last peeked token. The next Advance() replays from _buffer instead of calling
// the lexer again, draining the buffer in FIFO order. Once drained, _buffer is cleared and
// Advance() falls back to the lexer directly. This means the parser can look ahead without
// consuming tokens and without losing its place.
public sealed class Tokens(Lexer lexer, string sourcePath)
{
    private Token _current = lexer.NextToken();
    private readonly List<Token> _buffer = new List<Token>();
    private int _idx;

    public string SourcePath { get; internal set; } = sourcePath;

    internal Token Current() => _current;

    internal bool CurrentIs(TokenKind kind) => _current.Kind == kind;

    private bool BufferHasTokens() => _buffer.Count > 0 && _idx < _buffer.Count;

    private Token? NextTokenFromBuffer()
    {
        if (!BufferHasTokens()) return null;
        var current = _buffer[_idx++];
        if (_idx >= _buffer.Count) { _idx = 0; _buffer.Clear(); }
        return current;
    }

    internal void Advance() => _current = NextTokenFromBuffer() ?? lexer.NextToken();

    // Snapshots _current + 1 future token into the replay buffer; returns the future token.
    internal Token Peek()
    {
        _buffer.Add(_current);
        var peek = lexer.NextToken();
        _buffer.Add(peek);
        return peek;
    }

    // Snapshots _current + n future tokens; returns the nth future token.
    internal Token PeekN(int n)
    {
        _buffer.Add(_current);
        Token peek = _current;
        while (n-- > 0) { peek = lexer.NextToken(); _buffer.Add(peek); }
        return peek;
    }
}