namespace Raven.CodeAnalysis.Syntax;

public sealed class LetDeclarationExpressionSyntax : ExpressionSyntax
{
    private SyntaxToken? _letKeyword;
    private ExpressionSyntax? _targetExpression;
    private SyntaxToken? _assignmentToken;
    private ExpressionSyntax? _assignmentExpression;

    public LetDeclarationExpressionSyntax(
        SyntaxToken letKeyword, ExpressionSyntax targetExpression, SyntaxToken assignmentToken, ExpressionSyntax assignmentExpression)
    {
        _letKeyword = letKeyword;
        _targetExpression = targetExpression;
        _assignmentToken = assignmentToken;
        _assignmentExpression = assignmentExpression;

        AttachChild(0, letKeyword);
        AttachChild(1, targetExpression);
        AttachChild(2, assignmentToken);
        AttachChild(3, assignmentExpression);

        _internalNode = new InternalSyntax.LetDeclarationExpressionSyntax(
            _letKeyword.InternalSyntax, _targetExpression.InternalSyntax, _assignmentToken.InternalSyntax, _assignmentExpression.InternalSyntax);
    }

    public SyntaxToken LetKeyword => GetOrCreateNode(0, InternalSyntax.LetKeyword, ref _letKeyword)!;

    public ExpressionSyntax TargetExpression => GetOrCreateNode(1, InternalSyntax.TargetExpression, ref _targetExpression)!;

    public SyntaxToken AssignmentToken => GetOrCreateNode(2, InternalSyntax.AssignmentToken, ref _assignmentToken)!;

    public ExpressionSyntax AssignmentExpression => GetOrCreateNode(3, InternalSyntax.AssignmentExpression, ref _assignmentExpression)!;

    internal LetDeclarationExpressionSyntax(InternalSyntax.LetDeclarationExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.LetDeclarationExpressionSyntax InternalSyntax => (InternalSyntax.LetDeclarationExpressionSyntax)_internalNode;
}