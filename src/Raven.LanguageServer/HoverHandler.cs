using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Documentation;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using TextDocumentSelector = OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentSelector;

namespace Raven.LanguageServer;

internal sealed class HoverHandler : IHoverHandler
{
    private const double SlowHoverThresholdMs = 250;
    private const double HoverLifecycleLogThresholdMs = 100;

    private readonly DocumentStore _documents;
    private readonly ILogger<HoverHandler> _logger;
    private readonly LatestDocumentRequestTracker _latestRequests = new();
    private readonly ConcurrentDictionary<HoverPresentationCacheKey, string> _hoverPresentationCache = new();

    public HoverHandler(DocumentStore documents, ILogger<HoverHandler> logger)
    {
        _documents = documents;
        _logger = logger;
    }

    public HoverRegistrationOptions GetRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("raven")
        };

    public void SetCapability(HoverCapability capability)
    {
    }

    public async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var requestState = _latestRequests.Begin(request.TextDocument.Uri, cancellationToken);
        var effectiveCancellationToken = requestState.Token;
        var totalStopwatch = Stopwatch.StartNew();
        double gateWaitMs = 0;
        double analysisContextMs = 0;
        double semanticModelMs = 0;
        double resolutionMs = 0;
        double invocationOverrideMs = 0;
        double signatureMs = 0;
        double containingMs = 0;
        double documentationMs = 0;
        double capturesMs = 0;
        double hoverTextMs = 0;
        double rangeMs = 0;
        double directInvocationResolutionMs = 0;
        double directAttributeResolutionMs = 0;
        double declaredSymbolResolutionMs = 0;
        double genericSymbolResolutionMs = 0;
        Compilation? semanticCompilation = null;
        SemanticQueryInstrumentation.Snapshot? semanticBefore = null;
        var currentStage = "starting";
        using var hoverWatchdog = StartHoverWatchdog(request, totalStopwatch, () => currentStage, effectiveCancellationToken);

        try
        {
            currentStage = "analysisContext";
            var stageStopwatch = Stopwatch.StartNew();
            var context = await _documents.GetAnalysisContextAsync(
                request.TextDocument.Uri,
                request.Position,
                effectiveCancellationToken).ConfigureAwait(false);
            analysisContextMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (context is null)
                return null;

            var syntaxTree = context.Value.SyntaxTree;
            var sourceText = context.Value.SourceText;
            currentStage = "syntaxRoot";
            var root = syntaxTree.GetRoot(effectiveCancellationToken);
            var offset = Math.Clamp(PositionHelper.ToOffset(sourceText, request.Position), 0, root.FullSpan.End);

            currentStage = "syntaxOnlyHover";
            var syntaxOnlyHover = TryBuildFunctionExpressionDeclarationHover(sourceText, root, offset)
                ?? TryBuildDeclarationSyntaxHover(sourceText, root, offset)
                ?? TryBuildKnownTypeIdentifierSyntaxHover(sourceText, root, offset);
            if (syntaxOnlyHover is not null)
                return syntaxOnlyHover;

            if (ShouldSuppressSemanticHover(root, offset))
                return null;

            effectiveCancellationToken.ThrowIfCancellationRequested();

            currentStage = "semanticGate";
            var gateWaitStopwatch = Stopwatch.StartNew();
            using var semanticAccess = await _documents.EnterDocumentSemanticModelAccessAsync(
                request.TextDocument.Uri,
                context.Value,
                effectiveCancellationToken,
                "hover").ConfigureAwait(false);
            gateWaitMs = gateWaitStopwatch.Elapsed.TotalMilliseconds;

            currentStage = "semanticModel";
            stageStopwatch.Restart();
            var semanticModel = semanticAccess.SemanticModel;
            semanticModelMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (semanticModel is null)
                return null;
            semanticCompilation = semanticModel.Compilation;
            semanticBefore = semanticCompilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot();

            effectiveCancellationToken.ThrowIfCancellationRequested();

            currentStage = "macroSymbolHover";
            var macroSymbolResolution = TryResolveMacroSymbolHover(
                semanticModel,
                root,
                offset,
                effectiveCancellationToken);
            var isInMacroBody = IsInMacroTokenTreeBody(root, offset);
            if (macroSymbolResolution is null && !isInMacroBody)
            {
                currentStage = "macroHover";
                var macroHover = TryBuildMacroInvocationHover(sourceText, semanticModel, root, offset)
                    ?? TryBuildAttachedMacroHover(sourceText, semanticModel, root, offset);
                if (macroHover is not null)
                    return macroHover;
            }

            effectiveCancellationToken.ThrowIfCancellationRequested();

            currentStage = "literalHover";
            var literalHover = TryBuildLiteralHover(sourceText, semanticModel, root, offset);
            if (literalHover is not null)
                return literalHover;

            effectiveCancellationToken.ThrowIfCancellationRequested();

            currentStage = "patternHover";
            var patternHover = TryBuildPatternDeclarationHover(sourceText, semanticModel, root, offset);
            if (patternHover is not null)
                return patternHover;

            if (macroSymbolResolution is null && isInMacroBody)
                return null;

            effectiveCancellationToken.ThrowIfCancellationRequested();

            currentStage = "resolution";
            var resolutionStopwatch = Stopwatch.StartNew();
            stageStopwatch.Restart();
            var resolution = macroSymbolResolution
                ?? TryResolveDeclaredHoverSymbol(semanticModel, root, offset);
            declaredSymbolResolutionMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (resolution is null)
            {
                stageStopwatch.Restart();
                resolution = TryResolveAttributeHoverDirect(semanticModel, root, offset);
                directAttributeResolutionMs = stageStopwatch.Elapsed.TotalMilliseconds;
            }

            if (resolution is null)
            {
                stageStopwatch.Restart();
                resolution = TryResolveInvocationTargetHoverDirect(semanticModel, root, offset);
                directInvocationResolutionMs = stageStopwatch.Elapsed.TotalMilliseconds;
            }

            if (resolution is null &&
                !IsInvocationTargetIdentifierAtOffset(root, offset) &&
                ShouldRunGenericSymbolResolver(sourceText, root, offset))
            {
                stageStopwatch.Restart();
                resolution = SymbolResolver.ResolveSymbolAtPosition(semanticModel, root, offset);
                genericSymbolResolutionMs = stageStopwatch.Elapsed.TotalMilliseconds;
            }

            resolutionStopwatch.Stop();
            resolutionMs = resolutionStopwatch.Elapsed.TotalMilliseconds;
            if (resolution is null)
                return null;

            effectiveCancellationToken.ThrowIfCancellationRequested();

            var resolvedValue = resolution.Value;
            currentStage = "invocationOverride";
            stageStopwatch.Restart();
            if (TryResolveInvocationTargetHoverOverride(semanticModel, root, offset, resolvedValue, out var invocationOverride))
                resolvedValue = invocationOverride;
            invocationOverrideMs = stageStopwatch.Elapsed.TotalMilliseconds;

            var symbol = resolvedValue.Symbol;
            if (resolvedValue.Kind == SymbolResolutionKind.TypePosition &&
                symbol is ITypeSymbol { TypeKind: TypeKind.Error })
            {
                return null;
            }

            var presentationCacheKey = CreateHoverPresentationCacheKey(
                request.TextDocument.Uri,
                context.Value.Document.Version,
                context.Value.Document.Project.Version,
                resolvedValue);
            if (_hoverPresentationCache.TryGetValue(presentationCacheKey, out var cachedHoverText))
            {
                currentStage = "range";
                stageStopwatch.Restart();
                var cachedHoverRange = PositionHelper.ToRange(sourceText, GetHoverSpanForResolution(resolvedValue));
                rangeMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = cachedHoverText
                    }),
                    Range = cachedHoverRange
                };
            }

            currentStage = "signature";
            stageStopwatch.Restart();
            var signature = BuildDisplaySignatureForResolvedHover(resolvedValue, semanticModel, root, offset);
            signatureMs = stageStopwatch.Elapsed.TotalMilliseconds;
            if (signature == "()" &&
                resolvedValue.Node is IdentifierNameSyntax identifier &&
                (resolvedValue.Kind == SymbolResolutionKind.TypePosition ||
                 identifier.AncestorsAndSelf().OfType<TypeSyntax>().Any()))
            {
                _logger.LogWarning(
                    "Suspicious unit hover signature for {ResolutionKind} identifier {Identifier} in {Uri} at {Line}:{Character}. SymbolKind={SymbolKind} SymbolDisplay={SymbolDisplay}",
                    resolvedValue.Kind,
                    identifier.Identifier.ValueText,
                    request.TextDocument.Uri,
                    request.Position.Line,
                    request.Position.Character,
                    symbol.Kind,
                    FormatTrackedHoverSymbolDisplay(symbol));
            }
            currentStage = "containing";
            stageStopwatch.Restart();
            var containing = BuildContainingDisplay(symbol, semanticModel);
            containingMs = stageStopwatch.Elapsed.TotalMilliseconds;

            currentStage = "documentation";
            stageStopwatch.Restart();
            var documentation = GetHoverDocumentation(symbol);
            documentationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            currentStage = "captures";
            stageStopwatch.Restart();
            var functionCaptures = ImmutableArray<ISymbol>.Empty;
            var isCapturedVariable = false;
            if (ShouldComputeCaptureInfo(symbol, resolvedValue.Node))
            {
                functionCaptures = semanticModel.GetCapturedVariables(symbol);
                if (functionCaptures.IsDefaultOrEmpty &&
                    ShouldComputeCaptureInfoFromSyntax(resolvedValue.Node))
                {
                    functionCaptures = semanticModel.GetCapturedVariables(resolvedValue.Node);
                }
            }
            capturesMs = stageStopwatch.Elapsed.TotalMilliseconds;

            currentStage = "hoverText";
            stageStopwatch.Restart();
            var hoverText = BuildHoverText(
                signature,
                BuildKindDisplayForResolution(resolvedValue.Kind, symbol),
                containing,
                documentation,
                functionCaptures,
                isCapturedVariable);
            PruneHoverPresentationCacheIfNeeded();
            _hoverPresentationCache[presentationCacheKey] = hoverText;
            hoverTextMs = stageStopwatch.Elapsed.TotalMilliseconds;

            currentStage = "range";
            stageStopwatch.Restart();
            var hoverRange = PositionHelper.ToRange(sourceText, GetHoverSpanForResolution(resolvedValue));
            rangeMs = stageStopwatch.Elapsed.TotalMilliseconds;
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverText
                }),
                Range = hoverRange
            };
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            if (totalStopwatch.Elapsed.TotalMilliseconds >= HoverLifecycleLogThresholdMs)
            {
                _logger.LogInformation(
                    requestState.IsSuperseded
                        ? "Hover request superseded for {Uri} at {Line}:{Character} after {ElapsedMs:F1}ms."
                        : "Hover request canceled for {Uri} at {Line}:{Character} after {ElapsedMs:F1}ms.",
                    request.TextDocument.Uri,
                    request.Position.Line,
                    request.Position.Character,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Hover request failed for {Uri} at {Line}:{Character}.",
                request.TextDocument.Uri,
                request.Position.Line,
                request.Position.Character);
            return null;
        }
        finally
        {
            totalStopwatch.Stop();
            var semanticDelta = semanticCompilation is not null && semanticBefore is { } before
                ? SemanticQueryInstrumentation.FormatDelta(
                    SemanticQueryInstrumentation.Subtract(
                        semanticCompilation.PerformanceInstrumentation.SemanticQuery.CaptureSnapshot(),
                        before))
                : null;
            LanguageServerPerformanceInstrumentation.RecordOperation(
                "hover",
                request.TextDocument.Uri,
                null,
                totalStopwatch.Elapsed.TotalMilliseconds,
                detail: semanticDelta is null
                    ? $"{request.TextDocument.Uri} {request.Position.Line}:{request.Position.Character}"
                    : $"{request.TextDocument.Uri} {request.Position.Line}:{request.Position.Character} semantic=[{semanticDelta}]",
                stages:
                [
                    new LanguageServerPerformanceInstrumentation.StageTiming("gateWait", gateWaitMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("analysisContext", analysisContextMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("semanticModel", semanticModelMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("resolution", resolutionMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("directInvocationResolution", directInvocationResolutionMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("directAttributeResolution", directAttributeResolutionMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("declaredSymbolResolution", declaredSymbolResolutionMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("genericSymbolResolution", genericSymbolResolutionMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("invocationOverride", invocationOverrideMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("signature", signatureMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("containing", containingMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("documentation", documentationMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("captures", capturesMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("hoverText", hoverTextMs),
                    new LanguageServerPerformanceInstrumentation.StageTiming("range", rangeMs)
                ]);

            if (totalStopwatch.Elapsed.TotalMilliseconds >= SlowHoverThresholdMs)
            {
                _logger.LogInformation(
                    "Slow hover for {Uri} at {Line}:{Character}: total={TotalMs:F1}ms gateWait={GateWaitMs:F1}ms context={ContextMs:F1}ms semanticModel={SemanticModelMs:F1}ms resolution={ResolutionMs:F1}ms directInvocation={DirectInvocationMs:F1}ms directAttribute={DirectAttributeMs:F1}ms declaredSymbol={DeclaredSymbolMs:F1}ms genericSymbol={GenericSymbolMs:F1}ms invocationOverride={InvocationOverrideMs:F1}ms signature={SignatureMs:F1}ms containing={ContainingMs:F1}ms documentation={DocumentationMs:F1}ms captures={CapturesMs:F1}ms hoverText={HoverTextMs:F1}ms range={RangeMs:F1}ms lastStage={LastStage}.",
                    request.TextDocument.Uri,
                    request.Position.Line,
                    request.Position.Character,
                    totalStopwatch.Elapsed.TotalMilliseconds,
                    gateWaitMs,
                    analysisContextMs,
                    semanticModelMs,
                    resolutionMs,
                    directInvocationResolutionMs,
                    directAttributeResolutionMs,
                    declaredSymbolResolutionMs,
                    genericSymbolResolutionMs,
                    invocationOverrideMs,
                    signatureMs,
                    containingMs,
                    documentationMs,
                    capturesMs,
                    hoverTextMs,
                    rangeMs,
                    currentStage);
            }
            else if (totalStopwatch.Elapsed.TotalMilliseconds >= HoverLifecycleLogThresholdMs)
            {
                _logger.LogDebug(
                    "Hover request completed for {Uri} at {Line}:{Character} in {TotalMs:F1}ms.",
                    request.TextDocument.Uri,
                    request.Position.Line,
                    request.Position.Character,
                    totalStopwatch.Elapsed.TotalMilliseconds);
            }

            _latestRequests.Complete(requestState);
        }
    }

    private IDisposable StartHoverWatchdog(
        HoverParams request,
        Stopwatch stopwatch,
        Func<string> getCurrentStage,
        CancellationToken effectiveCancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(effectiveCancellationToken);
        _ = LogSlowHoverProgressAsync(request, stopwatch, getCurrentStage, cancellation.Token);
        return new CancellationDisposable(cancellation);
    }

    private async Task LogSlowHoverProgressAsync(
        HoverParams request,
        Stopwatch stopwatch,
        Func<string> getCurrentStage,
        CancellationToken effectiveCancellationToken)
    {
        var checkpoints = new[] { 1_000, 3_000, 10_000 };
        var previous = 0;

        try
        {
            foreach (var checkpoint in checkpoints)
            {
                await Task.Delay(checkpoint - previous, effectiveCancellationToken).ConfigureAwait(false);
                previous = checkpoint;

                _logger.LogWarning(
                    "Hover request still running for {Uri} at {Line}:{Character}: elapsed={ElapsedMs:F1}ms stage={Stage}.",
                    request.TextDocument.Uri,
                    request.Position.Line,
                    request.Position.Character,
                    stopwatch.Elapsed.TotalMilliseconds,
                    getCurrentStage());
            }
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class CancellationDisposable : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellationDisposable(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }

    private static HoverPresentationCacheKey CreateHoverPresentationCacheKey(
        DocumentUri uri,
        VersionStamp documentVersion,
        VersionStamp projectVersion,
        SymbolResolutionResult resolution)
    {
        var span = GetHoverSpanForResolution(resolution);
        return new HoverPresentationCacheKey(
            uri.ToString(),
            documentVersion,
            projectVersion,
            resolution.Kind,
            resolution.Node.Kind,
            span.Start,
            span.Length,
            CreateHoverSymbolDisplayKey(resolution.Symbol));
    }

    private static string CreateHoverSymbolDisplayKey(ISymbol symbol)
        => $"{symbol.Kind}|{FormatTrackedHoverSymbolDisplay(symbol)}";

    private static DocumentationComment? GetHoverDocumentation(ISymbol symbol)
    {
        if (symbol is PEMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType.Name: { } containingTypeName
            } &&
            containingTypeName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            return null;
        }

        return symbol.GetDocumentationComment();
    }

    private void PruneHoverPresentationCacheIfNeeded()
    {
        const int MaxCachedHoverPresentations = 4096;
        if (_hoverPresentationCache.Count > MaxCachedHoverPresentations)
            _hoverPresentationCache.Clear();
    }

    private readonly record struct HoverPresentationCacheKey(
        string Uri,
        VersionStamp DocumentVersion,
        VersionStamp ProjectVersion,
        SymbolResolutionKind ResolutionKind,
        SyntaxKind NodeKind,
        int SpanStart,
        int SpanLength,
        string SymbolDisplayKey);

    private static string BuildHoverText(
        string signature,
        string kind,
        string? containing,
        DocumentationComment? documentation,
        ImmutableArray<ISymbol> capturedVariables,
        bool isCapturedVariable)
    {
        var docsText = FormatDocumentation(documentation);
        var captureText = FormatCaptureText(capturedVariables, isCapturedVariable);
        var contextText = !string.IsNullOrWhiteSpace(containing)
            ? FormatKindAndContainingDisplay(kind, containing)
            : kind;

        var parts = new List<string>
        {
            $"```raven\n{signature}\n```",
            contextText
        };

        if (!string.IsNullOrWhiteSpace(captureText))
            parts.Add(captureText);

        if (!string.IsNullOrWhiteSpace(docsText))
            parts.Add($"---\n\n{docsText}");

        return string.Join("\n\n", parts);
    }

    private static string FormatKindAndContainingDisplay(string kind, string containing)
    {
        const string namespacePrefix = "namespace ";
        if (containing.StartsWith(namespacePrefix, StringComparison.Ordinal))
            return $"{kind} in namespace `{containing[namespacePrefix.Length..]}`";

        return $"{kind} in `{containing}`";
    }

    internal static string BuildInlayTypeHoverText(
        ITypeSymbol type,
        SemanticModel semanticModel,
        SyntaxNode root,
        SyntaxNode contextNode,
        int offset,
        string insertionText)
    {
        var signature = BuildDisplaySignatureForResolvedHover(
            new SymbolResolutionResult(SymbolResolutionKind.TypePosition, type, contextNode),
            semanticModel,
            root,
            offset);
        var hoverText = BuildHoverText(
            signature,
            BuildKindDisplayForResolution(SymbolResolutionKind.TypePosition, type),
            BuildContainingDisplay(type, semanticModel),
            type.GetDocumentationComment(),
            ImmutableArray<ISymbol>.Empty,
            isCapturedVariable: false);

        return hoverText + $"\n\nInferred type `{insertionText}`.";
    }

    private static Hover? TryBuildLiteralHover(SourceText sourceText, SemanticModel semanticModel, SyntaxNode root, int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (!LiteralHoverPreviewFormatter.TryCreatePreview(semanticModel, token, out var preview, out var span))
                continue;

            var hoverText = BuildHoverText(
                preview,
                kind: "Constant expression",
                containing: null,
                documentation: null,
                capturedVariables: ImmutableArray<ISymbol>.Empty,
                isCapturedVariable: false);

            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverText
                }),
                Range = PositionHelper.ToRange(sourceText, span)
            };
        }

        return null;
    }

    private static Hover? TryBuildMacroInvocationHover(SourceText sourceText, SemanticModel semanticModel, SyntaxNode root, int offset)
    {
        if (IsInMacroTokenTreeBody(root, offset))
            return null;

        SyntaxToken token;
        try
        {
            token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
        }
        catch
        {
            return null;
        }

        var invocation = FindFreestandingMacroCarrier(token);
        var invocationName = invocation switch
        {
            FreestandingMacroExpressionSyntax expression => expression.Name,
            FreestandingMacroDeclarationSyntax declaration => declaration.Name,
            FreestandingMacroMemberDeclarationSyntax member => member.Name,
            _ => null
        };
        if (invocationName is null || !invocationName.Span.Contains(token.Span))
            return null;

        if (!MacroExpansionDisplayService.TryCreateForOffset(sourceText, semanticModel, root, offset, out var display))
            return null;

        var parts = new List<string> { $"Macro `{display.InvocationDisplay}`." };

        if (TryGetMacroHint(semanticModel.Compilation, display.MacroName, out var macroHint))
            parts.Add(macroHint);

        var macroSymbol = semanticModel.GetSymbolInfo(invocationName).Symbol as IMacroSymbol
            ?? semanticModel.GetSymbolInfo(invocation).Symbol as IMacroSymbol;
        var expressionResultType = macroSymbol?.ExpressionResultType;
        var isDeclaredResultType = expressionResultType is not null;
        if (expressionResultType is null &&
            invocation is FreestandingMacroExpressionSyntax macroExpression)
        {
            expressionResultType = semanticModel.GetTypeInfo(macroExpression).Type;
        }

        if (expressionResultType is { TypeKind: not TypeKind.Error })
        {
            parts.Add(
                $"Expands to an expression of {(isDeclaredResultType ? "type" : "inferred type")} `{expressionResultType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}`.");
        }
        var documentation = FormatDocumentation(macroSymbol?.GetDocumentationComment());
        if (!string.IsNullOrWhiteSpace(documentation))
            parts.Add($"---\n\n{documentation}");

        parts.Add("Use `Show macro expansion` to inspect the full expansion.");

        var hoverText = string.Join("\n\n", parts);

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = hoverText
            }),
            Range = PositionHelper.ToRange(sourceText, display.Span)
        };
    }

    private static Hover? TryBuildAttachedMacroHover(
        SourceText sourceText,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        SyntaxToken token;
        try
        {
            token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
        }
        catch
        {
            return null;
        }

        var attribute = token.GetAncestor<AttributeSyntax>();
        if (attribute is null ||
            !attribute.Name.Span.Contains(token.Span) ||
            !attribute.TryGetMacroName(out var macroName))
        {
            return null;
        }

        var symbolInfo = semanticModel.GetSymbolInfo(attribute);
        var macroSymbol = symbolInfo.Symbol as IMacroSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMacroSymbol>().FirstOrDefault();
        if (macroSymbol is null || macroSymbol.MacroKind != MacroKind.AttachedDeclaration)
            return null;

        var invocationDisplay = attribute.ArgumentList is null
            ? $"#[{macroName}]"
            : $"#[{macroName}(...)]";
        var parts = new List<string> { $"Macro `{invocationDisplay}`." };

        if (TryGetMacroHint(semanticModel.Compilation, macroName, out var macroHint))
            parts.Add(macroHint);

        var documentation = FormatDocumentation(macroSymbol.GetDocumentationComment());
        if (!string.IsNullOrWhiteSpace(documentation))
            parts.Add($"---\n\n{documentation}");

        parts.Add("Use `Show macro expansion` to inspect the full expansion.");

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = string.Join("\n\n", parts)
            }),
            Range = PositionHelper.ToRange(sourceText, attribute.Name.Span)
        };
    }

    private static bool IsInMacroTokenTreeBody(SyntaxNode root, int offset)
    {
        SyntaxToken token;
        try
        {
            token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
        }
        catch
        {
            return false;
        }

        foreach (var invocation in token.Parent?.AncestorsAndSelf()
                     .Where(static node =>
                         node is FreestandingMacroExpressionSyntax or FreestandingMacroDeclarationSyntax or FreestandingMacroMemberDeclarationSyntax) ?? [])
        {
            var tokenTree = invocation switch
            {
                FreestandingMacroExpressionSyntax expression => expression.TokenTree,
                FreestandingMacroDeclarationSyntax declaration => declaration.TokenTree,
                FreestandingMacroMemberDeclarationSyntax member => member.TokenTree,
                _ => null
            };
            if (tokenTree is null)
                continue;

            var bodyStart = tokenTree.OpenBraceToken.Span.End;
            var bodyEnd = tokenTree.CloseBraceToken.IsMissing
                ? tokenTree.BodyToken.Span.End
                : tokenTree.CloseBraceToken.SpanStart;
            if (offset >= bodyStart && offset <= bodyEnd)
                return true;
        }

        return false;
    }

    private static SymbolResolutionResult? TryResolveMacroSymbolHover(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        CancellationToken cancellationToken)
        => TryResolveMacroTokenHover(semanticModel, root, offset, cancellationToken)
            ?? TryResolveMacroFragmentHover(semanticModel, root, offset, cancellationToken);

    private static SymbolResolutionResult? TryResolveMacroFragmentHover(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        CancellationToken cancellationToken)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var invocation = FindFreestandingMacroCarrier(token);
            if (invocation is null)
                continue;

            var info = invocation switch
            {
                FreestandingMacroExpressionSyntax expression => semanticModel.GetMacroFragmentSemanticInfo(expression, candidateOffset, cancellationToken),
                FreestandingMacroDeclarationSyntax declaration => semanticModel.GetMacroFragmentSemanticInfo(declaration, candidateOffset, cancellationToken),
                FreestandingMacroMemberDeclarationSyntax member => semanticModel.GetMacroFragmentSemanticInfo(member, candidateOffset, cancellationToken),
                _ => null
            };
            var symbol = info?.SymbolInfo.Symbol ?? info?.SymbolInfo.CandidateSymbols.FirstOrDefault();
            if (info is null || symbol is null)
                continue;

            return new SymbolResolutionResult(
                SymbolResolutionKind.MacroFragment,
                symbol.UnderlyingSymbol,
                info.Syntax);
        }

        return null;
    }

    private static SymbolResolutionResult? TryResolveMacroTokenHover(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        CancellationToken cancellationToken)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var invocation = FindFreestandingMacroCarrier(token);
            if (invocation is null)
                continue;

            var tokenInfo = invocation switch
            {
                FreestandingMacroExpressionSyntax expression => semanticModel.GetMacroTokenInfo(expression, candidateOffset, cancellationToken),
                FreestandingMacroDeclarationSyntax declaration => semanticModel.GetMacroTokenInfo(declaration, candidateOffset, cancellationToken),
                FreestandingMacroMemberDeclarationSyntax member => semanticModel.GetMacroTokenInfo(member, candidateOffset, cancellationToken),
                _ => null
            };
            if (tokenInfo?.Symbol is not { } symbol)
                continue;

            return new SymbolResolutionResult(
                SymbolResolutionKind.SymbolInfo,
                symbol.UnderlyingSymbol,
                invocation,
                tokenInfo.Span);
        }

        return null;
    }

    private static SyntaxNode? FindFreestandingMacroCarrier(SyntaxToken token)
        => token.Parent?.AncestorsAndSelf().FirstOrDefault(static node =>
            node is FreestandingMacroExpressionSyntax or
                FreestandingMacroDeclarationSyntax or FreestandingMacroMemberDeclarationSyntax);

    private static bool TryGetMacroHint(Compilation compilation, string macroName, out string hint)
    {
        foreach (var macroReference in compilation.MacroReferences)
        {
            IEnumerable<IMacroDefinition> macros;
            try
            {
                macros = macroReference.Macros;
            }
            catch
            {
                continue;
            }

            foreach (var macro in macros)
            {
                if (!string.Equals(macro.Name, macroName, StringComparison.Ordinal))
                    continue;

                var kindDisplay = MacroFacts.GetKind(macro) switch
                {
                    MacroKind.AttachedDeclaration => "Attached declaration macro",
                    MacroKind.Freestanding => "Freestanding macro",
                    _ => "Macro"
                };
                var targets = MacroFacts.GetTargets(macro);
                var targetsDisplay = targets == MacroTarget.None
                    ? null
                    : $"Targets `{FormatMacroTargets(targets)}`.";
                var argumentsDisplay = MacroFacts.AcceptsArguments(macro)
                    ? "Accepts arguments."
                    : "No arguments.";

                hint = string.Join(
                    " ",
                    new[] { kindDisplay + ".", targetsDisplay, argumentsDisplay }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                return true;
            }
        }

        hint = string.Empty;
        return false;
    }

    private static string FormatMacroTargets(MacroTarget targets)
    {
        return string.Join(
            ", ",
            Enum.GetValues<MacroTarget>()
                .Where(target => target != MacroTarget.None && targets.HasFlag(target))
                .Select(static target => target.ToString()));
    }

    private static bool TryResolveInvocationTargetHoverOverride(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        SymbolResolutionResult resolution,
        out SymbolResolutionResult overrideResolution)
    {
        overrideResolution = default;

        if (resolution.Symbol is not ILocalSymbol local || !local.Type.ContainsErrorType())
            return false;

        foreach (var (identifier, invocation) in FindInvocationTargetIdentifiersAtOffset(root, offset))
        {
            if (TryResolveInvocationMethodFromSyntax(semanticModel, invocation, identifier, out overrideResolution))
                return true;
        }

        return false;
    }

    private static SymbolResolutionResult? TryResolveInvocationTargetHoverDirect(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        foreach (var (identifier, invocation) in FindInvocationTargetIdentifiersAtOffset(root, offset))
        {
            if (TryResolveInvocationMethodFromSyntax(semanticModel, invocation, identifier, out var resolution))
                return resolution;
        }

        return null;
    }

    private static SymbolResolutionResult? TryResolveAttributeHoverDirect(
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Kind != SyntaxKind.IdentifierToken)
                continue;

            var attribute = token.GetAncestor<AttributeSyntax>();
            if (attribute is not null &&
                !attribute.Name.Span.Contains(token.Span) &&
                attribute.Name.Span.End != candidateOffset)
            {
                attribute = null;
            }

            if (attribute is null)
                continue;

            var symbolInfo = semanticModel.GetSymbolInfo(attribute);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is null)
                continue;

            return new SymbolResolutionResult(SymbolResolutionKind.SymbolInfo, symbol, attribute.Name);
        }

        return null;
    }

    private static bool IsInvocationTargetIdentifierAtOffset(SyntaxNode root, int offset)
        => FindInvocationTargetIdentifiersAtOffset(root, offset).Any();

    private static bool ShouldRunGenericSymbolResolver(SourceText sourceText, SyntaxNode root, int offset)
    {
        if (offset >= 0 && offset < sourceText.Length)
        {
            var current = sourceText.GetSubText(new TextSpan(offset, 1))[0];
            if (char.IsWhiteSpace(current))
                return false;
        }

        SyntaxToken token;
        try
        {
            token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
        }
        catch
        {
            return false;
        }

        return token.Kind != SyntaxKind.EndOfFileToken && !token.IsMissing;
    }

    private static IEnumerable<(SimpleNameSyntax Identifier, InvocationExpressionSyntax Invocation)> FindInvocationTargetIdentifiersAtOffset(
        SyntaxNode root,
        int offset)
    {
        var requestedToken = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
        var requestedIdentifier = requestedToken.Parent is SimpleNameSyntax identifierAtOffset &&
                                  identifierAtOffset.Identifier.Span.Contains(offset)
            ? identifierAtOffset
            : null;
        var seen = new HashSet<(SyntaxKind IdentifierKind, TextSpan IdentifierSpan, TextSpan InvocationSpan)>();

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var identifier = token.Parent is SimpleNameSyntax identifierAtCandidate &&
                             (identifierAtCandidate.Identifier.Span.Contains(candidateOffset) ||
                              identifierAtCandidate.Identifier.Span.End == candidateOffset)
                ? identifierAtCandidate
                : token.Parent?
                    .AncestorsAndSelf()
                    .OfType<SimpleNameSyntax>()
                    .FirstOrDefault(identifier => identifier.Identifier.Span.Contains(candidateOffset) ||
                                                  identifier.Identifier.Span.End == candidateOffset);

            if (identifier is null)
                continue;

            if (requestedIdentifier is not null &&
                !HaveEquivalentSpan(identifier, requestedIdentifier))
            {
                continue;
            }

            var invocation = identifier.Parent switch
            {
                InvocationExpressionSyntax direct => direct,
                MemberBindingExpressionSyntax { Name: var name, Parent: InvocationExpressionSyntax parent }
                    when HaveEquivalentSpan(name, identifier) => parent,
                MemberAccessExpressionSyntax { Name: var name, Parent: InvocationExpressionSyntax parent }
                    when HaveEquivalentSpan(name, identifier) => parent,
                _ => null
            };

            if (invocation is null || !HaveEquivalentSpan(GetInvocationTargetIdentifier(invocation), identifier))
                continue;

            if (!seen.Add((identifier.Kind, identifier.Span, invocation.Span)))
                continue;

            yield return (identifier, invocation);
        }
    }

    private static SimpleNameSyntax? GetInvocationTargetIdentifier(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            SimpleNameSyntax identifier => identifier,
            MemberBindingExpressionSyntax { Name: SimpleNameSyntax identifier } => identifier,
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax identifier } => identifier,
            _ => null
        };
    }

    private static bool HaveEquivalentSpan(SyntaxNode? left, SyntaxNode? right)
        => left is not null &&
           right is not null &&
           left.Kind == right.Kind &&
           left.Span == right.Span;

    private static bool IsPipeRightExpressionForInvocation(SyntaxNode pipeRight, InvocationExpressionSyntax invocation)
        => HaveEquivalentSpan(pipeRight, invocation);

    private static bool TryResolveInvocationMethodFromSyntax(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        SimpleNameSyntax identifier,
        out SymbolResolutionResult resolution)
    {
        if (TryResolveInvocationMethodFromCachedSymbolInfo(semanticModel, invocation, identifier, out resolution))
            return true;

        if (semanticModel.TryGetAvailableInvocationCandidates(invocation, out var fastInvocationCandidates) &&
            TryResolveInvocationMethodFromCandidates(
                semanticModel,
                fastInvocationCandidates,
                invocation,
                identifier,
                requireUnambiguousCandidate: true,
                out resolution))
        {
            return true;
        }

        if (semanticModel.TryGetInvocationTargetSymbolInfo(invocation, out var targetInfo) &&
            TryResolveInvocationMethodFromSymbolInfo(
                semanticModel,
                invocation,
                identifier,
                targetInfo,
                requireUnambiguousCandidate: true,
                out resolution))
        {
            return true;
        }

        var invocationInfo = semanticModel.GetSymbolInfo(invocation);
        if (invocationInfo.Symbol is IMethodSymbol invocationMethod &&
            IsInvocationMethodNameMatch(invocationMethod, identifier.Identifier.ValueText))
        {
            var projected = ProjectCachedInvocationHoverSymbol(invocationMethod);
            if (!IsUnitTypeSymbol(projected))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    projected,
                    identifier);
                return true;
            }
        }

        if (invocationInfo.Symbol is null &&
            invocationInfo.CandidateSymbols.IsDefaultOrEmpty &&
            invocation.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
        {
            return false;
        }

        if (invocationInfo.Symbol is INamedTypeSymbol invocationType &&
            TryChooseConstructorForInvocation(invocationType, invocation, out var invocationConstructor))
        {
            resolution = new SymbolResolutionResult(
                SymbolResolutionKind.InvocationTarget,
                invocationConstructor,
                identifier);
            return true;
        }

        if (!invocationInfo.CandidateSymbols.IsDefaultOrEmpty &&
            TryResolveInvocationMethodFromCandidates(
                semanticModel,
                invocationInfo.CandidateSymbols.OfType<IMethodSymbol>().ToImmutableArray(),
                invocation,
                identifier,
                requireUnambiguousCandidate: true,
                out resolution))
        {
            return true;
        }

        if (TryResolveInvocationMethodFromCachedSymbolInfoCore(semanticModel, invocation, identifier, identifier, out resolution))
            return true;

        if (semanticModel.TryGetAvailableInvocationCandidates(invocation, out var availableInvocationCandidates) &&
            TryResolveInvocationMethodFromCandidates(
                semanticModel,
                availableInvocationCandidates,
                invocation,
                identifier,
                requireUnambiguousCandidate: true,
                out resolution))
        {
            return true;
        }

        if (TryResolveInvocationMethodFromCachedSymbolInfo(semanticModel, invocation, identifier, out resolution))
            return true;

        if (TryResolveInvocationTargetTypeFromTypeInfo(semanticModel, invocation, out var targetType))
        {
            if (TryResolveConstructorFromTargetType(invocation, targetType, out var targetTypeConstructor))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    targetTypeConstructor,
                    identifier);
                return true;
            }

            resolution = new SymbolResolutionResult(
                SymbolResolutionKind.InvocationTarget,
                targetType,
                identifier);
            return true;
        }

        if (TryResolveInvocationTargetTypeFromArgumentContext(semanticModel, invocation, out targetType))
        {
            if (TryResolveConstructorFromTargetType(invocation, targetType, out var targetTypeConstructor))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    targetTypeConstructor,
                    identifier);
                return true;
            }

            resolution = new SymbolResolutionResult(
                SymbolResolutionKind.InvocationTarget,
                targetType,
                identifier);
            return true;
        }

        return false;
    }

    private static bool TryResolveInvocationMethodFromSymbolInfo(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        SimpleNameSyntax identifier,
        SymbolInfo symbolInfo,
        bool requireUnambiguousCandidate,
        out SymbolResolutionResult resolution)
    {
        resolution = default;

        if (symbolInfo.Symbol is IMethodSymbol invocationMethod &&
            IsInvocationMethodNameMatch(invocationMethod, identifier.Identifier.ValueText))
        {
            var projected = ProjectCachedInvocationHoverSymbol(invocationMethod);
            if (!IsUnitTypeSymbol(projected))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    projected,
                    identifier);
                return true;
            }
        }

        if (symbolInfo.Symbol is INamedTypeSymbol invocationType &&
            TryChooseConstructorForInvocation(invocationType, invocation, out var invocationConstructor))
        {
            resolution = new SymbolResolutionResult(
                SymbolResolutionKind.InvocationTarget,
                invocationConstructor,
                identifier);
            return true;
        }

        if (!symbolInfo.CandidateSymbols.IsDefaultOrEmpty &&
            TryResolveInvocationMethodFromCandidates(
                semanticModel,
                symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().ToImmutableArray(),
                invocation,
                identifier,
                requireUnambiguousCandidate,
                out resolution))
        {
            return true;
        }

        foreach (var candidateType in symbolInfo.CandidateSymbols.OfType<INamedTypeSymbol>())
        {
            if (TryChooseConstructorForInvocation(candidateType, invocation, out var candidateConstructor))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    candidateConstructor,
                    identifier);
                return true;
            }
        }

        return false;
    }

    private static bool IsPipeRightInvocation(InvocationExpressionSyntax invocation)
        => invocation.Parent is InfixOperatorExpressionSyntax
        {
            OperatorToken.Kind: SyntaxKind.PipeToken
        } pipeExpression &&
        IsPipeRightExpressionForInvocation(pipeExpression.Right, invocation);

    private static bool InvocationContainsFunctionArguments(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.Any(static argument =>
            argument.Expression.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>().Any());

    private static bool TryResolveInvocationMethodFromCandidates(
        SemanticModel semanticModel,
        ImmutableArray<IMethodSymbol> candidates,
        InvocationExpressionSyntax invocation,
        SimpleNameSyntax identifier,
        bool requireUnambiguousCandidate,
        out SymbolResolutionResult resolution)
    {
        resolution = default;

        if (candidates.IsDefaultOrEmpty)
            return false;

        var targetName = identifier.Identifier.ValueText;
        var matchingCandidates = candidates
            .Where(method => IsInvocationMethodNameMatch(method, targetName))
            .ToImmutableArray();
        var candidate = matchingCandidates.Length == 1
            ? matchingCandidates[0]
            : semanticModel.TryChooseAvailableInvocationMethodCandidate(matchingCandidates, invocation, out var availableCandidate)
                ? availableCandidate
            : SemanticModel.TryChooseInvocationMethodCandidate(
                matchingCandidates,
                invocation,
                requireUnambiguousCandidate
                    ? SemanticModel.InvocationCandidateFallback.None
                    : SemanticModel.InvocationCandidateFallback.FirstCompatibleOrSecondCandidateWhenArgumentsPresent);
        if (candidate is null)
            return false;

        var projected = ProjectCachedInvocationHoverSymbol(candidate);
        if (IsUnitTypeSymbol(projected))
            return false;

        resolution = new SymbolResolutionResult(
            SymbolResolutionKind.InvocationTarget,
            projected,
            identifier);
        return true;
    }

    private static bool IsInvocationMethodNameMatch(IMethodSymbol method, string targetName)
        => string.Equals(method.Name, targetName, StringComparison.Ordinal) ||
           method.MethodKind == MethodKind.Constructor &&
           string.Equals(method.ContainingType?.Name, targetName, StringComparison.Ordinal);

    private static bool TryResolveConstructorFromTargetType(
        InvocationExpressionSyntax invocation,
        ITypeSymbol targetType,
        [NotNullWhen(true)] out IMethodSymbol? constructor)
    {
        constructor = null;

        if (targetType is not INamedTypeSymbol namedType)
            return false;

        var invokedName = invocation.Expression switch
        {
            SimpleNameSyntax simpleName => simpleName.Identifier.ValueText,
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax simpleName } => simpleName.Identifier.ValueText,
            _ => null
        };

        if (!string.Equals(invokedName, namedType.Name, StringComparison.Ordinal))
            return false;

        return TryChooseConstructorForInvocation(namedType, invocation, out constructor);
    }

    private static bool TryChooseConstructorForInvocation(
        INamedTypeSymbol type,
        InvocationExpressionSyntax invocation,
        [NotNullWhen(true)] out IMethodSymbol? constructor)
    {
        constructor = type.Constructors.FirstOrDefault(candidate =>
            candidate.Parameters.Length == invocation.ArgumentList.Arguments.Count);
        constructor ??= type.Constructors.FirstOrDefault();
        return constructor is not null;
    }

    private static bool TryResolveInvocationTargetTypeFromTypeInfo(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        out ITypeSymbol targetType)
    {
        targetType = null!;

        var typeInfo = semanticModel.GetTypeInfo(invocation);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType is null ||
            convertedType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        if (IsTargetTypedConstructorBinding(invocation.Expression))
        {
            targetType = convertedType;
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(typeInfo.Type, convertedType))
            return false;

        var naturalUnionCase = typeInfo.Type?.TryGetUnionCase();
        var convertedUnion = convertedType.TryGetUnion();
        if (naturalUnionCase is null || convertedUnion is null)
            return false;

        targetType = convertedType;
        return true;
    }

    private static bool TryResolveInvocationTargetTypeFromArgumentContext(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        out ITypeSymbol targetType)
    {
        targetType = null!;

        if (invocation.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax outerInvocation)
        {
            return false;
        }

        var invokedName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            _ => null
        };

        var parameter = TryGetInvocationParameter(semanticModel, outerInvocation, argumentList.Arguments, argument);
        if (parameter?.Type is not { TypeKind: not TypeKind.Error } parameterType)
            return false;

        var typeInfo = semanticModel.GetTypeInfo(invocation);
        if (typeInfo.ConvertedType is { TypeKind: not TypeKind.Error } convertedType &&
            !SymbolEqualityComparer.Default.Equals(typeInfo.Type, convertedType))
        {
            targetType = convertedType;
            return true;
        }

        if (IsTargetTypedConstructorBinding(invocation.Expression))
        {
            targetType = parameterType;
            return true;
        }

        if (string.IsNullOrWhiteSpace(invokedName))
            return false;

        if (!ContainsUnionCaseNamed(parameterType, invokedName))
            return false;

        targetType = parameterType;
        return true;
    }

    private static IParameterSymbol? TryGetInvocationParameter(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol;
        var method = symbol as IMethodSymbol;
        if (method is null &&
            invocation.Expression is TypeSyntax typeSyntax &&
            semanticModel.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol namedType)
        {
            method = namedType.Constructors.FirstOrDefault();
        }
        if (method is null &&
            semanticModel.GetSymbolInfo(invocation.Expression).Symbol is INamedTypeSymbol expressionType)
        {
            method = expressionType.Constructors.FirstOrDefault();
        }

        if (method is null)
            return null;

        if (argument.NameColon?.Name.Identifier.ValueText is { Length: > 0 } argumentName)
        {
            return method.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, argumentName, StringComparison.OrdinalIgnoreCase));
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Span == argument.Span && i < method.Parameters.Length)
                return method.Parameters[i];
        }

        return null;
    }

    private static bool ContainsUnionCaseNamed(ITypeSymbol type, string caseName)
    {
        var union = type.TryGetUnion() ?? type.TryGetUnionCase()?.Union;
        return union?.Variants.Any(caseType => string.Equals(caseType.Name, caseName, StringComparison.Ordinal)) == true;
    }

    private static bool IsTargetTypedConstructorBinding(ExpressionSyntax expression)
        => expression is MemberBindingExpressionSyntax memberBinding &&
           (memberBinding.Name.IsMissing ||
            memberBinding.Name.Identifier.IsMissing ||
            string.IsNullOrEmpty(memberBinding.Name.Identifier.ValueText));

    private static bool TryResolveInvocationMethodFromCachedSymbolInfo(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        SimpleNameSyntax identifier,
        out SymbolResolutionResult resolution)
    {
        if (TryResolveInvocationMethodFromCachedSymbolInfoCore(semanticModel, invocation, invocation, identifier, out resolution))
            return true;

        if (TryResolveInvocationMethodFromCachedSymbolInfoCore(semanticModel, invocation, identifier, identifier, out resolution))
            return true;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (TryResolveInvocationMethodFromCachedSymbolInfoCore(semanticModel, invocation, memberAccess, memberAccess.Name, out resolution))
                return true;

            if (TryResolveInvocationMethodFromCachedSymbolInfoCore(semanticModel, invocation, memberAccess.Name, memberAccess.Name, out resolution))
                return true;
        }

        resolution = default;
        return false;
    }

    private static bool TryResolveInvocationMethodFromCachedSymbolInfoCore(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        SyntaxNode queryNode,
        SyntaxNode resultNode,
        out SymbolResolutionResult resolution)
    {
        resolution = default;

        if (!semanticModel.TryGetCachedSymbolInfo(queryNode, out var cachedInfo))
            return false;

        if (cachedInfo.Symbol is null && cachedInfo.CandidateSymbols.IsDefaultOrEmpty)
            return false;

        if (cachedInfo.Symbol is INamedTypeSymbol namedType &&
            TryChooseConstructorForInvocation(namedType, invocation, out var constructor))
        {
            resolution = new SymbolResolutionResult(
                SymbolResolutionKind.InvocationTarget,
                constructor,
                resultNode);
            return true;
        }

        foreach (var candidateType in cachedInfo.CandidateSymbols.OfType<INamedTypeSymbol>())
        {
            if (TryChooseConstructorForInvocation(candidateType, invocation, out var candidateConstructor))
            {
                resolution = new SymbolResolutionResult(
                    SymbolResolutionKind.InvocationTarget,
                    candidateConstructor,
                    resultNode);
                return true;
            }
        }

        var candidateMethods = cachedInfo.CandidateSymbols.OfType<IMethodSymbol>().ToImmutableArray();
        var method = cachedInfo.Symbol as IMethodSymbol
            ?? (candidateMethods.Length == 1
                ? candidateMethods[0]
                : SemanticModel.TryChooseInvocationMethodCandidate(
                candidateMethods,
                invocation,
                SemanticModel.InvocationCandidateFallback.None));
        if (method is null)
            return false;

        var projected = ProjectCachedInvocationHoverSymbol(method);
        if (IsUnitTypeSymbol(projected))
            return false;

        resolution = new SymbolResolutionResult(
            SymbolResolutionKind.InvocationTarget,
            projected,
            resultNode);
        return true;
    }

    private static bool IsUnitTypeSymbol(ISymbol symbol)
        => symbol is ITypeSymbol type &&
           (type.SpecialType == SpecialType.System_Unit ||
            string.Equals(type.Name, "Unit", StringComparison.Ordinal));

    private static ISymbol ProjectCachedInvocationHoverSymbol(IMethodSymbol method)
    {
        return method;
    }

    private static Hover? TryBuildPatternDeclarationHover(SourceText sourceText, SemanticModel semanticModel, SyntaxNode root, int offset)
    {
        var plainTypeFormat = CreatePlainTypeFormat();

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Kind == SyntaxKind.IdentifierToken &&
                token.Parent?.AncestorsAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault() is { } designation)
            {
                var declaredLocal = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, designation, out var designationInfo)
                    ? (designationInfo.Symbol ?? designationInfo.CandidateSymbols.FirstOrDefault()) as ILocalSymbol
                    : null;
                declaredLocal ??= semanticModel.GetDeclaredSymbol(designation) as ILocalSymbol;
                var binding = declaredLocal is { IsMutable: true }
                    ? "var"
                    : designation.BindingKeyword.Kind == SyntaxKind.VarKeyword ? "var" : "val";
                string? patternTypeDisplay = null;
                if (declaredLocal?.Type is { TypeKind: not TypeKind.Error } declaredLocalType)
                {
                    patternTypeDisplay = declaredLocalType.ToDisplayString(plainTypeFormat);
                }
                else
                    if (designation.GetAncestor<DeclarationPatternSyntax>() is { } declarationPattern)
                    {
                        patternTypeDisplay = TryResolveTypeSymbolFromSyntax(semanticModel, declarationPattern.Type, out var declaredType)
                            ? declaredType.ToDisplayString(plainTypeFormat)
                            : declarationPattern.Type.ToString();
                    }
                    else if (TryInferPatternDeclaredLocalType(designation, semanticModel, out var designationType))
                    {
                        patternTypeDisplay = designationType.ToDisplayString(plainTypeFormat);
                    }

                if (patternTypeDisplay is null)
                    continue;

                var designationSignature = $"{binding} {designation.Identifier.ValueText}: {patternTypeDisplay}";
                var designationHoverText = BuildHoverText(
                    designationSignature,
                    kind: "Local",
                    containing: null,
                    documentation: null,
                    capturedVariables: ImmutableArray<ISymbol>.Empty,
                    isCapturedVariable: false);

                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = designationHoverText
                    }),
                    Range = PositionHelper.ToRange(sourceText, designation.Identifier.Span)
                };
            }

            if (token.Kind != SyntaxKind.IdentifierToken ||
                token.Parent is not IdentifierNameSyntax identifierName ||
                identifierName.Parent is not ConstantPatternSyntax)
            {
                continue;
            }

            var pattern = identifierName.AncestorsAndSelf().FirstOrDefault(static n => n is PositionalPatternSyntax or SequencePatternSyntax);
            if (pattern is null)
                continue;

            var patternAssignment = identifierName.AncestorsAndSelf().OfType<PatternDeclarationAssignmentStatementSyntax>().FirstOrDefault();
            if (patternAssignment is null)
                continue;

            var inferredType = InferPatternElementType(pattern, token, patternAssignment.Right, semanticModel);
            var typeDisplay = inferredType is null ? "<Error>" : FormatType(inferredType, plainTypeFormat);
            var signature = $"{token.ValueText}: {typeDisplay}";
            var hoverText = BuildHoverText(
                signature,
                kind: "Local",
                containing: null,
                documentation: null,
                capturedVariables: ImmutableArray<ISymbol>.Empty,
                isCapturedVariable: false);

            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverText
                }),
                Range = PositionHelper.ToRange(sourceText, token.Span)
            };
        }

        return null;
    }

    private static SymbolResolutionResult? TryResolveDeclaredHoverSymbol(SemanticModel semanticModel, SyntaxNode root, int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Kind != SyntaxKind.IdentifierToken)
                continue;

            if (token.Parent is VariableDeclaratorSyntax declarator &&
                token == declarator.Identifier)
            {
                if (semanticModel.GetDeclaredSymbol(declarator) is { } declaredSymbol)
                    return new SymbolResolutionResult(SymbolResolutionKind.Declaration, declaredSymbol, declarator);
            }

            if (token.Parent is ParameterSyntax parameter &&
                token == parameter.Identifier)
            {
                var parameterSymbol = parameter.Ancestors().Any(static ancestor =>
                    ancestor is FunctionExpressionSyntax)
                        ? semanticModel.GetFunctionExpressionParameterSymbol(parameter)
                        : semanticModel.GetDeclaredSymbol(parameter) as IParameterSymbol;

                if (parameterSymbol is not null)
                    return new SymbolResolutionResult(SymbolResolutionKind.Declaration, parameterSymbol, parameter);
            }

            if (token.Parent is SingleVariableDesignationSyntax single &&
                token == single.Identifier)
            {
                var singleSymbol = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, single, out var singleInfo)
                    ? singleInfo.Symbol ?? singleInfo.CandidateSymbols.FirstOrDefault()
                    : null;
                singleSymbol ??= semanticModel.GetDeclaredSymbol(single);
                if (singleSymbol is not null)
                {
                    return new SymbolResolutionResult(SymbolResolutionKind.Declaration, singleSymbol, single);
                }
            }

            if (token.Parent is TypedVariableDesignationSyntax typed &&
                typed.Designation is SingleVariableDesignationSyntax typedSingle &&
                token == typedSingle.Identifier)
            {
                var typedSymbol = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, typedSingle, out var typedInfo)
                    ? typedInfo.Symbol ?? typedInfo.CandidateSymbols.FirstOrDefault()
                    : null;
                typedSymbol ??= semanticModel.GetDeclaredSymbol(typedSingle);
                if (typedSymbol is not null)
                {
                    return new SymbolResolutionResult(SymbolResolutionKind.Declaration, typedSymbol, typedSingle);
                }
            }
        }

        return null;
    }

    private static Hover? TryBuildFunctionExpressionDeclarationHover(
        SourceText sourceText,
        SyntaxNode root,
        int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Kind != SyntaxKind.IdentifierToken ||
                token.Parent is not FunctionExpressionSyntax functionExpression ||
                !IsFunctionExpressionIdentifierToken(functionExpression, token) ||
                IsInsideFunctionExpressionBody(functionExpression, token))
            {
                continue;
            }

            var signature = BuildFunctionExpressionSyntaxSignature(functionExpression);
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            var containing = functionExpression.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Select(static type => type.Identifier.ValueText)
                .FirstOrDefault();

            var hoverText = BuildHoverText(
                signature,
                kind: "Function",
                containing: containing,
                documentation: null,
                capturedVariables: ImmutableArray<ISymbol>.Empty,
                isCapturedVariable: false);

            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverText
                }),
                Range = PositionHelper.ToRange(sourceText, token.Span)
            };
        }

        return null;
    }

    private static Hover? TryBuildDeclarationSyntaxHover(
        SourceText sourceText,
        SyntaxNode root,
        int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (TryBuildTypeDeclarationSyntaxHover(sourceText, root, token, candidateOffset, out var typeHover))
                return typeHover;

            if (token.IsMissing || token.Kind != SyntaxKind.IdentifierToken)
                continue;

            if (TryBuildCallableDeclarationSyntaxHover(sourceText, token, out var callableHover))
                return callableHover;

            if (TryBuildUnionCaseDeclarationSyntaxHover(sourceText, token, out var unionCaseHover))
                return unionCaseHover;

            if (TryBuildUnionCaseParameterSyntaxHover(sourceText, token, out var unionCaseParameterHover))
                return unionCaseParameterHover;

            if (TryBuildCallableParameterSyntaxHover(sourceText, token, out var callableParameterHover))
                return callableParameterHover;

            if (TryBuildPrimaryConstructorParameterSyntaxHover(sourceText, token, out var parameterHover))
                return parameterHover;
        }

        return null;
    }

    private static bool ShouldSuppressSemanticHover(SyntaxNode root, int offset)
    {
        try
        {
            var token = root.FindToken(Math.Clamp(offset, 0, root.FullSpan.End));
            if (!token.IsMissing && token.Span.Contains(offset))
                return IsSemanticHoverSuppressedToken(token) ||
                       IsImportDirectiveNameToken(token) ||
                       IsInterpolatedStringNonExpressionToken(token);
        }
        catch
        {
        }

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (!token.Span.Contains(candidateOffset))
                continue;

            if (!IsSemanticHoverSuppressedToken(token) &&
                !IsImportDirectiveNameToken(token) &&
                !IsInterpolatedStringNonExpressionToken(token))
                return false;

            if (candidateOffset == offset)
                return true;
        }

        return false;
    }

    private static bool IsSemanticHoverSuppressedToken(SyntaxToken token)
    {
        if (token.Kind == SyntaxKind.DefaultKeyword &&
            token.Parent?.AncestorsAndSelf().OfType<DefaultExpressionSyntax>().Any() == true)
        {
            return false;
        }

        return IsNonSymbolKeywordHoverToken(token.Kind) || IsNonSymbolPunctuationHoverToken(token.Kind);
    }

    private static bool IsNonSymbolKeywordHoverToken(SyntaxKind kind)
        => kind is SyntaxKind.AbstractKeyword
            or SyntaxKind.AliasKeyword
            or SyntaxKind.AsyncKeyword
            or SyntaxKind.AwaitKeyword
            or SyntaxKind.BreakKeyword
            or SyntaxKind.ByKeyword
            or SyntaxKind.CaseKeyword
            or SyntaxKind.CatchKeyword
            or SyntaxKind.ClassKeyword
            or SyntaxKind.ConstKeyword
            or SyntaxKind.ContinueKeyword
            or SyntaxKind.DefaultKeyword
            or SyntaxKind.DelegateKeyword
            or SyntaxKind.ElseKeyword
            or SyntaxKind.EnumKeyword
            or SyntaxKind.EventKeyword
            or SyntaxKind.ExplicitKeyword
            or SyntaxKind.ExtensionKeyword
            or SyntaxKind.ExternKeyword
            or SyntaxKind.FieldKeyword
            or SyntaxKind.FileprivateKeyword
            or SyntaxKind.FinalKeyword
            or SyntaxKind.FinallyKeyword
            or SyntaxKind.FixedKeyword
            or SyntaxKind.ForKeyword
            or SyntaxKind.FuncKeyword
            or SyntaxKind.GetKeyword
            or SyntaxKind.GotoKeyword
            or SyntaxKind.IfKeyword
            or SyntaxKind.ImplicitKeyword
            or SyntaxKind.ImportKeyword
            or SyntaxKind.InKeyword
            or SyntaxKind.InitKeyword
            or SyntaxKind.InterfaceKeyword
            or SyntaxKind.InternalKeyword
            or SyntaxKind.IsKeyword
            or SyntaxKind.LetKeyword
            or SyntaxKind.LoopKeyword
            or SyntaxKind.MatchKeyword
            or SyntaxKind.NameOfKeyword
            or SyntaxKind.NamespaceKeyword
            or SyntaxKind.NewKeyword
            or SyntaxKind.OpenKeyword
            or SyntaxKind.OperatorKeyword
            or SyntaxKind.OutKeyword
            or SyntaxKind.OverrideKeyword
            or SyntaxKind.ParamsKeyword
            or SyntaxKind.PartialKeyword
            or SyntaxKind.PrivateKeyword
            or SyntaxKind.ProtectedKeyword
            or SyntaxKind.PublicKeyword
            or SyntaxKind.ReadonlyKeyword
            or SyntaxKind.RecordKeyword
            or SyntaxKind.RefKeyword
            or SyntaxKind.RemoveKeyword
            or SyntaxKind.RequiredKeyword
            or SyntaxKind.ReturnKeyword
            or SyntaxKind.SealedKeyword
            or SyntaxKind.SetKeyword
            or SyntaxKind.SizeOfKeyword
            or SyntaxKind.StaticKeyword
            or SyntaxKind.StructKeyword
            or SyntaxKind.ThrowKeyword
            or SyntaxKind.TryKeyword
            or SyntaxKind.TypeOfKeyword
            or SyntaxKind.UnsafeKeyword
            or SyntaxKind.UseKeyword
            or SyntaxKind.ValKeyword
            or SyntaxKind.VarKeyword
            or SyntaxKind.VirtualKeyword
            or SyntaxKind.WhenKeyword
            or SyntaxKind.WhereKeyword
            or SyntaxKind.WhileKeyword
            or SyntaxKind.WithKeyword
            or SyntaxKind.YieldKeyword;

    private static bool IsNonSymbolPunctuationHoverToken(SyntaxKind kind)
        => kind is SyntaxKind.ArrowToken
            or SyntaxKind.CloseArrayToken
            or SyntaxKind.CloseBraceToken
            or SyntaxKind.CloseBracketToken
            or SyntaxKind.CloseParenToken
            or SyntaxKind.ColonToken
            or SyntaxKind.CommaToken
            or SyntaxKind.EqualsToken
            or SyntaxKind.FatArrowToken
            or SyntaxKind.OpenArrayToken
            or SyntaxKind.OpenBraceToken
            or SyntaxKind.OpenBracketToken
            or SyntaxKind.OpenParenToken
            or SyntaxKind.QuestionToken
            or SyntaxKind.SemicolonToken
            or SyntaxKind.UnderscoreToken;

    private static bool IsImportDirectiveNameToken(SyntaxToken token)
        => token.Parent?
            .AncestorsAndSelf()
            .OfType<ImportDirectiveSyntax>()
            .Any(import => import.Name.Span.Contains(token.Span)) == true;

    private static bool IsInterpolatedStringNonExpressionToken(SyntaxToken token)
    {
        if (token.Parent is InterpolatedStringTextSyntax)
            return true;

        if (token.Parent is InterpolatedStringExpressionSyntax interpolatedString)
            return token == interpolatedString.StringStartToken ||
                   token == interpolatedString.StringEndToken;

        if (token.Parent is InterpolationSyntax interpolation)
            return token == interpolation.DollarToken ||
                   token == interpolation.OpenBraceToken ||
                   token == interpolation.CloseBraceToken;

        return false;
    }

    private static bool TryBuildTypeDeclarationSyntaxHover(
        SourceText sourceText,
        SyntaxNode root,
        SyntaxToken token,
        int offset,
        out Hover hover)
    {
        hover = null!;

        var declaration = token.Parent?
            .AncestorsAndSelf()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(candidate => token == candidate.Identifier)
            ?? root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault(candidate => candidate.Identifier.Span.Contains(offset) ||
                                             candidate.Identifier.Span.End == offset);

        if (declaration is null ||
            declaration.Identifier.IsMissing ||
            !TryBuildTypeDeclarationSyntaxSignature(sourceText, declaration, out var signature))
        {
            return false;
        }

        var containing = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(static type => type.Identifier.ValueText)
            .FirstOrDefault();

        var hoverText = BuildHoverText(
            signature,
            kind: GetTypeDeclarationSyntaxKindDisplay(declaration),
            containing: containing,
            documentation: null,
            capturedVariables: ImmutableArray<ISymbol>.Empty,
            isCapturedVariable: false);

        hover = new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = hoverText
            }),
            Range = PositionHelper.ToRange(sourceText, token.Span)
        };
        return true;
    }

    private static Hover? TryBuildKnownTypeIdentifierSyntaxHover(SourceText sourceText, SyntaxNode root, int offset)
    {
        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Kind != SyntaxKind.IdentifierToken ||
                token.Parent is not IdentifierNameSyntax identifier ||
                string.IsNullOrWhiteSpace(identifier.Identifier.ValueText))
            {
                continue;
            }

            var invocation = identifier.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is not null &&
                GetInvocationTargetIdentifier(invocation) is { } invocationTarget &&
                invocationTarget.Identifier.Span == identifier.Identifier.Span)
            {
                continue;
            }

            var declaration = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Identifier.ValueText,
                    identifier.Identifier.ValueText,
                    StringComparison.Ordinal));
            if (declaration is null ||
                !TryBuildTypeDeclarationSyntaxSignature(sourceText, declaration, out var signature))
            {
                continue;
            }

            var hoverText = BuildHoverText(
                signature,
                kind: GetTypeDeclarationSyntaxKindDisplay(declaration),
                containing: null,
                documentation: null,
                capturedVariables: ImmutableArray<ISymbol>.Empty,
                isCapturedVariable: false);

            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = hoverText
                }),
                Range = PositionHelper.ToRange(sourceText, identifier.Identifier.Span)
            };
        }

        return null;
    }

    private static bool TryBuildCallableDeclarationSyntaxHover(SourceText sourceText, SyntaxToken token, out Hover hover)
    {
        hover = null!;

        if (token.Parent is MethodDeclarationSyntax methodDeclaration &&
            token == methodDeclaration.Identifier &&
            !methodDeclaration.Identifier.IsMissing &&
            TryBuildMethodDeclarationSyntaxSignature(sourceText, methodDeclaration, out var methodSignature))
        {
            var containing = methodDeclaration.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Select(static type => $"class {type.Identifier.ValueText}")
                .FirstOrDefault();

            hover = new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = BuildHoverText(
                        methodSignature,
                        kind: "Method",
                        containing: containing,
                        documentation: null,
                        capturedVariables: ImmutableArray<ISymbol>.Empty,
                        isCapturedVariable: false)
                }),
                Range = PositionHelper.ToRange(sourceText, token.Span)
            };
            return true;
        }

        if (token.Parent is FunctionStatementSyntax functionStatement &&
            token == functionStatement.Identifier &&
            !functionStatement.Identifier.IsMissing &&
            TryBuildFunctionStatementSyntaxSignature(sourceText, functionStatement, out var functionSignature))
        {
            var containing = functionStatement.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Select(static type => type.Identifier.ValueText)
                .FirstOrDefault();

            hover = new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = BuildHoverText(
                        functionSignature,
                        kind: "Function",
                        containing: containing,
                        documentation: null,
                        capturedVariables: ImmutableArray<ISymbol>.Empty,
                        isCapturedVariable: false)
                }),
                Range = PositionHelper.ToRange(sourceText, token.Span)
            };
            return true;
        }

        return false;
    }

    private static bool TryBuildUnionCaseDeclarationSyntaxHover(SourceText sourceText, SyntaxToken token, out Hover hover)
    {
        hover = null!;

        if (token.Parent is not CaseDeclarationSyntax caseDeclaration ||
            token != caseDeclaration.Identifier ||
            caseDeclaration.Identifier.IsMissing ||
            !TryBuildUnionCaseSyntaxSignature(sourceText, caseDeclaration, out var caseSignature))
        {
            return false;
        }

        var containing = caseDeclaration.Ancestors()
            .OfType<UnionDeclarationSyntax>()
            .Select(static union => union.Identifier.ValueText)
            .FirstOrDefault();

        hover = new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = BuildHoverText(
                    caseSignature,
                    kind: "Union Case",
                    containing: containing,
                    documentation: null,
                    capturedVariables: ImmutableArray<ISymbol>.Empty,
                    isCapturedVariable: false)
            }),
            Range = PositionHelper.ToRange(sourceText, token.Span)
        };
        return true;
    }

    private static bool TryBuildUnionCaseParameterSyntaxHover(SourceText sourceText, SyntaxToken token, out Hover hover)
    {
        hover = null!;

        if (token.Parent is not ParameterSyntax parameter ||
            token != parameter.Identifier ||
            parameter.Identifier.IsMissing ||
            parameter.Parent is not ParameterListSyntax { Parent: CaseDeclarationSyntax caseDeclaration } ||
            !TryBuildUnionCaseSyntaxSignature(sourceText, caseDeclaration, out var caseSignature))
        {
            return false;
        }

        var signature = BuildCallableParameterSyntaxSignature(parameter);
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        hover = new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = BuildHoverText(
                    signature,
                    kind: "Parameter",
                    containing: caseSignature,
                    documentation: null,
                    capturedVariables: ImmutableArray<ISymbol>.Empty,
                    isCapturedVariable: false)
            }),
            Range = PositionHelper.ToRange(sourceText, token.Span)
        };
        return true;
    }

    private static bool TryBuildCallableParameterSyntaxHover(SourceText sourceText, SyntaxToken token, out Hover hover)
    {
        hover = null!;

        if (token.Parent is not ParameterSyntax parameter ||
            token != parameter.Identifier ||
            parameter.Identifier.IsMissing)
        {
            return false;
        }

        if (parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any())
            return false;

        var containing = parameter.Parent switch
        {
            ParameterListSyntax { Parent: MethodDeclarationSyntax methodDeclaration }
                when TryBuildMethodDeclarationSyntaxSignature(sourceText, methodDeclaration, out var methodSignature) => methodSignature,
            ParameterListSyntax { Parent: FunctionStatementSyntax functionStatement }
                when TryBuildFunctionStatementSyntaxSignature(sourceText, functionStatement, out var functionSignature) => functionSignature,
            ParameterListSyntax { Parent: FunctionExpressionSyntax functionExpression } => BuildFunctionExpressionSyntaxSignature(functionExpression),
            SimpleFunctionExpressionSyntax simpleFunction => BuildFunctionExpressionSyntaxSignature(simpleFunction),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(containing))
            return false;

        var signature = BuildCallableParameterSyntaxSignature(parameter);
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        hover = new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = BuildHoverText(
                    signature,
                    kind: "Parameter",
                    containing: containing,
                    documentation: null,
                    capturedVariables: ImmutableArray<ISymbol>.Empty,
                    isCapturedVariable: false)
            }),
            Range = PositionHelper.ToRange(sourceText, token.Span)
        };
        return true;
    }

    private static bool TryBuildPrimaryConstructorParameterSyntaxHover(SourceText sourceText, SyntaxToken token, out Hover hover)
    {
        hover = null!;

        if (token.Parent is not ParameterSyntax parameter ||
            token != parameter.Identifier ||
            parameter.Identifier.IsMissing ||
            parameter.Parent is not ParameterListSyntax { Parent: TypeDeclarationSyntax typeDeclaration })
        {
            return false;
        }

        var signature = BuildPrimaryConstructorParameterSyntaxSignature(parameter, typeDeclaration);
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var hoverText = BuildHoverText(
            signature,
            kind: IsPromotedPrimaryConstructorParameterSyntax(parameter, typeDeclaration) ? "Property" : "Parameter",
            containing: typeDeclaration.Identifier.ValueText,
            documentation: null,
            capturedVariables: ImmutableArray<ISymbol>.Empty,
            isCapturedVariable: false);

        hover = new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = hoverText
            }),
            Range = PositionHelper.ToRange(sourceText, token.Span)
        };
        return true;
    }

    private static string BuildCallableParameterSyntaxSignature(ParameterSyntax parameter)
    {
        var refPrefix = parameter.RefKindKeyword.Kind switch
        {
            SyntaxKind.RefKeyword => "ref ",
            SyntaxKind.OutKeyword => "out ",
            SyntaxKind.InKeyword => "in ",
            _ => string.Empty
        };
        var bindingPrefix = parameter.BindingKeyword.Kind switch
        {
            SyntaxKind.ValKeyword => "val ",
            SyntaxKind.VarKeyword => "var ",
            _ => string.Empty
        };
        var typeDisplay = parameter.TypeAnnotation?.Type.ToString() ?? "?";
        return $"{refPrefix}{bindingPrefix}{parameter.Identifier.ValueText}: {typeDisplay}";
    }

    private static bool TryBuildUnionCaseSyntaxSignature(
        SourceText sourceText,
        CaseDeclarationSyntax declaration,
        out string signature)
    {
        signature = string.Empty;

        var start = !declaration.CaseKeyword.IsMissing
            ? declaration.CaseKeyword.Span.Start
            : declaration.Identifier.Span.Start;
        var end = declaration.Identifier.Span.End;
        if (declaration.ParameterList is { } parameterList)
            end = MaxSpanEnd(end, parameterList);

        if (start < 0 || end <= start || start >= sourceText.Length)
            return false;

        end = Math.Min(end, sourceText.Length);
        signature = NormalizeHoverSignatureText(sourceText.ToString(TextSpan.FromBounds(start, end)));
        return !string.IsNullOrWhiteSpace(signature);
    }

    private static bool TryBuildMethodDeclarationSyntaxSignature(
        SourceText sourceText,
        MethodDeclarationSyntax declaration,
        out string signature)
    {
        signature = string.Empty;

        var start = GetMethodDeclarationHeaderStart(declaration);
        var end = GetCallableDeclarationHeaderEnd(declaration.Body, declaration.ExpressionBody, declaration.Span.End);
        if (start < 0 || end <= start || start >= sourceText.Length)
            return false;

        end = Math.Min(end, sourceText.Length);
        signature = NormalizeHoverSignatureText(sourceText.ToString(TextSpan.FromBounds(start, end)));
        return !string.IsNullOrWhiteSpace(signature);
    }

    private static bool TryBuildFunctionStatementSyntaxSignature(
        SourceText sourceText,
        FunctionStatementSyntax declaration,
        out string signature)
    {
        signature = string.Empty;

        var start = declaration.FuncKeyword.IsMissing
            ? declaration.Identifier.Span.Start
            : declaration.FuncKeyword.Span.Start;
        var end = GetCallableDeclarationHeaderEnd(declaration.Body, declaration.ExpressionBody, declaration.Span.End);
        if (start < 0 || end <= start || start >= sourceText.Length)
            return false;

        end = Math.Min(end, sourceText.Length);
        signature = NormalizeHoverSignatureText(sourceText.ToString(TextSpan.FromBounds(start, end)));
        return !string.IsNullOrWhiteSpace(signature);
    }

    private static int GetMethodDeclarationHeaderStart(MethodDeclarationSyntax declaration)
    {
        foreach (var modifier in declaration.Modifiers)
        {
            if (!modifier.IsMissing)
                return modifier.Span.Start;
        }

        return declaration.FuncKeyword.IsMissing
            ? declaration.Identifier.Span.Start
            : declaration.FuncKeyword.Span.Start;
    }

    private static int GetCallableDeclarationHeaderEnd(
        BlockStatementSyntax? body,
        ArrowExpressionClauseSyntax? expressionBody,
        int fallbackEnd)
    {
        if (body is not null && !body.OpenBraceToken.IsMissing)
            return body.OpenBraceToken.Span.Start;

        if (expressionBody is not null && !expressionBody.ArrowToken.IsMissing)
            return expressionBody.ArrowToken.Span.Start;

        return fallbackEnd;
    }

    private static bool TryBuildTypeDeclarationSyntaxSignature(
        SourceText _,
        BaseTypeDeclarationSyntax declaration,
        out string signature)
    {
        var modifiers = declaration.Modifiers
            .Where(static modifier => !modifier.IsMissing && !IsTypeAccessibilityModifier(modifier.Kind))
            .Select(static modifier => modifier.Text);
        var modifierPrefix = string.Join(" ", modifiers);
        if (modifierPrefix.Length > 0)
            modifierPrefix += " ";

        var kind = declaration switch
        {
            RecordDeclarationSyntax record => "record" + FormatOptionalTypeShape(record.ClassOrStructKeyword),
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            UnionDeclarationSyntax union => "union" + FormatOptionalTypeShape(union.ClassOrStructKeyword),
            EnumDeclarationSyntax => "enum",
            _ => string.Empty
        };
        if (kind.Length == 0 || declaration.Identifier.IsMissing)
        {
            signature = string.Empty;
            return false;
        }

        var typeParameters = declaration switch
        {
            TypeDeclarationSyntax typeDeclaration => GetTypeDeclarationTypeParameterList(typeDeclaration)?.ToString(),
            UnionDeclarationSyntax unionDeclaration => unionDeclaration.TypeParameterList?.ToString(),
            _ => null
        };
        signature = $"{modifierPrefix}{kind} {declaration.Identifier.ValueText}{typeParameters}";
        return true;
    }

    private static string FormatOptionalTypeShape(SyntaxToken token)
        => token.IsMissing || token.Kind == SyntaxKind.None ? string.Empty : $" {token.Text}";

    private static bool IsTypeAccessibilityModifier(SyntaxKind kind)
        => kind is SyntaxKind.PublicKeyword
            or SyntaxKind.InternalKeyword
            or SyntaxKind.PrivateKeyword
            or SyntaxKind.ProtectedKeyword
            or SyntaxKind.FileprivateKeyword;

    private static TypeParameterListSyntax? GetTypeDeclarationTypeParameterList(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDeclaration => classDeclaration.TypeParameterList,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.TypeParameterList,
            StructDeclarationSyntax structDeclaration => structDeclaration.TypeParameterList,
            InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.TypeParameterList,
            _ => null
        };

    private static string NormalizeHoverSignatureText(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var trimmedLines = lines
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0);

        return string.Join("\n", trimmedLines).Trim();
    }

    private static int MaxSpanEnd(int current, SyntaxNode? node)
        => node is null || node.IsMissing ? current : Math.Max(current, node.Span.End);

    private static string GetTypeDeclarationSyntaxKindDisplay(BaseTypeDeclarationSyntax declaration)
        => declaration switch
        {
            RecordDeclarationSyntax => "Record",
            ClassDeclarationSyntax => "Class",
            StructDeclarationSyntax => "Struct",
            InterfaceDeclarationSyntax => "Interface",
            UnionDeclarationSyntax => "Union",
            EnumDeclarationSyntax => "Enum",
            _ => "Type"
        };

    private static string BuildPrimaryConstructorParameterSyntaxSignature(
        ParameterSyntax parameter,
        TypeDeclarationSyntax typeDeclaration)
    {
        var refPrefix = parameter.RefKindKeyword.Kind switch
        {
            SyntaxKind.RefKeyword => "ref ",
            SyntaxKind.OutKeyword => "out ",
            SyntaxKind.InKeyword => "in ",
            _ => string.Empty
        };
        var bindingPrefix = GetPrimaryConstructorParameterSyntaxBindingPrefix(parameter, typeDeclaration);
        var typeDisplay = parameter.TypeAnnotation?.Type.ToString() ?? "?";
        return $"{refPrefix}{bindingPrefix}{parameter.Identifier.ValueText}: {typeDisplay}";
    }

    private static string GetPrimaryConstructorParameterSyntaxBindingPrefix(
        ParameterSyntax parameter,
        TypeDeclarationSyntax typeDeclaration)
    {
        return parameter.BindingKeyword.Kind switch
        {
            SyntaxKind.ValKeyword => "val ",
            SyntaxKind.VarKeyword => "var ",
            _ when typeDeclaration is RecordDeclarationSyntax => "val ",
            _ => string.Empty
        };
    }

    private static bool IsPromotedPrimaryConstructorParameterSyntax(
        ParameterSyntax parameter,
        TypeDeclarationSyntax typeDeclaration)
    {
        var refKeywordKind = parameter.RefKindKeyword.Kind;
        var typeIsByRef = parameter.TypeAnnotation?.Type is ByRefTypeSyntax;
        if (refKeywordKind is not SyntaxKind.None || typeIsByRef)
            return false;

        return typeDeclaration is RecordDeclarationSyntax ||
               parameter.BindingKeyword.Kind is SyntaxKind.ValKeyword or SyntaxKind.VarKeyword;
    }

    private static string BuildFunctionExpressionSyntaxSignature(FunctionExpressionSyntax functionExpression)
    {
        static string FormatParameter(ParameterSyntax parameter)
        {
            var parameterName = parameter.Identifier.ValueText;
            if (parameter.TypeAnnotation?.Type is not { } typeSyntax)
                return parameterName;

            return $"{parameterName}: {typeSyntax}";
        }

        var isStatic = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple => simple.StaticKeyword.Kind == SyntaxKind.StaticKeyword,
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.StaticKeyword.Kind == SyntaxKind.StaticKeyword,
            _ => false
        };

        var name = functionExpression switch
        {
            ParenthesizedFunctionExpressionSyntax parenthesized when parenthesized.Identifier.Kind == SyntaxKind.IdentifierToken
                => parenthesized.Identifier.ValueText,
            _ => null
        };

        var parameters = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple when simple.Parameter is not null
                => [FormatParameter(simple.Parameter)],
            ParenthesizedFunctionExpressionSyntax parenthesized when parenthesized.ParameterList is not null
                => parenthesized.ParameterList.Parameters.Select(FormatParameter),
            _ => Enumerable.Empty<string>()
        };

        var returnTypeSyntax = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple => simple.ReturnType?.Type,
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ReturnType?.Type,
            _ => null
        };

        var returnTypeDisplay = returnTypeSyntax is null
            ? "?"
            : returnTypeSyntax.ToString();

        var staticPrefix = isStatic ? "static " : string.Empty;
        var namePrefix = string.IsNullOrWhiteSpace(name) ? string.Empty : $"func {name}";
        var parameterList = string.Join(", ", parameters);
        return string.IsNullOrEmpty(namePrefix)
            ? $"{staticPrefix}({parameterList}) -> {returnTypeDisplay}"
            : $"{staticPrefix}{namePrefix}({parameterList}) -> {returnTypeDisplay}";
    }

    private static TextSpan GetHoverSpanForResolution(SymbolResolutionResult resolution)
    {
        if (resolution.SourceSpan is { } sourceSpan)
            return sourceSpan;

        var node = resolution.Node;

        if (node is not IdentifierNameSyntax and
            not SingleVariableDesignationSyntax and
            not VariableDeclaratorSyntax and
            not ParameterSyntax &&
            TryGetDeclaredIdentifierSpan(resolution.Symbol, out var declaredSpan))
        {
            return declaredSpan;
        }

        return node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Span,
            SingleVariableDesignationSyntax designation => designation.Identifier.Span,
            VariableDeclaratorSyntax declarator => declarator.Identifier.Span,
            ParameterSyntax parameter when !parameter.Identifier.IsMissing => parameter.Identifier.Span,
            MethodDeclarationSyntax method when !method.Identifier.IsMissing => method.Identifier.Span,
            FunctionStatementSyntax function when !function.Identifier.IsMissing => function.Identifier.Span,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax identifier } => identifier.Identifier.Span,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } when !memberAccess.Name.Identifier.IsMissing
                => memberAccess.Name.Identifier.Span,
            MemberAccessExpressionSyntax memberAccess when !memberAccess.Name.Identifier.IsMissing => memberAccess.Name.Identifier.Span,
            _ => node.Span
        };
    }

    private static bool TryGetDeclaredIdentifierSpan(ISymbol symbol, out TextSpan span)
    {
        foreach (var syntax in symbol.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()))
        {
            switch (syntax)
            {
                case VariableDeclaratorSyntax declarator when !declarator.Identifier.IsMissing:
                    span = declarator.Identifier.Span;
                    return true;
                case SingleVariableDesignationSyntax designation when !designation.Identifier.IsMissing:
                    span = designation.Identifier.Span;
                    return true;
                case ParameterSyntax parameter when !parameter.Identifier.IsMissing:
                    span = parameter.Identifier.Span;
                    return true;
                case MethodDeclarationSyntax method when !method.Identifier.IsMissing:
                    span = method.Identifier.Span;
                    return true;
                case FunctionStatementSyntax function when !function.Identifier.IsMissing:
                    span = function.Identifier.Span;
                    return true;
                case LabeledStatementSyntax labeledStatement when !labeledStatement.Identifier.IsMissing:
                    span = labeledStatement.Identifier.Span;
                    return true;
            }
        }

        span = default;
        return false;
    }

    private static ITypeSymbol? InferPatternElementType(
        SyntaxNode pattern,
        SyntaxToken token,
        ExpressionSyntax right,
        SemanticModel semanticModel)
    {
        var rightTypeInfo = semanticModel.GetTypeInfo(right);
        var rightType = rightTypeInfo.Type ?? rightTypeInfo.ConvertedType;
        if (rightType is null)
            return null;

        if (pattern is PositionalPatternSyntax positional)
        {
            var index = -1;
            for (var i = 0; i < positional.Elements.Count; i++)
            {
                if (!positional.Elements[i].Span.Contains(token.Span))
                    continue;

                index = i;
                break;
            }

            if (index >= 0 && rightType is ITupleTypeSymbol tupleType && index < tupleType.TupleElements.Length)
                return tupleType.TupleElements[index].Type;

            return null;
        }

        if (pattern is SequencePatternSyntax sequence)
        {
            var element = sequence.Elements.FirstOrDefault(e => e.Span.Contains(token.Span));
            if (element is null)
                return null;

            if (!TryGetSequencePatternElementType(rightType, semanticModel, out var sequenceElementType))
                return null;

            var elementIndex = FindSequencePatternElementIndex(sequence, element);
            return GetSequencePatternElementValueType(sequence, elementIndex, rightType, sequenceElementType, semanticModel);
        }

        return null;
    }

    private static IEnumerable<int> NormalizeOffsets(int offset, int maxOffset)
    {
        if (maxOffset < 0)
            yield break;

        var clamped = Math.Clamp(offset, 0, maxOffset);
        yield return clamped;

        if (clamped < maxOffset)
            yield return clamped + 1;

        if (clamped > 0)
            yield return clamped - 1;
    }

    private static string BuildSignature(ISymbol symbol, SyntaxNode contextNode, SemanticModel semanticModel)
    {
        var plainTypeFormat = CreatePlainTypeFormat();

        if (symbol is INamespaceSymbol namespaceSymbol)
            return $"namespace {FormatNamespaceDisplay(namespaceSymbol)}";

        if (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod } lambda)
        {
            var parameters = FormatParameters(lambda.Parameters, plainTypeFormat);
            var returnType = FormatType(lambda.ReturnType, plainTypeFormat);
            return $"({parameters}) -> {returnType}";
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            var containingType = constructor.ContainingType;
            var parameters = FormatParameters(constructor.Parameters, plainTypeFormat);

            if (containingType?.IsUnion == true)
            {
                var declarationTypeFormat = CreatePlainTypeFormat()
                    .WithKindOptions(SymbolDisplayKindOptions.IncludeTypeKeyword)
                    .WithMiscellaneousOptions(
                        CreatePlainTypeFormat().MiscellaneousOptions |
                        SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes);
                var unionDisplay = FormatType(containingType, declarationTypeFormat);
                return $"{unionDisplay}({parameters})";
            }

            var constructorName = containingType?.Name ?? constructor.Name;
            var typeParams = containingType is not null && !containingType.TypeParameters.IsDefaultOrEmpty
                ? !containingType.TypeArguments.IsDefaultOrEmpty &&
                  containingType.TypeArguments.Length == containingType.TypeParameters.Length &&
                  containingType.TypeArguments.Any(static argument => argument is not ITypeParameterSymbol)
                    ? $"<{string.Join(", ", containingType.TypeArguments.Select(argument => FormatType(argument, plainTypeFormat)))}>"
                    : $"<{string.Join(", ", containingType.TypeParameters.Select(static tp => tp.Name))}>"
                : string.Empty;
            return $"{constructorName}{typeParams}({parameters})";
        }

        if (symbol is IMethodSymbol method)
        {
            method = GetPreferredMethodForDisplay(method);

            if (MethodSignatureContainsError(method) &&
                TryBuildMethodSyntaxSignature(method, out var syntaxSignature))
            {
                return syntaxSignature;
            }

            var parameters = FormatParameters(
                GetDisplayParametersForMethod(method, contextNode, semanticModel),
                plainTypeFormat);
            var returnType = FormatType(method.ReturnType, plainTypeFormat);
            // Use concrete type arguments when available (inferred at a call site),
            // otherwise fall back to type parameter names for the generic definition.
            var typeParameters = method.TypeParameters.IsDefaultOrEmpty
                ? string.Empty
                : !method.TypeArguments.IsDefaultOrEmpty &&
                  method.TypeArguments.Length == method.TypeParameters.Length &&
                  method.TypeArguments.Any(static a => a is not ITypeParameterSymbol)
                    ? $"<{string.Join(", ", method.TypeArguments.Select(a => FormatType(a, plainTypeFormat)))}>"
                    : $"<{string.Join(", ", method.TypeParameters.Select(static tp => tp.Name))}>";
            var isExtensionAsInstance = IsExtensionMethodAccessedAsInstance(method, contextNode, semanticModel);
            var staticPrefix = !isExtensionAsInstance &&
                               (IsLocalFunctionDeclaredStatic(method) || (!IsFunctionStatementSymbol(method) && method.IsStatic))
                ? "static "
                : string.Empty;
            var accessibilityPrefix = IsFunctionStatementSymbol(method)
                ? string.Empty
                : GetNonPublicAccessibilityPrefix(method);
            return $"{accessibilityPrefix}{staticPrefix}func {method.Name}{typeParameters}({parameters}) -> {returnType}";
        }

        if (symbol is IEventSymbol ev)
        {
            var eventType = ev.Type.ContainsErrorType() &&
                TryGetMemberDeclaredTypeSyntaxDisplay(ev, out var declaredTypeDisplay)
                    ? declaredTypeDisplay
                    : FormatType(ev.Type, plainTypeFormat);
            var accessibilityPrefix = GetNonPublicAccessibilityPrefix(ev);
            return $"{accessibilityPrefix}event {ev.Name}: {eventType}";
        }

        if (symbol is IPropertySymbol { IsIndexer: false } property &&
            property.Type.ContainsErrorType() &&
            TryGetMemberDeclaredTypeSyntaxDisplay(property, out var propertyTypeDisplay))
        {
            var accessibilityPrefix = GetNonPublicAccessibilityPrefix(property);
            var staticPrefix = property.IsStatic ? "static " : string.Empty;
            var binding = property.IsMutable ? "var" : "val";
            return $"{accessibilityPrefix}{staticPrefix}{binding} {property.Name}: {propertyTypeDisplay}";
        }

        if (symbol is IFieldSymbol field &&
            field.Type.ContainsErrorType() &&
            TryGetMemberDeclaredTypeSyntaxDisplay(field, out var fieldTypeDisplay))
        {
            var accessibilityPrefix = GetNonPublicAccessibilityPrefix(field);
            var staticPrefix = field.IsStatic ? "static " : string.Empty;
            var constPrefix = field.IsConst ? "const " : string.Empty;
            return $"{accessibilityPrefix}{staticPrefix}{constPrefix}field {field.Name}: {fieldTypeDisplay}";
        }

        if (symbol is IParameterSymbol parameter)
        {
            var parameterTypeSymbol = parameter.Type;
            if (TryGetNarrowedHoverType(parameterTypeSymbol, contextNode, semanticModel, out var narrowedParameterType))
                parameterTypeSymbol = narrowedParameterType;

            if (parameterTypeSymbol.ContainsErrorType() &&
                contextNode.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault() is { } parameterSyntax &&
                semanticModel.GetFunctionExpressionParameterSymbol(parameterSyntax) is { Type: { } contextualParameterType } &&
                !contextualParameterType.ContainsErrorType())
            {
                parameterTypeSymbol = contextualParameterType;
            }
            else if (parameterTypeSymbol.ContainsErrorType() &&
                     TryInferLambdaParameterTypeFromFunctionTarget(contextNode, semanticModel, out var targetedParameterType))
            {
                parameterTypeSymbol = targetedParameterType;
            }
            else if (parameterTypeSymbol.ContainsErrorType() &&
                TryInferDeclaredTypeFromContext(contextNode, semanticModel, out var declaredParameterType))
            {
                parameterTypeSymbol = declaredParameterType;
            }
            else if (parameterTypeSymbol.ContainsErrorType() &&
                     TryInferParameterDeclaredType(parameter, semanticModel, out var declaredParameterSyntaxType))
            {
                parameterTypeSymbol = declaredParameterSyntaxType;
            }
            else if (parameterTypeSymbol.ContainsErrorType() &&
                TryInferLambdaParameterTypeFromContext(parameter, contextNode, semanticModel, out var inferredParameterType))
            {
                parameterTypeSymbol = inferredParameterType;
            }
            else if (parameterTypeSymbol.ContainsErrorType() &&
                     TryInferReceiverTypeFromMemberAccessContext(parameter.Name, contextNode, semanticModel, out inferredParameterType))
            {
                parameterTypeSymbol = inferredParameterType;
            }

            var parameterType = parameterTypeSymbol.ContainsErrorType() &&
                TryGetParameterDeclaredTypeSyntaxDisplay(parameter, out var declaredTypeDisplay)
                    ? declaredTypeDisplay
                    : FormatType(parameterTypeSymbol, plainTypeFormat);
            var accessibilityPrefix = GetNonPublicParameterAccessibilityPrefix(parameter);
            var promotedBindingPrefix = GetPromotedPrimaryConstructorBindingPrefix(parameter);
            var defaultValue = FormatParameterDefaultValue(parameter, plainTypeFormat);
            return $"{accessibilityPrefix}{promotedBindingPrefix}{parameter.Name}: {parameterType}{defaultValue}";
        }

        if (symbol is ILocalSymbol local)
        {
            var binding = local.IsMutable ? "var" : "val";
            var localTypeSymbol = local.Type;
            if (TryGetNarrowedHoverType(localTypeSymbol, contextNode, semanticModel, out var narrowedLocalType))
                localTypeSymbol = narrowedLocalType;

            if (localTypeSymbol.ContainsErrorType() &&
                TryInferPatternDeclaredLocalType(
                    contextNode.AncestorsAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault()
                    ?? local.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).OfType<SingleVariableDesignationSyntax>().FirstOrDefault(),
                    semanticModel,
                    out var patternLocalType))
            {
                localTypeSymbol = patternLocalType;
            }

            if (localTypeSymbol.ContainsErrorType() &&
                TryInferDeclaredTypeFromContext(contextNode, semanticModel, out var declaredLocalType))
            {
                localTypeSymbol = declaredLocalType;
            }

            if (localTypeSymbol.ContainsErrorType() &&
                TryInferReceiverTypeFromMemberAccessContext(local.Name, contextNode, semanticModel, out var inferredLocalType))
            {
                localTypeSymbol = inferredLocalType;
            }

            if (localTypeSymbol.SpecialType == SpecialType.System_Unit &&
                TryInferLocalInitializerType(local, semanticModel, out var initializerType))
            {
                localTypeSymbol = initializerType;
            }

            var localType = localTypeSymbol.ContainsErrorType() &&
                TryGetLocalDeclaredTypeSyntaxDisplay(local, out var declaredTypeDisplay)
                    ? declaredTypeDisplay
                    : FormatType(localTypeSymbol, plainTypeFormat);
            return $"{binding} {local.Name}: {localType}";
        }

        if (symbol is IUnionCaseTypeSymbol { IsUnionCase: true } unionCase)
        {
            var parameters = FormatParameters(unionCase.ConstructorParameters, plainTypeFormat);
            return $"{unionCase.Name}({parameters})";
        }

        if (symbol is ITypeSymbol typeSymbol)
        {
            if (contextNode is TypeSyntax contextTypeSyntax)
            {
                var contextTypeInfo = semanticModel.TryGetAvailableTypeInfo(contextTypeSyntax, out var availableContextTypeInfo)
                    ? availableContextTypeInfo
                    : semanticModel.GetTypeInfo(contextTypeSyntax);
                var contextType = contextTypeInfo.Type ?? contextTypeInfo.ConvertedType;
                if (contextType is not null && !contextType.ContainsErrorType())
                    typeSymbol = contextType;
            }
            else if (typeSymbol.ContainsErrorType() &&
                     TryInferDeclaredTypeFromContext(contextNode, semanticModel, out var contextType))
            {
                typeSymbol = contextType;
            }

            var declarationTypeFormat = CreatePlainTypeFormat()
                .WithMiscellaneousOptions(
                    CreatePlainTypeFormat().MiscellaneousOptions |
                    SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes);

            if (contextNode is FunctionTypeSyntax functionTypeSyntax &&
                TryFormatFunctionTypeSyntaxSignature(functionTypeSyntax, semanticModel, declarationTypeFormat, out var functionTypeSignature))
            {
                return functionTypeSignature;
            }

            var typeFormat = declarationTypeFormat.WithKindOptions(SymbolDisplayKindOptions.IncludeTypeKeyword);

            if (typeSymbol is ITupleTypeSymbol tupleType)
            {
                var tupleText = FormatTupleNominalType(tupleType, typeFormat);
                return AppendBaseTypeList(tupleText, tupleType, declarationTypeFormat);
            }

            if (typeSymbol is INamedTypeSymbol delegateType &&
                delegateType.TypeKind == TypeKind.Delegate)
            {
                if (TryFormatDelegateTypeSignature(delegateType, declarationTypeFormat, out var delegateSignature))
                    return delegateSignature;

                return FormatType(delegateType, declarationTypeFormat);
            }

            var text = typeSymbol is INamedTypeSymbol { Arity: > 0 } genericNamedType
                ? BuildGenericNamedTypeSignature(genericNamedType, contextNode, semanticModel, typeFormat, declarationTypeFormat)
                : FormatType(typeSymbol, typeFormat);

            if (typeSymbol is INamedTypeSymbol namedType)
                text = AppendBaseTypeList(text, namedType, declarationTypeFormat);

            return text;
        }

        return symbol.ToDisplayString(SymbolDisplayFormat.RavenTooltipFormat);
    }

    private static bool TryGetNarrowedHoverType(
        ITypeSymbol declaredType,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        [NotNullWhen(true)] out ITypeSymbol? narrowedType)
    {
        narrowedType = null;
        if (!declaredType.IsNullable || contextNode is not ExpressionSyntax expression)
            return false;

        var typeInfo = semanticModel.GetTypeInfo(expression);
        var contextualType = typeInfo.Type ?? typeInfo.ConvertedType;
        if (contextualType is null || contextualType.IsNullable || contextualType.TypeKind == TypeKind.Error)
            return false;

        narrowedType = contextualType;
        return true;
    }

    private static bool TryInferLocalInitializerType(
        ILocalSymbol local,
        SemanticModel semanticModel,
        [NotNullWhen(true)] out ITypeSymbol? initializerType)
    {
        initializerType = null;

        foreach (var syntaxReference in local.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                continue;

            if (initializer is InvocationExpressionSyntax invocation &&
                (TryGetInvocationReturnType(semanticModel.GetSymbolInfo(invocation.Expression), out var invocationReturnType) ||
                 TryGetInvocationReturnType(semanticModel.GetSymbolInfo(invocation), out invocationReturnType)))
            {
                initializerType = invocationReturnType;
                return true;
            }

            var type = semanticModel.GetTypeInfo(initializer).Type;
            if (!IsUsableInferredHoverType(type))
                continue;

            initializerType = type;
            return true;
        }

        return false;
    }

    private static bool TryGetInvocationReturnType(
        SymbolInfo symbolInfo,
        [NotNullWhen(true)] out ITypeSymbol? returnType)
    {
        returnType = null;

        foreach (var method in EnumerateMethodSymbols(symbolInfo))
        {
            if (!IsUsableInferredHoverType(method.ReturnType))
                continue;

            returnType = method.ReturnType;
            return true;
        }

        return false;
    }

    private static IEnumerable<IMethodSymbol> EnumerateMethodSymbols(SymbolInfo symbolInfo)
    {
        if (symbolInfo.Symbol is IMethodSymbol method)
            yield return method;

        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod &&
                !SymbolEqualityComparer.Default.Equals(candidateMethod, symbolInfo.Symbol))
            {
                yield return candidateMethod;
            }
        }
    }

    private static bool IsUsableInferredHoverType([NotNullWhen(true)] ITypeSymbol? type)
        => type is not null &&
           !type.ContainsErrorType() &&
           type.SpecialType != SpecialType.System_Unit;

    private static string BuildGenericNamedTypeSignature(
        INamedTypeSymbol type,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        SymbolDisplayFormat typeFormat,
        SymbolDisplayFormat argumentFormat)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct when type.IsUnion => "union struct",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Class when type.IsUnion => "union class",
            TypeKind.Class => "class",
            _ => null
        };

        var typeArguments = GetTypeParameterOrArgumentDisplay(type, contextNode, semanticModel, argumentFormat);
        var display = $"{type.Name}{typeArguments}";
        return typeFormat.KindOptions.HasFlag(SymbolDisplayKindOptions.IncludeTypeKeyword) &&
               !string.IsNullOrEmpty(kind)
            ? $"{kind} {display}"
            : display;
    }

    private static string GetTypeParameterOrArgumentDisplay(
        INamedTypeSymbol type,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        SymbolDisplayFormat argumentFormat)
    {
        if (type.Arity <= 0)
            return string.Empty;

        IEnumerable<string> arguments;
        if (TryGetConstructedTypeArgumentDisplay(type, out var constructedArguments))
        {
            arguments = constructedArguments;
        }
        else if (!type.TypeArguments.IsDefaultOrEmpty &&
            type.TypeArguments.Length == type.TypeParameters.Length &&
            type.TypeArguments.Any(static argument => argument is not ITypeParameterSymbol && argument.TypeKind != TypeKind.Error))
        {
            var offset = type.TypeArguments.Length - type.Arity;
            arguments = type.TypeArguments
                .Skip(offset)
                .Take(type.Arity)
                .Select(argument => FormatType(argument, argumentFormat));
        }
        else if (TryGetExplicitTypeArgumentDisplay(type, contextNode, semanticModel, argumentFormat, out var explicitArguments))
        {
            arguments = explicitArguments;
        }
        else
        {
            arguments = type.TypeParameters
                .TakeLast(type.Arity)
                .Select(static parameter => parameter.Name);
        }

        return $"<{string.Join(", ", arguments)}>";
    }

    private static bool TryGetConstructedTypeArgumentDisplay(
        INamedTypeSymbol type,
        out IEnumerable<string> arguments)
    {
        arguments = [];

        if (type.OriginalDefinition is null)
            return false;

        var displayFormat = CreatePlainTypeFormat();
        var display = FormatType(type, displayFormat);
        var definitionDisplay = FormatType(type.OriginalDefinition, displayFormat);
        if (string.Equals(display, definitionDisplay, StringComparison.Ordinal))
            return false;

        var open = display.IndexOf('<', StringComparison.Ordinal);
        var close = display.LastIndexOf('>');
        if (open < 0 || close <= open)
            return false;

        arguments = [display[(open + 1)..close]];
        return true;
    }

    private static bool TryGetExplicitTypeArgumentDisplay(
        INamedTypeSymbol type,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        SymbolDisplayFormat argumentFormat,
        out IEnumerable<string> arguments)
    {
        arguments = [];

        if (contextNode is GenericNameSyntax genericName &&
            genericName.TypeArgumentList.Arguments.Count == type.Arity)
        {
            arguments = genericName.TypeArgumentList.Arguments
                .Select(argument => FormatTypeSyntaxForSignature(argument.Type, semanticModel, argumentFormat))
                .ToArray();
            return true;
        }

        if (contextNode is UnionTypeSyntax unionType &&
            type.Name == "Union" &&
            unionType.Types.Count == type.Arity)
        {
            arguments = unionType.Types
                .Select(member => FormatTypeSyntaxForSignature(member, semanticModel, argumentFormat))
                .ToArray();
            return true;
        }

        return false;
    }

    private static string FormatTypeSyntaxForSignature(
        TypeSyntax typeSyntax,
        SemanticModel semanticModel,
        SymbolDisplayFormat argumentFormat)
    {
        var typeInfo = semanticModel.TryGetAvailableTypeInfo(typeSyntax, out var availableTypeInfo)
            ? availableTypeInfo
            : semanticModel.GetTypeInfo(typeSyntax);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        return type is not null && type.TypeKind != TypeKind.Error
            ? FormatType(type, argumentFormat)
            : typeSyntax.ToString();
    }

    private static IMethodSymbol GetPreferredMethodForDisplay(IMethodSymbol method)
    {
        if (!MethodSignatureContainsError(method))
            return method;

        if (method.OriginalDefinition is IMethodSymbol originalDefinition &&
            !ReferenceEquals(originalDefinition, method) &&
            !MethodSignatureContainsError(originalDefinition))
        {
            return originalDefinition;
        }

        return method;
    }

    private static bool MethodSignatureContainsError(IMethodSymbol method)
        => method.ReturnType.ContainsErrorType() ||
           method.Parameters.Any(static parameter => parameter.Type.ContainsErrorType());

    private static bool TryBuildMethodSyntaxSignature(IMethodSymbol method, out string signature)
    {
        signature = string.Empty;

        foreach (var syntax in method.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()))
        {
            switch (syntax)
            {
                case MethodDeclarationSyntax declaration:
                    signature = BuildMethodSyntaxSignature(
                        method,
                        declaration.Identifier.ValueText,
                        declaration.TypeParameterList?.ToString() ?? string.Empty,
                        declaration.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.StaticKeyword),
                        GetNonPublicAccessibilityPrefix(method),
                        declaration.ParameterList?.Parameters ?? [],
                        GetMethodReturnTypeDisplay(declaration.ReturnType));
                    return true;
                case FunctionStatementSyntax function:
                    signature = BuildMethodSyntaxSignature(
                        method,
                        function.Identifier.ValueText,
                        function.TypeParameterList?.ToString() ?? string.Empty,
                        function.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.StaticKeyword),
                        string.Empty,
                        function.ParameterList?.Parameters ?? [],
                        GetMethodReturnTypeDisplay(function.ReturnType));
                    return true;
            }
        }

        return false;
    }

    private static string BuildMethodSyntaxSignature(
        IMethodSymbol method,
        string declaredName,
        string typeParameterDisplay,
        bool isStatic,
        string accessibilityPrefix,
        IEnumerable<ParameterSyntax> parameters,
        string returnType)
    {
        var parameterDisplay = string.Join(", ", parameters.Select(FormatParameterSyntaxForMethodSignature));
        var staticPrefix = isStatic ? "static " : string.Empty;
        var methodName = string.IsNullOrWhiteSpace(declaredName) ? method.Name : declaredName;
        return $"{accessibilityPrefix}{staticPrefix}func {methodName}{typeParameterDisplay}({parameterDisplay}) -> {returnType}";
    }

    private static string FormatParameterSyntaxForMethodSignature(ParameterSyntax parameter)
    {
        var refPrefix = parameter.RefKindKeyword.Kind switch
        {
            SyntaxKind.RefKeyword => "ref ",
            SyntaxKind.OutKeyword => "out ",
            SyntaxKind.InKeyword => "in ",
            _ => string.Empty
        };
        var typeDisplay = parameter.TypeAnnotation?.Type.ToString() ?? "?";
        return $"{refPrefix}{parameter.Identifier.ValueText}: {typeDisplay}";
    }

    private static string GetMethodReturnTypeDisplay(ArrowTypeClauseSyntax? returnType)
        => returnType?.Type.ToString() ?? "unit";

    private static bool TryInferPatternDeclaredLocalType(
        SingleVariableDesignationSyntax? designation,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        if (designation is null)
            return false;

        if (designation.GetAncestor<DeclarationPatternSyntax>() is { } declarationPattern &&
            TryResolveTypeSymbolFromSyntax(semanticModel, declarationPattern.Type, out var declaredType))
        {
            inferredType = declaredType;
            return true;
        }

        if (designation.GetAncestor<SequencePatternElementSyntax>() is { } sequenceElement &&
            designation.GetAncestor<SequencePatternSyntax>() is { } sequencePattern &&
            TryGetPatternInputType(semanticModel, sequencePattern, out var sequenceInputType) &&
            TryGetSequencePatternElementType(sequenceInputType, semanticModel, out var sequenceElementType))
        {
            var elementIndex = FindSequencePatternElementIndex(sequencePattern, sequenceElement);
            inferredType = GetSequencePatternElementValueType(
                sequencePattern,
                elementIndex,
                sequenceInputType,
                sequenceElementType,
                semanticModel);
            return true;
        }

        if (designation.GetAncestor<PositionalPatternSyntax>() is { } positionalPattern &&
            TryGetPatternInputType(semanticModel, positionalPattern, out var positionalInputType))
        {
            var tupleElementTypes = GetTupleElementTypes(positionalInputType);
            for (var i = 0; i < positionalPattern.Elements.Count && i < tupleElementTypes.Length; i++)
            {
                if (!positionalPattern.Elements[i].Span.Contains(designation.Span))
                    continue;

                inferredType = tupleElementTypes[i];
                return true;
            }
        }

        return false;
    }

    private static string BuildSignatureForResolvedHover(
        SymbolResolutionResult resolution,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        var symbol = resolution.Symbol;
        var contextNode = resolution.Node;

        if (resolution.Kind is not SymbolResolutionKind.MacroFragment &&
            contextNode.AncestorsAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault() is { } single &&
            semanticModel.GetDeclaredSymbol(single) is { } declaredSymbol)
        {
            symbol = declaredSymbol;
        }

        if (resolution.Kind is SymbolResolutionKind.Declaration or SymbolResolutionKind.PatternLocal or SymbolResolutionKind.BlockLocal &&
            symbol is ILocalSymbol declarationLocal &&
            TryBuildPatternDeclarationSignatureOverride(declarationLocal, root, offset, semanticModel, out var patternDeclarationSignature))
        {
            return patternDeclarationSignature;
        }

        if (resolution.Kind is SymbolResolutionKind.Declaration or SymbolResolutionKind.PatternLocal or SymbolResolutionKind.BlockLocal &&
            symbol is ILocalSymbol local &&
            local.Type.ContainsErrorType() &&
            TryInferPatternDeclaredLocalTypeAtOffset(root, offset, semanticModel, out var localTypeAtOffset))
        {
            var plainTypeFormat = CreatePlainTypeFormat();
            var binding = local.IsMutable ? "var" : "val";
            return $"{binding} {local.Name}: {FormatType(localTypeAtOffset, plainTypeFormat)}";
        }

        if (resolution.Kind is SymbolResolutionKind.ParameterDeclaration or SymbolResolutionKind.Declaration &&
            TryBuildDeclaredTypeHoverSignatureOverride(symbol, semanticModel, root, offset, out var declaredTypeSignature))
            return declaredTypeSignature;

        if (resolution.Kind is SymbolResolutionKind.InvocationTarget &&
            symbol is INamedTypeSymbol invocationTargetType)
        {
            return BuildInvocationTargetTypeSignature(invocationTargetType);
        }

        var signature = BuildSignature(symbol, contextNode, semanticModel);
        if (resolution.Kind == SymbolResolutionKind.TypePosition &&
            string.Equals(signature, "<Error>", StringComparison.Ordinal) &&
            TryInferDeclaredTypeAtOffset(root, offset, semanticModel, out var typeAtOffset))
        {
            signature = BuildSignature(typeAtOffset, contextNode, semanticModel);
        }
        else if (resolution.Kind == SymbolResolutionKind.TypePosition &&
                 string.Equals(signature, "<Error>", StringComparison.Ordinal) &&
                 TryBuildTypeSyntaxSignatureAtOffset(root, offset, semanticModel, out var typeSyntaxSignature))
        {
            signature = typeSyntaxSignature;
        }

        if (!TryBuildReceiverErrorSignatureOverride(symbol, semanticModel, root, offset, out var overridden))
            return signature;

        return overridden;
    }

    private static bool TryBuildTypeSyntaxSignatureAtOffset(
        SyntaxNode root,
        int offset,
        SemanticModel semanticModel,
        out string signature)
    {
        signature = string.Empty;

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var genericName = token.Parent?
                .AncestorsAndSelf()
                .OfType<GenericNameSyntax>()
                .FirstOrDefault(generic => generic.Span.Contains(token.Span));
            if (genericName is null)
                continue;

            var arity = genericName.TypeArgumentList.Arguments.Count;
            var metadataName = genericName.Identifier.ValueText + "`" + arity;
            var definition = semanticModel.Compilation.GetTypeByMetadataName(metadataName)
                ?? semanticModel.Compilation.GetTypeByMetadataName("System." + metadataName);
            if (definition is null || definition.ContainsErrorType())
                continue;

            var kind = definition.TypeKind switch
            {
                TypeKind.Interface => "interface",
                TypeKind.Struct when definition.IsUnion => "union struct",
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                TypeKind.Class when definition.IsUnion => "union class",
                _ => "class"
            };

            var argumentFormat = CreatePlainTypeFormat();
            var arguments = genericName.TypeArgumentList.Arguments
                .Select(argument => FormatTypeSyntaxForSignature(argument.Type, semanticModel, argumentFormat));
            signature = $"{kind} {genericName.Identifier.ValueText}<{string.Join(", ", arguments)}>";
            return true;
        }

        return false;
    }

    private static string BuildInvocationTargetTypeSignature(INamedTypeSymbol type)
    {
        var plainTypeFormat = CreatePlainTypeFormat();
        var declarationTypeFormat = plainTypeFormat
            .WithMiscellaneousOptions(
                plainTypeFormat.MiscellaneousOptions |
                SymbolDisplayMiscellaneousOptions.IncludeUnionMemberTypes);

        var kind = type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct when type.IsUnion => "union struct",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Class when type.IsUnion => "union class",
            _ => "class"
        };

        var text = $"{kind} {FormatType(type, plainTypeFormat)}";
        var bases = new System.Collections.Generic.List<string>();

        if (type.BaseType is { SpecialType: SpecialType.None } baseType)
            bases.Add(FormatType(baseType, declarationTypeFormat));

        foreach (var iface in type.Interfaces)
            bases.Add(FormatType(iface, declarationTypeFormat));

        if (bases.Count > 0)
            text += ": " + string.Join(", ", bases);

        return text;
    }

    private static string BuildDisplaySignatureForResolvedHover(
        SymbolResolutionResult resolution,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        var signature = BuildSignatureForResolvedHover(resolution, semanticModel, root, offset);

        return IsExtensionHoverSymbol(resolution.Symbol)
            ? $"(extension) {signature}"
            : signature;
    }

    private static string BuildDisplaySignatureForHover(
        ISymbol symbol,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        var kind = contextNode switch
        {
            TypeSyntax => SymbolResolutionKind.TypePosition,
            ParameterSyntax => SymbolResolutionKind.ParameterDeclaration,
            _ => SymbolResolutionKind.SymbolInfo
        };

        return BuildDisplaySignatureForResolvedHover(
            new SymbolResolutionResult(kind, symbol, contextNode),
            semanticModel,
            root,
            offset);
    }

    private static string BuildSignatureForHover(
        ISymbol symbol,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset)
    {
        var kind = contextNode switch
        {
            TypeSyntax => SymbolResolutionKind.TypePosition,
            ParameterSyntax => SymbolResolutionKind.ParameterDeclaration,
            _ => SymbolResolutionKind.SymbolInfo
        };

        return BuildSignatureForResolvedHover(
            new SymbolResolutionResult(kind, symbol, contextNode),
            semanticModel,
            root,
            offset);
    }

    private static bool TryBuildDeclaredTypeHoverSignatureOverride(
        ISymbol symbol,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        out string signature)
    {
        signature = string.Empty;

        if (symbol is not ILocalSymbol and not IParameterSymbol)
            return false;

        var typeSymbol = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null
        };

        if (typeSymbol is null || !typeSymbol.ContainsErrorType())
            return false;

        if (!TryInferDeclaredTypeAtOffset(root, offset, semanticModel, out var declaredType))
            return false;

        var plainTypeFormat = CreatePlainTypeFormat();
        var bindingPrefix = symbol switch
        {
            ILocalSymbol local => $"{(local.IsMutable ? "var" : "val")} {local.Name}: ",
            IParameterSymbol parameter => $"{GetNonPublicParameterAccessibilityPrefix(parameter)}{GetPromotedPrimaryConstructorBindingPrefix(parameter)}{parameter.Name}: ",
            _ => string.Empty
        };

        signature = bindingPrefix + FormatType(declaredType, plainTypeFormat);
        return true;
    }

    private static bool TryGetPatternInputType(
        SemanticModel semanticModel,
        SyntaxNode patternNode,
        out ITypeSymbol inputType)
    {
        inputType = null!;

        if (patternNode.GetAncestor<PatternDeclarationAssignmentStatementSyntax>() is { } patternAssignment)
        {
            var type = GetExpressionType(semanticModel, patternAssignment.Right);
            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                inputType = type;
                return true;
            }
        }

        if (patternNode.GetAncestor<IsPatternExpressionSyntax>() is { } isPattern)
        {
            var type = GetExpressionType(semanticModel, isPattern.Expression);
            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                inputType = type;
                return true;
            }
        }

        if (patternNode.GetAncestor<MatchExpressionSyntax>() is { } matchExpression)
        {
            var type = GetExpressionType(semanticModel, matchExpression.Expression);
            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                inputType = type;
                return true;
            }
        }

        if (patternNode.GetAncestor<MatchStatementSyntax>() is { } matchStatement)
        {
            var type = GetExpressionType(semanticModel, matchStatement.Expression);
            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                inputType = type;
                return true;
            }
        }

        if (patternNode.GetAncestor<ForStatementSyntax>() is { } forStatement &&
            forStatement.Target is PatternSyntax)
        {
            var collectionType = GetExpressionType(semanticModel, forStatement.Expression);
            if (TryGetForIterationElementType(collectionType, semanticModel, out var elementType))
            {
                inputType = elementType;
                return true;
            }
        }

        if (patternNode.GetAncestor<ParameterSyntax>() is { Pattern: not null } parameterSyntax &&
            semanticModel.GetDeclaredSymbol(parameterSyntax) is IParameterSymbol parameterSymbol &&
            parameterSymbol.Type is { TypeKind: not TypeKind.Error } parameterType)
        {
            inputType = parameterType;
            return true;
        }

        return false;
    }

    private static ITypeSymbol? GetExpressionType(SemanticModel semanticModel, ExpressionSyntax expression)
    {
        var expressionTypeInfo = semanticModel.GetTypeInfo(expression);
        var type = expressionTypeInfo.Type ?? expressionTypeInfo.ConvertedType;
        if (type is not null && type.TypeKind != TypeKind.Error)
            return type;

        var symbol = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, expression, out var symbolInfo)
            ? symbolInfo.Symbol
            : null;
        return symbol switch
        {
            ILocalSymbol local => local.Type.TypeKind != TypeKind.Error && local.Type is not IArrayTypeSymbol { ElementType.TypeKind: TypeKind.Error }
                ? local.Type
                : TryGetReferencedIdentifierDeclaredType(semanticModel, expression) ?? local.Type,
            IParameterSymbol parameter => parameter.Type.TypeKind != TypeKind.Error && parameter.Type is not IArrayTypeSymbol { ElementType.TypeKind: TypeKind.Error }
                ? parameter.Type
                : TryGetReferencedIdentifierDeclaredType(semanticModel, expression) ?? parameter.Type,
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol ev => ev.Type,
            _ => TryGetReferencedIdentifierDeclaredType(semanticModel, expression) ?? type
        };
    }

    private static ITypeSymbol? TryGetReferencedIdentifierDeclaredType(SemanticModel semanticModel, ExpressionSyntax expression)
    {
        if (expression is not IdentifierNameSyntax identifier)
            return null;

        var root = expression.SyntaxTree!.GetRoot();
        var name = identifier.Identifier.ValueText;

        var parameterType = root.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(parameter => parameter.Identifier.ValueText == name &&
                                parameter.Span.Start <= expression.Span.Start &&
                                parameter.TypeAnnotation is not null)
            .Select(parameter => parameter.TypeAnnotation!.Type)
            .Select(typeSyntax => TryResolveTypeSymbolFromSyntax(semanticModel, typeSyntax, out var resolvedType) ? resolvedType : null)
            .LastOrDefault(type => type is not null);
        if (parameterType is not null)
            return parameterType;

        var localType = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.ValueText == name &&
                                 declarator.Span.Start <= expression.Span.Start &&
                                 declarator.TypeAnnotation is not null)
            .Select(declarator => declarator.TypeAnnotation!.Type)
            .Select(typeSyntax => TryResolveTypeSymbolFromSyntax(semanticModel, typeSyntax, out var resolvedType) ? resolvedType : null)
            .LastOrDefault(type => type is not null);

        return localType;
    }

    private static bool TryBuildPatternDeclarationSignatureOverride(
        ILocalSymbol local,
        SyntaxNode root,
        int offset,
        SemanticModel semanticModel,
        out string signature)
    {
        signature = string.Empty;
        var plainTypeFormat = CreatePlainTypeFormat();

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var designation = token.Parent?.AncestorsAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault();
            if (designation is null || !string.Equals(designation.Identifier.ValueText, local.Name, StringComparison.Ordinal))
                continue;

            var binding = local.IsMutable || designation.BindingKeyword.Kind == SyntaxKind.VarKeyword ? "var" : "val";

            if (local.Type.TypeKind != TypeKind.Error)
            {
                signature = $"{binding} {local.Name}: {FormatType(local.Type, plainTypeFormat)}";
                return true;
            }

            if (designation.GetAncestor<DeclarationPatternSyntax>() is { } declarationPattern)
            {
                var typeDisplay = TryResolveTypeSymbolFromSyntax(semanticModel, declarationPattern.Type, out var declaredType)
                    ? FormatType(declaredType, plainTypeFormat)
                    : declarationPattern.Type.ToString();
                signature = $"{binding} {local.Name}: {typeDisplay}";
                return true;
            }

            if (designation.GetAncestor<SequencePatternElementSyntax>() is { } sequenceElement &&
                designation.GetAncestor<SequencePatternSyntax>() is { } sequencePattern &&
                TryGetPatternInputType(semanticModel, sequencePattern, out var sequenceInputType) &&
                TryGetSequencePatternElementType(sequenceInputType, semanticModel, out var sequenceElementType))
            {
                var elementIndex = FindSequencePatternElementIndex(sequencePattern, sequenceElement);
                var elementType = GetSequencePatternElementValueType(
                    sequencePattern,
                    elementIndex,
                    sequenceInputType,
                    sequenceElementType,
                    semanticModel);
                signature = $"{binding} {local.Name}: {FormatType(elementType, plainTypeFormat)}";
                return true;
            }

            if (designation.GetAncestor<PositionalPatternSyntax>() is { } positionalPattern &&
                TryGetPatternInputType(semanticModel, positionalPattern, out var positionalInputType))
            {
                var tupleElementTypes = GetTupleElementTypes(positionalInputType);
                for (var i = 0; i < positionalPattern.Elements.Count && i < tupleElementTypes.Length; i++)
                {
                    if (!positionalPattern.Elements[i].Span.Contains(designation.Span))
                        continue;

                    signature = $"{binding} {local.Name}: {FormatType(tupleElementTypes[i], plainTypeFormat)}";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryInferPatternDeclaredLocalTypeAtOffset(
        SyntaxNode root,
        int offset,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            if (token.Parent?.AncestorsAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault() is not { } designation)
                continue;

            if (TryInferPatternDeclaredLocalType(designation, semanticModel, out inferredType))
                return true;
        }

        return false;
    }

    private static bool TryGetForIterationElementType(
        ITypeSymbol? collectionType,
        SemanticModel semanticModel,
        out ITypeSymbol elementType)
    {
        elementType = null!;
        if (collectionType is null || collectionType.TypeKind == TypeKind.Error)
            return false;

        if (collectionType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (collectionType.SpecialType == SpecialType.System_String)
        {
            elementType = semanticModel.Compilation.GetSpecialType(SpecialType.System_Char);
            return true;
        }

        if (collectionType is INamedTypeSymbol namedType)
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
            {
                if (candidate.TypeArguments.Length == 1 &&
                    candidate.Name is "IEnumerable" or "IAsyncEnumerable")
                {
                    elementType = candidate.TypeArguments[0];
                    return true;
                }
            }
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }
    }

    private static bool TryGetSequencePatternElementType(
        ITypeSymbol inputType,
        SemanticModel semanticModel,
        out ITypeSymbol elementType)
    {
        elementType = null!;

        if (inputType.TypeKind == TypeKind.Error)
            return false;

        if (inputType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (inputType.SpecialType == SpecialType.System_String)
        {
            elementType = semanticModel.Compilation.GetSpecialType(SpecialType.System_Char);
            return true;
        }

        if (inputType is INamedTypeSymbol namedType)
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
            {
                if (TryGetIndexableElementType(candidate, out var indexerElementType))
                {
                    elementType = indexerElementType;
                    return true;
                }
            }
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }

        static bool TryGetIndexableElementType(INamedTypeSymbol type, out ITypeSymbol indexerElementType)
        {
            indexerElementType = null!;

            var hasCount = type
                .GetMembers("Count")
                .OfType<IPropertySymbol>()
                .Any(static property =>
                    property.Parameters.Length == 0 &&
                    property.Type.SpecialType == SpecialType.System_Int32 &&
                    property.GetMethod is not null);

            if (!hasCount)
                return false;

            var indexer = type
                .GetMembers("Item")
                .OfType<IPropertySymbol>()
                .FirstOrDefault(static property =>
                    property.Parameters.Length == 1 &&
                    property.Parameters[0].Type.SpecialType == SpecialType.System_Int32 &&
                    property.GetMethod is not null);

            if (indexer is null)
                return false;

            indexerElementType = indexer.Type;
            return true;
        }
    }

    private static ITypeSymbol GetSequenceSliceType(
        ITypeSymbol valueType,
        ITypeSymbol elementType,
        SemanticModel semanticModel,
        int? fixedLength = null)
    {
        if (valueType.SpecialType == SpecialType.System_String)
            return semanticModel.Compilation.GetSpecialType(SpecialType.System_String);

        return semanticModel.Compilation.CreateArrayTypeSymbol(elementType, fixedLength: fixedLength);
    }

    private static ITypeSymbol GetSequencePatternElementValueType(
        SequencePatternSyntax pattern,
        int elementIndex,
        ITypeSymbol inputType,
        ITypeSymbol elementType,
        SemanticModel semanticModel)
    {
        if (elementIndex < 0 || elementIndex >= pattern.Elements.Count)
            return elementType;

        var (elementWidths, elementKinds, restIndex) = GetSequencePatternLayout(pattern);
        if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.Single)
            return elementType;

        if (inputType is IArrayTypeSymbol arrayType && arrayType.FixedLength is int sourceFixedLength)
        {
            if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.FixedSegment)
                return GetSequenceSliceType(inputType, elementType, semanticModel, elementWidths[elementIndex]);

            if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.RestSegment && restIndex >= 0)
            {
                var fixedWidth = elementWidths.Where(static width => width > 0).Sum();
                var restWidth = sourceFixedLength - fixedWidth;
                if (restWidth >= 0)
                    return GetSequenceSliceType(inputType, elementType, semanticModel, restWidth);
            }
        }

        return GetSequenceSliceType(inputType, elementType, semanticModel);
    }

    private static (ImmutableArray<int> Widths, ImmutableArray<BoundPositionalPattern.SequenceElementKind> Kinds, int RestIndex)
        GetSequencePatternLayout(SequencePatternSyntax pattern)
    {
        var widths = ImmutableArray.CreateBuilder<int>(pattern.Elements.Count);
        var kinds = ImmutableArray.CreateBuilder<BoundPositionalPattern.SequenceElementKind>(pattern.Elements.Count);
        var restIndex = -1;

        for (var i = 0; i < pattern.Elements.Count; i++)
        {
            var element = pattern.Elements[i];
            var prefix = element.Prefix;
            var dotDotKind = prefix.DotDotToken.Kind;
            if (dotDotKind is not SyntaxKind.DotDotToken and not SyntaxKind.DotDotDotToken)
            {
                widths.Add(1);
                kinds.Add(BoundPositionalPattern.SequenceElementKind.Single);
                continue;
            }

            if (prefix.SegmentLengthToken.Kind == SyntaxKind.NumericLiteralToken)
            {
                var width = int.TryParse(prefix.SegmentLengthToken.Text, out var parsedWidth)
                    ? parsedWidth
                    : 0;
                widths.Add(width);
                kinds.Add(BoundPositionalPattern.SequenceElementKind.FixedSegment);
                continue;
            }

            widths.Add(-1);
            kinds.Add(BoundPositionalPattern.SequenceElementKind.RestSegment);
            if (restIndex < 0)
                restIndex = i;
        }

        return (widths.ToImmutable(), kinds.ToImmutable(), restIndex);
    }

    private static int FindSequencePatternElementIndex(SequencePatternSyntax pattern, SequencePatternElementSyntax target)
    {
        for (var i = 0; i < pattern.Elements.Count; i++)
        {
            if (pattern.Elements[i].Span == target.Span)
                return i;
        }

        return -1;
    }

    private static ImmutableArray<ITypeSymbol> GetTupleElementTypes(ITypeSymbol expectedType)
    {
        if (expectedType is ITupleTypeSymbol tupleType)
            return tupleType.TupleElements.Select(static element => element.Type).ToImmutableArray();

        if (expectedType is INamedTypeSymbol namedType &&
            namedType.IsTupleType &&
            !namedType.TupleElements.IsDefaultOrEmpty)
        {
            return namedType.TupleElements.Select(static element => element.Type).ToImmutableArray();
        }

        return ImmutableArray<ITypeSymbol>.Empty;
    }

    private static bool TryBuildReceiverErrorSignatureOverride(
        ISymbol symbol,
        SemanticModel semanticModel,
        SyntaxNode root,
        int offset,
        out string signature)
    {
        signature = string.Empty;

        var plainTypeFormat = CreatePlainTypeFormat();

        var symbolName = symbol.Name;
        var isErrorParameter = symbol is IParameterSymbol parameter && parameter.Type.ContainsErrorType();
        var localBinding = symbol is ILocalSymbol local && local.Type.ContainsErrorType()
            ? local.IsMutable ? "var" : "val"
            : null;

        if (!isErrorParameter && localBinding is null)
            return false;

        var clampedOffset = Math.Clamp(offset, 0, root.FullSpan.End);
        var memberAccess = FindMemberAccessAtOffset(root, clampedOffset);
        if (memberAccess?.Expression is not IdentifierNameSyntax receiverIdentifier ||
            !string.Equals(receiverIdentifier.Identifier.ValueText, symbolName, StringComparison.Ordinal))
        {
            return false;
        }

        var receiverTypeInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
        var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
        {
            if (TryInferLambdaParameterTypeByNameFromContext(symbolName, receiverIdentifier, semanticModel, out var inferredLambdaType))
                receiverType = inferredLambdaType;
        }

        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
        {
            var memberSymbol =
                (SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, memberAccess.Name, out var memberNameInfo)
                    ? memberNameInfo.Symbol
                    : null)
                ??
                (SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, memberAccess, out var memberAccessInfo)
                    ? memberAccessInfo.Symbol
                    : null);
            receiverType = memberSymbol switch
            {
                IPropertySymbol property => property.ContainingType,
                IFieldSymbol field => field.ContainingType,
                IMethodSymbol method => method.ContainingType,
                _ => null
            };
        }

        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return false;

        signature = localBinding is not null
            ? $"{localBinding} {symbolName}: {FormatType(receiverType, plainTypeFormat)}"
            : $"{symbolName}: {FormatType(receiverType, plainTypeFormat)}";
        return true;
    }

    private static MemberAccessExpressionSyntax? FindMemberAccessAtOffset(SyntaxNode root, int offset)
    {
        var access = root
            .DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Span.Contains(offset))
            .OrderBy(member => member.Span.Length)
            .FirstOrDefault();

        if (access is not null)
            return access;

        if (offset <= 0)
            return null;

        return root
            .DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Span.Contains(offset - 1))
            .OrderBy(member => member.Span.Length)
            .FirstOrDefault();
    }

    private static string GetNonPublicAccessibilityPrefix(ISymbol symbol)
    {
        var accessibility = symbol.DeclaredAccessibility;
        if (accessibility is Accessibility.NotApplicable or Accessibility.Public)
            return string.Empty;

        return AccessibilityUtilities.GetDisplayText(accessibility) + " ";
    }

    private static string GetNonPublicParameterAccessibilityPrefix(IParameterSymbol parameter)
    {
        foreach (var syntaxReference in parameter.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not ParameterSyntax parameterSyntax)
                continue;

            var kind = parameterSyntax.AccessibilityKeyword.Kind;
            if (kind is SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ProtectedKeyword)
                return parameterSyntax.AccessibilityKeyword.Text + " ";
        }

        return string.Empty;
    }

    private static string FormatType(ITypeSymbol type, SymbolDisplayFormat format)
    {
        if (type is IErrorTypeSymbol || type.TypeKind == TypeKind.Error)
            return "<Error>";

        return type.ToDisplayString(format);
    }

    private static string FormatTupleNominalType(ITupleTypeSymbol tupleType, SymbolDisplayFormat format)
    {
        var underlyingTupleType = tupleType.UnderlyingTupleType;
        return underlyingTupleType is not null
            ? FormatType(underlyingTupleType, format)
            : FormatType(tupleType, format);
    }

    private static string AppendBaseTypeList(
        string text,
        INamedTypeSymbol type,
        SymbolDisplayFormat declarationTypeFormat)
    {
        var bases = new System.Collections.Generic.List<string>();

        if (type.BaseType is { SpecialType: SpecialType.None } baseType)
            bases.Add(FormatType(baseType, declarationTypeFormat));

        foreach (var iface in type.Interfaces)
            bases.Add(FormatType(iface, declarationTypeFormat));

        return bases.Count > 0
            ? text + ": " + string.Join(", ", bases)
            : text;
    }

    private static bool TryFormatDelegateTypeSignature(
        INamedTypeSymbol delegateType,
        SymbolDisplayFormat plainTypeFormat,
        out string signature)
    {
        var invokeMethod = delegateType.GetDelegateInvokeMethod();
        if (invokeMethod is null)
        {
            signature = string.Empty;
            return false;
        }

        var parameters = string.Join(
            ", ",
            invokeMethod.Parameters.Select(parameter =>
            {
                var modifier = parameter.RefKind switch
                {
                    RefKind.In => "in ",
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.RefReadOnly => "ref readonly ",
                    _ => string.Empty
                };

                return modifier + FormatType(parameter.Type, plainTypeFormat);
            }));

        var returnType = FormatType(invokeMethod.ReturnType, plainTypeFormat);
        signature = $"({parameters}) -> {returnType}";
        return true;
    }

    private static string FormatTrackedHoverSymbolDisplay(ISymbol symbol)
    {
        var tooltipFormat = SymbolDisplayFormat.RavenTooltipFormat;

        return symbol switch
        {
            INamespaceSymbol namespaceSymbol => $"namespace {FormatNamespaceDisplay(namespaceSymbol)}",
            ILocalSymbol local => $"{(local.IsMutable ? "var" : "val")} {local.Name}: {FormatType(local.Type, tooltipFormat)}",
            IParameterSymbol parameter => $"{parameter.Name}: {FormatType(parameter.Type, tooltipFormat)}",
            ITypeSymbol type => FormatType(type, tooltipFormat),
            _ => symbol.ToDisplayString(tooltipFormat)
        };
    }

    private static bool TryFormatFunctionTypeSyntaxSignature(
        FunctionTypeSyntax functionTypeSyntax,
        SemanticModel semanticModel,
        SymbolDisplayFormat plainTypeFormat,
        out string signature)
    {
        _ = semanticModel;
        _ = plainTypeFormat;

        var parameterTypes = functionTypeSyntax.Parameter is { } singleParameter
            ? [singleParameter.ToString()]
            : functionTypeSyntax.ParameterList is { } parameterList
                ? parameterList.Parameters.Select(static parameter => parameter.ToString()).ToList()
                : [];

        signature = $"({string.Join(", ", parameterTypes)}) -> {functionTypeSyntax.ReturnType}";
        return true;
    }

    private static ImmutableArray<IParameterSymbol> GetDisplayParametersForMethod(
        IMethodSymbol method,
        SyntaxNode contextNode,
        SemanticModel semanticModel)
    {
        if (IsExtensionMethodAccessedAsInstance(method, contextNode, semanticModel) &&
            !method.Parameters.IsDefaultOrEmpty)
        {
            return method.Parameters.RemoveAt(0);
        }

        return method.Parameters;
    }

    private static bool IsExtensionMethodAccessedAsInstance(
        IMethodSymbol method,
        SyntaxNode contextNode,
        SemanticModel semanticModel)
    {
        if (!method.IsExtensionMethod)
            return false;

        // We want C#-like behavior when the extension is used through member access:
        //   receiver.ExtMethod(...)
        // and NOT when called statically:
        //   Extensions.ExtMethod(receiver, ...)
        var nameNode = contextNode;

        // Hover resolution may give us the member access node or the identifier node.
        if (nameNode is MemberAccessExpressionSyntax memberAccess)
            nameNode = memberAccess.Name;

        if (nameNode is not IdentifierNameSyntax identifier)
            return false;

        if (identifier.Parent is not MemberAccessExpressionSyntax parentAccess ||
            parentAccess.Name != identifier)
        {
            return false;
        }

        // Most Raven extension calls are ordinary instance-style member accesses.
        // Avoid querying the receiver symbol during hover signature construction
        // unless the syntax looks like static-style type or namespace access.
        if (parentAccess.Expression is IdentifierNameSyntax receiverIdentifier &&
            !IsLikelyTypeOrNamespaceIdentifier(receiverIdentifier.Identifier.ValueText))
        {
            return true;
        }

        // If the receiver resolves to a type/namespace, this is a static-style access.
        var receiverSymbol = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, parentAccess.Expression, out var receiverInfo)
            ? receiverInfo.Symbol
            : null;
        if (receiverSymbol is ITypeSymbol or INamespaceSymbol)
            return false;

        return true;
    }

    private static bool IsLikelyTypeOrNamespaceIdentifier(string name)
        => !string.IsNullOrEmpty(name) && char.IsUpper(name[0]);

    private static string? BuildContainingDisplay(ISymbol symbol, SemanticModel semanticModel)
    {
        if (symbol is INamespaceSymbol)
            return null;

        if (TryGetExtensionContainerDisplay(symbol, out var extensionContaining))
            return extensionContaining;

        if (TryGetLambdaContainingDisplay(symbol, semanticModel, out var lambdaContaining))
            return lambdaContaining;

        if (symbol is ILocalSymbol &&
            GetUserFacingContainingSymbol(symbol) is IMethodSymbol localContainingMethod)
        {
            return FormatLocalContainingCallableDisplay(localContainingMethod);
        }

        if (symbol is IParameterSymbol parameterSymbol &&
            IsPromotedPrimaryConstructorParameter(parameterSymbol) &&
            parameterSymbol.ContainingSymbol is IMethodSymbol constructor &&
            constructor.ContainingType is { } containingType)
        {
            return containingType.ToDisplayString(
                SymbolDisplayFormat.RavenSignatureFormat.WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameOnly));
        }

        if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } containingConstructor } &&
            MethodSignatureContainsError(containingConstructor))
        {
            var plainTypeFormat = CreatePlainTypeFormat();
            var parameters = FormatParameters(containingConstructor.Parameters, plainTypeFormat);
            var returnType = FormatType(containingConstructor.ReturnType, plainTypeFormat);
            return $"init({parameters}) -> {returnType}";
        }

        if (symbol is IMethodSymbol method &&
            TryGetEnclosingCallableDisplayForLocalFunction(method, semanticModel, out var localContaining))
        {
            return localContaining;
        }

        if (symbol.ContainingType is { } topLevelContainingType &&
            semanticModel.Compilation.IsNamespaceMemberContainer(topLevelContainingType) &&
            topLevelContainingType.ContainingNamespace is { } containingNamespace)
        {
            return "namespace " + FormatNamespaceDisplay(containingNamespace);
        }

        var containing = GetUserFacingContainingSymbol(symbol);
        return containing?.ToDisplayString(
            SymbolDisplayFormat.RavenSignatureFormat.WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameOnly));
    }

    private static bool ShouldComputeCaptureInfo(ISymbol symbol, SyntaxNode node)
    {
        if (symbol.DeclaringSyntaxReferences.IsDefaultOrEmpty)
            return false;

        return symbol is IMethodSymbol ||
               node is FunctionExpressionSyntax or FunctionStatementSyntax;
    }

    private static bool ShouldComputeCaptureInfoFromSyntax(SyntaxNode node)
        => node is FunctionExpressionSyntax or FunctionStatementSyntax;

    private static string FormatNamespaceDisplay(INamespaceSymbol namespaceSymbol)
    {
        var names = new Stack<string>();
        for (var current = namespaceSymbol; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
                names.Push(current.Name);
        }

        return names.Count == 0 ? "<global>" : string.Join(".", names);
    }

    private static bool TryGetLambdaContainingDisplay(ISymbol symbol, SemanticModel semanticModel, out string containingDisplay)
    {
        containingDisplay = string.Empty;

        if (symbol.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.LambdaMethod } lambdaMethod)
        {
            var lambdaSyntax = symbol.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .Select(static syntax => syntax.AncestorsAndSelf().OfType<FunctionExpressionSyntax>().FirstOrDefault())
                .FirstOrDefault(static syntax => syntax is not null);
            if (lambdaSyntax is null)
                return false;

            if (semanticModel.TryGetFunctionExpressionSymbol(lambdaSyntax, out var lambdaSymbol) &&
                lambdaSymbol is not null)
            {
                containingDisplay = FormatLambdaContainingDisplay(lambdaSymbol);
                return !string.IsNullOrWhiteSpace(containingDisplay);
            }

            if (TryFormatLambdaContainingDisplay(lambdaSyntax, semanticModel, out containingDisplay))
                return true;

            return false;
        }

        containingDisplay = FormatLambdaContainingDisplay(lambdaMethod);
        return !string.IsNullOrWhiteSpace(containingDisplay);
    }

    private static ISymbol? GetUserFacingContainingSymbol(ISymbol symbol)
    {
        var containing = symbol.ContainingSymbol;
        while (containing is IMethodSymbol { MethodKind: MethodKind.LambdaMethod } lambdaContainer)
            containing = lambdaContainer.ContainingSymbol;
        return containing;
    }

    private static string FormatLocalContainingCallableDisplay(IMethodSymbol method)
        => IsFunctionStatementSymbol(method)
            ? $"function {method.Name}"
            : $"method {method.Name}";

    private static string BuildKindDisplayForResolution(SymbolResolutionKind resolutionKind, ISymbol symbol)
    {
        if (resolutionKind == SymbolResolutionKind.TypePosition)
            return "Type";

        if (symbol is IMethodSymbol method && method.ExtensionMemberKind != ExtensionMemberKind.None)
            return "Extension method";

        if (symbol is IPropertySymbol property && property.ExtensionMemberKind != ExtensionMemberKind.None)
            return "Extension property";

        if (symbol is IParameterSymbol parameterSymbol &&
            IsPromotedPrimaryConstructorParameter(parameterSymbol))
        {
            return "Property";
        }

        if (symbol is IMethodSymbol functionMethod && IsFunctionStatementSymbol(functionMethod))
            return "Function";

        if (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod })
            return "Function expression";

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
            return "Constructor";

        if (symbol is IFieldSymbol { IsConst: true } or ILocalSymbol { IsConst: true })
            return "Constant";

        return symbol.Kind.ToString();
    }

    private static string BuildKindDisplay(ISymbol symbol)
        => BuildKindDisplayForResolution(SymbolResolutionKind.SymbolInfo, symbol);

    private static bool TryGetExtensionContainerDisplay(ISymbol symbol, out string? display)
    {
        display = null;

        var containingType = symbol switch
        {
            IMethodSymbol method when method.ExtensionMemberKind != ExtensionMemberKind.None => method.ContainingType,
            IPropertySymbol property when property.ExtensionMemberKind != ExtensionMemberKind.None => property.ContainingType,
            _ => null
        };

        if (containingType is null)
            return false;

        display = containingType.ToDisplayString(
            SymbolDisplayFormat.RavenSignatureFormat
                .WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces)
                .WithKindOptions(SymbolDisplayKindOptions.None));
        return true;
    }

    private static bool IsExtensionHoverSymbol(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.ExtensionMemberKind != ExtensionMemberKind.None,
            IPropertySymbol property => property.ExtensionMemberKind != ExtensionMemberKind.None,
            _ => false
        };
    }

    private static bool IsPromotedPrimaryConstructorParameter(IParameterSymbol parameter)
    {
        foreach (var syntaxReference in parameter.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not ParameterSyntax parameterSyntax)
                continue;

            if (parameterSyntax.Parent is not ParameterListSyntax { Parent: TypeDeclarationSyntax typeDeclaration })
                continue;

            var refKeywordKind = parameterSyntax.RefKindKeyword.Kind;
            var typeIsByRef = parameterSyntax.TypeAnnotation?.Type is ByRefTypeSyntax;
            if (refKeywordKind is not SyntaxKind.None || typeIsByRef)
                return false;

            var bindingKeyword = parameterSyntax.BindingKeyword.Kind;
            var isRecord = typeDeclaration is RecordDeclarationSyntax;
            return isRecord || bindingKeyword is SyntaxKind.ValKeyword or SyntaxKind.VarKeyword;
        }

        return false;
    }

    private static string GetPromotedPrimaryConstructorBindingPrefix(IParameterSymbol parameter)
    {
        foreach (var syntaxReference in parameter.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not ParameterSyntax parameterSyntax)
                continue;

            if (parameterSyntax.Parent is not ParameterListSyntax { Parent: TypeDeclarationSyntax typeDeclaration })
                continue;

            var refKeywordKind = parameterSyntax.RefKindKeyword.Kind;
            var typeIsByRef = parameterSyntax.TypeAnnotation?.Type is ByRefTypeSyntax;
            if (refKeywordKind is not SyntaxKind.None || typeIsByRef)
                return string.Empty;

            var bindingKeyword = parameterSyntax.BindingKeyword.Kind;
            if (bindingKeyword == SyntaxKind.ValKeyword)
                return "val ";

            if (bindingKeyword == SyntaxKind.VarKeyword)
                return "var ";

            if (typeDeclaration is RecordDeclarationSyntax)
                return parameter.IsMutable ? "var " : "val ";
        }

        return string.Empty;
    }

    private static bool TryGetEnclosingCallableDisplayForLocalFunction(
        IMethodSymbol method,
        SemanticModel semanticModel,
        out string containingDisplay)
    {
        containingDisplay = string.Empty;
        if (!IsFunctionStatementSymbol(method))
            return false;

        var functionSyntax = method.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .OfType<FunctionStatementSyntax>()
            .FirstOrDefault();
        if (functionSyntax is null)
            return false;

        var containingSyntax = functionSyntax.Ancestors().FirstOrDefault(static node =>
            node is FunctionStatementSyntax
                or MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or ParameterlessConstructorDeclarationSyntax
                or InitializerBlockDeclarationSyntax
                or AccessorDeclarationSyntax);
        if (containingSyntax is null)
            return false;

        var containingSymbol = semanticModel.GetDeclaredSymbol(containingSyntax);
        if (containingSymbol is null)
            return false;

        containingDisplay = FormatEnclosingCallableDisplay(containingSymbol);
        return !string.IsNullOrWhiteSpace(containingDisplay);
    }

    private static bool IsFunctionStatementSymbol(IMethodSymbol method)
    {
        return method.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is FunctionStatementSyntax);
    }

    private static bool IsLocalFunctionDeclaredStatic(IMethodSymbol method)
    {
        var functionStatement = method.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .OfType<FunctionStatementSyntax>()
            .FirstOrDefault();
        if (functionStatement is null)
            return false;

        return functionStatement.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
    }

    private static string FormatEnclosingCallableDisplay(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            return symbol.ToDisplayString(
                SymbolDisplayFormat.RavenSignatureFormat.WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameOnly));
        }

        var plainTypeFormat = CreatePlainTypeFormat();
        var parameters = FormatParameters(method.Parameters, plainTypeFormat);
        var returnType = method.ReturnType.ToDisplayString(plainTypeFormat);
        var staticPrefix = IsMethodDeclaredStaticForDisplay(method) ? "static " : string.Empty;
        return $"{staticPrefix}func {method.Name}({parameters}) -> {returnType}";
    }

    private static string FormatLambdaContainingDisplay(IMethodSymbol lambdaMethod)
    {
        var plainTypeFormat = CreatePlainTypeFormat();
        var parameters = FormatParameters(lambdaMethod.Parameters, plainTypeFormat);
        var returnType = FormatType(lambdaMethod.ReturnType, plainTypeFormat);
        return $"func ({parameters}) -> {returnType}";
    }

    private static bool TryFormatLambdaContainingDisplay(
        FunctionExpressionSyntax functionExpression,
        SemanticModel semanticModel,
        out string containingDisplay)
    {
        containingDisplay = string.Empty;

        var functionType = semanticModel.TryGetFunctionExpressionDelegateType(functionExpression, out var contextualFunctionType)
            ? contextualFunctionType
            : semanticModel.GetTypeInfo(functionExpression) is var functionTypeInfo
                ? functionTypeInfo.ConvertedType ?? functionTypeInfo.Type
                : null;

        var delegateType = UnwrapDelegateType(functionType);
        var invokeMethod = delegateType?.GetDelegateInvokeMethod();
        if (invokeMethod is null)
            return false;

        containingDisplay = FormatLambdaContainingDisplay(invokeMethod);
        return !string.IsNullOrWhiteSpace(containingDisplay);
    }

    private static SymbolDisplayFormat CreatePlainTypeFormat()
    {
        var miscOptions = SymbolDisplayFormat.RavenSignatureFormat.MiscellaneousOptions |
                          SymbolDisplayMiscellaneousOptions.IncludeTupleElementNames;

        return SymbolDisplayFormat.RavenSignatureFormat
            .WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameOnly)
            .WithDelegateStyle(SymbolDisplayDelegateStyle.NameOnly)
            .WithKindOptions(SymbolDisplayKindOptions.None)
            .WithMiscellaneousOptions(miscOptions);
    }

    private static bool IsMethodDeclaredStaticForDisplay(IMethodSymbol method)
    {
        foreach (var syntax in method.DeclaringSyntaxReferences.Select(static r => r.GetSyntax()))
        {
            switch (syntax)
            {
                case FunctionStatementSyntax function:
                    return function.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
                case MethodDeclarationSyntax declaration:
                    return declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
                case ConstructorDeclarationSyntax declaration:
                    return declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
                case ParameterlessConstructorDeclarationSyntax declaration:
                    return declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
                case InitializerBlockDeclarationSyntax declaration:
                    return declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);
            }
        }

        return method.IsStatic;
    }

    private static bool TryInferLambdaParameterTypeFromContext(
        IParameterSymbol parameter,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
        => TryInferLambdaParameterTypeByNameFromContext(parameter.Name, contextNode, semanticModel, out inferredType);

    private static bool TryInferLambdaParameterTypeFromFunctionTarget(
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        var parameterSyntax = contextNode.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
        var functionExpression = contextNode.AncestorsAndSelf().OfType<FunctionExpressionSyntax>().FirstOrDefault();
        if (parameterSyntax is null || functionExpression is null)
            return false;

        var functionType = semanticModel.TryGetFunctionExpressionDelegateType(functionExpression, out var contextualFunctionType)
            ? contextualFunctionType
            : semanticModel.GetTypeInfo(functionExpression) is var functionTypeInfo
                ? functionTypeInfo.ConvertedType ?? functionTypeInfo.Type
                : null;

        var delegateType = UnwrapDelegateType(functionType);
        var invokeMethod = delegateType?.GetDelegateInvokeMethod();
        if (invokeMethod is null || invokeMethod.Parameters.IsDefaultOrEmpty)
            return false;

        var parameterIndex = GetLambdaParameterIndex(functionExpression, parameterSyntax.Identifier.ValueText);
        if (parameterIndex < 0 || parameterIndex >= invokeMethod.Parameters.Length)
            return false;

        var parameterType = invokeMethod.Parameters[parameterIndex].Type;
        if (parameterType is null || parameterType.ContainsErrorType())
            return false;

        inferredType = parameterType is NullableTypeSymbol nullable ? nullable.UnderlyingType : parameterType;
        return true;
    }

    private static bool TryInferParameterDeclaredType(
        IParameterSymbol parameter,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        foreach (var syntaxReference in parameter.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not ParameterSyntax { TypeAnnotation.Type: { } typeSyntax })
                continue;

            if (!TryResolveTypeSymbolFromSyntax(semanticModel, typeSyntax, out var resolvedType))
                continue;

            inferredType = resolvedType;
            return true;
        }

        return false;
    }

    private static bool TryGetParameterDeclaredTypeSyntaxDisplay(
        IParameterSymbol parameter,
        out string typeDisplay)
    {
        typeDisplay = string.Empty;

        foreach (var syntaxReference in parameter.DeclaringSyntaxReferences)
        {
            var typeSyntax = syntaxReference.GetSyntax() switch
            {
                ParameterSyntax { TypeAnnotation.Type: { } parameterType } => parameterType,
                CaseFieldSyntax { TypeAnnotation.Type: { } fieldType } => fieldType,
                _ => null
            };

            if (typeSyntax is null)
                continue;

            typeDisplay = typeSyntax.ToString();
            return !string.IsNullOrWhiteSpace(typeDisplay);
        }

        return false;
    }

    private static bool TryGetLocalDeclaredTypeSyntaxDisplay(
        ILocalSymbol local,
        out string typeDisplay)
    {
        typeDisplay = string.Empty;

        foreach (var syntaxReference in local.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax() is not VariableDeclaratorSyntax { TypeAnnotation.Type: { } typeSyntax })
                continue;

            typeDisplay = typeSyntax.ToString();
            return !string.IsNullOrWhiteSpace(typeDisplay);
        }

        return false;
    }

    private static bool TryGetMemberDeclaredTypeSyntaxDisplay(
        ISymbol symbol,
        out string typeDisplay)
    {
        typeDisplay = string.Empty;

        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var typeSyntax = syntaxReference.GetSyntax() switch
            {
                PropertyDeclarationSyntax property => property.Type.Type,
                IndexerDeclarationSyntax indexer => indexer.Type.Type,
                EventDeclarationSyntax @event => @event.Type.Type,
                VariableDeclaratorSyntax declarator => declarator.TypeAnnotation?.Type,
                _ => null
            };

            if (typeSyntax is null)
                continue;

            typeDisplay = typeSyntax.ToString();
            return !string.IsNullOrWhiteSpace(typeDisplay);
        }

        return false;
    }

    private static bool TryInferDeclaredTypeFromContext(
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        foreach (var typeSyntax in contextNode.AncestorsAndSelf().OfType<TypeSyntax>())
        {
            if (!TryResolveTypeSymbolFromSyntax(semanticModel, typeSyntax, out var resolvedType))
                continue;

            inferredType = resolvedType;
            return true;
        }

        return false;
    }

    private static bool TryInferDeclaredTypeAtOffset(
        SyntaxNode root,
        int offset,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        foreach (var candidateOffset in NormalizeOffsets(offset, root.FullSpan.End))
        {
            SyntaxToken token;
            try
            {
                token = root.FindToken(candidateOffset);
            }
            catch
            {
                continue;
            }

            var typeSyntax = token.Parent?.AncestorsAndSelf().OfType<TypeSyntax>().FirstOrDefault();
            if (typeSyntax is null)
                continue;

            var typeSyntaxes = token.Parent!
                .AncestorsAndSelf()
                .OfType<TypeSyntax>()
                .Where(typeNode => typeNode.Span.Contains(token.Span));

            foreach (var candidateTypeSyntax in typeSyntaxes)
            {
                if (!TryResolveTypeSymbolFromSyntax(semanticModel, candidateTypeSyntax, out var resolvedType))
                    continue;

                inferredType = resolvedType;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveTypeSymbolFromSyntax(
        SemanticModel semanticModel,
        TypeSyntax typeSyntax,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        var typeInfo = semanticModel.TryGetAvailableTypeInfo(typeSyntax, out var availableTypeInfo)
            ? availableTypeInfo
            : semanticModel.GetTypeInfo(typeSyntax);

        var resolvedType = typeInfo.Type ?? typeInfo.ConvertedType;
        if (resolvedType is null || resolvedType.TypeKind == TypeKind.Error)
        {
            var typeSymbol = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, typeSyntax, out var typeSymbolInfo)
                ? typeSymbolInfo.Symbol
                : null;
            resolvedType = typeSymbol switch
            {
                ITypeSymbol resolved => resolved,
                IAliasSymbol { UnderlyingSymbol: ITypeSymbol aliasedType } => aliasedType,
                _ => resolvedType
            };
        }

        if ((resolvedType is null || resolvedType.TypeKind == TypeKind.Error || resolvedType is IArrayTypeSymbol { ElementType.TypeKind: TypeKind.Error }) &&
            TryResolveArrayTypeFromSyntax(semanticModel, typeSyntax, out var reconstructedArrayType))
        {
            resolvedType = reconstructedArrayType;
        }

        if ((resolvedType is null || resolvedType.TypeKind == TypeKind.Error) &&
            TryResolveBuiltInTypeFromSyntax(semanticModel, typeSyntax, out var builtInType))
        {
            resolvedType = builtInType;
        }

        if ((resolvedType is null || resolvedType.ContainsErrorType()) &&
            typeSyntax is GenericNameSyntax genericName &&
            TryResolveMetadataGenericTypeFromSyntax(semanticModel, genericName, out var metadataGenericType))
        {
            resolvedType = metadataGenericType;
        }

        if (resolvedType is null || resolvedType.ContainsErrorType())
            return false;

        inferredType = resolvedType;
        return true;
    }

    private static bool TryResolveMetadataGenericTypeFromSyntax(
        SemanticModel semanticModel,
        GenericNameSyntax genericName,
        out ITypeSymbol resolvedType)
    {
        resolvedType = null!;

        var arity = genericName.TypeArgumentList.Arguments.Count;
        var metadataName = genericName.Identifier.ValueText + "`" + arity;
        var definition = semanticModel.Compilation.GetTypeByMetadataName(metadataName)
            ?? semanticModel.Compilation.GetTypeByMetadataName("System." + metadataName);
        if (definition is null || definition.ContainsErrorType())
            return false;

        var arguments = new List<ITypeSymbol>(arity);
        foreach (var argument in genericName.TypeArgumentList.Arguments)
        {
            if (!TryResolveTypeSymbolFromSyntax(semanticModel, argument.Type, out var argumentType) ||
                argumentType.ContainsErrorType())
            {
                return false;
            }

            arguments.Add(argumentType);
        }

        resolvedType = definition.Construct(arguments.ToArray());
        return !resolvedType.ContainsErrorType();
    }

    private static bool IsFunctionExpressionIdentifierToken(FunctionExpressionSyntax functionExpression, SyntaxToken token)
        => functionExpression is ParenthesizedFunctionExpressionSyntax parenthesized &&
           token == parenthesized.Identifier;

    private static bool IsInsideFunctionExpressionBody(FunctionExpressionSyntax functionExpression, SyntaxToken token)
    {
        var body = (SyntaxNode?)functionExpression.Body ?? functionExpression.ExpressionBody;
        return body is not null && body.Span.Contains(token.Span);
    }

    private static bool TryResolveArrayTypeFromSyntax(
        SemanticModel semanticModel,
        TypeSyntax typeSyntax,
        out ITypeSymbol resolvedType)
    {
        resolvedType = null!;

        if (typeSyntax is not ArrayTypeSyntax arrayTypeSyntax)
            return false;

        if (!TryResolveTypeSymbolFromSyntax(semanticModel, arrayTypeSyntax.ElementType, out var currentElementType))
            return false;

        var currentType = currentElementType;
        foreach (var rankSpecifier in arrayTypeSyntax.RankSpecifiers)
        {
            var rank = rankSpecifier.CommaTokens.Count + 1;
            currentType = semanticModel.Compilation.CreateArrayTypeSymbol(
                currentType,
                rank,
                TryGetFixedArraySize(rankSpecifier));
        }

        resolvedType = currentType;
        return true;
    }

    private static int? TryGetFixedArraySize(ArrayRankSpecifierSyntax rankSpecifier)
        => rankSpecifier.CommaTokens.Count == 0 &&
           rankSpecifier.SizeToken.Kind == SyntaxKind.NumericLiteralToken &&
           int.TryParse(rankSpecifier.SizeToken.ValueText, out var parsedSize)
            ? parsedSize
            : null;

    private static bool TryResolveBuiltInTypeFromSyntax(
        SemanticModel semanticModel,
        TypeSyntax typeSyntax,
        out ITypeSymbol resolvedType)
    {
        resolvedType = null!;

        var specialType = typeSyntax.ToString() switch
        {
            "bool" => SpecialType.System_Boolean,
            "byte" => SpecialType.System_Byte,
            "sbyte" => SpecialType.System_SByte,
            "short" => SpecialType.System_Int16,
            "ushort" => SpecialType.System_UInt16,
            "int" => SpecialType.System_Int32,
            "uint" => SpecialType.System_UInt32,
            "long" => SpecialType.System_Int64,
            "ulong" => SpecialType.System_UInt64,
            "char" => SpecialType.System_Char,
            "float" => SpecialType.System_Single,
            "double" => SpecialType.System_Double,
            "decimal" => SpecialType.System_Decimal,
            "string" => SpecialType.System_String,
            "object" => SpecialType.System_Object,
            _ => SpecialType.None
        };

        if (specialType == SpecialType.None)
            return false;

        resolvedType = semanticModel.Compilation.GetSpecialType(specialType);
        return resolvedType.TypeKind != TypeKind.Error;
    }

    private static bool TryInferLambdaParameterTypeByNameFromContext(
        string parameterName,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        var functionExpression = contextNode.AncestorsAndSelf().OfType<FunctionExpressionSyntax>().FirstOrDefault();
        if (functionExpression is null)
            return false;

        if (!SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, functionExpression, out var functionInfo))
            functionInfo = default;
        var lambdaParameterIndex = GetLambdaParameterIndex(functionExpression, parameterName);
        if (functionInfo.Symbol is IMethodSymbol functionMethod &&
            !functionMethod.Parameters.IsDefaultOrEmpty)
        {
            var fromMethod = TryGetDelegateParameter(functionMethod.Parameters, parameterName, lambdaParameterIndex);

            if (fromMethod is not null &&
                fromMethod.Type is { TypeKind: not TypeKind.Error } typedFromMethod)
            {
                inferredType = typedFromMethod is NullableTypeSymbol nullable ? nullable.UnderlyingType : typedFromMethod;
                return true;
            }
        }

        if (functionExpression.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return TryInferLambdaParameterTypeFromAssignmentTarget(
                parameterName,
                functionExpression,
                semanticModel,
                out inferredType);
        }

        var argumentIndex = 0;
        foreach (var current in argumentList.Arguments)
        {
            if (current.Span == argument.Span && current.Kind == argument.Kind)
                break;

            argumentIndex++;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is IdentifierNameSyntax memberNameIdentifier &&
            semanticModel.GetTypeInfo(memberAccess.Expression) is var invocationReceiverTypeInfo &&
            (invocationReceiverTypeInfo.Type ?? invocationReceiverTypeInfo.ConvertedType) is INamedTypeSymbol invocationReceiverType &&
            invocationReceiverType.TypeKind != TypeKind.Error)
        {
            foreach (var method in invocationReceiverType.GetMembers(memberNameIdentifier.Identifier.ValueText).OfType<IMethodSymbol>())
            {
                if (method.Parameters.Length <= argumentIndex)
                    continue;

                if (method.Parameters[argumentIndex].Type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
                    continue;

                var invokeMethod = delegateType.GetDelegateInvokeMethod();
                if (invokeMethod is null || invokeMethod.Parameters.IsDefaultOrEmpty)
                    continue;

                var delegateParameter = TryGetDelegateParameter(invokeMethod.Parameters, parameterName, lambdaParameterIndex);

                if (delegateParameter is null || delegateParameter.Type.TypeKind == TypeKind.Error)
                    continue;

                inferredType = delegateParameter.Type is NullableTypeSymbol nullable
                    ? nullable.UnderlyingType
                    : delegateParameter.Type;
                return true;
            }
        }

        static IEnumerable<IMethodSymbol> EnumerateCandidateMethods(SymbolInfo info)
        {
            if (info.Symbol is IMethodSymbol method)
                yield return method;

            if (info.CandidateSymbols.IsDefaultOrEmpty)
                yield break;

            foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>())
                yield return candidate;
        }

        if (!SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, invocation, out var invocationInfo))
            invocationInfo = default;
        foreach (var method in EnumerateCandidateMethods(invocationInfo))
        {
            if (method.Parameters.Length <= argumentIndex)
                continue;

            if (method.Parameters[argumentIndex].Type is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
                continue;

            var invokeMethod = delegateType.GetDelegateInvokeMethod();
            if (invokeMethod is null || invokeMethod.Parameters.IsDefaultOrEmpty)
                continue;

            var delegateParameter = TryGetDelegateParameter(invokeMethod.Parameters, parameterName, lambdaParameterIndex);

            if (delegateParameter is null)
                continue;

            inferredType = delegateParameter.Type is NullableTypeSymbol nullable
                ? nullable.UnderlyingType
                : delegateParameter.Type;
            return inferredType.TypeKind != TypeKind.Error;
        }

        return false;
    }

    private static bool TryInferLambdaParameterTypeFromAssignmentTarget(
        string parameterName,
        FunctionExpressionSyntax functionExpression,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        ExpressionSyntax? targetExpression = null;

        if (functionExpression.Parent is AssignmentExpressionSyntax assignmentExpression &&
            assignmentExpression.Right.Span == functionExpression.Span &&
            assignmentExpression.Right.Kind == functionExpression.Kind &&
            assignmentExpression.Left is ExpressionSyntax assignmentExpressionLeft)
        {
            targetExpression = assignmentExpressionLeft;
        }
        else if (functionExpression.Parent is AssignmentStatementSyntax assignmentStatement &&
                 assignmentStatement.Right.Span == functionExpression.Span &&
                 assignmentStatement.Right.Kind == functionExpression.Kind &&
                 assignmentStatement.Left is ExpressionSyntax assignmentStatementLeft)
        {
            targetExpression = assignmentStatementLeft;
        }

        if (targetExpression is null)
            return false;

        var targetTypeInfo = semanticModel.GetTypeInfo(targetExpression);
        var targetType = targetTypeInfo.ConvertedType ?? targetTypeInfo.Type;

        var delegateType = UnwrapDelegateType(targetType);
        if (delegateType is null)
        {
            if (targetExpression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name is IdentifierNameSyntax memberName &&
                semanticModel.GetTypeInfo(memberAccess.Expression) is var memberReceiverTypeInfo &&
                (memberReceiverTypeInfo.Type ?? memberReceiverTypeInfo.ConvertedType) is INamedTypeSymbol receiverType &&
                receiverType.TypeKind != TypeKind.Error)
            {
                for (var currentType = receiverType; currentType is not null; currentType = currentType.BaseType)
                {
                    targetType = currentType.GetMembers(memberName.Identifier.ValueText)
                        .Select(member => member switch
                        {
                            IEventSymbol eventSymbol => eventSymbol.Type,
                            IPropertySymbol property => property.Type,
                            IFieldSymbol field => field.Type,
                            _ => null
                        })
                        .FirstOrDefault(type => type is not null && !type.ContainsErrorType());

                    if (targetType is not null)
                        break;
                }
            }

            var targetSymbol = targetExpression is MemberAccessExpressionSyntax targetMemberAccess
                ? (SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, targetMemberAccess.Name, out var targetMemberInfo)
                    ? targetMemberInfo.Symbol
                    : null)
                  ?? (SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, targetExpression, out var targetExpressionInfo)
                    ? targetExpressionInfo.Symbol
                    : null)
                : (SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, targetExpression, out var directTargetInfo)
                    ? directTargetInfo.Symbol
                    : null);

            if (targetSymbol is IMethodSymbol { AssociatedSymbol: { } associatedSymbol })
                targetSymbol = associatedSymbol;
            else if (targetSymbol is IFieldSymbol { AssociatedSymbol: { } associatedFieldSymbol })
                targetSymbol = associatedFieldSymbol;

            targetType = targetSymbol switch
            {
                IEventSymbol eventSymbol => eventSymbol.Type,
                ILocalSymbol local => local.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IParameterSymbol parameter => parameter.Type,
                _ => targetType
            };

            delegateType = UnwrapDelegateType(targetType);
            if (delegateType is null)
                return false;
        }

        var invokeMethod = delegateType.GetDelegateInvokeMethod();
        if (invokeMethod is null || invokeMethod.Parameters.IsDefaultOrEmpty)
            return false;

        var lambdaParameterIndex = GetLambdaParameterIndex(functionExpression, parameterName);
        var delegateParameter = TryGetDelegateParameter(invokeMethod.Parameters, parameterName, lambdaParameterIndex);

        if (delegateParameter is null || delegateParameter.Type.ContainsErrorType())
            return false;

        inferredType = delegateParameter.Type is NullableTypeSymbol nullable
            ? nullable.UnderlyingType
            : delegateParameter.Type;
        return true;
    }

    private static INamedTypeSymbol? UnwrapDelegateType(ITypeSymbol? type)
    {
        return type switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType => delegateType,
            NullableTypeSymbol { UnderlyingType: INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType } => delegateType,
            _ => null
        };
    }

    private static int GetLambdaParameterIndex(FunctionExpressionSyntax functionExpression, string parameterName)
    {
        if (functionExpression is not ParenthesizedFunctionExpressionSyntax parenthesized ||
            parenthesized.ParameterList is null)
            return -1;

        var parameters = parenthesized.ParameterList.Parameters;
        if (parameters.Green is null || parameters.Count == 0)
            return -1;

        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter is null)
                continue;

            var identifier = parameter.Identifier;
            if (identifier.Kind == SyntaxKind.None || identifier.IsMissing)
                continue;

            if (string.Equals(identifier.ValueText, parameterName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static IParameterSymbol? TryGetDelegateParameter(
        ImmutableArray<IParameterSymbol> parameters,
        string parameterName,
        int parameterIndex)
    {
        if (parameters.IsDefaultOrEmpty)
            return null;

        if (parameters.Length == 1)
            return parameters[0];

        if (parameterIndex >= 0 && parameterIndex < parameters.Length)
            return parameters[parameterIndex];

        return parameters.FirstOrDefault(p => string.Equals(p.Name, parameterName, StringComparison.Ordinal));
    }

    private static bool TryInferReceiverTypeFromMemberAccessContext(
        string symbolName,
        SyntaxNode contextNode,
        SemanticModel semanticModel,
        out ITypeSymbol inferredType)
    {
        inferredType = null!;

        var receiverIdentifier = contextNode switch
        {
            IdentifierNameSyntax identifier
                when identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
                     memberAccess.Expression.Span == identifier.Span &&
                     memberAccess.Expression.Kind == identifier.Kind &&
                     string.Equals(identifier.Identifier.ValueText, symbolName, StringComparison.Ordinal)
                => identifier,
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax identifier }
                when string.Equals(identifier.Identifier.ValueText, symbolName, StringComparison.Ordinal)
                => identifier,
            _ => contextNode.AncestorsAndSelf()
                .OfType<IdentifierNameSyntax>()
                .FirstOrDefault(identifier =>
                    identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Expression.Span == identifier.Span &&
                    memberAccess.Expression.Kind == identifier.Kind &&
                    string.Equals(identifier.Identifier.ValueText, symbolName, StringComparison.Ordinal))
        };

        if (receiverIdentifier is null ||
            receiverIdentifier.Parent is not MemberAccessExpressionSyntax receiverMemberAccess)
        {
            return false;
        }

        var receiverTypeInfo = semanticModel.GetTypeInfo(receiverMemberAccess.Expression);
        var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
        if ((receiverType is null || receiverType.TypeKind == TypeKind.Error) &&
            TryInferLambdaParameterTypeByNameFromContext(symbolName, receiverIdentifier, semanticModel, out var inferredLambdaType))
        {
            receiverType = inferredLambdaType;
        }

        if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
        {
            inferredType = receiverType;
            return true;
        }

        var accessedMember = SymbolResolutionHelpers.TryGetPreferredSymbolInfo(semanticModel, receiverMemberAccess.Name, out var accessedMemberInfo)
            ? accessedMemberInfo.Symbol
            : null;
        var inferredContainingType = accessedMember switch
        {
            IPropertySymbol property => property.ContainingType,
            IFieldSymbol field => field.ContainingType,
            IMethodSymbol method => method.ContainingType,
            _ => null
        };

        if (inferredContainingType is null || inferredContainingType.TypeKind == TypeKind.Error)
            return false;

        inferredType = inferredContainingType;
        return true;
    }

    private static string FormatParameters(IEnumerable<IParameterSymbol> parameters, SymbolDisplayFormat format)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var paramsPrefix = parameter.IsVarParams ? "params " : string.Empty;
                var refPrefix = parameter.RefKind switch
                {
                    RefKind.Ref => "ref ",
                    RefKind.Out => "out ",
                    RefKind.In => "in ",
                    RefKind.RefReadOnly => "ref readonly ",
                    RefKind.RefReadOnlyParameter => "ref readonly ",
                    _ => string.Empty
                };
                var parameterType = parameter.Type.ContainsErrorType() &&
                    TryGetParameterDeclaredTypeSyntaxDisplay(parameter, out var declaredTypeDisplay)
                        ? declaredTypeDisplay
                        : FormatType(parameter.Type, format);
                var defaultValue = FormatParameterDefaultValue(parameter, format);
                return $"{paramsPrefix}{refPrefix}{parameter.Name}: {parameterType}{defaultValue}";
            }));
    }

    private static string FormatParameterDefaultValue(IParameterSymbol parameter, SymbolDisplayFormat format)
    {
        if (!parameter.HasExplicitDefaultValue)
            return string.Empty;

        var parameterFormat = format.WithParameterOptions(
            format.ParameterOptions |
            SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeType |
            SymbolDisplayParameterOptions.IncludeDefaultValue |
            SymbolDisplayParameterOptions.IncludeParamsRefOut);
        var parameterDisplay = parameter.ToDisplayString(parameterFormat);
        var marker = " = ";
        var markerIndex = parameterDisplay.IndexOf(marker, StringComparison.Ordinal);
        return markerIndex < 0
            ? string.Empty
            : parameterDisplay[markerIndex..];
    }

    private static string? FormatDocumentation(DocumentationComment? documentation)
    {
        return DocumentationMarkdownFormatter.FormatForEditor(documentation);
    }

    private static string? FormatCaptureText(
        ImmutableArray<ISymbol> capturedVariables,
        bool isCapturedVariable)
    {
        if (capturedVariables.IsDefaultOrEmpty)
            return isCapturedVariable ? "Captured variable" : null;

        var captures = string.Join(
            ", ",
            capturedVariables
                .Select(static symbol => symbol.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal));

        if (string.IsNullOrWhiteSpace(captures))
            return isCapturedVariable ? "Captured variable" : null;

        return $"Captures: `{captures}`";
    }
}
