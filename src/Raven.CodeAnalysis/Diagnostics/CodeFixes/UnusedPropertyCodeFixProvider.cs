using System;
using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class UnusedPropertyCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(UnusedPropertyCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [UnusedPropertyAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, UnusedPropertyAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var propertyDecl = node?.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
        if (propertyDecl is null)
            return;

        var span = GetRemovalSpan(propertyDecl);
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Remove unused property",
                context.Document.Id,
                new TextChange(span, string.Empty),
                EquivalenceKey));
    }

    private static TextSpan GetRemovalSpan(PropertyDeclarationSyntax propertyDecl)
    {
        if (propertyDecl.Parent is TypeDeclarationSyntax containingType &&
            containingType.Members.Count == 1 &&
            containingType.Members[0].Span == propertyDecl.Span &&
            TryGetBodyTokens(containingType, out var openBraceToken, out var closeBraceToken))
        {
            return TextSpan.FromBounds(openBraceToken.Span.End, closeBraceToken.FullSpan.Start);
        }

        return propertyDecl.FullSpan;
    }

    private static bool TryGetBodyTokens(
        TypeDeclarationSyntax containingType,
        out SyntaxToken openBraceToken,
        out SyntaxToken closeBraceToken)
    {
        switch (containingType)
        {
            case ClassDeclarationSyntax classDeclaration:
                openBraceToken = classDeclaration.OpenBraceToken;
                closeBraceToken = classDeclaration.CloseBraceToken;
                return true;
            case StructDeclarationSyntax structDeclaration:
                openBraceToken = structDeclaration.OpenBraceToken;
                closeBraceToken = structDeclaration.CloseBraceToken;
                return true;
            case RecordDeclarationSyntax recordDeclaration:
                openBraceToken = recordDeclaration.OpenBraceToken;
                closeBraceToken = recordDeclaration.CloseBraceToken;
                return true;
            default:
                openBraceToken = default;
                closeBraceToken = default;
                return false;
        }
    }
}
