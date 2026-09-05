using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferLoopOverWhileTrueCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferLoopOverWhileTrueCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [PreferLoopOverWhileTrueAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferLoopOverWhileTrueAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var whileStatement = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            ?.FirstAncestorOrSelf<WhileStatementSyntax>();

        if (whileStatement is null || !PreferLoopOverWhileTrueAnalyzer.IsTrueLiteral(whileStatement.Condition))
            return;

        var headerSpan = TextSpan.FromBounds(
            whileStatement.WhileKeyword.SpanStart,
            whileStatement.Condition.Span.End);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Replace 'while true' with 'loop'",
                context.Document.Id,
                new TextChange(headerSpan, "loop"),
                EquivalenceKey));
    }
}
