using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

using Raven.CodeAnalysis;

namespace Raven.CodeAnalysis.Symbols;

internal sealed class PEUnionCompanionSymbol : PENamedTypeSymbol
{
    private const string RavenUnionCompanionAttributeMetadataName = "Raven.Runtime.CompilerServices.RavenUnionCompanionAttribute";
    private IUnionSymbol? _associatedUnion;
    private bool _associationResolving;

    public PEUnionCompanionSymbol(
        ReflectionTypeLoader reflectionTypeLoader,
        System.Reflection.TypeInfo typeInfo,
        ISymbol containingSymbol,
        INamespaceSymbol? containingNamespace,
        Location[] locations)
        : base(reflectionTypeLoader, typeInfo, containingSymbol, containingType: null, containingNamespace, locations, addAsMember: false)
    {
    }

    internal bool TryGetAssociatedUnion(out IUnionSymbol union)
    {
        if (_associatedUnion is null && !_associationResolving)
        {
            _associationResolving = true;
            try
            {
                _associatedUnion = ResolveAssociatedUnion();
            }
            finally
            {
                _associationResolving = false;
            }
        }

        union = _associatedUnion!;
        return union is not null;
    }

    private IUnionSymbol? ResolveAssociatedUnion()
    {
        foreach (var attribute in GetCustomAttributesSafe(_typeInfo))
        {
            if (GetAttributeTypeName(attribute) != RavenUnionCompanionAttributeMetadataName)
                continue;

            try
            {
                if (attribute.ConstructorArguments is not [{ Value: string unionMetadataName }])
                    continue;

                if (ContainingAssembly?.GetTypeByMetadataName(unionMetadataName) is IUnionSymbol assemblyUnion)
                {
                    return assemblyUnion;
                }
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

        return null;
    }
}

internal sealed class PEUnionSymbol : PENamedTypeSymbol, IUnionSymbol
{
    private const string RavenUnionCaseAttributeMetadataName = "Raven.Runtime.CompilerServices.RavenUnionCaseAttribute";

    private ImmutableArray<IUnionCaseTypeSymbol>? _cases;
    private ImmutableArray<ITypeSymbol>? _variants;
    private ImmutableArray<ITypeSymbol>? _memberTypes;
    private bool? _contentMayBeNull;
    private IFieldSymbol? _discriminatorField;
    private IFieldSymbol? _payloadField;

    public PEUnionSymbol(
        ReflectionTypeLoader reflectionTypeLoader,
        System.Reflection.TypeInfo typeInfo,
        ISymbol containingSymbol,
        INamedTypeSymbol? containingType,
        INamespaceSymbol? containingNamespace,
        Location[] locations)
        : base(reflectionTypeLoader, typeInfo, containingSymbol, containingType, containingNamespace, locations, addAsMember: false)
    {
    }

    public ImmutableArray<ITypeSymbol> Variants
    {
        get
        {
            if (_variants is not null)
                return _variants.Value;

            var declaredCases = DeclaredCaseTypes;
            _variants = declaredCases.IsDefaultOrEmpty
                ? MemberTypes
                : declaredCases.Cast<ITypeSymbol>().ToImmutableArray();
            return _variants.Value;
        }
    }

    public ImmutableArray<IUnionCaseTypeSymbol> DeclaredCaseTypes
    {
        get
        {
            if (_cases is not null)
                return _cases.Value;

            if (UnionFacts.GetMemberProvider(this) is not null)
                return (_cases = ImmutableArray<IUnionCaseTypeSymbol>.Empty).Value;

            var cases = GetDeclaredCaseTypesFromRavenMetadata();

            if (cases.IsDefaultOrEmpty)
            {
                cases = GetMembers()
                    .OfType<IUnionCaseTypeSymbol>()
                    .ToImmutableArray();
            }

            if (cases.IsDefaultOrEmpty && ContainingNamespace is not null)
            {
                cases = ContainingNamespace
                    .GetAllMembersRecursive()
                    .OfType<IUnionCaseTypeSymbol>()
                    .Where(caseSymbol => SymbolEqualityComparer.Default.Equals(caseSymbol.Union, this))
                    .Distinct(SymbolEqualityComparer.Default)
                    .OfType<IUnionCaseTypeSymbol>()
                    .ToImmutableArray();
            }

            _cases = cases;
            return _cases.Value;
        }
    }

    internal bool TryGetDeclaredCaseType(string logicalCaseName, out IUnionCaseTypeSymbol caseType)
    {
        caseType = DeclaredCaseTypes.FirstOrDefault(@case =>
            string.Equals(@case.Name, logicalCaseName, StringComparison.Ordinal))!;
        return caseType is not null;
    }

    private ImmutableArray<IUnionCaseTypeSymbol> GetDeclaredCaseTypesFromRavenMetadata()
    {
        var builder = ImmutableArray.CreateBuilder<(int Ordinal, IUnionCaseTypeSymbol CaseType)>();

        foreach (var attribute in GetCustomAttributesSafe(_typeInfo))
        {
            if (GetAttributeTypeName(attribute) != RavenUnionCaseAttributeMetadataName)
                continue;

            if (!TryReadRavenUnionCaseAttribute(attribute, out var caseTypeMetadataName, out _, out var ordinal))
                continue;

            if (TryResolveRavenUnionCaseType(caseTypeMetadataName, out var caseType))
                builder.Add((ordinal, caseType));
        }

        if (builder.Count == 0)
            return ImmutableArray<IUnionCaseTypeSymbol>.Empty;

        return builder
            .OrderBy(static item => item.Ordinal)
            .Select(static item => item.CaseType)
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<IUnionCaseTypeSymbol>()
            .ToImmutableArray();
    }

    private bool TryResolveRavenUnionCaseType(string caseTypeMetadataName, out IUnionCaseTypeSymbol caseType)
    {
        return TryResolveCaseTypeByMetadataName(caseTypeMetadataName, out caseType);
    }

    private static bool TryReadRavenUnionCaseAttribute(
        CustomAttributeData attribute,
        out string caseTypeMetadataName,
        out string logicalCaseName,
        out int ordinal)
    {
        caseTypeMetadataName = string.Empty;
        logicalCaseName = string.Empty;
        ordinal = 0;

        try
        {
            if (attribute.ConstructorArguments is not
                [
                { Value: string caseTypeMetadataNameValue },
                { Value: string nameValue },
                { Value: int ordinalValue }
                ])
            {
                return false;
            }

            caseTypeMetadataName = caseTypeMetadataNameValue;
            logicalCaseName = nameValue;
            ordinal = ordinalValue;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryResolveCaseTypeByMetadataName(string metadataName, out IUnionCaseTypeSymbol caseType)
    {
        var namedType = ContainingAssembly?.GetTypeByMetadataName(metadataName) as INamedTypeSymbol;
        return TryAsUnionCaseType(namedType, out caseType);
    }

    private bool TryAsUnionCaseType(INamedTypeSymbol? namedType, out IUnionCaseTypeSymbol caseType)
    {
        if (namedType is IUnionCaseTypeSymbol existingCaseType)
        {
            caseType = existingCaseType;
            return true;
        }

        if (namedType is PENamedTypeSymbol peType)
        {
            caseType = new PEUnionCaseSymbol(
                _reflectionTypeLoader,
                peType.GetTypeInfo(),
                this,
                peType.ContainingType ?? this,
                peType.ContainingNamespace,
                peType.Locations.ToArray(),
                this);
            return true;
        }

        caseType = null!;
        return false;
    }

    public IFieldSymbol DiscriminatorField =>
        _discriminatorField ??= FindUnionField(UnionFieldUtilities.IsTagFieldName)
            ?? throw new InvalidOperationException($"Missing discriminator field on discriminated union '{Name}'.");

    public ImmutableArray<ITypeSymbol> MemberTypes
    {
        get
        {
            if (_memberTypes is not null)
                return _memberTypes.Value;

            var declaredCases = DeclaredCaseTypes;
            _memberTypes = declaredCases.IsDefaultOrEmpty
                ? GetRuntimeUnionMemberTypes()
                : declaredCases.Cast<ITypeSymbol>().ToImmutableArray();
            return _memberTypes.Value;
        }
    }

    public bool ContentMayBeNull => _contentMayBeNull ??= ComputeContentMayBeNull();

    public IFieldSymbol PayloadField =>
        _payloadField ??= FindUnionField(UnionFieldUtilities.IsPayloadFieldName)
            ?? throw new InvalidOperationException($"Missing payload field on discriminated union '{Name}'.");

    private IFieldSymbol? FindUnionField(Func<string, bool> predicate)
    {
        var fields = GetMembers()
            .OfType<IFieldSymbol>();

        foreach (var field in fields)
        {
            if (predicate(field.Name))
                return field;
        }

        return null;
    }

    private ImmutableArray<ITypeSymbol> GetRuntimeUnionMemberTypes()
    {
        var constructorTypes = ImmutableArray.CreateBuilder<ITypeSymbol>();

        if (UnionFacts.GetMemberProvider(this) is not null)
        {
            foreach (var factory in UnionFacts.GetProviderFactories(this))
                AddMemberType(constructorTypes, factory.Parameters[0].GetByRefElementType(), ref _contentMayBeNull);
            return constructorTypes.ToImmutable();
        }

        foreach (var constructor in Constructors)
        {
            if (constructor.IsStatic ||
                constructor.DeclaredAccessibility != Accessibility.Public ||
                constructor.Parameters.Length != 1)
            {
                continue;
            }

            AddMemberType(constructorTypes, constructor.Parameters[0].GetByRefElementType(), ref _contentMayBeNull);
        }

        if (constructorTypes.Count > 0)
            return constructorTypes.ToImmutable();

        var accessPatternTypes = ImmutableArray.CreateBuilder<ITypeSymbol>();

        foreach (var method in GetMembers("TryGetValue").OfType<IMethodSymbol>())
        {
            if (method.IsStatic ||
                method.DeclaredAccessibility != Accessibility.Public ||
                method.Parameters.Length != 1 ||
                method.Parameters[0].RefKind != RefKind.Out)
            {
                continue;
            }

            AddMemberType(accessPatternTypes, method.Parameters[0].GetByRefElementType(), ref _contentMayBeNull);
        }

        return accessPatternTypes.ToImmutable();
    }

    private bool ComputeContentMayBeNull()
    {
        _ = MemberTypes;
        return _contentMayBeNull.GetValueOrDefault();
    }

    private static void AddMemberType(
        ImmutableArray<ITypeSymbol>.Builder builder,
        ITypeSymbol memberType,
        ref bool? contentMayBeNull)
    {
        memberType = UnionContentNullability.GetNonNullContentType(memberType, out var memberMayBeNull);
        contentMayBeNull = contentMayBeNull.GetValueOrDefault() || memberMayBeNull;

        if (builder.Any(existing => SymbolEqualityComparer.Default.Equals(existing, memberType)))
            return;

        builder.Add(memberType);
    }

}

internal sealed class PEUnionCaseSymbol : PENamedTypeSymbol, IUnionCaseTypeSymbol
{
    private IUnionSymbol? _union;
    private readonly IUnionSymbol? _inferredUnion;
    private string? _logicalCaseName;
    private ImmutableArray<IParameterSymbol>? _constructorParameters;
    private int? _ordinal;
    private readonly INamedTypeSymbol _metadataContainingType;

    public PEUnionCaseSymbol(
        ReflectionTypeLoader reflectionTypeLoader,
        System.Reflection.TypeInfo typeInfo,
        IUnionSymbol semanticUnion,
        INamedTypeSymbol metadataContainingType,
        INamespaceSymbol? containingNamespace,
        Location[] locations,
        IUnionSymbol? inferredUnion)
        : base(reflectionTypeLoader, typeInfo, semanticUnion, (INamedTypeSymbol)semanticUnion, containingNamespace, locations,
        addAsMember: false)
    {
        _inferredUnion = inferredUnion;
        _metadataContainingType = metadataContainingType;
    }

    public override string Name => _logicalCaseName ??= ComputeLogicalCaseName();

    public IUnionSymbol Union
    {
        get
        {
            if (_union is not null)
                return _union;

            if (ContainingType is IUnionSymbol containingUnion)
                return _union = containingUnion;

            if (_inferredUnion is not null)
                return _union = _inferredUnion;

            throw new InvalidOperationException($"Could not resolve discriminated union for '{Name}'.");
        }
    }

    public INamedTypeSymbol MetadataContainingType => _metadataContainingType;

    public ImmutableArray<IParameterSymbol> ConstructorParameters
    {
        get
        {
            if (_constructorParameters is not null)
                return _constructorParameters.Value;

            var constructor = GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == ".ctor");

            _constructorParameters = constructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
            return _constructorParameters.Value;
        }
    }

    public int Ordinal
    {
        get
        {
            if (_ordinal is not null)
                return _ordinal.Value;

            var cases = Union.Variants;
            var index = cases.IndexOf(this, SymbolEqualityComparer.Default);
            _ordinal = index >= 0 ? index : 0;
            return _ordinal.Value;
        }
    }

    private string ComputeLogicalCaseName()
    {
        var rawName = base.Name;
        var union = TryGetKnownUnion();
        if (union is null)
            return rawName;

        _ = UnionFacts.TryGetLogicalCaseNameFromMetadata(union.Name, rawName, out var logicalCaseName);
        return logicalCaseName;
    }

    private IUnionSymbol? TryGetKnownUnion()
    {
        if (_union is not null)
            return _union;

        if (ContainingType is IUnionSymbol containingUnion)
            return containingUnion;

        if (_inferredUnion is not null)
            return _inferredUnion;

        return null;
    }
}
