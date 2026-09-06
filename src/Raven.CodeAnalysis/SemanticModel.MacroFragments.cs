using System.Collections.Immutable;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public partial class SemanticModel
{
    private const int MaxMacroFragmentNestingDepth = 16;

    /// <summary>
    /// Gets ordinary Raven semantic information at an authored position inside a
    /// fragment reported by a token-tree macro.
    /// </summary>
    public MacroFragmentSemanticInfo? GetMacroFragmentSemanticInfo(
        FreestandingMacroExpressionSyntax expression,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentSemanticInfoCore(expression, position, cancellationToken);

    /// <summary>
    /// Gets ordinary Raven semantic information at an authored position inside
    /// a declaration-shaped macro fragment.
    /// </summary>
    public MacroFragmentSemanticInfo? GetMacroFragmentSemanticInfo(
        FreestandingMacroDeclarationSyntax declaration,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentSemanticInfoCore(declaration, position, cancellationToken);

    public MacroFragmentSemanticInfo? GetMacroFragmentSemanticInfo(
        FreestandingMacroMemberDeclarationSyntax member,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentSemanticInfoCore(member, position, cancellationToken);

    internal MacroFragmentSemanticInfo? GetMacroFragmentSemanticInfoCore(
        SyntaxNode syntax,
        int position,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);
        if ((uint)position > (uint)SyntaxTree.GetRoot(cancellationToken).FullSpan.End)
            throw new ArgumentOutOfRangeException(nameof(position));

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        var region = GetMacroInputSnapshotCore(syntax, cancellationToken).FindFragmentRegion(position);
        if (region is null)
            return null;

        var parentBinder = GetBinder(syntax);
        return GetMacroFragmentSemanticInfo(
            syntax,
            region,
            position,
            parentBinder,
            MacroFragmentBinder.CreateVisibleSymbols(
                GetVisibleValueSymbols(syntax, allowBindingFallback: true)),
            syntax,
            nestingDepth: 0,
            cancellationToken);
    }

    /// <summary>
    /// Gets inferred type annotations for authored Raven declarations inside fragments reported by a token-tree macro.
    /// </summary>
    public ImmutableArray<MacroFragmentInferredTypeAnnotation> GetMacroFragmentInferredTypeAnnotations(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentInferredTypeAnnotationsCore(expression, cancellationToken);

    public ImmutableArray<MacroFragmentInferredTypeAnnotation> GetMacroFragmentInferredTypeAnnotations(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentInferredTypeAnnotationsCore(declaration, cancellationToken);

    public ImmutableArray<MacroFragmentInferredTypeAnnotation> GetMacroFragmentInferredTypeAnnotations(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentInferredTypeAnnotationsCore(member, cancellationToken);

    private ImmutableArray<MacroFragmentInferredTypeAnnotation> GetMacroFragmentInferredTypeAnnotationsCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        var regions = GetMacroInputSnapshotCore(syntax, cancellationToken).FragmentRegions;
        if (regions.IsDefaultOrEmpty)
            return ImmutableArray<MacroFragmentInferredTypeAnnotation>.Empty;

        var annotations = ImmutableArray.CreateBuilder<MacroFragmentInferredTypeAnnotation>();
        var context = CreateTokenTreeMacroContext(syntax, cancellationToken);
        var parentBinder = GetBinder(syntax);
        var visibleSymbols = MacroFragmentBinder.CreateVisibleSymbols(
            GetVisibleValueSymbols(syntax, allowBindingFallback: true));

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxNode? fragment = region.Kind switch
            {
                MacroFragmentKind.Expression => context.ParseExpression(region.BodyRelativeSpan),
                MacroFragmentKind.Statement => context.ParseStatement(region.BodyRelativeSpan),
                MacroFragmentKind.Block => context.ParseBlock(region.BodyRelativeSpan),
                _ => null
            };
            if (fragment is null)
                continue;

            var binder = new MacroFragmentBinder(parentBinder, region.Locals, visibleSymbols, SyntaxTree);
            switch (fragment)
            {
                case ExpressionSyntax fragmentExpression when region.TargetType is { } targetType:
                    binder.BindExpressionWithTargetTypeForSemanticQuery(fragmentExpression, targetType);
                    break;
                case ExpressionSyntax fragmentExpression:
                    binder.BindExpression(fragmentExpression);
                    break;
                case StatementSyntax fragmentStatement:
                    binder.BindStatement(fragmentStatement);
                    break;
            }

            annotations.AddRange(binder.GetInferredTypeAnnotations());
        }

        return annotations.ToImmutable();
    }

    /// <summary>
    /// Classifies the ordinary Raven syntax contained in all fragment regions
    /// reported by a token-tree macro.
    /// </summary>
    public SemanticClassificationResult GetMacroFragmentClassifications(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentClassificationsCore(expression, cancellationToken);

    /// <summary>
    /// Classifies the ordinary Raven syntax contained in all fragment regions
    /// reported by a declaration-shaped token-tree macro.
    /// </summary>
    public SemanticClassificationResult GetMacroFragmentClassifications(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentClassificationsCore(declaration, cancellationToken);

    public SemanticClassificationResult GetMacroFragmentClassifications(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentClassificationsCore(member, cancellationToken);

    private SemanticClassificationResult GetMacroFragmentClassificationsCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        var regions = GetMacroInputSnapshotCore(syntax, cancellationToken).FragmentRegions;
        if (regions.IsDefaultOrEmpty)
            return new SemanticClassificationResult([], []);

        var tokenMap = new Dictionary<SyntaxToken, SemanticClassification>();
        var triviaMap = new Dictionary<SyntaxTrivia, SemanticClassification>();
        var context = CreateTokenTreeMacroContext(syntax, cancellationToken);
        var parentBinder = GetBinder(syntax);
        var visibleSymbols = MacroFragmentBinder.CreateVisibleSymbols(
            GetVisibleValueSymbols(syntax, allowBindingFallback: true));

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SyntaxNode? fragment = region.Kind switch
            {
                MacroFragmentKind.Expression => context.ParseExpression(region.BodyRelativeSpan),
                MacroFragmentKind.Statement => context.ParseStatement(region.BodyRelativeSpan),
                MacroFragmentKind.Block => context.ParseBlock(region.BodyRelativeSpan),
                _ => null
            };
            if (fragment is null)
                continue;

            var binder = new MacroFragmentBinder(parentBinder, region.Locals, visibleSymbols, SyntaxTree);
            switch (fragment)
            {
                case ExpressionSyntax fragmentExpression when region.TargetType is { } targetType:
                    binder.BindExpressionWithTargetTypeForSemanticQuery(fragmentExpression, targetType);
                    break;
                case ExpressionSyntax fragmentExpression:
                    binder.BindExpression(fragmentExpression);
                    break;
                case StatementSyntax fragmentStatement:
                    binder.BindStatement(fragmentStatement);
                    break;
            }

            var classification = SemanticClassifier.Classify(fragment, this, allowBinding: false);
            foreach (var pair in classification.Tokens)
                tokenMap[pair.Key] = pair.Value;
            foreach (var token in fragment.DescendantTokens())
            {
                if (!tokenMap.ContainsKey(token) && SyntaxFacts.IsOverloadableOperatorToken(token.Kind))
                    tokenMap[token] = SemanticClassification.Operator;
            }
            foreach (var pair in classification.Trivia)
                triviaMap[pair.Key] = pair.Value;
        }

        return new SemanticClassificationResult(tokenMap, triviaMap);
    }

    /// <summary>
    /// Gets token metadata at an authored position inside a token-tree macro,
    /// including token-tree macros nested in reported Raven fragments.
    /// </summary>
    public MacroTokenInfo? GetMacroTokenInfo(
        FreestandingMacroExpressionSyntax expression,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroTokenInfoCore(expression, position, cancellationToken);

    public MacroTokenInfo? GetMacroTokenInfo(
        FreestandingMacroDeclarationSyntax declaration,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroTokenInfoCore(declaration, position, cancellationToken);

    public MacroTokenInfo? GetMacroTokenInfo(
        FreestandingMacroMemberDeclarationSyntax member,
        int position,
        CancellationToken cancellationToken = default)
        => GetMacroTokenInfoCore(member, position, cancellationToken);

    internal MacroTokenInfo? GetMacroTokenInfoCore(
        SyntaxNode syntax,
        int position,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);
        if ((uint)position > (uint)SyntaxTree.GetRoot(cancellationToken).FullSpan.End)
            throw new ArgumentOutOfRangeException(nameof(position));

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        return GetMacroTokenInfo(
            syntax,
            position,
            syntax,
            nestingDepth: 0,
            cancellationToken);
    }

    private MacroTokenInfo? GetMacroTokenInfo(
        SyntaxNode syntax,
        int position,
        SyntaxNode resolutionContext,
        int nestingDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!FreestandingMacroInvocation.TryCreate(syntax, out var invocation) || invocation.TokenTree is null)
            return null;

        var context = CreateTokenTreeMacroContext(syntax, cancellationToken);
        var snapshot = nestingDepth == 0
            ? GetMacroInputSnapshotCore(syntax, cancellationToken)
            : new MacroInputSnapshot(
                context.BodySpan,
                MacroTokenInfoService.GetTokens(this, syntax, resolutionContext, cancellationToken),
                MacroFragmentRegionService.GetFragmentRegions(this, syntax, resolutionContext, cancellationToken));

        var region = snapshot.FindFragmentRegion(position);
        if (region is not null && nestingDepth < MaxMacroFragmentNestingDepth)
        {
            SyntaxNode? fragment = region.Kind switch
            {
                MacroFragmentKind.Expression => context.ParseExpression(region.BodyRelativeSpan),
                MacroFragmentKind.Statement => context.ParseStatement(region.BodyRelativeSpan),
                MacroFragmentKind.Block => context.ParseBlock(region.BodyRelativeSpan),
                _ => null
            };
            if (fragment is not null)
            {
                var searchPosition = Math.Clamp(position, fragment.FullSpan.Start, fragment.FullSpan.End);
                if (searchPosition == fragment.FullSpan.End && searchPosition > fragment.FullSpan.Start)
                    searchPosition--;
                var token = fragment.FindToken(searchPosition);
                foreach (var nestedInvocation in token.Parent?.AncestorsAndSelf()
                    .OfType<FreestandingMacroExpressionSyntax>() ?? [])
                {
                    if (nestedInvocation.TokenTree?.Span.Contains(searchPosition) != true)
                        continue;

                    var nestedInfo = GetMacroTokenInfo(
                        nestedInvocation,
                        position,
                        resolutionContext,
                        nestingDepth + 1,
                        cancellationToken);
                    if (nestedInfo is not null)
                        return nestedInfo;
                }
            }
        }

        return snapshot.FindToken(position);
    }

    private MacroFragmentSemanticInfo? GetMacroFragmentSemanticInfo(
        SyntaxNode syntax,
        MacroFragmentRegion region,
        int position,
        Binder parentBinder,
        ImmutableArray<MacroFragmentVisibleSymbol> visibleSymbols,
        SyntaxNode resolutionContext,
        int nestingDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var context = CreateTokenTreeMacroContext(syntax, cancellationToken);
        SyntaxNode fragment;
        switch (region.Kind)
        {
            case MacroFragmentKind.Expression:
                fragment = context.ParseExpression(region.BodyRelativeSpan);
                break;
            case MacroFragmentKind.Statement:
                fragment = context.ParseStatement(region.BodyRelativeSpan);
                break;
            case MacroFragmentKind.Block:
                fragment = context.ParseBlock(region.BodyRelativeSpan);
                break;
            default:
                return null;
        }

        var binder = new MacroFragmentBinder(
            parentBinder,
            region.Locals,
            visibleSymbols,
            SyntaxTree);
        switch (fragment)
        {
            case ExpressionSyntax fragmentExpression:
                if (region.TargetType is { } targetType)
                    binder.BindExpressionWithTargetTypeForSemanticQuery(fragmentExpression, targetType);
                else
                    binder.BindExpression(fragmentExpression);
                break;
            case StatementSyntax fragmentStatement:
                binder.BindStatement(fragmentStatement);
                break;
        }

        var searchPosition = Math.Clamp(position, fragment.FullSpan.Start, fragment.FullSpan.End);
        if (searchPosition == fragment.FullSpan.End && searchPosition > fragment.FullSpan.Start)
            searchPosition--;
        var token = fragment.FindToken(searchPosition);
        if (token.Kind == SyntaxKind.None || token.Parent is null)
            return null;

        // The fragment bind records the expansion's symbols, not the macro
        // name itself. Resolve that name in the authored invocation's scope;
        // the speculative fragment has no imports or enclosing declarations.
        var namedMacro = token.Parent.AncestorsAndSelf()
            .OfType<FreestandingMacroExpressionSyntax>()
            .FirstOrDefault(invocation => invocation.Name.Span.Contains(token.Span));
        if (namedMacro is not null && namedMacro.TryGetMacroName(out var macroName))
        {
            IMacroSymbol? macroSymbol = null;
            if (Compilation.TryResolveLocalMacroDeclarationSymbol(
                    resolutionContext, macroName, namedMacro.Name.GetMacroArity(),
                    out var localMacro, out _))
            {
                macroSymbol = ConstructLocalMacroSymbol(namedMacro.Name, localMacro);
            }
            else if (Compilation.GetMacroRegistry().TryResolveMacroSymbol(
                    Compilation, resolutionContext, macroName, out var loadedMacro, out _))
            {
                macroSymbol = loadedMacro;
            }

            if (macroSymbol is not null)
            {
                return new MacroFragmentSemanticInfo(
                    region, token.Span, new SymbolInfo(macroSymbol),
                    new TypeInfo(macroSymbol.ExpressionResultType, macroSymbol.ExpressionResultType),
                    token.Parent);
            }
        }

        if (nestingDepth < MaxMacroFragmentNestingDepth)
        {
            foreach (var nestedInvocation in token.Parent.AncestorsAndSelf().OfType<FreestandingMacroExpressionSyntax>())
            {
                if (nestedInvocation.TokenTree?.Span.Contains(searchPosition) != true ||
                    !binder.TryGetNestedMacroVisibleSymbols(nestedInvocation, out var nestedVisibleSymbols))
                {
                    continue;
                }

                var nestedRegion = FindFragmentRegion(
                    MacroFragmentRegionService.GetFragmentRegions(
                        this,
                        nestedInvocation,
                        resolutionContext,
                        cancellationToken),
                    position);
                if (nestedRegion is null)
                    continue;

                var nestedInfo = GetMacroFragmentSemanticInfo(
                    nestedInvocation,
                    nestedRegion,
                    position,
                    binder,
                    nestedVisibleSymbols,
                    resolutionContext,
                    nestingDepth + 1,
                    cancellationToken);
                if (nestedInfo is not null)
                    return nestedInfo;
            }
        }

        foreach (var candidate in token.Parent.AncestorsAndSelf())
        {
            if (!fragment.FullSpan.Contains(candidate.Span))
                break;

            if (candidate is FreestandingMacroExpressionSyntax nestedInvocation &&
                nestedInvocation.TokenTree?.Span.Contains(searchPosition) == true)
            {
                continue;
            }

            if (candidate is ParameterSyntax parameterSyntax &&
                TryResolveFunctionExpressionParameterSymbolFast(parameterSyntax, out var parameterSymbol) &&
                parameterSymbol is not null)
            {
                return new MacroFragmentSemanticInfo(
                    region,
                    token.Span,
                    new SymbolInfo(parameterSymbol),
                    new TypeInfo(parameterSymbol.Type, parameterSymbol.Type),
                    token.Parent);
            }

            var bound = TryGetCachedBoundNode(candidate);
            var symbolInfo = TryGetSymbolMapping(candidate, out var mappedSymbolInfo) && HasSymbolInfo(mappedSymbolInfo)
                ? mappedSymbolInfo
                : bound switch
                {
                    BoundExpression boundExpression => boundExpression.GetSymbolInfo(),
                    BoundStatement boundStatement => boundStatement.GetSymbolInfo(),
                    _ => SymbolInfo.None
                };
            if (symbolInfo.Symbol is null && symbolInfo.CandidateSymbols.IsDefaultOrEmpty)
                continue;

            symbolInfo = UseAuthoredLocalName(symbolInfo, token.ValueText);

            var type = bound is BoundExpression expressionNode
                ? expressionNode.Type
                : candidate is ExpressionSyntax
                    ? GetTypeFromSymbol(symbolInfo.Symbol?.UnderlyingSymbol ?? symbolInfo.Symbol)
                    : null;
            return new MacroFragmentSemanticInfo(
                region,
                token.Span,
                symbolInfo,
                new TypeInfo(type, type),
                token.Parent);
        }

        return null;
    }

    private static SymbolInfo UseAuthoredLocalName(SymbolInfo symbolInfo, string authoredName)
    {
        if (symbolInfo.Symbol is not ILocalSymbol local ||
            !local.IsImplicitlyDeclared ||
            string.IsNullOrWhiteSpace(authoredName) ||
            string.Equals(local.Name, authoredName, StringComparison.Ordinal))
        {
            return symbolInfo;
        }

        var authoredLocal = new SourceLocalSymbol(
            authoredName,
            local.Type,
            local.IsMutable,
            local.ContainingSymbol!,
            local.ContainingType,
            local.ContainingNamespace,
            local.Locations.ToArray(),
            local.DeclaringSyntaxReferences.ToArray(),
            local.IsConst,
            local.ConstantValue,
            local.ScopedKind,
            isImplicitlyDeclared: true);
        return new SymbolInfo(authoredLocal);
    }

    private static MacroFragmentRegion? FindFragmentRegion(
        ImmutableArray<MacroFragmentRegion> regions,
        int position)
    {
        MacroFragmentRegion? best = null;
        foreach (var region in regions)
        {
            var containsPosition = region.Span.Length == 0
                ? position == region.Span.Start
                : position >= region.Span.Start && position <= region.Span.End;
            if (!containsPosition)
                continue;

            if (best is null || region.Span.Length < best.Span.Length)
                best = region;
        }

        return best;
    }

    private TokenTreeMacroContext CreateTokenTreeMacroContext(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
        => syntax switch
        {
            FreestandingMacroExpressionSyntax expression => new TokenTreeMacroContext(Compilation, this, expression, cancellationToken),
            FreestandingMacroMemberDeclarationSyntax member => new TokenTreeMacroContext(Compilation, this, member, cancellationToken),
            FreestandingMacroDeclarationSyntax declaration => new TokenTreeMacroContext(Compilation, this, declaration, cancellationToken),
            _ => throw new ArgumentException("Syntax is not a supported token-tree macro carrier.", nameof(syntax))
        };
}
