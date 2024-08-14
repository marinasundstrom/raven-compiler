namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public class ElseClauseSyntax : SyntaxNodeWithChildren
{
    public ElseClauseSyntax(SyntaxToken elseToken, StatementSyntax? parseStatement)
    {
        this.ElseToken = AddChild(elseToken);
        this.ParseStatement = AddChild(parseStatement!);
    }

    public override SyntaxKind Kind => SyntaxKind.ElseClauseSyntax;

    public SyntaxToken ElseToken { get; }

    public StatementSyntax? ParseStatement { get; }

    public override string ToFullString()
    {
        throw new System.NotImplementedException();
    }
}