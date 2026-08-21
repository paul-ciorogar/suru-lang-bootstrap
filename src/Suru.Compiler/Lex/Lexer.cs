using System.Globalization;
using System.Text;

namespace Suru.Compiler.Lex;

public sealed class Lexer(string source, string sourcePath, BuildMode mode = BuildMode.Production)
{
    private int _pos;
    private int _line = 1;
    private int _column = 1;

    public string SourcePath => sourcePath;

    internal Token NextToken()
    {
        while (_pos < source.Length)
        {
            char c = source[_pos];

            if (char.IsWhiteSpace(c))
            {
                AdvanceWhitespace();
                continue;
            }

            if (char.IsLetter(c))
                return ReadIdentifierOrKeyword();

            if (char.IsDigit(c))
                return ReadNumber();

            if (c == '/' && Peek(1) == '/')
            {
                SkipRestOfLine();
                continue;
            }

            // A production build treats a directive as a comment, so the parser never learns
            // one was there; a test build hands the '#' over and lexes the rest of the line
            // like any other code.
            if (c == '#' && mode == BuildMode.Production)
            {
                SkipRestOfLine();
                continue;
            }

            int startLine = _line, startCol = _column;
            switch (c)
            {
                case '(': Advance(); return new Token(TokenKind.LeftParen, startLine, startCol);
                case ')': Advance(); return new Token(TokenKind.RightParen, startLine, startCol);
                case '{': Advance(); return new Token(TokenKind.LeftBrace, startLine, startCol);
                case '}': Advance(); return new Token(TokenKind.RightBrace, startLine, startCol);
                case ',': Advance(); return new Token(TokenKind.Comma, startLine, startCol);
                // Reached only in BuildMode.Test; a production build skipped the line above.
                case '#': Advance(); return new Token(TokenKind.Hash, startLine, startCol);
                case ':': Advance(); return new Token(TokenKind.Colon, startLine, startCol);
                case '+': Advance(); return new Token(TokenKind.Plus, startLine, startCol);
                case '-': Advance(); return new Token(TokenKind.Minus, startLine, startCol);
                case '*': Advance(); return new Token(TokenKind.Star, startLine, startCol);
                // A '/' pair is a comment, handled above; a lone one divides.
                case '/': Advance(); return new Token(TokenKind.Slash, startLine, startCol);
                case '%': Advance(); return new Token(TokenKind.Percent, startLine, startCol);
                case '=': Advance(); return new Token(TokenKind.Equal, startLine, startCol);
                case '<': return ReadLess(startLine, startCol);
                case '>': return ReadGreater(startLine, startCol);
                default:
                    throw new LexException($"{sourcePath}({_line},{_column}): unexpected character '{c}'");
            }
        }

        return new Token(TokenKind.Eof, "", _line, _column);
    }

    private Token ReadIdentifierOrKeyword()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
            Advance();
        var text = source[start.._pos];
        var kind = text switch
        {
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "let" => TokenKind.Let,
            "if" => TokenKind.If,
            "else" => TokenKind.Else,
            "and" => TokenKind.And,
            "or" => TokenKind.Or,
            "not" => TokenKind.Not,
            _ => TokenKind.Identifier,
        };
        return new Token(kind, text, startLine, startCol);
    }

    /// <summary>'&lt;' opens three operators: '&lt;', '&lt;=' and the not-equal '&lt;&gt;'.</summary>
    private Token ReadLess(int startLine, int startCol)
    {
        Advance();
        switch (Peek())
        {
            case '=': Advance(); return new Token(TokenKind.LessOrEqual, startLine, startCol);
            case '>': Advance(); return new Token(TokenKind.NotEqual, startLine, startCol);
            default: return new Token(TokenKind.Less, startLine, startCol);
        }
    }

    private Token ReadGreater(int startLine, int startCol)
    {
        Advance();
        if (Peek() == '=')
        {
            Advance();
            return new Token(TokenKind.GreaterOrEqual, startLine, startCol);
        }
        return new Token(TokenKind.Greater, startLine, startCol);
    }

    /// <summary>
    /// Reads an integer or float literal. An integer is decimal, or hexadecimal, binary or
    /// octal behind a <c>0x</c>/<c>0b</c>/<c>0o</c> prefix — lowercase only, so a literal
    /// has one spelling; every form may separate digits with <c>_</c>. The literal is
    /// decoded here rather than in the parser, since this is the only stage that knows the
    /// base and has a position to report an error against.
    /// </summary>
    private Token ReadNumber()
    {
        int startLine = _line, startCol = _column;
        int start = _pos;
        var digits = new StringBuilder();

        if (Peek() == '0' && PrefixBase(Peek(1)) is var prefix && prefix is not null)
        {
            var (radix, name) = prefix.Value;
            Advance(); // '0'
            Advance(); // 'x', 'b' or 'o'
            ScanDigits(radix, name, digits);
            RejectTrailing(name);
            return new Token(source[start.._pos], ToMagnitude(digits, radix, startLine, startCol), startLine, startCol);
        }

        // '0X' would otherwise fall through to decimal and be reported as an invalid digit
        // in a decimal literal, which says nothing about the real mistake.
        if (Peek() == '0' && PrefixBase(char.ToLowerInvariant(Peek(1))) is not null)
            throw new LexException(
                $"{sourcePath}({_line},{_column + 1}): base prefix '{Peek(1)}' must be lowercase");

        ScanDigits(10, DecimalName, digits);

        // A '.' is part of the literal only when a digit follows it: the trailing-dot form
        // '1.' stays an integer followed by an unexpected character.
        if (Peek() == '.' && DigitValue(Peek(1), 10) >= 0)
        {
            digits.Append('.');
            Advance();
            ScanDigits(10, DecimalName, digits);
            RejectTrailing(DecimalName);
            var value = double.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            return new Token(source[start.._pos], value, startLine, startCol);
        }

        RejectTrailing(DecimalName);
        return new Token(source[start.._pos], ToMagnitude(digits, 10, startLine, startCol), startLine, startCol);
    }

    private const string DecimalName = "decimal";

    private static (int Radix, string Name)? PrefixBase(char c) => c switch
    {
        'x' => (16, "hexadecimal"),
        'b' => (2, "binary"),
        'o' => (8, "octal"),
        _ => null,
    };

    /// <summary>The value of <paramref name="c"/> in base <paramref name="radix"/>, or -1 if it is not a digit of that base.</summary>
    private static int DigitValue(char c, int radix)
    {
        int value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0 && value < radix ? value : -1;
    }

    /// <summary>
    /// Consumes a run of digits of the given base, appending them to
    /// <paramref name="digits"/> with the separators dropped. A <c>_</c> must sit between
    /// two digits, so it can neither open nor close the run nor follow another <c>_</c>.
    /// </summary>
    private void ScanDigits(int radix, string name, StringBuilder digits)
    {
        int count = 0;
        while (true)
        {
            if (Peek() == '_')
            {
                if (count == 0 || DigitValue(Peek(1), radix) < 0)
                    throw new LexException($"{sourcePath}({_line},{_column}): '_' must separate digits");
                Advance();
                continue;
            }

            if (DigitValue(Peek(), radix) < 0)
                break;

            digits.Append(Peek());
            Advance();
            count++;
        }

        if (count > 0)
            return;

        throw new LexException(char.IsLetterOrDigit(Peek())
            ? $"{sourcePath}({_line},{_column}): invalid digit '{Peek()}' in {name} literal"
            : $"{sourcePath}({_line},{_column}): {name} literal has no digits");
    }

    /// <summary>
    /// Rejects a letter, digit or <c>_</c> butted up against the end of a literal, so
    /// <c>0xffz</c> and <c>1i64</c> are errors rather than a literal followed by an
    /// identifier.
    /// </summary>
    private void RejectTrailing(string name)
    {
        char c = Peek();
        if (char.IsLetterOrDigit(c) || c == '_')
            throw new LexException($"{sourcePath}({_line},{_column}): invalid digit '{c}' in {name} literal");
    }

    /// <summary>
    /// Decodes the digits as an unsigned magnitude. The bound is one past
    /// <see cref="long.MaxValue"/> rather than <see cref="long.MaxValue"/> itself: the sign
    /// is not part of the literal, so only the parser knows whether 9223372036854775808 is
    /// the negated <c>i64</c> minimum or an out-of-range positive.
    /// </summary>
    private ulong ToMagnitude(StringBuilder digits, int radix, int line, int column)
    {
        try
        {
            ulong value = 0;
            checked
            {
                for (int i = 0; i < digits.Length; i++)
                    value = value * (ulong)radix + (ulong)DigitValue(digits[i], radix);
            }
            if (value <= NegatedMinimum)
                return value;
        }
        catch (OverflowException)
        {
            // Falls through to the same diagnostic — too big to decode is still too big.
        }

        throw new LexException($"{sourcePath}({line},{column}): integer literal is out of range for 'i64'");
    }

    /// <summary>The magnitude of <see cref="long.MinValue"/>, the largest a literal may reach.</summary>
    private const ulong NegatedMinimum = (ulong)long.MaxValue + 1;

    /// <summary>
    /// Consumes everything up to — but not including — the newline, so the newline itself
    /// still goes through <see cref="AdvanceWhitespace"/> and keeps the line counter right.
    /// A last line without one just runs into EOF.
    /// <para>
    /// Three callers, all discarding text no stage will look at: a <c>//</c> comment, a
    /// <c>#</c> directive in a production build, and the compiler-written annotation on a
    /// <c>#view</c> or <c>#assert</c> line. The last is why this is reachable from the
    /// parser: an annotation is output, not source, and need not lex at all.
    /// </para>
    /// </summary>
    internal void SkipRestOfLine()
    {
        while (_pos < source.Length && Peek() != '\n')
        {
            Advance();
        }
    }

    /// <summary>
    /// The character <paramref name="offset"/> positions ahead of the cursor, or
    /// <c>'\0'</c> past the end of the source — no token may contain a NUL, so every
    /// caller stops on it the same way it stops on a character it does not accept.
    /// </summary>
    private char Peek(int offset = 0) =>
        _pos + offset < source.Length ? source[_pos + offset] : '\0';

    private void Advance()
    {
        _column++;
        _pos++;
    }

    private void AdvanceWhitespace()
    {
        if (source[_pos] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _pos++;
    }
}
