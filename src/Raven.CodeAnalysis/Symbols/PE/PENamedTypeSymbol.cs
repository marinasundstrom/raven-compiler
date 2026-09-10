using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;

using Raven.CodeAnalysis;

namespace Raven.CodeAnalysis.Symbols;

internal partial class PENamedTypeSymbol : PESymbol, INamedTypeSymbol
{
    private static readonly Dictionary<string, SpecialType> s_specialTypeByFullName = new(StringComparer.Ordinal)
    {
        ["System.Object"] = SpecialType.System_Object,
        ["System.Enum"] = SpecialType.System_Enum,
        ["System.MulticastDelegate"] = SpecialType.System_MulticastDelegate,
        ["System.Delegate"] = SpecialType.System_Delegate,
        ["System.ValueType"] = SpecialType.System_ValueType,
        ["System.Void"] = SpecialType.System_Void,
        ["System.Boolean"] = SpecialType.System_Boolean,
        ["System.Char"] = SpecialType.System_Char,
        ["System.SByte"] = SpecialType.System_SByte,
        ["System.Byte"] = SpecialType.System_Byte,
        ["System.Int16"] = SpecialType.System_Int16,
        ["System.UInt16"] = SpecialType.System_UInt16,
        ["System.Int32"] = SpecialType.System_Int32,
        ["System.UInt32"] = SpecialType.System_UInt32,
        ["System.Int64"] = SpecialType.System_Int64,
        ["System.UInt64"] = SpecialType.System_UInt64,
        ["System.Decimal"] = SpecialType.System_Decimal,
        ["System.Single"] = SpecialType.System_Single,
        ["System.Double"] = SpecialType.System_Double,
        ["System.String"] = SpecialType.System_String,
        ["System.IntPtr"] = SpecialType.System_IntPtr,
        ["System.UIntPtr"] = SpecialType.System_UIntPtr,
        ["System.Array"] = SpecialType.System_Array,
        ["System.Collections.IEnumerable"] = SpecialType.System_Collections_IEnumerable,
        ["System.Collections.Generic.IEnumerable`1"] = SpecialType.System_Collections_Generic_IEnumerable_T,
        ["System.Collections.Generic.IList`1"] = SpecialType.System_Collections_Generic_IList_T,
        ["System.Collections.Generic.ICollection`1"] = SpecialType.System_Collections_Generic_ICollection_T,
        ["System.Collections.IEnumerator"] = SpecialType.System_Collections_IEnumerator,
        ["System.Collections.Generic.IEnumerator`1"] = SpecialType.System_Collections_Generic_IEnumerator_T,
        ["System.Nullable`1"] = SpecialType.System_Nullable_T,
        ["System.DateTime"] = SpecialType.System_DateTime,
        ["System.Runtime.CompilerServices.IsVolatile"] = SpecialType.System_Runtime_CompilerServices_IsVolatile,
        ["System.IDisposable"] = SpecialType.System_IDisposable,
        ["System.TypedReference"] = SpecialType.System_TypedReference,
        ["System.ArgIterator"] = SpecialType.System_ArgIterator,
        ["System.RuntimeArgumentHandle"] = SpecialType.System_RuntimeArgumentHandle,
        ["System.RuntimeFieldHandle"] = SpecialType.System_RuntimeFieldHandle,
        ["System.RuntimeMethodHandle"] = SpecialType.System_RuntimeMethodHandle,
        ["System.RuntimeTypeHandle"] = SpecialType.System_RuntimeTypeHandle,
        ["System.IAsyncResult"] = SpecialType.System_IAsyncResult,
        ["System.AsyncCallback"] = SpecialType.System_AsyncCallback,
        ["System.Runtime.CompilerServices.AsyncVoidMethodBuilder"] = SpecialType.System_Runtime_CompilerServices_AsyncVoidMethodBuilder,
        ["System.Runtime.CompilerServices.AsyncTaskMethodBuilder"] = SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder,
        ["System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1"] = SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder_T,
        ["System.Runtime.CompilerServices.AsyncStateMachineAttribute"] = SpecialType.System_Runtime_CompilerServices_AsyncStateMachineAttribute,
        ["System.Runtime.CompilerServices.IteratorStateMachineAttribute"] = SpecialType.System_Runtime_CompilerServices_IteratorStateMachineAttribute,
        ["System.Threading.Tasks.Task"] = SpecialType.System_Threading_Tasks_Task,
        ["System.Threading.Tasks.Task`1"] = SpecialType.System_Threading_Tasks_Task_T,
        ["System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken"] = SpecialType.System_Runtime_InteropServices_WindowsRuntime_EventRegistrationToken,
        ["System.Runtime.InteropServices.WindowsRuntime.EventRegistrationTokenTable`1"] = SpecialType.System_Runtime_InteropServices_WindowsRuntime_EventRegistrationTokenTable_T,
        ["System.ValueTuple`1"] = SpecialType.System_ValueTuple_T1,
        ["System.ValueTuple`2"] = SpecialType.System_ValueTuple_T2,
        ["System.ValueTuple`3"] = SpecialType.System_ValueTuple_T3,
        ["System.ValueTuple`4"] = SpecialType.System_ValueTuple_T4,
        ["System.ValueTuple`5"] = SpecialType.System_ValueTuple_T5,
        ["System.ValueTuple`6"] = SpecialType.System_ValueTuple_T6,
        ["System.ValueTuple`7"] = SpecialType.System_ValueTuple_T7,
        ["System.ValueTuple`8"] = SpecialType.System_ValueTuple_TRest,
        ["System.Type"] = SpecialType.System_Type,
        ["System.Exception"] = SpecialType.System_Exception,
        ["System.Runtime.CompilerServices.IAsyncStateMachine"] = SpecialType.System_Runtime_CompilerServices_IAsyncStateMachine,
    };

    protected readonly ReflectionTypeLoader _reflectionTypeLoader;
    protected readonly System.Reflection.TypeInfo _typeInfo;
    private readonly PETypeIdentity _metadataIdentity;
    private readonly bool _isValueType;
    private readonly List<ISymbol> _members = new(); //new(SymbolEqualityComparer.Default);
    private readonly object _membersGate = new();
    private readonly HashSet<string> _memberNamesLoaded = new(StringComparer.Ordinal);
    private INamedTypeSymbol? _baseType;
    private bool _membersLoaded;
    private ImmutableArray<ITypeParameterSymbol>? _typeParameters;
    private ITypeSymbol? _enumUnderlyingType;
    private string? _name;
    private ImmutableArray<INamedTypeSymbol>? _interfaces;
    private ImmutableArray<INamedTypeSymbol>? _allInterfaces;
    private readonly ITypeSymbol? _constructedFrom;
    private readonly ITypeSymbol? _originalDefinition;
    private bool _extensionReceiverTypeComputed;
    private ITypeSymbol? _extensionReceiverType;
    private bool _extensionMarkerMembersComputed;
    private bool _hasExtensionMarkerMembers;
    private ImmutableArray<INamedTypeSymbol>? _nestedTypes;
    private ImmutableArray<INamedTypeSymbol>? _permittedDirectSubtypes;
    private bool? _isSealedHierarchy;
    private Accessibility? _accessibility;

    internal static PENamedTypeSymbol Create(
        ReflectionTypeLoader reflectionTypeLoader,
        System.Reflection.TypeInfo typeInfo,
        ISymbol containingSymbol,
        INamedTypeSymbol? containingType,
        INamespaceSymbol? containingNamespace,
        Location[] locations)
    {
        var name = typeInfo.Name;

        foreach (var attribute in GetCustomAttributesSafe(typeInfo))
        {
            var attributeName = GetAttributeTypeName(attribute);
            if (attributeName is null)
                continue;

            if (attributeName is "Raven.Runtime.CompilerServices.RavenUnionCompanionAttribute")
            {
                return new PEUnionCompanionSymbol(
                    reflectionTypeLoader,
                    typeInfo,
                    containingSymbol,
                    containingNamespace,
                    locations);
            }

            if (attributeName is "System.Runtime.CompilerServices.UnionAttribute" &&
                LooksLikeCSharpUnion(typeInfo))
            {
                // Do not eagerly load members during construction; keep loading lazy to avoid re-entrancy/duplication.
                return new PEUnionSymbol(reflectionTypeLoader, typeInfo, containingSymbol, containingType, containingNamespace, locations).AddAsMember();
            }

        }

        IUnionSymbol? parentUnion = containingType as IUnionSymbol;
        var metadataContainingType = containingType;

        if (parentUnion is null &&
            containingType is PEUnionCompanionSymbol companion &&
            companion.TryGetAssociatedUnion(out var associatedUnion))
        {
            parentUnion = associatedUnion;
        }

        if (parentUnion is not null && !(typeInfo.IsInterface && name == "IUnionMembers"))
        {
            var caseSymbol = new PEUnionCaseSymbol(
                reflectionTypeLoader,
                typeInfo,
                parentUnion,
                metadataContainingType ?? parentUnion,
                containingNamespace,
                locations,
                parentUnion);

            caseSymbol.AddAsMember(parentUnion, containingNamespace);
            if (metadataContainingType is not null &&
                !ReferenceEquals(metadataContainingType, parentUnion))
            {
                caseSymbol.AddAsMember(metadataContainingType, containingNamespace);
            }

            return caseSymbol;
        }

        return new PENamedTypeSymbol(reflectionTypeLoader, typeInfo, containingSymbol, containingType, containingNamespace, locations, addAsMember: false)
            .AddAsMember();
    }

    private static bool LooksLikeCSharpUnion(System.Reflection.TypeInfo typeInfo)
    {
        try
        {
            if (!typeInfo.IsClass &&
                !(typeInfo.IsValueType && !typeInfo.IsEnum))
            {
                return false;
            }

            var provider = typeInfo.DeclaredNestedTypes.FirstOrDefault(type =>
                type.IsNestedPublic && type.IsInterface && type.Name == "IUnionMembers");
            if (provider is not null)
            {
                return typeInfo.ImplementedInterfaces.Any(implemented =>
                    (implemented.IsGenericType ? implemented.GetGenericTypeDefinition() : implemented) == provider.AsType()) &&
                    provider.DeclaredMethods.Any(method => method.Name == "Create" &&
                    method.IsStatic && method.IsPublic && !method.IsGenericMethod &&
                    method.GetParameters() is [var parameter] && IsUnionConstructorParameter(parameter) &&
                    IsProviderFactoryReturnType(method.ReturnType, typeInfo.AsType())) &&
                    provider.DeclaredProperties.Any(property => property.Name == "Value" &&
                        property.GetMethod is { IsPublic: true, IsStatic: false } && property.GetIndexParameters().Length == 0 &&
                        IsObjectType(property.PropertyType));
            }

            if (!typeInfo.DeclaredConstructors.Any(static constructor =>
                    constructor.IsPublic &&
                    !constructor.IsStatic &&
                    constructor.GetParameters() is [var parameter] &&
                    IsUnionConstructorParameter(parameter)))
            {
                return false;
            }

            return typeInfo.DeclaredProperties.Any(static property =>
                string.Equals(property.Name, "Value", StringComparison.Ordinal) &&
                property.GetMethod is { IsPublic: true, IsStatic: false } getter &&
                getter.GetParameters().Length == 0 &&
                IsObjectType(property.PropertyType));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsProviderFactoryReturnType(Type returnType, Type carrier)
    {
        if (!carrier.IsGenericType)
            return returnType == carrier;
        if (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != carrier.GetGenericTypeDefinition())
            return false;

        return returnType.GetGenericArguments().Zip(carrier.GetGenericArguments()).All(pair =>
            pair.First == pair.Second || pair.First.IsGenericParameter && pair.Second.IsGenericParameter &&
            pair.First.GenericParameterPosition == pair.Second.GenericParameterPosition);
    }

    private static bool IsObjectType(Type type)
        => string.Equals(type.FullName, "System.Object", StringComparison.Ordinal);

    private static bool IsUnionConstructorParameter(ParameterInfo parameter)
        => !parameter.ParameterType.IsByRef || parameter.IsIn;

    private static bool IsValueTypeLike(System.Reflection.TypeInfo typeInfo)
    {
        var runtimeType = typeInfo.AsType();

        try
        {
            if (runtimeType.IsValueType || runtimeType.IsPrimitive || runtimeType.IsEnum)
                return true;

            var baseTypeName = typeInfo.BaseType?.FullName;
            return baseTypeName == "System.ValueType" || baseTypeName == "System.Enum";
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or TypeLoadException)
        {
            // MetadataLoadContext can throw while resolving incomplete type graphs.
            // Fall back to stable runtime shape checks for known value-type families.
            var fullName = runtimeType.FullName ?? typeInfo.FullName ?? string.Empty;
            return fullName is "System.Decimal" or "System.DateTime" or "System.TimeSpan" or "System.Guid";
        }
    }

    internal static IEnumerable<CustomAttributeData> GetCustomAttributesSafe(System.Reflection.TypeInfo typeInfo)
    {
        IList<CustomAttributeData> attributes;
        try
        {
            attributes = typeInfo.GetCustomAttributesData();
        }
        catch (ArgumentException)
        {
            yield break;
        }

        foreach (var attribute in attributes)
        {
            CustomAttributeData? safeAttribute;
            try
            {
                safeAttribute = attribute;
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return safeAttribute;
        }
    }

    internal static string? GetAttributeTypeName(CustomAttributeData attribute)
    {
        try
        {
            return attribute.AttributeType.FullName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                BadImageFormatException or
                TypeLoadException or
                System.IO.FileNotFoundException)
        {
            return null;
        }
    }

    internal bool HasCustomAttribute(Func<string, bool> predicate)
    {
        foreach (var attribute in GetCustomAttributesSafe(_typeInfo))
        {
            var attributeName = GetAttributeTypeName(attribute);
            if (attributeName is not null && predicate(attributeName))
                return true;
        }

        return false;
    }

    public PENamedTypeSymbol(ReflectionTypeLoader reflectionTypeLoader, System.Reflection.TypeInfo typeInfo, ISymbol containingSymbol, INamedTypeSymbol? containingType, INamespaceSymbol? containingNamespace, Location[] locations, bool addAsMember = true)
        : base(containingSymbol, containingType, containingNamespace, locations, addAsMember: addAsMember)
    {
        _reflectionTypeLoader = reflectionTypeLoader;
        _typeInfo = typeInfo;
        _metadataIdentity = new PETypeIdentity(
            typeInfo.Assembly.GetName().Name ?? string.Empty,
            BuildMetadataName(typeInfo.AsType()));

        _isValueType = IsValueTypeLike(typeInfo);

        if (typeInfo.Name == "Object" || TryGetBaseTypeName(typeInfo) == "Object")
        {
            TypeKind = TypeKind.Class;
            (_constructedFrom, _originalDefinition) = ResolveGenericOrigins();
            return;
        }

        if (IsEnumType(typeInfo))
            TypeKind = TypeKind.Enum;
        else if (IsDelegateType(typeInfo))
            TypeKind = TypeKind.Delegate;
        else if (_isValueType)
            TypeKind = TypeKind.Struct;
        else if (typeInfo.IsInterface)
            TypeKind = TypeKind.Interface;
        else if (typeInfo.IsPointer)
            TypeKind = TypeKind.Pointer;
        else if (typeInfo.IsArray)
            TypeKind = TypeKind.Array;
        else
            TypeKind = TypeKind.Class;

        (_constructedFrom, _originalDefinition) = ResolveGenericOrigins();
    }

    private static bool IsEnumType(System.Reflection.TypeInfo typeInfo)
    {
        try
        {
            if (typeInfo.IsEnum)
                return true;
        }
        catch (Exception exception) when (IsIncompleteMetadataGraphException(exception))
        {
            // MetadataLoadContext can throw for incomplete type graphs.
        }

        try
        {
            if (typeInfo.FullName == "System.Enum" || typeInfo.AsType().FullName == "System.Enum")
                return false;

            return typeInfo.BaseType?.FullName == "System.Enum";
        }
        catch (Exception exception) when (IsIncompleteMetadataGraphException(exception))
        {
            return false;
        }
    }

    internal PETypeIdentity MetadataIdentity => _metadataIdentity;

    public override Accessibility DeclaredAccessibility => _accessibility ??= MapAccessibility(_typeInfo.AsType());

    internal ITypeSymbol? GetExtensionReceiverType()
    {
        if (_extensionReceiverTypeComputed)
            return _extensionReceiverType;

        _extensionReceiverTypeComputed = true;

        foreach (var methodInfo in GetDeclaredMethodsSafe())
        {
            if (!methodInfo.IsStatic || !HasExtensionAttribute(GetCustomAttributesSafe(methodInfo)))
                continue;

            if (!TryGetFirstParameterType(methodInfo, out var receiverType))
                continue;

            _extensionReceiverType = receiverType;
            break;
        }

        if (_extensionReceiverType is null)
        {
            if (!HasExtensionMarkerMembers())
                return null;

            _extensionReceiverType = GetFirstExtensionMarkerReceiverType();
            if (_extensionReceiverType is null)
            {
                foreach (var member in GetMembers())
                {
                    if (member is PEMethodSymbol peMethod && peMethod.TryGetExtensionMarkerName(out _))
                    {
                        _extensionReceiverType = GetExtensionMarkerReceiverType(peMethod);
                        break;
                    }

                    if (member is PEPropertySymbol peProperty && peProperty.TryGetExtensionMarkerName(out _))
                    {
                        _extensionReceiverType = GetExtensionMarkerReceiverType(peProperty);
                        break;
                    }
                }
            }
        }

        return _extensionReceiverType;
    }

    internal bool HasExtensionMarkerMembers()
    {
        if (_extensionMarkerMembersComputed)
            return _hasExtensionMarkerMembers;

        _extensionMarkerMembersComputed = true;

        foreach (var methodInfo in GetDeclaredMethodsSafe())
        {
            if (HasExtensionMarkerName(GetCustomAttributesSafe(methodInfo)))
            {
                _hasExtensionMarkerMembers = true;
                return true;
            }
        }

        foreach (var propertyInfo in GetDeclaredPropertiesSafe())
        {
            if (HasExtensionMarkerName(GetCustomAttributesSafe(propertyInfo)))
            {
                _hasExtensionMarkerMembers = true;
                return true;
            }
        }

        _hasExtensionMarkerMembers = false;
        return false;
    }

    internal IEnumerable<INamedTypeSymbol> GetNestedTypeMembers()
    {
        if (_nestedTypes is { } nestedTypes)
            return nestedTypes;

        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (var nestedType in GetDeclaredNestedTypesSafe())
        {
            if (_reflectionTypeLoader.ResolveType((Type)nestedType) is INamedTypeSymbol nestedTypeSymbol)
                builder.Add(nestedTypeSymbol);
        }

        _nestedTypes = builder.ToImmutable();
        return _nestedTypes.Value;
    }

    internal ITypeSymbol? GetExtensionMarkerReceiverType(ISymbol member)
    {
        if (member.ContainingType is not PENamedTypeSymbol)
            return null;

        if (!TryGetExtensionMarkerName(member, out var markerName))
            return null;

        if (string.IsNullOrWhiteSpace(markerName))
            return null;

        return GetExtensionMarkerReceiverType(markerName);
    }

    private ITypeSymbol? GetExtensionMarkerReceiverType(string markerName)
    {
        if (string.IsNullOrWhiteSpace(markerName))
            return null;

        var markerType = FindNestedMarkerType(markerName);
        if (markerType is null)
            return null;

        if (markerType is PENamedTypeSymbol peMarkerType &&
            peMarkerType.TryGetMarkerExtensionReceiverType(out var receiverType))
        {
            return receiverType;
        }

        var markerMethod = markerType.GetMembers("<Extension>$").OfType<IMethodSymbol>().FirstOrDefault()
            ?? markerType.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "<Extension>$");

        if (markerMethod is not null && !markerMethod.Parameters.IsDefaultOrEmpty)
            return markerMethod.Parameters[0].Type;

        return null;
    }

    private INamedTypeSymbol? FindNestedMarkerType(string markerName)
    {
        foreach (var nestedTypeInfo in GetDeclaredNestedTypesSafe())
        {
            var nested = PEContainingModule.GetType(nestedTypeInfo.AsType()) as INamedTypeSymbol;
            if (nested is null)
                continue;

            if (nested.Name == markerName)
                return nested;

            if (nested is PENamedTypeSymbol peNested &&
                peNested.FindNestedMarkerType(markerName) is { } nestedMatch)
            {
                return nestedMatch;
            }
        }

        foreach (var member in GetMembers().OfType<INamedTypeSymbol>())
        {
            if (member.Name == markerName)
                return member;

            if (member is PENamedTypeSymbol nested &&
                nested.FindNestedMarkerType(markerName) is { } nestedMatch)
            {
                return nestedMatch;
            }
        }

        return null;
    }

    private ITypeSymbol? GetFirstExtensionMarkerReceiverType()
    {
        foreach (var methodInfo in GetDeclaredMethodsSafe())
        {
            if (TryGetExtensionMarkerName(GetCustomAttributesSafe(methodInfo), out var markerName) &&
                GetExtensionMarkerReceiverType(markerName) is { } receiverType)
            {
                return receiverType;
            }
        }

        foreach (var propertyInfo in GetDeclaredPropertiesSafe())
        {
            if (TryGetExtensionMarkerName(GetCustomAttributesSafe(propertyInfo), out var markerName) &&
                GetExtensionMarkerReceiverType(markerName) is { } receiverType)
            {
                return receiverType;
            }
        }

        return null;
    }

    private bool TryGetMarkerExtensionReceiverType(out ITypeSymbol? receiverType)
    {
        foreach (var methodInfo in GetDeclaredMethodsSafe())
        {
            if (methodInfo.Name != "<Extension>$")
                continue;

            if (TryGetFirstParameterType(methodInfo, out receiverType))
                return true;

            return false;
        }

        receiverType = null;
        return false;
    }

    private bool TryGetFirstParameterType(MethodBase methodInfo, out ITypeSymbol? parameterType)
    {
        parameterType = null;

        try
        {
            var parameters = methodInfo.GetParameters();
            if (parameters.Length == 0)
                return false;

            parameterType = PEContainingModule.GetType(parameters[0].ParameterType);
            return parameterType is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException or System.IO.FileNotFoundException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetExtensionMarkerName(ISymbol member, out string markerName)
    {
        markerName = string.Empty;

        return member switch
        {
            PEMethodSymbol peMethod => peMethod.TryGetExtensionMarkerName(out markerName),
            PEPropertySymbol peProperty => peProperty.TryGetExtensionMarkerName(out markerName),
            _ => false
        };
    }

    public override SymbolKind Kind => SymbolKind.Type;
    public override string Name => _name ??= _typeInfo.IsGenericType ? StripArity(_typeInfo.Name) : _typeInfo.Name;

    public ITypeSymbol? EnumUnderlyingType => TypeKind == TypeKind.Enum
        ? _enumUnderlyingType ??= ResolveEnumUnderlyingType()
        : null;

    private ITypeSymbol? ResolveEnumUnderlyingType()
    {
        var valueField = _typeInfo.DeclaredFields.FirstOrDefault(static field => field.Name == "value__");
        return valueField is null
            ? null
            : _reflectionTypeLoader.ResolveType(valueField.FieldType);
    }

    private static string StripArity(string name)
    {
        var index = name.IndexOf('`');
        return index >= 0 ? name.Substring(0, index) : name;
    }

    public override string MetadataName => _typeInfo.Name;

    public bool IsNamespace { get; } = false;
    public bool IsType { get; } = true;
    public bool IsValueType => _isValueType;
    public bool IsReferenceType => !_isValueType;
    public bool IsInterface => _typeInfo.IsInterface;

    public ImmutableArray<IMethodSymbol> Constructors => GetMembers(".ctor").OfType<IMethodSymbol>().ToImmutableArray();
    public ImmutableArray<IMethodSymbol> InstanceConstructors => Constructors;
    public IMethodSymbol? StaticConstructor { get; }
    public ImmutableArray<ITypeSymbol> TypeArguments { get; } = [];

    public ImmutableArray<ITypeParameterSymbol> TypeParameters =>
        _typeParameters ??= ComputeTypeParameters();

    public int Arity => GetMetadataArity();

    private int GetMetadataArity()
    {
        var name = _typeInfo.Name;
        var index = name.IndexOf('`');
        if (index < 0)
            return 0;

        if (int.TryParse(name.AsSpan(index + 1), out var arity))
            return arity;

        return 0;
    }

    private ImmutableArray<ITypeParameterSymbol> ComputeTypeParameters()
    {
        var declaredArity = GetMetadataArity();

        // Non-generic or no params: trivial
        if (declaredArity == 0)
            return ImmutableArray<ITypeParameterSymbol>.Empty;

        var allParams = _typeInfo.GenericTypeParameters;
        if (allParams.Length == 0)
            return ImmutableArray<ITypeParameterSymbol>.Empty;

        // For nested generic type definitions, reflection gives you:
        //   [outer generic params..., inner generic params...]
        //
        // So: inner *declared* parameters are the LAST `declaredArity` ones.
        //
        // Example:
        //   class Outer<TOuter>
        //   {
        //       class Inner<TInner> { }
        //   }
        //
        // typeof(Outer<,>.Inner<>).GenericTypeParameters:
        //   [TOuter, TInner]
        // Name "Inner`1" -> declaredArity = 1 -> take last 1 -> [TInner]

        int start = Math.Max(0, allParams.Length - declaredArity);
        var slice = allParams
            .AsSpan(start, declaredArity)
            .ToArray();

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(slice.Length);
        foreach (var tp in slice)
        {
            var paramSymbol = _reflectionTypeLoader.ResolveTypeParameter(tp, this);
            builder.Add(paramSymbol);
        }

        return builder.ToImmutable();
    }

    public ITypeSymbol? ConstructedFrom => _constructedFrom;

    private (ITypeSymbol constructedFrom, ITypeSymbol originalDefinition) ResolveGenericOrigins()
    {
        var self = (ITypeSymbol)this;
        var constructedFrom = self;
        var originalDefinition = self;

        if (!_typeInfo.IsGenericType)
            return (constructedFrom, originalDefinition);

        if (_typeInfo.IsGenericTypeDefinition)
            return (constructedFrom, originalDefinition);

        var type = _typeInfo.AsType();
        var definitionType = type.GetGenericTypeDefinition();

        if (ReferenceEquals(definitionType, type))
            return (constructedFrom, originalDefinition);

        var resolved = _reflectionTypeLoader.ResolveType(definitionType);
        if (resolved is INamedTypeSymbol definitionSymbol)
        {
            constructedFrom = definitionSymbol;

            originalDefinition = definitionSymbol.OriginalDefinition ?? definitionSymbol;
        }

        return (constructedFrom, originalDefinition);
    }
    public bool IsAbstract => _typeInfo.IsAbstract;
    public bool IsClosed => _typeInfo.IsSealed;
    public bool IsSealedHierarchy
    {
        get
        {
            EnsureClosedHierarchyMetadata();
            return _isSealedHierarchy.GetValueOrDefault();
        }
    }
    public ImmutableArray<INamedTypeSymbol> PermittedDirectSubtypes
    {
        get
        {
            EnsureClosedHierarchyMetadata();
            return _permittedDirectSubtypes.GetValueOrDefault();
        }
    }
    public bool IsRefLikeType => _typeInfo.IsByRefLike;
    public bool IsReadOnly => _typeInfo.CustomAttributes.Any(attribute =>
        attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");
    public override bool IsStatic => TypeKind == TypeKind.Class && IsAbstract && IsClosed;
    public bool IsGenericType => _typeInfo.IsGenericType;
    public bool IsUnboundGenericType => _typeInfo.IsGenericTypeDefinition;

    public ImmutableArray<INamedTypeSymbol> Interfaces
    {
        get
        {
            return _interfaces ??= ComputeDirectInterfaces();
        }
    }

    public ImmutableArray<INamedTypeSymbol> AllInterfaces =>
        _allInterfaces ??= ComputeAllInterfaces();

    private ImmutableArray<INamedTypeSymbol> ComputeDirectInterfaces()
    {
        var declared = _typeInfo.ImplementedInterfaces
            .Select(i => _reflectionTypeLoader.ResolveType(i))
            .OfType<INamedTypeSymbol>()
            .ToImmutableArray();

        if (declared.IsDefaultOrEmpty)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        return declared;
    }

    private ImmutableArray<INamedTypeSymbol> ComputeAllInterfaces()
    {
        var directInterfaces = Interfaces;
        var baseInterfaces = BaseType?.AllInterfaces ?? ImmutableArray<INamedTypeSymbol>.Empty;

        if (directInterfaces.IsDefaultOrEmpty && baseInterfaces.IsDefaultOrEmpty)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default)
        {
            this
        };

        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        foreach (var interfaceType in directInterfaces)
            AddInterface(interfaceType);

        foreach (var inherited in baseInterfaces)
        {
            if (seen.Add(inherited))
                builder.Add(inherited);
        }

        return builder.ToImmutable();

        void AddInterface(INamedTypeSymbol interfaceType)
        {
            if (!seen.Add(interfaceType))
                return;

            builder.Add(interfaceType);

            var inheritedInterfaces = interfaceType.Interfaces;
            if (inheritedInterfaces.IsDefaultOrEmpty)
                return;

            foreach (var inherited in inheritedInterfaces)
                AddInterface(inherited);
        }
    }

    private static bool IsDelegateType(System.Reflection.TypeInfo typeInfo)
    {
        try
        {
            if (typeInfo.FullName == typeof(MulticastDelegate).FullName ||
                typeInfo.FullName == typeof(Delegate).FullName)
            {
                return true;
            }

            if (typeof(MulticastDelegate).IsAssignableFrom(typeInfo))
                return true;

            var baseType = typeInfo.BaseType;
            while (baseType is not null)
            {
                if (baseType.FullName == typeof(MulticastDelegate).FullName)
                    return true;

                baseType = baseType.BaseType;
            }
        }
        catch (Exception exception) when (IsIncompleteMetadataGraphException(exception))
        {
            // An unresolved optional dependency cannot turn an otherwise unknown
            // metadata type into a delegate.
        }

        return false;
    }

    private static string? TryGetBaseTypeName(System.Reflection.TypeInfo typeInfo)
    {
        try
        {
            return typeInfo.BaseType?.Name;
        }
        catch (Exception exception) when (IsIncompleteMetadataGraphException(exception))
        {
            return null;
        }
    }

    private static bool IsIncompleteMetadataGraphException(Exception exception)
        => exception is ArgumentException or FileNotFoundException or TypeLoadException;

    public SpecialType SpecialType
    {
        get
        {
            var type = _typeInfo.AsType();
            var fullName = type.FullName;
            if (fullName is not null &&
                s_specialTypeByFullName.TryGetValue(fullName, out var specialType))
            {
                return specialType;
            }

            if (type.IsArray)
                return SpecialType.System_Array;

            return SpecialType.None;
        }
    }

    public INamedTypeSymbol? BaseType => _baseType ??= (_typeInfo.BaseType is not null ? (INamedTypeSymbol?)_reflectionTypeLoader.ResolveType(_typeInfo.BaseType) : null);

    public TypeKind TypeKind { get; }

    public ITypeSymbol? OriginalDefinition => _originalDefinition;

    private ImmutableArray<IFieldSymbol>? _tupleElements;

    public INamedTypeSymbol? UnderlyingTupleType => null;

    public ImmutableArray<IFieldSymbol> TupleElements
    {
        get
        {
            if (!IsValueTupleSpecialType())
                return ImmutableArray<IFieldSymbol>.Empty;

            if (_tupleElements is not null)
                return _tupleElements.Value;

            EnsureMembersLoaded();

            _tupleElements = GetMembersSnapshot()
                .OfType<IFieldSymbol>()
                .Where(@field => @field.Name.StartsWith("Item", StringComparison.Ordinal))
                .OrderBy(@field => @field.Name, StringComparer.Ordinal)
                .ToImmutableArray();

            return _tupleElements.Value;
        }
    }

    public ImmutableArray<ISymbol> GetMembers()
    {
        EnsureMembersLoaded();
        return GetMembersSnapshot().ToImmutableArray();
    }

    public ImmutableArray<ISymbol> GetMembers(string name)
    {
        EnsureMembersWithNameLoaded(name);
        return GetMembersSnapshot().Where(x => x.Name == name).ToImmutableArray();
    }

    public bool IsMemberDefined(string name, out ISymbol? symbol)
    {
        EnsureMembersLoaded();
        symbol = GetMembersSnapshot().FirstOrDefault(m => m.Name == name);
        return symbol is not null;
    }

    public ITypeSymbol? LookupType(string name)
    {
        EnsureMembersLoaded();
        return TypeLookupUtilities.SelectBestTypeByName(
            GetMembersSnapshot()
                .OfType<ITypeSymbol>()
                .Where(type => type.Name == name));
    }

    private bool IsValueTupleSpecialType()
    {
        return SpecialType is SpecialType.System_ValueTuple_T1
            or SpecialType.System_ValueTuple_T2
            or SpecialType.System_ValueTuple_T3
            or SpecialType.System_ValueTuple_T4
            or SpecialType.System_ValueTuple_T5
            or SpecialType.System_ValueTuple_T6
            or SpecialType.System_ValueTuple_T7;
    }

    internal void AddMember(ISymbol member)
    {
        lock (_membersGate)
            _members.Add(member);

        /*
        if (!_members.Add(member))
        {
            throw new InvalidOperationException($"Member '{member.ToDisplayString()}' has already been added to type '{this.ToDisplayString()}'");
        }
        */
    }

    private void EnsureMembersLoaded()
    {
        lock (_membersGate)
        {
            if (_membersLoaded)
                return;

            _membersLoaded = true;

            foreach (var methodInfo in _typeInfo.DeclaredMethods)
            {
                if (_memberNamesLoaded.Contains(GetSimpleMetadataMemberName(methodInfo.Name)))
                    continue;

                if (methodInfo.IsSpecialName &&
                    methodInfo.Name != "<Extension>$" &&
                    !methodInfo.Name.StartsWith("op_", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = methodInfo.Name;

                if (name.StartsWith("get_")
                    || name.StartsWith("set_")
                    || name.StartsWith("add_")
                    || name.StartsWith("remove_"))
                    continue;

                new PEMethodSymbol(
                    _reflectionTypeLoader,
                    methodInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);
            }

            foreach (var propertyInfo in _typeInfo.DeclaredProperties)
            {
                if (_memberNamesLoaded.Contains(GetSimpleMetadataMemberName(propertyInfo.Name)))
                    continue;

                var property = new PEPropertySymbol(
                    _reflectionTypeLoader,
                    propertyInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);

                if (propertyInfo.GetMethod is not null)
                {
                    property.GetMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        propertyInfo.GetMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: property);
                }

                if (propertyInfo.SetMethod is not null)
                {
                    property.SetMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        propertyInfo.SetMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: property);
                }
            }

            if (this.HasStaticExtensionMembers)
            {
                var props = ExtensionPropertyReflection.GroupByAccessorConvention(_typeInfo);

                foreach (var prop in props)
                {
                    if (_memberNamesLoaded.Contains(prop.Name))
                        continue;

                    var property = new SynthesizedExtensionPropertySymbol(
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            [], name: prop.Name);

                    if (prop.GetMethod is not null)
                    {
                        property.GetMethod = new PEMethodSymbol(
                            _reflectionTypeLoader,
                            prop.GetMethod,
                            this,
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            associatedSymbol: property);
                    }

                    if (prop.SetMethod is not null)
                    {
                        property.SetMethod = new PEMethodSymbol(
                            _reflectionTypeLoader,
                            prop.SetMethod,
                            this,
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            associatedSymbol: property);
                    }
                }
            }

            foreach (var eventInfo in _typeInfo.DeclaredEvents)
            {
                if (_memberNamesLoaded.Contains(GetSimpleMetadataMemberName(eventInfo.Name)))
                    continue;

                var @event = new PEEventSymbol(
                    _reflectionTypeLoader,
                    eventInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);

                if (eventInfo.AddMethod is not null)
                {
                    @event.AddMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        eventInfo.AddMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: @event);
                }

                if (eventInfo.RemoveMethod is not null)
                {
                    @event.RemoveMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        eventInfo.RemoveMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: @event);
                }
            }

            foreach (var fieldInfo in _typeInfo.DeclaredFields)
            {
                if (_memberNamesLoaded.Contains(GetSimpleMetadataMemberName(fieldInfo.Name)))
                    continue;

                if (fieldInfo.IsSpecialName)
                    continue;

                new PEFieldSymbol(
                    _reflectionTypeLoader,
                    fieldInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);
            }

            foreach (var constructorInfo in _typeInfo.DeclaredConstructors)
            {
                if (_memberNamesLoaded.Contains(constructorInfo.Name))
                    continue;

                new PEMethodSymbol(
                    _reflectionTypeLoader,
                    constructorInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);
            }

            foreach (var nestedTypeInfo in _typeInfo.DeclaredNestedTypes)
            {
                if (_memberNamesLoaded.Contains(StripArity(nestedTypeInfo.Name)))
                    continue;

                // Always intern nested types via the module's Type-based cache to avoid creating duplicate symbols.
                // The module is responsible for placing nested types under the correct containing type.
                _ = PEContainingModule.GetType(nestedTypeInfo.AsType());
            }
        }
    }

    private void EnsureMembersWithNameLoaded(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            EnsureMembersLoaded();
            return;
        }

        lock (_membersGate)
        {
            if (_membersLoaded || !_memberNamesLoaded.Add(name))
                return;

            foreach (var methodInfo in _typeInfo.DeclaredMethods)
            {
                if (!string.Equals(GetSimpleMetadataMemberName(methodInfo.Name), name, StringComparison.Ordinal))
                    continue;

                if (methodInfo.IsSpecialName &&
                    methodInfo.Name != "<Extension>$" &&
                    !methodInfo.Name.StartsWith("op_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.StartsWith("get_")
                    || name.StartsWith("set_")
                    || name.StartsWith("add_")
                    || name.StartsWith("remove_"))
                    continue;

                new PEMethodSymbol(
                    _reflectionTypeLoader,
                    methodInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);
            }

            foreach (var propertyInfo in _typeInfo.DeclaredProperties)
            {
                if (!string.Equals(GetSimpleMetadataMemberName(propertyInfo.Name), name, StringComparison.Ordinal))
                    continue;

                var property = new PEPropertySymbol(
                    _reflectionTypeLoader,
                    propertyInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);

                if (propertyInfo.GetMethod is not null)
                {
                    property.GetMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        propertyInfo.GetMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: property);
                }

                if (propertyInfo.SetMethod is not null)
                {
                    property.SetMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        propertyInfo.SetMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: property);
                }
            }

            if (this.HasStaticExtensionMembers)
            {
                foreach (var prop in ExtensionPropertyReflection.GroupByAccessorConvention(_typeInfo))
                {
                    if (!string.Equals(prop.Name, name, StringComparison.Ordinal))
                        continue;

                    var property = new SynthesizedExtensionPropertySymbol(
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            [], name: prop.Name);

                    if (prop.GetMethod is not null)
                    {
                        property.GetMethod = new PEMethodSymbol(
                            _reflectionTypeLoader,
                            prop.GetMethod,
                            this,
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            associatedSymbol: property);
                    }

                    if (prop.SetMethod is not null)
                    {
                        property.SetMethod = new PEMethodSymbol(
                            _reflectionTypeLoader,
                            prop.SetMethod,
                            this,
                            this,
                            [new MetadataLocation(ContainingModule!)],
                            associatedSymbol: property);
                    }
                }
            }

            foreach (var eventInfo in _typeInfo.DeclaredEvents)
            {
                if (!string.Equals(GetSimpleMetadataMemberName(eventInfo.Name), name, StringComparison.Ordinal))
                    continue;

                var @event = new PEEventSymbol(
                    _reflectionTypeLoader,
                    eventInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);

                if (eventInfo.AddMethod is not null)
                {
                    @event.AddMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        eventInfo.AddMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: @event);
                }

                if (eventInfo.RemoveMethod is not null)
                {
                    @event.RemoveMethod = new PEMethodSymbol(
                        _reflectionTypeLoader,
                        eventInfo.RemoveMethod,
                        this,
                        this,
                        [new MetadataLocation(ContainingModule!)],
                        associatedSymbol: @event);
                }
            }

            foreach (var fieldInfo in _typeInfo.DeclaredFields)
            {
                if (fieldInfo.IsSpecialName ||
                    !string.Equals(GetSimpleMetadataMemberName(fieldInfo.Name), name, StringComparison.Ordinal))
                    continue;

                new PEFieldSymbol(
                    _reflectionTypeLoader,
                    fieldInfo,
                    this,
                    [new MetadataLocation(ContainingModule!)]);
            }

            if (name is ".ctor" or ".cctor")
            {
                foreach (var constructorInfo in _typeInfo.DeclaredConstructors)
                {
                    if (!string.Equals(constructorInfo.Name, name, StringComparison.Ordinal))
                        continue;

                    new PEMethodSymbol(
                        _reflectionTypeLoader,
                        constructorInfo,
                        this,
                        [new MetadataLocation(ContainingModule!)]);
                }
            }

            foreach (var nestedTypeInfo in _typeInfo.DeclaredNestedTypes)
            {
                if (!string.Equals(StripArity(nestedTypeInfo.Name), name, StringComparison.Ordinal))
                    continue;

                _ = PEContainingModule.GetType(nestedTypeInfo.AsType());
            }
        }
    }

    private IEnumerable<MethodInfo> GetDeclaredMethodsSafe()
    {
        try
        {
            return _typeInfo.DeclaredMethods;
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private IEnumerable<PropertyInfo> GetDeclaredPropertiesSafe()
    {
        try
        {
            return _typeInfo.DeclaredProperties;
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private IEnumerable<System.Reflection.TypeInfo> GetDeclaredNestedTypesSafe()
    {
        try
        {
            return _typeInfo.DeclaredNestedTypes;
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<CustomAttributeData> GetCustomAttributesSafe(MethodInfo methodInfo)
    {
        IList<CustomAttributeData> attributes;
        try
        {
            attributes = methodInfo.GetCustomAttributesData();
        }
        catch (ArgumentException)
        {
            return [];
        }

        return attributes;
    }

    private static IEnumerable<CustomAttributeData> GetCustomAttributesSafe(PropertyInfo propertyInfo)
    {
        IList<CustomAttributeData> attributes;
        try
        {
            attributes = propertyInfo.GetCustomAttributesData();
        }
        catch (ArgumentException)
        {
            return [];
        }

        return attributes;
    }

    private static string GetSimpleMetadataMemberName(string metadataName)
    {
        if (metadataName is ".ctor" or ".cctor")
            return metadataName;

        var separator = metadataName.LastIndexOf('.');
        return separator >= 0 ? metadataName[(separator + 1)..] : metadataName;
    }

    private static bool HasExtensionAttribute(IEnumerable<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (GetAttributeTypeName(attribute) == typeof(System.Runtime.CompilerServices.ExtensionAttribute).FullName)
                return true;
        }

        return false;
    }

    private static bool HasExtensionMarkerName(IEnumerable<CustomAttributeData> attributes)
    {
        return TryGetExtensionMarkerName(attributes, out _);
    }

    private static bool TryGetExtensionMarkerName(IEnumerable<CustomAttributeData> attributes, out string markerName)
    {
        markerName = string.Empty;

        foreach (var attribute in attributes)
        {
            if (GetAttributeTypeName(attribute) != "System.Runtime.CompilerServices.ExtensionMarkerNameAttribute")
                continue;

            if (attribute.ConstructorArguments is [{ Value: string name }])
                markerName = name;

            return true;
        }

        return false;
    }

    private List<ISymbol> GetMembersSnapshot()
    {
        lock (_membersGate)
        {
            return _members.ToList();
        }
    }

    public System.Reflection.TypeInfo GetTypeInfo() => _typeInfo;

    public ITypeSymbol Construct(params ITypeSymbol[] typeArguments)
    {
        if (typeArguments.Length != Arity)
            throw new ArgumentException($"Type '{Name}' expects {Arity} type arguments, but got {typeArguments.Length}.");

        return new ConstructedNamedTypeSymbol(this, typeArguments.ToImmutableArray());
    }

    public override void Complete()
    {
        IsCompleted = true;
    }

    private static string BuildMetadataName(Type type)
    {
        if (type.DeclaringType is { } declaringType)
            return $"{BuildMetadataName(declaringType)}+{type.Name}";

        return type.FullName ?? type.Name;
    }
}
