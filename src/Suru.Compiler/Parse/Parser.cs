using Suru.Compiler.Lex;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Parse;

public sealed class Parser
{
    private readonly Tokens _tokens;

    private Parser(Lexer lexer)
    {
        _tokens = new Tokens(lexer);
    }

    public static Module Parse(Lexer lexer)
    {
        var parser = new Parser(lexer);
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
        var token = _tokens.Current();
        if (token.Kind == TokenKind.Let)
            return ParseLet();
        // An identifier starts an assignment only when a ':' follows; otherwise it
        // is the start of an expression — a call, or a use of the name.
        if (token.Kind == TokenKind.Identifier && _tokens.Peek().Kind == TokenKind.Colon)
            return ParseAssignment();
        return new ExpressionStatement(ParseExpression());
    }

    private Statement ParseLet()
    {
        var let = Expect(TokenKind.Let);
        var name = Expect(TokenKind.Identifier);
        var type = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        return new LetStatement(
            PositionOf(let), name.Text, type.Text, PositionOf(type), ParseExpression());
    }

    private Statement ParseAssignment()
    {
        var name = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        return new AssignmentStatement(PositionOf(name), name.Text, ParseExpression());
    }

    /// <summary>
    /// Operands folded left to right with no precedence — Suru has none, so this is
    /// one loop rather than a precedence table. It is also the whole line-continuation
    /// rule: an expression keeps going while the next token is a binary operator, and
    /// since no statement can begin with one, the following line joins exactly when it
    /// starts with an operator.
    /// </summary>
    private Expression ParseExpression()
    {
        var left = ParseUnary();
        while (BinaryOperatorOf(_tokens.Current().Kind) is { } op)
        {
            _tokens.Next();
            left = new BinaryExpression(left.Position, op, left, ParseUnary());
        }
        return left;
    }

    private Expression ParseUnary()
    {
        var token = _tokens.Current();

        // A '-' straight in front of a number is part of the literal, not an operator on
        // it: '-1' is the literal -1. Only here, in prefix position — the '-' of '1 - 2'
        // is consumed as a binary operator before this is ever reached. It is also the
        // only way to write the i64 minimum, whose magnitude no positive literal may have.
        if (token.Kind == TokenKind.Minus && IsNumber(_tokens.Peek().Kind))
        {
            _tokens.Next();
            return NumberLiteral(_tokens.Current(), PositionOf(token), negated: true);
        }

        var op = token.Kind switch
        {
            TokenKind.Minus => (UnaryOperator?)UnaryOperator.Negate,
            TokenKind.Not => UnaryOperator.Not,
            _ => null,
        };
        if (op is null)
            return ParsePrimary();

        _tokens.Next();
        return new UnaryExpression(PositionOf(token), op.Value, ParseUnary());
    }

    private static bool IsNumber(TokenKind kind) =>
        kind is TokenKind.IntLiteral or TokenKind.FloatLiteral;

    /// <summary>
    /// Builds the literal for a number token, applying a folded-in sign. The lexer decodes
    /// an unsigned magnitude, so the range check lands here: 9223372036854775808 is the
    /// <c>i64</c> minimum when negated and out of range when not.
    /// </summary>
    private Expression NumberLiteral(Token token, SourcePosition position, bool negated)
    {
        _tokens.Next();

        if (token.Kind == TokenKind.FloatLiteral)
            return new FloatLiteral(position, negated ? -token.FloatValue : token.FloatValue);

        ulong magnitude = token.IntMagnitude;
        if (negated)
            return new IntLiteral(
                position,
                magnitude == (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude);

        if (magnitude > long.MaxValue)
            throw new ParseException(
                $"{_tokens.SourcePath}({token.Line},{token.Column}): integer literal is out of range for 'i64'");

        return new IntLiteral(position, (long)magnitude);
    }

    private static BinaryOperator? BinaryOperatorOf(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BinaryOperator.Add,
        TokenKind.Minus => BinaryOperator.Subtract,
        TokenKind.Star => BinaryOperator.Multiply,
        TokenKind.Slash => BinaryOperator.Divide,
        TokenKind.Percent => BinaryOperator.Remainder,
        TokenKind.Equal => BinaryOperator.Equal,
        TokenKind.NotEqual => BinaryOperator.NotEqual,
        TokenKind.Less => BinaryOperator.Less,
        TokenKind.LessOrEqual => BinaryOperator.LessOrEqual,
        TokenKind.Greater => BinaryOperator.Greater,
        TokenKind.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
        TokenKind.And => BinaryOperator.And,
        TokenKind.Or => BinaryOperator.Or,
        _ => null,
    };

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

        if (token.Kind == TokenKind.LeftParen)
        {
            _tokens.Next();
            var grouped = ParseExpression();
            _ = Expect(TokenKind.RightParen);
            return grouped;
        }

        if (token.Kind == TokenKind.Identifier)
        {
            _tokens.Next();
            if (_tokens.Current().Kind != TokenKind.LeftParen)
                return new IdentifierExpression(PositionOf(token), token.Text);

            _tokens.Next();
            var args = ParseArguments();
            _ = Expect(TokenKind.RightParen);
            return new CallExpression(PositionOf(token), token.Text, args);
        }

        // The lexer decodes a number, since only it knows the base it was written in.
        if (IsNumber(token.Kind))
            return NumberLiteral(token, PositionOf(token), negated: false);

        _tokens.Next();
        var position = PositionOf(token);
        return token.Kind switch
        {
            TokenKind.True  => new BoolLiteral(position, true),
            TokenKind.False => new BoolLiteral(position, false),
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
