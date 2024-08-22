namespace Raven.CodeAnalysis.Syntax;

public sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    private ExpressionSyntax? _targetExpression;
    private SyntaxToken _assignmentToken;
    private ExpressionSyntax? _assignmentExpression;

    public AssignmentExpressionSyntax(
        ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        _targetExpression = targetExpression;
        _assignmentToken = assignmentToken;
        _assignmentExpression = assignmentExpression;

        AttachChild(0, targetExpression);
        AttachChild(1, assignmentToken);
        AttachChild(2, assignmentExpression);

        _internalNode = new InternalSyntax.AssignmentExpressionSyntax(
            targetExpression.InternalSyntax,
            assignmentToken.InternalSyntax,
            assignmentExpression.InternalSyntax
        );
    }

    public ExpressionSyntax TargetExpression => GetOrCreateNode(0, InternalSyntax.TargetExpression, ref _targetExpression)!;
    public SyntaxToken AssignmentToken => GetOrCreateNode(1, InternalSyntax.AssignmentToken, ref _assignmentToken)!;
    public ExpressionSyntax AssignmentExpression => GetOrCreateNode(2, InternalSyntax.AssignmentExpression, ref _assignmentExpression)!;

    internal AssignmentExpressionSyntax(InternalSyntax.AssignmentExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.AssignmentExpressionSyntax InternalSyntax => (InternalSyntax.AssignmentExpressionSyntax)_internalNode;
}