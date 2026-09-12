using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

partial class BlockBinder
{
    private ImmutableArray<IMethodSymbol> LookupStaticMethodCandidates(string name, ITypeSymbol receiverType, Location location)
    {
        if (Compilation.Options.FrameworkProjectionMode == FrameworkProjectionMode.Standard &&
            FrameworkProjectionCatalog.TryGetStandard(receiverType, name, out _))
        {
            var resolution = FrameworkProjectionCatalog.ResolveStandardMethods(Compilation, receiverType, name);
            foreach (var failure in resolution.Failures)
                _diagnostics.ReportFrameworkProjectionUnavailable(failure.ProjectionId, failure.Reason, location);
            return resolution.Methods;
        }

        return new SymbolQuery(name, receiverType, IsStatic: true)
            .LookupMethods(this)
            .ToImmutableArray();
    }

    private ImmutableArray<IMethodSymbol> LookupInstanceMethodCandidates(string name, ITypeSymbol receiverType, Location location)
    {
        if (Compilation.Options.FrameworkProjectionMode == FrameworkProjectionMode.Standard &&
            FrameworkProjectionCatalog.TryGetStandard(receiverType, name, out _))
        {
            var resolution = FrameworkProjectionCatalog.ResolveStandardMethods(Compilation, receiverType, name);
            foreach (var failure in resolution.Failures)
                _diagnostics.ReportFrameworkProjectionUnavailable(failure.ProjectionId, failure.Reason, location);
            return resolution.Methods;
        }

        return new SymbolQuery(name, receiverType, IsStatic: false)
            .LookupMethods(this)
            .ToImmutableArray();
    }

    private ITypeSymbol EnsureSourceMemberSignatureDeclaredForExactLookup(ITypeSymbol receiverType, string memberName)
    {
        if (string.IsNullOrEmpty(memberName) ||
            Compilation.IsSourceNamespaceLookupDeclarationCompletionSuppressed ||
            receiverType is not INamedTypeSymbol namedReceiverType)
        {
            return receiverType;
        }

        Compilation.EnsureSourceTypeDeclarationsDeclared();

        var semanticModel = SemanticModel;
        if (semanticModel is null)
            return receiverType;

        var result = receiverType;
        for (INamedTypeSymbol? current = namedReceiverType; current is not null; current = current.BaseType)
        {
            if (!semanticModel.TryEnsureSourceTypeMemberSignatureDeclared(current, memberName, out var ensuredType))
                continue;

            if (SymbolEqualityComparer.Default.Equals(current, namedReceiverType))
                result = ensuredType;
        }

        return result;
    }

    private BoundErrorExpression InvocationError(
            BoundExpression? receiver,
            string methodName,
            BoundExpressionReason reason)
    {
        ImmutableArray<IMethodSymbol> candidates = default;
        ITypeSymbol? resultType = null;
        ISymbol? symbol = null;

        if (receiver is BoundNamespaceExpression nsReceiver)
        {
            var typeInNamespace = nsReceiver.Namespace
                .GetMembers(methodName)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            if (typeInNamespace is not null)
            {
                candidates = typeInNamespace.Constructors
                    .Where(static ctor => !ctor.IsStatic)
                    .ToImmutableArray();
                symbol = candidates.IsDefaultOrEmpty ? typeInNamespace : candidates[0];
                resultType = typeInNamespace;
            }
        }
        else if (receiver is BoundTypeExpression typeReceiver)
        {
            candidates = new SymbolQuery(methodName, typeReceiver.Type, IsStatic: true)
                .LookupMethods(this)
                .ToImmutableArray();

            if (!candidates.IsDefaultOrEmpty)
            {
                symbol = candidates[0];
                resultType = candidates[0].ReturnType;
            }
            else
            {
                symbol = typeReceiver.Type;
                resultType = typeReceiver.Type;
            }
        }
        else if (receiver is not null)
        {
            candidates = new SymbolQuery(methodName, receiver.Type, IsStatic: false)
                .LookupMethods(this)
                .ToImmutableArray();

            if (!candidates.IsDefaultOrEmpty)
            {
                symbol = candidates[0];
                resultType = candidates[0].ReturnType;
            }
            else
            {
                resultType = receiver.Type;
            }
        }
        else
        {
            candidates = new SymbolQuery(methodName)
                .LookupMethods(this)
                .ToImmutableArray();

            if (!candidates.IsDefaultOrEmpty)
            {
                symbol = candidates[0];
                resultType = candidates[0].ReturnType;
            }
        }

        return ErrorExpression(
            resultType ?? Compilation.ErrorTypeSymbol,
            symbol,
            reason,
            AsSymbolCandidates(candidates));
    }

    private BoundExpression BindInvocationOnMethodGroup(BoundMethodGroupExpression methodGroup, InvocationExpressionSyntax syntax)
        => _invocationResolver.BindInvocationOnMethodGroup(methodGroup, syntax);

    private sealed class InvocationResolver
    {
        private readonly BlockBinder _binder;
        private readonly Dictionary<InvocationCandidatePreparationCacheKey, InvocationCandidatePreparation> _candidatePreparationCache = new();
        private readonly Dictionary<InvocationCacheKey, BoundExpression> _successfulInvocationCache = new();

        public InvocationResolver(BlockBinder binder)
        {
            _binder = binder;
        }

        public BoundExpression BindInvocationOnMethodGroup(BoundMethodGroupExpression methodGroup, InvocationExpressionSyntax syntax)
        {
            var cacheKey = CreateCacheKey(methodGroup, syntax);
            if (cacheKey is { } key && _successfulInvocationCache.TryGetValue(key, out var cachedInvocation))
                return cachedInvocation;

            var preparation = PrepareInvocationCandidates(methodGroup, syntax);
            var boundArguments = _binder.BindInvocationArgumentsWithCandidateTargetTypes(
                preparation.CandidatesForArgumentBinding,
                syntax.ArgumentList.Arguments,
                out var hasErrors,
                methodGroup.Receiver,
                explicitTypeArguments: preparation.ExplicitTypeArguments);

            if (hasErrors || methodGroup.Receiver is { } receiver && IsErrorExpression(receiver))
            {
                _binder.ReportSuppressedLambdaDiagnostics(boundArguments);
                if (!HasArgumentBindingErrors(boundArguments) &&
                    !_binder.HasExistingArgumentErrors(syntax.ArgumentList.Arguments))
                {
                    _binder._diagnostics.ReportNoOverloadForMethod("method", preparation.MethodName, boundArguments.Length, syntax.GetLocation());
                }

                var selectedForError = methodGroup.SelectedMethod;
                var symbol = selectedForError ?? methodGroup.Methods.FirstOrDefault();
                var returnType = symbol?.ReturnType ?? _binder.Compilation.ErrorTypeSymbol;

                return _binder.ErrorExpression(
                    returnType,
                    symbol,
                    BoundExpressionReason.ArgumentBindingFailed,
                    AsSymbolCandidates(methodGroup.Methods));
            }

            var selected = methodGroup.SelectedMethod;

            if (selected is not null)
            {
                var inferred = OverloadResolver.ApplyTypeArgumentInference(selected, preparation.ExtensionReceiver, boundArguments, _binder.Compilation, _binder, preparation.ExplicitTypeArguments);
                if (inferred is not null)
                {
                    // If we still have unbound type parameters, skip the fast-path and fall back to full overload resolution.
                    if (selected.IsGenericMethod && selected.TypeParameters.Length > 0)
                    {
                        // Continue into normal overload resolution below.
                    }
                    else if (AreArgumentsCompatibleWithMethod(selected, boundArguments.Length, preparation.ExtensionReceiver, boundArguments))
                    {
                        var converted = _binder.ConvertInvocationArguments(
                            selected,
                            boundArguments,
                            preparation.ExtensionReceiver,
                            preparation.ReceiverSyntax,
                            syntax,
                            out var convertedExtensionReceiver);
                        _binder.ReportObsoleteIfNeeded(selected, syntax.Expression.GetLocation());
                        return CompleteSuccessfulInvocation(
                            cacheKey,
                            InvocationResolutionResult.Success(
                                selected,
                                converted,
                                convertedExtensionReceiver,
                                new BoundInvocationExpression(selected, converted, methodGroup.Receiver, convertedExtensionReceiver)));
                    }
                }
            }

            var resolution = OverloadResolver.ResolveOverload(
                methodGroup.Methods,
                boundArguments,
                _binder.Compilation,
                binder: _binder,
                receiver: preparation.ExtensionReceiver,
                canBindLambda: _binder.EnsureLambdaCompatible,
                callSyntax: syntax,
                explicitTypeArguments: preparation.ExplicitTypeArguments);

            if (resolution.Success)
            {
                var method = resolution.Method!;
                var convertedArgs = _binder.ConvertInvocationArguments(
                    method,
                    boundArguments,
                    preparation.ExtensionReceiver,
                    preparation.ReceiverSyntax,
                    syntax,
                    out var convertedExtensionReceiver);
                _binder.ReportObsoleteIfNeeded(method, syntax.Expression.GetLocation());
                return CompleteSuccessfulInvocation(
                    cacheKey,
                    InvocationResolutionResult.Success(
                        method,
                        convertedArgs,
                        convertedExtensionReceiver,
                        new BoundInvocationExpression(method, convertedArgs, methodGroup.Receiver, convertedExtensionReceiver)));
            }

            if (resolution.IsAmbiguous)
            {
                _binder._diagnostics.ReportCallIsAmbiguous(preparation.MethodName, resolution.AmbiguousCandidates, syntax.GetLocation());
                return _binder.ErrorExpression(
                    reason: BoundExpressionReason.Ambiguous,
                    candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
            }

            if (_binder.LookupType(preparation.MethodName) is INamedTypeSymbol typeFallback)
            {
                // Rebind arguments against ctor parameter types so target-typed member bindings (e.g. `.Ok`, `.Human`)
                // can be resolved even before overload resolution.
                return _binder.BindConstructorInvocation(typeFallback, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }

            if (_binder.ReportTypeArgumentConstraintFailureIfPresent(resolution, syntax.GetLocation()))
            {
                return _binder.ErrorExpression(
                    reason: BoundExpressionReason.OverloadResolutionFailed,
                    candidates: AsSymbolCandidates(
                        resolution.ConstraintFailureCandidate is { } rejected
                            ? ImmutableArray.Create(rejected)
                            : methodGroup.Methods));
            }

            _binder.ReportSuppressedLambdaDiagnostics(boundArguments);
            if (!HasArgumentBindingErrors(boundArguments) &&
                !_binder.HasExistingArgumentErrors(syntax.ArgumentList.Arguments))
                _binder._diagnostics.ReportNoOverloadForMethod("method", preparation.MethodName, boundArguments.Length, syntax.GetLocation());
            return _binder.ErrorExpression(
                reason: BoundExpressionReason.OverloadResolutionFailed,
                candidates: AsSymbolCandidates(methodGroup.Methods));
        }

        private InvocationCandidatePreparation PrepareInvocationCandidates(
            BoundMethodGroupExpression methodGroup,
            InvocationExpressionSyntax syntax)
        {
            var cacheKey = CreateCandidatePreparationCacheKey(methodGroup, syntax);
            if (cacheKey is { } key && _candidatePreparationCache.TryGetValue(key, out var cachedPreparation))
                return cachedPreparation;

            // Extract explicit method type arguments at the call site (if any), e.g. `items.CountItems<double>(2)`.
            // Bind these before argument binding so lambda arguments can be target-typed with the constructed delegate.
            var explicitTypeArguments = _binder.GetExplicitInvocationTypeArguments(syntax);
            var candidatesForArgumentBinding = _binder.FilterInvocationCandidatesForArgumentBinding(
                methodGroup.Methods,
                syntax.ArgumentList.Arguments,
                methodGroup.Receiver);

            var preparation = new InvocationCandidatePreparation(
                methodGroup.Methods[0].Name,
                candidatesForArgumentBinding,
                explicitTypeArguments,
                IsExtensionReceiver(methodGroup.Receiver) ? methodGroup.Receiver : null,
                GetInvocationReceiverSyntax(syntax) ?? syntax.Expression);

            if (cacheKey is { } newKey)
                _candidatePreparationCache[newKey] = preparation;

            return preparation;
        }

        private BoundExpression CompleteSuccessfulInvocation(InvocationCacheKey? cacheKey, InvocationResolutionResult result)
        {
            if (result.Cacheable && cacheKey is { } key)
                _successfulInvocationCache[key] = result.Expression;

            return result.Expression;
        }

        private InvocationCacheKey? CreateCacheKey(BoundMethodGroupExpression methodGroup, InvocationExpressionSyntax syntax)
        {
            if (syntax.SyntaxTree is null)
                return null;

            return new InvocationCacheKey(
                syntax.SyntaxTree,
                syntax.Span.Start,
                syntax.Span.Length,
                GetTypeKey(methodGroup.Receiver?.Type),
                GetTypeKey(_binder.GetScopedTargetType(syntax)),
                GetSymbolKey(methodGroup.SelectedMethod),
                string.Join("|", methodGroup.Methods.Select(GetSymbolKey)));
        }

        private InvocationCandidatePreparationCacheKey? CreateCandidatePreparationCacheKey(
            BoundMethodGroupExpression methodGroup,
            InvocationExpressionSyntax syntax)
        {
            if (syntax.SyntaxTree is null)
                return null;

            return new InvocationCandidatePreparationCacheKey(
                syntax.SyntaxTree,
                syntax.Span.Start,
                syntax.Span.Length,
                syntax.ArgumentList.Arguments.Count,
                GetTypeKey(methodGroup.Receiver?.Type),
                string.Join("|", methodGroup.Methods.Select(GetSymbolKey)));
        }

        private static string GetSymbolKey(ISymbol? symbol)
            => symbol?.GetShallowLookupIdentityKey() ?? string.Empty;

        private static string GetTypeKey(ITypeSymbol? type)
            => type?.GetShallowLookupIdentityKey() ?? string.Empty;

        private readonly record struct InvocationCacheKey(
            SyntaxTree SyntaxTree,
            int SpanStart,
            int SpanLength,
            string ReceiverTypeKey,
            string TargetTypeKey,
            string SelectedMethodKey,
            string CandidateMethodsKey);

        private readonly record struct InvocationCandidatePreparationCacheKey(
            SyntaxTree SyntaxTree,
            int SpanStart,
            int SpanLength,
            int ArgumentCount,
            string ReceiverTypeKey,
            string CandidateMethodsKey);

        private readonly record struct InvocationCandidatePreparation(
            string MethodName,
            ImmutableArray<IMethodSymbol> CandidatesForArgumentBinding,
            ImmutableArray<ITypeSymbol> ExplicitTypeArguments,
            BoundExpression? ExtensionReceiver,
            SyntaxNode ReceiverSyntax);

        private readonly record struct InvocationResolutionResult(
            IMethodSymbol? SelectedMethod,
            BoundExpression[] ConvertedArguments,
            BoundExpression? ConvertedExtensionReceiver,
            BoundExpression Expression,
            bool Cacheable)
        {
            public static InvocationResolutionResult Success(
                IMethodSymbol selectedMethod,
                BoundExpression[] convertedArguments,
                BoundExpression? convertedExtensionReceiver,
                BoundExpression expression)
                => new(
                    selectedMethod,
                    convertedArguments,
                    convertedExtensionReceiver,
                    expression,
                    Cacheable: true);
        }
    }

    private ImmutableArray<ITypeSymbol> GetExplicitInvocationTypeArguments(InvocationExpressionSyntax syntax)
    {
        GenericNameSyntax? genericName = syntax.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax memberGeneric } => memberGeneric,
            GenericNameSyntax invocationGeneric => invocationGeneric,
            _ => null
        };

        if (genericName is null)
            return ImmutableArray<ITypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(genericName.TypeArgumentList.Arguments.Count);
        foreach (var argument in genericName.TypeArgumentList.Arguments)
        {
            var boundType = BindTypeSyntaxAsExpression(argument.Type);
            builder.Add(boundType.Type ?? Compilation.ErrorTypeSymbol);
        }

        return builder.ToImmutable();
    }

    private ImmutableArray<IMethodSymbol> FilterInvocationCandidatesForArgumentBinding(
        ImmutableArray<IMethodSymbol> methods,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        BoundExpression? receiver = null)
    {
        if (methods.IsDefaultOrEmpty)
            return methods;

        var argCount = arguments.Count;

        // Best-effort filtering: drop candidates that cannot accept the given argument count.
        // This is important for target-typed member bindings inside arguments (e.g. `.Public | .Static`)
        // when overload sets contain both parameterless and parameterful candidates.
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>(methods.Length);

        foreach (var method in methods)
        {
            if (method is null)
                continue;

            if (method.IsExtensionMethod &&
                receiver is not null &&
                method.Parameters.Length > 0)
            {
                var extensionReceiverType = method.Parameters[0].Type;
                var hasOpenReceiverTypeParameters = ContainsAnyTypeParameter(extensionReceiverType, method.TypeParameters);
                if (!hasOpenReceiverTypeParameters)
                {
                    var conversion = Compilation.ClassifyConversion(receiver.Type, extensionReceiverType);
                    if (!conversion.Exists || !conversion.IsImplicit)
                        continue;
                }
            }

            var parameters = method.Parameters;

            // For extension methods, the receiver occupies parameter 0.
            var start = method.IsExtensionMethod ? 1 : 0;
            var effectiveCount = parameters.Length - start;

            // Determine required parameter count (excluding optional parameters).
            int required = 0;
            for (int i = start; i < parameters.Length; i++)
            {
                if (!parameters[i].IsOptional)
                    required++;
            }

            var hasParamsArray = effectiveCount > 0 && parameters[^1].IsVarParams;

            bool accepts;
            if (hasParamsArray)
            {
                // Params: any count >= required-1 (because the params array itself can take 0+ arguments)
                // but still must satisfy required non-optional parameters.
                accepts = argCount >= required - 1;
            }
            else
            {
                accepts = argCount >= required && argCount <= effectiveCount;
            }

            if (accepts)
                builder.Add(method);
        }

        // If filtering removed everything, keep original to avoid hiding diagnostics.
        return builder.Count > 0 ? builder.ToImmutable() : methods;
    }

    private BoundArgument[] BindInvocationArgumentsWithCandidateTargetTypes(
        ImmutableArray<IMethodSymbol> methods,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        out bool hasErrors,
        BoundExpression? receiver = null,
        ITypeSymbol? pipeReceiverType = null,
        ImmutableArray<ITypeSymbol> explicitTypeArguments = default)
    {
        // Bind invocation arguments while supplying a best-effort target type.
        // This is important for target-typed member bindings inside argument position,
        // e.g. `format(.Ok(42))`, where `.Ok(42)` needs the parameter type to resolve.
        //
        // pipeReceiverType: when non-null, the method candidates are being called via the pipe
        // operator and `pipeReceiverType` is the type of the left-hand side of the pipe.  For
        // non-extension pipe methods parameter[0] is the implicit pipe source, so each explicit
        // argument at index i maps to parameter[i+1].
        //
        // Generic type inference (C#-style phase 1 + phase 2):
        // Before binding any argument with a target type we run a pre-inference pass that binds
        // every non-lambda argument naturally (no target type). The resulting types are unified
        // against the matching parameter types to build a preliminary {T -> int, U -> string, ...}
        // substitution map. Those substitutions are then applied to every target type before use,
        // so that lambdas see concrete types (int -> bool) instead of open generics (T -> bool).

        hasErrors = false;

        // Pre-inference pass: infer type parameters from non-lambda arguments.
        var preInferredSubstitutions = TryPreInferTypeArguments(methods, arguments, receiver, pipeReceiverType, explicitTypeArguments);

        // Collect all method type parameters so we can detect unresolved ones.
        var allMethodTypeParams = CollectDistinctMethodTypeParameters(methods);

        var boundArguments = new BoundArgument[arguments.Count];

        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg is null || arg.Expression is null)
            {
                hasErrors = true;
                boundArguments[i] = new BoundArgument(
                    new BoundErrorExpression(Compilation.ErrorTypeSymbol, null, BoundExpressionReason.ArgumentBindingFailed),
                    RefKind.None,
                    null,
                    arg,
                    arg?.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken);
                continue;
            }

            // Bind invocation arguments while supplying a best-effort target type.
            // Positional args use the (common) parameter type at the given index.
            // Named args use the (common) parameter type for that name.
            ITypeSymbol? targetType = null;
            var argumentRefKind = RefKind.None;

            if (arg.NameColon is null)
            {
                targetType = TryGetCommonPositionalParameterType(methods, i, receiver);
                argumentRefKind = TryGetCommonPositionalParameterRefKind(methods, i, receiver);
            }
            else
            {
                var argName = arg.NameColon.Name?.Identifier.ValueText;
                if (!string.IsNullOrEmpty(argName))
                {
                    targetType = TryGetCommonNamedParameterType(methods, argName, receiver);
                    argumentRefKind = TryGetCommonNamedParameterRefKind(methods, argName, receiver);
                }
            }

            // For callable arguments where candidates disagree on the delegate type
            // (TryGetCommonPositionalParameterType returns null), fall back to the first
            // candidate that provides a delegate type for this argument position.  This
            // handles cases like [1,2,3].ToDictionary(x => x, y => y) where overloads agree
            // on input parameter types but differ on return-type type parameters,
            // and method-group calls like shipmentWeights.Select(Compute).
            var syntaxRefKind = arg.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None,
            };

            if (targetType is null &&
                TryGetTargetTypedUnionCaseName(arg.Expression, out var unionCaseName))
            {
                targetType = TryGetFirstUnionCaseParameterType(methods, i, receiver, pipeReceiverType, arg, unionCaseName);
            }

            BoundExpression? preboundExpression = null;
            if (targetType is null &&
                arg.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or GenericNameSyntax)
            {
                BoundExpression naturalBoundExpression;
                using (_diagnostics.CreateNonReportingScope())
                    naturalBoundExpression = BindExpression(arg.Expression);

                RemoveCachedBoundNode(arg.Expression);

                if (naturalBoundExpression is not BoundMethodGroupExpression &&
                    !HasExpressionErrors(naturalBoundExpression))
                {
                    preboundExpression = BindInvocationArgumentExpression(arg, targetType: null, syntaxRefKind);
                }
            }

            if (targetType is null &&
                preboundExpression is null &&
                arg.Expression is FunctionExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax or GenericNameSyntax)
            {
                targetType = TryGetFirstDelegateParameterType(methods, i, receiver, pipeReceiverType);
            }

            if (targetType is null &&
                arg.Expression is CollectionExpressionSyntax or ArrayExpressionSyntax)
            {
                // Keep the natural element types when a generic enumerable overload can
                // infer from them. Choosing the first array target here can widen elements
                // before overload resolution (for example, Task<T> to Task in Task.WhenAll).
                if (!HasGenericEnumerableCollectionCandidate(methods, i, receiver, pipeReceiverType))
                    targetType = TryGetFirstCollectionParameterType(methods, i, receiver, pipeReceiverType);
            }

            // Apply pre-inferred type-parameter substitutions to the target type, then
            // discard the target type if it still contains unresolved type parameters —
            // passing an open generic as a hint causes wrong inference.
            if (targetType is not null && preInferredSubstitutions.Count > 0)
                targetType = SubstituteTypeParameters(targetType, preInferredSubstitutions);

            // For lambda arguments, only null out the target type when the DELEGATE INPUT
            // PARAMETER TYPES still contain unresolved type parameters.  The return-type
            // position can be left open — the lambda binder already handles that correctly by
            // inferring the return type from the body (see BlockBinder.Lambda.cs line ~541).
            if (targetType is not null &&
                arg.Expression is FunctionExpressionSyntax &&
                ContainsAnyTypeParameterInDelegateInputParams(targetType, allMethodTypeParams))
            {
                targetType = null;
            }

            if (targetType is not null && methods.Length == 1)
            {
                var hasUnresolved = CanUseOpenDelegateReturnTypeHint(arg.Expression)
                    ? ContainsAnyTypeParameterInDelegateInputParams(targetType, allMethodTypeParams)
                    : ContainsAnyTypeParameter(targetType, allMethodTypeParams);
                if (hasUnresolved)
                    targetType = null;
            }

            if (targetType is null)
                RecordLambdaTargetsForArgument(i, arg.Expression);

            var boundExpr = preboundExpression ?? BindInvocationArgumentExpression(arg, targetType, syntaxRefKind);

            if (targetType is not null && HasExpressionErrors(boundExpr))
            {
                // Target-typed binding can fail for otherwise-valid expressions (e.g., nested union
                // case construction or method-group conversion). Retry without the target type
                // before treating it as an error.
                RemoveCachedBoundNode(arg.Expression);
                var naturalBoundExpr = BindInvocationArgumentExpression(arg, targetType: null, syntaxRefKind);
                if (!HasExpressionErrors(naturalBoundExpr))
                    boundExpr = naturalBoundExpr;
            }

            if (HasExpressionErrors(boundExpr) && boundExpr is not BoundMethodGroupExpression)
                hasErrors = true;

            var name = arg.NameColon?.Name.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
                name = null;

            var isSpread = arg.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken;
            boundArguments[i] = new BoundArgument(boundExpr, syntaxRefKind != RefKind.None ? syntaxRefKind : argumentRefKind, name, arg, isSpread);
        }


        return boundArguments;
        void RecordLambdaTargetsForArgument(int argumentIndex, ExpressionSyntax expression)
        {
            if (expression is not FunctionExpressionSyntax)
                return;

            var receiverTypeForLambda = pipeReceiverType ?? receiver?.Type;
            var extensionReceiverImplicit = receiver is not null && methods.All(static method => method.IsExtensionMethod);
            var pipeReceiverImplicit = pipeReceiverType is not null && methods.All(static method => !method.IsExtensionMethod);
            var callSiteArgumentCount = pipeReceiverImplicit ? arguments.Count + 1 : arguments.Count;

            var lambdaMethods = FilterMethodsForLambda(
                methods,
                argumentIndex,
                expression,
                extensionReceiverImplicit,
                callSiteArgumentCount);

            if (lambdaMethods.IsDefaultOrEmpty)
                return;

            RecordLambdaTargets(
                expression,
                lambdaMethods,
                argumentIndex,
                extensionReceiverImplicit,
                receiverTypeForLambda,
                pipeReceiverImplicit,
                preInferredSubstitutions);
        }

        static ImmutableArray<ITypeParameterSymbol> CollectDistinctMethodTypeParameters(ImmutableArray<IMethodSymbol> candidates)
        {
            if (candidates.IsDefaultOrEmpty)
                return ImmutableArray<ITypeParameterSymbol>.Empty;

            var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>();
            foreach (var method in candidates)
            {
                if (method is null)
                    continue;

                var typeParameters = method.TypeParameters;
                if (typeParameters.IsDefaultOrEmpty)
                    continue;

                foreach (var typeParameter in typeParameters)
                {
                    var exists = builder.Any(existing => SymbolEqualityComparer.Default.Equals(existing, typeParameter));
                    if (!exists)
                        builder.Add(typeParameter);
                }
            }

            return builder.ToImmutable();
        }

        ITypeSymbol? TryGetCommonPositionalParameterType(ImmutableArray<IMethodSymbol> methods, int argumentIndex, BoundExpression? invocationReceiver)
        {
            if (methods.IsDefaultOrEmpty)
                return null;

            ITypeSymbol? common = null;
            bool hasCommon = false;

            foreach (var method in methods)
            {
                if (method is null)
                    continue;

                // For extension methods, the receiver occupies parameter 0.
                // For non-extension pipe methods (pipeReceiverType != null), the pipe source
                // also occupies parameter 0, so explicit argument i maps to parameter i+1.
                var parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null)
                    ? argumentIndex + 1
                    : argumentIndex;

                if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                    return null;

                var type = GetInvocationParameterTypeForArgumentBinding(method, parameterIndex, invocationReceiver, pipeReceiverType);
                if (ShouldUseExpandedParamsElementTarget(method, parameterIndex, arguments.Count, pipeReceiverType) &&
                    TryGetVarParamsElementTypeForArgumentBinding(type, out var elementType))
                {
                    type = elementType;
                }

                if (!hasCommon)
                {
                    common = type;
                    hasCommon = true;
                    continue;
                }

                if (!SymbolEqualityComparer.Default.Equals(common, type))
                    return null;
            }

            return hasCommon ? common : null;
        }

        bool ShouldUseExpandedParamsElementTarget(
            IMethodSymbol method,
            int parameterIndex,
            int argumentCount,
            ITypeSymbol? pipeSourceType)
        {
            if (method.Parameters.IsDefaultOrEmpty ||
                parameterIndex != method.Parameters.Length - 1 ||
                !method.Parameters[parameterIndex].IsVarParams)
            {
                return false;
            }

            var firstVisibleParameter = method.IsExtensionMethod || pipeSourceType is not null ? 1 : 0;
            var fixedVisibleParameterCount = Math.Max(0, method.Parameters.Length - firstVisibleParameter - 1);
            return argumentCount > fixedVisibleParameterCount + 1;
        }

        bool TryGetVarParamsElementTypeForArgumentBinding(ITypeSymbol parameterType, out ITypeSymbol elementType)
        {
            parameterType = parameterType.GetNonNullableType();

            if (parameterType is IArrayTypeSymbol { Rank: 1 } arrayType)
            {
                elementType = arrayType.ElementType;
                return true;
            }

            if (parameterType is INamedTypeSymbol namedType &&
                TryGetIEnumerableElementType(namedType, out var enumerableElementType))
            {
                elementType = enumerableElementType;
                return true;
            }

            elementType = Compilation.ErrorTypeSymbol;
            return false;
        }

        RefKind TryGetCommonPositionalParameterRefKind(
            ImmutableArray<IMethodSymbol> methods,
            int argumentIndex,
            BoundExpression? invocationReceiver)
        {
            if (methods.IsDefaultOrEmpty)
                return RefKind.None;

            RefKind? common = null;

            foreach (var method in methods)
            {
                if (method is null)
                    continue;

                var parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null)
                    ? argumentIndex + 1
                    : argumentIndex;

                if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                    return RefKind.None;

                var refKind = method.Parameters[parameterIndex].RefKind;
                if (common is null)
                {
                    common = refKind;
                    continue;
                }

                if (common.Value != refKind)
                    return RefKind.None;
            }

            return common ?? RefKind.None;
        }

        ITypeSymbol? TryGetCommonNamedParameterType(ImmutableArray<IMethodSymbol> methods, string argumentName, BoundExpression? invocationReceiver)
        {
            if (methods.IsDefaultOrEmpty)
                return null;

            ITypeSymbol? common = null;
            bool hasCommon = false;

            foreach (var method in methods)
            {
                if (method is null)
                    continue;

                // Named arguments bind by parameter name.
                // Use OrdinalIgnoreCase: Raven convention declares record parameters as PascalCase
                // (e.g. Code: ErrorCode) but call sites use camelCase labels (e.g. code:).
                var parameter = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, argumentName, StringComparison.OrdinalIgnoreCase));
                if (parameter is null)
                    return null;

                var parameterIndex = method.Parameters.IndexOf(parameter);
                if (parameterIndex < 0)
                    return null;

                var type = GetInvocationParameterTypeForArgumentBinding(method, parameterIndex, invocationReceiver);

                if (!hasCommon)
                {
                    common = type;
                    hasCommon = true;
                    continue;
                }

                if (!SymbolEqualityComparer.Default.Equals(common, type))
                    return null;
            }

            return hasCommon ? common : null;
        }

        ITypeSymbol? TryGetFirstUnionCaseParameterType(
            ImmutableArray<IMethodSymbol> methods,
            int argumentIndex,
            BoundExpression? invocationReceiver,
            ITypeSymbol? pipeSourceType,
            ArgumentSyntax argument,
            string caseName)
        {
            if (methods.IsDefaultOrEmpty)
                return null;

            foreach (var method in methods)
            {
                if (method is null)
                    continue;

                int parameterIndex;
                if (argument.NameColon is not null)
                {
                    var argumentName = argument.NameColon.Name.Identifier.ValueText;
                    var parameter = method.Parameters.FirstOrDefault(p =>
                        string.Equals(p.Name, argumentName, StringComparison.OrdinalIgnoreCase));
                    if (parameter is null)
                        continue;

                    parameterIndex = method.Parameters.IndexOf(parameter);
                }
                else
                {
                    parameterIndex = (method.IsExtensionMethod || pipeSourceType is not null)
                        ? argumentIndex + 1
                        : argumentIndex;
                }

                if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                    continue;

                var type = GetInvocationParameterTypeForArgumentBinding(method, parameterIndex, invocationReceiver, pipeSourceType);
                var targetType = type.UnwrapLiteralType() ?? type;
                targetType = UnwrapAlias(UnwrapTaskLikeTargetType(targetType));

                var union = (targetType as INamedTypeSymbol)?.TryGetUnion()
                    ?? (targetType as INamedTypeSymbol)?.TryGetUnionCase()?.Union;

                if (union is not null && union.Variants.Any(@case => @case.Name == caseName))
                    return type;
            }

            return null;
        }

        RefKind TryGetCommonNamedParameterRefKind(ImmutableArray<IMethodSymbol> methods, string argumentName, BoundExpression? invocationReceiver)
        {
            if (methods.IsDefaultOrEmpty)
                return RefKind.None;

            RefKind? common = null;

            foreach (var method in methods)
            {
                if (method is null)
                    continue;

                var parameter = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, argumentName, StringComparison.OrdinalIgnoreCase));
                if (parameter is null)
                    return RefKind.None;

                if (common is null)
                {
                    common = parameter.RefKind;
                    continue;
                }

                if (common.Value != parameter.RefKind)
                    return RefKind.None;
            }

            return common ?? RefKind.None;
        }
    }

    private ITypeSymbol GetInvocationParameterTypeForArgumentBinding(
        IMethodSymbol method,
        int parameterIndex,
        BoundExpression? receiver,
        ITypeSymbol? pipeReceiverType = null)
    {
        var parameterType = method.Parameters[parameterIndex].Type;

        if (method.IsExtensionMethod)
        {
            if (receiver is null || receiver.Type is null || method.Parameters.IsDefaultOrEmpty)
                return parameterType;

            var extensionReceiverType = method.GetExtensionReceiverType() ?? method.Parameters[0].Type;
            if (extensionReceiverType is null || extensionReceiverType.TypeKind == TypeKind.Error)
                return parameterType;

            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!TryUnifyExtensionReceiverType(extensionReceiverType, receiver.Type, substitutions))
                return parameterType;

            return SubstituteTypeParameters(parameterType, substitutions);
        }

        // For non-extension pipe methods with unbound type parameters, infer the substitution
        // by unifying parameter[0] (the implicit pipe source parameter) with the actual pipe
        // receiver type — mirroring the extension-method path above.
        if (pipeReceiverType is not null && !method.TypeParameters.IsDefaultOrEmpty && !method.Parameters.IsDefaultOrEmpty)
        {
            var firstParamType = method.Parameters[0].Type;
            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (TryUnifyExtensionReceiverType(firstParamType, pipeReceiverType, substitutions))
                return SubstituteTypeParameters(parameterType, substitutions);
        }

        // For ordinary instance methods on generic receivers, partially bind the method's
        // containing type from the concrete receiver so higher-order arguments like
        // `Result<T, E>.MapError(error => ...)` can see `E` as the lambda input type.
        if (receiver?.Type is not null &&
            !method.IsExtensionMethod &&
            method.ContainingType is INamedTypeSymbol containingType &&
            !TypeSubstitution.GetShallowTypeArguments(containingType).IsDefaultOrEmpty)
        {
            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (TryUnifyExtensionReceiverType(containingType, receiver.Type, substitutions))
                return SubstituteTypeParameters(parameterType, substitutions);
        }

        return parameterType;
    }

    private static bool TryGetTargetTypedUnionCaseName(ExpressionSyntax expression, out string caseName)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                caseName = identifier.Identifier.ValueText;
                return !string.IsNullOrEmpty(caseName);
            case MemberBindingExpressionSyntax memberBinding:
                caseName = memberBinding.Name.Identifier.ValueText;
                return !string.IsNullOrEmpty(caseName);
            case InvocationExpressionSyntax { Expression: IdentifierNameSyntax identifier }:
                caseName = identifier.Identifier.ValueText;
                return !string.IsNullOrEmpty(caseName);
            case InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax memberBinding }:
                caseName = memberBinding.Name.Identifier.ValueText;
                return !string.IsNullOrEmpty(caseName);
            default:
                caseName = string.Empty;
                return false;
        }
    }

    private static bool TryUnifyExtensionReceiverType(
        ITypeSymbol parameterType,
        ITypeSymbol argumentType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        parameterType = NormalizeTypeForExtensionInference(parameterType);
        argumentType = NormalizeTypeForExtensionInference(argumentType);

        if (parameterType is ITypeParameterSymbol typeParameter)
            return TryRecordExtensionSubstitution(typeParameter, argumentType, substitutions);

        if (parameterType is INamedTypeSymbol paramNamed)
        {
            if (argumentType is INamedTypeSymbol argNamed)
            {
                if (TryUnifyNamedType(paramNamed, argNamed, substitutions))
                    return true;

                foreach (var iface in argNamed.AllInterfaces)
                {
                    if (TryUnifyNamedType(paramNamed, iface, substitutions))
                        return true;
                }

                for (var baseType = argNamed.BaseType; baseType is not null; baseType = baseType.BaseType)
                {
                    if (TryUnifyNamedType(paramNamed, baseType, substitutions))
                        return true;
                }

                return false;
            }

            if (argumentType is IArrayTypeSymbol arrayArgument)
            {
                if (TryUnifyArrayLike(paramNamed, arrayArgument, substitutions))
                    return true;

                foreach (var iface in arrayArgument.AllInterfaces)
                {
                    if (TryUnifyNamedType(paramNamed, iface, substitutions))
                        return true;
                }

                return false;
            }

            if (Conversion.IsNullable(argumentType))
            {
                var plainArgument = argumentType.GetNonNullableType();
                if (plainArgument is INamedTypeSymbol plainNamed && TryUnifyNamedType(paramNamed, plainNamed, substitutions))
                    return true;

                foreach (var iface in plainArgument.AllInterfaces)
                {
                    if (TryUnifyNamedType(paramNamed, iface, substitutions))
                        return true;
                }

                return false;
            }
        }

        if (parameterType is IArrayTypeSymbol paramArray && argumentType is IArrayTypeSymbol argArray)
            return TryUnifyExtensionReceiverType(paramArray.ElementType, argArray.ElementType, substitutions);

        if (Conversion.IsNullable(parameterType))
        {
            var plainParameter = parameterType.GetNonNullableType();

            if (Conversion.IsNullable(argumentType))
                return TryUnifyExtensionReceiverType(plainParameter, argumentType.GetNonNullableType(), substitutions);

            if (!argumentType.IsValueType)
                return TryUnifyExtensionReceiverType(plainParameter, argumentType, substitutions);

            return false;
        }

        return SymbolEqualityComparer.Default.Equals(parameterType, argumentType);

        static bool TryUnifyNamedType(
            INamedTypeSymbol parameterNamed,
            INamedTypeSymbol argumentNamed,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
        {
            var parameterDefinition = TypeSubstitution.GetDefinitionForSubstitution(parameterNamed);
            var argumentDefinition = TypeSubstitution.GetDefinitionForSubstitution(argumentNamed);

            if (!SymbolEqualityComparer.Default.Equals(parameterDefinition, argumentDefinition))
                return false;

            var parameterArguments = TypeSubstitution.GetShallowTypeArguments(parameterNamed);
            var argumentArguments = TypeSubstitution.GetShallowTypeArguments(argumentNamed);

            if (parameterArguments.IsDefault || argumentArguments.IsDefault || parameterArguments.Length != argumentArguments.Length)
                return false;

            for (int i = 0; i < parameterArguments.Length; i++)
            {
                if (!TryUnifyExtensionReceiverType(parameterArguments[i], argumentArguments[i], map))
                    return false;
            }

            return true;
        }

        static bool TryUnifyArrayLike(
            INamedTypeSymbol parameterNamed,
            IArrayTypeSymbol argumentArray,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
        {
            var constructedFrom = parameterNamed.ConstructedFrom ?? parameterNamed;

            if (constructedFrom.SpecialType is SpecialType.System_Collections_Generic_IEnumerable_T or
                SpecialType.System_Collections_Generic_ICollection_T or
                SpecialType.System_Collections_Generic_IList_T ||
                IsGenericCollectionInterface(parameterNamed, "IReadOnlyCollection") ||
                IsGenericCollectionInterface(parameterNamed, "IReadOnlyList"))
            {
                return TryUnifyExtensionReceiverType(parameterNamed.TypeArguments[0], argumentArray.ElementType, map);
            }

            return false;
        }

        static bool IsGenericCollectionInterface(INamedTypeSymbol parameterNamed, string interfaceName)
        {
            var definition = parameterNamed.ConstructedFrom ?? parameterNamed;

            if (!string.Equals(definition.Name, interfaceName, StringComparison.Ordinal))
                return false;

            var ns = definition.ContainingNamespace?.ToDisplayString();
            return string.Equals(ns, "System.Collections.Generic", StringComparison.Ordinal);
        }
    }

    private static bool TryRecordExtensionSubstitution(
        ITypeParameterSymbol typeParameter,
        ITypeSymbol argumentType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        argumentType = NormalizeTypeForExtensionInference(argumentType);

        if (substitutions.TryGetValue(typeParameter, out var existing))
        {
            existing = NormalizeTypeForExtensionInference(existing);

            if (SymbolEqualityComparer.Default.Equals(existing, argumentType))
                return true;

            return false;
        }

        substitutions[typeParameter] = argumentType;
        return true;
    }

    private static ITypeSymbol NormalizeTypeForExtensionInference(ITypeSymbol type)
    {
        return type switch
        {
            LiteralTypeSymbol literal => literal.UnderlyingType,
            IAddressTypeSymbol addressType => NormalizeTypeForExtensionInference(addressType.ReferencedType),
            RefTypeSymbol refType => NormalizeTypeForExtensionInference(refType.ElementType),
            _ => type
        };
    }

    private static ITypeSymbol SubstituteTypeParameters(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (type is ITypeParameterSymbol parameter &&
            substitutions.TryGetValue(parameter, out var replacement))
        {
            return replacement;
        }

        if (type is ITypeParameterSymbol unmatchedParameter &&
            TypeSubstitution.TryGetEquivalentTypeParameterSubstitution(unmatchedParameter, substitutions, out var equivalentReplacement))
        {
            return equivalentReplacement;
        }

        if (type is NullableTypeSymbol nullableType)
        {
            var substituted = SubstituteTypeParameters(nullableType.UnderlyingType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, nullableType.UnderlyingType)
                ? type
                : substituted.ApplySubstitutedNullability(nullableType);
        }

        if (type is RefTypeSymbol refTypeType)
        {
            var substituted = SubstituteTypeParameters(refTypeType.ElementType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, refTypeType.ElementType)
                ? type
                : new RefTypeSymbol(substituted);
        }

        if (type is IAddressTypeSymbol addressType)
        {
            var substituted = SubstituteTypeParameters(addressType.ReferencedType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, addressType.ReferencedType)
                ? type
                : new AddressTypeSymbol(substituted);
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            var substituted = SubstituteTypeParameters(arrayType.ElementType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, arrayType.ElementType)
                ? type
                : new ArrayTypeSymbol(arrayType.BaseType, substituted, arrayType.ContainingSymbol, arrayType.ContainingType, arrayType.ContainingNamespace, [], arrayType.Rank, arrayType.FixedLength);
        }

        if (type is INamedTypeSymbol namedType)
        {
            var typeArguments = TypeSubstitution.GetShallowTypeArguments(namedType);
            if (typeArguments.IsDefaultOrEmpty)
                return type;

            var substituted = new ITypeSymbol[typeArguments.Length];
            var changed = false;

            for (int i = 0; i < typeArguments.Length; i++)
            {
                substituted[i] = SubstituteTypeParameters(typeArguments[i], substitutions);
                changed |= !SymbolEqualityComparer.Default.Equals(substituted[i], typeArguments[i]);
            }

            if (changed)
            {
                var definition = TypeSubstitution.GetDefinitionForSubstitution(namedType);
                return definition.Construct(substituted);
            }
        }

        return type;
    }
    /// <summary>
    /// Phase-1 generic type inference: bind every non-lambda argument naturally (no target type)
    /// and unify its type against the corresponding parameter type to build a
    /// <c>{ T → int, U → string, … }</c> substitution map.
    ///
    /// This mirrors C#'s two-phase inference: infer type parameters from fixed arguments first,
    /// then use the inferred types when target-typing lambda arguments.
    /// </summary>
    private Dictionary<ITypeParameterSymbol, ITypeSymbol> TryPreInferTypeArguments(
        ImmutableArray<IMethodSymbol> methods,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        BoundExpression? receiver,
        ITypeSymbol? pipeReceiverType,
        ImmutableArray<ITypeSymbol> explicitTypeArguments = default)
    {
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);

        if (!explicitTypeArguments.IsDefaultOrEmpty)
        {
            foreach (var candidateMethod in methods)
            {
                if (candidateMethod.TypeParameters.IsDefaultOrEmpty)
                    continue;

                var arity = candidateMethod.TypeParameters.Length;
                if (explicitTypeArguments.Length > arity)
                    continue;

                var offset = arity - explicitTypeArguments.Length;
                for (int i = 0; i < explicitTypeArguments.Length; i++)
                {
                    var explicitType = explicitTypeArguments[i];
                    if (explicitType.TypeKind != TypeKind.Error)
                        substitutions[candidateMethod.TypeParameters[offset + i]] = explicitType;
                }
            }
        }

        // Only pre-infer when there is a single candidate with type parameters.
        // With multiple overloads the "natural" types may not pick the right one.
        if (methods.IsDefaultOrEmpty || methods.Length != 1)
            return substitutions;

        var method = methods[0];
        if (method.TypeParameters.IsDefaultOrEmpty)
            return substitutions;

        // Seed from the implicit first argument (pipe source or extension receiver).
        if (pipeReceiverType is not null && !method.Parameters.IsDefaultOrEmpty)
        {
            TryUnifyExtensionReceiverType(method.Parameters[0].Type, pipeReceiverType, substitutions);
        }
        else if (method.IsExtensionMethod && receiver is not null && !method.Parameters.IsDefaultOrEmpty)
        {
            var extensionReceiverType = method.GetExtensionReceiverType() ?? method.Parameters[0].Type;
            if (extensionReceiverType is not null &&
                extensionReceiverType.TypeKind != TypeKind.Error &&
                receiver.Type is not null &&
                receiver.Type.TypeKind != TypeKind.Error)
            {
                TryUnifyExtensionReceiverType(extensionReceiverType, receiver.Type, substitutions);
            }
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];

            // Skip lambda arguments — they consume the inferred types, not the other way around.
            if (arg.Expression is FunctionExpressionSyntax)
                continue;

            int parameterIndex;
            if (arg.NameColon is not null)
            {
                var argName = arg.NameColon.Name.Identifier.ValueText;
                var param = method.Parameters.FirstOrDefault(p =>
                    string.Equals(p.Name, argName, StringComparison.OrdinalIgnoreCase));
                if (param is null) continue;
                parameterIndex = method.Parameters.IndexOf(param);
            }
            else
            {
                parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null) ? i + 1 : i;
            }

            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var parameter = method.Parameters[parameterIndex];
            var parameterType = parameter.Type;

            // Only bother if this parameter type involves type parameters.
            if (!ContainsAnyTypeParameter(parameterType, method.TypeParameters))
                continue;

            // Bind the argument without any target type to get its natural type.
            var naturalBound = BindExpression(arg.Expression);
            if (naturalBound.Type is null || naturalBound.Type.TypeKind == TypeKind.Error)
                continue;

            var argumentType = naturalBound.Type;
            if (parameter.RefKind.IsByRef)
            {
                parameterType = parameter.GetByRefElementType();
                argumentType = argumentType switch
                {
                    IAddressTypeSymbol addressType => addressType.ReferencedType,
                    RefTypeSymbol refType => refType.ElementType,
                    _ => argumentType,
                };
            }

            TryUnifyExtensionReceiverType(parameterType, argumentType, substitutions);
        }

        return substitutions;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="type"/> references any of the given type parameters.
    /// </summary>
    private static bool ContainsAnyTypeParameter(
        ITypeSymbol type,
        ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        if (typeParameters.IsDefaultOrEmpty)
            return false;

        return ContainsAnyTypeParameterCore(type, typeParameters);
    }

    private static bool ContainsAnyTypeParameterCore(
        ITypeSymbol type,
        ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        if (type is ITypeParameterSymbol tp)
            return typeParameters.Contains(tp, SymbolEqualityComparer.Default);

        if (type is INamedTypeSymbol named)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (ContainsAnyTypeParameterCore(arg, typeParameters))
                    return true;
            }
        }

        if (type is IArrayTypeSymbol array)
            return ContainsAnyTypeParameterCore(array.ElementType, typeParameters);

        if (type is RefTypeSymbol refType)
            return ContainsAnyTypeParameterCore(refType.ElementType, typeParameters);

        if (type is IAddressTypeSymbol addressType)
            return ContainsAnyTypeParameterCore(addressType.ReferencedType, typeParameters);

        if (type is NullableTypeSymbol nullableType)
            return ContainsAnyTypeParameterCore(nullableType.UnderlyingType, typeParameters);

        return false;
    }

    /// <summary>
    /// For lambda argument positions where no common delegate target type could be computed
    /// (i.e. <see cref="TryGetCommonPositionalParameterType"/> returned <c>null</c> because the
    /// candidates' parameter types disagree), returns the first candidate that provides a
    /// delegate type for this argument index.  This lets lambdas infer their parameter types
    /// even when multiple overloads differ only in the return-type type parameter
    /// (e.g. <c>Func&lt;int,TKey1&gt;</c> vs <c>Func&lt;int,TKey2&gt;</c>).
    /// </summary>
    private ITypeSymbol? TryGetFirstDelegateParameterType(
        ImmutableArray<IMethodSymbol> methods,
        int argumentIndex,
        BoundExpression? receiver,
        ITypeSymbol? pipeReceiverType)
    {
        ITypeSymbol? firstConcreteDelegate = null;
        var sawSystemDelegateLike = false;

        foreach (var method in methods)
        {
            if (method is null)
                continue;

            var parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null)
                ? argumentIndex + 1
                : argumentIndex;

            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var type = GetInvocationParameterTypeForArgumentBinding(method, parameterIndex, receiver, pipeReceiverType);

            if (type is INamedTypeSymbol namedType)
            {
                if (namedType.TypeKind == TypeKind.Delegate)
                {
                    firstConcreteDelegate ??= type;
                    continue;
                }

                // Do not force the lambda into an arbitrary concrete delegate when overloads also
                // include System.Delegate/MulticastDelegate handlers (e.g. ASP.NET MapGet with
                // RequestDelegate + Delegate overloads). Let overload resolution choose first.
                if (IsSystemDelegateLike(namedType))
                    sawSystemDelegateLike = true;
            }
        }

        return sawSystemDelegateLike ? null : firstConcreteDelegate;
    }

    private ITypeSymbol? TryGetFirstCollectionParameterType(
        ImmutableArray<IMethodSymbol> methods,
        int argumentIndex,
        BoundExpression? receiver,
        ITypeSymbol? pipeReceiverType)
    {
        foreach (var method in methods)
        {
            if (method is null)
                continue;

            var parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null)
                ? argumentIndex + 1
                : argumentIndex;

            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var type = GetInvocationParameterTypeForArgumentBinding(method, parameterIndex, receiver, pipeReceiverType);
            var plainType = type.GetNonNullableType();

            if (plainType is IArrayTypeSymbol { Rank: 1 })
                return plainType;

            if (plainType is INamedTypeSymbol namedType &&
                (TryGetDictionaryInterfaceElementTypes(namedType, out _, out _) ||
                 TryGetDictionaryAddSignature(namedType, out _, out _, out _)))
            {
                return type;
            }
        }

        return null;
    }

    private bool HasGenericEnumerableCollectionCandidate(
        ImmutableArray<IMethodSymbol> methods,
        int argumentIndex,
        BoundExpression? receiver,
        ITypeSymbol? pipeReceiverType)
    {
        foreach (var method in methods)
        {
            if (method is null || method.TypeParameters.IsDefaultOrEmpty)
                continue;

            var parameterIndex = (method.IsExtensionMethod || pipeReceiverType is not null)
                ? argumentIndex + 1
                : argumentIndex;

            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var parameterType = GetInvocationParameterTypeForArgumentBinding(
                method,
                parameterIndex,
                receiver,
                pipeReceiverType);

            if (parameterType.GetNonNullableType() is INamedTypeSymbol namedType &&
                TryGetIEnumerableElementType(namedType, out var elementType) &&
                ContainsAnyTypeParameter(elementType, method.TypeParameters))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanUseOpenDelegateReturnTypeHint(ExpressionSyntax expression)
        => expression is FunctionExpressionSyntax
            or IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or GenericNameSyntax;

    /// <summary>
    /// Returns <c>true</c> when the <em>input parameter types</em> (not the return type) of a
    /// delegate target type reference any of the given method type parameters.
    /// For non-delegate types this falls back to the full <see cref="ContainsAnyTypeParameter"/>
    /// check.
    /// </summary>
    /// <remarks>
    /// The lambda binder already handles an unresolved return-type type parameter by inferring
    /// the return type from the lambda body, so it is safe to use a delegate target type whose
    /// return type is still open as long as the input parameter types are fully resolved.
    /// </remarks>
    private static bool ContainsAnyTypeParameterInDelegateInputParams(
        ITypeSymbol type,
        ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        if (typeParameters.IsDefaultOrEmpty)
            return false;

        if (type is INamedTypeSymbol namedType && namedType.TypeKind == TypeKind.Delegate)
        {
            var invoke = namedType.GetDelegateInvokeMethod();
            if (invoke is not null)
            {
                // Only check input parameter types; the return type is intentionally excluded.
                foreach (var param in invoke.Parameters)
                {
                    if (ContainsAnyTypeParameterCore(param.Type, typeParameters))
                        return true;
                }
                return false;
            }
        }

        // Not a recognised delegate type — fall back to the full check.
        return ContainsAnyTypeParameterCore(type, typeParameters);
    }

    private BoundExpression BindConstructorInvocation(
        INamedTypeSymbol typeSymbol,
        InvocationExpressionSyntax invocation,
        SyntaxNode receiverSyntax,
        BoundExpression? receiver = null)
    {
        EnsureImplicitDefaultConstructorAvailable(typeSymbol);

        // Bind constructor arguments while supplying best-effort target types based on ctor parameter types.
        var ctorsForArgumentBinding = FilterInvocationCandidatesForArgumentBinding(typeSymbol.Constructors, invocation.ArgumentList.Arguments);
        var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(ctorsForArgumentBinding, invocation.ArgumentList.Arguments, out var hasErrors);

        if (hasErrors)
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.ArgumentBindingFailed);

        return BindConstructorInvocation(typeSymbol, boundArguments, invocation, receiverSyntax, receiver);
    }

    private void EnsureImplicitDefaultConstructorAvailable(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol is not SourceNamedTypeSymbol sourceType ||
            sourceType is IUnionSymbol ||
            sourceType.IsStatic ||
            sourceType.HasPrimaryConstructorSyntax)
        {
            return;
        }

        if (sourceType.GetDeclaredMembersWithoutEnsuring(".ctor").OfType<IMethodSymbol>().Any(static constructor => !constructor.IsStatic))
            return;

        foreach (var reference in sourceType.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
                continue;

            if (declaration is ClassDeclarationSyntax { ParameterList: not null } ||
                declaration is RecordDeclarationSyntax { ParameterList: not null } ||
                declaration.Members.OfType<ConstructorDeclarationSyntax>().Any(static constructor => !HasStaticModifier(constructor.Modifiers)) ||
                declaration.Members.OfType<ParameterlessConstructorDeclarationSyntax>().Any(static constructor => !HasStaticModifier(constructor.Modifiers)))
            {
                return;
            }
        }

        var location = sourceType.Locations.FirstOrDefault() ?? Location.None;
        var syntaxReference = sourceType.DeclaringSyntaxReferences.FirstOrDefault();
        var references = syntaxReference is null ? Array.Empty<SyntaxReference>() : [syntaxReference];

        _ = new SourceMethodSymbol(
            ".ctor",
            Compilation.GetSpecialType(SpecialType.System_Unit),
            ImmutableArray<SourceParameterSymbol>.Empty,
            sourceType,
            sourceType,
            sourceType.ContainingNamespace?.AsSourceNamespace(),
            [location],
            references,
            isStatic: false,
            methodKind: MethodKind.Constructor,
            declaredAccessibility: Accessibility.Public);
    }

    private static bool HasStaticModifier(SyntaxTokenList modifiers)
        => modifiers.Any(static modifier => modifier.Kind == SyntaxKind.StaticKeyword);

    private BoundExpression BindConstructorInvocation(
        INamedTypeSymbol typeSymbol,
        BoundArgument[] boundArguments,
        SyntaxNode callSyntax,
        SyntaxNode receiverSyntax,
        BoundExpression? receiver = null)
    {
        Location GetCallLocation() => callSyntax.GetLocation() ?? receiverSyntax.GetLocation() ?? Location.None;
        Location GetReceiverLocation() => receiverSyntax.GetLocation() ?? callSyntax.GetLocation() ?? Location.None;

        if (typeSymbol.TypeKind == TypeKind.Error)
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OtherError);

        if (typeSymbol.IsGenericType &&
            !IsUninstantiatedGenericType(typeSymbol) &&
            !ValidateTypeArgumentConstraints(
                typeSymbol,
                typeSymbol.TypeArguments,
                _ => GetReceiverLocation(),
                typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
        {
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.TypeMismatch);
        }

        if (typeSymbol.IsStatic)
        {
            _diagnostics.ReportStaticTypeCannotBeInstantiated(typeSymbol.Name, GetCallLocation());
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OtherError);
        }
        if (typeSymbol.IsAbstract)
        {
            _diagnostics.ReportCannotInstantiateAbstractType(typeSymbol.Name, GetCallLocation());
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OtherError);
        }

        // For uninstantiated generic types (TypeArguments empty), infer type arguments from the
        // constructor arguments BEFORE attempting overload resolution on the open type.
        // Without this, overload resolution can succeed with an unsubstituted constructor
        // (e.g. Error<E_case>.init(data: E_case) matched against an argument of type E_method),
        // because two unrelated type parameters are treated as freely compatible.  That produces a
        // SourceMethodSymbol on the open generic, which codegen emits as `newobj Type`1::.ctor(!0)`
        // (a type generic parameter) instead of the correct `newobj Type`1<!!E>::.ctor(!!E)`
        // (a method generic parameter).
        // For generic types whose type arguments are still open (either empty or all are the type's
        // own type-level parameters), infer concrete type arguments from the constructor arguments
        // BEFORE attempting overload resolution.  Without this, overload resolution can succeed with
        // an unsubstituted constructor (e.g. Error<E_case>.init(data: E_case) matched against an
        // argument of type E_method) because two unrelated type parameters are treated as freely
        // compatible.  That produces a SourceMethodSymbol on the open/self-constructed generic, which
        // codegen emits as `newobj Type`1::.ctor(!0)` instead of the correct
        // `newobj Type`1<!!E>::.ctor(!!E)`.
        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol)
            && TryInferConstructedTypeForConstructor(typeSymbol, boundArguments, out var earlyInferred)
            && !SymbolEqualityComparer.Default.Equals(earlyInferred, typeSymbol))
        {
            return RebindConstructorInvocationWithInferredType(earlyInferred, boundArguments, callSyntax, receiverSyntax, receiver);
        }

        // If constructor-argument inference cannot infer type arguments (for example on parameterless
        // constructors), use target typing from the surrounding context, e.g. `val x: Box<int> = Box()`.
        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol)
            && TryInferConstructedTypeFromTargetType(typeSymbol, callSyntax, out var targetTypedInferred)
            && !SymbolEqualityComparer.Default.Equals(targetTypedInferred, typeSymbol))
        {
            return RebindConstructorInvocationWithInferredType(targetTypedInferred, boundArguments, callSyntax, receiverSyntax, receiver);
        }

        // Generic union case construction can also be target-typed from the enclosing union carrier,
        // e.g. `val option: Option<T> = Some(payload)`. That target does not match the case type
        // directly, so regular target-typed constructor inference cannot see it.
        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol)
            && TryInferConstructedUnionCaseTypeFromTargetType(typeSymbol, callSyntax, out var targetTypedUnionCase)
            && !SymbolEqualityComparer.Default.Equals(targetTypedUnionCase, typeSymbol))
        {
            return RebindConstructorInvocationWithInferredType(targetTypedUnionCase, boundArguments, callSyntax, receiverSyntax, receiver);
        }

        // If the generic type is still open at this point, constructor-argument inference and
        // target typing both failed to determine its type arguments. Do not allow overload
        // resolution to proceed on the open constructors, because that would incorrectly accept
        // calls like `MyResult(42)` for `union MyResult<T>(List<T> | int)`.
        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol))
        {
            _diagnostics.ReportTypeRequiresTypeArguments(typeSymbol.Name, typeSymbol.Arity, GetReceiverLocation());
            return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OtherError);
        }

        var resolution = OverloadResolver.ResolveOverload(typeSymbol.Constructors, boundArguments, Compilation, binder: this, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
        if (resolution.Success)
        {
            var constructor = resolution.Method!;
            if (!EnsureMemberAccessible(constructor, GetReceiverLocation(), "constructor"))
                return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.Inaccessible);
            ReportObsoleteIfNeeded(constructor, GetCallLocation());
            var convertedArgs = ConvertArguments(constructor.Parameters, boundArguments);

            BoundObjectInitializer? initializer = null;
            HashSet<string>? assignedInitializerMembers = null;

            if (callSyntax is InvocationExpressionSyntax { Initializer: { } objectInitializer })
            {
                initializer = BindObjectInitializer(typeSymbol, objectInitializer);
                assignedInitializerMembers = GetAssignedMemberNames(objectInitializer);
            }
            ValidateRequiredMembers(typeSymbol, constructor, callSyntax, assignedInitializerMembers);

            return new BoundObjectCreationExpression(constructor, convertedArgs, receiver, initializer);
        }

        if (resolution.IsAmbiguous)
        {
            _diagnostics.ReportCallIsAmbiguous(typeSymbol.Name, resolution.AmbiguousCandidates, GetCallLocation());
            return new BoundErrorExpression(
                typeSymbol,
                null,
                BoundExpressionReason.Ambiguous,
                AsSymbolCandidates(resolution.AmbiguousCandidates));
        }

        if (TryInferConstructedTypeForConstructor(typeSymbol, boundArguments, out var inferredType) &&
            !SymbolEqualityComparer.Default.Equals(inferredType, typeSymbol))
        {
            return RebindConstructorInvocationWithInferredType(inferredType, boundArguments, callSyntax, receiverSyntax, receiver);
        }

        ReportSuppressedLambdaDiagnostics(boundArguments);
        _diagnostics.ReportNoOverloadForMethod("constructor for type", typeSymbol.Name, boundArguments.Length, GetCallLocation());
        return new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OverloadResolutionFailed);
    }

    private BoundExpression RebindConstructorInvocationWithInferredType(
        INamedTypeSymbol inferredType,
        BoundArgument[] boundArguments,
        SyntaxNode callSyntax,
        SyntaxNode receiverSyntax,
        BoundExpression? receiver)
    {
        if (callSyntax is InvocationExpressionSyntax invocation)
        {
            ClearCachedLambdaBodyNodes(invocation);

            return BindConstructorInvocation(inferredType, invocation, receiverSyntax, receiver);
        }

        return BindConstructorInvocation(inferredType, boundArguments, callSyntax, receiverSyntax, receiver);
    }

    /// <summary>
    /// Returns true when <paramref name="typeSymbol"/> is a generic type whose type arguments are
    /// not yet concretely instantiated — either because the TypeArguments list is empty/default (the
    /// open generic form used by source and PE original-definition symbols), or because every type
    /// argument is still one of the type's OWN type-level type parameters (i.e. the type was
    /// constructed with its own parameters as placeholders, which also represents an unresolved
    /// open form).
    /// </summary>
    private static bool IsUninstantiatedGenericType(INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.IsGenericType)
            return false;

        var typeArguments = typeSymbol.TypeArguments;
        if (typeArguments.IsDefaultOrEmpty)
            return true;

        // If all type arguments are the type's own type-level parameters then the type is
        // effectively uninstantiated (constructed with itself).
        var typeParameters = typeSymbol.TypeParameters;
        if (!typeParameters.IsDefaultOrEmpty && typeArguments.Length == typeParameters.Length)
        {
            for (int i = 0; i < typeArguments.Length; i++)
            {
                if (typeArguments[i] is not ITypeParameterSymbol argumentTypeParameter)
                    return false;

                if (!AreSameTypeParameterIdentity(argumentTypeParameter, typeParameters[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    private bool TryInferConstructedTypeFromTargetType(
        INamedTypeSymbol typeSymbol,
        SyntaxNode callSyntax,
        out INamedTypeSymbol inferredType)
    {
        inferredType = typeSymbol;

        if (!typeSymbol.IsGenericType || typeSymbol.TypeParameters.IsDefaultOrEmpty)
            return false;

        var targetType = GetTargetType(callSyntax);
        if (targetType is null || targetType.TypeKind == TypeKind.Error)
            return false;

        targetType = UnwrapAlias(targetType);
        targetType = UnwrapTaskLikeTargetType(targetType);

        if (targetType is not INamedTypeSymbol targetNamedType)
            return false;

        var typeDefinition = (typeSymbol.OriginalDefinition as INamedTypeSymbol) ?? typeSymbol;
        var targetDefinition = (targetNamedType.OriginalDefinition as INamedTypeSymbol) ?? targetNamedType;

        if (!SymbolEqualityComparer.Default.Equals(typeDefinition, targetDefinition))
            return false;

        if (targetNamedType.TypeArguments.IsDefaultOrEmpty ||
            targetNamedType.TypeArguments.Length != typeSymbol.TypeParameters.Length)
        {
            return false;
        }

        var currentTypeArguments = typeSymbol.TypeArguments;
        if (currentTypeArguments.IsDefaultOrEmpty)
            currentTypeArguments = typeSymbol.TypeParameters.Cast<ITypeSymbol>().ToImmutableArray();

        var projected = new ITypeSymbol[typeSymbol.TypeParameters.Length];
        var changed = false;
        var hasConcreteTargetArgument = false;

        for (var i = 0; i < projected.Length; i++)
        {
            var targetArgument = targetNamedType.TypeArguments[i];
            if (targetArgument.TypeKind == TypeKind.Error)
                return false;

            projected[i] = targetArgument;

            if (targetArgument is not ITypeParameterSymbol)
                hasConcreteTargetArgument = true;

            if (!AreEquivalentGenericInstantiationArgument(projected[i], currentTypeArguments[i]))
                changed = true;
        }

        if (!changed || !hasConcreteTargetArgument)
            return false;

        inferredType = (INamedTypeSymbol)typeDefinition.Construct(projected);
        return true;
    }

    private bool TryInferConstructedUnionCaseTypeFromTargetType(
        INamedTypeSymbol typeSymbol,
        SyntaxNode callSyntax,
        out INamedTypeSymbol inferredType)
    {
        inferredType = typeSymbol;

        if (!typeSymbol.IsGenericType || typeSymbol.TypeParameters.IsDefaultOrEmpty)
            return false;

        if (typeSymbol.TryGetUnionCase() is not { } unionCase)
            return false;

        var targetType = GetTargetType(callSyntax);
        if (targetType is null || targetType.TypeKind == TypeKind.Error)
            return false;

        targetType = UnwrapAlias(targetType);
        targetType = UnwrapTaskLikeTargetType(targetType);

        if (targetType is not INamedTypeSymbol targetNamedType)
            return false;

        var targetUnion = targetNamedType.TryGetUnion() as INamedTypeSymbol
            ?? targetNamedType.TryGetUnionCase()?.Union as INamedTypeSymbol;
        var caseUnion = unionCase.Union as INamedTypeSymbol;

        if (targetUnion is null || caseUnion is null)
            return false;

        var caseUnionDefinition = (caseUnion.OriginalDefinition as INamedTypeSymbol) ?? caseUnion;
        var targetUnionDefinition = (targetUnion.OriginalDefinition as INamedTypeSymbol) ?? targetUnion;
        if (!SymbolEqualityComparer.Default.Equals(caseUnionDefinition, targetUnionDefinition))
            return false;

        var caseDefinition = (typeSymbol.OriginalDefinition as INamedTypeSymbol) ?? typeSymbol;
        if (ProjectCaseTypeToUnionArguments(caseDefinition, targetUnion) is not INamedTypeSymbol projectedCaseType)
            return false;

        if (AreEquivalentGenericInstantiationArgument(projectedCaseType, typeSymbol))
            return false;

        inferredType = projectedCaseType;
        return true;
    }

    private bool TryInferConstructedTypeForConstructor(
        INamedTypeSymbol typeSymbol,
        BoundArgument[] boundArguments,
        out INamedTypeSymbol inferredType)
    {
        inferredType = typeSymbol;

        if (!typeSymbol.IsGenericType || typeSymbol.TypeParameters.IsDefaultOrEmpty)
            return false;

        var typeParameters = typeSymbol.TypeParameters;

        // PE generic type definitions have TypeArguments == [] (the open form uses TypeParameters
        // as placeholders). Fall back to treating each type parameter as its own current argument
        // so inference can still infer concrete types from constructor arguments.
        var originalTypeArguments = typeSymbol.TypeArguments;
        if (originalTypeArguments.IsDefaultOrEmpty)
            originalTypeArguments = typeParameters.Cast<ITypeSymbol>().ToImmutableArray();

        // When typeSymbol is already constructed (e.g. Ok<Unit> from target-typed resolution),
        // its constructors have substituted parameter types (Unit instead of T), which prevents
        // inference. Use the original definition's unsubstituted constructors instead.
        var definitionForInference = (typeSymbol.OriginalDefinition is INamedTypeSymbol origDef &&
                                      !SymbolEqualityComparer.Default.Equals(origDef, typeSymbol))
            ? origDef : typeSymbol;
        var candidates = definitionForInference.Constructors;
        if (candidates.IsDefaultOrEmpty)
            return false;

        foreach (var constructor in candidates)
        {
            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!TryInferConstructorTypeArguments(typeParameters, constructor, boundArguments, substitutions))
                continue;

            if (substitutions.Count == 0)
                continue;

            var changed = false;
            var projected = new ITypeSymbol[typeParameters.Length];

            for (var i = 0; i < typeParameters.Length; i++)
            {
                var parameter = typeParameters[i];
                var current = originalTypeArguments[i];

                if (substitutions.TryGetValue(parameter, out var replacement))
                {
                    projected[i] = replacement;
                    if (!AreEquivalentGenericInstantiationArgument(current, replacement))
                        changed = true;
                }
                else
                {
                    projected[i] = current;
                }
            }

            // Constructor-argument inference must resolve all of the constructed type's own
            // type parameters. Otherwise open-generic invocations like `MyResult(42)` would
            // incorrectly succeed by selecting only the non-generic constructor branch.
            var fullyResolved = true;
            for (var i = 0; i < projected.Length; i++)
            {
                if (projected[i] is ITypeParameterSymbol projectedParameter &&
                    AreSameTypeParameterIdentity(projectedParameter, typeParameters[i]))
                {
                    fullyResolved = false;
                    break;
                }
            }

            if (!fullyResolved)
                continue;

            if (!changed)
                continue;

            inferredType = (INamedTypeSymbol)definitionForInference.Construct(projected);
            return true;
        }

        return false;
    }

    private bool TryInferConstructorTypeArguments(
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        IMethodSymbol constructor,
        BoundArgument[] boundArguments,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (boundArguments.Length == 0)
            return false;

        var parameters = constructor.Parameters;
        if (parameters.IsDefaultOrEmpty)
            return false;

        for (var argumentIndex = 0; argumentIndex < boundArguments.Length; argumentIndex++)
        {
            var argument = boundArguments[argumentIndex];
            var argumentType = argument.Expression.Type?.UnwrapLiteralType() ?? argument.Expression.Type;
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                continue;

            IParameterSymbol? parameter = null;
            if (!string.IsNullOrEmpty(argument.Name))
                parameter = parameters.FirstOrDefault(p => string.Equals(p.Name, argument.Name, StringComparison.Ordinal));
            else if (argumentIndex < parameters.Length)
                parameter = parameters[argumentIndex];

            if (parameter is null)
                return false;

            if (argument.Expression is BoundFunctionExpression lambda)
            {
                if (!TryUnifyConstructorFunctionArgument(typeParameters, parameter.Type, lambda, substitutions))
                    return false;

                continue;
            }

            if (!TryUnifyConstructorParameterType(typeParameters, parameter.Type, argumentType, substitutions))
                return false;
        }

        return substitutions.Count > 0;
    }

    private bool TryUnifyConstructorFunctionArgument(
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        ITypeSymbol parameterType,
        BoundFunctionExpression lambda,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        parameterType = parameterType is NullableTypeSymbol nullableParameterType
            ? nullableParameterType.UnderlyingType
            : parameterType;

        INamedTypeSymbol? delegateType = null;
        if (parameterType is INamedTypeSymbol namedParameterType)
        {
            if (namedParameterType.TypeKind == TypeKind.Delegate)
                delegateType = namedParameterType;
            else if (TryGetExpressionTreeDelegateType(namedParameterType, out var expressionTreeDelegate))
                delegateType = expressionTreeDelegate;
        }

        if (delegateType is null || delegateType.GetDelegateInvokeMethod() is not { } invoke)
            return false;

        var lambdaParameters = lambda.Parameters.ToImmutableArray();
        if (invoke.Parameters.Length != lambdaParameters.Length)
            return false;

        for (var i = 0; i < lambdaParameters.Length; i++)
        {
            var lambdaParameterType = lambdaParameters[i].Type;
            if (lambdaParameterType.TypeKind == TypeKind.Error)
                continue;

            if (!TryUnifyConstructorParameterType(typeParameters, invoke.Parameters[i].Type, lambdaParameterType, substitutions))
                return false;
        }

        if (lambda.ReturnType.TypeKind != TypeKind.Error &&
            !TryUnifyConstructorParameterType(typeParameters, invoke.ReturnType, lambda.ReturnType, substitutions))
        {
            return false;
        }

        return true;
    }

    private static bool AreEquivalentGenericInstantiationArgument(ITypeSymbol left, ITypeSymbol right)
    {
        if (left is ITypeParameterSymbol leftTypeParameter && right is ITypeParameterSymbol rightTypeParameter)
            return AreSameTypeParameterIdentity(leftTypeParameter, rightTypeParameter);

        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static bool AreSameTypeParameterIdentity(ITypeParameterSymbol left, ITypeParameterSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        left = (ITypeParameterSymbol)(left.OriginalDefinition ?? left);
        right = (ITypeParameterSymbol)(right.OriginalDefinition ?? right);

        if (ReferenceEquals(left, right))
            return true;

        if (left.OwnerKind != right.OwnerKind || left.Ordinal != right.Ordinal)
            return false;

        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            return false;

        if (left.OwnerKind == TypeParameterOwnerKind.Method)
        {
            var leftOwner = (IMethodSymbol?)(left.DeclaringMethodParameterOwner?.OriginalDefinition ?? left.DeclaringMethodParameterOwner);
            var rightOwner = (IMethodSymbol?)(right.DeclaringMethodParameterOwner?.OriginalDefinition ?? right.DeclaringMethodParameterOwner);
            return SymbolEqualityComparer.Default.Equals(leftOwner, rightOwner);
        }

        var leftTypeOwner = (INamedTypeSymbol?)(left.DeclaringTypeParameterOwner?.OriginalDefinition ?? left.DeclaringTypeParameterOwner);
        var rightTypeOwner = (INamedTypeSymbol?)(right.DeclaringTypeParameterOwner?.OriginalDefinition ?? right.DeclaringTypeParameterOwner);
        return SymbolEqualityComparer.Default.Equals(leftTypeOwner, rightTypeOwner);
    }

    private bool TryUnifyConstructorParameterType(
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        ITypeSymbol parameterType,
        ITypeSymbol argumentType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        parameterType = parameterType.UnwrapLiteralType() ?? parameterType;
        argumentType = argumentType.UnwrapLiteralType() ?? argumentType;

        if (parameterType is ITypeParameterSymbol typeParameter &&
            typeParameters.Any(tp => SymbolEqualityComparer.Default.Equals(tp, typeParameter)))
        {
            if (substitutions.TryGetValue(typeParameter, out var existing))
                return SymbolEqualityComparer.Default.Equals(existing, argumentType);

            substitutions[typeParameter] = argumentType;
            return true;
        }

        if (parameterType is NullableTypeSymbol parameterNullable)
        {
            if (argumentType is NullableTypeSymbol argumentNullable)
                return TryUnifyConstructorParameterType(typeParameters, parameterNullable.UnderlyingType, argumentNullable.UnderlyingType, substitutions);

            return TryUnifyConstructorParameterType(typeParameters, parameterNullable.UnderlyingType, argumentType, substitutions);
        }

        if (parameterType is RefTypeSymbol parameterRef && argumentType is RefTypeSymbol argumentRef)
            return TryUnifyConstructorParameterType(typeParameters, parameterRef.ElementType, argumentRef.ElementType, substitutions);

        if (parameterType is IAddressTypeSymbol parameterAddress && argumentType is IAddressTypeSymbol argumentAddress)
            return TryUnifyConstructorParameterType(typeParameters, parameterAddress.ReferencedType, argumentAddress.ReferencedType, substitutions);

        if (parameterType is IArrayTypeSymbol parameterArray && argumentType is IArrayTypeSymbol argumentArray)
            return TryUnifyConstructorParameterType(typeParameters, parameterArray.ElementType, argumentArray.ElementType, substitutions);

        if (parameterType is INamedTypeSymbol parameterNamed && argumentType is INamedTypeSymbol argumentNamed)
        {
            var parameterDefinition = parameterNamed.OriginalDefinition ?? parameterNamed;
            var argumentDefinition = argumentNamed.OriginalDefinition ?? argumentNamed;

            if (!SymbolEqualityComparer.Default.Equals(parameterDefinition, argumentDefinition))
                return true;

            var parameterArguments = parameterNamed.TypeArguments;
            var argumentArguments = argumentNamed.TypeArguments;

            if (parameterArguments.IsDefaultOrEmpty || argumentArguments.IsDefaultOrEmpty || parameterArguments.Length != argumentArguments.Length)
                return true;

            for (var i = 0; i < parameterArguments.Length; i++)
            {
                if (!TryUnifyConstructorParameterType(typeParameters, parameterArguments[i], argumentArguments[i], substitutions))
                    return false;
            }

            return true;
        }

        return true;
    }

    private void ValidateRequiredMembers(
    INamedTypeSymbol typeSymbol,
    IMethodSymbol constructor,
    SyntaxNode creationSyntax,
    ObjectInitializerExpressionSyntax? initializerSyntax)
        => ValidateRequiredMembers(
            typeSymbol,
            constructor,
            creationSyntax,
            initializerSyntax is null ? null : GetAssignedMemberNames(initializerSyntax));

    private void ValidateRequiredMembers(
    INamedTypeSymbol typeSymbol,
    IMethodSymbol constructor,
    SyntaxNode creationSyntax,
    HashSet<string>? assigned)
    {
        // If the selected constructor claims it sets required members, we're done.
        // Later steps can implement deeper checks (e.g. ctor chaining, actual assignments).
        if (constructor.SetsRequiredMembers)
            return;

        // Collect required member names from this type + base types.
        var required = GetRequiredMembers(typeSymbol);
        if (required.IsDefaultOrEmpty)
            return;

        assigned ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var memberName in required)
        {
            if (!assigned.Contains(memberName))
                _diagnostics.ReportRequiredMemberMustBeSet(memberName, creationSyntax.GetLocation());
        }
    }

    private static ImmutableArray<string> GetRequiredMembers(INamedTypeSymbol type)
    {
        var builder = ImmutableArray.CreateBuilder<string>();

        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member is SourceFieldSymbol field && field.IsRequired)
                    builder.Add(field.Name);
                else if (member is SourcePropertySymbol prop && prop.IsRequired)
                    builder.Add(prop.Name);
            }
        }

        return builder.ToImmutable();
    }

    private static HashSet<string> GetAssignedMemberNames(ObjectInitializerExpressionSyntax initializer)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in initializer.DescendantNodes())
        {
            if (node is ObjectInitializerAssignmentEntrySyntax assign)
            {
                switch (assign.Name)
                {
                    case IdentifierNameSyntax id:
                        names.Add(id.Identifier.ValueText);
                        break;
                }
            }
        }

        return names;
    }

    protected static IMethodSymbol EnsureConstructedConstructor(IMethodSymbol constructor, INamedTypeSymbol typeSymbol)
    {
        if (constructor is null)
            throw new ArgumentNullException(nameof(constructor));
        if (typeSymbol is null)
            throw new ArgumentNullException(nameof(typeSymbol));

        if (constructor is SubstitutedMethodSymbol || constructor is ConstructedMethodSymbol)
            return constructor;

        if (typeSymbol is not ConstructedNamedTypeSymbol constructed)
            return constructor;

        if (constructed.ConstructedFrom is not INamedTypeSymbol originalDefinition)
            return constructor;

        if (constructor.ContainingType is not INamedTypeSymbol containingDefinition)
            return constructor;

        if (!SymbolEqualityComparer.Default.Equals(containingDefinition, originalDefinition))
            return constructor;

        return new SubstitutedMethodSymbol(constructor, constructed);
    }

    // ============================
    // Conditional access helpers
    // ============================

    private static bool TryGetOptionType(ITypeSymbol? type, out INamedTypeSymbol option, out ITypeSymbol payload)
    {
        option = null!;
        payload = null!;

        if (type is null)
            return false;

        type = type.UnwrapLiteralType() ?? type;

        if (type is INamedTypeSymbol named &&
            named.Arity == 1 &&
            string.Equals(named.Name, "Option", StringComparison.Ordinal))
        {
            option = named;
            payload = named.TypeArguments[0];
            return true;
        }

        return false;
    }

    private static bool TryGetResultType(ITypeSymbol? type, out INamedTypeSymbol result, out ITypeSymbol payload, out ITypeSymbol error)
    {
        result = null!;
        payload = null!;
        error = null!;

        if (type is null)
            return false;

        type = type.UnwrapLiteralType() ?? type;

        if (type is INamedTypeSymbol named &&
            named.Arity == 2 &&
            string.Equals(named.Name, "Result", StringComparison.Ordinal))
        {
            result = named;
            payload = named.TypeArguments[0];
            error = named.TypeArguments[1];
            return true;
        }

        return false;
    }

    private ITypeSymbol? GetConditionalAccessLookupType(ITypeSymbol? type)
    {
        if (type is null)
            return null;

        type = type.UnwrapLiteralType() ?? type;

        // Carrier-aware lookup:
        // - Nullable wrapper: look up on underlying T
        // - Option<T>: look up on payload T
        // - Result<T,E>: look up on payload T
        if (TryGetOptionType(type, out _, out var optPayload))
            return optPayload.GetNonNullableType();

        if (TryGetResultType(type, out _, out var resPayload, out _))
            return resPayload.GetNonNullableType();

        // Raven nullable wrapper
        return type.GetNonNullableType();
    }

    private BoundExpression GetConditionalAccessWhenNotNullReceiver(BoundExpression receiver, ITypeSymbol? lookupType)
    {
        if (receiver.Type is null || lookupType is null)
            return receiver;

        var receiverType = receiver.Type.UnwrapLiteralType() ?? receiver.Type;
        if (SymbolEqualityComparer.Default.Equals(receiverType, lookupType))
            return receiver;

        var plainReceiverType = receiverType.GetNonNullableType();
        if (!SymbolEqualityComparer.Default.Equals(plainReceiverType, lookupType))
            return receiver;

        var conversion = new Conversion(
            isImplicit: true,
            isIdentity: true,
            isReference: !lookupType.IsValueType,
            isLifted: receiverType is NullableTypeSymbol);

        return new BoundConversionExpression(
            receiver,
            lookupType,
            conversion);
    }

    // ============================
    // Conditional access binding
    // ============================

    private BoundExpression BindConditionalAccessExpression(ConditionalAccessExpressionSyntax syntax)
    {
        var receiver = BindExpressionAllowingEvent(syntax.Expression);
        if (receiver is BoundErrorExpression) return receiver;

        // classify carrier
        var receiverType = UnwrapAlias(receiver.Type ?? Compilation.ErrorTypeSymbol);

        if (TryGetOptionType(receiverType, out var opt, out var optPayload))
            return BindCarrierConditionalAccess(syntax, receiver, BoundCarrierKind.Option, opt, optPayload, errorType: null);

        if (TryGetResultType(receiverType, out var res, out var resPayload, out var resError))
            return BindCarrierConditionalAccess(syntax, receiver, BoundCarrierKind.Result, res, resPayload, errorType: resError);

        if (receiverType is INamedTypeSymbol namedReceiver &&
            TryGetPropagationInfo(namedReceiver, out var propagationInfo) &&
            propagationInfo.Kind == PropagationKind.Contract)
        {
            return BindContractPropagationConditionalAccess(syntax, receiver);
        }

        // fallback: your existing nullable conditional access path
        return BindNullableConditionalAccessExpression(syntax, receiver);
    }

    private BoundExpression BindContractPropagationConditionalAccess(
        ConditionalAccessExpressionSyntax syntax,
        BoundExpression receiver)
    {
        var propagatedReceiver = BindPropagateExpressionCore(receiver, syntax.OperatorToken, syntax);
        if (propagatedReceiver is BoundErrorExpression)
            return propagatedReceiver;

        var payloadType = propagatedReceiver.Type ?? Compilation.ErrorTypeSymbol;

        return syntax.WhenNotNull switch
        {
            MemberBindingExpressionSyntax memberBinding =>
                BindMemberAccessOnReceiver(
                    propagatedReceiver,
                    memberBinding.Name,
                    preferMethods: false,
                    allowEventAccess: true,
                    suppressNullWarning: false,
                    receiverTypeForLookup: payloadType,
                    forceExtensionReceiver: true),

            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax memberBinding } invocation =>
                BindInvocationOnMethodGroup(
                    (BoundMethodGroupExpression)BindMemberAccessOnReceiver(
                        propagatedReceiver,
                        memberBinding.Name,
                        preferMethods: true,
                        allowEventAccess: false,
                        suppressNullWarning: false,
                        receiverTypeForLookup: payloadType,
                        forceExtensionReceiver: true),
                    invocation),

            ElementBindingExpressionSyntax elementBinding =>
                BindElementAccessExpression(
                    propagatedReceiver,
                    elementBinding.ArgumentList,
                    elementBinding,
                    suppressNullWarning: false),

            InvocationExpressionSyntax { Expression: ReceiverBindingExpressionSyntax } invocation =>
                BindInvocationExpressionCore(
                    propagatedReceiver,
                    "Invoke",
                    invocation.ArgumentList,
                    syntax.Expression,
                    invocation,
                    suppressNullWarning: false),

            _ => ErrorExpression(reason: BoundExpressionReason.NotFound)
        };
    }

    private BoundExpression BindCarrierConditionalAccess(
    ConditionalAccessExpressionSyntax syntax,
    BoundExpression carrierReceiver,
    BoundCarrierKind kind,
    INamedTypeSymbol carrierGeneric,
    ITypeSymbol payloadType,
    ITypeSymbol? errorType)
    {
        if ((kind is BoundCarrierKind.Option or BoundCarrierKind.Result) &&
            (!UnionFacts.UsesCarrierRepresentation(carrierReceiver.Type) ||
             !UnionFacts.UsesCarrierRepresentation(carrierGeneric)))
        {
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        // Synthesize a payload local that exists only for this node’s WhenPresent binding
        var payloadLocal = CreateTempLocal("payload", payloadType, syntax);
        var payloadReceiver = new BoundLocalAccess(payloadLocal);

        BoundExpression BindCarrierMemberInvocation(MemberBindingExpressionSyntax memberBinding, InvocationExpressionSyntax invocation)
        {
            var member = BindMemberAccessOnReceiver(
                payloadReceiver, memberBinding.Name,
                preferMethods: true,
                allowEventAccess: false,
                suppressNullWarning: true,
                receiverTypeForLookup: payloadType,
                forceExtensionReceiver: true);
            if (IsErrorExpression(member))
                return AsErrorExpression(member);
            if (member is BoundMethodGroupExpression methodGroup)
                return BindInvocationOnMethodGroup(methodGroup, invocation);

            if (TryGetInvokedMemberName(memberBinding, out var memberName))
                _diagnostics.ReportNonInvocableMember(memberName, invocation.GetLocation());
            else
                _diagnostics.ReportInvalidInvocation(invocation.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        // Bind WhenPresent using payloadReceiver *as the receiver*.
        // (This is different from your current approach, which binds against `receiver`
        // but uses receiverTypeForLookup to *pretend* it’s payload.)
        BoundExpression whenPresent = syntax.WhenNotNull switch
        {
            MemberBindingExpressionSyntax memberBinding =>
                BindMemberAccessOnReceiver(
                    payloadReceiver,
                    memberBinding.Name,
                    preferMethods: false,
                    allowEventAccess: true,
                    suppressNullWarning: true,
                    receiverTypeForLookup: payloadType,
                    forceExtensionReceiver: true),

            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax mb } inv =>
                BindCarrierMemberInvocation(mb, inv),

            ElementBindingExpressionSyntax eb =>
                BindElementAccessExpression(payloadReceiver, eb.ArgumentList, eb, suppressNullWarning: true),

            InvocationExpressionSyntax { Expression: ReceiverBindingExpressionSyntax } inv =>
                BindInvocationExpressionCore(payloadReceiver, "Invoke", inv.ArgumentList, syntax.Expression, inv, suppressNullWarning: true),

            _ => ErrorExpression(reason: BoundExpressionReason.NotFound)
        };

        whenPresent = IsErrorExpression(whenPresent) ? AsErrorExpression(whenPresent) : whenPresent;

        var accessType = whenPresent.Type ?? Compilation.ErrorTypeSymbol;
        var u = (accessType.UnwrapLiteralType() ?? accessType).GetNonNullableType();

        ITypeSymbol resultType =
            kind == BoundCarrierKind.Option ? carrierGeneric.Construct(u) :
            kind == BoundCarrierKind.Result ? carrierGeneric.Construct(u, errorType!) :
            throw new InvalidOperationException();

        ValidateCarrierConditionalAccessReturnCompatibility(syntax, kind, resultType, errorType);

        // Resolve carrier APIs as symbols so codegen doesn't have to guess via reflection.
        INamedTypeSymbol? carrierTypeSymbol = resultType as INamedTypeSymbol;

        INamedTypeSymbol? resOk = null;
        INamedTypeSymbol? resErr = null;
        IMethodSymbol? resTryOk = null;
        IMethodSymbol? resTryErr = null;
        IMethodSymbol? resOkValue = null;
        IMethodSymbol? resErrData = null;
        IMethodSymbol? resOkCtor = null;
        IMethodSymbol? resErrCtor = null;
        IMethodSymbol? resImplOk = null;
        IMethodSymbol? resImplErr = null;

        // Receiver Result<TPayload, E> API
        INamedTypeSymbol? recvOk = null;
        INamedTypeSymbol? recvErr = null;
        IMethodSymbol? recvOkValue = null;
        IMethodSymbol? recvErrData = null;

        INamedTypeSymbol? optSome = null;
        INamedTypeSymbol? optNone = null;
        IMethodSymbol? optTrySome = null;
        IMethodSymbol? optSomeValue = null;
        IMethodSymbol? optSomeCtor = null;
        IMethodSymbol? optNoneCtor = null;
        IMethodSymbol? optImplSome = null;
        IMethodSymbol? optImplNone = null;

        if (carrierTypeSymbol is not null)
        {
            if (kind == BoundCarrierKind.Result)
            {
                // Receiver is Result<T,E>
                var receiverNamed = (INamedTypeSymbol)carrierReceiver.Type!;

                // Receiver Result<TPayload, E> cases
                recvOk = FindUnionCase(receiverNamed, "Ok");
                recvErr = FindUnionCase(receiverNamed, "Error");
                resTryOk = FindTryGetValueMethod(receiverNamed, recvOk);
                resTryErr = FindTryGetValueMethod(receiverNamed, recvErr);
                recvOkValue = recvOk is not null ? FindPropertyGetter(recvOk, "Value") : null;
                recvErrData = recvErr is not null
                    ? (FindPropertyGetter(recvErr, "Data") ?? FindPropertyGetter(recvErr, "Value"))
                    : null;

                // Result<U,E> cases (carrier)
                resOk = FindUnionCase(carrierTypeSymbol, "Ok");
                resErr = FindUnionCase(carrierTypeSymbol, "Error");

                if (resOk is not null)
                {
                    resOkValue = FindPropertyGetter(resOk, "Value");
                    resOkCtor = FindSingleArgCtor(resOk);
                    resImplOk = FindImplicitConversion(carrierTypeSymbol, resOk);
                }

                if (resErr is not null)
                {
                    resErrData = FindPropertyGetter(resErr, "Data") ?? FindPropertyGetter(resErr, "Value");
                    resErrCtor = FindSingleArgCtor(resErr) ?? FindParameterlessCtor(resErr);
                    resImplErr = FindImplicitConversion(carrierTypeSymbol, resErr);
                }
            }
            else if (kind == BoundCarrierKind.Option)
            {
                var receiverNamed = (INamedTypeSymbol)carrierReceiver.Type!;
                optSome = FindUnionCase(carrierTypeSymbol, "Some");
                optNone = FindUnionCase(carrierTypeSymbol, "None");
                optTrySome = FindTryGetValueMethod(receiverNamed, optSome);

                if (optSome is not null)
                {
                    optSomeValue = FindPropertyGetter(optSome, "Value") ?? FindPropertyGetter(optSome, "Item") ?? FindPropertyGetter(optSome, "Payload");
                    optSomeCtor = FindSingleArgCtor(optSome);
                    optImplSome = FindImplicitConversion(carrierTypeSymbol, optSome);
                }

                if (optNone is not null)
                {
                    optNoneCtor = FindParameterlessCtor(optNone);
                    optImplNone = FindImplicitConversion(carrierTypeSymbol, optNone);
                }
            }
        }

        return new BoundCarrierConditionalAccessExpression(
            carrierReceiver,
            kind,
            payloadType,
            payloadLocal,
            whenPresent,
            resultType,
            carrierType: carrierTypeSymbol,

            receiverResultOkCaseType: recvOk,
            receiverResultErrorCaseType: recvErr,
            receiverResultOkValueGetter: recvOkValue,
            receiverResultErrorDataGetter: recvErrData,

            resultOkCaseType: resOk,
            resultErrorCaseType: resErr,
            resultTryGetValueForOkCaseMethod: resTryOk,
            resultTryGetValueForErrorCaseMethod: resTryErr,
            resultOkValueGetter: resOkValue,
            resultErrorDataGetter: resErrData,
            resultOkCtor: resOkCtor,
            resultErrorCtor: resErrCtor,
            resultImplicitFromOk: resImplOk,
            resultImplicitFromError: resImplErr,

            optionSomeCaseType: optSome,
            optionNoneCaseType: optNone,
            optionTryGetValueMethod: optTrySome,
            optionSomeValueGetter: optSomeValue,
            optionSomeCtor: optSomeCtor,
            optionNoneCtorOrFactory: optNoneCtor,
            optionImplicitFromSome: optImplSome,
            optionImplicitFromNone: optImplNone);
    }

    private void ValidateCarrierConditionalAccessReturnCompatibility(
        ConditionalAccessExpressionSyntax syntax,
        BoundCarrierKind kind,
        ITypeSymbol resultType,
        ITypeSymbol? resultErrorType)
    {
        if (!IsDirectReturnExpression(syntax))
            return;

        if (!TryGetEnclosingCarrierReturnType(out var enclosingReturnType) ||
            enclosingReturnType is null ||
            enclosingReturnType.TypeKind == TypeKind.Error)
        {
            return;
        }

        if (!TryGetPropagationInfo(enclosingReturnType, out var enclosingInfo))
        {
            ReportCannotConvertFromTypeToType(
                resultType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                enclosingReturnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.OperatorToken.GetLocation());
            return;
        }

        if ((kind == BoundCarrierKind.Result && enclosingInfo.Kind != PropagationKind.Result) ||
            (kind == BoundCarrierKind.Option && enclosingInfo.Kind != PropagationKind.Option))
        {
            ReportCannotConvertFromTypeToType(
                resultType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                enclosingInfo.UnionType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.OperatorToken.GetLocation());
            return;
        }

        if (kind != BoundCarrierKind.Result ||
            resultErrorType is null ||
            enclosingInfo.ErrorPayloadType is null)
        {
            return;
        }

        var errorConversion = Compilation.ClassifyConversion(resultErrorType, enclosingInfo.ErrorPayloadType);
        if (!errorConversion.Exists)
        {
            ReportCannotConvertFromTypeToType(
                resultErrorType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                enclosingInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.OperatorToken.GetLocation());
        }
        else if (errorConversion.IsImplicit &&
                 !errorConversion.IsIdentity &&
                 errorConversion.IsUserDefined &&
                 errorConversion.MethodSymbol is { } conversionMethod)
        {
            _diagnostics.ReportResultPropagationImplicitErrorConversion(
                resultErrorType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                enclosingInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                conversionMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.OperatorToken.GetLocation());
        }
    }

    private static bool IsDirectReturnExpression(ConditionalAccessExpressionSyntax syntax)
    {
        if (syntax.Parent is ReturnStatementSyntax returnStatement &&
            ReferenceEquals(returnStatement.Expression, syntax))
        {
            return true;
        }

        return syntax.Parent is ArrowExpressionClauseSyntax arrowExpressionClause &&
               ReferenceEquals(arrowExpressionClause.Expression, syntax);
    }

    private static INamedTypeSymbol? FindUnionCase(INamedTypeSymbol carrier, string name)
    {
        var tryGetCase = carrier.GetMembers("TryGetValue")
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == 1)
            .Select(method => method.Parameters[0].GetByRefElementType().GetNonNullableType() as INamedTypeSymbol)
            .FirstOrDefault(type => type is not null && IsUnionCaseNameMatch(carrier, type, name));
        if (tryGetCase is not null)
            return tryGetCase;

        var constructorCase = carrier.Constructors
            .Where(ctor => !ctor.IsStatic && ctor.Parameters.Length == 1)
            .Select(ctor => ctor.Parameters[0].Type.GetNonNullableType() as INamedTypeSymbol)
            .FirstOrDefault(type => type is not null && IsUnionCaseNameMatch(carrier, type, name));
        if (constructorCase is not null)
            return constructorCase;

        var directCase = carrier.GetTypeMembers(name).FirstOrDefault()
            ?? carrier.GetTypeMembers()
                .FirstOrDefault(t => IsUnionCaseNameMatch(carrier, t, name));
        if (directCase is not null)
            return directCase;

        var union = carrier.TryGetUnion();
        if (union is not null)
        {
            var caseSymbol = union.Variants.FirstOrDefault(@case => @case.Name == name);
            if (caseSymbol is INamedTypeSymbol namedCase)
                return namedCase;
        }

        return null;
    }

    private static bool IsUnionCaseNameMatch(INamedTypeSymbol carrier, INamedTypeSymbol caseType, string logicalName)
    {
        if (string.Equals(caseType.Name, logicalName, StringComparison.Ordinal))
            return true;

        if (caseType.Name.StartsWith(logicalName, StringComparison.Ordinal))
            return true;

        var rawCaseMetadataName = caseType.OriginalDefinition is INamedTypeSymbol originalDefinition
            ? originalDefinition.MetadataName
            : caseType.MetadataName;

        if (UnionFacts.TryGetLogicalCaseNameFromMetadata(carrier.Name, rawCaseMetadataName, out var metadataLogicalName) &&
            string.Equals(metadataLogicalName, logicalName, StringComparison.Ordinal))
        {
            return true;
        }

        if (carrier.OriginalDefinition is INamedTypeSymbol carrierDefinition &&
            !string.Equals(carrierDefinition.Name, carrier.Name, StringComparison.Ordinal) &&
            UnionFacts.TryGetLogicalCaseNameFromMetadata(carrierDefinition.Name, rawCaseMetadataName, out metadataLogicalName))
        {
            return string.Equals(metadataLogicalName, logicalName, StringComparison.Ordinal);
        }

        return false;
    }

    private static IMethodSymbol? FindTryGetValueMethod(INamedTypeSymbol receiverType, INamedTypeSymbol? caseType)
    {
        if (caseType is null)
            return null;

        var caseTypePlain = caseType.GetNonNullableType();

        return receiverType.GetMembers("TryGetValue")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.Parameters.Length == 1 &&
                m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                (m.Parameters[0].RefKind == RefKind.Out || m.Parameters[0].RefKind == RefKind.Ref) &&
                m.Parameters[0].GetByRefElementType().GetNonNullableType().MetadataIdentityEquals(caseTypePlain));
    }

    private static IMethodSymbol? FindPropertyGetter(INamedTypeSymbol type, string propertyName)
    {
        return type.GetMembers(propertyName)
            .OfType<IPropertySymbol>()
            .Select(p => p.GetMethod)
            .FirstOrDefault(m => m is not null);
    }

    private static IMethodSymbol? FindSingleArgCtor(INamedTypeSymbol type)
    {
        return type.Constructors.FirstOrDefault(c => c.Parameters.Length == 1);
    }

    private static IMethodSymbol? FindParameterlessCtor(INamedTypeSymbol type)
    {
        return type.Constructors.FirstOrDefault(c => c.Parameters.Length == 0);
    }

    private static IMethodSymbol? FindImplicitConversion(INamedTypeSymbol carrier, INamedTypeSymbol from)
    {
        return carrier.GetMembers("op_Implicit")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.IsStatic &&
                m.ReturnType.MetadataIdentityEquals(carrier) &&
                m.Parameters.Length == 1 &&
                m.Parameters[0].Type.MetadataIdentityEquals(from));
    }

    private BoundExpression BindNullableConditionalAccessExpression(
        ConditionalAccessExpressionSyntax syntax,
        BoundExpression receiver)
    {
        if (receiver is BoundMemberAccessExpression { Member: IEventSymbol eventSymbol } eventAccess)
        {
            receiver = BindEventInvocationReceiver(eventSymbol, eventAccess, syntax.Expression);
            if (IsErrorExpression(receiver))
                return receiver is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);
        }

        if (receiver.Type is { TypeKind: not TypeKind.Error } receiverType &&
            !receiverType.IsNullable)
        {
            _diagnostics.ReportConditionalAccessRequiresNullableReceiver(
                receiverType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.OperatorToken.GetLocation());
        }

        var lookupType = GetConditionalAccessLookupType(receiver.Type);
        var whenNotNullReceiver = GetConditionalAccessWhenNotNullReceiver(receiver, lookupType);

        BoundExpression whenNotNull;

        switch (syntax.WhenNotNull)
        {
            case MemberBindingExpressionSyntax memberBinding:
                {
                    // Bind using the same logic as '.' (including extensions),
                    // but:
                    // - lookup against underlying type (T? -> T)
                    // - suppress null warning (because it's conditional access)
                    // - force extension lookup even if receiver is nullable
                    whenNotNull = BindMemberAccessOnReceiver(
                        whenNotNullReceiver,
                        memberBinding.Name,
                        preferMethods: false,
                        allowEventAccess: true,
                        suppressNullWarning: true,
                        receiverTypeForLookup: lookupType,
                        forceExtensionReceiver: true);

                    if (IsErrorExpression(whenNotNull))
                        whenNotNull = AsErrorExpression(whenNotNull);

                    break;
                }

            case InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax memberBinding } invocation:
                {
                    // First bind "<receiver>.<name>" as a method group (incl. extensions),
                    // then reuse normal invocation binding.
                    var member = BindMemberAccessOnReceiver(
                        whenNotNullReceiver,
                        memberBinding.Name,
                        preferMethods: true,
                        allowEventAccess: false,
                        suppressNullWarning: true,
                        receiverTypeForLookup: lookupType,
                        forceExtensionReceiver: true);

                    if (IsErrorExpression(member))
                    {
                        whenNotNull = AsErrorExpression(member);
                        break;
                    }

                    if (member is BoundMethodGroupExpression mg)
                    {
                        whenNotNull = BindInvocationOnMethodGroup(mg, invocation);
                        if (IsErrorExpression(whenNotNull))
                            whenNotNull = AsErrorExpression(whenNotNull);
                    }
                    else
                    {
                        if (TryGetInvokedMemberName(memberBinding, out var memberName))
                            _diagnostics.ReportNonInvocableMember(memberName, invocation.GetLocation());
                        else
                            _diagnostics.ReportInvalidInvocation(invocation.GetLocation());
                        whenNotNull = ErrorExpression(reason: BoundExpressionReason.NotFound);
                    }

                    break;
                }

            case InvocationExpressionSyntax { Expression: ReceiverBindingExpressionSyntax } invocation:
                {
                    var result = BindInvocationExpressionCore(whenNotNullReceiver, "Invoke", invocation.ArgumentList, syntax.Expression, invocation, suppressNullWarning: true);
                    whenNotNull = IsErrorExpression(result) ? AsErrorExpression(result) : result;
                    break;
                }

            case ElementBindingExpressionSyntax elementBinding:
                {
                    whenNotNull = BindElementAccessExpression(whenNotNullReceiver, elementBinding.ArgumentList, elementBinding, suppressNullWarning: true);
                    if (IsErrorExpression(whenNotNull))
                        whenNotNull = AsErrorExpression(whenNotNull);
                    break;
                }

            default:
                whenNotNull = ErrorExpression(reason: BoundExpressionReason.NotFound);
                break;
        }

        var resultType = whenNotNull.Type;
        if (!resultType.IsNullable)
            resultType = resultType.GetNullableType();

        return new BoundConditionalAccessExpression(receiver, whenNotNull, resultType);
    }

    private BoundExpression BindElementAccessExpression(ElementAccessExpressionSyntax syntax)
    {
        var receiver = BindExpression(syntax.Expression);
        return BindElementAccessExpression(receiver, syntax.ArgumentList, syntax, suppressNullWarning: false);
    }

    private BoundExpression BindElementAccessExpression(
        BoundExpression receiver,
        BracketedArgumentListSyntax argumentList,
        SyntaxNode syntax,
        bool suppressNullWarning)
    {
        if (!suppressNullWarning)
            ReportNullableValueMemberAccess(receiver, syntax);

        var argumentExprs = argumentList.Arguments.Select(x => BindExpression(x.Expression)).ToArray();

        if (IsErrorExpression(receiver))
            return receiver is BoundErrorExpression boundError
                ? boundError
                : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

        var firstErrorArg = argumentExprs.FirstOrDefault(IsErrorExpression);
        if (firstErrorArg is not null)
            return firstErrorArg is BoundErrorExpression errorArg
                ? errorArg
                : new BoundErrorExpression(
                    firstErrorArg.Type ?? Compilation.ErrorTypeSymbol,
                    null,
                    BoundExpressionReason.OtherError);

        var receiverType = receiver.Type;
        if (receiverType is null)
        {
            _diagnostics.ReportInvalidInvocation(syntax.GetLocation());
            return new BoundErrorExpression(
                Compilation.GetSpecialType(SpecialType.System_Object),
                null,
                BoundExpressionReason.NotFound);
        }

        if (receiverType.TypeKind is TypeKind.Array)
        {
            var arrayType = (IArrayTypeSymbol)receiverType;
            if (argumentExprs.Length == 1 && IsRangeType(argumentExprs[0].Type))
            {
                return BindArrayRangeAccess(receiver, arrayType, argumentExprs[0], argumentList.Arguments[0].Expression);
            }

            if (argumentExprs.Any(argument => IsRangeType(argument.Type)))
            {
                _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(
                    FormatIndexableReceiverType(receiverType),
                    syntax.GetLocation());
                return new BoundErrorExpression(receiverType, null, BoundExpressionReason.NotFound);
            }

            if (!ValidateArrayElementAccess(arrayType, argumentExprs, syntax))
                return new BoundErrorExpression(receiverType, null, BoundExpressionReason.NotFound);

            return new BoundArrayAccessExpression(receiver, argumentExprs, ((IArrayTypeSymbol)receiverType).ElementType);
        }

        var indexer = ResolveIndexer(receiverType, argumentExprs, argumentList.Arguments, requireSetter: false, out var convertedArguments);

        if (indexer is null)
        {
            ReportIndexerResolutionFailure(receiverType, argumentExprs, syntax.GetLocation(), requireSetter: false);
            return new BoundErrorExpression(receiverType, null, BoundExpressionReason.NotFound);
        }

        var access = new BoundIndexerAccessExpression(receiver, convertedArguments, indexer);
        return TryGetIndexerByRefElementType(indexer, out var elementType)
            ? new BoundDereferenceExpression(access, elementType)
            : access;
    }

    private static bool TryGetIndexerByRefElementType(
        IPropertySymbol indexer,
        out ITypeSymbol elementType)
    {
        var type = indexer.Type;
        if (type is NullableTypeSymbol nullable)
            type = nullable.UnderlyingType;

        if (type is not RefTypeSymbol refType)
        {
            elementType = null!;
            return false;
        }

        var getterReturnType = indexer.GetMethod?.ReturnType;
        if (getterReturnType is NullableTypeSymbol nullableGetterReturn)
            getterReturnType = nullableGetterReturn.UnderlyingType;

        elementType = getterReturnType switch
        {
            RefTypeSymbol getterRefType => getterRefType.ElementType,
            not null => getterReturnType,
            _ => refType.ElementType
        };
        return true;
    }

    private static bool IsReadOnlyByRefIndexer(IPropertySymbol indexer)
        => indexer.GetMethod?.IsReadOnly == true ||
           indexer.ContainingType is
           {
               Name: "ReadOnlySpan",
               ContainingNamespace: { } containingNamespace
           } &&
           containingNamespace.ToDisplayString() == "System";

    private IPropertySymbol? ResolveIndexer(
        ITypeSymbol receiverType,
        BoundExpression[] arguments,
        SeparatedSyntaxList<ArgumentSyntax> argumentSyntaxes,
        bool requireSetter,
        out BoundExpression[] convertedArguments)
    {
        convertedArguments = arguments;

        var candidates = GetIndexerCandidates(receiverType, requireSetter)
            .Where(p => p.GetMethod!.Parameters.Length == arguments.Length)
            .ToArray();

        IPropertySymbol? best = null;
        BoundExpression[]? bestConverted = null;
        var bestConversionCost = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var converted = new BoundExpression[arguments.Length];
            var conversionCost = 0;
            var valid = true;

            for (var i = 0; i < arguments.Length; i++)
            {
                var parameterType = candidate.GetMethod!.Parameters[i].Type;
                var convertedArgument = arguments[i];

                if (parameterType.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(convertedArgument))
                {
                    convertedArgument = BindLambdaToDelegateIfNeeded(convertedArgument, parameterType);

                    if (!IsAssignable(parameterType, convertedArgument.Type, out var conversion))
                    {
                        valid = false;
                        break;
                    }

                    convertedArgument = ApplyConversion(convertedArgument, parameterType, conversion, argumentSyntaxes[i].Expression);
                    if (!conversion.IsIdentity)
                        conversionCost++;
                }

                converted[i] = convertedArgument;
            }

            if (!valid)
                continue;

            if (best is null || conversionCost < bestConversionCost)
            {
                best = candidate;
                bestConverted = converted;
                bestConversionCost = conversionCost;
            }
        }

        if (best is not null && bestConverted is not null)
            convertedArguments = bestConverted;

        return best;
    }

    private void ReportIndexerResolutionFailure(
        ITypeSymbol receiverType,
        BoundExpression[] arguments,
        Location location,
        bool requireSetter)
    {
        var candidates = GetIndexerCandidates(receiverType, requireSetter).ToArray();
        var receiverTypeText = FormatIndexableReceiverType(receiverType);

        if (candidates.Length == 0)
        {
            _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(receiverTypeText, location);
            return;
        }

        _diagnostics.ReportNoMatchingIndexer(
            receiverTypeText,
            FormatIndexerArgumentTypes(arguments),
            FormatIndexerParameterTypes(candidates),
            location);
    }

    private static IEnumerable<IPropertySymbol> GetIndexerCandidates(ITypeSymbol receiverType, bool requireSetter)
        => receiverType
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.GetMethod is not null &&
                        (p.IsIndexer || p.GetMethod.Parameters.Length > 0) &&
                        (!requireSetter || p.SetMethod is not null));

    private static string FormatIndexableReceiverType(ITypeSymbol type)
        => type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string FormatIndexerArgumentTypes(BoundExpression[] arguments)
    {
        if (arguments.Length == 0)
            return "no arguments";

        return string.Join(", ", arguments.Select(argument => argument.Type is { } type
            ? type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : "unknown"));
    }

    private static string FormatIndexerParameterTypes(IEnumerable<IPropertySymbol> candidates)
        => string.Join("; ", candidates
            .Select(FormatIndexerParameterTypes)
            .Distinct(StringComparer.Ordinal));

    private static string FormatIndexerParameterTypes(IPropertySymbol indexer)
    {
        var parameters = indexer.GetMethod!.Parameters;
        if (parameters.Length == 0)
            return "none";

        var parameterTypes = parameters.Select(parameter =>
            $"'{parameter.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}'");

        return parameters.Length == 1
            ? parameterTypes.Single()
            : $"({string.Join(", ", parameterTypes)})";
    }

    private BoundExpression BindArrayRangeAccess(
        BoundExpression receiver,
        IArrayTypeSymbol arrayType,
        BoundExpression rangeExpression,
        ExpressionSyntax rangeSyntax)
    {
        if (arrayType.Rank != 1)
        {
            _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(
                FormatIndexableReceiverType(arrayType),
                rangeSyntax.GetLocation());
            return new BoundErrorExpression(arrayType, null, BoundExpressionReason.NotFound);
        }

        var rangeType = GetRangeType();
        if (rangeType.TypeKind != TypeKind.Error && ShouldAttemptConversion(rangeExpression))
        {
            if (!IsAssignable(rangeType, rangeExpression.Type, out var conversion))
            {
                ReportCannotConvertFromTypeToType(
                    rangeExpression.Type.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    rangeType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    rangeSyntax.GetLocation());
                return new BoundErrorExpression(rangeType, null, BoundExpressionReason.TypeMismatch);
            }

            rangeExpression = ApplyConversion(rangeExpression, rangeType, conversion, rangeSyntax);
        }

        var subArrayMethod = ResolveArraySliceMethod(arrayType);
        if (subArrayMethod is null)
        {
            _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(
                FormatIndexableReceiverType(arrayType),
                rangeSyntax.GetLocation());
            return new BoundErrorExpression(arrayType, null, BoundExpressionReason.NotFound);
        }

        return new BoundInvocationExpression(subArrayMethod, [receiver, rangeExpression], receiver: null, extensionReceiver: null);
    }

    private IMethodSymbol? ResolveArraySliceMethod(IArrayTypeSymbol arrayType)
    {
        var runtimeHelpers = Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.RuntimeHelpers");
        var rangeType = GetRangeType();
        if (runtimeHelpers is null || runtimeHelpers.TypeKind == TypeKind.Error || rangeType.TypeKind == TypeKind.Error)
            return null;

        foreach (var candidate in runtimeHelpers.GetMembers("GetSubArray").OfType<IMethodSymbol>())
        {
            if (!candidate.IsStatic || !candidate.IsGenericMethod || candidate.TypeParameters.Length != 1 || candidate.Parameters.Length != 2)
                continue;

            if (candidate.Parameters[0].Type is not IArrayTypeSymbol parameterArray || parameterArray.Rank != 1)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(candidate.Parameters[1].Type, rangeType))
                continue;

            return candidate.Construct(arrayType.ElementType);
        }

        return null;
    }

    private bool ValidateArrayElementAccess(IArrayTypeSymbol arrayType, BoundExpression[] arguments, SyntaxNode syntax)
    {
        if (arrayType.Rank == 1)
            return true;

        if (arguments.Length != arrayType.Rank)
        {
            _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(
                FormatIndexableReceiverType(arrayType),
                syntax.GetLocation());
            return false;
        }

        if (arguments.Any(argument => argument is BoundIndexExpression))
        {
            _diagnostics.ReportCannotApplyIndexingWithToAnExpressionOfType(
                FormatIndexableReceiverType(arrayType),
                syntax.GetLocation());
            return false;
        }

        return true;
    }

    private static bool IsRangeType(ITypeSymbol? type)
        => type is INamedTypeSymbol named && named.ToFullyQualifiedMetadataName() == "System.Range";

    private BoundExpression BindAssignment(ExpressionSyntax leftSyntax, ExpressionSyntax rightSyntax, SyntaxNode node, SyntaxKind operatorTokenKind)
    {
        if (operatorTokenKind != SyntaxKind.EqualsToken)
            return BindCompoundAssignment(leftSyntax, rightSyntax, node, operatorTokenKind);

        if (leftSyntax is DiscardExpressionSyntax)
        {
            var right = BindExpression(rightSyntax);
            var assignmentType = right.Type ?? Compilation.ErrorTypeSymbol;
            var discardType = assignmentType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : assignmentType;
            var pattern = new BoundDiscardPattern(discardType);
            return BoundFactory.CreatePatternAssignmentExpression(assignmentType, pattern, right);
        }

        if (leftSyntax is ElementAccessExpressionSyntax elementAccess)
        {
            var receiver = BindExpression(elementAccess.Expression);
            var args = elementAccess.ArgumentList.Arguments.Select(x => BindExpression(x.Expression)).ToArray();

            if (IsErrorExpression(receiver))
                return receiver is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(
                        receiver.Type ?? Compilation.ErrorTypeSymbol,
                        null,
                        BoundExpressionReason.OtherError);

            var firstErrorArg = args.FirstOrDefault(IsErrorExpression);
            if (firstErrorArg is not null)
                return firstErrorArg is BoundErrorExpression errorArg
                    ? errorArg
                    : new BoundErrorExpression(
                        firstErrorArg.Type ?? Compilation.ErrorTypeSymbol,
                        null,
                        BoundExpressionReason.OtherError);

            if (receiver.Type is IArrayTypeSymbol arrayType)
            {
                if (args.Any(argument => IsRangeType(argument.Type)))
                {
                    _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                    return new BoundErrorExpression(receiver.Type!, null, BoundExpressionReason.NotFound);
                }

                var arrayRightExpression = BindExpressionWithTargetType(rightSyntax, arrayType.ElementType);

                if (IsErrorExpression(arrayRightExpression))
                    return AsErrorExpression(arrayRightExpression);

                if (arrayType.ElementType.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(arrayRightExpression))
                {
                    var right = BindLambdaToDelegateIfNeeded(arrayRightExpression, arrayType.ElementType);
                    if (!IsAssignable(arrayType.ElementType, right.Type, out var conversion))
                    {
                        ReportCannotAssignFromTypeToType(right.Type, arrayType.ElementType, rightSyntax.GetLocation());
                        return new BoundErrorExpression(arrayType.ElementType, null, BoundExpressionReason.TypeMismatch);
                    }

                    right = ApplyConversion(right, arrayType.ElementType, conversion, rightSyntax);
                    arrayRightExpression = right;
                }

                return BoundFactory.CreateArrayAssignmentExpression(
                    new BoundArrayAccessExpression(receiver, args, arrayType.ElementType),
                    arrayRightExpression);
            }

            var indexer = ResolveIndexer(receiver.Type!, args, elementAccess.ArgumentList.Arguments, requireSetter: true, out var convertedArguments);

            if (indexer is null)
            {
                indexer = ResolveIndexer(
                    receiver.Type!,
                    args,
                    elementAccess.ArgumentList.Arguments,
                    requireSetter: false,
                    out convertedArguments);
            }

            var byRefElementType = indexer is not null &&
                TryGetIndexerByRefElementType(indexer, out var resolvedByRefElementType)
                    ? resolvedByRefElementType
                    : null;
            var isWritableByRef = byRefElementType is not null && !IsReadOnlyByRefIndexer(indexer!);
            if (indexer is null || !indexer.IsMutable && !isWritableByRef)
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundErrorExpression(receiver.Type!, null, BoundExpressionReason.NotFound);
            }

            var assignmentTargetType = byRefElementType ?? indexer.Type;
            var indexerRightExpression = BindExpressionWithTargetType(rightSyntax, assignmentTargetType);

            if (IsErrorExpression(indexerRightExpression))
                return AsErrorExpression(indexerRightExpression);

            var access = new BoundIndexerAccessExpression(receiver, convertedArguments, indexer);
            if (assignmentTargetType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(indexerRightExpression))
            {
                var right = BindLambdaToDelegateIfNeeded(indexerRightExpression, assignmentTargetType);
                if (!IsAssignable(assignmentTargetType, right.Type, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right.Type, assignmentTargetType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(assignmentTargetType, null, BoundExpressionReason.TypeMismatch);
                }

                right = ApplyConversion(right, assignmentTargetType, conversion, rightSyntax);
                indexerRightExpression = right;
            }

            if (byRefElementType is not null)
                return BoundFactory.CreateByRefAssignmentExpression(access, byRefElementType, indexerRightExpression);

            return BoundFactory.CreateIndexerAssignmentExpression(access, indexerRightExpression);
        }

        // Fall back to normal variable/property assignment
        var left = BindExpressionAllowingEvent(leftSyntax);

        if (IsErrorExpression(left))
            return AsErrorExpression(left);

        if (left.Symbol is IEventSymbol eventSymbol)
        {
            _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(eventSymbol.Name, leftSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (left is BoundConditionalAccessExpression conditionalAccess &&
            leftSyntax is ConditionalAccessExpressionSyntax conditionalSyntax)
        {
            return BindConditionalAccessAssignment(conditionalSyntax, conditionalAccess, rightSyntax, node, operatorTokenKind);
        }

        if (left is BoundDereferenceExpression dereference)
        {
            if (dereference.Reference is BoundIndexerAccessExpression indexerAccess &&
                IsReadOnlyByRefIndexer(indexerAccess.Indexer))
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundErrorExpression(dereference.ElementType, null, BoundExpressionReason.NotFound);
            }

            var right2 = BindExpressionWithTargetType(rightSyntax, dereference.ElementType);

            if (IsErrorExpression(right2))
                return AsErrorExpression(right2);

            var converted = ConvertValueForAssignment(right2, dereference.ElementType, rightSyntax);
            if (converted is BoundErrorExpression)
                return converted;

            return BoundFactory.CreateByRefAssignmentExpression(dereference.Reference, dereference.ElementType, converted);
        }

        if (left is BoundLocalAccess localAccess)
        {
            var localSymbol = localAccess.Local;
            var localType = localSymbol.Type;

            var rightTargetType = localType is RefTypeSymbol refTypeLocal
                ? refTypeLocal.ElementType
                : localType;
            var right2 = BindExpressionWithTargetType(rightSyntax, rightTargetType);

            if (IsErrorExpression(right2))
                return AsErrorExpression(right2);

            if (localType is RefTypeSymbol refTypeLocalType)
            {
                var converted = ConvertValueForAssignment(right2, refTypeLocalType.ElementType, rightSyntax);
                if (converted is BoundErrorExpression)
                    return converted;

                return BoundFactory.CreateByRefAssignmentExpression(localAccess, refTypeLocalType.ElementType, converted);
            }

            if (!localSymbol.IsMutable)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            if (right2 is BoundEmptyCollectionExpression)
            {
                return BoundFactory.CreateLocalAssignmentExpression(localSymbol, localAccess, new BoundEmptyCollectionExpression(localSymbol.Type));
            }

            if (localType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right2))
            {
                right2 = BindLambdaToDelegateIfNeeded(right2, localType);
                if (!IsAssignable(localType, right2.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right2.Type!, localType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(localType, null, BoundExpressionReason.TypeMismatch);
                }

                right2 = ApplyConversion(right2, localType, conversion, rightSyntax);
            }

            return BoundFactory.CreateLocalAssignmentExpression(localSymbol, localAccess, right2);
        }
        else if (left is BoundParameterAccess parameterAccess)
        {
            var parameterSymbol = parameterAccess.Parameter;
            var parameterType = parameterSymbol.Type;

            if (!parameterSymbol.IsMutable)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var rightTargetType = parameterSymbol.RefKind is RefKind.Ref or RefKind.Out
                ? parameterSymbol.GetByRefElementType()
                : parameterType;
            var right2 = BindExpressionWithTargetType(rightSyntax, rightTargetType);

            if (IsErrorExpression(right2))
                return AsErrorExpression(right2);

            if (parameterSymbol.RefKind is RefKind.Ref or RefKind.Out)
            {
                var byRefElementType = parameterSymbol.GetByRefElementType();
                var converted = ConvertValueForAssignment(right2, byRefElementType, rightSyntax);
                if (converted is BoundErrorExpression)
                    return converted;

                return BoundFactory.CreateByRefAssignmentExpression(parameterAccess, byRefElementType, converted);
            }

            if (parameterType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right2))
            {
                right2 = BindLambdaToDelegateIfNeeded(right2, parameterType);
                if (!IsAssignable(parameterType, right2.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right2.Type!, parameterType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(parameterType, null, BoundExpressionReason.TypeMismatch);
                }

                right2 = ApplyConversion(right2, parameterType, conversion, rightSyntax);
            }

            return BoundFactory.CreateParameterAssignmentExpression(parameterSymbol, parameterAccess, right2);
        }
        else if (left.Symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.IsConst)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.NotFound);
            }

            var receiver = GetReceiver(left);

            var fieldInputType = NullableMetadataFacts.GetInputType(fieldSymbol, fieldSymbol.Type);
            var right2 = BindExpressionWithTargetType(rightSyntax, fieldInputType);

            if (IsErrorExpression(right2))
                return AsErrorExpression(right2);

            if (!CanAssignToField(fieldSymbol, receiver, leftSyntax))
                return new BoundErrorExpression(fieldSymbol.Type, fieldSymbol, BoundExpressionReason.NotFound);

            if (right2 is BoundEmptyCollectionExpression)
            {
                return CreateFieldAssignmentExpression(receiver, fieldSymbol, BoundFactory.CreateEmptyCollectionExpression(fieldSymbol.Type));
            }

            if (fieldSymbol.Type.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right2))
            {
                right2 = BindLambdaToDelegateIfNeeded(right2, fieldInputType);
                if (!IsAssignable(fieldInputType, right2.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right2.Type!, fieldInputType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.TypeMismatch);
                }

                right2 = ApplyConversion(right2, fieldSymbol.Type, conversion, rightSyntax);
            }

            return CreateFieldAssignmentExpression(receiver, fieldSymbol, right2);
        }
        else if (left.Symbol is IPropertySymbol propertySymbol)
        {
            SourceFieldSymbol? backingField = null;
            var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);

            var receiver = GetReceiver(left);

            if (IsInitOnly(propertySymbol) && !IsInInitOnlyAssignmentContext)
            {
                // IMPORTANT: reuse your *existing* “property has no setter / not writable” diagnostic path
                // (same one you use when SetMethod is null), so you don’t need a new diagnostic.

                // Example shape (adapt to your existing diag helpers):
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                return new BoundErrorExpression(propertySymbol.Type ?? Compilation.ErrorTypeSymbol, propertySymbol, BoundExpressionReason.UnsupportedOperation);
            }

            if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
            {
                if (!TryGetWritableAutoPropertyBackingField(propertySymbol, left, out backingField))
                {
                    _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }
            }

            var propertyInputType = NullableMetadataFacts.GetInputType(propertySymbol, propertySymbol.Type);
            var right2 = BindExpressionWithTargetType(rightSyntax, propertyInputType);

            if (IsErrorExpression(right2))
                return AsErrorExpression(right2);

            if (right2 is BoundEmptyCollectionExpression)
            {
                var empty = new BoundEmptyCollectionExpression(propertySymbol.Type);

                if (backingField is not null)
                {
                    if (!CanAssignToField(backingField, receiver, leftSyntax))
                        return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

                    return CreateFieldAssignmentExpression(receiver, backingField, empty);
                }

                return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, empty);
            }

            if (propertySymbol.Type.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right2))
            {
                right2 = BindLambdaToDelegateIfNeeded(right2, propertyInputType);
                if (!IsAssignable(propertyInputType, right2.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right2.Type!, propertyInputType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(propertySymbol.Type, null, BoundExpressionReason.TypeMismatch);
                }

                right2 = ApplyConversion(right2, propertySymbol.Type, conversion, rightSyntax);
            }

            if (backingField is not null)
            {
                if (!CanAssignToField(backingField, receiver, leftSyntax))
                    return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

                return CreateFieldAssignmentExpression(receiver, backingField, right2);
            }

            return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, right2);
        }

        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
    {
        return BindAssignment(syntax.Left, syntax.Right, syntax, syntax.OperatorToken.Kind);
    }

    private BoundExpression ConvertValueForAssignment(BoundExpression value, ITypeSymbol targetType, SyntaxNode syntax)
    {
        if (targetType.TypeKind == TypeKind.Error || value.Type is null || !ShouldAttemptConversion(value))
            return value;

        value = BindLambdaToDelegateIfNeeded(value, targetType);

        if (!IsAssignable(targetType, value.Type, out var conversion))
        {
            ReportCannotAssignFromTypeToType(value.Type, targetType, syntax.GetLocation());
            return new BoundErrorExpression(targetType, null, BoundExpressionReason.TypeMismatch);
        }

        return ApplyConversion(value, targetType, conversion, syntax);
    }

    private BoundStatement BindAssignmentStatement(AssignmentStatementSyntax syntax)
    {
        var bound = BindAssignment(syntax.Left, syntax.Right, syntax, syntax.OperatorToken.Kind);
        if (bound is BoundAssignmentExpression assignment)
            return new BoundAssignmentStatement(assignment);

        return new BoundExpressionStatement(bound);
    }

    private BoundStatement BindPatternDeclarationAssignmentStatement(PatternDeclarationAssignmentStatementSyntax syntax)
    {
        if (syntax.BindingKeyword.Kind is not (SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword))
        {
            _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(syntax.BindingKeyword.GetLocation());
            return new BoundExpressionStatement(ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation));
        }

        var inlineBindingKeyword = FindFirstInlinePatternBindingKeyword(syntax.Left);
        if (inlineBindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
        {
            _diagnostics.ReportPatternDeclarationBindingKeywordConflict(
                syntax.BindingKeyword.Text,
                inlineBindingKeyword.Text,
                inlineBindingKeyword.GetLocation());
        }

        if (syntax.ElseClause is not null)
            return BindLetElseStatement(syntax);

        ITypeSymbol? targetType = syntax.Left is VariablePatternSyntax
        {
            Designation: TypedVariableDesignationSyntax typedDesignation
        }
            ? ResolveTypeSyntaxOrError(typedDesignation.TypeAnnotation.Type)
            : null;
        var right = BindExpressionWithTargetType(syntax.Right, targetType);
        var bound = BindPatternAssignment(syntax.Left, right, syntax, syntax.BindingKeyword.Kind);
        if (bound is BoundAssignmentExpression assignment)
            return new BoundAssignmentStatement(assignment);

        return new BoundExpressionStatement(bound);
    }

    private BoundStatement BindLetElseStatement(PatternDeclarationAssignmentStatementSyntax syntax)
    {
        var condition = BindPatternStatementCondition(syntax.BindingKeyword, syntax.Left, syntax.Right);
        var elseStatement = syntax.ElseClause!.Statement;
        var elseBound = BindStatement(elseStatement);

        if (!LetElseClauseExits(elseStatement))
        {
            _diagnostics.Report(
                Diagnostic.Create(
                    CompilerDiagnostics.LetElseClauseMustNotCompleteNormally,
                    syntax.ElseClause.GetLocation()));
        }

        if (condition is BoundIsPatternExpression isPattern)
            RegisterPatternLocalsForCurrentLookup(isPattern.Pattern);

        return new BoundIfStatement(
            condition,
            new BoundBlockStatement(Array.Empty<BoundStatement>()),
            elseBound);
    }

    private bool LetElseClauseExits(StatementSyntax statement)
    {
        if (IsEarlyExitStatement(statement))
            return true;

        if (statement is not BlockStatementSyntax block)
            return false;

        var flow = SemanticModel.AnalyzeControlFlowInternal(
            new ControlFlowRegion(block),
            block,
            analyzeJumpPoints: false);
        return flow is { Succeeded: true, EndPointIsReachable: false };
    }

    private static SyntaxToken FindFirstInlinePatternBindingKeyword(PatternSyntax pattern)
    {
        foreach (var variablePattern in pattern.DescendantNodesAndSelf().OfType<VariablePatternSyntax>())
        {
            var keyword = variablePattern.BindingKeyword;
            if (keyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                return keyword;
        }

        foreach (var single in pattern.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
        {
            var keyword = single.BindingKeyword;
            if (keyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                return keyword;
        }

        return SyntaxFactory.Token(SyntaxKind.None);
    }

    private BoundExpression BindAssignment(ExpressionOrPatternSyntax leftSyntax, ExpressionSyntax rightSyntax, SyntaxNode node, SyntaxKind operatorTokenKind)
    {
        if (leftSyntax is ExpressionSyntax leftExpression)
            return BindAssignment(leftExpression, rightSyntax, node, operatorTokenKind);

        var right = BindExpression(rightSyntax);
        if (leftSyntax is PatternSyntax patternSyntax)
        {
            if (operatorTokenKind != SyntaxKind.EqualsToken)
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
            }

            return BindPatternAssignment(patternSyntax, right, node);
        }

        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
        return ErrorExpression(right.Type, reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindMemberAccessExpression(
       MemberAccessExpressionSyntax memberAccess,
       bool preferMethods = false,
       bool allowEventAccess = false)
    {
        if (memberAccess.IsKind(SyntaxKind.PointerMemberAccessExpression))
        {
            return BindPointerMemberAccessExpression(memberAccess, preferMethods, allowEventAccess);
        }

        // First, attempt to treat the *entire* member access as a type name.
        // This enables nested-type construction like `Outer<int>.Inner<string>` and `Foo<int>.Bar`.
        // Avoid rewriting syntax trees; resolve directly in the binder.
        if (TryBindMemberAccessExpressionAsType(memberAccess, out var resolvedType) &&
            resolvedType.TypeKind != TypeKind.Error)
        {
            // If the resolved type is a DU case type (e.g. Err.MissingName), promote it to a
            // BoundUnionCaseExpression whose Type is the union root so that generic type
            // inference infers Err rather than the individual case type MissingName.
            if (BindDiscriminatedUnionCaseType(resolvedType) is { } unionCaseExpr)
                return unionCaseExpr;

            return new BoundTypeExpression(resolvedType);
        }

        // Regular expression receiver binding.
        // IMPORTANT: Many name nodes (IdentifierName/GenericName/QualifiedName) also implement TypeSyntax.
        // We must NOT treat those as type receivers here, or ordinary member access like `handler.Handle`
        // would be bound as a static type access.
        BoundExpression receiver;
        if (memberAccess.Expression is TypeSyntax ts && memberAccess.Expression is not NameSyntax)
        {
            receiver = BindTypeSyntaxAsExpression(ts);
        }
        else
        {
            receiver = BindExpression(memberAccess.Expression);
        }

        if (IsErrorExpression(receiver))
            return receiver is BoundErrorExpression boundError
                ? boundError
                : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

        if (receiver.Type is { } boundReceiverType && boundReceiverType.ContainsErrorType())
            return new BoundErrorExpression(boundReceiverType, receiver.Symbol, BoundExpressionReason.OtherError);

        if (memberAccess.OperatorToken.Kind == SyntaxKind.ArrowToken)
        {
            if (!IsUnsafeEnabled)
            {
                _diagnostics.ReportPointerOperationRequiresUnsafe(memberAccess.OperatorToken.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
            }

            if (receiver.Type is not IPointerTypeSymbol pointerReceiver)
            {
                var receiverTypeText = (receiver.Type ?? Compilation.ErrorTypeSymbol)
                    .ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                _diagnostics.ReportPointerMemberAccessRequiresPointer(receiverTypeText, memberAccess.OperatorToken.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }

            var dereferencedReceiver = new BoundDereferenceExpression(receiver, pointerReceiver.PointedAtType);
            return BindMemberAccessOnReceiver(
                dereferencedReceiver,
                memberAccess.Name,
                preferMethods,
                allowEventAccess,
                suppressNullWarning: true,
                receiverTypeForLookup: pointerReceiver.PointedAtType,
                forceExtensionReceiver: false);
        }

        ReportNullableValueMemberAccess(receiver, memberAccess.Expression);

        var result = BindMemberAccessOnReceiver(
            receiver,
            memberAccess.Name,
            preferMethods,
            allowEventAccess,
            suppressNullWarning: true,
            receiverTypeForLookup: null,
            forceExtensionReceiver: false);

        return result;
    }

    private BoundExpression BindPointerMemberAccessExpression(
        MemberAccessExpressionSyntax memberAccess,
        bool preferMethods,
        bool allowEventAccess)
    {
        // Bind receiver expression (same pattern as normal member access).
        BoundExpression receiver;
        if (memberAccess.Expression is TypeSyntax ts && memberAccess.Expression is not NameSyntax)
            receiver = BindTypeSyntaxAsExpression(ts);
        else
            receiver = BindExpression(memberAccess.Expression);

        if (IsErrorExpression(receiver))
            return receiver is BoundErrorExpression boundError
                ? boundError
                : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

        if (!IsUnsafeEnabled)
        {
            _diagnostics.ReportPointerOperationRequiresUnsafe(memberAccess.OperatorToken.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        if (receiver.Type is not IPointerTypeSymbol pointerReceiver)
        {
            var receiverTypeText = (receiver.Type ?? Compilation.ErrorTypeSymbol)
                .ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

            _diagnostics.ReportPointerMemberAccessRequiresPointer(receiverTypeText, memberAccess.OperatorToken.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        var lookupType = pointerReceiver.PointedAtType;

        // Bind as if it were `(*receiver).Name` to reuse the proven lookup logic.
        var dereferencedReceiver = new BoundDereferenceExpression(receiver, lookupType);

        var bound = BindMemberAccessOnReceiver(
            dereferencedReceiver,
            memberAccess.Name,
            preferMethods,
            allowEventAccess,
            suppressNullWarning: true,
            receiverTypeForLookup: lookupType,
            forceExtensionReceiver: false);

        if (IsErrorExpression(bound))
            return AsErrorExpression(bound);

        // If it’s a plain member access, preserve the pointer nature explicitly.
        if (bound is BoundMemberAccessExpression ma)
            return new BoundPointerMemberAccessExpression(receiver, ma.Member, ma.Reason);

        // Method groups / invocations / other special forms: return as-is for now.
        return bound;
    }

    /// <summary>
    /// Shared member-access core used by both '.' and '?.' so conditional access can see extensions.
    /// </summary>
    private BoundExpression BindMemberAccessOnReceiver(
        BoundExpression receiver,
        SimpleNameSyntax simpleName,
        bool preferMethods,
        bool allowEventAccess,
        bool suppressNullWarning,
        ITypeSymbol? receiverTypeForLookup,
        bool forceExtensionReceiver)
    {
        // Raven feature: allow partial explicit method type arguments at the call site, e.g.:
        //   items.CountItems<double>(2)
        // Overload resolution will right-align the explicit args and infer the rest.
        // Therefore, member lookup must NOT require exact generic arity matches here.
        //
        // Implementation: strip the type-argument list for lookup so the existing method/extension
        // lookup can find candidates by name, then let OverloadResolver apply the explicit args.
        if (preferMethods && simpleName is GenericNameSyntax g)
        {
            var identifierOnly = SyntaxFactory.IdentifierName(g.Identifier);

            return BindMemberAccessOnReceiver(
                receiver,
                identifierOnly,
                preferMethods,
                allowEventAccess,
                suppressNullWarning,
                receiverTypeForLookup,
                forceExtensionReceiver);
        }

        // NOTE: For '.' we already reported null-ref and pass suppressNullWarning=true here.
        // For '?.' we also suppress, because conditional access handles null.
        // This method should not report possible null reference itself.

        if (simpleName.Identifier.IsMissing)
        {
            _diagnostics.ReportIdentifierExpected(simpleName.Identifier.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        var name = simpleName.Identifier.ValueText;
        ImmutableArray<ITypeSymbol>? explicitTypeArguments = null;
        GenericNameSyntax? genericTypeSyntax = null;

        if (simpleName is GenericNameSyntax genericName)
        {
            _ = BindTypeSyntax(genericName);

            var boundTypeArguments = TryBindTypeArguments(genericName);
            if (boundTypeArguments is null)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            explicitTypeArguments = boundTypeArguments;
            genericTypeSyntax = genericName;
        }

        var nameLocation = simpleName.GetLocation();
        // Deferred field to hold an inaccessible non-method member candidate.
        ISymbol? inaccessibleNonMethodMember = null;

        if (receiver is BoundNamespaceExpression nsExpr)
        {
            var member = nsExpr.Namespace.GetMembers(name).FirstOrDefault();

            if (member is INamespaceSymbol ns2)
                return new BoundNamespaceExpression(ns2);

            if (member is ITypeSymbol type)
                return new BoundTypeExpression(type);

            var namespaceMembers = Compilation.GetNamespaceMembers(nsExpr.Namespace, name, Compilation.Options.AllowNamespaceMemberImports);
            var topLevelMethods = namespaceMembers.OfType<IMethodSymbol>().ToImmutableArray();
            if (!topLevelMethods.IsDefaultOrEmpty)
            {
                if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                {
                    var instantiated = InstantiateMethodCandidates(topLevelMethods, typeArgs, genericTypeSyntax, nameLocation);
                    if (!instantiated.IsDefaultOrEmpty)
                        return BindMethodGroup(new BoundTypeExpression(topLevelMethods[0].ContainingType!), instantiated, nameLocation);
                }
                else
                {
                    return BindMethodGroup(new BoundTypeExpression(topLevelMethods[0].ContainingType!), topLevelMethods, nameLocation);
                }
            }

            var topLevelField = namespaceMembers.OfType<IFieldSymbol>().FirstOrDefault();
            if (topLevelField is not null)
            {
                if (!EnsureMemberAccessible(topLevelField, nameLocation, GetSymbolKindForDiagnostic(topLevelField)))
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                return new BoundMemberAccessExpression(new BoundTypeExpression(topLevelField.ContainingType!), topLevelField);
            }

            _diagnostics.ReportTypeOrNamespaceNameDoesNotExistInTheNamespace(name, nsExpr.Namespace.Name, nameLocation);
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (receiver is BoundTypeExpression typeExpr)
        {
            var typeLookupType = EnsureSourceMemberSignatureDeclaredForExactLookup(typeExpr.Type, name);

            if (preferMethods)
            {
                var methodCandidates = LookupStaticMethodCandidates(name, typeLookupType, nameLocation);

                if (!methodCandidates.IsDefaultOrEmpty)
                {
                    if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                    {
                        var instantiated = InstantiateMethodCandidates(methodCandidates, typeArgs, genericTypeSyntax, nameLocation);
                        if (!instantiated.IsDefaultOrEmpty)
                            return BindMethodGroup(typeExpr, instantiated, nameLocation);
                    }
                    else
                    {
                        return BindMethodGroup(typeExpr, methodCandidates, nameLocation);
                    }
                }
                else
                {
                    var extensionCandidates = LookupExtensionStaticMethods(name, typeLookupType).ToImmutableArray();

                    if (!extensionCandidates.IsDefaultOrEmpty)
                    {
                        if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                        {
                            var instantiated = InstantiateMethodCandidates(extensionCandidates, typeArgs, genericTypeSyntax, nameLocation);
                            if (!instantiated.IsDefaultOrEmpty)
                                return BindMethodGroup(typeExpr, instantiated, nameLocation);
                        }
                        else
                        {
                            return BindMethodGroup(typeExpr, extensionCandidates, nameLocation);
                        }
                    }
                }
            }

            var nonMethodMember = new SymbolQuery(name, typeLookupType, IsStatic: true)
                .Lookup(this)
                .FirstOrDefault(static m => m is not IMethodSymbol);

            if (nonMethodMember is not null)
            {
                if (nonMethodMember is IEventSymbol && !allowEventAccess)
                {
                    _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(nonMethodMember.Name, nameLocation);
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }

                if (!IsSymbolAccessible(nonMethodMember))
                {
                    inaccessibleNonMethodMember = nonMethodMember;
                }
                else
                {
                    if (nonMethodMember is ITypeSymbol typeMember)
                    {
                        if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null && typeMember is INamedTypeSymbol namedMember)
                        {
                            if (!ValidateTypeArgumentConstraints(namedMember, typeArgs, i => GetTypeArgumentLocation(genericTypeSyntax.TypeArgumentList.Arguments, genericTypeSyntax.GetLocation(), i), namedMember.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
                                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

                            if (TryConstructGeneric(namedMember, typeArgs, namedMember.Arity) is INamedTypeSymbol constructedType)
                                typeMember = constructedType;
                        }

                        // For DU case types (e.g. Err.MissingName), return a BoundUnionCaseExpression
                        // whose Type is the union root (Err) so that generic type inference infers
                        // the union root rather than the individual case type.
                        if (BindDiscriminatedUnionCaseType(typeMember, typeExpr.Type as INamedTypeSymbol) is { } unionCaseExpr)
                            return ApplyTargetTypedUnionCarrier(unionCaseExpr, simpleName);

                        return new BoundTypeExpression(typeMember);
                    }

                    ReportObsoleteIfNeeded(nonMethodMember, nameLocation);
                    return new BoundMemberAccessExpression(typeExpr, nonMethodMember);
                }
            }

            if (!preferMethods)
            {
                var methodCandidates = LookupStaticMethodCandidates(name, typeLookupType, nameLocation);

                if (!methodCandidates.IsDefaultOrEmpty)
                {
                    if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                    {
                        var instantiated = InstantiateMethodCandidates(methodCandidates, typeArgs, genericTypeSyntax, nameLocation);
                        if (!instantiated.IsDefaultOrEmpty)
                            return BindMethodGroup(typeExpr, instantiated, nameLocation);
                    }
                    else
                    {
                        return BindMethodGroup(typeExpr, methodCandidates, nameLocation);
                    }
                }
                else
                {
                    var extensionCandidates = LookupExtensionStaticMethods(name, typeLookupType).ToImmutableArray();

                    if (!extensionCandidates.IsDefaultOrEmpty)
                    {
                        if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                        {
                            var instantiated = InstantiateMethodCandidates(extensionCandidates, typeArgs, genericTypeSyntax, nameLocation);
                            if (!instantiated.IsDefaultOrEmpty)
                                return BindMethodGroup(typeExpr, instantiated, nameLocation);
                        }
                        else
                        {
                            return BindMethodGroup(typeExpr, extensionCandidates, nameLocation);
                        }
                    }
                }
            }

            var member = new SymbolQuery(name, typeLookupType, IsStatic: true)
                .Lookup(this)
                .FirstOrDefault();

            if (member is null)
            {
                var extensionProperties = LookupExtensionStaticProperties(name, typeExpr.Type).ToImmutableArray();

                if (!extensionProperties.IsDefaultOrEmpty)
                {
                    var accessibleProperties = GetAccessibleProperties(extensionProperties, nameLocation);

                    if (!accessibleProperties.IsDefaultOrEmpty)
                    {
                        if (accessibleProperties.Length == 1)
                            return BindExtensionPropertyGetInvocation(typeExpr, accessibleProperties[0], receiverSyntax: simpleName);

                        var ambiguousMethods = accessibleProperties
                            .Select(p => p.GetMethod ?? p.SetMethod)
                            .Where(m => m is not null)
                            .Cast<IMethodSymbol>()
                            .ToImmutableArray();

                        if (!ambiguousMethods.IsDefaultOrEmpty)
                            _diagnostics.ReportCallIsAmbiguous(name, ambiguousMethods, nameLocation);

                        return ErrorExpression(
                            reason: BoundExpressionReason.Ambiguous,
                            candidates: AsSymbolCandidates(ambiguousMethods));
                    }

                    EnsureMemberAccessible(extensionProperties[0], nameLocation, GetSymbolKindForDiagnostic(extensionProperties[0]));
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
                }

                if (TryBindDiscriminatedUnionCase(typeLookupType, name, nameLocation) is BoundExpression unionCase)
                    return unionCase;

                if (TryBindSealedHierarchyCase(typeLookupType, name, explicitTypeArguments, genericTypeSyntax, nameLocation) is BoundExpression sealedCase)
                    return sealedCase;

                var typeName = typeExpr.Symbol!.Name;
                _diagnostics.ReportMemberDoesNotContainDefinition(typeName, simpleName.ToString(), nameLocation);
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            if (!EnsureMemberAccessible(member, nameLocation, GetSymbolKindForDiagnostic(member)))
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

            if (member is ITypeSymbol typeMemberSymbol)
            {
                if (BindDiscriminatedUnionCaseType(typeMemberSymbol) is { } unionCase)
                    return ApplyTargetTypedUnionCarrier(unionCase, simpleName);

                return new BoundTypeExpression(typeMemberSymbol);
            }

            if (member is IEventSymbol && !allowEventAccess)
            {
                _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(member.Name, nameLocation);
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            ReportObsoleteIfNeeded(member, nameLocation);
            return new BoundMemberAccessExpression(typeExpr, member);
        }

        if (receiver.Type is not null)
        {
            var receiverType = receiverTypeForLookup ?? (receiver.Type.UnwrapLiteralType() ?? receiver.Type);
            receiverType = EnsureSourceMemberSignatureDeclaredForExactLookup(receiverType, name);

            if (!suppressNullWarning)
                ReportNullableValueMemberAccess(receiver, simpleName);

            var allowExtensions = forceExtensionReceiver || IsExtensionReceiver(receiver);

            if (preferMethods)
            {
                var methodCandidates = ImmutableArray<IMethodSymbol>.Empty;

                var instanceMethods = LookupInstanceMethodCandidates(name, receiverType, nameLocation);

                if (!instanceMethods.IsDefaultOrEmpty)
                    methodCandidates = instanceMethods;

                if (allowExtensions)
                {
                    var extensionMethods = LookupExtensionMethods(name, receiverType)
                        .ToImmutableArray();

                    if (!extensionMethods.IsDefaultOrEmpty)
                    {
                        methodCandidates = methodCandidates.IsDefaultOrEmpty
                            ? extensionMethods
                            : methodCandidates.AddRange(extensionMethods);
                    }
                }

                if (!methodCandidates.IsDefaultOrEmpty)
                {
                    if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                    {
                        var instantiated = InstantiateMethodCandidates(methodCandidates, typeArgs, genericTypeSyntax, nameLocation);
                        if (!instantiated.IsDefaultOrEmpty)
                            return BindMethodGroup(receiver, instantiated, nameLocation);
                    }
                    else
                    {
                        return BindMethodGroup(receiver, methodCandidates, nameLocation);
                    }
                }
            }

            var nonMethodMember = new SymbolQuery(name, receiverType, IsStatic: false)
                .Lookup(this)
                .FirstOrDefault(static m => m is not IMethodSymbol);

            if (nonMethodMember is not null)
            {
                if (nonMethodMember is IEventSymbol && !allowEventAccess)
                {
                    _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(nonMethodMember.Name, nameLocation);
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }

                // DO NOT report accessibility yet — may still bind to methods/extensions
                if (IsSymbolAccessible(nonMethodMember))
                {
                    ReportObsoleteIfNeeded(nonMethodMember, nameLocation);

                    if (nonMethodMember is IPropertySymbol property &&
                        TryGetFieldOnlyPropertyBackingField(property, out var backingField))
                    {
                        return new BoundMemberAccessExpression(receiver, backingField);
                    }

                    return new BoundMemberAccessExpression(receiver, nonMethodMember);
                }
                inaccessibleNonMethodMember = nonMethodMember;
            }

            if (!preferMethods)
            {
                var methodCandidates = ImmutableArray<IMethodSymbol>.Empty;

                var instanceMethods = new SymbolQuery(name, receiverType, IsStatic: false)
                    .LookupMethods(this)
                    .ToImmutableArray();

                if (!instanceMethods.IsDefaultOrEmpty)
                    methodCandidates = instanceMethods;

                if (allowExtensions)
                {
                    var extensionMethods = LookupExtensionMethods(name, receiverType)
                        .ToImmutableArray();

                    if (!extensionMethods.IsDefaultOrEmpty)
                    {
                        methodCandidates = methodCandidates.IsDefaultOrEmpty
                            ? extensionMethods
                            : methodCandidates.AddRange(extensionMethods);
                    }
                }

                if (!methodCandidates.IsDefaultOrEmpty)
                {
                    if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                    {
                        var instantiated = InstantiateMethodCandidates(methodCandidates, typeArgs, genericTypeSyntax, nameLocation);
                        if (!instantiated.IsDefaultOrEmpty)
                            return BindMethodGroup(receiver, instantiated, nameLocation);
                    }
                    else
                    {
                        return BindMethodGroup(receiver, methodCandidates, nameLocation);
                    }
                }
            }

            var instanceMember = new SymbolQuery(name, receiverType, IsStatic: false)
                .Lookup(this)
                .FirstOrDefault();

            if (instanceMember is not null)
            {
                if (!EnsureMemberAccessible(instanceMember, nameLocation, GetSymbolKindForDiagnostic(instanceMember)))
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                if (instanceMember is IEventSymbol && !allowEventAccess)
                {
                    _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(instanceMember.Name, nameLocation);
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }

                ReportObsoleteIfNeeded(instanceMember, nameLocation);

                if (instanceMember is IPropertySymbol property &&
                    TryGetFieldOnlyPropertyBackingField(property, out var backingField))
                {
                    return new BoundMemberAccessExpression(receiver, backingField);
                }

                return new BoundMemberAccessExpression(receiver, instanceMember);
            }

            if (allowExtensions)
            {
                var extensionProperties = LookupExtensionProperties(name, receiverType).ToImmutableArray();

                if (!extensionProperties.IsDefaultOrEmpty)
                {
                    var accessibleProperties = GetAccessibleProperties(extensionProperties, nameLocation);

                    if (!accessibleProperties.IsDefaultOrEmpty)
                    {
                        if (accessibleProperties.Length == 1)
                        {
                            var receiverSyntaxNode = simpleName.Parent ?? (SyntaxNode)simpleName;
                            return BindExtensionPropertyGetInvocation(receiver, accessibleProperties[0], receiverSyntaxNode);
                        }

                        var ambiguousMethods = accessibleProperties
                            .Select(p => p.GetMethod ?? p.SetMethod)
                            .Where(m => m is not null)
                            .Cast<IMethodSymbol>()
                            .ToImmutableArray();

                        if (!ambiguousMethods.IsDefaultOrEmpty)
                            _diagnostics.ReportCallIsAmbiguous(name, ambiguousMethods, nameLocation);

                        return ErrorExpression(
                            reason: BoundExpressionReason.Ambiguous,
                            candidates: AsSymbolCandidates(ambiguousMethods));
                    }

                    EnsureMemberAccessible(extensionProperties[0], nameLocation, GetSymbolKindForDiagnostic(extensionProperties[0]));
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
                }
            }
        }

        // Fallback: if we saw a non-method member but it was inaccessible, report that now.
        if (inaccessibleNonMethodMember is not null)
        {
            EnsureMemberAccessible(inaccessibleNonMethodMember, nameLocation, GetSymbolKindForDiagnostic(inaccessibleNonMethodMember));
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
        }

        if (preferMethods && receiver.Type is not null)
        {
            var receiverType = receiverTypeForLookup ?? (receiver.Type.UnwrapLiteralType() ?? receiver.Type);
            _diagnostics.ReportMemberDoesNotContainDefinition(receiverType.Name, name, nameLocation ?? simpleName.GetLocation() ?? Location.None);
        }
        else
        {
            _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, nameLocation ?? simpleName.GetLocation() ?? Location.None);
        }
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression ApplyTargetTypedUnionCarrier(BoundExpression expression, SyntaxNode syntax)
    {
        if (expression is not BoundUnionCaseExpression unionCase)
            return expression;

        var targetType = GetTargetType(syntax);
        if (targetType is null)
            return expression;

        targetType = UnwrapAlias(targetType);
        targetType = UnwrapTaskLikeTargetType(targetType);

        var targetUnion =
            targetType.TryGetUnion() as INamedTypeSymbol
            ?? targetType.TryGetUnionCase()?.Union as INamedTypeSymbol;

        if (targetUnion is null)
            return expression;

        if (!SymbolEqualityComparer.Default.Equals(
                (unionCase.UnionType.OriginalDefinition as INamedTypeSymbol) ?? unionCase.UnionType,
                (targetUnion.OriginalDefinition as INamedTypeSymbol) ?? targetUnion))
        {
            return expression;
        }

        if (SymbolEqualityComparer.Default.Equals(unionCase.UnionType, targetUnion))
            return expression;

        if (IsUninstantiatedGenericType(unionCase.UnionType) || !IsUninstantiatedGenericType(targetUnion))
        {
            var projectedCaseType = ProjectCaseTypeToUnionArguments(unionCase.CaseType, targetUnion) as INamedTypeSymbol
                ?? unionCase.CaseType;
            var projectedConstructor = unionCase.CaseConstructor;
            if (projectedConstructor is not null &&
                !SymbolEqualityComparer.Default.Equals(projectedCaseType, unionCase.CaseType))
            {
                projectedConstructor = projectedCaseType.InstanceConstructors.FirstOrDefault(candidate =>
                    candidate.Parameters.Length == unionCase.CaseConstructor.Parameters.Length);
            }

            return new BoundUnionCaseExpression(
                targetUnion,
                projectedCaseType,
                projectedConstructor,
                unionCase.Arguments);
        }

        return expression;
    }

    private BoundExpression BindExtensionPropertyGetInvocation(
        BoundExpression receiver,
        IPropertySymbol property,
        SyntaxNode receiverSyntax)
    {
        ReportObsoleteIfNeeded(property, receiverSyntax.GetLocation());

        var getter = property.GetMethod;
        if (getter is null)
            return new BoundMemberAccessExpression(receiver, property);

        // Lowered extension-property getter should be: static get_Xxx(self: TReceiver, ...) -> TResult
        var looksLikeLoweredExtensionGetter =
            getter.IsStatic &&
            getter.Parameters.Length > 0 &&
            string.Equals(getter.Parameters[0].Name, "self", StringComparison.Ordinal);

        // If this isn’t the lowered extension form, keep it as a normal property access.
        if (!looksLikeLoweredExtensionGetter && !getter.IsExtensionMethod)
            return new BoundMemberAccessExpression(receiver, property);

        // Property getter has no call-site args.
        var boundArguments = Array.Empty<BoundArgument>();

        // Receiver becomes the extension receiver.
        var extensionReceiver = receiver;

        // Critical: run type argument inference so T/E get inferred from `self` (and/or return type later).
        var inferred = OverloadResolver.ApplyTypeArgumentInference(
            getter,
            extensionReceiver,
            boundArguments,
            Compilation,
            this,
            explicitTypeArguments: ImmutableArray<ITypeSymbol>.Empty);

        var chosen = inferred ?? getter;

        var convertedArgs = ConvertInvocationArguments(
            chosen,
            boundArguments,
            extensionReceiver,
            receiverSyntax,
            receiverSyntax,
            out var convertedExtensionReceiver);

        // Extension call: no instance receiver, only ExtensionReceiver.
        ReportObsoleteIfNeeded(chosen, receiverSyntax.GetLocation());
        return new BoundInvocationExpression(
            chosen,
            convertedArgs,
            receiver: null,
            convertedExtensionReceiver);
    }

    // --- your BindMemberBindingExpression and below remains unchanged ---
    // (keeping your original implementation)

    private BoundExpression BindMemberBindingExpression(
        MemberBindingExpressionSyntax memberBinding,
        bool allowEventAccess = false)
    {
        // Member bindings like `.Human` / `.Male` are target-typed. They can only be resolved when
        // a target type is available (e.g. argument position, assignment, return, etc.).
        var expectedType = GetTargetType(memberBinding);

        if (expectedType is null && memberBinding.Parent is InvocationExpressionSyntax invocation)
        {
            expectedType = GetTargetType(invocation);

            if (expectedType is null &&
                invocation.Parent is ReturnStatementSyntax &&
                ContainingSymbol is IMethodSymbol containingMethod)
            {
                expectedType = GetReturnTargetType(containingMethod);
            }
        }

        if (expectedType is not null && expectedType.TypeKind != TypeKind.Error)
        {
            return BindTargetTypedMemberAccess(memberBinding.Name, expectedType, allowEventAccess);
        }

        // RAV2010
        var memberName = memberBinding.Name.Identifier.ValueText;
        _diagnostics.ReportMemberAccessRequiresTargetType(memberName, memberBinding.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.MissingType);
    }

    private BoundExpression BindMemberBindingExpression(
        MemberBindingExpressionSyntax memberBinding,
        ITypeSymbol expectedType,
        bool allowEventAccess = false)
    {
        return BindTargetTypedMemberAccess(memberBinding.Name, expectedType, allowEventAccess);
    }

    private BoundExpression BindTargetTypedMemberAccess(
        SimpleNameSyntax simpleName,
        ITypeSymbol expectedType,
        bool allowEventAccess = false)
    {
        // Target-typed member bindings should operate on the *value* target type.
        // In async contexts the expected type may be Task<T>/ValueTask<T>; unwrap so `.None` etc.
        // bind against `T` rather than the task-like wrapper.
        expectedType = UnwrapTaskLikeTargetType(expectedType);

        var memberName = simpleName.Identifier.ValueText;
        ImmutableArray<ITypeSymbol>? explicitTypeArguments = null;
        GenericNameSyntax? genericTypeSyntax = null;

        if (simpleName is GenericNameSyntax genericName)
        {
            var typeArgs = TryBindTypeArguments(genericName);
            if (typeArgs is null)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            explicitTypeArguments = typeArgs;
            genericTypeSyntax = genericName;
        }

        var nameLocation = simpleName.GetLocation();

        var methodCandidates = new SymbolQuery(memberName, expectedType, IsStatic: true)
            .LookupMethods(this)
            .ToImmutableArray();

        if (!methodCandidates.IsDefaultOrEmpty)
        {
            if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
            {
                var instantiated = InstantiateMethodCandidates(methodCandidates, typeArgs, genericTypeSyntax, simpleName.GetLocation());
                if (!instantiated.IsDefaultOrEmpty)
                    return BindMethodGroup(new BoundTypeExpression(expectedType), instantiated, nameLocation);
            }
            else
            {
                return BindMethodGroup(new BoundTypeExpression(expectedType), methodCandidates, nameLocation);
            }
        }

        var member = new SymbolQuery(memberName, expectedType, IsStatic: true)
            .Lookup(this)
            .FirstOrDefault();

        if (member is null)
        {
            var extensionCandidates = LookupExtensionStaticMethods(memberName, expectedType).ToImmutableArray();

            if (!extensionCandidates.IsDefaultOrEmpty)
            {
                if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
                {
                    var instantiated = InstantiateMethodCandidates(extensionCandidates, typeArgs, genericTypeSyntax, simpleName.GetLocation());
                    if (!instantiated.IsDefaultOrEmpty)
                        return BindMethodGroup(new BoundTypeExpression(expectedType), instantiated, nameLocation);
                }
                else
                {
                    return BindMethodGroup(new BoundTypeExpression(expectedType), extensionCandidates, nameLocation);
                }
            }

            var extensionProperties = LookupExtensionStaticProperties(memberName, expectedType).ToImmutableArray();

            if (!extensionProperties.IsDefaultOrEmpty)
            {
                var accessibleProperties = GetAccessibleProperties(extensionProperties, nameLocation);

                if (!accessibleProperties.IsDefaultOrEmpty)
                {
                    if (accessibleProperties.Length == 1)
                    {
                        ReportObsoleteIfNeeded(accessibleProperties[0], nameLocation);
                        return new BoundMemberAccessExpression(new BoundTypeExpression(expectedType), accessibleProperties[0]);
                    }

                    var ambiguousMethods = accessibleProperties
                        .Select(p => p.GetMethod ?? p.SetMethod)
                        .Where(m => m is not null)
                        .Cast<IMethodSymbol>()
                        .ToImmutableArray();

                    if (!ambiguousMethods.IsDefaultOrEmpty)
                        _diagnostics.ReportCallIsAmbiguous(memberName, ambiguousMethods, nameLocation);

                    return ErrorExpression(
                        reason: BoundExpressionReason.Ambiguous,
                        candidates: AsSymbolCandidates(ambiguousMethods));
                }

                EnsureMemberAccessible(extensionProperties[0], nameLocation, GetSymbolKindForDiagnostic(extensionProperties[0]));
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
            }

            if (TryBindDiscriminatedUnionCase(expectedType, memberName, nameLocation) is BoundExpression unionCase)
                return unionCase;

            _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(memberName, nameLocation ?? Location.None);
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (!EnsureMemberAccessible(member, nameLocation, GetSymbolKindForDiagnostic(member)))
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

        if (member is ITypeSymbol typeMember)
        {
            // Prefer contextual DU-case binding so case type arguments are projected from the
            // expected union carrier (e.g. `.Created` in `HttpResult<()>` should not stay
            // `HttpResult<T>`).
            if (TryBindDiscriminatedUnionCase(expectedType, memberName, nameLocation) is { } contextualUnionCase)
            {
                return contextualUnionCase;
            }

            if (BindDiscriminatedUnionCaseType(typeMember, expectedType as INamedTypeSymbol) is { } unionCase)
            {
                // Unit-case sugar: `.Case` => `.Case(())` for single-Unit payload cases,
                // but ONLY when `.Case` is used as a standalone expression.
                var isInvocationCallee =
                    simpleName.Parent is MemberBindingExpressionSyntax mb &&
                    mb.Parent is InvocationExpressionSyntax inv &&
                    ReferenceEquals(inv.Expression, mb);

                // Unit-payload sugar: when the case has no resolved constructor yet (null
                // CaseConstructor on BoundUnionCaseExpression) it may have a unit-arg ctor.
                // Also handle the legacy BoundTypeExpression path.
                if (!isInvocationCallee)
                {
                    INamedTypeSymbol? caseTypeForUnit = null;
                    INamedTypeSymbol? unionTypeForUnit = null;

                    if (unionCase is BoundUnionCaseExpression { CaseConstructor: null } rawUnionCase)
                    {
                        caseTypeForUnit = rawUnionCase.CaseType;
                        unionTypeForUnit = rawUnionCase.UnionType;
                    }
                    else if (unionCase is BoundTypeExpression { Type: INamedTypeSymbol legacyCaseType } &&
                             legacyCaseType.TryGetUnionCase() is not null)
                    {
                        caseTypeForUnit = legacyCaseType;
                    }

                    if (caseTypeForUnit is not null)
                    {
                        var unitArgCtor = caseTypeForUnit.Constructors.FirstOrDefault(ctor =>
                            ctor.Parameters.Length == 1 &&
                            IsUnitType(ctor.Parameters[0].Type));

                        if (unitArgCtor is not null)
                        {
                            if (!EnsureMemberAccessible(unitArgCtor, nameLocation, "constructor"))
                                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
                            ReportObsoleteIfNeeded(unitArgCtor, nameLocation);

                            var unitType = unitArgCtor.Parameters[0].Type;
                            var unitValue = new BoundUnitExpression(unitType);

                            // Return a BoundUnionCaseExpression so the type is the union root.
                            if (unionTypeForUnit is not null)
                                return new BoundUnionCaseExpression(unionTypeForUnit, caseTypeForUnit, unitArgCtor, ImmutableArray.Create<BoundExpression>(unitValue));

                            return new BoundObjectCreationExpression(
                                unitArgCtor,
                                ImmutableArray.Create<BoundExpression>(unitValue));
                        }
                    }
                }

                return unionCase;
            }

            return new BoundTypeExpression(typeMember);
        }

        if (member is IEventSymbol && !allowEventAccess)
        {
            _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(member.Name, nameLocation);
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        ReportObsoleteIfNeeded(member, nameLocation);
        return new BoundMemberAccessExpression(new BoundTypeExpression(expectedType), member);
    }

    private BoundExpression? TryBindDiscriminatedUnionCase(ITypeSymbol? receiverType, string memberName, Location location)
    {
        var targetType = receiverType?.UnwrapLiteralType() ?? receiverType;
        targetType = UnwrapAlias(targetType);
        if (targetType is not null)
            targetType = UnwrapTaskLikeTargetType(targetType);

        if (targetType is not INamedTypeSymbol namedType)
            return null;

        if (!namedType.TryFindUnionCaseType(memberName, out var caseType))
            return null;

        var accessibleType = EnsureTypeAccessible(caseType, location);
        if (accessibleType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

        // Pass the union carrier so the returned expression's Type is the union root, enabling
        // correct generic type inference when the case value is used as a generic argument.
        return BindDiscriminatedUnionCaseType(accessibleType, namedType);
    }

    private BoundExpression? TryBindSealedHierarchyCase(
        ITypeSymbol? receiverType,
        string memberName,
        ImmutableArray<ITypeSymbol>? explicitTypeArguments,
        GenericNameSyntax? genericTypeSyntax,
        Location location)
    {
        var targetType = receiverType?.UnwrapLiteralType() ?? receiverType;
        targetType = UnwrapAlias(targetType);
        if (targetType is not null)
            targetType = UnwrapTaskLikeTargetType(targetType);

        if (targetType is not INamedTypeSymbol namedType)
            return null;

        if (!SealedHierarchyFacts.TryGetCaseDefinition(namedType, memberName, out _, out var caseDefinition) ||
            caseDefinition is null)
        {
            return null;
        }

        INamedTypeSymbol caseType = SealedHierarchyFacts.ProjectCaseTypeToHierarchyArguments(caseDefinition, namedType);

        if (explicitTypeArguments is { } typeArgs && genericTypeSyntax is not null)
        {
            if (!ValidateTypeArgumentConstraints(
                    caseDefinition,
                    typeArgs,
                    i => GetTypeArgumentLocation(genericTypeSyntax.TypeArgumentList.Arguments, genericTypeSyntax.GetLocation(), i),
                    caseDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
            {
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }

            if (TryConstructGeneric(caseDefinition, typeArgs, caseDefinition.Arity) is not INamedTypeSymbol constructedCaseType)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            caseType = constructedCaseType;
        }

        var accessibleType = EnsureTypeAccessible(caseType, location);
        if (accessibleType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

        return new BoundTypeExpression(accessibleType);
    }

    private static ITypeSymbol ProjectCaseTypeToUnionArguments(INamedTypeSymbol caseType, INamedTypeSymbol unionType)
    {
        if (!caseType.IsGenericType || caseType.TypeParameters.IsDefaultOrEmpty)
            return caseType;

        var unionDefinition = unionType.TryGetUnion() ?? unionType;
        var unionTypeParameters = unionDefinition.TypeParameters;
        var unionTypeArguments = unionType.TypeArguments;

        if (unionTypeParameters.IsDefaultOrEmpty || unionTypeArguments.IsDefaultOrEmpty)
            return caseType;

        var projectedArguments = new ITypeSymbol[caseType.TypeParameters.Length];
        var changed = false;
        var caseSymbol = caseType.TryGetUnionCase();
        if (caseSymbol is null)
            return caseType;

        for (var i = 0; i < caseType.TypeParameters.Length; i++)
        {
            var parameter = caseType.TypeParameters[i];
            if (UnionFacts.TryProjectCaseTypeParameterFromUnionArguments(
                caseSymbol,
                parameter,
                unionTypeParameters,
                unionTypeArguments,
                out var mapped))
            {
                projectedArguments[i] = mapped;
                if (!AreEquivalentGenericInstantiationArgument(mapped, parameter))
                    changed = true;
            }
            else
            {
                projectedArguments[i] = parameter;
            }
        }

        if (!changed)
            return caseType;

        return caseType.Construct(projectedArguments);
    }

    private ITypeSymbol UnwrapTaskLikeTargetType(ITypeSymbol type)
    {
        if (type is null)
            return Compilation.ErrorTypeSymbol;

        // Normalize literals first.
        type = type.UnwrapLiteralType() ?? type;

        if (type is INamedTypeSymbol named && !named.TypeArguments.IsDefaultOrEmpty)
        {
            // Unwrap common task-like wrappers so target-typed members bind to the underlying value.
            // This is important for constructs like `return .None` in an `async` method returning `Task<Option<T>>`.
            var ns = named.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.RavenErrorMessageFormat
                .WithTypeQualificationStyle(SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces));
            if (ns == "System.Threading.Tasks" && (named.Name == "Task" || named.Name == "ValueTask"))
                return named.TypeArguments[0];
        }

        return type;
    }

    /// <summary>
    /// Binds a discriminated union case type symbol as a value expression.
    /// When <paramref name="unionCarrier"/> is supplied the returned expression is a
    /// <see cref="BoundUnionCaseExpression"/> whose <c>Type</c> is the union root, so that
    /// generic type inference sees the union root rather than the individual case type.
    /// When <paramref name="unionCarrier"/> is not supplied (legacy call sites that have no
    /// explicit carrier context) the method falls back to the old behaviour and returns a
    /// <see cref="BoundObjectCreationExpression"/> or <see cref="BoundTypeExpression"/>.
    /// </summary>
    private BoundExpression? BindDiscriminatedUnionCaseType(ITypeSymbol typeMember, INamedTypeSymbol? unionCarrier = null)
    {
        var isUnionCase = typeMember.TryGetUnionCase() is not null;

        if (!isUnionCase &&
            typeMember is INamedTypeSymbol namedType &&
            namedType.ContainingType?.TryGetUnion() is not null)
        {
            isUnionCase = true;
        }

        if (!isUnionCase && typeMember.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is Raven.CodeAnalysis.Syntax.CaseDeclarationSyntax))
        {
            isUnionCase = true;
        }

        if (!isUnionCase)
            return null;

        if (typeMember is INamedTypeSymbol caseType)
        {
            // Resolve the union carrier: prefer the explicitly supplied one, then fall back to
            // what the case type itself reports.
            var resolvedUnion = unionCarrier
                ?? caseType.TryGetUnionCase()?.Union as INamedTypeSymbol
                ?? caseType.ContainingType?.TryGetUnion() as INamedTypeSymbol;

            var parameterlessCtor = caseType.Constructors.FirstOrDefault(static ctor => ctor.Parameters.Length == 0);

            if (parameterlessCtor is not null)
            {
                // Return a BoundUnionCaseExpression whose Type is the union root so that generic
                // type inference infers the union root (e.g. Err) and not the case type
                // (e.g. Err_MissingName).
                if (resolvedUnion is not null)
                    return new BoundUnionCaseExpression(resolvedUnion, caseType, parameterlessCtor, ImmutableArray<BoundExpression>.Empty);

                // Fallback (no union info available): legacy behaviour.
                return new BoundObjectCreationExpression(parameterlessCtor, ImmutableArray<BoundExpression>.Empty);
            }

            // No parameterless ctor: fall back to a BoundTypeExpression so the caller can
            // perform unit-payload sugar or invocation binding.
            if (resolvedUnion is not null)
                return new BoundUnionCaseExpression(resolvedUnion, caseType, null, ImmutableArray<BoundExpression>.Empty);
        }

        return new BoundTypeExpression(typeMember);
    }
    private static bool IsUnitType(ITypeSymbol type)
    {
        type = type.UnwrapLiteralType() ?? type;

        if (type is INamedTypeSymbol named)
        {
            if (!string.Equals(named.Name, "Unit", StringComparison.Ordinal))
                return false;

            // In Raven.Core this is typically `System.Unit`.
            var ns = named.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return ns is not null && ns.EndsWith("System", StringComparison.Ordinal);
        }

        return false;
    }
}
