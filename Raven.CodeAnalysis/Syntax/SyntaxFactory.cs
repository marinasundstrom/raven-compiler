using System;

namespace Raven.CodeAnalysis.Syntax;

public static partial class SyntaxFactory
{
    public static IdentifierExpressionSyntax Identifier(SyntaxToken token) => new IdentifierExpressionSyntax(token);

    public static IdentifierExpressionSyntax Identifier(string identifier) => Identifier(new SyntaxToken(new InternalSyntax.SyntaxToken(SyntaxKind.IdentifierToken, identifier)));

    public static NumberExpressionSyntax Number(SyntaxToken token) => new NumberExpressionSyntax(token);

    public static NumberExpressionSyntax Number(int value) => Number(new SyntaxToken(new InternalSyntax.SyntaxToken(SyntaxKind.NumberToken, value.ToString())));

    public static BinaryOperationExpression BinaryOperation(ExpressionSyntax leftHandSide, SyntaxToken operatorToken, ExpressionSyntax rightHandSide)
        => new BinaryOperationExpression(leftHandSide, operatorToken, rightHandSide);

    public static LetDeclarationExpressionSyntax Let(ExpressionSyntax targetExpression, ExpressionSyntax assignmentExpression)
        => new LetDeclarationExpressionSyntax(SyntaxFactory.LetKeyword, targetExpression, SyntaxFactory.AssignmentToken, assignmentExpression);

    public static AssignmentExpressionSyntax Assignment(ExpressionSyntax targetExpression, ExpressionSyntax assignmentExpression)
        => new AssignmentExpressionSyntax(targetExpression, SyntaxFactory.AssignmentToken, assignmentExpression);

    public static ParenthesisExpressionSyntax Parenthesis(ExpressionSyntax expression)
        => new ParenthesisExpressionSyntax(SyntaxFactory.OpeningParenToken, expression, SyntaxFactory.ClosingParenToken);

    public static IfElseExpressionSyntax IfElseExpression(ExpressionSyntax conditionalExpression, ExpressionSyntax thenExpression, ExpressionSyntax? elseExpression = null)
        => new IfElseExpressionSyntax(SyntaxFactory.IfKeyword.AppendTrailingTrivia(SyntaxFactory.Whitespace(1)), conditionalExpression, SyntaxFactory.ThenKeyword, thenExpression, SyntaxFactory.ElseKeyword, elseExpression);

    public static SyntaxTrivia Whitespace(int length)
    {
        return new SyntaxTrivia();
    }

    public static BooleanTrueExpressionSyntax True()
        => new BooleanTrueExpressionSyntax(SyntaxFactory.TrueKeyword);

    public static BooleanFalseExpressionSyntax False()
        => new BooleanFalseExpressionSyntax(SyntaxFactory.FalseKeyword);

    public static readonly SyntaxToken AssignmentToken = new SyntaxToken(InternalSyntax.SyntaxFactory.AssignmentToken);

    public static readonly SyntaxToken LetKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.LetToken);

    public static readonly SyntaxToken IfKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.IfToken);

    public static readonly SyntaxToken ThenKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.ThenToken);

    public static readonly SyntaxToken ElseKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.ElseKeyword);

    public static readonly SyntaxToken TrueKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.TrueToken);

    public static readonly SyntaxToken FalseKeyword = new SyntaxToken(InternalSyntax.SyntaxFactory.FalseToken);

    public static readonly SyntaxToken GreaterThanToken = new SyntaxToken(InternalSyntax.SyntaxFactory.GreaterThanToken);

    public static readonly SyntaxToken OpeningParenToken = new SyntaxToken(InternalSyntax.SyntaxFactory.OpeningParenToken);

    public static readonly SyntaxToken ClosingParenToken = new SyntaxToken(InternalSyntax.SyntaxFactory.ClosingParenToken);
}