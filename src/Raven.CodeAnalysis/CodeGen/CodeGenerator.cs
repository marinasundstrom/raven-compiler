using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

using static Raven.CodeAnalysis.CodeGen.DebugUtils;

namespace Raven.CodeAnalysis.CodeGen;

internal class CodeGenerator
{
    readonly Stopwatch _stopwatch = new Stopwatch();

    readonly Dictionary<ITypeSymbol, TypeGenerator> _typeGenerators = new Dictionary<ITypeSymbol, TypeGenerator>(SymbolEqualityComparer.Default);
    readonly Dictionary<SourceSymbol, MemberInfo> _mappings = new Dictionary<SourceSymbol, MemberInfo>(SymbolEqualityComparer.Default);
    readonly Dictionary<MemberBuilderCacheKey, MemberInfo> _constructedMappings = new Dictionary<MemberBuilderCacheKey, MemberInfo>();
    readonly Dictionary<RuntimeTypeParameterKey, Stack<Type>> _genericParameterMap = new();
    readonly HashSet<GenericTypeParameterBuilder> _genericParameterConstraintsApplied = new();
    readonly HashSet<PETypeParameterIdentity> _resolvingMetadataTypeParameters = new();
    readonly Dictionary<IMethodSymbol, MethodInfo> _runtimeMethodCache = new Dictionary<IMethodSymbol, MethodInfo>(ReferenceEqualityComparer.Instance);
    readonly Dictionary<IMethodSymbol, ConstructorInfo> _runtimeConstructorCache = new Dictionary<IMethodSymbol, ConstructorInfo>(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, IMethodSymbol> _metadataMethodProxies = new(StringComparer.Ordinal);
    readonly EmitOptions? _emitOptions;
    TypeBuilder? _metadataMethodProxyType;
    int _metadataMethodProxyOrdinal;

    public IILBuilderFactory ILBuilderFactory { get; set; } = ReflectionEmitILBuilderFactory.Instance;
    internal RuntimeTypeMap RuntimeTypeMap { get; }
    internal IRuntimeSymbolResolver RuntimeSymbolResolver { get; }

    internal IMethodSymbol? CurrentEmittingMethod { get; set; }

    public void AddMemberBuilder(SourceSymbol symbol, MemberInfo memberInfo)
        => AddMemberBuilder(symbol, memberInfo, substitution: default);

    public void AddMemberBuilder(SourceSymbol symbol, MemberInfo memberInfo, ImmutableArray<ITypeSymbol> substitution)
    {
        if (!substitution.IsDefaultOrEmpty)
        {
            _constructedMappings[new MemberBuilderCacheKey(symbol, substitution)] = memberInfo;
            return;
        }

        _mappings[symbol] = memberInfo;
    }

    public MemberInfo? GetMemberBuilder(SourceSymbol symbol)
    {
        if (_mappings.TryGetValue(symbol, out var memberInfo))
            return memberInfo;

        if (symbol is SourceFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.ContainingType is not INamedTypeSymbol containingType)
                throw new KeyNotFoundException($"Missing containing type for field '{fieldSymbol.Name}'.");

            var typeGenerator = GetOrCreateTypeGenerator(containingType);

            if (typeGenerator.TypeBuilder is null)
                typeGenerator.DefineTypeBuilder();

            return typeGenerator.EnsureFieldBuilder(fieldSymbol);
        }

        throw new KeyNotFoundException($"Missing member builder for '{symbol.Name}'.");
    }

    internal bool HasMemberBuilder(SourceSymbol symbol)
        => _mappings.ContainsKey(symbol);

    internal bool TryGetMemberBuilder(SourceSymbol symbol, ImmutableArray<ITypeSymbol> substitution, out MemberInfo memberInfo)
    {
        if (!substitution.IsDefaultOrEmpty)
        {
            if (_constructedMappings.TryGetValue(new MemberBuilderCacheKey(symbol, substitution), out memberInfo!))
                return true;

            memberInfo = null!;
            return false;
        }

        return _mappings.TryGetValue(symbol, out memberInfo!);
    }

    internal bool TryGetMemberBuilder(SourceSymbol symbol, out MemberInfo memberInfo)
        => TryGetMemberBuilder(symbol, substitution: default, out memberInfo!);

    internal bool TryGetRuntimeMethod(IMethodSymbol symbol, out MethodInfo methodInfo)
        => _runtimeMethodCache.TryGetValue(symbol, out methodInfo);

    internal MethodInfo CacheRuntimeMethod(IMethodSymbol symbol, MethodInfo methodInfo)
    {
        _runtimeMethodCache[symbol] = methodInfo;
        return methodInfo;
    }

    internal bool TryGetRuntimeConstructor(IMethodSymbol symbol, out ConstructorInfo constructorInfo)
        => _runtimeConstructorCache.TryGetValue(symbol, out constructorInfo);

    internal ConstructorInfo CacheRuntimeConstructor(IMethodSymbol symbol, ConstructorInfo constructorInfo)
    {
        _runtimeConstructorCache[symbol] = constructorInfo;
        return constructorInfo;
    }

    internal Type CacheRuntimeTypeParameter(ITypeParameterSymbol symbol, Type type)
    {
        var stack = GetOrCreateGenericParameterStack(symbol);
        stack.Clear();
        stack.Push(type);
        PrintDebug($"[CodeGen:TypeParam] Cache runtime type parameter {symbol.Name} (ordinal={symbol.Ordinal}) => {type}");
        return type;
    }

    internal void RegisterGenericParameters(ImmutableArray<ITypeParameterSymbol> parameters, GenericTypeParameterBuilder[] builders)
    {
        if (parameters.IsDefaultOrEmpty || builders.Length == 0)
            return;

        var count = Math.Min(parameters.Length, builders.Length);
        var applyConstraints = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[i];
            var builder = builders[i];
            var stack = GetOrCreateGenericParameterStack(parameter);
            if (stack.Count == 0 || !ReferenceEquals(stack.Peek(), builder))
            {
                stack.Push(builder);
                applyConstraints[i] = true;
            }

            applyConstraints[i] = _genericParameterConstraintsApplied.Add(builder);

            var owner = builder.DeclaringMethod is null ? "type" : "method";
            PrintDebug($"[CodeGen:TypeParam] Register generic parameter {parameter.Name} (ordinal={parameter.Ordinal}, symbolOwner={parameter.OwnerKind}) => {builder} (owner={owner}, isMethodParam={builder.IsGenericMethodParameter}, isTypeParam={builder.IsGenericTypeParameter})");
        }

        for (var i = 0; i < count; i++)
        {
            if (applyConstraints[i])
                ApplyGenericParameterConstraints(parameters[i], builders[i]);
        }
    }

    internal void RegisterGenericParameterAliases(ImmutableArray<ITypeParameterSymbol> parameters, Type[] runtimeTypes)
    {
        if (parameters.IsDefaultOrEmpty || runtimeTypes.Length == 0)
            return;

        var count = Math.Min(parameters.Length, runtimeTypes.Length);
        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[i];
            var runtimeType = runtimeTypes[i];
            var stack = GetOrCreateGenericParameterStack(parameter);
            if (stack.Count == 0 || !ReferenceEquals(stack.Peek(), runtimeType))
                stack.Push(runtimeType);

            if (CodeGenFlags.PrintDebug)
            {
                var owner = runtimeType.IsGenericParameter
                    ? (runtimeType.DeclaringMethod is null ? "type" : "method")
                    : "n/a";
                PrintDebug(
                    $"[CodeGen:TypeParam] Register alias {parameter.Name} (ordinal={parameter.Ordinal}, symbolOwner={parameter.OwnerKind}) => {runtimeType} (owner={owner}, isMethodParam={runtimeType.IsGenericMethodParameter}, isTypeParam={runtimeType.IsGenericTypeParameter})");
            }
        }
    }

    internal void UnregisterGenericParameters(ImmutableArray<ITypeParameterSymbol> parameters)
    {
        if (parameters.IsDefaultOrEmpty)
            return;

        foreach (var parameter in parameters)
        {
            if (TryGetGenericParameterStack(parameter, out var stack) && stack.Count > 0)
            {
                PrintDebug($"[CodeGen:TypeParam] Unregister generic parameter {parameter.Name} (ordinal={parameter.Ordinal})");
                stack.Pop();
            }
        }
    }

    private Stack<Type> GetOrCreateGenericParameterStack(ITypeParameterSymbol parameter)
    {
        var key = RuntimeTypeParameterKey.Create(parameter);
        if (!_genericParameterMap.TryGetValue(key, out var stack))
        {
            stack = new Stack<Type>();
            _genericParameterMap[key] = stack;
        }

        return stack;
    }

    private bool TryGetGenericParameterStack(ITypeParameterSymbol parameter, out Stack<Type> stack)
    {
        var key = RuntimeTypeParameterKey.Create(parameter);
        return _genericParameterMap.TryGetValue(key, out stack!);
    }

    private void ApplyGenericParameterConstraints(ITypeParameterSymbol parameter, GenericTypeParameterBuilder builder)
    {
        var attributes = GenericParameterAttributes.None;

        attributes |= parameter.Variance switch
        {
            VarianceKind.Out => GenericParameterAttributes.Covariant,
            VarianceKind.In => GenericParameterAttributes.Contravariant,
            _ => GenericParameterAttributes.None,
        };

        if ((parameter.ConstraintKind & TypeParameterConstraintKind.ReferenceType) != 0)
            attributes |= GenericParameterAttributes.ReferenceTypeConstraint;

        if ((parameter.ConstraintKind & TypeParameterConstraintKind.ValueType) != 0)
            attributes |= GenericParameterAttributes.NotNullableValueTypeConstraint;

        if ((parameter.ConstraintKind & TypeParameterConstraintKind.Constructor) != 0)
            attributes |= GenericParameterAttributes.DefaultConstructorConstraint;

        if ((parameter.ConstraintKind & TypeParameterConstraintKind.AllowByRefLike) != 0)
            attributes |= GenericParameterAttributes.AllowByRefLike;

        builder.SetGenericParameterAttributes(attributes);

        if ((parameter.ConstraintKind & TypeParameterConstraintKind.NotNull) != 0)
            ApplyNullableAnnotationAttribute(isNullable: false, builder.SetCustomAttribute);

        if (parameter.ConstraintTypes.IsDefaultOrEmpty)
            return;

        Type? baseType = null;
        List<Type>? interfaces = null;
        var seenConstraintTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var constraintType in parameter.ConstraintTypes)
        {
            if (!seenConstraintTypes.Add(constraintType))
                continue;

            var constraintClrType = TypeSymbolExtensionsForCodeGen.GetClrType(constraintType, this);

            if (constraintClrType.IsInterface)
            {
                interfaces ??= new List<Type>();
                if (!interfaces.Contains(constraintClrType))
                    interfaces.Add(constraintClrType);
            }
            else
            {
                baseType = constraintClrType;
            }
        }

        if (baseType is not null)
            builder.SetBaseTypeConstraint(baseType);

        if (interfaces is { Count: > 0 })
            builder.SetInterfaceConstraints(interfaces.ToArray());
    }

    private readonly Compilation _compilation;

    public Compilation Compilation => _compilation;

    public PersistedAssemblyBuilder AssemblyBuilder { get; private set; }
    public ModuleBuilder ModuleBuilder { get; private set; }

    private MethodBase? EntryPoint { get; set; }

    public Type? ExtensionMarkerNameAttributeType { get; private set; }
    public Type? FixedLengthArrayAttributeType { get; private set; }
    public Type? NullableAttributeType { get; private set; }
    public Type? NullableContextAttributeType { get; private set; }
    public Type? TupleElementNamesAttributeType { get; private set; }
    public Type? DiscriminatedUnionAttributeType { get; private set; }
    public Type? UnionInterfaceType { get; private set; }
    public Type? RavenUnionCaseAttributeType { get; private set; }
    public Type? RavenUnionCompanionAttributeType { get; private set; }
    public Type? RavenOptionNoneDefaultValueAttributeType { get; private set; }
    public Type? ExtensionAttributeType { get; private set; }
    public Type? UnitType { get; private set; }
    public Type? ClosedHierarchyAttributeType { get; private set; }
    public Type? TopLevelAttributeType { get; private set; }
    ConstructorInfo? _nullableCtor;
    ConstructorInfo? _nullableArrayCtor;
    ConstructorInfo? _nullableContextCtor;
    ConstructorInfo? _tupleElementNamesCtor;
    ConstructorInfo? _discriminatedUnionCtor;
    ConstructorInfo? _ravenUnionCaseCtor;
    ConstructorInfo? _ravenUnionCompanionCtor;
    ConstructorInfo? _ravenOptionNoneDefaultValueCtor;
    ConstructorInfo? _extensionMarkerNameCtor;
    ConstructorInfo? _fixedLengthArrayCtor;
    ConstructorInfo? _extensionAttributeCtor;
    ConstructorInfo? _closedHierarchyCtor;
    ConstructorInfo? _compilerGeneratedCtor;
    ConstructorInfo? _topLevelAttributeCtor;
    ConstructorInfo? _isByRefLikeCtor;
    ConstructorInfo? _isReadOnlyCtor;
    ConstructorInfo? _scopedRefCtor;

    bool _emitExtensionMarkerNameAttribute = true;

    internal void ApplyCustomAttributes(ImmutableArray<AttributeData> attributes, Action<CustomAttributeBuilder> apply)
    {
        if (attributes.IsDefaultOrEmpty)
            return;

        foreach (var attribute in attributes)
        {
            var builder = CreateCustomAttribute(attribute);
            if (builder is not null)
                apply(builder);
        }
    }

    internal CustomAttributeBuilder? CreateCustomAttribute(AttributeData attribute)
    {
        if (attribute is null)
            return null;

        var constructor = ResolveAttributeConstructor(attribute);
        if (constructor is null)
            return null;

        var parameters = attribute.AttributeConstructor.Parameters;
        var args = new object?[attribute.ConstructorArguments.Length];

        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
        {
            var parameterType = i < parameters.Length ? parameters[i].Type : null;
            var parameterClrType = parameterType is not null ? TypeSymbolExtensionsForCodeGen.GetClrType(parameterType, this) : null;
            args[i] = GetAttributeValue(attribute.ConstructorArguments[i], parameterClrType, parameterType);
        }

        var attributeType = constructor.DeclaringType ?? TypeSymbolExtensionsForCodeGen.GetClrType(attribute.AttributeClass, this);

        List<PropertyInfo>? properties = null;
        List<object?>? propertyValues = null;
        List<FieldInfo>? fields = null;
        List<object?>? fieldValues = null;

        foreach (var (name, value) in attribute.NamedArguments)
        {
            var property = TryResolveSourceAttributeNamedArgument(attribute.AttributeClass, name, out var sourceMember) &&
                           sourceMember is PropertyInfo sourceProperty
                ? sourceProperty
                : GetAttributeProperty(attributeType, name);
            if (property is not null)
            {
                properties ??= new List<PropertyInfo>();
                propertyValues ??= new List<object?>();
                properties.Add(property);
                propertyValues.Add(GetAttributeValue(value, property.PropertyType, value.Type));
                continue;
            }

            var field = sourceMember as FieldInfo ?? GetAttributeField(attributeType, name);
            if (field is not null)
            {
                fields ??= new List<FieldInfo>();
                fieldValues ??= new List<object?>();
                fields.Add(field);
                fieldValues.Add(GetAttributeValue(value, field.FieldType, value.Type));
            }
        }

        return new CustomAttributeBuilder(
            constructor,
            args,
            properties is not null ? properties.ToArray() : Array.Empty<PropertyInfo>(),
            propertyValues is not null ? propertyValues.ToArray() : Array.Empty<object?>(),
            fields is not null ? fields.ToArray() : Array.Empty<FieldInfo>(),
            fieldValues is not null ? fieldValues.ToArray() : Array.Empty<object?>());
    }

    private bool TryResolveSourceAttributeNamedArgument(INamedTypeSymbol? attributeClass, string name, out MemberInfo memberInfo)
    {
        memberInfo = null!;

        if (attributeClass is null)
            return false;

        foreach (var member in attributeClass.GetMembers(name))
        {
            SourceSymbol? sourceSymbol = member switch
            {
                SourcePropertySymbol property => property,
                SourceFieldSymbol field => field,
                _ => null
            };

            if (sourceSymbol is null)
                continue;

            if (TryGetMemberBuilder(sourceSymbol, out memberInfo))
                return memberInfo is PropertyInfo or FieldInfo;
        }

        return false;
    }

    private static PropertyInfo? GetAttributeProperty(Type attributeType, string name)
    {
        try
        {
            return attributeType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static FieldInfo? GetAttributeField(Type attributeType, string name)
    {
        try
        {
            return attributeType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private ConstructorInfo? ResolveAttributeConstructor(AttributeData attribute)
    {
        var constructorSymbol = attribute.AttributeConstructor;

        if (constructorSymbol is SourceMethodSymbol sourceConstructor)
        {
            if (TryGetMemberBuilder(sourceConstructor, out var existingMember) &&
                existingMember is ConstructorInfo existingCtorInfo)
            {
                return existingCtorInfo;
            }

            if (sourceConstructor.ContainingType is INamedTypeSymbol containingType)
            {
                var typeGenerator = GetOrCreateTypeGenerator(containingType);
                if (typeGenerator.TypeBuilder is null && typeGenerator.Type is null)
                    typeGenerator.DefineTypeBuilder();

                typeGenerator.DefineMemberBuilders();
            }

            if (TryGetMemberBuilder(sourceConstructor, out var sourceMember) &&
                sourceMember is ConstructorInfo sourceCtorInfo)
            {
                return sourceCtorInfo;
            }
        }

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(attribute.AttributeClass, this);
        var parameterTypes = constructorSymbol.Parameters
            .Select(p => TypeSymbolExtensionsForCodeGen.GetClrType(p.Type, this))
            .ToArray();

        return attributeType.GetConstructor(parameterTypes);
    }

    private object? GetAttributeValue(TypedConstant constant, Type? targetClrType, ITypeSymbol? targetSymbol)
    {
        switch (constant.Kind)
        {
            case TypedConstantKind.Null:
                return null;
            case TypedConstantKind.Type:
                return constant.Value switch
                {
                    ITypeSymbol typeSymbol => TypeSymbolExtensionsForCodeGen.GetClrType(typeSymbol, this),
                    Type type => type,
                    _ => null
                };
            case TypedConstantKind.Array:
                {
                    var values = constant.Values;
                    if (values.IsDefaultOrEmpty)
                        return Array.CreateInstance((targetClrType ?? typeof(object)).GetElementType() ?? typeof(object), 0);

                    var arraySymbol = targetSymbol as IArrayTypeSymbol ?? constant.Type as IArrayTypeSymbol;
                    var elementSymbol = arraySymbol?.ElementType;
                    var elementClrType = targetClrType?.GetElementType()
                        ?? (elementSymbol is not null ? TypeSymbolExtensionsForCodeGen.GetClrType(elementSymbol, this) : typeof(object));

                    var array = Array.CreateInstance(elementClrType, values.Length);
                    for (var i = 0; i < values.Length; i++)
                    {
                        array.SetValue(GetAttributeValue(values[i], elementClrType, elementSymbol), i);
                    }

                    return array;
                }
            case TypedConstantKind.Enum:
                {
                    if (constant.Value is null)
                        return null;

                    var enumType = targetClrType ?? (constant.Type as INamedTypeSymbol is INamedTypeSymbol enumSymbol ? TypeSymbolExtensionsForCodeGen.GetClrType(enumSymbol, this) : null);
                    if (enumType is not null && enumType.IsEnum)
                        return Enum.ToObject(enumType, constant.Value);

                    return constant.Value;
                }
            case TypedConstantKind.Primitive:
                return constant.Value;
            case TypedConstantKind.Error:
            default:
                return null;
        }
    }

    internal void ApplyNullableAttribute(ITypeSymbol type, Action<ConstructorInfo, byte[]> apply)
    {
        var flags = new List<byte>();
        CollectNullableTransformFlags(type, flags);
        if (!flags.Contains(2))
            return;

        EnsureNullableAttributeType();
        if (flags.Count == 1)
        {
            apply(_nullableCtor!, CreateNullableByteAttributeBlob(flags[0]));
            return;
        }

        apply(_nullableArrayCtor!, CreateNullableAttributeBlob(flags));
    }

    private static void CollectNullableTransformFlags(ITypeSymbol type, List<byte> flags)
    {
        switch (type)
        {
            case RefTypeSymbol refType:
                CollectNullableTransformFlags(refType.ElementType, flags);
                return;
            case IAddressTypeSymbol addressType:
                CollectNullableTransformFlags(addressType.ReferencedType, flags);
                return;
            case IPointerTypeSymbol pointerType:
                CollectNullableTransformFlags(pointerType.PointedAtType, flags);
                return;
            case NullableTypeSymbol nullable when
                nullable.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType:
                CollectValueTypeNullableTransformFlags(nullable.UnderlyingType, flags);
                return;
            case NullableTypeSymbol nullable:
                flags.Add(2);
                CollectNestedNullableTransformFlags(nullable.UnderlyingType, flags);
                return;
            default:
                CollectNonNullableTransformFlags(type, flags);
                return;
        }
    }

    private static void CollectNonNullableTransformFlags(ITypeSymbol type, List<byte> flags)
    {
        if (type.IsValueType)
        {
            CollectValueTypeNullableTransformFlags(type, flags);
            return;
        }

        flags.Add(1);
        CollectNestedNullableTransformFlags(type, flags);
    }

    private static void CollectValueTypeNullableTransformFlags(ITypeSymbol type, List<byte> flags)
    {
        if (type is not INamedTypeSymbol { TypeArguments.Length: > 0 } namedType)
            return;

        flags.Add(0);
        foreach (var typeArgument in namedType.TypeArguments)
            CollectNullableTransformFlags(typeArgument, flags);
    }

    private static void CollectNestedNullableTransformFlags(ITypeSymbol type, List<byte> flags)
    {
        switch (type)
        {
            case IArrayTypeSymbol arrayType:
                CollectNullableTransformFlags(arrayType.ElementType, flags);
                break;
            case INamedTypeSymbol namedType:
                foreach (var typeArgument in namedType.TypeArguments)
                    CollectNullableTransformFlags(typeArgument, flags);
                break;
        }
    }

    private static byte[] CreateNullableAttributeBlob(List<byte> flags)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)1);
        writer.Write(flags.Count);
        writer.Write(flags.ToArray());
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static byte[] CreateNullableByteAttributeBlob(byte flag)
        => [0x01, 0x00, flag, 0x00, 0x00];

    internal void ApplyNullableAnnotationAttribute(
        bool isNullable,
        Action<ConstructorInfo, byte[]> apply)
    {
        EnsureNullableAttributeType();
        apply(_nullableCtor!, CreateNullableByteAttributeBlob(isNullable ? (byte)2 : (byte)1));
    }

    internal void ApplyNullableContextAttribute(Action<ConstructorInfo, byte[]> apply)
    {
        EnsureNullableContextAttributeType();
        apply(_nullableContextCtor!, CreateNullableByteAttributeBlob(1));
    }

    internal CustomAttributeBuilder? CreateTupleElementNamesAttribute(ITypeSymbol type)
    {
        if (type is null)
            return null;

        var transformNames = new List<string?>();
        if (!TryCollectTupleElementNames(type, transformNames))
            return null;

        EnsureTupleElementNamesAttributeType();
        return new CustomAttributeBuilder(_tupleElementNamesCtor!, new object?[] { transformNames.ToArray() });
    }

    internal void ApplyDiscriminatedUnionAttribute(Action<ConstructorInfo, byte[]> apply)
    {
        EnsureDiscriminatedUnionAttributeType();
        apply(_discriminatedUnionCtor!, CreateParameterlessAttributeBlob());
    }

    internal Type GetUnionInterfaceType()
    {
        EnsureUnionInterfaceType();
        return UnionInterfaceType!;
    }

    internal void ApplyRavenUnionCaseAttribute(
        string caseTypeMetadataName,
        string name,
        int ordinal,
        Action<ConstructorInfo, byte[]> apply)
    {
        ArgumentNullException.ThrowIfNull(caseTypeMetadataName);
        ArgumentNullException.ThrowIfNull(name);

        EnsureRavenUnionCaseAttributeType();
        apply(_ravenUnionCaseCtor!, CreateRavenUnionCaseAttributeBlob(caseTypeMetadataName, name, ordinal));
    }

    internal void ApplyRavenUnionCompanionAttribute(
        string unionTypeMetadataName,
        Action<ConstructorInfo, byte[]> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unionTypeMetadataName);

        EnsureRavenUnionCompanionAttributeType();
        apply(_ravenUnionCompanionCtor!, CreateStringAttributeBlob(unionTypeMetadataName));
    }

    internal void ApplyRavenOptionNoneDefaultValueAttribute(Action<ConstructorInfo, byte[]> apply)
    {
        EnsureRavenOptionNoneDefaultValueAttributeType();
        apply(_ravenOptionNoneDefaultValueCtor!, CreateParameterlessAttributeBlob());
    }

    private static byte[] CreateParameterlessAttributeBlob()
        => [0x01, 0x00, 0x00, 0x00];

    private static byte[] CreateStringAttributeBlob(string value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)1);
        WriteSerString(stream, value);
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static byte[] CreateRavenUnionCaseAttributeBlob(
        string caseTypeMetadataName,
        string name,
        int ordinal)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)1);
        WriteSerString(stream, caseTypeMetadataName);
        WriteSerString(stream, name);
        writer.Write(ordinal);
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    internal void ApplyClosedHierarchyAttribute(
        TypeKind typeKind,
        ImmutableArray<INamedTypeSymbol> permittedTypes,
        Action<ConstructorInfo, byte[]> apply)
    {
        const string nativeMetadataName = "System.Runtime.CompilerServices.IsClosedTypeAttribute";
        if (typeKind == TypeKind.Class && TargetRuntimeTypeExists(nativeMetadataName))
        {
            var attributeType = ResolveReferencedRuntimeType(nativeMetadataName)!;
            var constructor = attributeType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException("Missing IsClosedTypeAttribute() constructor.");
            apply(constructor, CreateClosedHierarchyAttributeBlob(permittedTypes, useFrameworkContract: true));
            return;
        }

        EnsureClosedHierarchyAttributeType();
        apply(_closedHierarchyCtor!, CreateClosedHierarchyAttributeBlob(permittedTypes));
    }

    private byte[] CreateClosedHierarchyAttributeBlob(
        ImmutableArray<INamedTypeSymbol> permittedTypes,
        bool useFrameworkContract = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)1);
        if (useFrameworkContract)
        {
            writer.Write((ushort)1); // One named property argument.
            writer.Write((byte)0x54); // Property.
            writer.Write((byte)0x1D); // Single-dimensional array.
            writer.Write((byte)0x50); // System.Type element.
            WriteSerString(stream, "DerivedTypes");
        }
        writer.Write(permittedTypes.Length);

        foreach (var permittedType in permittedTypes)
        {
            var typeName = $"{permittedType.ToFullyQualifiedMetadataName()}, {_compilation.AssemblyName}";
            WriteSerString(stream, typeName);
        }

        if (!useFrameworkContract)
            writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static void WriteSerString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteCompressedUInt(stream, (uint)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteCompressedUInt(Stream stream, uint value)
    {
        if (value <= 0x7F)
        {
            stream.WriteByte((byte)value);
        }
        else if (value <= 0x3FFF)
        {
            stream.WriteByte((byte)((value >> 8) | 0x80));
            stream.WriteByte((byte)(value & 0xFF));
        }
        else if (value <= 0x1FFFFFFF)
        {
            stream.WriteByte((byte)((value >> 24) | 0xC0));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    void EnsureNullableAttributeType()
    {
        if (NullableAttributeType is not null)
            return;

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.NullableAttribute",
            TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));

        var flagsField = attrBuilder.DefineField(
            "NullableFlags",
            typeof(byte[]),
            FieldAttributes.Public | FieldAttributes.InitOnly);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(byte) });

        var arrayCtorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(byte[]) });

        var il = ctorBuilder.GetILGenerator();
        var baseCtor = typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (baseCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, typeof(byte));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stelem_I1);
        il.Emit(OpCodes.Stfld, flagsField);
        il.Emit(OpCodes.Ret);

        var arrayIl = arrayCtorBuilder.GetILGenerator();
        arrayIl.Emit(OpCodes.Ldarg_0);
        arrayIl.Emit(OpCodes.Call, baseCtor);
        arrayIl.Emit(OpCodes.Ldarg_0);
        arrayIl.Emit(OpCodes.Ldarg_1);
        arrayIl.Emit(OpCodes.Stfld, flagsField);
        arrayIl.Emit(OpCodes.Ret);

        // Keep the builder-backed constructor. Looking the constructor up again
        // through the emitted runtime type asks Reflection.Emit for parameter
        // metadata, which is not implemented by browser WebAssembly.
        _nullableCtor = ctorBuilder;
        _nullableArrayCtor = arrayCtorBuilder;
        NullableAttributeType = attrBuilder.CreateType();
    }

    void EnsureNullableContextAttributeType()
    {
        if (NullableContextAttributeType is not null)
            return;

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.NullableContextAttribute",
            TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)]);
        var attrUsageBuilder = new CustomAttributeBuilder(
            attrUsageCtor!,
            [AttributeTargets.Class |
             AttributeTargets.Struct |
             AttributeTargets.Method |
             AttributeTargets.Interface |
             AttributeTargets.Delegate],
            [typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.Inherited))!],
            [false]);
        attrBuilder.SetCustomAttribute(attrUsageBuilder);

        var flagField = attrBuilder.DefineField(
            "Flag",
            typeof(byte),
            FieldAttributes.Public | FieldAttributes.InitOnly);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(byte)]);

        var baseCtor = typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (baseCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        var il = ctorBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, flagField);
        il.Emit(OpCodes.Ret);

        _nullableContextCtor = ctorBuilder;
        NullableContextAttributeType = attrBuilder.CreateType();
    }

    void EnsureTupleElementNamesAttributeType()
    {
        if (TupleElementNamesAttributeType is not null)
            return;

        TupleElementNamesAttributeType = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.TupleElementNamesAttribute")
            ?? throw new InvalidOperationException("Type 'System.Runtime.CompilerServices.TupleElementNamesAttribute' not found in runtime assemblies.");

        _tupleElementNamesCtor = TupleElementNamesAttributeType.GetConstructor(new[] { typeof(string[]) })
            ?? throw new InvalidOperationException("Missing TupleElementNamesAttribute(string[]) constructor.");
    }

    void EnsureDiscriminatedUnionAttributeType()
    {
        if (DiscriminatedUnionAttributeType is not null)
            return;

        if (TargetRuntimeTypeExists("System.Runtime.CompilerServices.UnionAttribute") ||
            TargetRuntimeTypeExists("System.Runtime.CompilerServices.DiscriminatedUnionAttribute"))
        {
            TryBindRuntimeCoreTypes();
            if (DiscriminatedUnionAttributeType is not null)
                return;
        }

        if (!_compilation.Options.EmbedCoreTypes)
        {
            throw new InvalidOperationException("Type 'System.Runtime.CompilerServices.UnionAttribute' not found in runtime assemblies.");
        }

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Attribute"), this);

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.UnionAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            attributeType);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);

        var il = ctorBuilder.GetILGenerator();
        var baseCtor = attributeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (baseCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);

        DiscriminatedUnionAttributeType = attrBuilder.CreateType();
        _discriminatedUnionCtor = DiscriminatedUnionAttributeType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException("Missing UnionAttribute() constructor.");
    }

    void EnsureUnionInterfaceType()
    {
        if (UnionInterfaceType is not null)
            return;

        if (TargetRuntimeTypeExists("System.Runtime.CompilerServices.IUnion"))
        {
            TryBindRuntimeCoreTypes();
            if (UnionInterfaceType is not null)
                return;
        }

        if (!_compilation.Options.EmbedCoreTypes)
        {
            throw new InvalidOperationException("Type 'System.Runtime.CompilerServices.IUnion' not found in runtime assemblies.");
        }

        var objectType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetSpecialType(SpecialType.System_Object), this);

        var interfaceBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.IUnion",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);

        var valueProperty = interfaceBuilder.DefineProperty(
            "Value",
            PropertyAttributes.None,
            objectType,
            null);

        var getter = interfaceBuilder.DefineMethod(
            "get_Value",
            MethodAttributes.Public |
            MethodAttributes.Abstract |
            MethodAttributes.Virtual |
            MethodAttributes.HideBySig |
            MethodAttributes.SpecialName |
            MethodAttributes.NewSlot,
            objectType,
            Type.EmptyTypes);

        valueProperty.SetGetMethod(getter);

        UnionInterfaceType = interfaceBuilder.CreateType();
    }

    private bool TargetRuntimeTypeExists(string metadataName)
        => Compilation.GetTypeByMetadataName(metadataName) is INamedTypeSymbol { TypeKind: not TypeKind.Error };

    void EnsureRavenUnionCaseAttributeType()
    {
        if (RavenUnionCaseAttributeType is not null)
            return;

        RavenUnionCaseAttributeType = TargetRuntimeTypeExists("Raven.Runtime.CompilerServices.RavenUnionCaseAttribute")
            ? Compilation.ResolveRuntimeType("Raven.Runtime.CompilerServices.RavenUnionCaseAttribute")
            : null;
        if (RavenUnionCaseAttributeType is not null)
        {
            _ravenUnionCaseCtor = RavenUnionCaseAttributeType.GetConstructor(new[] { typeof(string), typeof(string), typeof(int) });
            if (_ravenUnionCaseCtor is not null)
                return;
        }

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Attribute"), this);
        var stringType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetSpecialType(SpecialType.System_String), this);
        var intType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetSpecialType(SpecialType.System_Int32), this);

        var attrBuilder = ModuleBuilder.DefineType(
            "Raven.Runtime.CompilerServices.RavenUnionCaseAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            attributeType);

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)])
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute(AttributeTargets) constructor.");
        var allowMultipleProperty = typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.AllowMultiple))
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute.AllowMultiple property.");
        var inheritedProperty = typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.Inherited))
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute.Inherited property.");
        attrBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            attrUsageCtor,
            [AttributeTargets.Class | AttributeTargets.Struct],
            [allowMultipleProperty, inheritedProperty],
            [true, false]));

        var caseTypeMetadataNameField = DefineReadOnlyBackingField(attrBuilder, "CaseTypeMetadataName", stringType);
        var nameField = DefineReadOnlyBackingField(attrBuilder, "Name", stringType);
        var ordinalField = DefineReadOnlyBackingField(attrBuilder, "Ordinal", intType);

        DefineReadOnlyProperty(attrBuilder, "CaseTypeMetadataName", stringType, caseTypeMetadataNameField);
        DefineReadOnlyProperty(attrBuilder, "Name", stringType, nameField);
        DefineReadOnlyProperty(attrBuilder, "Ordinal", intType, ordinalField);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { stringType, stringType, intType });

        var il = ctorBuilder.GetILGenerator();
        var baseCtor = attributeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (baseCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, caseTypeMetadataNameField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, nameField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Stfld, ordinalField);
        il.Emit(OpCodes.Ret);

        RavenUnionCaseAttributeType = attrBuilder.CreateType();
        _ravenUnionCaseCtor = RavenUnionCaseAttributeType.GetConstructor(new[] { stringType, stringType, intType })
            ?? throw new InvalidOperationException("Missing RavenUnionCaseAttribute(string, string, int) constructor.");
    }

    void EnsureRavenUnionCompanionAttributeType()
    {
        if (RavenUnionCompanionAttributeType is not null)
            return;

        RavenUnionCompanionAttributeType = TargetRuntimeTypeExists("Raven.Runtime.CompilerServices.RavenUnionCompanionAttribute")
            ? Compilation.ResolveRuntimeType("Raven.Runtime.CompilerServices.RavenUnionCompanionAttribute")
            : null;
        if (RavenUnionCompanionAttributeType is not null)
        {
            _ravenUnionCompanionCtor = RavenUnionCompanionAttributeType.GetConstructor([typeof(string)]);
            if (_ravenUnionCompanionCtor is not null)
                return;
        }

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Attribute"), this);
        var stringType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetSpecialType(SpecialType.System_String), this);

        var attrBuilder = ModuleBuilder.DefineType(
            "Raven.Runtime.CompilerServices.RavenUnionCompanionAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            attributeType);

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)])
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute(AttributeTargets) constructor.");
        var inheritedProperty = typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.Inherited))
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute.Inherited property.");
        attrBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            attrUsageCtor,
            [AttributeTargets.Class],
            [inheritedProperty],
            [false]));

        var unionTypeMetadataNameField = DefineReadOnlyBackingField(attrBuilder, "UnionTypeMetadataName", stringType);
        DefineReadOnlyProperty(attrBuilder, "UnionTypeMetadataName", stringType, unionTypeMetadataNameField);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [stringType]);

        var il = ctorBuilder.GetILGenerator();
        var baseCtor = attributeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? throw new InvalidOperationException("Missing Attribute base constructor.");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, unionTypeMetadataNameField);
        il.Emit(OpCodes.Ret);

        RavenUnionCompanionAttributeType = attrBuilder.CreateType();
        _ravenUnionCompanionCtor = RavenUnionCompanionAttributeType.GetConstructor([stringType])
            ?? throw new InvalidOperationException("Missing RavenUnionCompanionAttribute(string) constructor.");
    }

    void EnsureRavenOptionNoneDefaultValueAttributeType()
    {
        if (RavenOptionNoneDefaultValueAttributeType is not null)
            return;

        const string metadataName =
            "Raven.Runtime.CompilerServices.RavenOptionNoneDefaultValueAttribute";
        RavenOptionNoneDefaultValueAttributeType = TargetRuntimeTypeExists(metadataName)
            ? Compilation.ResolveRuntimeType(metadataName)
            : null;
        if (RavenOptionNoneDefaultValueAttributeType is not null)
        {
            _ravenOptionNoneDefaultValueCtor =
                RavenOptionNoneDefaultValueAttributeType.GetConstructor(Type.EmptyTypes);
            if (_ravenOptionNoneDefaultValueCtor is not null)
                return;
        }

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(
            Compilation.GetTypeByMetadataName("System.Attribute"),
            this);
        var attrBuilder = ModuleBuilder.DefineType(
            metadataName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            attributeType);

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)])
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute(AttributeTargets) constructor.");
        var inheritedProperty = typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.Inherited))
            ?? throw new InvalidOperationException("Missing AttributeUsageAttribute.Inherited property.");
        attrBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            attrUsageCtor,
            [AttributeTargets.Parameter],
            [inheritedProperty],
            [false]));

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = ctorBuilder.GetILGenerator();
        var baseCtor = attributeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? throw new InvalidOperationException("Missing Attribute base constructor.");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);

        RavenOptionNoneDefaultValueAttributeType = attrBuilder.CreateType();
        _ravenOptionNoneDefaultValueCtor =
            RavenOptionNoneDefaultValueAttributeType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                "Missing RavenOptionNoneDefaultValueAttribute() constructor.");
    }

    private static FieldBuilder DefineReadOnlyBackingField(TypeBuilder typeBuilder, string propertyName, Type propertyType)
        => typeBuilder.DefineField(
            $"<{propertyName}>k__BackingField",
            propertyType,
            FieldAttributes.Private | FieldAttributes.InitOnly);

    private static void DefineReadOnlyProperty(TypeBuilder typeBuilder, string propertyName, Type propertyType, FieldBuilder field)
    {
        var propertyBuilder = typeBuilder.DefineProperty(
            propertyName,
            PropertyAttributes.None,
            propertyType,
            null);

        var getterMethod = typeBuilder.DefineMethod(
            "get_" + propertyName,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            propertyType,
            Type.EmptyTypes);

        var getterIl = getterMethod.GetILGenerator();
        getterIl.Emit(OpCodes.Ldarg_0);
        getterIl.Emit(OpCodes.Ldfld, field);
        getterIl.Emit(OpCodes.Ret);

        propertyBuilder.SetGetMethod(getterMethod);
    }

    void EnsureClosedHierarchyAttributeType()
    {
        if (ClosedHierarchyAttributeType is not null)
            return;

        var attributeType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Attribute"), this);
        var typeType = TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetSpecialType(SpecialType.System_Type), this);

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.ClosedHierarchyAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            attributeType);

        var permittedTypesField = attrBuilder.DefineField(
            "_permittedTypes",
            typeType.MakeArrayType(),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        var propertyBuilder = attrBuilder.DefineProperty(
            "PermittedTypes",
            PropertyAttributes.None,
            typeType.MakeArrayType(),
            null);

        var getterMethod = attrBuilder.DefineMethod(
            "get_PermittedTypes",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeType.MakeArrayType(),
            Type.EmptyTypes);

        var getterIl = getterMethod.GetILGenerator();
        getterIl.Emit(OpCodes.Ldarg_0);
        getterIl.Emit(OpCodes.Ldfld, permittedTypesField);
        getterIl.Emit(OpCodes.Ret);

        propertyBuilder.SetGetMethod(getterMethod);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeType.MakeArrayType() });

        var il = ctorBuilder.GetILGenerator();
        var baseCtor = attributeType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (baseCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, permittedTypesField);
        il.Emit(OpCodes.Ret);

        ClosedHierarchyAttributeType = attrBuilder.CreateType();
        _closedHierarchyCtor = ClosedHierarchyAttributeType.GetConstructor(new[] { typeType.MakeArrayType() })
            ?? throw new InvalidOperationException("Missing ClosedHierarchyAttribute(Type[]) constructor.");
    }

    bool TryCollectTupleElementNames(ITypeSymbol type, List<string?> transformNames)
    {
        var visiting = new HashSet<ITypeSymbol>(ReferenceEqualityComparer.Instance);
        return TryCollectTupleElementNames(type, transformNames, visiting);
    }

    bool TryCollectTupleElementNames(ITypeSymbol type, List<string?> transformNames, HashSet<ITypeSymbol> visiting)
    {
        if (type is null)
            return false;

        if (!visiting.Add(type))
            return false;

        var start = transformNames.Count;
        var hasAnyNames = false;

        try
        {
            switch (type)
            {
                case INamedTypeSymbol named when named.TypeKind == TypeKind.Tuple:
                    {
                        var tupleElements = named.TupleElements;
                        if (tupleElements.IsDefaultOrEmpty)
                            break;

                        for (var i = 0; i < tupleElements.Length; i++)
                        {
                            var element = tupleElements[i];
                            var elementName = element.Name;
                            var isExplicit = !string.IsNullOrEmpty(elementName) && elementName != $"Item{i + 1}";
                            transformNames.Add(isExplicit ? elementName : null);

                            if (isExplicit)
                                hasAnyNames = true;

                            if (TryCollectTupleElementNames(element.Type, transformNames, visiting))
                                hasAnyNames = true;
                        }

                        break;
                    }
                case NullableTypeSymbol nullableType:
                    hasAnyNames |= TryCollectTupleElementNames(nullableType.UnderlyingType, transformNames, visiting);
                    break;
                case IArrayTypeSymbol arrayType:
                    hasAnyNames |= TryCollectTupleElementNames(arrayType.ElementType, transformNames, visiting);
                    break;
                case IPointerTypeSymbol pointerType:
                    hasAnyNames |= TryCollectTupleElementNames(pointerType.PointedAtType, transformNames, visiting);
                    break;
                case IAddressTypeSymbol addressType:
                    hasAnyNames |= TryCollectTupleElementNames(addressType.ReferencedType, transformNames, visiting);
                    break;
                case INamedTypeSymbol namedType:
                    var typeArguments = namedType is ConstructedNamedTypeSymbol constructed
                        ? constructed.GetExplicitTypeArgumentsForInference()
                        : namedType.TypeArguments;

                    if (!typeArguments.IsDefaultOrEmpty)
                    {
                        foreach (var typeArgument in typeArguments)
                            hasAnyNames |= TryCollectTupleElementNames(typeArgument, transformNames, visiting);
                    }

                    break;
            }
        }
        finally
        {
            visiting.Remove(type);
        }

        if (!hasAnyNames)
        {
            var added = transformNames.Count - start;
            if (added > 0)
                transformNames.RemoveRange(start, added);
        }

        return hasAnyNames;
    }

    public CodeGenerator(Compilation compilation, EmitOptions? emitOptions = null)
    {
        _compilation = compilation;
        _emitOptions = emitOptions;
        RuntimeTypeMap = new RuntimeTypeMap(this);
        RuntimeSymbolResolver = new RuntimeSymbolResolver(this);
    }

    public Type? GetTypeBuilder(INamedTypeSymbol namedTypeSymbol)
    {
        var e = _typeGenerators[namedTypeSymbol];

        return e.Type ?? e?.TypeBuilder;
    }

    public void Emit(Stream peStream, Stream? pdbStream)
    {
        _stopwatch.Reset();
        _stopwatch.Start();

        PrintDebug($"Starting emitting code...");

        try
        {
            PrintDebug("Starting code generation emission.");
            var assemblyName = new AssemblyName(_compilation.AssemblyName)
            {
                Version = new Version(1, 0, 0, 0)
            };

            var coreAssembly = _compilation.EmitCoreAssembly ?? typeof(object).Assembly;
            AssemblyBuilder = new PersistedAssemblyBuilder(assemblyName, coreAssembly);
            ModuleBuilder = DefineDynamicModuleWithSymbols(AssemblyBuilder, _compilation.AssemblyName);

            DetermineShimTypeRequirements();
            PrintDebug("Determined shim type requirements.");

            if (!_compilation.Options.EmbedCoreTypes)
                TryBindRuntimeCoreTypes();

            if (_emitExtensionMarkerNameAttribute && (ExtensionMarkerNameAttributeType is null || _compilation.Options.EmbedCoreTypes))
                CreateExtensionMarkerNameAttributeType();
            if (UnitType is null || _compilation.Options.EmbedCoreTypes)
                CreateUnitStruct();

            DefineTypeBuilders();
            PrintDebug("Type builders defined.");
            ApplyDeferredTypeBuilderAttributes();
            PrintDebug("Deferred type builder attributes applied.");

            // Entry-point bridge synthesis mutates the containing source type by adding a
            // synthesized method (for example <Main>_EntryPoint). Compute the entry point
            // before defining members so TypeGenerator sees the final member set.
            var entryPointSymbol = _compilation.GetEntryPoint();
            DefineMemberBuilders();
            PrintDebug("Member builders defined.");
            ApplyCustomAttributes(_compilation.Assembly.GetAttributes(), attribute => AssemblyBuilder.SetCustomAttribute(attribute));

            EmitMemberILBodies();
            PrintDebug("Member IL bodies emitted.");

            CreateTypes();
            if (_metadataMethodProxyType is { } metadataMethodProxyType && !metadataMethodProxyType.IsCreated())
                metadataMethodProxyType.CreateType();
            PrintDebug("All types created.");
            ApplyCustomAttributes(_compilation.Module.GetAttributes(), attribute => ModuleBuilder.SetCustomAttribute(attribute));

            var asyncStateMachines = Compilation.GetSynthesizedAsyncStateMachineTypes().ToArray();
            foreach (var asyncStateMachine in asyncStateMachines)
            {
                var generator = GetOrCreateTypeGenerator(asyncStateMachine);
                if (generator.TypeBuilder is null)
                    generator.DefineTypeBuilder();

                if (generator.TypeBuilder is { } builder && !builder.IsCreated())
                {
                    PrintDebug($"Creating async state machine type: {asyncStateMachine.ToDisplayString()}");
                    generator.CreateType();
                }
            }

            var deferredTypes = _typeGenerators.Values
                .Where(generator => generator.TypeBuilder is { } builder && !builder.IsCreated())
                .ToArray();

            if (deferredTypes.Length > 0)
            {
                PrintDebug($"Found {deferredTypes.Length} type builders not created after CreateTypes.");
                foreach (var generator in deferredTypes)
                {
                    PrintDebug($"Creating deferred type: {generator.TypeSymbol.ToDisplayString()}");
                    generator.CreateType();
                }
            }

            var deferredMethodTypes = _typeGenerators.Values
                .SelectMany(generator => generator.MethodGenerators)
                .Select(generator => generator.MethodBase?.DeclaringType)
                .OfType<TypeBuilder>()
                .Where(builder => !builder.IsCreated())
                .Distinct()
                .ToArray();

            if (deferredMethodTypes.Length > 0)
            {
                PrintDebug($"Found {deferredMethodTypes.Length} method declaring types not created after CreateTypes.");
                foreach (var builder in deferredMethodTypes)
                {
                    PrintDebug($"Creating declaring type for method: {builder.FullName ?? builder.Name}");
                    var owner = _typeGenerators.Values.FirstOrDefault(generator => ReferenceEquals(generator.TypeBuilder, builder));
                    owner?.CreateType();
                }
            }

            MethodGenerator? entryPointGenerator = null;

            if (entryPointSymbol is not null)
            {
                foreach (var typeGenerator in _typeGenerators.Values)
                {
                    var generator = typeGenerator.GetMethodGenerator(entryPointSymbol);
                    if (generator is not null)
                    {
                        entryPointGenerator = generator;
                        break;
                    }
                }

                if (entryPointGenerator is null)
                {
                    throw new InvalidOperationException("Failed to locate entry point method.");
                }
            }
            EntryPoint = entryPointGenerator?.MethodBase;
            if (EntryPoint is not null)
                PrintDebug($"Selected entry point: {EntryPoint.Name}");

            PrintUncreatedModuleTypeBuilders();

            MetadataBuilder metadataBuilder = AssemblyBuilder.GenerateMetadata(out BlobBuilder ilStream, out _, out MetadataBuilder pdbBuilder);
            PrintDebug("Generated assembly metadata.");
            MethodDefinitionHandle entryPointHandle = EntryPoint is not null
                ? MetadataTokens.MethodDefinitionHandle(EntryPoint.MetadataToken)
                : default;
            using var provisionalPdbStream = new MemoryStream();
            var pdbFileName = pdbStream is FileStream fileStream && !string.IsNullOrWhiteSpace(fileStream.Name)
                ? Path.GetFileName(fileStream.Name)
                : $"{_compilation.AssemblyName}.pdb";
            DebugDirectoryBuilder debugDirectoryBuilder = EmitPdb(
                pdbBuilder,
                metadataBuilder.GetRowCounts(),
                entryPointHandle,
                provisionalPdbStream,
                pdbFileName,
                out var pdbContentId);

            Characteristics imageCharacteristics = _compilation.Options.OutputKind switch
            {
                OutputKind.ConsoleApplication => Characteristics.ExecutableImage,
                OutputKind.DynamicallyLinkedLibrary => Characteristics.Dll,
                _ => Characteristics.Dll,
            };

            ManagedPEBuilder peBuilder = new ManagedPEBuilder(
                            header: new PEHeaderBuilder(imageCharacteristics: imageCharacteristics, subsystem: Subsystem.WindowsCui),
                            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
                            ilStream: ilStream,
                            debugDirectoryBuilder: debugDirectoryBuilder,
                            entryPoint: entryPointHandle);

            BlobBuilder peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);

            using var rawPeStream = new MemoryStream();
            peBlob.WriteContentTo(rawPeStream);

            // Reflection.Emit binds core types to System.Private.CoreLib runtime identities.
            // Normalize to System.Runtime for consumer-facing assemblies while retaining
            // System.Private.CoreLib where required for non-forwarded runtime types.
            rawPeStream.Position = 0;
            if (pdbStream is null)
            {
                WriteFinalPe(rawPeStream, peStream);
            }
            else
            {
                using var alignedPdbStream = new MemoryStream();
                provisionalPdbStream.Position = 0;
                rawPeStream.Position = 0;
                using var rawPeReader = new PEReader(rawPeStream, PEStreamOptions.LeaveOpen);
                var rawMetadataReader = rawPeReader.GetMetadataReader();
                var methodCount = rawMetadataReader.GetTableRowCount(TableIndex.MethodDef);
                var debugInformationRows = GetMethodDebugInformationRows(
                    rawMetadataReader,
                    provisionalPdbStream);

                if (debugInformationRows.Count == 0)
                {
                    provisionalPdbStream.CopyTo(alignedPdbStream);
                }
                else
                {
                    var identityCorrections = Enumerable.Range(1, methodCount)
                        .ToDictionary(static row => row);
                    EmitCorrectedPdb(
                        provisionalPdbStream,
                        metadataBuilder.GetRowCounts(),
                        entryPointHandle,
                        identityCorrections,
                        pdbContentId,
                        alignedPdbStream,
                        debugInformationRows);
                }

                alignedPdbStream.Position = 0;
                rawPeStream.Position = 0;
                WriteFinalPe(rawPeStream, peStream, alignedPdbStream, pdbStream);
            }
        }
        catch (Exception ex)
        {
            if (CurrentEmittingMethod is { } method)
                throw new InvalidOperationException($"Emission failed while processing method '{method.ToDisplayString()}'", ex);

            throw;
        }
        finally
        {
            _stopwatch.Stop();
            PrintDebug($"Emitted code in {_stopwatch.ElapsedMilliseconds} ms");
        }
    }

    private void WriteFinalPe(
        Stream rawPeStream,
        Stream output,
        Stream? provisionalPdbStream = null,
        Stream? pdbOutputStream = null)
    {
        var targetReferences = GetTargetAssemblyReferences();
        if (_emitOptions?.TargetCoreLibraryIdentity is { } targetIdentity)
        {
            var targetReference = new Mono.Cecil.AssemblyNameReference(
                targetIdentity.Name,
                targetIdentity.Version ?? new Version(0, 0, 0, 0))
            {
                Culture = targetIdentity.CultureName ?? string.Empty
            };
            var publicKeyToken = targetIdentity.GetPublicKeyToken();
            if (publicKeyToken is { Length: > 0 })
                targetReference.PublicKeyToken = publicKeyToken;

            AssemblyReferenceNormalizer.RetargetCoreLibraryReference(
                rawPeStream,
                output,
                targetReference,
                targetReferences: targetReferences,
                metadataMethodProxies: _metadataMethodProxies,
                pdbInput: provisionalPdbStream,
                pdbOutput: pdbOutputStream);
            return;
        }

        AssemblyReferenceNormalizer.NormalizeCoreLibReference(
            rawPeStream,
            output,
            targetReferences: targetReferences,
            metadataMethodProxies: _metadataMethodProxies,
            pdbInput: provisionalPdbStream,
            pdbOutput: pdbOutputStream);
    }

    internal MethodInfo GetMethodInfoOrMetadataProxy(IMethodSymbol methodSymbol)
    {
        if (_emitOptions?.TargetCoreLibraryIdentity is not null &&
            TryGetMetadataMethod(methodSymbol, out var targetMetadataMethod) &&
            !targetMetadataMethod.IsGenericMethod)
        {
            return CreateMetadataMethodProxy(targetMetadataMethod);
        }

        try
        {
            return RuntimeSymbolResolver.GetMethodInfo(methodSymbol);
        }
        catch (InvalidOperationException) when (TryGetMetadataMethod(methodSymbol, out var metadataMethod))
        {
            if (metadataMethod.IsGenericMethod)
                throw;

            return CreateMetadataMethodProxy(metadataMethod);
        }
    }

    private MethodInfo CreateMetadataMethodProxy(IMethodSymbol metadataMethod)
    {
        _metadataMethodProxyType ??= ModuleBuilder.DefineType(
            "<RavenMetadataMethodReferences>",
            TypeAttributes.NotPublic | TypeAttributes.Class,
            typeof(object));

        var proxyName = $"Reference{++_metadataMethodProxyOrdinal}";
        var returnType = GetMetadataProxySignatureType(metadataMethod.ReturnType);
        var parameterTypes = metadataMethod.Parameters
            .Select(parameter => GetMetadataProxySignatureType(parameter.Type))
            .ToArray();
        var proxy = _metadataMethodProxyType.DefineMethod(
            proxyName,
            MethodAttributes.Assembly | MethodAttributes.HideBySig |
                (metadataMethod.IsStatic ? MethodAttributes.Static : 0),
            returnType,
            parameterTypes);

        var il = proxy.GetILGenerator();
        if (returnType == typeof(void))
        {
            il.Emit(OpCodes.Ret);
        }
        else if (returnType.IsValueType)
        {
            var local = il.DeclareLocal(returnType);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, returnType);
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }

        _metadataMethodProxies.Add(proxyName, metadataMethod);
        return proxy;
    }

    private Type GetMetadataProxySignatureType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } enumUnderlyingType })
            return GetMetadataProxySignatureType(enumUnderlyingType);

        var specialType = typeSymbol.SpecialType switch
        {
            SpecialType.System_Void or SpecialType.System_Unit => typeof(void),
            SpecialType.System_Object => typeof(object),
            SpecialType.System_String => typeof(string),
            SpecialType.System_Boolean => typeof(bool),
            SpecialType.System_Char => typeof(char),
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            SpecialType.System_Single => typeof(float),
            SpecialType.System_Double => typeof(double),
            SpecialType.System_Decimal => typeof(decimal),
            SpecialType.System_IntPtr => typeof(IntPtr),
            SpecialType.System_UIntPtr => typeof(UIntPtr),
            _ => null
        };
        if (specialType is not null)
            return specialType;

        try
        {
            return TypeSymbolExtensionsForCodeGen.GetClrTypeTreatingUnitAsVoid(typeSymbol, this);
        }
        catch (InvalidOperationException)
        {
            // The proxy exists only long enough for Reflection.Emit to allocate an IL token.
            // Its signature is replaced with the target metadata signature before the PE is written.
            return typeSymbol.IsValueType ? typeof(int) : typeof(object);
        }
    }

    private static bool TryGetMetadataMethod(IMethodSymbol methodSymbol, out IMethodSymbol metadataMethod)
    {
        while (true)
        {
            if (methodSymbol is PEMethodSymbol)
            {
                metadataMethod = methodSymbol;
                return true;
            }

            if (methodSymbol.UnderlyingSymbol is IMethodSymbol underlying &&
                !ReferenceEquals(underlying, methodSymbol))
            {
                methodSymbol = underlying;
                continue;
            }

            metadataMethod = null!;
            return false;
        }
    }

    private IReadOnlyDictionary<string, Mono.Cecil.AssemblyNameReference> GetTargetAssemblyReferences()
    {
        var references = new Dictionary<string, Mono.Cecil.AssemblyNameReference>(StringComparer.OrdinalIgnoreCase);
        var runtimeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in _compilation.References.OfType<PortableExecutableReference>())
        {
            if (string.IsNullOrWhiteSpace(reference.FilePath))
                continue;

            AddTargetAssemblyReference(reference.FilePath, references);
            if (TryGetSharedFrameworkDirectory(reference.FilePath) is { } runtimeDirectory)
                runtimeDirectories.Add(runtimeDirectory);
        }

        // Reflection.Emit describes forwarded framework types using the compiler
        // host's implementation assemblies (for example System.Private.Xml.Linq).
        // Those implementation assemblies do not appear in the target reference
        // pack, so include their target-runtime identities when retargeting the
        // emitted metadata.
        foreach (var runtimeDirectory in runtimeDirectories)
        {
            foreach (var assemblyPath in Directory.EnumerateFiles(runtimeDirectory, "*.dll"))
                AddTargetAssemblyReference(assemblyPath, references);
        }

        return references;
    }

    private static void AddTargetAssemblyReference(
        string assemblyPath,
        IDictionary<string, Mono.Cecil.AssemblyNameReference> references)
    {
        try
        {
            var identity = AssemblyName.GetAssemblyName(assemblyPath);
            if (string.IsNullOrWhiteSpace(identity.Name))
                return;

            var targetReference = new Mono.Cecil.AssemblyNameReference(
                identity.Name,
                identity.Version ?? new Version(0, 0, 0, 0))
            {
                Culture = identity.CultureName ?? string.Empty
            };
            var publicKeyToken = identity.GetPublicKeyToken();
            if (publicKeyToken is { Length: > 0 })
                targetReference.PublicKeyToken = publicKeyToken;

            references.TryAdd(identity.Name, targetReference);
        }
        catch (BadImageFormatException)
        {
        }
        catch (FileLoadException)
        {
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static string? TryGetSharedFrameworkDirectory(string referenceAssemblyPath)
    {
        try
        {
            var tfmDirectory = Path.GetDirectoryName(referenceAssemblyPath);
            var refDirectory = tfmDirectory is null ? null : Path.GetDirectoryName(tfmDirectory);
            if (refDirectory is null ||
                !string.Equals(Path.GetFileName(refDirectory), "ref", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var versionDirectory = Path.GetDirectoryName(refDirectory);
            var packDirectory = versionDirectory is null ? null : Path.GetDirectoryName(versionDirectory);
            var packsDirectory = packDirectory is null ? null : Path.GetDirectoryName(packDirectory);
            if (versionDirectory is null ||
                packDirectory is null ||
                packsDirectory is null ||
                !string.Equals(Path.GetFileName(packsDirectory), "packs", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var packName = Path.GetFileName(packDirectory);
            if (!packName.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
                return null;

            var dotnetRoot = Path.GetDirectoryName(packsDirectory);
            if (dotnetRoot is null)
                return null;

            var runtimeDirectory = Path.Combine(
                dotnetRoot,
                "shared",
                packName[..^4],
                Path.GetFileName(versionDirectory));
            return Directory.Exists(runtimeDirectory) ? runtimeDirectory : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void DetermineShimTypeRequirements()
    {
        _emitExtensionMarkerNameAttribute = Compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ExtensionDeclarationSyntax>())
            .Any();
        // Intentionally avoid recursive type walks here. On recursive constructed
        // generic graphs (e.g. nested option extensions), forcing TypeArguments can
        // recurse indefinitely during core emission.
    }

    private void TryBindRuntimeCoreTypes()
    {
        ExtensionMarkerNameAttributeType ??= Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.ExtensionMarkerNameAttribute");
        _extensionMarkerNameCtor ??= ExtensionMarkerNameAttributeType?.GetConstructor(new[] { typeof(string) });
        FixedLengthArrayAttributeType ??=
            Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.FixedLengthArrayAttribute")
            ?? Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.FixedSizeArrayAttribute");
        _fixedLengthArrayCtor ??= FixedLengthArrayAttributeType?.GetConstructor(new[] { typeof(int) });
        ExtensionAttributeType ??= Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.ExtensionAttribute");
        _extensionAttributeCtor ??= ExtensionAttributeType?.GetConstructor(Type.EmptyTypes);
        UnitType ??= Compilation.ResolveRuntimeType("System.Unit");
        TopLevelAttributeType ??= Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.TopLevelAttribute");
        _topLevelAttributeCtor ??= TopLevelAttributeType?.GetConstructor(Type.EmptyTypes);

        if (DiscriminatedUnionAttributeType is null)
        {
            DiscriminatedUnionAttributeType = ResolveReferencedRuntimeType(
                "System.Runtime.CompilerServices.UnionAttribute")
                ?? ResolveReferencedRuntimeType("System.Runtime.CompilerServices.DiscriminatedUnionAttribute");
            _discriminatedUnionCtor = DiscriminatedUnionAttributeType?.GetConstructor(Type.EmptyTypes);
        }

        if (UnionInterfaceType is null &&
            Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IUnion") is PENamedTypeSymbol unionInterfaceSymbol)
        {
            UnionInterfaceType = unionInterfaceSymbol.GetTypeInfo().AsType();
        }

    }

    private Type? ResolveReferencedRuntimeType(string metadataName)
    {
        if (Compilation.GetTypeByMetadataName(metadataName) is PENamedTypeSymbol metadataType)
            return metadataType.GetTypeInfo().AsType();

        return Compilation.ResolveRuntimeType(metadataName);
    }

    private void CreateExtensionMarkerNameAttributeType()
    {
        if (ExtensionMarkerNameAttributeType is not null)
            return;

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.ExtensionMarkerNameAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)]);
        var attrUsageBuilder = new CustomAttributeBuilder(
            attrUsageCtor,
            [AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate]);
        attrBuilder.SetCustomAttribute(attrUsageBuilder);

        var nameField = attrBuilder.DefineField(
            "<Name>k__BackingField",
            typeof(string),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        var propBuilder = attrBuilder.DefineProperty(
            "Name",
            PropertyAttributes.None,
            typeof(string),
            null);

        var getterMethod = attrBuilder.DefineMethod(
            "get_Name",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(string),
            Type.EmptyTypes);

        var ilGet = getterMethod.GetILGenerator();
        ilGet.Emit(OpCodes.Ldarg_0);
        ilGet.Emit(OpCodes.Ldfld, nameField);
        ilGet.Emit(OpCodes.Ret);

        propBuilder.SetGetMethod(getterMethod);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(string) });

        var ilCtor = ctorBuilder.GetILGenerator();
        var attributeCtor = typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (attributeCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Call, attributeCtor);
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Ldarg_1);
        ilCtor.Emit(OpCodes.Stfld, nameField);
        ilCtor.Emit(OpCodes.Ret);

        ExtensionMarkerNameAttributeType = attrBuilder.CreateType();
        _extensionMarkerNameCtor = ExtensionMarkerNameAttributeType.GetConstructor(new[] { typeof(string) });
    }

    private void CreateFixedLengthArrayAttributeType()
    {
        if (FixedLengthArrayAttributeType is not null)
            return;

        var attrBuilder = ModuleBuilder.DefineType(
            "System.Runtime.CompilerServices.FixedLengthArrayAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));

        var attrUsageCtor = typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)]);
        var attrUsageBuilder = new CustomAttributeBuilder(
            attrUsageCtor,
            [AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.ReturnValue]);
        attrBuilder.SetCustomAttribute(attrUsageBuilder);

        var lengthField = attrBuilder.DefineField(
            "<Length>k__BackingField",
            typeof(int),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        var propBuilder = attrBuilder.DefineProperty(
            "Length",
            PropertyAttributes.None,
            typeof(int),
            null);

        var getterMethod = attrBuilder.DefineMethod(
            "get_Size",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(int),
            Type.EmptyTypes);

        var ilGet = getterMethod.GetILGenerator();
        ilGet.Emit(OpCodes.Ldarg_0);
        ilGet.Emit(OpCodes.Ldfld, lengthField);
        ilGet.Emit(OpCodes.Ret);

        propBuilder.SetGetMethod(getterMethod);

        var ctorBuilder = attrBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(int) });

        var ilCtor = ctorBuilder.GetILGenerator();
        var attributeCtor = typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (attributeCtor is null)
            throw new InvalidOperationException("Missing Attribute base constructor.");

        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Call, attributeCtor);
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Ldarg_1);
        ilCtor.Emit(OpCodes.Stfld, lengthField);
        ilCtor.Emit(OpCodes.Ret);

        FixedLengthArrayAttributeType = attrBuilder.CreateType();
        _fixedLengthArrayCtor = FixedLengthArrayAttributeType.GetConstructor(new[] { typeof(int) });
    }

    internal CustomAttributeBuilder? CreateExtensionMarkerNameAttribute(string markerName)
    {
        if (string.IsNullOrWhiteSpace(markerName))
            return null;

        if (ExtensionMarkerNameAttributeType is null || _extensionMarkerNameCtor is null)
        {
            if (_emitExtensionMarkerNameAttribute)
                CreateExtensionMarkerNameAttributeType();
        }

        if (_extensionMarkerNameCtor is null)
            return null;

        return new CustomAttributeBuilder(_extensionMarkerNameCtor, new object[] { markerName });
    }

    internal CustomAttributeBuilder? CreateFixedLengthArrayAttribute(ITypeSymbol type)
    {
        if (type is not IArrayTypeSymbol { FixedLength: int fixedLength, Rank: 1 })
            return null;

        if (FixedLengthArrayAttributeType is null || _fixedLengthArrayCtor is null)
            CreateFixedLengthArrayAttributeType();

        if (_fixedLengthArrayCtor is null)
            return null;

        return new CustomAttributeBuilder(_fixedLengthArrayCtor, new object[] { fixedLength });
    }

    internal CustomAttributeBuilder? CreateExtensionAttributeBuilder()
    {
        if (ExtensionAttributeType is null)
            ExtensionAttributeType = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.ExtensionAttribute");

        _extensionAttributeCtor ??= ExtensionAttributeType?.GetConstructor(Type.EmptyTypes);
        if (_extensionAttributeCtor is null)
            return null;

        return new CustomAttributeBuilder(_extensionAttributeCtor, Array.Empty<object>());
    }

    internal CustomAttributeBuilder? CreateCompilerGeneratedAttributeBuilder()
    {
        if (_compilerGeneratedCtor is null)
        {
            var type = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.CompilerGeneratedAttribute");
            _compilerGeneratedCtor = type?.GetConstructor(Type.EmptyTypes);
        }

        if (_compilerGeneratedCtor is null)
            return null;

        return new CustomAttributeBuilder(_compilerGeneratedCtor, Array.Empty<object>());
    }

    internal CustomAttributeBuilder? CreateIsByRefLikeAttributeBuilder()
    {
        if (_isByRefLikeCtor is null)
        {
            var type = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.IsByRefLikeAttribute");
            _isByRefLikeCtor = type?.GetConstructor(Type.EmptyTypes);
        }

        if (_isByRefLikeCtor is null)
            return null;

        return new CustomAttributeBuilder(_isByRefLikeCtor, Array.Empty<object>());
    }

    internal CustomAttributeBuilder? CreateIsReadOnlyAttributeBuilder()
    {
        if (_isReadOnlyCtor is null)
        {
            var type = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.IsReadOnlyAttribute");
            _isReadOnlyCtor = type?.GetConstructor(Type.EmptyTypes);
        }

        if (_isReadOnlyCtor is null)
            return null;

        return new CustomAttributeBuilder(_isReadOnlyCtor, Array.Empty<object>());
    }

    internal CustomAttributeBuilder? CreateScopedRefAttributeBuilder()
    {
        if (_scopedRefCtor is null)
        {
            var type = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.ScopedRefAttribute");
            _scopedRefCtor = type?.GetConstructor(Type.EmptyTypes);
        }

        if (_scopedRefCtor is null)
            return null;

        return new CustomAttributeBuilder(_scopedRefCtor, Array.Empty<object>());
    }

    internal CustomAttributeBuilder? CreateTopLevelAttributeBuilder()
    {
        if (_topLevelAttributeCtor is null)
        {
            var type = Compilation.ResolveRuntimeType("System.Runtime.CompilerServices.TopLevelAttribute");
            _topLevelAttributeCtor = type?.GetConstructor(Type.EmptyTypes);
        }

        if (_topLevelAttributeCtor is null)
            return null;

        return new CustomAttributeBuilder(_topLevelAttributeCtor, Array.Empty<object>());
    }

    private void CreateUnitStruct()
    {
        if (UnitType is not null)
            return;

        var unitBuilder = ModuleBuilder.DefineType(
            "System.Unit",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.ValueType"), this));

        var valueField = unitBuilder.DefineField(
            "Value",
            unitBuilder,
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);

        var equalsMethod = unitBuilder.DefineMethod(
            "Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final,
            TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Boolean"), this),
            new[] { unitBuilder });
        var ilEquals = equalsMethod.GetILGenerator();
        ilEquals.Emit(OpCodes.Ldc_I4_1);
        ilEquals.Emit(OpCodes.Ret);

        var equalsObjMethod = unitBuilder.DefineMethod(
            "Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final,
            TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Boolean"), this),
            new[] { TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Object"), this) });
        var ilEqualsObj = equalsObjMethod.GetILGenerator();
        ilEqualsObj.Emit(OpCodes.Ldarg_1);
        ilEqualsObj.Emit(OpCodes.Isinst, unitBuilder);
        ilEqualsObj.Emit(OpCodes.Ldnull);
        ilEqualsObj.Emit(OpCodes.Cgt_Un);
        ilEqualsObj.Emit(OpCodes.Ret);

        var getHashCodeMethod = unitBuilder.DefineMethod(
            "GetHashCode",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.Int32"), this),
            Type.EmptyTypes);
        var ilHash = getHashCodeMethod.GetILGenerator();
        ilHash.Emit(OpCodes.Ldc_I4_0);
        ilHash.Emit(OpCodes.Ret);

        var toStringMethod = unitBuilder.DefineMethod(
            "ToString",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            TypeSymbolExtensionsForCodeGen.GetClrType(Compilation.GetTypeByMetadataName("System.String"), this),
            Type.EmptyTypes);
        var ilToString = toStringMethod.GetILGenerator();
        ilToString.Emit(OpCodes.Ldstr, "()");
        ilToString.Emit(OpCodes.Ret);

        UnitType = unitBuilder.CreateType();
    }

    private void DefineTypeBuilders()
    {
        PrintDebug("Defining type builders...");

        EnsureLoweredBoundNodes();

        var declaredTypes = Compilation.Module.GlobalNamespace
            .GetAllMembersRecursive()
            .OfType<ITypeSymbol>()
            .Where(t => t.DeclaringSyntaxReferences.Length > 0)
            .ToArray();

        var extensionTypes = Compilation.SyntaxTrees
            .Select(tree => (tree, model: Compilation.GetSemanticModel(tree)))
            .SelectMany(tuple => tuple.tree.GetRoot()
                .DescendantNodes()
                .OfType<ExtensionDeclarationSyntax>()
                .Select(decl => tuple.model.GetDeclaredSymbol(decl)))
            .OfType<ITypeSymbol>()
            .Where(t => t.DeclaringSyntaxReferences.Length > 0)
            .ToArray();

        var allTypes = declaredTypes
            .Cast<ISymbol>()
            .Concat(extensionTypes)
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<ITypeSymbol>()
            .ToArray();

        var unionCaseTypes = declaredTypes
            .OfType<IUnionSymbol>()
            .SelectMany(union => union.DeclaredCaseTypes)
            .OfType<ITypeSymbol>()
            .Where(t => t.DeclaringSyntaxReferences.Length > 0)
            .ToArray();

        var unionCompanionTypes = declaredTypes
            .OfType<SourceUnionSymbol>()
            .Select(union => union.CompanionType)
            .OfType<ITypeSymbol>()
            .ToArray();

        var synthesizedAsyncTypes = Compilation.GetSynthesizedAsyncStateMachineTypes().ToArray();
        var synthesizedDelegates = Compilation.GetSynthesizedDelegateTypes().ToArray();
        var synthesizedIterators = Compilation.GetSynthesizedIteratorTypes().ToArray();

        foreach (var typeSymbol in allTypes)
        {
            GetOrCreateTypeGenerator(typeSymbol);
        }

        foreach (var unionCaseType in unionCaseTypes)
        {
            GetOrCreateTypeGenerator(unionCaseType);
        }

        foreach (var unionCompanionType in unionCompanionTypes)
        {
            GetOrCreateTypeGenerator(unionCompanionType);
        }

        foreach (var asyncType in synthesizedAsyncTypes)
        {
            GetOrCreateTypeGenerator(asyncType);
        }

        foreach (var delegateType in synthesizedDelegates)
        {
            GetOrCreateTypeGenerator(delegateType);
        }

        foreach (var iteratorType in synthesizedIterators)
        {
            GetOrCreateTypeGenerator(iteratorType);
        }

        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var visiting = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var typeSymbol in allTypes)
        {
            EnsureTypeBuilderDefined(typeSymbol, visited, visiting);
        }

        foreach (var unionCaseType in unionCaseTypes)
        {
            EnsureTypeBuilderDefined(unionCaseType, visited, visiting);
        }

        foreach (var asyncType in synthesizedAsyncTypes)
        {
            EnsureTypeBuilderDefined(asyncType, visited, visiting);
        }

        foreach (var delegateType in synthesizedDelegates)
        {
            EnsureTypeBuilderDefined(delegateType, visited, visiting);
        }

        foreach (var iteratorType in synthesizedIterators)
        {
            EnsureTypeBuilderDefined(iteratorType, visited, visiting);
        }
    }

    private void EnsureLoweredBoundNodes()
    {
        foreach (var tree in Compilation.SyntaxTrees)
        {
            var semanticModel = Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            if (root is CompilationUnitSyntax compilationUnit)
            {
                var hasTopLevelStatements = compilationUnit.Members.Any(static member =>
                    member is GlobalStatementSyntax ||
                    member is FileScopedNamespaceDeclarationSyntax fileScoped && fileScoped.Members.OfType<GlobalStatementSyntax>().Any());

                if (hasTopLevelStatements)
                {
                    semanticModel.GetBoundNode(compilationUnit, BoundTreeView.Lowered);
                    EnsureTopLevelAsyncLowered(compilationUnit, semanticModel);
                }
            }

            foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                _ = semanticModel.GetDeclaredSymbol(methodDeclaration);

                if (methodDeclaration.Body is not null)
                {
                    semanticModel.GetBoundNode(methodDeclaration.Body, BoundTreeView.Original);
                    semanticModel.GetBoundNode(methodDeclaration.Body, BoundTreeView.Lowered);
                }

                if (methodDeclaration.ExpressionBody is not null)
                    semanticModel.GetBoundNode(methodDeclaration.ExpressionBody.Expression, BoundTreeView.Lowered);
            }

            foreach (var functionStatement in root.DescendantNodes().OfType<FunctionStatementSyntax>())
            {
                _ = semanticModel.GetDeclaredSymbol(functionStatement);

                if (functionStatement.Body is not null)
                {
                    semanticModel.GetBoundNode(functionStatement.Body, BoundTreeView.Original);
                    semanticModel.GetBoundNode(functionStatement.Body, BoundTreeView.Lowered);
                }

                if (functionStatement.ExpressionBody is not null)
                    semanticModel.GetBoundNode(functionStatement.ExpressionBody.Expression, BoundTreeView.Lowered);
            }

            foreach (var accessor in root.DescendantNodes().OfType<AccessorDeclarationSyntax>())
            {
                if (accessor.Body is not null)
                    semanticModel.GetBoundNode(accessor.Body, BoundTreeView.Lowered);

                if (accessor.ExpressionBody is not null)
                    semanticModel.GetBoundNode(accessor.ExpressionBody.Expression, BoundTreeView.Lowered);
            }

            foreach (var lambda in root.DescendantNodes().OfType<FunctionExpressionSyntax>())
                semanticModel.GetBoundNode(lambda, BoundTreeView.Lowered);
        }
    }

    private void EnsureTopLevelAsyncLowered(CompilationUnitSyntax compilationUnit, SemanticModel semanticModel)
    {
        var bindableGlobals = Compilation.GetBindableGlobalStatements(compilationUnit);
        var (_, _, asyncMain) = Compilation.GetOrCreateTopLevelProgram(
            compilationUnit,
            Compilation.SourceGlobalNamespace,
            bindableGlobals);

        if (asyncMain is null)
            return;

        if (semanticModel.GetBoundNode(compilationUnit, BoundTreeView.Original) is not BoundBlockStatement originalTopLevelBody)
            return;

        if (AsyncLowerer.ShouldRewrite(asyncMain, originalTopLevelBody))
            _ = AsyncLowerer.Rewrite(asyncMain, originalTopLevelBody);
    }

    internal TypeGenerator GetOrCreateTypeGenerator(ITypeSymbol typeSymbol)
    {
        var definitionTypeSymbol = GetDefinitionTypeSymbol(typeSymbol);
        if (!_typeGenerators.TryGetValue(definitionTypeSymbol, out var generator))
        {
            generator = new TypeGenerator(this, definitionTypeSymbol);
            _typeGenerators[definitionTypeSymbol] = generator;
        }

        return generator;
    }

    private void EnsureTypeBuilderDefined(ITypeSymbol typeSymbol, HashSet<ITypeSymbol> visited, HashSet<ITypeSymbol> visiting)
    {
        var originalTypeSymbol = typeSymbol;
        typeSymbol = GetDefinitionTypeSymbol(typeSymbol);

        if (visited.Contains(typeSymbol))
            return;

        if (!visiting.Add(typeSymbol))
            return;

        if (!_typeGenerators.TryGetValue(typeSymbol, out var generator))
        {
            visiting.Remove(typeSymbol);
            return;
        }

        if (generator.TypeBuilder is null && generator.Type is null)
        {
            if (originalTypeSymbol is INamedTypeSymbol named)
            {
                var baseType = named.BaseType;
                if (baseType is not null)
                    EnsureTypeDependencies(baseType, visited, visiting);

                foreach (var interfaceType in named.Interfaces)
                    EnsureTypeDependencies(interfaceType, visited, visiting);

                foreach (var typeArgument in named.TypeArguments)
                    EnsureTypeDependencies(typeArgument, visited, visiting);
            }
        }

        if (generator.TypeBuilder is null)
        {
            PrintDebug($"Defining type builder for {typeSymbol.ToDisplayString()}");
            generator.DefineTypeBuilder();
        }

        visiting.Remove(typeSymbol);
        visited.Add(typeSymbol);
    }

    private static ITypeSymbol GetDefinitionTypeSymbol(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol named &&
            named.IsGenericType &&
            named.ConstructedFrom is INamedTypeSymbol definition &&
            !ReferenceEquals(named, definition))
        {
            return definition;
        }

        return typeSymbol;
    }

    private void EnsureTypeDependencies(ITypeSymbol typeSymbol, HashSet<ITypeSymbol> visited, HashSet<ITypeSymbol> visiting)
    {
        if (typeSymbol.IsAlias && typeSymbol is IAliasSymbol { UnderlyingSymbol: ITypeSymbol aliasType })
        {
            EnsureTypeDependencies(aliasType, visited, visiting);
            return;
        }

        switch (typeSymbol)
        {
            case ITupleTypeSymbol tupleType:
                foreach (var element in tupleType.TupleElements)
                    EnsureTypeDependencies(element.Type, visited, visiting);
                break;
            case INamedTypeSymbol named:
                {
                    var definition = GetDefinitionTypeSymbol(named);
                    if (definition.DeclaringSyntaxReferences.Length > 0)
                        EnsureTypeBuilderDefined(definition, visited, visiting);

                    if (named.TypeArguments.IsDefaultOrEmpty)
                        break;

                    foreach (var typeArgument in named.TypeArguments)
                        EnsureTypeDependencies(typeArgument, visited, visiting);

                    break;
                }
            case IArrayTypeSymbol arrayType:
                EnsureTypeDependencies(arrayType.ElementType, visited, visiting);
                break;
            case RefTypeSymbol refTypeType:
                EnsureTypeDependencies(refTypeType.ElementType, visited, visiting);
                break;
            case IPointerTypeSymbol pointerType:
                EnsureTypeDependencies(pointerType.PointedAtType, visited, visiting);
                break;
            case NullableTypeSymbol nullableType:
                EnsureTypeDependencies(nullableType.UnderlyingType, visited, visiting);
                break;
            case LiteralTypeSymbol literalType:
                EnsureTypeDependencies(literalType.UnderlyingType, visited, visiting);
                break;
        }
    }

    private void DefineMemberBuilders()
    {
        PrintDebug("Defining member builders for all types.");
        foreach (var typeGenerator in _typeGenerators.Values.ToArray())
        {
            typeGenerator.DefineMemberBuilders();
        }

        PrintDebug("Completing interface implementations for all types.");
        foreach (var typeGenerator in _typeGenerators.Values.ToArray())
        {
            typeGenerator.CompleteInterfaceImplementations();
        }
    }

    private void ApplyDeferredTypeBuilderAttributes()
    {
        foreach (var typeGenerator in _typeGenerators.Values.ToArray())
            typeGenerator.ApplyDeferredTypeBuilderAttributes();
    }

    private void CreateTypes()
    {
        PrintDebug("Creating runtime types.");
        foreach (var typeGenerator in _typeGenerators.Values
            .OrderBy(generator => GetContainingTypeDepth(generator.TypeSymbol)))
        {
            typeGenerator.CreateType();
        }
    }

    private static int GetContainingTypeDepth(ITypeSymbol typeSymbol)
    {
        var depth = 0;
        var current = (typeSymbol as INamedTypeSymbol)?.ContainingType;
        while (current is not null)
        {
            depth++;
            current = current.ContainingType;
        }

        return depth;
    }

    private void PrintUncreatedModuleTypeBuilders()
    {
        var builders = new HashSet<TypeBuilder>();
        var moduleType = ModuleBuilder.GetType();

        foreach (var field in moduleType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var value = field.GetValue(ModuleBuilder);
            if (value is null)
                continue;

            if (value is TypeBuilder singleBuilder)
            {
                builders.Add(singleBuilder);
                continue;
            }

            if (value is string)
                continue;

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is TypeBuilder builder)
                        builders.Add(builder);
                }
            }
        }

        var uncreatedBuilders = builders.Where(builder => !builder.IsCreated()).ToArray();
        if (uncreatedBuilders.Length == 0)
            return;

        PrintDebug($"Found {uncreatedBuilders.Length} uncreated module type builders.");
        foreach (var builder in uncreatedBuilders)
        {
            PrintDebug($"Uncreated module type builder: {builder.FullName ?? builder.Name}");

            if (string.Equals(builder.Name, "<Module>", StringComparison.Ordinal))
                continue;

            try
            {
                builder.CreateType();
                PrintDebug($"Created module type builder: {builder.FullName ?? builder.Name}");
            }
            catch (Exception ex)
            {
                PrintDebug($"Failed to create module type builder: {builder.FullName ?? builder.Name} ({ex.GetType().Name})");
            }
        }
    }

    private void EmitMemberILBodies()
    {
        PrintDebug("Emitting IL bodies for members.");
        foreach (var typeGenerator in _typeGenerators.Values.ToArray())
        {
            typeGenerator.EmitMemberILBodies();
        }
    }

    private static Dictionary<int, int> GetMethodDebugInformationCorrections(
        MetadataReader sourceReader,
        MetadataReader targetReader)
    {
        // Mono.Cecil may renumber MethodDef rows while normalizing assembly
        // references. PDB method rows still describe the pre-normalized PE.
        // Type identity and same-name declaration order remain stable across
        // that rewrite, including overloads.
        var corrections = new Dictionary<int, int>();
        var sourceTypes = sourceReader.TypeDefinitions.ToDictionary(
            handle => GetTypeIdentity(sourceReader, handle),
            handle => handle,
            StringComparer.Ordinal);

        foreach (var targetTypeHandle in targetReader.TypeDefinitions)
        {
            if (!sourceTypes.TryGetValue(GetTypeIdentity(targetReader, targetTypeHandle), out var sourceTypeHandle))
                continue;

            var sourceMethods = GetMethodsByName(sourceReader, sourceTypeHandle);
            var targetMethods = GetMethodsByName(targetReader, targetTypeHandle);
            foreach (var (methodName, targetHandles) in targetMethods)
            {
                if (!sourceMethods.TryGetValue(methodName, out var sourceHandles) ||
                    sourceHandles.Count != targetHandles.Count)
                {
                    continue;
                }

                for (var index = 0; index < targetHandles.Count; index++)
                {
                    var sourceRow = MetadataTokens.GetRowNumber(sourceHandles[index]);
                    var targetRow = MetadataTokens.GetRowNumber(targetHandles[index]);
                    corrections[targetRow] = sourceRow;
                }
            }
        }

        var sourceMethodCount = sourceReader.GetTableRowCount(TableIndex.MethodDef);
        var targetMethodCount = targetReader.GetTableRowCount(TableIndex.MethodDef);
        if (sourceMethodCount != targetMethodCount ||
            corrections.Count != targetMethodCount ||
            corrections.Values.Distinct().Count() != sourceMethodCount)
        {
            return [];
        }

        return corrections.All(static pair => pair.Key == pair.Value)
            ? []
            : corrections;
    }

    private static Dictionary<int, int> GetMethodDebugInformationRows(
        MetadataReader metadataReader,
        Stream pdbStream)
    {
        pdbStream.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(
            pdbStream,
            MetadataStreamOptions.LeaveOpen);
        var pdbReader = provider.GetMetadataReader();
        var methodCount = metadataReader.GetTableRowCount(TableIndex.MethodDef);
        var debugInformationCount = pdbReader.GetTableRowCount(TableIndex.MethodDebugInformation);
        if (debugInformationCount == methodCount)
        {
            pdbStream.Position = 0;
            return [];
        }

        var rows = new Dictionary<int, int>();
        var debugInformationRow = 0;
        foreach (var methodHandle in metadataReader.MethodDefinitions)
        {
            var method = metadataReader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
                continue;

            rows[MetadataTokens.GetRowNumber(methodHandle)] = ++debugInformationRow;
        }

        if (debugInformationRow != debugInformationCount)
        {
            throw new InvalidOperationException(
                $"Portable PDB contains {debugInformationCount} method debug rows for " +
                $"{debugInformationRow} emitted method bodies and {methodCount} method definitions.");
        }

        pdbStream.Position = 0;
        return rows;
    }

    private static Dictionary<string, List<MethodDefinitionHandle>> GetMethodsByName(
        MetadataReader reader,
        TypeDefinitionHandle typeHandle)
    {
        var methods = new Dictionary<string, List<MethodDefinitionHandle>>(StringComparer.Ordinal);
        foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var name = reader.GetString(method.Name);
            if (!methods.TryGetValue(name, out var handles))
            {
                handles = [];
                methods.Add(name, handles);
            }

            handles.Add(methodHandle);
        }

        return methods;
    }

    private static string GetTypeIdentity(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        var type = reader.GetTypeDefinition(typeHandle);
        var name = reader.GetString(type.Name);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
            return $"{GetTypeIdentity(reader, declaringType)}+{name}";

        var namespaceName = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(namespaceName) ? name : $"{namespaceName}.{name}";
    }

    static DebugDirectoryBuilder EmitPdb(
        MetadataBuilder pdbBuilder,
        ImmutableArray<int> rowCounts,
        MethodDefinitionHandle entryPointHandle,
        Stream? pdbStream,
        string pdbFileName,
        out BlobContentId pdbContentId)
    {
        BlobBuilder portablePdbBlob = new BlobBuilder();
        PortablePdbBuilder portablePdbBuilder = new PortablePdbBuilder(pdbBuilder, rowCounts, entryPointHandle);
        pdbContentId = portablePdbBuilder.Serialize(portablePdbBlob);

        if (pdbStream is not null)
            portablePdbBlob.WriteContentTo(pdbStream);

        DebugDirectoryBuilder debugDirectoryBuilder = new DebugDirectoryBuilder();
        debugDirectoryBuilder.AddCodeViewEntry(pdbFileName, pdbContentId, portablePdbBuilder.FormatVersion);

        // In case embedded in PE:
        // debugDirectoryBuilder.AddEmbeddedPortablePdbEntry(portablePdbBlob, portablePdbBuilder.FormatVersion);
        return debugDirectoryBuilder;
    }

    internal static void EmitCorrectedPdb(
        Stream originalPdbStream,
        ImmutableArray<int> rowCounts,
        MethodDefinitionHandle entryPointHandle,
        IReadOnlyDictionary<int, int> corrections,
        BlobContentId contentId,
        Stream outputStream,
        IReadOnlyDictionary<int, int>? debugInformationRows = null)
    {
        if (corrections.Count == 0)
        {
            originalPdbStream.CopyTo(outputStream);
            return;
        }

        using var provider = MetadataReaderProvider.FromPortablePdbStream(
            originalPdbStream,
            MetadataStreamOptions.LeaveOpen);
        var reader = provider.GetMetadataReader();
        var builder = new MetadataBuilder();

        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            builder.AddDocument(
                builder.GetOrAddDocumentName(reader.GetString(document.Name)),
                CopyGuid(reader, builder, document.HashAlgorithm),
                CopyBlob(reader, builder, document.Hash),
                CopyGuid(reader, builder, document.Language));
        }

        var methodCount = rowCounts[(int)TableIndex.MethodDef];
        var debugInformationCount = reader.GetTableRowCount(TableIndex.MethodDebugInformation);
        for (var row = 1; row <= methodCount; row++)
        {
            var sourceRow = corrections[row];
            var sourceDebugInformationRow = debugInformationRows is null
                ? sourceRow
                : debugInformationRows.GetValueOrDefault(sourceRow);
            if (sourceDebugInformationRow == 0 || sourceDebugInformationRow > debugInformationCount)
            {
                builder.AddMethodDebugInformation(default, default);
                continue;
            }

            var info = reader.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(sourceDebugInformationRow));
            builder.AddMethodDebugInformation(
                info.Document,
                CopyBlob(reader, builder, info.SequencePointsBlob));
        }

        foreach (var handle in reader.ImportScopes)
        {
            var importScope = reader.GetImportScope(handle);
            builder.AddImportScope(
                importScope.Parent,
                CopyBlob(reader, builder, importScope.ImportsBlob));
        }

        var ownerToTarget = corrections.ToDictionary(static pair => pair.Value, static pair => pair.Key);
        var localScopes = reader.LocalScopes
            .Select(handle => reader.GetLocalScope(handle))
            .Select(localScope =>
            {
                var methodRow = MetadataTokens.GetRowNumber(localScope.Method);
                var correctedMethod = ownerToTarget.TryGetValue(methodRow, out var correctedMethodRow)
                    ? MetadataTokens.MethodDefinitionHandle(correctedMethodRow)
                    : localScope.Method;
                return (Scope: localScope, Method: correctedMethod);
            })
            .OrderBy(static item => MetadataTokens.GetRowNumber(item.Method))
            .ThenBy(static item => item.Scope.StartOffset)
            .ThenByDescending(static item => item.Scope.Length);
        foreach (var item in localScopes)
        {
            var localScope = item.Scope;
            var variableList = MetadataTokens.LocalVariableHandle(
                builder.GetRowCount(TableIndex.LocalVariable) + 1);
            foreach (var handle in localScope.GetLocalVariables())
            {
                var local = reader.GetLocalVariable(handle);
                builder.AddLocalVariable(
                    local.Attributes,
                    local.Index,
                    CopyString(reader, builder, local.Name));
            }

            var constantList = MetadataTokens.LocalConstantHandle(
                builder.GetRowCount(TableIndex.LocalConstant) + 1);
            foreach (var handle in localScope.GetLocalConstants())
            {
                var constant = reader.GetLocalConstant(handle);
                builder.AddLocalConstant(
                    CopyString(reader, builder, constant.Name),
                    CopyBlob(reader, builder, constant.Signature));
            }

            builder.AddLocalScope(
                item.Method,
                localScope.ImportScope,
                variableList,
                constantList,
                localScope.StartOffset,
                localScope.Length);
        }

        for (var row = 1; row <= methodCount; row++)
        {
            var sourceRow = corrections[row];
            var sourceDebugInformationRow = debugInformationRows is null
                ? sourceRow
                : debugInformationRows.GetValueOrDefault(sourceRow);
            if (sourceDebugInformationRow == 0 || sourceDebugInformationRow > debugInformationCount)
                continue;

            var info = reader.GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(sourceDebugInformationRow));
            var kickoffMethod = info.GetStateMachineKickoffMethod();
            if (!kickoffMethod.IsNil)
            {
                builder.AddStateMachineMethod(
                    MetadataTokens.MethodDefinitionHandle(row),
                    RemapMethod(kickoffMethod, ownerToTarget));
            }
        }

        foreach (var handle in reader.CustomDebugInformation)
        {
            var customInfo = reader.GetCustomDebugInformation(handle);
            builder.AddCustomDebugInformation(
                RemapDebugParent(customInfo.Parent, ownerToTarget),
                CopyGuid(reader, builder, customInfo.Kind),
                CopyBlob(reader, builder, customInfo.Value));
        }

        var correctedBlob = new BlobBuilder();
        var portablePdbBuilder = new PortablePdbBuilder(
            builder,
            rowCounts,
            entryPointHandle,
            _ => contentId);
        portablePdbBuilder.Serialize(correctedBlob);
        correctedBlob.WriteContentTo(outputStream);
    }

    private static EntityHandle RemapDebugParent(
        EntityHandle parent,
        IReadOnlyDictionary<int, int> ownerToTarget)
    {
        if (parent.Kind != HandleKind.MethodDefinition)
            return parent;

        var methodRow = MetadataTokens.GetRowNumber((MethodDefinitionHandle)parent);
        return ownerToTarget.TryGetValue(methodRow, out var correctedMethodRow)
            ? MetadataTokens.MethodDefinitionHandle(correctedMethodRow)
            : parent;
    }

    private static MethodDefinitionHandle RemapMethod(
        MethodDefinitionHandle method,
        IReadOnlyDictionary<int, int> ownerToTarget)
    {
        var methodRow = MetadataTokens.GetRowNumber(method);
        return ownerToTarget.TryGetValue(methodRow, out var correctedMethodRow)
            ? MetadataTokens.MethodDefinitionHandle(correctedMethodRow)
            : method;
    }

    private static BlobHandle CopyBlob(
        MetadataReader reader,
        MetadataBuilder builder,
        BlobHandle handle)
        => handle.IsNil ? default : builder.GetOrAddBlob(reader.GetBlobBytes(handle));

    private static GuidHandle CopyGuid(
        MetadataReader reader,
        MetadataBuilder builder,
        GuidHandle handle)
        => handle.IsNil ? default : builder.GetOrAddGuid(reader.GetGuid(handle));

    private static StringHandle CopyString(
        MetadataReader reader,
        MetadataBuilder builder,
        StringHandle handle)
        => handle.IsNil ? default : builder.GetOrAddString(reader.GetString(handle));

    private static ModuleBuilder DefineDynamicModuleWithSymbols(PersistedAssemblyBuilder assemblyBuilder, string moduleName)
    {
        var overload = typeof(PersistedAssemblyBuilder).GetMethod(
            "DefineDynamicModule",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(string), typeof(bool) },
            modifiers: null);

        if (overload is not null &&
            overload.Invoke(assemblyBuilder, new object[] { moduleName, true }) is ModuleBuilder withSymbols)
        {
            return withSymbols;
        }

        return assemblyBuilder.DefineDynamicModule(moduleName);
    }

    public bool TryGetRuntimeTypeForSymbol(INamedTypeSymbol symbol, out Type type)
    {
        if (symbol is ConstructedNamedTypeSymbol constructed &&
            constructed.ConstructedFrom is INamedTypeSymbol definition &&
            !SymbolEqualityComparer.Default.Equals(constructed, definition))
        {
            if (TryGetRuntimeTypeForSymbol(definition, out var definitionType))
            {
                if (!definitionType.IsGenericTypeDefinition && !definitionType.ContainsGenericParameters)
                {
                    type = definitionType;
                    return true;
                }

                var typeArguments = constructed.TypeArguments
                    .Select(arg => TypeSymbolExtensionsForCodeGen.GetClrType(arg, this))
                    .ToArray();

                type = typeArguments.Length == 0
                    ? definitionType
                    : definitionType.MakeGenericType(typeArguments);
                return true;
            }
        }

        var symbolDefinition = (INamedTypeSymbol)GetDefinitionTypeSymbol(symbol);
        if (_typeGenerators.TryGetValue(symbolDefinition, out var builder))
        {
            if (builder.TypeBuilder is not null)
            {
                type = builder.TypeBuilder;
                return true;
            }

            if (builder.Type is not null)
            {
                type = builder.Type;
                return true;
            }
        }

        // Async/state-machine and other synthesized symbols can be re-instantiated
        // across phases while preserving metadata identity. Fall back to metadata-name
        // matching when symbol identity lookup misses.
        var requestedMetadataName = symbolDefinition.ToFullyQualifiedMetadataName();
        foreach (var (candidateSymbol, candidateGenerator) in _typeGenerators)
        {
            if (!string.Equals(candidateSymbol.ToFullyQualifiedMetadataName(), requestedMetadataName, StringComparison.Ordinal))
                continue;

            if (candidateGenerator.TypeBuilder is not null)
            {
                type = candidateGenerator.TypeBuilder;
                return true;
            }

            if (candidateGenerator.Type is not null)
            {
                type = candidateGenerator.Type;
                return true;
            }
        }

        type = null!;
        return false;
    }

    internal bool TryEnsureRuntimeTypeForSymbol(INamedTypeSymbol symbol, out Type type)
    {
        if (TryGetRuntimeTypeForSymbol(symbol, out type))
            return true;

        var symbolDefinition = (INamedTypeSymbol)GetDefinitionTypeSymbol(symbol);
        if (_typeGenerators.TryGetValue(symbolDefinition, out var generator))
        {
            if (generator.TypeBuilder is null && generator.Type is null)
                generator.DefineTypeBuilder();

            return TryGetRuntimeTypeForSymbol(symbol, out type);
        }

        type = null!;
        return false;
    }

    internal bool TryGetRuntimeTypeForTypeParameter(ITypeParameterSymbol symbol, out Type type)
        => RuntimeTypeMap.TryResolveTypeParameter(symbol, RuntimeTypeUsage.Signature, out type);

    internal bool TryResolveRuntimeTypeParameter(ITypeParameterSymbol symbol, RuntimeTypeUsage usage, out Type type)
    {
        var normalized = (ITypeParameterSymbol)(symbol.OriginalDefinition ?? symbol);

        if (CodeGenFlags.PrintDebug)
        {
            var ownerIdentity = normalized.OwnerKind switch
            {
                TypeParameterOwnerKind.Method => RuntimeTypeParameterKey.GetMethodOwnerIdentity(
                    RuntimeTypeParameterKey.NormalizeMethodOwner(normalized.DeclaringMethodParameterOwner)),
                TypeParameterOwnerKind.Type => RuntimeTypeParameterKey.GetTypeOwnerIdentity(
                    RuntimeTypeParameterKey.NormalizeTypeOwner(normalized.DeclaringTypeParameterOwner)),
                _ => null
            };

            PrintDebug(
                $"[CodeGen:TypeParam] Lookup {symbol.Name} (ordinal={symbol.Ordinal}, owner={symbol.OwnerKind}, ownerId={ownerIdentity ?? "<null>"})");
        }

        if (TryGetGenericParameterStack(symbol, out var stack) && stack.Count > 0)
        {
            var top = stack.Peek();
            if (!(usage == RuntimeTypeUsage.MethodBody &&
                  symbol.OwnerKind == TypeParameterOwnerKind.Method &&
                  IsSignaturePlaceholderType(top)))
            {
                if (CodeGenFlags.PrintDebug)
                {
                    PrintDebug(
                        $"[CodeGen:TypeParam] Lookup hit {symbol.Name} -> {top} (isMethodParam={top.IsGenericMethodParameter}, isTypeParam={top.IsGenericTypeParameter}, depth={stack.Count})");
                }

                type = top;
                return true;
            }

            if (symbol.OwnerKind == TypeParameterOwnerKind.Method && usage == RuntimeTypeUsage.MethodBody)
            {
                foreach (var candidate in stack)
                {
                    if (!candidate.IsGenericParameter || IsSignaturePlaceholderType(candidate))
                        continue;

                    if (CodeGenFlags.PrintDebug)
                    {
                        PrintDebug(
                            $"[CodeGen:TypeParam] Lookup fallback runtime parameter {symbol.Name} -> {candidate} (isMethodParam={candidate.IsGenericMethodParameter}, isTypeParam={candidate.IsGenericTypeParameter}, depth={stack.Count})");
                    }

                    type = candidate;
                    return true;
                }
            }
        }

        if (normalized.OwnerKind == TypeParameterOwnerKind.Method &&
            TryResolveFromCurrentEmittingMethod(normalized, usage, out var currentMethodType))
        {
            type = CacheRuntimeTypeParameter(symbol, currentMethodType);
            return true;
        }

        if (normalized is PETypeParameterSymbol normalizedPeTypeParameter &&
            TryResolveMetadataTypeParameter(normalizedPeTypeParameter, out var normalizedResolved))
        {
            if (usage == RuntimeTypeUsage.MethodBody && IsSignaturePlaceholderType(normalizedResolved))
            {
                if (CodeGenFlags.PrintDebug)
                {
                    PrintDebug(
                        $"[CodeGen:TypeParam] Ignore signature placeholder {normalized.Name} for method body resolution (metadata owner).");
                }
            }
            else
            {
                type = CacheRuntimeTypeParameter(symbol, normalizedResolved);
                return true;
            }
        }

        if (symbol is PETypeParameterSymbol peTypeParameter && TryResolveMetadataTypeParameter(peTypeParameter, out var resolved))
        {
            if (usage == RuntimeTypeUsage.MethodBody && IsSignaturePlaceholderType(resolved))
            {
                if (CodeGenFlags.PrintDebug)
                {
                    PrintDebug(
                        $"[CodeGen:TypeParam] Ignore signature placeholder {symbol.Name} for method body resolution (direct metadata owner).");
                }
            }
            else
            {
                type = CacheRuntimeTypeParameter(symbol, resolved);
                return true;
            }
        }

        if (normalized.Ordinal >= 0)
        {
            if (normalized.OwnerKind == TypeParameterOwnerKind.Method)
            {
                if (usage == RuntimeTypeUsage.Signature)
                {
                    type = CacheRuntimeTypeParameter(symbol, Type.MakeGenericMethodParameter(normalized.Ordinal));
                    return true;
                }
            }
        }

        type = null!;
        if (CodeGenFlags.PrintDebug)
            PrintDebug($"[CodeGen:TypeParam] Lookup miss {symbol.Name}");
        return false;
    }

    private bool TryResolveFromCurrentEmittingMethod(
        ITypeParameterSymbol normalizedMethodParameter,
        RuntimeTypeUsage usage,
        out Type type)
    {
        type = null!;

        var currentMethod = CurrentEmittingMethod;
        if (currentMethod is null || currentMethod.TypeParameters.IsDefaultOrEmpty)
            return false;

        var ordinal = normalizedMethodParameter.Ordinal;
        if ((uint)ordinal >= (uint)currentMethod.TypeParameters.Length)
            return false;

        var currentTypeParameter = currentMethod.TypeParameters[ordinal];
        if (!TryGetGenericParameterStack(currentTypeParameter, out var currentStack) || currentStack.Count == 0)
            return false;

        if (usage == RuntimeTypeUsage.MethodBody)
        {
            foreach (var candidate in currentStack)
            {
                if (!candidate.IsGenericParameter || !candidate.IsGenericMethodParameter || candidate.IsGenericTypeParameter)
                    continue;

                if (IsSignaturePlaceholderType(candidate))
                    continue;

                type = candidate;
                return true;
            }
        }

        var top = currentStack.Peek();
        if (usage == RuntimeTypeUsage.MethodBody &&
            normalizedMethodParameter.OwnerKind == TypeParameterOwnerKind.Method &&
            IsSignaturePlaceholderType(top))
        {
            return false;
        }

        type = top;
        return true;
    }

    private static bool IsSignaturePlaceholderType(Type type)
    {
        try
        {
            _ = type.Assembly;
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private readonly struct RuntimeTypeParameterKey : IEquatable<RuntimeTypeParameterKey>
    {
        private RuntimeTypeParameterKey(
            TypeParameterOwnerKind ownerKind,
            int ordinal,
            string? methodOwnerIdentity,
            string? typeOwnerIdentity)
        {
            OwnerKind = ownerKind;
            Ordinal = ordinal;
            MethodOwnerIdentity = methodOwnerIdentity;
            TypeOwnerIdentity = typeOwnerIdentity;
        }

        public TypeParameterOwnerKind OwnerKind { get; }
        public int Ordinal { get; }
        public string? MethodOwnerIdentity { get; }
        public string? TypeOwnerIdentity { get; }

        public static RuntimeTypeParameterKey Create(ITypeParameterSymbol parameter)
        {
            var normalized = (ITypeParameterSymbol)(parameter.OriginalDefinition ?? parameter);
            return normalized.OwnerKind switch
            {
                TypeParameterOwnerKind.Method => new RuntimeTypeParameterKey(
                    normalized.OwnerKind,
                    normalized.Ordinal,
                    GetMethodOwnerIdentity(NormalizeMethodOwner(normalized.DeclaringMethodParameterOwner)),
                    null),
                TypeParameterOwnerKind.Type => new RuntimeTypeParameterKey(
                    normalized.OwnerKind,
                    normalized.Ordinal,
                    null,
                    GetTypeOwnerIdentity(NormalizeTypeOwner(normalized.DeclaringTypeParameterOwner))),
                _ => new RuntimeTypeParameterKey(normalized.OwnerKind, normalized.Ordinal, null, null),
            };
        }

        public bool Equals(RuntimeTypeParameterKey other)
        {
            return OwnerKind == other.OwnerKind &&
                   Ordinal == other.Ordinal &&
                   string.Equals(MethodOwnerIdentity, other.MethodOwnerIdentity, StringComparison.Ordinal) &&
                   string.Equals(TypeOwnerIdentity, other.TypeOwnerIdentity, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
            => obj is RuntimeTypeParameterKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(
                (int)OwnerKind,
                Ordinal,
                MethodOwnerIdentity is null ? 0 : StringComparer.Ordinal.GetHashCode(MethodOwnerIdentity),
                TypeOwnerIdentity is null ? 0 : StringComparer.Ordinal.GetHashCode(TypeOwnerIdentity));

        internal static IMethodSymbol? NormalizeMethodOwner(IMethodSymbol? method)
        {
            if (method is null)
                return null;

            while (method.UnderlyingSymbol is IMethodSymbol underlying &&
                   !ReferenceEquals(underlying, method))
            {
                method = underlying;
            }

            if (method is ConstructedMethodSymbol constructed)
                method = constructed.Definition;

            return (IMethodSymbol?)(method.OriginalDefinition ?? method);
        }

        internal static string? GetMethodOwnerIdentity(IMethodSymbol? method)
        {
            if (method is null)
                return null;

            var containingType = method.ContainingType?.ToFullyQualifiedMetadataName() ?? "<global>";
            return $"{containingType}::{method.MetadataName}/{method.Arity}/{method.Parameters.Length}";
        }

        internal static INamedTypeSymbol? NormalizeTypeOwner(INamedTypeSymbol? type)
        {
            if (type is null)
                return null;

            while (type.UnderlyingSymbol is INamedTypeSymbol underlying &&
                   !ReferenceEquals(underlying, type))
            {
                type = underlying;
            }

            if (type is IConstructedTypeSubstitutionInfo constructed)
                return constructed.DefinitionForSubstitution;

            return (INamedTypeSymbol?)(type.OriginalDefinition ?? type);
        }

        internal static string? GetTypeOwnerIdentity(INamedTypeSymbol? type)
        {
            return type?.ToFullyQualifiedMetadataName();
        }
    }

    private bool TryResolveMetadataTypeParameter(PETypeParameterSymbol symbol, out Type type)
    {
        if (!_resolvingMetadataTypeParameters.Add(symbol.MetadataIdentity))
        {
            type = null!;
            return false;
        }

        try
        {
            if (symbol.OwnerKind == TypeParameterOwnerKind.Type &&
                symbol.DeclaringTypeParameterOwner is INamedTypeSymbol containingType)
            {
                var runtimeType = RuntimeSymbolResolver.GetType(containingType, treatUnitAsVoid: true);
                var parameters = runtimeType.IsGenericTypeDefinition
                    ? runtimeType.GetTypeInfo().GenericTypeParameters
                    : runtimeType.GetGenericTypeDefinition().GetTypeInfo().GenericTypeParameters;

                var ordinal = symbol.Ordinal;
                if ((uint)ordinal < (uint)parameters.Length)
                {
                    type = parameters[ordinal];
                    return true;
                }
            }
            else if (symbol.OwnerKind == TypeParameterOwnerKind.Method &&
                     symbol.DeclaringMethodParameterOwner is IMethodSymbol containingMethod)
            {
                if (TryGetRuntimeMethod(containingMethod, out var methodInfo))
                {
                    var parameters = methodInfo.IsGenericMethodDefinition
                        ? methodInfo.GetGenericArguments()
                        : methodInfo.GetGenericMethodDefinition().GetGenericArguments();

                    var ordinal = symbol.Ordinal;
                    if ((uint)ordinal < (uint)parameters.Length)
                    {
                        type = parameters[ordinal];
                        return true;
                    }
                }

                try
                {
                    var resolvedMethod = RuntimeSymbolResolver.GetMethodInfo(containingMethod);
                    var parameters = resolvedMethod.IsGenericMethodDefinition
                        ? resolvedMethod.GetGenericArguments()
                        : resolvedMethod.GetGenericMethodDefinition().GetGenericArguments();

                    if ((uint)symbol.Ordinal < (uint)parameters.Length)
                    {
                        type = parameters[symbol.Ordinal];
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            type = null!;
            return false;
        }
        finally
        {
            _resolvingMetadataTypeParameters.Remove(symbol.MetadataIdentity);
        }
    }

}

internal readonly struct MemberBuilderCacheKey : IEquatable<MemberBuilderCacheKey>
{
    public MemberBuilderCacheKey(SourceSymbol symbol, ImmutableArray<ITypeSymbol> substitution)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        Substitution = substitution;
    }

    public SourceSymbol Symbol { get; }

    public ImmutableArray<ITypeSymbol> Substitution { get; }

    public bool Equals(MemberBuilderCacheKey other)
    {
        if (!SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol))
            return false;

        if (Substitution.IsDefaultOrEmpty && other.Substitution.IsDefaultOrEmpty)
            return true;

        if (Substitution.Length != other.Substitution.Length)
            return false;

        for (var i = 0; i < Substitution.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(Substitution[i], other.Substitution[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is MemberBuilderCacheKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = SymbolEqualityComparer.Default.GetHashCode(Symbol);

        if (!Substitution.IsDefaultOrEmpty)
        {
            for (var i = 0; i < Substitution.Length; i++)
            {
                hash = HashCode.Combine(hash, SymbolEqualityComparer.Default.GetHashCode(Substitution[i]));
            }
        }

        return hash;
    }
}
