using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

internal sealed class OverloadResolver
{
    public static OverloadResolutionResult ResolveOverload(
        IEnumerable<IMethodSymbol> methods,
        BoundArgument[] arguments,
        Compilation compilation,
        Binder? binder = null,
        BoundExpression? receiver = null,
        Func<IParameterSymbol, BoundFunctionExpression, bool>? canBindLambda = null,
        SyntaxNode? callSyntax = null,
        ImmutableArray<ITypeSymbol> explicitTypeArguments = default)
    {
        var logger = compilation.Options.OverloadResolutionLogger;
        List<OverloadCandidateLog>? candidateLogs = logger is null ? null : new();

        if (explicitTypeArguments.IsDefault)
            explicitTypeArguments = ImmutableArray<ITypeSymbol>.Empty;

        IMethodSymbol? bestMatch = null;
        int bestScore = int.MaxValue;
        ImmutableArray<IMethodSymbol>.Builder? ambiguous = null;
        bool bestIsExtension = false;
        TypeArgumentConstraintFailure? constraintFailure = null;
        IMethodSymbol? constraintFailureCandidate = null;
        var applicableCandidates = new List<ApplicableOverloadCandidate>();

        foreach (var candidate in DistinctCandidates(methods))
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var candidateStatus = OverloadCandidateStatus.Applicable;
            int? candidateScore = null;
            IMethodSymbol? constructed = null;
            List<OverloadArgumentComparisonLog>? candidateComparisons = logger is null ? null : new();
            var method = ApplyTypeArgumentInference(
                candidate,
                receiver,
                arguments,
                compilation,
                binder,
                explicitTypeArguments: !explicitTypeArguments.IsDefaultOrEmpty
                    ? explicitTypeArguments
                    : GetExplicitTypeArguments(callSyntax, compilation),
                constraintFailure: out var candidateConstraintFailure,
                retainConstraintFailureCandidate: true);
            if (method is null)
            {
                candidateStatus = OverloadCandidateStatus.TypeInferenceFailed;
                constraintFailure ??= candidateConstraintFailure;
                RecordCandidate(candidate, constructed, candidateStatus, candidateScore, isExtension: false, candidateComparisons);
                continue;
            }

            constructed = method;
            if (candidateConstraintFailure is not null)
            {
                constraintFailure ??= candidateConstraintFailure;
                constraintFailureCandidate ??= method;
                candidateStatus = OverloadCandidateStatus.TypeInferenceFailed;
                RecordCandidate(candidate, constructed, candidateStatus, candidateScore, isExtension: false, candidateComparisons);
                continue;
            }

            var parameters = method.Parameters;
            var treatAsExtension = method.IsExtensionMethod && receiver is not null;
            var providedCount = arguments.Length + (treatAsExtension ? 1 : 0);

            if (!HasSufficientArguments(parameters, providedCount))
            {
                candidateStatus = OverloadCandidateStatus.InsufficientArguments;
                RecordCandidate(candidate, constructed, candidateStatus, candidateScore, treatAsExtension, candidateComparisons);
                continue;
            }

            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (!TryMatch(method, arguments, receiver, treatAsExtension, compilation, binder, canBindLambda, candidateComparisons, out var score))
            {
                candidateStatus = OverloadCandidateStatus.ArgumentMismatch;
                RecordCandidate(candidate, constructed, candidateStatus, candidateScore, treatAsExtension, candidateComparisons);
                continue;
            }

            candidateScore = score;
            RecordCandidate(candidate, constructed, candidateStatus, candidateScore, treatAsExtension, candidateComparisons);
            applicableCandidates.Add(new ApplicableOverloadCandidate(method, score, treatAsExtension));
        }

        foreach (var applicableCandidate in PruneApplicableCandidatesByPriority(applicableCandidates, binder))
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var method = applicableCandidate.Method;
            var score = applicableCandidate.Score;
            var treatAsExtension = applicableCandidate.IsExtension;

            if (bestMatch is not null &&
                bestIsExtension == treatAsExtension &&
                GetOverloadPriorityGroup(method, treatAsExtension) == GetOverloadPriorityGroup(bestMatch, bestIsExtension))
            {
                var candidatePriority = GetOverloadResolutionPriority(method);
                var bestPriority = GetOverloadResolutionPriority(bestMatch);

                if (candidatePriority > bestPriority)
                {
                    bestMatch = method;
                    bestScore = score;
                    ambiguous = null;
                    bestIsExtension = treatAsExtension;
                    continue;
                }

                if (candidatePriority < bestPriority)
                    continue;
            }

            if (score < bestScore)
            {
                bestMatch = method;
                bestScore = score;
                ambiguous = null;
                bestIsExtension = treatAsExtension;
                continue;
            }

            if (score > bestScore || bestMatch is null)
                continue;

            if (bestIsExtension != treatAsExtension)
            {
                if (!treatAsExtension)
                {
                    bestMatch = method;
                    bestIsExtension = false;
                    ambiguous = null;
                }
                continue;
            }

            if (IsMoreSpecific(method, bestMatch, arguments, receiver, compilation, binder))
            {
                bestMatch = method;
                ambiguous = null;
                bestIsExtension = treatAsExtension;
                continue;
            }

            if (IsMoreSpecific(bestMatch, method, arguments, receiver, compilation, binder))
                continue;

            if (SymbolEqualityComparer.Default.Equals(bestMatch, method))
                continue;

            ambiguous ??= ImmutableArray.CreateBuilder<IMethodSymbol>();
            AddCandidateIfMissing(ambiguous, bestMatch);
            AddCandidateIfMissing(ambiguous, method);
        }

        var ambiguousCandidates = ambiguous?.ToImmutable() ?? ImmutableArray<IMethodSymbol>.Empty;

        if (logger is not null && candidateLogs is not null)
        {
            var resolvedCandidates = MarkCandidates(candidateLogs, bestMatch, ambiguousCandidates);
            var argumentLogs = CreateArgumentLogs(arguments);

            logger.Log(new OverloadResolutionLogEntry(
                callSyntax,
                receiver?.Type,
                argumentLogs,
                resolvedCandidates,
                bestMatch,
                ambiguousCandidates));
        }

        if (ambiguous is { Count: > 0 })
            return OverloadResolutionResult.Ambiguous(ambiguous.ToImmutable());

        return new OverloadResolutionResult(bestMatch, constraintFailure, constraintFailureCandidate);

        void RecordCandidate(
            IMethodSymbol original,
            IMethodSymbol? constructedMethod,
            OverloadCandidateStatus status,
            int? score,
            bool isExtension,
            List<OverloadArgumentComparisonLog>? comparisons)
        {
            if (candidateLogs is null)
                return;

            candidateLogs.Add(new OverloadCandidateLog(
                original,
                constructedMethod,
                status,
                score,
                isExtension,
                IsBest: false,
                IsAmbiguous: false,
                comparisons is null
                    ? ImmutableArray<OverloadArgumentComparisonLog>.Empty
                    : comparisons.ToImmutableArray()));
        }
    }

    private static IReadOnlyList<ApplicableOverloadCandidate> PruneApplicableCandidatesByPriority(
        IReadOnlyList<ApplicableOverloadCandidate> candidates,
        Binder? binder)
    {
        if (candidates.Count <= 1)
            return candidates;

        var maxPriorityByGroup = new Dictionary<OverloadPriorityGroupKey, int>();

        foreach (var candidate in candidates)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var group = GetOverloadPriorityGroup(candidate.Method, candidate.IsExtension);
            var priority = GetOverloadResolutionPriority(candidate.Method);

            if (!maxPriorityByGroup.TryGetValue(group, out var current) || priority > current)
                maxPriorityByGroup[group] = priority;
        }

        var pruned = new List<ApplicableOverloadCandidate>(candidates.Count);

        foreach (var candidate in candidates)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var group = GetOverloadPriorityGroup(candidate.Method, candidate.IsExtension);
            var priority = GetOverloadResolutionPriority(candidate.Method);

            if (maxPriorityByGroup.TryGetValue(group, out var maxPriority) && priority == maxPriority)
                pruned.Add(candidate);
        }

        return pruned;
    }

    private static void ThrowIfDiagnosticBindingCancellationRequested(Binder? binder)
        => binder?.SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();

    private static OverloadPriorityGroupKey GetOverloadPriorityGroup(IMethodSymbol method, bool isExtension)
    {
        if (isExtension)
            return new OverloadPriorityGroupKey(IsExtension: true, DeclaringType: null);

        var definition = method.OriginalDefinition ?? method;
        return new OverloadPriorityGroupKey(
            IsExtension: false,
            DeclaringType: GetDeclaringTypeIdentity(definition.ContainingType));
    }

    private static int GetOverloadResolutionPriority(IMethodSymbol method)
    {
        method = GetLeastDerivedPriorityMethod(method);

        if (TryGetSourceDeclaredOverloadResolutionPriority(method, out var sourcePriority))
            return sourcePriority;

        foreach (var attribute in method.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
                continue;

            var attributeNamespace = attributeClass.ContainingNamespace?.ToDisplayString();
            var attributeName = attributeClass.Name;

            if (!(string.Equals(attributeNamespace, "System.Runtime.CompilerServices", StringComparison.Ordinal) ||
                  string.Equals(attributeNamespace, "CompilerServices", StringComparison.Ordinal) ||
                  (attributeNamespace?.EndsWith(".CompilerServices", StringComparison.Ordinal) ?? false)) ||
                !string.Equals(attributeName, "OverloadResolutionPriorityAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int priority)
            {
                return priority;
            }
        }

        if (method is PEMethodSymbol peMethod &&
            TryGetOverloadResolutionPriority(peMethod.ReflectionMethodBase, out var metadataPriority))
        {
            return metadataPriority;
        }

        if (!ReferenceEquals(method.OriginalDefinition, method) &&
            method.OriginalDefinition is PEMethodSymbol peDefinition &&
            TryGetOverloadResolutionPriority(peDefinition.ReflectionMethodBase, out metadataPriority))
        {
            return metadataPriority;
        }

        return 0;

        static bool TryGetOverloadResolutionPriority(System.Reflection.MethodBase methodBase, out int priority)
        {
            priority = 0;

            try
            {
                if (methodBase is System.Reflection.MethodInfo methodInfo)
                    methodBase = methodInfo.GetBaseDefinition();

                foreach (var attribute in methodBase.GetCustomAttributesData())
                {
                    if (!string.Equals(attribute.AttributeType.FullName,
                            "System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (attribute.ConstructorArguments.Count == 1 &&
                        attribute.ConstructorArguments[0].Value is int constructorPriority)
                    {
                        priority = constructorPriority;
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }

    private static bool TryGetSourceDeclaredOverloadResolutionPriority(IMethodSymbol method, out int priority)
    {
        priority = 0;

        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syntax is null)
            return false;

        var attributes = syntax switch
        {
            MethodDeclarationSyntax methodDeclaration => methodDeclaration.AttributeLists.SelectMany(static list => list.Attributes),
            OperatorDeclarationSyntax operatorDeclaration => operatorDeclaration.AttributeLists.SelectMany(static list => list.Attributes),
            ConversionOperatorDeclarationSyntax conversionDeclaration => conversionDeclaration.AttributeLists.SelectMany(static list => list.Attributes),
            _ => Enumerable.Empty<AttributeSyntax>()
        };

        foreach (var attribute in attributes)
        {
            var name = attribute.Name.ToString();
            var simpleName = name.Split('.').LastOrDefault() ?? name;

            if (!string.Equals(simpleName, "OverloadResolutionPriority", StringComparison.Ordinal) &&
                !string.Equals(simpleName, "OverloadResolutionPriorityAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ArgumentList?.Arguments.Count != 1)
                return false;

            var argumentText = attribute.ArgumentList.Arguments[0].Expression.ToString();
            return int.TryParse(argumentText, out priority);
        }

        return false;
    }

    private static IMethodSymbol GetLeastDerivedPriorityMethod(IMethodSymbol method)
    {
        method = method.OriginalDefinition ?? method;

        while (true)
        {
            switch (method)
            {
                case SourceMethodSymbol { OverriddenMethod: { } overriddenMethod }:
                    method = overriddenMethod.OriginalDefinition ?? overriddenMethod;
                    continue;

                case PEMethodSymbol { ReflectionMethodBase: System.Reflection.MethodInfo methodInfo }:
                    return method;

                default:
                    return method;
            }
        }
    }

    private readonly record struct ApplicableOverloadCandidate(
        IMethodSymbol Method,
        int Score,
        bool IsExtension);

    private readonly record struct OverloadPriorityGroupKey(
        bool IsExtension,
        string? DeclaringType);

    private static string? GetDeclaringTypeIdentity(INamedTypeSymbol? containingType)
    {
        if (containingType is null)
            return null;

        var assemblyName = containingType.ContainingAssembly?.Name ?? string.Empty;
        var metadataName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{assemblyName}|{metadataName}";
    }

    private static ImmutableArray<ITypeSymbol> GetExplicitTypeArguments(SyntaxNode? callSyntax, Compilation compilation)
    {
        // Fallback: Only used if the binder cannot provide explicit type arguments.
        // We only support explicit method type arguments from invocation receivers like:
        //   items.CountItems<double>(2)
        // where the member name is a GenericNameSyntax.
        if (callSyntax is not InvocationExpressionSyntax inv)
            return ImmutableArray<ITypeSymbol>.Empty;

        // InvocationExpressionSyntax.Expression can be IdentifierNameSyntax, MemberAccessExpressionSyntax, etc.
        // We only care about the right-most name being a GenericNameSyntax.
        GenericNameSyntax? genericName = null;

        if (inv.Expression is MemberAccessExpressionSyntax ma)
            genericName = ma.Name as GenericNameSyntax;
        else if (inv.Expression is IdentifierNameSyntax)
            genericName = null;
        else if (inv.Expression is GenericNameSyntax g)
            genericName = g;

        if (genericName is null)
            return ImmutableArray<ITypeSymbol>.Empty;

        // Bind the explicit type arguments using a binder-independent binder helper.
        // We can’t bind types here without a binder, so we only support predefined/identifier types
        // through the compilation’s type resolver via metadata/predefined lookup.
        // If a type arg can’t be resolved, we keep it as ErrorType to let resolution fail gracefully.
        var args = genericName.TypeArgumentList.Arguments;
        if (args.Count == 0)
            return ImmutableArray<ITypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            var ts = args[i];
            var resolved = compilation.TryBindTypeSyntaxWithoutBinder(ts.Type);
            builder.Add(resolved ?? compilation.ErrorTypeSymbol);
        }

        return builder.ToImmutable();
    }

    internal static IMethodSymbol? ApplyTypeArgumentInference(
        IMethodSymbol method,
        BoundExpression? receiver,
        BoundArgument[] arguments,
        Compilation compilation,
        Binder? binder = null,
        ImmutableArray<ITypeSymbol> explicitTypeArguments = default)
        => ApplyTypeArgumentInference(method, receiver, arguments, compilation, binder, explicitTypeArguments, out _);

    internal static IMethodSymbol? ApplyTypeArgumentInference(
        IMethodSymbol method,
        BoundExpression? receiver,
        BoundArgument[] arguments,
        Compilation compilation,
        Binder? binder,
        ImmutableArray<ITypeSymbol> explicitTypeArguments,
        out TypeArgumentConstraintFailure? constraintFailure,
        bool retainConstraintFailureCandidate = false)
    {
        constraintFailure = null;
        var treatAsReceiverExtension = method.ExtensionMemberKind != ExtensionMemberKind.None && receiver is not null;

        if (treatAsReceiverExtension &&
            method.OriginalDefinition is IMethodSymbol originalDefinition &&
            !ReferenceEquals(originalDefinition, method))
        {
            method = originalDefinition;
        }

        if (receiver?.Type is not null &&
            !treatAsReceiverExtension &&
            TryConstructMethodOnReceiver(method, receiver.Type, compilation, out var receiverAdjustedMethod))
        {
            method = receiverAdjustedMethod;
        }

        // Extension lookup may return a method already adjusted for the receiver
        // (e.g. from a constructed extension container type), but the method can
        // still have method-level type parameters (like <E>) that must be inferred.
        if (explicitTypeArguments.IsDefault)
            explicitTypeArguments = ImmutableArray<ITypeSymbol>.Empty;

        // If the method isn’t generic, nothing to do.
        if (!method.IsGenericMethod || method.TypeParameters.IsDefaultOrEmpty || method.TypeParameters.Length == 0)
            return method;

        // Already-constructed generic methods (common for extension members on generic extension containers)
        // should not go through method-level inference again.
        if (!method.TypeArguments.IsDefaultOrEmpty &&
            method.TypeArguments.Length == method.TypeParameters.Length &&
            method.TypeArguments.All(static t => t is not ITypeParameterSymbol))
        {
            return method;
        }

        var treatAsExtension = treatAsReceiverExtension;

        // If explicit type args were provided, allow partial lists (Raven feature):
        // - if count == arity: construct directly
        // - if count < arity: right-align them to the last type parameters, infer the rest
        // - if count > arity: impossible
        if (!explicitTypeArguments.IsDefaultOrEmpty)
        {
            var constructed = TryConstructMethodWithExplicitAndInference(
                method,
                explicitTypeArguments,
                receiver,
                arguments,
                treatAsExtension,
                compilation,
                binder,
                out constraintFailure,
                retainConstraintFailureCandidate);

            return constructed;
        }

        // Otherwise infer all method type parameters.
        return TryConstructMethodWithInference(
            method,
            receiver,
            arguments,
            treatAsExtension,
            compilation,
            binder,
            out constraintFailure,
            retainConstraintFailureCandidate);
    }

    private static bool TryConstructMethodOnReceiver(
        IMethodSymbol method,
        ITypeSymbol receiverType,
        Compilation compilation,
        out IMethodSymbol constructed)
    {
        constructed = method;

        if (receiverType.TypeKind == TypeKind.Error)
            return false;

        var methodDefinition = method.OriginalDefinition ?? method;
        if (methodDefinition.ContainingType is not INamedTypeSymbol containingType ||
            !containingType.IsGenericType ||
            containingType.TypeParameters.IsDefaultOrEmpty ||
            containingType.TypeParameters.Length == 0)
        {
            return false;
        }

        INamedTypeSymbol constructedContaining;
        if (receiverType is INamedTypeSymbol receiverNamed &&
            SymbolEqualityComparer.Default.Equals(
                TypeSubstitution.GetDefinitionForSubstitution(receiverNamed),
                TypeSubstitution.GetDefinitionForSubstitution(containingType)))
        {
            constructedContaining = receiverNamed;
        }
        else
        {
            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!TryInferFromTypes(compilation, containingType, receiverType, substitutions, inferenceMethod: null))
                return false;

            var typeArguments = new ITypeSymbol[containingType.TypeParameters.Length];
            for (int i = 0; i < containingType.TypeParameters.Length; i++)
            {
                var typeParameter = containingType.TypeParameters[i];
                if (!substitutions.TryGetValue(typeParameter, out var typeArgument))
                    return false;

                typeArguments[i] = NormalizeType(typeArgument);
            }

            if (containingType.Construct(typeArguments) is not INamedTypeSymbol inferredContaining)
                return false;

            constructedContaining = inferredContaining;
        }

        foreach (var candidate in constructedContaining.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            var candidateOriginal = candidate.OriginalDefinition ?? candidate;
            if (SymbolEqualityComparer.Default.Equals(candidateOriginal, methodDefinition))
            {
                constructed = candidate;
                return true;
            }
        }

        return false;
    }

    private static IMethodSymbol? TryConstructMethodWithExplicitAndInference(
    IMethodSymbol method,
    ImmutableArray<ITypeSymbol> explicitTypeArguments,
    BoundExpression? receiver,
    BoundArgument[] arguments,
    bool treatAsExtension,
    Compilation compilation,
    Binder? binder,
    out TypeArgumentConstraintFailure? constraintFailure,
    bool retainConstraintFailureCandidate)
    {
        constraintFailure = null;

        if (explicitTypeArguments.IsDefaultOrEmpty)
            return TryConstructMethodWithInference(
                method,
                receiver,
                arguments,
                treatAsExtension,
                compilation,
                binder,
                out constraintFailure,
                retainConstraintFailureCandidate) ?? method;

        var arity = method.TypeParameters.Length;
        if (explicitTypeArguments.Length > arity)
            return null;

        // When the call provides every method type argument explicitly, C# treats the method
        // as fully constructed before checking argument applicability. Do not keep inferring
        // from later arguments (especially lambdas), because metadata delegate signatures like
        // Action<T> can surface void/unit normalization differences that are not type-inference failures.
        if (explicitTypeArguments.Length == arity)
        {
            var finalExplicitArgs = new ITypeSymbol[arity];
            for (int i = 0; i < arity; i++)
            {
                ThrowIfDiagnosticBindingCancellationRequested(binder);

                var normalized = NormalizeType(explicitTypeArguments[i]);
                if (normalized.TypeKind == TypeKind.Error)
                    return null;

                finalExplicitArgs[i] = normalized;
            }

            var immutableExplicitArgs = ImmutableArray.CreateRange(finalExplicitArgs);
            if (!SatisfiesMethodConstraints(method, immutableExplicitArgs, binder, out constraintFailure))
                return retainConstraintFailureCandidate ? method.Construct(finalExplicitArgs) : null;

            return method.Construct(finalExplicitArgs);
        }

        // Right-align explicit args to the last type parameters.
        var fixedArgs = new ITypeSymbol?[arity];
        var offset = arity - explicitTypeArguments.Length;
        for (int i = 0; i < explicitTypeArguments.Length; i++)
        {
            fixedArgs[offset + i] = NormalizeType(explicitTypeArguments[i]);
        }

        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);

        // Infer every position independently. Explicit arguments win when the final
        // constructed signature is produced, and ordinary applicability then checks
        // arguments against those fixed types. Keeping fixed arguments out of the
        // inference map lets the remaining type parameters collect and widen their
        // bounds without accidentally rewriting an explicit argument.

        var parameters = method.Parameters;
        var parameterIndex = 0;

        if (treatAsExtension)
        {
            if (receiver?.Type is null)
                return null;

            if (!TryInferFromTypes(compilation, parameters[parameterIndex].Type, receiver.Type, substitutions, method))
                return null;

            parameterIndex++;
        }

        if (!TryMapArguments(parameters, arguments, treatAsExtension, out var mappedArguments, out var paramsArguments, out var paramsParameterIndex))
            return null;

        for (; parameterIndex < parameters.Length; parameterIndex++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (parameterIndex == paramsParameterIndex && mappedArguments[parameterIndex] is null)
            {
                if (!TryInferFromParamsArguments(compilation, parameters[parameterIndex], paramsArguments, substitutions, method))
                    return null;

                continue;
            }

            var mapped = mappedArguments[parameterIndex];
            if (mapped is null)
                continue;

            var expression = mapped.Value.Expression;

            if (expression is BoundFunctionExpression lambda)
            {
                if (!TryInferFromLambda(compilation, parameters[parameterIndex].Type, lambda, substitutions, method))
                    return null;
                continue;
            }

            if (expression is BoundMethodGroupExpression methodGroup)
            {
                if (!TryInferFromMethodGroup(
                        compilation,
                        parameters[parameterIndex].Type,
                        methodGroup,
                        substitutions,
                        method,
                        binder,
                        out constraintFailure))
                    return null;
                continue;
            }

            var argumentType = expression.Type;
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                continue;

            var parameterType = parameters[parameterIndex].Type;
            if (parameters[parameterIndex].RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            {
                parameterType = GetByRefInferenceElementType(parameterType);
                argumentType = GetByRefInferenceElementType(argumentType);
            }

            if (!TryInferFromTypes(compilation, parameterType, argumentType, substitutions, method))
                return null;
        }

        // Produce final type arguments array: fixed args win; otherwise take inferred.
        var finalArgs = new ITypeSymbol[arity];
        for (int i = 0; i < arity; i++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var tp = method.TypeParameters[i];

            if (fixedArgs[i] is { } fixedType)
            {
                finalArgs[i] = NormalizeType(fixedType);
                continue;
            }

            if (!substitutions.TryGetValue(tp, out var inferred2))
                return null;

            finalArgs[i] = NormalizeType(inferred2);
        }

        var immutableArguments = ImmutableArray.CreateRange(finalArgs);
        if (!SatisfiesMethodConstraints(method, immutableArguments, binder, out constraintFailure))
            return retainConstraintFailureCandidate ? method.Construct(finalArgs) : null;

        return method.Construct(finalArgs);
    }

    private static IMethodSymbol? TryConstructMethodWithInference(
        IMethodSymbol method,
        BoundExpression? receiver,
        BoundArgument[] arguments,
        bool treatAsExtension,
        Compilation compilation,
        Binder? binder,
        out TypeArgumentConstraintFailure? constraintFailure,
        bool retainConstraintFailureCandidate)
    {
        constraintFailure = null;

        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        var parameters = method.Parameters;
        var parameterIndex = 0;

        if (treatAsExtension)
        {
            if (receiver?.Type is null)
                return null;

            if (!TryInferFromTypes(compilation, parameters[parameterIndex].Type, receiver.Type, substitutions, method))
                return null;

            parameterIndex++;
        }

        if (!TryMapArguments(parameters, arguments, treatAsExtension, out var mappedArguments, out var paramsArguments, out var paramsParameterIndex))
            return null;

        for (; parameterIndex < parameters.Length; parameterIndex++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (parameterIndex == paramsParameterIndex && mappedArguments[parameterIndex] is null)
            {
                if (!TryInferFromParamsArguments(compilation, parameters[parameterIndex], paramsArguments, substitutions, method))
                    return null;

                continue;
            }

            var mapped = mappedArguments[parameterIndex];
            if (mapped is null)
                continue;

            var expression = mapped.Value.Expression;

            if (expression is BoundFunctionExpression lambda)
            {
                if (!TryInferFromLambda(compilation, parameters[parameterIndex].Type, lambda, substitutions, method))
                    return null;

                continue;
            }

            if (expression is BoundMethodGroupExpression methodGroup)
            {
                if (!TryInferFromMethodGroup(
                        compilation,
                        parameters[parameterIndex].Type,
                        methodGroup,
                        substitutions,
                        method,
                        binder,
                        out constraintFailure))
                    return null;

                continue;
            }

            var argumentType = expression.Type;
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                continue;

            if (!TryInferFromTypes(compilation, parameters[parameterIndex].Type, argumentType, substitutions, method))
                return null;
        }

        var inferredArguments = new ITypeSymbol[method.TypeParameters.Length];
        for (int i = 0; i < method.TypeParameters.Length; i++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var typeParameter = method.TypeParameters[i];
            if (!TryGetInferredTypeArgument(typeParameter, substitutions, out var inferred))
                return null;

            inferredArguments[i] = NormalizeType(inferred);
        }

        var immutableArguments = ImmutableArray.CreateRange(inferredArguments);

        if (!SatisfiesMethodConstraints(method, immutableArguments, binder, out constraintFailure))
            return retainConstraintFailureCandidate ? method.Construct(inferredArguments) : null;

        return method.Construct(inferredArguments);
    }

    private static bool TryInferFromParamsArguments(
        Compilation compilation,
        IParameterSymbol paramsParameter,
        IReadOnlyList<BoundArgument> paramsArguments,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        IMethodSymbol inferenceMethod)
    {
        if (paramsArguments.Count == 0)
            return true;

        if (paramsArguments.Count == 1 &&
            !paramsArguments[0].IsSpread &&
            paramsArguments[0].Type is ITypeSymbol singleParamsType)
        {
            // Prefer normal-form varargs inference when a single collection argument is supplied.
            // Example: Collect<T>(items: T ...) with int[] should infer T=int (not T=int[]).
            if (TryGetVarParamsElementType(paramsParameter.Type, out var paramsElementType) &&
                TryGetVarParamsElementType(singleParamsType, out var singleParamsElementType))
            {
                if (!TryInferFromTypes(compilation, paramsElementType, singleParamsElementType, substitutions, inferenceMethod))
                    return false;

                return true;
            }

            if (TryInferFromTypes(compilation, paramsParameter.Type, singleParamsType, substitutions, inferenceMethod))
                return true;
        }

        if (!TryGetVarParamsElementType(paramsParameter.Type, out var elementType))
            return true;

        foreach (var paramsArgument in paramsArguments)
        {
            var argumentType = paramsArgument.Type;
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                continue;

            if (paramsArgument.IsSpread)
            {
                if (!TryGetVarParamsElementType(argumentType, out var spreadElementType))
                    continue;

                if (!TryInferFromTypes(compilation, elementType, spreadElementType, substitutions, inferenceMethod))
                    return false;

                continue;
            }

            if (!TryInferFromTypes(compilation, elementType, argumentType, substitutions, inferenceMethod))
                return false;
        }

        return true;
    }

    private static bool TryInferFromLambda(
        Compilation compilation,
        ITypeSymbol parameterType,
        BoundFunctionExpression lambda,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        IMethodSymbol? inferenceMethod)
    {
        INamedTypeSymbol? delegateType = null;
        if (parameterType is INamedTypeSymbol named)
        {
            if (named.TypeKind == TypeKind.Delegate)
                delegateType = named;
            else if (TryGetExpressionTreeDelegateType(named, out var innerDelegate))
                delegateType = innerDelegate;
        }

        if (delegateType is null)
            return true;

        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
            return true;

        var lambdaParameters = lambda.Parameters.ToImmutableArray();
        if (invoke.Parameters.Length != lambdaParameters.Length)
            return false;

        for (int i = 0; i < invoke.Parameters.Length; i++)
        {
            var parameter = invoke.Parameters[i];
            var lambdaParameter = lambdaParameters[i];
            var lambdaParameterType = lambdaParameter.Type;

            if (lambdaParameterType is null || lambdaParameterType.TypeKind == TypeKind.Error)
                continue;

            if (lambdaParameterType is ITypeParameterSymbol)
                continue;

            if (!TryInferFromTypes(compilation, parameter.Type, lambdaParameterType, substitutions, inferenceMethod))
                return false;
        }

        var lambdaReturnType = lambda.ReturnType;
        ITypeSymbol? collectedAsyncReturn = null;

        static bool ContainsTypeParameter(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol:
                    return true;
                case IArrayTypeSymbol array:
                    return ContainsTypeParameter(array.ElementType);
                case RefTypeSymbol refType:
                    return ContainsTypeParameter(refType.ElementType);
                case NullableTypeSymbol nullable:
                    return ContainsTypeParameter(nullable.UnderlyingType);
                case ITupleTypeSymbol tuple:
                    return tuple.TupleElements.Any(e => ContainsTypeParameter(e.Type));
                case INamedTypeSymbol named:
                    {
                        var typeArguments = TypeSubstitution.GetShallowTypeArguments(named);
                        return !typeArguments.IsDefaultOrEmpty && typeArguments.Any(ContainsTypeParameter);
                    }
                default:
                    return false;
            }
        }

        if (lambda.Symbol is ILambdaSymbol { IsAsync: true })
        {
            collectedAsyncReturn = ReturnTypeCollector.InferAsync(compilation, lambda.Body);

            // Prefer the already computed async return over re-inferring from the raw body
            // so we keep task-shaped inference (including nullable/Task<Unit> normalization)
            // when replaying the lambda for generic inference.
            if (lambdaReturnType is { TypeKind: not TypeKind.Error })
            {
                lambdaReturnType = AsyncReturnTypeUtilities.InferAsyncReturnType(compilation, lambdaReturnType);
            }
            else
            {
                lambdaReturnType = AsyncReturnTypeUtilities.InferAsyncReturnType(compilation, lambda.Body);
            }

            // If the async return still contains type parameters (e.g., Task<T>), fall back to
            // inferring the async return directly from the body so overload resolution can learn
            // about concrete return values such as `int` from `return 42` in a block-bodied async
            // lambda.
            if (lambdaReturnType is { TypeKind: not TypeKind.Error } withTypeParams && ContainsTypeParameter(withTypeParams))
            {
                var inferredFromBody = collectedAsyncReturn ?? AsyncReturnTypeUtilities.InferAsyncReturnType(compilation, lambda.Body);
                if (inferredFromBody is { TypeKind: not TypeKind.Error })
                    lambdaReturnType = inferredFromBody;
            }
        }

        if ((lambdaReturnType is null || lambdaReturnType.TypeKind == TypeKind.Error) &&
            lambda.Body.Type is { TypeKind: not TypeKind.Error } bodyType)
        {
            lambdaReturnType = bodyType;
        }
        else if (lambdaReturnType is ITypeParameterSymbol &&
                 lambda.Body.Type is { TypeKind: not TypeKind.Error } inferredBodyType)
        {
            lambdaReturnType = inferredBodyType;
        }

        if (collectedAsyncReturn is { TypeKind: not TypeKind.Error })
        {
            if (lambdaReturnType is null || lambdaReturnType.TypeKind == TypeKind.Error || ContainsTypeParameter(lambdaReturnType))
                lambdaReturnType = collectedAsyncReturn;
        }
        if (lambdaReturnType is not null && lambdaReturnType.TypeKind != TypeKind.Error)
        {
            if (lambda.Symbol is ILambdaSymbol { IsAsync: true })
            {
                var lambdaResult = AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation, lambdaReturnType)
                    ?? lambdaReturnType;
                var expectedResult = AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation, invoke.ReturnType)
                    ?? invoke.ReturnType;

                if (lambdaResult is ITypeParameterSymbol &&
                    collectedAsyncReturn is { TypeKind: not TypeKind.Error })
                {
                    var collectedAsyncResult = AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation, collectedAsyncReturn)
                        ?? collectedAsyncReturn;

                    if (collectedAsyncResult is { TypeKind: not TypeKind.Error })
                        lambdaResult = collectedAsyncResult;
                }

                if (!TryInferFromTypes(compilation, expectedResult, lambdaResult, substitutions, inferenceMethod))
                    return false;
            }
            else
            {
                if (!TryInferFromTypes(compilation, invoke.ReturnType, lambdaReturnType, substitutions, inferenceMethod))
                    return false;
            }
        }

        return true;
    }

    private static bool TryInferFromMethodGroup(
        Compilation compilation,
        ITypeSymbol parameterType,
        BoundMethodGroupExpression methodGroup,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        IMethodSymbol? inferenceMethod,
        Binder? binder,
        out TypeArgumentConstraintFailure? constraintFailure)
    {
        constraintFailure = null;

        // Method groups only participate in inference when the target parameter is a delegate type.
        if (parameterType is not INamedTypeSymbol delegateType ||
            delegateType.TypeKind != TypeKind.Delegate)
        {
            return true;
        }

        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
            return true;

        var candidates = methodGroup.Methods;
        if (candidates.IsDefaultOrEmpty)
            return false;

        foreach (var candidate in candidates)
        {
            if (candidate is null)
                continue;

            var constructedCandidate = TryConstructMethodGroupCandidate(
                candidate,
                invoke,
                compilation,
                binder,
                substitutions,
                out var candidateConstraintFailure);
            if (constructedCandidate is null)
            {
                constraintFailure ??= candidateConstraintFailure;
                continue;
            }

            if (constructedCandidate.Parameters.Length != invoke.Parameters.Length)
                continue;

            // Backtrackable inference: try candidate against a copy, then commit.
            var temp = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(substitutions, SymbolEqualityComparer.Default);
            var priorSubstitutions = substitutions.ToArray();

            var ok = true;

            for (int i = 0; i < invoke.Parameters.Length; i++)
            {
                var expectedParam = invoke.Parameters[i];
                var candidateParam = constructedCandidate.Parameters[i];

                // Ref-kind mismatch cannot be bridged by conversions.
                if (expectedParam.RefKind != candidateParam.RefKind)
                {
                    ok = false;
                    break;
                }

                if (!TryInferFromTypes(compilation, expectedParam.Type, candidateParam.Type, temp, inferenceMethod))
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
                continue;

            // Unify return types as well so we can infer TResult from a method group like `Compute`.
            if (!TryInferFromTypes(compilation, invoke.ReturnType, constructedCandidate.ReturnType, temp, inferenceMethod))
                continue;

            // A method group adapts to the delegate signature selected by the other
            // arguments. It may fill still-unbound type parameters, but it must not
            // widen an inference already established by an ordinary argument. The
            // final applicability pass validates the adapted method group against
            // the resulting delegate type.
            foreach (var (typeParameter, inferredType) in priorSubstitutions)
                temp[typeParameter] = inferredType;

            // Commit inferred substitutions.
            substitutions.Clear();
            foreach (var kv in temp)
                substitutions[kv.Key] = kv.Value;

            return true;
        }

        // No candidate could be used to infer the delegate conversion.
        return false;
    }

    internal static IMethodSymbol? TryConstructMethodGroupCandidate(
        IMethodSymbol candidate,
        IMethodSymbol delegateInvoke,
        Compilation compilation,
        Binder? binder,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>? knownTargetSubstitutions = null)
        => TryConstructMethodGroupCandidate(
            candidate,
            delegateInvoke,
            compilation,
            binder,
            knownTargetSubstitutions,
            out _);

    private static IMethodSymbol? TryConstructMethodGroupCandidate(
        IMethodSymbol candidate,
        IMethodSymbol delegateInvoke,
        Compilation compilation,
        Binder? binder,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol>? knownTargetSubstitutions,
        out TypeArgumentConstraintFailure? constraintFailure)
    {
        constraintFailure = null;

        if (!candidate.IsGenericMethod || candidate.TypeParameters.IsDefaultOrEmpty)
            return candidate;

        if (candidate.Parameters.Length != delegateInvoke.Parameters.Length)
            return null;

        var arguments = new BoundArgument[delegateInvoke.Parameters.Length];
        for (var i = 0; i < delegateInvoke.Parameters.Length; i++)
        {
            var delegateParameter = delegateInvoke.Parameters[i];
            var parameterType = knownTargetSubstitutions is null
                ? delegateParameter.Type
                : SubstituteType(delegateParameter.Type, knownTargetSubstitutions);
            arguments[i] = new BoundArgument(
                new BoundDefaultValueExpression(parameterType),
                delegateParameter.RefKind,
                name: null);
        }

        return ApplyTypeArgumentInference(
            candidate,
            receiver: null,
            arguments,
            compilation,
            binder,
            explicitTypeArguments: default,
            out constraintFailure);
    }

    private static ImmutableArray<OverloadCandidateLog> MarkCandidates(
        List<OverloadCandidateLog> candidates,
        IMethodSymbol? best,
        ImmutableArray<IMethodSymbol> ambiguous)
    {
        if (candidates.Count == 0)
            return ImmutableArray<OverloadCandidateLog>.Empty;

        var builder = ImmutableArray.CreateBuilder<OverloadCandidateLog>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var constructed = candidate.ConstructedMethod ?? candidate.OriginalMethod;
            var isBest = best is not null && SymbolEqualityComparer.Default.Equals(constructed, best);
            var isAmbiguous = !ambiguous.IsDefaultOrEmpty && ambiguous.Any(a => SymbolEqualityComparer.Default.Equals(a, constructed));

            builder.Add(candidate with { IsBest = isBest, IsAmbiguous = isAmbiguous });
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<OverloadArgumentLog> CreateArgumentLogs(IReadOnlyList<BoundArgument> arguments)
    {
        if (arguments.Count == 0)
            return ImmutableArray<OverloadArgumentLog>.Empty;

        var builder = ImmutableArray.CreateBuilder<OverloadArgumentLog>(arguments.Count);

        foreach (var argument in arguments)
        {
            builder.Add(new OverloadArgumentLog(
                argument.Name,
                argument.RefKind,
                argument.Type,
                argument.Syntax));
        }

        return builder.ToImmutable();
    }

    private static bool TryInferFromTypes(
        Compilation compilation,
        ITypeSymbol parameterType,
        ITypeSymbol argumentType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        IMethodSymbol? inferenceMethod)
    {
        parameterType = NormalizeType(parameterType);
        argumentType = NormalizeType(argumentType);

        if (argumentType.TypeKind == TypeKind.Error)
            return false;

        if (TryGetNullableInferenceUnderlyingType(parameterType, out var nullableParameterType))
        {
            var nullableArgumentType = TryGetNullableInferenceUnderlyingType(argumentType, out var underlyingArgumentType)
                ? underlyingArgumentType
                : argumentType;

            return TryInferFromTypes(
                compilation,
                nullableParameterType,
                nullableArgumentType,
                substitutions,
                inferenceMethod);
        }

        if (TryGetTupleInferenceElementTypes(parameterType, out var parameterTupleElements) &&
            TryGetTupleInferenceElementTypes(argumentType, out var argumentTupleElements))
        {
            if (parameterTupleElements.Length != argumentTupleElements.Length)
                return false;

            for (var i = 0; i < parameterTupleElements.Length; i++)
            {
                if (!TryInferFromTypes(
                        compilation,
                        parameterTupleElements[i],
                        argumentTupleElements[i],
                        substitutions,
                        inferenceMethod))
                {
                    return false;
                }
            }

            return true;
        }

        if (parameterType is ITypeParameterSymbol typeParameter)
        {
            typeParameter = GetCanonicalTypeParameter(typeParameter, inferenceMethod);
            argumentType = NormalizeType(argumentType);

            // A null literal does not provide a concrete type for type-parameter inference.
            // Keep existing substitutions (if any), but do not infer a new substitution from null.
            if (argumentType.TypeKind == TypeKind.Null)
                return true;

            if (substitutions.TryGetValue(typeParameter, out var existing))
            {
                existing = NormalizeType(existing);

                if (SymbolEqualityComparer.Default.Equals(existing, argumentType))
                    return true;

                // Keep the narrower inferred type when the new argument converts to it.
                if (compilation.ClassifyConversion(argumentType, existing).IsImplicit)
                    return true;

                // Widen an inferred bound when the existing bound converts to the new
                // argument type. This makes inference independent of argument order for
                // a base/derived pair such as Choose(derived, baseValue).
                if (compilation.ClassifyConversion(existing, argumentType).IsImplicit)
                {
                    substitutions[typeParameter] = argumentType;
                    return true;
                }

                return false;
            }

            substitutions[typeParameter] = argumentType;
            return true;
        }

        if (parameterType is INamedTypeSymbol paramNamed)
        {
            if (TryGetSpanTypeInfo(paramNamed, out var parameterElementType, out var parameterIsReadOnly) &&
                TryGetSpanInferenceElementType(argumentType, parameterIsReadOnly, out var argumentElementType))
            {
                return TryInferFromTypes(
                    compilation,
                    parameterElementType,
                    argumentElementType,
                    substitutions,
                    inferenceMethod);
            }

            if (argumentType is INamedTypeSymbol argNamed)
            {
                if (TryUnifyNamedType(paramNamed, argNamed))
                    return true;

                foreach (var iface in argNamed.AllInterfaces)
                {
                    if (TryUnifyNamedType(paramNamed, iface))
                        return true;
                }

                for (var baseType = argNamed.BaseType; baseType is not null; baseType = baseType.BaseType)
                {
                    if (TryUnifyNamedType(paramNamed, baseType))
                        return true;
                }
            }
            else if (argumentType is IArrayTypeSymbol arrayArgument)
            {
                var parameterArguments = TypeSubstitution.GetShallowTypeArguments(paramNamed);
                if ((paramNamed.ConstructedFrom ?? paramNamed).SpecialType is SpecialType.System_Collections_Generic_IEnumerable_T or
                    SpecialType.System_Collections_Generic_ICollection_T or
                    SpecialType.System_Collections_Generic_IList_T ||
                    IsGenericCollectionInterface(paramNamed, "IReadOnlyCollection") ||
                    IsGenericCollectionInterface(paramNamed, "IReadOnlyList"))
                {
                    return !parameterArguments.IsDefaultOrEmpty &&
                        TryInferFromTypes(compilation, parameterArguments[0], arrayArgument.ElementType, substitutions, inferenceMethod);
                }

                foreach (var iface in arrayArgument.AllInterfaces)
                {
                    if (TryUnifyNamedType(paramNamed, iface))
                        return true;
                }
            }
        }

        if (parameterType is IArrayTypeSymbol paramArray && argumentType is IArrayTypeSymbol argArray)
            return TryInferFromTypes(compilation, paramArray.ElementType, argArray.ElementType, substitutions, inferenceMethod);

        return true;

        bool TryGetSpanInferenceElementType(
            ITypeSymbol type,
            bool allowReadOnlySpan,
            out ITypeSymbol elementType)
        {
            if (type is IArrayTypeSymbol { Rank: 1 } array)
            {
                elementType = array.ElementType;
                return true;
            }

            if (type.SpecialType == SpecialType.System_String && allowReadOnlySpan)
            {
                elementType = compilation.GetSpecialType(SpecialType.System_Char);
                return true;
            }

            if (type is INamedTypeSymbol named &&
                TryGetSpanTypeInfo(named, out elementType, out var argumentIsReadOnly) &&
                (allowReadOnlySpan || !argumentIsReadOnly))
            {
                return true;
            }

            elementType = null!;
            return false;
        }

        bool TryUnifyNamedType(INamedTypeSymbol parameterNamed, INamedTypeSymbol argumentNamed)
        {
            var parameterDefinition = TypeSubstitution.GetDefinitionForSubstitution(parameterNamed);
            var argumentDefinition = TypeSubstitution.GetDefinitionForSubstitution(argumentNamed);
            if (!SymbolEqualityComparer.Default.Equals(parameterDefinition, argumentDefinition))
                return false;

            var paramArguments = TypeSubstitution.GetShallowTypeArguments(parameterNamed);
            var argArguments = TypeSubstitution.GetShallowTypeArguments(argumentNamed);

            if (paramArguments.IsDefault)
                paramArguments = ImmutableArray<ITypeSymbol>.Empty;

            if (argArguments.IsDefault)
                argArguments = ImmutableArray<ITypeSymbol>.Empty;

            if (paramArguments.Length != argArguments.Length)
                return false;

            for (int i = 0; i < paramArguments.Length; i++)
            {
                if (!TryInferFromTypes(compilation, paramArguments[i], argArguments[i], substitutions, inferenceMethod))
                    return false;
            }

            return true;
        }
    }

    private static bool IsGenericCollectionInterface(INamedTypeSymbol parameterNamed, string interfaceName)
    {
        var definition = parameterNamed.ConstructedFrom;

        if (!string.Equals(definition.Name, interfaceName, StringComparison.Ordinal))
            return false;

        var ns = definition.ContainingNamespace?.ToDisplayString();
        return string.Equals(ns, "System.Collections.Generic", StringComparison.Ordinal);
    }

    internal static bool SatisfiesMethodConstraints(
        IMethodSymbol method,
        ImmutableArray<ITypeSymbol> typeArguments,
        Binder? binder,
        out TypeArgumentConstraintFailure? constraintFailure)
    {
        constraintFailure = null;
        EnsureConstraintTypesResolved(binder, method, typeArguments);

        var typeParameters = method.TypeParameters;

        if (typeParameters.Length != typeArguments.Length)
            return true;

        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        for (int i = 0; i < typeParameters.Length; i++)
            substitutions[typeParameters[i]] = typeArguments[i];

        for (int i = 0; i < typeParameters.Length; i++)
        {
            var typeParameter = typeParameters[i];
            var typeArgument = typeArguments[i];
            var constraintKind = typeParameter.ConstraintKind;

            if ((constraintKind & TypeParameterConstraintKind.ReferenceType) != 0 &&
                !SemanticFacts.SatisfiesReferenceTypeConstraint(typeArgument))
            {
                constraintFailure = CreateConstraintFailure(method, typeArgument, "class", typeParameter);
                return false;
            }

            if ((constraintKind & TypeParameterConstraintKind.ValueType) != 0 &&
                !SemanticFacts.SatisfiesValueTypeConstraint(typeArgument))
            {
                constraintFailure = CreateConstraintFailure(method, typeArgument, "struct", typeParameter);
                return false;
            }

            if ((constraintKind & TypeParameterConstraintKind.NotNull) != 0 &&
                !SemanticFacts.SatisfiesNotNullConstraint(typeArgument))
            {
                constraintFailure = CreateConstraintFailure(method, typeArgument, "notnull", typeParameter);
                return false;
            }

            if ((constraintKind & TypeParameterConstraintKind.Constructor) != 0 &&
                !SemanticFacts.SatisfiesConstructorConstraint(typeArgument))
            {
                constraintFailure = CreateConstraintFailure(method, typeArgument, "new()", typeParameter);
                return false;
            }

            if ((constraintKind & TypeParameterConstraintKind.TypeConstraint) == 0)
                continue;

            foreach (var constraintType in typeParameter.ConstraintTypes)
            {
                var substitutedConstraint = SubstituteConstraintType(constraintType, substitutions);

                if (substitutedConstraint is ITypeParameterSymbol unsubstituted &&
                    !substitutions.ContainsKey(unsubstituted))
                {
                    continue;
                }

                if (substitutedConstraint is IErrorTypeSymbol)
                    continue;

                if (substitutedConstraint is INamedTypeSymbol namedConstraint)
                {
                    if (!SemanticFacts.SatisfiesNamedTypeConstraint(typeArgument, namedConstraint))
                    {
                        constraintFailure = CreateConstraintFailure(
                            method,
                            typeArgument,
                            namedConstraint.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            typeParameter);
                        return false;
                    }

                    continue;
                }

                if (!SemanticFacts.SatisfiesTypeConstraint(typeArgument, substitutedConstraint))
                {
                    constraintFailure = CreateConstraintFailure(
                        method,
                        typeArgument,
                        substitutedConstraint.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        typeParameter);
                    return false;
                }
            }
        }

        return true;
    }

    private static void EnsureConstraintTypesResolved(
        Binder? binder,
        IMethodSymbol method,
        ImmutableArray<ITypeSymbol> typeArguments)
    {
        if (binder is null)
            return;

        binder.EnsureTypeParameterConstraintTypesResolved(method.TypeParameters);

        if (method.ContainingType is { } containingType)
            binder.EnsureTypeParameterConstraintTypesResolved(containingType.TypeParameters);

        foreach (var typeArgument in typeArguments)
            EnsureConstraintTypesResolved(binder, typeArgument);
    }

    private static void EnsureConstraintTypesResolved(Binder binder, ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol typeParameter:
                binder.EnsureTypeParameterConstraintTypesResolved(ImmutableArray.Create(typeParameter));
                break;
            case INamedTypeSymbol namedType:
                foreach (var typeArgument in TypeSubstitution.GetShallowTypeArguments(namedType))
                    EnsureConstraintTypesResolved(binder, typeArgument);
                break;
            case NullableTypeSymbol nullableType:
                EnsureConstraintTypesResolved(binder, nullableType.UnderlyingType);
                break;
            case IArrayTypeSymbol arrayType:
                EnsureConstraintTypesResolved(binder, arrayType.ElementType);
                break;
        }
    }

    private static TypeArgumentConstraintFailure CreateConstraintFailure(
        IMethodSymbol method,
        ITypeSymbol typeArgument,
        string constraintDisplay,
        ITypeParameterSymbol typeParameter)
    {
        var argumentDisplay = typeArgument.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var genericDisplayName = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return new TypeArgumentConstraintFailure(argumentDisplay, constraintDisplay, typeParameter.Name, genericDisplayName);
    }

    private static ITypeSymbol NormalizeType(ITypeSymbol type)
    {
        return type switch
        {
            LiteralTypeSymbol literal => literal.UnderlyingType,
            IAddressTypeSymbol addressType => NormalizeType(addressType.ReferencedType),
            RefTypeSymbol refType => NormalizeType(refType.ElementType),
            _ => type,
        };
    }

    private static ITypeSymbol GetByRefInferenceElementType(ITypeSymbol type)
        => type switch
        {
            IAddressTypeSymbol addressType => addressType.ReferencedType,
            RefTypeSymbol refType => refType.ElementType,
            _ => type,
        };

    private static bool TryGetTupleInferenceElementTypes(
        ITypeSymbol type,
        out ImmutableArray<ITypeSymbol> elementTypes)
    {
        if (type is ITupleTypeSymbol tuple)
        {
            elementTypes = tuple.TupleElements.Select(static element => element.Type).ToImmutableArray();
            return true;
        }

        if (type is INamedTypeSymbol named &&
            (named.ConstructedFrom ?? named).SpecialType is >= SpecialType.System_ValueTuple_T1 and <= SpecialType.System_ValueTuple_TRest)
        {
            elementTypes = TypeSubstitution.GetShallowTypeArguments(named);
            return !elementTypes.IsDefaultOrEmpty;
        }

        elementTypes = default;
        return false;
    }

    private static bool TryGetNullableInferenceUnderlyingType(
        ITypeSymbol type,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? underlyingType)
    {
        if (type is NullableTypeSymbol nullable)
        {
            underlyingType = nullable.UnderlyingType;
            return true;
        }

        if (type is INamedTypeSymbol named &&
            (named.ConstructedFrom ?? named).SpecialType == SpecialType.System_Nullable_T)
        {
            var arguments = TypeSubstitution.GetShallowTypeArguments(named);
            if (arguments.Length == 1)
            {
                underlyingType = arguments[0];
                return true;
            }
        }

        underlyingType = null;
        return false;
    }

    private static ITypeParameterSymbol GetCanonicalTypeParameter(ITypeParameterSymbol typeParameter, IMethodSymbol? inferenceMethod)
    {
        if (inferenceMethod is null)
            return typeParameter;

        if (typeParameter.ContainingSymbol is IMethodSymbol &&
            typeParameter.Ordinal >= 0 &&
            typeParameter.Ordinal < inferenceMethod.TypeParameters.Length)
        {
            return inferenceMethod.TypeParameters[typeParameter.Ordinal];
        }

        return typeParameter;
    }

    private static ITypeSymbol SubstituteConstraintType(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        switch (type)
        {
            case ITypeParameterSymbol typeParameter:
                if (substitutions.TryGetValue(typeParameter, out var substitutedType))
                    return substitutedType;

                return type;

            case NullableTypeSymbol nullable:
                {
                    var underlyingType = SubstituteType(nullable.UnderlyingType, substitutions);
                    return SymbolEqualityComparer.Default.Equals(underlyingType, nullable.UnderlyingType)
                        ? type
                        : underlyingType.ApplySubstitutedNullability(nullable);
                }

            case IArrayTypeSymbol array:
                {
                    var elementType = SubstituteType(array.ElementType, substitutions);
                    return SymbolEqualityComparer.Default.Equals(elementType, array.ElementType)
                        ? type
                        : new ArrayTypeSymbol(array.BaseType, elementType, array.ContainingSymbol, array.ContainingType, array.ContainingNamespace, [], array.Rank, array.FixedLength);
                }

            case ITupleTypeSymbol tuple:
                return TypeSubstitution.SubstituteTupleElements(
                    tuple,
                    element => SubstituteType(element, substitutions));

            case INamedTypeSymbol named:
                {
                    var typeArguments = TypeSubstitution.GetShallowTypeArguments(named);
                    if (typeArguments.IsDefaultOrEmpty)
                        return type;

                    var rewritten = new ITypeSymbol[typeArguments.Length];
                    var changed = false;

                    for (int i = 0; i < typeArguments.Length; i++)
                    {
                        var substitutedArg = SubstituteConstraintType(typeArguments[i], substitutions);
                        rewritten[i] = substitutedArg;
                        changed |= !ReferenceEquals(substitutedArg, typeArguments[i]);
                    }

                    if (!changed)
                        return type;

                    try
                    {
                        var definition = TypeSubstitution.GetDefinitionForSubstitution(named);
                        if (definition.Arity == rewritten.Length)
                            return definition.Construct(rewritten);
                    }
                    catch
                    {
                        // Keep the original constraint type if reconstruction fails.
                    }

                    return type;
                }
        }

        return type;
    }

    private static ITypeSymbol SubstituteType(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        switch (type)
        {
            case ITypeParameterSymbol typeParameter:
                if (substitutions.TryGetValue(typeParameter, out var substitutedType) ||
                    TypeSubstitution.TryGetEquivalentTypeParameterSubstitution(typeParameter, substitutions, out substitutedType))
                {
                    return substitutedType;
                }

                return type;

            case INamedTypeSymbol named:
                {
                    var typeArguments = TypeSubstitution.GetShallowTypeArguments(named);
                    if (typeArguments.IsDefaultOrEmpty)
                        return type;

                    var rewritten = new ITypeSymbol[typeArguments.Length];
                    var changed = false;

                    for (var i = 0; i < typeArguments.Length; i++)
                    {
                        var substitutedArgument = SubstituteType(typeArguments[i], substitutions);
                        rewritten[i] = substitutedArgument;
                        changed |= !SymbolEqualityComparer.Default.Equals(substitutedArgument, typeArguments[i]);
                    }

                    if (!changed)
                        return type;

                    var definition = TypeSubstitution.GetDefinitionForSubstitution(named);
                    return definition.Arity == rewritten.Length
                        ? definition.Construct(rewritten)
                        : type;
                }

            default:
                return type;
        }
    }

    private static bool TryGetInferredTypeArgument(
        ITypeParameterSymbol typeParameter,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        out ITypeSymbol inferredType)
    {
        if (substitutions.TryGetValue(typeParameter, out inferredType!))
            return true;

        foreach (var entry in substitutions)
        {
            if (!AreEquivalentTypeParameters(typeParameter, entry.Key))
                continue;

            inferredType = entry.Value;
            return true;
        }

        inferredType = null!;
        return false;
    }

    private static bool AreEquivalentTypeParameters(
        ITypeParameterSymbol left,
        ITypeParameterSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left.OwnerKind != right.OwnerKind ||
            left.Ordinal != right.Ordinal)
        {
            return false;
        }

        return HaveEquivalentTypeParameterOwners(left.ContainingSymbol, right.ContainingSymbol);
    }

    private static bool HaveEquivalentTypeParameterOwners(
        ISymbol? leftOwner,
        ISymbol? rightOwner)
    {
        if (leftOwner is null || rightOwner is null)
            return false;

        if (SymbolEqualityComparer.Default.Equals(leftOwner, rightOwner))
            return true;

        if (leftOwner is INamedTypeSymbol leftType &&
            rightOwner is INamedTypeSymbol rightType)
        {
            return SymbolEqualityComparer.Default.Equals(
                TypeSubstitution.GetDefinitionForSubstitution(leftType),
                TypeSubstitution.GetDefinitionForSubstitution(rightType));
        }

        if (leftOwner is IMethodSymbol leftMethod &&
            rightOwner is IMethodSymbol rightMethod)
        {
            return SymbolEqualityComparer.Default.Equals(
                leftMethod.OriginalDefinition ?? leftMethod,
                rightMethod.OriginalDefinition ?? rightMethod);
        }

        return false;
    }

    private static void AddCandidateIfMissing(ImmutableArray<IMethodSymbol>.Builder builder, IMethodSymbol candidate)
    {
        foreach (var existing in builder)
        {
            if (string.Equals(existing.GetLookupIdentityKey(), candidate.GetLookupIdentityKey(), StringComparison.Ordinal))
                return;
        }

        builder.Add(candidate);
    }

    private static IEnumerable<IMethodSymbol> DistinctCandidates(IEnumerable<IMethodSymbol> methods)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            if (!seen.Add(method.GetLookupIdentityKey()))
                continue;

            yield return method;
        }
    }

    private static bool IsMoreSpecific(
        IMethodSymbol candidate,
        IMethodSymbol current,
        BoundArgument[] arguments,
        BoundExpression? receiver,
        Compilation compilation,
        Binder? binder)
    {
        bool better = false;
        var candParams = candidate.Parameters;
        var currentParams = current.Parameters;

        if (arguments.Any(static a => a.Expression is BoundFunctionExpression { Symbol: ILambdaSymbol { IsAsync: true } }))
        {
            var candidateTaskDepth = GetTaskDepth(candidate.ReturnType);
            var currentTaskDepth = GetTaskDepth(current.ReturnType);

            if (candidateTaskDepth < currentTaskDepth)
                return true;

            if (currentTaskDepth < candidateTaskDepth)
                return false;
        }

        bool candidateIsExtension = candidate.ExtensionMemberKind != ExtensionMemberKind.None && receiver is not null;
        bool currentIsExtension = current.ExtensionMemberKind != ExtensionMemberKind.None && receiver is not null;
        var candidateHasParams = candParams.Length > 0 && candParams[^1].IsVarParams;
        var currentHasParams = currentParams.Length > 0 && currentParams[^1].IsVarParams;

        if (candidateIsExtension != currentIsExtension)
            return !currentIsExtension;

        // Prefer non-varargs signatures over varargs signatures when both apply.
        // This mirrors standard overload resolution behavior where params-expanded
        // candidates are considered less specific than fixed-arity candidates.
        if (candidateHasParams != currentHasParams)
            return !candidateHasParams;

        // Nullable reference annotations do not distinguish otherwise identical
        // constructed signatures. Compare the original generic parameter shapes
        // so M<T>(Func<Task<T>>) wins over M<T>(Func<T>) for Func<Task<int>>.
        if (candidate.IsGenericMethod && current.IsGenericMethod &&
            HaveEquivalentParameterTypes(candParams, currentParams, SymbolEqualityComparer.IgnoringNullability))
        {
            var specificity = CompareGenericParameterSpecificity(candidate, current);
            if (specificity != 0)
                return specificity > 0;
        }

        if (candidateIsExtension && currentIsExtension && receiver?.Type is ITypeSymbol receiverType)
        {
            var candParamType = candParams[0].Type;
            var currentParamType = currentParams[0].Type;

            var candImplicit = IsImplicitConversion(compilation, receiverType, candParamType);
            var currentImplicit = IsImplicitConversion(compilation, receiverType, currentParamType);

            if (candImplicit && !currentImplicit)
            {
                better = true;
            }
            else if (!candImplicit && currentImplicit)
            {
                return false;
            }

            // The implicit receiver behaves like a first argument. If one receiver parameter type
            // implicitly converts to the other (but not vice versa), prefer the more specific one.
            // This is the rule that should distinguish IQueryable<T> from IEnumerable<T> for
            // extension methods such as Queryable.Select vs Enumerable.Select.
            var candToCurrent = IsImplicitConversion(compilation, candParamType, currentParamType);
            var currentToCand = IsImplicitConversion(compilation, currentParamType, candParamType);

            if (candToCurrent && !currentToCand)
            {
                better = true;
            }
            else if (!candToCurrent && currentToCand)
            {
                return false;
            }

            var candDist = GetInheritanceDistance(GetUnderlying(receiverType), GetUnderlying(candParamType));
            var currDist = GetInheritanceDistance(GetUnderlying(receiverType), GetUnderlying(currentParamType));

            if (candDist < currDist)
                better = true;
            else if (currDist < candDist)
                return false;
        }

        if (!TryMapArguments(candParams, arguments, candidateIsExtension, out var candidateMapped))
            return false;
        if (!TryMapArguments(currentParams, arguments, currentIsExtension, out var currentMapped))
            return false;

        var candidateMap = BuildArgumentParameterMap(candidateMapped, arguments, candidateIsExtension);
        var currentMap = BuildArgumentParameterMap(currentMapped, arguments, currentIsExtension);

        for (int i = 0; i < arguments.Length; i++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            var candidateParameterIndex = candidateMap[i];
            var currentParameterIndex = currentMap[i];

            if (candidateParameterIndex < 0 || currentParameterIndex < 0)
                continue;

            var argType = arguments[i].Expression.Type;
            if (argType is null)
                continue;

            var candParamType = candParams[candidateParameterIndex].Type;
            var currentParamType = currentParams[currentParameterIndex].Type;

            // Prefer a concrete delegate type (e.g. RequestDelegate) over the catch-all
            // `System.Delegate` / `System.MulticastDelegate` overload when both are applicable.
            // IMPORTANT: Decide immediately so later heuristics (like inheritance distance)
            // do not accidentally prefer `System.Delegate`.
            if (candParamType is INamedTypeSymbol candNamed && currentParamType is INamedTypeSymbol currentNamed)
            {
                var candIsSystemDelegate = IsSystemDelegateLike(candNamed);
                var currentIsSystemDelegate = IsSystemDelegateLike(currentNamed);

                if (candIsSystemDelegate != currentIsSystemDelegate)
                {
                    // Candidate wins iff it is NOT the System.Delegate-like parameter.
                    return !candIsSystemDelegate;
                }
            }

            if (argType.TypeKind == TypeKind.Null)
            {
                var candImplicit = IsImplicitConversion(compilation, candParamType, currentParamType);
                var currentImplicit = IsImplicitConversion(compilation, currentParamType, candParamType);

                if (candImplicit && !currentImplicit)
                {
                    better = true;
                }
                else if (!candImplicit && currentImplicit)
                {
                    return false;
                }

                continue;
            }

            // C# §12.6.4.4: when a lambda argument converts to both D and Expression<D>,
            // neither conversion is better. Skip this argument so the extension-receiver
            // distance (which correctly prefers IQueryable<T> over IEnumerable<T>) can decide.
            if (arguments[i].Expression is BoundFunctionExpression &&
                candParamType is INamedTypeSymbol candParamNamed &&
                currentParamType is INamedTypeSymbol currentParamNamed)
            {
                var candIsExprTree = TryGetExpressionTreeDelegateType(candParamNamed, out _);
                var currIsExprTree = TryGetExpressionTreeDelegateType(currentParamNamed, out _);

                if (candIsExprTree != currIsExprTree)
                    continue;
            }

            var candidateIsSpanConversion = IsImplicitSpanConversion(compilation, argType, candParamType);
            var currentIsSpanConversion = IsImplicitSpanConversion(compilation, argType, currentParamType);
            if (candidateIsSpanConversion != currentIsSpanConversion)
            {
                if (candidateIsSpanConversion)
                {
                    better = true;
                    continue;
                }

                return false;
            }

            if (TryGetSpanTypeInfo(candParamType, out var candidateSpanElement, out var candidateIsReadOnly) &&
                TryGetSpanTypeInfo(currentParamType, out var currentSpanElement, out var currentIsReadOnly))
            {
                if (candidateIsReadOnly != currentIsReadOnly &&
                    SymbolEqualityComparer.Default.Equals(candidateSpanElement, currentSpanElement))
                {
                    if (candidateIsReadOnly)
                    {
                        better = true;
                        continue;
                    }

                    return false;
                }
            }

            // Tie-breaker (C#-like): if one parameter type implicitly converts to the other (but not vice versa),
            // prefer the more specific type. This is essential for numeric overload resolution where inheritance
            // distance is not informative (e.g. byte -> int vs byte -> double/decimal).
            {
                // Prefer candidates that can use a standard implicit conversion from the actual argument.
                // This avoids ambiguous picks where one candidate only works through user-defined conversions.
                var candStandardFromArg = compilation.ClassifyConversion(argType, candParamType, includeUserDefined: false);
                var currentStandardFromArg = compilation.ClassifyConversion(argType, currentParamType, includeUserDefined: false);

                if (candStandardFromArg.Exists && candStandardFromArg.IsImplicit &&
                    (!currentStandardFromArg.Exists || !currentStandardFromArg.IsImplicit))
                {
                    better = true;
                    continue;
                }

                if ((!candStandardFromArg.Exists || !candStandardFromArg.IsImplicit) &&
                    currentStandardFromArg.Exists && currentStandardFromArg.IsImplicit)
                {
                    return false;
                }

                var candToCurrent = IsImplicitConversion(compilation, candParamType, currentParamType);
                var currentToCand = IsImplicitConversion(compilation, currentParamType, candParamType);

                if (candToCurrent && !currentToCand)
                {
                    better = true;
                }
                else if (!candToCurrent && currentToCand)
                {
                    return false;
                }
            }

            var candType = GetUnderlying(candParamType);
            var currentType = GetUnderlying(currentParamType);
            var underlyingArgType = GetUnderlying(argType);

            var candDist = GetInheritanceDistance(underlyingArgType, candType);
            var currDist = GetInheritanceDistance(underlyingArgType, currentType);

            if (candDist < currDist)
                better = true;
            else if (currDist < candDist)
                return false;
        }

        // When inference makes a generic candidate's parameter sequence
        // equivalent to a non-generic candidate, the non-generic member is the
        // more specific declaration. Conversion scores cannot distinguish this
        // case because both constructed signatures accept the same argument
        // types.
        if (!better &&
            candidate.IsGenericMethod != current.IsGenericMethod &&
            HaveEquivalentParameterTypes(candidate.Parameters, current.Parameters))
        {
            return !candidate.IsGenericMethod;
        }

        return better;
    }

    private static bool HaveEquivalentParameterTypes(
        ImmutableArray<IParameterSymbol> left,
        ImmutableArray<IParameterSymbol> right,
        SymbolEqualityComparer? comparer = null)
    {
        comparer ??= SymbolEqualityComparer.Default;
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].RefKind != right[i].RefKind ||
                !comparer.Equals(left[i].Type, right[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareGenericParameterSpecificity(IMethodSymbol candidate, IMethodSymbol current)
    {
        var candidateParameters = candidate.OriginalDefinition.Parameters;
        var currentParameters = current.OriginalDefinition.Parameters;
        var candidateBetter = false;
        var currentBetter = false;
        for (var i = 0; i < candidateParameters.Length; i++)
            Compare(candidateParameters[i].Type, currentParameters[i].Type);

        return candidateBetter == currentBetter ? 0 : candidateBetter ? 1 : -1;

        void Compare(ITypeSymbol left, ITypeSymbol right)
        {
            if (left is NullableTypeSymbol { UnderlyingType.IsValueType: false } leftNullable)
                left = leftNullable.UnderlyingType;
            if (right is NullableTypeSymbol { UnderlyingType.IsValueType: false } rightNullable)
                right = rightNullable.UnderlyingType;
            if (left is ITypeParameterSymbol || right is ITypeParameterSymbol)
            {
                candidateBetter |= left is not ITypeParameterSymbol;
                currentBetter |= right is not ITypeParameterSymbol;
            }
            else if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray &&
                     leftArray.Rank == rightArray.Rank)
            {
                Compare(leftArray.ElementType, rightArray.ElementType);
            }
            else if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed &&
                     leftNamed.OriginalDefinition.MetadataIdentityEquals(rightNamed.OriginalDefinition))
            {
                for (var i = 0; i < leftNamed.TypeArguments.Length; i++)
                    Compare(leftNamed.TypeArguments[i], rightNamed.TypeArguments[i]);
            }
        }
    }

    private static int GetTaskDepth(ITypeSymbol? type)
    {
        if (type is null)
            return int.MaxValue;

        var depth = 0;
        var current = type;

        while (current is INamedTypeSymbol named &&
            (named.SpecialType == SpecialType.System_Threading_Tasks_Task ||
             named.OriginalDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task_T))
        {
            depth++;

            if (named.SpecialType == SpecialType.System_Threading_Tasks_Task)
                break;

            current = named.TypeArguments.Length == 1 ? named.TypeArguments[0] : null;
        }

        return depth;
    }

    private static bool IsImplicitConversion(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var conversion = compilation.ClassifyConversion(source, destination);
        return conversion.Exists && conversion.IsImplicit;
    }

    private static bool IsImplicitSpanConversion(
        Compilation compilation,
        ITypeSymbol source,
        ITypeSymbol destination)
    {
        source = GetUnderlying(source);

        if (!TryGetSpanTypeInfo(destination, out _, out var destinationIsReadOnly))
            return false;

        var isSupportedSource =
            source is IArrayTypeSymbol { Rank: 1 } ||
            TryGetSpanTypeInfo(source, out _, out _) ||
            destinationIsReadOnly && source.SpecialType == SpecialType.System_String;

        return isSupportedSource && IsImplicitConversion(compilation, source, destination);
    }

    private static bool TryGetSpanTypeInfo(
        ITypeSymbol type,
        out ITypeSymbol elementType,
        out bool isReadOnly)
    {
        elementType = null!;
        isReadOnly = false;

        if (type is not INamedTypeSymbol named ||
            named.TypeArguments.Length != 1 ||
            named.ContainingNamespace?.ToDisplayString() != "System" ||
            named.Name is not ("Span" or "ReadOnlySpan"))
        {
            return false;
        }

        elementType = named.TypeArguments[0];
        isReadOnly = named.Name == "ReadOnlySpan";
        return true;
    }

    private static void LogComparison(
        List<OverloadArgumentComparisonLog>? log,
        IParameterSymbol parameter,
        ITypeSymbol? argumentType,
        OverloadArgumentComparisonResult result,
        string? detail)
    {
        if (log is null)
            return;

        log.Add(new OverloadArgumentComparisonLog(
            parameter.Name,
            parameter.RefKind,
            parameter.Type,
            argumentType,
            result,
            detail));
    }

    private static string DescribeConversion(Conversion conversion)
    {
        if (!conversion.Exists)
            return "conversion does not exist";

        var parts = new List<string>();

        if (conversion.IsIdentity)
            parts.Add("identity");
        if (conversion.IsNumeric)
            parts.Add("numeric");
        if (conversion.IsReference)
            parts.Add("reference");
        if (conversion.IsBoxing)
            parts.Add("boxing");
        if (conversion.IsUnboxing)
            parts.Add("unboxing");
        if (conversion.IsPointer)
            parts.Add("pointer");
        if (conversion.IsUnion)
            parts.Add("union");
        if (conversion.IsUserDefined)
            parts.Add("user-defined");
        if (conversion.IsAlias)
            parts.Add("alias");

        var kind = parts.Count == 0 ? "implicit" : string.Join(", ", parts);
        return conversion.IsImplicit ? kind : $"explicit {kind}";
    }

    private static bool TryMatch(
        IMethodSymbol method,
        BoundArgument[] arguments,
        BoundExpression? receiver,
        bool treatAsExtension,
        Compilation compilation,
        Binder? binder,
        Func<IParameterSymbol, BoundFunctionExpression, bool>? canBindLambda,
        List<OverloadArgumentComparisonLog>? comparisonLog,
        out int score)
    {
        score = 0;
        int parameterIndex = 0;
        var parameters = method.Parameters;

        if (treatAsExtension)
        {
            if (receiver is null || receiver.Type is null)
            {
                LogComparison(comparisonLog, parameters[parameterIndex], receiver?.Type, OverloadArgumentComparisonResult.NullArgumentType, "receiver is missing or has no type");
                return false;
            }

            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (!TryEvaluateArgument(parameters[parameterIndex], receiver, RefKind.None, compilation, binder, canBindLambda, comparisonLog, ref score))
                return false;

            parameterIndex++;
        }

        if (!TryMapArguments(parameters, arguments, treatAsExtension, out var mappedArguments, out var paramsArguments, out var paramsParameterIndex))
            return false;

        for (; parameterIndex < parameters.Length; parameterIndex++)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (parameterIndex == paramsParameterIndex)
            {
                var mappedParamsArgument = mappedArguments[parameterIndex];
                if (mappedParamsArgument is not null)
                {
                    if (!TryEvaluateArgument(parameters[parameterIndex], mappedParamsArgument.Value.Expression, mappedParamsArgument.Value.RefKind, compilation, binder, canBindLambda, comparisonLog, ref score))
                        return false;

                    continue;
                }

                if (paramsArguments.Length == 1 &&
                    !paramsArguments[0].IsSpread &&
                    paramsArguments[0].Type is ITypeSymbol singleParamsType)
                {
                    var singleParamsConversion = compilation.ClassifyConversion(singleParamsType, parameters[parameterIndex].Type);
                    if (singleParamsConversion.IsImplicit)
                    {
                        // Normal-form params binding: a single array-like argument maps directly.
                        if (!TryEvaluateArgument(parameters[parameterIndex], paramsArguments[0].Expression, paramsArguments[0].RefKind, compilation, binder, canBindLambda, comparisonLog, ref score))
                            return false;

                        // Prefer non-params overloads when both are otherwise equivalent.
                        score += 1;
                        continue;
                    }
                }

                // Expanded-form params binding: 0..N element arguments.
                score += 1;
                foreach (var paramsArgument in paramsArguments)
                {
                    ThrowIfDiagnosticBindingCancellationRequested(binder);

                    if (paramsArgument.IsSpread)
                    {
                        if (!TryEvaluateArgument(parameters[parameterIndex], paramsArgument.Expression, paramsArgument.RefKind, compilation, binder, canBindLambda, comparisonLog, ref score))
                            return false;

                        continue;
                    }

                    if (!TryEvaluateParamsElement(parameters[parameterIndex], paramsArgument.Expression, compilation, binder, comparisonLog, ref score))
                        return false;
                }

                continue;
            }

            var mapped = mappedArguments[parameterIndex];
            if (mapped is null)
            {
                if (!parameters[parameterIndex].HasExplicitDefaultValue)
                {
                    LogComparison(comparisonLog, parameters[parameterIndex], argumentType: null, OverloadArgumentComparisonResult.MissingArgument, "no argument supplied");
                    return false;
                }

                LogComparison(comparisonLog, parameters[parameterIndex], argumentType: null, OverloadArgumentComparisonResult.DefaultValueUsed, "default parameter value used");

                continue;
            }

            if (!TryEvaluateArgument(parameters[parameterIndex], mapped.Value.Expression, mapped.Value.RefKind, compilation, binder, canBindLambda, comparisonLog, ref score))
                return false;
        }

        return true;
    }

    internal static bool TryMapArguments(
        ImmutableArray<IParameterSymbol> parameters,
        IReadOnlyList<BoundArgument> arguments,
        bool treatAsExtension,
        out BoundArgument?[] orderedArguments)
    {
        return TryMapArguments(parameters, arguments, treatAsExtension, out orderedArguments, out _, out _);
    }

    internal static bool TryMapArguments(
        ImmutableArray<IParameterSymbol> parameters,
        IReadOnlyList<BoundArgument> arguments,
        bool treatAsExtension,
        out BoundArgument?[] orderedArguments,
        out BoundArgument[] paramsArguments,
        out int paramsParameterIndex)
    {
        orderedArguments = new BoundArgument?[parameters.Length];
        paramsArguments = Array.Empty<BoundArgument>();
        paramsParameterIndex = -1;

        var nextPositional = treatAsExtension ? 1 : 0;
        var maxNamedIndex = -1;
        var seenNamed = false;
        var paramsBuilder = new List<BoundArgument>();

        var hasParamsParameter = parameters.Length > nextPositional && parameters[^1].IsVarParams;
        if (hasParamsParameter)
            paramsParameterIndex = parameters.Length - 1;

        foreach (var argument in arguments)
        {
            if (argument.Name is { } name)
            {
                var parameterIndex = FindParameterIndex(parameters, name);
                if (parameterIndex < 0)
                    return false;

                if (treatAsExtension && parameterIndex == 0)
                    return false;

                if (parameterIndex == paramsParameterIndex)
                {
                    if (argument.IsSpread)
                    {
                        if (orderedArguments[parameterIndex] is not null)
                            return false;

                        paramsBuilder.Add(argument);
                    }
                    else
                    {
                        if (orderedArguments[parameterIndex] is not null || paramsBuilder.Count > 0)
                            return false;

                        orderedArguments[parameterIndex] = argument;
                    }
                }
                else
                {
                    if (orderedArguments[parameterIndex] is not null || argument.IsSpread)
                        return false;

                    orderedArguments[parameterIndex] = argument;
                }

                seenNamed = true;
                if (parameterIndex > maxNamedIndex)
                    maxNamedIndex = parameterIndex;
                continue;
            }

            while (nextPositional < parameters.Length &&
                   nextPositional != paramsParameterIndex &&
                   orderedArguments[nextPositional] is not null)
                nextPositional++;

            if (nextPositional >= parameters.Length)
            {
                if (paramsParameterIndex >= 0 && orderedArguments[paramsParameterIndex] is null)
                {
                    paramsBuilder.Add(argument);
                    continue;
                }

                return false;
            }

            if (nextPositional == paramsParameterIndex)
            {
                if (orderedArguments[paramsParameterIndex] is not null)
                    return false;

                if (seenNamed && nextPositional <= maxNamedIndex)
                    return false;

                paramsBuilder.Add(argument);
                continue;
            }

            if (seenNamed && nextPositional <= maxNamedIndex)
                return false;

            if (argument.IsSpread)
                return false;

            orderedArguments[nextPositional] = argument;
            nextPositional++;
        }

        var requiredStart = treatAsExtension ? 1 : 0;
        for (var i = requiredStart; i < parameters.Length; i++)
        {
            if (i == paramsParameterIndex)
                continue;

            if (orderedArguments[i] is null && !parameters[i].HasExplicitDefaultValue)
                return false;
        }

        paramsArguments = paramsBuilder.ToArray();
        return true;
    }

    private static bool TryEvaluateParamsElement(
        IParameterSymbol paramsParameter,
        BoundExpression argument,
        Compilation compilation,
        Binder? binder,
        List<OverloadArgumentComparisonLog>? comparisonLog,
        ref int score)
    {
        ThrowIfDiagnosticBindingCancellationRequested(binder);

        if (!TryGetVarParamsElementType(paramsParameter.Type, out var elementType))
            return TryEvaluateArgument(paramsParameter, argument, RefKind.None, compilation, binder, null, comparisonLog, ref score);

        var argumentType = argument.Type;
        if (argumentType is null || argumentType.SpecialType == SpecialType.System_Void)
        {
            LogComparison(comparisonLog, paramsParameter, argumentType, OverloadArgumentComparisonResult.NullArgumentType, "params element argument has no valid type");
            return false;
        }

        var conversion = compilation.ClassifyConversion(argumentType, elementType);
        if (!conversion.IsImplicit)
        {
            LogComparison(comparisonLog, paramsParameter, argumentType, OverloadArgumentComparisonResult.ConversionFailed, "params element conversion failed");
            return false;
        }

        score += IsImplicitSpanConversion(compilation, argumentType, elementType)
            ? 2
            : GetConversionScore(conversion);
        LogComparison(comparisonLog, paramsParameter, argumentType, OverloadArgumentComparisonResult.Success, "params element conversion succeeded");
        return true;
    }

    private static bool TryGetVarParamsElementType(ITypeSymbol paramsType, out ITypeSymbol elementType)
    {
        if (paramsType is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (paramsType is INamedTypeSymbol namedType)
        {
            if (namedType.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                namedType.TypeArguments.Length == 1)
            {
                elementType = namedType.TypeArguments[0];
                return true;
            }

            var definition = namedType.OriginalDefinition ?? namedType;
            if (definition.MetadataName == "IEnumerable`1" &&
                definition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
                namedType.TypeArguments.Length == 1)
            {
                elementType = namedType.TypeArguments[0];
                return true;
            }

            foreach (var iface in namedType.AllInterfaces)
            {
                if (iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                    iface.TypeArguments.Length == 1)
                {
                    elementType = iface.TypeArguments[0];
                    return true;
                }

                var ifaceDefinition = iface.OriginalDefinition ?? iface;
                if (ifaceDefinition.MetadataName == "IEnumerable`1" &&
                    ifaceDefinition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
                    iface.TypeArguments.Length == 1)
                {
                    elementType = iface.TypeArguments[0];
                    return true;
                }
            }
        }

        elementType = null!;
        return false;
    }

    private static int[] BuildArgumentParameterMap(
        BoundArgument?[] orderedArguments,
        BoundArgument[] originalArguments,
        bool treatAsExtension)
    {
        var map = new int[originalArguments.Length];
        Array.Fill(map, -1);

        for (int parameterIndex = treatAsExtension ? 1 : 0; parameterIndex < orderedArguments.Length; parameterIndex++)
        {
            var argument = orderedArguments[parameterIndex];
            if (argument is null)
                continue;

            for (int argumentIndex = 0; argumentIndex < originalArguments.Length; argumentIndex++)
            {
                if (ReferenceEquals(originalArguments[argumentIndex].Expression, argument.Value.Expression))
                {
                    map[argumentIndex] = parameterIndex;
                    break;
                }
            }
        }

        return map;
    }

    private static int FindParameterIndex(ImmutableArray<IParameterSymbol> parameters, string name)
    {
        // Raven convention: primary constructor parameters are declared PascalCase (e.g. Message, Code)
        // but named argument labels in call sites are camelCase (e.g. message:, code:).
        // Use OrdinalIgnoreCase so that both conventions resolve to the correct parameter.
        for (int i = 0; i < parameters.Length; i++)
        {
            if (string.Equals(parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool TryEvaluateArgument(
        IParameterSymbol parameter,
        BoundExpression argument,
        RefKind argumentRefKind,
        Compilation compilation,
        Binder? binder,
        Func<IParameterSymbol, BoundFunctionExpression, bool>? canBindLambda,
        List<OverloadArgumentComparisonLog>? comparisonLog,
        ref int score)
    {
        ThrowIfDiagnosticBindingCancellationRequested(binder);

        var argType = argument.Type;
        var parameterTargetType = parameter.Type is NullableTypeSymbol nullableParameterType
            ? nullableParameterType.UnderlyingType
            : parameter.Type;

        if (parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
        {
            if (argumentRefKind != parameter.RefKind)
            {
                LogComparison(comparisonLog, parameter, argument.Type, OverloadArgumentComparisonResult.RefKindMismatch, "argument ref kind does not match parameter ref kind");
                return false;
            }

            if (argType is null)
            {
                LogComparison(comparisonLog, parameter, argument.Type, OverloadArgumentComparisonResult.NullArgumentType, "argument type is null");
                return false;
            }

            if (argType.SpecialType == SpecialType.System_Void)
            {
                LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.VoidArgument, "argument type is void");
                return false;
            }

            if (argument is not BoundAddressOfExpression ||
                argType is not IAddressTypeSymbol addressType ||
                argType.SpecialType == SpecialType.System_Void)
            {
                LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.RefKindMismatch, "argument is not an address to match ref/out/in");
                return false;
            }

            var parameterType = parameter.Type;
            var referencedType = addressType.ReferencedType;

            var expectedByRefElementType = parameter.GetByRefElementType();
            // Reference-type nullable annotations are not part of the CLR by-ref
            // signature. They affect input/output contracts and flow, but must not
            // make an otherwise identical ref/out/in storage location inapplicable.
            // The comparer still distinguishes nullable value types because those
            // have a different runtime representation.
            if (!SymbolEqualityComparer.IgnoringNullability.Equals(referencedType, expectedByRefElementType))
            {
                LogComparison(comparisonLog, parameter, referencedType, OverloadArgumentComparisonResult.RefKindMismatch, "address type does not match parameter type");
                return false;
            }

            LogComparison(comparisonLog, parameter, referencedType, OverloadArgumentComparisonResult.Success, "address argument matches ref/out/in parameter");

            return true;
        }

        if (argumentRefKind != RefKind.None)
        {
            LogComparison(comparisonLog, parameter, argument.Type, OverloadArgumentComparisonResult.RefKindMismatch, "ref/out/in argument supplied for non-byref parameter");
            return false;
        }

        // Method group conversions must be validated against the target delegate type.
        // Without this, a method group can appear applicable for unrelated delegate types
        // (e.g. RequestDelegate) which breaks overload resolution for APIs that have both
        // RequestDelegate and System.Delegate overloads (like ASP.NET Minimal APIs).
        if (argument is BoundMethodGroupExpression methodGroup && parameterTargetType is INamedTypeSymbol target)
        {
            if (target.TypeKind == TypeKind.Delegate)
            {
                if (!IsMethodGroupCompatibleWithDelegate(methodGroup, target, compilation, binder))
                {
                    LogComparison(comparisonLog, parameter, target, OverloadArgumentComparisonResult.ConversionFailed, "method group is incompatible with delegate");
                    return false;
                }

                // Compatible method group => treat as a successful match without further conversion scoring.
                LogComparison(comparisonLog, parameter, target, OverloadArgumentComparisonResult.Success, "method group compatible with delegate");
                return true;
            }

            // Allow method groups to match System.Delegate / System.MulticastDelegate parameters.
            if (IsSystemDelegateLike(target))
            {
                LogComparison(comparisonLog, parameter, target, OverloadArgumentComparisonResult.Success, "method group accepted for System.Delegate-like parameter");
                return true;
            }
        }

        bool lambdaCompatible = false;
        if (argument is BoundFunctionExpression lambda && parameterTargetType is INamedTypeSymbol delegateType)
        {
            // Unwrap Expression<TDelegate> — treat it like a delegate parameter for lambda compatibility,
            // using the inner delegate type for signature checking.
            var isExpressionTree = false;
            var effectiveDelegateType = delegateType;
            if (delegateType.TypeKind != TypeKind.Delegate &&
                TryGetExpressionTreeDelegateType(delegateType, out var innerDelegateType))
            {
                effectiveDelegateType = innerDelegateType;
                isExpressionTree = true;
            }

            if (effectiveDelegateType.TypeKind == TypeKind.Delegate)
            {
                if (lambda.Symbol is ILambdaSymbol { IsAsync: true })
                {
                    var invoke = effectiveDelegateType.GetDelegateInvokeMethod();
                    string? asyncDetail = null;
                    var asyncCompatible = invoke is not null && IsAsyncDelegateCompatible(lambda, invoke.ReturnType, compilation, out asyncDetail);

                    if (!asyncCompatible)
                    {
                        LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.LambdaIncompatible, asyncDetail ?? "async delegate mismatch");
                        return false;
                    }
                }

                var effectiveDelegateTypeArguments = TypeSubstitution.GetShallowTypeArguments(effectiveDelegateType);
                if (effectiveDelegateType.IsGenericType &&
                    !effectiveDelegateTypeArguments.IsDefaultOrEmpty &&
                    effectiveDelegateTypeArguments.Any(static t => t is ITypeParameterSymbol))
                {
                    lambdaCompatible = true;
                    LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.Success, isExpressionTree ? "lambda retained for generic expression-tree binding" : "lambda retained for generic delegate binding");
                }
                else if (canBindLambda is not null)
                {
                    if (!canBindLambda(parameter, lambda))
                    {
                        LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.LambdaIncompatible, "lambda rejected by binder callback");
                        return false;
                    }

                    lambdaCompatible = true;
                    LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.Success, "lambda accepted via binder callback");
                }
                else if (!lambda.IsCompatibleWithDelegate(effectiveDelegateType, compilation))
                {
                    LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.LambdaIncompatible, "lambda signature is incompatible with delegate");
                    return false;
                }
                else
                {
                    lambdaCompatible = true;
                    LogComparison(comparisonLog, parameter, effectiveDelegateType, OverloadArgumentComparisonResult.Success, isExpressionTree ? "lambda compatible with expression-tree delegate" : "lambda compatible with delegate");
                }

                argType = lambda.DelegateType ?? argType;
            }
            else if (IsSystemDelegateLike(delegateType))
            {
                var inferredDelegate = lambda.DelegateType as INamedTypeSymbol;
                if (inferredDelegate is null || inferredDelegate.TypeKind != TypeKind.Delegate)
                {
                    LogComparison(comparisonLog, parameter, delegateType, OverloadArgumentComparisonResult.LambdaIncompatible, "lambda does not have an inferred delegate type");
                    return false;
                }

                lambdaCompatible = true;
                argType = inferredDelegate;
                LogComparison(comparisonLog, parameter, delegateType, OverloadArgumentComparisonResult.Success, "lambda accepted for System.Delegate-like parameter");
            }
        }

        if (argType is null)
        {
            LogComparison(comparisonLog, parameter, argument.Type, OverloadArgumentComparisonResult.NullArgumentType, "argument type is null");
            return false;
        }

        if (argType.SpecialType == SpecialType.System_Void)
        {
            LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.VoidArgument, "argument type is void");
            return false;
        }

        if (argument is BoundAddressOfExpression)
        {
            var conversion = compilation.ClassifyConversion(argType, parameter.Type);
            if (!conversion.IsImplicit)
            {
                LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.ConversionFailed, DescribeConversion(conversion));
                return false;
            }

            score += GetConversionScore(conversion);
            LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.Success, DescribeConversion(conversion));
            return true;
        }

        if (!lambdaCompatible)
        {
            var conversion = compilation.ClassifyConversion(argType, parameter.Type);
            if (!conversion.IsImplicit)
            {
                LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.ConversionFailed, DescribeConversion(conversion));
                return false;
            }

            var conversionScore = IsImplicitSpanConversion(compilation, argType, parameter.Type)
                ? 2
                : GetConversionScore(conversion);

            if (argType.TypeKind == TypeKind.Delegate && parameter.Type.TypeKind == TypeKind.Delegate &&
                SymbolEqualityComparer.IgnoringNullability.Equals(argType, parameter.Type))
            {
                conversionScore = 0;
            }

            if (parameter.Type is NullableTypeSymbol nullableParam && !Conversion.IsNullable(argType))
            {
                var liftedConversion = compilation.ClassifyConversion(argType, nullableParam.UnderlyingType);
                if (liftedConversion.Exists)
                    conversionScore = GetConversionScore(liftedConversion);

                conversionScore++;
            }

            score += conversionScore;
            LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.Success, DescribeConversion(conversion));
        }
        else
        {
            LogComparison(comparisonLog, parameter, argType, OverloadArgumentComparisonResult.Success, "lambda compatibility accepted");
        }
        return true;

        static bool IsAsyncDelegateCompatible(
            BoundFunctionExpression lambda,
            ITypeSymbol delegateReturnType,
            Compilation compilation,
            out string? failureDetail)
        {
            failureDetail = null;
            var lambdaAsyncReturn = AsyncReturnTypeUtilities.InferAsyncReturnType(compilation, lambda.Body);
            var lambdaAsyncResult = AsyncReturnTypeUtilities.ExtractAsyncResultType(compilation, lambdaAsyncReturn)
                ?? lambdaAsyncReturn;

            delegateReturnType = delegateReturnType.GetNonNullableType();

            if (delegateReturnType.SpecialType == SpecialType.System_Void)
            {
                var unitType = compilation.GetSpecialType(SpecialType.System_Unit);
                var compatible = SymbolEqualityComparer.Default.Equals(lambdaAsyncResult, unitType);
                if (!compatible)
                    failureDetail = $"async lambda result {lambdaAsyncResult?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} does not map to void";

                return compatible;
            }

            var unit = compilation.GetSpecialType(SpecialType.System_Unit);

            if (delegateReturnType.SpecialType == SpecialType.System_Threading_Tasks_Task)
            {
                var compatible = SymbolEqualityComparer.Default.Equals(lambdaAsyncResult, unit);
                if (!compatible)
                    failureDetail = $"async lambda result {lambdaAsyncResult?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} does not map to Task";

                return compatible;
            }

            if (delegateReturnType is INamedTypeSymbol named &&
                named.OriginalDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task_T &&
                named.TypeArguments.Length == 1)
            {
                var expectedResult = named.TypeArguments[0];
                var conversion = compilation.ClassifyConversion(lambdaAsyncResult, expectedResult);
                if (conversion.Exists && conversion.IsImplicit)
                    return true;

                failureDetail = $"async lambda result {lambdaAsyncResult?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} does not map to {expectedResult.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
                return false;
            }

            failureDetail = $"delegate return {delegateReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} is not task-shaped";
            return false;
        }
    }

    private static bool IsMethodGroupCompatibleWithDelegate(
        BoundMethodGroupExpression methodGroup,
        INamedTypeSymbol delegateType,
        Compilation compilation,
        Binder? binder)
    {
        if (delegateType.TypeKind != TypeKind.Delegate)
            return false;

        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
            return false;

        var candidates = methodGroup.Methods;
        if (candidates.IsDefaultOrEmpty)
            return false;

        foreach (var candidate in candidates)
        {
            ThrowIfDiagnosticBindingCancellationRequested(binder);

            if (candidate is null)
                continue;

            var constructedCandidate = TryConstructMethodGroupCandidate(
                candidate,
                invoke,
                compilation,
                binder);
            if (constructedCandidate is null)
                continue;

            // Delegate conversion requires the method to be invokable with the delegate's parameter list.
            // For now we require equal parameter counts (no optional/params-array bridging).
            if (constructedCandidate.Parameters.Length != invoke.Parameters.Length)
                continue;

            var ok = true;

            for (int i = 0; i < invoke.Parameters.Length; i++)
            {
                ThrowIfDiagnosticBindingCancellationRequested(binder);

                var invokeParam = invoke.Parameters[i];
                var methodParam = constructedCandidate.Parameters[i];

                if (invokeParam.RefKind != methodParam.RefKind)
                {
                    ok = false;
                    break;
                }

                // Delegate parameters are contravariant: the delegate-provided value must be
                // implicitly convertible to the method parameter type.
                var conv = compilation.ClassifyConversion(invokeParam.Type, methodParam.Type);
                if (!conv.Exists || !conv.IsImplicit)
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
                continue;

            // Returns are covariant: the method return must be implicitly convertible to the delegate return.
            var delegateReturn = invoke.ReturnType;
            var methodReturn = constructedCandidate.ReturnType;

            if (delegateReturn.SpecialType == SpecialType.System_Void)
            {
                // Accept a Unit-returning method for a void-returning delegate.
                if (methodReturn.SpecialType != SpecialType.System_Void &&
                    methodReturn.SpecialType != SpecialType.System_Unit)
                {
                    continue;
                }

                return true;
            }

            var retConv = compilation.ClassifyConversion(methodReturn, delegateReturn);
            if (!retConv.Exists || !retConv.IsImplicit)
                continue;

            return true;
        }

        return false;
    }

    private static bool IsSystemDelegateLike(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition ?? type;
        if (definition.ContainingNamespace is null)
            return false;
        if (!string.Equals(definition.ContainingNamespace.ToDisplayString(), "System", StringComparison.Ordinal))
            return false;

        return string.Equals(definition.MetadataName, "Delegate", StringComparison.Ordinal) ||
               string.Equals(definition.MetadataName, "MulticastDelegate", StringComparison.Ordinal);
    }

    private static ITypeSymbol GetUnderlying(ITypeSymbol type) => type switch
    {
        NullableTypeSymbol nt => nt.UnderlyingType,
        LiteralTypeSymbol lt => lt.UnderlyingType,
        _ => type,
    };

    private static int GetInheritanceDistance(ITypeSymbol? derived, ITypeSymbol baseType)
    {
        if (derived is null)
            return int.MaxValue;

        if (SymbolEqualityComparer.Default.Equals(derived, baseType))
            return 0;

        // Non-interface target: walk the BaseType chain (class hierarchy).
        if (baseType.TypeKind != TypeKind.Interface)
        {
            int distance = 1;
            var current = derived.BaseType;
            while (current is not null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                    return distance;
                current = current.BaseType;
                distance++;
            }
            return int.MaxValue;
        }

        // Interface target: BFS over directly-declared interfaces to find shortest path.
        // Quick containment check first — if the interface isn't implemented at all, bail out.
        if (baseType is INamedTypeSymbol baseNamed &&
            !derived.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, baseNamed)))
        {
            return int.MaxValue;
        }

        var queue = new Queue<(INamedTypeSymbol iface, int depth)>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // Seed with the derived type's directly-declared interfaces at depth 1.
        foreach (var iface in derived.Interfaces)
        {
            if (visited.Add(iface))
                queue.Enqueue((iface, 1));
        }

        while (queue.Count > 0)
        {
            var (iface, depth) = queue.Dequeue();
            if (SymbolEqualityComparer.Default.Equals(iface, baseType))
                return depth;

            foreach (var parent in iface.Interfaces)
            {
                if (visited.Add(parent))
                    queue.Enqueue((parent, depth + 1));
            }
        }

        return int.MaxValue;
    }

    private static int GetConversionScore(Conversion conversion)
    {
        if (!conversion.Exists)
            return int.MaxValue; // Not applicable, shouldn't occur during scoring

        if (conversion.IsIdentity)
            return 0;

        if (conversion.IsNumeric)
            return 1;

        if (conversion.IsPointer)
            return 1;

        if (conversion.IsReference)
            return 2;

        if (conversion.IsBoxing)
            return 3;

        if (conversion.IsUserDefined)
            return 4;

        if (conversion.IsUnboxing)
            return 5;

        return 10; // fallback or unspecified conversion
    }

    private static bool HasSufficientArguments(ImmutableArray<IParameterSymbol> parameters, int providedCount)
    {
        var hasParamsParameter = parameters.Length > 0 && parameters[^1].IsVarParams;
        if (!hasParamsParameter && providedCount > parameters.Length)
            return false;

        var required = GetRequiredParameterCount(parameters);
        return providedCount >= required;
    }

    private static int GetRequiredParameterCount(ImmutableArray<IParameterSymbol> parameters)
    {
        var required = parameters.Length;
        while (required > 0 &&
               (parameters[required - 1].HasExplicitDefaultValue || parameters[required - 1].IsVarParams))
            required--;

        return required;
    }

    /// <summary>
    /// If <paramref name="type"/> is <c>Expression&lt;TDelegate&gt;</c>, extracts <c>TDelegate</c> and
    /// returns <see langword="true"/>. Otherwise returns <see langword="false"/>.
    /// </summary>
    private static bool TryGetExpressionTreeDelegateType(ITypeSymbol type, out INamedTypeSymbol delegateType)
    {
        delegateType = null!;

        if (type is NullableTypeSymbol nullable)
            type = nullable.UnderlyingType;

        if (type is not INamedTypeSymbol named)
            return false;

        var definition = (named.OriginalDefinition as INamedTypeSymbol) ?? named;
        if (definition.Arity != 1)
            return false;

        if (!string.Equals(definition.Name, "Expression", StringComparison.Ordinal) &&
            !string.Equals(definition.MetadataName, "Expression`1", StringComparison.Ordinal))
            return false;

        if (named.TypeArguments.Length != 1)
            return false;

        var candidate = named.TypeArguments[0];
        if (candidate is not INamedTypeSymbol candidateDelegate)
            return false;

        if (candidateDelegate.TypeKind != TypeKind.Delegate &&
            candidateDelegate.GetDelegateInvokeMethod() is null)
            return false;

        delegateType = candidateDelegate;
        return true;
    }
}

internal readonly struct OverloadResolutionResult
{
    public OverloadResolutionResult(IMethodSymbol? method)
        : this(method, ImmutableArray<IMethodSymbol>.Empty, null, null)
    {
    }

    internal OverloadResolutionResult(IMethodSymbol? method, TypeArgumentConstraintFailure? constraintFailure)
        : this(method, ImmutableArray<IMethodSymbol>.Empty, constraintFailure, null)
    {
    }

    internal OverloadResolutionResult(
        IMethodSymbol? method,
        TypeArgumentConstraintFailure? constraintFailure,
        IMethodSymbol? constraintFailureCandidate)
        : this(method, ImmutableArray<IMethodSymbol>.Empty, constraintFailure, constraintFailureCandidate)
    {
    }

    private OverloadResolutionResult(
        IMethodSymbol? method,
        ImmutableArray<IMethodSymbol> ambiguousCandidates,
        TypeArgumentConstraintFailure? constraintFailure,
        IMethodSymbol? constraintFailureCandidate)
    {
        Method = method;
        AmbiguousCandidates = ambiguousCandidates;
        ConstraintFailure = constraintFailure;
        ConstraintFailureCandidate = constraintFailureCandidate;
    }

    public IMethodSymbol? Method { get; }

    public ImmutableArray<IMethodSymbol> AmbiguousCandidates { get; }

    public TypeArgumentConstraintFailure? ConstraintFailure { get; }

    public IMethodSymbol? ConstraintFailureCandidate { get; }

    public bool Success => Method is not null && !IsAmbiguous;

    public bool IsAmbiguous => !AmbiguousCandidates.IsDefaultOrEmpty && AmbiguousCandidates.Length > 0;

    public static OverloadResolutionResult Ambiguous(ImmutableArray<IMethodSymbol> candidates)
    {
        if (candidates.IsDefault)
            candidates = ImmutableArray<IMethodSymbol>.Empty;

        return new OverloadResolutionResult(null, candidates, null, null);
    }
}

internal readonly record struct TypeArgumentConstraintFailure(
    string TypeArgument,
    string Constraint,
    string TypeParameter,
    string GenericName);
