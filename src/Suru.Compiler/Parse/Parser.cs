using System.Globalization;
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
        var stmts = new List<Statement>();
        while (_tokens.Current().Kind != TokenKind.Eof)
            stmts.Add(ParseStatement());
        _ = Expect(TokenKind.Eof);
        return new Module { SourcePath = _tokens.SourcePath, Statements = stmts };
    }

    private Statement ParseStatement()
    {
        return new ExpressionStatement(ParseExpression());
    }

    private Expression ParseExpression()
    {
        var token = _tokens.Current();
        if (token.Kind == TokenKind.Identifier)
        {
            _tokens.Next();
            _ = Expect(TokenKind.LeftParen);
            var args = ParseArguments();
            _ = Expect(TokenKind.RightParen);
            return new CallExpression(PositionOf(token), token.Text, args);
        }
        return ParsePrimary();
    }

    private List<Expression> ParseArguments()
    {
        var args = new List<Expression>();
        if (_tokens.Current().Kind == TokenKind.RightParen)
            return args;
        args.Add(ParseExpression());
        while (_tokens.Current().Kind == TokenKind.Comma)
        {
            _tokens.Next();
            args.Add(ParseExpression());
        }
        return args;
    }

    private Expression ParsePrimary()
    {
        var token = _tokens.Current();
        _tokens.Next();
        var position = PositionOf(token);
        return token.Kind switch
        {
            TokenKind.True    => new BoolLiteral(position, true),
            TokenKind.False   => new BoolLiteral(position, false),
            TokenKind.IntLiteral  => new IntLiteral(position, long.Parse(token.Text)),
            TokenKind.FloatLiteral => new FloatLiteral(position, double.Parse(token.Text, CultureInfo.InvariantCulture)),
            _ => throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): unexpected token {token.Kind}"),
        };
    }

    private static SourcePosition PositionOf(Token token) => new(token.Line, token.Column);

    private Token Expect(TokenKind kind)
    {
        var token = _tokens.Current();
        if (token.Kind == kind)
        {
            _tokens.Next();
            return token;
        }
        // TODO: the parser should store the error message and try to continue parsing
        // presenting the user with more errors is a good thing
        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected {kind}, got {token.Kind}");
    }
}
