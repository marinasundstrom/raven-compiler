using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferIsNullOverEqualityCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(PreferIsNullOverEqualityCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [PreferIsNullOverEqualityAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferIsNullOverEqualityAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var binary = root.FindNode(diagnostic.Location.SourceSpan)
            ?.AncestorsAndSelf()
            .OfType<InfixOperatorExpressionSyntax>()
            .FirstOrDefault();

        if (binary is null)
            return;

        if (binary.OperatorToken.Kind is not (SyntaxKind.EqualsEqualsToken or SyntaxKind.NotEqualsToken))
            return;

        var leftIsNull = IsNullLiteral(binary.Left);
        var rightIsNull = IsNullLiteral(binary.Right);
        if (!leftIsNull && !rightIsNull)
            return;

        var expression = leftIsNull ? binary.Right : binary.Left;
        var replacement = binary.OperatorToken.Kind == SyntaxKind.EqualsEqualsToken
            ? $"{expression} is null"
            : $"{expression} is not null";

        var change = new TextChange(binary.Span, replacement);
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Use strict null check",
                context.Document.Id,
                change,
                EquivalenceKey));
    }

    private static bool IsNullLiteral(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression.Kind == SyntaxKind.NullLiteralExpression;
    }
}
