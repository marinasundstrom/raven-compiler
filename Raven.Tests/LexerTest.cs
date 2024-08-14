using System.IO;
using System.Text;

using Raven.CodeAnalysis.Parser.Internal;

using Xunit;

namespace Raven.Tests;

public class LexerTest
{
    [Fact]
    public void Test1()
    {
        var buffer = Encoding.UTF8.GetBytes(@"
tes33  foo 123
foo
");

        MemoryStream memoryStream = new MemoryStream(buffer);
        using (var textReader = new StreamReader(memoryStream))
        {
            ILexer lexer = new Lexer(textReader);
            var token1 = lexer.ReadToken();
            var token2 = lexer.ReadToken();
            var token3 = lexer.ReadToken();
            var token4 = lexer.ReadToken();
        }
    }

    [Fact]
    public void Test2()
    {
        var buffer = Encoding.UTF8.GetBytes(@"
tes33  if { foo} 123
foo
");

        MemoryStream memoryStream = new MemoryStream(buffer);
        using (var textReader = new StreamReader(memoryStream))
        {
            ILexer lexer = new Lexer(textReader);
            Tokenizer tokenizer = new Tokenizer(lexer);
            var token1 = tokenizer.ReadToken();
            var token2 = tokenizer.ReadToken();
            var token3 = tokenizer.ReadToken();
            var token4 = tokenizer.ReadToken();
        }
    }
}
