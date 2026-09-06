using System.Linq;

using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Syntax.Tests;

public class YieldStatementSyntaxTests
{
    [Fact]
    public void ParsesYieldFromStatement()
    {
        var tree = SyntaxTree.ParseText("yield from source\n");
        var statement = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();
        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal("from", statement.FromKeyword.Text);
        Assert.Equal("source", Assert.IsType<IdentifierNameSyntax>(statement.Expression).Identifier.Text);
        Assert.Equal("yield from source\n", tree.GetRoot().ToFullString());
    }

    [Fact]
    public void ParsesYieldFromExpression()
    {
        var tree = SyntaxTree.ParseText("let done = (yield from source)\n");
        var expression = tree.GetRoot().DescendantNodes().OfType<YieldExpressionSyntax>().Single();
        Assert.Empty(tree.GetDiagnostics());
        Assert.Equal("from", expression.FromKeyword.Text);
    }

    [Fact]
    public void YieldReturnForm_ReportsMigrationDiagnostic()
    {
        var tree = SyntaxTree.ParseText("yield return value\n");
        var yieldStatement = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();

        Assert.Contains(tree.GetDiagnostics(), diagnostic => diagnostic.Descriptor == CompilerDiagnostics.YieldReturnFormRemoved);
        var recovery = Assert.IsType<ReturnExpressionSyntax>(yieldStatement.Expression);
        Assert.Equal("value", ((IdentifierNameSyntax)recovery.Expression!).Identifier.Text);
    }

    [Fact]
    public void ParsesYieldStatement()
    {
        var tree = SyntaxTree.ParseText("yield value\n");
        var yieldStatement = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();

        Assert.Equal(SyntaxKind.YieldStatement, yieldStatement.Kind);
        Assert.Equal("value", ((IdentifierNameSyntax)yieldStatement.Expression).Identifier.Text);
    }

    [Fact]
    public void YieldBreakForm_ReportsMigrationDiagnostic()
    {
        var tree = SyntaxTree.ParseText("yield break\n");
        var yieldStatement = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();

        Assert.Contains(tree.GetDiagnostics(), diagnostic => diagnostic.Descriptor == CompilerDiagnostics.YieldBreakFormRemoved);
        Assert.IsType<BreakExpressionSyntax>(yieldStatement.Expression);
    }
}
