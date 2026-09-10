using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Tests;
using Raven.CodeAnalysis.Text;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class CollectionExpressionIncrementalSemanticTests : CompilationTestBase
{
    [Fact]
    public void EditingCollectionTargetType_RebindsContextualTypeAcrossQueryOrders()
    {
        const string intSource = "let values: int[] = [1, 2, 3]";
        const string longSource = "let values: long[] = [1, 2, 3]";
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "collection-target-edit",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "collections.rav",
            SourceText.From(intSource),
            "/tmp/collection-target-edit.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        AssertSnapshot(SpecialType.System_Int32, diagnosticsFirst: false);

        var documentId = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single().Id;
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(longSource)));
        AssertSnapshot(SpecialType.System_Int64, diagnosticsFirst: true);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(intSource)));
        AssertSnapshot(SpecialType.System_Int32, diagnosticsFirst: false);

        void AssertSnapshot(SpecialType expectedElementType, bool diagnosticsFirst)
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = compilation.SyntaxTrees.Single();
            if (diagnosticsFirst)
                Assert.Empty(compilation.GetDiagnostics());

            var collection = tree.GetRoot().DescendantNodes().OfType<CollectionExpressionSyntax>().Single();
            var typeInfo = compilation.GetSemanticModel(tree).GetTypeInfo(collection);
            var arrayType = Assert.IsAssignableFrom<IArrayTypeSymbol>(typeInfo.ConvertedType ?? typeInfo.Type);

            Assert.Equal(expectedElementType, arrayType.ElementType.SpecialType);
            Assert.Empty(compilation.GetDiagnostics());
        }
    }
}
