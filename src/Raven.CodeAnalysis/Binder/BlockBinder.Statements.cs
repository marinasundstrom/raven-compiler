using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

partial class BlockBinder
{
    private BoundStatement BindIfPatternStatement(IfPatternStatementSyntax ifPatternStmt)
    {
        var condition = BindIfPatternCondition(ifPatternStmt);
        return BindIfCore(
            condition,
            ifPatternStmt.ThenStatement,
            ifPatternStmt.ElseClause,
            ifPatternStmt.Expression,
            ifPatternStmt.IfKeyword.GetLocation());
    }

    private BoundStatement BindExpressionStatement(
        ExpressionStatementSyntax expressionStmt,
        ITypeSymbol? contextualTargetType = null)
    {
        if (expressionStmt.Expression is FreestandingMacroExpressionSyntax { TokenTree: not null } macroExpression)
        {
            var expansion = GetFreestandingMacroExpansion(macroExpression);
            if (expansion?.Statement is not { } expansionStatement)
                return new BoundExpressionStatement(ErrorExpression(reason: BoundExpressionReason.NotFound));

            SemanticModel.RegisterMacroReplacementSyntaxTree(macroExpression, expansionStatement);
            var boundStatement = BindStatement(expansionStatement);
            CacheBoundNode(expansionStatement, boundStatement);
            return boundStatement;
        }

        var isImplicitReturnTarget =
            _containingSymbol is IMethodSymbol &&
            expressionStmt.Parent switch
            {
                BlockStatementSyntax blockStatement => IsImplicitReturnTarget(blockStatement, expressionStmt),
                BlockSyntax blockExpression => IsImplicitReturnTarget(blockExpression, expressionStmt),
                _ => false
            };

        var expressionTargetType = contextualTargetType ??
            (isImplicitReturnTarget && _containingSymbol is IMethodSymbol method
                ? GetReturnTargetType(method)
                : null);

        if (expressionTargetType is not null)
        {
            // A value-bearing expression may already have been queried without its surrounding
            // context. Force the contextual path so target-typed union cases don't degrade into
            // open-generic fallbacks.
            RemoveCachedBoundNode(expressionStmt.Expression);
        }

        var expr = expressionTargetType is not null
            ? BindExpressionWithTargetType(expressionStmt.Expression, expressionTargetType)
            : BindExpression(expressionStmt.Expression, allowReturn: true);

        var valueExpression = expr;
        while (valueExpression is BoundParenthesizedExpression parenthesized)
            valueExpression = parenthesized.Expression;

        if (valueExpression is BoundTypeExpression { Type: not NullTypeSymbol } typeExpression)
        {
            _diagnostics.ReportTypeUsedAsValue(typeExpression.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), expressionStmt.Expression.GetLocation());
            expr = ErrorExpression(reason: BoundExpressionReason.OtherError);
            CacheBoundNode(expressionStmt.Expression, expr);
        }

        if (isImplicitReturnTarget && _containingSymbol is IMethodSymbol implicitReturnMethod)
        {
            var targetType = GetReturnTargetType(implicitReturnMethod);
            if (ReportStructUnionReturnMayBeDefault(targetType, expr, expressionStmt.Expression))
            {
                expr = new BoundErrorExpression(targetType, null, BoundExpressionReason.OtherError);
            }
        }

        if (expr is BoundMethodGroupExpression methodGroup && methodGroup.GetConvertedType() is null)
        {
            expr = ReportMethodGroupRequiresDelegate(methodGroup, expressionStmt.Expression);
            CacheBoundNode(expressionStmt.Expression, expr);
        }

        return ExpressionToStatement(expr);
    }

    private BoundStatement BindIfStatement(IfStatementSyntax ifStmt)
    {
        var condition = BindExpression(ifStmt.Condition);
        return BindIfCore(
            condition,
            ifStmt.ThenStatement,
            ifStmt.ElseClause,
            ifStmt.Condition,
            ifStmt.IfKeyword.GetLocation());
    }

    private BoundStatement BindIfCore(
        BoundExpression condition,
        StatementSyntax thenStatementSyntax,
        ElseClauseSyntax? elseClauseSyntax,
        SyntaxNode conditionSyntax,
        Location ifKeywordLocation)
    {
        var patternLocals = condition is BoundIsPatternExpression isPattern
            ? CollectPatternDesignatorLocals(isPattern.Pattern)
            : ImmutableArray<ILocalSymbol>.Empty;
        var patternDepth = _scopeDepth + 1;
        var shadowedLocals = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);

        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        var conversion = Compilation.ClassifyConversion(condition.Type, boolType);
        if (!conversion.Exists || !conversion.IsImplicit)
        {
            ReportCannotConvertFromTypeToType(condition.Type, boolType, conditionSyntax.GetLocation());
        }

        ISymbol? narrowedSymbol = null;
        var narrowedSymbolWasAlreadyActive = false;
        if (Compilation.Options.EnableIsNotNullNarrowing &&
            TryGetDirectIsNotNullNarrowedSymbol(condition, out narrowedSymbol))
        {
            narrowedSymbolWasAlreadyActive = !_isNotNullNarrowedSymbols.Add(narrowedSymbol);
        }

        foreach (var local in patternLocals)
        {
            if (!shadowedLocals.ContainsKey(local.Name))
                shadowedLocals[local.Name] = _locals.TryGetValue(local.Name, out var existing) ? existing : null;

            _locals[local.Name] = (local, patternDepth);
        }

        BoundStatement thenBound;
        try
        {
            thenBound = BindStatement(thenStatementSyntax);
        }
        finally
        {
            if (narrowedSymbol is not null && !narrowedSymbolWasAlreadyActive)
                _isNotNullNarrowedSymbols.Remove(narrowedSymbol);
        }
        foreach (var local in patternLocals)
        {
            if (!shadowedLocals.TryGetValue(local.Name, out var shadowed) || shadowed is null)
                _locals.Remove(local.Name);
            else
                _locals[local.Name] = shadowed.Value;
        }

        BoundStatement? elseBound = null;
        if (elseClauseSyntax is not null)
            elseBound = BindStatement(elseClauseSyntax.Statement);

        if (_containingSymbol is IMethodSymbol containingMethod &&
            GetReturnTargetType(containingMethod) is { } methodReturnType &&
            methodReturnType.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit &&
            elseBound is not null &&
            TryGetIgnoredValueExpression(thenBound, out var thenExpression) &&
            TryGetIgnoredValueExpression(elseBound, out var elseExpression) &&
            !HasExpressionErrors(thenExpression) &&
            !HasExpressionErrors(elseExpression) &&
            thenExpression.Type is { SpecialType: not SpecialType.System_Void and not SpecialType.System_Unit } &&
            elseExpression.Type is { SpecialType: not SpecialType.System_Void and not SpecialType.System_Unit })
        {
            // If the if/else is the last statement in a value-returning function body, the
            // implicit-return machinery (ImplicitReturnRewriter / codegen) will insert returns
            // into each branch — no warning needed in that position.
            if (!IsIfStatementImplicitReturn(thenStatementSyntax.Parent as SyntaxNode))
            {
                _diagnostics.ReportIfStatementValueIgnored(ifKeywordLocation);
            }
        }

        return new BoundIfStatement(condition, thenBound, elseBound);
    }

    private static bool TryGetDirectIsNotNullNarrowedSymbol(
        BoundExpression condition,
        out ISymbol symbol)
    {
        if (condition is BoundIsPatternExpression
            {
                Expression: var testedExpression,
                Pattern: BoundNotPattern
                {
                    Pattern: BoundConstantPattern { ConstantValue: null }
                }
            })
        {
            switch (testedExpression)
            {
                case BoundLocalAccess { Local.IsMutable: false } localAccess:
                    symbol = localAccess.Local;
                    return true;
                case BoundParameterAccess parameterAccess:
                    symbol = parameterAccess.Parameter;
                    return true;
            }
        }

        symbol = null!;
        return false;
    }

    private BoundExpression BindIfPatternCondition(IfPatternStatementSyntax syntax)
        => BindPatternStatementCondition(syntax.BindingKeyword, syntax.Pattern, syntax.Expression);

    private BoundExpression BindIfPatternCondition(IfPatternExpressionSyntax syntax)
        => BindPatternStatementCondition(syntax.BindingKeyword, syntax.Pattern, syntax.Value);

    private BoundExpression BindWhilePatternCondition(WhilePatternStatementSyntax syntax)
        => BindPatternStatementCondition(syntax.BindingKeyword, syntax.Pattern, syntax.Expression);

    private BoundExpression BindPatternStatementCondition(
        SyntaxToken bindingKeyword,
        PatternSyntax patternSyntax,
        ExpressionSyntax expressionSyntax)
    {
        var expression = BindExpression(expressionSyntax);
        var inlineBindingKeyword = FindFirstInlinePatternBindingKeyword(patternSyntax);
        if (inlineBindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
        {
            _diagnostics.ReportPatternDeclarationBindingKeywordConflict(
                bindingKeyword.Text,
                inlineBindingKeyword.Text,
                inlineBindingKeyword.GetLocation());
        }

        BoundPattern pattern;
        var previousBindingKeyword = _ambientPatternDeclarationBindingKeyword;
        _ambientPatternDeclarationBindingKeyword = bindingKeyword.Kind;
        try
        {
            pattern = BindPattern(patternSyntax, expression.Type);
        }
        finally
        {
            _ambientPatternDeclarationBindingKeyword = previousBindingKeyword;
        }
        var booleanType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        return new BoundIsPatternExpression(expression, pattern, booleanType);
    }

    private bool IsIfStatementImplicitReturn(SyntaxNode? ifSyntax)
    {
        if (ifSyntax is not IfStatementSyntax and not IfPatternStatementSyntax)
            return false;

        if (_containingSymbol is not IMethodSymbol method)
            return false;

        var returnType = GetReturnTargetType(method);
        if (returnType is null ||
            returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
            return false;

        if (ifSyntax.Parent is BlockStatementSyntax blockStatement)
        {
            if (blockStatement.Statements.Count == 0 || blockStatement.Statements.LastOrDefault() != ifSyntax)
                return false;

            return blockStatement.Parent switch
            {
                BaseMethodDeclarationSyntax => true,
                FunctionStatementSyntax => true,
                AccessorDeclarationSyntax => true,
                FunctionExpressionSyntax => true,
                _ => false,
            };
        }

        if (ifSyntax.Parent is BlockSyntax blockExpression)
        {
            if (blockExpression.Statements.Count == 0 || blockExpression.Statements.LastOrDefault() != ifSyntax)
                return false;

            return blockExpression.Parent switch
            {
                BaseMethodDeclarationSyntax => true,
                FunctionStatementSyntax => true,
                AccessorDeclarationSyntax => true,
                FunctionExpressionSyntax => true,
                _ => false,
            };
        }

        return false;
    }

    private static bool TryGetIgnoredValueExpression(BoundStatement statement, out BoundExpression expression)
    {
        switch (statement)
        {
            case BoundExpressionStatement expressionStatement:
                expression = expressionStatement.Expression;
                return true;
            case BoundBlockStatement blockStatement when blockStatement.Statements.Count() == 1:
                return TryGetIgnoredValueExpression(blockStatement.Statements.First(), out expression);
            default:
                expression = null!;
                return false;
        }
    }

    private static bool IsEarlyExitStatement(StatementSyntax statement)
    {
        return statement switch
        {
            ReturnStatementSyntax or
            MacroExpansionStatementSyntax { Keyword.ValueText: "expand" } or
            ExpressionStatementSyntax
            {
                Expression: MacroExpansionExpressionSyntax { Keyword.ValueText: "expand" }
            } or
            ThrowStatementSyntax or
            BreakStatementSyntax or
            ContinueStatementSyntax => true,
            BlockStatementSyntax block when block.Statements.Count > 0 => IsEarlyExitStatement(block.Statements[^1]),
            IfStatementSyntax { ElseClause: { } elseClause } ifStatement =>
                IsEarlyExitStatement(ifStatement.ThenStatement) &&
                IsEarlyExitStatement(elseClause.Statement),
            _ => false
        };
    }

    private BoundStatement BindWhileStatement(WhileStatementSyntax whileStmt)
    {
        var condition = BindExpression(whileStmt.Condition);
        var body = BindStatementInLoop(whileStmt.Statement);
        return new BoundWhileStatement(condition, body);
    }

    private BoundStatement BindWhilePatternStatement(WhilePatternStatementSyntax whileStmt)
    {
        var condition = BindWhilePatternCondition(whileStmt);
        var patternLocals = condition is BoundIsPatternExpression isPattern
            ? CollectPatternDesignatorLocals(isPattern.Pattern)
            : ImmutableArray<ILocalSymbol>.Empty;
        var patternDepth = _scopeDepth + 1;
        var shadowedLocals = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);

        foreach (var local in patternLocals)
        {
            if (!shadowedLocals.ContainsKey(local.Name))
                shadowedLocals[local.Name] = _locals.TryGetValue(local.Name, out var existing) ? existing : null;

            _locals[local.Name] = (local, patternDepth);
        }

        var body = BindStatementInLoop(whileStmt.Statement);

        foreach (var local in patternLocals)
        {
            if (!shadowedLocals.TryGetValue(local.Name, out var shadowed) || shadowed is null)
                _locals.Remove(local.Name);
            else
                _locals[local.Name] = shadowed.Value;
        }

        return new BoundWhileStatement(condition, body);
    }

    private BoundStatement BindLoopStatement(LoopStatementSyntax loopStmt)
    {
        var body = BindStatementInLoop(loopStmt.Statement);
        return new BoundLoopStatement(body);
    }

    private BoundStatement BindLockStatement(LockStatementSyntax lockStmt)
    {
        var expression = BindExpression(lockStmt.Expression);
        var body = BindStatement(lockStmt.Statement);

        if (expression.Type is { TypeKind: not TypeKind.Error } type && !type.IsReferenceType)
        {
            _diagnostics.ReportLockExpressionMustBeReferenceType(
                type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                lockStmt.Expression.GetLocation());
        }

        return new BoundLockStatement(expression, body);
    }

    private BoundStatement BindTryStatement(TryStatementSyntax tryStmt)
    {
        var tryBlock = BindBlockStatement(tryStmt.Block);
        var catchBuilder = ImmutableArray.CreateBuilder<BoundCatchClause>();
        foreach (var catchClause in tryStmt.CatchClauses)
        {
            var boundCatchClause = BindCatchClause(catchClause);
            catchBuilder.Add(boundCatchClause);
        }

        BoundBlockStatement? finallyBlock = null;
        if (tryStmt.FinallyClause is { } finallyClause)
            finallyBlock = BindBlockStatement(finallyClause.Block);

        if (_containingSymbol is IMethodSymbol containingMethod &&
            GetReturnTargetType(containingMethod) is { } methodReturnType &&
            methodReturnType.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit &&
            TryGetIgnoredValueExpression(tryBlock, out var tryExpression) &&
            !HasExpressionErrors(tryExpression) &&
            tryExpression.Type is { SpecialType: not SpecialType.System_Void and not SpecialType.System_Unit } &&
            (catchBuilder.Count == 0 || AllCatchBlocksProduceIgnoredValues(catchBuilder)))
        {
            _diagnostics.ReportTryStatementValueIgnored(tryStmt.TryKeyword.GetLocation());
        }

        if (catchBuilder.Count == 0 && finallyBlock is null)
            return tryBlock;

        return new BoundTryStatement(tryBlock, catchBuilder.ToImmutable(), finallyBlock);
    }

    private bool AllCatchBlocksProduceIgnoredValues(ImmutableArray<BoundCatchClause>.Builder catchBuilder)
    {
        foreach (var catchClause in catchBuilder)
        {
            if (!TryGetIgnoredValueExpression(catchClause.Block, out var catchExpression) ||
                HasExpressionErrors(catchExpression) ||
                catchExpression.Type is not { SpecialType: not SpecialType.System_Void and not SpecialType.System_Unit })
            {
                return false;
            }
        }

        return catchBuilder.Count > 0;
    }

    public Dictionary<string, List<IMethodSymbol>> _functions = new();

    protected void AddFunctionToScope(IMethodSymbol symbol)
    {
        if (!_functions.TryGetValue(symbol.Name, out var functions))
        {
            functions = new List<IMethodSymbol>();
            _functions.Add(symbol.Name, functions);
        }

        functions.Add(symbol);
    }

    private Dictionary<string, int> CaptureFunctionScope()
        => _functions.ToDictionary(static pair => pair.Key, static pair => pair.Value.Count, StringComparer.Ordinal);

    private bool HasFunctionInCurrentScope(
        IReadOnlyDictionary<string, int> scope,
        IMethodSymbol symbol)
    {
        if (!_functions.TryGetValue(symbol.Name, out var functions))
            return false;

        var start = scope.TryGetValue(symbol.Name, out var count) ? count : 0;
        return functions.Skip(start).Any(candidate => HaveSameSignature(candidate, symbol));
    }

    private void RestoreFunctionScope(IReadOnlyDictionary<string, int> scope)
    {
        foreach (var name in _functions.Keys.ToArray())
        {
            if (!scope.TryGetValue(name, out var count))
            {
                _functions.Remove(name);
                continue;
            }

            var functions = _functions[name];
            if (functions.Count > count)
                functions.RemoveRange(count, functions.Count - count);
        }
    }

    protected static bool HaveSameSignature(IMethodSymbol first, IMethodSymbol second)
    {
        if (first.Parameters.Length != second.Parameters.Length)
            return false;

        if (first.TypeParameters.Length != second.TypeParameters.Length)
            return false;

        for (int i = 0; i < first.Parameters.Length; i++)
        {
            var firstType = first.Parameters[i].Type;
            var secondType = second.Parameters[i].Type;

            if (!AreTypesEquivalent(firstType, secondType, first, second))
                return false;
        }

        return true;

        static bool AreTypesEquivalent(ITypeSymbol firstType, ITypeSymbol secondType, IMethodSymbol firstMethod, IMethodSymbol secondMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(firstType, secondType))
                return true;

            if (firstType is ITypeParameterSymbol firstParam && secondType is ITypeParameterSymbol secondParam)
            {
                if (!SymbolEqualityComparer.Default.Equals(firstParam.ContainingSymbol, firstMethod) ||
                    !SymbolEqualityComparer.Default.Equals(secondParam.ContainingSymbol, secondMethod))
                {
                    return false;
                }

                if (firstParam is SourceTypeParameterSymbol firstSource && secondParam is SourceTypeParameterSymbol secondSource)
                    return firstSource.Ordinal == secondSource.Ordinal;

                return SymbolEqualityComparer.Default.Equals(firstParam, secondParam);
            }

            if (firstType is INamedTypeSymbol firstNamed && secondType is INamedTypeSymbol secondNamed)
            {
                if (!SymbolEqualityComparer.Default.Equals(firstNamed.ConstructedFrom, secondNamed.ConstructedFrom))
                    return false;

                var firstArguments = firstNamed.TypeArguments;
                var secondArguments = secondNamed.TypeArguments;

                if (firstArguments.Length != secondArguments.Length)
                    return false;

                for (int i = 0; i < firstArguments.Length; i++)
                {
                    if (!AreTypesEquivalent(firstArguments[i], secondArguments[i], firstMethod, secondMethod))
                        return false;
                }

                return true;
            }

            if (firstType is IArrayTypeSymbol firstArray && secondType is IArrayTypeSymbol secondArray)
            {
                if (firstArray.Rank != secondArray.Rank)
                    return false;

                return AreTypesEquivalent(firstArray.ElementType, secondArray.ElementType, firstMethod, secondMethod);
            }

            return false;
        }
    }

    public virtual BoundBlockStatement BindBlockStatement(BlockStatementSyntax block)
    {
        if (TryGetCachedBoundNode(block) is BoundStatement cached)
            return (BoundBlockStatement)cached;

        SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
        _scopeDepth++;
        var depth = _scopeDepth;
        var functionScope = CaptureFunctionScope();

        try
        {
            EnsureLabelsDeclared(block);

            foreach (var stmt in block.Statements)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();

                if (stmt is not TypeDeclarationStatementSyntax typeDeclarationStatement)
                    continue;

                var symbol = SemanticModel.EnsureLocalTypeDeclarationBound(typeDeclarationStatement.Declaration, this);
                if (_localTypes.TryGetValue(symbol.Name, out var existing) && existing.Depth == depth)
                {
                    var isSameDeclaration = existing.Symbol.DeclaringSyntaxReferences.Any(reference =>
                        reference.SyntaxTree == typeDeclarationStatement.SyntaxTree &&
                        reference.Span == typeDeclarationStatement.Declaration.Span);

                    if (!isSameDeclaration)
                        _diagnostics.ReportTypeAlreadyDefined(symbol.Name, typeDeclarationStatement.Declaration.Identifier.GetLocation());
                }
                else
                {
                    _localTypes[symbol.Name] = (symbol, depth);
                }
            }

            foreach (var stmt in block.Statements)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();

                if (stmt is FunctionStatementSyntax func)
                {
                    var semanticModel = SemanticModel;
                    if (semanticModel is null)
                        continue;

                    var functionBinder = semanticModel.GetBinder(func, this);
                    if (functionBinder is FunctionBinder lfBinder)
                    {
                        var symbol = lfBinder.GetMethodSymbol();
                        if (HasFunctionInCurrentScope(functionScope, symbol))
                            _diagnostics.ReportFunctionAlreadyDefined(symbol.Name, func.Identifier.GetLocation());
                        else
                            AddFunctionToScope(symbol);
                    }
                }
            }

            var boundStatements = new List<BoundStatement>(block.Statements.Count);
            foreach (var stmt in block.Statements)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
                var bound = BindStatement(stmt);
                boundStatements.Add(bound);
            }

            var localsAtDepth = _localsToDispose
                .Where(l => l.Depth == depth)
                .Select(l => l.Local)
                .ToList();

            if (localsAtDepth.Count > 0)
                _localsToDispose.RemoveAll(l => l.Depth == depth);

            var blockStmt = new BoundBlockStatement(boundStatements.ToArray(), localsAtDepth.ToImmutableArray());
            CacheBoundNode(block, blockStmt);

            SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
            ReportUnreachableStatements(block);

            return blockStmt;
        }
        finally
        {
            foreach (var name in _locals.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _locals.Remove(name);

            foreach (var name in _localTypes.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _localTypes.Remove(name);

            RestoreFunctionScope(functionScope);

            _scopeDepth--;
        }
    }

    public virtual BoundBlockExpression BindBlock(
        BlockSyntax block,
        bool allowReturn = true,
        bool isExpressionContext = true)
    {
        var targetType = GetScopedTargetType(block);
        var cachedNode = targetType is null
            ? TryGetCachedBoundNode(block)
            : TryGetCachedBoundNode(block, targetType);
        if (cachedNode is BoundExpression cached)
            return (BoundBlockExpression)cached;

        SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
        _scopeDepth++;
        var depth = _scopeDepth;
        var functionScope = CaptureFunctionScope();

        if (isExpressionContext)
            _expressionContextDepth++;

        try
        {
            EnsureLabelsDeclared(block);

            foreach (var stmt in block.Statements)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();

                if (stmt is not TypeDeclarationStatementSyntax typeDeclarationStatement)
                    continue;

                var symbol = SemanticModel.EnsureLocalTypeDeclarationBound(typeDeclarationStatement.Declaration, this);
                if (_localTypes.TryGetValue(symbol.Name, out var existing) && existing.Depth == depth)
                {
                    var isSameDeclaration = existing.Symbol.DeclaringSyntaxReferences.Any(reference =>
                        reference.SyntaxTree == typeDeclarationStatement.SyntaxTree &&
                        reference.Span == typeDeclarationStatement.Declaration.Span);

                    if (!isSameDeclaration)
                        _diagnostics.ReportTypeAlreadyDefined(symbol.Name, typeDeclarationStatement.Declaration.Identifier.GetLocation());
                }
                else
                {
                    _localTypes[symbol.Name] = (symbol, depth);
                }
            }

            foreach (var stmt in block.Statements)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();

                if (stmt is FunctionStatementSyntax func)
                {
                    var functionBinder = SemanticModel.GetBinder(func, this);
                    if (functionBinder is FunctionBinder lfBinder)
                    {
                        var symbol = lfBinder.GetMethodSymbol();
                        if (HasFunctionInCurrentScope(functionScope, symbol))
                            _diagnostics.ReportFunctionAlreadyDefined(symbol.Name, func.Identifier.GetLocation());
                        else
                            AddFunctionToScope(symbol);
                    }
                }
            }

            var boundStatements = new List<BoundStatement>(block.Statements.Count);
            var hasDisallowedReturnInExpressionContext = false;
            for (var statementIndex = 0; statementIndex < block.Statements.Count; statementIndex++)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
                var stmt = block.Statements[statementIndex];

                BoundStatement bound;
                if (!allowReturn && stmt is ReturnStatementSyntax ret)
                {
                    hasDisallowedReturnInExpressionContext = true;
                    _diagnostics.ReportReturnStatementInExpression(stmt.GetLocation());
                    var expr = ret.Expression is null
                        ? BoundFactory.UnitExpression()
                        : BindExpression(ret.Expression);
                    bound = new BoundExpressionStatement(expr);
                }
                else if (isExpressionContext &&
                         targetType is not null &&
                         statementIndex == block.Statements.Count - 1 &&
                         stmt is ExpressionStatementSyntax valueStatement)
                {
                    bound = BindExpressionStatement(valueStatement, targetType);
                }
                else
                {
                    bound = BindStatement(stmt);
                    if (!allowReturn && bound is BoundReturnStatement br)
                    {
                        hasDisallowedReturnInExpressionContext = true;
                        _diagnostics.ReportReturnStatementInExpression(stmt.GetLocation());
                        var expr = br.Expression ?? BoundFactory.UnitExpression();
                        bound = new BoundExpressionStatement(expr);
                    }
                }
                boundStatements.Add(bound);
            }

            var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
            var localsAtDepth = _localsToDispose
                .Where(l => l.Depth == depth)
                .Select(l => l.Local)
                .ToList();

            if (localsAtDepth.Count > 0)
                _localsToDispose.RemoveAll(l => l.Depth == depth);

            var blockExpr = new BoundBlockExpression(boundStatements.ToArray(), unitType, localsAtDepth.ToImmutableArray());
            if (targetType is null)
                CacheBoundNode(block, blockExpr);
            else
                CacheBoundNode(block, blockExpr, targetType);

            if (allowReturn || !hasDisallowedReturnInExpressionContext)
            {
                SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
                ReportUnreachableStatements(block);
            }

            return blockExpr;
        }
        finally
        {
            foreach (var name in _locals.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _locals.Remove(name);

            foreach (var name in _localTypes.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _localTypes.Remove(name);

            RestoreFunctionScope(functionScope);

            _scopeDepth--;
            if (isExpressionContext)
                _expressionContextDepth--;
        }
    }

    private void ReportUnreachableStatements(SyntaxNode block)
    {
        if (!SemanticModel.IsCollectingBindingDiagnosticsForCurrentFlow)
            return;

        var controlFlow = block switch
        {
            BlockStatementSyntax statementBlock => SemanticModel.AnalyzeControlFlowInternal(new ControlFlowRegion(statementBlock), statementBlock, analyzeJumpPoints: false),
            BlockSyntax expressionBlock when expressionBlock.Statements.Count > 0 => SemanticModel.AnalyzeControlFlowInternal(expressionBlock, analyzeJumpPoints: false),
            _ => null
        };

        if (controlFlow is not { Succeeded: true })
            return;

        foreach (var statement in controlFlow.UnreachableStatements)
            _diagnostics.ReportUnreachableCodeDetected(statement.GetLocation());
    }

    private BoundStatement BindForStatement(ForStatementSyntax forStmt)
    {
        var semanticModel = SemanticModel;
        var loopBinder = semanticModel is null || forStmt.SyntaxTree is null
            ? this
            : semanticModel.GetBinder(forStmt, this) as BlockBinder ?? this;
        using var loopExecutionScope = EnterNestedBinderExecutionScope(loopBinder);
        BoundExpression collection;
        ForIterationInfo iteration;
        var isAwaitFor = forStmt.AwaitKeyword.Kind == SyntaxKind.AwaitKeyword;

        if (isAwaitFor && !IsAwaitExpressionAllowed())
            _diagnostics.ReportAwaitExpressionRequiresAsyncContext(forStmt.AwaitKeyword.GetLocation());

        if (!isAwaitFor && forStmt.Expression is RangeExpressionSyntax rangeSyntax)
        {
            (collection, iteration) = loopBinder.BindForRangeIteration(forStmt, rangeSyntax);
            CacheBoundNode(forStmt.Expression, collection);
        }
        else
        {
            collection = BindExpression(forStmt.Expression);
            iteration = isAwaitFor
                ? ClassifyAwaitForIteration(collection, forStmt.Expression)
                : ClassifyForIteration(collection, forStmt.Expression);
            CacheBoundNode(forStmt.Expression, collection);

            if (forStmt.ByKeyword.Kind == SyntaxKind.ByKeyword)
                _diagnostics.ReportRangeForLoopByClauseRequiresRange(forStmt.ByKeyword.GetLocation());
        }

        ILocalSymbol? local = null;
        BoundStatement body;

        switch (forStmt.Target)
        {
            case null:
                body = loopBinder.BindStatementInLoop(forStmt.Body);
                break;

            case DiscardPatternSyntax:
                body = loopBinder.BindStatementInLoop(forStmt.Body);
                break;

            case IdentifierNameSyntax identifierName:
                local = loopBinder.BindForSimpleIterationTarget(
                    forStmt,
                    identifierName.Identifier,
                    typeAnnotation: null,
                    declaringSyntax: forStmt,
                    iteration.ElementType);
                body = loopBinder.BindStatementInLoop(forStmt.Body);
                break;

            case VariablePatternSyntax variablePattern
                when TryGetTypedForSimpleIterationTarget(variablePattern, out var singleDesignation, out var typeAnnotation):
                local = loopBinder.BindForSimpleIterationTarget(
                    forStmt,
                    singleDesignation.Identifier,
                    typeAnnotation,
                    singleDesignation,
                    iteration.ElementType);
                body = loopBinder.BindStatementInLoop(forStmt.Body);
                break;

            case PatternSyntax patternSyntax:
                local = loopBinder.CreateLocalSymbol(forStmt, $"__forPattern{loopBinder._tempCounter++}", isMutable: false, iteration.ElementType);
                var inlineBindingKeyword = FindFirstInlinePatternBindingKeyword(patternSyntax);
                if (inlineBindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword &&
                    forStmt.BindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                {
                    loopBinder._diagnostics.ReportPatternDeclarationBindingKeywordConflict(
                        forStmt.BindingKeyword.Text,
                        inlineBindingKeyword.Text,
                        inlineBindingKeyword.GetLocation());
                }

                var previousBindingKeyword = loopBinder._ambientPatternDeclarationBindingKeyword;
                loopBinder._ambientPatternDeclarationBindingKeyword = forStmt.BindingKeyword.Kind;
                BoundPattern pattern;
                try
                {
                    pattern = loopBinder.BindPattern(patternSyntax, iteration.ElementType);
                }
                finally
                {
                    loopBinder._ambientPatternDeclarationBindingKeyword = previousBindingKeyword;
                }

                var loweredBody = loopBinder.BindStatementInLoop(forStmt.Body);
                var condition = new BoundIsPatternExpression(new BoundLocalAccess(local), pattern, Compilation.GetSpecialType(SpecialType.System_Boolean));
                body = new BoundIfStatement(condition, loweredBody);
                break;

            default:
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(forStmt.Target.GetLocation());
                body = loopBinder.BindStatementInLoop(forStmt.Body);
                break;
        }

        return new BoundForStatement(local, iteration, collection, body);
    }

    private SourceLocalSymbol? BindForSimpleIterationTarget(
        ForStatementSyntax forStmt,
        SyntaxToken identifier,
        TypeAnnotationClauseSyntax? typeAnnotation,
        SyntaxNode declaringSyntax,
        ITypeSymbol elementType)
    {
        if (forStmt.BindingKeyword.Kind == SyntaxKind.VarKeyword)
        {
            _diagnostics.ReportForIdentifierBindingKeywordMustBeValOrLet(
                identifier.ValueText,
                forStmt.BindingKeyword.GetLocation());
        }

        var localType = BindForIterationTargetType(typeAnnotation, elementType);
        if (string.IsNullOrWhiteSpace(identifier.ValueText) || identifier.ValueText == "_")
            return null;

        var local = CreateLocalSymbol(declaringSyntax, identifier.ValueText, isMutable: false, localType);
        if (declaringSyntax is SingleVariableDesignationSyntax singleDesignation)
            CacheBoundNode(singleDesignation, new BoundSingleVariableDesignator(local));

        return local;
    }

    private ITypeSymbol BindForIterationTargetType(TypeAnnotationClauseSyntax? typeAnnotation, ITypeSymbol elementType)
    {
        var normalizedElementType = TypeSymbolNormalization.NormalizeForInference(elementType);
        if (typeAnnotation is null)
            return normalizedElementType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : normalizedElementType;

        var declaredType = ResolveTypeSyntaxOrError(typeAnnotation.Type);
        declaredType = EnsureTypeAccessible(declaredType, typeAnnotation.Type.GetLocation());
        declaredType = EnsureTypeValidForStorageLocation(declaredType, typeAnnotation.Type.GetLocation());

        var sourceType = normalizedElementType.UnwrapLiteralType() ?? normalizedElementType;
        if (declaredType.TypeKind != TypeKind.Error &&
            sourceType.TypeKind != TypeKind.Error &&
            !IsAssignable(declaredType, sourceType, out _))
        {
            ReportCannotAssignFromTypeToType(sourceType, declaredType, typeAnnotation.Type.GetLocation());
            return Compilation.ErrorTypeSymbol;
        }

        return declaredType;
    }

    private static bool TryGetTypedForSimpleIterationTarget(
        VariablePatternSyntax variablePattern,
        out SingleVariableDesignationSyntax singleDesignation,
        out TypeAnnotationClauseSyntax typeAnnotation)
    {
        if (variablePattern is
            {
                BindingKeyword.Kind: SyntaxKind.None,
                Designation: TypedVariableDesignationSyntax
                {
                    Designation: SingleVariableDesignationSyntax single,
                    TypeAnnotation: { } annotation
                }
            })
        {
            singleDesignation = single;
            typeAnnotation = annotation;
            return true;
        }

        singleDesignation = null!;
        typeAnnotation = null!;
        return false;
    }

    private (BoundExpression Collection, ForIterationInfo Iteration) BindForRangeIteration(ForStatementSyntax forStatement, RangeExpressionSyntax rangeSyntax)
    {
        var start = BindForRangeBoundary(rangeSyntax.LeftExpression);
        if (start is BoundErrorExpression)
        {
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        var end = BindForRangeBoundary(rangeSyntax.RightExpression);
        if (end is BoundErrorExpression)
        {
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        if (end is null)
        {
            _diagnostics.ReportRangeForLoopRequiresEnd(rangeSyntax.GetLocation());
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        var endType = TypeSymbolNormalization.NormalizeForInference(end.Type ?? Compilation.ErrorTypeSymbol);
        var startType = TypeSymbolNormalization.NormalizeForInference(start?.Type ?? endType);

        if (!TryInferBestCommonType(startType, endType, out var elementType))
        {
            ReportCannotConvertFromTypeToType(
                startType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                endType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                rangeSyntax.GetLocation());
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        elementType = elementType.UnwrapLiteralType() ?? elementType;
        if (!IsSupportedForRangeLoopType(elementType))
        {
            ReportCannotConvertFromTypeToType(
                elementType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Compilation.GetSpecialType(SpecialType.System_Int32).ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                rangeSyntax.GetLocation());
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        var startValue = start ?? CreateZeroForRangeLoop(elementType);
        if (!TryConvertForRangeBoundary(startValue, elementType, rangeSyntax.LeftExpression ?? rangeSyntax, out var convertedStart))
        {
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        if (!TryConvertForRangeBoundary(end, elementType, rangeSyntax.RightExpression!, out var convertedEnd))
        {
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        BoundExpression convertedStep;
        if (forStatement.ByKeyword.Kind == SyntaxKind.ByKeyword)
        {
            var stepSyntax = forStatement.StepExpression;
            if (stepSyntax is null)
            {
                _diagnostics.ReportRangeForLoopStepCannotBeZero(forStatement.ByKeyword.GetLocation());
                var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
                return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
            }

            var step = BindExpression(stepSyntax);
            if (step is BoundErrorExpression)
            {
                var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
                return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
            }

            if (!TryConvertForRangeBoundary(step, elementType, stepSyntax, out convertedStep))
            {
                var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
                return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
            }
        }
        else
        {
            convertedStep = CreateOneForRangeLoop(elementType);
        }

        if (IsRangeStepConstantZero(convertedStep))
        {
            _diagnostics.ReportRangeForLoopStepCannotBeZero(
                (forStatement.StepExpression ?? forStatement.ByKeyword.Parent ?? forStatement).GetLocation());
            var error = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return (error, ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol));
        }

        var range = new BoundRangeExpression(
            new BoundIndexExpression(convertedStart, isFromEnd: false, GetIndexType()),
            new BoundIndexExpression(convertedEnd, isFromEnd: false, GetIndexType()),
            GetRangeType(),
            rangeSyntax.LessThanToken.Kind == SyntaxKind.LessThanToken);

        return (
            range,
            ForIterationInfo.ForRange(
                elementType,
                convertedStart,
                convertedEnd,
                convertedStep,
                range,
                rangeUpperExclusive: range.IsUpperExclusive));
    }

    private ForIterationInfo ClassifyForIteration(BoundExpression collection, ExpressionSyntax iterationSyntax)
    {
        var collectionType = collection.Type;
        if (collectionType?.ContainsErrorType() == true)
            return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);

        if (collection is BoundRangeExpression range)
        {
            var rangeIteration = ClassifyRangeIteration(range, iterationSyntax);
            if (rangeIteration is not null)
                return rangeIteration;
        }

        if (collectionType is IArrayTypeSymbol arrayType)
            return ForIterationInfo.ForArray(arrayType);

        if (collectionType is not null &&
            TryClassifyForEnumerator(collectionType, out var iteration))
        {
            return iteration;
        }

        var enumerableType = Compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
        ReportCannotConvertFromTypeToType(
            collectionType ?? Compilation.ErrorTypeSymbol,
            enumerableType,
            iterationSyntax.GetLocation());

        return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
    }

    private ForIterationInfo ClassifyAwaitForIteration(BoundExpression collection, ExpressionSyntax iterationSyntax)
    {
        var collectionType = collection.Type;
        if (collectionType?.ContainsErrorType() == true)
            return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);

        if (collectionType is not null &&
            TryClassifyAwaitForEnumerator(collectionType, out var iteration))
        {
            return iteration;
        }

        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var asyncEnumerableDefinition = Compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1") as INamedTypeSymbol;
        var asyncEnumerableType = asyncEnumerableDefinition is not null
            ? (ITypeSymbol)asyncEnumerableDefinition.Construct(objectType)
            : Compilation.ErrorTypeSymbol;

        ReportCannotConvertFromTypeToType(
            collectionType ?? Compilation.ErrorTypeSymbol,
            asyncEnumerableType,
            iterationSyntax.GetLocation());

        return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
    }

    private bool TryClassifyAwaitForEnumerator(ITypeSymbol collectionType, out ForIterationInfo iteration)
    {
        if (collectionType is INamedTypeSymbol namedType)
        {
            if (TryClassifyAsyncPatternGetEnumerator(namedType, out iteration))
                return true;

            if (TryClassifyAsyncEnumerableInterface(namedType, out iteration))
                return true;
        }

        if (collectionType is ITypeParameterSymbol typeParameter)
        {
            foreach (var constraintType in typeParameter.ConstraintTypes)
            {
                if (TryClassifyAwaitForEnumerator(constraintType, out iteration))
                    return true;
            }
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryClassifyAsyncPatternGetEnumerator(
        INamedTypeSymbol receiverType,
        out ForIterationInfo iteration)
    {
        foreach (var getAsyncEnumerator in receiverType
                     .GetMembers("GetAsyncEnumerator")
                     .OfType<IMethodSymbol>()
                     .Where(static method => !method.IsStatic))
        {
            if (!IsSymbolAccessible(getAsyncEnumerator))
                continue;

            if (!CanInvokeAsyncEnumeratorFactoryWithoutArguments(getAsyncEnumerator))
                continue;

            if (!TryResolveAsyncEnumeratorMembers(getAsyncEnumerator.ReturnType, out var moveNextAsyncMethod, out var currentGetter, out var disposeAsyncMethod))
                continue;

            iteration = ForIterationInfo.ForAsync(
                currentGetter.ReturnType,
                getAsyncEnumerator,
                moveNextAsyncMethod,
                currentGetter,
                disposeAsyncMethod);
            return true;
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryClassifyAsyncEnumerableInterface(INamedTypeSymbol collectionType, out ForIterationInfo iteration)
    {
        if (!TryGetGenericAsyncEnumerableInterface(collectionType, out var asyncEnumerableInterface))
        {
            iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
            return false;
        }

        foreach (var getAsyncEnumeratorMethod in asyncEnumerableInterface
                     .GetMembers("GetAsyncEnumerator")
                     .OfType<IMethodSymbol>()
                     .Where(static method => !method.IsStatic))
        {
            if (!CanInvokeAsyncEnumeratorFactoryWithoutArguments(getAsyncEnumeratorMethod))
                continue;

            if (!TryResolveAsyncEnumeratorMembers(getAsyncEnumeratorMethod.ReturnType, out var moveNextAsyncMethod, out var currentGetter, out var disposeAsyncMethod))
                continue;

            var elementType = asyncEnumerableInterface.TypeArguments.Length == 1
                ? asyncEnumerableInterface.TypeArguments[0]
                : currentGetter.ReturnType;

            iteration = ForIterationInfo.ForAsync(
                elementType,
                getAsyncEnumeratorMethod,
                moveNextAsyncMethod,
                currentGetter,
                disposeAsyncMethod);
            return true;
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryResolveAsyncEnumeratorMembers(
        ITypeSymbol enumeratorType,
        out IMethodSymbol moveNextAsyncMethod,
        out IMethodSymbol currentGetter,
        out IMethodSymbol? disposeAsyncMethod)
    {
        moveNextAsyncMethod = null!;
        currentGetter = null!;
        disposeAsyncMethod = null;

        if (enumeratorType is not INamedTypeSymbol namedEnumerator)
            return false;

        foreach (var method in namedEnumerator.GetMembers("MoveNextAsync").OfType<IMethodSymbol>())
        {
            if (method.IsStatic || method.Parameters.Length != 0)
                continue;

            if (!IsAsyncMoveNextReturnType(method.ReturnType))
                continue;

            if (TryResolveEnumeratorCurrentGetter(namedEnumerator, out currentGetter))
            {
                moveNextAsyncMethod = method;
                disposeAsyncMethod = TryResolveAsyncDisposeMethod(namedEnumerator);
                return true;
            }
        }

        foreach (var interfaceType in namedEnumerator.AllInterfaces.OfType<INamedTypeSymbol>())
        {
            foreach (var method in interfaceType.GetMembers("MoveNextAsync").OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.Parameters.Length != 0)
                    continue;

                if (!IsAsyncMoveNextReturnType(method.ReturnType))
                    continue;

                if (TryResolveEnumeratorCurrentGetter(interfaceType, out currentGetter))
                {
                    moveNextAsyncMethod = method;
                    disposeAsyncMethod = TryResolveAsyncDisposeMethod(interfaceType) ?? TryResolveAsyncDisposeMethod(namedEnumerator);
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsAsyncMoveNextReturnType(ITypeSymbol returnType)
    {
        if (!AwaitablePattern.TryFind(returnType, IsSymbolAccessible, out var awaitable, out _, out _))
            return false;

        return awaitable.GetResultMethod.ReturnType.SpecialType == SpecialType.System_Boolean;
    }

    private static bool CanInvokeAsyncEnumeratorFactoryWithoutArguments(IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
            return true;

        return method.Parameters.All(static p => p.IsOptional);
    }

    private IMethodSymbol? TryResolveAsyncDisposeMethod(INamedTypeSymbol enumeratorType)
    {
        foreach (var method in new[] { enumeratorType }.Concat(enumeratorType.AllInterfaces).SelectMany(type => type.GetMembers("DisposeAsync")).OfType<IMethodSymbol>())
        {
            if (method.IsStatic || method.Parameters.Length != 0)
                continue;

            if (!IsSymbolAccessible(method))
                continue;

            if (AwaitablePattern.TryFind(method.ReturnType, IsSymbolAccessible, out _, out _, out _))
                return method;
        }

        return null;
    }

    private ForIterationInfo? ClassifyRangeIteration(BoundRangeExpression range, ExpressionSyntax iterationSyntax)
    {
        var end = range.Right;

        if (end is null)
        {
            _diagnostics.ReportRangeForLoopRequiresEnd(iterationSyntax.GetLocation());
            return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        }

        var start = range.Left ?? CreateZeroIndex();

        if (start.IsFromEnd || end.IsFromEnd)
        {
            _diagnostics.ReportRangeForLoopFromEndNotSupported(iterationSyntax.GetLocation());
            return ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        }

        var normalizedRange = range.Left is null
            ? new BoundRangeExpression(start, end, range.Type, range.IsUpperExclusive)
            : range;

        return ForIterationInfo.ForRange(
            Compilation.GetSpecialType(SpecialType.System_Int32),
            normalizedRange.Left!.Value,
            normalizedRange.Right!.Value,
            CreateOneForRangeLoop(Compilation.GetSpecialType(SpecialType.System_Int32)),
            normalizedRange,
            rangeUpperExclusive: normalizedRange.IsUpperExclusive);
    }

    private BoundExpression? BindForRangeBoundary(ExpressionSyntax? endpointSyntax)
    {
        if (endpointSyntax is null || endpointSyntax.IsMissing)
            return null;

        var bound = BindExpression(endpointSyntax);
        if (IsErrorExpression(bound))
            return bound;

        if (bound is BoundIndexExpression index)
        {
            if (index.IsFromEnd)
            {
                _diagnostics.ReportRangeForLoopFromEndNotSupported(endpointSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }

            return index.Value;
        }

        return bound;
    }

    private bool TryConvertForRangeBoundary(
        BoundExpression boundary,
        ITypeSymbol targetType,
        SyntaxNode diagnosticNode,
        out BoundExpression convertedBoundary)
    {
        convertedBoundary = boundary;

        if (!ShouldAttemptConversion(boundary))
            return true;

        if (!IsAssignable(targetType, boundary, out var conversion))
        {
            ReportCannotConvertFromTypeToType(
                (boundary.Type ?? Compilation.ErrorTypeSymbol).ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                targetType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                diagnosticNode.GetLocation());
            convertedBoundary = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            return false;
        }

        convertedBoundary = ApplyConversion(boundary, targetType, conversion, diagnosticNode);
        return !IsErrorExpression(convertedBoundary);
    }

    private BoundExpression CreateZeroForRangeLoop(ITypeSymbol targetType)
    {
        targetType = targetType.UnwrapLiteralType() ?? targetType;

        object value = targetType.SpecialType switch
        {
            SpecialType.System_Byte => (byte)0,
            SpecialType.System_SByte => (sbyte)0,
            SpecialType.System_Int16 => (short)0,
            SpecialType.System_UInt16 => (ushort)0,
            SpecialType.System_Int32 => 0,
            SpecialType.System_UInt32 => 0u,
            SpecialType.System_Int64 => 0L,
            SpecialType.System_UInt64 => 0UL,
            SpecialType.System_Char => '\0',
            SpecialType.System_Single => 0f,
            SpecialType.System_Double => 0d,
            SpecialType.System_Decimal => 0m,
            _ => 0
        };

        var kind = targetType.SpecialType == SpecialType.System_Char
            ? BoundLiteralExpressionKind.CharLiteral
            : BoundLiteralExpressionKind.NumericLiteral;

        return new BoundLiteralExpression(kind, value, targetType);
    }

    private BoundExpression CreateOneForRangeLoop(ITypeSymbol targetType)
    {
        targetType = targetType.UnwrapLiteralType() ?? targetType;

        object value = targetType.SpecialType switch
        {
            SpecialType.System_Byte => (byte)1,
            SpecialType.System_SByte => (sbyte)1,
            SpecialType.System_Int16 => (short)1,
            SpecialType.System_UInt16 => (ushort)1,
            SpecialType.System_Int32 => 1,
            SpecialType.System_UInt32 => 1u,
            SpecialType.System_Int64 => 1L,
            SpecialType.System_UInt64 => 1UL,
            SpecialType.System_Char => (char)1,
            SpecialType.System_Single => 1f,
            SpecialType.System_Double => 1d,
            SpecialType.System_Decimal => 1m,
            _ => 1
        };

        var kind = targetType.SpecialType == SpecialType.System_Char
            ? BoundLiteralExpressionKind.CharLiteral
            : BoundLiteralExpressionKind.NumericLiteral;

        return new BoundLiteralExpression(kind, value, targetType);
    }

    private static bool IsRangeStepConstantZero(BoundExpression step)
    {
        if (step is not BoundLiteralExpression literal)
            return false;

        return literal.Value switch
        {
            byte v => v == 0,
            sbyte v => v == 0,
            short v => v == 0,
            ushort v => v == 0,
            int v => v == 0,
            uint v => v == 0,
            long v => v == 0,
            ulong v => v == 0,
            float v => v == 0f,
            double v => v == 0d,
            decimal v => v == 0m,
            char v => v == '\0',
            _ => false
        };
    }

    private static bool IsSupportedForRangeLoopType(ITypeSymbol type)
    {
        type = type.UnwrapLiteralType() ?? type;

        return type.SpecialType switch
        {
            SpecialType.System_Byte => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Char => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_Decimal => true,
            _ => false
        };
    }

    private BoundIndexExpression CreateZeroIndex()
    {
        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);
        var zero = new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0, intType);
        return new BoundIndexExpression(zero, isFromEnd: false, GetIndexType());
    }

    private bool TryClassifyForEnumerator(ITypeSymbol collectionType, out ForIterationInfo iteration)
    {
        if (collectionType is INamedTypeSymbol namedType)
        {
            if ((IsGenericIEnumerableType(namedType) ||
                 namedType.SpecialType == SpecialType.System_Collections_IEnumerable) &&
                TryClassifyEnumerableInterface(namedType, out iteration))
            {
                return true;
            }

            if (TryClassifyPatternGetEnumerator(namedType, includeExtensions: false, out iteration))
                return true;

            if (TryClassifyPatternGetEnumerator(namedType, includeExtensions: true, out iteration))
                return true;

            if (TryClassifyEnumerableInterface(namedType, out iteration))
                return true;
        }

        if (collectionType is ITypeParameterSymbol typeParameter)
        {
            foreach (var constraintType in typeParameter.ConstraintTypes)
            {
                if (TryClassifyForEnumerator(constraintType, out iteration))
                    return true;
            }
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryClassifyPatternGetEnumerator(
        INamedTypeSymbol receiverType,
        bool includeExtensions,
        out ForIterationInfo iteration)
    {
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        ForIterationInfo? fallbackIteration = null;

        IEnumerable<IMethodSymbol> candidates = includeExtensions
            ? LookupExtensionMethods("GetEnumerator", receiverType)
            : receiverType
                .GetMembers("GetEnumerator")
                .OfType<IMethodSymbol>()
                .Where(static method => !method.IsStatic);

        foreach (var getEnumerator in candidates)
        {
            if (!IsSymbolAccessible(getEnumerator))
                continue;

            if (includeExtensions)
            {
                if (!getEnumerator.IsExtensionMethod || getEnumerator.Parameters.Length != 1)
                    continue;
            }
            else if (getEnumerator.Parameters.Length != 0)
            {
                continue;
            }

            if (!TryResolveEnumeratorMembers(getEnumerator.ReturnType, out var moveNextMethod, out var currentGetter))
                continue;

            var candidateIteration = CreatePatternIteration(getEnumerator, moveNextMethod, currentGetter);
            if (!SymbolEqualityComparer.Default.Equals(candidateIteration.ElementType, objectType))
            {
                iteration = candidateIteration;
                return true;
            }

            fallbackIteration ??= candidateIteration;
        }

        if (fallbackIteration is not null)
        {
            iteration = fallbackIteration;
            return true;
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryClassifyEnumerableInterface(INamedTypeSymbol collectionType, out ForIterationInfo iteration)
    {
        if (TryGetGenericEnumerableInterface(collectionType, out var genericEnumerable) &&
            TryResolveInterfaceEnumeratorPattern(genericEnumerable, out iteration))
        {
            return true;
        }

        var nonGenericEnumerable = Compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
        if (nonGenericEnumerable is INamedTypeSymbol nonGenericEnumerableInterface &&
            ImplementsInterface(collectionType, nonGenericEnumerableInterface) &&
            TryResolveInterfaceEnumeratorPattern(nonGenericEnumerableInterface, out iteration))
        {
            return true;
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private bool TryResolveInterfaceEnumeratorPattern(
        INamedTypeSymbol enumerableInterface,
        out ForIterationInfo iteration)
    {
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        ForIterationInfo? fallbackIteration = null;

        foreach (var getEnumeratorMethod in enumerableInterface
                     .GetMembers("GetEnumerator")
                     .OfType<IMethodSymbol>()
                     .Where(static method => !method.IsStatic && method.Parameters.Length == 0))
        {
            if (!TryResolveEnumeratorMembers(getEnumeratorMethod.ReturnType, out var moveNextMethod, out var currentGetter))
                continue;

            ForIterationInfo candidateIteration;
            if (IsGenericIEnumerableType(enumerableInterface) &&
                enumerableInterface.TypeArguments.Length == 1 &&
                enumerableInterface.TypeArguments[0].TypeKind != TypeKind.Error &&
                getEnumeratorMethod.ReturnType is INamedTypeSymbol enumeratorInterface)
            {
                candidateIteration = ForIterationInfo.ForGeneric(
                    enumerableInterface,
                    enumeratorInterface,
                    getEnumeratorMethod,
                    moveNextMethod,
                    currentGetter);
            }
            else
            {
                candidateIteration = ForIterationInfo.ForNonGeneric(
                    currentGetter.ReturnType,
                    getEnumeratorMethod,
                    moveNextMethod,
                    currentGetter);
            }

            if (!SymbolEqualityComparer.Default.Equals(candidateIteration.ElementType, objectType))
            {
                iteration = candidateIteration;
                return true;
            }

            fallbackIteration ??= candidateIteration;
        }

        if (fallbackIteration is not null)
        {
            iteration = fallbackIteration;
            return true;
        }

        iteration = ForIterationInfo.ForNonGeneric(Compilation.ErrorTypeSymbol);
        return false;
    }

    private ForIterationInfo CreatePatternIteration(
        IMethodSymbol getEnumeratorMethod,
        IMethodSymbol moveNextMethod,
        IMethodSymbol currentGetter)
    {
        if (currentGetter.ReturnType.SpecialType == SpecialType.System_Object)
        {
            return ForIterationInfo.ForNonGeneric(
                currentGetter.ReturnType,
                getEnumeratorMethod,
                moveNextMethod,
                currentGetter);
        }

        return new ForIterationInfo(
            ForIterationKind.Generic,
            currentGetter.ReturnType,
            GetEnumeratorMethod: getEnumeratorMethod,
            MoveNextMethod: moveNextMethod,
            CurrentGetter: currentGetter);
    }

    private bool TryResolveEnumeratorMembers(
        ITypeSymbol enumeratorType,
        out IMethodSymbol moveNextMethod,
        out IMethodSymbol currentGetter)
    {
        if (enumeratorType is not INamedTypeSymbol namedEnumerator)
        {
            moveNextMethod = null!;
            currentGetter = null!;
            return false;
        }

        foreach (var method in namedEnumerator.GetMembers("MoveNext").OfType<IMethodSymbol>())
        {
            if (method.IsStatic || method.Parameters.Length != 0)
                continue;

            if (method.ReturnType.SpecialType != SpecialType.System_Boolean)
                continue;

            if (TryResolveEnumeratorCurrentGetter(namedEnumerator, out currentGetter))
            {
                moveNextMethod = method;
                return true;
            }
        }

        foreach (var interfaceType in namedEnumerator.AllInterfaces.OfType<INamedTypeSymbol>())
        {
            foreach (var method in interfaceType.GetMembers("MoveNext").OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.Parameters.Length != 0)
                    continue;

                if (method.ReturnType.SpecialType != SpecialType.System_Boolean)
                    continue;

                // Prefer Current from the original enumerator type (e.g. IEnumerator<T> returns T)
                // over the interface that declared MoveNext (e.g. IEnumerator returns object).
                if (TryResolveEnumeratorCurrentGetter(namedEnumerator, out currentGetter) ||
                    TryResolveEnumeratorCurrentGetter(interfaceType, out currentGetter))
                {
                    moveNextMethod = method;
                    return true;
                }
            }
        }

        moveNextMethod = null!;
        currentGetter = null!;
        return false;
    }

    private static bool TryResolveEnumeratorCurrentGetter(
        INamedTypeSymbol enumeratorType,
        out IMethodSymbol currentGetter)
    {
        IMethodSymbol? objectGetter = null;

        foreach (var property in enumeratorType.GetMembers("Current").OfType<IPropertySymbol>())
        {
            if (property.IsStatic)
                continue;

            if (property.GetMethod is not IMethodSymbol getter)
                continue;

            if (getter.IsStatic || getter.Parameters.Length != 0)
                continue;

            if (getter.ReturnType.SpecialType != SpecialType.System_Object)
            {
                currentGetter = getter;
                return true;
            }

            objectGetter ??= getter;
        }

        if (objectGetter is not null)
        {
            currentGetter = objectGetter;
            return true;
        }

        currentGetter = null!;
        return false;
    }

    private bool TryGetGenericEnumerableInterface(INamedTypeSymbol type, out INamedTypeSymbol enumerableInterface)
    {
        if (IsGenericIEnumerableType(type) &&
            type.TypeArguments.Length == 1)
        {
            enumerableInterface = type;
            return true;
        }

        foreach (var interfaceType in type.AllInterfaces.OfType<INamedTypeSymbol>())
        {
            if (IsGenericIEnumerableType(interfaceType) &&
                interfaceType.TypeArguments.Length == 1)
            {
                enumerableInterface = interfaceType;
                return true;
            }
        }

        enumerableInterface = null!;
        return false;
    }

    private bool TryGetGenericAsyncEnumerableInterface(INamedTypeSymbol type, out INamedTypeSymbol enumerableInterface)
    {
        if (IsGenericIAsyncEnumerableType(type) &&
            type.TypeArguments.Length == 1)
        {
            enumerableInterface = type;
            return true;
        }

        foreach (var interfaceType in type.AllInterfaces.OfType<INamedTypeSymbol>())
        {
            if (IsGenericIAsyncEnumerableType(interfaceType) &&
                interfaceType.TypeArguments.Length == 1)
            {
                enumerableInterface = interfaceType;
                return true;
            }
        }

        enumerableInterface = null!;
        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, INamedTypeSymbol interfaceType)
    {
        if (SymbolEqualityComparer.Default.Equals(type, interfaceType))
            return true;

        foreach (var implementedInterface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implementedInterface, interfaceType))
                return true;
        }

        return false;
    }

    private static bool IsGenericIEnumerableType(INamedTypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return true;

        var definition = type.OriginalDefinition as INamedTypeSymbol
            ?? type.ConstructedFrom as INamedTypeSymbol
            ?? type;

        return definition.MetadataName == "IEnumerable`1" &&
               IsInNamespace(definition.ContainingNamespace, "System.Collections.Generic");
    }

    private static bool IsGenericIAsyncEnumerableType(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition as INamedTypeSymbol
            ?? type.ConstructedFrom as INamedTypeSymbol
            ?? type;

        return definition.MetadataName == "IAsyncEnumerable`1" &&
               IsInNamespace(definition.ContainingNamespace, "System.Collections.Generic");
    }

    private static bool IsGenericIAsyncEnumeratorType(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition as INamedTypeSymbol
            ?? type.ConstructedFrom as INamedTypeSymbol
            ?? type;

        return definition.MetadataName == "IAsyncEnumerator`1" &&
               IsInNamespace(definition.ContainingNamespace, "System.Collections.Generic");
    }

    private static bool IsInNamespace(INamespaceSymbol? namespaceSymbol, string qualifiedNamespace)
    {
        if (namespaceSymbol is null)
            return false;

        var remaining = qualifiedNamespace;

        while (!namespaceSymbol.IsGlobalNamespace)
        {
            var dot = remaining.LastIndexOf('.');
            var segment = dot >= 0 ? remaining[(dot + 1)..] : remaining;

            if (!string.Equals(namespaceSymbol.Name, segment, StringComparison.Ordinal))
                return false;

            if (dot < 0)
                return namespaceSymbol.ContainingNamespace?.IsGlobalNamespace ?? false;

            remaining = remaining[..dot];
            if (namespaceSymbol.ContainingNamespace is not { } containingNamespace)
                return false;

            namespaceSymbol = containingNamespace;
        }

        return false;
    }

    private BoundCatchClause BindCatchClause(CatchClauseSyntax catchClause)
    {
        ITypeSymbol exceptionBase = Compilation.GetSpecialType(SpecialType.System_Exception);
        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        var exceptionType = exceptionBase;
        SourceLocalSymbol? localSymbol = null;
        BoundPattern? pattern = null;
        BoundExpression? guard = null;
        ImmutableArray<ILocalSymbol> patternLocals = ImmutableArray<ILocalSymbol>.Empty;
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?>? shadowedLocals = null;

        if (catchClause.Pattern is { } patternSyntax)
        {
            shadowedLocals = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);
            CapturePatternLocalShadows(patternSyntax, shadowedLocals);

            var previousBindingKeyword = _ambientPatternDeclarationBindingKeyword;
            _ambientPatternDeclarationBindingKeyword = SyntaxKind.None;

            BoundPattern boundPattern;
            try
            {
                boundPattern = BindPattern(patternSyntax, exceptionBase);
            }
            finally
            {
                _ambientPatternDeclarationBindingKeyword = previousBindingKeyword;
            }

            patternLocals = CollectPatternDesignatorLocals(boundPattern);

            foreach (var patternLocal in patternLocals)
                _locals[patternLocal.Name] = (patternLocal, _scopeDepth + 1);

            pattern = boundPattern;
            ExtractCatchPatternBinding(catchClause, patternSyntax, boundPattern, exceptionBase, ref exceptionType, ref localSymbol);
        }

        if (catchClause.WhenClause?.Guard is ExpressionSyntax guardSyntax)
            guard = BindExpressionWithTargetType(guardSyntax, boolType);

        var block = BindBlockStatement(catchClause.Block);

        RestorePatternLocalShadows(patternLocals, shadowedLocals);

        return new BoundCatchClause(exceptionType, localSymbol, pattern, guard, block);
    }

    private void ExtractCatchPatternBinding(
        CatchClauseSyntax catchClause,
        PatternSyntax patternSyntax,
        BoundPattern pattern,
        ITypeSymbol exceptionBase,
        ref ITypeSymbol exceptionType,
        ref SourceLocalSymbol? localSymbol)
    {
        switch (patternSyntax, pattern)
        {
            case (DeclarationPatternSyntax declarationSyntax, BoundDeclarationPattern declarationPattern):
                exceptionType = declarationPattern.DeclaredType;

                if (exceptionBase.TypeKind != TypeKind.Error &&
                    exceptionType.TypeKind != TypeKind.Error &&
                    !IsAssignable(exceptionBase, exceptionType, out _))
                {
                    _diagnostics.ReportCatchTypeMustDeriveFromSystemException(
                        exceptionType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        declarationSyntax.Type.GetLocation());
                }

                localSymbol = declarationPattern.Designator switch
                {
                    BoundSingleVariableDesignator { Local: SourceLocalSymbol sourceLocal } => sourceLocal,
                    _ => null
                };
                return;

            case (_, BoundDiscardPattern):
                exceptionType = exceptionBase;
                localSymbol = null;
                return;

            default:
                _diagnostics.ReportCatchPatternMustBeTypePattern(patternSyntax.Kind.ToString(), catchClause.GetLocation());
                exceptionType = exceptionBase;
                localSymbol = null;
                return;
        }
    }

    private void RestorePatternLocalShadows(
        ImmutableArray<ILocalSymbol> patternLocals,
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?>? shadowedLocals)
    {
        foreach (var patternLocal in patternLocals)
        {
            if (shadowedLocals is not null &&
                shadowedLocals.TryGetValue(patternLocal.Name, out var shadowed) &&
                shadowed is not null)
            {
                _locals[patternLocal.Name] = shadowed.Value;
                continue;
            }

            _locals.Remove(patternLocal.Name);
        }
    }

    private BoundStatement ExpressionToStatement(BoundExpression expression)
    {
        return expression switch
        {
            BoundIfExpression ifExpr => new BoundIfStatement(
                ifExpr.Condition,
                ExpressionToStatement(ifExpr.ThenBranch),
                ifExpr.ElseBranch is not null ? ExpressionToStatement(ifExpr.ElseBranch) : null),
            BoundBlockExpression blockExpr => new BoundBlockStatement(blockExpr.Statements, blockExpr.LocalsToDispose),
            BoundAssignmentExpression assignmentExpr => new BoundAssignmentStatement(assignmentExpr),
            _ => new BoundExpressionStatement(expression),
        };
    }

    private BoundStatement BindReturnStatement(ReturnStatementSyntax returnStatement)
    {
        if (_expressionContextDepth > 0)
            _diagnostics.ReportReturnStatementInExpression(returnStatement.ReturnKeyword.GetLocation());

        if (returnStatement.Expression is null && IsSynthesizedTopLevelEntryPointContext())
        {
            _diagnostics.ReportExpressionExpected(returnStatement.ReturnKeyword.GetLocation());
        }

        var expr = BindReturnValue(returnStatement.Expression, returnStatement);

        return new BoundReturnStatement(expr);
    }

    private BoundExpression? BindReturnValue(ExpressionSyntax? expressionSyntax, SyntaxNode returnSyntax)
    {
        MarkIteratorFromEnclosingSyntax(returnSyntax);

        BoundExpression? expr = null;

        if (expressionSyntax is not null)
        {
            if (_containingSymbol is IMethodSymbol { IsIterator: true })
            {
                expr = BindExpression(expressionSyntax, allowReturn: false);
            }
            else
            {
                var targetType = GetContainingReturnTargetType();

                // Return payloads are context-sensitive; ensure stale non-target-typed cache entries
                // do not block target-typed member bindings like `.Error(...)`.
                RemoveCachedBoundNode(expressionSyntax);
                expr = BindExpressionWithTargetType(expressionSyntax, targetType, allowReturn: false);
            }
        }

        if (_containingSymbol is IMethodSymbol method)
        {
            if (method.IsIterator)
            {
                if (expr is not null)
                    _diagnostics.ReportIteratorReturnCannotHaveExpression(expressionSyntax!.GetLocation());

                return expr;
            }

            var skipReturnConversions = method switch
            {
                SourceMethodSymbol { HasAsyncReturnTypeError: true } => true,
                SourceMethodSymbol { ShouldDeferAsyncReturnDiagnostics: true } => true,
                SourceLambdaSymbol { HasAsyncReturnTypeError: true } => true,
                SourceLambdaSymbol { ShouldDeferAsyncReturnDiagnostics: true } => true,
                _ => false,
            };

            if (!skipReturnConversions)
            {
                var methodReturnType = method.ReturnType;
                if (methodReturnType is null)
                    return expr;

                if (expr is null)
                {
                    var unit = Compilation.GetSpecialType(SpecialType.System_Unit);
                    if (!IsAssignable(methodReturnType, unit, out _))
                        ReportCannotConvertFromTypeToType(
                            unit.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            methodReturnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            returnSyntax.GetLocation());
                }
                else if (method.IsAsync &&
                    AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, methodReturnType) is
                    { SpecialType: SpecialType.System_Unit or SpecialType.System_Void })
                {
                    if (!TryConvertTaskLikeAsyncReturnExpression(method, expr, expressionSyntax!, out expr))
                    {
                        var methodDisplay = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        _diagnostics.ReportAsyncTaskReturnCannotHaveExpression(
                            methodDisplay,
                            expressionSyntax!.GetLocation());
                    }
                }
                else
                {
                    var targetType = methodReturnType;

                    if (method.IsAsync &&
                        methodReturnType.TypeKind != TypeKind.Error &&
                        AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, methodReturnType) is { } resultType)
                    {
                        targetType = resultType;
                    }

                    if (ShouldAttemptConversion(expr) && targetType.TypeKind != TypeKind.Error)
                    {
                        expr = BindLambdaToDelegateIfNeeded(expr, targetType);

                        if (!IsAssignable(targetType, expr.Type, out var conversion))
                        {
                            ReportCannotConvertExpressionToType(expr, targetType, expressionSyntax!.GetLocation());
                        }
                        else
                        {
                            expr = ApplyConversion(expr, targetType, conversion, expressionSyntax!);
                        }
                    }

                    if (ReportStructUnionReturnMayBeDefault(targetType, expr, expressionSyntax))
                    {
                        expr = new BoundErrorExpression(targetType, null, BoundExpressionReason.OtherError);
                    }
                }
            }

            if (expr is not null)
                expr = ValidateByRefReturnExpression(
                    method,
                    expr,
                    expressionSyntax as SyntaxNode ?? returnSyntax);

            return expr;
        }

        if (_containingSymbol is IPropertySymbol property)
        {
            var propertyType = property.Type;
            if (propertyType is null)
                return expr;

            if (expr is null)
            {
                var unit = Compilation.GetSpecialType(SpecialType.System_Unit);
                if (!IsAssignable(propertyType, unit, out _))
                    ReportCannotConvertFromTypeToType(
                        unit.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        propertyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        returnSyntax.GetLocation());
            }
            else if (ShouldAttemptConversion(expr) && propertyType.TypeKind != TypeKind.Error)
            {
                expr = BindLambdaToDelegateIfNeeded(expr, propertyType);

                if (!IsAssignable(propertyType, expr.Type, out var conversion))
                {
                    ReportCannotConvertExpressionToType(expr, propertyType, expressionSyntax!.GetLocation());
                }
                else
                {
                    expr = ApplyConversion(expr, propertyType, conversion, expressionSyntax!);
                }

                if (ReportStructUnionReturnMayBeDefault(propertyType, expr, expressionSyntax))
                {
                    expr = new BoundErrorExpression(propertyType, null, BoundExpressionReason.OtherError);
                }
            }
        }

        return expr;
    }

    private void MarkIteratorFromEnclosingSyntax(SyntaxNode syntax)
    {
        if (_containingSymbol is not IMethodSymbol { IsIterator: false })
            return;

        for (var current = syntax.Parent; current is not null; current = current.Parent)
        {
            SyntaxNode? body = current switch
            {
                FunctionExpressionSyntax function => function.Body ?? (SyntaxNode?)function.ExpressionBody?.Expression,
                FunctionStatementSyntax function => function.Body ?? (SyntaxNode?)function.ExpressionBody?.Expression,
                BaseMethodDeclarationSyntax method => method.Body ?? (SyntaxNode?)method.ExpressionBody?.Expression,
                AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
                _ => null,
            };

            if (body is null)
                continue;

            if (Compilation.ContainsYieldOutsideNestedFunctions(body))
                ResolveIteratorInfoForCurrentMethod();

            return;
        }
    }

    private bool TryConvertTaskLikeAsyncReturnExpression(
        IMethodSymbol method,
        BoundExpression expression,
        SyntaxNode syntax,
        out BoundExpression converted)
    {
        converted = expression;

        if (!method.IsAsync ||
            method.ReturnType is null ||
            method.ReturnType.TypeKind == TypeKind.Error ||
            expression.Type is null ||
            expression.Type.TypeKind == TypeKind.Error)
        {
            return false;
        }

        if (AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, method.ReturnType) is not
            { SpecialType: SpecialType.System_Unit or SpecialType.System_Void })
        {
            return false;
        }

        if (!IsAssignable(method.ReturnType, expression.Type, out var conversion))
            return false;

        converted = ApplyConversion(expression, method.ReturnType, conversion, syntax);
        return true;
    }

    protected BoundExpression ValidateByRefReturnExpression(
        IMethodSymbol method,
        BoundExpression expression,
        SyntaxNode syntax)
    {
        if (method.ReturnType is not RefTypeSymbol refTypeReturnType)
            return expression;

        if (!TryGetAddressOfStorage(expression, out var storage))
            return expression;

        if (storage is BoundLocalAccess localAccess)
            return ReportInvalidByRefReturnStorage(
                localAccess.Local.Name,
                refTypeReturnType,
                syntax,
                isValueParameter: false);

        if (storage is BoundParameterAccess parameterAccess && parameterAccess.Parameter.RefKind == RefKind.None)
            return ReportInvalidByRefReturnStorage(
                parameterAccess.Parameter.Name,
                refTypeReturnType,
                syntax,
                isValueParameter: true);

        return expression;
    }

    private BoundExpression ReportInvalidByRefReturnStorage(
        string sourceName,
        RefTypeSymbol targetType,
        SyntaxNode syntax,
        bool isValueParameter)
    {
        if (isValueParameter)
            _diagnostics.ReportByRefReturnCannotReferenceValueParameter(sourceName, syntax.GetLocation());
        else
            _diagnostics.ReportByRefReturnCannotReferenceLocal(sourceName, syntax.GetLocation());

        return new BoundErrorExpression(targetType, null, BoundExpressionReason.TypeMismatch);
    }

    private static bool TryGetAddressOfStorage(BoundExpression expression, out BoundExpression storage)
    {
        var current = expression;
        while (current is BoundConversionExpression conversion)
            current = conversion.Expression;

        if (current is BoundAddressOfExpression { Storage: BoundExpression storageExpression })
        {
            storage = storageExpression;
            return true;
        }

        storage = null!;
        return false;
    }

    private bool IsSynthesizedTopLevelEntryPointContext()
    {
        return _containingSymbol is SynthesizedMainMethodSymbol or SynthesizedMainAsyncMethodSymbol;
    }

    private BoundStatement BindYieldStatement(YieldStatementSyntax yieldStatement)
    {
        if (yieldStatement.Expression is BreakExpressionSyntax)
        {
            ResolveIteratorInfoForCurrentMethod();
            return new BoundReturnStatement(null);
        }

        if (yieldStatement.Expression is ReturnExpressionSyntax { Expression: { } recoveredExpression })
            return BindYieldValueExpression(recoveredExpression);

        return BindYieldValueExpression(yieldStatement.Expression, yieldStatement.FromKeyword.Kind != SyntaxKind.None);
    }

    private BoundStatement BindYieldValueExpression(ExpressionSyntax expressionSyntax, bool isDelegating = false)
    {
        if (_expressionContextDepth > 0)
        {
            var exprInExpressionContext = BindExpression(expressionSyntax);
            return new BoundExpressionStatement(exprInExpressionContext);
        }

        var expression = BindExpression(expressionSyntax);
        var (kind, elementType) = ResolveIteratorInfoForCurrentMethod();

        if (elementType.TypeKind == TypeKind.Error)
            elementType = Compilation.ErrorTypeSymbol;

        var iteration = isDelegating ? BindYieldFromIteration(expression, expressionSyntax, kind, elementType) : null;
        if (!isDelegating)
            expression = BindYieldValueConversion(expression, elementType, expressionSyntax);

        return new BoundYieldStatement(expression, elementType, kind, iteration);
    }

    private BoundStatement BindThrowStatement(ThrowStatementSyntax throwStatement)
    {
        var location = throwStatement.ThrowKeyword.GetLocation();

        if (_expressionContextDepth > 0)
            _diagnostics.ReportThrowStatementInExpression(location);

        var exceptionBase = Compilation.GetTypeByMetadataName("System.Exception")
            ?? Compilation.ErrorTypeSymbol;
        var expression = BindThrowValueExpression(BindExpression(throwStatement.Expression), throwStatement.Expression, exceptionBase);

        return new BoundThrowStatement(expression);
    }

    private BoundExpression BindThrowValueExpression(
        BoundExpression expression,
        ExpressionSyntax expressionSyntax,
        ITypeSymbol exceptionBase)
    {
        if (ShouldAttemptConversion(expression) &&
            expression.Type is { TypeKind: not TypeKind.Error } expressionType &&
            exceptionBase.TypeKind != TypeKind.Error)
        {
            if (!IsAssignable(exceptionBase, expressionType, out var conversion))
            {
                _diagnostics.ReportThrowExpressionMustBeException(
                    expressionType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    expressionSyntax.GetLocation());
            }
            else
            {
                expression = ApplyConversion(expression, exceptionBase, conversion, expressionSyntax);
            }
        }

        return expression;
    }

    private BoundStatement BindLabeledStatement(LabeledStatementSyntax labeledStatement)
    {
        if (_expressionContextDepth > 0)
            _diagnostics.ReportLabelInExpression(labeledStatement.Identifier.GetLocation());

        var labelSymbol = DeclareLabelSymbol(labeledStatement);
        var boundStatement = BindStatement(labeledStatement.Statement);
        var bound = new BoundLabeledStatement(labelSymbol, boundStatement);
        CacheBoundNode(labeledStatement, bound);
        return bound;
    }

    private BoundStatement BindGotoStatement(GotoStatementSyntax gotoStatement)
    {
        if (_expressionContextDepth > 0)
            _diagnostics.ReportGotoStatementInExpression(gotoStatement.GotoKeyword.GetLocation());

        var identifier = gotoStatement.Identifier;
        if (identifier.IsMissing)
        {
            var errorSymbol = CreateLabelSymbol(string.Empty, identifier.GetLocation(), gotoStatement.GetReference());
            var boundError = new BoundGotoStatement(errorSymbol);
            CacheBoundNode(gotoStatement, boundError);
            return boundError;
        }

        if (SyntaxFacts.IsReservedWordKind(identifier.Kind))
        {
            var identifierName = identifier.ValueText;
            _diagnostics.ReportReservedWordCannotBeLabel(identifierName, identifier.GetLocation());
            var errorSymbol = CreateLabelSymbol(string.Empty, identifier.GetLocation(), gotoStatement.GetReference());
            var boundError = new BoundGotoStatement(errorSymbol);
            CacheBoundNode(gotoStatement, boundError);
            return boundError;
        }

        var name = identifier.ValueText;
        EnsureLabelsDeclaredForLookup(gotoStatement);
        if (!TryLookupLabel(name, out var label, out var labeledSyntax))
        {
            _diagnostics.ReportLabelNotFound(name, identifier.GetLocation());
            label = CreateLabelSymbol(name, identifier.GetLocation(), gotoStatement.GetReference());
        }

        var isBackward = false;
        if (labeledSyntax is not null)
        {
            isBackward = labeledSyntax.Span.Start < gotoStatement.Span.Start;
            if (DoesGotoExitUseScope(gotoStatement, labeledSyntax))
                _diagnostics.ReportGotoCannotExitUseScope(identifier.GetLocation());
        }

        SemanticModel?.RegisterGoto(gotoStatement, label);

        var bound = new BoundGotoStatement(label, isBackward);
        CacheBoundNode(gotoStatement, bound);
        return bound;
    }

    private static bool DoesGotoExitUseScope(GotoStatementSyntax gotoStatement, LabeledStatementSyntax targetLabel)
    {
        var targetBlocks = targetLabel.AncestorsAndSelf().OfType<BlockStatementSyntax>().ToHashSet();

        foreach (var sourceBlock in gotoStatement.AncestorsAndSelf().OfType<BlockStatementSyntax>())
        {
            if (targetBlocks.Contains(sourceBlock))
                break;

            if (HasActiveUseDeclarationBeforeJump(sourceBlock, gotoStatement))
                return true;
        }

        return false;
    }

    private static bool HasActiveUseDeclarationBeforeJump(BlockStatementSyntax block, GotoStatementSyntax gotoStatement)
    {
        var containingStatement = gotoStatement.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault(statement => ReferenceEquals(statement.Parent, block));

        if (containingStatement is null)
            return false;

        foreach (var statement in block.Statements)
        {
            if (ReferenceEquals(statement, containingStatement))
                break;

            if (statement is UseDeclarationStatementSyntax)
                return true;
        }

        return false;
    }

    private BoundStatement BindBreakStatement(BreakStatementSyntax breakStatement)
    {
        var location = breakStatement.BreakKeyword.GetLocation();
        var targetLabel = BindControlFlowLabel(breakStatement.Identifier, breakStatement);

        if (_expressionContextDepth > 0)
        {
            _diagnostics.ReportBreakStatementInExpression(location);
        }
        else if (IsMissingControlFlowLabel(breakStatement.Identifier) && _loopDepth == 0)
        {
            _diagnostics.ReportBreakStatementNotWithinLoop(location);
        }

        var bound = new BoundBreakStatement(targetLabel);
        CacheBoundNode(breakStatement, bound);
        return bound;
    }

    private BoundStatement BindContinueStatement(ContinueStatementSyntax continueStatement)
    {
        var location = continueStatement.ContinueKeyword.GetLocation();
        var targetLabel = BindControlFlowLabel(continueStatement.Identifier, continueStatement);

        if (_expressionContextDepth > 0)
        {
            _diagnostics.ReportContinueStatementInExpression(location);
        }
        else if (IsMissingControlFlowLabel(continueStatement.Identifier) && _loopDepth == 0)
        {
            _diagnostics.ReportContinueStatementNotWithinLoop(location);
        }

        var bound = new BoundContinueStatement(targetLabel);
        CacheBoundNode(continueStatement, bound);
        return bound;
    }

    private ILabelSymbol? BindControlFlowLabel(SyntaxToken identifier, SyntaxNode transferNode)
    {
        if (IsMissingControlFlowLabel(identifier))
            return null;

        if (SyntaxFacts.IsReservedWordKind(identifier.Kind))
        {
            var identifierName = identifier.ValueText;
            _diagnostics.ReportReservedWordCannotBeLabel(identifierName, identifier.GetLocation());
            return null;
        }

        var name = identifier.ValueText;
        EnsureLabelsDeclaredForLookup(transferNode);
        if (!TryLookupLabel(name, out var label, out var labeledSyntax) || labeledSyntax is null)
        {
            _diagnostics.ReportLabelNotFound(name, identifier.GetLocation());
            return null;
        }

        if (!IsEnclosingLoopLabel(labeledSyntax, transferNode))
        {
            _diagnostics.ReportLabelDoesNotIdentifyEnclosingLoop(name, identifier.GetLocation());
            return null;
        }

        return label;
    }

    private static bool IsMissingControlFlowLabel(SyntaxToken identifier)
        => identifier.IsMissing || identifier.Kind == SyntaxKind.None;

    private void EnsureLabelsDeclaredForLookup(SyntaxNode node)
    {
        var scope = node.AncestorsAndSelf().OfType<BlockStatementSyntax>().LastOrDefault() as SyntaxNode
            ?? node.AncestorsAndSelf().OfType<BlockSyntax>().LastOrDefault();

        if (scope is null)
            return;

        GetOutermostBlockBinder().EnsureLabelsDeclared(scope);
    }

    private bool TryLookupLabel(
        string name,
        out ILabelSymbol label,
        out LabeledStatementSyntax? labeledSyntax)
    {
        foreach (var binder in EnumerateBlockBindersOutermostFirst())
        {
            if (binder._labelsByName.TryGetValue(name, out label) &&
                binder._syntaxByLabel.TryGetValue(label, out labeledSyntax))
            {
                return true;
            }
        }

        label = null!;
        labeledSyntax = null;
        return false;
    }

    private BlockBinder GetOutermostBlockBinder()
        => EnumerateBlockBindersOutermostFirst().FirstOrDefault() ?? this;

    private IEnumerable<BlockBinder> EnumerateBlockBindersOutermostFirst()
    {
        var binders = new Stack<BlockBinder>();
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (current is BlockBinder blockBinder)
                binders.Push(blockBinder);
        }

        while (binders.Count > 0)
            yield return binders.Pop();
    }

    private static bool IsEnclosingLoopLabel(LabeledStatementSyntax labeledSyntax, SyntaxNode transferNode)
    {
        if (!transferNode.Ancestors().OfType<LabeledStatementSyntax>().Any(ancestor =>
                ancestor.Span == labeledSyntax.Span &&
                ancestor.Identifier.ValueText == labeledSyntax.Identifier.ValueText))
        {
            return false;
        }

        var targetStatement = UnwrapLabeledStatement(labeledSyntax.Statement);
        return targetStatement is WhileStatementSyntax or WhilePatternStatementSyntax or ForStatementSyntax or LoopStatementSyntax;
    }

    private static StatementSyntax UnwrapLabeledStatement(StatementSyntax statement)
    {
        while (statement is LabeledStatementSyntax labeled)
            statement = labeled.Statement;

        return statement;
    }

    public BoundStatement BindStatementInLoop(StatementSyntax syntax)
    {
        using var _ = EnterExecutionScope();

        var previous = EnterLoop();
        try
        {
            return BindStatement(syntax);
        }
        finally
        {
            ExitLoop(previous);
        }
    }

    private int EnterLoop()
    {
        var previous = _loopDepth;
        _loopDepth++;
        return previous;
    }

    private void ExitLoop(int previous)
    {
        _loopDepth = previous;
    }

    private void EnsureLabelsDeclared(SyntaxNode node)
    {
        if (!_labelDeclarationNodes.Add(node))
            return;

        var stack = new Stack<SyntaxNode>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current is FunctionStatementSyntax)
                continue;

            if (current is LabeledStatementSyntax labeled)
            {
                _ = DeclareLabelSymbol(labeled);
                stack.Push(labeled.Statement);
                continue;
            }

            foreach (var child in current.ChildNodes())
                stack.Push(child);
        }
    }

    private ILabelSymbol DeclareLabelSymbol(LabeledStatementSyntax labeledStatement)
    {
        if (_labelsBySyntax.TryGetValue(labeledStatement, out var existing))
            return existing;

        var identifier = labeledStatement.Identifier;
        if (identifier.IsMissing)
            return CreateLabelSymbol(string.Empty, identifier.GetLocation(), labeledStatement.GetReference());

        if (SyntaxFacts.IsReservedWordKind(identifier.Kind))
        {
            var identifierName = identifier.ValueText;
            _diagnostics.ReportReservedWordCannotBeLabel(identifierName, identifier.GetLocation());
            return CreateLabelSymbol(string.Empty, identifier.GetLocation(), labeledStatement.GetReference());
        }

        var name = identifier.ValueText;

        if (_labelsByName.TryGetValue(name, out var conflict))
        {
            _diagnostics.ReportLabelAlreadyDefined(name, identifier.GetLocation());
            return conflict;
        }

        var symbol = CreateLabelSymbol(name, identifier.GetLocation(), labeledStatement.GetReference());

        _labelsByName[name] = symbol;
        _labelsBySyntax[labeledStatement] = symbol;
        _syntaxByLabel[symbol] = labeledStatement;
        SemanticModel?.RegisterLabel(labeledStatement, symbol);

        return symbol;
    }

    private INamedTypeSymbol GetGenericEnumeratorDefinition()
        => (INamedTypeSymbol)Compilation.GetSpecialType(
            SpecialType.System_Collections_Generic_IEnumerator_T);

    private INamedTypeSymbol GetNonGenericEnumeratorDefinition()
        => (INamedTypeSymbol)Compilation.GetSpecialType(
            SpecialType.System_Collections_IEnumerator);

    private (IteratorMethodKind Kind, ITypeSymbol ElementType) ResolveIteratorInfoForCurrentMethod()
    {
        if (_containingSymbol is not IMethodSymbol method)
            return (IteratorMethodKind.None, Compilation.ErrorTypeSymbol);

        var result = ResolveIteratorInfo(method);

        if (result.Kind != IteratorMethodKind.None)
        {
            switch (_containingSymbol)
            {
                case SourceMethodSymbol sourceMethod:
                    sourceMethod.MarkIterator(result.Kind, result.ElementType);
                    break;
                case SourceLambdaSymbol sourceLambda:
                    sourceLambda.MarkIterator(result.Kind, result.ElementType);
                    break;
            }
        }

        return result;
    }

    private (IteratorMethodKind Kind, ITypeSymbol ElementType) ResolveIteratorInfo(IMethodSymbol method)
    {
        var errorType = Compilation.ErrorTypeSymbol;
        var returnType = method.ReturnType;

        if (returnType.SpecialType == SpecialType.System_Collections_IEnumerable)
        {
            var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
            return (IteratorMethodKind.Enumerable, objectType);
        }

        if (returnType.SpecialType == SpecialType.System_Collections_IEnumerator)
        {
            var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
            return (IteratorMethodKind.Enumerator, objectType);
        }

        if (returnType is INamedTypeSymbol named)
        {
            var definition = named;
            if (named.ConstructedFrom is INamedTypeSymbol constructedFrom)
                definition = constructedFrom;

            if (definition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                var elementType = named.TypeArguments.Length > 0 ? named.TypeArguments[0] : errorType;
                return (IteratorMethodKind.Enumerable, elementType);
            }

            if (definition.SpecialType == SpecialType.System_Collections_Generic_IEnumerator_T)
            {
                var elementType = named.TypeArguments.Length > 0 ? named.TypeArguments[0] : errorType;
                return (IteratorMethodKind.Enumerator, elementType);
            }

            if (IsGenericIAsyncEnumerableType(definition))
            {
                var elementType = named.TypeArguments.Length > 0 ? named.TypeArguments[0] : errorType;
                return (IteratorMethodKind.AsyncEnumerable, elementType);
            }

            if (IsGenericIAsyncEnumeratorType(definition))
            {
                var elementType = named.TypeArguments.Length > 0 ? named.TypeArguments[0] : errorType;
                return (IteratorMethodKind.AsyncEnumerator, elementType);
            }
        }

        return (IteratorMethodKind.None, errorType);
    }

    private ILabelSymbol CreateLabelSymbol(string name, Location location, SyntaxReference reference)
    {
        return new LabelSymbol(
            name,
            _containingSymbol,
            _containingSymbol.ContainingType as INamedTypeSymbol,
            _containingSymbol?.ContainingNamespace,
            [location],
            [reference]);
    }

    // Helper for lambda return type target-typing (mirrors method rules).
    private static ITypeSymbol GetReturnTargetType(ILambdaSymbol lambda)
    {
        // Mirror method-return target type rules.
        var returnType = lambda.ReturnType;

        if (returnType is ErrorTypeSymbol)
            return returnType;

        if (lambda.IsAsync &&
            returnType is INamedTypeSymbol namedReturn &&
            (namedReturn.OriginalDefinition as INamedTypeSymbol
                ?? namedReturn.ConstructedFrom as INamedTypeSymbol
                ?? namedReturn).SpecialType == SpecialType.System_Threading_Tasks_Task_T &&
            namedReturn.TypeArguments.Length == 1 &&
            namedReturn.TypeArguments[0] is { } resultType)
        {
            return resultType;
        }

        return returnType;
    }
}
