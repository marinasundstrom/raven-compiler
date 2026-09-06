using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

partial class BlockBinder
{
    private ForIterationInfo BindYieldFromIteration(
        BoundExpression collection,
        ExpressionSyntax syntax,
        IteratorMethodKind kind,
        ITypeSymbol elementType)
    {
        var iteration = kind is IteratorMethodKind.AsyncEnumerable or IteratorMethodKind.AsyncEnumerator &&
            collection.Type is { } collectionType &&
            TryClassifyAwaitForEnumerator(collectionType, out var asyncIteration)
                ? asyncIteration
                : ClassifyForIteration(collection, syntax);

        // Check the yielded element conversion, rather than converting the collection.
        var local = CreateLocalSymbol(syntax, "<yieldFromElement>", isMutable: false, iteration.ElementType, recordDeclaration: false);
        BindYieldValueConversion(new BoundLocalAccess(local), elementType, syntax);

        if (iteration.Kind == ForIterationKind.Async && _containingSymbol is IMethodSymbol method &&
            AsyncIteratorCancellationUtilities.GetEffectiveEnumeratorCancellationParameter(Compilation, method, kind) is { } token)
        {
            iteration = iteration with { CancellationToken = new BoundParameterAccess(token) };
        }

        return iteration;
    }
}
