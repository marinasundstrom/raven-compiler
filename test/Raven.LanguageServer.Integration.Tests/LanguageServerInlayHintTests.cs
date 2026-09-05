using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class LanguageServerInlayHintTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"raven-ls-inlay-hints-{Guid.NewGuid():N}");

    [Fact]
    public void CreateRequestTrackerKey_UsesRangeScope()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_tempRoot, "main.rvn"));
        var fullDocumentRequest = new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(0, 0),
                End = new Position(100, 0)
            }
        };
        var visibleRangeRequest = new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(30, 0),
                End = new Position(45, 0)
            }
        };

        InlayHintHandler.CreateRequestTrackerKey(visibleRangeRequest)
            .ShouldNotBe(InlayHintHandler.CreateRequestTrackerKey(fullDocumentRequest));
        InlayHintHandler.CreateRequestTrackerKey(visibleRangeRequest)
            .ShouldBe(InlayHintHandler.CreateRequestTrackerKey(new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = visibleRangeRequest.Range
            }));
    }

    [Fact]
    public async Task EnterDocumentSemanticAccess_CancelsBackgroundSemanticWorkAsync()
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
    let answer = 1 + 2
}
""";
        await store.UpsertDocumentAsync(uri, code);

        using var backgroundCancellation = store.CreateBackgroundSemanticWorkCancellation(CancellationToken.None);
        backgroundCancellation.Token.IsCancellationRequested.ShouldBeFalse();

        using var semanticAccess = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "hover");

        backgroundCancellation.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_InferredLocalsAndImplicitUnitReturns_ProvidesSourceApplicableTypeHintsAsync()
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
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var handler = new InlayHintHandler(store, dispatcher, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class Calculator {
    func AddOne(value: int) {
        return value + 1
    }
}

func Add(left: int, right: int) {
    return left + right
}

func Main() -> unit {
    let answer = 1 + 2
    let explicit: int = 3
    let name = "Raven"
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);
        var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
        var delta = SemanticQueryInstrumentation.Subtract(after, before);

        var hints = result.ToArray();
        hints.Select(static hint => hint.Label.String).ShouldContain(" -> ()");
        hints.Select(static hint => hint.Label.String).ShouldContain(": int");
        hints.Select(static hint => hint.Label.String).ShouldContain(": string");
        hints.Count(static hint => hint.Label.String == " -> ()").ShouldBe(2);
        hints.Count(static hint => hint.Label.String == ": int").ShouldBe(1);

        var methodReturnHint = AssertHasHintAtInsertion(
            sourceText,
            hints,
            code.IndexOf("AddOne(value: int)", StringComparison.Ordinal) + "AddOne(value: int)".Length,
            " -> ()");
        AssertSourceApplicable(
            sourceText,
            methodReturnHint,
            code.IndexOf("AddOne(value: int)", StringComparison.Ordinal) + "AddOne(value: int)".Length,
            " -> ()");

        var functionReturnHint = AssertHasHintAtInsertion(
            sourceText,
            hints,
            code.IndexOf("Add(left: int, right: int)", StringComparison.Ordinal) + "Add(left: int, right: int)".Length,
            " -> ()");
        AssertSourceApplicable(
            sourceText,
            functionReturnHint,
            code.IndexOf("Add(left: int, right: int)", StringComparison.Ordinal) + "Add(left: int, right: int)".Length,
            " -> ()");

        var answerHint = hints.Single(static hint => hint.Label.String == ": int");
        AssertSourceApplicable(sourceText, answerHint, code.IndexOf("answer", StringComparison.Ordinal) + "answer".Length, ": int");

        var stringHint = hints.Single(static hint => hint.Label.String == ": string");
        AssertSourceApplicable(sourceText, stringHint, code.IndexOf("name", StringComparison.Ordinal) + "name".Length, ": string");
        delta.TypeInfoBoundFallbacks.ShouldBe(0);
        delta.BoundNodeBindFallbacks.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_AfterInitializerTypeEdit_UpdatesLocalTypeHintAsync()
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
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var handler = new InlayHintHandler(store, dispatcher, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string initialCode = """
func Main() -> unit {
    let value = 1
}
""";
        const string updatedCode = """
func Main() -> unit {
    let value = "Raven"
}
""";

        await store.UpsertDocumentAsync(uri, initialCode);
        var initialSourceText = SourceText.From(initialCode);
        var initialResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(initialSourceText)
        }, CancellationToken.None);

        var initialInsertion = initialCode.IndexOf("value", StringComparison.Ordinal) + "value".Length;
        AssertSourceApplicable(initialSourceText, AssertHasHintAtInsertion(initialSourceText, initialResult.ToArray(), initialInsertion, ": int"), initialInsertion, ": int");

        await store.UpsertDocumentAsync(uri, updatedCode);
        var updatedSourceText = SourceText.From(updatedCode);
        var updatedResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(updatedSourceText)
        }, CancellationToken.None);

        var updatedHints = updatedResult.ToArray();
        var updatedInsertion = updatedCode.IndexOf("value", StringComparison.Ordinal) + "value".Length;
        AssertSourceApplicable(updatedSourceText, AssertHasHintAtInsertion(updatedSourceText, updatedHints, updatedInsertion, ": string"), updatedInsertion, ": string");
        updatedHints.ShouldNotContain(static hint => hint.Label.String == ": int");
    }

    [Fact]
    public async Task Handle_AfterCrossFileTypeEdit_DoesNotReturnStaleCachedHintAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var sourceRoot = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "App.rvnproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="src/**/*.rvn" />
              </ItemGroup>
            </Project>
            """);

        var mainPath = Path.Combine(sourceRoot, "main.rvn");
        var utilitiesPath = Path.Combine(sourceRoot, "test.rvn");
        const string mainCode = """
import Utilities.*

func Main() -> unit {
    let test = Test2()
}
""";
        const string initialUtilitiesCode = """
namespace Utilities

func Test2() -> IDisposable? {
    return null
}
""";
        const string updatedUtilitiesCode = """
namespace Utilities

func Test2() -> IDisposable {
    return default!
}
""";

        await File.WriteAllTextAsync(mainPath, mainCode);
        await File.WriteAllTextAsync(utilitiesPath, initialUtilitiesCode);

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
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var handler = new InlayHintHandler(store, dispatcher, NullLogger<InlayHintHandler>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var utilitiesUri = DocumentUri.FromFileSystemPath(utilitiesPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);
        await store.UpsertDocumentAsync(utilitiesUri, initialUtilitiesCode);
        var sourceText = SourceText.From(mainCode);
        var request = new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Range = FullDocumentRange(sourceText)
        };

        var initialResult = await handler.Handle(request, CancellationToken.None);
        var initialContext = await store.GetAnalysisContextAsync(mainUri, CancellationToken.None);
        initialContext.ShouldNotBeNull();
        var initialSnapshot = dispatcher.CreateSnapshot(mainUri, initialContext.Value.Document, documentSession: 0);
        var insertion = mainCode.IndexOf("test =", StringComparison.Ordinal) + "test".Length;
        AssertSourceApplicable(
            sourceText,
            AssertHasHintAtInsertion(sourceText, initialResult.ToArray(), insertion, ": IDisposable?"),
            insertion,
            ": IDisposable?");

        await store.UpsertDocumentAsync(utilitiesUri, updatedUtilitiesCode);
        var cacheHit = dispatcher.TryGetCachedInlayHints(request, sourceText, out _);

        cacheHit.ShouldBeFalse();
        store.TryGetDocument(mainUri, out var currentMainDocument).ShouldBeTrue();
        dispatcher.IsCurrent(initialSnapshot).ShouldBeFalse();
        LanguageServerDispatcher.IsCurrent(
                dispatcher.CreateSnapshot(mainUri, currentMainDocument!, documentSession: 0),
                currentMainDocument!)
            .ShouldBeTrue();

        var updatedResult = await handler.Handle(request, CancellationToken.None);
        var updatedHints = updatedResult.ToArray();
        AssertSourceApplicable(
            sourceText,
            AssertHasHintAtInsertion(sourceText, updatedHints, insertion, ": IDisposable"),
            insertion,
            ": IDisposable");
        updatedHints.Select(static hint => hint.Label.String).ShouldNotContain(": IDisposable?");
    }

    [Fact]
    public async Task Handle_RepeatedCrossFileNullableTypeToggle_ReturnsCurrentHintAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var sourceRoot = Path.Combine(_tempRoot, "src");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "App.rvnproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="src/**/*.rvn" />
              </ItemGroup>
            </Project>
            """);

        var mainPath = Path.Combine(sourceRoot, "main.rvn");
        var utilitiesPath = Path.Combine(sourceRoot, "test.rvn");
        const string mainCode = """
import Utilities.*

func Main() -> unit {
    let test = Test2()
}
""";

        static string UtilitiesCode(string returnType, string returnValue)
            => $$"""
namespace Utilities

func Test2() -> {{returnType}} {
    return {{returnValue}}
}
""";

        await File.WriteAllTextAsync(mainPath, mainCode);
        await File.WriteAllTextAsync(utilitiesPath, UtilitiesCode("IDisposable?", "null"));

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
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var handler = new InlayHintHandler(store, dispatcher, NullLogger<InlayHintHandler>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var utilitiesUri = DocumentUri.FromFileSystemPath(utilitiesPath);
        await store.UpsertDocumentAsync(mainUri, mainCode);
        await store.UpsertDocumentAsync(utilitiesUri, UtilitiesCode("IDisposable?", "null"));
        var sourceText = SourceText.From(mainCode);
        var request = new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Range = FullDocumentRange(sourceText)
        };
        var insertion = mainCode.IndexOf("test =", StringComparison.Ordinal) + "test".Length;

        await AssertCurrentTypeHintAsync(": IDisposable?");

        await store.UpsertDocumentAsync(utilitiesUri, UtilitiesCode("IDisposable", "default!"));
        await AssertCurrentTypeHintAsync(": IDisposable");

        await store.UpsertDocumentAsync(utilitiesUri, UtilitiesCode("IDisposable?", "null"));
        await AssertCurrentTypeHintAsync(": IDisposable?");

        await store.UpsertDocumentAsync(utilitiesUri, UtilitiesCode("IDisposable", "default!"));
        await AssertCurrentTypeHintAsync(": IDisposable");

        async Task AssertCurrentTypeHintAsync(string expectedLabel)
        {
            var result = await handler.Handle(request, CancellationToken.None);
            var hints = result.ToArray();
            var hint = AssertHasHintAtInsertion(sourceText, hints, insertion, expectedLabel);
            AssertSourceApplicable(
                sourceText,
                hint,
                insertion,
                expectedLabel);
            hints
                .Where(candidate => candidate.Position == hint.Position)
                .Select(static hint => hint.Label.String)
                .ShouldBe([expectedLabel]);
        }
    }

    [Fact]
    public async Task Handle_FullDocumentAfterPendingEdit_ReturnsStickyHintsWithoutFlushingPendingEditAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string initialCode = """
func Main() -> unit {
    let value = 1
}
""";
        const string updatedCode = """
// edited while inlay hints are still cached

func Main() -> unit {
    let valueEdited = 1
}
""";

        await store.UpsertDocumentAsync(uri, initialCode);
        var initialSourceText = SourceText.From(initialCode);
        var initialResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(initialSourceText)
        }, CancellationToken.None);
        AssertHasHintAtInsertion(
            initialSourceText,
            initialResult.ToArray(),
            initialCode.IndexOf("value = 1", StringComparison.Ordinal) + "value".Length,
            ": int");

        store.QueuePendingDocumentChange(uri, SourceText.From(updatedCode), deferMacroConsumerRefresh: true);
        var updatedSourceText = SourceText.From(updatedCode);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(updatedSourceText)
        }, CancellationToken.None);

        var stickyHints = result.ToArray();
        AssertHasHintAtInsertion(
            updatedSourceText,
            stickyHints,
            updatedCode.IndexOf("valueEdited = 1", StringComparison.Ordinal) + "valueEdited".Length,
            ": int");
        store.TryGetPendingDocumentText(uri, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_FullDocumentAfterCommittedEdit_ClearsStickyHintsWhenNoHintsRemainAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string initialCode = """
func Main() -> unit {
    let value = 1
}
""";
        const string updatedCode = """
func Main() -> unit {
}
""";

        await store.UpsertDocumentAsync(uri, initialCode);
        var initialSourceText = SourceText.From(initialCode);
        var initialResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(initialSourceText)
        }, CancellationToken.None);
        AssertHasHintAtInsertion(
            initialSourceText,
            initialResult.ToArray(),
            initialCode.IndexOf("value = 1", StringComparison.Ordinal) + "value".Length,
            ": int");

        await store.UpsertDocumentAsync(uri, updatedCode);
        var updatedSourceText = SourceText.From(updatedCode);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(updatedSourceText)
        }, CancellationToken.None);

        result.ToArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_SmallDocumentAfterCommittedTypeChange_RecomputesHintImmediatelyAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string initialCode = """
func Main() -> unit {
    let value = 1
}
""";
        const string updatedCode = """
func Main() -> unit {
    let value = "changed"
}
""";

        await store.UpsertDocumentAsync(uri, initialCode);
        var initialSourceText = SourceText.From(initialCode);
        var initialResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(initialSourceText)
        }, CancellationToken.None);
        AssertHasHintAtInsertion(
            initialSourceText,
            initialResult.ToArray(),
            initialCode.IndexOf("value =", StringComparison.Ordinal) + "value".Length,
            ": int");

        await store.UpsertDocumentAsync(uri, updatedCode);
        var updatedSourceText = SourceText.From(updatedCode);
        var updatedResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(updatedSourceText)
        }, CancellationToken.None);
        var updatedHints = updatedResult.ToArray();
        var insertion = updatedCode.IndexOf("value =", StringComparison.Ordinal) + "value".Length;

        AssertHasHintAtInsertion(updatedSourceText, updatedHints, insertion, ": string");
        updatedHints.ShouldNotContain(hint => hint.Position == PositionHelper.ToRange(updatedSourceText, new TextSpan(insertion, 0)).Start && hint.Label.String == ": int");
    }

    [Fact]
    public async Task Handle_InvocationArguments_ProvidesSourceApplicableParameterNameHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class StackPanel {
    public init(spacing: double) {
    }
}

class Box<T> {
}

extension BoxExtensions<T> for Box<T> {
    func WithContext(message: string) -> string => message
}

func Render(spacing: double, title: string) -> unit {
}

func Main() -> unit {
    let panel = StackPanel(8.0)
    Render(8.0, title: "ready")
    Box<int>().WithContext("reading")
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        var constructorArgumentInsertion = code.IndexOf("8.0)", StringComparison.Ordinal);
        var methodArgumentInsertion = code.IndexOf("8.0, title", StringComparison.Ordinal);
        var namedArgumentInsertion = code.IndexOf("\"ready\"", StringComparison.Ordinal);
        var extensionArgumentInsertion = code.IndexOf("\"reading\"", StringComparison.Ordinal);

        var constructorHint = AssertHasHintAtInsertion(sourceText, hints, constructorArgumentInsertion, "spacing:");
        AssertParameterNameSourceApplicable(sourceText, constructorHint, constructorArgumentInsertion, "spacing: ");

        var methodHint = AssertHasHintAtInsertion(sourceText, hints, methodArgumentInsertion, "spacing:");
        AssertParameterNameSourceApplicable(sourceText, methodHint, methodArgumentInsertion, "spacing: ");

        var extensionHint = AssertHasHintAtInsertion(sourceText, hints, extensionArgumentInsertion, "message:");
        AssertParameterNameSourceApplicable(sourceText, extensionHint, extensionArgumentInsertion, "message: ");

        hints.ShouldNotContain(hint =>
            hint.Kind == InlayHintKind.Parameter &&
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(namedArgumentInsertion, 0)).Start);
    }

    [Fact]
    public async Task Handle_DeconstructionPatterns_ProvidesSourceApplicableElementNameHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
record class Person(name: string, age: int) {
}

record class Point(x: int, y: int) {
}

func Main() -> unit {
    let point = Point(1, 2)
    let (left, top) = point
    let (x: explicitLeft, explicitTop) = point

    let person = Person("Raven", 3)
    if let Person(personName, personAge) = person {
    }
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        var leftInsertion = code.IndexOf("left, top", StringComparison.Ordinal);
        var topInsertion = code.IndexOf("top) = point", StringComparison.Ordinal);
        var explicitLeftInsertion = code.IndexOf("explicitLeft", StringComparison.Ordinal);
        var explicitTopInsertion = code.IndexOf("explicitTop", StringComparison.Ordinal);
        var personNameInsertion = code.IndexOf("personName", StringComparison.Ordinal);
        var personAgeInsertion = code.IndexOf("personAge", StringComparison.Ordinal);

        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, leftInsertion, "x:"), leftInsertion, "x: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, topInsertion, "y:"), topInsertion, "y: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, explicitTopInsertion, "y:"), explicitTopInsertion, "y: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, personNameInsertion, "name:"), personNameInsertion, "name: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, personAgeInsertion, "age:"), personAgeInsertion, "age: ");

        hints.ShouldNotContain(hint =>
            hint.Kind == InlayHintKind.Parameter &&
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(explicitLeftInsertion, 0)).Start);
    }

    [Fact]
    public async Task Handle_BroadFullDocumentSemanticGateBusyWithoutCachedResult_ReturnsWithoutWaitingAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 90).Select(static i => $"// padding {i}"));
        var code = $$"""
func Main() -> unit {
    let answer = 1 + 2
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        using var heldLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var hintsTask = handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var completedTask = await Task.WhenAny(hintsTask, Task.Delay(1000));
        completedTask.ShouldBe(hintsTask);

        var result = await hintsTask;
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ConcurrentVisibleRanges_DoNotSupersedeEachOtherAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    let first = 1
    let second = "two"
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        IDisposable? heldLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        try
        {
            var firstTask = handler.Handle(new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = new LspRange
                {
                    Start = new Position(0, 0),
                    End = new Position(2, 0)
                }
            }, CancellationToken.None);

            await Task.Delay(50);
            var secondTask = handler.Handle(new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = new LspRange
                {
                    Start = new Position(2, 0),
                    End = new Position(4, 0)
                }
            }, CancellationToken.None);

            await Task.Delay(100);
            firstTask.IsCompleted.ShouldBeFalse();
            secondTask.IsCompleted.ShouldBeFalse();

            heldLease.Dispose();
            heldLease = null;

            var firstHints = (await firstTask).ToArray();
            var secondHints = (await secondTask).ToArray();

            AssertHasHintAtInsertion(
                sourceText,
                firstHints,
                code.IndexOf("first = 1", StringComparison.Ordinal) + "first".Length,
                ": int");
            AssertHasHintAtInsertion(
                sourceText,
                secondHints,
                code.IndexOf("second = \"two\"", StringComparison.Ordinal) + "second".Length,
                ": string");
        }
        finally
        {
            heldLease?.Dispose();
        }
    }

    [Fact]
    public async Task Handle_BroadFullDocumentSemanticGateBusyWithCachedResult_ReturnsCachedHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 90).Select(static i => $"// padding {i}"));
        var code = $$"""
func Main() -> unit {
    let answer = 1
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var answerInsertion = code.IndexOf("answer = 1", StringComparison.Ordinal) + "answer".Length;
        var request = new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        };

        var firstResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(answerInsertion, 0))
        }, CancellationToken.None);
        var firstHints = firstResult.ToArray();
        firstHints.ShouldContain(static hint => hint.Label.String == ": int");

        using var heldLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        var hintsTask = handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var completedTask = await Task.WhenAny(hintsTask, Task.Delay(1000));
        completedTask.ShouldBe(hintsTask);

        var cachedHints = (await hintsTask).ToArray();
        cachedHints.Select(static hint => hint.Label.String).ShouldContain(": int");
    }

    [Fact]
    public async Task Handle_BroadFullDocumentAfterVisibleLambdaRange_ReturnsCachedLambdaHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 90).Select(static i => $"// padding {i}"));
        var code = $$"""
func Where(values: int[], predicate: (int -> bool)) -> int[] {
    return values
}

func Main() -> unit {
    let filtered = Where([|1, 2, 3|], candidate => candidate > 1)
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var candidateInsertion = code.IndexOf("candidate =>", StringComparison.Ordinal) + "candidate".Length;

        var visibleResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(candidateInsertion, 0))
        }, CancellationToken.None);
        AssertHasHintAtInsertion(sourceText, visibleResult.ToArray(), candidateInsertion, ": int");

        var broadResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, broadResult.ToArray(), candidateInsertion, ": int");
    }

    [Fact]
    public async Task Handle_AfterEdit_ReturnsStickyUnaffectedCachedHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    let a = 1
    let b = 2
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var initialResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);
        var initialHints = initialResult.ToArray();
        AssertHasHintAtInsertion(sourceText, initialHints, code.IndexOf("a = 1", StringComparison.Ordinal) + 1, ": int");
        AssertHasHintAtInsertion(sourceText, initialHints, code.IndexOf("b = 2", StringComparison.Ordinal) + 1, ": int");

        var updatedCode = code.Replace("let a = 1", "let a: int = 1", StringComparison.Ordinal);
        var updatedSourceText = SourceText.From(updatedCode);
        await store.UpsertDocumentAsync(uri, updatedSourceText);

        var stickyResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(updatedSourceText)
        }, CancellationToken.None);
        var stickyHints = stickyResult.ToArray();

        AssertNoHintAtInsertion(updatedSourceText, stickyHints, updatedCode.IndexOf("a: int = 1", StringComparison.Ordinal) + 1);
        AssertHasHintAtInsertion(updatedSourceText, stickyHints, updatedCode.IndexOf("b = 2", StringComparison.Ordinal) + 1, ": int");
    }

    [Fact]
    public async Task Handle_RequestedRange_FiltersHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    let first = 1
    let second = "two"
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var secondLineStart = code.IndexOf("    let second", StringComparison.Ordinal);
        secondLineStart.ShouldBeGreaterThan(0);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(secondLineStart, code.Length - secondLineStart))
        }, CancellationToken.None);

        var hints = result.ToArray();
        hints.Length.ShouldBe(1);
        hints[0].Label.String.ShouldBe(": string");
    }

    [Fact]
    public async Task Handle_FullDocument_BindsInvocationInitializersForSmallDocumentsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Make(value: int) -> int {
    return value + 1
}

func Main() -> unit {
    let answer = Make(41)
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var answerInsertion = code.IndexOf("answer", StringComparison.Ordinal) + "answer".Length;

        var fullDocumentResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, fullDocumentResult.ToArray(), answerInsertion, ": int");

        var preciseResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(answerInsertion, 0))
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, preciseResult.ToArray(), answerInsertion, ": int");
    }

    [Fact]
    public async Task Handle_LargeRange_DoesNotColdBindInvocationInitializersAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
func Make(value: int) -> int {
    return value + 1
}

func Main() -> unit {
    let answer = Make(41)
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var answerInsertion = code.IndexOf("answer", StringComparison.Ordinal) + "answer".Length;

        var largeResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var largeHints = largeResult.ToArray();
        largeHints.ShouldNotContain(hint =>
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(answerInsertion, 0)).Start &&
            hint.Label.String == ": int");

        var smallResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(answerInsertion, 0))
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, smallResult.ToArray(), answerInsertion, ": int");
    }

    [Fact]
    public async Task Handle_CarrierPropagation_ProvidesFailureChannelHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
union class Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

union class FulfillmentError {
    case MissingPlan
}

record class FulfillmentPlan(val Name: string)

func Test(planResult: Result<FulfillmentPlan, FulfillmentError>) -> Result<string, FulfillmentError> {
    let plan = planResult?
    let nameResult = planResult?.Name
    return .Ok(plan.Name)
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        var propagateInsertion = code.IndexOf("planResult?", StringComparison.Ordinal) + "planResult?".Length;
        var conditionalAccessInsertion = code.IndexOf("planResult?.Name", StringComparison.Ordinal) + "planResult?.Name".Length;

        var propagateHint = AssertHasHintAtInsertion(sourceText, hints, propagateInsertion, "↩ Error<FulfillmentError>");
        var conditionalAccessHint = AssertHasHintAtInsertion(sourceText, hints, conditionalAccessInsertion, "↩ Error<FulfillmentError>");
        propagateHint.TextEdits.ShouldBeNull();
        conditionalAccessHint.TextEdits.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_FulfillmentWorkflowVisibleRange_UsesAwaitedCarrierTypesAndFailureHintsAsync()
    {
        var sampleRoot = Path.Combine(
            FindRepositoryRoot(),
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(0, 0),
                End = new Position(25, 0)
            }
        }, CancellationToken.None);

        var hints = result.ToArray();
        AssertHasHintAtInsertion(
            sourceText,
            hints,
            code.IndexOf("inventoryResult = await", StringComparison.Ordinal) + "inventoryResult".Length,
            ": Result<InventoryItem[], FulfillmentError>");
        AssertHasHintAtInsertion(
            sourceText,
            hints,
            code.IndexOf("inventory = inventoryResult", StringComparison.Ordinal) + "inventory".Length,
            ": InventoryItem[]");
        AssertHasHintAtInsertion(
            sourceText,
            hints,
            code.IndexOf("planResult?", StringComparison.Ordinal) + "planResult?".Length,
            "↩ Error<FulfillmentError>");
    }

    [Fact]
    public async Task Handle_LargeRange_UsesAvailablePropagationInitializerTypeAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
namespace System

union class Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

record class Dto(val Priority: string)

class Parser {
    static func ParsePriority(value: string) -> Result<int, string> {
        return Result<int, string>.Ok(1)
    }
}

class C {
    func Test() -> Result<int, string> {
        let dto = Dto("normal")
        let priority = Parser.ParsePriority(dto.Priority)?
        return Result<int, string>.Ok(priority)
    }
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var priorityInsertion = code.IndexOf("priority = Parser.ParsePriority", StringComparison.Ordinal) + "priority".Length;
        var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(0, 0),
                End = new Position(24, 0)
            }
        }, CancellationToken.None);

        var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
        var delta = SemanticQueryInstrumentation.Subtract(after, before);
        var hints = result.ToArray();
        AssertHasHintAtInsertion(sourceText, hints, priorityInsertion, ": int");
        delta.BoundNodeBindFallbacks.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_LargeRange_ProvidesLoopTargetAndPatternHintsWithBudgetAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
func Main() -> unit {
    let values = [1, 2, 3]
    for value in values {
        WriteLine(value)
    }

    let pairs = [("one", 1)]
    for let (key, count) in pairs {
        WriteLine(key)
        WriteLine(count)
    }
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var valueInsertion = code.IndexOf("value in values", StringComparison.Ordinal) + "value".Length;
        var keyInsertion = code.IndexOf("key, count", StringComparison.Ordinal) + "key".Length;

        var largeResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var largeHints = largeResult.ToArray();
        AssertHasHintAtInsertion(sourceText, largeHints, valueInsertion, ": int");
        AssertHasHintAtInsertion(sourceText, largeHints, keyInsertion, ": string");

        var smallForResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(valueInsertion, 0))
        }, CancellationToken.None);
        AssertHasHintAtInsertion(sourceText, smallForResult.ToArray(), valueInsertion, ": int");

        var smallPatternResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(keyInsertion, 0))
        }, CancellationToken.None);
        AssertHasHintAtInsertion(sourceText, smallPatternResult.ToArray(), keyInsertion, ": string");
    }

    [Fact]
    public async Task Handle_InsertApplied_DoesNotReturnAnnotationHintAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Add(left: int, right: int) -> int {
    return left + right
}

func Main() -> unit {
    let answer: int = 1 + 2
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AssignmentPatterns_OnlyAnnotatesDeclaredDesignationsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    var assignedA = 0
    var assignedB = ""
    (assignedA, assignedB) = (1, "one")

    let (declaredA, declaredB) = (2, "two")
    (var inlineA, var inlineB) = (3, "three")

    let values = [|4, 5, 6|]
    var assignedHead = 0
    var assignedRest = [|0|]
    [assignedHead, ...assignedRest] = values

    let [declaredHead, ...declaredRest] = values
    [let inlineHead, ...let inlineRest] = values
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();

        AssertNoHintAtInsertion(sourceText, hints, code.IndexOf("assignedA, assignedB", StringComparison.Ordinal) + "assignedA".Length);
        AssertNoHintAtInsertion(sourceText, hints, code.IndexOf("assignedA, assignedB", StringComparison.Ordinal) + "assignedA, assignedB".Length);
        AssertNoHintAtInsertion(sourceText, hints, code.IndexOf("assignedHead, ...assignedRest", StringComparison.Ordinal) + "assignedHead".Length);
        AssertNoHintAtInsertion(sourceText, hints, code.IndexOf("assignedHead, ...assignedRest", StringComparison.Ordinal) + "assignedHead, ...assignedRest".Length);

        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("declaredA, declaredB", StringComparison.Ordinal) + "declaredA".Length, ": int");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("declaredA, declaredB", StringComparison.Ordinal) + "declaredA, declaredB".Length, ": string");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("inlineA, var inlineB", StringComparison.Ordinal) + "inlineA".Length, ": int");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("inlineA, var inlineB", StringComparison.Ordinal) + "inlineA, var inlineB".Length, ": string");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("declaredHead, ...declaredRest", StringComparison.Ordinal) + "declaredHead".Length, ": int");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("declaredHead, ...declaredRest", StringComparison.Ordinal) + "declaredHead, ...declaredRest".Length, ": int");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("inlineHead, ...let inlineRest", StringComparison.Ordinal) + "inlineHead".Length, ": int");
        AssertHasHintAtInsertion(sourceText, hints, code.IndexOf("inlineHead, ...let inlineRest", StringComparison.Ordinal) + "inlineHead, ...let inlineRest".Length, ": int");
    }

    [Fact]
    public async Task Handle_ForIdentifierTarget_ProvidesSourceApplicableTypeHintAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    let numbers = [|1, 2, 3|]
    for number in numbers {
        number
    }

    for explicit: int in numbers {
        explicit
    }
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        var loopTargetHint = hints.Single(hint =>
            hint.Label.String == ": int" &&
            hint.Position.Line == PositionHelper.ToRange(
                sourceText,
                new TextSpan(code.IndexOf("number in", StringComparison.Ordinal) + "number".Length, 0)).Start.Line);

        AssertSourceApplicable(
            sourceText,
            loopTargetHint,
            code.IndexOf("number in", StringComparison.Ordinal) + "number".Length,
            ": int");

        hints.ShouldNotContain(hint =>
            hint.Position.Line == PositionHelper.ToRange(
                sourceText,
                new TextSpan(code.IndexOf("explicit", StringComparison.Ordinal) + "explicit".Length, 0)).Start.Line &&
            hint.Label.String == ": int");
    }

    [Fact]
    public async Task Handle_UseDeclarationAndFunctionExpressions_ProvidesTypeHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
class DisposableResource {
    func Dispose() -> unit {
    }
}

func Consume(callback: (int -> int)) -> int {
    return callback(41)
}

func Main() -> unit {
    use resource = DisposableResource()
    let result = Consume(value => value + 1)
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        hints.Select(static hint => hint.Label.String).ShouldContain(": DisposableResource");
        hints.Select(static hint => hint.Label.String).ShouldContain(" -> int");

        var resourceHint = hints.Single(static hint => hint.Label.String == ": DisposableResource");
        AssertSourceApplicable(
            sourceText,
            resourceHint,
            code.IndexOf("resource", StringComparison.Ordinal) + "resource".Length,
            ": DisposableResource");

        var callbackInsertion = code.IndexOf("value =>", StringComparison.Ordinal) + "value".Length;
        var callbackPosition = PositionHelper.ToRange(sourceText, new TextSpan(callbackInsertion, 0)).Start;
        var callbackReturnHint = hints
            .Where(static hint => hint.Label.String == " -> int")
            .Single(hint => hint.Position.Line == callbackPosition.Line && hint.Position.Character == callbackPosition.Character);
        AssertSourceApplicable(
            sourceText,
            callbackReturnHint,
            callbackInsertion,
            " -> int");
    }

    [Fact]
    public async Task Handle_SmallFullDocument_ShowsTopLevelAsyncFunctionExpressionLocalTypeAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Threading.Tasks.*

class RequestContext {
    public val Text: string = "body"
}

func Accept(handler: RequestContext -> Task<string>) -> unit { }

Accept(async func (context: RequestContext) {
    let content = await Task.FromResult(context.Text)
    return "submitted: $content"
})
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);
        var contentInsertion = code.IndexOf("content", StringComparison.Ordinal) + "content".Length;

        result.ToArray().ShouldContain(hint =>
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(contentInsertion, 0)).Start &&
            hint.Label.String == ": string");
    }

    [Fact]
    public async Task Handle_LargeFullDocument_DoesNotColdBindFunctionExpressionParameterTypesAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
func SelectValue(callback: (int -> int)) -> int {
    return callback(41)
}

func Main() -> unit {
    let result = SelectValue(value => value + 1)
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var parameterInsertion = code.IndexOf("value =>", StringComparison.Ordinal) + "value".Length;
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();

        var fullDocumentResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);
        var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
        var delta = SemanticQueryInstrumentation.Subtract(after, before);

        fullDocumentResult.ToArray().ShouldNotContain(hint =>
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(parameterInsertion, 0)).Start &&
            hint.Label.String == ": int");
        delta.BoundNodeBindFallbacks.ShouldBe(0);

        var preciseResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(parameterInsertion, 0))
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, preciseResult.ToArray(), parameterInsertion, ": int");
    }

    [Fact]
    public async Task Handle_BroadFullDocument_DefersExpensivePresentationHintsAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 90).Select(static i => $"// padding {i}"));
        var code = $$"""
func Build(value: int) {
    return value + 1
}

func Main() -> unit {
    let result = Build(41)
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var argumentInsertion = code.IndexOf("41", StringComparison.Ordinal);

        var broadResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange(new Position(0, 0), new Position(sourceText.GetLineCount(), 0))
        }, CancellationToken.None);

        var broadHints = broadResult.ToArray();
        broadHints.Select(static hint => hint.Label.String).ShouldNotContain(" -> int");
        broadHints.ShouldNotContain(hint =>
            hint.Position == PositionHelper.ToRange(sourceText, new TextSpan(argumentInsertion, 0)).Start &&
            hint.Label.String == "value:");

        var preciseResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(argumentInsertion, 0))
        }, CancellationToken.None);

        var preciseHint = AssertHasHintAtInsertion(sourceText, preciseResult.ToArray(), argumentInsertion, "value:");
        AssertParameterNameSourceApplicable(sourceText, preciseHint, argumentInsertion, "value: ");
    }

    [Fact]
    public async Task Handle_UnimportedMetadataType_UsesSimpleAnnotationAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
func Main() -> unit {
    let json = System.Text.Json.Nodes.JsonObject()
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var jsonInsertion = code.IndexOf("json", StringComparison.Ordinal) + "json".Length;

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hint = result.Single(static hint => hint.Label.String == ": JsonObject");
        AssertSourceApplicable(
            sourceText,
            hint,
            jsonInsertion,
            ": JsonObject");
        hint.Tooltip.ShouldBeNull();

        var preciseResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(jsonInsertion, 0))
        }, CancellationToken.None);

        var preciseHint = preciseResult.Single(static hint => hint.Label.String == ": JsonObject");
        AssertTooltipMentionsInsertion(preciseHint, ": JsonObject");
    }

    [Fact]
    public async Task Handle_ImportedMetadataType_UsesSimpleAnnotationAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
import System.Text.Json.Nodes.*

func Main() -> unit {
    let json = JsonObject()
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var jsonInsertion = code.IndexOf("json", StringComparison.Ordinal) + "json".Length;

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(jsonInsertion, 0))
        }, CancellationToken.None);

        var hint = result.Single(static hint => hint.Label.String == ": JsonObject");
        AssertSourceApplicable(
            sourceText,
            hint,
            jsonInsertion,
            ": JsonObject");
        AssertTooltipMentionsInsertion(hint, ": JsonObject");
    }

    [Fact]
    public async Task Handle_LargeDocumentImportedMetadataType_UsesSimpleAnnotationAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
import System.Text.Json.Nodes.*

func Main() -> unit {
    let json = JsonObject()
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var jsonInsertion = code.IndexOf("json", StringComparison.Ordinal) + "json".Length;

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(jsonInsertion, 0))
        }, CancellationToken.None);

        var hints = result.ToArray();
        var hint = hints.Single(static hint => hint.Label.String == ": JsonObject");
        hints.ShouldNotContain(static hint => hint.Label.String == ": System.Text.Json.Nodes.JsonObject");
        AssertSourceApplicable(
            sourceText,
            hint,
            jsonInsertion,
            ": JsonObject");
    }

    [Fact]
    public async Task Handle_LargeDocumentCurrentNamespaceSourceType_UsesSimpleAnnotationAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var padding = string.Join(Environment.NewLine, Enumerable.Range(0, 220).Select(static i => $"// padding {i}"));
        var code = $$"""
namespace Samples.VehicleCosts

import System.*

class FuelConsumptionRecord {
}

func Main() -> unit {
    let entry = FuelConsumptionRecord()
}

{{padding}}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var entryInsertion = code.IndexOf("entry", StringComparison.Ordinal) + "entry".Length;

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(entryInsertion, 0))
        }, CancellationToken.None);

        var hints = result.ToArray();
        var hint = hints.Single(static hint => hint.Label.String == ": FuelConsumptionRecord");
        hints.ShouldNotContain(static hint => hint.Label.String == ": Samples.VehicleCosts.FuelConsumptionRecord");
        AssertSourceApplicable(
            sourceText,
            hint,
            entryInsertion,
            ": FuelConsumptionRecord");
    }

    [Fact]
    public async Task Handle_AmbiguousVisibleTypeName_UsesSimpleAnnotationAsync()
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var documentPath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        const string code = """
namespace First {
    class JsonObject {
    }
}

namespace Second {
    class JsonObject {
    }
}

import First.*
import Second.*

func Main() -> unit {
    let json = First.JsonObject()
}
""";
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hint = result.Single(static hint => hint.Label.String == ": JsonObject");
        AssertSourceApplicable(
            sourceText,
            hint,
            code.IndexOf("json", StringComparison.Ordinal) + "json".Length,
            ": JsonObject");
    }

    [Fact]
    public async Task Handle_EfCoreVehicleSample_ProvidesRangeScopedHintsForLocalsAndRouteFunctionExpressionsAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-vehicle-costs");
        var filePath = Path.Combine(projectRoot, "src", "Api", "Main.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-vehicle-costs",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var builderInsertion = code.IndexOf("builder", StringComparison.Ordinal) + "builder".Length;
        var builderHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, builderInsertion);
        AssertHasHintAtInsertion(sourceText, builderHints, builderInsertion, ": ");

        var vehicleInsertion = code.IndexOf("vehicle =>", StringComparison.Ordinal) + "vehicle".Length;
        var vehicleHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, vehicleInsertion);
        AssertHasHintAtInsertion(sourceText, vehicleHints, vehicleInsertion, " -> ");

        var routeHandler = SyntaxTree.ParseText(code)
            .GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .First(function => function.ReturnType is null &&
                               function.ParameterList.Parameters.Any(parameter =>
                                   parameter.Identifier.ValueText == "context"));
        var routeHandlerInsertion = routeHandler.ParameterList.Span.End;
        var routeHandlerHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, routeHandlerInsertion);
        AssertHasHintAtInsertion(sourceText, routeHandlerHints, routeHandlerInsertion, " -> ");
    }

    [Fact]
    public async Task Handle_EfCoreVehicleSample_BroadFullDocumentAfterVisibleLambdaRange_ReturnsCachedLambdaHintsAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-vehicle-costs");
        var filePath = Path.Combine(projectRoot, "src", "Api", "Main.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-vehicle-costs",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        const string lambda = ".Include(candidate => candidate.FuelConsumptions)";
        var lambdaStart = code.IndexOf(lambda, StringComparison.Ordinal);
        lambdaStart.ShouldBeGreaterThanOrEqualTo(0);
        var candidateInsertion = lambdaStart + lambda.IndexOf("candidate =>", StringComparison.Ordinal) + "candidate".Length;

        var visibleResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(candidateInsertion, 0))
        }, CancellationToken.None);
        AssertHasHintAtInsertion(sourceText, visibleResult.ToArray(), candidateInsertion, ": VehicleEntity");

        var broadResult = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, broadResult.ToArray(), candidateInsertion, ": VehicleEntity");
    }

    [Fact]
    public async Task Handle_EfCoreExpressionTreesSample_ProvidesParameterHintForPipeExtensionLambdaAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-expression-trees",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        const string selectPipe = "|> Select(user => user.Name)";
        var selectPipeStart = code.IndexOf(selectPipe, StringComparison.Ordinal);
        selectPipeStart.ShouldBeGreaterThanOrEqualTo(0);
        var parameterInsertion = selectPipeStart + selectPipe.IndexOf("user", StringComparison.Ordinal) + "user".Length;

        var hints = await GetHintsAtInsertionAsync(handler, uri, sourceText, parameterInsertion);

        AssertHasHintAtInsertion(sourceText, hints, parameterInsertion, ": User");
    }

    [Fact]
    public async Task Handle_EfCoreExpressionTreesSample_FullDocumentProvidesTargetTypedConstructorParameterHintsAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-expression-trees",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        var hints = result.ToArray();
        var idInsertion = code.IndexOf(".(1, \"Ana\", 29, true)", StringComparison.Ordinal) + ".(".Length;
        var nameInsertion = code.IndexOf("\"Ana\"", StringComparison.Ordinal);
        var ageInsertion = code.IndexOf("29, true", StringComparison.Ordinal);
        var isActiveInsertion = code.IndexOf("true))", StringComparison.Ordinal);

        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, idInsertion, "Id:"), idInsertion, "Id: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, nameInsertion, "Name:"), nameInsertion, "Name: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, ageInsertion, "Age:"), ageInsertion, "Age: ");
        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, hints, isActiveInsertion, "IsActive:"), isActiveInsertion, "IsActive: ");
    }

    [Fact]
    public async Task Handle_EfCoreExpressionTreesSample_FullDocumentKeepsCachedQueryTypeHintAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-expression-trees",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var queryInsertion = code.IndexOf("query = db.Users", StringComparison.Ordinal) + "query".Length;

        var preciseHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, queryInsertion);
        AssertHasHintAtInsertion(sourceText, preciseHints, queryInsertion, ": ");

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = FullDocumentRange(sourceText)
        }, CancellationToken.None);

        AssertHasHintAtInsertion(sourceText, result.ToArray(), queryInsertion, ": ");
    }

    [Fact]
    public async Task Handle_EfCoreExpressionTreesSample_QueryDotEditCycleKeepsInlaysStickyAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var withDotCode = code.Replace(
            "    let names = query.ToList()",
            "    query.\n\n    let names = query.ToList()",
            StringComparison.Ordinal);
        withDotCode.ShouldNotBe(code);

        var withoutDotCode = withDotCode.Replace("    query.\n", "    query\n", StringComparison.Ordinal);
        withoutDotCode.ShouldNotBe(withDotCode);
        withoutDotCode.ShouldNotBe(code);

        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-expression-trees",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var inlayHandler = new InlayHintHandler(store, dispatcher, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);

        AssertQueryTypeHint(code, await GetFullHintsAsync(code));

        store.QueuePendingDocumentChange(uri, SourceText.From(withDotCode), deferMacroConsumerRefresh: true);
        AssertQueryTypeHint(withDotCode, await GetFullHintsAsync(withDotCode));

        _ = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        store.TryGetPendingDocumentText(uri, out _).ShouldBeFalse();
        AssertQueryTypeHint(withDotCode, await GetFullHintsAsync(withDotCode));

        store.QueuePendingDocumentChange(uri, SourceText.From(withoutDotCode), deferMacroConsumerRefresh: true);
        AssertQueryTypeHint(withoutDotCode, await GetFullHintsAsync(withoutDotCode));

        _ = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        store.TryGetPendingDocumentText(uri, out _).ShouldBeFalse();
        AssertQueryTypeHint(withoutDotCode, await GetFullHintsAsync(withoutDotCode));

        store.QueuePendingDocumentChange(uri, SourceText.From(withDotCode), deferMacroConsumerRefresh: true);
        AssertQueryTypeHint(withDotCode, await GetFullHintsAsync(withDotCode));

        async Task<InlayHint[]> GetFullHintsAsync(string source)
        {
            var sourceText = SourceText.From(source);
            var result = await inlayHandler.Handle(new InlayHintParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = FullDocumentRange(sourceText)
            }, CancellationToken.None);
            return result.ToArray();
        }

        static void AssertQueryTypeHint(string source, InlayHint[] hints)
        {
            var sourceText = SourceText.From(source);
            var queryInsertion = source.IndexOf("query = db.Users", StringComparison.Ordinal) + "query".Length;
            AssertHasHintAtInsertion(sourceText, hints, queryInsertion, ": IQueryable<string>");
        }
    }

    [Fact]
    public async Task Handle_EfCoreExpressionTreesSample_PreciseRangeProvidesTargetTypedConstructorParameterHintsAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var code = await File.ReadAllTextAsync(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "efcore-expression-trees",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var idInsertion = code.IndexOf(".(1, \"Ana\", 29, true)", StringComparison.Ordinal) + ".(".Length;
        var idHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, idInsertion);

        AssertParameterNameSourceApplicable(sourceText, AssertHasHintAtInsertion(sourceText, idHints, idInsertion, "Id:"), idInsertion, "Id: ");
    }


    [Fact]
    public async Task Handle_LargeViewportRange_PrioritizesCompleteHintPlacementOverTooltipsAsync()
    {
        var sampleRoot = Path.Combine(
            FindRepositoryRoot(),
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(133, 0),
                End = new Position(250, 0)
            }
        }, CancellationToken.None);

        var hints = result.ToArray();
        var doubledInsertion = code.IndexOf("doubled", StringComparison.Ordinal) + "doubled".Length;
        var describeStringsTextInsertion =
            code.LastIndexOf("text = \"[\"", StringComparison.Ordinal) + "text".Length;

        AssertHasHintAtInsertion(sourceText, hints, doubledInsertion, ": ");
        AssertHasHintAtInsertion(sourceText, hints, describeStringsTextInsertion, ": string");
        hints.All(static hint => hint.Tooltip is null).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_LargeVisibleDocumentRange_ProvidesCheapConstructorLocalTypeHintsAsync()
    {
        var sampleRoot = Path.Combine(
            FindRepositoryRoot(),
            "samples",
            "projects",
            "efcore-vehicle-costs");
        var documentPath = Path.Combine(sampleRoot, "src", "Data", "Seed.rvn");

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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);
        var vanInsertion = code.IndexOf("van = VehicleEntity()", StringComparison.Ordinal) + "van".Length;
        var hatchbackInsertion = code.IndexOf("hatchback = VehicleEntity()", StringComparison.Ordinal) + "hatchback".Length;
        var entryInsertion = code.IndexOf("entry = FuelConsumptionRecord()", StringComparison.Ordinal) + "entry".Length;

        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = new LspRange
            {
                Start = new Position(0, 0),
                End = PositionHelper.ToRange(sourceText, new TextSpan(0, entryInsertion + 1)).End
            }
        }, CancellationToken.None);

        var hints = result.ToArray();

        AssertHasHintAtInsertion(sourceText, hints, vanInsertion, ": VehicleEntity");
        AssertHasHintAtInsertion(sourceText, hints, hatchbackInsertion, ": VehicleEntity");
        AssertHasHintAtInsertion(sourceText, hints, entryInsertion, ": FuelConsumptionRecord");
        hints.All(static hint => hint.Tooltip is null).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_TestCasePatternDesignations_ProvidesTypeHintsAsync()
    {
        var sampleRoot = Path.Combine(
            FindRepositoryRoot(),
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
        var handler = new InlayHintHandler(store, NullLogger<InlayHintHandler>.Instance);
        var uri = DocumentUri.FromFileSystemPath(documentPath);
        var code = await File.ReadAllTextAsync(documentPath);
        await store.UpsertDocumentAsync(uri, code);
        var sourceText = SourceText.From(code);

        var nameInsertion = code.IndexOf("(2, name)", StringComparison.Ordinal) + "(2, name".Length;
        var keyInsertion = code.IndexOf("(key, value) in doubled", StringComparison.Ordinal) + "(key".Length;
        var valueInsertion = code.IndexOf("(key, value) in doubled", StringComparison.Ordinal) + "(key, value".Length;

        var nameHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, nameInsertion);
        var keyHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, keyInsertion);
        var valueHints = await GetHintsAtInsertionAsync(handler, uri, sourceText, valueInsertion);

        AssertHasHintAtInsertion(sourceText, nameHints, nameInsertion, ": string");
        AssertHasHintAtInsertion(sourceText, keyHints, keyInsertion, ": string");
        AssertHasHintAtInsertion(sourceText, valueHints, valueInsertion, ": int");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static LspRange FullDocumentRange(SourceText sourceText)
        => PositionHelper.ToRange(sourceText, new TextSpan(0, sourceText.Length));

    private static async Task<InlayHint[]> GetHintsAtInsertionAsync(
        InlayHintHandler handler,
        DocumentUri uri,
        SourceText sourceText,
        int insertionPosition)
    {
        var result = await handler.Handle(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Range = PositionHelper.ToRange(sourceText, new TextSpan(insertionPosition, 0))
        }, CancellationToken.None);

        return result.ToArray();
    }

    private static void AssertSourceApplicable(SourceText sourceText, InlayHint hint, int insertionPosition, string expectedText)
    {
        hint.Kind.ShouldBe(InlayHintKind.Type);
        hint.TextEdits.ShouldNotBeNull();

        var edit = hint.TextEdits.Single();
        edit.NewText.ShouldBe(expectedText);

        var expectedRange = PositionHelper.ToRange(sourceText, new TextSpan(insertionPosition, 0));
        edit.Range.Start.Line.ShouldBe(expectedRange.Start.Line);
        edit.Range.Start.Character.ShouldBe(expectedRange.Start.Character);
        edit.Range.End.Line.ShouldBe(expectedRange.End.Line);
        edit.Range.End.Character.ShouldBe(expectedRange.End.Character);
    }

    private static void AssertParameterNameSourceApplicable(SourceText sourceText, InlayHint hint, int insertionPosition, string expectedText)
    {
        hint.Kind.ShouldBe(InlayHintKind.Parameter);
        hint.TextEdits.ShouldNotBeNull();

        var edit = hint.TextEdits.Single();
        edit.NewText.ShouldBe(expectedText);

        var expectedRange = PositionHelper.ToRange(sourceText, new TextSpan(insertionPosition, 0));
        edit.Range.Start.Line.ShouldBe(expectedRange.Start.Line);
        edit.Range.Start.Character.ShouldBe(expectedRange.Start.Character);
        edit.Range.End.Line.ShouldBe(expectedRange.End.Line);
        edit.Range.End.Character.ShouldBe(expectedRange.End.Character);
    }

    private static InlayHint AssertHasHintAtInsertion(SourceText sourceText, InlayHint[] hints, int insertionPosition, string labelPrefix)
    {
        var expectedPosition = PositionHelper.ToRange(sourceText, new TextSpan(insertionPosition, 0)).Start;
        var hint = hints.FirstOrDefault(hint =>
            hint.Label.String.StartsWith(labelPrefix, StringComparison.Ordinal) &&
            hint.Position.Line == expectedPosition.Line &&
            hint.Position.Character == expectedPosition.Character);
        hint.ShouldNotBeNull();
        return hint;
    }

    private static void AssertNoHintAtInsertion(SourceText sourceText, InlayHint[] hints, int insertionPosition)
    {
        var expectedPosition = PositionHelper.ToRange(sourceText, new TextSpan(insertionPosition, 0)).Start;
        hints.ShouldNotContain(hint =>
            hint.Position.Line == expectedPosition.Line &&
            hint.Position.Character == expectedPosition.Character);
    }

    private static void AssertTooltipMentionsInsertion(InlayHint hint, string insertionText)
    {
        hint.Tooltip.ShouldNotBeNull();
        var tooltip = hint.Tooltip.MarkupContent;
        tooltip.ShouldNotBeNull();
        tooltip.Kind.ShouldBe(MarkupKind.Markdown);
        tooltip.Value.ShouldContain("Inferred type");
        tooltip.Value.ShouldContain(insertionText);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Raven.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Raven.sln from test output directory.");
    }
}
