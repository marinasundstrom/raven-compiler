namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    public AssignmentExpressionSyntax(
        ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        TargetExpression = AddChild(targetExpression);
        AssignmentToken = AddChild(assignmentToken);
        AssignmentExpression = AddChild(assignmentExpression);
    }

    public override SyntaxKind Kind => SyntaxKind.AssingmentExpression;

    public ExpressionSyntax TargetExpression { get; }

    public SyntaxToken AssignmentToken { get; }

    public ExpressionSyntax AssignmentExpression { get; }
}