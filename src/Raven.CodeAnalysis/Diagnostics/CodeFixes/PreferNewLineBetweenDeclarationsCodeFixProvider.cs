using System.Collections.Immutable;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferNewLineBetweenDeclarationsCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferNewLineBetweenDeclarationsCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [PreferNewLineBetweenDeclarationsAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferNewLineBetweenDeclarationsAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var documentText = context.Document.GetTextAsync(context.CancellationToken).GetAwaiter().GetResult().ToString();
        var newLine = documentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var change = new TextChange(diagnostic.Location.SourceSpan, $";{newLine}");
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Insert newline between declarations",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
