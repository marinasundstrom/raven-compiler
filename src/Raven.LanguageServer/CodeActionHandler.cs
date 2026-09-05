using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json.Linq;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;
using CodeDiagnosticSeverity = Raven.CodeAnalysis.DiagnosticSeverity;
using CodeFixAction = Raven.CodeAnalysis.CodeAction;
using CodeLocation = Raven.CodeAnalysis.Location;
using LspCodeAction = OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeAction;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using TextDocumentSelector = OmniSharp.Extensions.LanguageServer.Protocol.Models.TextDocumentSelector;

namespace Raven.LanguageServer;

internal sealed class CodeActionHandler : ICodeActionHandler
{
    private const string ShowMacroExpansionCommand = "raven.showMacroExpansion";
    private const string ShowCodeActionPreviewCommand = "raven.showCodeActionPreview";
    private readonly DocumentStore _documents;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger<CodeActionHandler> _logger;

    public CodeActionHandler(DocumentStore documents, WorkspaceManager workspaceManager, ILogger<CodeActionHandler> logger)
    {
        _documents = documents;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public CodeActionRegistrationOptions GetRegistrationOptions(CodeActionCapability capability, ClientCapabilities clientCapabilities)
        => new()
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("raven"),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix, CodeActionKind.RefactorRewrite, CodeActionKind.SourceFixAll)
        };

    public void SetCapability(CodeActionCapability capability)
    {
    }

    public async Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _documents.GetAnalysisContextAsync(request.TextDocument.Uri, cancellationToken).ConfigureAwait(false);
            if (context is null)
                return new CommandOrCodeActionContainer();

            var document = context.Value.Document;
            var syntaxTree = context.Value.SyntaxTree;
            var documentText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var selectionSpan = GetRequestedSpan(documentText, request.Range);
            var supportsRefactorRewrite = SupportsKind(request.Context?.Only, CodeActionKind.RefactorRewrite);
            var refactorRewriteExplicitlyRequested = IsKindExplicitlyRequested(request.Context?.Only, CodeActionKind.RefactorRewrite);
            var supportsQuickFix = SupportsKind(request.Context?.Only, CodeActionKind.QuickFix);
            var supportsFixAll = SupportsKind(request.Context?.Only, CodeActionKind.SourceFixAll);
            var fixAllExplicitlyRequested = IsKindExplicitlyRequested(request.Context?.Only, CodeActionKind.SourceFixAll);

            var filteredFixes = supportsQuickFix || supportsFixAll
                ? GetQuickFixesForRequest(request, documentText, syntaxTree, cancellationToken)
                    .Where(fix => IsFixInRequestedRange(fix, request.Range, documentText))
                    .Where(fix => MatchesRequestedDiagnostics(fix, request.Context?.Diagnostics, documentText))
                    .ToArray()
                : [];
            var fixAllCandidates = filteredFixes;
            if (supportsFixAll &&
                fixAllExplicitlyRequested &&
                _workspaceManager.TryGetCodeFixes(
                    request.TextDocument.Uri,
                    out var documentFixes,
                    cancellationToken: cancellationToken))
            {
                fixAllCandidates = documentFixes.ToArray();
            }
            var filteredRefactorings =
                supportsRefactorRewrite &&
                _workspaceManager.TryGetRefactorings(request.TextDocument.Uri, selectionSpan, out var refactorings, cancellationToken)
                    ? refactorings.ToArray()
                    : [];

            var actions = new List<CommandOrCodeAction>(filteredFixes.Length + filteredRefactorings.Length + 1);

            if (supportsRefactorRewrite)
            {
                DocumentStore.DocumentSemanticAccess? semanticAccess = null;
                try
                {
                    semanticAccess = refactorRewriteExplicitlyRequested
                        ? await _documents.EnterDocumentSemanticModelAccessAsync(
                            request.TextDocument.Uri,
                            context.Value,
                            cancellationToken,
                            "codeAction").ConfigureAwait(false)
                        : await _documents.TryEnterDocumentSemanticModelAccessAsync(
                            request.TextDocument.Uri,
                            context.Value,
                            cancellationToken,
                            "codeAction").ConfigureAwait(false);

                    var semanticModel = semanticAccess?.SemanticModel;
                    var root = syntaxTree.GetRoot(cancellationToken);
                    if (semanticModel is not null &&
                        TryCreateMacroExpansionAction(request.TextDocument.Uri, documentText, semanticModel, root, request.Range, out var macroAction))
                    {
                        actions.Add(macroAction);
                    }
                }
                finally
                {
                    semanticAccess?.Dispose();
                }
            }

            if (supportsQuickFix)
            {
                foreach (var fix in filteredFixes)
                {
                    var action = await TryCreateLspCodeActionAsync(
                        request.TextDocument.Uri,
                        document,
                        fix.DocumentId,
                        fix.Action,
                        CodeActionKind.QuickFix,
                        fix.Diagnostic,
                        cancellationToken).ConfigureAwait(false);
                    if (action is not null)
                    {
                        actions.Add(action);

                        var preview = await TryCreatePreviewCommandAsync(
                            request.TextDocument.Uri,
                            document,
                            fix.DocumentId,
                            fix.Action,
                            cancellationToken).ConfigureAwait(false);
                        if (preview is not null)
                            actions.Add(preview);
                    }
                }
            }

            if (supportsFixAll)
            {
                var seenFixAllActions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fix in fixAllCandidates)
                {
                    var equivalenceKey = fix.Action.EquivalenceKey;
                    if (string.IsNullOrWhiteSpace(equivalenceKey) ||
                        !seenFixAllActions.Add($"{fix.Provider.GetType().AssemblyQualifiedName}|{equivalenceKey}"))
                    {
                        continue;
                    }

                    if (!_workspaceManager.TryGetFixAll(
                            request.TextDocument.Uri,
                            fix,
                            FixAllScope.Document,
                            out var fixAllAction,
                            cancellationToken) ||
                        fixAllAction is null)
                    {
                        continue;
                    }

                    var titledAction = CodeFixAction.Create(
                        $"Fix all in document: {fix.Action.Title}",
                        fixAllAction.GetChangedSolution,
                        equivalenceKey);
                    var action = await TryCreateLspCodeActionAsync(
                        request.TextDocument.Uri,
                        document,
                        fix.DocumentId,
                        titledAction,
                        CodeActionKind.SourceFixAll,
                        diagnostic: null,
                        cancellationToken).ConfigureAwait(false);
                    if (action is not null)
                        actions.Add(action);
                }
            }

            foreach (var refactoring in filteredRefactorings)
            {
                var action = await TryCreateLspCodeActionAsync(
                    request.TextDocument.Uri,
                    document,
                    refactoring.DocumentId,
                    refactoring.Action,
                    CodeActionKind.RefactorRewrite,
                    diagnostic: null,
                    cancellationToken).ConfigureAwait(false);
                if (action is not null)
                {
                    actions.Add(action);

                    var preview = await TryCreatePreviewCommandAsync(
                        request.TextDocument.Uri,
                        document,
                        refactoring.DocumentId,
                        refactoring.Action,
                        cancellationToken).ConfigureAwait(false);
                    if (preview is not null)
                        actions.Add(preview);
                }
            }

            return new CommandOrCodeActionContainer(actions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CommandOrCodeActionContainer();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "CodeAction request failed for {Uri} at range {StartLine}:{StartChar}-{EndLine}:{EndChar}.",
                request.TextDocument.Uri,
                request.Range.Start.Line,
                request.Range.Start.Character,
                request.Range.End.Line,
                request.Range.End.Character);
            return new CommandOrCodeActionContainer();
        }
    }

    private CodeFix[] GetQuickFixesForRequest(
        CodeActionParams request,
        SourceText documentText,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var diagnostics = CreateDiagnosticsFromRequest(request.Context?.Diagnostics, documentText, syntaxTree);
        if (diagnostics.Length > 0)
        {
            return _workspaceManager.TryGetCodeFixesForDiagnostics(request.TextDocument.Uri, diagnostics, out var requestFixes, cancellationToken)
                ? requestFixes.ToArray()
                : [];
        }

        return [];
    }

    private static ImmutableArray<CodeDiagnostic> CreateDiagnosticsFromRequest(
        Container<LspDiagnostic>? requestDiagnostics,
        SourceText documentText,
        SyntaxTree syntaxTree)
    {
        if (requestDiagnostics is null || !requestDiagnostics.Any())
            return ImmutableArray<CodeDiagnostic>.Empty;

        var builder = ImmutableArray.CreateBuilder<CodeDiagnostic>();
        foreach (var diagnostic in requestDiagnostics)
        {
            var id = diagnostic.Code?.String;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var start = PositionHelper.ToOffset(documentText, diagnostic.Range.Start);
            var end = PositionHelper.ToOffset(documentText, diagnostic.Range.End);
            if (end < start)
                (start, end) = (end, start);

            var hasRavenData = RavenDiagnosticData.TryRead(
                diagnostic.Data,
                out var messageFormat,
                out var messageArguments,
                out var properties);
            var descriptor = DiagnosticDescriptor.Create(
                id,
                title: diagnostic.Message,
                description: null,
                helpLinkUri: string.Empty,
                messageFormat: hasRavenData ? messageFormat : "{0}",
                category: "LanguageServer",
                defaultSeverity: MapSeverity(diagnostic.Severity));
            var location = CodeLocation.Create(syntaxTree, new TextSpan(start, end - start));
            builder.Add(new CodeDiagnostic(
                descriptor,
                location,
                hasRavenData ? messageArguments : [diagnostic.Message],
                severity: null,
                properties: properties));
        }

        return builder.ToImmutable();
    }

    private static CodeDiagnosticSeverity MapSeverity(LspDiagnosticSeverity? severity)
        => severity switch
        {
            LspDiagnosticSeverity.Error => CodeDiagnosticSeverity.Error,
            LspDiagnosticSeverity.Warning => CodeDiagnosticSeverity.Warning,
            LspDiagnosticSeverity.Hint => CodeDiagnosticSeverity.Hidden,
            _ => CodeDiagnosticSeverity.Info
        };

    private static bool IsFixInRequestedRange(CodeFix fix, LspRange requestRange, SourceText text)
    {
        if (!fix.Diagnostic.Location.IsInSource)
            return false;

        var fixSpan = fix.Diagnostic.Location.SourceSpan;
        var requestStart = PositionHelper.ToOffset(text, requestRange.Start);
        var requestEnd = PositionHelper.ToOffset(text, requestRange.End);
        if (requestEnd < requestStart)
            (requestStart, requestEnd) = (requestEnd, requestStart);

        return SpansOverlap(fixSpan.Start, fixSpan.End, requestStart, requestEnd);
    }

    private static bool MatchesRequestedDiagnostics(CodeFix fix, Container<LspDiagnostic>? requestedDiagnostics, SourceText text)
    {
        if (requestedDiagnostics is null || !requestedDiagnostics.Any())
            return true;

        foreach (var diagnostic in requestedDiagnostics)
        {
            var diagnosticCode = diagnostic.Code?.String;
            var diagnosticStart = PositionHelper.ToOffset(text, diagnostic.Range.Start);
            var diagnosticEnd = PositionHelper.ToOffset(text, diagnostic.Range.End);
            if (diagnosticEnd < diagnosticStart)
                (diagnosticStart, diagnosticEnd) = (diagnosticEnd, diagnosticStart);

            var fixSpan = fix.Diagnostic.Location.SourceSpan;
            var codeMatches = string.IsNullOrWhiteSpace(diagnosticCode) ||
                string.Equals(diagnosticCode, fix.Diagnostic.Id, StringComparison.OrdinalIgnoreCase);

            if (codeMatches && SpansOverlap(fixSpan.Start, fixSpan.End, diagnosticStart, diagnosticEnd))
                return true;
        }

        return false;
    }

    private static bool SpansOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd)
        => leftStart <= rightEnd && rightStart <= leftEnd;

    private static TextSpan GetRequestedSpan(SourceText text, LspRange requestRange)
    {
        var start = PositionHelper.ToOffset(text, requestRange.Start);
        var end = PositionHelper.ToOffset(text, requestRange.End);
        if (end < start)
            (start, end) = (end, start);

        return new TextSpan(start, end - start);
    }

    private static bool SupportsKind(Container<CodeActionKind>? requestedKinds, CodeActionKind kind)
    {
        if (requestedKinds is null || !requestedKinds.Any())
            return true;

        return IsKindExplicitlyRequested(requestedKinds, kind);
    }

    private static bool IsKindExplicitlyRequested(Container<CodeActionKind>? requestedKinds, CodeActionKind kind)
    {
        if (requestedKinds is null || !requestedKinds.Any())
            return false;

        var kindText = kind.ToString();
        return requestedKinds.Any(requestedKind =>
        {
            var requestedText = requestedKind.ToString();
            return kindText.StartsWith(requestedText, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool TryCreateMacroExpansionAction(
        DocumentUri uri,
        SourceText documentText,
        SemanticModel semanticModel,
        SyntaxNode root,
        LspRange requestRange,
        out CommandOrCodeAction action)
    {
        action = default!;

        if (!MacroExpansionDisplayService.TryCreateForRange(documentText, semanticModel, root, requestRange, out var display))
            return false;

        action = new CommandOrCodeAction(new Command
        {
            Name = ShowMacroExpansionCommand,
            Title = $"Show macro expansion for {display.InvocationDisplay}",
            Arguments = new JArray(uri.ToString(), display.MacroName, display.FullText)
        });

        return true;
    }

    private async Task<CommandOrCodeAction?> TryCreateLspCodeActionAsync(
        DocumentUri uri,
        Document currentDocument,
        DocumentId documentId,
        CodeFixAction action,
        CodeActionKind kind,
        CodeDiagnostic? diagnostic,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentSolution = currentDocument.Project.Solution;
            var updatedSolution = action.GetChangedSolution(currentSolution, cancellationToken);
            if (updatedSolution.Version == currentSolution.Version)
                return null;

            var updatedDocument = updatedSolution.GetDocument(documentId);
            if (updatedDocument is null)
                return null;

            var currentDocumentForFix = currentSolution.GetDocument(documentId);
            if (currentDocumentForFix is null)
                return null;

            var currentText = await currentDocumentForFix.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var textChanges = await updatedDocument.GetTextChangesAsync(currentDocumentForFix, cancellationToken).ConfigureAwait(false);
            if (textChanges.Count == 0)
                return null;

            var edits = textChanges
                .Select(change => new TextEdit
                {
                    NewText = change.NewText,
                    Range = PositionHelper.ToRange(currentText, change.Span)
                })
                .ToArray();

            var workspaceEdit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [uri] = edits
                }
            };

            var codeAction = diagnostic is null
                ? new LspCodeAction
                {
                    Title = action.Title,
                    Kind = kind,
                    Edit = workspaceEdit
                }
                : new LspCodeAction
                {
                    Title = action.Title,
                    Kind = kind,
                    Edit = workspaceEdit,
                    Diagnostics = new Container<LspDiagnostic>(
                        new LspDiagnostic
                        {
                            Message = diagnostic.GetMessage(),
                            Source = "raven",
                            Code = diagnostic.Id,
                            Range = PositionHelper.ToRange(currentText, diagnostic.Location.SourceSpan),
                            Data = RavenDiagnosticData.Create(diagnostic)
                        })
                };

            return new CommandOrCodeAction(codeAction);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping code action '{Title}' for {Uri}; the action could not be materialized.",
                action.Title,
                uri);
            return null;
        }
    }

    private async Task<CommandOrCodeAction?> TryCreatePreviewCommandAsync(
        DocumentUri uri,
        Document currentDocument,
        DocumentId documentId,
        CodeFixAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentSolution = currentDocument.Project.Solution;
            var updatedSolution = action.GetChangedSolution(currentSolution, cancellationToken);
            if (updatedSolution.Version == currentSolution.Version)
                return null;

            var updatedDocument = updatedSolution.GetDocument(documentId);
            var currentDocumentForAction = currentSolution.GetDocument(documentId);
            if (updatedDocument is null || currentDocumentForAction is null)
                return null;

            var currentText = await currentDocumentForAction.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var updatedText = await updatedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(currentText.ToString(), updatedText.ToString(), StringComparison.Ordinal))
                return null;

            return new CommandOrCodeAction(new Command
            {
                Name = ShowCodeActionPreviewCommand,
                Title = $"Preview: {action.Title}",
                Arguments = new JArray(uri.ToString(), action.Title, currentText.ToString(), updatedText.ToString())
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping code action preview for '{Title}' on {Uri}; the action could not be materialized.",
                action.Title,
                uri);
            return null;
        }
    }
}
