namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class LetDeclarationExpressionSyntax : ExpressionSyntax
{
    public LetDeclarationExpressionSyntax(
        SyntaxToken letKeyword, ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        LetKeyword = AddChild(letKeyword);
        TargetExpression = AddChild(targetExpression);
        AssignmentToken = AddChild(assignmentToken);
        AssignmentExpression = AddChild(assignmentExpression);
    }

    public override SyntaxKind Kind => SyntaxKind.LetDeclarationExpression;

    public SyntaxToken LetKeyword { get; }

    public ExpressionSyntax TargetExpression { get; }

    public SyntaxToken AssignmentToken { get; }

    public ExpressionSyntax AssignmentExpression { get; }

    public override string ToFullString()
    {
        return LetKeyword.ToFullString() + TargetExpression.ToFullString() + AssignmentToken.ToFullString() + AssignmentExpression.ToFullString();
    }
}
