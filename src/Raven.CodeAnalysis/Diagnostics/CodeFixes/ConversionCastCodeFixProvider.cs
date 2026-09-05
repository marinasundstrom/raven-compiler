using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class ConversionCastCodeFixProvider : CodeFixProvider
{
    private const string AddCastEquivalenceKey = nameof(ConversionCastCodeFixProvider) + ".AddCast";
    private const string RemoveCastEquivalenceKey = nameof(ConversionCastCodeFixProvider) + ".RemoveCast";

    private static readonly ImmutableArray<string> FixableIds =
    [
        CompilerDiagnostics.ExplicitConversionExists.Id,
        CompilerDiagnostics.RedundantExplicitCast.Id
    ];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!diagnostic.Location.IsInSource)
            return;

        if (string.Equals(diagnostic.Id, CompilerDiagnostics.ExplicitConversionExists.Id, StringComparison.OrdinalIgnoreCase))
        {
            RegisterAddExplicitCastFix(context, diagnostic);
            return;
        }

        if (string.Equals(diagnostic.Id, CompilerDiagnostics.RedundantExplicitCast.Id, StringComparison.OrdinalIgnoreCase))
            RegisterRemoveRedundantCastFix(context, diagnostic);
    }

    private static void RegisterAddExplicitCastFix(CodeFixContext context, Diagnostic diagnostic)
    {
        var args = diagnostic.GetMessageArgs();
        if (args.Length < 2 || args[1] is not string targetType || string.IsNullOrWhiteSpace(targetType))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var expression = FindExpressionAtDiagnostic(root, diagnostic.Location.SourceSpan);
        if (expression is null || expression is CastExpressionSyntax)
            return;

        var replacement = $"({targetType}){expression}";
        var change = new TextChange(expression.Span, replacement);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                $"Add explicit cast to '{targetType}'",
                context.Document.Id,
                change,
                $"{AddCastEquivalenceKey}:{targetType}"));
    }

    private static void RegisterRemoveRedundantCastFix(CodeFixContext context, Diagnostic diagnostic)
    {
        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var castExpression = root.FindNode(diagnostic.Location.SourceSpan)
            ?.FirstAncestorOrSelf<CastExpressionSyntax>();
        if (castExpression is null)
            return;

        var innerText = castExpression.Expression.ToString();
        var change = new TextChange(castExpression.Span, innerText);

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Remove redundant explicit cast",
                context.Document.Id,
                change,
                RemoveCastEquivalenceKey));
    }

    private static ExpressionSyntax? FindExpressionAtDiagnostic(SyntaxNode root, TextSpan span)
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        if (node is ExpressionSyntax expression)
            return expression;

        return node?.FirstAncestorOrSelf<ExpressionSyntax>();
    }
}
