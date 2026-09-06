using System;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public sealed class IteratorBindingTests : CompilationTestBase
{
    [Fact]
    public void YieldFrom_OperationDescribesDelegation()
    {
        const string source = """
            import System.Collections.Generic.*
            func Numbers(source: IEnumerable<int>) -> IEnumerable<long> {
                yield from source
            }
            """;
        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var syntax = tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>().Single();
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var operation = Assert.IsAssignableFrom<Raven.CodeAnalysis.Operations.IYieldOperation>(model.GetOperation(syntax));
        Assert.True(operation.IsDelegating);
        Assert.Equal(SpecialType.System_Int64, operation.ElementType.SpecialType);
        Assert.NotNull(operation.ReturnedValue);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("IEnumerable<string>")]
    [InlineData("IAsyncEnumerable<int>")]
    public void YieldFrom_InvalidSourceReportsDiagnostic(string type)
    {
        var (compilation, _) = CreateCompilation($$"""
            import System.Collections.Generic.*
            func Numbers(source: {{type}}) -> IEnumerable<int> {
                yield from source
            }
            """);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Yield_InGenericEnumerableMethod_BindsIteratorElementType()
    {
        const string source = """
import System.Collections.Generic.*

func Numbers() -> IEnumerable<int> {
    yield 1
    yield 2
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var yieldStatements = root.DescendantNodes().OfType<YieldStatementSyntax>().ToArray();
        var methodDeclaration = root.DescendantNodes().OfType<FunctionStatementSyntax>().Single();

        var firstYield = Assert.IsType<BoundYieldStatement>(model.GetBoundNode(yieldStatements[0]));
        var secondYield = Assert.IsType<BoundYieldStatement>(model.GetBoundNode(yieldStatements[1]));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetDeclaredSymbol(methodDeclaration));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal(IteratorMethodKind.Enumerable, firstYield.IteratorKind);
        Assert.Equal(SpecialType.System_Int32, firstYield.ElementType.SpecialType);
        Assert.Equal(SpecialType.System_Int32, firstYield.Expression.Type?.SpecialType);
        Assert.Equal(IteratorMethodKind.Enumerable, secondYield.IteratorKind);
        Assert.True(method.IsIterator);
        Assert.Equal(IteratorMethodKind.Enumerable, method.IteratorKind);
        Assert.Equal(SpecialType.System_Int32, method.IteratorElementType?.SpecialType);
    }
}
