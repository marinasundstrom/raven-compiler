using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class ExceptionHandlingTests : DiagnosticTestBase
{
    [Fact]
    public void TryStatement_WithoutCatchOrFinally_ReportsDiagnostic()
    {
        var code = "try { }";

        var verifier = CreateVerifier(code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV1015").WithSpan(1, 7, 1, 8)
            ]);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithNonExceptionType_ReportsDiagnostic()
    {
        var code = """
try {
}
catch int ex {
}
""";

        var verifier = CreateVerifier(code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV1016").WithSpan(3, 7, 3, 10).WithArguments("int")
            ]);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithTypePattern_WithoutParentheses_Binds()
    {
        var code = """
import System.*

try {
}
catch FormatException ex {
    let message = ex.Message
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithLegacyParenthesizedTypeOnlyForm_Binds()
    {
        var code = """
import System.Threading.Tasks.*

try {
}
catch (TaskCanceledException) {
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithTypeOnlyForm_WithoutParentheses_ReportsUnsupportedPatternDiagnostic()
    {
        var code = """
import System.*

try {
}
catch FormatException {
}
""";

        var verifier = CreateVerifier(code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV1024").WithSpan(5, 1, 6, 2).WithArguments(nameof(SyntaxKind.PropertyPattern))
            ]);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithGuardedPattern_Binds()
    {
        var code = """
import System.Net.*
import System.Net.Http.*

try {
}
catch HttpRequestException ex when ex.Message != "" {
    let status = ex.StatusCode
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void CatchClause_WithNonTypePattern_ReportsDiagnostic()
    {
        var code = """
try {
}
catch > 0 {
}
""";

        var verifier = CreateVerifier(code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV0030").WithSpan(3, 9, 3, 10),
                new DiagnosticResult("RAV1024").WithSpan(3, 1, 4, 2).WithArguments(nameof(SyntaxKind.GreaterThanPattern))
            ]);

        verifier.Verify();
    }

    [Fact]
    public void TryExpression_InferredType_IsCurrentlyErrorType()
    {
        var code = """
let value = try int.Parse("foo")
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var variable = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v => v.Identifier.Text == "value");
        var local = (ILocalSymbol)model.GetDeclaredSymbol(variable)!;
        Assert.IsType<ErrorTypeSymbol>(local.Type);
    }

    [Fact]
    public void TryExpression_NestedTryReportsDiagnostic()
    {
        var code = "let value = try try 1";

        var verifier = CreateVerifier(code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV1906").WithSpan(1, 17, 1, 20)
            ]);

        verifier.Verify();
    }

    [Fact]
    public void ParenthesizedTryPropagation_HasPayloadType()
    {
        var code = """
import System.*

func ParseFlag(text: string) -> Result<bool, Exception> {
    let flag = (try System.Convert.ToBoolean(text))?
    return .Ok(flag)
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create("try-propagation", [tree],
            [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "Raven.Core.dll"))],
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var model = compilation.GetSemanticModel(tree);
        var local = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.Text == "flag");

        var localSymbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(local));
        Assert.Equal(SpecialType.System_Boolean, localSymbol.Type.SpecialType);
    }

    [Theory]
    [InlineData("let value = try? Compute()")]
    [InlineData("let value = try ? Compute()")]
    [InlineData("let value = try? Compute() match { _ => 1 }")]
    public void RemovedTryQuestionSyntax_ReportsMigrationDiagnosticAndPreservesText(string source)
    {
        var tree = SyntaxTree.ParseText(source);
        Assert.Contains(tree.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1925");
        Assert.Equal(source, tree.GetRoot().ToFullString());
        Assert.Single(tree.GetRoot().DescendantNodes().OfType<TryExpressionSyntax>());
    }

    [Fact]
    public void TryExpression_WithAwait_TypeIsCurrentlyErrorType()
    {
        var code = """
import System.Threading.Tasks.*

class C {
    async func Work() {
        let attempt = try await Task.FromResult(1)
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.Text == "attempt");

        var local = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(declarator));
        Assert.IsType<ErrorTypeSymbol>(local.Type);
    }

    [Fact]
    public void TryExpression_WithAwait_PatternMatchingTypeFlow_IsCurrentlyUnresolved()
    {
        var code = """
import System.*
import System.Threading.Tasks.*

class C {
    async func Work() -> Task<string> {
        return try await Task.FromResult(1) match {
            int value => value.ToString()
            Exception ex => ex.Message
        }
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var tryExpression = root
            .DescendantNodes()
            .OfType<TryExpressionSyntax>()
            .Single();

        Assert.IsType<ErrorTypeSymbol>(model.GetTypeInfo(tryExpression).Type);

        var matchExpression = root
            .DescendantNodes()
            .OfType<PostfixMatchExpressionSyntax>()
            .Single();

        Assert.Equal(SpecialType.System_String, model.GetTypeInfo(matchExpression).Type!.SpecialType);
    }

    [Fact]
    public void ParenthesizedTryAwaitPropagation_HasPayloadType()
    {
        var code = """
import System.*
import System.Threading.Tasks.*

class C {
    async func Work() -> Task<Result<int, Exception>> {
        let value = (try await Task.FromResult(1))?
        return .Ok(value)
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create("try-propagation", [tree],
            [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "Raven.Core.dll"))],
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.Text == "value");

        var local = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(declarator));
        Assert.Equal(SpecialType.System_Int32, local.Type.SpecialType);
    }

}
