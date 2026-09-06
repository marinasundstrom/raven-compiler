using System.Collections.Immutable;

namespace Raven.CodeAnalysis;

sealed partial class BoundLocalDeclarationStatement : BoundStatement
{
    public IEnumerable<BoundVariableDeclarator> Declarators { get; }
    public bool IsUsing { get; }

    public BoundLocalDeclarationStatement(IEnumerable<BoundVariableDeclarator> declarators, bool isUsing = false)
    {
        Declarators = declarators.ToImmutableArray();
        IsUsing = isUsing;
    }

    public override ISymbol Symbol => Declarators.First().Local;
}

sealed partial class BoundVariableDeclarator : BoundNode
{
    public ILocalSymbol Local { get; }
    public BoundExpression? Initializer { get; }
    public BoundAddressOfExpression? FixedAddressInitializer { get; }
    public ILocalSymbol? FixedPinnedLocal { get; }

    public BoundVariableDeclarator(
        ILocalSymbol local,
        BoundExpression? initializer,
        BoundAddressOfExpression? fixedAddressInitializer = null,
        ILocalSymbol? fixedPinnedLocal = null)
    {
        Local = local;
        Initializer = initializer;
        FixedAddressInitializer = fixedAddressInitializer;
        FixedPinnedLocal = fixedPinnedLocal;
    }

    public ISymbol Symbol => Local;
    public ITypeSymbol Type => Local.Type;
}
