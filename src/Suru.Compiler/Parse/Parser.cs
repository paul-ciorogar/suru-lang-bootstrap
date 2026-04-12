using Suru.Compiler.Lex;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Parse;

public sealed class Parser
{
    private readonly Tokens _tokens;

    private Parser(Tokens tokens)
    {
        _tokens = tokens;
    }

    public static Module Parse(Tokens tokens)
    {
        var parser = new Parser(tokens);
        return parser._Parse();
    }

    private Module _Parse()
    {
        _ = Expect(TokenKind.Eof);
        return new Module { SourcePath = _tokens.SourcePath };
    }

    private Token Expect(TokenKind kind)
    {
        var token = _tokens.Current();

        if (token.Kind == kind)
        {
            _tokens.Next();
            return token;
        }

        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected {kind}, got {token.Kind}");
    }
}
