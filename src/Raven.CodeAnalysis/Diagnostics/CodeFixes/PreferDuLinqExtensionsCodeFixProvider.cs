using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class PreferDuLinqExtensionsCodeFixProvider : CodeFixProvider
{
    private static readonly ImmutableArray<string> FixableIds = [PreferDuLinqExtensionsAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, PreferDuLinqExtensionsAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var args = diagnostic.GetMessageArgs();
        if (args.Length < 1 || args[0] is not string preferredName || string.IsNullOrWhiteSpace(preferredName))
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var token = root.FindToken(diagnostic.Location.SourceSpan.Start);
        var simpleName = token.Parent?.FirstAncestorOrSelf<SimpleNameSyntax>();

        var replacementSpan = simpleName?.Identifier.Span ?? diagnostic.Location.SourceSpan;
        var invocation = simpleName?.FirstAncestorOrSelf<InvocationExpressionSyntax>();

        if (!preferredName.EndsWith("OrError", StringComparison.Ordinal) || invocation is null)
        {
            var renameChange = new TextChange(replacementSpan, preferredName);
            context.RegisterCodeFix(
                CodeAction.CreateTextChange(
                    $"Replace with '{preferredName}'",
                    context.Document.Id,
                    renameChange,
                    $"{nameof(PreferDuLinqExtensionsCodeFixProvider)}.Rename:{preferredName}"));
            return;
        }

        var closeParenPosition = invocation.ArgumentList.CloseParenToken.Span.Start;
        var hasExistingArguments = invocation.ArgumentList.Arguments.Count > 0;
        var placeholderArgument = hasExistingArguments
            ? ", () => \"TODO: provide error\""
            : "() => \"TODO: provide error\"";

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Replace with '{preferredName}' and add error placeholder",
                (solution, _) =>
                {
                    var document = solution.GetDocument(context.Document.Id);
                    if (document is null)
                        return solution;

                    var updatedText = document.Text
                        .WithChange(new TextChange(new TextSpan(closeParenPosition, 0), placeholderArgument))
                        .WithChange(new TextChange(replacementSpan, preferredName));

                    return solution.WithDocumentText(context.Document.Id, updatedText);
                },
                $"{nameof(PreferDuLinqExtensionsCodeFixProvider)}.Placeholder:{preferredName}"));
    }
}
