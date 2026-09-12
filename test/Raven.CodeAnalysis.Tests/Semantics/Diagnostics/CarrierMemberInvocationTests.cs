using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests;

public class CarrierMemberInvocationTests
{
    [Theory]
    [InlineData("Option<string>", "Missing")]
    [InlineData("Option<string>", "Length")]
    [InlineData("Result<string, string>", "Missing")]
    [InlineData("Result<string, string>", "Length")]
    public void InvalidPayloadMemberCall_ReportsDiagnostic(string carrier, string member)
    {
        var source = $$"""
import System.*
func Test(value: {{carrier}}) -> unit {
    value?.{{member}}()
}
""";
        var compilation = Compilation.Create("invalid-carrier-call", [SyntaxTree.ParseText(source)],
            [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(
                Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll"))],
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Contains(compilation.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.GetMessage().Contains(member));
    }
}
