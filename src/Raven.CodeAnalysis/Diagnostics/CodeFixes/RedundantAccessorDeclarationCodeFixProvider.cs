using System.Collections.Immutable;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class RedundantAccessorDeclarationCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(RedundantAccessorDeclarationCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [RedundantAccessorDeclarationAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, RedundantAccessorDeclarationAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var span = diagnostic.Location.SourceSpan;
        var sourceText = context.Document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult().ToString();
        if (span.Start > 0 && sourceText[span.Start - 1] == ' ')
            span = new TextSpan(span.Start - 1, span.Length + 1);

        var change = new TextChange(span, string.Empty);
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Remove redundant accessor list",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
