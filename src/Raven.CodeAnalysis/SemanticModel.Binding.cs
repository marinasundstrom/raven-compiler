using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Documentation;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public partial class SemanticModel
{
    private readonly Dictionary<LabeledStatementSyntax, ILabelSymbol> _labelDeclarations = new();
    private readonly Dictionary<ILabelSymbol, LabeledStatementSyntax> _labelSyntax = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, List<ILabelSymbol>> _labelsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<GotoStatementSyntax, ILabelSymbol> _gotoTargets = new();
    private readonly Dictionary<AttributeSyntax, AttributeData?> _attributeCache = new();
    private static readonly object s_boundChildPropertyCacheGate = new();
    private static readonly Dictionary<Type, PropertyInfo[]> s_boundChildPropertyCache = new();
    private static readonly DiagnosticDescriptor s_globalStatementsDisabled = DiagnosticDescriptor.Create(
        "RAV7001",
        "Top-level statements are disabled",
        "",
        "",
        "Top-level statements are disabled by compilation options",
        "compiler",
        DiagnosticSeverity.Error,
        true);
    private static readonly DiagnosticDescriptor s_namespaceMembersDisabled = DiagnosticDescriptor.Create(
        "RAV7003",
        "Top-level members are disabled",
        "",
        "",
        "Top-level members are disabled by compilation options",
        "compiler",
        DiagnosticSeverity.Error,
        true);
    private static readonly DiagnosticDescriptor s_invalidLocalTypeDeclaration = DiagnosticDescriptor.Create(
        "RAV7002",
        "Invalid local type declaration",
        "",
        "",
        "Only class, struct, record, and enum declarations are valid local type declarations",
        "compiler",
        DiagnosticSeverity.Error,
        true);

    internal AttributeData? BindAttribute(AttributeSyntax attribute)
    {
        if (attribute is null)
            throw new ArgumentNullException(nameof(attribute));

        if (attribute.IsMacroAttribute())
            return null;

        if (_attributeCache.TryGetValue(attribute, out var cached))
            return cached;

        EnsureDeclarations();

        var boundExpression = BindAttributeExpression(attribute);
        var data = AttributeDataFactory.Create(boundExpression, attribute);
        if (data?.AttributeConstructor is null && !MemberSignaturesDeclared)
        {
            RemoveCachedBoundNode(attribute);
            EnsureMemberSignaturesDeclared();
            boundExpression = BindAttributeExpression(attribute);
            data = AttributeDataFactory.Create(boundExpression, attribute);
        }

        _attributeCache[attribute] = data;
        return data;
    }

    private BoundExpression? BindAttributeExpression(AttributeSyntax attribute)
    {
        BoundExpression? boundExpression = TryGetCachedBoundNode(attribute) as BoundExpression;
        var binderNode = (SyntaxNode?)attribute.Parent ?? attribute;
        var binder = GetBinderForIncrementalSemanticQuery(binderNode);

        if (boundExpression is null)
        {
            var attributeBinder = binder as AttributeBinder
                ?? new AttributeBinder(binder.ContainingSymbol, binder);
            boundExpression = attributeBinder.GetOrBindForSemanticQuery(attribute) as BoundExpression;
        }

        if (boundExpression is null)
            return null;

        return boundExpression;
    }

    internal void RegisterLabel(LabeledStatementSyntax syntax, ILabelSymbol symbol)
    {
        _labelDeclarations[syntax] = symbol;
        _labelSyntax[symbol] = syntax;

        if (!_labelsByName.TryGetValue(symbol.Name, out var list))
        {
            list = new List<ILabelSymbol>();
            _labelsByName[symbol.Name] = list;
        }

        if (!list.Any(existing => SymbolEqualityComparer.Default.Equals(existing, symbol)))
            list.Add(symbol);
    }

    internal void RegisterGoto(GotoStatementSyntax syntax, ILabelSymbol target)
    {
        _gotoTargets[syntax] = target;
    }

    internal void EnsureDeclarations()
    {
        Compilation.PerformanceInstrumentation.Setup.RecordEnsureDeclarationsCall();

        if (_declarationsComplete)
            return;

        var currentThreadId = Environment.CurrentManagedThreadId;

        lock (_bindingSetupGate)
        {
            while (_isEnsuringDeclarations && _declarationSetupThreadId != currentThreadId)
                Monitor.Wait(_bindingSetupGate);

            if (_declarationsComplete || _isEnsuringDeclarations)
                return;

            _isEnsuringDeclarations = true;
            _declarationSetupThreadId = currentThreadId;

            try
            {
                if (SyntaxTree.GetRoot() is CompilationUnitSyntax cu)
                {
                    Compilation.PerformanceInstrumentation.Setup.RecordDeclarationPass();
                    DeclareCompilationUnit(cu);
                }

                _declarationsComplete = true;
            }
            finally
            {
                _declarationSetupThreadId = 0;
                _isEnsuringDeclarations = false;
                Monitor.PulseAll(_bindingSetupGate);
            }
        }
    }

    internal bool DeclarationsComplete => _declarationsComplete;

    private void DeclareCompilationUnit(CompilationUnitSyntax cu)
    {
        var fileScopedNamespace = cu.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        INamespaceSymbol targetNamespace;

        if (fileScopedNamespace is not null)
        {
            targetNamespace = Compilation.GetOrCreateNamespaceSymbol(fileScopedNamespace.Name.ToString())
                ?? throw new Exception("Namespace not found");
        }
        else
        {
            targetNamespace = Compilation.GlobalNamespace;
        }

        DeclareNamespaceMembers(cu, targetNamespace);
    }

    private void DeclareNamespaceMembers(SyntaxNode containerNode, INamespaceSymbol parentNamespace)
    {
        var objectType = Compilation.TryGetMetadataReferenceTypeByMetadataName("System.Object");

        // Type-like declarations must be available before member signatures are
        // resolved, because top-level functions can reference source types that
        // are declared later in the same file or namespace.
        foreach (var member in Compilation.GetEffectiveNamespaceMembers(containerNode))
        {
            var classTargetDeclaration = member switch
            {
                GlobalStatementSyntax { Statement: FunctionStatementSyntax function } => (SyntaxNode)function,
                MemberDeclarationSyntax declaration when declaration is not GlobalStatementSyntax => declaration,
                _ => null
            };
            if (classTargetDeclaration is not null &&
                GetNamespaceContainerAttributeLists(classTargetDeclaration)
                    .Any(AttributeUsageHelper.IsNamespaceContainerAttributeDeclaration))
            {
                var attributeNamespace = containerNode is CompilationUnitSyntax &&
                                         member is FileScopedNamespaceDeclarationSyntax
                    ? Compilation.SourceGlobalNamespace
                    : parentNamespace.AsSourceNamespace();
                if (attributeNamespace is not null)
                    _ = GetOrCreateNamespaceMembersContainer(classTargetDeclaration, attributeNamespace);
            }

            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nsDecl:
                    {
                        var namespaceName = nsDecl is FileScopedNamespaceDeclarationSyntax
                            ? nsDecl.Name.ToString()
                            : parentNamespace.QualifyName(nsDecl.Name.ToString());

                        var nsSymbol = Compilation.GetOrCreateNamespaceSymbol(namespaceName)
                            ?? throw new Exception($"Unable to resolve namespace '{namespaceName}'.");

                        DeclareNamespaceMembers(nsDecl, nsSymbol);
                        break;
                    }

                case TypeDeclarationSyntax typeDecl when IsNominalTypeDeclaration(typeDecl):
                    {
                        var typeSymbol = DeclareClassSymbol(typeDecl, parentNamespace, objectType);
                        DeclareClassMemberTypes(typeDecl, typeSymbol);
                        break;
                    }

                case DelegateDeclarationSyntax delegateDecl:
                    {
                        DeclareDelegateSymbol(delegateDecl, parentNamespace);
                        break;
                    }

                case InterfaceDeclarationSyntax interfaceDecl:
                    {
                        DeclareInterfaceSymbol(interfaceDecl, parentNamespace, objectType);
                        break;
                    }

                case ExtensionDeclarationSyntax extensionDecl:
                    {
                        DeclareExtensionSymbol(extensionDecl, parentNamespace, objectType);
                        break;
                    }

                case EnumDeclarationSyntax enumDecl:
                    {
                        DeclareEnumSymbol(enumDecl, parentNamespace);
                        break;
                    }

                case UnionDeclarationSyntax unionDecl:
                    {
                        DeclareUnionSymbol(unionDecl, parentNamespace);
                        break;
                    }
            }
        }

        foreach (var member in Compilation.GetEffectiveNamespaceMembers(containerNode))
        {
            switch (member)
            {
                case GlobalStatementSyntax globalStatement
                    when Compilation.IsTopLevelFunctionMember(globalStatement) &&
                         !Compilation.IsFileScopeLocalFunction(globalStatement) &&
                         globalStatement.Statement is FunctionStatementSyntax functionStatement:
                    {
                        DeclareTopLevelFunctionSymbol(functionStatement, parentNamespace);
                        break;
                    }

                case MacroDeclarationSyntax macro:
                    {
                        DeclareMacroSymbol(macro, parentNamespace);
                        break;
                    }

                case ConstDeclarationSyntax constDecl:
                    {
                        if (!Compilation.Options.AllowNamespaceMembers)
                            _declarationDiagnostics.Report(Diagnostic.Create(s_namespaceMembersDisabled, constDecl.GetLocation()));
                        break;
                    }
            }
        }

        static IEnumerable<AttributeListSyntax> GetNamespaceContainerAttributeLists(SyntaxNode declaration)
            => declaration switch
            {
                FunctionStatementSyntax function => function.AttributeLists,
                MemberDeclarationSyntax member => member.AttributeLists,
                _ => []
            };
    }

    private void DeclareMacroSymbol(
        MacroDeclarationSyntax declaration,
        INamespaceSymbol parentNamespace)
    {
        if (TryGetMacroSymbol(declaration, out _))
            return;

        ReportUnsupportedMacroAsyncSyntax(declaration);

        var sourceNamespace = parentNamespace.AsSourceNamespace()
            ?? throw new InvalidOperationException("Macro declarations require a source namespace.");
        ITypeSymbol returnType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var symbol = new SourceMacroSymbol(
            Compilation,
            declaration.Identifier.ValueText,
            returnType,
            sourceNamespace,
            sourceNamespace,
            [declaration.GetLocation()],
            [declaration.GetReference()],
            DetermineNamespaceMemberAccessibility(declaration.Modifiers));

        TypeParameterInitializer.InitializeMacroTypeParameters(
            symbol,
            declaration.TypeParameterList,
            declaration.ConstraintClauses,
            declaration.SyntaxTree,
            _declarationDiagnostics);
        ResolveMacroConstraintTypes(symbol);

        if (declaration.ReturnType is { } returnTypeSyntax)
        {
            returnType = MemberSignatureDeclarationPass.ResolveSkeletonType(
                this,
                returnTypeSyntax.Type,
                returnType,
                symbol.DefinitionType,
                symbol.TypeParameters);
            symbol.SetReturnType(returnType);
        }

        var parameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>();
        foreach (var parameter in declaration.ParameterList.Parameters)
        {
            var parameterType = ResolveMacroParameterType(
                parameter,
                symbol.TypeParameters);
            var macroRole = parameter.OnKeyword.Kind == SyntaxKind.None
                ? MacroParameterRoleFacts.GetRole(parameterType)
                : MacroParameterRole.AttachedTarget;
            parameters.Add(MemberSignatureDeclarationPass.CreateSkeletonParameterSymbol(
                this,
                parameter,
                symbol.SourceExpandMethod,
                symbol.DefinitionType,
                symbol.TypeParameters,
                parameterType,
                macroRole));
        }

        var declaredParameters = parameters.ToImmutable();
        symbol.SetParameters(declaredParameters);

        TypeMemberBinder.ValidateTypeParameterConstraintAccessibility(
            symbol.TypeParameters,
            symbol.DeclaredAccessibility,
            containingType: null,
            "macro",
            symbol.Name,
            _declarationDiagnostics);
        TypeMemberBinder.ValidateTypeAccessibility(
            symbol.ReturnType,
            symbol.DeclaredAccessibility,
            containingType: null,
            "macro",
            symbol.Name,
            "return",
            _declarationDiagnostics,
            declaration.ReturnType?.Type.GetLocation() ?? declaration.Identifier.GetLocation());
        for (var i = 0; i < declaredParameters.Length; i++)
        {
            var parameter = declaredParameters[i];
            var parameterSyntax = declaration.ParameterList.Parameters[i];
            TypeMemberBinder.ValidateTypeAccessibility(
                parameter.Type,
                symbol.DeclaredAccessibility,
                containingType: null,
                "macro",
                symbol.Name,
                $"parameter '{parameter.Name}'",
                _declarationDiagnostics,
                parameterSyntax.TypeAnnotation?.Type.GetLocation() ?? parameterSyntax.GetLocation());
        }

        var targetParameters = declaration.ParameterList.Parameters
            .Select((syntax, index) => (Syntax: syntax, Symbol: declaredParameters[index]))
            .Where(static parameter => parameter.Syntax.OnKeyword.Kind != SyntaxKind.None)
            .ToArray();
        if (targetParameters.FirstOrDefault().Syntax is { } targetSyntax)
        {
            var targetParameter = targetParameters[0].Symbol;
            var target = GetMacroTarget(targetParameter.Type);
            symbol.SetTarget(target, targetParameter);
            if (target == MacroTarget.None)
            {
                _declarationDiagnostics.ReportUnknownMacroTarget(
                    targetParameter.Type.ToDisplayString(),
                    targetSyntax.TypeAnnotation?.Type.GetLocation() ?? targetSyntax.GetLocation());
            }
        }


        if (declaration.ReturnType is null)
        {
            symbol.SetInvocationTargets(MacroInvocationTargets.Expression);
        }
        else
        {
            var invocationTargets = GetMacroInvocationTargets(
                symbol.ReturnType,
                declaration.ReturnType.Type);
            symbol.SetInvocationTargets(invocationTargets);
            if (targetParameters.Length > 0)
            {
                _declarationDiagnostics.ReportAttachedMacroCannotDeclareInvocationTarget(
                    declaration.Identifier.ValueText,
                    symbol.ReturnType.ToDisplayString(),
                    declaration.ReturnType.Type.GetLocation());
            }
            else if (invocationTargets == MacroInvocationTargets.None &&
                     symbol.ReturnType.TypeKind != TypeKind.Error)
            {
                _declarationDiagnostics.ReportUnsupportedMacroReturnType(
                    symbol.ReturnType.ToDisplayString(),
                    declaration.ReturnType.Type.GetLocation());
            }
        }

        ValidateMacroParameters(declaration, symbol);
        ValidateMacroContributionStatements(declaration, symbol);
        ValidateMacroCapabilityClauses(declaration, symbol);
        RegisterMacroDeclarationSymbol(declaration, symbol);

        if (sourceNamespace.GetMembers(symbol.Name)
            .OfType<IMacroDeclarationSymbol>()
            .Any(existing => HaveSameMacroSignature(existing, symbol)))
        {
            _declarationDiagnostics.ReportMacroAlreadyDefined(
                symbol.Name,
                declaration.Identifier.GetLocation());
        }

        sourceNamespace.AddMember(symbol);
    }

    private ITypeSymbol ResolveMacroParameterType(
        ParameterSyntax parameter,
        ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        if (parameter.TypeAnnotation is not { } typeAnnotation)
            return Compilation.ErrorTypeSymbol;

        var parameterType = MemberSignatureDeclarationPass.ResolveSkeletonType(
            this,
            typeAnnotation.Type,
            Compilation.ErrorTypeSymbol,
            containingType: null,
            typeParameters);
        if (parameterType.TypeKind != TypeKind.Error)
            return parameterType;

        return MacroParameterRoleFacts.TryResolveKnownType(
            Compilation,
            typeAnnotation.Type,
            out var knownType)
                ? knownType
                : parameterType;
    }

    private void ValidateMacroParameters(
        MacroDeclarationSyntax declaration,
        SourceMacroSymbol symbol)
    {
        var parameters = declaration.ParameterList.Parameters
            .Select((syntax, index) => (Syntax: syntax, Symbol: symbol.Parameters[index]))
            .ToArray();
        var tokenStreamParameters = parameters
            .Where(static parameter =>
                parameter.Symbol.MacroRole == MacroParameterRole.TokenBody)
            .ToArray();

        foreach (var defaultedParameter in tokenStreamParameters
            .Where(static parameter => parameter.Syntax.DefaultValue is not null))
        {
            _declarationDiagnostics.ReportMacroTokenStreamParameterCannotHaveDefault(
                defaultedParameter.Symbol.Name,
                defaultedParameter.Syntax.DefaultValue!.GetLocation());
        }

        var targetParameters = parameters
            .Where(static parameter => parameter.Symbol.MacroRole == MacroParameterRole.AttachedTarget)
            .ToArray();

        foreach (var defaultedParameter in targetParameters
            .Where(static parameter => parameter.Syntax.DefaultValue is not null))
        {
            _declarationDiagnostics.ReportMacroTargetParameterCannotHaveDefault(
                defaultedParameter.Symbol.Name,
                defaultedParameter.Syntax.DefaultValue!.GetLocation());
        }

        if (targetParameters.Length > 1)
        {
            _declarationDiagnostics.ReportMultipleMacroTargetParameters(
                declaration.Identifier.ValueText,
                targetParameters[1].Syntax.OnKeyword.GetLocation());
        }

        if (targetParameters.Length > 0 &&
            tokenStreamParameters.FirstOrDefault().Syntax is { } tokenStreamParameter)
        {
            _declarationDiagnostics.ReportMacroTokenStreamCannotBeAttached(
                declaration.Identifier.ValueText,
                tokenStreamParameter.TypeAnnotation!.Type.GetLocation());
        }

        if (tokenStreamParameters.Length > 1)
        {
            _declarationDiagnostics.ReportMultipleMacroTokenStreamParameters(
                declaration.Identifier.ValueText,
                tokenStreamParameters[1].Syntax.TypeAnnotation!.Type.GetLocation());
        }

        foreach (var expressionParameter in parameters
            .Where(static parameter =>
                parameter.Symbol.MacroRole == MacroParameterRole.SyntaxInput &&
                parameter.Syntax.DefaultValue is not null))
        {
            _declarationDiagnostics.ReportExpressionSyntaxMacroParameterCannotHaveDefault(
                expressionParameter.Symbol.Name,
                expressionParameter.Syntax.DefaultValue!.GetLocation());
        }

        foreach (var contextParameter in parameters.Where(static parameter =>
            parameter.Symbol.MacroRole == MacroParameterRole.Context))
        {
            var contextKind = MacroParameterRoleFacts.GetContextKind(contextParameter.Symbol.Type);
            var requiredMacroKind = contextKind switch
            {
                MacroContextKind.Attached => "attached",
                MacroContextKind.TokenTree => "token-tree macro",
                _ => "argument-style macro"
            };
            var isValid = contextKind switch
            {
                MacroContextKind.Attached => targetParameters.Length > 0,
                MacroContextKind.TokenTree => targetParameters.Length == 0,
                MacroContextKind.Freestanding => targetParameters.Length == 0 &&
                    tokenStreamParameters.Length == 0 &&
                    parameters.All(static parameter =>
                        MacroParameterRoleFacts.GetContextKind(parameter.Symbol.Type) != MacroContextKind.TokenTree),
                _ => true
            };

            if (!isValid)
            {
                _declarationDiagnostics.ReportMacroContextParameterKindMismatch(
                    contextParameter.Symbol.Name,
                    contextParameter.Symbol.Type.Name,
                    requiredMacroKind,
                    contextParameter.Syntax.TypeAnnotation!.Type.GetLocation());
            }
        }
    }

    private ITypeSymbol GetMacroTargetSyntaxType(MacroTarget target)
    {
        var metadataName = target switch
        {
            MacroTarget.Type => "Raven.CodeAnalysis.Syntax.BaseTypeDeclarationSyntax",
            MacroTarget.Method => "Raven.CodeAnalysis.Syntax.MethodDeclarationSyntax",
            MacroTarget.Property => "Raven.CodeAnalysis.Syntax.PropertyDeclarationSyntax",
            MacroTarget.Field => "Raven.CodeAnalysis.Syntax.FieldDeclarationSyntax",
            MacroTarget.Event => "Raven.CodeAnalysis.Syntax.EventDeclarationSyntax",
            MacroTarget.Parameter => "Raven.CodeAnalysis.Syntax.ParameterSyntax",
            MacroTarget.Accessor => "Raven.CodeAnalysis.Syntax.AccessorDeclarationSyntax",
            MacroTarget.Constructor => "Raven.CodeAnalysis.Syntax.ConstructorDeclarationSyntax",
            _ => "Raven.CodeAnalysis.Syntax.SyntaxNode"
        };

        return Compilation.GetTypeByMetadataName(metadataName)
            ?? Compilation.ErrorTypeSymbol;
    }

    private MacroTarget GetMacroTarget(ITypeSymbol type)
    {
        var unionCaseType = Compilation.GetTypeByMetadataName(
            "Raven.CodeAnalysis.Syntax.CaseDeclarationSyntax");
        if (unionCaseType is not null &&
            (SymbolEqualityComparer.Default.Equals(type, unionCaseType) ||
             type.IsDerivedFrom(unionCaseType)))
        {
            return MacroTarget.Type;
        }

        foreach (var target in new[]
        {
            MacroTarget.Type,
            MacroTarget.Method,
            MacroTarget.Property,
            MacroTarget.Field,
            MacroTarget.Event,
            MacroTarget.Parameter,
            MacroTarget.Accessor,
            MacroTarget.Constructor
        })
        {
            var targetType = GetMacroTargetSyntaxType(target);
            if (SymbolEqualityComparer.Default.Equals(type, targetType) ||
                type.IsDerivedFrom(targetType))
            {
                return target;
            }
        }

        return MacroTarget.None;
    }

    private MacroInvocationTargets GetMacroInvocationTargets(
        ITypeSymbol type,
        TypeSyntax? syntax = null)
    {
        if (MacroParameterRoleFacts.IsExpressionSyntaxFacade(type))
            return MacroInvocationTargets.Expression;

        if (type is INamedTypeSymbol { TypeArguments.Length: 1 } namedType &&
            Compilation.GetTypeByMetadataName(
                "Raven.CodeAnalysis.Syntax.SyntaxList`1") is INamedTypeSymbol syntaxListType &&
            SymbolEqualityComparer.Default.Equals(
                namedType.OriginalDefinition,
                syntaxListType) &&
            IsMacroSyntaxType(
                namedType.TypeArguments[0],
                "Raven.CodeAnalysis.Syntax.MemberDeclarationSyntax"))
        {
            return MacroInvocationTargets.NamespaceMember | MacroInvocationTargets.TypeMember;
        }

        if (syntax is UnionTypeSyntax && type is INamedTypeSymbol inlineUnion)
        {
            var targets = MacroInvocationTargets.None;
            foreach (var memberType in inlineUnion.TypeArguments)
                targets |= GetMacroInvocationTargets(memberType);

            return targets;
        }

        if (type is IUnionSymbol union)
        {
            var targets = MacroInvocationTargets.None;
            foreach (var memberType in union.MemberTypes)
                targets |= GetMacroInvocationTargets(memberType);

            if (targets != MacroInvocationTargets.None)
                return targets;
        }

        if (IsExactMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.SyntaxNode"))
            return MacroInvocationTargets.AllSingleNode;
        if (IsMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.ExpressionSyntax"))
            return MacroInvocationTargets.Expression;
        if (IsMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.StatementSyntax"))
            return MacroInvocationTargets.Statement;
        if (IsMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.MemberDeclarationSyntax"))
            return MacroInvocationTargets.NamespaceMember | MacroInvocationTargets.TypeMember;
        if (IsMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.TypeSyntax"))
            return MacroInvocationTargets.Type;
        if (IsMacroSyntaxType(type, "Raven.CodeAnalysis.Syntax.PatternSyntax"))
            return MacroInvocationTargets.Pattern;

        return MacroInvocationTargets.None;
    }

    private bool IsMacroSyntaxType(ITypeSymbol type, string metadataName)
    {
        var categoryType = Compilation.GetTypeByMetadataName(metadataName);
        return categoryType is not null &&
            (SymbolEqualityComparer.Default.Equals(type, categoryType) ||
             type.IsDerivedFrom(categoryType));
    }

    private bool IsExactMacroSyntaxType(ITypeSymbol type, string metadataName)
    {
        var categoryType = Compilation.GetTypeByMetadataName(metadataName);
        return categoryType is not null &&
            SymbolEqualityComparer.Default.Equals(type, categoryType);
    }

    private void ValidateMacroContributionStatements(
        MacroDeclarationSyntax declaration,
        SourceMacroSymbol symbol)
    {
        foreach (var contribution in declaration.DescendantNodes()
            .Select(static node => node switch
            {
                MacroExpansionStatementSyntax statement =>
                    (Node: (SyntaxNode)statement, statement.Keyword),
                MacroExpansionExpressionSyntax expression =>
                    (Node: (SyntaxNode)expression, expression.Keyword),
                _ => (Node: (SyntaxNode?)null, Keyword: default)
            })
            .Where(static contribution => contribution.Node is not null))
        {
            var instruction = contribution.Keyword.ValueText;
            var valid = symbol.MacroKind switch
            {
                MacroKind.Freestanding => instruction == "expand" ||
                    (instruction is "fragment" or "token") && symbol.Parameters.Any(static parameter =>
                        parameter.MacroRole is MacroParameterRole.TokenBody or MacroParameterRole.Context),
                MacroKind.AttachedDeclaration => instruction is "expand" or "replace" or "introduce",
                _ => false
            };

            if (!valid)
            {
                _declarationDiagnostics.ReportInvalidMacroContribution(
                    instruction,
                    symbol.MacroKind == MacroKind.Freestanding
                        ? "freestanding"
                        : "attached",
                    contribution.Keyword.GetLocation());
            }
        }
    }

    private void ValidateMacroCapabilityClauses(
        MacroDeclarationSyntax declaration,
        SourceMacroSymbol symbol)
    {
        var hasTokenTreeInput = symbol.Parameters.Any(static parameter =>
            parameter.MacroRole == MacroParameterRole.TokenBody ||
            parameter.MacroRole == MacroParameterRole.Context &&
                MacroParameterRoleFacts.GetContextKind(parameter.Type) == MacroContextKind.TokenTree);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clause in declaration.CapabilityClauses)
        {
            var capability = clause.CapabilityKeyword.ValueText;
            if (!seen.Add(capability))
            {
                _declarationDiagnostics.ReportDuplicateMacroCapability(
                    capability,
                    clause.CapabilityKeyword.GetLocation());
            }

            if (!hasTokenTreeInput)
            {
                _declarationDiagnostics.ReportMacroCapabilityRequiresTokenTree(
                    capability,
                    clause.CapabilityKeyword.GetLocation());
            }
        }
    }

    private void ReportUnsupportedMacroAsyncSyntax(MacroDeclarationSyntax declaration)
    {
        foreach (var modifier in declaration.Modifiers)
        {
            if (modifier.Kind == SyntaxKind.AsyncKeyword)
            {
                _declarationDiagnostics.ReportMacroCannotBeAsync(
                    declaration.Identifier.ValueText,
                    modifier.GetLocation());
            }
        }

        foreach (var awaitExpression in declaration.DescendantNodes()
            .OfType<PrefixOperatorExpressionSyntax>()
            .Where(static expression => expression.Kind == SyntaxKind.AwaitExpression))
        {
            _declarationDiagnostics.ReportAwaitNotSupportedInMacro(
                awaitExpression.OperatorToken.GetLocation());
        }
    }

    private void ResolveMacroConstraintTypes(SourceMacroSymbol symbol)
    {
        foreach (var typeParameter in symbol.TypeParameters.OfType<SourceTypeParameterSymbol>())
        {
            var constraints = ImmutableArray.CreateBuilder<ITypeSymbol>();
            foreach (var reference in typeParameter.ConstraintTypeReferences)
            {
                if (reference.GetSyntax() is not TypeConstraintSyntax typeConstraint)
                    continue;

                constraints.Add(MemberSignatureDeclarationPass.ResolveSkeletonType(
                    this,
                    typeConstraint.Type,
                    Compilation.ErrorTypeSymbol,
                    symbol.DefinitionType,
                    symbol.TypeParameters));
            }

            typeParameter.SetConstraintTypes(constraints.ToImmutable());
        }
    }

    private static bool HaveSameMacroSignature(
        IMacroDeclarationSymbol first,
        IMacroDeclarationSymbol second)
    {
        if (first.Parameters.Length != second.Parameters.Length ||
            first.TypeParameters.Length != second.TypeParameters.Length)
        {
            return false;
        }

        for (var i = 0; i < first.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(first.Parameters[i].Type, second.Parameters[i].Type))
                return false;
        }

        return true;
    }

    private SourceNamedTypeSymbol GetOrCreateNamespaceMembersContainer(SyntaxNode declaration, INamespaceSymbol parentNamespace)
    {
        var sourceNamespace = parentNamespace.AsSourceNamespace()
            ?? throw new InvalidOperationException("Top-level members require a source namespace.");

        return Compilation.GetOrCreateNamespaceMembersContainer(sourceNamespace, declaration);
    }

    private void DeclareTopLevelFunctionSymbol(FunctionStatementSyntax functionStatement, INamespaceSymbol parentNamespace)
    {
        if (!Compilation.Options.AllowNamespaceMembers)
        {
            _declarationDiagnostics.Report(Diagnostic.Create(s_namespaceMembersDisabled, functionStatement.Identifier.GetLocation()));
            return;
        }

        ReportInvalidTopLevelFunctionModifiers(functionStatement);

        var container = GetOrCreateNamespaceMembersContainer(functionStatement, parentNamespace);

        if (container.GetMembers(functionStatement.Identifier.ValueText)
            .OfType<IMethodSymbol>()
            .Any(method => method.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() == functionStatement)))
        {
            return;
        }

        var isAsync = functionStatement.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.AsyncKeyword);
        var isExtern = functionStatement.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.ExternKeyword);
        if (isExtern && (functionStatement.Body is not null || functionStatement.ExpressionBody is not null))
        {
            _declarationDiagnostics.ReportExternMemberCannotHaveBody(
                functionStatement.Identifier.ValueText,
                functionStatement.Identifier.GetLocation());
        }

        ITypeSymbol returnType = isAsync
            ? Compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task)
            : Compilation.GetSpecialType(SpecialType.System_Unit);
        var accessibility = Compilation.IsSubmission
            ? AccessibilityUtilities.DetermineAccessibility(functionStatement.Modifiers, Accessibility.Public)
            : DetermineNamespaceMemberAccessibility(functionStatement.Modifiers);

        var methodSymbol = new SourceMethodSymbol(
            functionStatement.Identifier.ValueText,
            returnType,
            ImmutableArray<SourceParameterSymbol>.Empty,
            container,
            container,
            container.ContainingNamespace,
            [functionStatement.GetLocation()],
            [functionStatement.GetReference()],
            isStatic: true,
            isAsync: isAsync,
            isExtern: isExtern,
            declaredAccessibility: accessibility);

        TypeParameterInitializer.InitializeMethodTypeParameters(
            methodSymbol,
            container,
            functionStatement.TypeParameterList,
            functionStatement.ConstraintClauses,
            functionStatement.SyntaxTree,
            _declarationDiagnostics);

        if (functionStatement.ReturnType is { } returnTypeSyntax)
        {
            returnType = MemberSignatureDeclarationPass.ResolveSkeletonType(
                this,
                returnTypeSyntax.Type,
                returnType,
                container,
                methodSymbol.TypeParameters);
            methodSymbol.SetReturnType(returnType);

            if (isAsync && !AsyncReturnTypeUtilities.IsValidAsyncReturnType(returnType, allowErrorType: false))
            {
                var display = returnType.TypeKind == TypeKind.Error
                    ? returnTypeSyntax.Type.ToString()
                    : returnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var suggestedReturnType = AsyncReturnTypeUtilities.GetSuggestedAsyncReturnTypeDisplay(Compilation, returnType);
                _declarationDiagnostics.ReportAsyncReturnTypeMustBeTaskLike(
                    display,
                    suggestedReturnType,
                    returnTypeSyntax.Type.GetLocation());
            }
        }

        var parameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>();
        if (functionStatement.ParameterList is not null)
        {
            foreach (var parameter in functionStatement.ParameterList.Parameters)
                parameters.Add(MemberSignatureDeclarationPass.CreateSkeletonParameterSymbol(this, parameter, methodSymbol, container));
        }

        methodSymbol.SetParameters(parameters.ToImmutable());
        methodSymbol.MarkSignatureSkeleton();

        if (HasFileScopeModifier(functionStatement.Modifiers))
            methodSymbol.MarkFileScoped(functionStatement.SyntaxTree?.FilePath);

        RegisterMethodSymbol(functionStatement, methodSymbol);
        if (container.GetMembers(functionStatement.Identifier.ValueText)
            .OfType<IMethodSymbol>()
            .Any(existing =>
                !existing.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() == functionStatement) &&
                HaveSameSignature(existing, methodSymbol)))
        {
            _declarationDiagnostics.ReportFunctionAlreadyDefined(
                methodSymbol.Name,
                functionStatement.Identifier.GetLocation());
        }

        container.AddMember(methodSymbol);
    }

    private static bool HaveSameSignature(IMethodSymbol first, IMethodSymbol second)
    {
        if (first.Parameters.Length != second.Parameters.Length ||
            first.TypeParameters.Length != second.TypeParameters.Length)
        {
            return false;
        }

        for (var i = 0; i < first.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(first.Parameters[i].Type, second.Parameters[i].Type))
                return false;
        }

        return true;
    }

    private static Accessibility DetermineNamespaceMemberAccessibility(SyntaxTokenList modifiers)
        => AccessibilityUtilities.DetermineAccessibility(modifiers, Accessibility.Internal);

    private void ReportInvalidTopLevelFunctionModifiers(FunctionStatementSyntax functionStatement)
    {
        foreach (var modifier in functionStatement.Modifiers)
        {
            if (modifier.Kind is SyntaxKind.PublicKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.FileprivateKeyword
                or SyntaxKind.AsyncKeyword
                or SyntaxKind.ExternKeyword
                or SyntaxKind.UnsafeKeyword)
            {
                continue;
            }

            _declarationDiagnostics.ReportModifierNotValidOnMember(
                modifier.Text,
                "top-level function",
                functionStatement.Identifier.ValueText,
                modifier.GetLocation());
        }
    }

    private SourceNamedTypeSymbol DeclareClassSymbol(
        TypeDeclarationSyntax classDecl,
        INamespaceSymbol parentNamespace,
        INamedTypeSymbol? objectType)
    {
        var isPartial = classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
        if (TryReuseDeclaredTypeSymbol(
            classDecl,
            classDecl.Identifier.ValueText,
            supportsPartial: true,
            isPartial,
            classDecl.Identifier.GetLocation(),
            out var alreadyDeclaredType))
        {
            return alreadyDeclaredType;
        }

        ReportInvalidTypeModifiers(classDecl, isNestedType: false, _declarationDiagnostics);
        ReportRedundantTypeModifiers(classDecl, _declarationDiagnostics);

        var declaredTypeKind = IsStructLikeNominalType(classDecl)
            ? TypeKind.Struct
            : TypeKind.Class;
        var defaultBaseType = declaredTypeKind == TypeKind.Struct
            ? Compilation.TryGetMetadataReferenceTypeByMetadataName("System.ValueType")
            : objectType;

        var hasSealedModifier = classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);
        var isStatic = classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
        var isAbstract = isStatic || classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword) || hasSealedModifier;
        var isSealedHierarchy = hasSealedModifier && !isStatic;
        var isSealed = isStatic || (!isSealedHierarchy && !classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.OpenKeyword) && !isAbstract);
        var hasPermitsClause = GetPermitsClause(classDecl) is not null;

        if (hasPermitsClause && !hasSealedModifier)
        {
            _declarationDiagnostics.ReportPermitsClauseRequiresSealed(
                classDecl.Identifier.GetLocation());
        }
        var isRecord = classDecl is RecordDeclarationSyntax || classDecl.Modifiers.Any(m => m.Kind == SyntaxKind.RecordKeyword);
        var isFileScoped = HasFileScopeModifier(classDecl.Modifiers);
        var typeAccessibility = AccessibilityUtilities.DetermineAccessibility(
            classDecl.Modifiers,
            Compilation.IsSubmission ? Accessibility.Public : Accessibility.Internal);

        var declarationLocation = classDecl.GetLocation();
        var declarationReference = classDecl.GetReference();

        var parentSourceNamespace = parentNamespace.AsSourceNamespace();
        SourceNamedTypeSymbol classSymbol;
        var isNewSymbol = true;

        ReportExternalTypeRedeclaration(
            parentNamespace,
            classDecl.Identifier,
            GetTypeParameterList(classDecl)?.Parameters.Count ?? 0,
            _declarationDiagnostics);

        var existingType = parentSourceNamespace is not null
            ? FindExistingDeclaredType(parentSourceNamespace, classDecl.Identifier.ValueText, declaredTypeKind, GetDeclaredTypeParameterArity(classDecl))
            : null;

        if (existingType is not null)
        {
            var hadPartial = existingType.HasPartialModifier;
            var hadNonPartial = existingType.HasNonPartialDeclaration;
            var previouslyMixed = hadPartial && hadNonPartial;
            var willBeMixed = (hadPartial || isPartial) && (hadNonPartial || !isPartial);

            if (willBeMixed && !previouslyMixed)
            {
                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                    classDecl.Identifier.ValueText,
                    classDecl.Identifier.GetLocation());
            }
            else if (hadNonPartial && !isPartial)
            {
                _declarationDiagnostics.ReportTypeAlreadyDefined(
                    classDecl.Identifier.ValueText,
                    classDecl.Identifier.GetLocation());
            }

            existingType.AddDeclaration(declarationLocation, declarationReference);
            existingType.UpdateDeclarationModifiers(isSealed, isAbstract, isStatic);
            existingType.RegisterPartialModifier(isPartial);
            existingType.RegisterRecordModifier(isRecord);
            ReportPartialTypeCompatibility(existingType, classDecl, typeAccessibility, isFileScoped, _declarationDiagnostics);

            if (isFileScoped)
                existingType.MarkFileScoped(classDecl.SyntaxTree.FilePath);

            if (isSealedHierarchy)
                existingType.MarkAsSealedHierarchy(classDecl.SyntaxTree.FilePath, hasPermitsClause);

            classSymbol = existingType;
            isNewSymbol = false;
        }
        else
        {
            classSymbol = new SourceNamedTypeSymbol(
                classDecl.Identifier.ValueText,
                defaultBaseType!,
                declaredTypeKind,
                parentNamespace.AsSourceNamespace(),
                null,
                parentNamespace.AsSourceNamespace(),
                new[] { declarationLocation },
                new[] { declarationReference },
                isSealed,
                isAbstract,
                isStatic,
                declaredAccessibility: typeAccessibility,
                metadataName: GetFileScopedMetadataName(classDecl, classDecl.Identifier.ValueText));

            classSymbol.RegisterPartialModifier(isPartial);
            classSymbol.RegisterRecordModifier(isRecord);

            if (isFileScoped)
                classSymbol.MarkFileScoped(classDecl.SyntaxTree.FilePath);

            if (isSealedHierarchy)
                classSymbol.MarkAsSealedHierarchy(classDecl.SyntaxTree.FilePath, hasPermitsClause);
        }

        if (isNewSymbol)
            InitializeTypeParameters(classSymbol, GetTypeParameterList(classDecl), GetConstraintClauses(classDecl));

        RegisterDeclaredTypeSymbol(classDecl, classSymbol);

        return classSymbol;
    }

    private bool TryReuseDeclaredTypeSymbol(
        SyntaxNode declaration,
        string name,
        bool supportsPartial,
        bool isPartial,
        Location diagnosticLocation,
        [NotNullWhen(true)] out SourceNamedTypeSymbol? symbol)
    {
        symbol = null;

        if (!Compilation.TryGetDeclaredTypeSymbol(declaration, out var declaredType))
            return false;

        symbol = declaredType;

        if (IsPrimaryDeclaration(declaredType, declaration))
            return true;

        if (supportsPartial && (declaredType.HasPartialModifier || isPartial))
        {
            if (declaredType.HasPartialModifier && declaredType.HasNonPartialDeclaration)
            {
                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                    name,
                    diagnosticLocation);
            }

            return true;
        }

        _declarationDiagnostics.ReportTypeAlreadyDefined(name, diagnosticLocation);
        return true;
    }

    private static bool IsPrimaryDeclaration(SourceNamedTypeSymbol symbol, SyntaxNode declaration)
        => symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is { } primaryDeclaration &&
           IsSameDeclarationSyntax(primaryDeclaration, declaration);

    private static bool IsSameDeclarationSyntax(SyntaxNode left, SyntaxNode right)
        => left.SyntaxTree == right.SyntaxTree &&
           left.Span == right.Span &&
           left.Kind == right.Kind;

    private void ReportPartialTypeCompatibility(
        SourceNamedTypeSymbol existingType,
        TypeDeclarationSyntax declaration,
        Accessibility declaredAccessibility,
        bool isFileScoped,
        DiagnosticBag diagnostics)
    {
        if (existingType.DeclaredAccessibility != declaredAccessibility)
        {
            diagnostics.ReportPartialTypeDeclarationAccessibilityMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (existingType.IsFileScoped != isFileScoped)
        {
            diagnostics.ReportPartialTypeDeclarationFileScopeMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (isFileScoped &&
            existingType.IsFileScoped &&
            !existingType.IsAccessibleFromFile(declaration.SyntaxTree.FilePath))
        {
            diagnostics.ReportFileScopedPartialTypeMustRemainInSingleFile(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (!DoTypeParametersMatch(existingType, GetTypeParameterList(declaration)))
        {
            diagnostics.ReportPartialTypeDeclarationTypeParametersMustMatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        var refModifierStates = existingType.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Select(typeDeclaration => typeDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword)))
            .Distinct()
            .Take(2)
            .Count();
        if (refModifierStates > 1)
        {
            diagnostics.ReportPartialTypeDeclarationRefModifierMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        var readonlyModifierStates = existingType.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Select(typeDeclaration => typeDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ReadonlyKeyword)))
            .Distinct()
            .Take(2)
            .Count();
        if (readonlyModifierStates > 1)
        {
            diagnostics.ReportPartialTypeDeclarationReadonlyModifierMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }
    }

    private void ReportPartialTypeCompatibility(
        SourceNamedTypeSymbol existingType,
        UnionDeclarationSyntax declaration,
        Accessibility declaredAccessibility,
        bool isFileScoped,
        DiagnosticBag diagnostics)
    {
        if (existingType.DeclaredAccessibility != declaredAccessibility)
        {
            diagnostics.ReportPartialTypeDeclarationAccessibilityMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (existingType.IsFileScoped != isFileScoped)
        {
            diagnostics.ReportPartialTypeDeclarationFileScopeMismatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (isFileScoped &&
            existingType.IsFileScoped &&
            !existingType.IsAccessibleFromFile(declaration.SyntaxTree.FilePath))
        {
            diagnostics.ReportFileScopedPartialTypeMustRemainInSingleFile(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }

        if (!DoTypeParametersMatch(existingType, declaration.TypeParameterList))
        {
            diagnostics.ReportPartialTypeDeclarationTypeParametersMustMatch(
                declaration.Identifier.ValueText,
                declaration.Identifier.GetLocation());
        }
    }

    private static bool DoTypeParametersMatch(
        SourceNamedTypeSymbol existingType,
        TypeParameterListSyntax? typeParameterList)
    {
        var existingParameters = existingType.TypeParameters;
        var declaredCount = typeParameterList?.Parameters.Count ?? 0;

        if (existingParameters.Length != declaredCount)
            return false;

        if (typeParameterList is null)
            return existingParameters.Length == 0;

        for (var i = 0; i < existingParameters.Length; i++)
        {
            if (!string.Equals(existingParameters[i].Name, typeParameterList.Parameters[i].Identifier.ValueText, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int GetDeclaredTypeParameterArity(SyntaxNode declaration)
    {
        if (declaration is InterfaceDeclarationSyntax interfaceDeclaration)
            return interfaceDeclaration.TypeParameterList?.Parameters.Count ?? 0;

        if (declaration is TypeDeclarationSyntax typeDeclaration)
            return GetTypeParameterList(typeDeclaration)?.Parameters.Count ?? 0;

        return 0;
    }

    private static SourceNamedTypeSymbol? FindExistingDeclaredType(
        INamespaceOrTypeSymbol container,
        string name,
        TypeKind typeKind,
        int arity)
    {
        var members = container is SourceNamedTypeSymbol sourceType
            ? sourceType.GetDeclaredMembersWithoutEnsuring(name)
            : container.GetMembers(name);

        return members
            .OfType<SourceNamedTypeSymbol>()
            .FirstOrDefault(type => type.TypeKind == typeKind && type.Arity == arity);
    }

    private readonly record struct EffectiveMemberDeclaration(
        MemberDeclarationSyntax EffectiveSyntax,
        MemberDeclarationSyntax? OriginalSyntax = null);

    private ImmutableArray<EffectiveMemberDeclaration> GetEffectiveTypeMembers(TypeDeclarationSyntax typeDeclaration)
    {
        var builder = ImmutableArray.CreateBuilder<EffectiveMemberDeclaration>();

        foreach (var member in typeDeclaration.Members)
            AppendExpandedMember(builder, member);

        foreach (var attribute in typeDeclaration.AttributeLists.SelectMany(static list => list.Attributes))
        {
            if (!attribute.IsMacroAttribute())
                continue;

            var expansion = GetMacroExpansion(attribute);
            if (expansion is null)
                continue;

            foreach (var introducedMember in expansion.IntroducedMembers)
            {
                RegisterMacroContainingTypeSyntax(introducedMember, typeDeclaration);
                builder.Add(new EffectiveMemberDeclaration(introducedMember));
            }
        }

        return builder.ToImmutable();
    }

    private void AppendExpandedMember(
        ImmutableArray<EffectiveMemberDeclaration>.Builder builder,
        MemberDeclarationSyntax member)
    {
        if (member is FreestandingMacroMemberDeclarationSyntax or FreestandingMacroDeclarationSyntax)
        {
            var expansion = GetFreestandingMacroExpansion(member, CancellationToken.None);
            if (expansion is null || (!expansion.HasMemberExpansion && expansion.Node is null))
            {
                builder.Add(new EffectiveMemberDeclaration(member));
                return;
            }

            var expandedMembers = expansion.HasMemberExpansion
                ? expansion.Members
                : expansion.Node is MemberDeclarationSyntax expandedMember
                    ? ImmutableArray.Create(expandedMember)
                    : ImmutableArray<MemberDeclarationSyntax>.Empty;
            if (expandedMembers.Length > 0)
                RegisterMacroReplacementSyntaxTrees(member, expandedMembers);

            var containingType = (TypeDeclarationSyntax)member.Parent!;
            foreach (var generatedMember in expandedMembers)
            {
                RegisterMacroContainingTypeSyntax(generatedMember, containingType);
                builder.Add(new EffectiveMemberDeclaration(generatedMember));
            }

            return;
        }

        MemberDeclarationSyntax effectiveMember = member;
        var peerDeclarations = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();

        foreach (var attribute in member.AttributeLists.SelectMany(static list => list.Attributes))
        {
            if (!attribute.IsMacroAttribute())
                continue;

            var expansion = GetMacroExpansion(attribute);
            if (expansion is null)
                continue;

            foreach (var introducedMember in expansion.IntroducedMembers)
            {
                RegisterMacroContainingTypeSyntax(introducedMember, containingTypeDeclaration: (TypeDeclarationSyntax)member.Parent!);
                builder.Add(new EffectiveMemberDeclaration(introducedMember));
            }

            if (expansion.ReplacementDeclaration is MemberDeclarationSyntax replacementMember)
            {
                effectiveMember = replacementMember;
                RegisterMacroReplacementSyntaxTrees(
                    member,
                    [replacementMember, .. expansion.IntroducedMembers, .. expansion.PeerDeclarations]);
                RegisterMacroContainingTypeSyntax(replacementMember, containingTypeDeclaration: (TypeDeclarationSyntax)member.Parent!);
            }

            foreach (var peerDeclaration in expansion.PeerDeclarations)
            {
                RegisterMacroContainingTypeSyntax(peerDeclaration, containingTypeDeclaration: (TypeDeclarationSyntax)member.Parent!);
                peerDeclarations.Add(peerDeclaration);
            }
        }

        builder.Add(new EffectiveMemberDeclaration(effectiveMember, member));

        foreach (var peerDeclaration in peerDeclarations)
            builder.Add(new EffectiveMemberDeclaration(peerDeclaration));
    }

    private void RegisterMacroContainingTypeSyntax(MemberDeclarationSyntax member, BaseTypeDeclarationSyntax containingTypeDeclaration)
    {
        if (member.Parent is BaseTypeDeclarationSyntax generatedContainingType)
            RegisterMacroContainingTypeSyntax(generatedContainingType, containingTypeDeclaration);
    }

    private void DeclareClassMemberTypes(TypeDeclarationSyntax classDecl, SourceNamedTypeSymbol classSymbol)
    {
        var objectType = Compilation.TryGetMetadataReferenceTypeByMetadataName("System.Object");
        var valueType = Compilation.TryGetMetadataReferenceTypeByMetadataName("System.ValueType");
        var parentType = (INamedTypeSymbol)classSymbol;

        foreach (var effectiveMember in GetEffectiveTypeMembers(classDecl))
        {
            var member = effectiveMember.EffectiveSyntax;
            switch (member)
            {
                case TypeDeclarationSyntax nestedClass when nestedClass is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax:
                    {
                        var nestedPartial = nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
                        if (TryReuseDeclaredTypeSymbol(
                            nestedClass,
                            nestedClass.Identifier.ValueText,
                            supportsPartial: true,
                            nestedPartial,
                            nestedClass.Identifier.GetLocation(),
                            out var alreadyDeclaredNestedType))
                        {
                            DeclareClassMemberTypes(nestedClass, alreadyDeclaredNestedType);
                            break;
                        }

                        ReportInvalidTypeModifiers(nestedClass, isNestedType: true, _declarationDiagnostics);
                        ReportRedundantPublicTypeModifierIfNeeded(nestedClass, _declarationDiagnostics);
                        ReportRedundantTypeModifiers(nestedClass, _declarationDiagnostics);

                        var nestedTypeKind = IsStructLikeNominalType(nestedClass)
                            ? TypeKind.Struct
                            : TypeKind.Class;
                        var nestedBaseType = nestedTypeKind == TypeKind.Struct ? valueType : objectType;
                        var nestedHasSealedModifier = nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);
                        var nestedStatic = nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
                        var nestedAbstract = nestedStatic || nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword) || nestedHasSealedModifier;
                        var nestedIsSealedHierarchy = nestedHasSealedModifier && !nestedStatic;
                        var nestedSealed = nestedStatic || (!nestedIsSealedHierarchy && !nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.OpenKeyword) && !nestedAbstract);
                        var nestedRecord = nestedClass is RecordDeclarationSyntax || nestedClass.Modifiers.Any(m => m.Kind == SyntaxKind.RecordKeyword);
                        var nestedHasPermitsClause = GetPermitsClause(nestedClass) is not null;
                        var nestedFileScoped = HasFileScopeModifier(nestedClass.Modifiers);
                        var nestedAccessibility = AccessibilityUtilities.DetermineAccessibility(
                            nestedClass.Modifiers,
                            AccessibilityUtilities.GetDefaultTypeAccessibility(parentType));

                        var nestedLocation = nestedClass.GetLocation();
                        var nestedReference = nestedClass.GetReference();

                        SourceNamedTypeSymbol nestedSymbol;
                        var isNewNestedSymbol = true;

                        var existingNested = FindExistingDeclaredType(
                            parentType,
                            nestedClass.Identifier.ValueText,
                            nestedTypeKind,
                            GetDeclaredTypeParameterArity(nestedClass));

                        if (existingNested is not null)
                        {
                            var hadPartial = existingNested.HasPartialModifier;
                            var hadNonPartial = existingNested.HasNonPartialDeclaration;
                            var previouslyMixed = hadPartial && hadNonPartial;
                            var willBeMixed = (hadPartial || nestedPartial) && (hadNonPartial || !nestedPartial);

                            if (willBeMixed && !previouslyMixed)
                            {
                                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                                    nestedClass.Identifier.ValueText,
                                    nestedClass.Identifier.GetLocation());
                            }
                            else if (hadNonPartial && !nestedPartial)
                            {
                                _declarationDiagnostics.ReportTypeAlreadyDefined(
                                    nestedClass.Identifier.ValueText,
                                    nestedClass.Identifier.GetLocation());
                            }

                            existingNested.AddDeclaration(nestedLocation, nestedReference);
                            existingNested.UpdateDeclarationModifiers(nestedSealed, nestedAbstract, nestedStatic);
                            existingNested.RegisterPartialModifier(nestedPartial);
                            existingNested.RegisterRecordModifier(nestedRecord);
                            ReportPartialTypeCompatibility(existingNested, nestedClass, nestedAccessibility, nestedFileScoped, _declarationDiagnostics);

                            if (nestedFileScoped)
                                existingNested.MarkFileScoped(nestedClass.SyntaxTree.FilePath);
                            if (nestedHasPermitsClause && !nestedHasSealedModifier)
                            {
                                _declarationDiagnostics.ReportPermitsClauseRequiresSealed(
                                    nestedClass.Identifier.GetLocation());
                            }
                            if (nestedIsSealedHierarchy)
                                existingNested.MarkAsSealedHierarchy(nestedClass.SyntaxTree.FilePath, nestedHasPermitsClause);

                            nestedSymbol = existingNested;
                            isNewNestedSymbol = false;
                        }
                        else
                        {
                            nestedSymbol = new SourceNamedTypeSymbol(
                                nestedClass.Identifier.ValueText,
                                nestedBaseType!,
                                nestedTypeKind,
                                parentType,
                                parentType,
                                classSymbol.ContainingNamespace,
                                [nestedLocation],
                                [nestedReference],
                                nestedSealed,
                                nestedAbstract,
                                nestedStatic,
                                declaredAccessibility: nestedAccessibility,
                                metadataName: GetFileScopedMetadataName(nestedClass, nestedClass.Identifier.ValueText)
                            );

                            nestedSymbol.RegisterPartialModifier(nestedPartial);
                            nestedSymbol.RegisterRecordModifier(nestedRecord);

                            if (nestedFileScoped)
                                nestedSymbol.MarkFileScoped(nestedClass.SyntaxTree.FilePath);
                            if (nestedHasPermitsClause && !nestedHasSealedModifier)
                            {
                                _declarationDiagnostics.ReportPermitsClauseRequiresSealed(
                                    nestedClass.Identifier.GetLocation());
                            }
                            if (nestedIsSealedHierarchy)
                                nestedSymbol.MarkAsSealedHierarchy(nestedClass.SyntaxTree.FilePath, nestedHasPermitsClause);
                        }

                        if (isNewNestedSymbol)
                            InitializeTypeParameters(nestedSymbol, GetTypeParameterList(nestedClass), GetConstraintClauses(nestedClass));

                        RegisterDeclaredTypeSymbol(nestedClass, nestedSymbol);
                        DeclareClassMemberTypes(nestedClass, nestedSymbol);
                        break;
                    }

                case InterfaceDeclarationSyntax nestedInterface:
                    {
                        var nestedPartial = nestedInterface.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
                        if (TryReuseDeclaredTypeSymbol(
                            nestedInterface,
                            nestedInterface.Identifier.ValueText,
                            supportsPartial: true,
                            nestedPartial,
                            nestedInterface.Identifier.GetLocation(),
                            out var alreadyDeclaredNestedInterface))
                        {
                            DeclareClassMemberTypes(nestedInterface, alreadyDeclaredNestedInterface);
                            break;
                        }

                        ReportInvalidTypeModifiers(nestedInterface, isNestedType: true, _declarationDiagnostics);
                        ReportRedundantPublicTypeModifierIfNeeded(nestedInterface, _declarationDiagnostics);

                        var nestedHasSealedModifier = nestedInterface.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);
                        var nestedHasPermitsClause = nestedInterface.PermitsClause is not null;
                        if (nestedHasPermitsClause && !nestedHasSealedModifier)
                        {
                            _declarationDiagnostics.ReportPermitsClauseRequiresSealed(
                                nestedInterface.Identifier.GetLocation());
                        }

                        var nestedFileScoped = HasFileScopeModifier(nestedInterface.Modifiers);
                        var nestedAccessibility = AccessibilityUtilities.DetermineAccessibility(
                            nestedInterface.Modifiers,
                            AccessibilityUtilities.GetDefaultTypeAccessibility(parentType));
                        var existingNested = FindExistingDeclaredType(
                            parentType,
                            nestedInterface.Identifier.ValueText,
                            TypeKind.Interface,
                            GetDeclaredTypeParameterArity(nestedInterface));
                        SourceNamedTypeSymbol nestedInterfaceSymbol;

                        if (existingNested is not null)
                        {
                            var hadPartial = existingNested.HasPartialModifier;
                            var hadNonPartial = existingNested.HasNonPartialDeclaration;
                            var previouslyMixed = hadPartial && hadNonPartial;
                            var willBeMixed = (hadPartial || nestedPartial) && (hadNonPartial || !nestedPartial);

                            if (willBeMixed && !previouslyMixed)
                            {
                                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                                    nestedInterface.Identifier.ValueText,
                                    nestedInterface.Identifier.GetLocation());
                            }
                            else if (hadNonPartial && !nestedPartial)
                            {
                                _declarationDiagnostics.ReportTypeAlreadyDefined(
                                    nestedInterface.Identifier.ValueText,
                                    nestedInterface.Identifier.GetLocation());
                            }

                            existingNested.AddDeclaration(nestedInterface.GetLocation(), nestedInterface.GetReference());
                            existingNested.RegisterPartialModifier(nestedPartial);
                            ReportPartialTypeCompatibility(existingNested, nestedInterface, nestedAccessibility, nestedFileScoped, _declarationDiagnostics);

                            if (nestedFileScoped)
                                existingNested.MarkFileScoped(nestedInterface.SyntaxTree.FilePath);
                            if (nestedHasSealedModifier)
                                existingNested.MarkAsSealedHierarchy(nestedInterface.SyntaxTree.FilePath, nestedHasPermitsClause);
                            nestedInterfaceSymbol = existingNested;
                        }
                        else
                        {
                            nestedInterfaceSymbol = new SourceNamedTypeSymbol(
                                nestedInterface.Identifier.ValueText,
                                objectType!,
                                TypeKind.Interface,
                                parentType,
                                parentType,
                                classSymbol.ContainingNamespace,
                                [nestedInterface.GetLocation()],
                                [nestedInterface.GetReference()],
                                true,
                                isAbstract: true,
                                declaredAccessibility: nestedAccessibility,
                                metadataName: GetFileScopedMetadataName(nestedInterface, nestedInterface.Identifier.ValueText)
                            );

                            nestedInterfaceSymbol.RegisterPartialModifier(nestedPartial);

                            if (nestedFileScoped)
                                nestedInterfaceSymbol.MarkFileScoped(nestedInterface.SyntaxTree.FilePath);
                            if (nestedHasSealedModifier)
                                nestedInterfaceSymbol.MarkAsSealedHierarchy(nestedInterface.SyntaxTree.FilePath, nestedHasPermitsClause);
                            InitializeTypeParameters(nestedInterfaceSymbol, nestedInterface.TypeParameterList, nestedInterface.ConstraintClauses);
                        }

                        RegisterDeclaredTypeSymbol(nestedInterface, nestedInterfaceSymbol);
                        break;
                    }

                case EnumDeclarationSyntax enumDecl:
                    {
                        if (TryReuseDeclaredTypeSymbol(
                            enumDecl,
                            enumDecl.Identifier.ValueText,
                            supportsPartial: false,
                            isPartial: false,
                            enumDecl.Identifier.GetLocation(),
                            out _))
                        {
                            break;
                        }

                        ReportInvalidTypeModifiers(enumDecl, isNestedType: true, _declarationDiagnostics);
                        ReportRedundantPublicTypeModifierIfNeeded(enumDecl, _declarationDiagnostics);

                        var enumSymbol = new SourceNamedTypeSymbol(
                            enumDecl.Identifier.ValueText,
                            Compilation.GetSpecialType(SpecialType.System_Enum),
                            TypeKind.Enum,
                            parentType,
                            parentType,
                            classSymbol.ContainingNamespace,
                            [enumDecl.GetLocation()],
                            [enumDecl.GetReference()],
                            true,
                            declaredAccessibility: AccessibilityUtilities.DetermineAccessibility(
                                enumDecl.Modifiers,
                                AccessibilityUtilities.GetDefaultTypeAccessibility(parentType)),
                            metadataName: GetFileScopedMetadataName(enumDecl, enumDecl.Identifier.ValueText)
                        );

                        if (HasFileScopeModifier(enumDecl.Modifiers))
                            enumSymbol.MarkFileScoped(enumDecl.SyntaxTree.FilePath);

                        RegisterDeclaredTypeSymbol(enumDecl, enumSymbol);
                        break;
                    }

                case UnionDeclarationSyntax nestedUnion:
                    {
                        var nestedUnionPartial = nestedUnion.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
                        if (TryReuseDeclaredTypeSymbol(
                            nestedUnion,
                            nestedUnion.Identifier.ValueText,
                            supportsPartial: true,
                            nestedUnionPartial,
                            nestedUnion.Identifier.GetLocation(),
                            out _))
                        {
                            break;
                        }

                        ReportInvalidTypeModifiers(nestedUnion, isNestedType: true, _declarationDiagnostics);
                        ReportRedundantPublicTypeModifierIfNeeded(nestedUnion, _declarationDiagnostics);
                        var nestedUnionTypeKind = GetUnionTypeKind(nestedUnion);
                        var nestedUnionBaseType = GetUnionBaseType(nestedUnionTypeKind);

                        var unionSymbol = new SourceUnionSymbol(
                            nestedUnion.Identifier.ValueText,
                            nestedUnionBaseType,
                            nestedUnionTypeKind,
                            parentType,
                            parentType,
                            classSymbol.ContainingNamespace,
                            [nestedUnion.GetLocation()],
                            [nestedUnion.GetReference()],
                            AccessibilityUtilities.DetermineAccessibility(
                                nestedUnion.Modifiers,
                                AccessibilityUtilities.GetDefaultTypeAccessibility(parentType)),
                            metadataName: GetFileScopedMetadataName(nestedUnion, nestedUnion.Identifier.ValueText));

                        if (HasFileScopeModifier(nestedUnion.Modifiers))
                            unionSymbol.MarkFileScoped(nestedUnion.SyntaxTree.FilePath);

                        InitializeTypeParameters(unionSymbol, nestedUnion.TypeParameterList, nestedUnion.ConstraintClauses);
                        RegisterDeclaredTypeSymbol(nestedUnion, unionSymbol);
                        break;
                    }

                case DelegateDeclarationSyntax delegateDecl:
                    {
                        if (TryReuseDeclaredTypeSymbol(
                            delegateDecl,
                            delegateDecl.Identifier.ValueText,
                            supportsPartial: false,
                            isPartial: false,
                            delegateDecl.Identifier.GetLocation(),
                            out _))
                        {
                            break;
                        }

                        ReportInvalidTypeModifiers(delegateDecl, isNestedType: true, _declarationDiagnostics);
                        ReportRedundantPublicTypeModifierIfNeeded(delegateDecl, _declarationDiagnostics);

                        var delegateAccessibility = AccessibilityUtilities.DetermineAccessibility(
                            delegateDecl.Modifiers,
                            AccessibilityUtilities.GetDefaultTypeAccessibility(parentType));

                        var delegateSymbol = new SourceNamedTypeSymbol(
                            delegateDecl.Identifier.ValueText,
                            Compilation.GetSpecialType(SpecialType.System_MulticastDelegate)!,
                            TypeKind.Delegate,
                            parentType,
                            parentType,
                            classSymbol.ContainingNamespace,
                            [delegateDecl.GetLocation()],
                            [delegateDecl.GetReference()],
                            isSealed: true,
                            isAbstract: true,
                            isStatic: false,
                            declaredAccessibility: delegateAccessibility,
                            metadataName: GetFileScopedMetadataName(delegateDecl, delegateDecl.Identifier.ValueText));

                        if (HasFileScopeModifier(delegateDecl.Modifiers))
                            delegateSymbol.MarkFileScoped(delegateDecl.SyntaxTree.FilePath);

                        InitializeTypeParameters(delegateSymbol, delegateDecl.TypeParameterList, delegateDecl.ConstraintClauses);
                        RegisterDeclaredTypeSymbol(delegateDecl, delegateSymbol);
                        break;
                    }
            }
        }
    }

    private void DeclareDelegateSymbol(DelegateDeclarationSyntax delegateDecl, INamespaceSymbol parentNamespace)
    {
        if (TryReuseDeclaredTypeSymbol(
            delegateDecl,
            delegateDecl.Identifier.ValueText,
            supportsPartial: false,
            isPartial: false,
            delegateDecl.Identifier.GetLocation(),
            out _))
        {
            return;
        }

        ReportInvalidTypeModifiers(delegateDecl, isNestedType: false, _declarationDiagnostics);

        ReportExternalTypeRedeclaration(
            parentNamespace,
            delegateDecl.Identifier,
            delegateDecl.TypeParameterList?.Parameters.Count ?? 0,
            _declarationDiagnostics);

        var baseType = Compilation.GetSpecialType(SpecialType.System_MulticastDelegate);

        var delegateAccessibility = AccessibilityUtilities.DetermineAccessibility(
            delegateDecl.Modifiers,
            Accessibility.Internal);

        var delegateSymbol = new SourceNamedTypeSymbol(
            delegateDecl.Identifier.ValueText,
            baseType!,
            TypeKind.Delegate,
            parentNamespace.AsSourceNamespace(),
            null,
            parentNamespace.AsSourceNamespace(),
            new[] { delegateDecl.GetLocation() },
            new[] { delegateDecl.GetReference() },
            isSealed: true,
            isAbstract: true,
            isStatic: false,
            declaredAccessibility: delegateAccessibility,
            metadataName: GetFileScopedMetadataName(delegateDecl, delegateDecl.Identifier.ValueText));

        if (HasFileScopeModifier(delegateDecl.Modifiers))
            delegateSymbol.MarkFileScoped(delegateDecl.SyntaxTree.FilePath);

        InitializeTypeParameters(delegateSymbol, delegateDecl.TypeParameterList, delegateDecl.ConstraintClauses);
        RegisterDeclaredTypeSymbol(delegateDecl, delegateSymbol);
    }

    private void DeclareInterfaceSymbol(InterfaceDeclarationSyntax interfaceDecl, INamespaceSymbol parentNamespace, INamedTypeSymbol? objectType)
    {
        var isPartial = interfaceDecl.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
        if (TryReuseDeclaredTypeSymbol(
            interfaceDecl,
            interfaceDecl.Identifier.ValueText,
            supportsPartial: true,
            isPartial,
            interfaceDecl.Identifier.GetLocation(),
            out var alreadyDeclaredInterface))
        {
            DeclareClassMemberTypes(interfaceDecl, alreadyDeclaredInterface);
            return;
        }

        ReportInvalidTypeModifiers(interfaceDecl, isNestedType: false, _declarationDiagnostics);

        var hasSealedModifier = interfaceDecl.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);
        var hasPermitsClause = interfaceDecl.PermitsClause is not null;
        if (hasPermitsClause && !hasSealedModifier)
        {
            _declarationDiagnostics.ReportPermitsClauseRequiresSealed(
                interfaceDecl.Identifier.GetLocation());
        }

        ReportExternalTypeRedeclaration(
            parentNamespace,
            interfaceDecl.Identifier,
            interfaceDecl.TypeParameterList?.Parameters.Count ?? 0,
            _declarationDiagnostics);

        var isFileScoped = HasFileScopeModifier(interfaceDecl.Modifiers);
        var interfaceAccessibility = AccessibilityUtilities.DetermineAccessibility(
            interfaceDecl.Modifiers,
            Compilation.IsSubmission ? Accessibility.Public : Accessibility.Internal);

        var parentSourceNamespace = parentNamespace.AsSourceNamespace();
        var existingType = parentSourceNamespace is not null
            ? FindExistingDeclaredType(parentSourceNamespace, interfaceDecl.Identifier.ValueText, TypeKind.Interface, GetDeclaredTypeParameterArity(interfaceDecl))
            : null;

        if (existingType is not null)
        {
            var hadPartial = existingType.HasPartialModifier;
            var hadNonPartial = existingType.HasNonPartialDeclaration;
            var previouslyMixed = hadPartial && hadNonPartial;
            var willBeMixed = (hadPartial || isPartial) && (hadNonPartial || !isPartial);

            if (willBeMixed && !previouslyMixed)
            {
                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                    interfaceDecl.Identifier.ValueText,
                    interfaceDecl.Identifier.GetLocation());
            }
            else if (hadNonPartial && !isPartial)
            {
                _declarationDiagnostics.ReportTypeAlreadyDefined(
                    interfaceDecl.Identifier.ValueText,
                    interfaceDecl.Identifier.GetLocation());
            }

            existingType.AddDeclaration(interfaceDecl.GetLocation(), interfaceDecl.GetReference());
            existingType.RegisterPartialModifier(isPartial);
            ReportPartialTypeCompatibility(existingType, interfaceDecl, interfaceAccessibility, isFileScoped, _declarationDiagnostics);

            if (isFileScoped)
                existingType.MarkFileScoped(interfaceDecl.SyntaxTree.FilePath);
            if (hasSealedModifier)
                existingType.MarkAsSealedHierarchy(interfaceDecl.SyntaxTree.FilePath, hasPermitsClause);
            RegisterDeclaredTypeSymbol(interfaceDecl, existingType);
            DeclareClassMemberTypes(interfaceDecl, existingType);
            return;
        }

        var interfaceSymbol = new SourceNamedTypeSymbol(
            interfaceDecl.Identifier.ValueText,
            objectType!,
            TypeKind.Interface,
            parentNamespace.AsSourceNamespace(),
            null,
            parentNamespace.AsSourceNamespace(),
            new[] { interfaceDecl.GetLocation() },
            new[] { interfaceDecl.GetReference() },
            true,
            isAbstract: true,
            declaredAccessibility: interfaceAccessibility,
            metadataName: GetFileScopedMetadataName(interfaceDecl, interfaceDecl.Identifier.ValueText));

        interfaceSymbol.RegisterPartialModifier(isPartial);

        if (isFileScoped)
            interfaceSymbol.MarkFileScoped(interfaceDecl.SyntaxTree.FilePath);
        if (hasSealedModifier)
            interfaceSymbol.MarkAsSealedHierarchy(interfaceDecl.SyntaxTree.FilePath, hasPermitsClause);
        InitializeTypeParameters(interfaceSymbol, interfaceDecl.TypeParameterList, interfaceDecl.ConstraintClauses);
        RegisterDeclaredTypeSymbol(interfaceDecl, interfaceSymbol);
        DeclareClassMemberTypes(interfaceDecl, interfaceSymbol);
    }

    private void DeclareExtensionSymbol(ExtensionDeclarationSyntax extensionDecl, INamespaceSymbol parentNamespace, INamedTypeSymbol? objectType)
    {
        ReportInvalidTypeModifiers(extensionDecl, isNestedType: false, _declarationDiagnostics);

        var extensionAccessibility = AccessibilityUtilities.DetermineAccessibility(
            extensionDecl.Modifiers,
            Accessibility.Internal);
        var isFileScoped = HasFileScopeModifier(extensionDecl.Modifiers);

        var hasExplicitPublicModifier = extensionDecl.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);
        if (hasExplicitPublicModifier &&
            extensionDecl.Identifier.Kind == SyntaxKind.None)
        {
            _declarationDiagnostics.ReportPublicExtensionRequiresIdentifier(
                extensionDecl.GetLocation());
        }

        var extensionName = extensionDecl.Identifier.Kind == SyntaxKind.None
            ? MangleUnnamedExtensionName(extensionDecl)
            : extensionDecl.Identifier.ValueText;

        if (TryReuseDeclaredTypeSymbol(
            extensionDecl,
            extensionName,
            supportsPartial: false,
            isPartial: false,
            extensionDecl.GetLocation(),
            out _))
        {
            return;
        }

        ReportExternalTypeRedeclaration(
            parentNamespace,
            extensionName,
            extensionDecl.GetLocation(),
            extensionDecl.TypeParameterList?.Parameters.Count ?? 0,
            _declarationDiagnostics);

        var extensionSymbol = new SourceNamedTypeSymbol(
            extensionName,
            objectType!,
            TypeKind.Class,
            parentNamespace.AsSourceNamespace(),
            null,
            parentNamespace.AsSourceNamespace(),
            new[] { extensionDecl.GetLocation() },
            new[] { extensionDecl.GetReference() },
            isSealed: true,
            isAbstract: true,
            declaredAccessibility: extensionAccessibility,
            metadataName: extensionDecl.Identifier.Kind == SyntaxKind.None
                ? extensionName
                : GetFileScopedMetadataName(extensionDecl, extensionDecl.Identifier.ValueText));

        extensionSymbol.MarkAsExtensionContainer();

        if (isFileScoped)
            extensionSymbol.MarkFileScoped(extensionDecl.SyntaxTree.FilePath);
        InitializeTypeParameters(extensionSymbol, extensionDecl.TypeParameterList, extensionDecl.ConstraintClauses);
        RegisterDeclaredTypeSymbol(extensionDecl, extensionSymbol);
    }

    private void DeclareEnumSymbol(EnumDeclarationSyntax enumDecl, INamespaceSymbol parentNamespace)
    {
        if (TryReuseDeclaredTypeSymbol(
            enumDecl,
            enumDecl.Identifier.ValueText,
            supportsPartial: false,
            isPartial: false,
            enumDecl.Identifier.GetLocation(),
            out _))
        {
            return;
        }

        ReportInvalidTypeModifiers(enumDecl, isNestedType: false, _declarationDiagnostics);

        var enumAccessibility = AccessibilityUtilities.DetermineAccessibility(
            enumDecl.Modifiers,
            Compilation.IsSubmission ? Accessibility.Public : Accessibility.Internal);
        ReportExternalTypeRedeclaration(
            parentNamespace,
            enumDecl.Identifier,
            arity: 0,
            _declarationDiagnostics);

        var enumSymbol = new SourceNamedTypeSymbol(
            enumDecl.Identifier.ValueText,
            Compilation.GetSpecialType(SpecialType.System_Enum),
            TypeKind.Enum,
            parentNamespace.AsSourceNamespace(),
            null,
            parentNamespace.AsSourceNamespace(),
            new[] { enumDecl.GetLocation() },
            new[] { enumDecl.GetReference() },
            true,
            declaredAccessibility: enumAccessibility,
            metadataName: GetFileScopedMetadataName(enumDecl, enumDecl.Identifier.ValueText)
        );

        if (HasFileScopeModifier(enumDecl.Modifiers))
            enumSymbol.MarkFileScoped(enumDecl.SyntaxTree.FilePath);

        RegisterDeclaredTypeSymbol(enumDecl, enumSymbol);
    }

    private void DeclareUnionSymbol(UnionDeclarationSyntax unionDecl, INamespaceSymbol parentNamespace)
    {
        var isPartial = unionDecl.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword);
        if (TryReuseDeclaredTypeSymbol(
            unionDecl,
            unionDecl.Identifier.ValueText,
            supportsPartial: true,
            isPartial,
            unionDecl.Identifier.GetLocation(),
            out _))
        {
            return;
        }

        ReportInvalidTypeModifiers(unionDecl, isNestedType: false, _declarationDiagnostics);

        var declaringSymbol = (ISymbol)(parentNamespace.AsSourceNamespace() ?? parentNamespace);
        var namespaceSymbol = parentNamespace.AsSourceNamespace();
        var unionTypeKind = GetUnionTypeKind(unionDecl);
        var unionBaseType = GetUnionBaseType(unionTypeKind);
        ReportExternalTypeRedeclaration(
            parentNamespace,
            unionDecl.Identifier,
            unionDecl.TypeParameterList?.Parameters.Count ?? 0,
            _declarationDiagnostics);
        var isFileScoped = HasFileScopeModifier(unionDecl.Modifiers);
        var unionAccessibility = AccessibilityUtilities.DetermineAccessibility(
            unionDecl.Modifiers,
            Compilation.IsSubmission ? Accessibility.Public : Accessibility.Internal);

        var existingType = namespaceSymbol is not null
            ? FindExistingDeclaredType(namespaceSymbol, unionDecl.Identifier.ValueText, unionTypeKind, unionDecl.TypeParameterList?.Parameters.Count ?? 0)
            : null;

        SourceUnionSymbol unionSymbol;
        var isNewSymbol = true;

        if (existingType is SourceUnionSymbol existingUnion)
        {
            var hadPartial = existingUnion.HasPartialModifier;
            var hadNonPartial = existingUnion.HasNonPartialDeclaration;
            var previouslyMixed = hadPartial && hadNonPartial;
            var willBeMixed = (hadPartial || isPartial) && (hadNonPartial || !isPartial);

            if (willBeMixed && !previouslyMixed)
            {
                _declarationDiagnostics.ReportPartialTypeDeclarationMissingPartial(
                    unionDecl.Identifier.ValueText,
                    unionDecl.Identifier.GetLocation());
            }
            else if (hadNonPartial && !isPartial)
            {
                _declarationDiagnostics.ReportTypeAlreadyDefined(
                    unionDecl.Identifier.ValueText,
                    unionDecl.Identifier.GetLocation());
            }

            existingUnion.AddDeclaration(unionDecl.GetLocation(), unionDecl.GetReference());
            existingUnion.RegisterPartialModifier(isPartial);
            ReportPartialTypeCompatibility(existingUnion, unionDecl, unionAccessibility, isFileScoped, _declarationDiagnostics);

            if (isFileScoped)
                existingUnion.MarkFileScoped(unionDecl.SyntaxTree.FilePath);

            unionSymbol = existingUnion;
            isNewSymbol = false;
        }
        else
        {
            unionSymbol = new SourceUnionSymbol(
                unionDecl.Identifier.ValueText,
                unionBaseType,
                unionTypeKind,
                declaringSymbol,
                null,
                namespaceSymbol,
                [unionDecl.GetLocation()],
                [unionDecl.GetReference()],
                unionAccessibility,
                metadataName: GetFileScopedMetadataName(unionDecl, unionDecl.Identifier.ValueText));

            unionSymbol.RegisterPartialModifier(isPartial);

            if (isFileScoped)
                unionSymbol.MarkFileScoped(unionDecl.SyntaxTree.FilePath);
        }

        if (isNewSymbol)
            InitializeTypeParameters(unionSymbol, unionDecl.TypeParameterList, unionDecl.ConstraintClauses);

        RegisterDeclaredTypeSymbol(unionDecl, unionSymbol);
    }

    private Binder BindCompilationUnit(
        CompilationUnitSyntax cu,
        Binder parentBinder,
        bool allowSourceDeclarationCompletion = true)
    {
        using var sourceNamespaceLookupSuppression = allowSourceDeclarationCompletion
            ? null
            : Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();

        // Step 1: Resolve namespace
        var fileScopedNamespace = cu.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        INamespaceSymbol targetNamespace;
        NamespaceBinder namespaceBinder;

        if (fileScopedNamespace != null)
        {
            targetNamespace = Compilation.GetOrCreateNamespaceSymbol(fileScopedNamespace.Name.ToString())
                             ?? throw new Exception("Namespace not found");

            namespaceBinder = new NamespaceBinder(parentBinder, targetNamespace);
            parentBinder = namespaceBinder;
        }
        else
        {
            targetNamespace = Compilation.GlobalNamespace;
            namespaceBinder = new NamespaceBinder(parentBinder, targetNamespace);
            parentBinder = namespaceBinder;
        }

        // Step 2: Handle imports
        var namespaceImports = new List<INamespaceOrTypeSymbol>();
        var typeImports = new List<ITypeSymbol>();
        var aliases = new Dictionary<string, IReadOnlyList<IAliasSymbol>>();
        var unresolvedAliases = new List<AliasDirectiveSyntax>();
        var deferredWildcardImports = new List<NameSyntax>();
        var deferredConstantImports = new List<NameSyntax>();
        var globalImportKeys = CollectGlobalImportKeys();

        var provisionalImportBinder = new ImportBinder(namespaceBinder, namespaceImports, typeImports, aliases);

        foreach (var (import, isGlobalImport) in EnumerateEffectiveImportDirectives(cu))
        {
            var name = import.Name.ToString();
            if (!isGlobalImport &&
                globalImportKeys.Contains(name))
            {
                var location = import.GetLocation();
                namespaceBinder.Diagnostics.ReportImportDirectiveRedundantWithGlobalImport(
                    name,
                    location);
                _declarationDiagnostics.ReportImportDirectiveRedundantWithGlobalImport(
                    name,
                    location);
                continue;
            }

            if (IsWildcard(import.Name, out var nsName))
            {
                var nsImport = ResolveWildcardImportTarget(targetNamespace, nsName);
                if (nsImport != null)
                {
                    namespaceImports.Add(nsImport);
                }
                else
                {
                    deferredWildcardImports.Add(nsName);
                }

                continue;
            }

            if (IsIncompleteImportName(import.Name))
                continue;

            var nsSymbol = ResolveNamespace(targetNamespace, name);
            if (nsSymbol != null)
            {
                var location = import.Name.GetLocation();
                namespaceBinder.Diagnostics.ReportTypeExpectedWithoutWildcard(location);
                _declarationDiagnostics.ReportTypeExpectedWithoutWildcard(location);
                continue;
            }

            ITypeSymbol? typeSymbol = HasTypeArguments(import.Name)
                ? ResolveOpenGenericType(targetNamespace, import.Name)
                : ResolveType(targetNamespace, name);

            if (typeSymbol != null)
            {
                typeImports.Add(typeSymbol);
            }
            else if (TryResolveImportedConstant(targetNamespace, import.Name) is { } constant)
            {
                AddAliasImport(constant.Name, constant);
            }
            else if (TryResolveImportedNamespaceMember(targetNamespace, import.Name) is { } namespaceMember)
            {
                AddAliasImport(namespaceMember.Name, namespaceMember);
            }
            else
            {
                deferredConstantImports.Add(import.Name);
            }
        }

        var importBinder = new ImportBinder(namespaceBinder, namespaceImports, typeImports, aliases);

        parentBinder = importBinder;

        var compilationUnitBinder = new CompilationUnitBinder(parentBinder, this);
        if (!allowSourceDeclarationCompletion)
            EnsureDeclarations();

        BindAliases(EnumerateGlobalAliasDirectives(), reportUnresolved: false);
        BindAliases(cu.Aliases, reportUnresolved: false);

        if (fileScopedNamespace is not null)
        {
            BindAliases(fileScopedNamespace.Aliases, reportUnresolved: false);
        }

        var canBindIncrementalMemberSignatures =
            unresolvedAliases.Count == 0 &&
            deferredWildcardImports.Count == 0 &&
            deferredConstantImports.Count == 0;
        BindNamespaceMembers(
            cu,
            compilationUnitBinder,
            targetNamespace,
            bindMemberSignatures: allowSourceDeclarationCompletion || canBindIncrementalMemberSignatures);

        if (unresolvedAliases.Count > 0)
        {
            if (!allowSourceDeclarationCompletion)
            {
                BindNamespaceMembers(
                    cu,
                    compilationUnitBinder,
                    targetNamespace,
                    bindMemberSignatures: true,
                    bindTopLevelFunctionSignatures: false);
            }

            var aliasesToRetry = unresolvedAliases.ToArray();
            unresolvedAliases.Clear();
            BindAliases(aliasesToRetry, reportUnresolved: true);
        }

        if (!allowSourceDeclarationCompletion &&
            (deferredWildcardImports.Count > 0 || deferredConstantImports.Count > 0))
        {
            Compilation.EnsureSourceDeclarationsDeclared();
        }

        foreach (var baseName in deferredWildcardImports)
        {
            var resolved = ResolveWildcardImportTarget(targetNamespace, baseName);

            if (resolved != null)
            {
                namespaceImports.Add(resolved);
            }
            else if (IsOptionalGeneratedPreludeImport(baseName))
            {
                continue;
            }
            else
            {
                var location = baseName.GetLocation();
                namespaceBinder.Diagnostics.ReportInvalidImportTarget(location);
                _declarationDiagnostics.ReportInvalidImportTarget(location);
            }
        }

        foreach (var constantImport in deferredConstantImports)
        {
            if (TryResolveImportedConstant(targetNamespace, constantImport) is { } constant)
            {
                AddAliasImport(constant.Name, constant);
            }
            else if (TryResolveImportedNamespaceMember(targetNamespace, constantImport) is { } namespaceMember)
            {
                AddAliasImport(namespaceMember.Name, namespaceMember);
            }
            else
            {
                var location = constantImport.GetLocation();
                namespaceBinder.Diagnostics.ReportInvalidImportTarget(location);
                _declarationDiagnostics.ReportInvalidImportTarget(location);
            }
        }

        foreach (var diagnostic in namespaceBinder.Diagnostics.AsEnumerable())
            importBinder.Diagnostics.Report(diagnostic);

        foreach (var diagnostic in compilationUnitBinder.Diagnostics.AsEnumerable())
            importBinder.Diagnostics.Report(diagnostic);

        var topLevelBinder = CreateTopLevelBinder(cu, targetNamespace, compilationUnitBinder);

        foreach (var diagnostic in importBinder.Diagnostics.AsEnumerable())
            topLevelBinder.Diagnostics.Report(diagnostic);

        CacheBinder(cu, topLevelBinder);
        if (fileScopedNamespace != null)
            CacheBinder(fileScopedNamespace, compilationUnitBinder);

        return topLevelBinder;

        void BindAliases(IEnumerable<AliasDirectiveSyntax> aliasList, bool reportUnresolved)
        {
            foreach (var alias in aliasList)
            {
                IReadOnlyList<ISymbol> symbols;
                if (alias.Target is NameSyntax name)
                {
                    symbols = ResolveAlias(targetNamespace, name);
                }
                else
                {
                    var typeSymbol = TryResolveTypeSyntaxSilently(alias.Target);
                    symbols = typeSymbol is null
                        ? Array.Empty<ISymbol>()
                        : new ISymbol[] { typeSymbol };
                }

                if (symbols.Count > 0)
                {
                    var aliasName = alias.Identifier.ValueText;
                    var aliasSymbols = symbols
                        .Select(s => AliasSymbolFactory.Create(aliasName, s))
                        .ToArray();
                    aliases[aliasName] = aliasSymbols;
                }
                else
                {
                    if (!reportUnresolved)
                    {
                        unresolvedAliases.Add(alias);
                        continue;
                    }

                    var location = alias.Target.GetLocation();
                    namespaceBinder.Diagnostics.ReportInvalidAliasType(location);
                    _declarationDiagnostics.ReportInvalidAliasType(location);
                }
            }
        }

        INamespaceSymbol? ResolveNamespace(INamespaceSymbol current, string name)
        {
            var full = Combine(current, name);
            return Compilation.GetNamespaceSymbol(full) ?? Compilation.GetNamespaceSymbol(name);
        }

        ITypeSymbol? ResolveType(INamespaceSymbol current, string name)
        {
            return ResolveMetadataTypeByName(name)
                ?? ResolveScopedMetadataType(current, name)
                ?? ResolveTypeFromContainingNamespace(current, name)
                ?? ResolveTypeFromNamespace(current, name)
                ?? ResolveTypeFromGlobalRoots(name);
        }

        ITypeSymbol? ResolveTypeFromGlobalRoots(string name)
        {
            var sourceType = ResolveTypeFromNamespace(Compilation.SourceGlobalNamespace, name);
            if (sourceType is not null)
                return sourceType;

            foreach (var assembly in Compilation.ReferencedAssemblySymbols)
            {
                var metadataType = ResolveTypeFromNamespace(assembly.GlobalNamespace, name);
                if (metadataType is not null)
                    return metadataType;
            }

            return null;
        }

        INamedTypeSymbol? ResolveMetadataTypeByName(string name)
            => allowSourceDeclarationCompletion
                ? Compilation.GetTypeByMetadataName(name)
                : Compilation.TryGetMetadataReferenceTypeByMetadataName(name);

        INamedTypeSymbol? ResolveScopedMetadataType(INamespaceSymbol current, string name)
            => allowSourceDeclarationCompletion
                ? Compilation.GetTypeByMetadataName(current, name)
                : Compilation.TryGetMetadataReferenceTypeByMetadataName(current, name);

        ITypeSymbol? ResolveTypeFromContainingNamespace(INamespaceSymbol current, string name)
        {
            var lastDot = name.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == name.Length - 1)
                return null;

            var namespaceName = name[..lastDot];
            var typeName = name[(lastDot + 1)..];
            var ns = ResolveNamespace(current, namespaceName);
            return ns?.GetMembers(typeName).OfType<ITypeSymbol>().FirstOrDefault()
                ?? (allowSourceDeclarationCompletion ? ns?.LookupType(typeName) : null);
        }

        ITypeSymbol? ResolveTypeFromNamespace(INamespaceSymbol scope, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var parts = name.Split('.');
            INamespaceOrTypeSymbol current = scope;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (current is INamespaceSymbol ns)
                {
                    var isLastPart = i == parts.Length - 1;
                    var namespaceMember = !isLastPart
                        ? ns.LookupNamespace(part)
                            ?? ns.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault()
                        : null;
                    if (namespaceMember is not null)
                    {
                        current = namespaceMember;
                        continue;
                    }

                    var typeMember = ns.GetMembers(part).OfType<ITypeSymbol>().FirstOrDefault()
                        ?? (allowSourceDeclarationCompletion ? ns.LookupType(part) : null);
                    if (typeMember is not null)
                    {
                        current = typeMember;
                        continue;
                    }

                    namespaceMember = ns.LookupNamespace(part)
                        ?? ns.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault();
                    if (namespaceMember is null)
                        return null;

                    current = namespaceMember;
                    continue;
                }

                if (current is INamedTypeSymbol type)
                {
                    var nestedType = type.GetMembers()
                        .OfType<ITypeSymbol>()
                        .FirstOrDefault(member => member.Name == part);
                    if (nestedType is null)
                        return null;

                    current = nestedType;
                    continue;
                }

                return null;
            }

            return current as ITypeSymbol;
        }

        IReadOnlyList<ISymbol> ResolveAlias(INamespaceSymbol current, NameSyntax name)
        {
            var nsSymbol = ResolveNamespace(current, name.ToString());
            if (nsSymbol != null)
                return [nsSymbol];

            ITypeSymbol? typeSymbol = HasTypeArguments(name)
                ? ResolveGenericType(current, name)
                : ResolveType(current, name.ToString());
            if (typeSymbol != null)
                return [typeSymbol];

            if (name is QualifiedNameSyntax qn)
            {
                var memberName = GetRightmostIdentifier(name);
                var left = qn.Left;
                var leftHasTypeArguments = HasTypeArguments(left);

                ITypeSymbol? containingType = leftHasTypeArguments
                    ? ResolveGenericType(current, left) ?? TryResolveTypeSyntaxSilently(left)
                    : ResolveType(current, left.ToString());

                if (containingType != null)
                {
                    if (!leftHasTypeArguments &&
                        containingType is IUnionSymbol unionSymbol)
                    {
                        var variants = unionSymbol.Variants
                            .Where(type => string.Equals(type.Name, memberName, StringComparison.Ordinal))
                            .Cast<ISymbol>()
                            .ToArray();

                        if (variants.Length > 0)
                            return variants;
                    }

                    var members = containingType.GetMembers(memberName)
                        .Where(m => m.IsStatic)
                        .ToArray();

                    if (qn.Right is GenericNameSyntax genericRight &&
                        TryResolveTypeArgumentsSilently(genericRight.TypeArgumentList, out var memberTypeArguments))
                    {
                        var closedTypeMembers = members
                            .OfType<INamedTypeSymbol>()
                            .Where(t => t.Arity == memberTypeArguments.Length)
                            .Select(t => t.Construct(memberTypeArguments))
                            .Cast<ISymbol>()
                            .ToArray();

                        if (closedTypeMembers.Length > 0)
                            return closedTypeMembers;
                    }

                    if (members.Length > 0)
                        return members;
                }

                if (TryResolveImportedNamespaceMember(current, name) is { } namespaceMember)
                    return [namespaceMember];
            }

            return Array.Empty<ISymbol>();
        }

        static bool HasTypeArguments(NameSyntax nameSyntax)
            => nameSyntax.DescendantNodes().OfType<GenericNameSyntax>().Any();

        static bool IsIncompleteImportName(NameSyntax nameSyntax)
            => nameSyntax.DescendantNodesAndSelf()
                .OfType<SimpleNameSyntax>()
                .Any(static name => name.Identifier.IsMissing);

        ITypeSymbol? ResolveGenericType(INamespaceSymbol current, NameSyntax name)
        {
            if (name is GenericNameSyntax g)
            {
                var baseName = $"{g.Identifier.ValueText}`{g.TypeArgumentList.Arguments.Count}";
                var unconstructed = ResolveScopedMetadataType(current, baseName);
                if (unconstructed is null)
                    return null;

                if (!TryResolveTypeArgumentsSilently(g.TypeArgumentList, out var args))
                    return null;

                return Compilation.ConstructGenericType(unconstructed, args);
            }

            if (name is QualifiedNameSyntax { Right: GenericNameSyntax gen })
            {
                var leftName = ((QualifiedNameSyntax)name).Left.ToString();
                var baseName = $"{leftName}.{gen.Identifier.ValueText}`{gen.TypeArgumentList.Arguments.Count}";
                var unconstructed = ResolveMetadataTypeByName(baseName)
                    ?? ResolveScopedMetadataType(current, baseName);
                if (unconstructed is null)
                    return null;

                if (!TryResolveTypeArgumentsSilently(gen.TypeArgumentList, out var args))
                    return null;

                return Compilation.ConstructGenericType(unconstructed, args);
            }

            return null;
        }

        ITypeSymbol? ResolveOpenGenericType(INamespaceSymbol current, NameSyntax name)
        {
            if (name is GenericNameSyntax g)
            {
                var baseName = $"{g.Identifier.ValueText}`{g.TypeArgumentList.Arguments.SeparatorCount + 1}";
                var unconstructed = ResolveScopedMetadataType(current, baseName);
                if (unconstructed is null)
                    return null;

                if (!TryResolveTypeArgumentsSilently(g.TypeArgumentList, out var args))
                    return null;

                return Compilation.ConstructGenericType(unconstructed, args);
            }

            if (name is QualifiedNameSyntax { Right: GenericNameSyntax gen })
            {
                var leftName = ((QualifiedNameSyntax)name).Left.ToString();
                var baseName = $"{leftName}.{gen.Identifier.ValueText}`{gen.TypeArgumentList.Arguments.SeparatorCount + 1}";
                var unconstructed = ResolveMetadataTypeByName(baseName)
                    ?? ResolveScopedMetadataType(current, baseName);
                if (unconstructed is not null)
                    return unconstructed;
            }

            return null;
        }

        ITypeSymbol? TryResolveTypeSyntaxSilently(TypeSyntax syntax)
        {
            using (provisionalImportBinder.Diagnostics.CreateNonReportingScope())
            {
                var result = provisionalImportBinder.BindTypeSyntax(syntax);
                return result.Success ? result.ResolvedType : null;
            }
        }

        INamespaceOrTypeSymbol? ResolveWildcardImportTarget(INamespaceSymbol current, NameSyntax name)
        {
            var targetName = GetRightmostIdentifier(name);
            var nameText = name.ToString();

            if (ResolveType(current, nameText) is { } resolvedType &&
                string.Equals(resolvedType.Name, targetName, StringComparison.Ordinal))
                return GetLogicalImportType(resolvedType);

            var resolvedNamespace = ResolveNamespace(current, nameText);
            if (resolvedNamespace is not null &&
                provisionalImportBinder.TryResolveTypeFromNamespaceName(resolvedNamespace, out var namespaceType))
            {
                return namespaceType;
            }

            return resolvedNamespace;
        }

        static ITypeSymbol GetLogicalImportType(ITypeSymbol type)
        {
            if (type is PEUnionCompanionSymbol companion &&
                companion.TryGetAssociatedUnion(out var union))
            {
                return (ITypeSymbol)union;
            }

            return type;
        }

        IFieldSymbol? TryResolveImportedConstant(INamespaceSymbol current, NameSyntax name)
        {
            if (name is not QualifiedNameSyntax { Left: var left, Right: IdentifierNameSyntax right })
                return null;

            var ownerType = ResolveType(current, left.ToString())
                ?? TryResolveTypeSyntaxSilently(left);
            if (ownerType is null)
                return null;

            return ownerType.GetMembers(right.Identifier.ValueText)
                .OfType<IFieldSymbol>()
                .FirstOrDefault(IsImportableConstant);
        }

        ISymbol? TryResolveImportedNamespaceMember(INamespaceSymbol current, NameSyntax name)
        {
            if (name is not QualifiedNameSyntax { Left: var left, Right: IdentifierNameSyntax right })
                return null;

            var ns = ResolveNamespace(current, left.ToString());
            if (ns is null)
                return null;

            return Compilation.GetNamespaceMembers(ns, right.Identifier.ValueText, Compilation.Options.AllowNamespaceMemberImports)
                .FirstOrDefault(static member => member is IMethodSymbol or IFieldSymbol);
        }

        void AddAliasImport(string name, ISymbol symbol)
        {
            var alias = AliasSymbolFactory.Create(name, symbol);
            if (aliases.TryGetValue(name, out var existing))
            {
                aliases[name] = existing.Concat([alias]).ToArray();
                return;
            }

            aliases[name] = [alias];
        }

        bool TryResolveTypeArgumentsSilently(TypeArgumentListSyntax list, out ITypeSymbol[] args)
        {
            args = new ITypeSymbol[list.Arguments.Count];

            for (var i = 0; i < list.Arguments.Count; i++)
            {
                var resolved = TryResolveTypeSyntaxSilently(list.Arguments[i].Type);
                if (resolved is null)
                    return false;

                args[i] = resolved;
            }

            return true;
        }

        static string Combine(INamespaceSymbol current, string name)
        {
            var currentName = current.ToString();
            return string.IsNullOrEmpty(currentName) ? name : currentName + "." + name;
        }

        static string GetRightmostIdentifier(NameSyntax nameSyntax)
        {
            return nameSyntax switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                QualifiedNameSyntax qn => GetRightmostIdentifier(qn.Right),
                _ => nameSyntax.ToString()
            };
        }

        static bool IsImportableConstant(IFieldSymbol field)
            => field.IsConst || field.ContainingType?.TypeKind == TypeKind.Enum;

        static bool IsWildcard(NameSyntax nameSyntax, out NameSyntax baseName)
        {
            if (nameSyntax is QualifiedNameSyntax { Right: WildcardNameSyntax, Left: var left })
            {
                baseName = left;
                return true;
            }

            baseName = default!;
            return false;
        }

        IEnumerable<(ImportDirectiveSyntax Import, bool IsGlobal)> EnumerateEffectiveImportDirectives(CompilationUnitSyntax compilationUnit)
        {
            foreach (var tree in Compilation.SyntaxTrees)
            {
                if (tree.GetRoot() is not CompilationUnitSyntax root)
                    continue;

                foreach (var globalImport in root.Members.OfType<GlobalImportBlockSyntax>())
                {
                    foreach (var import in globalImport.Imports)
                        yield return (import, true);
                }
            }

            foreach (var import in compilationUnit.Imports)
                yield return (import, false);

            if (compilationUnit.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() is { } fileScopedNamespace)
            {
                foreach (var import in fileScopedNamespace.Imports)
                    yield return (import, false);
            }
        }

        HashSet<string> CollectGlobalImportKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tree in Compilation.SyntaxTrees)
            {
                if (tree.GetRoot() is not CompilationUnitSyntax root)
                    continue;

                foreach (var globalImport in root.Members.OfType<GlobalImportBlockSyntax>())
                {
                    foreach (var import in globalImport.Imports)
                        keys.Add(import.Name.ToString());
                }
            }

            return keys;
        }

        IEnumerable<AliasDirectiveSyntax> EnumerateGlobalAliasDirectives()
        {
            foreach (var tree in Compilation.SyntaxTrees)
            {
                if (tree.GetRoot() is not CompilationUnitSyntax root)
                    continue;

                if (!IsGeneratedPreludeFile(root.SyntaxTree?.FilePath))
                    continue;

                foreach (var alias in root.Aliases)
                    yield return alias;
            }
        }

        static bool IsOptionalGeneratedPreludeImport(NameSyntax nameSyntax)
        {
            if (!IsGeneratedPreludeFile(nameSyntax.SyntaxTree?.FilePath))
                return false;

            var importName = nameSyntax.ToString();
            return importName is
                "System" or
                "System.Collections" or
                "System.Collections.Generic" or
                "System.IO" or
                "System.Linq" or
                "System.Net.Http" or
                "System.Threading" or
                "System.Threading.Tasks" or
                "System.Result" or
                "System.Option";
        }

        static bool IsGeneratedPreludeFile(string? filePath)
            => !string.IsNullOrWhiteSpace(filePath) &&
                filePath.EndsWith(".Prelude.g.rvn", StringComparison.OrdinalIgnoreCase);
    }

    private Binder CreateTopLevelBinder(CompilationUnitSyntax cu, INamespaceSymbol namespaceSymbol, Binder parentBinder)
    {
        var fileScopedNamespace = cu.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        var bindableGlobals = Compilation.GetBindableGlobalStatements(cu);
        var hasNonGlobalMembers = Compilation.HasNonGlobalMembers(cu);
        var hadDisabledGlobalStatements = false;

        if (!Compilation.Options.AllowGlobalStatements && bindableGlobals.Count > 0)
        {
            foreach (var statement in bindableGlobals)
                parentBinder.Diagnostics.Report(Diagnostic.Create(s_globalStatementsDisabled, statement.GetLocation()));

            bindableGlobals = [];
            hadDisabledGlobalStatements = true;
        }

        var topLevelFunctionMembers = GetTopLevelFunctionMembers(cu).ToArray();
        var topLevelMainFunctions = topLevelFunctionMembers
            .Where(static function => function.Identifier.ValueText == "Main")
            .ToArray();

        var hasTopLevelMainFunction = topLevelMainFunctions.Length > 0;
        if (hasTopLevelMainFunction)
        {
            foreach (var statement in bindableGlobals)
            {
                parentBinder.Diagnostics.ReportTopLevelStatementsDisallowedWithMainFunction(statement.GetLocation());
            }
        }

        var supportsTopLevelProgram = Compilation.Options.OutputKind == OutputKind.ConsoleApplication;

        var shouldCreateTopLevelProgram = !hasTopLevelMainFunction && (bindableGlobals.Count > 0
            || (supportsTopLevelProgram
                && bindableGlobals.Count == 0
                && !hasNonGlobalMembers
                && !hadDisabledGlobalStatements
                && ShouldCreateImplicitTopLevelProgramForCompilationUnit(cu)));
        var hasExecutableFileScopedCode = bindableGlobals.Any(static g => g.Statement is not FunctionStatementSyntax);

        if (fileScopedNamespace != null)
        {
            foreach (var member in cu.Members)
            {
                if (member == fileScopedNamespace)
                    break;

                parentBinder.Diagnostics.ReportFileScopedNamespaceOutOfOrder(member.GetLocation());
            }
        }

        if (hasExecutableFileScopedCode)
        {
            var firstExecutableGlobal = bindableGlobals.First(static g => g.Statement is not FunctionStatementSyntax);

            if (Compilation.Options.OutputKind != OutputKind.ConsoleApplication)
                parentBinder.Diagnostics.ReportFileScopedCodeRequiresConsole(firstExecutableGlobal.GetLocation());

            Compilation.SyntaxTreeWithFileScopedCode ??= cu.SyntaxTree;
        }

        if (!shouldCreateTopLevelProgram)
            return parentBinder;

        var (programClass, mainMethod, asyncImplementation) = Compilation.GetOrCreateTopLevelProgram(
            cu,
            namespaceSymbol.AsSourceNamespace()!,
            bindableGlobals);

        var scriptMethod = asyncImplementation is { } asyncMain
            ? (SourceMethodSymbol)asyncMain
            : mainMethod;

        var executableParentBinder = Compilation.IsSubmission
            ? new SubmissionBinder(parentBinder, Compilation)
            : parentBinder;
        var topLevelBinder = new TopLevelBinder(executableParentBinder, this, scriptMethod, mainMethod, cu);

        // Cache the compilation unit and its global statements before binding.
        //
        // Binding the statements will request declared symbols, which rely on
        // binder lookup through SemanticModel.GetBinder. Without priming the
        // cache, any attempt to resolve the compilation unit binder re-enters
        // BindCompilationUnit, causing unbounded recursion (see stack trace in
        // bug report). By caching eagerly we guarantee re-entrant lookups
        // retrieve the partially constructed top-level binder instead of
        // rebuilding it.
        CacheBinder(cu, topLevelBinder);

        var topLevelFunctionGlobals = topLevelFunctionMembers
            .Select(static function => (GlobalStatementSyntax)function.Parent!)
            .ToArray();

        foreach (var stmt in bindableGlobals.Concat(topLevelFunctionGlobals))
            CacheBinder(stmt, topLevelBinder);

        topLevelBinder.DeclareGlobalFunctions(topLevelFunctionGlobals);

        return topLevelBinder;

        bool ShouldCreateImplicitTopLevelProgramForCompilationUnit(CompilationUnitSyntax current)
        {
            if (Compilation.SyntaxTreeWithFileScopedCode is { } selected)
                return selected == current.SyntaxTree;

            var firstEligibleSyntaxTree = Compilation.SyntaxTrees
                .Select(tree => tree.GetRoot() as CompilationUnitSyntax)
                .FirstOrDefault(root =>
                    root is not null
                    && Compilation.GetBindableGlobalStatements(root).Count == 0
                    && !Compilation.HasNonGlobalMembers(root))
                ?.SyntaxTree;

            return firstEligibleSyntaxTree == current.SyntaxTree;
        }
    }

    private static IEnumerable<FunctionStatementSyntax> GetTopLevelFunctionMembers(CompilationUnitSyntax compilationUnit)
    {
        foreach (var global in compilationUnit.DescendantNodes().OfType<GlobalStatementSyntax>())
        {
            if (Compilation.IsTopLevelFunctionMember(global) &&
                global.Statement is FunctionStatementSyntax function)
            {
                yield return function;
            }
        }
    }

    private static string MangleUnnamedExtensionName(ExtensionDeclarationSyntax extensionDecl)
    {
        return CreateMangledTypeName("__ext$", extensionDecl, "extension");
    }

    private static string? GetFileScopedMetadataName(MemberDeclarationSyntax declaration, string logicalName)
    {
        if (!HasFileScopeModifier(GetModifiers(declaration)))
            return null;

        return CreateMangledTypeName("__fs$", declaration, logicalName);
    }

    private static bool HasFileScopeModifier(SyntaxTokenList modifiers)
        => AccessibilityUtilities.HasFileScopeModifier(modifiers);

    private static SyntaxTokenList GetModifiers(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.Modifiers,
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Modifiers,
            ExtensionDeclarationSyntax extensionDeclaration => extensionDeclaration.Modifiers,
            EnumDeclarationSyntax enumDeclaration => enumDeclaration.Modifiers,
            UnionDeclarationSyntax unionDeclaration => unionDeclaration.Modifiers,
            _ => default
        };

    private static string CreateMangledTypeName(string prefix, MemberDeclarationSyntax declaration, string logicalName)
        => CreateMangledTypeName(prefix, (SyntaxNode)declaration, logicalName);

    private static string CreateMangledTypeName(string prefix, SyntaxNode declaration, string logicalName)
    {
        var loc = declaration.GetLocation();
        var span = loc.SourceSpan;
        var filePath = declaration.SyntaxTree?.FilePath ?? string.Empty;
        var sanitizedLogicalName = string.IsNullOrWhiteSpace(logicalName)
            ? "type"
            : new string(logicalName.Select(static ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in filePath)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            hash ^= (uint)span.Start;
            hash *= 16777619;
            hash ^= (uint)span.Length;
            hash *= 16777619;

            return $"{prefix}{hash:x8}_{span.Start}_{span.Length}_{sanitizedLogicalName}";
        }
    }

    internal SourceNamedTypeSymbol EnsureLocalTypeDeclarationBound(BaseTypeDeclarationSyntax declaration, BlockBinder parentBinder)
    {
        if (Compilation.TryGetDeclaredTypeSymbol(declaration, out var existing))
            return existing;

        var declaringSymbol = parentBinder.ContainingSymbol;
        var containingType = declaringSymbol switch
        {
            INamedTypeSymbol type => type,
            IMethodSymbol method => method.ContainingType,
            _ => declaringSymbol.ContainingType
        };

        if (containingType is null)
            throw new InvalidOperationException("Local type declarations require an enclosing containing type.");

        var namespaceSymbol = containingType.ContainingNamespace?.AsSourceNamespace()
            ?? declaringSymbol.ContainingNamespace?.AsSourceNamespace();
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var valueType = Compilation.GetSpecialType(SpecialType.System_ValueType);
        var metadataName = CreateMangledTypeName("__local$", declaration, declaration.Identifier.ValueText);

        ReportInvalidLocalTypeDeclaration(declaration, _declarationDiagnostics);

        switch (declaration)
        {
            case TypeDeclarationSyntax typeDeclaration when typeDeclaration is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax:
                {
                    ReportInvalidTypeModifiers(typeDeclaration, isNestedType: true, _declarationDiagnostics);
                    ReportRedundantTypeModifiers(typeDeclaration, _declarationDiagnostics);

                    var declaredTypeKind = IsStructLikeNominalType(typeDeclaration)
                        ? TypeKind.Struct
                        : TypeKind.Class;
                    var defaultBaseType = GetDefaultBaseTypeForNominalDeclaration(typeDeclaration, objectType, valueType);
                    var isStatic = typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
                    var hasSealedModifier = typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);
                    var isAbstract = isStatic || typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword) || hasSealedModifier;
                    var isSealedHierarchy = hasSealedModifier && !isStatic;
                    var isSealed = isStatic || (!isSealedHierarchy && !typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.OpenKeyword) && !isAbstract);
                    var accessibility = AccessibilityUtilities.DetermineAccessibility(
                        typeDeclaration.Modifiers,
                        AccessibilityUtilities.GetDefaultTypeAccessibility(containingType));

                    var typeSymbol = new SourceNamedTypeSymbol(
                        typeDeclaration.Identifier.ValueText,
                        defaultBaseType!,
                        declaredTypeKind,
                        declaringSymbol,
                        containingType,
                        namespaceSymbol,
                        [typeDeclaration.GetLocation()],
                        [typeDeclaration.GetReference()],
                        isSealed,
                        isAbstract,
                        isStatic,
                        declaredAccessibility: accessibility,
                        metadataName: metadataName);

                    typeSymbol.RegisterPartialModifier(typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword));
                    typeSymbol.RegisterRecordModifier(typeDeclaration is RecordDeclarationSyntax || typeDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.RecordKeyword));
                    InitializeTypeParameters(typeSymbol, GetTypeParameterList(typeDeclaration), GetConstraintClauses(typeDeclaration));
                    RegisterDeclaredTypeSymbol(typeDeclaration, typeSymbol);

                    var classBinder = new ClassDeclarationBinder(parentBinder, typeSymbol, typeDeclaration);
                    classBinder.EnsureTypeParameterConstraintTypesResolved(typeSymbol.TypeParameters);
                    var defaultInterfaces = GetDefaultNominalInterfaces(typeSymbol);
                    var shape = classBinder.BindNominalTypeShape(typeDeclaration, defaultBaseType, defaultInterfaces);
                    if (shape.BaseType is not null &&
                        !SymbolEqualityComparer.Default.Equals(typeSymbol.BaseType, shape.BaseType) &&
                        SymbolEqualityComparer.Default.Equals(typeSymbol.BaseType, defaultBaseType))
                    {
                        typeSymbol.SetBaseType(shape.BaseType);
                    }

                    if (!shape.Interfaces.IsDefaultOrEmpty)
                        typeSymbol.SetInterfaces(MergeInterfaceSets(typeSymbol.Interfaces, shape.Interfaces));

                    CacheBinder(typeDeclaration, classBinder);
                    RegisterClassSymbol(typeDeclaration, typeSymbol);
                    if (typeSymbol.IsSealedHierarchy)
                        ResolveSealedHierarchyPermits(typeDeclaration, typeSymbol, classBinder);
                    RegisterClassMembers(typeDeclaration, classBinder);
                    classBinder.EnsureDefaultConstructor();
                    return typeSymbol;
                }

            case EnumDeclarationSyntax enumDeclaration:
                {
                    ReportInvalidTypeModifiers(enumDeclaration, isNestedType: true, _declarationDiagnostics);

                    var enumSymbol = new SourceNamedTypeSymbol(
                        enumDeclaration.Identifier.ValueText,
                        Compilation.GetSpecialType(SpecialType.System_Enum),
                        TypeKind.Enum,
                        declaringSymbol,
                        containingType,
                        namespaceSymbol,
                        [enumDeclaration.GetLocation()],
                        [enumDeclaration.GetReference()],
                        isSealed: true,
                        declaredAccessibility: AccessibilityUtilities.DetermineAccessibility(
                            enumDeclaration.Modifiers,
                            AccessibilityUtilities.GetDefaultTypeAccessibility(containingType)),
                        metadataName: metadataName);

                    RegisterDeclaredTypeSymbol(enumDeclaration, enumSymbol);

                    var enumBinder = new EnumDeclarationBinder(parentBinder, enumSymbol, enumDeclaration);
                    CacheBinder(enumDeclaration, enumBinder);
                    enumSymbol.SetEnumUnderlyingType(ResolveEnumUnderlyingType(enumDeclaration, parentBinder));
                    RegisterEnumMembers(enumDeclaration, enumBinder, enumSymbol, namespaceSymbol);
                    return enumSymbol;
                }
        }

        _declarationDiagnostics.Report(Diagnostic.Create(s_invalidLocalTypeDeclaration, declaration.GetLocation()));

        var placeholderSymbol = new SourceNamedTypeSymbol(
            declaration.Identifier.ValueText,
            objectType!,
            TypeKind.Class,
            declaringSymbol,
            containingType,
            namespaceSymbol,
            [declaration.GetLocation()],
            [declaration.GetReference()],
            isSealed: true,
            declaredAccessibility: AccessibilityUtilities.DetermineAccessibility(
                declaration.Modifiers,
                AccessibilityUtilities.GetDefaultTypeAccessibility(containingType)),
            metadataName: metadataName);

        RegisterDeclaredTypeSymbol(declaration, placeholderSymbol);
        return placeholderSymbol;
    }

    private static void ReportInvalidLocalTypeDeclaration(
        BaseTypeDeclarationSyntax declaration,
        DiagnosticBag diagnostics)
    {
        var (typeKind, modifiers, allowed) = declaration switch
        {
            ClassDeclarationSyntax classDecl => (
                "local class",
                classDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.SealedKeyword,
                    SyntaxKind.OpenKeyword,
                }),
            RecordDeclarationSyntax recordDecl => (
                "local record",
                recordDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.SealedKeyword,
                    SyntaxKind.OpenKeyword,
                    SyntaxKind.RecordKeyword,
                }),
            StructDeclarationSyntax structDecl => (
                "local struct",
                structDecl.Modifiers,
                new HashSet<SyntaxKind>()),
            EnumDeclarationSyntax enumDecl => (
                "local enum",
                enumDecl.Modifiers,
                new HashSet<SyntaxKind>()),
            _ => (null, default, null),
        };

        if (typeKind is null || allowed is null)
            return;

        foreach (var modifier in modifiers)
        {
            if (!allowed.Contains(modifier.Kind))
                diagnostics.ReportModifierNotValidOnMember(
                    modifier.Text,
                    typeKind,
                    declaration.Identifier.ValueText,
                    modifier.GetLocation());
        }
    }

    private void ReportExternalTypeRedeclaration(
        INamespaceSymbol? namespaceSymbol,
        string name,
        Location location,
        int arity,
        DiagnosticBag diagnostics)
    {
        _ = (namespaceSymbol, name, location, arity, diagnostics);
        // Source declarations take precedence over referenced metadata. Source-source
        // duplicates are still diagnosed by the normal declaration lookup below.
    }

    private void BindNamespaceMembers(
        SyntaxNode containerNode,
        Binder parentBinder,
        INamespaceSymbol parentNamespace,
        bool bindMemberSignatures = true,
        bool bindTopLevelFunctionSignatures = true)
    {
        var classBinders = new List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)>();
        var interfaceBinders = new List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)>();
        var extensionBinders = new List<(ExtensionDeclarationSyntax Syntax, ExtensionDeclarationBinder Binder)>();
        var unionBinders = new List<(
            UnionDeclarationSyntax Syntax,
            UnionDeclarationBinder Binder,
            SourceUnionSymbol Symbol,
            ImmutableArray<MemberDeclarationSyntax> IntroducedMembers)>();

        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);

        foreach (var member in Compilation.GetEffectiveNamespaceMembers(containerNode))
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nsDecl:
                    {
                        var namespaceName = nsDecl is FileScopedNamespaceDeclarationSyntax
                            ? nsDecl.Name.ToString()
                            : parentNamespace.QualifyName(nsDecl.Name.ToString());

                        var nsSymbol = Compilation.GetOrCreateNamespaceSymbol(namespaceName);

                        var nsBinder = Compilation.BinderFactory.GetBinder(nsDecl, parentBinder)!;
                        CacheBinder(nsDecl, nsBinder);

                        BindNamespaceMembers(
                            nsDecl,
                            nsBinder,
                            nsSymbol,
                            bindMemberSignatures,
                            bindTopLevelFunctionSignatures);
                        break;
                    }

                case TypeDeclarationSyntax typeDecl when IsNominalTypeDeclaration(typeDecl):
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(typeDecl, out _))
                            break;

                        BindNominalTypeDeclaration(typeDecl, parentBinder, objectType, classBinders);
                        break;
                    }

                case UnionDeclarationSyntax unionDecl:
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(unionDecl, out var declaredUnionSymbol))
                            break;

                        var expansion = GetEffectiveUnionDeclaration(unionDecl);
                        var declaringSymbol = (ISymbol)(parentNamespace.AsSourceNamespace() ?? parentNamespace);
                        var namespaceSymbol = parentNamespace.AsSourceNamespace();
                        var unionSymbol = (SourceUnionSymbol)declaredUnionSymbol;
                        var (unionBinder, resolvedSymbol) = RegisterUnionDeclaration(
                            unionDecl,
                            parentBinder,
                            declaringSymbol,
                            namespaceSymbol,
                            unionSymbol,
                            expansion.Shape,
                            expansion.IntroducedMembers);
                        unionBinders.Add((unionDecl, unionBinder, resolvedSymbol, expansion.IntroducedMembers));
                        break;
                    }

                case InterfaceDeclarationSyntax interfaceDecl:
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(interfaceDecl, out var interfaceSymbol))
                            break;

                        var interfaceList = ResolveInterfaceBaseTypes(interfaceDecl, interfaceSymbol, parentBinder);

                        if (!interfaceList.IsDefaultOrEmpty)
                            interfaceSymbol.SetInterfaces(MergeInterfaceSets(interfaceSymbol.Interfaces, interfaceList));

                        var interfaceBinder = new InterfaceDeclarationBinder(parentBinder, interfaceSymbol, interfaceDecl);
                        interfaceBinder.EnsureTypeParameterConstraintTypesResolved(interfaceSymbol.TypeParameters);
                        ValidateTypeDeclarationConstraintAccessibility(interfaceSymbol, "type", interfaceBinder.Diagnostics);
                        CacheBinder(interfaceDecl, interfaceBinder);
                        if (interfaceSymbol.IsSealedHierarchy)
                            ResolveSealedHierarchyPermits(interfaceDecl, interfaceSymbol, interfaceBinder);
                        interfaceBinders.Add((interfaceDecl, interfaceBinder));
                        break;
                    }

                case ExtensionDeclarationSyntax extensionDecl:
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(extensionDecl, out var extensionSymbol))
                            break;

                        var extensionBinder = new ExtensionDeclarationBinder(parentBinder, extensionSymbol, extensionDecl);
                        extensionBinder.EnsureTypeParameterConstraintTypesResolved(extensionSymbol.TypeParameters);
                        ValidateTypeDeclarationConstraintAccessibility(extensionSymbol, "extension", extensionBinder.Diagnostics);
                        CacheBinder(extensionDecl, extensionBinder);

                        extensionBinders.Add((extensionDecl, extensionBinder));
                        break;
                    }

                case DelegateDeclarationSyntax delegateDecl:
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(delegateDecl, out var delegateSymbol))
                            break;

                        // Binder for attributes/type parameter constraints
                        var delegateBinder = new DelegateDeclarationBinder(parentBinder, delegateSymbol, delegateDecl);
                        delegateBinder.EnsureTypeParameterConstraintTypesResolved(delegateSymbol.TypeParameters);
                        TypeMemberBinder.ValidateTypeParameterConstraintAccessibility(
                            delegateSymbol.TypeParameters,
                            delegateSymbol.DeclaredAccessibility,
                            delegateSymbol.ContainingType,
                            "delegate",
                            delegateSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            delegateBinder.Diagnostics);
                        CacheBinder(delegateDecl, delegateBinder);

                        EnsureDelegateMembers(delegateSymbol, delegateDecl, delegateBinder);

                        break;
                    }

                case GlobalStatementSyntax globalStatement
                    when Compilation.IsTopLevelFunctionMember(globalStatement) &&
                         !Compilation.IsFileScopeLocalFunction(globalStatement) &&
                         globalStatement.Statement is FunctionStatementSyntax functionStatement:
                    {
                        if (!Compilation.Options.AllowNamespaceMembers)
                            break;

                        var functionBinder = new FunctionBinder(parentBinder, functionStatement);
                        if (bindMemberSignatures && bindTopLevelFunctionSignatures)
                            _ = functionBinder.GetMethodSymbol();
                        CacheBinder(globalStatement, functionBinder);
                        CacheBinder(functionStatement, functionBinder);
                        break;
                    }

                case ConstDeclarationSyntax constDecl:
                    {
                        if (!Compilation.Options.AllowNamespaceMembers)
                            break;

                        var container = GetOrCreateNamespaceMembersContainer(constDecl, parentNamespace);
                        var constBinder = new TypeMemberBinder(parentBinder, container);
                        constBinder.BindConstDeclaration(constDecl);
                        CacheBinder(constDecl, constBinder);
                        foreach (var declarator in constDecl.Declaration.Declarators)
                            CacheBinder(declarator, constBinder);
                        break;
                    }

                case EnumDeclarationSyntax enumDecl:
                    {
                        if (!Compilation.TryGetDeclaredTypeSymbol(enumDecl, out var enumSymbol))
                            break;

                        var enumBinder = new EnumDeclarationBinder(parentBinder, enumSymbol, enumDecl);
                        CacheBinder(enumDecl, enumBinder);

                        var enumUnderlyingType = ResolveEnumUnderlyingType(enumDecl, parentBinder);
                        enumSymbol.SetEnumUnderlyingType(enumUnderlyingType);

                        RegisterEnumMembers(enumDecl, enumBinder, enumSymbol, parentNamespace.AsSourceNamespace());
                        break;
                    }
            }
        }

        DiscoverSameFileSealedHierarchySubtypes(classBinders, interfaceBinders);
        ValidatePermitsListEntries(classBinders, interfaceBinders);

        if (!bindMemberSignatures)
            return;

        foreach (var (unionDecl, unionBinder, unionSymbol, introducedMembers) in unionBinders)
            RegisterUnionCases(unionDecl, unionBinder, unionSymbol, synthesizeUnionSurface: false, introducedMembers);

        foreach (var (unionDecl, unionBinder, unionSymbol, introducedMembers) in unionBinders)
            RegisterUnionDeclaredMembers(unionDecl, unionBinder, unionSymbol, introducedMembers);

        foreach (var (unionDecl, unionBinder, unionSymbol, introducedMembers) in unionBinders)
            RegisterUnionCases(unionDecl, unionBinder, unionSymbol, synthesizeUnionSurface: true, introducedMembers);

        foreach (var (interfaceDecl, interfaceBinder) in interfaceBinders)
            RegisterInterfaceMembers(interfaceDecl, interfaceBinder);

        foreach (var (extensionDecl, extensionBinder) in extensionBinders)
            RegisterExtensionMembers(extensionDecl, extensionBinder);

        foreach (var (classDecl, classBinder) in classBinders)
        {
            RegisterClassMembers(classDecl, classBinder);
            classBinder.EnsureDefaultConstructor();
        }

        foreach (var (unionDecl, unionBinder, unionSymbol, _) in unionBinders)
            ReportMissingInterfaceMembers(unionSymbol, unionDecl, unionBinder.Diagnostics);

        var checkedClasses = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (classDecl, classBinder) in classBinders)
        {
            var classSymbol = (INamedTypeSymbol)classBinder.ContainingSymbol;
            if (!checkedClasses.Add(classSymbol))
                continue;

            ReportMissingAbstractBaseMembers(classSymbol, classDecl, classBinder.Diagnostics);
            ReportMissingInterfaceMembers(classSymbol, classDecl, classBinder.Diagnostics);
            ReportIncompletePartialMembers(classSymbol, classBinder.Diagnostics);
        }

    }

    private void BindNominalTypeDeclaration(
        TypeDeclarationSyntax declaration,
        Binder parentBinder,
        INamedTypeSymbol? objectType,
        List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)> classBinders)
    {
        var typeSymbol = GetDeclaredTypeSymbol(declaration);
        var declarationBinder = new ClassDeclarationBinder(parentBinder, typeSymbol, declaration);
        declarationBinder.EnsureTypeParameterConstraintTypesResolved(typeSymbol.TypeParameters);
        var effectiveDeclaration = GetEffectiveNominalTypeDeclaration(declaration);

        var valueType = Compilation.GetSpecialType(SpecialType.System_ValueType);
        var defaultBaseType = GetDefaultBaseTypeForNominalDeclaration(effectiveDeclaration, objectType, valueType);
        var defaultInterfaces = GetDefaultNominalInterfaces(typeSymbol);
        var shape = declarationBinder.BindNominalTypeShape(effectiveDeclaration, defaultBaseType, defaultInterfaces);
        var baseTypeSymbol = shape.BaseType;
        var interfaceList = shape.Interfaces;

        if (baseTypeSymbol is not null &&
            !SymbolEqualityComparer.Default.Equals(typeSymbol.BaseType, baseTypeSymbol) &&
            (typeSymbol.BaseType is null || SymbolEqualityComparer.Default.Equals(typeSymbol.BaseType, defaultBaseType)))
        {
            typeSymbol.SetBaseType(baseTypeSymbol);
        }

        if (!interfaceList.IsDefaultOrEmpty)
            typeSymbol.SetInterfaces(MergeInterfaceSets(typeSymbol.Interfaces, interfaceList));

        CacheBinder(declaration, declarationBinder);
        RegisterClassSymbol(declaration, typeSymbol);

        if (typeSymbol.IsSealedHierarchy)
            ResolveSealedHierarchyPermits(declaration, typeSymbol, declarationBinder);

        classBinders.Add((declaration, declarationBinder));
    }

    private TypeDeclarationSyntax GetEffectiveNominalTypeDeclaration(TypeDeclarationSyntax declaration)
    {
        var effectiveDeclaration = declaration;

        foreach (var attribute in declaration.AttributeLists.SelectMany(static list => list.Attributes))
        {
            if (!attribute.IsMacroAttribute())
                continue;

            if (GetMacroExpansion(attribute)?.ReplacementDeclaration is TypeDeclarationSyntax replacement &&
                IsNominalTypeDeclaration(replacement))
            {
                effectiveDeclaration = replacement;
            }
        }

        return effectiveDeclaration;
    }

    private readonly record struct EffectiveUnionDeclaration(
        UnionDeclarationSyntax Shape,
        ImmutableArray<MemberDeclarationSyntax> IntroducedMembers);

    private EffectiveUnionDeclaration GetEffectiveUnionDeclaration(UnionDeclarationSyntax declaration)
    {
        var effectiveDeclaration = declaration;
        var introducedMembers = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();

        foreach (var attribute in declaration.AttributeLists.SelectMany(static list => list.Attributes))
        {
            if (!attribute.IsMacroAttribute())
                continue;

            var expansion = GetMacroExpansion(attribute);
            if (expansion is null)
                continue;

            if (expansion.ReplacementDeclaration is UnionDeclarationSyntax replacement)
                effectiveDeclaration = replacement;

            introducedMembers.AddRange(expansion.IntroducedMembers);
        }

        foreach (var introducedMember in introducedMembers)
            RegisterMacroContainingTypeSyntax(introducedMember, declaration);

        if (!ReferenceEquals(effectiveDeclaration, declaration))
        {
            RegisterMacroReplacementSyntaxTrees(
                declaration,
                [effectiveDeclaration, .. introducedMembers]);
        }

        return new EffectiveUnionDeclaration(effectiveDeclaration, introducedMembers.ToImmutable());
    }

    internal bool TryEnsureTypeMemberSignaturesDeclared(SourceNamedTypeSymbol typeSymbol)
    {
        foreach (var reference in typeSymbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                TryEnsureTypeMemberSignaturesDeclared(declaration))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEnsureTypeMemberSignaturesDeclared(TypeDeclarationSyntax declaration)
    {
        if (!IsNominalTypeDeclaration(declaration) ||
            !Compilation.TryGetDeclaredTypeSymbol(declaration, out _))
        {
            return false;
        }

        if (!_typeMemberSignaturesDeclared.TryAdd(declaration, 0))
            return true;

        DeclareTypeMemberSignatures(declaration);

        return true;
    }

    private void DeclareTypeMemberSignatures(TypeDeclarationSyntax declaration)
    {
        foreach (var effectiveMember in GetEffectiveTypeMembers(declaration))
        {
            switch (effectiveMember.EffectiveSyntax)
            {
                case FieldDeclarationSyntax fieldDeclaration:
                    MemberSignatureDeclarationPass.DeclareFieldSignature(this, fieldDeclaration);
                    break;

                case MethodDeclarationSyntax methodDeclaration:
                    MemberSignatureDeclarationPass.DeclareMethodSignature(this, methodDeclaration);
                    break;

                case ConstructorDeclarationSyntax constructorDeclaration:
                    MemberSignatureDeclarationPass.DeclareConstructorSignature(this, constructorDeclaration);
                    break;

                case ParameterlessConstructorDeclarationSyntax constructorDeclaration:
                    MemberSignatureDeclarationPass.DeclareParameterlessConstructorSignature(this, constructorDeclaration);
                    break;

                case PropertyDeclarationSyntax propertyDeclaration:
                    MemberSignatureDeclarationPass.DeclarePropertySignature(this, propertyDeclaration);
                    break;

                case IndexerDeclarationSyntax indexerDeclaration:
                    MemberSignatureDeclarationPass.DeclareIndexerSignature(this, indexerDeclaration);
                    break;

                case EventDeclarationSyntax eventDeclaration:
                    MemberSignatureDeclarationPass.DeclareEventSignature(this, eventDeclaration);
                    break;
            }
        }
    }

    private static bool IsNominalTypeDeclaration(TypeDeclarationSyntax declaration)
        => declaration is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax;

    private void ValidatePermitsListEntries(
        List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)> classBinders,
        List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)> interfaceBinders)
    {
        foreach (var (declaration, binder) in GetSealedHierarchyBinders(classBinders, interfaceBinders))
        {
            var typeSymbol = (SourceNamedTypeSymbol)binder.ContainingSymbol;
            if (!typeSymbol.IsSealedHierarchy || !typeSymbol.HasExplicitPermits)
                continue;

            var permitsClause = GetPermitsClause(declaration);

            if (permitsClause is null)
                continue;

            var typeIndex = 0;
            foreach (var typeSyntax in permitsClause.Types)
            {
                if (typeIndex >= typeSymbol.PermittedDirectSubtypes.Length)
                    break;

                var permitted = typeSymbol.PermittedDirectSubtypes[typeIndex];
                typeIndex++;

                if (!IsDirectSealedHierarchySubtype(typeSymbol, permitted))
                {
                    _declarationDiagnostics.ReportPermitsTypeNotDirectSubtype(
                        permitted.Name,
                        typeSymbol.Name,
                        typeSyntax.GetLocation());
                }
            }
        }
    }

    private void DiscoverSameFileSealedHierarchySubtypes(
        List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)> classBinders,
        List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)> interfaceBinders)
    {
        var candidates = GetSealedHierarchyCandidates(classBinders, interfaceBinders).ToArray();

        foreach (var (declaration, binder) in GetSealedHierarchyBinders(classBinders, interfaceBinders))
        {
            var typeSymbol = (SourceNamedTypeSymbol)binder.ContainingSymbol;
            if (!typeSymbol.IsSealedHierarchy || typeSymbol.HasExplicitPermits)
                continue;

            var sealedFile = typeSymbol.SealedHierarchySourceFile;
            var directSubtypes = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

            foreach (var (otherDecl, otherSymbol) in candidates)
            {
                if (ReferenceEquals(otherSymbol, typeSymbol))
                    continue;

                if (string.Equals(otherDecl.SyntaxTree?.FilePath, sealedFile, StringComparison.Ordinal) &&
                    IsDirectSealedHierarchySubtype(typeSymbol, otherSymbol))
                {
                    directSubtypes.Add(otherSymbol);
                }
            }

            typeSymbol.SetPermittedDirectSubtypes(directSubtypes.ToImmutable());
        }
    }

    private void ResolveSealedHierarchyPermits(
        TypeDeclarationSyntax declaration,
        SourceNamedTypeSymbol typeSymbol,
        Binder declarationBinder)
    {
        var permitsClause = GetPermitsClause(declaration);

        if (permitsClause is null)
            return;

        var permitted = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeSyntax in permitsClause.Types)
        {
            if (!declarationBinder.TryBindNamedTypeFromTypeSyntax(typeSyntax, out var resolved, reportDiagnostics: true) || resolved is null)
            {
                _declarationDiagnostics.ReportPermitsTypeNotFound(
                    typeSyntax.ToString(),
                    typeSyntax.GetLocation());
                continue;
            }

            if (!seen.Add(resolved.Name))
            {
                _declarationDiagnostics.ReportPermitsTypeDuplicate(
                    resolved.Name,
                    typeSyntax.GetLocation());
                continue;
            }

            permitted.Add(resolved);
        }

        typeSymbol.SetPermittedDirectSubtypes(permitted.ToImmutable());
    }

    private static PermitsClauseSyntax? GetPermitsClause(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDecl => classDecl.PermitsClause,
            RecordDeclarationSyntax recordDecl => recordDecl.PermitsClause,
            InterfaceDeclarationSyntax interfaceDecl => interfaceDecl.PermitsClause,
            _ => null,
        };

    private static bool IsDirectSealedHierarchySubtype(INamedTypeSymbol root, INamedTypeSymbol candidate)
    {
        var rootDefinition = root.OriginalDefinition as INamedTypeSymbol ?? root;

        if (candidate.BaseType is not null &&
            SymbolEqualityComparer.Default.Equals(
                candidate.BaseType.OriginalDefinition as INamedTypeSymbol ?? candidate.BaseType,
                rootDefinition))
        {
            return true;
        }

        return candidate.Interfaces.Any(interfaceType =>
            SymbolEqualityComparer.Default.Equals(
                interfaceType.OriginalDefinition as INamedTypeSymbol ?? interfaceType,
                rootDefinition));
    }

    private static IEnumerable<(TypeDeclarationSyntax Syntax, Binder Binder)> GetSealedHierarchyBinders(
        List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)> classBinders,
        List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)> interfaceBinders)
    {
        foreach (var (syntax, binder) in classBinders)
            yield return (syntax, binder);

        foreach (var (syntax, binder) in interfaceBinders)
            yield return (syntax, binder);
    }

    private static IEnumerable<(TypeDeclarationSyntax Syntax, SourceNamedTypeSymbol Symbol)> GetSealedHierarchyCandidates(
        List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)> classBinders,
        List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)> interfaceBinders)
    {
        foreach (var (syntax, binder) in classBinders)
            yield return (syntax, (SourceNamedTypeSymbol)binder.ContainingSymbol);

        foreach (var (syntax, binder) in interfaceBinders)
            yield return (syntax, (SourceNamedTypeSymbol)binder.ContainingSymbol);
    }

    private void MergeSameFileSealedHierarchySubtypes(
        SourceNamedTypeSymbol root,
        IEnumerable<(TypeDeclarationSyntax Syntax, SourceNamedTypeSymbol Symbol)> candidates)
    {
        if (!root.IsSealedHierarchy || root.HasExplicitPermits)
            return;

        var sealedFile = root.SealedHierarchySourceFile;
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var existing in root.PermittedDirectSubtypes)
        {
            if (seen.Add(existing))
                builder.Add(existing);
        }

        foreach (var (syntax, symbol) in candidates)
        {
            if (ReferenceEquals(symbol, root))
                continue;

            if (!string.Equals(syntax.SyntaxTree?.FilePath, sealedFile, StringComparison.Ordinal))
                continue;

            if (!IsDirectSealedHierarchySubtype(root, symbol))
                continue;

            if (seen.Add(symbol))
                builder.Add(symbol);
        }

        root.SetPermittedDirectSubtypes(builder.ToImmutable());
    }

    private static void ReportRedundantTypeModifiers(
        TypeDeclarationSyntax declaration,
        DiagnosticBag diagnostics)
    {
        if (declaration is not (ClassDeclarationSyntax or RecordDeclarationSyntax))
            return;

        var hasAbstractModifier = declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.AbstractKeyword);
        if (!hasAbstractModifier)
            return;

        var hasOpenModifier = declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.OpenKeyword);
        if (hasOpenModifier)
        {
            var openModifier = declaration.Modifiers.First(static m => m.Kind == SyntaxKind.OpenKeyword);
            diagnostics.ReportOpenModifierRedundantOnAbstractClass(
                declaration.Identifier.ValueText,
                openModifier.GetLocation());
        }

        var hasSealedModifier = declaration.Modifiers.Any(static m => m.Kind == SyntaxKind.SealedKeyword);
        if (!hasSealedModifier)
            return;

        var abstractModifier = declaration.Modifiers.First(static m => m.Kind == SyntaxKind.AbstractKeyword);
        diagnostics.ReportAbstractModifierRedundantOnSealedClass(
            declaration.Identifier.ValueText,
            abstractModifier.GetLocation());
    }

    private static void ReportRedundantPublicTypeModifierIfNeeded(
        MemberDeclarationSyntax declaration,
        DiagnosticBag diagnostics)
    {
        SyntaxTokenList modifiers;
        switch (declaration)
        {
            case TypeDeclarationSyntax typeDeclaration:
                modifiers = typeDeclaration.Modifiers;
                break;
            case EnumDeclarationSyntax enumDeclaration:
                modifiers = enumDeclaration.Modifiers;
                break;
            case DelegateDeclarationSyntax delegateDeclaration:
                modifiers = delegateDeclaration.Modifiers;
                break;
            case ExtensionDeclarationSyntax extensionDeclaration:
                modifiers = extensionDeclaration.Modifiers;
                break;
            case UnionDeclarationSyntax unionDeclaration:
                modifiers = unionDeclaration.Modifiers;
                break;
            default:
                return;
        }

        foreach (var modifier in modifiers)
        {
            if (modifier.Kind != SyntaxKind.PublicKeyword)
                continue;

            diagnostics.ReportPublicModifierRedundant(modifier.GetLocation());
            return;
        }
    }

    private static void ReportInvalidTypeModifiers(
        MemberDeclarationSyntax declaration,
        bool isNestedType,
        DiagnosticBag diagnostics)
    {
        var (typeName, typeKind, modifiers, allowed) = declaration switch
        {
            ClassDeclarationSyntax classDecl => (
                classDecl.Identifier.ValueText,
                "class",
                classDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.SealedKeyword,
                    SyntaxKind.PartialKeyword,
                    SyntaxKind.OpenKeyword,
                }),
            RecordDeclarationSyntax recordDecl => (
                recordDecl.Identifier.ValueText,
                "record",
                recordDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.SealedKeyword,
                    SyntaxKind.PartialKeyword,
                    SyntaxKind.OpenKeyword,
                    SyntaxKind.RecordKeyword,
                }),
            StructDeclarationSyntax structDecl => (
                structDecl.Identifier.ValueText,
                "struct",
                structDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                    SyntaxKind.PartialKeyword,
                    SyntaxKind.RefKeyword,
                    SyntaxKind.ReadonlyKeyword,
                }),
            InterfaceDeclarationSyntax interfaceDecl => (
                interfaceDecl.Identifier.ValueText,
                "interface",
                interfaceDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                    SyntaxKind.SealedKeyword,
                    SyntaxKind.PartialKeyword,
                }),
            EnumDeclarationSyntax enumDecl => (
                enumDecl.Identifier.ValueText,
                "enum",
                enumDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                }),
            DelegateDeclarationSyntax delegateDecl => (
                delegateDecl.Identifier.ValueText,
                "delegate",
                delegateDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                }),
            ExtensionDeclarationSyntax extensionDecl => (
                extensionDecl.Identifier.ValueText,
                "extension",
                extensionDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                }),
            UnionDeclarationSyntax unionDecl => (
                unionDecl.Identifier.ValueText,
                "union",
                unionDecl.Modifiers,
                new HashSet<SyntaxKind>
                {
                    SyntaxKind.PublicKeyword,
                    SyntaxKind.PrivateKeyword,
                    SyntaxKind.InternalKeyword,
                    SyntaxKind.ProtectedKeyword,
                    SyntaxKind.FileprivateKeyword,
                }),
            _ => (null, null, default, null),
        };

        if (typeName is null || typeKind is null || allowed is null)
            return;

        var refModifierSeen = false;
        var readonlyModifierSeen = false;
        foreach (var modifier in modifiers)
        {
            var kind = modifier.Kind;
            var text = modifier.Text;

            if (kind == SyntaxKind.RefKeyword)
            {
                if (refModifierSeen)
                    diagnostics.ReportDuplicateModifier(text, modifier.GetLocation());
                refModifierSeen = true;
            }
            else if (kind == SyntaxKind.ReadonlyKeyword)
            {
                if (readonlyModifierSeen)
                    diagnostics.ReportDuplicateModifier(text, modifier.GetLocation());
                readonlyModifierSeen = true;
            }

            if (kind is SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword && !isNestedType)
            {
                diagnostics.ReportModifierNotValidOnMember(text, typeKind, typeName, modifier.GetLocation());
                continue;
            }

            if (!allowed.Contains(kind))
                diagnostics.ReportModifierNotValidOnMember(text, typeKind, typeName, modifier.GetLocation());
        }
    }

    private static INamedTypeSymbol? GetDefaultBaseTypeForNominalDeclaration(
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol? objectType,
        INamedTypeSymbol? valueType)
        => IsStructLikeNominalType(declaration) ? valueType : objectType;

    private static bool IsStructLikeNominalType(TypeDeclarationSyntax declaration)
        => declaration is StructDeclarationSyntax ||
           declaration is RecordDeclarationSyntax { ClassOrStructKeyword.Kind: SyntaxKind.StructKeyword };

    private ImmutableArray<INamedTypeSymbol> GetDefaultNominalInterfaces(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol is not SourceNamedTypeSymbol sourceType || !sourceType.IsRecord)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        if (Compilation.GetTypeByMetadataName("System.IEquatable`1") is not INamedTypeSymbol equatableDefinition)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        var equatableType = (INamedTypeSymbol)equatableDefinition.Construct(typeSymbol);
        return ImmutableArray.Create(equatableType);
    }

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

    private static void ValidateTypeDeclarationConstraintAccessibility(
        INamedTypeSymbol typeSymbol,
        string typeKind,
        DiagnosticBag diagnostics)
    {
        TypeMemberBinder.ValidateTypeParameterConstraintAccessibility(
            typeSymbol.TypeParameters,
            typeSymbol.DeclaredAccessibility,
            typeSymbol.ContainingType,
            typeKind,
            typeSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            diagnostics);
    }

    private void EnsureDelegateMembers(SourceNamedTypeSymbol delegateSymbol, DelegateDeclarationSyntax delegateDecl, Binder binder)
    {
        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var intPtrType = Compilation.GetSpecialType(SpecialType.System_IntPtr);
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);

        void RegisterMember(SourceNamedTypeSymbol owner, ISymbol member)
        {
            if (!owner.GetMembers().Any(m => SymbolEqualityComparer.Default.Equals(m, member)))
                owner.AddMember(member);
        }

        ITypeSymbol ResolveTypeSyntaxForSignature(TypeSyntax typeSyntax, RefKind refKindHint)
        {
            return binder.BindTypeSyntaxAndReport(typeSyntax, refKindHint: refKindHint);
        }

        ITypeSymbol ResolveParameterTypeSyntaxForSignature(TypeSyntax typeSyntax, RefKind refKind)
        {
            var boundTypeSyntax = refKind.IsByRef && typeSyntax is ByRefTypeSyntax byRefType
                ? byRefType.ElementType
                : typeSyntax;
            return ResolveTypeSyntaxForSignature(boundTypeSyntax, RefKind.None);
        }

        // .ctor(object, IntPtr)
        var ctor = new SourceMethodSymbol(
            ".ctor",
            unitType!,
            ImmutableArray<SourceParameterSymbol>.Empty,
            delegateSymbol,
            delegateSymbol,
            delegateSymbol.ContainingNamespace,
            new[] { delegateDecl.GetLocation() },
            Array.Empty<SyntaxReference>(),
            isStatic: false,
            methodKind: MethodKind.Constructor,
            declaredAccessibility: Accessibility.Public);

        var ctorParams = ImmutableArray.Create(
            new SourceParameterSymbol(
                "object",
                objectType!,
                ctor,
                delegateSymbol,
                delegateSymbol.ContainingNamespace,
                new[] { delegateDecl.GetLocation() },
                Array.Empty<SyntaxReference>(),
                RefKind.None),
            new SourceParameterSymbol(
                "method",
                intPtrType!,
                ctor,
                delegateSymbol,
                delegateSymbol.ContainingNamespace,
                new[] { delegateDecl.GetLocation() },
                Array.Empty<SyntaxReference>(),
                RefKind.None));

        ctor.SetParameters(ctorParams);
        RegisterMember(delegateSymbol, ctor);

        // Invoke
        var returnType = delegateDecl.ReturnType is null
            ? unitType!
            : ResolveTypeSyntaxForSignature(delegateDecl.ReturnType.Type, RefKind.None);

        TypeMemberBinder.ValidateTypeAccessibility(
            returnType,
            delegateSymbol.DeclaredAccessibility,
            delegateSymbol.ContainingType,
            "delegate",
            delegateSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            "return",
            binder.Diagnostics,
            delegateDecl.ReturnType?.Type.GetLocation() ?? delegateDecl.Identifier.GetLocation());

        var invoke = new SourceMethodSymbol(
            "Invoke",
            returnType,
            ImmutableArray<SourceParameterSymbol>.Empty,
            delegateSymbol,
            delegateSymbol,
            delegateSymbol.ContainingNamespace,
            new[] { delegateDecl.GetLocation() },
            Array.Empty<SyntaxReference>(),
            isStatic: false,
            methodKind: MethodKind.Ordinary,
            declaredAccessibility: Accessibility.Public);

        var invokeParams = ImmutableArray.CreateBuilder<SourceParameterSymbol>(delegateDecl.ParameterList.Parameters.Count);
        foreach (var p in delegateDecl.ParameterList.Parameters)
        {
            var refKind = ParameterSyntaxUtilities.GetRefKind(p);
            var typeSyntax = p.TypeAnnotation!.Type;
            var pType = ResolveParameterTypeSyntaxForSignature(typeSyntax, refKind);

            TypeMemberBinder.ValidateTypeAccessibility(
                pType,
                delegateSymbol.DeclaredAccessibility,
                delegateSymbol.ContainingType,
                "delegate",
                delegateSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                $"parameter '{p.Identifier.ValueText}'",
                binder.Diagnostics,
                typeSyntax.GetLocation());

            invokeParams.Add(new SourceParameterSymbol(
                p.Identifier.ValueText,
                pType,
                invoke,
                delegateSymbol,
                delegateSymbol.ContainingNamespace,
                new[] { p.GetLocation() },
                new[] { p.GetReference() },
                refKind,
                scopedKind: ParameterSyntaxUtilities.GetScopedKind(p)));
        }

        invoke.SetParameters(invokeParams.ToImmutable());
        RegisterMember(delegateSymbol, invoke);
    }

    private void RegisterUnionDeclaredMembers(
        UnionDeclarationSyntax unionDecl,
        UnionDeclarationBinder unionBinder,
        SourceUnionSymbol unionSymbol,
        ImmutableArray<MemberDeclarationSyntax> introducedMembers = default)
    {
        var additionalMembers = introducedMembers.IsDefault
            ? ImmutableArray<MemberDeclarationSyntax>.Empty
            : introducedMembers;
        foreach (var member in unionDecl.Members.Concat(additionalMembers))
        {
            ReportInvalidUnionMemberKindIfNeeded(unionSymbol, unionBinder, member);

            switch (member)
            {
                case CaseDeclarationSyntax:
                    continue;

                case FieldDeclarationSyntax fieldDecl:
                    {
                        var fieldBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        fieldBinder.BindFieldDeclaration(fieldDecl);
                        CacheBinder(fieldDecl, fieldBinder);
                        foreach (var decl in fieldDecl.Declaration.Declarators)
                        {
                            CacheBinder(decl, fieldBinder);
                            ValidateReservedUnionMemberName(unionSymbol, decl.Identifier.ValueText, decl.Identifier.GetLocation(), unionBinder.Diagnostics);
                        }
                        break;
                    }

                case ConstDeclarationSyntax constDecl:
                    {
                        var constBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        constBinder.BindConstDeclaration(constDecl);
                        CacheBinder(constDecl, constBinder);
                        foreach (var decl in constDecl.Declaration.Declarators)
                        {
                            CacheBinder(decl, constBinder);
                            ValidateReservedUnionMemberName(unionSymbol, decl.Identifier.ValueText, decl.Identifier.GetLocation(), unionBinder.Diagnostics);
                        }
                        break;
                    }

                case MethodDeclarationSyntax methodDecl:
                    {
                        var memberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var methodBinder = memberBinder.BindMethodDeclaration(methodDecl);
                        CacheBinder(methodDecl, methodBinder);
                        if (methodBinder.ContainingSymbol is IMethodSymbol methodSymbol)
                        {
                            RegisterMethodSymbol(methodDecl, methodSymbol);
                            ValidateUnionDeclaredMethod(unionSymbol, methodSymbol, methodDecl, unionBinder.Diagnostics);
                        }
                        break;
                    }

                case OperatorDeclarationSyntax operatorDecl:
                    {
                        var operatorBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var boundOperatorBinder = operatorBinder.BindOperatorDeclaration(operatorDecl);
                        CacheBinder(operatorDecl, boundOperatorBinder);
                        if (boundOperatorBinder.ContainingSymbol is IMethodSymbol operatorSymbol)
                            ValidateUnionDeclaredOperator(unionSymbol, operatorSymbol, operatorDecl, unionBinder.Diagnostics);
                        break;
                    }

                case ConversionOperatorDeclarationSyntax conversionDecl:
                    {
                        var conversionBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var boundConversionBinder = conversionBinder.BindConversionOperatorDeclaration(conversionDecl);
                        CacheBinder(conversionDecl, boundConversionBinder);
                        break;
                    }

                case PropertyDeclarationSyntax propDecl:
                    {
                        var propMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var accessorBinders = propMemberBinder.BindPropertyDeclaration(propDecl);
                        CacheBinder(propDecl, propMemberBinder);
                        if (TryGetPropertySymbol(propDecl, out var propertySymbol))
                        {
                            RegisterUnionMember(propertySymbol);
                            if (propertySymbol.GetMethod is { } getter)
                                RegisterUnionMember(getter);
                            if (propertySymbol.SetMethod is { } setter)
                                RegisterUnionMember(setter);
                        }
                        ValidateReservedUnionMemberName(unionSymbol, propDecl.Identifier.ValueText, propDecl.Identifier.GetLocation(), unionBinder.Diagnostics);
                        ReportInvalidUnionPropertyIfNeeded(unionSymbol, unionBinder, propDecl);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }

                case EventDeclarationSyntax eventDecl:
                    {
                        var eventMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var eventAccessors = eventMemberBinder.BindEventDeclaration(eventDecl);
                        CacheBinder(eventDecl, eventMemberBinder);
                        ValidateReservedUnionMemberName(unionSymbol, eventDecl.Identifier.ValueText, eventDecl.Identifier.GetLocation(), unionBinder.Diagnostics);
                        foreach (var kv in eventAccessors)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }

                case IndexerDeclarationSyntax indexerDecl:
                    {
                        var indexerMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var indexerAccessorBinders = indexerMemberBinder.BindIndexerDeclaration(indexerDecl);
                        CacheBinder(indexerDecl, indexerMemberBinder);
                        foreach (var kv in indexerAccessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }

                case ConstructorDeclarationSyntax ctorDecl:
                    {
                        var ctorMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var ctorBinder = ctorMemberBinder.BindConstructorDeclaration(ctorDecl);
                        CacheBinder(ctorDecl, ctorBinder);
                        break;
                    }

                case ParameterlessConstructorDeclarationSyntax initDecl:
                    {
                        var initMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var initBinder = initMemberBinder.BindInitDeclaration(initDecl);
                        CacheBinder(initDecl, initBinder);
                        break;
                    }

                case InitializerBlockDeclarationSyntax initBlockDecl:
                    {
                        var initBlockMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var initBlockBinder = initBlockMemberBinder.BindInitBlockDeclaration(initBlockDecl);
                        CacheBinder(initBlockDecl, initBlockBinder);
                        break;
                    }

                case FinallyDeclarationSyntax finalDecl:
                    {
                        var finalMemberBinder = new TypeMemberBinder(unionBinder, unionSymbol);
                        var finalBinder = finalMemberBinder.BindFinallyDeclaration(finalDecl);
                        CacheBinder(finalDecl, finalBinder);
                        break;
                    }
            }
        }

        void RegisterUnionMember(ISymbol member)
        {
            if (!unionSymbol.GetMembers().Any(existing =>
                    SymbolEqualityComparer.Default.Equals(existing, member)))
            {
                unionSymbol.AddMember(member);
            }
        }
    }

    private static void ReportInvalidUnionMemberKindIfNeeded(
        SourceUnionSymbol unionSymbol,
        UnionDeclarationBinder unionBinder,
        MemberDeclarationSyntax member)
    {
        if (member is CaseDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or MethodDeclarationSyntax)
            return;

        var memberKind = member switch
        {
            FieldDeclarationSyntax => "field declarations",
            ConstDeclarationSyntax => "constant declarations",
            EventDeclarationSyntax => "event declarations",
            ConstructorDeclarationSyntax or ParameterlessConstructorDeclarationSyntax => "constructors",
            InitializerBlockDeclarationSyntax => "initializer blocks",
            FinallyDeclarationSyntax => "finalizers",
            OperatorDeclarationSyntax => "operator declarations",
            ConversionOperatorDeclarationSyntax => "conversion operator declarations",
            DelegateDeclarationSyntax => "nested delegate declarations",
            TypeDeclarationSyntax => "nested type declarations",
            _ => "this member kind",
        };

        unionBinder.Diagnostics.ReportUnionMemberKindNotAllowed(
            unionSymbol.Name,
            memberKind,
            member.GetLocation());
    }

    private static void ReportInvalidUnionPropertyIfNeeded(
        SourceUnionSymbol unionSymbol,
        UnionDeclarationBinder unionBinder,
        PropertyDeclarationSyntax property)
    {
        if (!IsInstanceStorageProperty(property))
            return;

        unionBinder.Diagnostics.ReportUnionStoragePropertyNotAllowed(
            unionSymbol.Name,
            property.Identifier.ValueText,
            property.Identifier.GetLocation());
    }

    private void RegisterUnionCases(
        UnionDeclarationSyntax unionDecl,
        UnionDeclarationBinder unionBinder,
        SourceUnionSymbol unionSymbol,
        bool synthesizeUnionSurface,
        ImmutableArray<MemberDeclarationSyntax> introducedMembers = default)
    {
        static IEnumerable<CaseDeclarationSyntax> GetCaseDeclarations(UnionDeclarationSyntax declaration)
            => declaration.Members.OfType<CaseDeclarationSyntax>();

        var allCasesAlreadyRegistered = true;
        foreach (var caseClause in GetCaseDeclarations(unionDecl))
        {
            if (!TryGetUnionCaseSymbol(caseClause, out _))
            {
                allCasesAlreadyRegistered = false;
                break;
            }
        }

        var shouldRegisterCaseDeclarations = !allCasesAlreadyRegistered;

        var namespaceSymbol = unionBinder.CurrentNamespace?.AsSourceNamespace()
            ?? unionSymbol.ContainingNamespace?.AsSourceNamespace();
        var caseSymbols = unionSymbol.DeclaredCaseTypes.ToList();
        var memberTypes = unionSymbol.MemberTypes.ToList();
        var payloadFields = unionSymbol.PayloadFields.OfType<SourceFieldSymbol>().ToList();
        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var valueType = Compilation.GetSpecialType(SpecialType.System_ValueType);
        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        var systemType = Compilation.GetSpecialType(SpecialType.System_Type);
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var objectToString = GetObjectToStringMethod();
        var ordinal = caseSymbols.Count;

        void RegisterMember(SourceNamedTypeSymbol owner, ISymbol member)
        {
            if (!owner.GetMembers().Any(m => SymbolEqualityComparer.Default.Equals(m, member)))
                owner.AddMember(member);
        }

        void RegisterCaseMember(ISymbol member)
            => RegisterMember((SourceNamedTypeSymbol)member.ContainingType!, member);

        void EnsureUnionValueProperty(ITypeSymbol valuePropertyType, Location location, SyntaxReference reference)
        {
            if (unionSymbol.GetMembers("Value").OfType<IPropertySymbol>().Any())
                return;

            var valueProperty = new SourcePropertySymbol(
                "Value",
                valuePropertyType,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                [reference],
                declaredAccessibility: Accessibility.Public);

            var getter = new SourceMethodSymbol(
                "get_Value",
                valuePropertyType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                valueProperty,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>(),
                isStatic: false,
                methodKind: MethodKind.PropertyGet,
                declaredAccessibility: Accessibility.Public);

            valueProperty.SetAccessors(getter, null);
            RegisterMember(unionSymbol, valueProperty);
            RegisterMember(unionSymbol, getter);
        }

        void EnsureUnionHasValueProperty(Location location, SyntaxReference reference)
        {
            if (unionSymbol.GetMembers("HasValue").OfType<IPropertySymbol>().Any())
                return;

            var hasValueProperty = new SourcePropertySymbol(
                "HasValue",
                boolType!,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                [reference],
                declaredAccessibility: Accessibility.Public);

            var getter = new SourceMethodSymbol(
                "get_HasValue",
                boolType!,
                ImmutableArray<SourceParameterSymbol>.Empty,
                hasValueProperty,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>(),
                isStatic: false,
                methodKind: MethodKind.PropertyGet,
                declaredAccessibility: Accessibility.Public);

            hasValueProperty.SetAccessors(getter, null);
            RegisterMember(unionSymbol, hasValueProperty);
            RegisterMember(unionSymbol, getter);
        }

        void EnsureUnionToStringMethod(Location location, SyntaxReference reference)
        {
            if (!Compilation.Options.SynthesizeStructuralToString)
                return;

            if (HasDeclaredUnionToStringMethod(unionDecl, introducedMembers))
                return;

            var unionToString = new SourceMethodSymbol(
                "ToString",
                stringType!,
                ImmutableArray<SourceParameterSymbol>.Empty,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                [reference],
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                isOverride: true,
                declaredAccessibility: Accessibility.Public);

            unionToString.SetOverriddenMethod(objectToString);
            RegisterMember(unionSymbol, unionToString);
        }

        void EnsureUnionHelperMethods(Location location, SyntaxReference reference)
        {
            if (!Compilation.Options.SynthesizeStructuralToString)
                return;

            if (!HasMethod(unionSymbol, SynthesizedUnionMethodNames.FormatValueHelper, MethodKind.Ordinary, objectType!))
            {
                var formatValueHelper = new SourceMethodSymbol(
                    SynthesizedUnionMethodNames.FormatValueHelper,
                    stringType!,
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    unionSymbol,
                    unionSymbol,
                    namespaceSymbol,
                    [location],
                    [reference],
                    isStatic: true,
                    methodKind: MethodKind.Ordinary,
                    declaredAccessibility: Accessibility.Private);

                var formatValueParameter = new SourceParameterSymbol(
                    "value",
                    objectType!,
                    formatValueHelper,
                    unionSymbol,
                    namespaceSymbol,
                    [location],
                    [reference]);
                formatValueHelper.SetParameters([formatValueParameter]);
                RegisterMember(unionSymbol, formatValueHelper);
            }
        }

        static ITypeSymbol GetUnionContentMemberType(ITypeSymbol memberType)
            => UnionContentNullability.GetNonNullContentType(memberType, out _);

        void RegisterUnionMemberArtifacts(
            ITypeSymbol memberType,
            ITypeSymbol artifactType,
            Location location,
            SyntaxReference reference)
        {
            memberTypes.Add(memberType);

            var payloadField = new SourceFieldSymbol(
                UnionFieldUtilities.GetPayloadFieldName(GetUnionMemberMetadataName(memberType)),
                artifactType,
                isStatic: false,
                isMutable: true,
                isConst: false,
                constantValue: null,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                [reference],
                null,
                declaredAccessibility: Accessibility.Private);

            payloadFields.Add(payloadField);

            var unionConstructor = new SourceMethodSymbol(
                ".ctor",
                unitType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>(),
                isStatic: false,
                methodKind: MethodKind.Constructor,
                declaredAccessibility: Accessibility.Public);

            var unionConstructorParameter = new SourceParameterSymbol(
                "value",
                artifactType,
                unionConstructor,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>());

            unionConstructor.SetParameters([unionConstructorParameter]);
            RegisterMember(unionSymbol, unionConstructor);

            if (UnionContentNullability.IsNullableContentType(artifactType))
                return;

            var tryGetMethod = new SourceMethodSymbol(
                "TryGetValue",
                boolType!,
                ImmutableArray<SourceParameterSymbol>.Empty,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>(),
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Public);

            var tryGetParameter = new SourceParameterSymbol(
                "value",
                artifactType is NullableTypeSymbol { UnderlyingType.IsValueType: true } nullableValueArtifact
                    ? nullableValueArtifact.UnderlyingType
                    : artifactType,
                tryGetMethod,
                unionSymbol,
                namespaceSymbol,
                [location],
                Array.Empty<SyntaxReference>(),
                RefKind.Out);

            tryGetMethod.SetParameters([tryGetParameter]);
            RegisterMember(unionSymbol, tryGetMethod);
        }

        static ITypeSymbol SubstituteTypeParameters(
            ITypeSymbol type,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
        {
            if (type is ITypeParameterSymbol parameter &&
                substitutions.TryGetValue(parameter, out var replacement))
            {
                return replacement;
            }

            if (type is NullableTypeSymbol nullableType)
            {
                var substituted = SubstituteTypeParameters(nullableType.UnderlyingType, substitutions);
                return SymbolEqualityComparer.Default.Equals(substituted, nullableType.UnderlyingType)
                    ? type
                    : substituted.ApplySubstitutedNullability(nullableType);
            }

            if (type is RefTypeSymbol refType)
            {
                var substituted = SubstituteTypeParameters(refType.ElementType, substitutions);
                return SymbolEqualityComparer.Default.Equals(substituted, refType.ElementType)
                    ? type
                    : new RefTypeSymbol(substituted);
            }

            if (type is IAddressTypeSymbol addressType)
            {
                var substituted = SubstituteTypeParameters(addressType.ReferencedType, substitutions);
                return SymbolEqualityComparer.Default.Equals(substituted, addressType.ReferencedType)
                    ? type
                    : new AddressTypeSymbol(substituted);
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                var substituted = SubstituteTypeParameters(arrayType.ElementType, substitutions);
                return SymbolEqualityComparer.Default.Equals(substituted, arrayType.ElementType)
                    ? type
                    : new ArrayTypeSymbol(arrayType.BaseType, substituted, arrayType.ContainingSymbol, arrayType.ContainingType, arrayType.ContainingNamespace, [], arrayType.Rank, arrayType.FixedLength);
            }

            if (type is INamedTypeSymbol namedType)
            {
                var typeArguments = TypeSubstitution.GetShallowTypeArguments(namedType);
                if (typeArguments.IsDefaultOrEmpty)
                    return type;

                var substituted = new ITypeSymbol[typeArguments.Length];
                var changed = false;

                for (var i = 0; i < typeArguments.Length; i++)
                {
                    substituted[i] = SubstituteTypeParameters(typeArguments[i], substitutions);
                    changed |= !SymbolEqualityComparer.Default.Equals(substituted[i], typeArguments[i]);
                }

                if (changed)
                {
                    var definition = TypeSubstitution.GetDefinitionForSubstitution(namedType);
                    return definition.Construct(substituted);
                }
            }

            return type;
        }

        static void CollectReferencedTypeParameters(
            ITypeSymbol type,
            HashSet<ITypeParameterSymbol> knownUnionTypeParameters,
            List<ITypeParameterSymbol> usedUnionTypeParameters,
            HashSet<ITypeParameterSymbol> seen)
        {
            if (type is ITypeParameterSymbol parameter)
            {
                if (knownUnionTypeParameters.Contains(parameter) && seen.Add(parameter))
                    usedUnionTypeParameters.Add(parameter);
                return;
            }

            if (type is NullableTypeSymbol nullableType)
            {
                CollectReferencedTypeParameters(nullableType.UnderlyingType, knownUnionTypeParameters, usedUnionTypeParameters, seen);
                return;
            }

            if (type is RefTypeSymbol refType)
            {
                CollectReferencedTypeParameters(refType.ElementType, knownUnionTypeParameters, usedUnionTypeParameters, seen);
                return;
            }

            if (type is IAddressTypeSymbol addressType)
            {
                CollectReferencedTypeParameters(addressType.ReferencedType, knownUnionTypeParameters, usedUnionTypeParameters, seen);
                return;
            }

            if (type is IArrayTypeSymbol arrayType)
            {
                CollectReferencedTypeParameters(arrayType.ElementType, knownUnionTypeParameters, usedUnionTypeParameters, seen);
                return;
            }

            if (type is INamedTypeSymbol namedType && !namedType.TypeArguments.IsDefaultOrEmpty)
            {
                foreach (var typeArgument in namedType.TypeArguments)
                    CollectReferencedTypeParameters(typeArgument, knownUnionTypeParameters, usedUnionTypeParameters, seen);
            }
        }

        var unionAccessibility = unionSymbol.DeclaredAccessibility;
        var knownUnionTypeParameters = new HashSet<ITypeParameterSymbol>(unionSymbol.TypeParameters, SymbolEqualityComparer.Default);
        var unionValuePropertyType = objectType!.GetNullableType();

        if (unionDecl.MemberTypes is { } memberTypeList)
        {
            var boundMemberTypes = new List<(ITypeSymbol MemberType, ITypeSymbol ArtifactType, Location Location, SyntaxReference Reference)>();

            foreach (var memberTypeSyntax in memberTypeList.Types)
            {
                if (memberTypeSyntax is NullTypeSyntax)
                {
                    _declarationDiagnostics.ReportTypeExpectedWithoutWildcard(memberTypeSyntax.GetLocation());
                    continue;
                }

                var memberType = unionBinder.BindTypeSyntaxAndReport(memberTypeSyntax);
                TypeMemberBinder.ValidateTypeAccessibility(
                    memberType,
                    unionAccessibility,
                    unionSymbol.ContainingType,
                    "union",
                    unionSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    "member",
                    _declarationDiagnostics,
                    memberTypeSyntax.GetLocation());
                boundMemberTypes.Add((GetUnionContentMemberType(memberType), memberType, memberTypeSyntax.GetLocation(), memberTypeSyntax.GetReference()));
            }

            if (unionSymbol.PayloadFields.IsDefaultOrEmpty)
            {
                memberTypes.Clear();
                payloadFields.Clear();

                foreach (var (memberType, artifactType, location, reference) in boundMemberTypes)
                    RegisterUnionMemberArtifacts(memberType, artifactType, location, reference);
            }

            unionSymbol.SetCases(caseSymbols);
            unionSymbol.SetVariants(caseSymbols.Cast<ITypeSymbol>().Concat(memberTypes));
            unionSymbol.SetMemberTypes(memberTypes);
            unionSymbol.SetContentMayBeNull(boundMemberTypes.Any(static member => UnionContentNullability.IsNullableContentType(member.ArtifactType)));
            unionSymbol.SetPayloadFields(payloadFields);

            if (synthesizeUnionSurface)
            {
                EnsureUnionValueProperty(unionValuePropertyType, unionDecl.GetLocation(), unionDecl.GetReference());
                EnsureUnionHasValueProperty(unionDecl.GetLocation(), unionDecl.GetReference());
                EnsureUnionToStringMethod(unionDecl.GetLocation(), unionDecl.GetReference());
                EnsureUnionHelperMethods(unionDecl.GetLocation(), unionDecl.GetReference());
            }

            return;
        }

        if (synthesizeUnionSurface)
        {
            EnsureUnionValueProperty(unionValuePropertyType, unionDecl.GetLocation(), unionDecl.GetReference());
            EnsureUnionHasValueProperty(unionDecl.GetLocation(), unionDecl.GetReference());
            EnsureUnionToStringMethod(unionDecl.GetLocation(), unionDecl.GetReference());
            EnsureUnionHelperMethods(unionDecl.GetLocation(), unionDecl.GetReference());
        }

        if (shouldRegisterCaseDeclarations)
        {
            foreach (var caseClause in GetCaseDeclarations(unionDecl))
            {
                var caseBaseType = unionSymbol.TypeKind == TypeKind.Struct
                    ? valueType!
                    : Compilation.GetSpecialType(SpecialType.System_Object);
                var caseTypeKind = unionSymbol.TypeKind == TypeKind.Struct
                    ? TypeKind.Struct
                    : TypeKind.Class;

                var caseSymbol = new SourceUnionCaseTypeSymbol(
                    caseClause.Identifier.ValueText,
                    ordinal++,
                    unionSymbol,
                    unionSymbol.GetMetadataCaseContainer(),
                    caseBaseType,
                    caseTypeKind,
                    unionSymbol,
                    unionSymbol,
                    namespaceSymbol,
                    [caseClause.GetLocation()],
                    [caseClause.GetReference()],
                    unionAccessibility);

                RegisterMember(unionSymbol, caseSymbol);

                var rawParameters = new List<(SyntaxNode Syntax, string Name, Location Location, ITypeSymbol Type, RefKind RefKind, bool HasExplicitDefaultValue, object? ExplicitDefaultValue, bool IsMutable, bool IsVarParams, bool HasImplicitName)>();
                var seenOptionalParameter = false;

                if (caseClause.ParameterList is { } parameterList)
                {
                    var caseParameters = parameterList.Parameters.ToArray();
                    var hasNamedPayloads = caseParameters.Any(static parameter => !parameter.Identifier.IsMissing);
                    var hasUnnamedPayloads = caseParameters.Any(static parameter => parameter.Identifier.IsMissing);
                    if (hasNamedPayloads && hasUnnamedPayloads)
                    {
                        _declarationDiagnostics.ReportUnionCasePayloadStyleMixed(
                            caseClause.Identifier.ValueText,
                            parameterList.GetLocation());
                    }

                    for (var parameterOrdinal = 0; parameterOrdinal < caseParameters.Length; parameterOrdinal++)
                    {
                        var parameterSyntax = caseParameters[parameterOrdinal];
                        var typeSyntax = parameterSyntax.TypeAnnotation?.Type;
                        var refKind = ParameterSyntaxUtilities.GetRefKind(parameterSyntax);
                        var parameterName = UnionFacts.GetCaseParameterName(
                            parameterSyntax.Identifier.ValueText,
                            parameterOrdinal,
                            caseParameters.Length);
                        var parameterLocation = parameterSyntax.Identifier.IsMissing
                            ? typeSyntax?.GetLocation() ?? parameterSyntax.GetLocation()
                            : parameterSyntax.Identifier.GetLocation();

                        ITypeSymbol parameterType;
                        if (typeSyntax is null)
                        {
                            parameterType = Compilation.ErrorTypeSymbol;
                        }
                        else
                        {
                            var boundTypeSyntax = refKind.IsByRef && typeSyntax is ByRefTypeSyntax byRefType
                                ? byRefType.ElementType
                                : typeSyntax;
                            parameterType = unionBinder.BindTypeSyntaxAndReport(
                                boundTypeSyntax,
                                additionalDiagnostics: _declarationDiagnostics);
                        }

                        parameterType = TypeMemberBinder.NormalizeVarParamsParameterType(
                            Compilation,
                            parameterSyntax,
                            parameterType,
                            parameterName,
                            unionBinder.Diagnostics,
                            out var isVarParams);

                        TypeMemberBinder.ValidateTypeAccessibility(
                            parameterType,
                            unionAccessibility,
                            unionSymbol.ContainingType,
                            "union case",
                            $"{unionSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{caseClause.Identifier.ValueText}",
                            $"parameter '{parameterName}'",
                            _declarationDiagnostics,
                            typeSyntax?.GetLocation() ?? parameterLocation);

                        var defaultResult = TypeMemberBinder.ProcessParameterDefault(
                            parameterSyntax,
                            parameterType,
                            parameterName,
                            unionBinder.Diagnostics,
                            ref seenOptionalParameter);

                        var isMutable = refKind is RefKind.Ref or RefKind.Out;
                        rawParameters.Add((
                            Syntax: parameterSyntax,
                            Name: parameterName,
                            Location: parameterLocation,
                            Type: parameterType,
                            RefKind: refKind,
                            HasExplicitDefaultValue: defaultResult.HasExplicitDefaultValue,
                            ExplicitDefaultValue: defaultResult.ExplicitDefaultValue,
                            IsMutable: isMutable,
                            IsVarParams: isVarParams,
                            HasImplicitName: parameterSyntax.Identifier.IsMissing));
                    }
                }
                else if (caseClause.FieldClause is { } fieldClause)
                {
                    foreach (var fieldSyntax in fieldClause.Fields)
                    {
                        var parameterType = unionBinder.BindTypeSyntaxAndReport(
                            fieldSyntax.TypeAnnotation.Type,
                            additionalDiagnostics: _declarationDiagnostics);
                        TypeMemberBinder.ValidateTypeAccessibility(
                            parameterType,
                            unionAccessibility,
                            unionSymbol.ContainingType,
                            "union case",
                            $"{unionSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{caseClause.Identifier.ValueText}",
                            $"field '{fieldSyntax.Identifier.ValueText}'",
                            _declarationDiagnostics,
                            fieldSyntax.TypeAnnotation.Type.GetLocation());
                        var defaultResult = TypeMemberBinder.ProcessParameterDefault(
                            fieldSyntax.Initializer,
                            parameterType,
                            fieldSyntax.Identifier.ValueText,
                            fieldSyntax.Identifier.GetLocation(),
                            unionBinder.Diagnostics,
                            ref seenOptionalParameter,
                            requireTrailingOptional: false);

                        rawParameters.Add((
                            Syntax: fieldSyntax,
                            Name: fieldSyntax.Identifier.ValueText,
                            Location: fieldSyntax.Identifier.GetLocation(),
                            Type: parameterType,
                            RefKind: RefKind.None,
                            HasExplicitDefaultValue: defaultResult.HasExplicitDefaultValue,
                            ExplicitDefaultValue: defaultResult.ExplicitDefaultValue,
                            IsMutable: false,
                            IsVarParams: false,
                            HasImplicitName: false));
                    }
                }

                var usedUnionTypeParameters = new List<ITypeParameterSymbol>();
                var seenUnionTypeParameters = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
                foreach (var rawParameter in rawParameters)
                {
                    CollectReferencedTypeParameters(
                        rawParameter.Type,
                        knownUnionTypeParameters,
                        usedUnionTypeParameters,
                        seenUnionTypeParameters);
                }

                var caseTypeParameterMap = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
                var projectedUnionTypeParameterMappings = new List<(ITypeParameterSymbol CaseTypeParameter, ITypeParameterSymbol UnionTypeParameter)>();
                if (usedUnionTypeParameters.Count > 0)
                {
                    var caseTypeParameters = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(usedUnionTypeParameters.Count);

                    for (var i = 0; i < usedUnionTypeParameters.Count; i++)
                    {
                        var unionTypeParameter = usedUnionTypeParameters[i];
                        var sourceUnionTypeParameter = unionTypeParameter as SourceTypeParameterSymbol;

                        var caseTypeParameter = new SourceTypeParameterSymbol(
                            unionTypeParameter.Name,
                            caseSymbol,
                            caseSymbol,
                            namespaceSymbol,
                            sourceUnionTypeParameter?.Locations.ToArray() ?? [caseClause.GetLocation()],
                            sourceUnionTypeParameter?.DeclaringSyntaxReferences.ToArray() ?? [caseClause.GetReference()],
                            i,
                            sourceUnionTypeParameter?.ConstraintKind ?? TypeParameterConstraintKind.None,
                            sourceUnionTypeParameter?.ConstraintTypeReferences ?? ImmutableArray<SyntaxReference>.Empty,
                            sourceUnionTypeParameter?.Variance ?? VarianceKind.None);

                        if (sourceUnionTypeParameter is { HasResolvedConstraintTypes: true })
                            caseTypeParameter.SetConstraintTypes(sourceUnionTypeParameter.ConstraintTypes);

                        caseTypeParameters.Add(caseTypeParameter);
                        caseTypeParameterMap[unionTypeParameter] = caseTypeParameter;
                        projectedUnionTypeParameterMappings.Add((caseTypeParameter, unionTypeParameter));
                    }

                    caseSymbol.SetTypeParameters(caseTypeParameters.MoveToImmutable());
                    caseSymbol.SetProjectedUnionTypeParameters(projectedUnionTypeParameterMappings);
                }

                ITypeSymbol caseTypeForUnionMembers = caseSymbol;
                if (usedUnionTypeParameters.Count > 0)
                {
                    caseTypeForUnionMembers = caseSymbol.Construct(
                        usedUnionTypeParameters.Cast<ITypeSymbol>().ToArray());
                }

                var constructor = new SourceMethodSymbol(
                    ".ctor",
                    unitType,
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    caseSymbol,
                    caseSymbol,
                    namespaceSymbol,
                    [caseClause.GetLocation()],
                    [caseClause.GetReference()],
                    isStatic: false,
                    methodKind: MethodKind.Constructor,
                    declaredAccessibility: Accessibility.Public);

                RegisterCaseMember(constructor);

                var parameters = new List<SourceParameterSymbol>();
                foreach (var rawParameter in rawParameters)
                {
                    var parameterSyntax = rawParameter.Syntax;
                    var parameterName = rawParameter.Name;
                    var refKind = rawParameter.RefKind;
                    var parameterType = rawParameter.Type;
                    if (caseTypeParameterMap.Count > 0)
                        parameterType = SubstituteTypeParameters(parameterType, caseTypeParameterMap);

                    var parameterSymbol = new SourceParameterSymbol(
                        parameterName,
                        parameterType,
                        constructor,
                        caseSymbol,
                        namespaceSymbol,
                        [rawParameter.Location],
                        [parameterSyntax.GetReference()],
                        refKind,
                        rawParameter.HasExplicitDefaultValue,
                        rawParameter.ExplicitDefaultValue,
                        rawParameter.IsMutable,
                        rawParameter.IsVarParams,
                        hasImplicitName: rawParameter.HasImplicitName);

                    parameters.Add(parameterSymbol);

                    if (refKind == RefKind.None)
                    {
                        var propertyName = UnionFacts.GetCasePropertyName(parameterName);

                        var backingField = new SourceFieldSymbol(
                            $"<{parameterName}>k__BackingField",
                            parameterType,
                            isStatic: false,
                            isMutable: parameterSymbol.IsMutable,
                            isConst: false,
                            constantValue: null,
                            caseSymbol,
                            caseSymbol,
                            namespaceSymbol,
                            [parameterSyntax.GetLocation()],
                            [parameterSyntax.GetReference()],
                            new BoundParameterAccess(parameterSymbol),
                            declaredAccessibility: Accessibility.Private);

                        RegisterCaseMember(backingField);

                        var propertySymbol = new SourcePropertySymbol(
                            propertyName,
                            parameterType,
                            caseSymbol,
                            caseSymbol,
                            namespaceSymbol,
                            [parameterSyntax.GetLocation()],
                            [parameterSyntax.GetReference()],
                            declaredAccessibility: Accessibility.Public);

                        RegisterCaseMember(propertySymbol);

                        var getterSymbol = new SourceMethodSymbol(
                            $"get_{propertyName}",
                            parameterType,
                            ImmutableArray<SourceParameterSymbol>.Empty,
                            propertySymbol,
                            caseSymbol,
                            namespaceSymbol,
                            [parameterSyntax.GetLocation()],
                            [parameterSyntax.GetReference()],
                            isStatic: false,
                            methodKind: MethodKind.PropertyGet,
                            declaredAccessibility: Accessibility.Public);

                        RegisterCaseMember(getterSymbol);

                        propertySymbol.SetBackingField(backingField);
                        propertySymbol.MarkSynthesizedBackingFieldAccessors();
                        propertySymbol.SetAccessors(getterSymbol, null);
                    }
                }

                constructor.SetParameters(parameters);
                caseSymbol.SetConstructorParameters(parameters);

                if (parameters.Count > 0)
                {
                    var deconstructMethod = new SourceMethodSymbol(
                        "Deconstruct",
                        unitType,
                        ImmutableArray<SourceParameterSymbol>.Empty,
                        caseSymbol,
                        caseSymbol,
                        namespaceSymbol,
                        [],
                        [],
                        isStatic: false,
                        methodKind: MethodKind.Ordinary,
                        declaredAccessibility: Accessibility.Public);

                    var deconstructParameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>();

                    foreach (var parameter in parameters)
                    {
                        if (parameter.RefKind != RefKind.None)
                            continue;

                        var propertyName = UnionFacts.GetCasePropertyName(parameter.Name);
                        if (caseSymbol.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault() is not { } property)
                            continue;

                        var deconstructParameter = new SourceParameterSymbol(
                            property.Name,
                            property.Type,
                            deconstructMethod,
                            caseSymbol,
                            namespaceSymbol,
                            [caseClause.GetLocation()],
                            [caseClause.GetReference()],
                            refKind: RefKind.Out);

                        deconstructParameters.Add(deconstructParameter);
                    }

                    if (deconstructParameters.Count > 0)
                    {
                        deconstructMethod.SetParameters(deconstructParameters.ToImmutable());
                        RegisterCaseMember(deconstructMethod);
                    }
                }

                if (Compilation.Options.SynthesizeStructuralToString)
                {
                    var caseToString = new SourceMethodSymbol(
                    "ToString",
                    stringType!,
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    caseSymbol,
                    caseSymbol,
                    namespaceSymbol,
                    new[] { caseClause.GetLocation() },
                    Array.Empty<SyntaxReference>(),
                    isStatic: false,
                    methodKind: MethodKind.Ordinary,
                    isOverride: true,
                    declaredAccessibility: Accessibility.Public);

                    RegisterCaseMember(caseToString);

                    var caseFormatValueHelper = new SourceMethodSymbol(
                    SynthesizedUnionMethodNames.FormatValueHelper,
                    stringType!,
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    caseSymbol,
                    caseSymbol,
                    namespaceSymbol,
                    new[] { caseClause.GetLocation() },
                    Array.Empty<SyntaxReference>(),
                    isStatic: true,
                    methodKind: MethodKind.Ordinary,
                    declaredAccessibility: Accessibility.Private);

                    var caseFormatValueParameter = new SourceParameterSymbol(
                        "value",
                        Compilation.GetSpecialType(SpecialType.System_Object)!,
                        caseFormatValueHelper,
                        caseSymbol,
                        namespaceSymbol,
                        [caseClause.GetLocation()],
                        Array.Empty<SyntaxReference>());
                    caseFormatValueHelper.SetParameters([caseFormatValueParameter]);

                    RegisterCaseMember(caseFormatValueHelper);

                    caseToString.SetOverriddenMethod(objectToString);
                }

                RegisterUnionCaseSymbol(caseClause, caseSymbol);
                caseSymbols.Add(caseSymbol);
                RegisterUnionMemberArtifacts(caseTypeForUnionMembers, caseTypeForUnionMembers, caseClause.GetLocation(), caseClause.GetReference());
            }
        }

        unionSymbol.SetCases(caseSymbols);
        unionSymbol.SetVariants(caseSymbols.Cast<ITypeSymbol>());
        unionSymbol.SetMemberTypes(memberTypes);
        unionSymbol.SetPayloadFields(payloadFields);

    }

    private static void ValidateReservedUnionMemberName(
        SourceUnionSymbol unionSymbol,
        string memberName,
        Location location,
        DiagnosticBag diagnostics)
    {
        if (memberName is "Value" or "HasValue")
            diagnostics.ReportUnionMemberNameReserved(unionSymbol.Name, memberName, location);
    }

    private static void ValidateUnionDeclaredMethod(
        SourceUnionSymbol unionSymbol,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodDecl,
        DiagnosticBag diagnostics)
    {
        ValidateReservedUnionMemberName(unionSymbol, methodDecl.Identifier.ValueText, methodDecl.Identifier.GetLocation(), diagnostics);

        if (methodSymbol.MethodKind != MethodKind.Ordinary)
            return;

        if (IsUnionToStringDeclaration(methodDecl))
            return;

        if (methodSymbol.Name is "Equals" or "GetHashCode")
            diagnostics.ReportUnionSpecialMemberNotSupported(unionSymbol.Name, methodSymbol.Name, methodDecl.Identifier.GetLocation());
    }

    private static void ValidateUnionDeclaredOperator(
        SourceUnionSymbol unionSymbol,
        IMethodSymbol operatorSymbol,
        OperatorDeclarationSyntax operatorDecl,
        DiagnosticBag diagnostics)
    {
        if (operatorSymbol.MethodKind != MethodKind.UserDefinedOperator)
            return;

        if (operatorDecl.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.NotEqualsToken)
        {
            var displayName = $"operator {OperatorFacts.GetDisplayText(operatorDecl.OperatorToken.Kind)}";
            diagnostics.ReportUnionSpecialMemberNotSupported(unionSymbol.Name, displayName, operatorDecl.OperatorToken.GetLocation());
        }
    }

    private static bool HasDeclaredUnionToStringMethod(
        UnionDeclarationSyntax unionDeclaration,
        ImmutableArray<MemberDeclarationSyntax> introducedMembers = default)
        => unionDeclaration.Members
               .Concat(introducedMembers.IsDefault
                   ? ImmutableArray<MemberDeclarationSyntax>.Empty
                   : introducedMembers)
               .OfType<MethodDeclarationSyntax>()
               .Any(IsUnionToStringDeclaration);

    private static bool IsUnionToStringDeclaration(MethodDeclarationSyntax methodDecl)
        => methodDecl.Identifier.ValueText == "ToString" &&
           methodDecl.ParameterList.Parameters.Count == 0 &&
           !methodDecl.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);

    private void ReportExternalTypeRedeclaration(
        INamespaceSymbol? namespaceSymbol,
        SyntaxToken identifier,
        int arity,
        DiagnosticBag diagnostics)
    {
        _ = (namespaceSymbol, identifier, arity, diagnostics);
        // Source declarations take precedence over referenced metadata. Source-source
        // duplicates are still diagnosed by the normal declaration lookup below.
    }

    private (UnionDeclarationBinder Binder, SourceUnionSymbol Symbol) RegisterUnionDeclaration(
        UnionDeclarationSyntax unionDecl,
        Binder parentBinder,
        ISymbol declaringSymbol,
        SourceNamespaceSymbol? namespaceSymbol,
        SourceUnionSymbol? existingSymbol = null,
        UnionDeclarationSyntax? shapeDeclaration = null,
        ImmutableArray<MemberDeclarationSyntax> introducedMembers = default)
    {
        var unionShape = shapeDeclaration ?? unionDecl;
        var containingType = declaringSymbol as INamedTypeSymbol;
        var containingNamespace = declaringSymbol switch
        {
            INamespaceSymbol ns => ns,
            INamedTypeSymbol type => type.ContainingNamespace,
            _ => namespaceSymbol
        };

        var unionTypeKind = GetUnionTypeKind(unionShape);
        var baseTypeSymbol = GetUnionBaseType(unionTypeKind);
        var unionAccessibility = AccessibilityUtilities.DetermineAccessibility(
            unionDecl.Modifiers,
            AccessibilityUtilities.GetDefaultTypeAccessibility(declaringSymbol));

        var unionSymbol = existingSymbol ?? new SourceUnionSymbol(
            unionDecl.Identifier.ValueText,
            baseTypeSymbol!,
            unionTypeKind,
            declaringSymbol,
            containingType,
            containingNamespace,
            [unionDecl.GetLocation()],
            [unionDecl.GetReference()],
            unionAccessibility,
            metadataName: GetFileScopedMetadataName(unionDecl, unionDecl.Identifier.ValueText));

        if (HasFileScopeModifier(unionDecl.Modifiers))
            unionSymbol.MarkFileScoped(unionDecl.SyntaxTree.FilePath);

        if (unionSymbol.TypeParameters.IsDefaultOrEmpty)
            InitializeTypeParameters(unionSymbol, unionDecl.TypeParameterList, unionDecl.ConstraintClauses);

        var unionBinder = new UnionDeclarationBinder(parentBinder, unionSymbol, unionDecl);
        unionBinder.EnsureTypeParameterConstraintTypesResolved(unionSymbol.TypeParameters);
        ValidateTypeDeclarationConstraintAccessibility(unionSymbol, "union", unionBinder.Diagnostics);
        CacheBinder(unionDecl, unionBinder);

        var interfaces = ResolveUnionInterfaceTypes(unionShape, unionBinder);
        if (!interfaces.IsDefaultOrEmpty)
            unionSymbol.SetInterfaces(MergeInterfaceSets(unionSymbol.Interfaces, interfaces));

        namespaceSymbol ??= unionSymbol.ContainingNamespace?.AsSourceNamespace();

        var discriminatorField = new SourceFieldSymbol(
            UnionFieldUtilities.TagFieldName,
            Compilation.GetSpecialType(SpecialType.System_Byte),
            isStatic: false,
            isMutable: true,
            isConst: false,
            constantValue: 0,
            unionSymbol,
            unionSymbol,
            namespaceSymbol,
            [unionDecl.GetLocation()],
            [unionDecl.GetReference()],
            null,
            declaredAccessibility: Accessibility.Private);

        if (Compilation.Options.SynthesizeStructuralToString)
        {
            var stringType = Compilation.GetSpecialType(SpecialType.System_String);
            var objectToString = GetObjectToStringMethod();

            SourceMethodSymbol? unionToString = null;
            if (!HasDeclaredUnionToStringMethod(unionDecl, introducedMembers))
            {
                unionToString = new SourceMethodSymbol(
                    "ToString",
                    stringType!,
                    ImmutableArray<SourceParameterSymbol>.Empty,
                    unionSymbol,
                    unionSymbol,
                    namespaceSymbol,
                    new[] { unionDecl.GetLocation() },
                    Array.Empty<SyntaxReference>(),
                    isStatic: false,
                    methodKind: MethodKind.Ordinary,
                    isOverride: true,
                    declaredAccessibility: Accessibility.Public);
            }

            var formatValueHelper = new SourceMethodSymbol(
                SynthesizedUnionMethodNames.FormatValueHelper,
                stringType!,
                ImmutableArray<SourceParameterSymbol>.Empty,
                unionSymbol,
                unionSymbol,
                namespaceSymbol,
                new[] { unionDecl.GetLocation() },
                Array.Empty<SyntaxReference>(),
                isStatic: true,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Private);

            var formatValueParameter = new SourceParameterSymbol(
                "value",
                Compilation.GetSpecialType(SpecialType.System_Object)!,
                formatValueHelper,
                unionSymbol,
                namespaceSymbol,
                [unionDecl.GetLocation()],
                Array.Empty<SyntaxReference>());
            formatValueHelper.SetParameters([formatValueParameter]);

            unionToString?.SetOverriddenMethod(objectToString);
        }
        unionSymbol.SetDiscriminatorField(discriminatorField);

        RegisterUnionSymbol(unionDecl, unionSymbol);

        return (unionBinder, unionSymbol);
    }

    private ImmutableArray<INamedTypeSymbol> ResolveUnionInterfaceTypes(
        UnionDeclarationSyntax unionDecl,
        UnionDeclarationBinder unionBinder)
    {
        if (unionDecl.BaseList is null)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var baseTypeSyntax in unionDecl.BaseList.Types)
        {
            if (!unionBinder.TryBindNamedTypeFromTypeSyntax(baseTypeSyntax.Type, out var resolved, reportDiagnostics: true) ||
                resolved is null)
            {
                continue;
            }

            if (resolved.TypeKind != TypeKind.Interface)
            {
                unionBinder.Diagnostics.ReportUnionBaseTypeMustBeInterface(
                    unionDecl.Identifier.ValueText,
                    resolved.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    baseTypeSyntax.GetLocation());
                continue;
            }

            unionBinder.ReportInvalidInheritedInterfaceType(unionDecl, baseTypeSyntax, resolved);
            builder.Add(resolved);
        }

        return builder.Count == 0
            ? ImmutableArray<INamedTypeSymbol>.Empty
            : builder.ToImmutable();
    }

    private TypeKind GetUnionTypeKind(UnionDeclarationSyntax unionDecl)
    {
        return unionDecl.ClassOrStructKeyword.Kind switch
        {
            SyntaxKind.StructKeyword => TypeKind.Struct,
            SyntaxKind.ClassKeyword => TypeKind.Class,
            _ => TypeKind.Struct
        };
    }

    private INamedTypeSymbol GetUnionBaseType(TypeKind unionTypeKind)
    {
        return unionTypeKind == TypeKind.Struct
            ? Compilation.GetSpecialType(SpecialType.System_ValueType)!
            : Compilation.GetSpecialType(SpecialType.System_Object)!;
    }

    private static string GetUnionMemberMetadataName(ITypeSymbol memberType)
    {
        return memberType switch
        {
            INamedTypeSymbol named => named.Name,
            _ => memberType.Name
        };
    }

    private IMethodSymbol GetObjectToStringMethod()
    {
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        return objectType!
            .GetMembers("ToString")
            .OfType<IMethodSymbol>()
            .First(m => m.Parameters.Length == 0);
    }

    private void RegisterClassMembers(TypeDeclarationSyntax classDecl, ClassDeclarationBinder classBinder)
    {
        if (classDecl is ClassDeclarationSyntax { ParameterList: not null }
            or RecordDeclarationSyntax { ParameterList: not null }
            or StructDeclarationSyntax { ParameterList: not null })
            RegisterPrimaryConstructor(classDecl, classBinder);

        var nestedClassBinders = new List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)>();
        var nestedInterfaceBinders = new List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)>();
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var valueType = Compilation.GetSpecialType(SpecialType.System_ValueType);
        var parentType = (INamedTypeSymbol)classBinder.ContainingSymbol;
        var effectiveMembers = GetEffectiveTypeMembers(classDecl).ToArray();

        foreach (var effectiveMember in effectiveMembers)
        {
            var member = effectiveMember.EffectiveSyntax;
            var originalSyntax = effectiveMember.OriginalSyntax;
            if (member is not InterfaceDeclarationSyntax nestedInterface)
                continue;

            ImmutableArray<INamedTypeSymbol> parentInterfaces = ImmutableArray<INamedTypeSymbol>.Empty;
            if (nestedInterface.BaseList is not null)
            {
                var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
                foreach (var t in nestedInterface.BaseList.Types)
                {
                    if (classBinder.TryBindNamedTypeFromTypeSyntax(t.Type, out var resolved, reportDiagnostics: true) &&
                        resolved is not null &&
                        resolved.TypeKind == TypeKind.Interface)
                    {
                        builder.Add(resolved);
                    }
                }

                if (builder.Count > 0)
                    parentInterfaces = builder.ToImmutable();
            }

            var nestedInterfaceSymbol = GetDeclaredTypeSymbol(nestedInterface);
            if (!parentInterfaces.IsDefaultOrEmpty)
                nestedInterfaceSymbol.SetInterfaces(MergeInterfaceSets(nestedInterfaceSymbol.Interfaces, parentInterfaces));

            var nestedInterfaceBinder = new InterfaceDeclarationBinder(classBinder, nestedInterfaceSymbol, nestedInterface);
            nestedInterfaceBinder.EnsureTypeParameterConstraintTypesResolved(nestedInterfaceSymbol.TypeParameters);
            ValidateTypeDeclarationConstraintAccessibility(nestedInterfaceSymbol, "type", nestedInterfaceBinder.Diagnostics);
            CacheBinder(nestedInterface, nestedInterfaceBinder);
            if (originalSyntax is not null)
                CacheBinder(originalSyntax, nestedInterfaceBinder);
            if (nestedInterfaceSymbol.IsSealedHierarchy)
                ResolveSealedHierarchyPermits(nestedInterface, nestedInterfaceSymbol, nestedInterfaceBinder);
            RegisterInterfaceMembers(nestedInterface, nestedInterfaceBinder);
            nestedInterfaceBinders.Add((nestedInterface, nestedInterfaceBinder));
        }

        foreach (var effectiveMember in effectiveMembers)
        {
            var member = effectiveMember.EffectiveSyntax;
            var originalSyntax = effectiveMember.OriginalSyntax;
            ReportInvalidRecordBodyMemberIfNeeded(classDecl, classBinder, member);

            switch (member)
            {
                case FieldDeclarationSyntax fieldDecl:
                    var fieldBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    fieldBinder.BindFieldDeclaration(fieldDecl);
                    CacheBinder(fieldDecl, fieldBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, fieldBinder);
                    foreach (var decl in fieldDecl.Declaration.Declarators)
                        CacheBinder(decl, fieldBinder);
                    break;

                case ConstDeclarationSyntax constDecl:
                    var constBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    constBinder.BindConstDeclaration(constDecl);
                    CacheBinder(constDecl, constBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, constBinder);
                    foreach (var decl in constDecl.Declaration.Declarators)
                        CacheBinder(decl, constBinder);
                    break;

                case MethodDeclarationSyntax methodDecl:
                    var memberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var methodBinder = memberBinder.BindMethodDeclaration(methodDecl);
                    CacheBinder(methodDecl, methodBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, methodBinder);
                    if (methodBinder.ContainingSymbol is IMethodSymbol methodSymbol)
                        RegisterMethodSymbol(methodDecl, methodSymbol);
                    break;

                case OperatorDeclarationSyntax operatorDecl:
                    var operatorBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var boundOperatorBinder = operatorBinder.BindOperatorDeclaration(operatorDecl);
                    CacheBinder(operatorDecl, boundOperatorBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, boundOperatorBinder);
                    break;

                case ConversionOperatorDeclarationSyntax conversionDecl:
                    var conversionBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var boundConversionBinder = conversionBinder.BindConversionOperatorDeclaration(conversionDecl);
                    CacheBinder(conversionDecl, boundConversionBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, boundConversionBinder);
                    break;

                case PropertyDeclarationSyntax propDecl:
                    var propMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var accessorBinders = propMemberBinder.BindPropertyDeclaration(propDecl);
                    CacheBinder(propDecl, propMemberBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, propMemberBinder);
                    foreach (var kv in accessorBinders)
                        CacheBinder(kv.Key, kv.Value);
                    break;

                case EventDeclarationSyntax eventDecl:
                    var eventMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var eventAccessors = eventMemberBinder.BindEventDeclaration(eventDecl);
                    CacheBinder(eventDecl, eventMemberBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, eventMemberBinder);
                    foreach (var kv in eventAccessors)
                        CacheBinder(kv.Key, kv.Value);
                    break;

                case IndexerDeclarationSyntax indexerDecl:
                    var indexerMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var indexerAccessorBinders = indexerMemberBinder.BindIndexerDeclaration(indexerDecl);
                    CacheBinder(indexerDecl, indexerMemberBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, indexerMemberBinder);
                    foreach (var kv in indexerAccessorBinders)
                        CacheBinder(kv.Key, kv.Value);
                    break;

                case ConstructorDeclarationSyntax ctorDecl:
                    var ctorMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var ctorBinder = ctorMemberBinder.BindConstructorDeclaration(ctorDecl);
                    CacheBinder(ctorDecl, ctorBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, ctorBinder);
                    break;

                case ParameterlessConstructorDeclarationSyntax initDecl:
                    var initMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var initBinder = initMemberBinder.BindInitDeclaration(initDecl);
                    CacheBinder(initDecl, initBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, initBinder);
                    break;

                case InitializerBlockDeclarationSyntax initBlockDecl:
                    var initBlockMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var initBlockBinder = initBlockMemberBinder.BindInitBlockDeclaration(initBlockDecl);
                    CacheBinder(initBlockDecl, initBlockBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, initBlockBinder);
                    break;

                case FinallyDeclarationSyntax finalDecl:
                    var finalMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var finalBinder = finalMemberBinder.BindFinallyDeclaration(finalDecl);
                    CacheBinder(finalDecl, finalBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, finalBinder);
                    break;

                case DelegateDeclarationSyntax del:
                    var delMemberBinder = new TypeMemberBinder(classBinder, (INamedTypeSymbol)classBinder.ContainingSymbol);
                    var delBinder = delMemberBinder.BindDelegateDeclaration(del);
                    CacheBinder(del, delBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, delBinder);
                    break;

                case TypeDeclarationSyntax nestedClass when nestedClass is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax:
                    var nestedSymbol = GetDeclaredTypeSymbol(nestedClass);
                    var nestedBinder = new ClassDeclarationBinder(classBinder, nestedSymbol, nestedClass);
                    nestedBinder.EnsureTypeParameterConstraintTypesResolved(nestedSymbol.TypeParameters);
                    var defaultNestedBaseType = GetDefaultBaseTypeForNominalDeclaration(nestedClass, objectType, valueType);
                    var nestedDefaultInterfaces = GetDefaultNominalInterfaces(nestedSymbol);
                    var shape = nestedBinder.BindNominalTypeShape(nestedClass, defaultNestedBaseType, nestedDefaultInterfaces);
                    var nestedBaseType = shape.BaseType;
                    var nestedInterfaces = shape.Interfaces;

                    if (nestedBaseType is not null &&
                        !SymbolEqualityComparer.Default.Equals(nestedSymbol.BaseType, nestedBaseType) &&
                        SymbolEqualityComparer.Default.Equals(nestedSymbol.BaseType, defaultNestedBaseType))
                    {
                        nestedSymbol.SetBaseType(nestedBaseType);
                    }

                    if (!nestedInterfaces.IsDefaultOrEmpty)
                        nestedSymbol.SetInterfaces(MergeInterfaceSets(nestedSymbol.Interfaces, nestedInterfaces));
                    CacheBinder(nestedClass, nestedBinder);
                    if (originalSyntax is not null)
                        CacheBinder(originalSyntax, nestedBinder);
                    RegisterClassSymbol(nestedClass, nestedSymbol);
                    if (nestedSymbol.IsSealedHierarchy)
                        ResolveSealedHierarchyPermits(nestedClass, nestedSymbol, nestedBinder);
                    RegisterClassMembers(nestedClass, nestedBinder);
                    nestedBinder.EnsureDefaultConstructor();
                    nestedClassBinders.Add((nestedClass, nestedBinder));
                    break;

                case UnionDeclarationSyntax nestedUnion:
                    {
                        var expansion = GetEffectiveUnionDeclaration(nestedUnion);
                        var declaringSymbol = (ISymbol)classBinder.ContainingSymbol;
                        var namespaceSymbol = classBinder.CurrentNamespace?.AsSourceNamespace();
                        var unionSymbol = (SourceUnionSymbol)GetDeclaredTypeSymbol(nestedUnion);
                        var (unionBinder, resolvedSymbol) = RegisterUnionDeclaration(
                            nestedUnion,
                            classBinder,
                            declaringSymbol,
                            namespaceSymbol,
                            unionSymbol,
                            expansion.Shape,
                            expansion.IntroducedMembers);
                        RegisterUnionCases(nestedUnion, unionBinder, resolvedSymbol, synthesizeUnionSurface: false, expansion.IntroducedMembers);
                        RegisterUnionDeclaredMembers(nestedUnion, unionBinder, resolvedSymbol, expansion.IntroducedMembers);
                        RegisterUnionCases(nestedUnion, unionBinder, resolvedSymbol, synthesizeUnionSurface: true, expansion.IntroducedMembers);
                        ReportMissingInterfaceMembers(resolvedSymbol, nestedUnion, unionBinder.Diagnostics);
                        break;
                    }

                case InterfaceDeclarationSyntax:
                    break;

                case EnumDeclarationSyntax enumDecl:
                    {
                        var enumSymbol = GetDeclaredTypeSymbol(enumDecl);

                        var enumBinder = new EnumDeclarationBinder(classBinder, enumSymbol, enumDecl);
                        CacheBinder(enumDecl, enumBinder);
                        if (originalSyntax is not null)
                            CacheBinder(originalSyntax, enumBinder);

                        var enumUnderlyingType = ResolveEnumUnderlyingType(enumDecl, classBinder);
                        enumSymbol.SetEnumUnderlyingType(enumUnderlyingType);

                        RegisterEnumMembers(enumDecl, enumBinder, enumSymbol, classBinder.CurrentNamespace!.AsSourceNamespace());
                        break;
                    }
            }
        }

        MergeSameFileSealedHierarchySubtypes(
            (SourceNamedTypeSymbol)classBinder.ContainingSymbol,
            GetSealedHierarchyCandidates(nestedClassBinders, nestedInterfaceBinders));
        DiscoverSameFileSealedHierarchySubtypes(nestedClassBinders, nestedInterfaceBinders);
        ValidatePermitsListEntries(nestedClassBinders, nestedInterfaceBinders);

        var checkedNestedClasses = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (nestedSyntax, nestedBinder) in nestedClassBinders)
        {
            var nestedSymbol = (INamedTypeSymbol)nestedBinder.ContainingSymbol;
            if (!checkedNestedClasses.Add(nestedSymbol))
                continue;

            ReportMissingAbstractBaseMembers(nestedSymbol, nestedSyntax, nestedBinder.Diagnostics);
            ReportMissingInterfaceMembers(nestedSymbol, nestedSyntax, nestedBinder.Diagnostics);
        }

        if (classBinder.ContainingSymbol is SourceNamedTypeSymbol { IsRecord: true })
            RegisterRecordValueMembers(classDecl, classBinder);

        classBinder.EnsureDefaultConstructor();

    }

    private static void ReportInvalidRecordBodyMemberIfNeeded(
        TypeDeclarationSyntax typeDeclaration,
        ClassDeclarationBinder classBinder,
        MemberDeclarationSyntax member)
    {
        if (typeDeclaration is not RecordDeclarationSyntax ||
            classBinder.ContainingSymbol is not SourceNamedTypeSymbol recordSymbol ||
            !recordSymbol.IsRecord)
        {
            return;
        }

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl when !HasStaticModifier(fieldDecl.Modifiers):
                foreach (var declarator in fieldDecl.Declaration.Declarators)
                {
                    classBinder.Diagnostics.ReportRecordInstanceStorageMemberNotAllowed(
                        recordSymbol.Name,
                        "field",
                        declarator.Identifier.ValueText,
                        declarator.Identifier.GetLocation());
                }
                break;

            case PropertyDeclarationSyntax propertyDecl
                when !HasStaticModifier(propertyDecl.Modifiers) &&
                     IsInstanceStorageProperty(propertyDecl):
                classBinder.Diagnostics.ReportRecordInstanceStorageMemberNotAllowed(
                    recordSymbol.Name,
                    "property",
                    propertyDecl.Identifier.ValueText,
                    propertyDecl.Identifier.GetLocation());
                break;

            case EventDeclarationSyntax eventDecl when !HasStaticModifier(eventDecl.Modifiers):
                classBinder.Diagnostics.ReportRecordInstanceStorageMemberNotAllowed(
                    recordSymbol.Name,
                    "event",
                    eventDecl.Identifier.ValueText,
                    eventDecl.Identifier.GetLocation());
                break;

            case ConstructorDeclarationSyntax ctorDecl when !HasStaticModifier(ctorDecl.Modifiers):
                classBinder.Diagnostics.ReportRecordSecondaryConstructorNotAllowed(
                    recordSymbol.Name,
                    ctorDecl.InitKeyword.GetLocation());
                break;

            case ParameterlessConstructorDeclarationSyntax initDecl when !HasStaticModifier(initDecl.Modifiers):
                classBinder.Diagnostics.ReportRecordSecondaryConstructorNotAllowed(
                    recordSymbol.Name,
                    initDecl.InitKeyword.GetLocation());
                break;

            case InitializerBlockDeclarationSyntax initBlockDecl when !HasStaticModifier(initBlockDecl.Modifiers):
                classBinder.Diagnostics.ReportRecordSecondaryConstructorNotAllowed(
                    recordSymbol.Name,
                    initBlockDecl.GetLocation());
                break;
        }
    }

    private static bool IsInstanceStorageProperty(PropertyDeclarationSyntax propertyDecl)
    {
        if (propertyDecl.Initializer is not null)
            return true;

        if (propertyDecl.ExpressionBody is not null)
            return false;

        if (propertyDecl.AccessorList is null || propertyDecl.AccessorList.IsMissing)
            return true;

        if (propertyDecl.AccessorList.Accessors.All(static accessor =>
                accessor.Body is null &&
                accessor.ExpressionBody is null))
        {
            return true;
        }

        return UsesAutoPropertyFieldKeyword(propertyDecl);
    }

    private static bool UsesAutoPropertyFieldKeyword(PropertyDeclarationSyntax propertyDecl)
    {
        if (propertyDecl.AccessorList is not { } accessorList)
            return false;

        foreach (var accessor in accessorList.Accessors)
        {
            if (accessor.Body is { } body &&
                body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(static id => id.Identifier.ValueText == "field"))
            {
                return true;
            }

            if (accessor.ExpressionBody is { } expressionBody &&
                expressionBody.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(static id => id.Identifier.ValueText == "field"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasStaticModifier(SyntaxTokenList modifiers)
        => modifiers.Any(static modifier => modifier.Kind == SyntaxKind.StaticKeyword);

    private static void ReportIncompletePartialMembers(INamedTypeSymbol containingType, DiagnosticBag diagnostics)
    {
        ReportIncompletePartialMethods(containingType, diagnostics);
        ReportIncompletePartialProperties(containingType, diagnostics);
        ReportIncompletePartialEvents(containingType, diagnostics);
    }

    private static void ReportIncompletePartialMethods(INamedTypeSymbol containingType, DiagnosticBag diagnostics)
    {
        foreach (var method in containingType.GetMembers().OfType<SourceMethodSymbol>())
        {
            if (!method.IsPartialMember)
                continue;

            var primaryLocation = method.Locations.FirstOrDefault();

            if (!method.HasPartialDefinition)
            {
                diagnostics.ReportPartialMethodMissingDefinition(
                    method.Name,
                    primaryLocation);
            }

            if (!method.HasPartialImplementation)
            {
                diagnostics.ReportPartialMethodMissingImplementation(
                    method.Name,
                    primaryLocation);
            }
        }
    }

    private static void ReportIncompletePartialProperties(INamedTypeSymbol containingType, DiagnosticBag diagnostics)
    {
        foreach (var property in containingType.GetMembers().OfType<SourcePropertySymbol>())
        {
            if (!property.IsPartialMember)
                continue;

            var primaryLocation = property.Locations.FirstOrDefault();

            if (!property.HasPartialDefinition)
            {
                diagnostics.ReportPartialPropertyMissingDefinition(
                    property.Name,
                    primaryLocation);
            }

            if (!property.HasPartialImplementation)
            {
                diagnostics.ReportPartialPropertyMissingImplementation(
                    property.Name,
                    primaryLocation);
            }
        }
    }

    private static void ReportIncompletePartialEvents(INamedTypeSymbol containingType, DiagnosticBag diagnostics)
    {
        foreach (var @event in containingType.GetMembers().OfType<SourceEventSymbol>())
        {
            if (!@event.IsPartialMember)
                continue;

            var primaryLocation = @event.Locations.FirstOrDefault();

            if (!@event.HasPartialDefinition)
            {
                diagnostics.ReportPartialEventMissingDefinition(
                    @event.Name,
                    primaryLocation);
            }

            if (!@event.HasPartialImplementation)
            {
                diagnostics.ReportPartialEventMissingImplementation(
                    @event.Name,
                    primaryLocation);
            }
        }
    }


    private void RegisterEnumMembers(
        EnumDeclarationSyntax enumDecl,
        EnumDeclarationBinder enumBinder,
        SourceNamedTypeSymbol enumSymbol,
        SourceNamespaceSymbol? containingNamespace)
    {
        // C#-like enum member semantics:
        // - If no initializer is present, the value is previous + 1 (first defaults to 0).
        // - If an initializer is present, it must be a constant expression.
        // - Previous enum members must be in scope while binding later initializers.
        var underlyingType = enumSymbol.EnumUnderlyingType
            ?? Compilation.GetSpecialType(SpecialType.System_Int32);
        var nextValue = (object?)null;

        foreach (var enumMember in enumDecl.Members)
        {
            var memberValue = nextValue ?? GetDefaultEnumMemberValue(underlyingType);
            var equalsValue = enumMember.EqualsValue;
            if (equalsValue is not null)
            {
                var exprBinder = new BlockBinder(enumSymbol, enumBinder);
                var bound = exprBinder.BindExpression(equalsValue.Value);
                foreach (var diagnostic in exprBinder.Diagnostics.AsEnumerable())
                    enumBinder.Diagnostics.Report(diagnostic);

                CacheBoundNode(equalsValue.Value, bound, exprBinder);

                if (TryGetEnumMemberConstantValue(bound, out var constant) &&
                    TryConvertEnumMemberValue(underlyingType, constant, out memberValue))
                {
                    if (!TryIncrementEnumValue(memberValue, underlyingType, out nextValue))
                    {
                        enumBinder.Diagnostics.ReportEnumMemberValueCannotConvert(
                            enumMember.Identifier.ValueText,
                            underlyingType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            equalsValue.Value.GetLocation());
                        nextValue = null;
                    }
                }
                else
                {
                    var typeDisplay = underlyingType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    if (!TryGetEnumMemberConstantValue(bound, out _))
                    {
                        enumBinder.Diagnostics.ReportEnumMemberValueMustBeConstant(
                            enumMember.Identifier.ValueText,
                            equalsValue.Value.GetLocation());
                    }
                    else
                    {
                        enumBinder.Diagnostics.ReportEnumMemberValueCannotConvert(
                            enumMember.Identifier.ValueText,
                            typeDisplay,
                            equalsValue.Value.GetLocation());
                    }

                    memberValue = nextValue ?? GetDefaultEnumMemberValue(underlyingType);
                    if (!TryIncrementEnumValue(memberValue, underlyingType, out nextValue))
                        nextValue = null;
                }
            }
            else
            {
                if (!TryIncrementEnumValue(memberValue, underlyingType, out nextValue))
                {
                    enumBinder.Diagnostics.ReportEnumMemberValueCannotConvert(
                        enumMember.Identifier.ValueText,
                        underlyingType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        enumMember.Identifier.GetLocation());
                    nextValue = null;
                }
            }

            var fieldSymbol = new SourceFieldSymbol(
                enumMember.Identifier.ValueText,
                enumSymbol,
                isStatic: true,
                isMutable: false,
                isConst: true,
                constantValue: memberValue,
                enumSymbol,
                enumSymbol,
                containingNamespace,
                new[] { enumMember.GetLocation() },
                new[] { enumMember.GetReference() },
                null,
                declaredAccessibility: Accessibility.Public);

            // Ensure later member initializers can resolve earlier members.
            if (!enumSymbol.GetMembers().Any(m => SymbolEqualityComparer.Default.Equals(m, fieldSymbol)))
                enumSymbol.AddMember(fieldSymbol);
        }
    }

    private ITypeSymbol ResolveEnumUnderlyingType(EnumDeclarationSyntax enumDecl, Binder binder)
    {
        var defaultType = Compilation.GetSpecialType(SpecialType.System_Int32);

        if (enumDecl.BaseList is not { } baseList || baseList.Types.Count == 0)
            return defaultType;

        if (baseList.Types.Count > 1)
        {
            for (var i = 1; i < baseList.Types.Count; i++)
            {
                binder.Diagnostics.ReportEnumUnderlyingTypeMustBeSingle(baseList.Types[i].GetLocation());
            }
        }

        var underlyingBaseTypeSyntax = baseList.Types[0];
        var resolvedResult = binder.BindTypeSyntax(underlyingBaseTypeSyntax.Type);
        if (!resolvedResult.Success)
        {
            binder.ReportResolveTypeResultDiagnostics(resolvedResult, underlyingBaseTypeSyntax.Type);
            return defaultType;
        }

        var resolvedType = resolvedResult.ResolvedType;

        if (resolvedType is null || resolvedType == Compilation.ErrorTypeSymbol)
            return defaultType;

        var normalizedType = UnwrapAliasType(resolvedType);
        if (normalizedType is NullableTypeSymbol || !IsValidEnumUnderlyingType(normalizedType))
        {
            binder.Diagnostics.ReportEnumUnderlyingTypeMustBeIntegral(
                resolvedType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                underlyingBaseTypeSyntax.GetLocation());
            return defaultType;
        }

        return normalizedType;
    }

    private static bool IsValidEnumUnderlyingType(ITypeSymbol type)
    {
        return type.SpecialType is
            SpecialType.System_SByte or
            SpecialType.System_Byte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Char;
    }

    private static ITypeSymbol UnwrapAliasType(ITypeSymbol type)
    {
        while (type is IAliasSymbol alias && alias.UnderlyingSymbol is ITypeSymbol underlying)
            type = underlying;

        return type;
    }

    private static object GetDefaultEnumMemberValue(ITypeSymbol underlyingType)
    {
        return underlyingType.SpecialType switch
        {
            SpecialType.System_SByte => (sbyte)0,
            SpecialType.System_Byte => (byte)0,
            SpecialType.System_Int16 => (short)0,
            SpecialType.System_UInt16 => (ushort)0,
            SpecialType.System_Int32 => 0,
            SpecialType.System_UInt32 => (uint)0,
            SpecialType.System_Int64 => 0L,
            SpecialType.System_UInt64 => 0UL,
            SpecialType.System_Char => '\0',
            _ => 0
        };
    }

    private static bool TryGetEnumMemberConstantValue(BoundExpression expression, out object? value)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal when literal.Value is not null:
                value = literal.Value;
                return true;
            case BoundFieldAccess fieldAccess:
                {
                    var field = fieldAccess.Field;
                    if (field is not null && field.IsConst)
                    {
                        value = field.GetConstantValue();
                        return value is not null;
                    }

                    break;
                }
        }

        value = null;
        return false;
    }

    private bool TryConvertEnumMemberValue(ITypeSymbol underlyingType, object? value, out object? converted)
    {
        if (value is null)
        {
            converted = null;
            return false;
        }

        underlyingType = UnwrapAliasType(underlyingType);

        if (underlyingType.SpecialType == SpecialType.System_Char)
        {
            if (value is char ch)
            {
                converted = ch;
                return true;
            }

            if (ConstantValueEvaluator.TryConvert(
                    Compilation.GetSpecialType(SpecialType.System_UInt16),
                    value,
                    out var ushortValue) &&
                ushortValue is ushort u16)
            {
                converted = (char)u16;
                return true;
            }

            converted = null;
            return false;
        }

        return ConstantValueEvaluator.TryConvert(underlyingType, value, out converted);
    }

    private static bool TryIncrementEnumValue(object? value, ITypeSymbol underlyingType, out object? incremented)
    {
        switch (underlyingType.SpecialType)
        {
            case SpecialType.System_SByte when value is sbyte sb && sb < sbyte.MaxValue:
                incremented = (sbyte)(sb + 1);
                return true;
            case SpecialType.System_Byte when value is byte b && b < byte.MaxValue:
                incremented = (byte)(b + 1);
                return true;
            case SpecialType.System_Int16 when value is short s && s < short.MaxValue:
                incremented = (short)(s + 1);
                return true;
            case SpecialType.System_UInt16 when value is ushort us && us < ushort.MaxValue:
                incremented = (ushort)(us + 1);
                return true;
            case SpecialType.System_Int32 when value is int i && i < int.MaxValue:
                incremented = i + 1;
                return true;
            case SpecialType.System_UInt32 when value is uint ui && ui < uint.MaxValue:
                incremented = ui + 1;
                return true;
            case SpecialType.System_Int64 when value is long l && l < long.MaxValue:
                incremented = l + 1;
                return true;
            case SpecialType.System_UInt64 when value is ulong ul && ul < ulong.MaxValue:
                incremented = ul + 1;
                return true;
            case SpecialType.System_Char when value is char ch && ch < char.MaxValue:
                incremented = (char)(ch + 1);
                return true;
        }

        incremented = null;
        return false;
    }

    private void RegisterInterfaceMembers(InterfaceDeclarationSyntax interfaceDecl, InterfaceDeclarationBinder interfaceBinder)
    {
        var nestedClassBinders = new List<(TypeDeclarationSyntax Syntax, ClassDeclarationBinder Binder)>();
        var nestedInterfaceBinders = new List<(InterfaceDeclarationSyntax Syntax, InterfaceDeclarationBinder Binder)>();
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var valueType = Compilation.GetSpecialType(SpecialType.System_ValueType);
        var members = interfaceDecl.Members.ToArray();

        foreach (var member in members)
        {
            if (member is not InterfaceDeclarationSyntax nestedInterface)
                continue;

            var nestedInterfaceSymbol = GetDeclaredTypeSymbol(nestedInterface);
            var parentInterfaces = ResolveInterfaceBaseTypes(nestedInterface, nestedInterfaceSymbol, interfaceBinder);

            if (!parentInterfaces.IsDefaultOrEmpty)
                nestedInterfaceSymbol.SetInterfaces(MergeInterfaceSets(nestedInterfaceSymbol.Interfaces, parentInterfaces));

            var nestedInterfaceBinder = new InterfaceDeclarationBinder(interfaceBinder, nestedInterfaceSymbol, nestedInterface);
            nestedInterfaceBinder.EnsureTypeParameterConstraintTypesResolved(nestedInterfaceSymbol.TypeParameters);
            ValidateTypeDeclarationConstraintAccessibility(nestedInterfaceSymbol, "type", nestedInterfaceBinder.Diagnostics);
            CacheBinder(nestedInterface, nestedInterfaceBinder);
            if (nestedInterfaceSymbol.IsSealedHierarchy)
                ResolveSealedHierarchyPermits(nestedInterface, nestedInterfaceSymbol, nestedInterfaceBinder);
            RegisterInterfaceMembers(nestedInterface, nestedInterfaceBinder);
            nestedInterfaceBinders.Add((nestedInterface, nestedInterfaceBinder));
        }

        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax methodDecl:
                    {
                        var memberBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var methodBinder = memberBinder.BindMethodDeclaration(methodDecl);
                        CacheBinder(methodDecl, methodBinder);
                        break;
                    }
                case OperatorDeclarationSyntax operatorDecl:
                    {
                        var memberBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var operatorBinder = memberBinder.BindOperatorDeclaration(operatorDecl);
                        CacheBinder(operatorDecl, operatorBinder);
                        break;
                    }
                case ConversionOperatorDeclarationSyntax conversionDecl:
                    {
                        var memberBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var conversionBinder = memberBinder.BindConversionOperatorDeclaration(conversionDecl);
                        CacheBinder(conversionDecl, conversionBinder);
                        break;
                    }
                case PropertyDeclarationSyntax propertyDecl:
                    {
                        var propertyBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var accessorBinders = propertyBinder.BindPropertyDeclaration(propertyDecl);
                        CacheBinder(propertyDecl, propertyBinder);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }
                case EventDeclarationSyntax eventDecl:
                    {
                        var eventBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var accessorBinders = eventBinder.BindEventDeclaration(eventDecl);
                        CacheBinder(eventDecl, eventBinder);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }
                case IndexerDeclarationSyntax indexerDecl:
                    {
                        var indexerBinder = new TypeMemberBinder(interfaceBinder, (INamedTypeSymbol)interfaceBinder.ContainingSymbol);
                        var accessorBinders = indexerBinder.BindIndexerDeclaration(indexerDecl);
                        CacheBinder(indexerDecl, indexerBinder);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }
                case TypeDeclarationSyntax nestedType when nestedType is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax:
                    {
                        var nestedSymbol = GetDeclaredTypeSymbol(nestedType);
                        var nestedBinder = new ClassDeclarationBinder(interfaceBinder, nestedSymbol, nestedType);
                        nestedBinder.EnsureTypeParameterConstraintTypesResolved(nestedSymbol.TypeParameters);
                        var defaultNestedBaseType = GetDefaultBaseTypeForNominalDeclaration(nestedType, objectType, valueType);
                        var nestedDefaultInterfaces = GetDefaultNominalInterfaces(nestedSymbol);
                        var shape = nestedBinder.BindNominalTypeShape(nestedType, defaultNestedBaseType, nestedDefaultInterfaces);
                        var nestedBaseType = shape.BaseType;
                        var nestedInterfaces = shape.Interfaces;

                        if (nestedBaseType is not null &&
                            !SymbolEqualityComparer.Default.Equals(nestedSymbol.BaseType, nestedBaseType) &&
                            SymbolEqualityComparer.Default.Equals(nestedSymbol.BaseType, defaultNestedBaseType))
                        {
                            nestedSymbol.SetBaseType(nestedBaseType);
                        }

                        if (!nestedInterfaces.IsDefaultOrEmpty)
                            nestedSymbol.SetInterfaces(MergeInterfaceSets(nestedSymbol.Interfaces, nestedInterfaces));

                        CacheBinder(nestedType, nestedBinder);
                        RegisterClassSymbol(nestedType, nestedSymbol);
                        if (nestedSymbol.IsSealedHierarchy)
                            ResolveSealedHierarchyPermits(nestedType, nestedSymbol, nestedBinder);
                        RegisterClassMembers(nestedType, nestedBinder);
                        nestedBinder.EnsureDefaultConstructor();
                        nestedClassBinders.Add((nestedType, nestedBinder));
                        break;
                    }
                case InterfaceDeclarationSyntax:
                    break;
            }
        }

        MergeSameFileSealedHierarchySubtypes(
            (SourceNamedTypeSymbol)interfaceBinder.ContainingSymbol,
            GetSealedHierarchyCandidates(nestedClassBinders, nestedInterfaceBinders));
        DiscoverSameFileSealedHierarchySubtypes(nestedClassBinders, nestedInterfaceBinders);
        ValidatePermitsListEntries(nestedClassBinders, nestedInterfaceBinders);
    }

    private ImmutableArray<INamedTypeSymbol> ResolveInterfaceBaseTypes(
        InterfaceDeclarationSyntax interfaceDecl,
        INamedTypeSymbol interfaceSymbol,
        Binder binder)
    {
        if (interfaceDecl.BaseList is null)
            return ImmutableArray<INamedTypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var t in interfaceDecl.BaseList.Types)
        {
            if (binder.TryBindNamedTypeFromTypeSyntax(t.Type, out var resolved, reportDiagnostics: true) &&
                resolved is not null &&
                resolved.TypeKind == TypeKind.Interface)
            {
                TypeMemberBinder.ValidateTypeAccessibility(
                    resolved,
                    interfaceSymbol.DeclaredAccessibility,
                    interfaceSymbol.ContainingType,
                    "type",
                    interfaceSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    "base interface",
                    binder.Diagnostics,
                    t.Type.GetLocation());

                if (binder is TypeDeclarationBinder typeDeclarationBinder)
                    typeDeclarationBinder.ReportInvalidInheritedInterfaceType(interfaceDecl, t, resolved);
                builder.Add(resolved);
            }
        }

        return builder.Count > 0
            ? builder.ToImmutable()
            : ImmutableArray<INamedTypeSymbol>.Empty;
    }

    private void RegisterExtensionMembers(ExtensionDeclarationSyntax extensionDecl, ExtensionDeclarationBinder extensionBinder)
    {
        if (extensionBinder.ContainingSymbol is SourceNamedTypeSymbol extensionSymbol)
        {
            var receiverType = extensionBinder.BindTypeSyntaxAndReport(extensionDecl.ReceiverType);
            extensionSymbol.SetExtensionReceiverType(receiverType);
        }

        foreach (var member in extensionDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax methodDecl:
                    {
                        var memberBinder = new TypeMemberBinder(extensionBinder, (INamedTypeSymbol)extensionBinder.ContainingSymbol, extensionDecl.ReceiverType);
                        var methodBinder = memberBinder.BindMethodDeclaration(methodDecl);
                        CacheBinder(methodDecl, methodBinder);
                        break;
                    }

                case OperatorDeclarationSyntax operatorDecl:
                    {
                        var memberBinder = new TypeMemberBinder(extensionBinder, (INamedTypeSymbol)extensionBinder.ContainingSymbol, extensionDecl.ReceiverType);
                        var operatorBinder = memberBinder.BindOperatorDeclaration(operatorDecl);
                        CacheBinder(operatorDecl, operatorBinder);
                        break;
                    }
                case ConversionOperatorDeclarationSyntax conversionDecl:
                    {
                        var memberBinder = new TypeMemberBinder(extensionBinder, (INamedTypeSymbol)extensionBinder.ContainingSymbol, extensionDecl.ReceiverType);
                        var conversionBinder = memberBinder.BindConversionOperatorDeclaration(conversionDecl);
                        CacheBinder(conversionDecl, conversionBinder);
                        break;
                    }

                case PropertyDeclarationSyntax propertyDecl:
                    {
                        var memberBinder = new TypeMemberBinder(extensionBinder, (INamedTypeSymbol)extensionBinder.ContainingSymbol, extensionDecl.ReceiverType);
                        var accessorBinders = memberBinder.BindPropertyDeclaration(propertyDecl);
                        CacheBinder(propertyDecl, memberBinder);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }
                case EventDeclarationSyntax eventDecl:
                    {
                        var memberBinder = new TypeMemberBinder(extensionBinder, (INamedTypeSymbol)extensionBinder.ContainingSymbol, extensionDecl.ReceiverType);
                        var accessorBinders = memberBinder.BindEventDeclaration(eventDecl);
                        CacheBinder(eventDecl, memberBinder);
                        foreach (var kv in accessorBinders)
                            CacheBinder(kv.Key, kv.Value);
                        break;
                    }
            }
        }
    }

    private static void InitializeTypeParameters(
        SourceNamedTypeSymbol typeSymbol,
        TypeParameterListSyntax? typeParameterList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses)
    {
        if (typeParameterList is null || typeParameterList.Parameters.Count == 0)
            return;

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(typeParameterList.Parameters.Count);
        var ordinal = 0;

        // Optional: pre-index clauses by name for fast lookup and duplicate detection
        var clausesByName = new Dictionary<string, List<TypeParameterConstraintClauseSyntax>>(StringComparer.Ordinal);
        foreach (var clause in constraintClauses)
        {
            var name = clause.TypeParameter.Identifier.ValueText;
            if (!clausesByName.TryGetValue(name, out var list))
                clausesByName[name] = list = new List<TypeParameterConstraintClauseSyntax>();
            list.Add(clause);
        }

        foreach (var parameter in typeParameterList.Parameters)
        {
            var name = parameter.Identifier.ValueText;

            // 1) inline
            var (inlineKind, inlineRefs) = TypeParameterConstraintAnalyzer.AnalyzeInline(parameter);

            // 2) where clauses for this parameter
            TypeParameterConstraintKind clauseKind = TypeParameterConstraintKind.None;
            var clauseRefsBuilder = ImmutableArray.CreateBuilder<SyntaxReference>();

            if (clausesByName.TryGetValue(name, out var matchingClauses))
            {
                // If you want “at most one where per type parameter”, enforce here.
                // Otherwise, allow multiple and merge.
                foreach (var clause in matchingClauses)
                {
                    var (k, refs) = TypeParameterConstraintAnalyzer.AnalyzeClause(clause);
                    clauseKind |= k;
                    clauseRefsBuilder.AddRange(refs);
                }
            }

            var mergedKind = inlineKind | clauseKind;
            var mergedRefs = inlineRefs.AddRange(clauseRefsBuilder.ToImmutable());

            var variance = GetDeclaredVariance(parameter);

            var typeParameter = new SourceTypeParameterSymbol(
                name,
                typeSymbol,
                typeSymbol,
                typeSymbol.ContainingNamespace,
                [parameter.GetLocation()],
                [parameter.GetReference()],
                ordinal++,
                mergedKind,
                mergedRefs,
                variance);

            builder.Add(typeParameter);
        }

        // Optional: report where-clauses that reference unknown type parameters
        // (you likely want diagnostics for this — but that needs a DiagnosticBag)
        // You can do it in the binder or in the place where you call this helper.

        typeSymbol.SetTypeParameters(builder.MoveToImmutable());
    }

    private static VarianceKind GetDeclaredVariance(TypeParameterSyntax parameter)
    {
        return parameter.VarianceKeyword.Kind switch
        {
            SyntaxKind.OutKeyword => VarianceKind.Out,
            SyntaxKind.InKeyword => VarianceKind.In,
            _ => VarianceKind.None,
        };
    }

    private void RegisterPrimaryConstructor(TypeDeclarationSyntax classDecl, ClassDeclarationBinder classBinder)
    {
        var classSymbol = (SourceNamedTypeSymbol)classBinder.ContainingSymbol;
        var namespaceSymbol = classBinder.CurrentNamespace!.AsSourceNamespace();
        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var isRecord = classSymbol.IsRecord;

        if (classSymbol.IsStatic)
        {
            classBinder.Diagnostics.ReportStaticClassCannotContainInstanceMember(
                classSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                "init",
                classDecl.ParameterList!.GetLocation());
            classSymbol.MarkPrimaryConstructorMembersDeclared();
            return;
        }

        var constructorAccessibility = classDecl.PrimaryConstructorAccessibilityKeyword.Kind switch
        {
            SyntaxKind.PrivateKeyword => Accessibility.Private,
            SyntaxKind.InternalKeyword => Accessibility.Internal,
            SyntaxKind.ProtectedKeyword => Accessibility.ProtectedAndProtected,
            _ => Accessibility.Public,
        };

        if (constructorAccessibility == Accessibility.ProtectedAndProtected &&
            classSymbol.TypeKind == TypeKind.Struct)
        {
            classBinder.Diagnostics.ReportModifierNotValidOnMember(
                classDecl.PrimaryConstructorAccessibilityKeyword.Text,
                "constructor",
                classSymbol.Name,
                classDecl.PrimaryConstructorAccessibilityKeyword.GetLocation());
        }

        var constructorSymbol = new SourceMethodSymbol(
            ".ctor",
            unitType,
            ImmutableArray<SourceParameterSymbol>.Empty,
            classSymbol,
            classSymbol,
            namespaceSymbol,
            [classDecl.GetLocation()],
            [classDecl.GetReference()],
            isStatic: false,
            methodKind: MethodKind.Constructor,
            declaredAccessibility: constructorAccessibility);

        if (isRecord)
        {
            constructorSymbol.MarkSetsRequiredMembers();
        }

        var parameters = new List<SourceParameterSymbol>();
        var recordProperties = isRecord
            ? ImmutableArray.CreateBuilder<SourcePropertySymbol>()
            : null;
        var deconstructProperties = ImmutableArray.CreateBuilder<SourcePropertySymbol>();

        var seenOptionalParameter = false;
        foreach (var parameterSyntax in classDecl.ParameterList!.Parameters)
        {
            var typeSyntax = parameterSyntax.TypeAnnotation?.Type;
            var refKind = ParameterSyntaxUtilities.GetRefKind(parameterSyntax);

            ITypeSymbol parameterType;
            if (typeSyntax is null)
            {
                classBinder.Diagnostics.ReportParameterTypeAnnotationRequired(
                    parameterSyntax.Identifier.ValueText,
                    parameterSyntax.Identifier.GetLocation());
                parameterType = Compilation.ErrorTypeSymbol;
            }
            else
            {
                var boundTypeSyntax = refKind.IsByRef && typeSyntax is ByRefTypeSyntax byRefType
                    ? byRefType.ElementType
                    : typeSyntax;
                parameterType = classBinder.BindTypeSyntaxAndReport(boundTypeSyntax);
                parameterType = classBinder.EnsureTypeValidForStorageLocation(parameterType, boundTypeSyntax.GetLocation());
            }

            parameterType = TypeMemberBinder.NormalizeVarParamsParameterType(
                Compilation,
                parameterSyntax,
                parameterType,
                parameterSyntax.Identifier.ValueText,
                classBinder.Diagnostics,
                out var isVarParams);

            var parameterTypeLocation = typeSyntax?.GetLocation() ?? parameterSyntax.Identifier.GetLocation();
            TypeMemberBinder.ValidateTypeAccessibility(
                parameterType,
                constructorAccessibility,
                classSymbol,
                "constructor",
                $"{classSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}..ctor",
                $"parameter '{parameterSyntax.Identifier.ValueText}'",
                classBinder.Diagnostics,
                parameterTypeLocation);

            var defaultResult = TypeMemberBinder.ProcessParameterDefault(
                parameterSyntax,
                parameterType,
                parameterSyntax.Identifier.ValueText,
                classBinder.Diagnostics,
                ref seenOptionalParameter);
            var bindingKeywordKind = parameterSyntax.BindingKeyword.Kind;
            var isMutable = bindingKeywordKind == SyntaxKind.VarKeyword;
            var accessibilityKeywordKind = parameterSyntax.AccessibilityKeyword.Kind;
            var parameterSymbol = new SourceParameterSymbol(
                parameterSyntax.Identifier.ValueText,
                parameterType,
                constructorSymbol,
                classSymbol,
                namespaceSymbol,
                [parameterSyntax.Identifier.GetLocation()],
                [parameterSyntax.GetReference()],
                refKind,
                defaultResult.HasExplicitDefaultValue,
                defaultResult.ExplicitDefaultValue,
                isMutable,
                isVarParams,
                ParameterSyntaxUtilities.GetScopedKind(
                    parameterSyntax,
                    parameterType,
                    classBinder.Diagnostics));

            parameters.Add(parameterSymbol);

            var shouldPromoteToProperty = refKind == RefKind.None &&
                                          (isRecord || bindingKeywordKind is SyntaxKind.ValKeyword or SyntaxKind.VarKeyword);
            if (!shouldPromoteToProperty && accessibilityKeywordKind is not SyntaxKind.None)
            {
                var keywordText = parameterSyntax.AccessibilityKeyword.Text;
                classBinder.Diagnostics.ReportModifierNotValidOnMember(
                    keywordText,
                    "parameter",
                    parameterSyntax.Identifier.ValueText,
                    parameterSyntax.AccessibilityKeyword.GetLocation());
            }

            if (refKind == RefKind.None)
            {
                // Record positional parameters always synthesize properties. For class/struct primary
                // constructors, only `val`/`var` parameters are promoted.
                var shouldPromoteToPropertyWithoutRef = isRecord || bindingKeywordKind is SyntaxKind.ValKeyword or SyntaxKind.VarKeyword;
                if (shouldPromoteToPropertyWithoutRef)
                {
                    if (string.Equals(parameterSymbol.Name, classSymbol.Name, StringComparison.Ordinal))
                    {
                        classBinder.Diagnostics.ReportMemberNameCannotMatchContainingType(
                            parameterSymbol.Name,
                            classSymbol.Name,
                            parameterSyntax.Identifier.GetLocation());
                        continue;
                    }

                    var promotedPropertyAccessibility = GetPrimaryConstructorPropertyAccessibility(accessibilityKeywordKind);
                    TypeMemberBinder.ValidateTypeAccessibility(
                        parameterType,
                        promotedPropertyAccessibility,
                        classSymbol,
                        "property",
                        $"{classSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{parameterSymbol.Name}",
                        "property",
                        classBinder.Diagnostics,
                        parameterTypeLocation);
                    var propertySymbol = CreatePrimaryConstructorProperty(
                        classSymbol,
                        parameterSymbol,
                        parameterSyntax,
                        parameterType,
                        namespaceSymbol,
                        classBinder,
                        promotedPropertyAccessibility,
                        applyRecordInheritanceSemantics: isRecord,
                        markRequired: isRecord);

                    if (isRecord && propertySymbol is not null)
                    {
                        recordProperties?.Add(propertySymbol);
                    }

                    if (propertySymbol is not null &&
                        (isRecord || promotedPropertyAccessibility == Accessibility.Public))
                    {
                        deconstructProperties.Add(propertySymbol);
                    }
                }
                else
                {
                    // For class/struct primary constructors, non-promoted parameters are still
                    // captured in private instance storage so members can reference them.
                    _ = new SourceFieldSymbol(
                        parameterSyntax.Identifier.ValueText,
                        parameterType,
                        isStatic: false,
                        isMutable: parameterSymbol.IsMutable,
                        isConst: false,
                        constantValue: null,
                        classSymbol,
                        classSymbol,
                        namespaceSymbol,
                        [parameterSyntax.GetLocation()],
                        [parameterSyntax.GetReference()],
                        new BoundParameterAccess(parameterSymbol),
                        declaredAccessibility: Accessibility.Private);
                }
            }
        }

        constructorSymbol.SetParameters(parameters);

        if (recordProperties is not null)
            classSymbol.SetRecordProperties(recordProperties.ToImmutable());

        classSymbol.SetDeconstructProperties(deconstructProperties.ToImmutable());

        if (deconstructProperties.Count > 0 &&
            !HasDeconstruct(classSymbol, deconstructProperties.ToImmutable()))
        {
            SynthesizeDeconstructMethod(classDecl, classBinder, classSymbol, deconstructProperties.ToImmutable());
        }

        // For records with a primary constructor that inherit from another record,
        // bind the base constructor call from the PrimaryConstructorBaseTypeSyntax in the base list.
        var baseList = IsStructLikeNominalType(classDecl)
            ? null
            : classDecl switch
            {
                RecordDeclarationSyntax rec => rec.BaseList,
                ClassDeclarationSyntax cls => cls.BaseList,
                _ => null
            };

        if (baseList is not null)
        {
            var primaryCtorBase = baseList.Types
                .OfType<PrimaryConstructorBaseTypeSyntax>()
                .FirstOrDefault();

            if (primaryCtorBase is not null)
            {
                constructorSymbol.MarkConstructorInitializerSyntax();

                // Use a MethodBinder as the parent so that the primary constructor's
                // parameters are in scope when binding the base argument list.
                var methodBinder = new MethodBinder(constructorSymbol, classBinder);
                var initializerBinder = new ConstructorInitializerBinder(constructorSymbol, methodBinder);
                var boundInitializer = initializerBinder.BindFromPrimaryConstructorBase(primaryCtorBase.ArgumentList);

                foreach (var diagnostic in initializerBinder.Diagnostics.AsEnumerable())
                    classBinder.Diagnostics.Report(diagnostic);

                if (boundInitializer is not null)
                    constructorSymbol.SetConstructorInitializer(boundInitializer);
            }
        }

        classSymbol.MarkPrimaryConstructorMembersDeclared();
    }

    private string ToCamelCase(string valueText)
    {
        return char.ToLowerInvariant(valueText[0]) + valueText[1..];
    }

    private SourcePropertySymbol? CreatePrimaryConstructorProperty(
        SourceNamedTypeSymbol classSymbol,
        SourceParameterSymbol parameterSymbol,
        ParameterSyntax parameterSyntax,
        ITypeSymbol parameterType,
        SourceNamespaceSymbol? namespaceSymbol,
        ClassDeclarationBinder classBinder,
        Accessibility declaredAccessibility,
        bool applyRecordInheritanceSemantics,
        bool markRequired)
    {
        var propertyName = parameterSyntax.Identifier.ValueText;
        // C#-compatible record inheritance semantics for primary-constructor properties:
        // if a derived record redeclares a parameter matching an inherited instance member,
        // reuse the inherited property to avoid shadowing.
        if (applyRecordInheritanceSemantics && classSymbol.BaseType is INamedTypeSymbol baseType)
        {
            for (var current = baseType; current is not null; current = current.BaseType)
            {
                // Only consider inherited instance members.
                var inherited = current.GetMembers(propertyName).FirstOrDefault(static m => !m.IsStatic);
                if (inherited is null)
                    continue;

                // Reuse the inherited property if it is a source property symbol.
                // This avoids creating a new property on the derived record while preserving the
                // record’s positional parameter/property list used by downstream logic.
                if (inherited is SourcePropertySymbol sourceProperty)
                    return sourceProperty;

                if (inherited is IPropertySymbol)
                    return null;

                // Inherited non-property instance member with the same name: do not synthesize.
                return null;
            }
        }
        var existingMember = classSymbol.GetDeclaredMembersWithoutEnsuring(propertyName).FirstOrDefault();
        if (existingMember is not null)
        {
            if (existingMember is SourcePropertySymbol existingProperty &&
                SymbolDeclarationUtilities.HasDeclaringSyntax(existingProperty, parameterSyntax))
            {
                return existingProperty;
            }

            classBinder.Diagnostics.ReportTypeAlreadyDefinesMember(
                classSymbol.Name,
                propertyName,
                parameterSyntax.Identifier.GetLocation());
            return null;
        }

        var location = parameterSyntax.GetLocation();
        var references = new[] { parameterSyntax.GetReference() };

        var propertySymbol = new SourcePropertySymbol(
            propertyName,
            parameterType,
            classSymbol,
            classSymbol,
            namespaceSymbol,
            [location],
            references,
            isStatic: false,
            declaredAccessibility: declaredAccessibility);

        if (markRequired)
            propertySymbol.MarkAsRequired();

        var lowerAsFieldOnly = declaredAccessibility is Accessibility.Private or Accessibility.ProtectedAndProtected;
        var backingField = new SourceFieldSymbol(
            lowerAsFieldOnly ? propertySymbol.Name : $"<{propertySymbol.Name}>k__BackingField",
            parameterType,
            isStatic: false,
            isMutable: parameterSymbol.IsMutable,
            isConst: false,
            constantValue: null,
            classSymbol,
            classSymbol,
            namespaceSymbol,
            [location],
            references,
            new BoundParameterAccess(parameterSymbol),
            declaredAccessibility: Accessibility.Private);

        propertySymbol.SetBackingField(backingField);
        if (lowerAsFieldOnly)
        {
            propertySymbol.MarkEmitAsFieldOnly();
        }
        else
        {
            propertySymbol.MarkSynthesizedBackingFieldAccessors();
        }

        var getMethod = new SourceMethodSymbol(
            $"get_{propertySymbol.Name}",
            parameterType,
            ImmutableArray<SourceParameterSymbol>.Empty,
            propertySymbol,
            classSymbol,
            namespaceSymbol,
            [location],
            references,
            isStatic: false,
            methodKind: MethodKind.PropertyGet,
            declaredAccessibility: declaredAccessibility);

        var setMethodKind = parameterSymbol.IsMutable ? MethodKind.PropertySet : MethodKind.InitOnly;
        var setMethod = new SourceMethodSymbol(
            $"set_{propertySymbol.Name}",
            Compilation.GetSpecialType(SpecialType.System_Unit),
            ImmutableArray<SourceParameterSymbol>.Empty,
            propertySymbol,
            classSymbol,
            namespaceSymbol,
            [location],
            references,
            isStatic: false,
            methodKind: setMethodKind,
            declaredAccessibility: declaredAccessibility);

        var valueParameter = new SourceParameterSymbol(
            "value",
            parameterType,
            setMethod,
            classSymbol,
            namespaceSymbol,
            [location],
            references);
        setMethod.SetParameters(ImmutableArray.Create(valueParameter));

        propertySymbol.SetAccessors(getMethod, setMethod);
        return propertySymbol;
    }

    private static Accessibility GetPrimaryConstructorPropertyAccessibility(SyntaxKind accessibilityKeywordKind)
    {
        return accessibilityKeywordKind switch
        {
            SyntaxKind.PrivateKeyword => Accessibility.Private,
            SyntaxKind.InternalKeyword => Accessibility.Internal,
            SyntaxKind.ProtectedKeyword => Accessibility.ProtectedAndProtected,
            SyntaxKind.PublicKeyword => Accessibility.Public,
            _ => Accessibility.Public,
        };
    }

    private void RegisterRecordValueMembers(TypeDeclarationSyntax classDecl, ClassDeclarationBinder classBinder)
    {
        if (classBinder.ContainingSymbol is not SourceNamedTypeSymbol recordSymbol || !recordSymbol.IsRecord)
            return;

        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        var systemType = Compilation.GetSpecialType(SpecialType.System_Type);
        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var namespaceSymbol = classBinder.CurrentNamespace!.AsSourceNamespace();
        var location = classDecl.GetLocation();
        var references = Array.Empty<SyntaxReference>();

        if (objectType is null || boolType is null || intType is null || unitType is null || stringType is null || systemType is null)
            return;

        var equalsObject = objectType.GetMembers(nameof(object.Equals))
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 1 &&
                m.Parameters[0].Type.GetNonNullableType().SpecialType == SpecialType.System_Object);

        var getHashCode = objectType.GetMembers(nameof(object.GetHashCode))
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 0);

        if (equalsObject is null || getHashCode is null)
            return;

        var objectToString = GetObjectToStringMethod();

        if (!HasMethod(recordSymbol, "Equals", MethodKind.Ordinary, recordSymbol))
        {
            var equalsTyped = new SourceMethodSymbol(
                "Equals",
                boolType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Public);

            var otherParameter = new SourceParameterSymbol(
                "other",
                recordSymbol,
                equalsTyped,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            equalsTyped.SetParameters(ImmutableArray.Create(otherParameter));
        }

        if (!HasMethod(recordSymbol, "Equals", MethodKind.Ordinary, objectType))
        {
            var equalsObj = new SourceMethodSymbol(
                "Equals",
                boolType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                isOverride: true,
                declaredAccessibility: Accessibility.Public);

            equalsObj.SetOverriddenMethod(equalsObject);

            var otherParameter = new SourceParameterSymbol(
                "obj",
                objectType,
                equalsObj,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            equalsObj.SetParameters(ImmutableArray.Create(otherParameter));
        }

        if (!HasMethod(recordSymbol, "GetHashCode", MethodKind.Ordinary))
        {
            var hashMethod = new SourceMethodSymbol(
                "GetHashCode",
                intType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                isOverride: true,
                declaredAccessibility: Accessibility.Public);

            hashMethod.SetOverriddenMethod(getHashCode);
        }

        if (Compilation.Options.SynthesizeStructuralToString &&
            !HasMethod(recordSymbol, "ToString", MethodKind.Ordinary))
        {
            var toStringMethod = new SourceMethodSymbol(
                "ToString",
                stringType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                isOverride: true,
                declaredAccessibility: Accessibility.Public);

            toStringMethod.SetOverriddenMethod(objectToString);
        }

        if (Compilation.Options.SynthesizeStructuralToString &&
            !HasMethod(recordSymbol, SynthesizedUnionMethodNames.FriendlyTypeNameHelper, MethodKind.Ordinary, systemType))
        {
            var friendlyTypeNameHelper = new SourceMethodSymbol(
                SynthesizedUnionMethodNames.FriendlyTypeNameHelper,
                stringType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: true,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Private);

            var typeParameter = new SourceParameterSymbol(
                "type",
                systemType,
                friendlyTypeNameHelper,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            friendlyTypeNameHelper.SetParameters(ImmutableArray.Create(typeParameter));
        }

        if (Compilation.Options.SynthesizeStructuralToString &&
            !HasMethod(recordSymbol, SynthesizedUnionMethodNames.FormatValueHelper, MethodKind.Ordinary, objectType, systemType, boolType))
        {
            var formatValueHelper = new SourceMethodSymbol(
                SynthesizedUnionMethodNames.FormatValueHelper,
                stringType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: true,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Private);

            var formatValueParameter = new SourceParameterSymbol(
                "value",
                objectType,
                formatValueHelper,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            var formatValueTypeParameter = new SourceParameterSymbol(
                "valueType",
                systemType,
                formatValueHelper,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            var formatStructuredParameter = new SourceParameterSymbol(
                "renderStructured",
                boolType,
                formatValueHelper,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            formatValueHelper.SetParameters(ImmutableArray.Create(formatValueParameter, formatValueTypeParameter, formatStructuredParameter));
        }

        if (!HasMethod(recordSymbol, "op_Equality", MethodKind.UserDefinedOperator, recordSymbol, recordSymbol))
        {
            var equalsOperator = new SourceMethodSymbol(
                "op_Equality",
                boolType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: true,
                methodKind: MethodKind.UserDefinedOperator,
                declaredAccessibility: Accessibility.Public);

            var leftParameter = new SourceParameterSymbol(
                "left",
                recordSymbol,
                equalsOperator,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            var rightParameter = new SourceParameterSymbol(
                "right",
                recordSymbol,
                equalsOperator,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            equalsOperator.SetParameters(ImmutableArray.Create(leftParameter, rightParameter));
        }

        if (!HasMethod(recordSymbol, "op_Inequality", MethodKind.UserDefinedOperator, recordSymbol, recordSymbol))
        {
            var notEqualsOperator = new SourceMethodSymbol(
                "op_Inequality",
                boolType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: true,
                methodKind: MethodKind.UserDefinedOperator,
                declaredAccessibility: Accessibility.Public);

            var leftParameter = new SourceParameterSymbol(
                "left",
                recordSymbol,
                notEqualsOperator,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            var rightParameter = new SourceParameterSymbol(
                "right",
                recordSymbol,
                notEqualsOperator,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            notEqualsOperator.SetParameters(ImmutableArray.Create(leftParameter, rightParameter));
        }

        if (!HasDeconstruct(recordSymbol))
        {
            var deconstructMethod = new SourceMethodSymbol(
                "Deconstruct",
                unitType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Ordinary,
                declaredAccessibility: Accessibility.Public);

            var deconstructProperties = GetRecordValueProperties(recordSymbol);
            var parameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>(deconstructProperties.Length);
            foreach (var property in deconstructProperties)
            {
                var parameter = new SourceParameterSymbol(
                    property.Name,
                    property.Type,
                    deconstructMethod,
                    recordSymbol,
                    namespaceSymbol,
                    [location],
                    references,
                    refKind: RefKind.Out);

                parameters.Add(parameter);
            }

            deconstructMethod.SetParameters(parameters.ToImmutable());
        }

        if (!HasCopyConstructor(recordSymbol))
        {
            var copyConstructor = new SourceMethodSymbol(
                ".ctor",
                unitType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                recordSymbol,
                recordSymbol,
                namespaceSymbol,
                [location],
                references,
                isStatic: false,
                methodKind: MethodKind.Constructor,
                declaredAccessibility: Accessibility.Internal);

            var otherParameter = new SourceParameterSymbol(
                "other",
                recordSymbol,
                copyConstructor,
                recordSymbol,
                namespaceSymbol,
                [location],
                references);
            copyConstructor.SetParameters(ImmutableArray.Create(otherParameter));
        }
    }

    private static bool HasMethod(
        SourceNamedTypeSymbol typeSymbol,
        string name,
        MethodKind methodKind,
        params ITypeSymbol[] parameters)
    {
        foreach (var method in typeSymbol.GetMembers(name).OfType<IMethodSymbol>())
        {
            if (method.MethodKind != methodKind)
                continue;

            if (method.Parameters.Length != parameters.Length)
                continue;

            var matches = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, parameters[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static bool HasDeconstruct(SourceNamedTypeSymbol typeSymbol, ImmutableArray<SourcePropertySymbol>? deconstructProperties = null)
    {
        var properties = deconstructProperties ?? GetPublicDeconstructProperties(typeSymbol);

        foreach (var method in typeSymbol.GetDeclaredMembersWithoutEnsuring("Deconstruct").OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary || method.IsStatic)
                continue;

            if (method.ReturnType.SpecialType != SpecialType.System_Unit)
                continue;

            if (method.Parameters.Any(parameter => parameter.RefKind != RefKind.Out))
                continue;

            if (method.Parameters.Length != properties.Length)
                continue;

            return true;
        }

        return false;
    }

    private static ImmutableArray<SourcePropertySymbol> GetPublicDeconstructProperties(SourceNamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.DeconstructProperties.IsDefaultOrEmpty)
            return typeSymbol.DeconstructProperties;

        return GetRecordValueProperties(typeSymbol);
    }

    private static ImmutableArray<SourcePropertySymbol> GetRecordValueProperties(SourceNamedTypeSymbol typeSymbol)
        => typeSymbol.RecordProperties.IsDefaultOrEmpty
            ? ImmutableArray<SourcePropertySymbol>.Empty
            : typeSymbol.RecordProperties;

    private void SynthesizeDeconstructMethod(
        TypeDeclarationSyntax classDecl,
        ClassDeclarationBinder classBinder,
        SourceNamedTypeSymbol classSymbol,
        ImmutableArray<SourcePropertySymbol> deconstructProperties)
    {
        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        if (unitType is null)
            return;

        var namespaceSymbol = classBinder.CurrentNamespace!.AsSourceNamespace();
        var location = classDecl.GetLocation();
        var references = Array.Empty<SyntaxReference>();

        var deconstructMethod = new SourceMethodSymbol(
            "Deconstruct",
            unitType,
            ImmutableArray<SourceParameterSymbol>.Empty,
            classSymbol,
            classSymbol,
            namespaceSymbol,
            [location],
            references,
            isStatic: false,
            methodKind: MethodKind.Ordinary,
            declaredAccessibility: Accessibility.Public);

        var parameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>(deconstructProperties.Length);
        foreach (var property in deconstructProperties)
        {
            var parameter = new SourceParameterSymbol(
                property.Name,
                property.Type,
                deconstructMethod,
                classSymbol,
                namespaceSymbol,
                [location],
                references,
                refKind: RefKind.Out);

            parameters.Add(parameter);
        }

        deconstructMethod.SetParameters(parameters.ToImmutable());
    }

    private static bool HasCopyConstructor(SourceNamedTypeSymbol typeSymbol)
    {
        foreach (var constructor in typeSymbol.InstanceConstructors)
        {
            if (constructor.IsStatic)
                continue;

            if (constructor.Parameters.Length != 1)
                continue;

            if (SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, typeSymbol))
                return true;
        }

        return false;
    }

    private static TypeParameterListSyntax? GetTypeParameterList(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDeclaration => classDeclaration.TypeParameterList,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.TypeParameterList,
            StructDeclarationSyntax structDeclaration => structDeclaration.TypeParameterList,
            _ => null
        };

    private static SyntaxList<TypeParameterConstraintClauseSyntax> GetConstraintClauses(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDeclaration => classDeclaration.ConstraintClauses,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.ConstraintClauses,
            StructDeclarationSyntax structDeclaration => structDeclaration.ConstraintClauses,
            _ => SyntaxList<TypeParameterConstraintClauseSyntax>.Empty
        };

    private static BaseListSyntax? GetBaseList(TypeDeclarationSyntax declaration)
        => declaration switch
        {
            ClassDeclarationSyntax classDeclaration => classDeclaration.BaseList,
            RecordDeclarationSyntax recordDeclaration => recordDeclaration.BaseList,
            StructDeclarationSyntax structDeclaration => structDeclaration.BaseList,
            _ => null
        };

    private void ReportMissingAbstractBaseMembers(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax declaration,
        DiagnosticBag diagnostics)
    {
        if (typeSymbol.IsAbstract)
            return;

        if (typeSymbol.BaseType is not INamedTypeSymbol baseType ||
            baseType.TypeKind != TypeKind.Class)
        {
            return;
        }

        foreach (var abstractMember in baseType.GetMembers().OfType<IMethodSymbol>())
        {
            if (!abstractMember.IsAbstract || abstractMember.IsStatic)
                continue;

            if (abstractMember.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
                continue;

            if (ImplementsAbstractMember(typeSymbol, abstractMember))
                continue;

            if (HasDeclaredOverrideCandidate(declaration, abstractMember))
                continue;

            var memberDisplay = GetAbstractMemberDisplay(abstractMember);
            var baseTypeDisplay = baseType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

            diagnostics.ReportTypeDoesNotImplementAbstractMember(
                typeSymbol.Name,
                memberDisplay,
                baseTypeDisplay,
                declaration.Identifier.GetLocation());
        }
    }

    private bool HasDeclaredOverrideCandidate(TypeDeclarationSyntax declaration, IMethodSymbol abstractMember)
    {
        // The method binder owns final override validation. This guard prevents an
        // early missing-abstract diagnostic while source member state is still being completed.
        foreach (var member in GetEffectiveTypeMembers(declaration).Select(static member => member.EffectiveSyntax))
        {
            if (member is MethodDeclarationSyntax method &&
                method.Identifier.ValueText == abstractMember.Name &&
                method.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.OverrideKeyword) &&
                HasCompatibleParameterSurface(method, abstractMember))
            {
                return true;
            }

            if (abstractMember.AssociatedSymbol is IPropertySymbol property &&
                member is PropertyDeclarationSyntax propertyDeclaration &&
                propertyDeclaration.Identifier.ValueText == property.Name &&
                propertyDeclaration.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.OverrideKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCompatibleParameterSurface(MethodDeclarationSyntax method, IMethodSymbol abstractMember)
    {
        if (method.ParameterList.Parameters.Count != abstractMember.Parameters.Length)
            return false;

        for (var i = 0; i < method.ParameterList.Parameters.Count; i++)
        {
            if (ParameterSyntaxUtilities.GetRefKind(method.ParameterList.Parameters[i]) != abstractMember.Parameters[i].RefKind)
                return false;
        }

        return true;
    }

    private void ReportMissingInterfaceMembers(
        INamedTypeSymbol typeSymbol,
        BaseTypeDeclarationSyntax declaration,
        DiagnosticBag diagnostics)
    {
        if (typeSymbol.IsAbstract)
            return;

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            if (typeSymbol.BaseType is INamedTypeSymbol baseType &&
                baseType.AllInterfaces.Any(inheritedInterface =>
                    SymbolEqualityComparer.Default.Equals(inheritedInterface, interfaceType)))
            {
                continue;
            }

            foreach (var interfaceMethod in interfaceType.GetMembers().OfType<IMethodSymbol>())
            {
                if (!RequiresInterfaceImplementation(interfaceMethod))
                    continue;

                if (HasInheritedDefaultInterfaceImplementation(typeSymbol, interfaceMethod))
                    continue;

                if (ImplementsInterfaceMethod(typeSymbol, interfaceMethod))
                    continue;

                var memberDisplay = GetAbstractMemberDisplay(interfaceMethod);
                var interfaceDisplay = interfaceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

                diagnostics.ReportTypeDoesNotImplementAbstractMember(
                    typeSymbol.Name,
                    memberDisplay,
                    interfaceDisplay,
                    declaration.Identifier.GetLocation());
            }

            foreach (var interfaceProperty in interfaceType.GetMembers().OfType<IPropertySymbol>())
            {
                if (!RequiresInterfacePropertyImplementation(interfaceProperty))
                    continue;

                if (ImplementsInterfaceProperty(typeSymbol, interfaceProperty))
                    continue;

                var memberDisplay = GetInterfacePropertyDisplay(interfaceProperty);
                var interfaceDisplay = interfaceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

                diagnostics.ReportTypeDoesNotImplementAbstractMember(
                    typeSymbol.Name,
                    memberDisplay,
                    interfaceDisplay,
                    declaration.Identifier.GetLocation());
            }
        }
    }

    private static bool HasInheritedDefaultInterfaceImplementation(
        INamedTypeSymbol typeSymbol,
        IMethodSymbol interfaceMethod)
    {
        foreach (var candidateInterface in typeSymbol.AllInterfaces)
        {
            foreach (var candidateMethod in candidateInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (candidateMethod.IsAbstract)
                    continue;

                if (candidateMethod.ExplicitInterfaceImplementations.Any(implementation =>
                        SymbolEqualityComparer.Default.Equals(implementation, interfaceMethod)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RequiresInterfaceImplementation(IMethodSymbol interfaceMethod)
    {
        if (interfaceMethod.AssociatedSymbol is IPropertySymbol or IEventSymbol)
            return false;

        if (interfaceMethod.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
            return false;

        if (interfaceMethod.IsAbstract)
            return true;

        return IsSourceInterfaceMethodWithoutBody(interfaceMethod);
    }

    private static bool IsSourceInterfaceMethodWithoutBody(IMethodSymbol interfaceMethod)
    {
        if (interfaceMethod.ContainingType?.TypeKind != TypeKind.Interface)
            return false;

        var sourceMethod = interfaceMethod as SourceMethodSymbol
            ?? interfaceMethod.OriginalDefinition as SourceMethodSymbol
            ?? interfaceMethod.UnderlyingSymbol as SourceMethodSymbol;

        if (sourceMethod is null)
            return false;

        foreach (var reference in sourceMethod.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is MethodDeclarationSyntax declaration)
                return declaration.Body is null && declaration.ExpressionBody is null;
        }

        return false;
    }

    private static bool RequiresInterfacePropertyImplementation(IPropertySymbol interfaceProperty)
    {
        if (interfaceProperty.IsStatic)
            return false;

        return interfaceProperty.GetMethod is { IsAbstract: true } ||
               interfaceProperty.SetMethod is { IsAbstract: true } ||
               IsSourceInterfacePropertyWithoutImplementation(interfaceProperty);
    }

    private static bool IsSourceInterfacePropertyWithoutImplementation(IPropertySymbol interfaceProperty)
    {
        if (interfaceProperty.ContainingType?.TypeKind != TypeKind.Interface)
            return false;

        var sourceProperty = interfaceProperty as SourcePropertySymbol
            ?? interfaceProperty.OriginalDefinition as SourcePropertySymbol
            ?? interfaceProperty.UnderlyingSymbol as SourcePropertySymbol;
        if (sourceProperty is null)
            return false;

        foreach (var reference in sourceProperty.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not BasePropertyDeclarationSyntax declaration)
                continue;

            var hasExpressionBody = declaration switch
            {
                PropertyDeclarationSyntax property => property.ExpressionBody is not null,
                IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null,
                _ => false
            };
            if (hasExpressionBody)
                continue;

            if (declaration.AccessorList is null || declaration.AccessorList.Accessors.Any(
                    static accessor => accessor.Body is null && accessor.ExpressionBody is null))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterfaceMethod(INamedTypeSymbol typeSymbol, IMethodSymbol interfaceMethod)
    {
        foreach (var candidate in EnumerateTypeAndBaseMethods(typeSymbol))
        {
            if (candidate.IsStatic != interfaceMethod.IsStatic)
                continue;

            if (candidate.ExplicitInterfaceImplementations.Any(implementation =>
                    SymbolEqualityComparer.Default.Equals(implementation, interfaceMethod)))
            {
                return true;
            }

            if (candidate.Name != interfaceMethod.Name)
                continue;

            if (candidate is SourceMethodSymbol { IsSignatureSkeleton: true })
                return true;

            if (MethodSignaturesMatch(candidate, interfaceMethod))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterfaceProperty(INamedTypeSymbol typeSymbol, IPropertySymbol interfaceProperty)
    {
        foreach (var candidate in EnumerateTypeAndBaseProperties(typeSymbol))
        {
            if (candidate.IsStatic)
                continue;

            if (candidate.ExplicitInterfaceImplementations.Any(implementation =>
                    SymbolEqualityComparer.Default.Equals(implementation, interfaceProperty)))
            {
                return true;
            }

            if (candidate.Name != interfaceProperty.Name &&
                !(candidate.IsIndexer && interfaceProperty.IsIndexer))
            {
                continue;
            }

            if (PropertySignaturesMatch(candidate, interfaceProperty))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PropertySignaturesMatch(IPropertySymbol candidate, IPropertySymbol interfaceProperty)
    {
        if (candidate.IsIndexer != interfaceProperty.IsIndexer)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(
                StripNullableReference(candidate.Type),
                StripNullableReference(interfaceProperty.Type)))
        {
            return false;
        }

        if (interfaceProperty.GetMethod is not null && candidate.GetMethod is null)
            return false;

        if (interfaceProperty.SetMethod is not null && candidate.SetMethod is null)
            return false;

        if (candidate.Parameters.Length != interfaceProperty.Parameters.Length)
            return false;

        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            var candidateParameter = candidate.Parameters[i];
            var interfaceParameter = interfaceProperty.Parameters[i];

            if (candidateParameter.RefKind != interfaceParameter.RefKind)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(
                    StripNullableReference(candidateParameter.Type),
                    StripNullableReference(interfaceParameter.Type)))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<IMethodSymbol> EnumerateTypeAndBaseMethods(INamedTypeSymbol typeSymbol, string name)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(name).OfType<IMethodSymbol>())
                yield return candidate;
        }
    }

    private static IEnumerable<IMethodSymbol> EnumerateTypeAndBaseMethods(INamedTypeSymbol typeSymbol)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers().OfType<IMethodSymbol>())
                yield return candidate;
        }
    }

    private static IEnumerable<IPropertySymbol> EnumerateTypeAndBaseProperties(INamedTypeSymbol typeSymbol, string name)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(name).OfType<IPropertySymbol>())
                yield return candidate;
        }
    }

    private static IEnumerable<IPropertySymbol> EnumerateTypeAndBaseProperties(INamedTypeSymbol typeSymbol)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers().OfType<IPropertySymbol>())
                yield return candidate;
        }
    }

    private static bool ImplementsAbstractMember(INamedTypeSymbol typeSymbol, IMethodSymbol abstractMember)
    {
        foreach (var candidate in typeSymbol.GetMembers(abstractMember.Name).OfType<IMethodSymbol>())
        {
            var sourceMethod = candidate as SourceMethodSymbol;
            if (!candidate.IsOverride && !HasOverrideModifier(sourceMethod))
                continue;

            if (sourceMethod is not null &&
                sourceMethod.OverriddenMethod is not null &&
                SymbolEqualityComparer.Default.Equals(sourceMethod.OverriddenMethod, abstractMember))
            {
                return true;
            }

            if (candidate is SourceMethodSymbol { IsSignatureSkeleton: true })
                return true;

            if (MethodSignaturesMatch(candidate, abstractMember))
                return true;
        }

        return false;
    }

    private static bool HasOverrideModifier(SourceMethodSymbol? method)
    {
        if (method is null)
            return false;

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is MethodDeclarationSyntax declaration &&
                declaration.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.OverrideKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MethodSignaturesMatch(IMethodSymbol candidate, IMethodSymbol abstractMember)
    {
        if (candidate.TypeParameters.Length != abstractMember.TypeParameters.Length)
            return false;

        if (!SignatureTypesMatch(candidate.ReturnType, abstractMember.ReturnType))
        {
            return false;
        }

        if (candidate.Parameters.Length != abstractMember.Parameters.Length)
            return false;

        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            var candidateParameter = candidate.Parameters[i];
            var abstractParameter = abstractMember.Parameters[i];

            if (candidateParameter.RefKind != abstractParameter.RefKind)
                return false;

            if (!SignatureTypesMatch(candidateParameter.Type, abstractParameter.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SignatureTypesMatch(ITypeSymbol candidate, ITypeSymbol contract)
    {
        candidate = StripNullableReference(candidate);
        contract = StripNullableReference(contract);

        if (SymbolEqualityComparer.Default.Equals(candidate, contract))
            return true;

        if (candidate is ITypeParameterSymbol { OwnerKind: TypeParameterOwnerKind.Method } candidateParameter &&
            contract is ITypeParameterSymbol { OwnerKind: TypeParameterOwnerKind.Method } contractParameter)
        {
            return candidateParameter.Ordinal == contractParameter.Ordinal;
        }

        if (candidate is INamedTypeSymbol candidateNamed && contract is INamedTypeSymbol contractNamed)
        {
            if (!SymbolEqualityComparer.Default.Equals(candidateNamed.ConstructedFrom, contractNamed.ConstructedFrom) ||
                candidateNamed.TypeArguments.Length != contractNamed.TypeArguments.Length)
            {
                return false;
            }

            for (var i = 0; i < candidateNamed.TypeArguments.Length; i++)
            {
                if (!SignatureTypesMatch(candidateNamed.TypeArguments[i], contractNamed.TypeArguments[i]))
                    return false;
            }

            return true;
        }

        if (candidate is IArrayTypeSymbol candidateArray && contract is IArrayTypeSymbol contractArray)
        {
            return candidateArray.Rank == contractArray.Rank &&
                   SignatureTypesMatch(candidateArray.ElementType, contractArray.ElementType);
        }

        if (candidate is IPointerTypeSymbol candidatePointer && contract is IPointerTypeSymbol contractPointer)
            return SignatureTypesMatch(candidatePointer.PointedAtType, contractPointer.PointedAtType);

        if (candidate is IAddressTypeSymbol candidateAddress && contract is IAddressTypeSymbol contractAddress)
            return SignatureTypesMatch(candidateAddress.ReferencedType, contractAddress.ReferencedType);

        return false;
    }

    private static ITypeSymbol StripNullableReference(ITypeSymbol type)
    {
        if (type is NullableTypeSymbol nullableType && !nullableType.UnderlyingType.IsValueType)
            return StripNullableReference(nullableType.UnderlyingType);

        return type;
    }

    private static string GetAbstractMemberDisplay(IMethodSymbol abstractMember)
    {
        if (abstractMember.AssociatedSymbol is IPropertySymbol property)
        {
            var propertyFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.None);
            return property.ToDisplayString(propertyFormat);
        }

        var methodFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters);
        return abstractMember.ToDisplayString(methodFormat);
    }

    private static string GetInterfacePropertyDisplay(IPropertySymbol interfaceProperty)
    {
        var propertyFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(SymbolDisplayMemberOptions.None);
        return interfaceProperty.ToDisplayString(propertyFormat);
    }

    internal BoundNode? TryGetCachedBoundNode(SyntaxNode node)
    {
        if (_isCollectingDiagnostics && _nonReportingBoundNodeCache.ContainsKey(node))
            return null;

        return _boundNodeCache.TryGetValue(node, out var bound) ? bound : null;
    }

    internal BoundNode? TryGetCachedBoundNode(SyntaxNode node, ITypeSymbol? targetType)
    {
        if (targetType is null)
            return TryGetCachedBoundNode(node);

        var key = new ContextualBoundNodeCacheKey(node, targetType);
        if (_isCollectingDiagnostics && _nonReportingContextualBoundNodeCache.ContainsKey(key))
            return null;

        return _contextualBoundNodeCache.TryGetValue(key, out var bound)
            ? bound
            : null;
    }

    internal BoundNode? TryGetCachedLoweredBoundNode(SyntaxNode node)
        => _loweredBoundNodeCache.TryGetValue(node, out var bound) ? bound : null;

    internal void CacheBoundNode(SyntaxNode node, BoundNode bound, Binder? binder = null)
    {
        var selectedBound = _boundNodeCache.AddOrUpdate(
            node,
            _ => bound,
            (_, existing) => ShouldReplaceCachedBoundNode(existing, bound)
                ? bound
                : existing);
        var selectedBoundIsNonReporting = ReferenceEquals(selectedBound, bound)
            ? IsNonReportingBinder(binder)
            : _nonReportingBoundNodeCache.ContainsKey(node);

        _syntaxCache[selectedBound] = node;
        UpdateNonReportingBoundNodeCache(node, selectedBound, bound, binder);
        CacheSymbolInfo(node, selectedBound, selectedBoundIsNonReporting);
        if (IsDebuggingEnabled && binder is not null)
        {
            _boundNodeCache2.AddOrUpdate(
                node,
                _ => (binder, selectedBound),
                (_, existing) =>
                {
                    if (ShouldReplaceCachedBoundNode(existing.Item2, bound))
                        return (binder, bound);

                    return existing;
                });
        }

        if (node is FunctionExpressionSyntax functionExpression &&
            selectedBound is BoundFunctionExpression boundFunction)
        {
            RemoveCachedSymbolMapping(functionExpression);
            _ = TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, boundFunction, out _);
        }
    }

    internal void CacheBoundNode(SyntaxNode node, BoundNode bound, Binder? binder, ITypeSymbol? targetType)
    {
        if (targetType is null)
        {
            CacheBoundNode(node, bound, binder);
            return;
        }

        var key = new ContextualBoundNodeCacheKey(node, targetType);
        var selectedBound = _contextualBoundNodeCache.AddOrUpdate(
            key,
            _ => bound,
            (_, existing) => ShouldReplaceCachedBoundNode(existing, bound)
                ? bound
                : existing);
        var selectedBoundIsNonReporting = ReferenceEquals(selectedBound, bound)
            ? IsNonReportingBinder(binder)
            : _nonReportingContextualBoundNodeCache.ContainsKey(key);

        _syntaxCache[selectedBound] = node;
        UpdateNonReportingBoundNodeCache(key, selectedBound, bound, binder);
        CacheSymbolInfo(node, selectedBound, selectedBoundIsNonReporting);
        if (IsDebuggingEnabled && binder is not null)
        {
            _contextualBoundNodeCache2.AddOrUpdate(
                key,
                _ => (binder, selectedBound),
                (_, existing) =>
                {
                    if (ShouldReplaceCachedBoundNode(existing.Item2, bound))
                        return (binder, bound);

                    return existing;
                });
        }

        if (node is FunctionExpressionSyntax functionExpression &&
            selectedBound is BoundFunctionExpression boundFunction)
        {
            RemoveCachedSymbolMapping(functionExpression);
            _ = TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, boundFunction, out _);
        }
    }

    private void UpdateNonReportingBoundNodeCache(SyntaxNode node, BoundNode selectedBound, BoundNode incomingBound, Binder? binder)
    {
        if (!ReferenceEquals(selectedBound, incomingBound))
            return;

        if (binder?.Diagnostics.IsReportingDisabled == true)
            _nonReportingBoundNodeCache[node] = 0;
        else
            _nonReportingBoundNodeCache.TryRemove(node, out _);
    }

    private void UpdateNonReportingBoundNodeCache(ContextualBoundNodeCacheKey key, BoundNode selectedBound, BoundNode incomingBound, Binder? binder)
    {
        if (!ReferenceEquals(selectedBound, incomingBound))
            return;

        if (binder?.Diagnostics.IsReportingDisabled == true)
            _nonReportingContextualBoundNodeCache[key] = 0;
        else
            _nonReportingContextualBoundNodeCache.TryRemove(key, out _);
    }

    private static bool IsNonReportingBinder(Binder? binder)
        => binder?.Diagnostics.IsReportingDisabled == true;

    private void CacheSymbolInfo(SyntaxNode node, BoundNode bound, bool nonReporting)
    {
        var info = bound switch
        {
            BoundExpression expression => expression.GetSymbolInfo(),
            BoundStatement statement => statement.GetSymbolInfo(),
            _ => default
        };

        if (info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty)
            StoreSymbolMappingExact(node, info, nonReporting);
    }

    private static bool ShouldReplaceCachedBoundNode(BoundNode existing, BoundNode incoming)
    {
        var existingIsError = IsErrorBoundNode(existing);
        var incomingIsError = IsErrorBoundNode(incoming);

        if (existingIsError != incomingIsError)
            return existingIsError;

        return true;
    }

    private static bool IsErrorBoundNode(BoundNode node)
    {
        if (node is BoundErrorExpression)
            return true;

        if (node is BoundExpression expression)
            return expression.Type?.TypeKind == TypeKind.Error;

        return false;
    }

    internal void CacheLoweredBoundNode(SyntaxNode node, BoundNode bound, Binder? binder = null)
    {
        _loweredBoundNodeCache[node] = bound;
        _loweredSyntaxCache[bound] = node;
        PropagateLoweredSyntaxMappings(bound, node);
        if (IsDebuggingEnabled && binder is not null)
        {
            _loweredBoundNodeCache2[node] = (binder, bound);
        }
    }

    internal void RegisterLoweredSyntaxMappings(BoundNode loweredRoot, SyntaxNode fallbackSyntax)
        => PropagateLoweredSyntaxMappings(loweredRoot, fallbackSyntax);

    private void PropagateLoweredSyntaxMappings(BoundNode loweredRoot, SyntaxNode fallbackSyntax)
    {
        var resolving = new HashSet<BoundNode>(ReferenceEqualityComparer.Instance);
        if (!_loweredSyntaxCache.ContainsKey(loweredRoot))
            _loweredSyntaxCache[loweredRoot] = fallbackSyntax;

        foreach (var child in EnumerateBoundChildren(loweredRoot))
            _ = ResolveLoweredSyntax(child, fallbackSyntax, resolving);
    }

    private SyntaxNode ResolveLoweredSyntax(
        BoundNode node,
        SyntaxNode fallbackSyntax,
        HashSet<BoundNode> resolving)
    {
        if (_loweredSyntaxCache.TryGetValue(node, out var loweredSyntax))
            return loweredSyntax;

        if (_syntaxCache.TryGetValue(node, out var originalSyntax))
        {
            _loweredSyntaxCache[node] = originalSyntax;
            return originalSyntax;
        }

        if (!resolving.Add(node))
            return fallbackSyntax;

        SyntaxNode? resolvedSyntax = null;
        foreach (var child in EnumerateBoundChildren(node))
        {
            var childSyntax = ResolveLoweredSyntax(child, fallbackSyntax, resolving);
            if (childSyntax is null)
                continue;

            resolvedSyntax = ChoosePreferredLoweredSyntax(fallbackSyntax, resolvedSyntax, childSyntax);
        }

        resolving.Remove(node);

        resolvedSyntax ??= fallbackSyntax;
        _loweredSyntaxCache[node] = resolvedSyntax;
        return resolvedSyntax;
    }

    private static SyntaxNode ChoosePreferredLoweredSyntax(
        SyntaxNode fallbackSyntax,
        SyntaxNode? currentBest,
        SyntaxNode candidate)
    {
        if (currentBest is null)
            return candidate;

        var candidateScore = ScoreLoweredSyntaxCandidate(candidate, fallbackSyntax);
        var currentScore = ScoreLoweredSyntaxCandidate(currentBest, fallbackSyntax);

        if (candidateScore.CompareTo(currentScore) > 0)
            return candidate;

        return currentBest;
    }

    private static (int DiffersFromFallback, int IsContainedByFallback, int SpanLength, int EarlierStart) ScoreLoweredSyntaxCandidate(
        SyntaxNode candidate,
        SyntaxNode fallbackSyntax)
    {
        var differsFromFallback = candidate.Span != fallbackSyntax.Span ? 1 : 0;
        var isContainedByFallback = ContainsSpan(fallbackSyntax.Span, candidate.Span) ? 1 : 0;
        var spanLength = candidate.Span.Length;
        var earlierStart = -candidate.Span.Start;
        return (differsFromFallback, isContainedByFallback, spanLength, earlierStart);
    }

    private static bool ContainsSpan(TextSpan outer, TextSpan inner)
        => outer.Start <= inner.Start && outer.End >= inner.End;

    private static IEnumerable<BoundNode> EnumerateBoundChildren(BoundNode node)
    {
        foreach (var property in GetBoundChildProperties(node.GetType()))
        {
            var value = property.GetValue(node);
            switch (value)
            {
                case null:
                    continue;
                case BoundNode child:
                    yield return child;
                    break;
                case IEnumerable enumerable:
                    foreach (var item in enumerable)
                    {
                        if (item is BoundNode boundChild)
                            yield return boundChild;
                    }

                    break;
            }
        }
    }

    private static PropertyInfo[] GetBoundChildProperties(Type type)
    {
        lock (s_boundChildPropertyCacheGate)
        {
            if (s_boundChildPropertyCache.TryGetValue(type, out var cached))
                return cached;

            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static p => p.GetIndexParameters().Length == 0)
                .Where(static p => IsCandidateBoundChildProperty(p.PropertyType))
                .ToArray();

            s_boundChildPropertyCache[type] = properties;
            return properties;
        }
    }

    private static bool IsCandidateBoundChildProperty(Type propertyType)
    {
        if (typeof(BoundNode).IsAssignableFrom(propertyType))
            return true;

        return TryGetEnumerableElementType(propertyType, out var elementType) &&
               typeof(BoundNode).IsAssignableFrom(elementType);
    }

    private static bool TryGetEnumerableElementType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        elementType = null;

        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
            return false;

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        foreach (var @interface in type.GetInterfaces())
        {
            if (!@interface.IsGenericType ||
                @interface.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            elementType = @interface.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    internal void RemoveCachedBoundNode(SyntaxNode node)
    {
        RemoveBoundDiagnostics(node);

        if (_boundNodeCache.TryGetValue(node, out var bound))
            _syntaxCache.TryRemove(bound, out _);

        _boundNodeCache.TryRemove(node, out _);
        _nonReportingBoundNodeCache.TryRemove(node, out _);
        foreach (var key in _contextualBoundNodeCache.Keys.Where(key => ReferenceEquals(key.Node, node)))
        {
            if (_contextualBoundNodeCache.TryRemove(key, out var contextualBound))
                _syntaxCache.TryRemove(contextualBound, out _);

            _nonReportingContextualBoundNodeCache.TryRemove(key, out _);

            if (IsDebuggingEnabled)
                _contextualBoundNodeCache2.TryRemove(key, out _);
        }

        _symbolMappings.TryRemove(node, out _);
        _nonReportingSymbolMappings.TryRemove(node, out _);
        _typeMappings.TryRemove(node, out _);
        _nonReportingTypeMappings.TryRemove(node, out _);
        _operationCache.TryRemove(node, out _);
        if (node is FunctionExpressionSyntax functionExpression)
        {
            _functionExpressionSymbolCache.TryRemove(functionExpression, out _);
            _functionExpressionSymbolCreationInProgress.TryRemove(functionExpression, out _);
        }

        if (_loweredBoundNodeCache.TryGetValue(node, out var loweredBound))
            _loweredSyntaxCache.TryRemove(loweredBound, out _);

        _loweredBoundNodeCache.TryRemove(node, out _);

        if (IsDebuggingEnabled)
        {
            _boundNodeCache2.TryRemove(node, out _);
            _loweredBoundNodeCache2.TryRemove(node, out _);
        }
    }

    internal void RemoveCachedBinder(SyntaxNode node)
    {
        if (_binderCache.TryRemove(node, out var binder))
            _binderLifecycleSnapshots.TryRemove(binder, out _);

        if (CanUseStructuralBinderCache(node))
        {
            if (_binderCacheByKey.TryRemove(GetSyntaxNodeMapKey(node), out binder))
                _binderLifecycleSnapshots.TryRemove(binder, out _);
        }
    }

    internal SyntaxNode? GetSyntax(BoundNode? node)
        => node is null
            ? null
            : _syntaxCache.TryGetValue(node, out var syntax)
                ? syntax
                : _loweredSyntaxCache.TryGetValue(node, out var loweredSyntax)
                    ? loweredSyntax
                    : null;

    internal SyntaxNode? GetOriginalSyntax(BoundNode? node)
        => node is not null && _syntaxCache.TryGetValue(node, out var syntax)
            ? syntax
            : null;

    private void RegisterDeclaredTypeSymbol(SyntaxNode node, SourceNamedTypeSymbol symbol)
    {
        Compilation.RegisterDeclaredTypeSymbol(node, symbol);

        if (node is TypeDeclarationSyntax typeDecl)
            RegisterClassSymbol(typeDecl, symbol);
        else if (node is UnionDeclarationSyntax unionDecl && symbol is SourceUnionSymbol unionSymbol)
            RegisterUnionSymbol(unionDecl, unionSymbol);
    }

    private SourceNamedTypeSymbol GetDeclaredTypeSymbol(SyntaxNode node)
    {
        if (node is BaseTypeDeclarationSyntax generatedType &&
            TryGetMacroContainingTypeSyntax(generatedType, out var containingType))
        {
            node = containingType;
        }

        if (Compilation.TryGetDeclaredTypeSymbol(node, out var symbol))
            return symbol;

        if (Compilation.IsSourceNamespaceLookupDeclarationCompletionSuppressed &&
            TryDeclareAvailableSourceTypeSymbol(node, out symbol))
        {
            return symbol;
        }

        // Defensive recovery: in some re-entrant binder flows, declarations may not
        // have been materialized for this semantic model yet.
        EnsureDeclarations();
        if (Compilation.TryGetDeclaredTypeSymbol(node, out symbol))
            return symbol;

        if (node is BaseTypeDeclarationSyntax localTypeDeclaration &&
            localTypeDeclaration.Parent is TypeDeclarationStatementSyntax &&
            localTypeDeclaration.Ancestors().OfType<BlockStatementSyntax>().FirstOrDefault() is { } enclosingBlock &&
            GetBinder(enclosingBlock) is BlockBinder blockBinder)
        {
            return EnsureLocalTypeDeclarationBound(localTypeDeclaration, blockBinder);
        }

        throw new InvalidOperationException($"Type symbol not declared for syntax node '{node}'.");
    }

    internal SourceNamedTypeSymbol GetDeclaredTypeSymbolForDeclaration(SyntaxNode node)
        => GetDeclaredTypeSymbol(node);

    internal bool TryDeclareAvailableSourceTypeSymbol(
        SyntaxNode node,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SourceNamedTypeSymbol? symbol)
    {
        symbol = null;

        if (Compilation.TryGetDeclaredTypeSymbol(node, out symbol))
            return true;

        if (!TryGetDeclarationNamespace(node, out var parentNamespace))
            return false;

        var objectType = Compilation.TryGetMetadataReferenceTypeByMetadataName("System.Object");
        switch (node)
        {
            case TypeDeclarationSyntax typeDeclaration when IsNominalTypeDeclaration(typeDeclaration):
                symbol = DeclareClassSymbol(typeDeclaration, parentNamespace, objectType);
                DeclareClassMemberTypes(typeDeclaration, symbol);
                return true;

            case InterfaceDeclarationSyntax interfaceDeclaration:
                DeclareInterfaceSymbol(interfaceDeclaration, parentNamespace, objectType);
                return Compilation.TryGetDeclaredTypeSymbol(interfaceDeclaration, out symbol);

            case ExtensionDeclarationSyntax extensionDeclaration:
                DeclareExtensionSymbol(extensionDeclaration, parentNamespace, objectType);
                return Compilation.TryGetDeclaredTypeSymbol(extensionDeclaration, out symbol);

            case EnumDeclarationSyntax enumDeclaration:
                DeclareEnumSymbol(enumDeclaration, parentNamespace);
                return Compilation.TryGetDeclaredTypeSymbol(enumDeclaration, out symbol);

            case UnionDeclarationSyntax unionDeclaration:
                DeclareUnionSymbol(unionDeclaration, parentNamespace);
                return Compilation.TryGetDeclaredTypeSymbol(unionDeclaration, out symbol);

            case DelegateDeclarationSyntax delegateDeclaration:
                DeclareDelegateSymbol(delegateDeclaration, parentNamespace);
                return Compilation.TryGetDeclaredTypeSymbol(delegateDeclaration, out symbol);

            default:
                symbol = null;
                return false;
        }
    }

    private bool TryGetDeclarationNamespace(SyntaxNode declaration, out INamespaceSymbol parentNamespace)
    {
        parentNamespace = Compilation.SourceGlobalNamespace;

        var namespaceName = string.Empty;
        foreach (var namespaceDeclaration in declaration
            .Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse())
        {
            namespaceName = namespaceDeclaration is FileScopedNamespaceDeclarationSyntax ||
                string.IsNullOrWhiteSpace(namespaceName)
                    ? namespaceDeclaration.Name.ToString()
                    : namespaceName + "." + namespaceDeclaration.Name;
        }

        if (string.IsNullOrWhiteSpace(namespaceName))
            return true;

        parentNamespace = Compilation.GetOrCreateNamespaceSymbol(namespaceName)
            ?? Compilation.SourceGlobalNamespace;
        return true;
    }

    internal void RegisterClassSymbol(TypeDeclarationSyntax node, SourceNamedTypeSymbol symbol)
        => Compilation.RegisterClassSymbol(node, symbol);

    internal SourceNamedTypeSymbol GetClassSymbol(TypeDeclarationSyntax node)
        => Compilation.GetClassSymbol(node);

    internal bool TryGetClassSymbol(TypeDeclarationSyntax node, out SourceNamedTypeSymbol symbol)
        => Compilation.TryGetClassSymbol(node, out symbol!);

    internal void RegisterUnionSymbol(UnionDeclarationSyntax node, SourceUnionSymbol symbol)
        => Compilation.RegisterUnionSymbol(node, symbol);

    internal SourceUnionSymbol GetUnionSymbol(UnionDeclarationSyntax node)
        => Compilation.GetUnionSymbol(node);

    internal bool TryGetUnionSymbol(UnionDeclarationSyntax node, out SourceUnionSymbol symbol)
        => Compilation.TryGetUnionSymbol(node, out symbol!);

    internal void RegisterUnionCaseSymbol(CaseDeclarationSyntax node, SourceUnionCaseTypeSymbol symbol)
        => Compilation.RegisterUnionCaseSymbol(node, symbol);

    internal SourceUnionCaseTypeSymbol GetUnionCaseSymbol(CaseDeclarationSyntax node)
        => Compilation.GetUnionCaseSymbol(node);

    internal bool TryGetUnionCaseSymbol(CaseDeclarationSyntax node, out SourceUnionCaseTypeSymbol symbol)
        => Compilation.TryGetUnionCaseSymbol(node, out symbol!);

    private static SyntaxNodeMapKey GetSyntaxNodeMapKey(SyntaxNode node)
        => new(node.SyntaxTree, node.Span, node.Kind);

    private readonly record struct SyntaxNodeMapKey(SyntaxTree SyntaxTree, TextSpan Span, SyntaxKind Kind);
}
