using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.CodeGen;

internal class TypeGenerator
{
    readonly Dictionary<IMethodSymbol, MethodGenerator> _methodGenerators = new Dictionary<IMethodSymbol, MethodGenerator>(SymbolEqualityComparer.Default);
    readonly Dictionary<IFieldSymbol, FieldBuilder> _fieldBuilders = new Dictionary<IFieldSymbol, FieldBuilder>(SymbolEqualityComparer.Default);
    readonly Dictionary<ILambdaSymbol, LambdaClosure> _lambdaClosures = new Dictionary<ILambdaSymbol, LambdaClosure>(SymbolEqualityComparer.Default);
    readonly Dictionary<IMethodSymbol, LambdaClosure> _methodClosures = new Dictionary<IMethodSymbol, LambdaClosure>(SymbolEqualityComparer.Default);

    private int _lambdaClosureOrdinal;

    private Compilation _compilation;
    private const string ExtensionMarkerMethodName = "<Extension>$";
    private const string ExtensionGroupingTypePrefix = "<>__RavenExtensionGrouping_For_";
    private const string ExtensionMarkerTypePrefix = "<>__RavenExtensionMarker_";
    private TypeBuilder? _extensionGroupingTypeBuilder;
    private TypeBuilder? _extensionMarkerTypeBuilder;
    private string? _extensionMarkerName;
    private GenericTypeParameterBuilder[]? _extensionGroupingTypeParameters;

    public CodeGenerator CodeGen { get; }
    public Compilation Compilation => _compilation ??= CodeGen.Compilation;
    public ITypeSymbol TypeSymbol { get; }
    public TypeBuilder? TypeBuilder { get; private set; }

    public IEnumerable<MethodGenerator> MethodGenerators => _methodGenerators.Values;

    public Type? Type { get; private set; }

    ImmutableArray<ITypeParameterSymbol> _inheritedTypeParameters = ImmutableArray<ITypeParameterSymbol>.Empty;
    bool _releasedInheritedTypeParameters;
    bool _memberBuildersDefined;

    public TypeGenerator(CodeGenerator codeGen, ITypeSymbol typeSymbol)
    {
        CodeGen = codeGen;
        TypeSymbol = typeSymbol;
    }

    public void DefineTypeBuilder()
    {
        TypeAttributes typeAttributes = TypeAttributes.Public;
        var hoistNestedSealedHierarchyCase = ShouldHoistNestedSealedHierarchyCase();

        if (TypeSymbol is INamedTypeSymbol named)
        {
            if (named is SourceNamedTypeSymbol sourceNamed && sourceNamed.IsExtensionDeclaration)
            {
                DefineExtensionContainerTypeBuilder(named);
                return;
            }

            if (named.TypeKind == TypeKind.Delegate)
            {
                var accessibilityAttributes = GetTypeAccessibilityAttributes(named);
                var delegateAttributes = accessibilityAttributes |
                    TypeAttributes.Class |
                    TypeAttributes.Sealed |
                    TypeAttributes.AutoClass |
                    TypeAttributes.AnsiClass;

                if (named.ContainingType is INamedTypeSymbol delegateContainingType)
                {
                    var containingGenerator = CodeGen.GetOrCreateTypeGenerator(delegateContainingType);
                    if (containingGenerator.TypeBuilder is null)
                        containingGenerator.DefineTypeBuilder();

                    TypeBuilder = containingGenerator.TypeBuilder!.DefineNestedType(
                        GetNestedTypeMetadataName(named),
                        delegateAttributes,
                        ResolveClrType(named.BaseType));
                }
                else
                {
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        named.ToFullyQualifiedMetadataName(),
                        delegateAttributes,
                        ResolveClrType(named.BaseType));
                }

                DefineTypeGenericParameters(named);
                ApplyTypeCustomAttributes();
                return;
            }

            if (named.TypeKind == TypeKind.Interface)
            {
                typeAttributes = GetTypeAccessibilityAttributes(named) | TypeAttributes.Interface | TypeAttributes.Abstract;
            }
            else
            {
                typeAttributes = GetTypeAccessibilityAttributes(named);
                if (named.TypeKind == TypeKind.Struct)
                {
                    typeAttributes |= TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass;
                }
                else
                {
                    if (named.IsAbstract)
                        typeAttributes |= TypeAttributes.Abstract;

                    if (named is SourceNamedTypeSymbol sn && sn.IsSealedHierarchy)
                    {
                        // Sealed hierarchy: do NOT emit IL sealed — inheritance is allowed for permitted types
                    }
                    else if (named.IsClosed)
                    {
                        typeAttributes |= TypeAttributes.Sealed;
                    }
                }
            }

            if (TypeSymbol is SourceUnionSymbol unionSymbol)
            {
                var unionNamed = (INamedTypeSymbol)unionSymbol;
                if (unionNamed.TypeKind == TypeKind.Struct)
                {
                    typeAttributes &= ~TypeAttributes.LayoutMask;
                    typeAttributes |= ShouldUseExplicitUnionLayout(unionSymbol)
                        ? TypeAttributes.ExplicitLayout
                        : TypeAttributes.SequentialLayout;
                }
            }
        }

        if (TypeSymbol.BaseType?.Name == "Enum")
        {
            var enumType = (INamedTypeSymbol)TypeSymbol;
            var accessibilityAttributes = GetTypeAccessibilityAttributes(enumType);
            var enumAttributes = accessibilityAttributes | TypeAttributes.Sealed | TypeAttributes.Serializable;
            if (enumType.ContainingType is INamedTypeSymbol enumContainingType)
            {
                var containingGenerator = CodeGen.GetOrCreateTypeGenerator(enumContainingType);
                if (containingGenerator.TypeBuilder is null)
                    containingGenerator.DefineTypeBuilder();

                TypeBuilder = containingGenerator.TypeBuilder!.DefineNestedType(
                    GetNestedTypeMetadataName(enumType),
                    enumAttributes,
                    ResolveClrType(TypeSymbol.BaseType!));
            }
            else
            {
                TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                    enumType.ToFullyQualifiedMetadataName(),
                    enumAttributes,
                    ResolveClrType(TypeSymbol.BaseType!));
            }

            // Add value__ using the enum's bound underlying type
            // Defaults to Int32 if no explicit underlying type was specified
            var enumUnderlyingTypeSymbol =
                (TypeSymbol as INamedTypeSymbol)?.EnumUnderlyingType
               ?? Compilation.GetSpecialType(SpecialType.System_Int32);

            var runtimeUnderlyingType = ResolveClrType(enumUnderlyingTypeSymbol);

            TypeBuilder.DefineField(
                "value__",
                runtimeUnderlyingType,
                FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName
            );

            ApplyTypeCustomAttributes();
            return;
        }

        TypeBuilder? containingTypeBuilder = null;
        if (!hoistNestedSealedHierarchyCase &&
            TypeSymbol is INamedTypeSymbol namedTypeWithMetadataOwner &&
            GetMetadataContainingType(namedTypeWithMetadataOwner) is INamedTypeSymbol containingType)
        {
            var containingGenerator = CodeGen.GetOrCreateTypeGenerator(containingType);
            if (containingGenerator.TypeBuilder is null)
                containingGenerator.DefineTypeBuilder();

            containingTypeBuilder = containingGenerator.TypeBuilder;
        }

        var syntaxReference = TypeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxReference is not null)
        {
            if (TypeSymbol is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface)
            {
                if (containingTypeBuilder is not null)
                {
                    var nestedName = GetNestedTypeMetadataName(nt);
                    TypeBuilder = containingTypeBuilder.DefineNestedType(
                        nestedName,
                        typeAttributes);
                }
                else
                {
                    var interfaceName = hoistNestedSealedHierarchyCase
                        ? GetHoistedNestedTypeMetadataName(nt)
                        : nt.ToFullyQualifiedMetadataName();
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        interfaceName,
                        hoistNestedSealedHierarchyCase
                            ? GetHoistedTopLevelAccessibilityAttributes(nt) | TypeAttributes.Interface | TypeAttributes.Abstract
                            : typeAttributes);
                }

                DefineTypeGenericParameters(nt);

                if (!nt.Interfaces.IsDefaultOrEmpty)
                {
                    foreach (var iface in GetAllInterfaces(nt))
                        TypeBuilder.AddInterfaceImplementation(ResolveClrType(iface));
                }

                ApplyTypeCustomAttributes();
                ApplyClosedHierarchyMetadata();
                return;
            }

            if (containingTypeBuilder is not null && TypeSymbol is INamedTypeSymbol nestedType)
            {
                var nestedName = GetNestedTypeMetadataName(nestedType);
                if (nestedType.TypeKind == TypeKind.Struct && TypeSymbol.BaseType is not null)
                {
                    TypeBuilder = containingTypeBuilder.DefineNestedType(
                        nestedName,
                        typeAttributes,
                        ResolveClrType(TypeSymbol.BaseType));
                }
                else
                {
                    TypeBuilder = containingTypeBuilder.DefineNestedType(
                        nestedName,
                        typeAttributes);
                }
            }
            else
            {
                if (TypeSymbol is INamedTypeSymbol namedForDefinition &&
                    namedForDefinition.TypeKind == TypeKind.Struct &&
                    TypeSymbol.BaseType is not null)
                {
                    var structName = hoistNestedSealedHierarchyCase
                        ? GetHoistedNestedTypeMetadataName(namedForDefinition)
                        : namedForDefinition.ToFullyQualifiedMetadataName();
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        structName,
                        hoistNestedSealedHierarchyCase
                            ? GetHoistedTopLevelAccessibilityAttributes(namedForDefinition) | (typeAttributes & ~TypeAttributes.VisibilityMask)
                            : typeAttributes,
                        ResolveClrType(TypeSymbol.BaseType));
                }
                else
                {
                    var typeName = hoistNestedSealedHierarchyCase && TypeSymbol is INamedTypeSymbol hoistedNamed
                        ? GetHoistedNestedTypeMetadataName(hoistedNamed)
                        : ((INamedTypeSymbol)TypeSymbol).ToFullyQualifiedMetadataName();
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        typeName,
                        hoistNestedSealedHierarchyCase && TypeSymbol is INamedTypeSymbol hoistedAccessibilityNamed
                            ? GetHoistedTopLevelAccessibilityAttributes(hoistedAccessibilityNamed) | (typeAttributes & ~TypeAttributes.VisibilityMask)
                            : typeAttributes);
                }
            }

            if (TypeSymbol is INamedTypeSymbol namedType)
                DefineTypeGenericParameters(namedType);

            // Set base type after generic parameters are defined so type parameters (e.g. T) can be resolved.
            if (TypeSymbol is INamedTypeSymbol namedWithBase &&
                namedWithBase.TypeKind != TypeKind.Struct &&
                TypeSymbol.BaseType is not null)
                TypeBuilder!.SetParent(ResolveClrType(TypeSymbol.BaseType));

        }
        else if (TypeSymbol is INamedTypeSymbol synthesizedType)
        {
            var synthesizedAttributes = GetTypeAccessibilityAttributes(synthesizedType);

            if (synthesizedType.TypeKind == TypeKind.Interface)
                synthesizedAttributes |= TypeAttributes.Interface | TypeAttributes.Abstract;
            else if (synthesizedType.TypeKind == TypeKind.Struct)
            {
                if (synthesizedType is SynthesizedAsyncStateMachineTypeSymbol)
                    synthesizedAttributes |= TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit;
                else
                    synthesizedAttributes |= TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass;
            }
            else
            {
                synthesizedAttributes |= TypeAttributes.Class;

                if (synthesizedType.IsAbstract)
                    synthesizedAttributes |= TypeAttributes.Abstract;

                if (synthesizedType.IsClosed)
                    synthesizedAttributes |= TypeAttributes.Sealed;
            }

            TypeBuilder? synthesizedContainingBuilder = null;
            if (synthesizedType.ContainingType is INamedTypeSymbol synthesizedContainingType)
            {
                var containingGenerator = CodeGen.GetOrCreateTypeGenerator(synthesizedContainingType);
                if (containingGenerator.TypeBuilder is null)
                    containingGenerator.DefineTypeBuilder();

                synthesizedContainingBuilder = containingGenerator.TypeBuilder;
            }

            if (synthesizedContainingBuilder is not null)
            {
                var nestedName = GetNestedTypeMetadataName(synthesizedType);
                if (synthesizedType.TypeKind == TypeKind.Struct && synthesizedType.BaseType is not null)
                {
                    TypeBuilder = synthesizedContainingBuilder.DefineNestedType(
                        nestedName,
                        synthesizedAttributes,
                        ResolveClrType(synthesizedType.BaseType));
                }
                else
                {
                    TypeBuilder = synthesizedContainingBuilder.DefineNestedType(
                        nestedName,
                        synthesizedAttributes);
                }
            }
            else
            {
                var synthesizedMetadataName = synthesizedType is SynthesizedUnionCompanionTypeSymbol
                    ? synthesizedType.ToFullyQualifiedMetadataName()
                    : synthesizedType.MetadataName;

                if (synthesizedType.TypeKind == TypeKind.Struct && synthesizedType.BaseType is not null)
                {
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        synthesizedMetadataName,
                        synthesizedAttributes,
                        ResolveClrType(synthesizedType.BaseType));
                }
                else
                {
                    TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                        synthesizedMetadataName,
                        synthesizedAttributes);
                }
            }

            DefineTypeGenericParameters(synthesizedType);

            // Set base type after generic parameters are defined so type parameters can be resolved.
            if (synthesizedType.TypeKind != TypeKind.Struct &&
                synthesizedType.BaseType is not null)
                TypeBuilder!.SetParent(ResolveClrType(synthesizedType.BaseType));
        }

        if (TypeSymbol is INamedTypeSymbol nt2 && !nt2.Interfaces.IsDefaultOrEmpty)
        {
            foreach (var iface in GetAllInterfaces(nt2))
                TypeBuilder.AddInterfaceImplementation(ResolveClrType(iface));
        }

        var hasRavenStructuredDisplay = TypeSymbol is SourceUnionSymbol or
            SourceUnionCaseTypeSymbol or
            SourceNamedTypeSymbol { IsRecord: true };
        if (hasRavenStructuredDisplay &&
            RavenRuntimeTypeNames.GetStructuredDisplayInterface(Compilation) is { } structuredDisplayInterface)
        {
            TypeBuilder.AddInterfaceImplementation(ResolveClrType(structuredDisplayInterface));
        }

        ApplyTypeCustomAttributes();
        if (TypeSymbol is INamedTypeSymbol { IsRefLikeType: true })
        {
            var isByRefLikeAttribute = CodeGen.CreateIsByRefLikeAttributeBuilder();
            if (isByRefLikeAttribute is not null)
                TypeBuilder!.SetCustomAttribute(isByRefLikeAttribute);
        }
        if (TypeSymbol is SourceNamedTypeSymbol { IsReadOnly: true })
        {
            var isReadOnlyAttribute = CodeGen.CreateIsReadOnlyAttributeBuilder();
            if (isReadOnlyAttribute is not null)
                TypeBuilder!.SetCustomAttribute(isReadOnlyAttribute);
        }
        ApplyTopLevelAttributeIfNamespaceMembersContainer();
        ApplyCompilerGeneratedAttributeIfClosureFrame();

        if (TypeSymbol is SourceUnionSymbol discriminatedUnionSymbol)
        {
            TypeBuilder!.AddInterfaceImplementation(CodeGen.GetUnionInterfaceType());

            if (discriminatedUnionSymbol.TypeKind == TypeKind.Struct)
                ApplyDiscriminatedUnionLayout();
            CodeGen.ApplyDiscriminatedUnionAttribute(TypeBuilder!.SetCustomAttribute);
        }

        ApplyClosedHierarchyMetadata();

        if (TypeSymbol is SynthesizedUnionCompanionTypeSymbol companionType)
        {
            CodeGen.ApplyRavenUnionCompanionAttribute(
                companionType.Union.ToFullyQualifiedMetadataName(),
                TypeBuilder!.SetCustomAttribute);
        }

        EnsureExtensionGroupingType();
    }

    private void ApplyClosedHierarchyMetadata()
    {
        if (TypeSymbol is SourceNamedTypeSymbol sourceNamedType && sourceNamedType.IsSealedHierarchy)
        {
            CodeGen.ApplyClosedHierarchyAttribute(
                sourceNamedType.TypeKind,
                sourceNamedType.PermittedDirectSubtypes,
                TypeBuilder!.SetCustomAttribute);
        }
    }

    private static INamedTypeSymbol? GetMetadataContainingType(INamedTypeSymbol type)
        => type is IUnionCaseTypeSymbol { IsUnionCase: true } unionCase
            ? unionCase.MetadataContainingType
            : type.ContainingType;

    private static string GetNestedTypeMetadataName(INamedTypeSymbol type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));

        var name = type.Name;
        if (type.Arity > 0)
            name = $"{name}`{type.Arity}";

        return name;
    }

    private void ApplyDiscriminatedUnionLayout()
    {
        if (TypeBuilder is null)
            return;

        if (TypeSymbol is not INamedTypeSymbol namedType)
            return;

        var layoutKind = LayoutKind.Sequential;

        if (TypeSymbol is SourceUnionSymbol unionSymbol && ShouldUseExplicitUnionLayout(unionSymbol))
            layoutKind = LayoutKind.Explicit;
        var layoutCtor = typeof(StructLayoutAttribute).GetConstructor(new[] { typeof(LayoutKind) });
        if (layoutCtor is null)
            return;

        if (namedType.IsGenericType && layoutKind == LayoutKind.Explicit)
            throw new InvalidOperationException("Generic discriminated unions cannot use explicit layout on .NET.");

        var attribute = new CustomAttributeBuilder(layoutCtor, new object[] { layoutKind });
        TypeBuilder.SetCustomAttribute(attribute);
    }

    internal void ApplyDeferredTypeBuilderAttributes()
    {
        if (TypeSymbol is SourceUnionSymbol unionSymbol)
            ApplyRavenUnionCaseAttributes(unionSymbol);
    }

    private bool _ravenUnionCaseAttributesApplied;

    private void ApplyRavenUnionCaseAttributes(SourceUnionSymbol unionSymbol)
    {
        if (_ravenUnionCaseAttributesApplied ||
            TypeBuilder is null ||
            unionSymbol.DeclaredCaseTypes.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var caseSymbol in unionSymbol.DeclaredCaseTypes.OrderBy(static caseSymbol => caseSymbol.Ordinal))
        {
            var caseTypeMetadataName = ((INamedTypeSymbol)caseSymbol).ToFullyQualifiedMetadataName();
            CodeGen.ApplyRavenUnionCaseAttribute(
                caseTypeMetadataName,
                caseSymbol.Name,
                caseSymbol.Ordinal,
                TypeBuilder.SetCustomAttribute);
        }

        _ravenUnionCaseAttributesApplied = true;
    }

    private bool ShouldUseExplicitUnionLayout(SourceUnionSymbol unionSymbol)
    {
        var named = (INamedTypeSymbol)unionSymbol;
        if (named.IsGenericType)
            return false;

        return !UnionHasManagedReferences(unionSymbol);
    }

    private bool UnionHasManagedReferences(SourceUnionSymbol unionSymbol)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var field in unionSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
                continue;

            if (ContainsManagedReference(field.Type, visited))
                return true;
        }

        foreach (var caseSymbol in unionSymbol.DeclaredCaseTypes)
        {
            foreach (var parameter in caseSymbol.ConstructorParameters)
            {
                if (parameter.RefKind != RefKind.None || parameter.Type is null)
                    continue;

                if (ContainsManagedReference(parameter.Type, visited))
                    return true;
            }
        }

        return false;
    }

    private bool ContainsManagedReference(ITypeSymbol typeSymbol, HashSet<ITypeSymbol> visited)
    {
        if (typeSymbol is null)
            return false;

        var definition = typeSymbol.OriginalDefinition ?? typeSymbol;
        if (!visited.Add(definition))
            return false;

        if (typeSymbol.IsReferenceType)
            return true;

        if (typeSymbol is ITypeParameterSymbol)
            return true;

        switch (typeSymbol)
        {
            case IArrayTypeSymbol:
                return true;
            case RefTypeSymbol:
                return true;
            case IAddressTypeSymbol:
                return true;
            case IPointerTypeSymbol:
                return true;
            case NullableTypeSymbol nullableType:
                return ContainsManagedReference(nullableType.UnderlyingType, visited);
            case ITupleTypeSymbol tupleType:
                foreach (var element in tupleType.TupleElements)
                {
                    if (ContainsManagedReference(element.Type, visited))
                        return true;
                }

                return false;
            case INamedTypeSymbol named when named.IsValueType:
                foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic)
                        continue;

                    if (ContainsManagedReference(field.Type, visited))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    private void DefineTypeGenericParameters(INamedTypeSymbol namedType)
    {
        if (TypeBuilder is null)
            return;

        var inScopeParameters = GetTypeParametersInScope(namedType);

        // Some source containers (notably generic extension containers) are lowered to
        // non-generic runtime types. For nested synthesized types under those containers,
        // inherited source type parameters must be dropped to keep emitted signatures valid.
        if (ShouldHoistNestedSealedHierarchyCase(namedType))
        {
            inScopeParameters = namedType.TypeParameters;
        }
        else if (namedType.ContainingType is not null &&
            TypeBuilder.DeclaringType is { IsGenericTypeDefinition: false, ContainsGenericParameters: false })
        {
            inScopeParameters = namedType.TypeParameters;
        }

        if (inScopeParameters.IsDefaultOrEmpty)
            return;

        var parameterBuilders = TypeBuilder.DefineGenericParameters(inScopeParameters.Select(tp => tp.Name).ToArray());
        CodeGen.RegisterGenericParameters(inScopeParameters, parameterBuilders);

        _inheritedTypeParameters = inScopeParameters.Length > namedType.TypeParameters.Length && namedType.ContainingType is not null
            ? GetTypeParametersInScope(namedType.ContainingType)
            : ImmutableArray<ITypeParameterSymbol>.Empty;
    }

    private static ImmutableArray<ITypeParameterSymbol> GetTypeParametersInScope(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
            return ImmutableArray<ITypeParameterSymbol>.Empty;

        var stack = new Stack<INamedTypeSymbol>();
        var current = typeSymbol;
        while (current is not null)
        {
            stack.Push(current);
            current = current.ContainingType;
        }

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>();
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!next.TypeParameters.IsDefaultOrEmpty)
                builder.AddRange(next.TypeParameters);
        }

        return builder.ToImmutable();
    }

    private bool ShouldHoistNestedSealedHierarchyCase()
        => TypeSymbol is INamedTypeSymbol namedType && ShouldHoistNestedSealedHierarchyCase(namedType);

    private static bool ShouldHoistNestedSealedHierarchyCase(INamedTypeSymbol namedType)
    {
        if (namedType.ContainingType is not INamedTypeSymbol containingType ||
            !containingType.IsSealedHierarchy ||
            !containingType.IsGenericType)
        {
            return false;
        }

        if (namedType.BaseType is INamedTypeSymbol baseType &&
            SymbolEqualityComparer.Default.Equals(GetDefinition(baseType), containingType))
        {
            return true;
        }

        return namedType.Interfaces.Any(interfaceType =>
            SymbolEqualityComparer.Default.Equals(GetDefinition(interfaceType), containingType));
    }

    private static INamedTypeSymbol GetDefinition(INamedTypeSymbol type)
        => type.ConstructedFrom is INamedTypeSymbol definition &&
           !SymbolEqualityComparer.Default.Equals(type, definition)
            ? definition
            : type;

    internal static string GetEmittedTypeMetadataName(INamedTypeSymbol type)
        => ShouldHoistNestedSealedHierarchyCase(type)
            ? GetHoistedNestedTypeMetadataName(type)
            : type.ToFullyQualifiedMetadataName();

    private static string GetHoistedNestedTypeMetadataName(INamedTypeSymbol type)
    {
        var namespacePrefix = type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.MetadataName + "."
            : string.Empty;
        var containingName = type.ContainingType?.MetadataName
            .Replace('+', '_')
            .Replace('`', '_') ?? "Nested";
        var ownName = type.MetadataName.Split('+').Last().Replace('`', '_');
        return $"{namespacePrefix}{containingName}__{ownName}";
    }

    private static TypeAttributes GetHoistedTopLevelAccessibilityAttributes(INamedTypeSymbol typeSymbol)
        => typeSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => TypeAttributes.Public,
            _ => TypeAttributes.NotPublic
        };

    private static TypeAttributes GetTypeAccessibilityAttributes(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.ContainingType is null)
        {
            return typeSymbol.DeclaredAccessibility switch
            {
                Accessibility.Public => TypeAttributes.Public,
                Accessibility.Internal => TypeAttributes.NotPublic,
                Accessibility.Private => TypeAttributes.NotPublic,
                Accessibility.ProtectedAndProtected => TypeAttributes.NotPublic,
                Accessibility.ProtectedOrInternal => TypeAttributes.NotPublic,
                Accessibility.ProtectedAndInternal => TypeAttributes.NotPublic,
                _ => TypeAttributes.NotPublic
            };
        }

        return typeSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => TypeAttributes.NestedPublic,
            Accessibility.Private => TypeAttributes.NestedPrivate,
            Accessibility.ProtectedAndProtected => TypeAttributes.NestedFamily,
            Accessibility.Internal => TypeAttributes.NestedAssembly,
            Accessibility.ProtectedOrInternal => TypeAttributes.NestedFamORAssem,
            Accessibility.ProtectedAndInternal => TypeAttributes.NestedFamANDAssem,
            _ => TypeAttributes.NestedPrivate
        };
    }

    private static FieldAttributes GetFieldAccessibilityAttributes(IFieldSymbol fieldSymbol)
    {
        return fieldSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => FieldAttributes.Public,
            Accessibility.Private => FieldAttributes.Private,
            Accessibility.Internal => FieldAttributes.Assembly,
            Accessibility.ProtectedAndProtected => FieldAttributes.Family,
            Accessibility.ProtectedOrInternal => FieldAttributes.FamORAssem,
            Accessibility.ProtectedAndInternal => FieldAttributes.FamANDAssem,
            _ => FieldAttributes.Private
        };
    }

    internal FieldBuilder EnsureFieldBuilder(SourceFieldSymbol fieldSymbol)
    {
        if (fieldSymbol is null)
            throw new ArgumentNullException(nameof(fieldSymbol));

        if (_fieldBuilders.TryGetValue(fieldSymbol, out var existing))
            return existing;

        if (TypeBuilder is null)
            DefineTypeBuilder();

        if (TypeBuilder is null)
            throw new InvalidOperationException("Type builder must be defined before creating field builders.");

        var fieldType = ResolveFieldClrType(fieldSymbol);
        var attributes = GetFieldAccessibilityAttributes(fieldSymbol);

        if (fieldSymbol.IsConst)
            attributes |= FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;

        if (fieldSymbol.IsReadOnly && !fieldSymbol.IsConst)
            attributes |= FieldAttributes.InitOnly;

        if (fieldSymbol.IsStatic)
            attributes |= FieldAttributes.Static;

        var fieldBuilder = TypeBuilder.DefineField(fieldSymbol.Name, fieldType, attributes);

        if (TypeSymbol is SourceUnionSymbol unionSymbol)
        {
            if (ShouldUseExplicitUnionLayout(unionSymbol))
            {
                if (UnionFieldUtilities.IsTagFieldName(fieldSymbol.Name))
                    fieldBuilder.SetOffset(UnionFieldUtilities.TagFieldOffset);
                else if (UnionFieldUtilities.IsPayloadFieldName(fieldSymbol.Name))
                    fieldBuilder.SetOffset(UnionFieldUtilities.PayloadFieldOffset);
            }
        }

        if (fieldSymbol.IsConst)
            fieldBuilder.SetConstant(fieldSymbol.GetConstantValue());

        CodeGen.ApplyNullableAttribute(fieldSymbol.Type, fieldBuilder.SetCustomAttribute);

        var tupleNamesAttr = CodeGen.CreateTupleElementNamesAttribute(fieldSymbol.Type);
        if (tupleNamesAttr is not null)
            fieldBuilder.SetCustomAttribute(tupleNamesAttr);

        var fixedLengthArrayAttr = CodeGen.CreateFixedLengthArrayAttribute(fieldSymbol.Type);
        if (fixedLengthArrayAttr is not null)
            fieldBuilder.SetCustomAttribute(fixedLengthArrayAttr);

        CodeGen.ApplyCustomAttributes(fieldSymbol.GetAttributes(), attribute => fieldBuilder.SetCustomAttribute(attribute));

        _fieldBuilders[fieldSymbol] = fieldBuilder;
        CodeGen.AddMemberBuilder(fieldSymbol, fieldBuilder);

        return fieldBuilder;
    }

    private Type ResolveFieldClrType(IFieldSymbol fieldSymbol)
    {
        if (fieldSymbol is null)
            throw new ArgumentNullException(nameof(fieldSymbol));

        var fieldTypeSymbol = fieldSymbol.Type;

        if (fieldTypeSymbol.Equals(TypeSymbol, SymbolEqualityComparer.Default))
        {
            if (TypeBuilder is null)
                throw new InvalidOperationException("Type builder must be created before resolving field types.");

            return TypeBuilder;
        }

        var resolved = ResolveClrType(fieldTypeSymbol);

        if (resolved is TypeBuilder)
            return resolved;

        if (fieldTypeSymbol is INamedTypeSymbol named &&
            named.IsGenericType &&
            !named.TypeArguments.IsDefaultOrEmpty)
        {
            var definition = named.ConstructedFrom;

            try
            {
                var argumentTypes = named.TypeArguments
                    .Select(ResolveClrType)
                    .ToArray();

                Type definitionType;

                if (definition.SpecialType == SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder_T)
                {
                    definitionType = typeof(AsyncTaskMethodBuilder<int>).GetGenericTypeDefinition();
                }
                else
                {
                    definitionType = ResolveClrType(definition);
                }

                if (definitionType.IsGenericTypeDefinition || definitionType.ContainsGenericParameters)
                    return definitionType.MakeGenericType(argumentTypes);

                if (resolved.IsGenericType && resolved.ContainsGenericParameters)
                    return resolved.GetGenericTypeDefinition().MakeGenericType(argumentTypes);
            }
            catch
            {
                // Fall back to the initially resolved type when the runtime types for the
                // generic arguments cannot be materialised (e.g. for unsupported type parameters).
            }
        }

        return resolved;
    }

    public void DefineMemberBuilders()
    {
        if (_memberBuildersDefined)
            return;

        _memberBuildersDefined = true;

        if (TypeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
        {
            DefineDelegateMembers(delegateType);
            return;
        }

        if (TypeSymbol.BaseType is { ContainingNamespace.Name: "System", Name: "Enum" })
        {
            // Enum types only emit fields (value__ is created in DefineTypeBuilder).
            // Use the normal field path so const values, accessibility, and attributes are consistent.
            foreach (var fieldSymbol in TypeSymbol.GetMembers().OfType<IFieldSymbol>())
            {
                // The runtime backing field is injected directly when defining the enum type.
                if (string.Equals(fieldSymbol.Name, "value__", StringComparison.Ordinal))
                    continue;

                if (fieldSymbol is SourceFieldSymbol sourceField)
                {
                    _ = EnsureFieldBuilder(sourceField);
                }
                else if (fieldSymbol is SourceSymbol src)
                {
                    // Best-effort fallback for any non-SourceFieldSymbol fields that still
                    // participate in codegen (should be rare for enums).
                    var fieldType = ResolveFieldClrType(fieldSymbol);
                    var attributes = GetFieldAccessibilityAttributes(fieldSymbol);

                    if (fieldSymbol.IsConst)
                        attributes |= FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;

                    if (fieldSymbol.IsReadOnly && !fieldSymbol.IsConst)
                        attributes |= FieldAttributes.InitOnly;

                    if (fieldSymbol.IsStatic)
                        attributes |= FieldAttributes.Static;

                    var builder = TypeBuilder.DefineField(fieldSymbol.Name, fieldType, attributes);

                    if (fieldSymbol.IsConst)
                        builder.SetConstant(fieldSymbol.GetConstantValue());

                    CodeGen.ApplyNullableAttribute(fieldSymbol.Type, builder.SetCustomAttribute);

                    var tupleNamesAttr = CodeGen.CreateTupleElementNamesAttribute(fieldSymbol.Type);
                    if (tupleNamesAttr is not null)
                        builder.SetCustomAttribute(tupleNamesAttr);

                    CodeGen.ApplyCustomAttributes(fieldSymbol.GetAttributes(), attribute => builder.SetCustomAttribute(attribute));

                    _fieldBuilders[fieldSymbol] = builder;
                    CodeGen.AddMemberBuilder(src, builder);
                }
            }

            return;
        }

        if (TypeSymbol is SourceNamedTypeSymbol sourceType && sourceType.IsExtensionDeclaration)
            DefineExtensionGroupingMembers();

        foreach (var memberSymbol in TypeSymbol.GetMembers())
        {
            if (memberSymbol.ContainingType is { } containingType &&
                !SymbolEqualityComparer.Default.Equals(containingType, TypeSymbol))
            {
                // Skip members that belong to nested types (e.g., discriminated union cases)
                // but are surfaced on the containing union symbol for semantic analysis.
                continue;
            }

            switch (memberSymbol)
            {
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly or MethodKind.EventAdd or MethodKind.EventRemove):
                    {
                        if (methodSymbol is SourceMethodSymbol { IsSignatureSkeleton: true })
                            break;

                        if (methodSymbol is SynthesizedMainMethodSymbol { ContainsExecutableCode: false })
                            break;

                        if (methodSymbol.MethodKind == MethodKind.LambdaMethod)
                            break;

                        if (TypeSymbol is SynthesizedAsyncStateMachineTypeSymbol &&
                            methodSymbol.MethodKind == MethodKind.Constructor)
                            break;

                        if (_methodGenerators.ContainsKey(methodSymbol))
                            break;

                        var methodGenerator = new MethodGenerator(this, methodSymbol, CodeGen.ILBuilderFactory);

                        if (methodSymbol is SourceLambdaSymbol sourceLambda && sourceLambda.HasCaptures)
                        {
                            var closure = EnsureLambdaClosure(sourceLambda);
                            methodGenerator.SetLambdaClosure(closure);
                        }
                        else if ((methodSymbol as SourceMethodSymbol ?? methodSymbol.UnderlyingSymbol as SourceMethodSymbol) is { } sourceMethod &&
                                 methodSymbol.MethodKind != MethodKind.Function)
                        {
                            // Local functions (MethodKind.Function) defer their closure assignment:
                            // DeclareLocals will create a single shared DisplayClass for the entire
                            // containing method scope and register it via EnsureSharedMethodClosure.
                            var capturedVariables = GetCapturedVariablesForMethod(sourceMethod);
                            if (!capturedVariables.IsDefaultOrEmpty &&
                                !Compilation.IsEntryPointCandidate(sourceMethod) &&
                                !sourceMethod.IsImplicitlyDeclared)
                            {
                                var closure = EnsureMethodClosure(sourceMethod, capturedVariables);
                                methodGenerator.SetLambdaClosure(closure);
                            }
                        }

                        _methodGenerators[methodSymbol] = methodGenerator;
                        methodGenerator.DefineMethodBuilder();
                        if (methodGenerator.MethodBase is MethodBuilder methodBuilder)
                            ApplyExtensionMarkerNameAttribute(methodSymbol, methodBuilder.SetCustomAttribute);

                        CodeGen.AddMemberBuilder((SourceSymbol)methodSymbol, methodGenerator.MethodBase);
                        break;
                    }
                case IFieldSymbol fieldSymbol:
                    {
                        if (fieldSymbol is SourceFieldSymbol sourceField)
                        {
                            _ = EnsureFieldBuilder(sourceField);
                        }

                        break;
                    }
                case IPropertySymbol propertySymbol:
                    {
                        var sourceProperty = propertySymbol as SourcePropertySymbol
                            ?? propertySymbol.UnderlyingSymbol as SourcePropertySymbol;

                        if (sourceProperty is not null &&
                            sourceProperty.EmitAsFieldOnly)
                            break;

                        var getterSymbol = propertySymbol.GetMethod as IMethodSymbol;
                        var setterSymbol = propertySymbol.SetMethod as IMethodSymbol;

                        DebugUtils.PrintDebug($"Defining propertySymbol: {propertySymbol.Name} from {TypeBuilder.Name}");

                        if (propertySymbol.IsExtensionProperty)
                        {
                            // Extension properties are not emitted as real CLR properties on the extension container.
                            // But their accessor methods *must* be emitted as real methods so invocation works.

                            if (getterSymbol is not null)
                            {
                                if (_methodGenerators.ContainsKey(getterSymbol))
                                    continue;

                                var getGen2 = new MethodGenerator(this, getterSymbol, CodeGen.ILBuilderFactory);
                                _methodGenerators[getterSymbol] = getGen2;
                                getGen2.DefineMethodBuilder();
                                if (getGen2.MethodBase is MethodBuilder getterBuilder)
                                    ApplyExtensionMarkerNameAttribute(getterSymbol, getterBuilder.SetCustomAttribute);
                                CodeGen.AddMemberBuilder((SourceSymbol)getterSymbol, getGen2.MethodBase);
                            }

                            if (setterSymbol is not null)
                            {
                                if (_methodGenerators.ContainsKey(setterSymbol))
                                    continue;

                                var setGen2 = new MethodGenerator(this, setterSymbol, CodeGen.ILBuilderFactory);
                                _methodGenerators[setterSymbol] = setGen2;
                                setGen2.DefineMethodBuilder();
                                if (setGen2.MethodBase is MethodBuilder setterBuilder)
                                    ApplyExtensionMarkerNameAttribute(setterSymbol, setterBuilder.SetCustomAttribute);
                                CodeGen.AddMemberBuilder((SourceSymbol)setterSymbol, setGen2.MethodBase);
                            }

                            // No CLR PropertyBuilder for extension properties on the container.
                            break;
                        }

                        MethodGenerator? getGen = null;
                        MethodGenerator? setGen = null;

                        if (getterSymbol is not null)
                        {
                            if (!_methodGenerators.TryGetValue(getterSymbol, out getGen))
                            {
                                getGen = new MethodGenerator(this, getterSymbol, CodeGen.ILBuilderFactory);
                                _methodGenerators[getterSymbol] = getGen;
                                getGen.DefineMethodBuilder();
                                CodeGen.AddMemberBuilder((SourceSymbol)getterSymbol, getGen.MethodBase);
                            }
                        }

                        if (setterSymbol is not null)
                        {
                            if (!_methodGenerators.TryGetValue(setterSymbol, out setGen))
                            {
                                setGen = new MethodGenerator(this, setterSymbol, CodeGen.ILBuilderFactory);
                                _methodGenerators[setterSymbol] = setGen;
                                setGen.DefineMethodBuilder();
                                CodeGen.AddMemberBuilder((SourceSymbol)setterSymbol, setGen.MethodBase);
                            }
                        }

                        var propertyType = ResolveClrType(propertySymbol.Type);

                        Type[]? paramTypes = null;
                        if (propertySymbol.IsIndexer)
                        {
                            var parameters = propertySymbol.Parameters;
                            if (!parameters.IsDefaultOrEmpty)
                            {
                                paramTypes = parameters
                                    .Select(p => ResolveClrType(p.Type))
                                    .ToArray();
                            }
                        }

                        var propBuilder = TypeBuilder.DefineProperty(propertySymbol.MetadataName, PropertyAttributes.None, propertyType, paramTypes);

                        if (getGen != null)
                            propBuilder.SetGetMethod((MethodBuilder)getGen.MethodBase);
                        if (setGen != null)
                            propBuilder.SetSetMethod((MethodBuilder)setGen.MethodBase);

                        CodeGen.ApplyNullableAttribute(propertySymbol.Type, propBuilder.SetCustomAttribute);

                        var fixedLengthArrayAttr = CodeGen.CreateFixedLengthArrayAttribute(propertySymbol.Type);
                        if (fixedLengthArrayAttr is not null)
                            propBuilder.SetCustomAttribute(fixedLengthArrayAttr);

                        CodeGen.ApplyCustomAttributes(propertySymbol.GetAttributes(), attribute => propBuilder.SetCustomAttribute(attribute));
                        ApplyExtensionMarkerNameAttribute(propertySymbol, propBuilder.SetCustomAttribute);

                        CodeGen.AddMemberBuilder((SourceSymbol)propertySymbol, propBuilder);
                        break;
                    }
                case IEventSymbol eventSymbol:
                    {
                        var addSymbol = eventSymbol.AddMethod as IMethodSymbol;
                        var removeSymbol = eventSymbol.RemoveMethod as IMethodSymbol;

                        MethodGenerator? addGen = null;
                        MethodGenerator? removeGen = null;

                        if (addSymbol is not null)
                        {
                            addGen = new MethodGenerator(this, addSymbol, CodeGen.ILBuilderFactory);
                            _methodGenerators[addSymbol] = addGen;
                            addGen.DefineMethodBuilder();
                            CodeGen.AddMemberBuilder((SourceSymbol)addSymbol, addGen.MethodBase);
                        }

                        if (removeSymbol is not null)
                        {
                            removeGen = new MethodGenerator(this, removeSymbol, CodeGen.ILBuilderFactory);
                            _methodGenerators[removeSymbol] = removeGen;
                            removeGen.DefineMethodBuilder();
                            CodeGen.AddMemberBuilder((SourceSymbol)removeSymbol, removeGen.MethodBase);
                        }

                        var eventType = ResolveClrType(eventSymbol.Type);
                        var eventBuilder = TypeBuilder.DefineEvent(eventSymbol.MetadataName, EventAttributes.None, eventType);

                        if (addGen != null)
                            eventBuilder.SetAddOnMethod((MethodBuilder)addGen.MethodBase);
                        if (removeGen != null)
                            eventBuilder.SetRemoveOnMethod((MethodBuilder)removeGen.MethodBase);

                        CodeGen.ApplyNullableAttribute(eventSymbol.Type, eventBuilder.SetCustomAttribute);

                        CodeGen.ApplyCustomAttributes(eventSymbol.GetAttributes(), attribute => eventBuilder.SetCustomAttribute(attribute));
                        break;
                    }
            }
        }

    }

    private void DefineDelegateMembers(INamedTypeSymbol delegateType)
    {
        if (TypeBuilder is null)
            throw new InvalidOperationException("Type builder must be defined before creating delegate members.");

        var ctorSymbol = delegateType.Constructors.FirstOrDefault();
        if (ctorSymbol is not null)
        {
            var ctorParameters = ctorSymbol.Parameters
                .Select(p => ResolveClrType(p.Type))
                .ToArray();

            var ctorBuilder = TypeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
                CallingConventions.Standard,
                ctorParameters);
            ctorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            CodeGen.ApplyCustomAttributes(ctorSymbol.GetAttributes(), attribute => ctorBuilder.SetCustomAttribute(attribute));
            if (ctorSymbol is SourceSymbol ctorSource)
                CodeGen.AddMemberBuilder(ctorSource, ctorBuilder);
        }
        else
        {
            var objectType = ResolveClrType(CodeGen.Compilation.GetSpecialType(SpecialType.System_Object));
            var intPtrType = ResolveClrType(CodeGen.Compilation.GetSpecialType(SpecialType.System_IntPtr));
            var ctorBuilder = TypeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
                CallingConventions.Standard,
                [objectType, intPtrType]);
            ctorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        }

        var invokeSymbol = delegateType.GetDelegateInvokeMethod();
        if (invokeSymbol is not null)
        {
            var invokeParameters = invokeSymbol.Parameters
                .Select(GetParameterClrType)
                .ToArray();

            var invokeBuilder = TypeBuilder.DefineMethod(
                invokeSymbol.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                GetMethodReturnClrType(invokeSymbol.ReturnType),
                invokeParameters);
            invokeBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            for (var i = 0; i < invokeSymbol.Parameters.Length; i++)
            {
                var parameter = invokeSymbol.Parameters[i];
                var parameterBuilder = invokeBuilder.DefineParameter(
                    i + 1,
                    GetParameterAttributes(parameter),
                    parameter.Name);
                var hasImplicitScopedDefault =
                    parameter.RefKind == RefKind.Out ||
                    parameter.RefKind == RefKind.Ref &&
                    SemanticFacts.MayBeRefLike(parameter.Type);
                if (parameter.ScopedKind != ScopedKind.None && !hasImplicitScopedDefault)
                {
                    var scopedRefAttribute = CodeGen.CreateScopedRefAttributeBuilder();
                    if (scopedRefAttribute is not null)
                        parameterBuilder.SetCustomAttribute(scopedRefAttribute);
                }

                CodeGen.ApplyCustomAttributes(
                    parameter.GetAttributes(),
                    attribute => parameterBuilder.SetCustomAttribute(attribute));
            }

            CodeGen.ApplyCustomAttributes(invokeSymbol.GetAttributes(), attribute => invokeBuilder.SetCustomAttribute(attribute));
            if (invokeSymbol is SourceSymbol invokeSource)
                CodeGen.AddMemberBuilder(invokeSource, invokeBuilder);
        }
    }

    public void EmitMemberILBodies()
    {
        DebugUtils.PrintDebug($"Emitting IL bodies for type: {TypeSymbol.ToDisplayString()}");

        while (true)
        {
            var pending = _methodGenerators.Values
                .Where(static generator => !generator.HasEmittedBody)
                .ToList();

            if (pending.Count == 0)
                break;

            foreach (var methodGenerator in pending)
            {
                CodeGen.CurrentEmittingMethod = methodGenerator.MethodSymbol;
                try
                {
                    DebugUtils.PrintDebug($"Emitting IL body for method: {methodGenerator.MethodSymbol.ToDisplayString()}");

                    methodGenerator.EmitBody();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to emit method '{methodGenerator.MethodSymbol.ToDisplayString()}'", ex);
                }
            }
        }
    }

    public Type CreateType()
    {
        foreach (var closure in _lambdaClosures.Values)
            closure.CreateType();

        foreach (var closure in _methodClosures.Values)
            closure.CreateType();

        _extensionMarkerTypeBuilder?.CreateType();
        _extensionGroupingTypeBuilder?.CreateType();
        Type ??= TypeBuilder!.CreateType();
        ReleaseInheritedGenericParameters();
        return Type!;
    }

    internal void ApplyExtensionMarkerNameAttribute(IMethodSymbol methodSymbol, Action<CustomAttributeBuilder> apply)
    {
        if (!ShouldApplyExtensionMarkerName(methodSymbol))
            return;

        if (_extensionMarkerName is null)
            return;

        var builder = CodeGen.CreateExtensionMarkerNameAttribute(_extensionMarkerName);
        if (builder is not null)
            apply(builder);
    }

    internal void ApplyExtensionMarkerNameAttribute(IPropertySymbol propertySymbol, Action<CustomAttributeBuilder> apply)
    {
        if (!ShouldApplyExtensionMarkerName(propertySymbol))
            return;

        if (_extensionMarkerName is null)
            return;

        var builder = CodeGen.CreateExtensionMarkerNameAttribute(_extensionMarkerName);
        if (builder is not null)
            apply(builder);
    }

    private bool ShouldApplyExtensionMarkerName(IMethodSymbol methodSymbol)
    {
        if (TypeSymbol is not SourceNamedTypeSymbol sourceType || !sourceType.IsExtensionDeclaration)
            return false;

        if (_extensionMarkerName is null)
            return false;

        return methodSymbol.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor);
    }

    private bool ShouldApplyExtensionMarkerName(IPropertySymbol propertySymbol)
    {
        if (TypeSymbol is not SourceNamedTypeSymbol sourceType || !sourceType.IsExtensionDeclaration)
            return false;

        return _extensionMarkerName is not null;
    }

    private void DefineExtensionContainerTypeBuilder(INamedTypeSymbol named)
    {
        TypeAttributes typeAttributes = GetTypeAccessibilityAttributes(named);

        if (named.IsAbstract)
            typeAttributes |= TypeAttributes.Abstract;

        if (named.IsClosed)
            typeAttributes |= TypeAttributes.Sealed;

        TypeBuilder? containingTypeBuilder = null;
        if (named.ContainingType is INamedTypeSymbol containingType)
        {
            var containingGenerator = CodeGen.GetOrCreateTypeGenerator(containingType);
            if (containingGenerator.TypeBuilder is null)
                containingGenerator.DefineTypeBuilder();

            containingTypeBuilder = containingGenerator.TypeBuilder;
        }

        var baseClrType = named.BaseType is not null
            ? ResolveClrType(named.BaseType)
            : null;

        if (containingTypeBuilder is not null)
        {
            var nestedName = named.Name;
            TypeBuilder = containingTypeBuilder.DefineNestedType(
                nestedName,
                typeAttributes,
                baseClrType);
        }
        else
        {
            var metadataName = named.ContainingNamespace?.QualifyName(named.Name) ?? named.Name;
            TypeBuilder = CodeGen.ModuleBuilder.DefineType(
                metadataName,
                typeAttributes,
                baseClrType);
        }

        ApplyTypeCustomAttributes();
        ApplyCompilerGeneratedAttributeIfClosureFrame();

        var extensionAttribute = CodeGen.CreateExtensionAttributeBuilder();
        if (extensionAttribute is not null)
            TypeBuilder!.SetCustomAttribute(extensionAttribute);

        EnsureExtensionGroupingType();
    }

    private void ApplyTypeCustomAttributes()
    {
        CodeGen.ApplyCustomAttributes(TypeSymbol.GetAttributes(), attribute => TypeBuilder!.SetCustomAttribute(attribute));
        CodeGen.ApplyNullableContextAttribute(TypeBuilder!.SetCustomAttribute);
    }

    private void EnsureExtensionGroupingType()
    {
        if (_extensionGroupingTypeBuilder is not null)
            return;

        if (TypeBuilder is null)
            return;

        if (TypeSymbol is not SourceNamedTypeSymbol sourceType || !sourceType.IsExtensionDeclaration)
            return;

        var receiverType = sourceType.ExtensionReceiverType;
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return;

        var groupingTypeName = GetExtensionGroupingTypeName(receiverType);
        _extensionGroupingTypeBuilder = TypeBuilder.DefineNestedType(
            groupingTypeName,
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.SpecialName);

        DefineExtensionGroupingTypeParameters();

        var extensionAttribute = CodeGen.CreateExtensionAttributeBuilder();
        if (extensionAttribute is not null)
            _extensionGroupingTypeBuilder.SetCustomAttribute(extensionAttribute);

        DefineExtensionMarkerType(receiverType);
    }

    private void DefineExtensionGroupingTypeParameters()
    {
        if (_extensionGroupingTypeBuilder is null)
            return;

        var extensionTypeParameters = GetExtensionTypeParameters();
        if (extensionTypeParameters.IsDefaultOrEmpty)
            return;

        var names = new string[extensionTypeParameters.Length];
        for (int i = 0; i < names.Length; i++)
            names[i] = extensionTypeParameters[i].Name;

        _extensionGroupingTypeParameters = _extensionGroupingTypeBuilder.DefineGenericParameters(names);
    }

    private void DefineExtensionMarkerType(ITypeSymbol receiverType)
    {
        if (_extensionGroupingTypeBuilder is null)
            return;

        _extensionMarkerName = GetExtensionMarkerTypeName(receiverType);
        _extensionMarkerTypeBuilder = _extensionGroupingTypeBuilder.DefineNestedType(
            _extensionMarkerName,
            TypeAttributes.NestedPublic | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.SpecialName);

        var receiverClrType = ResolveExtensionGroupingType(receiverType);

        var markerMethod = _extensionMarkerTypeBuilder.DefineMethod(
            ExtensionMarkerMethodName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(void),
            new[] { receiverClrType });

        markerMethod.DefineParameter(1, ParameterAttributes.None, "self");

        var il = markerMethod.GetILGenerator();
        il.Emit(OpCodes.Ret);
    }

    private string GetExtensionGroupingTypeName(ITypeSymbol receiverType)
        => ExtensionGroupingTypePrefix + GetExtensionReceiverSuffix(receiverType);

    private string GetExtensionMarkerTypeName(ITypeSymbol receiverType)
        => ExtensionMarkerTypePrefix + $"{TypeSymbol.Name}_for_{GetExtensionReceiverSuffix(receiverType)}";

    private string GetExtensionReceiverSuffix(ITypeSymbol receiverType)
        => GetExtensionReceiverSuffix(receiverType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private string GetExtensionReceiverSuffix(ITypeSymbol receiverType, HashSet<ITypeSymbol> visiting)
    {
        if (!visiting.Add(receiverType))
            return receiverType.Name;

        try
        {
            switch (receiverType)
            {
                case INamedTypeSymbol named:
                    {
                        var baseName = named.Name;
                        var typeArguments = named is ConstructedNamedTypeSymbol constructed
                            ? constructed.GetExplicitTypeArgumentsForInference()
                            : named.TypeArguments;

                        if (typeArguments.IsDefaultOrEmpty)
                            return baseName;

                        var args = new string[typeArguments.Length];
                        for (int i = 0; i < typeArguments.Length; i++)
                            args[i] = GetExtensionReceiverSuffix(typeArguments[i], visiting);

                        return $"{baseName}_{string.Join("_", args)}";
                    }
                case IArrayTypeSymbol arrayType:
                    return $"{GetExtensionReceiverSuffix(arrayType.ElementType, visiting)}_Array";
                case ITypeParameterSymbol typeParameter:
                    return typeParameter.Name;
                default:
                    return receiverType.Name;
            }
        }
        finally
        {
            visiting.Remove(receiverType);
        }
    }

    private Type ResolveExtensionGroupingType(ITypeSymbol typeSymbol)
    {
        var typeParameters = GetExtensionTypeParameters();
        var registered = false;
        if (!typeParameters.IsDefaultOrEmpty && _extensionGroupingTypeParameters is not null)
        {
            CodeGen.RegisterGenericParameters(typeParameters, _extensionGroupingTypeParameters);
            registered = true;
        }

        try
        {
            return ResolveClrType(typeSymbol);
        }
        finally
        {
            if (registered)
                CodeGen.UnregisterGenericParameters(typeParameters);
        }
    }

    internal ImmutableArray<ITypeParameterSymbol> GetExtensionTypeParameters()
        => TypeSymbol is SourceNamedTypeSymbol sourceType && sourceType.IsExtensionDeclaration
            ? sourceType.TypeParameters
            : ImmutableArray<ITypeParameterSymbol>.Empty;

    private void DefineExtensionGroupingMembers()
    {
        if (_extensionGroupingTypeBuilder is null || _extensionMarkerName is null)
            return;

        var markerAttribute = CodeGen.CreateExtensionMarkerNameAttribute(_extensionMarkerName);
        if (markerAttribute is null)
            return;

        var extensionTypeParameters = GetExtensionTypeParameters();
        var registered = false;
        if (!extensionTypeParameters.IsDefaultOrEmpty && _extensionGroupingTypeParameters is not null)
        {
            CodeGen.RegisterGenericParameters(extensionTypeParameters, _extensionGroupingTypeParameters);
            registered = true;
        }

        try
        {
            var emitted = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var memberSymbol in TypeSymbol.GetMembers())
            {
                if (memberSymbol.ContainingType is { } containingType &&
                    !SymbolEqualityComparer.Default.Equals(containingType, TypeSymbol))
                {
                    continue;
                }

                switch (memberSymbol)
                {
                    case IMethodSymbol methodSymbol when methodSymbol.MethodKind is not (MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly or MethodKind.EventAdd or MethodKind.EventRemove):
                        {
                            // Only emit extension methods into the grouping type (as skeleton methods).
                            if (methodSymbol.IsExtensionMethod && emitted.Add(methodSymbol))
                            {
                                DefineExtensionSkeletonMethod(methodSymbol, markerAttribute);
                            }
                            break;
                        }
                    case IPropertySymbol propertySymbol:
                        {
                            // C#-compatible: do not emit a CLR Property for extension properties.
                            // Only emit their accessor methods into the grouping type.
                            if (propertySymbol.IsExtensionProperty)
                            {
                                if (propertySymbol.GetMethod is IMethodSymbol getMethod && emitted.Add(getMethod))
                                    DefineExtensionSkeletonMethod(getMethod, markerAttribute);

                                if (propertySymbol.SetMethod is IMethodSymbol setMethod && emitted.Add(setMethod))
                                    DefineExtensionSkeletonMethod(setMethod, markerAttribute);

                                // Only emit accessor skeletons, not a CLR property.
                                break;
                            }

                            // Non-extension properties (if any appear here) can keep existing behavior,
                            // but extension grouping is generally only for extension members.
                            break;
                        }
                        // Other member kinds ignored for extension grouping.
                }
            }
        }
        finally
        {
            if (registered)
                CodeGen.UnregisterGenericParameters(extensionTypeParameters);
        }
    }

    private void DefineExtensionSkeletonMethod(IMethodSymbol methodSymbol, CustomAttributeBuilder markerAttribute)
    {
        if (_extensionGroupingTypeBuilder is null)
            return;

        if (methodSymbol.MethodKind is MethodKind.Constructor or MethodKind.LambdaMethod)
            return;
        var emittedMethodName = GetEmittedMethodName(methodSymbol);
        if (string.IsNullOrWhiteSpace(emittedMethodName))
            return;

        var isExtensionInstance = methodSymbol.IsExtensionMethod;
        var isStatic = methodSymbol.IsStatic && !isExtensionInstance;
        var parameters = methodSymbol.Parameters;

        var methodTypeParameters = methodSymbol.TypeParameters;
        var extensionTypeParameterCount = GetExtensionTypeParameters().Length;
        var extensionMethodTypeParameters = extensionTypeParameterCount > 0 && methodTypeParameters.Length >= extensionTypeParameterCount
            ? methodTypeParameters.Take(extensionTypeParameterCount).ToImmutableArray()
            : ImmutableArray<ITypeParameterSymbol>.Empty;
        var methodSpecificTypeParameters = methodTypeParameters.Length > extensionTypeParameterCount
            ? methodTypeParameters.Skip(extensionTypeParameterCount).ToImmutableArray()
            : ImmutableArray<ITypeParameterSymbol>.Empty;

        var parameterSymbols = isExtensionInstance && parameters.Length > 0
            ? parameters.Skip(1).ToArray()
            : parameters.ToArray();

        var attributes = MethodAttributes.Public | MethodAttributes.HideBySig;

        if (isStatic)
            attributes |= MethodAttributes.Static;

        if (methodSymbol.MethodKind is MethodKind.Conversion or MethodKind.UserDefinedOperator or MethodKind.PropertyGet or MethodKind.PropertySet)
            attributes |= MethodAttributes.SpecialName;

        var methodBuilder = _extensionGroupingTypeBuilder.DefineMethod(
            emittedMethodName,
            attributes,
            CallingConventions.Standard);

        GenericTypeParameterBuilder[]? methodGenericBuilders = null;
        if (!methodSpecificTypeParameters.IsDefaultOrEmpty)
            methodGenericBuilders = methodBuilder.DefineGenericParameters(methodSpecificTypeParameters.Select(tp => tp.Name).ToArray());

        var registeredExtensionParameters = false;
        var registeredMethodParameters = false;

        try
        {
            if (!extensionMethodTypeParameters.IsDefaultOrEmpty &&
                _extensionGroupingTypeParameters is not null &&
                extensionMethodTypeParameters.Length == _extensionGroupingTypeParameters.Length)
            {
                CodeGen.RegisterGenericParameters(extensionMethodTypeParameters, _extensionGroupingTypeParameters);
                registeredExtensionParameters = true;
            }

            if (!methodSpecificTypeParameters.IsDefaultOrEmpty && methodGenericBuilders is not null)
            {
                CodeGen.RegisterGenericParameters(methodSpecificTypeParameters, methodGenericBuilders);
                registeredMethodParameters = true;
            }

            var parameterTypes = parameterSymbols
                .Select(p => ResolveClrType(p.Type))
                .Select((type, index) =>
                {
                    var parameter = parameterSymbols[index];
                    return parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter
                        ? type.MakeByRefType()
                        : type;
                })
                .ToArray();

            var returnType = methodSymbol.ReturnType.SpecialType == SpecialType.System_Unit
                ? typeof(void)
                : ResolveClrType(methodSymbol.ReturnType);

            methodBuilder.SetSignature(returnType, null, null, parameterTypes, null, null);

            for (int i = 0; i < parameterSymbols.Length; i++)
            {
                var parameter = parameterSymbols[i];
                methodBuilder.DefineParameter(i + 1, ParameterAttributes.None, parameter.Name);
            }

            methodBuilder.SetCustomAttribute(markerAttribute);
            EmitSkeletonMethodBody(methodBuilder);
        }
        finally
        {
            if (registeredMethodParameters)
                CodeGen.UnregisterGenericParameters(methodSpecificTypeParameters);
            if (registeredExtensionParameters)
                CodeGen.UnregisterGenericParameters(extensionMethodTypeParameters);
        }
    }

    private static string GetEmittedMethodName(IMethodSymbol methodSymbol)
    {
        if (!string.IsNullOrWhiteSpace(methodSymbol.Name))
            return methodSymbol.Name;

        var declaration = methodSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .FirstOrDefault();

        if (methodSymbol.MethodKind == MethodKind.Conversion &&
            declaration is ConversionOperatorDeclarationSyntax conversionDecl &&
            OperatorFacts.TryGetConversionOperatorMetadataName(conversionDecl.ConversionKindKeyword.Kind, out var conversionMetadataName))
        {
            return conversionMetadataName;
        }

        if (methodSymbol.MethodKind == MethodKind.UserDefinedOperator &&
            declaration is OperatorDeclarationSyntax operatorDecl &&
            OperatorFacts.TryGetUserDefinedOperatorInfo(
                operatorDecl.OperatorToken.Kind,
                operatorDecl.ParameterList.Parameters.Count,
                out var operatorInfo))
        {
            return operatorInfo.MetadataName;
        }

        return methodSymbol.MetadataName;
    }

    private MethodBuilder DefineExtensionSkeletonAccessor(
        IMethodSymbol accessorSymbol,
        Type returnType,
        CustomAttributeBuilder markerAttribute)
    {
        if (_extensionGroupingTypeBuilder is null)
            throw new InvalidOperationException("Grouping type builder is not available.");

        var isExtensionInstance = accessorSymbol.IsExtensionMethod;
        var isStatic = accessorSymbol.IsStatic && !isExtensionInstance;

        var parameters = accessorSymbol.Parameters;
        var parameterSymbols = isExtensionInstance && parameters.Length > 0
            ? parameters.Skip(1).ToArray()
            : parameters.ToArray();

        var parameterTypes = parameterSymbols
            .Select(p => ResolveClrType(p.Type))
            .Select((type, index) =>
            {
                var parameter = parameterSymbols[index];
                return parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter
                    ? type.MakeByRefType()
                    : type;
            })
            .ToArray();

        var attributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

        if (isStatic)
            attributes |= MethodAttributes.Static;

        // Add required return-type modifier for init-only setters
        var isInitOnly = accessorSymbol.MethodKind == MethodKind.InitOnly;
        var requiredReturnMods = isInitOnly
            ? new[] { typeof(System.Runtime.CompilerServices.IsExternalInit) }
            : null;

        var accessorBuilder = _extensionGroupingTypeBuilder.DefineMethod(
            accessorSymbol.Name,
            attributes,
            CallingConventions.Standard,
            returnType,
            requiredReturnMods,
            null,
            parameterTypes,
            null,
            null);

        for (int i = 0; i < parameterSymbols.Length; i++)
        {
            var parameter = parameterSymbols[i];
            accessorBuilder.DefineParameter(i + 1, ParameterAttributes.None, parameter.Name);
        }

        accessorBuilder.SetCustomAttribute(markerAttribute);
        EmitSkeletonMethodBody(accessorBuilder);
        return accessorBuilder;
    }

    private static void EmitSkeletonMethodBody(MethodBuilder methodBuilder)
    {
        var ctor = typeof(NotImplementedException).GetConstructor(Type.EmptyTypes);
        var il = methodBuilder.GetILGenerator();
        if (ctor is not null)
            il.Emit(OpCodes.Newobj, ctor);
        il.Emit(OpCodes.Throw);
    }
    private void ReleaseInheritedGenericParameters()
    {
        if (_releasedInheritedTypeParameters)
            return;

        if (!_inheritedTypeParameters.IsDefaultOrEmpty)
            CodeGen.UnregisterGenericParameters(_inheritedTypeParameters);

        _releasedInheritedTypeParameters = true;
    }

    public bool HasMethodGenerator(IMethodSymbol methodSymbol)
    {
        return _methodGenerators.ContainsKey(methodSymbol);
    }

    public MethodGenerator? GetMethodGenerator(IMethodSymbol methodSymbol)
    {
        return _methodGenerators.TryGetValue(methodSymbol, out var generator)
            ? generator
            : null;
    }

    public void Add(IMethodSymbol methodSymbol, MethodGenerator methodGenerator)
    {
        _methodGenerators[methodSymbol] = methodGenerator;
    }

    public bool TryGetLambdaClosure(ILambdaSymbol lambdaSymbol, out LambdaClosure closure)
    {
        return _lambdaClosures.TryGetValue(lambdaSymbol, out closure);
    }

    public bool TryGetMethodClosure(IMethodSymbol methodSymbol, out LambdaClosure closure)
    {
        if (_methodClosures.TryGetValue(methodSymbol, out closure))
            return true;

        if (methodSymbol.UnderlyingSymbol is IMethodSymbol underlyingMethod &&
            !ReferenceEquals(underlyingMethod, methodSymbol))
        {
            return _methodClosures.TryGetValue(underlyingMethod, out closure);
        }

        closure = null!;
        return false;
    }

    internal LambdaClosure EnsureLambdaClosure(SourceLambdaSymbol lambdaSymbol)
    {
        if (lambdaSymbol.ClosureFrameType is null)
            lambdaSymbol.SetClosureFrameType(ClosureFrameSymbolFactory.Create(lambdaSymbol));

        if (TypeSymbol is SynthesizedAsyncStateMachineTypeSymbol asyncStateMachine &&
            asyncStateMachine.AsyncMethod.ContainingType is { } containingType &&
            !SymbolEqualityComparer.Default.Equals(containingType, TypeSymbol))
        {
            var hostGenerator = CodeGen.GetOrCreateTypeGenerator(containingType);
            if (!ReferenceEquals(hostGenerator, this))
                return hostGenerator.EnsureLambdaClosure(lambdaSymbol);
        }

        if (TypeSymbol is SynthesizedAsyncStateMachineTypeSymbol { AsyncMethod.ContainingSymbol: INamedTypeSymbol hostType } &&
            !SymbolEqualityComparer.Default.Equals(hostType, TypeSymbol))
        {
            var hostGenerator = CodeGen.GetOrCreateTypeGenerator(hostType);
            if (!ReferenceEquals(hostGenerator, this))
                return hostGenerator.EnsureLambdaClosure(lambdaSymbol);
        }

        // Capture analysis also visits nested lambdas. Remove locals declared by this lambda
        // itself; they belong to a nested closure and must not become uninitialized fields on
        // the enclosing lambda's closure.
        var captures = lambdaSymbol.CapturedVariables
            .Where(captured => !IsDeclaredWithinLambda(captured, lambdaSymbol))
            .ToImmutableArray();

        // Compiler-generated iteration locals are scoped to the expression that creates the
        // lambda. A nested lambda must therefore receive a fresh closure for each evaluation,
        // rather than sharing the closure of an enclosing lambda across every iteration.
        if (captures.Any(IsCompilerGeneratedLocal))
        {
            if (_lambdaClosures.TryGetValue(lambdaSymbol, out var iterationClosure))
            {
                iterationClosure.EnsureFields(captures);
                return iterationClosure;
            }

            iterationClosure = CreateClosure(lambdaSymbol, captures);
            _lambdaClosures[lambdaSymbol] = iterationClosure;
            return iterationClosure;
        }

        // C#-style: the display class is keyed by the *declaring scope* of the captured locals.
        // If the captured variables are declared in a method, all lambdas/local functions in that
        // method share one closure.
        var owner = GetCaptureOwnerSymbol(captures);

        if (owner is IMethodSymbol ownerMethod &&
            (ownerMethod as SourceMethodSymbol ?? ownerMethod.UnderlyingSymbol as SourceMethodSymbol) is { } ownerSourceMethod)
        {
            if (ownerSourceMethod.ContainingType is { } ownerContainingType &&
                !SymbolEqualityComparer.Default.Equals(ownerContainingType, TypeSymbol))
            {
                var ownerGenerator = CodeGen.GetOrCreateTypeGenerator(ownerContainingType);
                if (!ReferenceEquals(ownerGenerator, this))
                    return ownerGenerator.EnsureLambdaClosure(lambdaSymbol);
            }

            var methodClosure = EnsureMethodClosure(ownerSourceMethod, captures);
            // Map this lambda to the method closure so future lookups are fast and consistent.
            _lambdaClosures[lambdaSymbol] = methodClosure;
            return methodClosure;
        }

        if (owner is ILambdaSymbol ownerLambda && ownerLambda is SourceLambdaSymbol ownerSourceLambda)
        {
            if (SymbolEqualityComparer.Default.Equals(ownerSourceLambda, lambdaSymbol))
            {
                if (_lambdaClosures.TryGetValue(lambdaSymbol, out var selfExisting))
                {
                    selfExisting.EnsureFields(captures);
                    return selfExisting;
                }

                var selfClosure = CreateClosure(lambdaSymbol, captures);
                _lambdaClosures[lambdaSymbol] = selfClosure;
                return selfClosure;
            }

            // Nested lambdas capturing locals declared in an outer lambda: one closure per outer-lambda scope.
            var lambdaClosure = EnsureLambdaClosure(ownerSourceLambda);
            lambdaClosure.EnsureFields(captures);
            _lambdaClosures[lambdaSymbol] = lambdaClosure;
            return lambdaClosure;
        }

        if (_lambdaClosures.TryGetValue(lambdaSymbol, out var existing))
        {
            existing.EnsureFields(captures);
            return existing;
        }

        var closure = CreateClosure(lambdaSymbol, captures);
        _lambdaClosures[lambdaSymbol] = closure;
        return closure;
    }

    internal LambdaClosure EnsureMethodClosure(SourceMethodSymbol methodSymbol, ImmutableArray<ISymbol>? capturedVariables2 = null)
    {
        if (methodSymbol.ClosureFrameType is null)
            methodSymbol.SetClosureFrameType(ClosureFrameSymbolFactory.Create(methodSymbol));

        var capturedVariables = capturedVariables2 ?? methodSymbol.CapturedVariables;
        var owner = GetCaptureOwnerSymbol(capturedVariables);

        if (owner is IMethodSymbol ownerMethod &&
            (ownerMethod as SourceMethodSymbol ?? ownerMethod.UnderlyingSymbol as SourceMethodSymbol) is { } ownerSourceMethod &&
            !SymbolEqualityComparer.Default.Equals(ownerSourceMethod, methodSymbol))
        {
            if (ownerSourceMethod.ContainingType is { } ownerContainingType &&
                !SymbolEqualityComparer.Default.Equals(ownerContainingType, TypeSymbol))
            {
                var ownerGenerator = CodeGen.GetOrCreateTypeGenerator(ownerContainingType);
                if (!ReferenceEquals(ownerGenerator, this))
                    return ownerGenerator.EnsureMethodClosure(ownerSourceMethod, capturedVariables);
            }

            var ownerClosure = EnsureMethodClosure(ownerSourceMethod, capturedVariables);
            _methodClosures[methodSymbol] = ownerClosure;
            return ownerClosure;
        }

        if (_methodClosures.TryGetValue(methodSymbol, out var existing))
        {
            existing.EnsureFields(capturedVariables);
            return existing;
        }

        if (methodSymbol.UnderlyingSymbol is IMethodSymbol underlying &&
            !ReferenceEquals(underlying, methodSymbol) &&
            _methodClosures.TryGetValue(underlying, out var existingUnderlying))
        {
            existingUnderlying.EnsureFields(capturedVariables);
            // Cache under the wrapper symbol too, to avoid future misses.
            _methodClosures[methodSymbol] = existingUnderlying;
            return existingUnderlying;
        }

        var closure = CreateClosure(methodSymbol, capturedVariables);
        _methodClosures[methodSymbol] = closure;
        return closure;
    }

    /// <summary>
    /// Creates a single shared closure for an outer method that contains lambdas and/or local
    /// functions capturing outer locals. Registers the closure for every supplied lambda symbol
    /// so that <see cref="EnsureLambdaClosure"/> returns the shared instance instead of
    /// allocating a fresh per-lambda closure, and for every local function symbol so that
    /// <see cref="EnsureMethodClosure"/> returns the shared instance.
    /// </summary>
    internal LambdaClosure EnsureSharedMethodClosure(
        IMethodSymbol hostMethod,
        ImmutableArray<ISymbol> allCapturedSymbols,
        IReadOnlyList<ILambdaSymbol> lambdas,
        IReadOnlyList<SourceMethodSymbol>? localFunctions = null)
    {
        var closure = EnsureMethodClosure((SourceMethodSymbol)hostMethod, allCapturedSymbols);

        // Pre-register the shared closure for every lambda so that EnsureLambdaClosure
        // returns the same object instead of creating a separate per-lambda closure.
        foreach (var lambda in lambdas)
        {
            if (lambda is SourceLambdaSymbol source &&
                !source.CapturedVariables.Any(IsCompilerGeneratedLocal))
            {
                _lambdaClosures[source] = closure;
            }
        }

        // Pre-register for every local function so that EnsureMethodClosure returns the
        // shared closure rather than creating a separate per-function DisplayClass.
        if (localFunctions is not null)
        {
            foreach (var localFunc in localFunctions)
                _methodClosures[localFunc] = closure;
        }

        closure.EnsureFields(allCapturedSymbols);

        return closure;
    }

    public Type ResolveClrType(ITypeSymbol typeSymbol)
    {
        return TypeSymbolExtensionsForCodeGen.GetClrType(typeSymbol, CodeGen);
    }

    private Type ResolveCapturedSymbolType(ISymbol symbol)
    {
        return ResolveClrType(GetCapturedSymbolTypeSymbol(symbol));
    }

    private ITypeSymbol GetCapturedSymbolTypeSymbol(ISymbol symbol)
    {
        var typeSymbol = symbol switch
        {
            ILocalSymbol local when local.Type is not null => local.Type,
            IParameterSymbol parameter when parameter.Type is not null => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            ITypeSymbol type => type,
            _ => Compilation.ErrorTypeSymbol
        };

        return typeSymbol;
    }

    private static string CreateClosureFieldName(ISymbol symbol, int ordinal)
    {
        return symbol switch
        {
            ILocalSymbol local => $"<{local.Name}>__{ordinal}",
            IParameterSymbol parameter => $"<{parameter.Name}>__{ordinal}",
            ITypeSymbol => $"<>self__{ordinal}",
            _ => $"<>capture__{ordinal}"
        };
    }

    private static ISymbol? GetCaptureOwnerSymbol(ImmutableArray<ISymbol> capturedVariables)
    {
        if (capturedVariables.IsDefaultOrEmpty)
            return null;

        foreach (var captured in capturedVariables)
        {
            if (captured is null)
                continue;

            if (captured.ContainingSymbol is { } containing)
                return containing;
        }

        return null;
    }

    private static bool IsDeclaredWithinLambda(ISymbol captured, SourceLambdaSymbol lambda)
    {
        if (SymbolEqualityComparer.Default.Equals(captured.ContainingSymbol, lambda))
            return true;

        foreach (var capturedReference in captured.DeclaringSyntaxReferences)
        {
            foreach (var lambdaReference in lambda.DeclaringSyntaxReferences)
            {
                if (capturedReference.SyntaxTree == lambdaReference.SyntaxTree &&
                    lambdaReference.Span.Contains(capturedReference.Span))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCompilerGeneratedLocal(ISymbol symbol)
    {
        return symbol is ILocalSymbol { IsImplicitlyDeclared: true };
    }

    private ImmutableArray<ISymbol> GetCapturedVariablesForMethod(SourceMethodSymbol methodSymbol)
    {
        var capturedVariables = methodSymbol.CapturedVariables;
        if (!capturedVariables.IsDefaultOrEmpty)
            return capturedVariables;

        if (methodSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not FunctionStatementSyntax functionSyntax)
            return ImmutableArray<ISymbol>.Empty;

        var semanticModel = Compilation.GetSemanticModel(functionSyntax.SyntaxTree);
        return semanticModel.GetCapturedVariables(functionSyntax);
    }

    private FieldBuilder DefineClosureField(TypeBuilder closureBuilder, ISymbol captured, int ordinal)
    {
        var capturedTypeSymbol = GetCapturedSymbolTypeSymbol(captured);
        var fieldType = ResolveClrType(capturedTypeSymbol);
        var fieldName = CreateClosureFieldName(captured, ordinal);
        var fieldBuilder = closureBuilder.DefineField(fieldName, fieldType, FieldAttributes.Public);

        var tupleAttr = CodeGen.CreateTupleElementNamesAttribute(capturedTypeSymbol);
        if (tupleAttr is not null)
            fieldBuilder.SetCustomAttribute(tupleAttr);

        return fieldBuilder;
    }

    private LambdaClosure CreateClosure(IMethodSymbol owner, ImmutableArray<ISymbol> capturedVariables)
    {
        if (TypeBuilder is null)
            throw new InvalidOperationException("Type builder must be defined before creating a lambda closure.");

        var closureSymbol = owner switch
        {
            SourceLambdaSymbol { ClosureFrameType: { } lambdaClosureFrame } => lambdaClosureFrame,
            SourceMethodSymbol { ClosureFrameType: { } methodClosureFrame } => methodClosureFrame,
            _ => null
        };

        var createdSymbol = false;
        if (closureSymbol is null)
        {
            var closureName = $"<>c__DisplayClass{_lambdaClosureOrdinal++}";
            var baseType = Compilation.GetSpecialType(SpecialType.System_Object);
            closureSymbol = new SourceNamedTypeSymbol(
                closureName,
                baseType,
                TypeKind.Class,
                owner.ContainingSymbol,
                containingType: TypeSymbol as INamedTypeSymbol,
                containingNamespace: TypeSymbol.ContainingNamespace,
                locations: owner.Locations.ToArray(),
                declaringSyntaxReferences: owner.DeclaringSyntaxReferences.ToArray(),
                isSealed: true,
                declaredAccessibility: Accessibility.Private);
            createdSymbol = true;
        }

        var aliasTypeParameters = ImmutableArray<ITypeParameterSymbol>.Empty;
        if (createdSymbol && !owner.TypeParameters.IsDefaultOrEmpty)
        {
            // The closure type must own its own type parameters. Reusing the outer
            // method's symbols preserves the wrong owner identity and can leak
            // method-scoped generic slots (`!!`) into display-class methods.
            aliasTypeParameters = owner.TypeParameters;
            closureSymbol.SetTypeParameters(CloneClosureTypeParameters(owner, closureSymbol));
        }
        else if (!owner.TypeParameters.IsDefaultOrEmpty)
        {
            aliasTypeParameters = owner.TypeParameters;
        }

        var closureGenerator = CodeGen.GetOrCreateTypeGenerator(closureSymbol);
        if (closureGenerator.TypeBuilder is null)
            closureGenerator.DefineTypeBuilder();

        var closureBuilder = closureGenerator.TypeBuilder
            ?? throw new InvalidOperationException("Failed to define closure type builder.");

        var ctor = closureBuilder.DefineDefaultConstructor(MethodAttributes.Public);

        var fields = new Dictionary<ISymbol, FieldBuilder>(SymbolEqualityComparer.Default);
        var capturedSymbols = capturedVariables
            .Where(static symbol => symbol is not null)
            .Distinct(SymbolEqualityComparer.Default)
            .ToImmutableArray();
        var index = 0;
        foreach (var captured in capturedSymbols)
        {
            var fieldBuilder = DefineClosureField(closureBuilder, captured, index++);
            fields[captured] = fieldBuilder;
        }

        return new LambdaClosure(closureSymbol, this, closureBuilder, ctor, fields, capturedSymbols, aliasTypeParameters);
    }

    private static ImmutableArray<ITypeParameterSymbol> CloneClosureTypeParameters(
        IMethodSymbol owner,
        SourceNamedTypeSymbol closureSymbol)
    {
        if (owner.TypeParameters.IsDefaultOrEmpty)
            return ImmutableArray<ITypeParameterSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(owner.TypeParameters.Length);
        foreach (var typeParameter in owner.TypeParameters)
        {
            var clone = new SourceTypeParameterSymbol(
                typeParameter.Name,
                closureSymbol,
                closureSymbol,
                closureSymbol.ContainingNamespace,
                typeParameter.Locations.ToArray(),
                typeParameter.DeclaringSyntaxReferences.ToArray(),
                typeParameter.Ordinal,
                typeParameter.ConstraintKind,
                typeParameter is SourceTypeParameterSymbol sourceTypeParameter
                    ? sourceTypeParameter.ConstraintTypeReferences
                    : ImmutableArray<SyntaxReference>.Empty,
                typeParameter.Variance);

            if (!typeParameter.ConstraintTypes.IsDefaultOrEmpty)
                clone.SetConstraintTypes(typeParameter.ConstraintTypes);

            builder.Add(clone);
        }

        return builder.ToImmutable();
    }

    internal sealed class LambdaClosure
    {
        private readonly TypeGenerator _typeGenerator;
        private readonly Dictionary<ISymbol, FieldBuilder> _fields;
        private ImmutableArray<ISymbol> _capturedSymbols;
        private int _nextFieldOrdinal;
        private Type? _createdType;

        public LambdaClosure(
            SourceNamedTypeSymbol symbol,
            TypeGenerator typeGenerator,
            TypeBuilder typeBuilder,
            ConstructorBuilder constructor,
            Dictionary<ISymbol, FieldBuilder> fields,
            ImmutableArray<ISymbol> capturedSymbols,
            ImmutableArray<ITypeParameterSymbol> aliasTypeParameters)
        {
            Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _typeGenerator = typeGenerator;
            TypeBuilder = typeBuilder;
            Constructor = constructor;
            _fields = fields;
            _capturedSymbols = capturedSymbols.IsDefault
                ? ImmutableArray<ISymbol>.Empty
                : capturedSymbols;
            AliasTypeParameters = aliasTypeParameters.IsDefault
                ? ImmutableArray<ITypeParameterSymbol>.Empty
                : aliasTypeParameters;
            RuntimeTypeSymbol = CreateRuntimeTypeSymbol(symbol, AliasTypeParameters);
            _nextFieldOrdinal = _fields.Count;
        }

        public SourceNamedTypeSymbol Symbol { get; }
        public ImmutableArray<ITypeParameterSymbol> AliasTypeParameters { get; }
        public INamedTypeSymbol RuntimeTypeSymbol { get; }
        public TypeBuilder TypeBuilder { get; }

        public ConstructorBuilder Constructor { get; }

        public ImmutableArray<ISymbol> CapturedSymbols => _capturedSymbols;

        private static INamedTypeSymbol CreateRuntimeTypeSymbol(
            SourceNamedTypeSymbol symbol,
            ImmutableArray<ITypeParameterSymbol> aliasTypeParameters)
        {
            var selfTypeArguments = aliasTypeParameters.IsDefaultOrEmpty
                ? ImmutableArray<ITypeSymbol>.Empty
                : aliasTypeParameters.Select(static parameter => (ITypeSymbol)parameter).ToImmutableArray();

            if (aliasTypeParameters.IsDefaultOrEmpty &&
                symbol.ContainingType is INamedTypeSymbol containingType &&
                !IsExtensionDeclaration(containingType))
            {
                var containingTypeArguments = containingType.TypeParameters
                    .Where(static parameter => parameter.OwnerKind == TypeParameterOwnerKind.Type)
                    .Select(static parameter => (ITypeSymbol)parameter)
                    .ToImmutableArray();

                if (!containingTypeArguments.IsDefaultOrEmpty)
                {
                    var containingRuntimeType = new ConstructedNamedTypeSymbol(containingType, containingTypeArguments);

                    return ConstructedNamedTypeSymbol.ReanchorNested(
                        symbol,
                        containingRuntimeType,
                        inheritedSubstitution: null,
                        selfTypeArguments);
                }
            }

            return selfTypeArguments.IsDefaultOrEmpty
                ? symbol
                : new ConstructedNamedTypeSymbol(symbol, selfTypeArguments);
        }

        private static bool IsExtensionDeclaration(INamedTypeSymbol type)
        {
            if (type is SourceNamedTypeSymbol { IsExtensionDeclaration: true })
                return true;

            if (type is ConstructedNamedTypeSymbol constructed &&
                constructed.ConstructedFrom is SourceNamedTypeSymbol { IsExtensionDeclaration: true })
            {
                return true;
            }

            return false;
        }

        public bool TryGetField(ISymbol symbol, out FieldBuilder fieldBuilder) => _fields.TryGetValue(symbol, out fieldBuilder);

        public FieldBuilder GetField(ISymbol symbol) => _fields[symbol];

        public Type GetRuntimeType(CodeGenerator codeGen)
        {
            if (codeGen is null)
                throw new ArgumentNullException(nameof(codeGen));

            if (RuntimeTypeSymbol is ConstructedNamedTypeSymbol constructedRuntimeType)
                return constructedRuntimeType.GetTypeInfo(codeGen).AsType();

            return TypeSymbolExtensionsForCodeGen.GetClrTypeTreatingUnitAsVoidForMethodBody(RuntimeTypeSymbol, codeGen);
        }

        public void EnsureFields(ImmutableArray<ISymbol> capturedVariables)
        {
            if (capturedVariables.IsDefaultOrEmpty)
                return;

            if (_createdType is not null)
                throw new InvalidOperationException("Cannot add closure fields after the closure type has been created.");

            foreach (var symbol in capturedVariables)
            {
                if (symbol is null)
                    continue;

                if (_fields.ContainsKey(symbol))
                    continue;

                AddField(symbol);
            }
        }

        private void AddField(ISymbol captured)
        {
            // The TypeBuilder stored in this closure is the authoritative builder we can still mutate.
            // Field naming is ordinal-based to match CreateClosure's scheme.
            var ordinal = _nextFieldOrdinal++;
            var fieldBuilder = _typeGenerator.DefineClosureField(TypeBuilder, captured, ordinal);

            _fields[captured] = fieldBuilder;
            _capturedSymbols = _capturedSymbols.Add(captured);
        }

        public Type EnsureCreatedType()
        {
            CreateType();
            return _createdType ?? throw new InvalidOperationException("Lambda closure type was not created.");
        }

        public void CreateType()
        {
            if (_createdType is not null)
                return;

            _createdType = TypeBuilder.CreateType();
        }
    }

    internal bool ImplementsInterfaceMethod(IMethodSymbol methodSymbol)
    {
        if (TypeSymbol is not INamedTypeSymbol named || named.TypeKind == TypeKind.Interface)
            return false;

        if (TypeSymbol is SourceUnionSymbol &&
            methodSymbol.MethodKind == MethodKind.PropertyGet &&
            string.Equals(methodSymbol.Name, "get_Value", StringComparison.Ordinal))
        {
            return true;
        }

        var interfaces = GetAllInterfaces(named);
        if (interfaces.IsDefaultOrEmpty)
            return false;

        if (!methodSymbol.ExplicitInterfaceImplementations.IsDefaultOrEmpty)
        {
            foreach (var implemented in methodSymbol.ExplicitInterfaceImplementations)
            {
                if (implemented.ContainingType is INamedTypeSymbol containingInterface &&
                    interfaces.Contains(containingInterface, SymbolEqualityComparer.Default))
                    return true;
            }

            return false;
        }

        foreach (var interfaceType in interfaces)
        {
            foreach (var interfaceMethod in GetInterfaceContractMethods(interfaceType))
            {
                if (SignaturesMatch(methodSymbol, interfaceMethod))
                    return true;
            }
        }

        return false;
    }

    internal void CompleteInterfaceImplementations()
    {
        if (TypeSymbol is INamedTypeSymbol named && named.TypeKind != TypeKind.Interface)
        {
            ImplementInterfaceMembers(named);
            ImplementUnionInterfaceMember();
            ImplementVirtualOverrides(named);
        }
    }

    private void ImplementUnionInterfaceMember()
    {
        if (TypeBuilder is null || TypeSymbol is not SourceUnionSymbol union)
            return;

        var valueProperty = union
            .GetMembers("Value")
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static property => property.GetMethod is not null);

        if (valueProperty?.GetMethod is not { } valueGetter)
            return;

        if (!_methodGenerators.TryGetValue(valueGetter, out var valueGetterGenerator))
            return;

        if (valueGetterGenerator.MethodBase is not MethodBuilder valueGetterBuilder)
            return;

        var unionInterface = CodeGen.GetUnionInterfaceType();
        var interfaceGetter = unionInterface.GetMethod("get_Value", Type.EmptyTypes);
        if (interfaceGetter is null)
            return;

        TypeBuilder.DefineMethodOverride(valueGetterBuilder, interfaceGetter);
    }

    private void ImplementInterfaceMembers(INamedTypeSymbol named)
    {
        if (TypeBuilder is null)
            return;

        var interfaces = GetAllInterfaces(named);
        if (interfaces.IsDefaultOrEmpty)
            return;

        foreach (var interfaceType in interfaces)
        {
            foreach (var interfaceMethod in GetInterfaceContractMethods(interfaceType))
            {
                if (!TryFindImplementation(interfaceMethod, out var implementation))
                    continue;

                if (!_methodGenerators.TryGetValue(implementation, out var implementationGenerator))
                    continue;

                if (implementationGenerator.MethodBase is not MethodBuilder methodBuilder)
                    continue;

                if (!TryGetInterfaceMethodInfo(interfaceMethod, out var interfaceMethodInfo))
                    continue;

                TypeBuilder.DefineMethodOverride(methodBuilder, interfaceMethodInfo);
            }
        }
    }

    private static IEnumerable<IMethodSymbol> GetInterfaceContractMethods(INamedTypeSymbol interfaceType)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        bool Add(IMethodSymbol? method)
            => method is not null && seen.Add(method);

        foreach (var member in interfaceType.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method:
                    if (Add(method))
                        yield return method;
                    break;
                case IPropertySymbol property:
                    if (Add(property.GetMethod))
                        yield return property.GetMethod;
                    if (Add(property.SetMethod))
                        yield return property.SetMethod;
                    break;
                case IEventSymbol eventSymbol:
                    if (Add(eventSymbol.AddMethod))
                        yield return eventSymbol.AddMethod;
                    if (Add(eventSymbol.RemoveMethod))
                        yield return eventSymbol.RemoveMethod;
                    break;
            }
        }
    }

    private void ImplementVirtualOverrides(INamedTypeSymbol named)
    {
        if (TypeBuilder is null)
            return;

        foreach (var methodSymbol in named.GetMembers().OfType<IMethodSymbol>())
        {
            if (methodSymbol is not SourceMethodSymbol sourceMethod)
                continue;

            if (sourceMethod.OverriddenMethod is null)
                continue;

            if (!_methodGenerators.TryGetValue(sourceMethod, out var implementationGenerator))
                continue;

            if (implementationGenerator.MethodBase is not MethodBuilder methodBuilder)
                continue;

            if (!TryGetMethodInfo(sourceMethod.OverriddenMethod, out var baseMethodInfo))
                continue;

            // TypeBuilder.DefineMethodOverride should only be used for interface implementations.
            // Virtual overrides are emitted by setting the correct MethodAttributes when the
            // MethodBuilder is created, so there is no need to define an explicit override
            // mapping for base class methods. Skipping this avoids Reflection.Emit throwing
            // when the overridden method belongs to a base type rather than an interface.
            if (baseMethodInfo.DeclaringType?.IsInterface == true)
                TypeBuilder.DefineMethodOverride(methodBuilder, baseMethodInfo);
        }
    }

    private static ImmutableArray<INamedTypeSymbol> GetAllInterfaces(INamedTypeSymbol named)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        void AddInterface(INamedTypeSymbol interfaceType)
        {
            if (builder.Contains(interfaceType, SymbolEqualityComparer.Default))
                return;

            builder.Add(interfaceType);

            foreach (var inheritedInterface in interfaceType.Interfaces)
                AddInterface(inheritedInterface);
        }

        foreach (var interfaceType in named.Interfaces)
            AddInterface(interfaceType);

        if (!named.AllInterfaces.IsDefaultOrEmpty)
        {
            foreach (var interfaceType in named.AllInterfaces)
                AddInterface(interfaceType);
        }

        return builder.ToImmutable();
    }

    private bool TryFindImplementation(IMethodSymbol interfaceMethod, out IMethodSymbol implementation)
    {
        foreach (var candidate in GetImplementationMethodCandidates())
        {
            if (candidate.ExplicitInterfaceImplementations.IsDefaultOrEmpty)
                continue;

            if (ExplicitInterfaceImplementationsContain(candidate, interfaceMethod))
            {
                implementation = candidate;
                return true;
            }

        }

        foreach (var candidate in TypeSymbol.GetMembers(interfaceMethod.Name).OfType<IMethodSymbol>())
        {
            if (SignaturesMatch(candidate, interfaceMethod))
            {
                implementation = candidate;
                return true;
            }
        }

        implementation = null!;
        return false;

        IEnumerable<IMethodSymbol> GetImplementationMethodCandidates()
        {
            foreach (var member in TypeSymbol.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method:
                        yield return method;
                        break;
                    case IPropertySymbol property:
                        if (property.GetMethod is not null)
                            yield return property.GetMethod;
                        if (property.SetMethod is not null)
                            yield return property.SetMethod;
                        break;
                    case IEventSymbol eventSymbol:
                        if (eventSymbol.AddMethod is not null)
                            yield return eventSymbol.AddMethod;
                        if (eventSymbol.RemoveMethod is not null)
                            yield return eventSymbol.RemoveMethod;
                        break;
                }
            }
        }
    }

    private static bool ExplicitInterfaceImplementationsContain(IMethodSymbol candidate, IMethodSymbol interfaceMethod)
    {
        foreach (var implemented in candidate.ExplicitInterfaceImplementations)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented, interfaceMethod))
                return true;

            var implementedDefinition = implemented.OriginalDefinition ?? implemented;
            var interfaceDefinition = interfaceMethod.OriginalDefinition ?? interfaceMethod;
            if (SymbolEqualityComparer.Default.Equals(implementedDefinition, interfaceDefinition))
                return true;
        }

        return false;
    }

    private bool TryGetInterfaceMethodInfo(IMethodSymbol interfaceMethod, out MethodInfo methodInfo)
    {
        var definitionMethod = interfaceMethod;
        if (interfaceMethod is SubstitutedMethodSymbol sub)
            definitionMethod = sub.OriginalDefinition;

        if (!TryGetInterfaceDefinitionMethodInfo(definitionMethod, out var definitionMethodInfo))
        {
            methodInfo = null!;
            return false;
        }

        // If the interface method is constructed, we MUST target the constructed method.
        if (!SymbolEqualityComparer.Default.Equals(interfaceMethod, definitionMethod) &&
            interfaceMethod.ContainingType is not null)
        {
            var constructedIfaceClr = ResolveClrType(interfaceMethod.ContainingType);

            // TypeBuilderInstantiation (emitted generic types)
            if (constructedIfaceClr is TypeBuilder || constructedIfaceClr.GetType().Name.Contains("TypeBuilderInstantiation"))
            {
                methodInfo = TypeBuilder.GetMethod(constructedIfaceClr, definitionMethodInfo);
                return true;
            }

            // Normal runtime constructed generic types (e.g. typeof(IEnumerable<int>))
            if (constructedIfaceClr.IsGenericType)
            {
                var parms = definitionMethod.Parameters.Select(p => GetParameterClrType(p)).ToArray();
                var flags = BindingFlags.Public | BindingFlags.NonPublic |
                            (definitionMethod.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

                var m = constructedIfaceClr.GetMethod(definitionMethodInfo.Name, flags, null, parms, null);
                if (m is null)
                {
                    methodInfo = null!;
                    return false;
                }

                methodInfo = m;
                return true;
            }
        }

        // Non-constructed case: definition MethodInfo is fine.
        methodInfo = definitionMethodInfo;
        return true;
    }

    private bool TryGetInterfaceDefinitionMethodInfo(IMethodSymbol definitionMethod, out MethodInfo methodInfo)
    {
        // Source-defined interfaces: we should already have MethodBuilder registered.
        if ((definitionMethod as SourceSymbol ?? definitionMethod.UnderlyingSymbol as SourceSymbol) is { } src)
        {
            if (CodeGen.TryGetMemberBuilder(src, out var existing) &&
                existing is MethodInfo existingMethod)
            {
                methodInfo = existingMethod;
                return true;
            }

            if (definitionMethod.ContainingType is not null)
            {
                var interfaceGenerator = CodeGen.GetOrCreateTypeGenerator(definitionMethod.ContainingType);
                if (interfaceGenerator.TypeBuilder is null)
                    interfaceGenerator.DefineTypeBuilder();

                interfaceGenerator.DefineMemberBuilders();
            }

            if (CodeGen.TryGetMemberBuilder(src, out var ensured) &&
                ensured is MethodInfo mb)
            {
                methodInfo = mb;
                return true;
            }
        }

        // Metadata-defined interfaces: resolve through reflection.
        if (definitionMethod.ContainingType is not null)
        {
            var ifaceClr = ResolveClrType(definitionMethod.ContainingType);

            if (ifaceClr is TypeBuilder || ifaceClr.GetType().Name.Contains("TypeBuilderInstantiation"))
            {
                methodInfo = null!;
                return false;
            }

            // Ensure we reflect on the *generic type definition* for the definition method.
            if (ifaceClr.IsGenericType && !ifaceClr.IsGenericTypeDefinition)
                ifaceClr = ifaceClr.GetGenericTypeDefinition();

            // If ifaceClr is still a TypeBuilder/TypeBuilderInstantiation, avoid GetMethod here too.
            // But for metadata this will be a real runtime Type.
            var parameterTypes = definitionMethod.Parameters.Select(GetParameterClrType).ToArray();
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        (definitionMethod.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

            var m = ifaceClr.GetMethod(definitionMethod.Name, flags, null, parameterTypes, null);
            if (m is not null)
            {
                methodInfo = m;
                return true;
            }
        }

        methodInfo = null!;
        return false;
    }

    private bool TryGetMethodInfo(IMethodSymbol methodSymbol, out MethodInfo methodInfo)
    {
        switch (methodSymbol)
        {
            case SourceMethodSymbol sourceMethod:
                {
                    if (CodeGen.GetMemberBuilder(sourceMethod) is MethodInfo builder)
                    {
                        methodInfo = builder;
                        return true;
                    }

                    break;
                }
            case PEMethodSymbol peMethod:
                methodInfo = CodeGen.RuntimeSymbolResolver.GetMethodInfo(peMethod);
                return true;
            case SubstitutedMethodSymbol substitutedMethod:
                methodInfo = substitutedMethod.GetMethodInfo(CodeGen);
                return true;
        }

        methodInfo = null!;
        return false;
    }

    private Type GetParameterClrType(IParameterSymbol parameter)
    {
        var parameterType = ResolveClrType(parameter.Type);
        return parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter
            ? parameterType.MakeByRefType()
            : parameterType;
    }

    private Type GetMethodReturnClrType(ITypeSymbol returnType)
        => returnType.SpecialType == SpecialType.System_Unit
            ? typeof(void)
            : ResolveClrType(returnType);

    private static ParameterAttributes GetParameterAttributes(IParameterSymbol parameter)
    {
        return parameter.RefKind switch
        {
            RefKind.Out => ParameterAttributes.Out,
            RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter => ParameterAttributes.In,
            _ => ParameterAttributes.None
        };
    }

    private void ApplyTopLevelAttributeIfNamespaceMembersContainer()
    {
        if (TypeSymbol is not SynthesizedNamespaceMembersClassSymbol)
            return;

        var topLevelAttribute = CodeGen.CreateTopLevelAttributeBuilder();
        if (topLevelAttribute is not null)
            TypeBuilder!.SetCustomAttribute(topLevelAttribute);
    }

    private void ApplyCompilerGeneratedAttributeIfClosureFrame()
    {
        if (TypeBuilder is null || !IsClosureFrameType(TypeSymbol))
            return;

        var compilerGeneratedAttr = CodeGen.CreateCompilerGeneratedAttributeBuilder();
        if (compilerGeneratedAttr is not null)
            TypeBuilder.SetCustomAttribute(compilerGeneratedAttr);
    }

    private static bool IsClosureFrameType(ITypeSymbol typeSymbol)
        => typeSymbol.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal) ||
           typeSymbol.MetadataName.StartsWith("<>c__DisplayClass", StringComparison.Ordinal);

    private static bool SignaturesMatch(IMethodSymbol candidate, IMethodSymbol interfaceMethod)
    {
        if (!string.Equals(candidate.Name, interfaceMethod.Name, StringComparison.Ordinal))
            return false;

        return SignatureTypesMatch(candidate, interfaceMethod);
    }

    private static bool SignatureTypesMatch(IMethodSymbol candidate, IMethodSymbol interfaceMethod)
    {
        if (!ReturnTypesMatch(candidate.ReturnType, interfaceMethod.ReturnType))
            return false;

        if (candidate.Parameters.Length != interfaceMethod.Parameters.Length)
            return false;

        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            var candidateParameter = candidate.Parameters[i];
            var interfaceParameter = interfaceMethod.Parameters[i];

            if (candidateParameter.RefKind != interfaceParameter.RefKind)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(candidateParameter.Type, interfaceParameter.Type))
                return false;
        }

        return true;
    }

    private static bool ReturnTypesMatch(ITypeSymbol candidateReturnType, ITypeSymbol interfaceReturnType)
    {
        if (SymbolEqualityComparer.Default.Equals(candidateReturnType, interfaceReturnType))
            return true;

        if (candidateReturnType.SpecialType == SpecialType.System_Unit
            && interfaceReturnType.SpecialType == SpecialType.System_Void)
            return true;

        if (candidateReturnType.SpecialType == SpecialType.System_Void
            && interfaceReturnType.SpecialType == SpecialType.System_Unit)
            return true;

        return false;
    }
}
