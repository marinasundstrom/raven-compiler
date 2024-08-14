using System.Collections.Generic;

namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public class SyntaxList<T> : List<SyntaxListItem<T>>
    where T : SyntaxNode
{
    public SyntaxList(IEnumerable<SyntaxListItem<T>> collection) : base(collection)
    {
    }
}