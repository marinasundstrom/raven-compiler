using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class FileScopedCodeDiagnosticsTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("0", true)]
    [InlineData("namespace Example;\n0", false)]
    [InlineData("namespace Example;\n0", true)]
    public void Library_WithFileScopedCode_ProducesDiagnostic(string source, bool queryBeforeDiagnostics)
    {
        var tree = SyntaxTree.ParseText(source);
        var compilation = Compilation.Create("lib", [tree], TestMetadataReferences.Default, new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        if (queryBeforeDiagnostics)
            model.GetTypeInfo(tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Single());

        var diagnostic = Assert.Single(model.GetDiagnostics().Where(d => d.Descriptor == CompilerDiagnostics.FileScopedCodeRequiresConsole));
        Assert.Equal("0", tree.GetText().ToString(diagnostic.Location.SourceSpan));
        Assert.Single(compilation.GetDiagnostics().Where(d => d.Descriptor == CompilerDiagnostics.FileScopedCodeRequiresConsole));
    }

    [Fact]
    public void MultipleFiles_WithFileScopedCode_ProducesDiagnostic()
    {
        var tree1 = SyntaxTree.ParseText("System.Console.WriteLine(\"First\")", path: "src/Main.rvn");
        var tree2 = SyntaxTree.ParseText("System.Console.WriteLine(\"Second\")", path: "src/Program.rvn");
        var compilation = Compilation.Create("app", [tree1, tree2], TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Descriptor == CompilerDiagnostics.FileScopedCodeMultipleFiles)
            .ToArray();

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.Location.SourceTree == tree1);
        Assert.Contains(diagnostics, d => d.Location.SourceTree == tree2);
    }

    [Fact]
    public void FileScopedCode_CanAppearAfterTypeDeclaration()
    {
        var code = """
let x = S()

struct S {}

0
""";
        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create("app", [tree], TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.FileScopedCodeOutOfOrder);
    }

    [Fact]
    public void FileScopedNamespace_AfterGlobalStatement_ProducesDiagnostic()
    {
        var code = """
0
namespace Foo;
""";
        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create("app", [tree], TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.FileScopedNamespaceOutOfOrder);
    }
}
