using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Raven.CodeAnalysis.Symbols;

internal partial class PEMethodSymbol : PESymbol, IMethodSymbol
{
    private static readonly ConditionalWeakTable<MethodBase, ParameterInfo[]> s_parameterInfoCache = new();
    private static readonly ConcurrentDictionary<MetadataMethodKey, int> s_parameterCountCache = new();

    private readonly ReflectionTypeLoader _reflectionTypeLoader;
    private readonly MethodBase _methodInfo;
    private ITypeSymbol? _returnType;
    private ImmutableArray<IParameterSymbol>? _parameters;
    private ImmutableArray<ITypeParameterSymbol>? _typeParameters;
    private ImmutableArray<ITypeSymbol>? _typeArguments;
    private Accessibility? _accessibility;
    private ImmutableArray<IMethodSymbol>? _explicitInterfaceImplementations;
    private ImmutableArray<AttributeData>? _attributes;
    private ImmutableArray<AttributeData>? _returnTypeAttributes;
    private int? _parameterCount;

    public PEMethodSymbol(ReflectionTypeLoader reflectionTypeLoader, MethodBase methodInfo, INamedTypeSymbol containingType, Location[] locations, ISymbol? associatedSymbol = null, bool addAsMember = true)
        : base(containingType, containingType, containingType.ContainingNamespace, locations, addAsMember: addAsMember)
    {
        _reflectionTypeLoader = reflectionTypeLoader;
        _methodInfo = methodInfo;
        _reflectionTypeLoader.RegisterMethodSymbol(_methodInfo, this);

        var name = _methodInfo.Name;

        if (name.StartsWith("op_Implicit") || name.StartsWith("op_Explicit"))
        {
            MethodKind = MethodKind.Conversion;
        }
        else if (name.StartsWith("op_"))
        {
            MethodKind = MethodKind.UserDefinedOperator;
        }
        else if (name.StartsWith(".ctor"))
        {
            MethodKind = MethodKind.Constructor;
        }
        else if (name.StartsWith(".cctor"))
        {
            MethodKind = MethodKind.StaticConstructor;
        }
        else if (name.StartsWith("get_"))
        {
            MethodKind = MethodKind.PropertyGet;
        }
        else if (name.StartsWith("set_"))
        {
            MethodKind = MethodKind.PropertySet;
        }
        else if (name.StartsWith("add_"))
        {
            MethodKind = MethodKind.EventAdd;
        }
        else if (name.StartsWith("remove_"))
        {
            MethodKind = MethodKind.EventRemove;
        }
        else if (name.Contains('.'))
        {
            MethodKind = MethodKind.ExplicitInterfaceImplementation;
        }
        else
        {
            MethodKind = MethodKind.Ordinary;
        }

        AssociatedSymbol = associatedSymbol;
    }

    public PEMethodSymbol(ReflectionTypeLoader reflectionTypeLoader, MethodBase methodBaseInfo, ISymbol containingSymbol, INamedTypeSymbol? containingType, Location[] locations, ISymbol? associatedSymbol = null, bool addAsMember = true)
    : base(containingSymbol, containingType, containingType?.ContainingNamespace, locations, addAsMember: addAsMember)
    {
        _reflectionTypeLoader = reflectionTypeLoader;
        _methodInfo = methodBaseInfo;
        _reflectionTypeLoader.RegisterMethodSymbol(_methodInfo, this);

        var name = _methodInfo.Name;

        if (name.StartsWith("op_Implicit") || name.StartsWith("op_Explicit"))
        {
            MethodKind = MethodKind.Conversion;
        }
        else if (name.StartsWith("op_"))
        {
            MethodKind = MethodKind.UserDefinedOperator;
        }
        else if (name.StartsWith(".ctor"))
        {
            MethodKind = MethodKind.Constructor;
        }
        else if (name.StartsWith(".cctor"))
        {
            MethodKind = MethodKind.StaticConstructor;
        }
        else if (name.StartsWith("get_"))
        {
            MethodKind = MethodKind.PropertyGet;
        }
        else if (name.StartsWith("set_"))
        {
            if (methodBaseInfo is MethodInfo methodInfo)
            {
                if (methodInfo.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.Name == typeof(IsExternalInit).Name))
                {
                    MethodKind = MethodKind.InitOnly;
                }
            }
            MethodKind = MethodKind.PropertySet;
        }
        else if (name.StartsWith("add_"))
        {
            MethodKind = MethodKind.EventAdd;
        }
        else if (name.StartsWith("remove_"))
        {
            MethodKind = MethodKind.EventRemove;
        }
        else if (name.Contains('.'))
        {
            MethodKind = MethodKind.ExplicitInterfaceImplementation;
        }
        else
        {
            MethodKind = MethodKind.Ordinary;
        }

        AssociatedSymbol = associatedSymbol;
    }

    public override SymbolKind Kind => SymbolKind.Method;
    internal MethodBase ReflectionMethodBase => _methodInfo;

    public override string Name
    {
        get
        {
            if (_name is not null)
            {
                return _name;
            }
            if (!_methodInfo.Name.Contains(".ctor")
                && !_methodInfo.Name.Contains(".cctor")
                && _methodInfo.Name.Contains('.'))
            {
                _name = _methodInfo.Name.Split('.').Last();
            }
            else
            {
                _name = _methodInfo.Name;
            }
            return _name;
        }
    }

    public override string MetadataName => _methodInfo.Name;
    public ITypeSymbol ReturnType
    {
        get
        {
            if (_returnType == null)
            {
                if (_methodInfo is MethodInfo methodInfo)
                {
                    _returnType = _reflectionTypeLoader.ResolveType(methodInfo.ReturnParameter)
                        ?? _reflectionTypeLoader.Compilation.ErrorTypeSymbol;
                    if (_returnType.SpecialType == SpecialType.System_Void)
                        _returnType = _reflectionTypeLoader.Compilation.GetSpecialType(SpecialType.System_Unit);
                }
                else if (MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                {
                    _returnType = _reflectionTypeLoader.ResolveType(typeof(void))
                        ?? _reflectionTypeLoader.Compilation.ErrorTypeSymbol;
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported method base type '{_methodInfo.GetType()}'.");
                }
            }
            return _returnType;
        }
    }

    public ImmutableArray<IParameterSymbol> Parameters
    {
        get
        {
            if (_parameters is not null)
                return _parameters.Value;

            try
            {
                if (!TryGetParameterInfos(out var parameterInfos))
                    throw new InvalidOperationException($"Unable to read parameters for '{_methodInfo}'.");

                _parameterCount = parameterInfos.Length;
                CacheParameterCount(_parameterCount.Value);
                _parameters = parameterInfos.Select(param =>
                {
                    return new PEParameterSymbol(
                          _reflectionTypeLoader,
                          param, this, this.ContainingType, this.ContainingNamespace,
                          [new MetadataLocation(ContainingModule!)]);
                }).OfType<IParameterSymbol>().ToImmutableArray();
            }
            catch
            {
                // Some metadata-only references carry optional/transitive dependencies that
                // are intentionally absent at runtime (for example Microsoft.Build on
                // Raven.CodeAnalysis). Mark unreadable signatures so callers can
                // treat the member as invalid instead of silently treating it as parameterless.
                _hasUnreadableSignature = true;
                _parameters =
                [
                    new SourceParameterSymbol(
                        "__unreadable",
                        _reflectionTypeLoader.Compilation.ErrorTypeSymbol,
                        this,
                        this.ContainingType,
                        this.ContainingNamespace,
                        [new MetadataLocation(ContainingModule!)],
                        [])
                ];
            }

            return _parameters.Value;
        }
    }

    internal int ParameterCount
    {
        get
        {
            if (_parameterCount is { } count)
                return count;

            try
            {
                if (TryGetCachedParameterCount(out var cachedCount))
                {
                    _parameterCount = cachedCount;
                }
                else
                {
                    _parameterCount = TryGetParameterInfos(out var parameterInfos)
                        ? parameterInfos.Length
                        : 1;
                    CacheParameterCount(_parameterCount.Value);
                }
            }
            catch
            {
                _parameterCount = 1;
            }

            return _parameterCount.Value;
        }
    }

    internal bool TryGetCachedParameterCount(out int count)
    {
        if (TryCreateMetadataMethodKey(out var key) &&
            s_parameterCountCache.TryGetValue(key, out count))
        {
            return true;
        }

        count = 0;
        return false;
    }

    internal bool TryGetParameterType(int index, out ITypeSymbol? type)
    {
        type = null;

        if (index < 0)
            return false;

        try
        {
            if (_parameters is { } parameters)
            {
                if (index >= parameters.Length)
                    return false;

                type = parameters[index].Type;
                return type is not null;
            }

            if (!TryGetParameterInfos(out var parameterInfos) ||
                index >= parameterInfos.Length)
            {
                return false;
            }

            var parameterInfo = parameterInfos[index];
            var resolved = _reflectionTypeLoader.ResolveType(parameterInfo);
            if (parameterInfo.ParameterType.IsByRef && resolved is RefTypeSymbol refType)
                resolved = refType.ElementType;

            type = resolved;
            return type is not null;
        }
        catch
        {
            _hasUnreadableSignature = true;
            type = _reflectionTypeLoader.Compilation.ErrorTypeSymbol;
            return false;
        }
    }

    internal bool TryGetParameterRuntimeTypeMetadataName(int index, out string? metadataName)
    {
        metadataName = null;

        if (index < 0)
            return false;

        try
        {
            if (!TryGetParameterInfos(out var parameterInfos) ||
                index >= parameterInfos.Length)
            {
                return false;
            }

            var parameterType = parameterInfos[index].ParameterType;
            if (parameterType.IsByRef)
                parameterType = parameterType.GetElementType() ?? parameterType;

            metadataName = parameterType.FullName;
            return !string.IsNullOrWhiteSpace(metadataName);
        }
        catch
        {
            _hasUnreadableSignature = true;
            metadataName = null;
            return false;
        }
    }

    internal bool TryGetParameterUsage(int index, out bool hasExplicitDefaultValue, out bool isParams)
    {
        hasExplicitDefaultValue = false;
        isParams = false;

        if (index < 0)
            return false;

        try
        {
            if (!TryGetParameterInfos(out var parameterInfos) ||
                index >= parameterInfos.Length)
            {
                return false;
            }

            var parameterInfo = parameterInfos[index];
            hasExplicitDefaultValue = parameterInfo.IsOptional || parameterInfo.HasDefaultValue;
            isParams = parameterInfo
                .GetCustomAttributesData()
                .Any(static attribute => string.Equals(
                    attribute.AttributeType.FullName,
                    typeof(ParamArrayAttribute).FullName,
                    StringComparison.Ordinal));
            return true;
        }
        catch
        {
            _hasUnreadableSignature = true;
            return false;
        }
    }

    private void CacheParameterCount(int count)
    {
        if (TryCreateMetadataMethodKey(out var key))
            s_parameterCountCache.TryAdd(key, count);
    }

    private bool TryCreateMetadataMethodKey(out MetadataMethodKey key)
    {
        try
        {
            key = new MetadataMethodKey(_methodInfo.Module.ModuleVersionId, _methodInfo.MetadataToken);
            return true;
        }
        catch
        {
            key = default;
            return false;
        }
    }

    internal bool TryGetMetadataIdentity(out Guid moduleVersionId, out int metadataToken)
    {
        if (TryCreateMetadataMethodKey(out var key))
        {
            moduleVersionId = key.ModuleVersionId;
            metadataToken = key.MetadataToken;
            return true;
        }

        moduleVersionId = default;
        metadataToken = 0;
        return false;
    }

    private bool TryGetParameterInfos(out ParameterInfo[] parameterInfos)
    {
        try
        {
            parameterInfos = s_parameterInfoCache.GetValue(_methodInfo, static method => method.GetParameters());
            CacheParameterCount(parameterInfos.Length);
            return true;
        }
        catch
        {
            parameterInfos = [];
            return false;
        }
    }

    private readonly record struct MetadataMethodKey(Guid ModuleVersionId, int MetadataToken);

    public ImmutableArray<AttributeData> GetReturnTypeAttributes()
    {
        if (_returnTypeAttributes.HasValue)
            return _returnTypeAttributes.Value;

        if (_methodInfo is not MethodInfo methodInfo)
        {
            _returnTypeAttributes = ImmutableArray<AttributeData>.Empty;
            return _returnTypeAttributes.Value;
        }

        try
        {
            _returnTypeAttributes = ImmutableArray.CreateRange(
                methodInfo.ReturnParameter.GetCustomAttributesData()
                    .Select(attribute => PEAttributeDataFactory.Create(_reflectionTypeLoader, attribute))
                    .OfType<AttributeData>());
        }
        catch
        {
            _returnTypeAttributes = ImmutableArray<AttributeData>.Empty;
        }

        return _returnTypeAttributes.Value;
    }

    public override ImmutableArray<AttributeData> GetAttributes()
    {
        if (_attributes.HasValue)
            return _attributes.Value;

        try
        {
            var builder = ImmutableArray.CreateBuilder<AttributeData>();

            foreach (var attribute in _methodInfo.GetCustomAttributesData())
            {
                var data = PEAttributeDataFactory.Create(_reflectionTypeLoader, attribute);
                if (data is not null)
                    builder.Add(data);
            }

            _attributes = builder.ToImmutable();
        }
        catch
        {
            _attributes = ImmutableArray<AttributeData>.Empty;
        }

        return _attributes.Value;
    }

    public override Accessibility DeclaredAccessibility => _accessibility ??= MapAccessibility(_methodInfo);

    public override bool IsStatic => _methodInfo.IsStatic;

    public bool IsConstructor => _methodInfo.IsConstructor;

    public MethodKind MethodKind { get; }

    public IMethodSymbol? OriginalDefinition => this;

    public ISymbol? AssociatedSymbol { get; }

    public bool IsAbstract => _methodInfo.IsAbstract;

    public bool IsAsync =>
        (_methodInfo as MethodInfo)?.ReturnType == typeof(Task) ||
        (_methodInfo as MethodInfo)?.ReturnType.IsGenericType == true &&
        (_methodInfo as MethodInfo)?.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);

    public bool IsCheckedBuiltin => false; // No metadata indicator; default to false or customize

    public bool IsDefinition => true; // Metadata methods are always definitions

    private bool? _lazyIsExtensionMethod;
    private string? _name;
    private string? _extensionMarkerName;
    private bool? _setsRequiredMembers;
    private bool _hasUnreadableSignature;

    public bool IsExtensionMethod => _lazyIsExtensionMethod ??= ComputeIsExtensionMethod();

    internal bool TryGetExtensionMarkerName(out string markerName)
    {
        markerName = _extensionMarkerName ?? string.Empty;

        if (_extensionMarkerName is not null)
            return markerName.Length > 0;

        if (_methodInfo is null)
            return false;

        try
        {
            foreach (var attribute in _methodInfo.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != "System.Runtime.CompilerServices.ExtensionMarkerNameAttribute")
                    continue;

                if (attribute.ConstructorArguments is [{ Value: string name }])
                {
                    _extensionMarkerName = name;
                    markerName = name;
                    return true;
                }
            }
        }
        catch (Exception)
        {
            _extensionMarkerName = string.Empty;
            return false;
        }

        _extensionMarkerName = string.Empty;
        return false;
    }

    private bool ComputeIsExtensionMethod()
    {
        if (_methodInfo is null || !_methodInfo.IsStatic)
            return false;

        if (_hasUnreadableSignature)
            return false;

        try
        {
            var hasExtensionAttribute = HasExtensionAttribute(_methodInfo.GetCustomAttributesData());
            if (hasExtensionAttribute)
                return true;

            if (TryGetExtensionMarkerName(out _))
                return false;

            var declaringType = _methodInfo.DeclaringType;
            if (!hasExtensionAttribute && declaringType is not null)
                hasExtensionAttribute = HasExtensionAttribute(declaringType.GetCustomAttributesData());

            return hasExtensionAttribute;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasExtensionAttribute(IList<CustomAttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName == typeof(ExtensionAttribute).FullName)
                return true;
        }

        return false;
    }

    public bool IsExtern => _methodInfo.IsAbstract || (_methodInfo.Attributes & MethodAttributes.PinvokeImpl) != 0;

    public bool IsUnsafe => false;

    public bool IsGenericMethod => _methodInfo.IsGenericMethod;

    public bool IsOverride
    {
        get
        {
            try
            {
                return (_methodInfo as MethodInfo)?.GetBaseDefinition()?.DeclaringType != _methodInfo.DeclaringType;
            }
            catch { return false; }
        }
    }

    public bool IsReadOnly =>
        (_methodInfo as MethodInfo)?.ReturnParameter?.GetRequiredCustomModifiers()
            .Contains(typeof(IsReadOnlyAttribute)) == true;

    public bool IsFinal =>
        (_methodInfo as MethodInfo)?.IsFinal == true &&
        (_methodInfo as MethodInfo)?.IsVirtual == true;

    public bool IsVirtual => _methodInfo.IsVirtual;

    public bool IsIterator => false;

    public IteratorMethodKind IteratorKind => IteratorMethodKind.None;

    public ITypeSymbol? IteratorElementType => null;

    public ImmutableArray<IMethodSymbol> ExplicitInterfaceImplementations
    {
        get
        {
            if (_explicitInterfaceImplementations.HasValue)
                return _explicitInterfaceImplementations.Value;

            // Fast-path: most methods are not explicit impls
            if (!_methodInfo.Name.Contains('.'))
            {
                _explicitInterfaceImplementations = ImmutableArray<IMethodSymbol>.Empty;
                return _explicitInterfaceImplementations.Value;
            }

            var declaringType = _methodInfo.DeclaringType;
            if (declaringType is null)
            {
                _explicitInterfaceImplementations = ImmutableArray<IMethodSymbol>.Empty;
                return _explicitInterfaceImplementations.Value;
            }

            // Metadata name is something like "Namespace.IFoo`1.Bar"
            var metadataName = _methodInfo.Name;
            var lastDot = metadataName.LastIndexOf('.');
            if (lastDot <= 0)
            {
                _explicitInterfaceImplementations = ImmutableArray<IMethodSymbol>.Empty;
                return _explicitInterfaceImplementations.Value;
            }

            var ifaceMetadataName = metadataName.Substring(0, lastDot);     // "Namespace.IFoo`1"
            var memberName = metadataName.Substring(lastDot + 1);           // "Bar" or "get_Bar"

            var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();

            foreach (var iface in declaringType.GetInterfaces())
            {
                // Compare to generic *definition* name, because the metadata prefix uses that
                var candidateName = GetFormattedTypeName(iface);
                if (!string.Equals(candidateName, ifaceMetadataName, StringComparison.Ordinal))
                    continue;

                // We have the right interface. Now find the matching method on that interface.
                foreach (var ifaceMethod in iface.GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!string.Equals(ifaceMethod.Name, memberName, StringComparison.Ordinal))
                        continue;

                    if (!HasSameSignature((MethodInfo)_methodInfo, ifaceMethod))
                        continue;

                    // Map interface MethodInfo -> IMethodSymbol via ReflectionTypeLoader
                    var ifaceMethodSymbol = _reflectionTypeLoader.ResolveMethodSymbol(ifaceMethod);
                    if (ifaceMethodSymbol is not null)
                        builder.Add(ifaceMethodSymbol);
                }
            }

            _explicitInterfaceImplementations = builder.ToImmutable();
            return _explicitInterfaceImplementations.Value;
        }
    }

    private static string GetFormattedTypeName(Type t)
    {
        // For generic constructed interfaces, we want the definition name
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            t = t.GetGenericTypeDefinition();

        // Metadata-style full name (includes `1, + for nested, etc.)
        var name = t.FullName ?? t.Name;
        var i = name.LastIndexOf('`');
        if (i > -1)
        {
            name = name[..i];
            var param = t.GetGenericArguments().Select(x => x.Name);
            name = $"{name}<{string.Join(",", param)}>";
        }

        return name;

        /*
        // For generic constructed interfaces, we want the definition name
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
            t = t.GetGenericTypeDefinition();

        // Metadata-style full name (includes `1, + for nested, etc.)
        return t.FullName ?? t.Name;
        */
    }

    private static bool HasSameSignature(MethodInfo impl, MethodInfo iface)
    {
        // Generic arity
        if (impl.IsGenericMethod != iface.IsGenericMethod)
            return false;

        if (impl.IsGenericMethod &&
            impl.GetGenericArguments().Length != iface.GetGenericArguments().Length)
            return false;

        var implParams = impl.GetParameters();
        var ifaceParams = iface.GetParameters();

        if (implParams.Length != ifaceParams.Length)
            return false;

        for (int i = 0; i < implParams.Length; i++)
        {
            var pImpl = implParams[i].ParameterType;
            var pIface = ifaceParams[i].ParameterType;

            if (!TypesEqual(pImpl, pIface))
                return false;
        }

        return TypesEqual(impl.ReturnType, iface.ReturnType);
    }

    private static bool TypesEqual(Type a, Type b)
    {
        return PEReflectionTypeIdentity.AreEquivalent(a, b);
    }

    public ImmutableArray<ITypeParameterSymbol> TypeParameters =>
        _typeParameters ??= !_methodInfo.IsGenericMethodDefinition
            ? ImmutableArray<ITypeParameterSymbol>.Empty
            : ((MethodInfo)_methodInfo).GetGenericArguments()
                .Select(t => (ITypeParameterSymbol)_reflectionTypeLoader.ResolveMethodTypeParameter(t, this)!)
                .ToImmutableArray();

    public ImmutableArray<ITypeSymbol> TypeArguments =>
        _typeArguments ??= TypeParameters.IsDefaultOrEmpty
            ? ImmutableArray<ITypeSymbol>.Empty
            : TypeParameters.Select(static tp => (ITypeSymbol)tp).ToImmutableArray();

    public IMethodSymbol? ConstructedFrom => this;

    public IMethodSymbol Construct(params ITypeSymbol[] typeArguments)
    {
        if (typeArguments is null)
            throw new ArgumentNullException(nameof(typeArguments));

        return new ConstructedMethodSymbol(this, typeArguments.ToImmutableArray());
    }

    public bool SetsRequiredMembers
    {
        get
        {
            return _setsRequiredMembers ??= _methodInfo.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName != "System.Runtime.CompilerServices.SetsRequiredMembersAttribute");
        }
    }

    internal MethodBase GetMethodBase() => _methodInfo;
}
