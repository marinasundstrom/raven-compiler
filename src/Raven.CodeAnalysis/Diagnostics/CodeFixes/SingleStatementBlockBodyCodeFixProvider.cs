using System.Collections.Immutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Diagnostics;

public sealed class SingleStatementBlockBodyCodeFixProvider : CodeFixProvider
{
    private const string EquivalenceKey = nameof(SingleStatementBlockBodyCodeFixProvider);
    private static readonly ImmutableArray<string> FixableIds = [SingleStatementBlockBodyAnalyzer.DiagnosticId];

    public override IEnumerable<string> FixableDiagnosticIds => FixableIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override void RegisterCodeFixes(CodeFixContext context)
    {
        var diagnostic = context.Diagnostic;
        if (!string.Equals(diagnostic.Id, SingleStatementBlockBodyAnalyzer.DiagnosticId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!diagnostic.Location.IsInSource)
            return;

        var syntaxTree = context.Document.GetSyntaxTreeAsync(context.CancellationToken).GetAwaiter().GetResult();
        var root = syntaxTree?.GetRoot(context.CancellationToken);
        if (root is null)
            return;

        var anchorNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var block = TryGetBodyBlock(anchorNode) ?? anchorNode.FirstAncestorOrSelf<BlockStatementSyntax>();
        if (block is null)
            return;

        if (!SingleStatementBlockBodyAnalyzer.TryGetConvertibleExpression(block, out var expression))
            return;

        var change = new TextChange(block.Span, $"=> {expression}");
        context.RegisterCodeFix(
            CodeAction.CreateTextChange(
                "Convert to expression body",
                context.Document.Id,
                change,
                EquivalenceKey));
    }

    internal static BlockStatementSyntax? TryGetBodyBlock(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax method => method.Body,
            FunctionStatementSyntax function => function.Body,
            AccessorDeclarationSyntax accessor => accessor.Body,
            ConstructorDeclarationSyntax constructor => constructor.Body,
            OperatorDeclarationSyntax op => op.Body,
            ConversionOperatorDeclarationSyntax conversion => conversion.Body,
            _ => node.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Body
                ?? node.FirstAncestorOrSelf<FunctionStatementSyntax>()?.Body
                ?? node.FirstAncestorOrSelf<AccessorDeclarationSyntax>()?.Body
                ?? node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>()?.Body
                ?? node.FirstAncestorOrSelf<OperatorDeclarationSyntax>()?.Body
                ?? node.FirstAncestorOrSelf<ConversionOperatorDeclarationSyntax>()?.Body
        };
    }
}
