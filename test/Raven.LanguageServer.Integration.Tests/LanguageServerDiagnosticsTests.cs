using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Syntax;
using Raven.LanguageServer;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;
using CodeDiagnosticSeverity = Raven.CodeAnalysis.DiagnosticSeverity;
using CodeLocation = Raven.CodeAnalysis.Location;
using CodeTextSpan = Raven.CodeAnalysis.Text.TextSpan;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class LanguageServerDiagnosticsTests : IDisposable
{
    private const string ThisValueIsNotMutableDiagnosticId = "RAV0027";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"raven-ls-diags-{Guid.NewGuid():N}");

    private sealed class BlockingMethodSymbolAnalyzer : DiagnosticAnalyzer
    {
        public static readonly ManualResetEventSlim Entered = new(false);
        public static readonly ManualResetEventSlim Release = new(false);

        private static readonly DiagnosticDescriptor Descriptor = DiagnosticDescriptor.Create(
            id: "ANLSP001",
            title: "Blocking method symbol",
            description: null,
            helpLinkUri: string.Empty,
            messageFormat: "Blocking method symbol",
            category: "Testing",
            defaultSeverity: CodeDiagnosticSeverity.Info);

        public static void Reset()
        {
            Entered.Reset();
            Release.Reset();
        }

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();

            context.RegisterSymbolAction(ctx =>
            {
                Entered.Set();
                Release.Wait(ctx.CancellationToken);
                ctx.ReportDiagnostic(CodeDiagnostic.Create(Descriptor, ctx.Symbol.Locations.First()));
            }, Raven.CodeAnalysis.SymbolKind.Method);
        }
    }

    [Fact]
    public void ShouldReport_MatchesEquivalentSyntaxTreesByPath()
    {
        const string path = "/workspace/test.rvn";
        const string code = """
class Flag {
    static func explicit(value: Flag) -> bool { return true }
}

func Main() -> () {
    let flag = Flag()
    if flag {
    }
}
""";

        var diagnosticTree = SyntaxTree.ParseText(code, path: path);
        var documentTree = SyntaxTree.ParseText(code, path: path);
        var location = CodeLocation.Create(diagnosticTree, new CodeTextSpan(code.IndexOf("flag {", StringComparison.Ordinal), 4));
        var descriptor = DiagnosticDescriptor.Create(
            "RAVTEST001",
            "Compiler info diagnostic",
            null,
            string.Empty,
            "Compiler info diagnostic",
            "compiler",
            CodeDiagnosticSeverity.Info);
        var diagnostic = CodeDiagnostic.Create(descriptor, location);

        DocumentStore.ShouldReport(diagnostic, documentTree).ShouldBeTrue();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_IncludesCompilerInfoDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class Flag {
    static func explicit(value: Flag) -> bool { return true }
}

func Main() -> () {
    let flag = Flag()
    if flag {
    }
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, "RAV1507", StringComparison.Ordinal)).ShouldBeTrue();
        diagnostics.ShouldContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Information);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_GenericFallbackLambdaPublishesReturnTypeErrorsWithRangesAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "lambda-return.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*

func parse(input: string) -> int {
    let value = int.Parse(input).UnwrapOrElse(() => {
        return ""
    })
    return value
}
""";
        await store.UpsertDocumentAsync(uri, code);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        var diagnostic = diagnostics
            .Where(item => string.Equals(item.Code?.String, "RAV1503", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        diagnostic.Range.Start.Line.ShouldBe(4);
        diagnostic.Range.Start.Character.ShouldBe(15);
        diagnostic.Range.End.Line.ShouldBe(4);
        diagnostic.Range.End.Character.ShouldBe(17);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_MacroBodyPublishesMatchExhaustivenessImmediatelyAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string code = """
import Raven.CodeAnalysis.Syntax.*

macro Inspect(expression: ExpressionSyntax) {
    match expression {}
    expand SyntaxFactory.ParseExpression("0")
}
""";
        File.WriteAllText(documentPath, code);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        var diagnosticSummary = string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code?.String}@{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character} {diagnostic.Message}"));

        diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV2100", StringComparison.Ordinal) &&
            diagnostic.Message.Contains(nameof(AssignmentExpressionSyntax), StringComparison.Ordinal) &&
            diagnostic.Range.Start.Line == 3).ShouldBeTrue(diagnosticSummary);
        diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV2100", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Missing", StringComparison.Ordinal) &&
            diagnostic.Range.Start.Line == 3).ShouldBeTrue(diagnosticSummary);
    }

    [Fact]
    public async Task TryGetDocumentCompilerDiagnosticsAsync_MacroBodyEditPublishesAuthoredDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string validCode = """
import System.Console.*
import Raven.CodeAnalysis.Macros.*
import Raven.CodeAnalysis.Syntax.*

macro Double(value: int) {
    let doubled = value * 2
    expand SyntaxFactory.ParseExpression(doubled.ToString())
}

macro AddOffset(offset: int, expression: ExpressionSyntax) {
    let source = expression.ToString() + " + " + offset.ToString()
    expand SyntaxFactory.ParseExpression(source)
}

macro FirstTokenLength(offset: int, tokens: IMacroTokenStream) {
    let token = tokens.ReadToken()
    let length = token.Text.Length + offset
    expand SyntaxFactory.ParseExpression(length.ToString())
}

func Main() {
    let answer = Double!(21)
    let projectedExpression = AddOffset!(2, 40)
    let tokenLength = FirstTokenLength!(1) { raven }
}
""";
        const string incompleteCode = """
import System.Console.*
import Raven.CodeAnalysis.Macros.*
import Raven.CodeAnalysis.Syntax.*

macro Double(value: int) {
    let doubled = value * 2
    expand SyntaxFactory.ParseExpression(doubled.ToString())
}

macro AddOffset(offset: int, expression: ExpressionSyntax) {
    match expression {
    let source = expression.ToString() + " + " + offset.ToString()
    expand SyntaxFactory.ParseExpression(source)
}

macro FirstTokenLength(offset: int, tokens: IMacroTokenStream) {
    let token = tokens.ReadToken()
    let length = token.Text.Length + offset
    expand SyntaxFactory.ParseExpression(length.ToString())
}

func Main() {
    let answer = Double!(21)
    let projectedExpression = AddOffset!(2, 40)
    let tokenLength = FirstTokenLength!(1) { raven }
}
""";
        const string invalidCode = """
import System.Console.*
import Raven.CodeAnalysis.Macros.*
import Raven.CodeAnalysis.Syntax.*

macro Double(value: int) {
    let doubled = value * 2
    expand SyntaxFactory.ParseExpression(doubled.ToString())
}

macro AddOffset(offset: int, expression: ExpressionSyntax) {
    match expression {
    }
    let source = expression.ToString() + " + " + offset.ToString()
    expand SyntaxFactory.ParseExpression(source)
}

macro FirstTokenLength(offset: int, tokens: IMacroTokenStream) {
    let token = tokens.ReadToken()
    let length = token.Text.Length + offset
    expand SyntaxFactory.ParseExpression(length.ToString())
}

func Main() {
    let answer = Double!(21)
    let projectedExpression = AddOffset!(2, 40)
    let tokenLength = FirstTokenLength!(1) { raven }
}
""";
        File.WriteAllText(documentPath, validCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        await store.UpsertDocumentAsync(uri, validCode);
        var warmDiagnostics = (await store.TryGetDocumentCompilerDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None)).Diagnostics;
        warmDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV2100", StringComparison.Ordinal));

        await store.UpsertDocumentAsync(uri, incompleteCode);
        _ = await store.TryGetDocumentCompilerDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None);

        await store.UpsertDocumentAsync(uri, invalidCode);
        var editedResult = await store.TryGetDocumentCompilerDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None);
        var editedDiagnostics = editedResult.Diagnostics;
        var editedSummary = string.Join(
            Environment.NewLine,
            editedDiagnostics.Select(diagnostic =>
                $"{diagnostic.Code?.String}@{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character} {diagnostic.Message}")) +
            Environment.NewLine +
            $"Was skipped: {editedResult.WasSkipped}";

        editedDiagnostics.Any(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV2100", StringComparison.Ordinal) &&
            diagnostic.Range.Start.Line == 10).ShouldBeTrue(editedSummary);
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV1013", StringComparison.Ordinal));
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV1021", StringComparison.Ordinal));
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV3601", StringComparison.Ordinal));
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAVM010", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Double", StringComparison.Ordinal));
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAVM010", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("FirstTokenLength", StringComparison.Ordinal));
        editedDiagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAVM010", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("AddOffset", StringComparison.Ordinal));

        var hoverHandler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        await AssertHoverAsync("match expression", "match ".Length + 1, "expression: ExpressionSyntax");
        await AssertHoverAsync(
            "ParseExpression(source)",
            "ParseExpression(".Length + 1,
            "val source: string");

        async Task AssertHoverAsync(string marker, int markerOffset, string expected)
        {
            var offset = invalidCode.IndexOf(marker, StringComparison.Ordinal);
            offset.ShouldBeGreaterThanOrEqualTo(0);
            var sourceText = Raven.CodeAnalysis.Text.SourceText.From(invalidCode);
            var hover = await hoverHandler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(
                    sourceText,
                    new CodeTextSpan(offset + markerOffset, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull();
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task GetDiagnosticsAsync_TagsUnusedLocalAsUnnecessaryAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class C {
    public func M() -> unit {
        let count = 0
    }
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        var diagnostic = diagnostics.Single(d => string.Equals(
            d.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.DiagnosticId,
            StringComparison.Ordinal));

        diagnostic.Tags.ShouldNotBeNull();
        diagnostic.Tags!.ShouldContain(DiagnosticTag.Unnecessary);
        diagnostic.Severity.ShouldBe(LspDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_TagsUnusedParameterAsUnnecessaryAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class C {
    public func M(value: int) -> unit {
    }
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new UnusedParameterAnalyzer()))).ShouldBeTrue();
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        var diagnostic = diagnostics.Single(d => string.Equals(
            d.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
            StringComparison.Ordinal));

        diagnostic.Tags.ShouldNotBeNull();
        diagnostic.Tags!.ShouldContain(DiagnosticTag.Unnecessary);
        diagnostic.Severity.ShouldBe(LspDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_AfterConstructorAssignmentEdit_ReportsOnlyActuallyUnusedParameterAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class UiStackPanel {
}

class UiWindow {
    private val title: string

    init(content: UiStackPanel, title: string) {
        Content = content
        self.title = title
    }

    val Content: UiStackPanel
    val Title: string => title
}
""";
        var updatedCode = code.Replace("        self.title = title\n", string.Empty, StringComparison.Ordinal);

        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new UnusedParameterAnalyzer()))).ShouldBeTrue();
        var beforeDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        beforeDiagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
            StringComparison.Ordinal)).ShouldBeFalse();

        await store.UpsertDocumentAsync(uri, updatedCode);
        var afterDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        var parameterDiagnostics = afterDiagnostics
            .Where(diagnostic => string.Equals(
                diagnostic.Code?.String,
                Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
                StringComparison.Ordinal))
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();

        parameterDiagnostics.ShouldBe(["Parameter 'title' is never used."]);

        await store.UpsertDocumentAsync(uri, updatedCode);
        var afterSaveLikeDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        var saveLikeParameterDiagnostics = afterSaveLikeDiagnostics
            .Where(diagnostic => string.Equals(
                diagnostic.Code?.String,
                Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
                StringComparison.Ordinal))
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();

        saveLikeParameterDiagnostics.ShouldBe(["Parameter 'title' is never used."]);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_AfterNamespaceFunctionInvocationEdit_ReportsNoOverloadAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "src", "main.rvn"), string.Empty);
        File.WriteAllText(Path.Combine(_tempRoot, "src", "test.rvn"), string.Empty);
        WriteProject(_tempRoot, "Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="src/**/*.rvn" /></ItemGroup>
            </Project>
            """);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var sourceRoot = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceRoot);

        var mainPath = Path.Combine(sourceRoot, "main.rvn");
        var testPath = Path.Combine(sourceRoot, "test.rvn");
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        const string validMain = """
import Utilities.*

func Main() {
    let x = A(42)
    A(x)
}
""";
        const string invalidMain = """
import Utilities.*

func Main() {
    let x = A(42)
    A()
}
""";
        const string utilities = """
namespace Utilities

public func A(x: int) -> int {
    return 42 + x
}
""";

        await store.UpsertDocumentAsync(testUri, utilities);
        await store.UpsertDocumentAsync(mainUri, validMain);

        var initialDiagnostics = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        initialDiagnostics.WasSkipped.ShouldBeFalse();
        initialDiagnostics.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV1501", StringComparison.Ordinal));

        await store.UpsertDocumentAsync(mainUri, invalidMain);

        var updatedDiagnostics = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        updatedDiagnostics.WasSkipped.ShouldBeFalse();
        var updatedDiagnosticSummary = string.Join(
            Environment.NewLine,
            updatedDiagnostics.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code?.String}: {diagnostic.Message}"));
        updatedDiagnostics.Diagnostics.Any(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV1501", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("A", StringComparison.Ordinal))
            .ShouldBeTrue(updatedDiagnosticSummary);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_AfterCrossFileDeclarationEdit_RemovesNotInScopeDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        File.WriteAllText(Path.Combine(_tempRoot, "src", "main.rvn"), string.Empty);
        File.WriteAllText(Path.Combine(_tempRoot, "src", "test.rvn"), string.Empty);
        WriteProject(_tempRoot, "Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="src/**/*.rvn" /></ItemGroup>
            </Project>
            """);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var sourceRoot = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceRoot);

        var mainPath = Path.Combine(sourceRoot, "main.rvn");
        var testPath = Path.Combine(sourceRoot, "test.rvn");
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        const string main = """
import Utilities.*

func Main() {
    use test = Test2()
    test.Dispose()
}
""";
        const string incompleteDeclaration = """
namespace Utilities

import System.Console.*

func Test() {
    WriteLine("Hello")
}

func A(x: int) -> int {
    42 + x
}

func A(x: string) -> int {
    42 + int.Parse(x)
}
""";
        const string fixedDeclaration = """
namespace Utilities

import System.Console.*

func Test() {
    WriteLine("Hello")
}

func A(x: int) -> int {
    42 + x
}

func A(x: string) -> int {
    42 + int.Parse(x)
}

func Test2() -> IDisposable {
    return default!
}
""";

        await store.UpsertDocumentAsync(testUri, incompleteDeclaration);
        await store.UpsertDocumentAsync(mainUri, main);

        var initialDiagnostics = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        initialDiagnostics.WasSkipped.ShouldBeFalse();
        initialDiagnostics.Diagnostics.Any(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Test2", StringComparison.Ordinal))
            .ShouldBeTrue();

        await store.UpsertDocumentAsync(testUri, fixedDeclaration);

        var updatedDiagnostics = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        updatedDiagnostics.WasSkipped.ShouldBeFalse();
        var updatedDiagnosticSummary = string.Join(
            Environment.NewLine,
            updatedDiagnostics.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code?.String}: {diagnostic.Message}"));
        updatedDiagnostics.Diagnostics.Any(diagnostic =>
            diagnostic.Code.HasValue &&
            string.Equals(diagnostic.Code.Value.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Test2", StringComparison.Ordinal))
            .ShouldBeFalse(updatedDiagnosticSummary);
    }

    [Fact]
    public async Task GetDocumentWithAnalyzersDiagnosticsAsync_AfterConstructorAssignmentEdit_ReportsOnlyActuallyUnusedParameterAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class UiStackPanel {
}

class UiWindow {
    private val title: string

    init(content: UiStackPanel, title: string) {
        Content = content
        self.title = title
    }

    val Content: UiStackPanel
    val Title: string => title
}

class UiPanel {
    private val spacing: double

    init(children: UiStackPanel, spacing: double) {
        Content = children
        self.spacing = spacing
    }

    val Content: UiStackPanel
}
""";
        var updatedCode = code.Replace("        self.title = title\n", string.Empty, StringComparison.Ordinal);

        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new UnusedParameterAnalyzer()))).ShouldBeTrue();
        var beforeResult = await store.TryGetDocumentWithAnalyzersDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None);

        beforeResult.WasSkipped.ShouldBeFalse();
        beforeResult.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
            StringComparison.Ordinal)).ShouldBeFalse();

        await store.UpsertDocumentAsync(uri, updatedCode);
        var afterResult = await store.TryGetDocumentWithAnalyzersDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None);

        afterResult.WasSkipped.ShouldBeFalse();
        var parameterDiagnostics = afterResult.Diagnostics
            .Where(diagnostic => string.Equals(
                diagnostic.Code?.String,
                Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId,
                StringComparison.Ordinal))
            .Select(static diagnostic => diagnostic.Message)
            .ToArray();

        parameterDiagnostics.ShouldBe(["Parameter 'title' is never used."]);
    }

    [Fact]
    public async Task GetDocumentWithAnalyzersDiagnosticsAsync_DoesNotHoldSemanticGateWhileSymbolAnalyzerRunsAsync()
    {
        BlockingMethodSymbolAnalyzer.Reset();
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class C {
    public func M() -> unit { }
}
""";
        await store.UpsertDocumentAsync(uri, code);

        manager.TryGetDocument(uri, out var document).ShouldBeTrue();
        document.ShouldNotBeNull();

        var project = workspace.CurrentSolution.GetProject(document.Project.Id)!;
        project = project.AddAnalyzerReference(new AnalyzerReference(new BlockingMethodSymbolAnalyzer()));
        workspace.TryApplyChanges(project.Solution);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var diagnosticsTask = Task.Run(
            () => store.TryGetDocumentWithAnalyzersDiagnosticsAsync(
                uri,
                shouldSkipWork: null,
                timeout.Token),
            timeout.Token);

        try
        {
            BlockingMethodSymbolAnalyzer.Entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();

            var semanticAccess = await store.TryEnterExistingDocumentSemanticModelAccessAsync(uri, timeout.Token, "test");
            semanticAccess.ShouldNotBeNull();
            semanticAccess.Dispose();
        }
        finally
        {
            BlockingMethodSymbolAnalyzer.Release.Set();
        }

        var result = await diagnosticsTask.WaitAsync(timeout.Token);
        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Count(diagnostic => string.Equals(diagnostic.Code?.String, "ANLSP001", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_TagsUnusedLocalFunctionAsUnnecessaryAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
let c = C()
c.M()

class C {
    func M() -> () {
        func Helper() -> () { }
    }
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new UnusedMethodAnalyzer()))).ShouldBeTrue();
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Count(d => string.Equals(
            d.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedMethodAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBe(1);

        var diagnostic = diagnostics.Single(d =>
            string.Equals(d.Code?.String, Raven.CodeAnalysis.Diagnostics.UnusedMethodAnalyzer.DiagnosticId, StringComparison.Ordinal));

        diagnostic.Tags.ShouldNotBeNull();
        diagnostic.Tags!.ShouldContain(DiagnosticTag.Unnecessary);
        diagnostic.Severity.ShouldBe(LspDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentWithAnalyzersLane_IncludesBuiltInAnalyzerDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func test() -> int {
    42
}

func Main() -> unit {
    test()
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var project = manager.GetProjectsSnapshot().Single();
        var compilationOptions = (project.CompilationOptions ?? new CompilationOptions(OutputKind.ConsoleApplication))
            .WithReturnedValueHandlingMode(ReturnedValueHandlingMode.Full);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithCompilationOptions(project.Id, compilationOptions)).ShouldBeTrue();

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.UnusedExpressionResultAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentWithAnalyzersLane_SkipsWhenAnalyzerSemanticGateIsBusyAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    var count = 0
    fb
}
""";
        await store.UpsertDocumentAsync(uri, code);

        var compilerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        compilerResult.WasSkipped.ShouldBeFalse();
        compilerResult.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            "RAV0103",
            StringComparison.Ordinal)).ShouldBeTrue();

        using var semanticAccess = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var analyzerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        analyzerResult.WasSkipped.ShouldBeTrue();
        analyzerResult.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_BackgroundDocumentWithAnalyzersLane_SkipsUntilCompilerDiagnosticsAreCachedAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    var count = 0
    count
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new VarCanBeLetAnalyzer()))).ShouldBeTrue();

        var firstAnalyzerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: () => false,
            cancellationToken: CancellationToken.None);

        firstAnalyzerResult.WasSkipped.ShouldBeTrue();
        firstAnalyzerResult.Diagnostics.ShouldBeEmpty();

        var compilerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: () => false,
            cancellationToken: CancellationToken.None);
        compilerResult.WasSkipped.ShouldBeFalse();

        var secondAnalyzerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: () => false,
            cancellationToken: CancellationToken.None);

        secondAnalyzerResult.WasSkipped.ShouldBeFalse();
        secondAnalyzerResult.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.VarCanBeLetAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentWithAnalyzersLane_ReusesCachedAnalyzerDiagnosticsWhenSemanticGateIsBusyAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    var count = 0
    count
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new VarCanBeLetAnalyzer()))).ShouldBeTrue();

        var firstResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        firstResult.WasSkipped.ShouldBeFalse();
        firstResult.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.VarCanBeLetAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBeTrue();

        using var semanticAccess = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var secondResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        secondResult.WasSkipped.ShouldBeFalse();
        secondResult.Diagnostics.Select(static diagnostic => diagnostic.Message)
            .ShouldBe(firstResult.Diagnostics.Select(static diagnostic => diagnostic.Message), ignoreOrder: true);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentWithAnalyzersLane_SkipsWhenCompilerDiagnosticsNeedBusySemanticGateAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    var count = 0
    fb
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var semanticAccess = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var analyzerResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        analyzerResult.WasSkipped.ShouldBeTrue();
        analyzerResult.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentWithAnalyzersLane_DoesNotReuseStaleCompilerDiagnosticsAfterProjectChangeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(Path.Combine(_tempRoot, "main.rvn"), string.Empty);
        File.WriteAllText(Path.Combine(_tempRoot, "test.rvn"), string.Empty);
        WriteProject(_tempRoot, "Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Include="*.rvn" /></ItemGroup>
            </Project>
            """);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainPath = Path.Combine(_tempRoot, "main.rvn");
        var testPath = Path.Combine(_tempRoot, "test.rvn");
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        const string mainCode = """
func Main() -> () {
    var count = 0
    Test()
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(mainUri, mainCode);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new VarCanBeLetAnalyzer()))).ShouldBeTrue();

        var compilerResult = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        compilerResult.WasSkipped.ShouldBeFalse();
        compilerResult.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Test", StringComparison.Ordinal)).ShouldBeTrue();

        var addedDocument = await store.UpsertDocumentAsync(testUri, """
public func Test() -> () {
}
""");

        addedDocument.Project.Id.ShouldBe(authoredDocument.Project.Id);

        var analyzerResult = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        analyzerResult.WasSkipped.ShouldBeFalse();
        analyzerResult.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Test", StringComparison.Ordinal)).ShouldBeFalse();
        analyzerResult.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.VarCanBeLetAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDocumentAnalyzerDiagnostics_WithCapturedSnapshot_SurvivesWorkspaceProjectReplacementAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    var count = 0
}
""";
        await store.UpsertDocumentAsync(uri, code);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        workspace.TryApplyChanges(
            workspace.CurrentSolution.RemoveProject(context.Value.Document.Project.Id)).ShouldBeTrue();

        var succeeded = false;
        Should.NotThrow(() => succeeded = manager.TryGetDocumentAnalyzerDiagnostics(
            context.Value.Document,
            context.Value.Compilation,
            out _,
            cancellationToken: CancellationToken.None));
        succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectWithAnalyzersLane_IncludesBuiltInAnalyzerDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    var count = 0
    count
}
""";
        var authoredDocument = await store.UpsertDocumentAsync(uri, code);
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(
            authoredDocument.Project.Id, new AnalyzerReference(new VarCanBeLetAnalyzer()))).ShouldBeTrue();
        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.ProjectWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic => string.Equals(
            diagnostic.Code?.String,
            Raven.CodeAnalysis.Diagnostics.VarCanBeLetAnalyzer.DiagnosticId,
            StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentCompilerLane_AfterInlayHints_DoesNotPublishStalePipeLocalDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*
import System.Linq.*
import System.Collections.Generic.*
import System.Linq.Expressions.*

class User(var Name: string, var Age: int, var IsActive: bool)

func Main(users: IQueryable<User>) {
    let minAge = 21
    let onlyActiveAdults: Expression<System.Func<User, bool>> =
        user => user.IsActive && user.Age >= minAge

    let query = users
        |> Where(onlyActiveAdults)
        |> OrderBy(user => user.Name)
        |> Select(user => user.Name)
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var beforeInlayResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        var beforeInlayErrors = beforeInlayResult.Diagnostics
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray();
        beforeInlayErrors.ShouldBeEmpty(string.Join(Environment.NewLine, beforeInlayErrors));

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("'onlyActiveAdults' is not in scope", StringComparison.Ordinal));
        var errorDiagnostics = result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray();
        errorDiagnostics.ShouldBeEmpty(string.Join(Environment.NewLine, errorDiagnostics));
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentCompilerLane_AfterInlayHints_DoesNotPublishStaleAsyncLambdaBodyDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*
import System.Threading.Tasks.*

class RequestContext {
    public val Text: string = "body"
}

func Main() -> unit {
    Accept(async func (context: RequestContext) {
        let content = await Task.FromResult(context.Text)
        return "submitted: $content"
    })
}

func Accept(handler: func (RequestContext) -> Task<string>) -> unit { }
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("'context' is not in scope", StringComparison.Ordinal));
        result.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error &&
            (string.Equals(diagnostic.Code?.String, "RAV2700", StringComparison.Ordinal) ||
             string.Equals(diagnostic.Code?.String, "RAV1900", StringComparison.Ordinal) ||
             string.Equals(diagnostic.Code?.String, "RAV2705", StringComparison.Ordinal))).ShouldBeFalse();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentCompilerLane_ReportsTypedDeconstructionMismatchOnAnnotationSpanAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Collections.Immutable.*

let doubled: ImmutableDictionary<string, int> = ["two": 4]

for let (key: string, value: string) in doubled {
    _ = key
    _ = value
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        var valueTypeStart = code.IndexOf("value: string", StringComparison.Ordinal) + "value: ".Length;
        var expectedRange = PositionHelper.ToRange(sourceText, new CodeTextSpan(valueTypeStart, "string".Length));

        result.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV1503", StringComparison.Ordinal) &&
            diagnostic.Range.Start == expectedRange.Start &&
            diagnostic.Range.End == expectedRange.End).ShouldBeTrue();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_TopLevelGenericWhereClauseSelfConstraint_DoesNotReportTypeParameterOutOfScopeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*
import System.Console.*

func Main() -> () {
    let r = Parse<int>("42")
    WriteLine(r)
}

func Parse<T>(str: string) -> T
    where T: IParsable<T>
    => T.Parse(str, null)
""";
        await store.UpsertDocumentAsync(uri, code);
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ProjectBackedNamespaceMembers_AfterHover_DoesNotReportMissingImportedMemberAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "members.rvn"), """
namespace Utilities

public const Answer: int = 41

public func AddOne(value: int) -> int => value + 1
""");

        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string code = """
import Utilities.*

func Run() -> int {
    return AddOne(Answer)
}
""";
        File.WriteAllText(documentPath, code);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var semanticModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);
        semanticModel.ShouldNotBeNull();
        var invocation = context.Value.SyntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "AddOne" });
        semanticModel.GetSymbolInfo(invocation).Symbol.ShouldNotBeNull();

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        var addOneOffset = code.IndexOf("AddOne", StringComparison.Ordinal);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new CodeTextSpan(addOneOffset + 1, 0)).Start
        }, CancellationToken.None);
        hover.ShouldNotBeNull();

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        diagnostics
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray()
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectBackedNamespaceMemberImport_InLocalInitializer_DoesNotReportMissingImportedMemberAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "test.rvn"), """
namespace Utilities

func Test() {
}

func A(x: int) -> int {
    42
}
""");

        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string code = """
import Utilities.*

func Main() {
    Test()
    let x = A(42)
    Test()
    A(42)
}
""";
        File.WriteAllText(documentPath, code);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var diagnostics = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        diagnostics.Diagnostics
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray()
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectBackedNamespaceMemberEdit_InvalidatesDependentDocumentAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        var membersPath = Path.Combine(sourceDirectory, "members.rvn");
        const string initialMembers = """
namespace Utilities

public const Prefix: string = "ready"
""";
        File.WriteAllText(membersPath, initialMembers);

        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string code = """
import Utilities.*

func Run() -> string {
    return Format()
}
""";
        File.WriteAllText(documentPath, code);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var membersUri = DocumentUri.FromFileSystemPath(membersPath);
        await store.UpsertDocumentAsync(uri, code);

        var initialResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        initialResult.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Format", StringComparison.Ordinal)).ShouldBeTrue();
        await store.UpsertDocumentAsync(membersUri, """
namespace Utilities

public const Prefix: string = "ready"

public func Format() -> string => Prefix
""");

        var updatedResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        updatedResult.WasSkipped.ShouldBeFalse();
        updatedResult.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Format", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectBackedReopen_ResolvesAlreadyIncludedCrossFileFunctionAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);

        var mainPath = Path.Combine(sourceDirectory, "main.rvn");
        const string mainCode = "Test()";
        File.WriteAllText(mainPath, mainCode);

        var testPath = Path.Combine(sourceDirectory, "test.rvn");
        const string testCode = """
func Test() {
}
""";
        File.WriteAllText(testPath, testCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);

        var result = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        result.WasSkipped.ShouldBeFalse();
        HasNotInScopeDiagnostic(result.Diagnostics, "Test").ShouldBeFalse();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_LooseFileOutsideProjectGlob_DoesNotAffectProjectDiagnosticsUntilOpenedAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);

        var mainPath = Path.Combine(sourceDirectory, "main.rvn");
        const string mainCode = "Test()";
        File.WriteAllText(mainPath, mainCode);

        var looseTestPath = Path.Combine(_tempRoot, "test.rvn");
        const string testCode = """
func Test() {
}
""";
        File.WriteAllText(looseTestPath, testCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);

        var result = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        result.WasSkipped.ShouldBeFalse();
        HasNotInScopeDiagnostic(result.Diagnostics, "Test").ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_LooseFileOpenedAsFileApplication_DoesNotAffectProjectDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);

        var mainPath = Path.Combine(sourceDirectory, "main.rvn");
        const string mainCode = "Test()";
        File.WriteAllText(mainPath, mainCode);

        var looseTestPath = Path.Combine(_tempRoot, "test.rvn");
        const string testCode = """
func Test() {
}
""";
        File.WriteAllText(looseTestPath, testCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);

        var initialResult = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        initialResult.WasSkipped.ShouldBeFalse();
        HasNotInScopeDiagnostic(initialResult.Diagnostics, "Test").ShouldBeTrue();

        var looseTestUri = DocumentUri.FromFileSystemPath(looseTestPath);
        await store.UpsertDocumentAsync(looseTestUri, testCode);
        store.GetOpenDocumentUrisInSameProject(looseTestUri, excludeSelf: true)
            .ShouldNotContain(mainUri);

        var updatedResult = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        updatedResult.WasSkipped.ShouldBeFalse();
        HasNotInScopeDiagnostic(updatedResult.Diagnostics, "Test").ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_StandaloneFilesUseSeparateApplicationProjectsAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var mainPath = Path.Combine(_tempRoot, "main.rvn");
        const string mainCode = "Test()";
        File.WriteAllText(mainPath, mainCode);

        var otherPath = Path.Combine(_tempRoot, "other.rvn");
        const string otherCode = """
func Test() {
}
""";
        File.WriteAllText(otherPath, otherCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var otherUri = DocumentUri.FromFileSystemPath(otherPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);
        await store.UpsertDocumentAsync(otherUri, otherCode);

        store.GetOpenDocumentUrisInSameProject(mainUri, excludeSelf: true)
            .ShouldNotContain(otherUri);

        var result = await store.TryGetDiagnosticsAsync(
            mainUri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);
        result.WasSkipped.ShouldBeFalse();
        HasNotInScopeDiagnostic(result.Diagnostics, "Test").ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_StandaloneFileUsesStandardLibrariesAndDefaultPreludeAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var sourcePath = Path.Combine(_tempRoot, "main.rvn");
        const string source = """
#!/usr/bin/env rvn

import Raven.Macros.*

func Main(args: string[]) {
    Console.WriteLine("Hello from Raven!")

    for argument in args {
        Console.WriteLine("Argument: ${argument}")
    }

    let syntax = quote! { 40 + 2 }
}

func CreateResult() -> Result<int, string> => Ok(42)
""";
        File.WriteAllText(sourcePath, source);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        await store.UpsertDocumentAsync(uri, source);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error);
        var project = workspace.CurrentSolution.Projects.Single();
        project.Documents
            .ShouldContain(document => document.Name.EndsWith(".Prelude.g.rvn", StringComparison.Ordinal));
        project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .ShouldContain(reference => string.Equals(
                Path.GetFileName(reference.FilePath),
                "Raven.Core.dll",
                StringComparison.OrdinalIgnoreCase));
        project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .ShouldContain(reference => string.Equals(
                Path.GetFileName(reference.FilePath),
                "Raven.Macros.dll",
                StringComparison.OrdinalIgnoreCase));
        workspace.GetCompilation(project.Id).MacroReferences
            .ShouldContain(reference => string.Equals(
                Path.GetFileName(reference.Display),
                "Raven.Macros.dll",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StandaloneFile_CloseRemovesEphemeralProjectAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var sourcePath = Path.Combine(_tempRoot, "main.rvn");
        const string source = """Console.WriteLine("Hello")""";
        File.WriteAllText(sourcePath, source);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        await store.UpsertDocumentAsync(uri, source);

        workspace.CurrentSolution.Projects.Count().ShouldBe(1);
        store.RemoveDocument(uri).ShouldBeTrue();
        workspace.CurrentSolution.Projects.ShouldBeEmpty();

        await store.UpsertDocumentAsync(uri, source);
        workspace.CurrentSolution.Projects.Count().ShouldBe(1);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentCompilerLane_TopLevelFileprivateConstAfterHover_ReportsInaccessibleAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "members.rvn"), """
namespace Hidden

fileprivate const Secret: int = 1

public func SameFile() -> int {
    return Secret
}
""");

        var documentPath = Path.Combine(sourceDirectory, "main.rvn");
        const string code = """
namespace Hidden

public func OtherFile() -> int {
    return Secret
}
""";
        File.WriteAllText(documentPath, code);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        var secretOffset = code.IndexOf("Secret", StringComparison.Ordinal);
        _ = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new CodeTextSpan(secretOffset + 1, 0)).Start
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0500", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ProjectBackedDocument_DoesNotReportNamespaceSegmentOutOfScopeForImportedFrameworkNamespacesAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*
import System.Console.*
import System.Linq.*
import System.Collections.Generic.*

func Main() {
    let test = MyResult(List<string>())
}

union MyResult<T>(List<T> | int)
""";
        await store.UpsertDocumentAsync(uri, code);
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("Linq", StringComparison.Ordinal))
            .ShouldBeFalse();
        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("Generic", StringComparison.Ordinal))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_AspNetMinimalApiSample_DoesNotReportBogusOutOfScopeDiagnosticsAsync()
    {
        var sampleRoot = Path.Combine(
            GetRepositoryRoot(),
            "samples",
            "projects",
            "aspnet-minimal-api");
        var documentPath = Path.Combine(sampleRoot, "src", "Program.rvn");

        Directory.Exists(sampleRoot).ShouldBeTrue();
        File.Exists(documentPath).ShouldBeTrue();

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "aspnet-minimal-api",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        var initialCode = code.Replace("use app = builder.Build()", "let app = builder.Build()", StringComparison.Ordinal);
        initialCode.ShouldNotBe(code);

        await store.UpsertDocumentAsync(uri, initialCode);

        var initialSourceText = Raven.CodeAnalysis.Text.SourceText.From(initialCode);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(initialSourceText, new CodeTextSpan(0, initialSourceText.Length))
        }, CancellationToken.None);

        await store.UpsertDocumentAsync(uri, code);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        foreach (var name in new[]
                 {
                     "Generic",
                     "AspNetCore",
                     "Builder",
                     "Http",
                     "Mvc",
                     "Tasks",
                     "encoding",
                     "detectEncodingFromByteOrderMarks",
                     "bufferSize",
                     "leaveOpen",
                     "app"
                 })
        {
            diagnostics.Any(diagnostic =>
                    string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                    diagnostic.Message.Contains(name, StringComparison.Ordinal))
                .ShouldBeFalse();
        }

        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("'context' is not in scope", StringComparison.Ordinal))
            .ShouldBeFalse();
        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV2700", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code?.String, "RAV1900", StringComparison.Ordinal))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_EfCoreContractsSample_AfterInlayHints_DoesNotReportBogusMissingMemberDiagnosticsAsync()
    {
        var sampleRoot = Path.Combine(
            GetRepositoryRoot(),
            "samples",
            "projects",
            "efcore-vehicle-costs");
        var documentPath = Path.Combine(sampleRoot, "src", "Contracts", "Contracts.rvn");

        Directory.Exists(sampleRoot).ShouldBeTrue();
        File.Exists(documentPath).ShouldBeTrue();

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-vehicle-costs",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0117", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("'VehicleStatusDto' has no member 'ToStatus'", StringComparison.Ordinal))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_EfCoreSeedSample_WhenOpenedFromRepositoryRoot_DoesNotReportBogusMissingVehicleDbContextAsync()
    {
        var repositoryRoot = GetRepositoryRoot();
        var documentPath = Path.Combine(
            repositoryRoot,
            "samples",
            "projects",
            "efcore-vehicle-costs",
            "src",
            "Data",
            "Seed.rvn");

        Directory.Exists(repositoryRoot).ShouldBeTrue();
        File.Exists(documentPath).ShouldBeTrue();

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "Raven",
                Uri = DocumentUri.FromFileSystemPath(repositoryRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var result = await store.TryGetDocumentCompilerDiagnosticsAsync(
            uri,
            shouldSkipWork: null,
            CancellationToken.None);
        result.WasSkipped.ShouldBeFalse();
        var diagnostics = result.Diagnostics;

        diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("'VehicleDbContext' is not in scope", StringComparison.Ordinal))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_FileScopedNamespaceImportCoveredByPrelude_ReportsRedundantImportAsync()
    {
        _ = WriteProject(
            _tempRoot,
            "App",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="src/**/*.rvn" />
              </ItemGroup>
            </Project>
            """);

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);
        var documentPath = Path.Combine(sourceDirectory, "Members.rvn");
        await File.WriteAllTextAsync(
            documentPath,
            """
            namespace Samples.Members

            import System.*

            public func Format(value: int) -> string => value.ToString()
            """);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        var redundantImports = diagnostics
            .Where(diagnostic => string.Equals(
                diagnostic.Code?.String,
                "RAV1052",
                StringComparison.Ordinal))
            .ToArray();

        var redundantImport = redundantImports.ShouldHaveSingleItem();
        redundantImport.Severity.ShouldBe(LspDiagnosticSeverity.Hint);
        redundantImport.Tags.ShouldNotBeNull();
        redundantImport.Tags!.ShouldContain(DiagnosticTag.Unnecessary);
        redundantImport.Message.ShouldContain("System.*");
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_TopLevelGenericInvocationAfterOpenFeatures_DoesNotPublishStaleOverloadDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var semanticTokensHandler = new SemanticTokensHandler(store, NullLogger<SemanticTokensHandler>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Text.Json.*

let foo = Foo("Marina")
let options = JsonSerializerOptions()
let str = JsonSerializer.Serialize(foo, options)
let obj = JsonSerializer.Deserialize<Foo>(str, options)

record Foo(Name: string)
""";
        await store.UpsertDocumentAsync(uri, code);
        _ = await semanticTokensHandler.Handle(new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        }, CancellationToken.None);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV1501", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("Deserialize", StringComparison.Ordinal))
            .ShouldBeFalse();
        result.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("'str' is not in scope", StringComparison.Ordinal))
            .ShouldBeFalse();
        result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_InvalidTopLevelGenericInvocationAfterOpenFeatures_PublishesOverloadDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var semanticTokensHandler = new SemanticTokensHandler(store, NullLogger<SemanticTokensHandler>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Text.Json.*

let foo = Foo("Marina")
let options = JsonSerializerOptions()
let str = JsonSerializer.Serialize(foo, options)
let obj = JsonSerializer.Deserialize<Foo>(options, str)

record Foo(Name: string)
""";
        await store.UpsertDocumentAsync(uri, code);
        _ = await semanticTokensHandler.Handle(new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        }, CancellationToken.None);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        var diagnostic = result.Diagnostics.Single(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV1501", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Deserialize", StringComparison.Ordinal));

        diagnostic.Severity.ShouldBe(LspDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_TopLevelTargetTypedUnionCaseAfterOpenFeatures_DoesNotPublishMissingTargetTypeDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var semanticTokensHandler = new SemanticTokensHandler(store, NullLogger<SemanticTokensHandler>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var hoverHandler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.*

let foo = Foo(
    Name: "Foo",
    Status: .OnMaintenance(.UtcNow, "Test"),
    Item: .Some("Foo")
)

record Foo(
    val Name: string,
    val Status: Status
    val Item: Option<string>
)

union Status {
    case Active(Date: DateTimeOffset)
    case OnMaintenance(Date: DateTimeOffset, Reason: string)
}
""";
        await store.UpsertDocumentAsync(uri, code);
        _ = await semanticTokensHandler.Handle(new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        }, CancellationToken.None);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var onMaintenanceOffset = code.IndexOf("OnMaintenance", StringComparison.Ordinal);
        onMaintenanceOffset.ShouldBeGreaterThanOrEqualTo(0);
        var hover = await hoverHandler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new CodeTextSpan(onMaintenanceOffset + 1, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV2010", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("OnMaintenance", StringComparison.Ordinal))
            .ShouldBeFalse();
        result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_UnionAttributeAfterInlayHints_DoesNotUseSynthesizedMethodOwnerAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Text.Json.Serialization.*

[JsonConverter(typeof(JsonConverterFactory))]
union JsonValue(string | double | bool | JsonValue[])
""";
        await store.UpsertDocumentAsync(uri, code);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0502", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("target 'method'", StringComparison.Ordinal))
            .ShouldBeFalse();
        result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_TestCaseSample_AfterInlayHints_DoesNotReportSelfShadowingDiagnosticsAsync()
    {
        var sampleRoot = Path.Combine(
            GetRepositoryRoot(),
            "samples",
            "projects",
            "test-case");
        var documentPath = Path.Combine(sampleRoot, "src", "Program.rvn");

        Directory.Exists(sampleRoot).ShouldBeTrue();
        File.Exists(documentPath).ShouldBeTrue();

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "test-case",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var sourceText = Raven.CodeAnalysis.Text.SourceText.From(code);
        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new CodeTextSpan(0, sourceText.Length))
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        var shadowDiagnostics = result.Diagnostics
            .Where(diagnostic => string.Equals(diagnostic.Code?.String, "RAV0168", StringComparison.Ordinal))
            .Select(diagnostic => $"{diagnostic.Message} {diagnostic.Range.Start.Line + 1}:{diagnostic.Range.Start.Character + 1}")
            .ToArray();
        shadowDiagnostics.ShouldBeEmpty();

        var missingPatternLocalDiagnostics = result.Diagnostics
            .Where(diagnostic =>
                string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
                (diagnostic.Message.Contains("'entry' is not in scope", StringComparison.Ordinal) ||
                 diagnostic.Message.Contains("'id' is not in scope", StringComparison.Ordinal) ||
                 diagnostic.Message.Contains("'name' is not in scope", StringComparison.Ordinal)))
            .Select(diagnostic => $"{diagnostic.Message} {diagnostic.Range.Start.Line + 1}:{diagnostic.Range.Start.Character + 1}")
            .ToArray();
        missingPatternLocalDiagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_FulfillmentWorkflowSample_AfterVisibleInlayHints_DoesNotReportTaskPropagationDiagnosticAsync()
    {
        var sampleRoot = Path.Combine(
            GetRepositoryRoot(),
            "samples",
            "projects",
            "fulfillment-workflow");
        var documentPath = Path.Combine(sampleRoot, "src", "FulfillmentWorkflow.rvn");

        Directory.Exists(sampleRoot).ShouldBeTrue();
        File.Exists(documentPath).ShouldBeTrue();

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "fulfillment-workflow",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var inlayHandler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);

        var beforeInlayResult = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        beforeInlayResult.WasSkipped.ShouldBeFalse();
        var beforeInlayErrors = beforeInlayResult.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray();

        beforeInlayErrors.ShouldBeEmpty();

        _ = await inlayHandler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
            {
                Start = new Position(0, 0),
                End = new Position(25, 0)
            }
        }, CancellationToken.None);

        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code?.String}: {diagnostic.Message}")
            .ToArray();

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ProjectBackedDocument_VarValToggle_RecomputesReadOnlyAssignmentDiagnosticAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);

        var mutableSource = """
import System.Collections.Immutable.*

var people = [
    Person("Alice", 25, [])
]

people = people.Add(Person("Test", 30, []))

record Person(
    val Name: string
    val Age: int
    val Items: string[]
)
""";

        var readOnlySource = mutableSource.Replace("var people", "let people", StringComparison.Ordinal);
        await store.UpsertDocumentAsync(uri, mutableSource);
        var mutableDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        mutableDiagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, ThisValueIsNotMutableDiagnosticId, StringComparison.Ordinal)).ShouldBeFalse();
        await store.UpsertDocumentAsync(uri, readOnlySource);
        var readOnlyDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        readOnlyDiagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, ThisValueIsNotMutableDiagnosticId, StringComparison.Ordinal)).ShouldBeTrue();
        await store.UpsertDocumentAsync(uri, mutableSource);
        var mutableAgainDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        mutableAgainDiagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, ThisValueIsNotMutableDiagnosticId, StringComparison.Ordinal)).ShouldBeFalse();
        await store.UpsertDocumentAsync(uri, readOnlySource);
        var readOnlyAgainDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        readOnlyAgainDiagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, ThisValueIsNotMutableDiagnosticId, StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ProjectBackedDocument_RecoversAfterTransientParseFailureAndReportsReadOnlyAssignmentAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);

        var invalidSource = """
let flag = true and
""";
        await store.UpsertDocumentAsync(uri, invalidSource);
        var invalidDiagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        invalidDiagnostics.ShouldNotBeNull();

        var readOnlySource = """
import System.Collections.Immutable.*

let people = [
    Person("Alice", 25, [])
]

people = people.Add(Person("Test", 30, []))

record Person(
    val Name: string
    val Age: int
    val Items: string[]
)
""";
        await store.UpsertDocumentAsync(uri, readOnlySource);
        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);

        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, ThisValueIsNotMutableDiagnosticId, StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectWithAnalyzersLane_DoesNotRequireDocumentSemanticGateAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    fb
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var heldLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var diagnosticsTask = store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.ProjectWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        var completedTask = await Task.WhenAny(diagnosticsTask, Task.Delay(1000));
        completedTask.ShouldBe(diagnosticsTask);

        var result = await diagnosticsTask;
        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_ProjectWithAnalyzersLane_SkipsWhenCompilerGateIsBusyAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    fb
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var heldLease = await store.EnterCompilerAccessAsync(CancellationToken.None, "test", uri);
        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.ProjectWithAnalyzers,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_DocumentCompilerLane_SkipsWhenDocumentSemanticGateIsBusyAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> () {
    fb
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var semanticAccess = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var result = await store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.DocumentCompiler,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        result.WasSkipped.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryGetDiagnosticsAsync_SyntaxLane_DoesNotRequireCompilerGateAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main( -> () {
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var heldLease = await store.EnterCompilerAccessAsync(CancellationToken.None, "test", uri);
        var diagnosticsTask = store.TryGetDiagnosticsAsync(
            uri,
            DocumentStore.DiagnosticLane.Syntax,
            shouldSkipWork: null,
            cancellationToken: CancellationToken.None);

        var completedTask = await Task.WhenAny(diagnosticsTask, Task.Delay(1000));
        completedTask.ShouldBe(diagnosticsTask);

        var result = await diagnosticsTask;
        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task UpsertDocumentWithResultAsync_ProjectBackedExistingDocumentWithSameText_DoesNotReportProjectChangeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var sourceDirectory = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceDirectory);

        var mainPath = Path.Combine(sourceDirectory, "main.rvn");
        const string mainCode = """
import Utilities.*

func Main() {
    Test()
}
""";
        File.WriteAllText(mainPath, mainCode);

        var testPath = Path.Combine(sourceDirectory, "test.rvn");
        const string testCode = """
namespace Utilities

func Test() {
}
""";
        File.WriteAllText(testPath, testCode);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        await store.UpsertDocumentWithResultAsync(mainUri, SourceText.From(mainCode));

        var testUri = DocumentUri.FromFileSystemPath(testPath);
        var result = await store.UpsertDocumentWithResultAsync(testUri, SourceText.From(testCode));

        result.TextChanged.ShouldBeFalse();
        result.ProjectChanged.ShouldBeFalse();
        result.AddedDocument.ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string WriteProject(string directory, string name, string contents)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.rvnproj");
        File.WriteAllText(path, contents);
        return path;
    }

    private static bool HasNotInScopeDiagnostic(IEnumerable<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> diagnostics, string symbolName)
        => diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains(symbolName, StringComparison.Ordinal));

    private static string GetRepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
