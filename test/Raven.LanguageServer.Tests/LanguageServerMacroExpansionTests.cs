using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;

using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

namespace Raven.LanguageServer.Tests;

using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

public sealed class LanguageServerMacroExpansionTests
{
    [Theory]
    [InlineData("answer!()", "answer")]
    [InlineData("raven! { 42 }", "raven")]
    public async Task HoverHandler_NestedMacroName_ResolvesInsideDeclarationFragment(string invocation, string name)
    {
        var code = "import Raven.LanguageServer.Tests.*\ncomponent! Greeting(Name: string) {\n    let value = " + invocation + "\n}";
        var path = Path.Combine(Path.GetTempPath(), $"raven-nested-macro-{Guid.NewGuid():N}.rvn");
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(path);
        var document = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddMacroReference(document.Project.Id,
            new MacroReference(new DeclarationFragmentMacro()))
            .AddMacroReference(document.Project.Id, new MacroReference(new AnswerMacro()))
            .AddMacroReference(document.Project.Id, new MacroReference(new RavenBodyMacro()))).ShouldBeTrue();
        var offset = code.IndexOf(invocation, StringComparison.Ordinal) + 1;
        var hover = await new HoverHandler(store, NullLogger<HoverHandler>.Instance).Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(SourceText.From(code), new TextSpan(offset, 0)).Start
        }, CancellationToken.None);
        hover.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain(name);
        hover.Contents.MarkupContent!.Value.ShouldContain("Macro");
        hover.Range!.Start.Line.ShouldBe(2);
    }

    [Fact]
    public async Task HoverHandler_ArgumentListMacro_ResolvesNameArgumentsAndCallback()
    {
        const string code = """
import System.*
import Raven.LanguageServer.Tests.*

class ObservableInt {
    func Subscribe(handler: (int) -> unit) -> unit { }
}
class CounterViewModel {
    var Count: int = 0
    val CountChanged: ObservableInt = ObservableInt()
}
class Harness {
    func WriteLine(value: int) -> unit { }
    func Run(viewModel: CounterViewModel) -> unit {
        let subscription = subscribe!(viewModel.Count, func (value) => {
            WriteLine(value)
        })
    }
}
""";
        var documentPath = Path.Combine(Path.GetTempPath(), $"raven-subscribe-{Guid.NewGuid():N}.rvn");
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var document = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddMacroReference(
            document.Project.Id,
            new MacroReference(new SubscribeMacro()))).ShouldBeTrue();
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var sourceText = SourceText.From(code);

        foreach (var (marker, expected) in new[]
        {
            ("subscribe!", "subscribe"),
            ("viewModel.Count", "CounterViewModel"),
            ("Count,", "int"),
            ("value) =>", "int"),
            ("WriteLine(value)", "WriteLine"),
            ("value)\n", "int")
        })
        {
            var offset = code.LastIndexOf(marker, StringComparison.Ordinal);
            offset.ShouldBeGreaterThanOrEqualTo(0);
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(sourceText, new TextSpan(offset + 1, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull(marker);
            hover!.Contents.MarkupContent!.Value.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task ComponentMacro_RemainsExpandableAfterAppendingBlankLinesAsync()
    {
        const string code = """
component! Greeting(Name: string) {
    let message = Name
}

func Main() {}
""";
        var documentPath = Path.Combine(Path.GetTempPath(), $"raven-component-{Guid.NewGuid():N}.rvn");
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);

        var document = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddMacroReference(
            document.Project.Id,
            new MacroReference(new DeclarationFragmentMacro()))).ShouldBeTrue();
        store.TryGetCompilation(uri, out var initialCompilation).ShouldBeTrue();
        initialCompilation.ShouldNotBeNull();
        initialCompilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == Raven.CodeAnalysis.DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        await store.UpsertDocumentAsync(uri, code + "\n\n", deferMacroConsumerRefresh: true);
        await manager.FlushPendingMacroConsumerRefreshesAsync();
        store.TryGetCompilation(uri, out var updatedCompilation).ShouldBeTrue();
        updatedCompilation.ShouldNotBeNull();
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree =>
            string.Equals(Path.GetFullPath(tree.FilePath), documentPath, StringComparison.Ordinal));
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedRoot = updatedTree.GetRoot();
        var hints = new List<InlayHint>();
        var budget = new InlayHintHandler.InlayHintCollectionBudget(
            Stopwatch.StartNew(),
            CancellationToken.None,
            double.PositiveInfinity,
            includeTooltips: false);
        InlayHintHandler.AddMacroFragmentTypeHints(
            hints,
            updatedModel,
            updatedRoot,
            updatedTree.GetText(),
            updatedRoot.FullSpan,
            budget,
            CancellationToken.None);

        updatedCompilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == Raven.CodeAnalysis.DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        var lspDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        lspDiagnostics.Where(static diagnostic => diagnostic.Code?.String == "RAVM022").ShouldBeEmpty();
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewAtAttributePosition()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[Observable]
    var Title: string
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(ObservableMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            attribute.Span.Start + 2,
            out var display);

        success.ShouldBeTrue();
        display.MacroName.ShouldBe("Observable");
        display.FullText.ShouldContain("private field");
        display.FullText.ShouldContain("_Title: string");
        display.FullText.ShouldContain("var Title: string");
        display.PreviewText.ShouldContain("_Title");
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewForRequestedRange()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[Observable]
    var Title: string
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(ObservableMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().Single();
        var selectionStart = attribute.Span.Start + 2;
        var selectionEnd = attribute.Span.End - 1;
        var (startLine, startColumn) = sourceText.GetLineAndColumn(selectionStart);
        var (endLine, endColumn) = sourceText.GetLineAndColumn(selectionEnd);

        var success = MacroExpansionDisplayService.TryCreateForRange(
            sourceText,
            semanticModel,
            root,
            new LspRange(
                new Position(startLine - 1, startColumn - 1),
                new Position(endLine - 1, endColumn - 1)),
            out var display);

        success.ShouldBeTrue();
        display.Span.ShouldBe(attribute.Span);
        display.FullText.ShouldContain("set {");
    }

    [Fact]
    public void MacroExpansionDisplayService_FormatsDetachedSyntaxFactoryExpansion()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[Observable]
    var Title: string = ""
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(DetachedObservableMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            attribute.Span.Start + 2,
            out var display);

        success.ShouldBeTrue();
        display.FullText.ShouldContain("private var _Title: string = \"\"");
        display.FullText.ShouldContain("var Title: string");
        display.FullText.ShouldContain("get => _Title");
        display.FullText.ShouldContain("set {");
        display.FullText.ShouldNotContain("privatevar_Title");
        display.FullText.ShouldNotContain("valoldValue");
    }

    [Fact]
    public void MacroExpansionDisplayService_ShowsComposedExpansionForStackedMacros()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[First]
    #[Second]
    var Title: string
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(
                new MacroReference(typeof(FirstStackingMacro)),
                new MacroReference(typeof(SecondStackingMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().First();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            attribute.Span.Start + 2,
            out var display);

        success.ShouldBeTrue();
        display.FullText.ShouldContain("func Before_Title() -> int");
        display.FullText.ShouldContain("var Second_First_Title: string");
        display.FullText.ShouldContain("func AfterAgain_First_Title() -> int");
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewForArgumentInvocation()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class Harness {
    func Run() -> int => answer!()
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(
                new MacroReference(typeof(AnswerMacro)),
                new MacroReference(typeof(RavenBodyMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var expression = root.DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            expression.Span.Start + 1,
            out var display);

        success.ShouldBeTrue();
        display.MacroName.ShouldBe("answer");
        display.InvocationDisplay.ShouldBe("answer!(...)");
        display.FullText.ShouldBe("42");
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewForExpressionHeaderInvocation()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class Harness {
    func Run() -> int => headerIdentity! 40 + 2
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(HeaderIdentityMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var expression = root.DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            expression.Name.Span.Start,
            out var display);

        success.ShouldBeTrue();
        display.MacroName.ShouldBe("headerIdentity");
        display.InvocationDisplay.ShouldBe("headerIdentity! 40 + 2");
        display.FullText.ShouldBe("40 + 2");
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewForTokenTreeMacroExpression()
    {
        const string code = """
class Harness {
    func Run() -> int => Raven.LanguageServer.Tests.raven! {
        40 + 2
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(
                new MacroReference(typeof(AnswerMacro)),
                new MacroReference(typeof(RavenBodyMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var expression = root.DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            expression.Name.Span.Start,
            out var display);

        success.ShouldBeTrue();
        display.MacroName.ShouldBe("Raven.LanguageServer.Tests.raven");
        display.InvocationDisplay.ShouldBe("Raven.LanguageServer.Tests.raven! { ... }");
        display.FullText.ShouldBe("40 + 2");
    }

    [Fact]
    public void MacroExpansionDisplayService_BuildsPreviewForImportedTokenTreeInvocation()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class Harness {
    func Run() -> int => raven! {
        40 + 2
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(RavenBodyMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var expression = root.DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            expression.Name.Span.Start,
            out var display);

        success.ShouldBeTrue();
        display.MacroName.ShouldBe("raven");
        display.InvocationDisplay.ShouldBe("raven! { ... }");
        display.FullText.ShouldBe("40 + 2");
    }

    [Fact]
    public void MacroExpansionDisplayService_DoesNotBuildPreviewInsideFreestandingMacroArguments()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class Harness {
    func Run(viewModel: CounterViewModel) {
        let subscription = subscribe!(viewModel.Count, (value) => {
            WriteLine(value)
        })
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(ReactiveSubscribeMacro)));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var sourceText = SourceText.From(code);
        var root = syntaxTree.GetRoot();
        var argumentOffset = code.IndexOf("Count", StringComparison.Ordinal) + 1;

        var success = MacroExpansionDisplayService.TryCreateForOffset(
            sourceText,
            semanticModel,
            root,
            argumentOffset,
            out _);

        success.ShouldBeFalse();
    }

    [Fact]
    public void HoverHandler_MacroHint_IncludesKindTargetsAndArguments()
    {
        const string code = """
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[Observable]
    var Title: string
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(ObservableMacro)));

        var tryGetMacroHint = typeof(HoverHandler)
            .GetMethod("TryGetMacroHint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var arguments = new object?[] { compilation, "Observable", null };

        var success = (bool)tryGetMacroHint.Invoke(null, arguments)!;
        var hint = (string)arguments[2]!;

        success.ShouldBeTrue();
        hint.ShouldContain("Attached declaration macro.");
        hint.ShouldContain("Targets `Property`.");
        hint.ShouldContain("No arguments.");
    }

    [Fact]
    public void SymbolResolver_FreestandingMacroLambdaArgument_ResolvesLambdaParameter()
    {
        const string code = """
import System.*
import Raven.LanguageServer.Tests.*

class ObservableInt {
    func Subscribe(handler: (int) -> unit) -> unit { }
}

class CounterViewModel {
    var Count: int = 0
    val CountChanged: ObservableInt = ObservableInt()
}

class Harness {
    func WriteLine(value: int) -> unit { }

    func Run(viewModel: CounterViewModel) -> unit {
        let subscription = subscribe!(viewModel.Count, (value) => {
            WriteLine(value)
        })
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(SubscribeMacro)))
            .AddReferences(LanguageServerTestReferences.Default);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        compilation.GetDiagnostics().Where(d => d.Severity == Raven.CodeAnalysis.DiagnosticSeverity.Error).ShouldBeEmpty();
        var root = syntaxTree.GetRoot();
        var valueReference = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(static identifier => identifier.Identifier.ValueText == "value")
            .Last();

        var resolution = SymbolResolver.ResolveSymbolAtPosition(semanticModel, root, valueReference.Identifier.SpanStart + 1);

        resolution.ShouldNotBeNull();
        var parameterSymbol = resolution!.Value.Symbol.ShouldBeAssignableTo<IParameterSymbol>();
        parameterSymbol.Name.ShouldBe("value");
        parameterSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("int");
    }

    [Fact]
    public void SymbolResolver_AttachedMacroPropertyReference_PrefersReceiverBoundProperty()
    {
        const string code = """
import System.*
import System.Console.*
import Raven.LanguageServer.Tests.*

class MyViewModel {
    #[Observable]
    var Title: string
}

class Program {
    static func Main() -> unit {
        let viewModel = MyViewModel()
        viewModel.Title = "Hello from Raven"
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code, path: "/workspace/test.rvn");
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddMacroReferences(new MacroReference(typeof(ObservableMacro)))
            .AddReferences(LanguageServerTestReferences.Default);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();
        var titleReference = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Single(static access => access.ToString() == "viewModel.Title");

        var resolution = SymbolResolver.ResolveSymbolAtPosition(
            semanticModel,
            root,
            titleReference.Name.Span.Start + 1);

        resolution.ShouldNotBeNull();
        var propertySymbol = resolution!.Value.Symbol.ShouldBeAssignableTo<IPropertySymbol>();
        propertySymbol.Name.ShouldBe("Title");
        propertySymbol.ContainingType?.Name.ShouldBe("MyViewModel");
    }

    public sealed class ObservableMacro : IMacroDefinition
    {
        public string Name => "Observable";

        public MacroKind Kind => MacroKind.AttachedDeclaration;

        public MacroTarget Targets => MacroTarget.Property;

        public MacroExpansionResult Expand(PropertyDeclarationSyntax property, AttachedMacroContext context)
        {
            var tree = SyntaxFactory.ParseSyntaxTree("""
                class __GeneratedContainer {
                    private field _Title: string

                    var Title: string {
                        get => _Title
                        set {
                            _Title = value
                        }
                    }
                }
                """);

            var container = (ClassDeclarationSyntax)tree.GetRoot().Members.Single();
            return new MacroExpansionResult
            {
                IntroducedMembers = [(FieldDeclarationSyntax)container.Members[0]],
                ReplacementDeclaration = (PropertyDeclarationSyntax)container.Members[1]
            };
        }
    }

    public sealed class DetachedObservableMacro : IMacroDefinition
    {
        public string Name => "Observable";

        public MacroKind Kind => MacroKind.AttachedDeclaration;

        public MacroTarget Targets => MacroTarget.Property;

        public MacroExpansionResult Expand(PropertyDeclarationSyntax target, AttachedMacroContext context)
        {
            var property = (PropertyDeclarationSyntax)context.TargetDeclaration;
            var propertyName = property.Identifier.ValueText;
            var backingFieldName = "_" + propertyName;
            var propertyType = property.Type;

            var backingField = SyntaxFactory.StoredPropertyDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)),
                SyntaxFactory.Token(SyntaxKind.VarKeyword),
                SyntaxFactory.Identifier(backingFieldName),
                propertyType,
                property.Initializer);

            var replacement = property
                .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>())
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.List<AccessorDeclarationSyntax>(
                    [
                        SyntaxFactory.AccessorDeclaration(
                            SyntaxKind.GetAccessorDeclaration,
                            SyntaxFactory.List<AttributeListSyntax>(),
                            SyntaxFactory.TokenList(),
                            SyntaxFactory.Token(SyntaxKind.GetKeyword),
                            SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(backingFieldName))),
                        SyntaxFactory.AccessorDeclaration(
                            SyntaxKind.SetAccessorDeclaration,
                            SyntaxFactory.List<AttributeListSyntax>(),
                            SyntaxFactory.TokenList(),
                            SyntaxFactory.Token(SyntaxKind.SetKeyword),
                            SyntaxFactory.BlockStatement(
                                SyntaxFactory.List<StatementSyntax>(
                                [
                                    SyntaxFactory.LocalDeclarationStatement(
                                        SyntaxFactory.VariableDeclaration(
                                            SyntaxFactory.Token(SyntaxKind.ValKeyword),
                                            SyntaxFactory.SeparatedList<VariableDeclaratorSyntax>(
                                            [
                                                new SyntaxNodeOrToken(SyntaxFactory.VariableDeclarator(
                                                    SyntaxFactory.Identifier("oldValue"),
                                                    typeAnnotation: null,
                                                    initializer: SyntaxFactory.EqualsValueClause(SyntaxFactory.IdentifierName(backingFieldName))))
                                            ]))),
                                    SyntaxFactory.AssignmentStatement(
                                        SyntaxKind.SimpleAssignmentStatement,
                                        SyntaxFactory.IdentifierName(backingFieldName),
                                        SyntaxFactory.Token(SyntaxKind.EqualsToken),
                                        SyntaxFactory.IdentifierName("value")),
                                    SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.InvocationExpression(
                                            SyntaxFactory.IdentifierName("RaisePropertyChanged"),
                                            SyntaxFactory.ArgumentList(
                                                SyntaxFactory.SeparatedList<ArgumentSyntax>(
                                                [
                                                    new SyntaxNodeOrToken(SyntaxFactory.Argument(SyntaxFactory.NameOfExpression(SyntaxFactory.IdentifierName(propertyName)))),
                                                    new SyntaxNodeOrToken(SyntaxFactory.Token(SyntaxKind.CommaToken)),
                                                    new SyntaxNodeOrToken(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("oldValue"))),
                                                    new SyntaxNodeOrToken(SyntaxFactory.Token(SyntaxKind.CommaToken)),
                                                    new SyntaxNodeOrToken(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value")))
                                                ]))))
                                ])))
                    ])))
                .WithExpressionBody(null)
                .WithInitializer(null)
                .WithTerminatorToken(SyntaxFactory.Token(SyntaxKind.None));

            return new MacroExpansionResult
            {
                IntroducedMembers = [backingField],
                ReplacementDeclaration = replacement
            };
        }
    }

    public sealed class AnswerMacro : IMacroDefinition
    {
        public string Name => "answer";
        public MacroKind Kind => MacroKind.Freestanding;

        public FreestandingMacroExpansionResult Expand(FreestandingMacroContext context)
            => new()
            {
                Expression = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(42))
            };
    }

    public sealed class RavenBodyMacro : IMacroDefinition
    {
        public string Name => "raven";

        public FreestandingMacroExpansionResult Expand(TokenTreeMacroContext context)
            => new()
            {
                Expression = context.ParseExpression()
            };
    }

    public sealed class HeaderIdentityMacro : IMacroDefinition
    {
        public string Name => "headerIdentity";
        public MacroCarrierKinds CarrierKinds => MacroCarrierKinds.ExpressionHeader;
        public MacroBodyRequirement BodyRequirement => MacroBodyRequirement.None;

        public FreestandingMacroExpansionResult Expand(
            ExpressionSyntax expression,
            FreestandingMacroContext context)
            => new()
            {
                Expression = expression
            };
    }

    public sealed class SubscribeMacro : IMacroDefinition
    {
        public string Name => "subscribe";
        public MacroKind Kind => MacroKind.Freestanding;
        public bool AcceptsArguments => true;

        public FreestandingMacroExpansionResult Expand(
            ExpressionSyntax propertyExpression,
            ExpressionSyntax callback,
            FreestandingMacroContext context)
        {
            var propertyAccess = (MemberAccessExpressionSyntax)propertyExpression;
            var propertyIdentifier = (IdentifierNameSyntax)propertyAccess.Name;
            var signalName = propertyIdentifier.Identifier.ValueText + "Changed";

            return new FreestandingMacroExpansionResult
            {
                Expression = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            propertyAccess.Expression,
                            SyntaxFactory.Token(SyntaxKind.DotToken),
                            SyntaxFactory.IdentifierName(signalName)),
                        SyntaxFactory.Token(SyntaxKind.DotToken),
                        SyntaxFactory.IdentifierName("Subscribe")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(
                        [
                            new SyntaxNodeOrToken(SyntaxFactory.Argument(callback))
                        ])))
            };
        }
    }

    public sealed class ReactiveSubscribeMacro : IMacroDefinition
    {
        public string Name => "subscribe";
        public MacroKind Kind => MacroKind.Freestanding;
        public bool AcceptsArguments => true;

        public FreestandingMacroExpansionResult Expand(
            ExpressionSyntax propertyExpression,
            ExpressionSyntax callback,
            FreestandingMacroContext context)
        {
            var propertyAccess = (MemberAccessExpressionSyntax)propertyExpression;
            var propertyIdentifier = (IdentifierNameSyntax)propertyAccess.Name;
            var signalName = propertyIdentifier.Identifier.ValueText + "Changed";

            return new FreestandingMacroExpansionResult
            {
                Expression = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            propertyAccess.Expression,
                            SyntaxFactory.Token(SyntaxKind.DotToken),
                            SyntaxFactory.IdentifierName(signalName)),
                        SyntaxFactory.Token(SyntaxKind.DotToken),
                        SyntaxFactory.IdentifierName("Subscribe")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(
                        [
                            new SyntaxNodeOrToken(SyntaxFactory.Argument(callback))
                        ])))
            };
        }
    }

    public sealed class FirstStackingMacro : IMacroDefinition
    {
        public string Name => "First";
        public MacroKind Kind => MacroKind.AttachedDeclaration;
        public MacroTarget Targets => MacroTarget.Property;

        public MacroExpansionResult Expand(PropertyDeclarationSyntax target, AttachedMacroContext context)
        {
            var property = (PropertyDeclarationSyntax)context.TargetDeclaration;
            var members = ParseMembers($$"""
                class __GeneratedContainer {
                    func Before_{{property.Identifier.ValueText}}() -> int { return 1 }
                    var First_{{property.Identifier.ValueText}}: string { get => "" }
                }
                """);

            return new MacroExpansionResult
            {
                IntroducedMembers = [members[0]],
                ReplacementDeclaration = members[1]
            };
        }
    }

    public sealed class SecondStackingMacro : IMacroDefinition
    {
        public string Name => "Second";
        public MacroKind Kind => MacroKind.AttachedDeclaration;
        public MacroTarget Targets => MacroTarget.Property;

        public MacroExpansionResult Expand(PropertyDeclarationSyntax target, AttachedMacroContext context)
        {
            var property = (PropertyDeclarationSyntax)context.CurrentDeclaration;
            var members = ParseMembers($$"""
                class __GeneratedContainer {
                    var Second_{{property.Identifier.ValueText}}: string { get => "" }
                    func AfterAgain_{{property.Identifier.ValueText}}() -> int { return 2 }
                }
                """);

            return new MacroExpansionResult
            {
                ReplacementDeclaration = members[0],
                PeerDeclarations = [members[1]]
            };
        }
    }

    private static ImmutableArray<MemberDeclarationSyntax> ParseMembers(string source)
    {
        var tree = SyntaxFactory.ParseSyntaxTree(source);
        var container = (ClassDeclarationSyntax)tree.GetRoot().Members.Single();
        return [.. container.Members];
    }
}
