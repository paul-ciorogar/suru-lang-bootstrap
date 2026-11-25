using System.Text;
using Suru.Compiler.Lex;

namespace Suru.Compiler.Debug;

/// <summary>
/// Prints the token stream in source order.
/// <para>
/// Re-lexes the source rather than teeing the parser's cursor, so the pull path through
/// <c>Tokens</c> stays untouched and the dump costs nothing when it is off.
/// </para>
/// </summary>
public static class TokenPrinter
{
    public static string Print(string source, string sourcePath, BuildMode mode = BuildMode.Production)
    {
        var lexer = new Lexer(source, sourcePath, mode);
        var output = new StringBuilder();

        try
        {
            while (true)
            {
                var token = lexer.NextToken();
                output.Append($"{token.Position(),-10}{token.Kind}");
                if (token.Text.Length > 0)
                    output.Append(' ').Append(token.Text);
                output.Append('\n');

                if (token.Kind == TokenKind.Eof)
                    break;
            }
        }
        catch (LexException exception)
        {
            // The real parse reports this; the dump only stops where the lexer did.
            output.Append($"<lex error: {exception.Message}>\n");
        }

        return output.ToString();
    }

    private static string Position(this Token token) => $"({token.Line},{token.Column})";
}
