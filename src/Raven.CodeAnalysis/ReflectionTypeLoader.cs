using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

internal class ReflectionTypeLoader(Compilation compilation)
{
    private readonly ConcurrentDictionary<Type, ITypeSymbol> _cache = new();
    private readonly ConcurrentDictionary<string, ITypeSymbol> _metadataCache = new(StringComparer.Ordinal);
    private readonly NullabilityInfoContext _nullabilityContext = new();
    private readonly object _nullabilityContextGate = new();
    private readonly ConcurrentDictionary<MethodIdentity, PEMethodSymbol> _methodSymbols = new();
    private readonly ConcurrentDictionary<(PEMethodSymbol method, Type parameter), ITypeParameterSymbol> _methodTypeParameters = new();

    internal Compilation Compilation => compilation;

    public ITypeSymbol? ResolveType(ParameterInfo parameterInfo)
    {
        var methodContext = parameterInfo.Member as MethodBase;
        var parameterType = parameterInfo.ParameterType;
        var useWriteState = parameterInfo.Position < 0 || parameterInfo.IsOut;
        if (IsRuntimeVoid(parameterType))
            return compilation.GetSpecialType(SpecialType.System_Unit);

        var attributes = parameterInfo.GetCustomAttributesData();

        if (parameterType.IsByRef)
        {
            var elementType = ResolveType(parameterType.GetElementType()!, methodContext);
            if (elementType is null)
                return null;

            if (TryGetExplicitNullableFlags(attributes, out var explicitFlags))
                elementType = ApplyExplicitNullableFlags(elementType, explicitFlags);
            if (TryCreateNullabilityInfo(parameterInfo, out var nullInfo))
                elementType = ApplyNullability(elementType, nullInfo.ElementType ?? nullInfo, useWriteState);

            return elementType;
        }

        var declaredType = ResolveType(parameterType, methodContext);

        var type = declaredType;
        if (TryGetExplicitNullableFlags(attributes, out var parameterFlags))
            type = ApplyExplicitNullableFlags(type!, parameterFlags);

        if (TryCreateNullabilityInfo(parameterInfo, out var parameterNullInfo))
            type = ApplyNullability(type!, parameterNullInfo, useWriteState);
        return ApplyFixedArrayMetadata(type, TryGetFixedLengthArray(attributes));
    }

    public ITypeSymbol? ResolveType(FieldInfo fieldInfo)
    {
        var attributes = fieldInfo.GetCustomAttributesData();
        var type = ResolveType(fieldInfo.FieldType);
        if (TryGetExplicitNullableFlags(attributes, out var fieldFlags))
            type = ApplyExplicitNullableFlags(type!, fieldFlags);

        if (TryCreateNullabilityInfo(fieldInfo, out var nullInfo))
            type = ApplyNullability(type!, nullInfo);
        return ApplyFixedArrayMetadata(type, TryGetFixedLengthArray(attributes));
    }

    public ITypeSymbol? ResolveType(PropertyInfo propertyInfo)
    {
        var attributes = propertyInfo.GetCustomAttributesData();
        var type = ResolveType(propertyInfo.PropertyType);
        if (TryGetExplicitNullableFlags(attributes, out var propertyFlags))
            type = ApplyExplicitNullableFlags(type!, propertyFlags);

        if (TryCreateNullabilityInfo(propertyInfo, out var nullInfo))
            type = ApplyNullability(type!, nullInfo);
        return ApplyFixedArrayMetadata(type, TryGetFixedLengthArray(attributes));
    }

    public ITypeSymbol? ResolveType(EventInfo eventInfo)
    {
        var handlerType = eventInfo.EventHandlerType;
        if (handlerType is null)
            return null;

        var attributes = eventInfo.GetCustomAttributesData();
        var type = ResolveType(handlerType);
        if (TryGetExplicitNullableFlags(attributes, out var eventFlags))
            type = ApplyExplicitNullableFlags(type!, eventFlags);

        if (TryCreateNullabilityInfo(eventInfo, out var nullInfo))
            type = ApplyNullability(type!, nullInfo);
        return type;
    }

    private bool TryCreateNullabilityInfo(ParameterInfo parameterInfo, out System.Reflection.NullabilityInfo nullabilityInfo)
    {
        try
        {
            lock (_nullabilityContextGate)
                nullabilityInfo = _nullabilityContext.Create(parameterInfo);
            return true;
        }
        catch (Exception exception) when (IsUnavailableNullabilityReflection(exception))
        {
            nullabilityInfo = null!;
            return false;
        }
    }

    private bool TryCreateNullabilityInfo(FieldInfo fieldInfo, out System.Reflection.NullabilityInfo nullabilityInfo)
    {
        try
        {
            lock (_nullabilityContextGate)
                nullabilityInfo = _nullabilityContext.Create(fieldInfo);
            return true;
        }
        catch (Exception exception) when (IsUnavailableNullabilityReflection(exception))
        {
            nullabilityInfo = null!;
            return false;
        }
    }

    private bool TryCreateNullabilityInfo(PropertyInfo propertyInfo, out System.Reflection.NullabilityInfo nullabilityInfo)
    {
        try
        {
            lock (_nullabilityContextGate)
                nullabilityInfo = _nullabilityContext.Create(propertyInfo);
            return true;
        }
        catch (Exception exception) when (IsUnavailableNullabilityReflection(exception))
        {
            nullabilityInfo = null!;
            return false;
        }
    }

    private bool TryCreateNullabilityInfo(EventInfo eventInfo, out System.Reflection.NullabilityInfo nullabilityInfo)
    {
        try
        {
            lock (_nullabilityContextGate)
                nullabilityInfo = _nullabilityContext.Create(eventInfo);
            return true;
        }
        catch (Exception exception) when (IsUnavailableNullabilityReflection(exception))
        {
            nullabilityInfo = null!;
            return false;
        }
    }

    internal static bool IsUnavailableNullabilityReflection(Exception exception)
        => exception is NotSupportedException or NotImplementedException;

    public FieldInfo? ResolveRuntimeField(FieldInfo fieldInfo)
    {
        if (fieldInfo is null)
            throw new ArgumentNullException(nameof(fieldInfo));

        var declaringType = fieldInfo.DeclaringType?.GetTypeInfo();
        if (declaringType is null)
            return null;

        var runtimeType = compilation.ResolveRuntimeType(declaringType);
        if (runtimeType is null)
            return null;

        var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic |
                           (fieldInfo.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

        return runtimeType.GetField(fieldInfo.Name, bindingFlags);
    }

    public void RegisterMethodSymbol(MethodBase method, PEMethodSymbol symbol)
    {
        _methodSymbols[MethodIdentity.Create(method)] = symbol;
    }

    public ITypeSymbol? ResolveType(Type type)
        => ResolveType(type, null);

    public ITypeSymbol? ResolveType(Type type, MethodBase? methodContext)
    {
        if (IsRuntimeVoid(type))
        {
            var unit = compilation.GetSpecialType(SpecialType.System_Unit);
            var hasVoidMetadataCacheKey = TryGetMetadataCacheKey(type, out var voidMetadataCacheKey);
            CacheResolvedType(type, hasVoidMetadataCacheKey ? voidMetadataCacheKey : null, unit);
            return unit;
        }

        if (_cache.TryGetValue(type, out var cached))
            return cached;

        var hasMetadataCacheKey = TryGetMetadataCacheKey(type, out var metadataCacheKey);
        if (hasMetadataCacheKey && _metadataCache.TryGetValue(metadataCacheKey!, out var metadataCached))
        {
            _cache[type] = metadataCached;
            return metadataCached;
        }

        if (type.IsNullableValueType())
        {
            var underlying = ResolveType(type.GetGenericArguments()[0], methodContext);
            var nullable = underlying!.GetNullableType();
            CacheResolvedType(type, metadataCacheKey, nullable);
            return nullable;
        }

        // TODO: Return immediately if built in type

        if (type.IsByRef)
        {
            var element = ResolveType(type.GetElementType()!, methodContext);
            if (element is null)
                return null;

            var byRef = new RefTypeSymbol(element);
            CacheResolvedType(type, metadataCacheKey, byRef);
            return byRef;
        }

        if (type.IsPointer)
        {
            var element = ResolveType(type.GetElementType()!, methodContext);
            if (element is null)
                return null;

            var pointer = new PointerTypeSymbol(element);
            CacheResolvedType(type, metadataCacheKey, pointer);
            return pointer;
        }

        if (type.IsGenericTypeDefinition)
        {
            var definition = CanonicalizeSpecialTypeDefinition(ResolveTypeCore(type));
            CacheResolvedType(type, metadataCacheKey, definition);
            return definition;
        }

        if (type.IsGenericType)
        {
            // Important:
            // - For *nested* types, reflection may report generic arguments that include the declaring type's
            //   arguments. The correct way to resolve these is to rebuild the declaring-type chain and
            //   anchor the nested type under the constructed declaring type.
            // - For *non-nested* generic types (e.g. TaskAwaiter<TResult>), we can resolve the generic
            //   definition and construct it directly.

            if (type.RequiresNestedChainResolution())
                return ResolveNestedTypeChain(type, methodContext);

            var genericTypeDefinition = (INamedTypeSymbol?)ResolveType(type.GetGenericTypeDefinition(), methodContext);
            if (genericTypeDefinition is null)
                return null;

            var args = type.GetGenericArguments().Select(x => ResolveType(x, methodContext)!).ToArray();
            var constructed = TryConstructNamedType(genericTypeDefinition, args);
            if (constructed is null)
                return compilation.ErrorTypeSymbol;

            CacheResolvedType(type, metadataCacheKey, constructed);
            return constructed;
        }

        if (type.IsGenericMethodParameter)
        {
            var method = methodContext ?? type.DeclaringMethod;
            if (method is null)
                throw new InvalidOperationException($"Unable to resolve declaring method for type parameter: {type}");

            if (!_methodSymbols.TryGetValue(MethodIdentity.Create(method), out var methodSymbol))
                throw new InvalidOperationException($"Method symbol not registered for {method}.");

            return ResolveMethodTypeParameter(type, methodSymbol);
        }

        if (type.IsGenericTypeParameter)
        {
            if (ResolveType(type.DeclaringType!) is not INamedTypeSymbol declaringNamedType)
                throw new InvalidOperationException($"Could not resolve declaring type for type parameter: {type}");

            return ResolveTypeParameter(type, declaringNamedType);
        }

        if (type.IsArray)
        {
            var runtimeElementType = type.GetElementType()
                ?? throw new InvalidOperationException($"Array type '{type}' has no element type.");
            var elementType = ResolveType(runtimeElementType)
                ?? throw new InvalidOperationException($"Could not resolve element type '{runtimeElementType}' for array type '{type}'.");
            return new ArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Array), elementType, null, null, null, [], type.GetArrayRank());
        }

        var symbol = CanonicalizeSpecialTypeDefinition(ResolveTypeCore(type));

        CacheResolvedType(type, metadataCacheKey, symbol!);

        return symbol;
    }

    internal ITypeParameterSymbol ResolveTypeParameter(Type type, INamedTypeSymbol declaringType)
    {
        if (_cache.TryGetValue(type, out var cached))
            return (ITypeParameterSymbol)cached;

        // CLR nested types repeat the enclosing type's parameters. Canonicalize
        // those inherited slots to their declaring owner before substitution.
        if (type.DeclaringType?.DeclaringType is { } enclosingType)
        {
            var enclosingArguments = enclosingType.GetGenericArguments();
            if (type.GenericParameterPosition < enclosingArguments.Length &&
                ResolveType(enclosingArguments[type.GenericParameterPosition]) is ITypeParameterSymbol inherited)
            {
                return (ITypeParameterSymbol)_cache.GetOrAdd(type, inherited);
            }
        }

        var symbol = new PETypeParameterSymbol(
            type,
            declaringType,
            declaringType,
            declaringType.ContainingNamespace,
            [new MetadataLocation(declaringType.ContainingModule!)],
            this).AddAsMember2();

        return (ITypeParameterSymbol)_cache.GetOrAdd(type, symbol);
    }

    private ITypeSymbol CanonicalizeSpecialTypeDefinition(ITypeSymbol symbol)
    {
        // Collection contracts commonly cross the System.Runtime facade boundary in
        // metadata signatures. Intern them before constructing closed receiver types so
        // extension lookup observes one compilation-owned definition.
        if (symbol is not INamedTypeSymbol namedType ||
            namedType.SpecialType is not (
                SpecialType.System_Collections_IEnumerable or
                SpecialType.System_Collections_Generic_IEnumerable_T or
                SpecialType.System_Collections_Generic_IList_T or
                SpecialType.System_Collections_Generic_ICollection_T or
                SpecialType.System_Collections_IEnumerator or
                SpecialType.System_Collections_Generic_IEnumerator_T))
        {
            return symbol;
        }

        var canonicalType = compilation.GetSpecialType(namedType.SpecialType);
        return canonicalType is IErrorTypeSymbol ? symbol : canonicalType;
    }

    private INamedTypeSymbol ResolveNestedTypeChain(Type t, MethodBase? methodContext)
    {
        // 1) chain outermost..innermost
        var chain = t.DeclaringTypeChain();

        // 2) slice args per level (declared arity per level)
        var slices = t.SliceGenericArgumentsPerLevel(chain);

        // 3) resolve outermost and walk down
        var resolvedOuter = ResolveType(chain[0], methodContext);
        if (resolvedOuter is not INamedTypeSymbol outerNamed)
            return (INamedTypeSymbol)compilation.ErrorTypeSymbol;

        INamedTypeSymbol current = outerNamed;

        if (slices[0].Length > 0)
        {
            var constructedOuter = TryConstructNamedType(current, slices[0].Select(x => ResolveType(x, methodContext)!).ToArray());
            if (constructedOuter is null)
                return (INamedTypeSymbol)compilation.ErrorTypeSymbol;

            current = constructedOuter;
        }

        var str = current.ToString();

        for (int i = 1; i < chain.Count; i++)
        {
            var levelType = chain[i];

            // Find nested under current containing symbol
            var nestedDef = current.FindNestedTypeForRuntime(levelType);
            if (nestedDef is null)
                return (INamedTypeSymbol)compilation.ErrorTypeSymbol;

            current = nestedDef;

            if (slices[i].Length > 0)
            {
                var constructedNested = TryConstructNamedType(current, slices[i].Select(x => ResolveType(x, methodContext)!).ToArray());
                if (constructedNested is null)
                    return (INamedTypeSymbol)compilation.ErrorTypeSymbol;

                current = constructedNested;
            }
        }

        return current;
    }

    internal ITypeParameterSymbol ResolveMethodTypeParameter(Type type, PEMethodSymbol methodSymbol)
    {
        var key = (methodSymbol, type);

        if (_methodTypeParameters.TryGetValue(key, out var existing))
            return existing;

        var symbol = new PETypeParameterSymbol(type, methodSymbol, methodSymbol.ContainingType, methodSymbol.ContainingNamespace, [new MetadataLocation(methodSymbol.ContainingModule!)], this).AddAsMember2();
        _methodTypeParameters[key] = symbol;
        return symbol;
    }

    private ITypeSymbol ApplyNullability(
        ITypeSymbol typeSymbol,
        System.Reflection.NullabilityInfo nullInfo,
        bool useWriteState = false)
    {
        if (typeSymbol is IArrayTypeSymbol array && nullInfo.ElementType is not null)
        {
            var element = ApplyNullability(array.ElementType, nullInfo.ElementType, useWriteState);
            if (!ReferenceEquals(element, array.ElementType))
                typeSymbol = compilation.CreateArrayTypeSymbol(element);
        }
        else if (typeSymbol is INamedTypeSymbol named && nullInfo.GenericTypeArguments.Length > 0)
        {
            var typeArgs = named.TypeArguments.ToArray();
            var changed = false;
            var len = Math.Min(typeArgs.Length, nullInfo.GenericTypeArguments.Length);
            for (int i = 0; i < len; i++)
            {
                var newArg = ApplyNullability(typeArgs[i], nullInfo.GenericTypeArguments[i], useWriteState);
                if (!ReferenceEquals(newArg, typeArgs[i]))
                {
                    typeArgs[i] = newArg;
                    changed = true;
                }
            }

            if (changed)
            {
                var reconstructed = TryConstructNamedType(named, typeArgs);
                if (reconstructed is not null)
                    typeSymbol = reconstructed;
            }
        }

        var declaredState = useWriteState ? nullInfo.WriteState : nullInfo.ReadState;
        if (declaredState == NullabilityState.Nullable
            && typeSymbol is not NullableTypeSymbol
            && typeSymbol is not RefTypeSymbol
            && !typeSymbol.IsValueType
            && typeSymbol is not ITypeParameterSymbol)
        {
            typeSymbol = typeSymbol.GetNullableType();
        }

        return typeSymbol;
    }

    private ITypeSymbol ApplyExplicitNullableFlags(ITypeSymbol typeSymbol, NullableTransformFlags flags)
    {
        var index = 0;
        return ApplyExplicitNullableFlags(typeSymbol, flags, ref index);
    }

    private ITypeSymbol ApplyExplicitNullableFlags(ITypeSymbol typeSymbol, NullableTransformFlags flags, ref int index)
    {
        if (typeSymbol is NullableTypeSymbol { UnderlyingType.IsValueType: true } nullableValue)
        {
            var underlying = ApplyExplicitNullableFlags(nullableValue.UnderlyingType, flags, ref index);
            return ReferenceEquals(underlying, nullableValue.UnderlyingType)
                ? typeSymbol
                : underlying.GetNullableType();
        }

        var consumesFlag = !typeSymbol.IsValueType ||
            typeSymbol is INamedTypeSymbol { TypeArguments.Length: > 0 };
        var flag = consumesFlag ? flags.GetValue(index++) : (byte)0;

        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementType = ApplyExplicitNullableFlags(arrayType.ElementType, flags, ref index);
            if (!ReferenceEquals(elementType, arrayType.ElementType))
                typeSymbol = compilation.CreateArrayTypeSymbol(elementType, arrayType.Rank, arrayType.FixedLength);
        }
        else if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            var typeArguments = namedType.TypeArguments.ToArray();
            var changed = false;

            for (int i = 0; i < typeArguments.Length; i++)
            {
                var updated = ApplyExplicitNullableFlags(typeArguments[i], flags, ref index);
                if (!ReferenceEquals(updated, typeArguments[i]))
                {
                    typeArguments[i] = updated;
                    changed = true;
                }
            }

            if (changed)
            {
                var reconstructed = TryConstructNamedType(namedType, typeArguments);
                if (reconstructed is not null)
                    typeSymbol = reconstructed;
            }
        }

        if (flag == 2 && typeSymbol is not NullableTypeSymbol && !typeSymbol.IsValueType)
            typeSymbol = typeSymbol.GetNullableType();

        return typeSymbol;
    }

    private static bool TryGetExplicitNullableFlags(IList<CustomAttributeData> attributes, out NullableTransformFlags flags)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName != "System.Runtime.CompilerServices.NullableAttribute" ||
                attribute.ConstructorArguments.Count != 1)
            {
                continue;
            }

            var argument = attribute.ConstructorArguments[0];
            if (argument.Value is byte single)
            {
                flags = new NullableTransformFlags([single], IsUniform: true);
                return true;
            }

            if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> many)
            {
                flags = new NullableTransformFlags(
                    many.Select(x => (byte)x.Value!).ToImmutableArray(),
                    IsUniform: false);
                return true;
            }
        }

        flags = default;
        return false;
    }

    private readonly record struct NullableTransformFlags(ImmutableArray<byte> Values, bool IsUniform)
    {
        public byte GetValue(int index)
        {
            if (Values.IsDefaultOrEmpty)
                return 0;

            if (IsUniform)
                return Values[0];

            return index < Values.Length ? Values[index] : (byte)0;
        }
    }

    private ITypeSymbol ApplyFixedArrayMetadata(ITypeSymbol typeSymbol, int? fixedLength)
    {
        if (fixedLength is null || typeSymbol is not IArrayTypeSymbol arrayType || arrayType.Rank != 1)
            return typeSymbol;

        return compilation.CreateArrayTypeSymbol(arrayType.ElementType, arrayType.Rank, fixedLength);
    }

    private static int? TryGetFixedLengthArray(IList<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName is not ("System.Runtime.CompilerServices.FixedLengthArrayAttribute" or "System.Runtime.CompilerServices.FixedSizeArrayAttribute"))
                continue;

            if (attribute.ConstructorArguments is [{ Value: int size }])
                return size;
        }

        return null;
    }

    private INamedTypeSymbol? TryConstructNamedType(INamedTypeSymbol definition, ITypeSymbol[] typeArguments)
    {
        if (definition is IErrorTypeSymbol)
            return null;

        try
        {
            return (INamedTypeSymbol)definition.Construct(typeArguments);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    protected ITypeSymbol ResolveTypeCore(Type type)
    {
        var typeInfo = type.GetTypeInfo();
        var metadataName = GetMetadataName(typeInfo);
        if (string.IsNullOrEmpty(metadataName))
            return compilation.ErrorTypeSymbol;

        var corLibrary = (PEAssemblySymbol)compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
        var assemblyIdentity = type.Assembly.GetName().Name;
        var preferredAssemblyNames = GetPreferredAssemblyNames(assemblyIdentity);
        var assemblySymbol = preferredAssemblyNames
            .Select(name => (PEAssemblySymbol?)compilation.ReferencedAssemblySymbols.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(x => x is not null)
            ?? corLibrary;

        if (assemblySymbol is null)
            return compilation.GetTypeByMetadataName(metadataName) ?? compilation.ErrorTypeSymbol;

        var metadataMatch = assemblySymbol.GetTypeByMetadataName(metadataName);
        if (metadataMatch is not null)
            return metadataMatch;

        // Fallback to Type-based interning when metadata lookup cannot find a symbol in the preferred assembly.
        if (assemblySymbol.PrimaryModule is PEModuleSymbol peModule)
        {
            var r = peModule.GetType(type);
            if (r is not null)
            {
                return r;
            }
        }

        return compilation.GetTypeByMetadataName(metadataName)
            ?? compilation.ErrorTypeSymbol;
    }

    private static IEnumerable<string> GetPreferredAssemblyNames(string? assemblyName)
    {
        if (!string.IsNullOrEmpty(assemblyName))
            yield return assemblyName;

        if (string.Equals(assemblyName, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
            yield return "System.Runtime";

        if (string.Equals(assemblyName, "System.Runtime", StringComparison.OrdinalIgnoreCase))
            yield return "System.Private.CoreLib";
    }

    private static bool TryGetMetadataCacheKey(Type type, out string? key)
    {
        key = null;

        if (type.IsGenericParameter || type.ContainsGenericParameters)
            return false;

        var metadataName = GetMetadataName(type);
        if (string.IsNullOrEmpty(metadataName))
            return false;

        var assemblyName = type.Assembly.GetName().Name;
        key = $"{assemblyName ?? string.Empty}:{metadataName}";
        return true;
    }

    private void CacheResolvedType(Type runtimeType, string? metadataCacheKey, ITypeSymbol symbol)
    {
        _cache[runtimeType] = symbol;

        if (!string.IsNullOrEmpty(metadataCacheKey))
            _metadataCache[metadataCacheKey] = symbol;
    }

    private static bool IsRuntimeVoid(Type type)
        => type == typeof(void) ||
           string.Equals(type.FullName, "System.Void", StringComparison.Ordinal) ||
           (string.Equals(type.Name, "Void", StringComparison.Ordinal) &&
            string.Equals(type.Namespace, "System", StringComparison.Ordinal));

    private static string? GetMetadataName(Type type)
    {
        if (type.DeclaringType is { } declaringType)
        {
            var declaringName = GetMetadataName(declaringType);
            if (declaringName is null)
                return null;

            var (nestedName, nestedArity) = type.NestedNameAndDeclaredArity();
            var nestedMetadataName = nestedArity > 0 ? $"{nestedName}`{nestedArity}" : nestedName;
            return $"{declaringName}+{nestedMetadataName}";
        }

        if (type.FullName is { } fullName)
            return fullName;

        var name = type.Name;
        return string.IsNullOrEmpty(type.Namespace)
            ? name
            : $"{type.Namespace}.{name}";
    }

    public IMethodSymbol? ResolveMethodSymbol(MethodInfo ifaceMethod)
    {
        var type = ResolveType(ifaceMethod.DeclaringType!);

        if (type is null) return null;
        return type.GetMembers()
            .OfType<IMethodSymbol>()
            // TODO: Better condition filtering
            .FirstOrDefault(x => x.Name == ifaceMethod.Name);
    }

    internal IPropertySymbol? ResolvePropertySymbol(PropertyInfo ifaceProp)
    {
        var type = ResolveType(ifaceProp.DeclaringType!);

        if (type is null) return null;
        return type.GetMembers()
            .OfType<IPropertySymbol>()
            // TODO: Better condition for filtering
            .FirstOrDefault(x => x.Name == ifaceProp.Name);
    }

    internal IEventSymbol? ResolveEventSymbol(EventInfo ifaceEvent)
    {
        var type = ResolveType(ifaceEvent.DeclaringType!);

        if (type is null) return null;
        return type.GetMembers()
            .OfType<IEventSymbol>()
            // TODO: Better condition for filtering
            .FirstOrDefault(x => x.Name == ifaceEvent.Name);
    }
}

internal readonly record struct MethodIdentity(
    string? ModuleName,
    Guid ModuleVersionId,
    int MetadataToken,
    string? DeclaringTypeName,
    string Name,
    int GenericArity)
{
    public static MethodIdentity Create(MethodBase methodBase)
    {
        if (methodBase is MethodInfo methodInfo && methodInfo.IsGenericMethod && !methodInfo.IsGenericMethodDefinition)
            methodBase = methodInfo.GetGenericMethodDefinition();

        var module = methodBase.Module;
        var moduleName = module.ScopeName;

        Guid moduleVersionId;
        try
        {
            moduleVersionId = module.ModuleVersionId;
        }
        catch
        {
            moduleVersionId = Guid.Empty;
        }

        int metadataToken;
        try
        {
            metadataToken = methodBase.MetadataToken;
        }
        catch
        {
            metadataToken = 0;
        }

        var genericArity = methodBase is MethodInfo genericMethod
            ? genericMethod.GetGenericArguments().Length
            : 0;

        return new MethodIdentity(
            moduleName,
            moduleVersionId,
            metadataToken,
            methodBase.DeclaringType?.FullName ?? methodBase.DeclaringType?.Name,
            methodBase.Name,
            genericArity);
    }
}

internal static class ReflectionTypeExtensions
{
    /// <summary>True if this Type represents System.Nullable&lt;T&gt;.</summary>
    public static bool IsNullableValueType(this Type t)
        => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);

    /// <summary>
    /// True when reflection generic args include inherited outer args and we must rebuild the chain.
    /// </summary>
    public static bool RequiresNestedChainResolution(this Type t)
        => t.IsNested && (
               t.IsGenericType
            || t.ContainsGenericParameters
            || (t.DeclaringType?.IsGenericType ?? false)
           );

    /// <summary>The declaring type chain from outermost -> innermost (includes this type).</summary>
    public static IReadOnlyList<Type> DeclaringTypeChain(this Type t)
    {
        var chain = new List<Type>();
        for (var cur = t; cur != null; cur = cur.DeclaringType)
            chain.Add(cur);
        chain.Reverse();
        return chain;
    }

    /// <summary>Simple runtime name without generic arity tick (e.g. "Ok`2" -> "Ok").</summary>
    public static string SimpleName(this Type t)
    {
        var name = t.Name;
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    /// <summary>Total arity according to reflection (includes inherited outer args for nested types).</summary>
    public static int TotalRuntimeArity(this Type t)
    {
        if (!t.IsGenericType && !t.IsGenericTypeDefinition) return 0;
        var def = t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition();
        return def.GetGenericArguments().Length;
    }

    /// <summary>
    /// Declared arity for this level in the source sense:
    /// for nested types, subtract declaring total arity.
    /// </summary>
    public static int DeclaredArity(this Type t)
    {
        if (!t.IsGenericType && !t.IsGenericTypeDefinition) return 0;

        var total = t.TotalRuntimeArity();
        var decl = t.DeclaringType;
        var declTotal = decl is null ? 0 : decl.TotalRuntimeArity();
        return Math.Max(0, total - declTotal);
    }

    /// <summary>Returns (simpleName, declaredArity) for nested matching.</summary>
    public static (string name, int declaredArity) NestedNameAndDeclaredArity(this Type t)
        => (t.SimpleName(), t.DeclaredArity());
}

internal static class ReflectionGenericArgumentExtensions
{
    /// <summary>
    /// Slices the flat generic argument list into per-level slices using each level's declared arity.
    /// Outer-to-inner order.
    /// </summary>
    public static Type[][] SliceGenericArgumentsPerLevel(this Type innermost, IReadOnlyList<Type> chain)
    {
        var arities = chain.Select(x => x.DeclaredArity()).ToArray();

        var flat = (innermost.IsGenericType || innermost.ContainsGenericParameters)
            ? innermost.GetGenericArguments()
            : Type.EmptyTypes;

        var result = new Type[arities.Length][];
        var pos = 0;

        for (int i = 0; i < arities.Length; i++)
        {
            var n = arities[i];
            if (n == 0)
            {
                result[i] = Array.Empty<Type>();
                continue;
            }

            result[i] = flat.Skip(pos).Take(n).ToArray();
            pos += n;
        }

        return result;
    }
}

internal static class SymbolNestedLookupExtensions
{
    /// <summary>
    /// Finds a nested type symbol under <paramref name="containing"/> that corresponds to the runtime nested type.
    /// Matching is based on simple name + declared arity (not total runtime arity).
    /// </summary>
    public static INamedTypeSymbol? FindNestedTypeForRuntime(this INamedTypeSymbol containing, Type runtimeNestedType)
    {
        var (name, declaredArity) = runtimeNestedType.NestedNameAndDeclaredArity();

        INamedTypeSymbol? bestNameOnly = null;

        foreach (var candidate in containing.GetMembers().OfType<INamedTypeSymbol>())
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal) && candidate.Arity == declaredArity)
                return candidate;

            if (bestNameOnly is null && string.Equals(candidate.Name, name, StringComparison.Ordinal))
                bestNameOnly = candidate;
        }

        return bestNameOnly;
    }
}
