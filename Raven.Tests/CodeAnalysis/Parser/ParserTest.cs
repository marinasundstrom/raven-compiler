using System.Text;

using Raven.CodeAnalysis.Parser;
using Raven.CodeAnalysis.Parser.Internal;

using Xunit;

namespace Raven.CodeAnalysis.Parser.Tests;

public class ParserTest
{
    [Fact]
    public void Test3()
    {
        var buffer = Encoding.UTF8.GetBytes(@"
 if (1) {

}");

        MemoryStream memoryStream = new MemoryStream(buffer);
        using (var textReader = new StreamReader(memoryStream))
        {
            ILexer lexer = new Lexer(textReader);
            Tokenizer tokenizer = new Tokenizer(lexer);
            SyntaxParser parser = new SyntaxParser(tokenizer);
            var block = parser.ParseStatement();

            var str = block!.ToFullString();
        }
    }

    /*
    [Fact]
    public void Test4()
    {
        var buffer = Encoding.UTF8.GetBytes(
@"string? GetGreeting(string name)
requires name.Length > 0
\{
return $""Hello, \{ name \}!"";
\}");

        MemoryStream memoryStream = new MemoryStream(buffer);
        using (var textReader = new StreamReader(memoryStream))
        {
            ILexer lexer = new Lexer(textReader);
            Tokenizer tokenizer = new Tokenizer(lexer);
            SyntaxParser parser = new SyntaxParser(tokenizer);
            var block = parser.ParseStatement();

            var str = block.ToFullString();
        }
    }
    */
}