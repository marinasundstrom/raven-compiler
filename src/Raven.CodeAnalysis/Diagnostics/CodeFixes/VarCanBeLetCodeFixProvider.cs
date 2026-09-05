using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class VarCanBeLetCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(VarCanBeLetCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [VarCanBeLetAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, VarCanBeLetAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var span = diagnostic.Location.SourceSpan;
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declaration = node?.FirstAncestorOrSelf<VariableDeclarationSyntax>()
            ?? node?.FirstAncestorOrSelf<VariableDeclaratorSyntax>()?.FirstAncestorOrSelf<VariableDeclarationSyntax>();
        if (declaration is null)
            return;

        if (!declaration.BindingKeyword.IsKind(SyntaxKind.VarKeyword))
            return;

        var change = new TextChange(declaration.BindingKeyword.Span, "let");
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Replace 'var' with 'let'",
                context.Document.Id,
                change,
                EquivalenceKey));
    }
}
