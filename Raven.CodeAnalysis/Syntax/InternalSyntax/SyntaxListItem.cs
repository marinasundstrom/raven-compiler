namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public class SyntaxListItem<T>
     where T : SyntaxNode
{
    public SyntaxListItem(T item, SyntaxToken separatorToken)
    {
        Item = item;
        SeparatorToken = separatorToken;
    }

    public T Item { get; }

    public SyntaxToken SeparatorToken { get; }
}