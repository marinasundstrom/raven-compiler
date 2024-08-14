namespace Raven.CodeAnalysis.Syntax;

public sealed class LetDeclarationExpressionSyntax : ExpressionSyntax
{
    private SyntaxToken _letKeyword;
    private ExpressionSyntax _targetExpression;
    private SyntaxToken _assignmentToken;
    private ExpressionSyntax _assignmentExpression;

    public LetDeclarationExpressionSyntax(
        SyntaxToken letKeyword, ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        _letKeyword = letKeyword;
        _targetExpression = targetExpression;
        _assignmentToken = assignmentToken;
        _assignmentExpression = assignmentExpression;

        AttachChild(letKeyword);
        AttachChild(targetExpression);
        AttachChild(assignmentToken);
        AttachChild(assignmentExpression);

        _internalNode = new InternalSyntax.LetDeclarationExpressionSyntax(
            _letKeyword.InternalSyntax, _targetExpression.InternalSyntax, _assignmentToken.InternalSyntax, _assignmentExpression.InternalSyntax);
    }

    public SyntaxToken LetKeyword => GetOrCreateNode(InternalSyntax.LetKeyword, ref _letKeyword);

    public ExpressionSyntax TargetExpression => GetOrCreateNode(InternalSyntax.TargetExpression, ref _targetExpression);

    public SyntaxToken AssignmentToken => GetOrCreateNode(InternalSyntax.AssignmentToken, ref _assignmentToken);

    public ExpressionSyntax AssignmentExpression => GetOrCreateNode(InternalSyntax.AssignmentExpression, ref _assignmentExpression);

    internal LetDeclarationExpressionSyntax(InternalSyntax.LetDeclarationExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.LetDeclarationExpressionSyntax InternalSyntax => (InternalSyntax.LetDeclarationExpressionSyntax)_internalNode;
}