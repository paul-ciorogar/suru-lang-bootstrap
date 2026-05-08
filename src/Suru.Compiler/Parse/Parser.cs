using System.Globalization;
using Suru.Compiler;
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

    public static CompilationResult<Module> Parse(Tokens tokens)
    {
        try
        {
            var parser = new Parser(tokens);
            return CompilationResult<Module>.Ok(parser._Parse());
        }
        catch (ParseException ex)
        {
            return CompilationResult<Module>.Fail(ex.Message);
        }
    }

    private Module _Parse()
    {
        var stmts = new List<Statement>();

        while (IsNot(TokenKind.Eof))
        {
            stmts.Add(ParseStatement());
        }

        _ = Consume(TokenKind.Eof);

        // Build type declaration indexes; duplicates are intentionally allowed here —
        // the semantic analyzer reports the error. Last definition wins in each dict.
        var typeDecls = new Dictionary<string, TypeDeclaration>();
        foreach (var td in stmts.OfType<TypeDeclaration>())
            typeDecls[td.Name] = td;

        var sumTypeDecls = new Dictionary<string, SumTypeDeclaration>();
        foreach (var std in stmts.OfType<SumTypeDeclaration>())
            sumTypeDecls[std.Name] = std;

        return new Module
        {
            SourcePath           = _tokens.SourcePath,
            Statements           = stmts,
            TypeDeclarations     = typeDecls,
            SumTypeDeclarations  = sumTypeDecls,
        };
    }

    private Statement ParseStatement()
    {
        if (CanConsume(TokenKind.Include))
            return ParseIncludeDirective();

        if (CanConsume(TokenKind.Type))
            return ParseTypeDeclaration();

        if (CanConsume(TokenKind.Fn))
            return ParseFunctionDeclaration();

        if (CanConsume(TokenKind.While))
        {
            var condition = ParseExpression();
            Consume(TokenKind.LeftBrace);
            var body = new List<Statement>();
            while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
                body.Add(ParseStatement());
            Consume(TokenKind.RightBrace);
            return new WhileStatement(condition, body);
        }

        if (CanConsume(TokenKind.Return))
            return ParseReturnStatement();

        // let <name> <TypeAnnotation> : <expr>
        // The type annotation is mandatory. It may be a simple identifier (Int64)
        // or a generic form (Array<Struct>). ParseTypeAnnotation handles both.
        if (CanConsume(TokenKind.Let))
        {
            var nameToken = Consume(TokenKind.Identifier);
            var typeAnnotation = ParseTypeAnnotation();
            Consume(TokenKind.Colon);
            return new LetStatement(nameToken.Text, ParseExpression(), typeAnnotation);
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

        // match at statement level → MatchStatement with block-body arms.
        // match in expression position (let RHS, return value) still goes through
        // ParsePrimary → MatchExpression.
        if (Is(TokenKind.Match))
            return ParseMatchStatement();

        return new ExpressionStatement(ParseExpression());
    }

    // match <cond> { <pattern>: <body> ... }
    //
    // Arm body dispatch (backward-compatible):
    //   { stmts }  — block body (enables early returns, let bindings, etc.)
    //   }          — empty body (arm is the last one, closes the match)
    //   <expr>     — single-expression body for backward compat with existing code
    private MatchStatement ParseMatchStatement()
    {
        Consume(TokenKind.Match);
        var condition = ParseExpression();
        Consume(TokenKind.LeftBrace);

        var arms = new List<MatchStatementArm>();
        while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Colon);

            List<Statement> body;
            if (Is(TokenKind.LeftBrace))
            {
                // Block body: { stmt* }
                Consume(TokenKind.LeftBrace);
                body = new List<Statement>();
                while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
                    body.Add(ParseStatement());
                Consume(TokenKind.RightBrace);
            }
            else if (Is(TokenKind.RightBrace))
            {
                // Bare colon followed immediately by the closing } of the match → empty body.
                body = [];
            }
            else
            {
                // Backward-compat: single expression arm body (e.g. printLn(x), 42).
                body = [new ExpressionStatement(ParseExpression())];
            }

            arms.Add(new MatchStatementArm(pattern, body));
            CanConsume(TokenKind.Comma);
        }

        Consume(TokenKind.RightBrace);
        return new MatchStatement(condition, arms);
    }

    private IncludeDirective ParseIncludeDirective()
    {
        var pathToken = Consume(TokenKind.StringLiteral);
        var asToken = Consume(TokenKind.Identifier);
        if (asToken.Text != "as")
            throw new ParseException(
                $"{_tokens.SourcePath}({asToken.Line},{asToken.Column}): expected 'as', got '{asToken.Text}'");
        var nsToken = Consume(TokenKind.Identifier);
        return new IncludeDirective(pathToken.Text, nsToken.Text);
    }

    // Parses either a struct type or a sum type declaration, dispatching on what
    // follows the colon:
    //   type Point: { x Int64, y Int64 }    → TypeDeclaration (struct)
    //   type Shape: Circle, Square           → SumTypeDeclaration (sum type)
    private Statement ParseTypeDeclaration()
    {
        var nameToken = Consume(TokenKind.Identifier);
        Consume(TokenKind.Colon);

        if (_tokens.Current().Kind == TokenKind.LeftBrace)
            return ParseStructTypeBody(nameToken.Text);

        if (_tokens.Current().Kind == TokenKind.Identifier)
            return ParseSumTypeBody(nameToken.Text);

        throw new ParseException(
            $"Expected '{{' (struct) or identifier (sum type) after 'type {nameToken.Text}:'");
    }

    // Parses:  { field Type [, field Type]* }
    // Fields may be separated by commas or newlines (the lexer discards whitespace,
    // so newline-separated fields just have no comma token between them).
    private TypeDeclaration ParseStructTypeBody(string name)
    {
        Consume(TokenKind.LeftBrace);

        var fields = new List<(string Field, TypeAnnotation Type)>();
        while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
        {
            var fieldName = Consume(TokenKind.Identifier);
            var fieldType = ParseTypeAnnotation();
            fields.Add((fieldName.Text, fieldType));
            CanConsume(TokenKind.Comma); // optional comma between fields
        }

        Consume(TokenKind.RightBrace);
        return new TypeDeclaration(name, fields);
    }

    // Parses:  VariantA, VariantB [, VariantC]*
    // Variants are comma-separated struct type names on a single logical line.
    private SumTypeDeclaration ParseSumTypeBody(string name)
    {
        var variants = new List<string>();
        variants.Add(Consume(TokenKind.Identifier).Text);
        while (CanConsume(TokenKind.Comma))
            variants.Add(Consume(TokenKind.Identifier).Text);
        return new SumTypeDeclaration(name, variants);
    }

    private FunctionDeclaration ParseFunctionDeclaration()
    {
        var nameToken = Consume(TokenKind.Identifier);
        Consume(TokenKind.LeftParen);
        var parameters = new List<FunctionParameter>();
        while (IsNot(TokenKind.RightParen) && IsNot(TokenKind.Eof))
        {
            var paramName = Consume(TokenKind.Identifier);
            var paramType = ParseTypeAnnotation();
            parameters.Add(new FunctionParameter(paramName.Text, paramType));
            CanConsume(TokenKind.Comma);
        }
        Consume(TokenKind.RightParen);

        TypeAnnotation returnType;
        if      (CanConsume(TokenKind.Void))    returnType = new TypeAnnotation("void");
        else if (Is(TokenKind.LeftBrace))        returnType = new TypeAnnotation("void");
        else                                     returnType = ParseTypeAnnotation();

        Consume(TokenKind.LeftBrace);
        var body = new List<Statement>();
        while (IsNot(TokenKind.RightBrace) && IsNot(TokenKind.Eof))
            body.Add(ParseStatement());
        Consume(TokenKind.RightBrace);

        return new FunctionDeclaration(nameToken.Text, parameters, returnType, body);
    }

    // Parses a type annotation: Identifier [< TypeAnnotation >]
    // Examples: Int64, Array<Struct>, Array<Array<Int64>>
    private TypeAnnotation ParseTypeAnnotation()
    {
        var name = Consume(TokenKind.Identifier).Text;
        TypeAnnotation? param = null;
        if (CanConsume(TokenKind.LessThan))
        {
            param = ParseTypeAnnotation();
            Consume(TokenKind.GreaterThan);
        }
        return new TypeAnnotation(name, param);
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

        if (token.Kind == TokenKind.Minus)
            return ParseNegativeNumber();

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

        if (token.Kind == TokenKind.StringLiteral)
        {
            Advance();
            return new StringLiteralExpression(token.Text);
        }

        if (token.Kind == TokenKind.Minus)
            return ParseNegativeNumber();

        if (token.Kind == TokenKind.Identifier)
        {
            Advance();
            return new VariableReferenceExpression(token.Text);
        }

        throw new ParseException($"{_tokens.SourcePath}({token.Line},{token.Column}): expected match pattern, got {token.Kind}");
    }

    private Expression ParseNegativeNumber()
    {
        Advance(); // consume '-'
        var num = _tokens.Current();
        if (num.Kind == TokenKind.IntLiteral)
        {
            Advance();
            return new IntLiteral(-long.Parse(num.Text));
        }
        if (num.Kind == TokenKind.FloatLiteral)
        {
            Advance();
            return new FloatLiteral(-double.Parse(num.Text, CultureInfo.InvariantCulture));
        }
        throw new ParseException($"{_tokens.SourcePath}({num.Line},{num.Column}): expected number after '-', got {num.Kind}");
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
