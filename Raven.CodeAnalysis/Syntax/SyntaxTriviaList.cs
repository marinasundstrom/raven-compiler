namespace Raven.CodeAnalysis.Syntax;

public class SyntaxTriviaList
{
    private readonly InternalSyntax.SyntaxTriviaList node;

    internal SyntaxTriviaList(InternalSyntax.SyntaxTriviaList node)
    {
        this.node = node;
    }

    internal InternalSyntax.SyntaxTriviaList Node => node;

    public static implicit operator SyntaxTriviaList(InternalSyntax.SyntaxTriviaList e)
    {
        return new SyntaxTriviaList(e);
    }
}