using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class GeneratedCodeAttributeAnalyzerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCodeAttribute_ControlsMemberCallbacksAndDiagnosticReporting(bool projectLane)
    {
        const string source = """
import System.CodeDom.Compiler.*

[GeneratedCode("test", "1")]
class Generated {
    func Target() { Helper() }
    func Helper() { }
}

class Authored {
    func Target() { Helper() }
    func Helper() { }

    [GeneratedCode("test", "1")]
    func Hidden() { Helper() }
}
""";
        var analyzer = new AttributeAwareAnalyzer();
        var workspace = CreateWorkspace([source], analyzer, out var projectId);

        var diagnostics = projectLane
            ? workspace.GetProjectAnalyzerDiagnostics(projectId)
            : workspace.GetDiagnostics(projectId);

        analyzer.TypeCallbacks.ShouldBe(1);
        analyzer.MethodCallbacks.ShouldBe(2);
        analyzer.FunctionCallbacks.ShouldBe(2);
        analyzer.InvocationCallbacks.ShouldBe(1);
        diagnostics.Count(diagnostic => diagnostic.Id == "AN9060").ShouldBe(2);
        diagnostics.ShouldContain(diagnostic => diagnostic.Location == Location.None);
        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Location.SourceTree != null &&
            diagnostic.Location.SourceSpan.Start >= source.IndexOf("class Authored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartiallyGeneratedType_RemainsAnalyzable(bool projectLane)
    {
        const string generatedPart = """
import System.CodeDom.Compiler.*

[GeneratedCode("test", "1")]
partial class Mixed {
    func GeneratedPart() { }
}
""";
        const string authoredPart = """
partial class Mixed {
    func AuthoredPart() { }
}
""";
        var analyzer = new SymbolOnlyAnalyzer();
        var workspace = CreateWorkspace([generatedPart, authoredPart], analyzer, out var projectId);

        if (projectLane)
            workspace.GetProjectAnalyzerDiagnostics(projectId);
        else
            workspace.GetDiagnostics(projectId);

        analyzer.Types.ShouldContain("Mixed");
    }

    private static AdhocWorkspace CreateWorkspace(
        IReadOnlyList<string> sources,
        DiagnosticAnalyzer analyzer,
        out ProjectId projectId)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject("GeneratedAttributes");
        projectId = solution.Projects.Single().Id;
        foreach (var reference in TestMetadataReferences.Default)
            solution = solution.AddMetadataReference(projectId, reference);
        for (var index = 0; index < sources.Count; index++)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNew(projectId),
                $"Input{index}.rvn",
                Raven.CodeAnalysis.Text.SourceText.From(sources[index]),
                $"/Input{index}.rvn");
        }
        solution = solution.AddAnalyzerReference(projectId, new AnalyzerReference(analyzer));
        workspace.TryApplyChanges(solution).ShouldBeTrue();
        return workspace;
    }

    private sealed class AttributeAwareAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            "AN9060", "Generated attribute", null, "", "Generated attribute", "Testing", DiagnosticSeverity.Warning);

        public int TypeCallbacks { get; private set; }
        public int MethodCallbacks { get; private set; }
        public int FunctionCallbacks { get; private set; }
        public int InvocationCallbacks { get; private set; }

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(action => TypeCallbacks++, SymbolKind.Type);
            context.RegisterSymbolAction(action => MethodCallbacks++, SymbolKind.Method);
            context.RegisterSyntaxNodeAction(action => FunctionCallbacks++, SyntaxKind.MethodDeclaration);
            context.RegisterOperationAction(action => InvocationCallbacks++, OperationKind.Invocation);
            context.RegisterCompilationAction(action =>
            {
                foreach (var typeName in new[] { "Generated", "Authored" })
                {
                    var type = action.Compilation.GetTypeByMetadataName(typeName)!;
                    action.ReportDiagnostic(Diagnostic.Create(Rule, type.Locations.Single()));
                }
                var hidden = action.Compilation.GetTypeByMetadataName("Authored")!.GetMembers("Hidden").Single();
                action.ReportDiagnostic(Diagnostic.Create(Rule, hidden.Locations.Single()));
                action.ReportDiagnostic(Diagnostic.Create(Rule, Location.None));
            });
        }
    }

    private sealed class SymbolOnlyAnalyzer : DiagnosticAnalyzer
    {
        public List<string> Types { get; } = [];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(action => Types.Add(action.Symbol.Name), SymbolKind.Type);
        }
    }
}
