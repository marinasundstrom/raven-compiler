﻿namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public class ElseClauseSyntax : SyntaxNodeWithChildren
{
    public ElseClauseSyntax(SyntaxToken elseToken, StatementSyntax? parseStatement)
    {
        this.ElseToken = AddChild(0, elseToken);
        this.ParseStatement = AddChild(1, parseStatement!);
    }

    public override SyntaxKind Kind => SyntaxKind.ElseClauseSyntax;

    public SyntaxToken ElseToken { get; }

    public StatementSyntax? ParseStatement { get; }
}