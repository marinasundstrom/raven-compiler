using System;

namespace Raven.CodeAnalysis.Syntax;

public sealed class IdentifierExpressionSyntax : ExpressionSyntax
{
    public IdentifierExpressionSyntax(SyntaxToken token)
    {
        Token = token;

        AttachChild(0, token);

        _internalNode = new InternalSyntax.IdentifierExpressionSyntax(
            token.InternalSyntax
        );
    }

    public SyntaxToken Token { get; }

    internal IdentifierExpressionSyntax(InternalSyntax.IdentifierExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.IdentifierExpressionSyntax InternalSyntax => (InternalSyntax.IdentifierExpressionSyntax)_internalNode;
}