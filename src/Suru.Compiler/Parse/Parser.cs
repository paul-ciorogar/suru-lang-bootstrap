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

        while (IsNot(TokenKind.Eof))
        {
            stmts.Add(ParseStatement());
        }

        _ = Consume(TokenKind.Eof);

        return new Module { SourcePath = _tokens.SourcePath, Statements = stmts };
    }

    private Statement ParseStatement()
    {
        if (CanConsume(TokenKind.Fn))
            return ParseFunctionDeclaration();

        if (CanConsume(TokenKind.Return))
            return ParseReturnStatement();

        // let <name> [<TypeAnnotation>] : <expr>
        if (CanConsume(TokenKind.Let))
        {
            var nameToken = Consume(TokenKind.Identifier);

            string? typeAnnotation = null;

            if (Is(TokenKind.Identifier))
            {
                typeAnnotation = Consume().Text;
            }

            Consume(TokenKind.Colon);

            return new LetStatement(nameToken.Text, typeAnnotation, ParseExpression());
        }

        // <name> : <expr>  (assignment — must consume identifier first, then check for colon)
        if (Is(TokenKind.Identifier))
        {
            var nameToken = Consume();

            if (CanConsume(TokenKind.Colon))
            {
                return new AssignmentStatement(nameToken.Text, ParseExpression());
            }

            // Not a simple assignment — finish parsing the expression that starts with this identifier
            var expr = FinishExprFromIdent(nameToken);

            // <receiver>.<field> : <expr>  (field assignment)
            if (expr is FieldAccessExpression fa && CanConsume(TokenKind.Colon))
                return new FieldAssignmentStatement(fa.Receiver, fa.FieldName, ParseExpression());

            return new ExpressionStatement(expr);
        }

        return new ExpressionStatement(ParseExpression());
    }

    private FunctionDeclaration ParseFunctionDeclaration()
    {
        var nameToken = Consume(TokenKind.Identifier);
        Consume(TokenKind.LeftParen);
        var parameters = new List<FunctionParameter>();
        while (IsNot(TokenKind.RightParen) && IsNot(TokenKind.Eof))
        {
            var paramName = Consume(TokenKind.Identifier);
            var paramType = Consume(TokenKind.Identifier);
            parameters.Add(new FunctionParameter(paramName.Text, paramType.Text));
            CanConsume(TokenKind.Comma);
        }
        Consume(TokenKind.RightParen);

        string returnTypeName = CanConsume(TokenKind.Void) ? "void" : Consume(TokenKind.Identifier).Text;

        Consume(TokenKind.LeftBrace);
        var body = new List<Statement>();
        while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
            body.Add(ParseStatement());
        Consume(TokenKind.RightBrace);

        return new FunctionDeclaration(nameToken.Text, parameters, returnTypeName, body);
    }

    private ReturnStatement ParseReturnStatement()
    {
        if (Is(TokenKind.RightBrace) || Is(TokenKind.Eof))
            return new ReturnStatement(null);
        return new ReturnStatement(ParseExpression());
    }

    // Parse the rest of an expression when the leading identifier has already been consumed.
    private Expression FinishExprFromIdent(Token identToken)
    {
        Expression primary;
        if (CanConsume(TokenKind.LeftParen))
        {
            var args = ParseArguments();
            Consume(TokenKind.RightParen);
            primary = new CallExpression(identToken.Text, args);
        }
        else
        {
            primary = new VariableReferenceExpression(identToken.Text);
        }

        // postfix chain: (.methodName(args))*
        primary = ParsePostfixChain(primary);

        // binary: or / and
        return ParseOrFrom(primary);
    }

    // or_expr -> and_expr ('or' and_expr)*
    private Expression ParseExpression() => ParseOrFrom(ParseAndExpr());

    private Expression ParseOrFrom(Expression left)
    {
        while (CanConsume(TokenKind.Or))
        {
            left = new BinaryExpression(left, BinaryOp.Or, ParseAndExpr());
        }

        return left;
    }

    // and_expr -> not_expr ('and' not_expr)*
    private Expression ParseAndExpr()
    {
        var left = ParseNotExpr();
        while (CanConsume(TokenKind.And))
        {
            left = new BinaryExpression(left, BinaryOp.And, ParseNotExpr());
        }
        return left;
    }

    // not_expr -> 'not' not_expr | postfix_expr
    private Expression ParseNotExpr()
    {
        if (CanConsume(TokenKind.Not))
        {
            return new UnaryExpression(UnaryOp.Not, ParseNotExpr());
        }
        return ParsePostfixChain(ParsePrimary());
    }

    // postfix chain: ('.' Identifier ['(' args ')'])*
    private Expression ParsePostfixChain(Expression expr)
    {
        while (CanConsume(TokenKind.Dot))
        {
            var memberName = Consume(TokenKind.Identifier);
            if (CanConsume(TokenKind.LeftParen))
            {
                var args = ParseArguments();
                Consume(TokenKind.RightParen);
                expr = new MethodCallExpression(expr, memberName.Text, args);
            }
            else
            {
                expr = new FieldAccessExpression(expr, memberName.Text);
            }
        }
        return expr;
    }

    private Expression ParsePrimary()
    {
        var token = _tokens.Current();

        // array literal: [ expr (, expr)* ]
        if (CanConsume(TokenKind.LeftBracket))
        {
            var elements = new List<Expression>();
            while (IsNot(TokenKind.RightBracket) && IsNot(TokenKind.Eof))
            {
                elements.Add(ParseExpression());
                CanConsume(TokenKind.Comma);
            }
            Consume(TokenKind.RightBracket);
            return new ArrayLiteralExpression(elements);
        }

        // string literal
        if (token.Kind == TokenKind.StringLiteral)
        {
            Advance();
            return new StringLiteralExpression(token.Text);
        }

        // struct literal: { field: expr [, field: expr]* }
        if (CanConsume(TokenKind.LeftBrace))
        {
            var fields = new List<(string Name, Expression Value)>();
            while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
            {
                var fieldName = Consume(TokenKind.Identifier);
                Consume(TokenKind.Colon);
                var fieldValue = ParseExpression();
                fields.Add((fieldName.Text, fieldValue));
                CanConsume(TokenKind.Comma);
            }
            Consume(TokenKind.RightBrace);
            return new StructLiteralExpression(fields);
        }

        if (CanConsume(TokenKind.Match))
        {
            var condition = ParseExpression();
            Consume(TokenKind.LeftBrace);
            var arms = ParseMatchArms();
            Consume(TokenKind.RightBrace);
            return new MatchExpression(condition, arms);
        }

        if (CanConsume(TokenKind.Identifier))
        {
            if (CanConsume(TokenKind.LeftParen))
            {
                var args = ParseArguments();
                Consume(TokenKind.RightParen);
                return new CallExpression(token.Text, args);
            }
            return new VariableReferenceExpression(token.Text);
        }

        Advance();

        return token.Kind switch
        {
            TokenKind.True => new BoolLiteral(true),
            TokenKind.False => new BoolLiteral(false),
            TokenKind.IntLiteral => new IntLiteral(long.Parse(token.Text)),
            TokenKind.FloatLiteral => new FloatLiteral(double.Parse(token.Text, CultureInfo.InvariantCulture)),
            _ => throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): unexpected token {token.Kind}"),
        };
    }

    private List<MatchArm> ParseMatchArms()
    {
        var arms = new List<MatchArm>();
        while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Colon);
            var body = ParseExpression();
            arms.Add(new MatchArm(pattern, body));
            CanConsume(TokenKind.Comma);
        }
        return arms;
    }

    // Returns null for wildcard (_), otherwise a literal expression.
    private Expression? ParseMatchPattern()
    {
        if (CanConsume(TokenKind.Wildcard)) return null;
        if (CanConsume(TokenKind.True))     return new BoolLiteral(true);
        if (CanConsume(TokenKind.False))    return new BoolLiteral(false);

        var token = _tokens.Current();
        if (token.Kind == TokenKind.IntLiteral)
        {
            Advance();
            return new IntLiteral(long.Parse(token.Text));
        }
        if (token.Kind == TokenKind.FloatLiteral)
        {
            Advance();
            return new FloatLiteral(double.Parse(token.Text, System.Globalization.CultureInfo.InvariantCulture));
        }

        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected match pattern, got {token.Kind}");
    }

    private List<Expression> ParseArguments()
    {
        var args = new List<Expression>();
        if (Is(TokenKind.RightParen))
            return args;
        args.Add(ParseExpression());
        while (CanConsume(TokenKind.Comma))
        {
            args.Add(ParseExpression());
        }
        return args;
    }

    private void Advance()
    {
        _tokens.Advance();
    }

    private bool IsNot(TokenKind kind)
    {
        return !_tokens.CurrentIs(kind);
    }

    private bool Is(TokenKind kind)
    {
        return _tokens.CurrentIs(kind);
    }

    private Token Consume()
    {
        var token = _tokens.Current();
        _tokens.Advance();
        return token;
    }

    private Token Consume(TokenKind kind)
    {
        var token = _tokens.Current();
        if (token.Kind == kind)
        {
            _tokens.Advance();
            return token;
        }
        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected {kind}, got {token.Kind}");
    }

    // If current token kind match then comsume and return true else return false
    private bool CanConsume(TokenKind kind)
    {
        var token = _tokens.Current();
        if (token.Kind != kind) { return false; }
        _tokens.Advance();
        return true;
    }
}
