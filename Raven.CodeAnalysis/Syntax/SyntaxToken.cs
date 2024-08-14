using System;
using System.Collections.Generic;

namespace Raven.CodeAnalysis.Syntax;

public struct SyntaxToken : ISyntaxNode, IHasParent
{
    private readonly InternalSyntax.SyntaxToken syntaxToken;

    internal SyntaxToken(InternalSyntax.SyntaxToken syntaxToken, SyntaxNode parentNode) // IEnumerable<SyntaxTrivia> leadingTrivia, IEnumerable<SyntaxTrivia> trailingTrivia)
    {
        this.syntaxToken = syntaxToken;
        Parent = parentNode;
    }

    public SyntaxToken(InternalSyntax.SyntaxToken syntaxToken)
        : this(syntaxToken, null!)
    {

    }

    public SyntaxKind Kind => syntaxToken.Kind;

    public int Width => syntaxToken.Width;

    public int FullWidth => syntaxToken.FullWidth;

    public TextSpan Span => new TextSpan(Parent?.Span?.End ?? 0, syntaxToken.Width);

    public TextSpan FullSpan => new TextSpan(Parent?.FullSpan?.End ?? 0, syntaxToken.FullWidth);

    public SyntaxNode? Parent { get; internal set; }

    public SyntaxTriviaList LeadingTrivia => syntaxToken.LeadingTrivia;

    public SyntaxTriviaList TrailingTrivia => syntaxToken.TrailingTrivia;

    public string ToFullString()
    {
        return this.syntaxToken.ToFullString();
    }

    public override string ToString()
    {
        return SyntaxFacts.GetSyntaxTokenText(Kind);
    }

    void IHasParent.SetParent(SyntaxNode parent) => Parent = parent;

    public SyntaxToken PrependLeadingTrivia(params SyntaxTrivia[] syntaxTrivia)
    {
        return this;
    }

    public SyntaxToken AppendTrailingTrivia(params SyntaxTrivia[] syntaxTrivia)
    {
        return this;
    }

    internal InternalSyntax.SyntaxToken InternalSyntax => syntaxToken;
}