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
        if (token.Kind == TokenKind.If)
            return ParseIf();
        if (token.Kind == TokenKind.While)
            return ParseWhile();
        if (token.Kind == TokenKind.Fn)
            return ParseFunction();
        if (token.Kind == TokenKind.Return)
            return ParseReturn();
        if (token.Kind == TokenKind.Break)
            return new BreakStatement(PositionOf(Expect(TokenKind.Break)));
        if (token.Kind == TokenKind.Continue)
            return new ContinueStatement(PositionOf(Expect(TokenKind.Continue)));
        // Without a ':' after it, an identifier starts an expression: a call or a use of the name.
        if (token.Kind == TokenKind.Identifier && _tokens.Peek().Kind == TokenKind.Colon)
            return ParseAssignment();
        return new ExpressionStatement(ParseExpression());
    }

    /// <summary>
    /// Stops at <c>Eof</c> as well as <c>}</c>, so an unterminated block reports
    /// 'expected RightBrace, got Eof' rather than spinning.
    /// </summary>
    /// <remarks>
    /// The kind is decided here rather than re-derived downstream: the parser is the one stage
    /// that knows why it is building a block. See the design note on <see cref="ScopeStack{T, S}"/>.
    /// </remarks>
    private BlockStatement ParseBlock(ScopeKind kind = ScopeKind.Plain)
    {
        var open = Expect(TokenKind.LeftBrace);
        var stmts = new List<Statement>();
        while (_tokens.Current().Kind is not TokenKind.RightBrace and not TokenKind.Eof)
            stmts.Add(ParseStatement());
        _ = Expect(TokenKind.RightBrace);
        return new BlockStatement(PositionOf(open), stmts, kind);
    }

    /// <summary>
    /// <c>if &lt;condition&gt; { ... }</c> with an optional <c>else</c>. Arms are always braced,
    /// so there is no dangling <c>else</c> to have a rule about.
    /// <para>
    /// <c>else if</c> is an <c>else</c> whose body is another <c>if</c>, not a form of its own.
    /// The condition needs no terminator: <c>{</c> is not a binary operator, so
    /// <see cref="ParseExpression"/> stops at it.
    /// </para>
    /// <para>
    /// Never uses <see cref="Tokens.Peek"/>: a directive inside an arm relies on
    /// <see cref="Tokens.DiscardRestOfLine"/>, which refuses to run with anything buffered.
    /// </para>
    /// </summary>
    private IfStatement ParseIf()
    {
        var keyword = Expect(TokenKind.If);
        var condition = ParseExpression();
        var then = ParseBlock(ScopeKind.Branch);

        Statement? otherwise = null;
        if (_tokens.Current().Kind == TokenKind.Else)
        {
            _tokens.Next();
            otherwise = _tokens.Current().Kind == TokenKind.If
                ? ParseIf()
                : ParseBlock(ScopeKind.Branch);
        }

        return new IfStatement(PositionOf(keyword), condition, then, otherwise);
    }

    /// <summary>
    /// <c>while &lt;condition&gt; { ... }</c>. An <see cref="ParseIf"/> with no second arm, for
    /// the same reasons: braced body, unterminated condition, and no <see cref="Tokens.Peek"/>
    /// so a directive in the body can still <see cref="Tokens.DiscardRestOfLine"/>.
    /// </summary>
    private WhileStatement ParseWhile()
    {
        var keyword = Expect(TokenKind.While);
        var condition = ParseExpression();
        // The scope a 'break' or 'continue' searches outward for.
        var body = ParseBlock(ScopeKind.Loop);
        return new WhileStatement(PositionOf(keyword), condition, body);
    }

    /// <summary>
    /// <c>fn &lt;name&gt;(&lt;parameters&gt;) &lt;return type&gt; { ... }</c>. The return type
    /// is always written and is a plain identifier, <c>void</c> included, so where <c>void</c>
    /// may appear is the analyzer's call — as is where a declaration may appear, since this is
    /// reachable from any statement.
    /// <para>
    /// Only <see cref="Expect"/> and <see cref="Tokens.Current"/>, never <see cref="Tokens.Peek"/>:
    /// a directive as the body's first statement relies on <see cref="Tokens.DiscardRestOfLine"/>,
    /// which refuses to run with anything buffered.
    /// </para>
    /// </summary>
    private FunctionDeclaration ParseFunction()
    {
        var keyword = Expect(TokenKind.Fn);
        var name = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.LeftParen);
        var parameters = ParseParameters();
        _ = Expect(TokenKind.RightParen);
        var returnType = Expect(TokenKind.Identifier);
        // The barrier that hides enclosing variables and stops a 'break' finding an outer loop.
        var body = ParseBlock(ScopeKind.Function);
        return new FunctionDeclaration(
            PositionOf(keyword), name.Text, parameters, returnType.Text, PositionOf(returnType), body);
    }

    /// <summary>
    /// Comma-separated, zero allowed, no trailing comma. No <see cref="Tokens.Peek"/>, for the
    /// reason <see cref="ParseFunction"/> gives.
    /// </summary>
    private List<Parameter> ParseParameters()
    {
        var parameters = new List<Parameter>();
        if (_tokens.Current().Kind == TokenKind.RightParen)
            return parameters;
        parameters.Add(ParseParameter());
        while (_tokens.Current().Kind == TokenKind.Comma)
        {
            _tokens.Next();
            parameters.Add(ParseParameter());
        }
        return parameters;
    }

    private Parameter ParseParameter()
    {
        var name = Expect(TokenKind.Identifier);
        var type = Expect(TokenKind.Identifier);
        return new Parameter(PositionOf(name), name.Text, type.Text, PositionOf(type));
    }

    /// <summary>
    /// <c>return</c>, optionally followed by a value. A <c>return</c> outside a function parses;
    /// the analyzer reports it.
    /// <para>
    /// No <see cref="Tokens.Peek"/>, for the reason <see cref="ParseFunction"/> gives: a
    /// directive on the line after a bare <c>return</c> must still find nothing buffered.
    /// </para>
    /// </summary>
    private Statement ParseReturn()
    {
        var keyword = Expect(TokenKind.Return);
        // A value belongs to this 'return' only if it starts on its line — the one place a
        // newline is significant, since otherwise the next statement would silently become the
        // returned value. Once started, the value continues across lines like any expression.
        var next = _tokens.Current();
        var value = next.Line == keyword.Line && StartsAnExpression(next.Kind)
            ? ParseExpression()
            : null;
        return new ReturnStatement(PositionOf(keyword), value);
    }

    /// <summary>The tokens <see cref="ParseUnary"/> and <see cref="ParsePrimary"/> accept first.</summary>
    private static bool StartsAnExpression(TokenKind kind) => kind is
        TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.FloatLiteral or
        TokenKind.True or TokenKind.False or TokenKind.LeftParen or
        TokenKind.Minus or TokenKind.Not;

    private Statement ParseLet()
    {
        var let = Expect(TokenKind.Let);
        var name = Expect(TokenKind.Identifier);
        var type = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        return new LetStatement(
            PositionOf(let), name.Text, PositionOf(name), type.Text, PositionOf(type), ParseExpression());
    }

    private Statement ParseAssignment()
    {
        var name = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        return new AssignmentStatement(PositionOf(name), name.Text, ParseExpression());
    }

    /// <summary>
    /// A <c>#</c> test directive. Its name is an identifier, not a keyword, so <c>mock</c>,
    /// <c>view</c> and <c>assert</c> stay usable as names everywhere else.
    /// <para>
    /// A directive owns the rest of its line: it ends at its terminator (the <c>:</c> of a
    /// <c>#view</c>, the <c>)</c> of a <c>#assert</c>, the value of a <c>#mock</c>), and the two
    /// that carry an annotation discard the rest of the line unlexed, since a run writes text
    /// there that no lexer can accept.
    /// </para>
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

    /// <summary>
    /// <c>#mock &lt;name&gt;: &lt;value&gt;</c>. The colon is an assignment's, not an
    /// annotation's: a mock reports nothing and is never written back to, so anything after its
    /// value is a mistake rather than text to discard.
    /// </summary>
    private Statement ParseMock(Token hash)
    {
        var name = Expect(TokenKind.Identifier);
        _ = Expect(TokenKind.Colon);
        var value = ParseExpression();
        RequireOneLine(hash, _tokens.LastConsumed!);
        RequireNothingElseOnTheLine(hash);
        return new MockDirective(PositionOf(hash), name.Text, PositionOf(name), value);
    }

    /// <summary>
    /// <c>#view &lt;expression&gt;:</c>. The colon is required: it is where a run writes the
    /// value, so without it there is nowhere to report to.
    /// </summary>
    private Statement ParseView(Token hash)
    {
        var subject = ParseExpression();
        RequireOneLine(hash, _tokens.LastConsumed!);

        var colon = _tokens.Current();
        if (colon.Kind != TokenKind.Colon || colon.Line != hash.Line)
            throw new ParseException(
                $"{_tokens.SourcePath}({colon.Line},{colon.Column}): expected {TokenKind.Colon}, got {colon.Kind}");

        _tokens.DiscardRestOfLine();
        return new ViewDirective(PositionOf(hash), _directives++, subject);
    }

    /// <summary>
    /// <c>#assert(&lt;actual&gt;, &lt;expected&gt;)</c>.
    /// <para>
    /// The <c>)</c> is not consumed with <see cref="Expect"/>: that would lex the token after
    /// it, and the annotation a run writes there is text no lexer can accept.
    /// </para>
    /// </summary>
    private Statement ParseAssert(Token hash)
    {
        _ = Expect(TokenKind.LeftParen);
        var args = ParseArguments();

        var close = _tokens.Current();
        if (close.Kind != TokenKind.RightParen)
            throw new ParseException(
                $"{_tokens.SourcePath}({close.Line},{close.Column}): expected {TokenKind.RightParen}, got {close.Kind}");
        RequireOneLine(hash, close);

        if (args.Count != 2)
            throw new ParseException(
                $"{_tokens.SourcePath}({hash.Line},{hash.Column}): " +
                $"'#assert' expects 2 arguments, got {args.Count}");

        _tokens.DiscardRestOfLine();
        return new AssertDirective(PositionOf(hash), _directives++, args[0], args[1]);
    }

    /// <summary>
    /// Only <c>#mock</c> needs this: the other two discard the rest of their line.
    /// </summary>
    private void RequireNothingElseOnTheLine(Token hash)
    {
        var next = _tokens.Current();
        if (next.Kind != TokenKind.Eof && next.Line == hash.Line)
            throw new ParseException(
                $"{_tokens.SourcePath}({next.Line},{next.Column}): " +
                "a directive ends at the end of its line");
    }

    /// <summary>
    /// Holds a directive to the line its <c>#</c> is on: to a production build a directive is a
    /// comment, and a comment stops at the newline.
    /// <para>
    /// <paramref name="last"/> is the directive's final token — for a <c>#assert</c>, the
    /// <c>)</c> still at the cursor.
    /// </para>
    /// </summary>
    private void RequireOneLine(Token hash, Token last)
    {
        // TODO: multi-line directives, continued by a line starting with '#':
        // #mock val
        // # + 1
        // # * 3
        // and likewise a long #view annotation:
        // #view largeTextVal: "some really large text
        // # that continues on this line also"
        if (last.Line != hash.Line)
            throw new ParseException(
                $"{_tokens.SourcePath}({last.Line},{last.Column}): " +
                $"a directive must be written on one line; '#' is on line {hash.Line}");
    }

    /// <summary>
    /// Folds left to right: Suru has no precedence. This is also the whole line-continuation
    /// rule — no statement begins with a binary operator, so a line joins the previous
    /// expression exactly when it starts with one.
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

        // A prefix '-' on a number is part of the literal, not a negation: it is the only way
        // to write the i64 minimum, whose magnitude no positive literal may have.
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
    /// The range check lives here, not in the lexer, because only the sign decides it:
    /// 9223372036854775808 is the <c>i64</c> minimum when negated and out of range when not.
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

        // Already decoded by the lexer, the only stage that knows the base it was written in.
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
        // TODO: record the error and recover, so one run reports more than the first.
        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected {kind}, got {token.Kind}");
    }
}
