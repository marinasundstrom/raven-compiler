namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    public AssignmentExpressionSyntax(
        ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        TargetExpression = AddChild(0, targetExpression);
        AssignmentToken = AddChild(1, assignmentToken);
        AssignmentExpression = AddChild(2, assignmentExpression);
    }

    public override SyntaxKind Kind => SyntaxKind.AssingmentExpression;

    public ExpressionSyntax TargetExpression { get; }

    public SyntaxToken AssignmentToken { get; }

    public ExpressionSyntax AssignmentExpression { get; }
}