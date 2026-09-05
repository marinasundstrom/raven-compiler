using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;

using Microsoft.Extensions.Logging;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;
using CodeDiagnosticSeverity = Raven.CodeAnalysis.DiagnosticSeverity;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using SyntaxTree = Raven.CodeAnalysis.Syntax.SyntaxTree;

namespace Raven.LanguageServer;

internal sealed class DocumentStore
{
    private const double SlowCompilerGateThresholdMs = 100;
    private const double SlowAnalysisContextThresholdMs = 100;
    private const double SlowSemanticModelMaterializationThresholdMs = 100;
    private const double SlowDiagnosticsThresholdMs = 150;
    private const double SlowWarmAnalysisThresholdMs = 250;

    internal readonly record struct DocumentAnalysisContext(
        Document Document,
        Compilation Compilation,
        SyntaxTree SyntaxTree,
        Raven.CodeAnalysis.Text.SourceText SourceText);

    internal readonly record struct DocumentSyntaxContext(
        Document Document,
        SyntaxTree SyntaxTree,
        Raven.CodeAnalysis.Text.SourceText SourceText);

    private readonly record struct PendingDocumentChange(
        Raven.CodeAnalysis.Text.SourceText Text,
        bool DeferMacroConsumerRefresh);

    internal readonly record struct DiagnosticsComputationResult(
        IReadOnlyList<LspDiagnostic> Diagnostics,
        bool WasSkipped,
        DiagnosticSnapshotKey? SnapshotKey = null,
        Raven.CodeAnalysis.Text.SourceText? SourceText = null);

    internal sealed class DocumentSemanticAccess : IDisposable
    {
        private readonly SemanticModel? _semanticModel;
        private IDisposable? _ambientLease;
        private IDisposable? _lease;

        public DocumentSemanticAccess(SemanticModel? semanticModel, IDisposable lease)
        {
            _semanticModel = semanticModel;
            _lease = lease;
        }

        public SemanticModel? SemanticModel
        {
            get
            {
                if (_semanticModel is not null && _ambientLease is null)
                    _ambientLease = _semanticModel.EnterAmbientSemanticAccess();

                return _semanticModel;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _ambientLease, null)?.Dispose();
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    internal enum DiagnosticLane
    {
        ProjectWithAnalyzers,
        Syntax,
        DocumentCompiler,
        ProjectCompiler,
        DocumentWithAnalyzers
    }

    private static readonly HashSet<string> UnnecessaryDiagnosticIds = new(StringComparer.OrdinalIgnoreCase)
    {
        CompilerDiagnostics.UnreachableCodeDetected.Id,
        CompilerDiagnostics.ImportDirectiveRedundantWithGlobalImport.Id,
        Raven.CodeAnalysis.Diagnostics.UnusedImportDirectiveAnalyzer.DiagnosticId,
        Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.DiagnosticId,
        Raven.CodeAnalysis.Diagnostics.UnusedVariableAnalyzer.UnusedParameterDiagnosticId
    };

    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger<DocumentStore> _logger;
    private readonly SemaphoreSlim _compilerAccessGate = new(1, 1);
    private readonly object _backgroundSemanticWorkCancellationGate = new();
    private readonly ConcurrentDictionary<DocumentDiagnosticsCacheKey, ImmutableArray<LspDiagnostic>> _documentCompilerDiagnosticsCache = new();
    private readonly ConcurrentDictionary<DocumentDiagnosticsCacheKey, ImmutableArray<LspDiagnostic>> _documentWithAnalyzersDiagnosticsCache = new();
    private readonly ConcurrentDictionary<DocumentUri, CancellationTokenSource> _pendingPostEditSemanticWarmups = new();
    private readonly ConcurrentDictionary<DocumentUri, PendingDocumentChange> _pendingDocumentChanges = new();
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _pendingDocumentChangeGates = new();
    private readonly ConcurrentDictionary<DocumentUri, long> _recentDocumentChangeTicks = new();
    private CancellationTokenSource _backgroundSemanticWorkPreemption = new();

    public DocumentStore(WorkspaceManager workspaceManager, ILogger<DocumentStore> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public Task<Document> UpsertDocumentAsync(DocumentUri uri, string text)
        => UpsertDocumentAsync(uri, Raven.CodeAnalysis.Text.SourceText.From(text), deferMacroConsumerRefresh: false);

    internal Task<Document> UpsertDocumentAsync(DocumentUri uri, string text, bool deferMacroConsumerRefresh = false)
        => UpsertDocumentAsync(uri, Raven.CodeAnalysis.Text.SourceText.From(text), deferMacroConsumerRefresh);

    internal async Task<Document> UpsertDocumentAsync(DocumentUri uri, Raven.CodeAnalysis.Text.SourceText text, bool deferMacroConsumerRefresh = false)
        => (await UpsertDocumentWithResultAsync(uri, text, deferMacroConsumerRefresh)).Document;

    internal async Task<WorkspaceManager.DocumentUpsertResult> UpsertDocumentWithResultAsync(DocumentUri uri, Raven.CodeAnalysis.Text.SourceText text, bool deferMacroConsumerRefresh = false)
        => ApplyUpsertResult(uri, await _workspaceManager.UpsertDocumentWithResultAsync(uri, text, deferMacroConsumerRefresh), deferMacroConsumerRefresh);

    internal Document UpsertDocument(DocumentUri uri, Raven.CodeAnalysis.Text.SourceText text, bool deferMacroConsumerRefresh = false)
        => UpsertDocumentWithResult(uri, text, deferMacroConsumerRefresh).Document;

    internal Document UpsertDocument(DocumentUri uri, string text, bool deferMacroConsumerRefresh = false)
        => UpsertDocument(uri, Raven.CodeAnalysis.Text.SourceText.From(text), deferMacroConsumerRefresh);

    internal WorkspaceManager.DocumentUpsertResult UpsertDocumentWithResult(DocumentUri uri, Raven.CodeAnalysis.Text.SourceText text, bool deferMacroConsumerRefresh = false)
        => ApplyUpsertResult(uri, _workspaceManager.UpsertDocumentWithResult(uri, text, deferMacroConsumerRefresh), deferMacroConsumerRefresh);

    internal void QueuePendingDocumentChange(
        DocumentUri uri,
        Raven.CodeAnalysis.Text.SourceText text,
        bool deferMacroConsumerRefresh = false)
    {
        _pendingDocumentChanges[uri] = new PendingDocumentChange(text, deferMacroConsumerRefresh);
        _recentDocumentChangeTicks[uri] = Stopwatch.GetTimestamp();
        RemoveCachedDocumentDiagnostics(uri);
        CancelPostEditSemanticWarmup(uri);
    }

    internal bool TryGetPendingDocumentText(
        DocumentUri uri,
        [NotNullWhen(true)] out Raven.CodeAnalysis.Text.SourceText? text)
    {
        if (_pendingDocumentChanges.TryGetValue(uri, out var pending))
        {
            text = pending.Text;
            return true;
        }

        text = null;
        return false;
    }

    internal async Task<WorkspaceManager.DocumentUpsertResult?> FlushPendingDocumentChangeAsync(
        DocumentUri uri,
        CancellationToken cancellationToken)
    {
        if (!_pendingDocumentChanges.ContainsKey(uri))
            return null;

        var gate = _pendingDocumentChangeGates.GetOrAdd(uri, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pendingDocumentChanges.TryRemove(uri, out var pending))
                return null;

            return await UpsertDocumentWithResultAsync(
                uri,
                pending.Text,
                pending.DeferMacroConsumerRefresh).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal void DiscardPendingDocumentChange(DocumentUri uri)
    {
        _pendingDocumentChanges.TryRemove(uri, out _);
        if (_pendingDocumentChangeGates.TryRemove(uri, out var gate))
            gate.Dispose();
    }

    internal bool HasRecentDocumentChange(DocumentUri uri, TimeSpan window)
    {
        if (!_recentDocumentChangeTicks.TryGetValue(uri, out var timestamp))
            return false;

        var elapsed = Stopwatch.GetElapsedTime(timestamp);
        return elapsed <= window;
    }

    private WorkspaceManager.DocumentUpsertResult ApplyUpsertResult(
        DocumentUri uri,
        WorkspaceManager.DocumentUpsertResult result,
        bool deferMacroConsumerRefresh)
    {
        if (result.TextChanged)
        {
            RemoveCachedDocumentDiagnostics(uri);

            var syntaxTree = result.Document.SyntaxTree;
            if (syntaxTree is { IncrementalParseFallbackReason: not IncrementalParseFallbackReason.None })
            {
                _logger.LogDebug(
                    "Incremental parse fallback for {Uri}: reason={Reason} documentVersion={DocumentVersion} projectVersion={ProjectVersion} length={Length}.",
                    uri,
                    syntaxTree.IncrementalParseFallbackReason,
                    result.Document.Version,
                    result.Document.Project.Version,
                    syntaxTree.Length);
            }
        }

        if (result.ProjectChanged)
            RemoveStaleCachedDocumentDiagnostics(result.Document.Project.Id, result.Document.Project.Version);

        if (deferMacroConsumerRefresh && result.ProjectChanged)
            SchedulePostEditSemanticWarmup(uri);

        return result;
    }

    private void SchedulePostEditSemanticWarmup(DocumentUri uri)
    {
        var source = new CancellationTokenSource();
        var current = _pendingPostEditSemanticWarmups.AddOrUpdate(
            uri,
            source,
            (_, previous) =>
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                return source;
            });

        if (!ReferenceEquals(current, source))
        {
            source.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(75, source.Token).ConfigureAwait(false);
                await WarmPostEditSemanticsAsync(uri, source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-edit semantic warmup failed for {Uri}.", uri);
            }
            finally
            {
                if (_pendingPostEditSemanticWarmups.TryGetValue(uri, out var active) && ReferenceEquals(active, source))
                    _pendingPostEditSemanticWarmups.TryRemove(uri, out _);

                try
                {
                    source.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, CancellationToken.None);
    }

    private void CancelPostEditSemanticWarmup(DocumentUri uri)
    {
        if (!_pendingPostEditSemanticWarmups.TryRemove(uri, out var source))
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WarmPostEditSemanticsAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        double contextMs = 0;
        double semanticModelMs = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stageStopwatch = Stopwatch.StartNew();
            var context = await CreateDocumentAnalysisContextAsync(
                uri,
                position: null,
                cancellationToken).ConfigureAwait(false);
            contextMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (context is null)
                return;

            cancellationToken.ThrowIfCancellationRequested();

            stageStopwatch.Restart();
            var setupBefore = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            _ = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
            semanticModelMs = stageStopwatch.Elapsed.TotalMilliseconds;

            var setupAfter = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            var setupDelta = CompilerSetupInstrumentation.Subtract(setupAfter, setupBefore);

            if (stopwatch.Elapsed.TotalMilliseconds >= SlowWarmAnalysisThresholdMs)
            {
                _logger.LogInformation(
                    "Post-edit semantic warmup for {Uri}: total={TotalMs:F1}ms context={ContextMs:F1}ms semanticModel={SemanticModelMs:F1}ms setupDelta=[{SetupDelta}].",
                    uri,
                    stopwatch.Elapsed.TotalMilliseconds,
                    contextMs,
                    semanticModelMs,
                    CompilerSetupInstrumentation.FormatDelta(setupDelta));
            }
        }
        finally
        {
            stopwatch.Stop();
            LanguageServerPerformanceInstrumentation.RecordOperation(
                "postEditSemanticWarmup",
                uri,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                detail: $"{uri}",
                stages:
                [
                    new LanguageServerPerformanceInstrumentation.StageTiming("analysisContext", contextMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("semanticModel", semanticModelMs)
                ]);
        }
    }

    public bool TryGetDocument(DocumentUri uri, [NotNullWhen(true)] out Document? document)
        => _workspaceManager.TryGetDocument(uri, out document);

    public IReadOnlyList<DocumentUri> GetOpenDocumentUrisInSameProject(DocumentUri uri, bool excludeSelf = false)
        => _workspaceManager.GetOpenDocumentUrisInSameProject(uri, excludeSelf);

    public bool TryGetDocumentContext(
        DocumentUri uri,
        [NotNullWhen(true)] out Document? document,
        [NotNullWhen(true)] out Compilation? compilation)
        => _workspaceManager.TryGetDocumentContext(uri, out document, out compilation);

    public async Task<DocumentAnalysisContext?> GetAnalysisContextAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        return await CreateDocumentAnalysisContextAsync(
            uri,
            position: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DocumentAnalysisContext?> GetAnalysisContextAsync(
        DocumentUri uri,
        Position position,
        CancellationToken cancellationToken)
    {
        return await CreateDocumentAnalysisContextAsync(
            uri,
            position,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<DocumentSyntaxContext?> GetDocumentSyntaxContextAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        if (GeneratedSourceDocument.TryParse(uri, out _, out _))
        {
            var context = await GetAnalysisContextAsync(uri, cancellationToken).ConfigureAwait(false);
            return context is { } generated
                ? new DocumentSyntaxContext(generated.Document, generated.SyntaxTree, generated.SourceText)
                : null;
        }

        await FlushPendingDocumentChangeAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!TryGetDocument(uri, out var document) || document is null)
            return null;

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxTree is null)
            return null;

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentSyntaxContext(document, syntaxTree, sourceText);
    }

    internal async Task<SemanticModel?> GetSemanticModelAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        var context = await CreateDocumentAnalysisContextAsync(
            uri,
            position: null,
            cancellationToken).ConfigureAwait(false);
        if (context is null)
            return null;

        var stopwatch = Stopwatch.StartNew();
        var setupBefore = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
        var semanticModel = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
        stopwatch.Stop();

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowSemanticModelMaterializationThresholdMs)
        {
            var setupAfter = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            var setupDelta = CompilerSetupInstrumentation.Subtract(setupAfter, setupBefore);
            _logger.LogInformation(
                "Slow semantic model materialization for {Uri}: total={TotalMs:F1}ms setupDelta=[{SetupDelta}].",
                uri,
                stopwatch.Elapsed.TotalMilliseconds,
                CompilerSetupInstrumentation.FormatDelta(setupDelta));
        }

        return semanticModel;
    }

    private async Task<DocumentAnalysisContext?> CreateDocumentAnalysisContextAsync(
        DocumentUri uri,
        Position? position,
        CancellationToken cancellationToken)
    {
        if (GeneratedSourceDocument.TryParse(uri, out var origin, out var generatedPath))
        {
            var sourceContext = await CreateDocumentAnalysisContextAsync(origin, null, cancellationToken).ConfigureAwait(false);
            if (sourceContext is null)
                return null;
            var generatedTree = sourceContext.Value.Compilation.SyntaxTrees.FirstOrDefault(tree => tree.FilePath == generatedPath);
            if (generatedTree is null || sourceContext.Value.Document.Project.Documents.Any(document => document.FilePath == generatedPath))
                return null;
            return sourceContext.Value with { SyntaxTree = generatedTree, SourceText = generatedTree.GetText() };
        }

        await FlushPendingDocumentChangeAsync(uri, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        double compilationLookupMs = 0;
        double syntaxTreeMs = 0;
        double sourceTextMs = 0;

        if (!TryGetDocumentContext(uri, out var document, out var compilation) ||
            document is null ||
            compilation is null)
        {
            return null;
        }
        compilationLookupMs = stopwatch.Elapsed.TotalMilliseconds;

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (syntaxTree is null)
            return null;
        syntaxTreeMs = stopwatch.Elapsed.TotalMilliseconds - compilationLookupMs;

        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        sourceTextMs = stopwatch.Elapsed.TotalMilliseconds - compilationLookupMs - syntaxTreeMs;
        if (!compilation.SyntaxTrees.Contains(syntaxTree) &&
            !compilation.MacroSyntaxTrees.Contains(syntaxTree))
        {
            syntaxTree = position is { } authoredPosition
                ? compilation.GetSemanticModel(
                    syntaxTree,
                    Math.Clamp(
                        PositionHelper.ToOffset(sourceText, authoredPosition),
                        0,
                        syntaxTree.Length))
                    .SyntaxTree
                : FindMatchingCompilationSyntaxTree(compilation, syntaxTree, document.FilePath);
            if (syntaxTree is null)
                return null;

            sourceText = syntaxTree.GetText() ?? sourceText;
        }

        stopwatch.Stop();
        if (stopwatch.Elapsed.TotalMilliseconds >= SlowAnalysisContextThresholdMs)
        {
            _logger.LogInformation(
                "Slow analysis context for {Uri}: total={TotalMs:F1}ms compilationLookup={CompilationLookupMs:F1}ms syntaxTree={SyntaxTreeMs:F1}ms sourceText={SourceTextMs:F1}ms.",
                uri,
                stopwatch.Elapsed.TotalMilliseconds,
                compilationLookupMs,
                syntaxTreeMs,
                sourceTextMs);
        }

        return new DocumentAnalysisContext(document, compilation, syntaxTree, sourceText);
    }

    public bool TryGetCompilation(DocumentUri uri, [NotNullWhen(true)] out Compilation? compilation)
        => _workspaceManager.TryGetCompilation(uri, out compilation);

    public bool RemoveDocument(DocumentUri uri)
    {
        DiscardPendingDocumentChange(uri);
        RemoveCachedDocumentDiagnostics(uri);
        return _workspaceManager.RemoveDocument(uri);
    }

    public async ValueTask<IDisposable> EnterCompilerAccessAsync(
        CancellationToken cancellationToken,
        string? purpose = null,
        DocumentUri? uri = null)
    {
        var stopwatch = Stopwatch.StartNew();
        await _compilerAccessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowCompilerGateThresholdMs)
        {
            _logger.LogInformation(
                "Slow compiler gate wait for {Purpose} {Uri}: wait={WaitMs:F1}ms.",
                purpose ?? "unknown",
                uri?.ToString() ?? "<none>",
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new CompilerAccessLease(_compilerAccessGate);
    }

    public async ValueTask<IDisposable?> TryEnterCompilerAccessAsync(
        CancellationToken cancellationToken,
        string? purpose = null,
        DocumentUri? uri = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var acquired = await _compilerAccessGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!acquired)
        {
            _logger.LogDebug(
                "Skipped compiler gate acquisition for {Purpose} {Uri}: gate busy.",
                purpose ?? "unknown",
                uri?.ToString() ?? "<none>");
            return null;
        }

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowCompilerGateThresholdMs)
        {
            _logger.LogInformation(
                "Slow compiler gate wait for {Purpose} {Uri}: wait={WaitMs:F1}ms.",
                purpose ?? "unknown",
                uri?.ToString() ?? "<none>",
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new CompilerAccessLease(_compilerAccessGate);
    }

    public async ValueTask<IDisposable> EnterDocumentSemanticAccessAsync(
        DocumentUri uri,
        CancellationToken cancellationToken,
        string? purpose = null)
        => await EnterDocumentSemanticModelAccessAsync(uri, cancellationToken, purpose).ConfigureAwait(false);

    internal async ValueTask<DocumentSemanticAccess> EnterDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        PreemptBackgroundSemanticWork(uri, purpose);

        var semanticModel = await GetSemanticModelAsync(uri, cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
            return new DocumentSemanticAccess(null, NoopAccessLease.Instance);

        return await EnterDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    internal async ValueTask<DocumentSemanticAccess> EnterDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        DocumentAnalysisContext context,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        PreemptBackgroundSemanticWork(uri, purpose);

        var semanticModel = GetSemanticModel(context, uri);
        return await EnterDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    private async ValueTask<DocumentSemanticAccess> EnterDocumentSemanticModelAccessCoreAsync(
        DocumentUri uri,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        string? purpose)
    {
        var stopwatch = Stopwatch.StartNew();
        var lease = await semanticModel.EnterSemanticAccessAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowCompilerGateThresholdMs)
        {
            _logger.LogInformation(
                "Slow document semantic gate wait for {Purpose} {Uri}: wait={WaitMs:F1}ms.",
                purpose ?? "unknown",
                uri,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new DocumentSemanticAccess(semanticModel, lease);
    }

    public async ValueTask<IDisposable?> TryEnterDocumentSemanticAccessAsync(
        DocumentUri uri,
        CancellationToken cancellationToken,
        string? purpose = null)
        => await TryEnterDocumentSemanticModelAccessAsync(uri, cancellationToken, purpose).ConfigureAwait(false);

    internal async ValueTask<DocumentSemanticAccess?> TryEnterDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        var semanticModel = await GetSemanticModelAsync(uri, cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
            return null;

        return await TryEnterDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    internal async ValueTask<DocumentSemanticAccess?> TryEnterDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        DocumentAnalysisContext context,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        var semanticModel = GetSemanticModel(context, uri);
        return await TryEnterDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    private async ValueTask<DocumentSemanticAccess?> TryEnterDocumentSemanticModelAccessCoreAsync(
        DocumentUri uri,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        string? purpose)
    {
        var stopwatch = Stopwatch.StartNew();
        var lease = await semanticModel.TryEnterSemanticAccessAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (lease is null)
        {
            _logger.LogDebug(
                "Skipped document semantic gate acquisition for {Purpose} {Uri}: gate busy.",
                purpose ?? "unknown",
                uri);
            return null;
        }

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowCompilerGateThresholdMs)
        {
            _logger.LogInformation(
                "Slow document semantic gate wait for {Purpose} {Uri}: wait={WaitMs:F1}ms.",
                purpose ?? "unknown",
                uri,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new DocumentSemanticAccess(semanticModel, lease);
    }

    private SemanticModel GetSemanticModel(DocumentAnalysisContext context, DocumentUri uri)
    {
        var stopwatch = Stopwatch.StartNew();
        var setupBefore = context.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
        var semanticModel = context.Compilation.GetSemanticModel(context.SyntaxTree);
        stopwatch.Stop();

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowSemanticModelMaterializationThresholdMs)
        {
            var setupAfter = context.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            var setupDelta = CompilerSetupInstrumentation.Subtract(setupAfter, setupBefore);
            _logger.LogInformation(
                "Slow semantic model materialization for {Uri}: total={TotalMs:F1}ms setupDelta=[{SetupDelta}].",
                uri,
                stopwatch.Elapsed.TotalMilliseconds,
                CompilerSetupInstrumentation.FormatDelta(setupDelta));
        }

        return semanticModel;
    }

    internal async ValueTask<DocumentSemanticAccess?> TryEnterExistingDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        var context = await CreateDocumentAnalysisContextAsync(
            uri,
            position: null,
            cancellationToken).ConfigureAwait(false);
        if (context is null)
            return null;

        if (!context.Value.Compilation.TryGetExistingSemanticModel(context.Value.SyntaxTree, out var semanticModel))
            return null;

        return await TryEnterExistingDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    internal async ValueTask<DocumentSemanticAccess?> TryEnterExistingDocumentSemanticModelAccessAsync(
        DocumentUri uri,
        DocumentAnalysisContext context,
        CancellationToken cancellationToken,
        string? purpose = null)
    {
        if (!context.Compilation.TryGetExistingSemanticModel(context.SyntaxTree, out var semanticModel))
            return null;

        return await TryEnterExistingDocumentSemanticModelAccessCoreAsync(
            uri,
            semanticModel,
            cancellationToken,
            purpose).ConfigureAwait(false);
    }

    private async ValueTask<DocumentSemanticAccess?> TryEnterExistingDocumentSemanticModelAccessCoreAsync(
        DocumentUri uri,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        string? purpose)
    {
        var stopwatch = Stopwatch.StartNew();
        var lease = await semanticModel.TryEnterSemanticAccessAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (lease is null)
        {
            _logger.LogDebug(
                "Skipped existing document semantic gate acquisition for {Purpose} {Uri}: gate busy.",
                purpose ?? "unknown",
                uri);
            return null;
        }

        if (stopwatch.Elapsed.TotalMilliseconds >= SlowCompilerGateThresholdMs)
        {
            _logger.LogInformation(
                "Slow existing document semantic gate wait for {Purpose} {Uri}: wait={WaitMs:F1}ms.",
                purpose ?? "unknown",
                uri,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return new DocumentSemanticAccess(semanticModel, lease);
    }

    public async Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
    {
        var result = await GetDiagnosticsCoreAsync(uri, shouldSkipWork, allowBusySkip: false, DiagnosticLane.ProjectWithAnalyzers, cancellationToken).ConfigureAwait(false);
        return result.Diagnostics;
    }

    internal Task<DiagnosticsComputationResult> TryGetSyntaxDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => TryGetDiagnosticsAsync(uri, DiagnosticLane.Syntax, shouldSkipWork, cancellationToken);

    internal Task<DiagnosticsComputationResult> TryGetDocumentCompilerDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => TryGetDiagnosticsAsync(uri, DiagnosticLane.DocumentCompiler, shouldSkipWork, cancellationToken);

    internal Task<DiagnosticsComputationResult> TryGetProjectCompilerDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => TryGetDiagnosticsAsync(uri, DiagnosticLane.ProjectCompiler, shouldSkipWork, cancellationToken);

    internal Task<DiagnosticsComputationResult> TryGetProjectWithAnalyzersDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => TryGetDiagnosticsAsync(uri, DiagnosticLane.ProjectWithAnalyzers, shouldSkipWork, cancellationToken);

    internal Task<DiagnosticsComputationResult> TryGetDocumentWithAnalyzersDiagnosticsAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => TryGetDiagnosticsAsync(uri, DiagnosticLane.DocumentWithAnalyzers, shouldSkipWork, cancellationToken);

    internal Task<DiagnosticsComputationResult> TryGetDiagnosticsAsync(
        DocumentUri uri,
        DiagnosticLane lane,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
        => GetDiagnosticsCoreAsync(uri, shouldSkipWork, allowBusySkip: true, lane, cancellationToken);

    private async Task<DiagnosticsComputationResult> GetDiagnosticsCoreAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        bool allowBusySkip,
        DiagnosticLane lane,
        CancellationToken cancellationToken)
    {
        if (lane == DiagnosticLane.Syntax)
            return await GetSyntaxDiagnosticsCoreAsync(uri, shouldSkipWork, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        double gateWaitMs = 0;
        double syntaxTreeMs = 0;
        double diagnosticsFetchMs = 0;
        string? diagnosticsSetupDelta = null;
        string? diagnosticsBinderDelta = null;
        string? diagnosticsSemanticDelta = null;
        string? diagnosticsBindingDelta = null;
        var busySkipped = false;
        var preempted = false;

        try
        {
            if (shouldSkipWork?.Invoke() == true)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

            if (!_workspaceManager.CanPublishSemanticDiagnostics(uri))
            {
                _logger.LogDebug(
                    "Skipped semantic diagnostics for {Uri}: project-backed root has not loaded successfully.",
                    uri);
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: false);
            }

            var useBusySkip = allowBusySkip;
            using var effectiveCancellation = useBusySkip
                ? CreateBackgroundSemanticWorkCancellation(cancellationToken)
                : null;
            var effectiveCancellationToken = effectiveCancellation?.Token ?? cancellationToken;

            DocumentAnalysisContext? context;
            if (useBusySkip)
            {
                using (var lease = await TryEnterCompilerAccessAsync(effectiveCancellationToken, "diagnostics", uri).ConfigureAwait(false))
                {
                    if (lease is null)
                    {
                        busySkipped = true;
                        return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
                    }

                    gateWaitMs = stopwatch.Elapsed.TotalMilliseconds;
                    if (shouldSkipWork?.Invoke() == true)
                        return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

                    context = await CreateDocumentAnalysisContextAsync(
                        uri,
                        position: null,
                        effectiveCancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                using (var lease = await EnterCompilerAccessAsync(effectiveCancellationToken, "diagnostics", uri).ConfigureAwait(false))
                {
                    gateWaitMs = stopwatch.Elapsed.TotalMilliseconds;
                    if (shouldSkipWork?.Invoke() == true)
                        return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

                    context = await CreateDocumentAnalysisContextAsync(
                        uri,
                        position: null,
                        effectiveCancellationToken).ConfigureAwait(false);
                }
            }

            if (context is null)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: false);
            var syntaxTree = context.Value.SyntaxTree;
            var diagnosticSnapshotKey = new DiagnosticSnapshotKey(
                uri.ToString(),
                context.Value.Document.Project.Id,
                context.Value.Document.Version,
                context.Value.Document.Project.Version);
            var documentDiagnosticsCacheKey = new DocumentDiagnosticsCacheKey(diagnosticSnapshotKey);
            syntaxTreeMs = stopwatch.Elapsed.TotalMilliseconds - gateWaitMs;
            if (shouldSkipWork?.Invoke() == true)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

            var setupBefore = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            var semanticBefore = context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();
            var diagnosticBindingBefore = context.Value.Compilation.PerformanceInstrumentation.DiagnosticBinding.CaptureSnapshot();
            var binderBefore = context.Value.Compilation.PerformanceInstrumentation.BinderReentry.CaptureSnapshot();
            IReadOnlyCollection<CodeDiagnostic>? diagnosticsForProject = null;
            ImmutableArray<LspDiagnostic>? mappedDiagnostics = null;
            if (lane == DiagnosticLane.DocumentCompiler)
            {
                mappedDiagnostics = await GetOrComputeDocumentCompilerDiagnosticsAsync(
                    uri,
                    context.Value.Compilation,
                    syntaxTree,
                    context.Value.Document.SyntaxTree ?? syntaxTree,
                    documentDiagnosticsCacheKey,
                    useBusySkip,
                    effectiveCancellationToken).ConfigureAwait(false);
                if (mappedDiagnostics is null)
                {
                    busySkipped = true;
                    return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
                }
            }
            else if (lane == DiagnosticLane.DocumentWithAnalyzers)
            {
                mappedDiagnostics = await GetOrComputeDocumentWithAnalyzersDiagnosticsAsync(
                    uri,
                    context.Value.Document,
                    context.Value.Compilation,
                    syntaxTree,
                    documentDiagnosticsCacheKey,
                    requireCachedCompilerDiagnostics: useBusySkip && shouldSkipWork is not null,
                    useBusySkip,
                    effectiveCancellationToken).ConfigureAwait(false);
                if (mappedDiagnostics is null)
                {
                    busySkipped = true;
                    return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
                }
            }
            else if (lane == DiagnosticLane.ProjectCompiler)
            {
                diagnosticsForProject = context.Value.Compilation.GetDiagnostics(
                    analyzerOptions: null,
                    cancellationToken: effectiveCancellationToken);
            }
            else
            {
                if (!_workspaceManager.TryGetProjectAnalyzerDiagnostics(
                        context.Value.Document,
                        context.Value.Compilation,
                        out var projectDiagnosticsWithAnalyzers,
                        cancellationToken: effectiveCancellationToken,
                        allowBusySkip: useBusySkip))
                    return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

                diagnosticsForProject = projectDiagnosticsWithAnalyzers;
            }

            diagnosticsFetchMs = stopwatch.Elapsed.TotalMilliseconds - gateWaitMs - syntaxTreeMs;
            if (shouldSkipWork?.Invoke() == true)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

            var diagnostics = mappedDiagnostics ?? MapDiagnostics(diagnosticsForProject ?? [], syntaxTree);
            if (lane == DiagnosticLane.DocumentCompiler)
                _documentCompilerDiagnosticsCache[documentDiagnosticsCacheKey] = diagnostics;
            diagnosticsSetupDelta = CompilerSetupInstrumentation.FormatDelta(
                CompilerSetupInstrumentation.Subtract(
                    context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot(),
                    setupBefore));
            diagnosticsBinderDelta = BinderReentryInstrumentation.FormatDelta(
                BinderReentryInstrumentation.Subtract(
                    context.Value.Compilation.PerformanceInstrumentation.BinderReentry.CaptureSnapshot(),
                    binderBefore));
            diagnosticsSemanticDelta = SemanticQueryInstrumentation.FormatDelta(
                SemanticQueryInstrumentation.Subtract(
                    context.Value.Compilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot(),
                    semanticBefore));
            diagnosticsBindingDelta = DiagnosticBindingInstrumentation.FormatDelta(
                DiagnosticBindingInstrumentation.Subtract(
                    context.Value.Compilation.PerformanceInstrumentation.DiagnosticBinding.CaptureSnapshot(),
                    diagnosticBindingBefore));

            stopwatch.Stop();
            if (stopwatch.Elapsed.TotalMilliseconds >= SlowDiagnosticsThresholdMs)
            {
                if (diagnosticsSetupDelta is not null ||
                    diagnosticsBinderDelta is not null ||
                    diagnosticsSemanticDelta is not null ||
                    diagnosticsBindingDelta is not null)
                {
                    _logger.LogInformation(
                        "Slow diagnostics for {Uri}: total={TotalMs:F1}ms gateWait={GateWaitMs:F1}ms syntaxTree={SyntaxTreeMs:F1}ms diagnosticsFetch={DiagnosticsFetchMs:F1}ms count={Count} setupDelta=[{SetupDelta}] diagnosticBindingDelta=[{DiagnosticBindingDelta}] binderDelta=[{BinderDelta}] semanticDelta=[{SemanticDelta}].",
                        uri,
                        stopwatch.Elapsed.TotalMilliseconds,
                        gateWaitMs,
                        syntaxTreeMs,
                        diagnosticsFetchMs,
                        diagnostics.Length,
                        diagnosticsSetupDelta ?? "<none>",
                        diagnosticsBindingDelta ?? "<none>",
                        diagnosticsBinderDelta ?? "<none>",
                        diagnosticsSemanticDelta ?? "<none>");
                }
                else
                {
                    _logger.LogInformation(
                        "Slow diagnostics for {Uri}: total={TotalMs:F1}ms gateWait={GateWaitMs:F1}ms syntaxTree={SyntaxTreeMs:F1}ms diagnosticsFetch={DiagnosticsFetchMs:F1}ms count={Count}.",
                        uri,
                        stopwatch.Elapsed.TotalMilliseconds,
                        gateWaitMs,
                        syntaxTreeMs,
                        diagnosticsFetchMs,
                        diagnostics.Length);
                }
            }

            return new DiagnosticsComputationResult(diagnostics, WasSkipped: false, diagnosticSnapshotKey, context.Value.SourceText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
        }
        catch (OperationCanceledException) when (allowBusySkip && !cancellationToken.IsCancellationRequested)
        {
            preempted = true;
            return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic computation failed for {Uri}.", uri);
            // Semantic diagnostic lanes are background work. If they fail, do not
            // publish an empty diagnostic set and erase the last valid analyzer
            // diagnostics; let the caller keep/retry the previous result.
            return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
        }
        finally
        {
            stopwatch.Stop();
            LanguageServerPerformanceInstrumentation.RecordOperation(
                GetComputeDiagnosticsOperationName(lane, busySkipped),
                uri,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                detail: CreateDiagnosticsPerformanceDetail(
                    uri,
                    lane,
                    diagnosticsSetupDelta,
                    diagnosticsBindingDelta,
                    diagnosticsBinderDelta,
                    diagnosticsSemanticDelta),
                stages:
                [
                    new LanguageServerPerformanceInstrumentation.StageTiming("gateWait", gateWaitMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("syntaxTree", syntaxTreeMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("diagnosticsFetch", diagnosticsFetchMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("preempted", preempted ? 1 : 0)
                ]);
        }
    }

    private async Task<ImmutableArray<LspDiagnostic>?> GetOrComputeDocumentCompilerDiagnosticsAsync(
        DocumentUri uri,
        Compilation compilation,
        SyntaxTree syntaxTree,
        SyntaxTree authoredSyntaxTree,
        DocumentDiagnosticsCacheKey cacheKey,
        bool useBusySkip,
        CancellationToken cancellationToken)
    {
        if (_documentCompilerDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
            return cachedDiagnostics;

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        using var semanticLease = await EnterDiagnosticSemanticAccessAsync(
            semanticModel,
            useBusySkip,
            cancellationToken).ConfigureAwait(false);
        if (semanticLease is null)
            return null;

        var compilerDiagnostics = GetDocumentCompilerDiagnostics(
            compilation,
            authoredSyntaxTree,
            semanticModel,
            analyzerOptions: null,
            cancellationToken);
        var mappedDiagnostics = MapDocumentDiagnostics(compilerDiagnostics);
        _documentCompilerDiagnosticsCache[cacheKey] = mappedDiagnostics;
        return mappedDiagnostics;
    }

    private async Task<ImmutableArray<LspDiagnostic>?> GetOrComputeDocumentWithAnalyzersDiagnosticsAsync(
        DocumentUri uri,
        Document document,
        Compilation compilation,
        SyntaxTree syntaxTree,
        DocumentDiagnosticsCacheKey cacheKey,
        bool requireCachedCompilerDiagnostics,
        bool useBusySkip,
        CancellationToken cancellationToken)
    {
        if (_documentWithAnalyzersDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
            return cachedDiagnostics;

        if (requireCachedCompilerDiagnostics &&
            !_documentCompilerDiagnosticsCache.ContainsKey(cacheKey))
        {
            return null;
        }

        var compilerDiagnostics = await GetOrComputeDocumentCompilerDiagnosticsAsync(
            uri,
            compilation,
            syntaxTree,
            document.SyntaxTree ?? syntaxTree,
            cacheKey,
            useBusySkip,
            cancellationToken).ConfigureAwait(false);
        if (compilerDiagnostics is null)
            return null;

        if (!_workspaceManager.TryGetDocumentAnalyzerDiagnostics(
            document,
            compilation,
            out var documentAnalyzerDiagnostics,
            allowBusySkip: useBusySkip,
            cancellationToken: cancellationToken))
        {
            return null;
        }

        var mappedDiagnostics = MergeDiagnostics(
            compilerDiagnostics.Value,
            MapDiagnostics(documentAnalyzerDiagnostics, syntaxTree));
        _documentWithAnalyzersDiagnosticsCache[cacheKey] = mappedDiagnostics;
        return mappedDiagnostics;
    }

    private async ValueTask<IDisposable?> EnterDiagnosticSemanticAccessAsync(
        SemanticModel semanticModel,
        bool allowBusySkip,
        CancellationToken cancellationToken)
    {
        if (!allowBusySkip)
            return await semanticModel.EnterSemanticAccessAsync(cancellationToken).ConfigureAwait(false);

        var lease = await semanticModel.TryEnterSemanticAccessAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogDebug(
                "Skipped diagnostic semantic gate acquisition for {Uri}: gate busy.",
                semanticModel.SyntaxTree.FilePath ?? "<unknown>");
        }

        return lease;
    }

    private static ImmutableArray<CodeDiagnostic> GetDocumentCompilerDiagnostics(
        Compilation compilation,
        SyntaxTree syntaxTree,
        SemanticModel semanticModel,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        using var ambientAccess = semanticModel.EnterAmbientSemanticAccess();
        return compilation
            .GetDocumentDiagnostics(syntaxTree, analyzerOptions, cancellationToken)
            .OrderBy(static diagnostic => diagnostic.Location)
            .ToImmutableArray();
    }

    private void PreemptBackgroundSemanticWork(DocumentUri uri, string? purpose)
    {
        CancelPostEditSemanticWarmup(uri);

        lock (_backgroundSemanticWorkCancellationGate)
        {
            var previous = _backgroundSemanticWorkPreemption;
            _backgroundSemanticWorkPreemption = new CancellationTokenSource();

            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _logger.LogDebug(
            "Preempted background semantic work before semantic request for {Purpose} {Uri}.",
            purpose ?? "unknown",
            uri);
    }

    internal CancellationTokenSource CreateBackgroundSemanticWorkCancellation(CancellationToken cancellationToken)
    {
        CancellationToken preemptionToken;
        lock (_backgroundSemanticWorkCancellationGate)
        {
            preemptionToken = _backgroundSemanticWorkPreemption.Token;
        }

        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, preemptionToken)
            : CancellationTokenSource.CreateLinkedTokenSource(preemptionToken);
    }

    private static string CreateDiagnosticsPerformanceDetail(
        DocumentUri uri,
        DiagnosticLane lane,
        string? setupDelta,
        string? diagnosticBindingDelta,
        string? binderDelta,
        string? semanticDelta)
    {
        var detail = $"{uri} lane={lane}";

        if (!string.IsNullOrWhiteSpace(setupDelta))
            detail += $" setup=[{setupDelta}]";

        if (!string.IsNullOrWhiteSpace(diagnosticBindingDelta))
            detail += $" diagnosticBinding=[{diagnosticBindingDelta}]";

        if (!string.IsNullOrWhiteSpace(binderDelta))
            detail += $" binder=[{binderDelta}]";

        if (!string.IsNullOrWhiteSpace(semanticDelta))
            detail += $" semantic=[{semanticDelta}]";

        return detail;
    }

    private static string GetComputeDiagnosticsOperationName(DiagnosticLane lane, bool skipped)
        => (lane, skipped) switch
        {
            (DiagnosticLane.Syntax, false) => "computeSyntaxDiagnostics",
            (DiagnosticLane.Syntax, true) => "computeSyntaxDiagnosticsSkipped",
            (DiagnosticLane.DocumentCompiler, false) => "computeDocumentCompilerDiagnostics",
            (DiagnosticLane.DocumentCompiler, true) => "computeDocumentCompilerDiagnosticsSkipped",
            (DiagnosticLane.ProjectCompiler, false) => "computeProjectCompilerDiagnostics",
            (DiagnosticLane.ProjectCompiler, true) => "computeProjectCompilerDiagnosticsSkipped",
            (DiagnosticLane.DocumentWithAnalyzers, false) => "computeDocumentWithAnalyzersDiagnostics",
            (DiagnosticLane.DocumentWithAnalyzers, true) => "computeDocumentWithAnalyzersDiagnosticsSkipped",
            (DiagnosticLane.ProjectWithAnalyzers, false) => "computeProjectWithAnalyzersDiagnostics",
            _ => "computeProjectWithAnalyzersDiagnosticsSkipped"
        };

    private async Task<DiagnosticsComputationResult> GetSyntaxDiagnosticsCoreAsync(
        DocumentUri uri,
        Func<bool>? shouldSkipWork,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        double diagnosticsFetchMs = 0;

        try
        {
            if (shouldSkipWork?.Invoke() == true)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);

            var stageStopwatch = Stopwatch.StartNew();
            var context = await GetDocumentSyntaxContextAsync(uri, cancellationToken).ConfigureAwait(false);
            if (context is null)
                return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: false);
            var diagnosticSnapshotKey = new DiagnosticSnapshotKey(
                uri.ToString(),
                context.Value.Document.Project.Id,
                context.Value.Document.Version,
                context.Value.Document.Project.Version);

            var diagnostics = MapSyntaxDiagnostics(context.Value.SyntaxTree);
            diagnosticsFetchMs = stageStopwatch.Elapsed.TotalMilliseconds;

            return new DiagnosticsComputationResult(diagnostics, WasSkipped: false, diagnosticSnapshotKey, context.Value.SourceText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Syntax diagnostic computation failed for {Uri}.", uri);
            return new DiagnosticsComputationResult(Array.Empty<LspDiagnostic>(), WasSkipped: false);
        }
        finally
        {
            stopwatch.Stop();
            LanguageServerPerformanceInstrumentation.RecordOperation(
                "computeSyntaxDiagnostics",
                uri,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                detail: $"{uri} syntaxOnly",
                stages:
                [
                    new LanguageServerPerformanceInstrumentation.StageTiming("gateWait", 0),
                    new LanguageServerPerformanceInstrumentation.StageTiming("documentLookup", 0),
                    new LanguageServerPerformanceInstrumentation.StageTiming("syntaxTree", 0),
                    new LanguageServerPerformanceInstrumentation.StageTiming("diagnosticsFetch", diagnosticsFetchMs)
                ]);
        }
    }

    public Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(DocumentUri uri, CancellationToken cancellationToken)
        => GetDiagnosticsAsync(uri, shouldSkipWork: null, cancellationToken);

    public async Task WarmAnalysisAsync(DocumentUri uri, Func<bool>? shouldSkipWork, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        double gateWaitMs = 0;
        double analysisContextMs = 0;
        double semanticModelMs = 0;

        try
        {
            if (shouldSkipWork?.Invoke() == true)
                return;

            using var lease = await EnterCompilerAccessAsync(cancellationToken, "warmAnalysis", uri).ConfigureAwait(false);
            gateWaitMs = stopwatch.Elapsed.TotalMilliseconds;

            var stageStopwatch = Stopwatch.StartNew();
            var context = await CreateDocumentAnalysisContextAsync(
                uri,
                position: null,
                cancellationToken).ConfigureAwait(false);
            analysisContextMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (context is null)
                return;

            if (shouldSkipWork?.Invoke() == true)
                return;

            stageStopwatch.Restart();
            var setupBefore = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
            _ = context.Value.Compilation.GetSemanticModel(context.Value.SyntaxTree);
            semanticModelMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (semanticModelMs >= SlowSemanticModelMaterializationThresholdMs)
            {
                var setupAfter = context.Value.Compilation.PerformanceInstrumentation.Setup.CaptureSnapshot();
                var setupDelta = CompilerSetupInstrumentation.Subtract(setupAfter, setupBefore);
                _logger.LogInformation(
                    "Warm analysis semantic model setup for {Uri}: semanticModel={SemanticModelMs:F1}ms setupDelta=[{SetupDelta}].",
                    uri,
                    semanticModelMs,
                    CompilerSetupInstrumentation.FormatDelta(setupDelta));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background analysis warmup failed for {Uri}.", uri);
        }
        finally
        {
            stopwatch.Stop();
            if (stopwatch.Elapsed.TotalMilliseconds >= SlowWarmAnalysisThresholdMs)
            {
                _logger.LogInformation(
                    "Slow analysis warmup for {Uri}: total={TotalMs:F1}ms gateWait={GateWaitMs:F1}ms context={ContextMs:F1}ms semanticModel={SemanticModelMs:F1}ms.",
                    uri,
                    stopwatch.Elapsed.TotalMilliseconds,
                    gateWaitMs,
                    analysisContextMs,
                    semanticModelMs);
            }

            LanguageServerPerformanceInstrumentation.RecordOperation(
                "warmAnalysis",
                uri,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                detail: $"{uri}",
                stages:
                [
                    new LanguageServerPerformanceInstrumentation.StageTiming("gateWait", gateWaitMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("analysisContext", analysisContextMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("semanticModel", semanticModelMs)
                ]);
        }
    }

    internal static bool ShouldReport(CodeDiagnostic diagnostic, SyntaxTree syntaxTree)
    {
        if (diagnostic.Location.SourceTree == syntaxTree)
            return true;

        var syntaxTreePath = syntaxTree.FilePath;
        if (string.IsNullOrWhiteSpace(syntaxTreePath))
            return false;

        if (PathsEqual(diagnostic.Location.SourceTree?.FilePath, syntaxTreePath))
            return true;

        var lineSpanPath = diagnostic.Location.GetLineSpan().Path;
        return PathsEqual(lineSpanPath, syntaxTreePath);
    }

    internal static ImmutableArray<LspDiagnostic> MapSyntaxDiagnostics(SyntaxTree syntaxTree)
        => syntaxTree.GetDiagnostics()
            .Select(MapDiagnostic)
            .OrderBy(static diagnostic => diagnostic.Range.Start.Line)
            .ThenBy(static diagnostic => diagnostic.Range.Start.Character)
            .ThenBy(static diagnostic => diagnostic.Range.End.Line)
            .ThenBy(static diagnostic => diagnostic.Range.End.Character)
            .ThenBy(static diagnostic => diagnostic.Code?.String ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<LspDiagnostic> MapDiagnostics(
        IEnumerable<CodeDiagnostic> diagnostics,
        SyntaxTree syntaxTree)
        => diagnostics
            .Where(diagnostic => ShouldReport(diagnostic, syntaxTree))
            .Select(MapDiagnostic)
            .OrderBy(static diagnostic => diagnostic.Range.Start.Line)
            .ThenBy(static diagnostic => diagnostic.Range.Start.Character)
            .ThenBy(static diagnostic => diagnostic.Range.End.Line)
            .ThenBy(static diagnostic => diagnostic.Range.End.Character)
            .ThenBy(static diagnostic => diagnostic.Code?.String ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<LspDiagnostic> MapDocumentDiagnostics(
        IEnumerable<CodeDiagnostic> diagnostics)
        => diagnostics
            .Select(MapDiagnostic)
            .OrderBy(static diagnostic => diagnostic.Range.Start.Line)
            .ThenBy(static diagnostic => diagnostic.Range.Start.Character)
            .ThenBy(static diagnostic => diagnostic.Range.End.Line)
            .ThenBy(static diagnostic => diagnostic.Range.End.Character)
            .ThenBy(static diagnostic => diagnostic.Code?.String ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<LspDiagnostic> MergeDiagnostics(
        ImmutableArray<LspDiagnostic> compilerDiagnostics,
        ImmutableArray<LspDiagnostic> analyzerDiagnostics)
        => compilerDiagnostics
            .Concat(analyzerDiagnostics)
            .OrderBy(static diagnostic => diagnostic.Range.Start.Line)
            .ThenBy(static diagnostic => diagnostic.Range.Start.Character)
            .ThenBy(static diagnostic => diagnostic.Range.End.Line)
            .ThenBy(static diagnostic => diagnostic.Range.End.Character)
            .ThenBy(static diagnostic => diagnostic.Code?.String ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static LspDiagnostic MapDiagnostic(CodeDiagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var range = new LspRange(
            new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
            new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character));

        return new LspDiagnostic
        {
            Message = diagnostic.GetMessage(),
            Code = diagnostic.Id,
            Severity = MapSeverity(diagnostic.Severity),
            Source = diagnostic.Properties.ContainsKey(Raven.CodeAnalysis.Diagnostics.AnalyzerDiagnosticProperties.AnalyzerName)
                ? "raven-analyzer"
                : "raven",
            Range = range,
            Tags = MapTags(diagnostic),
            Data = RavenDiagnosticData.Create(diagnostic)
        };
    }

    internal static Container<DiagnosticTag>? MapTags(CodeDiagnostic diagnostic)
    {
        if (UnnecessaryDiagnosticIds.Contains(diagnostic.Id) ||
            (string.Equals(diagnostic.Id, Raven.CodeAnalysis.Diagnostics.UnusedMethodAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase) &&
             diagnostic.Properties.TryGetValue(Raven.CodeAnalysis.Diagnostics.UnusedMethodAnalyzer.UnnecessaryDiagnosticProperty, out var value) &&
             string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase)))
        {
            return new Container<DiagnosticTag>(DiagnosticTag.Unnecessary);
        }

        return null;
    }

    private static LspDiagnosticSeverity? MapSeverity(CodeDiagnosticSeverity severity)
        => severity switch
        {
            CodeDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
            CodeDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
            CodeDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
            CodeDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hint,
            _ => null
        };

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveCachedDocumentDiagnostics(DocumentUri uri)
    {
        var uriText = uri.ToString();
        foreach (var key in _documentCompilerDiagnosticsCache.Keys)
        {
            if (string.Equals(key.SnapshotKey.Uri, uriText, StringComparison.Ordinal))
                _documentCompilerDiagnosticsCache.TryRemove(key, out _);
        }

        foreach (var key in _documentWithAnalyzersDiagnosticsCache.Keys)
        {
            if (string.Equals(key.SnapshotKey.Uri, uriText, StringComparison.Ordinal))
                _documentWithAnalyzersDiagnosticsCache.TryRemove(key, out _);
        }
    }

    private void RemoveStaleCachedDocumentDiagnostics(ProjectId projectId, VersionStamp projectVersion)
    {
        foreach (var key in _documentCompilerDiagnosticsCache.Keys)
        {
            if (key.SnapshotKey.ProjectId == projectId && key.SnapshotKey.ProjectVersion != projectVersion)
                _documentCompilerDiagnosticsCache.TryRemove(key, out _);
        }

        foreach (var key in _documentWithAnalyzersDiagnosticsCache.Keys)
        {
            if (key.SnapshotKey.ProjectId == projectId && key.SnapshotKey.ProjectVersion != projectVersion)
                _documentWithAnalyzersDiagnosticsCache.TryRemove(key, out _);
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static SyntaxTree? FindMatchingCompilationSyntaxTree(Compilation compilation, SyntaxTree syntaxTree, string? documentFilePath)
    {
        if (compilation.SyntaxTrees.Contains(syntaxTree))
            return syntaxTree;

        var candidatePath = documentFilePath ?? syntaxTree.FilePath;
        if (!string.IsNullOrWhiteSpace(candidatePath))
        {
            var normalizedCandidatePath = NormalizePath(candidatePath);
            foreach (var compilationSyntaxTree in compilation.SyntaxTrees)
            {
                if (string.IsNullOrWhiteSpace(compilationSyntaxTree.FilePath))
                    continue;

                if (string.Equals(
                        NormalizePath(compilationSyntaxTree.FilePath),
                        normalizedCandidatePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return compilationSyntaxTree;
                }
            }
        }

        return null;
    }

    private sealed class CompilerAccessLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public CompilerAccessLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }

    private sealed class NoopAccessLease : IDisposable
    {
        public static readonly NoopAccessLease Instance = new();

        public void Dispose()
        {
        }
    }

    internal readonly record struct DiagnosticSnapshotKey(
        string Uri,
        ProjectId ProjectId,
        VersionStamp DocumentVersion,
        VersionStamp ProjectVersion);

    private readonly record struct DocumentDiagnosticsCacheKey(DiagnosticSnapshotKey SnapshotKey);
}
