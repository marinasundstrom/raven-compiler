using System.Collections.Immutable;

using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PragmaWarningSuppressionCodeFixProvider : CodeFixProvider
{
    private static readonly ImmutableHashSet<string> NonSuppressibleIds =
    [
        CompilerDiagnostics.UnreachableCodeDetected.Id
    ];

    private static readonly ImmutableArray<string> FixableIds = ["*"];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!diagnostic.Location.IsInSource)
            return;
        if (diagnostic.Severity == DiagnosticSeverity.Error)
            return;
        if (NonSuppressibleIds.Contains(diagnostic.Id))
            return;

        var text = context.Document.Text.ToString();
        if (!TryCreateInsertionPlan(text, diagnostic.Location.SourceSpan.Start, diagnostic.Id, out var plan))
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Suppress {diagnostic.Id} with pragma",
                (solution, _) =>
                {
                    var document = solution.GetDocument(context.Document.Id);
                    if (document is null)
                        return solution;

                    var updatedText = document.Text.WithChange(new TextChange(new TextSpan(plan.InsertPosition, 0), plan.InsertText));
                    return solution.WithDocumentText(context.Document.Id, updatedText);
                },
                $"{nameof(PragmaWarningSuppressionCodeFixProvider)}:{diagnostic.Id}"));
    }

    private static bool TryCreateInsertionPlan(string text, int position, string diagnosticId, out InsertionPlan plan)
    {
        plan = default;

        if (string.IsNullOrWhiteSpace(diagnosticId))
            return false;

        if (position < 0 || position > text.Length)
            return false;

        var lineStart = FindLineStart(text, position);
        var lineEnd = FindLineEnd(text, position);
        if (HasMatchingDisableNextLinePragma(text, lineStart, diagnosticId))
            return false;

        var newline = DetectPreferredNewline(text);
        var indent = GetLeadingIndent(text, lineStart, lineEnd);
        var insertText = $"{indent}#pragma warning disable-next-line {diagnosticId}{newline}";

        plan = new InsertionPlan(lineStart, insertText);

        return true;
    }

    private static int FindLineStart(string text, int position)
    {
        var index = Math.Min(position, text.Length);
        while (index > 0)
        {
            var previous = text[index - 1];
            if (previous is '\n' or '\r')
                break;

            index--;
        }

        return index;
    }

    private static bool HasMatchingDisableNextLinePragma(string text, int lineStart, string diagnosticId)
    {
        if (lineStart <= 0)
            return false;

        var previousLineEnd = lineStart;
        if (previousLineEnd > 0 && text[previousLineEnd - 1] == '\n')
            previousLineEnd--;

        if (previousLineEnd > 0 && text[previousLineEnd - 1] == '\r')
            previousLineEnd--;

        var previousLineStart = FindLineStart(text, previousLineEnd);
        var previousLine = text[previousLineStart..previousLineEnd].Trim();
        if (!previousLine.StartsWith("#pragma warning disable-next-line", StringComparison.OrdinalIgnoreCase))
            return false;

        var idsText = previousLine["#pragma warning disable-next-line".Length..];
        var ids = idsText.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Any(id => string.Equals(id, diagnosticId, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindLineEnd(string text, int position)
    {
        var index = Math.Min(position, text.Length);
        while (index < text.Length)
        {
            var current = text[index];
            if (current is '\n' or '\r')
                break;

            index++;
        }

        return index;
    }

    private static string DetectPreferredNewline(string text)
    {
        if (text.Contains("\r\n", StringComparison.Ordinal))
            return "\r\n";

        if (text.Contains('\r'))
            return "\r";

        return "\n";
    }

    private static string GetLeadingIndent(string text, int lineStart, int lineEnd)
    {
        var index = lineStart;
        while (index < lineEnd && (text[index] == ' ' || text[index] == '\t'))
            index++;

        return text[lineStart..index];
    }

    private readonly record struct InsertionPlan(int InsertPosition, string InsertText);
}
