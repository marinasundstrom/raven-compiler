using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis;

partial class BlockBinder : Binder
{
    private static readonly DiagnosticDescriptor s_macroUsedLikeAType = DiagnosticDescriptor.Create(
        "RAVM015",
        "Macro used as a type",
        "",
        "",
        "'{0}' is a macro, not a type.",
        "compiler",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor s_macroExpressionResultTypeMismatch = DiagnosticDescriptor.Create(
        "RAVM023",
        "Macro expression result type mismatch",
        "",
        "",
        "Macro '{0}' promises an expression compatible with '{1}', but its expansion has type '{2}'.",
        "compiler",
        DiagnosticSeverity.Error,
        true);

    private sealed class ExpressionSyntaxStructuralComparer : IEqualityComparer<ExpressionSyntax>
    {
        public bool Equals(ExpressionSyntax? x, ExpressionSyntax? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;

            return ReferenceEquals(x.SyntaxTree, y.SyntaxTree) &&
                   x.Kind == y.Kind &&
                   x.Span == y.Span;
        }

        public int GetHashCode(ExpressionSyntax obj)
        {
            return HashCode.Combine(obj.SyntaxTree, (int)obj.Kind, obj.Span.Start, obj.Span.Length);
        }
    }

    private readonly ISymbol _containingSymbol;
    protected readonly object _executionGate = new();
    protected readonly Dictionary<string, (ILocalSymbol Symbol, int Depth)> _locals = new();
    private readonly Dictionary<string, (SourceNamedTypeSymbol Symbol, int Depth)> _localTypes = new();
    private readonly List<(ILocalSymbol Local, int Depth)> _localsToDispose = new();
    private readonly Dictionary<string, ILabelSymbol> _labelsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<LabeledStatementSyntax, ILabelSymbol> _labelsBySyntax = new();
    private readonly Dictionary<ILabelSymbol, LabeledStatementSyntax> _syntaxByLabel = new(SymbolEqualityComparer.Default);
    private readonly HashSet<SyntaxNode> _labelDeclarationNodes = new();
    private readonly Dictionary<ExpressionSyntax, ImmutableArray<INamedTypeSymbol>> _lambdaDelegateTargets = new(new ExpressionSyntaxStructuralComparer());
    private readonly Dictionary<FunctionExpressionRebindKey, BoundFunctionExpression> _reboundLambdaCache = new();
    private readonly Stack<TargetTypeFrame> _targetTypeStack = new();
    private readonly InvocationResolver _invocationResolver;
    private readonly BlockDeclarationState _declarationState = new();
    private readonly StatementDeclarationProgress _statementDeclarationProgress = new();
    private readonly HashSet<SyntaxReferenceKey> _localDeclarationUpgradesInProgress = new();
    private readonly HashSet<ISymbol> _isNotNullNarrowedSymbols = new(SymbolEqualityComparer.Default);
    private int _scopeDepth;
    private bool _allowReturnsInExpression;
    private bool _allowReturnsInBlockExpressionsOnly;
    private int _loopDepth;
    private int _expressionContextDepth;
    private int _tempCounter;
    private int _objectInitializerDepth;
    private int _withInitializerDepth;
    private int _unsafeBlockDepth;
    private int _semanticQueryScopeDepth;
    private SyntaxKind _ambientPatternDeclarationBindingKeyword;

    private bool IsInObjectInitializer => _objectInitializerDepth > 0;
    private bool IsInWithInitializer => _withInitializerDepth > 0;

    private bool IsInInitOnlyAssignmentContext =>
        IsInObjectInitializer || IsInWithInitializer;

    protected override bool IsInUnsafeContext => _unsafeBlockDepth > 0 || base.IsInUnsafeContext;

    private static bool IsInitOnly(IPropertySymbol property)
        => property.SetMethod?.MethodKind == MethodKind.InitOnly;

    public BlockBinder(ISymbol containingSymbol, Binder parent) : base(parent)
    {
        _containingSymbol = containingSymbol;
        _invocationResolver = new InvocationResolver(this);

        if (parent is BlockBinder parentBlockBinder)
        {
            foreach (var targetType in parentBlockBinder._targetTypeStack.Reverse())
                _targetTypeStack.Push(targetType);
        }
    }

    public override ISymbol ContainingSymbol => _containingSymbol;

    public override ISymbol? BindDeclaredSymbol(SyntaxNode node)
    {
        using var _ = EnterSemanticQueryScope();
        return node switch
        {
            VariableDeclaratorSyntax
            {
                Identifier.ValueText: "_",
                Identifier.IsMissing: false,
                Parent: VariableDeclarationSyntax { BindingKeyword.Kind: SyntaxKind.LetKeyword or SyntaxKind.ValKeyword }
            } => null,
            VariableDeclaratorSyntax v => BindDeclaredLocalSymbol(v),
            CompilationUnitSyntax unit => BindCompilationUnit(unit).Symbol,
            SingleVariableDesignationSyntax singleVariableDesignation => BindDeclaredPatternLocal(singleVariableDesignation),
            FunctionStatementSyntax functionStatement => BindFunction(functionStatement).Method,
            BaseTypeDeclarationSyntax typeDeclaration when typeDeclaration.Parent is TypeDeclarationStatementSyntax
                => SemanticModel.EnsureLocalTypeDeclarationBound(typeDeclaration, this),
            LabeledStatementSyntax labeledStatement => DeclareLabelSymbol(labeledStatement),
            _ => base.BindDeclaredSymbol(node)
        };
    }

    private ILocalSymbol? BindDeclaredPatternLocal(SingleVariableDesignationSyntax singleVariableDesignation)
    {
        if (IsExistingAssignmentTargetDesignation(singleVariableDesignation))
            return null;

        EnsurePrecedingStatementDeclarations(singleVariableDesignation);

        if (TryGetCachedBoundNode(singleVariableDesignation) is BoundSingleVariableDesignator cachedDesignator)
            return cachedDesignator.Local;

        if (TryGetPatternDeclarationOwner(
            singleVariableDesignation,
            PatternDeclarationOwnerPurpose.Binding,
            out var owner))
        {
            BindPatternDeclarationOwner(owner);

            return TryGetCachedBoundNode(singleVariableDesignation) is BoundSingleVariableDesignator reboundDesignator
                ? reboundDesignator.Local
                : null;
        }

        return BindSingleVariableDesignation(singleVariableDesignation)?.Local;
    }

    private static bool IsExistingAssignmentTargetDesignation(SingleVariableDesignationSyntax designation)
    {
        if (HasInlinePatternBindingKeyword(designation))
            return false;

        for (SyntaxNode? current = designation; current is not null; current = current.Parent)
        {
            if (current.Parent is PatternDeclarationAssignmentStatementSyntax patternDeclaration &&
                ContainsNode(patternDeclaration.Left, designation))
            {
                return false;
            }

            if (current.Parent is AssignmentStatementSyntax assignmentStatement &&
                ContainsNode(assignmentStatement.Left, designation))
            {
                return true;
            }

            if (current.Parent is AssignmentExpressionSyntax assignmentExpression &&
                ContainsNode(assignmentExpression.Left, designation))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInlinePatternBindingKeyword(SingleVariableDesignationSyntax designation)
    {
        if (IsDeclarationBindingKeyword(designation.BindingKeyword.Kind))
            return true;

        return designation
            .Ancestors()
            .OfType<VariablePatternSyntax>()
            .Any(pattern =>
                IsDeclarationBindingKeyword(pattern.BindingKeyword.Kind) &&
                ContainsNode(pattern.Designation, designation));
    }

    private static bool ContainsNode(SyntaxNode root, SyntaxNode node)
        => ReferenceEquals(root.SyntaxTree, node.SyntaxTree) &&
           node.Span.Start >= root.Span.Start &&
           node.Span.End <= root.Span.End;

    private enum PatternDeclarationOwnerPurpose
    {
        Binding,
        LexicalScope
    }

    private static bool TryGetPatternDeclarationOwner(
        SingleVariableDesignationSyntax designation,
        PatternDeclarationOwnerPurpose purpose,
        out SyntaxNode owner)
    {
        if (designation.GetAncestor<MatchExpressionSyntax>() is { } matchExpression)
        {
            owner = matchExpression;
            return true;
        }

        if (designation.GetAncestor<PostfixMatchExpressionSyntax>() is { } postfixMatchExpression)
        {
            owner = postfixMatchExpression;
            return true;
        }

        if (designation.GetAncestor<MatchStatementSyntax>() is { } matchStatement)
        {
            owner = matchStatement;
            return true;
        }

        if (designation.GetAncestor<IfStatementSyntax>() is { } ifStatement &&
            ContainsNode(ifStatement.Condition, designation))
        {
            owner = ifStatement;
            return true;
        }

        if (designation.GetAncestor<IsPatternExpressionSyntax>() is { } isPatternExpression)
        {
            owner = isPatternExpression;
            return true;
        }

        if (designation.GetAncestor<IfPatternStatementSyntax>() is { } ifPatternStatement)
        {
            owner = ifPatternStatement;
            return true;
        }

        if (designation.GetAncestor<IfPatternExpressionSyntax>() is { } ifPatternExpression)
        {
            owner = ifPatternExpression;
            return true;
        }

        if (designation.GetAncestor<WhilePatternStatementSyntax>() is { } whilePatternStatement)
        {
            owner = whilePatternStatement;
            return true;
        }

        if (designation.GetAncestor<ForStatementSyntax>() is { } forStatement)
        {
            owner = forStatement;
            return true;
        }

        if (designation.GetAncestor<CollectionComprehensionElementSyntax>() is { } collectionComprehension)
        {
            owner = collectionComprehension.GetAncestor<CollectionExpressionSyntax>() ?? (SyntaxNode)collectionComprehension;
            return true;
        }

        if (designation.GetAncestor<DictionaryComprehensionElementSyntax>() is { } dictionaryComprehension)
        {
            owner = dictionaryComprehension.GetAncestor<CollectionExpressionSyntax>() ?? (SyntaxNode)dictionaryComprehension;
            return true;
        }

        if (designation.GetAncestor<PatternDeclarationAssignmentStatementSyntax>() is { } patternDeclarationAssignment)
        {
            owner = purpose == PatternDeclarationOwnerPurpose.LexicalScope
                ? patternDeclarationAssignment.Parent ?? patternDeclarationAssignment
                : patternDeclarationAssignment;
            return true;
        }

        if (designation.GetAncestor<AssignmentStatementSyntax>() is { } assignmentStatement &&
            ContainsNode(assignmentStatement.Left, designation))
        {
            owner = assignmentStatement;
            return true;
        }

        if (designation.GetAncestor<AssignmentExpressionSyntax>() is { } assignmentExpression &&
            ContainsNode(assignmentExpression.Left, designation))
        {
            owner = assignmentExpression;
            return true;
        }

        owner = null!;
        return false;
    }

    private ILocalSymbol BindDeclaredLocalSymbol(VariableDeclaratorSyntax variableDeclarator)
    {
        EnsurePrecedingStatementDeclarations(variableDeclarator);
        return BindLocalDeclaration(variableDeclarator, declarationOnly: true).Local;
    }

    internal ILocalSymbol? TryDeclareLocalSymbolShallow(
        VariableDeclaratorSyntax variableDeclarator,
        bool allowInitializerBinding = true,
        bool allowBoundInitializerBinding = true,
        bool ensurePrecedingDeclarations = true)
    {
        if (variableDeclarator.Parent is not VariableDeclarationSyntax declaration)
            return null;

        var name = variableDeclarator.Identifier.ValueText;
        if (name == "_" || variableDeclarator.Identifier.IsMissing)
            return null;

        if (ensurePrecedingDeclarations)
            EnsurePrecedingStatementDeclarations(variableDeclarator);

        if (TryGetDeclaredLocal(variableDeclarator, out var existingDeclaredLocal))
        {
            if (IsCompleteLocalDeclarationType(existingDeclaredLocal.Type) || !allowInitializerBinding)
            {
                RegisterLocalForCurrentLookup(name, existingDeclaredLocal);
                return existingDeclaredLocal;
            }
        }

        if (TryGetSameLocalFromCurrentLookup(name, variableDeclarator, out var sameLocal))
        {
            if (IsCompleteLocalDeclarationType(sameLocal.Type) || !allowInitializerBinding)
            {
                RegisterLocalForCurrentLookup(name, sameLocal);
                OnLocalDeclared(sameLocal, variableDeclarator);
                return sameLocal;
            }
        }

        var isUseDeclaration = declaration.Parent is UseDeclarationStatementSyntax;
        if (!isUseDeclaration &&
            declaration.BindingKeyword.Kind is not (SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword or SyntaxKind.ConstKeyword))
        {
            return null;
        }

        var isMutable = declaration.BindingKeyword.Kind == SyntaxKind.VarKeyword;
        var isConst = declaration.BindingKeyword.Kind == SyntaxKind.ConstKeyword;
        var type = TryResolveLocalDeclaredTypeShallow(variableDeclarator)
            ?? (allowInitializerBinding
                ? TryInferLocalTypeFromExplicitGenericConstructorInitializer(variableDeclarator)
                : null)
            ?? (allowInitializerBinding || CanInferLocalTypeFromAvailableInitializerDuringDeclarationSeeding(variableDeclarator)
                ? TryInferLocalTypeFromAvailableInitializer(variableDeclarator)
                : null)
            ?? (allowInitializerBinding && allowBoundInitializerBinding
                ? TryInferLocalTypeFromBoundInitializer(variableDeclarator, ensurePrecedingDeclarations)
                : null);
        if (type is null && !allowInitializerBinding)
            type = Compilation.ErrorTypeSymbol;

        if (type is null)
            return null;

        var scopedKind = GetLocalScopedKind(declaration, type);
        if (existingDeclaredLocal is not null &&
            ShouldUpgradeLocalDeclarationType(existingDeclaredLocal.Type, type))
        {
            var upgradedLocal = CreateLocalSymbol(
                variableDeclarator,
                name,
                isMutable,
                type,
                isConst,
                scopedKind: scopedKind,
                recordDeclaration: false);
            _declarationState.ReplaceDeclaredLocal(existingDeclaredLocal, upgradedLocal, variableDeclarator);
            return upgradedLocal;
        }

        return CreateLocalSymbol(variableDeclarator, name, isMutable, type, isConst, scopedKind: scopedKind);
    }

    private static bool CanInferLocalTypeFromAvailableInitializerDuringDeclarationSeeding(VariableDeclaratorSyntax variableDeclarator)
    {
        if (variableDeclarator.Initializer?.Value is not { } initializer)
            return false;

        return !initializer.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any() &&
            !initializer.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>().Any();
    }

    private ITypeSymbol? TryInferLocalTypeFromExplicitGenericConstructorInitializer(VariableDeclaratorSyntax variableDeclarator)
    {
        if (variableDeclarator.TypeAnnotation is not null ||
            variableDeclarator.Initializer?.Value is not InvocationExpressionSyntax
            {
                Expression: GenericNameSyntax genericName,
                Initializer: null
            } invocation ||
            invocation.ArgumentList.Arguments.Count != 0)
        {
            return null;
        }

        if (LookupSymbols(genericName.Identifier.ValueText).OfType<IMethodSymbol>().Any())
            return null;

        var result = BindTypeSyntax(genericName);
        if (!result.Success || result.ResolvedType is not { } type)
            return null;

        if (type.TypeKind == TypeKind.Error || IsIncompleteGenericLocalType(type))
            return null;

        type = EnsureTypeAccessible(type, genericName.GetLocation());
        return EnsureTypeValidForStorageLocation(type, genericName.GetLocation());
    }

    private ITypeSymbol? TryResolveLocalDeclaredTypeShallow(VariableDeclaratorSyntax variableDeclarator)
    {
        if (variableDeclarator.TypeAnnotation?.Type is not { } typeSyntax)
            return null;

        var result = BindTypeSyntax(typeSyntax);
        if (!result.Success || result.ResolvedType is null)
            return Compilation.ErrorTypeSymbol;

        var type = result.ResolvedType;
        if (type.TypeKind == TypeKind.Error)
            return type;

        type = EnsureTypeAccessible(type, typeSyntax.GetLocation());
        return EnsureTypeValidForStorageLocation(type, typeSyntax.GetLocation());
    }

    private ITypeSymbol? TryInferLocalTypeFromAvailableInitializer(VariableDeclaratorSyntax variableDeclarator)
    {
        if (variableDeclarator.TypeAnnotation is not null ||
            variableDeclarator.Initializer?.Value is not { } initializer)
        {
            return null;
        }

        if (!SemanticModel.TryGetAvailableTypeInfo(initializer, out var typeInfo))
            return null;

        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type is null || type.TypeKind == TypeKind.Error)
            return null;

        type = TypeSymbolNormalization.NormalizeForInference(type);
        type = EnsureTypeAccessible(type, initializer.GetLocation());
        return EnsureTypeValidForStorageLocation(type, initializer.GetLocation());
    }

    private ITypeSymbol? TryInferLocalTypeFromBoundInitializer(
        VariableDeclaratorSyntax variableDeclarator,
        bool ensurePrecedingDeclarations = true)
    {
        if (variableDeclarator.TypeAnnotation is not null ||
            variableDeclarator.Initializer?.Value is not { } initializer)
        {
            return null;
        }

        if (!initializer.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any())
        {
            return null;
        }

        if (ensurePrecedingDeclarations)
            EnsurePrecedingStatementDeclarations(variableDeclarator);

        using var _ = EnterSemanticQueryScope();
        var boundInitializer = BindExpression(initializer);
        var type = boundInitializer.Type ?? boundInitializer.GetConvertedType();
        if (type is null || type.TypeKind == TypeKind.Error)
            return null;

        type = TypeSymbolNormalization.NormalizeForInference(type);
        type = EnsureTypeAccessible(type, initializer.GetLocation());
        return EnsureTypeValidForStorageLocation(type, initializer.GetLocation());
    }

    private void EnsurePrecedingStatementDeclarations(SyntaxNode node)
    {
        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent is null)
            return;

        if (statement.Parent is GlobalStatementSyntax globalStatement)
        {
            EnsurePrecedingGlobalStatementDeclarations(globalStatement);
            return;
        }

        var seededThrough = _statementDeclarationProgress.GetSeededThrough(statement.Parent);
        foreach (var sibling in statement.Parent.ChildNodes().OfType<StatementSyntax>())
        {
            if (sibling.Span.Start >= statement.Span.Start)
                break;

            if (sibling.Span.Start <= seededThrough)
                continue;

            EnsureStatementDeclarations(sibling);
            _statementDeclarationProgress.MarkSeededThrough(statement.Parent, sibling.Span.Start);
        }
    }

    internal void EnsurePrecedingStatementContextForSemanticQuery(SyntaxNode node)
    {
        EnsurePrecedingStatementDeclarations(node);

        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent is null || statement.Parent is GlobalStatementSyntax)
            return;

        foreach (var sibling in statement.Parent.ChildNodes().OfType<StatementSyntax>())
        {
            if (sibling.Span.Start >= statement.Span.Start)
                break;

            if (sibling is LocalDeclarationStatementSyntax localDeclaration &&
                localDeclaration.Declaration.Declarators.Any(RequiresInferredLocalBinding))
            {
                _ = BindLocalDeclarationStatement(localDeclaration);
            }
        }

        bool RequiresInferredLocalBinding(VariableDeclaratorSyntax declarator)
            => declarator.TypeAnnotation is null &&
               (!TryGetDeclaredLocal(declarator, out var local) ||
                !IsCompleteLocalDeclarationType(local.Type));
    }

    private void EnsurePrecedingGlobalStatementDeclarations(GlobalStatementSyntax globalStatement)
    {
        if (globalStatement.Parent is null)
            return;

        var seededThrough = _statementDeclarationProgress.GetSeededThrough(globalStatement.Parent);
        foreach (var sibling in globalStatement.Parent.ChildNodes().OfType<GlobalStatementSyntax>())
        {
            if (sibling.Span.Start >= globalStatement.Span.Start)
                break;

            if (sibling.Span.Start <= seededThrough)
                continue;

            EnsureStatementDeclarations(sibling.Statement);
            _statementDeclarationProgress.MarkSeededThrough(globalStatement.Parent, sibling.Span.Start);
        }
    }

    internal override void EnsureStatementDeclarations(StatementSyntax statement)
    {
        using var scope = EnterExecutionScope();

        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDeclaration:
                EnsureVariableDeclarationDeclarations(localDeclaration.Declaration);
                break;

            case UseDeclarationStatementSyntax { InBlockClause: null } useDeclaration:
                EnsureVariableDeclarationDeclarations(useDeclaration.Declaration);
                break;

            case PatternDeclarationAssignmentStatementSyntax patternDeclarationAssignment:
                _ = BindPatternDeclarationAssignmentStatement(patternDeclarationAssignment);
                break;

            case FunctionStatementSyntax function:
                _ = BindFunction(function);
                break;

            case TypeDeclarationStatementSyntax typeDeclaration:
                _ = SemanticModel.EnsureLocalTypeDeclarationBound(typeDeclaration.Declaration, this);
                break;
        }
    }

    private void EnsureVariableDeclarationDeclarations(VariableDeclarationSyntax declaration)
    {
        if ((declaration.BindingKeyword.IsKind(SyntaxKind.LetKeyword) ||
             declaration.BindingKeyword.IsKind(SyntaxKind.ValKeyword)) &&
            declaration.Declarators.Count > 0 &&
            IsDiscardDeclarator(declaration.Declarators[0]))
        {
            return;
        }

        foreach (var declarator in declaration.Declarators)
        {
            if (TryDeclareLocalSymbolShallow(declarator, allowInitializerBinding: false) is null)
                _ = BindLocalDeclaration(declarator, declarationOnly: true);
        }
    }

    private void BindPatternDeclarationOwner(SyntaxNode owner)
    {
        _ = GetOrBind(owner);
    }

    public override SymbolInfo BindReferencedSymbol(SyntaxNode node)
    {
        using var _ = EnterSemanticQueryScope();
        EnsurePrecedingStatementDeclarations(node);

        return node switch
        {
            IdentifierNameSyntax identifier => BindIdentifierReference(identifier),
            InvocationExpressionSyntax invocation => BindInvocationReference(invocation),
            MemberAccessExpressionSyntax memberAccess => BindMemberAccessReference(memberAccess),
            MemberBindingExpressionSyntax memberBinding => BindMemberBindingReference(memberBinding),
            ExpressionSyntax expr => BindExpression(expr).GetSymbolInfo(),
            ExpressionStatementSyntax stmt => BindStatement(stmt).GetSymbolInfo(),
            _ => base.BindReferencedSymbol(node)
        };
    }

    internal override SymbolInfo BindIdentifierReference(IdentifierNameSyntax node)
    {
        return BindIdentifierName(node, allowEventAccess: IsEventAssignmentLeftHandSide(node)).GetSymbolInfo();
    }

    internal override SymbolInfo BindInvocationReference(InvocationExpressionSyntax node)
    {
        return BindInvocationExpression(node).GetSymbolInfo();
    }

    internal override SymbolInfo BindMemberAccessReference(MemberAccessExpressionSyntax node)
    {
        return BindMemberAccessExpression(node, allowEventAccess: IsEventAssignmentLeftHandSide(node)).GetSymbolInfo();
    }

    internal override SymbolInfo BindMemberBindingReference(MemberBindingExpressionSyntax node)
    {
        return BindMemberBindingExpression(node, allowEventAccess: IsEventAssignmentLeftHandSide(node)).GetSymbolInfo();
    }

    private static bool IsEventAssignmentLeftHandSide(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current.Parent is AssignmentStatementSyntax assignmentStatement &&
                IsSameSyntaxNode(assignmentStatement.Left, current) &&
                IsEventAssignmentOperator(assignmentStatement.OperatorToken.Kind))
            {
                return true;
            }

            if (current.Parent is AssignmentExpressionSyntax assignmentExpression &&
                IsSameSyntaxNode(assignmentExpression.Left, current) &&
                IsEventAssignmentOperator(assignmentExpression.OperatorToken.Kind))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEventAssignmentOperator(SyntaxKind kind)
    {
        return kind is SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken;
    }

    public override ISymbol? LookupSymbol(string name)
    {
        // Forward to LookupSymbols so import directives and alias mappings are considered
        // when binding identifier references. Previously this method bypassed the import
        // binder, causing namespace or type aliases to be ignored and resulting in missing
        // completions for alias-qualified names.
        return LookupSymbols(name).FirstOrDefault();
    }

    public override ITypeSymbol? LookupType(string name)
    {
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (current is BlockBinder block && block._localTypes.TryGetValue(name, out var localType))
                return localType.Symbol;

            if (current is FunctionExpressionBinder functionExpressionBinder &&
                functionExpressionBinder.ContainingSymbol is IMethodSymbol lambdaMethod)
            {
                var methodTypeParameter = lambdaMethod.TypeParameters.FirstOrDefault(tp => tp.Name == name);
                if (methodTypeParameter is not null)
                    return methodTypeParameter;
            }

            if (current is MethodBinder methodBinder)
            {
                var methodTypeParameter = methodBinder.GetMethodSymbol().TypeParameters.FirstOrDefault(tp => tp.Name == name);
                if (methodTypeParameter is not null)
                    return methodTypeParameter;
            }

            if (current is TypeDeclarationBinder typeDeclarationBinder)
            {
                var typeParameter = typeDeclarationBinder.ContainingSymbol.TypeParameters.FirstOrDefault(tp => tp.Name == name);
                if (typeParameter is not null)
                    return typeParameter;
            }
        }

        return base.LookupType(name);
    }

    private SymbolInfo BindCompilationUnit(CompilationUnitSyntax compilationUnit)
    {
        var entryPoint = Compilation.GetEntryPoint();
        if (entryPoint is not null && entryPoint.IsImplicitlyDeclared)
        {
            if (entryPoint.DeclaringSyntaxReferences.FirstOrDefault()!.GetSyntax() == compilationUnit)
            {
                return new SymbolInfo(entryPoint);
            }
        }
        return new SymbolInfo(Compilation.SourceGlobalNamespace);
    }

    private BoundVariableDeclarator BindLocalDeclaration(
        VariableDeclaratorSyntax variableDeclarator,
        bool declarationOnly = false)
    {
        var name = variableDeclarator.Identifier.ValueText;
        var decl = (VariableDeclarationSyntax)variableDeclarator.Parent!;
        var bindingKeyword = decl.BindingKeyword;
        var isUseDeclaration = decl.Parent is UseDeclarationStatementSyntax;
        var isFixedInitializer = variableDeclarator.Initializer?.Value is PrefixOperatorExpressionSyntax { Kind: SyntaxKind.FixedExpression };
        var initializer = variableDeclarator.Initializer;
        var existingDeclaredLocal = TryGetDeclaredLocal(variableDeclarator, out var declaredLocal)
            ? declaredLocal
            : null;

        if (TryBindExistingLocalDeclaration(
            variableDeclarator,
            name,
            allowDeclaredOnly: declarationOnly &&
                existingDeclaredLocal is not null &&
                IsCompleteLocalDeclarationType(existingDeclaredLocal.Type) &&
                !SemanticModel.IsCollectingDiagnostics,
            allowCurrentLookupOnly: declarationOnly,
            out var existingDeclarator))
        {
            return existingDeclarator;
        }

        var isDuplicateInScope = false;

        if (_locals.TryGetValue(name, out var existing) && existing.Depth == _scopeDepth)
        {
            var isSameDeclarator = IsSameDeclaringSyntax(existing.Symbol, variableDeclarator);

            if (isSameDeclarator && declarationOnly)
                return new BoundVariableDeclarator(existing.Symbol, null);

            if (!isSameDeclarator)
                isDuplicateInScope = true;
        }

        if (!isDuplicateInScope &&
            _declarationState.TryGetDeclaredLocalInSameScope(name, variableDeclarator, out var sameScopeLocal) &&
            !IsSameDeclaringSyntax(sameScopeLocal, variableDeclarator))
        {
            isDuplicateInScope = true;
        }

        if (isDuplicateInScope)
        {
            _diagnostics.ReportVariableAlreadyDefined(name, variableDeclarator.Identifier.GetLocation());
        }
        else if (TryLookupDeclarationForShadowing(name, out var lookupSymbol) &&
                 !IsSameDeclaringSyntax(lookupSymbol, variableDeclarator))
        {
            _diagnostics.ReportVariableShadowsPreviousDeclaration(name, variableDeclarator.Identifier.GetLocation());
        }
        var isConst = bindingKeyword.IsKind(SyntaxKind.ConstKeyword);
        var isMutable = bindingKeyword.IsKind(SyntaxKind.VarKeyword);
        var shouldDispose = isUseDeclaration && !isFixedInitializer;

        ITypeSymbol type = Compilation.ErrorTypeSymbol;
        BoundExpression? boundInitializer = null;
        ITypeSymbol? initializerValueType = null;
        ITypeSymbol? annotatedType = null;
        IMethodSymbol? functionValueTargetMethod = null;
        BoundAddressOfExpression? fixedAddressInitializer = null;
        ILocalSymbol? fixedPinnedLocal = null;
        var isFunctionValueAlias = false;
        var typeLocation = variableDeclarator.TypeAnnotation?.Type.GetLocation()
            ?? bindingKeyword.GetLocation();
        object? constantValue = null;
        if (variableDeclarator.TypeAnnotation is not null)
        {
            annotatedType = ResolveTypeSyntaxOrError(variableDeclarator.TypeAnnotation.Type);
            annotatedType = EnsureTypeValidForStorageLocation(annotatedType, variableDeclarator.TypeAnnotation.Type.GetLocation());
        }
        if (initializer is not null)
        {
            // Initializers evaluate to values, but block expressions can still be used
            // as scoped early-exit helpers. Allow early-exit statements inside block
            // expressions only, while keeping inline expression arms (`if`/`match`)
            // under expression-context restrictions.
            boundInitializer = BindExpressionWithTargetType(
                initializer.Value,
                annotatedType,
                allowReturn: false,
                allowReturnInBlockExpressionsOnly: true);
            boundInitializer = BindImplicitParameterlessConstructionIfNeeded(boundInitializer, initializer.Value);
            initializerValueType = boundInitializer?.Type;
        }

        if (initializer is null)
        {
            if (annotatedType is not null)
                type = annotatedType;

            _diagnostics.ReportLocalVariableMustBeInitialized(name, variableDeclarator.Identifier.GetLocation());
            boundInitializer = new BoundErrorExpression(type, null, BoundExpressionReason.OtherError);
        }
        else if (variableDeclarator.TypeAnnotation is null)
        {
            if (boundInitializer is BoundFunctionExpression lambdaInitializer &&
                lambdaInitializer.Symbol is IMethodSymbol lambdaMethod &&
                !lambdaMethod.TypeParameters.IsDefaultOrEmpty &&
                initializer?.Value is FunctionExpressionSyntax functionSyntax &&
                functionSyntax switch
                {
                    ParenthesizedFunctionExpressionSyntax p => p.FuncKeyword.Kind == SyntaxKind.FuncKeyword,
                    SimpleFunctionExpressionSyntax s => s.FuncKeyword.Kind == SyntaxKind.FuncKeyword,
                    _ => false
                })
            {
                isFunctionValueAlias = true;
                functionValueTargetMethod = lambdaMethod;

                if (lambdaMethod.TypeParameters.IsDefaultOrEmpty)
                {
                    type = lambdaInitializer.DelegateType;
                }
                else
                {
                    // Generic function values are method aliases, not runtime delegate instances.
                    type = Compilation.GetSpecialType(SpecialType.System_Object);
                }
            }
            else
            {
                if (boundInitializer is BoundMethodGroupExpression methodGroup)
                {
                    var inferredDelegate = methodGroup.DelegateType ?? methodGroup.DelegateTypeFactory?.Invoke();
                    if (inferredDelegate is INamedTypeSymbol delegateType && delegateType.TypeKind == TypeKind.Delegate)
                    {
                        boundInitializer = ConvertMethodGroupToDelegate(methodGroup, delegateType, initializer.Value);
                        CacheBoundNode(initializer.Value, boundInitializer);
                        type = delegateType;
                    }
                    else
                    {
                        boundInitializer = ReportMethodGroupRequiresDelegate(methodGroup, initializer.Value);
                        CacheBoundNode(initializer.Value, boundInitializer);
                        type = Compilation.ErrorTypeSymbol;
                    }
                }
                else if (boundInitializer?.Type is { } initType)
                {
                    type = TypeSymbolNormalization.NormalizeForInference(initType);
                }
                else
                {
                    type = Compilation.ErrorTypeSymbol;
                }
            }
        }
        else
        {
            type = annotatedType ?? ResolveTypeSyntaxOrError(variableDeclarator.TypeAnnotation.Type);
        }

        var constantValueComputed = false;

        if (type.TypeKind != TypeKind.Error &&
            initializer is not null &&
            boundInitializer is not null &&
            !isFunctionValueAlias &&
            ShouldAttemptConversion(boundInitializer))
        {
            boundInitializer = BindLambdaToDelegateIfNeeded(boundInitializer, type);
            if (!IsAssignable(type, boundInitializer, out var conversion))
            {
                if (initializer is not null &&
                    ConstantValueEvaluator.TryEvaluate(initializer.Value, out var evaluated) &&
                    evaluated is not null &&
                    ConstantValueEvaluator.TryConvert(type, evaluated, out var converted))
                {
                    constantValue = converted;
                    constantValueComputed = true;

                    if (boundInitializer is BoundLiteralExpression literal)
                    {
                        var literalValue = converted ?? literal.Value;
                        boundInitializer = new BoundLiteralExpression(literal.Kind, literalValue!, literal.Type!, type);
                    }

                    CacheBoundNode(initializer.Value, boundInitializer);
                }
                else
                {
                    ReportCannotAssignExpressionToType(boundInitializer, type, initializer.Value.GetLocation());
                    boundInitializer = new BoundErrorExpression(type, null, BoundExpressionReason.TypeMismatch);
                }
            }
            else
            {
                boundInitializer = ApplyConversion(boundInitializer, type, conversion, initializer.Value);
                CacheBoundNode(initializer.Value, boundInitializer);
            }
        }

        type = EnsureTypeAccessible(type, typeLocation);
        type = EnsureTypeValidForStorageLocation(type, typeLocation);

        if (decl.ScopedKeyword.IsKind(SyntaxKind.ScopedKeyword) &&
            type is not RefTypeSymbol &&
            type.TypeKind != TypeKind.Error &&
            !SemanticFacts.MayBeRefLike(type))
        {
            _diagnostics.ReportScopedModifierRequiresRefLikeTypeOrReference(
                decl.ScopedKeyword.GetLocation());
        }

        if (!constantValueComputed &&
            isConst &&
            initializer is not null &&
            type.TypeKind != TypeKind.Error &&
            boundInitializer is not BoundErrorExpression)
        {
            if (!ConstantValueEvaluator.TryEvaluate(initializer.Value, out var rawConstant))
            {
                _diagnostics.ReportConstLocalMustBeConstant(name, initializer.Value.GetLocation());
            }
            else if (!ConstantValueEvaluator.TryConvert(type, rawConstant, out constantValue))
            {
                var display = type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                _diagnostics.ReportConstLocalCannotConvert(name, display, initializer.Value.GetLocation());
            }
        }

        if (initializer is not null && boundInitializer is not null)
            CacheBoundNode(initializer.Value, boundInitializer);

        if (isFixedInitializer)
        {
            fixedAddressInitializer = TryGetFixedAddressInitializer(boundInitializer);
            if (fixedAddressInitializer is not null && type.TypeKind != TypeKind.Error)
            {
                fixedPinnedLocal = CreateSynthesizedPinnedLocal(variableDeclarator, name, fixedAddressInitializer.Type);
            }
        }

        if (isUseDeclaration && !isFixedInitializer)
        {
            var preferAsyncDispose = PrefersAsyncUseDisposal();
            var expectedTargetDisplay = UseDisposalUtilities.GetExpectedUseTargetDisplay(Compilation, preferAsyncDispose);

            var initializerSupportsDispose = initializerValueType is not null &&
                IsValidUseDeclarationTarget(initializerValueType, preferAsyncDispose);

            if (!initializerSupportsDispose && initializerValueType is not null && initializerValueType.TypeKind != TypeKind.Error)
            {
                ReportCannotConvertFromTypeToType(
                    initializerValueType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    expectedTargetDisplay,
                    variableDeclarator.Identifier.GetLocation());
                shouldDispose = false;
            }
            else if (type.TypeKind != TypeKind.Error &&
                     !IsValidUseDeclarationTarget(type, preferAsyncDispose))
            {
                ReportCannotConvertFromTypeToType(
                    type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    expectedTargetDisplay,
                    variableDeclarator.Identifier.GetLocation());
                shouldDispose = false;
            }
        }

        ILocalSymbol localSymbol;
        var scopedKind = GetLocalScopedKind(decl, type);
        if (existingDeclaredLocal is not null &&
            ShouldUpgradeLocalDeclarationType(existingDeclaredLocal.Type, type))
        {
            localSymbol = isFunctionValueAlias && functionValueTargetMethod is not null
                ? CreateFunctionValueSymbol(variableDeclarator, name, isMutable, type, functionValueTargetMethod, recordDeclaration: false)
                : CreateLocalSymbol(variableDeclarator, name, isMutable, type, isConst, constantValue, scopedKind, recordDeclaration: false);

            _declarationState.ReplaceDeclaredLocal(existingDeclaredLocal, localSymbol, variableDeclarator);
        }
        else if (existingDeclaredLocal is not null)
        {
            localSymbol = existingDeclaredLocal;
            RegisterLocalForCurrentLookup(name, localSymbol);
        }
        else
        {
            localSymbol = isFunctionValueAlias && functionValueTargetMethod is not null
                ? CreateFunctionValueSymbol(variableDeclarator, name, isMutable, type, functionValueTargetMethod)
                : CreateLocalSymbol(variableDeclarator, name, isMutable, type, isConst, constantValue, scopedKind);
        }

        var declarator = new BoundVariableDeclarator(localSymbol, boundInitializer, fixedAddressInitializer, fixedPinnedLocal);

        if (shouldDispose)
            _localsToDispose.Add((declarator.Local, _scopeDepth));

        CacheBoundNode(variableDeclarator, declarator);

        return declarator;
    }

    private static ScopedKind GetLocalScopedKind(
        VariableDeclarationSyntax declaration,
        ITypeSymbol type)
    {
        if (!declaration.ScopedKeyword.IsKind(SyntaxKind.ScopedKeyword))
            return ScopedKind.None;

        return type is RefTypeSymbol ? ScopedKind.ScopedRef : ScopedKind.ScopedValue;
    }

    private bool TryBindExistingLocalDeclaration(
        VariableDeclaratorSyntax variableDeclarator,
        string name,
        bool allowDeclaredOnly,
        bool allowCurrentLookupOnly,
        out BoundVariableDeclarator declarator)
    {
        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator cachedDeclarator &&
            TryGetDeclaredLocal(variableDeclarator, out var cachedDeclaredLocal) &&
            SymbolEqualityComparer.Default.Equals(cachedDeclarator.Local, cachedDeclaredLocal) &&
            IsCompleteLocalDeclarationType(cachedDeclarator.Local.Type))
        {
            RegisterLocalForCurrentLookup(name, cachedDeclarator.Local);
            declarator = cachedDeclarator;
            return true;
        }

        if (allowDeclaredOnly && TryGetDeclaredLocal(variableDeclarator, out var declaredLocal))
        {
            RegisterLocalForCurrentLookup(name, declaredLocal);
            declarator = new BoundVariableDeclarator(declaredLocal, null);
            return true;
        }

        if (allowCurrentLookupOnly &&
            TryGetSameLocalFromCurrentLookup(name, variableDeclarator, out var sameLocal) &&
            IsCompleteLocalDeclarationType(sameLocal.Type))
        {
            RegisterLocalForCurrentLookup(name, sameLocal);
            declarator = new BoundVariableDeclarator(sameLocal, null);
            return true;
        }

        declarator = null!;
        return false;
    }

    private bool TryGetSameLocalFromCurrentLookup(
        string name,
        VariableDeclaratorSyntax variableDeclarator,
        out ILocalSymbol local)
    {
        foreach (var symbol in LookupLocalDeclarationSymbols(name, includeTypeFields: false).OfType<ILocalSymbol>())
        {
            if (!IsSameDeclaringSyntax(symbol, variableDeclarator))
                continue;

            local = symbol;
            return true;
        }

        local = null!;
        return false;
    }

    private bool TryLookupDeclarationForShadowing(string name, out ISymbol symbol)
    {
        foreach (var candidate in LookupLocalDeclarationSymbols(name, includeTypeFields: true))
        {
            if (candidate is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            {
                symbol = candidate;
                return true;
            }
        }

        symbol = null!;
        return false;
    }

    private IEnumerable<ISymbol> LookupLocalDeclarationSymbols(string name, bool includeTypeFields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Binder? current = this;

        static string GetLookupKey(ISymbol symbol) => symbol.GetLookupIdentityKey();

        // Local declaration binding restores binder-owned state. It must not walk
        // imports or the global namespace, because semantic queries for method bodies
        // should not force source declaration completion.
        var restrict = IsStaticFunctionBody;
        var boundarySymbol = restrict ? _containingSymbol : null;
        var pastBoundary = false;

        while (current is not null)
        {
            if (restrict && !pastBoundary)
            {
                if (current is MethodBinder mb &&
                         !SymbolEqualityComparer.Default.Equals(mb.GetMethodSymbol(), boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is FunctionBinder fb &&
                         !SymbolEqualityComparer.Default.Equals(fb.GetMethodSymbol(), boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is MacroBinder boundaryMacroBinder &&
                         !SymbolEqualityComparer.Default.Equals(boundaryMacroBinder.GetMacroSymbol(), boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is TopLevelBinder or FunctionExpressionBinder)
                {
                    pastBoundary = true;
                }
            }

            var allowLocalsAndParams = !pastBoundary;

            if (allowLocalsAndParams && current is BlockBinder block)
            {
                if (block._locals.TryGetValue(name, out var local) && seen.Add(GetLookupKey(local.Symbol)))
                    yield return local.Symbol;
            }

            if (allowLocalsAndParams && current is TopLevelBinder topLevelBinder)
            {
                foreach (var param in topLevelBinder.GetParameters())
                {
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
                }
            }

            if (allowLocalsAndParams && current is MethodBinder methodBinder)
            {
                foreach (var param in methodBinder.GetMethodSymbol().Parameters)
                {
                    if (param.Name != "_" && param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
                }
            }

            if (allowLocalsAndParams && current is MacroBinder macroBinder)
            {
                var macro = macroBinder.GetMacroSymbol();
                foreach (var param in macro.Parameters)
                {
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
                }

                if (macro.TargetParameter is { } targetParameter &&
                    targetParameter.Name == name &&
                    seen.Add(GetLookupKey(targetParameter)))
                {
                    yield return targetParameter;
                }
            }

            if (allowLocalsAndParams && current is FunctionExpressionBinder lambdaBinder)
            {
                foreach (var param in lambdaBinder.GetParameters())
                {
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
                }
            }

            if (includeTypeFields && current is TypeMemberBinder typeMemberBinder)
            {
                foreach (var field in EnumerateTypeAndBaseMembers(typeMemberBinder.ContainingTypeSymbol, name).OfType<IFieldSymbol>())
                {
                    if (seen.Add(GetLookupKey(field)))
                        yield return field;
                }
            }

            current = current.ParentBinder;
        }
    }

    private static bool IsSameDeclaringSyntax(ISymbol symbol, VariableDeclaratorSyntax variableDeclarator)
        => symbol.DeclaringSyntaxReferences.Any(reference =>
            reference.SyntaxTree == variableDeclarator.SyntaxTree &&
            (reference.Span == variableDeclarator.Span ||
             reference.Span == variableDeclarator.Identifier.Span));

    private static bool IsCompleteLocalDeclarationType(ITypeSymbol type)
        => !type.ContainsErrorType() && !IsIncompleteGenericLocalType(type);

    private static bool ShouldUpgradeLocalDeclarationType(ITypeSymbol existingType, ITypeSymbol replacementType)
        => !replacementType.ContainsErrorType() &&
           !IsIncompleteGenericLocalType(replacementType) &&
           (existingType.ContainsErrorType() || IsIncompleteGenericLocalType(existingType));

    private static bool IsIncompleteGenericLocalType(ITypeSymbol type)
        => type is INamedTypeSymbol { Arity: > 0 } namedType &&
           (namedType.IsUnboundGenericType ||
            namedType.TypeArguments.IsDefaultOrEmpty ||
            namedType.TypeArguments.Length < namedType.TypeParameters.Length);

    private static BoundAddressOfExpression? TryGetFixedAddressInitializer(BoundExpression? expression)
    {
        while (expression is BoundConversionExpression conversion)
        {
            if (conversion.Expression is BoundAddressOfExpression addressOf)
                return addressOf;

            expression = conversion.Expression;
        }

        return expression as BoundAddressOfExpression;
    }

    private ILocalSymbol CreateSynthesizedPinnedLocal(VariableDeclaratorSyntax variableDeclarator, string name, ITypeSymbol type)
    {
        return new SourceLocalSymbol(
            $"<{name}>pinned",
            type,
            isMutable: true,
            _containingSymbol,
            _containingSymbol?.ContainingType as INamedTypeSymbol,
            _containingSymbol?.ContainingNamespace,
            [],
            [],
            isConst: false,
            constantValue: null);
    }

    private bool PrefersAsyncUseDisposal()
    {
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            switch (current.ContainingSymbol)
            {
                case ILambdaSymbol lambda:
                    return lambda.IsAsync;
                case IMethodSymbol method:
                    return method.IsAsync;
            }
        }

        return false;
    }

    protected ITypeSymbol EnsureTypeAccessible(ITypeSymbol type, Location location)
    {
        if (type.TypeKind == TypeKind.Error)
            return type;

        if (EnsureMemberAccessible(type, location, "type"))
            return type;

        return Compilation.ErrorTypeSymbol;
    }

    protected override bool EnsureMemberAccessible(ISymbol symbol, Location location, string symbolKind)
    {
        if (base.EnsureMemberAccessible(symbol, location, symbolKind))
            return true;

        var inaccessibleMemberDisplayFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithKindOptions(SymbolDisplayKindOptions.IncludeMemberKeyword)
            .WithPropertyStyle(SymbolDisplayPropertyStyle.NameOnly);

        var display = symbol switch
        {
            ITypeSymbol typeSymbol => typeSymbol.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IPropertySymbol propertySymbol => $"{(propertySymbol.IsMutable ? "var" : "val")} {propertySymbol.Name}: {propertySymbol.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}",
            _ => symbol.ToDisplayString(inaccessibleMemberDisplayFormat),
        };

        _diagnostics.ReportSymbolIsInaccessible(symbolKind, display, location);
        return false;
    }

    private ImmutableArray<IPropertySymbol> GetAccessibleProperties(
        ImmutableArray<IPropertySymbol> properties,
        Location location)
    {
        if (properties.IsDefaultOrEmpty)
            return properties;

        var builder = ImmutableArray.CreateBuilder<IPropertySymbol>();

        foreach (var property in properties)
        {
            if (IsSymbolAccessible(property))
                builder.Add(property);
        }

        if (builder.Count > 0)
            return builder.ToImmutable();

        EnsureMemberAccessible(properties[0], location, "property");
        return ImmutableArray<IPropertySymbol>.Empty;
    }

    private BoundExpression BindMethodGroup(BoundExpression? receiver, ImmutableArray<IMethodSymbol> methods, Location location)
    {
        var accessibleMethods = GetAccessibleMethods(DistinctMethodCandidates(methods), location);

        if (accessibleMethods.IsDefaultOrEmpty)
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

        return CreateMethodGroup(receiver, accessibleMethods);
    }

    private ImmutableArray<ITypeSymbol>? TryBindTypeArguments(GenericNameSyntax generic)
    {
        if (generic.TypeArgumentList.Arguments.Count == 0)
            return ImmutableArray<ITypeSymbol>.Empty;

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(generic.TypeArgumentList.Arguments.Count);

        foreach (var argument in generic.TypeArgumentList.Arguments)
        {
            var bound = BindTypeSyntaxAsExpression(argument.Type);
            if (bound is not BoundTypeExpression bt)
                return null;

            builder.Add(bt.Type);
        }

        return builder.ToImmutable();
    }

    private ImmutableArray<IMethodSymbol> InstantiateMethodCandidates(
        ImmutableArray<IMethodSymbol> methods,
        ImmutableArray<ITypeSymbol> typeArguments,
        GenericNameSyntax? typeArgumentSyntax = null,
        Location? fallbackLocation = null)
    {
        if (methods.IsDefaultOrEmpty)
            return methods;

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();

        Location GetArgumentLocation(int index)
        {
            if (typeArgumentSyntax is not null)
            {
                var arguments = typeArgumentSyntax.TypeArgumentList.Arguments;
                if (index >= 0 && index < arguments.Count)
                    return arguments[index].GetLocation();
                return typeArgumentSyntax.GetLocation();
            }

            return fallbackLocation ?? Location.None;
        }

        foreach (var method in methods)
        {
            if (method.TypeParameters.Length != typeArguments.Length)
                continue;

            if (!ValidateMethodTypeArgumentConstraints(method, typeArguments, GetArgumentLocation))
                continue;

            try
            {
                var constructed = method.Construct(typeArguments.ToArray());
                builder.Add(constructed);
            }
            catch
            {
                // Ignore methods that cannot be constructed with the provided type arguments.
            }
        }

        return DistinctMethodCandidates(builder.ToImmutable());
    }

    private static ImmutableArray<IMethodSymbol> DistinctMethodCandidates(ImmutableArray<IMethodSymbol> methods)
    {
        if (methods.IsDefaultOrEmpty || methods.Length == 1)
            return methods;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>(methods.Length);

        foreach (var method in methods)
        {
            if (!seen.Add(method.GetLookupIdentityKey()))
                continue;

            builder.Add(method);
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<IMethodSymbol> FilterStaleSignatureSkeletonMethods(ImmutableArray<IMethodSymbol> methods)
    {
        if (methods.IsDefaultOrEmpty || methods.Length == 1)
            return methods;

        var completedSignatures = methods
            .Where(static method => method is not SourceMethodSymbol { IsSignatureSkeleton: true })
            .Select(GetParameterOnlyMethodSignatureKey)
            .ToHashSet(StringComparer.Ordinal);

        if (completedSignatures.Count == 0)
            return methods;

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>(methods.Length);
        foreach (var method in methods)
        {
            if (method is SourceMethodSymbol { IsSignatureSkeleton: true } &&
                completedSignatures.Contains(GetParameterOnlyMethodSignatureKey(method)))
            {
                continue;
            }

            builder.Add(method);
        }

        return builder.Count == methods.Length ? methods : builder.ToImmutable();
    }

    private static string GetParameterOnlyMethodSignatureKey(IMethodSymbol method)
        => $"{method.Name}({string.Join(",", method.Parameters.Select(static parameter => $"{parameter.RefKind}:{(parameter.IsVarParams ? "params " : string.Empty)}{parameter.Type.ToDisplayString()}"))})";

    private static string GetSymbolKindForDiagnostic(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => "constructor",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamedTypeSymbol => "type",
            _ => "member"
        };
    }

    private SourceLocalSymbol CreateLocalSymbol(
        SyntaxNode declaringSyntax,
        string name,
        bool isMutable,
        ITypeSymbol type,
        bool isConst = false,
        object? constantValue = null,
        ScopedKind scopedKind = ScopedKind.None,
        bool recordDeclaration = true)
    {
        var location = declaringSyntax is VariableDeclaratorSyntax variableDeclarator
            ? variableDeclarator.Identifier.GetLocation()
            : declaringSyntax.GetLocation();
        var syntaxReferences = GetDeclaringSyntaxReferences(declaringSyntax);

        var symbol = new SourceLocalSymbol(
            name,
            type,
            isMutable,
            _containingSymbol,
            _containingSymbol?.ContainingType as INamedTypeSymbol,
            _containingSymbol?.ContainingNamespace,
            [location],
            syntaxReferences,
            isConst,
            constantValue,
            scopedKind);

        RegisterLocalForCurrentLookup(name, symbol);
        if (recordDeclaration)
            OnLocalDeclared(symbol, declaringSyntax);
        return symbol;
    }

    private void RegisterLocalForCurrentLookup(string name, ILocalSymbol local)
        => _locals[name] = (local, _scopeDepth);

    private bool IsValidUseDeclarationTarget(ITypeSymbol type, bool preferAsync)
        => !type.IsNullable &&
           UseDisposalUtilities.SupportsUseDisposal(Compilation, type, preferAsync);

    protected virtual void OnLocalDeclared(ILocalSymbol local, SyntaxNode declaringSyntax)
        => _declarationState.AddDeclaredLocal(local, declaringSyntax);

    internal ImmutableArray<ILocalSymbol> GetDeclaredLocalsForTesting()
        => _declarationState.DeclaredLocals;

    internal ILocalSymbol? GetLocalForTesting(string name)
        => _declarationState.GetLocal(name);

    internal string? GetLocalScopeKeyForTesting(ILocalSymbol local)
        => _declarationState.TryGetScope(local, out var scope)
            ? scope.ToDisplayString()
            : null;

    internal int LocalStateVersionForTesting => _declarationState.Version;

    internal int StatementDeclarationProgressCountForTesting => _statementDeclarationProgress.Count;

    internal int ActiveLocalLookupCountForTesting => _locals.Count;

    private bool TryGetDeclaredLocal(SyntaxNode declaringSyntax, out ILocalSymbol local)
        => _declarationState.TryGetDeclaredLocal(declaringSyntax, out local);

    internal bool TryGetDeclaredLocalForSemanticQuery(SyntaxNode declaringSyntax, out ILocalSymbol local)
        => TryGetDeclaredLocal(declaringSyntax, out local);

    private static bool TryGetSyntaxReferenceKey(ISymbol symbol, out SyntaxReferenceKey key)
    {
        var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is not null)
        {
            key = new SyntaxReferenceKey(reference.SyntaxTree, reference.Span);
            return true;
        }

        key = default;
        return false;
    }

    private static bool TryGetSyntaxReferenceKey(SyntaxNode node, out SyntaxReferenceKey key)
    {
        if (node is VariableDeclaratorSyntax variableDeclarator)
        {
            key = new SyntaxReferenceKey(variableDeclarator.SyntaxTree, variableDeclarator.Identifier.Span);
            return true;
        }

        if (node.SyntaxTree is not null)
        {
            key = new SyntaxReferenceKey(node.SyntaxTree, node.Span);
            return true;
        }

        key = default;
        return false;
    }

    private readonly record struct SyntaxReferenceKey(SyntaxTree SyntaxTree, TextSpan Span);

    private readonly record struct LexicalScopeKey(SyntaxKind Kind, SyntaxReferenceKey Reference)
    {
        public string ToDisplayString()
            => $"{Kind}@{Reference.Span.Start}:{Reference.Span.Length}";
    }

    private sealed class BlockDeclarationState
    {
        private readonly record struct DeclaredLocalEntry(ILocalSymbol Local, LexicalScopeKey Scope);

        private readonly List<ILocalSymbol> _declaredLocals = new();
        private readonly Dictionary<ILocalSymbol, LexicalScopeKey> _scopesByLocal = new(SymbolReferenceComparer<ILocalSymbol>.Instance);
        private readonly Dictionary<SyntaxReferenceKey, ILocalSymbol> _declaredLocalsBySyntax = new();
        private readonly Dictionary<string, List<DeclaredLocalEntry>> _declaredLocalsByName = new(StringComparer.Ordinal);

        public int Version { get; private set; }

        public ImmutableArray<ILocalSymbol> DeclaredLocals => _declaredLocals.ToImmutableArray();

        public void AddDeclaredLocal(ILocalSymbol local, SyntaxNode declaringSyntax)
        {
            if (TryGetSyntaxReferenceKey(local, out var key) &&
                _declaredLocalsBySyntax.TryGetValue(key, out var existingLocal))
            {
                if (!ReferenceEquals(existingLocal, local))
                    ReplaceDeclaredLocalForSameSyntax(existingLocal, local, declaringSyntax, key);

                return;
            }

            var scope = GetLexicalScopeKey(declaringSyntax);
            _declaredLocals.Add(local);
            _scopesByLocal[local] = scope;
            if (TryGetSyntaxReferenceKey(local, out key))
                _declaredLocalsBySyntax[key] = local;

            if (!_declaredLocalsByName.TryGetValue(local.Name, out var localsByName))
            {
                localsByName = new List<DeclaredLocalEntry>();
                _declaredLocalsByName[local.Name] = localsByName;
            }

            localsByName.Add(new DeclaredLocalEntry(local, scope));
            Version++;
        }

        private void ReplaceDeclaredLocalForSameSyntax(
            ILocalSymbol oldLocal,
            ILocalSymbol newLocal,
            SyntaxNode declaringSyntax,
            SyntaxReferenceKey key)
        {
            var scope = GetLexicalScopeKey(declaringSyntax);
            for (var i = _declaredLocals.Count - 1; i >= 0; i--)
            {
                if (IsSameDeclaredLocalEntry(_declaredLocals[i], oldLocal, key))
                    _declaredLocals.RemoveAt(i);
            }

            _scopesByLocal.Remove(oldLocal);
            foreach (var existingLocal in _scopesByLocal.Keys.Where(local => IsSameDeclaredLocalEntry(local, oldLocal, key)).ToArray())
                _scopesByLocal.Remove(existingLocal);

            _scopesByLocal[newLocal] = scope;
            _declaredLocalsBySyntax[key] = newLocal;

            if (_declaredLocalsByName.TryGetValue(oldLocal.Name, out var oldNameEntries))
            {
                for (var i = oldNameEntries.Count - 1; i >= 0; i--)
                {
                    if (IsSameDeclaredLocalEntry(oldNameEntries[i].Local, oldLocal, key))
                        oldNameEntries.RemoveAt(i);
                }
            }

            if (!_declaredLocalsByName.TryGetValue(newLocal.Name, out var newNameEntries))
            {
                newNameEntries = new List<DeclaredLocalEntry>();
                _declaredLocalsByName[newLocal.Name] = newNameEntries;
            }

            _declaredLocals.Add(newLocal);
            newNameEntries.Add(new DeclaredLocalEntry(newLocal, scope));
        }

        private static bool IsSameDeclaredLocalEntry(
            ILocalSymbol local,
            ILocalSymbol expectedLocal,
            SyntaxReferenceKey expectedKey)
        {
            if (ReferenceEquals(local, expectedLocal))
            {
                return true;
            }

            if (TryGetSyntaxReferenceKey(local, out var key) && key.Equals(expectedKey))
                return true;

            return SymbolEqualityComparer.Default.Equals(local, expectedLocal);
        }

        public void ReplaceDeclaredLocal(ILocalSymbol oldLocal, ILocalSymbol newLocal, SyntaxNode declaringSyntax)
        {
            var scope = GetLexicalScopeKey(declaringSyntax);
            for (var i = 0; i < _declaredLocals.Count; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(_declaredLocals[i], oldLocal))
                {
                    _declaredLocals[i] = newLocal;
                    break;
                }
            }

            _scopesByLocal.Remove(oldLocal);
            _scopesByLocal[newLocal] = scope;

            if (TryGetSyntaxReferenceKey(declaringSyntax, out var key))
                _declaredLocalsBySyntax[key] = newLocal;

            if (_declaredLocalsByName.TryGetValue(oldLocal.Name, out var oldNameEntries))
            {
                for (var i = oldNameEntries.Count - 1; i >= 0; i--)
                {
                    if (SymbolEqualityComparer.Default.Equals(oldNameEntries[i].Local, oldLocal))
                    {
                        oldNameEntries.RemoveAt(i);
                        break;
                    }
                }
            }

            if (!_declaredLocalsByName.TryGetValue(newLocal.Name, out var newNameEntries))
            {
                newNameEntries = new List<DeclaredLocalEntry>();
                _declaredLocalsByName[newLocal.Name] = newNameEntries;
            }

            newNameEntries.Add(new DeclaredLocalEntry(newLocal, scope));
            Version++;
        }

        public ILocalSymbol? GetLocal(string name)
            => _declaredLocalsByName.TryGetValue(name, out var locals) && locals.Count > 0
                ? locals[^1].Local
                : null;

        public bool TryGetScope(ILocalSymbol local, out LexicalScopeKey scope)
        {
            if (_scopesByLocal.TryGetValue(local, out scope))
                return true;

            if (TryGetSyntaxReferenceKey(local, out var key) &&
                _declaredLocalsBySyntax.TryGetValue(key, out var declaredLocal))
            {
                return _scopesByLocal.TryGetValue(declaredLocal, out scope);
            }

            scope = default;
            return false;
        }

        public bool TryGetDeclaredLocalInSameScope(string name, SyntaxNode declaringSyntax, out ILocalSymbol local)
        {
            if (_declaredLocalsByName.TryGetValue(name, out var locals))
            {
                var scope = GetLexicalScopeKey(declaringSyntax);
                for (var i = locals.Count - 1; i >= 0; i--)
                {
                    if (!locals[i].Scope.Equals(scope))
                        continue;

                    local = locals[i].Local;
                    return true;
                }
            }

            local = null!;
            return false;
        }

        public bool TryGetDeclaredLocal(SyntaxNode declaringSyntax, out ILocalSymbol local)
        {
            if (TryGetSyntaxReferenceKey(declaringSyntax, out var key) &&
                _declaredLocalsBySyntax.TryGetValue(key, out local!))
            {
                return true;
            }

            local = null!;
            return false;
        }

        private static LexicalScopeKey GetLexicalScopeKey(SyntaxNode declaringSyntax)
        {
            var scopeNode = GetLexicalScopeNode(declaringSyntax);
            return TryGetSyntaxReferenceKey(scopeNode, out var key)
                ? new LexicalScopeKey(scopeNode.Kind, key)
                : default;
        }

        private static SyntaxNode GetLexicalScopeNode(SyntaxNode declaringSyntax)
        {
            if (declaringSyntax is SingleVariableDesignationSyntax designation &&
                TryGetPatternDeclarationOwner(
                    designation,
                    PatternDeclarationOwnerPurpose.LexicalScope,
                    out var patternOwner))
            {
                return patternOwner;
            }

            if (declaringSyntax.AncestorsAndSelf()
                .FirstOrDefault(static ancestor => ancestor is BlockSyntax or BlockStatementSyntax) is { } block)
            {
                return block;
            }

            if (declaringSyntax.AncestorsAndSelf().OfType<CompilationUnitSyntax>().FirstOrDefault() is { } compilationUnit)
                return compilationUnit;

            return declaringSyntax.Parent ?? declaringSyntax;
        }
    }

    private sealed class StatementDeclarationProgress
    {
        private readonly Dictionary<SyntaxReferenceKey, int> _seededThroughByStatementParent = new();

        public int Count => _seededThroughByStatementParent.Count;

        public int GetSeededThrough(SyntaxNode statementParent)
            => TryGetSyntaxReferenceKey(statementParent, out var key) &&
               _seededThroughByStatementParent.TryGetValue(key, out var seededThrough)
                ? seededThrough
                : -1;

        public void MarkSeededThrough(SyntaxNode statementParent, int statementStart)
        {
            if (!TryGetSyntaxReferenceKey(statementParent, out var key))
                return;

            if (!_seededThroughByStatementParent.TryGetValue(key, out var existing) ||
                statementStart > existing)
            {
                _seededThroughByStatementParent[key] = statementStart;
            }
        }

        public Dictionary<SyntaxReferenceKey, int> CreateSnapshot()
            => new(_seededThroughByStatementParent);

        public void Restore(Dictionary<SyntaxReferenceKey, int> snapshot)
        {
            _seededThroughByStatementParent.Clear();
            foreach (var entry in snapshot)
                _seededThroughByStatementParent[entry.Key] = entry.Value;
        }
    }

    private SourceFunctionValueSymbol CreateFunctionValueSymbol(
        SyntaxNode declaringSyntax,
        string name,
        bool isMutable,
        ITypeSymbol type,
        IMethodSymbol targetMethod,
        bool recordDeclaration = true)
    {
        var location = declaringSyntax is VariableDeclaratorSyntax variableDeclarator
            ? variableDeclarator.Identifier.GetLocation()
            : declaringSyntax.GetLocation();
        SyntaxReference? syntaxReference = null;
        if (declaringSyntax.SyntaxTree is { } syntaxTree)
        {
            syntaxReference = declaringSyntax is VariableDeclaratorSyntax variableDeclaratorReference
                ? new SyntaxReference(syntaxTree, variableDeclaratorReference.Identifier.Span)
                : declaringSyntax.GetReference();
        }

        var symbol = new SourceFunctionValueSymbol(
            name,
            type,
            isMutable,
            _containingSymbol,
            _containingSymbol?.ContainingType as INamedTypeSymbol,
            _containingSymbol?.ContainingNamespace,
            [location],
            syntaxReference is null ? [] : [syntaxReference],
            targetMethod);

        RegisterLocalForCurrentLookup(name, symbol);
        if (recordDeclaration)
            OnLocalDeclared(symbol, declaringSyntax);
        return symbol;
    }

    private SourceLocalSymbol CreateTempLocal(string nameHint, ITypeSymbol type, SyntaxNode syntax)
    {
        var containingType = _containingSymbol.ContainingType as INamedTypeSymbol;
        var containingNamespace = _containingSymbol.ContainingNamespace;
        var name = $"<{nameHint}>__{_tempCounter++}";
        var location = syntax.GetLocation() ?? Location.None;
        var syntaxReferences = GetDeclaringSyntaxReferences(syntax);

        return new SourceLocalSymbol(
            name,
            type,
            isMutable: true,
            _containingSymbol,
            containingType,
            containingNamespace,
            [location],
            syntaxReferences,
            isImplicitlyDeclared: true);
    }

    private static SyntaxReference[] GetDeclaringSyntaxReferences(SyntaxNode syntax)
        => syntax.SyntaxTree is null ? [] : [syntax.GetReference()];

    public override BoundStatement BindStatement(StatementSyntax statement)
    {
        SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
        using var _ = EnterExecutionScope();

        if (TryGetCachedBoundNode(statement) is BoundStatement cached)
        {
            RegisterCachedStatementDeclarations(cached);
            if (statement is PatternDeclarationAssignmentStatementSyntax { ElseClause: not null } &&
                cached is BoundIfStatement { Condition: BoundIsPatternExpression patternCondition })
            {
                RegisterPatternLocalsForCurrentLookup(patternCondition.Pattern);
            }
            return cached;
        }

        BoundStatement boundNode = statement switch
        {
            LocalDeclarationStatementSyntax localDeclaration => BindLocalDeclarationStatement(localDeclaration),
            UseDeclarationStatementSyntax useDeclaration => BindUseDeclarationStatement(useDeclaration),
            AssignmentStatementSyntax assignmentStatement => BindAssignmentStatement(assignmentStatement),
            PatternDeclarationAssignmentStatementSyntax patternDeclarationAssignment => BindPatternDeclarationAssignmentStatement(patternDeclarationAssignment),
            MatchStatementSyntax matchStatement => BindMatchStatement(matchStatement),
            ExpressionStatementSyntax expressionStmt => BindExpressionStatement(expressionStmt),
            IfStatementSyntax ifStmt => BindIfStatement(ifStmt),
            IfPatternStatementSyntax ifPatternStmt => BindIfPatternStatement(ifPatternStmt),
            WhileStatementSyntax whileStmt => BindWhileStatement(whileStmt),
            WhilePatternStatementSyntax whilePatternStmt => BindWhilePatternStatement(whilePatternStmt),
            LoopStatementSyntax loopStmt => BindLoopStatement(loopStmt),
            LockStatementSyntax lockStmt => BindLockStatement(lockStmt),
            TryStatementSyntax tryStmt => BindTryStatement(tryStmt),
            FunctionStatementSyntax function => BindFunction(function),
            TypeDeclarationStatementSyntax typeDeclarationStatement => BindTypeDeclarationStatement(typeDeclarationStatement),
            ReturnStatementSyntax returnStatement => BindReturnStatement(returnStatement),
            ThrowStatementSyntax throwStatement => BindThrowStatement(throwStatement),
            BlockStatementSyntax blockStmt => BindBlockStatement(blockStmt),
            UnsafeStatementSyntax unsafeStatement => BindUnsafeStatement(unsafeStatement),
            ForStatementSyntax forStmt => BindForStatement(forStmt),
            LabeledStatementSyntax labeledStatement => BindLabeledStatement(labeledStatement),
            GotoStatementSyntax gotoStatement => BindGotoStatement(gotoStatement),
            BreakStatementSyntax breakStatement => BindBreakStatement(breakStatement),
            ContinueStatementSyntax continueStatement => BindContinueStatement(continueStatement),
            YieldStatementSyntax yieldStatement => BindYieldStatement(yieldStatement),
            EmptyStatementSyntax emptyStatement => new BoundExpressionStatement(BoundFactory.UnitExpression()),
            IncompleteStatementSyntax incompleteStatement => BindIncompleteStatement(incompleteStatement),
            _ => throw new NotSupportedException($"Unsupported statement: {statement.Kind}")
        };

        CacheBoundNode(statement, boundNode);

        return boundNode;
    }

    private void RegisterCachedStatementDeclarations(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundLocalDeclarationStatement localDeclaration:
                foreach (var declarator in localDeclaration.Declarators)
                {
                    RegisterLocalForCurrentLookup(declarator.Local.Name, declarator.Local);

                    if (localDeclaration.IsUsing &&
                        declarator.FixedAddressInitializer is null &&
                        IsValidUseDeclarationTarget(declarator.Local.Type, PrefersAsyncUseDisposal()))
                    {
                        RegisterLocalToDisposeForCurrentScope(declarator.Local);
                    }
                }
                break;

            case BoundAssignmentStatement { Expression: BoundPatternAssignmentExpression patternAssignment }:
                RegisterPatternLocalsForCurrentLookup(patternAssignment.Pattern);
                break;
        }
    }

    private void RegisterPatternLocalsForCurrentLookup(BoundPattern pattern)
    {
        foreach (var local in CollectPatternDesignatorLocals(pattern))
            RegisterLocalForCurrentLookup(local.Name, local);
    }

    private void RegisterLocalToDisposeForCurrentScope(ILocalSymbol local)
    {
        if (_localsToDispose.Any(entry =>
                entry.Depth == _scopeDepth &&
                SymbolEqualityComparer.Default.Equals(entry.Local, local)))
        {
            return;
        }

        _localsToDispose.Add((local, _scopeDepth));
    }

    private BoundStatement BindTypeDeclarationStatement(TypeDeclarationStatementSyntax typeDeclarationStatement)
    {
        _ = SemanticModel.EnsureLocalTypeDeclarationBound(typeDeclarationStatement.Declaration, this);
        return new BoundExpressionStatement(BoundFactory.UnitExpression());
    }

    private BoundStatement BindIncompleteStatement(IncompleteStatementSyntax incompleteStatement)
    {
        var errorExpression = ErrorExpression(Compilation.ErrorTypeSymbol, reason: BoundExpressionReason.OtherError);
        return new BoundExpressionStatement(errorExpression);
    }

    private BoundStatement BindUnsafeStatement(UnsafeStatementSyntax unsafeStatement)
    {
        _unsafeBlockDepth++;
        try
        {
            return BindBlockStatement(unsafeStatement.Block);
        }
        finally
        {
            _unsafeBlockDepth--;
        }
    }

    private BoundExpression BindUnsafeExpression(UnsafeExpressionSyntax unsafeExpression)
    {
        _unsafeBlockDepth++;
        try
        {
            return BindBlock(unsafeExpression.Block, allowReturn: _allowReturnsInExpression || _allowReturnsInBlockExpressionsOnly);
        }
        finally
        {
            _unsafeBlockDepth--;
        }
    }

    private BoundStatement BindLocalDeclarationStatement(LocalDeclarationStatementSyntax localDeclaration)
    {
        var declaration = localDeclaration.Declaration;

        if ((declaration.BindingKeyword.IsKind(SyntaxKind.LetKeyword) || declaration.BindingKeyword.IsKind(SyntaxKind.ValKeyword)) &&
            declaration.Declarators.Count > 0 &&
            IsDiscardDeclarator(declaration.Declarators[0]))
        {
            return BindDiscardDeclarator(declaration.Declarators[0], isUseDeclaration: false);
        }

        var boundDeclarators = ImmutableArray.CreateBuilder<BoundVariableDeclarator>(declaration.Declarators.Count);

        foreach (var declarator in declaration.Declarators)
        {
            boundDeclarators.Add(BindLocalDeclaration(declarator));
        }

        return new BoundLocalDeclarationStatement(boundDeclarators.ToImmutable(), isUsing: false);
    }

    private BoundStatement BindUseDeclarationStatement(UseDeclarationStatementSyntax useDeclaration)
    {
        if (useDeclaration.InBlockClause is not null)
            return BindUseInClauseStatement(useDeclaration);

        return BindUseDeclarationCore(useDeclaration.Declaration);
    }

    private BoundBlockStatement BindUseInClauseStatement(UseDeclarationStatementSyntax useDeclaration)
    {
        _scopeDepth++;
        var depth = _scopeDepth;
        var functionScope = CaptureFunctionScope();

        try
        {
            var boundUse = BindUseDeclarationCore(useDeclaration.Declaration);

            var inBlock = useDeclaration.InBlockClause!.Block;

            EnsureLabelsDeclared(inBlock);

            foreach (var stmt in inBlock.Statements)
            {
                if (stmt is FunctionStatementSyntax func)
                {
                    var functionBinder = SemanticModel.GetBinder(func, this);
                    if (functionBinder is FunctionBinder lfBinder)
                    {
                        var symbol = lfBinder.GetMethodSymbol();
                        if (HasFunctionInCurrentScope(functionScope, symbol))
                            _diagnostics.ReportFunctionAlreadyDefined(symbol.Name, func.Identifier.GetLocation());
                        else
                            AddFunctionToScope(symbol);
                    }
                }
            }

            var boundStatements = new List<BoundStatement>(inBlock.Statements.Count + 1) { boundUse };
            foreach (var stmt in inBlock.Statements)
                boundStatements.Add(BindStatement(stmt));

            var localsAtDepth = _localsToDispose
                .Where(l => l.Depth == depth)
                .Select(l => l.Local)
                .ToList();

            if (localsAtDepth.Count > 0)
                _localsToDispose.RemoveAll(l => l.Depth == depth);

            var boundBlock = new BoundBlockStatement(boundStatements.ToArray(), localsAtDepth.ToImmutableArray());
            CacheBoundNode(useDeclaration, boundBlock);

            foreach (var name in _locals.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _locals.Remove(name);

            return boundBlock;
        }
        finally
        {
            RestoreFunctionScope(functionScope);
            _scopeDepth--;
        }
    }

    private BoundStatement BindUseDeclarationCore(VariableDeclarationSyntax declaration)
    {
        if (declaration.Declarators.Count > 0 &&
            IsDiscardDeclarator(declaration.Declarators[0]))
        {
            return BindDiscardDeclarator(declaration.Declarators[0], isUseDeclaration: true);
        }

        var boundDeclarators = ImmutableArray.CreateBuilder<BoundVariableDeclarator>(declaration.Declarators.Count);

        foreach (var declarator in declaration.Declarators)
            boundDeclarators.Add(BindLocalDeclaration(declarator));

        return new BoundLocalDeclarationStatement(boundDeclarators.ToImmutable(), isUsing: true);
    }

    private static bool IsDiscardDeclarator(VariableDeclaratorSyntax variableDeclarator)
    {
        return !variableDeclarator.Identifier.IsMissing &&
               variableDeclarator.Identifier.ValueText == "_";
    }

    private BoundStatement BindDiscardDeclarator(
        VariableDeclaratorSyntax variableDeclarator,
        bool isUseDeclaration)
    {
        var initializer = variableDeclarator.Initializer;

        if (initializer is null)
        {
            _diagnostics.ReportLocalVariableMustBeInitialized("_", variableDeclarator.Identifier.GetLocation());
            return new BoundExpressionStatement(BoundFactory.UnitExpression());
        }

        var boundInitializer = BindExpression(
            initializer.Value,
            allowReturn: false,
            allowReturnInBlockExpressionsOnly: true);
        boundInitializer = BindImplicitParameterlessConstructionIfNeeded(boundInitializer, initializer.Value);

        if (variableDeclarator.TypeAnnotation is not null)
        {
            var annotatedType = ResolveTypeSyntaxOrError(variableDeclarator.TypeAnnotation.Type);
            annotatedType = EnsureTypeAccessible(annotatedType, variableDeclarator.TypeAnnotation.Type.GetLocation());
            annotatedType = EnsureTypeValidForStorageLocation(annotatedType, variableDeclarator.TypeAnnotation.Type.GetLocation());

            if (annotatedType.TypeKind != TypeKind.Error && ShouldAttemptConversion(boundInitializer))
            {
                if (!IsAssignable(annotatedType, boundInitializer.Type!, out var conversion))
                {
                    ReportCannotAssignExpressionToType(boundInitializer, annotatedType, initializer.Value.GetLocation());
                    boundInitializer = new BoundErrorExpression(annotatedType, null, BoundExpressionReason.TypeMismatch);
                }
                else
                {
                    boundInitializer = ApplyConversion(boundInitializer, annotatedType, conversion, initializer.Value);
                }
            }
        }

        CacheBoundNode(initializer.Value, boundInitializer);

        if (isUseDeclaration)
        {
            _diagnostics.ReportDiscardExpressionNotAllowed(variableDeclarator.Identifier.GetLocation());
        }

        return new BoundExpressionStatement(boundInitializer);
    }

    private BoundExpression BindImplicitParameterlessConstructionIfNeeded(BoundExpression expression, ExpressionSyntax syntax)
    {
        if (expression is not BoundTypeExpression typeExpression)
            return expression;

        if (typeExpression.Type is NullTypeSymbol)
            return expression;

        _diagnostics.ReportInvalidInvocation(syntax.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
    }

    public BoundExpression BindExpression(ExpressionSyntax syntax, bool allowReturn, bool allowReturnInBlockExpressionsOnly = false)
    {
        var previousAllowReturn = _allowReturnsInExpression;
        var previousAllowReturnInBlocksOnly = _allowReturnsInBlockExpressionsOnly;
        _allowReturnsInExpression = allowReturn;
        _allowReturnsInBlockExpressionsOnly = allowReturnInBlockExpressionsOnly;
        try
        {
            return BindExpression(syntax);
        }
        finally
        {
            _allowReturnsInExpression = previousAllowReturn;
            _allowReturnsInBlockExpressionsOnly = previousAllowReturnInBlocksOnly;
        }
    }

    public TargetTypeScope PushTargetType(ITypeSymbol? targetType)
        => PushTargetTypeCore(new TargetTypeFrame(targetType, Root: null, AppliesToDescendants: true));

    internal TargetTypeScope PushTargetType(ITypeSymbol? targetType, ExpressionSyntax root)
        => PushTargetTypeCore(new TargetTypeFrame(targetType, root, AppliesToDescendants: false));

    private TargetTypeScope PushTargetTypeCore(TargetTypeFrame frame)
    {
        Monitor.Enter(_executionGate);
        var depthBeforePush = _targetTypeStack.Count;
        _targetTypeStack.Push(frame);
        return new TargetTypeScope(this, depthBeforePush);
    }

    private BoundExpression BindExpressionWithTargetType(
        ExpressionSyntax syntax,
        ITypeSymbol? targetType,
        bool allowReturn = true,
        bool allowReturnInBlockExpressionsOnly = false)
    {
        if (targetType is null)
            return BindExpression(syntax, allowReturn, allowReturnInBlockExpressionsOnly);

        using var _ = PushTargetType(targetType, syntax);
        return BindExpression(syntax, allowReturn, allowReturnInBlockExpressionsOnly);
    }

    internal BoundExpression BindExpressionWithTargetTypeForSemanticQuery(
        ExpressionSyntax syntax,
        ITypeSymbol targetType)
    {
        var expression = BindExpressionWithTargetType(syntax, targetType);
        if (expression is BoundMethodGroupExpression methodGroup)
            return ConvertMethodGroupToDelegate(methodGroup, targetType, syntax);

        return IsAssignable(targetType, expression, out var conversion)
            ? ApplyConversion(expression, targetType, conversion, syntax)
            : expression;
    }

    private BoundExpression BindExpressionWithoutTargetType(
        ExpressionSyntax syntax,
        bool allowReturn = true,
        bool allowReturnInBlockExpressionsOnly = false)
    {
        using var _ = PushTargetType(null);
        return BindExpression(syntax, allowReturn, allowReturnInBlockExpressionsOnly);
    }

    public override BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        SemanticModel.ThrowIfDiagnosticBindingCancellationRequested();
        using var _ = EnterExecutionScope();

        if (syntax is FreestandingMacroExpressionSyntax macroExpressionForScope)
            OnFreestandingMacroExpressionBinding(macroExpressionForScope);

        var activeTargetType = GetScopedTargetType(syntax);
        var useContextualCache = activeTargetType is not null;
        var skipCache = syntax is CollectionExpressionSyntax or ArrayExpressionSyntax or FunctionExpressionSyntax;

        if (useContextualCache && TryGetCachedBoundNode(syntax, activeTargetType) is BoundExpression contextualCached)
            return contextualCached;

        if (!skipCache && !useContextualCache && TryGetCachedBoundNode(syntax) is BoundExpression cached)
            return cached;

        var boundNode = syntax switch
        {
            LiteralExpressionSyntax literal => BindLiteralExpression(literal),
            IdentifierNameSyntax identifier => BindIdentifierName(identifier),
            TypeSyntax type => BindTypeSyntaxAsExpression(type),
            InfixOperatorExpressionSyntax binary => BindBinaryExpression(binary),
            NullCoalesceExpressionSyntax coalesce => BindNullCoalesceExpression(coalesce),
            RangeExpressionSyntax rangeExpression => BindRangeExpression(rangeExpression),
            InvocationExpressionSyntax invocation => BindInvocationExpression(invocation),
            WithExpressionSyntax withExpression => BindWithExpression(withExpression),
            MemberAccessExpressionSyntax memberAccess => BindMemberAccessExpression(memberAccess),
            MemberBindingExpressionSyntax memberBinding => BindMemberBindingExpression(memberBinding),
            ConditionalAccessExpressionSyntax conditionalAccess => BindConditionalAccessExpression(conditionalAccess),
            ElementAccessExpressionSyntax elementAccess => BindElementAccessExpression(elementAccess),
            AssignmentExpressionSyntax assignment => BindAssignmentExpression(assignment),
            CollectionExpressionSyntax collection => BindCollectionExpression(collection),
            ArrayExpressionSyntax arrayExpression => BindArrayExpression(arrayExpression),
            ObjectInitializerExpressionSyntax objectInitializer => BindObjectInitializerExpressionForSemanticQuery(objectInitializer),
            ParenthesizedExpressionSyntax parenthesizedExpression => BindParenthesizedExpression(parenthesizedExpression),
            CastExpressionSyntax castExpression => BindConversionExpression(castExpression),
            AsExpressionSyntax asExpression => BindAsExpression(asExpression),
            DefaultExpressionSyntax defaultExpression => BindDefaultExpression(defaultExpression),
            TypeOfExpressionSyntax typeOfExpression => BindTypeOfExpression(typeOfExpression),
            SizeOfExpressionSyntax sizeOfExpression => BindSizeOfExpression(sizeOfExpression),
            StackAllocExpressionSyntax stackAllocExpression => BindStackAllocExpression(stackAllocExpression),
            NameOfExpressionSyntax nameOfExpression => BindNameOfExpression(nameOfExpression),
            TupleExpressionSyntax tupleExpression => BindTupleExpression(tupleExpression),
            IfExpressionSyntax ifExpression => BindIfExpression(ifExpression),
            IfPatternExpressionSyntax ifPatternExpression => BindIfPatternExpression(ifPatternExpression),
            BlockSyntax block => BindBlock(block, allowReturn: _allowReturnsInExpression || _allowReturnsInBlockExpressionsOnly),
            UnsafeExpressionSyntax unsafeExpression => BindUnsafeExpression(unsafeExpression),
            IsPatternExpressionSyntax isPatternExpression => BindIsPatternExpression(isPatternExpression),
            MatchExpressionSyntax matchExpression => BindMatchExpression(
                matchExpression,
                matchExpression.Expression,
                matchExpression.Arms),
            PostfixMatchExpressionSyntax matchExpression => BindMatchExpression(
                matchExpression,
                matchExpression.Expression,
                matchExpression.Arms),
            TryExpressionSyntax tryExpression => BindTryExpression(tryExpression),
            ReturnExpressionSyntax returnExpression => BindReturnExpression(returnExpression),
            ThrowExpressionSyntax throwExpression => BindThrowExpression(throwExpression),
            BreakExpressionSyntax breakExpression => BindBreakExpression(breakExpression),
            ContinueExpressionSyntax continueExpression => BindContinueExpression(continueExpression),
            YieldExpressionSyntax yieldExpression => BindYieldExpression(yieldExpression),
            PropagateExpressionSyntax propagateExpression => BindPropagateExpression(propagateExpression),
            FunctionExpressionSyntax lambdaExpression => BindLambdaExpression(lambdaExpression),
            InterpolatedStringExpressionSyntax interpolated => BindInterpolatedStringExpression(interpolated),
            PrefixOperatorExpressionSyntax unaryExpression => BindUnaryExpression(unaryExpression),
            PostfixOperatorExpressionSyntax postfixUnary => BindPostfixUnaryExpression(postfixUnary),
            IndexExpressionSyntax indexExpression => BindIndexExpression(indexExpression),
            SelfExpressionSyntax selfExpression => BindSelfExpression(selfExpression),
            BaseExpressionSyntax baseExpression => BindBaseExpression(baseExpression),
            ReceiverBindingExpressionSyntax receiverBindingExpression => BindReceiverBindingExpression(receiverBindingExpression),
            DiscardExpressionSyntax discardExpression => BindDiscardExpression(discardExpression),
            FreestandingMacroExpressionSyntax freestandingMacroExpression => BindFreestandingMacroExpression(freestandingMacroExpression),
            UnitExpressionSyntax unitExpression => BindUnitExpression(unitExpression),
            ExpressionSyntax.Missing missing => BindMissingExpression(missing),
            _ => throw new NotSupportedException($"Unsupported expression: {syntax.Kind}")
        };

        if (useContextualCache)
            CacheBoundNode(syntax, boundNode, activeTargetType);
        else if (!skipCache)
            CacheBoundNode(syntax, boundNode);

        return boundNode;
    }

    protected virtual void OnFreestandingMacroExpressionBinding(FreestandingMacroExpressionSyntax syntax)
    {
    }

    private BoundExpression BindObjectInitializerExpressionForSemanticQuery(ObjectInitializerExpressionSyntax syntax)
    {
        if (syntax.Parent is InvocationExpressionSyntax invocation)
        {
            var boundInvocation = BindExpression(invocation);
            if (boundInvocation.Type is { TypeKind: not TypeKind.Error } type)
                return new BoundDefaultValueExpression(type);
        }

        return new BoundErrorExpression(Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);
    }

    public override BoundNode GetOrBind(SyntaxNode node)
    {
        using var _ = EnterExecutionScope();

        if (node is ArrowExpressionClauseSyntax arrow)
        {
            if (TryGetCachedBoundNode(arrow) is BoundNode cached)
                return cached;

            var expression = BindExpression(arrow.Expression, allowReturn: true);
            if (GetContainingReturnTargetType() is { } targetType &&
                targetType.SpecialType is not SpecialType.System_Unit and not SpecialType.System_Void &&
                ReportStructUnionReturnMayBeDefault(targetType, expression, arrow.Expression))
            {
                expression = new BoundErrorExpression(targetType, null, BoundExpressionReason.OtherError);
            }

            BoundBlockStatement bound = expression is BoundBlockExpression blockExpression
                ? new BoundBlockStatement(blockExpression.Statements, blockExpression.LocalsToDispose)
                : new BoundBlockStatement([new BoundExpressionStatement(expression)]);

            CacheBoundNode(arrow, bound);
            return bound;
        }

        return base.GetOrBind(node);
    }

    public override BoundNode GetOrBindForSemanticQuery(SyntaxNode node)
    {
        using var _ = EnterSemanticQueryScope();
        return GetOrBind(node);
    }

    private bool IsInSemanticQueryScope => _semanticQueryScopeDepth > 0;

    private IDisposable EnterNestedBinderExecutionScope(BlockBinder binder)
        => IsInSemanticQueryScope
            ? binder.EnterSemanticQueryScope()
            : binder.EnterExecutionScope();

    private ExecutionScope EnterExecutionScope()
    {
        Monitor.Enter(_executionGate);
        return new ExecutionScope(_executionGate);
    }

    private SemanticQueryScope EnterSemanticQueryScope()
    {
        Monitor.Enter(_executionGate);
        return new SemanticQueryScope(this);
    }

    private sealed class SemanticQueryScope : IDisposable
    {
        private readonly BlockBinder _binder;
        private readonly BlockTransientStateSnapshot _snapshot;
        private readonly IDisposable _diagnosticsScope;
        private bool _disposed;

        public SemanticQueryScope(BlockBinder binder)
        {
            _binder = binder;
            _binder._semanticQueryScopeDepth++;
            _snapshot = BlockTransientStateSnapshot.Capture(binder);
            _diagnosticsScope = binder.Diagnostics.CreateNonReportingScope();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _diagnosticsScope.Dispose();
            _snapshot.Restore(_binder);
            _binder._semanticQueryScopeDepth--;
            _disposed = true;
            Monitor.Exit(_binder._executionGate);
        }
    }

    private readonly struct BlockTransientStateSnapshot
    {
        private readonly Dictionary<string, (ILocalSymbol Symbol, int Depth)> _locals;
        private readonly List<(ILocalSymbol Local, int Depth)> _localsToDispose;
        private readonly Dictionary<SyntaxReferenceKey, int> _statementDeclarationProgress;
        private readonly HashSet<ISymbol> _isNotNullNarrowedSymbols;
        private readonly int _scopeDepth;
        private readonly SyntaxKind _ambientPatternDeclarationBindingKeyword;

        private BlockTransientStateSnapshot(
            Dictionary<string, (ILocalSymbol Symbol, int Depth)> locals,
            List<(ILocalSymbol Local, int Depth)> localsToDispose,
            Dictionary<SyntaxReferenceKey, int> statementDeclarationProgress,
            HashSet<ISymbol> isNotNullNarrowedSymbols,
            int scopeDepth,
            SyntaxKind ambientPatternDeclarationBindingKeyword)
        {
            _locals = locals;
            _localsToDispose = localsToDispose;
            _statementDeclarationProgress = statementDeclarationProgress;
            _isNotNullNarrowedSymbols = isNotNullNarrowedSymbols;
            _scopeDepth = scopeDepth;
            _ambientPatternDeclarationBindingKeyword = ambientPatternDeclarationBindingKeyword;
        }

        public static BlockTransientStateSnapshot Capture(BlockBinder binder)
            => new(
                new Dictionary<string, (ILocalSymbol Symbol, int Depth)>(binder._locals, StringComparer.Ordinal),
                new List<(ILocalSymbol Local, int Depth)>(binder._localsToDispose),
                binder._statementDeclarationProgress.CreateSnapshot(),
                new HashSet<ISymbol>(binder._isNotNullNarrowedSymbols, SymbolEqualityComparer.Default),
                binder._scopeDepth,
                binder._ambientPatternDeclarationBindingKeyword);

        public void Restore(BlockBinder binder)
        {
            binder._locals.Clear();
            foreach (var entry in _locals)
                binder._locals[entry.Key] = entry.Value;

            binder._localsToDispose.Clear();
            binder._localsToDispose.AddRange(_localsToDispose);

            binder._statementDeclarationProgress.Restore(_statementDeclarationProgress);
            binder._isNotNullNarrowedSymbols.Clear();
            binder._isNotNullNarrowedSymbols.UnionWith(_isNotNullNarrowedSymbols);
            binder._scopeDepth = _scopeDepth;
            binder._ambientPatternDeclarationBindingKeyword = _ambientPatternDeclarationBindingKeyword;
        }
    }

    private BoundExpression BindReceiverBindingExpression(ReceiverBindingExpressionSyntax syntax)
    {
        _diagnostics.ReportInvalidInvocation(syntax.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindFreestandingMacroExpression(FreestandingMacroExpressionSyntax syntax)
    {
        if (syntax.Name is SimpleNameSyntax simpleName &&
            LookupSymbol(simpleName.Identifier.ValueText) is ILocalSymbol or IParameterSymbol)
        {
            _diagnostics.ReportInvalidInvocation(syntax.Name.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        var expansion = GetFreestandingMacroExpansion(syntax);
        if (expansion?.Expression is null)
            return ErrorExpression(reason: BoundExpressionReason.NotFound);

        SemanticModel.RegisterMacroReplacementSyntaxTree(syntax, expansion.Expression);

        var expressionResultType = expansion.ExpressionResultType is { } runtimeResultType
            ? MacroExpressionTypeFacts.ResolveConstraint(Compilation, runtimeResultType)
            : null;
        var bound = BindExpressionWithTargetType(
            expansion.Expression,
            expressionResultType ?? GetScopedTargetType(syntax),
            allowReturn: _allowReturnsInExpression,
            allowReturnInBlockExpressionsOnly: _allowReturnsInBlockExpressionsOnly);

        if (expressionResultType is not null &&
            bound.Type is { } boundType &&
            !IsAssignable(expressionResultType, boundType, out _))
        {
            _diagnostics.Report(Diagnostic.Create(
                s_macroExpressionResultTypeMismatch,
                syntax.Name.GetLocation(),
                syntax.Name.ToString(),
                expressionResultType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                boundType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            bound = new BoundErrorExpression(
                expressionResultType,
                bound.Symbol,
                BoundExpressionReason.OtherError);
        }

        CacheBoundNode(expansion.Expression, bound);
        return bound;
    }

    protected virtual FreestandingMacroExpansionResult? GetFreestandingMacroExpansion(
        FreestandingMacroExpressionSyntax syntax)
        => SemanticModel.GetMacroExpansion(syntax);

    private BoundExpression BindNullCoalesceExpression(NullCoalesceExpressionSyntax coalesce)
    {
        var left = BindExpression(coalesce.Left);

        if (left is BoundErrorExpression)
            return left;

        var leftType = left.Type ?? Compilation.ErrorTypeSymbol;

        // If the left is nullable, the result is typically the non-nullable left type
        // combined with the right type.
        var leftNonNullable = leftType.GetNonNullableType();
        var right = BindExpressionWithTargetType(coalesce.Right, leftNonNullable);

        if (right is BoundErrorExpression)
            return right;

        var rightType = right.Type ?? Compilation.ErrorTypeSymbol;
        var rightNeverCompletes = IsEarlyExitExpression(right, coalesce.Right);

        var resultType = rightNeverCompletes
            ? leftNonNullable
            : TypeSymbolNormalization.GetBestCommonType(new[] { leftNonNullable, rightType });

        // Ensure the RHS can be used as the result type.
        if (!rightNeverCompletes)
        {
            right = PrepareRightForAssignment(right, resultType, coalesce.Right);
            if (right is BoundErrorExpression)
                return right;
        }

        // NOTE: We keep `left` as-is (possibly nullable). Codegen will implement the
        // short-circuit null test and produce `resultType`.
        return new BoundNullCoalesceExpression(left, right, resultType);
    }

    private bool IsEarlyExitExpression(BoundExpression expression, ExpressionSyntax syntax)
    {
        if (expression is BoundReturnExpression or BoundThrowExpression or
            BoundBreakExpression or BoundContinueExpression)
            return true;

        if (syntax is not BlockSyntax block)
            return false;

        var controlFlow = SemanticModel.AnalyzeControlFlowInternal(block, analyzeJumpPoints: false);
        return controlFlow is { Succeeded: true, EndPointIsReachable: false };
    }

    private BoundExpression BindSelfExpression(SelfExpressionSyntax selfExpression)
    {
        if (_containingSymbol is IMethodSymbol method)
        {
            if (!method.IsStatic)
            {
                var containingType = method.ContainingType;
                return new BoundSelfExpression(containingType);
            }

            if (method.IsExtensionMethod && method.Parameters.Length > 0)
            {
                var receiver = method.Parameters[0];
                return new BoundParameterAccess(receiver);
            }

            // Lambdas report IsStatic=true until their captures are analyzed, so we
            // can't rely on IsStatic here.  Walk the binder chain for an enclosing
            // instance method — if one exists, self is reachable and will be lifted
            // into the closure during capture analysis.
            if (method is ILambdaSymbol)
            {
                var enclosing = FindEnclosingInstanceMethod();
                if (enclosing is not null)
                    return new BoundSelfExpression(enclosing.ContainingType);
            }

            _diagnostics.ReportSelfNotAvailableInStaticContext(selfExpression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        _diagnostics.ReportSelfNotAvailableInStaticContext(selfExpression.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindBaseExpression(BaseExpressionSyntax baseExpression)
    {
        if (_containingSymbol is not IMethodSymbol method || method.IsStatic)
        {
            _diagnostics.ReportBaseExpressionNotAvailable(baseExpression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (method.ContainingType?.TypeKind == TypeKind.Interface)
        {
            _diagnostics.ReportBaseExpressionNotAvailable(baseExpression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        var baseType = method.ContainingType?.BaseType;
        if (baseType is null)
        {
            _diagnostics.ReportBaseExpressionNotAvailable(baseExpression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        return new BoundBaseExpression(baseType);
    }

    private IMethodSymbol? FindEnclosingInstanceMethod()
    {
        var binder = ParentBinder;
        while (binder is not null)
        {
            if (binder.ContainingSymbol is IMethodSymbol m && m is not ILambdaSymbol && !m.IsStatic)
                return m;
            binder = binder.ParentBinder;
        }
        return null;
    }

    private BoundExpression BindCompoundAssignmentValue(
        BoundExpression leftValue,
        BoundExpression rightValue,
        ITypeSymbol targetType,
        SyntaxKind binaryOperatorKind,
        ExpressionSyntax rightSyntax)
    {
        var preparedRight = PrepareRightForAssignment(rightValue, targetType, rightSyntax);

        if (preparedRight is BoundErrorExpression)
            return preparedRight;

        var binary = BindBinaryExpression(binaryOperatorKind, leftValue, preparedRight, rightSyntax.GetLocation());

        return ConvertValueForAssignment(binary, targetType, rightSyntax);
    }

    private BoundExpression PrepareRightForAssignment(BoundExpression right, ITypeSymbol targetType, SyntaxNode syntax)
    {
        if (targetType.TypeKind != TypeKind.Error && ShouldAttemptConversion(right))
        {
            if (!IsAssignable(targetType, right.Type!, out var conversion))
            {
                ReportCannotAssignFromTypeToType(right.Type!, targetType, syntax.GetLocation());
                return new BoundErrorExpression(targetType, null, BoundExpressionReason.TypeMismatch);
            }

            right = ApplyConversion(right, targetType, conversion, syntax);
        }

        return right;
    }

    private static SyntaxKind? GetBinaryOperatorFromAssignment(SyntaxKind assignmentOperatorKind)
    {
        return assignmentOperatorKind switch
        {
            SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
            SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
            SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
            SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
            SyntaxKind.AmpersandEqualsToken => SyntaxKind.AmpersandToken,
            SyntaxKind.BarEqualsToken => SyntaxKind.BarToken,
            SyntaxKind.CaretEqualsToken => SyntaxKind.CaretToken,
            SyntaxKind.QuestionQuestionEqualsToken => SyntaxKind.QuestionQuestionToken,
            _ => null,
        };
    }

    private BoundExpression BindDiscardExpression(DiscardExpressionSyntax discardExpression)
    {
        _diagnostics.ReportDiscardExpressionNotAllowed(discardExpression.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
    }

    private BoundExpression BindTupleExpression(TupleExpressionSyntax tupleExpression)
    {
        var elements = new List<BoundExpression>(tupleExpression.Arguments.Count);

        if (GetTargetType(tupleExpression) is INamedTypeSymbol target &&
            target.TypeKind == TypeKind.Tuple &&
            target.TupleElements.Length == tupleExpression.Arguments.Count)
        {
            for (int i = 0; i < tupleExpression.Arguments.Count; i++)
            {
                var arg = tupleExpression.Arguments[i];
                var expected = target.TupleElements[i].Type;
                var boundExpr = BindExpressionWithTargetType(arg.Expression, expected);
                if (expected.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(boundExpr))
                {
                    if (!IsAssignable(expected, boundExpr.Type!, out var conversion))
                    {
                        ReportCannotConvertFromTypeToType(
                            boundExpr.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            expected.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            arg.GetLocation());
                    }
                    else
                    {
                        boundExpr = ApplyConversion(boundExpr, expected, conversion, arg.Expression);
                    }
                }

                elements.Add(boundExpr);
            }

            return new BoundTupleExpression(elements.ToImmutableArray(), target);
        }

        var tupleElements = new List<(string? name, ITypeSymbol type)>();

        foreach (var node in tupleExpression.Arguments)
        {
            if (node is ArgumentSyntax arg)
            {
                var boundExpr = BindExpression(arg.Expression);
                elements.Add(boundExpr);

                var type = boundExpr.Type ?? Compilation.ErrorTypeSymbol;
                string? name = arg.NameColon?.Name.ToString();
                tupleElements.Add((name, type));
            }
        }

        var tupleType = Compilation.CreateTupleTypeSymbol(tupleElements);

        return new BoundTupleExpression(
            elements.ToImmutableArray(),
            tupleType
        );
    }

    private BoundExpression BindUnaryExpression(PrefixOperatorExpressionSyntax unaryExpression)
    {
        if (unaryExpression.Kind == SyntaxKind.AwaitExpression)
            return BindAwaitExpression(unaryExpression);

        if (unaryExpression.Kind == SyntaxKind.PreIncrementExpression)
            return BindIncrementOrDecrement(unaryExpression.Expression, unaryExpression.OperatorToken, SyntaxKind.PlusToken, isPostfix: false);

        if (unaryExpression.Kind == SyntaxKind.PreDecrementExpression)
            return BindIncrementOrDecrement(unaryExpression.Expression, unaryExpression.OperatorToken, SyntaxKind.MinusToken, isPostfix: false);

        var operand = BindExpression(unaryExpression.Expression);

        if (operand is BoundErrorExpression)
            return operand;

        switch (unaryExpression.Kind)
        {
            case SyntaxKind.AddressOfExpression:
                return BindAddressOfExpression(operand, unaryExpression);

            case SyntaxKind.FixedExpression:
                return BindFixedExpression(operand, unaryExpression);

            case SyntaxKind.DereferenceExpression:
                return BindDereferenceExpression(operand, unaryExpression);

            case SyntaxKind.LogicalNotExpression:
            case SyntaxKind.BitwiseNotExpression:
            case SyntaxKind.UnaryMinusExpression:
            case SyntaxKind.UnaryPlusExpression:
                {
                    var opKind = unaryExpression.OperatorToken.Kind;
                    var operandType = operand.Type ?? Compilation.ErrorTypeSymbol;
                    var userDefined = BindUserDefinedUnaryOperator(opKind, operand, unaryExpression.OperatorToken.GetLocation(), unaryExpression.Expression, unaryExpression);
                    if (userDefined is not null)
                        return userDefined;

                    if (BoundUnaryOperator.TryLookup(Compilation, opKind, operandType, out var op))
                        return new BoundUnaryExpression(op, operand);

                    var operatorText = SyntaxFacts.GetSyntaxTokenText(opKind) ?? opKind.ToString();
                    _diagnostics.ReportOperatorCannotBeAppliedToOperandOfType(
                        operatorText,
                        operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        unaryExpression.OperatorToken.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }

            default:
                throw new NotSupportedException("Unsupported unary expression");
        }
    }

    private BoundExpression BindPostfixUnaryExpression(PostfixOperatorExpressionSyntax postfixUnary)
    {
        if (postfixUnary.OperatorToken.Kind == SyntaxKind.ExclamationToken)
            return BindSuppressNullableWarningExpression(postfixUnary);

        var binaryOperatorKind = postfixUnary.OperatorToken.Kind == SyntaxKind.PlusPlusToken
            ? SyntaxKind.PlusToken
            : SyntaxKind.MinusToken;

        return BindIncrementOrDecrement(postfixUnary.Expression, postfixUnary.OperatorToken, binaryOperatorKind, isPostfix: true);
    }

    private BoundExpression BindSuppressNullableWarningExpression(PostfixOperatorExpressionSyntax postfixUnary)
    {
        var operand = BindNullableSuppressionOperand(postfixUnary);

        if (operand is BoundErrorExpression || operand.Type is null)
            return operand;

        if (!operand.Type.IsNullable)
            return operand;

        var unwrappedType = operand.Type.GetNonNullableType();

        _diagnostics.ReportNullableSuppressionUsed(postfixUnary.GetLocation());

        if (operand.Type is NullableTypeSymbol nullableValueType && nullableValueType.UnderlyingType.IsValueType)
            return new BoundNullableValueExpression(operand, nullableValueType.UnderlyingType);

        return new BoundConversionExpression(
            operand,
            unwrappedType,
            new Conversion(isImplicit: false, isIdentity: true, isLifted: true),
            isNullableSuppression: true);
    }

    private BoundExpression BindNullableSuppressionOperand(PostfixOperatorExpressionSyntax postfixUnary)
    {
        if (postfixUnary.Expression is DefaultExpressionSyntax defaultExpression &&
            defaultExpression.Type is null &&
            GetTargetType(postfixUnary) is { } targetType &&
            targetType.TypeKind != TypeKind.Error)
        {
            return BindExpressionWithTargetType(defaultExpression, targetType.GetDefaultValueType());
        }

        return BindExpression(postfixUnary.Expression);
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax indexExpression)
    {
        var value = BindExpression(indexExpression.Expression);

        if (IsErrorExpression(value))
            return AsErrorExpression(value);

        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);

        if (intType.TypeKind != TypeKind.Error && ShouldAttemptConversion(value))
        {
            var sourceType = value.Type ?? Compilation.ErrorTypeSymbol;

            if (!IsAssignable(intType, sourceType, out var conversion))
            {
                ReportCannotConvertFromTypeToType(
                    sourceType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    intType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    indexExpression.Expression.GetLocation());

                return new BoundErrorExpression(intType, null, BoundExpressionReason.TypeMismatch);
            }

            value = ApplyConversion(value, intType, conversion, indexExpression.Expression);
        }

        return new BoundIndexExpression(value, isFromEnd: true, GetIndexType());
    }

    private BoundExpression BindRangeExpression(RangeExpressionSyntax rangeExpression)
    {
        var start = BindRangeEndpoint(rangeExpression.LeftExpression);
        if (start is BoundErrorExpression errorStart)
            return errorStart;

        var end = BindRangeEndpoint(rangeExpression.RightExpression);
        if (end is BoundErrorExpression errorEnd)
            return errorEnd;

        return new BoundRangeExpression(
            start as BoundIndexExpression,
            end as BoundIndexExpression,
            GetRangeType(),
            rangeExpression.LessThanToken.Kind == SyntaxKind.LessThanToken);
    }

    private BoundExpression? BindRangeEndpoint(ExpressionSyntax? endpointSyntax)
    {
        if (endpointSyntax is null)
            return null;

        var bound = BindExpression(endpointSyntax);

        if (IsErrorExpression(bound))
            return bound;

        if (bound is BoundIndexExpression index)
            return index;

        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);

        if (intType.TypeKind != TypeKind.Error && ShouldAttemptConversion(bound))
        {
            var sourceType = bound.Type ?? Compilation.ErrorTypeSymbol;

            if (!IsAssignable(intType, sourceType, out var conversion))
            {
                ReportCannotConvertFromTypeToType(
                    sourceType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    intType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    endpointSyntax.GetLocation());

                return new BoundErrorExpression(intType, null, BoundExpressionReason.TypeMismatch);
            }

            bound = ApplyConversion(bound, intType, conversion, endpointSyntax);
        }

        return new BoundIndexExpression(bound, isFromEnd: false, GetIndexType());
    }

    private BoundExpression BindIncrementOrDecrement(
        ExpressionSyntax operandSyntax,
        SyntaxToken operatorToken,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        var operand = BindExpression(operandSyntax);

        if (operand is BoundErrorExpression)
            return operand;

        return operand switch
        {
            BoundLocalAccess local => BindIncrementForLocal(local, operandSyntax, binaryOperatorKind, isPostfix),
            BoundParameterAccess parameter => BindIncrementForParameter(parameter, operandSyntax, binaryOperatorKind, isPostfix),
            BoundArrayAccessExpression arrayAccess => BindIncrementCore(
                operandSyntax,
                operand,
                arrayAccess.Type ?? Compilation.ErrorTypeSymbol,
                binaryOperatorKind,
                operatorToken.Kind,
                isPostfix,
                value => BoundFactory.CreateArrayAssignmentExpression(arrayAccess, value)),
            BoundIndexerAccessExpression indexerAccess => BindIncrementCore(
                operandSyntax,
                operand,
                indexerAccess.Type ?? Compilation.ErrorTypeSymbol,
                binaryOperatorKind,
                operatorToken.Kind,
                isPostfix,
                value => BoundFactory.CreateIndexerAssignmentExpression(indexerAccess, value)),
            BoundFieldAccess fieldAccess => BindIncrementForField(fieldAccess, operandSyntax, binaryOperatorKind, isPostfix),
            BoundMemberAccessExpression memberAccess when memberAccess.Symbol is IPropertySymbol propertySymbol
                => BindIncrementForProperty(memberAccess, propertySymbol, operandSyntax, binaryOperatorKind, isPostfix),
            BoundPropertyAccess propertyAccess
                => BindIncrementForProperty(propertyAccess, propertyAccess.Property, operandSyntax, binaryOperatorKind, isPostfix),
            _ => BindInvalidIncrementOperand(operatorToken)
        };
    }

    private BoundExpression BindIncrementForLocal(
        BoundLocalAccess localAccess,
        ExpressionSyntax operandSyntax,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        var localSymbol = localAccess.Local;
        var localType = localSymbol.Type ?? Compilation.ErrorTypeSymbol;
        var targetType = GetIncrementTargetType(localType);

        if (!localSymbol.IsMutable)
        {
            _diagnostics.ReportThisValueIsNotMutable(operandSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        return BindIncrementCore(
            operandSyntax,
            localAccess,
            targetType,
            binaryOperatorKind,
            binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
            isPostfix,
            value => localType is RefTypeSymbol refType
                ? BoundFactory.CreateByRefAssignmentExpression(localAccess, refType.ElementType, value)
                : BoundFactory.CreateLocalAssignmentExpression(localSymbol, localAccess, value));
    }

    private BoundExpression BindIncrementForParameter(
        BoundParameterAccess parameterAccess,
        ExpressionSyntax operandSyntax,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        var parameterSymbol = parameterAccess.Parameter;
        var parameterType = parameterSymbol.Type ?? Compilation.ErrorTypeSymbol;
        var targetType = GetIncrementTargetType(parameterType);

        if (!parameterSymbol.IsMutable)
        {
            _diagnostics.ReportThisValueIsNotMutable(operandSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        return BindIncrementCore(
            operandSyntax,
            parameterAccess,
            targetType,
            binaryOperatorKind,
            binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
            isPostfix,
            value => parameterSymbol.RefKind is RefKind.Ref or RefKind.Out
                ? BoundFactory.CreateByRefAssignmentExpression(parameterAccess, parameterSymbol.GetByRefElementType(), value)
                : BoundFactory.CreateParameterAssignmentExpression(parameterSymbol, parameterAccess, value));
    }

    private BoundExpression BindIncrementForField(
        BoundFieldAccess fieldAccess,
        ExpressionSyntax operandSyntax,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        var fieldSymbol = fieldAccess.Field;

        if (fieldSymbol.IsConst)
        {
            _diagnostics.ReportThisValueIsNotMutable(operandSyntax.GetLocation());
            return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.NotFound);
        }

        if (!CanAssignToField(fieldSymbol, fieldAccess.Receiver, operandSyntax))
            return new BoundErrorExpression(fieldSymbol.Type, fieldSymbol, BoundExpressionReason.NotFound);

        var targetType = fieldSymbol.Type ?? Compilation.ErrorTypeSymbol;

        return BindIncrementCore(
            operandSyntax,
            fieldAccess,
            targetType,
            binaryOperatorKind,
            binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
            isPostfix,
            value => CreateFieldAssignmentExpression(fieldAccess.Receiver, fieldSymbol, value));
    }

    private BoundExpression BindIncrementForProperty(
        BoundMemberAccessExpression memberAccess,
        IPropertySymbol propertySymbol,
        ExpressionSyntax operandSyntax,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        SourceFieldSymbol? backingField = null;
        var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);

        if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
        {
            if (!TryGetWritableAutoPropertyBackingField(propertySymbol, memberAccess, out backingField))
            {
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, operandSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var backingAccess = new BoundFieldAccess(memberAccess.Receiver, backingField);

            if (!CanAssignToField(backingField, memberAccess.Receiver, operandSyntax))
                return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

            return BindIncrementCore(
                operandSyntax,
                backingAccess,
                propertySymbol.Type,
                binaryOperatorKind,
                binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
                isPostfix,
                value => CreateFieldAssignmentExpression(memberAccess.Receiver, backingField, value));
        }

        if (useFieldOnlyLowering && backingField is not null)
        {
            var backingAccess = new BoundFieldAccess(memberAccess.Receiver, backingField);

            if (!CanAssignToField(backingField, memberAccess.Receiver, operandSyntax))
                return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

            return BindIncrementCore(
                operandSyntax,
                backingAccess,
                propertySymbol.Type,
                binaryOperatorKind,
                binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
                isPostfix,
                value => CreateFieldAssignmentExpression(memberAccess.Receiver, backingField, value));
        }

        return BindIncrementCore(
            operandSyntax,
            memberAccess,
            propertySymbol.Type,
            binaryOperatorKind,
            binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
            isPostfix,
            value => BoundFactory.CreatePropertyAssignmentExpression(memberAccess.Receiver, propertySymbol, value));
    }

    private BoundExpression BindIncrementForProperty(
        BoundPropertyAccess propertyAccess,
        IPropertySymbol propertySymbol,
        ExpressionSyntax operandSyntax,
        SyntaxKind binaryOperatorKind,
        bool isPostfix)
    {
        SourceFieldSymbol? backingField = null;
        var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);

        if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
        {
            if (!TryGetWritableAutoPropertyBackingField(propertySymbol, propertyAccess, out backingField))
            {
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, operandSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            if (!CanAssignToField(backingField, receiver: null, operandSyntax))
                return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

            var backingAccess = new BoundFieldAccess(backingField);
            return BindIncrementCore(
                operandSyntax,
                backingAccess,
                propertySymbol.Type,
                binaryOperatorKind,
                binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
                isPostfix,
                value => CreateFieldAssignmentExpression(receiver: null, backingField, value));
        }

        if (useFieldOnlyLowering && backingField is not null)
        {
            if (!CanAssignToField(backingField, receiver: null, operandSyntax))
                return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

            var backingAccess = new BoundFieldAccess(backingField);
            return BindIncrementCore(
                operandSyntax,
                backingAccess,
                propertySymbol.Type,
                binaryOperatorKind,
                binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
                isPostfix,
                value => CreateFieldAssignmentExpression(receiver: null, backingField, value));
        }

        return BindIncrementCore(
            operandSyntax,
            propertyAccess,
            propertySymbol.Type,
            binaryOperatorKind,
            binaryOperatorKind == SyntaxKind.PlusToken ? SyntaxKind.PlusPlusToken : SyntaxKind.MinusMinusToken,
            isPostfix,
            value => BoundFactory.CreatePropertyAssignmentExpression(receiver: null, propertySymbol, value));
    }

    private BoundExpression BindIncrementCore(
        ExpressionSyntax operandSyntax,
        BoundExpression valueExpression,
        ITypeSymbol targetType,
        SyntaxKind binaryOperatorKind,
        SyntaxKind incrementOperatorKind,
        bool isPostfix,
        Func<BoundExpression, BoundAssignmentExpression> createAssignment)
    {
        if (targetType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        var initialValue = ConvertValueForAssignment(valueExpression, targetType, operandSyntax);

        if (initialValue is BoundErrorExpression)
            return initialValue;

        var tempLocal = CreateTempLocal("inc", targetType, operandSyntax);
        var tempAccess = new BoundLocalAccess(tempLocal);

        var statements = new List<BoundStatement>
        {
            new BoundLocalDeclarationStatement([new BoundVariableDeclarator(tempLocal, initialValue)])
        };

        var updatedValue = BindUserDefinedUnaryOperator(
            incrementOperatorKind,
            tempAccess,
            operandSyntax.GetLocation(),
            operandSyntax,
            operandSyntax);

        if (updatedValue is null)
        {
            var stepLiteral = CreateStepLiteral();
            var preparedStep = PrepareRightForAssignment(stepLiteral, targetType, operandSyntax);

            if (preparedStep is BoundErrorExpression)
                return preparedStep;

            updatedValue = BindBinaryExpression(binaryOperatorKind, tempAccess, preparedStep, operandSyntax.GetLocation());
        }
        else if (updatedValue is BoundErrorExpression)
        {
            return updatedValue;
        }

        var convertedUpdatedValue = ConvertValueForAssignment(updatedValue, targetType, operandSyntax);

        if (convertedUpdatedValue is BoundErrorExpression)
            return convertedUpdatedValue;

        var assignment = createAssignment(convertedUpdatedValue);
        statements.Add(new BoundExpressionStatement(assignment));

        var resultExpression = isPostfix
            ? (BoundExpression)new BoundLocalAccess(tempLocal)
            : convertedUpdatedValue;

        statements.Add(new BoundExpressionStatement(resultExpression));

        return BoundFactory.CreateBlockExpression(statements);
    }

    private BoundExpression BindInvalidIncrementOperand(SyntaxToken operatorToken)
    {
        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(operatorToken.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
    }

    private ITypeSymbol GetIncrementTargetType(ITypeSymbol type)
    {
        return type is RefTypeSymbol refType ? refType.ElementType : type;
    }

    private BoundLiteralExpression CreateStepLiteral()
    {
        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);
        return new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, 1, intType);
    }

    private BoundExpression BindAwaitExpression(PrefixOperatorExpressionSyntax awaitExpression)
    {
        if (!IsAwaitExpressionAllowed())
        {
            _diagnostics.ReportAwaitExpressionRequiresAsyncContext(awaitExpression.OperatorToken.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var operand = BindExpression(awaitExpression.Expression);

        if (operand is BoundErrorExpression)
            return operand;

        var operandType = operand.Type;

        if (operandType is null || operandType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);

        if (!AwaitablePattern.TryFind(operandType, IsSymbolAccessible, out var awaitable, out var failure, out var awaiterType))
        {
            switch (failure)
            {
                case AwaitablePatternFailure.IsCompletedMissing:
                    _diagnostics.ReportAwaiterMissingIsCompleted(
                        (awaiterType ?? operandType).ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        awaitExpression.Expression.GetLocation());
                    break;
                case AwaitablePatternFailure.GetResultMissing:
                    _diagnostics.ReportAwaiterMissingGetResult(
                        (awaiterType ?? operandType).ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        awaitExpression.Expression.GetLocation());
                    break;
                default:
                    _diagnostics.ReportExpressionIsNotAwaitable(
                        operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        awaitExpression.Expression.GetLocation());
                    break;
            }

            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var resultType = awaitable.GetResultMethod.ReturnType;
        if (resultType.SpecialType == SpecialType.System_Void)
            resultType = Compilation.GetSpecialType(SpecialType.System_Unit);

        return new BoundAwaitExpression(
            operand,
            resultType,
            awaitable.AwaiterType,
            awaitable.GetAwaiterMethod,
            awaitable.GetResultMethod,
            awaitable.IsCompletedProperty);
    }

    private bool IsAwaitExpressionAllowed()
    {
        static bool IsAsyncSymbol(ISymbol? symbol)
        {
            return symbol switch
            {
                ILambdaSymbol lambda => lambda.IsAsync,
                IMethodSymbol method => method.IsAsync,
                _ => false,
            };
        }

        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (IsAsyncSymbol(current.ContainingSymbol))
                return true;

            if (current is BlockBinder block && IsAsyncSymbol(block.ContainingSymbol))
                return true;
        }

        return false;
    }

    private static bool IsImplicitReturnTarget(BlockStatementSyntax block, ExpressionStatementSyntax expressionStatement)
    {
        if (block.Statements.Count == 0 || block.Statements.LastOrDefault() != expressionStatement)
            return false;

        return block.Parent switch
        {
            BaseMethodDeclarationSyntax => true,
            FunctionStatementSyntax => true,
            AccessorDeclarationSyntax => true,
            FunctionExpressionSyntax => true,
            _ => false,
        };
    }

    private static bool IsImplicitReturnTarget(BlockSyntax block, ExpressionStatementSyntax expressionStatement)
    {
        if (block.Statements.Count == 0 || block.Statements.LastOrDefault() != expressionStatement)
            return false;

        return block.Parent switch
        {
            BaseMethodDeclarationSyntax => true,
            FunctionStatementSyntax => true,
            AccessorDeclarationSyntax => true,
            FunctionExpressionSyntax => true,
            _ => false,
        };
    }

    private BoundExpression BindAddressOfExpression(BoundExpression operand, PrefixOperatorExpressionSyntax syntax)
    {
        if (operand is BoundErrorExpression)
            return operand;

        switch (operand)
        {
            case BoundLocalAccess or BoundParameterAccess:
                return new BoundAddressOfExpression(operand);

            case BoundFieldAccess fieldAccess:
                return new BoundAddressOfExpression(fieldAccess);

            case BoundMemberAccessExpression { Member: IFieldSymbol } memberAccess:
                return new BoundAddressOfExpression(memberAccess);

            case BoundArrayAccessExpression arrayAccess:
                return new BoundAddressOfExpression(arrayAccess);

            case BoundSelfExpression selfExpression:
                return new BoundAddressOfExpression(selfExpression);
        }

        //_diagnostics.ReportInvalidAddressOf(syntax.Expression.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed /*,.InvalidAddressOfTarget */);
    }

    private BoundExpression BindFixedExpression(BoundExpression operand, PrefixOperatorExpressionSyntax syntax)
    {
        if (!IsUnsafeEnabled)
        {
            _diagnostics.ReportPointerOperationRequiresUnsafe(syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        if (!IsInsideUseDeclarationInitializer(syntax))
        {
            _diagnostics.ReportFixedExpressionRequiresUseInitializer(syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        if (operand is not BoundAddressOfExpression addressOf)
        {
            _diagnostics.ReportFixedExpressionRequiresAddressOfOperand(syntax.Expression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        var pointerType = Compilation.CreatePointerTypeSymbol(addressOf.ReferencedType);
        var conversion = Compilation.ClassifyConversion(addressOf.Type, pointerType, includeUserDefined: false);
        return new BoundConversionExpression(addressOf, pointerType, conversion);
    }

    private BoundExpression BindStackAllocExpression(StackAllocExpressionSyntax syntax)
    {
        var elementType = BindTypeSyntaxAndReport(syntax.ElementType);
        if (elementType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.OtherError);

        if (!IsStackAllocElementType(elementType))
        {
            _diagnostics.ReportStackAllocElementTypeMustBeUnmanaged(
                elementType.ToDisplayString(),
                syntax.ElementType.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);
        var count = BindExpressionWithTargetType(syntax.Count, intType);
        var conversion = Compilation.ClassifyConversion(count.Type, intType, includeUserDefined: false);
        if (!conversion.Exists || !conversion.IsImplicit)
        {
            _diagnostics.ReportStackAllocCountMustBeInteger(
                count.Type.ToDisplayString(),
                syntax.Count.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        if (!conversion.IsIdentity)
            count = new BoundConversionExpression(count, intType, conversion);

        if (count is BoundLiteralExpression { Value: int value } && value < 0)
        {
            _diagnostics.ReportStackAllocCountCannotBeNegative(syntax.Count.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        var targetType = GetTargetType(syntax);
        ITypeSymbol allocationType;

        if (targetType is IPointerTypeSymbol pointerTarget &&
            SymbolEqualityComparer.Default.Equals(pointerTarget.PointedAtType, elementType))
        {
            if (!IsUnsafeEnabled)
            {
                _diagnostics.ReportPointerOperationRequiresUnsafe(syntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.OtherError);
            }

            allocationType = targetType;
        }
        else if (TryGetSpanElementType(targetType, out var targetElementType) &&
                 SymbolEqualityComparer.Default.Equals(targetElementType, elementType))
        {
            allocationType = targetType!;
        }
        else if (Compilation.GetTypeByMetadataName("System.Span`1") is INamedTypeSymbol spanDefinition)
        {
            allocationType = spanDefinition.Construct(elementType);
        }
        else
        {
            allocationType = Compilation.CreatePointerTypeSymbol(elementType);
            if (!IsUnsafeEnabled)
            {
                _diagnostics.ReportPointerOperationRequiresUnsafe(syntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.OtherError);
            }
        }

        return new BoundStackAllocExpression(elementType, count, allocationType);
    }

    private static bool TryGetSpanElementType(ITypeSymbol? type, out ITypeSymbol elementType)
    {
        if (type is INamedTypeSymbol namedType &&
            namedType.TypeArguments.Length == 1 &&
            namedType.ContainingNamespace?.ToDisplayString() == "System" &&
            namedType.Name is "Span" or "ReadOnlySpan")
        {
            elementType = namedType.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static bool IsStackAllocElementType(ITypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Pointer or TypeKind.Enum)
            return true;

        if (type.SpecialType is not SpecialType.None)
            return type.IsValueType;

        if (type is ITypeParameterSymbol)
            return false;

        if (type is not INamedTypeSymbol { IsValueType: true } namedType)
            return false;

        return namedType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => !field.IsStatic)
            .All(field => IsStackAllocElementType(field.Type));
    }

    private static bool IsInsideUseDeclarationInitializer(SyntaxNode syntax)
    {
        for (SyntaxNode? current = syntax; current is not null; current = current.Parent)
        {
            if (current is EqualsValueClauseSyntax equalsValue &&
                equalsValue.Parent is VariableDeclaratorSyntax declarator &&
                declarator.Parent is VariableDeclarationSyntax declaration &&
                declaration.Parent is UseDeclarationStatementSyntax)
            {
                return true;
            }
        }

        return false;
    }

    protected BoundExpression BindByRefInvocationArgument(BoundExpression operand, RefKind refKind, SyntaxNode syntax)
    {
        if (!refKind.IsByRef)
            return operand;

        if (operand is BoundErrorExpression)
            return operand;

        if (refKind is RefKind.Ref or RefKind.Out)
        {
            switch (operand)
            {
                case BoundLocalAccess { Local.IsMutable: false }:
                case BoundParameterAccess { Parameter.IsMutable: false }:
                    _diagnostics.ReportThisValueIsNotMutable(syntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);
            }
        }

        return operand switch
        {
            BoundLocalAccess or BoundParameterAccess => new BoundAddressOfExpression(operand),
            BoundFieldAccess fieldAccess => CanAssignToField(fieldAccess.Field, fieldAccess.Receiver, syntax)
                ? new BoundAddressOfExpression(fieldAccess)
                : new BoundErrorExpression(fieldAccess.Type ?? Compilation.ErrorTypeSymbol, fieldAccess.Field, BoundExpressionReason.ArgumentBindingFailed),
            BoundMemberAccessExpression { Member: IFieldSymbol } memberAccess => new BoundAddressOfExpression(memberAccess),
            BoundArrayAccessExpression arrayAccess => new BoundAddressOfExpression(arrayAccess),
            BoundSelfExpression selfExpression => new BoundAddressOfExpression(selfExpression),
            _ => ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed),
        };
    }

    protected BoundExpression BindInvocationArgumentExpression(ArgumentSyntax syntax, ITypeSymbol? targetType, RefKind refKind)
    {
        if (syntax.BindingKeyword.Kind != SyntaxKind.None)
            return BindDeclaredOutArgument(syntax, targetType, refKind);

        var expressionTargetType = refKind.IsByRef && targetType is RefTypeSymbol byRefTargetType
            ? byRefTargetType.ElementType
            : targetType;

        BoundExpression boundExpression;
        if (refKind.IsByRef && syntax.Expression is IdentifierNameSyntax identifier)
        {
            using var _ = PushTargetType(expressionTargetType, identifier);
            boundExpression = BindIdentifierName(identifier, applyFlowNarrowing: false);
        }
        else
        {
            boundExpression = expressionTargetType is null
                ? BindExpression(syntax.Expression)
                : BindExpressionWithTargetType(syntax.Expression, expressionTargetType);
        }

        if (refKind.IsByRef)
            boundExpression = BindByRefInvocationArgument(boundExpression, refKind, syntax.Expression);

        return boundExpression;
    }

    private BoundExpression BindDeclaredOutArgument(ArgumentSyntax syntax, ITypeSymbol? targetType, RefKind refKind)
    {
        if (refKind != RefKind.Out || syntax.Expression is not IdentifierNameSyntax identifier)
            return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);

        var name = identifier.Identifier.ValueText;
        var localType = targetType is RefTypeSymbol byRefTargetType
            ? byRefTargetType.ElementType
            : targetType ?? Compilation.ErrorTypeSymbol;
        var isMutable = syntax.BindingKeyword.Kind == SyntaxKind.VarKeyword;

        if (_locals.TryGetValue(name, out var existing) && existing.Depth == _scopeDepth)
        {
            var isSameDeclaration = existing.Symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree == identifier.SyntaxTree &&
                reference.Span == identifier.Span);

            if (isSameDeclaration)
                return BindByRefInvocationArgument(new BoundLocalAccess(existing.Symbol), refKind, syntax.Expression);
        }

        if (LookupSymbol(name) is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            _diagnostics.ReportVariableShadowsPreviousDeclaration(name, identifier.Identifier.GetLocation());

        var local = CreateLocalSymbol(identifier, name, isMutable, localType);
        var access = new BoundLocalAccess(local);
        CacheBoundNode(identifier, access);
        return BindByRefInvocationArgument(access, refKind, syntax.Expression);
    }

    private BoundExpression BindDereferenceExpression(BoundExpression operand, PrefixOperatorExpressionSyntax syntax)
    {
        if (operand is BoundErrorExpression)
            return operand;

        var operandType = operand.Type ?? Compilation.ErrorTypeSymbol;
        if (operandType is not RefTypeSymbol && !IsUnsafeEnabled)
        {
            _diagnostics.ReportPointerOperationRequiresUnsafe(syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var elementType = operandType switch
        {
            IPointerTypeSymbol pointer => pointer.PointedAtType,
            IAddressTypeSymbol address => address.ReferencedType,
            RefTypeSymbol refType => refType.ElementType,
            _ => null,
        };

        if (elementType is null)
        {
            _diagnostics.ReportDereferenceRequiresPointerOrByRef(
                operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        return new BoundDereferenceExpression(operand, elementType);
    }

    private BoundExpression BindMissingExpression(ExpressionSyntax.Missing missing)
    {
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindParenthesizedExpression(ParenthesizedExpressionSyntax parenthesizedExpression)
    {
        var targetType = GetScopedTargetType(parenthesizedExpression);
        var expression = targetType is null
            ? BindExpression(parenthesizedExpression.Expression)
            : BindExpressionWithTargetType(parenthesizedExpression.Expression, targetType);

        if (expression is BoundErrorExpression)
            return expression;

        return new BoundParenthesizedExpression(expression);
    }

    private BoundExpression BindConversionExpression(CastExpressionSyntax castExpression)
    {
        var expression = BindExpression(castExpression.Expression);
        var targetType = ResolveTypeSyntaxOrError(castExpression.Type);

        if (HasExpressionErrors(expression))
            return expression;
        if (targetType.ContainsErrorType())
            return ErrorExpression(targetType, reason: BoundExpressionReason.NotFound);

        var conversion = Compilation.ClassifyConversion(expression.Type!, targetType);
        if (!conversion.Exists)
        {
            ReportCannotConvertFromTypeToType(expression.Type!, targetType, castExpression.GetLocation());
            return new BoundErrorExpression(targetType, null, BoundExpressionReason.TypeMismatch);
        }

        if (conversion.IsImplicit)
        {
            _diagnostics.ReportRedundantExplicitCast(
                expression.Type!.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                targetType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                castExpression.GetLocation());
        }

        return new BoundConversionExpression(expression, targetType, conversion);
    }

    private BoundExpression BindTypeOfExpression(TypeOfExpressionSyntax typeOfExpression)
    {
        ITypeSymbol operandType;
        using (Diagnostics.CreateNonReportingScope())
            operandType = ResolveTypeSyntaxOrError(typeOfExpression.Type);

        if (operandType.ContainsErrorType() &&
            Compilation.GetMacroRegistry().TryResolveMacroSymbol(
                Compilation,
                typeOfExpression.Type,
                typeOfExpression.Type.ToString(),
                out var macroSymbol,
                out _))
        {
            _diagnostics.Report(Diagnostic.Create(
                s_macroUsedLikeAType,
                typeOfExpression.Type.GetLocation(),
                macroSymbol.Name));
            return ErrorExpression(
                Compilation.ErrorTypeSymbol,
                macroSymbol,
                reason: BoundExpressionReason.TypeMismatch);
        }

        if (operandType.ContainsErrorType())
            operandType = ResolveTypeSyntaxOrError(typeOfExpression.Type);

        if (operandType.ContainsErrorType())
            return ErrorExpression(operandType, reason: BoundExpressionReason.NotFound);

        var systemType = Compilation.GetSpecialType(SpecialType.System_Type);

        return new BoundTypeOfExpression(operandType, systemType);
    }

    private BoundExpression BindSizeOfExpression(SizeOfExpressionSyntax sizeOfExpression)
    {
        var operandType = ResolveTypeSyntaxOrError(sizeOfExpression.Type);

        if (operandType.ContainsErrorType())
            return ErrorExpression(operandType, reason: BoundExpressionReason.NotFound);

        var intType = Compilation.GetSpecialType(SpecialType.System_Int32);

        return new BoundTypeOfExpression(operandType, intType);
    }

    private BoundExpression BindNameOfExpression(NameOfExpressionSyntax nameOfExpression)
    {
        // `nameof` is a compile-time-only operation. We still bind the operand so normal
        // name lookup, overload resolution and diagnostics apply, but the operand is not
        // evaluated at runtime.

        var operand = nameOfExpression.Operand;

        ISymbol? symbol = ResolveNameOfSymbol(nameOfExpression.Operand);

        if (symbol is null)
        {
            // Report the most helpful missing name. For qualified/member-access chains we
            // report the left-most identifier so users see "Foo" rather than "Foo.Bar.Baz".
            var leftMost = GetLeftMostNameNode(operand);

            var nameText = leftMost switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax gen => gen.Identifier.ValueText,
                _ => leftMost.ToString()
            };

            var location = leftMost.GetLocation() ?? nameOfExpression.GetLocation();

            _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(nameText, location);

            return ErrorExpression(null, symbol, reason: BoundExpressionReason.NotFound);
        }

        static SyntaxNode GetLeftMostNameNode(ExpressionSyntax operand)
        {
            // Handles:
            //  - nameof(x)
            //  - nameof(List<int>)
            //  - nameof(Console.WriteLine)
            //  - nameof(System.Console.WriteLine)
            //  - nameof(Foo.Console.WriteLine)

            SyntaxNode current = operand;

            // Walk down expression member access: A.B.C => A
            while (current is MemberAccessExpressionSyntax ma)
                current = ma.Expression;

            // Walk down qualified names: A.B.C => A
            while (current is QualifiedNameSyntax qn)
                current = qn.Left;

            // Some names may still be wrapped (e.g. parenthesized) though `nameof` operand is restricted.
            while (current is ParenthesizedExpressionSyntax paren)
                current = paren.Expression;

            return current;
        }

        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        return new BoundNameOfExpression(symbol, stringType);
    }

    private ISymbol? ResolveNameOfSymbol(ExpressionSyntax operand)
    {
        using var nonReportingScope = Diagnostics.CreateNonReportingScope();

        // `nameof` is special: we want to resolve *members* (method groups, fields, properties, events)
        // even when the parser produced a name node (QualifiedName) rather than a member-access node.
        // Prefer expression resolution first, then fall back to type resolution.

        // 1) If the operand is a qualified name, interpret it as a member-access expression chain.
        //    This makes `nameof(System.Console.WriteLine)` resolve to `WriteLine`, not `Console`.
        if (operand is QualifiedNameSyntax qn)
        {
            if (TryBindTypeSyntaxAsMemberAccessExpression(qn, out var boundExpression)
                && boundExpression is not BoundErrorExpression)
            {
                // For nameof we want the *symbol* represented by the final segment, not the expression's resulting type.
                // - Method groups: pick the selected method (or first candidate)
                // - Member access: the accessed symbol
                // - Type/namespace expressions: the type/namespace symbol
                if (boundExpression is BoundMethodGroupExpression mg)
                    return mg.SelectedMethod ?? mg.Methods.FirstOrDefault();

                var info = boundExpression.GetSymbolInfo();
                if (info.Symbol is not null)
                    return info.Symbol;

                if (boundExpression is BoundTypeExpression bt)
                    return bt.Type;

                if (boundExpression is BoundNamespaceExpression nsExpr)
                    return nsExpr.Namespace;
            }
        }

        // 2) Try expression binding (locals, parameters, and member access).
        {
            var sym = ResolveExpression(operand);
            if (sym is not null)
                return sym;
        }

        // 3) Resolve compile-time macro symbols without treating their provider
        // implementation type as the language-facing symbol.
        if (Compilation.GetMacroRegistry().TryResolveMacroSymbol(
                Compilation,
                operand,
                operand.ToString(),
                out var macroSymbol,
                out _))
        {
            CacheBoundNode(
                operand,
                new BoundErrorExpression(
                    Compilation.ErrorTypeSymbol,
                    macroSymbol,
                    BoundExpressionReason.None));
            return macroSymbol;
        }

        // 4) Fall back to type resolution for pure type operands like `List<int>`.
        if (operand is TypeSyntax typeSyntax)
        {
            if (TryBindTypeSyntaxAsMemberAccessExpression(typeSyntax, out var resolvedExpression))
            {
                return resolvedExpression.GetSymbolInfo().Symbol;
            }
        }

        return null;
    }

    private static ExpressionSyntax? ConvertQualifiedNameToMemberAccess(QualifiedNameSyntax qn)
    {
        // Convert A.B.C (QualifiedName chain) into ((A).B).C (member access chain).
        ExpressionSyntax? leftExpr = qn.Left switch
        {
            QualifiedNameSyntax nested => ConvertQualifiedNameToMemberAccess(nested),
            ExpressionSyntax e => e,
            _ => null
        };

        if (leftExpr is null)
            return null;

        // Right is usually IdentifierNameSyntax / GenericNameSyntax (SimpleNameSyntax).
        if (qn.Right is not SimpleNameSyntax right)
            return null;

        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            leftExpr,
            qn.DotToken,
            right);
    }

    private ISymbol? ResolveExpression(ExpressionSyntax exprSyntax)
    {
        // Bind the expression purely for symbol lookup and diagnostics.
        var bound = BindExpression(exprSyntax, allowReturn: false);

        // Method-group binding doesn't always populate SymbolInfo.Symbol (it can be ambiguous).
        // For `nameof(Console.WriteLine)` we still want the member name even if overloaded.
        if (bound is BoundMethodGroupExpression mg0)
            return mg0.SelectedMethod ?? mg0.Methods.FirstOrDefault();

        var info = bound.GetSymbolInfo();
        if (info.Symbol is not null)
            return info.Symbol;

        // Dotted chain: `nameof(Console.WriteLine)` or `nameof(Collections.Generic.List)`
        // Walk the member-access chain and try prefixes as `System.<prefix>` type names.
        if (exprSyntax is MemberAccessExpressionSyntax or QualifiedNameSyntax)
        {
            var parts = CollectDottedNameParts(exprSyntax);
            if (parts.Length >= 2)
            {
                // `parts` includes both the left-most identifier and the final member name.
                // Try prefixes excluding the final member, longest-first.
                for (int prefixLen = parts.Length - 1; prefixLen >= 1; prefixLen--)
                {
                    var typeName = "System." + string.Join(".", parts.Take(prefixLen));
                    var systemType = Compilation.GetTypeByMetadataName(typeName);
                    if (systemType is null || systemType.TypeKind == TypeKind.Error)
                        continue;

                    // Re-bind the remaining segments as member accesses on that type.
                    BoundExpression current = new BoundTypeExpression(systemType);

                    for (int i = prefixLen; i < parts.Length; i++)
                    {
                        var seg = parts[i];
                        var nameSyntax = SyntaxFactory.IdentifierName(seg);

                        var member = BindMemberAccessOnReceiver(
                            current,
                            nameSyntax,
                            // Prefer methods on the final segment so `Console.WriteLine` binds as a method group.
                            preferMethods: i == parts.Length - 1,
                            allowEventAccess: true,
                            suppressNullWarning: true,
                            receiverTypeForLookup: current.Type,
                            forceExtensionReceiver: true);

                        if (member is BoundMethodGroupExpression mg)
                            return mg.SelectedMethod ?? mg.Methods.FirstOrDefault();

                        var memberInfo = member.GetSymbolInfo();
                        if (memberInfo.Symbol is not null)
                        {
                            if (i == parts.Length - 1)
                                return memberInfo.Symbol;

                            // Continue chaining using a value-producing bound expression.
                            current = member;
                            continue;
                        }

                        // If we can't bind a segment, abandon this prefix.
                        current = null!;
                        break;
                    }

                    // If we successfully walked the chain, the loop would have returned.
                }
            }
        }

        return null;

        static SyntaxToken[] CollectDottedNameParts(ExpressionSyntax expr)
        {
            // Collect names from either an expression member-access chain `A.B.C`
            // or a qualified-name chain `A.B.C`.

            var names = new List<SyntaxToken>();

            void AddQualified(NameSyntax name)
            {
                switch (name)
                {
                    case QualifiedNameSyntax q:
                        AddQualified(q.Left);
                        AddQualified(q.Right);
                        break;
                    case IdentifierNameSyntax id:
                        names.Add(id.Identifier);
                        break;
                    case GenericNameSyntax gen:
                        names.Add(gen.Identifier);
                        break;
                    default:
                        //names.Add(name.ToString());
                        break;
                }
            }

            void AddExpression(ExpressionSyntax e)
            {
                switch (e)
                {
                    case MemberAccessExpressionSyntax ma:
                        AddExpression(ma.Expression);
                        // ma.Name is a SimpleNameSyntax
                        names.Add(ma.Name.Identifier);
                        break;
                    case IdentifierNameSyntax id:
                        names.Add(id.Identifier);
                        break;
                    case GenericNameSyntax gen:
                        names.Add(gen.Identifier);
                        break;
                    case QualifiedNameSyntax q:
                        AddQualified(q);
                        break;
                    default:
                        //names.Add(e.ToString());
                        break;
                }
            }

            AddExpression(expr);
            return names.ToArray();
        }
    }

    private BoundExpression ConvertMethodGroupToDelegate(BoundMethodGroupExpression methodGroup, ITypeSymbol targetType, SyntaxNode? syntax)
    {
        if (targetType is not INamedTypeSymbol delegateType || delegateType.TypeKind != TypeKind.Delegate)
            return methodGroup;

        var invoke = delegateType.GetDelegateInvokeMethod();
        if (invoke is null)
        {
            var fallbackGroup = new BoundMethodGroupExpression(
                methodGroup.Receiver,
                methodGroup.Methods,
                methodGroup.MethodGroupType,
                delegateTypeFactory: () => targetType,
                selectedMethod: methodGroup.SelectedMethod,
                reason: methodGroup.Reason != BoundExpressionReason.None ? methodGroup.Reason : BoundExpressionReason.OverloadResolutionFailed);

            return new BoundDelegateCreationExpression(fallbackGroup, delegateType);
        }

        var compatibleMethods = methodGroup.Methods
            .Select(candidate => GetMethodCompatibleWithDelegate(candidate, invoke, methodGroup.Receiver is not null))
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .ToImmutableArray();

        var selectedMethod = methodGroup.SelectedMethod;
        var computedReason = BoundExpressionReason.None;

        if (compatibleMethods.Length == 1)
        {
            selectedMethod = compatibleMethods[0];
        }
        else if (compatibleMethods.IsDefaultOrEmpty)
        {
            selectedMethod = null;
            computedReason = BoundExpressionReason.OverloadResolutionFailed;
        }
        else
        {
            var resolution = ResolveMethodGroupOverloadForDelegate(compatibleMethods, invoke, methodGroup.Receiver);

            if (resolution.Success)
            {
                selectedMethod = resolution.Method;
            }
            else if (resolution.IsAmbiguous)
            {
                selectedMethod = null;
                computedReason = BoundExpressionReason.Ambiguous;
            }
            else
            {
                selectedMethod = null;
                computedReason = BoundExpressionReason.OverloadResolutionFailed;
            }
        }

        var reason = methodGroup.Reason != BoundExpressionReason.None ? methodGroup.Reason : computedReason;
        var reportReason = methodGroup.Reason == BoundExpressionReason.None ? computedReason : BoundExpressionReason.None;

        var resolvedGroup = new BoundMethodGroupExpression(
            methodGroup.Receiver,
            methodGroup.Methods,
            methodGroup.MethodGroupType,
            delegateTypeFactory: () => targetType,
            selectedMethod: selectedMethod,
            reason: reason);

        if (syntax is not null)
        {
            if (reportReason is BoundExpressionReason.Ambiguous)
            {
                _diagnostics.ReportMethodGroupConversionIsAmbiguous(GetMethodGroupDisplay(methodGroup), syntax.GetLocation());
            }
            else if (reportReason is BoundExpressionReason.OverloadResolutionFailed)
            {
                _diagnostics.ReportNoOverloadMatchesDelegate(
                    GetMethodGroupDisplay(methodGroup),
                    delegateType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    syntax.GetLocation());
            }
        }

        return new BoundDelegateCreationExpression(resolvedGroup, delegateType);
    }

    private OverloadResolutionResult ResolveMethodGroupOverloadForDelegate(
        ImmutableArray<IMethodSymbol> methods,
        IMethodSymbol invoke,
        BoundExpression? receiver)
    {
        var arguments = new BoundArgument[invoke.Parameters.Length];
        for (var i = 0; i < invoke.Parameters.Length; i++)
        {
            var parameter = invoke.Parameters[i];
            var expression = new BoundDefaultValueExpression(parameter.Type);
            arguments[i] = new BoundArgument(expression, parameter.RefKind, name: null);
        }

        return OverloadResolver.ResolveOverload(methods, arguments, Compilation, binder: this, receiver: receiver);
    }

    private IMethodSymbol? GetMethodCompatibleWithDelegate(IMethodSymbol method, IMethodSymbol invoke, bool hasReceiver)
    {
        if (!hasReceiver && !method.IsStatic)
            return null;

        method = OverloadResolver.TryConstructMethodGroupCandidate(method, invoke, Compilation, this) ?? method;

        if (method.IsGenericMethod &&
            method.TypeArguments.Any(static typeArgument => typeArgument is ITypeParameterSymbol))
        {
            return null;
        }

        if (method.Parameters.Length != invoke.Parameters.Length)
            return null;

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var methodParameter = method.Parameters[i];
            var delegateParameter = invoke.Parameters[i];

            if (methodParameter.RefKind != delegateParameter.RefKind)
                return null;

            if (SymbolEqualityComparer.Default.Equals(delegateParameter.Type, methodParameter.Type))
                continue;

            var conversion = Compilation.ClassifyConversion(delegateParameter.Type, methodParameter.Type);
            if (!conversion.Exists || !conversion.IsImplicit)
                return null;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, invoke.ReturnType))
            return method;

        var returnConversion = Compilation.ClassifyConversion(method.ReturnType, invoke.ReturnType);
        return returnConversion.Exists && returnConversion.IsImplicit ? method : null;
    }

    private static ImmutableArray<ISymbol> AsSymbolCandidates(ImmutableArray<IMethodSymbol> methods)
    {
        if (methods.IsDefaultOrEmpty)
            return ImmutableArray<ISymbol>.Empty;

        return ImmutableArray.CreateRange(methods, static m => (ISymbol)m);
    }

    private BoundExpression BindAsExpression(AsExpressionSyntax asExpression)
    {
        var expression = BindExpression(asExpression.Expression);
        var targetType = ResolveTypeSyntaxOrError(asExpression.Type);

        if (HasExpressionErrors(expression))
            return expression;
        if (targetType.ContainsErrorType())
            return ErrorExpression(targetType.GetNullableType(), reason: BoundExpressionReason.NotFound);

        if (expression.Type!.IsValueType || targetType.IsValueType)
        {
            ReportCannotConvertFromTypeToType(
                expression.Type!.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                targetType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                asExpression.GetLocation());
            var errorType = targetType.GetNullableType();
            return new BoundErrorExpression(errorType, null, BoundExpressionReason.TypeMismatch);
        }

        var conversion = Compilation.ClassifyConversion(expression.Type!, targetType);
        if (!conversion.Exists || conversion.IsNumeric || conversion.IsUserDefined)
        {
            ReportCannotConvertFromTypeToType(
                expression.Type!.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                targetType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
                asExpression.GetLocation());
            var errorType = targetType.GetNullableType();
            return new BoundErrorExpression(errorType, null, BoundExpressionReason.TypeMismatch);
        }

        var resultType = targetType.GetNullableType();
        return new BoundAsExpression(expression, resultType, conversion);
    }

    private BoundStatement BindMatchStatement(MatchStatementSyntax matchStatement)
    {
        var isImplicitReturn = IsMatchStatementImplicitReturn(matchStatement);
        var armTargetType = isImplicitReturn && _containingSymbol is IMethodSymbol method
            ? GetReturnTargetType(method)
            : null;

        var (scrutinee, arms) = BindMatchCommon(
            matchStatement,
            matchStatement.Expression,
            matchStatement.Arms,
            allowReturnInArmExpressions: true,
            requireArmValue: false,
            armTargetType: armTargetType);

        if (!isImplicitReturn &&
            _containingSymbol is IMethodSymbol containingMethod &&
            GetReturnTargetType(containingMethod) is { } methodReturnType &&
            methodReturnType.SpecialType is not SpecialType.System_Void and not SpecialType.System_Unit &&
            HasIgnoredValueProducingMatchArm(arms))
        {
            _diagnostics.ReportMatchStatementValueIgnored(matchStatement.MatchKeyword.GetLocation());
        }

        // When a match statement is the last statement in a function body that
        // has a non-void return type, and at least one arm produces a value
        // (i.e. it is not an explicit return/throw), promote it to a
        // BoundMatchExpression wrapped in an ExpressionStatement so that the
        // method body's implicit-return logic can emit `ret` for it.
        if (isImplicitReturn)
        {
            var hasContributingArm = arms.Any(arm => !BoundNodeFacts.IsAbruptExpression(arm.Expression));

            if (hasContributingArm)
            {
                var contributingArmTypes = arms
                    .Select(arm => arm.Expression)
                    .Where(static expression => !BoundNodeFacts.IsAbruptExpression(expression))
                    .Select(expression => expression.Type ?? Compilation.ErrorTypeSymbol)
                    .ToArray();

                var resultType = TypeSymbolNormalization.GetBestCommonType(
                    contributingArmTypes,
                    errorTypeSymbol: Compilation.ErrorTypeSymbol);
                var matchExpr = new BoundMatchExpression(scrutinee, arms, resultType);
                return new BoundExpressionStatement(matchExpr);
            }
        }

        return new BoundMatchStatement(scrutinee, arms);
    }

    private bool IsMatchStatementImplicitReturn(MatchStatementSyntax matchStatement)
    {
        if (_containingSymbol is not IMethodSymbol method)
            return false;

        var returnType = GetReturnTargetType(method);
        if (returnType is null ||
            returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
            return false;

        if (matchStatement.Parent is BlockStatementSyntax blockStatement)
        {
            if (blockStatement.Statements.Count == 0 || blockStatement.Statements.LastOrDefault() != matchStatement)
                return false;

            return blockStatement.Parent switch
            {
                BaseMethodDeclarationSyntax => true,
                FunctionStatementSyntax => true,
                AccessorDeclarationSyntax => true,
                FunctionExpressionSyntax => true,
                _ => false,
            };
        }

        if (matchStatement.Parent is BlockSyntax blockExpression)
        {
            if (blockExpression.Statements.Count == 0 || blockExpression.Statements.LastOrDefault() != matchStatement)
                return false;

            return blockExpression.Parent switch
            {
                BaseMethodDeclarationSyntax => true,
                FunctionStatementSyntax => true,
                AccessorDeclarationSyntax => true,
                FunctionExpressionSyntax => true,
                _ => false,
            };
        }

        return false;
    }

    private static bool HasIgnoredValueProducingMatchArm(ImmutableArray<BoundMatchArm> arms)
    {
        foreach (var arm in arms)
        {
            var expression = arm.Expression;
            if (BoundNodeFacts.IsAbruptExpression(expression) || HasExpressionErrors(expression))
                continue;

            var type = expression.Type;
            if (type is null || type.SpecialType is SpecialType.System_Void or SpecialType.System_Unit)
                continue;

            return true;
        }

        return false;
    }

    private BoundExpression BindMatchExpression(
        SyntaxNode matchExpression,
        ExpressionSyntax expression,
        SyntaxList<MatchArmSyntax> matchArms)
    {
        var armTargetType = GetTargetType(matchExpression);

        var (scrutinee, arms) = BindMatchCommon(
            matchExpression,
            expression,
            matchArms,
            allowReturnInArmExpressions: _allowReturnsInExpression,
            requireArmValue: true,
            armTargetType: armTargetType);
        var contributingArmTypes = arms
            .Select(arm => arm.Expression)
            .Where(static expression => !BoundNodeFacts.IsAbruptExpression(expression))
            .Select(expression => expression.Type ?? Compilation.ErrorTypeSymbol)
            .ToArray();

        // If every arm is abrupt (e.g., all return/throw), retain legacy behavior.
        if (contributingArmTypes.Length == 0)
        {
            contributingArmTypes = arms
                .Select(arm => arm.Expression.Type ?? Compilation.ErrorTypeSymbol)
                .ToArray();
        }

        var resultType = TypeSymbolNormalization.GetBestCommonType(
            contributingArmTypes,
            errorTypeSymbol: Compilation.ErrorTypeSymbol);

        return new BoundMatchExpression(scrutinee, arms, resultType);
    }

    private (BoundExpression Scrutinee, ImmutableArray<BoundMatchArm> Arms) BindMatchCommon(
        SyntaxNode matchSyntax,
        ExpressionSyntax scrutineeSyntax,
        SyntaxList<MatchArmSyntax> armSyntaxes,
        bool allowReturnInArmExpressions,
        bool requireArmValue,
        ITypeSymbol? armTargetType)
    {
        var scrutinee = BindExpression(scrutineeSyntax);

        var armBuilder = ImmutableArray.CreateBuilder<BoundMatchArm>();

        foreach (var arm in armSyntaxes)
        {
            _scopeDepth++;
            var depth = _scopeDepth;

            var inlineBindingKeyword = FindFirstInlinePatternBindingKeyword(arm.Pattern);
            if (inlineBindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword &&
                arm.BindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            {
                _diagnostics.ReportPatternDeclarationBindingKeywordConflict(
                    arm.BindingKeyword.Text,
                    inlineBindingKeyword.Text,
                    inlineBindingKeyword.GetLocation());
            }

            var previousBindingKeyword = _ambientPatternDeclarationBindingKeyword;
            _ambientPatternDeclarationBindingKeyword = arm.BindingKeyword.Kind;
            BoundPattern pattern;
            try
            {
                pattern = BindPattern(arm.Pattern, scrutinee.Type);
            }
            finally
            {
                _ambientPatternDeclarationBindingKeyword = previousBindingKeyword;
            }

            // ✅ Make locals introduced by the pattern visible to `when` + arm expression
            RegisterPatternDesignatorLocals(pattern, depth);

            BoundExpression? guard = null;
            if (arm.WhenClause is { } whenClause && whenClause.Guard is ExpressionSyntax guardSyntax)
                guard = BindExpression(guardSyntax);

            var expressionTargetType = armTargetType;

            var expression = !requireArmValue && arm.Expression is BlockSyntax statementArmBlock
                ? BindBlock(statementArmBlock, allowReturn: true, isExpressionContext: false)
                : BindExpressionWithTargetType(
                    arm.Expression,
                    expressionTargetType,
                    allowReturn: allowReturnInArmExpressions);

            // Case-only arm expressions like `None` can bind as open generic case carriers
            // (`Option<T>`) when no explicit target exists. If the arm pattern already
            // establishes the scrutinee union, retarget only union-case expressions to that
            // concrete scrutinee carrier.
            if (expressionTargetType is null &&
                expression is BoundUnionCaseExpression &&
                scrutinee.Type is INamedTypeSymbol scrutineeUnion &&
                TryGetPatternUnion(pattern) is INamedTypeSymbol caseUnion &&
                ((ITypeSymbol)(scrutineeUnion.OriginalDefinition ?? scrutineeUnion)).MetadataIdentityEquals(
                    caseUnion.OriginalDefinition ?? caseUnion))
            {
                expressionTargetType = scrutineeUnion;
                RemoveCachedBoundNode(arm.Expression);
                expression = BindExpressionWithTargetType(
                    arm.Expression,
                    expressionTargetType,
                    allowReturn: allowReturnInArmExpressions);
            }

            if (expressionTargetType is not null &&
                expressionTargetType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(expression))
            {
                expression = BindLambdaToDelegateIfNeeded(expression, expressionTargetType);

                var sourceType = expression.Type;
                if (sourceType is not null &&
                    !SymbolEqualityComparer.Default.Equals(sourceType, expressionTargetType))
                {
                    if (!IsAssignable(expressionTargetType, sourceType, out var conversion))
                    {
                        ReportCannotConvertFromTypeToType(
                            sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            expressionTargetType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            arm.Expression.GetLocation());
                        expression = new BoundErrorExpression(expressionTargetType, null, BoundExpressionReason.TypeMismatch);
                    }
                    else
                    {
                        expression = ApplyConversion(expression, expressionTargetType, conversion, arm.Expression);
                    }
                }
            }

            if (requireArmValue)
                expression = expression.RequireValue();

            foreach (var name in _locals.Where(kvp => kvp.Value.Depth == depth).Select(kvp => kvp.Key).ToList())
                _locals.Remove(name);

            _scopeDepth--;

            armBuilder.Add(new BoundMatchArm(pattern, guard, expression));
        }

        var arms = armBuilder.ToImmutable();

        EnsureMatchArmPatternsValid(scrutinee, matchSyntax, armSyntaxes, arms);
        EnsureMatchArmOrder(matchSyntax, armSyntaxes, scrutinee, arms);
        EnsureMatchExhaustive(matchSyntax, armSyntaxes, scrutinee, arms);
        return (scrutinee, arms);

        // Local helper: take locals carried by the bound pattern and register them in this arm scope.
        void RegisterPatternDesignatorLocals(BoundPattern boundPattern, int depth)
        {
            foreach (var designator in boundPattern.GetDesignators())
            {
                if (designator is BoundSingleVariableDesignator single)
                {
                    var local = single.Local;
                    _locals[local.Name] = (local, depth);
                }
            }
        }

        static IUnionSymbol? TryGetPatternUnion(BoundPattern boundPattern)
            => boundPattern switch
            {
                BoundCasePattern casePattern => casePattern.CaseSymbol.Union,
                BoundUnionMemberPattern memberPattern => memberPattern.MemberType.TryGetUnionCase()?.Union,
                BoundDeclarationPattern declarationPattern => declarationPattern.Type.TryGetUnionCase()?.Union,
                _ => null,
            };
    }

    private BoundExpression BindTryExpression(TryExpressionSyntax tryExpression)
    {
        if (tryExpression.Expression is TryExpressionSyntax nestedTry)
        {
            _diagnostics.ReportTryExpressionCannotBeNested(nestedTry.TryKeyword.GetLocation());
            return ErrorExpression();
        }

        var expression = BindExpression(tryExpression.Expression);
        var exceptionType = Compilation.GetTypeByMetadataName("System.Exception") ?? Compilation.ErrorTypeSymbol;
        var expressionType = expression.Type ?? Compilation.ErrorTypeSymbol;

        var resultDefinition = Compilation.GetTypeByMetadataName("System.Result`2") as INamedTypeSymbol;
        if (resultDefinition is null)
            return ErrorExpression();

        var resultType = (INamedTypeSymbol?)resultDefinition.Construct(expressionType, exceptionType);

        var union = resultType.TryGetUnion();
        if (union is null)
            return ErrorExpression();

        var okCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "Ok");
        var errorCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "Error");
        if (okCase is null || errorCase is null)
            return ErrorExpression();

        var okConstructor = okCase.Constructors.FirstOrDefault(ctor => ctor.Parameters.Length == okCase.ConstructorParameters.Length);
        var errorConstructor = errorCase.Constructors.FirstOrDefault(ctor => ctor.Parameters.Length == errorCase.ConstructorParameters.Length);
        if (okConstructor is null || errorConstructor is null)
            return ErrorExpression();

        var boundTry = new BoundTryExpression(expression, exceptionType, resultType, okConstructor, errorConstructor);

        return boundTry;
    }

    private BoundExpression BindReturnExpression(ReturnExpressionSyntax returnExpression)
    {
        if (returnExpression.Expression is null)
            ResolveIteratorInfoForCurrentMethod();

        var returnValue = BindReturnValue(returnExpression.Expression, returnExpression);

        var targetType = GetTargetType(returnExpression);
        if (targetType is NullableTypeSymbol nullableTargetType)
            targetType = nullableTargetType.UnderlyingType;

        targetType ??= _containingSymbol switch
        {
            IMethodSymbol method => GetReturnTargetType(method),
            _ => null,
        };

        targetType ??= Compilation.UnitTypeSymbol;

        return new BoundReturnExpression(returnValue, targetType);
    }

    private BoundExpression BindThrowExpression(ThrowExpressionSyntax throwExpression)
    {
        var exceptionBase = Compilation.GetTypeByMetadataName("System.Exception")
            ?? Compilation.ErrorTypeSymbol;

        var expression = BindExpression(throwExpression.Expression);
        expression = BindThrowValueExpression(expression, throwExpression.Expression, exceptionBase);

        var targetType = GetTargetType(throwExpression);
        var resultType = targetType is not null && targetType.TypeKind != TypeKind.Error
            ? targetType
            : Compilation.UnitTypeSymbol;

        return new BoundThrowExpression(expression, resultType);
    }

    private BoundExpression BindBreakExpression(BreakExpressionSyntax breakExpression)
    {
        var targetLabel = BindControlFlowLabel(breakExpression.Identifier, breakExpression);
        if (IsMissingControlFlowLabel(breakExpression.Identifier) && _loopDepth == 0)
            _diagnostics.ReportBreakStatementNotWithinLoop(breakExpression.BreakKeyword.GetLocation());

        return new BoundBreakExpression(targetLabel, GetAbruptExpressionType(breakExpression));
    }

    private BoundExpression BindContinueExpression(ContinueExpressionSyntax continueExpression)
    {
        var targetLabel = BindControlFlowLabel(continueExpression.Identifier, continueExpression);
        if (IsMissingControlFlowLabel(continueExpression.Identifier) && _loopDepth == 0)
            _diagnostics.ReportContinueStatementNotWithinLoop(continueExpression.ContinueKeyword.GetLocation());

        return new BoundContinueExpression(targetLabel, GetAbruptExpressionType(continueExpression));
    }

    private BoundExpression BindYieldExpression(YieldExpressionSyntax yieldExpression)
    {
        if (yieldExpression.Expression is BreakExpressionSyntax)
        {
            ResolveIteratorInfoForCurrentMethod();
            return new BoundReturnExpression(null, GetAbruptExpressionType(yieldExpression));
        }

        var expressionSyntax = yieldExpression.Expression is ReturnExpressionSyntax { Expression: { } recoveredExpression }
            ? recoveredExpression
            : yieldExpression.Expression;
        var expression = BindExpression(expressionSyntax);
        var (kind, elementType) = ResolveIteratorInfoForCurrentMethod();

        if (elementType.TypeKind == TypeKind.Error)
            elementType = Compilation.ErrorTypeSymbol;

        var isDelegating = yieldExpression.FromKeyword.Kind != SyntaxKind.None;
        var iteration = isDelegating ? BindYieldFromIteration(expression, expressionSyntax, kind, elementType) : null;
        if (!isDelegating)
            expression = BindYieldValueConversion(expression, elementType, expressionSyntax);

        return new BoundYieldExpression(expression, elementType, kind, Compilation.UnitTypeSymbol, iteration);
    }

    private BoundExpression BindYieldValueConversion(
        BoundExpression expression,
        ITypeSymbol elementType,
        ExpressionSyntax expressionSyntax)
    {
        if (!ShouldAttemptConversion(expression) ||
            expression.Type is not { TypeKind: not TypeKind.Error } expressionType ||
            elementType.TypeKind == TypeKind.Error)
        {
            return expression;
        }

        if (!IsAssignable(elementType, expressionType, out var conversion))
        {
            ReportCannotConvertExpressionToType(expression, elementType, expressionSyntax.GetLocation());
            ReportExplicitConversionHint(expressionType, elementType, expressionSyntax.GetLocation());
            return expression;
        }

        return ApplyConversion(expression, elementType, conversion, expressionSyntax);
    }

    private ITypeSymbol GetAbruptExpressionType(ExpressionSyntax expression)
    {
        var targetType = GetTargetType(expression);
        if (targetType is NullableTypeSymbol nullableTargetType)
            targetType = nullableTargetType.UnderlyingType;

        return targetType is { TypeKind: not TypeKind.Error }
            ? targetType
            : Compilation.UnitTypeSymbol;
    }

    private BoundExpression BindPropagateExpression(PropagateExpressionSyntax propagateExpression)
    {
        // Bind the operand in a "pure" expression context: explicit `return` is not allowed here.
        var operand = BindExpression(propagateExpression.Expression, allowReturn: false);

        return BindPropagateExpressionCore(operand, propagateExpression.QuestionToken, propagateExpression);
    }

    private BoundExpression BindPropagateExpressionCore(
        BoundExpression operand,
        SyntaxToken questionToken,
        SyntaxNode callSyntax)
    {
        if (operand is BoundErrorExpression)
            return operand;

        var operandType = UnwrapAlias(operand.Type ?? Compilation.ErrorTypeSymbol);
        if (operandType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        if (operandType is not INamedTypeSymbol operandNamed || !TryGetPropagationInfo(operandNamed, out var operandInfo))
        {
            _diagnostics.ReportOperatorCannotBeAppliedToOperandOfType(
                "?",
                operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                questionToken.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        if (!TryGetEnclosingCarrierReturnType(out var enclosingReturnType))
        {
            _diagnostics.ReportOperatorCannotBeAppliedToOperandOfType(
                "?",
                operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                questionToken.GetLocation());

            if (enclosingReturnType is not null && enclosingReturnType.TypeKind != TypeKind.Error)
            {
                ReportCannotConvertFromTypeToType(
                    enclosingReturnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    operandInfo.UnionType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    questionToken.GetLocation());
            }

            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        if (!TryGetPropagationInfo(enclosingReturnType, out var enclosingInfo) ||
            !PropagationKindsAreCompatible(operandInfo, enclosingInfo))
        {
            _diagnostics.ReportOperatorCannotBeAppliedToOperandOfType(
                "?",
                operandType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                questionToken.GetLocation());

            ReportCannotConvertFromTypeToType(
                operandInfo.UnionType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                enclosingReturnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                questionToken.GetLocation());

            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var okType = operandInfo.OkPayloadType;
        var errorType = operandInfo.ErrorPayloadType;

        // If the Ok case exposes the payload via a `Value` property, record it.
        IPropertySymbol? okValueProperty = null;
        if (operandInfo.OkCaseType is INamedTypeSymbol okCaseNamed)
        {
            okValueProperty = okCaseNamed.GetMembers("Value").OfType<IPropertySymbol>().FirstOrDefault();
        }

        var enclosingErrorCase = enclosingInfo.ErrorCase;
        var enclosingErrorConstructor = enclosingInfo.ResidualFactoryMethod
            ?? enclosingErrorCase?.Constructors.FirstOrDefault(ctor =>
                ctor.Parameters.Length == enclosingErrorCase.ConstructorParameters.Length);
        if (enclosingErrorConstructor is null)
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);

        var errorConversion = default(Conversion);
        if (operandInfo.ErrorPayloadType is not null && enclosingInfo.ErrorPayloadType is not null)
        {
            errorConversion = Compilation.ClassifyConversion(operandInfo.ErrorPayloadType, enclosingInfo.ErrorPayloadType);
            if (!errorConversion.Exists)
            {
                ReportCannotConvertFromTypeToType(
                    operandInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    enclosingInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    questionToken.GetLocation());
            }
            else if (errorConversion.IsImplicit &&
                     !errorConversion.IsIdentity &&
                     errorConversion.IsUserDefined &&
                     errorConversion.MethodSymbol is { } conversionMethod)
            {
                _diagnostics.ReportResultPropagationImplicitErrorConversion(
                    operandInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    enclosingInfo.ErrorPayloadType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    conversionMethod.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    questionToken.GetLocation());
            }
        }

        // Lowering/codegen will branch, extract the error payload when available, convert if needed, and early-return.
        return new BoundPropagateExpression(
            operand,
            okType,
            errorType,
            enclosingInfo.UnionType,
            enclosingErrorConstructor,
            okCaseName: operandInfo.OkCaseName,
            errorCaseName: operandInfo.ErrorCaseName,
            errorCaseHasPayload: operandInfo.ErrorCaseHasPayload,
            okCaseType: operandInfo.OkCaseType,
            errorCaseType: operandInfo.ErrorCase,
            okValueProperty: okValueProperty,
            unwrapErrorMethod: null,
            errorConversion: errorConversion,
            tryGetOutputMethod: operandInfo.TryGetOutputMethod,
            tryGetResidualMethod: operandInfo.TryGetResidualMethod);
    }

    private bool TryGetEnclosingCarrierReturnType(out INamedTypeSymbol? enclosingReturnType)
    {
        enclosingReturnType = null;

        ITypeSymbol? declaredReturnType = null;
        var enclosingIsAsync = false;
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (current.ContainingSymbol is IMethodSymbol method)
            {
                declaredReturnType = method.ReturnType;
                enclosingIsAsync = method.IsAsync;
                break;
            }

            if (current.ContainingSymbol is ILambdaSymbol lambda)
            {
                declaredReturnType = lambda.ReturnType;
                enclosingIsAsync = lambda.IsAsync;
                break;
            }
        }

        if (declaredReturnType is null)
            return false;

        var effectiveReturnType = UnwrapAlias(declaredReturnType);

        if (enclosingIsAsync)
        {
            effectiveReturnType = AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, effectiveReturnType)
                ?? effectiveReturnType;
        }

        enclosingReturnType = effectiveReturnType as INamedTypeSymbol;
        return enclosingReturnType is not null;
    }

    private enum PropagationKind
    {
        Contract,
        Result,
        Option
    }

    private sealed record PropagationInfo(
        PropagationKind Kind,
        INamedTypeSymbol UnionType,
        IUnionSymbol? Union,
        IUnionCaseTypeSymbol? OkCase,
        IUnionCaseTypeSymbol? ErrorCase,
        ITypeSymbol OkPayloadType,
        ITypeSymbol? ErrorPayloadType,
        ITypeSymbol? OkCaseType,
        string OkCaseName,
        string ErrorCaseName,
        bool ErrorCaseHasPayload,
        IMethodSymbol? TryGetOutputMethod = null,
        IMethodSymbol? TryGetResidualMethod = null,
        IMethodSymbol? ResidualFactoryMethod = null,
        bool UsesContract = false);

    private static bool PropagationKindsAreCompatible(PropagationInfo operand, PropagationInfo enclosing)
        => operand.Kind == enclosing.Kind || (operand.UsesContract && enclosing.UsesContract);

    private static bool TryGetPropagationInfo(INamedTypeSymbol typeSymbol, out PropagationInfo info)
    {
        info = null!;

        var propagationInterface = typeSymbol.AllInterfaces.FirstOrDefault(@interface =>
            @interface.ContainingNamespace?.ToDisplayString() == "System" &&
            @interface.MetadataName == "IPropagatable`3" &&
            @interface.TypeArguments.Length == 3 &&
            SymbolEqualityComparer.Default.Equals(@interface.TypeArguments[0], typeSymbol));

        if (propagationInterface is not null)
        {
            var outputType = propagationInterface.TypeArguments[1];
            var residualType = propagationInterface.TypeArguments[2];
            var tryGetOutputMethod = typeSymbol.GetMembers("TryGetOutput").OfType<IMethodSymbol>().FirstOrDefault(method =>
                !method.IsStatic &&
                method.Parameters.Length == 1 &&
                method.Parameters[0].RefKind == RefKind.Out &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].GetByRefElementType(), outputType));
            var tryGetResidualMethod = typeSymbol.GetMembers("TryGetResidual").OfType<IMethodSymbol>().FirstOrDefault(method =>
                !method.IsStatic &&
                method.Parameters.Length == 1 &&
                method.Parameters[0].RefKind == RefKind.Out &&
                method.ReturnType.SpecialType == SpecialType.System_Boolean &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].GetByRefElementType(), residualType));
            var fromResidualMethod = typeSymbol.GetMembers("FromResidual").OfType<IMethodSymbol>().FirstOrDefault(method =>
                method.IsStatic &&
                method.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, residualType) &&
                SymbolEqualityComparer.Default.Equals(method.ReturnType, typeSymbol));

            if (tryGetOutputMethod is not null && tryGetResidualMethod is not null && fromResidualMethod is not null)
            {
                var kind = typeSymbol.Name switch
                {
                    "Result" => PropagationKind.Result,
                    "Option" => PropagationKind.Option,
                    _ => PropagationKind.Contract,
                };

                info = new PropagationInfo(
                    kind,
                    typeSymbol,
                    Union: null,
                    OkCase: null,
                    ErrorCase: null,
                    outputType,
                    residualType,
                    OkCaseType: null,
                    OkCaseName: "Output",
                    ErrorCaseName: "Residual",
                    ErrorCaseHasPayload: true,
                    tryGetOutputMethod,
                    tryGetResidualMethod,
                    fromResidualMethod,
                    UsesContract: true);

                return true;
            }
        }

        if (!UnionFacts.UsesCarrierRepresentation(typeSymbol))
            return false;

        var union = typeSymbol.TryGetUnion();
        if (union is null)
            return false;

        if (typeSymbol.Name == "Result")
        {
            var okCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "Ok");
            var errorCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "Error");
            if (okCase is null || errorCase is null)
                return false;

            if (okCase.ConstructorParameters.Length != 1 || errorCase.ConstructorParameters.Length != 1)
                return false;

            info = new PropagationInfo(
                PropagationKind.Result,
                typeSymbol,
                union,
                okCase,
                errorCase,
                okCase.ConstructorParameters[0].Type,
                errorCase.ConstructorParameters[0].Type,
                okCase,
                okCase.Name,
                errorCase.Name,
                ErrorCaseHasPayload: true);
            return true;
        }

        if (typeSymbol.Name == "Option")
        {
            var okCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "Some");
            var errorCase = union.DeclaredCaseTypes.FirstOrDefault(@case => @case.Name == "None");
            if (okCase is null || errorCase is null)
                return false;

            if (okCase.ConstructorParameters.Length != 1 || errorCase.ConstructorParameters.Length != 0)
                return false;

            info = new PropagationInfo(
                PropagationKind.Option,
                typeSymbol,
                union,
                okCase,
                errorCase,
                okCase.ConstructorParameters[0].Type,
                ErrorPayloadType: null,
                okCase,
                okCase.Name,
                errorCase.Name,
                ErrorCaseHasPayload: false);
            return true;
        }

        return false;
    }

    private void EnsureMatchArmPatternsValid(
        BoundExpression scrutinee,
        SyntaxNode matchSyntax,
        SyntaxList<MatchArmSyntax> armSyntaxes,
        ImmutableArray<BoundMatchArm> arms)
    {
        if (scrutinee.Type is not ITypeSymbol scrutineeType)
            return;

        scrutineeType = UnwrapAlias(scrutineeType);

        if (scrutineeType.TypeKind == TypeKind.Error)
            return;

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];
            var patternSyntax = armSyntaxes[i].Pattern;
            EnsureMatchArmPatternValid(scrutineeType, patternSyntax, arm.Pattern);
        }
    }

    private void EnsureMatchArmPatternValid(
        ITypeSymbol scrutineeType,
        PatternSyntax patternSyntax,
        BoundPattern pattern)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                return;
            case BoundGuardedPattern guardedPattern:
                {
                    var innerSyntax = patternSyntax is GuardedPatternSyntax guardedSyntax
                        ? guardedSyntax.Pattern
                        : patternSyntax;
                    EnsureMatchArmPatternValid(scrutineeType, innerSyntax, guardedPattern.Pattern);
                    return;
                }
            case BoundDeclarationPattern declaration:
                {
                    var patternType = UnwrapAlias(declaration.DeclaredType);

                    if (patternType.TypeKind == TypeKind.Error)
                        return;

                    // `null` is parsed as a type in some contexts (e.g. `null => ...`).
                    // Treat it as a constant null pattern: it is valid whenever the scrutinee can be null.
                    if (patternType is NullTypeSymbol)
                    {
                        if (CanBeNull(scrutineeType))
                            return;

                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            "null",
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            patternSyntax.GetLocation());
                        return;
                    }

                    if (PatternCanMatch(scrutineeType, patternType))
                        return;

                    var patternDisplay = GetMatchPatternDisplay(patternType);
                    var location = patternSyntax switch
                    {
                        DeclarationPatternSyntax declarationSyntax => declarationSyntax.Type.GetLocation(),
                        _ => patternSyntax.GetLocation(),
                    };

                    _diagnostics.ReportMatchExpressionArmPatternInvalid(
                        patternDisplay,
                        scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        location);
                    return;
                }
            case BoundConstantPattern constant:
                {
                    // Null patterns are unreachable when the scrutinee is non-nullable.
                    // `IsNullable` covers both reference and value types.
                    if (constant.Expression is null && constant.ConstantValue is null && !CanBeNull(scrutineeType))
                    {
                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            "null",
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            patternSyntax.GetLocation());
                        return;
                    }
                    // Literal-backed constant pattern
                    if (constant.LiteralType is not null)
                    {
                        var underlyingType = UnwrapAlias(constant.LiteralType.UnderlyingType);

                        if (underlyingType.TypeKind == TypeKind.Error)
                            return;

                        if (PatternCanMatch(scrutineeType, underlyingType))
                            return;

                        var patternDisplay = GetMatchPatternDisplay(constant.LiteralType);
                        var location = patternSyntax.GetLocation();

                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            patternDisplay,
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            location);
                        return;
                    }

                    // Expression-backed value pattern
                    if (constant.Expression is not null)
                    {
                        var valueType = constant.Expression.Type ?? Compilation.ErrorTypeSymbol;
                        valueType = UnwrapAlias(valueType);

                        if (valueType.TypeKind == TypeKind.Error)
                            return;

                        if (PatternCanMatch(scrutineeType, valueType))
                            return;

                        var patternDisplay = GetMatchPatternDisplay(valueType);
                        var location = patternSyntax.GetLocation();

                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            patternDisplay,
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            location);
                        return;
                    }

                    // Defensive fallback: should never happen
                    return;
                }
            case BoundUnionMemberPattern unionMemberPattern:
                {
                    EnsureMatchArmPatternValid(unionMemberPattern.MemberType, patternSyntax, unionMemberPattern.Pattern);
                    return;
                }
            case BoundCasePattern casePattern:
                {
                    var scrutineePatternType = scrutineeType.GetNonNullableType();
                    var scrutineeUnion = scrutineePatternType.TryGetUnion()
                        ?? scrutineePatternType.TryGetUnionCase()?.Union;

                    var caseUnion = UnwrapAlias(casePattern.CaseSymbol.Union);

                    if (scrutineeUnion is null || !AreSameUnionPatternTarget(UnwrapAlias(scrutineeUnion), caseUnion))
                    {
                        var patternDisplay = $"for case '{casePattern.CaseSymbol.Name}'";
                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            patternDisplay,
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            patternSyntax.GetLocation());
                        return;
                    }

                    if (patternSyntax is MemberPatternSyntax caseSyntax && caseSyntax.ArgumentList is { } argumentList)
                    {
                        var parameterTypes = casePattern.CaseSymbol.ConstructorParameters;
                        var elementCount = Math.Min(parameterTypes.Length, casePattern.Arguments.Length);
                        var argumentCount = Math.Min(argumentList.Arguments.Count, elementCount);

                        for (var i = 0; i < argumentCount; i++)
                        {
                            EnsureMatchArmPatternValid(
                                parameterTypes[i].Type,
                                argumentList.Arguments[i],
                                casePattern.Arguments[i]);
                        }
                    }

                    return;
                }
            case BoundDeconstructPattern deconstructPattern:
                {
                    var narrowedType = UnwrapAlias(deconstructPattern.NarrowedType ?? deconstructPattern.ReceiverType);
                    if (ReportRecursivePatternTypeInvalid(scrutineeType, patternSyntax, narrowedType))
                        return;

                    if (patternSyntax is NominalDeconstructionPatternSyntax nominalSyntax)
                    {
                        var parameterTypes = deconstructPattern.DeconstructMethod.Parameters;
                        var elementCount = Math.Min(parameterTypes.Length, deconstructPattern.Arguments.Length);
                        var argumentCount = Math.Min(nominalSyntax.ArgumentList.Arguments.Count, elementCount);

                        for (var i = 0; i < argumentCount; i++)
                        {
                            EnsureMatchArmPatternValid(
                                parameterTypes[i].Type,
                                nominalSyntax.ArgumentList.Arguments[i].Pattern,
                                deconstructPattern.Arguments[i]);
                        }
                    }

                    return;
                }
            case BoundPropertyPattern propertyPattern:
                {
                    var narrowedType = UnwrapAlias(propertyPattern.NarrowedType ?? propertyPattern.ReceiverType);
                    if (ReportRecursivePatternTypeInvalid(scrutineeType, patternSyntax, narrowedType))
                        return;

                    if (patternSyntax is PropertyPatternSyntax propertySyntax)
                    {
                        var propertyCount = Math.Min(propertyPattern.Properties.Length, propertySyntax.PropertyPatternClause.Properties.Count);
                        for (var i = 0; i < propertyCount; i++)
                        {
                            EnsureMatchArmPatternValid(
                                propertyPattern.Properties[i].Type,
                                propertySyntax.PropertyPatternClause.Properties[i].Pattern,
                                propertyPattern.Properties[i].Pattern);
                        }
                    }

                    return;
                }
            case BoundOrPattern orPattern:
                {
                    if (patternSyntax is BinaryPatternSyntax binarySyntax)
                    {
                        EnsureMatchArmPatternValid(scrutineeType, binarySyntax.Left, orPattern.Left);
                        EnsureMatchArmPatternValid(scrutineeType, binarySyntax.Right, orPattern.Right);
                    }

                    return;
                }
            case BoundAndPattern andPattern:
                {
                    if (patternSyntax is BinaryPatternSyntax binarySyntax)
                    {
                        EnsureMatchArmPatternValid(scrutineeType, binarySyntax.Left, andPattern.Left);
                        EnsureMatchArmPatternValid(scrutineeType, binarySyntax.Right, andPattern.Right);
                    }

                    return;
                }
            case BoundNotPattern notPattern:
                {
                    if (patternSyntax is UnaryPatternSyntax unarySyntax)
                        EnsureMatchArmPatternValid(scrutineeType, unarySyntax.Pattern, notPattern.Pattern);

                    return;
                }
            case BoundPositionalPattern tuplePattern:
                {
                    if (patternSyntax is SequencePatternSyntax collectionSyntax)
                    {
                        if (!TryGetSequencePatternElementType(scrutineeType, out var elementType))
                            return;

                        var patternElements = tuplePattern.Elements;
                        var elementCount = Math.Min(patternElements.Length, collectionSyntax.Elements.Count);
                        for (var i = 0; i < elementCount; i++)
                        {
                            var expectedType = GetSequencePatternElementType(
                                scrutineeType,
                                elementType,
                                tuplePattern.ElementWidths,
                                tuplePattern.ElementKinds,
                                tuplePattern.RestIndex,
                                i);
                            EnsureMatchArmPatternValid(expectedType, collectionSyntax.Elements[i].Pattern, patternElements[i]);
                        }

                        return;
                    }

                    var tuplePatternType = UnwrapAlias(tuplePattern.Type);

                    if (tuplePatternType.TypeKind == TypeKind.Error)
                        return;

                    if (!PatternCanMatch(scrutineeType, tuplePatternType))
                    {
                        var patternDisplay = GetMatchPatternDisplay(tuplePatternType);
                        _diagnostics.ReportMatchExpressionArmPatternInvalid(
                            patternDisplay,
                            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            patternSyntax.GetLocation());
                        return;
                    }

                    if (patternSyntax is PositionalPatternSyntax tupleSyntax)
                    {
                        var elementTypes = GetTupleElementTypes(scrutineeType);

                        if (elementTypes.Length == 0)
                            elementTypes = GetTupleElementTypes(tuplePatternType);

                        var patternElements = tuplePattern.Elements;
                        var elementCount = Math.Min(patternElements.Length, tupleSyntax.Elements.Count);

                        for (var i = 0; i < elementCount; i++)
                        {
                            var elementType = elementTypes.Length > i
                                ? elementTypes[i]
                                : patternElements[i].Type ?? Compilation.ErrorTypeSymbol;

                            EnsureMatchArmPatternValid(elementType, tupleSyntax.Elements[i].Pattern, patternElements[i]);
                        }
                    }

                    return;
                }
        }
    }

    private bool ReportRecursivePatternTypeInvalid(
        ITypeSymbol scrutineeType,
        PatternSyntax patternSyntax,
        ITypeSymbol patternType)
    {
        patternType = UnwrapAlias(patternType);

        if (patternType.TypeKind == TypeKind.Error)
        {
            if (!TryGetRecursivePatternDisplay(patternSyntax, out var fallbackDisplay, out var fallbackLocation))
                return false;

            _diagnostics.ReportMatchExpressionArmPatternInvalid(
                fallbackDisplay,
                scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                fallbackLocation);
            return true;
        }

        if (PatternCanMatch(scrutineeType, patternType))
            return false;

        var location = patternSyntax switch
        {
            NominalDeconstructionPatternSyntax nominalSyntax => nominalSyntax.Type.GetLocation(),
            PropertyPatternSyntax propertySyntax when propertySyntax.Type is not null => propertySyntax.Type.GetLocation(),
            _ => patternSyntax.GetLocation(),
        };

        _diagnostics.ReportMatchExpressionArmPatternInvalid(
            GetMatchPatternDisplay(patternType),
            scrutineeType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            location);
        return true;
    }

    private static bool TryGetRecursivePatternDisplay(
        PatternSyntax patternSyntax,
        out string patternDisplay,
        out Location location)
    {
        switch (patternSyntax)
        {
            case NominalDeconstructionPatternSyntax nominalSyntax:
                patternDisplay = $"for type '{nominalSyntax.Type}'";
                location = nominalSyntax.Type.GetLocation();
                return true;
            case PropertyPatternSyntax propertySyntax when propertySyntax.Type is not null:
                patternDisplay = $"for type '{propertySyntax.Type}'";
                location = propertySyntax.Type.GetLocation();
                return true;
            default:
                patternDisplay = string.Empty;
                location = patternSyntax.GetLocation();
                return false;
        }
    }

    private static bool AreSameUnionPatternTarget(ITypeSymbol left, ITypeSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is not INamedTypeSymbol leftNamed || right is not INamedTypeSymbol rightNamed)
            return false;

        leftNamed = leftNamed.OriginalDefinition as INamedTypeSymbol ?? leftNamed;
        rightNamed = rightNamed.OriginalDefinition as INamedTypeSymbol ?? rightNamed;

        if (!string.Equals(leftNamed.Name, rightNamed.Name, StringComparison.Ordinal))
            return false;

        if (leftNamed.TypeParameters.Length != rightNamed.TypeParameters.Length)
            return false;

        var leftNs = leftNamed.ContainingNamespace?.ToDisplayString();
        var rightNs = rightNamed.ContainingNamespace?.ToDisplayString();
        return string.Equals(leftNs, rightNs, StringComparison.Ordinal);
    }

    private static bool ArePatternTypesEquivalent(ITypeSymbol left, ITypeSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        left = UnwrapAlias(left);
        right = UnwrapAlias(right);

        if (left.SpecialType != SpecialType.None && left.SpecialType == right.SpecialType)
            return true;

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            leftNamed = leftNamed.OriginalDefinition as INamedTypeSymbol ?? leftNamed;
            rightNamed = rightNamed.OriginalDefinition as INamedTypeSymbol ?? rightNamed;

            if (!string.Equals(leftNamed.Name, rightNamed.Name, StringComparison.Ordinal))
                return false;

            if (leftNamed.TypeParameters.Length != rightNamed.TypeParameters.Length)
                return false;

            var leftNs = leftNamed.ContainingNamespace?.ToDisplayString();
            var rightNs = rightNamed.ContainingNamespace?.ToDisplayString();
            return string.Equals(leftNs, rightNs, StringComparison.Ordinal);
        }

        return false;
    }

    private static string GetMatchPatternDisplay(ITypeSymbol patternType)
        => patternType switch
        {
            LiteralTypeSymbol literal => literal.Name,
            _ => $"for type '{patternType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat)}'"
        };

    private bool PatternCanMatch(ITypeSymbol scrutineeType, ITypeSymbol patternType)
    {
        scrutineeType = UnwrapAlias(scrutineeType);
        patternType = UnwrapAlias(patternType);

        if (scrutineeType.TypeKind == TypeKind.Error || patternType.TypeKind == TypeKind.Error)
            return true;

        if (TypeCoverageHelper.CanTypeParameterMatchPattern(scrutineeType, patternType))
            return true;

        if (patternType is NullTypeSymbol)
            return CanBeNull(scrutineeType);

        var nonNullableScrutineeType = scrutineeType.GetNonNullableType();
        if (!SymbolEqualityComparer.Default.Equals(nonNullableScrutineeType, scrutineeType) &&
            PatternCanMatch(nonNullableScrutineeType, patternType))
        {
            return true;
        }

        if (scrutineeType.TryGetUnion() is { } nominalUnion &&
            !nominalUnion.MemberTypes.IsDefaultOrEmpty)
        {
            foreach (var member in nominalUnion.MemberTypes)
            {
                var memberContentType = UnionContentNullability.GetNonNullContentType(member, out _);
                if (PatternCanMatch(memberContentType, patternType))
                    return true;
            }
        }

        if (patternType.TryGetUnionCase() is { } caseType)
        {
            var targetUnion = scrutineeType.TryGetUnion()
                ?? scrutineeType.TryGetUnionCase()?.Union;

            if (targetUnion is not null &&
                AreSameUnionPatternTarget(UnwrapAlias(targetUnion), UnwrapAlias(caseType.Union)))
            {
                return true;
            }
        }

        return IsAssignable(patternType, scrutineeType, out _) ||
               IsAssignable(scrutineeType, patternType, out _);

    }

    private bool CanBeNull(ITypeSymbol type)
    {
        type = UnwrapAlias(type);

        if (type.TypeKind == TypeKind.Error)
            return true;

        // Direct null type.
        if (type is NullTypeSymbol)
            return true;

        // Explicit nullable wrapper.
        if (type.IsNullable)
            return true;

        if (type.TryGetUnion() is { ContentMayBeNull: true })
            return true;

        return false;
    }

    private ImmutableArray<ITypeSymbol> GetTupleElementTypes(ITypeSymbol type)
    {
        type = UnwrapAlias(type);

        if (type is INamedTypeSymbol named)
        {
            var elements = named.TupleElements;
            if (!elements.IsDefaultOrEmpty)
                return elements.Select(e => e.Type).ToImmutableArray();
        }

        if (type is ITupleTypeSymbol tuple)
        {
            return tuple.TupleElements.Select(e => e.Type).ToImmutableArray();
        }

        return ImmutableArray<ITypeSymbol>.Empty;
    }

    private void EnsureMatchArmOrder(
        SyntaxNode matchSyntax,
        SyntaxList<MatchArmSyntax> armSyntaxes,
        BoundExpression scrutinee,
        ImmutableArray<BoundMatchArm> arms)
    {
        if (scrutinee.Type is not ITypeSymbol scrutineeType)
            return;

        scrutineeType = UnwrapAlias(scrutineeType);

        if (scrutineeType.TypeKind == TypeKind.Error)
            return;

        var seenCatchAll = false;

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];

            if (seenCatchAll)
            {
                _diagnostics.ReportMatchExpressionArmUnreachable(
                    armSyntaxes[i].Pattern.GetLocation());
                continue;
            }

            for (var previousIndex = 0; previousIndex < i; previousIndex++)
            {
                var previousArm = arms[previousIndex];
                if (!BoundNodeFacts.MatchArmGuardGuaranteesMatch(previousArm.Guard))
                    continue;

                if (!ArePatternsEquivalentForReachability(previousArm.Pattern, arm.Pattern))
                    continue;

                _diagnostics.ReportMatchExpressionArmUnreachable(
                    armSyntaxes[i].Pattern.GetLocation());
                goto NextArm;
            }

            if (BoundNodeFacts.MatchArmGuardGuaranteesMatch(arm.Guard) &&
                IsCatchAllPattern(scrutineeType, arm.Pattern))
                seenCatchAll = true;

        NextArm:
            ;
        }
    }

    private bool ArePatternsEquivalentForReachability(BoundPattern left, BoundPattern right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.GetType() != right.GetType())
            return false;

        switch (left)
        {
            case BoundCasePattern leftCase when right is BoundCasePattern rightCase:
                return SymbolEqualityComparer.Default.Equals(leftCase.CaseSymbol, rightCase.CaseSymbol) &&
                       HaveEquivalentPatterns(leftCase.Arguments, rightCase.Arguments);

            case BoundUnionMemberPattern leftUnion when right is BoundUnionMemberPattern rightUnion:
                return ArePatternTypesEquivalent(leftUnion.UnionType, rightUnion.UnionType) &&
                       ArePatternTypesEquivalent(leftUnion.MemberType, rightUnion.MemberType) &&
                       ArePatternsEquivalentForReachability(leftUnion.Pattern, rightUnion.Pattern);

            case BoundNotPattern leftNot when right is BoundNotPattern rightNot:
                return ArePatternsEquivalentForReachability(leftNot.Pattern, rightNot.Pattern);

            case BoundAndPattern leftAnd when right is BoundAndPattern rightAnd:
                return ArePatternsEquivalentForReachability(leftAnd.Left, rightAnd.Left) &&
                       ArePatternsEquivalentForReachability(leftAnd.Right, rightAnd.Right);

            case BoundOrPattern leftOr when right is BoundOrPattern rightOr:
                return ArePatternsEquivalentForReachability(leftOr.Left, rightOr.Left) &&
                       ArePatternsEquivalentForReachability(leftOr.Right, rightOr.Right);

            case BoundDeclarationPattern leftDeclaration when right is BoundDeclarationPattern rightDeclaration:
                return ArePatternTypesEquivalent(leftDeclaration.DeclaredType, rightDeclaration.DeclaredType);

            case BoundPositionalPattern leftPositional when right is BoundPositionalPattern rightPositional:
                return ArePatternTypesEquivalent(leftPositional.Type, rightPositional.Type) &&
                       leftPositional.RestIndex == rightPositional.RestIndex &&
                       leftPositional.ElementWidths.SequenceEqual(rightPositional.ElementWidths) &&
                       leftPositional.ElementKinds.SequenceEqual(rightPositional.ElementKinds) &&
                       HaveEquivalentPatterns(leftPositional.Elements, rightPositional.Elements);

            case BoundDeconstructPattern leftDeconstruct when right is BoundDeconstructPattern rightDeconstruct:
                return ArePatternTypesEquivalent(leftDeconstruct.InputType, rightDeconstruct.InputType) &&
                       ArePatternTypesEquivalent(leftDeconstruct.ReceiverType, rightDeconstruct.ReceiverType) &&
                       AreNullablePatternTypesEquivalent(leftDeconstruct.NarrowedType, rightDeconstruct.NarrowedType) &&
                       SymbolEqualityComparer.Default.Equals(leftDeconstruct.DeconstructMethod, rightDeconstruct.DeconstructMethod) &&
                       HaveEquivalentPatterns(leftDeconstruct.Arguments, rightDeconstruct.Arguments);

            case BoundConstantPattern leftConstant when right is BoundConstantPattern rightConstant:
                return AreConstantPatternsEquivalent(leftConstant, rightConstant);

            case BoundDiscardPattern:
                return true;

            case BoundPropertyPattern leftProperty when right is BoundPropertyPattern rightProperty:
                return ArePatternTypesEquivalent(leftProperty.InputType, rightProperty.InputType) &&
                       ArePatternTypesEquivalent(leftProperty.ReceiverType, rightProperty.ReceiverType) &&
                       AreNullablePatternTypesEquivalent(leftProperty.NarrowedType, rightProperty.NarrowedType) &&
                       HaveEquivalentPropertySubpatterns(leftProperty.Properties, rightProperty.Properties);

            case BoundDictionaryPattern leftDictionary when right is BoundDictionaryPattern rightDictionary:
                return ArePatternTypesEquivalent(leftDictionary.InputType, rightDictionary.InputType) &&
                       ArePatternTypesEquivalent(leftDictionary.ReceiverType, rightDictionary.ReceiverType) &&
                       ArePatternTypesEquivalent(leftDictionary.KeyType, rightDictionary.KeyType) &&
                       ArePatternTypesEquivalent(leftDictionary.ValueType, rightDictionary.ValueType) &&
                       HaveEquivalentDictionarySubpatterns(leftDictionary.Entries, rightDictionary.Entries);

            case BoundComparisonPattern leftComparison when right is BoundComparisonPattern rightComparison:
                return ArePatternTypesEquivalent(leftComparison.InputType, rightComparison.InputType) &&
                       leftComparison.Operator == rightComparison.Operator &&
                       AreExpressionsEquivalentForPatternValue(leftComparison.Value, rightComparison.Value);

            case BoundRangePattern leftRange when right is BoundRangePattern rightRange:
                return ArePatternTypesEquivalent(leftRange.Type, rightRange.Type) &&
                       leftRange.IsUpperExclusive == rightRange.IsUpperExclusive &&
                       AreExpressionsEquivalentForPatternValue(leftRange.LowerBound, rightRange.LowerBound) &&
                       AreExpressionsEquivalentForPatternValue(leftRange.UpperBound, rightRange.UpperBound);

            default:
                return false;
        }
    }

    private bool HaveEquivalentPatterns(ImmutableArray<BoundPattern> left, ImmutableArray<BoundPattern> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!ArePatternsEquivalentForReachability(left[i], right[i]))
                return false;
        }

        return true;
    }

    private bool HaveEquivalentPropertySubpatterns(
        ImmutableArray<BoundPropertySubpattern> left,
        ImmutableArray<BoundPropertySubpattern> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(left[i].Member, right[i].Member) ||
                !ArePatternTypesEquivalent(left[i].Type, right[i].Type) ||
                !ArePatternsEquivalentForReachability(left[i].Pattern, right[i].Pattern))
            {
                return false;
            }
        }

        return true;
    }

    private bool HaveEquivalentDictionarySubpatterns(
        ImmutableArray<BoundDictionarySubpattern> left,
        ImmutableArray<BoundDictionarySubpattern> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!AreExpressionsEquivalentForPatternValue(left[i].Key, right[i].Key) ||
                !ArePatternsEquivalentForReachability(left[i].Pattern, right[i].Pattern))
            {
                return false;
            }
        }

        return true;
    }

    private bool AreConstantPatternsEquivalent(BoundConstantPattern left, BoundConstantPattern right)
    {
        if (!ArePatternTypesEquivalent(left.Type, right.Type))
            return false;

        var leftConstant = left.ConstantValue;
        var rightConstant = right.ConstantValue;
        if (leftConstant is not null || rightConstant is not null)
            return Equals(leftConstant, rightConstant);

        return AreExpressionsEquivalentForPatternValue(left.Expression, right.Expression);
    }

    private bool AreExpressionsEquivalentForPatternValue(BoundExpression? left, BoundExpression? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null || right is null || left.GetType() != right.GetType())
            return false;

        switch (left)
        {
            case BoundLiteralExpression leftLiteral when right is BoundLiteralExpression rightLiteral:
                return Equals(leftLiteral.Value, rightLiteral.Value) &&
                       AreNullablePatternTypesEquivalent(leftLiteral.Type, rightLiteral.Type);

            case BoundConversionExpression leftConversion when right is BoundConversionExpression rightConversion:
                return AreNullablePatternTypesEquivalent(leftConversion.Type, rightConversion.Type) &&
                       AreExpressionsEquivalentForPatternValue(leftConversion.Expression, rightConversion.Expression);

            case BoundFieldAccess leftField when right is BoundFieldAccess rightField:
                return SymbolEqualityComparer.Default.Equals(leftField.Field, rightField.Field) &&
                       AreExpressionsEquivalentForPatternValue(leftField.Receiver, rightField.Receiver);

            case BoundPropertyAccess leftProperty when right is BoundPropertyAccess rightProperty:
                return SymbolEqualityComparer.Default.Equals(leftProperty.Property, rightProperty.Property);

            case BoundLocalAccess leftLocal when right is BoundLocalAccess rightLocal:
                return SymbolEqualityComparer.Default.Equals(leftLocal.Local, rightLocal.Local);

            case BoundParameterAccess leftParameter when right is BoundParameterAccess rightParameter:
                return SymbolEqualityComparer.Default.Equals(leftParameter.Parameter, rightParameter.Parameter);

            default:
                return false;
        }
    }

    private static bool AreNullablePatternTypesEquivalent(ITypeSymbol? left, ITypeSymbol? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return ArePatternTypesEquivalent(left, right);
    }


    private void EnsureMatchExhaustive(
        SyntaxNode matchSyntax,
        SyntaxList<MatchArmSyntax> armSyntaxes,
        BoundExpression scrutinee,
        ImmutableArray<BoundMatchArm> arms)
    {
        if (scrutinee.Type is not ITypeSymbol scrutineeType ||
            UnwrapAlias(scrutineeType).TypeKind == TypeKind.Error)
        {
            return;
        }

        scrutineeType = UnwrapAlias(scrutineeType);
        ReportPartialMatchCoverageDiagnostics(armSyntaxes, arms, scrutineeType);

        var evaluator = new MatchExhaustivenessEvaluator(
            Compilation,
            node => TryGetCachedBoundNode(node));
        var options = new MatchExhaustivenessOptions(ignoreCatchAllPatterns: false);
        var result = evaluator.Evaluate(matchSyntax, scrutinee, arms, options);

        if (!result.IsExhaustive)
        {
            foreach (var missingCase in result.MissingCases)
                ReportMatchNotExhaustive(matchSyntax, missingCase);

            return;
        }

        if (!result.HasCatchAll)
            return;

        var withoutCatchAll = evaluator.Evaluate(
            matchSyntax,
            scrutinee,
            arms,
            new MatchExhaustivenessOptions(ignoreCatchAllPatterns: true));

        if (!withoutCatchAll.IsExhaustive)
            return;

        var union = scrutineeType.TryGetUnion() ?? scrutineeType.TryGetUnionCase()?.Union;
        if (union is not null &&
            RequiresInactiveStructUnionStateCoverage(matchSyntax, scrutinee, scrutineeType, union))
        {
            return;
        }

        var catchAllIndex = GetCatchAllArmIndex(scrutineeType, arms);
        if (catchAllIndex >= 0)
            ReportRedundantCatchAll(armSyntaxes, catchAllIndex);
    }

    private void ReportPartialMatchCoverageDiagnostics(
        SyntaxList<MatchArmSyntax> armSyntaxes,
        ImmutableArray<BoundMatchArm> arms,
        ITypeSymbol scrutineeType)
    {
        var catchAllIndex = GetCatchAllArmIndex(scrutineeType, arms);
        if (catchAllIndex >= 0)
            return;

        var reported = new HashSet<string>(StringComparer.Ordinal);

        if (!TypeCoverageHelper.TryGetSealedHierarchy(scrutineeType, out var sealedRoot))
            return;

        var projectedHierarchy = scrutineeType as INamedTypeSymbol ?? sealedRoot;
        var coverageTypes = TypeCoverageHelper.GetSealedHierarchyCoverageTypes(sealedRoot, projectedHierarchy);
        var sealedRemaining = new HashSet<ITypeSymbol>(
            coverageTypes.Cast<ITypeSymbol>(),
            TypeSymbolReferenceComparer.Instance);

        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];
            if (!BoundNodeFacts.MatchArmGuardGuaranteesMatch(arm.Guard))
                continue;

            if (TryGetPartiallyCoveredTypeName(sealedRemaining, arm.Pattern, out var typeName) &&
                reported.Add(typeName))
            {
                ReportMatchArmPatternNotFullyCovered(armSyntaxes[i].Pattern.GetLocation(), typeName);
            }

            RemoveCoveredUnionMembers(sealedRemaining, arm.Pattern);
        }
    }



    private int GetCatchAllArmIndex(ITypeSymbol scrutineeType, ImmutableArray<BoundMatchArm> arms)
    {
        for (var i = 0; i < arms.Length; i++)
        {
            var arm = arms[i];

            if (!BoundNodeFacts.MatchArmGuardGuaranteesMatch(arm.Guard))
                continue;

            if (IsCatchAllPattern(scrutineeType, arm.Pattern))
                return i;
        }

        return -1;
    }

    private void ReportRedundantCatchAll(SyntaxList<MatchArmSyntax> armSyntaxes, int catchAllIndex)
    {
        var patternLocation = armSyntaxes[catchAllIndex].Pattern.GetLocation();
        _diagnostics.ReportMatchExpressionCatchAllRedundant(patternLocation);
    }

    private bool IsCatchAllPattern(ITypeSymbol scrutineeType, BoundPattern pattern)
        => MatchExhaustivenessEvaluator.IsCatchAllPattern(scrutineeType, pattern);





    private bool CasePatternCoversAllArguments(BoundCasePattern casePattern)
    {
        var parameters = casePattern.CaseSymbol.ConstructorParameters;
        var argumentCount = Math.Min(parameters.Length, casePattern.Arguments.Length);

        for (var i = 0; i < argumentCount; i++)
        {
            if (!IsTotalPattern(parameters[i].Type, casePattern.Arguments[i]))
                return false;
        }

        return true;
    }

    private bool RequiresInactiveStructUnionStateCoverage(
        SyntaxNode matchSyntax,
        BoundExpression scrutinee,
        ITypeSymbol scrutineeType,
        IUnionSymbol union)
    {
        if (!CanRepresentInactiveStructUnionState(scrutineeType, union))
            return false;

        return StructUnionDefaultStateFlow.ContentsMayBeDefault(scrutinee, matchSyntax, TryGetCachedBoundNode);
    }

    private static bool CanRepresentInactiveStructUnionState(ITypeSymbol scrutineeType, IUnionSymbol union)
    {
        if (union.TypeKind != TypeKind.Struct)
            return false;

        if (scrutineeType.TryGetUnion() is null)
            return false;

        return true;
    }

    private bool PatternCoversInactiveStructUnionState(
        ITypeSymbol scrutineeType,
        BoundPattern pattern,
        IUnionSymbol union)
    {
        if (union.TypeKind != TypeKind.Struct)
            return false;

        if (IsCatchAllPattern(scrutineeType, pattern))
            return true;

        return pattern is BoundOrPattern orPattern &&
               (PatternCoversInactiveStructUnionState(scrutineeType, orPattern.Left, union) ||
                PatternCoversInactiveStructUnionState(scrutineeType, orPattern.Right, union));
    }

    private bool IsTotalPattern(ITypeSymbol inputType, BoundPattern pattern)
    {
        inputType = UnwrapAlias(inputType);

        switch (pattern)
        {
            case BoundDiscardPattern:
                return true;
            case BoundConstantPattern constant:
                return inputType.SpecialType == SpecialType.System_Unit &&
                       constant.Expression is BoundUnitExpression;
            case BoundDeclarationPattern declaration:
                {
                    var declaredType = UnwrapAlias(declaration.DeclaredType);

                    if (declaredType.TypeKind == TypeKind.Error || inputType.TypeKind == TypeKind.Error)
                        return true;

                    if (ArePatternTypesEquivalent(declaredType, inputType))
                        return true;

                    return IsAssignable(declaredType, inputType, out _);
                }
            case BoundPositionalPattern tuplePattern:
                {
                    var elementTypes = GetTupleElementTypes(inputType);

                    if (elementTypes.Length == 0)
                    {
                        return inputType.SpecialType == SpecialType.System_Unit &&
                               tuplePattern.Elements.Length == 0;
                    }

                    if (elementTypes.Length != tuplePattern.Elements.Length)
                        return false;

                    for (var i = 0; i < tuplePattern.Elements.Length; i++)
                    {
                        if (!IsTotalPattern(elementTypes[i], tuplePattern.Elements[i]))
                            return false;
                    }

                    return true;
                }
            case BoundDeconstructPattern deconstructPattern:
                {
                    if (CanBeNull(inputType))
                        return false;

                    if (deconstructPattern.NarrowedType is not null &&
                        !IsAssignable(deconstructPattern.NarrowedType, inputType, out _))
                    {
                        return false;
                    }

                    var parameters = deconstructPattern.DeconstructMethod.Parameters;
                    var parameterOffset = deconstructPattern.DeconstructMethod.IsExtensionMethod ? 1 : 0;
                    var parameterCount = parameters.Length - parameterOffset;

                    if (parameterCount != deconstructPattern.Arguments.Length)
                        return false;

                    for (var i = 0; i < parameterCount; i++)
                    {
                        if (!IsTotalPattern(parameters[i + parameterOffset].Type, deconstructPattern.Arguments[i]))
                            return false;
                    }

                    return true;
                }
            case BoundPropertyPattern propertyPattern:
                {
                    if (CanBeNull(inputType))
                        return false;

                    if (propertyPattern.NarrowedType is not null &&
                        !IsAssignable(propertyPattern.NarrowedType, inputType, out _))
                    {
                        return false;
                    }

                    foreach (var property in propertyPattern.Properties)
                    {
                        if (!IsTotalPattern(property.Type, property.Pattern))
                            return false;
                    }

                    return true;
                }
            case BoundOrPattern orPattern:
                return IsTotalPattern(inputType, orPattern.Left) ||
                       IsTotalPattern(inputType, orPattern.Right);
            case BoundAndPattern andPattern:
                return IsTotalPattern(inputType, andPattern.Left) &&
                       IsTotalPattern(inputType, andPattern.Right);
            default:
                return false;
        }
    }

    private static bool TryGetLiteralBoolConstant(BoundPattern pattern, out bool value)
    {
        value = default;

        if (pattern is not BoundConstantPattern constant)
            return false;

        if (TryGetExpressionBoolConstant(constant.Expression, out value))
            return true;

        if (constant.ConstantValue is bool b)
        {
            value = b;
            return true;
        }

        return false;
    }

    private static bool TryGetExpressionBoolConstant(BoundExpression? expression, out bool value)
    {
        switch (expression)
        {
            case BoundLiteralExpression { Value: bool b }:
                value = b;
                return true;
            case BoundConversionExpression conversion:
                return TryGetExpressionBoolConstant(conversion.Expression, out value);
            default:
                value = default;
                return false;
        }
    }

    private void RemoveCoveredUnionMembers(
        HashSet<ITypeSymbol> remaining,
        BoundPattern pattern,
        Dictionary<ITypeSymbol, HashSet<object?>>? literalCoverage = null)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                remaining.Clear();
                literalCoverage?.Clear();
                break;
            case BoundDeclarationPattern declaration:
                RemoveMembersAssignableToPattern(remaining, declaration.DeclaredType, literalCoverage);
                break;
            case BoundConstantPattern constant:
                {
                    // Expression-backed value patterns may depend on runtime values and must not
                    // participate in compile-time coverage reasoning. Null constants are still
                    // compile-time exhaustive for the null member in nullable unions.
                    if (constant.Expression is not null)
                    {
                        if (IsNullLiteralPatternExpression(constant.Expression))
                        {
                            foreach (var candidate in remaining.ToArray())
                            {
                                var candidateType = UnwrapAlias(candidate);
                                if (candidateType.TypeKind == TypeKind.Null)
                                {
                                    remaining.Remove(candidate);
                                    literalCoverage?.Remove(candidate);
                                }
                            }
                        }

                        break;
                    }

                    // Defensive: literal-backed patterns should always have a literal type.
                    if (constant.LiteralType is null)
                        break;

                    var literalType = (LiteralTypeSymbol)UnwrapAlias(constant.LiteralType);

                    if (TryUpdateLiteralCoverage(remaining, literalCoverage, literalType))
                        break;

                    if (constant.ConstantValue is null)
                    {
                        foreach (var candidate in remaining.ToArray())
                        {
                            var candidateType = UnwrapAlias(candidate);

                            if (candidateType.TypeKind == TypeKind.Null)
                            {
                                remaining.Remove(candidate);
                                literalCoverage?.Remove(candidate);
                            }
                        }

                        break;
                    }

                    foreach (var candidate in remaining.ToArray())
                    {
                        var candidateType = UnwrapAlias(candidate);

                        if (SymbolEqualityComparer.Default.Equals(candidateType, literalType))
                        {
                            remaining.Remove(candidate);
                            literalCoverage?.Remove(candidate);
                        }
                    }

                    break;
                }
            case BoundUnionMemberPattern unionMemberPattern:
                RemoveUnionMemberPatternCoverage(remaining, unionMemberPattern, literalCoverage);
                break;
            case BoundOrPattern orPattern:
                RemoveCoveredUnionMembers(remaining, orPattern.Left, literalCoverage);
                RemoveCoveredUnionMembers(remaining, orPattern.Right, literalCoverage);
                break;
            case BoundPositionalPattern tuplePattern:
                RemoveMembersTotallyMatchedByPattern(remaining, tuplePattern, literalCoverage);
                break;
            case BoundDeconstructPattern deconstructPattern:
                {
                    RemoveMembersTotallyMatchedByPattern(remaining, deconstructPattern, literalCoverage);
                    break;
                }
            case BoundPropertyPattern propertyPattern:
                {
                    RemoveMembersTotallyMatchedByPattern(remaining, propertyPattern, literalCoverage);
                    break;
                }
        }

        static bool IsNullLiteralPatternExpression(BoundExpression expression)
            => expression switch
            {
                BoundLiteralExpression { Kind: BoundLiteralExpressionKind.NullLiteral } => true,
                BoundTypeExpression { Type: NullTypeSymbol } => true,
                BoundConversionExpression conversion => IsNullLiteralPatternExpression(conversion.Expression),
                _ => false
            };
    }

    private void RemoveUnionMemberPatternCoverage(
        HashSet<ITypeSymbol> remaining,
        BoundUnionMemberPattern unionMemberPattern,
        Dictionary<ITypeSymbol, HashSet<object?>>? literalCoverage)
    {
        var patternMemberType = UnwrapAlias(unionMemberPattern.MemberType);
        patternMemberType = UnionContentNullability.GetNonNullContentType(patternMemberType, out _);

        foreach (var candidate in remaining.ToArray())
        {
            var candidateType = UnwrapAlias(candidate);
            candidateType = UnionContentNullability.GetNonNullContentType(candidateType, out _);
            if (!ArePatternTypesEquivalent(candidateType, patternMemberType))
                continue;

            if (!IsTotalPattern(candidateType, unionMemberPattern.Pattern))
                continue;

            remaining.Remove(candidate);
            literalCoverage?.Remove(candidate);
        }
    }

    private void RemoveCoveredCases(
        HashSet<IUnionCaseTypeSymbol> remaining,
        BoundPattern pattern,
        IUnionSymbol union)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                remaining.Clear();
                break;
            case BoundDeclarationPattern declaration:
                {
                    var declaredType = UnwrapAlias(declaration.DeclaredType);

                    if (declaredType.SpecialType == SpecialType.System_Object ||
                        ArePatternTypesEquivalent(declaredType, UnwrapAlias((ITypeSymbol)union)))
                    {
                        remaining.Clear();
                        break;
                    }

                    if (declaredType.TryGetUnionCase() is { } declarationCase &&
                        AreSameUnionPatternTarget(UnwrapAlias(declarationCase.Union), UnwrapAlias(union)))
                    {
                        var matchedCase = declarationCase.OriginalDefinition as IUnionCaseTypeSymbol ?? declarationCase;
                        remaining.RemoveWhere(candidate =>
                            candidate.Ordinal == matchedCase.Ordinal ||
                            SymbolEqualityComparer.Default.Equals(candidate, matchedCase));
                    }
                    else if (declaredType.TryGetUnion() is { } declarationUnion &&
                        AreSameUnionPatternTarget(UnwrapAlias(declarationUnion), UnwrapAlias(union)))
                    {
                        remaining.Clear();
                    }

                    break;
                }
            case BoundCasePattern casePattern:
                if (AreSameUnionPatternTarget(UnwrapAlias(casePattern.CaseSymbol.Union), UnwrapAlias(union)) &&
                    CasePatternCoversAllArguments(casePattern))
                {
                    var matchedCase = casePattern.CaseSymbol.OriginalDefinition as IUnionCaseTypeSymbol ?? casePattern.CaseSymbol;
                    remaining.RemoveWhere(candidate =>
                        candidate.Ordinal == matchedCase.Ordinal ||
                        SymbolEqualityComparer.Default.Equals(candidate, matchedCase));
                }
                break;
            case BoundOrPattern orPattern:
                RemoveCoveredCases(remaining, orPattern.Left, union);
                RemoveCoveredCases(remaining, orPattern.Right, union);
                break;
        }
    }


    private void RemoveCoveredEnumMembers(
        HashSet<IFieldSymbol> remaining,
        INamedTypeSymbol enumType,
        BoundPattern pattern)
    {
        enumType = (INamedTypeSymbol)UnwrapAlias(enumType);

        if (IsCatchAllPattern(enumType, pattern))
        {
            remaining.Clear();
            return;
        }

        switch (pattern)
        {
            case BoundConstantPattern constant:
                if (TryGetEnumField(constant.Expression, enumType, out var field))
                    remaining.Remove(field);

                break;
            case BoundOrPattern orPattern:
                RemoveCoveredEnumMembers(remaining, enumType, orPattern.Left);
                RemoveCoveredEnumMembers(remaining, enumType, orPattern.Right);
                break;
        }
    }

    private static bool TryGetEnumField(BoundExpression? expression, INamedTypeSymbol enumType, out IFieldSymbol field)
    {
        field = null!;

        if (expression is null)
            return false;

        switch (expression)
        {
            case BoundFieldAccess fieldAccess:
                return TryBindEnumField(fieldAccess.Field, enumType, out field);
            case BoundMemberAccessExpression { Member: IFieldSymbol memberField }:
                return TryBindEnumField(memberField, enumType, out field);
            default:
                return false;
        }
    }

    private static bool TryBindEnumField(IFieldSymbol candidate, INamedTypeSymbol enumType, out IFieldSymbol field)
    {
        field = null!;

        if (!candidate.IsConst)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(UnwrapAlias(candidate.Type), enumType))
            return false;

        field = candidate;
        return true;
    }

    private void RemoveMembersAssignableToPattern(
        HashSet<ITypeSymbol> remaining,
        ITypeSymbol patternType,
        Dictionary<ITypeSymbol, HashSet<object?>>? literalCoverage = null)
    {
        patternType = UnwrapAlias(patternType);
        patternType = UnionContentNullability.GetNonNullContentType(patternType, out _);

        if (patternType.TypeKind == TypeKind.Error)
            return;

        foreach (var candidate in remaining.ToArray())
        {
            var candidateType = UnwrapAlias(candidate);

            if (candidateType.TypeKind == TypeKind.Error)
            {
                remaining.Remove(candidate);
                literalCoverage?.Remove(candidate);
                continue;
            }

            if (candidateType.TypeKind == TypeKind.Null)
                continue;

            if (IsAssignable(patternType, candidateType, out _))
            {
                remaining.Remove(candidate);
                literalCoverage?.Remove(candidate);
                continue;
            }

        }
    }

    private void RemoveMembersTotallyMatchedByPattern(
        HashSet<ITypeSymbol> remaining,
        BoundPattern pattern,
        Dictionary<ITypeSymbol, HashSet<object?>>? literalCoverage = null)
    {
        foreach (var candidate in remaining.ToArray())
        {
            var candidateType = UnwrapAlias(candidate);
            if (!IsTotalPattern(candidateType, pattern))
                continue;

            remaining.Remove(candidate);
            literalCoverage?.Remove(candidate);
        }
    }

    private static ITypeSymbol UnwrapAlias(ITypeSymbol type)
    {
        while (type.IsAlias && type.UnderlyingSymbol is ITypeSymbol alias)
            type = alias;

        return type;
    }

    private static bool IsBooleanType(ITypeSymbol type)
    {
        type = UnwrapAlias(type);

        if (type.SpecialType == SpecialType.System_Boolean)
            return true;

        if (type is LiteralTypeSymbol literal)
            return IsBooleanType(literal.UnderlyingType);

        return false;
    }





    private static bool TryUpdateLiteralCoverage(
        HashSet<ITypeSymbol> remaining,
        Dictionary<ITypeSymbol, HashSet<object?>>? literalCoverage,
        LiteralTypeSymbol literal)
    {
        if (literalCoverage is null || literalCoverage.Count == 0)
            return false;

        var updated = false;

        foreach (var entry in literalCoverage.ToArray())
        {
            var candidate = entry.Key;

            if (!remaining.Contains(candidate))
            {
                literalCoverage.Remove(candidate);
                continue;
            }

            var targetType = UnwrapAlias(candidate);

            if (!TypeCoverageHelper.LiteralBelongsToType(literal, targetType))
                continue;

            updated = true;

            var constants = entry.Value;
            constants.Add(literal.ConstantValue);

            if (TypeCoverageHelper.LiteralsCoverType(targetType, constants))
            {
                remaining.Remove(candidate);
                literalCoverage.Remove(candidate);
            }
        }

        return updated;
    }



    private BoundExpression BindIfExpression(IfExpressionSyntax ifExpression)
    {
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?>? patternLocalShadows = null;
        if (ifExpression.Condition is IsPatternExpressionSyntax isPatternExpression)
        {
            patternLocalShadows = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);
            CapturePatternLocalShadows(isPatternExpression.Pattern, patternLocalShadows);
        }

        var condition = BindExpression(ifExpression.Condition);

        if (!HasExpressionErrors(condition))
        {
            var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
            var conversion = Compilation.ClassifyConversion(condition.Type, boolType);
            if (!conversion.Exists || !conversion.IsImplicit)
            {
                ReportCannotConvertFromTypeToType(condition.Type, boolType, ifExpression.Condition.GetLocation());
            }
        }

        return BindIfExpressionCore(
            ifExpression,
            condition,
            ifExpression.Expression,
            ifExpression.ElseClause,
            patternLocalShadows);
    }

    private BoundExpression BindIfPatternExpression(IfPatternExpressionSyntax ifExpression)
    {
        var patternLocalShadows = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);
        CapturePatternLocalShadows(ifExpression.Pattern, patternLocalShadows);
        var condition = BindIfPatternCondition(ifExpression);
        return BindIfExpressionCore(
            ifExpression,
            condition,
            ifExpression.Expression,
            ifExpression.ElseClause,
            patternLocalShadows);
    }

    private BoundExpression BindIfExpressionCore(
        ExpressionSyntax ifExpression,
        BoundExpression condition,
        ExpressionSyntax thenExpression,
        ElseExpressionClauseSyntax? elseClause,
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?>? patternLocalShadows)
    {
        var targetType = GetTargetType(ifExpression);
        if (targetType is NullableTypeSymbol nullableTargetType)
            targetType = nullableTargetType.UnderlyingType;

        var thenBinder = SemanticModel.GetBinder(ifExpression, this);
        var previousAllowReturnsInBlockExpressionsOnly = _allowReturnsInBlockExpressionsOnly;
        _allowReturnsInBlockExpressionsOnly = false;
        BoundExpression thenExpr;
        try
        {
            thenExpr = BindBranchExpression(thenBinder, thenExpression);
        }
        finally
        {
            _allowReturnsInBlockExpressionsOnly = previousAllowReturnsInBlockExpressionsOnly;
        }

        if (patternLocalShadows is not null)
        {
            foreach (var (name, shadowed) in patternLocalShadows)
            {
                if (shadowed is null)
                    _locals.Remove(name);
                else
                    _locals[name] = shadowed.Value;
            }
        }

        if (elseClause is null)
        {
            _diagnostics.ReportIfExpressionRequiresElse(ifExpression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OtherError);
        }

        var elseBinder = SemanticModel.GetBinder(elseClause, this);
        previousAllowReturnsInBlockExpressionsOnly = _allowReturnsInBlockExpressionsOnly;
        _allowReturnsInBlockExpressionsOnly = false;
        BoundExpression elseExpr;
        try
        {
            elseExpr = BindBranchExpression(elseBinder, elseClause.Expression);
        }
        finally
        {
            _allowReturnsInBlockExpressionsOnly = previousAllowReturnsInBlockExpressionsOnly;
        }

        var thenType = thenExpr.Type ?? Compilation.ErrorTypeSymbol;
        var elseType = elseExpr.Type ?? Compilation.ErrorTypeSymbol;

        var hasInferredResultType = TryInferBestCommonType(thenType, elseType, out var inferredResultType);
        var resultType = hasInferredResultType ? inferredResultType : Compilation.ErrorTypeSymbol;
        var usedReferenceFallbackResultType = false;

        if (targetType is null &&
            hasInferredResultType &&
            !SymbolEqualityComparer.Default.Equals(thenType, elseType) &&
            TryGetReferenceFallbackCommonType(resultType, thenType, elseType, out var referenceFallbackType))
        {
            resultType = referenceFallbackType;
            usedReferenceFallbackResultType = true;
        }

        if (!hasInferredResultType && targetType is null)
        {
            ReportCannotConvertFromTypeToType(
                thenType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                elseType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                ifExpression.GetLocation());
            return new BoundErrorExpression(Compilation.ErrorTypeSymbol, null, BoundExpressionReason.TypeMismatch);
        }

        if (targetType is null &&
            hasInferredResultType &&
            !SymbolEqualityComparer.Default.Equals(thenType, elseType) &&
            IsDisallowedImplicitCommonType(resultType) &&
            !usedReferenceFallbackResultType)
        {
            ReportCannotConvertFromTypeToType(
                thenType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                elseType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                ifExpression.GetLocation());
            return new BoundErrorExpression(Compilation.ErrorTypeSymbol, null, BoundExpressionReason.TypeMismatch);
        }

        if (targetType is not null &&
            targetType.TypeKind != TypeKind.Error &&
            IsAssignable(targetType, thenType, out _) &&
            IsAssignable(targetType, elseType, out _))
        {
            if (!ShouldPreferInferredResultType(resultType, targetType))
                resultType = targetType;
        }

        thenExpr = ConvertIfNeeded(resultType, thenExpr, thenExpression);
        elseExpr = ConvertIfNeeded(resultType, elseExpr, elseClause.Expression);

        return new BoundIfExpression(condition, thenExpr, elseExpr);

        BoundExpression BindBranchExpression(Binder? branchScope, ExpressionSyntax expression)
        {
            var blockBinder = FindEnclosingBlockBinder(branchScope) ?? this;
            return targetType is null
                ? blockBinder.BindExpression(
                    expression,
                    allowReturn: _allowReturnsInExpression,
                    allowReturnInBlockExpressionsOnly: false)
                : blockBinder.BindExpressionWithTargetType(
                    expression,
                    targetType,
                    allowReturn: _allowReturnsInExpression,
                    allowReturnInBlockExpressionsOnly: false);
        }

        static BlockBinder? FindEnclosingBlockBinder(Binder? binder)
        {
            for (var current = binder; current is not null; current = current.ParentBinder)
            {
                if (current is BlockBinder blockBinder)
                    return blockBinder;
            }

            return null;
        }

        BoundExpression ConvertIfNeeded(ITypeSymbol target, BoundExpression expression, ExpressionSyntax syntax)
        {
            if (target.TypeKind == TypeKind.Error || !ShouldAttemptConversion(expression))
                return expression;

            var sourceType = expression.Type;
            if (sourceType is null || SymbolEqualityComparer.Default.Equals(sourceType, target))
                return expression;

            if (!IsAssignable(target, sourceType, out var conversion))
            {
                ReportCannotConvertFromTypeToType(
                    sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    target.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    syntax.GetLocation());
                return new BoundErrorExpression(target, null, BoundExpressionReason.TypeMismatch);
            }

            return ApplyConversion(expression, target, conversion, syntax);
        }

        static bool ShouldPreferInferredResultType(ITypeSymbol inferredType, ITypeSymbol targetType)
        {
            if (SymbolEqualityComparer.Default.Equals(inferredType, targetType))
                return false;

            if (targetType is ITypeParameterSymbol && inferredType is not ITypeParameterSymbol)
                return true;

            if (targetType is not INamedTypeSymbol targetNamed ||
                inferredType is not INamedTypeSymbol inferredNamed ||
                targetNamed.TypeArguments.IsDefaultOrEmpty ||
                inferredNamed.TypeArguments.IsDefaultOrEmpty ||
                targetNamed.TypeArguments.Length != inferredNamed.TypeArguments.Length)
            {
                return false;
            }

            var targetDefinition = targetNamed.OriginalDefinition as INamedTypeSymbol ?? targetNamed;
            var inferredDefinition = inferredNamed.OriginalDefinition as INamedTypeSymbol ?? inferredNamed;
            if (!SymbolEqualityComparer.Default.Equals(targetDefinition, inferredDefinition))
                return false;

            var targetHasPlaceholder = false;
            var inferredIsMoreConcrete = false;

            for (var i = 0; i < targetNamed.TypeArguments.Length; i++)
            {
                var targetArgument = targetNamed.TypeArguments[i];
                var inferredArgument = inferredNamed.TypeArguments[i];

                if (targetArgument is ITypeParameterSymbol)
                {
                    targetHasPlaceholder = true;
                    if (inferredArgument is not ITypeParameterSymbol)
                        inferredIsMoreConcrete = true;
                }
            }

            return targetHasPlaceholder && inferredIsMoreConcrete;
        }

        static bool IsDisallowedImplicitCommonType(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_ValueType)
                return true;

            if (type is INamedTypeSymbol named &&
                (named.TypeKind is TypeKind.Interface or TypeKind.TypeParameter || named.IsAbstract))
            {
                return true;
            }

            return false;
        }

        bool TryGetReferenceFallbackCommonType(
            ITypeSymbol inferredType,
            ITypeSymbol thenType,
            ITypeSymbol elseType,
            out ITypeSymbol fallbackType)
        {
            fallbackType = Compilation.ErrorTypeSymbol;

            if (inferredType.TypeKind == TypeKind.Error ||
                (IsAssignable(inferredType, thenType, out _) && IsAssignable(inferredType, elseType, out _)))
            {
                return false;
            }

            if (!IsReferenceLike(thenType) || !IsReferenceLike(elseType))
                return false;

            var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
            if (objectType.TypeKind == TypeKind.Error)
                return false;

            if (!IsAssignable(objectType, thenType, out _) || !IsAssignable(objectType, elseType, out _))
                return false;

            fallbackType = objectType;
            return true;
        }

        static bool IsReferenceLike(ITypeSymbol type)
            => !type.IsValueType &&
               type.TypeKind is not TypeKind.Error and not TypeKind.TypeParameter;
    }

    private ITypeSymbol? GetTargetType(SyntaxNode node)
    {
        // If the node is the callee of an invocation (e.g. `.Some` in `.Some(value)`),
        // recover contextual target typing from the invocation's parent context.
        if (node.Parent is InvocationExpressionSyntax invocation &&
            ReferenceEquals(invocation.Expression, node))
        {
            var invocationTargetType = GetTargetType(invocation);
            if (invocationTargetType is not null)
                return invocationTargetType;
        }

        // Target type from constructor arguments: `Theme(None)` / `Theme(.None)`.
        // Invocation binding also pushes candidate parameter types, but simple
        // target-typed expressions may ask for context while binding themselves.
        if (node.Parent is ArgumentSyntax argument &&
            ReferenceEquals(argument.Expression, node) &&
            node is ExpressionSyntax argumentExpression &&
            TryGetTargetTypedUnionCaseName(argumentExpression, out _) &&
            argument.Parent is ArgumentListSyntax argumentList &&
            argumentList.Parent is InvocationExpressionSyntax argumentInvocation &&
            argumentInvocation.Expression is TypeSyntax constructorTypeSyntax)
        {
            var constructorArgumentTargetType = GetConstructorArgumentTargetTypeFromSyntax(
                constructorTypeSyntax,
                argumentList.Arguments,
                argument);

            if (constructorArgumentTargetType is not null)
                return constructorArgumentTargetType;
        }

        // Target type from `return <expr>`.
        if (node.Parent is ReturnStatementSyntax)
        {
            var returnTargetType = GetContainingReturnTargetType();
            if (returnTargetType is not null)
                return returnTargetType;
        }

        // Target type from expression-bodied members and functions: `=> <expr>`.
        if (node.Parent is ArrowExpressionClauseSyntax)
        {
            var returnTargetType = GetContainingReturnTargetType();
            if (returnTargetType is not null)
                return returnTargetType;
        }

        // Target type from expression-bodied lambdas: `x => <expr>`.
        if (node.Parent is FunctionExpressionSyntax)
        {
            var returnTargetType = GetContainingReturnTargetType();
            if (returnTargetType is not null)
                return returnTargetType;
        }

        // Target type from binary equality/inequality: `x == .Member` / `.Member == x`.
        if (node.Parent is InfixOperatorExpressionSyntax binary &&
            node is ExpressionSyntax equalityOperand &&
            IsTargetTypedMemberBinding(equalityOperand) &&
            (binary.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.NotEqualsToken))
        {
            var other = ReferenceEquals(binary.Left, node) ? binary.Right : binary.Left;

            // Avoid recursion if the other side is also target-typed.
            if (other is not ExpressionSyntax otherExpression || !IsTargetTypedMemberBinding(otherExpression))
            {
                var otherBound = BindExpression(other);
                if (otherBound.Type is { } otherType &&
                    otherType.TypeKind != TypeKind.Error)
                {
                    return otherType;
                }
            }

            return null;
        }

        return GetTargetTypeFromBinderChain(node);
    }

    private static bool IsTargetTypedMemberBinding(ExpressionSyntax expression)
    {
        return expression is MemberBindingExpressionSyntax ||
               expression is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax };
    }

    private ITypeSymbol? GetTargetTypeFromBinderChain(SyntaxNode node)
    {
        for (Binder? binder = this; binder is not null; binder = binder.ParentBinder)
        {
            if (binder is BlockBinder blockBinder &&
                blockBinder._targetTypeStack.Count > 0)
            {
                var frame = blockBinder._targetTypeStack.Peek();
                return frame.AppliesToDescendants || ReferenceEquals(frame.Root, node)
                    ? frame.Type
                    : null;
            }
        }

        return null;
    }

    private ITypeSymbol? GetScopedTargetType(SyntaxNode node)
    {
        if (_targetTypeStack.Count == 0)
            return null;

        var frame = _targetTypeStack.Peek();
        return frame.AppliesToDescendants || ReferenceEquals(frame.Root, node)
            ? frame.Type
            : null;
    }

    private ITypeSymbol? GetConstructorArgumentTargetTypeFromSyntax(
        TypeSyntax typeSyntax,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        using var nonReportingScope = _diagnostics.CreateNonReportingScope();
        var boundType = BindTypeSyntaxAsExpression(typeSyntax);

        if (boundType is not BoundTypeExpression typeExpression ||
            typeExpression.Type is not INamedTypeSymbol typeSymbol)
        {
            return null;
        }

        return GetConstructorArgumentTargetType(typeSymbol, arguments, argument);
    }

    private ITypeSymbol? GetConstructorArgumentTargetType(
        INamedTypeSymbol typeSymbol,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        var argumentIndex = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == argument)
            {
                argumentIndex = i;
                break;
            }
        }

        if (argumentIndex < 0)
            return null;

        var argumentCount = arguments.Count;
        var argumentExpression = argument.Expression;
        var argumentName = argument.NameColon?.Name.Identifier.ValueText;

        var placeholderArguments = new BoundArgument[argumentCount];
        for (var i = 0; i < argumentCount; i++)
        {
            var syntax = arguments[i];
            var name = syntax.NameColon?.Name.Identifier.ValueText;
            placeholderArguments[i] = new BoundArgument(BoundFactory.NullLiteral(), RefKind.None, name, syntax);
        }

        var constructors = typeSymbol.Constructors
            .Where(m => AreArgumentsCompatibleWithMethod(m, argumentCount, receiver: null, placeholderArguments))
            .ToImmutableArray();

        RecordLambdaTargets(argumentExpression, constructors, argumentIndex, extensionReceiverImplicit: false);

        ITypeSymbol? parameterType = null;

        foreach (var constructor in constructors)
        {
            IParameterSymbol? parameter = null;
            if (!string.IsNullOrEmpty(argumentName))
                parameter = constructor.Parameters.FirstOrDefault(p => string.Equals(p.Name, argumentName, StringComparison.OrdinalIgnoreCase));
            else if (argumentIndex < constructor.Parameters.Length)
                parameter = constructor.Parameters[argumentIndex];

            if (parameter is null)
                continue;

            if (parameterType is null)
            {
                parameterType = parameter.Type;
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(parameterType, parameter.Type))
            {
                parameterType = null;
                break;
            }
        }

        return parameterType;
    }

    private ITypeSymbol GetReturnTargetType(IMethodSymbol method)
    {
        if (method is SourceMethodSymbol { HasAsyncReturnTypeError: true } or SourceLambdaSymbol { HasAsyncReturnTypeError: true })
            return method.ReturnType;

        var returnType = method.ReturnType;

        if (returnType is ErrorTypeSymbol)
        {
            return returnType;
        }

        if (method.IsAsync &&
            AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, returnType) is { } resultType &&
            resultType.SpecialType is not SpecialType.System_Unit and not SpecialType.System_Void)
        {
            return resultType;
        }

        return returnType;
    }

    private ITypeSymbol? GetContainingReturnTargetType()
    {
        if (_containingSymbol is IMethodSymbol method)
            return GetReturnTargetType(method);

        if (_containingSymbol is ILambdaSymbol lambda)
            return GetReturnTargetType(lambda);

        if (_containingSymbol is IPropertySymbol property)
            return property.Type;

        return null;
    }

    protected BoundExpression BindTypeSyntaxAsExpression(TypeSyntax syntax)
    {
        if (syntax is NullTypeSyntax)
        {
            return new BoundTypeExpression(Compilation.NullTypeSymbol);
        }

        if (syntax is IdentifierNameSyntax id)
        {
            return BindTypeName(id.Identifier.ValueText, id.GetLocation(), []);
        }

        if (syntax is PredefinedTypeSyntax predefinedType)
        {
            var type = Compilation.ResolvePredefinedType(predefinedType);
            return new BoundTypeExpression(type);
        }

        if (syntax is UnitTypeSyntax)
        {
            var type = Compilation.GetSpecialType(SpecialType.System_Unit);
            return new BoundTypeExpression(type);
        }

        if (syntax is NullableTypeSyntax nullableTypeSyntax)
        {
            var type = BindTypeSyntaxAsExpression(nullableTypeSyntax.ElementType);
            return new BoundTypeExpression(type.Type.GetNullableType());
        }

        if (syntax is PointerTypeSyntax pointerTypeSyntax)
        {
            if (!IsUnsafeEnabled)
            {
                _diagnostics.ReportPointerTypeRequiresUnsafe(pointerTypeSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }

            if (BindTypeSyntaxAsExpression(pointerTypeSyntax.ElementType) is not BoundTypeExpression elementTypeExpression)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            var elementType = elementTypeExpression.Type;

            if (elementType.ContainsErrorType())
                return new BoundTypeExpression(elementType);

            var pointerType = Compilation.CreatePointerTypeSymbol(elementType);
            return new BoundTypeExpression(pointerType);
        }

        if (syntax is ArrayTypeSyntax arrayTypeSyntax)
        {
            if (BindTypeSyntaxAsExpression(arrayTypeSyntax.ElementType) is not BoundTypeExpression elementTypeExpression)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            var elementType = elementTypeExpression.Type;

            if (elementType.ContainsErrorType())
                return new BoundTypeExpression(elementType);

            foreach (var rankSpecifier in arrayTypeSyntax.RankSpecifiers)
            {
                var rank = rankSpecifier.CommaTokens.Count + 1;
                elementType = Compilation.CreateArrayTypeSymbol(elementType, rank, TryGetFixedArraySize(rankSpecifier));
            }

            return new BoundTypeExpression(elementType);
        }

        if (syntax is TupleTypeSyntax tupleTypeSyntax)
        {
            var boundElements = new List<(string? name, ITypeSymbol type)>();

            foreach (var element in tupleTypeSyntax.Elements)
            {
                if (BindTypeSyntaxAsExpression(element.Type) is not BoundTypeExpression bt)
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

                boundElements.Add((element.NameColon?.Name.ToString(), bt.Type));
            }

            var tupleType = Compilation.CreateTupleTypeSymbol(boundElements);

            return new BoundTypeExpression(tupleType);
        }

        if (syntax is GenericNameSyntax generic)
        {
            if (!TryBindTypeArguments(generic, out var typeArgs, out var requestedArity))
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            return BindTypeName(
                generic.Identifier.ValueText,
                generic.GetLocation(),
                typeArgs,
                generic.TypeArgumentList.Arguments,
                requestedArity);
        }

        if (syntax is QualifiedNameSyntax qualified)
        {
            var left = BindTypeSyntaxAsExpression(qualified.Left);

            if (left is not BoundNamespaceExpression ns && left is not BoundTypeExpression leftType)
                return ErrorExpression(reason: BoundExpressionReason.NotFound);

            string name;
            ImmutableArray<ITypeSymbol> typeArgs = [];

            GenericNameSyntax? rightGeneric = null;
            int requestedArity = 0;

            if (qualified.Right is IdentifierNameSyntax id2)
            {
                name = id2.Identifier.ValueText;
            }
            else if (qualified.Right is GenericNameSyntax generic2)
            {
                name = generic2.Identifier.ValueText;
                rightGeneric = generic2;
                if (!TryBindTypeArguments(generic2, out typeArgs, out requestedArity))
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }
            else
            {
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            if (rightGeneric is null)
                requestedArity = typeArgs.Length;

            ISymbol? member = left switch
            {
                BoundNamespaceExpression nsExpr => nsExpr.Namespace.GetMembers(name)
                    .FirstOrDefault(s => s is INamespaceSymbol ||
                        (s is INamedTypeSymbol type && type.Arity == requestedArity)),
                BoundTypeExpression typeExpr => typeExpr.Type.GetMembers(name)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault(m => m.Arity == requestedArity),
                _ => null
            };

            if (member is INamespaceSymbol nsResult)
                return new BoundNamespaceExpression(nsResult);

            if (member is INamedTypeSymbol namedType)
            {
                if (namedType.Arity != requestedArity)
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

                if (!typeArgs.IsEmpty && rightGeneric is not null && !ValidateTypeArgumentConstraints(namedType, typeArgs, i => GetTypeArgumentLocation(rightGeneric.TypeArgumentList.Arguments, rightGeneric.GetLocation(), i), namedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

                var constructed = typeArgs.IsEmpty
                    ? namedType
                    : TryConstructGeneric(namedType, typeArgs, namedType.Arity) ?? namedType;

                if (!typeArgs.IsEmpty &&
                    constructed is ConstructedNamedTypeSymbol constructedNamed &&
                    constructedNamed.TypeArguments.Any(static argument => argument is ITypeParameterSymbol))
                {
                    var fallback = constructedNamed.Construct(typeArgs.ToArray());
                    if (fallback is INamedTypeSymbol substituted)
                        constructed = substituted;
                }

                return new BoundTypeExpression(constructed);
            }

            if (member is null && left is BoundNamespaceExpression namespaceExpression && typeArgs.IsEmpty)
            {
                var metadataName = namespaceExpression.Namespace.QualifyName(name);
                if (!string.IsNullOrEmpty(metadataName))
                {
                    if (requestedArity > 0)
                        metadataName = string.Concat(metadataName, "`", requestedArity);

                    var metadataType = Compilation.GetTypeByMetadataName(metadataName);
                    if (metadataType is not null && metadataType.TypeKind != TypeKind.Error)
                        return new BoundTypeExpression(metadataType);
                }
            }

            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindCompoundAssignment(
        ExpressionSyntax leftSyntax,
        ExpressionSyntax rightSyntax,
        SyntaxNode node,
        SyntaxKind operatorTokenKind)
    {
        var binaryOperator = GetBinaryOperatorFromAssignment(operatorTokenKind);
        if (binaryOperator is null)
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);

        if (leftSyntax is DiscardExpressionSyntax)
        {
            _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        if (leftSyntax is ElementAccessExpressionSyntax elementAccess)
        {
            var receiver = BindExpression(elementAccess.Expression);
            var args = elementAccess.ArgumentList.Arguments.Select(x => BindExpression(x.Expression)).ToArray();

            if (IsErrorExpression(receiver))
                return receiver is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(
                        receiver.Type ?? Compilation.ErrorTypeSymbol,
                        null,
                        BoundExpressionReason.OtherError);

            var firstErrorArg = args.FirstOrDefault(IsErrorExpression);
            if (firstErrorArg is not null)
                return firstErrorArg is BoundErrorExpression errorArg
                    ? errorArg
                    : new BoundErrorExpression(
                        firstErrorArg.Type ?? Compilation.ErrorTypeSymbol,
                        null,
                        BoundExpressionReason.OtherError);

            if (receiver.Type is IArrayTypeSymbol arrayType)
            {
                if (args.Any(argument => IsRangeType(argument.Type)))
                {
                    _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                    return new BoundErrorExpression(receiver.Type!, null, BoundExpressionReason.NotFound);
                }

                if (!ValidateArrayElementAccess(arrayType, args, node))
                {
                    _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                    return new BoundErrorExpression(receiver.Type!, null, BoundExpressionReason.NotFound);
                }

                var arrayRightExpression = BindExpressionWithTargetType(rightSyntax, arrayType.ElementType);

                if (IsErrorExpression(arrayRightExpression))
                    return AsErrorExpression(arrayRightExpression);

                var arrayAccess = new BoundArrayAccessExpression(receiver, args, arrayType.ElementType);
                var arrayRight = BindCompoundAssignmentValue(arrayAccess, arrayRightExpression, arrayType.ElementType, binaryOperator.Value, rightSyntax);
                return BoundFactory.CreateArrayAssignmentExpression(arrayAccess, arrayRight);
            }

            var indexer = ResolveIndexer(receiver.Type!, args, elementAccess.ArgumentList.Arguments, requireSetter: true, out var convertedArguments);

            if (indexer is null || !indexer.IsMutable)
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundErrorExpression(receiver.Type!, null, BoundExpressionReason.NotFound);
            }

            var indexerRightExpression = BindExpressionWithTargetType(rightSyntax, indexer.Type);

            if (IsErrorExpression(indexerRightExpression))
                return AsErrorExpression(indexerRightExpression);

            var indexerAccess = new BoundIndexerAccessExpression(receiver, convertedArguments, indexer);
            var indexerRight = BindCompoundAssignmentValue(indexerAccess, indexerRightExpression, indexer.Type, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreateIndexerAssignmentExpression(indexerAccess, indexerRight);
        }

        var left = BindExpressionAllowingEvent(leftSyntax);

        if (IsErrorExpression(left))
            return AsErrorExpression(left);

        if (left.Symbol is IEventSymbol eventSymbol)
        {
            if (operatorTokenKind is not (SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken))
            {
                _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(eventSymbol.Name, leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var right = BindExpressionWithTargetType(rightSyntax, eventSymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            var eventType = eventSymbol.Type;
            var converted = ConvertValueForAssignment(right, eventType, rightSyntax);
            if (converted is BoundErrorExpression)
                return converted;

            var receiver = GetReceiver(left);
            var accessor = operatorTokenKind == SyntaxKind.PlusEqualsToken
                ? eventSymbol.AddMethod
                : eventSymbol.RemoveMethod;

            if (accessor is null)
                return ErrorExpression(reason: BoundExpressionReason.NotFound);

            if (!EnsureMemberAccessible(accessor, leftSyntax.GetLocation(), GetSymbolKindForDiagnostic(accessor)))
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

            return new BoundInvocationExpression(accessor, [converted], receiver);
        }

        if (left is BoundConditionalAccessExpression conditionalAccess &&
            conditionalAccess.WhenNotNull is BoundMemberAccessExpression { Member: IEventSymbol conditionalEventSymbol } eventAccess)
        {
            if (operatorTokenKind is not (SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken))
            {
                _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(conditionalEventSymbol.Name, leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var right = BindExpressionWithTargetType(rightSyntax, conditionalEventSymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            var eventType = conditionalEventSymbol.Type;
            var converted = ConvertValueForAssignment(right, eventType, rightSyntax);
            if (converted is BoundErrorExpression)
                return converted;

            var accessor = operatorTokenKind == SyntaxKind.PlusEqualsToken
                ? conditionalEventSymbol.AddMethod
                : conditionalEventSymbol.RemoveMethod;

            if (accessor is null)
                return ErrorExpression(reason: BoundExpressionReason.NotFound);

            if (!EnsureMemberAccessible(accessor, leftSyntax.GetLocation(), GetSymbolKindForDiagnostic(accessor)))
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

            var invocation = new BoundInvocationExpression(accessor, [converted], eventAccess.Receiver);
            var resultType = invocation.Type ?? Compilation.ErrorTypeSymbol;
            if (!resultType.IsNullable)
                resultType = resultType.GetNullableType();

            return new BoundConditionalAccessExpression(conditionalAccess.Receiver, invocation, resultType);
        }

        if (left is BoundConditionalAccessExpression conditionalAssignmentAccess &&
            leftSyntax is ConditionalAccessExpressionSyntax conditionalSyntax)
        {
            return BindConditionalAccessAssignment(conditionalSyntax, conditionalAssignmentAccess, rightSyntax, node, operatorTokenKind);
        }

        if (left is BoundLocalAccess localAccess)
        {
            var localSymbol = localAccess.Local;
            var localType = localSymbol.Type;
            var rightTargetType = localType is RefTypeSymbol refTypeLocal
                ? refTypeLocal.ElementType
                : localType;
            var right = BindExpressionWithTargetType(rightSyntax, rightTargetType);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (localType is RefTypeSymbol refTypeLocalType)
            {
                var localByRefRight = BindCompoundAssignmentValue(localAccess, right, refTypeLocalType.ElementType, binaryOperator.Value, rightSyntax);
                if (localByRefRight is BoundErrorExpression)
                    return localByRefRight;

                return BoundFactory.CreateByRefAssignmentExpression(localAccess, refTypeLocalType.ElementType, localByRefRight);
            }

            if (!localSymbol.IsMutable)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var localRight = BindCompoundAssignmentValue(localAccess, right, localType, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreateLocalAssignmentExpression(localSymbol, localAccess, localRight);
        }
        else if (left is BoundParameterAccess parameterAccess)
        {
            var parameterSymbol = parameterAccess.Parameter;
            var parameterType = parameterSymbol.Type;
            var rightTargetType = parameterSymbol.RefKind is RefKind.Ref or RefKind.Out
                ? parameterSymbol.GetByRefElementType()
                : parameterType;
            var right = BindExpressionWithTargetType(rightSyntax, rightTargetType);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (!parameterSymbol.IsMutable)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            if (parameterSymbol.RefKind is RefKind.Ref or RefKind.Out)
            {
                var byRefElementType = parameterSymbol.GetByRefElementType();
                var parameterByRefRight = BindCompoundAssignmentValue(parameterAccess, right, byRefElementType, binaryOperator.Value, rightSyntax);
                if (parameterByRefRight is BoundErrorExpression)
                    return parameterByRefRight;

                return BoundFactory.CreateByRefAssignmentExpression(parameterAccess, byRefElementType, parameterByRefRight);
            }

            var parameterRight = BindCompoundAssignmentValue(parameterAccess, right, parameterType, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreateParameterAssignmentExpression(parameterSymbol, parameterAccess, parameterRight);
        }
        else if (left.Symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.IsConst)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.NotFound);
            }

            var receiver = GetReceiver(left);

            var right = BindExpressionWithTargetType(rightSyntax, fieldSymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (!CanAssignToField(fieldSymbol, receiver, leftSyntax))
                return new BoundErrorExpression(fieldSymbol.Type, fieldSymbol, BoundExpressionReason.NotFound);

            var access = left is BoundPointerMemberAccessExpression
                ? left
                : new BoundFieldAccess(receiver, fieldSymbol);

            var fieldRight = BindCompoundAssignmentValue(access, right, fieldSymbol.Type, binaryOperator.Value, rightSyntax);
            return CreateFieldAssignmentExpression(receiver, fieldSymbol, fieldRight);
        }
        else if (left.Symbol is IPropertySymbol propertySymbol)
        {
            SourceFieldSymbol? backingField = null;
            var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);

            var receiver = GetReceiver(left);

            if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
            {
                if (!TryGetWritableAutoPropertyBackingField(propertySymbol, left, out backingField))
                {
                    _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }
            }

            var right = BindExpressionWithTargetType(rightSyntax, propertySymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (backingField is not null)
            {
                var backingAccess = new BoundFieldAccess(receiver, backingField);
                if (!CanAssignToField(backingField, receiver, leftSyntax))
                    return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);
                var backingRight = BindCompoundAssignmentValue(backingAccess, right, propertySymbol.Type, binaryOperator.Value, rightSyntax);
                return CreateFieldAssignmentExpression(receiver, backingField, backingRight);
            }

            var propertyAccess = new BoundMemberAccessExpression(receiver, propertySymbol);
            var result = BindCompoundAssignmentValue(propertyAccess, right, propertySymbol.Type, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, result);
        }

        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindConditionalAccessAssignment(
        ConditionalAccessExpressionSyntax syntax,
        BoundConditionalAccessExpression conditionalAccess,
        ExpressionSyntax rightSyntax,
        SyntaxNode node,
        SyntaxKind operatorTokenKind)
    {
        var receiver = conditionalAccess.Receiver;
        var receiverType = receiver.Type ?? Compilation.ErrorTypeSymbol;

        if (receiverType is NullableTypeSymbol { UnderlyingType.IsValueType: true })
        {
            _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var receiverLocal = CreateTempLocal("conditionalReceiver", receiverType, syntax.Expression);
        var receiverAccess = new BoundLocalAccess(receiverLocal);
        var lookupType = GetConditionalAccessLookupType(receiverType);
        var whenNotNullReceiver = GetConditionalAccessWhenNotNullReceiver(receiverAccess, lookupType);

        BoundExpression target = syntax.WhenNotNull switch
        {
            MemberBindingExpressionSyntax memberBinding => BindMemberAccessOnReceiver(
                whenNotNullReceiver,
                memberBinding.Name,
                preferMethods: false,
                allowEventAccess: true,
                suppressNullWarning: true,
                receiverTypeForLookup: lookupType,
                forceExtensionReceiver: true),
            ElementBindingExpressionSyntax elementBinding => BindElementAccessExpression(
                whenNotNullReceiver,
                elementBinding.ArgumentList,
                elementBinding,
                suppressNullWarning: true),
            _ => ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation)
        };

        if (IsErrorExpression(target))
            return AsErrorExpression(target);

        var assignment = operatorTokenKind == SyntaxKind.EqualsToken
            ? BindSimpleAssignmentToBoundTarget(target, rightSyntax, node, syntax)
            : BindCompoundAssignmentToBoundTarget(target, rightSyntax, node, syntax, operatorTokenKind);

        if (IsErrorExpression(assignment))
            return AsErrorExpression(assignment);

        var condition = CreateNullableHasValueCondition(receiverAccess);
        if (condition is BoundErrorExpression conditionError)
            return conditionError;

        return new BoundBlockExpression(
        [
            new BoundLocalDeclarationStatement([new BoundVariableDeclarator(receiverLocal, receiver)]),
            new BoundIfStatement(condition, new BoundExpressionStatement(assignment))
        ],
        Compilation.GetSpecialType(SpecialType.System_Unit));
    }

    private BoundExpression BindSimpleAssignmentToBoundTarget(
        BoundExpression left,
        ExpressionSyntax rightSyntax,
        SyntaxNode node,
        SyntaxNode leftSyntax)
    {
        if (left is BoundArrayAccessExpression arrayAccess)
        {
            var arrayType = arrayAccess.Type;
            var arrayRightExpression = BindExpressionWithTargetType(rightSyntax, arrayType);

            if (IsErrorExpression(arrayRightExpression))
                return AsErrorExpression(arrayRightExpression);

            if (arrayType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(arrayRightExpression))
            {
                var right = BindLambdaToDelegateIfNeeded(arrayRightExpression, arrayType);
                if (!IsAssignable(arrayType, right.Type, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right.Type, arrayType, rightSyntax.GetLocation());
                    return new BoundErrorExpression(arrayType, null, BoundExpressionReason.TypeMismatch);
                }

                arrayRightExpression = ApplyConversion(right, arrayType, conversion, rightSyntax);
            }

            return BoundFactory.CreateArrayAssignmentExpression(arrayAccess, arrayRightExpression);
        }

        if (left is BoundIndexerAccessExpression indexerAccess)
        {
            var indexer = indexerAccess.Indexer;
            if (!indexer.IsMutable)
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundErrorExpression(indexer.Type, null, BoundExpressionReason.NotFound);
            }

            var indexerRightExpression = BindExpressionWithTargetType(rightSyntax, indexer.Type);

            if (IsErrorExpression(indexerRightExpression))
                return AsErrorExpression(indexerRightExpression);

            if (indexer.Type.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(indexerRightExpression))
            {
                var right = BindLambdaToDelegateIfNeeded(indexerRightExpression, indexer.Type);
                if (!IsAssignable(indexer.Type, right.Type, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right.Type, indexer.Type, rightSyntax.GetLocation());
                    return new BoundErrorExpression(indexer.Type, null, BoundExpressionReason.TypeMismatch);
                }

                indexerRightExpression = ApplyConversion(right, indexer.Type, conversion, rightSyntax);
            }

            return BoundFactory.CreateIndexerAssignmentExpression(indexerAccess, indexerRightExpression);
        }

        if (left.Symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.IsConst)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.NotFound);
            }

            var receiver = GetReceiver(left);
            var right = BindExpressionWithTargetType(rightSyntax, fieldSymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (!CanAssignToField(fieldSymbol, receiver, leftSyntax))
                return new BoundErrorExpression(fieldSymbol.Type, fieldSymbol, BoundExpressionReason.NotFound);

            if (right is BoundEmptyCollectionExpression)
                return CreateFieldAssignmentExpression(receiver, fieldSymbol, BoundFactory.CreateEmptyCollectionExpression(fieldSymbol.Type));

            if (fieldSymbol.Type.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right))
            {
                right = BindLambdaToDelegateIfNeeded(right, fieldSymbol.Type);
                if (!IsAssignable(fieldSymbol.Type, right.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right.Type!, fieldSymbol.Type, rightSyntax.GetLocation());
                    return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.TypeMismatch);
                }

                right = ApplyConversion(right, fieldSymbol.Type, conversion, rightSyntax);
            }

            return CreateFieldAssignmentExpression(receiver, fieldSymbol, right);
        }

        if (left.Symbol is IPropertySymbol propertySymbol)
        {
            SourceFieldSymbol? backingField = null;
            var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);
            var receiver = GetReceiver(left);

            if (IsInitOnly(propertySymbol) && !IsInInitOnlyAssignmentContext)
            {
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                return new BoundErrorExpression(propertySymbol.Type ?? Compilation.ErrorTypeSymbol, propertySymbol, BoundExpressionReason.UnsupportedOperation);
            }

            if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
            {
                if (!TryGetWritableAutoPropertyBackingField(propertySymbol, left, out backingField))
                {
                    _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }
            }

            var right = BindExpressionWithTargetType(rightSyntax, propertySymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (right is BoundEmptyCollectionExpression)
            {
                var empty = new BoundEmptyCollectionExpression(propertySymbol.Type);

                if (backingField is not null)
                {
                    if (!CanAssignToField(backingField, receiver, leftSyntax))
                        return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

                    return CreateFieldAssignmentExpression(receiver, backingField, empty);
                }

                return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, empty);
            }

            if (propertySymbol.Type.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(right))
            {
                right = BindLambdaToDelegateIfNeeded(right, propertySymbol.Type);
                if (!IsAssignable(propertySymbol.Type, right.Type!, out var conversion))
                {
                    ReportCannotAssignFromTypeToType(right.Type!, propertySymbol.Type, rightSyntax.GetLocation());
                    return new BoundErrorExpression(propertySymbol.Type, null, BoundExpressionReason.TypeMismatch);
                }

                right = ApplyConversion(right, propertySymbol.Type, conversion, rightSyntax);
            }

            if (backingField is not null)
            {
                if (!CanAssignToField(backingField, receiver, leftSyntax))
                    return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

                return CreateFieldAssignmentExpression(receiver, backingField, right);
            }

            return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, right);
        }

        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindCompoundAssignmentToBoundTarget(
        BoundExpression left,
        ExpressionSyntax rightSyntax,
        SyntaxNode node,
        SyntaxNode leftSyntax,
        SyntaxKind operatorTokenKind)
    {
        var binaryOperator = GetBinaryOperatorFromAssignment(operatorTokenKind);
        if (binaryOperator is null)
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);

        if (left is BoundArrayAccessExpression arrayAccess)
        {
            var arrayType = arrayAccess.Type;
            var right = BindExpressionWithTargetType(rightSyntax, arrayType);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            var arrayRight = BindCompoundAssignmentValue(arrayAccess, right, arrayType, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreateArrayAssignmentExpression(arrayAccess, arrayRight);
        }

        if (left is BoundIndexerAccessExpression indexerAccess)
        {
            var indexer = indexerAccess.Indexer;
            if (!indexer.IsMutable)
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundErrorExpression(indexer.Type, null, BoundExpressionReason.NotFound);
            }

            var right = BindExpressionWithTargetType(rightSyntax, indexer.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            var indexerRight = BindCompoundAssignmentValue(indexerAccess, right, indexer.Type, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreateIndexerAssignmentExpression(indexerAccess, indexerRight);
        }

        if (left.Symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.IsConst)
            {
                _diagnostics.ReportThisValueIsNotMutable(leftSyntax.GetLocation());
                return new BoundErrorExpression(fieldSymbol.Type, null, BoundExpressionReason.NotFound);
            }

            var receiver = GetReceiver(left);
            var right = BindExpressionWithTargetType(rightSyntax, fieldSymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (!CanAssignToField(fieldSymbol, receiver, leftSyntax))
                return new BoundErrorExpression(fieldSymbol.Type, fieldSymbol, BoundExpressionReason.NotFound);

            var access = left is BoundPointerMemberAccessExpression
                ? left
                : new BoundFieldAccess(receiver, fieldSymbol);
            var fieldRight = BindCompoundAssignmentValue(access, right, fieldSymbol.Type, binaryOperator.Value, rightSyntax);
            return CreateFieldAssignmentExpression(receiver, fieldSymbol, fieldRight);
        }

        if (left.Symbol is IPropertySymbol propertySymbol)
        {
            SourceFieldSymbol? backingField = null;
            var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);
            var receiver = GetReceiver(left);

            if (!useFieldOnlyLowering && !propertySymbol.IsMutable)
            {
                if (!TryGetWritableAutoPropertyBackingField(propertySymbol, left, out backingField))
                {
                    _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, leftSyntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.NotFound);
                }
            }

            var right = BindExpressionWithTargetType(rightSyntax, propertySymbol.Type);

            if (IsErrorExpression(right))
                return AsErrorExpression(right);

            if (backingField is not null)
            {
                var backingAccess = new BoundFieldAccess(receiver, backingField);
                if (!CanAssignToField(backingField, receiver, leftSyntax))
                    return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

                var backingRight = BindCompoundAssignmentValue(backingAccess, right, propertySymbol.Type, binaryOperator.Value, rightSyntax);
                return CreateFieldAssignmentExpression(receiver, backingField, backingRight);
            }

            var propertyAccess = new BoundMemberAccessExpression(receiver, propertySymbol);
            var result = BindCompoundAssignmentValue(propertyAccess, right, propertySymbol.Type, binaryOperator.Value, rightSyntax);
            return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, result);
        }

        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private BoundExpression BindTypeName(string name, Location location, ImmutableArray<ITypeSymbol> typeArguments, SeparatedSyntaxList<TypeArgumentSyntax> typeArgumentSyntax = default, int? arityOverride = null)
    {
        var symbol = LookupType(name);

        var requestedArity = arityOverride ?? typeArguments.Length;

        if (symbol is null && typeArguments.IsEmpty)
        {
            var namespaceSymbol = LookupNamespace(name);
            if (namespaceSymbol is not null)
                return new BoundNamespaceExpression(namespaceSymbol);
        }

        if (symbol is ITypeSymbol type && type is INamedTypeSymbol named)
        {
            // If the resolved symbol is already a constructed generic type (e.g., from an alias
            // like `alias StringList = System.Collections.Generic.List<string>`), then we don't
            // expect any additional type arguments when the alias is used. Return the constructed
            // type directly.
            if (named.ConstructedFrom is not null && !SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, named))
            {
                if (!typeArguments.IsEmpty)
                {
                    //_diagnostics.ReportTypeArityMismatch(name, named.Arity, typeArguments.Length, location);
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
                }

                return new BoundTypeExpression(named);
            }

            var definition = NormalizeDefinition(named);

            if (definition.Arity != requestedArity)
            {
                var match = FindAccessibleNamedType(name, requestedArity);
                if (match is null)
                    return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

                definition = match;
            }

            if (typeArguments.IsEmpty)
                return new BoundTypeExpression(definition);

            if (!ValidateTypeArgumentConstraints(definition, typeArguments, i => GetTypeArgumentLocation(typeArgumentSyntax, location, i), definition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            var constructed = TryConstructGeneric(definition, typeArguments, definition.Arity) ?? definition;
            return new BoundTypeExpression(constructed);
        }

        if (symbol is ITypeSymbol nonNamedType)
        {
            return new BoundTypeExpression(EnsureTypeAccessible(nonNamedType, location));
        }

        var alternate = FindAccessibleNamedType(name, requestedArity);
        if (alternate is not null)
        {
            if (typeArguments.IsEmpty)
                return new BoundTypeExpression(alternate);

            if (!ValidateTypeArgumentConstraints(alternate, typeArguments, i => GetTypeArgumentLocation(typeArgumentSyntax, location, i), alternate.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            var constructed = TryConstructGeneric(alternate, typeArguments, alternate.Arity) ?? alternate;
            return new BoundTypeExpression(constructed);
        }

        _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, location);
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private bool TryBindTypeArguments(GenericNameSyntax generic, out ImmutableArray<ITypeSymbol> boundArguments, out int requestedArity)
    {
        requestedArity = GetTypeArgumentArity(generic.TypeArgumentList);

        var argumentSyntax = generic.TypeArgumentList.Arguments;
        if (argumentSyntax.Count == 0)
        {
            boundArguments = ImmutableArray<ITypeSymbol>.Empty;
            return true;
        }

        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(argumentSyntax.Count);
        var hasExplicitArguments = false;
        var hasOmittedArguments = false;

        foreach (var argument in argumentSyntax)
        {
            if (argument.Type.IsMissing)
            {
                hasOmittedArguments = true;
                continue;
            }

            hasExplicitArguments = true;

            if (BindTypeSyntaxAsExpression(argument.Type) is not BoundTypeExpression bt)
            {
                boundArguments = ImmutableArray<ITypeSymbol>.Empty;
                return false;
            }

            builder.Add(bt.Type);
        }

        if (hasExplicitArguments && hasOmittedArguments)
        {
            boundArguments = ImmutableArray<ITypeSymbol>.Empty;
            return false;
        }

        boundArguments = builder.ToImmutable();
        if (hasExplicitArguments && boundArguments.Length != requestedArity)
            return false;

        return true;
    }

    private static int GetTypeArgumentArity(TypeArgumentListSyntax typeArgumentList)
    {
        var arguments = typeArgumentList.Arguments;
        var argumentCount = arguments.Count;
        var separatorCount = arguments.SeparatorCount;

        if (argumentCount == 0)
        {
            // `<>` represents an open generic with a single type parameter.
            return separatorCount == 0 ? 1 : separatorCount + 1;
        }

        // When omitted type arguments are present (e.g., `<,>`), the separator count
        // reflects the intended arity even though some argument nodes may be missing.
        var inferredArity = separatorCount + 1;
        return Math.Max(argumentCount, inferredArity);
    }

    private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
    {
        if (syntax.Kind == SyntaxKind.NullLiteralExpression)
        {
            ITypeSymbol? convertedType = null;
            var targetType = GetTargetType(syntax);

            if (targetType is not null)
            {
                var conversion = Compilation.ClassifyConversion(Compilation.NullTypeSymbol, targetType);
                if (conversion.Exists && conversion.IsImplicit)
                    convertedType = targetType;
            }

            return BoundFactory.NullLiteral(convertedType);
        }

        var value = syntax.Token.Value ?? syntax.Token.Text!;
        if (value is EncodedStringLiteralValue encodedStringLiteral)
            return BindEncodedStringLiteral(syntax, encodedStringLiteral);

        var contextualTargetType = GetTargetType(syntax);
        var underlying = value switch
        {
            byte => Compilation.GetSpecialType(SpecialType.System_Byte),
            int => Compilation.GetSpecialType(SpecialType.System_Int32),
            long => Compilation.GetSpecialType(SpecialType.System_Int64),
            float => Compilation.GetSpecialType(SpecialType.System_Single),
            double => Compilation.GetSpecialType(SpecialType.System_Double),
            decimal => Compilation.GetSpecialType(SpecialType.System_Decimal),
            bool => Compilation.GetSpecialType(SpecialType.System_Boolean),
            char => Compilation.GetSpecialType(SpecialType.System_Char),
            string => Compilation.GetSpecialType(SpecialType.System_String),
            _ => null
        };

        if (underlying is null)
        {
            _diagnostics.ReportInvalidExpressionTerm(syntax.Token.Text, syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        ITypeSymbol type = underlying;

        BoundLiteralExpressionKind? kind = syntax.Kind switch
        {
            SyntaxKind.NumericLiteralExpression => BoundLiteralExpressionKind.NumericLiteral,
            SyntaxKind.StringLiteralExpression => BoundLiteralExpressionKind.StringLiteral,
            SyntaxKind.CharacterLiteralExpression => BoundLiteralExpressionKind.CharLiteral,
            SyntaxKind.TrueLiteralExpression => BoundLiteralExpressionKind.TrueLiteral,
            SyntaxKind.FalseLiteralExpression => BoundLiteralExpressionKind.FalseLiteral,
            SyntaxKind.NullLiteralExpression => BoundLiteralExpressionKind.NullLiteral,
            _ => null
        };

        if (kind is null)
        {
            _diagnostics.ReportInvalidExpressionTerm(syntax.Token.Text, syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        return new BoundLiteralExpression(kind.Value, value, type);
    }

    private BoundExpression BindEncodedStringLiteral(
        LiteralExpressionSyntax syntax,
        EncodedStringLiteralValue encodedStringLiteral)
    {
        var byteType = Compilation.GetSpecialType(SpecialType.System_Byte);
        var byteArrayType = Compilation.CreateArrayTypeSymbol(byteType);

        if (encodedStringLiteral.ContainsInterpolation)
        {
            _diagnostics.Report(Diagnostic.Create(
                CompilerDiagnostics.EncodedStringLiteralInterpolationNotSupported,
                syntax.GetLocation()));
            return ErrorExpression(byteArrayType, reason: BoundExpressionReason.TypeMismatch);
        }

        byte[] bytes;
        if (encodedStringLiteral.Encoding == EncodedStringLiteralEncoding.Utf8)
        {
            bytes = Encoding.UTF8.GetBytes(encodedStringLiteral.Text);
        }
        else
        {
            foreach (var rune in encodedStringLiteral.Text.EnumerateRunes())
            {
                if (rune.Value <= 0x7F)
                    continue;

                _diagnostics.Report(Diagnostic.Create(
                    CompilerDiagnostics.EncodedStringLiteralAsciiOutOfRange,
                    syntax.GetLocation(),
                    rune.ToString()));
                return ErrorExpression(byteArrayType, reason: BoundExpressionReason.TypeMismatch);
            }

            bytes = Encoding.ASCII.GetBytes(encodedStringLiteral.Text);
        }

        var elements = new BoundExpression[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            elements[i] = new BoundLiteralExpression(
                BoundLiteralExpressionKind.NumericLiteral,
                bytes[i],
                byteType);
        }

        return new BoundCollectionExpression(byteArrayType, elements);
    }

    private static bool ShouldPreserveLiteralPrecision(ITypeSymbol? targetType)
    {
        if (targetType is null)
            return true;

        return RequiresLiteralPrecision(targetType);
    }

    private static bool RequiresLiteralPrecision(ITypeSymbol type)
    {
        type = UnwrapAlias(type);

        if (type is LiteralTypeSymbol)
            return true;

        return false;
    }

    private BoundExpression BindDefaultExpression(DefaultExpressionSyntax syntax)
    {
        if (syntax.Type is TypeSyntax explicitType)
        {
            var type = ResolveTypeSyntaxOrError(explicitType);
            type = EnsureTypeAccessible(type, explicitType.GetLocation());

            return new BoundDefaultValueExpression(type.GetDefaultValueType());
        }

        var targetType = GetTargetType(syntax);

        if (targetType is null)
        {
            _diagnostics.ReportDefaultLiteralRequiresTargetType(syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        if (targetType.TypeKind == TypeKind.Error)
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        var expression = new BoundDefaultValueExpression(targetType.GetDefaultValueType());
        if (syntax.Parent is ArrowExpressionClauseSyntax &&
            ReportStructUnionReturnMayBeDefault(targetType, expression, syntax))
        {
            return new BoundErrorExpression(targetType, null, BoundExpressionReason.OtherError);
        }

        return expression;
    }

    private BoundExpression BindUnitExpression(UnitExpressionSyntax syntax)
    {
        return BoundFactory.UnitExpression();
    }

    private BoundExpression BindInterpolatedStringExpression(InterpolatedStringExpressionSyntax syntax)
    {
        // Normal interpolated strings: bind directly.
        // Multiline interpolated strings (triple-quoted) should apply indentation trimming in binding/lowering,
        // because the syntax tree must preserve raw spans while the runtime value should match multiline literal semantics.

        var isMultiline = syntax.StringStartToken.Text == "\"\"\"";

        BoundExpression? result = null;

        if (!isMultiline)
        {
            foreach (var content in syntax.Contents)
            {
                BoundExpression expr = content switch
                {
                    InterpolatedStringTextSyntax text => new BoundLiteralExpression(
                        BoundLiteralExpressionKind.StringLiteral,
                        text.Token.ValueText ?? string.Empty,
                        Compilation.GetSpecialType(SpecialType.System_String)),
                    InterpolationSyntax interpolation => BindExpression(interpolation.Expression),
                    _ => BindInvalidContent(content)
                };

                result = ConcatOrFirst(result, expr);
            }

            return result ?? new BoundLiteralExpression(
                BoundLiteralExpressionKind.StringLiteral,
                string.Empty,
                Compilation.GetSpecialType(SpecialType.System_String));
        }

        // Multiline: reconstruct a raw template with sentinels for interpolations, trim indentation on the template,
        // then split back into text/interpolation parts.
        const char Sentinel = '\u0001';

        var templateBuilder = new StringBuilder();
        var interpolations = new List<InterpolationSyntax>();

        foreach (var content in syntax.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    templateBuilder.Append(text.Token.ValueText ?? string.Empty);
                    break;

                case InterpolationSyntax interpolation:
                    interpolations.Add(interpolation);
                    templateBuilder.Append(Sentinel);
                    break;

                default:
                    _diagnostics.ReportInvalidExpressionTerm(content.ToString(), content.GetLocation());
                    break;
            }
        }

        var trimmedTemplate = TrimMultilineTemplate(templateBuilder.ToString());
        var parts = SplitBySentinel(trimmedTemplate, Sentinel);

        // Interleave text parts with bound interpolation expressions.
        var interpIndex = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var textPart = parts[i];
            if (!string.IsNullOrEmpty(textPart))
            {
                var textExpr = new BoundLiteralExpression(
                    BoundLiteralExpressionKind.StringLiteral,
                    textPart,
                    Compilation.GetSpecialType(SpecialType.System_String));

                result = ConcatOrFirst(result, textExpr);
            }

            if (interpIndex < interpolations.Count)
            {
                var interpolationExpr = BindExpression(interpolations[interpIndex].Expression);
                result = ConcatOrFirst(result, interpolationExpr);
                interpIndex++;
            }
        }

        return result ?? new BoundLiteralExpression(
            BoundLiteralExpressionKind.StringLiteral,
            string.Empty,
            Compilation.GetSpecialType(SpecialType.System_String));

        BoundExpression BindInvalidContent(InterpolatedStringContentSyntax content)
        {
            _diagnostics.ReportInvalidExpressionTerm(content.ToString(), content.GetLocation());
            return ErrorExpression(
                Compilation.GetSpecialType(SpecialType.System_String),
                reason: BoundExpressionReason.TypeMismatch);
        }

        BoundExpression? ConcatOrFirst(BoundExpression? left, BoundExpression right)
        {
            if (left is null)
            {
                if (right.Type?.SpecialType == SpecialType.System_String)
                    return right;

                var empty = new BoundLiteralExpression(
                    BoundLiteralExpressionKind.StringLiteral,
                    string.Empty,
                    Compilation.GetSpecialType(SpecialType.System_String));

                var firstConcat = ResolveStringConcatMethod(empty, right);
                return firstConcat is null
                    ? ErrorExpression(reason: BoundExpressionReason.OtherError)
                    : new BoundInvocationExpression(firstConcat, [empty, right]);
            }

            if (left is BoundErrorExpression)
                return left;

            if (right is BoundErrorExpression)
                return right;

            if (IsErrorOrNull(left) || IsErrorOrNull(right))
                return ErrorExpression(reason: BoundExpressionReason.OtherError);

            var concatMethod = ResolveStringConcatMethod(left, right);
            return concatMethod is null
                ? ErrorExpression(reason: BoundExpressionReason.OtherError)
                : new BoundInvocationExpression(concatMethod, [left, right]);
        }

        static List<string> SplitBySentinel(string s, char sentinel)
        {
            var list = new List<string>();
            var start = 0;

            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == sentinel)
                {
                    list.Add(i > start ? s.Substring(start, i - start) : string.Empty);
                    start = i + 1;
                }
            }

            list.Add(start < s.Length ? s.Substring(start) : string.Empty);
            return list;
        }

        static string TrimMultilineTemplate(string template)
        {
            if (string.IsNullOrEmpty(template))
                return string.Empty;

            // Normalize CRLF to LF for trimming logic.
            template = template.Replace("\r\n", "\n");

            // Remove the first newline if present (common triple-quoted form).
            if (template.StartsWith("\n", StringComparison.Ordinal))
                template = template.Substring(1);

            // Determine indentation from the final line (the whitespace before the closing delimiter).
            var lastNl = template.LastIndexOf('\n');
            if (lastNl < 0)
                return template;

            var suffix = template.Substring(lastNl + 1);
            if (!suffix.All(static ch => ch == ' ' || ch == '\t'))
                return template;

            var indent = suffix;

            // Drop the final newline + indent line entirely.
            template = template.Substring(0, lastNl);

            if (indent.Length == 0)
                return template;

            // Remove the indentation prefix from each line when present.
            var lines = template.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith(indent, StringComparison.Ordinal))
                    lines[i] = line.Substring(indent.Length);
            }

            return string.Join("\n", lines);
        }
    }

    private IMethodSymbol? ResolveToStringMethod(ITypeSymbol type)
    {
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);

        if (type is ITypeParameterSymbol)
        {
            var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
            return objectType.GetMethodRecursive("ToString", stringType, []);
        }

        var currentType = type;
        while (currentType != null)
        {
            var method = currentType.GetMethodRecursive("ToString", stringType, []);

            if (method != null)
                return method;

            currentType = currentType.BaseType;
        }
        return null;
    }

    private static bool IsErrorOrNull(BoundExpression expression)
    {
        return expression.Type is null || expression.Type.TypeKind == TypeKind.Error;
    }

    private static bool IsSameSyntaxNode(SyntaxNode left, SyntaxNode right)
    {
        return left.Kind == right.Kind &&
               left.Span == right.Span &&
               ReferenceEquals(left.SyntaxTree, right.SyntaxTree);
    }

    private bool TryGetLaterLocalDeclaration(IdentifierNameSyntax usage, string name, out VariableDeclaratorSyntax declaration)
    {
        declaration = null!;

        for (SyntaxNode? current = usage; current is not null; current = current.Parent)
        {
            if (current is VariableDeclaratorSyntax currentDeclarator &&
                currentDeclarator.Parent is VariableDeclarationSyntax variableDeclaration &&
                TryFindLaterDeclarator(variableDeclaration.Declarators, currentDeclarator, name, out declaration))
            {
                return true;
            }

            if (current is StatementSyntax currentStatement)
            {
                if (currentStatement.Parent is BlockStatementSyntax blockStatement &&
                    TryFindLaterDeclarator(blockStatement.Statements, currentStatement, name, out declaration))
                {
                    return true;
                }

                if (currentStatement.Parent is BlockSyntax blockExpression &&
                    TryFindLaterDeclarator(blockExpression.Statements, currentStatement, name, out declaration))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindLaterDeclarator(
        SeparatedSyntaxList<VariableDeclaratorSyntax> declarators,
        VariableDeclaratorSyntax currentDeclarator,
        string name,
        out VariableDeclaratorSyntax declaration)
    {
        declaration = null!;

        var currentIndex = -1;
        for (var i = 0; i < declarators.Count; i++)
        {
            if (IsSameSyntaxNode(declarators[i], currentDeclarator))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
            return false;

        for (var i = currentIndex + 1; i < declarators.Count; i++)
        {
            var candidate = declarators[i];
            if (candidate.Identifier.IsMissing || IsDiscardDeclarator(candidate))
                continue;

            if (string.Equals(candidate.Identifier.ValueText, name, StringComparison.Ordinal))
            {
                declaration = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindLaterDeclarator(
        SyntaxList<StatementSyntax> statements,
        StatementSyntax currentStatement,
        string name,
        out VariableDeclaratorSyntax declaration)
    {
        declaration = null!;

        var currentIndex = -1;
        for (var i = 0; i < statements.Count; i++)
        {
            if (IsSameSyntaxNode(statements[i], currentStatement))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
            return false;

        for (var i = currentIndex + 1; i < statements.Count; i++)
        {
            if (TryFindDeclaratorInStatement(statements[i], name, out declaration))
                return true;
        }

        return false;
    }

    private static bool TryFindDeclaratorInStatement(
        StatementSyntax statement,
        string name,
        out VariableDeclaratorSyntax declaration)
    {
        declaration = null!;

        VariableDeclarationSyntax? variableDeclaration = statement switch
        {
            LocalDeclarationStatementSyntax local => local.Declaration,
            UseDeclarationStatementSyntax use => use.Declaration,
            _ => null
        };

        if (variableDeclaration is null)
            return false;

        foreach (var declarator in variableDeclaration.Declarators)
        {
            if (declarator.Identifier.IsMissing || IsDiscardDeclarator(declarator))
                continue;

            if (string.Equals(declarator.Identifier.ValueText, name, StringComparison.Ordinal))
            {
                declaration = declarator;
                return true;
            }
        }

        return false;
    }

    private bool TryUpgradeErrorLocalFromDeclaration(ILocalSymbol local, out ILocalSymbol upgradedLocal)
    {
        foreach (var syntaxReference in local.DeclaringSyntaxReferences)
        {
            var root = syntaxReference.SyntaxTree.GetRoot();
            var declarator = root.FindNode(syntaxReference.Span, getInnermostNodeForTie: true)
                .AncestorsAndSelf()
                .OfType<VariableDeclaratorSyntax>()
                .FirstOrDefault();

            if (declarator is null)
            {
                var token = root.FindToken(syntaxReference.Span.Start);
                declarator = token.Parent?
                    .AncestorsAndSelf()
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault();
            }

            if (declarator is null)
                continue;

            if (!TryGetSyntaxReferenceKey(declarator, out var key) ||
                !_localDeclarationUpgradesInProgress.Add(key))
            {
                continue;
            }

            try
            {
                var boundDeclarator = BindLocalDeclaration(declarator);
                if (!boundDeclarator.Local.Type.ContainsErrorType())
                {
                    upgradedLocal = boundDeclarator.Local;
                    return true;
                }
            }
            finally
            {
                _localDeclarationUpgradesInProgress.Remove(key);
            }
        }

        upgradedLocal = null!;
        return false;
    }

    private bool TryGetTargetTypedEnumField(string name, SyntaxNode syntax, out IFieldSymbol field)
    {
        field = null!;

        var targetType = GetTargetType(syntax);
        if (targetType is null)
            return false;

        targetType = UnwrapTaskLikeTargetType(targetType);
        if (targetType is not INamedTypeSymbol { TypeKind: TypeKind.Enum } targetEnum)
            return false;

        field = LookupSymbols(name)
            .OfType<IFieldSymbol>()
            .FirstOrDefault(candidate =>
                (candidate.IsConst || candidate.ContainingType?.TypeKind == TypeKind.Enum) &&
                SymbolEqualityComparer.Default.Equals(candidate.ContainingType, targetEnum))!;

        return field is not null;
    }

    private BoundExpression BindIdentifierName(
        IdentifierNameSyntax syntax,
        bool allowEventAccess = false,
        bool applyFlowNarrowing = true)
    {
        var name = syntax.Identifier.ValueText;

        if (string.Equals(name, "field", StringComparison.Ordinal))
        {
            if (TryBindAutoPropertyFieldKeyword(out var fieldAccess))
                return fieldAccess;

            if (TryReportInvalidFieldKeywordUsage(syntax))
            {
                var error = ErrorExpression(reason: BoundExpressionReason.NotFound);
                CacheBoundNode(syntax, error);
                return error;
            }
        }

        if (TryGetLaterLocalDeclaration(syntax, name, out _) &&
            !IsIdentifierBoundByEnclosingLambdaParameter(syntax, name))
        {
            _diagnostics.ReportVariableUsedBeforeDeclaration(name, syntax.Identifier.GetLocation());
            var error = ErrorExpression(reason: BoundExpressionReason.NotFound);
            CacheBoundNode(syntax, error);
            return error;
        }

        var symbol = LookupSymbol(name);
        if (symbol is ILocalSymbol unresolvedLocal &&
            unresolvedLocal.Type.ContainsErrorType() &&
            TryUpgradeErrorLocalFromDeclaration(unresolvedLocal, out var upgradedLocal))
        {
            symbol = upgradedLocal;
        }

        if (TryGetTargetTypedEnumField(name, syntax, out var targetTypedEnumField))
            symbol = targetTypedEnumField;

        // Locals/parameters must win over implicit instance members.
        // Otherwise record ctor parameters like `Left`/`Right` get captured by `self.Left`/`self.Right`
        // and we emit `base(this.Left, this.Right)` instead of `base(Left, Right)`.
        if (symbol is ILocalSymbol localEarly)
        {
            if (localEarly is SourceFunctionValueSymbol functionValue)
            {
                var methods = ImmutableArray.Create(functionValue.TargetMethod);
                var receiver = BindImplicitMethodGroupReceiver(methods);
                if (receiver is BoundErrorExpression error)
                    return error;

                return BindMethodGroup(receiver, methods, syntax.Identifier.GetLocation());
            }

            var b = new BoundLocalAccess(localEarly);
            return applyFlowNarrowing ? ApplyIsNotNullNarrowing(b, localEarly) : b;
        }

        if (symbol is IParameterSymbol paramEarly)
        {
            var p = new BoundParameterAccess(paramEarly);
            return applyFlowNarrowing ? ApplyIsNotNullNarrowing(p, paramEarly) : p;
        }

        if (TryBindImplicitInstanceMember(name, syntax, allowEventAccess, out var memberExpr))
            return memberExpr;

        if (symbol is null)
        {
            var isInvocationCallee =
                syntax.Parent is InvocationExpressionSyntax inv &&
                ReferenceEquals(inv.Expression, syntax);

            var contextualTargetType = GetTargetType(syntax);
            if (contextualTargetType is not null && !isInvocationCallee)
            {
                var unwrappedTargetType = UnwrapTaskLikeTargetType(contextualTargetType);
                if (LookupUnionCaseTypeCandidates(name).Length > 0 &&
                    TryBindDiscriminatedUnionCase(unwrappedTargetType, name, syntax.Identifier.GetLocation()) is BoundExpression contextualUnionCase)
                    return contextualUnionCase;
            }

            var type = LookupType(name);
            if (type is not null)
            {
                if (IsUnimportedUnionCaseType(name, type))
                    type = null;
            }

            if (type is not null)
            {
                if (BindDiscriminatedUnionCaseType(type) is { } unionCaseFromLookup)
                {
                    if (!isInvocationCallee &&
                        unionCaseFromLookup is BoundTypeExpression { Type: INamedTypeSymbol caseType } &&
                        caseType.TryGetUnionCase() is not null)
                    {
                        var unitArgCtor = caseType.Constructors.FirstOrDefault(ctor =>
                            ctor.Parameters.Length == 1 &&
                            IsUnitType(ctor.Parameters[0].Type));

                        if (unitArgCtor is not null)
                        {
                            if (!EnsureMemberAccessible(unitArgCtor, syntax.Identifier.GetLocation(), "constructor"))
                                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
                            ReportObsoleteIfNeeded(unitArgCtor, syntax.Identifier.GetLocation());

                            var unitType = unitArgCtor.Parameters[0].Type;
                            var unitValue = new BoundUnitExpression(unitType);
                            return new BoundObjectCreationExpression(
                                unitArgCtor,
                                ImmutableArray.Create<BoundExpression>(unitValue));
                        }
                    }

                    return unionCaseFromLookup;
                }

                return new BoundTypeExpression(type);
            }

            var ns = LookupNamespace(name);
            if (ns is not null)
                return new BoundNamespaceExpression(ns);

            _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, syntax.Identifier.GetLocation());

            var n = ErrorExpression(reason: BoundExpressionReason.NotFound);
            CacheBoundNode(syntax, n);
            return n;
        }

        if (symbol is IMethodSymbol)
        {
            var methods = FilterStaleSignatureSkeletonMethods(LookupSymbols(name)
                .OfType<IMethodSymbol>()
                .ToImmutableArray());

            if (methods.IsDefaultOrEmpty)
                return ErrorExpression(reason: BoundExpressionReason.NotFound);

            var receiver = BindImplicitMethodGroupReceiver(methods);

            if (receiver is BoundErrorExpression error)
                return error;

            return BindMethodGroup(receiver, methods, syntax.Identifier.GetLocation());
        }

        switch (symbol)
        {
            case IAliasSymbol { UnderlyingSymbol: ITypeSymbol aliasType }:
                {
                    if (IsUnimportedUnionCaseType(name, aliasType))
                    {
                        _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, syntax.Identifier.GetLocation());
                        var n = ErrorExpression(reason: BoundExpressionReason.NotFound);
                        CacheBoundNode(syntax, n);
                        return n;
                    }

                    if (BindDiscriminatedUnionCaseType(aliasType) is { } unionCase)
                        return unionCase;

                    return new BoundTypeExpression(aliasType);
                }
            case INamespaceSymbol ns:
                return new BoundNamespaceExpression(ns);
            case ITypeSymbol type:
                {
                    if (IsUnimportedUnionCaseType(name, type))
                    {
                        _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, syntax.Identifier.GetLocation());
                        var n = ErrorExpression(reason: BoundExpressionReason.NotFound);
                        CacheBoundNode(syntax, n);
                        return n;
                    }

                    var isInvocationCallee =
                        syntax.Parent is InvocationExpressionSyntax inv &&
                        ReferenceEquals(inv.Expression, syntax);

                    var contextualTargetType = GetTargetType(syntax);
                    if (contextualTargetType is not null && !isInvocationCallee)
                    {
                        var unwrappedTargetType = UnwrapTaskLikeTargetType(contextualTargetType);
                        if (LookupUnionCaseTypeCandidates(name).Length > 0 &&
                            TryBindDiscriminatedUnionCase(unwrappedTargetType, name, syntax.Identifier.GetLocation()) is BoundExpression contextualUnionCase)
                            return contextualUnionCase;
                    }

                    if (BindDiscriminatedUnionCaseType(type) is { } unionCase)
                    {
                        if (!isInvocationCallee &&
                            unionCase is BoundTypeExpression { Type: INamedTypeSymbol caseType } &&
                            caseType.TryGetUnionCase() is not null)
                        {
                            var unitArgCtor = caseType.Constructors.FirstOrDefault(ctor =>
                                ctor.Parameters.Length == 1 &&
                                IsUnitType(ctor.Parameters[0].Type));

                            if (unitArgCtor is not null)
                            {
                                if (!EnsureMemberAccessible(unitArgCtor, syntax.Identifier.GetLocation(), "constructor"))
                                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);
                                ReportObsoleteIfNeeded(unitArgCtor, syntax.Identifier.GetLocation());

                                var unitType = unitArgCtor.Parameters[0].Type;
                                var unitValue = new BoundUnitExpression(unitType);
                                return new BoundObjectCreationExpression(
                                    unitArgCtor,
                                    ImmutableArray.Create<BoundExpression>(unitValue));
                            }
                        }

                        return unionCase;
                    }

                    return new BoundTypeExpression(type);
                }
            case IEventSymbol @event:
                if (!EnsureMemberAccessible(@event, syntax.Identifier.GetLocation(), GetSymbolKindForDiagnostic(@event)))
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                ReportObsoleteIfNeeded(@event, syntax.Identifier.GetLocation());
                return new BoundMemberAccessExpression(null, @event);
            case ILocalSymbol local:
                var b = new BoundLocalAccess(local);
                return applyFlowNarrowing ? ApplyIsNotNullNarrowing(b, local) : b;
            case IParameterSymbol param:
                var p = new BoundParameterAccess(param);
                return applyFlowNarrowing ? ApplyIsNotNullNarrowing(p, param) : p;
            case IFieldSymbol field:
                {
                    if (!EnsureMemberAccessible(field, syntax.Identifier.GetLocation(), GetSymbolKindForDiagnostic(field)))
                        return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                    ReportObsoleteIfNeeded(field, syntax.Identifier.GetLocation());
                    var f = new BoundFieldAccess(field);
                    return f;
                }
            case IPropertySymbol prop:
                {
                    if (!EnsureMemberAccessible(prop, syntax.Identifier.GetLocation(), GetSymbolKindForDiagnostic(prop)))
                        return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                    ReportObsoleteIfNeeded(prop, syntax.Identifier.GetLocation());

                    if (TryGetFieldOnlyPropertyBackingField(prop, out var fieldSymbol))
                    {
                        var f = new BoundFieldAccess(fieldSymbol);
                        return f;
                    }

                    var p2 = new BoundPropertyAccess(prop);
                    return p2;
                }
            default:
                {
                    var n = ErrorExpression(reason: BoundExpressionReason.NotFound);
                    CacheBoundNode(syntax, n);
                    return n;
                }
        }
    }

    private bool IsUnimportedUnionCaseType(string name, ITypeSymbol type)
        => type is INamedTypeSymbol namedType &&
           namedType.TryGetUnionCase() is not null &&
           LookupUnionCaseTypeCandidates(name).Length == 0;

    private bool TryBindImplicitInstanceMember(string name, IdentifierNameSyntax syntax, bool allowEventAccess, out BoundExpression expr)
    {
        expr = null!;

        if (_containingSymbol is not IMethodSymbol m || m.IsStatic)
            return false;

        var type = m.ContainingType;
        if (type is null || type.TypeKind == TypeKind.Error)
            return false;

        var containingType = type;

        // Walk the base-type chain so unqualified identifiers can bind to inherited members.
        // Search most-derived first so derived members win.
        for (INamedTypeSymbol? current = containingType; current is not null; current = current.BaseType)
        {
            var members = current.GetMembers(name);

            var field = members.OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic);
            if (field is not null && IsSymbolAccessible(field))
            {
                expr = new BoundFieldAccess(new BoundSelfExpression(containingType), field);
                return true;
            }

            var property = members.OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic);
            if (property is not null && IsSymbolAccessible(property))
            {
                expr = new BoundMemberAccessExpression(new BoundSelfExpression(containingType), property);
                return true;
            }

            if (allowEventAccess)
            {
                var @event = members.OfType<IEventSymbol>().FirstOrDefault(e => !e.IsStatic);
                if (@event is not null && IsSymbolAccessible(@event))
                {
                    expr = new BoundMemberAccessExpression(new BoundSelfExpression(containingType), @event);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsIdentifierBoundByEnclosingLambdaParameter(IdentifierNameSyntax syntax, string name)
    {
        for (var current = syntax.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case SimpleFunctionExpressionSyntax simpleLambda:
                    if (string.Equals(simpleLambda.Parameter.Identifier.ValueText, name, StringComparison.Ordinal))
                        return true;
                    break;

                case ParenthesizedFunctionExpressionSyntax parenthesizedLambda:
                    foreach (var parameter in parenthesizedLambda.ParameterList.Parameters)
                    {
                        if (string.Equals(parameter.Identifier.ValueText, name, StringComparison.Ordinal))
                            return true;
                    }
                    break;
            }
        }

        return false;
    }

    private bool TryBindAutoPropertyFieldKeyword(out BoundFieldAccess fieldAccess)
    {
        fieldAccess = null!;

        if (_containingSymbol is not IMethodSymbol
            {
                MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly,
                ContainingSymbol: SourcePropertySymbol propertySymbol
            })
        {
            return false;
        }

        if (propertySymbol.IsDeclaredInExtension || propertySymbol.BackingField is null)
            return false;

        fieldAccess = new BoundFieldAccess(propertySymbol.BackingField);
        return true;
    }

    private bool TryReportInvalidFieldKeywordUsage(IdentifierNameSyntax syntax)
    {
        if (_containingSymbol is IMethodSymbol
            {
                MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly,
                ContainingSymbol: SourcePropertySymbol propertySymbol
            })
        {
            if (propertySymbol.BackingField is null)
            {
                _diagnostics.ReportFieldKeywordRequiresPropertyBackingStorage(
                    propertySymbol.Name,
                    syntax.Identifier.GetLocation());
                return true;
            }

            return false;
        }

        if (_containingSymbol is IMethodSymbol
            {
                MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly,
                ContainingSymbol: IPropertySymbol extensionProperty
            } &&
            extensionProperty.IsExtensionProperty)
        {
            _diagnostics.ReportFieldKeywordRequiresPropertyBackingStorage(
                extensionProperty.Name,
                syntax.Identifier.GetLocation());
            return true;
        }

        _diagnostics.ReportFieldKeywordOnlyValidInPropertyAccessor(syntax.Identifier.GetLocation());
        return true;
    }

    private bool IsExtensionPropertyAccessor()
    {
        return _containingSymbol is IMethodSymbol
        {
            MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.InitOnly,
            ContainingSymbol: IPropertySymbol { IsExtensionProperty: true }
        };
    }

    private BoundMethodGroupExpression CreateMethodGroup(BoundExpression? receiver, ImmutableArray<IMethodSymbol> methods)
    {
        Func<ITypeSymbol?>? delegateFactory = null;

        if (!methods.IsDefaultOrEmpty && methods.Length == 1)
        {
            var method = methods[0];
            delegateFactory = () => Compilation.GetMethodReferenceDelegate(method);
        }

        return BoundFactory.MethodGroupExpression(receiver, methods, delegateFactory);
    }

    private BoundExpression? BindImplicitMethodGroupReceiver(ImmutableArray<IMethodSymbol> methods)
    {
        if (!methods.Any(static method => RequiresImplicitInstanceReceiver(method)))
            return null;

        if (_containingSymbol is IMethodSymbol method && !method.IsStatic)
        {
            var containingType = method.ContainingType ?? Compilation.ErrorTypeSymbol;
            return new BoundSelfExpression(containingType);
        }

        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private static bool RequiresImplicitInstanceReceiver(IMethodSymbol method)
    {
        if (method.IsStatic)
            return false;

        // Only type members use implicit `self`/`this` receivers.
        // Local functions and lambdas are instance-like during lowering, but they do
        // not require an implicit receiver at call sites.
        return method.ContainingSymbol is INamedTypeSymbol;
    }

    private BoundExpression BindBinaryExpression(InfixOperatorExpressionSyntax syntax)
    {
        var expressionTargetType = GetScopedTargetType(syntax);
        var left = BindBinaryOperand(syntax.Left, expressionTargetType);
        var opKind = syntax.OperatorToken.Kind;

        if (opKind == SyntaxKind.PipeToken)
            return BindPipeExpression(left, syntax);

        var right = BindBinaryOperand(syntax.Right, expressionTargetType);

        return BindBinaryExpression(
            opKind,
            left,
            right,
            syntax.OperatorToken.GetLocation(),
            syntax.Left,
            syntax.Right,
            syntax);
    }

    private BoundExpression BindBinaryOperand(ExpressionSyntax operand, ITypeSymbol? expressionTargetType)
        => expressionTargetType is not null && IsTargetTypedMemberBinding(operand)
            ? BindExpressionWithTargetType(operand, expressionTargetType)
            : BindExpression(operand);

    private BoundExpression BindBinaryExpression(
        SyntaxKind opKind,
        BoundExpression left,
        BoundExpression right,
        Location? diagnosticLocation = null,
        ExpressionSyntax? leftSyntax = null,
        ExpressionSyntax? rightSyntax = null,
        SyntaxNode? callSyntax = null)
    {
        if (HasExpressionErrors(left) || HasExpressionErrors(right))
            return new BoundErrorExpression(Compilation.ErrorTypeSymbol, null, BoundExpressionReason.ArgumentBindingFailed);

        if (RequiresUnsafePointerArithmetic(opKind, left.Type, right.Type) && !IsUnsafeEnabled)
        {
            _diagnostics.ReportPointerOperationRequiresUnsafe(diagnosticLocation ?? Location.None);
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        // 1. Specialfall: string + any → string-konkatenering
        if (opKind == SyntaxKind.PlusToken)
        {
            if (TryFoldEncodedByteArrayConcatenation(left, right, out var concatenatedByteArray))
                return concatenatedByteArray;

            var leftIsString = left.Type.UnwrapLiteralType().SpecialType == SpecialType.System_String;
            var rightIsString = right.Type.UnwrapLiteralType().SpecialType == SpecialType.System_String;

            if (left is BoundLiteralExpression litLeft &&
                right is BoundLiteralExpression litRight &&
                (leftIsString || rightIsString))
            {
                var stringType = Compilation.GetSpecialType(SpecialType.System_String);
                var value = (litLeft.Value?.ToString() ?? string.Empty) +
                            (litRight.Value?.ToString() ?? string.Empty);
                return new BoundLiteralExpression(BoundLiteralExpressionKind.StringLiteral, value, stringType);
            }

            if (leftIsString || rightIsString)
            {
                if (IsErrorOrNull(left) || IsErrorOrNull(right))
                    return ErrorExpression(reason: BoundExpressionReason.OtherError);

                var concatMethod = ResolveStringConcatMethod(left, right);
                if (concatMethod is not null)
                    return new BoundInvocationExpression(concatMethod, [left, right]);
            }
        }

        // Operators on concrete primitive types are predefined language operators.
        // Resolve them before user-defined overload lookup so static abstract members
        // inherited from generic-math interfaces cannot become concrete call targets.
        var leftScalarType = left.Type.UnwrapLiteralType() ?? left.Type;
        var rightScalarType = right.Type.UnwrapLiteralType() ?? right.Type;
        if (leftScalarType.SpecialType != SpecialType.None &&
            rightScalarType.SpecialType != SpecialType.None &&
            BoundBinaryOperator.TryLookup(Compilation, opKind, left.Type, right.Type, out var predefinedOperator))
        {
            return new BoundBinaryExpression(left, predefinedOperator, right);
        }

        // 2. Överlagrade operatorer
        var userDefinedExpression = BindUserDefinedBinaryOperator(opKind, left, right, diagnosticLocation, leftSyntax, rightSyntax, callSyntax);
        if (userDefinedExpression is not null)
            return userDefinedExpression;

        // 3. Inbyggda operatorer
        if (BoundBinaryOperator.TryLookup(Compilation, opKind, left.Type, right.Type, out var op))
        {
            return new BoundBinaryExpression(left, op, right);
        }

        // Metadata identity drift can produce distinct enum symbols for the same CLR enum type.
        // Keep common enum flag operations bindable when names/namespace align.
        if (BoundBinaryOperator.TryCreateEnumLikeOperator(Compilation, opKind, left.Type, right.Type, out var enumOp))
            return new BoundBinaryExpression(left, enumOp, right);

        // 4. Fel
        var operatorText = SyntaxFacts.GetSyntaxTokenText(opKind) ?? opKind.ToString();
        _diagnostics.ReportOperatorCannotBeAppliedToOperandsOfTypes(
            operatorText,
            left.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            right.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
            diagnosticLocation ?? Location.None);

        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private static bool TryFoldEncodedByteArrayConcatenation(
        BoundExpression left,
        BoundExpression right,
        out BoundExpression result)
    {
        result = null!;

        if (left is not BoundCollectionExpression leftCollection ||
            right is not BoundCollectionExpression rightCollection)
            return false;

        if (!IsByteArrayType(leftCollection.Type) ||
            !IsByteArrayType(rightCollection.Type))
            return false;

        if (leftCollection.Elements.Any(static e => e is BoundSpreadElement) ||
            rightCollection.Elements.Any(static e => e is BoundSpreadElement))
            return false;

        var concatenated = leftCollection.Elements.Concat(rightCollection.Elements).ToImmutableArray();
        result = new BoundCollectionExpression(leftCollection.Type, concatenated);
        return true;
    }

    private static bool IsByteArrayType(ITypeSymbol? type)
    {
        return type is IArrayTypeSymbol { Rank: 1 } arrayType &&
               arrayType.ElementType.UnwrapLiteralType().SpecialType == SpecialType.System_Byte;
    }

    private static bool RequiresUnsafePointerArithmetic(SyntaxKind opKind, ITypeSymbol? leftType, ITypeSymbol? rightType)
    {
        if (opKind is not (SyntaxKind.PlusToken or SyntaxKind.MinusToken))
            return false;

        return leftType is IPointerTypeSymbol || rightType is IPointerTypeSymbol;
    }

    private BoundExpression BindPipeExpression(BoundExpression left, InfixOperatorExpressionSyntax syntax)
    {
        if (left is BoundErrorExpression)
            return left;

        if (syntax.Right is InvocationExpressionSyntax invocation)
        {
            var target = BindPipelineInvocationTargetExpression(invocation.Expression, left);

            if (target is BoundErrorExpression error)
                return error;

            if (target is BoundMethodGroupExpression methodGroup)
            {
                var extensionCandidates = FilterInvocationCandidatesForArgumentBinding(
                    methodGroup.Methods.Where(static method => method.IsExtensionMethod).ToImmutableArray(),
                    invocation.ArgumentList.Arguments);

                if (!extensionCandidates.IsDefaultOrEmpty)
                {
                    var extensionCandidatesForBinding = extensionCandidates;
                    for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
                    {
                        var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
                        extensionCandidatesForBinding = FilterMethodsForLambda(
                            extensionCandidatesForBinding,
                            argumentIndex,
                            argumentExpression,
                            extensionReceiverImplicit: true,
                            callSiteArgumentCount: invocation.ArgumentList.Arguments.Count);

                        RecordLambdaTargets(
                            argumentExpression,
                            extensionCandidatesForBinding,
                            argumentIndex,
                            extensionReceiverImplicit: true,
                            receiverType: left.Type);
                    }

                    var boundExtensionArguments = BindInvocationArgumentsWithCandidateTargetTypes(
                        extensionCandidatesForBinding,
                        invocation.ArgumentList.Arguments,
                        out var hasExtensionErrors,
                        left);

                    if (hasExtensionErrors)
                    {
                        boundExtensionArguments = BindInvocationArguments(invocation.ArgumentList.Arguments, out hasExtensionErrors);
                    }

                    if (!hasExtensionErrors)
                        return BindPipelineInvocationOnMethodGroup(methodGroup, invocation, syntax.Left, left, boundExtensionArguments);
                }

                // For non-extension pipe methods the pipe inserts 'left' as the implicit first
                // argument (parameter index 0), so each explicit argument at index i maps to
                // parameter index i+1.  Use BindInvocationArgumentsWithCandidateTargetTypes with
                // pipeReceiverType so that (a) the correct +1 offset is applied and (b) delegate
                // target types are pushed via PushTargetType/GetTargetType — bypassing the
                // reference-equality _lambdaDelegateTargets dictionary that would otherwise fail
                // because SyntaxNodeCache is currently disabled (each access to arguments[i] creates
                // a new red-node instance).
                var nonExtensionCandidates = methodGroup.Methods
                    .Where(static m => !m.IsExtensionMethod)
                    .ToImmutableArray();

                BoundArgument[] boundArguments;
                bool hasErrors;

                if (!nonExtensionCandidates.IsDefaultOrEmpty)
                {
                    var nonExtensionCandidatesForBinding = nonExtensionCandidates;
                    for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
                    {
                        var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
                        nonExtensionCandidatesForBinding = FilterMethodsForLambda(
                            nonExtensionCandidatesForBinding,
                            argumentIndex + 1,
                            argumentExpression,
                            extensionReceiverImplicit: false,
                            callSiteArgumentCount: invocation.ArgumentList.Arguments.Count + 1);
                    }

                    boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(
                        nonExtensionCandidatesForBinding,
                        invocation.ArgumentList.Arguments,
                        out hasErrors,
                        pipeReceiverType: left.Type);
                }
                else
                {
                    boundArguments = BindInvocationArguments(invocation.ArgumentList.Arguments, out hasErrors);
                }

                if (hasErrors)
                    return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);

                return BindPipelineInvocationOnMethodGroup(methodGroup, invocation, syntax.Left, left, boundArguments);
            }

            if (target is BoundMemberAccessExpression { Member: IMethodSymbol method } memberExpr)
            {
                BoundArgument[] boundArguments;
                bool hasErrors;

                if (method.IsExtensionMethod)
                {
                    for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
                    {
                        var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
                        RecordLambdaTargets(
                            argumentExpression,
                            ImmutableArray.Create(method),
                            argumentIndex,
                            extensionReceiverImplicit: true,
                            receiverType: left.Type);
                    }

                    boundArguments = BindInvocationArguments(invocation.ArgumentList.Arguments, method, left, out hasErrors);
                }
                else
                {
                    // Non-extension pipe: method's parameter[0] is the implicit pipe source.
                    // Use pipeReceiverType so the +1 offset and generic inference are applied.
                    boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(
                        ImmutableArray.Create(method),
                        invocation.ArgumentList.Arguments,
                        out hasErrors,
                        pipeReceiverType: left.Type);
                }

                if (hasErrors)
                    return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);

                return BindPipelineInvocationOnBoundMethod(memberExpr, invocation, syntax.Left, left, boundArguments);
            }

            var delegateArguments = BindInvocationArguments(invocation.ArgumentList.Arguments, out var delegateArgumentErrors);
            if (delegateArgumentErrors)
                return ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);

            if (BindPipelineInvocationOnDelegate(target, invocation, syntax.Left, left, delegateArguments) is { } delegateInvocation)
                return delegateInvocation;

            _diagnostics.ReportInvalidInvocation(invocation.Expression.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        RecordPipelineLambdaDelegateTarget(syntax.Right, left);
        var propertyTarget = BindPipelineInvocationTargetExpression(syntax.Right, left);

        if (propertyTarget is BoundErrorExpression propertyError)
            return propertyError;

        if (TryBindPipelineImplicitInvocation(propertyTarget, syntax.Left, left, syntax.Right, out var implicitInvocation))
            return implicitInvocation;

        if (propertyTarget is BoundMemberAccessExpression { Member: IPropertySymbol } or BoundPropertyAccess)
            return BindPipelinePropertyAssignment(propertyTarget, syntax.Left, left, syntax.Right);

        _diagnostics.ReportPipeRequiresInvocation(syntax.OperatorToken.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private void RecordPipelineLambdaDelegateTarget(ExpressionSyntax expression, BoundExpression pipelineValue)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expression is not FunctionExpressionSyntax lambda)
            return;

        var pipelineType = pipelineValue.Type?.UnwrapLiteralType() ?? pipelineValue.Type;
        if (pipelineType is null || pipelineType.TypeKind == TypeKind.Error)
            return;

        var parameters = lambda switch
        {
            SimpleFunctionExpressionSyntax simple => new[] { simple.Parameter },
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToArray(),
            _ => Array.Empty<ParameterSyntax>(),
        };

        if (parameters.Length == 0 || parameters[0].TypeAnnotation?.Type is not null)
            return;

        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var candidateDelegate = Compilation.CreateFunctionTypeSymbol([pipelineType], unitType);
        if (candidateDelegate is INamedTypeSymbol namedDelegate)
            _lambdaDelegateTargets[lambda] = ImmutableArray.Create(namedDelegate);
    }

    private BoundExpression BindPipelineInvocationTargetExpression(ExpressionSyntax expression, BoundExpression pipelineValue)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                {
                    var methodName = identifier.Identifier.ValueText;
                    var hasRegularSymbols = LookupSymbol(methodName) is not null || LookupSymbols(methodName).Any();
                    var hasExtensionCandidates = TryBindPipelineExtensionMethodGroup(methodName, pipelineValue, identifier.Identifier.GetLocation(), out var extensionGroup);

                    if (!hasRegularSymbols)
                        return hasExtensionCandidates
                            ? extensionGroup!
                            : BindIdentifierName(identifier);

                    var regularTarget = BindIdentifierName(identifier);
                    if (regularTarget is BoundErrorExpression && hasExtensionCandidates)
                        return extensionGroup!;

                    if (regularTarget is BoundMethodGroupExpression regularMethodGroup && hasExtensionCandidates)
                        return MergePipelineMethodGroupCandidates(regularMethodGroup, extensionGroup!.Methods);

                    return regularTarget;
                }
            case GenericNameSyntax generic:
                {
                    var methodName = generic.Identifier.ValueText;
                    var hasRegularSymbols = LookupSymbols(methodName).Any();
                    var hasExtensionCandidates = TryBindPipelineExtensionMethodGroup(methodName, pipelineValue, generic.Identifier.GetLocation(), out var extensionGroup);

                    if (!hasRegularSymbols && hasExtensionCandidates)
                        return extensionGroup!;

                    var regularTarget = BindGenericInvocationTarget(generic);
                    if (regularTarget is BoundErrorExpression && hasExtensionCandidates)
                        return extensionGroup!;

                    if (regularTarget is BoundMethodGroupExpression regularMethodGroup && hasExtensionCandidates)
                    {
                        return MergePipelineMethodGroupCandidates(regularMethodGroup, extensionGroup!.Methods);
                    }

                    return regularTarget;
                }
            default:
                return BindPipelineTargetExpression(expression);
        }
    }

    private BoundExpression BindPipelineTargetExpression(ExpressionSyntax expression)
        => expression switch
        {
            MemberAccessExpressionSyntax memberAccess => BindMemberAccessExpression(memberAccess),
            MemberBindingExpressionSyntax memberBinding => BindMemberBindingExpression(memberBinding),
            GenericNameSyntax generic => BindGenericInvocationTarget(generic),
            IdentifierNameSyntax identifier => BindIdentifierName(identifier),
            _ => BindExpression(expression),
        };

    private bool TryBindPipelineExtensionMethodGroup(
        string methodName,
        BoundExpression pipelineValue,
        Location location,
        out BoundMethodGroupExpression? methodGroup)
    {
        methodGroup = null;

        if (!IsExtensionReceiver(pipelineValue))
            return false;

        var receiverType = pipelineValue.Type.UnwrapLiteralType() ?? pipelineValue.Type;
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return false;

        var extensionCandidates = LookupExtensionMethods(methodName, receiverType).ToImmutableArray();
        if (extensionCandidates.IsDefaultOrEmpty)
            extensionCandidates = LookupExtensionMethods(methodName, receiverType, includePartialMatches: true).ToImmutableArray();

        if (extensionCandidates.IsDefaultOrEmpty)
            return false;

        var boundGroup = BindMethodGroup(null, extensionCandidates, location);
        methodGroup = boundGroup as BoundMethodGroupExpression;
        return methodGroup is not null;
    }

    private BoundMethodGroupExpression MergePipelineMethodGroupCandidates(
        BoundMethodGroupExpression regularMethodGroup,
        ImmutableArray<IMethodSymbol> extensionMethods)
    {
        if (extensionMethods.IsDefaultOrEmpty)
            return regularMethodGroup;

        var merged = ImmutableArray.CreateBuilder<IMethodSymbol>(regularMethodGroup.Methods.Length + extensionMethods.Length);
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var method in regularMethodGroup.Methods)
        {
            if (seen.Add(method))
                merged.Add(method);
        }

        foreach (var method in extensionMethods)
        {
            if (seen.Add(method))
                merged.Add(method);
        }

        return CreateMethodGroup(regularMethodGroup.Receiver, merged.ToImmutable());
    }

    private BoundExpression BindPipelinePropertyAssignment(
        BoundExpression target,
        ExpressionSyntax pipelineSyntax,
        BoundExpression pipelineValue,
        ExpressionSyntax propertySyntax)
    {
        IPropertySymbol? propertySymbol = target switch
        {
            BoundMemberAccessExpression { Member: IPropertySymbol property } => property,
            BoundPropertyAccess propertyAccess => propertyAccess.Property,
            _ => null,
        };

        if (propertySymbol is null)
        {
            _diagnostics.ReportPipeRequiresInvocation(propertySyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        SourceFieldSymbol? backingField = null;
        var useFieldOnlyLowering = TryGetFieldOnlyPropertyBackingField(propertySymbol, out backingField);

        if (!useFieldOnlyLowering &&
            !propertySymbol.IsMutable &&
            !TryGetWritableAutoPropertyBackingField(propertySymbol, target, out backingField))
        {
            _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(propertySymbol.Name, propertySyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (pipelineValue.Type is { } pipelineType &&
            propertySymbol.Type.TypeKind != TypeKind.Error &&
            ShouldAttemptConversion(pipelineValue))
        {
            if (!IsAssignable(propertySymbol.Type, pipelineType, out var conversion))
            {
                ReportCannotAssignFromTypeToType(pipelineType, propertySymbol.Type, pipelineSyntax.GetLocation());
                return new BoundErrorExpression(propertySymbol.Type, null, BoundExpressionReason.TypeMismatch);
            }

            pipelineValue = ApplyConversion(pipelineValue, propertySymbol.Type, conversion, pipelineSyntax);
        }

        var receiver = GetReceiver(target);

        if (backingField is not null)
        {
            if (!CanAssignToField(backingField, receiver, pipelineSyntax))
                return new BoundErrorExpression(backingField.Type, backingField, BoundExpressionReason.NotFound);

            return CreateFieldAssignmentExpression(receiver, backingField, pipelineValue);
        }

        return BoundFactory.CreatePropertyAssignmentExpression(receiver, propertySymbol, pipelineValue);
    }

    private BoundExpression BindGenericInvocationTarget(GenericNameSyntax generic)
    {
        var boundTypeArguments = TryBindTypeArguments(generic);
        if (boundTypeArguments is null)
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        var symbolCandidates = LookupSymbols(generic.Identifier.ValueText)
            .OfType<IMethodSymbol>()
            .ToImmutableArray();

        if (!symbolCandidates.IsDefaultOrEmpty)
        {
            var instantiated = InstantiateMethodCandidates(symbolCandidates, boundTypeArguments.Value, generic, generic.GetLocation());
            if (!instantiated.IsDefaultOrEmpty)
                return CreateMethodGroup(null, instantiated);
        }

        return BindTypeSyntaxAsExpression(generic);
    }

    private BoundExpression BindPipelineInvocationOnMethodGroup(
        BoundMethodGroupExpression methodGroup,
        SyntaxNode callSyntax,
        ExpressionSyntax pipelineSyntax,
        BoundExpression pipelineValue,
        BoundArgument[] boundArguments)
    {
        var methodName = methodGroup.Methods[0].Name;
        var extensionCandidates = methodGroup.Methods
            .Where(static m => m.IsExtensionMethod)
            .ToImmutableArray();
        var staticCandidates = methodGroup.Methods
            .Where(static m => m.IsStatic && !m.IsExtensionMethod)
            .ToImmutableArray();

        if (!extensionCandidates.IsDefaultOrEmpty && IsExtensionReceiver(pipelineValue))
        {
            var resolution = OverloadResolver.ResolveOverload(extensionCandidates, boundArguments, Compilation, binder: this, receiver: pipelineValue, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
            if (resolution.Success)
            {
                var method = resolution.Method!;
                ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                var converted = ConvertInvocationArguments(
                    method,
                    boundArguments,
                    pipelineValue,
                    pipelineSyntax,
                    callSyntax,
                    out var convertedExtensionReceiver);
                return new BoundInvocationExpression(method, converted, methodGroup.Receiver, convertedExtensionReceiver);
            }

            if (resolution.IsAmbiguous)
            {
                _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                return ErrorExpression(
                    reason: BoundExpressionReason.Ambiguous,
                    candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
            }
        }

        if (!staticCandidates.IsDefaultOrEmpty)
        {
            var totalArguments = new BoundArgument[boundArguments.Length + 1];
            totalArguments[0] = new BoundArgument(pipelineValue, RefKind.None, name: null, pipelineSyntax);
            Array.Copy(boundArguments, 0, totalArguments, 1, boundArguments.Length);

            var resolution = OverloadResolver.ResolveOverload(staticCandidates, totalArguments, Compilation, binder: this, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
            if (resolution.Success)
            {
                var method = resolution.Method!;
                ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                var convertedArguments = ConvertPipelineStaticInvocationArguments(method, pipelineValue, pipelineSyntax, boundArguments, callSyntax);
                return new BoundInvocationExpression(method, convertedArguments, methodGroup.Receiver);
            }

            if (resolution.IsAmbiguous)
            {
                _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                return ErrorExpression(
                    reason: BoundExpressionReason.Ambiguous,
                    candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
            }

            if (ReportTypeArgumentConstraintFailureIfPresent(resolution, callSyntax.GetLocation()))
                return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);

            ReportSuppressedLambdaDiagnostics(totalArguments);
            _diagnostics.ReportNoOverloadForMethod("method", methodName, totalArguments.Length, callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
        }

        ReportSuppressedLambdaDiagnostics(PrependPipelineArgument(pipelineValue, pipelineSyntax, boundArguments));
        _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length + 1, callSyntax.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
    }

    private BoundExpression BindPipelineInvocationOnBoundMethod(
        BoundMemberAccessExpression memberExpr,
        SyntaxNode callSyntax,
        ExpressionSyntax pipelineSyntax,
        BoundExpression pipelineValue,
        BoundArgument[] boundArguments)
    {
        if (memberExpr.Member is not IMethodSymbol method)
        {
            _diagnostics.ReportInvalidInvocation(callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (method.IsExtensionMethod && IsExtensionReceiver(pipelineValue))
        {
            ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
            var converted = ConvertInvocationArguments(
                method,
                boundArguments,
                pipelineValue,
                pipelineSyntax,
                callSyntax,
                out var convertedExtensionReceiver);
            return new BoundInvocationExpression(method, converted, memberExpr.Receiver, convertedExtensionReceiver);
        }

        if (!method.IsStatic)
        {
            _diagnostics.ReportInvalidInvocation(callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        var totalCount = boundArguments.Length + 1;
        if (!SupportsArgumentCount(method.Parameters, totalCount))
        {
            ReportSuppressedLambdaDiagnostics(PrependPipelineArgument(pipelineValue, pipelineSyntax, boundArguments));
            _diagnostics.ReportNoOverloadForMethod("method", method.Name, totalCount, callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
        }

        var convertedArguments = ConvertPipelineStaticInvocationArguments(method, pipelineValue, pipelineSyntax, boundArguments, callSyntax);
        ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
        return new BoundInvocationExpression(method, convertedArguments, memberExpr.Receiver);
    }

    private BoundExpression? BindPipelineInvocationOnDelegate(
        BoundExpression target,
        SyntaxNode callSyntax,
        ExpressionSyntax pipelineSyntax,
        BoundExpression pipelineValue,
        BoundArgument[] boundArguments)
    {
        var rawType = target.Type;
        var targetType = rawType.UnwrapLiteralType() ?? rawType;

        if (targetType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
            return null;

        var invokeMethod = delegateType.GetDelegateInvokeMethod();
        if (invokeMethod is null)
        {
            _diagnostics.ReportInvalidInvocation(callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (!EnsureMemberAccessible(invokeMethod, callSyntax.GetLocation(), GetSymbolKindForDiagnostic(invokeMethod)))
            return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

        var totalCount = boundArguments.Length + 1;
        if (!SupportsArgumentCount(invokeMethod.Parameters, totalCount))
        {
            ReportSuppressedLambdaDiagnostics(PrependPipelineArgument(pipelineValue, pipelineSyntax, boundArguments));
            _diagnostics.ReportNoOverloadForMethod("method", invokeMethod.Name, totalCount, callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
        }

        var convertedArguments = ConvertPipelineStaticInvocationArguments(invokeMethod, pipelineValue, pipelineSyntax, boundArguments, callSyntax);
        return new BoundInvocationExpression(invokeMethod, convertedArguments, target);
    }

    private BoundExpression[] ConvertPipelineStaticInvocationArguments(
        IMethodSymbol method,
        BoundExpression pipelineValue,
        ExpressionSyntax pipelineSyntax,
        BoundArgument[] remainingArguments,
        SyntaxNode callSyntax)
    {
        var parameters = method.Parameters;
        var converted = new BoundExpression[parameters.Length];

        if (parameters.Length == 0)
            return converted;

        converted[0] = ConvertSingleArgument(pipelineValue, parameters[0], pipelineSyntax);

        if (parameters.Length == 1)
            return converted;

        var rest = ConvertArguments(parameters.RemoveAt(0), remainingArguments, callSyntax);
        Array.Copy(rest, 0, converted, 1, rest.Length);
        for (int i = 1 + rest.Length; i < converted.Length; i++)
            converted[i] = CreateOptionalArgument(parameters[i], callSyntax);

        return converted;
    }

    private bool TryBindPipelineImplicitInvocation(
        BoundExpression target,
        ExpressionSyntax pipelineSyntax,
        BoundExpression pipelineValue,
        ExpressionSyntax targetSyntax,
        out BoundExpression result)
    {
        var emptyArguments = Array.Empty<BoundArgument>();

        if (target is BoundMethodGroupExpression methodGroup)
        {
            result = BindPipelineInvocationOnMethodGroup(methodGroup, targetSyntax, pipelineSyntax, pipelineValue, emptyArguments);
            return true;
        }

        if (target is BoundMemberAccessExpression { Member: IMethodSymbol } memberExpr)
        {
            result = BindPipelineInvocationOnBoundMethod(memberExpr, targetSyntax, pipelineSyntax, pipelineValue, emptyArguments);
            return true;
        }

        var delegateInvocation = BindPipelineInvocationOnDelegate(target, targetSyntax, pipelineSyntax, pipelineValue, emptyArguments);
        if (delegateInvocation is not null)
        {
            result = delegateInvocation;
            return true;
        }

        result = null!;
        return false;
    }

    private static IEnumerable<BoundArgument> PrependPipelineArgument(
        BoundExpression pipelineValue,
        ExpressionSyntax pipelineSyntax,
        IEnumerable<BoundArgument> remainingArguments)
    {
        yield return new BoundArgument(pipelineValue, RefKind.None, null, pipelineSyntax);

        foreach (var argument in remainingArguments)
            yield return argument;
    }

    private IMethodSymbol? ResolveStringConcatMethod(BoundExpression left, BoundExpression right)
    {
        var stringType = Compilation.GetSpecialType(SpecialType.System_String);
        var candidates = stringType.GetMembers("Concat").OfType<IMethodSymbol>();

        var resolution = OverloadResolver.ResolveOverload(
            candidates.ToArray(),
            new[]
            {
                new BoundArgument(left, RefKind.None, null),
                new BoundArgument(right, RefKind.None, null)
            },
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible);

        if (resolution.Success && resolution.Method is not null)
            return resolution.Method;

        IMethodSymbol? fallback = null;
        var bestScore = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Length != 2)
                continue;

            if (left.Type is null || right.Type is null)
                continue;

            var leftConversion = Compilation.ClassifyConversion(left.Type, candidate.Parameters[0].Type);
            var rightConversion = Compilation.ClassifyConversion(right.Type, candidate.Parameters[1].Type);

            if (!leftConversion.IsImplicit || !rightConversion.IsImplicit)
                continue;

            var score = GetConversionScore(leftConversion) + GetConversionScore(rightConversion);

            if (score >= bestScore)
                continue;

            fallback = candidate;
            bestScore = score;
        }

        return fallback;

        static int GetConversionScore(Conversion conversion)
        {
            if (!conversion.Exists)
                return int.MaxValue;

            if (conversion.IsIdentity)
                return 0;

            if (conversion.IsReference)
                return 1;

            if (conversion.IsBoxing)
                return 2;

            if (conversion.IsNumeric)
                return 3;

            if (conversion.IsUserDefined)
                return 4;

            if (conversion.IsUnboxing)
                return 5;

            return 10;
        }
    }

    private BoundExpression? BindUserDefinedBinaryOperator(
        SyntaxKind opKind,
        BoundExpression left,
        BoundExpression right,
        Location? diagnosticLocation,
        ExpressionSyntax? leftSyntax,
        ExpressionSyntax? rightSyntax,
        SyntaxNode? callSyntax)
    {
        if (!OperatorFacts.TryGetUserDefinedOperatorInfo(opKind, 2, out var operatorInfo))
            return null;

        var leftType = left.Type ?? Compilation.ErrorTypeSymbol;
        var rightType = right.Type ?? Compilation.ErrorTypeSymbol;
        leftType = leftType.UnwrapLiteralType() ?? leftType;
        rightType = rightType.UnwrapLiteralType() ?? rightType;

        if (TryBindLiftedUserDefinedEqualityOperator(
                opKind,
                operatorInfo.MetadataName,
                left,
                right,
                leftType,
                rightType,
                diagnosticLocation,
                leftSyntax,
                rightSyntax,
                callSyntax) is { } liftedEquality)
        {
            return liftedEquality;
        }

        var candidates = GetUserDefinedOperatorCandidates(
            operatorInfo.MetadataName,
            leftType,
            rightType,
            diagnosticLocation);

        if (candidates.IsDefaultOrEmpty)
            return null;

        var arguments = new[]
        {
            new BoundArgument(left, RefKind.None, null, leftSyntax),
            new BoundArgument(right, RefKind.None, null, rightSyntax),
        };

        var resolution = OverloadResolver.ResolveOverload(
            candidates,
            arguments,
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible,
            callSyntax: callSyntax);

        if (resolution.IsAmbiguous)
        {
            var operatorText = OperatorFacts.GetDisplayText(opKind);
            _diagnostics.ReportCallIsAmbiguous($"operator {operatorText}", resolution.AmbiguousCandidates, diagnosticLocation ?? Location.None);
            return ErrorExpression(
                reason: BoundExpressionReason.Ambiguous,
                candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
        }

        if (!resolution.Success)
            return null;

        var method = resolution.Method!;
        ReportObsoleteIfNeeded(method, callSyntax?.GetLocation() ?? diagnosticLocation ?? Location.None);
        var converted = ConvertArguments(method.Parameters, arguments, callSyntax);
        return new BoundInvocationExpression(method, converted);
    }

    private BoundExpression? TryBindLiftedUserDefinedEqualityOperator(
        SyntaxKind opKind,
        string metadataName,
        BoundExpression left,
        BoundExpression right,
        ITypeSymbol leftType,
        ITypeSymbol rightType,
        Location? diagnosticLocation,
        ExpressionSyntax? leftSyntax,
        ExpressionSyntax? rightSyntax,
        SyntaxNode? callSyntax)
    {
        if (opKind is not SyntaxKind.EqualsEqualsToken and not SyntaxKind.NotEqualsToken)
            return null;

        var leftNullable = leftType as NullableTypeSymbol;
        var rightNullable = rightType as NullableTypeSymbol;

        var leftIsNullableValue = leftNullable is { UnderlyingType: { IsValueType: true } };
        var rightIsNullableValue = rightNullable is { UnderlyingType: { IsValueType: true } };

        if (!leftIsNullableValue && !rightIsNullableValue)
            return null;

        var candidateLeftType = leftIsNullableValue ? leftNullable!.UnderlyingType : leftType;
        var candidateRightType = rightIsNullableValue ? rightNullable!.UnderlyingType : rightType;

        var candidates = GetUserDefinedOperatorCandidates(
            metadataName,
            candidateLeftType,
            candidateRightType,
            diagnosticLocation);

        if (candidates.IsDefaultOrEmpty)
            return null;

        var unwrappedLeft = leftIsNullableValue
            ? new BoundNullableValueExpression(left, candidateLeftType)
            : left;
        var unwrappedRight = rightIsNullableValue
            ? new BoundNullableValueExpression(right, candidateRightType)
            : right;

        var liftedArguments = new[]
        {
            new BoundArgument(unwrappedLeft, RefKind.None, null, leftSyntax),
            new BoundArgument(unwrappedRight, RefKind.None, null, rightSyntax),
        };

        var resolution = OverloadResolver.ResolveOverload(
            candidates,
            liftedArguments,
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible,
            callSyntax: callSyntax);

        if (resolution.IsAmbiguous)
        {
            var operatorText = OperatorFacts.GetDisplayText(opKind);
            _diagnostics.ReportCallIsAmbiguous($"operator {operatorText}", resolution.AmbiguousCandidates, diagnosticLocation ?? Location.None);
            return ErrorExpression(
                reason: BoundExpressionReason.Ambiguous,
                candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
        }

        if (!resolution.Success)
            return null;

        var method = resolution.Method!;
        ReportObsoleteIfNeeded(method, callSyntax?.GetLocation() ?? diagnosticLocation ?? Location.None);
        var convertedArguments = ConvertArguments(method.Parameters, liftedArguments, callSyntax);
        var invocation = (BoundExpression)new BoundInvocationExpression(method, convertedArguments);
        var isInequality = opKind == SyntaxKind.NotEqualsToken;

        if (leftIsNullableValue && rightIsNullableValue)
        {
            var leftHasValue = CreateNullableHasValueCondition(left);
            var rightHasValue = CreateNullableHasValueCondition(right);
            var mismatchResult = CreateBoolLiteral(isInequality);
            var bothNullResult = CreateBoolLiteral(!isInequality);
            var rightBranch = new BoundIfExpression(rightHasValue, mismatchResult, bothNullResult);
            var leftThenBranch = new BoundIfExpression(rightHasValue, invocation, mismatchResult);
            return new BoundIfExpression(leftHasValue, leftThenBranch, rightBranch);
        }

        var hasValue = leftIsNullableValue
            ? CreateNullableHasValueCondition(left)
            : CreateNullableHasValueCondition(right);
        var whenNull = CreateBoolLiteral(isInequality);
        return new BoundIfExpression(hasValue, invocation, whenNull);
    }

    private BoundExpression CreateNullableHasValueCondition(BoundExpression nullableOperand)
    {
        var nullableType = nullableOperand.Type;
        if (nullableType is null)
            return ErrorExpression(reason: BoundExpressionReason.NotFound);

        if (nullableType.TryGetUnion() is { TypeKind: TypeKind.Struct } &&
            nullableType is INamedTypeSymbol namedUnionType)
        {
            var hasValueProperty = namedUnionType
                .GetMembers("HasValue")
                .OfType<IPropertySymbol>()
                .FirstOrDefault(property =>
                    property.GetMethod is not null &&
                    SymbolEqualityComparer.Default.Equals(property.ContainingType, namedUnionType));

            if (hasValueProperty?.GetMethod is not null)
                return new BoundInvocationExpression(
                    hasValueProperty.GetMethod,
                    Array.Empty<BoundExpression>(),
                    nullableOperand,
                    requiresReceiverAddress: true);
        }

        var nullLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, nullableType);
        if (!BoundBinaryOperator.TryLookup(Compilation, SyntaxKind.NotEqualsToken, nullableType, nullableType, out var notEquals))
            return ErrorExpression(reason: BoundExpressionReason.NotFound);

        return new BoundBinaryExpression(nullableOperand, notEquals, nullLiteral);
    }

    private BoundLiteralExpression CreateBoolLiteral(bool value)
    {
        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        var kind = value ? BoundLiteralExpressionKind.TrueLiteral : BoundLiteralExpressionKind.FalseLiteral;
        return new BoundLiteralExpression(kind, value, boolType);
    }

    private BoundExpression? BindUserDefinedUnaryOperator(
        SyntaxKind opKind,
        BoundExpression operand,
        Location? diagnosticLocation,
        ExpressionSyntax? operandSyntax,
        SyntaxNode? callSyntax)
    {
        if (!OperatorFacts.TryGetUserDefinedOperatorInfo(opKind, 1, out var operatorInfo))
            return null;

        var operandType = operand.Type ?? Compilation.ErrorTypeSymbol;
        operandType = operandType.UnwrapLiteralType() ?? operandType;
        var candidates = GetUserDefinedOperatorCandidates(operatorInfo.MetadataName, operandType, diagnosticLocation);

        if (candidates.IsDefaultOrEmpty)
            return null;

        var arguments = new[]
        {
            new BoundArgument(operand, RefKind.None, null, operandSyntax),
        };

        var resolution = OverloadResolver.ResolveOverload(
            candidates,
            arguments,
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible,
            callSyntax: callSyntax);

        if (resolution.IsAmbiguous)
        {
            var operatorText = OperatorFacts.GetDisplayText(opKind);
            _diagnostics.ReportCallIsAmbiguous($"operator {operatorText}", resolution.AmbiguousCandidates, diagnosticLocation ?? Location.None);
            return ErrorExpression(
                reason: BoundExpressionReason.Ambiguous,
                candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
        }

        if (!resolution.Success)
            return null;

        var method = resolution.Method!;
        ReportObsoleteIfNeeded(method, callSyntax?.GetLocation() ?? diagnosticLocation ?? Location.None);
        var converted = ConvertArguments(method.Parameters, arguments, callSyntax);
        return new BoundInvocationExpression(method, converted);
    }

    private ImmutableArray<IMethodSymbol> GetUserDefinedOperatorCandidates(
        string metadataName,
        ITypeSymbol operandType,
        Location? diagnosticLocation)
        => GetUserDefinedOperatorCandidates(metadataName, operandType, operandType, diagnosticLocation);

    private ImmutableArray<IMethodSymbol> GetUserDefinedOperatorCandidates(
        string metadataName,
        ITypeSymbol leftType,
        ITypeSymbol rightType,
        Location? diagnosticLocation)
    {
        var types = new List<ITypeSymbol> { leftType };

        if (!SymbolEqualityComparer.Default.Equals(leftType, rightType))
            types.Add(rightType);

        var candidates = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var type in types)
        {
            foreach (var method in type.GetMembers(metadataName).OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.UserDefinedOperator)
                    continue;

                if (!method.IsStatic)
                    continue;

                // Static abstract interface operators are invoked through a constrained
                // type parameter. Interface members surfaced through a concrete metadata
                // type are not concrete user-defined operator candidates.
                if (type is not ITypeParameterSymbol && method.ContainingType?.TypeKind == TypeKind.Interface)
                    continue;

                if (seen.Add(method))
                    candidates.Add(method);
            }

            foreach (var method in LookupExtensionStaticMethods(metadataName, type))
            {
                if (method.MethodKind != MethodKind.UserDefinedOperator)
                    continue;

                if (!method.IsStatic || !method.IsStaticExtensionMember)
                    continue;

                if (seen.Add(method))
                    candidates.Add(method);
            }
        }

        if (candidates.Count == 0)
            return ImmutableArray<IMethodSymbol>.Empty;

        return GetAccessibleMethods(
            candidates.ToImmutable(),
            diagnosticLocation ?? Location.None,
            reportIfInaccessible: true);
    }

    private BoundExpression BindInvocationExpression(InvocationExpressionSyntax syntax)
    {
        BoundExpression? receiver;
        string methodName;

        if (syntax.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var boundMember = BindMemberAccessExpression(memberAccess, preferMethods: true, allowEventAccess: true);

            if (IsErrorExpression(boundMember))
                return boundMember is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(boundMember.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

            if (boundMember is BoundMethodGroupExpression methodGroup)
                return BindInvocationOnMethodGroup(methodGroup, syntax);

            if (boundMember is BoundMemberAccessExpression { Member: IEventSymbol eventSymbol } eventAccess)
            {
                receiver = BindEventInvocationReceiver(eventSymbol, eventAccess, syntax.Expression);
                if (IsErrorExpression(receiver))
                    return receiver is BoundErrorExpression boundError
                        ? boundError
                        : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

                methodName = "Invoke";
            }
            else if (boundMember is BoundMemberAccessExpression { Member: IMethodSymbol method } memberExpr)
            {
                {
                    var argExprs = BindInvocationArguments(syntax.ArgumentList.Arguments, method, memberExpr.Receiver, out var argErrors);

                    if (argErrors)
                    {
                        var candidates = ImmutableArray.Create(method);
                        return ErrorExpression(
                            method.ReturnType,
                            method,
                            BoundExpressionReason.ArgumentBindingFailed,
                            AsSymbolCandidates(candidates));
                    }

                    if (AreArgumentsCompatibleWithMethod(method, argExprs.Length, memberExpr.Receiver, argExprs))
                    {
                        var convertedArgs = ConvertArguments(method.Parameters, argExprs, syntax);
                        ReportObsoleteIfNeeded(method, syntax.Expression.GetLocation());
                        return new BoundInvocationExpression(method, convertedArgs, memberExpr.Receiver);
                    }
                }

                receiver = memberExpr.Receiver;
                methodName = method.Name;
            }
            else if (boundMember is BoundUnionCaseExpression unionCaseCallee)
            {
                return BindInvokedUnionCaseExpression(unionCaseCallee, syntax);
            }
            else if (boundMember is BoundTypeExpression { Type: INamedTypeSymbol namedType })
            {
                // If the callee binds to a type, `TypeName(...)` is a constructor invocation.
                // Bind arguments with ctor-parameter target types so member bindings like `.Human` / `.Male` can resolve.
                return BindConstructorInvocation(namedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }
            else
            {
                receiver = boundMember;
                methodName = "Invoke";
            }
        }
        else if (syntax.Expression is MemberBindingExpressionSyntax memberBinding)
        {
            if (IsTargetTypedConstructorBinding(memberBinding))
            {
                var targetType = GetTargetType(syntax);
                if (targetType is not null && targetType.TypeKind != TypeKind.Error)
                {
                    targetType = UnwrapTaskLikeTargetType(UnwrapAlias(targetType)).GetNonNullableType();
                    if (targetType is INamedTypeSymbol targetNamedType)
                        return BindConstructorInvocation(targetNamedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
                }

                _diagnostics.ReportMemberAccessRequiresTargetType("init", memberBinding.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.MissingType);
            }

            var invocationTargetType = GetTargetType(syntax);
            var boundMember = invocationTargetType is not null && invocationTargetType.TypeKind != TypeKind.Error
                ? BindMemberBindingExpression(memberBinding, invocationTargetType, allowEventAccess: true)
                : BindMemberBindingExpression(memberBinding, allowEventAccess: true);

            if (IsErrorExpression(boundMember))
                return boundMember is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(boundMember.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

            if (boundMember is BoundMethodGroupExpression methodGroup)
                return BindInvocationOnMethodGroup(methodGroup, syntax);

            if (boundMember is BoundMemberAccessExpression { Member: IEventSymbol eventSymbol } eventAccess)
            {
                receiver = BindEventInvocationReceiver(eventSymbol, eventAccess, syntax.Expression);
                if (IsErrorExpression(receiver))
                    return receiver is BoundErrorExpression boundError
                        ? boundError
                        : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

                methodName = "Invoke";
            }
            else if (boundMember is BoundMemberAccessExpression { Member: IMethodSymbol method } memberExpr)
            {
                {
                    var argExprs = BindInvocationArguments(syntax.ArgumentList.Arguments, method, memberExpr.Receiver, out var argErrors);

                    if (argErrors)
                    {
                        var candidates = ImmutableArray.Create(method);
                        return ErrorExpression(
                            method.ReturnType,
                            method,
                            BoundExpressionReason.ArgumentBindingFailed,
                            AsSymbolCandidates(candidates));
                    }

                    if (AreArgumentsCompatibleWithMethod(method, argExprs.Length, memberExpr.Receiver, argExprs))
                    {
                        var convertedArgs = ConvertArguments(method.Parameters, argExprs, syntax);
                        ReportObsoleteIfNeeded(method, syntax.Expression.GetLocation());
                        return new BoundInvocationExpression(method, convertedArgs, memberExpr.Receiver);
                    }
                }

                receiver = memberExpr.Receiver;
                methodName = method.Name;
            }
            else if (boundMember is BoundUnionCaseExpression unionCaseCallee2)
            {
                // The callee resolved to a union case expression (e.g. .SomeCase from a member binding).
                return BindInvokedUnionCaseExpression(unionCaseCallee2, syntax);
            }
            else if (boundMember is BoundTypeExpression { Type: INamedTypeSymbol namedType })
            {
                // If the callee binds to a type, `TypeName(...)` is a constructor invocation.
                // Bind arguments with ctor-parameter target types so member bindings like `.Human` / `.Male` can resolve.
                return BindConstructorInvocation(namedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }
            else
            {
                receiver = boundMember;
                methodName = "Invoke";
            }
        }
        else if (syntax.Expression is IdentifierNameSyntax id)
        {
            var invocationTargetType = GetTargetType(syntax);
            if (invocationTargetType is not null)
            {
                var unwrappedTargetType = UnwrapTaskLikeTargetType(invocationTargetType);
                if (TryBindDiscriminatedUnionCase(unwrappedTargetType, id.Identifier.ValueText, id.Identifier.GetLocation()) is BoundUnionCaseExpression contextualUnionCase)
                    return BindInvokedUnionCaseExpression(contextualUnionCase, syntax);
            }

            var boundIdentifier = BindIdentifierName(id, allowEventAccess: true);
            if (IsErrorExpression(boundIdentifier))
            {
                var unionCaseCandidates = LookupUnionCaseTypeCandidates(id.Identifier.ValueText);
                if (unionCaseCandidates.Length > 1)
                {
                    var (first, second) = GetAmbiguousCaseDisplayNames(unionCaseCandidates[0], unionCaseCandidates[1]);
                    _diagnostics.ReportCallIsAmbiguous(first, second, syntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.Ambiguous);
                }

                receiver = null;
                methodName = id.Identifier.ValueText;
                return BindInvocationExpressionCore(receiver, methodName, syntax.ArgumentList, syntax.Expression, syntax);
            }

            if (boundIdentifier is BoundMethodGroupExpression methodGroup)
                return BindInvocationOnMethodGroup(methodGroup, syntax);

            if (boundIdentifier is BoundMemberAccessExpression { Member: IEventSymbol eventSymbol } eventAccess)
            {
                receiver = BindEventInvocationReceiver(eventSymbol, eventAccess, syntax.Expression);
                if (IsErrorExpression(receiver))
                    return receiver is BoundErrorExpression boundError
                        ? boundError
                        : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

                methodName = "Invoke";
            }
            else if (boundIdentifier is BoundUnionCaseExpression unionCaseCallee)
            {
                var caseCandidates = LookupUnionCaseTypeCandidates(id.Identifier.ValueText);
                if (caseCandidates.Length > 1)
                {
                    var (first, second) = GetAmbiguousCaseDisplayNames(caseCandidates[0], caseCandidates[1]);
                    _diagnostics.ReportCallIsAmbiguous(first, second, syntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.Ambiguous);
                }

                if (invocationTargetType is not null)
                    return BindInvokedUnionCaseExpression(unionCaseCallee, syntax);

                _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(id.Identifier.ValueText, id.Identifier.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.MissingType);
            }
            else if (IsInvocableValueReceiver(boundIdentifier))
            {
                receiver = boundIdentifier;
                methodName = "Invoke";
            }
            else if (boundIdentifier is BoundTypeExpression { Type: INamedTypeSymbol namedType })
            {
                if (!HasApplicableExplicitFunctionConstructor(namedType, syntax) &&
                    TryBindInferredGenericConstructorAlternative(id.Identifier.ValueText, syntax, out var inferredGenericConstructor))
                {
                    return inferredGenericConstructor;
                }

                // If the callee binds to a type, `TypeName(...)` is a constructor invocation.
                // Union case types are treated as plain type instantiations (not target-typed).
                // But if the same case name exists in multiple unions in scope, report ambiguity.
                if (namedType.TryGetUnionCase() is not null)
                {
                    var caseCandidates = LookupUnionCaseTypeCandidates(id.Identifier.ValueText);
                    if (caseCandidates.Length > 1)
                    {
                        var (first, second) = GetAmbiguousCaseDisplayNames(caseCandidates[0], caseCandidates[1]);
                        _diagnostics.ReportCallIsAmbiguous(first, second, syntax.GetLocation());
                        return ErrorExpression(reason: BoundExpressionReason.Ambiguous);
                    }

                    _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(id.Identifier.ValueText, id.Identifier.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.MissingType);
                }

                return BindConstructorInvocation(namedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }
            else
            {
                receiver = null;
                methodName = id.Identifier.ValueText;
            }
        }
        else if (syntax.Expression is GenericNameSyntax generic)
        {
            var boundTypeArguments = TryBindTypeArguments(generic);
            if (boundTypeArguments is null)
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

            var unionCaseCandidates = LookupUnionCaseTypeCandidates(generic.Identifier.ValueText, boundTypeArguments.Value.Length);
            if (unionCaseCandidates.Length > 1)
            {
                var (first, second) = GetAmbiguousCaseDisplayNames(unionCaseCandidates[0], unionCaseCandidates[1]);
                _diagnostics.ReportCallIsAmbiguous(first, second, syntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.Ambiguous);
            }

            var symbolCandidates = LookupSymbols(generic.Identifier.ValueText)
                .OfType<IMethodSymbol>()
                .ToImmutableArray();

            if (!symbolCandidates.IsDefaultOrEmpty)
            {
                // A partial explicit list leaves leading method type parameters open.
                // Preserve the definitions so the shared invocation resolver can combine
                // the explicit trailing arguments with inference from the call arguments.
                // Fully explicit calls can still be constructed eagerly for precise
                // constraint diagnostics and target typing.
                if (symbolCandidates.Any(method => method.TypeParameters.Length > boundTypeArguments.Value.Length))
                {
                    var methodGroup = CreateMethodGroup(null, symbolCandidates);
                    return BindInvocationOnMethodGroup(methodGroup, syntax);
                }

                var diagnosticCountBeforeInstantiation = _diagnostics.AsEnumerable().Count();
                var instantiated = InstantiateMethodCandidates(symbolCandidates, boundTypeArguments.Value, generic, syntax.GetLocation());
                if (!instantiated.IsDefaultOrEmpty)
                {
                    var methodGroup = CreateMethodGroup(null, instantiated);
                    return BindInvocationOnMethodGroup(methodGroup, syntax);
                }

                var producedInstantiationDiagnostics = _diagnostics.AsEnumerable().Count() > diagnosticCountBeforeInstantiation;
                if (producedInstantiationDiagnostics)
                    return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);

                _diagnostics.ReportNoOverloadForMethod("method", generic.Identifier.ValueText, syntax.ArgumentList.Arguments.Count, syntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
            }

            var typeExpr = BindTypeSyntaxAsExpression(generic);
            if (typeExpr is BoundTypeExpression type && type.Type is INamedTypeSymbol namedType)
            {
                // If the callee binds to a type, `TypeName(...)` is a constructor invocation.
                // Bind arguments with ctor-parameter target types so member bindings like `.Human` / `.Male` can resolve.
                return BindConstructorInvocation(namedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }

            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }
        else
        {
            receiver = BindExpression(syntax.Expression);

            if (IsErrorExpression(receiver))
                return receiver is BoundErrorExpression boundError
                    ? boundError
                    : new BoundErrorExpression(receiver.Type ?? Compilation.ErrorTypeSymbol, null, BoundExpressionReason.OtherError);

            // If the callee binds to a type, `TypeName(...)` is a constructor invocation.
            // Bind arguments with ctor-parameter target types so member bindings like `.Human` / `.Male` can resolve.
            if (receiver is BoundTypeExpression typeExpr && typeExpr.Type is INamedTypeSymbol namedType)
            {
                return BindConstructorInvocation(namedType, syntax, receiverSyntax: syntax.Expression, receiver: null);
            }

            methodName = "Invoke";
        }

        return BindInvocationExpressionCore(receiver, methodName, syntax.ArgumentList, syntax.Expression, syntax);
    }

    private static bool IsTargetTypedConstructorBinding(MemberBindingExpressionSyntax memberBinding)
        => memberBinding.Name.IsMissing ||
           memberBinding.Name.Identifier.IsMissing ||
           string.IsNullOrEmpty(memberBinding.Name.Identifier.ValueText);

    private bool HasApplicableExplicitFunctionConstructor(INamedTypeSymbol type, InvocationExpressionSyntax syntax)
    {
        using var nonReportingScope = _diagnostics.CreateNonReportingScope();

        var constructors = FilterInvocationCandidatesForArgumentBinding(
            type.Constructors,
            syntax.ArgumentList.Arguments);

        if (constructors.IsDefaultOrEmpty)
            return false;

        var functionCompatibleConstructors = ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (var constructor in constructors)
        {
            if (HasCompatibleExplicitFunctionArguments(constructor, syntax))
                functionCompatibleConstructors.Add(constructor);
        }

        if (functionCompatibleConstructors.Count == 0)
            return false;

        constructors = functionCompatibleConstructors.ToImmutable();

        var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(
            constructors,
            syntax.ArgumentList.Arguments,
            out var hasErrors);

        if (hasErrors)
            return false;

        var resolution = OverloadResolver.ResolveOverload(
            constructors,
            boundArguments,
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible,
            callSyntax: syntax);

        return resolution.Success;
    }

    private bool HasCompatibleExplicitFunctionArguments(IMethodSymbol constructor, InvocationExpressionSyntax syntax)
    {
        for (var i = 0; i < syntax.ArgumentList.Arguments.Count; i++)
        {
            var argument = syntax.ArgumentList.Arguments[i];
            if (argument.Expression is not FunctionExpressionSyntax functionExpression)
                continue;

            if (!CanTargetExplicitFunctionArgument(functionExpression, constructor.Parameters[i]))
                return false;
        }

        return true;
    }

    private bool CanTargetExplicitFunctionArgument(FunctionExpressionSyntax functionExpression, IParameterSymbol targetParameter)
        => CanTargetExplicitFunctionParameters(GetExplicitFunctionParameters(functionExpression), targetParameter);

    private bool CanTargetExplicitFunctionParameters(ImmutableArray<ParameterSyntax> functionParameters, IParameterSymbol targetParameter)
    {
        var parameterType = targetParameter.Type is NullableTypeSymbol nullableParameterType
            ? nullableParameterType.UnderlyingType
            : targetParameter.Type;

        if (parameterType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType ||
            delegateType.GetDelegateInvokeMethod() is not { } invoke)
        {
            return false;
        }

        if (invoke.Parameters.Length != functionParameters.Length)
            return false;

        for (var i = 0; i < functionParameters.Length; i++)
        {
            if (functionParameters[i].TypeAnnotation?.Type is not { } parameterTypeSyntax)
                continue;

            var sourceType = ResolveTypeSyntaxOrError(parameterTypeSyntax);
            var targetType = invoke.Parameters[i].Type;
            if (!IsAssignable(targetType, sourceType, out _))
                return false;
        }

        return true;
    }

    private bool TryBindInferredGenericConstructorAlternative(
        string name,
        InvocationExpressionSyntax syntax,
        out BoundExpression boundExpression)
    {
        boundExpression = null!;

        if (!HasExplicitFunctionExpressionArgument(syntax))
            return false;

        BoundExpression? inferredCandidate = null;
        INamedTypeSymbol? inferredCandidateType = null;

        foreach (var candidate in LookupNamedTypeCandidates(name))
        {
            if (!candidate.IsGenericType || !IsUninstantiatedGenericType(candidate))
                continue;

            var constructors = FilterInvocationCandidatesForArgumentBinding(
                candidate.Constructors,
                syntax.ArgumentList.Arguments);

            if (constructors.IsDefaultOrEmpty)
                continue;

            var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(
                constructors,
                syntax.ArgumentList.Arguments,
                out var hasErrors);

            if (hasErrors)
                continue;

            if (!TryInferConstructedTypeForConstructor(candidate, boundArguments, out var inferredType) ||
                SymbolEqualityComparer.Default.Equals(inferredType, candidate))
            {
                continue;
            }

            if (inferredCandidate is not null)
            {
                var first = inferredCandidateType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? name;
                var second = inferredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                _diagnostics.ReportCallIsAmbiguous(first, second, syntax.GetLocation());
                boundExpression = ErrorExpression(reason: BoundExpressionReason.Ambiguous);
                return true;
            }

            inferredCandidateType = inferredType;
            inferredCandidate = BindConstructorInvocation(inferredType, syntax, receiverSyntax: syntax.Expression, receiver: null);
        }

        if (inferredCandidate is null)
            return false;

        boundExpression = inferredCandidate;
        return true;
    }

    private static int GetExplicitFunctionParameterCount(FunctionExpressionSyntax functionExpression)
        => functionExpression switch
        {
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
            SimpleFunctionExpressionSyntax simple => simple.Parameter.Identifier.IsMissing ? 0 : 1,
            _ => 0
        };

    private static ImmutableArray<ParameterSyntax> GetExplicitFunctionParameters(FunctionExpressionSyntax functionExpression)
        => functionExpression switch
        {
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToImmutableArray(),
            SimpleFunctionExpressionSyntax simple when !simple.Parameter.Identifier.IsMissing => ImmutableArray.Create(simple.Parameter),
            _ => ImmutableArray<ParameterSyntax>.Empty
        };

    private static bool HasExplicitFunctionExpressionArgument(InvocationExpressionSyntax syntax)
    {
        foreach (var argument in syntax.ArgumentList.Arguments)
        {
            if (argument.Expression is FunctionExpressionSyntax functionExpression &&
                GetExplicitFunctionParameterCount(functionExpression) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvocableValueReceiver(BoundExpression expression)
    {
        return expression switch
        {
            BoundLocalAccess or BoundParameterAccess or BoundFieldAccess or BoundPropertyAccess => true,
            BoundConversionExpression { Expression: BoundExpression inner } => IsInvocableValueReceiver(inner),
            _ => false,
        };
    }

    private BoundExpression BindInvokedUnionCaseExpression(BoundUnionCaseExpression unionCaseCallee, InvocationExpressionSyntax syntax)
    {
        unionCaseCallee = RefineInvokedUnionCaseCalleeForContext(unionCaseCallee, syntax);

        var caseCreation = TryBindUnionCaseFieldInitializer(unionCaseCallee, syntax, out var fieldInitializerCreation)
            ? fieldInitializerCreation
            : BindConstructorInvocation(unionCaseCallee.CaseType, syntax, receiverSyntax: syntax.Expression, receiver: null);
        if (caseCreation is not BoundObjectCreationExpression creationExpr)
            return caseCreation;

        var caseType = creationExpr.Constructor.ContainingType as INamedTypeSymbol ?? unionCaseCallee.CaseType;
        var unionType = ResolveInvokedUnionCaseUnionType(unionCaseCallee, caseType, syntax);
        var constructor = creationExpr.Constructor;
        var arguments = creationExpr.Arguments.ToImmutableArray();

        if (unionType.TryGetUnion() is not null &&
            ProjectCaseTypeToUnionArguments(caseType, unionType) is INamedTypeSymbol projectedCaseType &&
            !SymbolEqualityComparer.Default.Equals(projectedCaseType, caseType))
        {
            var projectedConstructor = projectedCaseType.Constructors.FirstOrDefault(candidate =>
            {
                if (candidate.Parameters.Length != arguments.Length)
                    return false;

                for (var i = 0; i < candidate.Parameters.Length; i++)
                {
                    var conversion = Compilation.ClassifyConversion(arguments[i].Type!, candidate.Parameters[i].Type);
                    if (!conversion.Exists || !conversion.IsImplicit)
                        return false;
                }

                return true;
            });

            if (projectedConstructor is not null)
            {
                var convertedArguments = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
                for (var i = 0; i < arguments.Length; i++)
                {
                    var argument = arguments[i];
                    var parameterType = projectedConstructor.Parameters[i].Type;
                    var conversion = Compilation.ClassifyConversion(argument.Type!, parameterType);

                    convertedArguments.Add(
                        conversion.Exists && !conversion.IsIdentity
                            ? new BoundConversionExpression(argument, parameterType, conversion)
                            : argument);
                }

                caseType = projectedCaseType;
                constructor = projectedConstructor;
                arguments = convertedArguments.MoveToImmutable();
            }
            else if (caseType.TryGetUnionCase() is { } caseSymbol)
            {
                // If the target-typed carrier would require reprojecting the inferred case into a
                // different generic instantiation, only keep that carrier when the constructor can
                // actually be rebound for the projected case. Otherwise preserve the carrier implied
                // by the inferred case so normal conversion diagnostics still run.
                unionType = TryProjectDiscriminatedUnionFromCaseArguments(
                    caseType,
                    caseSymbol,
                    out var inferredCaseUnion)
                    ? inferredCaseUnion
                    : caseSymbol.Union as INamedTypeSymbol ?? unionType;
            }
        }

        return new BoundUnionCaseExpression(
            unionType,
            caseType,
            constructor,
            arguments);
    }

    private bool TryBindUnionCaseFieldInitializer(
        BoundUnionCaseExpression unionCaseCallee,
        InvocationExpressionSyntax syntax,
        out BoundExpression boundExpression)
    {
        boundExpression = null!;

        if (syntax.Initializer is not { } initializer)
            return false;

        var assignmentEntries = initializer.Entries.OfType<ObjectInitializerAssignmentEntrySyntax>().ToArray();
        if (assignmentEntries.Length != initializer.Entries.Count)
            return false;

        var typeSymbol = unionCaseCallee.CaseType;
        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol) &&
            TryInferConstructedUnionCaseTypeFromTargetType(typeSymbol, syntax, out var targetTypedUnionCase))
        {
            typeSymbol = targetTypedUnionCase;
        }

        if (typeSymbol.IsGenericType && IsUninstantiatedGenericType(typeSymbol))
        {
            _diagnostics.ReportTypeRequiresTypeArguments(typeSymbol.Name, typeSymbol.Arity, syntax.Expression.GetLocation());
            boundExpression = new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OtherError);
            return true;
        }

        var constructors = typeSymbol.Constructors;
        if (constructors.IsDefaultOrEmpty)
            return false;

        var constructorForBinding = constructors[0];
        var arguments = new List<BoundArgument>(syntax.ArgumentList.Arguments.Count + assignmentEntries.Length);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenErrors = false;

        for (var i = 0; i < syntax.ArgumentList.Arguments.Count; i++)
        {
            var argumentSyntax = syntax.ArgumentList.Arguments[i];
            var targetParameter = i < constructorForBinding.Parameters.Length
                ? constructorForBinding.Parameters[i]
                : null;
            var syntaxRefKind = argumentSyntax.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None,
            };
            var boundArgument = BindInvocationArgumentExpression(argumentSyntax, targetParameter?.Type, syntaxRefKind);
            if (HasExpressionErrors(boundArgument))
                seenErrors = true;

            var name = argumentSyntax.NameColon?.Name.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
                name = null;
            else
                seenNames.Add(name);

            var isSpread = argumentSyntax.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken;
            arguments.Add(new BoundArgument(boundArgument, syntaxRefKind, name, argumentSyntax, isSpread));
        }

        foreach (var assignment in assignmentEntries)
        {
            if (assignment.EqualsToken.Kind != SyntaxKind.EqualsToken)
            {
                _ = BindExpression(assignment.Expression, allowReturn: false);
                boundExpression = ErrorExpression(reason: BoundExpressionReason.ArgumentBindingFailed);
                return true;
            }

            var assignmentName = assignment.Name;
            var name = assignmentName.Identifier.ValueText;
            if (!seenNames.Add(name))
                _diagnostics.ReportWithExpressionMemberAssignedMultipleTimes(name, assignmentName.GetLocation());

            var targetParameter = constructorForBinding.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, name, StringComparison.Ordinal) ||
                string.Equals(UnionFacts.GetCasePropertyName(parameter.Name), name, StringComparison.Ordinal));

            var boundArgument = targetParameter is not null
                ? BindExpressionWithTargetType(assignment.Expression, targetParameter.Type, allowReturn: false)
                : BindExpression(assignment.Expression, allowReturn: false);

            if (HasExpressionErrors(boundArgument))
                seenErrors = true;

            arguments.Add(new BoundArgument(boundArgument, RefKind.None, name, assignment));
        }

        if (seenErrors)
        {
            boundExpression = new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.ArgumentBindingFailed);
            return true;
        }

        if (!OverloadResolver.TryMapArguments(
                constructorForBinding.Parameters,
                arguments,
                treatAsExtension: false,
                out _))
        {
            _diagnostics.ReportNoOverloadForMethod("constructor for type", typeSymbol.Name, arguments.Count, syntax.GetLocation());
            boundExpression = new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.OverloadResolutionFailed);
            return true;
        }

        if (!EnsureMemberAccessible(constructorForBinding, syntax.Expression.GetLocation(), "constructor"))
        {
            boundExpression = new BoundErrorExpression(typeSymbol, null, BoundExpressionReason.Inaccessible);
            return true;
        }

        ReportObsoleteIfNeeded(constructorForBinding, syntax.Expression.GetLocation());
        var convertedArguments = ConvertArguments(constructorForBinding.Parameters, arguments, syntax);
        boundExpression = new BoundObjectCreationExpression(constructorForBinding, convertedArguments, receiver: null);
        return true;
    }

    private BoundUnionCaseExpression RefineInvokedUnionCaseCalleeForContext(
        BoundUnionCaseExpression unionCaseCallee,
        InvocationExpressionSyntax invocation)
    {
        var targetType = GetTargetType(invocation);
        if (targetType is null)
            return unionCaseCallee;

        targetType = UnwrapAlias(targetType);
        targetType = UnwrapTaskLikeTargetType(targetType);

        var targetUnion =
            targetType.TryGetUnion() as INamedTypeSymbol
            ?? targetType.TryGetUnionCase()?.Union as INamedTypeSymbol;

        if (targetUnion is null ||
            !SymbolEqualityComparer.Default.Equals(unionCaseCallee.UnionType.OriginalDefinition, targetUnion.OriginalDefinition))
        {
            return unionCaseCallee;
        }

        var projectedCaseType = ProjectCaseTypeToUnionArguments(unionCaseCallee.CaseType, targetUnion);
        if (projectedCaseType is not INamedTypeSymbol projectedNamedCase)
            return unionCaseCallee;

        if (SymbolEqualityComparer.Default.Equals(unionCaseCallee.UnionType, targetUnion) &&
            SymbolEqualityComparer.Default.Equals(unionCaseCallee.CaseType, projectedNamedCase))
        {
            return unionCaseCallee;
        }

        return new BoundUnionCaseExpression(
            targetUnion,
            projectedNamedCase,
            caseConstructor: null,
            arguments: ImmutableArray<BoundExpression>.Empty);
    }

    private INamedTypeSymbol ResolveInvokedUnionCaseUnionType(
        BoundUnionCaseExpression unionCaseCallee,
        INamedTypeSymbol caseType,
        InvocationExpressionSyntax invocation)
    {
        // Start with the callee-carried union (e.g. Result<int,string> from Result<int,string>.Error),
        // then only fall back to the case-reported union when needed.
        var resolvedUnion = unionCaseCallee.UnionType;
        var caseUnion = caseType.TryGetUnionCase()?.Union as INamedTypeSymbol;

        if (caseUnion is not null)
        {
            if (resolvedUnion is null ||
                (IsUninstantiatedGenericType(resolvedUnion) && !IsUninstantiatedGenericType(caseUnion)))
            {
                resolvedUnion = caseUnion;
            }
        }

        // For qualified calls (e.g. Result<int,string>.Error("boom")), recover the concrete
        // carrier from the left side to avoid collapsing back to an open Result<T,E>.
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var boundReceiver = BindExpression(memberAccess.Expression);
            if (boundReceiver is BoundTypeExpression { Type: INamedTypeSymbol receiverType })
            {
                var receiverUnion =
                    receiverType.TryGetUnion() as INamedTypeSymbol
                    ?? receiverType.TryGetUnionCase()?.Union as INamedTypeSymbol;

                if (receiverUnion is not null &&
                    (resolvedUnion is null ||
                     IsUninstantiatedGenericType(resolvedUnion) ||
                     !IsUninstantiatedGenericType(receiverUnion)))
                {
                    resolvedUnion = receiverUnion;
                }
            }
        }

        // If the invocation is target-typed (assignment, return, lambda return), prefer that
        // concrete union construction when it belongs to the same union family.
        var targetType = GetTargetType(invocation);
        if (targetType is null)
            return resolvedUnion;

        targetType = UnwrapAlias(targetType);
        targetType = UnwrapTaskLikeTargetType(targetType);

        var targetUnion =
            targetType.TryGetUnion() as INamedTypeSymbol
            ?? targetType.TryGetUnionCase()?.Union as INamedTypeSymbol;

        if (targetUnion is null)
            return resolvedUnion;

        if (!SymbolEqualityComparer.Default.Equals(resolvedUnion.OriginalDefinition, targetUnion.OriginalDefinition))
            return resolvedUnion;

        if (!CanRefineResolvedUnionFromTarget(resolvedUnion, targetUnion))
            return resolvedUnion;

        return targetUnion;

        static bool CanRefineResolvedUnionFromTarget(INamedTypeSymbol resolvedUnion, INamedTypeSymbol targetUnion)
        {
            if (IsUninstantiatedGenericType(resolvedUnion))
                return true;

            if (resolvedUnion.TypeArguments.Length != targetUnion.TypeArguments.Length)
                return false;

            var targetAddsConcreteArguments = false;

            for (var i = 0; i < resolvedUnion.TypeArguments.Length; i++)
            {
                var resolvedArgument = resolvedUnion.TypeArguments[i];
                var targetArgument = targetUnion.TypeArguments[i];

                if (SymbolEqualityComparer.Default.Equals(resolvedArgument, targetArgument))
                    continue;

                if (resolvedArgument is ITypeParameterSymbol)
                {
                    targetAddsConcreteArguments |= targetArgument is not ITypeParameterSymbol;
                    continue;
                }

                return false;
            }

            return targetAddsConcreteArguments;
        }
    }

    private BoundExpression BindInvocationExpressionCore(
        BoundExpression? receiver,
        string methodName,
        ArgumentListSyntax argumentList,
        SyntaxNode receiverSyntax,
        SyntaxNode callSyntax,
        bool suppressNullWarning = false)
    {
        if (receiver is not null && HasExpressionErrors(receiver))
            return AsErrorExpression(receiver);

        if (!suppressNullWarning)
            ReportNullableValueMemberAccess(receiver, receiverSyntax);

        // Bind invocation arguments AFTER we have a candidate set, so we can provide
        // best-effort target types for target-typed member bindings like `.Human`, `.Male`, `.Ok(...)`, etc.

        // Delegate invocation: receiver.Invoke(...)
        if (receiver is not null)
        {
            var receiverType = receiver.Type.UnwrapLiteralType() ?? receiver.Type;

            if (methodName == "Invoke" &&
                receiverType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType &&
                delegateType.GetDelegateInvokeMethod() is { } invokeMethod)
            {
                if (!EnsureMemberAccessible(invokeMethod, receiverSyntax.GetLocation(), GetSymbolKindForDiagnostic(invokeMethod)))
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                var invokeForArgBinding = ImmutableArray.Create(invokeMethod);
                invokeForArgBinding = FilterInvocationCandidatesForArgumentBinding(invokeForArgBinding, argumentList.Arguments);

                var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(invokeForArgBinding, argumentList.Arguments, out var hasErrors);
                if (hasErrors)
                    return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);

                if (!AreArgumentsCompatibleWithMethod(invokeMethod, boundArguments.Length, receiver, boundArguments))
                {
                    ReportSuppressedLambdaDiagnostics(boundArguments);
                    if (!HasArgumentBindingErrors(boundArguments) &&
                        !HasExistingArgumentErrors(argumentList.Arguments))
                        _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length, callSyntax.GetLocation());
                    return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
                }

                var converted = ConvertArguments(invokeMethod.Parameters, boundArguments, callSyntax);
                ReportObsoleteIfNeeded(invokeMethod, callSyntax.GetLocation());
                return new BoundInvocationExpression(invokeMethod, converted, receiver);
            }
        }

        // Namespace receiver: Namespace.TypeName(...)
        if (receiver is BoundNamespaceExpression nsReceiver)
        {
            var topLevelMethods = Compilation.GetNamespaceMembers(nsReceiver.Namespace, methodName, Compilation.Options.AllowNamespaceMemberImports)
                .OfType<IMethodSymbol>()
                .ToImmutableArray();
            if (!topLevelMethods.IsDefaultOrEmpty)
            {
                var methodGroupReceiver = new BoundTypeExpression(topLevelMethods[0].ContainingType!);
                var accessibleMethods = GetAccessibleMethods(DistinctMethodCandidates(topLevelMethods), callSyntax.GetLocation());
                var candidatesForArgumentBinding = !accessibleMethods.IsDefaultOrEmpty ? accessibleMethods : topLevelMethods;
                candidatesForArgumentBinding = FilterInvocationCandidatesForArgumentBinding(candidatesForArgumentBinding, argumentList.Arguments);

                var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(candidatesForArgumentBinding, argumentList.Arguments, out var hasErrors);
                if (hasErrors)
                    return InvocationError(methodGroupReceiver, methodName, BoundExpressionReason.ArgumentBindingFailed);

                if (accessibleMethods.IsDefaultOrEmpty)
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                var resolution = OverloadResolver.ResolveOverload(accessibleMethods, boundArguments, Compilation, binder: this, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
                if (resolution.Success)
                {
                    var method = resolution.Method!;
                    ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                    var convertedArgs = ConvertArguments(method.Parameters, boundArguments, callSyntax);
                    return new BoundInvocationExpression(method, convertedArgs, methodGroupReceiver);
                }

                if (resolution.IsAmbiguous)
                {
                    _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                    return ErrorExpression(
                        reason: BoundExpressionReason.Ambiguous,
                        candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
                }

                ReportSuppressedLambdaDiagnostics(boundArguments);
                if (!HasArgumentBindingErrors(boundArguments) &&
                    !HasExistingArgumentErrors(argumentList.Arguments))
                {
                    if (!ReportTypeArgumentConstraintFailureIfPresent(resolution, callSyntax.GetLocation()))
                        _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length, callSyntax.GetLocation());
                }

                return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
            }

            var typeInNs = nsReceiver.Namespace
                .GetMembers(methodName)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            if (typeInNs is null)
            {
                _diagnostics.ReportTypeOrNamespaceNameDoesNotExistInTheNamespace(methodName, nsReceiver.Namespace.Name, receiverSyntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            // Rebind arguments against ctor parameter types so target-typed member bindings can resolve.
            if (callSyntax is InvocationExpressionSyntax inv)
                return BindConstructorInvocation(typeInNs, inv, receiverSyntax, receiver);

            // Fallback: should not normally happen, but preserve behavior.
            var fallbackArgs = BindInvocationArguments(argumentList.Arguments, out var fallbackHasErrors);
            if (fallbackHasErrors)
                return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);
            return BindConstructorInvocation(typeInNs, fallbackArgs, callSyntax, receiverSyntax, receiver);
        }

        // Static call on type receiver: TypeName.Method(...)
        if (receiver is BoundTypeExpression typeReceiver)
        {
            var candidates = new SymbolQuery(methodName, typeReceiver.Type, IsStatic: true)
                .LookupMethods(this)
                .ToImmutableArray();

            if (!candidates.IsDefaultOrEmpty)
            {
                var accessibleCandidates = GetAccessibleMethods(candidates, receiverSyntax.GetLocation());

                // Bind args against accessible candidates if possible; otherwise bind against the full
                // candidate set so we still get target typing and diagnostics.
                var candidatesForArgBinding = !accessibleCandidates.IsDefaultOrEmpty ? accessibleCandidates : candidates;
                candidatesForArgBinding = FilterInvocationCandidatesForArgumentBinding(candidatesForArgBinding, argumentList.Arguments);

                var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(candidatesForArgBinding, argumentList.Arguments, out var hasErrors);
                if (hasErrors)
                    return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);

                if (accessibleCandidates.IsDefaultOrEmpty)
                    return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

                var resolution = OverloadResolver.ResolveOverload(accessibleCandidates, boundArguments, Compilation, binder: this, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
                if (resolution.Success)
                {
                    var method = resolution.Method!;
                    ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                    var convertedArgs = ConvertArguments(method.Parameters, boundArguments, callSyntax);
                    return new BoundInvocationExpression(method, convertedArgs, receiver);
                }

                if (resolution.IsAmbiguous)
                {
                    _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                    return ErrorExpression(
                        reason: BoundExpressionReason.Ambiguous,
                        candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
                }

                var nestedType = typeReceiver.Type
                    .GetMembers(methodName)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault();

                if (nestedType is not null)
                {
                    if (callSyntax is InvocationExpressionSyntax inv)
                        return BindConstructorInvocation(nestedType, inv, receiverSyntax, receiver);

                    var fallbackArgs = BindInvocationArguments(argumentList.Arguments, out var fallbackHasErrors);
                    if (fallbackHasErrors)
                        return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);
                    return BindConstructorInvocation(nestedType, fallbackArgs, callSyntax, receiverSyntax, receiver);
                }

                ReportSuppressedLambdaDiagnostics(boundArguments);
                if (!HasArgumentBindingErrors(boundArguments) &&
                    !HasExistingArgumentErrors(argumentList.Arguments))
                {
                    if (!ReportTypeArgumentConstraintFailureIfPresent(resolution, callSyntax.GetLocation()))
                        _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length, callSyntax.GetLocation());
                }
                return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
            }

            var nested = typeReceiver.Type
                .GetMembers(methodName)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            if (nested is not null)
            {
                if (callSyntax is InvocationExpressionSyntax inv)
                    return BindConstructorInvocation(nested, inv, receiverSyntax, receiver);

                var fallbackArgs = BindInvocationArguments(argumentList.Arguments, out var fallbackHasErrors);
                if (fallbackHasErrors)
                    return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);
                return BindConstructorInvocation(nested, fallbackArgs, callSyntax, receiverSyntax, receiver);
            }

            _diagnostics.ReportMemberDoesNotContainDefinition(typeReceiver.Type.Name, methodName, receiverSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        // Instance receiver call: receiver.Method(...)
        if (receiver is not null)
        {
            var candidates = new SymbolQuery(methodName, receiver.Type, IsStatic: false)
                .LookupMethods(this)
                .ToImmutableArray();

            if (candidates.IsDefaultOrEmpty)
            {
                if (methodName == "Invoke")
                {
                    if (TryGetInvokedMemberName(receiverSyntax, out var memberName))
                        _diagnostics.ReportNonInvocableMember(memberName, receiverSyntax.GetLocation());
                    else
                        _diagnostics.ReportInvalidInvocation(receiverSyntax.GetLocation());
                }
                else
                    _diagnostics.ReportMemberDoesNotContainDefinition(receiver.Type.Name, methodName, receiverSyntax.GetLocation());

                return ErrorExpression(reason: BoundExpressionReason.NotFound);
            }

            var accessibleCandidates = GetAccessibleMethods(candidates, receiverSyntax.GetLocation());
            var candidatesForArgBinding = !accessibleCandidates.IsDefaultOrEmpty ? accessibleCandidates : candidates;
            candidatesForArgBinding = FilterInvocationCandidatesForArgumentBinding(candidatesForArgBinding, argumentList.Arguments);

            var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(candidatesForArgBinding, argumentList.Arguments, out var hasErrors, receiver);
            if (hasErrors)
                return InvocationError(receiver, methodName, BoundExpressionReason.ArgumentBindingFailed);

            if (accessibleCandidates.IsDefaultOrEmpty)
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

            var resolution = OverloadResolver.ResolveOverload(accessibleCandidates, boundArguments, Compilation, binder: this, receiver: receiver, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
            if (resolution.Success)
            {
                var method = resolution.Method!;
                ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                var convertedArgs = ConvertArguments(method.Parameters, boundArguments, callSyntax);
                return new BoundInvocationExpression(method, convertedArgs, receiver);
            }

            if (resolution.IsAmbiguous)
            {
                _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                return ErrorExpression(
                    reason: BoundExpressionReason.Ambiguous,
                    candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
            }

            ReportSuppressedLambdaDiagnostics(boundArguments);
            if (!HasArgumentBindingErrors(boundArguments) &&
                !HasExistingArgumentErrors(argumentList.Arguments))
            {
                if (!ReportTypeArgumentConstraintFailureIfPresent(resolution, callSyntax.GetLocation()))
                    _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length, callSyntax.GetLocation());
            }
            return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
        }

        // Global function call: MethodName(...)
        var methodCandidates = new SymbolQuery(methodName)
            .LookupMethods(this)
            .ToImmutableArray();
        if (!methodCandidates.IsDefaultOrEmpty)
        {
            var accessibleMethods = GetAccessibleMethods(methodCandidates, receiverSyntax.GetLocation());
            var candidatesForArgBinding = !accessibleMethods.IsDefaultOrEmpty ? accessibleMethods : methodCandidates;
            candidatesForArgBinding = FilterInvocationCandidatesForArgumentBinding(candidatesForArgBinding, argumentList.Arguments);

            var boundArguments = BindInvocationArgumentsWithCandidateTargetTypes(candidatesForArgBinding, argumentList.Arguments, out var hasErrors);
            if (hasErrors)
                return InvocationError(null, methodName, BoundExpressionReason.ArgumentBindingFailed);

            if (accessibleMethods.IsDefaultOrEmpty)
                return ErrorExpression(reason: BoundExpressionReason.Inaccessible);

            var resolution = OverloadResolver.ResolveOverload(accessibleMethods, boundArguments, Compilation, binder: this, canBindLambda: EnsureLambdaCompatible, callSyntax: callSyntax);
            if (resolution.Success)
            {
                var method = resolution.Method!;
                ReportObsoleteIfNeeded(method, callSyntax.GetLocation());
                var convertedArgs = ConvertArguments(method.Parameters, boundArguments, callSyntax);
                return new BoundInvocationExpression(method, convertedArgs, null);
            }

            if (resolution.IsAmbiguous)
            {
                _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, callSyntax.GetLocation());
                return ErrorExpression(
                    reason: BoundExpressionReason.Ambiguous,
                    candidates: AsSymbolCandidates(resolution.AmbiguousCandidates));
            }

            if (LookupType(methodName) is INamedTypeSymbol { TypeKind: not TypeKind.Error } typeFallback)
            {
                // Rebind arguments against ctor parameter types so target-typed member bindings can resolve.
                if (callSyntax is InvocationExpressionSyntax inv)
                    return BindConstructorInvocation(typeFallback, inv, receiverSyntax: receiverSyntax, receiver: null);

                return BindConstructorInvocation(typeFallback, boundArguments, callSyntax, receiverSyntax, receiver: null);
            }

            ReportSuppressedLambdaDiagnostics(boundArguments);
            if (!HasArgumentBindingErrors(boundArguments) &&
                !HasExistingArgumentErrors(argumentList.Arguments))
            {
                if (!ReportTypeArgumentConstraintFailureIfPresent(resolution, callSyntax.GetLocation()))
                    _diagnostics.ReportNoOverloadForMethod("method", methodName, boundArguments.Length, callSyntax.GetLocation());
            }
            return ErrorExpression(reason: BoundExpressionReason.OverloadResolutionFailed);
        }

        var unionCaseCandidates = LookupUnionCaseTypeCandidates(methodName);
        if (unionCaseCandidates.Length > 1)
        {
            var (first, second) = GetAmbiguousCaseDisplayNames(unionCaseCandidates[0], unionCaseCandidates[1]);
            _diagnostics.ReportCallIsAmbiguous(first, second, callSyntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.Ambiguous);
        }

        if (LookupType(methodName) is INamedTypeSymbol { TypeKind: not TypeKind.Error } typeSymbol)
        {
            if (callSyntax is InvocationExpressionSyntax inv)
                return BindConstructorInvocation(typeSymbol, inv, receiverSyntax: receiverSyntax, receiver: null);

            var fallbackArgs = BindInvocationArguments(argumentList.Arguments, out var fallbackHasErrors);
            if (fallbackHasErrors)
                return InvocationError(null, methodName, BoundExpressionReason.ArgumentBindingFailed);
            return BindConstructorInvocation(typeSymbol, fallbackArgs, callSyntax, receiverSyntax, receiver: null);
        }

        _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(methodName, receiverSyntax.GetLocation());
        return ErrorExpression(reason: BoundExpressionReason.NotFound);
    }

    private ImmutableArray<INamedTypeSymbol> LookupUnionCaseTypeCandidates(
        string name,
        int? typeArgumentCount = null,
        bool includeNamespaceTypeLookups = false)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        // Track seen types by OriginalDefinition reference: the same case type can be found via
        // multiple lookup paths (direct LookupSymbols + AddCasesFromUnionCarrier), and for source
        // types the two paths may return different symbol instances for the same logical type.
        var seenOriginalDefs = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void AddCaseIfMatch(INamedTypeSymbol caseType)
        {
            if (caseType.TryGetUnionCase() is null)
                return;

            if (!string.Equals(caseType.Name, name, StringComparison.Ordinal))
                return;

            if (typeArgumentCount is int expectedArity && caseType.Arity != expectedArity)
                return;

            // Deduplicate by OriginalDefinition reference so that the same case type found
            // via both LookupSymbols and AddCasesFromUnionCarrier is counted only once.
            if (seenOriginalDefs.Add(caseType.OriginalDefinition))
                builder.Add(caseType);
        }

        void AddCasesFromUnionCarrier(INamedTypeSymbol carrier)
        {
            var union = carrier.TryGetUnion();
            if (union is null)
                return;

            foreach (var caseType in union.DeclaredCaseTypes)
            {
                if (!string.Equals(caseType.Name, name, StringComparison.Ordinal))
                    continue;

                if (typeArgumentCount is int expectedArity && caseType.Arity != expectedArity)
                    continue;

                if (seenOriginalDefs.Add(caseType.OriginalDefinition))
                    builder.Add(caseType);
            }
        }

        if (includeNamespaceTypeLookups &&
            CurrentNamespace?.LookupType(name) is INamedTypeSymbol currentNamespaceType)
        {
            AddCaseIfMatch(currentNamespaceType);
        }

        // Imported type scopes can introduce union cases that are not surfaced as ordinary
        // symbols. Only wildcard type imports (`import UnionType.*`) bring cases into
        // unqualified scope; importing or aliasing the carrier itself does not.
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (current is not ImportBinder importBinder)
                continue;

            foreach (var importedScope in importBinder.GetImportedNamespacesOrTypeScopes())
            {
                if (importedScope is INamedTypeSymbol importedTypeScope)
                    AddCasesFromUnionCarrier(importedTypeScope);
                else if (importedScope is INamespaceSymbol importedNamespaceScope &&
                    importBinder.TryResolveTypeFromNamespaceName(importedNamespaceScope, out var namespaceNamedType) &&
                    namespaceNamedType is INamedTypeSymbol namespaceNamedCarrier)
                {
                    AddCasesFromUnionCarrier(namespaceNamedCarrier);
                }

                if (includeNamespaceTypeLookups &&
                    importedScope.LookupType(name) is INamedTypeSymbol importedScopeType)
                {
                    AddCaseIfMatch(importedScopeType);
                }
            }

            foreach (var importedType in importBinder.GetImportedTypes().OfType<INamedTypeSymbol>())
                AddCaseIfMatch(importedType);

            foreach (var aliasList in importBinder.GetAliases().Values)
            {
                foreach (var aliasSymbol in aliasList)
                {
                    if (aliasSymbol.UnderlyingSymbol is not INamedTypeSymbol aliasNamedType)
                        continue;

                    if (!string.Equals(aliasSymbol.Name, name, StringComparison.Ordinal))
                        continue;

                    if (aliasNamedType.TryGetUnionCase() is null)
                        continue;

                    if (typeArgumentCount is int expectedArity && aliasNamedType.Arity != expectedArity)
                        continue;

                    if (seenOriginalDefs.Add(aliasNamedType.OriginalDefinition))
                        builder.Add(aliasNamedType);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static bool TryGetInvokedMemberName(SyntaxNode receiverSyntax, out string memberName)
    {
        switch (receiverSyntax)
        {
            case IdentifierNameSyntax identifier:
                memberName = identifier.Identifier.ValueText;
                return !string.IsNullOrEmpty(memberName);
            case GenericNameSyntax generic:
                memberName = generic.Identifier.ValueText;
                return !string.IsNullOrEmpty(memberName);
            case MemberAccessExpressionSyntax memberAccess:
                memberName = memberAccess.Name.Identifier.ValueText;
                return !string.IsNullOrEmpty(memberName);
            case MemberBindingExpressionSyntax memberBinding:
                memberName = memberBinding.Name.Identifier.ValueText;
                return !string.IsNullOrEmpty(memberName);
            default:
                memberName = string.Empty;
                return false;
        }
    }

    private BoundArgument[] BindInvocationArguments(SeparatedSyntaxList<ArgumentSyntax> arguments, out bool hasErrors)
    {
        if (arguments.Count == 0)
        {
            hasErrors = false;
            return Array.Empty<BoundArgument>();
        }

        var boundArguments = new BoundArgument[arguments.Count];
        var seenErrors = false;

        for (int i = 0; i < arguments.Count; i++)
        {
            var syntax = arguments[i];
            var boundArg = BindExpression(syntax.Expression);
            if (HasExpressionErrors(boundArg))
                seenErrors = true;

            var name = syntax.NameColon?.Name.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
                name = null;

            var isSpread = syntax.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken;
            boundArguments[i] = new BoundArgument(boundArg, RefKind.None, name, syntax, isSpread);
        }

        hasErrors = seenErrors;
        return boundArguments;
    }

    private BoundArgument[] BindInvocationArguments(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IMethodSymbol targetMethod,
        BoundExpression? receiver,
        out bool hasErrors)
    {
        if (arguments.Count == 0)
        {
            hasErrors = false;
            return Array.Empty<BoundArgument>();
        }

        var boundArguments = new BoundArgument[arguments.Count];
        var seenErrors = false;
        var extensionReceiverImplicit = targetMethod.IsExtensionMethod && IsExtensionReceiver(receiver);

        for (int i = 0; i < arguments.Count; i++)
        {
            var syntax = arguments[i];
            var targetType = TryGetLambdaParameter(targetMethod, i, extensionReceiverImplicit, out var parameter)
                ? parameter!.Type
                : null;
            var syntaxRefKind = syntax.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None,
            };
            var boundArg = BindInvocationArgumentExpression(syntax, targetType, syntaxRefKind);
            if (HasExpressionErrors(boundArg))
                seenErrors = true;

            var name = syntax.NameColon?.Name.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
                name = null;

            var isSpread = syntax.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken;
            boundArguments[i] = new BoundArgument(boundArg, syntaxRefKind, name, syntax, isSpread);
        }

        hasErrors = seenErrors;
        return boundArguments;
    }

    private void ReportNullableValueMemberAccess(BoundExpression? receiver, SyntaxNode receiverSyntax)
    {
        if (receiver is null or BoundTypeExpression or BoundNamespaceExpression ||
            receiver.Type is null ||
            !receiver.Type.IsNullable)
        {
            return;
        }

        _diagnostics.ReportNullableValueMemberAccess(
            receiver.Type.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
            receiverSyntax.GetLocation());
    }

    private BoundExpression ApplyIsNotNullNarrowing(BoundExpression expression, ISymbol symbol)
    {
        if (!Compilation.Options.EnableIsNotNullNarrowing ||
            !_isNotNullNarrowedSymbols.Contains(symbol) ||
            expression.Type is not { IsNullable: true } nullableType ||
            !nullableType.TryGetNullableUnderlyingType(out var underlyingType))
        {
            return expression;
        }

        if (nullableType.GetNullableAbiProjection() == NullableAbiProjection.NullableValueType)
            return new BoundNullableValueExpression(expression, underlyingType);

        return new BoundConversionExpression(
            expression,
            underlyingType,
            new Conversion(isImplicit: true, isIdentity: true, isReference: !underlyingType.IsValueType),
            isNullableSuppression: true);
    }

    private static bool IsErrorExpression(BoundExpression expression)
        => expression is BoundErrorExpression;

    private static bool HasExpressionErrors(BoundExpression expression)
    {
        if (IsErrorExpression(expression))
            return true;

        if (expression is BoundFunctionExpression function)
            return HasFunctionExpressionErrors(function);

        return expression.Type?.ContainsErrorType() == true;
    }

    private static bool HasFunctionExpressionErrors(BoundFunctionExpression function)
    {
        var finder = new BoundErrorExpressionFinder();
        finder.Visit(function.Body);
        return finder.Found;
    }

    private sealed class BoundErrorExpressionFinder : BoundTreeWalker
    {
        public bool Found { get; private set; }

        public override void Visit(BoundNode node)
        {
            if (Found || node is null)
                return;

            if (node is BoundErrorExpression)
            {
                Found = true;
                return;
            }

            base.Visit(node);
        }
    }

    private BoundErrorExpression AsErrorExpression(BoundExpression expression)
    {
        return expression as BoundErrorExpression
            ?? new BoundErrorExpression(
                expression.Type ?? Compilation.ErrorTypeSymbol,
                null,
                BoundExpressionReason.OtherError);
    }

    private BoundExpression BindPatternAssignment(
        PatternSyntax patternSyntax,
        BoundExpression right,
        SyntaxNode node,
        SyntaxKind declarationBindingKeywordKind = SyntaxKind.None)
    {
        var valueType = right.Type ?? Compilation.ErrorTypeSymbol;
        var boundPattern = BindPatternForAssignment(patternSyntax, valueType, node, declarationBindingKeywordKind);

        if (boundPattern.Reason == BoundExpressionReason.UnsupportedOperation)
        {
            return ErrorExpression(reason: BoundExpressionReason.UnsupportedOperation);
        }

        var assignmentType = right.Type ?? boundPattern.Type ?? Compilation.ErrorTypeSymbol;
        return BoundFactory.CreatePatternAssignmentExpression(assignmentType, boundPattern, right);
    }

    private string GetPatternTypeDisplay(ITypeSymbol type)
    {
        if (type is LiteralTypeSymbol literal)
            return literal.UnderlyingType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);

        return type.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private void ReportSequenceDeconstructionRequiresSequenceType(ITypeSymbol type, Location location)
        => _diagnostics.Report(Diagnostic.Create(
            CompilerDiagnostics.SequenceDeconstructionRequiresSequenceType,
            location,
            GetPatternTypeDisplay(type)));

    private void ReportDictionaryDeconstructionRequiresDictionaryType(ITypeSymbol type, Location location)
        => _diagnostics.Report(Diagnostic.Create(
            CompilerDiagnostics.DictionaryDeconstructionRequiresDictionaryType,
            location,
            GetPatternTypeDisplay(type)));

    private void ReportDuplicateDictionaryKey(Location location, object? key)
        => _diagnostics.Report(Diagnostic.Create(
            CompilerDiagnostics.DuplicateDictionaryKey,
            location,
            FormatDictionaryKeyForDiagnostic(key)));

    private static string FormatDictionaryKeyForDiagnostic(object? key)
        => key switch
        {
            null => "null",
            string text => text,
            char ch => ch.ToString(),
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => key.ToString() ?? string.Empty
        };

    private void ReportDuplicateDictionaryKeys(
        IEnumerable<(BoundExpression Key, SyntaxNode Syntax)> keyedEntries)
    {
        var seen = new List<(object? Value, Location FirstLocation)>();

        foreach (var (keyExpression, syntax) in keyedEntries)
        {
            if (!TryGetExpressionConstantValue(keyExpression, out var keyValue))
                continue;

            if (seen.Any(existing => Equals(existing.Value, keyValue)))
            {
                ReportDuplicateDictionaryKey(syntax.GetLocation(), keyValue);
                continue;
            }

            seen.Add((keyValue, syntax.GetLocation()));
        }
    }

    private BoundPattern BindPatternForAssignment(
        PatternSyntax patternSyntax,
        ITypeSymbol valueType,
        SyntaxNode node,
        SyntaxKind declarationBindingKeywordKind = SyntaxKind.None)
    {
        valueType ??= Compilation.ErrorTypeSymbol;

        BoundPattern bound = patternSyntax switch
        {
            VariablePatternSyntax variablePattern => BindVariablePatternForAssignment(variablePattern, valueType, declarationBindingKeywordKind),
            PositionalPatternSyntax tuplePattern => BindPositionalPatternForAssignment(tuplePattern, valueType, declarationBindingKeywordKind),
            SequencePatternSyntax sequencePattern => BindSequencePatternForAssignment(sequencePattern, valueType, declarationBindingKeywordKind),
            DictionaryPatternSyntax dictionaryPattern => BindDictionaryPatternForAssignment(dictionaryPattern, valueType, declarationBindingKeywordKind),
            DiscardPatternSyntax => new BoundDiscardPattern(valueType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : valueType),
            ConstantPatternSyntax { Expression: IdentifierNameSyntax identifierName }
                => BindIdentifierPatternForAssignment(identifierName, valueType, declarationBindingKeywordKind),
            DeclarationPatternSyntax declaration => BindDeclarationPatternForAssignment(declaration, valueType, node, declarationBindingKeywordKind),
            _ => Misc(node)
        };

        CacheBoundNode(patternSyntax, bound);
        return bound;
    }

    private BoundPattern Misc(SyntaxNode node)
    {
        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
        return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.UnsupportedOperation);
    }

    private BoundPattern BindVariablePatternForAssignment(
        VariablePatternSyntax pattern,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var bindingKeywordKind = GetPatternAssignmentBindingKeywordKind(pattern, declarationBindingKeywordKind);
        if (!IsDeclarationBindingKeyword(bindingKeywordKind))
        {
            return BindVariableDesignationForExistingAssignment(pattern.Designation, valueType);
        }

        var isMutable = IsMutableBindingKeyword(bindingKeywordKind);
        return BindVariableDesignationForAssignment(pattern.Designation, valueType, isMutable);
    }

    private static SyntaxKind GetPatternAssignmentBindingKeywordKind(
        VariablePatternSyntax pattern,
        SyntaxKind declarationBindingKeywordKind)
    {
        if (IsDeclarationBindingKeyword(pattern.BindingKeyword.Kind))
            return pattern.BindingKeyword.Kind;

        if (pattern.Designation is SingleVariableDesignationSyntax single &&
            IsDeclarationBindingKeyword(single.BindingKeyword.Kind))
        {
            return single.BindingKeyword.Kind;
        }

        return declarationBindingKeywordKind;
    }

    private BoundPattern BindDeclarationPatternForAssignment(
        DeclarationPatternSyntax pattern,
        ITypeSymbol valueType,
        SyntaxNode node,
        SyntaxKind declarationBindingKeywordKind)
    {
        if (pattern.Type is IdentifierNameSyntax identifier &&
            (pattern.Designation is null ||
             pattern.Designation is SingleVariableDesignationSyntax { Identifier.IsMissing: true } ||
             pattern.Designation is SingleVariableDesignationSyntax { Identifier.Kind: SyntaxKind.None }))
        {
            var name = identifier.Identifier.ValueText;

            if (string.IsNullOrEmpty(name))
            {
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
                return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.UnsupportedOperation);
            }

            if (name == "_")
                return new BoundDiscardPattern(valueType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : valueType);

            if (IsDeclarationBindingKeyword(declarationBindingKeywordKind))
            {
                return BindIdentifierTokenForDeclarationAssignment(
                    identifier.Identifier,
                    valueType,
                    IsMutableBindingKeyword(declarationBindingKeywordKind));
            }

            return BindIdentifierPatternForExistingLocalAssignment(identifier.Identifier, valueType);
        }

        _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(node.GetLocation());
        return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.UnsupportedOperation);
    }

    private BoundPattern BindPositionalPatternForAssignment(
        PositionalPatternSyntax pattern,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var elements = pattern.Elements;
        var elementCount = elements.Count;

        if (valueType.TypeKind != TypeKind.Error)
        {
            var deconstructMethod = FindDeconstructMethod(valueType, elementCount);
            if (deconstructMethod is not null)
                return BindDeconstructPatternForAssignment(elements, deconstructMethod, valueType, declarationBindingKeywordKind);
        }

        var elementTypes = ImmutableArray<ITypeSymbol>.Empty;
        if (valueType.TypeKind != TypeKind.Error)
            elementTypes = GetTupleElementTypes(valueType);
        if (elementTypes.IsDefaultOrEmpty && valueType.TypeKind != TypeKind.Error)
            elementTypes = GetPrimaryConstructorDeconstructionElementTypes(valueType);

        if (elementTypes.IsDefaultOrEmpty)
        {
            if (elementCount > 0 && valueType.TypeKind != TypeKind.Error)
            {
                _diagnostics.ReportPositionalDeconstructionRequiresDeconstructableType(
                    GetPatternTypeDisplay(valueType),
                    pattern.GetLocation());
            }

            var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(elementCount);
            for (var i = 0; i < elementCount; i++)
                builder.Add(Compilation.ErrorTypeSymbol);
            elementTypes = builder.ToImmutable();
        }
        else if (elementTypes.Length != elementCount)
        {
            _diagnostics.ReportPositionalDeconstructionElementCountMismatch(
                elementCount,
                elementTypes.Length,
                pattern.GetLocation());

            if (elementTypes.Length < elementCount)
            {
                var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(elementCount);
                builder.AddRange(elementTypes);
                while (builder.Count < elementCount)
                    builder.Add(Compilation.ErrorTypeSymbol);
                elementTypes = builder.ToImmutable();
            }
            else
            {
                elementTypes = elementTypes.Take(elementCount).ToImmutableArray();
            }
        }

        var boundElements = ImmutableArray.CreateBuilder<BoundPattern>(elementCount);

        for (var i = 0; i < elementCount; i++)
        {
            var elementSyntax = elements[i];
            var elementType = elementTypes.Length > i ? elementTypes[i] : Compilation.ErrorTypeSymbol;
            var boundElement = BindPatternForAssignment(
                elementSyntax.Pattern,
                elementType,
                elementSyntax.Pattern,
                declarationBindingKeywordKind);
            boundElements.Add(boundElement);
        }

        var tupleElements = new List<(string? name, ITypeSymbol type)>(boundElements.Count);
        foreach (var element in boundElements)
        {
            var elementType = element.Type ?? Compilation.ErrorTypeSymbol;
            tupleElements.Add((null, elementType));
        }

        var tupleType = Compilation.CreateTupleTypeSymbol(tupleElements);
        return new BoundPositionalPattern(tupleType, boundElements.ToImmutable());
    }

    private BoundPattern BindSequencePatternForAssignment(
        SequencePatternSyntax pattern,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var elements = pattern.Elements;
        var elementCount = elements.Count;
        var boundElements = ImmutableArray.CreateBuilder<BoundPattern>(elementCount);
        var elementType = Compilation.ErrorTypeSymbol;
        var patternType = valueType;
        var (elementWidths, elementKinds, restIndex) = GetSequencePatternLayout(elements);
        if (TryGetSequenceDeconstructionElementType(valueType, out var sequenceElementType))
        {
            elementType = sequenceElementType;
        }
        else if (valueType.TypeKind != TypeKind.Error)
        {
            ReportSequenceDeconstructionRequiresSequenceType(
                valueType,
                pattern.GetLocation());

            patternType = Compilation.ErrorTypeSymbol;
        }

        if (valueType is IArrayTypeSymbol fixedArray && fixedArray.FixedLength is int fixedLength)
        {
            var requiredWidth = elementWidths.Where(width => width > 0).Sum();
            var hasRestSegment = restIndex >= 0;
            var matches = hasRestSegment ? requiredWidth <= fixedLength : requiredWidth == fixedLength;

            if (!matches)
            {
                _diagnostics.ReportPositionalDeconstructionElementCountMismatch(
                    requiredWidth,
                    fixedLength,
                    pattern.GetLocation());
            }
        }

        for (var i = 0; i < elementCount; i++)
        {
            var elementSyntax = elements[i];
            var expectedType = GetSequencePatternElementType(
                valueType,
                elementType,
                elementWidths,
                elementKinds,
                restIndex,
                i);
            var boundElement = BindPatternForAssignment(
                elementSyntax.Pattern,
                expectedType,
                elementSyntax.Pattern,
                declarationBindingKeywordKind);
            boundElements.Add(boundElement);
        }

        return new BoundPositionalPattern(
            patternType,
            boundElements.ToImmutable(),
            restIndex: restIndex,
            elementWidths: elementWidths,
            elementKinds: elementKinds,
            isSequence: true);
    }

    private BoundPattern BindDictionaryPatternForAssignment(
        DictionaryPatternSyntax pattern,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var receiverType = Compilation.ErrorTypeSymbol;
        var keyType = Compilation.ErrorTypeSymbol;
        var elementValueType = Compilation.ErrorTypeSymbol;
        var reason = BoundExpressionReason.None;

        if (valueType is INamedTypeSymbol namedValueType &&
            TryGetDictionaryInterfaceInfo(namedValueType, out var dictionaryReceiverType, out var dictionaryKeyType, out var dictionaryValueType))
        {
            receiverType = dictionaryReceiverType;
            keyType = dictionaryKeyType;
            elementValueType = dictionaryValueType;
        }
        else
        {
            ReportDictionaryDeconstructionRequiresDictionaryType(
                valueType,
                pattern.GetLocation());
            reason = BoundExpressionReason.TypeMismatch;
        }

        var entries = ImmutableArray.CreateBuilder<BoundDictionarySubpattern>(pattern.Entries.Count);

        foreach (var entrySyntax in pattern.Entries)
        {
            var key = keyType.TypeKind == TypeKind.Error
                ? BindExpression(entrySyntax.Key)
                : BindExpressionWithTargetType(entrySyntax.Key, keyType);

            if (keyType.TypeKind != TypeKind.Error &&
                key.Type?.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(key))
            {
                if (IsAssignable(keyType, key.Type!, out var keyConversion))
                    key = ApplyConversion(key, keyType, keyConversion, entrySyntax.Key);
                else
                    ReportCannotConvertFromTypeToType(
                        key.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        keyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        entrySyntax.Key.GetLocation());
            }

            var boundPattern = BindPatternForAssignment(
                entrySyntax.Pattern,
                elementValueType,
                entrySyntax.Pattern,
                declarationBindingKeywordKind);
            entries.Add(new BoundDictionarySubpattern(key, boundPattern));
        }

        ReportDuplicateDictionaryKeys(pattern.Entries.Select((entry, index) => (entries[index].Key, (SyntaxNode)entry.Key)));

        return new BoundDictionaryPattern(
            valueType,
            receiverType,
            keyType,
            elementValueType,
            designator: null,
            entries: entries.ToImmutable(),
            reason: reason);
    }

    private bool TryGetSequenceDeconstructionElementType(ITypeSymbol valueType, out ITypeSymbol elementType)
    {
        elementType = Compilation.ErrorTypeSymbol;

        if (valueType.TypeKind == TypeKind.Error)
            return false;

        if (valueType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (valueType.SpecialType == SpecialType.System_String)
        {
            elementType = Compilation.GetSpecialType(SpecialType.System_Char);
            return true;
        }

        if (valueType is not INamedTypeSymbol namedType)
            return false;

        foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
        {
            if (TryGetIndexableElementType(candidate, out var indexerElementType))
            {
                elementType = indexerElementType;
                return true;
            }
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }

        static bool TryGetIndexableElementType(INamedTypeSymbol type, out ITypeSymbol indexerElementType)
        {
            indexerElementType = default!;

            var hasLength = type
                .GetMembers("Length")
                .Concat(type.GetMembers("Count"))
                .OfType<IPropertySymbol>()
                .Any(static property =>
                    property.Name is "Count" or "Length" &&
                    property.Parameters.Length == 0 &&
                    property.Type.SpecialType == SpecialType.System_Int32 &&
                    property.GetMethod is { DeclaredAccessibility: Accessibility.Public });

            if (!hasLength)
                return false;

            var indexer = type
                .GetMembers("Item")
                .OfType<IPropertySymbol>()
                .FirstOrDefault(static property =>
                    property.Name == "Item" &&
                    property.Parameters.Length == 1 &&
                    property.Parameters[0].Type.SpecialType == SpecialType.System_Int32 &&
                    property.GetMethod is { DeclaredAccessibility: Accessibility.Public });

            if (indexer is null)
                return false;

            indexerElementType = indexer.Type;
            return true;
        }

    }

    private ITypeSymbol GetSequenceSliceType(ITypeSymbol valueType, ITypeSymbol elementType, int? fixedLength = null)
    {
        if (valueType.SpecialType == SpecialType.System_String)
            return Compilation.GetSpecialType(SpecialType.System_String);

        if (valueType is IArrayTypeSymbol)
            return Compilation.CreateArrayTypeSymbol(elementType, fixedLength: fixedLength);

        if (valueType is INamedTypeSymbol namedType &&
            TryCreatePreservedSequenceSliceType(namedType, elementType, out var preservedType))
        {
            return preservedType;
        }

        return Compilation.CreateArrayTypeSymbol(elementType, fixedLength: fixedLength);
    }

    private bool TryCreatePreservedSequenceSliceType(
        INamedTypeSymbol valueType,
        ITypeSymbol elementType,
        out ITypeSymbol sliceType)
    {
        sliceType = default!;

        if (valueType.OriginalDefinition is not INamedTypeSymbol originalDefinition)
            return false;

        var metadataName = originalDefinition.ToFullyQualifiedMetadataName();

        if (string.Equals(metadataName, "System.Collections.Generic.List`1", StringComparison.Ordinal) ||
            string.Equals(metadataName, "System.Collections.Immutable.ImmutableList`1", StringComparison.Ordinal) ||
            string.Equals(metadataName, "System.Collections.Immutable.ImmutableArray`1", StringComparison.Ordinal))
        {
            sliceType = originalDefinition.Construct(elementType);
            return true;
        }

        return false;
    }

    private ITypeSymbol GetSequencePatternElementType(
        ITypeSymbol valueType,
        ITypeSymbol elementType,
        ImmutableArray<int> elementWidths,
        ImmutableArray<BoundPositionalPattern.SequenceElementKind> elementKinds,
        int restIndex,
        int elementIndex)
    {
        if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.Single)
            return elementType;

        if (valueType is IArrayTypeSymbol arrayType && arrayType.FixedLength is int sourceFixedLength)
        {
            if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.FixedSegment)
                return GetSequenceSliceType(valueType, elementType, elementWidths[elementIndex]);

            if (elementKinds[elementIndex] == BoundPositionalPattern.SequenceElementKind.RestSegment && restIndex >= 0)
            {
                var fixedWidth = elementWidths.Where(width => width > 0).Sum();
                var restWidth = sourceFixedLength - fixedWidth;
                if (restWidth >= 0)
                    return GetSequenceSliceType(valueType, elementType, restWidth);
            }
        }

        return GetSequenceSliceType(valueType, elementType);
    }

    private static int GetSequenceRestElementIndex(SeparatedSyntaxList<SequencePatternElementSyntax> elements)
    {
        var index = -1;
        for (var i = 0; i < elements.Count; i++)
        {
            if (!IsSequenceRestElement(elements[i]))
                continue;

            if (index >= 0)
                return index;

            index = i;
        }

        return index;
    }

    private static (ImmutableArray<int> Widths, ImmutableArray<BoundPositionalPattern.SequenceElementKind> Kinds, int RestIndex)
        GetSequencePatternLayout(SeparatedSyntaxList<SequencePatternElementSyntax> elements)
    {
        var widths = ImmutableArray.CreateBuilder<int>(elements.Count);
        var kinds = ImmutableArray.CreateBuilder<BoundPositionalPattern.SequenceElementKind>(elements.Count);
        var restIndex = -1;

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var prefix = element.Prefix;
            var dotDotKind = prefix.DotDotToken.Kind;
            if (dotDotKind is not SyntaxKind.DotDotToken and not SyntaxKind.DotDotDotToken)
            {
                widths.Add(1);
                kinds.Add(BoundPositionalPattern.SequenceElementKind.Single);
                continue;
            }

            if (prefix.SegmentLengthToken.Kind == SyntaxKind.NumericLiteralToken)
            {
                var width = int.TryParse(prefix.SegmentLengthToken.Text, out var parsedWidth)
                    ? parsedWidth
                    : 0;
                widths.Add(width);
                kinds.Add(BoundPositionalPattern.SequenceElementKind.FixedSegment);
                continue;
            }

            widths.Add(-1);
            kinds.Add(BoundPositionalPattern.SequenceElementKind.RestSegment);
            if (restIndex < 0)
                restIndex = i;
        }

        return (widths.ToImmutable(), kinds.ToImmutable(), restIndex);
    }

    private static bool IsSequenceRestElement(SequencePatternElementSyntax element)
        => element.Prefix.DotDotToken.Kind is SyntaxKind.DotDotToken or SyntaxKind.DotDotDotToken;

    private BoundPattern BindIdentifierPatternForAssignment(
        IdentifierNameSyntax identifierName,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        if (IsDeclarationBindingKeyword(declarationBindingKeywordKind))
            return BindIdentifierPatternForDeclarationAssignment(identifierName, valueType, declarationBindingKeywordKind);

        var name = identifierName.Identifier.ValueText;
        if (string.IsNullOrEmpty(name) || name == "_")
            return new BoundDiscardPattern(valueType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : valueType);

        return BindIdentifierPatternForExistingLocalAssignment(identifierName.Identifier, valueType);
    }

    private BoundPattern BindIdentifierPatternForDeclarationAssignment(
        IdentifierNameSyntax identifierName,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var name = identifierName.Identifier.ValueText;
        if (string.IsNullOrEmpty(name) || name == "_")
            return new BoundDiscardPattern(valueType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : valueType);

        return BindIdentifierTokenForDeclarationAssignment(
            identifierName.Identifier,
            valueType,
            IsMutableBindingKeyword(declarationBindingKeywordKind));
    }

    private BoundPattern BindIdentifierPatternForExistingLocalAssignment(
        SyntaxToken identifier,
        ITypeSymbol valueType)
    {
        var name = identifier.ValueText;

        ILocalSymbol? local = null;
        if (_locals.TryGetValue(name, out var existingLocal))
        {
            local = existingLocal.Symbol;
        }
        else if (LookupSymbol(name) is ILocalSymbol scopedLocal)
        {
            local = scopedLocal;
        }

        if (local is null)
        {
            _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(name, identifier.GetLocation());
            return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.NotFound);
        }

        if (!local.IsMutable)
        {
            _diagnostics.ReportThisValueIsNotMutable(identifier.GetLocation());
            return new BoundDeclarationPattern(
                local.Type ?? Compilation.ErrorTypeSymbol,
                new BoundSingleVariableDesignator(local),
                BoundExpressionReason.UnsupportedOperation);
        }

        var targetType = local.Type ?? Compilation.ErrorTypeSymbol;
        var sourceType = valueType.UnwrapLiteralType() ?? valueType;

        if (targetType.TypeKind != TypeKind.Error &&
            sourceType.TypeKind != TypeKind.Error &&
            !IsAssignable(targetType, sourceType, out _))
        {
            ReportCannotAssignFromTypeToType(sourceType, targetType, identifier.GetLocation());

            return new BoundDeclarationPattern(
                targetType,
                new BoundSingleVariableDesignator(local),
                BoundExpressionReason.TypeMismatch);
        }

        var designator = new BoundSingleVariableDesignator(local);
        if (identifier.Parent is SingleVariableDesignationSyntax single)
            CacheBoundNode(single, designator);

        return new BoundDeclarationPattern(targetType, designator);
    }

    private BoundPattern BindIdentifierTokenForDeclarationAssignment(
        SyntaxToken identifier,
        ITypeSymbol valueType,
        bool isMutable)
    {
        var normalizedType = TypeSymbolNormalization.NormalizeForInference(valueType);
        if (identifier.IsMissing || identifier.ValueText == "_")
            return new BoundDiscardPattern(normalizedType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : normalizedType);

        var type = normalizedType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : normalizedType;
        type = EnsureTypeAccessible(type, identifier.GetLocation());

        var local = GetOrCreatePatternLocal(identifier.Parent ?? throw new InvalidOperationException("Identifier token must have a parent syntax node."), identifier.ValueText, isMutable, type);
        var designator = new BoundSingleVariableDesignator(local);
        if (identifier.Parent is SingleVariableDesignationSyntax single)
            CacheBoundNode(single, designator);

        return new BoundDeclarationPattern(type, designator);
    }

    private static bool IsDeclarationBindingKeyword(SyntaxKind kind)
        => kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword;

    private static bool IsMutableBindingKeyword(SyntaxKind kind)
        => kind == SyntaxKind.VarKeyword;

    private BoundPattern BindVariableDesignationForAssignment(
        VariableDesignationSyntax designation,
        ITypeSymbol valueType,
        bool isMutable)
    {
        valueType ??= Compilation.ErrorTypeSymbol;

        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                return BindSingleVariableDesignationForAssignment(single, valueType, isMutable);
            case ParenthesizedVariableDesignationSyntax parenthesized:
                return BindParenthesizedDesignationForAssignment(parenthesized, valueType, isMutable);
            case TypedVariableDesignationSyntax typed:
                return BindTypedDesignationForAssignment(typed, valueType, isMutable);
            default:
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(designation.GetLocation());
                return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.UnsupportedOperation);
        }
    }

    private BoundPattern BindVariableDesignationForExistingAssignment(
        VariableDesignationSyntax designation,
        ITypeSymbol valueType)
    {
        valueType ??= Compilation.ErrorTypeSymbol;

        switch (designation)
        {
            case SingleVariableDesignationSyntax single:
                return BindIdentifierPatternForExistingLocalAssignment(single.Identifier, valueType);
            case TypedVariableDesignationSyntax typed:
                {
                    ReportTypedPatternBindingsMissingKeyword(typed, hasAmbientBindingKeyword: false);

                    var declaredType = ResolveTypeSyntaxOrError(typed.TypeAnnotation.Type);
                    declaredType = EnsureTypeAccessible(declaredType, typed.TypeAnnotation.Type.GetLocation());
                    declaredType = EnsureTypeValidForStorageLocation(declaredType, typed.TypeAnnotation.Type.GetLocation());

                    var sourceType = valueType.UnwrapLiteralType() ?? valueType;
                    if (declaredType.TypeKind != TypeKind.Error &&
                        sourceType.TypeKind != TypeKind.Error &&
                        !IsAssignable(declaredType, sourceType, out _))
                    {
                        ReportCannotAssignFromTypeToType(sourceType, declaredType, typed.TypeAnnotation.Type.GetLocation());

                        return new BoundDiscardPattern(declaredType, BoundExpressionReason.TypeMismatch);
                    }

                    return BindVariableDesignationForExistingAssignment(typed.Designation, declaredType);
                }
            default:
                _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(designation.GetLocation());
                return new BoundDiscardPattern(Compilation.ErrorTypeSymbol, BoundExpressionReason.UnsupportedOperation);
        }
    }

    private BoundPattern BindTypedDesignationForAssignment(
        TypedVariableDesignationSyntax typedDesignation,
        ITypeSymbol incomingType,
        bool isMutable)
    {
        ReportTypedPatternBindingsMissingKeyword(typedDesignation, hasAmbientBindingKeyword: true);

        var declaredType = ResolveTypeSyntaxOrError(typedDesignation.TypeAnnotation.Type);
        declaredType = EnsureTypeAccessible(declaredType, typedDesignation.TypeAnnotation.Type.GetLocation());
        declaredType = EnsureTypeValidForStorageLocation(declaredType, typedDesignation.TypeAnnotation.Type.GetLocation());

        if (incomingType.TypeKind != TypeKind.Error &&
            declaredType.TypeKind != TypeKind.Error &&
            !IsAssignable(declaredType, incomingType, out _))
        {
            ReportCannotAssignFromTypeToType(incomingType, declaredType, typedDesignation.TypeAnnotation.Type.GetLocation());

            declaredType = Compilation.ErrorTypeSymbol;
        }

        return BindVariableDesignationForAssignment(typedDesignation.Designation, declaredType, isMutable);
    }

    private static bool RequiresByRefType(TypeSyntax typeSyntax, RefKind refKindHint)
        => refKindHint is RefKind.Ref or RefKind.Out or RefKind.In or RefKind.RefReadOnly or RefKind.RefReadOnlyParameter ||
           typeSyntax is ByRefTypeSyntax;

    private ITypeSymbol ResolveTypeSyntaxOrError(TypeSyntax typeSyntax, RefKind refKindHint = RefKind.None)
    {
        return BindTypeSyntaxAndReport(typeSyntax, refKindHint: refKindHint);
    }

    private BoundPattern BindSingleVariableDesignationForAssignment(
        SingleVariableDesignationSyntax single,
        ITypeSymbol valueType,
        bool isMutable)
    {
        var identifier = single.Identifier;

        var normalizedType = TypeSymbolNormalization.NormalizeForInference(valueType);
        if (identifier.IsMissing || identifier.ValueText == "_")
            return new BoundDiscardPattern(normalizedType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : normalizedType);

        var type = normalizedType.TypeKind == TypeKind.Error ? Compilation.ErrorTypeSymbol : normalizedType;
        type = EnsureTypeAccessible(type, identifier.GetLocation());

        var local = GetOrCreatePatternLocal(single, identifier.ValueText, isMutable, type);
        var designator = new BoundSingleVariableDesignator(local);
        CacheBoundNode(single, designator);

        return new BoundDeclarationPattern(type, designator);
    }

    private BoundPattern BindParenthesizedDesignationForAssignment(
        ParenthesizedVariableDesignationSyntax parenthesized,
        ITypeSymbol valueType,
        bool isMutable)
    {
        var variables = parenthesized.Variables;
        var elementCount = variables.Count;

        if (valueType.TypeKind != TypeKind.Error)
        {
            var deconstructMethod = FindDeconstructMethod(valueType, elementCount);
            if (deconstructMethod is not null)
                return BindDeconstructPatternForAssignment(variables, deconstructMethod, valueType, isMutable);
        }

        var elementTypes = ImmutableArray<ITypeSymbol>.Empty;
        if (valueType.TypeKind != TypeKind.Error)
            elementTypes = GetTupleElementTypes(valueType);
        if (elementTypes.IsDefaultOrEmpty && valueType.TypeKind != TypeKind.Error)
            elementTypes = GetPrimaryConstructorDeconstructionElementTypes(valueType);

        if (elementTypes.IsDefaultOrEmpty)
        {
            if (elementCount > 0 && valueType.TypeKind != TypeKind.Error)
            {
                _diagnostics.ReportPositionalDeconstructionRequiresDeconstructableType(
                    GetPatternTypeDisplay(valueType),
                    parenthesized.GetLocation());
            }

            var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(elementCount);
            for (var i = 0; i < elementCount; i++)
                builder.Add(Compilation.ErrorTypeSymbol);
            elementTypes = builder.ToImmutable();
        }
        else if (elementTypes.Length != elementCount)
        {
            _diagnostics.ReportPositionalDeconstructionElementCountMismatch(
                elementCount,
                elementTypes.Length,
                parenthesized.GetLocation());

            if (elementTypes.Length < elementCount)
            {
                var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(elementCount);
                builder.AddRange(elementTypes);
                while (builder.Count < elementCount)
                    builder.Add(Compilation.ErrorTypeSymbol);
                elementTypes = builder.ToImmutable();
            }
            else
            {
                elementTypes = elementTypes.Take(elementCount).ToImmutableArray();
            }
        }

        var boundElements = ImmutableArray.CreateBuilder<BoundPattern>(elementCount);

        for (var i = 0; i < elementCount; i++)
        {
            var variable = variables[i];
            var elementType = elementTypes.Length > i ? elementTypes[i] : Compilation.ErrorTypeSymbol;
            var boundElement = BindVariableDesignationForAssignment(variable, elementType, isMutable);
            boundElements.Add(boundElement);
        }

        var tupleElements = new List<(string? name, ITypeSymbol type)>(boundElements.Count);
        foreach (var element in boundElements)
        {
            var elementType = element.Type ?? Compilation.ErrorTypeSymbol;
            tupleElements.Add((null, elementType));
        }

        var tupleType = Compilation.CreateTupleTypeSymbol(tupleElements);
        return new BoundPositionalPattern(tupleType, boundElements.ToImmutable());
    }

    private BoundPattern BindDeconstructPatternForAssignment(
        SeparatedSyntaxList<PositionalPatternElementSyntax> elements,
        IMethodSymbol deconstructMethod,
        ITypeSymbol valueType,
        SyntaxKind declarationBindingKeywordKind)
    {
        var fallbackLocation = elements.Count > 0 ? elements[0].GetLocation() : Location.None;
        var parameterOffset = GetDeconstructParameterOffset(deconstructMethod);
        var parameters = deconstructMethod.Parameters;
        var parameterCount = parameters.Length - parameterOffset;
        var receiverType = GetDeconstructReceiverType(deconstructMethod);
        var boundElements = new BoundPattern?[parameterCount];
        var matchedParameters = new bool[parameterCount];
        var nextUnnamedParameter = 0;

        for (var i = 0; i < elements.Count; i++)
        {
            var elementSyntax = elements[i];
            var parameterIndex = -1;

            if (TryGetPositionalPatternElementName(elementSyntax, out var elementName))
            {
                parameterIndex = FindDeconstructParameterIndex(parameters, parameterOffset, elementName);
                if (parameterIndex < 0)
                {
                    _diagnostics.ReportPropertyPatternMemberNotFound(
                        elementName,
                        receiverType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        elementSyntax.NameColon!.Name.GetLocation());

                    _ = BindPatternForAssignment(
                        elementSyntax.Pattern,
                        Compilation.ErrorTypeSymbol,
                        elementSyntax.Pattern,
                        declarationBindingKeywordKind);
                    continue;
                }

                if (matchedParameters[parameterIndex])
                {
                    _diagnostics.Report(Diagnostic.Create(
                        CompilerDiagnostics.DuplicatePropertyPatternMember,
                        elementSyntax.NameColon!.Name.GetLocation(),
                        elementName));

                    var duplicateType = EnsureTypeAccessible(parameters[parameterIndex + parameterOffset].Type, elementSyntax.GetLocation());
                    _ = BindPatternForAssignment(
                        elementSyntax.Pattern,
                        duplicateType,
                        elementSyntax.Pattern,
                        declarationBindingKeywordKind);
                    continue;
                }
            }
            else
            {
                while (nextUnnamedParameter < parameterCount && matchedParameters[nextUnnamedParameter])
                    nextUnnamedParameter++;

                if (nextUnnamedParameter >= parameterCount)
                {
                    _ = BindPatternForAssignment(
                        elementSyntax.Pattern,
                        Compilation.ErrorTypeSymbol,
                        elementSyntax.Pattern,
                        declarationBindingKeywordKind);
                    continue;
                }

                parameterIndex = nextUnnamedParameter++;
            }

            var expectedType = EnsureTypeAccessible(parameters[parameterIndex + parameterOffset].Type, elementSyntax.GetLocation());
            boundElements[parameterIndex] = BindPatternForAssignment(
                elementSyntax.Pattern,
                expectedType,
                elementSyntax.Pattern,
                declarationBindingKeywordKind);
            matchedParameters[parameterIndex] = true;
        }

        var orderedElements = ImmutableArray.CreateBuilder<BoundPattern>(parameterCount);
        for (var i = 0; i < parameterCount; i++)
        {
            orderedElements.Add(
                boundElements[i] ??
                new BoundDiscardPattern(
                    EnsureTypeAccessible(parameters[i + parameterOffset].Type, fallbackLocation),
                    BoundExpressionReason.TypeMismatch));
        }

        return new BoundDeconstructPattern(
            inputType: valueType,
            receiverType: GetDeconstructReceiverType(deconstructMethod),
            narrowedType: null,
            deconstructMethod: deconstructMethod,
            arguments: orderedElements.ToImmutable());
    }

    private BoundPattern BindDeconstructPatternForAssignment(
        SeparatedSyntaxList<VariableDesignationSyntax> designations,
        IMethodSymbol deconstructMethod,
        ITypeSymbol valueType,
        bool isMutable)
    {
        var fallbackLocation = designations.Count > 0 ? designations[0].GetLocation() : Location.None;
        var parameterOffset = GetDeconstructParameterOffset(deconstructMethod);
        var parameters = deconstructMethod.Parameters;
        var parameterCount = parameters.Length - parameterOffset;
        var boundElements = ImmutableArray.CreateBuilder<BoundPattern>(parameterCount);
        var elementCount = Math.Min(designations.Count, parameterCount);

        for (var i = 0; i < elementCount; i++)
        {
            var variable = designations[i];
            var expectedType = EnsureTypeAccessible(parameters[i + parameterOffset].Type, variable.GetLocation());
            var boundElement = BindVariableDesignationForAssignment(variable, expectedType, isMutable);
            boundElements.Add(boundElement);
        }

        for (var i = elementCount; i < parameterCount; i++)
        {
            var parameterType = EnsureTypeAccessible(parameters[i + parameterOffset].Type, fallbackLocation);
            boundElements.Add(new BoundDiscardPattern(parameterType, BoundExpressionReason.TypeMismatch));
        }

        for (var i = elementCount; i < designations.Count; i++)
            _ = BindVariableDesignationForAssignment(designations[i], Compilation.ErrorTypeSymbol, isMutable);

        return new BoundDeconstructPattern(
            inputType: valueType,
            receiverType: GetDeconstructReceiverType(deconstructMethod),
            narrowedType: null,
            deconstructMethod: deconstructMethod,
            arguments: boundElements.ToImmutable());
    }

    private ILocalSymbol GetOrCreatePatternLocal(
        SyntaxNode designationSyntax,
        string name,
        bool isMutable,
        ITypeSymbol type)
    {
        if (TryGetDeclaredLocal(designationSyntax, out var declaredLocal))
        {
            RegisterLocalForCurrentLookup(name, declaredLocal);
            return declaredLocal;
        }

        var isShadowingExistingInScope = false;

        if (_locals.TryGetValue(name, out var existing) && existing.Depth == _scopeDepth)
        {
            var existingSyntax = existing.Symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (existingSyntax?.Parent == designationSyntax.Parent)
            {
                _diagnostics.ReportVariableAlreadyDefined(name, designationSyntax.GetLocation());
                return existing.Symbol;
            }

            isShadowingExistingInScope = true;
        }

        if (isShadowingExistingInScope)
            _diagnostics.ReportVariableShadowsPreviousDeclaration(name, designationSyntax.GetLocation());

        return CreateLocalSymbol(designationSyntax, name, isMutable, type);
    }

    private bool TryGetWritableAutoPropertyBackingField(
        IPropertySymbol propertySymbol,
        BoundExpression left,
        out SourceFieldSymbol? backingField)
    {
        backingField = null;

        if (propertySymbol is not SourcePropertySymbol sourceProperty ||
            !sourceProperty.IsAutoProperty ||
            sourceProperty.BackingField is null)
        {
            return false;
        }

        if (_containingSymbol is not IMethodSymbol methodSymbol ||
            !methodSymbol.IsConstructor)
        {
            return false;
        }

        if (!SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, propertySymbol.ContainingType))
        {
            return false;
        }

        if (propertySymbol.IsStatic)
        {
            return false;
        }

        if (!IsConstructorSelfReceiver(methodSymbol, GetReceiver(left)))
        {
            return false;
        }

        backingField = sourceProperty.BackingField;
        return true;
    }

    private static bool TryGetFieldOnlyPropertyBackingField(
        IPropertySymbol propertySymbol,
        out SourceFieldSymbol? backingField)
    {
        backingField = null;

        if (propertySymbol is not SourcePropertySymbol &&
            propertySymbol.UnderlyingSymbol is SourcePropertySymbol underlying)
        {
            propertySymbol = underlying;
        }

        if (propertySymbol is not SourcePropertySymbol sourceProperty ||
            sourceProperty.BackingField is null)
        {
            return false;
        }

        if (!sourceProperty.EmitAsFieldOnly)
            return false;

        backingField = sourceProperty.BackingField;
        return true;
    }

    private static bool IsConstructorSelfReceiver(IMethodSymbol methodSymbol, BoundExpression? receiver)
    {
        if (receiver is null)
            return true;

        if (receiver is BoundSelfExpression)
            return true;

        return false;
    }

    private BoundExpression? GetReceiver(BoundExpression left)
    {
        // Unwrap receive from member access

        if (left is BoundMemberAccessExpression pa)
            return pa.Receiver;

        if (left is BoundPropertyAccess or BoundFieldAccess)
            return null;

        return left;
    }

    private BoundExpression BindExpressionAllowingEvent(ExpressionSyntax syntax)
    {
        var bound = syntax switch
        {
            IdentifierNameSyntax identifier => BindIdentifierName(
                identifier,
                allowEventAccess: true,
                applyFlowNarrowing: false),
            MemberAccessExpressionSyntax memberAccess => BindMemberAccessExpression(memberAccess, allowEventAccess: true),
            MemberBindingExpressionSyntax memberBinding => BindMemberBindingExpression(memberBinding, allowEventAccess: true),
            _ => BindExpression(syntax)
        };

        CacheBoundNode(syntax, bound);
        return bound;
    }

    private bool IsWithinEventContainingType(IEventSymbol eventSymbol)
    {
        var containingType = _containingSymbol switch
        {
            IMethodSymbol methodSymbol => methodSymbol.ContainingType,
            INamedTypeSymbol typeSymbol => typeSymbol,
            _ => null
        };

        return containingType is not null &&
            SymbolEqualityComparer.Default.Equals(containingType, eventSymbol.ContainingType);
    }

    private static bool TryGetEventBackingField(IEventSymbol eventSymbol, out SourceFieldSymbol? backingField)
    {
        backingField = eventSymbol is SourceEventSymbol sourceEvent ? sourceEvent.BackingField : null;
        return backingField is not null;
    }

    private BoundExpression BindEventInvocationReceiver(
        IEventSymbol eventSymbol,
        BoundMemberAccessExpression eventAccess,
        SyntaxNode syntax)
    {
        if (!IsWithinEventContainingType(eventSymbol))
        {
            _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(eventSymbol.Name, syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        if (!TryGetEventBackingField(eventSymbol, out var backingField))
        {
            _diagnostics.ReportEventCannotBeInvoked(eventSymbol.Name, syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.NotFound);
        }

        return new BoundFieldAccess(eventAccess.Receiver, backingField);
    }

    private BoundAssignmentExpression CreateFieldAssignmentExpression(
        BoundExpression? receiver,
        IFieldSymbol fieldSymbol,
        BoundExpression value)
    {
        return BoundFactory.CreateFieldAssignmentExpression(receiver, fieldSymbol, value);
    }

    private bool CanAssignToField(IFieldSymbol fieldSymbol, BoundExpression? receiver, SyntaxNode syntax)
    {
        if (!fieldSymbol.IsReadOnly)
            return true;

        if (_containingSymbol is IMethodSymbol methodSymbol)
        {
            if (fieldSymbol.IsStatic)
            {
                if (methodSymbol.MethodKind == MethodKind.StaticConstructor &&
                    SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, fieldSymbol.ContainingType))
                {
                    return true;
                }
            }
            else if (methodSymbol.MethodKind == MethodKind.Constructor &&
                     !methodSymbol.IsStatic &&
                     SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, fieldSymbol.ContainingType) &&
                     IsConstructorSelfReceiver(methodSymbol, receiver))
            {
                return true;
            }
        }

        _diagnostics.ReportReadOnlyFieldCannotBeAssignedTo(syntax.GetLocation());
        return false;
    }

    protected bool IsAssignable(ITypeSymbol targetType, ITypeSymbol sourceType, out Conversion conversion)
    {
        if (targetType.ContainsErrorType() || sourceType.ContainsErrorType())
        {
            conversion = new Conversion(isImplicit: true, isIdentity: true);
            return true;
        }

        conversion = Compilation.ClassifyConversion(sourceType, targetType);
        return conversion.Exists && conversion.IsImplicit;
    }

    protected bool IsAssignable(ITypeSymbol targetType, BoundExpression sourceExpression, out Conversion conversion)
    {
        var sourceType = sourceExpression.Type!;

        return IsAssignable(targetType, sourceType, out conversion);
    }

    private bool TryClassifyParameterArgument(
        ITypeSymbol parameterType,
        ITypeSymbol argumentType,
        out ITypeSymbol targetType,
        out Conversion conversion)
    {
        if (IsAssignable(parameterType, argumentType, out conversion))
        {
            targetType = parameterType;
            return true;
        }

        if (parameterType is NullableTypeSymbol { UnderlyingType.IsValueType: false } nullableParameterType &&
            TryClassifyNullableReferenceUnderlyingArgument(nullableParameterType.UnderlyingType, argumentType, out conversion))
        {
            targetType = nullableParameterType.UnderlyingType;
            return true;
        }

        targetType = parameterType;
        return false;
    }

    private bool TryClassifyNullableReferenceUnderlyingArgument(
        ITypeSymbol underlyingParameterType,
        ITypeSymbol argumentType,
        out Conversion conversion)
    {
        if (IsAssignable(underlyingParameterType, argumentType, out conversion))
            return true;

        var plainArgumentType = argumentType.GetNonNullableType();
        if (SymbolEqualityComparer.Default.Equals(underlyingParameterType, plainArgumentType))
        {
            conversion = new Conversion(isImplicit: true, isIdentity: true);
            return true;
        }

        return false;
    }

    private static bool ShouldAttemptConversion(BoundExpression expression)
    {
        return expression is BoundMethodGroupExpression ||
            expression.Type is { } type && !type.ContainsErrorType();
    }

    private BoundExpression BindLambdaToDelegateIfNeeded(BoundExpression expression, ITypeSymbol targetType)
    {
        if (targetType is NullableTypeSymbol nullableTargetType)
            targetType = nullableTargetType.UnderlyingType;

        var lambda = expression as BoundFunctionExpression;
        if (lambda is null &&
            expression is BoundConversionExpression { Expression: BoundFunctionExpression convertedLambda, IsExplicit: false })
        {
            lambda = convertedLambda;
        }

        if (lambda is null)
            return expression;

        if (TryGetExpressionTreeDelegateType(targetType, out var expressionTreeDelegate))
        {
            // Stage 1: shape the lambda to the inner delegate signature so expression-tree
            // lowering can consume stable parameter/return metadata in follow-up stages.
            var replayed = ReplayLambda(lambda, expressionTreeDelegate);
            var lambdaForTree = replayed;
            if (lambdaForTree is null)
            {
                // Function-type-shaped lambdas can be compatible without symbol identity.
                // Preserve existing lambda binding and still mark the expression-tree conversion.
                if (!lambda.IsCompatibleWithDelegate(expressionTreeDelegate, Compilation))
                    return expression;

                lambdaForTree = lambda;
            }

            if (lambdaForTree.Symbol is SourceLambdaSymbol sourceLambda)
                sourceLambda.MarkExpressionTreeLambda();

            return new BoundConversionExpression(
                lambdaForTree,
                targetType,
                new Conversion(isImplicit: true, isReference: true));
        }

        if (targetType is not INamedTypeSymbol delegateType || delegateType.TypeKind != TypeKind.Delegate)
            return expression;

        return ReplayLambda(lambda, delegateType) ?? expression;
    }

    private ITypeSymbol GetIndexType() =>
        Compilation.GetTypeByMetadataName("System.Index") ?? Compilation.ErrorTypeSymbol;

    private ITypeSymbol GetRangeType() =>
        Compilation.GetTypeByMetadataName("System.Range") ?? Compilation.ErrorTypeSymbol;

    private BoundExpression ApplyConversion(BoundExpression expression, ITypeSymbol targetType, Conversion conversion, SyntaxNode? syntax = null)
    {
        if (!conversion.Exists || expression is BoundErrorExpression)
            return expression;

        if (targetType.TypeKind == TypeKind.Error)
            return expression;

        if (expression is BoundMethodGroupExpression methodGroup)
        {
            return ConvertMethodGroupToDelegate(methodGroup, targetType, syntax);
        }

        if (conversion.IsIdentity)
        {
            if (expression is BoundLiteralExpression literal &&
                literal.Kind == BoundLiteralExpressionKind.NullLiteral &&
                !SymbolEqualityComparer.Default.Equals(literal.GetConvertedType(), targetType))
            {
                return new BoundLiteralExpression(literal.Kind, literal.Value, literal.Type, targetType);
            }

            return expression;
        }

        if (expression is BoundLiteralExpression literalNull &&
            literalNull.Kind == BoundLiteralExpressionKind.NullLiteral)
        {
            return new BoundLiteralExpression(literalNull.Kind, literalNull.Value, literalNull.Type, targetType);
        }

        return new BoundConversionExpression(expression, targetType, conversion);
    }

    private static string GetMethodGroupDisplay(BoundMethodGroupExpression methodGroup)
    {
        var method = methodGroup.Methods[0];
        var containingType = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var parameterTypes = method.Parameters
            .Select(p => p.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToArray();
        var parameterList = string.Join(", ", parameterTypes);
        var name = containingType is null or { Length: 0 }
            ? method.Name
            : $"{containingType}.{method.Name}";

        return parameterTypes.Length == 0 ? name : $"{name}({parameterList})";
    }

    private BoundMethodGroupExpression ReportMethodGroupRequiresDelegate(BoundMethodGroupExpression methodGroup, SyntaxNode syntax)
    {
        _diagnostics.ReportMethodGroupRequiresDelegateType(GetMethodGroupDisplay(methodGroup), syntax.GetLocation());

        if (methodGroup.Reason != BoundExpressionReason.None)
            return methodGroup;

        return new BoundMethodGroupExpression(
            methodGroup.Receiver,
            methodGroup.Methods,
            methodGroup.MethodGroupType,
            methodGroup.DelegateTypeFactory,
            methodGroup.SelectedMethod,
            BoundExpressionReason.OverloadResolutionFailed);
    }

    protected BoundExpression[] ConvertArguments(
        ImmutableArray<IParameterSymbol> parameters,
        IReadOnlyList<BoundArgument> arguments,
        SyntaxNode? callSyntax = null)
    {
        var converted = new BoundExpression[parameters.Length];
        BoundArgument[] paramsArguments = Array.Empty<BoundArgument>();
        var paramsParameterIndex = -1;

        if (!OverloadResolver.TryMapArguments(parameters, arguments, treatAsExtension: false, out var mappedArguments, out paramsArguments, out paramsParameterIndex))
        {
            mappedArguments = new BoundArgument?[parameters.Length];

            var limit = Math.Min(arguments.Count, parameters.Length);
            for (int i = 0; i < limit; i++)
            {
                mappedArguments[i] = arguments[i];
            }
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (i == paramsParameterIndex)
            {
                var mappedParamsArgument = mappedArguments[i];
                if (mappedParamsArgument is not null)
                {
                    // Explicit params-array argument (normal form): convert as a regular parameter.
                }
                else
                {
                    if (!TryGetVarParamsElementType(parameter.Type, out var elementType))
                    {
                        converted[i] = new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.ArgumentBindingFailed);
                        continue;
                    }

                    if (paramsArguments.Length == 1 &&
                        !paramsArguments[0].IsSpread &&
                        paramsArguments[0].Type is ITypeSymbol singleParamsType &&
                        TryClassifyParameterArgument(parameter.Type, singleParamsType, out var paramsArrayTargetType, out var paramsArrayConversion))
                    {
                        var singleParamsArgument = paramsArguments[0];
                        var paramsSyntaxNode = singleParamsArgument.Syntax switch
                        {
                            ArgumentSyntax argumentSyntax => argumentSyntax.Expression,
                            SyntaxNode node => node,
                            _ => null
                        };

                        converted[i] = ApplyConversion(singleParamsArgument.Expression, paramsArrayTargetType, paramsArrayConversion, paramsSyntaxNode);
                        continue;
                    }

                    var paramsElements = new List<BoundExpression>(paramsArguments.Length);

                    foreach (var paramsArgument in paramsArguments)
                    {
                        var paramsExpression = paramsArgument.Expression;
                        var paramsSyntaxNode = paramsArgument.Syntax switch
                        {
                            ArgumentSyntax argumentSyntax => argumentSyntax.Expression,
                            SyntaxNode node => node,
                            _ => null
                        };

                        if (paramsArgument.IsSpread)
                        {
                            if (paramsExpression.Type is null || !IsSpreadEnumerable(paramsExpression.Type))
                            {
                                var typeName = paramsExpression.Type?.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "unknown";
                                _diagnostics.ReportSpreadSourceMustBeEnumerable(typeName, paramsSyntaxNode?.GetLocation() ?? Location.None);
                                paramsElements.Add(new BoundErrorExpression(elementType, null, BoundExpressionReason.TypeMismatch));
                                continue;
                            }

                            var spreadElementType = GetSpreadElementType(paramsExpression.Type);
                            if (!IsAssignable(elementType, spreadElementType, out _))
                            {
                                var fromType = spreadElementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                var toType = elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                                ReportCannotConvertFromTypeToType(fromType, toType, paramsSyntaxNode?.GetLocation() ?? Location.None);
                                paramsElements.Add(new BoundErrorExpression(elementType, null, BoundExpressionReason.TypeMismatch));
                                continue;
                            }

                            paramsElements.Add(new BoundSpreadElement(paramsExpression));
                            continue;
                        }

                        if (!ShouldAttemptConversion(paramsExpression) ||
                            elementType.TypeKind == TypeKind.Error ||
                            paramsExpression.Type is null)
                        {
                            paramsElements.Add(paramsExpression);
                            continue;
                        }

                        if (!IsAssignable(elementType, paramsExpression.Type, out var elementConversion))
                        {
                            var location = paramsSyntaxNode?.GetLocation() ?? parameter.Locations.FirstOrDefault();
                            if (location is not null)
                            {
                                ReportCannotConvertFromTypeToType(
                                    paramsExpression.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                    elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                    location);
                            }

                            paramsElements.Add(new BoundErrorExpression(elementType, null, BoundExpressionReason.TypeMismatch));
                            continue;
                        }

                        if (ReportStructUnionArgumentMayBeDefault(parameter, elementType, paramsExpression, paramsSyntaxNode))
                        {
                            paramsElements.Add(new BoundErrorExpression(elementType, null, BoundExpressionReason.ArgumentBindingFailed));
                            continue;
                        }

                        paramsElements.Add(ApplyConversion(paramsExpression, elementType, elementConversion, paramsSyntaxNode));
                    }

                    converted[i] = new BoundCollectionExpression(parameter.Type, paramsElements, parameter);
                    continue;
                }
            }

            var argument = mappedArguments[i];

            if (argument is null)
            {
                converted[i] = CreateOptionalArgument(parameter, callSyntax);
                continue;
            }

            var boundArgument = argument.Value;
            var expression = boundArgument.Expression;
            var syntaxNode = boundArgument.Syntax switch
            {
                ArgumentSyntax argumentSyntax => argumentSyntax.Expression,
                SyntaxNode node => node,
                _ => null
            };

            ReportNullableArgumentIfNeeded(parameter, expression, syntaxNode);

            // --- BEGIN NEW BLOCK ---
            if (parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            {
                converted[i] = expression;
                continue;
            }

            // Shape lambdas to a concrete delegate type when possible.
            // This mirrors local-initializer behavior and enables passing lambdas to `System.Delegate` parameters.
            if (expression is BoundFunctionExpression lambda)
            {
                // First: if the parameter itself is a concrete delegate type, replay the lambda against it.
                expression = BindLambdaToDelegateIfNeeded(expression, parameter.Type);
                if (ReferenceEquals(expression, lambda))
                    ReportSelectedLambdaReturnDiagnostics(lambda, parameter.Type);

                // Second: if the parameter is `System.Delegate` / `System.MulticastDelegate`,
                // keep the lambda's inferred delegate type and rely on implicit reference conversion.
                if (IsSystemDelegateLike(parameter.Type) && expression is BoundFunctionExpression stillLambda)
                {
                    var inferred = stillLambda.DelegateType as INamedTypeSymbol;
                    if (inferred is not null && inferred.TypeKind == TypeKind.Delegate)
                    {
                        var rebound = ReplayLambda(stillLambda, inferred);
                        if (rebound is not null)
                        {
                            expression = rebound;
                        }
                    }
                }
            }
            // --- END NEW BLOCK ---

            if (!ShouldAttemptConversion(expression) ||
                parameter.Type.TypeKind == TypeKind.Error ||
                expression.Type is null)
            {
                converted[i] = expression;
                continue;
            }

            if (expression is BoundFunctionExpression lambdaWithErrors &&
                HasExpressionErrors(lambdaWithErrors))
            {
                converted[i] = expression;
                continue;
            }

            if (!TryClassifyParameterArgument(parameter.Type, expression.Type, out var conversionTargetType, out var conversion))
            {
                var location = syntaxNode?.GetLocation() ?? parameter.Locations.FirstOrDefault();

                if (location is not null)
                {
                    ReportCannotConvertFromTypeToType(
                        expression.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        parameter.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        location);
                }

                converted[i] = new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.TypeMismatch);
                continue;
            }

            if (ReportStructUnionArgumentMayBeDefault(parameter, parameter.Type, expression, syntaxNode))
            {
                converted[i] = new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.ArgumentBindingFailed);
                continue;
            }

            var convertedExpr = ApplyConversion(expression, conversionTargetType, conversion, syntaxNode);

            // When a method group is resolved to a concrete delegate type during argument
            // conversion, update the bound-node cache so that language-service queries
            // (e.g. hover) on the argument expression see the selected overload rather than
            // the unresolved method group.
            if (syntaxNode is not null &&
                expression is BoundMethodGroupExpression &&
                convertedExpr is BoundDelegateCreationExpression)
            {
                CacheBoundNode(syntaxNode, convertedExpr);
            }

            converted[i] = convertedExpr;
        }

        return converted;
    }

    private bool ReportStructUnionArgumentMayBeDefault(
        IParameterSymbol parameter,
        ITypeSymbol parameterType,
        BoundExpression expression,
        SyntaxNode? syntaxNode)
    {
        if (parameterType.TryGetUnion() is not { TypeKind: TypeKind.Struct })
            return false;

        if (expression.Type?.TryGetUnion() is not { TypeKind: TypeKind.Struct })
            return false;

        if (syntaxNode is null)
            return false;

        if (!StructUnionDefaultStateFlow.ExpressionMayBeDefault(expression, syntaxNode, TryGetCachedBoundNode))
            return false;

        var parameterName = string.IsNullOrEmpty(parameter.Name)
            ? "<unnamed>"
            : parameter.Name;
        var unionType = parameterType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
        _diagnostics.ReportStructUnionArgumentMayBeDefault(parameterName, unionType, syntaxNode.GetLocation());
        return true;
    }

    protected bool ReportStructUnionReturnMayBeDefault(
        ITypeSymbol returnType,
        BoundExpression expression,
        SyntaxNode? syntaxNode)
    {
        if (returnType.TryGetUnion() is not { TypeKind: TypeKind.Struct })
            return false;

        if (expression.Type is { } expressionType &&
            expressionType.TypeKind != TypeKind.Error &&
            expressionType.TryGetUnion() is not { TypeKind: TypeKind.Struct })
        {
            return false;
        }

        if (syntaxNode is null)
            return false;

        if (!StructUnionDefaultStateFlow.ExpressionMayBeDefault(expression, syntaxNode, TryGetCachedBoundNode))
            return false;

        var unionType = returnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
        _diagnostics.ReportStructUnionReturnMayBeDefault(unionType, syntaxNode.GetLocation());
        return true;
    }

    private bool TryGetVarParamsElementType(ITypeSymbol parameterType, out ITypeSymbol elementType)
    {
        parameterType = parameterType.GetNonNullableType();

        if (parameterType is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            elementType = arrayType.ElementType;
            return true;
        }

        if (parameterType is INamedTypeSymbol namedType &&
            TryGetIEnumerableElementType(namedType, out var enumerableElementType))
        {
            elementType = enumerableElementType;
            return true;
        }

        elementType = Compilation.ErrorTypeSymbol;
        return false;
    }

    private static bool IsSystemDelegateLike(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var def = named.OriginalDefinition ?? named;

        if (def.ContainingNamespace is null)
            return false;

        // Match System.Delegate / System.MulticastDelegate
        if (!string.Equals(def.ContainingNamespace.ToDisplayString(), "System", StringComparison.Ordinal))
            return false;

        return string.Equals(def.MetadataName, "Delegate", StringComparison.Ordinal) ||
               string.Equals(def.MetadataName, "MulticastDelegate", StringComparison.Ordinal);
    }

    protected BoundExpression CreateOptionalArgument(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
            return new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.ArgumentBindingFailed);

        var parameterType = parameter.Type;
        if (parameterType.TypeKind == TypeKind.Error)
            return new BoundErrorExpression(parameterType, null, BoundExpressionReason.ArgumentBindingFailed);

        var value = parameter.ExplicitDefaultValue;

        if (value is OptionNoneParameterDefaultValue)
        {
            return TryBindDiscriminatedUnionCase(parameterType, "None", parameter.Locations.FirstOrDefault() ?? Location.None)
                ?? new BoundErrorExpression(parameterType, null, BoundExpressionReason.ArgumentBindingFailed);
        }

        if (value is null)
        {
            if (parameterType.TryGetUnion() is { TypeKind: TypeKind.Struct })
                return new BoundDefaultValueExpression(parameterType);

            return BoundFactory.NullLiteral(parameterType);
        }

        if (parameter is PEParameterSymbol { ExplicitDefaultValueIsTypeDefault: true }
            && parameterType.IsValueType
            && parameterType.SpecialType == SpecialType.None
            && parameterType.TypeKind != TypeKind.Enum)
        {
            return new BoundDefaultValueExpression(parameterType);
        }

        if (!TryCreateOptionalLiteral(parameterType, value, out var literal))
        {
            if (parameter is PEParameterSymbol { ExplicitDefaultValueIsTypeDefault: true })
                return new BoundDefaultValueExpression(parameterType);

            ReportOptionalParameterDefaultValueCannotConvert(parameter, parameterType);
            return new BoundErrorExpression(parameterType, null, BoundExpressionReason.ArgumentBindingFailed);
        }

        if (SymbolEqualityComparer.Default.Equals(literal.Type, parameterType))
            return literal;

        var conversion = Compilation.ClassifyConversion(literal.Type!, parameterType);
        if (!conversion.Exists || !conversion.IsImplicit)
        {
            ReportOptionalParameterDefaultValueCannotConvert(parameter, parameterType);
            return new BoundErrorExpression(parameterType, null, BoundExpressionReason.ArgumentBindingFailed);
        }

        return ApplyConversion(literal, parameterType, conversion);
    }

    protected BoundExpression CreateOptionalArgument(IParameterSymbol parameter, SyntaxNode? callSyntax)
    {
        var expression = CreateOptionalArgument(parameter);

        if (ReportStructUnionArgumentMayBeDefault(parameter, parameter.Type, expression, callSyntax))
            return new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.ArgumentBindingFailed);

        return expression;
    }

    private void ReportOptionalParameterDefaultValueCannotConvert(IParameterSymbol parameter, ITypeSymbol parameterType)
    {
        var parameterTypeDisplay = parameterType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var parameterName = parameter.Name ?? string.Empty;
        var location = parameter.Locations.FirstOrDefault() ?? Location.None;

        _diagnostics.ReportOptionalParameterDefaultValueCannotConvert(parameterName, parameterTypeDisplay, location);
    }

    private bool TryCreateOptionalLiteral(ITypeSymbol parameterType, object value, out BoundLiteralExpression literal)
    {
        if (IsEnumLikeType(parameterType) &&
            TryConvertEnumLiteralValue(value, out var enumValue))
        {
            literal = new BoundLiteralExpression(BoundLiteralExpressionKind.NumericLiteral, enumValue, parameterType);
            return true;
        }

        if (!ConstantValueEvaluator.TryConvert(parameterType, value, out var converted))
        {
            literal = null!;
            return false;
        }

        var literalType = GetOptionalLiteralType(parameterType, converted);
        if (literalType is null)
        {
            literal = null!;
            return false;
        }

        literal = new BoundLiteralExpression(GetOptionalLiteralKind(converted!), converted!, literalType);
        return true;
    }

    private ITypeSymbol? GetOptionalLiteralType(ITypeSymbol parameterType, object value)
    {
        return value switch
        {
            bool => Compilation.GetSpecialType(SpecialType.System_Boolean),
            string => Compilation.GetSpecialType(SpecialType.System_String),
            char => Compilation.GetSpecialType(SpecialType.System_Char),
            sbyte => Compilation.GetSpecialType(SpecialType.System_SByte),
            byte => Compilation.GetSpecialType(SpecialType.System_Byte),
            short => Compilation.GetSpecialType(SpecialType.System_Int16),
            ushort => Compilation.GetSpecialType(SpecialType.System_UInt16),
            int => Compilation.GetSpecialType(SpecialType.System_Int32),
            uint => Compilation.GetSpecialType(SpecialType.System_UInt32),
            long => Compilation.GetSpecialType(SpecialType.System_Int64),
            ulong => Compilation.GetSpecialType(SpecialType.System_UInt64),
            float => Compilation.GetSpecialType(SpecialType.System_Single),
            double => Compilation.GetSpecialType(SpecialType.System_Double),
            decimal => Compilation.GetSpecialType(SpecialType.System_Decimal),
            DateTime => Compilation.GetSpecialType(SpecialType.System_DateTime),
            Enum => parameterType,
            _ => parameterType
        };
    }

    private static bool TryConvertEnumLiteralValue(object value, out object converted)
    {
        switch (value)
        {
            case Enum enumValue:
                converted = Convert.ToInt64(enumValue);
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                converted = value;
                return true;
            default:
                converted = null!;
                return false;
        }
    }

    private static bool IsEnumLikeType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
            return true;

        return type is INamedTypeSymbol named &&
               (named.EnumUnderlyingType is not null ||
                named.BaseType?.SpecialType == SpecialType.System_Enum);
    }

    private static BoundLiteralExpressionKind GetOptionalLiteralKind(object value)
    {
        return value switch
        {
            bool b => b ? BoundLiteralExpressionKind.TrueLiteral : BoundLiteralExpressionKind.FalseLiteral,
            string => BoundLiteralExpressionKind.StringLiteral,
            char => BoundLiteralExpressionKind.CharLiteral,
            _ => BoundLiteralExpressionKind.NumericLiteral
        };
    }

    private BoundExpression[] ConvertInvocationArguments(
        IMethodSymbol method,
        BoundArgument[] invocationArguments,
        BoundExpression? extensionReceiver,
        SyntaxNode receiverSyntax,
        SyntaxNode callSyntax,
        out BoundExpression? convertedExtensionReceiver)
    {
        convertedExtensionReceiver = null;

        if (method.IsExtensionMethod && IsExtensionReceiver(extensionReceiver))
        {
            var parameters = method.Parameters;
            convertedExtensionReceiver = ConvertSingleArgument(
                extensionReceiver!,
                parameters[0],
                receiverSyntax);

            if (parameters.Length == 1)
                return Array.Empty<BoundExpression>();

            return ConvertArguments(parameters.RemoveAt(0), invocationArguments, callSyntax);
        }

        return ConvertArguments(method.Parameters, invocationArguments, callSyntax);
    }

    private BoundExpression ConvertSingleArgument(BoundExpression argument, IParameterSymbol parameter, SyntaxNode syntax)
    {
        ReportNullableArgumentIfNeeded(parameter, argument, syntax);

        if (parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.In)
            return argument;

        if (!ShouldAttemptConversion(argument) ||
            parameter.Type.TypeKind == TypeKind.Error ||
            argument.Type is null)
        {
            return argument;
        }

        if (!TryClassifyParameterArgument(parameter.Type, argument.Type, out var conversionTargetType, out var conversion))
        {
            ReportCannotConvertFromTypeToType(
                argument.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                parameter.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.GetLocation());

            return new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.TypeMismatch);
        }

        if (ReportStructUnionArgumentMayBeDefault(parameter, parameter.Type, argument, syntax))
            return new BoundErrorExpression(parameter.Type, null, BoundExpressionReason.ArgumentBindingFailed);

        return ApplyConversion(argument, conversionTargetType, conversion, syntax);
    }

    private void ReportNullableArgumentIfNeeded(
        IParameterSymbol parameter,
        BoundExpression argument,
        SyntaxNode? syntax)
    {
        if (parameter.RefKind == RefKind.Out ||
            NullableMetadataFacts.ParameterAllowsNullInput(parameter) ||
            !ArgumentMayBeNull(argument))
        {
            return;
        }

        var targetType = parameter.Type is RefTypeSymbol refType
            ? refType.ElementType
            : parameter.Type;
        _diagnostics.ReportCannotAssignNullToType(
            targetType.ToDisplayStringForDiagnostics(SymbolDisplayFormat.MinimallyQualifiedFormat),
            syntax?.GetLocation() ?? parameter.Locations.FirstOrDefault() ?? Location.None);
    }

    private static bool ArgumentMayBeNull(BoundExpression argument)
    {
        if (argument is BoundAddressOfExpression { Storage: { } storage })
            argument = storage;

        return argument.Type?.TypeKind == TypeKind.Null ||
               argument.Type?.IsNullable == true;
    }

    protected static bool AreArgumentsCompatibleWithMethod(
        IMethodSymbol method,
        int argumentCount,
        BoundExpression? receiver,
        BoundArgument[]? arguments = null)
    {
        var providedCount = argumentCount;

        if (method.IsExtensionMethod && IsExtensionReceiver(receiver))
            providedCount++;

        if (!SupportsArgumentCount(method.Parameters, providedCount))
            return false;

        if (arguments is null)
            return true;

        return OverloadResolver.TryMapArguments(
            method.Parameters,
            arguments,
            method.IsExtensionMethod && IsExtensionReceiver(receiver),
            out _);
    }

    protected static bool SupportsArgumentCount(ImmutableArray<IParameterSymbol> parameters, int providedCount)
    {
        var hasParamsParameter = parameters.Length > 0 && parameters[^1].IsVarParams;
        if (!hasParamsParameter && providedCount > parameters.Length)
            return false;

        var required = GetRequiredParameterCount(parameters);
        return providedCount >= required;
    }

    protected static int GetRequiredParameterCount(ImmutableArray<IParameterSymbol> parameters)
    {
        var required = parameters.Length;
        while (required > 0 &&
               (parameters[required - 1].HasExplicitDefaultValue || parameters[required - 1].IsVarParams))
            required--;

        return required;
    }

    private static bool IsExtensionReceiver(BoundExpression? receiver)
    {
        return receiver is not null and not BoundTypeExpression and not BoundNamespaceExpression;
    }

    public readonly struct TargetTypeScope : IDisposable
    {
        private readonly BlockBinder _binder;
        private readonly int _depthBeforePush;

        public TargetTypeScope(BlockBinder binder, int depthBeforePush)
        {
            _binder = binder;
            _depthBeforePush = depthBeforePush;
        }

        public void Dispose()
        {
            try
            {
                if (_binder._targetTypeStack.Count > _depthBeforePush)
                    _binder._targetTypeStack.Pop();
            }
            finally
            {
                Monitor.Exit(_binder._executionGate);
            }
        }
    }

    private readonly record struct TargetTypeFrame(
        ITypeSymbol? Type,
        SyntaxNode? Root,
        bool AppliesToDescendants);

    private readonly struct ExecutionScope : IDisposable
    {
        private readonly object _gate;

        public ExecutionScope(object gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Monitor.Exit(_gate);
        }
    }

    private readonly struct FunctionExpressionRebindKey : IEquatable<FunctionExpressionRebindKey>
    {
        public FunctionExpressionRebindKey(ExpressionSyntax syntax, INamedTypeSymbol delegateType)
        {
            Syntax = syntax;
            DelegateType = delegateType;
        }

        public ExpressionSyntax Syntax { get; }
        public INamedTypeSymbol DelegateType { get; }

        public bool Equals(FunctionExpressionRebindKey other)
        {
            return ReferenceEquals(Syntax, other.Syntax) &&
                   SymbolEqualityComparer.Default.Equals(DelegateType, other.DelegateType);
        }

        public override bool Equals(object? obj)
        {
            return obj is FunctionExpressionRebindKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = ReferenceEqualityComparer.Instance.GetHashCode(Syntax);
            hash = (hash * 397) ^ SymbolEqualityComparer.Default.GetHashCode(DelegateType);
            return hash;
        }
    }

    private static SyntaxNode? GetInvocationReceiverSyntax(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            MemberBindingExpressionSyntax binding when invocation.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == invocation
                => conditional.Expression,
            _ => null
        };
    }

    private BoundExpression BindCollectionExpression(CollectionExpressionSyntax syntax)
    {
        return BindCollectionExpressionCore(
            syntax,
            syntax.Elements,
            inferArrayByDefault: false,
            isMutableByDefault: syntax.IsMutableByDefault);
    }

    private BoundExpression BindArrayExpression(ArrayExpressionSyntax syntax)
    {
        return BindCollectionExpressionCore(
            syntax,
            syntax.Elements,
            inferArrayByDefault: true,
            isMutableByDefault: false);
    }

    private BoundExpression BindCollectionExpressionCore(
        SyntaxNode syntax,
        SeparatedSyntaxList<CollectionElementSyntax> syntaxElements,
        bool inferArrayByDefault,
        bool isMutableByDefault,
        ITypeSymbol? targetTypeOverride = null)
    {
        var targetType = targetTypeOverride ?? GetTargetType(syntax);

        // Expression-statement contexts can flow Unit as a target type.
        // Collection literals should still infer a concrete collection type.
        if (targetType is not null &&
            (targetType.SpecialType is SpecialType.System_Unit or SpecialType.System_Void))
        {
            targetType = null;
        }

        if (targetType is NullableTypeSymbol nullableTargetType)
            targetType = nullableTargetType.UnderlyingType;

        var hasDictionaryElements = syntaxElements.Any(static element =>
            element is DictionaryElementSyntax or DictionarySpreadElementSyntax or DictionaryComprehensionElementSyntax);

        if (targetTypeOverride is null &&
            targetType is not null &&
            TryGetCollectionExpressionUnionTarget(targetType, hasDictionaryElements, out var unionMemberTarget))
        {
            var expression = BindCollectionExpressionCore(
                syntax,
                syntaxElements,
                inferArrayByDefault,
                isMutableByDefault,
                unionMemberTarget);

            if (expression.Type is not null &&
                IsAssignable(targetType, expression.Type, out var unionConversion))
            {
                return ApplyConversion(expression, targetType, unionConversion, syntax);
            }

            return expression;
        }

        // Empty collection: defer to target type if available
        if (syntaxElements.Count == 0)
        {
            if (targetType != null)
            {
                if (TryBindCollectionBuilderInvocation(
                        syntax,
                        targetType,
                        Array.Empty<BoundExpression>(),
                        Array.Empty<SyntaxNode>(),
                        out var builderInvocation))
                {
                    return builderInvocation;
                }

                return new BoundEmptyCollectionExpression(targetType);
            }

            _diagnostics.ReportEmptyCollectionLiteralRequiresTargetType(syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        var elements = new List<BoundExpression>(syntaxElements.Count);
        var elementNodes = new List<SyntaxNode>(syntaxElements.Count);

        if (hasDictionaryElements)
            return BindDictionaryExpressionCore(syntax, syntaxElements, targetType, inferArrayByDefault, isMutableByDefault);

        var collectionElementTargetType = GetCollectionElementTargetType(targetType);

        foreach (var elementSyntax in syntaxElements)
        {
            BoundExpression boundElement;
            SyntaxNode elementNode;
            switch (elementSyntax)
            {
                case ExpressionElementSyntax exprElem:
                    if (exprElem.Expression is RangeExpressionSyntax rangeExpression &&
                        TryBindCollectionRangeElement(rangeExpression, out var rangeElement))
                    {
                        boundElement = rangeElement;
                    }
                    else
                    {
                        boundElement = collectionElementTargetType is not null
                            ? BindExpressionWithTargetType(exprElem.Expression, collectionElementTargetType)
                            : BindExpression(exprElem.Expression);
                    }

                    elementNode = exprElem.Expression;
                    break;
                case SpreadElementSyntax spreadElem:
                    var spreadExpr = BindExpression(spreadElem.Expression);
                    if (!IsSpreadEnumerable(spreadExpr.Type!))
                    {
                        _diagnostics.ReportSpreadSourceMustBeEnumerable(
                            spreadExpr.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            spreadElem.GetLocation());
                    }

                    boundElement = new BoundSpreadElement(spreadExpr);
                    elementNode = spreadElem.Expression;
                    break;
                case CollectionComprehensionElementSyntax comprehensionElem:
                    boundElement = BindCollectionComprehensionElement(comprehensionElem, collectionElementTargetType);
                    elementNode = comprehensionElem.Source;
                    break;
                default:
                    continue;
            }

            elements.Add(boundElement);
            elementNodes.Add(elementNode);
        }

        if (targetType is IArrayTypeSymbol arrayType)
        {
            if (arrayType.Rank != 1)
            {
                ReportCannotConvertFromTypeToType(
                    "array expression",
                    arrayType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    syntax.GetLocation());
                return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
            }

            var elementType = arrayType.ElementType;
            if (arrayType.FixedLength is int fixedLength &&
                TryGetStaticallyKnownCollectionLength(elements, out var targetLength) &&
                targetLength != fixedLength)
            {
                _diagnostics.ReportPositionalDeconstructionElementCountMismatch(
                    targetLength,
                    fixedLength,
                    syntax.GetLocation());
            }

            var converted = ImmutableArray.CreateBuilder<BoundExpression>(elements.Count);

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var elementSyntax = elementNodes[i];

                if (element is BoundSpreadElement spread)
                {
                    var sourceType = GetSpreadElementType(spread.Expression.Type!);
                    if (!IsAssignable(elementType, sourceType, out _))
                    {
                        ReportCannotConvertFromTypeToType(
                            sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            syntax.GetLocation());
                    }

                    converted.Add(element);
                    continue;
                }

                if (elementType.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(element))
                {
                    if (!IsAssignable(elementType, element.Type!, out var conversion))
                    {
                        ReportCannotConvertFromTypeToType(
                            element.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            syntax.GetLocation());
                    }
                    else
                    {
                        converted.Add(ApplyConversion(element, elementType, conversion, elementSyntax));
                        continue;
                    }
                }

                converted.Add(element);
            }

            return new BoundCollectionExpression(arrayType, converted.ToImmutable());
        }

        if (TryGetSpanElementType(targetType, out var spanElementType))
        {
            var converted = ImmutableArray.CreateBuilder<BoundExpression>(elements.Count);

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                var elementSyntax = elementNodes[i];

                if (element is BoundSpreadElement spread)
                {
                    var sourceType = GetSpreadElementType(spread.Expression.Type!);
                    if (!IsAssignable(spanElementType, sourceType, out _))
                    {
                        ReportCannotConvertFromTypeToType(
                            sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            spanElementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            syntax.GetLocation());
                    }

                    converted.Add(element);
                    continue;
                }

                if (spanElementType.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(element))
                {
                    if (!IsAssignable(spanElementType, element.Type!, out var conversion))
                    {
                        ReportCannotConvertFromTypeToType(
                            element.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            spanElementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            syntax.GetLocation());
                    }
                    else
                    {
                        converted.Add(ApplyConversion(element, spanElementType, conversion, elementSyntax));
                        continue;
                    }
                }

                converted.Add(element);
            }

            return new BoundCollectionExpression(targetType!, converted.ToImmutable());
        }

        if (targetType is not null &&
            TryBindCollectionBuilderInvocation(syntax, targetType, elements, elementNodes, out var builderBasedCollection))
        {
            return builderBasedCollection;
        }

        if (targetType is INamedTypeSymbol namedType)
        {
            var addMethod = new SymbolQuery("Add", namedType, IsStatic: false)
                .Lookup(this)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(static method => method.Parameters.Length == 1);
            var constructor = namedType.Constructors
                .FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);

            if (addMethod is not null && constructor is not null)
            {
                var elementType = addMethod.Parameters[0].Type;
                var converted = ImmutableArray.CreateBuilder<BoundExpression>(elements.Count);

                for (var i = 0; i < elements.Count; i++)
                {
                    var element = elements[i];
                    var elementSyntax = elementNodes[i];

                    if (element is BoundSpreadElement spread)
                    {
                        var sourceType = GetSpreadElementType(spread.Expression.Type!);
                        if (!IsAssignable(elementType, sourceType, out _))
                        {
                            ReportCannotConvertFromTypeToType(
                                sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                syntax.GetLocation());
                        }

                        converted.Add(element);
                        continue;
                    }

                    if (elementType.TypeKind != TypeKind.Error &&
                        ShouldAttemptConversion(element))
                    {
                        if (!IsAssignable(elementType, element.Type!, out var conversion))
                        {
                            ReportCannotConvertFromTypeToType(
                                element.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                elementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                syntax.GetLocation());
                        }
                        else
                        {
                            converted.Add(ApplyConversion(element, elementType, conversion, elementSyntax));
                            continue;
                        }
                    }

                    converted.Add(element);
                }

                return new BoundCollectionExpression(namedType, converted.ToImmutable(), addMethod);
            }
        }

        if (targetType is not null &&
            TryBindCollectionExpressionViaImplicitEnumerableConversion(syntax, targetType, elements, elementNodes, out var implicitlyConvertedCollection))
        {
            return implicitlyConvertedCollection;
        }

        // Fallback to array if target type couldn't be determined
        if (!TryInferCollectionElementType(elements, out var inferredElementType, out var inferenceConflict))
        {
            ReportCannotConvertFromTypeToType(
                inferenceConflict.Left.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                inferenceConflict.Right.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }
        var convertedFallback = ImmutableArray.CreateBuilder<BoundExpression>(elements.Count);

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var elementSyntax = elementNodes[i];

            if (element is BoundSpreadElement)
            {
                convertedFallback.Add(element);
                continue;
            }

            if (inferredElementType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(element))
            {
                if (IsAssignable(inferredElementType, element.Type!, out var conversion))
                {
                    convertedFallback.Add(ApplyConversion(element, inferredElementType, conversion, elementSyntax));
                    continue;
                }
            }

            convertedFallback.Add(element);
        }

        if (inferArrayByDefault)
        {
            var fallbackFixedLength = TryGetStaticallyKnownCollectionLength(elements, out var knownLength)
                ? (int?)knownLength
                : null;
            var inferredArrayType = Compilation.CreateArrayTypeSymbol(inferredElementType, fixedLength: fallbackFixedLength);
            return new BoundCollectionExpression(inferredArrayType, convertedFallback.ToImmutable());
        }

        if (isMutableByDefault &&
            Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1") is INamedTypeSymbol mutableListTypeDefinition)
        {
            var inferredMutableListType = (INamedTypeSymbol)mutableListTypeDefinition.Construct(inferredElementType);
            var addMethod = inferredMutableListType.GetMembers("Add")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 1);

            return new BoundCollectionExpression(inferredMutableListType, convertedFallback.ToImmutable(), addMethod);
        }

        if (Compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableList`1") is INamedTypeSymbol immutableListTypeDefinition)
        {
            var inferredImmutableListType = (INamedTypeSymbol)immutableListTypeDefinition.Construct(inferredElementType);
            return new BoundCollectionExpression(inferredImmutableListType, convertedFallback.ToImmutable());
        }

        var fallbackArray = Compilation.CreateArrayTypeSymbol(inferredElementType);
        return new BoundCollectionExpression(fallbackArray, convertedFallback.ToImmutable());
    }

    private ITypeSymbol? GetCollectionElementTargetType(ITypeSymbol? targetType)
    {
        if (targetType is IArrayTypeSymbol arrayTarget)
            return arrayTarget.ElementType;

        if (TryGetSpanElementType(targetType, out var spanElementType))
            return spanElementType;

        if (targetType is not INamedTypeSymbol namedType)
            return null;

        var addMethod = namedType.GetMembers("Add")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => !method.IsStatic && method.Parameters.Length == 1);

        return addMethod?.Parameters[0].Type;
    }

    private bool TryGetCollectionExpressionUnionTarget(
        ITypeSymbol unionType,
        bool hasDictionaryElements,
        out ITypeSymbol targetType)
    {
        targetType = Compilation.ErrorTypeSymbol;
        IEnumerable<ITypeSymbol> members;

        if (unionType.TryGetUnion() is IUnionSymbol discriminatedUnion)
            members = discriminatedUnion.MemberTypes.Select(UnwrapAlias);
        else
            return false;

        var candidates = new List<ITypeSymbol>();

        foreach (var member in members)
        {
            if (!IsCollectionExpressionTargetCandidate(member, hasDictionaryElements))
                continue;

            if (!candidates.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, member)))
                candidates.Add(member);
        }

        if (candidates.Count != 1)
            return false;

        targetType = candidates[0];
        return true;
    }

    private bool IsCollectionExpressionTargetCandidate(ITypeSymbol type, bool hasDictionaryElements)
    {
        type = UnwrapAlias(type);

        if (hasDictionaryElements)
        {
            return type is INamedTypeSymbol namedType &&
                (TryGetDictionaryInterfaceElementTypes(namedType, out _, out _) ||
                 TryGetDictionaryAddSignature(namedType, out var dictionaryAddMethod, out _, out _) && dictionaryAddMethod is not null);
        }

        if (type is IArrayTypeSymbol arrayType)
            return arrayType.Rank == 1;

        if (type is not INamedTypeSymbol namedTarget)
            return false;

        if (TryGetCollectionBuilderInfo(namedTarget, out _, out _))
            return true;

        var addMethod = namedTarget.GetMembers("Add")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => !method.IsStatic && method.Parameters.Length == 1);
        var constructor = namedTarget.Constructors
            .FirstOrDefault(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);

        if (addMethod is not null && constructor is not null)
            return true;

        if (TryGetIEnumerableElementType(namedTarget, out var enumerableElementType))
        {
            var arrayTarget = Compilation.CreateArrayTypeSymbol(enumerableElementType);
            var conversion = Compilation.ClassifyConversion(arrayTarget, namedTarget);
            return conversion.Exists && conversion.IsImplicit;
        }

        return false;
    }

    private BoundExpression BindDictionaryExpressionCore(
        SyntaxNode syntax,
        SeparatedSyntaxList<CollectionElementSyntax> syntaxElements,
        ITypeSymbol? targetType,
        bool inferArrayByDefault,
        bool isMutableByDefault)
    {
        if (inferArrayByDefault)
        {
            ReportCannotConvertFromTypeToType("dictionary entry", "array element", syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        if (syntaxElements.Any(static element =>
                element is not (DictionaryElementSyntax or DictionarySpreadElementSyntax or SpreadElementSyntax or DictionaryComprehensionElementSyntax)))
        {
            ReportCannotConvertFromTypeToType("dictionary entry", "collection element", syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        if (targetType is INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceTarget &&
            TryGetDictionaryInterfaceElementTypes(interfaceTarget, out var interfaceKeyType, out var interfaceValueType) &&
            Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2") is INamedTypeSymbol dictionaryTypeDefinition)
        {
            var concreteDictionaryType = (INamedTypeSymbol)dictionaryTypeDefinition.Construct(interfaceKeyType, interfaceValueType);
            var concreteDictionary = BindDictionaryExpressionToNamedTarget(syntax, syntaxElements, concreteDictionaryType);
            var conversion = Compilation.ClassifyConversion(concreteDictionary.Type!, interfaceTarget);
            if (conversion.Exists && conversion.IsImplicit)
                return ApplyConversion(concreteDictionary, interfaceTarget, conversion, syntax);

            return concreteDictionary;
        }

        if (targetType is INamedTypeSymbol namedTargetType)
            return BindDictionaryExpressionToNamedTarget(syntax, syntaxElements, namedTargetType);

        if (!TryInferDictionaryElementTypes(syntaxElements, out var inferredKeyType, out var inferredValueType))
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        var inferredDictionaryMetadataName = isMutableByDefault
            ? "System.Collections.Generic.Dictionary`2"
            : "System.Collections.Immutable.ImmutableDictionary`2";

        if (Compilation.GetTypeByMetadataName(inferredDictionaryMetadataName) is not INamedTypeSymbol inferredDictionaryDefinition)
        {
            ReportCannotConvertFromTypeToType("dictionary entry", "Dictionary<TKey, TValue>", syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        var inferredDictionaryType = (INamedTypeSymbol)inferredDictionaryDefinition.Construct(inferredKeyType, inferredValueType);
        return BindDictionaryExpressionToNamedTarget(syntax, syntaxElements, inferredDictionaryType);
    }

    private BoundExpression BindDictionaryExpressionToNamedTarget(
        SyntaxNode syntax,
        IEnumerable<CollectionElementSyntax> syntaxElements,
        INamedTypeSymbol targetType)
    {
        if (!TryGetDictionaryAddSignature(targetType, out var addMethod, out var keyType, out var valueType))
        {
            ReportCannotConvertFromTypeToType(
                "dictionary entry",
                targetType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.GetLocation());
            return ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        }

        var elements = ImmutableArray.CreateBuilder<DictionaryElementBinding>();

        foreach (var elementSyntax in syntaxElements)
        {
            switch (elementSyntax)
            {
                case DictionaryElementSyntax entry:
                    elements.Add(BindDictionaryEntryElement(entry.Key, entry.Value, keyType, valueType));
                    break;
                case DictionarySpreadElementSyntax spreadEntry:
                    elements.Add(BindDictionaryEntryElement(spreadEntry.Key, spreadEntry.Value, keyType, valueType));
                    break;
                case SpreadElementSyntax spread:
                    var spreadExpression = BindExpression(spread.Expression);
                    if (!TryGetDictionarySpreadElementTypes(spreadExpression.Type ?? Compilation.ErrorTypeSymbol, out var spreadKeyType, out var spreadValueType))
                    {
                        _diagnostics.ReportSpreadSourceMustBeEnumerable(
                            spreadExpression.Type?.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "error",
                            spread.Expression.GetLocation());
                    }
                    else
                    {
                        if (!IsAssignable(keyType, spreadKeyType, out _))
                        {
                            ReportCannotConvertFromTypeToType(
                                spreadKeyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                keyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                spread.Expression.GetLocation());
                        }

                        if (!IsAssignable(valueType, spreadValueType, out _))
                        {
                            ReportCannotConvertFromTypeToType(
                                spreadValueType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                valueType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                spread.Expression.GetLocation());
                        }
                    }

                    elements.Add(new DictionarySpreadBinding(spreadExpression));
                    break;
                case DictionaryComprehensionElementSyntax comprehension:
                    elements.Add(BindDictionaryComprehensionElement(comprehension, keyType, valueType));
                    break;
            }
        }

        return new BoundDictionaryExpression(targetType, elements.ToImmutable(), addMethod);
    }

    private bool TryGetDictionaryAddSignature(
        INamedTypeSymbol targetType,
        out IMethodSymbol? addMethod,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        addMethod = targetType
            .GetMembers("Add")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => !method.IsStatic && method.Parameters.Length == 2);

        if (addMethod is not null)
        {
            keyType = addMethod.Parameters[0].Type;
            valueType = addMethod.Parameters[1].Type;
            return true;
        }

        keyType = Compilation.ErrorTypeSymbol;
        valueType = Compilation.ErrorTypeSymbol;
        return false;
    }

    private bool TryGetDictionaryInterfaceInfo(
        INamedTypeSymbol targetType,
        out INamedTypeSymbol receiverType,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        receiverType = null!;
        keyType = Compilation.ErrorTypeSymbol;
        valueType = Compilation.ErrorTypeSymbol;

        foreach (var candidate in EnumerateSelfAndInterfaces(targetType))
        {
            if (!IsKnownDictionaryInterface(candidate))
                continue;

            receiverType = candidate;
            keyType = candidate.TypeArguments[0];
            valueType = candidate.TypeArguments[1];
            return true;
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }

        static bool IsKnownDictionaryInterface(INamedTypeSymbol type)
        {
            if (type.TypeArguments.Length != 2 || type.OriginalDefinition is not INamedTypeSymbol definition)
                return false;

            var metadataName = definition.ToFullyQualifiedMetadataName();
            return string.Equals(metadataName, "System.Collections.Generic.IDictionary`2", StringComparison.Ordinal) ||
                   string.Equals(metadataName, "System.Collections.Generic.IReadOnlyDictionary`2", StringComparison.Ordinal);
        }
    }

    private bool TryGetDictionaryInterfaceElementTypes(
        INamedTypeSymbol targetType,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        return TryGetDictionaryInterfaceInfo(targetType, out _, out keyType, out valueType);
    }

    private bool TryInferDictionaryElementTypes(
        IEnumerable<CollectionElementSyntax> syntaxElements,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        keyType = Compilation.ErrorTypeSymbol;
        valueType = Compilation.ErrorTypeSymbol;

        var keyTypes = new List<ITypeSymbol>();
        var valueTypes = new List<ITypeSymbol>();

        foreach (var elementSyntax in syntaxElements)
        {
            switch (elementSyntax)
            {
                case DictionaryElementSyntax entry:
                    keyTypes.Add(BindExpression(entry.Key).Type ?? Compilation.ErrorTypeSymbol);
                    valueTypes.Add(BindExpression(entry.Value).Type ?? Compilation.ErrorTypeSymbol);
                    break;
                case DictionarySpreadElementSyntax spreadEntry:
                    keyTypes.Add(BindExpression(spreadEntry.Key).Type ?? Compilation.ErrorTypeSymbol);
                    valueTypes.Add(BindExpression(spreadEntry.Value).Type ?? Compilation.ErrorTypeSymbol);
                    break;
                case SpreadElementSyntax spread:
                    var spreadExpression = BindExpression(spread.Expression);
                    if (!TryGetDictionarySpreadElementTypes(spreadExpression.Type ?? Compilation.ErrorTypeSymbol, out var spreadKeyType, out var spreadValueType))
                    {
                        _diagnostics.ReportSpreadSourceMustBeEnumerable(
                            spreadExpression.Type?.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "error",
                            spread.Expression.GetLocation());
                        return false;
                    }

                    keyTypes.Add(spreadKeyType);
                    valueTypes.Add(spreadValueType);
                    break;
                case DictionaryComprehensionElementSyntax comprehension:
                    var boundComprehension = BindDictionaryComprehensionElement(comprehension, targetKeyType: null, targetValueType: null);
                    keyTypes.Add(boundComprehension.KeyType);
                    valueTypes.Add(boundComprehension.ValueType);
                    break;
            }
        }

        if (!TryInferBestCommonType(keyTypes, out keyType, out var keyConflict))
        {
            ReportCannotConvertFromTypeToType(
                keyConflict.Left.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                keyConflict.Right.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntaxElements.First().GetLocation());
            return false;
        }

        if (!TryInferBestCommonType(valueTypes, out valueType, out var valueConflict))
        {
            ReportCannotConvertFromTypeToType(
                valueConflict.Left.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                valueConflict.Right.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntaxElements.First().GetLocation());
            return false;
        }

        return true;
    }

    private DictionaryEntryBinding BindDictionaryEntryElement(
        ExpressionSyntax keySyntax,
        ExpressionSyntax valueSyntax,
        ITypeSymbol keyType,
        ITypeSymbol valueType)
    {
        var key = keyType.TypeKind == TypeKind.Error
            ? BindExpression(keySyntax)
            : BindExpressionWithTargetType(keySyntax, keyType);
        var value = valueType.TypeKind == TypeKind.Error
            ? BindExpression(valueSyntax)
            : BindExpressionWithTargetType(valueSyntax, valueType);

        if (keyType.TypeKind != TypeKind.Error &&
            key.Type?.TypeKind != TypeKind.Error &&
            ShouldAttemptConversion(key))
        {
            if (IsAssignable(keyType, key.Type!, out var keyConversion))
                key = ApplyConversion(key, keyType, keyConversion, keySyntax);
            else
                ReportCannotConvertFromTypeToType(
                    key.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    keyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    keySyntax.GetLocation());
        }

        if (valueType.TypeKind != TypeKind.Error &&
            value.Type?.TypeKind != TypeKind.Error &&
            ShouldAttemptConversion(value))
        {
            if (IsAssignable(valueType, value.Type!, out var valueConversion))
                value = ApplyConversion(value, valueType, valueConversion, valueSyntax);
            else
                ReportCannotConvertFromTypeToType(
                    value.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    valueType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    valueSyntax.GetLocation());
        }

        return new DictionaryEntryBinding(key, value);
    }

    private bool TryGetDictionarySpreadElementTypes(
        ITypeSymbol type,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        keyType = Compilation.ErrorTypeSymbol;
        valueType = Compilation.ErrorTypeSymbol;

        if (type is IArrayTypeSymbol arrayType &&
            TryGetKeyValuePairElementTypes(arrayType.ElementType, out keyType, out valueType))
        {
            return true;
        }

        if (type is not INamedTypeSymbol namedType)
            return false;

        foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
        {
            if (candidate.OriginalDefinition is not INamedTypeSymbol definition ||
                !string.Equals(definition.ToFullyQualifiedMetadataName(), "System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryGetKeyValuePairElementTypes(candidate.TypeArguments[0], out keyType, out valueType))
                return true;
        }

        return false;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }
    }

    private bool TryGetKeyValuePairElementTypes(
        ITypeSymbol type,
        out ITypeSymbol keyType,
        out ITypeSymbol valueType)
    {
        keyType = Compilation.ErrorTypeSymbol;
        valueType = Compilation.ErrorTypeSymbol;

        if (type is not INamedTypeSymbol namedType ||
            namedType.OriginalDefinition is not INamedTypeSymbol definition ||
            !string.Equals(definition.ToFullyQualifiedMetadataName(), "System.Collections.Generic.KeyValuePair`2", StringComparison.Ordinal))
        {
            return false;
        }

        keyType = namedType.TypeArguments[0];
        valueType = namedType.TypeArguments[1];
        return true;
    }

    private bool TryInferBestCommonType(
        IReadOnlyList<ITypeSymbol> types,
        out ITypeSymbol inferredType,
        out (ITypeSymbol Left, ITypeSymbol Right) conflict)
    {
        inferredType = Compilation.ErrorTypeSymbol;
        conflict = (Compilation.ErrorTypeSymbol, Compilation.ErrorTypeSymbol);

        if (types.Count == 0)
            return false;

        inferredType = types[0];

        for (var i = 1; i < types.Count; i++)
        {
            if (!TryInferBestCommonType(inferredType, types[i], out var mergedType))
            {
                conflict = (inferredType, types[i]);
                return false;
            }

            inferredType = mergedType;
        }

        if (IsDisallowedImplicitCollectionInferenceType(inferredType))
        {
            conflict = types.Count > 1 ? (types[0], types[1]) : (inferredType, inferredType);
            return false;
        }

        return true;
    }

    private static bool IsDisallowedImplicitCollectionInferenceType(ITypeSymbol type)
    {
        return type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType ||
               type.TypeKind == TypeKind.Interface;
    }

    private bool TryBindCollectionBuilderInvocation(
        SyntaxNode syntax,
        ITypeSymbol targetType,
        IReadOnlyList<BoundExpression> elements,
        IReadOnlyList<SyntaxNode> elementNodes,
        out BoundExpression result)
    {
        result = null!;

        if (targetType is not INamedTypeSymbol namedTarget)
            return false;

        if (!TryGetCollectionBuilderInfo(namedTarget, out var builderType, out var methodName))
            return false;

        if (elements.Any(static element => element is BoundSpreadElement))
            return false;

        var candidates = builderType
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(static method => method.IsStatic)
            .ToImmutableArray();

        if (candidates.IsDefaultOrEmpty)
            return false;

        var accessibleCandidates = GetAccessibleMethods(candidates, syntax.GetLocation(), reportIfInaccessible: false);
        if (accessibleCandidates.IsDefaultOrEmpty)
            return false;

        accessibleCandidates = PreferCollectionBuilderTargetTypeCandidates(accessibleCandidates, namedTarget);

        var arguments = CreateCollectionBuilderArguments(syntax, namedTarget, elements, elementNodes);

        var resolution = OverloadResolver.ResolveOverload(
            accessibleCandidates,
            arguments,
            Compilation,
            binder: this,
            canBindLambda: EnsureLambdaCompatible,
            callSyntax: syntax);

        var method = resolution.Success
            ? resolution.Method!
            : SelectCollectionBuilderMethodFallback(accessibleCandidates, arguments, namedTarget);
        if (method is null)
            return false;

        var convertedArguments = ConvertArguments(method.Parameters, arguments, syntax);

        BoundExpression invocation = new BoundInvocationExpression(method, convertedArguments);
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, namedTarget))
        {
            var conversion = Compilation.ClassifyConversion(method.ReturnType, namedTarget);
            if (!conversion.Exists || !conversion.IsImplicit)
                return false;

            invocation = ApplyConversion(invocation, namedTarget, conversion, syntax);
        }

        ReportObsoleteIfNeeded(method, syntax.GetLocation());
        result = invocation;
        return true;
    }

    private BoundArgument[] CreateCollectionBuilderArguments(
        SyntaxNode syntax,
        INamedTypeSymbol targetType,
        IReadOnlyList<BoundExpression> elements,
        IReadOnlyList<SyntaxNode> elementNodes)
    {
        var arguments = new BoundArgument[elements.Count];
        var hasTargetElementType = TryGetCollectionBuilderElementType(targetType, out var targetElementType);

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var elementSyntax = elementNodes[i];

            if (hasTargetElementType &&
                element.Type is not null &&
                targetElementType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(element) &&
                IsAssignable(targetElementType, element.Type, out var conversion))
            {
                element = ApplyConversion(element, targetElementType, conversion, elementSyntax);
            }

            arguments[i] = new BoundArgument(element, RefKind.None, null, elementSyntax);
        }

        return arguments;
    }

    private ImmutableArray<IMethodSymbol> PreferCollectionBuilderTargetTypeCandidates(
        ImmutableArray<IMethodSymbol> candidates,
        INamedTypeSymbol targetType)
    {
        if (candidates.IsDefaultOrEmpty || targetType.TypeArguments.IsDefaultOrEmpty)
            return candidates;

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>(candidates.Length);

        foreach (var candidate in candidates)
        {
            if (TryConstructCollectionBuilderMethodFromTargetType(candidate, targetType, out var constructed))
                builder.Add(constructed);
            else
                builder.Add(candidate);
        }

        return DistinctMethodCandidates(builder.ToImmutable());
    }

    private bool TryConstructCollectionBuilderMethodFromTargetType(
        IMethodSymbol candidate,
        INamedTypeSymbol targetType,
        out IMethodSymbol constructed)
    {
        constructed = null!;

        if (candidate.TypeParameters.Length != targetType.TypeArguments.Length ||
            candidate.ReturnType is not INamedTypeSymbol returnType ||
            !string.Equals(
                returnType.OriginalDefinition.ToFullyQualifiedMetadataName(),
                targetType.OriginalDefinition.ToFullyQualifiedMetadataName(),
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            constructed = candidate.Construct(targetType.TypeArguments.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IMethodSymbol? SelectCollectionBuilderMethodFallback(
        ImmutableArray<IMethodSymbol> candidates,
        BoundArgument[] arguments,
        INamedTypeSymbol targetType)
    {
        IMethodSymbol? bestMethod = null;
        var bestScore = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var method = OverloadResolver.ApplyTypeArgumentInference(
                candidate,
                receiver: null,
                arguments,
                Compilation,
                this,
                explicitTypeArguments: ImmutableArray<ITypeSymbol>.Empty);
            if (method is null)
                continue;

            if (!OverloadResolver.TryMapArguments(
                    method.Parameters,
                    arguments,
                    treatAsExtension: false,
                    out var mappedArguments,
                    out var paramsArguments,
                    out var paramsParameterIndex))
            {
                continue;
            }

            var score = 0;
            var valid = true;

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                if (i == paramsParameterIndex)
                {
                    if (mappedArguments[i] is not null)
                    {
                        var argument = mappedArguments[i]!.Value;
                        if (argument.IsSpread ||
                            argument.Type is null ||
                            !IsAssignable(parameter.Type, argument.Type, out var conversion))
                        {
                            valid = false;
                            break;
                        }

                        if (!conversion.IsIdentity)
                            score++;

                        continue;
                    }

                    if (!TryGetVarParamsElementType(parameter.Type, out var elementType))
                    {
                        valid = false;
                        break;
                    }

                    foreach (var paramsArgument in paramsArguments)
                    {
                        if (paramsArgument.IsSpread ||
                            paramsArgument.Type is null ||
                            !IsAssignable(elementType, paramsArgument.Type, out var conversion))
                        {
                            valid = false;
                            break;
                        }

                        if (!conversion.IsIdentity)
                            score++;
                    }

                    if (!valid)
                        break;

                    continue;
                }

                var mapped = mappedArguments[i];
                if (mapped is null)
                    continue;

                var boundArgument = mapped.Value;
                if (boundArgument.IsSpread ||
                    boundArgument.Type is null ||
                    !IsAssignable(parameter.Type, boundArgument.Type, out var parameterConversion))
                {
                    valid = false;
                    break;
                }

                if (!parameterConversion.IsIdentity)
                    score++;
            }

            if (!valid)
                continue;

            var returnConversion = Compilation.ClassifyConversion(method.ReturnType, targetType);
            if (!returnConversion.Exists || !returnConversion.IsImplicit)
                continue;

            if (!returnConversion.IsIdentity)
                score += 2;

            if (method.Parameters.Length > 0 && method.Parameters[^1].IsVarParams)
                score--;

            if (method.Parameters.Length == 1 &&
                method.Parameters[0].Type is INamedTypeSymbol singleParamType &&
                string.Equals(singleParamType.Name, "ReadOnlySpan", StringComparison.Ordinal))
            {
                score += 3;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestMethod = method;
            }
        }

        return bestMethod;
    }

    private bool TryGetCollectionBuilderInfo(
        INamedTypeSymbol targetType,
        out INamedTypeSymbol builderType,
        out string methodName)
    {
        var targetDefinition = (targetType.OriginalDefinition as INamedTypeSymbol) ?? targetType;

        if (TryGetCollectionBuilderInfoFromAttributes(targetDefinition.GetAttributes(), out builderType, out methodName))
            return true;

        var peTargetType = TryGetMetadataNamedType(targetDefinition);
        if (peTargetType is null)
        {
            builderType = null!;
            methodName = string.Empty;
            return false;
        }

        foreach (var attribute in PENamedTypeSymbol.GetCustomAttributesSafe(peTargetType.GetTypeInfo()))
        {
            if (!string.Equals(attribute.AttributeType.FullName, "System.Runtime.CompilerServices.CollectionBuilderAttribute", StringComparison.Ordinal))
                continue;

            try
            {
                if (attribute.ConstructorArguments.Count < 2)
                    continue;

                var builderTypeArgument = attribute.ConstructorArguments[0].Value;
                var builderRuntimeType = builderTypeArgument switch
                {
                    Type runtimeType => runtimeType,
                    _ => null
                };

                if (builderRuntimeType is null ||
                    attribute.ConstructorArguments[1].Value is not string builderMethodName)
                {
                    continue;
                }

                var builderMetadataName = GetRuntimeMetadataName(builderRuntimeType);
                var resolvedBuilder = Compilation.GetTypeByMetadataName(builderMetadataName);
                if (resolvedBuilder is not INamedTypeSymbol namedBuilder)
                    continue;

                builderType = namedBuilder;
                methodName = builderMethodName;
                return true;
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        if (TryGetRuntimeCollectionBuilderInfo(targetDefinition, out builderType, out methodName))
            return true;

        if (TryGetCollectionBuilderInfoByConvention(targetDefinition, out builderType, out methodName))
            return true;

        builderType = null!;
        methodName = string.Empty;
        return false;
    }

    private static bool TryGetCollectionBuilderElementType(INamedTypeSymbol targetType, out ITypeSymbol elementType)
    {
        if (targetType.Arity == 1 && targetType.TypeArguments.Length == 1)
        {
            elementType = targetType.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private bool TryGetRuntimeCollectionBuilderInfo(
        INamedTypeSymbol targetDefinition,
        out INamedTypeSymbol builderType,
        out string methodName)
    {
        builderType = null!;
        methodName = string.Empty;

        if (!TryGetRuntimeTypeByMetadataName(targetDefinition.ToFullyQualifiedMetadataName(), out var runtimeTargetType))
            return false;

        foreach (var attribute in runtimeTargetType.GetCustomAttributesData())
        {
            if (!string.Equals(attribute.AttributeType.FullName, "System.Runtime.CompilerServices.CollectionBuilderAttribute", StringComparison.Ordinal))
                continue;

            if (attribute.ConstructorArguments.Count < 2)
                continue;

            if (attribute.ConstructorArguments[0].Value is not Type runtimeBuilderType ||
                attribute.ConstructorArguments[1].Value is not string runtimeMethodName)
            {
                continue;
            }

            var builderMetadataName = GetRuntimeMetadataName(runtimeBuilderType);
            if (Compilation.GetTypeByMetadataName(builderMetadataName) is not INamedTypeSymbol namedBuilder)
                continue;

            builderType = namedBuilder;
            methodName = runtimeMethodName;
            return true;
        }

        return false;
    }

    private bool TryBindCollectionExpressionViaImplicitEnumerableConversion(
        SyntaxNode syntax,
        ITypeSymbol targetType,
        IReadOnlyList<BoundExpression> elements,
        IReadOnlyList<SyntaxNode> elementNodes,
        out BoundExpression result)
    {
        result = null!;

        if (targetType is not INamedTypeSymbol namedTarget ||
            !TryGetIEnumerableElementType(namedTarget, out var enumerableElementType))
        {
            return false;
        }

        var arrayType = Compilation.CreateArrayTypeSymbol(enumerableElementType);
        var conversion = Compilation.ClassifyConversion(arrayType, targetType);
        if (!conversion.Exists || !conversion.IsImplicit)
            return false;

        var converted = ImmutableArray.CreateBuilder<BoundExpression>(elements.Count);

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            var elementSyntax = elementNodes[i];

            if (element is BoundSpreadElement spread)
            {
                var sourceType = GetSpreadElementType(spread.Expression.Type!);
                if (!IsAssignable(enumerableElementType, sourceType, out _))
                {
                    ReportCannotConvertFromTypeToType(
                        sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        enumerableElementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        syntax.GetLocation());
                }

                converted.Add(element);
                continue;
            }

            if (enumerableElementType.TypeKind != TypeKind.Error &&
                ShouldAttemptConversion(element))
            {
                if (!IsAssignable(enumerableElementType, element.Type!, out var elementConversion))
                {
                    ReportCannotConvertFromTypeToType(
                        element.Type!.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        enumerableElementType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        syntax.GetLocation());
                }
                else
                {
                    converted.Add(ApplyConversion(element, enumerableElementType, elementConversion, elementSyntax));
                    continue;
                }
            }

            converted.Add(element);
        }

        var arrayExpression = new BoundCollectionExpression(arrayType, converted.ToImmutable());
        result = ApplyConversion(arrayExpression, targetType, conversion, syntax);
        return true;
    }

    private bool TryGetCollectionBuilderInfoByConvention(
        INamedTypeSymbol targetDefinition,
        out INamedTypeSymbol builderType,
        out string methodName)
    {
        builderType = null!;
        methodName = string.Empty;

        if (!TryGetCollectionBuilderElementType(targetDefinition, out _))
            return false;

        if (string.IsNullOrEmpty(targetDefinition.ContainingNamespace?.ToDisplayString()))
            return false;

        var builderMetadataName = $"{targetDefinition.ContainingNamespace.ToDisplayString()}.{targetDefinition.Name}";
        if (Compilation.GetTypeByMetadataName(builderMetadataName) is not INamedTypeSymbol candidateBuilder)
            return false;

        var createMethods = candidateBuilder
            .GetMembers("Create")
            .OfType<IMethodSymbol>()
            .Where(static method => method.IsStatic);

        foreach (var method in createMethods)
        {
            var returnType = method.ReturnType as INamedTypeSymbol;
            if (returnType is null)
                continue;

            if (string.Equals(
                    returnType.OriginalDefinition.ToFullyQualifiedMetadataName(),
                    targetDefinition.ToFullyQualifiedMetadataName(),
                    StringComparison.Ordinal))
            {
                builderType = candidateBuilder;
                methodName = "Create";
                return true;
            }
        }

        return false;
    }

    private static bool TryGetRuntimeTypeByMetadataName(string metadataName, out Type runtimeType)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var candidate = assembly.GetType(metadataName, throwOnError: false, ignoreCase: false);
                if (candidate is not null)
                {
                    runtimeType = candidate;
                    return true;
                }
            }
            catch
            {
                // Ignore reflection load failures and continue probing loaded assemblies.
            }
        }

        runtimeType = null!;
        return false;
    }

    private static PENamedTypeSymbol? TryGetMetadataNamedType(INamedTypeSymbol type)
    {
        var seen = new HashSet<INamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<INamedTypeSymbol>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
                continue;

            if (current is PENamedTypeSymbol pe)
                return pe;

            if (current.OriginalDefinition is INamedTypeSymbol original &&
                !ReferenceEquals(original, current))
            {
                queue.Enqueue(original);
            }

            if (current.ConstructedFrom is INamedTypeSymbol constructedFrom &&
                !ReferenceEquals(constructedFrom, current))
            {
                queue.Enqueue(constructedFrom);
            }
        }

        return null;
    }

    private static bool TryGetCollectionBuilderInfoFromAttributes(
        ImmutableArray<AttributeData> attributes,
        out INamedTypeSymbol builderType,
        out string methodName)
    {
        foreach (var attribute in attributes)
        {
            if (!string.Equals(attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "System.Runtime.CompilerServices.CollectionBuilderAttribute", StringComparison.Ordinal))
                continue;

            if (attribute.ConstructorArguments.Length != 2)
                continue;

            var typeArgument = attribute.ConstructorArguments[0];
            var methodArgument = attribute.ConstructorArguments[1];

            if (typeArgument.Kind != TypedConstantKind.Type ||
                typeArgument.Value is not INamedTypeSymbol namedBuilder ||
                methodArgument.Value is not string builderMethodName)
            {
                continue;
            }

            builderType = namedBuilder;
            methodName = builderMethodName;
            return true;
        }

        builderType = null!;
        methodName = string.Empty;
        return false;
    }

    private static string GetRuntimeMetadataName(Type runtimeType)
    {
        if (runtimeType.DeclaringType is { } declaringType)
            return $"{GetRuntimeMetadataName(declaringType)}+{runtimeType.Name}";

        return runtimeType.FullName ?? runtimeType.Name;
    }

    private BoundExpression BindCollectionComprehensionElement(
        CollectionComprehensionElementSyntax syntax,
        ITypeSymbol? targetElementType)
    {
        BoundExpression source;
        ITypeSymbol iterationType;

        if (syntax.Source is RangeExpressionSyntax rangeSource &&
            TryBindCollectionComprehensionRangeSource(rangeSource, out var loweredRangeSource, out var rangeIterationType))
        {
            source = loweredRangeSource;
            iterationType = rangeIterationType;
            CacheBoundNode(syntax.Source, source);
        }
        else
        {
            source = BindExpressionWithoutTargetType(syntax.Source);
            if (HasExpressionErrors(source))
                return AsErrorExpression(source);

            var sourceType = source.Type ?? Compilation.ErrorTypeSymbol;
            if (!IsSpreadEnumerable(sourceType))
            {
                _diagnostics.ReportSpreadSourceMustBeEnumerable(
                    sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    syntax.Source.GetLocation());
            }

            iterationType = GetSpreadElementType(sourceType);
        }

        var (iterationLocal, patternCondition, condition, selector, _, _) = BindComprehensionTargetAndBody(
            syntax,
            syntax.BindingKeyword,
            syntax.Target,
            syntax.Condition,
            syntax.Selector,
            iterationType,
            keySelectorSyntax: null,
            valueSelectorSyntax: null,
            targetKeyType: targetElementType,
            targetValueType: null,
            out _,
            out _);

        if (patternCondition is not null)
            condition = CombineBooleanConditions(patternCondition, condition);

        var selectorType = selector.Type ?? Compilation.ErrorTypeSymbol;
        var resultArrayType = Compilation.CreateArrayTypeSymbol(selectorType);
        var comprehension = new BoundCollectionComprehensionExpression(
            resultArrayType,
            source,
            iterationLocal,
            condition,
            selector,
            selectorType);

        return new BoundSpreadElement(comprehension);
    }

    private DictionaryComprehensionBinding BindDictionaryComprehensionElement(
        DictionaryComprehensionElementSyntax syntax,
        ITypeSymbol? targetKeyType,
        ITypeSymbol? targetValueType)
    {
        BoundExpression source;
        ITypeSymbol iterationType;

        if (syntax.Source is RangeExpressionSyntax rangeSource &&
            TryBindCollectionComprehensionRangeSource(rangeSource, out var loweredRangeSource, out var rangeIterationType))
        {
            source = loweredRangeSource;
            iterationType = rangeIterationType;
            CacheBoundNode(syntax.Source, source);
        }
        else
        {
            source = BindExpressionWithoutTargetType(syntax.Source);
            if (HasExpressionErrors(source))
                return new DictionaryComprehensionBinding(
                    AsErrorExpression(source),
                    CreateTempLocal("__error", Compilation.ErrorTypeSymbol, syntax),
                    condition: null,
                    ErrorExpression(reason: BoundExpressionReason.TypeMismatch),
                    ErrorExpression(reason: BoundExpressionReason.TypeMismatch),
                    Compilation.ErrorTypeSymbol,
                    Compilation.ErrorTypeSymbol);

            var sourceType = source.Type ?? Compilation.ErrorTypeSymbol;
            if (!IsSpreadEnumerable(sourceType))
            {
                _diagnostics.ReportSpreadSourceMustBeEnumerable(
                    sourceType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    syntax.Source.GetLocation());
            }

            iterationType = GetSpreadElementType(sourceType);
        }

        var (iterationLocal, patternCondition, condition, _, keySelector, valueSelector) = BindComprehensionTargetAndBody(
            syntax,
            syntax.BindingKeyword,
            syntax.Target,
            syntax.Condition,
            selectorSyntax: null,
            iterationType,
            syntax.KeySelector,
            syntax.ValueSelector,
            targetKeyType,
            targetValueType,
            out _,
            out _);

        if (patternCondition is not null)
            condition = CombineBooleanConditions(patternCondition, condition);

        var keyType = targetKeyType is not null && targetKeyType.TypeKind != TypeKind.Error
            ? targetKeyType
            : keySelector.Type ?? Compilation.ErrorTypeSymbol;
        var valueType = targetValueType is not null && targetValueType.TypeKind != TypeKind.Error
            ? targetValueType
            : valueSelector.Type ?? Compilation.ErrorTypeSymbol;

        var iterationName = syntax.Target is IdentifierNameSyntax identifierTarget && !string.IsNullOrWhiteSpace(identifierTarget.Identifier.ValueText)
            ? identifierTarget.Identifier.ValueText
            : iterationLocal.Name;
        if (condition is not null)
            condition = RewriteDictionaryComprehensionIterationAccess(condition, iterationName, iterationLocal);
        keySelector = RewriteDictionaryComprehensionIterationAccess(keySelector, iterationName, iterationLocal);
        valueSelector = RewriteDictionaryComprehensionIterationAccess(valueSelector, iterationName, iterationLocal);

        var keyValuePairDefinition = Compilation.GetTypeByMetadataName("System.Collections.Generic.KeyValuePair`2") as INamedTypeSymbol;
        if (keyValuePairDefinition is null)
            return new DictionaryComprehensionBinding(
                ErrorExpression(reason: BoundExpressionReason.TypeMismatch),
                iterationLocal,
                condition,
                ErrorExpression(reason: BoundExpressionReason.TypeMismatch),
                ErrorExpression(reason: BoundExpressionReason.TypeMismatch),
                Compilation.ErrorTypeSymbol,
                Compilation.ErrorTypeSymbol);

        return new DictionaryComprehensionBinding(
            source,
            iterationLocal,
            condition,
            keySelector,
            valueSelector,
            keyType,
            valueType);
    }

    private (
        ILocalSymbol IterationLocal,
        BoundExpression? PatternCondition,
        BoundExpression? Condition,
        BoundExpression Selector,
        BoundExpression KeySelector,
        BoundExpression ValueSelector)
        BindComprehensionTargetAndBody(
            SyntaxNode syntax,
            SyntaxToken bindingKeyword,
            ExpressionOrPatternSyntax targetSyntax,
            ExpressionSyntax? conditionSyntax,
            ExpressionSyntax? selectorSyntax,
            ITypeSymbol iterationType,
            ExpressionSyntax? keySelectorSyntax,
            ExpressionSyntax? valueSelectorSyntax,
            ITypeSymbol? targetKeyType,
            ITypeSymbol? targetValueType,
            out ITypeSymbol? selectorType,
            out ImmutableArray<ILocalSymbol> patternLocals)
    {
        selectorType = null;
        patternLocals = ImmutableArray<ILocalSymbol>.Empty;

        var iterationName = targetSyntax switch
        {
            IdentifierNameSyntax identifier when !string.IsNullOrWhiteSpace(identifier.Identifier.ValueText) => identifier.Identifier.ValueText,
            _ => $"__item{_tempCounter++}"
        };

        var iterationLocal = CreateTempLocal(iterationName, iterationType, syntax);
        var priorDepth = _scopeDepth;
        _scopeDepth = priorDepth + 1;
        var currentDepth = _scopeDepth;
        var shadowedLocals = new Dictionary<string, (ILocalSymbol Symbol, int Depth)?>(StringComparer.Ordinal);

        BoundExpression? patternCondition = null;
        BoundExpression? condition = null;
        BoundExpression selector = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        BoundExpression keySelector = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        BoundExpression valueSelector = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        try
        {
            switch (targetSyntax)
            {
                case IdentifierNameSyntax identifierName:
                    if (bindingKeyword.Kind == SyntaxKind.VarKeyword)
                    {
                        _diagnostics.ReportForIdentifierBindingKeywordMustBeValOrLet(
                            identifierName.Identifier.ValueText,
                            bindingKeyword.GetLocation());
                    }

                    RegisterComprehensionLocal(identifierName.Identifier.ValueText, iterationLocal, currentDepth, shadowedLocals);
                    OnComprehensionTargetBound(identifierName, iterationLocal);
                    break;

                case DiscardPatternSyntax:
                    break;

                case PatternSyntax patternSyntax:
                    {
                        CapturePatternLocalShadows(patternSyntax, shadowedLocals);

                        var inlineBindingKeyword = FindFirstInlinePatternBindingKeyword(patternSyntax);
                        if (inlineBindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword &&
                            bindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                        {
                            _diagnostics.ReportPatternDeclarationBindingKeywordConflict(
                                bindingKeyword.Text,
                                inlineBindingKeyword.Text,
                                inlineBindingKeyword.GetLocation());
                        }

                        var previousBindingKeyword = _ambientPatternDeclarationBindingKeyword;
                        _ambientPatternDeclarationBindingKeyword = bindingKeyword.Kind;
                        BoundPattern pattern;
                        try
                        {
                            pattern = BindPattern(patternSyntax, iterationType);
                        }
                        finally
                        {
                            _ambientPatternDeclarationBindingKeyword = previousBindingKeyword;
                        }

                        patternLocals = CollectPatternDesignatorLocals(pattern);
                        patternCondition = new BoundIsPatternExpression(
                            new BoundLocalAccess(iterationLocal),
                            pattern,
                            Compilation.GetSpecialType(SpecialType.System_Boolean));
                        break;
                    }

                default:
                    _diagnostics.ReportLeftOfAssignmentMustBeAVariablePropertyOrIndexer(targetSyntax.GetLocation());
                    break;
            }

            if (conditionSyntax is not null)
            {
                using var _ = PushTargetType(null);
                condition = BindBooleanComprehensionCondition(conditionSyntax);
            }

            if (selectorSyntax is not null)
            {
                selector = targetKeyType is not null && targetKeyType.TypeKind != TypeKind.Error
                    ? BindExpressionWithTargetType(selectorSyntax, targetKeyType)
                    : BindExpressionWithoutTargetType(selectorSyntax);
                selectorType = selector.Type ?? Compilation.ErrorTypeSymbol;
            }

            if (keySelectorSyntax is not null)
            {
                keySelector = targetKeyType is not null && targetKeyType.TypeKind != TypeKind.Error
                    ? BindExpressionWithTargetType(keySelectorSyntax, targetKeyType)
                    : BindExpressionWithoutTargetType(keySelectorSyntax);

                if (targetKeyType is not null &&
                    targetKeyType.TypeKind != TypeKind.Error &&
                    keySelector.Type is not null &&
                    keySelector.Type.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(keySelector) &&
                    IsAssignable(targetKeyType, keySelector.Type, out var keyConversion))
                {
                    keySelector = ApplyConversion(keySelector, targetKeyType, keyConversion, keySelectorSyntax);
                }
            }

            if (valueSelectorSyntax is not null)
            {
                valueSelector = targetValueType is not null && targetValueType.TypeKind != TypeKind.Error
                    ? BindExpressionWithTargetType(valueSelectorSyntax, targetValueType)
                    : BindExpressionWithoutTargetType(valueSelectorSyntax);

                if (targetValueType is not null &&
                    targetValueType.TypeKind != TypeKind.Error &&
                    valueSelector.Type is not null &&
                    valueSelector.Type.TypeKind != TypeKind.Error &&
                    ShouldAttemptConversion(valueSelector) &&
                    IsAssignable(targetValueType, valueSelector.Type, out var valueConversion))
                {
                    valueSelector = ApplyConversion(valueSelector, targetValueType, valueConversion, valueSelectorSyntax);
                }
            }
        }
        finally
        {
            foreach (var entry in shadowedLocals)
            {
                if (entry.Value is { } shadowed)
                    _locals[entry.Key] = shadowed;
                else
                    _locals.Remove(entry.Key);
            }

            _scopeDepth = priorDepth;
        }

        return (iterationLocal, patternCondition, condition, selector, keySelector, valueSelector);
    }

    private BoundExpression BindBooleanComprehensionCondition(ExpressionSyntax conditionSyntax)
    {
        var condition = BindExpression(conditionSyntax);
        var boolType = Compilation.GetSpecialType(SpecialType.System_Boolean);
        if (condition.Type is not null &&
            condition.Type.TypeKind != TypeKind.Error &&
            !SymbolEqualityComparer.Default.Equals(condition.Type, boolType))
        {
            if (!IsAssignable(boolType, condition.Type, out var conversion))
            {
                ReportCannotConvertFromTypeToType(
                    condition.Type.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    boolType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    conditionSyntax.GetLocation());
            }
            else
            {
                condition = ApplyConversion(condition, boolType, conversion, conditionSyntax);
            }
        }

        return condition;
    }

    private BoundExpression? CombineBooleanConditions(BoundExpression? left, BoundExpression? right)
    {
        if (left is null)
            return right;

        if (right is null)
            return left;

        if (!BoundBinaryOperator.TryLookup(
                Compilation,
                SyntaxKind.AmpersandAmpersandToken,
                Compilation.GetSpecialType(SpecialType.System_Boolean),
                Compilation.GetSpecialType(SpecialType.System_Boolean),
                out var op))
        {
            return right;
        }

        return new BoundBinaryExpression(left, op, right);
    }

    private void RegisterComprehensionLocal(
        string name,
        ILocalSymbol local,
        int depth,
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?> shadowedLocals)
    {
        if (!shadowedLocals.ContainsKey(name))
            shadowedLocals[name] = _locals.TryGetValue(name, out var existing) ? existing : null;

        _locals[name] = (local, depth);
    }

    private static ImmutableArray<ILocalSymbol> CollectPatternDesignatorLocals(BoundPattern pattern)
    {
        var builder = ImmutableArray.CreateBuilder<ILocalSymbol>();
        foreach (var designator in pattern.GetDesignators())
        {
            if (designator is BoundSingleVariableDesignator single)
                builder.Add(single.Local);
        }

        return builder.ToImmutable();
    }

    private void CapturePatternLocalShadows(
        PatternSyntax patternSyntax,
        Dictionary<string, (ILocalSymbol Symbol, int Depth)?> shadowedLocals)
    {
        foreach (var designation in patternSyntax.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
        {
            var name = designation.Identifier.ValueText;
            if (string.IsNullOrEmpty(name) || name == "_" || shadowedLocals.ContainsKey(name))
                continue;

            shadowedLocals[name] = _locals.TryGetValue(name, out var existing) ? existing : null;
        }
    }

    protected virtual void OnComprehensionTargetBound(IdentifierNameSyntax syntax, ILocalSymbol local)
    {
    }

    private static BoundExpression RewriteDictionaryComprehensionIterationAccess(
        BoundExpression expression,
        string iterationName,
        ILocalSymbol iterationLocal)
    {
        var rewritten = new DictionaryComprehensionIterationLocalRewriter(iterationName, iterationLocal).Visit(expression);
        return (BoundExpression)rewritten!;
    }

    private sealed class DictionaryComprehensionIterationLocalRewriter : BoundTreeRewriter
    {
        private readonly string _iterationName;
        private readonly ILocalSymbol _iterationLocal;

        public DictionaryComprehensionIterationLocalRewriter(string iterationName, ILocalSymbol iterationLocal)
        {
            _iterationName = iterationName;
            _iterationLocal = iterationLocal;
        }

        public override BoundNode? VisitLocalAccess(BoundLocalAccess node)
        {
            if (string.Equals(node.Local.Name, _iterationName, StringComparison.Ordinal) ||
                string.Equals(node.Local.Name, _iterationLocal.Name, StringComparison.Ordinal) ||
                node.Local.Name.Contains(_iterationName, StringComparison.Ordinal))
            {
                return new BoundLocalAccess(_iterationLocal, node.Reason);
            }

            return node;
        }
    }

    private bool TryBindCollectionRangeElement(RangeExpressionSyntax rangeSyntax, out BoundExpression result)
    {
        result = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);

        if (!TryBindCollectionComprehensionRangeSource(rangeSyntax, out var loweredRangeSource, out var iterationType))
            return false;

        var iterationLocal = CreateTempLocal("__rangeItem", iterationType, rangeSyntax);
        var comprehension = new BoundCollectionComprehensionExpression(
            Compilation.CreateArrayTypeSymbol(iterationType),
            loweredRangeSource,
            iterationLocal,
            condition: null,
            selector: new BoundLocalAccess(iterationLocal),
            iterationType);

        result = new BoundSpreadElement(comprehension);
        CacheBoundNode(rangeSyntax, loweredRangeSource);
        return true;
    }

    private bool TryBindCollectionComprehensionRangeSource(
        RangeExpressionSyntax rangeSyntax,
        out BoundExpression loweredSource,
        out ITypeSymbol iterationType)
    {
        loweredSource = ErrorExpression(reason: BoundExpressionReason.TypeMismatch);
        iterationType = Compilation.ErrorTypeSymbol;

        var rangeInfo = BindCollectionComprehensionRangeInfo(rangeSyntax);
        if (!rangeInfo.IsValid)
            return false;

        var rangeExpression = new BoundRangeExpression(
            new BoundIndexExpression(rangeInfo.Start!, isFromEnd: false, GetIndexType()),
            new BoundIndexExpression(rangeInfo.End!, isFromEnd: false, GetIndexType()),
            GetRangeType(),
            rangeSyntax.LessThanToken.Kind == SyntaxKind.LessThanToken);

        loweredSource = rangeExpression;
        iterationType = rangeInfo.ElementType!;
        return true;
    }

    private ComprehensionRangeInfo BindCollectionComprehensionRangeInfo(RangeExpressionSyntax rangeSyntax)
    {
        var start = BindForRangeBoundary(rangeSyntax.LeftExpression);
        if (start is BoundErrorExpression)
            return ComprehensionRangeInfo.Invalid;

        var end = BindForRangeBoundary(rangeSyntax.RightExpression);
        if (end is BoundErrorExpression)
            return ComprehensionRangeInfo.Invalid;

        if (end is null)
        {
            _diagnostics.ReportRangeForLoopRequiresEnd(rangeSyntax.GetLocation());
            return ComprehensionRangeInfo.Invalid;
        }

        var endType = TypeSymbolNormalization.NormalizeForInference(end.Type ?? Compilation.ErrorTypeSymbol);
        var startType = TypeSymbolNormalization.NormalizeForInference(start?.Type ?? endType);

        if (!TryInferBestCommonType(startType, endType, out var elementType))
        {
            ReportCannotConvertFromTypeToType(
                startType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                endType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                rangeSyntax.GetLocation());
            return ComprehensionRangeInfo.Invalid;
        }

        elementType = elementType.UnwrapLiteralType() ?? elementType;
        if (!IsSupportedForRangeLoopType(elementType))
        {
            ReportCannotConvertFromTypeToType(
                elementType.ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Compilation.GetSpecialType(SpecialType.System_Int32).ToDisplayStringForTypeMismatchDiagnostic(SymbolDisplayFormat.MinimallyQualifiedFormat),
                rangeSyntax.GetLocation());
            return ComprehensionRangeInfo.Invalid;
        }

        var startValue = start ?? CreateZeroForRangeLoop(elementType);
        if (!TryConvertForRangeBoundary(startValue, elementType, rangeSyntax.LeftExpression ?? rangeSyntax, out var convertedStart))
            return ComprehensionRangeInfo.Invalid;

        if (!TryConvertForRangeBoundary(end, elementType, rangeSyntax.RightExpression!, out var convertedEnd))
            return ComprehensionRangeInfo.Invalid;

        return new ComprehensionRangeInfo(true, convertedStart, convertedEnd, elementType);
    }

    private static bool TryGetStaticallyKnownCollectionLength(
        IReadOnlyList<BoundExpression> elements,
        out int knownLength)
    {
        knownLength = 0;

        foreach (var element in elements)
        {
            switch (element)
            {
                case BoundSpreadElement { Expression.Type: IArrayTypeSymbol { FixedLength: int spreadLength, Rank: 1 } }:
                    knownLength += spreadLength;
                    break;
                case BoundSpreadElement { Expression: BoundCollectionComprehensionExpression comprehension }
                    when TryGetStaticallyKnownCollectionComprehensionLength(comprehension, out var comprehensionLength):
                    knownLength += comprehensionLength;
                    break;
                case BoundSpreadElement:
                case BoundCollectionComprehensionExpression:
                    knownLength = 0;
                    return false;
                default:
                    knownLength++;
                    break;
            }
        }

        return true;
    }

    private static bool TryGetStaticallyKnownCollectionComprehensionLength(
        BoundCollectionComprehensionExpression comprehension,
        out int knownLength)
    {
        knownLength = 0;

        if (comprehension.Condition is not null ||
            comprehension.Source is not BoundRangeExpression range ||
            comprehension.Selector is not BoundLocalAccess selectorLocal ||
            !SymbolEqualityComparer.Default.Equals(selectorLocal.Local, comprehension.IterationLocal))
        {
            return false;
        }

        return TryGetStaticallyKnownRangeLength(range, out knownLength);
    }

    private static bool TryGetStaticallyKnownRangeLength(BoundRangeExpression range, out int knownLength)
    {
        knownLength = 0;

        if (range.Left is not { IsFromEnd: false, Value: var startValue } ||
            range.Right is not { IsFromEnd: false, Value: var endValue })
        {
            return false;
        }

        if (!TryGetCountableRangeEndpoint(startValue, out var start) ||
            !TryGetCountableRangeEndpoint(endValue, out var end))
        {
            return false;
        }

        if (start.Kind != end.Kind)
            return false;

        var distance = end.Value - start.Value;
        if (distance < 0)
        {
            knownLength = 0;
            return true;
        }

        knownLength = checked((int)(range.IsUpperExclusive ? distance : distance + 1));
        return true;
    }

    private static bool TryGetCountableRangeEndpoint(BoundExpression expression, out (CountableRangeEndpointKind Kind, long Value) endpoint)
    {
        endpoint = default;

        if (!TryGetExpressionConstantValue(expression, out var value))
            return false;

        switch (value)
        {
            case byte byteValue:
                endpoint = (CountableRangeEndpointKind.Integral, byteValue);
                return true;
            case sbyte sbyteValue:
                endpoint = (CountableRangeEndpointKind.Integral, sbyteValue);
                return true;
            case short shortValue:
                endpoint = (CountableRangeEndpointKind.Integral, shortValue);
                return true;
            case ushort ushortValue:
                endpoint = (CountableRangeEndpointKind.Integral, ushortValue);
                return true;
            case int intValue:
                endpoint = (CountableRangeEndpointKind.Integral, intValue);
                return true;
            case uint uintValue:
                endpoint = (CountableRangeEndpointKind.Integral, uintValue);
                return true;
            case long longValue:
                endpoint = (CountableRangeEndpointKind.Integral, longValue);
                return true;
            case char charValue:
                endpoint = (CountableRangeEndpointKind.Char, charValue);
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetExpressionConstantValue(BoundExpression expression, out object? value)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                value = literal.Value;
                return true;
            case BoundConversionExpression conversion:
                return TryGetExpressionConstantValue(conversion.Expression, out value);
            case BoundLocalAccess localAccess:
                value = localAccess.Local.ConstantValue;
                return value is not null;
            case BoundFieldAccess fieldAccess:
                value = fieldAccess.Field.GetConstantValue();
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    private bool TryGetConcreteSpreadCollectionType(ITypeSymbol spreadType, out ITypeSymbol concreteType)
    {
        concreteType = null!;

        var normalized = TypeSymbolNormalization.NormalizeForInference(spreadType);
        if (normalized.TypeKind == TypeKind.Error)
            return false;

        if (normalized is IArrayTypeSymbol { Rank: 1 } arrayType)
        {
            concreteType = Compilation.CreateArrayTypeSymbol(arrayType.ElementType);
            return true;
        }

        if (normalized is not INamedTypeSymbol namedType)
            return false;

        if (!IsSpreadEnumerable(namedType))
            return false;

        if (!IsConcreteCollectionTargetType(namedType))
            return false;

        concreteType = namedType;
        return true;
    }

    private bool IsConcreteCollectionTargetType(INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Error)
            return false;

        if (type.SpecialType == SpecialType.System_String)
            return false;

        if (TryGetCollectionBuilderInfo(type, out _, out _))
            return true;

        var hasParameterlessConstructor = type.Constructors.Any(static ctor => !ctor.IsStatic && ctor.Parameters.Length == 0);
        if (!hasParameterlessConstructor)
            return false;

        var addMethod = new SymbolQuery("Add", type, IsStatic: false)
            .Lookup(this)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(static method => method.Parameters.Length == 1);

        return addMethod is not null;
    }

    private readonly record struct ComprehensionRangeInfo(bool IsValid, BoundExpression? Start, BoundExpression? End, ITypeSymbol? ElementType)
    {
        public static ComprehensionRangeInfo Invalid => new(false, null, null, null);
    }

    private enum CountableRangeEndpointKind
    {
        Integral,
        Char
    }

    private bool TryInferCollectionElementType(
        IEnumerable<BoundExpression> elements,
        out ITypeSymbol inferredType,
        out (ITypeSymbol Left, ITypeSymbol Right) conflict)
    {
        inferredType = Compilation.ErrorTypeSymbol;
        conflict = default;
        ITypeSymbol? inferred = null;

        foreach (var element in elements)
        {
            ITypeSymbol? elementType = element switch
            {
                BoundSpreadElement spread when spread.Expression.Type is ITypeSymbol spreadType
                    => GetSpreadElementType(spreadType),
                _ => element.Type
            };

            if (elementType is null)
                continue;

            elementType = TypeSymbolNormalization.NormalizeForInference(elementType);

            if (elementType.TypeKind == TypeKind.Error)
                continue;

            if (inferred is null)
            {
                inferred = elementType;
                continue;
            }

            if (!TryInferBestCommonType(inferred, elementType, out var mergedType))
            {
                conflict = (inferred, elementType);
                return false;
            }

            inferred = mergedType;
        }

        inferredType = inferred ?? Compilation.GetSpecialType(SpecialType.System_Object);
        return true;
    }

    private bool TryInferBestCommonType(ITypeSymbol left, ITypeSymbol right, out ITypeSymbol bestCommonType)
    {
        bestCommonType = Compilation.ErrorTypeSymbol;

        if (left.TypeKind == TypeKind.Error || right.TypeKind == TypeKind.Error)
            return false;

        if (SymbolEqualityComparer.Default.Equals(left, right))
        {
            bestCommonType = left;
            return true;
        }

        if (TryInferBestCommonNumericType(left, right, out var numericCommonType))
        {
            bestCommonType = numericCommonType;
            return true;
        }

        if (IsAssignable(left, right, out _))
        {
            bestCommonType = left;
            return true;
        }

        if (IsAssignable(right, left, out _))
        {
            bestCommonType = right;
            return true;
        }

        if (TryFindCompatibleDiscriminatedUnionCarrier(left, right, out var discriminatedUnionCarrier))
        {
            bestCommonType = discriminatedUnionCarrier;
            return true;
        }

        if (TryFindCommonImplicitConversionTarget(left, right, out var conversionTarget))
        {
            bestCommonType = conversionTarget;
            return true;
        }

        if (TryFindCommonBaseType(left, right, out var commonBase))
        {
            bestCommonType = commonBase;
            return true;
        }

        return false;
    }

    private static bool TryFindCompatibleDiscriminatedUnionCarrier(ITypeSymbol left, ITypeSymbol right, out ITypeSymbol carrierType)
    {
        carrierType = null!;

        if (!TryGetUnionCarrier(left, out var leftCarrier) ||
            !TryGetUnionCarrier(right, out var rightCarrier))
        {
            return false;
        }

        if (!TryMergeCompatibleNamedTypes(leftCarrier, rightCarrier, out var mergedCarrier))
            return false;

        carrierType = mergedCarrier;
        return true;
    }

    private static bool TryGetUnionCarrier(ITypeSymbol sourceType, out INamedTypeSymbol carrierType)
    {
        carrierType = null!;

        if (sourceType.TryGetUnion() is INamedTypeSymbol unionType)
        {
            carrierType = unionType;
            return true;
        }

        var caseSymbol = sourceType.TryGetUnionCase();
        if (caseSymbol is null)
            return false;

        var caseType = sourceType as INamedTypeSymbol;
        if (caseType is not null &&
            TryProjectDiscriminatedUnionFromCaseArguments(caseType, caseSymbol, out var projectedCarrier))
        {
            carrierType = projectedCarrier;
            return true;
        }

        if (caseSymbol.Union is INamedTypeSymbol caseUnion)
        {
            carrierType = caseUnion;
            return true;
        }

        return false;
    }

    private static bool TryProjectDiscriminatedUnionFromCaseArguments(
        INamedTypeSymbol caseType,
        IUnionCaseTypeSymbol caseSymbol,
        out INamedTypeSymbol projectedCarrier)
    {
        projectedCarrier = null!;

        if (caseType.TypeArguments.IsDefaultOrEmpty)
            return false;

        var caseDefinition = caseSymbol.OriginalDefinition as IUnionCaseTypeSymbol ?? caseSymbol;
        if (caseDefinition is not INamedTypeSymbol caseDefinitionNamed ||
            caseDefinitionNamed.TypeParameters.IsDefaultOrEmpty ||
            caseDefinitionNamed.TypeParameters.Length != caseType.TypeArguments.Length)
        {
            return false;
        }

        var unionDefinition = caseDefinition.Union.OriginalDefinition as INamedTypeSymbol ?? caseDefinition.Union as INamedTypeSymbol;
        if (unionDefinition is null || unionDefinition.TypeParameters.IsDefaultOrEmpty)
            return false;

        var unionArguments = unionDefinition.TypeParameters
            .Select(typeParameter => (ITypeSymbol)typeParameter)
            .ToArray();

        var changed = false;

        for (var i = 0; i < caseDefinitionNamed.TypeParameters.Length; i++)
        {
            var caseTypeParameter = caseDefinitionNamed.TypeParameters[i];
            ITypeParameterSymbol? mappedUnionTypeParameter = null;

            if (caseDefinition is SourceUnionCaseTypeSymbol sourceCaseDefinition &&
                sourceCaseDefinition.TryGetProjectedUnionTypeParameter(caseTypeParameter, out var mapped))
            {
                mappedUnionTypeParameter = mapped;
            }
            else
            {
                mappedUnionTypeParameter = unionDefinition.TypeParameters
                    .FirstOrDefault(tp => string.Equals(tp.Name, caseTypeParameter.Name, StringComparison.Ordinal));
            }

            if (mappedUnionTypeParameter is null)
                continue;

            var unionIndex = -1;
            for (var unionParameterIndex = 0; unionParameterIndex < unionDefinition.TypeParameters.Length; unionParameterIndex++)
            {
                if (SymbolEqualityComparer.Default.Equals(unionDefinition.TypeParameters[unionParameterIndex], mappedUnionTypeParameter))
                {
                    unionIndex = unionParameterIndex;
                    break;
                }
            }

            if (unionIndex < 0 || unionIndex >= unionArguments.Length)
                continue;

            unionArguments[unionIndex] = caseType.TypeArguments[i];
            changed = true;
        }

        if (!changed)
            return false;

        projectedCarrier = (INamedTypeSymbol)unionDefinition.Construct(unionArguments);
        return true;
    }

    private static bool TryMergeCompatibleNamedTypes(INamedTypeSymbol left, INamedTypeSymbol right, out INamedTypeSymbol merged)
    {
        merged = left;

        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        var leftDefinition = left.OriginalDefinition as INamedTypeSymbol ?? left;
        var rightDefinition = right.OriginalDefinition as INamedTypeSymbol ?? right;
        if (!SymbolEqualityComparer.Default.Equals(leftDefinition, rightDefinition))
            return false;

        if (left.TypeArguments.IsDefaultOrEmpty && right.TypeArguments.IsDefaultOrEmpty)
        {
            merged = left;
            return true;
        }

        if (left.TypeArguments.Length != right.TypeArguments.Length)
            return false;

        var mergedArguments = new ITypeSymbol[left.TypeArguments.Length];
        var changed = false;

        for (var i = 0; i < mergedArguments.Length; i++)
        {
            var leftArgument = left.TypeArguments[i];
            var rightArgument = right.TypeArguments[i];

            if (SymbolEqualityComparer.Default.Equals(leftArgument, rightArgument))
            {
                mergedArguments[i] = leftArgument;
                continue;
            }

            if (leftArgument is ITypeParameterSymbol && rightArgument is not ITypeParameterSymbol)
            {
                mergedArguments[i] = rightArgument;
                changed = true;
                continue;
            }

            if (rightArgument is ITypeParameterSymbol && leftArgument is not ITypeParameterSymbol)
            {
                mergedArguments[i] = leftArgument;
                continue;
            }

            return false;
        }

        if (!changed)
        {
            merged = left;
            return true;
        }

        merged = (INamedTypeSymbol)leftDefinition.Construct(mergedArguments);
        return true;
    }

    private bool TryFindCommonBaseType(ITypeSymbol left, ITypeSymbol right, out ITypeSymbol commonBase)
    {
        commonBase = Compilation.ErrorTypeSymbol;

        if (left is not INamedTypeSymbol leftNamed || right is not INamedTypeSymbol rightNamed)
            return false;

        var rightBases = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        for (var current = rightNamed; current is not null; current = current.BaseType)
            rightBases.Add(current);

        for (var current = leftNamed; current is not null; current = current.BaseType)
        {
            if (!rightBases.Contains(current))
                continue;

            if (current.SpecialType == SpecialType.System_Object &&
                (left.IsValueType || right.IsValueType))
            {
                continue;
            }

            // Avoid broad/common-root fallbacks for implicit inference.
            if (current.SpecialType == SpecialType.System_ValueType || current.TypeKind is TypeKind.Interface)
                continue;

            if (IsAssignable(current, left, out _) && IsAssignable(current, right, out _))
            {
                commonBase = current;
                return true;
            }
        }

        return false;
    }

    private bool TryFindCommonImplicitConversionTarget(ITypeSymbol left, ITypeSymbol right, out ITypeSymbol targetType)
    {
        targetType = Compilation.ErrorTypeSymbol;

        if (left is not INamedTypeSymbol leftNamed || right is not INamedTypeSymbol rightNamed)
            return false;

        var leftTargets = GetImplicitConversionTargets(leftNamed);
        var rightTargets = GetImplicitConversionTargets(rightNamed);
        if (leftTargets.Count == 0 || rightTargets.Count == 0)
            return false;

        var sharedTargets = leftTargets
            .Where(candidate => rightTargets.Any(other => SymbolEqualityComparer.Default.Equals(candidate, other)))
            .ToList();

        if (sharedTargets.Count == 0)
            return false;

        // Prefer the most specific shared target by discarding candidates that implicitly convert to another.
        ITypeSymbol? best = null;
        foreach (var candidate in sharedTargets)
        {
            if (candidate.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
                continue;

            if (candidate.TypeKind is TypeKind.Interface or TypeKind.TypeParameter)
                continue;

            if (!IsAssignable(candidate, left, out _) || !IsAssignable(candidate, right, out _))
                continue;

            if (best is null)
            {
                best = candidate;
                continue;
            }

            if (IsAssignable(best, candidate, out _) && !IsAssignable(candidate, best, out _))
                best = candidate;
        }

        if (best is null)
            return false;

        targetType = best;
        return true;
    }

    private bool TryInferBestCommonNumericType(ITypeSymbol left, ITypeSymbol right, out ITypeSymbol numericType)
    {
        numericType = Compilation.ErrorTypeSymbol;

        if (!IsNumericTypeForInference(left) || !IsNumericTypeForInference(right))
            return false;

        foreach (var specialType in s_numericInferencePreferenceOrder)
        {
            var candidate = Compilation.GetSpecialType(specialType);
            if (candidate.TypeKind == TypeKind.Error)
                continue;

            if (IsAssignable(candidate, left, out _) && IsAssignable(candidate, right, out _))
            {
                numericType = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsNumericTypeForInference(ITypeSymbol type)
        => type.SpecialType is
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal;

    private static readonly SpecialType[] s_numericInferencePreferenceOrder =
    [
        SpecialType.System_Byte,
        SpecialType.System_SByte,
        SpecialType.System_Int16,
        SpecialType.System_UInt16,
        SpecialType.System_Int32,
        SpecialType.System_UInt32,
        SpecialType.System_Int64,
        SpecialType.System_UInt64,
        SpecialType.System_Single,
        SpecialType.System_Double,
        SpecialType.System_Decimal,
    ];

    private static List<ITypeSymbol> GetImplicitConversionTargets(INamedTypeSymbol sourceType)
    {
        var targets = new List<ITypeSymbol>();

        foreach (var method in sourceType.GetMembers().OfType<IMethodSymbol>())
        {
            if (!method.IsStatic ||
                method.MethodKind is not MethodKind.Conversion ||
                !string.Equals(method.Name, "op_Implicit", StringComparison.Ordinal) ||
                method.Parameters.Length != 1)
            {
                continue;
            }

            if (!targets.Any(existing => SymbolEqualityComparer.Default.Equals(existing, method.ReturnType)))
                targets.Add(method.ReturnType);
        }

        return targets;
    }

    private bool TryGetIEnumerableElementType(INamedTypeSymbol targetType, out ITypeSymbol elementType)
    {
        elementType = Compilation.ErrorTypeSymbol;

        // Direct match: IEnumerable<T>
        if (targetType.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
            targetType.TypeArguments.Length == 1)
        {
            elementType = targetType.TypeArguments[0];
            return true;
        }

        // Check original definition by metadata name (covers symbols that don't set SpecialType)
        var def = targetType.OriginalDefinition ?? targetType;
        if (def.MetadataName == "IEnumerable`1" &&
            def.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
            targetType.TypeArguments.Length == 1)
        {
            elementType = targetType.TypeArguments[0];
            return true;
        }

        // Interface implementation: look for IEnumerable<T> among all interfaces
        foreach (var iface in targetType.AllInterfaces)
        {
            if (iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                iface.TypeArguments.Length == 1)
            {
                elementType = iface.TypeArguments[0];
                return true;
            }

            var idef = iface.OriginalDefinition ?? iface;
            if (idef.MetadataName == "IEnumerable`1" &&
                idef.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" &&
                iface.TypeArguments.Length == 1)
            {
                elementType = iface.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private ITypeSymbol GetSpreadElementType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            foreach (var iface in named.AllInterfaces)
            {
                if (iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T && iface.TypeArguments.Length == 1)
                    return iface.TypeArguments[0];
            }
        }

        if (type is IArrayTypeSymbol array)
            return array.ElementType;

        if (type is INamedTypeSymbol namedType && namedType.TypeArguments.Length == 1)
            return namedType.TypeArguments[0];

        return Compilation.GetSpecialType(SpecialType.System_Object);
    }

    private bool IsSpreadEnumerable(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
            return true;

        if (type is INamedTypeSymbol named)
        {
            var enumerable = Compilation.GetTypeByMetadataName("System.Collections.IEnumerable") as INamedTypeSymbol;
            var genericEnumerable = Compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1") as INamedTypeSymbol;

            bool MatchesEnumerable(INamedTypeSymbol candidate)
            {
                if (candidate.SpecialType == SpecialType.System_Collections_IEnumerable ||
                    candidate.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                {
                    return true;
                }

                var definition = candidate.OriginalDefinition ?? candidate;
                if (enumerable is not null && SymbolEqualityComparer.Default.Equals(definition, enumerable))
                    return true;
                if (genericEnumerable is not null && SymbolEqualityComparer.Default.Equals(definition, genericEnumerable))
                    return true;

                if (definition.MetadataName == "IEnumerable" &&
                    IsInNamespace(definition.ContainingNamespace, "System.Collections"))
                {
                    return true;
                }

                if (definition.MetadataName == "IEnumerable`1" &&
                    IsInNamespace(definition.ContainingNamespace, "System.Collections.Generic"))
                {
                    return true;
                }

                return false;
            }

            static bool IsInNamespace(INamespaceSymbol? namespaceSymbol, string qualifiedNamespace)
            {
                if (namespaceSymbol is null)
                    return false;

                var remaining = qualifiedNamespace;

                while (!namespaceSymbol.IsGlobalNamespace)
                {
                    var lastDot = remaining.LastIndexOf('.');
                    var segment = lastDot >= 0 ? remaining[(lastDot + 1)..] : remaining;

                    if (!string.Equals(namespaceSymbol.Name, segment, StringComparison.Ordinal))
                        return false;

                    if (lastDot < 0)
                        return namespaceSymbol.ContainingNamespace.IsGlobalNamespace;

                    remaining = remaining[..lastDot];
                    namespaceSymbol = namespaceSymbol.ContainingNamespace;

                    if (namespaceSymbol is null)
                        return false;
                }

                return false;
            }

            if (MatchesEnumerable(named))
                return true;

            foreach (var iface in named.AllInterfaces)
            {
                if (MatchesEnumerable(iface))
                    return true;
            }
        }

        return false;
    }

    private bool IsStaticFunctionBody
    {
        get
        {
            if (_containingSymbol is IMacroDeclarationSymbol)
                return true;

            if (_containingSymbol is not IMethodSymbol method)
                return false;

            if (method.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                return false;

            return method.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<FunctionStatementSyntax>()
                .Any(f => f.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword) || IsNamespaceLevelFunctionMember(f));
        }
    }

    private bool IsNamespaceLevelFunctionMember(FunctionStatementSyntax function)
        => function.Parent is GlobalStatementSyntax globalStatement &&
            Compilation.IsTopLevelFunctionMember(globalStatement) &&
            !Compilation.IsFileScopeLocalFunction(globalStatement);

    public override IEnumerable<ISymbol> LookupSymbols(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Binder? current = this;

        static string GetLookupKey(ISymbol symbol) => symbol.GetLookupIdentityKey();

        // static func bodies do not close over enclosing locals/parameters.
        var restrict = IsStaticFunctionBody;
        var boundarySymbol = restrict ? _containingSymbol : null;
        var pastBoundary = false;

        while (current is not null)
        {
            if (restrict && !pastBoundary)
            {
                // Once we encounter a binder that belongs to a different containing symbol,
                // we've left the function body scope. After that we must not see locals/params.
                if (current is BlockBinder bb &&
                    !SymbolEqualityComparer.Default.Equals(bb.ContainingSymbol, boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is MethodBinder mb &&
                         !SymbolEqualityComparer.Default.Equals(mb.GetMethodSymbol(), boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is MacroBinder boundaryMacroBinder &&
                         !SymbolEqualityComparer.Default.Equals(boundaryMacroBinder.GetMacroSymbol(), boundarySymbol))
                {
                    pastBoundary = true;
                }
                else if (current is TopLevelBinder or FunctionExpressionBinder)
                {
                    pastBoundary = true;
                }
            }

            var allowLocalsAndParams = !pastBoundary;

            if (current is BlockBinder block)
            {
                if (allowLocalsAndParams)
                {
                    if (block._locals.TryGetValue(name, out var local) && seen.Add(GetLookupKey(local.Symbol)))
                        yield return local.Symbol;

                    if (block._localTypes.TryGetValue(name, out var localType) && seen.Add(GetLookupKey(localType.Symbol)))
                        yield return localType.Symbol;

                    if (block._functions.TryGetValue(name, out var functions))
                    {
                        foreach (var function in functions)
                        {
                            if (seen.Add(GetLookupKey(function)))
                                yield return function;
                        }
                    }

                    if (block._labelsByName.TryGetValue(name, out var label) && seen.Add(GetLookupKey(label)))
                        yield return label;
                }
            }

            if (allowLocalsAndParams && current is TopLevelBinder topLevelBinder)
            {
                foreach (var param in topLevelBinder.GetParameters())
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
            }

            if (allowLocalsAndParams && current is MethodBinder methodBinder)
            {
                foreach (var param in methodBinder.GetMethodSymbol().Parameters)
                    if (param.Name != "_" && param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
            }

            if (allowLocalsAndParams && current is MacroBinder macroBinder)
            {
                var macro = macroBinder.GetMacroSymbol();
                foreach (var param in macro.Parameters)
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;

                if (macro.TargetParameter is { } targetParameter &&
                    targetParameter.Name == name &&
                    seen.Add(GetLookupKey(targetParameter)))
                {
                    yield return targetParameter;
                }
            }

            if (allowLocalsAndParams && current is FunctionExpressionBinder lambdaBinder)
            {
                foreach (var param in lambdaBinder.GetParameters())
                    if (param.Name == name && seen.Add(GetLookupKey(param)))
                        yield return param;
            }

            // Previous submission declarations behave as script-class members, not
            // locals of the current executable method. Keep them visible across static
            // function boundaries; lowering will provide their persistent receiver.
            if (current is SubmissionBinder submissionBinder)
            {
                foreach (var symbol in submissionBinder.LookupSubmissionSymbols(name))
                    if (seen.Add(GetLookupKey(symbol)))
                        yield return symbol;
            }

            // Members/imports remain visible even after the boundary.
            if (current is TypeMemberBinder typeMemberBinder)
            {
                var containingType = typeMemberBinder.ContainingTypeSymbol;
                if (!IsSuppressedNamespaceMembersContainer(containingType))
                {
                    foreach (var member in EnumerateTypeAndBaseMembers(containingType, name))
                        if (!IsLexicallyScopedFunctionSymbol(member) && seen.Add(GetLookupKey(member)))
                            yield return member;
                }
            }

            if (current is ImportBinder importBinder)
            {
                foreach (var symbol in importBinder.LookupSymbols(name))
                    if (!IsLexicallyScopedFunctionSymbol(symbol) && seen.Add(GetLookupKey(symbol)))
                        yield return symbol;
            }

            current = current.ParentBinder;
        }

        if (CurrentNamespace is { } currentNamespace)
        {
            foreach (var member in Compilation.GetNamespaceMembers(currentNamespace, name, ShouldIncludeNamespaceMembers()))
                if (!IsLexicallyScopedFunctionSymbol(member) && seen.Add(GetLookupKey(member)))
                    yield return member;
        }

        foreach (var member in Compilation.SymbolLookup.GetGlobalMembersSourceFirst(name))
            if (!IsLexicallyScopedFunctionSymbol(member) && seen.Add(GetLookupKey(member)))
                yield return member;

        foreach (var member in Compilation.GetNamespaceMembers(Compilation.SourceGlobalNamespace, name, ShouldIncludeNamespaceMembers()))
            if (!IsLexicallyScopedFunctionSymbol(member) && seen.Add(GetLookupKey(member)))
                yield return member;

        bool IsLexicallyScopedFunctionSymbol(ISymbol symbol)
            => symbol is IMethodSymbol && symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is FunctionStatementSyntax function &&
                !IsNamespaceLevelFunctionMember(function));

        bool ShouldIncludeNamespaceMembers()
            => Compilation.Options.AllowNamespaceMembers &&
               Compilation.Options.AllowNamespaceMemberImports;

        bool IsSuppressedNamespaceMembersContainer(INamedTypeSymbol type)
            => Compilation.IsNamespaceMemberContainer(type) &&
               !ShouldIncludeNamespaceMembers();
    }

    public override IEnumerable<ISymbol> LookupAvailableSymbols()
    {
        var seen = new HashSet<string>();
        Binder? current = this;

        while (current is not null)
        {
            // Locals and scoped symbols
            if (current is BlockBinder block)
            {
                foreach (var local in block._locals.Values)
                {
                    if (seen.Add(local.Symbol.Name))
                        yield return local.Symbol;
                }

                foreach (var localType in block._localTypes.Values)
                {
                    if (seen.Add(localType.Symbol.Name))
                        yield return localType.Symbol;
                }

                foreach (var label in block._labelsByName.Values)
                {
                    if (string.IsNullOrEmpty(label.Name))
                        continue;

                    if (seen.Add(label.Name))
                        yield return label;
                }
            }

            // Import namespaces
            if (current is ImportBinder importBinder)
            {
                foreach (var ns in importBinder.GetImportedNamespacesOrTypeScopes())
                {
                    if (ns is INamespaceSymbol namespaceSymbol)
                    {
                        foreach (var member in GetNamespaceScopeCompletionMembers(namespaceSymbol))
                        {
                            if (seen.Add(member.Name))
                                yield return member;
                        }
                    }
                    else
                    {
                        foreach (var member in ns.GetMembers())
                        {
                            if (!ImportBinder.IsImportableTypeScopeMember(member))
                                continue;

                            if (seen.Add(member.Name))
                                yield return member;
                        }
                    }
                }
            }

            if (current is TopLevelBinder topLevelBinder)
            {
                foreach (var param in topLevelBinder.GetParameters())
                {
                    if (seen.Add(param.Name))
                        yield return param;
                }
            }

            if (current is MethodBinder methodBinder)
            {
                foreach (var param in methodBinder.GetMethodSymbol().Parameters)
                {
                    if (param.Name != "_" && seen.Add(param.Name))
                        yield return param;
                }
            }

            if (current is MacroBinder macroBinder)
            {
                var macro = macroBinder.GetMacroSymbol();
                foreach (var param in macro.Parameters)
                {
                    if (seen.Add(param.Name))
                        yield return param;
                }

                if (macro.TargetParameter is { } targetParameter &&
                    seen.Add(targetParameter.Name))
                {
                    yield return targetParameter;
                }
            }

            if (current is FunctionExpressionBinder lambdaBinder)
            {
                foreach (var param in lambdaBinder.GetParameters())
                {
                    if (seen.Add(param.Name))
                        yield return param;
                }

            }

            if (current is SubmissionBinder submissionBinder)
            {
                foreach (var symbol in submissionBinder.GetSubmissionSymbols())
                {
                    if (seen.Add(symbol.Name))
                        yield return symbol;
                }
            }

            if (current is TypeMemberBinder typeMemberBinder)
            {
                var containingType = typeMemberBinder.ContainingTypeSymbol;
                if (IsSuppressedNamespaceMembersContainer(containingType))
                {
                    current = current.ParentBinder;
                    continue;
                }

                if (seen.Add(containingType.Name))
                    yield return containingType;

                for (var type = containingType; type is not null; type = type.BaseType)
                {
                    foreach (var member in type.GetMembers())
                    {
                        if (seen.Add(member.Name))
                            yield return member;
                    }
                }
            }

            current = current.ParentBinder;
        }

        if (!IsInsideTopLevelProgramBinder() &&
            CurrentNamespace is { IsGlobalNamespace: false } currentNamespace)
        {
            foreach (var member in GetNamespaceScopeCompletionMembers(currentNamespace))
            {
                if (seen.Add(member.Name))
                    yield return member;
            }
        }

        // Include source and referenced global roots as a last fallback.
        foreach (var member in Compilation.SymbolLookup.GetGlobalMembers())
        {
            if (seen.Add(member.Name))
                yield return member;
        }

        if (CurrentNamespace is null or { IsGlobalNamespace: true })
        {
            foreach (var member in GetNamespaceMemberCompletionSymbols(Compilation.SourceGlobalNamespace))
            {
                if (seen.Add(member.Name))
                    yield return member;
            }
        }

        IEnumerable<ISymbol> GetNamespaceScopeCompletionMembers(INamespaceSymbol namespaceSymbol)
        {
            var namespaceMembersContainer = Compilation.GetNamespaceMembersContainer(namespaceSymbol);

            foreach (var member in GetNamespaceMemberCompletionSymbols(namespaceSymbol))
            {
                yield return member;
            }

            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (namespaceMembersContainer is not null &&
                    SymbolEqualityComparer.Default.Equals(namespaceMembersContainer, member))
                {
                    continue;
                }

                if (member is not INamespaceOrTypeSymbol)
                    continue;

                yield return member;
            }
        }

        IEnumerable<ISymbol> GetNamespaceMemberCompletionSymbols(INamespaceSymbol namespaceSymbol)
        {
            if (!Compilation.Options.AllowNamespaceMembers ||
                !Compilation.Options.AllowNamespaceMemberImports)
            {
                yield break;
            }

            foreach (var member in Compilation.GetNamespaceMembers(namespaceSymbol))
            {
                if (IsNamespaceScopeCompletionSymbol(member))
                    yield return member;
            }
        }

        static bool IsNamespaceScopeCompletionSymbol(ISymbol symbol)
            => symbol switch
            {
                IMethodSymbol method => method.DeclaringSyntaxReferences
                    .Any(static reference =>
                        reference.GetSyntax() is FunctionStatementSyntax { Parent: GlobalStatementSyntax global } &&
                        Compilation.IsTopLevelFunctionMember(global)),
                IFieldSymbol { IsConst: true } field => field.DeclaringSyntaxReferences
                    .Any(static reference =>
                        reference.GetSyntax() is ConstDeclarationSyntax constDeclaration &&
                        constDeclaration.Parent is CompilationUnitSyntax or FileScopedNamespaceDeclarationSyntax or NamespaceDeclarationSyntax),
                _ => symbol is INamespaceOrTypeSymbol
            };

        bool IsInsideTopLevelProgramBinder()
        {
            for (var current = (Binder?)this; current is not null; current = current.ParentBinder)
            {
                if (current is TopLevelBinder)
                    return true;
            }

            return false;
        }

        bool IsSuppressedNamespaceMembersContainer(INamedTypeSymbol type)
            => Compilation.IsNamespaceMemberContainer(type) &&
               (!Compilation.Options.AllowNamespaceMembers ||
                !Compilation.Options.AllowNamespaceMemberImports);
    }

    public override BoundFunctionStatement BindFunction(FunctionStatementSyntax function)
    {
        // Get the binder from the factory
        var binder = SemanticModel.GetBinder(function, this);

        if (binder is not FunctionBinder functionBinder)
            throw new InvalidOperationException("Expected FunctionBinder");

        // Register the symbol in the current scope
        var symbol = functionBinder.GetMethodSymbol();

        var methodBinder = functionBinder.GetMethodBodyBinder();

        BoundBlockStatement? functionBody = null;

        if (function.Body is not null)
        {
            var blockBinder = (BlockBinder)SemanticModel.GetBinder(function.Body, methodBinder);
            functionBody = blockBinder.BindBlockStatement(function.Body);
        }
        else if (function.ExpressionBody is not null)
        {
            var expressionBinder = (BlockBinder)SemanticModel.GetBinder(function.ExpressionBody, methodBinder);
            var expression = expressionBinder.BindExpression(function.ExpressionBody.Expression, allowReturn: true);
            var returnType = symbol.ReturnType;
            var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
            var statements = new List<BoundStatement>(capacity: 1);

            if (symbol is SourceMethodSymbol sourceMethod &&
                sourceMethod.RequiresAsyncReturnTypeInference &&
                !sourceMethod.AsyncReturnTypeInferenceComplete)
            {
                var inferredReturnType = AsyncReturnTypeUtilities.InferAsyncReturnType(Compilation, expression.Type);
                sourceMethod.SetReturnType(inferredReturnType);
                sourceMethod.CompleteAsyncReturnTypeInference();
                returnType = sourceMethod.ReturnType;
            }

            if (SymbolEqualityComparer.Default.Equals(returnType, unitType) || returnType.SpecialType == SpecialType.System_Void)
            {
                var statement = expressionBinder.ExpressionToStatement(expression);
                statements.Add(statement);
            }
            else
            {
                var converted = expression;
                var skipReturnConversions = symbol switch
                {
                    SourceMethodSymbol { HasAsyncReturnTypeError: true } => true,
                    SourceMethodSymbol { ShouldDeferAsyncReturnDiagnostics: true } => true,
                    _ => false,
                };
                var returnTargetType = symbol.IsAsync &&
                    AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, returnType) is { } asyncReturnTargetType
                        ? asyncReturnTargetType
                        : returnType;

                if (!skipReturnConversions && converted.Type is not null && ShouldAttemptConversion(converted) &&
                    returnType.TypeKind != TypeKind.Error)
                {
                    var asyncResultType = symbol.IsAsync
                        ? AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, returnType)
                        : null;

                    if (symbol.IsAsync &&
                        asyncResultType is { SpecialType: SpecialType.System_Unit or SpecialType.System_Void })
                    {
                        if (!expressionBinder.TryConvertTaskLikeAsyncReturnExpression(symbol, converted, function.ExpressionBody.Expression, out converted))
                        {
                            var methodDisplay = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            expressionBinder.Diagnostics.ReportAsyncTaskReturnCannotHaveExpression(
                                methodDisplay,
                                function.ExpressionBody.Expression.GetLocation());
                        }
                    }
                    else
                    {
                        var targetType = returnType;

                        if (symbol.IsAsync &&
                            AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, returnType) is { } awaitedType)
                        {
                            targetType = awaitedType;
                        }

                        returnTargetType = targetType;

                        if (!expressionBinder.IsAssignable(targetType, converted.Type, out var conversion))
                        {
                            expressionBinder.ReportCannotConvertExpressionToType(
                                converted,
                                targetType,
                                function.ExpressionBody.Expression.GetLocation());
                        }
                        else
                        {
                            converted = expressionBinder.ApplyConversion(converted, targetType, conversion, function.ExpressionBody.Expression);
                        }
                    }
                }

                if (expressionBinder.ReportStructUnionReturnMayBeDefault(returnTargetType, converted, function.ExpressionBody.Expression))
                {
                    converted = new BoundErrorExpression(returnTargetType, null, BoundExpressionReason.OtherError);
                }

                converted = expressionBinder.ValidateByRefReturnExpression(symbol, converted, function.ExpressionBody.Expression);
                statements.Add(new BoundReturnStatement(converted));
            }

            var boundBlock = new BoundBlockStatement(statements);
            functionBody = boundBlock;

            if (symbol is SourceMethodSymbol { IsAsync: true } asyncMethod)
            {
                var containsAwait = AsyncLowerer.ContainsAwait(boundBlock) ||
                    Compilation.ContainsAwaitExpressionOutsideNestedFunctions(function.ExpressionBody);
                asyncMethod.SetContainsAwait(containsAwait);

                if (containsAwait)
                {
                    RefSafetyDiagnosticReporter.ReportLocalsAcrossAwait(boundBlock, _diagnostics);
                    RefSafetyDiagnosticReporter.ReportParametersAcrossAwait(asyncMethod, _diagnostics);
                }

                if (!containsAwait)
                {
                    var description = AsyncDiagnosticUtilities.GetAsyncMemberDescription(asyncMethod);
                    var location = AsyncDiagnosticUtilities.GetAsyncKeywordLocation(asyncMethod, function.ExpressionBody);
                    _diagnostics.ReportAsyncLacksAwait(description, location);
                }
            }

            SemanticModel.CacheBoundNode(function.ExpressionBody, boundBlock, this);
        }

        if (symbol is SourceMethodSymbol functionSourceMethod &&
            functionBody is not null)
        {
            if (functionSourceMethod.IsIterator)
            {
                RefSafetyDiagnosticReporter.ReportIteratorStorage(
                    functionBody,
                    functionSourceMethod,
                    _diagnostics);
            }

            RefSafetyDiagnosticReporter.Report(
                functionBody,
                function.GetLocation(),
                expressionResultEscapes: functionSourceMethod.ReturnType.SpecialType != SpecialType.System_Unit,
                _diagnostics);

            var capturedVariables = AnalyzeFunctionCapturedVariables(functionBody, symbol);
            RefSafetyDiagnosticReporter.ReportCaptures(
                capturedVariables,
                function.GetLocation(),
                _diagnostics);
            functionSourceMethod.SetCapturedVariables(capturedVariables);
            if (capturedVariables.Length != 0 && functionSourceMethod.ClosureFrameType is null)
                functionSourceMethod.SetClosureFrameType(ClosureFrameSymbolFactory.Create(functionSourceMethod));
        }

        return new BoundFunctionStatement(symbol); // Possibly include body here if needed
    }

    private static ImmutableArray<ISymbol> AnalyzeFunctionCapturedVariables(BoundBlockStatement body, IMethodSymbol functionSymbol)
    {
        var walker = new FunctionCapturedVariableWalker(functionSymbol);
        walker.VisitStatement(body);
        return walker.GetCapturedVariables();
    }

    private sealed class FunctionCapturedVariableWalker : BoundTreeWalker
    {
        private readonly IMethodSymbol _functionSymbol;
        private readonly HashSet<ISymbol> _captured = new(SymbolEqualityComparer.Default);

        public FunctionCapturedVariableWalker(IMethodSymbol functionSymbol)
        {
            _functionSymbol = functionSymbol;
        }

        public ImmutableArray<ISymbol> GetCapturedVariables()
        {
            if (_captured.Count == 0)
                return ImmutableArray<ISymbol>.Empty;

            return _captured.ToImmutableArray();
        }

        public override void VisitLocalAccess(BoundLocalAccess node)
        {
            AddIfCaptured(node.Symbol);
            base.VisitLocalAccess(node);
        }

        public override void VisitParameterAccess(BoundParameterAccess node)
        {
            AddIfCaptured(node.Symbol);
            base.VisitParameterAccess(node);
        }

        public override void VisitVariableExpression(BoundVariableExpression node)
        {
            AddIfCaptured(node.Symbol);
            base.VisitVariableExpression(node);
        }

        public override void VisitSelfExpression(BoundSelfExpression node)
        {
            AddIfCaptured(node.Symbol ?? node.Type);
            base.VisitSelfExpression(node);
        }

        private void AddIfCaptured(ISymbol? symbol)
        {
            if (symbol is null)
                return;

            if (symbol is ILocalSymbol or IParameterSymbol)
            {
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, _functionSymbol))
                    return;

                // Skip variables declared inside nested lambdas/functions.
                // They are not captures from the outer scope of _functionSymbol.
                if (IsNestedWithin(symbol.ContainingSymbol, _functionSymbol))
                    return;

                _captured.Add(symbol);
                return;
            }

            if (symbol is ITypeSymbol typeSymbol &&
                _functionSymbol.ContainingType is { } containingType &&
                SymbolEqualityComparer.Default.Equals(typeSymbol, containingType))
            {
                _captured.Add(typeSymbol);
            }
        }

        private static bool IsNestedWithin(ISymbol? scope, ISymbol parent)
        {
            var current = scope;
            while (current is not null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, parent))
                    return true;
                current = current.ContainingSymbol;
            }
            return false;
        }

        static bool IsDisallowedImplicitCommonType(ITypeSymbol type)
        {
            if (type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType)
                return true;

            if (type is INamedTypeSymbol named &&
                named.TypeKind is TypeKind.Interface or TypeKind.TypeParameter)
            {
                return true;
            }

            return false;
        }
    }

    private BoundExpression BindWithExpression(WithExpressionSyntax syntax)
    {
        var receiver = BindExpression(syntax.Expression);

        var receiverType = UnwrapAlias(receiver.Type ?? Compilation.ErrorTypeSymbol);
        var assignments = BindWithAssignments(receiverType, syntax.Entries);

        BoundExpression ReturnWithError(BoundExpressionReason reason)
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                strategy: BoundWithStrategyKind.UpdateMethod,
                method: null,
                memberMethods: ImmutableArray<IMethodSymbol>.Empty,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: null,
                type: Compilation.ErrorTypeSymbol,
                reason: reason);
        }

        if (receiverType.TypeKind == TypeKind.Error)
            return ReturnWithError(BoundExpressionReason.OtherError);

        if (receiverType is not INamedTypeSymbol namedReceiver)
        {
            _diagnostics.ReportTypeDoesNotSupportWithExpression(
                receiverType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                syntax.WithKeyword.GetLocation());
            return ReturnWithError(BoundExpressionReason.UnsupportedOperation);
        }

        if (IsRecordType(receiverType) && TryGetCopyConstructor(namedReceiver, out var recordCopyConstructor))
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                BoundWithStrategyKind.RecordClone,
                method: null,
                memberMethods: ImmutableArray<IMethodSymbol>.Empty,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: recordCopyConstructor,
                type: receiverType);
        }

        // Optimization: if only a single member is updated and a matching WithX(...) member method exists,
        // prefer that over the broader Update/With convention methods.
        if (assignments.Length == 1 &&
            TryBindWithMemberMethods(receiver, namedReceiver, assignments, syntax, out var singleMemberMethods, out var singleMemberMethodsReturnType))
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                BoundWithStrategyKind.WithMemberMethods,
                method: null,
                memberMethods: singleMemberMethods,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: null,
                type: singleMemberMethodsReturnType);
        }

        if (TryBindWithConventionMethod(receiver, namedReceiver, assignments, syntax, "Update", BoundWithStrategyKind.UpdateMethod, out var updateBound))
            return updateBound;

        if (TryBindWithConventionMethod(receiver, namedReceiver, assignments, syntax, "With", BoundWithStrategyKind.WithMethod, out var withBound))
            return withBound;

        if (TryBindWithMemberMethods(receiver, namedReceiver, assignments, syntax, out var memberMethods, out var memberMethodsReturnType))
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                BoundWithStrategyKind.WithMemberMethods,
                method: null,
                memberMethods: memberMethods,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: null,
                type: memberMethodsReturnType);
        }

        if (TryGetCloneMethod(namedReceiver, syntax.WithKeyword.GetLocation(), out var cloneMethod))
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                BoundWithStrategyKind.Clone,
                method: null,
                memberMethods: ImmutableArray<IMethodSymbol>.Empty,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: cloneMethod,
                copyConstructor: null,
                type: cloneMethod.ReturnType);
        }

        if (TryGetCopyConstructor(namedReceiver, out var copyConstructor))
        {
            return new BoundWithExpression(
                receiver,
                 [.. assignments.Select(x => x.BoundNode)],
                BoundWithStrategyKind.Clone,
                method: null,
                memberMethods: ImmutableArray<IMethodSymbol>.Empty,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: copyConstructor,
                type: receiverType);
        }

        _diagnostics.ReportTypeDoesNotSupportWithExpression(
            receiverType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            syntax.WithKeyword.GetLocation());
        return ReturnWithError(BoundExpressionReason.UnsupportedOperation);
    }

    private ImmutableArray<(BoundWithAssignment BoundNode, WithAssignmentSyntax SyntaxNode)> BindWithAssignments(
        ITypeSymbol receiverType,
        SyntaxList<WithEntrySyntax> entries)
    {
        foreach (var expressionEntry in entries.OfType<WithExpressionEntrySyntax>())
        {
            _diagnostics.ReportTypeDoesNotSupportWithExpression(
                receiverType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                expressionEntry.Expression.GetLocation());
            _ = BindExpression(expressionEntry.Expression, allowReturn: false);
        }

        var assignmentEntries = entries.OfType<WithAssignmentSyntax>().ToImmutableArray();
        if (assignmentEntries.Length == 0)
            return ImmutableArray<(BoundWithAssignment BoundNode, WithAssignmentSyntax SyntaxNode)>.Empty;

        var builder = ImmutableArray.CreateBuilder<(BoundWithAssignment BoundNode, WithAssignmentSyntax SyntaxNode)>(assignmentEntries.Length);
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);

        _withInitializerDepth++;
        try
        {
            foreach (var assignment in assignmentEntries)
            {
                var nameSyntax = assignment.Name;
                var nameToken = nameSyntax.Identifier;
                var memberName = nameToken.ValueText;

                if (string.IsNullOrEmpty(memberName))
                {
                    _ = BindExpression(assignment.Expression, allowReturn: false);
                    continue;
                }

                if (!seenMembers.Add(memberName))
                {
                    _diagnostics.ReportWithExpressionMemberAssignedMultipleTimes(memberName, nameSyntax.GetLocation());
                    _ = BindExpression(assignment.Expression, allowReturn: false);
                    continue;
                }

                if (receiverType is not INamedTypeSymbol namedReceiver)
                {
                    _ = BindExpression(assignment.Expression, allowReturn: false);
                    continue;
                }

                if (!TryGetAssignableMember(namedReceiver, assignment, out var member, out var memberType))
                    continue;

                var value = BindExpressionWithTargetType(assignment.Expression, memberType, allowReturn: false);
                value = PrepareRightForAssignment(value, memberType, assignment.Expression);
                if (value is BoundErrorExpression)
                    continue;

                builder.Add((new BoundWithAssignment(member, value), assignment));
            }
        }
        finally
        {
            _withInitializerDepth--;
        }

        return builder.ToImmutable();
    }

    private bool TryBindWithConventionMethod(
        BoundExpression receiver,
        INamedTypeSymbol receiverType,
        ImmutableArray<(BoundWithAssignment BoundNode, WithAssignmentSyntax Syntax)> assignments,
        WithExpressionSyntax syntax,
        string methodName,
        BoundWithStrategyKind strategy,
        out BoundWithExpression boundExpression)
    {
        boundExpression = null!;

        var candidates = new SymbolQuery(methodName, receiverType, IsStatic: false)
            .LookupMethods(this)
            .ToImmutableArray();

        if (candidates.IsDefaultOrEmpty)
            return false;

        var accessible = GetAccessibleMethods(candidates, syntax.WithKeyword.GetLocation(), reportIfInaccessible: false);
        if (accessible.IsDefaultOrEmpty)
            return false;

        if (string.Equals(methodName, "With", StringComparison.Ordinal))
        {
            accessible = accessible
                .Where(static method => !method.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                .ToImmutableArray();

            if (accessible.IsDefaultOrEmpty)
                return false;
        }

        // Map assignments by *parameter-style* name (camelCase), because convention methods typically
        // use parameter names like `middleName` while members are `MiddleName`.
        // This prevents false negatives when comparing assignment keys to method parameter names.
        var assignmentMap = new Dictionary<string, BoundWithAssignment>(StringComparer.Ordinal);
        foreach (var (a, snode) in assignments)
        {
            var key = NormalizeWithConventionKey(a.Member.Name);
            assignmentMap[key] = a;
        }
        var applicable = new List<(IMethodSymbol Method, ImmutableArray<ISymbol> ParameterMembers)>();

        foreach (var method in accessible)
        {
            if (!IsReturnTypeCovariant(receiverType, method.ReturnType))
                continue;

            var parameterMembers = ImmutableArray.CreateBuilder<ISymbol>(method.Parameters.Length);
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            var valid = true;

            foreach (var parameter in method.Parameters)
            {
                var name = NormalizeWithConventionMemberName(parameter.Name);
                if (!TryGetReadableMember(receiverType, name, syntax.WithKeyword.GetLocation(), out var member))
                {
                    valid = false;
                    break;
                }

                parameterMembers.Add(member);
                parameterNames.Add(parameter.Name);

                var argumentType = assignmentMap.TryGetValue(parameter.Name, out var assignment)
                    ? assignment.Value.Type ?? Compilation.ErrorTypeSymbol
                    : GetMemberType(member);

                if (!IsAssignable(parameter.Type, argumentType, out _))
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
                continue;

            // Reject assignments that don't correspond to any parameter name.
            if (assignmentMap.Keys.Any(name => !parameterNames.Contains(name)))
                continue;

            applicable.Add((method, parameterMembers.ToImmutable()));
        }

        if (applicable.Count == 0)
            return false;

        if (applicable.Count > 1)
        {
            _diagnostics.ReportCallIsAmbiguous(methodName, applicable.Select(x => x.Method).ToImmutableArray(), syntax.WithKeyword.GetLocation());
            boundExpression = new BoundWithExpression(
                receiver,
                [.. assignments.Select(x => x.BoundNode)],
                strategy,
                method: null,
                memberMethods: ImmutableArray<IMethodSymbol>.Empty,
                parameterMembers: ImmutableArray<ISymbol>.Empty,
                cloneMethod: null,
                copyConstructor: null,
                type: receiverType,
                reason: BoundExpressionReason.Ambiguous);
            return true;
        }

        var selected = applicable[0];
        boundExpression = new BoundWithExpression(
            receiver,
            [.. assignments.Select(x => x.BoundNode)],
            strategy,
            selected.Method,
            memberMethods: ImmutableArray<IMethodSymbol>.Empty,
            parameterMembers: selected.ParameterMembers,
            cloneMethod: null,
            copyConstructor: null,
            type: selected.Method.ReturnType);

        return true;
    }

    private static string NormalizeWithConventionKey(string memberName)
    {
        if (string.IsNullOrEmpty(memberName))
            return memberName;

        // `MiddleName` -> `middleName`
        return char.ToLowerInvariant(memberName[0]) + memberName[1..];
    }

    private static string NormalizeWithConventionMemberName(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName))
            return parameterName;

        // `middleName` -> `MiddleName`
        return char.ToUpperInvariant(parameterName[0]) + parameterName[1..];
    }

    private bool TryBindWithMemberMethods(
    BoundExpression receiver,
    INamedTypeSymbol namedReceiver,
    ImmutableArray<(BoundWithAssignment BoundNode, WithAssignmentSyntax Syntax)> assignments,
    WithExpressionSyntax syntax,
    out ImmutableArray<IMethodSymbol> memberMethods,
    out ITypeSymbol memberMethodsReturnType)
    {
        return TryBindWithMemberMethods(
            receiver,
            namedReceiver,
            assignments,
            syntax,
            out memberMethods,
            out memberMethodsReturnType,
            reportDiagnostics: true);
    }

    private bool TryBindWithMemberMethods(
        BoundExpression receiver,
        INamedTypeSymbol receiverType,
        ImmutableArray<(BoundWithAssignment BoundNode, WithAssignmentSyntax Syntax)> assignments,
        WithExpressionSyntax syntax,
        out ImmutableArray<IMethodSymbol> memberMethods,
        out ITypeSymbol returnType,
        bool reportDiagnostics)
    {
        memberMethods = ImmutableArray<IMethodSymbol>.Empty;
        returnType = receiverType;

        if (assignments.IsDefaultOrEmpty)
            return false;

        var methods = ImmutableArray.CreateBuilder<IMethodSymbol>(assignments.Length);
        ITypeSymbol lastReturnType = receiverType;

        foreach (var (assignment, syntaxNode) in assignments)
        {
            var assignmentLocation = syntaxNode.Name.GetLocation()
                ?? syntax.WithKeyword.GetLocation()
                ?? syntax.GetLocation()
                ?? Location.None;
            var valueLocation = syntaxNode.Expression.GetLocation()
                ?? assignmentLocation;
            var methodName = $"With{assignment.Member.Name}";
            var candidates = new SymbolQuery(methodName, receiverType, IsStatic: false)
                .LookupMethods(this)
                .ToImmutableArray();

            if (candidates.IsDefaultOrEmpty)
            {
                if (reportDiagnostics)
                    _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(methodName, assignmentLocation);
                return false;
            }

            var accessible = GetAccessibleMethods(candidates, assignmentLocation, reportIfInaccessible: false);
            if (accessible.IsDefaultOrEmpty)
                return false;

            var applicable = accessible
                .Where(m => m.Parameters.Length == 1 && IsReturnTypeCovariant(receiverType, m.ReturnType))
                .ToImmutableArray();

            if (applicable.IsDefaultOrEmpty)
            {
                _diagnostics.ReportTheNameDoesNotExistInTheCurrentContext(
                    methodName,
                    assignmentLocation);
                return false;
            }

            var args = new[] { new BoundArgument(assignment.Value, RefKind.None, name: null, syntax) };
            var resolution = OverloadResolver.ResolveOverload(applicable, args, Compilation, binder: this, receiver: receiver, canBindLambda: EnsureLambdaCompatible, callSyntax: syntax);

            if (resolution.IsAmbiguous)
            {
                _diagnostics.ReportCallIsAmbiguous(methodName, resolution.AmbiguousCandidates, syntax.WithKeyword.GetLocation());
                return false;
            }

            if (!resolution.Success)
            {
                if (reportDiagnostics)
                {
                    // Best effort expected type:
                    var expectedType = candidates[0].Parameters[0].Type;

                    var valueType = assignment.Value.Type ?? Compilation.ErrorTypeSymbol;
                    if (valueType.TypeKind != TypeKind.Error && expectedType.TypeKind != TypeKind.Error)
                    {
                        ReportCannotConvertFromTypeToType(
                            valueType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            expectedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            valueLocation);
                    }
                }
                return false;
            }

            methods.Add(resolution.Method!);
            lastReturnType = resolution.Method!.ReturnType;
        }

        memberMethods = methods.ToImmutable();
        returnType = lastReturnType;
        return true;
    }

    private bool TryGetAssignableMember(
        INamedTypeSymbol receiverType,
        WithAssignmentSyntax assignment,
        out ISymbol member,
        out ITypeSymbol memberType)
    {
        member = null!;
        memberType = Compilation.ErrorTypeSymbol;

        var memberName = assignment.Name.Identifier.ValueText;
        var members = GetNearestTypeMembers(receiverType, memberName);

        var property = members.OfType<IPropertySymbol>().FirstOrDefault();
        if (property is not null)
        {
            if (!EnsureMemberAccessible(property, assignment.Name.GetLocation(), "property"))
                return false;

            // In a `with` initializer, assignments are *conceptual* updates that may be applied via
            // Update/With conventions or cloning strategies. A property can therefore be assignable
            // even if it has no setter.
            var inWithInitializer = _withInitializerDepth > 0;

            // Still require the member to be readable so we can treat it as a stable assignment target
            // (and so convention methods can map parameter names back to members).
            if (property.GetMethod is null)
            {
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(property.Name, assignment.Name.GetLocation());
                _ = BindExpression(assignment.Expression, allowReturn: false);
                return false;
            }

            if (!inWithInitializer)
            {
                // Outside of `with`, require a real setter.
                if (!property.IsMutable)
                {
                    _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(property.Name, assignment.Name.GetLocation());
                    _ = BindExpression(assignment.Expression, allowReturn: false);
                    return false;
                }
            }

            member = property;
            memberType = property.Type;
            return true;
        }

        var field = members.OfType<IFieldSymbol>().FirstOrDefault();
        if (field is not null)
        {
            if (!EnsureMemberAccessible(field, assignment.Name.GetLocation(), "field"))
                return false;

            var inWithInitializer = _withInitializerDepth > 0;

            if (field.IsConst || (!inWithInitializer && field.IsReadOnly))
            {
                _diagnostics.ReportReadOnlyFieldCannotBeAssignedTo(assignment.Name.GetLocation());
                _ = BindExpression(assignment.Expression, allowReturn: false);
                return false;
            }

            member = field;
            memberType = field.Type;
            return true;
        }

        _diagnostics.ReportMemberDoesNotContainDefinition(
            receiverType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            memberName,
            assignment.Name.GetLocation());
        _ = BindExpression(assignment.Expression, allowReturn: false);
        return false;
    }

    private bool TryGetReadableMember(
        INamedTypeSymbol receiverType,
        string memberName,
        Location location,
        out ISymbol member)
    {
        member = null!;
        var members = GetNearestTypeMembers(receiverType, memberName);

        foreach (var candidate in members.OfType<IPropertySymbol>())
        {
            if (candidate.IsStatic || candidate.GetMethod is null)
                continue;

            if (!IsSymbolAccessible(candidate))
                continue;

            member = candidate;
            return true;
        }

        foreach (var candidate in members.OfType<IFieldSymbol>())
        {
            if (candidate.IsStatic)
                continue;

            if (!IsSymbolAccessible(candidate))
                continue;

            member = candidate;
            return true;
        }

        if (members.Length > 0)
            _ = EnsureMemberAccessible(members[0], location, "member");

        return false;
    }

    private bool TryGetCloneMethod(
        INamedTypeSymbol receiverType,
        Location location,
        out IMethodSymbol cloneMethod)
    {
        cloneMethod = null!;
        var candidates = new SymbolQuery("Clone", receiverType, IsStatic: false)
            .LookupMethods(this)
            .ToImmutableArray();

        if (candidates.IsDefaultOrEmpty)
            return false;

        var accessible = GetAccessibleMethods(candidates, location, reportIfInaccessible: false);
        if (accessible.IsDefaultOrEmpty)
            return false;

        var applicable = accessible
            .Where(m => m.Parameters.Length == 0 && IsReturnTypeCovariant(receiverType, m.ReturnType))
            .ToImmutableArray();

        if (applicable.IsDefaultOrEmpty)
            return false;

        if (applicable.Length > 1)
        {
            _diagnostics.ReportCallIsAmbiguous("Clone", applicable, location);
            return false;
        }

        cloneMethod = applicable[0];
        return true;
    }

    private bool TryGetCopyConstructor(INamedTypeSymbol receiverType, out IMethodSymbol copyConstructor)
    {
        copyConstructor = null!;

        foreach (var ctor in receiverType.InstanceConstructors)
        {
            if (ctor.IsStatic || ctor.Parameters.Length != 1)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, receiverType))
                continue;

            if (!IsSymbolAccessible(ctor))
                continue;

            copyConstructor = ctor;
            return true;
        }

        return false;
    }

    private bool IsReturnTypeCovariant(ITypeSymbol receiverType, ITypeSymbol returnType)
        => IsAssignable(receiverType, returnType, out _);

    private static bool IsRecordType(ITypeSymbol typeSymbol)
        => typeSymbol is SourceNamedTypeSymbol { IsRecord: true } ||
           typeSymbol is ConstructedNamedTypeSymbol { OriginalDefinition: SourceNamedTypeSymbol { IsRecord: true } };

    private static ITypeSymbol GetMemberType(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new InvalidOperationException($"Unsupported member type: {member.GetType()}")
        };
    }

    private BoundObjectInitializer BindObjectInitializer(
     ITypeSymbol instanceType,
     ObjectInitializerExpressionSyntax initializer)
    {
        _objectInitializerDepth++;
        try
        {
            instanceType = UnwrapAlias(instanceType);

            var entries = ImmutableArray.CreateBuilder<BoundObjectInitializerEntry>(initializer.Entries.Count);
            IPropertySymbol? contentProperty = null;

            if (instanceType is INamedTypeSymbol namedInstance && namedInstance.TypeKind != TypeKind.Error)
            {
                foreach (var member in GetNearestTypeMembers(namedInstance, "Content"))
                {
                    if (member is IPropertySymbol { IsStatic: false, IsMutable: true } property)
                    {
                        contentProperty = property;
                        break;
                    }
                }
            }

            var hasContentConvention = contentProperty is not null && contentProperty.Type.TypeKind != TypeKind.Error;
            var seenContentEntry = false;

            foreach (var entry in initializer.Entries)
            {
                switch (entry)
                {
                    case ObjectInitializerAssignmentEntrySyntax assignment:
                        {
                            var boundEntry = BindObjectInitializerAssignmentEntry(instanceType, assignment);
                            if (boundEntry is not null)
                                entries.Add(boundEntry);
                            break;
                        }

                    case ObjectInitializerExpressionEntrySyntax expressionEntry:
                        {
                            if (!hasContentConvention)
                            {
                                var expression = BindExpression(expressionEntry.Expression, allowReturn: false);
                                entries.Add(new BoundObjectInitializerExpressionEntry(expression));
                                break;
                            }

                            if (seenContentEntry)
                            {
                                _ = BindExpressionWithTargetType(expressionEntry.Expression, contentProperty!.Type, allowReturn: false);
                                _diagnostics.ReportMultipleContentEntriesNotAllowed(
                                    instanceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                    expressionEntry.Expression.GetLocation());
                                break;
                            }

                            seenContentEntry = true;

                            if (!EnsureMemberAccessible(contentProperty!, expressionEntry.GetLocation(), "property"))
                            {
                                _ = BindExpressionWithTargetType(expressionEntry.Expression, contentProperty.Type, allowReturn: false);
                                break;
                            }

                            var value = BindExpressionWithTargetType(expressionEntry.Expression, contentProperty.Type, allowReturn: false);
                            value = PrepareRightForAssignment(value, contentProperty.Type, expressionEntry.Expression);

                            if (value is not BoundErrorExpression)
                                entries.Add(new BoundObjectInitializerAssignmentEntry(contentProperty, value));
                            break;
                        }
                }
            }

            return new BoundObjectInitializer(entries.ToImmutable());
        }
        finally
        {
            _objectInitializerDepth--;
        }
    }

    private BoundObjectInitializerAssignmentEntry? BindObjectInitializerAssignmentEntry(
     ITypeSymbol receiverType,
     ObjectInitializerAssignmentEntrySyntax assignment)
        => BindObjectInitializerAssignmentEntry(
            receiverType,
            assignment.Name,
            assignment.EqualsToken,
            assignment.Expression);

    private BoundObjectInitializerAssignmentEntry? BindObjectInitializerAssignmentEntry(
     ITypeSymbol receiverType,
     IdentifierNameSyntax assignmentName,
     SyntaxToken operatorToken,
     ExpressionSyntax assignmentExpression)
    {
        receiverType = UnwrapAlias(receiverType);

        if (receiverType.TypeKind == TypeKind.Error)
        {
            _ = BindExpression(assignmentExpression, allowReturn: false);
            return null;
        }

        if (receiverType is not INamedTypeSymbol named)
        {
            _ = BindExpression(assignmentExpression, allowReturn: false);
            return null;
        }

        var name = assignmentName.Identifier.ValueText;
        var members = GetNearestTypeMembers(named, name);

        IPropertySymbol? property = null;
        IFieldSymbol? field = null;
        IEventSymbol? @event = null;

        foreach (var member in members)
        {
            if (member is IPropertySymbol p)
            {
                property = p;
                break;
            }
        }

        if (property is null)
        {
            foreach (var member in members)
            {
                if (member is IFieldSymbol f)
                {
                    field = f;
                    break;
                }
            }
        }

        if (property is null && field is null)
        {
            foreach (var member in members)
            {
                if (member is IEventSymbol e)
                {
                    @event = e;
                    break;
                }
            }
        }

        var resolvedMember = (ISymbol?)property ?? (ISymbol?)field ?? @event;
        if (resolvedMember is null)
        {
            // Unknown member. RHS already bound for diagnostics.
            var memberName = assignmentName.Identifier.ValueText;
            _diagnostics.ReportMemberDoesNotContainDefinition(
                receiverType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                memberName,
                assignmentName.GetLocation());
            _ = BindExpression(assignmentExpression, allowReturn: false);
            return null;
        }

        // The initializer name is semantically a member reference even though it has no
        // explicit receiver in source. Preserve that relationship for public semantic
        // queries used by hover, definition, and other language-service features.
        CacheBoundNode(assignmentName, new BoundMemberAccessExpression(receiver: null, resolvedMember));

        var operatorTokenKind = operatorToken.Kind;

        if (operatorTokenKind == SyntaxKind.EqualsToken && @event is not null)
        {
            _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(@event.Name, assignmentName.GetLocation());
            _ = BindExpression(assignmentExpression, allowReturn: false);
            return null;
        }

        if (property is not null)
        {
            if (!EnsureMemberAccessible(property, assignmentName.GetLocation(), "property"))
                return null;

            var setMethod = property.SetMethod;

            if (setMethod is null)
            {
                _diagnostics.ReportPropertyOrIndexerCannotBeAssignedIsReadOnly(
                    property.Name,
                    assignmentName.GetLocation());
                return null;
            }
            else if (setMethod.MethodKind == MethodKind.InitOnly)
            {
                // OK here (object initializer context)
            }
            else
            {
                // OK (normal setter)
            }

            var right = BindExpressionWithTargetType(assignmentExpression, property.Type, allowReturn: false);
            if (right is BoundErrorExpression)
                return null;

            if (operatorTokenKind == SyntaxKind.EqualsToken)
            {
                var value = PrepareRightForAssignment(right, property.Type, assignmentExpression);
                if (value is BoundErrorExpression)
                    return null;

                return new BoundObjectInitializerAssignmentEntry(property, SyntaxKind.EqualsToken, value);
            }

            var binaryOperatorKind = GetBinaryOperatorFromAssignment(operatorTokenKind);
            if (binaryOperatorKind is null)
            {
                _diagnostics.ReportOperatorCannotBeAppliedToOperandsOfTypes(
                    operatorToken.Text,
                    property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    right.Type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    assignmentExpression.GetLocation());
                return null;
            }

            var left = new BoundMemberAccessExpression(null, property);
            var validated = BindCompoundAssignmentValue(left, right, property.Type, binaryOperatorKind.Value, assignmentExpression);
            if (validated is BoundErrorExpression)
                return null;

            return new BoundObjectInitializerAssignmentEntry(property, operatorTokenKind, right);
        }

        if (field is not null)
        {
            if (!EnsureMemberAccessible(field, assignmentName.GetLocation(), "field"))
                return null;

            if (field.IsConst || field.IsReadOnly)
            {
                _diagnostics.ReportReadOnlyFieldCannotBeAssignedTo(assignmentName.GetLocation());
                return null;
            }

            var right = BindExpressionWithTargetType(assignmentExpression, field.Type, allowReturn: false);
            if (right is BoundErrorExpression)
                return null;

            if (operatorTokenKind == SyntaxKind.EqualsToken)
            {
                var value = PrepareRightForAssignment(right, field.Type, assignmentExpression);
                if (value is BoundErrorExpression)
                    return null;

                return new BoundObjectInitializerAssignmentEntry(field, SyntaxKind.EqualsToken, value);
            }

            var binaryOperatorKind = GetBinaryOperatorFromAssignment(operatorTokenKind);
            if (binaryOperatorKind is null)
            {
                _diagnostics.ReportOperatorCannotBeAppliedToOperandsOfTypes(
                    operatorToken.Text,
                    field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    right.Type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    assignmentExpression.GetLocation());
                return null;
            }

            var left = new BoundMemberAccessExpression(null, field);
            var validated = BindCompoundAssignmentValue(left, right, field.Type, binaryOperatorKind.Value, assignmentExpression);
            if (validated is BoundErrorExpression)
                return null;

            return new BoundObjectInitializerAssignmentEntry(field, operatorTokenKind, right);
        }

        if (@event is not null)
        {
            if (operatorTokenKind is not (SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken))
            {
                _diagnostics.ReportEventCanOnlyBeUsedWithPlusOrMinus(@event.Name, assignmentName.GetLocation());
                _ = BindExpression(assignmentExpression, allowReturn: false);
                return null;
            }

            var right = BindExpressionWithTargetType(assignmentExpression, @event.Type, allowReturn: false);
            if (right is BoundErrorExpression)
                return null;

            var converted = ConvertValueForAssignment(right, @event.Type, assignmentExpression);
            if (converted is BoundErrorExpression)
                return null;

            return new BoundObjectInitializerAssignmentEntry(@event, operatorTokenKind, converted);
        }

        return null;
    }

    private ImmutableArray<ISymbol> GetNearestTypeMembers(INamedTypeSymbol type, string name)
    {
        Compilation.EnsureSourceTypeDeclarationsDeclared();

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            var lookupType = EnsureSourceMemberSignatureDeclaredForExactLookup(current, name) as INamedTypeSymbol ?? current;
            var members = lookupType.GetMembers(name);
            if (!members.IsDefaultOrEmpty)
                return members;
        }

        return [];
    }

    private sealed class TypeSymbolReferenceComparer : IEqualityComparer<ITypeSymbol>
    {
        public static TypeSymbolReferenceComparer Instance { get; } = new();

        public bool Equals(ITypeSymbol? x, ITypeSymbol? y) => ReferenceEquals(x, y);

        public int GetHashCode(ITypeSymbol obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class SymbolReferenceComparer<TSymbol> : IEqualityComparer<TSymbol>
        where TSymbol : class, ISymbol
    {
        public static SymbolReferenceComparer<TSymbol> Instance { get; } = new();

        public bool Equals(TSymbol? x, TSymbol? y) => ReferenceEquals(x, y);

        public int GetHashCode(TSymbol obj) => RuntimeHelpers.GetHashCode(obj);
    }


    // Sealed hierarchy exhaustiveness: ensures all concrete (non-abstract) leaves are covered.

    private void ReportMatchNotExhaustive(SyntaxNode matchSyntax, string missingCase)
        => _diagnostics.ReportMatchExpressionNotExhaustive(missingCase, GetMatchKeywordLocation(matchSyntax));

    private void ReportMatchArmPatternNotFullyCovered(Location location, string typeName)
        => _diagnostics.Report(Diagnostic.Create(CompilerDiagnostics.MatchArmPatternNotFullyCovered, location, typeName));

    private bool TryGetPartiallyCoveredTypeName(
        HashSet<ITypeSymbol> remaining,
        BoundPattern pattern,
        out string typeName)
    {
        foreach (var candidate in remaining)
        {
            var candidateType = UnwrapAlias(candidate);
            if (!PatternTargetsType(pattern, candidateType))
                continue;

            if (IsTotalPattern(candidateType, pattern))
                continue;

            typeName = candidateType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return true;
        }

        typeName = string.Empty;
        return false;
    }

    private bool PatternTargetsType(BoundPattern pattern, ITypeSymbol candidateType)
    {
        static bool TargetsType(BlockBinder binder, ITypeSymbol targetType, ITypeSymbol candidate)
            => binder.IsAssignable(targetType, candidate, out _);

        return pattern switch
        {
            BoundDeconstructPattern deconstructPattern => TargetsType(this, UnwrapAlias(deconstructPattern.NarrowedType ?? deconstructPattern.ReceiverType), candidateType),
            BoundDictionaryPattern dictionaryPattern => TargetsType(this, UnwrapAlias(dictionaryPattern.ReceiverType), candidateType),
            BoundPropertyPattern propertyPattern => TargetsType(this, UnwrapAlias(propertyPattern.NarrowedType ?? propertyPattern.ReceiverType), candidateType),
            BoundPositionalPattern positionalPattern => TargetsType(this, UnwrapAlias(positionalPattern.Type), candidateType),
            BoundDeclarationPattern declarationPattern => TargetsType(this, UnwrapAlias(declarationPattern.DeclaredType), candidateType),
            _ => false,
        };
    }

    private static Location GetMatchKeywordLocation(SyntaxNode matchSyntax)
    {
        return matchSyntax switch
        {
            MatchExpressionSyntax matchExpression => matchExpression.MatchKeyword.GetLocation(),
            PostfixMatchExpressionSyntax matchExpression => matchExpression.MatchKeyword.GetLocation(),
            MatchStatementSyntax matchStatement => matchStatement.MatchKeyword.GetLocation(),
            _ => UnexpectedMatchSyntaxLocation(matchSyntax),
        };
    }

    private static bool ShouldReportRedundantCatchAll<T>(
        int armIndex,
        int catchAllIndex,
        ICollection<T>? guaranteedRemaining)
        => ShouldReportRedundantCatchAll(armIndex, catchAllIndex, guaranteedRemaining, inactiveStructStateRemaining: null);

    private static bool ShouldReportRedundantCatchAll<T>(
        int armIndex,
        int catchAllIndex,
        ICollection<T>? guaranteedRemaining,
        bool? inactiveStructStateRemaining)
    {
        if (catchAllIndex < 0)
            return false;

        var noRemainingCases = guaranteedRemaining is null || guaranteedRemaining.Count == 0;
        var noRemainingInactiveState = inactiveStructStateRemaining is not true;

        if (armIndex < catchAllIndex)
            return noRemainingCases && noRemainingInactiveState;

        return armIndex == catchAllIndex &&
               guaranteedRemaining is not null &&
               noRemainingCases &&
               noRemainingInactiveState;
    }

    private static Location UnexpectedMatchSyntaxLocation(SyntaxNode matchSyntax)
    {
        Debug.Fail($"Unexpected match syntax node kind for exhaustiveness diagnostics: {matchSyntax.Kind}");
        return Location.None;
    }

    private static (string First, string Second) GetAmbiguousCaseDisplayNames(
        INamedTypeSymbol first, INamedTypeSymbol second)
        => UnionSymbolExtensions.FormatAmbiguousCasePair(first, second);

    private INamedTypeSymbol? TryGetUnionCarrierForCase(INamedTypeSymbol caseType)
    {
        // Fast path: IUnionCaseTypeSymbol already holds a direct reference to its union.
        if (caseType.TryGetUnionCase() is { Union: INamedTypeSymbol directUnion })
            return directUnion;

        // Fallback: for nested cases, ContainingType is the union.
        if (caseType.ContainingType is INamedTypeSymbol containingType)
            return containingType;

        // Non-nested cases: find the union carrier that declares this case.
        var key = caseType.OriginalDefinition;

        // Scan visible symbols first.
        foreach (var symbol in LookupAvailableSymbols())
        {
            if (symbol is not INamedTypeSymbol namedType)
                continue;

            var union = namedType.TryGetUnion();
            if (union is null)
                continue;

            foreach (var c in union.Variants)
            {
                if (ReferenceEquals(c.OriginalDefinition, key))
                    return namedType;
            }
        }

        // Also scan import binders directly (explicit type imports / aliases may not surface via LookupAvailableSymbols).
        for (Binder? current = this; current is not null; current = current.ParentBinder)
        {
            if (current is not ImportBinder importBinder)
                continue;

            foreach (var importedScope in importBinder.GetImportedNamespacesOrTypeScopes().OfType<INamedTypeSymbol>())
            {
                var union = importedScope.TryGetUnion();
                if (union is not null)
                {
                    foreach (var c in union.Variants)
                    {
                        if (ReferenceEquals(c.OriginalDefinition, key))
                            return importedScope;
                    }
                }
            }

            foreach (var importedType in importBinder.GetImportedTypes().OfType<INamedTypeSymbol>())
            {
                var union = importedType.TryGetUnion();
                if (union is not null)
                {
                    foreach (var c in union.Variants)
                    {
                        if (ReferenceEquals(c.OriginalDefinition, key))
                            return importedType;
                    }
                }
            }

            foreach (var aliasList in importBinder.GetAliases().Values)
            {
                foreach (var aliasSymbol in aliasList)
                {
                    if (aliasSymbol.UnderlyingSymbol is not INamedTypeSymbol aliasNamedType)
                        continue;

                    var union = aliasNamedType.TryGetUnion();
                    if (union is null)
                        continue;

                    foreach (var c in union.Variants)
                    {
                        if (ReferenceEquals(c.OriginalDefinition, key))
                            return aliasNamedType;
                    }
                }
            }
        }

        return null;
    }
}
