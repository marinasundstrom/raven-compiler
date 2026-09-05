using System;
using System.Collections.Generic;
using System.Linq;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

class FunctionBinder : Binder
{
    private readonly FunctionStatementSyntax _syntax;
    private MethodBinder? _methodBodyBinder;
    private SourceMethodSymbol? _methodSymbol;

    public FunctionBinder(Binder parent, FunctionStatementSyntax syntax)
        : base(CreateParentBinder(parent, syntax))
    {
        _syntax = syntax;
    }

    public override ISymbol? BindDeclaredSymbol(SyntaxNode node)
    {
        if (node == _syntax)
        {
            return GetMethodSymbol();
        }

        return base.BindDeclaredSymbol(node);
    }

    public IMethodSymbol GetMethodSymbol()
    {
        if (_methodSymbol is not null)
        {
            ValidateNamespaceFunctionAccessibility(_methodSymbol);
            return _methodSymbol;
        }

        var isNamespaceMember = IsNamespaceLevelFunctionMember(_syntax);

        if (Compilation.TryGetMethodSymbol(_syntax, out var cachedMethod) &&
            cachedMethod is SourceMethodSymbol { IsSignatureSkeleton: false } cachedCompletedMethod)
        {
            _methodSymbol = cachedCompletedMethod;
            _methodBodyBinder ??= new MethodBinder(_methodSymbol, this);
            ValidateNamespaceFunctionAccessibility(_methodSymbol);
            return _methodSymbol;
        }

        var container = ResolveContainingType();
        if (container is null)
            throw new InvalidOperationException("Unable to resolve containing type for function declaration.");

        var existingMethod = container
            .GetMembers(_syntax.Identifier.ValueText)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.DeclaringSyntaxReferences.Any(r => r.GetSyntax() == _syntax));

        if (existingMethod is SourceMethodSymbol existingSource && !existingSource.IsSignatureSkeleton)
        {
            _methodSymbol = existingSource;
            Compilation.RegisterMethodSymbol(_syntax, _methodSymbol);
            ValidateNamespaceFunctionAccessibility(_methodSymbol);
            return _methodSymbol;
        }

        var isAsync = _syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AsyncKeyword);
        var isExtern = _syntax.Modifiers.Any(m => m.Kind == SyntaxKind.ExternKeyword);

        var inferredReturnType = isAsync
            ? Compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task)
            : Compilation.GetSpecialType(SpecialType.System_Unit);

        var isSubmissionFunction = Compilation.IsSubmission && _syntax.Parent is GlobalStatementSyntax;
        var accessibility = isSubmissionFunction
            ? AccessibilityUtilities.DetermineAccessibility(_syntax.Modifiers, Accessibility.Public)
            : isNamespaceMember
                ? AccessibilityUtilities.DetermineAccessibility(_syntax.Modifiers, Accessibility.Internal)
                : Accessibility.Internal;

        _methodSymbol = existingMethod is SourceMethodSymbol existingSkeleton
            ? existingSkeleton
            : new SourceMethodSymbol(
                _syntax.Identifier.ValueText,
                inferredReturnType,
                [],
                container,
                container,
                container.ContainingNamespace,
                [_syntax.GetLocation()],
                [_syntax.GetReference()],
                isStatic: true,
                isAsync: isAsync,
                isExtern: isExtern,
                declaredAccessibility: accessibility);

        TypeParameterInitializer.InitializeMethodTypeParameters(
            _methodSymbol,
            (INamedTypeSymbol)container,
            _syntax.TypeParameterList,
            _syntax.ConstraintClauses,
            _syntax.SyntaxTree,
            _diagnostics);

        _methodBodyBinder ??= new MethodBinder(_methodSymbol, this);
        var methodBinder = _methodBodyBinder;

        if (_methodSymbol.TypeParameters.Length > 0)
            methodBinder.EnsureTypeParameterConstraintTypesResolved(_methodSymbol.TypeParameters);

        var hasInvalidAsyncReturnType = false;

        var returnType = _syntax.ReturnType is null
            ? inferredReturnType
            : methodBinder.BindTypeSyntaxAndReport(_syntax.ReturnType.Type);

        if (isAsync && _syntax.ReturnType is { } annotatedReturn &&
            (!IsValidAsyncReturnType(returnType) || returnType.TypeKind == TypeKind.Error))
        {
            var display = returnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var suggestedReturnType = AsyncReturnTypeUtilities.GetSuggestedAsyncReturnTypeDisplay(Compilation, returnType);
            _diagnostics.ReportAsyncReturnTypeMustBeTaskLike(display, suggestedReturnType, annotatedReturn.Type.GetLocation());
            returnType = Compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task);
            hasInvalidAsyncReturnType = true;
        }

        _methodSymbol.SetReturnType(returnType);

        if (hasInvalidAsyncReturnType)
            _methodSymbol.MarkAsyncReturnTypeError();

        if (isExtern && (_syntax.Body is not null || _syntax.ExpressionBody is not null))
        {
            _diagnostics.ReportExternMemberCannotHaveBody(_syntax.Identifier.ValueText, _syntax.Identifier.GetLocation());
        }

        var parameters = new List<SourceParameterSymbol>();
        var seenOptionalParameter = false;
        foreach (var p in _syntax.ParameterList.Parameters)
        {
            var typeSyntax = p.TypeAnnotation?.Type;
            if (typeSyntax is ByRefTypeSyntax &&
                p.RefKindKeyword.Kind is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            {
                _diagnostics.ReportParameterModifierCannotBeCombinedWithByRefType(
                    p.Identifier.ValueText,
                    p.RefKindKeyword.Text,
                    typeSyntax.GetLocation());
            }

            var refKind = ParameterSyntaxUtilities.GetRefKind(p);

            ITypeSymbol type;
            if (typeSyntax is null)
            {
                _diagnostics.ReportParameterTypeAnnotationRequired(
                    p.Identifier.ValueText,
                    p.Identifier.GetLocation());
                type = Compilation.ErrorTypeSymbol;
            }
            else
            {
                var boundTypeSyntax = refKind.IsByRef && typeSyntax is ByRefTypeSyntax byRefType
                    ? byRefType.ElementType
                    : typeSyntax;
                type = methodBinder.BindTypeSyntaxAndReport(boundTypeSyntax);
                type = methodBinder.EnsureTypeValidForStorageLocation(type, boundTypeSyntax.GetLocation());
            }

            if (p.BindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            {
                _diagnostics.ReportParameterBindingKeywordNotAllowed(
                    p.BindingKeyword.Text,
                    p.Identifier.ValueText,
                    p.BindingKeyword.GetLocation());
            }

            var isMutable = refKind is RefKind.Ref or RefKind.Out;

            var defaultResult = TypeMemberBinder.ProcessParameterDefault(
                p,
                type,
                p.Identifier.ValueText,
                _diagnostics,
                ref seenOptionalParameter);

            type = TypeMemberBinder.NormalizeVarParamsParameterType(
                Compilation,
                p,
                type,
                p.Identifier.ValueText,
                _diagnostics,
                out var isVarParams);

            parameters.Add(new SourceParameterSymbol(
                p.Identifier.ValueText,
                type,
                _methodSymbol,
                container.ContainingType,
                container.ContainingNamespace,
                [p.Identifier.GetLocation()],
                [p.GetReference()],
                refKind,
                defaultResult.HasExplicitDefaultValue,
                defaultResult.ExplicitDefaultValue,
                isMutable,
                isVarParams,
                ParameterSyntaxUtilities.GetScopedKind(p, type, _diagnostics)));
        }

        _methodSymbol.SetParameters(parameters);

        ValidateNamespaceFunctionAccessibility(_methodSymbol);

        _methodSymbol.MarkSignatureBindingComplete();
        Compilation.RegisterMethodSymbol(_syntax, _methodSymbol);

        if (isNamespaceMember && _methodSymbol is Symbol sourceSymbol && _syntax.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.FileprivateKeyword))
            sourceSymbol.MarkFileScoped(_syntax.SyntaxTree?.FilePath);

        return _methodSymbol;
    }

    private void ValidateNamespaceFunctionAccessibility(SourceMethodSymbol method)
    {
        if (!IsNamespaceLevelFunctionMember(_syntax) || method.IsSignatureSkeleton)
            return;

        EnsureTypeParameterConstraintTypesResolved(method.TypeParameters);
        TypeMemberBinder.ValidateTypeParameterConstraintAccessibility(
            method.TypeParameters,
            method.DeclaredAccessibility,
            containingType: null,
            "function",
            method.Name,
            _diagnostics);

        TypeMemberBinder.ValidateTypeAccessibility(
            method.ReturnType,
            method.DeclaredAccessibility,
            containingType: null,
            "function",
            method.Name,
            "return",
            _diagnostics,
            _syntax.ReturnType?.Type.GetLocation() ?? _syntax.Identifier.GetLocation());

        // Match signature construction, which enumerates parameter nodes and
        // ignores recovery tokens that need not occupy separator slots.
        var parameterSyntaxes = _syntax.ParameterList.Parameters.ToArray();
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var parameterSyntax = parameterSyntaxes[i];
            TypeMemberBinder.ValidateTypeAccessibility(
                parameter.Type,
                method.DeclaredAccessibility,
                containingType: null,
                "function",
                method.Name,
                $"parameter '{parameter.Name}'",
                _diagnostics,
                parameterSyntax.TypeAnnotation?.Type.GetLocation() ?? parameterSyntax.Identifier.GetLocation());
        }
    }

    private INamedTypeSymbol? ResolveContainingType()
    {
        if (TryResolveNamespaceMembersContainer() is { } namespaceMembersContainer)
            return namespaceMembersContainer;

        // Local functions should bind within the enclosing type context, not
        // require a synthesized Program type.
        for (var current = ParentBinder; current is not null; current = current.ParentBinder)
        {
            switch (current.ContainingSymbol)
            {
                case INamedTypeSymbol namedType:
                    return namedType;
                case IMethodSymbol method when method.ContainingType is not null:
                    return method.ContainingType;
            }
        }

        if (TryResolveTopLevelProgramContainer() is { } topLevelContainer)
            return topLevelContainer;

        return Compilation.SourceGlobalNamespace.LookupType("Program") as INamedTypeSymbol;
    }

    private INamedTypeSymbol? TryResolveNamespaceMembersContainer()
    {
        if (!IsNamespaceLevelFunctionMember(_syntax))
            return null;

        var targetNamespace = ResolveNamespaceMemberNamespace(_syntax);
        return targetNamespace is null
            ? null
            : Compilation.GetOrCreateNamespaceMembersContainer(targetNamespace, _syntax);
    }

    private INamedTypeSymbol? TryResolveTopLevelProgramContainer()
    {
        if (_syntax.SyntaxTree.GetRoot() is not CompilationUnitSyntax compilationUnit)
            return null;

        var bindableGlobals = Compilation.GetBindableGlobalStatements(compilationUnit);
        if (bindableGlobals.Count == 0)
            return null;

        var fileScopedNamespace = compilationUnit.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        var targetNamespace = fileScopedNamespace is null
            ? Compilation.SourceGlobalNamespace
            : Compilation.GetOrCreateNamespaceSymbol(fileScopedNamespace.Name.ToString())?.AsSourceNamespace()
                ?? Compilation.SourceGlobalNamespace;

        var (programClass, _, _) = Compilation.GetOrCreateTopLevelProgram(
            compilationUnit,
            targetNamespace,
            bindableGlobals);

        return programClass;
    }

    private SourceNamespaceSymbol? ResolveNamespaceMemberNamespace(SyntaxNode syntax)
    {
        if (syntax.Parent is not GlobalStatementSyntax)
            return null;

        var namespaceName = string.Join(
            ".",
            syntax.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(static declaration => declaration.Name.ToString()));

        return string.IsNullOrWhiteSpace(namespaceName)
            ? Compilation.SourceGlobalNamespace
            : Compilation.GetOrCreateNamespaceSymbol(namespaceName)?.AsSourceNamespace();
    }

    private bool IsNamespaceLevelFunctionMember(FunctionStatementSyntax syntax)
        => syntax.Parent is GlobalStatementSyntax globalStatement &&
            Compilation.IsTopLevelFunctionMember(globalStatement) &&
            !Compilation.IsFileScopeLocalFunction(globalStatement);

    private static Binder CreateParentBinder(Binder parent, FunctionStatementSyntax syntax)
    {
        if (!parent.Compilation.IsSubmission ||
            syntax.Parent is not GlobalStatementSyntax globalStatement ||
            !Compilation.IsTopLevelFunctionMember(globalStatement))
        {
            return parent;
        }

        for (var current = parent; current is not null; current = current.ParentBinder)
        {
            if (current is SubmissionBinder)
                return parent;
        }

        return new SubmissionBinder(parent, parent.Compilation);
    }

    public MethodBinder GetMethodBodyBinder()
    {
        var methodSymbol = Compilation.TryGetMethodSymbol(_syntax, out var cachedMethod) &&
            cachedMethod is SourceMethodSymbol { IsSignatureSkeleton: false }
                ? cachedMethod
                : GetMethodSymbol();
        return _methodBodyBinder ??= new MethodBinder(methodSymbol!, this);
    }
}
