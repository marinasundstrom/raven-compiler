using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

class MethodBodyBinder : BlockBinder
{
    private readonly IMethodSymbol _methodSymbol;

    public MethodBodyBinder(IMethodSymbol methodSymbol, Binder parent)
        : base(methodSymbol, parent)
    {
        _methodSymbol = methodSymbol;
    }

    public override Compilation Compilation
    {
        get
        {
            if (ParentBinder?.Compilation is { } compilation)
                return compilation;

            if (_methodSymbol.ContainingAssembly is SourceAssemblySymbol assemblySymbol)
                return assemblySymbol.Compilation;

            return base.Compilation;
        }
    }

    public override SemanticModel SemanticModel
    {
        get
        {
            if (ParentBinder?.SemanticModel is { } semanticModel)
                return semanticModel;

            if (_methodSymbol.ContainingAssembly is SourceAssemblySymbol assemblySymbol &&
                _methodSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree is { } syntaxTree)
            {
                return assemblySymbol.Compilation.GetSemanticModel(syntaxTree);
            }

            return base.SemanticModel;
        }
    }

    public override BoundBlockStatement BindBlockStatement(BlockStatementSyntax block)
    {
        var bound = base.BindBlockStatement(block);
        if (!IsMethodLikeBodyBlock(block))
            return bound;

        return FinalizeMethodBody(block, bound, () => base.BindBlockStatement(block));
    }

    private static bool IsMethodLikeBodyBlock(BlockStatementSyntax block)
    {
        return block.Parent switch
        {
            BaseMethodDeclarationSyntax => true,
            FunctionStatementSyntax => true,
            AccessorDeclarationSyntax => true,
            _ => false,
        };
    }

    protected override BoundNode BindArrowExpressionClause(ArrowExpressionClauseSyntax clause)
    {
        if (TryGetCachedBoundNode(clause) is BoundBlockStatement cached)
            return cached;

        var bound = BindArrowExpressionClauseCore(clause);
        return FinalizeMethodBody(clause, bound, () => BindArrowExpressionClauseCore(clause));
    }

    private BoundBlockStatement BindArrowExpressionClauseCore(ArrowExpressionClauseSyntax clause)
    {
        var expression = BindExpression(clause.Expression);

        if (expression is BoundBlockExpression blockExpression)
            return new BoundBlockStatement(blockExpression.Statements, blockExpression.LocalsToDispose);

        BoundStatement statement;
        if (_methodSymbol.ReturnType.SpecialType == SpecialType.System_Unit)
        {
            statement = new BoundExpressionStatement(expression);
        }
        else
        {
            var targetType = GetTrailingExpressionTargetType(_methodSymbol);
            var convertedExpression = expression;

            if (convertedExpression.Type is { } sourceType &&
                !targetType.ContainsErrorType() &&
                !sourceType.ContainsErrorType())
            {
                if (!IsAssignable(targetType, sourceType, out var conversion))
                {
                    ReportCannotConvertFromTypeToType(
                        sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        targetType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        clause.Expression.GetLocation());
                    convertedExpression = new BoundErrorExpression(targetType, null, BoundExpressionReason.TypeMismatch);
                }
                else if (!SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                {
                    convertedExpression = new BoundConversionExpression(convertedExpression, targetType, conversion);
                }
            }

            if (ReportStructUnionReturnMayBeDefault(targetType, convertedExpression, clause.Expression))
            {
                convertedExpression = new BoundErrorExpression(targetType, null, BoundExpressionReason.OtherError);
            }

            statement = new BoundReturnStatement(ValidateByRefReturnExpression(_methodSymbol, convertedExpression, clause.Expression));
        }

        return new BoundBlockStatement([statement]);
    }

    private BoundBlockStatement FinalizeMethodBody(SyntaxNode bodySyntax, BoundBlockStatement bound, Func<BoundBlockStatement> rebind)
    {
        if (_methodSymbol is SourceMethodSymbol
            {
                RequiresAsyncReturnTypeInference: true,
                AsyncReturnTypeInferenceComplete: false
            } sourceMethod)
        {
            var inferredReturnType = AsyncReturnTypeUtilities.InferAsyncReturnType(Compilation, bound);
            sourceMethod.SetReturnType(inferredReturnType);
            sourceMethod.CompleteAsyncReturnTypeInference();

            RemoveCachedBoundNode(bodySyntax);
            bound = rebind();
        }

        ReportMissingReturnIfNeeded(bodySyntax, bound);
        ReportUnassignedOutParametersIfNeeded(bodySyntax, bound);

        var unit = Compilation.UnitTypeSymbol;
        var skipTrailingExpressionCheck = _methodSymbol.IsIterator || ShouldSkipTrailingExpressionCheck(unit);

        if (!skipTrailingExpressionCheck &&
            !SymbolEqualityComparer.Default.Equals(GetTrailingExpressionTargetType(_methodSymbol), unit))
        {
            if (bound.Statements.LastOrDefault() is BoundExpressionStatement exprStmt &&
                exprStmt.Expression.Type is ITypeSymbol t &&
                !_methodSymbol.ReturnType.ContainsErrorType() &&
                !t.ContainsErrorType() &&
                !IsAssignable(GetTrailingExpressionTargetType(_methodSymbol), t, out _))
            {
                var exprLocation = bodySyntax switch
                {
                    BlockStatementSyntax block when block.Statements.LastOrDefault() is ExpressionStatementSyntax lastExpr
                        => lastExpr.Expression.GetLocation(),
                    ArrowExpressionClauseSyntax arrow => arrow.Expression.GetLocation(),
                    _ => bodySyntax.GetLocation()
                };
                ReportCannotConvertFromTypeToType(
                    t.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    GetTrailingExpressionTargetType(_methodSymbol).ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    exprLocation);
            }
        }

        if (_methodSymbol is SourceMethodSymbol { IsAsync: true } asyncMethod)
            AnalyzeAsyncBody(bodySyntax, asyncMethod, bound);

        if (_methodSymbol is SourceMethodSymbol { IsIterator: true } iteratorMethod)
            RefSafetyDiagnosticReporter.ReportIteratorStorage(bound, iteratorMethod, _diagnostics);

        RefSafetyDiagnosticReporter.Report(
            bound,
            bodySyntax.GetLocation(),
            expressionResultEscapes: GetTrailingExpressionTargetType(_methodSymbol).SpecialType != SpecialType.System_Unit,
            _diagnostics);

        CacheBoundNode(bodySyntax, bound);
        return bound;
    }

    private void ReportMissingReturnIfNeeded(SyntaxNode bodySyntax, BoundBlockStatement bound)
    {
        if (bodySyntax is not BlockStatementSyntax blockSyntax)
            return;

        if (_methodSymbol.IsIterator)
            return;

        var requiredReturnType = _methodSymbol.IsAsync
            ? AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, _methodSymbol.ReturnType) ?? GetTrailingExpressionTargetType(_methodSymbol)
            : GetTrailingExpressionTargetType(_methodSymbol);

        if (requiredReturnType.ContainsErrorType() ||
            requiredReturnType.SpecialType is SpecialType.System_Unit or SpecialType.System_Void)
        {
            return;
        }

        // Raven permits implicit trailing-expression returns for non-unit methods.
        // If the final bound statement is an expression statement, trailing-expression
        // validation will handle type compatibility diagnostics.
        if (bound.Statements.LastOrDefault() is BoundExpressionStatement)
            return;

        // Lowering supports implicit returns from trailing if/else branches.
        // Avoid reporting a missing return when that rewrite would succeed.
        var unitType = Compilation.UnitTypeSymbol;
        if (!ReferenceEquals(ImplicitReturnRewriter.RewriteIfNeeded(requiredReturnType, unitType, bound), bound))
            return;

        var controlFlow = SemanticModel.AnalyzeControlFlowInternal(
            new ControlFlowRegion(blockSyntax),
            blockSyntax,
            analyzeJumpPoints: false);

        if (controlFlow is { Succeeded: true, EndPointIsReachable: true })
            _diagnostics.ReportNotAllCodePathsReturnAValue(GetMissingReturnDiagnosticLocation(blockSyntax));
    }

    private static Location GetMissingReturnDiagnosticLocation(BlockStatementSyntax blockSyntax)
    {
        return blockSyntax.Parent switch
        {
            FunctionStatementSyntax function => function.Identifier.GetLocation(),
            MethodDeclarationSyntax method => method.Identifier.GetLocation(),
            OperatorDeclarationSyntax @operator => @operator.OperatorToken.GetLocation(),
            ConversionOperatorDeclarationSyntax conversion => conversion.ConversionKindKeyword.GetLocation(),
            _ => blockSyntax.GetLocation()
        };
    }

    private void AnalyzeAsyncBody(SyntaxNode bodySyntax, SourceMethodSymbol asyncMethod, BoundBlockStatement bound)
    {
        var containsAwait = AsyncLowerer.ContainsAwait(bound) ||
            Compilation.ContainsAwaitExpressionOutsideNestedFunctions(bodySyntax);
        asyncMethod.SetContainsAwait(containsAwait);

        if (containsAwait)
        {
            RefSafetyDiagnosticReporter.ReportLocalsAcrossAwait(bound, _diagnostics);
            RefSafetyDiagnosticReporter.ReportParametersAcrossAwait(asyncMethod, _diagnostics);
        }

        if (!containsAwait)
        {
            var memberDescription = AsyncDiagnosticUtilities.GetAsyncMemberDescription(asyncMethod);
            var location = AsyncDiagnosticUtilities.GetAsyncKeywordLocation(asyncMethod, bodySyntax);

            _diagnostics.ReportAsyncLacksAwait(memberDescription, location);
        }

        ReportAsyncIteratorCancellationDiagnostics(asyncMethod);
    }

    private bool ShouldSkipTrailingExpressionCheck(ITypeSymbol unitType)
    {
        if (_methodSymbol is SourceMethodSymbol { ShouldDeferAsyncReturnDiagnostics: true })
            return true;

        if (!_methodSymbol.IsAsync)
            return false;

        var returnType = _methodSymbol.ReturnType;

        if (returnType is ErrorTypeSymbol)
            return false;

        if (AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, returnType) is { } resultType)
        {
            if (resultType is not null && SymbolEqualityComparer.Default.Equals(resultType, unitType))
                return true;
        }

        return false;
    }

    private void ReportUnassignedOutParametersIfNeeded(SyntaxNode bodySyntax, BoundBlockStatement bound)
    {
        var outParameters = _methodSymbol.Parameters
            .Where(static parameter => parameter.RefKind == RefKind.Out)
            .ToImmutableArray();

        if (outParameters.IsDefaultOrEmpty)
            return;

        var analyzer = new OutParameterAssignmentAnalyzer(this, outParameters);
        analyzer.Analyze(bodySyntax, bound);
    }

    private Location GetOutParameterDiagnosticLocation(SyntaxNode bodySyntax)
    {
        return bodySyntax switch
        {
            BlockStatementSyntax block => GetMissingReturnDiagnosticLocation(block),
            ArrowExpressionClauseSyntax arrow => arrow.GetLocation(),
            _ => _methodSymbol.Locations.FirstOrDefault() ?? Location.None
        };
    }

    private void ReportAsyncIteratorCancellationDiagnostics(SourceMethodSymbol asyncMethod)
    {
        if (asyncMethod.IteratorKind != IteratorMethodKind.AsyncEnumerable)
            return;

        var attributedParameters = AsyncIteratorCancellationUtilities.GetEnumeratorCancellationParameters(Compilation, asyncMethod);
        var memberName = asyncMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (attributedParameters.Length > 1)
        {
            _diagnostics.ReportMultipleEnumeratorCancellationParameters(
                memberName,
                attributedParameters[1].Locations.FirstOrDefault() ?? asyncMethod.Locations.FirstOrDefault() ?? Location.None);
            return;
        }

        if (!AsyncIteratorCancellationUtilities.ShouldWarnAboutMissingEnumeratorCancellation(
                Compilation,
                asyncMethod,
                asyncMethod.IteratorKind))
        {
            return;
        }

        var firstCancellationToken = AsyncIteratorCancellationUtilities
            .GetCancellationTokenParameters(Compilation, asyncMethod)
            .FirstOrDefault();
        if (firstCancellationToken is null)
            return;

        _diagnostics.ReportEnumeratorCancellationAttributeMissing(
            memberName,
            firstCancellationToken.Name,
            firstCancellationToken.Locations.FirstOrDefault() ?? asyncMethod.Locations.FirstOrDefault() ?? Location.None);
    }

    private sealed class OutParameterAssignmentAnalyzer
    {
        private readonly MethodBodyBinder _binder;
        private readonly ImmutableArray<IParameterSymbol> _outParameters;
        private readonly Stack<List<ImmutableHashSet<IParameterSymbol>>> _breakExitStates = new();

        public OutParameterAssignmentAnalyzer(MethodBodyBinder binder, ImmutableArray<IParameterSymbol> outParameters)
        {
            _binder = binder;
            _outParameters = outParameters;
        }

        public void Analyze(SyntaxNode bodySyntax, BoundBlockStatement body)
        {
            var state = AnalyzeBlock(body, ImmutableHashSet<IParameterSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default));
            if (state.CompletesNormally)
                ReportMissing(state.Assigned, _binder.GetOutParameterDiagnosticLocation(bodySyntax));
        }

        private AnalysisState AnalyzeBlock(BoundBlockStatement block, ImmutableHashSet<IParameterSymbol> assigned)
        {
            var currentAssigned = assigned;
            var completesNormally = true;

            foreach (var statement in block.Statements)
            {
                if (!completesNormally)
                    break;

                var state = AnalyzeStatement(statement, currentAssigned);
                currentAssigned = state.Assigned;
                completesNormally = state.CompletesNormally;
            }

            return new AnalysisState(currentAssigned, completesNormally);
        }

        private AnalysisState AnalyzeStatement(BoundStatement statement, ImmutableHashSet<IParameterSymbol> assigned)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    return AnalyzeBlock(block, assigned);
                case BoundExpressionStatement expressionStatement:
                    return AnalyzeExpression(expressionStatement.Expression, assigned);
                case BoundAssignmentStatement assignmentStatement:
                    return new AnalysisState(MarkAssignedExpression(assignmentStatement.Expression, assigned), true);
                case BoundReturnStatement:
                    ReportMissing(assigned, _binder._methodSymbol.Locations.FirstOrDefault() ?? Location.None);
                    return new AnalysisState(assigned, false);
                case BoundThrowStatement:
                case BoundBreakStatement:
                    if (_breakExitStates.TryPeek(out var breakExitStates))
                        breakExitStates.Add(assigned);

                    return new AnalysisState(assigned, false);
                case BoundContinueStatement:
                    return new AnalysisState(assigned, false);
                case BoundIfStatement ifStatement:
                    return AnalyzeIf(ifStatement, assigned);
                case BoundWhileStatement whileStatement:
                    _ = AnalyzeStatement(whileStatement.Body, assigned);
                    return new AnalysisState(assigned, CanCompleteNormally(whileStatement));
                case BoundLoopStatement loopStatement:
                    return AnalyzeLoop(loopStatement, assigned);
                case BoundForStatement forStatement:
                    _ = AnalyzeStatement(forStatement.Body, assigned);
                    return new AnalysisState(assigned, true);
                case BoundMatchStatement matchStatement:
                    return AnalyzeMatch(matchStatement, assigned);
                case BoundLockStatement lockStatement:
                    return AnalyzeStatement(
                        lockStatement.Body,
                        MarkAssignedExpression(lockStatement.Expression, assigned));
                case BoundTryStatement tryStatement:
                    return AnalyzeTry(tryStatement, assigned);
                default:
                    return new AnalysisState(assigned, true);
            }
        }

        private AnalysisState AnalyzeLoop(
            BoundLoopStatement loopStatement,
            ImmutableHashSet<IParameterSymbol> assigned)
        {
            var breakExitStates = new List<ImmutableHashSet<IParameterSymbol>>();
            _breakExitStates.Push(breakExitStates);
            try
            {
                _ = AnalyzeStatement(loopStatement.Body, assigned);
            }
            finally
            {
                _breakExitStates.Pop();
            }

            return breakExitStates.Count == 0
                ? new AnalysisState(assigned, false)
                : new AnalysisState(breakExitStates.Aggregate(Intersect), true);
        }

        private AnalysisState AnalyzeIf(BoundIfStatement ifStatement, ImmutableHashSet<IParameterSymbol> assigned)
        {
            var thenState = AnalyzeStatement(ifStatement.ThenNode, assigned);
            var elseState = ifStatement.ElseNode is null
                ? new AnalysisState(assigned, true)
                : AnalyzeStatement(ifStatement.ElseNode, assigned);

            return (thenState.CompletesNormally, elseState.CompletesNormally) switch
            {
                (true, true) => new AnalysisState(Intersect(thenState.Assigned, elseState.Assigned), true),
                (true, false) => thenState,
                (false, true) => elseState,
                _ => new AnalysisState(assigned, false),
            };
        }

        private AnalysisState AnalyzeTry(BoundTryStatement tryStatement, ImmutableHashSet<IParameterSymbol> assigned)
        {
            var completingStates = new List<ImmutableHashSet<IParameterSymbol>>();

            var tryState = AnalyzeBlock(tryStatement.TryBlock, assigned);
            if (tryState.CompletesNormally)
                completingStates.Add(tryState.Assigned);

            foreach (var catchClause in tryStatement.CatchClauses)
            {
                var catchState = AnalyzeBlock(catchClause.Block, assigned);
                if (catchState.CompletesNormally)
                    completingStates.Add(catchState.Assigned);
            }

            var completesNormally = completingStates.Count > 0;
            var afterTry = completesNormally
                ? completingStates.Aggregate(Intersect)
                : assigned;

            if (tryStatement.FinallyBlock is null)
                return new AnalysisState(afterTry, completesNormally);

            var finallyState = AnalyzeBlock(tryStatement.FinallyBlock, afterTry);
            return new AnalysisState(finallyState.Assigned, completesNormally && finallyState.CompletesNormally);
        }

        private AnalysisState AnalyzeMatch(
            BoundMatchStatement matchStatement,
            ImmutableHashSet<IParameterSymbol> assigned)
        {
            var beforeArms = MarkAssignedExpression(matchStatement.Expression, assigned);
            var completingStates = new List<ImmutableHashSet<IParameterSymbol>>();

            foreach (var arm in matchStatement.Arms)
            {
                var armState = AnalyzeExpression(arm.Expression, beforeArms);
                if (armState.CompletesNormally)
                    completingStates.Add(armState.Assigned);
            }

            if (!IsExhaustive(matchStatement))
                completingStates.Add(beforeArms);

            return completingStates.Count == 0
                ? new AnalysisState(beforeArms, false)
                : new AnalysisState(completingStates.Aggregate(Intersect), true);
        }

        private AnalysisState AnalyzeExpression(
            BoundExpression expression,
            ImmutableHashSet<IParameterSymbol> assigned)
        {
            switch (expression)
            {
                case BoundBlockExpression block:
                    return AnalyzeStatements(block.Statements, assigned);
                case BoundReturnExpression:
                    ReportMissing(assigned, _binder._methodSymbol.Locations.FirstOrDefault() ?? Location.None);
                    return new AnalysisState(assigned, false);
                case BoundThrowExpression:
                    return new AnalysisState(assigned, false);
                case BoundBreakExpression:
                    if (_breakExitStates.TryPeek(out var breakExitStates))
                        breakExitStates.Add(assigned);

                    return new AnalysisState(assigned, false);
                case BoundContinueExpression:
                    return new AnalysisState(assigned, false);
                case BoundYieldExpression yieldExpression:
                    return new AnalysisState(MarkAssignedExpression(yieldExpression.Expression, assigned), true);
                case BoundRequiredResultExpression required:
                    return AnalyzeExpression(required.Operand, assigned);
                default:
                    return new AnalysisState(MarkAssignedExpression(expression, assigned), true);
            }
        }

        private AnalysisState AnalyzeStatements(
            IEnumerable<BoundStatement> statements,
            ImmutableHashSet<IParameterSymbol> assigned)
        {
            var current = new AnalysisState(assigned, true);
            foreach (var statement in statements)
            {
                if (!current.CompletesNormally)
                    break;

                current = AnalyzeStatement(statement, current.Assigned);
            }

            return current;
        }

        private bool IsExhaustive(BoundMatchStatement matchStatement)
        {
            var semanticModel = _binder.SemanticModel;
            if (semanticModel?.GetSyntax(matchStatement) is not MatchStatementSyntax syntax)
                return false;

            var evaluator = new MatchExhaustivenessEvaluator(
                _binder.Compilation,
                semanticModel.TryGetCachedBoundNode);
            return evaluator.Evaluate(syntax, matchStatement, default).IsExhaustive;
        }

        private bool CanCompleteNormally(BoundStatement statement)
        {
            var semanticModel = _binder.SemanticModel;
            if (semanticModel?.GetSyntax(statement) is not StatementSyntax syntax)
                return true;

            var flow = semanticModel.AnalyzeControlFlowInternal(
                new ControlFlowRegion(syntax),
                syntax,
                analyzeJumpPoints: false);
            return flow.EndPointIsReachable;
        }

        private ImmutableHashSet<IParameterSymbol> MarkAssignedExpression(BoundExpression? expression, ImmutableHashSet<IParameterSymbol> assigned)
        {
            if (expression is null)
                return assigned;

            if (expression is BoundByRefAssignmentExpression { Reference: BoundParameterAccess parameterAccess })
                return MarkAssignedParameter(parameterAccess.Parameter, assigned);

            if (expression is BoundParameterAssignmentExpression parameterAssignment)
                return MarkAssignedParameter(parameterAssignment.Parameter, assigned);

            var collector = new AssignedOutParameterCollector(_outParameters, assigned);
            collector.VisitExpression(expression);
            return collector.Assigned;
        }

        private ImmutableHashSet<IParameterSymbol> MarkAssignedParameter(IParameterSymbol parameter, ImmutableHashSet<IParameterSymbol> assigned)
        {
            foreach (var outParameter in _outParameters)
            {
                if (SymbolEqualityComparer.Default.Equals(outParameter, parameter))
                    return assigned.Add(outParameter);
            }

            return assigned;
        }

        private void ReportMissing(ImmutableHashSet<IParameterSymbol> assigned, Location location)
        {
            foreach (var parameter in _outParameters)
            {
                if (!assigned.Contains(parameter))
                    _binder._diagnostics.ReportUnassignedOutParameter(parameter.Name, location);
            }
        }

        private static ImmutableHashSet<IParameterSymbol> Intersect(
            ImmutableHashSet<IParameterSymbol> left,
            ImmutableHashSet<IParameterSymbol> right)
        {
            var builder = ImmutableHashSet.CreateBuilder<IParameterSymbol>(SymbolEqualityComparer.Default);
            foreach (var parameter in left)
            {
                if (right.Contains(parameter))
                    builder.Add(parameter);
            }

            return builder.ToImmutable();
        }

        private readonly record struct AnalysisState(ImmutableHashSet<IParameterSymbol> Assigned, bool CompletesNormally);
    }

    private sealed class AssignedOutParameterCollector : BoundTreeWalker
    {
        private readonly ImmutableArray<IParameterSymbol> _outParameters;

        public AssignedOutParameterCollector(
            ImmutableArray<IParameterSymbol> outParameters,
            ImmutableHashSet<IParameterSymbol> assigned)
        {
            _outParameters = outParameters;
            Assigned = assigned;
        }

        public ImmutableHashSet<IParameterSymbol> Assigned { get; private set; }

        public override void VisitParameterAssignmentExpression(BoundParameterAssignmentExpression node)
        {
            base.VisitParameterAssignmentExpression(node);
            Mark(node.Parameter);
        }

        public override void VisitByRefAssignmentExpression(BoundByRefAssignmentExpression node)
        {
            base.VisitByRefAssignmentExpression(node);
            if (node.Reference is BoundParameterAccess parameterAccess)
                Mark(parameterAccess.Parameter);
        }

        public override void VisitFunctionExpression(BoundFunctionExpression node)
        {
        }

        private void Mark(IParameterSymbol parameter)
        {
            foreach (var outParameter in _outParameters)
            {
                if (SymbolEqualityComparer.Default.Equals(outParameter, parameter))
                {
                    Assigned = Assigned.Add(outParameter);
                    break;
                }
            }
        }
    }

    private ITypeSymbol GetTrailingExpressionTargetType(IMethodSymbol method)
    {
        if (method is SourceMethodSymbol { HasAsyncReturnTypeError: true } or SourceLambdaSymbol { HasAsyncReturnTypeError: true })
            return method.ReturnType;

        var returnType = method.ReturnType;
        if (returnType is ErrorTypeSymbol)
            return returnType;

        if (method.IsAsync &&
            AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation: Compilation, asyncReturnType: returnType) is { } resultType &&
            resultType.SpecialType is not SpecialType.System_Unit and not SpecialType.System_Void)
        {
            return resultType;
        }

        return returnType;
    }

    public override BoundNode GetOrBind(SyntaxNode node)
    {
        lock (_executionGate)
        {
            return GetOrBindCore(node);
        }
    }

    private BoundNode GetOrBindCore(SyntaxNode node)
    {
        if (TryGetCachedBoundNode(node) is BoundNode cached)
            return cached;

        if (node is BlockStatementSyntax blockStmt)
            return BindBlockStatement(blockStmt);
        if (node is BlockSyntax block)
            return BindBlock(block, allowReturn: true, isExpressionContext: false);
        if (node is ArrowExpressionClauseSyntax arrow)
            return BindArrowExpressionClause(arrow);

        return base.GetOrBind(node);
    }
}
