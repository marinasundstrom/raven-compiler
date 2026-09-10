using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

internal static class AsyncLowerer
{
    public static AsyncMethodAnalysis Analyze(SourceLambdaSymbol lambda, BoundBlockStatement body)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        if (!lambda.IsAsync)
            return new AsyncMethodAnalysis(requiresStateMachine: false, containsAwait: false);

        var containsAwait = ContainsAwait(body);

        lambda.SetContainsAwait(containsAwait);

        var compilation = GetCompilation(lambda);
        var requiresStateMachine = containsAwait && !compilation.IsRuntimeAsyncEnabled;
        return new AsyncMethodAnalysis(requiresStateMachine, containsAwait);
    }

    public static AsyncMethodAnalysis Analyze(SourceMethodSymbol method, BoundBlockStatement body)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        if (!method.IsAsync)
            return new AsyncMethodAnalysis(requiresStateMachine: false, containsAwait: false);

        var containsAwait = method.ContainsAwait || ContainsAwait(body) || HasAwaitInDeclaringSyntax(method);

        method.SetContainsAwait(containsAwait);

        var compilation = GetCompilation(method);
        var requiresStateMachine = containsAwait && !compilation.IsRuntimeAsyncEnabled;
        return new AsyncMethodAnalysis(requiresStateMachine, containsAwait);
    }

    private static bool HasAwaitInDeclaringSyntax(SourceMethodSymbol method)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            if (syntax is null)
                continue;

            switch (syntax)
            {
                case MethodDeclarationSyntax { Body: not null } methodDeclaration:
                    if (Compilation.ContainsAwaitExpressionOutsideNestedFunctions(methodDeclaration.Body))
                        return true;
                    break;
                case MethodDeclarationSyntax { ExpressionBody: not null } methodDeclaration:
                    if (Compilation.ContainsAwaitExpressionOutsideNestedFunctions(methodDeclaration.ExpressionBody.Expression))
                        return true;
                    break;
                case FunctionStatementSyntax { Body: not null } functionStatement:
                    if (Compilation.ContainsAwaitExpressionOutsideNestedFunctions(functionStatement.Body))
                        return true;
                    break;
                case FunctionStatementSyntax { ExpressionBody: not null } functionStatement:
                    if (Compilation.ContainsAwaitExpressionOutsideNestedFunctions(functionStatement.ExpressionBody.Expression))
                        return true;
                    break;
            }
        }

        return false;
    }

    public static bool ContainsAwait(BoundNode node)
    {
        if (node is null)
            throw new ArgumentNullException(nameof(node));

        var finder = new AwaitExpressionFinder();

        switch (node)
        {
            case BoundBlockStatement block:
                finder.VisitBlockStatement(block);
                break;
            case BoundExpression expression:
                finder.VisitExpression(expression);
                break;
            default:
                finder.Visit(node);
                break;
        }

        return finder.FoundAwait;
    }

    internal static ImmutableArray<ILocalSymbol> GetLocalsCapturedAcrossAwait(BoundNode body)
        => AwaitCaptureWalker.Analyze(body).Keys.ToImmutableArray();

    private static ITypeSymbol? FindSelfType(BoundNode node)
    {
        var finder = new SelfTypeFinder();
        finder.Visit(node);
        return finder.SelfType;
    }

    public static BoundExpression RewriteAwaitlessLambdaBody(SourceLambdaSymbol lambda, BoundExpression body)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        // This rewrite is only valid for truly awaitless async lambdas.
        // If await is present, runtime-async expects value returns directly.
        if (ContainsAwait(body))
            return body;

        var compilation = GetCompilation(lambda);

        if (!TryGetAsyncReturnInfo(compilation, lambda.ReturnType, out var returnInfo))
            return body;

        var rewriter = new AwaitlessAsyncRewriter(compilation, returnInfo);
        return rewriter.RewriteLambdaBody(body);
    }

    public static BoundBlockStatement Rewrite(SourceMethodSymbol method, BoundBlockStatement body)
    {
        return RewriteMethod(method, body).Body;
    }

    public static AsyncRewriteResult RewriteMethod(SourceMethodSymbol method, BoundBlockStatement body)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        body = AwaitForLowerer.Rewrite(method, body);

        var analysis = Analyze(method, body);
        var compilation = GetCompilation(method);
        if (analysis.ContainsAwait)
        {
            body = LowerBeforeAsyncRewrite(method, body);
            body = AsyncProtectedRegionLowerer.Rewrite(method, body);
            if (compilation.IsRuntimeAsyncEnabled)
                body = RuntimeAsyncLowerer.Rewrite(method, body);
        }

        return RewriteMethod(compilation, method, body, analysis);
    }

    public static AsyncRewriteResult Rewrite(
        SourceLambdaSymbol lambda,
        BoundBlockStatement body,
        SynthesizedAsyncStateMachineTypeSymbol? stateMachine = null,
        ITypeSymbol? selfType = null)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        body = AwaitForLowerer.Rewrite(lambda, body);

        var analysis = Analyze(lambda, body);
        var compilation = GetCompilation(lambda);
        if (analysis.ContainsAwait)
        {
            body = LowerBeforeAsyncRewrite(lambda, body);
            body = AsyncProtectedRegionLowerer.Rewrite(lambda, body);
            if (compilation.IsRuntimeAsyncEnabled)
                body = RuntimeAsyncLowerer.Rewrite(lambda, body);
        }

        if (!analysis.ContainsAwait && !compilation.IsRuntimeAsyncEnabled)
            body = RewriteAwaitlessAsyncBody(compilation, lambda.ReturnType, body);

        if (!analysis.RequiresStateMachine)
            return new AsyncRewriteResult(body, stateMachine, analysis);

        var closureSelfType = selfType;

        if (lambda.HasCaptures)
        {
            closureSelfType ??= lambda.CapturedVariables
                .OfType<IFieldSymbol>()
                .Select(field => field.ContainingType)
                .FirstOrDefault(type => type is not null);

            closureSelfType ??= lambda.CapturedVariables
                .Select(captured => captured.ContainingType)
                .FirstOrDefault(type => type is not null);

            closureSelfType ??= FindSelfType(body);
        }

        closureSelfType ??= FindCapturedClosureType(body);

        stateMachine ??= lambda.AsyncStateMachine;
        if (stateMachine is null)
        {
            stateMachine = compilation.CreateAsyncStateMachine(lambda, closureSelfType);
            lambda.SetAsyncStateMachine(stateMachine);
        }

        if (stateMachine.OriginalBody is null)
            stateMachine.SetOriginalBody(body);

        if (stateMachine.MoveNextBody is null)
        {
            var moveNextBody = CreateMoveNextBody(compilation, stateMachine);
            stateMachine.SetMoveNextBody(moveNextBody);
        }

        if (stateMachine.SetStateMachineBody is null)
        {
            var setStateMachineBody = CreateSetStateMachineBody(stateMachine);
            if (setStateMachineBody is not null)
                stateMachine.SetSetStateMachineBody(setStateMachineBody);
        }

        var asyncMethod = stateMachine.AsyncMethod;
        var rewrittenBody = RewriteAsyncBody(compilation, asyncMethod, stateMachine);
        return new AsyncRewriteResult(rewrittenBody, stateMachine, analysis);
    }

    private static BoundBlockStatement LowerBeforeAsyncRewrite(ISymbol symbol, BoundBlockStatement body)
    {
        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        var compilation = GetCompilation(symbol);
        if (!compilation.IsRuntimeAsyncEnabled)
        {
            var stateMachineHasUsingDeclaration = ContainsUsingDeclaration(body);
            var preStateMachineMatchLowerer = new AsyncMatchLowerer(symbol);
            var preStateMachineMatchLowered = preStateMachineMatchLowerer.Rewrite(body);

            // For state-machine async we must still preserve implicit returns (last expression in a block)
            // without lowering `use` declarations into try/finally (which would dispose too early on suspension).
            var withImplicitReturn = RewriteImplicitReturnIfNeeded(symbol, preStateMachineMatchLowered);
            return stateMachineHasUsingDeclaration
                ? withImplicitReturn
                : Lowerer.LowerBlock(symbol, withImplicitReturn);
        }

        var hadUsingDeclaration = ContainsUsingDeclaration(body);
        var normalizedUseDeclarations = hadUsingDeclaration
            ? new AsyncUseDeclarationLowerer(compilation).Rewrite(body)
            : body;

        // Normalize using declarations into try/finally before await/state-machine
        // rewriting so dispatch guards are computed against final protected regions.
        // Match constructs must be lowered before async state-machine rewriting so
        // MoveNext bodies don't carry BoundMatch* nodes into codegen.
        var matchLowerer = new AsyncMatchLowerer(symbol);
        var withMatchLowered = matchLowerer.Rewrite(normalizedUseDeclarations);

        // Lower propagate and other general block constructs after the async pre-normalization
        // steps so introduced locals/flow align with the final pre-state-machine shape.
        if (hadUsingDeclaration)
            return withMatchLowered;

        return Lowerer.LowerBlock(symbol, withMatchLowered);
    }

    private static BoundBlockStatement RewriteImplicitReturnIfNeeded(ISymbol symbol, BoundBlockStatement body)
    {
        ITypeSymbol? returnType = symbol switch
        {
            IMethodSymbol m => m.ReturnType,
            _ => null
        };

        if (returnType is null)
            return body;

        var compilation = GetCompilation(symbol);
        var unitType = compilation.GetSpecialType(SpecialType.System_Unit);

        return ImplicitReturnRewriter.RewriteIfNeeded(returnType, unitType, body);
    }

    private static bool ContainsUsingDeclaration(BoundBlockStatement block)
    {
        if (block is null)
            return false;

        static bool Contains(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundLocalDeclarationStatement localDeclaration when localDeclaration.IsUsing:
                    return true;
                case BoundBlockStatement nestedBlock:
                    return nestedBlock.Statements.Any(Contains);
                case BoundTryStatement tryStatement:
                    if (tryStatement.TryBlock.Statements.Any(Contains))
                        return true;
                    if (tryStatement.CatchClauses.Any(catchClause => catchClause.Block.Statements.Any(Contains)))
                        return true;
                    return tryStatement.FinallyBlock?.Statements.Any(Contains) == true;
                case BoundIfStatement ifStatement:
                    if (Contains(ifStatement.ThenNode))
                        return true;
                    return ifStatement.ElseNode is not null && Contains(ifStatement.ElseNode);
                case BoundLabeledStatement labeledStatement:
                    return Contains(labeledStatement.Statement);
                default:
                    return false;
            }
        }

        return block.Statements.Any(Contains);
    }

    private static BoundInvocationExpression NormalizeInvocationForLowering(
        IMethodSymbol method,
        BoundExpression[] arguments,
        BoundExpression? receiver,
        BoundExpression? extensionReceiver,
        bool requiresReceiverAddress)
    {
        if (!method.IsExtensionMethod)
        {
            return new BoundInvocationExpression(
                method,
                arguments,
                receiver,
                extensionReceiver,
                requiresReceiverAddress);
        }

        var normalizedReceiver = extensionReceiver ?? receiver;
        var staticQualifiedExtensionCall =
            extensionReceiver is null &&
            receiver is BoundTypeExpression &&
            arguments.Length == method.Parameters.Length &&
            method.Parameters.Length > 0;

        if (staticQualifiedExtensionCall)
        {
            return new BoundInvocationExpression(
                method,
                arguments,
                receiver: null,
                extensionReceiver: null,
                requiresReceiverAddress: requiresReceiverAddress);
        }

        if (normalizedReceiver is null)
        {
            return new BoundInvocationExpression(
                method,
                arguments,
                receiver,
                extensionReceiver,
                requiresReceiverAddress);
        }

        if (arguments.Length > 0 && ReferenceEquals(arguments[0], normalizedReceiver))
        {
            return new BoundInvocationExpression(
                method,
                arguments,
                receiver: null,
                extensionReceiver: null,
                requiresReceiverAddress: requiresReceiverAddress);
        }

        var normalizedArguments = new BoundExpression[arguments.Length + 1];
        normalizedArguments[0] = normalizedReceiver;
        Array.Copy(arguments, 0, normalizedArguments, 1, arguments.Length);

        return new BoundInvocationExpression(
            method,
            normalizedArguments,
            receiver: null,
            extensionReceiver: null,
            requiresReceiverAddress: requiresReceiverAddress);
    }

    private sealed class AsyncMatchLowerer : BoundTreeRewriter
    {
        private readonly ISymbol _containingSymbol;
        private int _rewriteOrdinal;

        public AsyncMatchLowerer(ISymbol containingSymbol)
        {
            _containingSymbol = containingSymbol ?? throw new ArgumentNullException(nameof(containingSymbol));
        }

        public BoundBlockStatement Rewrite(BoundBlockStatement body)
        {
            return (BoundBlockStatement)VisitStatement(body)!;
        }

        public override BoundNode? VisitMatchStatement(BoundMatchStatement node)
        {
            var rewritten = (BoundMatchStatement?)base.VisitMatchStatement(node) ?? node;
            var lowered = Lowerer.LowerStatement(_containingSymbol, rewritten);
            return RewriteLoweredLabels(lowered);
        }

        public override BoundNode? VisitMatchExpression(BoundMatchExpression node)
        {
            var rewritten = (BoundMatchExpression?)base.VisitMatchExpression(node) ?? node;
            var lowered = Lowerer.LowerExpression(_containingSymbol, rewritten);
            return (BoundExpression)RewriteLoweredLabels(lowered);
        }

        private BoundNode RewriteLoweredLabels(BoundNode lowered)
        {
            var rewriter = new LoweredLabelUniquifier(_rewriteOrdinal++);
            return rewriter.Visit(lowered)!;
        }

        private sealed class LoweredLabelUniquifier : BoundTreeRewriter
        {
            private readonly int _ordinal;
            private readonly Dictionary<ILabelSymbol, ILabelSymbol> _labelMap = new(SymbolEqualityComparer.Default);

            public LoweredLabelUniquifier(int ordinal)
            {
                _ordinal = ordinal;
            }

            public override ILabelSymbol VisitLabel(ILabelSymbol label)
            {
                if (_labelMap.TryGetValue(label, out var mapped))
                    return mapped;

                var containingType = label.ContainingType as INamedTypeSymbol;
                var renamed = new LabelSymbol(
                    $"{label.Name}__async{_ordinal}",
                    label.ContainingSymbol,
                    containingType,
                    label.ContainingNamespace,
                    [Location.None],
                    Array.Empty<SyntaxReference>());

                _labelMap[label] = renamed;
                return renamed;
            }
        }
    }

    private sealed class AsyncUseDeclarationLowerer : BoundTreeRewriter
    {
        private readonly Compilation _compilation;

        public AsyncUseDeclarationLowerer(Compilation compilation)
        {
            _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        }

        public BoundBlockStatement Rewrite(BoundBlockStatement body)
        {
            return (BoundBlockStatement)VisitBlockStatement(body)!;
        }

        public override BoundNode DefaultVisit(BoundNode boundNode)
        {
            return boundNode;
        }

        public override BoundNode? VisitBlockStatement(BoundBlockStatement node)
        {
            if (node is null)
                return null;

            var statements = new List<BoundStatement>();
            foreach (var statement in node.Statements)
                statements.Add((BoundStatement)VisitStatement(statement)!);

            var loweredStatements = statements.ToImmutableArray();
            var handledUsingLocals = new HashSet<ILocalSymbol>(ReferenceEqualityComparer.Instance);
            var rewritten = RewriteUseDeclarations(loweredStatements, handledUsingLocals);

            if (handledUsingLocals.Count == 0)
                return new BoundBlockStatement(rewritten, node.LocalsToDispose);

            var localsBuilder = ImmutableArray.CreateBuilder<ILocalSymbol>(node.LocalsToDispose.Length);
            foreach (var local in node.LocalsToDispose)
            {
                if (!handledUsingLocals.Contains(local))
                    localsBuilder.Add(local);
            }

            return new BoundBlockStatement(rewritten, localsBuilder.ToImmutable());
        }

        public override BoundNode? VisitBlockExpression(BoundBlockExpression node)
        {
            if (node is null)
                return null;

            var statements = new List<BoundStatement>();
            foreach (var statement in node.Statements)
                statements.Add((BoundStatement)VisitStatement(statement)!);

            var loweredStatements = statements.ToImmutableArray();
            var handledUsingLocals = new HashSet<ILocalSymbol>(ReferenceEqualityComparer.Instance);
            var rewritten = RewriteUseDeclarations(loweredStatements, handledUsingLocals);

            if (handledUsingLocals.Count == 0)
                return new BoundBlockExpression(rewritten, node.UnitType, node.LocalsToDispose);

            var localsBuilder = ImmutableArray.CreateBuilder<ILocalSymbol>(node.LocalsToDispose.Length);
            foreach (var local in node.LocalsToDispose)
            {
                if (!handledUsingLocals.Contains(local))
                    localsBuilder.Add(local);
            }

            return new BoundBlockExpression(rewritten, node.UnitType, localsBuilder.ToImmutable());
        }

        private ImmutableArray<BoundStatement> RewriteUseDeclarations(
            ImmutableArray<BoundStatement> statements,
            HashSet<ILocalSymbol> handledUsingLocals)
        {
            if (statements.IsDefaultOrEmpty)
                return statements;

            var builder = ImmutableArray.CreateBuilder<BoundStatement>(statements.Length);

            for (var i = 0; i < statements.Length; i++)
            {
                var statement = statements[i];

                if (statement is BoundLocalDeclarationStatement { IsUsing: true } useDeclaration)
                {
                    var declarators = useDeclaration.Declarators.ToArray();
                    foreach (var declarator in declarators)
                        handledUsingLocals.Add(declarator.Local);

                    var loweredUsing = new BoundLocalDeclarationStatement(declarators);
                    var remaining = statements.RemoveRange(0, i + 1);
                    var tryBlockStatements = RewriteUseDeclarations(remaining, handledUsingLocals);
                    var tryBlock = new BoundBlockStatement(tryBlockStatements, ImmutableArray<ILocalSymbol>.Empty);

                    var finallyStatements = CreateDisposeStatements(declarators);
                    var finallyBlock = new BoundBlockStatement(finallyStatements, ImmutableArray<ILocalSymbol>.Empty);
                    var tryStatement = new BoundTryStatement(
                        tryBlock,
                        ImmutableArray<BoundCatchClause>.Empty,
                        finallyBlock,
                        BoundTryStatementKind.UsingLifetime);

                    builder.Add(loweredUsing);
                    builder.Add(tryStatement);
                    return builder.ToImmutable();
                }

                builder.Add(statement);
            }

            return builder.ToImmutable();
        }

        private ImmutableArray<BoundStatement> CreateDisposeStatements(BoundVariableDeclarator[] declarators)
        {
            if (declarators.Length == 0)
                return ImmutableArray<BoundStatement>.Empty;

            var builder = ImmutableArray.CreateBuilder<BoundStatement>(declarators.Length);

            for (var i = declarators.Length - 1; i >= 0; i--)
            {
                if (declarators[i].FixedPinnedLocal is { } fixedPinnedLocal)
                {
                    builder.Add(CreateFixedCleanupStatement(fixedPinnedLocal));
                    continue;
                }

                var local = declarators[i].Local;
                if (local.Type is null || local.Type.TypeKind == TypeKind.Error)
                    continue;

                if (!UseDisposalUtilities.TryResolveUseDisposeMethod(_compilation, local.Type, preferAsync: true, out var disposeMethod, out var useAwait) ||
                    disposeMethod is null)
                {
                    continue;
                }

                var disposeStatement = CreateDisposeStatement(local, disposeMethod, useAwait);
                if (disposeStatement is not null)
                    builder.Add(disposeStatement);
            }

            return builder.ToImmutable();
        }

        private BoundStatement CreateFixedCleanupStatement(ILocalSymbol pinnedLocal)
        {
            var left = new BoundLocalAccess(pinnedLocal);
            var right = new BoundDefaultValueExpression(pinnedLocal.Type);
            return new BoundAssignmentStatement(new BoundLocalAssignmentExpression(pinnedLocal, left, right, _compilation.UnitTypeSymbol));
        }

        private BoundStatement? CreateDisposeStatement(ILocalSymbol local, IMethodSymbol disposeMethod, bool useAwait)
        {
            if (local.Type is null || local.Type.TypeKind == TypeKind.Error)
                return null;

            var disposeCall = new BoundExpressionStatement(
                UseDisposalUtilities.CreateDisposeInvocationExpression(_compilation, new BoundLocalAccess(local), disposeMethod, useAwait));

            if (local.Type.IsReferenceType || local.Type.TypeKind == TypeKind.Null)
            {
                var nullLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, local.Type);

                if (BoundBinaryOperator.TryLookup(_compilation, SyntaxKind.NotEqualsToken, local.Type, local.Type, out var notEquals))
                {
                    var condition2 = new BoundBinaryExpression(new BoundLocalAccess(local), notEquals, nullLiteral);
                    return new BoundIfStatement(condition2, new BoundBlockStatement(new[] { disposeCall }));
                }

                var booleanType = _compilation.GetSpecialType(SpecialType.System_Boolean);
                var objectType = _compilation.GetSpecialType(SpecialType.System_Object);
                if (booleanType.TypeKind == TypeKind.Error || objectType.TypeKind == TypeKind.Error)
                    return disposeCall;

                var nullPatternLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, _compilation.NullTypeSymbol);
                var nullPattern = new BoundConstantPattern(nullPatternLiteral);
                var notNullPattern = new BoundNotPattern(nullPattern);
                var condition = new BoundIsPatternExpression(new BoundLocalAccess(local), notNullPattern, booleanType);
                return new BoundIfStatement(condition, new BoundBlockStatement(new[] { disposeCall }));
            }

            return disposeCall;
        }
    }

    private static AsyncRewriteResult RewriteMethod(
        Compilation compilation,
        SourceMethodSymbol method,
        BoundBlockStatement body,
        AsyncMethodAnalysis analysis)
    {

        if (!analysis.ContainsAwait && !compilation.IsRuntimeAsyncEnabled)
            body = RewriteAwaitlessAsyncBody(compilation, method.ReturnType, body);

        if (!analysis.RequiresStateMachine)
            return new AsyncRewriteResult(body, method.AsyncStateMachine, analysis);

        if (method.AsyncStateMachine is null)
        {
            var stateMachine = compilation.CreateAsyncStateMachine(method);
            method.SetAsyncStateMachine(stateMachine);
        }

        var asyncStateMachine = method.AsyncStateMachine;
        if (asyncStateMachine is null)
            throw new InvalidOperationException("Async state machine not created.");

        if (asyncStateMachine.OriginalBody is null)
            asyncStateMachine.SetOriginalBody(body);

        if (asyncStateMachine.MoveNextBody is null)
        {
            var moveNextBody = CreateMoveNextBody(compilation, asyncStateMachine);
            asyncStateMachine.SetMoveNextBody(moveNextBody);
        }

        if (asyncStateMachine.SetStateMachineBody is null)
        {
            var setStateMachineBody = CreateSetStateMachineBody(asyncStateMachine);
            if (setStateMachineBody is not null)
                asyncStateMachine.SetSetStateMachineBody(setStateMachineBody);
        }

        var rewrittenBody = RewriteAsyncBody(compilation, method, asyncStateMachine);
        return new AsyncRewriteResult(rewrittenBody, asyncStateMachine, analysis);
    }

    private static BoundBlockStatement RewriteLambdaBody(
        Compilation compilation,
        SourceLambdaSymbol lambda,
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        BoundBlockStatement body)
    {
        if (compilation is null)
            throw new ArgumentNullException(nameof(compilation));
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (stateMachine is null)
            throw new ArgumentNullException(nameof(stateMachine));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        return RewriteAsyncBody(compilation, lambda, stateMachine);
    }

    public static bool ShouldRewrite(SourceMethodSymbol method, BoundBlockStatement body)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        return method.IsAsync && !method.IsIterator && !method.IsSignatureSkeleton;
    }

    public static bool ShouldRewrite(SourceLambdaSymbol lambda, BoundBlockStatement body)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (body is null)
            throw new ArgumentNullException(nameof(body));

        return lambda.IsAsync && !lambda.IsIterator;
    }

    private static BoundBlockStatement CreateMoveNextBody(
        Compilation compilation,
        SynthesizedAsyncStateMachineTypeSymbol stateMachine)
    {
        var context = new MoveNextLoweringContext(compilation, stateMachine);
        var originalBody = stateMachine.OriginalBody ?? new BoundBlockStatement(Array.Empty<BoundStatement>());

        AsyncLambdaClosureRewriter? closureRewriter = null;

        var constructedMembers = stateMachine.GetConstructedMembers(stateMachine.AsyncMethod);

        if (constructedMembers.ThisField is IFieldSymbol closureField)
        {
            if (stateMachine.AsyncMethod.ContainingType is SynthesizedIteratorTypeSymbol iteratorType)
            {
                originalBody = new AsyncIteratorReceiverRewriter(
                    stateMachine,
                    iteratorType,
                    closureField).Rewrite(originalBody);
            }
            else
            {
                closureRewriter = new AsyncLambdaClosureRewriter(stateMachine, closureField);
                originalBody = closureRewriter.Rewrite(originalBody);
            }
        }
        else if (stateMachine.ThisField is IFieldSymbol definitionClosureField)
        {
            closureRewriter = new AsyncLambdaClosureRewriter(stateMachine, definitionClosureField);
            originalBody = closureRewriter.Rewrite(originalBody);
        }

        var entryLabel = CreateLabel(stateMachine, "state");

        var lowerAfterAwaitRewrite = ContainsUsingDeclaration(originalBody);
        var awaitRewriter = new AwaitLoweringRewriter(stateMachine, context.BuilderMembers, lowerAfterAwaitRewrite);
        var rewrittenBody = awaitRewriter.Rewrite(originalBody);

        // Await rewriting can expose propagation and other general constructs whose
        // lowering introduces protected regions. Materialize those regions before
        // dispatch injection so resume guards are computed from the final try shape.
        // Bodies without `use` were fully lowered before await rewriting. Bodies with
        // `use` must defer general lowering so disposal remains suspension-aware.
        if (lowerAfterAwaitRewrite)
            rewrittenBody = Lowerer.LowerBlock(stateMachine.MoveNextMethod, rewrittenBody);

        rewrittenBody = StateDispatchInjector.Inject(
            rewrittenBody,
            stateMachine,
            awaitRewriter.Dispatches,
            out var guardEntryLabels);

        if (closureRewriter is not null)
            rewrittenBody = closureRewriter.Rewrite(rewrittenBody);

        var tryStatements = new List<BoundStatement>();
        tryStatements.AddRange(CreateStateDispatchStatements(
            context,
            entryLabel,
            awaitRewriter.Dispatches,
            guardEntryLabels));

        var entryStatements = awaitRewriter.CompletionLabel is null
            ? new List<BoundStatement>(rewrittenBody.Statements)
            : new List<BoundStatement> { rewrittenBody };
        var completionStatements = new List<BoundStatement>();
        if (!stateMachine.HoistedLocalsToDispose.IsDefaultOrEmpty)
        {
            var disposeStatements = CreateDisposeStatements(
                stateMachine,
                EnumerateReverse(stateMachine.HoistedLocalsToDispose));
            completionStatements.AddRange(disposeStatements);
        }

        completionStatements.AddRange(CreateCompletionStatements(
            context,
            awaitRewriter.CompletionResult is { } resultLocal ? new BoundLocalAccess(resultLocal) : null));
        if (awaitRewriter.CompletionLabel is { } completionLabel)
            entryStatements.Add(new BoundLabeledStatement(completionLabel, new BoundBlockStatement(completionStatements)));
        else
            entryStatements.AddRange(completionStatements);
        var entryBlock = new BoundBlockStatement(
            entryStatements,
            awaitRewriter.CompletionLabel is null ? rewrittenBody.LocalsToDispose : ImmutableArray<ILocalSymbol>.Empty);

        if (closureRewriter is not null)
            entryBlock = closureRewriter.Rewrite(entryBlock);

        tryStatements.Add(new BoundLabeledStatement(entryLabel, entryBlock));

        var tryBlock = new BoundBlockStatement(tryStatements);

        var catchClauses = ImmutableArray<BoundCatchClause>.Empty;
        var catchClause = CreateExceptionCatchClause(context);
        if (catchClause is not null)
            catchClauses = ImmutableArray.Create(catchClause);

        var tryStatement = new BoundTryStatement(
            tryBlock,
            catchClauses,
            finallyBlock: null,
            BoundTryStatementKind.AsyncDispatchGuard);
        var moveNextStatements = new List<BoundStatement>();
        if (awaitRewriter.CompletionResult is { } completionResult)
        {
            moveNextStatements.Add(new BoundLocalDeclarationStatement([
                new BoundVariableDeclarator(completionResult, new BoundDefaultValueExpression(completionResult.Type))
            ]));
        }
        moveNextStatements.Add(tryStatement);
        var moveNextBody = new BoundBlockStatement(moveNextStatements);
        return Lowerer.LowerBlock(stateMachine.MoveNextMethod, moveNextBody);
    }

    private static BoundBlockStatement RewriteAsyncBody(
        Compilation compilation,
        IMethodSymbol method,
        SynthesizedAsyncStateMachineTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();
        var unitType = compilation.GetSpecialType(SpecialType.System_Unit);

        var constructed = stateMachine.GetConstructedMembers(method);
        var stateMachineType = constructed.StateMachineType;
        var moveNextMethod = constructed.MoveNext;
        var stateField = constructed.StateField;
        var builderMembers = constructed.AsyncMethodBuilderMembers;
        var builderField = builderMembers.BuilderField;
        var thisField = constructed.ThisField;
        var parameterFieldMap = constructed.ParameterFields;

        var asyncLocal = new SourceLocalSymbol(
            "<>async",
            stateMachineType,
            isMutable: true,
            method,
            method.ContainingType,
            method.ContainingNamespace,
            Array.Empty<Location>(),
            Array.Empty<SyntaxReference>());

        var declarator = new BoundVariableDeclarator(asyncLocal, initializer: null);
        statements.Add(new BoundLocalDeclarationStatement(new[] { declarator }));

        if (thisField is not null)
        {
            var receiver = new BoundLocalAccess(asyncLocal);
            var value = new BoundSelfExpression(thisField.Type);
            var assignment = new BoundFieldAssignmentExpression(receiver, thisField, value, unitType, requiresReceiverAddress: true);
            statements.Add(new BoundAssignmentStatement(assignment));
        }

        foreach (var parameter in method.Parameters)
        {
            if (!parameterFieldMap.TryGetValue(parameter, out var field))
                continue;

            var receiver = new BoundLocalAccess(asyncLocal);
            var value = new BoundParameterAccess(parameter);
            var assignment = new BoundFieldAssignmentExpression(receiver, field, value, unitType, requiresReceiverAddress: true);
            statements.Add(new BoundAssignmentStatement(assignment));
        }

        var stateReceiver = new BoundLocalAccess(asyncLocal);
        var initialState = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            -1,
            stateField.Type);
        var stateAssignment = new BoundFieldAssignmentExpression(stateReceiver, stateField, initialState, unitType, requiresReceiverAddress: true);
        statements.Add(new BoundAssignmentStatement(stateAssignment));

        var builderInitialization = CreateBuilderInitializationStatement(stateMachine, asyncLocal, builderMembers, unitType);
        if (builderInitialization is not null)
            statements.Add(builderInitialization);

        var builderStartStatement = CreateBuilderStartStatement(stateMachine, asyncLocal, builderMembers, stateMachineType);
        if (builderStartStatement is not null)
        {
            statements.Add(builderStartStatement);
        }
        else
        {
            var moveNextInvocation = new BoundInvocationExpression(
                moveNextMethod,
                Array.Empty<BoundExpression>(),
                receiver: new BoundLocalAccess(asyncLocal),
                requiresReceiverAddress: true);
            statements.Add(new BoundExpressionStatement(moveNextInvocation));
        }

        BoundExpression? returnExpression = null;
        if (method.ReturnType.SpecialType != SpecialType.System_Void)
        {
            returnExpression = CreateReturnExpression(method, builderMembers, asyncLocal);
        }

        statements.Add(new BoundReturnStatement(returnExpression));

        return new BoundBlockStatement(statements);
    }

    private static BoundCatchClause? CreateExceptionCatchClause(MoveNextLoweringContext context)
    {
        var exceptionType = context.Compilation.GetSpecialType(SpecialType.System_Exception);

        var exceptionLocal = new SourceLocalSymbol(
            "<>ex",
            exceptionType,
            isMutable: true,
            context.StateMachine.MoveNextMethod,
            context.StateMachine,
            context.StateMachine.ContainingNamespace,
            Array.Empty<Location>(),
            Array.Empty<SyntaxReference>());

        var statements = new List<BoundStatement>
        {
            CreateStateAssignment(context.StateMachine, -2)
        };

        statements.AddRange(CreateDisposeStatements(context.StateMachine, EnumerateReverse(context.StateMachine.HoistedLocalsToDispose)));

        var setException = CreateBuilderSetExceptionStatement(context.StateMachine, context.BuilderMembers, exceptionLocal);
        if (setException is not null)
            statements.Add(setException);

        statements.Add(new BoundReturnStatement(null));

        var catchBlock = new BoundBlockStatement(statements);
        return new BoundCatchClause(exceptionType, exceptionLocal, pattern: null, guard: null, catchBlock);
    }

    private static IEnumerable<BoundStatement> CreateStateDispatchStatements(
        MoveNextLoweringContext context,
        ILabelSymbol entryLabel,
        ImmutableArray<StateDispatch> dispatches,
        ImmutableDictionary<int, ILabelSymbol> guardEntryLabels)
    {
        var statements = new List<BoundStatement>();

        var stateField = context.StateMachine.StateField;
        var stateType = stateField.Type;
        if (!BoundBinaryOperator.TryLookup(context.Compilation, SyntaxKind.EqualsEqualsToken, stateType, stateType, out var equals))
            throw new InvalidOperationException("Async lowering requires integer equality operator.");

        statements.Add(CreateStateDispatchStatement(stateField, equals, -1, entryLabel));

        if (!dispatches.IsDefaultOrEmpty)
        {
            foreach (var dispatch in dispatches)
            {
                ILabelSymbol targetLabel = dispatch.Label;
                if (!guardEntryLabels.IsEmpty && guardEntryLabels.TryGetValue(dispatch.State, out var guardEntryLabel))
                    targetLabel = guardEntryLabel;

                statements.Add(CreateStateDispatchStatement(stateField, equals, dispatch.State, targetLabel));
            }
        }

        statements.Add(new BoundGotoStatement(entryLabel));

        return statements;
    }

    private static BoundStatement CreateStateDispatchStatement(
        SourceFieldSymbol stateField,
        BoundBinaryOperator equals,
        int state,
        ILabelSymbol targetLabel)
    {
        var stateAccess = new BoundFieldAccess(stateField);
        var stateLiteral = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            state,
            stateField.Type);

        var condition = new BoundBinaryExpression(stateAccess, equals, stateLiteral);
        var gotoTarget = new BoundGotoStatement(targetLabel);
        var thenBlock = new BoundBlockStatement(new BoundStatement[] { gotoTarget });
        return new BoundIfStatement(condition, thenBlock);
    }

    private static IEnumerable<BoundStatement> CreateCompletionStatements(MoveNextLoweringContext context, BoundExpression? result = null)
    {
        yield return CreateStateAssignment(context.StateMachine, -2);

        var setResult = CreateBuilderSetResultStatement(context.StateMachine, context.BuilderMembers, result);
        if (setResult is not null)
            yield return setResult;

        yield return new BoundReturnStatement(null);
    }

    private readonly struct MoveNextLoweringContext
    {
        public MoveNextLoweringContext(
            Compilation compilation,
            SynthesizedAsyncStateMachineTypeSymbol stateMachine)
        {
            Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
            StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            BuilderMembers = stateMachine.GetBuilderMembers(stateMachine.AsyncMethod);
        }

        public Compilation Compilation { get; }
        public SynthesizedAsyncStateMachineTypeSymbol StateMachine { get; }
        public SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers BuilderMembers { get; }
    }

    private sealed class AsyncLambdaClosureRewriter : BoundTreeRewriter
    {
        private readonly SynthesizedAsyncStateMachineTypeSymbol _stateMachine;
        private readonly IFieldSymbol _closureField;
        private readonly Dictionary<string, ILocalSymbol> _capturedLocals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SourceFieldSymbol> _capturedHoists = new(StringComparer.Ordinal);

        public AsyncLambdaClosureRewriter(
            SynthesizedAsyncStateMachineTypeSymbol stateMachine,
            IFieldSymbol closureField)
        {
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _closureField = closureField ?? throw new ArgumentNullException(nameof(closureField));

            if (stateMachine.AsyncMethod is SourceLambdaSymbol { HasCaptures: true } lambda)
            {
                foreach (var captured in lambda.CapturedVariables.OfType<ILocalSymbol>())
                    _capturedLocals[captured.Name] = captured;
            }
        }

        public BoundBlockStatement Rewrite(BoundBlockStatement body)
        {
            if (body is null)
                throw new ArgumentNullException(nameof(body));

            return (BoundBlockStatement)VisitBlockStatement(body)!;
        }

        public override BoundNode DefaultVisit(BoundNode boundNode)
        {
            return boundNode;
        }

        public override BoundExpression? VisitSelfExpression(BoundSelfExpression node)
        {
            if (_stateMachine.AsyncMethod is SourceLambdaSymbol { HasCaptures: true } &&
                node.Type is { } type &&
                !SymbolEqualityComparer.Default.Equals(type, _stateMachine) &&
                SymbolEqualityComparer.Default.Equals(type, _closureField.Type))
            {
                var receiver = new BoundSelfExpression(_stateMachine);
                return new BoundMemberAccessExpression(receiver, _closureField);
            }

            return (BoundExpression?)base.VisitSelfExpression(node);
        }

        public override BoundExpression? VisitFieldAccess(BoundFieldAccess node)
        {
            var visited = (BoundExpression?)base.VisitFieldAccess(node);

            if (visited is BoundFieldAccess fieldAccess)
            {
                if (TryRewriteCapturedField(fieldAccess.Field, fieldAccess.Reason, out var rewritten))
                    return rewritten;

                if (_stateMachine.AsyncMethod is SourceLambdaSymbol { HasCaptures: true } &&
                    fieldAccess.Field.ContainingType is { } containingType &&
                    SymbolEqualityComparer.Default.Equals(containingType, _closureField.Type))
                {
                    var receiver = new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), _closureField);
                    return new BoundMemberAccessExpression(receiver, fieldAccess.Field, fieldAccess.Reason);
                }
            }

            return visited;
        }

        public override BoundExpression? VisitMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var visitedReceiver = (BoundExpression?)Visit(node.Receiver);

            if (node.Member is IFieldSymbol field &&
                TryRewriteCapturedField(field, node.Reason, out var rewritten))
            {
                return rewritten;
            }

            if (!ReferenceEquals(visitedReceiver, node.Receiver))
                return new BoundMemberAccessExpression(visitedReceiver, node.Member, node.Reason);

            return node;
        }

        private bool TryRewriteCapturedField(IFieldSymbol field, BoundExpressionReason reason, out BoundExpression? rewritten)
        {
            rewritten = null;

            var capturedName = ExtractCapturedName(field.Name);
            if (capturedName is null)
                return false;

            if (_stateMachine.AsyncMethod is SourceLambdaSymbol { HasCaptures: true } &&
                _closureField.Type is { } closureType &&
                field.ContainingType is { } containingType &&
                (SymbolEqualityComparer.Default.Equals(containingType, closureType) ||
                 containingType.Name.Contains("DisplayClass", StringComparison.Ordinal)))
            {
                var closureReceiver = new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), _closureField);
                rewritten = new BoundMemberAccessExpression(closureReceiver, field, reason);
                return true;
            }

            if (!_capturedLocals.TryGetValue(capturedName, out var capturedLocal))
            {
                if (!_capturedHoists.TryGetValue(capturedName, out var hoistedField))
                {
                    var hoistedType = field.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
                    var hoistedName = $"<>local{_stateMachine.HoistedLocals.Length}";
                    hoistedField = _stateMachine.AddHoistedLocal(hoistedName, hoistedType, requiresDispose: false);
                    _capturedHoists[capturedName] = hoistedField;
                }

                var hoistReceiver = new BoundSelfExpression(_stateMachine);
                rewritten = new BoundMemberAccessExpression(hoistReceiver, hoistedField, reason);
                return true;
            }

            if (!_capturedHoists.TryGetValue(capturedName, out var hoisted))
            {
                if (!_stateMachine.TryGetHoistedLocalField(capturedLocal, out hoisted))
                {
                    var type = capturedLocal.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
                    var fieldName = $"<>local{_stateMachine.HoistedLocals.Length}";
                    hoisted = _stateMachine.AddHoistedLocal(fieldName, type, requiresDispose: false, capturedLocal);
                }

                _capturedHoists[capturedName] = hoisted;
            }

            var stateReceiver = new BoundSelfExpression(_stateMachine);
            rewritten = new BoundMemberAccessExpression(stateReceiver, hoisted, reason);
            return true;
        }

        private static string? ExtractCapturedName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
                return null;

            if (fieldName.Length > 2 && fieldName[0] == '<')
            {
                var closing = fieldName.IndexOf('>');
                if (closing > 1)
                    return fieldName.Substring(1, closing - 1);
            }

            return null;
        }
    }

    private sealed class SelfTypeFinder : BoundTreeVisitor
    {
        public ITypeSymbol? SelfType { get; private set; }

        public override void VisitSelfExpression(BoundSelfExpression node)
        {
            SelfType ??= node.Type;
            base.VisitSelfExpression(node);
        }
    }

    private static ITypeSymbol? FindCapturedClosureType(BoundBlockStatement body)
    {
        var finder = new CapturedClosureTypeFinder();
        finder.VisitBlockStatement(body);
        return finder.ClosureType;
    }

    private sealed class CapturedClosureTypeFinder : BoundTreeVisitor
    {
        public ITypeSymbol? ClosureType { get; private set; }

        public override void VisitFieldAccess(BoundFieldAccess node)
        {
            if (ClosureType is null &&
                node.Field.ContainingType is { } containingType &&
                containingType.Name.Contains("DisplayClass", StringComparison.Ordinal))
            {
                ClosureType = containingType;
                return;
            }

            base.VisitFieldAccess(node);
        }
    }

    private static BoundStatement? CreateBuilderSetResultStatement(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
        BoundExpression? expression)
    {
        var setResultMethod = builderMembers.SetResult;
        if (setResultMethod is null)
            return null;

        var arguments = Array.Empty<BoundExpression>();

        if (setResultMethod.Parameters.Length == 1)
        {
            BoundExpression? argument = expression;

            if (IsEffectivelyVoidExpression(argument))
                argument = null;

            if (argument is null)
            {
                argument = CreateDefaultValueExpression(setResultMethod.Parameters[0].Type);
                if (argument is null)
                    return null;
            }

            arguments = new[] { argument };
        }
        else if (setResultMethod.Parameters.Length != 0)
        {
            return null;
        }

        if (setResultMethod.Parameters.Length == 0 && expression is not null && !IsEffectivelyVoidExpression(expression))
            return null;

        var receiver = new BoundMemberAccessExpression(new BoundSelfExpression(stateMachine), builderMembers.BuilderField);
        var invocation = new BoundInvocationExpression(setResultMethod, arguments, receiver, requiresReceiverAddress: true);
        return new BoundExpressionStatement(invocation);
    }

    private static bool IsEffectivelyVoidExpression(BoundExpression? expression)
    {
        if (expression is null)
            return true;

        switch (expression)
        {
            case BoundUnitExpression:
                return true;

            case BoundLiteralExpression literal when literal.Value is null:
                return true;

            case BoundConversionExpression cast when cast.Conversion.IsIdentity && IsEffectivelyVoidExpression(cast.Expression):
                return true;

            case BoundAsExpression asExpression when asExpression.Conversion.IsIdentity && IsEffectivelyVoidExpression(asExpression.Expression):
                return true;

            case BoundMemberAccessExpression memberAccess when IsCompletedTaskAccess(memberAccess):
                return true;
        }

        return false;
    }

    private static bool IsCompletedTaskAccess(BoundMemberAccessExpression memberAccess)
    {
        if (memberAccess.Member is not ISymbol member)
            return false;

        if (member is not (IPropertySymbol or IFieldSymbol))
            return false;

        if (member.Name != "CompletedTask")
            return false;

        return member.ContainingType?.SpecialType == SpecialType.System_Threading_Tasks_Task;
    }

    private static BoundExpression? CreateDefaultValueExpression(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Void)
            return null;

        if (type.IsReferenceType)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, type);

        if (type.SpecialType == SpecialType.System_Boolean)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.FalseLiteral, false, type);

        if (type.SpecialType == SpecialType.System_Int32)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0, type);

        if (type.SpecialType == SpecialType.System_Double)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0d, type);

        if (type.SpecialType == SpecialType.System_Single)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0f, type);

        if (type.SpecialType == SpecialType.System_Int64)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0L, type);

        if (type.SpecialType == SpecialType.System_Decimal)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0m, type);

        if (type.SpecialType == SpecialType.System_Int16)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, (short)0, type);

        if (type.SpecialType == SpecialType.System_Byte)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, (byte)0, type);

        if (type.SpecialType == SpecialType.System_UInt16)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, (ushort)0, type);

        if (type.SpecialType == SpecialType.System_UInt32)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0u, type);

        if (type.SpecialType == SpecialType.System_UInt64)
            return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0ul, type);

        return null;
    }

    private static LabelSymbol CreateLabel(SynthesizedAsyncStateMachineTypeSymbol stateMachine, string name)
    {
        return new LabelSymbol(
            name,
            stateMachine.MoveNextMethod,
            stateMachine,
            stateMachine.ContainingNamespace,
            new[] { Location.None },
            Array.Empty<SyntaxReference>());
    }

    private static BoundAssignmentStatement CreateStateAssignment(SynthesizedAsyncStateMachineTypeSymbol stateMachine, int state)
    {
        var literal = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            state,
            stateMachine.StateField.Type);

        var assignment = CreateStateMachineFieldAssignment(stateMachine, stateMachine.StateField, literal);
        return new BoundAssignmentStatement(assignment);
    }

    private static BoundFieldAssignmentExpression CreateStateMachineFieldAssignment(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SourceFieldSymbol field,
        BoundExpression value)
    {
        return new BoundFieldAssignmentExpression(
            new BoundSelfExpression(stateMachine),
            field,
            value,
            stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit),
            requiresReceiverAddress: true);
    }

    private static BoundStatement? CreateBuilderSetExceptionStatement(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
        ILocalSymbol exceptionLocal)
    {
        var setExceptionMethod = builderMembers.SetException;
        if (setExceptionMethod is null)
            return null;

        var builderAccess = new BoundMemberAccessExpression(new BoundSelfExpression(stateMachine), builderMembers.BuilderField);
        var exceptionAccess = new BoundLocalAccess(exceptionLocal);

        var invocation = new BoundInvocationExpression(
            setExceptionMethod,
            new BoundExpression[] { exceptionAccess },
            builderAccess,
            requiresReceiverAddress: true);

        return new BoundExpressionStatement(invocation);
    }

    private static IEnumerable<BoundStatement> CreateDisposeStatements(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        IEnumerable<SourceFieldSymbol> fields)
    {
        foreach (var field in fields)
        {
            if (!UseDisposalUtilities.TryResolveUseDisposeMethod(
                    stateMachine.Compilation,
                    field.Type,
                    preferAsync: true,
                    out var disposeMethod,
                    out var useAwait) ||
                disposeMethod is null)
            {
                continue;
            }

            yield return CreateDisposeStatement(stateMachine, field, disposeMethod, useAwait);
        }
    }

    private static IEnumerable<SourceFieldSymbol> EnumerateReverse(ImmutableArray<SourceFieldSymbol> fields)
    {
        if (fields.IsDefaultOrEmpty)
            yield break;

        for (var i = fields.Length - 1; i >= 0; i--)
            yield return fields[i];
    }

    private static BoundStatement CreateDisposeStatement(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SourceFieldSymbol field,
        IMethodSymbol disposeMethod,
        bool useAwait)
    {
        var compilation = stateMachine.Compilation;
        var receiver = new BoundMemberAccessExpression(new BoundSelfExpression(stateMachine), field);
        var disposeCall = new BoundExpressionStatement(
            UseDisposalUtilities.CreateBlockingDisposeInvocationExpression(compilation, receiver, disposeMethod, useAwait));

        BoundStatement? clearStatement = null;
        var defaultValue = CreateDefaultValueExpression(field.Type);
        if (defaultValue is not null)
        {
            var assignment = CreateStateMachineFieldAssignment(stateMachine, field, defaultValue);
            clearStatement = new BoundAssignmentStatement(assignment);
        }

        if (field.Type.IsReferenceType)
        {
            var nullLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, field.Type);

            if (BoundBinaryOperator.TryLookup(compilation, SyntaxKind.NotEqualsToken, field.Type, field.Type, out var notEquals))
            {
                var access = new BoundMemberAccessExpression(new BoundSelfExpression(stateMachine), field);
                var condition = new BoundBinaryExpression(access, notEquals, nullLiteral);

                var statements = clearStatement is null
                    ? new BoundStatement[] { disposeCall }
                    : new BoundStatement[] { disposeCall, clearStatement };

                return new BoundIfStatement(condition, new BoundBlockStatement(statements));
            }
        }

        if (clearStatement is null)
            return disposeCall;

        return new BoundBlockStatement(new BoundStatement[] { disposeCall, clearStatement });
    }

    private sealed class AsyncMethodExpressionSubstituter : BoundTreeRewriter
    {
        private readonly SynthesizedAsyncStateMachineTypeSymbol _stateMachine;
        private readonly INamedTypeSymbol? _builderTypeDefinition;

        private AsyncMethodExpressionSubstituter(SynthesizedAsyncStateMachineTypeSymbol stateMachine)
        {
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _builderTypeDefinition = GetBuilderTypeDefinition(stateMachine.BuilderField.Type);
        }

        public static BoundExpression Substitute(
            SynthesizedAsyncStateMachineTypeSymbol stateMachine,
            BoundExpression expression)
        {
            if (expression is null)
                throw new ArgumentNullException(nameof(expression));

            var substituter = new AsyncMethodExpressionSubstituter(stateMachine);
            return (BoundExpression)(substituter.VisitExpression(expression) ?? expression);
        }

        public override ITypeSymbol VisitType(ITypeSymbol type)
        {
            if (type is null)
                return type;

            return _stateMachine.SubstituteStateMachineTypeParameters(type);
        }

        public override IMethodSymbol VisitMethod(IMethodSymbol method)
        {
            if (method is null)
                return method;

            return _stateMachine.SubstituteStateMachineTypeParameters(method);
        }

        private ITypeSymbol? SubstituteType(ITypeSymbol? type)
        {
            if (type is null)
                return null;

            return _stateMachine.SubstituteStateMachineTypeParameters(type);
        }

        private IMethodSymbol? SubstituteMethod(IMethodSymbol? method)
        {
            if (method is null)
                return null;

            return _stateMachine.SubstituteStateMachineTypeParameters(method);
        }

        private IPropertySymbol? SubstituteProperty(IPropertySymbol? property)
        {
            if (property is null)
                return null;

            return _stateMachine.SubstituteStateMachineTypeParameters(property);
        }

        public override BoundNode? VisitCarrierConditionalAccessExpression(BoundCarrierConditionalAccessExpression node)
        {
            if (node is null)
                return null;

            // Rewrite child expressions first.
            var receiver = (BoundExpression?)VisitExpression(node.Receiver) ?? node.Receiver;
            var whenPresent = (BoundExpression?)VisitExpression(node.WhenPresent) ?? node.WhenPresent;

            // Substitute the type/method symbols carried on the node so codegen doesn't
            // try to reflect over TypeBuilderInstantiation (which throws / can produce invalid IL).
            var payloadType = SubstituteType(node.PayloadType) ?? node.PayloadType;
            var resultType = SubstituteType(node.ResultType) ?? node.ResultType;
            var carrierType = SubstituteType(node.CarrierType) ?? node.CarrierType;

            var resultOkCaseType = SubstituteType(node.ResultOkCaseType) ?? node.ResultOkCaseType;
            var resultErrorCaseType = SubstituteType(node.ResultErrorCaseType) ?? node.ResultErrorCaseType;

            var tryGetOk = SubstituteMethod(node.ResultTryGetValueForOkCaseMethod) ?? node.ResultTryGetValueForOkCaseMethod;
            var tryGetError = SubstituteMethod(node.ResultTryGetValueForErrorCaseMethod) ?? node.ResultTryGetValueForErrorCaseMethod;

            var okValueGetter = SubstituteMethod(node.ResultOkValueGetter) ?? node.ResultOkValueGetter;
            var errorDataGetter = SubstituteMethod(node.ResultErrorDataGetter) ?? node.ResultErrorDataGetter;

            var okCtor = SubstituteMethod(node.ResultOkCtor) ?? node.ResultOkCtor;
            var errorCtor = SubstituteMethod(node.ResultErrorCtor) ?? node.ResultErrorCtor;

            var implicitFromOk = SubstituteMethod(node.ResultImplicitFromOk) ?? node.ResultImplicitFromOk;
            var implicitFromError = SubstituteMethod(node.ResultImplicitFromError) ?? node.ResultImplicitFromError;

            var receiverResultOkCaseType = SubstituteType(node.ReceiverResultOkCaseType) ?? node.ReceiverResultOkCaseType;
            var receiverResultErrorCaseType = SubstituteType(node.ReceiverResultErrorCaseType) ?? node.ReceiverResultErrorCaseType;
            var receiverResultOkValueGetter = SubstituteMethod(node.ReceiverResultOkValueGetter) ?? node.ReceiverResultOkValueGetter;
            var receiverResultErrorDataGetter = SubstituteMethod(node.ReceiverResultErrorDataGetter) ?? node.ReceiverResultErrorDataGetter;

            var optionSomeCaseType = SubstituteType(node.OptionSomeCaseType) ?? node.OptionSomeCaseType;
            var optionNoneCaseType = SubstituteType(node.OptionNoneCaseType) ?? node.OptionNoneCaseType;
            var optionTryGetValueMethod = SubstituteMethod(node.OptionTryGetValueMethod) ?? node.OptionTryGetValueMethod;
            var optionSomeValueGetter = SubstituteMethod(node.OptionSomeValueGetter) ?? node.OptionSomeValueGetter;
            var optionSomeCtor = SubstituteMethod(node.OptionSomeCtor) ?? node.OptionSomeCtor;
            var optionNoneCtorOrFactory = SubstituteMethod(node.OptionNoneCtorOrFactory) ?? node.OptionNoneCtorOrFactory;
            var optionImplicitFromSome = SubstituteMethod(node.OptionImplicitFromSome) ?? node.OptionImplicitFromSome;
            var optionImplicitFromNone = SubstituteMethod(node.OptionImplicitFromNone) ?? node.OptionImplicitFromNone;

            // If nothing changed, keep the existing node.
            if (ReferenceEquals(receiver, node.Receiver) &&
                ReferenceEquals(whenPresent, node.WhenPresent) &&
                SymbolEqualityComparer.Default.Equals(payloadType, node.PayloadType) &&
                SymbolEqualityComparer.Default.Equals(resultType, node.ResultType) &&
                SymbolEqualityComparer.Default.Equals(carrierType, node.CarrierType) &&
                SymbolEqualityComparer.Default.Equals(resultOkCaseType, node.ResultOkCaseType) &&
                SymbolEqualityComparer.Default.Equals(resultErrorCaseType, node.ResultErrorCaseType) &&
                SymbolEqualityComparer.Default.Equals(tryGetOk, node.ResultTryGetValueForOkCaseMethod) &&
                SymbolEqualityComparer.Default.Equals(tryGetError, node.ResultTryGetValueForErrorCaseMethod) &&
                SymbolEqualityComparer.Default.Equals(okValueGetter, node.ResultOkValueGetter) &&
                SymbolEqualityComparer.Default.Equals(errorDataGetter, node.ResultErrorDataGetter) &&
                SymbolEqualityComparer.Default.Equals(okCtor, node.ResultOkCtor) &&
                SymbolEqualityComparer.Default.Equals(errorCtor, node.ResultErrorCtor) &&
                SymbolEqualityComparer.Default.Equals(implicitFromOk, node.ResultImplicitFromOk) &&
                SymbolEqualityComparer.Default.Equals(implicitFromError, node.ResultImplicitFromError) &&
                SymbolEqualityComparer.Default.Equals(receiverResultOkCaseType, node.ReceiverResultOkCaseType) &&
                SymbolEqualityComparer.Default.Equals(receiverResultErrorCaseType, node.ReceiverResultErrorCaseType) &&
                SymbolEqualityComparer.Default.Equals(receiverResultOkValueGetter, node.ReceiverResultOkValueGetter) &&
                SymbolEqualityComparer.Default.Equals(receiverResultErrorDataGetter, node.ReceiverResultErrorDataGetter) &&
                SymbolEqualityComparer.Default.Equals(optionSomeCaseType, node.OptionSomeCaseType) &&
                SymbolEqualityComparer.Default.Equals(optionNoneCaseType, node.OptionNoneCaseType) &&
                SymbolEqualityComparer.Default.Equals(optionTryGetValueMethod, node.OptionTryGetValueMethod) &&
                SymbolEqualityComparer.Default.Equals(optionSomeValueGetter, node.OptionSomeValueGetter) &&
                SymbolEqualityComparer.Default.Equals(optionSomeCtor, node.OptionSomeCtor) &&
                SymbolEqualityComparer.Default.Equals(optionNoneCtorOrFactory, node.OptionNoneCtorOrFactory) &&
                SymbolEqualityComparer.Default.Equals(optionImplicitFromSome, node.OptionImplicitFromSome) &&
                SymbolEqualityComparer.Default.Equals(optionImplicitFromNone, node.OptionImplicitFromNone))
            {
                return node;
            }

            // IMPORTANT: PayloadLocal is preserved as-is; it's a temp local used by the expression lowering.
            // Recreate the node with substituted types/symbols so later codegen doesn't see open generic defs.
            return new BoundCarrierConditionalAccessExpression(
             receiver: receiver,
                whenPresent: whenPresent,
                payloadType: payloadType,
                resultType: resultType,
                payloadLocal: node.PayloadLocal,
                carrierType: (INamedTypeSymbol?)carrierType,
                receiverResultOkCaseType: (INamedTypeSymbol?)receiverResultOkCaseType,
                receiverResultErrorCaseType: (INamedTypeSymbol?)receiverResultErrorCaseType,
                receiverResultOkValueGetter: receiverResultOkValueGetter,
                receiverResultErrorDataGetter: receiverResultErrorDataGetter,
                resultOkCaseType: (INamedTypeSymbol?)resultOkCaseType,
                resultErrorCaseType: (INamedTypeSymbol?)resultErrorCaseType,
                resultTryGetValueForOkCaseMethod: tryGetOk,
                resultTryGetValueForErrorCaseMethod: tryGetError,
                resultOkValueGetter: okValueGetter,
                resultErrorDataGetter: errorDataGetter,
                resultOkCtor: okCtor,
                resultErrorCtor: errorCtor,
                resultImplicitFromOk: implicitFromOk,
                resultImplicitFromError: implicitFromError,
                optionSomeCaseType: (INamedTypeSymbol?)optionSomeCaseType,
                optionNoneCaseType: (INamedTypeSymbol?)optionNoneCaseType,
                optionTryGetValueMethod: optionTryGetValueMethod,
                optionSomeValueGetter: optionSomeValueGetter,
                optionSomeCtor: optionSomeCtor,
                optionNoneCtorOrFactory: optionNoneCtorOrFactory,
                optionImplicitFromSome: optionImplicitFromSome,
                optionImplicitFromNone: optionImplicitFromNone,
                carrierKind: node.CarrierKind);
        }

        public override BoundNode? VisitRequiredResultExpression(BoundRequiredResultExpression node)
        {
            if (node is null)
                return null;

            var operand = (BoundExpression?)VisitExpression(node.Operand) ?? node.Operand;

            if (ReferenceEquals(operand, node.Operand))
            {
                return node;
            }

            return new BoundRequiredResultExpression(
                operand: operand);
        }

        public override BoundNode? VisitPropagateExpression(BoundPropagateExpression node)
        {
            if (node is null)
                return null;

            var operand = (BoundExpression?)VisitExpression(node.Operand) ?? node.Operand;

            var okType = SubstituteType(node.OkType) ?? node.OkType;
            var errorType = SubstituteType(node.ErrorType) ?? node.ErrorType;
            var enclosingResultType = SubstituteType(node.EnclosingResultType) ?? node.EnclosingResultType;

            var okCaseType = SubstituteType(node.OkCaseType) ?? node.OkCaseType;
            var errorCaseType = SubstituteType(node.ErrorCaseType) ?? node.ErrorCaseType;

            var enclosingErrorCtor = SubstituteMethod(node.EnclosingErrorConstructor) ?? node.EnclosingErrorConstructor;
            var unwrapError = SubstituteMethod(node.UnwrapErrorMethod) ?? node.UnwrapErrorMethod;
            var tryGetOutput = SubstituteMethod(node.TryGetOutputMethod) ?? node.TryGetOutputMethod;
            var tryGetResidual = SubstituteMethod(node.TryGetResidualMethod) ?? node.TryGetResidualMethod;

            var okValueProperty = SubstituteProperty(node.OkValueProperty) ?? node.OkValueProperty;

            if (ReferenceEquals(operand, node.Operand) &&
                SymbolEqualityComparer.Default.Equals(okType, node.OkType) &&
                SymbolEqualityComparer.Default.Equals(errorType, node.ErrorType) &&
                SymbolEqualityComparer.Default.Equals(enclosingResultType, node.EnclosingResultType) &&
                SymbolEqualityComparer.Default.Equals(okCaseType, node.OkCaseType) &&
                SymbolEqualityComparer.Default.Equals(errorCaseType, node.ErrorCaseType) &&
                SymbolEqualityComparer.Default.Equals(enclosingErrorCtor, node.EnclosingErrorConstructor) &&
                SymbolEqualityComparer.Default.Equals(unwrapError, node.UnwrapErrorMethod) &&
                SymbolEqualityComparer.Default.Equals(tryGetOutput, node.TryGetOutputMethod) &&
                SymbolEqualityComparer.Default.Equals(tryGetResidual, node.TryGetResidualMethod) &&
                SymbolEqualityComparer.Default.Equals(okValueProperty, node.OkValueProperty))
            {
                return node;
            }

            return new BoundPropagateExpression(
                operand: operand,
                okType: okType,
                errorType: errorType,
                enclosingResultType: (INamedTypeSymbol)enclosingResultType,
                enclosingErrorConstructor: enclosingErrorCtor,
                unwrapErrorMethod: unwrapError,
                okCaseName: node.OkCaseName,
                errorCaseName: node.ErrorCaseName,
                errorCaseHasPayload: node.ErrorCaseHasPayload,
                okCaseType: okCaseType,
                errorCaseType: errorCaseType,
                okValueProperty: okValueProperty,
                errorConversion: node.ErrorConversion,
                tryGetOutputMethod: tryGetOutput,
                tryGetResidualMethod: tryGetResidual);
        }

        public override BoundNode? VisitInvocationExpression(BoundInvocationExpression node)
        {
            if (node is null)
                return null;

            var originalArguments = node.Arguments.ToArray();
            var rewrittenArguments = new BoundExpression[originalArguments.Length];
            var changed = false;

            for (var i = 0; i < originalArguments.Length; i++)
            {
                var rewritten = VisitExpression(originalArguments[i]) ?? originalArguments[i];
                rewrittenArguments[i] = rewritten;
                if (!ReferenceEquals(rewritten, originalArguments[i]))
                    changed = true;
            }

            var receiver = VisitExpression(node.Receiver) ?? node.Receiver;
            var extensionReceiver = VisitExpression(node.ExtensionReceiver) ?? node.ExtensionReceiver;
            var method = VisitMethod(node.Method);

            changed |= !ReferenceEquals(receiver, node.Receiver);
            changed |= !ReferenceEquals(extensionReceiver, node.ExtensionReceiver);
            changed |= !SymbolEqualityComparer.Default.Equals(method, node.Method);

            var normalized = NormalizeInvocationForLowering(
                method,
                rewrittenArguments,
                receiver,
                extensionReceiver,
                node.RequiresReceiverAddress);

            if (!changed &&
                ReferenceEquals(normalized.Arguments, node.Arguments) &&
                ReferenceEquals(normalized.Receiver, node.Receiver) &&
                ReferenceEquals(normalized.ExtensionReceiver, node.ExtensionReceiver) &&
                SymbolEqualityComparer.Default.Equals(normalized.Method, node.Method))
            {
                return node;
            }

            return normalized;
        }

        private static INamedTypeSymbol? GetBuilderTypeDefinition(ITypeSymbol builderType)
        {
            if (builderType is INamedTypeSymbol named)
            {
                if (named.IsGenericType && !named.IsUnboundGenericType)
                    return named.ConstructedFrom as INamedTypeSymbol ?? named;

                return named;
            }

            return null;
        }

        private bool IsBuilderType(ITypeSymbol type)
        {
            if (_builderTypeDefinition is null)
                return false;

            if (SymbolEqualityComparer.Default.Equals(type, _builderTypeDefinition))
                return true;

            if (type is INamedTypeSymbol named)
            {
                var definition = named.ConstructedFrom as INamedTypeSymbol ?? named;
                if (SymbolEqualityComparer.Default.Equals(definition, _builderTypeDefinition))
                    return true;

                if (named.OriginalDefinition is INamedTypeSymbol original &&
                    !ReferenceEquals(original, named) &&
                    SymbolEqualityComparer.Default.Equals(original, _builderTypeDefinition))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsBuilderMethod(IMethodSymbol method)
        {
            if (method is null)
                return false;

            if (method.UnderlyingSymbol is IMethodSymbol underlying && !ReferenceEquals(underlying, method))
                return IsBuilderMethod(underlying);

            if (method.OriginalDefinition is IMethodSymbol original && !ReferenceEquals(original, method))
                return IsBuilderMethod(original);

            if (method.ContainingType is ITypeSymbol containingType && IsBuilderType(containingType))
                return true;

            return false;
        }
    }

    private sealed class AwaitLoweringRewriter : BoundTreeRewriter
    {
        private readonly SynthesizedAsyncStateMachineTypeSymbol _stateMachine;
        private readonly SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers _builderMembers;
        private readonly List<StateDispatch> _dispatches = new();
        private readonly Dictionary<ILocalSymbol, SourceFieldSymbol> _hoistedLocals = new(ReferenceEqualityComparer.Instance);
        private ImmutableDictionary<ILocalSymbol, bool> _hoistableLocals =
            ImmutableDictionary<ILocalSymbol, bool>.Empty.WithComparers(ReferenceEqualityComparer.Instance);
        private int _nextState;
        private int _nextHoistedLocalId;
        private int _nextAwaitResultId;
        private int _nextAwaiterLocalId;
        private readonly Stack<BoundBlockStatement> _tryBlocks = new();
        private readonly Dictionary<BoundBlockStatement, BoundBlockStatement> _blockMap = new(ReferenceEqualityComparer.Instance);

        public AwaitLoweringRewriter(
            SynthesizedAsyncStateMachineTypeSymbol stateMachine,
            SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
            bool deferCompletion)
        {
            if (stateMachine is null)
                throw new ArgumentNullException(nameof(stateMachine));

            _stateMachine = stateMachine;
            _builderMembers = builderMembers;
            _nextHoistedLocalId = DetermineInitialHoistedLocalId(stateMachine);
            _nextAwaitResultId = 0;
            _nextAwaiterLocalId = 0;
            if (deferCompletion)
            {
                CompletionLabel = new AsyncProtectedRegionExitLabelSymbol(
                    "complete",
                    stateMachine.MoveNextMethod,
                    stateMachine,
                    stateMachine.ContainingNamespace,
                    [Location.None],
                    []);
                if (builderMembers.SetResult is { Parameters.Length: 1 } setResult)
                {
                    CompletionResult = new SourceLocalSymbol(
                        "$asyncCompletionResult",
                        SubstituteStateMachineTypeParameters(setResult.Parameters[0].Type),
                        isMutable: true,
                        stateMachine.MoveNextMethod,
                        stateMachine,
                        stateMachine.ContainingNamespace,
                        [Location.None],
                        []);
                }
            }
        }

        public LabelSymbol? CompletionLabel { get; }
        public SourceLocalSymbol? CompletionResult { get; }

        public ImmutableArray<StateDispatch> Dispatches => _dispatches.ToImmutableArray();

        public BoundBlockStatement Rewrite(BoundBlockStatement body)
        {
            if (body is null)
                throw new ArgumentNullException(nameof(body));

            _hoistableLocals = AwaitCaptureWalker.Analyze(body, out var declarationOrder);

            foreach (var local in declarationOrder)
                AddHoistedLocal(local);

            var rewritten = RewriteBlockStatement(body, appendDisposeStatements: false);
            RemapGuardBlocks();
            return rewritten;
        }

        private static int DetermineInitialHoistedLocalId(SynthesizedAsyncStateMachineTypeSymbol stateMachine)
        {
            const string prefix = "<>local";
            var next = 0;

            foreach (var field in stateMachine.HoistedLocals)
            {
                if (!field.Name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (int.TryParse(field.Name.Substring(prefix.Length), out var parsed) && parsed >= next)
                    next = parsed + 1;
            }

            return next;
        }

        public override BoundNode? VisitBlockStatement(BoundBlockStatement node)
        {
            if (node is null)
                return null;

            return RewriteBlockStatement(node, appendDisposeStatements: true);
        }

        private BoundBlockStatement RewriteBlockStatement(
            BoundBlockStatement node,
            bool appendDisposeStatements)
        {
            var statements = new List<BoundStatement>();
            var hoistedDisposables = CollectHoistedDisposables(node.LocalsToDispose);

            foreach (var statement in node.Statements)
            {
                var rewritten = VisitStatement(statement);

                if (rewritten is BoundBlockStatement block && block.LocalsToDispose.Length == 0)
                {
                    statements.AddRange(block.Statements);
                }
                else
                {
                    statements.Add(rewritten);
                }
            }

            if (appendDisposeStatements && hoistedDisposables.Count > 0)
            {
                var disposeStatements = CreateDisposeStatements(
                    _stateMachine,
                    EnumerateReverse(hoistedDisposables));

                statements.AddRange(disposeStatements);
            }

            var localsToDispose = FilterLocalsToDispose(node.LocalsToDispose);
            var rewrittenBlock = new BoundBlockStatement(statements, localsToDispose);
            _blockMap[node] = rewrittenBlock;
            return rewrittenBlock;
        }

        private void RemapGuardBlocks()
        {
            for (var i = 0; i < _dispatches.Count; i++)
            {
                var dispatch = _dispatches[i];
                if (dispatch.GuardPath.IsDefaultOrEmpty || dispatch.GuardPath.Length == 0)
                    continue;

                var guards = dispatch.GuardPath;
                var changed = false;
                var builder = ImmutableArray.CreateBuilder<BoundBlockStatement>(guards.Length);

                for (var guardIndex = 0; guardIndex < guards.Length; guardIndex++)
                {
                    var guard = guards[guardIndex];
                    var mapped = ResolveMappedGuardBlock(guard);
                    if (!ReferenceEquals(mapped, guard))
                    {
                        builder.Add(mapped);
                        changed = true;
                    }
                    else
                    {
                        builder.Add(guard);
                    }
                }

                if (changed)
                    _dispatches[i] = new StateDispatch(dispatch.State, dispatch.Label, builder.MoveToImmutable());
            }
        }

        private BoundBlockStatement ResolveMappedGuardBlock(BoundBlockStatement guard)
        {
            var current = guard;

            // Guard blocks can be rewritten more than once (placeholder -> intermediate -> final).
            // Follow the remap chain so dispatch guards always reference the final block instance.
            while (_blockMap.TryGetValue(current, out var mapped) && !ReferenceEquals(mapped, current))
                current = mapped;

            return current;
        }

        private ImmutableArray<BoundBlockStatement> GetCurrentGuardPath()
        {
            if (_tryBlocks.Count == 0)
                return ImmutableArray<BoundBlockStatement>.Empty;

            var guards = _tryBlocks.ToArray();
            Array.Reverse(guards);
            return ImmutableArray.Create(guards);
        }

        private List<SourceFieldSymbol> CollectHoistedDisposables(ImmutableArray<ILocalSymbol> locals)
        {
            var result = new List<SourceFieldSymbol>();
            if (_hoistableLocals.Count == 0 || locals.IsDefaultOrEmpty)
                return result;

            foreach (var local in locals)
            {
                if (!_hoistableLocals.TryGetValue(local, out var requiresDispose) || !requiresDispose)
                    continue;

                if (_hoistedLocals.TryGetValue(local, out var field))
                    result.Add(field);
            }

            return result;
        }

        private static IEnumerable<SourceFieldSymbol> EnumerateReverse(List<SourceFieldSymbol> fields)
        {
            for (var i = fields.Count - 1; i >= 0; i--)
                yield return fields[i];
        }

        public override BoundNode? VisitLocalDeclarationStatement(BoundLocalDeclarationStatement node)
        {
            if (node is null)
                return null;

            var hoistedStatements = new List<BoundStatement>();
            var remainingDeclarators = new List<BoundVariableDeclarator>();

            foreach (var declarator in node.Declarators)
            {
                var initializer = VisitExpression(declarator.Initializer) ?? declarator.Initializer;

                if (_hoistedLocals.TryGetValue(declarator.Local, out var field))
                {
                    if (initializer is not null)
                    {
                        var receiver = new BoundSelfExpression(_stateMachine);
                        var assignment = new BoundFieldAssignmentExpression(
                            receiver,
                            field,
                            initializer,
                            _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit),
                            requiresReceiverAddress: true);
                        hoistedStatements.Add(new BoundAssignmentStatement(assignment));
                    }
                }
                else
                {
                    if (!ReferenceEquals(initializer, declarator.Initializer))
                        remainingDeclarators.Add(new BoundVariableDeclarator(declarator.Local, initializer));
                    else
                        remainingDeclarators.Add(declarator);
                }
            }

            if (remainingDeclarators.Count == 0)
            {
                return hoistedStatements.Count switch
                {
                    0 => new BoundBlockStatement(Array.Empty<BoundStatement>()),
                    1 => hoistedStatements[0],
                    _ => new BoundBlockStatement(hoistedStatements),
                };
            }

            var declaration = new BoundLocalDeclarationStatement(remainingDeclarators, node.IsUsing);
            if (hoistedStatements.Count == 0)
                return declaration;

            hoistedStatements.Add(declaration);
            return new BoundBlockStatement(hoistedStatements);
        }

        public override BoundNode? VisitExpressionStatement(BoundExpressionStatement node)
        {
            if (node is null)
                return null;

            var expression = VisitExpression(node.Expression) ?? node.Expression;

            if (expression is BoundBlockExpression blockExpression)
                return new BoundBlockStatement(blockExpression.Statements.ToArray(), blockExpression.LocalsToDispose);

            if (!ReferenceEquals(expression, node.Expression))
                return new BoundExpressionStatement(expression);

            return node;
        }

        public override BoundNode? VisitMatchStatement(BoundMatchStatement node)
        {
            if (node is null)
                return null;

            var expression = VisitExpression(node.Expression) ?? node.Expression;
            var changed = !ReferenceEquals(expression, node.Expression);

            var rewrittenArms = ImmutableArray.CreateBuilder<BoundMatchArm>(node.Arms.Length);
            foreach (var arm in node.Arms)
            {
                var guard = arm.Guard is null ? null : VisitExpression(arm.Guard) ?? arm.Guard;
                var armExpression = VisitExpression(arm.Expression) ?? arm.Expression;

                if (!ReferenceEquals(guard, arm.Guard) || !ReferenceEquals(armExpression, arm.Expression))
                {
                    changed = true;
                    rewrittenArms.Add(new BoundMatchArm(arm.Pattern, guard, armExpression));
                }
                else
                {
                    rewrittenArms.Add(arm);
                }
            }

            if (!changed)
                return node;

            return new BoundMatchStatement(expression, rewrittenArms.MoveToImmutable());
        }

        public override BoundNode? VisitAssignmentStatement(BoundAssignmentStatement node)
        {
            if (node is null)
                return null;

            var expression = Visit(node.Expression) as BoundAssignmentExpression ?? node.Expression;
            if (!ReferenceEquals(expression, node.Expression))
                return new BoundAssignmentStatement(expression);

            return node;
        }

        public override BoundNode? VisitIfStatement(BoundIfStatement node)
        {
            if (node is null)
                return null;

            var condition = VisitExpression(node.Condition) ?? node.Condition;
            var thenStatement = VisitStatement(node.ThenNode);
            var elseStatement = node.ElseNode is null ? null : VisitStatement(node.ElseNode);

            if (!ReferenceEquals(condition, node.Condition) ||
                !ReferenceEquals(thenStatement, node.ThenNode) ||
                !ReferenceEquals(elseStatement, node.ElseNode))
            {
                return new BoundIfStatement(condition, thenStatement, elseStatement);
            }

            return node;
        }

        public override BoundNode? VisitWhileStatement(BoundWhileStatement node)
        {
            if (node is null)
                return null;

            var condition = VisitExpression(node.Condition) ?? node.Condition;
            var body = VisitStatement(node.Body);

            if (!ReferenceEquals(condition, node.Condition) ||
                !ReferenceEquals(body, node.Body))
            {
                return new BoundWhileStatement(condition, body);
            }

            return node;
        }

        public override BoundNode? VisitLoopStatement(BoundLoopStatement node)
        {
            if (node is null)
                return null;

            var body = VisitStatement(node.Body);

            if (!ReferenceEquals(body, node.Body))
            {
                return new BoundLoopStatement(body);
            }

            return node;
        }

        public override BoundNode? VisitForStatement(BoundForStatement node)
        {
            if (node is null)
                return null;

            var collection = VisitExpression(node.Collection) ?? node.Collection;
            var body = VisitStatement(node.Body);

            if (!ReferenceEquals(collection, node.Collection) ||
                !ReferenceEquals(body, node.Body))
            {
                return new BoundForStatement(node.Local, node.Iteration, collection, body);
            }

            return node;
        }

        public override BoundNode? VisitTryStatement(BoundTryStatement node)
        {
            if (node is null)
                return null;

            _tryBlocks.Push(node.TryBlock);
            var tryBlock = (BoundBlockStatement)VisitBlockStatement(node.TryBlock)!;
            _tryBlocks.Pop();
            var changed = !ReferenceEquals(tryBlock, node.TryBlock);

            var catchClauses = node.CatchClauses;
            ImmutableArray<BoundCatchClause> rewrittenCatchClauses = catchClauses;

            if (!catchClauses.IsDefaultOrEmpty && catchClauses.Length > 0)
            {
                var builder = ImmutableArray.CreateBuilder<BoundCatchClause>(catchClauses.Length);

                foreach (var clause in catchClauses)
                {
                    var rewrittenClause = RewriteCatchClause(clause);
                    builder.Add(rewrittenClause);

                    if (!ReferenceEquals(rewrittenClause, clause))
                        changed = true;
                }

                rewrittenCatchClauses = builder.MoveToImmutable();
            }

            BoundBlockStatement? finallyBlock = null;
            if (node.FinallyBlock is not null)
            {
                finallyBlock = (BoundBlockStatement)VisitBlockStatement(node.FinallyBlock)!;
                if (!ReferenceEquals(finallyBlock, node.FinallyBlock))
                    changed = true;
            }

            if (changed)
                return new BoundTryStatement(tryBlock, rewrittenCatchClauses, finallyBlock, node.Kind);

            return node;
        }

        private BoundCatchClause RewriteCatchClause(BoundCatchClause clause)
        {
            if (clause is null)
                throw new ArgumentNullException(nameof(clause));

            var block = (BoundBlockStatement)VisitBlockStatement(clause.Block)!;
            if (!ReferenceEquals(block, clause.Block))
                return new BoundCatchClause(clause.ExceptionType, clause.Local, clause.Pattern, clause.Guard, block);

            return clause;
        }

        public override BoundNode? VisitReturnStatement(BoundReturnStatement node)
        {
            if (node is null)
                return null;

            var expression = VisitExpression(node.Expression) ?? node.Expression;

            var statements = new List<BoundStatement>();
            ImmutableArray<ILocalSymbol> localsToDispose = ImmutableArray<ILocalSymbol>.Empty;
            BoundExpression? resultExpression = expression;

            if (expression is BoundBlockExpression blockExpression)
            {
                localsToDispose = blockExpression.LocalsToDispose;
                var blockStatements = blockExpression.Statements.ToArray();

                if (blockStatements.Length > 0 &&
                    blockStatements[^1] is BoundExpressionStatement resultStatement)
                {
                    for (var i = 0; i < blockStatements.Length - 1; i++)
                        statements.Add(blockStatements[i]);

                    resultExpression = resultStatement.Expression;
                }
                else
                {
                    statements.AddRange(blockStatements);
                    resultExpression = null;
                }
            }

            if (IsEffectivelyVoidExpression(resultExpression))
                resultExpression = null;

            if (resultExpression is BoundMatchExpression matchExpression)
            {
                var resultType = matchExpression.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
                var resultLocal = new SourceLocalSymbol(
                    "$matchReturnResult",
                    SubstituteStateMachineTypeParameters(resultType),
                    isMutable: true,
                    _stateMachine.MoveNextMethod,
                    _stateMachine,
                    _stateMachine.ContainingNamespace,
                    new[] { Location.None },
                    Array.Empty<SyntaxReference>());

                statements.Add(new BoundLocalDeclarationStatement(new[]
                {
                    new BoundVariableDeclarator(resultLocal, matchExpression)
                }));
                resultExpression = new BoundLocalAccess(resultLocal);
            }

            // Complex return expressions can lower into control-flow-heavy argument emission.
            // Materialize into a temp local before builder.SetResult to keep IL stack shape stable.
            if (resultExpression is not null and not BoundLocalAccess)
            {
                var resultType = resultExpression.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
                var resultLocal = new SourceLocalSymbol(
                    "$asyncReturnResult",
                    SubstituteStateMachineTypeParameters(resultType),
                    isMutable: true,
                    _stateMachine.MoveNextMethod,
                    _stateMachine,
                    _stateMachine.ContainingNamespace,
                    new[] { Location.None },
                    Array.Empty<SyntaxReference>());

                statements.Add(new BoundLocalDeclarationStatement(new[]
                {
                    new BoundVariableDeclarator(resultLocal, resultExpression)
                }));
                resultExpression = new BoundLocalAccess(resultLocal);
            }

            if (CompletionLabel is { } completionLabel)
            {
                if (CompletionResult is { } completionResult && resultExpression is not null)
                {
                    statements.Add(new BoundAssignmentStatement(new BoundLocalAssignmentExpression(
                        completionResult,
                        new BoundLocalAccess(completionResult),
                        resultExpression,
                        _stateMachine.Compilation.UnitTypeSymbol)));
                }
                // Leave source scopes and exception handlers before disposing resources
                // and publishing completion to callers awaiting the task.
                statements.Add(new BoundGotoStatement(completionLabel));
                return new BoundBlockStatement(statements, localsToDispose);
            }

            statements.Add(CreateStateAssignment(_stateMachine, -2));

            var setResult = CreateBuilderSetResultStatement(_stateMachine, _builderMembers, resultExpression);
            if (setResult is not null)
                statements.Add(setResult);

            statements.Add(new BoundReturnStatement(null));

            if (!localsToDispose.IsDefaultOrEmpty && localsToDispose.Length > 0)
                return new BoundBlockStatement(statements, localsToDispose);

            return new BoundBlockStatement(statements);
        }

        public override BoundNode? VisitAwaitExpression(BoundAwaitExpression node)
        {
            if (node is null)
                return null;

            var expression = VisitExpression(node.Expression) ?? node.Expression;
            expression = AsyncMethodExpressionSubstituter.Substitute(_stateMachine, expression);

            var resultType = SubstituteStateMachineTypeParameters(node.ResultType);
            var awaiterType = SubstituteStateMachineTypeParameters(node.AwaiterType);
            var getAwaiter = SubstituteStateMachineTypeParameters(node.GetAwaiterMethod);
            var getResult = SubstituteStateMachineTypeParameters(node.GetResultMethod);
            var isCompleted = SubstituteStateMachineTypeParameters(node.IsCompletedProperty);

            if (!ReferenceEquals(expression, node.Expression) ||
                !SymbolEqualityComparer.Default.Equals(resultType, node.ResultType) ||
                !SymbolEqualityComparer.Default.Equals(awaiterType, node.AwaiterType) ||
                !SymbolEqualityComparer.Default.Equals(getAwaiter, node.GetAwaiterMethod) ||
                !SymbolEqualityComparer.Default.Equals(getResult, node.GetResultMethod) ||
                !SymbolEqualityComparer.Default.Equals(isCompleted, node.IsCompletedProperty))
            {
                node = new BoundAwaitExpression(
                    expression,
                    resultType,
                    awaiterType,
                    getAwaiter,
                    getResult,
                    isCompleted);
            }

            return LowerAwaitExpressionToBlock(node);
        }

        public override BoundNode? VisitTryExpression(BoundTryExpression node)
        {
            if (node is null)
                return null;

            var dispatchCount = _dispatches.Count;
            var guardPlaceholder = new BoundBlockStatement(Array.Empty<BoundStatement>());
            _tryBlocks.Push(guardPlaceholder);

            var expression = VisitExpression(node.Expression) ?? node.Expression;

            _tryBlocks.Pop();

            // Match and other expression lowering can hide an await from a
            // shape-based pre-scan. The authoritative signal is whether visiting
            // this try operand actually introduced a suspension state.
            var containsAwait = _dispatches.Count > dispatchCount;

            if (containsAwait)
            {
                var compilation = _stateMachine.Compilation;
                var resultType = SubstituteStateMachineTypeParameters(node.Type ?? compilation.ErrorTypeSymbol);
                var unitType = compilation.GetSpecialType(SpecialType.System_Unit);
                var okConstructor = SubstituteStateMachineTypeParameters(node.OkConstructor);
                var errorConstructor = SubstituteStateMachineTypeParameters(node.ErrorConstructor);

                var tryStatements = new List<BoundStatement>();

                // Await lowering can produce a block-expression that contains resume labels.
                // Keep those labels in statement flow inside this guarded try region so
                // state dispatch never needs to jump into expression-only control flow.
                if (expression is BoundBlockExpression blockExpression)
                {
                    var spilledStatements = blockExpression.Statements.ToList();
                    BoundExpression blockValueExpression;

                    if (spilledStatements.Count > 0 && spilledStatements[^1] is BoundExpressionStatement tailExpression)
                    {
                        blockValueExpression = tailExpression.Expression;
                        spilledStatements.RemoveAt(spilledStatements.Count - 1);
                    }
                    else
                    {
                        blockValueExpression = new BoundUnitExpression(unitType);
                    }

                    if (spilledStatements.Count > 0)
                        tryStatements.AddRange(spilledStatements);

                    expression = blockValueExpression;
                }

                // If the try-expression already produces the enclosing Result type (common for `try? await ...` where
                // the awaited expression returns Result<TOk, TErr>), do NOT wrap it in an Ok-case. Just pass it through.
                // We still need the try/catch here to convert *thrown* exceptions (from await) into the Error-case.
                var expressionType = expression.Type ?? compilation.ErrorTypeSymbol;

                BoundExpression convertedExpression;

                if (SymbolEqualityComparer.Default.Equals(expressionType, resultType))
                {
                    convertedExpression = ApplyConversionIfNeeded(expression, resultType, compilation);
                }
                else
                {
                    BoundExpression okCreation;
                    if (okConstructor.Parameters.Length == 0)
                    {
                        tryStatements.Add(new BoundExpressionStatement(expression));
                        okCreation = new BoundObjectCreationExpression(okConstructor, ImmutableArray<BoundExpression>.Empty);
                    }
                    else
                    {
                        var okArgument = ApplyConversionIfNeeded(expression, okConstructor.Parameters[0].Type, compilation);
                        okCreation = new BoundObjectCreationExpression(okConstructor, ImmutableArray.Create(okArgument));
                    }

                    convertedExpression = ApplyConversionIfNeeded(okCreation, resultType, compilation);
                }

                var resultLocal = new SourceLocalSymbol(
                    "$tryExprResult",
                    SubstituteStateMachineTypeParameters(resultType),
                    isMutable: true,
                    _stateMachine.MoveNextMethod,
                    _stateMachine,
                    _stateMachine.ContainingNamespace,
                    new[] { Location.None },
                    Array.Empty<SyntaxReference>());

                var resultDeclarator = new BoundVariableDeclarator(resultLocal, initializer: null);
                var resultDeclaration = new BoundLocalDeclarationStatement(new[] { resultDeclarator });

                var localAccess = new BoundLocalAccess(resultLocal);

                var assignment = new BoundLocalAssignmentExpression(resultLocal, localAccess, convertedExpression, unitType);
                tryStatements.Add(new BoundExpressionStatement(assignment));
                var tryBlock = new BoundBlockStatement(tryStatements);

                _blockMap[guardPlaceholder] = tryBlock;

                var exceptionLocal = new SourceLocalSymbol(
                    "$tryExprException",
                    SubstituteStateMachineTypeParameters(node.ExceptionType),
                    isMutable: true,
                    _stateMachine.MoveNextMethod,
                    _stateMachine,
                    _stateMachine.ContainingNamespace,
                    new[] { Location.None },
                    Array.Empty<SyntaxReference>());

                BoundExpression errorCreation;
                if (errorConstructor.Parameters.Length == 0)
                {
                    errorCreation = new BoundObjectCreationExpression(errorConstructor, ImmutableArray<BoundExpression>.Empty);
                }
                else
                {
                    var errorArgument = ApplyConversionIfNeeded(new BoundLocalAccess(exceptionLocal), errorConstructor.Parameters[0].Type, compilation);
                    errorCreation = new BoundObjectCreationExpression(errorConstructor, ImmutableArray.Create(errorArgument));
                }

                var catchExpression = ApplyConversionIfNeeded(
                    errorCreation,
                    resultType,
                    compilation);

                var resultLocalAccess = new BoundLocalAccess(resultLocal);

                var catchAssignment = new BoundLocalAssignmentExpression(
                    resultLocal,
                    resultLocalAccess,
                    catchExpression,
                    unitType);

                var catchBlock = new BoundBlockStatement(new BoundStatement[]
                {
                    new BoundExpressionStatement(catchAssignment)
                });

                var catchClause = new BoundCatchClause(node.ExceptionType, exceptionLocal, pattern: null, guard: null, catchBlock);
                var tryStatement = new BoundTryStatement(
                    tryBlock,
                    ImmutableArray.Create(catchClause),
                    finallyBlock: null,
                    BoundTryStatementKind.ExceptionProjection);

                var blockStatements = new BoundStatement[]
                {
                    resultDeclaration,
                    tryStatement,
                    new BoundExpressionStatement(new BoundLocalAccess(resultLocal))
                };

                return new BoundBlockExpression(blockStatements, resultType);
            }

            if (!ReferenceEquals(expression, node.Expression))
            {
                var type = node.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
                return new BoundTryExpression(expression, node.ExceptionType, type, node.OkConstructor, node.ErrorConstructor);
            }

            return node;
        }

        public override BoundExpression? VisitExpression(BoundExpression? node)
        {
            if (node is null)
                return null;

            switch (node)
            {
                case BoundAwaitExpression awaitExpression:
                    return (BoundExpression?)VisitAwaitExpression(awaitExpression);

                case BoundCarrierConditionalAccessExpression carrierConditionalAccessExpression:
                    {
                        // Keep object identity stable for dispatch/guard mapping in the async
                        // rewriter; carrier symbols are already rewritten by node-specific visits.
                        var visited = (BoundExpression?)base.VisitCarrierConditionalAccessExpression(carrierConditionalAccessExpression)
                                      ?? carrierConditionalAccessExpression;

                        return visited;
                    }

                case BoundPropagateExpression propagateExpression:
                    {
                        // Keep object identity stable for dispatch/guard mapping in the async
                        // rewriter; propagate symbols are rewritten by node-specific visits.
                        var visited = (BoundExpression?)base.VisitPropagateExpression(propagateExpression)
                                      ?? propagateExpression;

                        return visited;
                    }

                case BoundBinaryExpression binaryExpression:
                    {
                        var left = VisitExpression(binaryExpression.Left) ?? binaryExpression.Left;
                        var right = VisitExpression(binaryExpression.Right) ?? binaryExpression.Right;

                        if (!ReferenceEquals(left, binaryExpression.Left) || !ReferenceEquals(right, binaryExpression.Right))
                        {
                            var rewritten = new BoundBinaryExpression(left, binaryExpression.Operator, right);
                            return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, rewritten);
                        }

                        return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, binaryExpression);
                    }

                case BoundUnaryExpression unaryExpression:
                    {
                        var operand = VisitExpression(unaryExpression.Operand) ?? unaryExpression.Operand;
                        if (!ReferenceEquals(operand, unaryExpression.Operand))
                        {
                            var rewritten = new BoundUnaryExpression(unaryExpression.Operator, operand);
                            return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, rewritten);
                        }

                        return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, unaryExpression);
                    }

                case BoundInvocationExpression invocationExpression:
                    {
                        var receiver = VisitExpression(invocationExpression.Receiver) ?? invocationExpression.Receiver;
                        var extensionReceiver = invocationExpression.ExtensionReceiver is null
                            ? null
                            : VisitExpression(invocationExpression.ExtensionReceiver) ?? invocationExpression.ExtensionReceiver;

                        var originalArguments = invocationExpression.Arguments.ToArray();
                        var rewrittenArguments = new BoundExpression[originalArguments.Length];
                        var changed = !ReferenceEquals(receiver, invocationExpression.Receiver) ||
                            !ReferenceEquals(extensionReceiver, invocationExpression.ExtensionReceiver);

                        for (var i = 0; i < originalArguments.Length; i++)
                        {
                            var rewritten = VisitExpression(originalArguments[i]) ?? originalArguments[i];
                            rewrittenArguments[i] = rewritten;
                            if (!ReferenceEquals(rewritten, originalArguments[i]))
                                changed = true;
                        }

                        if (changed)
                        {
                            var normalized = NormalizeInvocationForLowering(
                                invocationExpression.Method,
                                rewrittenArguments,
                                receiver,
                                extensionReceiver,
                                invocationExpression.RequiresReceiverAddress);

                            return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, normalized);
                        }

                        var stable = NormalizeInvocationForLowering(
                            invocationExpression.Method,
                            originalArguments,
                            invocationExpression.Receiver,
                            invocationExpression.ExtensionReceiver,
                            invocationExpression.RequiresReceiverAddress);

                        return AsyncMethodExpressionSubstituter.Substitute(_stateMachine, stable);
                    }

                case BoundObjectCreationExpression objectCreationExpression:
                    {
                        var receiver = VisitExpression(objectCreationExpression.Receiver) ?? objectCreationExpression.Receiver;
                        var originalArguments = objectCreationExpression.Arguments.ToArray();
                        var rewrittenArguments = new BoundExpression[originalArguments.Length];
                        var changed = !ReferenceEquals(receiver, objectCreationExpression.Receiver);

                        for (var i = 0; i < originalArguments.Length; i++)
                        {
                            var rewritten = VisitExpression(originalArguments[i]) ?? originalArguments[i];
                            rewrittenArguments[i] = rewritten;
                            if (!ReferenceEquals(rewritten, originalArguments[i]))
                                changed = true;
                        }

                        if (changed)
                            return new BoundObjectCreationExpression(objectCreationExpression.Constructor, rewrittenArguments, receiver);

                        return objectCreationExpression;
                    }

                case BoundConversionExpression conversionExpression:
                    {
                        var operand = VisitExpression(conversionExpression.Expression) ?? conversionExpression.Expression;
                        if (!ReferenceEquals(operand, conversionExpression.Expression))
                            return new BoundConversionExpression(operand, conversionExpression.Type, conversionExpression.Conversion);

                        return conversionExpression;
                    }

                case BoundAsExpression asExpression:
                    {
                        var operand = VisitExpression(asExpression.Expression) ?? asExpression.Expression;
                        if (!ReferenceEquals(operand, asExpression.Expression))
                            return new BoundAsExpression(operand, asExpression.Type, asExpression.Conversion);

                        return asExpression;
                    }

                case BoundParenthesizedExpression parenthesizedExpression:
                    {
                        var inner = VisitExpression(parenthesizedExpression.Expression) ?? parenthesizedExpression.Expression;
                        if (!ReferenceEquals(inner, parenthesizedExpression.Expression))
                            return new BoundParenthesizedExpression(inner);

                        return parenthesizedExpression;
                    }

                case BoundConditionalAccessExpression conditionalAccessExpression:
                    {
                        var receiver = VisitExpression(conditionalAccessExpression.Receiver) ?? conditionalAccessExpression.Receiver;
                        var whenNotNull = VisitExpression(conditionalAccessExpression.WhenNotNull) ?? conditionalAccessExpression.WhenNotNull;

                        if (!ReferenceEquals(receiver, conditionalAccessExpression.Receiver) ||
                            !ReferenceEquals(whenNotNull, conditionalAccessExpression.WhenNotNull))
                        {
                            return new BoundConditionalAccessExpression(receiver, whenNotNull, conditionalAccessExpression.Type);
                        }

                        return conditionalAccessExpression;
                    }

                case BoundArrayAccessExpression arrayAccessExpression:
                    {
                        var receiver = VisitExpression(arrayAccessExpression.Receiver) ?? arrayAccessExpression.Receiver;
                        var originalIndices = arrayAccessExpression.Indices.ToArray();
                        var rewrittenIndices = new BoundExpression[originalIndices.Length];
                        var changed = !ReferenceEquals(receiver, arrayAccessExpression.Receiver);

                        for (var i = 0; i < originalIndices.Length; i++)
                        {
                            var rewritten = VisitExpression(originalIndices[i]) ?? originalIndices[i];
                            rewrittenIndices[i] = rewritten;
                            if (!ReferenceEquals(rewritten, originalIndices[i]))
                                changed = true;
                        }

                        if (changed)
                            return new BoundArrayAccessExpression(receiver, rewrittenIndices, arrayAccessExpression.ElementType);

                        return arrayAccessExpression;
                    }

                case BoundIfExpression ifExpression:
                    {
                        var condition = VisitExpression(ifExpression.Condition) ?? ifExpression.Condition;
                        var thenBranch = VisitExpression(ifExpression.ThenBranch) ?? ifExpression.ThenBranch;
                        var elseBranch = VisitExpression(ifExpression.ElseBranch) ?? ifExpression.ElseBranch;

                        if (!ReferenceEquals(condition, ifExpression.Condition) ||
                            !ReferenceEquals(thenBranch, ifExpression.ThenBranch) ||
                            !ReferenceEquals(elseBranch, ifExpression.ElseBranch))
                        {
                            return new BoundIfExpression(condition, thenBranch, elseBranch);
                        }

                        return ifExpression;
                    }

                case BoundTupleExpression tupleExpression:
                    {
                        var originalElements = tupleExpression.Elements.ToArray();
                        var rewrittenElements = new BoundExpression[originalElements.Length];
                        var changed = false;

                        for (var i = 0; i < originalElements.Length; i++)
                        {
                            var rewritten = VisitExpression(originalElements[i]) ?? originalElements[i];
                            rewrittenElements[i] = rewritten;
                            if (!ReferenceEquals(rewritten, originalElements[i]))
                                changed = true;
                        }

                        if (changed)
                            return new BoundTupleExpression(rewrittenElements, tupleExpression.Type);

                        return tupleExpression;
                    }

                case BoundFunctionExpression:
                    // Nested lambdas are lowered separately; avoid rewriting them with the enclosing
                    // async state machine's builder/awaiter fields.
                    return node;

                case BoundCollectionExpression collectionExpression:
                    {
                        var originalElements = collectionExpression.Elements.ToArray();
                        var rewrittenElements = new BoundExpression[originalElements.Length];
                        var changed = false;

                        for (var i = 0; i < originalElements.Length; i++)
                        {
                            var rewritten = VisitExpression(originalElements[i]) ?? originalElements[i];
                            rewrittenElements[i] = rewritten;
                            if (!ReferenceEquals(rewritten, originalElements[i]))
                                changed = true;
                        }

                        if (changed)
                            return new BoundCollectionExpression(collectionExpression.Type!, rewrittenElements, collectionExpression.CollectionSymbol, collectionExpression.Reason);

                        return collectionExpression;
                    }

                case BoundDictionaryExpression dictionaryExpression:
                    {
                        var originalElements = dictionaryExpression.Elements.ToArray();
                        var rewrittenElements = new DictionaryElementBinding[originalElements.Length];
                        var changed = false;

                        for (var i = 0; i < originalElements.Length; i++)
                        {
                            var originalElement = originalElements[i];
                            switch (originalElement)
                            {
                                case DictionaryEntryBinding originalEntry:
                                    var rewrittenKey = VisitExpression(originalEntry.Key) ?? originalEntry.Key;
                                    var rewrittenValue = VisitExpression(originalEntry.Value) ?? originalEntry.Value;
                                    rewrittenElements[i] = new DictionaryEntryBinding(rewrittenKey, rewrittenValue);

                                    if (!ReferenceEquals(rewrittenKey, originalEntry.Key) ||
                                        !ReferenceEquals(rewrittenValue, originalEntry.Value))
                                    {
                                        changed = true;
                                    }

                                    break;
                                case DictionarySpreadBinding originalSpread:
                                    var rewrittenExpression = VisitExpression(originalSpread.Expression) ?? originalSpread.Expression;
                                    rewrittenElements[i] = new DictionarySpreadBinding(rewrittenExpression);
                                    if (!ReferenceEquals(rewrittenExpression, originalSpread.Expression))
                                        changed = true;
                                    break;
                                case DictionaryComprehensionBinding originalComprehension:
                                    var rewrittenSource = VisitExpression(originalComprehension.Source) ?? originalComprehension.Source;
                                    var rewrittenCondition = originalComprehension.Condition is null
                                        ? null
                                        : VisitExpression(originalComprehension.Condition) ?? originalComprehension.Condition;
                                    var rewrittenKeySelector = VisitExpression(originalComprehension.KeySelector) ?? originalComprehension.KeySelector;
                                    var rewrittenValueSelector = VisitExpression(originalComprehension.ValueSelector) ?? originalComprehension.ValueSelector;
                                    rewrittenElements[i] = new DictionaryComprehensionBinding(
                                        rewrittenSource,
                                        originalComprehension.IterationLocal,
                                        rewrittenCondition,
                                        rewrittenKeySelector,
                                        rewrittenValueSelector,
                                        originalComprehension.KeyType,
                                        originalComprehension.ValueType);

                                    if (!ReferenceEquals(rewrittenSource, originalComprehension.Source) ||
                                        !ReferenceEquals(rewrittenCondition, originalComprehension.Condition) ||
                                        !ReferenceEquals(rewrittenKeySelector, originalComprehension.KeySelector) ||
                                        !ReferenceEquals(rewrittenValueSelector, originalComprehension.ValueSelector))
                                    {
                                        changed = true;
                                    }

                                    break;
                                default:
                                    rewrittenElements[i] = originalElement;
                                    break;
                            }
                        }

                        if (changed)
                        {
                            return new BoundDictionaryExpression(
                                dictionaryExpression.Type!,
                                rewrittenElements,
                                dictionaryExpression.CollectionSymbol,
                                dictionaryExpression.Reason);
                        }

                        return dictionaryExpression;
                    }

                case BoundBlockExpression blockExpression:
                    {
                        var statements = new List<BoundStatement>();
                        var changed = false;

                        foreach (var statement in blockExpression.Statements)
                        {
                            var rewritten = VisitStatement(statement);
                            statements.Add(rewritten);
                            if (!ReferenceEquals(rewritten, statement))
                                changed = true;
                        }

                        if (changed)
                            return new BoundBlockExpression(statements, blockExpression.UnitType, blockExpression.LocalsToDispose);

                        return blockExpression;
                    }

                case BoundIsPatternExpression isPattern:
                    {
                        var expr = VisitExpression(isPattern.Expression) ?? isPattern.Expression;
                        var pat = VisitPattern(isPattern.Pattern);

                        if (!ReferenceEquals(expr, isPattern.Expression) ||
                            !ReferenceEquals(pat, isPattern.Pattern))
                        {
                            return new BoundIsPatternExpression(expr, pat, isPattern.BooleanType, isPattern.Reason);
                        }

                        return isPattern;
                    }
            }

            return base.VisitExpression(node);
        }

        public override BoundNode? VisitLocalAccess(BoundLocalAccess node)
        {
            if (node is null)
                return null;

            if (_hoistedLocals.TryGetValue(node.Local, out var field))
                return new BoundFieldAccess(field, node.Reason);

            return node;
        }

        public override BoundNode? VisitAddressOfExpression(BoundAddressOfExpression node)
        {
            if (node is null)
                return null;

            if (node.Symbol is ILocalSymbol local && _hoistedLocals.TryGetValue(local, out var field))
            {
                var stateReceiver = new BoundSelfExpression(_stateMachine);
                var fieldAccess = new BoundMemberAccessExpression(stateReceiver, field);
                var fieldType = field.Type ?? node.ValueType;
                return new BoundAddressOfExpression(field, fieldType, stateReceiver, fieldAccess);
            }

            var receiver = VisitExpression(node.Receiver) ?? node.Receiver;
            var storage = VisitExpression(node.Storage) ?? node.Storage;

            if (!ReferenceEquals(receiver, node.Receiver) || !ReferenceEquals(storage, node.Storage))
                return new BoundAddressOfExpression(node.Symbol, node.ValueType, receiver, storage);

            return node;
        }

        public override BoundNode? VisitParameterAccess(BoundParameterAccess node)
        {
            if (node is null)
                return null;

            if (TryGetParameterField(node.Parameter, out var field))
                return new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), field, node.Reason);

            return node;
        }

        public override BoundExpression? VisitSelfExpression(BoundSelfExpression node)
        {
            if (node is null)
                return null;

            // Receivers introduced by async lowering already refer to the synthesized
            // state machine. Only the original method receiver should be redirected
            // through the hoisted `_this` field.
            if (SymbolEqualityComparer.Default.Equals(node.Type, _stateMachine))
                return node;

            // Async instance methods execute inside MoveNext on the synthesized state-machine struct.
            // Rebind original method `self`/`this` references through the hoisted `_this` field.
            if (_stateMachine.ThisField is IFieldSymbol thisField)
                return new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), thisField);

            return node;
        }

        private bool TryGetParameterField(IParameterSymbol parameter, out SourceFieldSymbol field)
        {
            if (_stateMachine.ParameterFieldMap.TryGetValue(parameter, out field))
                return true;

            foreach (var asyncParameter in _stateMachine.AsyncMethod.Parameters)
            {
                if (!string.Equals(asyncParameter.Name, parameter.Name, StringComparison.Ordinal))
                    continue;

                if (!SymbolEqualityComparer.Default.Equals(asyncParameter.Type, parameter.Type))
                    continue;

                if (_stateMachine.ParameterFieldMap.TryGetValue(asyncParameter, out field))
                {
                    return true;
                }
            }

            foreach (var (mappedParameter, mappedField) in _stateMachine.ParameterFieldMap)
            {
                if (!string.Equals(mappedParameter.Name, parameter.Name, StringComparison.Ordinal))
                    continue;

                if (!SymbolEqualityComparer.Default.Equals(mappedParameter.Type, parameter.Type))
                    continue;

                field = mappedField;
                return true;
            }

            field = null!;
            return false;
        }

        public override BoundNode? VisitVariableExpression(BoundVariableExpression node)
        {
            if (node is null)
                return null;

            if (_hoistedLocals.TryGetValue(node.Variable, out var field))
                return new BoundFieldAccess(field, node.Reason);

            return node;
        }

        public override BoundNode DefaultVisit(BoundNode boundNode)
        {
            return boundNode;
        }

        public override BoundNode? VisitLocalAssignmentExpression(BoundLocalAssignmentExpression node)
        {
            if (node is null)
                return null;

            var right = VisitExpression(node.Right) ?? node.Right;

            if (_hoistedLocals.TryGetValue(node.Local, out var field))
                return CreateStateMachineFieldAssignment(_stateMachine, field, right);

            if (!ReferenceEquals(right, node.Right))
            {
                var localAccess = new BoundLocalAccess(node.Local);
                return new BoundLocalAssignmentExpression(node.Local, localAccess, right, _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit));
            }

            return node;
        }

        public override BoundPattern VisitPattern(BoundPattern pattern)
        {
            switch (pattern)
            {
                case BoundGuardedPattern guarded:
                    {
                        var inner = VisitPattern(guarded.Pattern);
                        var guardPattern = guarded.GuardPattern is null ? null : VisitPattern(guarded.GuardPattern);
                        var guardExpression = VisitExpression(guarded.GuardExpression) ?? guarded.GuardExpression;

                        return ReferenceEquals(inner, guarded.Pattern) &&
                            ReferenceEquals(guardPattern, guarded.GuardPattern) &&
                            ReferenceEquals(guardExpression, guarded.GuardExpression)
                            ? guarded
                            : new BoundGuardedPattern(inner, guardPattern, guardExpression, guarded.Reason);
                    }

                case BoundCasePattern c:
                    {
                        var args = c.Arguments;
                        var changed = false;
                        var b = ImmutableArray.CreateBuilder<BoundPattern>(args.Length);

                        for (int i = 0; i < args.Length; i++)
                        {
                            var a = VisitPattern(args[i]);
                            b.Add(a);
                            changed |= !ReferenceEquals(a, args[i]);
                        }

                        if (!changed)
                            return c;

                        return new BoundCasePattern(c.CaseSymbol, c.TryGetMethod, b.ToImmutable(), c.Designator, c.Reason);
                    }

                case BoundUnionMemberPattern unionMember:
                    {
                        var inner = VisitPattern(unionMember.Pattern);
                        return ReferenceEquals(inner, unionMember.Pattern)
                            ? unionMember
                            : new BoundUnionMemberPattern(
                                unionMember.UnionType,
                                unionMember.MemberType,
                                unionMember.TryGetMethod,
                                inner,
                                unionMember.Reason);
                    }

                case BoundNotPattern n:
                    {
                        var inner = VisitPattern(n.Pattern);
                        return ReferenceEquals(inner, n.Pattern) ? n : new BoundNotPattern(inner);
                    }

                case BoundAndPattern a:
                    {
                        var left = VisitPattern(a.Left);
                        var right = VisitPattern(a.Right);
                        return (ReferenceEquals(left, a.Left) && ReferenceEquals(right, a.Right))
                            ? a
                            : new BoundAndPattern(left, right);
                    }

                case BoundOrPattern o:
                    {
                        var left = VisitPattern(o.Left);
                        var right = VisitPattern(o.Right);
                        return (ReferenceEquals(left, o.Left) && ReferenceEquals(right, o.Right))
                            ? o
                            : new BoundOrPattern(left, right);
                    }

                case BoundPositionalPattern t:
                    {
                        var elems = t.Elements;
                        var changed = false;
                        var b = ImmutableArray.CreateBuilder<BoundPattern>(elems.Length);

                        for (int i = 0; i < elems.Length; i++)
                        {
                            var e = VisitPattern(elems[i]);
                            b.Add(e);
                            changed |= !ReferenceEquals(e, elems[i]);
                        }

                        return changed
                            ? new BoundPositionalPattern(
                                t.Type!,
                                b.ToImmutable(),
                                t.Designator,
                                t.Reason,
                                t.RestIndex,
                                t.ElementWidths,
                                t.ElementKinds,
                                t.IsSequence)
                            : t;
                    }

                case BoundDeconstructPattern deconstruct:
                    {
                        var arguments = deconstruct.Arguments;
                        var rewrittenArguments = ImmutableArray.CreateBuilder<BoundPattern>(arguments.Length);
                        var changed = false;

                        foreach (var argument in arguments)
                        {
                            var rewritten = VisitPattern(argument);
                            rewrittenArguments.Add(rewritten);
                            changed |= !ReferenceEquals(rewritten, argument);
                        }

                        if (!changed)
                            return deconstruct;

                        return new BoundDeconstructPattern(
                            deconstruct.InputType,
                            deconstruct.ReceiverType,
                            deconstruct.NarrowedType,
                            deconstruct.DeconstructMethod,
                            rewrittenArguments.ToImmutable(),
                            deconstruct.Designator,
                            deconstruct.Reason);
                    }

                case BoundConstantPattern c:
                    {
                        // Literal-backed constant pattern needs no rewriting.
                        if (c.Expression is null)
                            return c;

                        // Rewrite the expression so captured locals/parameters are hoisted correctly.
                        var rewrittenExpr = VisitExpression(c.Expression) ?? c.Expression;
                        if (ReferenceEquals(rewrittenExpr, c.Expression))
                            return c;

                        return new BoundConstantPattern(rewrittenExpr, c.Designator, c.Reason);
                    }

                case BoundPropertyPattern p:
                    {
                        var props = p.Properties;
                        var changed = false;
                        var b = ImmutableArray.CreateBuilder<BoundPropertySubpattern>(props.Length);

                        for (int i = 0; i < props.Length; i++)
                        {
                            var sp = props[i];
                            var rewritten = VisitPattern(sp.Pattern);
                            if (!ReferenceEquals(rewritten, sp.Pattern))
                            {
                                changed = true;
                                b.Add(sp with { Pattern = rewritten });
                            }
                            else
                            {
                                b.Add(sp);
                            }
                        }

                        var designator = p.Designator is null ? null : VisitDesignator(p.Designator);

                        return changed
                            ? new BoundPropertyPattern(p.InputType, p.ReceiverType, p.NarrowedType, designator, b.ToImmutable(), p.Reason)
                            : p;
                    }

                case BoundDictionaryPattern d:
                    {
                        var entries = d.Entries;
                        var changed = false;
                        var b = ImmutableArray.CreateBuilder<BoundDictionarySubpattern>(entries.Length);

                        for (int i = 0; i < entries.Length; i++)
                        {
                            var entry = entries[i];
                            var rewrittenKey = VisitExpression(entry.Key) ?? entry.Key;
                            var rewrittenPattern = VisitPattern(entry.Pattern);

                            if (!ReferenceEquals(rewrittenKey, entry.Key) ||
                                !ReferenceEquals(rewrittenPattern, entry.Pattern))
                            {
                                changed = true;
                                b.Add(new BoundDictionarySubpattern(rewrittenKey, rewrittenPattern));
                            }
                            else
                            {
                                b.Add(entry);
                            }
                        }

                        var designator = d.Designator is null ? null : VisitDesignator(d.Designator);

                        return changed || !ReferenceEquals(designator, d.Designator)
                            ? new BoundDictionaryPattern(d.InputType, d.ReceiverType, d.KeyType, d.ValueType, designator, b.ToImmutable(), d.Reason)
                            : d;
                    }

                case BoundComparisonPattern comparison:
                    {
                        var value = VisitExpression(comparison.Value) ?? comparison.Value;
                        return ReferenceEquals(value, comparison.Value)
                            ? comparison
                            : new BoundComparisonPattern(
                                comparison.InputType,
                                comparison.Operator,
                                value,
                                comparison.Reason);
                    }

                case BoundRangePattern range:
                    {
                        var lowerBound = VisitExpression(range.LowerBound) ?? range.LowerBound;
                        var upperBound = VisitExpression(range.UpperBound) ?? range.UpperBound;

                        return ReferenceEquals(lowerBound, range.LowerBound) &&
                            ReferenceEquals(upperBound, range.UpperBound)
                            ? range
                            : new BoundRangePattern(
                                range.Type,
                                lowerBound,
                                upperBound,
                                range.IsUpperExclusive,
                                range.Reason);
                    }

                case BoundDiscardPattern:
                    return pattern;

                case BoundDeclarationPattern d:
                    {
                        var des = VisitDesignator(d.Designator);

                        // Usually DeclaredType doesn't change during async rewriting.
                        // If you ever substitute types, do it here.
                        if (!ReferenceEquals(des, d.Designator))
                            return new BoundDeclarationPattern(d.DeclaredType, des, d.Reason);

                        return d;
                    }

                default:
                    return pattern;
            }
        }

        public override BoundDesignator VisitDesignator(BoundDesignator designator)
        {
            switch (designator)
            {
                case BoundSingleVariableDesignator d:
                    // Typically nothing to rewrite here for async lowering.
                    // But if you ever remap locals, this is the hook.
                    return d;

                case BoundDiscardDesignator dd:
                    return dd;

                default:
                    return designator;
            }
        }

        private BoundExpression LowerAwaitExpressionToBlock(BoundAwaitExpression awaitExpression)
        {
            var statements = new List<BoundStatement>();
            SourceLocalSymbol? resultLocal = null;

            var resultType = awaitExpression.ResultType ?? _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit);
            resultType = SubstituteAsyncMethodTypeParameters(resultType);

            if (resultType.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit)
            {
                resultLocal = CreateAwaitResultLocal(resultType);
                var declarator = new BoundVariableDeclarator(resultLocal, initializer: null);
                statements.Add(new BoundLocalDeclarationStatement(new[] { declarator }));
            }

            statements.AddRange(LowerAwaitExpression(awaitExpression, getResult =>
            {
                if (resultLocal is null)
                    return new BoundStatement[] { new BoundExpressionStatement(getResult) };

                var localAccess = new BoundLocalAccess(resultLocal);

                var assignment = new BoundLocalAssignmentExpression(resultLocal, localAccess, getResult, _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit));
                return new BoundStatement[] { new BoundExpressionStatement(assignment) };
            }));

            BoundExpression resultExpression;
            if (resultLocal is not null)
            {
                resultExpression = new BoundLocalAccess(resultLocal);
            }
            else
            {
                var unitType = _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit);
                resultExpression = new BoundUnitExpression(unitType);
            }

            statements.Add(new BoundExpressionStatement(resultExpression));

            var unit = _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit);
            return new BoundBlockExpression(statements, unit);
        }

        private IEnumerable<BoundStatement> LowerAwaitExpression(
            BoundAwaitExpression awaitExpression,
            Func<BoundInvocationExpression, IEnumerable<BoundStatement>> createResumeStatements)
        {
            var state = _nextState++;
            var awaiterType = SubstituteAsyncMethodTypeParameters(awaitExpression.AwaiterType);
            var awaiterField = _stateMachine.AddHoistedLocal($"<>awaiter{state}", awaiterType);
            var resumeLabel = CreateLabel(_stateMachine, $"state{state}");
            var guardPath = GetCurrentGuardPath();
            _dispatches.Add(new StateDispatch(state, resumeLabel, guardPath));

            var awaiterStore = CreateAwaiterStoreStatement(awaitExpression, awaiterField);
            yield return awaiterStore;

            var condition = CreateIsCompletedAccess(awaitExpression, awaiterField);

            var scheduleStatements = new List<BoundStatement>
            {
                CreateStateAssignment(_stateMachine, state),
                CreateAwaitOnCompletedStatement(awaitExpression, awaiterField),
                new BoundReturnStatement(null)
            };

            var ifStatement = new BoundIfStatement(
                condition,
                new BoundBlockStatement(Array.Empty<BoundStatement>()),
                new BoundBlockStatement(scheduleStatements));

            yield return ifStatement;

            var resumeStatements = new List<BoundStatement>
            {
                CreateStateAssignment(_stateMachine, -1)
            };

            var awaiterLocal = CreateAwaiterLocal(awaiterType);
            var awaiterDeclarator = new BoundVariableDeclarator(awaiterLocal, initializer: null);
            resumeStatements.Add(new BoundLocalDeclarationStatement(new[] { awaiterDeclarator }));

            var awaiterFieldAccess = new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), awaiterField);

            var awaiterLocalAccess = new BoundLocalAccess(awaiterLocal);

            var captureAwaiter = new BoundLocalAssignmentExpression(awaiterLocal, awaiterLocalAccess, awaiterFieldAccess, _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit));
            resumeStatements.Add(new BoundExpressionStatement(captureAwaiter));

            var awaiterAccess = new BoundLocalAccess(awaiterLocal);
            var getResult = CreateGetResultInvocation(awaitExpression, awaiterAccess);
            resumeStatements.AddRange(createResumeStatements(getResult));
            var unitType = _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit);
            var clearAwaiterField = new BoundFieldAssignmentExpression(
                new BoundSelfExpression(_stateMachine),
                awaiterField,
                new BoundDefaultValueExpression(awaiterField.Type),
                unitType,
                requiresReceiverAddress: true);
            resumeStatements.Add(new BoundAssignmentStatement(clearAwaiterField));

            yield return new BoundLabeledStatement(resumeLabel, new BoundBlockStatement(resumeStatements));
        }

        private SourceLocalSymbol CreateAwaitResultLocal(ITypeSymbol type)
        {
            var name = $"<>awaitResult{_nextAwaitResultId++}";
            type = SubstituteAsyncMethodTypeParameters(type);
            return new SourceLocalSymbol(
                name,
                type,
                isMutable: true,
                _stateMachine.MoveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());
        }

        private SourceLocalSymbol CreateAwaiterLocal(ITypeSymbol type)
        {
            var name = $"<>awaiterLocal{_nextAwaiterLocalId++}";
            type = SubstituteAsyncMethodTypeParameters(type);
            return new SourceLocalSymbol(
                name,
                type,
                isMutable: true,
                _stateMachine.MoveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());
        }

        private BoundStatement CreateAwaiterStoreStatement(BoundAwaitExpression awaitExpression, SourceFieldSymbol awaiterField)
        {
            var getAwaiter = new BoundInvocationExpression(
                awaitExpression.GetAwaiterMethod,
                Array.Empty<BoundExpression>(),
                awaitExpression.Expression);

            var receiver = new BoundSelfExpression(_stateMachine);
            var assignment = new BoundFieldAssignmentExpression(
                receiver,
                awaiterField,
                getAwaiter,
                _stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit),
                requiresReceiverAddress: true);
            return new BoundAssignmentStatement(assignment);
        }

        private BoundExpression CreateIsCompletedAccess(BoundAwaitExpression awaitExpression, SourceFieldSymbol awaiterField)
        {
            var awaiterAccess = new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), awaiterField);
            return new BoundMemberAccessExpression(awaiterAccess, awaitExpression.IsCompletedProperty);
        }

        private BoundInvocationExpression CreateGetResultInvocation(BoundAwaitExpression awaitExpression, BoundExpression awaiterReceiver)
        {
            return new BoundInvocationExpression(
                awaitExpression.GetResultMethod,
                Array.Empty<BoundExpression>(),
                awaiterReceiver,
                requiresReceiverAddress: true);
        }

        private BoundStatement CreateAwaitOnCompletedStatement(BoundAwaitExpression awaitExpression, SourceFieldSymbol awaiterField)
        {
            var awaitMethod = _builderMembers.AwaitOnCompleted
                ?? throw new InvalidOperationException("Async builder is missing AwaitOnCompleted/AwaitUnsafeOnCompleted.");

            if (awaitMethod.IsGenericMethod)
            {
                var awaiterType = SubstituteAsyncMethodTypeParameters(awaitExpression.AwaiterType);
                awaitMethod = awaitMethod.Construct(awaiterType, _stateMachine);
            }

            var builderAccess = new BoundMemberAccessExpression(new BoundSelfExpression(_stateMachine), _builderMembers.BuilderField);
            var awaiterAddress = new BoundAddressOfExpression(awaiterField, awaiterField.Type, new BoundSelfExpression(_stateMachine));
            var thisAddress = new BoundAddressOfExpression(_stateMachine, _stateMachine);

            var invocation = new BoundInvocationExpression(
                awaitMethod,
                new BoundExpression[] { awaiterAddress, thisAddress },
                builderAccess,
                requiresReceiverAddress: true);

            return new BoundExpressionStatement(invocation);
        }

        private SourceFieldSymbol AddHoistedLocal(ILocalSymbol local)
        {
            if (_hoistedLocals.TryGetValue(local, out var existing))
                return existing;

            var type = local.Type ?? _stateMachine.Compilation.ErrorTypeSymbol;
            type = SubstituteAsyncMethodTypeParameters(type);
            var fieldName = $"<>local{_nextHoistedLocalId++}";
            var requiresDispose = _hoistableLocals.TryGetValue(local, out var dispose) && dispose;
            var field = _stateMachine.AddHoistedLocal(fieldName, type, requiresDispose, local);
            _hoistedLocals.Add(local, field);
            return field;
        }

        private ITypeSymbol SubstituteAsyncMethodTypeParameters(ITypeSymbol type)
        {
            if (type is null)
                return _stateMachine.Compilation.ErrorTypeSymbol;

            return _stateMachine.SubstituteAsyncMethodTypeParameters(type);
        }

        private ITypeSymbol SubstituteStateMachineTypeParameters(ITypeSymbol type)
        {
            if (type is null)
                return _stateMachine.Compilation.ErrorTypeSymbol;

            return _stateMachine.SubstituteStateMachineTypeParameters(type);
        }

        private IMethodSymbol SubstituteStateMachineTypeParameters(IMethodSymbol method)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            return _stateMachine.SubstituteStateMachineTypeParameters(method);
        }

        private IPropertySymbol SubstituteStateMachineTypeParameters(IPropertySymbol property)
        {
            if (property is null)
                throw new ArgumentNullException(nameof(property));

            return _stateMachine.SubstituteStateMachineTypeParameters(property);
        }

        private static BoundExpression ApplyConversionIfNeeded(
            BoundExpression expression,
            ITypeSymbol targetType,
            Compilation compilation)
        {
            if (targetType is null)
                return expression;

            var sourceType = expression.Type ?? compilation.ErrorTypeSymbol;

            if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                return expression;

            var conversion = compilation.ClassifyConversion(sourceType, targetType);
            if (!conversion.Exists || conversion.IsIdentity)
                return expression;

            return new BoundConversionExpression(expression, targetType, conversion);
        }

        private static bool ContainsAwait(BoundExpression expression)
        {
            var visitor = new AwaitDetector();
            visitor.VisitExpression(expression);
            return visitor.FoundAwait;
        }

        private sealed class AwaitDetector : BoundTreeWalker
        {
            public bool FoundAwait { get; private set; }

            public override void VisitAwaitExpression(BoundAwaitExpression node)
            {
                FoundAwait = true;
            }

            public override void VisitExpression(BoundExpression? node)
            {
                if (FoundAwait || node is null)
                    return;

                if (node is BoundPropagateExpression propagateExpression)
                {
                    VisitExpression(propagateExpression.Operand);
                    return;
                }

                base.VisitExpression(node);
            }

            public override void VisitStatement(BoundStatement? node)
            {
                if (FoundAwait || node is null)
                    return;

                base.VisitStatement(node);
            }
        }

        private ImmutableArray<ILocalSymbol> FilterLocalsToDispose(ImmutableArray<ILocalSymbol> locals)
        {
            if (_hoistableLocals.Count == 0 || locals.IsDefaultOrEmpty)
                return locals;

            var builder = ImmutableArray.CreateBuilder<ILocalSymbol>(locals.Length);
            var removedHoistedLocal = false;

            foreach (var local in locals)
            {
                if (_hoistableLocals.ContainsKey(local))
                {
                    removedHoistedLocal = true;
                    continue;
                }

                builder.Add(local);
            }

            if (!removedHoistedLocal)
                return locals;

            return builder.ToImmutable();
        }
    }

    private sealed class AsyncIteratorReceiverRewriter : BoundTreeRewriter
    {
        private readonly SynthesizedAsyncStateMachineTypeSymbol _asyncStateMachine;
        private readonly SynthesizedIteratorTypeSymbol _iteratorType;
        private readonly IFieldSymbol _iteratorField;

        public AsyncIteratorReceiverRewriter(
            SynthesizedAsyncStateMachineTypeSymbol asyncStateMachine,
            SynthesizedIteratorTypeSymbol iteratorType,
            IFieldSymbol iteratorField)
        {
            _asyncStateMachine = asyncStateMachine;
            _iteratorType = iteratorType;
            _iteratorField = iteratorField;
        }

        public BoundBlockStatement Rewrite(BoundBlockStatement body)
            => (BoundBlockStatement)VisitBlockStatement(body)!;

        public override BoundNode? VisitFieldAssignmentExpression(BoundFieldAssignmentExpression node)
        {
            if (node is not BoundFieldAssignmentExpression fieldAssignment ||
                fieldAssignment.Field.ContainingType is not { } containingType ||
                !SymbolEqualityComparer.Default.Equals(containingType, _iteratorType))
            {
                return base.VisitFieldAssignmentExpression(node);
            }

            var receiver = new BoundMemberAccessExpression(
                new BoundSelfExpression(_asyncStateMachine),
                _iteratorField);
            var right = (BoundExpression)(VisitExpression(fieldAssignment.Right) ?? fieldAssignment.Right);

            return new BoundFieldAssignmentExpression(
                receiver,
                fieldAssignment.Field,
                right,
                fieldAssignment.UnitType);
        }

        public override BoundExpression? VisitSelfExpression(BoundSelfExpression node)
        {
            if (!SymbolEqualityComparer.Default.Equals(node.Type, _iteratorType))
                return (BoundExpression?)base.VisitSelfExpression(node);

            return new BoundMemberAccessExpression(
                new BoundSelfExpression(_asyncStateMachine),
                _iteratorField);
        }
    }

    private static class StateDispatchInjector
    {
        public static BoundBlockStatement Inject(
            BoundBlockStatement body,
            SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        ImmutableArray<StateDispatch> dispatches,
        out ImmutableDictionary<int, ILabelSymbol> guardEntryLabels)
        {
            if (body is null)
                throw new ArgumentNullException(nameof(body));

            guardEntryLabels = ImmutableDictionary<int, ILabelSymbol>.Empty;

            if (dispatches.IsDefaultOrEmpty)
                return body;

            var labelMap = dispatches.ToImmutableDictionary(
                d => (ISymbol)d.Label,
                d => d,
                SymbolEqualityComparer.Default);
            var collector = new DispatchCollector(labelMap);
            var blockDispatches = collector.Collect(body);
            var effectiveDispatches = collector.GetEffectiveDispatches(dispatches);
            var preparedDispatches = PrepareBlockDispatches(
                stateMachine,
                blockDispatches,
                effectiveDispatches,
                out guardEntryLabels);

            if (preparedDispatches.Count == 0)
                return body;

            var rewriter = new DispatchInsertionRewriter(stateMachine, preparedDispatches);
            return (BoundBlockStatement)rewriter.VisitBlockStatement(body)!;
        }

        private static Dictionary<BoundNode, BlockDispatchInfo> PrepareBlockDispatches(
            SynthesizedAsyncStateMachineTypeSymbol stateMachine,
            Dictionary<BoundNode, List<StateDispatch>> blockDispatches,
            ImmutableArray<StateDispatch> dispatches,
            out ImmutableDictionary<int, ILabelSymbol> guardEntryLabels)
        {
            if (blockDispatches is null)
                throw new ArgumentNullException(nameof(blockDispatches));

            var guardBlocks = new HashSet<BoundBlockStatement>(ReferenceEqualityComparer.Instance);

            foreach (var dispatch in dispatches)
            {
                if (!dispatch.HasGuards)
                    continue;

                foreach (var guard in dispatch.GuardPath)
                {
                    guardBlocks.Add(guard);

                    if (!blockDispatches.TryGetValue(guard, out var list))
                    {
                        list = new List<StateDispatch>();
                        blockDispatches[guard] = list;
                    }

                    if (!list.Contains(dispatch))
                        list.Add(dispatch);
                }
            }

            var guardEntryByBlock = new Dictionary<BoundBlockStatement, LabelSymbol>(guardBlocks.Count, ReferenceEqualityComparer.Instance);
            var guardId = 0;

            foreach (var guard in guardBlocks)
                guardEntryByBlock[guard] = CreateLabel(stateMachine, $"guard{guardId++}");

            var result = new Dictionary<BoundNode, BlockDispatchInfo>(blockDispatches.Count, ReferenceEqualityComparer.Instance);

            foreach (var pair in blockDispatches)
            {
                var block = pair.Key;
                var dispatchList = pair.Value
                    .OrderBy(d => d.State)
                    .ToImmutableArray();

                var blockDispatchesBuilder = ImmutableArray.CreateBuilder<BlockDispatch>(dispatchList.Length);
                foreach (var dispatch in dispatchList)
                {
                    var target = ResolveBlockDispatchTarget(dispatch, block, guardEntryByBlock);
                    blockDispatchesBuilder.Add(new BlockDispatch(dispatch.State, target));
                }

                ILabelSymbol? entryLabel = null;
                if (block is BoundBlockStatement blockStatement)
                {
                    if (guardEntryByBlock.TryGetValue(blockStatement, out var guardEntry))
                        entryLabel = guardEntry;
                }

                result[block] = new BlockDispatchInfo(blockDispatchesBuilder.MoveToImmutable(), entryLabel);
            }

            var entryLabelBuilder = ImmutableDictionary.CreateBuilder<int, ILabelSymbol>();
            foreach (var dispatch in dispatches)
            {
                if (!dispatch.HasGuards)
                    continue;

                var firstGuard = dispatch.GuardPath[0];
                if (guardEntryByBlock.TryGetValue(firstGuard, out var entryLabel) &&
                    result.ContainsKey(firstGuard))
                {
                    entryLabelBuilder[dispatch.State] = entryLabel;
                }
            }

            guardEntryLabels = entryLabelBuilder.ToImmutable();
            return result;
        }

        private static ILabelSymbol ResolveBlockDispatchTarget(
            StateDispatch dispatch,
            BoundNode block,
            Dictionary<BoundBlockStatement, LabelSymbol> guardEntryByBlock)
        {
            if (block is BoundBlockStatement blockStatement && dispatch.TryGetGuardIndex(blockStatement, out var guardIndex))
            {
                if (guardIndex < dispatch.GuardPath.Length - 1)
                {
                    var nextGuard = dispatch.GuardPath[guardIndex + 1];
                    if (!guardEntryByBlock.TryGetValue(nextGuard, out var nextEntry))
                        throw new InvalidOperationException("Missing guard entry label for nested protected region.");

                    return nextEntry;
                }

                return dispatch.Label;
            }

            return dispatch.Label;
        }

        private sealed class DispatchCollector : BoundTreeWalker
        {
            private readonly ImmutableDictionary<ISymbol, StateDispatch> _dispatches;
            private readonly Dictionary<BoundNode, List<StateDispatch>> _blockDispatches = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<int, StateDispatch> _effectiveDispatches = new();
            private readonly Stack<BoundNode> _blocks = new();
            private readonly Stack<BoundBlockStatement> _tryBlocks = new();

            public DispatchCollector(ImmutableDictionary<ISymbol, StateDispatch> dispatches)
            {
                _dispatches = dispatches;
            }

            public Dictionary<BoundNode, List<StateDispatch>> Collect(BoundBlockStatement root)
            {
                VisitBlockStatement(root);
                return _blockDispatches;
            }

            public ImmutableArray<StateDispatch> GetEffectiveDispatches(ImmutableArray<StateDispatch> dispatches)
            {
                var builder = ImmutableArray.CreateBuilder<StateDispatch>(dispatches.Length);
                foreach (var dispatch in dispatches)
                {
                    builder.Add(_effectiveDispatches.TryGetValue(dispatch.State, out var effective)
                        ? effective
                        : dispatch);
                }

                return builder.MoveToImmutable();
            }

            public override void VisitBlockStatement(BoundBlockStatement node)
            {
                if (node is null)
                    return;

                _blocks.Push(node);
                foreach (var statement in node.Statements)
                    VisitStatement(statement);
                _blocks.Pop();
            }

            public override void VisitTryStatement(BoundTryStatement node)
            {
                if (node is null)
                    return;

                _tryBlocks.Push(node.TryBlock);
                VisitBlockStatement(node.TryBlock);
                _tryBlocks.Pop();

                foreach (var catchClause in node.CatchClauses)
                    VisitCatchClause(catchClause);

                if (node.FinallyBlock is not null)
                    VisitBlockStatement(node.FinallyBlock);
            }

            public override void VisitBlockExpression(BoundBlockExpression node)
            {
                if (node is null)
                    return;

                _blocks.Push(node);

                foreach (var statement in node.Statements)
                    VisitStatement(statement);

                _blocks.Pop();
            }

            public override void VisitLabeledStatement(BoundLabeledStatement node)
            {
                if (node is null)
                    return;

                if (_dispatches.TryGetValue(node.Label, out var dispatch) &&
                    _blocks.TryPeek(out var block))
                {
                    if (_tryBlocks.Count > 0)
                    {
                        var guards = _tryBlocks.ToArray();
                        Array.Reverse(guards);
                        dispatch = new StateDispatch(
                            dispatch.State,
                            dispatch.Label,
                            ImmutableArray.Create(guards));
                    }

                    _effectiveDispatches[dispatch.State] = dispatch;

                    if (!_blockDispatches.TryGetValue(block, out var list))
                    {
                        list = new List<StateDispatch>();
                        _blockDispatches[block] = list;
                    }

                    list.Add(dispatch);
                }

                base.VisitLabeledStatement(node);
            }
        }

        private sealed class DispatchInsertionRewriter : BoundTreeRewriter
        {
            private readonly SynthesizedAsyncStateMachineTypeSymbol _stateMachine;
            private readonly Dictionary<BoundNode, BlockDispatchInfo> _blockDispatches;

            public DispatchInsertionRewriter(
                SynthesizedAsyncStateMachineTypeSymbol stateMachine,
                Dictionary<BoundNode, BlockDispatchInfo> blockDispatches)
            {
                _stateMachine = stateMachine;
                _blockDispatches = blockDispatches;
            }

            public override BoundNode DefaultVisit(BoundNode boundNode)
            {
                return boundNode;
            }

            public override BoundNode? VisitBlockStatement(BoundBlockStatement node)
            {
                if (node is null)
                    return null;

                var statements = new List<BoundStatement>();
                foreach (var statement in node.Statements)
                    statements.Add((BoundStatement)VisitStatement(statement));

                if (_blockDispatches.TryGetValue(node, out var dispatchInfo) && dispatchInfo.Dispatches.Length > 0)
                {
                    var dispatchStatements = CreateDispatchStatements(dispatchInfo.Dispatches);
                    statements.InsertRange(0, dispatchStatements);
                }

                return new BoundBlockStatement(statements, node.LocalsToDispose);
            }

            public override BoundNode? VisitBlockExpression(BoundBlockExpression node)
            {
                if (node is null)
                    return null;

                var statements = new List<BoundStatement>();
                foreach (var statement in node.Statements)
                    statements.Add((BoundStatement)VisitStatement(statement));

                if (_blockDispatches.TryGetValue(node, out var dispatchInfo) && dispatchInfo.Dispatches.Length > 0)
                {
                    var dispatchStatements = CreateDispatchStatements(dispatchInfo.Dispatches);
                    statements.InsertRange(0, dispatchStatements);
                }

                return new BoundBlockExpression(statements, node.UnitType, node.LocalsToDispose);
            }

            public override BoundNode? VisitTryStatement(BoundTryStatement node)
            {
                if (node is null)
                    return null;

                var entryLabel = _blockDispatches.TryGetValue(node.TryBlock, out var dispatchInfo)
                    ? dispatchInfo.EntryLabel
                    : null;
                var rewritten = (BoundTryStatement)base.VisitTryStatement(node)!;

                return entryLabel is null
                    ? rewritten
                    : new BoundLabeledStatement(entryLabel, rewritten);
            }

            private IEnumerable<BoundStatement> CreateDispatchStatements(ImmutableArray<BlockDispatch> dispatches)
            {
                var stateType = _stateMachine.StateField.Type;
                if (!BoundBinaryOperator.TryLookup(_stateMachine.Compilation, SyntaxKind.EqualsEqualsToken, stateType, stateType, out var equals))
                    throw new InvalidOperationException("Async lowering requires integer equality operator.");

                foreach (var dispatch in dispatches)
                {
                    var stateAccess = new BoundFieldAccess(_stateMachine.StateField);
                    var stateLiteral = new BoundLiteralExpression(
                        BoundLiteralExpressionKind.NumericLiteral,
                        dispatch.State,
                        stateType);

                    var condition = new BoundBinaryExpression(stateAccess, equals, stateLiteral);
                    var gotoStatement = new BoundGotoStatement(dispatch.Target);
                    var gotoBlock = new BoundBlockStatement(new BoundStatement[] { gotoStatement });
                    yield return new BoundIfStatement(condition, gotoBlock);
                }
            }
        }

        private sealed class BlockDispatchInfo
        {
            public BlockDispatchInfo(ImmutableArray<BlockDispatch> dispatches, ILabelSymbol? entryLabel)
            {
                Dispatches = dispatches;
                EntryLabel = entryLabel;
            }

            public ImmutableArray<BlockDispatch> Dispatches { get; }

            public ILabelSymbol? EntryLabel { get; }
        }

        private readonly struct BlockDispatch
        {
            public BlockDispatch(int state, ILabelSymbol target)
            {
                State = state;
                Target = target ?? throw new ArgumentNullException(nameof(target));
            }

            public int State { get; }

            public ILabelSymbol Target { get; }
        }
    }

    private sealed class AwaitCaptureWalker : BoundTreeWalker
    {
        private readonly Stack<Scope> _scopes = new();
        private readonly Dictionary<ILocalSymbol, bool> _hoisted = new(ReferenceEqualityComparer.Instance);
        private readonly List<ILocalSymbol> _declarationOrder = new();
        private readonly HashSet<ILocalSymbol> _declaredLocals = new(ReferenceEqualityComparer.Instance);

        private AwaitCaptureWalker()
        {
        }

        public static ImmutableDictionary<ILocalSymbol, bool> Analyze(BoundNode body)
            => Analyze(body, out _);

        public static ImmutableDictionary<ILocalSymbol, bool> Analyze(BoundNode body, out ImmutableArray<ILocalSymbol> declarationOrder)
        {
            if (body is null)
                throw new ArgumentNullException(nameof(body));

            var walker = new AwaitCaptureWalker();
            switch (body)
            {
                case BoundBlockStatement block:
                    walker.VisitBlockStatement(block);
                    break;
                case BoundExpression expression:
                    walker.VisitExpression(expression);
                    break;
                default:
                    walker.Visit(body);
                    break;
            }

            var builder = ImmutableDictionary.CreateBuilder<ILocalSymbol, bool>(ReferenceEqualityComparer.Instance);
            foreach (var pair in walker._hoisted)
                builder.Add(pair.Key, pair.Value);

            declarationOrder = walker._declarationOrder.Where(walker._hoisted.ContainsKey).ToImmutableArray();
            return builder.ToImmutable();
        }

        public override void VisitBlockStatement(BoundBlockStatement node)
        {
            if (node is null)
                return;

            _scopes.Push(new Scope(node.LocalsToDispose));

            foreach (var statement in node.Statements)
                VisitStatement(statement);

            _scopes.Pop();
        }

        public override void VisitBlockExpression(BoundBlockExpression node)
        {
            if (node is null)
                return;

            _scopes.Push(new Scope(node.LocalsToDispose));

            foreach (var statement in node.Statements)
                VisitStatement(statement);

            _scopes.Pop();
        }

        public override void VisitLocalDeclarationStatement(BoundLocalDeclarationStatement node)
        {
            if (node is null)
                return;

            foreach (var declarator in node.Declarators)
            {
                VisitExpression(declarator.Initializer);
                DeclareLocal(declarator.Local, node.IsUsing);
            }
        }

        public override void VisitIfStatement(BoundIfStatement node)
        {
            if (node is null)
                return;

            VisitExpression(node.Condition);

            if (node.Condition is BoundIsPatternExpression { Pattern: { } pattern })
            {
                _scopes.Push(new Scope(ImmutableArray<ILocalSymbol>.Empty));
                DeclarePatternLocals(pattern);
                VisitStatement(node.ThenNode);
                _scopes.Pop();
            }
            else
            {
                VisitStatement(node.ThenNode);
            }

            if (node.ElseNode is not null)
                VisitStatement(node.ElseNode);
        }

        public override void VisitWhileStatement(BoundWhileStatement node)
        {
            if (node is null)
                return;

            VisitExpression(node.Condition);

            _scopes.Push(new Scope(ImmutableArray<ILocalSymbol>.Empty));
            if (node.Condition is BoundIsPatternExpression { Pattern: { } pattern })
                DeclarePatternLocals(pattern);
            VisitStatement(node.Body);
            _scopes.Pop();
        }

        public override void VisitConditionalGotoStatement(BoundConditionalGotoStatement node)
        {
            if (node is null)
                return;

            VisitExpression(node.Condition);
            if (node.Condition is BoundIsPatternExpression { Pattern: { } pattern })
                DeclarePatternLocals(pattern);
        }

        public override void VisitForStatement(BoundForStatement node)
        {
            if (node is null)
                return;

            VisitExpression(node.Collection);

            _scopes.Push(new Scope(ImmutableArray<ILocalSymbol>.Empty));
            if (node.Local is not null)
                DeclareLocal(node.Local, isUsing: false);
            VisitStatement(node.Body);
            _scopes.Pop();
        }

        public override void VisitCatchClause(BoundCatchClause node)
        {
            if (node is null)
                return;

            _scopes.Push(new Scope(node.Block.LocalsToDispose));

            if (node.Local is not null)
                DeclareLocal(node.Local, isUsing: false);

            foreach (var statement in node.Block.Statements)
                VisitStatement(statement);

            _scopes.Pop();
        }

        public override void VisitAwaitExpression(BoundAwaitExpression node)
        {
            if (node is null)
                return;

            VisitExpression(node.Expression);
            CaptureCurrentLocals();
        }

        public override void VisitFunctionExpression(BoundFunctionExpression node)
        {
            // Lambdas are lowered independently.
        }

        public override void VisitFunctionStatement(BoundFunctionStatement node)
        {
            // Local functions are lowered independently.
        }

        private void DeclareLocal(ILocalSymbol local, bool isUsing)
        {
            if (_declaredLocals.Add(local))
                _declarationOrder.Add(local);

            if (_scopes.Count == 0)
                _scopes.Push(new Scope(ImmutableArray<ILocalSymbol>.Empty));

            var scope = _scopes.Peek();
            scope.Declare(local, isUsing);
        }

        private void DeclarePatternLocals(BoundPattern pattern)
        {
            foreach (var designator in pattern.GetDesignators())
            {
                if (designator is BoundSingleVariableDesignator single)
                    DeclareLocal(single.Local, isUsing: false);
            }
        }

        private void CaptureCurrentLocals()
        {
            foreach (var scope in _scopes)
            {
                foreach (var pair in scope.Locals)
                    Merge(pair.Key, pair.Value);
            }
        }

        private void Merge(ILocalSymbol local, bool requiresDispose)
        {
            if (_hoisted.TryGetValue(local, out var existing))
            {
                if (!existing && requiresDispose)
                    _hoisted[local] = true;
            }
            else
            {
                _hoisted.Add(local, requiresDispose);
            }
        }

        private sealed class Scope
        {
            private readonly Dictionary<ILocalSymbol, bool> _locals = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<ILocalSymbol> _localsToDispose;

            public Scope(IEnumerable<ILocalSymbol> localsToDispose)
            {
                _localsToDispose = new HashSet<ILocalSymbol>(localsToDispose, ReferenceEqualityComparer.Instance);
            }

            public void Declare(ILocalSymbol local, bool isUsing)
            {
                var requiresDispose = isUsing || _localsToDispose.Contains(local);
                _locals[local] = requiresDispose;
            }

            public IEnumerable<KeyValuePair<ILocalSymbol, bool>> Locals => _locals;
        }
    }

    private readonly struct StateDispatch
    {
        public StateDispatch(int state, LabelSymbol label, ImmutableArray<BoundBlockStatement> guardPath)
        {
            State = state;
            Label = label ?? throw new ArgumentNullException(nameof(label));
            GuardPath = guardPath;
        }

        public int State { get; }

        public LabelSymbol Label { get; }

        public ImmutableArray<BoundBlockStatement> GuardPath { get; }

        public bool HasGuards => !GuardPath.IsDefaultOrEmpty && GuardPath.Length > 0;

        public bool TryGetGuardIndex(BoundBlockStatement block, out int index)
        {
            if (block is null || GuardPath.IsDefaultOrEmpty || GuardPath.Length == 0)
            {
                index = -1;
                return false;
            }

            for (var i = 0; i < GuardPath.Length; i++)
            {
                if (ReferenceEquals(GuardPath[i], block))
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }
    }

    private static BoundBlockStatement RewriteAwaitlessAsyncBody(
        Compilation compilation,
        ITypeSymbol asyncReturnType,
        BoundBlockStatement body)
    {
        if (!TryGetAsyncReturnInfo(compilation, asyncReturnType, out var returnInfo))
            return body;

        var rewriter = new AwaitlessAsyncRewriter(compilation, returnInfo);
        return rewriter.RewriteBlock(body);
    }

    private static bool TryGetAsyncReturnInfo(
        Compilation compilation,
        ITypeSymbol asyncReturnType,
        out AwaitlessAsyncReturnInfo returnInfo)
    {
        returnInfo = default;

        if (asyncReturnType is null)
            return false;

        if (compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task) is not INamedTypeSymbol taskType ||
            taskType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var resultType = AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation, asyncReturnType);
        if (resultType is null)
            return false;

        var taskOfT = compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task_T) as INamedTypeSymbol;

        returnInfo = new AwaitlessAsyncReturnInfo(asyncReturnType, resultType, taskType, taskOfT);
        return returnInfo.IsSupported;
    }

    private readonly struct AwaitlessAsyncReturnInfo
    {
        public AwaitlessAsyncReturnInfo(
            ITypeSymbol asyncReturnType,
            ITypeSymbol resultType,
            INamedTypeSymbol taskType,
            INamedTypeSymbol? taskOfT)
        {
            AsyncReturnType = asyncReturnType;
            ResultType = resultType;
            TaskType = taskType;
            TaskOfT = taskOfT;
        }

        public ITypeSymbol AsyncReturnType { get; }

        public ITypeSymbol ResultType { get; }

        public INamedTypeSymbol TaskType { get; }

        public INamedTypeSymbol? TaskOfT { get; }

        public bool IsTask => SymbolEqualityComparer.Default.Equals(AsyncReturnType, TaskType);

        public bool IsTaskOfT =>
            AsyncReturnType is INamedTypeSymbol named &&
            TaskOfT is not null &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, TaskOfT);

        public bool IsValueTask => AsyncReturnTypeUtilities.IsNonGenericValueTask(AsyncReturnType);

        public bool IsSupported => IsTask || IsTaskOfT || IsValueTask;
    }

    private sealed class AwaitlessAsyncRewriter : BoundTreeRewriter
    {
        private readonly Compilation _compilation;
        private readonly AwaitlessAsyncReturnInfo _returnInfo;
        private readonly ITypeSymbol _unitType;
        private readonly BoundExpression? _completedTask;

        public AwaitlessAsyncRewriter(Compilation compilation, AwaitlessAsyncReturnInfo returnInfo)
        {
            _compilation = compilation;
            _returnInfo = returnInfo;
            _unitType = compilation.GetSpecialType(SpecialType.System_Unit);
            _completedTask = CreateCompletedTaskAccess();
        }

        public BoundBlockStatement RewriteBlock(BoundBlockStatement body)
        {
            return (BoundBlockStatement)VisitBlockStatement(body)!;
        }

        public BoundExpression RewriteExpression(BoundExpression body)
        {
            var rewritten = (BoundExpression)VisitExpression(body)!;
            return RewriteReturnExpression(rewritten);
        }

        public BoundExpression RewriteLambdaBody(BoundExpression body)
        {
            var rewritten = (BoundExpression)VisitExpression(body)!;

            // Block-bodied lambdas already have rewritten return statements.
            // Re-wrapping the entire block as a return value introduces an
            // invalid fallback return path for Task<T> lambdas.
            if (rewritten is BoundBlockExpression)
                return rewritten;

            return RewriteReturnExpression(rewritten);
        }

        public override BoundNode? VisitReturnStatement(BoundReturnStatement node)
        {
            var expression = VisitExpression(node.Expression);
            var rewritten = RewriteReturnExpression(expression);
            return new BoundReturnStatement(rewritten);
        }

        public override BoundNode? VisitBlockStatement(BoundBlockStatement node)
        {
            if (node is null)
                return null;

            var statements = new List<BoundStatement>();
            var changed = false;

            foreach (var statement in node.Statements)
            {
                var rewritten = (BoundStatement)VisitStatement(statement);
                changed |= !ReferenceEquals(rewritten, statement);
                statements.Add(rewritten);
            }

            if (!changed)
                return node;

            return new BoundBlockStatement(statements, node.LocalsToDispose);
        }

        public override BoundNode? VisitBlockExpression(BoundBlockExpression node)
        {
            if (node is null)
                return null;

            var statements = new List<BoundStatement>();
            var changed = false;

            foreach (var statement in node.Statements)
            {
                var rewritten = (BoundStatement)VisitStatement(statement);
                changed |= !ReferenceEquals(rewritten, statement);
                statements.Add(rewritten);
            }

            if (!changed)
                return node;

            return new BoundBlockExpression(statements, node.UnitType, node.LocalsToDispose);
        }

        private BoundExpression RewriteReturnExpression(BoundExpression? expression)
        {
            if (expression is not null)
            {
                var converted = TryImplicitlyConvert(expression, _returnInfo.AsyncReturnType);
                if (converted is not null)
                    return converted;
            }

            if (SymbolEqualityComparer.Default.Equals(_returnInfo.ResultType, _unitType))
            {
                var completedTask = _completedTask ?? (expression ?? CreateNullLiteral(_returnInfo.AsyncReturnType));

                if (expression is null || IsEffectivelyVoidExpression(expression))
                    return completedTask;

                return CreateSequencedExpression(expression, completedTask);
            }

            var value = expression ?? CreateDefaultValueExpression(_returnInfo.ResultType);
            if (value is null)
                return expression ?? CreateNullLiteral(_returnInfo.AsyncReturnType);

            var fromResult = CreateFromResultInvocation(value);
            return fromResult ?? expression ?? CreateNullLiteral(_returnInfo.AsyncReturnType);
        }

        private BoundExpression? TryImplicitlyConvert(BoundExpression expression, ITypeSymbol targetType)
        {
            var sourceType = expression.Type ?? _compilation.ErrorTypeSymbol;
            var conversion = _compilation.ClassifyConversion(sourceType, targetType);

            if (!conversion.Exists || !conversion.IsImplicit)
                return null;

            if (conversion.IsIdentity)
                return expression;

            return new BoundConversionExpression(expression, targetType, conversion);
        }

        private BoundExpression CreateSequencedExpression(BoundExpression expression, BoundExpression result)
        {
            var statements = new List<BoundStatement>
            {
                new BoundExpressionStatement(expression),
                new BoundExpressionStatement(result)
            };

            return new BoundBlockExpression(statements, _unitType);
        }

        private BoundExpression? CreateFromResultInvocation(BoundExpression value)
        {
            if (!_returnInfo.IsTaskOfT)
                return null;

            var fromResult = _returnInfo.TaskType
                .GetMembers(nameof(Task.FromResult))
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.TypeParameters.Length == 1 && m.Parameters.Length == 1);

            if (fromResult is null)
                return null;

            var constructed = fromResult.Construct(_returnInfo.ResultType);
            var parameterType = constructed.Parameters[0].Type;
            var argument = ApplyConversionIfNeeded(value, parameterType);
            return new BoundInvocationExpression(constructed, new[] { argument });
        }

        private BoundExpression ApplyConversionIfNeeded(BoundExpression expression, ITypeSymbol targetType)
        {
            var sourceType = expression.Type ?? _compilation.ErrorTypeSymbol;
            var conversion = _compilation.ClassifyConversion(sourceType, targetType);
            if (!conversion.Exists || conversion.IsIdentity)
                return expression;

            return new BoundConversionExpression(expression, targetType, conversion);
        }

        private BoundExpression? CreateCompletedTaskAccess()
        {
            if (_returnInfo.IsValueTask)
                return new BoundDefaultValueExpression(_returnInfo.AsyncReturnType);

            if (!SymbolEqualityComparer.Default.Equals(_returnInfo.ResultType, _unitType))
                return null;

            foreach (var member in _returnInfo.TaskType.GetMembers(nameof(Task.CompletedTask)))
            {
                if (member is IPropertySymbol { IsStatic: true, Type: var type } property &&
                    SymbolEqualityComparer.Default.Equals(type, _returnInfo.TaskType))
                {
                    return new BoundMemberAccessExpression(null, property);
                }

                if (member is IFieldSymbol { IsStatic: true, Type: var fieldType } field &&
                    SymbolEqualityComparer.Default.Equals(fieldType, _returnInfo.TaskType))
                {
                    return new BoundMemberAccessExpression(null, field);
                }
            }

            return null;
        }
    }

    internal readonly struct AsyncMethodAnalysis
    {
        public AsyncMethodAnalysis(bool requiresStateMachine, bool containsAwait)
        {
            RequiresStateMachine = requiresStateMachine;
            ContainsAwait = containsAwait;
        }

        public bool RequiresStateMachine { get; }

        public bool ContainsAwait { get; }
    }

    internal readonly struct AsyncRewriteResult
    {
        public AsyncRewriteResult(
            BoundBlockStatement body,
            SynthesizedAsyncStateMachineTypeSymbol? stateMachine,
            AsyncMethodAnalysis analysis)
        {
            Body = body;
            StateMachine = stateMachine;
            Analysis = analysis;
        }

        public BoundBlockStatement Body { get; }

        public SynthesizedAsyncStateMachineTypeSymbol? StateMachine { get; }

        public AsyncMethodAnalysis Analysis { get; }
    }

    private sealed class AwaitExpressionFinder : BoundTreeWalker
    {
        public bool FoundAwait { get; private set; }

        public override void VisitStatement(BoundStatement statement)
        {
            if (FoundAwait)
                return;

            if (statement is BoundFunctionStatement)
                return;

            base.VisitStatement(statement);
        }

        public override void VisitExpression(BoundExpression node)
        {
            if (FoundAwait)
                return;

            if (node is BoundPropagateExpression propagateExpression)
            {
                VisitExpression(propagateExpression.Operand);
                return;
            }

            base.VisitExpression(node);
        }

        public override void VisitYieldStatement(BoundYieldStatement node)
        {
            FoundAwait |= node.Iteration?.Kind == ForIterationKind.Async;
            base.VisitYieldStatement(node);
        }

        public override void VisitYieldExpression(BoundYieldExpression node)
        {
            FoundAwait |= node.Iteration?.Kind == ForIterationKind.Async;
            base.VisitYieldExpression(node);
        }

        public override void VisitAwaitExpression(BoundAwaitExpression node)
        {
            FoundAwait = true;
        }

        public override void VisitFunctionExpression(BoundFunctionExpression node)
        {
            // Nested lambdas are lowered independently.
        }

        public override void VisitLocalDeclarationStatement(BoundLocalDeclarationStatement node)
        {
            if (FoundAwait)
                return;

            foreach (var declarator in node.Declarators)
            {
                if (declarator.Initializer is not null)
                    VisitExpression(declarator.Initializer);

                if (FoundAwait)
                    break;
            }
        }
    }

    private static BoundStatement? CreateBuilderInitializationStatement(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SourceLocalSymbol asyncLocal,
        SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
        ITypeSymbol unitType)
    {
        var createMethod = builderMembers.Create;
        if (createMethod is null)
            return null;

        var invocation = new BoundInvocationExpression(createMethod, Array.Empty<BoundExpression>());
        var receiver = new BoundLocalAccess(asyncLocal);
        var assignment = new BoundFieldAssignmentExpression(
            receiver,
            builderMembers.BuilderField,
            invocation,
            unitType,
            requiresReceiverAddress: true);
        return new BoundAssignmentStatement(assignment);
    }

    private static BoundStatement? CreateBuilderStartStatement(
        SynthesizedAsyncStateMachineTypeSymbol stateMachine,
        SourceLocalSymbol asyncLocal,
        SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
        INamedTypeSymbol stateMachineType)
    {
        var startMethod = builderMembers.Start;
        if (startMethod is null)
            return null;

        // Generic async kickoff currently routes through runtime generic method projection that can
        // produce invalid member references. Emit direct MoveNext fallback for generic async methods.
        if (stateMachine.AsyncMethod.IsGenericMethod)
            return null;

        var constructedStart = startMethod.IsGenericMethod
            ? startMethod.Construct(stateMachineType)
            : startMethod;

        var builderAccess = new BoundMemberAccessExpression(new BoundLocalAccess(asyncLocal), builderMembers.BuilderField);
        var stateMachineReference = new BoundAddressOfExpression(asyncLocal, stateMachineType);

        var invocation = new BoundInvocationExpression(
            constructedStart,
            new BoundExpression[] { stateMachineReference },
            builderAccess,
            requiresReceiverAddress: true);

        return new BoundExpressionStatement(invocation);
    }

    private static BoundBlockStatement? CreateSetStateMachineBody(SynthesizedAsyncStateMachineTypeSymbol stateMachine)
    {
        var builderMembers = stateMachine.GetBuilderMembers(stateMachine.AsyncMethod);
        var setStateMachineMethod = builderMembers.SetStateMachine;
        if (setStateMachineMethod is null)
            return null;

        setStateMachineMethod = stateMachine.SubstituteAsyncMethodTypeParameters(setStateMachineMethod);

        var builderAccess = new BoundMemberAccessExpression(new BoundSelfExpression(stateMachine), builderMembers.BuilderField);
        var parameter = stateMachine.SetStateMachineMethod.Parameters.Length > 0
            ? stateMachine.SetStateMachineMethod.Parameters[0]
            : null;

        if (parameter is null)
            return null;

        var invocation = new BoundInvocationExpression(
            setStateMachineMethod,
            new BoundExpression[] { new BoundParameterAccess(parameter) },
            builderAccess,
            requiresReceiverAddress: true);

        var assignment = CreateStateAssignment(stateMachine, -1);
        return new BoundBlockStatement(new BoundStatement[]
        {
            assignment,
            new BoundExpressionStatement(invocation)
        });
    }

    private static BoundExpression CreateReturnExpression(
        IMethodSymbol method,
        SynthesizedAsyncStateMachineTypeSymbol.BuilderMembers builderMembers,
        SourceLocalSymbol asyncLocal)
    {
        var taskProperty = builderMembers.TaskProperty;
        if (taskProperty is null)
            return CreateNullLiteral(method.ReturnType);

        var builderAccess = new BoundMemberAccessExpression(new BoundLocalAccess(asyncLocal), builderMembers.BuilderField);
        var taskAccess = new BoundMemberAccessExpression(builderAccess, taskProperty);

        if (!SymbolEqualityComparer.Default.Equals(taskProperty.Type, method.ReturnType))
        {
            var conversion = new Conversion(isImplicit: true, isIdentity: true);
            return new BoundConversionExpression(taskAccess, method.ReturnType, conversion);
        }

        return taskAccess;
    }

    private static BoundLiteralExpression CreateNullLiteral(ITypeSymbol type)
    {
        return new BoundLiteralExpression(
            BoundLiteralExpressionKind.NullLiteral,
            null!,
            type);
    }

    private static Compilation GetCompilation(ISymbol symbol)
    {
        if (symbol?.ContainingAssembly is SourceAssemblySymbol sourceAssembly)
            return sourceAssembly.Compilation;

        throw new InvalidOperationException("Async lowering requires a source assembly symbol.");
    }
}
