using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

internal static class IteratorLowerer
{
    public static bool ShouldRewrite(SourceMethodSymbol method, BoundBlockStatement block)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (block is null)
            throw new ArgumentNullException(nameof(block));

        var finder = new YieldStatementFinder();
        finder.VisitBlockStatement(block);
        return finder.FoundYield;
    }

    public static bool ShouldRewrite(SourceLambdaSymbol lambda, BoundBlockStatement block)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (block is null)
            throw new ArgumentNullException(nameof(block));

        var finder = new YieldStatementFinder();
        finder.VisitBlockStatement(block);
        return finder.FoundYield;
    }

    public static BoundBlockStatement Rewrite(SourceMethodSymbol method, BoundBlockStatement block)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (block is null)
            throw new ArgumentNullException(nameof(block));

        var compilation = GetCompilation(method);

        var signature = DetermineIteratorSignature(compilation, method.ReturnType);
        if (signature.Kind == IteratorMethodKind.None)
            return block;

        method.MarkIterator(signature.Kind, signature.ElementType);

        if (method.IteratorStateMachine is null)
        {
            var stateMachine = compilation.CreateIteratorStateMachine(method, signature.Kind, signature.ElementType);
            method.SetIteratorStateMachine(stateMachine);
        }

        var iteratorType = method.IteratorStateMachine;
        if (iteratorType is null)
            throw new InvalidOperationException("Iterator state machine not created.");

        if (iteratorType.MoveNextBody is null)
        {
            var moveNextBody = CreateMoveNextBody(compilation, iteratorType, block);
            iteratorType.SetMoveNextBody(moveNextBody);
        }

        EnsureIteratorHelpers(compilation, iteratorType);

        return RewriteIteratorMethodBody(compilation, method, iteratorType);
    }

    public static BoundBlockStatement Rewrite(SourceLambdaSymbol lambda, BoundBlockStatement block, ITypeSymbol? selfType = null)
    {
        if (lambda is null)
            throw new ArgumentNullException(nameof(lambda));
        if (block is null)
            throw new ArgumentNullException(nameof(block));

        var compilation = GetCompilation(lambda);

        var signature = DetermineIteratorSignature(compilation, lambda.ReturnType);
        if (signature.Kind == IteratorMethodKind.None)
            return block;

        lambda.MarkIterator(signature.Kind, signature.ElementType);

        if (lambda.IteratorStateMachine is null ||
            (selfType is not null &&
             lambda.IteratorStateMachine.ThisField is { } thisField &&
             !SymbolEqualityComparer.Default.Equals(thisField.Type, selfType)))
        {
            var stateMachine = compilation.CreateIteratorStateMachine(lambda, signature.Kind, signature.ElementType, selfType);
            lambda.SetIteratorStateMachine(stateMachine);
        }

        var iteratorType = lambda.IteratorStateMachine;
        if (iteratorType is null)
            throw new InvalidOperationException("Iterator state machine not created.");

        if (iteratorType.MoveNextBody is null)
        {
            var moveNextBody = CreateMoveNextBody(compilation, iteratorType, block);
            iteratorType.SetMoveNextBody(moveNextBody);
        }

        EnsureIteratorHelpers(compilation, iteratorType);

        return RewriteIteratorMethodBody(compilation, lambda, iteratorType);
    }

    private static BoundBlockStatement CreateMoveNextBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine,
        BoundBlockStatement body)
    {
        body = YieldFromLowerer.Rewrite(stateMachine.IteratorMethod, compilation, body);
        body = AwaitForLowerer.Rewrite(stateMachine.IteratorMethod, body);
        var targetMethod = stateMachine.AsyncMoveNextMethod ?? stateMachine.MoveNextMethod;
        var builder = new MoveNextBuilder(compilation, stateMachine, targetMethod);
        var moveNext = builder.Rewrite(body);

        if (stateMachine.DisposeBody is null && builder.DisposeBody is not null)
            stateMachine.SetDisposeBody(builder.DisposeBody);

        if (stateMachine.IteratorMethod is not SourceLambdaSymbol &&
            stateMachine.AsyncMoveNextMethod is { } asyncMoveNextMethod &&
            AsyncLowerer.ContainsAwait(moveNext))
        {
            var asyncMoveNextBody = AsyncLowerer.Rewrite(asyncMoveNextMethod, moveNext);
            stateMachine.SetAsyncMoveNextBody(asyncMoveNextBody);

            return new BoundBlockStatement(new BoundStatement[]
            {
                new BoundReturnStatement(new BoundLiteralExpression(
                    BoundLiteralExpressionKind.NumericLiteral,
                    0,
                    compilation.GetSpecialType(SpecialType.System_Boolean))),
            });
        }

        return moveNext;
    }

    private static BoundBlockStatement CreateCurrentGetterBody(SynthesizedIteratorTypeSymbol stateMachine)
    {
        var currentAccess = new BoundFieldAccess(stateMachine.CurrentField);
        var returnStatement = new BoundReturnStatement(currentAccess);
        return new BoundBlockStatement(new[] { returnStatement });
    }

    private static BoundBlockStatement CreateNonGenericCurrentGetterBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var currentAccess = new BoundFieldAccess(stateMachine.CurrentField);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var result = ConvertIfNeeded(compilation, currentAccess, objectType);

        var returnStatement = new BoundReturnStatement(result);
        return new BoundBlockStatement(new[] { returnStatement });
    }

    private static BoundBlockStatement CreateDisposeBody(SynthesizedIteratorTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();

        var literal = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            -1,
            stateMachine.StateField.Type);
        var assignment = new BoundFieldAssignmentExpression(
            null,
            stateMachine.StateField,
            literal,
            stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit));
        statements.Add(new BoundAssignmentStatement(assignment));

        statements.Add(new BoundReturnStatement(null));
        return new BoundBlockStatement(statements);
    }

    private static BoundBlockStatement CreateAsyncDisposeBody(SynthesizedIteratorTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();

        if (stateMachine.CombinedTokensField is { } combinedTokensField)
        {
            var combinedTokensAccess = new BoundFieldAccess(combinedTokensField);
            var disposeMethod = (combinedTokensField.Type as INamedTypeSymbol)?
                .GetMembers("Dispose")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 0);

            if (disposeMethod is not null)
            {
                var nullLiteral = new BoundLiteralExpression(
                    BoundLiteralExpressionKind.NullLiteral,
                    null!,
                    combinedTokensField.Type);
                var hasCombinedTokens = CreateBinaryExpression(
                    stateMachine.Compilation,
                    SyntaxKind.NotEqualsToken,
                    combinedTokensAccess,
                    nullLiteral);

                var disposeInvocation = new BoundInvocationExpression(
                    disposeMethod,
                    Array.Empty<BoundExpression>(),
                    receiver: combinedTokensAccess);
                var disposeStatement = new BoundExpressionStatement(disposeInvocation);

                statements.Add(new BoundIfStatement(hasCombinedTokens, disposeStatement));
            }
        }

        var literal = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            -1,
            stateMachine.StateField.Type);
        var assignment = new BoundFieldAssignmentExpression(
            null,
            stateMachine.StateField,
            literal,
            stateMachine.Compilation.GetSpecialType(SpecialType.System_Unit));
        statements.Add(new BoundAssignmentStatement(assignment));

        var cleanup = new BoundBlockStatement(statements);
        var pendingCleanup = stateMachine.DisposeBody ?? new BoundBlockStatement([]);
        var body = new BoundBlockStatement([
            new BoundTryStatement(pendingCleanup, [], cleanup),
            new BoundReturnStatement(null),
        ]);
        return AsyncLowerer.Rewrite(stateMachine.AsyncDisposeMethod!, body);
    }

    private static BoundBlockStatement CreateResetBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var notSupportedType = compilation.GetTypeByMetadataName("System.NotSupportedException") as INamedTypeSymbol;
        var constructor = notSupportedType?
            .Constructors
            .FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);

        if (constructor is null)
            return new BoundBlockStatement(new[] { new BoundReturnStatement(null) });

        var creation = new BoundObjectCreationExpression(constructor, Array.Empty<BoundExpression>());
        var throwStatement = new BoundThrowStatement(creation);
        return new BoundBlockStatement(new BoundStatement[] { throwStatement });
    }

    private static BoundBlockStatement CreateGenericGetEnumeratorBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();

        var literal = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            0,
            stateMachine.StateField.Type);
        var assignment = new BoundFieldAssignmentExpression(
            null,
            stateMachine.StateField,
            literal,
            compilation.GetSpecialType(SpecialType.System_Unit));
        statements.Add(new BoundAssignmentStatement(assignment));

        BoundExpression result = new BoundSelfExpression(stateMachine);
        var method = stateMachine.GenericGetEnumeratorMethod!;
        result = ConvertIfNeeded(compilation, result, method.ReturnType);

        statements.Add(new BoundReturnStatement(result));
        return new BoundBlockStatement(statements);
    }

    private static BoundBlockStatement CreateNonGenericGetEnumeratorBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var method = stateMachine.NonGenericGetEnumeratorMethod!;
        var genericMethod = stateMachine.GenericGetEnumeratorMethod;

        BoundExpression result;
        if (genericMethod is not null)
        {
            result = new BoundInvocationExpression(
                genericMethod,
                Array.Empty<BoundExpression>(),
                receiver: new BoundSelfExpression(stateMachine));
        }
        else
        {
            result = new BoundSelfExpression(stateMachine);
        }

        result = ConvertIfNeeded(compilation, result, method.ReturnType);

        var returnStatement = new BoundReturnStatement(result);
        return new BoundBlockStatement(new[] { returnStatement });
    }

    private static BoundBlockStatement CreateAsyncMoveNextBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var method = stateMachine.AsyncMoveNextMethod!;
        var moveNextInvocation = new BoundInvocationExpression(
            stateMachine.MoveNextMethod,
            Array.Empty<BoundExpression>(),
            receiver: new BoundSelfExpression(stateMachine));

        BoundExpression result = moveNextInvocation;
        if (method.ReturnType is INamedTypeSymbol valueTaskOfBoolType)
        {
            var constructor = valueTaskOfBoolType.Constructors
                .FirstOrDefault(candidate =>
                    !candidate.IsStatic &&
                    candidate.Parameters.Length == 1 &&
                    candidate.Parameters[0].Type.SpecialType == SpecialType.System_Boolean);

            if (constructor is not null)
            {
                result = new BoundObjectCreationExpression(constructor, new[] { moveNextInvocation });
            }
            else
            {
                result = ConvertIfNeeded(compilation, result, method.ReturnType);
            }
        }
        else
        {
            result = ConvertIfNeeded(compilation, result, method.ReturnType);
        }

        return new BoundBlockStatement(new[] { new BoundReturnStatement(result) });
    }

    private static BoundBlockStatement CreateAsyncGetEnumeratorBody(
        Compilation compilation,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();

        var literal = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            0,
            stateMachine.StateField.Type);
        var assignment = new BoundFieldAssignmentExpression(
            null,
            stateMachine.StateField,
            literal,
            compilation.GetSpecialType(SpecialType.System_Unit));
        statements.Add(new BoundAssignmentStatement(assignment));

        if (stateMachine.EnumeratorCancellationParameter is { } cancellationParameter &&
            stateMachine.EnumeratorCancellationOriginalParameterField is { } originalCancellationField &&
            stateMachine.ParameterFieldMap.TryGetValue(cancellationParameter, out var effectiveCancellationField))
        {
            var enumeratorCancellationToken = stateMachine.AsyncGetEnumeratorMethod!.Parameters[0];
            statements.AddRange(CreateEnumeratorCancellationAssignments(
                compilation,
                originalCancellationField,
                effectiveCancellationField,
                stateMachine.CombinedTokensField,
                enumeratorCancellationToken));
        }

        BoundExpression result = new BoundSelfExpression(stateMachine);
        var method = stateMachine.AsyncGetEnumeratorMethod!;
        result = ConvertIfNeeded(compilation, result, method.ReturnType);
        statements.Add(new BoundReturnStatement(result));

        return new BoundBlockStatement(statements);
    }

    private static void EnsureIteratorHelpers(Compilation compilation, SynthesizedIteratorTypeSymbol stateMachine)
    {
        if (stateMachine.CurrentGetterBody is null)
        {
            var body = CreateCurrentGetterBody(stateMachine);
            stateMachine.SetCurrentGetterBody(body);
        }

        if (stateMachine.NonGenericCurrentGetterBody is null)
        {
            var body = CreateNonGenericCurrentGetterBody(compilation, stateMachine);
            stateMachine.SetNonGenericCurrentGetterBody(body);
        }

        if (stateMachine.DisposeMethod is not null && stateMachine.DisposeBody is null)
        {
            var body = CreateDisposeBody(stateMachine);
            stateMachine.SetDisposeBody(body);
        }

        if (stateMachine.AsyncDisposeMethod is not null && stateMachine.AsyncDisposeBody is null)
        {
            var body = CreateAsyncDisposeBody(stateMachine);
            stateMachine.SetAsyncDisposeBody(body);
        }

        if (stateMachine.ResetMethod is not null && stateMachine.ResetBody is null)
        {
            var body = CreateResetBody(compilation, stateMachine);
            stateMachine.SetResetBody(body);
        }

        if (stateMachine.GenericGetEnumeratorMethod is not null && stateMachine.GenericGetEnumeratorBody is null)
        {
            var body = CreateGenericGetEnumeratorBody(compilation, stateMachine);
            stateMachine.SetGenericGetEnumeratorBody(body);
        }

        if (stateMachine.NonGenericGetEnumeratorMethod is not null && stateMachine.NonGenericGetEnumeratorBody is null)
        {
            var body = CreateNonGenericGetEnumeratorBody(compilation, stateMachine);
            stateMachine.SetNonGenericGetEnumeratorBody(body);
        }

        if (stateMachine.AsyncMoveNextMethod is not null && stateMachine.AsyncMoveNextBody is null)
        {
            var body = CreateAsyncMoveNextBody(compilation, stateMachine);
            stateMachine.SetAsyncMoveNextBody(body);
        }

        if (stateMachine.AsyncGetEnumeratorMethod is not null && stateMachine.AsyncGetEnumeratorBody is null)
        {
            var body = CreateAsyncGetEnumeratorBody(compilation, stateMachine);
            stateMachine.SetAsyncGetEnumeratorBody(body);
        }
    }

    private static BoundExpression ConvertIfNeeded(
        Compilation compilation,
        BoundExpression expression,
        ITypeSymbol targetType)
    {
        if (SymbolEqualityComparer.Default.Equals(expression.Type, targetType))
            return expression;

        var conversion = compilation.ClassifyConversion(expression.Type, targetType);
        if (!conversion.Exists)
        {
            var fromType = expression.Type;
            var isReference = !fromType.IsValueType && targetType.IsReferenceType;
            var isBoxing = fromType.IsValueType && targetType.IsReferenceType;
            conversion = new Conversion(isImplicit: true, isReference: isReference, isBoxing: isBoxing);
        }

        return new BoundConversionExpression(expression, targetType, conversion);
    }

    private static BoundBlockStatement RewriteIteratorMethodBody(
        Compilation compilation,
        IMethodSymbol method,
        SynthesizedIteratorTypeSymbol stateMachine)
    {
        var statements = new List<BoundStatement>();
        var unitType = compilation.GetSpecialType(SpecialType.System_Unit);

        var stateMachineLocal = new SourceLocalSymbol(
            "<>iter",
            stateMachine,
            isMutable: true,
            method,
            method.ContainingType,
            method.ContainingNamespace,
            Array.Empty<Location>(),
            Array.Empty<SyntaxReference>());

        var creation = new BoundObjectCreationExpression(stateMachine.Constructor, Array.Empty<BoundExpression>());
        var declarator = new BoundVariableDeclarator(stateMachineLocal, creation);
        statements.Add(new BoundLocalDeclarationStatement(new[] { declarator }));

        if (stateMachine.ThisField is not null)
        {
            var receiver = new BoundLocalAccess(stateMachineLocal);
            var value = new BoundSelfExpression(stateMachine.ThisField.Type);
            var assignment = new BoundFieldAssignmentExpression(receiver, stateMachine.ThisField, value, unitType);
            statements.Add(new BoundAssignmentStatement(assignment));
        }

        foreach (var parameter in method.Parameters)
        {
            SourceFieldSymbol? field = null;
            if (stateMachine.EnumeratorCancellationParameter is not null &&
                SymbolEqualityComparer.Default.Equals(parameter, stateMachine.EnumeratorCancellationParameter))
            {
                field = stateMachine.EnumeratorCancellationOriginalParameterField;
            }

            if (field is null && !stateMachine.ParameterFieldMap.TryGetValue(parameter, out field))
                continue;

            var receiver = new BoundLocalAccess(stateMachineLocal);
            var value = new BoundParameterAccess(parameter);
            var assignment = new BoundFieldAssignmentExpression(receiver, field, value, unitType);
            statements.Add(new BoundAssignmentStatement(assignment));
        }

        var stateReceiver = new BoundLocalAccess(stateMachineLocal);
        var initialState = new BoundLiteralExpression(
            BoundLiteralExpressionKind.NumericLiteral,
            0,
            stateMachine.StateField.Type);
        var stateAssignment = new BoundFieldAssignmentExpression(stateReceiver, stateMachine.StateField, initialState, unitType);
        statements.Add(new BoundAssignmentStatement(stateAssignment));

        BoundExpression returnExpression = new BoundLocalAccess(stateMachineLocal);
        var returnType = method.ReturnType;
        returnExpression = ConvertIfNeeded(compilation, returnExpression, returnType);

        statements.Add(new BoundReturnStatement(returnExpression));

        return new BoundBlockStatement(statements);
    }

    private static Compilation GetCompilation(IMethodSymbol method)
    {
        if (method.ContainingAssembly is SourceAssemblySymbol sourceAssembly)
            return sourceAssembly.Compilation;

        throw new InvalidOperationException("Iterator lowering requires a source assembly method.");
    }

    private static IteratorSignature DetermineIteratorSignature(Compilation compilation, ITypeSymbol returnType)
    {
        if (returnType is null)
            return new IteratorSignature(IteratorMethodKind.None, compilation.ErrorTypeSymbol);

        var enumerableDefinition = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);
        var enumeratorDefinition = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerator_T);
        var enumerableType = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
        var enumeratorType = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerator);
        var asyncEnumerableMetadata = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        var asyncEnumeratorMetadata = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerator`1");

        if (returnType is INamedTypeSymbol named)
        {
            var definition = named.ConstructedFrom as INamedTypeSymbol ?? named;

            if (MatchesMetadata(definition, enumerableDefinition))
            {
                var element = named.TypeArguments.Length == 1
                    ? named.TypeArguments[0]
                    : compilation.GetSpecialType(SpecialType.System_Object);
                return new IteratorSignature(IteratorMethodKind.Enumerable, element);
            }

            if (MatchesMetadata(definition, enumeratorDefinition))
            {
                var element = named.TypeArguments.Length == 1
                    ? named.TypeArguments[0]
                    : compilation.GetSpecialType(SpecialType.System_Object);
                return new IteratorSignature(IteratorMethodKind.Enumerator, element);
            }

            if (asyncEnumerableMetadata is INamedTypeSymbol asyncEnumerableDefinition &&
                MatchesMetadata(definition, asyncEnumerableDefinition))
            {
                var element = named.TypeArguments.Length == 1
                    ? named.TypeArguments[0]
                    : compilation.GetSpecialType(SpecialType.System_Object);
                return new IteratorSignature(IteratorMethodKind.AsyncEnumerable, element);
            }

            if (asyncEnumeratorMetadata is INamedTypeSymbol asyncEnumeratorDefinition &&
                MatchesMetadata(definition, asyncEnumeratorDefinition))
            {
                var element = named.TypeArguments.Length == 1
                    ? named.TypeArguments[0]
                    : compilation.GetSpecialType(SpecialType.System_Object);
                return new IteratorSignature(IteratorMethodKind.AsyncEnumerator, element);
            }

            if (MatchesMetadata(definition, enumerableType))
                return new IteratorSignature(IteratorMethodKind.Enumerable, compilation.GetSpecialType(SpecialType.System_Object));

            if (MatchesMetadata(definition, enumeratorType))
                return new IteratorSignature(IteratorMethodKind.Enumerator, compilation.GetSpecialType(SpecialType.System_Object));
        }

        if (returnType is INamedTypeSymbol namedEnumerable && MatchesMetadata(namedEnumerable, enumerableType))
            return new IteratorSignature(IteratorMethodKind.Enumerable, compilation.GetSpecialType(SpecialType.System_Object));

        if (returnType is INamedTypeSymbol namedEnumerator && MatchesMetadata(namedEnumerator, enumeratorType))
            return new IteratorSignature(IteratorMethodKind.Enumerator, compilation.GetSpecialType(SpecialType.System_Object));

        return new IteratorSignature(IteratorMethodKind.None, compilation.ErrorTypeSymbol);
    }

    private readonly record struct IteratorSignature(IteratorMethodKind Kind, ITypeSymbol ElementType);

    private static IEnumerable<BoundStatement> CreateEnumeratorCancellationAssignments(
        Compilation compilation,
        SourceFieldSymbol originalCancellationField,
        SourceFieldSymbol effectiveCancellationField,
        SourceFieldSymbol? combinedTokensField,
        IParameterSymbol enumeratorCancellationParameter)
    {
        var statements = new List<BoundStatement>();
        var unitType = compilation.GetSpecialType(SpecialType.System_Unit);
        var originalToken = new BoundFieldAccess(originalCancellationField);
        var effectiveToken = new BoundFieldAccess(effectiveCancellationField);
        var enumeratorToken = new BoundParameterAccess(enumeratorCancellationParameter);
        var defaultToken = new BoundDefaultValueExpression(originalCancellationField.Type);

        var assignEffectiveToEnumerator = new BoundAssignmentStatement(
            new BoundFieldAssignmentExpression(
                null,
                effectiveCancellationField,
                enumeratorToken,
                unitType));

        var assignEffectiveToOriginal = new BoundAssignmentStatement(
            new BoundFieldAssignmentExpression(
                null,
                effectiveCancellationField,
                originalToken,
                unitType));

        var originalIsDefault = CreateCancellationTokenEquals(
            compilation,
            originalCancellationField.Type,
            originalToken,
            defaultToken);

        var enumeratorIsDefault = CreateCancellationTokenEquals(
            compilation,
            enumeratorCancellationParameter.Type,
            enumeratorToken,
            defaultToken);

        var enumeratorEqualsOriginal = CreateCancellationTokenEquals(
            compilation,
            enumeratorCancellationParameter.Type,
            enumeratorToken,
            originalToken);

        BoundStatement combinedAssignment = assignEffectiveToOriginal;
        if (combinedTokensField is not null)
        {
            var cancellationTokenSourceType = combinedTokensField.Type as INamedTypeSymbol;
            if (cancellationTokenSourceType is not null)
            {
                var createLinkedTokenSource = cancellationTokenSourceType
                    .GetMembers("CreateLinkedTokenSource")
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(method =>
                        method.IsStatic &&
                        method.Parameters.Length == 2 &&
                        SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, originalCancellationField.Type) &&
                        SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, enumeratorCancellationParameter.Type));
                var tokenProperty = cancellationTokenSourceType
                    .GetMembers("Token")
                    .OfType<IPropertySymbol>()
                    .FirstOrDefault(property =>
                        !property.IsStatic &&
                        SymbolEqualityComparer.Default.Equals(property.Type, originalCancellationField.Type) &&
                        property.GetMethod is not null);

                if (createLinkedTokenSource is not null && tokenProperty?.GetMethod is not null)
                {
                    var createLinkedInvocation = new BoundInvocationExpression(
                        createLinkedTokenSource,
                        new BoundExpression[] { originalToken, enumeratorToken });
                    var assignCombinedTokens = new BoundAssignmentStatement(
                        new BoundFieldAssignmentExpression(
                            null,
                            combinedTokensField,
                            createLinkedInvocation,
                            unitType));
                    var combinedTokenAccess = new BoundFieldAccess(combinedTokensField);
                    var getCombinedToken = new BoundInvocationExpression(
                        tokenProperty.GetMethod,
                        Array.Empty<BoundExpression>(),
                        receiver: combinedTokenAccess);
                    var assignEffectiveToCombined = new BoundAssignmentStatement(
                        new BoundFieldAssignmentExpression(
                            null,
                            effectiveCancellationField,
                            getCombinedToken,
                            unitType));

                    combinedAssignment = new BoundBlockStatement(new BoundStatement[]
                    {
                        assignCombinedTokens,
                        assignEffectiveToCombined,
                    });
                }
            }
        }

        statements.Add(new BoundIfStatement(
            originalIsDefault,
            assignEffectiveToEnumerator,
            new BoundIfStatement(
                enumeratorIsDefault,
                assignEffectiveToOriginal,
                new BoundIfStatement(
                    enumeratorEqualsOriginal,
                    assignEffectiveToOriginal,
                    combinedAssignment))));

        return statements;
    }

    private static bool MatchesMetadata(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        if (candidate.MetadataName != target.MetadataName)
            return false;

        return NamespaceEquals(candidate.ContainingNamespace, target.ContainingNamespace);
    }

    private static bool NamespaceEquals(INamespaceSymbol? left, INamespaceSymbol? right)
    {
        if (left is null && right is null)
            return true;

        if (left is null || right is null)
            return false;

        if (left.IsGlobalNamespace && right.IsGlobalNamespace)
            return true;

        if (left.MetadataName != right.MetadataName)
            return false;

        return NamespaceEquals(left.ContainingNamespace, right.ContainingNamespace);
    }

    private static BoundExpression CreateCancellationTokenEquals(
        Compilation compilation,
        ITypeSymbol receiverType,
        BoundExpression receiver,
        BoundExpression argument)
    {
        var equalsMethod = (receiverType as INamedTypeSymbol)?
            .GetMembers("Equals")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                !method.IsStatic &&
                method.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, argument.Type) &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean);

        if (equalsMethod is not null)
        {
            return new BoundInvocationExpression(
                equalsMethod,
                new[] { argument },
                receiver: receiver);
        }

        return CreateBinaryExpression(compilation, SyntaxKind.EqualsEqualsToken, receiver, argument);
    }

    private static BoundExpression CreateBinaryExpression(
        Compilation compilation,
        SyntaxKind operatorKind,
        BoundExpression left,
        BoundExpression right)
    {
        if (BoundBinaryOperator.TryLookup(compilation, operatorKind, left.Type, right.Type, out var op))
            return new BoundBinaryExpression(left, op, right);

        throw new InvalidOperationException($"Iterator lowering requires operator '{operatorKind}'.");
    }

    private sealed class YieldStatementFinder : BoundTreeWalker
    {
        public bool FoundYield { get; private set; }

        public override void VisitStatement(BoundStatement statement)
        {
            if (FoundYield)
                return;

            base.VisitStatement(statement);
        }

        public override void VisitExpression(BoundExpression node)
        {
            if (FoundYield)
                return;

            base.VisitExpression(node);
        }

        public override void VisitYieldStatement(BoundYieldStatement node)
        {
            FoundYield = true;
        }

        public override void VisitYieldExpression(BoundYieldExpression node)
        {
            FoundYield = true;
        }

        public override void VisitFunctionExpression(BoundFunctionExpression node)
        {
            // Nested lambdas are lowered independently.
        }
    }

    private sealed class MoveNextBuilder : BoundTreeRewriter
    {
        private readonly Compilation _compilation;
        private readonly SynthesizedIteratorTypeSymbol _stateMachine;
        private readonly SourceMethodSymbol _moveNextMethod;
        private readonly List<StateEntry> _states = new();
        private readonly Dictionary<int, ILabelSymbol> _protectedStateTargets = new();
        private readonly Dictionary<ILocalSymbol, SourceFieldSymbol> _hoistedLocals = new(SymbolEqualityComparer.Default);
        private readonly HashSet<string> _hoistedFieldNames = new(StringComparer.Ordinal);
        private readonly Stack<FinallyFrame> _finallyStack = new();
        private readonly Stack<(ILabelSymbol BreakLabel, ILabelSymbol ContinueLabel)> _loopLabels = new();
        private readonly Dictionary<ILabelSymbol, (ILabelSymbol BreakLabel, ILabelSymbol ContinueLabel)> _labeledLoopTargets = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<int, ImmutableArray<FinallyFrame>> _pendingFinallyStates = new();
        private readonly ITypeSymbol _boolType;
        private readonly ITypeSymbol _unitType;
        private ImmutableArray<ILabelSymbol> _pendingSourceLoopLabels = ImmutableArray<ILabelSymbol>.Empty;
        private bool _isTopLevelBlock = true;
        private StateEntry _startState;
        private LabelSymbol? _returnLabel;
        private SourceLocalSymbol? _resultLocal;
        private int _nextHoistedLocalId;

        public BoundBlockStatement? DisposeBody { get; private set; }

        public MoveNextBuilder(
            Compilation compilation,
            SynthesizedIteratorTypeSymbol stateMachine,
            SourceMethodSymbol moveNextMethod)
        {
            _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _moveNextMethod = moveNextMethod ?? throw new ArgumentNullException(nameof(moveNextMethod));
            _boolType = compilation.GetSpecialType(SpecialType.System_Boolean);
            _unitType = compilation.GetSpecialType(SpecialType.System_Unit);
        }

        public BoundBlockStatement Rewrite(BoundBlockStatement block)
        {
            if (block is null)
                throw new ArgumentNullException(nameof(block));

            HoistLocals(block);
            _startState = AllocateState();
            _returnLabel = new AsyncProtectedRegionExitLabelSymbol("<>Return", _moveNextMethod, _stateMachine, _stateMachine.ContainingNamespace, [Location.None], []);
            _resultLocal = CreateResultLocal();

            var rewrittenBody = (BoundBlockStatement)VisitBlockStatement(block);

            var statements = new List<BoundStatement>();
            var resultLocal = _resultLocal ?? throw new InvalidOperationException("Result local not created.");
            var resultDeclarator = new BoundVariableDeclarator(resultLocal, CreateBoolLiteral(false));
            statements.Add(new BoundLocalDeclarationStatement(new[] { resultDeclarator }));

            statements.AddRange(CreateStateDispatch());
            statements.Add(CreateReturnStatement(CreateBoolLiteral(false)));
            statements.AddRange(rewrittenBody.Statements);
            statements.Add(CreateStateAssignment(-1));
            statements.Add(CreateReturnStatement(CreateBoolLiteral(false)));

            var returnLabel = _returnLabel ?? throw new InvalidOperationException("Return label not created.");
            var returnBlock = new BoundBlockStatement(new BoundStatement[]
            {
                new BoundReturnStatement(new BoundLocalAccess(resultLocal)),
            });
            statements.Add(new BoundLabeledStatement(returnLabel, returnBlock));

            DisposeBody = BuildDisposeBody();

            return new BoundBlockStatement(statements);
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
                if (initializer?.Type?.SpecialType == SpecialType.System_Unit &&
                    BoundNodeFacts.ContainsControlTransfer(initializer))
                {
                    // Complete suspension before loading the assignment receiver. Keep
                    // the expression's scope and cleanup intact while discarding its unit value.
                    hoistedStatements.Add(new BoundExpressionStatement(initializer));
                    initializer = new BoundUnitExpression(_unitType);
                }

                if (_hoistedLocals.TryGetValue(declarator.Local, out var field))
                {
                    if (initializer is not null)
                    {
                        var assignment = new BoundFieldAssignmentExpression(
                            new BoundSelfExpression(_stateMachine),
                            field,
                            initializer,
                            _unitType);
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

        public override BoundNode? VisitLocalAccess(BoundLocalAccess node)
        {
            if (node is null)
                return null;

            if (_hoistedLocals.TryGetValue(node.Local, out var field))
                return new BoundFieldAccess(field, node.Reason);

            return node;
        }

        public override BoundNode? VisitVariableExpression(BoundVariableExpression node)
        {
            if (node is null)
                return null;

            if (_hoistedLocals.TryGetValue(node.Variable, out var field))
                return new BoundFieldAccess(field, node.Reason);

            return node;
        }

        public override BoundNode? VisitParameterAccess(BoundParameterAccess node)
        {
            if (node is null)
                return null;

            if (_stateMachine.ParameterFieldMap.TryGetValue(node.Parameter, out var field))
                return new BoundFieldAccess(field, node.Reason);

            return node;
        }

        public override BoundNode? VisitReturnStatement(BoundReturnStatement node)
        {
            if (node is null)
                return null;

            var expression = node.Expression is null
                ? null
                : VisitExpression(node.Expression) ?? node.Expression;

            return CreateReturnStatement(expression);
        }

        public override BoundNode? VisitLocalAssignmentExpression(BoundLocalAssignmentExpression node)
        {
            if (node is null)
                return null;

            var right = VisitExpression(node.Right) ?? node.Right;

            if (_hoistedLocals.TryGetValue(node.Local, out var field))
                return new BoundFieldAssignmentExpression(
                    new BoundSelfExpression(_stateMachine),
                    field,
                    right,
                    _unitType);

            if (!ReferenceEquals(right, node.Right))
            {
                return new BoundLocalAssignmentExpression(node.Local, node.LocalAccess, right, _unitType);
            }

            return node;
        }

        public override BoundNode? VisitBlockStatement(BoundBlockStatement node)
        {
            if (node is null)
                return null;

            var wasTopLevel = _isTopLevelBlock;
            _isTopLevelBlock = false;

            var statements = new List<BoundStatement>();
            foreach (var statement in node.Statements)
            {
                var rewritten = VisitStatement(statement);
                if (rewritten is BoundBlockStatement rewrittenBlock &&
                    !rewrittenBlock.Statements.Any() &&
                    rewrittenBlock.LocalsToDispose.Length == 0)
                {
                    continue;
                }

                statements.Add(rewritten);
            }

            _isTopLevelBlock = wasTopLevel;

            if (wasTopLevel)
            {
                var inner = new BoundBlockStatement(new[] { CreateStateAssignment(-1) }.Cast<BoundStatement>().Concat(statements), node.LocalsToDispose);
                var labeled = new BoundLabeledStatement(_startState.Label, inner);
                return new BoundBlockStatement(new BoundStatement[] { labeled });
            }

            return new BoundBlockStatement(statements, node.LocalsToDispose);
        }

        public override BoundNode? VisitTryStatement(BoundTryStatement node)
        {
            if (node is null)
                return null;

            var firstState = _states.Count;
            FinallyFrame? frame = null;
            if (node.FinallyBlock is not null)
            {
                frame = new FinallyFrame();
                _finallyStack.Push(frame);
            }

            try
            {
                var tryBlock = (BoundBlockStatement)VisitBlockStatement(node.TryBlock);

                var catchBuilder = ImmutableArray.CreateBuilder<BoundCatchClause>(node.CatchClauses.Length);
                foreach (var catchClause in node.CatchClauses)
                {
                    var catchBlock = (BoundBlockStatement)VisitBlockStatement(catchClause.Block);
                    catchBuilder.Add(new BoundCatchClause(catchClause.ExceptionType, catchClause.Local, catchClause.Pattern, catchClause.Guard, catchBlock));
                }

                BoundBlockStatement? finallyBlock = null;
                if (node.FinallyBlock is not null)
                {
                    var rewrittenFinally = (BoundBlockStatement)VisitBlockStatement(node.FinallyBlock);
                    finallyBlock = WrapFinallyBlock(rewrittenFinally);

                    if (frame is not null)
                        frame.FinallyBlock = finallyBlock;
                }

                return ProtectIteratorRegion(new BoundTryStatement(tryBlock, catchBuilder.ToImmutable(), finallyBlock), firstState);
            }
            finally
            {
                if (frame is not null && _finallyStack.Count > 0 && ReferenceEquals(_finallyStack.Peek(), frame))
                    _finallyStack.Pop();
            }
        }

        public override BoundNode? VisitYieldStatement(BoundYieldStatement node)
        {
            if (node is null)
                return null;

            var resumeState = AllocateState();
            var expression = VisitExpression(node.Expression) ?? node.Expression;
            var currentValue = ApplyElementConversion(expression);

            var assignCurrent = new BoundAssignmentStatement(
                new BoundFieldAssignmentExpression(
                    new BoundSelfExpression(_stateMachine),
                    _stateMachine.CurrentField,
                    currentValue,
                    _unitType));

            var assignState = CreateStateAssignment(resumeState.Value);

            var returnTrue = CreateReturnStatement(CreateBoolLiteral(true));
            var resumeLabel = new BoundLabeledStatement(resumeState.Label, new BoundBlockStatement([CreateStateAssignment(-1)]));

            if (_finallyStack.Count > 0)
            {
                var builder = ImmutableArray.CreateBuilder<FinallyFrame>(_finallyStack.Count);
                foreach (var frame in _finallyStack)
                    builder.Add(frame);

                _pendingFinallyStates[resumeState.Value] = builder.ToImmutable();
            }

            return new BoundBlockStatement(new BoundStatement[]
            {
                assignCurrent,
                assignState,
                returnTrue,
                resumeLabel,
            });
        }

        public override BoundNode? VisitYieldExpression(BoundYieldExpression node)
        {
            var rewritten = (BoundStatement)VisitYieldStatement(
                new BoundYieldStatement(node.Expression, node.ElementType, node.IteratorKind))!;
            return new BoundBlockExpression(
                [rewritten, new BoundExpressionStatement(new BoundUnitExpression(_unitType))],
                _unitType,
                []);
        }

        public override BoundNode? VisitReturnExpression(BoundReturnExpression node)
        {
            var rewritten = (BoundBlockStatement)VisitReturnStatement(
                new BoundReturnStatement(node.Expression))!;
            return new BoundBlockExpression(rewritten.Statements, node.Type, rewritten.LocalsToDispose);
        }

        public override BoundNode? VisitMatchExpression(BoundMatchExpression node)
        {
            var expression = VisitExpression(node.Expression) ?? node.Expression;
            var arms = RewriteMatchArms(node.Arms);
            var rewritten = node.Update(expression, arms, node.Type);
            return Lowerer.LowerExpression(_moveNextMethod, rewritten);
        }

        public override BoundNode? VisitMatchStatement(BoundMatchStatement node)
        {
            var expression = VisitExpression(node.Expression) ?? node.Expression;
            var arms = RewriteMatchArms(node.Arms);
            var rewritten = node.Update(expression, arms);
            return Lowerer.LowerStatement(_moveNextMethod, rewritten);
        }

        private ImmutableArray<BoundMatchArm> RewriteMatchArms(ImmutableArray<BoundMatchArm> arms)
        {
            var rewritten = ImmutableArray.CreateBuilder<BoundMatchArm>(arms.Length);
            foreach (var arm in arms)
            {
                var guard = arm.Guard is null ? null : VisitExpression(arm.Guard) ?? arm.Guard;
                var expression = VisitExpression(arm.Expression) ?? arm.Expression;
                rewritten.Add(new BoundMatchArm(arm.Pattern, guard, expression));
            }

            return rewritten.MoveToImmutable();
        }

        public override BoundNode? VisitWhileStatement(BoundWhileStatement node)
        {
            if (node is null)
                return null;

            var sourceLabels = TakePendingSourceLoopLabels();
            var breakLabel = CreateLabel("while_break");
            var continueLabel = CreateLabel("while_continue");

            var condition = (BoundExpression)(VisitExpression(node.Condition) ?? node.Condition);

            var body = VisitLoopBody(node.Body, breakLabel, continueLabel, sourceLabels);

            return new BoundBlockStatement([
                new BoundLabeledStatement(continueLabel, new BoundBlockStatement([
                    new BoundConditionalGotoStatement(breakLabel, condition, jumpIfTrue: false),
                ])),
                body,
                new BoundGotoStatement(continueLabel, isBackward: true),
                new BoundLabeledStatement(breakLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
            ]);
        }

        public override BoundNode? VisitLoopStatement(BoundLoopStatement node)
        {
            if (node is null)
                return null;

            var sourceLabels = TakePendingSourceLoopLabels();
            var breakLabel = CreateLabel("loop_break");
            var continueLabel = CreateLabel("loop_continue");

            var body = VisitLoopBody(node.Body, breakLabel, continueLabel, sourceLabels);

            return new BoundBlockStatement([
                new BoundLabeledStatement(continueLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
                body,
                new BoundGotoStatement(continueLabel, isBackward: true),
                new BoundLabeledStatement(breakLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
            ]);
        }

        public override BoundNode? VisitBreakStatement(BoundBreakStatement node)
        {
            if (node.TargetLabel is { } targetLabel)
            {
                if (_labeledLoopTargets.TryGetValue(targetLabel, out var target))
                    return new BoundGotoStatement(target.BreakLabel);

                return base.VisitBreakStatement(node);
            }

            if (_loopLabels.Count == 0)
                return base.VisitBreakStatement(node);

            var (breakLabel, _) = _loopLabels.Peek();
            return new BoundGotoStatement(breakLabel);
        }

        public override BoundNode? VisitContinueStatement(BoundContinueStatement node)
        {
            if (node.TargetLabel is { } targetLabel)
            {
                if (_labeledLoopTargets.TryGetValue(targetLabel, out var target))
                    return new BoundGotoStatement(target.ContinueLabel, isBackward: true);

                return base.VisitContinueStatement(node);
            }

            if (_loopLabels.Count == 0)
                return base.VisitContinueStatement(node);

            var (_, continueLabel) = _loopLabels.Peek();
            return new BoundGotoStatement(continueLabel, isBackward: true);
        }

        public override BoundNode? VisitBreakExpression(BoundBreakExpression node)
        {
            ILabelSymbol? breakLabel = null;
            if (node.TargetLabel is { } targetLabel)
            {
                if (_labeledLoopTargets.TryGetValue(targetLabel, out var target))
                    breakLabel = target.BreakLabel;
            }
            else if (_loopLabels.Count > 0)
            {
                breakLabel = _loopLabels.Peek().BreakLabel;
            }

            return breakLabel is null
                ? base.VisitBreakExpression(node)
                : new BoundBlockExpression([new BoundGotoStatement(breakLabel)], node.Type, []);
        }

        public override BoundNode? VisitContinueExpression(BoundContinueExpression node)
        {
            ILabelSymbol? continueLabel = null;
            if (node.TargetLabel is { } targetLabel)
            {
                if (_labeledLoopTargets.TryGetValue(targetLabel, out var target))
                    continueLabel = target.ContinueLabel;
            }
            else if (_loopLabels.Count > 0)
            {
                continueLabel = _loopLabels.Peek().ContinueLabel;
            }

            return continueLabel is null
                ? base.VisitContinueExpression(node)
                : new BoundBlockExpression(
                    [new BoundGotoStatement(continueLabel, isBackward: true)],
                    node.Type,
                    []);
        }

        public override BoundNode? VisitLabeledStatement(BoundLabeledStatement node)
        {
            var labels = new List<ILabelSymbol>();
            BoundStatement current = node;
            while (current is BoundLabeledStatement labeled)
            {
                labels.Add(labeled.Label);
                current = labeled.Statement;
            }

            if (!IsLoopStatement(current))
                return base.VisitLabeledStatement(node);

            var previousLabels = _pendingSourceLoopLabels;
            _pendingSourceLoopLabels = previousLabels.AddRange(labels);
            try
            {
                var lowered = (BoundStatement)VisitStatement(current)!;
                return WrapLabels(labels, lowered);
            }
            finally
            {
                _pendingSourceLoopLabels = previousLabels;
            }
        }

        public override BoundNode? VisitForStatement(BoundForStatement node)
        {
            if (node is null)
                return null;

            return node.Iteration.Kind switch
            {
                ForIterationKind.Range => RewriteRangeForStatement(node),
                ForIterationKind.Array => RewriteArrayForStatement(node),
                ForIterationKind.Generic or ForIterationKind.NonGeneric => RewriteEnumeratorForStatement(node),
                _ => base.VisitForStatement(node),
            };
        }

        private ImmutableArray<ILabelSymbol> TakePendingSourceLoopLabels()
        {
            var labels = _pendingSourceLoopLabels;
            _pendingSourceLoopLabels = ImmutableArray<ILabelSymbol>.Empty;
            return labels;
        }

        private BoundStatement VisitLoopBody(
            BoundStatement body,
            ILabelSymbol breakLabel,
            ILabelSymbol continueLabel,
            ImmutableArray<ILabelSymbol> sourceLabels)
        {
            foreach (var label in sourceLabels)
                _labeledLoopTargets[label] = (breakLabel, continueLabel);

            _loopLabels.Push((breakLabel, continueLabel));
            try
            {
                return (BoundStatement)VisitStatement(body)!;
            }
            finally
            {
                _loopLabels.Pop();

                foreach (var label in sourceLabels)
                    _labeledLoopTargets.Remove(label);
            }
        }

        private static bool IsLoopStatement(BoundStatement statement)
        {
            while (statement is BoundLabeledStatement labeled)
                statement = labeled.Statement;

            return statement is BoundWhileStatement or BoundLoopStatement or BoundForStatement;
        }

        private static BoundStatement WrapLabels(List<ILabelSymbol> labels, BoundStatement statement)
        {
            for (var i = labels.Count - 1; i >= 0; i--)
                statement = new BoundLabeledStatement(labels[i], statement);

            return statement;
        }

        private BoundBlockStatement? BuildDisposeBody()
        {
            if (_pendingFinallyStates.Count == 0)
                return null;

            var statements = new List<BoundStatement>();

            foreach (var entry in _pendingFinallyStates.OrderBy(static pair => pair.Key))
            {
                var condition = CreateStateEquals(entry.Key);
                var thenStatements = new List<BoundStatement>
                {
                    CreateStateAssignment(-1),
                };

                BoundBlockStatement? cleanup = null;
                foreach (var frame in entry.Value)
                {
                    if (frame.FinallyBlock is null)
                        continue;

                    // Outer finally blocks must still execute when inner cleanup throws.
                    cleanup = cleanup is null
                        ? frame.FinallyBlock
                        : new BoundBlockStatement([new BoundTryStatement(cleanup, [], frame.FinallyBlock)]);
                }
                if (cleanup is not null)
                    thenStatements.Add(cleanup);

                thenStatements.Add(new BoundReturnStatement(null));

                var thenBlock = new BoundBlockStatement(thenStatements);
                statements.Add(new BoundIfStatement(condition, thenBlock));
            }

            statements.Add(CreateStateAssignment(-1));
            statements.Add(new BoundReturnStatement(null));

            return new BoundBlockStatement(statements);
        }

        private BoundStatement CreateReturnStatement(BoundExpression? expression)
        {
            if (_resultLocal is null || _returnLabel is null)
                throw new InvalidOperationException("Return infrastructure not initialized.");

            var value = expression ?? CreateBoolLiteral(false);
            value = ConvertIfNeeded(_compilation, value, _boolType);

            var localAccess = new BoundLocalAccess(_resultLocal);

            var assignment = new BoundLocalAssignmentExpression(_resultLocal, localAccess, value, _unitType);
            var assignStatement = new BoundAssignmentStatement(assignment);
            var gotoStatement = new BoundGotoStatement(_returnLabel);

            return new BoundBlockStatement(new BoundStatement[]
            {
                assignStatement,
                gotoStatement,
            });
        }

        private IEnumerable<BoundStatement> CreateStateDispatch()
        {
            foreach (var state in _states)
            {
                var condition = CreateStateEquals(state.Value);
                var thenBlock = new BoundBlockStatement(new BoundStatement[]
                {
                    new BoundGotoStatement(_protectedStateTargets.GetValueOrDefault(state.Value, state.Label)),
                });

                yield return new BoundIfStatement(condition, thenBlock);
            }
        }

        private BoundStatement ProtectIteratorRegion(BoundTryStatement region, int firstState)
        {
            if (firstState == _states.Count)
                return region;

            // CLR branches cannot enter a protected region. Resume through its entry,
            // then dispatch from inside the try, preserving nested region boundaries.
            var entry = CreateLabel("iterator_try_entry");
            var dispatch = new List<BoundStatement>();
            foreach (var state in _states.Skip(firstState))
            {
                var target = _protectedStateTargets.GetValueOrDefault(state.Value, state.Label);
                dispatch.Add(new BoundConditionalGotoStatement(target, CreateStateEquals(state.Value), jumpIfTrue: true));
                _protectedStateTargets[state.Value] = entry;
            }
            dispatch.AddRange(region.TryBlock.Statements);
            var rewritten = new BoundTryStatement(new BoundBlockStatement(dispatch, region.TryBlock.LocalsToDispose), region.CatchClauses, region.FinallyBlock, region.Kind);
            return new BoundBlockStatement([new BoundLabeledStatement(entry, new BoundBlockStatement([])), rewritten]);
        }

        private BoundExpression CreateStateEquals(int value)
        {
            var stateAccess = new BoundFieldAccess(_stateMachine.StateField);
            var literal = CreateIntLiteral(value);

            if (!BoundBinaryOperator.TryLookup(_compilation, SyntaxKind.EqualsEqualsToken, stateAccess.Type, literal.Type, out var op))
                throw new InvalidOperationException("Iterator lowering requires integer equality operator.");

            return new BoundBinaryExpression(stateAccess, op, literal);
        }

        private BoundBlockStatement WrapFinallyBlock(BoundBlockStatement block)
        {
            var stateAccess = new BoundFieldAccess(_stateMachine.StateField);
            var zero = CreateIntLiteral(0);

            if (!BoundBinaryOperator.TryLookup(_compilation, SyntaxKind.LessThanToken, stateAccess.Type, zero.Type, out var op))
                throw new InvalidOperationException("Iterator lowering requires integer comparison operator.");

            var condition = new BoundBinaryExpression(stateAccess, op, zero);
            var guard = new BoundIfStatement(condition, block);

            return new BoundBlockStatement(new BoundStatement[] { guard });
        }

        private BoundAssignmentStatement CreateStateAssignment(int value)
        {
            var literal = CreateIntLiteral(value);
            var assignment = new BoundFieldAssignmentExpression(
                new BoundSelfExpression(_stateMachine),
                _stateMachine.StateField,
                literal,
                _unitType);
            return new BoundAssignmentStatement(assignment);
        }

        private LabelSymbol CreateLabel(string name)
        {
            return new LabelSymbol(
                name,
                _moveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());
        }

        private SourceLocalSymbol CreateResultLocal()
        {
            return new SourceLocalSymbol(
                "<>result",
                _boolType,
                isMutable: true,
                _moveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());
        }

        private BoundLiteralExpression CreateIntLiteral(int value)
        {
            return new BoundLiteralExpression(
                BoundLiteralExpressionKind.NumericLiteral,
                value,
                _stateMachine.StateField.Type);
        }

        private BoundLiteralExpression CreateBoolLiteral(bool value)
        {
            return new BoundLiteralExpression(
                value ? BoundLiteralExpressionKind.TrueLiteral : BoundLiteralExpressionKind.FalseLiteral,
                value,
                _boolType);
        }

        private BoundExpression ApplyElementConversion(BoundExpression expression)
        {
            var targetType = _stateMachine.ElementType ?? _compilation.ErrorTypeSymbol;
            var sourceType = expression.Type ?? _compilation.ErrorTypeSymbol;

            if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                return expression;

            var conversion = _compilation.ClassifyConversion(sourceType, targetType);
            if (!conversion.Exists || conversion.IsIdentity)
                return expression;

            return new BoundConversionExpression(expression, targetType, conversion);
        }

        private StateEntry AllocateState()
        {
            var value = _states.Count;
            var label = new LabelSymbol(
                $"<>State_{value}",
                _moveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());

            var entry = new StateEntry(value, label);
            _states.Add(entry);
            return entry;
        }

        private BoundBlockStatement RewriteRangeForStatement(BoundForStatement node)
        {
            var sourceLabels = TakePendingSourceLoopLabels();
            var breakLabel = CreateLabel("for_break");
            var continueLabel = CreateLabel("for_continue");
            var beginLabel = CreateLabel("for_begin");
            var positiveStepLabel = CreateLabel("for_positive");
            var negativeStepLabel = CreateLabel("for_negative");
            var bodyLabel = CreateLabel("for_body");

            var currentLocal = CreateHoistedTempLocal("forCurrent", node.Iteration.ElementType);
            var endLocal = CreateHoistedTempLocal("forEnd", node.Iteration.ElementType);
            var stepLocal = CreateHoistedTempLocal("forStep", node.Iteration.ElementType);

            var start = VisitExpression(node.Iteration.RangeStart!) ?? node.Iteration.RangeStart!;
            var end = VisitExpression(node.Iteration.RangeEnd!) ?? node.Iteration.RangeEnd!;
            var step = VisitExpression(node.Iteration.RangeStep!) ?? node.Iteration.RangeStep!;

            var body = VisitLoopBody(node.Body, breakLabel, continueLabel, sourceLabels);

            var statements = new List<BoundStatement>
            {
                CreateHoistedAssignment(currentLocal, start),
                CreateHoistedAssignment(endLocal, end),
                CreateHoistedAssignment(stepLocal, step),
                new BoundConditionalGotoStatement(
                    breakLabel,
                    CreateBinaryExpression(_compilation, SyntaxKind.EqualsEqualsToken, CreateHoistedAccess(stepLocal), CreateZeroLiteral(node.Iteration.ElementType)),
                    jumpIfTrue: true),
                new BoundLabeledStatement(beginLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
                new BoundConditionalGotoStatement(
                    positiveStepLabel,
                    CreateBinaryExpression(_compilation, SyntaxKind.GreaterThanToken, CreateHoistedAccess(stepLocal), CreateZeroLiteral(node.Iteration.ElementType)),
                    jumpIfTrue: true),
                new BoundConditionalGotoStatement(
                    negativeStepLabel,
                    CreateBinaryExpression(_compilation, SyntaxKind.LessThanToken, CreateHoistedAccess(stepLocal), CreateZeroLiteral(node.Iteration.ElementType)),
                    jumpIfTrue: true),
                new BoundGotoStatement(breakLabel),
                new BoundLabeledStatement(
                    positiveStepLabel,
                    new BoundBlockStatement(new BoundStatement[]
                    {
                        new BoundConditionalGotoStatement(
                            breakLabel,
                            CreateRangeBreakCondition(CreateHoistedAccess(currentLocal), CreateHoistedAccess(endLocal), positiveStep: true, node.Iteration.RangeUpperExclusive),
                            jumpIfTrue: true),
                        new BoundGotoStatement(bodyLabel),
                    })),
                new BoundLabeledStatement(
                    negativeStepLabel,
                    new BoundBlockStatement(new BoundStatement[]
                    {
                        new BoundConditionalGotoStatement(
                            breakLabel,
                            CreateRangeBreakCondition(CreateHoistedAccess(currentLocal), CreateHoistedAccess(endLocal), positiveStep: false, node.Iteration.RangeUpperExclusive),
                            jumpIfTrue: true),
                    })),
                new BoundLabeledStatement(
                    bodyLabel,
                    new BoundBlockStatement(CreateLoopBodyStatements(node.Local, CreateHoistedAccess(currentLocal), body))),
                new BoundLabeledStatement(
                    continueLabel,
                    new BoundBlockStatement(new BoundStatement[]
                    {
                        CreateHoistedAssignment(
                            currentLocal,
                            CreateBinaryExpression(_compilation, SyntaxKind.PlusToken, CreateHoistedAccess(currentLocal), CreateHoistedAccess(stepLocal))),
                        new BoundGotoStatement(beginLabel, isBackward: true),
                    })),
                new BoundLabeledStatement(breakLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
            };

            return new BoundBlockStatement(statements);
        }

        private BoundBlockStatement RewriteArrayForStatement(BoundForStatement node)
        {
            var sourceLabels = TakePendingSourceLoopLabels();
            var arrayType = node.Iteration.ArrayType
                ?? throw new InvalidOperationException("Array for-loop rewrite requires array type.");

            var breakLabel = CreateLabel("for_break");
            var continueLabel = CreateLabel("for_continue");
            var beginLabel = CreateLabel("for_begin");

            var collection = VisitExpression(node.Collection) ?? node.Collection;
            var collectionLocal = CreateHoistedTempLocal("forArray", arrayType);
            var indexLocal = CreateHoistedTempLocal("forIndex", _compilation.GetSpecialType(SpecialType.System_Int32));

            var lengthProperty = arrayType.GetMembers("Length").OfType<IPropertySymbol>().FirstOrDefault()
                ?? arrayType.BaseType?.GetMembers("Length").OfType<IPropertySymbol>().FirstOrDefault()
                ?? throw new InvalidOperationException("Array for-loop rewrite requires Length property.");

            var body = VisitLoopBody(node.Body, breakLabel, continueLabel, sourceLabels);

            var arrayAccess = new BoundArrayAccessExpression(
                CreateHoistedAccess(collectionLocal),
                new[] { (BoundExpression)CreateHoistedAccess(indexLocal) },
                node.Iteration.ElementType);

            var statements = new List<BoundStatement>
            {
                CreateHoistedAssignment(collectionLocal, collection),
                CreateHoistedAssignment(indexLocal, CreateIntLiteral(0)),
                new BoundLabeledStatement(beginLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
                new BoundConditionalGotoStatement(
                    breakLabel,
                    CreateBinaryExpression(
                        _compilation,
                        SyntaxKind.GreaterThanOrEqualsToken,
                        CreateHoistedAccess(indexLocal),
                        new BoundMemberAccessExpression(CreateHoistedAccess(collectionLocal), lengthProperty)),
                    jumpIfTrue: true),
                new BoundBlockStatement(CreateLoopBodyStatements(node.Local, arrayAccess, body)),
                new BoundLabeledStatement(
                    continueLabel,
                    new BoundBlockStatement(new BoundStatement[]
                    {
                        CreateHoistedAssignment(
                            indexLocal,
                            CreateBinaryExpression(_compilation, SyntaxKind.PlusToken, CreateHoistedAccess(indexLocal), CreateIntLiteral(1))),
                        new BoundGotoStatement(beginLabel, isBackward: true),
                    })),
                new BoundLabeledStatement(breakLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
            };

            return new BoundBlockStatement(statements);
        }

        private BoundBlockStatement RewriteEnumeratorForStatement(BoundForStatement node)
        {
            var sourceLabels = TakePendingSourceLoopLabels();
            var getEnumeratorMethod = node.Iteration.GetEnumeratorMethod
                ?? throw new InvalidOperationException("Enumerator for-loop rewrite requires GetEnumerator.");
            var moveNextMethod = node.Iteration.MoveNextMethod
                ?? throw new InvalidOperationException("Enumerator for-loop rewrite requires MoveNext.");
            var currentGetter = node.Iteration.CurrentGetter
                ?? throw new InvalidOperationException("Enumerator for-loop rewrite requires Current getter.");

            var breakLabel = CreateLabel("for_break");
            var continueLabel = CreateLabel("for_continue");
            var beginLabel = CreateLabel("for_begin");

            var collection = VisitExpression(node.Collection) ?? node.Collection;
            var enumeratorLocal = CreateHoistedTempLocal("forEnumerator", getEnumeratorMethod.ReturnType);

            var firstState = _states.Count;
            BoundBlockStatement? finallyBlock = null;
            FinallyFrame? frame = null;
            if (UseDisposalUtilities.TryResolveUseDisposeMethod(_compilation, getEnumeratorMethod.ReturnType, preferAsync: false, out var disposeMethod, out var useAwait) && disposeMethod is not null)
            {
                var dispose = UseDisposalUtilities.CreateDisposeInvocationExpression(_compilation, CreateHoistedAccess(enumeratorLocal), disposeMethod, useAwait);
                finallyBlock = WrapFinallyBlock(new BoundBlockStatement([new BoundExpressionStatement(dispose)]));
                frame = new FinallyFrame { FinallyBlock = finallyBlock };
                _finallyStack.Push(frame);
            }
            BoundStatement body;
            try
            {
                body = VisitLoopBody(node.Body, breakLabel, continueLabel, sourceLabels);
            }
            finally
            {
                if (frame is not null)
                    _finallyStack.Pop();
            }

            var currentValue = new BoundInvocationExpression(
                currentGetter,
                Array.Empty<BoundExpression>(),
                receiver: CreateHoistedAccess(enumeratorLocal));

            var statements = new List<BoundStatement>
            {
                CreateHoistedAssignment(enumeratorLocal, CreateGetEnumeratorInvocation(collection, getEnumeratorMethod)),
                new BoundLabeledStatement(beginLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
                new BoundConditionalGotoStatement(
                    breakLabel,
                    ConvertIfNeeded(
                        _compilation,
                        new BoundInvocationExpression(
                            moveNextMethod,
                            Array.Empty<BoundExpression>(),
                            receiver: CreateHoistedAccess(enumeratorLocal)),
                        _boolType),
                    jumpIfTrue: false),
                new BoundBlockStatement(CreateLoopBodyStatements(node.Local, currentValue, body)),
                new BoundLabeledStatement(
                    continueLabel,
                    new BoundBlockStatement(new BoundStatement[]
                    {
                        new BoundGotoStatement(beginLabel, isBackward: true),
                    })),
                new BoundLabeledStatement(breakLabel, new BoundBlockStatement(Array.Empty<BoundStatement>())),
            };

            if (finallyBlock is null)
                return new BoundBlockStatement(statements);

            return new BoundBlockStatement([
                statements[0],
                ProtectIteratorRegion(new BoundTryStatement(new BoundBlockStatement(statements.Skip(1)), [], finallyBlock), firstState),
            ]);
        }

        private readonly record struct StateEntry(int Value, ILabelSymbol Label);

        private void HoistLocals(BoundBlockStatement body)
        {
            var collector = new HoistableLocalCollector();
            collector.VisitBlockStatement(body);

            foreach (var local in collector.Locals)
                EnsureHoistedLocal(local);
        }

        private string CreateHoistedLocalFieldName()
        {
            string candidate;
            do
            {
                candidate = $"_local{_nextHoistedLocalId++}";
            } while (!_hoistedFieldNames.Add(candidate));

            return candidate;
        }

        private SourceLocalSymbol CreateHoistedTempLocal(string nameHint, ITypeSymbol type)
        {
            var local = new SourceLocalSymbol(
                $"<{nameHint}>__iterator_{_nextHoistedLocalId}",
                type,
                isMutable: true,
                _moveNextMethod,
                _stateMachine,
                _stateMachine.ContainingNamespace,
                new[] { Location.None },
                Array.Empty<SyntaxReference>());

            EnsureHoistedLocal(local);
            return local;
        }

        private void EnsureHoistedLocal(ILocalSymbol local)
        {
            if (_hoistedLocals.ContainsKey(local))
                return;

            var type = local.Type ?? _compilation.ErrorTypeSymbol;
            var fieldName = CreateHoistedLocalFieldName();
            var field = _stateMachine.AddHoistedLocal(fieldName, type);
            _hoistedLocals.Add(local, field);
        }

        private BoundFieldAccess CreateHoistedAccess(ILocalSymbol local)
        {
            EnsureHoistedLocal(local);
            return new BoundFieldAccess(_hoistedLocals[local]);
        }

        private BoundAssignmentStatement CreateHoistedAssignment(ILocalSymbol local, BoundExpression value)
        {
            EnsureHoistedLocal(local);
            return new BoundAssignmentStatement(new BoundFieldAssignmentExpression(
                new BoundSelfExpression(_stateMachine),
                _hoistedLocals[local],
                value,
                _unitType));
        }

        private IEnumerable<BoundStatement> CreateLoopBodyStatements(ILocalSymbol? loopLocal, BoundExpression valueExpression, BoundStatement body)
        {
            if (loopLocal is not null)
            {
                var convertedValue = ConvertIfNeeded(_compilation, valueExpression, loopLocal.Type);
                yield return CreateHoistedAssignment(loopLocal, convertedValue);
            }

            yield return body;
        }

        private BoundExpression CreateRangeBreakCondition(BoundExpression current, BoundExpression end, bool positiveStep, bool upperExclusive)
        {
            var operatorKind = positiveStep
                ? upperExclusive ? SyntaxKind.GreaterThanOrEqualsToken : SyntaxKind.GreaterThanToken
                : upperExclusive ? SyntaxKind.LessThanOrEqualsToken : SyntaxKind.LessThanToken;

            return CreateBinaryExpression(_compilation, operatorKind, current, end);
        }

        private BoundExpression CreateZeroLiteral(ITypeSymbol type)
        {
            var targetType = type.UnwrapLiteralType() ?? type;
            return targetType.SpecialType switch
            {
                SpecialType.System_Int64 or SpecialType.System_UInt64 => new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0L, targetType),
                SpecialType.System_Single => new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0f, targetType),
                SpecialType.System_Double => new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0d, targetType),
                SpecialType.System_Decimal => new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0m, targetType),
                _ => new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 0, targetType),
            };
        }

        private BoundInvocationExpression CreateGetEnumeratorInvocation(BoundExpression collection, IMethodSymbol getEnumeratorMethod)
        {
            if (getEnumeratorMethod.IsExtensionMethod)
            {
                var arguments = new List<BoundExpression> { collection };
                arguments.AddRange(getEnumeratorMethod.Parameters.Skip(1).Select(parameter => (BoundExpression)new BoundDefaultValueExpression(parameter.Type)));
                return new BoundInvocationExpression(getEnumeratorMethod, arguments, receiver: null, extensionReceiver: collection);
            }

            var optionalArguments = getEnumeratorMethod.Parameters.Select(parameter => (BoundExpression)new BoundDefaultValueExpression(parameter.Type));
            return new BoundInvocationExpression(getEnumeratorMethod, optionalArguments, receiver: collection);
        }

        private sealed class HoistableLocalCollector : BoundTreeWalker
        {
            private readonly HashSet<ILocalSymbol> _locals = new(SymbolEqualityComparer.Default);

            public IEnumerable<ILocalSymbol> Locals => _locals;

            public override void VisitLocalDeclarationStatement(BoundLocalDeclarationStatement node)
            {
                foreach (var declarator in node.Declarators)
                    _locals.Add(declarator.Local);

                base.VisitLocalDeclarationStatement(node);
            }

            public override void VisitLocalAccess(BoundLocalAccess node)
            {
                _locals.Add(node.Local);
                base.VisitLocalAccess(node);
            }

            public override void VisitVariableExpression(BoundVariableExpression node)
            {
                _locals.Add(node.Variable);
                base.VisitVariableExpression(node);
            }

            public override void VisitLocalAssignmentExpression(BoundLocalAssignmentExpression node)
            {
                _locals.Add(node.Local);
                base.VisitLocalAssignmentExpression(node);
            }

            public override void VisitForStatement(BoundForStatement node)
            {
                if (node.Local is not null)
                    _locals.Add(node.Local);

                base.VisitForStatement(node);
            }
        }

        private sealed class FinallyFrame
        {
            public BoundBlockStatement? FinallyBlock { get; set; }
        }
    }
}
