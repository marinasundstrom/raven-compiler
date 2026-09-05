using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class MemberCanBeStaticCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(MemberCanBeStaticCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [MemberCanBeStaticAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, MemberCanBeStaticAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var methodDecl = node?.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodDecl is null)
            return;

        var change = MemberModifierCodeFixHelper.CreateMakeStaticChange(methodDecl);
        if (change is null)
            return;

        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Make method static",
                context.Document.Id,
                change.Value,
                EquivalenceKey));
    }
}
