using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

internal enum ForIterationKind
{
    Array,
    Generic,
    Async,
    Range,
    NonGeneric,
}

internal sealed record ForIterationInfo(
    ForIterationKind Kind,
    ITypeSymbol ElementType,
    IArrayTypeSymbol? ArrayType = null,
    INamedTypeSymbol? EnumerableInterface = null,
    INamedTypeSymbol? EnumeratorInterface = null,
    IMethodSymbol? GetEnumeratorMethod = null,
    IMethodSymbol? MoveNextMethod = null,
    IMethodSymbol? MoveNextAsyncMethod = null,
    IMethodSymbol? CurrentGetter = null,
    IMethodSymbol? DisposeAsyncMethod = null,
    BoundRangeExpression? Range = null,
    BoundExpression? RangeStart = null,
    BoundExpression? RangeEnd = null,
    BoundExpression? RangeStep = null,
    bool RangeUpperExclusive = false,
    BoundExpression? CancellationToken = null)
{
    public static ForIterationInfo ForArray(IArrayTypeSymbol arrayType) =>
        new(ForIterationKind.Array, arrayType.ElementType, arrayType);

    public static ForIterationInfo ForGeneric(
        INamedTypeSymbol enumerableInterface,
        INamedTypeSymbol enumeratorInterface,
        IMethodSymbol getEnumeratorMethod,
        IMethodSymbol moveNextMethod,
        IMethodSymbol currentGetter) =>
        new(ForIterationKind.Generic,
            enumerableInterface.TypeArguments[0],
            null,
            enumerableInterface,
            enumeratorInterface,
            getEnumeratorMethod,
            moveNextMethod,
            null,
            currentGetter);

    public static ForIterationInfo ForAsync(
        ITypeSymbol elementType,
        IMethodSymbol getAsyncEnumeratorMethod,
        IMethodSymbol moveNextAsyncMethod,
        IMethodSymbol currentGetter,
        IMethodSymbol? disposeAsyncMethod = null) =>
        new(ForIterationKind.Async,
            elementType,
            null,
            null,
            null,
            getAsyncEnumeratorMethod,
            null,
            moveNextAsyncMethod,
            currentGetter,
            disposeAsyncMethod);

    public static ForIterationInfo ForNonGeneric(
        ITypeSymbol elementType,
        IMethodSymbol? getEnumeratorMethod = null,
        IMethodSymbol? moveNextMethod = null,
        IMethodSymbol? currentGetter = null) =>
        new(ForIterationKind.NonGeneric, elementType, null, null, null, getEnumeratorMethod, moveNextMethod, null, currentGetter);

    public static ForIterationInfo ForRange(
        ITypeSymbol elementType,
        BoundExpression start,
        BoundExpression end,
        BoundExpression step,
        BoundRangeExpression? range = null,
        bool rangeUpperExclusive = false) =>
        new(
            ForIterationKind.Range,
            elementType,
            Range: range,
            RangeStart: start,
            RangeEnd: end,
            RangeStep: step,
            RangeUpperExclusive: rangeUpperExclusive);
}
