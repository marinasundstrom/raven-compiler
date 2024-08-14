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
    readonly List<SyntaxNode> _children = new List<SyntaxNode>();

    public override int Width => _children.Sum(x => x.Width);

    public override int FullWidth => _children.Sum(x => x.FullWidth);

    public override string ToFullString() => string.Join(string.Empty, _children.Select(x => x.ToFullString()));

    protected T AddChild<T>(T node)
        where T : SyntaxNode
    {
        _children.Add(node);
        return node;
    }
}