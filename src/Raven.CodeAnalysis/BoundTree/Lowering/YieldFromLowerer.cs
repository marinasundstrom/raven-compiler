using System;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

internal static class YieldFromLowerer
{
    public static BoundBlockStatement Rewrite(IMethodSymbol method, Compilation compilation, BoundBlockStatement body)
        => (BoundBlockStatement)new Rewriter(method, compilation).VisitStatement(body)!;

    private sealed class Rewriter(IMethodSymbol method, Compilation compilation) : BoundTreeRewriter
    {
        private int _localCounter;

        public override BoundNode? VisitFunctionExpression(BoundFunctionExpression node) => node;

        public override BoundNode? VisitYieldStatement(BoundYieldStatement node)
            => node.Iteration is { } iteration
                ? CreateLoop(node.Expression, iteration, node.ElementType, node.IteratorKind)
                : base.VisitYieldStatement(node);

        public override BoundNode? VisitYieldExpression(BoundYieldExpression node)
            => node.Iteration is { } iteration
                ? new BoundBlockExpression(
                    [CreateLoop(node.Expression, iteration, node.ElementType, node.IteratorKind),
                     new BoundExpressionStatement(new BoundUnitExpression(node.Type))], node.Type)
                : base.VisitYieldExpression(node);

        private BoundForStatement CreateLoop(
            BoundExpression collection,
            ForIterationInfo iteration,
            ITypeSymbol elementType,
            IteratorMethodKind kind)
        {
            var local = new SourceLocalSymbol(
                $"<yieldFromElement>{_localCounter++}", iteration.ElementType, isMutable: false,
                method, method.ContainingType, method.ContainingNamespace,
                [Location.None], Array.Empty<SyntaxReference>());
            BoundExpression value = new BoundLocalAccess(local);
            if (!SymbolEqualityComparer.Default.Equals(iteration.ElementType, elementType))
                value = new BoundConversionExpression(value, elementType, compilation.ClassifyConversion(iteration.ElementType, elementType));

            return new BoundForStatement(local, iteration, VisitExpression(collection)!, new BoundYieldStatement(value, elementType, kind));
        }
    }
}
