using System.IO;
using System.Linq;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class ConstructorInitializerTests : DiagnosticTestBase
{
    [Theory]
    [InlineData("class Derived : Base { init() {} }")]
    [InlineData("class Derived() : Base {}")]
    [InlineData("class Derived : Base {}")]
    public void MissingImplicitBaseConstructor_ReportsDiagnosticAndPreventsEmit(string derivedDeclaration)
    {
        var tree = SyntaxTree.ParseText("open class Base { init(value: int) {} }\n" + derivedDeclaration);
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(tree)
            .AddReferences(TestMetadataReferences.Default);

        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal("RAV1501", diagnostic.Id);
        Assert.Contains("Base", diagnostic.GetMessage());
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(diagnostic, Assert.Single(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Id == "RAV1501");
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void PrimaryConstructor_WithInvalidExplicitBaseInitializer_ReportsOnlyExplicitCallError()
    {
        var tree = SyntaxTree.ParseText("open class Base { init(value: int) {} }\nclass Derived() : Base() {}");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(tree)
            .AddReferences(TestMetadataReferences.Default);

        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal("RAV1501", diagnostic.Id);
        Assert.Equal("()", tree.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public void StaticConstructor_WithBaseInitializer_ReportsDiagnostic()
    {
        var code = """
open class Base {
    public init() {}
}

class Derived : Base {
    public static init(): base() {}
}
""";

        var verifier = CreateVerifier(
            code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV0312").WithSpan(6, 27, 6, 31)
            ]);

        verifier.Verify();
    }

    [Fact]
    public void ConstructorInitializer_WithNoMatchingOverload_ReportsDiagnostic()
    {
        var code = """
open class Base {
    public init() {}
}

class Derived : Base {
    public init(): base(1) {}
}
""";

        var verifier = CreateVerifier(
            code,
            expectedDiagnostics: [
                new DiagnosticResult("RAV1501").WithSpan(6, 24, 6, 27).WithArguments("constructor for type", "Base", 1)
            ]);

        verifier.Verify();
    }

    [Fact]
    public void ConstructorInitializer_WithIncompatibleArgumentType_ReportsDiagnostic()
    {
        var code = """
open class Base {
    public init(value: int) {}
}

class Derived : Base {
    public init(): base("text") {}
}
""";

        var verifier = CreateVerifier(
            code,
            expectedDiagnostics: [
                new DiagnosticResult(CompilerDiagnostics.CannotConvertFromTypeToType.Id).WithSpan(6, 25, 6, 31).WithArguments("string", "int")
            ]);

        verifier.Verify();
    }
}
