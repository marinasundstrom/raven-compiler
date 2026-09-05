using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferTargetTypedUnionCaseCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferTargetTypedUnionCaseCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [PreferTargetTypedUnionCaseAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferTargetTypedUnionCaseAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        string? rewritten = null;
        if (SuggestionsDiagnosticProperties.TryGetRewriteSuggestion(diagnostic, out _, out var rewrittenFromProperties))
            rewritten = rewrittenFromProperties;

        if (string.IsNullOrWhiteSpace(rewritten))
        {
            var args = diagnostic.GetMessageArgs();
            if (args.Length >= 2 && args[1] is string rewrittenFromArgs && !string.IsNullOrWhiteSpace(rewrittenFromArgs))
                rewritten = rewrittenFromArgs;
        }

        if (string.IsNullOrWhiteSpace(rewritten))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        var declaration = token.Parent?.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>();
        if (declaration is null)
            return;

        var change = new TextChange(declaration.Span, rewritten);
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Rewrite declaration using target-typed union case construction",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
