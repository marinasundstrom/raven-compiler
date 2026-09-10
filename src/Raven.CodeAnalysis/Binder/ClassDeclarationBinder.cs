using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

internal sealed class ClassDeclarationBinder : TypeDeclarationBinder
{
    public ClassDeclarationBinder(Binder parent, INamedTypeSymbol containingType, TypeDeclarationSyntax syntax)
        : base(parent, containingType, syntax)
    {
    }

    public void EnsureDefaultConstructor()
    {
        if (ContainingSymbol is INamedTypeSymbol named)
        {
            var typeSyntax = (TypeDeclarationSyntax)Syntax;

            if (named.IsStatic)
            {
                EnsureStaticConstructorIfNeeded(named, typeSyntax);
                return;
            }

            var hasPrimaryConstructor = typeSyntax is ClassDeclarationSyntax { ParameterList: not null }
                or RecordDeclarationSyntax { ParameterList: not null }
                or StructDeclarationSyntax { ParameterList: not null };
            var hasExplicitInstanceConstructor = typeSyntax.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Any(ctor => !ctor.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword));

            var hasParameterlessCtor = named.Constructors
                .Any(ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);

            if (!hasPrimaryConstructor &&
                !hasExplicitInstanceConstructor &&
                !hasParameterlessCtor)
            {
                _ = new SourceMethodSymbol(
                    ".ctor",
                    Compilation.GetSpecialType(SpecialType.System_Unit),
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    ContainingSymbol,
                    ContainingSymbol,
                    CurrentNamespace!.AsSourceNamespace(),
                    [typeSyntax.GetLocation()],
                    [typeSyntax.GetReference()],
                    isStatic: false,
                    methodKind: MethodKind.Constructor,
                    declaredAccessibility: Accessibility.Public);
            }

            bool hasStaticCtor = named.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.MethodKind == MethodKind.StaticConstructor);

            if (!hasStaticCtor)
            {
                EnsureStaticConstructorIfNeeded(named, typeSyntax);
            }
        }
    }

    public void ValidateImplicitBaseConstructors()
    {
        var type = ContainingSymbol;
        if (type.IsStatic || type.IsValueType || type.BaseType is not { TypeKind: not TypeKind.Error } baseType)
            return;

        if (baseType.Constructors.Any(static constructor => !constructor.IsStatic && constructor.Parameters.Length == 0))
            return;

        foreach (var constructor in type.Constructors.OfType<SourceMethodSymbol>())
        {
            if (constructor.IsStatic || constructor.HasConstructorInitializerSyntax || constructor.ConstructorInitializer is not null)
                continue;

            // Synthesized record copy constructors call the base copy constructor.
            if (type is SourceNamedTypeSymbol { IsRecord: true } && constructor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type))
            {
                continue;
            }

            var declaration = constructor.GetDeclaringTypeSyntax();
            if (declaration?.SyntaxTree != Syntax.SyntaxTree || declaration.Span != Syntax.Span)
                continue;

            _diagnostics.ReportNoOverloadForMethod(
                "constructor for type", baseType.Name, 0, constructor.Locations[0]);
        }
    }

    private void EnsureStaticConstructorIfNeeded(INamedTypeSymbol named, TypeDeclarationSyntax typeSyntax)
    {
        bool needsStaticCtor = named.GetMembers()
            .OfType<SourceFieldSymbol>()
            .Any(f => f.IsStatic && f.Initializer is not null);

        if (!needsStaticCtor)
            return;

        _ = new SourceMethodSymbol(
            ".cctor",
            Compilation.GetSpecialType(SpecialType.System_Unit),
            ImmutableArray<SourceParameterSymbol>.Empty,
            ContainingSymbol,
            ContainingSymbol,
            CurrentNamespace!.AsSourceNamespace(),
            [typeSyntax.GetLocation()],
            [typeSyntax.GetReference()],
            isStatic: true,
            methodKind: MethodKind.StaticConstructor,
            declaredAccessibility: Accessibility.Private);
    }
}
