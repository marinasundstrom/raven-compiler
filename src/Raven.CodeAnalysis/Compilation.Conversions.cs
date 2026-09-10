using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

public partial class Compilation
{
    private ImmutableArray<IMethodSymbol> _extensionConversionOperators;
    private bool _extensionConversionOperatorsInitialized;
    private readonly ConcurrentDictionary<ConversionCacheKey, Conversion> _conversionCache = new(new ConversionCacheKeyComparer());

    public Conversion ClassifyConversion(ITypeSymbol source, ITypeSymbol destination, bool includeUserDefined = true)
    {
        if (source is null || destination is null)
            return Conversion.None;

        var key = new ConversionCacheKey(source, destination, includeUserDefined);
        if (_conversionCache.TryGetValue(key, out var cached))
            return cached;

        var conversion = ClassifyConversionCore(source, destination, includeUserDefined);
        return _conversionCache.GetOrAdd(key, conversion);
    }

    private Conversion ClassifyConversionCore(ITypeSymbol source, ITypeSymbol destination, bool includeUserDefined = true)
    {
        if (source is null || destination is null)
            return Conversion.None;

        static ITypeSymbol Unalias(ITypeSymbol type, ref bool wasAlias)
        {
            while (true)
            {
                if (!type.IsAlias)
                    return type;

                wasAlias = true;

                if (type.UnderlyingSymbol is ITypeSymbol aliasTarget)
                {
                    type = aliasTarget;
                    continue;
                }

                return type;
            }
        }

        bool sourceUsedAlias = false;
        source = Unalias(source, ref sourceUsedAlias);

        bool destinationUsedAlias = false;
        destination = Unalias(destination, ref destinationUsedAlias);

        static bool IsSystemNullableDefinition(INamedTypeSymbol named)
            => named.SpecialType == SpecialType.System_Nullable_T
               || (named.Arity == 1
                   && named.Name == "Nullable"
                   && named.ContainingNamespace?.ToDisplayString() == "System");

        static ITypeSymbol NormalizeExplicitNullableGeneric(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named &&
                IsSystemNullableDefinition(named) &&
                named.TypeArguments.Length == 1)
            {
                var underlying = named.TypeArguments[0];
                return underlying.IsNullable ? underlying : underlying.GetNullableType();
            }

            return type;
        }

        source = NormalizeExplicitNullableGeneric(source);
        destination = NormalizeExplicitNullableGeneric(destination);

        if (destination is null)
            return Conversion.None;

        static bool TryGetExpressionTreeDelegateType(ITypeSymbol type, out INamedTypeSymbol delegateType)
        {
            delegateType = null!;

            static ITypeSymbol Unalias(ITypeSymbol symbol)
            {
                while (symbol.IsAlias && symbol.UnderlyingSymbol is ITypeSymbol underlying)
                    symbol = underlying;

                return symbol;
            }

            type = Unalias(type);
            if (type is NullableTypeSymbol nullable)
                type = nullable.UnderlyingType;

            if (type is not INamedTypeSymbol named)
                return false;

            var definition = (named.OriginalDefinition as INamedTypeSymbol) ?? named;
            if (definition.Arity != 1)
                return false;

            var isExpressionType =
                string.Equals(definition.Name, "Expression", StringComparison.Ordinal) ||
                string.Equals(definition.MetadataName, "Expression`1", StringComparison.Ordinal);
            if (!isExpressionType)
                return false;

            if (named.TypeArguments.Length != 1)
                return false;

            var candidate = Unalias(named.TypeArguments[0]);
            if (candidate is not INamedTypeSymbol candidateDelegate)
                return false;

            if (candidateDelegate.TypeKind != TypeKind.Delegate &&
                candidateDelegate.GetDelegateInvokeMethod() is null)
            {
                return false;
            }

            delegateType = candidateDelegate;
            return true;
        }

        var aliasInvolved = sourceUsedAlias || destinationUsedAlias;

        if (source.SpecialType is SpecialType.System_Unit &&
            destination.SpecialType is SpecialType.System_Void ||
            source.SpecialType is SpecialType.System_Void &&
            destination.SpecialType is SpecialType.System_Unit)
        {
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));
        }

        Conversion Finalize(Conversion conversion)
        {
            if (!conversion.Exists)
                return conversion;

            return conversion.WithAlias(aliasInvolved);
        }

        if (SymbolEqualityComparer.Default.Equals(source, destination))
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));

        if (TryGetExpressionTreeDelegateType(destination, out var expressionTreeDelegate) &&
            source is INamedTypeSymbol sourceDelegate &&
            (sourceDelegate.TypeKind == TypeKind.Delegate || sourceDelegate.GetDelegateInvokeMethod() is not null) &&
            SymbolEqualityComparer.Default.Equals(sourceDelegate, expressionTreeDelegate))
        {
            return Finalize(new Conversion(isImplicit: true, isReference: true));
        }

        if (source.ContainsErrorType() || destination.ContainsErrorType())
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));

        if (source is LiteralTypeSymbol litSrc && destination is LiteralTypeSymbol litDest)
            return Equals(litSrc.ConstantValue, litDest.ConstantValue)
                ? Finalize(new Conversion(isImplicit: true, isIdentity: true))
                : Conversion.None;

        if (destination is LiteralTypeSymbol)
            return Conversion.None;

        // Implicit constant numeric conversions (C#-style):
        // An integral constant expression of type int can be implicitly converted to certain smaller integral types
        // (e.g. byte/char) if the value is within the target type's range.
        if (source is LiteralTypeSymbol literalSource)
        {
            // Unwrap literal sources to their underlying numeric type.
            var underlying = literalSource.UnderlyingType;
            if (underlying.SpecialType == SpecialType.System_Int32)
            {
                if (TryGetInt32ConstantValue(literalSource.ConstantValue, out var intValue) &&
                    IsImplicitInt32ConstantConversion(intValue, destination.SpecialType))
                {
                    return Finalize(new Conversion(isImplicit: true, isNumeric: true));
                }
            }
        }

        if (source is INamedTypeSymbol { OriginalDefinition: { } taskSourceDefinition } taskSourceNamed &&
            taskSourceNamed.TypeKind != TypeKind.Error &&
            taskSourceDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task_T &&
            taskSourceNamed.TypeArguments.Length == 1 &&
            taskSourceNamed.TypeArguments[0] is { } taskSourceArgument &&
            taskSourceArgument.SpecialType == SpecialType.System_Unit &&
            destination.SpecialType == SpecialType.System_Threading_Tasks_Task)
        {
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));
        }

        if (destination is INamedTypeSymbol { OriginalDefinition: { } taskDestinationDefinition } taskDestinationNamed &&
            taskDestinationNamed.TypeKind != TypeKind.Error &&
            taskDestinationDefinition.SpecialType == SpecialType.System_Threading_Tasks_Task_T &&
            taskDestinationNamed.TypeArguments.Length == 1 &&
            taskDestinationNamed.TypeArguments[0] is { } taskDestinationArgument &&
            taskDestinationArgument.SpecialType == SpecialType.System_Unit &&
            source.SpecialType == SpecialType.System_Threading_Tasks_Task)
        {
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));
        }

        var sourceUnionCase = source.TryGetUnionCase();
        var destinationUnion = destination.TryGetUnion();
        var sourceUnionForCase = ResolveSourceUnionForCase(source, sourceUnionCase);
        if (sourceUnionCase is not null &&
            sourceUnionForCase is not null &&
            destinationUnion is not null &&
            (SymbolEqualityComparer.Default.Equals(sourceUnionForCase, destinationUnion) ||
             (SymbolEqualityComparer.Default.Equals(sourceUnionForCase.OriginalDefinition, destinationUnion.OriginalDefinition) &&
              // For same-family unions, only allow when concrete type args at each position are
              // compatible. A type parameter at either position is freely compatible; two concrete
              // args must be equal. This prevents Ok<int> → Result<(), E> while still allowing
              // Error<E> → Result<T[], E> (type params) and Ok<int> → Result<int, string> (E open).
              ConcreteTypeArgumentsAreCompatible(sourceUnionForCase, (ITypeSymbol)destinationUnion))))
        {
            var unionConstructor = FindUnionCarrierConstructor(source, destination);
            if (unionConstructor is not null)
            {
                return Finalize(new Conversion(
                    isImplicit: true,
                    isDiscriminatedUnion: true,
                    isUserDefined: false,
                    methodSymbol: null,
                    constructorSymbol: unionConstructor));
            }
        }

        if (sourceUnionCase is not null &&
            sourceUnionForCase is not null &&
            destinationUnion is not null &&
            SymbolEqualityComparer.Default.Equals(sourceUnionForCase.OriginalDefinition, destinationUnion.OriginalDefinition) &&
            !ConcreteTypeArgumentsAreCompatible(sourceUnionForCase, (ITypeSymbol)destinationUnion))
        {
            return Conversion.None;
        }

        if (source.TypeKind != TypeKind.Null &&
            destinationUnion is not null &&
            (sourceUnionCase is null || sourceUnionForCase is null))
        {
            var fallbackUnionConstructor = FindUnionCarrierConstructor(source, destination);
            if (fallbackUnionConstructor is not null)
            {
                return Finalize(new Conversion(
                    isImplicit: true,
                    isDiscriminatedUnion: true,
                    isUserDefined: false,
                    methodSymbol: null,
                    constructorSymbol: fallbackUnionConstructor));
            }
        }

        if (source is INamedTypeSymbol sourceNamedUnion &&
            sourceNamedUnion.TryGetUnion() is not null &&
            destination is ITypeSymbol destinationMemberType &&
            FindUnionTryGetValueMethod(sourceNamedUnion, destinationMemberType) is not null)
        {
            return Finalize(new Conversion(
                isImplicit: false,
                isDiscriminatedUnion: true,
                isUserDefined: false,
                methodSymbol: null,
                constructorSymbol: null));
        }

        bool ElementTypesAreCompatible(ITypeSymbol sourceElement, ITypeSymbol destinationElement)
        {
            bool sourceElementUsedAlias = false;
            var unaliasedSource = Unalias(sourceElement, ref sourceElementUsedAlias);

            bool destinationElementUsedAlias = false;
            var unaliasedDestination = Unalias(destinationElement, ref destinationElementUsedAlias);

            if (!unaliasedSource.MetadataIdentityEquals(unaliasedDestination))
                return false;

            if (sourceElementUsedAlias || destinationElementUsedAlias)
                aliasInvolved = true;

            return true;
        }

        if (source is IAddressTypeSymbol addressSource)
        {
            if (destination is RefTypeSymbol refTypeDestination &&
                ElementTypesAreCompatible(addressSource.ReferencedType, refTypeDestination.ElementType))
            {
                return Finalize(new Conversion(isImplicit: true));
            }

            if (destination is IPointerTypeSymbol addressPointerDestination &&
                ElementTypesAreCompatible(addressSource.ReferencedType, addressPointerDestination.PointedAtType))
            {
                return Finalize(new Conversion(isImplicit: true, isPointer: true));
            }
        }

        if (source is RefTypeSymbol refTypeSource &&
            destination is IPointerTypeSymbol pointerDestination &&
            ElementTypesAreCompatible(refTypeSource.ElementType, pointerDestination.PointedAtType))
        {
            return Finalize(new Conversion(isImplicit: true, isPointer: true));
        }

        if (source is IArrayTypeSymbol sourceArray && destination is IArrayTypeSymbol destinationArray)
        {
            if (sourceArray.Rank != destinationArray.Rank)
            {
                return Conversion.None;
            }

            var elementConversionIsIdentity = false;
            if (sourceArray.ElementType.IsValueType || destinationArray.ElementType.IsValueType)
            {
                if (!ElementTypesAreCompatible(sourceArray.ElementType, destinationArray.ElementType))
                    return Conversion.None;

                elementConversionIsIdentity = true;
            }
            else
            {
                var elementConversion = ClassifyConversion(
                    sourceArray.ElementType,
                    destinationArray.ElementType,
                    includeUserDefined: false);
                if (!elementConversion.IsImplicit ||
                    !(elementConversion.IsIdentity || elementConversion.IsReference))
                {
                    return Conversion.None;
                }

                elementConversionIsIdentity = elementConversion.IsIdentity;
            }

            if (sourceArray.FixedLength == destinationArray.FixedLength)
                return Finalize(new Conversion(
                    isImplicit: true,
                    isIdentity: elementConversionIsIdentity,
                    isReference: !elementConversionIsIdentity));

            if (sourceArray.FixedLength is not null && destinationArray.FixedLength is null)
                return Finalize(new Conversion(isImplicit: true, isReference: true));

            return Conversion.None;
        }

        if (source.MetadataIdentityEquals(destination) &&
            !Conversion.IsNullable(source) &&
            !Conversion.IsNullable(destination))
        {
            return Finalize(new Conversion(isImplicit: true, isIdentity: true));
        }

        if (source.TypeKind == TypeKind.Null)
        {
            if (destination.TypeKind == TypeKind.Nullable)
                return Finalize(new Conversion(isImplicit: true, isReference: true));

            return Conversion.None;
        }

        if (source is NullableTypeSymbol nullableSource)
        {
            if (!nullableSource.UnderlyingType.IsValueType)
            {
                if (destination is NullableTypeSymbol nullableReferenceDestination &&
                    !nullableReferenceDestination.UnderlyingType.IsValueType)
                {
                    var conv = ClassifyConversion(nullableSource.UnderlyingType, nullableReferenceDestination.UnderlyingType, includeUserDefined);
                    if (conv.Exists)
                    {
                        return Finalize(new Conversion(
                            isImplicit: conv.IsImplicit,
                            isIdentity: conv.IsIdentity,
                            isNumeric: conv.IsNumeric,
                            isReference: true,
                            isBoxing: conv.IsBoxing,
                            isUnboxing: conv.IsUnboxing,
                            isPointer: conv.IsPointer,
                            isDiscriminatedUnion: conv.IsUnion,
                            isLifted: true,
                            isUserDefined: conv.IsUserDefined,
                            isAlias: conv.IsAlias,
                            methodSymbol: conv.MethodSymbol,
                            constructorSymbol: conv.ConstructorSymbol));
                    }
                }

            }
            else
            {
                if (destination is NullableTypeSymbol nullableDestination && nullableDestination.UnderlyingType.IsValueType)
                {
                    var conv2 = ClassifyConversion(nullableSource.UnderlyingType, nullableDestination.UnderlyingType, includeUserDefined);
                    if (conv2.Exists)
                    {
                        return Finalize(new Conversion(
                            isImplicit: conv2.IsImplicit,
                            isIdentity: conv2.IsIdentity,
                            isNumeric: conv2.IsNumeric,
                            isReference: conv2.IsReference,
                            isBoxing: conv2.IsBoxing,
                            isUnboxing: conv2.IsUnboxing,
                            isUserDefined: conv2.IsUserDefined,
                            isAlias: conv2.IsAlias,
                            isLifted: true,
                            methodSymbol: conv2.MethodSymbol));
                    }
                }

                var conv3 = ClassifyConversion(nullableSource.UnderlyingType, destination, includeUserDefined);
                if (conv3.Exists)
                {
                    var isImplicit = !destination.IsValueType && conv3.IsImplicit;
                    return Finalize(new Conversion(
                        isImplicit: isImplicit,
                        isIdentity: conv3.IsIdentity,
                        isNumeric: conv3.IsNumeric,
                        isReference: conv3.IsReference,
                        isBoxing: conv3.IsBoxing,
                        isUnboxing: conv3.IsUnboxing,
                        isUserDefined: conv3.IsUserDefined,
                        isAlias: conv3.IsAlias,
                        isLifted: true,
                        methodSymbol: conv3.MethodSymbol));
                }
            }
        }

        if (destination is NullableTypeSymbol nullableDest)
        {
            if (!nullableDest.UnderlyingType.IsValueType)
            {
                var conv = ClassifyConversion(source, nullableDest.UnderlyingType, includeUserDefined);
                if (conv.Exists)
                    return Finalize(conv);
            }
            else
            {
                var conv = ClassifyConversion(source, nullableDest.UnderlyingType, includeUserDefined);
                if (conv.Exists)
                {
                    return Finalize(new Conversion(
                        isImplicit: true,
                        isIdentity: false,
                        isNumeric: conv.IsNumeric,
                        isReference: conv.IsReference || !source.IsValueType,
                        isBoxing: conv.IsBoxing,
                        isUnboxing: conv.IsUnboxing,
                        isUserDefined: conv.IsUserDefined,
                        isAlias: conv.IsAlias,
                        isLifted: true,
                        methodSymbol: conv.MethodSymbol));
                }
            }
        }

        if (source.SpecialType == SpecialType.System_Void)
            return Conversion.None;

        if (SemanticFacts.MayBeRefLike(source) && destination.IsReferenceType)
            return Conversion.None;

        var objType = GetSpecialType(SpecialType.System_Object);

        if (destination.MetadataIdentityEquals(objType))
        {
            if (source.MetadataIdentityEquals(objType))
            {
                return Conversion.None;
            }

            return Finalize(new Conversion(
                isImplicit: true,
                isReference: !source.IsValueType,
                isBoxing: source.IsValueType));
        }

        if (source is LiteralTypeSymbol litSrc2)
            return Finalize(ClassifyConversion(litSrc2.UnderlyingType, destination, includeUserDefined));

        if (IsReferenceConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: true, isReference: true));
        }

        if (IsExplicitReferenceConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: false, isReference: true));
        }

        if (IsBoxingConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: true, isBoxing: true));
        }

        if (IsUnboxingConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: false, isUnboxing: true));
        }

        // C#-style explicit enum conversions:
        // - enum -> any integral type
        // - any integral type -> enum
        // - enum -> enum
        // Treat them as explicit numeric conversions for ranking and diagnostics.
        if (source is INamedTypeSymbol sourceNamedEnum &&
            sourceNamedEnum.TypeKind == TypeKind.Enum &&
            (IsIntegralType(destination) ||
             destination is INamedTypeSymbol { TypeKind: TypeKind.Enum }))
        {
            return Finalize(new Conversion(isImplicit: false, isNumeric: true));
        }

        if (destination is INamedTypeSymbol destNamedEnum &&
            destNamedEnum.TypeKind == TypeKind.Enum &&
            IsIntegralType(source))
        {
            return Finalize(new Conversion(isImplicit: false, isNumeric: true));
        }

        if (IsImplicitNumericConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: true, isNumeric: true));
        }

        if (IsExplicitNumericConversion(source, destination))
        {
            return Finalize(new Conversion(isImplicit: false, isNumeric: true));
        }

        if (includeUserDefined &&
            TryClassifyCovariantReadOnlySpanConversion(source, destination, out var covariantReadOnlySpanConversion))
        {
            return Finalize(covariantReadOnlySpanConversion);
        }

        var sourceNamed = source as INamedTypeSymbol;
        var destinationNamed = destination as INamedTypeSymbol;

        if (includeUserDefined && (sourceNamed != null || destinationNamed != null))
        {
            IEnumerable<IMethodSymbol> candidateConversions =
                Enumerable.Empty<IMethodSymbol>();

            if (sourceNamed != null)
                candidateConversions = candidateConversions.Concat(sourceNamed.GetMembers().OfType<IMethodSymbol>());
            if (destinationNamed != null && !source.MetadataIdentityEquals(destination))
                candidateConversions = candidateConversions.Concat(destinationNamed.GetMembers().OfType<IMethodSymbol>());

            candidateConversions = candidateConversions.Concat(GetExtensionConversionCandidates(source, destination));

            var applicableConversions = ImmutableArray.CreateBuilder<IMethodSymbol>();
            var seenConversions = new HashSet<IMethodSymbol>(ReferenceEqualityComparer.Instance);

            foreach (var method in candidateConversions)
            {
                if (method.MethodKind is not MethodKind.Conversion ||
                    method.Parameters.Length != 1)
                {
                    continue;
                }

                if (ContainsUnboundTypeParameter(method.Parameters[0].Type) ||
                    ContainsUnboundTypeParameter(method.ReturnType))
                {
                    continue;
                }

                if (!SatisfiesMethodAndContainingTypeConstraints(method))
                    continue;

                if (source.TypeKind == TypeKind.Null)
                    continue;

                var sourceConversion = ClassifyConversion(source, method.Parameters[0].Type, includeUserDefined: false);
                var sourceConvertible = sourceConversion.Exists && sourceConversion.IsImplicit;

                if (!sourceConvertible && SourceMatchesDiscriminatedUnionCase(source, method.Parameters[0].Type))
                    sourceConvertible = true;

                if (sourceConvertible &&
                    ClassifyConversion(method.ReturnType, destination, includeUserDefined: false) is { Exists: true, IsImplicit: true })
                {
                    if (method.Name == "op_Implicit" &&
                        SymbolEqualityComparer.Default.Equals(source, method.Parameters[0].Type) &&
                        SymbolEqualityComparer.Default.Equals(method.ReturnType, destination))
                    {
                        return Finalize(new Conversion(
                            isImplicit: true,
                            isUserDefined: true,
                            methodSymbol: method));
                    }

                    if (seenConversions.Add(method))
                        applicableConversions.Add(method);
                }
            }

            if (SelectBestUserDefinedConversion(source, destination, applicableConversions) is { } bestConversion)
            {
                var isImplicit = bestConversion.Name == "op_Implicit";
                return Finalize(new Conversion(isImplicit: isImplicit, isUserDefined: true, methodSymbol: bestConversion));
            }
        }

        if (source.TryGetUnionCase() is { } unionCase &&
            !SymbolEqualityComparer.Default.Equals(source, unionCase.Union))
        {
            var unionConversion = ClassifyConversion(unionCase.Union, destination, includeUserDefined);
            if (unionConversion.Exists)
                return unionConversion;
        }

        return Conversion.None;

        static bool TryGetInt32ConstantValue(object? constantValue, out int value)
        {
            switch (constantValue)
            {
                case int i:
                    value = i;
                    return true;

                case byte b:
                    value = b;
                    return true;

                case char c:
                    value = c;
                    return true;

                case sbyte sb:
                    value = sb;
                    return true;

                case short s:
                    value = s;
                    return true;

                case ushort us:
                    value = us;
                    return true;

                default:
                    value = default;
                    return false;
            }
        }

        static bool IsImplicitInt32ConstantConversion(int value, SpecialType destination)
        {
            // Match C#'s implicit constant expression conversions.
            // An int constant can be implicitly converted to smaller integral types when the value fits.
            return destination switch
            {
                SpecialType.System_SByte => value >= sbyte.MinValue && value <= sbyte.MaxValue,
                SpecialType.System_Byte => (uint)value <= byte.MaxValue,
                SpecialType.System_Int16 => value >= short.MinValue && value <= short.MaxValue,
                SpecialType.System_UInt16 => (uint)value <= ushort.MaxValue,
                SpecialType.System_UInt32 => value >= 0,
                SpecialType.System_UInt64 => value >= 0,
                SpecialType.System_Char => (uint)value <= char.MaxValue,
                _ => false
            };
        }

        bool SourceMatchesDiscriminatedUnionCase(ITypeSymbol sourceType, ITypeSymbol parameterType)
        {
            var sourceCase = sourceType.TryGetUnionCase();
            var parameterUnion = parameterType.TryGetUnion();
            var sourceUnion = ResolveSourceUnionForCase(sourceType, sourceCase);

            if (sourceCase is null || sourceUnion is null || parameterUnion is null)
                return false;

            if (SymbolEqualityComparer.Default.Equals(sourceUnion, parameterUnion))
                return true;

            if (!SymbolEqualityComparer.Default.Equals(sourceUnion.OriginalDefinition, parameterUnion.OriginalDefinition))
                return false;

            return ConcreteTypeArgumentsAreCompatible(sourceUnion, (ITypeSymbol)parameterUnion);
        }

        // Returns true when, for each type argument position, at least one side is a type parameter
        // (freely compatible) or both concrete args are equal. This allows Error<E> → Result<T,E>
        // (type params) and Ok<int> → Result<int,string> (E open on source side) while blocking
        // Ok<int> → Result<(),string> (int ≠ () in the same position).
        bool ConcreteTypeArgumentsAreCompatible(ITypeSymbol sourceUnion, ITypeSymbol destUnion)
        {
            return TypeArgumentsAreCompatible(sourceUnion, destUnion);
        }

        static ITypeSymbol? ResolveSourceUnionForCase(ITypeSymbol sourceType, IUnionCaseTypeSymbol? sourceCase)
        {
            if (sourceCase is null || sourceCase.Union is not INamedTypeSymbol sourceUnion)
                return null;

            if (sourceType is INamedTypeSymbol sourceNamed &&
                TryProjectUnionFromCaseArguments(sourceNamed, sourceCase, out var projectedUnion))
            {
                return projectedUnion;
            }

            return sourceUnion;
        }

        static bool TryProjectUnionFromCaseArguments(
            INamedTypeSymbol caseType,
            IUnionCaseTypeSymbol caseSymbol,
            out INamedTypeSymbol? projectedUnion)
        {
            projectedUnion = null;

            if (caseType.TypeArguments.IsDefaultOrEmpty)
                return false;

            var caseDefinition = caseSymbol.OriginalDefinition as IUnionCaseTypeSymbol ?? caseSymbol;
            if (caseDefinition is not INamedTypeSymbol caseDefinitionNamed ||
                caseDefinitionNamed.TypeParameters.IsDefaultOrEmpty ||
                caseDefinitionNamed.TypeParameters.Length != caseType.TypeArguments.Length)
            {
                return false;
            }

            var unionDefinition = caseDefinition.Union.OriginalDefinition as INamedTypeSymbol ?? caseDefinition.Union as INamedTypeSymbol;
            if (unionDefinition is null || unionDefinition.TypeParameters.IsDefaultOrEmpty)
                return false;

            var unionTypeArguments = unionDefinition.TypeParameters
                .Select(typeParameter => (ITypeSymbol)typeParameter)
                .ToArray();

            var changed = false;

            for (var i = 0; i < caseDefinitionNamed.TypeParameters.Length; i++)
            {
                var caseTypeParameter = caseDefinitionNamed.TypeParameters[i];
                ITypeParameterSymbol? unionTypeParameter = null;

                if (caseDefinition is SourceUnionCaseTypeSymbol sourceCaseDefinition &&
                    sourceCaseDefinition.TryGetProjectedUnionTypeParameter(caseTypeParameter, out var mapped))
                {
                    unionTypeParameter = mapped;
                }
                else
                {
                    unionTypeParameter = unionDefinition.TypeParameters
                        .FirstOrDefault(tp => string.Equals(tp.Name, caseTypeParameter.Name, StringComparison.Ordinal));
                }

                if (unionTypeParameter is null)
                    continue;

                var unionIndex = -1;
                for (var unionParameterIndex = 0; unionParameterIndex < unionDefinition.TypeParameters.Length; unionParameterIndex++)
                {
                    if (SymbolEqualityComparer.Default.Equals(unionDefinition.TypeParameters[unionParameterIndex], unionTypeParameter))
                    {
                        unionIndex = unionParameterIndex;
                        break;
                    }
                }

                if (unionIndex < 0 || unionIndex >= unionTypeArguments.Length)
                    continue;

                unionTypeArguments[unionIndex] = caseType.TypeArguments[i];
                changed = true;
            }

            if (!changed)
                return false;

            projectedUnion = (INamedTypeSymbol)unionDefinition.Construct(unionTypeArguments);
            return true;
        }

        static bool ContainsUnboundTypeParameter(ITypeSymbol type)
        {
            type = type.GetNonNullableType();

            return type switch
            {
                ITypeParameterSymbol => true,
                IArrayTypeSymbol array => ContainsUnboundTypeParameter(array.ElementType),
                INamedTypeSymbol named when !named.TypeArguments.IsDefaultOrEmpty =>
                    named.TypeArguments.Any(ContainsUnboundTypeParameter),
                INamedTypeSymbol => false,
                _ => false
            };
        }

        static bool SatisfiesMethodAndContainingTypeConstraints(IMethodSymbol method)
        {
            var methodDefinition = method.OriginalDefinition ?? method;
            var methodTypeParameters = methodDefinition.TypeParameters;

            if (!methodTypeParameters.IsDefaultOrEmpty && methodTypeParameters.Length > 0)
            {
                var methodTypeArguments = method.TypeArguments;
                if (methodTypeArguments.IsDefaultOrEmpty || methodTypeArguments.Length != methodTypeParameters.Length)
                    return false;

                for (int i = 0; i < methodTypeParameters.Length; i++)
                {
                    var argument = methodTypeArguments[i];
                    if (argument is ITypeParameterSymbol || !argument.SatisfiesConstraints(methodTypeParameters[i]))
                        return false;
                }
            }

            if (method.ContainingType is not INamedTypeSymbol containingType)
                return true;

            var containerDefinition = containingType.ConstructedFrom as INamedTypeSymbol ?? containingType;
            var containerTypeParameters = containerDefinition.TypeParameters;
            if (containerTypeParameters.IsDefaultOrEmpty || containerTypeParameters.Length == 0)
                return true;

            var containerTypeArguments = containingType.TypeArguments;
            if (containerTypeArguments.IsDefaultOrEmpty || containerTypeArguments.Length != containerTypeParameters.Length)
                return false;

            for (int i = 0; i < containerTypeParameters.Length; i++)
            {
                var argument = containerTypeArguments[i];
                if (argument is ITypeParameterSymbol || !argument.SatisfiesConstraints(containerTypeParameters[i]))
                    return false;
            }

            return true;
        }
    }

    private IMethodSymbol? SelectBestUserDefinedConversion(
        ITypeSymbol source,
        ITypeSymbol destination,
        ImmutableArray<IMethodSymbol>.Builder applicableConversions)
    {
        if (applicableConversions.Count == 0)
            return null;

        var implicitConversions = applicableConversions
            .Where(static method => method.Name == "op_Implicit")
            .ToImmutableArray();
        var candidates = implicitConversions.IsEmpty
            ? applicableConversions.ToImmutable()
            : implicitConversions;

        if (candidates.Length == 1)
            return candidates[0];

        IMethodSymbol? best = null;
        foreach (var candidate in candidates)
        {
            var dominatesEveryOtherCandidate = true;
            foreach (var other in candidates)
            {
                if (ReferenceEquals(candidate, other) ||
                    SymbolEqualityComparer.Default.Equals(candidate, other))
                {
                    continue;
                }

                if (!IsBetterUserDefinedConversion(candidate, other, source, destination))
                {
                    dominatesEveryOtherCandidate = false;
                    break;
                }
            }

            if (!dominatesEveryOtherCandidate)
                continue;

            if (best is not null && !SymbolEqualityComparer.Default.Equals(best, candidate))
                return null;

            best = candidate;
        }

        return best;
    }

    private bool IsBetterUserDefinedConversion(
        IMethodSymbol candidate,
        IMethodSymbol other,
        ITypeSymbol source,
        ITypeSymbol destination)
    {
        var sourceComparison = CompareUserDefinedSourceTypes(
            candidate.Parameters[0].Type,
            other.Parameters[0].Type,
            source);
        var targetComparison = CompareUserDefinedTargetTypes(
            candidate.ReturnType,
            other.ReturnType,
            destination);

        return sourceComparison <= 0 &&
            targetComparison <= 0 &&
            (sourceComparison < 0 || targetComparison < 0);
    }

    private int CompareUserDefinedSourceTypes(
        ITypeSymbol candidate,
        ITypeSymbol other,
        ITypeSymbol source)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, other))
            return 0;
        if (SymbolEqualityComparer.Default.Equals(candidate, source))
            return -1;
        if (SymbolEqualityComparer.Default.Equals(other, source))
            return 1;

        var candidateToOther = ClassifyConversion(candidate, other, includeUserDefined: false).IsImplicit;
        var otherToCandidate = ClassifyConversion(other, candidate, includeUserDefined: false).IsImplicit;
        if (candidateToOther != otherToCandidate)
            return candidateToOther ? -1 : 1;

        return 0;
    }

    private int CompareUserDefinedTargetTypes(
        ITypeSymbol candidate,
        ITypeSymbol other,
        ITypeSymbol destination)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, other))
            return 0;
        if (SymbolEqualityComparer.Default.Equals(candidate, destination))
            return -1;
        if (SymbolEqualityComparer.Default.Equals(other, destination))
            return 1;

        var candidateToOther = ClassifyConversion(candidate, other, includeUserDefined: false).IsImplicit;
        var otherToCandidate = ClassifyConversion(other, candidate, includeUserDefined: false).IsImplicit;
        if (candidateToOther != otherToCandidate)
            return otherToCandidate ? -1 : 1;

        return 0;
    }

    private bool TryClassifyCovariantReadOnlySpanConversion(
        ITypeSymbol source,
        ITypeSymbol destination,
        out Conversion conversion)
    {
        conversion = Conversion.None;

        if (!TryGetReadOnlySpanElementType(destination, out var destinationElementType) ||
            !TryGetSpanConversionSourceElementType(source, out var sourceElementType) ||
            SymbolEqualityComparer.Default.Equals(sourceElementType, destinationElementType))
        {
            return false;
        }

        var elementConversion = ClassifyConversion(
            sourceElementType,
            destinationElementType,
            includeUserDefined: false);

        if (!elementConversion.IsImplicit || !elementConversion.IsReference)
            return false;

        var destinationNamed = (INamedTypeSymbol)destination;
        var castUp = destinationNamed
            .GetMembers("CastUp")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method =>
                method.IsStatic &&
                method.Arity == 1 &&
                method.Parameters.Length == 1);

        if (castUp is null)
            return false;

        var constructedCastUp = castUp.Construct(sourceElementType);
        conversion = new Conversion(
            isImplicit: true,
            isUserDefined: true,
            methodSymbol: constructedCastUp);
        return true;
    }

    private static bool TryGetReadOnlySpanElementType(
        ITypeSymbol type,
        out ITypeSymbol elementType)
    {
        elementType = null!;

        if (type is not INamedTypeSymbol named ||
            named.TypeArguments.Length != 1 ||
            named.Name != "ReadOnlySpan" ||
            named.ContainingNamespace?.ToDisplayString() != "System")
        {
            return false;
        }

        elementType = named.TypeArguments[0];
        return true;
    }

    private static bool TryGetSpanConversionSourceElementType(
        ITypeSymbol type,
        out ITypeSymbol elementType)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            elementType = array.ElementType;
            return true;
        }

        elementType = null!;
        if (type is not INamedTypeSymbol named ||
            named.TypeArguments.Length != 1 ||
            named.ContainingNamespace?.ToDisplayString() != "System" ||
            named.Name is not ("Span" or "ReadOnlySpan"))
        {
            return false;
        }

        elementType = named.TypeArguments[0];
        return true;
    }

    private readonly record struct ConversionCacheKey(
        ITypeSymbol Source,
        ITypeSymbol Destination,
        bool IncludeUserDefined);

    private sealed class ConversionCacheKeyComparer : IEqualityComparer<ConversionCacheKey>
    {
        public bool Equals(ConversionCacheKey x, ConversionCacheKey y)
        {
            return x.IncludeUserDefined == y.IncludeUserDefined &&
                SymbolEqualityComparer.Default.Equals(x.Source, y.Source) &&
                SymbolEqualityComparer.Default.Equals(x.Destination, y.Destination);
        }

        public int GetHashCode(ConversionCacheKey obj)
        {
            return HashCode.Combine(
                SymbolEqualityComparer.Default.GetHashCode(obj.Source),
                SymbolEqualityComparer.Default.GetHashCode(obj.Destination),
                obj.IncludeUserDefined);
        }
    }

    private IEnumerable<IMethodSymbol> GetExtensionConversionCandidates(ITypeSymbol source, ITypeSymbol destination)
    {
        var extensionOperators = GetExtensionConversionOperators();
        if (extensionOperators.IsDefaultOrEmpty)
            yield break;

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var receiverTypes = ImmutableArray.CreateBuilder<ITypeSymbol>(4);
        receiverTypes.Add(source);
        receiverTypes.Add(destination);

        if (source.TryGetUnionCase() is { } sourceUnionCase)
            receiverTypes.Add(sourceUnionCase.Union);
        if (destination.TryGetUnionCase() is { } destinationUnionCase)
            receiverTypes.Add(destinationUnionCase.Union);

        foreach (var method in extensionOperators)
        {
            IMethodSymbol? constructed = null;

            foreach (var receiverType in receiverTypes)
            {
                constructed = TryConstructExtensionConversion(method, receiverType);
                if (constructed is not null)
                    break;
            }
            if (constructed is null && !IsOpenGenericConversionMethod(method))
                constructed = method;

            if (constructed is not null && seen.Add(constructed))
                yield return constructed;
        }

        static bool IsOpenGenericConversionMethod(IMethodSymbol method)
        {
            if (!method.TypeParameters.IsDefaultOrEmpty && method.TypeParameters.Length > 0)
                return true;

            if (method.ContainingType is not INamedTypeSymbol containingType || !containingType.IsGenericType)
                return false;

            var typeArguments = containingType.TypeArguments;
            if (!typeArguments.IsDefaultOrEmpty && typeArguments.Length > 0)
                return typeArguments.Any(static argument => argument is ITypeParameterSymbol);

            return !containingType.TypeParameters.IsDefaultOrEmpty && containingType.TypeParameters.Length > 0;
        }
    }

    private ImmutableArray<IMethodSymbol> GetExtensionConversionOperators()
    {
        if (_extensionConversionOperatorsInitialized)
            return _extensionConversionOperators;

        _extensionConversionOperatorsInitialized = true;

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var members = SymbolLookup.GetExtensionConversionContainers();

        foreach (var type in members)
        {
            foreach (var member in type.GetMembers("op_Implicit").Concat(type.GetMembers("op_Explicit")))
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (method.MethodKind is not MethodKind.Conversion)
                    continue;

                if (!TryGetExtensionConversionReceiverType(method, out _))
                    continue;

                builder.Add(method);
            }
        }

        _extensionConversionOperators = builder.ToImmutable();
        return _extensionConversionOperators;

        static bool TryGetExtensionConversionReceiverType(IMethodSymbol method, out ITypeSymbol? receiverType)
        {
            try
            {
                receiverType = method.GetExtensionReceiverType();
                return receiverType is not null;
            }
            catch (InvalidOperationException)
            {
                receiverType = null;
                return false;
            }
        }
    }

    private IMethodSymbol? TryConstructExtensionConversion(IMethodSymbol method, ITypeSymbol receiverType)
    {
        var extensionReceiverType = method.GetExtensionReceiverType();
        if (extensionReceiverType is null)
            return null;

        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return null;

        var methodDefinition = method.OriginalDefinition ?? method;
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);

        if (!TryUnifyExtensionReceiver(extensionReceiverType, receiverType, substitutions))
            return null;

        if (!methodDefinition.TypeParameters.IsDefaultOrEmpty && methodDefinition.TypeParameters.Length > 0)
        {
            var methodTypeArguments = new ITypeSymbol[methodDefinition.TypeParameters.Length];

            for (int i = 0; i < methodDefinition.TypeParameters.Length; i++)
            {
                var typeParameter = methodDefinition.TypeParameters[i];
                if (!TryGetMethodSubstitution(substitutions, typeParameter, out var typeArgument))
                    return null;

                methodTypeArguments[i] = typeArgument;
            }

            if (!SatisfiesTypeParameterConstraints(methodDefinition.TypeParameters, methodTypeArguments))
                return null;

            if (!SatisfiesContainingTypeConstraints(methodDefinition, substitutions))
                return null;

            return methodDefinition.Construct(methodTypeArguments);
        }

        if (methodDefinition.ContainingType is not INamedTypeSymbol container)
            return null;

        var containerDefinition = container.ConstructedFrom as INamedTypeSymbol ?? container;
        if (!containerDefinition.IsGenericType ||
            containerDefinition.TypeParameters.IsDefaultOrEmpty ||
            containerDefinition.TypeParameters.Length == 0)
        {
            return method;
        }

        var typeArguments = new ITypeSymbol[containerDefinition.TypeParameters.Length];

        for (int i = 0; i < containerDefinition.TypeParameters.Length; i++)
        {
            var typeParameter = containerDefinition.TypeParameters[i];
            if (!substitutions.TryGetValue(typeParameter, out var typeArgument))
                return null;

            typeArguments[i] = typeArgument;
        }

        if (!SatisfiesTypeParameterConstraints(containerDefinition.TypeParameters, typeArguments))
            return null;

        if (containerDefinition.Construct(typeArguments) is not INamedTypeSymbol constructedContainer)
            return null;

        var originalDefinition = methodDefinition;

        foreach (var candidate in constructedContainer.GetMembers(method.Name).OfType<IMethodSymbol>())
        {
            var candidateOriginal = candidate.OriginalDefinition ?? candidate;
            if (SymbolEqualityComparer.Default.Equals(candidateOriginal, originalDefinition))
                return candidate;
        }

        return null;

        static bool TryGetMethodSubstitution(
            Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
            ITypeParameterSymbol methodParameter,
            out ITypeSymbol typeArgument)
        {
            if (substitutions.TryGetValue(methodParameter, out typeArgument))
                return true;

            foreach (var (parameter, argument) in substitutions)
            {
                if (string.Equals(parameter.Name, methodParameter.Name, StringComparison.Ordinal) &&
                    parameter.Ordinal == methodParameter.Ordinal)
                {
                    typeArgument = argument;
                    return true;
                }
            }

            typeArgument = null!;
            return false;
        }
    }

    private static bool SatisfiesTypeParameterConstraints(
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        ITypeSymbol[] typeArguments)
    {
        if (typeParameters.Length != typeArguments.Length)
            return false;

        for (int i = 0; i < typeParameters.Length; i++)
        {
            if (!typeArguments[i].SatisfiesConstraints(typeParameters[i]))
                return false;
        }

        return true;
    }

    private static bool SatisfiesContainingTypeConstraints(
        IMethodSymbol methodDefinition,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (methodDefinition.ContainingType is not INamedTypeSymbol containingType)
            return true;

        var containerDefinition = containingType.ConstructedFrom as INamedTypeSymbol ?? containingType;
        if (!containerDefinition.IsGenericType || containerDefinition.TypeParameters.IsDefaultOrEmpty)
            return true;

        var typeArguments = new ITypeSymbol[containerDefinition.TypeParameters.Length];

        for (int i = 0; i < containerDefinition.TypeParameters.Length; i++)
        {
            var typeParameter = containerDefinition.TypeParameters[i];
            if (!substitutions.TryGetValue(typeParameter, out var typeArgument))
                return false;

            typeArguments[i] = typeArgument;
        }

        return SatisfiesTypeParameterConstraints(containerDefinition.TypeParameters, typeArguments);
    }

    private bool TryUnifyExtensionReceiver(
        ITypeSymbol parameterType,
        ITypeSymbol argumentType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        parameterType = NormalizeTypeForExtensionInference(parameterType);
        argumentType = NormalizeTypeForExtensionInference(argumentType);

        if (parameterType is ITypeParameterSymbol parameter)
            return TryRecordExtensionSubstitution(parameter, argumentType, substitutions);

        if (parameterType is INamedTypeSymbol paramNamed && argumentType is INamedTypeSymbol argNamed)
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

        if (parameterType is IArrayTypeSymbol paramArray && argumentType is IArrayTypeSymbol argArray)
            return TryUnifyExtensionReceiver(paramArray.ElementType, argArray.ElementType, substitutions);

        if (parameterType is NullableTypeSymbol paramNullable)
        {
            if (argumentType is NullableTypeSymbol argNullable)
                return TryUnifyExtensionReceiver(paramNullable.UnderlyingType, argNullable.UnderlyingType, substitutions);

            if (!argumentType.IsValueType)
                return TryUnifyExtensionReceiver(paramNullable.UnderlyingType, argumentType, substitutions);

            return false;
        }

        return SymbolEqualityComparer.Default.Equals(parameterType, argumentType);

        bool TryUnifyNamedType(
            INamedTypeSymbol parameterNamed,
            INamedTypeSymbol? argumentNamed,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
        {
            if (argumentNamed is null)
                return false;

            var parameterDefinition = parameterNamed.OriginalDefinition ?? parameterNamed;
            var argumentDefinition = argumentNamed.OriginalDefinition ?? argumentNamed;

            if (!SymbolEqualityComparer.Default.Equals(parameterDefinition, argumentDefinition))
                return false;

            var parameterArguments = parameterNamed.TypeArguments;
            var argumentArguments = argumentNamed.TypeArguments;

            if (parameterArguments.Length != argumentArguments.Length)
                return false;

            for (int i = 0; i < parameterArguments.Length; i++)
            {
                if (!TryUnifyExtensionReceiver(parameterArguments[i], argumentArguments[i], map))
                    return false;
            }

            return true;
        }
    }

    private bool TryRecordExtensionSubstitution(
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

            if (ClassifyConversion(argumentType, existing, includeUserDefined: false).IsImplicit)
                return true;

            if (ClassifyConversion(existing, argumentType, includeUserDefined: false).IsImplicit)
            {
                substitutions[typeParameter] = argumentType;
                return true;
            }

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
            _ => type
        };
    }

    private static IMethodSymbol? FindDiscriminatedUnionConversionMethod(ITypeSymbol source, ITypeSymbol destination)
    {
        if (destination is INamedTypeSymbol destinationNamed)
        {
            var method = FindMatchingConversion(destinationNamed, source, destination);
            if (method is not null)
                return method;
        }

        if (source is INamedTypeSymbol sourceNamed)
        {
            var method = FindMatchingConversion(sourceNamed, source, destination);
            if (method is not null)
                return method;
        }

        return null;

        static IMethodSymbol? FindMatchingConversion(
            INamedTypeSymbol owner,
            ITypeSymbol sourceType,
            ITypeSymbol destinationType)
        {
            foreach (var member in owner.GetMembers("op_Implicit"))
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (method.MethodKind is not MethodKind.Conversion)
                    continue;

                if (method.Parameters.Length != 1)
                    continue;

                var parameterType = method.Parameters[0].Type;
                if (parameterType is null || method.ReturnType is null)
                    continue;

                if (!SymbolEqualityComparer.Default.Equals(parameterType, sourceType) &&
                    !SymbolEqualityComparer.Default.Equals(
                        parameterType.OriginalDefinition ?? parameterType,
                        sourceType.OriginalDefinition ?? sourceType))
                    continue;

                if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, destinationType) &&
                    !SymbolEqualityComparer.Default.Equals(
                        method.ReturnType.OriginalDefinition ?? method.ReturnType,
                        destinationType.OriginalDefinition ?? destinationType))
                    continue;

                return method;
            }

            return null;
        }
    }

    private IMethodSymbol? FindUnionCarrierConstructor(ITypeSymbol source, ITypeSymbol destination)
    {
        if (destination is not INamedTypeSymbol destinationNamed)
            return null;

        var sourceCase = source.TryGetUnionCase();
        var sourceDefinition = source.OriginalDefinition ?? source;
        var sourceCaseDefinition = (sourceCase as ITypeSymbol)?.OriginalDefinition ?? sourceCase as ITypeSymbol;

        foreach (var candidate in destinationNamed.Constructors)
        {
            if (candidate.IsStatic || candidate.Parameters.Length != 1)
                continue;

            var parameterType = candidate.Parameters[0].Type;
            if (SymbolEqualityComparer.Default.Equals(parameterType, source))
                return EnsureConstructedConstructor(candidate, destinationNamed);

            var parameterDefinition = parameterType.OriginalDefinition ?? parameterType;
            if (SymbolEqualityComparer.Default.Equals(parameterDefinition, sourceDefinition) &&
                TypeArgumentsAreCompatible(source, parameterType))
            {
                return EnsureConstructedConstructor(candidate, destinationNamed);
            }

            if (sourceCaseDefinition is not null &&
                SymbolEqualityComparer.Default.Equals(parameterDefinition, sourceCaseDefinition) &&
                TypeArgumentsAreCompatible(source, parameterType))
            {
                return EnsureConstructedConstructor(candidate, destinationNamed);
            }

            var parameterConversion = ClassifyConversion(source, parameterType, includeUserDefined: false);
            if (parameterConversion is { Exists: true, IsImplicit: true })
                return EnsureConstructedConstructor(candidate, destinationNamed);
        }

        return null;

        static IMethodSymbol EnsureConstructedConstructor(IMethodSymbol constructorSymbol, INamedTypeSymbol targetUnionType)
        {
            if (constructorSymbol is SubstitutedMethodSymbol or ConstructedMethodSymbol)
                return constructorSymbol;

            if (targetUnionType is not ConstructedNamedTypeSymbol constructedType)
                return constructorSymbol;

            if (constructedType.ConstructedFrom is not INamedTypeSymbol constructedDefinition)
                return constructorSymbol;

            if (constructorSymbol.ContainingType is not INamedTypeSymbol constructorContainingType)
                return constructorSymbol;

            var constructorContainingDefinition = constructorContainingType.OriginalDefinition as INamedTypeSymbol ?? constructorContainingType;
            if (!SymbolEqualityComparer.Default.Equals(constructorContainingDefinition, constructedDefinition))
                return constructorSymbol;

            return new SubstitutedMethodSymbol(constructorSymbol, constructedType);
        }
    }

    private static bool TypeArgumentsAreCompatible(ITypeSymbol sourceType, ITypeSymbol destinationType)
    {
        if (sourceType is not INamedTypeSymbol sourceNamed ||
            destinationType is not INamedTypeSymbol destinationNamed)
        {
            return true;
        }

        var sourceArguments = sourceNamed.TypeArguments;
        var destinationArguments = destinationNamed.TypeArguments;

        if (sourceArguments.Length != destinationArguments.Length)
            return false;

        for (var i = 0; i < sourceArguments.Length; i++)
        {
            if (sourceArguments[i] is ITypeParameterSymbol ||
                destinationArguments[i] is ITypeParameterSymbol)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(sourceArguments[i], destinationArguments[i]))
                return false;
        }

        return true;
    }

    private static IMethodSymbol? FindUnionTryGetValueMethod(INamedTypeSymbol unionType, ITypeSymbol memberType)
    {
        foreach (var method in unionType.GetMembers("TryGetValue").OfType<IMethodSymbol>())
        {
            if (method.IsStatic || method.Parameters.Length != 1)
                continue;

            var parameter = method.Parameters[0];
            if (parameter.RefKind != RefKind.Out)
                continue;

            if (SymbolEqualityComparer.Default.Equals(parameter.Type, memberType))
                return method;

            var parameterDefinition = parameter.Type.OriginalDefinition ?? parameter.Type;
            var memberDefinition = memberType.OriginalDefinition ?? memberType;
            if (SymbolEqualityComparer.Default.Equals(parameterDefinition, memberDefinition))
                return method;
        }

        return null;
    }

    private bool IsReferenceConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        if (source.IsValueType || destination.IsValueType)
            return false;

        if (source is NullableTypeSymbol { UnderlyingType.IsValueType: false } &&
            destination is not NullableTypeSymbol)
        {
            return false;
        }

        if (source.MetadataIdentityEquals(destination))
            return false;

        if (source is INamedTypeSymbol { TypeKind: TypeKind.Delegate } sourceDelegate &&
            destination is INamedTypeSymbol { TypeKind: TypeKind.Delegate } destinationDelegate &&
            sourceDelegate.OriginalDefinition is INamedTypeSymbol delegateDefinition &&
            delegateDefinition.MetadataIdentityEquals(destinationDelegate.OriginalDefinition))
        {
            var parameters = delegateDefinition.TypeParameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                var from = sourceDelegate.TypeArguments[i];
                var to = destinationDelegate.TypeArguments[i];
                if (SymbolEqualityComparer.Default.Equals(from, to))
                    continue;

                if (parameters[i].Variance == VarianceKind.None || from.IsValueType || to.IsValueType)
                    return false;

                var conversion = parameters[i].Variance == VarianceKind.Out
                    ? ClassifyConversion(from, to, includeUserDefined: false)
                    : ClassifyConversion(to, from, includeUserDefined: false);
                if (!conversion.IsImplicit || !(conversion.IsIdentity || conversion.IsReference))
                    return false;
            }

            return true;
        }

        var current = source.BaseType;
        while (current is not null)
        {
            if (current.MetadataIdentityEquals(destination))
                return true;

            current = current.BaseType;
        }

        if (destination is INamedTypeSymbol destinationNamed &&
            destinationNamed.TypeKind == TypeKind.Interface)
        {
            if (SemanticFacts.ImplementsInterface(source, destinationNamed, SymbolEqualityComparer.Default))
                return true;
        }

        if (source.TypeKind == TypeKind.Interface && destination.SpecialType is SpecialType.System_Object)
            return true;

        return false;
    }

    private bool IsExplicitReferenceConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        if (source.IsValueType || destination.IsValueType)
            return false;

        if (source.MetadataIdentityEquals(destination))
            return false;

        var comparer = SymbolEqualityComparer.Default;

        if (SemanticFacts.IsDerivedFrom(destination, source, comparer))
            return true;

        if (source.SpecialType is SpecialType.System_Object && !destination.IsValueType)
            return true;

        if (source is INamedTypeSymbol sourceInterface && sourceInterface.TypeKind == TypeKind.Interface)
        {
            if (destination.SpecialType is SpecialType.System_Object)
                return true;

            if (destination is INamedTypeSymbol destinationNamed &&
                destinationNamed.TypeKind != TypeKind.Interface &&
                SemanticFacts.ImplementsInterface(destinationNamed, sourceInterface, comparer))
            {
                return true;
            }

            if (destination is INamedTypeSymbol sourceToTargetInterface && sourceToTargetInterface.TypeKind == TypeKind.Interface &&
                (SemanticFacts.ImplementsInterface(sourceInterface, sourceToTargetInterface, comparer) ||
                 SemanticFacts.ImplementsInterface(sourceToTargetInterface, sourceInterface, comparer)))
            {
                return true;
            }
        }

        if (destination is INamedTypeSymbol targetInterface && targetInterface.TypeKind == TypeKind.Interface &&
            SemanticFacts.ImplementsInterface(source, targetInterface, comparer))
        {
            return true;
        }

        return false;
    }

    private bool IsBoxingConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        if (!source.IsValueType)
            return false;

        if (destination.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
            return true;

        if (source.TypeKind == TypeKind.Enum && destination.SpecialType == SpecialType.System_Enum)
            return true;

        return destination is INamedTypeSymbol { TypeKind: TypeKind.Interface } targetInterface &&
               SemanticFacts.ImplementsInterface(source, targetInterface, SymbolEqualityComparer.Default);
    }

    private bool IsUnboxingConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        if (!destination.IsValueType)
            return false;

        if (source.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
            return true;

        if (destination.TypeKind == TypeKind.Enum && source.SpecialType == SpecialType.System_Enum)
            return true;

        return source is INamedTypeSymbol { TypeKind: TypeKind.Interface } sourceInterface &&
               SemanticFacts.ImplementsInterface(destination, sourceInterface, SymbolEqualityComparer.Default);
    }

    private bool IsImplicitNumericConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        var sourceType = source.SpecialType;
        var destType = destination.SpecialType;

        // NOTE:
        // - No implicit conversion between decimal and float/double in C#.
        // - C# treats byte and char as numeric for conversion purposes.
        // - We include integral/char -> double and integral/char -> decimal to support binary numeric promotion.

        return (sourceType, destType) switch
        {
            // -------- integral widening --------
            (SpecialType.System_Byte, SpecialType.System_Int32) => true,
            (SpecialType.System_Byte, SpecialType.System_Int64) => true,

            (SpecialType.System_Char, SpecialType.System_Int32) => true,
            (SpecialType.System_Char, SpecialType.System_Int64) => true,

            (SpecialType.System_Int32, SpecialType.System_Int64) => true,

            // -------- to floating --------
            (SpecialType.System_Byte, SpecialType.System_Single) => true,
            (SpecialType.System_Byte, SpecialType.System_Double) => true,

            (SpecialType.System_Char, SpecialType.System_Single) => true,
            (SpecialType.System_Char, SpecialType.System_Double) => true,

            (SpecialType.System_Int32, SpecialType.System_Single) => true,
            (SpecialType.System_Int32, SpecialType.System_Double) => true,
            (SpecialType.System_Int64, SpecialType.System_Single) => true,
            (SpecialType.System_Int64, SpecialType.System_Double) => true,
            (SpecialType.System_Single, SpecialType.System_Double) => true,

            // -------- to decimal (C# allows implicit integral/char -> decimal) --------
            (SpecialType.System_Byte, SpecialType.System_Decimal) => true,
            (SpecialType.System_Char, SpecialType.System_Decimal) => true,
            (SpecialType.System_Int32, SpecialType.System_Decimal) => true,
            (SpecialType.System_Int64, SpecialType.System_Decimal) => true,

            _ => false
        };
    }

    private static bool IsIntegralType(ITypeSymbol type)
        => type.SpecialType is
            SpecialType.System_SByte or
            SpecialType.System_Byte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Char;

    private bool IsExplicitNumericConversion(ITypeSymbol source, ITypeSymbol destination)
    {
        var sourceType = source.SpecialType;
        var destType = destination.SpecialType;

        return (sourceType, destType) switch
        {
            // -------- to byte --------
            (SpecialType.System_Int32, SpecialType.System_Byte) => true,
            (SpecialType.System_Int64, SpecialType.System_Byte) => true,
            (SpecialType.System_Char, SpecialType.System_Byte) => true,
            (SpecialType.System_Single, SpecialType.System_Byte) => true,
            (SpecialType.System_Double, SpecialType.System_Byte) => true,
            (SpecialType.System_Decimal, SpecialType.System_Byte) => true,

            // -------- to char --------
            (SpecialType.System_Byte, SpecialType.System_Char) => true,
            (SpecialType.System_Int32, SpecialType.System_Char) => true,
            (SpecialType.System_Int64, SpecialType.System_Char) => true,
            (SpecialType.System_Single, SpecialType.System_Char) => true,
            (SpecialType.System_Double, SpecialType.System_Char) => true,
            (SpecialType.System_Decimal, SpecialType.System_Char) => true,

            // -------- floating to integral --------
            (SpecialType.System_Double, SpecialType.System_Int32) => true,
            (SpecialType.System_Double, SpecialType.System_Int64) => true,
            (SpecialType.System_Single, SpecialType.System_Int32) => true,
            (SpecialType.System_Single, SpecialType.System_Int64) => true,

            // -------- integral narrowing --------
            (SpecialType.System_Int64, SpecialType.System_Int32) => true,

            // -------- decimal to integral --------
            (SpecialType.System_Decimal, SpecialType.System_Int32) => true,
            (SpecialType.System_Decimal, SpecialType.System_Int64) => true,

            // -------- decimal <-> floating are explicit only in C# --------
            (SpecialType.System_Decimal, SpecialType.System_Double) => true,
            (SpecialType.System_Double, SpecialType.System_Decimal) => true,
            (SpecialType.System_Decimal, SpecialType.System_Single) => true,
            (SpecialType.System_Single, SpecialType.System_Decimal) => true,

            // explicit from double to float
            (SpecialType.System_Double, SpecialType.System_Single) => true,

            _ => false
        };
    }
}
