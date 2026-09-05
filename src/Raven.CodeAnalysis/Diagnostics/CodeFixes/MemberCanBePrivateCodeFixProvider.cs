using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class MemberCanBePrivateCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(MemberCanBePrivateCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [MemberCanBePrivateAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, MemberCanBePrivateAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var memberDecl = node?.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (memberDecl is null)
            return;

        var change = MemberModifierCodeFixHelper.CreateMakePrivateChange(memberDecl);
        if (change is null)
            return;

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Make member private",
                context.Document.Id,
                change.Value,
                EquivalenceKey));
    }
}
