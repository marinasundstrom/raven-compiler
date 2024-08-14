namespace Raven.CodeAnalysis.Syntax;

public sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    public AssignmentExpressionSyntax(
        ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        TargetExpression = targetExpression;
        AssignmentToken = assignmentToken;
        AssignmentExpression = assignmentExpression;

        AttachChild(targetExpression);
        AttachChild(assignmentToken);
        AttachChild(assignmentExpression);

        _internalNode = new InternalSyntax.AssignmentExpressionSyntax(
            targetExpression.InternalSyntax,
            assignmentToken.InternalSyntax,
            assignmentExpression.InternalSyntax
        );
    }

    public ExpressionSyntax TargetExpression { get; }
    public SyntaxToken AssignmentToken { get; }
    public ExpressionSyntax AssignmentExpression { get; }

    internal AssignmentExpressionSyntax(InternalSyntax.AssignmentExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    internal InternalSyntax.AssignmentExpressionSyntax Syntax => (InternalSyntax.AssignmentExpressionSyntax)_internalNode;
}