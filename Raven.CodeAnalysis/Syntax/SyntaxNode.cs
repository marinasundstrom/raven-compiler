using System;
using System.Collections.Generic;
using System.Linq;

namespace Raven.CodeAnalysis.Syntax;

public abstract class SyntaxNode : ISyntaxNode, IHasParent
{
    internal InternalSyntax.SyntaxNode _internalNode;
    readonly List<ISyntaxNode> _children = new List<ISyntaxNode>();

    public virtual SyntaxKind Kind => _internalNode.Kind;

    public TextSpan Span => new TextSpan(Parent?.Span?.End ?? 0, _internalNode.Width);

    public TextSpan FullSpan => new TextSpan(Parent?.FullSpan?.End ?? 0, _internalNode.FullWidth);

    public virtual SyntaxNode? Parent { get; private set; } = null!;

    IReadOnlyList<SyntaxNode> ChildNodes => _children.OfType<SyntaxNode>().ToList();

    protected void AttachChild(ISyntaxNode childSyntax)
    {
        ((IHasParent)childSyntax).SetParent(this);
        _children.Add(childSyntax);
    }

    void IHasParent.SetParent(SyntaxNode parent) => Parent = parent;

    public TSyntaxNode GetOrCreateNode<TSyntaxNode>(InternalSyntax.SyntaxNode internalSyntaxNode, ref TSyntaxNode node)
        where TSyntaxNode : ISyntaxNode
    {
        if (node is null)
        {
            node = (TSyntaxNode)Activator.CreateInstance(typeof(TSyntaxNode), [internalSyntaxNode])!;

            AttachChild(node);
        }

        return node!;
    }

    public string ToFullString() => _internalNode.ToFullString();
}