namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class LetDeclarationExpressionSyntax : ExpressionSyntax
{
    public LetDeclarationExpressionSyntax(
        SyntaxToken letKeyword, ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        LetKeyword = AddChild(0, letKeyword);
        TargetExpression = AddChild(1, targetExpression);
        AssignmentToken = AddChild(2, assignmentToken);
        AssignmentExpression = AddChild(3, assignmentExpression);
    }

    public override SyntaxKind Kind => SyntaxKind.LetDeclarationExpression;

    public SyntaxToken LetKeyword { get; }

    public ExpressionSyntax TargetExpression { get; }

    public SyntaxToken AssignmentToken { get; }

    public ExpressionSyntax AssignmentExpression { get; }
}