using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

using Raven.CodeAnalysis.CodeGen;
using Raven.CodeAnalysis.Documentation;

namespace Raven.CodeAnalysis.Symbols;

[DebuggerDisplay("{GetDebuggerDisplay(), nq}")]
internal sealed class ConstructedNamedTypeSymbol : INamedTypeSymbol, IUnionSymbol, IUnionCaseTypeSymbol, IConstructedTypeSubstitutionInfo
{
    private readonly INamedTypeSymbol _originalDefinition;
    private readonly Dictionary<ITypeParameterSymbol, ITypeSymbol> _substitutionMap;
    private readonly INamedTypeSymbol? _containingTypeOverride;
    private ImmutableArray<ISymbol>? _members;
    private readonly ConcurrentDictionary<string, ImmutableArray<ISymbol>> _membersByName = new(StringComparer.Ordinal);
    private ImmutableArray<IFieldSymbol>? _tupleElements;
    private ImmutableArray<INamedTypeSymbol>? _interfaces;
    private ImmutableArray<INamedTypeSymbol>? _allInterfaces;
    private ImmutableArray<ITypeSymbol>? _variants;
    private ImmutableArray<IUnionCaseTypeSymbol>? _declaredCases;
    private ImmutableArray<ITypeSymbol>? _memberTypes;
    private ImmutableArray<IParameterSymbol>? _constructorParameters;
    private ImmutableArray<ITypeParameterSymbol> _typeParameters;
    private INamedTypeSymbol? _baseType;
    private IUnionSymbol? _union;
    private IFieldSymbol? _discriminatorField;
    private IFieldSymbol? _payloadField;

    private readonly ImmutableArray<ITypeSymbol> _explicitTypeArguments;
    private ImmutableArray<ITypeSymbol> _typeArguments;
    private ImmutableArray<ITypeSymbol> _allTypeArguments;
    private TypeArgumentsComputationState _typeArgumentsState;

    private enum TypeArgumentsComputationState
    {
        Uninitialized = 0,
        Computing = 1,
        Computed = 2
    }

    public ConstructedNamedTypeSymbol(INamedTypeSymbol originalDefinition, ImmutableArray<ITypeSymbol> typeArguments)
        : this(originalDefinition, typeArguments, inheritedSubstitution: null, containingTypeOverride: null)
    {
    }

    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> CreateSubstitutionMap(
     INamedTypeSymbol originalDefinition,
     ImmutableArray<ITypeSymbol> typeArguments,
     Dictionary<ITypeParameterSymbol, ITypeSymbol>? inheritedSubstitution)
    {
        static ITypeParameterSymbol NormalizeKey(ITypeParameterSymbol parameter) =>
            (ITypeParameterSymbol)(parameter.OriginalDefinition ?? parameter);

        var substitution = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
            ReferenceEqualityComparer.Instance);

        if (inheritedSubstitution is not null)
        {
            foreach (var (key, value) in inheritedSubstitution)
                substitution[NormalizeKey(key)] = value;
        }

        if (!typeArguments.IsDefaultOrEmpty)
        {
            var typeParameters = originalDefinition.TypeParameters;
            var argIndex = 0;

            foreach (var rawParam in typeParameters)
            {
                if (argIndex >= typeArguments.Length)
                    break;

                var parameter = NormalizeKey(rawParam);
                var argument = typeArguments[argIndex++];

                if (substitution.TryGetValue(parameter, out var existing))
                {
                    // If they’re logically equal, nothing to do
                    if (IsEquivalentForSubstitution(existing, argument))
                        continue;

                    // If we already have a concrete type and the new one is still a TP,
                    // keep the existing mapping.
                    if (existing is not ITypeParameterSymbol && argument is ITypeParameterSymbol)
                        continue;
                }

                substitution[parameter] = argument;
            }
        }

        return substitution;
    }

    private ConstructedNamedTypeSymbol(
        INamedTypeSymbol originalDefinition,
        ImmutableArray<ITypeSymbol> typeArguments,
        Dictionary<ITypeParameterSymbol, ITypeSymbol>? inheritedSubstitution,
        INamedTypeSymbol? containingTypeOverride)
    {
        ConstructedFrom = originalDefinition;
        _originalDefinition = originalDefinition;
        _explicitTypeArguments = typeArguments.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : typeArguments;
        _containingTypeOverride = containingTypeOverride;

        _substitutionMap = CreateSubstitutionMap(originalDefinition, typeArguments, inheritedSubstitution);
    }

    public ImmutableArray<ITypeSymbol> TypeArguments
    {
        get
        {
            if (_typeArgumentsState == TypeArgumentsComputationState.Computed)
                return _typeArguments;

            if (_typeArgumentsState == TypeArgumentsComputationState.Computing)
                return GetExplicitTypeArgumentsForInference();

            _typeArgumentsState = TypeArgumentsComputationState.Computing;
            try
            {
                _typeArguments = BuildTypeArguments();
                _typeArgumentsState = TypeArgumentsComputationState.Computed;
                return _typeArguments;
            }
            catch
            {
                _typeArgumentsState = TypeArgumentsComputationState.Uninitialized;
                _typeArguments = default;
                throw;
            }
        }
    }

    internal ImmutableArray<ITypeSymbol> GetExplicitTypeArgumentsForInference()
        => _explicitTypeArguments.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : _explicitTypeArguments;

    INamedTypeSymbol IConstructedTypeSubstitutionInfo.DefinitionForSubstitution => _originalDefinition;
    ImmutableArray<ITypeSymbol> IConstructedTypeSubstitutionInfo.ExplicitTypeArgumentsForSubstitution => GetExplicitTypeArgumentsForInference();

    private bool TryGetSubstitution(ITypeParameterSymbol parameter, out ITypeSymbol replacement)
    {
        var lookup = (ITypeParameterSymbol)(parameter.OriginalDefinition ?? parameter);
        if (_substitutionMap.TryGetValue(lookup, out replacement!))
            return true;

        foreach (var (key, value) in _substitutionMap)
        {
            var normalizedKey = (ITypeParameterSymbol)(key.OriginalDefinition ?? key);
            if (normalizedKey.Ordinal == lookup.Ordinal && normalizedKey.Name == lookup.Name)
            {
                replacement = value;
                return true;
            }
        }

        replacement = null!;
        return false;
    }

    public ITypeSymbol Substitute(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>? methodMap = null)
    {
        var inProgress = new HashSet<ITypeSymbol>(ReferenceEqualityComparer.Instance);
        var cache = new Dictionary<ITypeSymbol, ITypeSymbol>(ReferenceEqualityComparer.Instance);
        return ReanchorNestedTypeIfNeeded(SubstituteCore(type, methodMap, inProgress, cache), methodMap);
    }

    internal ITypeSymbol ReanchorNestedTypeIfNeeded(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>? methodMap = null)
    {
        if (type is INamedTypeSymbol { ContainingType: { } anchoredContainingType } &&
            ReferenceEquals(anchoredContainingType, this))
        {
            return type;
        }

        if (type is INamedTypeSymbol namedSelf &&
            ReferenceEquals(TypeSubstitution.GetDefinitionForSubstitution(namedSelf), _originalDefinition) &&
            HaveEquivalentExplicitTypeArguments(namedSelf))
        {
            return this;
        }

        if (type is RefTypeSymbol refType)
        {
            var rewrittenElement = ReanchorNestedTypeIfNeeded(refType.ElementType, methodMap);
            return SymbolEqualityComparer.Default.Equals(rewrittenElement, refType.ElementType)
                ? type
                : new RefTypeSymbol(rewrittenElement);
        }

        if (type is INamedTypeSymbol named &&
            named.ContainingType is INamedTypeSymbol containing &&
            ReferenceEquals(TypeSubstitution.GetDefinitionForSubstitution(containing), _originalDefinition))
        {
            var nestedDefinition = TypeSubstitution.GetDefinitionForSubstitution(named);
            var nestedArguments = GetShallowTypeArguments(named);
            var substitutedArguments = nestedArguments.IsDefaultOrEmpty
                ? ImmutableArray<ITypeSymbol>.Empty
                : ImmutableArray.CreateRange(nestedArguments.Select(argument => Substitute(argument, methodMap)));

            return new ConstructedNamedTypeSymbol(nestedDefinition, substitutedArguments, _substitutionMap, this);
        }

        return type;
    }

    private bool HaveEquivalentExplicitTypeArguments(INamedTypeSymbol candidate)
    {
        var candidateArguments = GetShallowTypeArguments(candidate);
        if (candidateArguments.Length != _explicitTypeArguments.Length)
            return false;

        for (var index = 0; index < candidateArguments.Length; index++)
        {
            if (!IsEquivalentForSubstitution(candidateArguments[index], _explicitTypeArguments[index]))
                return false;
        }

        return true;
    }


    private ITypeSymbol SubstituteCore(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>? methodMap,
        HashSet<ITypeSymbol> inProgress,
        Dictionary<ITypeSymbol, ITypeSymbol> cache)
    {
        if (cache.TryGetValue(type, out var cached))
            return cached;

        if (!inProgress.Add(type))
            return type;

        try
        {
            ITypeSymbol result = type;

            if (type is ITypeParameterSymbol tp)
            {
                if (methodMap is not null && methodMap.TryGetValue(tp, out var mappedMethodParameter))
                {
                    result = mappedMethodParameter;
                    cache[type] = result;
                    return result;
                }

                if (TryGetSubstitution(tp, out var concrete))
                {
                    result = concrete;
                    cache[type] = result;
                    return result;
                }
            }

            if (type is INamedTypeSymbol nestedType &&
                nestedType.ContainingType is INamedTypeSymbol nestedContainingType &&
                ReferenceEquals(TypeSubstitution.GetDefinitionForSubstitution(nestedContainingType), _originalDefinition))
            {
                var nestedArguments = GetShallowTypeArguments(nestedType);
                var substitutedNestedArguments = nestedArguments.IsDefaultOrEmpty
                    ? ImmutableArray<ITypeSymbol>.Empty
                    : ImmutableArray.CreateRange(nestedArguments.Select(argument => SubstituteCore(argument, methodMap, inProgress, cache)));

                var nestedDefinition = TypeSubstitution.GetDefinitionForSubstitution(nestedType);
                result = new ConstructedNamedTypeSymbol(nestedDefinition, substitutedNestedArguments, _substitutionMap, this);
                cache[type] = result;
                return result;
            }

            if (type is NullableTypeSymbol nullableTypeSymbol)
            {
                var underlyingType = SubstituteCore(nullableTypeSymbol.UnderlyingType, methodMap, inProgress, cache);

                if (!IsEquivalentForSubstitution(underlyingType, nullableTypeSymbol.UnderlyingType))
                    result = underlyingType.ApplySubstitutedNullability(nullableTypeSymbol);
                else
                    result = type;

                cache[type] = result;
                return result;
            }

            if (type is RefTypeSymbol refType)
            {
                var substitutedElement = SubstituteCore(refType.ElementType, methodMap, inProgress, cache);

                if (!IsEquivalentForSubstitution(substitutedElement, refType.ElementType))
                    result = new RefTypeSymbol(substitutedElement);
                else
                    result = type;

                cache[type] = result;
                return result;
            }

            if (type is IAddressTypeSymbol address)
            {
                var substitutedElement = SubstituteCore(address.ReferencedType, methodMap, inProgress, cache);

                if (!IsEquivalentForSubstitution(substitutedElement, address.ReferencedType))
                    result = new AddressTypeSymbol(substitutedElement);
                else
                    result = type;

                cache[type] = result;
                return result;
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                var substitutedElement = SubstituteCore(arrayType.ElementType, methodMap, inProgress, cache);

                if (!IsEquivalentForSubstitution(substitutedElement, arrayType.ElementType))
                    result = new ArrayTypeSymbol(arrayType.BaseType, substitutedElement, arrayType.ContainingSymbol, arrayType.ContainingType, arrayType.ContainingNamespace, [], arrayType.Rank, arrayType.FixedLength);
                else
                    result = type;

                cache[type] = result;
                return result;
            }

            if (type is ITupleTypeSymbol tupleType)
            {
                result = TypeSubstitution.SubstituteTupleElements(
                    tupleType,
                    element => SubstituteCore(element, methodMap, inProgress, cache));
                cache[type] = result;
                return result;
            }

            if (type is INamedTypeSymbol named && named.IsGenericType && !named.IsUnboundGenericType)
            {
                var typeArguments = GetShallowTypeArguments(named);
                var substitutedArgs = new ITypeSymbol[typeArguments.Length];
                var changed = false;

                for (int i = 0; i < typeArguments.Length; i++)
                {
                    var originalArg = typeArguments[i];
                    var substitutedArg = SubstituteCore(originalArg, methodMap, inProgress, cache);

                    substitutedArgs[i] = substitutedArg;

                    if (!IsEquivalentForSubstitution(substitutedArg, originalArg))
                        changed = true;
                }

                if ((named.ConstructedFrom ?? named).SpecialType == SpecialType.System_Nullable_T &&
                    substitutedArgs.Length == 1)
                {
                    result = substitutedArgs[0].GetNullableType();
                    cache[type] = result;
                    return result;
                }

                if (!changed)
                {
                    // Even if generic args did not change, we may still need to re-anchor a nested type
                    // under a substituted containing type.
                    if (named.ContainingType is INamedTypeSymbol && TryGetContainingOverride(named, out var containingOverride) && containingOverride is not null)
                    {
                        if (named is ConstructedNamedTypeSymbol existingConstructed &&
                            existingConstructed.ContainingType is INamedTypeSymbol existingContaining &&
                            AreNamedTypesEquivalentShallow(existingContaining, containingOverride))
                        {
                            result = named;
                        }
                        else
                        {
                            result = new ConstructedNamedTypeSymbol((INamedTypeSymbol?)(named.ConstructedFrom ?? named) ?? named, typeArguments, _substitutionMap, containingOverride);
                        }
                    }
                    else
                    {
                        result = named;
                    }

                    cache[type] = result;
                    return result;
                }

                // Avoid reusing a possibly already-constructed named
                var constructedFrom = (INamedTypeSymbol?)named.ConstructedFrom ?? named;

                // If this is nested and the containing type must be substituted, create a constructed wrapper
                // so ContainingType/ContainingSymbol reflect the constructed outer.
                if (named.ContainingType is INamedTypeSymbol && TryGetContainingOverride(named, out var overrideContaining) && overrideContaining is not null)
                {
                    var immutableArguments = ImmutableArray.Create(substitutedArgs);
                    if (named is ConstructedNamedTypeSymbol existingConstructed &&
                        existingConstructed.ContainingType is INamedTypeSymbol existingContaining &&
                        AreNamedTypesEquivalentShallow(existingContaining, overrideContaining))
                    {
                        result = named;
                    }
                    else
                    {
                        result = new ConstructedNamedTypeSymbol(constructedFrom, immutableArguments, _substitutionMap, overrideContaining);
                    }

                    cache[type] = result;
                    return result;
                }

                result = constructedFrom.Construct(substitutedArgs);
                cache[type] = result;
                return result;
            }

            // Nested non-generic named types (e.g. Result<T,E>.Ok) must still be re-anchored under a substituted containing type.
            if (type is INamedTypeSymbol nestedNamed && nestedNamed.ContainingType is INamedTypeSymbol)
            {
                if (TryGetContainingOverride(nestedNamed, out var containingOverride) && containingOverride is not null)
                {
                    if (nestedNamed is ConstructedNamedTypeSymbol existingConstructed &&
                        existingConstructed.ContainingType is INamedTypeSymbol existingContaining &&
                        AreNamedTypesEquivalentShallow(existingContaining, containingOverride))
                    {
                        result = nestedNamed;
                    }
                    else
                    {
                        result = new ConstructedNamedTypeSymbol(nestedNamed, ImmutableArray<ITypeSymbol>.Empty, _substitutionMap, containingOverride);
                    }

                    cache[type] = result;
                    return result;
                }
            }

            cache[type] = result;
            return result;
        }
        finally
        {
            inProgress.Remove(type);
        }
    }

    private static bool IsEquivalentForSubstitution(ITypeSymbol left, ITypeSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is ITypeParameterSymbol leftParameter && right is ITypeParameterSymbol rightParameter)
        {
            var leftDefinition = (ITypeParameterSymbol)(leftParameter.OriginalDefinition ?? leftParameter);
            var rightDefinition = (ITypeParameterSymbol)(rightParameter.OriginalDefinition ?? rightParameter);
            if (ReferenceEquals(leftDefinition, rightDefinition))
                return true;

            if (leftDefinition.OwnerKind != rightDefinition.OwnerKind)
                return false;

            if (leftDefinition.Ordinal != rightDefinition.Ordinal)
                return false;

            if (!string.Equals(leftDefinition.Name, rightDefinition.Name, StringComparison.Ordinal))
                return false;

            if (leftDefinition.OwnerKind == TypeParameterOwnerKind.Method)
            {
                var leftOwner = (IMethodSymbol?)(leftDefinition.DeclaringMethodParameterOwner?.OriginalDefinition ?? leftDefinition.DeclaringMethodParameterOwner);
                var rightOwner = (IMethodSymbol?)(rightDefinition.DeclaringMethodParameterOwner?.OriginalDefinition ?? rightDefinition.DeclaringMethodParameterOwner);
                return SymbolEqualityComparer.Default.Equals(leftOwner, rightOwner);
            }

            var leftTypeOwner = (INamedTypeSymbol?)(leftDefinition.DeclaringTypeParameterOwner?.OriginalDefinition ?? leftDefinition.DeclaringTypeParameterOwner);
            var rightTypeOwner = (INamedTypeSymbol?)(rightDefinition.DeclaringTypeParameterOwner?.OriginalDefinition ?? rightDefinition.DeclaringTypeParameterOwner);
            return SymbolEqualityComparer.Default.Equals(leftTypeOwner, rightTypeOwner);
        }

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            var leftDefinition = (INamedTypeSymbol)(leftNamed.OriginalDefinition ?? leftNamed);
            var rightDefinition = (INamedTypeSymbol)(rightNamed.OriginalDefinition ?? rightNamed);

            // Definitions must match.
            if (!string.Equals(leftDefinition.MetadataName, rightDefinition.MetadataName, StringComparison.Ordinal))
                return false;

            if (leftDefinition.Arity != rightDefinition.Arity)
                return false;

            // For non-generic types, or when no type arguments are present, definition+arity is enough.
            if (!leftNamed.IsGenericType || leftNamed.IsUnboundGenericType || leftDefinition.Arity == 0)
                return true;

            // Compare shallow type arguments. This is required for correct change detection during substitution
            // (e.g. IEnumerable<KeyValuePair<TKey,TValue>> must become IEnumerable<KeyValuePair<string,int>>).
            var leftArgs = GetShallowTypeArguments(leftNamed);
            var rightArgs = GetShallowTypeArguments(rightNamed);

            if (leftArgs.IsDefaultOrEmpty || rightArgs.IsDefaultOrEmpty)
                return leftArgs.Length == rightArgs.Length;

            if (leftArgs.Length != rightArgs.Length)
                return false;

            for (var i = 0; i < leftArgs.Length; i++)
            {
                if (!IsEquivalentForSubstitution(leftArgs[i], rightArgs[i]))
                    return false;
            }

            return true;
        }

        return false;
    }

    private static bool AreNamedTypesEquivalentShallow(INamedTypeSymbol left, INamedTypeSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        var leftDefinition = (INamedTypeSymbol)(left.OriginalDefinition ?? left);
        var rightDefinition = (INamedTypeSymbol)(right.OriginalDefinition ?? right);

        if (!ReferenceEquals(leftDefinition, rightDefinition))
            return false;

        if (left.Arity != right.Arity)
            return false;

        var leftArgs = GetShallowTypeArguments(left);
        var rightArgs = GetShallowTypeArguments(right);

        if (leftArgs.IsDefaultOrEmpty || rightArgs.IsDefaultOrEmpty)
            return leftArgs.Length == rightArgs.Length;

        if (leftArgs.Length != rightArgs.Length)
            return false;

        for (var i = 0; i < leftArgs.Length; i++)
        {
            if (!ReferenceEquals(leftArgs[i], rightArgs[i]))
                return false;
        }

        return true;
    }

    private static ImmutableArray<ITypeSymbol> GetShallowTypeArguments(INamedTypeSymbol type)
    {
        return TypeSubstitution.GetShallowTypeArguments(type);
    }

    internal static INamedTypeSymbol ReanchorNested(
        INamedTypeSymbol nestedDefinition,
        INamedTypeSymbol containingOverride,
        Dictionary<ITypeParameterSymbol, ITypeSymbol>? inheritedSubstitution,
        ImmutableArray<ITypeSymbol> typeArguments)
    {
        return new ConstructedNamedTypeSymbol(nestedDefinition, typeArguments, inheritedSubstitution, containingOverride);
    }

    private ImmutableArray<INamedTypeSymbol> BuildSubstitutedInterfaceSet(ImmutableArray<INamedTypeSymbol> interfaces)
    {
        if (interfaces.IsDefaultOrEmpty || interfaces.Length == 0)
            return interfaces;

        var cache = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var visiting = new HashSet<INamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>(interfaces.Length);

        foreach (var interfaceType in interfaces)
        {
            var result = SubstituteInterfaceType(interfaceType, cache, visiting);
            builder.Add(result);
        }

        return builder.MoveToImmutable();
    }

    private INamedTypeSymbol SubstituteInterfaceType(
        INamedTypeSymbol interfaceType,
        Dictionary<INamedTypeSymbol, INamedTypeSymbol> cache,
        HashSet<INamedTypeSymbol> visiting)
    {
        if (cache.TryGetValue(interfaceType, out var cached))
        {
            return cached;
        }

        if (!visiting.Add(interfaceType))
        {
            return interfaceType;
        }

        try
        {
            if (!interfaceType.IsGenericType || interfaceType.IsUnboundGenericType)
            {
                cache[interfaceType] = interfaceType;
                return interfaceType;
            }

            var originalArguments = interfaceType.TypeArguments;
            var substitutedArguments = new ITypeSymbol[originalArguments.Length];
            var changed = false;

            for (var i = 0; i < originalArguments.Length; i++)
            {
                var original = originalArguments[i];
                var substituted = SubstituteInterfaceTypeArgument(original, cache, visiting);
                substitutedArguments[i] = substituted;

                if (!IsEquivalentForSubstitution(substituted, original))
                    changed = true;
            }

            if (!changed)
            {
                cache[interfaceType] = interfaceType;
                return interfaceType;
            }

            var definition = (INamedTypeSymbol?)(interfaceType.ConstructedFrom ?? interfaceType) ?? interfaceType;
            var constructed = (INamedTypeSymbol)definition.Construct(substitutedArguments);
            cache[interfaceType] = constructed;
            return constructed;
        }
        finally
        {
            visiting.Remove(interfaceType);
        }
    }

    private ITypeSymbol SubstituteInterfaceTypeArgument(
        ITypeSymbol argument,
        Dictionary<INamedTypeSymbol, INamedTypeSymbol> cache,
        HashSet<INamedTypeSymbol> visiting)
    {
        if (argument is ITypeParameterSymbol typeParameter && TryGetSubstitution(typeParameter, out var replacement))
            return replacement;

        if (argument is INamedTypeSymbol namedArg)
        {
            // Non-generic named types (e.g. string, int) contain no type parameters —
            // return unchanged.  For generic named types (e.g. KeyValuePair<TKey, TValue>)
            // we must recurse so that inner type parameters get substituted as well.
            if (!namedArg.IsGenericType || namedArg.TypeArguments.IsDefaultOrEmpty)
                return argument;

            return SubstituteInterfaceType(namedArg, cache, visiting);
        }

        if (argument is NullableTypeSymbol nullableArgument)
        {
            var underlying = SubstituteInterfaceTypeArgument(nullableArgument.UnderlyingType, cache, visiting);
            return IsEquivalentForSubstitution(underlying, nullableArgument.UnderlyingType)
                ? argument
                : underlying.ApplySubstitutedNullability(nullableArgument);
        }

        if (argument is IArrayTypeSymbol arrayArgument)
        {
            var element = SubstituteInterfaceTypeArgument(arrayArgument.ElementType, cache, visiting);
            if (IsEquivalentForSubstitution(element, arrayArgument.ElementType))
                return argument;

            return new ArrayTypeSymbol(
                arrayArgument.BaseType,
                element,
                arrayArgument.ContainingSymbol,
                arrayArgument.ContainingType,
                arrayArgument.ContainingNamespace,
                [],
                arrayArgument.Rank,
                arrayArgument.FixedLength);
        }

        if (argument is RefTypeSymbol refTypeArgument)
        {
            var element = SubstituteInterfaceTypeArgument(refTypeArgument.ElementType, cache, visiting);
            return IsEquivalentForSubstitution(element, refTypeArgument.ElementType)
                ? argument
                : new RefTypeSymbol(element);
        }

        if (argument is IAddressTypeSymbol addressArgument)
        {
            var element = SubstituteInterfaceTypeArgument(addressArgument.ReferencedType, cache, visiting);
            return IsEquivalentForSubstitution(element, addressArgument.ReferencedType)
                ? argument
                : new AddressTypeSymbol(element);
        }

        return argument;
    }

    public ImmutableArray<ISymbol> GetMembers()
    {
        var substitutedMembers = _originalDefinition.GetMembers().Select(SubstituteMember).ToImmutableArray();

        if (ShouldCacheMutableSourceUnionState())
            return substitutedMembers;

        return _members ??= substitutedMembers;
    }

    public ImmutableArray<ISymbol> GetMembers(string name)
    {
        if (string.IsNullOrEmpty(name))
            return GetMembers();

        if (ShouldCacheMutableSourceUnionState())
            return BuildMembersByName(name);

        return _membersByName.GetOrAdd(name, BuildMembersByName);

        ImmutableArray<ISymbol> BuildMembersByName(string memberName)
        {
            return _originalDefinition.GetMembers(memberName)
                .Select(SubstituteMember)
                .Where(member => string.Equals(member.Name, memberName, StringComparison.Ordinal))
                .ToImmutableArray();
        }
    }

    internal ImmutableArray<ITypeSymbol> GetAllTypeArguments() =>
        _allTypeArguments.IsDefault ? _allTypeArguments = BuildAllTypeArguments() : _allTypeArguments;

    private ImmutableArray<ITypeSymbol> BuildAllTypeArguments()
    {
        var selfArgs = TypeArguments;

        // Union cases are semantically nested in their union, but their CLR type is nested
        // in either the non-generic union or a non-generic companion. The semantic owner's
        // type arguments must therefore never be inherited into the case's runtime identity.
        if (TryGetCaseDefinition(out _))
            return selfArgs;

        if (_containingTypeOverride is ConstructedNamedTypeSymbol constructedContaining)
        {
            var outerArgs = constructedContaining.GetAllTypeArguments();
            if (selfArgs.IsDefaultOrEmpty || selfArgs.Length == 0)
                return outerArgs;

            return outerArgs.AddRange(selfArgs);
        }

        return selfArgs;
    }

    private ImmutableArray<ITypeSymbol> BuildTypeArguments()
    {
        // The constructor receives explicit type arguments for this constructed symbol.
        // Re-substituting them here can apply the same mapping twice (for example T[] -> T[][])
        // when the argument was already produced through substitution.
        return _explicitTypeArguments.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : _explicitTypeArguments;
    }

    private ImmutableArray<ITypeParameterSymbol> BuildTypeParameters()
    {
        // Nested types do not implicitly inherit containing type parameters, but
        // their own constraints can mention parameters from a constructed outer
        // type. Keep definition ownership while projecting those constraint uses.
        var originalParameters = _originalDefinition.TypeParameters;
        if (originalParameters.IsDefaultOrEmpty)
            return originalParameters;

        ImmutableArray<ITypeParameterSymbol>.Builder? builder = null;
        for (var i = 0; i < originalParameters.Length; i++)
        {
            var parameter = originalParameters[i];
            var constraints = parameter.ConstraintTypes;
            if (constraints.IsDefaultOrEmpty)
            {
                builder?.Add(parameter);
                continue;
            }

            var substitutedConstraints = ImmutableArray.CreateRange(
                constraints.Select(constraint => Substitute(constraint)));
            var changed = false;
            for (var constraintIndex = 0; constraintIndex < constraints.Length; constraintIndex++)
            {
                if (!SymbolEqualityComparer.Default.Equals(constraints[constraintIndex], substitutedConstraints[constraintIndex]))
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                builder?.Add(parameter);
                continue;
            }

            if (builder is null)
            {
                builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(originalParameters.Length);
                for (var previous = 0; previous < i; previous++)
                    builder.Add(originalParameters[previous]);
            }

            builder.Add(new SubstitutedNamedTypeParameterSymbol(parameter, substitutedConstraints));
        }

        return builder?.MoveToImmutable() ?? originalParameters;
    }

    // Helper methods for chain-aware substitution of nested types
    private INamedTypeSymbol? SubstituteContainingType(INamedTypeSymbol? containing)
    {
        if (containing is null)
            return null;

        // Substitute the containing type using the current substitution map.
        // This will walk outward (recursively) and produce the constructed outer chain when needed.
        var substituted = Substitute(containing) as INamedTypeSymbol;

        if (substituted is null)
            return containing;

        return substituted;
    }

    private bool TryGetContainingOverride(INamedTypeSymbol namedType, out INamedTypeSymbol? containingOverride)
    {
        containingOverride = null;

        if (namedType.ContainingType is not INamedTypeSymbol containing)
            return false;

        var substitutedContaining = SubstituteContainingType(containing);

        if (substitutedContaining is null)
            return false;

        // If the containing type changed due to substitution (or the symbol is logically the same but a different instance),
        // we must re-anchor the nested type under the substituted containing type.
        if (!AreNamedTypesEquivalentShallow(substitutedContaining, containing))
        {
            containingOverride = substitutedContaining;
            return true;
        }

        var containingDefinition = TypeSubstitution.GetDefinitionForSubstitution(containing);
        if (ReferenceEquals(containingDefinition, _originalDefinition))
        {
            containingOverride = this;
            return true;
        }

        // Also re-anchor if the nested type is directly under the original definition and we are the constructed instance.
        if (_containingTypeOverride is null && AreNamedTypesEquivalentShallow(containing, _originalDefinition))
        {
            containingOverride = this;
            return true;
        }

        return false;
    }

    private ISymbol SubstituteMember(ISymbol member) => member switch
    {
        IMethodSymbol m => new SubstitutedMethodSymbol(m, this),
        IFieldSymbol f => new SubstitutedFieldSymbol(f, this),
        IPropertySymbol p => new SubstitutedPropertySymbol(p, this),
        IEventSymbol e => new SubstitutedEventSymbol(e, this),
        INamedTypeSymbol t => SubstituteNamedType(t),
        _ => member
    };

    private INamedTypeSymbol SubstituteNamedType(INamedTypeSymbol namedType)
    {
        // Determine whether this type must be re-anchored under a substituted containing type.
        INamedTypeSymbol? containingOverride = null;
        _ = TryGetContainingOverride(namedType, out containingOverride);

        // Non-generic nested types (arity 0) still require a constructed wrapper when the containing type changes.
        if (namedType.Arity == 0)
        {
            return containingOverride is not null
                ? new ConstructedNamedTypeSymbol(namedType, ImmutableArray<ITypeSymbol>.Empty, _substitutionMap, containingOverride)
                : namedType;
        }

        var typeParameters = namedType.TypeParameters;
        if (typeParameters.Length == 0)
        {
            return containingOverride is not null
                ? new ConstructedNamedTypeSymbol(namedType, ImmutableArray<ITypeSymbol>.Empty, _substitutionMap, containingOverride)
                : namedType;
        }

        // Compatibility bridge: until all construction/binding paths are fully case-generic-first,
        // allow legacy projection of case parameters from substituted union arguments, but only when
        // the case's generic parameter names are coupled to the union's generic parameter names.
        ImmutableArray<ITypeParameterSymbol> unionTypeParameters = ImmutableArray<ITypeParameterSymbol>.Empty;
        ImmutableArray<ITypeSymbol> unionTypeArguments = ImmutableArray<ITypeSymbol>.Empty;
        IUnionCaseTypeSymbol? unionCaseSymbol = null;
        if (namedType is IUnionCaseTypeSymbol caseSymbol)
        {
            unionCaseSymbol = caseSymbol;
            var unionDefinition = TypeSubstitution.GetDefinitionForSubstitution((INamedTypeSymbol)caseSymbol.Union);
            var substitutedUnion = SubstituteNamedType(unionDefinition);
            unionTypeParameters = unionDefinition.TypeParameters;
            unionTypeArguments = substitutedUnion.TypeArguments;
        }

        var typeArguments = new ITypeSymbol[typeParameters.Length];
        var changed = false;

        for (var i = 0; i < typeParameters.Length; i++)
        {
            var parameter = typeParameters[i];
            if (TryGetSubstitution(parameter, out var replacement))
            {
                typeArguments[i] = replacement;
                if (!IsEquivalentForSubstitution(replacement, parameter))
                    changed = true;
            }
            else if (unionCaseSymbol is not null &&
                UnionFacts.TryProjectCaseTypeParameterFromUnionArguments(
                    unionCaseSymbol,
                    parameter,
                    unionTypeParameters,
                    unionTypeArguments,
                    out var projectedType))
            {
                typeArguments[i] = projectedType;
                if (!IsEquivalentForSubstitution(projectedType, parameter))
                    changed = true;
            }
            else
            {
                typeArguments[i] = parameter;
            }
        }

        // If nothing changed and there is no containing override, reuse the existing symbol.
        if (!changed && containingOverride is null)
            return namedType;

        var immutableArguments = ImmutableArray.Create(typeArguments);

        if (containingOverride is null)
            return (INamedTypeSymbol)namedType.Construct(typeArguments);

        return new ConstructedNamedTypeSymbol(namedType, immutableArguments, _substitutionMap, containingOverride);
    }

    private bool TryGetUnionDefinition(out IUnionSymbol unionDefinition)
    {
        if (_originalDefinition is IUnionSymbol union)
        {
            unionDefinition = union;
            return true;
        }

        if (ConstructedFrom is IUnionSymbol constructedUnion)
        {
            unionDefinition = constructedUnion;
            return true;
        }

        unionDefinition = null!;
        return false;
    }

    private bool TryGetCaseDefinition(out IUnionCaseTypeSymbol caseDefinition)
    {
        if (_originalDefinition is IUnionCaseTypeSymbol caseSymbol)
        {
            caseDefinition = caseSymbol;
            return true;
        }

        if (ConstructedFrom is IUnionCaseTypeSymbol constructedCase)
        {
            caseDefinition = constructedCase;
            return true;
        }

        caseDefinition = null!;
        return false;
    }

    // Symbol metadata forwarding
    public string Name => _originalDefinition.Name;
    public string MetadataName => _originalDefinition.MetadataName;
    public SymbolKind Kind => _originalDefinition.Kind;
    public TypeKind TypeKind
    {
        get
        {
            if (ConstructedFrom is INamedTypeSymbol constructedFrom)
                return constructedFrom.TypeKind;

            return _originalDefinition.TypeKind;
        }
    }
    public SpecialType SpecialType => _originalDefinition.SpecialType;
    public bool IsNamespace => false;
    public bool IsType => true;
    public bool IsReferenceType => _originalDefinition.IsReferenceType;
    public bool IsValueType => _originalDefinition.IsValueType;
    public bool IsUnion => _originalDefinition.IsUnion;
    public bool IsUnionCase => _originalDefinition.IsUnionCase;
    public INamedTypeSymbol? UnderlyingUnionType => _originalDefinition.UnderlyingUnionType;
    public INamedTypeSymbol MetadataContainingType =>
        TryGetCaseDefinition(out var caseDefinition)
            ? caseDefinition.MetadataContainingType
            : _originalDefinition;
    public INamedTypeSymbol? ContainingType => _containingTypeOverride ?? _originalDefinition.ContainingType;
    public INamespaceSymbol? ContainingNamespace => _containingTypeOverride?.ContainingNamespace ?? _originalDefinition.ContainingNamespace;
    public ISymbol? ContainingSymbol => _containingTypeOverride ?? _originalDefinition.ContainingSymbol;
    public IAssemblySymbol? ContainingAssembly => _originalDefinition.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _originalDefinition.ContainingModule;
    public Accessibility DeclaredAccessibility => _originalDefinition.DeclaredAccessibility;
    public bool IsStatic => _originalDefinition.IsStatic;
    public bool IsImplicitlyDeclared => true;
    public bool CanBeReferencedByName => true;
    public ImmutableArray<Location> Locations => _originalDefinition.Locations;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _originalDefinition.DeclaringSyntaxReferences;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _originalDefinition.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _originalDefinition.GetDocumentationComment();
    public int Arity => _originalDefinition.Arity;
    public ImmutableArray<ITypeSymbol> GetTypeArguments() => TypeArguments;
    public ITypeSymbol? OriginalDefinition => _originalDefinition;
    public INamedTypeSymbol? BaseType => _baseType ??= _originalDefinition.BaseType is { } baseType
        ? Substitute(baseType) as INamedTypeSymbol
        : null;
    public ImmutableArray<ITypeParameterSymbol> TypeParameters =>
        _typeParameters.IsDefault ? _typeParameters = BuildTypeParameters() : _typeParameters;
    public ITypeSymbol? ConstructedFrom { get; }
    public bool IsAbstract => _originalDefinition.IsAbstract;
    public bool IsClosed => _originalDefinition.IsClosed;
    public bool IsRefLikeType => _originalDefinition.IsRefLikeType;
    public bool IsReadOnly => _originalDefinition.IsReadOnly;
    public bool IsGenericType => _originalDefinition.IsGenericType;
    public bool IsUnboundGenericType => false;
    public ImmutableArray<INamedTypeSymbol> Interfaces =>
 _interfaces ??= BuildSubstitutedInterfaceSet(_originalDefinition.Interfaces);
    public ImmutableArray<INamedTypeSymbol> AllInterfaces =>
       _allInterfaces ??= BuildSubstitutedInterfaceSet(_originalDefinition.AllInterfaces);
    public ImmutableArray<ITypeSymbol> Variants
    {
        get
        {
            if (!TryGetUnionDefinition(out var unionDefinition))
                return ImmutableArray<ITypeSymbol>.Empty;

            if (!ShouldCacheMutableSourceUnionState() && _variants is not null)
                return _variants.Value;

            var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(unionDefinition.Variants.Length);
            foreach (var variant in unionDefinition.Variants)
                builder.Add(Substitute(variant));

            var substitutedVariants = builder.MoveToImmutable();
            if (ShouldCacheMutableSourceUnionState())
                return substitutedVariants;

            _variants = substitutedVariants;
            return _variants.Value;
        }
    }

    public ImmutableArray<IUnionCaseTypeSymbol> DeclaredCaseTypes
    {
        get
        {
            if (!TryGetUnionDefinition(out var unionDefinition))
                return ImmutableArray<IUnionCaseTypeSymbol>.Empty;

            var substitutedCases = SubstituteUnionCases(unionDefinition.DeclaredCaseTypes);

            if (ShouldCacheMutableSourceUnionState())
                return substitutedCases;

            return _declaredCases ??= substitutedCases;
        }
    }
    public ImmutableArray<ITypeSymbol> MemberTypes
    {
        get
        {
            if (!TryGetUnionDefinition(out var unionDefinition))
                return ImmutableArray<ITypeSymbol>.Empty;

            if (!ShouldCacheMutableSourceUnionState() && _memberTypes is not null)
                return _memberTypes.Value;

            var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(unionDefinition.MemberTypes.Length);
            foreach (var memberType in unionDefinition.MemberTypes)
                builder.Add(Substitute(memberType));

            var substitutedMembers = builder.MoveToImmutable();
            if (ShouldCacheMutableSourceUnionState())
                return substitutedMembers;

            _memberTypes = substitutedMembers;
            return _memberTypes.Value;
        }
    }
    public bool ContentMayBeNull
    {
        get
        {
            if (!TryGetUnionDefinition(out var unionDefinition))
                return MemberTypes.Any(UnionContentNullability.IsNullableContentType);

            return unionDefinition.ContentMayBeNull ||
                   MemberTypes.Any(UnionContentNullability.IsNullableContentType);
        }
    }

    public IFieldSymbol DiscriminatorField
    {
        get
        {
            if (_discriminatorField is not null)
                return _discriminatorField;

            if (!TryGetUnionDefinition(out var unionDefinition))
                throw new InvalidOperationException("Constructed type is not a discriminated union.");

            _discriminatorField = new SubstitutedFieldSymbol(unionDefinition.DiscriminatorField, this);
            return _discriminatorField;
        }
    }

    private bool ShouldCacheMutableSourceUnionState()
        => _originalDefinition is SourceUnionSymbol or SourceUnionCaseTypeSymbol;
    public IFieldSymbol PayloadField
    {
        get
        {
            if (_payloadField is not null)
                return _payloadField;

            if (!TryGetUnionDefinition(out var unionDefinition))
                throw new InvalidOperationException("Constructed type is not a discriminated union.");

            _payloadField = new SubstitutedFieldSymbol(unionDefinition.PayloadField, this);
            return _payloadField;
        }
    }
    public ImmutableArray<IMethodSymbol> Constructors => GetMembers().OfType<IMethodSymbol>().Where(x => !x.IsStatic && x.IsConstructor).ToImmutableArray();
    public ImmutableArray<IMethodSymbol> InstanceConstructors => Constructors;
    public IMethodSymbol? StaticConstructor => GetMembers().OfType<IMethodSymbol>().FirstOrDefault(x => x.MethodKind == MethodKind.StaticConstructor);

    public IUnionSymbol Union
    {
        get
        {
            if (_union is not null)
                return _union;

            if (ContainingType is IUnionSymbol containingUnion)
                return _union = containingUnion;

            if (!TryGetCaseDefinition(out var caseDefinition))
                throw new InvalidOperationException("Constructed type is not a discriminated union case.");

            return _union = (IUnionSymbol)SubstituteNamedType((INamedTypeSymbol)caseDefinition.Union);
        }
    }

    public ImmutableArray<IParameterSymbol> ConstructorParameters
    {
        get
        {
            if (!TryGetCaseDefinition(out var caseDefinition))
                return ImmutableArray<IParameterSymbol>.Empty;

            return _constructorParameters ??= caseDefinition.ConstructorParameters
                .Select(p => (IParameterSymbol)new SubstitutedParameterSymbol(p, this))
                .ToImmutableArray();
        }
    }

    public int Ordinal => TryGetCaseDefinition(out var caseDefinition) ? caseDefinition.Ordinal : 0;

    public INamedTypeSymbol? UnderlyingTupleType
    {
        get
        {
            var underlying = _originalDefinition.UnderlyingTupleType;
            if (underlying is null)
                return null;

            if (AreNamedTypesEquivalentShallow(underlying, _originalDefinition))
                return this;

            return SubstituteNamedType(underlying);
        }
    }

    public ImmutableArray<IFieldSymbol> TupleElements =>
        _tupleElements ??= _originalDefinition.TupleElements
            .Select(member => SubstituteMember(member))
            .OfType<IFieldSymbol>()
            .ToImmutableArray();

    private ImmutableArray<IUnionCaseTypeSymbol> SubstituteUnionCases(ImmutableArray<IUnionCaseTypeSymbol> cases)
    {
        if (cases.IsDefaultOrEmpty || cases.Length == 0)
            return cases;

        var builder = ImmutableArray.CreateBuilder<IUnionCaseTypeSymbol>(cases.Length);
        foreach (var caseSymbol in cases)
            builder.Add((IUnionCaseTypeSymbol)SubstituteNamedType((INamedTypeSymbol)caseSymbol));

        return builder.MoveToImmutable();
    }

    public void Accept(SymbolVisitor visitor) => visitor.VisitNamedType(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.VisitNamedType(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);
    public ITypeSymbol Construct(params ITypeSymbol[] typeArguments)
    {
        if (_containingTypeOverride is null)
            return _originalDefinition.Construct(typeArguments);

        var immutableArguments = ImmutableArray.Create(typeArguments);
        return new ConstructedNamedTypeSymbol(_originalDefinition, immutableArguments, _substitutionMap, _containingTypeOverride);
    }

    public ITypeSymbol? LookupType(string name)
    {
        return TypeLookupUtilities.SelectBestTypeByName(
            _originalDefinition.GetMembers(name)
                .OfType<INamedTypeSymbol>()
                .Select(SubstituteNamedType)
                .Cast<ITypeSymbol>());
    }

    public string ToFullyQualifiedMetadataName()
    {
        var segments = new Stack<string>();

        for (INamedTypeSymbol? current = this; current is not null; current = GetMetadataContainingType(current))
        {
            var definition = (current as ConstructedNamedTypeSymbol)?._originalDefinition ?? current;
            var metadataName = definition.Name;
            var arity = definition.Arity;

            if (arity > 0)
                metadataName = $"{metadataName}`{arity}";

            segments.Push(metadataName);
        }

        static INamedTypeSymbol? GetMetadataContainingType(INamedTypeSymbol type)
            => type is IUnionCaseTypeSymbol { IsUnionCase: true } unionCase
                ? unionCase.MetadataContainingType
                : type.ContainingType;

        var typeName = segments.Count > 0
            ? string.Join("+", segments)
            : MetadataName;

        var containingNamespace = ContainingNamespace;

        if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
            return typeName;

        var namespaceName = containingNamespace.ToMetadataName();

        return string.IsNullOrEmpty(namespaceName)
            ? typeName
            : $"{namespaceName}.{typeName}";
    }

    public bool IsMemberDefined(string name, out ISymbol? symbol)
    {
        foreach (var member in _originalDefinition.GetMembers(name))
        {
            if (member.Name == name)
            {
                symbol = SubstituteMember(member);
                return true;
            }
        }

        symbol = null;
        return false;
    }

    internal System.Reflection.TypeInfo GetTypeInfo(CodeGenerator codeGen)
    {
        var runtimeArguments = GetAllTypeArguments();

        if (_originalDefinition is PENamedTypeSymbol pen)
        {
            var genericTypeDef = TypeSymbolExtensionsForCodeGen.GetClrType(pen, codeGen);
            if (runtimeArguments.IsDefaultOrEmpty)
                return genericTypeDef.GetTypeInfo();

            var resolved = runtimeArguments
                .Select(arg => ResolveRuntimeTypeArgument(arg, codeGen))
                .ToArray();
            return genericTypeDef.MakeGenericType(resolved).GetTypeInfo();
        }

        if (_originalDefinition is SourceNamedTypeSymbol source)
        {
            var definitionType = codeGen.GetTypeBuilder(source) ?? throw new InvalidOperationException("Missing type builder for generic definition.");
            if (source.IsExtensionDeclaration)
                return definitionType.GetTypeInfo();
            if (runtimeArguments.IsDefaultOrEmpty)
                return definitionType.GetTypeInfo();

            var runtimeArgs = runtimeArguments
                .Select(arg => ResolveRuntimeTypeArgument(arg, codeGen))
                .ToArray();
            try
            {
                var constructed = definitionType.MakeGenericType(runtimeArgs);
                return constructed.GetTypeInfo();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Unable to construct runtime type '{source.ToFullyQualifiedMetadataName()}' " +
                    $"from builder '{definitionType}' with {runtimeArgs.Length} type argument(s).",
                    exception);
            }
        }

        throw new InvalidOperationException("ConstructedNamedTypeSymbol is not based on a supported symbol type.");
    }

    private Type ResolveRuntimeTypeArgument(ITypeSymbol typeArgument, CodeGenerator codeGen)
    {
        if (typeArgument is ITypeParameterSymbol { OwnerKind: TypeParameterOwnerKind.Method } methodTypeParameter &&
            methodTypeParameter.DeclaringMethodParameterOwner is IMethodSymbol methodSymbol)
        {
            if (codeGen.TryResolveRuntimeTypeParameter(methodTypeParameter, RuntimeTypeUsage.MethodBody, out var methodBodyResolved))
                return methodBodyResolved;

            if (TryGetMethodGenericParameter(methodSymbol, methodTypeParameter.Ordinal, codeGen, out var methodParameter))
                return methodParameter;

            if (codeGen.TryGetRuntimeTypeForTypeParameter(methodTypeParameter, out var resolved))
            {
                if (IsMethodGenericParameter(resolved))
                    return resolved;

                if (TryGetMethodGenericParameter(methodSymbol, methodTypeParameter.Ordinal, codeGen, out var refreshedMethodParameter))
                {
                    codeGen.CacheRuntimeTypeParameter(methodTypeParameter, refreshedMethodParameter);
                    return refreshedMethodParameter;
                }

                if (_originalDefinition is SynthesizedAsyncStateMachineTypeSymbol stateMachine &&
                    TryGetMethodGenericParameter(stateMachine.AsyncMethod, methodTypeParameter.Ordinal, codeGen, out var asyncMethodParameter))
                {
                    codeGen.CacheRuntimeTypeParameter(methodTypeParameter, asyncMethodParameter);
                    return asyncMethodParameter;
                }

                return resolved;
            }

            if (_originalDefinition is SynthesizedAsyncStateMachineTypeSymbol fallbackStateMachine &&
                TryGetMethodGenericParameter(fallbackStateMachine.AsyncMethod, methodTypeParameter.Ordinal, codeGen, out var mappedFallback))
            {
                codeGen.CacheRuntimeTypeParameter(methodTypeParameter, mappedFallback);
                return mappedFallback;
            }
        }

        if (typeArgument is ITypeParameterSymbol typeParameter)
        {
            if (codeGen.TryResolveRuntimeTypeParameter(typeParameter, RuntimeTypeUsage.MethodBody, out var methodBodyResolved))
                return methodBodyResolved;

            if (codeGen.TryGetRuntimeTypeForTypeParameter(typeParameter, out var runtimeType))
                return runtimeType;

            if (TryGetMappedAsyncParameter(typeParameter, out var stateMachine, out var asyncParameter) &&
                stateMachine is not null &&
                asyncParameter is not null)
            {
                if (TryGetMethodGenericParameter(stateMachine.AsyncMethod, asyncParameter.Ordinal, codeGen, out var asyncMethodParameter))
                {
                    codeGen.CacheRuntimeTypeParameter(asyncParameter, asyncMethodParameter);
                    return asyncMethodParameter;
                }

                if (codeGen.TryGetRuntimeTypeForTypeParameter(asyncParameter, out var asyncResolved))
                {
                    if (IsMethodGenericParameter(asyncResolved))
                        return asyncResolved;

                    if (TryGetMethodGenericParameter(stateMachine.AsyncMethod, asyncParameter.Ordinal, codeGen, out var refreshedAsyncMethodParameter))
                    {
                        codeGen.CacheRuntimeTypeParameter(asyncParameter, refreshedAsyncMethodParameter);
                        return refreshedAsyncMethodParameter;
                    }

                    throw new InvalidOperationException("Unable to map async method type parameter to runtime generic parameter.");
                }

                if (TryGetMethodGenericParameter(stateMachine.AsyncMethod, asyncParameter.Ordinal, codeGen, out var mappedFallback))
                {
                    codeGen.CacheRuntimeTypeParameter(asyncParameter, mappedFallback);
                    return mappedFallback;
                }

                throw new InvalidOperationException("Unable to resolve async method generic parameter for state machine mapping.");
            }
            throw new InvalidOperationException("Unable to map state machine type parameter to async method generic parameter.");
        }

        return TypeSymbolExtensionsForCodeGen.GetClrType(typeArgument, codeGen);
    }

    private static bool TryGetMappedAsyncParameter(
        ITypeParameterSymbol typeParameter,
        out SynthesizedAsyncStateMachineTypeSymbol? stateMachine,
        out ITypeParameterSymbol? asyncParameter)
    {
        stateMachine = null;
        asyncParameter = null;

        var containingType = typeParameter.ContainingType;
        if (containingType is SynthesizedAsyncStateMachineTypeSymbol direct &&
            direct.TryMapToAsyncMethodTypeParameter(typeParameter, out var mapped))
        {
            stateMachine = direct;
            asyncParameter = mapped;
            return true;
        }

        if (containingType is ConstructedNamedTypeSymbol constructed &&
            constructed.ConstructedFrom is SynthesizedAsyncStateMachineTypeSymbol constructedStateMachine &&
            typeParameter.OriginalDefinition is ITypeParameterSymbol original &&
            constructedStateMachine.TryMapToAsyncMethodTypeParameter(original, out mapped))
        {
            stateMachine = constructedStateMachine;
            asyncParameter = mapped;
            return true;
        }

        return false;
    }

    private static bool TryGetMethodGenericParameter(IMethodSymbol methodSymbol, int ordinal, CodeGenerator codeGen, out Type parameter)
    {
        if (methodSymbol is null)
            throw new ArgumentNullException(nameof(methodSymbol));

        parameter = null!;

        if (TryGetSourceMethod(methodSymbol, out var sourceMethod) &&
            codeGen.TryGetMemberBuilder(sourceMethod, out var member) &&
            member is MethodInfo methodInfo)
        {
            var definition = methodInfo.IsGenericMethodDefinition
                ? methodInfo
                : methodInfo.GetGenericMethodDefinition();

            var arguments = definition.GetGenericArguments();
            if ((uint)ordinal < (uint)arguments.Length)
            {
                parameter = arguments[ordinal];
                return true;
            }
        }

        return false;
    }

    private static bool IsMethodGenericParameter(Type runtimeType)
    {
        try
        {
            return runtimeType.IsGenericParameter && runtimeType.IsGenericMethodParameter;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSignaturePlaceholderType(Type runtimeType)
    {
        try
        {
            _ = runtimeType.Assembly;
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private static bool TryGetSourceMethod(IMethodSymbol methodSymbol, out SourceMethodSymbol sourceMethod)
    {
        switch (methodSymbol)
        {
            case SourceMethodSymbol source:
                sourceMethod = source;
                return true;
            case IAliasSymbol alias when alias.UnderlyingSymbol is IMethodSymbol aliasMethod &&
                TryGetSourceMethod(aliasMethod, out sourceMethod):
                return true;
        }

        if (methodSymbol.UnderlyingSymbol is IMethodSymbol underlying &&
            !ReferenceEquals(underlying, methodSymbol) &&
            TryGetSourceMethod(underlying, out sourceMethod))
        {
            return true;
        }

        var originalDefinition = methodSymbol.OriginalDefinition;
        if (originalDefinition is not null &&
            !ReferenceEquals(originalDefinition, methodSymbol) &&
            TryGetSourceMethod(originalDefinition, out sourceMethod))
        {
            return true;
        }

        var constructedFrom = methodSymbol.ConstructedFrom;
        if (constructedFrom is not null &&
            !ReferenceEquals(constructedFrom, methodSymbol) &&
            TryGetSourceMethod(constructedFrom, out sourceMethod))
        {
            return true;
        }

        sourceMethod = null!;
        return false;
    }

    private string GetDebuggerDisplay()
    {
        try
        {
            return $"{Kind}: {this.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        }
        catch (Exception exc)
        {
            return $"{Kind}: <{exc.GetType().Name}>";
        }
    }

    public override string ToString()
    {
        return this.ToDisplayString();
    }
}

internal sealed class SubstitutedNamedTypeParameterSymbol : ITypeParameterSymbol
{
    private readonly ITypeParameterSymbol _original;
    private readonly ImmutableArray<ITypeSymbol> _constraintTypes;
    private ImmutableArray<INamedTypeSymbol>? _interfaces;
    private ImmutableArray<INamedTypeSymbol>? _allInterfaces;

    public SubstitutedNamedTypeParameterSymbol(
        ITypeParameterSymbol original,
        ImmutableArray<ITypeSymbol> constraintTypes)
    {
        _original = original;
        _constraintTypes = constraintTypes;
    }

    public string Name => _original.Name;
    public string MetadataName => _original.MetadataName;
    public SymbolKind Kind => _original.Kind;
    public ISymbol? ContainingSymbol => _original.ContainingSymbol;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _original.ContainingType;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => false;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public int Ordinal => _original.Ordinal;
    public TypeParameterOwnerKind OwnerKind => _original.OwnerKind;
    public INamedTypeSymbol? DeclaringTypeParameterOwner => _original.DeclaringTypeParameterOwner;
    public IMethodSymbol? DeclaringMethodParameterOwner => null;
    public TypeParameterConstraintKind ConstraintKind => _original.ConstraintKind;
    public ImmutableArray<ITypeSymbol> ConstraintTypes => _constraintTypes;
    public VarianceKind Variance => _original.Variance;

    public INamedTypeSymbol? BaseType => _constraintTypes
        .OfType<INamedTypeSymbol>()
        .FirstOrDefault(static constraint => constraint.TypeKind == TypeKind.Class) ?? _original.BaseType;
    public ITypeSymbol? OriginalDefinition => _original.OriginalDefinition ?? _original;
    public SpecialType SpecialType => _original.SpecialType;
    public TypeKind TypeKind => _original.TypeKind;
    public bool IsNamespace => false;
    public bool IsType => true;
    public bool IsReferenceType => _original.IsReferenceType;
    public bool IsValueType => _original.IsValueType;
    public bool IsInterface => _original.IsInterface;
    public bool IsTupleType => _original.IsTupleType;
    public bool IsUnion => _original.IsUnion;
    public bool IsUnionCase => _original.IsUnionCase;
    public INamedTypeSymbol? UnderlyingUnionType => _original.UnderlyingUnionType;

    public ImmutableArray<INamedTypeSymbol> Interfaces =>
        _interfaces ??= BuildInterfaces();

    public ImmutableArray<INamedTypeSymbol> AllInterfaces
    {
        get
        {
            if (_allInterfaces.HasValue)
                return _allInterfaces.Value;

            var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var direct in Interfaces)
            {
                if (seen.Add(direct))
                    builder.Add(direct);

                foreach (var inherited in direct.AllInterfaces)
                {
                    if (seen.Add(inherited))
                        builder.Add(inherited);
                }
            }

            _allInterfaces = builder.MoveToImmutable();
            return _allInterfaces.Value;
        }
    }

    public ImmutableArray<ISymbol> GetMembers() => ImmutableArray<ISymbol>.Empty;
    public ImmutableArray<ISymbol> GetMembers(string name) => ImmutableArray<ISymbol>.Empty;
    public ITypeSymbol? LookupType(string name) => null;
    public bool IsMemberDefined(string name, out ISymbol? symbol)
    {
        symbol = null;
        return false;
    }

    public void Accept(SymbolVisitor visitor) => visitor.DefaultVisit(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.DefaultVisit(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);

    private ImmutableArray<INamedTypeSymbol> BuildInterfaces()
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var constraint in _constraintTypes.OfType<INamedTypeSymbol>())
        {
            if (constraint.TypeKind == TypeKind.Interface && seen.Add(constraint))
                builder.Add(constraint);
        }

        return builder.MoveToImmutable();
    }
}

internal sealed class SubstitutedMethodSymbol : IMethodSymbol
{
    private readonly IMethodSymbol _original;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private readonly ISymbol? _associatedSymbol;
    private ImmutableArray<IParameterSymbol>? _parameters;
    private Dictionary<ITypeParameterSymbol, ImmutableArray<ITypeSymbol>>? _constraintTypeMap;
    private ImmutableArray<IMethodSymbol>? _explicitInterfaceImplementations;
    private readonly ImmutableArray<ITypeParameterSymbol> _typeParameters;
    private readonly Dictionary<ITypeParameterSymbol, ITypeParameterSymbol> _methodTypeParameterMap;
    private ITypeSymbol? _returnType;

    public SubstitutedMethodSymbol(
        IMethodSymbol original,
        ConstructedNamedTypeSymbol constructed,
        ISymbol? associatedSymbol = null)
    {
        _original = original;
        _constructed = constructed;
        _associatedSymbol = associatedSymbol;

        if (!_original.TypeParameters.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(_original.TypeParameters.Length);
            _methodTypeParameterMap = new Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>(
                _original.TypeParameters.Length,
                TypeParameterSubstitutionComparer.Instance);

            foreach (var typeParameter in _original.TypeParameters)
            {
                var substituted = new SubstitutedMethodTypeParameterSymbol(
                    typeParameter,
                    this,
                    _constructed,
                    _constructed.ContainingNamespace ?? typeParameter.ContainingNamespace);

                builder.Add(substituted);
                _methodTypeParameterMap[typeParameter] = substituted;
            }

            _typeParameters = builder.ToImmutable();
        }
        else
        {
            _typeParameters = _original.TypeParameters.IsDefault
                ? ImmutableArray<ITypeParameterSymbol>.Empty
                : _original.TypeParameters;
            _methodTypeParameterMap = new Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>(
                TypeParameterSubstitutionComparer.Instance);
        }
    }

    public string Name => _original.Name;
    public ITypeSymbol ReturnType => _returnType ??= _constructed.ReanchorNestedTypeIfNeeded(
        _constructed.Substitute(_original.ReturnType, _methodTypeParameterMap),
        _methodTypeParameterMap);

    public ImmutableArray<IParameterSymbol> Parameters =>
        _parameters ??= _original.Parameters
            .Select(p => (IParameterSymbol)new SubstitutedParameterSymbol(
                p,
                _constructed,
                _methodTypeParameterMap,
                this))
            .ToImmutableArray();

    public ISymbol ContainingSymbol => _constructed;

    public ImmutableArray<AttributeData> GetReturnTypeAttributes() => _original.GetReturnTypeAttributes();

    public MethodKind MethodKind => _original.MethodKind;
    public bool IsConstructor => _original.IsConstructor;
    public IMethodSymbol? OriginalDefinition => _original;
    public bool IsAbstract => _original.IsAbstract;
    public bool IsAsync => _original.IsAsync;
    public bool IsCheckedBuiltin => _original.IsCheckedBuiltin;
    public bool IsDefinition => _original.IsDefinition;
    public bool IsExtensionMethod => _original.IsExtensionMethod;
    public bool IsExtern => _original.IsExtern;
    public bool IsUnsafe => _original.IsUnsafe;
    public bool IsGenericMethod => _original.IsGenericMethod;
    public bool IsOverride => _original.IsOverride;
    public bool IsReadOnly => _original.IsReadOnly;
    public bool IsFinal => _original.IsFinal;
    public bool IsVirtual => _original.IsVirtual;
    public bool SetsRequiredMembers => _original.SetsRequiredMembers;
    public bool IsIterator => _original.IsIterator;
    public IteratorMethodKind IteratorKind => _original.IteratorKind;
    public ITypeSymbol? IteratorElementType => _original.IteratorElementType;

    public ImmutableArray<IMethodSymbol> ExplicitInterfaceImplementations
    {
        get
        {
            if (_explicitInterfaceImplementations.HasValue)
                return _explicitInterfaceImplementations.Value;

            var originals = _original.ExplicitInterfaceImplementations;

            if (originals.IsDefaultOrEmpty || originals.Length == 0)
            {
                _explicitInterfaceImplementations = originals;
                return originals;
            }

            var builder = ImmutableArray.CreateBuilder<IMethodSymbol>(originals.Length);

            foreach (var origImpl in originals)
            {
                var origIface = (INamedTypeSymbol?)origImpl.ContainingType;
                if (origIface is null)
                {
                    builder.Add(origImpl);
                    continue;
                }

                // Substitute the interface type with the constructed type’s substitution
                var substitutedIface = _constructed.Substitute(origIface) as INamedTypeSymbol;

                // If nothing changed, just reuse the original method
                if (substitutedIface is null ||
                    SymbolEqualityComparer.Default.Equals(origIface, substitutedIface))
                {
                    builder.Add(origImpl);
                    continue;
                }

                // Find the corresponding method on the substituted interface
                IMethodSymbol? substitutedMethod = null;

                foreach (var candidate in substitutedIface.GetMembers(origImpl.Name).OfType<IMethodSymbol>())
                {
                    // Match via OriginalDefinition – this is the key
                    if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, origImpl.OriginalDefinition))
                    {
                        substitutedMethod = candidate;
                        break;
                    }
                }

                builder.Add(substitutedMethod ?? origImpl);
            }

            _explicitInterfaceImplementations = builder.ToImmutable();
            return _explicitInterfaceImplementations.Value;
        }
    }

    public ImmutableArray<ITypeParameterSymbol> TypeParameters => _typeParameters;
    public ImmutableArray<ITypeSymbol> TypeArguments => _original.TypeArguments;
    public IMethodSymbol? ConstructedFrom => _original.ConstructedFrom ?? _original;
    public SymbolKind Kind => _original.Kind;
    public string MetadataName => _original.MetadataName;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ISymbol? AssociatedSymbol => _associatedSymbol ?? _original.AssociatedSymbol;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => _original.IsStatic;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public void Accept(SymbolVisitor visitor)
    {
        visitor.VisitMethod(this);
    }

    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor)
    {
        return visitor.VisitMethod(this);
    }

    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) =>
               comparer.Equals(this, other);

    public bool Equals(ISymbol? other) =>
        SymbolEqualityComparer.Default.Equals(this, other);

    public IMethodSymbol Construct(params ITypeSymbol[] typeArguments)
    {
        return new ConstructedMethodSymbol(this, typeArguments.ToImmutableArray(), _constructed);
    }

    internal bool TryGetSubstitutedConstraintTypes(
        ITypeParameterSymbol typeParameter,
        out ImmutableArray<ITypeSymbol> substituted)
    {
        substituted = default;

        if (_typeParameters.IsDefaultOrEmpty ||
            !_typeParameters.Any(tp => TypeParameterSubstitutionComparer.Instance.Equals(tp, typeParameter)))
        {
            return false;
        }

        _constraintTypeMap ??= new Dictionary<ITypeParameterSymbol, ImmutableArray<ITypeSymbol>>(
            TypeParameterSubstitutionComparer.Instance);

        if (_constraintTypeMap.TryGetValue(typeParameter, out substituted))
            return true;

        var originalConstraints = typeParameter.ConstraintTypes;

        if (originalConstraints.IsDefaultOrEmpty || originalConstraints.Length == 0)
        {
            substituted = originalConstraints;
            _constraintTypeMap[typeParameter] = substituted;
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(originalConstraints.Length);

        foreach (var constraint in originalConstraints)
            builder.Add(_constructed.Substitute(constraint, _methodTypeParameterMap));

        substituted = builder.ToImmutable();
        _constraintTypeMap[typeParameter] = substituted;
        return true;
    }

    internal ConstructorInfo GetConstructorInfo(CodeGenerator codeGen)
    {
        var cacheArguments = _constructed.GetAllTypeArguments();

        if (_original is SourceMethodSymbol cachedSource &&
            codeGen.TryGetMemberBuilder(cachedSource, cacheArguments, out var cachedMember) &&
            cachedMember is ConstructorInfo cachedConstructor)
        {
            return cachedConstructor;
        }

        if (_original is PEMethodSymbol peMethod)
        {
            var baseCtor = MethodSymbolCodeGenResolver.GetClrConstructorInfo(peMethod, codeGen);

            if (baseCtor.DeclaringType.IsGenericType)
            {
                var constructedType = _constructed.GetTypeInfo(codeGen).AsType();

                if (constructedType.GetType().FullName == "System.Reflection.Emit.TypeBuilderInstantiation")
                {
                    var constructedCtor = TypeBuilder.GetConstructor(constructedType, baseCtor);
                    if (constructedCtor is not null)
                        return constructedCtor;
                }

                var parameterTypes = Parameters
                    .Select(parameter => TypeSymbolExtensionsForCodeGen.GetClrType(parameter.Type, codeGen))
                    .ToArray();

                var resolved = constructedType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: parameterTypes,
                    modifiers: null);

                if (resolved is not null)
                    return resolved;

                if (constructedType is TypeBuilder typeBuilder)
                {
                    var constructedCtor = TypeBuilder.GetConstructor(typeBuilder, baseCtor);
                    if (constructedCtor is not null)
                        return constructedCtor;
                }

                throw new InvalidOperationException($"Unable to resolve constructed constructor for '{_constructed}' from metadata definition '{_original}'.");
            }

            return baseCtor;
        }

        if (_original is SubstitutedMethodSymbol substitutedMethod)
            return substitutedMethod.GetConstructorInfo(codeGen);

        if (_original is ConstructedMethodSymbol constructedMethod)
            return MethodSymbolCodeGenResolver.GetClrConstructorInfo(constructedMethod, codeGen);

        var unwrapped = _original;
        while (unwrapped.UnderlyingSymbol is IMethodSymbol underlying &&
               !ReferenceEquals(underlying, unwrapped))
        {
            unwrapped = underlying;
        }

        if (!ReferenceEquals(unwrapped, _original))
        {
            var rebound = unwrapped is SubstitutedMethodSymbol or ConstructedMethodSymbol
                ? unwrapped
                : new SubstitutedMethodSymbol(unwrapped, _constructed);

            return MethodSymbolCodeGenResolver.GetClrConstructorInfo(rebound, codeGen);
        }

        if (_original is SourceMethodSymbol sourceMethod)
        {
            var constructedType = _constructed.GetTypeInfo(codeGen).AsType();
            if (codeGen.GetMemberBuilder(sourceMethod) is ConstructorInfo definitionCtor)
            {
                var constructedCtor = TypeBuilder.GetConstructor(constructedType, definitionCtor);
                if (constructedCtor is not null)
                {
                    codeGen.AddMemberBuilder(sourceMethod, constructedCtor, cacheArguments);
                    return constructedCtor;
                }
            }

            throw new InvalidOperationException("Constructor builder not found for source method.");
        }

        throw new Exception("Unexpected method kind");
    }

    internal MethodInfo GetMethodInfo(CodeGenerator codeGen)
    {
        var cacheArguments = _constructed.GetAllTypeArguments();

        if (_original is SourceMethodSymbol cachedSource &&
            codeGen.TryGetMemberBuilder(cachedSource, cacheArguments, out var cachedMember) &&
            cachedMember is MethodInfo cachedMethod)
        {
            return cachedMethod;
        }

        if (_original is PEMethodSymbol peMethod)
        {
            var baseMethod = MethodSymbolCodeGenResolver.GetClrMethodInfo(peMethod, codeGen);

            // Resolve the constructed runtime type
            var constructedType = _constructed.GetTypeInfo(codeGen).AsType();

            if (constructedType.IsGenericType &&
                baseMethod.DeclaringType is Type baseDeclaringTypeForInstantiation &&
                baseDeclaringTypeForInstantiation.IsGenericTypeDefinition &&
                ReferenceEquals(constructedType.GetGenericTypeDefinition(), baseDeclaringTypeForInstantiation))
            {
                try
                {
                    var instantiated = TypeBuilder.GetMethod(constructedType, baseMethod);
                    if (instantiated is not null)
                        return instantiated;
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
                {
                }
            }

            // Use metadata name and parameter types to resolve the method on the constructed type
            if (!IsSignaturePlaceholderType(constructedType))
            {
                var parameterTypes = Parameters
                    .Select(x => TypeSymbolExtensionsForCodeGen.GetClrType(x.Type, codeGen))
                    .ToArray();
                var method = constructedType.GetMethod(
                    baseMethod.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                    null,
                    parameterTypes,
                    null
                );

                if (method != null)
                    return method;

                // Fallback: metadata-token matching is more resilient for generic instantiations
                // where reflected parameter types can differ from substituted symbol projections.
                if (baseMethod.DeclaringType is Type baseDeclaringType &&
                    constructedType.IsGenericType &&
                    baseDeclaringType.IsGenericTypeDefinition &&
                    ReferenceEquals(constructedType.GetGenericTypeDefinition(), baseDeclaringType))
                {
                    var candidates = constructedType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

                    foreach (var candidate in candidates)
                    {
                        if (candidate.Name != baseMethod.Name)
                            continue;

                        if (candidate.GetParameters().Length != baseMethod.GetParameters().Length)
                            continue;

                        if (candidate.IsGenericMethod != baseMethod.IsGenericMethod)
                            continue;

                        try
                        {
                            if (candidate.MetadataToken == baseMethod.MetadataToken)
                                return candidate;
                        }
                        catch
                        {
                            // Some reflected members (e.g. dynamic methods) can throw for MetadataToken;
                            // skip and continue probing.
                        }
                    }
                }
            }
            else
            {
                return baseMethod;
            }

            throw new MissingMethodException($"Method '{baseMethod.Name}' with specified parameters not found on constructed type '{constructedType}'.");
        }

        if (_original is SourceMethodSymbol sourceMethod)
        {
            if (codeGen.GetMemberBuilder(sourceMethod) is not MethodInfo definitionMethod)
                throw new InvalidOperationException("Method builder not found for source method.");

            var constructedType = _constructed.GetTypeInfo(codeGen).AsType();

            if (!ReferenceEquals(constructedType, definitionMethod.DeclaringType) && constructedType.IsGenericType)
            {
                var constructedMethod = TypeBuilder.GetMethod(constructedType, definitionMethod);
                if (constructedMethod is not null)
                {
                    codeGen.AddMemberBuilder(sourceMethod, constructedMethod, cacheArguments);
                    return constructedMethod;
                }
            }

            if (ReferenceEquals(constructedType, definitionMethod.DeclaringType))
                return definitionMethod;

            if (!IsSignaturePlaceholderType(constructedType))
            {
                var parameterTypes = sourceMethod.Parameters
                    .Select(p => TypeSymbolExtensionsForCodeGen.GetClrType(p.Type, codeGen))
                    .ToArray();

                var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var resolved = constructedType.GetMethod(definitionMethod.Name, bindingFlags, null, parameterTypes, null);
                if (resolved is not null)
                {
                    codeGen.AddMemberBuilder(sourceMethod, resolved, cacheArguments);
                    return resolved;
                }
            }
            else
            {
                return definitionMethod;
            }

            throw new MissingMethodException($"Method '{definitionMethod.Name}' with specified parameters not found on constructed type '{constructedType}'.");
        }

        throw new InvalidOperationException("Expected PE or source method symbol.");
    }

    private static bool IsSignaturePlaceholderType(Type runtimeType)
    {
        try
        {
            _ = runtimeType.Assembly;
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private string GetDebuggerDisplay()
    {
        try
        {
            return $"{Kind}: {this.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        }
        catch (Exception exc)
        {
            return $"{Kind}: <{exc.GetType().Name}>";
        }
    }

    public override string ToString()
    {
        return this.ToDisplayString();
    }
}

internal sealed class SubstitutedMethodTypeParameterSymbol : ITypeParameterSymbol
{
    private readonly ITypeParameterSymbol _original;
    private readonly SubstitutedMethodSymbol _containingMethod;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private readonly INamespaceSymbol? _containingNamespace;
    private ImmutableArray<ITypeSymbol>? _constraintTypes;

    public SubstitutedMethodTypeParameterSymbol(
        ITypeParameterSymbol original,
        SubstitutedMethodSymbol containingMethod,
        ConstructedNamedTypeSymbol constructed,
        INamespaceSymbol? containingNamespace)
    {
        _original = original;
        _containingMethod = containingMethod;
        _constructed = constructed;
        _containingNamespace = containingNamespace;
    }

    public string Name => _original.Name;
    public string MetadataName => _original.MetadataName;
    public SymbolKind Kind => _original.Kind;
    public ISymbol? ContainingSymbol => _containingMethod;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _containingNamespace ?? _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => false;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public int Ordinal => _original.Ordinal;
    public TypeParameterOwnerKind OwnerKind => TypeParameterOwnerKind.Method;
    public INamedTypeSymbol? DeclaringTypeParameterOwner => null;
    public IMethodSymbol? DeclaringMethodParameterOwner => _containingMethod;
    public TypeParameterConstraintKind ConstraintKind => _original.ConstraintKind;
    public ImmutableArray<ITypeSymbol> ConstraintTypes
    {
        get
        {
            if (_constraintTypes.HasValue)
                return _constraintTypes.Value;

            if (_containingMethod.TryGetSubstitutedConstraintTypes(_original, out var substituted))
                _constraintTypes = substituted;
            else
                _constraintTypes = _original.ConstraintTypes;

            return _constraintTypes.Value;
        }
    }
    public VarianceKind Variance => _original.Variance;

    public INamedTypeSymbol? BaseType => _original.BaseType;
    public ITypeSymbol? OriginalDefinition => _original.OriginalDefinition ?? _original;
    public SpecialType SpecialType => _original.SpecialType;
    public TypeKind TypeKind => _original.TypeKind;
    public bool IsNamespace => false;
    public bool IsType => true;
    public bool IsReferenceType => _original.IsReferenceType;
    public bool IsValueType => _original.IsValueType;
    public bool IsInterface => _original.IsInterface;
    public bool IsTupleType => _original.IsTupleType;
    public bool IsUnion => _original.IsUnion;
    public bool IsUnionCase => _original.IsUnionCase;
    public INamedTypeSymbol? UnderlyingUnionType => _original.UnderlyingUnionType;
    public ImmutableArray<INamedTypeSymbol> Interfaces => _original.Interfaces;
    public ImmutableArray<INamedTypeSymbol> AllInterfaces => _original.AllInterfaces;
    public ImmutableArray<ISymbol> GetMembers() => ImmutableArray<ISymbol>.Empty;
    public ImmutableArray<ISymbol> GetMembers(string name) => ImmutableArray<ISymbol>.Empty;
    public ITypeSymbol? LookupType(string name) => null;
    public bool IsMemberDefined(string name, out ISymbol? symbol)
    {
        symbol = null;
        return false;
    }

    public void Accept(SymbolVisitor visitor) => visitor.DefaultVisit(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.DefaultVisit(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);
}

internal sealed class SubstitutedFieldSymbol : IFieldSymbol
{
    private readonly IFieldSymbol _original;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private ITypeSymbol? _type;

    public SubstitutedFieldSymbol(IFieldSymbol original, ConstructedNamedTypeSymbol constructed)
    {
        _original = original;
        _constructed = constructed;
    }

    public string Name => _original.Name;
    public ITypeSymbol Type => _type ??= _constructed.Substitute(_original.Type);
    public ISymbol ContainingSymbol => _constructed;

    public bool IsConst => _original.IsConst;
    public bool IsRequired => _original.IsRequired;
    public bool IsReadOnly => _original.IsReadOnly;
    public RefKind RefKind => _original.RefKind;
    public SymbolKind Kind => _original.Kind;
    public string MetadataName => _original.MetadataName;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => _original.IsStatic;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public void Accept(SymbolVisitor visitor) => visitor.VisitField(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.VisitField(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);

    public object? GetConstantValue() => _original.GetConstantValue();

    internal FieldInfo GetFieldInfo(CodeGenerator codeGen)
    {
        var cacheArguments = _constructed.GetAllTypeArguments();

        if (_original is SourceFieldSymbol cachedSource &&
            codeGen.TryGetMemberBuilder(cachedSource, cacheArguments, out var cachedMember) &&
            cachedMember is FieldInfo cachedField)
        {
            return cachedField;
        }

        if (_original is PEFieldSymbol peField)
        {
            var constructedType = _constructed.GetTypeInfo(codeGen).AsType();
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var expectedType = TypeSymbolExtensionsForCodeGen.GetClrTypeTreatingUnitAsVoid(Type, codeGen);

            foreach (var candidate in constructedType.GetFields(bindingFlags))
            {
                if (!string.Equals(candidate.Name, peField.MetadataName, StringComparison.Ordinal))
                    continue;

                if (candidate.IsStatic != peField.IsStatic)
                    continue;

                var candidateFieldType = candidate.FieldType;
                if (candidateFieldType == expectedType ||
                    (candidateFieldType.IsGenericTypeDefinition && expectedType.IsGenericType && candidateFieldType == expectedType.GetGenericTypeDefinition()) ||
                    (candidateFieldType.IsGenericType && expectedType.IsGenericTypeDefinition && candidateFieldType.GetGenericTypeDefinition() == expectedType))
                {
                    return candidate;
                }
            }

            throw new MissingFieldException(constructedType.FullName, peField.MetadataName);
        }

        if (_original is SourceFieldSymbol sourceField)
        {
            if (codeGen.GetMemberBuilder(sourceField) is not FieldInfo definitionField)
                throw new InvalidOperationException("Field builder not found for source field.");

            var constructedType = _constructed.GetTypeInfo(codeGen).AsType();

            if (!ReferenceEquals(constructedType, definitionField.DeclaringType) && constructedType.IsGenericType)
            {
                if (_constructed.TypeArguments.Any(static argument =>
                        argument is ITypeParameterSymbol typeParameter &&
                        typeParameter.OwnerKind == TypeParameterOwnerKind.Method))
                {
                    try
                    {
                        var genericDefinition = constructedType.IsGenericTypeDefinition
                            ? constructedType
                            : constructedType.GetGenericTypeDefinition();
                        var projectedArguments = _constructed.TypeArguments
                            .Select(argument =>
                            {
                                if (argument is ITypeParameterSymbol typeParameter &&
                                    typeParameter.OwnerKind == TypeParameterOwnerKind.Method &&
                                    typeParameter.Ordinal >= 0)
                                {
                                    return System.Type.MakeGenericMethodParameter(typeParameter.Ordinal);
                                }

                                return TypeSymbolExtensionsForCodeGen.GetClrTypeTreatingUnitAsVoid(argument, codeGen);
                            })
                            .ToArray();

                        if (genericDefinition.GetGenericArguments().Length == projectedArguments.Length)
                            constructedType = genericDefinition.MakeGenericType(projectedArguments);
                    }
                    catch (NotSupportedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                var constructedField = TypeBuilder.GetField(constructedType, definitionField);
                if (constructedField is not null)
                {
                    if (CodeGenFlags.PrintDebug)
                    {
                        static string GetOwner(Type argument)
                        {
                            if (!argument.IsGenericParameter)
                                return "n/a";

                            try
                            {
                                return argument.DeclaringMethod is null ? "type" : "method";
                            }
                            catch (NotSupportedException)
                            {
                                return "method";
                            }
                        }

                        var fieldType = constructedField.FieldType;
                        var owner = fieldType.IsGenericParameter
                            ? GetOwner(fieldType)
                            : "n/a";
                        var containingArgs = constructedType.IsGenericType
                            ? string.Join(
                                ", ",
                                constructedType.GetGenericArguments().Select(argument =>
                                    argument.IsGenericParameter
                                        ? $"{argument}[owner={GetOwner(argument)}]"
                                        : argument.ToString()))
                            : "<non-generic>";
                        DebugUtils.PrintDebug(
                            $"[CodeGen:Field] Constructed field {_original.Name} on {constructedType} args=[{containingArgs}] -> {fieldType} (genericOwner={owner})");
                    }

                    codeGen.AddMemberBuilder(sourceField, constructedField, cacheArguments);
                    return constructedField;
                }
            }

            if (ReferenceEquals(constructedType, definitionField.DeclaringType))
                return definitionField;

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var resolved = constructedType.GetField(definitionField.Name, bindingFlags);
            if (resolved is not null)
            {
                codeGen.AddMemberBuilder(sourceField, resolved, cacheArguments);
                return resolved;
            }

            throw new MissingFieldException(constructedType.FullName, definitionField.Name);
        }

        throw new Exception("Not a supported field symbol.");
    }
}

internal sealed class SubstitutedPropertySymbol : IPropertySymbol
{
    private readonly IPropertySymbol _original;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private ImmutableArray<IPropertySymbol>? _explicitInterfaceImplementations;
    private ITypeSymbol? _type;
    private IMethodSymbol? _getMethod;
    private IMethodSymbol? _setMethod;

    public SubstitutedPropertySymbol(IPropertySymbol original, ConstructedNamedTypeSymbol constructed)
    {
        _original = original;
        _constructed = constructed;
    }

    public string Name => _original.Name;
    public ITypeSymbol Type => _type ??= _constructed.Substitute(_original.Type);
    public ISymbol ContainingSymbol => _constructed;
    public IPropertySymbol? OriginalDefinition => _original.OriginalDefinition ?? _original;
    public IMethodSymbol? GetMethod => _original.GetMethod is null
        ? null
        : _getMethod ??= new SubstitutedMethodSymbol(_original.GetMethod, _constructed, this);
    public IMethodSymbol? SetMethod => _original.SetMethod is null
        ? null
        : _setMethod ??= new SubstitutedMethodSymbol(_original.SetMethod, _constructed, this);
    public bool IsIndexer => _original.IsIndexer;
    public bool IsRequired => _original.IsRequired;
    public SymbolKind Kind => _original.Kind;
    public string MetadataName => _original.MetadataName;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => _original.IsStatic;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public ImmutableArray<IPropertySymbol> ExplicitInterfaceImplementations
    {
        get
        {
            if (_explicitInterfaceImplementations.HasValue)
                return _explicitInterfaceImplementations.Value;

            var originals = _original.ExplicitInterfaceImplementations;

            if (originals.IsDefaultOrEmpty || originals.Length == 0)
            {
                _explicitInterfaceImplementations = originals;
                return originals;
            }

            var builder = ImmutableArray.CreateBuilder<IPropertySymbol>(originals.Length);

            foreach (var origImpl in originals)
            {
                var origIface = (INamedTypeSymbol?)origImpl.ContainingType;
                if (origIface is null)
                {
                    builder.Add(origImpl);
                    continue;
                }

                // Substitute the interface type with the constructed type’s substitution
                var substitutedIface = _constructed.Substitute(origIface) as INamedTypeSymbol;

                // If nothing changed, just reuse the original property
                if (substitutedIface is null ||
                    SymbolEqualityComparer.Default.Equals(origIface, substitutedIface))
                {
                    builder.Add(origImpl);
                    continue;
                }

                // Find the corresponding property on the substituted interface
                IPropertySymbol? substitutedProperty = null;

                foreach (var candidate in substitutedIface.GetMembers(origImpl.Name).OfType<IPropertySymbol>())
                {
                    // Match via OriginalDefinition – this is the key
                    if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, origImpl.OriginalDefinition))
                    {
                        substitutedProperty = candidate;
                        break;
                    }
                }

                builder.Add(substitutedProperty ?? origImpl);
            }

            _explicitInterfaceImplementations = builder.ToImmutable();
            return _explicitInterfaceImplementations.Value;
        }
    }

    public void Accept(SymbolVisitor visitor) => visitor.VisitProperty(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.VisitProperty(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);
}

internal sealed class SubstitutedEventSymbol : IEventSymbol
{
    private readonly IEventSymbol _original;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private ImmutableArray<IEventSymbol>? _explicitInterfaceImplementations;
    private IMethodSymbol? _addMethod;
    private IMethodSymbol? _removeMethod;

    public SubstitutedEventSymbol(IEventSymbol original, ConstructedNamedTypeSymbol constructed)
    {
        _original = original;
        _constructed = constructed;
    }

    public string Name => _original.Name;
    public ITypeSymbol Type => _constructed.Substitute(_original.Type);
    public ISymbol ContainingSymbol => _constructed;
    public IMethodSymbol? AddMethod => _original.AddMethod is null
        ? null
        : _addMethod ??= new SubstitutedMethodSymbol(_original.AddMethod, _constructed, this);
    public IMethodSymbol? RemoveMethod => _original.RemoveMethod is null
        ? null
        : _removeMethod ??= new SubstitutedMethodSymbol(_original.RemoveMethod, _constructed, this);
    public SymbolKind Kind => _original.Kind;
    public string MetadataName => _original.MetadataName;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => _original.IsStatic;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public DocumentationComment? GetDocumentationComment() => _original.GetDocumentationComment();

    public ImmutableArray<IEventSymbol> ExplicitInterfaceImplementations
    {
        get
        {
            if (_explicitInterfaceImplementations.HasValue)
                return _explicitInterfaceImplementations.Value;

            var originals = _original.ExplicitInterfaceImplementations;

            if (originals.IsDefaultOrEmpty || originals.Length == 0)
            {
                _explicitInterfaceImplementations = originals;
                return originals;
            }

            var builder = ImmutableArray.CreateBuilder<IEventSymbol>(originals.Length);

            foreach (var originalImplementation in originals)
            {
                var originalInterfaceType = originalImplementation.ContainingType;
                if (originalInterfaceType is null)
                {
                    builder.Add(originalImplementation);
                    continue;
                }

                var substitutedInterfaceType = _constructed.Substitute(originalInterfaceType) as INamedTypeSymbol;
                if (substitutedInterfaceType is null ||
                    SymbolEqualityComparer.Default.Equals(originalInterfaceType, substitutedInterfaceType))
                {
                    builder.Add(originalImplementation);
                    continue;
                }

                IEventSymbol? substitutedEvent = null;
                foreach (var candidate in substitutedInterfaceType.GetMembers(originalImplementation.Name).OfType<IEventSymbol>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(candidate.Type, originalImplementation.Type))
                        continue;

                    substitutedEvent = candidate;
                    break;
                }

                builder.Add(substitutedEvent ?? originalImplementation);
            }

            _explicitInterfaceImplementations = builder.ToImmutable();
            return _explicitInterfaceImplementations.Value;
        }
    }

    public void Accept(SymbolVisitor visitor) => visitor.VisitEvent(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.VisitEvent(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);
}

internal sealed class SubstitutedParameterSymbol : IParameterSymbol
{
    private readonly IParameterSymbol _original;
    private readonly ConstructedNamedTypeSymbol _constructed;
    private readonly Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>? _methodMap;
    private readonly ISymbol _containingSymbol;
    private ITypeSymbol? _type;

    public SubstitutedParameterSymbol(
        IParameterSymbol original,
        ConstructedNamedTypeSymbol constructed,
        Dictionary<ITypeParameterSymbol, ITypeParameterSymbol>? methodMap = null,
        ISymbol? containingSymbol = null)
    {
        _original = original;
        _constructed = constructed;
        _methodMap = methodMap;
        _containingSymbol = containingSymbol ?? constructed;
    }

    public string Name => _original.Name;
    public ITypeSymbol Type => _type ??= _constructed.ReanchorNestedTypeIfNeeded(
        _constructed.Substitute(_original.Type, _methodMap),
        _methodMap);
    public bool HasImplicitName => _original.HasImplicitName;

    public SymbolKind Kind => _original.Kind;
    public string MetadataName => _original.MetadataName;
    public ISymbol? ContainingSymbol => _containingSymbol;
    public IAssemblySymbol? ContainingAssembly => _original.ContainingAssembly;
    public IModuleSymbol? ContainingModule => _original.ContainingModule;
    public INamedTypeSymbol? ContainingType => _constructed;
    public INamespaceSymbol? ContainingNamespace => _original.ContainingNamespace;
    public ImmutableArray<Location> Locations => _original.Locations;
    public Accessibility DeclaredAccessibility => _original.DeclaredAccessibility;
    public ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _original.DeclaringSyntaxReferences;
    public bool IsImplicitlyDeclared => _original.IsImplicitlyDeclared;
    public bool IsStatic => false;
    public ISymbol UnderlyingSymbol => this;
    public bool IsAlias => false;
    public ImmutableArray<AttributeData> GetAttributes() => _original.GetAttributes();
    public bool IsVarParams => _original.IsVarParams;
    public RefKind RefKind => _original.RefKind;
    public ScopedKind ScopedKind => _original.ScopedKind;
    public bool IsMutable => _original.IsMutable;
    public bool HasExplicitDefaultValue => _original.HasExplicitDefaultValue;
    public object? ExplicitDefaultValue => _original.ExplicitDefaultValue;

    public void Accept(SymbolVisitor visitor) => visitor.VisitParameter(this);
    public TResult Accept<TResult>(SymbolVisitor<TResult> visitor) => visitor.VisitParameter(this);
    public bool Equals(ISymbol? other, SymbolEqualityComparer comparer) => comparer.Equals(this, other);
    public bool Equals(ISymbol? other) => SymbolEqualityComparer.Default.Equals(this, other);

    private string GetDebuggerDisplay()
    {
        try
        {
            return $"{Kind}: {this.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        }
        catch (Exception exc)
        {
            return $"{Kind}: <{exc.GetType().Name}>";
        }
    }

    public override string ToString()
    {
        return this.ToDisplayString();
    }
}

internal sealed class TypeParameterSubstitutionComparer : IEqualityComparer<ITypeParameterSymbol>
{
    public static readonly TypeParameterSubstitutionComparer Instance = new();

    private TypeParameterSubstitutionComparer() { }

    public bool Equals(ITypeParameterSymbol? defX, ITypeParameterSymbol? defY)
    {
        if (defX is null || defY is null)
            return defX is null && defY is null;

        return defX.Ordinal == defY.Ordinal
            && defX.Name == defY.Name
            && defX.OwnerKind == defY.OwnerKind;
    }

    public int GetHashCode(ITypeParameterSymbol def)
    {
        // Avoid recursive symbol-display hashing for constructed/nested contexts.
        return HashCode.Combine(
            def.Name,
            def.Ordinal,
            (int)def.OwnerKind);
    }
}
