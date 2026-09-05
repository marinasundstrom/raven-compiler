using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class LanguageServerSnapshotConsistencyTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"raven-ls-snapshot-{Guid.NewGuid():N}");

    [Fact]
    public async Task HoverHandler_ClearedDocument_DoesNotReuseStaleStateAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("let number = 42");
        await store.UpsertDocumentAsync(uri, string.Empty);

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, 0)
        }, CancellationToken.None);

        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_UnresolvedTypeNamesDoNotExposeErrorTypeQuickInfoAsync()
    {
        const string text = """
class ActivityStore {
    static val Received: ConcurrentQueue<OrderActivity> {
        get { return null }
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        foreach (var unresolvedTypeName in new[] { "ConcurrentQueue", "OrderActivity" })
        {
            var offset = text.IndexOf(unresolvedTypeName, StringComparison.Ordinal) + 1;
            var position = GetPosition(context.Value.SourceText, offset);
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = position
            }, CancellationToken.None);

            hover.ShouldBeNull();
        }
    }

    [Fact]
    public async Task HoverHandler_UnresolvedGenericNameStillResolvesKnownTypeArgumentAsync()
    {
        const string text = """
class OrderActivity

class ActivityStore {
    static val Received: MissingGeneric<OrderActivity> {
        get { return null }
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var missingGenericPosition = GetPosition(
            context.Value.SourceText,
            text.IndexOf("MissingGeneric", StringComparison.Ordinal) + 1);
        var missingGenericHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = missingGenericPosition
        }, CancellationToken.None);
        missingGenericHover.ShouldBeNull();

        var orderActivityReference = text.LastIndexOf("OrderActivity", StringComparison.Ordinal) + 1;
        var orderActivityHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = GetPosition(context.Value.SourceText, orderActivityReference)
        }, CancellationToken.None);

        orderActivityHover.ShouldNotBeNull();
        orderActivityHover!.Contents.MarkupContent.ShouldNotBeNull();
        orderActivityHover.Contents.MarkupContent!.Value.ShouldContain("class OrderActivity");
    }

    [Fact]
    public async Task HoverHandler_LocalMacroDeclaration_UsesMacroSemanticProjectionAsync()
    {
        const string text = """
[LocalMacro]
class MacroSupport {
    val Value: int => 42

    func Read() -> int => Value
}

func Main() -> int => 0
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var referenceOffset = text.LastIndexOf("Value", StringComparison.Ordinal) + 1;
        var position = PositionHelper.ToRange(
            context.Value.SourceText,
            new TextSpan(referenceOffset, 0)).Start;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("val Value: int");
        hover.Range.ShouldNotBeNull();
    }

    [Fact]
    public async Task HoverHandler_MacroBody_UsesMethodLikeSemanticProjectionAsync()
    {
        const string text = """
import Raven.CodeAnalysis.Syntax.*
import Raven.CodeAnalysis.Macros.*

macro Double(value: int) {
    let doubled = value * 2
    let text = doubled.ToString()
    expand SyntaxFactory.ParseExpression(text)
}

macro FirstToken(tokens: IMacroTokenStream) {
    let token = tokens.ReadToken()
    expand SyntaxFactory.ParseExpression(token.Text)
}

func Main() -> int => Double!(21)
""";
        var results = await ReplayInlineHoversAsync(
            text,
            new HoverReplayTarget("parameter", "value: int", 1, "value: int"),
            new HoverReplayTarget("parameter reference", "value * 2", 1, "value: int"),
            new HoverReplayTarget("local reference", "doubled.ToString", 1, "val doubled: int"),
            new HoverReplayTarget("member invocation", "ToString()", 1, "ToString() -> string"),
            new HoverReplayTarget("imported invocation", "ParseExpression(text)", 1, "ParseExpression"),
            new HoverReplayTarget("later local", "ParseExpression(text)", "ParseExpression(".Length + 1, "val text: string"),
            new HoverReplayTarget("token stream invocation", "ReadToken()", 1, "ReadToken()"),
            new HoverReplayTarget("token member", "token.Text", "token.".Length + 1, "Text"));

        results.Count.ShouldBe(8);
    }

    [Fact]
    public async Task HoverHandler_ConsumerLocalAfterMacroInvocation_UsesConsumerProjectionAsync()
    {
        const string text = """
macro Double(value: int) {
    let doubled = value * 2
    expand Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression(doubled.ToString())
}

func Main() {
    let answer = Double!(21)
    System.Console.WriteLine(answer)
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var sourceText = SourceText.From(text);
        var referenceOffset = text.LastIndexOf("answer", StringComparison.Ordinal) + 1;
        var position = GetPosition(sourceText, referenceOffset);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("answer");
        hover.Contents.MarkupContent!.Value.ShouldContain("int");
    }

    [Fact]
    public async Task HoverHandler_MacroEdits_InvalidateWarmStateAndRecoverAsync()
    {
        const string initialText = """
macro Measure(value: int) {
    let measured = value * 2
    expand Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression(measured.ToString())
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        await AssertHoverAsync(initialText, "value * 2", 1, "value: int");
        await AssertHoverAsync(initialText, "measured.ToString", 1, "val measured: int");

        const string signatureEdit = """
macro Measure(value: string) {
    let measured = value.Length
    expand Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression(measured.ToString())
}
""";
        await store.UpsertDocumentAsync(uri, signatureEdit);
        await AssertHoverAsync(signatureEdit, "value.Length", 1, "value: string");
        await AssertHoverAsync(signatureEdit, "measured.ToString", 1, "val measured: int");

        const string invalidEdit = """
macro (value: string) {
    let measured = value.Length
    expand Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression(measured.ToString())
}
""";
        await store.UpsertDocumentAsync(uri, invalidEdit);
        await AssertHoverAsync(invalidEdit, "value.Length", 1, "value: string");

        const string restoredText = """
macro Measure(text: string) {
    let length = text.Length
    expand Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression(length.ToString())
}
""";
        await store.UpsertDocumentAsync(uri, restoredText);
        await AssertHoverAsync(restoredText, "text.Length", 1, "text: string");
        await AssertHoverAsync(restoredText, "length.ToString", 1, "val length: int");

        async Task AssertHoverAsync(string source, string marker, int markerOffset, string expected)
        {
            var sourceText = SourceText.From(source);
            var offset = source.IndexOf(marker, StringComparison.Ordinal);
            offset.ShouldBeGreaterThanOrEqualTo(0);

            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = GetPosition(sourceText, offset + markerOffset)
            }, CancellationToken.None);

            hover.ShouldNotBeNull();
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task DefinitionHandler_LocalMacroDeclaration_UsesMacroSemanticProjectionAsync()
    {
        const string text = """
[LocalMacro]
class MacroSupport {
    val Value: int => 42

    func Read() -> int => Value
}

func Main() -> int => 0
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var position = GetPosition(context.Value.SourceText, text.LastIndexOf("Value", StringComparison.Ordinal));

        var handler = new DefinitionHandler(store, NullLogger<DefinitionHandler>.Instance);
        var result = await handler.Handle(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position
        }, CancellationToken.None);

        result.ShouldNotBeNull();
        var link = result!.Single().LocationLink;
        link.ShouldNotBeNull();
        link!.TargetUri.ShouldBe(uri);
        link.TargetSelectionRange.Start.Line.ShouldBe(2);
    }

    [Fact]
    public async Task ReferencesHandler_LocalMacroDeclaration_FindsDeclarationAndReferenceAsync()
    {
        const string text = """
[LocalMacro]
class MacroSupport {
    val Value: int => 42

    func Read() -> int => Value
}

func Main() -> int => 0
""";
        var (store, manager, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var position = GetPosition(context.Value.SourceText, text.LastIndexOf("Value", StringComparison.Ordinal));

        var handler = new ReferencesHandler(
            store,
            manager,
            NullLogger<ReferencesHandler>.Instance);
        var result = await handler.Handle(new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position,
            Context = new ReferenceContext
            {
                IncludeDeclaration = true
            }
        }, CancellationToken.None);

        result.ShouldNotBeNull();
        var locations = result!.ToArray();
        locations.Length.ShouldBe(2);
        locations.ShouldAllBe(location => location.Uri == uri);
        locations.Select(static location => location.Range.Start.Line)
            .OrderBy(static line => line)
            .ShouldBe([2, 4]);
    }

    [Fact]
    public async Task RenameHandler_LocalMacroDeclaration_RenamesDeclarationAndReferenceAsync()
    {
        const string text = """
[LocalMacro]
class MacroSupport {
    val Value: int => 42

    func Read() -> int => Value
}

func Main() -> int => 0
""";
        var (store, manager, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var position = GetPosition(context.Value.SourceText, text.LastIndexOf("Value", StringComparison.Ordinal));

        var handler = new RenameHandler(
            store,
            manager,
            NullLogger<RenameHandler>.Instance);
        var prepared = await handler.Handle(new PrepareRenameParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position
        }, CancellationToken.None);
        prepared.ShouldNotBeNull();

        var result = await handler.Handle(new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position,
            NewName = "ExpandedValue"
        }, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Changes.ShouldNotBeNull();
        result.Changes!.ContainsKey(uri).ShouldBeTrue();
        var edits = result.Changes[uri].ToArray();
        edits.Length.ShouldBe(2);
        edits.ShouldAllBe(static edit => edit.NewText == "ExpandedValue");
        edits.Select(static edit => edit.Range.Start.Line)
            .OrderBy(static line => line)
            .ShouldBe([2, 4]);
    }

    [Fact]
    public async Task HoverHandler_UpdatedDocument_DoesNotReuseCachedHoverFromPreviousVersionAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    WriteLine(number)
}
""");
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var firstHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        firstHover.ShouldNotBeNull();
        firstHover.Contents.ShouldNotBeNull();
        await store.UpsertDocumentAsync(uri, """
import System.Console.*

func Main() -> unit {
    let value = 42
    WriteLine(value)
}
""");

        var secondHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        secondHover.ShouldNotBeNull();
        secondHover.Contents.ShouldNotBeNull();
        secondHover.Range.ShouldNotBeNull();
        secondHover.Range.Start.Character.ShouldBe(14);
        secondHover.Range.End.Character.ShouldBe(19);
    }

    [Fact]
    public async Task HoverHandler_EditedProjectDocument_ResolvesCrossFileFunctionAsync()
    {
        var (store, _, mainUri) = await CreateWorkspaceAsync("""
func Main() -> () {
    Test()
}
""", "test.rvn");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        await store.UpsertDocumentAsync(testUri, """
func Test() -> () {
}
""");

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Position = new Position(1, 5)
        }, CancellationToken.None);
        var completedTask = await Task.WhenAny(hoverTask, Task.Delay(1000));

        completedTask.ShouldBe(hoverTask);
        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("func Test() -> ()");
    }

    [Fact]
    public async Task HoverHandler_WatchedSourceCreate_ResolvesCrossFileFunctionInUnchangedOpenDocumentAsync()
    {
        var (store, manager, mainUri) = await CreateWorkspaceAsync("""
func Main() -> () {
    Test()
}
""");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        await File.WriteAllTextAsync(testPath, """
func Test() -> () {
}
""");
        var testUri = DocumentUri.FromFileSystemPath(testPath);

        var refreshedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = testUri,
                Type = FileChangeType.Created
            }
        ]);

        refreshedDocuments.ShouldContain(mainUri);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Position = new Position(1, 5)
        }, CancellationToken.None);
        var completedTask = await Task.WhenAny(hoverTask, Task.Delay(1000));

        completedTask.ShouldBe(hoverTask);
        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("func Test() -> ()");
    }

    [Fact]
    public async Task HoverHandler_WatchedSiblingSourceChange_UpdatesCrossFileFunctionInUnchangedOpenDocumentAsync()
    {
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
        await File.WriteAllTextAsync(testPath, """
func Test() -> int {
    return 1
}
""");

        var (store, manager, mainUri) = await CreateWorkspaceAsync("""
func Main() -> unit {
    let value = Test()
}
""");
        await File.WriteAllTextAsync(testPath, """
func Test() -> string {
    return "updated"
}
""");
        var testUri = DocumentUri.FromFileSystemPath(testPath);

        var refreshedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = testUri,
                Type = FileChangeType.Changed
            }
        ]);

        refreshedDocuments.ShouldContain(mainUri);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Position = new Position(1, 16)
        }, CancellationToken.None);
        var completedTask = await Task.WhenAny(hoverTask, Task.Delay(1000));

        completedTask.ShouldBe(hoverTask);
        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("func Test() -> string");
    }

    [Fact]
    public async Task HoverAndDiagnostics_WatchedSiblingSourceDelete_DropCrossFileFunctionFromUnchangedOpenDocumentAsync()
    {
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
        await File.WriteAllTextAsync(testPath, """
func Test() -> int {
    return 1
}
""");

        var (store, manager, mainUri) = await CreateWorkspaceAsync("""
func Main() -> unit {
    let value = Test()
}
""");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var beforeHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Position = new Position(1, 16)
        }, CancellationToken.None);
        beforeHover.ShouldNotBeNull();
        beforeHover!.Contents.MarkupContent.ShouldNotBeNull();
        beforeHover.Contents.MarkupContent!.Value.ShouldContain("func Test() -> int");

        File.Delete(testPath);
        var refreshedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = testUri,
                Type = FileChangeType.Deleted
            }
        ]);

        refreshedDocuments.ShouldContain(mainUri);
        var afterHoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(mainUri),
            Position = new Position(1, 16)
        }, CancellationToken.None);
        var completedTask = await Task.WhenAny(afterHoverTask, Task.Delay(1000));

        completedTask.ShouldBe(afterHoverTask);
        var afterHover = await afterHoverTask;
        afterHover.ShouldBeNull();

        var diagnostics = await store.TryGetDocumentCompilerDiagnosticsAsync(
            mainUri,
            shouldSkipWork: null,
            CancellationToken.None);
        diagnostics.WasSkipped.ShouldBeFalse();
        diagnostics.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Test", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task HoverHandler_InterpolatedStringText_DoesNotShowLoweredConcatAsync()
    {
        const string text = """
func Main() -> unit {
    let name = "Raven"
    let message = "Hello $name"
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var textOffset = text.IndexOf("Hello", StringComparison.Ordinal);
        textOffset.ShouldBeGreaterThanOrEqualTo(0);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(textOffset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_InterpolatedStringExpression_StillResolvesSymbolAsync()
    {
        const string text = """
func Main() -> unit {
    let name = "Raven"
    let message = "Hello $name"
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var nameOffset = text.IndexOf("$name", StringComparison.Ordinal);
        nameOffset.ShouldBeGreaterThanOrEqualTo(0);
        nameOffset += 1;

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(nameOffset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("val name:");
        hover.Contents.MarkupContent!.Value.ShouldNotContain("Concat");
    }

    [Fact]
    public async Task HoverHandler_DefaultExpression_ShowsTypedLiteralPreviewAsync()
    {
        const string text = """
import System.*

func Main() -> unit {
    let resource: IDisposable? = default
    let value: int = default
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        await AssertDefaultHoverAsync(text, context.Value.SourceText, handler, uri, "resource:", "default(IDisposable?) = null");
        await AssertDefaultHoverAsync(text, context.Value.SourceText, handler, uri, "value:", "default(int) = 0");
    }

    private static async Task AssertDefaultHoverAsync(
        string text,
        SourceText sourceText,
        HoverHandler handler,
        DocumentUri uri,
        string declarationMarker,
        string expectedPreview)
    {
        var markerOffset = text.IndexOf(declarationMarker, StringComparison.Ordinal);
        markerOffset.ShouldBeGreaterThanOrEqualTo(0);
        var defaultOffset = text.IndexOf("default", markerOffset, StringComparison.Ordinal);
        defaultOffset.ShouldBeGreaterThanOrEqualTo(0);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(defaultOffset + 1, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain(expectedPreview);
        hover.Contents.MarkupContent.Value.ShouldContain("Constant expression");
        hover.Range.ShouldNotBeNull();
    }

    [Fact]
    public async Task HoverHandler_CanceledRequest_ReturnsNullAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    WriteLine(number)
}
""");
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, cancellation.Token);

        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_BrokenNearbyCode_StillShowsUnchangedLocalSymbolAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    let broken =
    WriteLine(number)
}
""");
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(5, 14)
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("number");
        hover.Contents.MarkupContent!.Value.ShouldNotContain("Error");
        hover.Range.ShouldNotBeNull();
        hover.Range.Start.Line.ShouldBe(5);
        hover.Range.Start.Character.ShouldBe(14);
    }

    [Fact]
    public async Task HoverHandler_MalformedEarlierRecord_DoesNotFailLaterHoverAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Collections.Generic.*

record Foo(
    val Name: string
    val Count: int
)

union JsonValue(string | double | bool | JsonObject)
record JsonObject(Properties: IDictionary<string, JsonValue>)

let x = JsonObject([
    "name": 42
])

x.Properties["name"].HasValue
""");
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(10, 14)
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("JsonObject");
    }

    [Fact]
    public async Task HoverHandler_MalformedRecordBeforeUnion_DoesNotFailTypeHoverAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.*
import System.Console.*

let foo = Foo(
    Name: "Foo",
    Status: .OnMaintenance(.UtcNow, "Test"),
)

record Foo(
    val Name: string,
    val Status: Status
    val Test: int | bool
)

[RavenTaggedUnionJsonConverter("kind")]
union Status {
    case Active(Date: DateTimeOffset)
    case OnMaintenance(Date: DateTimeOffset, Reason: string)
}
""");
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(5, 7)
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("Status");
    }

    [Fact]
    public async Task HoverHandler_QualifiedEnumConstantPattern_ShowsEnumMemberAsync()
    {
        const string text = """
import System.Text.Json.*

func Test(element: JsonElement) -> bool {
    return element.ValueKind is JsonValueKind.Array
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var markerOffset = text.IndexOf("JsonValueKind.Array", StringComparison.Ordinal);
        markerOffset.ShouldBeGreaterThanOrEqualTo(0);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(
                context.Value.SourceText,
                new TextSpan(markerOffset + "JsonValueKind.".Length + 1, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        var value = hover.Contents.MarkupContent!.Value;
        value.ShouldContain("Array");
        value.ShouldContain("JsonValueKind");
        value.ShouldNotContain("class Array");
        value.ShouldNotContain("Type in `System`");
    }

    [Fact]
    public async Task HoverHandler_LocalDeclarationRange_DoesNotCoverPipeInitializerInvocationAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.*

class Runner {
    static func Where(value: Int32, predicate: (Int32) -> bool) -> Int32 {
        return value
    }

    static func Main() -> unit {
        let query = 5
            |> Where(x => x > 1)

        query
    }
}
""");

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var queryHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(8, 12)
        }, CancellationToken.None);

        queryHover.ShouldNotBeNull();
        queryHover!.Range.ShouldNotBeNull();
        queryHover.Range.Start.Line.ShouldBe(8);
        queryHover.Range.End.Line.ShouldBe(8);
        queryHover.Range.End.Character.ShouldBeGreaterThan(queryHover.Range.Start.Character);

        var whereHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(9, 16)
        }, CancellationToken.None);

        whereHover.ShouldNotBeNull();
        whereHover!.Contents.ShouldNotBeNull();
        whereHover.Range.ShouldNotBeNull();
        whereHover.Range.Start.Line.ShouldBe(9);
    }

    [Fact]
    public async Task HoverHandler_EditCycle_MemberReceiverAndInvocationTargetStayDistinctAsync()
    {
        var initialText = """
import System.Console.*

class Counter {
    val Count: int => 1

    func Next() -> int {
        Count + 1
    }
}

func Main() -> unit {
    let counter = Counter()
    WriteLine(counter.Next())
}
""";
        var updatedText = """
import System.Console.*

class Counter {
    val Count: int => 1

    func Next() -> int {
        Count + 1
    }
}

func Main() -> unit {
    let padding = 0
    let counter = Counter()
    WriteLine(counter.Next())
}
""";

        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        await AssertMemberAccessHoverAsync(store, handler, uri, initialText, expectedLine: 12);
        await store.UpsertDocumentAsync(uri, updatedText);

        await AssertMemberAccessHoverAsync(store, handler, uri, updatedText, expectedLine: 13);
    }

    [Fact]
    public async Task HoverHandler_EditCycle_PropertyRemovedAndReadded_ResolvesPropertyHoverAsync()
    {
        var initialText = """
class Foo {
    val Test: string => ""
}

func Main() -> unit {
    let foo = Foo()
    foo.Test
}
""";
        var withoutPropertyText = """
class Foo {
}

func Main() -> unit {
    let foo = Foo()
    foo.Test
}
""";
        var restoredText = initialText;

        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        await AssertPropertyMemberHoverAsync(store, handler, uri, initialText);
        await store.UpsertDocumentAsync(uri, withoutPropertyText);
        var missingHover = await GetPropertyMemberHoverAsync(store, handler, uri, withoutPropertyText);
        missingHover?.Contents.MarkupContent?.Value.ShouldNotContain("val Test: string");
        await store.UpsertDocumentAsync(uri, restoredText);

        await AssertPropertyMemberHoverAsync(store, handler, uri, restoredText);
    }

    [Fact]
    public async Task HoverHandler_EditCycle_BodyDeclarationEdit_ResolvesUpdatedLocalHoverAsync()
    {
        var initialText = """
class Runner {
    func Main(value: int) -> int {
        let result = value
        return result
    }
}
""";
        var updatedText = """
class Runner {
    func Main(value: int) -> int {
        let renamed = value
        return renamed
    }
}
""";

        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        await AssertLocalHoverAsync(store, handler, uri, initialText, "return result", "result", "val result: int");
        await store.UpsertDocumentAsync(uri, updatedText);

        await AssertLocalHoverAsync(store, handler, uri, updatedText, "return renamed", "renamed", "val renamed: int");
    }

    [Fact]
    public async Task HoverHandler_NullableLocalReference_ShowsStaticTypeAtEveryPositionAsync()
    {
        const string text = """
func Length(value: string?) -> int {
    let copy: string? = value
    if copy is not null {
        return copy.Length
    }

    return 0
}
""";

        var results = await ReplayInlineHoversAsync(
            text,
            new HoverReplayTarget(
                "checked local",
                "copy is not null",
                1,
                "val copy: string?"),
            new HoverReplayTarget(
                "dereferenced local",
                "copy.Length",
                1,
                "val copy: string?"));

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HoverHandler_EditCycle_AddedOverload_ResolvesNewSignatureHoverAsync()
    {
        var initialText = """
class Runner {
    static func Pick(value: int) -> int {
        return value
    }

    static func Main() -> unit {
        Pick(1)
    }
}
""";
        var updatedText = """
class Runner {
    static func Pick(value: int) -> int {
        return value
    }

    static func Pick(text: string) -> string {
        return text
    }

    static func Main() -> unit {
        Pick("text")
    }
}
""";

        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        await store.UpsertDocumentAsync(uri, updatedText);

        await AssertMethodDeclarationHoverAsync(
            store,
            handler,
            uri,
            updatedText,
            "Pick(text: string)",
            "func Pick(text: string) -> string");
    }

    [Fact]
    public async Task HoverHandler_EditCycle_PipeInvocationTargetStaysMethodAsync()
    {
        var initialText = """
import System.*

class Runner {
    static func Where(value: Int32, predicate: (Int32) -> bool) -> Int32 {
        return value
    }

    static func Main() -> unit {
        let query = 5
            |> Where(x => x > 1)

        query
    }
}
""";
        var updatedText = """
import System.*

class Runner {
    static func Where(value: Int32, predicate: (Int32) -> bool) -> Int32 {
        return value
    }

    static func Main() -> unit {
        let padding = 0
        let query = 5
            |> Where(x => x > 1)

        query
    }
}
""";

        var (store, _, uri) = await CreateWorkspaceAsync(initialText);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        await AssertPipeWhereHoverAsync(store, handler, uri, initialText, expectedLine: 9);
        await store.UpsertDocumentAsync(uri, updatedText);

        await AssertPipeWhereHoverAsync(store, handler, uri, updatedText, expectedLine: 10);
    }

    [Fact]
    public async Task CompletionHandler_ClearedDocument_ReturnsWithoutOutOfBoundsFailureAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("let number = 42");
        await store.UpsertDocumentAsync(uri, string.Empty);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, 0)
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompletionHandler_LocalMacroDeclaration_UsesMacroSemanticProjectionAsync()
    {
        const string text = """
[LocalMacro]
class MacroSupport {
    val Value: int => 42

    func Read() -> int {
        return self.
    }
}

func Main() -> int => 0
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var completionOffset = text.IndexOf("self.", StringComparison.Ordinal) + "self.".Length;
        var position = PositionHelper.ToRange(
            context.Value.SourceText,
            new TextSpan(completionOffset, 0)).Start;

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position,
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("Value");
        completions.Items.Select(static item => item.Label).ShouldContain("Read");
    }

    [Fact]
    public async Task CompletionHandler_ImportNamespaceTrailingDot_ReturnsWildcardAndTypeMembersAsync()
    {
        const string text = "import System.Collections.Generic.";
        var (store, _, uri) = await CreateWorkspaceAsync(text);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, text.Length),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("List");
    }

    [Fact]
    public async Task CompletionHandler_ImportRootNamespaceTrailingDot_ReturnsWildcardAndMembersAsync()
    {
        const string text = "import System.";
        var (store, _, uri) = await CreateWorkspaceAsync(text);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, text.Length),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("String");
        var wildcard = completions.Items!.Single(static item => item.Label == "*");
        wildcard.TextEdit.ShouldNotBeNull();
        wildcard.TextEdit!.IsTextEdit.ShouldBeTrue();
        wildcard.TextEdit.TextEdit.Range.Start.Line.ShouldBe(0);
        wildcard.TextEdit.TextEdit.Range.Start.Character.ShouldBe(text.Length);
        wildcard.TextEdit.TextEdit.Range.End.Line.ShouldBe(0);
        wildcard.TextEdit.TextEdit.Range.End.Character.ShouldBe(text.Length);
    }

    [Fact]
    public async Task CompletionHandler_ImportRootNamespaceTrailingDot_DoesNotBindExpressionReceiverAsync()
    {
        const string text = """
import System.*
import System.Console.*
import System.Text.Json.*
import System.

let foo = 1
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(3, "import System.".Length),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);
        var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
        var delta = SemanticQueryInstrumentation.Subtract(after, before);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("String");
        var wildcard = completions.Items!.Single(static item => item.Label == "*");
        wildcard.TextEdit.ShouldNotBeNull();
        wildcard.TextEdit!.IsTextEdit.ShouldBeTrue();
        wildcard.TextEdit.TextEdit.Range.Start.Line.ShouldBe(3);
        wildcard.TextEdit.TextEdit.Range.Start.Character.ShouldBe("import System.".Length);
        wildcard.TextEdit.TextEdit.Range.End.Line.ShouldBe(3);
        wildcard.TextEdit.TextEdit.Range.End.Character.ShouldBe("import System.".Length);
        delta.SymbolInfoBinderFallbacks.ShouldBe(0);
        delta.TypeInfoBoundFallbacks.ShouldBe(0);
        delta.BoundNodeBindFallbacks.ShouldBe(0);
    }

    [Fact]
    public async Task CompletionHandler_ImportRootNamespaceTrailingDot_DoesNotWaitForSemanticGateAsync()
    {
        const string text = "import System.";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(
            uri,
            CancellationToken.None,
            "test-held-lease");

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, text.Length),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("String");
    }

    [Fact]
    public async Task CompletionHandler_ImportMetadataNamespaceTrailingDot_ReturnsWildcardAndMembersAsync()
    {
        const string text = "import System.Net.";
        var (store, _, uri) = await CreateWorkspaceAsync(text);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, text.Length),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("Http");
        completions.Items!.Select(static item => item.Label).ShouldContain("IPAddress");
    }

    [Theory]
    [InlineData("""
namespace App

import System.Collections.Generic.
""")]
    [InlineData("""
namespace App {
    import System.Collections.Generic.
}
""")]
    public async Task CompletionHandler_NamespaceScopedImportNamespaceTrailingDot_ReturnsWildcardAndTypeMembersAsync(string text)
    {
        var (store, _, uri) = await CreateWorkspaceAsync(text);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(
                (await store.GetAnalysisContextAsync(uri, CancellationToken.None))!.Value.SourceText,
                new TextSpan(text.LastIndexOf('.') + 1, 0)).Start,
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("*");
        completions.Items!.Select(static item => item.Label).ShouldContain("List");
    }

    [Fact]
    public async Task CompletionHandler_SameNamespaceSourceMemberPrefix_IncludesCrossFileFunctionAsync()
    {
        const string text = """
namespace Samples.App

func Main() -> unit {
    Tes
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text, "test.rvn");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        await store.UpsertDocumentAsync(testUri, """
namespace Samples.App

public func Test() -> unit {
}
""");

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(3, "    Tes".Length)
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        var testCompletion = completions.Items!.Single(static item => item.Label == "Test");
        testCompletion.LabelDetails.ShouldNotBeNull();
        testCompletion.LabelDetails!.Description.ShouldBe("Samples.App");
        testCompletion.LabelDetails.Description.ShouldNotContain("NamespaceMembers");
    }

    [Fact]
    public async Task CompletionHandler_GlobalNamespaceSourceMemberPrefix_IncludesCrossFileFunctionAsync()
    {
        const string text = """
func Main() -> unit {
    Tes
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text, "test.rvn");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        await store.UpsertDocumentAsync(testUri, """
public func Test() -> unit {
}
""");

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(1, "    Tes".Length)
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        var testCompletion = completions.Items!.Single(static item => item.Label == "Test");
        testCompletion.LabelDetails?.Description.ShouldNotBe("NamespaceMembers");
    }

    [Fact]
    public async Task CompletionHandler_GlobalTopLevelProgramSourceMemberPrefix_IncludesCrossFileFunctionAsync()
    {
        const string text = """
Tes
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text, "test.rvn");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        await store.UpsertDocumentAsync(testUri, """
public func Test() -> unit {
}
""");

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, "Tes".Length)
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("Test");
    }

    [Fact]
    public async Task CompletionHandler_MemberAccessOnLocalInitializedFromImportedNamespaceFunction_ReturnsMemberItemsAsync()
    {
        const string text = """
import Utilities.*

func Main() {
    Test()
    let x = A(42)
    x.
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text, "test.rvn");
        var testPath = Path.Combine(_tempRoot, "src", "test.rvn");
        var testUri = DocumentUri.FromFileSystemPath(testPath);
        await store.UpsertDocumentAsync(testUri, """
namespace Utilities

public func Test() -> unit {
}

public func A(value: int) -> int {
    value
}
""");
        var position = new Position(5, "    x.".Length);

        var handler = new CompletionHandler(store, NullLogger<CompletionHandler>.Instance);
        var completions = await handler.Handle(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position,
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.TriggerCharacter,
                TriggerCharacter = "."
            }
        }, CancellationToken.None);

        completions.ShouldNotBeNull();
        completions.Items.ShouldNotBeNull();
        completions.Items!.Select(static item => item.Label).ShouldContain("ToString");
        completions.Items!.Select(static item => item.Label).ShouldNotContain("return");
    }

    [Fact]
    public async Task GetAnalysisContextAsync_ClearedDocument_ReturnsCompilationOwnedSyntaxTreeAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("let number = 42");
        await store.UpsertDocumentAsync(uri, string.Empty);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);

        context.ShouldNotBeNull();
        context.Value.SourceText.ToString().ShouldBe(string.Empty);
        context.Value.Compilation.SyntaxTrees.ShouldContain(context.Value.SyntaxTree);
        Should.NotThrow(() => context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree));
    }

    [Fact]
    public async Task GetAnalysisContextAsync_RapidSuccessiveUpdates_StaysSnapshotConsistentAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("let number = 42");
        await store.UpsertDocumentAsync(uri, "let number = 100");
        await store.UpsertDocumentAsync(uri, """
record Payment(amount: int)

let payment = Payment(42)
""");

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);

        context.ShouldNotBeNull();
        context.Value.SourceText.ToString().ShouldContain("record Payment");
        context.Value.Compilation.SyntaxTrees.ShouldContain(context.Value.SyntaxTree);
        Should.NotThrow(() => context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree));
    }

    [Fact]
    public async Task GetSemanticModelAsync_ReturnsCurrentDocumentSemanticModel_AndInvalidatesOnUpdateAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        var firstContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        var firstModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);
        var secondContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        var secondModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);

        firstContext.ShouldNotBeNull();
        secondContext.ShouldNotBeNull();
        firstModel.ShouldNotBeNull();
        secondModel.ShouldNotBeNull();
        ReferenceEquals(firstContext.Value.SyntaxTree, secondContext.Value.SyntaxTree).ShouldBeTrue();
        Should.NotThrow(() => firstModel.GetDiagnostics());
        Should.NotThrow(() => secondModel.GetDiagnostics());
        await store.UpsertDocumentAsync(uri, """
func Main() -> () {
    let value = 100
}
""");

        var updatedContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        var updatedModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);

        updatedContext.ShouldNotBeNull();
        updatedModel.ShouldNotBeNull();
        updatedContext.Value.SourceText.ToString().ShouldContain("value = 100");
        ReferenceEquals(firstContext.Value.SyntaxTree, updatedContext.Value.SyntaxTree).ShouldBeFalse();
        Should.NotThrow(() => updatedModel.GetDiagnostics());
    }

    [Fact]
    public async Task GetAnalysisContextAsync_ProjectBackedDocumentFullReplacement_DoesNotRetainStaleGenericScopeDiagnosticsAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.*

func Main() -> () {
    let value = 42
    Console.WriteLine(value)
}
""");
        await store.UpsertDocumentAsync(uri, """
import System.*
import System.Console.*

func Main() -> () {
    let r = Parse<int>("42")
    WriteLine(r)
}

func Parse<T>(str: string) -> T
    where T: IParsable<T>
    => T.Parse(str, null)
""");

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal)).ShouldBeFalse();

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        context.Value.Compilation.SyntaxTrees.ShouldContain(context.Value.SyntaxTree);
        Should.NotThrow(() => context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree));
    }

    [Fact]
    public async Task GetAnalysisContextAsync_WorkspaceReload_DoesNotReuseStaleCachedAnalysisAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var projectPath = Path.Combine(_tempRoot, "App.rvnproj");
        File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
import System.*

func Main() -> () {
    let value = 42
    Console.WriteLine(value)
}
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
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var firstContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        firstContext.ShouldNotBeNull();

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var secondContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        secondContext.ShouldNotBeNull();
        secondContext.Value.Document.Id.ShouldNotBe(firstContext.Value.Document.Id);
        secondContext.Value.Document.Project.Id.ShouldNotBe(firstContext.Value.Document.Project.Id);
        secondContext.Value.Compilation.SyntaxTrees.ShouldContain(secondContext.Value.SyntaxTree);
        Should.NotThrow(() => secondContext.Value.Compilation.GetSemanticModel(secondContext.Value.SyntaxTree));
    }

    [Fact]
    public async Task GetSemanticModelAsync_ProjectVersionChangeWithoutDocumentEdit_InvalidatesCachedAnalysisAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var projectPath = Path.Combine(_tempRoot, "App.rvnproj");
        File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var mainFilePath = Path.Combine(_tempRoot, "src", "main.rvn");
        var helperFilePath = Path.Combine(_tempRoot, "src", "helper.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFilePath)!);
        File.WriteAllText(mainFilePath, """
func Main() -> () {
    Helper()
}
""");
        File.WriteAllText(helperFilePath, """
func Helper() -> () { }
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
        var mainUri = DocumentUri.FromFileSystemPath(mainFilePath);
        var helperUri = DocumentUri.FromFileSystemPath(helperFilePath);

        var firstContext = await store.GetAnalysisContextAsync(mainUri, CancellationToken.None);
        var firstModel = await store.GetSemanticModelAsync(mainUri, CancellationToken.None);

        firstContext.ShouldNotBeNull();
        firstModel.ShouldNotBeNull();
        await store.UpsertDocumentAsync(helperUri, """
func Helper() -> () {
    let answer = 42
}
""");

        var secondContext = await store.GetAnalysisContextAsync(mainUri, CancellationToken.None);
        var secondModel = await store.GetSemanticModelAsync(mainUri, CancellationToken.None);

        secondContext.ShouldNotBeNull();
        secondModel.ShouldNotBeNull();
        secondContext.Value.Document.Version.ShouldBe(firstContext.Value.Document.Version);
        secondContext.Value.Document.Project.Version.ShouldNotBe(firstContext.Value.Document.Project.Version);
        ReferenceEquals(firstContext.Value.Compilation, secondContext.Value.Compilation).ShouldBeFalse();
        secondContext.Value.Compilation.SyntaxTrees.ShouldContain(secondContext.Value.SyntaxTree);
    }

    [Fact]
    public async Task WarmAnalysisAsync_UsesCompilerOwnedSemanticModelWhileDocumentSemanticGateIsHeldAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        await store.WarmAnalysisAsync(uri, shouldSkipWork: null, CancellationToken.None);

        var warmedModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var returnedModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);

        ReferenceEquals(warmedModel, returnedModel).ShouldBeTrue();
    }

    [Fact]
    public async Task WarmAnalysisAsync_WaitsForCompilerGateInsteadOfSkippingAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        using var compilerLease = await store.EnterCompilerAccessAsync(CancellationToken.None, "test", uri);
        var warmupTask = store.WarmAnalysisAsync(uri, shouldSkipWork: null, CancellationToken.None);

        await Task.Delay(100);
        warmupTask.IsCompleted.ShouldBeFalse();

        compilerLease.Dispose();
        await warmupTask;

        var returnedModel = await store.GetSemanticModelAsync(uri, CancellationToken.None);
        returnedModel.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterDocumentSemanticModelAccessAsync_ReturnsCompilerOwnedSemanticModelAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var expectedModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        using var access = await store.EnterDocumentSemanticModelAccessAsync(uri, CancellationToken.None, "test");

        access.SemanticModel.ShouldNotBeNull();
        ReferenceEquals(expectedModel, access.SemanticModel).ShouldBeTrue();
    }

    [Fact]
    public async Task EnterDocumentSemanticModelAccessAsync_WithContext_UsesProvidedSnapshotAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        var firstContext = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        firstContext.ShouldNotBeNull();
        var expectedModel = firstContext.Value.Compilation.GetSemanticModel(firstContext.Value.SyntaxTree);

        await store.UpsertDocumentAsync(uri, """
func Main() -> () {
    let value = 100
}
""");

        using var access = await store.EnterDocumentSemanticModelAccessAsync(
            uri,
            firstContext.Value,
            CancellationToken.None,
            "test");

        access.SemanticModel.ShouldNotBeNull();
        ReferenceEquals(expectedModel, access.SemanticModel).ShouldBeTrue();
        ReferenceEquals(firstContext.Value.SyntaxTree, access.SemanticModel!.SyntaxTree).ShouldBeTrue();
    }

    [Fact]
    public async Task EnterDocumentSemanticModelAccessAsync_DoesNotWaitForCompilerGateAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        using var compilerLease = await store.EnterCompilerAccessAsync(CancellationToken.None, "test", uri);

        var accessTask = store.EnterDocumentSemanticModelAccessAsync(uri, CancellationToken.None, "hover").AsTask();
        var completedTask = await Task.WhenAny(accessTask, Task.Delay(1000));

        completedTask.ShouldBe(accessTask);
        using var access = await accessTask;
        access.SemanticModel.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryEnterDocumentSemanticModelAccessAsync_DoesNotWaitForCompilerGateAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        using var compilerLease = await store.EnterCompilerAccessAsync(CancellationToken.None, "test", uri);

        var access = await store.TryEnterDocumentSemanticModelAccessAsync(uri, CancellationToken.None, "inlayHint");

        access.ShouldNotBeNull();
        access.SemanticModel.ShouldNotBeNull();
        access.Dispose();
    }

    [Fact]
    public async Task TryEnterDocumentSemanticModelAccessAsync_SkipsWhenDocumentSemanticGateIsBusyAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
func Main() -> () {
    let number = 42
}
""");

        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var access = await store.TryEnterDocumentSemanticModelAccessAsync(uri, CancellationToken.None, "inlayHint");

        access.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_WaitsForDocumentSemanticGateInsteadOfReturningNullAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    WriteLine(number)
}
""");

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        await Task.Delay(100);
        hoverTask.IsCompleted.ShouldBeFalse();

        semanticLease.Dispose();
        var hover = await hoverTask;

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("number");
    }

    [Fact]
    public async Task HoverHandler_PreemptsBackgroundSemanticWorkBeforeWaitingForDocumentSemanticGateAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    WriteLine(number)
}
""");

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");
        using var backgroundCancellation = store.CreateBackgroundSemanticWorkCancellation(CancellationToken.None);

        backgroundCancellation.Token.IsCancellationRequested.ShouldBeFalse();
        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        await Task.Delay(100);
        backgroundCancellation.Token.IsCancellationRequested.ShouldBeTrue();
        hoverTask.IsCompleted.ShouldBeFalse();

        semanticLease.Dispose();
        var hover = await hoverTask;

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("number");
    }

    [Fact]
    public async Task HoverHandler_NewerDocumentHoverSupersedesBlockedOlderHoverAsync()
    {
        var (store, _, uri) = await CreateWorkspaceAsync("""
import System.Console.*

func Main() -> unit {
    let number = 42
    WriteLine(number)
}
""");

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var firstHoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        await Task.Delay(100);
        firstHoverTask.IsCompleted.ShouldBeFalse();

        var secondHoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(4, 14)
        }, CancellationToken.None);

        var firstCompletedTask = await Task.WhenAny(firstHoverTask, Task.Delay(1000));
        firstCompletedTask.ShouldBe(firstHoverTask);
        var firstHover = await firstHoverTask;
        firstHover.ShouldBeNull();

        secondHoverTask.IsCompleted.ShouldBeFalse();

        semanticLease.Dispose();
        var secondHover = await secondHoverTask;

        secondHover.ShouldNotBeNull();
        secondHover!.Contents.MarkupContent.ShouldNotBeNull();
        secondHover.Contents.MarkupContent!.Value.ShouldContain("number");
    }

    [Fact]
    public async Task HoverHandler_TypeDeclarationIdentifier_UsesSyntaxHoverWithoutSemanticGateAsync()
    {
        var text = """
record Foo(
    val Name: string,
    val Status: Status
    val Test: int | bool
)

[RavenTaggedUnionJsonConverter("kind")]
union Status {
    case Active
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var statusOffset = text.IndexOf("Status {", StringComparison.Ordinal);
        statusOffset.ShouldBeGreaterThanOrEqualTo(0);
        var position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(statusOffset, 0)).Start;
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = position
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("union Status");
        hover.Contents.MarkupContent!.Value.ShouldContain("Union");
    }

    [Fact]
    public async Task HoverHandler_PrimaryConstructorParameterDeclaration_UsesSyntaxHoverWithoutSemanticGateAsync()
    {
        var text = """
record Foo(
    val Name: string,
    val Status: Status
)

union Status {
    case Active
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var nameOffset = text.IndexOf("Name: string", StringComparison.Ordinal);
        nameOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(nameOffset, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("val Name: string");
        hover.Contents.MarkupContent!.Value.ShouldContain("Property in `Foo`");
    }

    [Fact]
    public async Task HoverHandler_MethodDeclarationIdentifier_UsesSyntaxHoverWithoutSemanticGateAsync()
    {
        var text = """
class Runner {
    static func Pick(text: string) -> string {
        return text
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var pickOffset = text.IndexOf("Pick(text: string)", StringComparison.Ordinal);
        pickOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(pickOffset + 1, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("static func Pick(text: string) -> string");
        hover.Contents.MarkupContent!.Value.ShouldContain("Method in `class Runner`");
    }

    [Fact]
    public async Task HoverHandler_MethodParameterDeclaration_UsesSyntaxHoverWithoutSemanticGateAsync()
    {
        var text = """
class Runner {
    static async func Main(args: string[]) -> Task {
        return Task.CompletedTask
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var argsOffset = text.IndexOf("args: string[]", StringComparison.Ordinal);
        argsOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(argsOffset + 2, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("args: string[]");
        hover.Contents.MarkupContent!.Value.ShouldContain("Parameter in `static async func Main(args: string[]) -> Task`");
    }

    [Fact]
    public async Task HoverHandler_UnionCaseParameterDeclaration_UsesSyntaxHoverWithoutSemanticGateAsync()
    {
        var text = """
union VehicleStatus {
    case Decommissioned(retiredUtc: DateTimeOffset, reason: string)
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var parameterOffset = text.IndexOf("retiredUtc: DateTimeOffset", StringComparison.Ordinal);
        parameterOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(parameterOffset + 2, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("retiredUtc: DateTimeOffset");
        hover.Contents.MarkupContent!.Value.ShouldContain("Parameter in `case Decommissioned(retiredUtc: DateTimeOffset, reason: string)`");
    }

    [Fact]
    public async Task HoverHandler_ArgumentIdentifier_ConsistentlyShowsLocalSymbolAsync()
    {
        var text = """
class Options {
}

record Foo(val Name: string)

func Test(foo: Foo, options: Options) -> string {
    "ok"
}

let foo = Foo("Foo")
let options = Options()
let str = Test(foo, options)
WriteLine(str)
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var fooOffset = text.IndexOf("foo, options", StringComparison.Ordinal);
        fooOffset.ShouldBeGreaterThanOrEqualTo(0);
        var optionsOffset = fooOffset + "foo, ".Length;

        for (var i = 0; i < "foo".Length; i++)
        {
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(fooOffset + i, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull($"foo character offset {i}");
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            var value = hover.Contents.MarkupContent!.Value;
            value.ShouldContain("val foo: Foo");
            value.ShouldNotContain("```raven\n()\n```");
        }

        for (var i = 0; i < "options".Length; i++)
        {
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(optionsOffset + i, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull($"options character offset {i}");
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            var value = hover.Contents.MarkupContent!.Value;
            value.ShouldContain("val options: Options");
            value.ShouldNotContain("val options: ()");
            value.ShouldNotContain("```raven\n()\n```");
        }

        var strOffset = text.IndexOf("WriteLine(str)", StringComparison.Ordinal);
        strOffset.ShouldBeGreaterThanOrEqualTo(0);
        strOffset += "WriteLine(".Length;

        for (var i = 0; i < "str".Length; i++)
        {
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(strOffset + i, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull($"str character offset {i}");
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            var value = hover.Contents.MarkupContent!.Value;
            value.ShouldContain("val str: string");
            value.ShouldNotContain("```raven\n()\n```");
        }
    }

    [Fact]
    public async Task HoverHandler_AwaitKeyword_ReturnsNullWithoutSemanticGateAsync()
    {
        var text = """
import System.Threading.Tasks.*

class Runner {
    static async func Main() -> Task {
        _ = await Task.CompletedTask
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var awaitOffset = text.IndexOf("await", StringComparison.Ordinal);
        awaitOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(awaitOffset + 2, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_ReturnKeyword_ReturnsNullWithoutSemanticGateAsync()
    {
        var text = """
class Runner {
    static func Main() -> int {
        return 1
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var returnOffset = text.IndexOf("return", StringComparison.Ordinal);
        returnOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(returnOffset + 2, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_EqualsToken_ReturnsNullWithoutSemanticGateAsync()
    {
        var text = """
class Runner {
    static func Main() -> unit {
        let value = 1
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var equalsOffset = text.IndexOf("=", StringComparison.Ordinal);
        equalsOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(equalsOffset, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_DiscardAssignmentUnderscore_ReturnsNullWithoutSemanticGateAsync()
    {
        var text = """
class Runner {
    static func Main() -> unit {
        _ = 1
    }
}
""";
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var discardOffset = text.IndexOf("_", StringComparison.Ordinal);
        discardOffset.ShouldBeGreaterThanOrEqualTo(0);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        using var semanticLease = await store.EnterDocumentSemanticAccessAsync(uri, CancellationToken.None, "test");

        var hoverTask = handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(discardOffset, 0)).Start
        }, CancellationToken.None);

        var completed = await Task.WhenAny(hoverTask, Task.Delay(1000));
        completed.ShouldBe(hoverTask);

        var hover = await hoverTask;
        hover.ShouldBeNull();
    }

    [Fact]
    public async Task HoverHandler_ProjectBackedExplicitTypeIdentifiers_ShowNamedTypeSignaturesAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var projectPath = Path.Combine(_tempRoot, "AspNetMinimalApi.rvnproj");
        File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AspNetMinimalApi</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var text = """
func ping(name: string) -> PingResult {
    PingResult("pong $name")
}

func ping1(name: string) -> Result<PingResult, CustomError> {
    match name {
        "Bob" | "bob" => Ok(PingResult("pong $name"))
        _ => Error(CustomError("Invalid name"))
    }
}

record PingResult(val Message: string)
record CustomError(val Message: string)
""";
        File.WriteAllText(filePath, text);

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
        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var semanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var root = context.Value.SyntaxTree.GetRoot();

        var sourceText = context.Value.SourceText;
        var pingResultOffset = text.IndexOf("-> PingResult", StringComparison.Ordinal);
        pingResultOffset.ShouldBeGreaterThanOrEqualTo(0);
        pingResultOffset += "-> ".Length + 2;

        var ping1ResultOffset = text.IndexOf("Result<PingResult, CustomError>", StringComparison.Ordinal);
        ping1ResultOffset.ShouldBeGreaterThanOrEqualTo(0);
        var customErrorOffset = ping1ResultOffset + "Result<PingResult, ".Length + 2;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var pingResultHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(pingResultOffset, 0)).Start
        }, CancellationToken.None);

        pingResultHover.ShouldNotBeNull();
        pingResultHover.Contents.MarkupContent.ShouldNotBeNull();
        pingResultHover.Contents.MarkupContent!.Value.ShouldContain("PingResult");
        pingResultHover.Contents.MarkupContent!.Value.ShouldNotContain("```raven\n()\n```");

        var customErrorHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(customErrorOffset, 0)).Start
        }, CancellationToken.None);

        customErrorHover.ShouldNotBeNull();
        customErrorHover.Contents.MarkupContent.ShouldNotBeNull();
        customErrorHover.Contents.MarkupContent!.Value.ShouldContain("CustomError");
        customErrorHover.Contents.MarkupContent!.Value.ShouldNotContain("```raven\n()\n```");
    }

    [Fact]
    public async Task HoverHandler_RepoAspNetSample_ExplicitTypeIdentifiers_ShowNamedTypeSignaturesAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "aspnet-minimal-api");
        var filePath = Path.Combine(projectRoot, "src", "Domain.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "aspnet-minimal-api",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var semanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var root = context.Value.SyntaxTree.GetRoot();

        var sourceText = context.Value.SourceText;
        var petOffset = text.IndexOf("-> Pet {", StringComparison.Ordinal);
        petOffset.ShouldBeGreaterThanOrEqualTo(0);
        petOffset += "-> ".Length + 1;

        var vaccinationStatusOffset = text.IndexOf("Task<VaccinationStatus>", StringComparison.Ordinal);
        vaccinationStatusOffset.ShouldBeGreaterThanOrEqualTo(0);
        vaccinationStatusOffset += "Task<".Length + 2;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        foreach (var (offset, expectedName) in new[]
                 {
                     (petOffset, "Pet"),
                     (vaccinationStatusOffset, "VaccinationStatus")
                 })
        {
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(sourceText, new TextSpan(offset, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull();
            hover.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(expectedName);
            hover.Contents.MarkupContent!.Value.ShouldNotContain("```raven\n()\n```");
        }
    }

    [Fact]
    public async Task HoverHandler_RepoAspNetSample_UnionCaseIdentifiers_DoNotFallbackToUnitAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "aspnet-minimal-api");
        var filePath = Path.Combine(projectRoot, "src", "Domain.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "aspnet-minimal-api",
                Uri = DocumentUri.FromFileSystemPath(projectRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var semanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var root = context.Value.SyntaxTree.GetRoot();

        var sourceText = context.Value.SourceText;
        var currentOffset = text.IndexOf("VaccinationStatus.Current", StringComparison.Ordinal);
        currentOffset.ShouldBeGreaterThanOrEqualTo(0);
        currentOffset += "VaccinationStatus.".Length + 1;

        var dueOffset = text.IndexOf("VaccinationStatus.Due", StringComparison.Ordinal);
        dueOffset.ShouldBeGreaterThanOrEqualTo(0);
        dueOffset += "VaccinationStatus.".Length + 1;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        foreach (var (offset, expectedName) in new[]
                 {
                     (currentOffset, "Current"),
                     (dueOffset, "Due")
                 })
        {
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(sourceText, new TextSpan(offset, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull();
            hover.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(expectedName);
            hover.Contents.MarkupContent!.Value.ShouldNotContain("```raven\n()\n```");
        }
    }

    [Fact]
    public async Task HoverHandler_RepoEfCoreSample_PipeInvocationTarget_DoesNotReuseLocalHoverAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
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
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var sourceText = context.Value.SourceText;
        var queryOffset = text.IndexOf("let query =", StringComparison.Ordinal);
        queryOffset.ShouldBeGreaterThanOrEqualTo(0);
        queryOffset += "let ".Length + 2;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        var queryHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(queryOffset, 0)).Start
        }, CancellationToken.None);

        queryHover.ShouldNotBeNull();
        queryHover!.Range.ShouldNotBeNull();
        queryHover.Range.Start.Line.ShouldBe(queryHover.Range.End.Line);

        await AssertPipeTargetHoverAsync("        |> Where(onlyActiveAdults)", "Where", hover =>
        {
            hover.ShouldContain("System.Linq.Queryable");
            hover.ShouldContain("Expression<");
            hover.ShouldNotContain("(User, int) -> bool");
        });
        await AssertPipeTargetHoverAsync("        |> OrderBy(user => user.Name)", "OrderBy", hover =>
        {
            hover.ShouldContain("System.Linq.Queryable");
            hover.ShouldNotContain("System.Linq.Enumerable");
            hover.ShouldNotContain("IEnumerable<TSource>");
        });
        await AssertPipeTargetHoverAsync("        |> Select(user => user.Name)", "Select", hover =>
        {
            hover.ShouldContain("System.Linq.Queryable");
            hover.ShouldNotContain("System.Linq.Enumerable");
        });

        async Task AssertPipeTargetHoverAsync(string lineText, string targetName, Action<string> assertHover)
        {
            var lineOffset = text.IndexOf(lineText, StringComparison.Ordinal);
            lineOffset.ShouldBeGreaterThanOrEqualTo(0);
            var column = lineText.IndexOf(targetName, StringComparison.Ordinal);
            column.ShouldBeGreaterThanOrEqualTo(0);

            foreach (var delta in new[] { 0, Math.Min(2, targetName.Length - 1), targetName.Length - 1 })
            {
                var offset = lineOffset + column + delta;
                var hover = await handler.Handle(new HoverParams
                {
                    TextDocument = new TextDocumentIdentifier(uri),
                    Position = PositionHelper.ToRange(sourceText, new TextSpan(offset, 0)).Start
                }, CancellationToken.None);

                hover.ShouldNotBeNull();
                hover.Contents.MarkupContent.ShouldNotBeNull();
                var hoverText = hover.Contents.MarkupContent!.Value;
                hoverText.ShouldContain(targetName);
                hoverText.ShouldNotContain("let query");
                assertHover(hoverText);
                hover.Range.ShouldNotBeNull();
                hover.Range.Start.Line.ShouldBe(hover.Range.End.Line);
            }
        }
    }

    [Fact]
    public async Task HoverHandler_RepoEfCoreExpressionTrees_DbSetAddHover_DoesNotThrowOnMetadataLoadContextAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
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
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var addOffset = text.IndexOf("db.Users.Add(.(3", StringComparison.Ordinal);
        addOffset.ShouldBeGreaterThanOrEqualTo(0);
        addOffset += "db.Users.".Length + 1;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(addOffset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("Add");
        hover.Contents.MarkupContent!.Value.ShouldContain("User");
    }

    [Fact]
    public async Task HoverHandler_RepoEfCoreSample_LambdaParameter_ShowsLambdaContainingSignatureAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
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
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var sourceText = context.Value.SourceText;
        var lambdaOffset = text.IndexOf("OrderBy(user => user.Name)", StringComparison.Ordinal);
        lambdaOffset.ShouldBeGreaterThanOrEqualTo(0);
        lambdaOffset += "OrderBy(".Length + 1;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(lambdaOffset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("user: User");
        hover.Contents.MarkupContent!.Value.ShouldContain("Parameter in `func (user: User) -> string`");
    }

    [Fact]
    public async Task HoverHandler_InlineExtensionLambdaReplay_ResolvesOrchestratedTargetsWithoutBindingFallbackAsync()
    {
        const string text = """
class ServiceCollection {
}

class DbContextOptionsBuilder {
}

class VehicleDbContext {
}

class ConnectionFactory {
    static func GetConnectionString() -> string {
        return "Host=localhost"
    }
}

class Runner {
    func Configure(services: ServiceCollection) -> ServiceCollection {
        services.AddDbContext<VehicleDbContext>(func (options: DbContextOptionsBuilder) {
            options.UseProvider(ConnectionFactory.GetConnectionString())
        })
    }
}

extension ServiceCollectionExtensions for ServiceCollection {
    func AddDbContext<T>(configure: (DbContextOptionsBuilder -> ())?) -> ServiceCollection {
        return ServiceCollection()
    }
}

extension DbContextOptionsBuilderExtensions for DbContextOptionsBuilder {
    func UseProvider(connectionString: string) -> DbContextOptionsBuilder {
        return DbContextOptionsBuilder()
    }
}
""";

        var results = await ReplayInlineHoversAsync(
            text,
            new HoverReplayTarget("AddDbContext", "AddDbContext<", 2, "AddDbContext"),
            new HoverReplayTarget("options", "options.UseProvider", 2, "options: DbContextOptionsBuilder"),
            new HoverReplayTarget("UseProvider", "UseProvider(", 2, "UseProvider"),
            new HoverReplayTarget("ConnectionFactory", "ConnectionFactory.GetConnectionString", 2, "ConnectionFactory"),
            new HoverReplayTarget("UseProvider again", "UseProvider(", 2, "UseProvider"));

        foreach (var result in results)
        {
            result.SemanticDelta.SymbolInfoBinderFallbacks.ShouldBe(0);
            result.SemanticDelta.TypeInfoBoundFallbacks.ShouldBe(0);
        }
    }

    [Fact]
    public async Task HoverHandler_RepoEfCoreSample_ReplaysRecordedHoverPositionsWithoutBindingFallbackAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-vehicle-costs");
        var filePath = Path.Combine(projectRoot, "src", "Api", "Main.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var text = File.ReadAllText(filePath);
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
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var targets = new[]
        {
            CreateHoverPositionTarget(context.Value.SourceText, text, "UseNpgsql", "UseNpgsql", "UseNpgsql"),
            CreateHoverPositionTarget(context.Value.SourceText, text, "Task", "Task {", "class Task"),
            CreateHoverPositionTarget(context.Value.SourceText, text, "CreateBuilder", "CreateBuilder", "CreateBuilder"),
            CreateHoverPositionTarget(context.Value.SourceText, text, "builder", "builder.Services", "builder: WebApplicationBuilder"),
            CreateHoverPositionTarget(context.Value.SourceText, text, "VehicleDbContext", "VehicleDbContext", "VehicleDbContext")
        };

        foreach (var target in targets)
        {
            var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(target.Line, target.Character)
            }, CancellationToken.None);
            var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var delta = SemanticQueryInstrumentation.Subtract(after, before);

            hover.ShouldNotBeNull();
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(target.ExpectedText);
            delta.SymbolInfoBinderFallbacks.ShouldBe(0);
            delta.TypeInfoBoundFallbacks.ShouldBe(0);
            delta.BoundNodeBindFallbacks.ShouldBe(0);
        }
    }

    [Fact]
    public async Task GetAnalysisContextAsync_RepoEfCoreSample_MinAgeEdit_DoesNotPoisonDiagnosticsOrHoverAsync()
    {
        var repoRoot = FindRepositoryRoot();
        var projectRoot = Path.Combine(repoRoot, "samples", "projects", "efcore-expression-trees");
        var filePath = Path.Combine(projectRoot, "src", "Program.rvn");
        File.Exists(filePath).ShouldBeTrue();

        var originalText = File.ReadAllText(filePath);
        var minAgeMatch = Regex.Match(originalText, @"let minAge = (?<value>\d+)");
        minAgeMatch.Success.ShouldBeTrue();
        var currentMinAge = int.Parse(minAgeMatch.Groups["value"].Value);
        var updatedText = string.Concat(
            originalText.AsSpan(0, minAgeMatch.Index),
            $"let minAge = {currentMinAge + 1}",
            originalText.AsSpan(minAgeMatch.Index + minAgeMatch.Length));
        updatedText.ShouldNotBe(originalText);

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
        _ = await store.UpsertDocumentAsync(uri, originalText);
        _ = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        await store.UpsertDocumentAsync(uri, updatedText);

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, "RAV0103", StringComparison.Ordinal)).ShouldBeFalse();
        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code?.String, "RAV0168", StringComparison.Ordinal)).ShouldBeFalse();

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();
        var semanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var root = context.Value.SyntaxTree.GetRoot();

        var sourceText = context.Value.SourceText;
        var whereLineText = "        |> Where(onlyActiveAdults)";
        var whereLineOffset = updatedText.IndexOf(whereLineText, StringComparison.Ordinal);
        whereLineOffset.ShouldBeGreaterThanOrEqualTo(0);
        var whereColumn = whereLineText.IndexOf("Where", StringComparison.Ordinal);
        whereColumn.ShouldBeGreaterThanOrEqualTo(0);
        var whereOffset = whereLineOffset + whereColumn;
        var whereIdentifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "Where");
        var whereSymbolInfo = semanticModel.GetSymbolInfo(whereIdentifier);
        var whereSymbol = whereSymbolInfo.Symbol ?? whereSymbolInfo.CandidateSymbols.FirstOrDefault();
        var retrySemanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        var retryWhereSymbolInfo = retrySemanticModel.GetSymbolInfo(whereIdentifier);
        var retryWhereSymbol = retryWhereSymbolInfo.Symbol ?? retryWhereSymbolInfo.CandidateSymbols.FirstOrDefault();
        Assert.True(whereSymbol is IMethodSymbol || retryWhereSymbol is IMethodSymbol);

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);

        foreach (var delta in new[] { 0, 2, 4 })
        {
            whereOffset = whereLineOffset + whereColumn + delta;
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = PositionHelper.ToRange(sourceText, new TextSpan(whereOffset, 0)).Start
            }, CancellationToken.None);

            hover.ShouldNotBeNull();
            hover.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain("Where");
            hover.Contents.MarkupContent!.Value.ShouldNotContain("val query: Error");
        }
    }

    [Fact]
    public async Task HoverHandler_NamedFunctionExpressionDeclaration_UsesSyntaxSignatureWithoutBroadBindingAsync()
    {
        const string text = """
class C {
    func Run() -> int {
        let seed = 1
        let compute = func Step(n: int) -> int {
            if n < 1
                seed
            else
                Step(n - 1)
        }

        compute(3)
    }
}
""";

        var uri = DocumentUri.FromFileSystemPath(Path.Combine(_tempRoot, "named-lambda-hover.rav"));
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
        _ = await store.UpsertDocumentAsync(uri, text);

        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var sourceText = context.Value.SourceText;
        var offset = text.IndexOf("Step(n: int)", StringComparison.Ordinal);
        offset.ShouldBeGreaterThanOrEqualTo(0);
        offset += 2;

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(offset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("func Step(n: int) -> int");
        hover.Contents.MarkupContent!.Value.ShouldContain("Function in `C`");
    }

    private static async Task AssertMemberAccessHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text,
        int expectedLine)
    {
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var sourceText = context.Value.SourceText;
        var receiverOffset = text.IndexOf("counter.Next()", StringComparison.Ordinal);
        receiverOffset.ShouldBeGreaterThanOrEqualTo(0);
        var targetOffset = receiverOffset + "counter.".Length + 1;

        var receiverHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(receiverOffset + 2, 0)).Start
        }, CancellationToken.None);

        receiverHover.ShouldNotBeNull();
        receiverHover!.Contents.MarkupContent.ShouldNotBeNull();
        receiverHover.Contents.MarkupContent!.Value.ShouldContain("counter");
        receiverHover.Contents.MarkupContent!.Value.ShouldNotContain("func Next");
        receiverHover.Range.ShouldNotBeNull();
        receiverHover.Range.Start.Line.ShouldBe(expectedLine);

        var targetHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(targetOffset, 0)).Start
        }, CancellationToken.None);

        targetHover.ShouldNotBeNull();
        targetHover!.Contents.MarkupContent.ShouldNotBeNull();
        targetHover.Contents.MarkupContent!.Value.ShouldContain("Next");
        targetHover.Contents.MarkupContent!.Value.ShouldNotContain("let counter");
        targetHover.Range.ShouldNotBeNull();
        targetHover.Range.Start.Line.ShouldBe(expectedLine);
    }

    private static async Task AssertPropertyMemberHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text)
    {
        var hover = await GetPropertyMemberHoverAsync(store, handler, uri, text);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("val Test: string");
        hover.Contents.MarkupContent!.Value.ShouldContain("Property in `class Foo`");
    }

    private static async Task<Hover?> GetPropertyMemberHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text)
    {
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var testOffset = text.IndexOf("foo.Test", StringComparison.Ordinal);
        testOffset.ShouldBeGreaterThanOrEqualTo(0);
        testOffset += "foo.".Length + 1;

        return await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(testOffset, 0)).Start
        }, CancellationToken.None);
    }

    private static async Task AssertPipeWhereHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text,
        int expectedLine)
    {
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var sourceText = context.Value.SourceText;
        var whereOffset = text.IndexOf("|> Where", StringComparison.Ordinal);
        whereOffset.ShouldBeGreaterThanOrEqualTo(0);
        whereOffset += "|> ".Length + 1;

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(whereOffset, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("Where");
        hover.Contents.MarkupContent!.Value.ShouldNotContain("let query");
        hover.Range.ShouldNotBeNull();
        hover.Range.Start.Line.ShouldBe(expectedLine);
    }

    private static async Task AssertLocalHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text,
        string marker,
        string symbolName,
        string expectedSignature)
    {
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var markerOffset = text.IndexOf(marker, StringComparison.Ordinal);
        markerOffset.ShouldBeGreaterThanOrEqualTo(0);
        var symbolOffset = text.IndexOf(symbolName, markerOffset, StringComparison.Ordinal);
        symbolOffset.ShouldBeGreaterThanOrEqualTo(0);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(symbolOffset + 1, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain(expectedSignature);
        hover.Range.ShouldNotBeNull();
    }

    private static async Task AssertMethodDeclarationHoverAsync(
        DocumentStore store,
        HoverHandler handler,
        DocumentUri uri,
        string text,
        string marker,
        string expectedSignature)
    {
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var markerOffset = text.IndexOf(marker, StringComparison.Ordinal);
        markerOffset.ShouldBeGreaterThanOrEqualTo(0);

        var hover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(context.Value.SourceText, new TextSpan(markerOffset + 1, 0)).Start
        }, CancellationToken.None);

        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain(expectedSignature);
        hover.Contents.MarkupContent!.Value.ShouldContain("Method in `class Runner`");
        hover.Range.ShouldNotBeNull();
    }

    private async Task<IReadOnlyList<HoverReplayResult>> ReplayInlineHoversAsync(
        string text,
        params HoverReplayTarget[] targets)
    {
        var (store, _, uri) = await CreateWorkspaceAsync(text);
        var context = await store.GetAnalysisContextAsync(uri, CancellationToken.None);
        context.ShouldNotBeNull();

        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var results = new List<HoverReplayResult>(targets.Length);

        foreach (var target in targets)
        {
            var targetOffset = IndexOfOccurrence(text, target.SearchText, target.Occurrence);
            targetOffset.ShouldBeGreaterThanOrEqualTo(0);

            var hoverPosition = PositionHelper.ToRange(
                context.Value.SourceText,
                new TextSpan(targetOffset + target.CharacterOffset, 0)).Start;
            var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var hover = await handler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = hoverPosition
            }, CancellationToken.None);
            var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var delta = SemanticQueryInstrumentation.Subtract(after, before);

            hover.ShouldNotBeNull();
            hover!.Contents.MarkupContent.ShouldNotBeNull();
            hover.Contents.MarkupContent!.Value.ShouldContain(target.ExpectedText);

            results.Add(new HoverReplayResult(target.Label, hoverPosition, delta));
        }

        return results;
    }

    private static int IndexOfOccurrence(string text, string searchText, int occurrence)
    {
        var index = -1;
        for (var i = 0; i < occurrence; i++)
        {
            index = text.IndexOf(searchText, index + 1, StringComparison.Ordinal);
            if (index < 0)
                return -1;
        }

        return index;
    }

    private static HoverPositionTarget CreateHoverPositionTarget(
        SourceText sourceText,
        string text,
        string label,
        string searchText,
        string expectedText)
    {
        var targetOffset = text.IndexOf(searchText, StringComparison.Ordinal);
        targetOffset.ShouldBeGreaterThanOrEqualTo(0);

        var position = PositionHelper.ToRange(sourceText, new TextSpan(targetOffset, 0)).Start;
        return new HoverPositionTarget(label, position.Line, position.Character, expectedText);
    }

    private async Task<(DocumentStore store, WorkspaceManager manager, DocumentUri uri)> CreateWorkspaceAsync(
        string text, string? additionalDocumentName = null)
    {
        Directory.CreateDirectory(_tempRoot);

        var projectPath = Path.Combine(_tempRoot, "App.rvnproj");
        File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, text);
        if (additionalDocumentName is not null)
            File.WriteAllText(Path.Combine(_tempRoot, "src", additionalDocumentName), "");

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
        var uri = DocumentUri.FromFileSystemPath(filePath);
        await store.UpsertDocumentAsync(uri, text);
        return (store, manager, uri);
    }

    private static Position GetPosition(SourceText sourceText, int offset)
        => PositionHelper.ToRange(sourceText, new TextSpan(offset, 0)).Start;

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Raven.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Raven.sln from test base directory.");
    }

    private sealed record HoverReplayTarget(
        string Label,
        string SearchText,
        int CharacterOffset,
        string ExpectedText,
        int Occurrence = 1);

    private sealed record HoverPositionTarget(
        string Label,
        int Line,
        int Character,
        string ExpectedText);

    private sealed record HoverReplayResult(
        string Label,
        Position Position,
        SemanticQueryInstrumentation.Snapshot SemanticDelta);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
