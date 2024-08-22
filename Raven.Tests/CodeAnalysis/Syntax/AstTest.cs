using System;
using System.Text.Json;

using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Syntax.Tests;

public class AstTest
{
    [Fact]
    public void Test1()
    {
        // let x = if y > 1337 then true

        try
        {
            var expr1 = SyntaxFactory.Let(
                SyntaxFactory.Identifier("x"),
                SyntaxFactory.IfElseExpression(
                        SyntaxFactory.BinaryOperation(
                             SyntaxFactory.Identifier("y"),
                             SyntaxFactory.GreaterThanToken
                                .PrependLeadingTrivia(SyntaxFactory.Whitespace(1)),
                             SyntaxFactory.Number(1337)
                        ),
                        SyntaxFactory.True()
                    ));

            var x = expr1.ToFullString();

            Console.WriteLine(x);
        }
        catch (Exception ex) { }


        // x = if (y > 1337) then true else false

        var expr = SyntaxFactory.Assignment(
                SyntaxFactory.Identifier("x"),
                SyntaxFactory.IfElseExpression(
                    SyntaxFactory.Parenthesis(
                        SyntaxFactory.BinaryOperation(
                             SyntaxFactory.Identifier("y"),
                             SyntaxFactory.GreaterThanToken,
                             SyntaxFactory.Number(1337)
                        )
                    ),
                    SyntaxFactory.True(),
                    SyntaxFactory.False()
                )
            );

        var str = JsonSerializer.Serialize(expr, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Console.WriteLine(str);
    }
}