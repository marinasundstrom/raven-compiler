using System.Collections.Immutable;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferLetInsteadOfValCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferLetInsteadOfValCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds =
        [PreferLetInsteadOfValAnalyzer.PreferLetInsteadOfValDiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferLetInsteadOfValAnalyzer.PreferLetInsteadOfValDiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var change = new TextChange(diagnostic.Location.SourceSpan, "let");
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Replace 'val' with 'let'",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
