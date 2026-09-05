using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

using Xunit.Abstractions;

using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class HeadlessEditSimulationTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"raven-ls-headless-edit-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _output;

    public HeadlessEditSimulationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task BodyEdit_ReparsesBindsHoversAndReusesUnchangedCompilationTreesAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(_tempRoot, InitialMainText);
        var initial = await simulation.CaptureSnapshotAsync();

        var updatedText = ReplaceFirst(initial.SourceText, "baseValue * 2", "baseValue * 3");
        var result = await simulation.ApplyEditAndProbeAsync(
            updatedText,
            new HeadlessHoverProbe("changed local", "answer", ExpectedText: "answer", Occurrence: 2),
            new HeadlessHoverProbe("stable sibling", "item", ExpectedText: "item", Occurrence: 2));

        result.SyntaxRootMatchesText.ShouldBeTrue();
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        result.SemanticModelMaterialized.ShouldBeTrue();
        result.EditedSyntaxTreeChanged.ShouldBeTrue();
        result.UnchangedSyntaxTreeReused.ShouldBeTrue();
        result.Probes.ShouldAllBe(probe => probe.HasHover);
        result.Probes.ShouldAllBe(probe => probe.ElapsedMs < 5_000);
    }

    [Fact]
    public async Task SignatureEdit_ReparsesBindsAndReturnsCurrentHoverSymbolsAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(_tempRoot, InitialMainText);
        var initial = await simulation.CaptureSnapshotAsync();

        var updatedText = ReplaceFirst(initial.SourceText, "func Compute(value: int) -> int", "func Compute(value: int, extra: int) -> int");
        updatedText = ReplaceFirst(updatedText, "value + 1", "value + extra");
        var result = await simulation.ApplyEditAndProbeAsync(
            updatedText,
            new HeadlessHoverProbe("new parameter", "extra", ExpectedText: "extra"),
            new HeadlessHoverProbe("stable sibling", "item", ExpectedText: "item", Occurrence: 2));

        result.SyntaxRootMatchesText.ShouldBeTrue();
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        result.SemanticModelMaterialized.ShouldBeTrue();
        result.EditedSyntaxTreeChanged.ShouldBeTrue();
        result.UnchangedSyntaxTreeReused.ShouldBeTrue();
        result.Probes.ShouldAllBe(probe => probe.HasHover);
        result.Probes.ShouldAllBe(probe => probe.ElapsedMs < 5_000);
    }

    [Fact]
    public async Task AddSecondSourceFile_CrossFileHoverResolvesThroughCurrentSnapshotAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(
            _tempRoot,
            """
            func Main() {
                Test()
            }
            """);

        var initialDiagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        initialDiagnostics.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("'Test' is not in scope", StringComparison.Ordinal));

        await simulation.UpsertAdditionalDocumentAsync(
            "test.rvn",
            """
            func Test() {
            }
            """);

        var diagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        diagnostics.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);

        var firstHover = await simulation.RunHoverProbeAsync(new HeadlessHoverProbe("cross-file function", "Test", "func Test"));
        var secondHover = await simulation.RunHoverProbeAsync(new HeadlessHoverProbe("cross-file function repeat", "Test", "func Test"));

        firstHover.HasHover.ShouldBeTrue();
        secondHover.HasHover.ShouldBeTrue();
        secondHover.ElapsedMs.ShouldBeLessThan(250);
        secondHover.SemanticDelta.SymbolInfoBinderFallbacks.ShouldBe(0);
        secondHover.SemanticDelta.SymbolInfoOperationFallbacks.ShouldBe(0);
        secondHover.SemanticDelta.BoundNodeBindFallbacks.ShouldBe(0);
    }

    [Fact]
    public async Task CrossFileReturnTypeEdit_ClearsNullableUseDeclarationDiagnosticsAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(
            _tempRoot,
            """
            import Utilities.*

            func Main() -> unit {
                use test = Test2()
                test.Dispose()
            }
            """);

        await simulation.UpsertAdditionalDocumentAsync(
            "test.rvn",
            """
            namespace Utilities

            func Test2() -> IDisposable? {
                return null
            }
            """);

        var nullableDiagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        nullableDiagnostics.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV1503", StringComparison.Ordinal) &&
            diagnostic.Range.Start.Line == 3 &&
            diagnostic.Range.Start.Character == 8).ShouldBeTrue();
        nullableDiagnostics.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV0402", StringComparison.Ordinal)).ShouldBeTrue();

        await simulation.UpsertAdditionalDocumentAsync(
            "test.rvn",
            """
            namespace Utilities

            func Test2() -> IDisposable {
                return default!
            }
            """);

        var nonNullableDiagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        nonNullableDiagnostics.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code?.String, "RAV1503", StringComparison.Ordinal) ||
            string.Equals(diagnostic.Code?.String, "RAV0402", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task DeveloperEditSequence_RecoversAndMatchesOneShotCompilationAtEverySnapshotAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(_tempRoot, InitialMainText);

        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var whitespaceEdit = InitialMainText.Replace("value + 1", "value  + 1", StringComparison.Ordinal);
        await ApplyValidEditAsync(whitespaceEdit, "whitespace edit");
        await ApplyValidEditAsync(InitialMainText, "undo whitespace edit");

        var expressionEdit = InitialMainText.Replace("baseValue * 2", "baseValue * 3", StringComparison.Ordinal);
        await ApplyValidEditAsync(expressionEdit, "expression edit");
        await ApplyValidEditAsync(InitialMainText, "undo expression edit");

        const string membersMoved =
            """
            class Runner {
                func Stable(item: int) -> int {
                    return item
                }

                func Compute(value: int) -> int {
                    let baseValue = value + 1
                    let answer = baseValue * 2
                    return answer
                }
            }
            """;
        await ApplyValidEditAsync(membersMoved, "move members");
        await ApplyValidEditAsync(InitialMainText, "undo member move");

        const string memberRemoved =
            """
            class Runner {
                func Stable(item: int) -> int {
                    return item
                }
            }
            """;
        var removed = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(memberRemoved),
            new HeadlessHoverProbe("remaining member after removal", "item", ExpectedText: "item", Occurrence: 2));
        removed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        await ApplyValidEditAsync(InitialMainText, "undo member removal");

        var invalidText = InitialMainText.Replace("baseValue * 2", "]baseValue * 2", StringComparison.Ordinal);
        var invalid = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(invalidText),
            new HeadlessHoverProbe("stable sibling during syntax error", "item", ExpectedText: "item", Occurrence: 2));
        invalid.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        invalid.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        await ApplyValidEditAsync(InitialMainText, "undo unexpected character");

        await simulation.UpsertAdditionalDocumentAsync("empty.rvn", string.Empty);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        var finalHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("semantic response after empty file", "answer", ExpectedText: "answer", Occurrence: 2));
        finalHover.HasHover.ShouldBeTrue();

        const string helperText =
            """
            func Increment(value: int) -> int {
                return value + 1
            }
            """;
        await simulation.UpsertAdditionalDocumentAsync("helper.rvn", helperText);
        var crossFileText = InitialMainText.Replace("value + 1", "Increment(value)", StringComparison.Ordinal);
        var crossFile = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(crossFileText),
            new HeadlessHoverProbe("cross-file symbol", "Increment", ExpectedText: "func Increment"));
        crossFile.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        await simulation.RemoveAdditionalDocumentAsync("helper.rvn");
        var missingHelperDiagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        missingHelperDiagnostics.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error &&
            diagnostic.Message.Contains("'Increment' is not in scope", StringComparison.Ordinal));
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        await simulation.UpsertAdditionalDocumentAsync("helper.rvn", helperText);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        var recoveredCrossFileHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("cross-file symbol after undo", "Increment", ExpectedText: "func Increment"));
        recoveredCrossFileHover.HasHover.ShouldBeTrue();

        await simulation.RenameAdditionalDocumentAsync("helper.rvn", "utilities.rvn");
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        var renamedFileHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("cross-file symbol after file rename", "Increment", ExpectedText: "func Increment"));
        renamedFileHover.HasHover.ShouldBeTrue();

        await simulation.RenameAdditionalDocumentAsync("utilities.rvn", "helper.rvn");
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
        var renameUndoHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("cross-file symbol after file rename undo", "Increment", ExpectedText: "func Increment"));
        renameUndoHover.HasHover.ShouldBeTrue();

        async Task ApplyValidEditAsync(string text, string label)
        {
            var result = await simulation.ApplyEditAndProbeAsync(
                SourceText.From(text),
                new HeadlessHoverProbe(label, "answer", ExpectedText: "answer", Occurrence: 2));

            result.SyntaxRootMatchesText.ShouldBeTrue();
            result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
            result.Probes.ShouldAllBe(probe => probe.HasHover);
            await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync(label);
        }
    }

    [Fact]
    public async Task SingleFileTypingSequence_TracksIncompleteCodeAndRecoversAfterUndoAsync()
    {
        await using var simulation = HeadlessEditSimulation.Create(
            _tempRoot,
            string.Empty,
            includeStableDocument: false);

        string[] typingStages =
        [
            "f",
            "func Main(",
            "func Main() {",
            "func Main() {\n    let value =",
            "func Main() {\n    let value = 1",
            "func Main() {\n    let value = 1\n    value",
            "func Main() {\n    let value = 1\n    value\n}\n"
        ];

        foreach (var stage in typingStages)
        {
            var result = await simulation.ApplyEditAndProbeAsync(SourceText.From(stage));
            result.SyntaxRootMatchesText.ShouldBeTrue();
            result.SemanticModelMaterialized.ShouldBeTrue();
            await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync(stage);
        }

        var completedDiagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        completedDiagnostics.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == LspDiagnosticSeverity.Error);
        var completedHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("typed local", "value", ExpectedText: "value", Occurrence: 2));
        completedHover.HasHover.ShouldBeTrue();

        var incompleteExpression = typingStages[^1].Replace("let value = 1", "let value = ]", StringComparison.Ordinal);
        var incomplete = await simulation.ApplyEditAndProbeAsync(SourceText.From(incompleteExpression));
        incomplete.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var recovered = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(typingStages[^1]),
            new HeadlessHoverProbe("typed local after undo", "value", ExpectedText: "value", Occurrence: 2));
        recovered.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        recovered.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
    }

    [Fact]
    public async Task StructuralWrapperEdit_ReparentsTopLevelLocalsAndRecoversAcrossIncompleteSnapshotsAsync()
    {
        const string topLevelSource =
            """
            let seed = 1
            let answer = seed + 1
            answer
            """;
        await using var simulation = HeadlessEditSimulation.Create(
            _tempRoot,
            topLevelSource,
            includeStableDocument: false);

        var initialHover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("top-level local", "answer", ExpectedText: "answer", Occurrence: 2));
        initialHover.HasHover.ShouldBeTrue();
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var incompleteWrapper = $"func Main() {{\n{topLevelSource}";
        var incomplete = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(incompleteWrapper),
            new HeadlessHoverProbe("reparented local before close brace", "answer", ExpectedText: "answer", Occurrence: 2));
        incomplete.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var completeWrapper = $"{incompleteWrapper}}}\n";
        var wrapped = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(completeWrapper),
            new HeadlessHoverProbe("reparented local", "answer", ExpectedText: "answer", Occurrence: 2));
        wrapped.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        wrapped.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var bodyEdited = completeWrapper.Replace("seed + 1", "seed + 2", StringComparison.Ordinal);
        var edited = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(bodyEdited),
            new HeadlessHoverProbe("local after wrapped body edit", "answer", ExpectedText: "answer", Occurrence: 2));
        edited.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        edited.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();

        var unwrapped = await simulation.ApplyEditAndProbeAsync(
            SourceText.From(topLevelSource),
            new HeadlessHoverProbe("local after wrapper undo", "answer", ExpectedText: "answer", Occurrence: 2));
        unwrapped.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        unwrapped.Probes.ShouldAllBe(probe => probe.HasHover);
        await simulation.AssertDocumentCompilerDiagnosticsMatchOneShotAsync();
    }

    [Fact]
    public async Task ColdMalformedMethod_DoesNotHideHoverInUnchangedSiblingMethodAsync()
    {
        var invalidText = InitialMainText.Replace("baseValue * 2", "]baseValue * 2", StringComparison.Ordinal);
        await using var simulation = HeadlessEditSimulation.Create(_tempRoot, invalidText);

        var diagnostics = await simulation.GetDocumentCompilerDiagnosticsAsync();
        diagnostics.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error);
        var hover = await simulation.RunHoverProbeAsync(
            new HeadlessHoverProbe("stable sibling during cold syntax error", "item", ExpectedText: "item", Occurrence: 2));

        hover.HasHover.ShouldBeTrue();
    }

    public static TheoryData<string> IncrementalProjectMatrix => new()
    {
        "hello-world",
        "conditional-compilation",
        "top-level-members",
        "repository-result-patterns"
    };

    [Theory]
    [MemberData(nameof(IncrementalProjectMatrix))]
    public async Task SampleProjectMatrix_EditorEditsAndUndoRecoverAsync(string sampleName)
    {
        var sampleRoot = Path.Combine(FindRepoRoot(), "samples", "projects", sampleName);
        var sourceRoot = Path.Combine(sampleRoot, "src");
        var sourceFiles = Directory.GetFiles(sourceRoot, "*.rvn", SearchOption.AllDirectories)
            .OrderByDescending(static path => string.Equals(Path.GetFileName(path), "Main.rvn", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        sourceFiles.ShouldNotBeEmpty();

        var mainSource = await File.ReadAllTextAsync(sourceFiles[0]);
        var coldRootText = SyntaxTree.ParseText(mainSource).GetRoot().ToFullString();
        Assert.True(
            string.Equals(mainSource, coldRootText, StringComparison.Ordinal),
            $"{sampleName}: one-shot parser did not round-trip the baseline. " +
            $"Expected length {mainSource.Length}, actual length {coldRootText.Length}. " +
            $"Actual source:\n{coldRootText}");
        await using var simulation = HeadlessEditSimulation.Create(
            _tempRoot,
            mainSource,
            includeStableDocument: false);

        foreach (var sourceFile in sourceFiles.Skip(1))
        {
            await simulation.UpsertAdditionalDocumentAsync(
                Path.GetFileName(sourceFile),
                await File.ReadAllTextAsync(sourceFile));
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshots = 0;
        await AssertSnapshotAsync(mainSource, "baseline");
        var baselineDiagnostics = await simulation.GetDocumentCompilerDiagnosticSignaturesAsync();
        var baselineSemantics = await simulation.GetPublicSemanticSignaturesAsync();
        await AssertSnapshotAsync(mainSource + Environment.NewLine, "append whitespace");
        await AssertSnapshotAsync(mainSource, "undo whitespace");
        await AssertRecoveredAsync("undo whitespace");

        foreach (var insertion in new[] { "@", "]" })
        {
            await AssertSnapshotAsync(mainSource + insertion, $"insert {insertion} at end of file");
            await AssertSnapshotAsync(mainSource, $"undo {insertion}");
            await AssertRecoveredAsync($"undo {insertion}");
        }

        await simulation.UpsertAdditionalDocumentAsync("empty.rvn", string.Empty);
        await AssertRecoveredAsync("add empty file");
        snapshots++;
        stopwatch.Stop();

        _output.WriteLine(
            $"{sampleName}: files={sourceFiles.Length}, chars={sourceFiles.Sum(static path => new FileInfo(path).Length)}, " +
            $"snapshots={snapshots}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}");

        async Task AssertSnapshotAsync(string text, string label)
        {
            var coldRoot = SyntaxTree.ParseText(text).GetRoot();
            coldRoot.ToFullString().ShouldBe(text, $"{sampleName}: one-shot parse for {label}");

            var result = await simulation.ApplyEditAndProbeAsync(SourceText.From(text));
            result.SyntaxRootMatchesText.ShouldBeTrue(
                $"{sampleName}: {label}; expected {text.Length} source characters");
            _ = await simulation.GetDocumentCompilerDiagnosticSignaturesAsync();
            snapshots++;
        }

        async Task AssertRecoveredAsync(string label)
        {
            (await simulation.GetDocumentCompilerDiagnosticSignaturesAsync())
                .ShouldBe(baselineDiagnostics, $"{sampleName}: diagnostics after {label}");
            (await simulation.GetPublicSemanticSignaturesAsync())
                .ShouldBe(baselineSemantics, $"{sampleName}: semantics after {label}");
        }
    }

    private const string InitialMainText =
        """
        class Runner {
            func Compute(value: int) -> int {
                let baseValue = value + 1
                let answer = baseValue * 2
                return answer
            }

            func Stable(item: int) -> int {
                return item
            }
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static SourceText ReplaceFirst(SourceText sourceText, string oldText, string newText)
    {
        var text = sourceText.ToString();
        var start = text.IndexOf(oldText, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        return sourceText.Replace(new TextSpan(start, oldText.Length), newText);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "samples")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "test")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Raven repository root.");
    }

    private sealed class HeadlessEditSimulation : IAsyncDisposable
    {
        private const string StableText =
            """
            class Sibling {
                func Identity(value: int) -> int {
                    return value
                }
            }
            """;

        private readonly string _mainPath;
        private readonly string? _stablePath;
        private readonly DocumentUri _mainUri;
        private readonly WorkspaceManager _manager;
        private readonly DocumentStore _store;
        private readonly HoverHandler _hoverHandler;
        private readonly RecordingLogger<DocumentStore> _documentStoreLogger = new();

        public IReadOnlyList<string> LogMessages => _documentStoreLogger.Messages;

        private HeadlessEditSimulation(
            string root,
            string mainText,
            bool includeStableDocument)
        {
            Directory.CreateDirectory(root);

            var projectPath = Path.Combine(root, "App.rvnproj");
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

            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);
            _mainPath = Path.Combine(sourceRoot, "main.rvn");
            File.WriteAllText(_mainPath, mainText);

            if (includeStableDocument)
            {
                _stablePath = Path.Combine(sourceRoot, "stable.rvn");
                File.WriteAllText(_stablePath, StableText);
            }

            var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
            _manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
            _manager.Initialize(new InitializeParams
            {
                WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
                {
                    Name = "headless-edit",
                    Uri = DocumentUri.FromFileSystemPath(root)
                })
            });

            _store = new DocumentStore(_manager, _documentStoreLogger);
            _hoverHandler = new HoverHandler(_store, NullLogger<HoverHandler>.Instance);
            _mainUri = DocumentUri.FromFileSystemPath(_mainPath);
            _store.UpsertDocumentAsync(_mainUri, mainText);

            if (_stablePath is not null)
                _store.UpsertDocumentAsync(DocumentUri.FromFileSystemPath(_stablePath), StableText);
        }

        public static HeadlessEditSimulation Create(
            string root,
            string mainText,
            bool includeStableDocument = true)
            => new(root, mainText, includeStableDocument);

        public async Task<HeadlessEditSnapshot> CaptureSnapshotAsync()
        {
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            context.ShouldNotBeNull();

            var semanticModel = await _store.GetSemanticModelAsync(_mainUri, CancellationToken.None);
            semanticModel.ShouldNotBeNull();

            var compilation = context.Value.Compilation;
            return new HeadlessEditSnapshot(
                context.Value.SourceText,
                context.Value.SyntaxTree,
                _stablePath is null ? null : GetCompilationTree(compilation, _stablePath));
        }

        public Task<DocumentStore.DiagnosticsComputationResult> GetDocumentCompilerDiagnosticsAsync()
            => _store.TryGetDiagnosticsAsync(
                _mainUri,
                DocumentStore.DiagnosticLane.DocumentCompiler,
                shouldSkipWork: null,
                CancellationToken.None);

        public async Task<string[]> GetDocumentCompilerDiagnosticSignaturesAsync()
        {
            var result = await GetDocumentCompilerDiagnosticsAsync();
            result.WasSkipped.ShouldBeFalse();
            return result.Diagnostics
                .Select(static diagnostic =>
                    $"{diagnostic.Code?.String}@{diagnostic.Range.Start}-{diagnostic.Range.End}:{diagnostic.Message}")
                .OrderBy(static signature => signature, StringComparer.Ordinal)
                .ToArray();
        }

        public async Task<string[]> GetPublicSemanticSignaturesAsync()
        {
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            context.ShouldNotBeNull();
            var model = await _store.GetSemanticModelAsync(_mainUri, CancellationToken.None);
            model.ShouldNotBeNull();
            return CapturePublicSemanticSignatures(model!, context.Value.SyntaxTree);
        }

        public async Task AssertDocumentCompilerDiagnosticsMatchOneShotAsync(string? snapshotContext = null)
        {
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            context.ShouldNotBeNull();

            var incrementalResult = await GetDocumentCompilerDiagnosticsAsync();
            incrementalResult.WasSkipped.ShouldBeFalse();

            var incrementalCompilation = context.Value.Compilation;
            var coldTrees = incrementalCompilation.SyntaxTrees
                .Select(tree => SyntaxTree.ParseText(tree.GetText(), tree.Options, path: tree.FilePath))
                .ToArray();
            var coldCompilation = Compilation.Create(
                $"{incrementalCompilation.AssemblyName}.cold",
                coldTrees,
                incrementalCompilation.References.ToArray(),
                incrementalCompilation.MacroReferences.ToArray(),
                incrementalCompilation.Options);
            if (incrementalCompilation.MacroSyntaxTrees.Length > 0)
            {
                var coldMacroTrees = incrementalCompilation.MacroSyntaxTrees
                    .Select(tree => SyntaxTree.ParseText(tree.GetText(), tree.Options, path: tree.FilePath))
                    .ToArray();
                coldCompilation = coldCompilation.AddMacroSyntaxTrees(coldMacroTrees);
            }
            var coldMainTree = GetCompilationTree(coldCompilation, _mainPath);
            var coldDiagnostics = coldCompilation.GetDiagnostics()
                .Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, coldMainTree));

            var incrementalSignatures = incrementalResult.Diagnostics
                .Select(static diagnostic =>
                    $"{diagnostic.Code?.String}@{diagnostic.Range.Start}-{diagnostic.Range.End}:{diagnostic.Message}")
                .OrderBy(static signature => signature, StringComparer.Ordinal)
                .ToArray();
            var coldSignatures = coldDiagnostics
                .Select(diagnostic =>
                {
                    var range = PositionHelper.ToRange(coldMainTree.GetText(), diagnostic.Location.SourceSpan);
                    return $"{diagnostic.Id}@{range.Start}-{range.End}:{diagnostic.GetMessage()}";
                })
                .OrderBy(static signature => signature, StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                incrementalSignatures.SequenceEqual(coldSignatures, StringComparer.Ordinal),
                $"Snapshot: {snapshotContext ?? "<unspecified>"}\n" +
                $"Incremental: {string.Join(" | ", incrementalSignatures)}\n" +
                $"Cold: {string.Join(" | ", coldSignatures)}");

            var incrementalModel = incrementalCompilation.GetSemanticModel(context.Value.SyntaxTree);
            var coldModel = coldCompilation.GetSemanticModel(coldMainTree);
            CapturePublicSemanticSignatures(incrementalModel, context.Value.SyntaxTree)
                .ShouldBe(CapturePublicSemanticSignatures(coldModel, coldMainTree));
        }

        private static string[] CapturePublicSemanticSignatures(SemanticModel model, SyntaxTree tree)
        {
            var root = tree.GetRoot();
            var signatures = new List<string>();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                signatures.Add($"method@{method.Span}:{GetSymbolShape(model.GetDeclaredSymbol(method))}");

            foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
                signatures.Add($"parameter@{parameter.Span}:{GetSymbolShape(model.GetDeclaredSymbol(parameter))}");

            foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                signatures.Add($"declarator@{declarator.Span}:{GetSymbolShape(model.GetDeclaredSymbol(declarator))}");

            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = model.GetSymbolInfo(identifier);
                var typeInfo = model.GetTypeInfo(identifier);
                signatures.Add(
                    $"identifier@{identifier.Span}:{identifier.Identifier.ValueText}" +
                    $":symbol={GetSymbolShape(symbolInfo.Symbol)}" +
                    $":reason={symbolInfo.CandidateReason}" +
                    $":type={GetTypeShape(typeInfo.Type)}" +
                    $":converted={GetTypeShape(typeInfo.ConvertedType)}");
            }

            return signatures.ToArray();

            static string GetSymbolShape(ISymbol? symbol)
            {
                if (symbol is null)
                    return "<null>";

                var type = symbol switch
                {
                    ILocalSymbol local => local.Type,
                    IParameterSymbol parameter => parameter.Type,
                    IMethodSymbol method => method.ReturnType,
                    _ => null
                };
                return $"{symbol.Kind}:{symbol.Name}:{GetTypeShape(type)}";
            }

            static string GetTypeShape(ITypeSymbol? type)
                => type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "<null>";
        }

        public async Task UpsertAdditionalDocumentAsync(string fileName, string text)
        {
            var sourceRoot = Path.GetDirectoryName(_mainPath)!;
            var path = Path.Combine(sourceRoot, fileName);
            var isNewDocument = !File.Exists(path);
            File.WriteAllText(path, text);
            var uri = DocumentUri.FromFileSystemPath(path);

            if (isNewDocument)
            {
                await _manager.ReloadForWatchedFilesAsync([
                    new FileEvent
                    {
                        Uri = uri,
                        Type = FileChangeType.Created
                    }
                ]);
            }

            await _store.UpsertDocumentAsync(uri, text);
        }

        public async Task RemoveAdditionalDocumentAsync(string fileName)
        {
            var path = Path.Combine(Path.GetDirectoryName(_mainPath)!, fileName);
            var uri = DocumentUri.FromFileSystemPath(path);
            File.Delete(path);

            await _manager.ReloadForWatchedFilesAsync([
                new FileEvent
                {
                    Uri = uri,
                    Type = FileChangeType.Deleted
                }
            ]);
            _store.RemoveDocument(uri);
        }

        public async Task RenameAdditionalDocumentAsync(string oldFileName, string newFileName)
        {
            var sourceRoot = Path.GetDirectoryName(_mainPath)!;
            var oldPath = Path.Combine(sourceRoot, oldFileName);
            var newPath = Path.Combine(sourceRoot, newFileName);
            var oldUri = DocumentUri.FromFileSystemPath(oldPath);
            var newUri = DocumentUri.FromFileSystemPath(newPath);
            File.Move(oldPath, newPath);

            await _manager.ReloadForWatchedFilesAsync([
                new FileEvent
                {
                    Uri = oldUri,
                    Type = FileChangeType.Deleted
                },
                new FileEvent
                {
                    Uri = newUri,
                    Type = FileChangeType.Created
                }
            ]);
            _store.RemoveDocument(oldUri);
        }

        public async Task<HeadlessEditResult> ApplyEditAndProbeAsync(
            SourceText updatedText,
            params HeadlessHoverProbe[] probes)
        {
            var before = await CaptureSnapshotAsync();
            await _store.UpsertDocumentAsync(_mainUri, updatedText);

            var contextStopwatch = Stopwatch.StartNew();
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            contextStopwatch.Stop();
            context.ShouldNotBeNull();

            var syntaxRootMatchesText = string.Equals(
                context.Value.SyntaxTree.GetRoot().ToFullString(),
                updatedText.ToString(),
                StringComparison.Ordinal);

            var semanticStopwatch = Stopwatch.StartNew();
            var semanticModel = await _store.GetSemanticModelAsync(_mainUri, CancellationToken.None);
            semanticStopwatch.Stop();

            var diagnosticsStopwatch = Stopwatch.StartNew();
            var diagnostics = await _store.GetDiagnosticsAsync(_mainUri, CancellationToken.None);
            diagnosticsStopwatch.Stop();

            var updatedCompilation = context.Value.Compilation;
            var updatedStableTree = _stablePath is null ? null : GetCompilationTree(updatedCompilation, _stablePath);
            var hoverResults = new List<HeadlessHoverProbeResult>(probes.Length);

            foreach (var probe in probes)
                hoverResults.Add(await RunHoverProbeAsync(context.Value.SourceText, probe));

            return new HeadlessEditResult(
                syntaxRootMatchesText,
                semanticModel is not null,
                diagnostics,
                !ReferenceEquals(before.EditedSyntaxTree, context.Value.SyntaxTree),
                ReferenceEquals(before.UnchangedSyntaxTree, updatedStableTree),
                contextStopwatch.Elapsed.TotalMilliseconds,
                semanticStopwatch.Elapsed.TotalMilliseconds,
                diagnosticsStopwatch.Elapsed.TotalMilliseconds,
                hoverResults);
        }

        public async Task<HeadlessHoverProbeResult> RunHoverProbeAsync(HeadlessHoverProbe probe)
        {
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            context.ShouldNotBeNull();
            return await RunHoverProbeAsync(context.Value.SourceText, probe);
        }

        private async Task<HeadlessHoverProbeResult> RunHoverProbeAsync(SourceText sourceText, HeadlessHoverProbe probe)
        {
            var text = sourceText.ToString();
            var offset = IndexOfOccurrence(text, probe.SearchText, probe.Occurrence);
            offset.ShouldBeGreaterThanOrEqualTo(0);

            var position = PositionHelper.ToRange(
                sourceText,
                new TextSpan(offset + Math.Min(probe.CharacterOffset, probe.SearchText.Length), 0)).Start;
            var context = await _store.GetAnalysisContextAsync(_mainUri, CancellationToken.None);
            context.ShouldNotBeNull();

            var before = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var stopwatch = Stopwatch.StartNew();
            var hover = await _hoverHandler.Handle(new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(_mainUri),
                Position = position
            }, CancellationToken.None);
            stopwatch.Stop();
            var after = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var delta = SemanticQueryInstrumentation.Subtract(after, before);

            var hoverText = hover?.Contents.MarkupContent?.Value ?? string.Empty;
            hover.ShouldNotBeNull();
            hoverText.ShouldContain(probe.ExpectedText);

            return new HeadlessHoverProbeResult(
                probe.Label,
                hover is not null,
                stopwatch.Elapsed.TotalMilliseconds,
                delta);
        }

        private static SyntaxTree GetCompilationTree(Compilation compilation, string path)
            => compilation.SyntaxTrees.Single(tree =>
                string.Equals(Path.GetFullPath(tree.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

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

        public async ValueTask DisposeAsync()
        {
            await _manager.FlushPendingMacroConsumerRefreshesAsync();
        }
    }

    private sealed record HeadlessEditSnapshot(
        SourceText SourceText,
        SyntaxTree EditedSyntaxTree,
        SyntaxTree? UnchangedSyntaxTree);

    private sealed record HeadlessEditResult(
        bool SyntaxRootMatchesText,
        bool SemanticModelMaterialized,
        IReadOnlyList<LspDiagnostic> Diagnostics,
        bool EditedSyntaxTreeChanged,
        bool UnchangedSyntaxTreeReused,
        double AnalysisContextMs,
        double SemanticModelMs,
        double DiagnosticsMs,
        IReadOnlyList<HeadlessHoverProbeResult> Probes);

    private sealed record HeadlessHoverProbe(
        string Label,
        string SearchText,
        string ExpectedText,
        int Occurrence = 1,
        int CharacterOffset = 1);

    private sealed record HeadlessHoverProbeResult(
        string Label,
        bool HasHover,
        double ElapsedMs,
        SemanticQueryInstrumentation.Snapshot SemanticDelta);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }
}
