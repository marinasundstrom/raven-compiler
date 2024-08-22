namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public class BlockSyntax : StatementSyntax
{
    public BlockSyntax(SyntaxToken openBraceToken, SyntaxToken closeBraceToken)
    {
        OpenBraceToken = AddChild(0, openBraceToken);
        CloseBraceToken = AddChild(1, closeBraceToken);
    }

    public override SyntaxKind Kind => SyntaxKind.BlockStatementSyntax;

    public SyntaxToken OpenBraceToken { get; }

    public SyntaxToken CloseBraceToken { get; }
}