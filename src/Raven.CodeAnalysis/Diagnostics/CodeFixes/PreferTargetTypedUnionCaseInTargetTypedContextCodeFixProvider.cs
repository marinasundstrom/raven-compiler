using System.Collections.Immutable;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferTargetTypedUnionCaseInTargetTypedContextCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferTargetTypedUnionCaseInTargetTypedContextCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds =
        [PreferTargetTypedUnionCaseInTargetTypedContextAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferTargetTypedUnionCaseInTargetTypedContextAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
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

        var change = new TextChange(diagnostic.Location.SourceSpan, rewritten);
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Rewrite to target-typed union case syntax",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
