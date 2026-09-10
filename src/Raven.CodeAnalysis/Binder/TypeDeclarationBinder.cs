using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

internal abstract class TypeDeclarationBinder : Binder
{
    internal readonly record struct NominalTypeShape(
        INamedTypeSymbol? BaseType,
        ImmutableArray<INamedTypeSymbol> Interfaces);

    protected TypeDeclarationBinder(Binder parent, INamedTypeSymbol containingType, SyntaxNode syntax)
        : base(parent)
    {
        ContainingSymbol = containingType;
        Syntax = syntax;
    }

    protected SyntaxNode Syntax { get; }

    public override INamedTypeSymbol ContainingSymbol { get; }

    public override ISymbol? LookupSymbol(string name)
        => LookupSymbols(name).FirstOrDefault();

    public override IEnumerable<ISymbol> LookupSymbols(string name)
    {
        foreach (var symbol in EnumerateTypeAndBaseMembers(ContainingSymbol, name))
            yield return symbol;

        foreach (var symbol in base.LookupSymbols(name))
            yield return symbol;
    }

    public override ITypeSymbol? LookupType(string name)
    {
        if (string.Equals(ContainingSymbol.Name, name, StringComparison.Ordinal))
            return ContainingSymbol;

        var typeParameter = ContainingSymbol.TypeParameters.FirstOrDefault(tp => tp.Name == name);
        if (typeParameter is not null)
            return typeParameter;

        if (BlocksContainingTypeParameters())
            return null;

        return base.LookupType(name);
    }

    public override ISymbol? BindDeclaredSymbol(SyntaxNode node)
    {
        return node == Syntax ? ContainingSymbol : base.BindDeclaredSymbol(node);
    }

    protected override IReadOnlyDictionary<string, ITypeSymbol> GetInScopeTypeParameters()
    {
        var map = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);

        if (!ContainingSymbol.TypeParameters.IsDefaultOrEmpty)
        {
            foreach (var tp in ContainingSymbol.TypeParameters)
                map.TryAdd(tp.Name, tp);
        }

        return map;
    }

    internal NominalTypeShape BindNominalTypeShape(
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol? defaultBaseType,
        ImmutableArray<INamedTypeSymbol> defaultInterfaces)
    {
        EnsureTypeParameterConstraintTypesResolved(ContainingSymbol.TypeParameters);
        TypeMemberBinder.ValidateTypeParameterConstraintAccessibility(
            ContainingSymbol.TypeParameters,
            ContainingSymbol.DeclaredAccessibility,
            ContainingSymbol.ContainingType,
            "type",
            ContainingSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Diagnostics);

        var baseType = defaultBaseType;
        var interfaces = defaultInterfaces;
        var baseList = GetBaseList(declaration);
        var isStructLike = IsStructLikeNominalType(declaration);
        var baseTypeAssigned = false;

        if (baseList is not null)
        {
            var resolvedInterfaces = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            foreach (var (baseTypeSyntax, index) in baseList.Types.Select((syntax, i) => (syntax, i)))
            {
                if (!TryBindNamedTypeFromTypeSyntax(baseTypeSyntax.Type, out var resolved, reportDiagnostics: true) || resolved is null)
                    continue;

                TypeMemberBinder.ValidateTypeAccessibility(
                    resolved,
                    ContainingSymbol.DeclaredAccessibility,
                    ContainingSymbol.ContainingType,
                    "type",
                    ContainingSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    resolved.TypeKind == TypeKind.Interface ? "base interface" : "base",
                    Diagnostics,
                    baseTypeSyntax.Type.GetLocation());

                if (resolved.ConstructedFrom is INamedTypeSymbol genericDefinition &&
                    !SymbolEqualityComparer.Default.Equals(genericDefinition, resolved) &&
                    !resolved.TypeArguments.IsDefaultOrEmpty)
                {
                    var typeArgumentSyntax = GetRightmostTypeArgumentList(baseTypeSyntax.Type);
                    ValidateTypeArgumentConstraints(
                        genericDefinition,
                        resolved.TypeArguments,
                        i => GetTypeArgumentLocation(typeArgumentSyntax, baseTypeSyntax.Type.GetLocation(), i),
                        genericDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                }

                if (resolved.TypeKind == TypeKind.Interface)
                {
                    resolvedInterfaces.Add(resolved);
                    ReportInvalidInheritedInterfaceType(declaration, baseTypeSyntax, resolved);
                    continue;
                }

                // Classes and record classes can have one non-interface base type in the first slot.
                // Structs/record structs only support interfaces in the base list.
                if (!isStructLike && index == 0 && !baseTypeAssigned)
                {
                    baseType = resolved;
                    baseTypeAssigned = true;
                }
            }

            if (resolvedInterfaces.Count > 0)
                interfaces = MergeInterfaceSets(interfaces, resolvedInterfaces.ToImmutable());

            ReportInvalidInheritedBaseType(declaration, baseList, baseType);
        }

        return new NominalTypeShape(baseType, interfaces);
    }

    private static SeparatedSyntaxList<TypeArgumentSyntax> GetRightmostTypeArgumentList(TypeSyntax syntax)
    {
        return syntax switch
        {
            GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
            QualifiedNameSyntax qualified => GetRightmostTypeArgumentList(qualified.Right),
            _ => default,
        };
    }

    private void ReportInvalidInheritedBaseType(TypeDeclarationSyntax declaration, BaseListSyntax baseList, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
            return;

        if (baseType.IsStatic)
        {
            Diagnostics.ReportStaticTypeCannotBeInherited(
                baseType.Name,
                baseList.Types[0].GetLocation());
            return;
        }

        if (baseType is SourceNamedTypeSymbol sourceBase && sourceBase.IsSealedHierarchy)
        {
            if (sourceBase.HasExplicitPermits)
            {
                var derivingName = declaration.Identifier.ValueText;
                var isPermitted = false;
                foreach (var permitted in sourceBase.PermittedDirectSubtypes)
                {
                    if (string.Equals(permitted.Name, derivingName, StringComparison.Ordinal))
                    {
                        isPermitted = true;
                        break;
                    }
                }

                if (!isPermitted)
                {
                    Diagnostics.ReportSealedHierarchyInheritanceDeniedNotPermitted(
                        derivingName,
                        baseType.Name,
                        baseList.Types[0].GetLocation());
                }
            }
            else
            {
                var derivingFile = declaration.SyntaxTree?.FilePath;
                var baseFile = sourceBase.SealedHierarchySourceFile;

                if (!string.Equals(derivingFile, baseFile, StringComparison.Ordinal))
                {
                    Diagnostics.ReportSealedHierarchyInheritanceDeniedSameFile(
                        declaration.Identifier.ValueText,
                        baseType.Name,
                        baseList.Types[0].GetLocation());
                }
            }
            return;
        }

        if (baseType.IsClosed || baseType.IsSealedHierarchy)
        {
            Diagnostics.ReportCannotInheritFromClosedType(
                baseType.Name,
                baseList.Types[0].GetLocation());
        }
    }

    internal void ReportInvalidInheritedInterfaceType(
        BaseTypeDeclarationSyntax declaration,
        BaseTypeSyntax baseTypeSyntax,
        INamedTypeSymbol interfaceType)
    {
        if (interfaceType is not SourceNamedTypeSymbol sourceInterface)
        {
            if (interfaceType.IsSealedHierarchy)
            {
                Diagnostics.ReportCannotInheritFromClosedType(
                    interfaceType.Name,
                    baseTypeSyntax.GetLocation());
            }
            return;
        }

        if (!sourceInterface.IsSealedHierarchy)
            return;

        if (sourceInterface.HasExplicitPermits)
        {
            var derivingName = declaration.Identifier.ValueText;
            var isPermitted = sourceInterface.PermittedDirectSubtypes
                .Any(permitted => string.Equals(permitted.Name, derivingName, StringComparison.Ordinal));

            if (!isPermitted)
            {
                Diagnostics.ReportSealedHierarchyInheritanceDeniedNotPermitted(
                    derivingName,
                    interfaceType.Name,
                    baseTypeSyntax.GetLocation());
            }

            return;
        }

        var derivingFile = declaration.SyntaxTree?.FilePath;
        var baseFile = sourceInterface.SealedHierarchySourceFile;
        if (!string.Equals(derivingFile, baseFile, StringComparison.Ordinal))
        {
            Diagnostics.ReportSealedHierarchyInheritanceDeniedSameFile(
                declaration.Identifier.ValueText,
                interfaceType.Name,
                baseTypeSyntax.GetLocation());
        }
    }

    private static BaseListSyntax? GetBaseList(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDeclaration => classDeclaration.BaseList,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.BaseList,
            StructDeclarationSyntax structDeclaration => structDeclaration.BaseList,
            _ => null
        };

    private static bool IsStructLikeNominalType(TypeDeclarationSyntax declaration)
        => declaration is StructDeclarationSyntax ||
           declaration is RecordDeclarationSyntax { ClassOrStructKeyword.Kind: SyntaxKind.StructKeyword };

    private static ImmutableArray<INamedTypeSymbol> MergeInterfaceSets(
        ImmutableArray<INamedTypeSymbol> existing,
        ImmutableArray<INamedTypeSymbol> additional)
    {
        if (existing.IsDefaultOrEmpty)
            return additional;

        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var type in existing)
            if (seen.Add(type))
                builder.Add(type);

        foreach (var type in additional)
            if (seen.Add(type))
                builder.Add(type);

        return builder.ToImmutable();
    }

    private bool BlocksContainingTypeParameters()
    {
        if (ContainingSymbol.ContainingType is not INamedTypeSymbol containingType ||
            !containingType.IsSealedHierarchy ||
            !containingType.IsGenericType)
        {
            return false;
        }

        if (ContainingSymbol.BaseType is INamedTypeSymbol baseType &&
            SymbolEqualityComparer.Default.Equals(GetDefinition(baseType), containingType))
        {
            return true;
        }

        return ContainingSymbol.Interfaces.Any(interfaceType =>
            SymbolEqualityComparer.Default.Equals(GetDefinition(interfaceType), containingType));
    }

    private static INamedTypeSymbol GetDefinition(INamedTypeSymbol type)
        => type.ConstructedFrom is INamedTypeSymbol definition &&
           !SymbolEqualityComparer.Default.Equals(type, definition)
            ? definition
            : type;
}
