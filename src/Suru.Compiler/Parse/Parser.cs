using Suru.Compiler.Lex;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Parse;

public sealed class Parser
{
    private readonly Tokens _tokens;

    /// <summary>Numbers the directives that report a value at runtime, in source order.</summary>
    private int _directives;

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
        if (token.Kind == TokenKind.LeftBrace)
            return ParseBlock();
        if (token.Kind == TokenKind.Hash)
            return ParseDirective();
        if (token.Kind == TokenKind.Let)
            return ParseLet();
        // An identifier starts an assignment only when a ':' follows; otherwise it
        // is the start of an expression — a call, or a use of the name.
        if (token.Kind == TokenKind.Identifier && _tokens.Peek().Kind == TokenKind.Colon)
            return ParseAssignment();
        return new ExpressionStatement(ParseExpression());
    }

    /// <summary>
    /// The same loop as <see cref="_Parse"/>, stopping at the closing brace. It stops at
    /// <c>Eof</c> too, so an unterminated block ends as 'expected RightBrace, got Eof'
    /// rather than spinning.
    /// </summary>
    private Statement ParseBlock()
    {
        var open = Expect(TokenKind.LeftBrace);
        var stmts = new List<Statement>();
        while (_tokens.Current().Kind is not TokenKind.RightBrace and not TokenKind.Eof)
            stmts.Add(ParseStatement());
        _ = Expect(TokenKind.RightBrace);
        return new BlockStatement(PositionOf(open), stmts);
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
    /// A <c>#</c> test directive. The word after the <c>#</c> is an ordinary identifier
    /// rather than a keyword, so <c>mock</c>, <c>view</c> and <c>assert</c> remain usable as
    /// names everywhere else in the language.
    /// </summary>
    private Statement ParseDirective()
    {
        var hash = Expect(TokenKind.Hash);
        var name = Expect(TokenKind.Identifier);

        return name.Text switch
        {
            "mock" => ParseMock(hash),
            "view" => ParseView(hash),
            "assert" => ParseAssert(hash),
            _ => throw new ParseException(
                $"{_tokens.SourcePath}({name.Line},{name.Column}): unknown directive " +
                $"'#{name.Text}'; expected '#mock', '#view' or '#assert'"),
        };
    }

    private Statement ParseMock(Token hash)
    {
        var name = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        var value = ParseExpression();
        RequireOneLine(hash);
        return new MockDirective(PositionOf(hash), name.Text, PositionOf(name), value);
    }

    private Statement ParseView(Token hash)
    {
        var subject = ParseExpression();
        RequireOneLine(hash);
        SkipAnnotation(hash);
        return new ViewDirective(PositionOf(hash), _directives++, subject);
    }

    private Statement ParseAssert(Token hash)
    {
        _ = Expect(TokenKind.LeftParen);
        var args = ParseArguments();
        _ = Expect(TokenKind.RightParen);
        RequireOneLine(hash);

        if (args.Count != 2)
            throw new ParseException(
                $"{_tokens.SourcePath}({hash.Line},{hash.Column}): " +
                $"'#assert' expects 2 arguments, got {args.Count}");

        SkipAnnotation(hash);
        return new AssertDirective(PositionOf(hash), _directives++, args[0], args[1]);
    }

    /// <summary>
    /// Discards the compiler-written annotation that follows a <c>#view</c> or
    /// <c>#assert</c>, so the directive survives being read back after a test run wrote its
    /// result into the line. Everything after the colon belongs to the compiler and is
    /// thrown away without being lexed — see <see cref="Tokens.SkipRestOfLine"/> for why it
    /// cannot simply be parsed and ignored.
    /// </summary>
    private void SkipAnnotation(Token hash)
    {
        var next = _tokens.Current();
        if (next.Kind == TokenKind.Colon && next.Line == hash.Line)
            _tokens.SkipRestOfLine();
    }

    /// <summary>
    /// Holds a directive to the line its <c>#</c> is on. Ordinary statements may run over
    /// several lines, but a directive is a comment as far as a production build is
    /// concerned, and a comment stops at the newline.
    /// </summary>
    private void RequireOneLine(Token hash)
    {
        // TODO: add multy line directives ex:
        // #mock val
        // # + 1
        // # * 3
        // or in the case of a view that will print a lot of text adding a new line with # would continue the print ex:
        // #view largeTextVal: "some really large text
        // # that continues on this line also"
        var last = _tokens.LastConsumed!;
        if (last.Line != hash.Line)
            throw new ParseException(
                $"{_tokens.SourcePath}({last.Line},{last.Column}): " +
                $"a directive must be written on one line; '#' is on line {hash.Line}");
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
