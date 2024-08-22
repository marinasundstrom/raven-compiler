using System.Collections.Generic;
using System.Linq;

namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public abstract class SyntaxNode
{
    readonly List<ISyntaxNode> _children = new List<ISyntaxNode>();

    public virtual SyntaxKind Kind { get; }

    public virtual int Width { get; }

    public virtual int FullWidth { get; }

    public bool ValidateNode()
    {
        return false;
    }

    public abstract string ToFullString();
}

public abstract class SyntaxNodeWithChildren : SyntaxNode
{
    readonly Dictionary<int, SyntaxNode> _children = new Dictionary<int, SyntaxNode>();

    public override int Width => _children
        .Select(x => x.Value)
        .Sum(x => x.Width);

    public override int FullWidth => _children
        .Select(x => x.Value)
        .Sum(x => x.FullWidth);

    public override string ToFullString() => string.Join(string.Empty, _children
        .OrderBy(x => x.Key)
        .Select(x => x.Value)
        .Select(x => x.ToFullString()));

    protected T AddChild<T>(int index, T node)
        where T : SyntaxNode
    {
        _children.Add(index, node);
        return node;
    }
}