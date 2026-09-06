using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Documentation;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public partial class SemanticModel
{
    private static readonly CompletionService s_completionService = new();
    private readonly SemanticBindingState _bindingState = new();
    private readonly ConcurrentDictionary<SyntaxNodeMapKey, byte> _asyncLoweringInProgress = new();
    private readonly ConcurrentDictionary<SyntaxNode, ImmutableDictionary<AttributeSyntax, MacroExpansionResult?>> _macroExpansionCache = new();
    private readonly ConcurrentDictionary<SyntaxNode, FreestandingMacroExpansionCacheEntry> _freestandingMacroExpansionCache = new();
    private readonly ConcurrentDictionary<AttributeSyntax, ImmutableArray<SyntaxNode>> _expandedDeclarationCache = new();
    private readonly ConcurrentDictionary<SyntaxNode, SyntaxNode> _macroReplacementSyntaxMap = new();
    private readonly ConcurrentDictionary<BaseTypeDeclarationSyntax, BaseTypeDeclarationSyntax> _macroContainingTypeSyntaxMap = new();
    private readonly ConcurrentDictionary<SyntaxNode, ImmutableArray<MacroFragmentRegion>> _macroFragmentRegionCache = new();
    private readonly ConcurrentDictionary<SyntaxNode, ImmutableArray<MacroTokenInfo>> _macroTokenInfoCache = new();
    private readonly ConcurrentDictionary<SyntaxNode, MacroInputSnapshot> _macroInputSnapshotCache = new();
    private readonly ConcurrentDictionary<SyntaxNode, MacroEmbeddedLanguageProjection> _macroEmbeddedLanguageProjectionCache = new();

    private readonly DeclaredSymbolLookup _declaredSymbolLookup;
    private readonly object _diagnosticsCollectionGate = new();
    private readonly object _bindingSetupGate = new();
    private readonly object _expandedRootGate = new();
    private readonly SemaphoreSlim _semanticAccessGate = new(1, 1);
    private readonly AsyncLocal<int> _semanticAccessDepth = new();
    // Public semantic APIs are allowed to run while diagnostics are being collected,
    // but they must keep using non-reporting binding. Diagnostic traversal is the
    // only path that should add binder diagnostics during a diagnostics pass.
    private readonly AsyncLocal<int> _semanticQueryBindingDepth = new();
    private readonly AsyncLocal<AvailableLocalBindingQuery?> _availableLocalBindingQuery = new();
    private bool _isCollectingDiagnostics;
    private int _diagnosticCollectionThreadId;
    private CancellationToken _diagnosticBindingCancellationToken;
    private bool _declarationsComplete;
    private bool _rootBinderCreated;
    private bool _isEnsuringDeclarations;
    private int _declarationSetupThreadId;
    private bool _isCreatingRootBinder;
    private int _rootBinderThreadId;
    private CompilationUnitSyntax? _expandedRoot;

    private ConcurrentDictionary<SyntaxNode, Binder> _binderCache => _bindingState.BinderCache;
    private ConcurrentDictionary<SyntaxNodeMapKey, Binder> _binderCacheByKey => _bindingState.BinderCacheByKey;
    private ConcurrentDictionary<Binder, BinderLifecycleSnapshot> _binderLifecycleSnapshots => _bindingState.BinderLifecycleSnapshots;
    private ConcurrentDictionary<SyntaxNode, SymbolInfo> _symbolMappings => _bindingState.SymbolMappings;
    private ConcurrentDictionary<SyntaxNode, TypeInfo> _typeMappings => _bindingState.TypeMappings;
    private ConcurrentDictionary<SyntaxNodeMapKey, TypeInfo> _typeMappingsByKey => _bindingState.TypeMappingsByKey;
    private ConcurrentDictionary<SyntaxNode, byte> _nonReportingSymbolMappings => _bindingState.NonReportingSymbolMappings;
    private ConcurrentDictionary<SyntaxNode, byte> _nonReportingTypeMappings => _bindingState.NonReportingTypeMappings;
    private ConcurrentDictionary<SyntaxNodeMapKey, byte> _nonReportingTypeMappingsByKey => _bindingState.NonReportingTypeMappingsByKey;
    private ConcurrentDictionary<SyntaxNode, BoundNode> _boundNodeCache => _bindingState.BoundNodeCache;
    private ConcurrentDictionary<SyntaxNode, (Binder, BoundNode)> _boundNodeCache2 => _bindingState.BoundNodeCacheWithBinder;
    private ConcurrentDictionary<SyntaxNode, ImmutableArray<Diagnostic>> _boundNodeDiagnostics => _bindingState.BoundNodeDiagnostics;
    private ConcurrentDictionary<ContextualBoundNodeCacheKey, BoundNode> _contextualBoundNodeCache => _bindingState.ContextualBoundNodeCache;
    private ConcurrentDictionary<ContextualBoundNodeCacheKey, (Binder, BoundNode)> _contextualBoundNodeCache2 => _bindingState.ContextualBoundNodeCacheWithBinder;
    private ConcurrentDictionary<SyntaxNode, byte> _nonReportingBoundNodeCache => _bindingState.NonReportingBoundNodeCache;
    private ConcurrentDictionary<ContextualBoundNodeCacheKey, byte> _nonReportingContextualBoundNodeCache => _bindingState.NonReportingContextualBoundNodeCache;
    private ConcurrentDictionary<SyntaxNode, BoundNode> _loweredBoundNodeCache => _bindingState.LoweredBoundNodeCache;
    private ConcurrentDictionary<SyntaxNode, (Binder, BoundNode)> _loweredBoundNodeCache2 => _bindingState.LoweredBoundNodeCacheWithBinder;
    private ConcurrentDictionary<FunctionExpressionSyntax, IMethodSymbol> _functionExpressionSymbolCache => _bindingState.FunctionExpressionSymbolCache;
    private ConcurrentDictionary<FunctionExpressionSyntax, ITypeSymbol> _functionExpressionDelegateTypeCache => _bindingState.FunctionExpressionDelegateTypeCache;
    private ConcurrentDictionary<FunctionExpressionSyntax, byte> _functionExpressionSymbolCreationInProgress => _bindingState.FunctionExpressionSymbolCreationInProgress;
    private ConcurrentDictionary<SyntaxNode, byte> _availableInvocationSymbolInfoInProgress => _bindingState.AvailableInvocationSymbolInfoInProgress;
    private ConcurrentDictionary<SyntaxNode, byte> _functionExpressionParameterLookupInProgress => _bindingState.FunctionExpressionParameterLookupInProgress;
    private ConcurrentDictionary<SyntaxNode, byte> _functionExpressionRebindInProgress => _bindingState.FunctionExpressionRebindInProgress;
    private ConcurrentDictionary<SyntaxNode, byte> _typeMemberSignaturesDeclared => _bindingState.TypeMemberSignaturesDeclared;
    private ConcurrentDictionary<BoundNode, SyntaxNode> _syntaxCache => _bindingState.SyntaxCache;
    private ConcurrentDictionary<BoundNode, SyntaxNode> _loweredSyntaxCache => _bindingState.LoweredSyntaxCache;
    private ConcurrentDictionary<SyntaxNode, ImmutableArray<Compilation.VisibleValueDeclaration>> _visibleValueScopeCache => _bindingState.VisibleValueScopeCache;
    private IImmutableList<Diagnostic>? _diagnostics
    {
        get => _bindingState.Diagnostics;
        set => _bindingState.Diagnostics = value;
    }

    private IImmutableList<Diagnostic>? _documentDiagnostics
    {
        get => _bindingState.DocumentDiagnostics;
        set => _bindingState.DocumentDiagnostics = value;
    }

    private DiagnosticBag _declarationDiagnostics => _bindingState.DeclarationDiagnostics;

    internal void AddDeclarationDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            _declarationDiagnostics.Report(diagnostic);
    }

    public bool IsDebuggingEnabled { get; set; } = true;

    public SemanticModel(Compilation compilation, SyntaxTree syntaxTree)
    {
        Compilation = compilation;
        SyntaxTree = syntaxTree;
        _declaredSymbolLookup = new DeclaredSymbolLookup(this);
    }

    public Compilation Compilation { get; }

    public SyntaxTree SyntaxTree { get; }

    private void ValidateSyntaxNode(SyntaxNode node, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(node, parameterName);

        if (!ReferenceEquals(node.SyntaxTree, SyntaxTree))
            throw new ArgumentException("Syntax node is not part of this semantic model's syntax tree.", parameterName);
    }

    internal ImmutableArray<IMethodSymbol> GetFrameworkProjectionMethods(
        ITypeSymbol receiverType,
        string memberName)
    {
        if (Compilation.Options.FrameworkProjectionMode != FrameworkProjectionMode.Standard)
            return [];

        return FrameworkProjectionCatalog.GetStandardMethods(Compilation, receiverType, memberName);
    }

    internal static bool IsFrameworkProjectionMethod(IMethodSymbol method)
        => method is ProjectedMethodSymbol;

    internal bool IsCollectingDiagnostics => _isCollectingDiagnostics;

    private bool IsInSemanticQueryBinding => _semanticQueryBindingDepth.Value > 0;

    internal bool IsCollectingBindingDiagnosticsForCurrentFlow =>
        _isCollectingDiagnostics && !IsInSemanticQueryBinding;

    private bool HasSemanticAccessForCurrentFlow =>
        _semanticAccessDepth.Value > 0 ||
        (_isCollectingDiagnostics && _diagnosticCollectionThreadId == Environment.CurrentManagedThreadId);

    private static bool RequiresSemanticAccessGate
    {
        get
        {
#if NET11_0_OR_GREATER
            return RuntimeFeature.IsMultithreadingSupported;
#else
            return true;
#endif
        }
    }

    internal void ThrowIfDiagnosticBindingCancellationRequested()
        => _diagnosticBindingCancellationToken.ThrowIfCancellationRequested();

    internal async ValueTask<IDisposable> EnterSemanticAccessAsync(CancellationToken cancellationToken)
    {
        if (HasSemanticAccessForCurrentFlow)
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);

        if (!RequiresSemanticAccessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);
        }

        var declarationAccess = await Compilation.EnterSourceDeclarationAccessAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _semanticAccessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            declarationAccess?.Dispose();
            throw;
        }
        // AsyncLocal changes made here do not flow back into the awaiting caller.
        // The caller establishes ambient access in its own execution context.
        return new SemanticAccessLease(this, releaseDepth: false, releaseGate: true, declarationAccess);
    }

    internal IDisposable EnterSemanticAccess(CancellationToken cancellationToken)
    {
        if (HasSemanticAccessForCurrentFlow)
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);

        if (!RequiresSemanticAccessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _semanticAccessDepth.Value++;
            return new SemanticAccessLease(this, releaseDepth: true, releaseGate: false);
        }

        var declarationAccess = Compilation.EnterSourceDeclarationAccess(cancellationToken);
        try
        {
            _semanticAccessGate.Wait(cancellationToken);
        }
        catch
        {
            declarationAccess?.Dispose();
            throw;
        }
        _semanticAccessDepth.Value++;
        return new SemanticAccessLease(this, releaseDepth: true, releaseGate: true, declarationAccess);
    }

    internal async ValueTask<IDisposable?> TryEnterSemanticAccessAsync(CancellationToken cancellationToken)
    {
        if (HasSemanticAccessForCurrentFlow)
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);

        if (!RequiresSemanticAccessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);
        }

        var declarationAccess = await Compilation.EnterSourceDeclarationAccessAsync(cancellationToken, tryEnter: true).ConfigureAwait(false);
        if (declarationAccess is null)
            return null;
        try
        {
            if (await _semanticAccessGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return new SemanticAccessLease(this, releaseDepth: false, releaseGate: true, declarationAccess);
        }
        catch
        {
            declarationAccess.Dispose();
            throw;
        }
        declarationAccess.Dispose();
        return null;
    }

    internal IDisposable? TryEnterSemanticAccess(CancellationToken cancellationToken)
    {
        if (HasSemanticAccessForCurrentFlow)
            return new SemanticAccessLease(this, releaseDepth: false, releaseGate: false);

        if (!RequiresSemanticAccessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _semanticAccessDepth.Value++;
            return new SemanticAccessLease(this, releaseDepth: true, releaseGate: false);
        }

        var declarationAccess = Compilation.EnterSourceDeclarationAccess(cancellationToken, tryEnter: true);
        if (declarationAccess is null)
            return null;
        try
        {
            if (_semanticAccessGate.Wait(0, cancellationToken))
            {
                _semanticAccessDepth.Value++;
                return new SemanticAccessLease(this, releaseDepth: true, releaseGate: true, declarationAccess);
            }
        }
        catch
        {
            declarationAccess.Dispose();
            throw;
        }
        declarationAccess.Dispose();
        return null;
    }

    internal IDisposable EnterAmbientSemanticAccess()
    {
        _semanticAccessDepth.Value++;
        return new SemanticAccessLease(this, releaseDepth: true, releaseGate: false,
            Compilation.EnterAmbientSourceDeclarationAccess());
    }

    private sealed class SemanticBindingState
    {
        public ConcurrentDictionary<SyntaxNode, Binder> BinderCache { get; } = new();
        public ConcurrentDictionary<SyntaxNodeMapKey, Binder> BinderCacheByKey { get; } = new();
        public ConcurrentDictionary<Binder, BinderLifecycleSnapshot> BinderLifecycleSnapshots { get; } = new();
        public ConcurrentDictionary<SyntaxNode, SymbolInfo> SymbolMappings { get; } = new();
        public ConcurrentDictionary<SyntaxNode, TypeInfo> TypeMappings { get; } = new();
        public ConcurrentDictionary<SyntaxNodeMapKey, TypeInfo> TypeMappingsByKey { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> NonReportingSymbolMappings { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> NonReportingTypeMappings { get; } = new();
        public ConcurrentDictionary<SyntaxNodeMapKey, byte> NonReportingTypeMappingsByKey { get; } = new();
        public ConcurrentDictionary<SyntaxNode, BoundNode> BoundNodeCache { get; } = new();
        public ConcurrentDictionary<SyntaxNode, (Binder, BoundNode)> BoundNodeCacheWithBinder { get; } = new();
        public ConcurrentDictionary<SyntaxNode, ImmutableArray<Diagnostic>> BoundNodeDiagnostics { get; } = new();
        public ConcurrentDictionary<ContextualBoundNodeCacheKey, BoundNode> ContextualBoundNodeCache { get; } = new(ContextualBoundNodeCacheKeyComparer.Instance);
        public ConcurrentDictionary<ContextualBoundNodeCacheKey, (Binder, BoundNode)> ContextualBoundNodeCacheWithBinder { get; } = new(ContextualBoundNodeCacheKeyComparer.Instance);
        public ConcurrentDictionary<SyntaxNode, byte> NonReportingBoundNodeCache { get; } = new();
        public ConcurrentDictionary<ContextualBoundNodeCacheKey, byte> NonReportingContextualBoundNodeCache { get; } = new(ContextualBoundNodeCacheKeyComparer.Instance);
        public ConcurrentDictionary<SyntaxNode, BoundNode> LoweredBoundNodeCache { get; } = new();
        public ConcurrentDictionary<SyntaxNode, (Binder, BoundNode)> LoweredBoundNodeCacheWithBinder { get; } = new();
        public ConcurrentDictionary<FunctionExpressionSyntax, IMethodSymbol> FunctionExpressionSymbolCache { get; } = new();
        public ConcurrentDictionary<FunctionExpressionSyntax, ITypeSymbol> FunctionExpressionDelegateTypeCache { get; } = new();
        public ConcurrentDictionary<FunctionExpressionSyntax, byte> FunctionExpressionSymbolCreationInProgress { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> AvailableInvocationSymbolInfoInProgress { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> FunctionExpressionParameterLookupInProgress { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> FunctionExpressionRebindInProgress { get; } = new();
        public ConcurrentDictionary<SyntaxNode, byte> TypeMemberSignaturesDeclared { get; } = new();
        public ConcurrentDictionary<BoundNode, SyntaxNode> SyntaxCache { get; } = new(ReferenceEqualityComparer.Instance);
        public ConcurrentDictionary<BoundNode, SyntaxNode> LoweredSyntaxCache { get; } = new(ReferenceEqualityComparer.Instance);
        public ConcurrentDictionary<SyntaxNode, ImmutableArray<Compilation.VisibleValueDeclaration>> VisibleValueScopeCache { get; } = new();
        public DiagnosticBag DeclarationDiagnostics { get; } = new();
        public IImmutableList<Diagnostic>? Diagnostics { get; set; }
        public IImmutableList<Diagnostic>? DocumentDiagnostics { get; set; }
    }

    private sealed class SemanticAccessLease : IDisposable
    {
        private SemanticModel? _semanticModel;
        private readonly bool _releaseDepth;
        private readonly bool _releaseGate;
        private readonly IDisposable? _declarationAccess;

        public SemanticAccessLease(SemanticModel semanticModel, bool releaseDepth, bool releaseGate, IDisposable? declarationAccess = null)
        {
            _semanticModel = semanticModel;
            _releaseDepth = releaseDepth;
            _releaseGate = releaseGate;
            _declarationAccess = declarationAccess;
        }

        public void Dispose()
        {
            var semanticModel = Interlocked.Exchange(ref _semanticModel, null);
            if (semanticModel is null)
                return;

            if (_releaseDepth)
                semanticModel._semanticAccessDepth.Value--;

            if (_releaseGate)
                semanticModel._semanticAccessGate.Release();
            _declarationAccess?.Dispose();
        }
    }

    private sealed class AvailableLocalBindingQuery
    {
        public int Depth { get; set; }
        public HashSet<AvailableLocalBindingKey> Active { get; } = new();
        public HashSet<AvailableLocalBindingKey> Failed { get; } = new();
    }

    private readonly record struct AvailableLocalBindingKey(
        VariableDeclaratorSyntax VariableDeclarator,
        bool AllowErrorType,
        bool AllowInitializerBinding,
        bool AllowBindingFallback,
        bool AllowBoundInitializerBindingWithoutFallback);

    private SemanticQueryBindingLease EnterSemanticQueryBinding()
    {
        _semanticQueryBindingDepth.Value++;
        return new SemanticQueryBindingLease(this);
    }

    private sealed class SemanticQueryBindingLease : IDisposable
    {
        private SemanticModel? _semanticModel;

        public SemanticQueryBindingLease(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public void Dispose()
        {
            var semanticModel = Interlocked.Exchange(ref _semanticModel, null);
            if (semanticModel is null)
                return;

            semanticModel._semanticQueryBindingDepth.Value--;
        }
    }

    private readonly record struct ContextualBoundNodeCacheKey(SyntaxNode Node, ITypeSymbol TargetType);

    internal readonly record struct BinderLifecycleSnapshot(
        string BinderType,
        SyntaxKind NodeKind,
        Text.TextSpan NodeSpan,
        string CacheKind,
        bool IsStructuralCacheable,
        bool SourceDeclarationsDeclared,
        bool SourceNamespaceLookupSuppressed,
        string ContainingSymbolKey,
        string? ParentBinderType,
        string? ParentContainingSymbolKey);

    private sealed class ContextualBoundNodeCacheKeyComparer : IEqualityComparer<ContextualBoundNodeCacheKey>
    {
        public static readonly ContextualBoundNodeCacheKeyComparer Instance = new();

        public bool Equals(ContextualBoundNodeCacheKey x, ContextualBoundNodeCacheKey y)
            => ReferenceEquals(x.Node, y.Node) &&
               SymbolEqualityComparer.Default.Equals(x.TargetType, y.TargetType);

        public int GetHashCode(ContextualBoundNodeCacheKey obj)
            => HashCode.Combine(
                RuntimeHelpers.GetHashCode(obj.Node),
                SymbolEqualityComparer.Default.GetHashCode(obj.TargetType));
    }

    /// <summary>
    /// Gets completion items available at a position in this semantic model's syntax tree.
    /// </summary>
    /// <param name="position">The zero-based position in the syntax tree.</param>
    /// <returns>A sequence of completion items.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="position"/> is outside the tree bounds.</exception>
    public IEnumerable<CompletionItem> GetCompletions(int position)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);

        var treeLength = SyntaxTree.GetRoot().FullSpan.End;
        if ((uint)position > (uint)treeLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        return s_completionService.GetCompletions(this, position);
    }

    /// <summary>
    /// Gets macro signature help at a position in this semantic model's syntax tree.
    /// </summary>
    /// <param name="position">The zero-based position in the syntax tree.</param>
    /// <returns>The macro signature at the position, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="position"/> is outside the tree bounds.</exception>
    public MacroSignatureHelp? GetMacroSignatureHelp(int position)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);

        var treeLength = SyntaxTree.GetRoot().FullSpan.End;
        if ((uint)position > (uint)treeLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        return MacroSignatureHelpService.GetSignatureHelp(this, position);
    }

    /// <summary>
    /// Gets ordinary Raven fragment regions reported for a token-tree macro invocation.
    /// </summary>
    public ImmutableArray<MacroFragmentRegion> GetMacroFragmentRegions(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentRegionsCore(expression, cancellationToken);

    public ImmutableArray<MacroFragmentRegion> GetMacroFragmentRegions(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentRegionsCore(declaration, cancellationToken);

    public ImmutableArray<MacroFragmentRegion> GetMacroFragmentRegions(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroFragmentRegionsCore(member, cancellationToken);

    internal ImmutableArray<MacroFragmentRegion> GetMacroFragmentRegionsCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        if (_macroFragmentRegionCache.TryGetValue(syntax, out var cached))
            return cached;

        var regions = MacroFragmentRegionService.GetFragmentRegions(this, syntax, syntax, cancellationToken);
        return _macroFragmentRegionCache.GetOrAdd(syntax, regions);
    }

    /// <summary>
    /// Gets the token stream and optional classifications for a token-tree macro invocation.
    /// </summary>
    public ImmutableArray<MacroTokenInfo> GetMacroTokens(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroTokensCore(expression, cancellationToken);

    public ImmutableArray<MacroTokenInfo> GetMacroTokens(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroTokensCore(declaration, cancellationToken);

    public ImmutableArray<MacroTokenInfo> GetMacroTokens(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroTokensCore(member, cancellationToken);

    internal ImmutableArray<MacroTokenInfo> GetMacroTokensCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        if (_macroTokenInfoCache.TryGetValue(syntax, out var cached))
            return cached;

        var tokens = MacroTokenInfoService.GetTokens(this, syntax, syntax, cancellationToken);
        return _macroTokenInfoCache.GetOrAdd(syntax, tokens);
    }

    /// <summary>
    /// Gets the compiler-owned token-and-fragment snapshot for a token-tree macro invocation.
    /// </summary>
    public MacroInputSnapshot GetMacroInputSnapshot(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroInputSnapshotCore(expression, cancellationToken);

    public MacroInputSnapshot GetMacroInputSnapshot(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroInputSnapshotCore(declaration, cancellationToken);

    public MacroInputSnapshot GetMacroInputSnapshot(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroInputSnapshotCore(member, cancellationToken);

    /// <summary>
    /// Gets a position-preserving embedded-language projection for a token-tree macro invocation.
    /// </summary>
    public MacroEmbeddedLanguageProjection? GetMacroEmbeddedLanguageProjection(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetMacroEmbeddedLanguageProjectionCore(expression, cancellationToken);

    public MacroEmbeddedLanguageProjection? GetMacroEmbeddedLanguageProjection(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetMacroEmbeddedLanguageProjectionCore(member, cancellationToken);

    public MacroEmbeddedLanguageProjection? GetMacroEmbeddedLanguageProjection(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetMacroEmbeddedLanguageProjectionCore(declaration, cancellationToken);

    /// <summary>
    /// Gets the embedded-language projection that owns an authored position,
    /// including a projection from a nested macro inside a reported Raven fragment.
    /// </summary>
    public MacroEmbeddedLanguageProjection? GetMacroEmbeddedLanguageProjection(
        int position,
        CancellationToken cancellationToken = default)
    {
        var treeLength = SyntaxTree.GetRoot(cancellationToken).FullSpan.End;
        if ((uint)position > (uint)treeLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        if (!MacroEmbeddedLanguageProjectionService.TryFindInvocationAtPosition(
                this,
                position,
                cancellationToken,
                out var syntax,
                out var resolutionContext) ||
            syntax is null ||
            resolutionContext is null)
        {
            return null;
        }

        return ReferenceEquals(syntax.SyntaxTree, SyntaxTree)
            ? GetMacroEmbeddedLanguageProjectionCore(syntax, cancellationToken)
            : MacroEmbeddedLanguageProjectionService.GetProjection(
                this,
                syntax,
                resolutionContext,
                cancellationToken);
    }

    internal MacroEmbeddedLanguageProjection? GetMacroEmbeddedLanguageProjectionCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        if (_macroEmbeddedLanguageProjectionCache.TryGetValue(syntax, out var cached))
            return cached;

        var projection = MacroEmbeddedLanguageProjectionService.GetProjection(this, syntax, cancellationToken);
        return projection is null
            ? null
            : _macroEmbeddedLanguageProjectionCache.GetOrAdd(syntax, projection);
    }

    internal MacroInputSnapshot GetMacroInputSnapshotCore(
        SyntaxNode syntax,
        CancellationToken cancellationToken)
    {
        ValidateMacroInvocationSyntax(syntax);

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        if (_macroInputSnapshotCache.TryGetValue(syntax, out var cached))
            return cached;

        FreestandingMacroInvocation.TryCreate(syntax, out var invocation);

        var snapshot = new MacroInputSnapshot(
            TextSpan.FromBounds(
                invocation.TokenTree?.OpenBraceToken.Span.End ?? syntax.Span.End,
                invocation.TokenTree is { } tokenTree
                    ? tokenTree.CloseBraceToken.IsMissing
                        ? tokenTree.BodyToken.Span.End
                        : tokenTree.CloseBraceToken.SpanStart
                    : syntax.Span.End),
            GetMacroTokensCore(syntax, cancellationToken),
            GetMacroFragmentRegionsCore(syntax, cancellationToken));
        return _macroInputSnapshotCache.GetOrAdd(syntax, snapshot);
    }

    private void ValidateMacroInvocationSyntax(SyntaxNode syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        if (syntax.SyntaxTree != SyntaxTree)
            throw new ArgumentException("Macro invocation is not part of this semantic model's syntax tree.", nameof(syntax));
        if (!FreestandingMacroInvocation.TryCreate(syntax, out _))
            throw new ArgumentException("Syntax is not a freestanding macro carrier.", nameof(syntax));
    }

    /// <summary>
    /// Gets completion items available at a position in this semantic model's syntax tree asynchronously.
    /// </summary>
    /// <param name="position">The zero-based position in the syntax tree.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A materialized set of completion items.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="position"/> is outside the tree bounds.</exception>
    public Task<ImmutableArray<CompletionItem>> GetCompletionsAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        var treeLength = SyntaxTree.GetRoot().FullSpan.End;
        if ((uint)position > (uint)treeLength)
            throw new ArgumentOutOfRangeException(nameof(position));

        return s_completionService.GetCompletionsAsync(this, position, cancellationToken);
    }

    public IImmutableList<Diagnostic> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        InvalidateStaleFreestandingMacroExpansions();

        if (_diagnostics is null)
            EnsureDiagnosticBindingCompleted(requireCompleteDeclarations: true, cancellationToken);

        return _diagnostics;
    }

    internal IImmutableList<Diagnostic> GetDocumentDiagnostics(CancellationToken cancellationToken = default)
    {
        InvalidateStaleFreestandingMacroExpansions();

        if (_documentDiagnostics is null)
            EnsureDiagnosticBindingCompleted(requireCompleteDeclarations: false, cancellationToken);

        return _documentDiagnostics;
    }

    private void EnsureDiagnosticBindingCompleted(
        bool requireCompleteDeclarations = true,
        CancellationToken cancellationToken = default)
    {
        lock (_diagnosticsCollectionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (requireCompleteDeclarations && _diagnostics is not null)
                return;

            if (!requireCompleteDeclarations && _documentDiagnostics is not null)
                return;

            var currentThreadId = Environment.CurrentManagedThreadId;
            if (_isCollectingDiagnostics)
            {
                if (_diagnosticCollectionThreadId == currentThreadId)
                    return;
            }

            _isCollectingDiagnostics = true;
            _diagnosticCollectionThreadId = currentThreadId;
            var previousDiagnosticBindingCancellationToken = _diagnosticBindingCancellationToken;
            _diagnosticBindingCancellationToken = cancellationToken;

            try
            {
                var diagnosticInstrumentation = Compilation.PerformanceInstrumentation.DiagnosticBinding;
                diagnosticInstrumentation.RecordCall(requireCompleteDeclarations);
                cancellationToken.ThrowIfCancellationRequested();
                var root = SyntaxTree.GetRoot();
                using var sourceNamespaceLookupSuppression = requireCompleteDeclarations
                    ? null
                    : Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();

                if (!requireCompleteDeclarations &&
                    TryCollectTransferredDocumentDiagnostics(root, out var transferredDiagnostics))
                {
                    _documentDiagnostics = transferredDiagnostics;
                    diagnosticInstrumentation.RecordTransferredDocumentHit();
                    return;
                }

                var phaseStart = Stopwatch.GetTimestamp();
                if (requireCompleteDeclarations)
                {
                    Compilation.EnsureSourceDeclarationsComplete();
                    EnsureDeclarations();
                }
                else
                {
                    // Document diagnostics should first use demand-driven source declaration
                    // lookup. Eagerly declaring every syntax tree here makes each edit pay a
                    // project-wide declaration cost even when the edited owner only references
                    // metadata or declarations already available from its own file.
                    if (!Compilation.IsSemanticDiagnosticTransferBlocked(SyntaxTree) &&
                        root is CompilationUnitSyntax compilationUnit)
                    {
                        EnsureTopLevelFunctionDeclarations(compilationUnit, ensureSourceDeclarations: false);
                    }

                    EnsureDeclarations();
                    EnsureMemberSignaturesDeclared();
                }

                diagnosticInstrumentation.RecordDeclarationTicks(Stopwatch.GetTimestamp() - phaseStart);

                phaseStart = Stopwatch.GetTimestamp();
                var binder = requireCompleteDeclarations
                    ? GetRootBinderForCompleteDiagnostics(root)
                    : GetBinderForIncrementalSemanticQuery(root);
                diagnosticInstrumentation.RecordBinderSelectionTicks(Stopwatch.GetTimestamp() - phaseStart);

                var diagnosticsBuilder = ImmutableArray.CreateBuilder<Diagnostic>();
                if (!requireCompleteDeclarations &&
                    TryCollectIncrementalDiagnostics(root, binder, diagnosticsBuilder, diagnosticInstrumentation))
                {
                    diagnosticInstrumentation.RecordIncrementalPass();
                }
                else
                {
                    diagnosticInstrumentation.RecordFullPass();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!requireCompleteDeclarations)
                    {
                        phaseStart = Stopwatch.GetTimestamp();
                        Compilation.EnsureSourceDeclarationsDeclared();
                        EnsureDeclarations();
                        diagnosticInstrumentation.RecordDeclarationTicks(Stopwatch.GetTimestamp() - phaseStart);
                    }

                    phaseStart = Stopwatch.GetTimestamp();
                    foreach (var binderState in _binderCache.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Declaration and import-scope binders own diagnostics produced
                        // while deriving the scope itself. Keep those immutable syntax facts
                        // intact; the rebind below is for executable/body binders.
                        if (ShouldPreserveBinderDiagnosticsDuringExecutableRebind(binderState))
                            continue;

                        binderState.Diagnostics.ClearDiagnostics(root.Span);
                    }

                    ClearCachedBoundNodes(root.Span);
                    diagnosticInstrumentation.RecordClearTicks(Stopwatch.GetTimestamp() - phaseStart);

                    phaseStart = Stopwatch.GetTimestamp();
                    Traverse(root, binder);
                    diagnosticInstrumentation.RecordTraverseTicks(Stopwatch.GetTimestamp() - phaseStart);

                    cancellationToken.ThrowIfCancellationRequested();
                    phaseStart = Stopwatch.GetTimestamp();
                    DocumentationCommentValidator.Analyze(this, root, binder.Diagnostics);
                    diagnosticInstrumentation.RecordDocumentationTicks(Stopwatch.GetTimestamp() - phaseStart);

                    phaseStart = Stopwatch.GetTimestamp();
                    var rootDiagnostics = CollectAllBinderDiagnostics(cancellationToken);
                    StoreBoundDiagnostics(root, rootDiagnostics);
                    diagnosticsBuilder.AddRange(rootDiagnostics);
                    diagnosticInstrumentation.RecordCollectTicks(Stopwatch.GetTimestamp() - phaseStart);
                }

                cancellationToken.ThrowIfCancellationRequested();
                phaseStart = Stopwatch.GetTimestamp();
                AnalyzeIndexerDeclarationDiagnostics(root, diagnosticsBuilder);
                AnalyzeUnionVariantCardinalityDiagnostics(root, diagnosticsBuilder, requireCompleteDeclarations);
                diagnosticsBuilder.AddRange(_declarationDiagnostics.AsEnumerable());
                var diagnostics = diagnosticsBuilder
                    .Distinct()
                    .ToImmutableArray();
                diagnosticInstrumentation.RecordMaterializeTicks(Stopwatch.GetTimestamp() - phaseStart);

                if (requireCompleteDeclarations)
                    _diagnostics = diagnostics;
                else
                    _documentDiagnostics = diagnostics;

                phaseStart = Stopwatch.GetTimestamp();
                StoreSemanticDiagnosticDescriptors(root, diagnostics.ToImmutableArray());
                diagnosticInstrumentation.RecordStoreDescriptorTicks(Stopwatch.GetTimestamp() - phaseStart);
            }
            finally
            {
                _diagnosticBindingCancellationToken = previousDiagnosticBindingCancellationToken;
                _isCollectingDiagnostics = false;
                _diagnosticCollectionThreadId = 0;
            }
        }

        bool TryCollectTransferredDocumentDiagnostics(
            SyntaxNode root,
            out IImmutableList<Diagnostic> diagnostics)
        {
            diagnostics = ImmutableArray<Diagnostic>.Empty;

            if (Compilation.IsSemanticDiagnosticTransferBlocked(SyntaxTree))
                return false;

            var owners = GetExecutableOwnersForDiagnostics(root).ToArray();
            if (owners.Length == 0)
                return false;

            var builder = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var owner in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Compilation.IsChangedExecutableOwner(owner))
                    return false;

                if (!TryGetTransferredSemanticDiagnostics(owner, out var ownerDiagnostics))
                    return false;

                builder.AddRange(ownerDiagnostics);
            }

            if (_declarationsComplete)
                builder.AddRange(_declarationDiagnostics.AsEnumerable());

            diagnostics = builder
                .Distinct()
                .ToImmutableArray();
            return true;
        }

        void Traverse(SyntaxNode node, Binder currentBinder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is TypeSyntax)
                return;

            BindDeclarationAttributes(node, currentBinder);
            if (node is TypeDeclarationSyntax typeDeclaration)
                ValidatePrimaryConstructorParameters(typeDeclaration, currentBinder);

            if (node is GlobalStatementSyntax { Statement: FunctionStatementSyntax globalFunction })
            {
                var globalFunctionBinder = currentBinder as FunctionBinder
                    ?? GetBinderForDiagnostics(globalFunction, currentBinder) as FunctionBinder;
                if (globalFunctionBinder is not null)
                {
                    BindDeclarationAttributes(globalFunction, globalFunctionBinder);
                    ValidateFunctionParameters(globalFunction, globalFunctionBinder);
                    BindFunctionBody(globalFunction, globalFunctionBinder);
                    return;
                }
            }

            if (node is FunctionStatementSyntax functionStatement)
            {
                var functionBinder = currentBinder as FunctionBinder
                    ?? GetBinderForDiagnostics(functionStatement, currentBinder) as FunctionBinder;
                if (functionBinder is not null)
                {
                    ValidateFunctionParameters(functionStatement, functionBinder);
                    BindFunctionBody(functionStatement, functionBinder);
                    return;
                }
            }

            if (node is MacroDeclarationSyntax macro)
            {
                var macroBinder = currentBinder as MacroBinder
                    ?? GetBinderForDiagnostics(macro, currentBinder) as MacroBinder;
                if (macroBinder is not null)
                    BindMacroBody(macro, macroBinder);

                return;
            }

            if (TryTraverseTypeMemberDeclaration(node, currentBinder))
                return;

            if (node is AccessorListSyntax)
            {
                foreach (var child in node.ChildNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var childBinder = requireCompleteDeclarations
                        ? GetBinder(child, currentBinder)
                        : GetBinderForIncrementalSemanticQuery(child, currentBinder);
                    Traverse(child, childBinder);
                }

                return;
            }

            if (node is ExpressionSyntax or StatementSyntax)
            {
                currentBinder.GetOrBind(node);
                BindStatementAttributeSyntaxes(node, currentBinder);
                return;
            }

            foreach (var child in node.ChildNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var childBinder = requireCompleteDeclarations
                    ? GetBinder(child, currentBinder)
                    : GetBinderForIncrementalSemanticQuery(child, currentBinder);

                if (child is AttributeSyntax attributeSyntax)
                {
                    if (attributeSyntax.IsMacroAttribute())
                    {
                        _ = GetMacroExpansion(attributeSyntax);
                        continue;
                    }

                    // Attribute names/arguments have attribute-specific binding rules.
                    // Binding descendant expressions directly can produce bogus name lookup
                    // diagnostics (e.g. [Obsolete] resolving as an identifier expression).
                    var attributeBinder = childBinder as AttributeBinder
                        ?? new AttributeBinder(childBinder.ContainingSymbol, childBinder);
                    _ = attributeBinder.BindAttribute(attributeSyntax);
                    continue;
                }

                if (child is GlobalStatementSyntax global)
                {
                    if (global.Statement is FunctionStatementSyntax function)
                    {
                        var functionBinder = childBinder as FunctionBinder
                            ?? GetBinderForDiagnostics(function, childBinder) as FunctionBinder;
                        if (functionBinder is not null)
                        {
                            BindDeclarationAttributes(function, functionBinder);
                            ValidateFunctionParameters(function, functionBinder);
                            BindFunctionBody(function, functionBinder);
                            continue;
                        }
                    }

                    // Bind the contained statement so locals are registered
                    childBinder.GetOrBind(global.Statement);
                    BindStatementAttributeSyntaxes(global, childBinder);
                    continue;
                }

                if (child is FunctionStatementSyntax childFunctionStatement)
                {
                    var functionBinder = childBinder as FunctionBinder
                        ?? GetBinderForDiagnostics(childFunctionStatement, childBinder) as FunctionBinder;
                    if (functionBinder is not null)
                    {
                        BindDeclarationAttributes(childFunctionStatement, functionBinder);
                        ValidateFunctionParameters(childFunctionStatement, functionBinder);
                        BindFunctionBody(childFunctionStatement, functionBinder);
                        continue;
                    }
                }

                if (TryTraverseTypeMemberDeclaration(child, childBinder))
                    continue;

                if (child is AccessorListSyntax)
                {
                    Traverse(child, childBinder);
                    continue;
                }

                if (child is TypeSyntax)
                    continue;

                if (child is ExpressionSyntax || child is StatementSyntax)
                {
                    childBinder.GetOrBind(child);
                    BindStatementAttributeSyntaxes(child, childBinder);
                    continue;
                }

                Traverse(child, childBinder);
            }
        }

        void BindFunctionBody(FunctionStatementSyntax function, FunctionBinder functionBinder)
        {
            _ = functionBinder.GetMethodSymbol();
            var methodBodyBinder = functionBinder.GetMethodBodyBinder();
            var useCompleteBodyBinder = function.Parent is GlobalStatementSyntax globalStatement &&
                Compilation.IsTopLevelFunctionMember(globalStatement);

            if (function.Body is { } body)
            {
                var bodyBinder = useCompleteBodyBinder
                    ? GetBinder(body, methodBodyBinder)
                    : GetBinderForDiagnostics(body, methodBodyBinder);
                Traverse(body, bodyBinder);
                return;
            }

            if (function.ExpressionBody is { } expressionBody)
            {
                var expressionBinder = useCompleteBodyBinder
                    ? GetBinder(expressionBody, methodBodyBinder)
                    : GetBinderForDiagnostics(expressionBody, methodBodyBinder);
                Traverse(expressionBody, expressionBinder);
                ReportStructUnionDefaultExpressionBodyReturn(functionBinder, expressionBody, expressionBinder);
            }
        }

        void BindMacroBody(
            MacroDeclarationSyntax macro,
            MacroBinder macroBinder)
        {
            _ = macroBinder.GetMacroSymbol();

            if (macro.Body is { } body)
            {
                Traverse(body, GetBinderForDiagnostics(body, macroBinder));
                return;
            }

            if (macro.ExpressionBody is { } expressionBody)
                Traverse(expressionBody, GetBinderForDiagnostics(expressionBody, macroBinder));
        }

        void ReportStructUnionDefaultExpressionBodyReturn(
            FunctionBinder functionBinder,
            ArrowExpressionClauseSyntax expressionBody,
            Binder expressionBinder)
        {
            if (expressionBody.Expression is not DefaultExpressionSyntax defaultExpression)
                return;

            var method = functionBinder.GetMethodSymbol();
            var returnType = method.IsAsync &&
                AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, method.ReturnType) is { } asyncReturnType
                    ? asyncReturnType
                    : method.ReturnType;

            if (returnType.TryGetUnion() is not { TypeKind: TypeKind.Struct })
                return;

            var unionType = returnType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
            expressionBinder.Diagnostics.ReportStructUnionReturnMayBeDefault(unionType, defaultExpression.GetLocation());
        }

        bool TryTraverseTypeMemberDeclaration(SyntaxNode node, Binder currentBinder)
        {
            if (currentBinder is not TypeMemberBinder and not TypeDeclarationBinder)
                return false;

            if (node is MemberDeclarationSyntax originalMember &&
                TryGetMacroReplacementSyntax(originalMember, out var replacementNode) &&
                replacementNode is MemberDeclarationSyntax replacementMember)
            {
                node = replacementMember;
            }

            switch (node)
            {
                case MethodDeclarationSyntax method:
                    BindMemberAttributes(method, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } methodMemberBinder)
                    {
                        var methodBinder = methodMemberBinder.BindMethodDeclaration(method);
                        CacheBinderForNode(method, methodBinder);
                        if (methodBinder.ContainingSymbol is IMethodSymbol methodSymbol)
                            RegisterMethodSymbol(method, methodSymbol);
                        TraverseMethodLikeBody(method, methodBinder);
                    }

                    return true;

                case OperatorDeclarationSyntax operatorDeclaration:
                    BindMemberAttributes(operatorDeclaration, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } operatorMemberBinder)
                    {
                        var methodBinder = operatorMemberBinder.BindOperatorDeclaration(operatorDeclaration);
                        CacheBinderForNode(operatorDeclaration, methodBinder);
                        TraverseMethodLikeBody(operatorDeclaration, methodBinder);
                    }

                    return true;

                case ConversionOperatorDeclarationSyntax conversion:
                    BindMemberAttributes(conversion, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } conversionMemberBinder)
                    {
                        var methodBinder = conversionMemberBinder.BindConversionOperatorDeclaration(conversion);
                        CacheBinderForNode(conversion, methodBinder);
                        TraverseMethodLikeBody(conversion, methodBinder);
                    }

                    return true;

                case ConstructorDeclarationSyntax constructor:
                    BindMemberAttributes(constructor, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } constructorMemberBinder)
                    {
                        var methodBinder = constructorMemberBinder.BindConstructorDeclaration(constructor);
                        CacheBinderForNode(constructor, methodBinder);
                        TraverseMethodLikeBody(constructor, methodBinder);
                    }

                    return true;

                case ParameterlessConstructorDeclarationSyntax init:
                    BindMemberAttributes(init, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } initMemberBinder)
                    {
                        var methodBinder = initMemberBinder.BindInitDeclaration(init);
                        CacheBinderForNode(init, methodBinder);
                        TraverseParameterlessConstructorBody(init, methodBinder);
                    }

                    return true;

                case InitializerBlockDeclarationSyntax initializer:
                    BindMemberAttributes(initializer, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } initializerMemberBinder)
                    {
                        var methodBinder = initializerMemberBinder.BindInitBlockDeclaration(initializer);
                        CacheBinderForNode(initializer, methodBinder);
                        TraverseInitializerBlockBody(initializer, methodBinder);
                    }

                    return true;

                case FinallyDeclarationSyntax finalizer:
                    BindMemberAttributes(finalizer, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } finalizerMemberBinder)
                    {
                        var methodBinder = finalizerMemberBinder.BindFinallyDeclaration(finalizer);
                        CacheBinderForNode(finalizer, methodBinder);
                        TraverseFinalizerBody(finalizer, methodBinder);
                    }

                    return true;

                case FieldDeclarationSyntax field:
                    BindMemberAttributes(field, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } fieldMemberBinder)
                    {
                        fieldMemberBinder.BindFieldDeclaration(field);
                        CacheBinderForNode(field, fieldMemberBinder);
                    }

                    return true;

                case ConstDeclarationSyntax constant:
                    BindMemberAttributes(constant, currentBinder);
                    if (GetDeclarationMemberBinder(currentBinder) is { } constMemberBinder)
                    {
                        constMemberBinder.BindConstDeclaration(constant);
                        CacheBinderForNode(constant, constMemberBinder);
                    }

                    return true;

                case PropertyDeclarationSyntax property:
                    BindMemberAttributes(property, currentBinder);
                    var propertyBinder = GetDeclarationMemberBinder(currentBinder)
                        ?? GetBinderForDiagnostics(property, currentBinder);
                    if (propertyBinder is TypeMemberBinder propertyMemberBinder)
                    {
                        var accessorBinders = propertyMemberBinder.BindPropertyDeclaration(property);
                        CacheBinderForNode(property, propertyMemberBinder);
                        foreach (var (accessor, accessorBinder) in accessorBinders)
                            CacheBinderForNode(accessor, accessorBinder);
                    }

                    TraversePropertyBody(property, propertyBinder);
                    return true;

                case IndexerDeclarationSyntax indexer:
                    BindMemberAttributes(indexer, currentBinder);
                    var indexerBinder = GetDeclarationMemberBinder(currentBinder)
                        ?? GetBinderForDiagnostics(indexer, currentBinder);
                    if (indexerBinder is TypeMemberBinder indexerMemberBinder)
                    {
                        ValidateRegularParameters(indexer.ParameterList.Parameters, indexerMemberBinder.Diagnostics);
                        var accessorBinders = indexerMemberBinder.BindIndexerDeclaration(indexer);
                        CacheBinderForNode(indexer, indexerMemberBinder);
                        foreach (var (accessor, accessorBinder) in accessorBinders)
                            CacheBinderForNode(accessor, accessorBinder);
                    }

                    TraversePropertyBody(indexer, indexerBinder);
                    return true;

                case EventDeclarationSyntax eventDeclaration:
                    BindMemberAttributes(eventDeclaration, currentBinder);
                    if (eventDeclaration.AccessorList is { } eventAccessors)
                        Traverse(eventAccessors, GetBinderForDiagnostics(eventAccessors, currentBinder));
                    return true;

                default:
                    return false;
            }
        }

        TypeMemberBinder? GetDeclarationMemberBinder(Binder currentBinder)
            => currentBinder switch
            {
                MethodBinder { ParentBinder: TypeMemberBinder parentMemberBinder } => parentMemberBinder,
                TypeMemberBinder typeMemberBinder => typeMemberBinder,
                TypeDeclarationBinder { ContainingSymbol: INamedTypeSymbol containingType } => new TypeMemberBinder(currentBinder, containingType),
                _ => null
            };

        void TraverseMethodLikeBody(BaseMethodDeclarationSyntax method, MethodBinder methodBinder)
        {
            if (method.Body is { } body)
            {
                Traverse(body, GetBinderForDiagnostics(body, methodBinder));
                return;
            }

            if (method.ExpressionBody is { } expressionBody)
                Traverse(expressionBody, GetBinderForDiagnostics(expressionBody, methodBinder));
        }

        void TraverseParameterlessConstructorBody(ParameterlessConstructorDeclarationSyntax init, MethodBinder methodBinder)
        {
            if (init.Body is { } body)
            {
                Traverse(body, GetBinderForDiagnostics(body, methodBinder));
                return;
            }

            if (init.ExpressionBody is { } expressionBody)
                Traverse(expressionBody, GetBinderForDiagnostics(expressionBody, methodBinder));
        }

        void TraverseInitializerBlockBody(InitializerBlockDeclarationSyntax initializer, MethodBinder methodBinder)
            => Traverse(initializer.Body, GetBinderForDiagnostics(initializer.Body, methodBinder));

        void TraverseFinalizerBody(FinallyDeclarationSyntax finalizer, MethodBinder methodBinder)
            => Traverse(finalizer.Body, GetBinderForDiagnostics(finalizer.Body, methodBinder));

        void ValidatePrimaryConstructorParameters(TypeDeclarationSyntax typeDeclaration, Binder currentBinder)
        {
            if (typeDeclaration.ParameterList is not { } parameterList)
                return;

            foreach (var parameter in parameterList.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (parameter.TypeAnnotation is null)
                {
                    currentBinder.Diagnostics.ReportParameterTypeAnnotationRequired(
                        parameter.Identifier.ValueText,
                        parameter.Identifier.GetLocation());
                }
            }
        }

        void ValidateFunctionParameters(FunctionStatementSyntax function, FunctionBinder functionBinder)
        {
            ValidateRegularParameters(function.ParameterList.Parameters, functionBinder.Diagnostics);

            _ = functionBinder.GetMethodSymbol();
            var methodBinder = functionBinder.GetMethodBodyBinder();
            foreach (var parameter in function.ParameterList.Parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (parameter.TypeAnnotation?.Type is not { } typeSyntax)
                    continue;

                var boundTypeSyntax = ParameterSyntaxUtilities.GetRefKind(parameter).IsByRef &&
                    typeSyntax is ByRefTypeSyntax byRefType
                        ? byRefType.ElementType
                        : typeSyntax;
                var parameterType = methodBinder.BindTypeSyntaxAndReport(boundTypeSyntax);
                _ = methodBinder.EnsureTypeValidForStorageLocation(parameterType, boundTypeSyntax.GetLocation());
            }
        }

        void ValidateRegularParameters(IEnumerable<ParameterSyntax> parameters, DiagnosticBag diagnostics)
        {
            foreach (var parameter in parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (parameter.TypeAnnotation is null)
                {
                    diagnostics.ReportParameterTypeAnnotationRequired(
                        parameter.Identifier.ValueText,
                        parameter.Identifier.GetLocation());
                }

                if (parameter.BindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                {
                    diagnostics.ReportParameterBindingKeywordNotAllowed(
                        parameter.BindingKeyword.Text,
                        parameter.Identifier.ValueText,
                        parameter.BindingKeyword.GetLocation());
                }

                if (parameter.ParamsKeyword.Kind == SyntaxKind.ParamsKeyword &&
                    parameter.DotDotDotToken.Kind == SyntaxKind.DotDotDotToken)
                {
                    diagnostics.ReportVarParamsMarkersAreMutuallyExclusive(
                        parameter.Identifier.ValueText,
                        parameter.GetLocation());
                }
            }
        }

        void AnalyzeIndexerDeclarationDiagnostics(
            SyntaxNode root,
            ImmutableArray<Diagnostic>.Builder diagnosticsBuilder)
        {
            foreach (var indexer in root.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var parameter in indexer.ParameterList.Parameters)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (parameter.TypeAnnotation is null)
                    {
                        diagnosticsBuilder.Add(Diagnostic.Create(
                            CompilerDiagnostics.ParameterTypeAnnotationRequired,
                            parameter.Identifier.GetLocation(),
                            parameter.Identifier.ValueText));
                    }

                    if (parameter.BindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                    {
                        diagnosticsBuilder.Add(Diagnostic.Create(
                            CompilerDiagnostics.ParameterBindingKeywordNotAllowed,
                            parameter.BindingKeyword.GetLocation(),
                            parameter.BindingKeyword.Text,
                            parameter.Identifier.ValueText));
                    }
                }

                var hasAsyncGetter = indexer.AccessorList?.Accessors.Any(static accessor =>
                    accessor.Kind == SyntaxKind.GetAccessorDeclaration &&
                    accessor.Modifiers.Any(static modifier => modifier.Kind == SyntaxKind.AsyncKeyword)) == true;
                if (!hasAsyncGetter)
                    continue;

                var propertyTypeSyntax = indexer.Type.Type;
                var typeBinder = GetBinderForIncrementalSemanticQuery(propertyTypeSyntax);
                var propertyType = typeBinder.BindTypeSyntaxAndReport(propertyTypeSyntax);
                if (AsyncReturnTypeUtilities.IsValidAsyncReturnType(propertyType, allowErrorType: false))
                    continue;

                if (propertyType.TypeKind == TypeKind.Error &&
                    propertyTypeSyntax is IdentifierNameSyntax identifierName)
                {
                    diagnosticsBuilder.Add(Diagnostic.Create(
                        CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext,
                        propertyTypeSyntax.GetLocation(),
                        identifierName.Identifier.ValueText));
                }

                var display = propertyType.TypeKind == TypeKind.Error
                    ? propertyTypeSyntax.ToString()
                    : propertyType.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var suggestedReturnType = AsyncReturnTypeUtilities.GetSuggestedAsyncReturnTypeDisplay(
                    Compilation,
                    propertyType);
                diagnosticsBuilder.Add(Diagnostic.Create(
                    CompilerDiagnostics.AsyncReturnTypeMustBeTaskLike,
                    propertyTypeSyntax.GetLocation(),
                    display,
                    suggestedReturnType));
            }
        }

        void TraversePropertyBody(BasePropertyDeclarationSyntax property, Binder currentBinder)
        {
            if (property.AccessorList is { } accessorList)
                Traverse(accessorList, GetBinderForDiagnostics(accessorList, currentBinder));

            var expressionBody = property switch
            {
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.ExpressionBody,
                IndexerDeclarationSyntax indexerDeclaration => indexerDeclaration.ExpressionBody,
                _ => null
            };

            if (expressionBody is not null)
                Traverse(expressionBody, GetBinderForDiagnostics(expressionBody, currentBinder));
        }

        void BindMemberAttributes(MemberDeclarationSyntax member, Binder currentBinder)
        {
            foreach (var attributeList in member.AttributeLists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributeListBinder = GetBinderForDiagnostics(attributeList, currentBinder);
                foreach (var attribute in attributeList.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Traverse(attribute, GetBinderForDiagnostics(attribute, attributeListBinder));
                }
            }
        }

        void BindDeclarationAttributes(SyntaxNode declaration, Binder currentBinder)
        {
            var attributeLists = GetDeclarationAttributeLists(declaration);
            if (attributeLists.Count == 0)
                return;

            var seenAttributes = new Dictionary<AttributeTargets, HashSet<INamedTypeSymbol>>();
            foreach (var attributeList in attributeLists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attributeListBinder = GetBinderForDiagnostics(attributeList, currentBinder);
                var owner = ResolveAttributeOwner(declaration, attributeList, currentBinder, attributeListBinder);
                if (owner is SynthesizedNamespaceMembersClassSymbol namespaceMembersContainer)
                {
                    _ = namespaceMembersContainer.GetAttributes();
                    continue;
                }

                var defaultTarget = AttributeUsageHelper.GetDefaultTargetForOwner(owner);

                foreach (var attribute in attributeList.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (attribute.IsMacroAttribute())
                    {
                        _ = GetMacroExpansion(attribute);
                        continue;
                    }

                    var attributeBinder = GetBinderForDiagnostics(attribute, attributeListBinder) as AttributeBinder
                        ?? new AttributeBinder(owner, attributeListBinder);
                    RemoveCachedBoundNode(attribute);
                    var boundAttribute = attributeBinder.BindAttribute(attribute);
                    var data = AttributeDataFactory.Create(boundAttribute, attribute);

                    if (data is not null)
                    {
                        AttributeUsageHelper.TryValidateAttribute(
                            Compilation,
                            attributeBinder,
                            owner,
                            attribute,
                            data,
                            defaultTarget,
                            seenAttributes);
                    }
                }
            }
        }

        static IReadOnlyList<AttributeListSyntax> GetDeclarationAttributeLists(SyntaxNode declaration)
            => declaration switch
            {
                CompilationUnitSyntax compilationUnit => compilationUnit.AttributeLists,
                BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.AttributeLists,
                BaseTypeDeclarationSyntax typeDeclaration => typeDeclaration.AttributeLists,
                DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.AttributeLists,
                EnumMemberDeclarationSyntax enumMember => enumMember.AttributeLists,
                MethodDeclarationSyntax methodDeclaration => methodDeclaration.AttributeLists,
                FunctionStatementSyntax functionStatement => functionStatement.AttributeLists,
                MacroDeclarationSyntax macro => macro.AttributeLists,
                ConstructorDeclarationSyntax constructorDeclaration => constructorDeclaration.AttributeLists,
                ParameterlessConstructorDeclarationSyntax initDeclaration => initDeclaration.AttributeLists,
                InitializerBlockDeclarationSyntax initBlockDeclaration => initBlockDeclaration.AttributeLists,
                FinallyDeclarationSyntax finallyDeclaration => finallyDeclaration.AttributeLists,
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.AttributeLists,
                IndexerDeclarationSyntax indexerDeclaration => indexerDeclaration.AttributeLists,
                EventDeclarationSyntax eventDeclaration => eventDeclaration.AttributeLists,
                AccessorDeclarationSyntax accessorDeclaration => accessorDeclaration.AttributeLists,
                FieldDeclarationSyntax fieldDeclaration => fieldDeclaration.AttributeLists,
                ConstDeclarationSyntax constDeclaration => constDeclaration.AttributeLists,
                ParameterSyntax parameter => parameter.AttributeLists,
                ArrowTypeClauseSyntax arrowTypeClause => arrowTypeClause.AttributeLists,
                _ => []
            };

        ISymbol ResolveAttributeOwner(
            SyntaxNode declaration,
            AttributeListSyntax attributeList,
            Binder currentBinder,
            Binder attributeListBinder)
        {
            if (declaration is CompilationUnitSyntax)
            {
                if (HasExplicitAttributeTarget(attributeList, "module"))
                    return Compilation.Module;

                return Compilation.Assembly;
            }

            if (HasExplicitAttributeTarget(attributeList, "class"))
            {
                if (AttributeUsageHelper.IsNamespaceContainerAttributeDeclaration(attributeList) &&
                    declaration.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or GlobalStatementSyntax)
                {
                    var namespaceName = string.Join(
                        ".",
                        declaration.Ancestors()
                            .OfType<BaseNamespaceDeclarationSyntax>()
                            .Reverse()
                            .Select(static current => current.Name.ToString()));
                    var namespaceSymbol = string.IsNullOrWhiteSpace(namespaceName)
                        ? Compilation.SourceGlobalNamespace
                        : Compilation.GetOrCreateNamespaceSymbol(namespaceName)?.AsSourceNamespace();
                    if (namespaceSymbol is not null)
                        return Compilation.GetOrCreateNamespaceMembersContainer(namespaceSymbol, declaration);
                }
            }

            if (declaration is TypeDeclarationSyntax { ParameterList: not null } typeDeclaration &&
                HasExplicitAttributeTarget(attributeList, "method") &&
                TryResolvePrimaryConstructor(typeDeclaration, currentBinder, out var primaryConstructor))
            {
                return primaryConstructor;
            }

            if (declaration is BaseTypeDeclarationSyntax or InterfaceDeclarationSyntax or ExtensionDeclarationSyntax or
                UnionDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax)
            {
                return GetDeclaredTypeSymbol(declaration);
            }

            // Resolve member ownership through attached-macro replacement, just
            // as member body traversal does, while retaining authored attributes.
            if (TryGetMacroReplacementSyntax(declaration, out var replacementDeclaration))
                declaration = replacementDeclaration;

            if (declaration is PropertyDeclarationSyntax &&
                HasExplicitAttributeTarget(attributeList, "field") &&
                currentBinder.BindDeclaredSymbol(declaration) is SourcePropertySymbol { BackingField: { } backingField })
            {
                return backingField;
            }

            if (declaration is EventDeclarationSyntax &&
                HasExplicitAttributeTarget(attributeList, "field") &&
                currentBinder.BindDeclaredSymbol(declaration) is SourceEventSymbol { BackingField: { } eventBackingField })
            {
                return eventBackingField;
            }

            if (declaration is FieldDeclarationSyntax fieldDeclaration)
            {
                var declarator = fieldDeclaration.Declaration.Declarators.FirstOrDefault();
                if (declarator is not null &&
                    currentBinder.BindDeclaredSymbol(declarator) is { } field)
                {
                    return field;
                }
            }

            if (declaration is ConstDeclarationSyntax constDeclaration)
            {
                var declarator = constDeclaration.Declaration.Declarators.FirstOrDefault();
                if (declarator is not null &&
                    currentBinder.BindDeclaredSymbol(declarator) is { } constant)
                {
                    return constant;
                }
            }

            return currentBinder.BindDeclaredSymbol(declaration)
                ?? attributeListBinder.ContainingSymbol
                ?? currentBinder.ContainingSymbol
                ?? Compilation.Assembly;
        }

        static bool TryResolvePrimaryConstructor(
            TypeDeclarationSyntax declaration,
            Binder currentBinder,
            out IMethodSymbol primaryConstructor)
        {
            if (currentBinder.BindDeclaredSymbol(declaration) is INamedTypeSymbol type)
            {
                foreach (var constructor in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (constructor.MethodKind != MethodKind.Constructor)
                        continue;

                    if (constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() == declaration))
                    {
                        primaryConstructor = constructor;
                        return true;
                    }
                }
            }

            primaryConstructor = null!;
            return false;
        }

        static bool HasExplicitAttributeTarget(AttributeListSyntax attributeList, string targetName)
            => string.Equals(
                attributeList.Target?.Identifier.ValueText,
                targetName,
                StringComparison.OrdinalIgnoreCase);

        Binder GetBinderForDiagnostics(SyntaxNode node, Binder parentBinder)
            => requireCompleteDeclarations
                ? GetBinder(node, parentBinder)
                : GetBinderForIncrementalSemanticQuery(node, parentBinder);

        bool TryCollectIncrementalDiagnostics(
            SyntaxNode root,
            Binder rootBinder,
            ImmutableArray<Diagnostic>.Builder diagnosticsBuilder,
            DiagnosticBindingInstrumentation diagnosticInstrumentation)
        {
            if (Compilation.IsSemanticDiagnosticTransferBlocked(SyntaxTree))
                return false;

            var allOwners = GetExecutableOwnersForDiagnostics(root).ToArray();
            var changedOwners = allOwners
                .Where(owner =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Compilation.IsChangedExecutableOwner(owner);
                })
                .ToArray();

            if (changedOwners.Length == 0)
                return TryCollectTransferredDiagnosticsForAllOwners(root, rootBinder, allOwners, diagnosticsBuilder) ||
                       TryCollectOwnerScopedDiagnosticsForAllOwners(root, rootBinder, allOwners, diagnosticsBuilder, diagnosticInstrumentation);

            changedOwners = RemoveChangedAncestorsCoveredByChangedNestedOwners(changedOwners);

            var changedOwnerSet = changedOwners
                .Select(static owner => new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind))
                .ToHashSet();

            var orderSensitiveChangedGlobalOwners = changedOwners
                .OfType<GlobalStatementSyntax>()
                .ToArray();

            var ownersToBind = changedOwners
                .Where(owner => !owner.Ancestors()
                    .Any(ancestor => IsExecutableOwnerForDiagnostics(ancestor) &&
                                     changedOwnerSet.Contains(new Compilation.ExecutableOwnerDescriptor(ancestor.Span, ancestor.Kind))))
                .Where(owner => owner is GlobalStatementSyntax ||
                                !orderSensitiveChangedGlobalOwners.Any(globalOwner =>
                                    globalOwner.Span.Start <= owner.Span.Start &&
                                    globalOwner.Span.End >= owner.Span.End))
                .Concat(orderSensitiveChangedGlobalOwners)
                .Distinct()
                .ToArray();

            if (ownersToBind.Length == 0)
                return false;

            ownersToBind = ExpandTopLevelOrderSensitiveOwners(root, ownersToBind);

            var missingTransferOwners = new List<SyntaxNode>();
            foreach (var owner in allOwners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ContainsOwner(ownersToBind, owner))
                    continue;

                if (IsCoveredBy(owner, ownersToBind))
                    continue;

                if (!TryGetTransferredSemanticDiagnostics(owner, out _))
                    missingTransferOwners.Add(owner);
            }

            if (missingTransferOwners.Count > 0)
            {
                var candidateOwnersToBind = ownersToBind
                    .Concat(missingTransferOwners)
                    .DistinctBy(static owner => owner, ReferenceEqualityComparer.Instance)
                    .ToArray();
                ownersToBind = candidateOwnersToBind
                    .Where(owner => !IsCoveredBy(owner, candidateOwnersToBind))
                    .ToArray();
            }

            var ownersToBindSet = ownersToBind
                .Select(static owner => new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind))
                .ToHashSet();

            foreach (var owner in allOwners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ownersToBindSet.Contains(new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind)))
                    continue;

                if (IsCoveredBy(owner, ownersToBind))
                    continue;

                if (!TryGetTransferredSemanticDiagnostics(owner, out var diagnostics))
                    return false;

                diagnosticsBuilder.AddRange(diagnostics);
            }

            foreach (var owner in ownersToBind)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClearCachedBinderDiagnostics(owner.Span);
                ClearCachedBoundNodes(owner.Span);

                var ownerBinder = ReferenceEquals(owner, root)
                    ? rootBinder
                    : requireCompleteDeclarations
                        ? GetBinder(owner)
                        : GetBinderForIncrementalSemanticQuery(owner);

                if (owner is GlobalStatementSyntax globalOwner)
                    BindPrecedingGlobalStatementsForScope(root, globalOwner, ownerBinder, requireCompleteDeclarations);
                else if (ReferenceEquals(owner, root) &&
                    root is CompilationUnitSyntax compilationUnit &&
                    Compilation.HasRunnableFileScopeCode(compilationUnit))
                {
                    EnsureTopLevelCompilationUnitBound(compilationUnit, ensureSourceDeclarations: requireCompleteDeclarations);
                    BindGlobalStatementDeclarationsForScope(root, ownerBinder, requireCompleteDeclarations);
                }

                Traverse(owner, ownerBinder);

                var ownerDiagnostics = CollectBinderDiagnosticsForOwner(owner, cancellationToken);
                StoreBoundDiagnostics(owner, ownerDiagnostics);
                diagnosticsBuilder.AddRange(ownerDiagnostics);

                StoreSemanticDiagnosticDescriptors(
                    owner,
                    ownerDiagnostics);
            }

            DocumentationCommentValidator.Analyze(this, root, rootBinder.Diagnostics);
            diagnosticsBuilder.AddRange(rootBinder.Diagnostics.AsEnumerable()
                .Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, SyntaxTree) &&
                                     !allOwners.Any(owner => owner.Span.IntersectsWith(diagnostic.Location.SourceSpan))));
            return true;

            static bool ContainsOwner(IEnumerable<SyntaxNode> owners, SyntaxNode owner)
                => owners.Contains(owner, ReferenceEqualityComparer.Instance);

            static bool IsCoveredBy(SyntaxNode owner, IEnumerable<SyntaxNode> coveringOwners)
                => coveringOwners.Any(ownerToBind =>
                    !ReferenceEquals(ownerToBind, owner) &&
                    ownerToBind.Span.Start <= owner.Span.Start &&
                    ownerToBind.Span.End >= owner.Span.End);

            SyntaxNode[] RemoveChangedAncestorsCoveredByChangedNestedOwners(SyntaxNode[] owners)
            {
                if (owners.Length <= 1)
                    return owners;

                return owners
                    .Where(owner => !ShouldTransferAncestorDiagnostics(owner))
                    .ToArray();

                bool ShouldTransferAncestorDiagnostics(SyntaxNode owner)
                {
                    if (!Compilation.TryGetExecutableOwnerChange(owner, out var change) ||
                        change.Kind is Compilation.OwnerRelativeChangeKind.SignatureOrDeclaration or Compilation.OwnerRelativeChangeKind.Unknown)
                    {
                        return false;
                    }

                    if (!Compilation.TryGetSemanticDiagnosticDescriptors(owner, out _))
                        return false;

                    var changedSpan = new Text.TextSpan(owner.Span.Start + change.CurrentSpan.Start, change.CurrentSpan.Length);
                    return owners.Any(candidate =>
                        !ReferenceEquals(candidate, owner) &&
                        ContainsSpan(owner.Span, candidate.Span) &&
                        ContainsOrTouchesInsertion(candidate.Span, changedSpan));
                }
            }

            void ClearCachedBinderDiagnostics(Text.TextSpan span)
            {
                foreach (var binderState in _binderCache.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ShouldPreserveBinderDiagnosticsDuringExecutableRebind(binderState))
                        continue;

                    binderState.Diagnostics.ClearDiagnostics(span);
                }
            }
        }

        bool TryCollectOwnerScopedDiagnosticsForAllOwners(
            SyntaxNode root,
            Binder rootBinder,
            SyntaxNode[] allOwners,
            ImmutableArray<Diagnostic>.Builder diagnosticsBuilder,
            DiagnosticBindingInstrumentation diagnosticInstrumentation)
        {
            if (allOwners.Length == 0)
                return false;

            var outerOwners = allOwners
                .Where(owner => !owner.Ancestors().Any(IsExecutableOwnerForDiagnostics))
                .ToArray();

            var ownersToBind = outerOwners
                .Where(owner => !TryGetCachedBoundDiagnostics(owner, out _))
                .ToArray();

            var phaseStart = Stopwatch.GetTimestamp();
            foreach (var owner in ownersToBind)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClearCachedBinderDiagnostics(owner.Span);
                ClearCachedBoundNodes(owner.Span);
            }

            diagnosticInstrumentation.RecordClearTicks(Stopwatch.GetTimestamp() - phaseStart);

            phaseStart = Stopwatch.GetTimestamp();
            foreach (var owner in ownersToBind)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ownerBinder = ReferenceEquals(owner, root)
                    ? rootBinder
                    : requireCompleteDeclarations
                        ? GetBinder(owner)
                        : GetBinderForIncrementalSemanticQuery(owner);

                if (owner is GlobalStatementSyntax globalOwner)
                    BindPrecedingGlobalStatementsForScope(root, globalOwner, ownerBinder, requireCompleteDeclarations);
                else if (ReferenceEquals(owner, root) &&
                    root is CompilationUnitSyntax compilationUnit &&
                    Compilation.HasRunnableFileScopeCode(compilationUnit))
                {
                    EnsureTopLevelCompilationUnitBound(compilationUnit, ensureSourceDeclarations: requireCompleteDeclarations);
                    BindGlobalStatementDeclarationsForScope(root, ownerBinder, requireCompleteDeclarations);
                }

                Traverse(owner, ownerBinder);
            }

            diagnosticInstrumentation.RecordTraverseTicks(Stopwatch.GetTimestamp() - phaseStart);

            phaseStart = Stopwatch.GetTimestamp();
            DocumentationCommentValidator.Analyze(this, root, rootBinder.Diagnostics);
            diagnosticInstrumentation.RecordDocumentationTicks(Stopwatch.GetTimestamp() - phaseStart);

            phaseStart = Stopwatch.GetTimestamp();
            var collectedOwnerDiagnostics = CollectBinderDiagnosticsForOwners(ownersToBind, cancellationToken);
            foreach (var owner in outerOwners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetCachedBoundDiagnostics(owner, out var ownerDiagnostics))
                {
                    ownerDiagnostics = collectedOwnerDiagnostics.TryGetValue(owner, out var collectedDiagnostics)
                        ? collectedDiagnostics
                        : ImmutableArray<Diagnostic>.Empty;
                    StoreBoundDiagnostics(owner, ownerDiagnostics);
                }

                diagnosticsBuilder.AddRange(ownerDiagnostics);
            }

            diagnosticInstrumentation.RecordCollectTicks(Stopwatch.GetTimestamp() - phaseStart);

            diagnosticsBuilder.AddRange(rootBinder.Diagnostics.AsEnumerable()
                .Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, SyntaxTree) &&
                                     !allOwners.Any(owner => owner.Span.IntersectsWith(diagnostic.Location.SourceSpan))));
            return true;

            void ClearCachedBinderDiagnostics(Text.TextSpan span)
            {
                foreach (var binderState in _binderCache.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (ShouldPreserveBinderDiagnosticsDuringExecutableRebind(binderState))
                        continue;

                    binderState.Diagnostics.ClearDiagnostics(span);
                }
            }
        }

        ImmutableArray<Diagnostic> CollectAllBinderDiagnostics(CancellationToken cancellationToken)
        {
            var builder = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var binderState in _binderCache.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AddRange(binderState.Diagnostics.AsEnumerable());
            }

            return builder.ToImmutable();
        }

        ImmutableArray<Diagnostic> CollectBinderDiagnosticsForOwner(
            SyntaxNode owner,
            CancellationToken cancellationToken)
        {
            var diagnosticsByOwner = CollectBinderDiagnosticsForOwners([owner], cancellationToken);
            return diagnosticsByOwner.TryGetValue(owner, out var diagnostics)
                ? diagnostics
                : ImmutableArray<Diagnostic>.Empty;
        }

        Dictionary<SyntaxNode, ImmutableArray<Diagnostic>> CollectBinderDiagnosticsForOwners(
            IReadOnlyCollection<SyntaxNode> owners,
            CancellationToken cancellationToken)
        {
            var builders = new Dictionary<SyntaxNode, ImmutableArray<Diagnostic>.Builder>(ReferenceEqualityComparer.Instance);
            foreach (var owner in owners)
                builders[owner] = ImmutableArray.CreateBuilder<Diagnostic>();

            if (builders.Count == 0)
                return new Dictionary<SyntaxNode, ImmutableArray<Diagnostic>>(ReferenceEqualityComparer.Instance);

            foreach (var binderState in _binderCache.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var diagnostic in binderState.Diagnostics.AsEnumerable())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var owner in owners)
                    {
                        if (BelongsToOwner(diagnostic, owner))
                        {
                            builders[owner].Add(diagnostic);
                            break;
                        }
                    }
                }
            }

            var result = new Dictionary<SyntaxNode, ImmutableArray<Diagnostic>>(ReferenceEqualityComparer.Instance);
            foreach (var (owner, builder) in builders)
                result[owner] = builder.ToImmutable();

            return result;
        }

        void ClearCachedBoundNodes(Text.TextSpan span)
        {
            var seen = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
            var nodes = _boundNodeCache.Keys
                .Concat(_contextualBoundNodeCache.Keys.Select(static key => key.Node))
                .Where(node =>
                    seen.Add(node) &&
                    ReferenceEquals(node.SyntaxTree, SyntaxTree) &&
                    node.Span.IntersectsWith(span))
                .ToArray();

            foreach (var node in nodes)
                RemoveCachedBoundNode(node);

            ClearBoundDiagnostics(span);
        }

        Binder GetRootBinderForCompleteDiagnostics(SyntaxNode root)
        {
            EnsureRootBinderCreated();
            return GetBinder(root);
        }

        bool TryCollectTransferredDiagnosticsForAllOwners(
            SyntaxNode root,
            Binder rootBinder,
            SyntaxNode[] allOwners,
            ImmutableArray<Diagnostic>.Builder diagnosticsBuilder)
        {
            if (allOwners.Length == 0)
                return false;

            foreach (var owner in allOwners)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetTransferredSemanticDiagnostics(owner, out var diagnostics))
                    return false;

                diagnosticsBuilder.AddRange(diagnostics);
            }

            DocumentationCommentValidator.Analyze(this, root, rootBinder.Diagnostics);
            diagnosticsBuilder.AddRange(rootBinder.Diagnostics.AsEnumerable()
                .Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, SyntaxTree) &&
                                     !allOwners.Any(owner => owner.Span.IntersectsWith(diagnostic.Location.SourceSpan))));
            return true;
        }

        static SyntaxNode[] ExpandTopLevelOrderSensitiveOwners(SyntaxNode root, SyntaxNode[] ownersToBind)
        {
            var firstChangedGlobalStart = ownersToBind
                .OfType<GlobalStatementSyntax>()
                .Select(static owner => (int?)owner.Span.Start)
                .Min();

            if (firstChangedGlobalStart is not { } start)
                return ownersToBind;

            var expanded = ownersToBind.ToList();
            var seen = expanded
                .Select(static owner => new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind))
                .ToHashSet();

            foreach (var global in root.DescendantNodesAndSelf().OfType<GlobalStatementSyntax>())
            {
                if (global.Span.Start < start)
                    continue;

                if (seen.Add(new Compilation.ExecutableOwnerDescriptor(global.Span, global.Kind)))
                    expanded.Add(global);
            }

            return expanded.ToArray();
        }

        bool TryGetTransferredSemanticDiagnostics(
            SyntaxNode owner,
            out ImmutableArray<Diagnostic> diagnostics)
        {
            diagnostics = default;

            if (!Compilation.TryGetSemanticDiagnosticDescriptors(owner, out var descriptors))
                return false;

            var builder = ImmutableArray.CreateBuilder<Diagnostic>(descriptors.Length);
            foreach (var descriptor in descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var location = Location.Create(
                    owner.SyntaxTree,
                    new Text.TextSpan(owner.Span.Start + descriptor.RelativeStart, descriptor.Length));

                builder.Add(new Diagnostic(
                    descriptor.Descriptor,
                    location,
                    descriptor.MessageArgs.ToArray(),
                    descriptor.Severity,
                    descriptor.IsSuppressed,
                    descriptor.Properties));
            }

            diagnostics = builder.ToImmutable();
            return true;
        }

        void StoreSemanticDiagnosticDescriptors(
            SyntaxNode root,
            ImmutableArray<Diagnostic> diagnostics)
        {
            var byOwner = diagnostics
                .Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, SyntaxTree))
                .Select(diagnostic => (diagnostic, owner: TryFindDiagnosticOwner(root, diagnostic.Location.SourceSpan)))
                .Where(static item => item.owner is not null)
                .GroupBy(static item => item.owner!)
                .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());

            foreach (var owner in GetExecutableOwnersForDiagnostics(root))
            {
                var descriptors = byOwner.TryGetValue(owner, out var ownerDiagnostics)
                    ? ownerDiagnostics.Select(item => new Compilation.SemanticDiagnosticDescriptor(
                        item.diagnostic.Descriptor,
                        item.diagnostic.Location.SourceSpan.Start - owner.Span.Start,
                        item.diagnostic.Location.SourceSpan.Length,
                        item.diagnostic.Severity,
                        item.diagnostic.IsSuppressed,
                        item.diagnostic.Properties,
                        item.diagnostic.GetMessageArgs().Cast<object?>().ToImmutableArray()))
                    .ToImmutableArray()
                    : ImmutableArray<Compilation.SemanticDiagnosticDescriptor>.Empty;

                Compilation.StoreSemanticDiagnosticDescriptors(owner, descriptors);
            }
        }

        static SyntaxNode? TryFindDiagnosticOwner(SyntaxNode root, Text.TextSpan span)
        {
            var node = root.FindNode(span, getInnermostNodeForTie: true);
            return node?.AncestorsAndSelf().FirstOrDefault(IsExecutableOwnerForDiagnostics);
        }

        static bool BelongsToOwner(Diagnostic diagnostic, SyntaxNode owner)
            => ReferenceEquals(diagnostic.Location.SourceTree, owner.SyntaxTree) &&
               owner.Span.IntersectsWith(diagnostic.Location.SourceSpan);

        static bool ContainsOrTouchesInsertion(Text.TextSpan container, Text.TextSpan span)
        {
            if (span.Length == 0)
                return span.Start >= container.Start && span.Start <= container.End + 1;

            return span.Start >= container.Start && span.End <= container.End;
        }

        static bool ContainsSpan(Text.TextSpan container, Text.TextSpan span)
            => span.Start >= container.Start && span.End <= container.End;

        IEnumerable<SyntaxNode> GetExecutableOwnersForDiagnostics(SyntaxNode root)
        {
            if (root is CompilationUnitSyntax compilationUnit &&
                Compilation.HasRunnableFileScopeCode(compilationUnit))
            {
                yield return root;
                yield break;
            }

            foreach (var owner in root.DescendantNodesAndSelf().Where(IsExecutableOwnerForDiagnostics))
                yield return owner;
        }

        static bool IsExecutableOwnerForDiagnostics(SyntaxNode node)
            => node is FunctionExpressionSyntax
                or FunctionStatementSyntax
                or MacroDeclarationSyntax
                or BaseMethodDeclarationSyntax
                or BaseConstructorDeclarationSyntax
                or ParameterlessConstructorDeclarationSyntax
                or AccessorDeclarationSyntax
                or PropertyDeclarationSyntax
                or EventDeclarationSyntax
                or GlobalStatementSyntax { Statement: not FunctionStatementSyntax };

        void BindStatementAttributeSyntaxes(SyntaxNode statementNode, Binder parentBinder)
        {
            foreach (var attributeSyntax in statementNode.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (attributeSyntax.IsMacroAttribute())
                {
                    _ = GetMacroExpansion(attributeSyntax);
                    continue;
                }

                var attributeParent = (SyntaxNode?)attributeSyntax.Parent ?? statementNode;
                var binderForAttribute = GetBinder(attributeParent, parentBinder);
                var attributeBinder = binderForAttribute as AttributeBinder
                    ?? new AttributeBinder(binderForAttribute.ContainingSymbol, binderForAttribute);

                _ = attributeBinder.BindAttribute(attributeSyntax);
            }
        }
    }

    private void AnalyzeUnionVariantCardinalityDiagnostics(
        SyntaxNode root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool declarationsAreComplete)
    {
        foreach (var declaration in root.DescendantNodes().OfType<UnionDeclarationSyntax>())
        {
            if (!TryGetUnionSymbol(declaration, out var unionSymbol))
                continue;

            var declarations = unionSymbol.DeclaringSyntaxReferences
                .Select(static reference => reference.GetSyntax())
                .OfType<UnionDeclarationSyntax>()
                .OrderBy(candidate =>
                {
                    var treeIndex = Array.IndexOf(Compilation.SyntaxTrees, candidate.SyntaxTree);
                    return treeIndex >= 0
                        ? treeIndex
                        : Compilation.SyntaxTrees.Length + Array.IndexOf(Compilation.MacroSyntaxTrees, candidate.SyntaxTree);
                })
                .ThenBy(static candidate => candidate.Span.Start)
                .ToArray();
            var primaryDeclaration = declarations.FirstOrDefault();
            if (primaryDeclaration is null ||
                primaryDeclaration.SyntaxTree != declaration.SyntaxTree ||
                primaryDeclaration.Span != declaration.Span)
            {
                continue;
            }

            var hasMacroAttributes = declarations.Any(static candidate =>
                candidate.AttributeLists
                    .SelectMany(static list => list.Attributes)
                    .Any(static attribute => attribute.IsMacroAttribute()));
            if (hasMacroAttributes && !declarationsAreComplete)
                continue;

            var isParenthesized = primaryDeclaration.MemberTypes is not null;
            var variantCount = hasMacroAttributes
                ? unionSymbol.Variants.Length
                : isParenthesized
                    ? declarations.Sum(static candidate => candidate.MemberTypes?.Types.Count ?? 0)
                    : declarations.Sum(static candidate => candidate.Members.OfType<CaseDeclarationSyntax>().Count());

            if (isParenthesized && variantCount < 2)
            {
                diagnostics.Add(Diagnostic.Create(
                    CompilerDiagnostics.ParenthesizedUnionRequiresTwoVariants,
                    primaryDeclaration.MemberTypes!.GetLocation(),
                    unionSymbol.Name));
            }
            else if (!isParenthesized && variantCount < 1)
            {
                diagnostics.Add(Diagnostic.Create(
                    CompilerDiagnostics.UnionRequiresCase,
                    primaryDeclaration.Identifier.GetLocation(),
                    unionSymbol.Name));
            }
        }
    }

    private void StoreBoundDiagnostics(SyntaxNode node, ImmutableArray<Diagnostic> diagnostics)
    {
        _boundNodeDiagnostics[node] = diagnostics;
    }

    internal bool TryGetCachedBoundDiagnostics(SyntaxNode node, out ImmutableArray<Diagnostic> diagnostics)
        => _boundNodeDiagnostics.TryGetValue(node, out diagnostics);

    private void ClearBoundDiagnostics(Text.TextSpan span)
    {
        var nodes = _boundNodeDiagnostics.Keys
            .Where(node =>
                ReferenceEquals(node.SyntaxTree, SyntaxTree) &&
                node.Span.IntersectsWith(span))
            .ToArray();

        foreach (var node in nodes)
            _boundNodeDiagnostics.TryRemove(node, out _);
    }

    private void RemoveBoundDiagnostics(SyntaxNode node)
    {
        _boundNodeDiagnostics.TryRemove(node, out _);
    }

    private static bool ShouldPreserveBinderDiagnosticsDuringExecutableRebind(Binder binderState)
        => binderState is TypeMemberBinder
            or TypeDeclarationBinder
            or FunctionBinder
            or MacroBinder
            or ImportBinder
            or CompilationUnitBinder
            or NamespaceBinder;

    private static SyntaxNode? TryGetMacroTarget(AttributeSyntax attributeSyntax)
        => attributeSyntax.Parent?.Parent switch
        {
            AttributeListSyntax { Parent: SyntaxNode parent } => parent,
            SyntaxNode parent => parent,
            _ => null
        };

    internal IMacroDeclarationSymbol ConstructLocalMacroSymbol(
        TypeSyntax name,
        IMacroDeclarationSymbol macroSymbol)
    {
        var typeArguments = ResolveMacroTypeArguments(name);
        if (typeArguments.IsDefaultOrEmpty)
            return macroSymbol;

        return typeArguments.Length != macroSymbol.Arity
            ? macroSymbol
            : macroSymbol.Construct(typeArguments.ToArray());
    }

    internal ImmutableArray<ITypeSymbol> ResolveMacroTypeArguments(TypeSyntax name)
        => name.TryGetMacroTypeArgumentList(out var typeArgumentList)
            ? ResolveAvailableTypeArguments(typeArgumentList)
            : [];

    /// <summary>
    /// Gets symbol information about a syntax node
    /// </summary>
    /// <param name="node">The syntax node</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The symbol info</returns>
    public SymbolInfo GetSymbolInfo(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        ValidateSyntaxNode(node, nameof(node));

        using var semanticAccess = EnterSemanticAccess(cancellationToken);
        using var semanticQueryBinding = EnterSemanticQueryBinding();
        Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoQuery();

        var macroExpression = node as FreestandingMacroExpressionSyntax
            ?? node.Ancestors()
                .OfType<FreestandingMacroExpressionSyntax>()
                .FirstOrDefault(expression =>
                    ReferenceEquals(expression.Name, node) ||
                    expression.Name.DescendantNodes().Any(descendant => ReferenceEquals(descendant, node)));
        if (macroExpression is not null &&
            macroExpression.TryGetMacroName(out var macroName))
        {
            IMacroSymbol? macroSymbol = null;
            if (Compilation.TryResolveLocalMacroDeclarationSymbol(
                    macroExpression,
                    macroName,
                    macroExpression.Name.GetMacroArity(),
                    out var localMacroSymbol,
                    out _))
            {
                macroSymbol = ConstructLocalMacroSymbol(macroExpression.Name, localMacroSymbol);
            }
            else if (Compilation.GetMacroRegistry().TryResolveMacroSymbol(
                    Compilation,
                    macroExpression,
                    macroName,
                    out var loadedMacroSymbol,
                    out _))
            {
                macroSymbol = loadedMacroSymbol;
            }

            if (macroSymbol is not null)
            {
                var macroInfo = new SymbolInfo(macroSymbol);
                StoreSymbolMapping(node, macroInfo);
                StoreNodeInterestSymbolDescriptor(node, macroSymbol);
                return macroInfo;
            }
        }

        if (node is AttributeSyntax macroAttribute &&
            macroAttribute.TryGetMacroName(out var attachedMacroName))
        {
            IMacroSymbol? attachedMacroSymbol = null;
            if (Compilation.TryResolveLocalMacroDeclarationSymbol(
                    macroAttribute,
                    attachedMacroName,
                    macroAttribute.Name.GetMacroArity(),
                    out var localAttachedMacroSymbol,
                    out _))
            {
                attachedMacroSymbol = ConstructLocalMacroSymbol(
                    macroAttribute.Name,
                    localAttachedMacroSymbol);
            }
            else if (Compilation.GetMacroRegistry().TryResolveMacroSymbol(
                    Compilation,
                    macroAttribute,
                    attachedMacroName,
                    out var loadedAttachedMacroSymbol,
                    out _))
            {
                attachedMacroSymbol = loadedAttachedMacroSymbol;
            }

            if (attachedMacroSymbol?.MacroKind == MacroKind.AttachedDeclaration)
            {
                var macroInfo = new SymbolInfo(attachedMacroSymbol);
                StoreSymbolMapping(node, macroInfo);
                StoreNodeInterestSymbolDescriptor(node, attachedMacroSymbol);
                return macroInfo;
            }
        }

        EnsureContainingFreestandingMacroReplacementSyntax(node);
        if (TryGetMacroReplacementSyntax(node, out var macroReplacement) &&
            !ReferenceEquals(macroReplacement, node))
        {
            var replacementInfo = GetSymbolInfo(macroReplacement, cancellationToken);
            StoreSymbolMapping(node, replacementInfo);
            return replacementInfo;
        }

        if (node is IdentifierNameSyntax macroParameterReference &&
            TryGetMacroParameterReference(macroParameterReference, out var macroParameter))
        {
            var macroParameterInfo = new SymbolInfo(macroParameter);
            StoreSymbolMapping(node, macroParameterInfo);
            StoreNodeInterestSymbolDescriptor(node, macroParameter);
            return macroParameterInfo;
        }

        if (TryGetPipeInvocationSymbolInfo(node, out var pipeInvocationInfo))
        {
            pipeInvocationInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, pipeInvocationInfo);
            StoreSymbolMapping(node, pipeInvocationInfo);
            return pipeInvocationInfo;
        }

        if (node is AttributeSyntax attributeSyntax &&
            BindAttribute(attributeSyntax)?.AttributeConstructor is { } attributeConstructor)
        {
            var attributeInfo = new SymbolInfo(attributeConstructor);
            StoreSymbolMapping(node, attributeInfo);
            StoreNodeInterestSymbolDescriptor(node, attributeConstructor);
            return attributeInfo;
        }

        if (node is IdentifierNameSyntax casePatternIdentifier &&
            TryGetCasePatternHeadSymbol(casePatternIdentifier, out var casePatternIdentifierSymbol))
        {
            var casePatternInfo = new SymbolInfo(casePatternIdentifierSymbol);
            StoreSymbolMapping(node, casePatternInfo);
            StoreNodeInterestSymbolDescriptor(node, casePatternIdentifierSymbol);
            return casePatternInfo;
        }

        if (node is IdentifierNameSyntax propertyPatternIdentifier &&
            TryGetPropertySubpatternMemberSymbol(propertyPatternIdentifier, out var propertyPatternMember))
        {
            var propertyPatternInfo = new SymbolInfo(propertyPatternMember);
            StoreSymbolMapping(node, propertyPatternInfo);
            StoreNodeInterestSymbolDescriptor(node, propertyPatternMember);
            return propertyPatternInfo;
        }

        if (node is GenericNameSyntax casePatternGenericName &&
            TryGetCasePatternHeadSymbol(casePatternGenericName, out var casePatternGenericSymbol))
        {
            var casePatternInfo = new SymbolInfo(casePatternGenericSymbol);
            StoreSymbolMapping(node, casePatternInfo);
            StoreNodeInterestSymbolDescriptor(node, casePatternGenericSymbol);
            return casePatternInfo;
        }

        if (node is TypeSyntax patternTypeSyntax &&
            TryGetProjectedPatternTypeSymbol(patternTypeSyntax, out var projectedPatternType))
        {
            var patternTypeInfo = new SymbolInfo(projectedPatternType);
            StoreSymbolMapping(node, patternTypeInfo);
            StoreNodeInterestSymbolDescriptor(node, projectedPatternType);
            return patternTypeInfo;
        }

        if (node is TypeSyntax typeSyntax &&
            IsExplicitTypeSyntaxContext(typeSyntax))
        {
            if (typeSyntax is IdentifierNameSyntax typeIdentifier &&
                TryLookupAvailableAlias(typeIdentifier, out var aliasSymbol) &&
                aliasSymbol is not null)
            {
                var aliasInfo = new SymbolInfo(aliasSymbol);
                StoreSymbolMapping(node, aliasInfo);
                StoreNodeInterestSymbolDescriptor(node, aliasSymbol);
                return aliasInfo;
            }

            var type = GetTypeInfo(typeSyntax).Type;
            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                var typeInfo = new SymbolInfo(type);
                StoreSymbolMapping(node, typeInfo);
                StoreNodeInterestSymbolDescriptor(node, type);
                return typeInfo;
            }
        }

        if (node is IdentifierNameSyntax functionParameterReference &&
            TryGetAvailableFunctionExpressionParameterReferenceSymbolInfo(functionParameterReference, out var functionParameterInfo))
        {
            return functionParameterInfo;
        }

        if (node is IdentifierNameSyntax contextualFunctionParameterReference &&
            TryGetFunctionExpressionParameterReferenceSymbolInfo(contextualFunctionParameterReference, out var contextualFunctionParameterInfo))
        {
            return contextualFunctionParameterInfo;
        }

        if (TryGetInvocationTargetSymbolInfo(node, out var invocationTargetInfo, cancellationToken))
        {
            invocationTargetInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, invocationTargetInfo);
            StoreSymbolMapping(node, invocationTargetInfo);
            return invocationTargetInfo;
        }

        if (node is IdentifierNameSyntax invokedMemberName &&
            invokedMemberName.Parent is MemberAccessExpressionSyntax invokedMemberAccess &&
            IsSameSyntaxNode(invokedMemberAccess.Name, invokedMemberName) &&
            invokedMemberAccess.Parent is InvocationExpressionSyntax invokedMemberInvocation &&
            IsSameSyntaxNode(invokedMemberInvocation.Expression, invokedMemberAccess))
        {
            if (ShouldUseDirectTypeMemberFastPath(invokedMemberAccess.Expression) &&
                TryResolveAvailableTypeExpression(invokedMemberAccess.Expression, out var invokedReceiverType) &&
                TryCreateAvailableInvocationSymbolInfo(
                    invokedReceiverType.GetMembers(invokedMemberName.Identifier.ValueText).OfType<IMethodSymbol>(),
                    invokedMemberInvocation,
                    out var directTypeMemberInfo))
            {
                directTypeMemberInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, directTypeMemberInfo);
                StoreSymbolMapping(node, directTypeMemberInfo);
                StoreSymbolMapping(invokedMemberAccess, directTypeMemberInfo);
                StoreSymbolMapping(invokedMemberInvocation, directTypeMemberInfo);
                return directTypeMemberInfo;
            }

            if (TryGetAvailableInvocationSymbolInfo(invokedMemberInvocation, out var availableInvocationInfo))
            {
                availableInvocationInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, availableInvocationInfo);
                if (HasSymbolInfo(availableInvocationInfo))
                {
                    StoreSymbolMapping(node, availableInvocationInfo);
                    return availableInvocationInfo;
                }
            }

            var invocationInfo = GetSymbolInfo(invokedMemberInvocation, cancellationToken);
            if (invocationInfo.Symbol is not null || !invocationInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                if (invocationInfo.Symbol is null &&
                    TryChoosePreferredCandidate(invocationInfo.CandidateSymbols, invokedMemberName.Identifier.ValueText, out var preferredCandidate))
                {
                    invocationInfo = new SymbolInfo(preferredCandidate);
                }

                invocationInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, invocationInfo);
                StoreSymbolMapping(node, invocationInfo);
                return invocationInfo;
            }
        }

        if (node is IdentifierNameSyntax pipelineIdentifier &&
            pipelineIdentifier.Parent is InvocationExpressionSyntax pipelineInvocation &&
            IsSameSyntaxNode(pipelineInvocation.Expression, pipelineIdentifier))
        {
            if (TryLookupPipelineInvocationSymbol(pipelineInvocation, pipelineIdentifier, pipelineIdentifier.Identifier.ValueText, out var directPipelineInfo))
            {
                directPipelineInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, directPipelineInfo);
                StoreSymbolMapping(node, directPipelineInfo);
                return directPipelineInfo;
            }
        }

        if (node is IdentifierNameSyntax earlyWithAssignmentIdentifier &&
            earlyWithAssignmentIdentifier.Parent is WithAssignmentSyntax earlyWithAssignment &&
            IsSameSyntaxNode(earlyWithAssignment.Name, earlyWithAssignmentIdentifier) &&
            TryGetWithAssignmentMemberSymbolInfo(earlyWithAssignment, out var earlyWithAssignmentInfo))
        {
            StoreSymbolMapping(node, earlyWithAssignmentInfo);
            return earlyWithAssignmentInfo;
        }

        if (node is IdentifierNameSyntax earlyArgumentName &&
            earlyArgumentName.Parent is NameColonSyntax earlyNameColon &&
            earlyNameColon.Parent is ArgumentSyntax earlyArgument &&
            IsSameSyntaxNode(earlyNameColon.Name, earlyArgumentName) &&
            TryGetArgumentParameterSymbolInfo(earlyArgument, out var earlyArgumentInfo))
        {
            StoreSymbolMapping(node, earlyArgumentInfo);
            return earlyArgumentInfo;
        }

        if (node is MemberBindingExpressionSyntax earlyMemberBinding &&
            TryGetMemberBindingTargetMemberSymbolInfo(earlyMemberBinding, out var earlyMemberBindingInfo))
        {
            StoreSymbolMapping(node, earlyMemberBindingInfo);
            return earlyMemberBindingInfo;
        }

        if (node is IdentifierNameSyntax contextualArgumentIdentifier &&
            TryGetContextualArgumentSymbolInfo(contextualArgumentIdentifier, out var contextualArgumentInfo))
        {
            StoreSymbolMapping(node, contextualArgumentInfo);
            return contextualArgumentInfo;
        }

        if (node is IdentifierNameSyntax earlyValueIdentifier &&
            TryGetVisibleValueIdentifierSymbolInfo(earlyValueIdentifier, out var earlyValueInfo))
        {
            return earlyValueInfo;
        }

        if (TryGetCachedSymbolInfo(node, out var symbolInfo))
        {
            if (symbolInfo.Symbol is not null || !symbolInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                if (node is ExpressionSyntax cachedExpression &&
                    TryBindTargetTypedMethodGroup(cachedExpression, symbolInfo, out var targetTypedCachedInfo))
                {
                    StoreSymbolMapping(node, targetTypedCachedInfo);
                    return targetTypedCachedInfo;
                }

                symbolInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, symbolInfo);
                if (symbolInfo.Symbol is { } cachedSymbol)
                    StoreNodeInterestSymbolDescriptor(node, cachedSymbol);

                StoreSymbolMapping(node, symbolInfo);
                Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoCacheHit();
                return symbolInfo;
            }

            RemoveCachedSymbolMapping(node);
        }

        if (TryGetCachedNodeInterestSymbolInfo(node, out var cachedNodeInterestInfo))
        {
            cachedNodeInterestInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, cachedNodeInterestInfo);
            StoreSymbolMapping(node, cachedNodeInterestInfo);
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoCacheHit();
            return cachedNodeInterestInfo;
        }

        if (TryGetPatternConstantValueSymbolInfo(node, out var patternConstantValueInfo))
        {
            StoreSymbolMapping(node, patternConstantValueInfo);
            return patternConstantValueInfo;
        }

        if (node is ExpressionSyntax cachedExpressionNode &&
            TryGetCachedBoundNode(cachedExpressionNode) is BoundExpression cachedBoundExpression &&
            !IsLikelyStaleFunctionBodyNode(cachedBoundExpression))
        {
            var cachedBoundInfo = cachedBoundExpression.GetSymbolInfo();
            if (cachedBoundInfo.Symbol is not null || !cachedBoundInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                if (TryBindTargetTypedMethodGroup(cachedExpressionNode, cachedBoundInfo, out var targetTypedCachedBoundInfo))
                {
                    StoreSymbolMapping(node, targetTypedCachedBoundInfo);
                    return targetTypedCachedBoundInfo;
                }

                cachedBoundInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, cachedBoundInfo);
                StoreSymbolMapping(node, cachedBoundInfo);
                return cachedBoundInfo;
            }
        }

        if (TryGetAvailableInvocationSymbolInfo(node, out var availableInvocationSymbolInfo))
        {
            availableInvocationSymbolInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, availableInvocationSymbolInfo);
            if (HasSymbolInfo(availableInvocationSymbolInfo))
            {
                StoreSymbolMapping(node, availableInvocationSymbolInfo);
                return availableInvocationSymbolInfo;
            }
        }

        if (TryGetAvailableSymbolInfo(node, out var availableSymbolInfo) &&
            (availableSymbolInfo.Symbol is not null || !availableSymbolInfo.CandidateSymbols.IsDefaultOrEmpty))
        {
            if (node is ExpressionSyntax availableExpression &&
                TryBindTargetTypedMethodGroup(availableExpression, availableSymbolInfo, out var targetTypedAvailableInfo))
            {
                StoreSymbolMapping(node, targetTypedAvailableInfo);
                return targetTypedAvailableInfo;
            }

            if (node is ExpressionSyntax unresolvedAvailableExpression &&
                IsTargetTypedMethodGroup(unresolvedAvailableExpression, availableSymbolInfo))
            {
                goto BindAfterDeclarations;
            }

            if (availableSymbolInfo.Symbol is { } availableSymbol)
                StoreNodeInterestSymbolDescriptor(node, availableSymbol);

            StoreSymbolMapping(node, availableSymbolInfo);
            return availableSymbolInfo;
        }

        if (node is IdentifierNameSyntax earlyMemberIdentifier &&
            earlyMemberIdentifier.Parent is MemberAccessExpressionSyntax earlyMemberAccess &&
            IsSameSyntaxNode(earlyMemberAccess.Name, earlyMemberIdentifier) &&
            TryResolveMemberAccessFromVisibleReceiver(earlyMemberAccess, earlyMemberIdentifier, out var earlyMemberInfo))
        {
            earlyMemberInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, earlyMemberInfo);
            if (earlyMemberInfo.Symbol is { } earlyMemberSymbol)
                StoreNodeInterestSymbolDescriptor(node, earlyMemberSymbol);

            StoreSymbolMapping(node, earlyMemberInfo);
            return earlyMemberInfo;
        }

        if (TryGetNodeInterestSymbolInfo(node, out var nodeInterestInfo))
        {
            nodeInterestInfo = ProjectBackingFieldSymbolsToAssociatedProperty(node, nodeInterestInfo);
            StoreSymbolMapping(node, nodeInterestInfo);
            return nodeInterestInfo;
        }

    BindAfterDeclarations:
        EnsureDeclarations();
        EnsureMemberSignaturesDeclared();
        if (SyntaxTree.GetRoot() is CompilationUnitSyntax compilationUnit)
            EnsureTopLevelFunctionDeclarations(compilationUnit);

        if (TryGetAvailableSymbolInfo(node, out var postDeclarationAvailableSymbolInfo) &&
            (postDeclarationAvailableSymbolInfo.Symbol is not null ||
             !postDeclarationAvailableSymbolInfo.CandidateSymbols.IsDefaultOrEmpty))
        {
            if (node is ExpressionSyntax postDeclarationExpression &&
                TryBindTargetTypedMethodGroup(
                    postDeclarationExpression,
                    postDeclarationAvailableSymbolInfo,
                    out var targetTypedPostDeclarationInfo))
            {
                StoreSymbolMapping(node, targetTypedPostDeclarationInfo);
                return targetTypedPostDeclarationInfo;
            }

            if (node is not ExpressionSyntax unresolvedPostDeclarationExpression ||
                !IsTargetTypedMethodGroup(unresolvedPostDeclarationExpression, postDeclarationAvailableSymbolInfo))
            {
                if (postDeclarationAvailableSymbolInfo.Symbol is { } postDeclarationAvailableSymbol)
                    StoreNodeInterestSymbolDescriptor(node, postDeclarationAvailableSymbol);

                StoreSymbolMapping(node, postDeclarationAvailableSymbolInfo);
                return postDeclarationAvailableSymbolInfo;
            }

        }

        SymbolInfo info;

        if (node is SimpleNameSyntax invokedName &&
            invokedName.Parent is InvocationExpressionSyntax invokedCall &&
            IsSameSyntaxNode(invokedCall.Expression, invokedName))
        {
            if (TryBindInterestRegion(invokedCall, out var regionBoundInvocation))
            {
                if (TryGetInvokedExpressionSymbolInfo(regionBoundInvocation, invokedName, out var invokedExpressionInfo))
                {
                    info = invokedExpressionInfo;
                }
                else if (regionBoundInvocation.GetSymbolInfo() is { } regionInfo &&
                    (regionInfo.Symbol is not null || !regionInfo.CandidateSymbols.IsDefaultOrEmpty))
                {
                    info = regionInfo;
                }
                else
                {
                    var boundInvocation = GetBoundNode(invokedCall);
                    info = TryGetInvokedExpressionSymbolInfo(boundInvocation, invokedName, out var boundInvokedExpressionInfo)
                        ? boundInvokedExpressionInfo
                        : boundInvocation.GetSymbolInfo();
                }
            }
            else
            {
                var boundInvocation = GetBoundNode(invokedCall);
                var boundInvocationInfo = TryGetInvokedExpressionSymbolInfo(boundInvocation, invokedName, out var boundInvokedExpressionInfo)
                    ? boundInvokedExpressionInfo
                    : boundInvocation.GetSymbolInfo();

                if (boundInvocationInfo.Symbol is not null || !boundInvocationInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    info = boundInvocationInfo;
                }
                else if (TryRebindInvocationAfterRefreshingFunctionArguments(invokedCall, out var reboundInvocationInfo))
                {
                    info = reboundInvocationInfo;
                }
                else if (TryLookupPipelineInvocationSymbol(invokedCall, invokedName, invokedName.Identifier.ValueText, out var pipelineInvocationInfo))
                {
                    info = pipelineInvocationInfo;
                }
                else if (TryBindExactSymbol(node, out var exactNameInfo))
                {
                    info = exactNameInfo;
                }
                else
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
                    var binder = GetBinder(node);
                    info = binder.BindSymbol(node);
                }
            }
        }
        else if (node is IdentifierNameSyntax identifier &&
            identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
            IsSameSyntaxNode(memberAccess.Name, identifier))
        {
            if (TryResolveMemberAccessFromVisibleReceiver(memberAccess, identifier, out var fastMemberInfo))
            {
                info = fastMemberInfo;
                goto Complete;
            }

            if (TryBindExactSymbol(node, out var exactInfo))
            {
                info = exactInfo;
            }
            else if (TryBindExactSymbol(memberAccess, out var memberAccessInfo))
            {
                info = memberAccessInfo;
            }
            else if (memberAccess.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) &&
                     TryBindExactSymbol(invocation, out var invocationInfo))
            {
                info = invocationInfo;
            }
            else
            {
                if (memberAccess.Parent is InvocationExpressionSyntax invocationExpression &&
                    GetSymbolInfo(invocationExpression, cancellationToken) is var invocationSymbolInfo &&
                    (invocationSymbolInfo.Symbol is not null || !invocationSymbolInfo.CandidateSymbols.IsDefaultOrEmpty))
                {
                    info = invocationSymbolInfo;
                    goto Complete;
                }

                info = TryGetBoundNodeForSemanticQuery(memberAccess, out var boundMemberAccessNode) &&
                       boundMemberAccessNode is BoundExpression boundMemberAccess
                    ? boundMemberAccess.GetSymbolInfo()
                    : SymbolInfo.None;

                if (info.Symbol is null &&
                    info.CandidateSymbols.IsDefaultOrEmpty &&
                    memberAccess.Parent is InvocationExpressionSyntax operationInvocation &&
                    GetOperation(operationInvocation, cancellationToken) is IInvocationOperation invocationOperation &&
                    invocationOperation.TargetMethod is { } targetMethod)
                {
                    info = new SymbolInfo(targetMethod);
                }

                if (info.Symbol is null &&
                    info.CandidateSymbols.IsDefaultOrEmpty &&
                    memberAccess.Parent is InvocationExpressionSyntax refreshedInvocationSyntax &&
                    IsSameSyntaxNode(refreshedInvocationSyntax.Expression, memberAccess))
                {
                    ClearCachedSemanticState(memberAccess);
                    ClearCachedSemanticState(refreshedInvocationSyntax);

                    if (TryGetBoundNodeForSemanticQuery(refreshedInvocationSyntax, out var refreshedInvocationNode) &&
                        refreshedInvocationNode is BoundInvocationExpression refreshedInvocation)
                        info = new SymbolInfo(refreshedInvocation.Method);
                }

            }
        }
        else if (node is IdentifierNameSyntax receiverIdentifier &&
                 receiverIdentifier.Parent is MemberAccessExpressionSyntax receiverMemberAccess &&
                 IsSameSyntaxNode(receiverMemberAccess.Expression, receiverIdentifier))
        {
            if (TryBindExactSymbol(node, out var exactInfo))
            {
                info = exactInfo;
            }
            else
            {
                var semanticQueryBoundMemberAccess = TryGetBoundNodeForSemanticQuery(receiverMemberAccess, out var receiverMemberAccessNode) &&
                                                     receiverMemberAccessNode is BoundExpression receiverBoundExpression
                    ? receiverBoundExpression
                    : null;
                var receiverInfo = semanticQueryBoundMemberAccess switch
                {
                    BoundMemberAccessExpression memberAccessExpression => memberAccessExpression.Receiver.GetSymbolInfo(),
                    BoundExpression boundExpression => boundExpression.GetSymbolInfo(),
                    _ => SymbolInfo.None
                };

                if (receiverInfo.Symbol is not null || !receiverInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    info = receiverInfo;
                }
                else if (semanticQueryBoundMemberAccess is not null &&
                         TryFindBoundNodeBySyntax(semanticQueryBoundMemberAccess, receiverIdentifier, out var boundReceiverNode))
                {
                    var resolvedFromChild = boundReceiverNode switch
                    {
                        BoundExpression boundExpression => boundExpression.GetSymbolInfo(),
                        BoundStatement boundStatement => boundStatement.GetSymbolInfo(),
                        _ => default
                    };
                    if (resolvedFromChild.Symbol is not null || !resolvedFromChild.CandidateSymbols.IsDefaultOrEmpty)
                    {
                        info = resolvedFromChild;
                    }
                    else
                    {
                        Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
                        var binder = GetBinderForIncrementalSemanticQuery(node);
                        info = binder.BindSymbol(node);
                    }
                }
                else
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
                    var binder = GetBinderForIncrementalSemanticQuery(node);
                    info = binder.BindSymbol(node);
                }
            }
        }
        else if (node is IdentifierNameSyntax memberBindingIdentifier &&
                 memberBindingIdentifier.Parent is MemberBindingExpressionSyntax memberBinding &&
                 IsSameSyntaxNode(memberBinding.Name, memberBindingIdentifier))
        {
            if (TryGetMemberBindingTargetMemberSymbolInfo(memberBinding, out var targetMemberInfo))
            {
                info = targetMemberInfo;
            }
            else if (TryBindExactSymbol(node, out var exactInfo))
            {
                info = exactInfo;
            }
            else if (TryBindExactSymbol(memberBinding, out var memberBindingInfo))
            {
                info = memberBindingInfo;
            }
            else
            {
                info = TryGetBoundNodeForSemanticQuery(memberBinding, out var boundMemberBindingNode) &&
                       boundMemberBindingNode is BoundExpression boundMemberBinding
                    ? boundMemberBinding.GetSymbolInfo()
                    : SymbolInfo.None;
            }
        }
        else if (node is IdentifierNameSyntax withAssignmentIdentifier &&
                 withAssignmentIdentifier.Parent is WithAssignmentSyntax withAssignment &&
                 IsSameSyntaxNode(withAssignment.Name, withAssignmentIdentifier) &&
                 TryGetWithAssignmentMemberSymbolInfo(withAssignment, out var withAssignmentInfo))
        {
            info = withAssignmentInfo;
        }
        else if (node is IdentifierNameSyntax argumentNameIdentifier &&
                 argumentNameIdentifier.Parent is NameColonSyntax nameColon &&
                 nameColon.Parent is ArgumentSyntax argument &&
                 IsSameSyntaxNode(nameColon.Name, argumentNameIdentifier) &&
                 TryGetArgumentParameterSymbolInfo(argument, out var argumentInfo))
        {
            info = argumentInfo;
        }
        else if (node is ExpressionSyntax expression)
        {
            if (IsExpressionWithoutDirectSymbol(expression))
            {
                info = SymbolInfo.None;
                goto Complete;
            }

            if (TryGetCachedBoundNode(expression) is BoundExpression cachedExpression &&
                !IsLikelyStaleFunctionBodyNode(cachedExpression))
            {
                var cachedInfo = cachedExpression.GetSymbolInfo();
                if (cachedInfo.Symbol is not null || !cachedInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    info = cachedInfo;
                    goto Complete;
                }
            }

            if (expression is not InvocationExpressionSyntax &&
                expression is not InfixOperatorExpressionSyntax and not PrefixOperatorExpressionSyntax &&
                TryBindExactSymbol(expression, out var exactInfo))
            {
                info = exactInfo;
                goto Complete;
            }

            if (expression is InvocationExpressionSyntax earlyInvocationExpression &&
                TryResolveInvocationOperatorFromReceiver(earlyInvocationExpression, out var earlyInvocationOperatorInfo))
            {
                info = earlyInvocationOperatorInfo;
                goto Complete;
            }

            if (TryBindInterestRegion(expression, out var regionBoundExpression))
            {
                var regionInfo = regionBoundExpression.GetSymbolInfo();
                if (regionInfo.Symbol is not null || !regionInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    info = TryBindTargetTypedMethodGroup(expression, regionInfo, out var targetTypedRegionInfo)
                        ? targetTypedRegionInfo
                        : regionInfo;
                    goto Complete;
                }
            }

            var boundExpression = TryGetBoundNodeForSemanticQuery(expression, out var boundExpressionNode) &&
                                  boundExpressionNode is BoundExpression semanticQueryBoundExpression
                ? semanticQueryBoundExpression
                : null;
            var boundInfo = boundExpression?.GetSymbolInfo() ?? SymbolInfo.None;
            if (boundInfo.Symbol is not null || !boundInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                info = TryBindTargetTypedMethodGroup(expression, boundInfo, out var targetTypedBoundInfo)
                    ? targetTypedBoundInfo
                    : boundInfo;
                goto Complete;
            }

            if (expression is InvocationExpressionSyntax &&
                GetBoundNode(expression) is BoundExpression fullyBoundInvocation)
            {
                var fullyBoundInfo = fullyBoundInvocation.GetSymbolInfo();
                if (fullyBoundInfo.Symbol is not null || !fullyBoundInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
                    info = fullyBoundInfo;
                    goto Complete;
                }
            }

            if (TryRebindExpressionFromEnclosingFunctionContext(expression, out var functionContextInfo))
            {
                info = functionContextInfo;
                goto Complete;
            }

            if (TryResolveInterestLocalSymbol(expression) is { } interestLocalSymbol)
            {
                info = new SymbolInfo(interestLocalSymbol);
                goto Complete;
            }

            var binder = GetBinderForIncrementalSemanticQuery(expression);
            var binderInfo = binder.BindSymbol(expression);
            if (binderInfo.Symbol is not null || !binderInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
                info = binderInfo;
                goto Complete;
            }

            Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoOperationFallback();
            var operation = GetOperation(expression, cancellationToken);
            var operationSymbol = operation switch
            {
                IFieldReferenceOperation fieldReference => fieldReference.Field,
                IPropertyReferenceOperation propertyReference => propertyReference.Property,
                IMethodReferenceOperation methodReference => methodReference.Method,
                IMemberReferenceOperation memberReference => memberReference.Symbol,
                IInvocationOperation invocation => invocation.TargetMethod,
                IParameterReferenceOperation parameterReference => parameterReference.Parameter,
                ILocalReferenceOperation localReference => localReference.Local,
                IVariableReferenceOperation variableReference => variableReference.Variable,
                _ => null
            };

            if (operationSymbol is not null)
            {
                info = new SymbolInfo(operationSymbol);
            }
            else
            {
                info = boundExpression?.GetSymbolInfo() ?? SymbolInfo.None;

            }
        }
        else if (node is StatementSyntax statement)
        {
            info = TryGetBoundNodeForSemanticQuery(statement, out var boundStatementNode) &&
                   boundStatementNode is BoundStatement boundStatement
                ? boundStatement.GetSymbolInfo()
                : SymbolInfo.None;
        }
        else
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBinderFallback();
            var binder = GetBinderForIncrementalSemanticQuery(node);
            info = binder.BindSymbol(node);
        }

        if (info.Symbol is null &&
            info.CandidateSymbols.IsDefaultOrEmpty &&
            node is SimpleNameSyntax pipelineName &&
            pipelineName.Parent is InvocationExpressionSyntax fallbackPipelineInvocation &&
            IsSameSyntaxNode(fallbackPipelineInvocation.Expression, pipelineName))
        {
            if (TryLookupPipelineInvocationSymbol(fallbackPipelineInvocation, pipelineName, pipelineName.Identifier.ValueText, out var pipelineInfo))
            {
                info = pipelineInfo;
            }
        }

    Complete:
        if (node is ExpressionSyntax completedExpression &&
            TryBindTargetTypedMethodGroup(completedExpression, info, out var completedTargetTypedInfo))
        {
            info = completedTargetTypedInfo;
        }

        info = ProjectBackingFieldSymbolsToAssociatedProperty(node, info);
        if (info.Symbol is { } resolvedSymbol)
            StoreNodeInterestSymbolDescriptor(node, resolvedSymbol);

        StoreSymbolMapping(node, info);
        return info;
    }

    private bool TryBindTargetTypedMethodGroup(
        ExpressionSyntax expression,
        SymbolInfo currentInfo,
        out SymbolInfo info)
    {
        info = default;
        if (!IsTargetTypedMethodGroup(expression, currentInfo) ||
            !TryGetTargetTypeForExpression(expression, out var targetType) ||
            targetType is null)
        {
            return false;
        }

        var binder = GetBinderForIncrementalSemanticQuery(expression);
        if (!TryGetNearestBlockBinder(binder, out var blockBinder))
            return false;

        var bound = blockBinder.BindExpressionWithTargetTypeForSemanticQuery(expression, targetType);
        var resolvedInfo = bound.GetSymbolInfo();
        if (resolvedInfo.Symbol is null)
            return false;

        info = resolvedInfo;
        return true;
    }

    private bool IsTargetTypedMethodGroup(ExpressionSyntax expression, SymbolInfo info)
        => info.Symbol is null &&
           !info.CandidateSymbols.IsDefaultOrEmpty &&
           info.CandidateSymbols.Any(static candidate => candidate is IMethodSymbol) &&
           TryGetTargetTypeForExpression(expression, out var targetType) &&
           targetType is { TypeKind: TypeKind.Delegate };

    private bool TryGetMacroParameterReference(
        IdentifierNameSyntax identifier,
        out IParameterSymbol parameter)
    {
        parameter = null!;
        var declaration = identifier.Ancestors()
            .OfType<MacroDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null)
            return false;

        Compilation.EnsureSourceDeclarationsDeclared();
        if (!TryGetMacroSymbol(declaration, out var macro))
            return false;

        parameter = macro.Parameters.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, identifier.Identifier.ValueText, StringComparison.Ordinal))!;
        return parameter is not null;
    }

    private static bool IsExpressionWithoutDirectSymbol(ExpressionSyntax expression)
        => expression is PrefixOperatorExpressionSyntax { Kind: SyntaxKind.AwaitExpression };

    private static bool TryChoosePreferredCandidate(
        ImmutableArray<ISymbol> candidates,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISymbol? symbol)
    {
        symbol = null;

        if (candidates.IsDefaultOrEmpty)
            return false;

        symbol = candidates.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                 ?? candidates[0];
        return symbol is not null;
    }

    internal bool TryGetInvocationTargetSymbolInfo(
        SyntaxNode node,
        out SymbolInfo info,
        CancellationToken cancellationToken = default)
    {
        var invocation = TryGetInvocationForSymbolInfoNode(node);
        if (invocation is null)
        {
            info = SymbolInfo.None;
            return false;
        }

        if (!TryGetInvocationTargetSymbolInfo(invocation, out info, cancellationToken))
            return false;

        if (TryGetInvokedName(node, invocation) is { } invokedName &&
            info.Symbol is IMethodSymbol { Name: "Invoke" } &&
            TryGetInvokedExpressionSymbolInfo(GetBoundNode(invocation), invokedName, out var invokedExpressionInfo))
        {
            info = invokedExpressionInfo;
        }

        return true;
    }

    internal bool TryGetInvocationTargetSymbolInfo(
        InvocationExpressionSyntax invocation,
        out SymbolInfo info,
        CancellationToken cancellationToken = default)
    {
        if (TryGetPipeInvocationSymbolInfo(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (TryGetAvailableInvocationSymbolInfo(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (TryGetCachedSymbolInfo(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            return true;
        }

        if (TryGetCachedSymbolInfo(invocation.Expression, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (TryBindInterestRegion(invocation, out var regionBoundInvocation))
        {
            info = regionBoundInvocation.GetSymbolInfo();
            if (HasInvocationTargetSymbolInfo(info))
            {
                CacheInvocationTargetSymbolInfo(invocation, info);
                return true;
            }
        }

        if (TryGetBoundNodeForSemanticQuery(invocation, out var semanticQueryBoundNode) &&
            semanticQueryBoundNode is BoundExpression semanticQueryBoundExpression)
        {
            info = semanticQueryBoundExpression.GetSymbolInfo();
            if (HasInvocationTargetSymbolInfo(info))
            {
                CacheInvocationTargetSymbolInfo(invocation, info);
                return true;
            }
        }

        if (TryRebindInvocationAfterRefreshingFunctionArguments(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (TryResolveInvocationOperatorFromReceiver(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (TryBindExactSymbol(invocation, out info) &&
            HasInvocationTargetSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        if (GetBoundNode(invocation) is BoundInvocationExpression boundInvocation)
        {
            info = new SymbolInfo(boundInvocation.Method);
            CacheInvocationTargetSymbolInfo(invocation, info);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        info = SymbolInfo.None;
        return false;
    }

    private bool TryGetPipeInvocationSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        var invocation = TryGetPipeInvocationForSymbolNode(node);
        if (invocation is null)
            return false;

        if (invocation.Parent is not InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression ||
            !IsSameSyntaxNode(pipeExpression.Right, invocation))
        {
            return false;
        }

        if (TryGetCachedBoundNode(pipeExpression) is BoundInvocationExpression cachedBoundPipe)
        {
            info = cachedBoundPipe.GetSymbolInfo();
            return info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;
        }

        if (TryGetAvailableInvocationSymbolInfo(invocation, out info) &&
            HasSymbolInfo(info))
        {
            CacheInvocationTargetSymbolInfo(invocation, info);
            StorePipeExpressionTypeMapping(pipeExpression, info);
            return true;
        }

        var binderInfo = GetBinderForIncrementalSemanticQuery(pipeExpression).BindSymbol(pipeExpression);
        if (binderInfo.Symbol is not null || !binderInfo.CandidateSymbols.IsDefaultOrEmpty)
        {
            info = binderInfo;
            StorePipeExpressionTypeMapping(pipeExpression, info);
            return true;
        }

        if (GetBoundNode(pipeExpression) is not BoundInvocationExpression boundPipe)
            return false;

        info = boundPipe.GetSymbolInfo();
        StorePipeExpressionTypeMapping(pipeExpression, info);
        return info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;
    }

    private void StorePipeExpressionTypeMapping(InfixOperatorExpressionSyntax pipeExpression, SymbolInfo info)
    {
        if (info.Symbol is not IMethodSymbol method)
        {
            return;
        }

        var returnType = GetInvocationReturnType(method);
        if (TryGetAvailableTypeInfo(pipeExpression.Left, out var receiverTypeInfo) &&
            (receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } receiverType)
        {
            returnType = GetAvailablePipeInvocationReturnType(method, receiverType) ?? returnType;
        }

        if (!IsUsefulAvailableExpressionType(returnType))
            return;

        StoreTypeMapping(
            pipeExpression,
            new TypeInfo(returnType, returnType, ComputeConversion(returnType, returnType)));
    }

    private static InvocationExpressionSyntax? TryGetPipeInvocationForSymbolNode(SyntaxNode node)
        => node switch
        {
            InvocationExpressionSyntax invocation => invocation,
            SimpleNameSyntax simpleName
                when simpleName.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, simpleName) => invocation,
            SimpleNameSyntax simpleName
                when simpleName.Parent is MemberAccessExpressionSyntax memberAccess &&
                     IsSameSyntaxNode(memberAccess.Name, simpleName) &&
                     memberAccess.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) => invocation,
            MemberAccessExpressionSyntax memberAccess
                when memberAccess.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) => invocation,
            _ => null
        };

    private bool TryGetMemberBindingTargetMemberSymbolInfo(MemberBindingExpressionSyntax memberBinding, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        var memberName = memberBinding.Name.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(memberName))
            return false;

        if (!TryGetTargetTypeForExpression(memberBinding, out var targetType) ||
            targetType is null ||
            targetType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        if (TryGetTargetTypedUnionCaseSymbolInfo(targetType, memberName, memberBinding, out var unionCaseInfo))
        {
            info = unionCaseInfo;
            return true;
        }

        var members = targetType
            .GetMembers(memberName)
            .Where(static member => member is IPropertySymbol or IFieldSymbol or IMethodSymbol)
            .ToImmutableArray<ISymbol>();

        if (members.IsDefaultOrEmpty)
            return false;

        var preferred = members.OfType<IPropertySymbol>().FirstOrDefault()
            ?? members.OfType<IFieldSymbol>().FirstOrDefault() as ISymbol
            ?? members.OfType<IMethodSymbol>().FirstOrDefault()
            ?? members[0];

        info = members.Length == 1
            ? new SymbolInfo(preferred)
            : new SymbolInfo(preferred, members, CandidateReason.MemberGroup);
        return true;
    }

    private static bool TryGetTargetTypedUnionCaseSymbolInfo(
        ITypeSymbol targetType,
        string memberName,
        MemberBindingExpressionSyntax memberBinding,
        out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (!TryGetTargetTypedUnionCaseType(targetType, memberName, out var caseType))
            return false;

        var constructor = memberBinding.Parent is InvocationExpressionSyntax invocation &&
                          IsSameSyntaxNode(invocation.Expression, memberBinding)
            ? ChooseUnionCaseConstructor(caseType, invocation.ArgumentList.Arguments.Count)
            : null;

        info = constructor is not null
            ? new SymbolInfo(constructor)
            : new SymbolInfo(caseType);
        return true;
    }

    private static bool TryGetTargetTypedUnionCaseType(
        ITypeSymbol targetType,
        string memberName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INamedTypeSymbol? caseType)
    {
        caseType = null;

        var normalizedTargetType = targetType.UnwrapLiteralType() ?? targetType;
        normalizedTargetType = normalizedTargetType.GetNonNullableType();

        return normalizedTargetType is INamedTypeSymbol targetNamedType &&
               targetNamedType.TryFindUnionCaseType(memberName, out caseType);
    }

    private static IMethodSymbol? ChooseUnionCaseConstructor(
        INamedTypeSymbol caseType,
        int argumentCount)
        => caseType.Constructors.FirstOrDefault(constructor =>
               SupportsInvocationArgumentCount(constructor.Parameters, argumentCount))
           ?? caseType.Constructors.FirstOrDefault();

    private static bool SupportsInvocationArgumentCount(
        ImmutableArray<IParameterSymbol> parameters,
        int argumentCount)
    {
        var hasParamsParameter = parameters.Length > 0 && parameters[^1].IsVarParams;
        if (!hasParamsParameter && argumentCount > parameters.Length)
            return false;

        var required = parameters.Length;
        while (required > 0 &&
               (parameters[required - 1].HasExplicitDefaultValue || parameters[required - 1].IsVarParams))
        {
            required--;
        }

        return argumentCount >= required;
    }

    private bool TryGetTargetTypeForExpression(ExpressionSyntax expression, out ITypeSymbol? targetType)
    {
        targetType = null;

        if (TryGetArgumentTargetTypeForExpression(expression, out targetType))
            return true;

        if (expression.Parent is ReturnStatementSyntax &&
            TryGetEnclosingSourceMethod(expression) is { } method)
        {
            targetType = method.ReturnType;
            if (method.IsAsync &&
                AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, targetType) is { } asyncResultType)
            {
                targetType = asyncResultType;
            }

            return targetType.TypeKind != TypeKind.Error;
        }

        if (expression.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variableDeclarator } &&
            variableDeclarator.TypeAnnotation?.Type is { } annotationType)
        {
            targetType = GetTypeInfo(annotationType).Type;
            return targetType is not null && targetType.TypeKind != TypeKind.Error;
        }

        if (expression.Parent is WithAssignmentSyntax withAssignment &&
            TryGetWithAssignmentMemberSymbolInfo(withAssignment, out var withAssignmentInfo))
        {
            targetType = withAssignmentInfo.Symbol switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null
            };

            return targetType is not null && targetType.TypeKind != TypeKind.Error;
        }

        return false;
    }

    internal bool TryGetContextualTargetTypeForExpression(ExpressionSyntax expression, out ITypeSymbol? targetType)
        => TryGetTargetTypeForExpression(expression, out targetType);

    private bool TryGetArgumentTargetTypeForExpression(ExpressionSyntax expression, out ITypeSymbol? targetType)
    {
        targetType = null;

        var contextualExpression = expression;
        if (expression.Parent is InvocationExpressionSyntax invocation &&
            IsSameSyntaxNode(invocation.Expression, expression))
        {
            contextualExpression = invocation;
        }

        if (contextualExpression.Parent is not ArgumentSyntax argument ||
            !IsSameSyntaxNode(argument.Expression, contextualExpression) ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax containingInvocation)
        {
            return false;
        }

        var parameterType = TryGetCommonConstructorArgumentType(containingInvocation, argumentList.Arguments, argument)
            ?? TryGetInvocationArgumentParameterType(containingInvocation, argumentList.Arguments, argument);
        if (parameterType is null || parameterType.TypeKind == TypeKind.Error)
            return false;

        targetType = parameterType;
        return true;
    }

    private ITypeSymbol? TryGetCommonConstructorArgumentType(
        InvocationExpressionSyntax invocation,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        INamedTypeSymbol? typeSymbol = null;
        if (invocation.Expression is TypeSyntax typeSyntax &&
            GetTypeInfo(typeSyntax).Type is INamedTypeSymbol directType)
        {
            typeSymbol = directType;
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier &&
                 TryLookupAvailableNamedType(identifier.Identifier.ValueText, 0, out var availableType) &&
                 availableType is not null)
        {
            typeSymbol = availableType;
        }
        else if (invocation.Expression is GenericNameSyntax genericName)
        {
            var typeArguments = ResolveAvailableTypeArguments(genericName.TypeArgumentList);
            if (!typeArguments.IsDefault &&
                TryLookupAvailableNamedType(genericName.Identifier.ValueText, typeArguments.Length, out var availableGenericType) &&
                availableGenericType is not null)
            {
                typeSymbol = availableGenericType.TypeParameters.Length == typeArguments.Length
                    ? availableGenericType.Construct(typeArguments.ToArray()) as INamedTypeSymbol
                    : availableGenericType;
            }
        }
        else if (GetSymbolInfo(invocation.Expression).Symbol is INamedTypeSymbol expressionType)
        {
            typeSymbol = expressionType;
        }

        if (typeSymbol is null)
            return null;

        return TryGetCommonConstructorArgumentType(typeSymbol, arguments, argument);
    }

    private static ITypeSymbol? TryGetCommonConstructorArgumentType(
        INamedTypeSymbol typeSymbol,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        ITypeSymbol? commonType = null;
        var hasCommonType = false;

        foreach (var constructor in typeSymbol.Constructors)
        {
            var parameter = GetParameterForArgument(constructor, arguments, argument);
            if (parameter is null)
                continue;

            if (!hasCommonType)
            {
                commonType = parameter.Type;
                hasCommonType = true;
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(commonType, parameter.Type))
                return null;
        }

        return hasCommonType ? commonType : null;
    }

    private ITypeSymbol? TryGetInvocationArgumentParameterType(
        InvocationExpressionSyntax invocation,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        if (!TryGetInvocationMethodSymbol(invocation, out var method) || method is null)
            return null;

        return GetParameterForArgument(method, arguments, argument)?.Type;
    }

    private bool TryGetAvailableTargetTypeForUnionCaseInvocation(
        InvocationExpressionSyntax invocation,
        out ITypeSymbol? targetType)
    {
        targetType = null;

        if (invocation.Parent is ArgumentSyntax argument &&
            IsSameSyntaxNode(argument.Expression, invocation) &&
            argument.Parent is ArgumentListSyntax argumentList &&
            argumentList.Parent is InvocationExpressionSyntax containingInvocation)
        {
            targetType = TryGetCommonConstructorArgumentTypeFromAvailableConstructor(
                containingInvocation,
                argumentList.Arguments,
                argument);
            return targetType is not null && targetType.TypeKind != TypeKind.Error;
        }

        if (invocation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variableDeclarator } &&
            variableDeclarator.TypeAnnotation?.Type is { } annotationType)
        {
            targetType = GetTypeInfo(annotationType).Type;
            return targetType is not null && targetType.TypeKind != TypeKind.Error;
        }

        return false;
    }

    private ITypeSymbol? TryGetCommonConstructorArgumentTypeFromAvailableConstructor(
        InvocationExpressionSyntax invocation,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        INamedTypeSymbol? typeSymbol = null;
        if (invocation.Expression is TypeSyntax typeSyntax &&
            GetTypeInfo(typeSyntax).Type is INamedTypeSymbol directType)
        {
            typeSymbol = directType;
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier &&
                 TryLookupAvailableNamedType(identifier.Identifier.ValueText, 0, out var availableType) &&
                 availableType is not null)
        {
            typeSymbol = availableType;
        }
        else if (invocation.Expression is GenericNameSyntax genericName)
        {
            var typeArguments = ResolveAvailableTypeArguments(genericName.TypeArgumentList);
            if (!typeArguments.IsDefault &&
                TryLookupAvailableNamedType(genericName.Identifier.ValueText, typeArguments.Length, out var availableGenericType) &&
                availableGenericType is not null)
            {
                typeSymbol = availableGenericType.TypeParameters.Length == typeArguments.Length
                    ? availableGenericType.Construct(typeArguments.ToArray()) as INamedTypeSymbol
                    : availableGenericType;
            }
        }

        if (typeSymbol is null)
            return null;

        return TryGetCommonConstructorArgumentType(typeSymbol, arguments, argument);
    }

    private bool TryGetWithAssignmentMemberSymbolInfo(WithAssignmentSyntax assignment, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        var withExpression = assignment.Parent as WithExpressionSyntax
            ?? assignment.Ancestors().OfType<WithExpressionSyntax>().FirstOrDefault();
        if (withExpression is null)
            return false;

        if (!TryGetWithReceiverType(withExpression.Expression, out var receiverType) ||
            receiverType is null ||
            receiverType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var memberName = assignment.Name.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(memberName))
            return false;

        var members = receiverType
            .GetMembers(memberName)
            .Where(static member => member is IPropertySymbol or IFieldSymbol)
            .Where(static member => !member.IsStatic)
            .ToImmutableArray<ISymbol>();
        if (members.IsDefaultOrEmpty)
            return false;

        var preferred = members.OfType<IPropertySymbol>().FirstOrDefault()
            ?? members.OfType<IFieldSymbol>().FirstOrDefault() as ISymbol
            ?? members[0];

        info = members.Length == 1
            ? new SymbolInfo(preferred)
            : new SymbolInfo(preferred, members, CandidateReason.MemberGroup);
        return true;
    }

    private bool TryGetWithReceiverType(ExpressionSyntax receiver, out INamedTypeSymbol? receiverType)
    {
        receiverType = null;

        if (receiver is TypeSyntax typeSyntax)
        {
            receiverType = GetTypeInfo(typeSyntax).Type as INamedTypeSymbol;
            if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                return true;
        }

        if (GetTypeInfo(receiver).Type is INamedTypeSymbol expressionType &&
            expressionType.TypeKind != TypeKind.Error)
        {
            receiverType = expressionType;
            return true;
        }

        var symbol = GetSymbolInfo(receiver).Symbol;
        receiverType = symbol switch
        {
            INamedTypeSymbol namedType => namedType,
            ILocalSymbol { Type: INamedTypeSymbol localType } => localType,
            IParameterSymbol { Type: INamedTypeSymbol parameterType } => parameterType,
            IPropertySymbol { Type: INamedTypeSymbol propertyType } => propertyType,
            IFieldSymbol { Type: INamedTypeSymbol fieldType } => fieldType,
            _ => null
        };

        if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
            return true;

        if (TryGetCachedBoundNode(receiver) is BoundTypeExpression { Type: INamedTypeSymbol boundType })
        {
            receiverType = boundType;
            return receiverType.TypeKind != TypeKind.Error;
        }

        if (TryGetCachedBoundNode(receiver) is BoundObjectCreationExpression { Type: INamedTypeSymbol createdType })
        {
            receiverType = createdType;
            return receiverType.TypeKind != TypeKind.Error;
        }

        return false;
    }

    public bool TryGetSymbolInfo(SyntaxNode node, out SymbolInfo info, CancellationToken cancellationToken = default)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        if (node is IdentifierNameSyntax functionParameterReference &&
            TryGetAvailableFunctionExpressionParameterReferenceSymbolInfo(functionParameterReference, out info))
        {
            return true;
        }

        if (node is IdentifierNameSyntax contextualFunctionParameterReference &&
            TryGetFunctionExpressionParameterReferenceSymbolInfo(contextualFunctionParameterReference, out info))
        {
            return true;
        }

        if (TryGetAvailableSymbolInfo(node, out info))
            return true;

        info = GetSymbolInfo(node, cancellationToken);
        return info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;
    }

    internal bool TryGetCachedSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        if (TryGetSymbolMapping(node, out info) && HasSymbolInfo(info))
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoCacheHit();
            return true;
        }

        if (TryGetCachedBoundSymbolInfo(node, out info))
            return true;

        if (node is IdentifierNameSyntax identifier)
        {
            if (identifier.Parent is InvocationExpressionSyntax invocation &&
                IsSameSyntaxNode(invocation.Expression, identifier))
            {
                if (TryGetCachedSymbolInfo(invocation, out info))
                {
                    StoreSymbolMapping(node, info);
                    return true;
                }
            }

            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess)
            {
                if (IsSameSyntaxNode(memberAccess.Name, identifier))
                {
                    if (memberAccess.Parent is InvocationExpressionSyntax memberInvocation &&
                        IsSameSyntaxNode(memberInvocation.Expression, memberAccess) &&
                        TryGetCachedSymbolInfo(memberInvocation, out info))
                    {
                        StoreSymbolMapping(node, info);
                        return true;
                    }

                    if (TryGetCachedSymbolInfo(memberAccess, out info))
                    {
                        StoreSymbolMapping(node, info);
                        return true;
                    }
                }
                else if (IsSameSyntaxNode(memberAccess.Expression, identifier) &&
                         TryGetCachedBoundNode(memberAccess) is BoundMemberAccessExpression boundMemberAccess)
                {
                    info = boundMemberAccess.Receiver.GetSymbolInfo();
                    if (HasSymbolInfo(info))
                    {
                        info = ProjectBackingFieldSymbolsToAssociatedProperty(node, info);
                        StoreSymbolMapping(node, info);
                        return true;
                    }
                }
            }

            if (identifier.Parent is MemberBindingExpressionSyntax memberBinding &&
                IsSameSyntaxNode(memberBinding.Name, identifier) &&
                TryGetCachedSymbolInfo(memberBinding, out info))
            {
                StoreSymbolMapping(node, info);
                return true;
            }
        }

        info = default;
        return false;
    }

    private bool TryGetSymbolMapping(SyntaxNode node, out SymbolInfo info)
    {
        if (_isCollectingDiagnostics && _nonReportingSymbolMappings.ContainsKey(node))
        {
            info = default;
            return false;
        }

        return _symbolMappings.TryGetValue(node, out info);
    }

    private bool TryGetCachedTypeInfo(SyntaxNode node, out TypeInfo info)
    {
        if (_isCollectingDiagnostics && _nonReportingTypeMappings.ContainsKey(node))
        {
            info = default;
            return false;
        }

        if (_typeMappings.TryGetValue(node, out info))
            return true;

        var nodeKey = GetSyntaxNodeMapKey(node);
        if (_isCollectingDiagnostics && _nonReportingTypeMappingsByKey.ContainsKey(nodeKey))
        {
            info = default;
            return false;
        }

        return _typeMappingsByKey.TryGetValue(nodeKey, out info);
    }

    private void StoreSymbolMapping(SyntaxNode node, SymbolInfo info, bool nonReporting = false)
    {
        StoreSymbolMappingExact(node, info, nonReporting || !IsCollectingDiagnostics);
    }

    private void StoreSymbolMappingExact(SyntaxNode node, SymbolInfo info, bool nonReporting)
    {
        _symbolMappings[node] = info;
        UpdateNonReportingSymbolMapping(node, nonReporting);
    }

    private void StoreTypeMapping(SyntaxNode node, TypeInfo info, bool nonReporting = false)
    {
        _typeMappings[node] = info;
        _typeMappingsByKey[GetSyntaxNodeMapKey(node)] = info;
        UpdateNonReportingTypeMapping(node, nonReporting || !IsCollectingDiagnostics);
    }

    private void UpdateNonReportingSymbolMapping(SyntaxNode node, bool isNonReporting)
    {
        if (isNonReporting)
            _nonReportingSymbolMappings[node] = 0;
        else
            _nonReportingSymbolMappings.TryRemove(node, out _);
    }

    private void UpdateNonReportingTypeMapping(SyntaxNode node, bool isNonReporting)
    {
        if (isNonReporting)
        {
            _nonReportingTypeMappings[node] = 0;
            _nonReportingTypeMappingsByKey[GetSyntaxNodeMapKey(node)] = 0;
        }
        else
        {
            _nonReportingTypeMappings.TryRemove(node, out _);
            _nonReportingTypeMappingsByKey.TryRemove(GetSyntaxNodeMapKey(node), out _);
        }
    }

    /// <summary>
    /// Tries to retrieve symbol information from semantic state that is already available.
    /// This method does not bind cold bodies, create operations, or run diagnostics.
    /// </summary>
    internal bool TryGetAvailableSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        if (node is MemberBindingExpressionSyntax memberBinding &&
            TryGetMemberBindingTargetMemberSymbolInfo(memberBinding, out info))
        {
            StoreSymbolMapping(node, info);
            return true;
        }

        if (node is IdentifierNameSyntax functionParameterReference &&
            TryGetAvailableFunctionExpressionParameterReferenceSymbolInfo(functionParameterReference, out info))
        {
            return true;
        }

        if (node is IdentifierNameSyntax forTargetReference &&
            TryGetAvailableForTargetSymbolInfo(forTargetReference, out info))
        {
            StoreSymbolMapping(node, info);
            if (info.Symbol is { } forTargetSymbol)
                StoreNodeInterestSymbolDescriptor(node, forTargetSymbol);
            return true;
        }

        if (node is IdentifierNameSyntax identifier &&
            TryLookupVisibleValueSymbol(identifier) is { } visibleSymbol)
        {
            info = new SymbolInfo(visibleSymbol);
            StoreSymbolMapping(node, info);
            StoreNodeInterestSymbolDescriptor(node, visibleSymbol);
            return true;
        }

        if (TryGetCachedSymbolInfo(node, out info))
            return true;

        if (node is IdentifierNameSyntax syntaxScopedIdentifier &&
            TryResolveAvailableLocalReferenceFromSyntax(syntaxScopedIdentifier, out var syntaxScopedLocal))
        {
            info = new SymbolInfo(syntaxScopedLocal);
            StoreSymbolMapping(node, info);
            StoreNodeInterestSymbolDescriptor(node, syntaxScopedLocal);
            return true;
        }

        if (node is IdentifierNameSyntax aliasIdentifier &&
            TryLookupAvailableAlias(aliasIdentifier, out var aliasSymbol) &&
            aliasSymbol is not null)
        {
            info = new SymbolInfo(aliasSymbol);
            StoreSymbolMapping(node, info);
            StoreNodeInterestSymbolDescriptor(node, aliasSymbol);
            return true;
        }

        if (TryGetAvailableNamespaceSymbolInfo(node, out info))
            return true;

        if (node is IdentifierNameSyntax namedTypeIdentifier &&
            IsAvailableNamedTypeLookupContext(namedTypeIdentifier) &&
            TryLookupAvailableNamedType(namedTypeIdentifier.Identifier.ValueText, 0, out var namedType) &&
            namedType is not null)
        {
            info = new SymbolInfo(namedType);
            StoreSymbolMapping(node, info);
            StoreNodeInterestSymbolDescriptor(node, namedType);
            return true;
        }

        if (TryGetAvailableMemberAccessSymbolInfo(node, out info))
            return true;

        if (TryGetCachedNodeInterestSymbolInfo(node, out info))
            return true;

        if (TryGetDeclarationSymbolInfo(node, out info))
            return true;

        info = default;
        return false;
    }

    private bool TryGetAvailableNamespaceSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        ExpressionSyntax? namespaceExpression = node switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess,
            SimpleNameSyntax simpleName when
                simpleName.Parent is MemberAccessExpressionSyntax parentAccess &&
                IsSameSyntaxNode(parentAccess.Name, simpleName) => parentAccess,
            SimpleNameSyntax simpleName => simpleName,
            _ => null
        };

        if (namespaceExpression is null ||
            !TryResolveAvailableNamespaceExpression(namespaceExpression, out var namespaceSymbol) ||
            namespaceSymbol is null)
        {
            info = default;
            return false;
        }

        info = new SymbolInfo(namespaceSymbol);
        StoreSymbolMapping(node, info);
        if (!ReferenceEquals(node, namespaceExpression))
            StoreSymbolMapping(namespaceExpression, info);
        StoreNodeInterestSymbolDescriptor(node, namespaceSymbol);
        return true;
    }

    private bool TryResolveAvailableNamespaceExpression(
        ExpressionSyntax expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INamespaceSymbol? namespaceSymbol)
    {
        namespaceSymbol = expression switch
        {
            SimpleNameSyntax simpleName =>
                Compilation.SymbolLookup.LookupNamespaceSourceFirst(null, simpleName.Identifier.ValueText),
            MemberAccessExpressionSyntax
            {
                Expression: { } receiver,
                Name: SimpleNameSyntax memberName
            } when TryResolveAvailableNamespaceExpression(receiver, out var containingNamespace) =>
                Compilation.SymbolLookup.LookupNamespaceSourceFirst(
                    containingNamespace,
                    memberName.Identifier.ValueText),
            _ => null
        };

        return namespaceSymbol is not null;
    }

    private bool TryGetAvailableForTargetSymbolInfo(IdentifierNameSyntax identifier, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (identifier.Parent is not ForStatementSyntax forStatement ||
            !IsSameSyntaxNode(forStatement.Target, identifier) ||
            string.IsNullOrWhiteSpace(identifier.Identifier.ValueText))
        {
            return false;
        }

        var iterationType = TryGetForIterationElementType(forStatement.Expression);
        if (iterationType is null || iterationType.TypeKind == TypeKind.Error)
            return false;

        info = new SymbolInfo(CreateSyntheticInterestLocalSymbol(identifier.Identifier.ValueText, iterationType, identifier));
        return true;
    }

    private bool TryGetAvailableFunctionExpressionParameterReferenceSymbolInfo(
        IdentifierNameSyntax identifier,
        out SymbolInfo info)
    {
        info = default;

        if (!TryGetEnclosingFunctionExpression(identifier, out var functionExpression))
            return false;

        foreach (var parameter in GetFunctionExpressionParameters(functionExpression))
        {
            if (!string.Equals(parameter.Identifier.ValueText, identifier.Identifier.ValueText, StringComparison.Ordinal))
                continue;

            if (!TryResolveFunctionExpressionParameterSymbolFast(parameter, out var parameterSymbol) ||
                parameterSymbol is null)
            {
                return false;
            }

            info = new SymbolInfo(parameterSymbol);
            StoreSymbolMapping(identifier, info);
            StoreNodeInterestSymbolDescriptor(identifier, parameterSymbol);
            return true;
        }

        return false;
    }

    private bool TryGetVisibleValueIdentifierSymbolInfo(IdentifierNameSyntax identifier, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (!IsValueIdentifierLookupContext(identifier))
            return false;

        if (TryLookupVisibleValueSymbol(identifier) is not { } visibleSymbol)
            return false;

        info = new SymbolInfo(visibleSymbol);
        StoreSymbolMapping(identifier, info);
        StoreNodeInterestSymbolDescriptor(identifier, visibleSymbol);
        return true;
    }

    private static bool IsValueIdentifierLookupContext(IdentifierNameSyntax identifier)
    {
        return identifier.Parent switch
        {
            MemberAccessExpressionSyntax memberAccess when ReferenceEquals(memberAccess.Name, identifier) => false,
            QualifiedNameSyntax => false,
            TypeAnnotationClauseSyntax => false,
            TypeArgumentSyntax => false,
            ImportDirectiveSyntax => false,
            AttributeSyntax => false,
            NameColonSyntax => false,
            _ => true,
        };
    }

    private bool TryGetFunctionExpressionParameterReferenceSymbolInfo(
        IdentifierNameSyntax identifier,
        out SymbolInfo info)
    {
        info = default;

        if (!TryGetEnclosingFunctionExpression(identifier, out var functionExpression))
            return false;

        foreach (var parameter in GetFunctionExpressionParameters(functionExpression))
        {
            if (!string.Equals(parameter.Identifier.ValueText, identifier.Identifier.ValueText, StringComparison.Ordinal))
                continue;

            var parameterSymbol = GetFunctionExpressionParameterSymbol(parameter);
            if (parameterSymbol is null)
                return false;

            info = new SymbolInfo(parameterSymbol);
            StoreSymbolMapping(identifier, info);
            StoreNodeInterestSymbolDescriptor(identifier, parameterSymbol);
            return true;
        }

        return false;
    }

    private static IEnumerable<ParameterSyntax> GetFunctionExpressionParameters(FunctionExpressionSyntax functionExpression)
    {
        return functionExpression switch
        {
            SimpleFunctionExpressionSyntax { Parameter: { } parameter } => [parameter],
            ParenthesizedFunctionExpressionSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
            _ => []
        };
    }

    private bool TryLookupAvailableAlias(IdentifierNameSyntax identifier, out IAliasSymbol? alias)
    {
        alias = null;

        if (!IsAvailableNamedTypeLookupContext(identifier))
            return false;

        var name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        for (var binder = GetBinderForIncrementalSemanticQuery(identifier); binder is not null; binder = binder.ParentBinder)
        {
            if (binder is not ImportBinder importBinder)
                continue;

            if (importBinder.GetAliases().TryGetValue(name, out var aliases))
            {
                alias = aliases.FirstOrDefault();
                return alias is not null;
            }
        }

        return false;
    }

    private static bool IsAvailableNamedTypeLookupContext(IdentifierNameSyntax identifier)
    {
        if (!identifier.AncestorsAndSelf().OfType<TypeSyntax>().Any() &&
            !IsLikelyTypeIdentifier(identifier.Identifier.ValueText))
        {
            return false;
        }

        if (identifier.AncestorsAndSelf().OfType<TypeSyntax>().Any())
            return true;

        if (identifier.Parent is InvocationExpressionSyntax invocation &&
            IsSameSyntaxNode(invocation.Expression, identifier))
        {
            return true;
        }

        return identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
               IsSameSyntaxNode(memberAccess.Expression, identifier);
    }

    private static bool IsLikelyTypeIdentifier(string name)
        => !string.IsNullOrEmpty(name) && char.IsUpper(name[0]);

    private static bool IsLikelyTypeExpression(ExpressionSyntax expression)
        => expression switch
        {
            SimpleNameSyntax simpleName => IsLikelyTypeIdentifier(simpleName.Identifier.ValueText),
            MemberAccessExpressionSyntax memberAccess => IsLikelyTypeExpression(memberAccess.Expression),
            _ => false
        };

    private bool ShouldUseDirectTypeMemberFastPath(ExpressionSyntax expression)
    {
        if (!IsLikelyTypeExpression(expression))
            return false;

        if (!TryGetAvailableSymbolInfo(expression, out var receiverInfo))
            return true;

        if (GetNamedTypeFromAvailableSymbol(receiverInfo.Symbol) is not null)
            return true;

        return receiverInfo.CandidateSymbols
            .Select(GetNamedTypeFromAvailableSymbol)
            .Any(static type => type is not null);
    }

    private bool TryResolveAvailableLocalReferenceFromSyntax(
        IdentifierNameSyntax identifier,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSymbol? localSymbol)
    {
        localSymbol = null;

        var name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var scope in identifier.Ancestors().Where(static ancestor =>
                     ancestor is BlockSyntax or BlockStatementSyntax or CompilationUnitSyntax))
        {
            foreach (var declarator in DescendantNodesExcludingNestedScopes(scope).OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Span.Start >= identifier.Span.Start ||
                    !string.Equals(declarator.Identifier.ValueText, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryGetAvailableLocalDeclarationSymbol(
                        declarator,
                        out localSymbol,
                        allowErrorType: true,
                        allowInitializerBinding: true,
                        allowBindingFallback: false))
                    return true;
            }
        }

        return false;
    }

    private bool TryGetAvailableInvocationSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        var invocation = TryGetInvocationForSymbolInfoNode(node);
        if (invocation is null)
            return false;

        if (!_availableInvocationSymbolInfoInProgress.TryAdd(invocation, 0))
        {
            return false;
        }

        try
        {
            if (!TryGetAvailableInvocationCandidates(invocation, out var methods) ||
                methods.IsDefaultOrEmpty)
            {
                return false;
            }

            if (methods.Any(static method => method.TypeParameters.Length > 0))
            {
                if (TryGetInvocationGenericName(invocation.Expression) is { } genericName)
                {
                    var typeArguments = ResolveAvailableTypeArguments(genericName.TypeArgumentList);
                    if (typeArguments.IsDefaultOrEmpty ||
                        typeArguments.Length != genericName.TypeArgumentList.Arguments.Count)
                    {
                        return false;
                    }

                    methods = methods
                        .Select(method => method.TypeParameters.Length == typeArguments.Length
                            ? method.Construct(typeArguments.ToArray())
                            : method)
                        .ToImmutableArray();
                }
                else
                {
                    var inferredMethods = methods
                        .Select(method => ConstructAvailableGenericInvocationCandidate(method, invocation))
                        .ToImmutableArray();

                    // Available semantic state is only an optimization. If it cannot fully
                    // infer one member of an overload set, do not silently remove that member
                    // and let a less suitable overload win; let authoritative binding decide.
                    if (inferredMethods.Any(static method =>
                            method.TypeParameters.Length > 0 &&
                            HasUnresolvedMethodTypeParameters(method)))
                    {
                        return false;
                    }

                    methods = inferredMethods;
                }
            }

            var rejectedByConstraints = ImmutableArray.CreateBuilder<IMethodSymbol>();
            var applicableMethods = ImmutableArray.CreateBuilder<IMethodSymbol>(methods.Length);
            var constraintBinder = GetBinderForIncrementalSemanticQuery(invocation);
            foreach (var method in methods)
            {
                if (method.IsGenericMethod &&
                    method.TypeArguments.Length == method.TypeParameters.Length &&
                    method.TypeArguments.All(static argument => argument is not ITypeParameterSymbol) &&
                    !OverloadResolver.SatisfiesMethodConstraints(
                        method,
                        method.TypeArguments,
                        constraintBinder,
                        out _))
                {
                    rejectedByConstraints.Add(method);
                    continue;
                }

                applicableMethods.Add(method);
            }

            if (applicableMethods.Count == 0 && rejectedByConstraints.Count > 0)
            {
                info = new SymbolInfo(
                    CandidateReason.OverloadResolutionFailure,
                    rejectedByConstraints.Cast<ISymbol>().ToImmutableArray());
                CacheAvailableInvocationSymbolInfo(invocation, info);
                return true;
            }

            methods = applicableMethods.ToImmutable();

            var candidates = methods.Cast<ISymbol>().ToImmutableArray();
            var preferred = methods.Length == 1
                ? methods[0]
                : TryChooseAvailablePipeInvocationMethodCandidate(methods, invocation) ??
                  TryChooseAvailableInvocationMethodCandidate(methods, invocation) ??
                  TryChooseInvocationMethodCandidate(methods, invocation, InvocationCandidateFallback.None);

            if (preferred is null)
                return false;

            info = new SymbolInfo(preferred, candidates);
            CacheAvailableInvocationSymbolInfo(invocation, info);
            return true;
        }
        finally
        {
            _availableInvocationSymbolInfoInProgress.TryRemove(invocation, out _);
        }
    }

    private IMethodSymbol? TryChooseAvailablePipeInvocationMethodCandidate(
        IEnumerable<IMethodSymbol> methods,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is not InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression ||
            !IsSameSyntaxNode(pipeExpression.Right, invocation) ||
            !TryGetAvailableTypeInfo(pipeExpression.Left, out var receiverTypeInfo) ||
            (receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType) is not { TypeKind: not TypeKind.Error } receiverType)
        {
            return null;
        }

        IMethodSymbol? selected = null;
        var selectedScore = int.MinValue;
        var isAmbiguous = false;

        foreach (var method in methods)
        {
            var compatibility = GetPipeInvocationArgumentCompatibility(method, invocation);
            if (compatibility < 0)
                continue;

            var score = compatibility * 100;
            if (method.Parameters.Length > 0)
            {
                var receiverParameterType = method.Parameters[0].Type;
                if (receiverParameterType is not null &&
                    receiverParameterType.TypeKind != TypeKind.Error)
                {
                    if (SymbolEqualityComparer.Default.Equals(receiverParameterType, receiverType))
                    {
                        score += 20;
                    }
                    else if (Compilation.ClassifyConversion(receiverType, receiverParameterType, includeUserDefined: false).Exists)
                    {
                        score += 10;
                    }
                }

                if (method.Parameters.Length > 1 &&
                    IsExpressionTreeDelegateParameter(method.Parameters[1].Type))
                {
                    score += 2;
                }
            }

            if (score > selectedScore)
            {
                selected = method;
                selectedScore = score;
                isAmbiguous = false;
            }
            else if (score == selectedScore)
            {
                isAmbiguous = true;
            }
        }

        return isAmbiguous ? null : selected;
    }

    private static bool HasUnresolvedMethodTypeParameters(IMethodSymbol method)
    {
        if (method.TypeParameters.IsDefaultOrEmpty)
            return false;

        if (ContainsTypeParameter(method.ReturnType))
            return true;

        foreach (var parameter in method.Parameters)
        {
            if (ContainsTypeParameter(parameter.Type))
                return true;
        }

        return false;
    }

    private IMethodSymbol ConstructAvailableGenericInvocationCandidate(
        IMethodSymbol method,
        InvocationExpressionSyntax invocation)
    {
        if (method.TypeParameters.IsDefaultOrEmpty)
            return method;

        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        TryInferAvailableInvocationArgumentSubstitutions(method, invocation, substitutions);
        TryInferFunctionArgumentSubstitutions(
            method,
            invocation,
            IsAvailableExtensionReceiverInvocation(method, invocation) ? 1 : 0,
            substitutions);

        if (substitutions.Count == 0)
            return method;

        var typeArguments = new ITypeSymbol[method.TypeParameters.Length];
        var changed = false;

        for (var i = 0; i < method.TypeParameters.Length; i++)
        {
            var typeParameter = method.TypeParameters[i];
            var existingArgument = method.TypeArguments.Length == method.TypeParameters.Length
                ? method.TypeArguments[i]
                : typeParameter;

            if (TryGetInferredTypeArgument(typeParameter, substitutions, out var inferred) &&
                inferred.TypeKind != TypeKind.Error)
            {
                typeArguments[i] = inferred;
                changed |= !SymbolEqualityComparer.Default.Equals(existingArgument, inferred);
            }
            else
            {
                typeArguments[i] = existingArgument;
            }
        }

        return changed ? method.Construct(typeArguments) : method;
    }

    private void TryInferAvailableInvocationArgumentSubstitutions(
        IMethodSymbol method,
        InvocationExpressionSyntax invocation,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (invocation.ArgumentList.Arguments.Any(static argument => argument.NameColon is not null))
        {
            return;
        }

        var parameterOffset = IsAvailableExtensionReceiverInvocation(method, invocation) ? 1 : 0;
        for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
        {
            var parameterIndex = argumentIndex + parameterOffset;
            if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                continue;

            var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
            if (argumentExpression is FunctionExpressionSyntax)
                continue;

            var argumentType = TryGetNonContextualAvailableArgumentType(argumentExpression);
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                continue;

            var parameterType = SubstituteTypeParameters(method.Parameters[parameterIndex].Type, substitutions);
            _ = TryUnifyExtensionReceiver(parameterType, argumentType, substitutions);
        }
    }

    private static GenericNameSyntax? TryGetInvocationGenericName(ExpressionSyntax expression)
        => expression switch
        {
            GenericNameSyntax genericName => genericName,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName } => genericName,
            _ => null
        };

    private static bool TryCreateAvailableInvocationSymbolInfo(
        IEnumerable<IMethodSymbol> methods,
        InvocationExpressionSyntax invocation,
        out SymbolInfo info)
    {
        var candidateMethods = methods.ToImmutableArray();
        if (candidateMethods.IsDefaultOrEmpty ||
            candidateMethods.Any(static method => method.TypeParameters.Length > 0))
        {
            info = default;
            return false;
        }

        var candidates = candidateMethods.Cast<ISymbol>().ToImmutableArray();
        var preferred = candidateMethods.Length == 1
            ? candidateMethods[0]
            : TryChooseInvocationMethodCandidate(candidateMethods, invocation, InvocationCandidateFallback.FirstCandidate);

        info = new SymbolInfo(preferred, candidates);
        return true;
    }

    internal enum InvocationCandidateFallback
    {
        None,
        FirstCandidate,
        FirstCompatibleOrSecondCandidateWhenArgumentsPresent
    }

    internal static IMethodSymbol? TryChooseInvocationMethodCandidate(
        IEnumerable<IMethodSymbol> methods,
        InvocationExpressionSyntax invocation,
        InvocationCandidateFallback fallback)
    {
        var candidates = methods.ToImmutableArray();
        var argumentCount = invocation.ArgumentList.Arguments.Count;
        IMethodSymbol? selected = null;

        foreach (var method in candidates)
        {
            if (!TryGetFastParameterCount(method, out var total))
                continue;

            var receiverOffset = method.IsExtensionMethod &&
                invocation.Expression is MemberAccessExpressionSyntax
                    ? 1
                    : 0;
            if (total < receiverOffset)
                continue;

            var visibleTotal = total - receiverOffset;
            var required = TryGetFastRequiredParameterCount(method, receiverOffset, out var requiredCount, out var hasParams)
                ? requiredCount
                : visibleTotal;

            if (argumentCount < required || (!hasParams && argumentCount > visibleTotal))
                continue;

            if (selected is not null)
            {
                return fallback == InvocationCandidateFallback.None
                    ? null
                    : selected;
            }

            selected = method;
        }

        if (selected is not null)
            return selected;

        return fallback switch
        {
            InvocationCandidateFallback.FirstCandidate => candidates.FirstOrDefault(),
            InvocationCandidateFallback.FirstCompatibleOrSecondCandidateWhenArgumentsPresent
                when argumentCount > 0 && candidates.Length > 1 => candidates[1],
            _ => null
        };
    }

    internal bool TryChooseAvailableInvocationMethodCandidate(
        ImmutableArray<IMethodSymbol> methods,
        InvocationExpressionSyntax invocation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? method)
    {
        method = TryChooseAvailablePipeInvocationMethodCandidate(methods, invocation) ??
                 TryChooseAvailableInvocationMethodCandidate(methods, invocation);
        return method is not null;
    }

    private static bool TryGetFastRequiredParameterCount(
        IMethodSymbol method,
        int parameterOffset,
        out int requiredCount,
        out bool hasParams)
    {
        requiredCount = 0;
        hasParams = false;

        if (!TryGetFastParameterCount(method, out var parameterCount) ||
            parameterOffset < 0 ||
            parameterOffset > parameterCount)
        {
            return false;
        }

        if (method is PEMethodSymbol peMethod)
        {
            for (var i = parameterOffset; i < parameterCount; i++)
            {
                if (!peMethod.TryGetParameterUsage(i, out var hasExplicitDefaultValue, out var isParams))
                    return false;

                hasParams |= isParams;
                if (!hasExplicitDefaultValue && !isParams)
                    requiredCount++;
            }

            return true;
        }

        var parameters = method.Parameters;
        for (var i = parameterOffset; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            hasParams |= parameter.IsVarParams;
            if (!parameter.HasExplicitDefaultValue && !parameter.IsVarParams)
                requiredCount++;
        }

        return true;
    }

    private IMethodSymbol? TryChooseAvailableInvocationMethodCandidate(
        IEnumerable<IMethodSymbol> methods,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Any(static argument => argument.NameColon is not null))
        {
            return null;
        }

        var arguments = invocation.ArgumentList.Arguments;
        var argumentTypes = new ITypeSymbol?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Expression is FunctionExpressionSyntax)
            {
                continue;
            }

            var argumentType = TryGetNonContextualAvailableArgumentType(arguments[i].Expression);
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                return null;

            argumentTypes[i] = argumentType;
        }

        return TryChooseAvailableInvocationMethodCandidateCore(methods, out var candidate)
            ? candidate
            : null;

        bool TryChooseAvailableInvocationMethodCandidateCore(
            IEnumerable<IMethodSymbol> candidateMethods,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? candidate)
        {
            IMethodSymbol? selected = null;
            var selectedScore = int.MinValue;
            var isAmbiguous = false;

            foreach (var method in candidateMethods)
            {
                if (!TryScoreAvailableInvocationCandidate(method, invocation, argumentTypes, out var score))
                    continue;

                if (score > selectedScore)
                {
                    selected = method;
                    selectedScore = score;
                    isAmbiguous = false;
                }
                else if (score == selectedScore)
                {
                    isAmbiguous = true;
                }
            }

            candidate = isAmbiguous ? null : selected;
            return candidate is not null;
        }
    }

    private bool TryScoreAvailableInvocationCandidate(
        IMethodSymbol method,
        InvocationExpressionSyntax invocation,
        IReadOnlyList<ITypeSymbol?> argumentTypes,
        out int score)
    {
        score = 0;

        var receiverOffset = IsAvailableExtensionReceiverInvocation(method, invocation) ? 1 : 0;
        if (receiverOffset == 1 &&
            !TryScoreAvailableExtensionReceiver(method, invocation, ref score))
        {
            return false;
        }

        if (method is PEMethodSymbol peMethod)
        {
            var peParameterCount = peMethod.ParameterCount;
            if (peParameterCount < receiverOffset ||
                !TryGetFastRequiredParameterCount(method, receiverOffset, out var peRequiredParameterCount, out var peHasParamsParameter))
            {
                return false;
            }

            var visibleParameterCount = peParameterCount - receiverOffset;
            if (argumentTypes.Count < peRequiredParameterCount ||
                argumentTypes.Count > visibleParameterCount ||
                peHasParamsParameter)
            {
                return false;
            }

            for (var i = 0; i < argumentTypes.Count; i++)
            {
                var argumentExpression = invocation.ArgumentList.Arguments[i].Expression;
                if (argumentExpression is FunctionExpressionSyntax functionExpression &&
                    peMethod.TryGetParameterType(i + receiverOffset, out var functionParameterType) &&
                    functionParameterType is not null &&
                    TryScoreAvailableFunctionExpressionArgument(functionExpression, functionParameterType, ref score))
                {
                    continue;
                }

                if (argumentExpression is FunctionExpressionSyntax &&
                    argumentTypes[i] is null)
                {
                    return false;
                }

                if (TryScoreFastMetadataArgumentConversion(argumentTypes[i], peMethod, i + receiverOffset, ref score, out var handled))
                    continue;

                if (handled ||
                    !peMethod.TryGetParameterType(i + receiverOffset, out var parameterType) ||
                        parameterType is null ||
                        !TryScoreAvailableArgumentConversion(argumentTypes[i], parameterType, ref score))
                {
                    return false;
                }
            }

            return true;
        }

        if (method.Parameters.Length < receiverOffset)
            return false;

        var visibleParameters = method.Parameters.Skip(receiverOffset).ToImmutableArray();
        var hasParamsParameter = visibleParameters.Length > 0 && visibleParameters[^1].IsVarParams;
        var requiredParameterCount = visibleParameters.Count(static parameter =>
            !parameter.HasExplicitDefaultValue &&
            !parameter.IsVarParams);

        if (argumentTypes.Count < requiredParameterCount)
            return false;

        if (argumentTypes.Count > visibleParameters.Length || hasParamsParameter)
            return false;

        for (var i = 0; i < argumentTypes.Count; i++)
        {
            var argumentExpression = invocation.ArgumentList.Arguments[i].Expression;
            var parameterType = visibleParameters[i].Type;
            if (parameterType is not null &&
                argumentExpression is FunctionExpressionSyntax functionExpression &&
                TryScoreAvailableFunctionExpressionArgument(functionExpression, parameterType, ref score))
            {
                continue;
            }

            if (argumentExpression is FunctionExpressionSyntax &&
                argumentTypes[i] is null)
            {
                return false;
            }

            if (parameterType is null ||
                !TryScoreAvailableArgumentConversion(argumentTypes[i], parameterType, ref score))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryScoreAvailableExtensionReceiver(
        IMethodSymbol method,
        InvocationExpressionSyntax invocation,
        ref int score)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var receiverType = TryGetAvailableReceiverType(memberAccess.Expression) ??
                           TryGetCachedReceiverType(memberAccess.Expression);
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return false;

        var handled = false;
        if (method is PEMethodSymbol peMethod &&
            TryScoreFastMetadataArgumentConversion(receiverType, peMethod, 0, ref score, out handled))
        {
            return true;
        }

        if (method is PEMethodSymbol && handled)
            return false;

        return TryGetFastParameterType(method, 0, out var parameterType) &&
               parameterType is not null &&
               TryScoreAvailableArgumentConversion(receiverType, parameterType, ref score);
    }

    private static bool TryScoreAvailableFunctionExpressionArgument(
        FunctionExpressionSyntax functionExpression,
        ITypeSymbol parameterType,
        ref int score)
    {
        if (!TryUnwrapCallableDelegateType(parameterType, out var delegateType) ||
            delegateType.GetDelegateInvokeMethod() is not { } invokeMethod)
        {
            if (IsSystemDelegateType(parameterType))
            {
                score += 1;
                return true;
            }

            return false;
        }

        var explicitParameterCount = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple when !simple.Parameter.Identifier.IsMissing => 1,
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
            _ => 0
        };

        if (invokeMethod.Parameters.Length != explicitParameterCount)
            return false;

        score += IsExpressionTreeDelegateParameter(parameterType) ? 7 : 6;
        return true;
    }

    private static bool IsSystemDelegateType(ITypeSymbol type)
    {
        var plainType = type.GetNonNullableType();
        return string.Equals(plainType.ContainingNamespace?.ToDisplayString(), "System", StringComparison.Ordinal) &&
               (string.Equals(plainType.Name, "Delegate", StringComparison.Ordinal) ||
                string.Equals(plainType.Name, "MulticastDelegate", StringComparison.Ordinal));
    }

    private bool TryScoreFastMetadataArgumentConversion(
        ITypeSymbol? argumentType,
        PEMethodSymbol method,
        int parameterIndex,
        ref int score,
        out bool handled)
    {
        handled = false;

        if (argumentType is null ||
            argumentType.TypeKind == TypeKind.Error ||
            !TryGetSpecialTypeMetadataName(argumentType.GetNonNullableType(), out var argumentMetadataName) ||
            !method.TryGetParameterRuntimeTypeMetadataName(parameterIndex, out var parameterMetadataName) ||
            string.IsNullOrWhiteSpace(parameterMetadataName))
        {
            return false;
        }

        handled = true;
        if (string.Equals(parameterMetadataName, argumentMetadataName, StringComparison.Ordinal))
        {
            score += 8;
            return true;
        }

        if (IsImplicitSpecialTypeMetadataConversion(argumentMetadataName, parameterMetadataName))
        {
            score += 5;
            return true;
        }

        if (string.Equals(parameterMetadataName, "System.Object", StringComparison.Ordinal))
        {
            score += 4;
            return true;
        }

        return false;
    }

    private static bool TryGetSpecialTypeMetadataName(ITypeSymbol type, out string metadataName)
    {
        if (type is IArrayTypeSymbol { Rank: 1, ElementType: { } elementType } &&
            TryGetSpecialTypeMetadataName(elementType.GetNonNullableType(), out var elementMetadataName))
        {
            metadataName = elementMetadataName + "[]";
            return true;
        }

        metadataName = type.SpecialType switch
        {
            SpecialType.System_Boolean => "System.Boolean",
            SpecialType.System_Byte => "System.Byte",
            SpecialType.System_Char => "System.Char",
            SpecialType.System_Decimal => "System.Decimal",
            SpecialType.System_Double => "System.Double",
            SpecialType.System_Int16 => "System.Int16",
            SpecialType.System_Int32 => "System.Int32",
            SpecialType.System_Int64 => "System.Int64",
            SpecialType.System_Object => "System.Object",
            SpecialType.System_SByte => "System.SByte",
            SpecialType.System_Single => "System.Single",
            SpecialType.System_String => "System.String",
            SpecialType.System_UInt16 => "System.UInt16",
            SpecialType.System_UInt32 => "System.UInt32",
            SpecialType.System_UInt64 => "System.UInt64",
            _ => string.Empty
        };

        return metadataName.Length > 0;
    }

    private static bool IsImplicitSpecialTypeMetadataConversion(string sourceMetadataName, string targetMetadataName)
        => sourceMetadataName switch
        {
            "System.Byte" => targetMetadataName is "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.SByte" => targetMetadataName is "System.Int16" or "System.Int32" or "System.Int64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.Int16" => targetMetadataName is "System.Int32" or "System.Int64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.UInt16" => targetMetadataName is "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.Int32" => targetMetadataName is "System.Int64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.UInt32" => targetMetadataName is "System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.Decimal",
            "System.Int64" => targetMetadataName is "System.Single" or "System.Double" or "System.Decimal",
            "System.UInt64" => targetMetadataName is "System.Single" or "System.Double" or "System.Decimal",
            "System.Single" => targetMetadataName is "System.Double",
            _ => false
        };

    private bool TryScoreAvailableArgumentConversion(ITypeSymbol? argumentType, ITypeSymbol parameterType, ref int score)
    {
        if (argumentType is null ||
            argumentType.TypeKind == TypeKind.Error ||
            parameterType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(argumentType, parameterType) ||
            SymbolEqualityComparer.Default.Equals(argumentType.GetNonNullableType(), parameterType.GetNonNullableType()))
        {
            score += 8;
            return true;
        }

        var conversion = Compilation.ClassifyConversion(argumentType, parameterType, includeUserDefined: false);
        if (!conversion.Exists || !conversion.IsImplicit)
            return false;

        score += conversion.IsIdentity ? 8 : 4;
        return true;
    }

    private bool IsAvailableExtensionReceiverInvocation(IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        if (!method.IsExtensionMethod ||
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        return !TryResolveAvailableTypeExpression(memberAccess.Expression, out _);
    }

    private static bool TryGetFastParameterCount(IMethodSymbol method, out int count)
    {
        if (method is PEMethodSymbol peMethod)
        {
            count = peMethod.ParameterCount;
            return true;
        }

        count = method.Parameters.Length;
        return true;
    }

    private static bool TryGetFastParameterType(
        IMethodSymbol method,
        int index,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        if (method is PEMethodSymbol peMethod)
            return peMethod.TryGetParameterType(index, out type);

        if (index < 0 || index >= method.Parameters.Length)
        {
            type = null;
            return false;
        }

        type = method.Parameters[index].Type;
        return type is not null;
    }

    private static InvocationExpressionSyntax? TryGetInvocationForSymbolInfoNode(SyntaxNode node)
        => node switch
        {
            InvocationExpressionSyntax invocation => invocation,
            SimpleNameSyntax simpleName
                when simpleName.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, simpleName) => invocation,
            SimpleNameSyntax simpleName
                when simpleName.Parent is MemberAccessExpressionSyntax memberAccess &&
                     IsSameSyntaxNode(memberAccess.Name, simpleName) &&
                     memberAccess.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) => invocation,
            MemberAccessExpressionSyntax memberAccess
                when memberAccess.Parent is InvocationExpressionSyntax invocation &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) => invocation,
            _ => null
        };

    private static SimpleNameSyntax? TryGetInvokedName(SyntaxNode node, InvocationExpressionSyntax invocation)
        => node switch
        {
            SimpleNameSyntax simpleName
                when IsSameSyntaxNode(invocation.Expression, simpleName) => simpleName,
            SimpleNameSyntax simpleName
                when simpleName.Parent is MemberAccessExpressionSyntax memberAccess &&
                     IsSameSyntaxNode(memberAccess.Name, simpleName) &&
                     IsSameSyntaxNode(invocation.Expression, memberAccess) => simpleName,
            SimpleNameSyntax simpleName
                when simpleName.Parent is MemberBindingExpressionSyntax memberBinding &&
                     IsSameSyntaxNode(memberBinding.Name, simpleName) &&
                     IsSameSyntaxNode(invocation.Expression, memberBinding) => simpleName,
            _ => null
        };

    private void CacheAvailableInvocationSymbolInfo(InvocationExpressionSyntax invocation, SymbolInfo info)
        => CacheInvocationTargetSymbolInfo(invocation, info);

    private void CacheInvocationTargetSymbolInfo(InvocationExpressionSyntax invocation, SymbolInfo info)
    {
        StoreSymbolMapping(invocation, info);
        StoreSymbolMapping(invocation.Expression, info);

        switch (invocation.Expression)
        {
            case SimpleNameSyntax simpleName:
                StoreSymbolMapping(simpleName, info);
                break;
            case MemberAccessExpressionSyntax { Name: SimpleNameSyntax memberName }:
                StoreSymbolMapping(memberName, info);
                break;
            case MemberBindingExpressionSyntax { Name: SimpleNameSyntax memberName }:
                StoreSymbolMapping(memberName, info);
                break;
        }
    }

    /// <summary>
    /// Tries to retrieve invocation candidates from semantic state and declarations that are already available.
    /// This method does not bind cold bodies, create operations, or run diagnostics.
    /// </summary>
    internal bool TryGetAvailableInvocationCandidates(InvocationExpressionSyntax invocation, out ImmutableArray<IMethodSymbol> methods)
        => TryGetAvailableInvocationCandidates(invocation, allowSourceDeclarationBinding: true, out methods);

    private bool TryGetAvailableInvocationCandidates(
        InvocationExpressionSyntax invocation,
        bool allowSourceDeclarationBinding,
        out ImmutableArray<IMethodSymbol> methods)
    {
        if (TryGetAvailablePipeInvocationCandidates(invocation, out methods))
            return true;

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var seenMethods = new HashSet<string>(StringComparer.Ordinal);
        var explicitGenericName = TryGetInvocationGenericName(invocation.Expression);
        var explicitTypeArgumentsResolved = false;
        var explicitTypeArguments = ImmutableArray<ITypeSymbol>.Empty;

        bool TryGetExplicitTypeArguments(out ImmutableArray<ITypeSymbol> typeArguments)
        {
            if (!explicitTypeArgumentsResolved)
            {
                explicitTypeArguments = explicitGenericName is null
                    ? ImmutableArray<ITypeSymbol>.Empty
                    : ResolveAvailableTypeArguments(explicitGenericName.TypeArgumentList);
                explicitTypeArgumentsResolved = true;
            }

            typeArguments = explicitTypeArguments;
            return explicitGenericName is null ||
                   (!typeArguments.IsDefault &&
                    typeArguments.Length == explicitGenericName.TypeArgumentList.Arguments.Count);
        }

        bool TryNormalizeAvailableInvocationCandidate(IMethodSymbol method, out IMethodSymbol? normalized)
        {
            normalized = method;

            if (explicitGenericName is null)
                return true;

            if (method.MethodKind == MethodKind.Constructor)
            {
                if (method is SubstitutedMethodSymbol or ConstructedMethodSymbol ||
                    method.ContainingType is ConstructedNamedTypeSymbol)
                {
                    return true;
                }

                if (!TryGetExplicitTypeArguments(out var containingTypeArguments) ||
                    method.ContainingType is not INamedTypeSymbol containingType ||
                    containingType.TypeParameters.Length != containingTypeArguments.Length)
                {
                    normalized = null;
                    return false;
                }

                if (containingType.Construct(containingTypeArguments.ToArray()) is not ConstructedNamedTypeSymbol constructedType)
                {
                    normalized = null;
                    return false;
                }

                normalized = new SubstitutedMethodSymbol(method.OriginalDefinition ?? method, constructedType);
                return true;
            }

            if (!TryGetExplicitTypeArguments(out var typeArguments) ||
                method.TypeParameters.Length != typeArguments.Length)
            {
                normalized = null;
                return false;
            }

            normalized = method.Construct(typeArguments.ToArray());
            return true;
        }

        void AddIfNotPresent(IMethodSymbol? method)
        {
            if (method is null)
                return;

            if (!TryNormalizeAvailableInvocationCandidate(method, out method) || method is null)
                return;

            if (seenMethods.Add(method.GetShallowLookupIdentityKey()))
                builder.Add(method);
        }

        void AddSymbolInfoCandidates(SymbolInfo symbolInfo)
        {
            if (symbolInfo.Symbol is IMethodSymbol method)
                AddIfNotPresent(method);

            if (symbolInfo.Symbol is INamedTypeSymbol type)
                AddAvailableConstructors(type, AddIfNotPresent);

            if (!symbolInfo.CandidateSymbols.IsDefaultOrEmpty)
            {
                foreach (var candidateMethod in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
                    AddIfNotPresent(candidateMethod);

                foreach (var candidateType in symbolInfo.CandidateSymbols.OfType<INamedTypeSymbol>())
                    AddAvailableConstructors(candidateType, AddIfNotPresent);
            }
        }

        if (TryGetCachedSymbolInfo(invocation, out var cachedInvocationInfo))
        {
            AddSymbolInfoCandidates(cachedInvocationInfo);
            methods = builder.ToImmutable();
            if (methods.Length > 0)
            {
                return true;
            }
        }

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: SimpleNameSyntax memberName } memberAccess &&
            TryResolveAvailableTypeExpression(memberAccess.Expression, out var typeExpressionType) &&
            typeExpressionType is not null)
        {
            foreach (var method in GetAvailableMethodMembers(typeExpressionType, memberName.Identifier.ValueText))
                AddIfNotPresent(method);
        }

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: SimpleNameSyntax availableMemberName } availableMemberAccess &&
            TryGetAvailableSymbolInfo(availableMemberAccess.Expression, out var availableReceiverInfo))
        {
            var availableReceiverType = GetNamedTypeFromAvailableSymbol(availableReceiverInfo.Symbol)
                ?? availableReceiverInfo.CandidateSymbols.Select(GetNamedTypeFromAvailableSymbol).FirstOrDefault(static type => type is not null);
            if (availableReceiverType is not null)
            {
                foreach (var method in GetAvailableMethodMembers(availableReceiverType, availableMemberName.Identifier.ValueText))
                    AddIfNotPresent(method);
            }
        }

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: SimpleNameSyntax receiverMemberName } receiverMemberAccess &&
            TryGetAvailableTypeInfo(receiverMemberAccess.Expression, out var receiverTypeInfo) &&
            (receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType)?.GetNonNullableType() is INamedTypeSymbol receiverType)
        {
            foreach (var method in GetAvailableMethodMembers(receiverType, receiverMemberName.Identifier.ValueText))
                AddIfNotPresent(method);
        }

        methods = builder.ToImmutable();
        if (methods.Length > 0)
        {
            return true;
        }

        if (invocation.Expression is SimpleNameSyntax constructorName &&
            TryLookupVisibleValueSymbol(constructorName) is null &&
            TryResolveAvailableTypeExpression(constructorName, out var constructorType) &&
            constructorType is not null)
        {
            AddAvailableConstructors(constructorType, AddIfNotPresent);

            methods = builder.ToImmutable();
            if (methods.Length > 0)
                return true;
        }

        if (invocation.Expression is SimpleNameSyntax targetTypedCaseName &&
            TryLookupVisibleValueSymbol(targetTypedCaseName) is null &&
            TryGetAvailableTargetTypeForUnionCaseInvocation(invocation, out var targetType) &&
            targetType is not null &&
            TryGetTargetTypedUnionCaseType(targetType, targetTypedCaseName.Identifier.ValueText, out var caseType))
        {
            AddIfNotPresent(ChooseUnionCaseConstructor(caseType, invocation.ArgumentList.Arguments.Count));

            methods = builder.ToImmutable();
            if (methods.Length > 0)
                return true;
        }

        if (invocation.Expression is IdentifierNameSyntax invocationIdentifier &&
            TryLookupVisibleValueSymbol(invocationIdentifier) is null &&
            TryLookupAvailableFunctionDeclarations(
                invocationIdentifier,
                invocationIdentifier.Identifier.ValueText,
                allowSourceDeclarationBinding,
                out var availableFunctions))
        {
            foreach (var function in availableFunctions)
                AddIfNotPresent(function);

            methods = builder.ToImmutable();
            return methods.Length > 0;
        }

        if (TryGetAvailableExtensionInvocationCandidates(invocation, out var extensionCandidates))
        {
            foreach (var extensionCandidate in extensionCandidates)
                AddIfNotPresent(extensionCandidate);

            methods = builder.ToImmutable();
            return methods.Length > 0;
        }

        if (TryGetAvailableSymbolInfo(invocation, out var invocationInfo))
            AddSymbolInfoCandidates(invocationInfo);

        if (TryGetAvailableSymbolInfo(invocation.Expression, out var expressionInfo))
            AddSymbolInfoCandidates(expressionInfo);

        var invocationName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax genericName => genericName.Identifier.ValueText,
            _ => null
        };

        if (invocation.Expression is GenericNameSyntax &&
            !string.IsNullOrWhiteSpace(invocationName))
        {
            var containingType = GetBinderForIncrementalSemanticQuery(invocation).ContainingSymbol switch
            {
                INamedTypeSymbol type => type,
                IMethodSymbol method => method.ContainingType,
                _ => null
            };

            if (containingType is not null)
            {
                foreach (var method in containingType.GetMembers(invocationName).OfType<IMethodSymbol>())
                    AddIfNotPresent(method);
            }
        }

        if (TryResolveAvailableCallableExpression(invocation.Expression, out var callableSymbol))
        {
            switch (callableSymbol)
            {
                case IMethodSymbol method:
                    AddIfNotPresent(method);
                    break;
                case INamedTypeSymbol type:
                    AddAvailableConstructors(type, AddIfNotPresent);
                    break;
            }
        }

        if (builder.Count == 0 &&
            TryResolveAvailableTypeExpression(invocation.Expression, out var constructorExpressionType) &&
            constructorExpressionType?.GetNonNullableType() is INamedTypeSymbol expressionConstructorType)
        {
            AddAvailableConstructors(expressionConstructorType, AddIfNotPresent);
        }

        if (builder.Count == 0 &&
            TryGetAvailableTypeInfo(invocation.Expression, out var typeInfo) &&
            (typeInfo.Type ?? typeInfo.ConvertedType)?.GetNonNullableType() is INamedTypeSymbol expressionType)
        {
            if (expressionType.GetDelegateInvokeMethod() is { } invokeMethod)
                AddIfNotPresent(invokeMethod);

            AddInvokeCandidatesFromType(expressionType, AddIfNotPresent);
        }

        methods = builder.ToImmutable();
        return methods.Length > 0;
    }

    internal bool TryGetAvailableExtensionInvocationCandidates(InvocationExpressionSyntax invocation, out ImmutableArray<IMethodSymbol> methods)
    {
        methods = ImmutableArray<IMethodSymbol>.Empty;

        if (invocation.Expression is not MemberAccessExpressionSyntax { Name: SimpleNameSyntax memberName } memberAccess)
            return false;

        var binder = GetBinderForIncrementalSemanticQuery(GetExtensionMemberLookupContext(invocation));
        var extensionName = memberName.Identifier.ValueText;
        var receiverType = TryGetAvailableReceiverType(memberAccess.Expression) ??
                           TryGetCachedReceiverType(memberAccess.Expression);
        var hasReceiverType = receiverType is not null && receiverType.TypeKind != TypeKind.Error;

        if (!hasReceiverType)
            return false;

        using var sourceNamespaceLookupSuppression = Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();
        var hasCachedExtensions = ExtensionMemberLookup.TryGetCached(
                binder,
                receiverType!,
                out var cachedExtensions,
                extensionName,
                includePartialMatches: false,
                kinds: ExtensionMemberKinds.InstanceMethods);
        var usedCachedExtensions = hasCachedExtensions && !cachedExtensions.IsEmpty;
        var extensions = usedCachedExtensions
            ? cachedExtensions
            : ExtensionMemberLookupResult.Empty;

        methods = BuildAvailableExtensionMethods(extensions.InstanceMethods);

        if (methods.IsDefaultOrEmpty)
        {
            extensions = ExtensionMemberLookup.Lookup(
                binder,
                receiverType!,
                extensionName,
                includePartialMatches: false,
                kinds: ExtensionMemberKinds.InstanceMethods);
            methods = BuildAvailableExtensionMethods(extensions.InstanceMethods);
        }

        if (methods.IsDefaultOrEmpty)
            methods = BuildAvailableExtensionMethods(binder.LookupExtensionMethodsByName(extensionName));

        if (methods.IsDefaultOrEmpty)
            return false;

        if (hasReceiverType)
        {
            methods = methods
                .OrderByDescending(method =>
                    TryGetFastParameterType(method, 0, out var parameterType) &&
                    SymbolEqualityComparer.Default.Equals(parameterType, receiverType))
                .ThenByDescending(method =>
                    TryGetFastParameterType(method, 0, out var parameterType) &&
                    parameterType is not null &&
                    parameterType.TypeKind != TypeKind.Error &&
                    Compilation.ClassifyConversion(receiverType!, parameterType, includeUserDefined: false).Exists)
                .ToImmutableArray();
        }

        return methods.Length > 0;

        ImmutableArray<IMethodSymbol> BuildAvailableExtensionMethods(IEnumerable<IMethodSymbol> candidateMethods)
            => candidateMethods
                .Where(method => string.Equals(method.Name, extensionName, StringComparison.Ordinal))
                .Where(method => TryGetFastParameterCount(method, out var parameterCount) &&
                                 parameterCount >= invocation.ArgumentList.Arguments.Count + 1)
                .Select(method => ConstructAvailableExtensionCandidate(method, receiverType!, invocation))
                .Select(MaterializeAvailableInvocationCandidate)
                .Where(method => IsAvailableExtensionReceiverCandidate(method, receiverType!))
                .ToImmutableArray();
    }

    private static IMethodSymbol MaterializeAvailableInvocationCandidate(IMethodSymbol method)
    {
        _ = method.ReturnType;

        if (TryGetFastParameterCount(method, out var parameterCount))
        {
            for (var i = 0; i < parameterCount; i++)
                _ = TryGetFastParameterType(method, i, out _);
        }

        return method;
    }

    private bool IsAvailableExtensionReceiverCandidate(IMethodSymbol method, ITypeSymbol receiverType)
    {
        if (receiverType.TypeKind == TypeKind.Error)
            return false;

        if (!TryGetFastParameterType(method, 0, out var parameterType) ||
            parameterType is null ||
            parameterType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(parameterType, receiverType) ||
               Compilation.ClassifyConversion(receiverType, parameterType, includeUserDefined: false).Exists;
    }

    private IMethodSymbol ConstructAvailableExtensionCandidate(
        IMethodSymbol method,
        ITypeSymbol receiverType,
        InvocationExpressionSyntax? invocation = null)
    {
        if (receiverType.TypeKind == TypeKind.Error ||
            !TryGetFastParameterCount(method, out var parameterCount) ||
            parameterCount == 0 ||
            method.TypeParameters.IsDefaultOrEmpty)
        {
            return method;
        }

        if (!TryGetFastParameterType(method, 0, out var receiverParameterType))
            return method;

        if (receiverParameterType is null ||
            receiverParameterType.TypeKind == TypeKind.Error ||
            !TryInferExtensionReceiverSubstitutions(receiverParameterType, receiverType, out var substitutions))
        {
            return method;
        }

        if (invocation is not null)
            TryInferFunctionArgumentSubstitutions(method, invocation, parameterOffset: 1, substitutions);

        if (substitutions.Count == 0)
            return method;

        var typeArguments = new ITypeSymbol[method.TypeParameters.Length];
        var changed = false;

        for (var i = 0; i < method.TypeParameters.Length; i++)
        {
            var typeParameter = method.TypeParameters[i];
            var existingArgument = method.TypeArguments.Length == method.TypeParameters.Length
                ? method.TypeArguments[i]
                : typeParameter;

            if (TryGetInferredTypeArgument(typeParameter, substitutions, out var inferred) &&
                inferred.TypeKind != TypeKind.Error)
            {
                typeArguments[i] = inferred;
                changed |= !SymbolEqualityComparer.Default.Equals(existingArgument, inferred);
            }
            else
            {
                typeArguments[i] = existingArgument;
            }
        }

        return changed ? method.Construct(typeArguments) : method;
    }

    private static bool TryGetInferredTypeArgument(
        ITypeParameterSymbol typeParameter,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? inferred)
    {
        if (substitutions.TryGetValue(typeParameter, out inferred))
            return true;

        return TypeSubstitution.TryGetEquivalentTypeParameterSubstitution(typeParameter, substitutions, out inferred);
    }

    private void TryInferFunctionArgumentSubstitutions(
        IMethodSymbol method,
        InvocationExpressionSyntax invocation,
        int parameterOffset,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (!TryGetFastParameterCount(method, out var parameterCount))
            return;

        for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
        {
            var parameterIndex = argumentIndex + parameterOffset;
            if (parameterIndex < 0 ||
                parameterIndex >= parameterCount ||
                !TryGetFastParameterType(method, parameterIndex, out var rawParameterType) ||
                rawParameterType is null)
            {
                continue;
            }

            var argument = invocation.ArgumentList.Arguments[argumentIndex];
            var parameterType = SubstituteTypeParameters(rawParameterType, substitutions);
            if (!TryUnwrapCallableDelegateType(parameterType, out var delegateType) ||
                delegateType.GetDelegateInvokeMethod() is not { } invokeMethod)
            {
                continue;
            }

            if (argument.Expression is not FunctionExpressionSyntax functionExpression)
            {
                if (TryGetAvailableMethodGroupReturnType(argument.Expression, invokeMethod, out var methodGroupReturnType) &&
                    methodGroupReturnType is not null &&
                    methodGroupReturnType.TypeKind != TypeKind.Error)
                {
                    _ = TryUnifyExtensionReceiver(invokeMethod.ReturnType, methodGroupReturnType, substitutions);
                }

                continue;
            }

            if (!TryGetAvailableFunctionExpressionReturnType(
                    functionExpression,
                    invokeMethod,
                    out var returnType) ||
                returnType is null ||
                returnType.TypeKind == TypeKind.Error)
            {
                continue;
            }

            _ = TryUnifyExtensionReceiver(invokeMethod.ReturnType, returnType, substitutions);
        }
    }

    private bool TryGetAvailableMethodGroupReturnType(
        ExpressionSyntax expression,
        IMethodSymbol delegateInvokeMethod,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? returnType)
    {
        returnType = null;

        ImmutableArray<IMethodSymbol> methods;
        if (expression is IdentifierNameSyntax identifier &&
            TryLookupAvailableFunctionDeclarations(
                identifier,
                identifier.Identifier.ValueText,
                allowSourceDeclarationBinding: false,
                out var availableMethods))
        {
            methods = availableMethods;
        }
        else if (expression is IdentifierNameSyntax sourceIdentifier &&
                 TryLookupAvailableFunctionDeclarations(
                     sourceIdentifier,
                     sourceIdentifier.Identifier.ValueText,
                     allowSourceDeclarationBinding: true,
                     out var declaredSourceMethods))
        {
            methods = declaredSourceMethods;
        }
        else if (TryResolveAvailableCallableExpression(expression, out var callableSymbol) &&
                 callableSymbol is IMethodSymbol method)
        {
            methods = ImmutableArray.Create(method);
        }
        else
        {
            return false;
        }

        foreach (var method in methods)
        {
            if (method.Parameters.Length != delegateInvokeMethod.Parameters.Length)
                continue;

            var compatible = true;
            for (var i = 0; i < delegateInvokeMethod.Parameters.Length; i++)
            {
                var delegateParameterType = delegateInvokeMethod.Parameters[i].Type;
                var methodParameterType = method.Parameters[i].Type;
                if (delegateParameterType is null ||
                    methodParameterType is null ||
                    delegateParameterType.TypeKind == TypeKind.Error ||
                    methodParameterType.TypeKind == TypeKind.Error)
                {
                    compatible = false;
                    break;
                }

                if (!SymbolEqualityComparer.Default.Equals(delegateParameterType, methodParameterType) &&
                    !Compilation.ClassifyConversion(delegateParameterType, methodParameterType, includeUserDefined: false).Exists)
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
                continue;

            var candidateReturnType = method.ReturnType;
            if (candidateReturnType is null || candidateReturnType.TypeKind == TypeKind.Error)
                continue;

            returnType = candidateReturnType;
            return true;
        }

        return false;
    }

    private bool TryGetAvailableFunctionExpressionReturnType(
        FunctionExpressionSyntax functionExpression,
        IMethodSymbol delegateInvokeMethod,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? returnType)
    {
        returnType = null;

        var parameters = GetFunctionExpressionParameters(functionExpression).ToImmutableArray();
        if (parameters.Length != delegateInvokeMethod.Parameters.Length)
            return false;

        var parameterTypes = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        for (var i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(name) || name == "_")
                continue;

            var parameterType = delegateInvokeMethod.Parameters[i].Type;
            if (parameterType is null || parameterType.TypeKind == TypeKind.Error)
                return false;

            parameterTypes[name] = parameterType;
        }

        if (functionExpression.ExpressionBody?.Expression is not { } bodyExpression)
            return false;

        return TryGetAvailableExpressionType(bodyExpression, parameterTypes, out returnType);
    }

    private bool TryGetAvailableExpressionType(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, ITypeSymbol> parameterTypes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        if (expression is IdentifierNameSyntax identifier &&
            parameterTypes.TryGetValue(identifier.Identifier.ValueText, out var parameterType))
        {
            type = parameterType;
            return true;
        }

        if (expression is MemberAccessExpressionSyntax memberAccess &&
            TryGetAvailableExpressionType(memberAccess.Expression, parameterTypes, out var receiverType))
        {
            var memberName = memberAccess.Name.Identifier.ValueText;
            if (TryGetAvailableMemberValueType(receiverType, memberName, out type))
                return true;
        }

        if (expression is InvocationExpressionSyntax invocation &&
            TryGetAvailableInvocationExpressionType(invocation, parameterTypes, out type))
        {
            return true;
        }

        if (expression is InfixOperatorExpressionSyntax infixExpression &&
            TryGetAvailableExpressionType(infixExpression.Left, parameterTypes, out var leftType) &&
            TryGetAvailableExpressionType(infixExpression.Right, parameterTypes, out var rightType) &&
            BoundBinaryOperator.TryLookup(Compilation, infixExpression.OperatorToken.Kind, leftType, rightType, out var op) &&
            op.ResultType.TypeKind != TypeKind.Error)
        {
            type = op.ResultType;
            return true;
        }

        if (TryGetAvailableLiteralType(expression, out var literalType) &&
            literalType is not null &&
            literalType.TypeKind != TypeKind.Error)
        {
            type = literalType;
            return true;
        }

        if (expression is IdentifierNameSyntax enclosingParameterIdentifier &&
            TryGetEnclosingParameterTypeFromSyntax(enclosingParameterIdentifier, out var enclosingParameterType, allowCandidateLookup: false))
        {
            type = enclosingParameterType;
            return true;
        }

        if (expression is IdentifierNameSyntax localIdentifier &&
            TryResolveAvailableLocalReferenceFromSyntax(localIdentifier, out var localSymbol) &&
            localSymbol.Type is { TypeKind: not TypeKind.Error } localType)
        {
            type = localType;
            return true;
        }

        return false;
    }

    private bool TryGetAvailableMemberValueType(
        ITypeSymbol receiverType,
        string memberName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;
        if (receiverType.GetNonNullableType() is not INamedTypeSymbol namedReceiver)
            return false;

        var member = LookupAvailableMember(namedReceiver, memberName);
        type = GetTypeFromSymbol(member);
        if ((type is null || type.TypeKind == TypeKind.Error) &&
            TryEnsureSourceTypeValueMemberSignatureDeclared(namedReceiver, memberName, out _, out var valueMembers))
        {
            member = valueMembers.FirstOrDefault();
            type = GetTypeFromSymbol(member);
        }

        return type is not null && type.TypeKind != TypeKind.Error;
    }

    private bool TryGetAvailableInvocationExpressionType(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, ITypeSymbol> parameterTypes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
            !TryGetAvailableExpressionType(memberAccess.Expression, parameterTypes, out var receiverType))
        {
            return false;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        var candidates = receiverType.GetNonNullableType()
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == invocation.ArgumentList.Arguments.Count)
            .ToImmutableArray();
        if (candidates.IsDefaultOrEmpty)
            return false;

        foreach (var candidate in candidates)
        {
            var compatible = true;
            for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
            {
                if (!TryGetAvailableExpressionType(invocation.ArgumentList.Arguments[i].Expression, parameterTypes, out var argumentType))
                {
                    compatible = false;
                    break;
                }

                var parameterType = candidate.Parameters[i].Type;
                if (parameterType is null ||
                    parameterType.TypeKind == TypeKind.Error ||
                    (!SymbolEqualityComparer.Default.Equals(argumentType, parameterType) &&
                     !Compilation.ClassifyConversion(argumentType, parameterType, includeUserDefined: false).Exists))
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
                continue;

            type = candidate.ReturnType;
            return type is not null && type.TypeKind != TypeKind.Error;
        }

        return false;
    }

    private SyntaxNode GetExtensionMemberLookupContext(SyntaxNode contextNode)
    {
        foreach (var ancestor in contextNode.AncestorsAndSelf())
        {
            if (ancestor is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                return ancestor;
        }

        return SyntaxTree.GetRoot();
    }

    private ITypeSymbol? TryGetCachedReceiverType(ExpressionSyntax receiver)
    {
        if (TryGetCachedTypeInfo(receiver, out var receiverTypeInfo) &&
            HasTypeInfo(receiverTypeInfo))
        {
            var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
            if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                return receiverType;
        }

        if (TryGetCachedBoundNode(receiver) is BoundExpression cachedExpression)
        {
            var receiverType = cachedExpression.Type ?? cachedExpression.GetConvertedType();
            if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                return receiverType;
        }

        if (TryGetCachedSymbolInfo(receiver, out var receiverInfo))
            return GetTypeFromSymbol(receiverInfo.Symbol?.UnderlyingSymbol ?? receiverInfo.Symbol);

        return null;
    }

    private bool TryGetPatternConstantValueSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (node is not IdentifierNameSyntax identifier)
            return false;

        var patternSyntax = (PatternSyntax?)identifier
            .AncestorsAndSelf()
            .OfType<DeclarationPatternSyntax>()
            .FirstOrDefault(pattern =>
                IsUndesignatedDeclarationPattern(pattern) &&
                pattern.Type.FullSpan.Contains(identifier.Span) &&
                IsTerminalTypeNameIdentifier(pattern.Type, identifier));

        patternSyntax ??= identifier
            .AncestorsAndSelf()
            .OfType<ConstantPatternSyntax>()
            .FirstOrDefault(pattern =>
                pattern.Expression is TypeSyntax typeSyntax &&
                typeSyntax.FullSpan.Contains(identifier.Span) &&
                IsTerminalTypeNameIdentifier(typeSyntax, identifier));

        if (patternSyntax is null)
            return false;

        BindPatternOwner(patternSyntax);

        if (TryGetCachedBoundNode(patternSyntax) is not BoundConstantPattern { Expression: { } valueExpression })
            return false;

        info = valueExpression.GetSymbolInfo();
        if (info.Symbol is null && info.CandidateSymbols.IsDefaultOrEmpty)
            return false;

        info = ProjectBackingFieldSymbolsToAssociatedProperty(node, info);
        return info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;

        static bool IsUndesignatedDeclarationPattern(DeclarationPatternSyntax pattern)
            => pattern.Designation is null
               || pattern.Designation is SingleVariableDesignationSyntax { Identifier.IsMissing: true }
               || pattern.Designation is SingleVariableDesignationSyntax { Identifier.Kind: SyntaxKind.None };

        static bool IsTerminalTypeNameIdentifier(TypeSyntax typeSyntax, IdentifierNameSyntax identifier)
        {
            var terminalName = GetTerminalTypeName(typeSyntax);
            return terminalName is IdentifierNameSyntax terminalIdentifier &&
                   terminalIdentifier.Kind == identifier.Kind &&
                   terminalIdentifier.Span == identifier.Span;
        }

        static SimpleNameSyntax? GetTerminalTypeName(TypeSyntax typeSyntax)
        {
            while (typeSyntax is QualifiedNameSyntax qualified)
                typeSyntax = (TypeSyntax)qualified.Right;

            return typeSyntax as SimpleNameSyntax;
        }
    }

    private void BindPatternOwner(PatternSyntax pattern)
    {
        var owner = pattern.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is IsPatternExpressionSyntax or
                IfPatternStatementSyntax or
                IfPatternExpressionSyntax or
                WhilePatternStatementSyntax or
                MatchExpressionSyntax or
                PostfixMatchExpressionSyntax or
                MatchStatementSyntax or
                ForStatementSyntax or
                PatternDeclarationAssignmentStatementSyntax or
                CatchClauseSyntax);

        if (owner is not null)
            _ = GetBoundNode(owner);
    }

    internal bool TryGetAvailablePipeInvocationCandidates(InvocationExpressionSyntax invocation, out ImmutableArray<IMethodSymbol> methods)
    {
        methods = ImmutableArray<IMethodSymbol>.Empty;

        if (invocation.Parent is not InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression ||
            !IsSameSyntaxNode(pipeExpression.Right, invocation))
        {
            return false;
        }

        var methodName = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax identifier } => identifier.Identifier.ValueText,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(methodName))
            return false;

        if (!TryGetAvailableTypeInfo(pipeExpression.Left, out var receiverTypeInfo))
            return false;

        var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return false;

        using var sourceNamespaceLookupSuppression = Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();
        var extensions = LookupApplicableExtensionMembers(
            receiverType,
            invocation,
            methodName,
            kinds: ExtensionMemberKinds.InstanceMethods | ExtensionMemberKinds.StaticMethods);
        var candidates = extensions.InstanceMethods
            .Concat(extensions.StaticMethods)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .Where(method => method.Parameters.Length == invocation.ArgumentList.Arguments.Count + 1)
            .Select(method => ConstructAvailableExtensionCandidate(method, receiverType, invocation))
            .Select(method => (Method: method, Compatibility: GetPipeInvocationArgumentCompatibility(method, invocation)))
            .Where(candidate => candidate.Compatibility >= 0)
            .ToImmutableArray();

        if (candidates.IsDefaultOrEmpty)
            return false;

        methods = candidates
            .OrderByDescending(candidate => candidate.Compatibility)
            .ThenByDescending(candidate => SymbolEqualityComparer.Default.Equals(candidate.Method.Parameters[0].Type, receiverType))
            .ThenByDescending(candidate => candidate.Method.Parameters[0].Type.TypeKind != TypeKind.Error &&
                                           Compilation.ClassifyConversion(receiverType, candidate.Method.Parameters[0].Type, includeUserDefined: false).Exists)
            .ThenByDescending(static candidate => candidate.Method.Parameters.Length > 1 &&
                                                  IsExpressionTreeDelegateParameter(candidate.Method.Parameters[1].Type))
            .Select(static candidate => candidate.Method)
            .ToImmutableArray();

        return methods.Length > 0;
    }

    private int GetPipeInvocationArgumentCompatibility(IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        var score = 0;
        for (var argumentIndex = 0; argumentIndex < invocation.ArgumentList.Arguments.Count; argumentIndex++)
        {
            var parameterIndex = argumentIndex + 1;
            if (parameterIndex >= method.Parameters.Length)
                return -1;

            var parameterType = method.Parameters[parameterIndex].Type;
            if (parameterType is null || parameterType.TypeKind == TypeKind.Error)
                continue;

            var argumentExpression = invocation.ArgumentList.Arguments[argumentIndex].Expression;
            if (argumentExpression is FunctionExpressionSyntax functionExpression &&
                TryUnwrapCallableDelegateType(parameterType, out var delegateType) &&
                delegateType.GetDelegateInvokeMethod() is { } invokeMethod)
            {
                var explicitParameterCount = functionExpression switch
                {
                    SimpleFunctionExpressionSyntax simple when !simple.Parameter.Identifier.IsMissing => 1,
                    ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
                    _ => 0
                };

                if (invokeMethod.Parameters.Length != explicitParameterCount)
                    return -1;

                score += IsExpressionTreeDelegateParameter(parameterType) ? 2 : 1;
            }
            else if (TryGetNonContextualAvailableArgumentType(argumentExpression) is { TypeKind: not TypeKind.Error } argumentType)
            {
                if (SymbolEqualityComparer.Default.Equals(argumentType, parameterType))
                {
                    score += 4;
                    continue;
                }

                if (Compilation.ClassifyConversion(argumentType, parameterType, includeUserDefined: false).Exists)
                {
                    score += 3;
                    continue;
                }

                return -1;
            }
        }

        return score;
    }

    private ITypeSymbol? TryGetNonContextualAvailableArgumentType(ExpressionSyntax expression)
    {
        if (TryGetCachedBoundNode(expression) is BoundExpression cachedExpression &&
            !IsLikelyStaleFunctionBodyNode(cachedExpression))
        {
            var type = cachedExpression.Type ?? cachedExpression.GetConvertedType();
            if (type is not null && type.TypeKind != TypeKind.Error)
                return type;
        }

        if (TryGetNodeInterestSymbolType(expression, out var symbolType) &&
            symbolType is not null &&
            symbolType.TypeKind != TypeKind.Error)
        {
            return symbolType;
        }

        if (expression is IdentifierNameSyntax &&
            TryGetAvailableTypeInfo(expression, out var availableIdentifierTypeInfo) &&
            (availableIdentifierTypeInfo.Type ?? availableIdentifierTypeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } availableSymbolType)
        {
            return availableSymbolType;
        }

        if (TryGetAvailableLiteralType(expression, out var literalType) &&
            literalType is not null &&
            literalType.TypeKind != TypeKind.Error)
        {
            return literalType;
        }

        if (expression is WithExpressionSyntax withExpression &&
            TryGetWithReceiverType(withExpression.Expression, out var withReceiverType) &&
            withReceiverType is not null &&
            withReceiverType.TypeKind != TypeKind.Error)
        {
            return withReceiverType;
        }

        if (expression is InvocationExpressionSyntax invocation &&
            TryGetAvailableInvocationCandidates(invocation, allowSourceDeclarationBinding: false, out var invocationCandidates) &&
            TryGetCommonAvailableInvocationReturnType(invocationCandidates, out var invocationReturnType) &&
            invocationReturnType is not null &&
            invocationReturnType.TypeKind != TypeKind.Error)
        {
            return invocationReturnType;
        }

        if (expression is InvocationExpressionSyntax symbolInvocation &&
            TryGetAvailableInvocationSymbolInfo(symbolInvocation, out var invocationInfo) &&
            invocationInfo.Symbol is IMethodSymbol selectedMethod &&
            GetInvocationReturnType(selectedMethod) is { TypeKind: not TypeKind.Error } selectedReturnType)
        {
            return selectedReturnType;
        }

        return null;
    }

    private bool TryResolveAvailableCallableExpression(ExpressionSyntax expression, out ISymbol? symbol)
    {
        symbol = null;

        if (expression is IdentifierNameSyntax identifier)
        {
            if (TryLookupVisibleValueSymbol(identifier) is { } visibleSymbol)
            {
                symbol = visibleSymbol;
                return true;
            }

            if (TryLookupAvailableFunctionDeclaration(identifier, identifier.Identifier.ValueText, out var method))
            {
                symbol = method;
                return true;
            }

            if (TryLookupAvailableNamedType(identifier.Identifier.ValueText, 0, out var namedType))
            {
                symbol = namedType;
                return true;
            }
        }
        else if (expression is GenericNameSyntax genericName)
        {
            var typeArguments = ResolveAvailableTypeArguments(genericName.TypeArgumentList);
            if (typeArguments.IsDefault)
                return false;

            if (TryLookupAvailableNamedType(genericName.Identifier.ValueText, typeArguments.Length, out var genericType))
            {
                symbol = genericType.TypeParameters.Length == typeArguments.Length
                    ? genericType.Construct(typeArguments.ToArray()) as INamedTypeSymbol
                    : genericType;
                return symbol is not null;
            }
        }

        return false;
    }

    private bool TryLookupAvailableFunctionDeclaration(
        SyntaxNode contextNode,
        string name,
        out IMethodSymbol? method)
    {
        method = TryLookupAvailableFunctionDeclarations(
                contextNode,
                name,
                allowSourceDeclarationBinding: false,
                out var methods)
            ? methods[0]
            : null;
        return method is not null;
    }

    private bool TryLookupAvailableFunctionDeclarations(
        SyntaxNode contextNode,
        string name,
        bool allowSourceDeclarationBinding,
        out ImmutableArray<IMethodSymbol> methods)
    {
        methods = ImmutableArray<IMethodSymbol>.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!Compilation.SourceDeclarationsDeclared && allowSourceDeclarationBinding)
            Compilation.EnsureSourceDeclarationsDeclared();

        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
        var seenFunctions = new HashSet<SyntaxNode>();
        var seenMethods = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        void AddMethod(IMethodSymbol method, bool allowLexicallyScopedFunction = false)
        {
            if (!allowLexicallyScopedFunction && IsLexicallyScopedFunction(method))
                return;

            if (seenMethods.Add(method))
                builder.Add(method);
        }

        void AddFunction(FunctionStatementSyntax function)
        {
            if (!string.Equals(function.Identifier.ValueText, name, StringComparison.Ordinal) ||
                !seenFunctions.Add(function))
            {
                return;
            }

            if (TryGetNamespaceFunctionSymbol(function, out var method))
                AddMethod(method, allowLexicallyScopedFunction: true);
        }

        void AddNamespaceFunctionMembers()
        {
            if (GetBinderForIncrementalSemanticQuery(contextNode).CurrentNamespace is not { } currentNamespace)
                return;

            if (!Compilation.SourceDeclarationsDeclared)
            {
                var declarations = Compilation.GetNamespaceFunctionDeclarations(currentNamespace, name);
                foreach (var function in declarations)
                    AddFunction(function);

                return;
            }

            foreach (var method in Compilation.GetNamespaceMembers(
                         currentNamespace,
                         name,
                         Compilation.Options.AllowNamespaceMemberImports).OfType<IMethodSymbol>())
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    AddMethod(method);
            }
        }

        void AddImportedFunctionMembers()
        {
            using var sourceNamespaceLookupSuppression = Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();

            for (var binder = GetBinderForIncrementalSemanticQuery(contextNode); binder is not null; binder = binder.ParentBinder)
            {
                if (binder is not ImportBinder importBinder)
                    continue;

                foreach (var method in importBinder.LookupSymbols(name).OfType<IMethodSymbol>())
                {
                    if (string.Equals(method.Name, name, StringComparison.Ordinal))
                        AddMethod(method);
                }
            }
        }

        for (SyntaxNode? current = contextNode; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BlockStatementSyntax block:
                    foreach (var function in block.Statements.OfType<FunctionStatementSyntax>())
                        AddFunction(function);
                    break;

                case BlockSyntax block:
                    foreach (var function in block.Statements.OfType<FunctionStatementSyntax>())
                        AddFunction(function);
                    break;

                case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    foreach (var global in fileScopedNamespace.Members.OfType<GlobalStatementSyntax>())
                    {
                        if (Compilation.IsTopLevelFunctionMember(global) &&
                            global.Statement is FunctionStatementSyntax function)
                            AddFunction(function);
                    }
                    break;

                case CompilationUnitSyntax compilationUnit:
                    foreach (var global in compilationUnit.Members.OfType<GlobalStatementSyntax>())
                    {
                        if (Compilation.IsTopLevelFunctionMember(global) &&
                            global.Statement is FunctionStatementSyntax function)
                            AddFunction(function);
                    }
                    break;
            }
        }

        AddNamespaceFunctionMembers();
        AddImportedFunctionMembers();

        methods = builder.ToImmutable();

        return methods.Length > 0;

        static bool IsLexicallyScopedFunction(IMethodSymbol method)
            => method.DeclaringSyntaxReferences.Any(static reference =>
                reference.GetSyntax() is FunctionStatementSyntax function &&
                !IsTopLevelFunctionMember(function));

        bool TryGetNamespaceFunctionSymbol(
            FunctionStatementSyntax function,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? method)
        {
            method = null;

            var model = ReferenceEquals(function.SyntaxTree, SyntaxTree)
                ? this
                : Compilation.TryGetSemanticModelForDeclarationBinding(function.SyntaxTree, out var declarationModel)
                    ? declarationModel
                    : null;

            if (model is null)
                return false;

            if (!model.TryGetAvailableNamespaceFunctionSymbol(function, out var functionMethod) &&
                !model.TryResolveAvailableFunctionStatementSymbol(function, out functionMethod))
            {
                return false;
            }

            method = functionMethod;
            return true;
        }
    }

    private bool TryLookupAvailableNamedType(string name, int arity, out INamedTypeSymbol? type)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (Compilation.TryGetDeclaredTypeSymbol(name, arity, out var declaredType))
        {
            type = declaredType;
            return true;
        }

        var root = SyntaxTree.GetRoot();
        type = root
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(declaration => string.Equals(declaration.Identifier.ValueText, name, StringComparison.Ordinal))
            .Select(declaration => TryDeclareAvailableSourceTypeSymbol(declaration, out var sourceType)
                ? sourceType
                : GetDeclaredSymbol(declaration) as INamedTypeSymbol)
            .FirstOrDefault(candidate => candidate is not null && (candidate.Arity == arity || candidate.TypeParameters.Length == arity));
        if (type is not null)
            return true;

        type = root
            .DescendantNodes()
            .OfType<UnionDeclarationSyntax>()
            .Where(declaration => string.Equals(declaration.Identifier.ValueText, name, StringComparison.Ordinal))
            .Select(declaration => TryDeclareAvailableSourceTypeSymbol(declaration, out var sourceType)
                ? sourceType
                : GetDeclaredSymbol(declaration) as INamedTypeSymbol)
            .FirstOrDefault(candidate => candidate is not null && (candidate.Arity == arity || candidate.TypeParameters.Length == arity));
        if (type is not null)
            return true;

        if (Compilation.TryGetDeclaredTypeDeclaration(name, arity, out var sourceTypeDeclaration) &&
            Compilation.TryGetSemanticModelForDeclarationBinding(sourceTypeDeclaration.SyntaxTree, out var sourceTypeModel))
        {
            var sourceType = sourceTypeModel.TryDeclareAvailableSourceTypeSymbol(sourceTypeDeclaration, out var declaredSourceType)
                ? declaredSourceType
                : sourceTypeModel.GetDeclaredTypeSymbolForDeclaration(sourceTypeDeclaration);
            if (sourceType.Arity == arity ||
                (!sourceType.TypeParameters.IsDefault && sourceType.TypeParameters.Length == arity))
            {
                type = sourceType;
                return true;
            }
        }

        if (TryLookupImportedMetadataTypeFromSyntax(root, name, arity, out type))
            return true;

        if (!Compilation.SourceDeclarationsDeclared)
            return false;

        type = Compilation.GlobalNamespace
            .GetMembers(name)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(candidate => candidate.Arity == arity || candidate.TypeParameters.Length == arity);
        if (type is not null)
            return true;

        type = Compilation.SymbolLookup.GetTypeBySimpleName(name, arity);
        return type is not null;
    }

    private bool TryLookupImportedMetadataTypeFromSyntax(
        SyntaxNode context,
        string name,
        int arity,
        out INamedTypeSymbol? type)
    {
        var metadataTypeName = GetMetadataTypeName(name, arity);
        foreach (var importDirective in EnumerateImportDirectives(context))
        {
            var importName = importDirective.Name.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(importName))
                continue;

            if (importName.EndsWith(".*", StringComparison.Ordinal))
            {
                var namespaceName = importName[..^2];
                if (string.IsNullOrWhiteSpace(namespaceName))
                    continue;

                type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(namespaceName + "." + metadataTypeName);
                if (type is not null)
                    return true;

                continue;
            }

            var importedTypeName = GetImportedMetadataTypeName(importDirective.Name, arity);
            var simpleImportedName = GetImportedSimpleTypeName(importDirective.Name);
            if (!string.Equals(simpleImportedName, name, StringComparison.Ordinal))
                continue;

            type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(importedTypeName);
            if (type is not null)
                return true;
        }

        type = null;
        return false;

        static string GetImportedSimpleTypeName(NameSyntax importNameSyntax)
            => importNameSyntax switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                QualifiedNameSyntax qualified => GetImportedSimpleTypeName(qualified.Right),
                _ => importNameSyntax.ToString()
            };

        static string GetImportedMetadataTypeName(NameSyntax importNameSyntax, int requestedArity)
        {
            var text = importNameSyntax.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
            return importNameSyntax switch
            {
                GenericNameSyntax generic => GetMetadataTypeName(generic.Identifier.ValueText, GetImportGenericArity(generic, requestedArity)),
                QualifiedNameSyntax { Right: GenericNameSyntax generic } qualified =>
                    qualified.Left + "." + GetMetadataTypeName(generic.Identifier.ValueText, GetImportGenericArity(generic, requestedArity)),
                _ => requestedArity > 0 ? text + "`" + requestedArity : text
            };
        }

        static int GetImportGenericArity(GenericNameSyntax generic, int requestedArity)
        {
            var argumentCount = generic.TypeArgumentList.Arguments.Count;
            if (argumentCount > 0)
                return argumentCount;

            return Math.Max(requestedArity, generic.TypeArgumentList.Arguments.SeparatorCount + 1);
        }
    }

    private ImmutableArray<ITypeSymbol> ResolveAvailableTypeArguments(TypeArgumentListSyntax typeArgumentList)
    {
        var builder = ImmutableArray.CreateBuilder<ITypeSymbol>(typeArgumentList.Arguments.Count);
        foreach (var argument in typeArgumentList.Arguments)
        {
            if (!TryResolveAvailableTypeSyntax(argument.Type, out var type))
                return default;

            builder.Add(type);
        }

        return builder.ToImmutable();
    }

    private bool TryResolveAvailableTypeSyntax(TypeSyntax typeSyntax, out ITypeSymbol type)
    {
        if (TryGetAvailableTypeInfo(typeSyntax, out var typeInfo) &&
            (typeInfo.Type ?? typeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } availableType)
        {
            type = availableType;
            return true;
        }

        if (TryGetSpecialType(typeSyntax.ToString(), out type))
            return true;

        if (typeSyntax is IdentifierNameSyntax identifier &&
            TryLookupAvailableNamedType(identifier.Identifier.ValueText, 0, out var namedType))
        {
            type = namedType;
            return true;
        }

        type = Compilation.ErrorTypeSymbol;
        return false;
    }

    private bool TryGetSpecialType(string typeName, out ITypeSymbol type)
    {
        type = typeName switch
        {
            "bool" => Compilation.GetSpecialType(SpecialType.System_Boolean),
            "double" => Compilation.GetSpecialType(SpecialType.System_Double),
            "float" => Compilation.GetSpecialType(SpecialType.System_Single),
            "int" => Compilation.GetSpecialType(SpecialType.System_Int32),
            "nint" => Compilation.GetSpecialType(SpecialType.System_IntPtr),
            "nuint" => Compilation.GetSpecialType(SpecialType.System_UIntPtr),
            "string" => Compilation.GetSpecialType(SpecialType.System_String),
            "uint" => Compilation.GetSpecialType(SpecialType.System_UInt32),
            "unit" => Compilation.GetSpecialType(SpecialType.System_Unit),
            _ => Compilation.ErrorTypeSymbol
        };

        return type.TypeKind != TypeKind.Error;
    }

    private void AddAvailableConstructors(INamedTypeSymbol type, Action<IMethodSymbol?> addIfNotPresent)
    {
        if (type.TryGetUnion() is { } union)
        {
            foreach (var memberType in union.MemberTypes)
            {
                if (type.TryGetUnionCarrierConstructor(memberType, out var unionConstructor))
                    addIfNotPresent(unionConstructor);
            }

            foreach (var caseType in union.DeclaredCaseTypes)
            {
                if (type.TryGetUnionCarrierConstructor(caseType, out var unionConstructor))
                    addIfNotPresent(unionConstructor);
            }

            if (type.InstanceConstructors.IsDefaultOrEmpty || type.InstanceConstructors.Length == 0)
                AddAvailableStandardUnionConstructors(type, addIfNotPresent);
        }

        AddAvailableImplicitDefaultConstructor(type);

        foreach (var constructor in type.InstanceConstructors)
            addIfNotPresent(constructor);
    }

    private void AddAvailableImplicitDefaultConstructor(INamedTypeSymbol type)
    {
        if (type is not SourceNamedTypeSymbol sourceType ||
            sourceType is IUnionSymbol ||
            sourceType.IsStatic ||
            sourceType.HasPrimaryConstructorSyntax)
        {
            return;
        }

        if (sourceType.GetDeclaredMembersWithoutEnsuring(".ctor").OfType<IMethodSymbol>().Any(static constructor => !constructor.IsStatic))
            return;

        foreach (var reference in sourceType.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration)
                continue;

            if (declaration is ClassDeclarationSyntax { ParameterList: not null } ||
                declaration is RecordDeclarationSyntax { ParameterList: not null } ||
                declaration.Members.OfType<ConstructorDeclarationSyntax>().Any(static constructor => !HasStaticModifier(constructor.Modifiers)) ||
                declaration.Members.OfType<ParameterlessConstructorDeclarationSyntax>().Any(static constructor => !HasStaticModifier(constructor.Modifiers)))
            {
                return;
            }
        }

        var location = sourceType.Locations.FirstOrDefault() ?? Location.None;
        var syntaxReference = sourceType.DeclaringSyntaxReferences.FirstOrDefault();
        var references = syntaxReference is null ? Array.Empty<SyntaxReference>() : [syntaxReference];

        _ = new SourceMethodSymbol(
            ".ctor",
            Compilation.GetSpecialType(SpecialType.System_Unit),
            ImmutableArray<SourceParameterSymbol>.Empty,
            sourceType,
            sourceType,
            sourceType.ContainingNamespace?.AsSourceNamespace(),
            [location],
            references,
            isStatic: false,
            methodKind: MethodKind.Constructor,
            declaredAccessibility: Accessibility.Public);
    }

    private void AddAvailableStandardUnionConstructors(INamedTypeSymbol unionType, Action<IMethodSymbol?> addIfNotPresent)
    {
        var declaration = unionType.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<UnionDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration?.MemberTypes is not { } memberTypes)
            return;

        var unitType = Compilation.GetSpecialType(SpecialType.System_Unit);
        var namespaceSymbol = unionType.ContainingNamespace;

        foreach (var memberTypeSyntax in memberTypes.Types)
        {
            if (!TryResolveStandardUnionMemberTypeSyntax(memberTypeSyntax, out var memberType))
                continue;

            var constructor = new SourceMethodSymbol(
                ".ctor",
                unitType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                unionType,
                unionType,
                namespaceSymbol,
                [memberTypeSyntax.GetLocation()],
                Array.Empty<SyntaxReference>(),
                isStatic: false,
                methodKind: MethodKind.Constructor,
                declaredAccessibility: Accessibility.Public);

            var parameter = new SourceParameterSymbol(
                "value",
                memberType,
                constructor,
                unionType,
                namespaceSymbol,
                [memberTypeSyntax.GetLocation()],
                Array.Empty<SyntaxReference>());

            constructor.SetParameters([parameter]);
            addIfNotPresent(constructor);
        }

        bool TryResolveStandardUnionMemberTypeSyntax(TypeSyntax memberTypeSyntax, out ITypeSymbol memberType)
        {
            if (memberTypeSyntax is IdentifierNameSyntax identifier &&
                !unionType.TypeParameters.IsDefaultOrEmpty &&
                !unionType.TypeArguments.IsDefaultOrEmpty &&
                unionType.TypeParameters.Length == unionType.TypeArguments.Length)
            {
                for (var i = 0; i < unionType.TypeParameters.Length; i++)
                {
                    if (string.Equals(unionType.TypeParameters[i].Name, identifier.Identifier.ValueText, StringComparison.Ordinal))
                    {
                        memberType = unionType.TypeArguments[i];
                        return true;
                    }
                }
            }

            return TryResolveAvailableTypeSyntax(memberTypeSyntax, out memberType);
        }
    }

    private static void AddInvokeCandidatesFromType(INamedTypeSymbol type, Action<IMethodSymbol?> addIfNotPresent)
    {
        foreach (var invokeCandidate in type.GetMembers("Invoke").OfType<IMethodSymbol>())
        {
            if (!invokeCandidate.IsStatic)
                addIfNotPresent(invokeCandidate);
        }
    }

    private bool TryGetAvailableMemberAccessSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        var memberAccess = node switch
        {
            MemberAccessExpressionSyntax access => access,
            IdentifierNameSyntax identifier when
                identifier.Parent is MemberAccessExpressionSyntax access &&
                IsSameSyntaxNode(access.Name, identifier) => access,
            _ => null
        };

        if (memberAccess?.Name is not IdentifierNameSyntax memberName)
        {
            info = default;
            return false;
        }

        if (IsLikelyTypeExpression(memberAccess.Expression) &&
            TryResolveAvailableTypeExpression(memberAccess.Expression, out var typeExpressionType) &&
            typeExpressionType is not null)
        {
            var staticMembers = LookupAvailableMembers(typeExpressionType, memberName.Identifier.ValueText);
            if (!staticMembers.IsDefaultOrEmpty)
            {
                info = CreateAvailableMemberAccessSymbolInfo(node, staticMembers);
                StoreSymbolMapping(node, info);
                if (!ReferenceEquals(node, memberAccess))
                    StoreSymbolMapping(memberAccess, info);

                if (info.Symbol is { } staticMember)
                    StoreNodeInterestSymbolDescriptor(node, staticMember);
                return true;
            }
        }

        var receiverType = TryGetAvailableReceiverType(memberAccess.Expression);
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
        {
            info = default;
            return false;
        }

        var memberReceiverType = receiverType.GetNonNullableType();
        if (memberReceiverType is null || memberReceiverType.TypeKind == TypeKind.Error)
        {
            info = default;
            return false;
        }

        var members = LookupAvailableMembers(memberReceiverType, memberName.Identifier.ValueText);
        if (members.IsDefaultOrEmpty)
        {
            info = default;
            return false;
        }

        info = CreateAvailableMemberAccessSymbolInfo(node, members);
        StoreSymbolMapping(node, info);
        if (!ReferenceEquals(node, memberAccess))
            StoreSymbolMapping(memberAccess, info);

        if (info.Symbol is { } member)
            StoreNodeInterestSymbolDescriptor(node, member);
        return true;
    }

    private SymbolInfo CreateAvailableMemberAccessSymbolInfo(SyntaxNode node, ImmutableArray<ISymbol> members)
    {
        var selected = members.Length == 1
            ? members[0]
            : members.FirstOrDefault(static member => member is IPropertySymbol or IFieldSymbol or IEventSymbol);
        var info = selected is not null
            ? new SymbolInfo(selected, members)
            : new SymbolInfo(CandidateReason.MemberGroup, members);
        return ProjectBackingFieldSymbolsToAssociatedProperty(node, info);
    }

    private bool TryGetInvokedExpressionSymbolInfo(
        BoundNode boundInvocationRoot,
        SimpleNameSyntax invokedName,
        out SymbolInfo info)
    {
        info = default;

        if (boundInvocationRoot is BoundInvocationExpression { Receiver: { } receiver } &&
            receiver.GetSymbolInfo() is { } receiverInfo &&
            HasSymbolInfo(receiverInfo) &&
            receiverInfo.Symbol is { } receiverSymbol &&
            string.Equals(receiverSymbol.Name, invokedName.Identifier.ValueText, StringComparison.Ordinal))
        {
            info = receiverInfo;
            return true;
        }

        if (!TryFindBoundNodeBySyntax(boundInvocationRoot, invokedName, out var boundInvokedNode) ||
            boundInvokedNode is not BoundExpression boundInvokedExpression)
        {
            return false;
        }

        var invokedInfo = boundInvokedExpression.GetSymbolInfo();
        if (!HasSymbolInfo(invokedInfo))
            return false;

        var symbol = invokedInfo.Symbol;
        if (symbol is not null &&
            !string.Equals(symbol.Name, invokedName.Identifier.ValueText, StringComparison.Ordinal))
        {
            return false;
        }

        info = invokedInfo;
        return true;
    }

    private bool TryGetContextualArgumentSymbolInfo(
        IdentifierNameSyntax identifier,
        out SymbolInfo info)
    {
        info = default;

        if (identifier.Parent is not ArgumentSyntax argument ||
            !IsSameSyntaxNode(argument.Expression, identifier) ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (!TryLookupAvailableFunctionDeclarations(
                identifier,
                identifier.Identifier.ValueText,
                allowSourceDeclarationBinding: true,
                out var methods) ||
            methods.IsDefaultOrEmpty)
        {
            return false;
        }

        if (!TryBindInterestRegion(invocation, out var boundInvocation) ||
            !TryFindBoundNodeBySyntax(boundInvocation, identifier, out var boundArgumentNode) ||
            boundArgumentNode is not BoundExpression boundArgument)
        {
            return false;
        }

        var contextualInfo = boundArgument.GetSymbolInfo();
        if (!HasSymbolInfo(contextualInfo))
            return false;

        info = contextualInfo;
        return true;
    }

    private static INamedTypeSymbol? GetNamedTypeFromAvailableSymbol(ISymbol? symbol)
        => symbol switch
        {
            INamedTypeSymbol type => type,
            IAliasSymbol { UnderlyingSymbol: INamedTypeSymbol aliasedType } => aliasedType,
            _ => null
        };

    private bool TryResolveAvailableTypeExpression(
        ExpressionSyntax expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INamedTypeSymbol? type)
    {
        type = null;

        if (expression is SimpleNameSyntax simpleName)
        {
            if (!IsLikelyTypeIdentifier(simpleName.Identifier.ValueText))
                return false;

            if (TryLookupAvailableTypeFromBinder(simpleName, out var binderType) &&
                binderType is INamedTypeSymbol { TypeKind: not TypeKind.Error } namedBinderType)
            {
                if (TryConstructAvailableNamedType(simpleName, namedBinderType, out var constructedBinderType) &&
                    constructedBinderType is INamedTypeSymbol constructedBinderNamedType)
                {
                    type = constructedBinderNamedType;
                    return true;
                }

                return false;
            }

            if (!TryLookupAvailableNamedType(simpleName.Identifier.ValueText, simpleName is GenericNameSyntax genericName
                    ? genericName.TypeArgumentList.Arguments.Count
                    : 0, out var namedType) ||
                namedType is null)
            {
                return false;
            }

            if (!TryConstructAvailableNamedType(simpleName, namedType, out var constructedType) ||
                constructedType is not INamedTypeSymbol constructedNamedType)
            {
                return false;
            }

            type = constructedNamedType;
            return true;
        }

        var metadataName = expression.ToString();
        type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(metadataName);
        if (type is not null)
            return true;

        if (expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name is not SimpleNameSyntax memberName)
        {
            return false;
        }

        var arity = memberName is GenericNameSyntax memberGeneric
            ? memberGeneric.TypeArgumentList.Arguments.Count
            : 0;
        metadataName = memberAccess.Expression + "." + GetMetadataTypeName(memberName.Identifier.ValueText, arity);
        type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(metadataName);
        return type is not null;
    }

    private ITypeSymbol? TryGetAvailableReceiverType(ExpressionSyntax receiver)
    {
        if (receiver is InvocationExpressionSyntax receiverInvocation &&
            TryGetAvailableInvocationReceiverReturnType(receiverInvocation, out var invocationReceiverType))
        {
            return invocationReceiverType;
        }

        if (TryGetAvailableTypeInfo(receiver, out var receiverTypeInfo))
        {
            var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
            if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                return receiverType;
        }

        if (TryGetAvailableSymbolInfo(receiver, out var receiverInfo))
        {
            var symbolType = GetTypeFromSymbol(receiverInfo.Symbol?.UnderlyingSymbol ?? receiverInfo.Symbol);
            return symbolType;
        }

        return null;
    }

    private bool TryGetAvailableInvocationReceiverReturnType(
        InvocationExpressionSyntax invocation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? returnType)
    {
        returnType = null;

        if (!TryGetAvailableInvocationCandidates(invocation, out var candidates) ||
            candidates.IsDefaultOrEmpty)
        {
            return false;
        }

        var resolvedCandidates = candidates
            .Where(static candidate => !HasUnresolvedMethodTypeParameters(candidate))
            .ToImmutableArray();
        var candidateMethods = resolvedCandidates.IsDefaultOrEmpty
            ? candidates
            : resolvedCandidates;

        IMethodSymbol? selectedMethod = null;
        if (TryChooseAvailableInvocationMethodCandidate(candidateMethods, invocation, out var chosenMethod))
        {
            selectedMethod = chosenMethod;
        }
        else if (TryGetAvailableInvocationSymbolInfo(invocation, out var symbolInfo) &&
                 symbolInfo.Symbol is IMethodSymbol symbolMethod)
        {
            selectedMethod = symbolMethod;
        }

        if (selectedMethod is not null)
        {
            if (GetInvocationReturnType(selectedMethod) is { } selectedReturnType &&
                IsUsefulAvailableExpressionType(selectedReturnType))
            {
                returnType = selectedReturnType;
                StoreTypeMapping(
                    invocation,
                    new TypeInfo(selectedReturnType, selectedReturnType, ComputeConversion(selectedReturnType, selectedReturnType)));
                StoreSymbolMapping(invocation, new SymbolInfo(selectedMethod, candidates.Cast<ISymbol>().ToImmutableArray()));
                return true;
            }
        }

        if (TryGetBestAvailableInvocationReturnType(invocation, candidates, out returnType) &&
            returnType is not null &&
            returnType.TypeKind != TypeKind.Error)
        {
            StoreTypeMapping(invocation, new TypeInfo(returnType, returnType, ComputeConversion(returnType, returnType)));
            return true;
        }

        return false;
    }

    private static ISymbol? LookupAvailableMember(ITypeSymbol receiverType, string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            return null;

        for (var current = receiverType as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            var member = current
                .GetMembers(memberName)
                .FirstOrDefault(IsAvailableLookupMember);
            if (member is not null)
                return member;
        }

        return null;
    }

    private static ImmutableArray<ISymbol> LookupAvailableMembers(ITypeSymbol receiverType, string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            return ImmutableArray<ISymbol>.Empty;

        for (var current = receiverType as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            var members = current
                .GetMembers(memberName)
                .Where(IsAvailableLookupMember)
                .ToImmutableArray();
            if (!members.IsDefaultOrEmpty)
                return members;
        }

        return ImmutableArray<ISymbol>.Empty;
    }

    private static bool IsAvailableLookupMember(ISymbol candidate)
        => candidate switch
        {
            IFieldSymbol => true,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.IsDefaultOrEmpty,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations.IsDefaultOrEmpty,
            IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary or MethodKind.Function or MethodKind.ReducedExtension,
                ExplicitInterfaceImplementations.IsDefaultOrEmpty: true
            } => true,
            _ => false
        };

    /// <summary>
    /// Tries to retrieve type information from semantic state that is already available.
    /// This method does not bind cold bodies, create operations, or run diagnostics.
    /// </summary>
    internal bool TryGetAvailableTypeInfo(ExpressionSyntax expression, out TypeInfo typeInfo)
    {
        if (TryGetCachedTypeInfo(expression, out typeInfo) &&
            HasNonErrorTypeInfo(typeInfo))
        {
            if (expression.Kind == SyntaxKind.SuppressNullableWarningExpression &&
                (typeInfo.Type ?? typeInfo.ConvertedType) is { } suppressedType)
            {
                var nonNullableType = suppressedType.GetNonNullableType();
                typeInfo = new TypeInfo(
                    nonNullableType,
                    nonNullableType,
                    ComputeConversion(nonNullableType, nonNullableType));
            }

            if (TryGetContextualConvertedType(expression, typeInfo.Type, out var contextualConvertedType) &&
                !SymbolEqualityComparer.Default.Equals(typeInfo.ConvertedType, contextualConvertedType))
            {
                typeInfo = new TypeInfo(
                    typeInfo.Type,
                    contextualConvertedType,
                    ComputeConversion(typeInfo.Type, contextualConvertedType));
                StoreTypeMapping(expression, typeInfo);
            }

            return true;
        }

        if (TryGetAvailableLiteralType(expression, out var literalType) &&
            literalType is not null &&
            literalType.TypeKind != TypeKind.Error)
        {
            var convertedType = literalType;
            if (TryGetTargetTypeForExpression(expression, out var targetType) &&
                targetType is not null &&
                targetType.TypeKind != TypeKind.Error)
            {
                convertedType = targetType;
            }

            typeInfo = new TypeInfo(literalType, convertedType, ComputeConversion(literalType, convertedType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is IdentifierNameSyntax identifierExpression &&
            TryGetAvailableVisibleLocalType(identifierExpression, out var visibleLocalType))
        {
            typeInfo = new TypeInfo(visibleLocalType, visibleLocalType, ComputeConversion(visibleLocalType, visibleLocalType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is WithExpressionSyntax withExpression &&
            TryGetWithReceiverType(withExpression.Expression, out var withReceiverType) &&
            withReceiverType is not null &&
            withReceiverType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(
                withReceiverType,
                withReceiverType,
                ComputeConversion(withReceiverType, withReceiverType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is PrefixOperatorExpressionSyntax { Kind: SyntaxKind.AwaitExpression } awaitExpression &&
            TryGetAvailableAwaitExpressionType(awaitExpression, out var awaitType) &&
            awaitType is not null &&
            awaitType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(awaitType, awaitType, ComputeConversion(awaitType, awaitType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is PostfixOperatorExpressionSyntax
            {
                Kind: SyntaxKind.SuppressNullableWarningExpression
            } suppressNullableExpression &&
            TryGetAvailableTypeInfo(suppressNullableExpression.Expression, out var operandTypeInfo) &&
            (operandTypeInfo.Type ?? operandTypeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } operandType)
        {
            var type = operandType.GetNonNullableType();
            typeInfo = new TypeInfo(type, type, ComputeConversion(type, type));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is PropagateExpressionSyntax propagateExpression &&
            TryGetAvailablePropagationExpressionType(propagateExpression, out var propagateType) &&
            propagateType is not null &&
            propagateType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(propagateType, propagateType, ComputeConversion(propagateType, propagateType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is TryExpressionSyntax tryExpression &&
            TryGetAvailableTryExpressionType(tryExpression, out var tryType) &&
            tryType is not null &&
            tryType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(tryType, tryType, ComputeConversion(tryType, tryType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is InfixOperatorExpressionSyntax infixExpression &&
            TryGetAvailableInfixExpressionType(infixExpression, out typeInfo))
        {
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is FunctionExpressionSyntax functionExpression &&
            TryGetAvailableOrCachedFunctionExpressionDelegateType(functionExpression, out var functionDelegateType) &&
            functionDelegateType is not null &&
            functionDelegateType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(
                functionDelegateType,
                functionDelegateType,
                ComputeConversion(functionDelegateType, functionDelegateType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (TryGetCachedBoundNode(expression) is BoundExpression cachedExpression &&
            !IsLikelyStaleFunctionBodyNode(cachedExpression))
        {
            var type = cachedExpression.Type;
            var convertedType = cachedExpression.GetConvertedType() ?? type;
            if (TryGetContextualConvertedType(expression, type, out var contextualConvertedType))
                convertedType = contextualConvertedType;

            if ((type is not null && type.TypeKind != TypeKind.Error) ||
                (convertedType is not null && convertedType.TypeKind != TypeKind.Error))
            {
                var conversion = cachedExpression switch
                {
                    BoundConversionExpression cast => cast.Conversion,
                    BoundAsExpression asExpression => asExpression.Conversion,
                    _ => ComputeConversion(type, convertedType)
                };

                typeInfo = new TypeInfo(type, convertedType, conversion);
                StoreTypeMapping(expression, typeInfo);
                return true;
            }
        }

        if (expression is MemberAccessExpressionSyntax memberAccessExpression &&
            TryGetAvailableMemberAccessTypeInfo(memberAccessExpression, out typeInfo))
        {
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (expression is InvocationExpressionSyntax invocation &&
            TryGetAvailableInvocationCandidates(invocation, out var invocationCandidates))
        {
            if (TryGetBestAvailableInvocationReturnType(invocation, invocationCandidates, out var inferredType) &&
                inferredType is not null)
            {
                var convertedType = inferredType;
                if (TryGetContextualConvertedType(expression, inferredType, out var contextualConvertedType))
                    convertedType = contextualConvertedType;

                typeInfo = new TypeInfo(inferredType, convertedType, ComputeConversion(inferredType, convertedType));
                StoreTypeMapping(expression, typeInfo);
                return true;
            }

            if (TryGetCommonAvailableInvocationReturnType(invocationCandidates, out inferredType) &&
                inferredType is not null)
            {
                var convertedType = inferredType;
                if (TryGetContextualConvertedType(expression, inferredType, out var contextualConvertedType))
                    convertedType = contextualConvertedType;

                typeInfo = new TypeInfo(inferredType, convertedType, ComputeConversion(inferredType, convertedType));
                StoreTypeMapping(expression, typeInfo);
                return true;
            }

            if (TryGetAvailableInvocationSymbolInfo(invocation, out var invocationInfo) &&
                invocationInfo.Symbol is IMethodSymbol selectedMethod &&
                GetInvocationReturnType(selectedMethod) is { } selectedReturnType &&
                IsUsefulAvailableExpressionType(selectedReturnType))
            {
                var convertedType = selectedReturnType;
                if (TryGetContextualConvertedType(expression, selectedReturnType, out var contextualConvertedType))
                    convertedType = contextualConvertedType;

                typeInfo = new TypeInfo(selectedReturnType, convertedType, ComputeConversion(selectedReturnType, convertedType));
                StoreTypeMapping(expression, typeInfo);
                return true;
            }
        }

        if (expression is InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken,
                Right: InvocationExpressionSyntax pipeInvocation
            } pipeExpression &&
            TryGetAvailablePipeInvocationCandidates(pipeInvocation, out var pipeCandidates) &&
            TryGetAvailableTypeInfo(pipeExpression.Left, out var pipeReceiverTypeInfo) &&
            (pipeReceiverTypeInfo.Type ?? pipeReceiverTypeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } pipeReceiverType)
        {
            var inferredType = pipeCandidates
                .Select(method => GetAvailablePipeInvocationReturnType(method, pipeReceiverType))
                .FirstOrDefault(static type => type is not null && type.TypeKind != TypeKind.Error);
            if (inferredType is not null)
            {
                typeInfo = new TypeInfo(inferredType, inferredType, ComputeConversion(inferredType, inferredType));
                StoreTypeMapping(expression, typeInfo);
                return true;
            }
        }

        if (expression is IdentifierNameSyntax identifier &&
            TryGetEnclosingParameterTypeFromSyntax(identifier, out var functionParameterType))
        {
            typeInfo = new TypeInfo(functionParameterType, functionParameterType, ComputeConversion(functionParameterType, functionParameterType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        if (TryGetAvailableSymbolInfo(expression, out var symbolInfo) &&
            GetTypeFromSymbol(symbolInfo.Symbol?.UnderlyingSymbol ?? symbolInfo.Symbol) is { } symbolType &&
            symbolType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(symbolType, symbolType, ComputeConversion(symbolType, symbolType));
            StoreTypeMapping(expression, typeInfo);
            return true;
        }

        typeInfo = new TypeInfo(null, null);
        return false;
    }

    private bool TryGetAvailableVisibleLocalType(
        IdentifierNameSyntax identifier,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        var name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var position = identifier.Span.Start;
        foreach (var scopeNode in EnumerateVisibleValueScopes(identifier))
        {
            if (!_visibleValueScopeCache.TryGetValue(scopeNode, out var symbols))
            {
                symbols = GetOrCollectVisibleValueDeclarations(scopeNode);
                _visibleValueScopeCache[scopeNode] = symbols;
            }

            for (var i = 0; i < symbols.Length; i++)
            {
                var candidate = symbols[i];
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal) ||
                    candidate.Start > position ||
                    candidate.DeclarationNode is not VariableDeclaratorSyntax variableDeclarator)
                {
                    continue;
                }

                if (TryBindLocalDeclarationForStableLocalSymbol(
                        variableDeclarator,
                        out var localSymbol,
                        allowErrorType: false,
                        allowInitializerBinding: true,
                        allowBindingFallback: false,
                        allowBoundInitializerBindingWithoutFallback: true) &&
                    localSymbol.Type is { TypeKind: not TypeKind.Error } localType)
                {
                    type = localType;
                    StoreSymbolInfo(identifier, localSymbol);
                    return true;
                }

                if (variableDeclarator.Initializer is not null &&
                    TryBindLocalDeclarationForStableLocalSymbol(
                        variableDeclarator,
                        out localSymbol,
                        allowErrorType: false,
                        allowInitializerBinding: true,
                        allowBindingFallback: true) &&
                    localSymbol.Type is { TypeKind: not TypeKind.Error } reboundLocalType)
                {
                    type = reboundLocalType;
                    StoreSymbolInfo(identifier, localSymbol);
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryGetAvailableMemberAccessTypeInfo(
        MemberAccessExpressionSyntax memberAccess,
        out TypeInfo typeInfo)
    {
        typeInfo = new TypeInfo(null, null);

        if (!TryGetAvailableTypeInfo(memberAccess.Expression, out var receiverTypeInfo))
            return false;

        var receiverType = receiverTypeInfo.Type ?? receiverTypeInfo.ConvertedType;
        if (receiverType?.GetNonNullableType() is not INamedTypeSymbol namedReceiver ||
            namedReceiver.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var memberName = memberAccess.Name.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(memberName))
            return false;

        var lookupReceiver = namedReceiver;
        var member = TryGetAvailableInstanceValueMember(lookupReceiver, memberName);
        var memberType = GetTypeFromSymbol(member);
        if ((member is null || memberType is null || memberType.TypeKind == TypeKind.Error) &&
            TryEnsureSourceTypeValueMemberSignatureDeclared(namedReceiver, memberName, out var ensuredValueReceiverType, out var valueMembers))
        {
            lookupReceiver = ensuredValueReceiverType;
            member = valueMembers.FirstOrDefault();
            memberType = GetTypeFromSymbol(member);
        }

        if ((member is null || memberType is null || memberType.TypeKind == TypeKind.Error) &&
            TryEnsureSourceTypeMemberSignaturesDeclared(namedReceiver, out var ensuredReceiverType))
        {
            lookupReceiver = ensuredReceiverType;
            member = TryGetAvailableInstanceValueMember(lookupReceiver, memberName);
            memberType = GetTypeFromSymbol(member);
        }

        if (memberType is null || memberType.TypeKind == TypeKind.Error)
            return false;

        typeInfo = new TypeInfo(memberType, memberType, ComputeConversion(memberType, memberType));
        if (member is not null)
            StoreSymbolMapping(memberAccess, new SymbolInfo(member));

        return true;
    }

    private static ISymbol? TryGetAvailableInstanceValueMember(INamedTypeSymbol receiverType, string memberName)
        => GetAvailableMembers(receiverType, memberName)
            .FirstOrDefault(static symbol =>
                symbol is IFieldSymbol or IPropertySymbol or IEventSymbol);

    private static ImmutableArray<ISymbol> GetAvailableMembers(INamedTypeSymbol receiverType, string memberName)
        => receiverType is SourceNamedTypeSymbol sourceType
            ? sourceType.GetDeclaredMembersWithoutEnsuring(memberName)
            : receiverType.GetMembers(memberName);

    private ImmutableArray<IMethodSymbol> GetAvailableMethodMembers(INamedTypeSymbol receiverType, string memberName)
    {
        var methods = GetAvailableMembers(receiverType, memberName)
            .OfType<IMethodSymbol>()
            .ToImmutableArray();
        if (!methods.IsDefaultOrEmpty)
            return methods;

        if (TryEnsureSourceTypeMethodSignaturesDeclared(receiverType, memberName, out _, out methods))
            return methods;

        return TryEnsureSourceTypeMemberSignaturesDeclared(receiverType, out var ensuredReceiverType)
            ? GetAvailableMembers(ensuredReceiverType, memberName)
                .OfType<IMethodSymbol>()
                .ToImmutableArray()
            : ImmutableArray<IMethodSymbol>.Empty;
    }

    internal bool TryEnsureSourceTypeMemberSignatureDeclared(
        INamedTypeSymbol receiverType,
        string memberName,
        out INamedTypeSymbol ensuredReceiverType)
    {
        ensuredReceiverType = receiverType;
        var ensured = false;

        if (TryEnsureSourceTypeValueMemberSignatureDeclared(receiverType, memberName, out var valueReceiverType, out _))
        {
            ensuredReceiverType = valueReceiverType;
            ensured = true;
        }

        if (TryEnsureSourceTypeMethodSignaturesDeclared(ensuredReceiverType, memberName, out var methodReceiverType, out _))
        {
            ensuredReceiverType = methodReceiverType;
            ensured = true;
        }

        return ensured;
    }

    private bool TryEnsureSourceTypeMethodSignaturesDeclared(
        INamedTypeSymbol receiverType,
        string memberName,
        out INamedTypeSymbol ensuredReceiverType,
        out ImmutableArray<IMethodSymbol> methods)
    {
        ensuredReceiverType = receiverType;
        methods = ImmutableArray<IMethodSymbol>.Empty;

        var sourceType = receiverType.GetNonNullableType() as SourceNamedTypeSymbol ??
            receiverType.ConstructedFrom as SourceNamedTypeSymbol;
        if (sourceType is null)
            return false;

        foreach (var reference in sourceType.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration ||
                declaration.SyntaxTree is null)
            {
                continue;
            }

            var matchingMethods = declaration.Members
                .OfType<MethodDeclarationSyntax>()
                .Where(method => GetDeclaredMethodLookupName(method) == memberName)
                .ToArray();
            if (matchingMethods.Length == 0)
                continue;

            var declaringModel = GetDeclaringSemanticModel(sourceType, declaration.SyntaxTree);
            declaringModel.Compilation.EnsureSourceTypeDeclarationsDeclared();
            declaringModel.EnsureDeclarations();

            if (!declaringModel.Compilation.TryGetDeclaredTypeSymbol(declaration, out _) &&
                sourceType.DeclaringSyntaxReferences.Any(typeReference =>
                    typeReference.SyntaxTree == declaration.SyntaxTree &&
                    typeReference.Span == declaration.Span))
            {
                declaringModel.RegisterDeclaredTypeSymbol(declaration, sourceType);
            }

            var directMethods = ImmutableArray.CreateBuilder<IMethodSymbol>();
            var directMethodKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in matchingMethods)
            {
                MemberSignatureDeclarationPass.DeclareMethodSignature(declaringModel, method);
                if (declaringModel.Compilation.TryGetMethodSymbol(method, out var declaredMethod) &&
                    directMethodKeys.Add(declaredMethod.GetShallowLookupIdentityKey()))
                {
                    directMethods.Add(declaredMethod);
                }
            }

            if (declaringModel.Compilation.TryGetDeclaredTypeSymbol(declaration, out var declaredType))
                ensuredReceiverType = ReconstructSourceReceiverType(receiverType, declaredType);

            methods = GetAvailableMembers(ensuredReceiverType, memberName)
                .OfType<IMethodSymbol>()
                .ToImmutableArray();
            if (!methods.IsDefaultOrEmpty)
                return true;

            if (directMethods.Count > 0)
            {
                methods = directMethods.ToImmutable();
                return true;
            }
        }

        return false;
    }

    private bool TryEnsureSourceTypeValueMemberSignatureDeclared(
        INamedTypeSymbol receiverType,
        string memberName,
        out INamedTypeSymbol ensuredReceiverType,
        out ImmutableArray<ISymbol> members)
    {
        ensuredReceiverType = receiverType;
        members = ImmutableArray<ISymbol>.Empty;

        var sourceType = receiverType.GetNonNullableType() as SourceNamedTypeSymbol ??
            receiverType.ConstructedFrom as SourceNamedTypeSymbol;
        if (sourceType is null)
            return false;

        foreach (var reference in sourceType.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not TypeDeclarationSyntax declaration ||
                declaration.SyntaxTree is null)
            {
                continue;
            }

            var matchingMembers = GetEffectiveTypeMembers(declaration)
                .Select(static member => member.EffectiveSyntax)
                .Where(member => member switch
                {
                    FieldDeclarationSyntax field => field.Declaration.Declarators.Any(declarator =>
                        string.Equals(declarator.Identifier.ValueText, memberName, StringComparison.Ordinal)),
                    PropertyDeclarationSyntax property => string.Equals(property.Identifier.ValueText, memberName, StringComparison.Ordinal),
                    EventDeclarationSyntax @event => string.Equals(@event.Identifier.ValueText, memberName, StringComparison.Ordinal),
                    _ => false
                })
                .ToArray();
            if (matchingMembers.Length == 0)
                continue;

            var declaringModel = GetDeclaringSemanticModel(sourceType, declaration.SyntaxTree);
            declaringModel.Compilation.EnsureSourceTypeDeclarationsDeclared();
            declaringModel.EnsureDeclarations();

            if (!declaringModel.Compilation.TryGetDeclaredTypeSymbol(declaration, out _) &&
                sourceType.DeclaringSyntaxReferences.Any(typeReference =>
                    typeReference.SyntaxTree == declaration.SyntaxTree &&
                    typeReference.Span == declaration.Span))
            {
                declaringModel.RegisterDeclaredTypeSymbol(declaration, sourceType);
            }

            var directMembers = ImmutableArray.CreateBuilder<ISymbol>();
            foreach (var matchingMember in matchingMembers)
            {
                switch (matchingMember)
                {
                    case FieldDeclarationSyntax fieldDeclaration:
                        MemberSignatureDeclarationPass.DeclareFieldSignature(declaringModel, fieldDeclaration);
                        foreach (var declarator in fieldDeclaration.Declaration.Declarators)
                        {
                            if (string.Equals(declarator.Identifier.ValueText, memberName, StringComparison.Ordinal))
                            {
                                directMembers.AddRange(sourceType
                                    .GetDeclaredMembersWithoutEnsuring(memberName)
                                    .OfType<IFieldSymbol>()
                                    .Where(field => SymbolDeclarationUtilities.HasDeclaringSpan(field, declarator)));
                            }
                        }

                        break;
                    case PropertyDeclarationSyntax propertyDeclaration:
                        MemberSignatureDeclarationPass.DeclarePropertySignature(declaringModel, propertyDeclaration);
                        if (declaringModel.Compilation.TryGetPropertySymbol(propertyDeclaration, out var propertySymbol))
                            directMembers.Add(propertySymbol);

                        break;
                    case EventDeclarationSyntax eventDeclaration:
                        MemberSignatureDeclarationPass.DeclareEventSignature(declaringModel, eventDeclaration);
                        if (declaringModel.Compilation.TryGetEventSymbol(eventDeclaration, out var eventSymbol))
                            directMembers.Add(eventSymbol);

                        break;
                }
            }

            if (declaringModel.Compilation.TryGetDeclaredTypeSymbol(declaration, out var declaredType))
                ensuredReceiverType = ReconstructSourceReceiverType(receiverType, declaredType);

            members = GetAvailableMembers(ensuredReceiverType, memberName)
                .Where(static symbol => symbol is IFieldSymbol or IPropertySymbol or IEventSymbol)
                .ToImmutableArray();
            if (!members.IsDefaultOrEmpty)
                return true;

            if (directMembers.Count > 0)
            {
                members = directMembers.ToImmutable();
                return true;
            }
        }

        return false;
    }

    private static string GetDeclaredMethodLookupName(MethodDeclarationSyntax method)
        => method.Identifier.Kind == SyntaxKind.SelfKeyword
            ? "Invoke"
            : method.Identifier.ValueText;

    private bool TryEnsureSourceTypeMemberSignaturesDeclared(
        INamedTypeSymbol receiverType,
        out INamedTypeSymbol ensuredReceiverType)
    {
        ensuredReceiverType = receiverType;

        var sourceType = receiverType.GetNonNullableType() as SourceNamedTypeSymbol ??
            receiverType.ConstructedFrom as SourceNamedTypeSymbol;
        if (sourceType is null)
            return false;

        var declarationSyntax = sourceType.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();
        if (declarationSyntax?.SyntaxTree is null)
            return false;

        var declaringModel = GetDeclaringSemanticModel(sourceType, declarationSyntax.SyntaxTree);

        declaringModel.Compilation.EnsureSourceDeclarationsDeclared();

        if (!declaringModel.Compilation.TryGetDeclaredTypeSymbol(declarationSyntax, out _) &&
            sourceType.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree == declarationSyntax.SyntaxTree &&
                reference.Span == declarationSyntax.Span))
        {
            declaringModel.RegisterDeclaredTypeSymbol(declarationSyntax, sourceType);
        }

        var ensured = declaringModel.TryEnsureTypeMemberSignaturesDeclared(sourceType);
        if (declaringModel.Compilation.TryGetDeclaredTypeSymbol(declarationSyntax, out var declaredType))
            ensuredReceiverType = ReconstructSourceReceiverType(receiverType, declaredType);

        return ensured;
    }

    private SemanticModel GetDeclaringSemanticModel(
        SourceNamedTypeSymbol sourceType,
        SyntaxTree syntaxTree)
    {
        var declaringCompilation = sourceType.ContainingAssembly is SourceAssemblySymbol sourceAssembly
            ? sourceAssembly.Compilation
            : Compilation;

        return ReferenceEquals(declaringCompilation, Compilation) &&
            ReferenceEquals(syntaxTree, SyntaxTree)
                ? this
                : declaringCompilation.GetSemanticModel(syntaxTree);
    }

    private static INamedTypeSymbol ReconstructSourceReceiverType(
        INamedTypeSymbol receiverType,
        SourceNamedTypeSymbol declaredType)
    {
        var typeArguments = TypeSubstitution.GetShallowTypeArguments(receiverType);
        if (typeArguments.Length != declaredType.TypeParameters.Length)
        {
            return declaredType;
        }

        if (receiverType.ContainingType is { } containingType &&
            declaredType.ContainingType is not null)
        {
            var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(
                TypeParameterSubstitutionComparer.Instance);
            TypeSubstitution.AddContainingTypeSubstitutions(containingType, substitutions);
            return TypeSubstitution.ReanchorNested(
                declaredType,
                containingType,
                substitutions,
                typeArguments);
        }

        return typeArguments.IsDefaultOrEmpty
            ? declaredType
            : (INamedTypeSymbol)declaredType.Construct(typeArguments.ToArray());
    }

    private static bool IsUsefulAvailableExpressionType(ITypeSymbol? type)
        => type is not null &&
           type.TypeKind != TypeKind.Error &&
           !ContainsTypeParameter(type) &&
           type is not ITypeParameterSymbol;

    private static ITypeSymbol? GetInvocationReturnType(IMethodSymbol method)
        => method.MethodKind == MethodKind.Constructor
            ? method.ContainingType
            : NullableMetadataFacts.GetReturnType(method);

    private static ITypeSymbol? GetAvailablePipeInvocationReturnType(IMethodSymbol method, ITypeSymbol receiverType)
    {
        var returnType = NullableMetadataFacts.GetReturnType(method);
        if (returnType is null || returnType.ContainsErrorType())
            return returnType;

        if (method.Parameters.Length > 0 &&
            TryInferExtensionReceiverSubstitutions(method.Parameters[0].Type, receiverType, out var substitutions))
        {
            return SubstituteTypeParameters(returnType, substitutions);
        }

        return returnType;
    }

    private bool TryGetAvailableInfixExpressionType(
        InfixOperatorExpressionSyntax expression,
        out TypeInfo typeInfo)
    {
        typeInfo = default;

        if (expression.OperatorToken.Kind == SyntaxKind.PipeToken)
            return false;

        if (!TryGetAvailableTypeInfo(expression.Left, out var leftInfo) ||
            !TryGetAvailableTypeInfo(expression.Right, out var rightInfo))
        {
            return false;
        }

        var leftType = leftInfo.ConvertedType ?? leftInfo.Type;
        var rightType = rightInfo.ConvertedType ?? rightInfo.Type;
        if (leftType is null ||
            rightType is null ||
            leftType.TypeKind == TypeKind.Error ||
            rightType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        if (!BoundBinaryOperator.TryLookup(Compilation, expression.OperatorToken.Kind, leftType, rightType, out var op) ||
            op.ResultType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var resultType = op.ResultType;
        var convertedType = resultType;
        if (TryGetTargetTypeForExpression(expression, out var targetType) &&
            targetType is not null &&
            targetType.TypeKind != TypeKind.Error)
        {
            convertedType = targetType;
        }

        typeInfo = new TypeInfo(resultType, convertedType, ComputeConversion(resultType, convertedType));
        return true;
    }

    private bool TryGetAvailableAwaitExpressionType(
        PrefixOperatorExpressionSyntax awaitExpression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        if (!TryGetAvailableTypeInfo(awaitExpression.Expression, out var operandTypeInfo))
            return false;

        var operandType = operandTypeInfo.Type ?? operandTypeInfo.ConvertedType;
        if (operandType is null || operandType.TypeKind == TypeKind.Error)
            return false;

        type = AsyncReturnTypeUtilities.ExtractAsyncResultType(Compilation, operandType);
        if (type is not null && type.TypeKind != TypeKind.Error)
            return true;

        if (AwaitablePattern.TryFind(operandType, isAccessible: null, out var awaitable, out _, out _))
        {
            type = awaitable.GetResultMethod.ReturnType;
            if (type.SpecialType == SpecialType.System_Void)
                type = Compilation.GetSpecialType(SpecialType.System_Unit);

            return type is not null && type.TypeKind != TypeKind.Error;
        }

        type = null;
        return false;
    }

    private bool TryGetAvailablePropagationExpressionType(
        PropagateExpressionSyntax propagateExpression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        if (!TryGetAvailableTypeInfo(propagateExpression.Expression, out var operandTypeInfo))
            return false;

        var operandType = (operandTypeInfo.Type ?? operandTypeInfo.ConvertedType)?.GetNonNullableType();
        if (operandType is not INamedTypeSymbol operandNamed ||
            operandNamed.TypeKind == TypeKind.Error ||
            !UnionFacts.UsesCarrierRepresentation(operandNamed))
        {
            return false;
        }

        var payloadType = TryGetAvailableCarrierPayloadType(operandNamed);
        if (payloadType is null || payloadType.TypeKind == TypeKind.Error)
            return false;

        type = TypeSymbolNormalization.NormalizeForInference(payloadType);
        return type.TypeKind != TypeKind.Error;
    }

    private static ITypeSymbol? TryGetAvailableCarrierPayloadType(INamedTypeSymbol operandNamed)
    {
        var union = operandNamed.TryGetUnion();
        if (union is null)
            return null;

        return operandNamed.Name switch
        {
            "Result" => union.DeclaredCaseTypes
                .FirstOrDefault(static @case => @case.Name == "Ok")
                ?.ConstructorParameters
                .SingleOrDefault()
                ?.Type,
            "Option" => union.DeclaredCaseTypes
                .FirstOrDefault(static @case => @case.Name == "Some")
                ?.ConstructorParameters
                .SingleOrDefault()
                ?.Type,
            _ => null
        };
    }

    internal bool TryGetCarrierFailureInfo(
        ExpressionSyntax carrierExpression,
        out string caseName,
        out ITypeSymbol? payloadType,
        out bool hasPayload,
        bool allowBindingFallback = false)
    {
        caseName = string.Empty;
        payloadType = null;
        hasPayload = false;

        TypeInfo operandTypeInfo;
        if (!TryGetAvailableTypeInfo(carrierExpression, out operandTypeInfo))
        {
            if (!allowBindingFallback)
                return false;

            operandTypeInfo = GetTypeInfo(carrierExpression);
        }

        var operandType = (operandTypeInfo.Type ?? operandTypeInfo.ConvertedType)?.GetNonNullableType();
        if (operandType is not INamedTypeSymbol operandNamed ||
            operandNamed.TypeKind == TypeKind.Error ||
            !UnionFacts.UsesCarrierRepresentation(operandNamed))
        {
            return false;
        }

        if (!TryGetCarrierFailureInfo(operandNamed, out caseName, out payloadType, out hasPayload))
            return false;

        if (payloadType is not null)
            payloadType = TypeSymbolNormalization.NormalizeForInference(payloadType);

        return payloadType is null || payloadType.TypeKind != TypeKind.Error;
    }

    private static bool TryGetCarrierFailureInfo(
        INamedTypeSymbol operandNamed,
        out string caseName,
        out ITypeSymbol? payloadType,
        out bool hasPayload)
    {
        caseName = string.Empty;
        payloadType = null;
        hasPayload = false;

        var union = operandNamed.TryGetUnion();
        if (union is null)
            return false;

        switch (operandNamed.Name)
        {
            case "Result":
                var errorCase = union.DeclaredCaseTypes.FirstOrDefault(static @case => @case.Name == "Error");
                if (errorCase is null)
                    return false;

                caseName = errorCase.Name;
                payloadType = errorCase.ConstructorParameters.SingleOrDefault()?.Type;
                hasPayload = payloadType is not null;
                return true;

            case "Option":
                var noneCase = union.DeclaredCaseTypes.FirstOrDefault(static @case => @case.Name == "None");
                if (noneCase is null)
                    return false;

                caseName = noneCase.Name;
                return true;

            default:
                return false;
        }
    }

    private bool TryGetAvailableTryExpressionType(
        TryExpressionSyntax tryExpression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        if (!TryGetAvailableTypeInfo(tryExpression.Expression, out var expressionTypeInfo))
            return false;

        var expressionType = expressionTypeInfo.Type ?? expressionTypeInfo.ConvertedType;
        if (expressionType is null || expressionType.TypeKind == TypeKind.Error)
            return false;

        var exceptionType = Compilation.GetTypeByMetadataName("System.Exception") ?? Compilation.ErrorTypeSymbol;
        if (exceptionType.TypeKind == TypeKind.Error)
            return false;

        var resultDefinition = Compilation.GetTypeByMetadataName("System.Result`2") as INamedTypeSymbol;
        if (resultDefinition is null &&
            (!TryLookupAvailableNamedType("Result", 2, out resultDefinition) || resultDefinition is null))
        {
            return false;
        }

        var resultType = resultDefinition.Construct(expressionType, exceptionType);
        if (tryExpression.QuestionToken.Kind == SyntaxKind.None)
        {
            type = resultType;
            return true;
        }

        if (resultType is not INamedTypeSymbol resultNamedType)
            return false;

        var resultPayloadType = TryGetAvailableCarrierPayloadType(resultNamedType);
        if (resultPayloadType is null || resultPayloadType.TypeKind == TypeKind.Error)
            return false;

        type = TypeSymbolNormalization.NormalizeForInference(resultPayloadType);
        return type.TypeKind != TypeKind.Error;
    }

    private bool TryGetEnclosingParameterTypeFromSyntax(
        IdentifierNameSyntax identifier,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type,
        bool allowCandidateLookup = true)
    {
        type = null;
        var name = identifier.Identifier.ValueText;

        foreach (var ancestor in identifier.Ancestors())
        {
            IEnumerable<ParameterSyntax> parameters = ancestor switch
            {
                BaseMethodDeclarationSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
                FunctionStatementSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
                MacroDeclarationSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
                SimpleFunctionExpressionSyntax { Parameter: { } parameter } => [parameter],
                ParenthesizedFunctionExpressionSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
                _ => Enumerable.Empty<ParameterSyntax>()
            };

            foreach (var parameter in parameters)
            {
                if (!string.Equals(parameter.Identifier.ValueText, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryGetAvailableParameterType(parameter, out type, allowCandidateLookup) && type is not null)
                    return true;
            }
        }

        return false;
    }

    private bool TryGetAvailableParameterType(
        ParameterSyntax parameter,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type,
        bool allowCandidateLookup = true)
    {
        type = null;

        if (!parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any() &&
            TryResolveParameterSymbolFast(parameter, out var parameterSymbol) &&
            parameterSymbol?.Type is { TypeKind: not TypeKind.Error } symbolType)
        {
            type = symbolType;
            return true;
        }

        if (TryResolveFunctionExpressionParameterSymbolFast(parameter, out var functionParameterSymbol, allowCandidateLookup) &&
            functionParameterSymbol?.Type is { TypeKind: not TypeKind.Error } functionParameterType)
        {
            type = functionParameterType;
            return true;
        }

        if (parameter.TypeAnnotation?.Type is { } typeSyntax &&
            TryGetAvailableFunctionParameterType(typeSyntax, out var syntaxType) &&
            syntaxType.TypeKind != TypeKind.Error)
        {
            type = syntaxType;
            return true;
        }

        return false;
    }

    private bool TryGetAvailableLiteralType(ExpressionSyntax expression, out ITypeSymbol? type)
    {
        type = null;

        if (expression is InterpolatedStringExpressionSyntax)
        {
            type = Compilation.GetSpecialType(SpecialType.System_String);
            return true;
        }

        if (expression is not LiteralExpressionSyntax literal)
            return false;

        if (literal.Kind == SyntaxKind.NullLiteralExpression)
        {
            type = Compilation.NullTypeSymbol;
            return true;
        }

        type = literal.Token.Value switch
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

        return type is not null;
    }

    private bool TryGetBestAvailableInvocationReturnType(
        InvocationExpressionSyntax invocation,
        ImmutableArray<IMethodSymbol> invocationCandidates,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? returnType)
    {
        returnType = null;

        if (invocationCandidates.IsDefaultOrEmpty ||
            invocation.ArgumentList.Arguments.Any(static argument => argument.NameColon is not null))
        {
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        var argumentTypes = new ITypeSymbol?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Expression is FunctionExpressionSyntax)
                continue;

            var argumentType = TryGetNonContextualAvailableArgumentType(arguments[i].Expression);
            if (argumentType is null || argumentType.TypeKind == TypeKind.Error)
                return false;

            argumentTypes[i] = argumentType;
        }

        var bestScore = int.MinValue;
        foreach (var candidate in invocationCandidates)
        {
            if (!TryScoreAvailableInvocationCandidate(candidate, invocation, argumentTypes, out var score))
                continue;

            var candidateReturnType = GetInvocationReturnType(candidate);
            if (!IsUsefulAvailableExpressionType(candidateReturnType))
                continue;

            if (score > bestScore)
            {
                returnType = candidateReturnType;
                bestScore = score;
                continue;
            }

            if (score == bestScore &&
                !HaveEquivalentAvailableTypeShape(returnType, candidateReturnType))
            {
                returnType = null;
                return false;
            }
        }

        return returnType is not null;
    }

    private static bool TryGetCommonAvailableInvocationReturnType(
        ImmutableArray<IMethodSymbol> invocationCandidates,
        out ITypeSymbol? returnType)
    {
        returnType = null;

        foreach (var candidate in invocationCandidates)
        {
            var candidateReturnType = GetInvocationReturnType(candidate);

            if (!IsUsefulAvailableExpressionType(candidateReturnType))
                continue;

            if (returnType is null)
            {
                returnType = candidateReturnType;
                continue;
            }

            if (!HaveEquivalentAvailableTypeShape(returnType, candidateReturnType))
            {
                returnType = null;
                return false;
            }
        }

        return returnType is not null;
    }

    private static bool HaveEquivalentAvailableTypeShape(ITypeSymbol? left, ITypeSymbol? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        if (left.SpecialType != SpecialType.None || right.SpecialType != SpecialType.None)
            return left.SpecialType == right.SpecialType;

        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            if (!string.Equals(leftNamed.MetadataName, rightNamed.MetadataName, StringComparison.Ordinal) ||
                leftNamed.Arity != rightNamed.Arity ||
                leftNamed.TypeArguments.Length != rightNamed.TypeArguments.Length)
            {
                return false;
            }

            for (var i = 0; i < leftNamed.TypeArguments.Length; i++)
            {
                if (!HaveEquivalentAvailableTypeShape(leftNamed.TypeArguments[i], rightNamed.TypeArguments[i]))
                    return false;
            }

            return true;
        }

        return string.Equals(left.ToDisplayString(), right.ToDisplayString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Tries to retrieve type syntax information from semantic state that is already available.
    /// This method does not bind cold bodies, create operations, or run diagnostics.
    /// </summary>
    internal bool TryGetAvailableTypeInfo(TypeSyntax typeSyntax, out TypeInfo typeInfo)
    {
        if (TryGetCachedTypeInfo(typeSyntax, out typeInfo) &&
            HasNonErrorTypeInfo(typeInfo))
        {
            return true;
        }

        if (TryGetAvailablePredefinedTypeInfo(typeSyntax, out typeInfo))
        {
            StoreTypeMapping(typeSyntax, typeInfo);
            return true;
        }

        if (typeSyntax is ExpressionSyntax expressionSyntax &&
            !IsExplicitTypeSyntaxContext(typeSyntax))
        {
            return TryGetAvailableTypeInfo(expressionSyntax, out typeInfo);
        }

        if (typeSyntax is SimpleNameSyntax typeName &&
            TryLookupAvailableTypeFromBinder(typeName, out var binderType) &&
            binderType is not null &&
            binderType.TypeKind != TypeKind.Error)
        {
            if (typeName is GenericNameSyntax genericName &&
                binderType is INamedTypeSymbol namedType)
            {
                var typeArguments = ResolveAvailableTypeArguments(genericName.TypeArgumentList);
                if (!typeArguments.IsDefaultOrEmpty &&
                    typeArguments.Length == genericName.TypeArgumentList.Arguments.Count)
                {
                    binderType = namedType.Construct(typeArguments.ToArray());
                }
                else
                {
                    binderType = null;
                }
            }

            if (binderType is not null)
            {
                typeInfo = new TypeInfo(binderType, binderType, ComputeConversion(binderType, binderType));
                StoreTypeMapping(typeSyntax, typeInfo);
                return true;
            }
        }

        if (TryGetAvailableSymbolInfo(typeSyntax, out var symbolInfo))
        {
            var type = symbolInfo.Symbol switch
            {
                ITypeSymbol typeSymbol => typeSymbol,
                IAliasSymbol { UnderlyingSymbol: ITypeSymbol aliasedType } => aliasedType,
                _ => null
            };

            if (type is not null && type.TypeKind != TypeKind.Error)
            {
                typeInfo = new TypeInfo(type, type, ComputeConversion(type, type));
                StoreTypeMapping(typeSyntax, typeInfo);
                return true;
            }
        }

        if (TryBindAvailableTypeSyntax(typeSyntax, out var boundType) &&
            boundType is not null &&
            boundType.TypeKind != TypeKind.Error)
        {
            typeInfo = new TypeInfo(boundType, boundType, ComputeConversion(boundType, boundType));
            StoreTypeMapping(typeSyntax, typeInfo);
            return true;
        }

        typeInfo = new TypeInfo(null, null);
        return false;
    }

    private bool TryBindAvailableTypeSyntax(TypeSyntax typeSyntax, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;

        try
        {
            var binder = GetBinder(typeSyntax);
            using var nonReportingScope = binder.Diagnostics.CreateNonReportingScope();
            type = binder.BindTypeSyntax(typeSyntax).ResolvedType;
            return type is not null;
        }
        catch
        {
            type = null;
            return false;
        }
    }

    private bool TryGetAvailablePredefinedTypeInfo(TypeSyntax typeSyntax, out TypeInfo typeInfo)
    {
        var specialType = typeSyntax switch
        {
            PredefinedTypeSyntax predefined => predefined.Keyword.Kind switch
            {
                SyntaxKind.BoolKeyword => SpecialType.System_Boolean,
                SyntaxKind.DoubleKeyword => SpecialType.System_Double,
                SyntaxKind.FloatKeyword => SpecialType.System_Single,
                SyntaxKind.IntKeyword => SpecialType.System_Int32,
                SyntaxKind.NIntKeyword => SpecialType.System_IntPtr,
                SyntaxKind.NUIntKeyword => SpecialType.System_UIntPtr,
                SyntaxKind.StringKeyword => SpecialType.System_String,
                SyntaxKind.UIntKeyword => SpecialType.System_UInt32,
                SyntaxKind.UnitKeyword => SpecialType.System_Unit,
                _ => SpecialType.None
            },
            UnitTypeSyntax => SpecialType.System_Unit,
            _ => SpecialType.None
        };

        if (specialType == SpecialType.None)
        {
            typeInfo = default;
            return false;
        }

        var type = Compilation.GetSpecialType(specialType);
        if (type.TypeKind == TypeKind.Error)
        {
            typeInfo = default;
            return false;
        }

        typeInfo = new TypeInfo(type, type, ComputeConversion(type, type));
        return true;
    }

    private bool TryLookupAvailableTypeFromBinder(SimpleNameSyntax typeName, out ITypeSymbol? type)
    {
        var arity = typeName is GenericNameSyntax genericName
            ? genericName.TypeArgumentList.Arguments.Count
            : 0;

        using var sourceNamespaceLookupSuppression = Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();

        type = GetBinderForIncrementalSemanticQuery(typeName).LookupType(typeName.Identifier.ValueText);
        if (IsMatchingAvailableType(type, arity))
            return true;

        type = LookupImportedType(typeName, arity);
        if (type is not null)
            return true;

        var metadataName = GetMetadataTypeName(typeName.Identifier.ValueText, arity);
        type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(metadataName);
        return type is not null;
    }

    private ITypeSymbol? LookupImportedType(SimpleNameSyntax typeName, int arity)
    {
        for (var binder = GetBinderForIncrementalSemanticQuery(typeName); binder is not null; binder = binder.ParentBinder)
        {
            if (binder is ImportBinder importBinder)
            {
                foreach (var scope in importBinder.GetImportedNamespacesOrTypeScopes())
                {
                    var type = scope.LookupType(typeName.Identifier.ValueText);
                    if (IsMatchingAvailableType(type, arity))
                        return type;

                    if (scope is INamespaceSymbol namespaceSymbol &&
                        TryGetNamespaceMetadataName(namespaceSymbol, out var namespaceMetadataName) &&
                        Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(namespaceMetadataName + "." + GetMetadataTypeName(typeName.Identifier.ValueText, arity)) is { } metadataType)
                    {
                        return metadataType;
                    }
                }

                foreach (var importedType in importBinder.GetImportedTypes())
                {
                    if (string.Equals(importedType.Name, typeName.Identifier.ValueText, StringComparison.Ordinal) &&
                        IsMatchingAvailableType(importedType, arity))
                    {
                        return importedType;
                    }
                }
            }

            if (binder is NamespaceBinder namespaceBinder)
            {
                var type = namespaceBinder.NamespaceSymbol.LookupType(typeName.Identifier.ValueText);
                if (IsMatchingAvailableType(type, arity))
                    return type;
            }
        }

        return null;
    }

    private static string GetMetadataTypeName(string name, int arity)
        => arity > 0 ? name + "`" + arity : name;

    private static bool TryGetNamespaceMetadataName(INamespaceSymbol namespaceSymbol, out string metadataName)
    {
        metadataName = namespaceSymbol.ToMetadataName() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(metadataName);
    }

    private static bool IsMatchingAvailableType(ITypeSymbol? type, int arity)
        => type is not null &&
           type.TypeKind != TypeKind.Error &&
           (type is not INamedTypeSymbol namedType ||
            namedType.Arity == arity ||
            namedType.TypeParameters.Length == arity);

    private static bool HasTypeInfo(TypeInfo typeInfo)
        => typeInfo.Type is not null || typeInfo.ConvertedType is not null;

    private static bool HasNonErrorTypeInfo(TypeInfo typeInfo)
        => (typeInfo.Type is not null && typeInfo.Type.TypeKind != TypeKind.Error) ||
           (typeInfo.ConvertedType is not null && typeInfo.ConvertedType.TypeKind != TypeKind.Error);

    private bool TryGetCachedBoundSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        if (TryGetCachedBoundNode(node) is not { } boundNode)
        {
            info = default;
            return false;
        }

        info = boundNode switch
        {
            BoundExpression expression => expression.GetSymbolInfo(),
            BoundStatement statement => statement.GetSymbolInfo(),
            _ => default
        };
        if (!HasSymbolInfo(info))
            return false;

        Compilation.PerformanceInstrumentation.SemanticQuery.RecordSymbolInfoBoundCacheHit();
        info = ProjectBackingFieldSymbolsToAssociatedProperty(node, info);
        StoreSymbolMapping(node, info);
        return true;
    }

    private static bool HasSymbolInfo(SymbolInfo info)
        => info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;

    private static bool HasInvocationTargetSymbolInfo(SymbolInfo info)
        => info.Symbol is IMethodSymbol ||
           (!info.CandidateSymbols.IsDefaultOrEmpty &&
            info.CandidateSymbols.Any(static symbol => symbol is IMethodSymbol));

    private bool TryGetCachedNodeInterestSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        if (Compilation.TryGetNodeInterestSymbolDescriptor(node, out var cachedDescriptor) &&
            TryResolveNodeInterestSymbolDescriptor(cachedDescriptor, out var cachedSymbol))
        {
            info = new SymbolInfo(cachedSymbol);
            return true;
        }

        info = default;
        return false;
    }

    private bool TryGetDeclarationSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        var declarationNode = node switch
        {
            ParameterSyntax => node,
            VariableDeclaratorSyntax => node,
            SingleVariableDesignationSyntax => node,
            FunctionExpressionSyntax => node,
            IdentifierNameSyntax identifier => TryGetIdentifierDeclarationParent(identifier),
            _ => null
        };

        var symbol = declarationNode switch
        {
            null => null,
            FunctionExpressionSyntax functionExpression when TryGetFunctionExpressionSymbol(functionExpression, out var functionSymbol) => functionSymbol,
            ParameterSyntax parameter when TryResolveFunctionExpressionParameterSymbolFast(parameter, out var fastFunctionParameter) => fastFunctionParameter,
            ParameterSyntax parameter when parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any() => null,
            ParameterSyntax parameter => TryResolveParameterSymbolFast(parameter, out var parameterSymbol) ? parameterSymbol : GetDeclaredSymbol(parameter),
            VariableDeclaratorSyntax variableDeclarator when TryGetAvailableLocalDeclarationSymbol(variableDeclarator, out var localSymbol, allowErrorType: true) => localSymbol,
            SingleVariableDesignationSyntax designation when TryResolveAvailablePatternDesignationSymbol(designation, out var designationSymbol, allowErrorType: true) => designationSymbol,
            _ => GetDeclaredSymbol(declarationNode)
        };

        if (symbol is null)
        {
            info = default;
            return false;
        }

        info = new SymbolInfo(symbol);
        StoreSymbolInfo(node, symbol);
        return true;
    }

    private static SyntaxNode? TryGetIdentifierDeclarationParent(IdentifierNameSyntax identifier)
        => identifier.Parent switch
        {
            TypeDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            UnionDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            CaseDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            DelegateDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            MethodDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            ConstructorDeclarationSyntax declaration when declaration.InitKeyword.Span == identifier.Identifier.Span => declaration,
            ParameterlessConstructorDeclarationSyntax declaration when declaration.InitKeyword.Span == identifier.Identifier.Span => declaration,
            FunctionStatementSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            PropertyDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            EventDeclarationSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            AccessorDeclarationSyntax declaration when declaration.Keyword.Span == identifier.Identifier.Span => declaration,
            ParameterSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            VariableDeclaratorSyntax declaration when declaration.Identifier.Span == identifier.Identifier.Span => declaration,
            _ => null
        };

    private bool TryBindExactSymbol(SyntaxNode node, out SymbolInfo info)
    {
        var binder = GetBinderForIncrementalSemanticQuery(node);
        info = binder.BindReferencedSymbol(node);
        return info.Symbol is not null || !info.CandidateSymbols.IsDefaultOrEmpty;
    }

    private static bool IsSameSyntaxNode(SyntaxNode? left, SyntaxNode? right)
    {
        return ReferenceEquals(left, right) ||
               left is not null &&
               right is not null &&
               left.Kind == right.Kind &&
               left.Span == right.Span &&
               ReferenceEquals(left.SyntaxTree, right.SyntaxTree);
    }

    internal bool TryGetNodeInterestSymbolInfo(SyntaxNode node, out SymbolInfo info)
    {
        info = default;

        if (TryGetCachedNodeInterestSymbolInfo(node, out info))
            return true;

        switch (node)
        {
            case IdentifierNameSyntax identifier when TryGetPropertySubpatternMemberSymbol(identifier, out var propertyPatternMember):
                {
                    info = new SymbolInfo(propertyPatternMember);
                    StoreNodeInterestSymbolDescriptor(node, propertyPatternMember);
                    return true;
                }

            case MemberPatternPathSyntax memberPatternPath when TryGetCasePatternPathSymbol(memberPatternPath, out var memberPatternCaseSymbol):
                {
                    info = new SymbolInfo(memberPatternCaseSymbol);
                    StoreNodeInterestSymbolDescriptor(node, memberPatternCaseSymbol);
                    return true;
                }

            case IdentifierNameSyntax identifier when TryGetCasePatternHeadSymbol(identifier, out var casePatternSymbol):
                {
                    info = new SymbolInfo(casePatternSymbol);
                    StoreNodeInterestSymbolDescriptor(node, casePatternSymbol);
                    return true;
                }

            case GenericNameSyntax genericName when TryGetCasePatternHeadSymbol(genericName, out var genericCasePatternSymbol):
                {
                    info = new SymbolInfo(genericCasePatternSymbol);
                    StoreNodeInterestSymbolDescriptor(node, genericCasePatternSymbol);
                    return true;
                }

            case TypeSyntax typeSyntax when TryGetProjectedPatternTypeSymbol(typeSyntax, out var projectedPatternType):
                {
                    info = new SymbolInfo(projectedPatternType);
                    StoreNodeInterestSymbolDescriptor(node, projectedPatternType);
                    return true;
                }

            case IdentifierNameSyntax identifier:
                {
                    if (identifier.Parent is MemberAccessExpressionSyntax memberAccess)
                    {
                        if (ReferenceEquals(memberAccess.Name, identifier))
                            return false;

                        if (ReferenceEquals(memberAccess.Expression, identifier) &&
                            TryLookupVisibleValueSymbol(identifier) is { } receiverSymbol)
                        {
                            info = new SymbolInfo(receiverSymbol);
                            StoreNodeInterestSymbolDescriptor(node, receiverSymbol);
                            return true;
                        }
                    }

                    if (TryLookupVisibleValueSymbol(identifier) is { } visibleSymbol)
                    {
                        info = new SymbolInfo(visibleSymbol);
                        StoreNodeInterestSymbolDescriptor(node, visibleSymbol);
                        return true;
                    }

                    break;
                }

            case ParameterSyntax parameter:
                {
                    var parameterSymbol = TryResolveFunctionExpressionParameterSymbolFast(parameter, out var fastFunctionParameter)
                        ? fastFunctionParameter
                        : parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any()
                        ? null
                        : GetDeclaredSymbol(parameter);
                    if (parameterSymbol is not null)
                    {
                        info = new SymbolInfo(parameterSymbol);
                        StoreNodeInterestSymbolDescriptor(node, parameterSymbol);
                        return true;
                    }

                    break;
                }

            case VariableDeclaratorSyntax declarator:
                {
                    var localSymbol = GetDeclaredSymbol(declarator);
                    if (localSymbol is not null)
                    {
                        info = new SymbolInfo(localSymbol);
                        StoreNodeInterestSymbolDescriptor(node, localSymbol);
                        return true;
                    }

                    break;
                }

            case SingleVariableDesignationSyntax designation:
                {
                    var designatedSymbol = TryResolveAvailablePatternDesignationSymbol(designation, out var availableDesignatedSymbol, allowErrorType: true)
                        ? availableDesignatedSymbol
                        : GetDeclaredSymbol(designation);
                    if (designatedSymbol is not null)
                    {
                        info = new SymbolInfo(designatedSymbol);
                        StoreNodeInterestSymbolDescriptor(node, designatedSymbol);
                        return true;
                    }

                    break;
                }

            case FunctionExpressionSyntax functionExpression:
                {
                    if (TryGetFunctionExpressionSymbol(functionExpression, out var functionSymbol))
                    {
                        info = new SymbolInfo(functionSymbol);
                        StoreNodeInterestSymbolDescriptor(node, functionSymbol);
                        return true;
                    }

                    break;
                }
        }

        return false;
    }

    private bool TryGetPropertySubpatternMemberSymbol(IdentifierNameSyntax identifier, out ISymbol symbol)
    {
        symbol = null!;

        if (identifier.GetAncestor<PropertySubpatternSyntax>() is not { } subpattern ||
            identifier.GetAncestor<PropertyPatternSyntax>() is not { } propertyPattern)
        {
            return false;
        }

        var path = subpattern.MemberPath
            .Concat([subpattern.NameColon.Name])
            .ToImmutableArray();
        var segmentIndex = path.IndexOf(identifier);
        if (segmentIndex < 0)
            return false;

        BindPatternContextForSemanticQuery(propertyPattern);
        if (TryGetCachedBoundNode(propertyPattern) is not BoundPropertyPattern boundPattern)
            return false;

        var propertyIndex = -1;
        for (var i = 0; i < propertyPattern.PropertyPatternClause.Properties.Count; i++)
        {
            if (IsSameSyntaxNode(propertyPattern.PropertyPatternClause.Properties[i], subpattern))
            {
                propertyIndex = i;
                break;
            }
        }

        if ((uint)propertyIndex >= (uint)boundPattern.Properties.Length)
            return false;

        var boundProperty = boundPattern.Properties[propertyIndex];
        for (var i = 0; i < segmentIndex; i++)
        {
            if (boundProperty.Pattern is not BoundPropertyPattern nestedPattern ||
                nestedPattern.Properties.Length != 1)
            {
                return false;
            }

            boundProperty = nestedPattern.Properties[0];
        }

        if (boundProperty.Member.Kind is SymbolKind.Error or SymbolKind.ErrorType)
            return false;

        symbol = boundProperty.Member;
        return true;
    }

    private bool TryResolveMemberAccessFromVisibleReceiver(
        MemberAccessExpressionSyntax memberAccess,
        IdentifierNameSyntax memberName,
        out SymbolInfo info)
    {
        info = default;

        var receiverSymbol = TryLookupVisibleValueSymbol(memberAccess.Expression);
        var receiverType = GetTypeFromSymbol(receiverSymbol);
        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return false;

        var members = receiverType.GetMembers(memberName.Identifier.ValueText)
            .Where(static member => member is IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol)
            .ToImmutableArray();

        if (members.IsDefaultOrEmpty)
            return false;

        var selected = members.Length == 1
            ? members[0]
            : members.FirstOrDefault(static member => member is IPropertySymbol or IFieldSymbol or IEventSymbol);

        info = selected is not null
            ? new SymbolInfo(selected, members)
            : new SymbolInfo(CandidateReason.MemberGroup, members);
        return true;
    }

    private bool TryGetCasePatternPathSymbol(MemberPatternPathSyntax pathSyntax, out ISymbol symbol)
    {
        symbol = null!;

        if (pathSyntax.Parent is not MemberPatternSyntax memberPattern ||
            !IsSameSyntaxNode(memberPattern.Path, pathSyntax))
        {
            return false;
        }

        if (TryGetCasePatternSymbol(memberPattern, out symbol))
            return true;

        return TryResolveCasePatternSymbolFromContext(memberPattern, pathSyntax.Identifier.ValueText, out symbol);
    }

    private bool TryGetCasePatternHeadSymbol(SimpleNameSyntax nameSyntax, out ISymbol symbol)
    {
        symbol = null!;

        SyntaxNode? patternNode = nameSyntax.Parent switch
        {
            NominalDeconstructionPatternSyntax nominal when ReferenceEquals(nominal.Type, nameSyntax) => nominal,
            DeclarationPatternSyntax declaration when ReferenceEquals(declaration.Type, nameSyntax) => declaration,
            ConstantPatternSyntax constant when ReferenceEquals(constant.Expression, nameSyntax) => constant,
            MemberPatternSyntax memberPattern when nameSyntax.Parent is QualifiedNameSyntax qualified &&
                                                 ReferenceEquals(qualified.Right, nameSyntax) &&
                                                 ReferenceEquals(memberPattern.Path, qualified) => memberPattern,
            MemberPatternSyntax memberPattern when ReferenceEquals(memberPattern.Path, nameSyntax) => memberPattern,
            _ => null
        };

        if (patternNode is null)
            return false;

        if (TryGetCasePatternSymbol(patternNode, out symbol))
            return true;

        return TryResolveCasePatternSymbolFromContext(patternNode, nameSyntax.Identifier.ValueText, out symbol);
    }

    private bool TryGetCasePatternSymbol(SyntaxNode patternNode, out ISymbol symbol)
    {
        if (TryGetCachedBoundNode(patternNode) is not BoundCasePattern casePattern)
            BindPatternContextForSemanticQuery(patternNode);

        if (TryGetCachedBoundNode(patternNode) is not BoundCasePattern reboundCasePattern)
        {
            symbol = null!;
            return false;
        }

        symbol = reboundCasePattern.CaseSymbol;
        return true;
    }

    private void BindPatternContextForSemanticQuery(SyntaxNode patternNode)
    {
        if (patternNode.GetAncestor<MatchExpressionSyntax>() is { } matchExpression)
        {
            _ = GetBoundNode(matchExpression);
            return;
        }

        if (patternNode.GetAncestor<PostfixMatchExpressionSyntax>() is { } postfixMatchExpression)
        {
            _ = GetBoundNode(postfixMatchExpression);
            return;
        }

        if (patternNode.GetAncestor<MatchStatementSyntax>() is { } matchStatement)
        {
            _ = GetBoundNode(matchStatement);
            return;
        }

        if (patternNode.GetAncestor<IsPatternExpressionSyntax>() is { } isPatternExpression)
        {
            _ = GetBoundNode(isPatternExpression);
            return;
        }

        if (patternNode.GetAncestor<IfPatternStatementSyntax>() is { } ifPatternStatement)
        {
            _ = GetBoundNode(ifPatternStatement);
            return;
        }

        if (patternNode.GetAncestor<IfPatternExpressionSyntax>() is { } ifPatternExpression)
        {
            _ = GetBoundNode(ifPatternExpression);
            return;
        }

        if (patternNode.GetAncestor<WhilePatternStatementSyntax>() is { } whilePatternStatement)
        {
            _ = GetBoundNode(whilePatternStatement);
        }
    }

    private bool TryResolveCasePatternSymbolFromContext(SyntaxNode patternNode, string caseName, out ISymbol symbol)
    {
        symbol = null!;

        if (string.IsNullOrWhiteSpace(caseName))
            return false;

        ITypeSymbol? inputType = null;
        if (patternNode.GetAncestor<MatchExpressionSyntax>() is { } matchExpression)
        {
            inputType = GetPatternScrutineeType(matchExpression.Expression);
        }
        else if (patternNode.GetAncestor<PostfixMatchExpressionSyntax>() is { } postfixMatchExpression)
        {
            inputType = GetPatternScrutineeType(postfixMatchExpression.Expression);
        }
        else if (patternNode.GetAncestor<MatchStatementSyntax>() is { } matchStatement)
        {
            inputType = GetPatternScrutineeType(matchStatement.Expression);
        }
        else if (patternNode.GetAncestor<IsPatternExpressionSyntax>() is { } isPatternExpression)
        {
            inputType = GetPatternScrutineeType(isPatternExpression.Expression);
        }
        else if (patternNode.GetAncestor<IfPatternStatementSyntax>() is { } ifPatternStatement)
        {
            inputType = GetPatternScrutineeType(ifPatternStatement.Expression);
        }
        else if (patternNode.GetAncestor<IfPatternExpressionSyntax>() is { } ifPatternExpression)
        {
            inputType = GetPatternScrutineeType(ifPatternExpression.Value);
        }
        else if (patternNode.GetAncestor<WhilePatternStatementSyntax>() is { } whilePatternStatement)
        {
            inputType = GetPatternScrutineeType(whilePatternStatement.Expression);
        }

        if (inputType is null)
            return false;

        var union = inputType.TryGetUnion()
            ?? inputType.TryGetUnionCase()?.Union;

        if (union is null)
            return false;

        var caseSymbol = union.DeclaredCaseTypes
            .FirstOrDefault(c => string.Equals(c.Name, caseName, StringComparison.Ordinal));
        if (caseSymbol is null)
            return false;

        symbol = TryProjectCasePatternSymbol(inputType, caseSymbol, out var projectedCase)
            ? projectedCase
            : caseSymbol;
        return true;
    }

    private bool TryGetProjectedPatternTypeSymbol(TypeSyntax typeSyntax, out ISymbol symbol)
    {
        symbol = null!;

        if (!TryGetPatternNodeForType(typeSyntax, out var patternNode))
            return false;

        BindPatternContextForSemanticQuery(patternNode);

        if (TryGetCachedBoundNode(patternNode) is BoundDeclarationPattern { DeclaredType: INamedTypeSymbol declaredType } &&
            declaredType.TypeKind != TypeKind.Error)
        {
            symbol = declaredType;
            return true;
        }

        if (!TryGetPatternScrutineeType(patternNode, out var scrutineeType) ||
            !TryBindPatternTypeSyntax(typeSyntax, out var patternType) ||
            !IsOpenGenericPatternType(patternType) ||
            !TryProjectOpenGenericPatternType(patternType, scrutineeType, out var projectedPatternType))
        {
            return false;
        }

        symbol = projectedPatternType;
        return true;
    }

    private static bool TryGetPatternNodeForType(TypeSyntax typeSyntax, out SyntaxNode patternNode)
    {
        patternNode = null!;

        patternNode = typeSyntax.Parent switch
        {
            DeclarationPatternSyntax declaration when ReferenceEquals(declaration.Type, typeSyntax) => declaration,
            NominalDeconstructionPatternSyntax nominal when ReferenceEquals(nominal.Type, typeSyntax) => nominal,
            _ => null!
        };

        return patternNode is not null;
    }

    private bool TryGetPatternScrutineeType(SyntaxNode patternNode, out ITypeSymbol scrutineeType)
    {
        scrutineeType = null!;

        if (patternNode.GetAncestor<MatchExpressionSyntax>() is { } matchExpression)
            scrutineeType = GetPatternScrutineeType(matchExpression.Expression)!;
        else if (patternNode.GetAncestor<PostfixMatchExpressionSyntax>() is { } postfixMatchExpression)
            scrutineeType = GetPatternScrutineeType(postfixMatchExpression.Expression)!;
        else if (patternNode.GetAncestor<MatchStatementSyntax>() is { } matchStatement)
            scrutineeType = GetPatternScrutineeType(matchStatement.Expression)!;
        else if (patternNode.GetAncestor<IsPatternExpressionSyntax>() is { } isPatternExpression)
            scrutineeType = GetPatternScrutineeType(isPatternExpression.Expression)!;
        else if (patternNode.GetAncestor<IfPatternStatementSyntax>() is { } ifPatternStatement)
            scrutineeType = GetPatternScrutineeType(ifPatternStatement.Expression)!;
        else if (patternNode.GetAncestor<IfPatternExpressionSyntax>() is { } ifPatternExpression)
            scrutineeType = GetPatternScrutineeType(ifPatternExpression.Value)!;
        else if (patternNode.GetAncestor<WhilePatternStatementSyntax>() is { } whilePatternStatement)
            scrutineeType = GetPatternScrutineeType(whilePatternStatement.Expression)!;

        return scrutineeType is not null && scrutineeType.TypeKind != TypeKind.Error;
    }

    private bool TryBindPatternTypeSyntax(TypeSyntax typeSyntax, out INamedTypeSymbol patternType)
    {
        patternType = null!;

        Compilation.EnsureSourceDeclarationsDeclared();

        var binder = GetBinder(typeSyntax);
        try
        {
            var result = binder.BindTypeSyntax(typeSyntax);
            if (!result.Success || result.ResolvedType is not INamedTypeSymbol namedType)
                return false;

            patternType = namedType;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOpenGenericPatternType(INamedTypeSymbol patternType)
    {
        var definition = TypeSubstitution.GetDefinitionForSubstitution(patternType);
        if (definition.TypeParameters.IsDefaultOrEmpty || definition.TypeParameters.Length == 0)
            return false;

        var arguments = TypeSubstitution.GetShallowTypeArguments(patternType);
        return arguments.IsDefaultOrEmpty ||
               arguments.All(static argument => argument is ITypeParameterSymbol);
    }

    private static bool TryProjectOpenGenericPatternType(
        INamedTypeSymbol patternType,
        ITypeSymbol scrutineeType,
        out INamedTypeSymbol projectedPatternType)
    {
        projectedPatternType = null!;

        if (scrutineeType is not INamedTypeSymbol scrutineeNamed ||
            scrutineeNamed.TypeArguments.IsDefaultOrEmpty ||
            scrutineeNamed.TypeArguments.Any(static argument => argument is ITypeParameterSymbol))
        {
            return false;
        }

        var patternDefinition = TypeSubstitution.GetDefinitionForSubstitution(patternType);
        var patternParameters = patternDefinition.TypeParameters;
        if (patternParameters.IsDefaultOrEmpty || patternParameters.Length == 0)
            return false;

        foreach (var view in EnumeratePatternTypeViews(patternType))
        {
            if (!TryProjectOpenGenericPatternTypeFromView(
                    patternDefinition,
                    patternParameters,
                    view,
                    scrutineeNamed,
                    out projectedPatternType))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumeratePatternTypeViews(INamedTypeSymbol patternType)
    {
        yield return patternType;

        for (var baseType = patternType.BaseType; baseType is not null; baseType = baseType.BaseType)
            yield return baseType;

        foreach (var interfaceType in patternType.AllInterfaces)
            yield return interfaceType;
    }

    private static bool TryProjectOpenGenericPatternTypeFromView(
        INamedTypeSymbol patternDefinition,
        ImmutableArray<ITypeParameterSymbol> patternParameters,
        INamedTypeSymbol patternView,
        INamedTypeSymbol scrutineeType,
        out INamedTypeSymbol projectedPatternType)
    {
        projectedPatternType = null!;

        var patternViewDefinition = TypeSubstitution.GetDefinitionForSubstitution(patternView);
        var scrutineeDefinition = TypeSubstitution.GetDefinitionForSubstitution(scrutineeType);
        if (!SymbolEqualityComparer.Default.Equals(patternViewDefinition, scrutineeDefinition))
            return false;

        var viewArguments = TypeSubstitution.GetShallowTypeArguments(patternView);
        var scrutineeArguments = TypeSubstitution.GetShallowTypeArguments(scrutineeType);
        if (viewArguments.IsDefaultOrEmpty ||
            scrutineeArguments.IsDefaultOrEmpty ||
            viewArguments.Length != scrutineeArguments.Length)
        {
            return false;
        }

        var inferredArguments = new ITypeSymbol[patternParameters.Length];
        for (var i = 0; i < inferredArguments.Length; i++)
            inferredArguments[i] = patternParameters[i];

        var inferredAny = false;
        for (var i = 0; i < viewArguments.Length; i++)
        {
            if (viewArguments[i] is not ITypeParameterSymbol viewParameter)
                continue;

            var patternParameterIndex = IndexOfEquivalentTypeParameter(patternParameters, viewParameter);
            if (patternParameterIndex < 0)
                continue;

            inferredArguments[patternParameterIndex] = scrutineeArguments[i];
            inferredAny = true;
        }

        if (!inferredAny ||
            inferredArguments.Any(static argument => argument is ITypeParameterSymbol))
        {
            return false;
        }

        projectedPatternType = (INamedTypeSymbol)patternDefinition.Construct(inferredArguments);
        return true;
    }

    private static int IndexOfEquivalentTypeParameter(
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        ITypeParameterSymbol typeParameter)
    {
        for (var i = 0; i < typeParameters.Length; i++)
        {
            if (TypeSubstitution.AreEquivalentTypeParameters(typeParameters[i], typeParameter))
                return i;
        }

        return -1;
    }

    private ITypeSymbol? GetPatternScrutineeType(ExpressionSyntax expression)
    {
        var typeInfo = GetTypeInfo(expression);
        var expressionType = typeInfo.ConvertedType ?? typeInfo.Type;

        if (GetSymbolInfo(expression).Symbol is { } symbol &&
            TryGetValueSymbolType(symbol) is { } symbolType &&
            ShouldPreferValueSymbolTypeForPattern(expressionType, symbolType))
        {
            return symbolType;
        }

        return expressionType;
    }

    private static bool ShouldPreferValueSymbolTypeForPattern(ITypeSymbol? expressionType, ITypeSymbol symbolType)
    {
        if (expressionType is null || expressionType.TypeKind == TypeKind.Error)
            return true;

        if (ContainsUnboundTypeParameter(expressionType) &&
            !ContainsUnboundTypeParameter(symbolType))
        {
            return true;
        }

        return expressionType.TryGetUnionCase() is not null &&
               (symbolType.TryGetUnion() is not null || symbolType.TryGetUnionCase() is not null);
    }

    private static bool ContainsUnboundTypeParameter(ITypeSymbol type)
    {
        return type switch
        {
            ITypeParameterSymbol => true,
            INamedTypeSymbol named when !named.TypeArguments.IsDefaultOrEmpty =>
                named.TypeArguments.Any(ContainsUnboundTypeParameter),
            IArrayTypeSymbol array => ContainsUnboundTypeParameter(array.ElementType),
            IPointerTypeSymbol pointer => ContainsUnboundTypeParameter(pointer.PointedAtType),
            IAddressTypeSymbol address => ContainsUnboundTypeParameter(address.ReferencedType),
            _ => false
        };
    }

    private static ITypeSymbol? TryGetValueSymbolType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null
        };

    private static bool TryProjectCasePatternSymbol(
        ITypeSymbol inputType,
        IUnionCaseTypeSymbol caseSymbol,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IUnionCaseTypeSymbol? projectedCase)
    {
        projectedCase = null;

        var unionType = inputType.TryGetUnion() as INamedTypeSymbol;
        if (unionType is null &&
            inputType is INamedTypeSymbol inputCaseType &&
            inputCaseType.TryGetUnionCase() is { } inputCaseSymbol &&
            TryProjectUnionFromCaseArguments(inputCaseType, inputCaseSymbol, out var projectedUnion))
        {
            unionType = projectedUnion;
        }

        if (unionType is null || unionType.TypeArguments.IsDefaultOrEmpty)
            return false;

        var caseDefinition = caseSymbol.OriginalDefinition as IUnionCaseTypeSymbol ?? caseSymbol;
        if (caseDefinition is not INamedTypeSymbol caseDefinitionNamed ||
            caseDefinitionNamed.TypeParameters.IsDefaultOrEmpty)
        {
            return false;
        }

        var unionDefinition = unionType.OriginalDefinition as INamedTypeSymbol ?? unionType;
        if (unionDefinition.TypeParameters.IsDefaultOrEmpty ||
            unionDefinition.TypeParameters.Length != unionType.TypeArguments.Length)
        {
            return false;
        }

        var caseTypeArguments = caseDefinitionNamed.TypeParameters
            .Select(typeParameter => (ITypeSymbol)typeParameter)
            .ToArray();

        var changed = false;
        for (var i = 0; i < caseDefinitionNamed.TypeParameters.Length; i++)
        {
            var caseTypeParameter = caseDefinitionNamed.TypeParameters[i];
            ITypeParameterSymbol? unionTypeParameter = null;

            if (caseDefinition is SourceUnionCaseTypeSymbol sourceCaseDefinition &&
                sourceCaseDefinition.TryGetProjectedUnionTypeParameter(caseTypeParameter, out var mapped))
            {
                unionTypeParameter = mapped;
            }
            else
            {
                unionTypeParameter = unionDefinition.TypeParameters
                    .FirstOrDefault(tp => string.Equals(tp.Name, caseTypeParameter.Name, StringComparison.Ordinal));
            }

            if (unionTypeParameter is null)
                continue;

            var unionIndex = -1;
            for (var unionParameterIndex = 0; unionParameterIndex < unionDefinition.TypeParameters.Length; unionParameterIndex++)
            {
                if (SymbolEqualityComparer.Default.Equals(unionDefinition.TypeParameters[unionParameterIndex], unionTypeParameter))
                {
                    unionIndex = unionParameterIndex;
                    break;
                }
            }

            if (unionIndex < 0 || unionIndex >= unionType.TypeArguments.Length)
                continue;

            caseTypeArguments[i] = unionType.TypeArguments[unionIndex];
            changed = true;
        }

        if (!changed)
            return false;

        projectedCase = (IUnionCaseTypeSymbol)caseDefinitionNamed.Construct(caseTypeArguments);
        return true;
    }

    private bool TryRebindInvocationAfterRefreshingFunctionArguments(
        InvocationExpressionSyntax invocation,
        out SymbolInfo info)
    {
        info = default;

        if (!InvocationContainsFunctionArguments(invocation))
            return false;

        var refreshedAnyFunction = false;
        foreach (var functionExpression in invocation.ArgumentList.Arguments
                     .SelectMany(static argument => argument.Expression.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>()))
        {
            refreshedAnyFunction |= TryGetFunctionExpressionSymbol(functionExpression, out _);
        }

        if (!refreshedAnyFunction)
            return false;

        if (TryGetContextualBindingRoot(invocation, out var contextualRoot) &&
            !ReferenceEquals(contextualRoot, invocation))
        {
            ClearCachedSemanticState(contextualRoot);
            var reboundRoot = BindContextualRootForSemanticQuery(contextualRoot);
            if (TryFindBoundNodeBySyntax(reboundRoot, invocation, out var reboundNode) &&
                reboundNode is BoundExpression reboundExpression)
            {
                var reboundInfo = reboundExpression.GetSymbolInfo();
                if (reboundInfo.Symbol is not null || !reboundInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    info = reboundInfo;
                    return true;
                }

            }
        }

        ClearCachedSemanticState(invocation);
        var refreshedInvocation = GetBoundNode(invocation);
        var refreshedInfo = refreshedInvocation.GetSymbolInfo();
        if (refreshedInfo.Symbol is not null || !refreshedInfo.CandidateSymbols.IsDefaultOrEmpty)
        {
            info = refreshedInfo;
            return true;
        }

        return false;
    }

    private bool TryRebindExpressionFromEnclosingFunctionContext(
        ExpressionSyntax expression,
        out SymbolInfo info)
    {
        info = default;

        if (!TryGetEnclosingFunctionExpression(expression, out var enclosingFunctionExpression))
            return false;

        var rebindRoot = GetFunctionExpressionRebindRoot(enclosingFunctionExpression);
        ClearCachedSemanticState(rebindRoot);

        var reboundRoot = GetBoundNode(rebindRoot, BoundTreeView.Original);
        if (!TryFindBoundNodeBySyntax(reboundRoot, expression, out var reboundNode) ||
            reboundNode is not BoundExpression reboundExpression)
        {
            return false;
        }

        var reboundInfo = reboundExpression.GetSymbolInfo();
        if (reboundInfo.Symbol is null && reboundInfo.CandidateSymbols.IsDefaultOrEmpty)
            return false;

        info = reboundInfo;
        return true;
    }

    private static bool InvocationContainsFunctionArguments(InvocationExpressionSyntax invocation)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>().Any())
                return true;
        }

        return false;
    }

    private bool TryLookupPipelineInvocationSymbol(
        InvocationExpressionSyntax invocation,
        SyntaxNode contextNode,
        string methodName,
        out SymbolInfo info)
    {
        info = default;

        if (invocation.Parent is not InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression ||
            !IsSameSyntaxNode(pipeExpression.Right, invocation))
        {
            return false;
        }

        if (TryGetPipeInvocationSymbolInfo(contextNode, out info) ||
            TryGetPipeInvocationSymbolInfo(invocation, out info) ||
            TryGetPipeInvocationSymbolInfo(pipeExpression, out info))
        {
            return info.Symbol is IMethodSymbol method &&
                   string.Equals(method.Name, methodName, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsExpressionTreeDelegateParameter(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var definition = (named.OriginalDefinition as INamedTypeSymbol) ?? named;
        return string.Equals(definition.Name, "Expression", StringComparison.Ordinal) ||
               string.Equals(definition.MetadataName, "Expression`1", StringComparison.Ordinal);
    }

    private bool TryResolveNodeInterestSymbolDescriptor(
        Compilation.NodeInterestSymbolDescriptor descriptor,
        out ISymbol symbol)
    {
        var candidate = SyntaxTree.GetRoot().FindNode(descriptor.ReferencedSpan, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind != descriptor.ReferencedKind || current.Span != descriptor.ReferencedSpan)
                continue;

            symbol = current switch
            {
                ParameterSyntax parameter when parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any()
                    => null!,
                ParameterSyntax parameter when TryResolveParameterSymbolFast(parameter, out var parameterSymbol)
                    => parameterSymbol!,
                VariableDeclaratorSyntax variableDeclarator when variableDeclarator.Initializer?.Value.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>().Any() == true
                    => null!,
                VariableDeclaratorSyntax variableDeclarator when TryGetAvailableLocalDeclarationSymbol(variableDeclarator, out var localSymbol, allowErrorType: true)
                    => localSymbol!,
                SingleVariableDesignationSyntax designation when TryResolveAvailablePatternDesignationSymbol(designation, out var designationSymbol, allowErrorType: true)
                    => designationSymbol!,
                FunctionExpressionSyntax functionExpression when TryGetFunctionExpressionSymbol(functionExpression, out var functionSymbol)
                    => functionSymbol!,
                SyntaxNode declaredNode when TryResolveAvailableDeclaredSymbol(declaredNode, out var declaredSymbol)
                    => declaredSymbol!,
                _ => GetDeclaredSymbol(current)!
            };

            if (symbol is not null)
                return true;
        }

        symbol = null!;
        return false;
    }

    private void StoreNodeInterestSymbolDescriptor(SyntaxNode queryNode, ISymbol symbol)
    {
        if (TryCreateNodeInterestSymbolDescriptor(symbol, out var descriptor))
        {
            Compilation.StoreNodeInterestSymbolDescriptor(queryNode, descriptor);
        }
    }

    private bool TryCreateNodeInterestSymbolDescriptor(
        ISymbol symbol,
        out Compilation.NodeInterestSymbolDescriptor descriptor)
    {
        var declarationReference = symbol.DeclaringSyntaxReferences.FirstOrDefault(reference => reference.SyntaxTree == SyntaxTree);
        if (declarationReference?.GetSyntax() is not SyntaxNode declarationNode)
        {
            descriptor = default;
            return false;
        }

        if (declarationNode is ParameterSyntax parameter &&
            parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any())
        {
            descriptor = default;
            return false;
        }

        if (declarationNode is VariableDeclaratorSyntax variableDeclarator &&
            variableDeclarator.Initializer?.Value.DescendantNodesAndSelf().OfType<FunctionExpressionSyntax>().Any() == true)
        {
            descriptor = default;
            return false;
        }

        descriptor = new Compilation.NodeInterestSymbolDescriptor(declarationNode.Span, declarationNode.Kind);
        return true;
    }

    private static SymbolInfo ProjectBackingFieldSymbolsToAssociatedProperty(SyntaxNode node, SymbolInfo info)
    {
        if (!TryProjectSymbol(node, info.Symbol, out var projectedSymbol))
            return info;

        var candidatesChanged = false;
        ImmutableArray<ISymbol> projectedCandidates;

        if (info.CandidateSymbols.IsDefaultOrEmpty)
        {
            projectedCandidates = projectedSymbol is null
                ? ImmutableArray<ISymbol>.Empty
                : ImmutableArray.Create(projectedSymbol);
            candidatesChanged = true;
        }
        else
        {
            var builder = ImmutableArray.CreateBuilder<ISymbol>(info.CandidateSymbols.Length);

            foreach (var candidate in info.CandidateSymbols)
            {
                if (TryProjectSymbol(node, candidate, out var projectedCandidate))
                {
                    if (projectedCandidate is not null && !builder.Contains(projectedCandidate, SymbolEqualityComparer.Default))
                        builder.Add(projectedCandidate);
                    candidatesChanged = true;
                }
                else if (!builder.Contains(candidate, SymbolEqualityComparer.Default))
                {
                    builder.Add(candidate);
                }
            }

            projectedCandidates = builder.ToImmutable();
        }

        if (!candidatesChanged && SymbolEqualityComparer.Default.Equals(info.Symbol, projectedSymbol))
            return info;

        return new SymbolInfo(projectedSymbol, projectedCandidates, info.CandidateReason);
    }

    private static bool TryProjectSymbol(SyntaxNode node, ISymbol? symbol, out ISymbol? projected)
    {
        projected = symbol;

        if (node is IdentifierNameSyntax identifierName &&
            !IsExplicitMemberName(identifierName) &&
            TryGetPrimaryConstructorCaptureParameter(symbol, out var primaryParameter))
        {
            projected = primaryParameter;
            return true;
        }

        if (symbol is IMethodSymbol methodSymbol &&
            methodSymbol.AssociatedSymbol is { } associatedMemberSymbol &&
            associatedMemberSymbol is IPropertySymbol or IEventSymbol)
        {
            projected = associatedMemberSymbol;
            return true;
        }

        if (symbol is not IFieldSymbol fieldSymbol)
            return false;

        if (node is IdentifierNameSyntax identifier &&
            string.Equals(identifier.Identifier.ValueText, "field", StringComparison.Ordinal))
        {
            return false;
        }

        if (fieldSymbol.AssociatedSymbol is { } associatedSymbol &&
            associatedSymbol is IPropertySymbol or IEventSymbol)
        {
            projected = associatedSymbol;
            return true;
        }

        return false;
    }

    private static bool IsExplicitMemberName(IdentifierNameSyntax identifier)
    {
        return identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
               ReferenceEquals(memberAccess.Name, identifier);
    }

    private static bool TryGetPrimaryConstructorCaptureParameter(ISymbol? symbol, out IParameterSymbol? parameter)
    {
        parameter = null;

        while (symbol is not null)
        {
            switch (symbol)
            {
                case SourceFieldSymbol sourceField when sourceField.Initializer is BoundParameterAccess parameterAccess:
                    parameter = parameterAccess.Parameter;
                    return true;
                case SourcePropertySymbol sourceProperty when sourceProperty.BackingField?.Initializer is BoundParameterAccess parameterAccess:
                    parameter = parameterAccess.Parameter;
                    return true;
            }

            var underlying = symbol.UnderlyingSymbol;
            if (ReferenceEquals(underlying, symbol))
                break;

            symbol = underlying;
        }

        return false;
    }

    /// <summary>
    /// Given a syntax node that declares a method, property, or member accessor, get the corresponding symbol.
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    public ISymbol? GetDeclaredSymbol(SyntaxNode node)
    {
        ValidateSyntaxNode(node, nameof(node));

        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        if (node is FunctionStatementSyntax functionStatement &&
            TryResolveAvailableFunctionStatementSymbol(functionStatement, out var functionStatementSymbol))
        {
            StoreSymbolInfo(node, functionStatementSymbol);
            return functionStatementSymbol;
        }

        if (RequiresCompleteSourceDeclarationSymbol(node))
            Compilation.EnsureSourceDeclarationsComplete();

        switch (node)
        {
            case SyntaxNode declaredNode when TryResolveAvailableDeclaredSymbol(declaredNode, out var declaredSymbol):
                StoreSymbolInfo(node, declaredSymbol);
                return declaredSymbol;

            case ParameterSyntax parameter
                when TryResolveFunctionExpressionParameterSymbolFast(parameter, out var functionParameterSymbol):
                StoreSymbolInfo(node, functionParameterSymbol);
                return functionParameterSymbol;

            case ParameterSyntax parameter
                when TryResolveParameterSymbolFast(parameter, out var parameterSymbol):
                StoreSymbolInfo(node, parameterSymbol);
                return parameterSymbol;

            case ParameterSyntax parameter
                when TryResolveAvailableParameterSymbol(parameter, out var availableParameterSymbol):
                StoreSymbolInfo(node, availableParameterSymbol);
                return availableParameterSymbol;

            case VariableDeclaratorSyntax variableDeclarator
                when TryGetAvailableLocalDeclarationSymbol(variableDeclarator, out var localSymbol, allowErrorType: true):
                StoreSymbolInfo(node, localSymbol);
                return localSymbol;

            case SingleVariableDesignationSyntax designation
                when TryResolveAvailablePatternDesignationSymbol(designation, out var designationSymbol, allowErrorType: true):
                StoreSymbolInfo(node, designationSymbol);
                return designationSymbol;
        }

        var symbol = _declaredSymbolLookup.Lookup(node);
        if (symbol is not null)
            StoreSymbolInfo(node, symbol);

        return symbol;
    }

    private static bool RequiresCompleteSourceDeclarationSymbol(SyntaxNode node)
        => node is TypeDeclarationSyntax
            or UnionDeclarationSyntax
            or CaseDeclarationSyntax
            or MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or ParameterlessConstructorDeclarationSyntax
            or FunctionStatementSyntax
            or MacroDeclarationSyntax
            or PropertyDeclarationSyntax
            or EventDeclarationSyntax
            or AccessorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or ParameterSyntax { Parent.Parent: not LocalDeclarationStatementSyntax };

    private static bool IsTopLevelFunctionMember(FunctionStatementSyntax functionStatement)
        => functionStatement.Parent is GlobalStatementSyntax globalStatement &&
           Compilation.IsTopLevelFunctionMember(globalStatement);

    private bool TryResolveAvailableDeclaredSymbol(SyntaxNode node, out ISymbol? symbol)
    {
        switch (node)
        {
            case TypeDeclarationSyntax typeDeclaration when TryGetClassSymbol(typeDeclaration, out var typeSymbol):
                symbol = typeSymbol;
                return true;

            case UnionDeclarationSyntax unionDeclaration when TryGetUnionSymbol(unionDeclaration, out var unionSymbol):
                symbol = unionSymbol;
                return true;

            case CaseDeclarationSyntax caseDeclaration when TryGetUnionCaseSymbol(caseDeclaration, out var caseSymbol):
                symbol = caseSymbol;
                return true;

            case MethodDeclarationSyntax methodDeclaration when TryGetMethodSymbol(methodDeclaration, out var methodSymbol):
                symbol = methodSymbol;
                return true;

            case MacroDeclarationSyntax macroDeclaration
                when TryGetMacroSymbol(macroDeclaration, out var macroSymbol):
                symbol = macroSymbol;
                return true;

            case ConstructorDeclarationSyntax constructorDeclaration when TryGetMethodSymbol(constructorDeclaration, out var constructorSymbol):
                symbol = constructorSymbol;
                return true;

            case ParameterlessConstructorDeclarationSyntax constructorDeclaration when TryGetMethodSymbol(constructorDeclaration, out var constructorSymbol):
                symbol = constructorSymbol;
                return true;

            case PropertyDeclarationSyntax propertyDeclaration when TryGetPropertySymbol(propertyDeclaration, out var propertySymbol):
                symbol = propertySymbol;
                return true;

            case EventDeclarationSyntax eventDeclaration when TryGetEventSymbol(eventDeclaration, out var eventSymbol):
                symbol = eventSymbol;
                return true;

            case AccessorDeclarationSyntax accessorDeclaration when TryResolveAvailableAccessorSymbol(accessorDeclaration, out var accessorSymbol):
                symbol = accessorSymbol;
                return true;

            default:
                symbol = null;
                return false;
        }
    }

    private bool TryResolveAvailableFunctionStatementSymbol(
        FunctionStatementSyntax functionStatement,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? methodSymbol)
    {
        if (_declaredSymbolLookup.Lookup(functionStatement) is IMethodSymbol declaredMethod)
        {
            if (declaredMethod is not SourceMethodSymbol { IsSignatureSkeleton: true })
            {
                methodSymbol = declaredMethod;
                return true;
            }
        }

        if (GetBinderForIncrementalSemanticQuery(functionStatement) is FunctionBinder functionBinder)
        {
            methodSymbol = functionBinder.GetMethodSymbol();
            return true;
        }

        methodSymbol = null;
        return false;
    }

    private bool TryGetAvailableNamespaceFunctionSymbol(
        FunctionStatementSyntax functionStatement,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? methodSymbol)
    {
        if (Compilation.TryGetMethodSymbol(functionStatement, out methodSymbol) &&
            methodSymbol is not null &&
            methodSymbol is not SourceMethodSymbol { IsSignatureSkeleton: true })
        {
            return true;
        }

        if (functionStatement.Parent is not GlobalStatementSyntax globalStatement ||
            !Compilation.IsTopLevelFunctionMember(globalStatement) ||
            Compilation.IsFileScopeLocalFunction(globalStatement))
        {
            methodSymbol = null;
            return false;
        }

        if (!TryGetDeclarationNamespace(functionStatement, out var parentNamespace))
        {
            methodSymbol = null;
            return false;
        }

        Compilation.EnsureSourceTypeDeclarationsDeclared();
        DeclareTopLevelFunctionSymbol(functionStatement, parentNamespace);
        return Compilation.TryGetMethodSymbol(functionStatement, out methodSymbol) && methodSymbol is not null;
    }

    private bool TryResolveAvailableAccessorSymbol(AccessorDeclarationSyntax accessorDeclaration, out IMethodSymbol? accessorSymbol)
    {
        accessorSymbol = null;

        if (accessorDeclaration.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } propertyDeclaration &&
            TryGetPropertySymbol(propertyDeclaration, out var propertySymbol))
        {
            accessorSymbol = accessorDeclaration.Kind == SyntaxKind.GetAccessorDeclaration ||
                             accessorDeclaration.Keyword.Kind == SyntaxKind.GetKeyword
                ? propertySymbol.GetMethod
                : propertySymbol.SetMethod;
            return accessorSymbol is not null;
        }

        if (accessorDeclaration.Ancestors().OfType<EventDeclarationSyntax>().FirstOrDefault() is { } eventDeclaration &&
            TryGetEventSymbol(eventDeclaration, out var eventSymbol))
        {
            accessorSymbol = accessorDeclaration.Kind == SyntaxKind.AddAccessorDeclaration ||
                             accessorDeclaration.Keyword.Kind == SyntaxKind.AddKeyword
                ? eventSymbol.AddMethod
                : eventSymbol.RemoveMethod;
            return accessorSymbol is not null;
        }

        return false;
    }

    internal void ReportTopLevelFunctionAlreadyDefined(string name, Location location)
        => _declarationDiagnostics.ReportFunctionAlreadyDefined(name, location);

    internal void ReportDeclarationAsyncReturnTypeMustBeTaskLike(
        string returnType,
        string suggestedReturnType,
        Location location)
        => _declarationDiagnostics.ReportAsyncReturnTypeMustBeTaskLike(
            returnType,
            suggestedReturnType,
            location);

    internal void ReportDeclarationParameterTypeAnnotationRequired(
        string parameterName,
        Location location)
        => _declarationDiagnostics.ReportParameterTypeAnnotationRequired(
            parameterName,
            location);

    internal void ReportDeclarationScopedModifierRequiresRefLikeTypeOrReference(Location location)
        => _declarationDiagnostics.ReportScopedModifierRequiresRefLikeTypeOrReference(location);

    internal void ReportDeclarationParameterBindingKeywordNotAllowed(
        string keyword,
        string parameterName,
        Location location)
        => _declarationDiagnostics.ReportParameterBindingKeywordNotAllowed(
            keyword,
            parameterName,
            location);

    internal void ReportDeclarationNameDoesNotExist(
        string name,
        Location location)
        => _declarationDiagnostics.ReportTheNameDoesNotExistInTheCurrentContext(
            name,
            location);

    private void StoreSymbolInfo(SyntaxNode node, ISymbol symbol)
    {
        StoreSymbolMapping(node, new SymbolInfo(symbol));
        StoreNodeInterestSymbolDescriptor(node, symbol);
    }

    internal bool TryGetStableLocalDeclarationSymbol(
        VariableDeclaratorSyntax variableDeclarator,
        out ILocalSymbol? localSymbol)
    {
        if (!IsLocalVariableDeclarator(variableDeclarator))
        {
            localSymbol = null;
            return false;
        }

        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator cachedDeclarator &&
            IsCompleteLocalDeclarationType(cachedDeclarator.Local.Type))
        {
            localSymbol = cachedDeclarator.Local;
            return true;
        }

        if (TryBindLocalDeclarationForStableLocalSymbol(variableDeclarator, out localSymbol))
            return true;

        if (variableDeclarator.Initializer is null)
        {
            localSymbol = null;
            return false;
        }

        var interestRoot = GetInterestBindingRoot(variableDeclarator, includeExtendedExecutableRoots: true);
        if (interestRoot is null)
        {
            localSymbol = null;
            return false;
        }

        ClearCachedSemanticState(interestRoot);
        PrimeContextualFunctionExpressions(interestRoot);
        _ = BindContextualRootForSemanticQuery(interestRoot);

        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator reboundDeclarator &&
            IsCompleteLocalDeclarationType(reboundDeclarator.Local.Type))
        {
            localSymbol = reboundDeclarator.Local;
            return true;
        }

        localSymbol = null;
        return false;
    }

    internal bool TryGetAvailableLocalDeclarationSymbol(
        VariableDeclaratorSyntax variableDeclarator,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSymbol? localSymbol,
        bool allowErrorType = false,
        bool allowInitializerBinding = true,
        bool allowBindingFallback = true)
    {
        if (!IsLocalVariableDeclarator(variableDeclarator))
        {
            localSymbol = null;
            return false;
        }

        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator cachedDeclarator &&
            (allowErrorType || !cachedDeclarator.Local.Type.ContainsErrorType()))
        {
            if (CanReturnLocalDeclarationSymbol(cachedDeclarator.Local.Type, allowErrorType, allowInitializerBinding))
            {
                localSymbol = cachedDeclarator.Local;
                return true;
            }
        }

        return TryBindLocalDeclarationForStableLocalSymbol(
            variableDeclarator,
            out localSymbol,
            allowErrorType,
            allowInitializerBinding,
            allowBindingFallback);
    }

    private static bool IsLocalVariableDeclarator(VariableDeclaratorSyntax variableDeclarator)
        => variableDeclarator.Ancestors().Any(static ancestor =>
            ancestor is LocalDeclarationStatementSyntax or UseDeclarationStatementSyntax);

    private bool TryBindLocalDeclarationForStableLocalSymbol(
        VariableDeclaratorSyntax variableDeclarator,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSymbol? localSymbol,
        bool allowErrorType = false,
        bool allowInitializerBinding = true,
        bool allowBindingFallback = true,
        bool allowBoundInitializerBindingWithoutFallback = false)
    {
        var query = _availableLocalBindingQuery.Value;
        if (query is null)
        {
            query = new AvailableLocalBindingQuery();
            _availableLocalBindingQuery.Value = query;
        }

        query.Depth++;
        var key = new AvailableLocalBindingKey(
            variableDeclarator,
            allowErrorType,
            allowInitializerBinding,
            allowBindingFallback,
            allowBoundInitializerBindingWithoutFallback);
        if (query.Failed.Contains(key) || !query.Active.Add(key))
        {
            query.Depth--;
            if (query.Depth == 0)
                _availableLocalBindingQuery.Value = null;
            localSymbol = null;
            return false;
        }

        try
        {
            var result = TryBindLocalDeclarationForStableLocalSymbolCore(
                variableDeclarator,
                out localSymbol,
                allowErrorType,
                allowInitializerBinding,
                allowBindingFallback,
                allowBoundInitializerBindingWithoutFallback);
            if (!result)
                query.Failed.Add(key);
            return result;
        }
        finally
        {
            query.Active.Remove(key);
            query.Depth--;
            if (query.Depth == 0)
                _availableLocalBindingQuery.Value = null;
        }
    }

    private bool TryBindLocalDeclarationForStableLocalSymbolCore(
        VariableDeclaratorSyntax variableDeclarator,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSymbol? localSymbol,
        bool allowErrorType,
        bool allowInitializerBinding,
        bool allowBindingFallback,
        bool allowBoundInitializerBindingWithoutFallback)
    {
        if (!allowBindingFallback)
        {
            var queryBinder = GetBinderForIncrementalSemanticQuery(variableDeclarator);
            if (TryGetNearestBlockBinder(queryBinder, out var queryBlockBinder))
            {
                var availableLocal = queryBlockBinder.TryDeclareLocalSymbolShallow(
                    variableDeclarator,
                    allowInitializerBinding,
                    allowBoundInitializerBinding: allowBoundInitializerBindingWithoutFallback,
                    ensurePrecedingDeclarations: false);
                if (availableLocal is not null &&
                    (allowErrorType || !availableLocal.Type.ContainsErrorType()))
                {
                    if (!CanReturnLocalDeclarationSymbol(availableLocal.Type, allowErrorType, allowInitializerBinding))
                    {
                        localSymbol = null;
                        return false;
                    }

                    localSymbol = availableLocal;
                    return true;
                }
            }

            localSymbol = null;
            return false;
        }

        EnsureDeclarations();

        if (variableDeclarator.Ancestors().OfType<FunctionExpressionSyntax>().FirstOrDefault() is { } functionExpression)
        {
            var functionExpressionRoot = GetFunctionExpressionRebindRoot(functionExpression);
            if (functionExpressionRoot.SyntaxTree.GetRoot() is CompilationUnitSyntax functionExpressionCompilationUnit)
                EnsureTopLevelFunctionDeclarations(functionExpressionCompilationUnit);

            PrimeContextualFunctionExpressions(functionExpressionRoot);
            var contextualBoundRoot = BindContextualRootForSemanticQuery(functionExpressionRoot);

            if (TryGetLocalFromContextualBoundRoot(contextualBoundRoot, out localSymbol))
                return true;

            if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator functionExpressionDeclarator &&
                (allowErrorType || !functionExpressionDeclarator.Local.Type.ContainsErrorType()))
            {
                if (CanReturnLocalDeclarationSymbol(functionExpressionDeclarator.Local.Type, allowErrorType, allowInitializerBinding))
                {
                    localSymbol = functionExpressionDeclarator.Local;
                    return true;
                }

                ClearCachedSemanticState(functionExpressionRoot);
                PrimeContextualFunctionExpressions(functionExpressionRoot);
                contextualBoundRoot = BindContextualRootForSemanticQuery(functionExpressionRoot);

                if (TryGetLocalFromContextualBoundRoot(contextualBoundRoot, out localSymbol))
                    return true;

                if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator reboundFunctionExpressionDeclarator &&
                    (allowErrorType || !reboundFunctionExpressionDeclarator.Local.Type.ContainsErrorType()) &&
                    CanReturnLocalDeclarationSymbol(reboundFunctionExpressionDeclarator.Local.Type, allowErrorType, allowInitializerBinding))
                {
                    localSymbol = reboundFunctionExpressionDeclarator.Local;
                    return true;
                }
            }

            bool TryGetLocalFromContextualBoundRoot(
                BoundNode boundRoot,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ILocalSymbol? contextualLocal)
            {
                if (TryFindBoundNodeBySyntax(boundRoot, variableDeclarator, out var boundNode) &&
                    boundNode is BoundVariableDeclarator contextualDeclarator &&
                    (allowErrorType || !contextualDeclarator.Local.Type.ContainsErrorType()) &&
                    CanReturnLocalDeclarationSymbol(contextualDeclarator.Local.Type, allowErrorType, allowInitializerBinding))
                {
                    contextualLocal = contextualDeclarator.Local;
                    return true;
                }

                contextualLocal = null;
                return false;
            }
        }

        var binder = GetBinderForIncrementalSemanticQuery(variableDeclarator);
        if (TryGetNearestBlockBinder(binder, out var blockBinder))
        {
            var shallowLocal = blockBinder.TryDeclareLocalSymbolShallow(
                variableDeclarator,
                allowInitializerBinding,
                allowBoundInitializerBinding: allowBindingFallback);
            if (shallowLocal is not null &&
                (allowErrorType || !shallowLocal.Type.ContainsErrorType()))
            {
                if (CanReturnLocalDeclarationSymbol(shallowLocal.Type, allowErrorType, allowInitializerBinding))
                {
                    localSymbol = shallowLocal;
                    return true;
                }
            }
        }

        if (!allowInitializerBinding || !allowBindingFallback)
        {
            localSymbol = null;
            return false;
        }

        ILocalSymbol? errorLocal = null;
        var declaredSymbol = binder.BindDeclaredSymbol(variableDeclarator);
        if (declaredSymbol is ILocalSymbol declaredLocal &&
            (allowErrorType || !declaredLocal.Type.ContainsErrorType()))
        {
            if (CanReturnLocalDeclarationSymbol(declaredLocal.Type, allowErrorType, allowInitializerBinding))
            {
                localSymbol = declaredLocal;
                return true;
            }

            errorLocal = declaredLocal;
        }

        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator reboundDeclarator &&
            !reboundDeclarator.Local.Type.ContainsErrorType())
        {
            if (CanReturnLocalDeclarationSymbol(reboundDeclarator.Local.Type, allowErrorType, allowInitializerBinding))
            {
                localSymbol = reboundDeclarator.Local;
                return true;
            }

            errorLocal ??= reboundDeclarator.Local;
        }

        if (allowInitializerBinding &&
            errorLocal is not null &&
            variableDeclarator.Initializer is not null &&
            TryRebindLocalDeclarationFromInterestRoot(variableDeclarator, allowErrorType: false, out var reboundLocal))
        {
            localSymbol = reboundLocal;
            return true;
        }

        if (allowErrorType && errorLocal is not null)
        {
            localSymbol = GetCanonicalLocalDeclarationSymbol(variableDeclarator, errorLocal);
            return true;
        }

        localSymbol = null;
        return false;
    }

    private static bool TryGetNearestBlockBinder(
        Binder? binder,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlockBinder? blockBinder)
    {
        for (var current = binder; current is not null; current = current.ParentBinder)
        {
            if (current is BlockBinder currentBlockBinder)
            {
                blockBinder = currentBlockBinder;
                return true;
            }
        }

        blockBinder = null;
        return false;
    }

    private bool TryRebindLocalDeclarationFromInterestRoot(
        VariableDeclaratorSyntax variableDeclarator,
        bool allowErrorType,
        out ILocalSymbol localSymbol)
    {
        var interestRoot = GetInterestBindingRoot(variableDeclarator, includeExtendedExecutableRoots: true);
        if (interestRoot is null)
        {
            localSymbol = null!;
            return false;
        }

        ClearCachedSemanticState(interestRoot);
        var reboundRoot = BindContextualRootForSemanticQuery(interestRoot);

        if (TryFindBoundNodeBySyntax(reboundRoot, variableDeclarator, out var reboundNode) &&
            reboundNode is BoundVariableDeclarator contextualDeclarator &&
            CanReturnLocalDeclarationSymbol(contextualDeclarator.Local.Type, allowErrorType, allowInitializerBinding: true))
        {
            localSymbol = GetCanonicalLocalDeclarationSymbol(variableDeclarator, contextualDeclarator.Local);
            return true;
        }

        if (TryGetCachedBoundNode(variableDeclarator) is BoundVariableDeclarator reboundDeclarator &&
            CanReturnLocalDeclarationSymbol(reboundDeclarator.Local.Type, allowErrorType, allowInitializerBinding: true))
        {
            localSymbol = GetCanonicalLocalDeclarationSymbol(variableDeclarator, reboundDeclarator.Local);
            return true;
        }

        localSymbol = null!;
        return false;
    }

    private ILocalSymbol GetCanonicalLocalDeclarationSymbol(
        VariableDeclaratorSyntax variableDeclarator,
        ILocalSymbol localSymbol)
    {
        var binderRoot = GetInterestBindingRoot(variableDeclarator, includeExtendedExecutableRoots: true)
            ?? (SyntaxNode)variableDeclarator;
        var binder = GetBinderForIncrementalSemanticQuery(binderRoot);
        return TryGetNearestBlockBinder(binder, out var blockBinder) &&
               blockBinder.TryGetDeclaredLocalForSemanticQuery(variableDeclarator, out var declaredLocal) &&
               (IsCompleteLocalDeclarationType(declaredLocal.Type) || !IsCompleteLocalDeclarationType(localSymbol.Type))
            ? declaredLocal
            : localSymbol;
    }

    private static bool CanReturnLocalDeclarationSymbol(
        ITypeSymbol type,
        bool allowErrorType,
        bool allowInitializerBinding)
        => !allowInitializerBinding
            ? allowErrorType || !type.ContainsErrorType()
            : IsCompleteLocalDeclarationType(type);

    private static bool IsCompleteLocalDeclarationType(ITypeSymbol type)
        => !type.ContainsErrorType() && !IsIncompleteGenericLocalType(type);

    private static bool IsIncompleteGenericLocalType(ITypeSymbol type)
        => type is INamedTypeSymbol { Arity: > 0 } namedType &&
           (namedType.IsUnboundGenericType ||
            namedType.TypeArguments.IsDefaultOrEmpty ||
            namedType.TypeArguments.Length < namedType.TypeParameters.Length);

    private void BindPrecedingGlobalStatementsForScope(
        SyntaxNode root,
        GlobalStatementSyntax owner,
        Binder ownerBinder,
        bool requireCompleteDeclarations)
    {
        var scopeBinder = !requireCompleteDeclarations &&
            TryGetNearestBlockBinder(ownerBinder, out var ownerBlockBinder)
                ? ownerBlockBinder
                : null;

        foreach (var global in root.DescendantNodesAndSelf().OfType<GlobalStatementSyntax>())
        {
            if (!ReferenceEquals(global.Parent, owner.Parent))
                continue;

            if (global.Span.Start >= owner.Span.Start)
                break;

            var binder = requireCompleteDeclarations
                ? GetBinder(global.Statement)
                : scopeBinder ?? GetBinderForIncrementalSemanticQuery(global.Statement);
            if (requireCompleteDeclarations)
                binder.GetOrBind(global.Statement);
            else
                binder.EnsureStatementDeclarations(global.Statement);
        }
    }

    private void BindGlobalStatementDeclarationsForScope(
        SyntaxNode root,
        Binder ownerBinder,
        bool requireCompleteDeclarations)
    {
        var scopeBinder = !requireCompleteDeclarations &&
            TryGetNearestBlockBinder(ownerBinder, out var ownerBlockBinder)
                ? ownerBlockBinder
                : null;

        var globals = root is CompilationUnitSyntax compilationUnit
            ? Compilation.GetBindableGlobalStatements(compilationUnit)
            : root.DescendantNodesAndSelf().OfType<GlobalStatementSyntax>();

        foreach (var global in globals)
        {
            var binder = requireCompleteDeclarations
                ? GetBinder(global.Statement)
                : scopeBinder ?? GetBinderForIncrementalSemanticQuery(global.Statement);
            if (requireCompleteDeclarations)
                binder.GetOrBind(global.Statement);
            else
                binder.EnsureStatementDeclarations(global.Statement);
        }
    }

    internal void EnsurePrecedingGlobalStatementsBoundForSemanticQuery(SyntaxNode contextNode)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        if (contextNode.AncestorsAndSelf().OfType<GlobalStatementSyntax>().FirstOrDefault() is not { } globalOwner ||
            contextNode.SyntaxTree.GetRoot() is not CompilationUnitSyntax root)
        {
            return;
        }

        BindPrecedingGlobalStatementsForScope(root, globalOwner, GetBinder(globalOwner), requireCompleteDeclarations: true);
    }

    private void PrimeContextualFunctionExpressions(SyntaxNode root)
    {
        foreach (var parameter in root.DescendantNodes()
                     .OfType<ParameterSyntax>()
                     .Where(static parameter =>
                         parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any()))
        {
            _ = GetFunctionExpressionParameterSymbol(parameter);
        }
    }

    public MacroExpansionResult? GetMacroExpansion(
        AttributeSyntax attribute,
        CancellationToken cancellationToken = default)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        ArgumentNullException.ThrowIfNull(attribute);

        if (!attribute.IsMacroAttribute())
            return null;

        if (TryGetMacroTarget(attribute) is not { } targetDeclaration)
            return null;

        var expansionMap = _macroExpansionCache.GetOrAdd(
            targetDeclaration,
            static (syntax, state) => MacroExpansionService.ExpandAttachedMacros(
                state.Model.Compilation,
                state.Model,
                syntax,
                state.Model._declarationDiagnostics,
                state.CancellationToken),
            (Model: this, CancellationToken: cancellationToken));

        return expansionMap.TryGetValue(attribute, out var expansion)
            ? expansion
            : null;
    }

    public FreestandingMacroExpansionResult? GetMacroExpansion(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
        => GetFreestandingMacroExpansion(expression, cancellationToken);

    public FreestandingMacroExpansionResult? GetMacroExpansion(
        FreestandingMacroMemberDeclarationSyntax member,
        CancellationToken cancellationToken = default)
        => GetFreestandingMacroExpansion(member, cancellationToken);

    public FreestandingMacroExpansionResult? GetMacroExpansion(
        FreestandingMacroDeclarationSyntax declaration,
        CancellationToken cancellationToken = default)
        => GetFreestandingMacroExpansion(declaration, cancellationToken);

    internal FreestandingMacroExpansionResult? GetFreestandingMacroExpansion(
        SyntaxNode invocation,
        CancellationToken cancellationToken)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        ArgumentNullException.ThrowIfNull(invocation);
        if (!FreestandingMacroInvocation.TryCreate(invocation, out _))
            throw new ArgumentException("Syntax is not a freestanding macro carrier.", nameof(invocation));

        if (_freestandingMacroExpansionCache.TryGetValue(invocation, out var cached) &&
            cached.IsCurrent())
        {
            return cached.Result;
        }

        if (cached is not null)
            InvalidateFreestandingMacroExpansion(invocation);

        var result = invocation switch
        {
            FreestandingMacroExpressionSyntax expression => MacroExpansionService.ExpandFreestandingMacro(
                Compilation,
                this,
                expression,
                _declarationDiagnostics,
                cancellationToken),
            FreestandingMacroMemberDeclarationSyntax member => MacroExpansionService.ExpandFreestandingMacro(
                Compilation,
                this,
                member,
                _declarationDiagnostics,
                cancellationToken),
            FreestandingMacroDeclarationSyntax declaration => MacroExpansionService.ExpandFreestandingMacro(
                Compilation,
                this,
                declaration,
                _declarationDiagnostics,
                cancellationToken),
            _ => null
        };
        _freestandingMacroExpansionCache[invocation] =
            new FreestandingMacroExpansionCacheEntry(result);
        _diagnostics = null;
        _documentDiagnostics = null;
        return result;
    }

    internal FreestandingMacroExpansionResult? GetFreestandingMacroExpansionForFragmentTooling(
        FreestandingMacroExpressionSyntax expression,
        CancellationToken cancellationToken = default)
    {
        using var semanticAccess = EnterSemanticAccess(cancellationToken);

        ArgumentNullException.ThrowIfNull(expression);

        // Fragment syntax is parsed speculatively to power editor features. Its
        // immediate statement/expression position belongs to the fragment parse,
        // not necessarily to the outer macro's eventual expansion, so diagnostics
        // from this expansion must not escape into the authored document.
        return MacroExpansionService.ExpandFreestandingMacro(
            Compilation,
            this,
            expression,
            new DiagnosticBag(),
            cancellationToken,
            registerGeneratedSyntax: false);
    }

    private void InvalidateStaleFreestandingMacroExpansions()
    {
        foreach (var (invocation, entry) in _freestandingMacroExpansionCache)
        {
            if (!entry.IsCurrent())
                InvalidateFreestandingMacroExpansion(invocation);
        }
    }

    private void InvalidateFreestandingMacroExpansion(SyntaxNode invocation)
    {
        _freestandingMacroExpansionCache.TryRemove(invocation, out _);
        _declarationDiagnostics.ClearDiagnostics(invocation.Span);
        _diagnostics = null;
        _documentDiagnostics = null;
        _expandedRoot = null;
        _macroReplacementSyntaxMap.Clear();
    }

    internal IEnumerable<string> GetObservedMacroFilePaths()
        => _freestandingMacroExpansionCache.Values
            .SelectMany(static entry =>
                entry.Result?.FileDependencies ?? ImmutableArray<MacroFileDependency>.Empty)
            .Select(static dependency => dependency.Path);

    internal bool TryGetMacroReplacementSyntax(SyntaxNode node, out SyntaxNode replacement)
        => _macroReplacementSyntaxMap.TryGetValue(node, out replacement!);

    private void EnsureContainingFreestandingMacroReplacementSyntax(SyntaxNode node)
    {
        if (_macroReplacementSyntaxMap.ContainsKey(node))
            return;

        var invocation = node.Ancestors()
            .OfType<FreestandingMacroExpressionSyntax>()
            .FirstOrDefault();
        if (invocation is null)
            return;

        var expansion = GetMacroExpansion(invocation);
        if (expansion?.Expression is { } replacement)
            RegisterMacroReplacementSyntaxTree(invocation, replacement);
    }

    internal void RegisterMacroReplacementSyntax(SyntaxNode original, SyntaxNode replacement)
        => _macroReplacementSyntaxMap[original] = replacement;

    internal void RegisterMacroReplacementSyntaxTree(SyntaxNode originalRoot, SyntaxNode replacementRoot)
    {
        RegisterMacroReplacementSyntaxTrees(originalRoot, [replacementRoot]);
    }

    internal void RegisterMacroReplacementSyntaxTrees(SyntaxNode originalRoot, IEnumerable<SyntaxNode> replacementRoots)
    {
        ArgumentNullException.ThrowIfNull(originalRoot);
        ArgumentNullException.ThrowIfNull(replacementRoots);

        var replacementRootArray = replacementRoots
            .Where(static root => root is not null)
            .ToArray();

        if (replacementRootArray.Length == 0)
            return;

        RegisterMacroReplacementSyntax(originalRoot, replacementRootArray[0]);

        var replacementLookup = new Dictionary<object, Queue<SyntaxNode>>(ReferenceEqualityComparer.Instance);
        foreach (var replacementRoot in replacementRootArray)
        {
            foreach (var replacementNode in replacementRoot.DescendantNodesAndSelf())
            {
                if (!replacementLookup.TryGetValue(replacementNode.Green, out var matches))
                {
                    matches = new Queue<SyntaxNode>();
                    replacementLookup.Add(replacementNode.Green, matches);
                }

                matches.Enqueue(replacementNode);
            }
        }

        foreach (var originalNode in originalRoot.DescendantNodesAndSelf())
        {
            if (ReferenceEquals(originalNode, originalRoot))
                continue;

            if (!replacementLookup.TryGetValue(originalNode.Green, out var matches) || matches.Count == 0)
                continue;

            RegisterMacroReplacementSyntax(originalNode, matches.Dequeue());
        }
    }

    internal bool TryGetMacroContainingTypeSyntax(BaseTypeDeclarationSyntax generatedType, out BaseTypeDeclarationSyntax containingType)
        => _macroContainingTypeSyntaxMap.TryGetValue(generatedType, out containingType!);

    internal void RegisterMacroContainingTypeSyntax(BaseTypeDeclarationSyntax generatedType, BaseTypeDeclarationSyntax containingType)
        => _macroContainingTypeSyntaxMap[generatedType] = containingType;

    /// <summary>
    /// Gets type information about an expression.
    /// </summary>
    /// <param name="expr">The expression syntax node</param>
    /// <returns>The type info</returns>
    public TypeInfo GetTypeInfo(ExpressionSyntax expr)
    {
        ValidateSyntaxNode(expr, nameof(expr));

        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        using var semanticQueryBinding = EnterSemanticQueryBinding();
        Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoQuery();

        EnsureContainingFreestandingMacroReplacementSyntax(expr);
        if (TryGetMacroReplacementSyntax(expr, out var macroReplacement) &&
            macroReplacement is ExpressionSyntax replacementExpression &&
            !ReferenceEquals(replacementExpression, expr))
        {
            var replacementInfo = GetTypeInfo(replacementExpression);
            if (HasTypeInfo(replacementInfo))
                StoreTypeMapping(expr, replacementInfo);
            return replacementInfo;
        }

        TypeInfo Cache(TypeInfo info)
        {
            if (TryGetContextualConvertedType(expr, info.Type, out var contextualConvertedType) &&
                !SymbolEqualityComparer.Default.Equals(info.ConvertedType, contextualConvertedType))
            {
                info = new TypeInfo(
                    info.Type,
                    contextualConvertedType,
                    ComputeConversion(info.Type, contextualConvertedType));
            }

            if (HasTypeInfo(info))
                StoreTypeMapping(expr, info);

            return info;
        }

        if (Compilation.Options.EnableIsNotNullNarrowing &&
            GetBoundNode(expr) is BoundExpression
            {
                Type: { } narrowedType
            } narrowedExpression &&
            narrowedExpression is BoundNullableValueExpression or
                BoundConversionExpression { IsNullableSuppression: true })
        {
            return Cache(new TypeInfo(
                narrowedType,
                narrowedType,
                ComputeConversion(narrowedType, narrowedType)));
        }

        if (TryGetAvailableTypeInfo(expr, out var availableTypeInfo))
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoSymbolHit();
            return Cache(availableTypeInfo);
        }

        if (expr is FunctionExpressionSyntax functionExpression &&
            TryGetFunctionExpressionDelegateType(functionExpression, out var functionDelegateType) &&
            functionDelegateType is not null &&
            functionDelegateType.TypeKind != TypeKind.Error)
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoSymbolHit();
            return Cache(new TypeInfo(
                functionDelegateType,
                functionDelegateType,
                ComputeConversion(functionDelegateType, functionDelegateType)));
        }

        if (TryGetNodeInterestSymbolType(expr, out var nodeInterestType) &&
            nodeInterestType is not null &&
            nodeInterestType.TypeKind != TypeKind.Error)
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoSymbolHit();
            return Cache(new TypeInfo(nodeInterestType, nodeInterestType, ComputeConversion(nodeInterestType, nodeInterestType)));
        }

        if (expr is DefaultExpressionSyntax { Type: null } &&
            TryGetTargetTypeForExpression(expr, out var defaultTargetType) &&
            defaultTargetType is not null &&
            defaultTargetType.TypeKind != TypeKind.Error)
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoSymbolHit();
            var defaultValueType = defaultTargetType.GetDefaultValueType();
            return Cache(new TypeInfo(
                defaultValueType,
                defaultValueType,
                ComputeConversion(defaultValueType, defaultValueType)));
        }

        var symbolInfo = GetSymbolInfo(expr);
        var symbolType = GetTypeFromSymbol(symbolInfo.Symbol);
        if (symbolType is null && symbolInfo.Symbol is not null)
        {
            EnsureDeclarationInterestBound(symbolInfo.Symbol);
            symbolType = GetTypeFromSymbol(symbolInfo.Symbol);
        }
        if (symbolType is not null && symbolType.TypeKind != TypeKind.Error)
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoSymbolHit();
            return Cache(new TypeInfo(symbolType, symbolType, ComputeConversion(symbolType, symbolType)));
        }

        Compilation.PerformanceInstrumentation.SemanticQuery.RecordTypeInfoBoundFallback();
        var boundExpr = GetBoundNode(expr) as BoundExpression;

        if (boundExpr is null || (boundExpr.Type is null && boundExpr.GetConvertedType() is null))
        {
            if (TryBindInterestRegion(expr, out var regionBoundExpression))
                boundExpr = regionBoundExpression;

        }

        if (boundExpr is null)
            return new TypeInfo(null, null);

        ITypeSymbol? naturalType = boundExpr.Type;

        ITypeSymbol? convertedType = boundExpr.GetConvertedType() ?? boundExpr.Type;
        if (TryGetContextualConvertedType(expr, naturalType, out var contextualConvertedType))
            convertedType = contextualConvertedType;

        var conversion = boundExpr switch
        {
            BoundConversionExpression cast => cast.Conversion,
            BoundAsExpression asExpression => asExpression.Conversion,
            _ => ComputeConversion(naturalType, convertedType)
        };

        return Cache(new TypeInfo(naturalType, convertedType, conversion));
    }

    internal ITypeSymbol? GetMacroArgumentType(ExpressionSyntax expression)
    {
        ValidateSyntaxNode(expression, nameof(expression));

        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        var binder = GetBinder(expression);
        return binder.BindExpression(expression).Type;
    }

    private bool TryGetContextualConvertedType(
        ExpressionSyntax expression,
        ITypeSymbol? naturalType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? convertedType)
    {
        convertedType = null;

        if (naturalType is null || naturalType.TypeKind == TypeKind.Error)
            return false;

        if (!TryGetTargetTypeForExpression(expression, out var targetType) ||
            targetType is not { TypeKind: not TypeKind.Error })
        {
            return false;
        }

        convertedType = ProjectContextualConvertedType(targetType, naturalType);
        return true;
    }

    private bool TryGetArgumentParameterSymbolInfo(ArgumentSyntax argument, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax invocation ||
            !TryGetInvocationMethodSymbol(invocation, out var method) ||
            method is null)
        {
            return false;
        }

        var parameter = GetParameterForArgument(method, argumentList.Arguments, argument);
        if (parameter is null)
            return false;

        info = new SymbolInfo(parameter);
        return true;
    }

    private bool TryGetInvocationMethodSymbol(
        InvocationExpressionSyntax invocation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMethodSymbol? method)
    {
        if (TryGetCachedSymbolInfo(invocation, out var cachedInvocationInfo))
        {
            method = cachedInvocationInfo.Symbol as IMethodSymbol
                ?? cachedInvocationInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (method is not null)
                return true;
        }

        if (_availableInvocationSymbolInfoInProgress.ContainsKey(invocation))
        {
            method = null;
            return false;
        }

        var invocationInfo = GetSymbolInfo(invocation);
        method = invocationInfo.Symbol as IMethodSymbol
            ?? invocationInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (method is not null)
            return true;

        var boundInvocation = GetBoundNode(invocation);
        method = boundInvocation switch
        {
            BoundInvocationExpression invocationExpression => invocationExpression.Method,
            BoundObjectCreationExpression objectCreation => objectCreation.Constructor,
            _ => null
        };
        if (method is not null)
            return true;

        if (invocation.Expression is TypeSyntax typeSyntax &&
            GetTypeInfo(typeSyntax).Type is INamedTypeSymbol namedType)
        {
            method = namedType.Constructors.FirstOrDefault();
        }
        if (method is not null)
            return true;

        if (GetSymbolInfo(invocation.Expression).Symbol is INamedTypeSymbol expressionType)
            method = expressionType.Constructors.FirstOrDefault();

        return method is not null;
    }

    private static ITypeSymbol ProjectContextualConvertedType(ITypeSymbol parameterType, ITypeSymbol naturalType)
    {
        if (parameterType is not INamedTypeSymbol parameterNamed ||
            naturalType is not INamedTypeSymbol naturalNamed ||
            naturalNamed.TryGetUnionCase() is not { } naturalCase ||
            !TryProjectUnionFromCaseArguments(naturalNamed, naturalCase, out var projectedUnion) ||
            projectedUnion is null)
        {
            return parameterType;
        }

        var parameterDefinition = parameterNamed.OriginalDefinition as INamedTypeSymbol ?? parameterNamed;
        var projectedDefinition = projectedUnion.OriginalDefinition as INamedTypeSymbol ?? projectedUnion;
        if (SymbolEqualityComparer.Default.Equals(parameterDefinition, projectedDefinition))
            return projectedUnion;

        var parameterUnionDefinition = parameterNamed.TryGetUnion()?.OriginalDefinition as INamedTypeSymbol;
        if (parameterUnionDefinition is not null &&
            SymbolEqualityComparer.Default.Equals(parameterUnionDefinition, projectedDefinition))
        {
            return projectedUnion;
        }

        return parameterType;
    }

    private static bool TryProjectUnionFromCaseArguments(
        INamedTypeSymbol caseType,
        IUnionCaseTypeSymbol caseSymbol,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INamedTypeSymbol? projectedUnion)
    {
        projectedUnion = null;

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

        var unionTypeArguments = unionDefinition.TypeParameters
            .Select(typeParameter => (ITypeSymbol)typeParameter)
            .ToArray();

        var changed = false;

        for (var i = 0; i < caseDefinitionNamed.TypeParameters.Length; i++)
        {
            var caseTypeParameter = caseDefinitionNamed.TypeParameters[i];
            ITypeParameterSymbol? unionTypeParameter = null;

            if (caseDefinition is SourceUnionCaseTypeSymbol sourceCaseDefinition &&
                sourceCaseDefinition.TryGetProjectedUnionTypeParameter(caseTypeParameter, out var mapped))
            {
                unionTypeParameter = mapped;
            }
            else
            {
                unionTypeParameter = unionDefinition.TypeParameters
                    .FirstOrDefault(tp => string.Equals(tp.Name, caseTypeParameter.Name, StringComparison.Ordinal));
            }

            if (unionTypeParameter is null)
                continue;

            var unionIndex = -1;
            for (var unionParameterIndex = 0; unionParameterIndex < unionDefinition.TypeParameters.Length; unionParameterIndex++)
            {
                if (SymbolEqualityComparer.Default.Equals(unionDefinition.TypeParameters[unionParameterIndex], unionTypeParameter))
                {
                    unionIndex = unionParameterIndex;
                    break;
                }
            }

            if (unionIndex < 0 || unionIndex >= unionTypeArguments.Length)
                continue;

            unionTypeArguments[unionIndex] = caseType.TypeArguments[i];
            changed = true;
        }

        if (!changed)
            return false;

        projectedUnion = (INamedTypeSymbol)unionDefinition.Construct(unionTypeArguments);
        return true;
    }

    private static IParameterSymbol? GetParameterForArgument(
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        ArgumentSyntax argument)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is { Length: > 0 } argumentName)
        {
            return method.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, argumentName, StringComparison.OrdinalIgnoreCase));
        }

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

        if (argumentIndex < method.Parameters.Length)
            return method.Parameters[argumentIndex];

        var lastParameter = method.Parameters.LastOrDefault();
        return lastParameter?.IsVarParams == true ? lastParameter : null;
    }

    public bool TryGetTypeInfo(ExpressionSyntax expression, out TypeInfo typeInfo)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        typeInfo = GetTypeInfo(expression);
        return HasTypeInfo(typeInfo);
    }

    private bool TryGetNodeInterestSymbolType(ExpressionSyntax expression, out ITypeSymbol? type)
    {
        if (TryLookupVisibleValueSymbol(expression) is { } visibleSymbol)
        {
            type = GetTypeFromSymbol(visibleSymbol);
            if (type is not null)
                return true;
        }

        if (TryGetNodeInterestSymbolInfo(expression, out var symbolInfo))
        {
            type = GetTypeFromSymbol(symbolInfo.Symbol);
            return type is not null;
        }

        type = null;
        return false;
    }

    internal bool TryGetFunctionExpressionDelegateType(
        FunctionExpressionSyntax functionExpression,
        out ITypeSymbol? delegateType)
    {
        if (_functionExpressionDelegateTypeCache.TryGetValue(functionExpression, out var cachedDelegateType) &&
            cachedDelegateType.TypeKind != TypeKind.Error &&
            !ContainsTypeParameter(cachedDelegateType))
        {
            delegateType = cachedDelegateType;
            return true;
        }

        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction) &&
            cachedFunction.DelegateType is not null)
        {
            delegateType = cachedFunction.DelegateType;
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        if (TryGetContextualBoundFunctionExpression(functionExpression, out var contextualFunction) &&
            contextualFunction.DelegateType is not null &&
            contextualFunction.DelegateType.TypeKind != TypeKind.Error)
        {
            delegateType = contextualFunction.DelegateType;
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        if (TryGetAvailableFunctionExpressionDelegateType(functionExpression, out delegateType))
        {
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        delegateType = null;
        return false;
    }

    private void CacheFunctionExpressionDelegateType(
        FunctionExpressionSyntax functionExpression,
        ITypeSymbol? delegateType)
    {
        if (delegateType is not null &&
            delegateType.TypeKind != TypeKind.Error &&
            !ContainsTypeParameter(delegateType) &&
            !ContainsErrorTypeShallow(delegateType))
        {
            _functionExpressionDelegateTypeCache[functionExpression] = delegateType;
        }
    }

    internal bool TryGetAvailableFunctionExpressionDelegateType(
        FunctionExpressionSyntax functionExpression,
        out ITypeSymbol? delegateType)
    {
        delegateType = null;

        if (functionExpression.Parent is not ArgumentSyntax argument ||
            argument.Parent is not ArgumentListSyntax argumentList ||
            argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var argumentIndex = 0;
        foreach (var current in argumentList.Arguments)
        {
            if (current.Span == argument.Span && current.Kind == argument.Kind)
                break;

            argumentIndex++;
        }

        if (TryGetCachedFunctionExpressionDelegateTypeFromInvocation(
                invocation,
                argumentIndex,
                out delegateType))
        {
            return true;
        }

        if (invocation.Parent is InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression &&
            IsSameSyntaxNode(pipeExpression.Right, invocation) &&
            TryGetAvailableTypeInfo(pipeExpression.Left, out var pipeReceiverTypeInfo) &&
            (pipeReceiverTypeInfo.Type ?? pipeReceiverTypeInfo.ConvertedType) is { TypeKind: not TypeKind.Error } pipeReceiverType &&
            TryGetAvailablePipeInvocationCandidates(invocation, out var pipeMethods) &&
            TryGetDelegateTypeFromCandidateParameter(pipeMethods, argumentIndex + 1, pipeReceiverType, out delegateType))
        {
            return true;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var extensionReceiverType = TryGetAvailableReceiverType(memberAccess.Expression);
            if (extensionReceiverType is { TypeKind: not TypeKind.Error })
            {
                var hasExtensionMethods = TryGetAvailableExtensionInvocationCandidates(invocation, out var extensionMethods);
                if (hasExtensionMethods &&
                    TryGetDelegateTypeFromCandidateParameter(extensionMethods, argumentIndex + 1, extensionReceiverType, out delegateType))
                {
                    return true;
                }
            }
        }

        return TryGetAvailableInvocationCandidates(invocation, out var methods) &&
               TryGetDelegateTypeFromCandidateParameter(methods, argumentIndex, receiverType: null, out delegateType);
    }

    internal bool TryGetAvailableFunctionExpressionReturnType(
        FunctionExpressionSyntax functionExpression,
        out ITypeSymbol? returnType)
    {
        if (_functionExpressionSymbolCache.TryGetValue(functionExpression, out var cachedMethod) &&
            cachedMethod.ReturnType.TypeKind != TypeKind.Error &&
            !ContainsTypeParameter(cachedMethod.ReturnType))
        {
            returnType = cachedMethod.ReturnType;
            return true;
        }

        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction) &&
            cachedFunction.ReturnType.TypeKind != TypeKind.Error &&
            !ContainsTypeParameter(cachedFunction.ReturnType))
        {
            returnType = cachedFunction.ReturnType;
            return true;
        }

        if (TryGetAvailableFunctionExpressionDelegateType(functionExpression, out var delegateType) &&
            delegateType is INamedTypeSymbol namedDelegateType &&
            namedDelegateType.GetDelegateInvokeMethod()?.ReturnType is { TypeKind: not TypeKind.Error } delegateReturnType &&
            !ContainsTypeParameter(delegateReturnType))
        {
            returnType = delegateReturnType;
            return true;
        }

        returnType = null;
        return false;
    }

    private bool TryGetCachedFunctionExpressionDelegateTypeFromInvocation(
        InvocationExpressionSyntax invocation,
        int argumentIndex,
        out ITypeSymbol? delegateType)
    {
        delegateType = null;

        if (TryGetCachedInvocationDelegateTypeFromBoundNode(invocation, argumentIndex, out delegateType))
            return true;

        if (TryGetCachedInvocationMethods(invocation, out var methods) &&
            !methods.IsDefaultOrEmpty)
        {
            var parameterIndex = GetFunctionArgumentParameterIndex(invocation, methods, argumentIndex, out var receiverType);
            if (TryGetDelegateTypeFromCandidateParameter(methods, parameterIndex, receiverType, out delegateType))
                return true;
        }

        return false;
    }

    private bool TryGetCachedInvocationDelegateTypeFromBoundNode(
        InvocationExpressionSyntax invocation,
        int argumentIndex,
        out ITypeSymbol? delegateType)
    {
        delegateType = null;

        BoundInvocationExpression? boundInvocation = null;
        if (TryGetCachedBoundNode(invocation) is BoundInvocationExpression cachedInvocation)
        {
            boundInvocation = cachedInvocation;
        }
        else if (invocation.Parent is InfixOperatorExpressionSyntax
        {
            OperatorToken.Kind: SyntaxKind.PipeToken
        } pipeExpression &&
                 IsSameSyntaxNode(pipeExpression.Right, invocation) &&
                 TryGetCachedBoundNode(pipeExpression) is BoundInvocationExpression cachedPipe)
        {
            boundInvocation = cachedPipe;
        }

        if (boundInvocation is null)
            return false;

        var receiverType = boundInvocation.ExtensionReceiver?.Type;
        var parameterIndex = boundInvocation.Method.IsExtensionMethod && boundInvocation.ExtensionReceiver is not null
            ? argumentIndex + 1
            : argumentIndex;

        return TryGetDelegateTypeFromCandidateParameter(
            ImmutableArray.Create(boundInvocation.Method),
            parameterIndex,
            receiverType,
            out delegateType);
    }

    private bool TryGetCachedInvocationMethods(
        InvocationExpressionSyntax invocation,
        out ImmutableArray<IMethodSymbol> methods)
    {
        var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();

        void Add(IMethodSymbol? method)
        {
            if (method is null)
                return;

            foreach (var existing in builder)
            {
                if (SymbolEqualityComparer.Default.Equals(existing, method))
                    return;
            }

            builder.Add(method);
        }

        void AddFromSymbolInfo(SymbolInfo info)
        {
            if (info.Symbol is IMethodSymbol method)
                Add(method);

            if (!info.CandidateSymbols.IsDefaultOrEmpty)
            {
                foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>())
                    Add(candidate);
            }
        }

        if (TryGetCachedSymbolInfo(invocation, out var invocationInfo))
            AddFromSymbolInfo(invocationInfo);

        if (TryGetCachedSymbolInfo(invocation.Expression, out var expressionInfo))
            AddFromSymbolInfo(expressionInfo);

        methods = builder.ToImmutable();
        return methods.Length > 0;
    }

    private int GetFunctionArgumentParameterIndex(
        InvocationExpressionSyntax invocation,
        ImmutableArray<IMethodSymbol> methods,
        int argumentIndex,
        out ITypeSymbol? receiverType)
    {
        receiverType = null;

        if (invocation.Parent is InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression &&
            IsSameSyntaxNode(pipeExpression.Right, invocation))
        {
            receiverType = TryGetCachedReceiverType(pipeExpression.Left) ??
                           TryGetAvailableReceiverType(pipeExpression.Left);
            return argumentIndex + 1;
        }

        if (methods.Any(static method => method.IsExtensionMethod) &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            receiverType = TryGetCachedReceiverType(memberAccess.Expression) ??
                           TryGetAvailableReceiverType(memberAccess.Expression);
            if (receiverType is not null && receiverType.TypeKind != TypeKind.Error)
                return argumentIndex + 1;
        }

        return argumentIndex;
    }

    private static bool TryGetDelegateTypeFromCandidateParameter(
        ImmutableArray<IMethodSymbol> methods,
        int parameterIndex,
        ITypeSymbol? receiverType,
        out ITypeSymbol? delegateType)
    {
        delegateType = null;

        if (methods.IsDefaultOrEmpty)
            return false;

        foreach (var method in methods)
        {
            if (!TryGetFastParameterCount(method, out var parameterCount) ||
                parameterIndex < 0 ||
                parameterIndex >= parameterCount)
                continue;

            if (!TryGetFastParameterType(method, parameterIndex, out var parameterType))
                continue;
            if (parameterType is null || parameterType.ContainsErrorType())
                continue;

            if (receiverType is not null &&
                TryGetFastParameterCount(method, out var receiverParameterCount) &&
                receiverParameterCount > 0 &&
                TryGetFastParameterType(method, 0, out var receiverParameterType) &&
                receiverParameterType is not null &&
                TryInferExtensionReceiverSubstitutions(receiverParameterType, receiverType, out var substitutions))
            {
                parameterType = SubstituteTypeParameters(parameterType, substitutions);
            }

            if (TryUnwrapCallableDelegateType(parameterType, out var candidateDelegate))
            {
                delegateType = candidateDelegate;
                return true;
            }
        }

        return false;
    }

    private static bool TryInferExtensionReceiverSubstitutions(
        ITypeSymbol parameterType,
        ITypeSymbol receiverType,
        out Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        return TryUnifyExtensionReceiver(parameterType, receiverType, substitutions);
    }

    private static bool TryUnifyExtensionReceiver(
        ITypeSymbol parameterType,
        ITypeSymbol receiverType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (parameterType is ITypeParameterSymbol parameter)
        {
            substitutions[parameter] = receiverType;
            return true;
        }

        if (parameterType is IArrayTypeSymbol parameterArray &&
            receiverType is IArrayTypeSymbol receiverArray)
        {
            return TryUnifyExtensionReceiver(parameterArray.ElementType, receiverArray.ElementType, substitutions);
        }

        if (TryGetTupleElementTypes(parameterType, out var parameterElements) &&
            TryGetTupleElementTypes(receiverType, out var receiverElements))
        {
            if (parameterElements.Length != receiverElements.Length)
                return false;

            for (var i = 0; i < parameterElements.Length; i++)
            {
                if (!TryUnifyExtensionReceiver(parameterElements[i], receiverElements[i], substitutions))
                    return false;
            }

            return true;
        }

        if (parameterType is not INamedTypeSymbol parameterNamed ||
            receiverType is not INamedTypeSymbol receiverNamed)
        {
            return SymbolEqualityComparer.Default.Equals(parameterType, receiverType);
        }

        if (TryUnifyNamedTypes(parameterNamed, receiverNamed, substitutions))
            return true;

        foreach (var receiverInterface in receiverNamed.AllInterfaces)
        {
            if (TryUnifyNamedTypes(parameterNamed, receiverInterface, substitutions))
                return true;
        }

        for (var baseType = receiverNamed.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (TryUnifyNamedTypes(parameterNamed, baseType, substitutions))
                return true;
        }

        return false;
    }

    private static bool TryGetTupleElementTypes(
        ITypeSymbol type,
        out ImmutableArray<ITypeSymbol> elementTypes)
    {
        if (type is ITupleTypeSymbol tuple)
        {
            elementTypes = tuple.TupleElements.Select(static element => element.Type).ToImmutableArray();
            return true;
        }

        if (type is INamedTypeSymbol named &&
            (named.ConstructedFrom ?? named).SpecialType is >= SpecialType.System_ValueTuple_T1 and <= SpecialType.System_ValueTuple_TRest)
        {
            elementTypes = TypeSubstitution.GetShallowTypeArguments(named);
            return !elementTypes.IsDefaultOrEmpty;
        }

        elementTypes = default;
        return false;
    }

    private static bool TryUnifyNamedTypes(
        INamedTypeSymbol parameterType,
        INamedTypeSymbol receiverType,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        var parameterDefinition = TypeSubstitution.GetDefinitionForSubstitution(parameterType);
        var receiverDefinition = TypeSubstitution.GetDefinitionForSubstitution(receiverType);

        if (!SymbolEqualityComparer.Default.Equals(parameterDefinition, receiverDefinition))
            return false;

        var parameterArguments = TypeSubstitution.GetShallowTypeArguments(parameterType);
        var receiverArguments = TypeSubstitution.GetShallowTypeArguments(receiverType);

        if (parameterArguments.IsDefault)
            parameterArguments = ImmutableArray<ITypeSymbol>.Empty;

        if (receiverArguments.IsDefault)
            receiverArguments = ImmutableArray<ITypeSymbol>.Empty;

        if (parameterArguments.Length != receiverArguments.Length)
            return false;

        for (var i = 0; i < parameterArguments.Length; i++)
        {
            if (!TryUnifyExtensionReceiver(parameterArguments[i], receiverArguments[i], substitutions))
                return false;
        }

        return true;
    }

    private static ITypeSymbol SubstituteTypeParameters(
        ITypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        if (type is ITypeParameterSymbol parameter &&
            substitutions.TryGetValue(parameter, out var replacement))
        {
            return replacement;
        }

        if (type is ITypeParameterSymbol unmatchedParameter &&
            TypeSubstitution.TryGetEquivalentTypeParameterSubstitution(unmatchedParameter, substitutions, out var equivalentReplacement))
        {
            return equivalentReplacement;
        }

        if (type is NullableTypeSymbol nullableType)
        {
            var substituted = SubstituteTypeParameters(nullableType.UnderlyingType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, nullableType.UnderlyingType)
                ? type
                : substituted.ApplySubstitutedNullability(nullableType);
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            var substituted = SubstituteTypeParameters(arrayType.ElementType, substitutions);
            return SymbolEqualityComparer.Default.Equals(substituted, arrayType.ElementType)
                ? type
                : new ArrayTypeSymbol(arrayType.BaseType, substituted, arrayType.ContainingSymbol, arrayType.ContainingType, arrayType.ContainingNamespace, [], arrayType.Rank, arrayType.FixedLength);
        }

        if (type is ITupleTypeSymbol tupleType)
        {
            return TypeSubstitution.SubstituteTupleElements(
                tupleType,
                element => SubstituteTypeParameters(element, substitutions));
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

    private static bool ContainsTypeParameter(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type is ITypeParameterSymbol)
            return true;

        if (type is NullableTypeSymbol nullableType)
            return ContainsTypeParameter(nullableType.UnderlyingType);

        if (type is IArrayTypeSymbol arrayType)
            return ContainsTypeParameter(arrayType.ElementType);

        if (type is INamedTypeSymbol namedType)
        {
            var typeArguments = TypeSubstitution.GetShallowTypeArguments(namedType);
            return !typeArguments.IsDefaultOrEmpty && typeArguments.Any(ContainsTypeParameter);
        }

        return false;
    }

    private static bool ContainsErrorTypeShallow(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type.TypeKind == TypeKind.Error || type is IErrorTypeSymbol)
            return true;

        if (type is LiteralTypeSymbol literal)
            return ContainsErrorTypeShallow(literal.UnderlyingType);

        if (type is NullableTypeSymbol nullable)
            return ContainsErrorTypeShallow(nullable.UnderlyingType);

        if (type is IArrayTypeSymbol array)
            return ContainsErrorTypeShallow(array.ElementType);

        if (type is RefTypeSymbol refType)
            return ContainsErrorTypeShallow(refType.ElementType);

        if (type is INamedTypeSymbol namedType)
        {
            var typeArguments = TypeSubstitution.GetShallowTypeArguments(namedType);
            return !typeArguments.IsDefaultOrEmpty && typeArguments.Any(ContainsErrorTypeShallow);
        }

        return false;
    }

    private static bool TryUnwrapCallableDelegateType(ITypeSymbol type, out INamedTypeSymbol delegateType)
    {
        delegateType = null!;

        static ITypeSymbol Unalias(ITypeSymbol symbol)
        {
            while (symbol.IsAlias && symbol.UnderlyingSymbol is ITypeSymbol underlying)
                symbol = underlying;

            return symbol;
        }

        type = Unalias(type);

        if (type is NullableTypeSymbol nullable)
            type = nullable.UnderlyingType;

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Delegate } directDelegate)
        {
            delegateType = directDelegate;
            return true;
        }

        if (type is not INamedTypeSymbol named)
            return false;

        if (named.GetDelegateInvokeMethod() is not null)
        {
            delegateType = named;
            return true;
        }

        var definition = (named.OriginalDefinition as INamedTypeSymbol) ?? named;
        if (definition.Arity != 1)
            return false;

        var isExpressionType =
            string.Equals(definition.Name, "Expression", StringComparison.Ordinal) ||
            string.Equals(definition.MetadataName, "Expression`1", StringComparison.Ordinal);
        if (!isExpressionType || named.TypeArguments.Length != 1)
            return false;

        var candidate = Unalias(named.TypeArguments[0]);
        if (candidate is not INamedTypeSymbol expressionDelegate)
            return false;

        if (expressionDelegate.TypeKind != TypeKind.Delegate &&
            expressionDelegate.GetDelegateInvokeMethod() is null)
        {
            return false;
        }

        delegateType = expressionDelegate;
        return true;
    }

    internal bool TryGetFunctionExpressionSymbol(
        FunctionExpressionSyntax functionExpression,
        out IMethodSymbol? functionSymbol)
    {
        if (_functionExpressionSymbolCache.TryGetValue(functionExpression, out var cachedSymbol))
        {
            if (!FunctionExpressionSymbolContainsError(cachedSymbol))
            {
                functionSymbol = cachedSymbol;
                return true;
            }

            if (TryGetUpgradedFunctionExpressionSymbol(functionExpression, out var upgradedSymbol))
            {
                functionSymbol = _functionExpressionSymbolCache.AddOrUpdate(
                    functionExpression,
                    upgradedSymbol,
                    (_, _) => upgradedSymbol);
                return true;
            }

            functionSymbol = cachedSymbol;
            return true;
        }

        if (_functionExpressionSymbolCreationInProgress.TryAdd(functionExpression, 0))
        {
            try
            {
                if (TryCreateShallowFunctionExpressionSymbol(functionExpression, out var shallowSymbol))
                {
                    functionSymbol = _functionExpressionSymbolCache.GetOrAdd(functionExpression, shallowSymbol);
                    return true;
                }
            }
            finally
            {
                _functionExpressionSymbolCreationInProgress.TryRemove(functionExpression, out _);
            }
        }

        if (TryGetUpgradedFunctionExpressionSymbol(functionExpression, out var functionExpressionMethod))
        {
            functionSymbol = functionExpressionMethod;
            return true;
        }

        functionSymbol = null;
        return false;
    }

    private bool TryGetUpgradedFunctionExpressionSymbol(
        FunctionExpressionSyntax functionExpression,
        out IMethodSymbol? functionSymbol)
    {
        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction) &&
            TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, cachedFunction, out var cachedMethod))
        {
            functionSymbol = cachedMethod;
            return true;
        }

        if (TryGetContextualBoundFunctionExpression(functionExpression, out var contextualFunction) &&
            TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, contextualFunction, out var contextualMethod))
        {
            functionSymbol = contextualMethod;
            return true;
        }

        functionSymbol = null;
        return false;
    }

    private static bool FunctionExpressionSymbolContainsError(IMethodSymbol method)
    {
        if (method.ReturnType.ContainsErrorType())
            return true;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ContainsErrorType())
                return true;
        }

        return false;
    }

    private static bool FunctionExpressionSymbolContainsErrorShallow(IMethodSymbol method)
    {
        if (ContainsErrorTypeShallow(method.ReturnType))
            return true;

        foreach (var parameter in method.Parameters)
        {
            if (ContainsErrorTypeShallow(parameter.Type))
                return true;
        }

        return false;
    }

    private bool TryCreateShallowFunctionExpressionSymbol(
        FunctionExpressionSyntax functionExpression,
        out IMethodSymbol? functionSymbol)
    {
        var containingSymbol = TryGetAvailableContainingSymbol(functionExpression) ?? Compilation.GlobalNamespace;

        var containingType = containingSymbol.ContainingType as INamedTypeSymbol;
        var containingNamespace = containingSymbol.ContainingNamespace;
        var delegateInvokeMethod =
            TryGetAvailableOrCachedFunctionExpressionDelegateType(functionExpression, out var delegateType) &&
            delegateType is INamedTypeSymbol namedDelegateType
                ? namedDelegateType.GetDelegateInvokeMethod()
                : null;
        var isAsync = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple => simple.AsyncKeyword.Kind == SyntaxKind.AsyncKeyword,
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.AsyncKeyword.Kind == SyntaxKind.AsyncKeyword,
            _ => false
        };

        var defaultReturnType = isAsync
            ? Compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task)
            : Compilation.GetSpecialType(SpecialType.System_Unit);

        var annotatedReturnTypeSyntax = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple => simple.ReturnType?.Type,
            ParenthesizedFunctionExpressionSyntax parenthesized => parenthesized.ReturnType?.Type,
            _ => null
        };

        ITypeSymbol returnType;
        if (TryResolveShallowFunctionExpressionType(annotatedReturnTypeSyntax, out var resolvedReturnType))
        {
            returnType = resolvedReturnType;
        }
        else if (delegateInvokeMethod?.ReturnType is { TypeKind: not TypeKind.Error } delegateReturnType)
        {
            returnType = delegateReturnType;
        }
        else
        {
            returnType = defaultReturnType;
        }

        var lambdaSymbol = new SourceLambdaSymbol(
            parameters: [],
            returnType,
            containingSymbol,
            containingType,
            containingNamespace,
            [functionExpression.GetLocation()],
            [functionExpression.GetReference()],
            isAsync: isAsync);

        var parameters = functionExpression switch
        {
            SimpleFunctionExpressionSyntax simple when simple.Parameter is not null
                => [CreateShallowFunctionExpressionParameter(lambdaSymbol, simple.Parameter, 0, delegateInvokeMethod)],
            ParenthesizedFunctionExpressionSyntax parenthesized when parenthesized.ParameterList is not null
                => parenthesized.ParameterList.Parameters
                    .Select((parameter, index) => CreateShallowFunctionExpressionParameter(lambdaSymbol, parameter, index, delegateInvokeMethod))
                    .ToArray(),
            _ => []
        };

        lambdaSymbol.SetParameters(parameters);
        functionSymbol = lambdaSymbol;
        return true;
    }

    private bool TryGetContainingExecutableOwnerSymbol(
        SyntaxNode node,
        out ISymbol? symbol)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case FunctionExpressionSyntax functionExpression:
                    if (TryGetFunctionExpressionSymbol(functionExpression, out var functionSymbol))
                    {
                        symbol = functionSymbol;
                        return true;
                    }

                    symbol = null;
                    return false;

                case BaseConstructorDeclarationSyntax:
                case BaseMethodDeclarationSyntax:
                case MacroDeclarationSyntax:
                case ParameterlessConstructorDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case EventDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case GlobalStatementSyntax:
                    return TryGetShallowDeclaredExecutableOwnerSymbol(current, out symbol);

                case CompilationUnitSyntax:
                    symbol = Compilation.GlobalNamespace;
                    return true;
            }
        }

        symbol = null;
        return false;
    }

    private bool TryGetExecutableOwnerSymbol(
        SyntaxNode node,
        out ISymbol? symbol)
    {
        if (!TryGetExecutableOwner(node, out var owner))
        {
            symbol = null;
            return false;
        }

        switch (owner)
        {
            case FunctionExpressionSyntax functionExpression:
                if (TryGetFunctionExpressionSymbol(functionExpression, out var functionSymbol))
                {
                    symbol = functionSymbol;
                    return true;
                }

                symbol = null;
                return false;

            case BaseConstructorDeclarationSyntax:
            case BaseMethodDeclarationSyntax:
            case MacroDeclarationSyntax:
            case ParameterlessConstructorDeclarationSyntax:
            case PropertyDeclarationSyntax:
            case EventDeclarationSyntax:
            case AccessorDeclarationSyntax:
            case GlobalStatementSyntax:
                return TryGetShallowDeclaredExecutableOwnerSymbol(owner, out symbol);

            case CompilationUnitSyntax:
                symbol = Compilation.GlobalNamespace;
                return true;
        }

        symbol = null;
        return false;
    }

    private bool TryGetShallowDeclaredExecutableOwnerSymbol(
        SyntaxNode owner,
        out ISymbol? symbol)
    {
        switch (owner)
        {
            case MethodDeclarationSyntax methodDeclaration when
                TryResolveMethodSymbolForDeclaration(methodDeclaration, out var methodSymbol):
                symbol = methodSymbol;
                return true;

            case MacroDeclarationSyntax macroDeclaration when
                TryGetMacroSymbol(macroDeclaration, out var macroSymbol):
                symbol = macroSymbol;
                return true;

            case BaseConstructorDeclarationSyntax constructorDeclaration when
                TryResolveShallowConstructorSymbol(constructorDeclaration, out var constructorSymbol):
                symbol = constructorSymbol;
                return true;

            case ParameterlessConstructorDeclarationSyntax parameterlessConstructor when
                TryResolveShallowParameterlessConstructorSymbol(parameterlessConstructor, out var parameterlessConstructorSymbol):
                symbol = parameterlessConstructorSymbol;
                return true;

            case PropertyDeclarationSyntax propertyDeclaration when
                TryResolveShallowPropertySymbol(propertyDeclaration, out var propertySymbol):
                symbol = propertySymbol;
                return true;

            case EventDeclarationSyntax eventDeclaration when
                TryResolveShallowEventSymbol(eventDeclaration, out var eventSymbol):
                symbol = eventSymbol;
                return true;

            case AccessorDeclarationSyntax accessorDeclaration when
                TryResolveShallowAccessorSymbol(accessorDeclaration, out var accessorSymbol):
                symbol = accessorSymbol;
                return true;

            case GlobalStatementSyntax:
                symbol = Compilation.GlobalNamespace;
                return true;
        }

        symbol = null;
        return false;
    }

    private bool TryResolveShallowConstructorSymbol(
        BaseConstructorDeclarationSyntax constructorDeclaration,
        out IMethodSymbol? constructorSymbol)
    {
        constructorSymbol = null;

        if (constructorDeclaration.Parent is not TypeDeclarationSyntax containingTypeSyntax ||
            !TryGetClassSymbol(containingTypeSyntax, out var containingType))
        {
            return false;
        }

        var parameterCount = constructorDeclaration.ParameterList?.Parameters.Count ?? 0;
        var targetTree = constructorDeclaration.SyntaxTree;
        var targetSpan = constructorDeclaration.Span;

        constructorSymbol = containingType
            .GetMembers(".ctor")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                method.Parameters.Length == parameterCount &&
                method.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan))
            ?? containingType
                .GetMembers(".ctor")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.Parameters.Length == parameterCount);

        return constructorSymbol is not null;
    }

    private bool TryResolveShallowParameterlessConstructorSymbol(
        ParameterlessConstructorDeclarationSyntax constructorDeclaration,
        out IMethodSymbol? constructorSymbol)
    {
        constructorSymbol = null;

        if (constructorDeclaration.Parent is not TypeDeclarationSyntax containingTypeSyntax ||
            !TryGetClassSymbol(containingTypeSyntax, out var containingType))
        {
            return false;
        }

        var targetTree = constructorDeclaration.SyntaxTree;
        var targetSpan = constructorDeclaration.Span;

        constructorSymbol = containingType
            .GetMembers(".ctor")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                method.Parameters.Length == 0 &&
                method.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan))
            ?? containingType
                .GetMembers(".ctor")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.Parameters.Length == 0);

        return constructorSymbol is not null;
    }

    private bool TryResolveShallowPropertySymbol(
        PropertyDeclarationSyntax propertyDeclaration,
        out IPropertySymbol? propertySymbol)
    {
        propertySymbol = null;

        if (propertyDeclaration.Parent is not TypeDeclarationSyntax containingTypeSyntax ||
            !TryGetClassSymbol(containingTypeSyntax, out var containingType))
        {
            return false;
        }

        var identifierToken = propertyDeclaration.ExplicitInterfaceSpecifier is null
            ? propertyDeclaration.Identifier
            : propertyDeclaration.ExplicitInterfaceSpecifier.Identifier;

        var targetTree = propertyDeclaration.SyntaxTree;
        var targetSpan = propertyDeclaration.Span;

        propertySymbol = containingType
            .GetMembers(identifierToken.ValueText)
            .OfType<IPropertySymbol>()
            .FirstOrDefault(property =>
                property.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan))
            ?? containingType
                .GetMembers(identifierToken.ValueText)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();

        return propertySymbol is not null;
    }

    private bool TryResolveShallowEventSymbol(
        EventDeclarationSyntax eventDeclaration,
        out IEventSymbol? eventSymbol)
    {
        eventSymbol = null;

        if (eventDeclaration.Parent is not TypeDeclarationSyntax containingTypeSyntax ||
            !TryGetClassSymbol(containingTypeSyntax, out var containingType))
        {
            return false;
        }

        var identifierToken = eventDeclaration.ExplicitInterfaceSpecifier is null
            ? eventDeclaration.Identifier
            : eventDeclaration.ExplicitInterfaceSpecifier.Identifier;

        var targetTree = eventDeclaration.SyntaxTree;
        var targetSpan = eventDeclaration.Span;

        eventSymbol = containingType
            .GetMembers(identifierToken.ValueText)
            .OfType<IEventSymbol>()
            .FirstOrDefault(@event =>
                @event.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan))
            ?? containingType
                .GetMembers(identifierToken.ValueText)
                .OfType<IEventSymbol>()
                .FirstOrDefault();

        return eventSymbol is not null;
    }

    private bool TryResolveShallowAccessorSymbol(
        AccessorDeclarationSyntax accessorDeclaration,
        out IMethodSymbol? accessorSymbol)
    {
        accessorSymbol = null;

        if (accessorDeclaration.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is { } propertyDeclaration &&
            TryResolveShallowPropertySymbol(propertyDeclaration, out var propertySymbol))
        {
            accessorSymbol = accessorDeclaration.Keyword.Kind switch
            {
                SyntaxKind.GetKeyword => propertySymbol.GetMethod,
                SyntaxKind.SetKeyword => propertySymbol.SetMethod,
                _ => null
            };

            return accessorSymbol is not null;
        }

        if (accessorDeclaration.Ancestors().OfType<EventDeclarationSyntax>().FirstOrDefault() is { } eventDeclaration &&
            TryResolveShallowEventSymbol(eventDeclaration, out var eventSymbol))
        {
            accessorSymbol = accessorDeclaration.Keyword.Kind switch
            {
                SyntaxKind.AddKeyword => eventSymbol.AddMethod,
                SyntaxKind.RemoveKeyword => eventSymbol.RemoveMethod,
                _ => null
            };

            return accessorSymbol is not null;
        }

        return false;
    }

    private SourceParameterSymbol CreateShallowFunctionExpressionParameter(
        SourceLambdaSymbol lambdaSymbol,
        ParameterSyntax parameterSyntax,
        int parameterIndex = -1,
        IMethodSymbol? delegateInvokeMethod = null)
    {
        ITypeSymbol parameterType;
        RefKind refKind;
        bool hasExplicitDefaultValue;
        object? explicitDefaultValue;
        bool isVarParams;

        if (TryResolveShallowFunctionExpressionType(parameterSyntax.TypeAnnotation?.Type, out var resolvedType))
        {
            parameterType = resolvedType;
            refKind = parameterSyntax.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None
            };
            hasExplicitDefaultValue = false;
            explicitDefaultValue = null;
            isVarParams = false;
        }
        else if (delegateInvokeMethod is not null &&
                 parameterIndex >= 0 &&
                 parameterIndex < delegateInvokeMethod.Parameters.Length &&
                 delegateInvokeMethod.Parameters[parameterIndex] is { } delegateParameter &&
                 delegateParameter.Type is { TypeKind: not TypeKind.Error } delegateParameterType)
        {
            parameterType = delegateParameterType;
            refKind = delegateParameter.RefKind;
            hasExplicitDefaultValue = delegateParameter.HasExplicitDefaultValue;
            explicitDefaultValue = delegateParameter.ExplicitDefaultValue;
            isVarParams = delegateParameter.IsVarParams;
        }
        else
        {
            parameterType = Compilation.ErrorTypeSymbol;
            refKind = parameterSyntax.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None
            };
            hasExplicitDefaultValue = false;
            explicitDefaultValue = null;
            isVarParams = false;
        }

        return new SourceParameterSymbol(
            parameterSyntax.Identifier.ValueText,
            parameterType,
            lambdaSymbol,
            lambdaSymbol.ContainingType as INamedTypeSymbol,
            lambdaSymbol.ContainingNamespace,
            [parameterSyntax.GetLocation()],
            [parameterSyntax.GetReference()],
            refKind,
            hasExplicitDefaultValue,
            explicitDefaultValue,
            parameterSyntax.BindingKeyword.Kind == SyntaxKind.VarKeyword,
            isVarParams);
    }

    private bool TryResolveShallowFunctionExpressionType(
        TypeSyntax? typeSyntax,
        out ITypeSymbol resolvedType)
    {
        resolvedType = Compilation.ErrorTypeSymbol;
        if (typeSyntax is null)
            return false;

        var typeInfo = GetTypeInfo(typeSyntax);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type is not null && type.TypeKind != TypeKind.Error)
        {
            resolvedType = type;
            return true;
        }

        var symbol = GetBinder(typeSyntax).BindReferencedSymbol(typeSyntax).Symbol;
        type = symbol switch
        {
            ITypeSymbol typeSymbol => typeSymbol,
            IAliasSymbol { UnderlyingSymbol: ITypeSymbol aliasedType } => aliasedType,
            _ => null
        };

        if (type is null || type.TypeKind == TypeKind.Error)
            return false;

        resolvedType = type;
        return true;
    }

    internal ISymbol? TryLookupVisibleValueSymbol(ExpressionSyntax expression)
        => expression is IdentifierNameSyntax identifier
            ? TryLookupVisibleValueSymbol(expression, identifier.Identifier.ValueText)
            : null;

    internal ITypeSymbol? GetMacroFragmentExpressionType(
        SyntaxNode invocation,
        ExpressionSyntax expression)
    {
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        if (expression is IdentifierNameSyntax identifier &&
            TryLookupVisibleValueSymbol(
                invocation,
                identifier.Identifier.ValueText,
                allowBindingFallback: true) is { } visibleSymbol)
        {
            return visibleSymbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IEventSymbol @event => @event.Type,
                _ => null,
            };
        }

        return GetBinder(invocation).BindExpression(expression).Type;
    }

    internal TypeInfo GetMacroFragmentTypeInfo(
        SyntaxNode invocation,
        ExpressionSyntax expression)
    {
        var type = GetMacroFragmentExpressionType(invocation, expression);
        return new TypeInfo(type, type);
    }

    internal SymbolInfo GetMacroFragmentSymbolInfo(
        SyntaxNode invocation,
        ExpressionSyntax expression)
    {
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        if (expression is IdentifierNameSyntax identifier &&
            TryLookupVisibleValueSymbol(
                invocation,
                identifier.Identifier.ValueText,
                allowBindingFallback: true) is { } visibleSymbol)
        {
            return new SymbolInfo(visibleSymbol);
        }

        return GetBinder(invocation).BindExpression(expression).GetSymbolInfo();
    }

    internal ImmutableArray<ISymbol> GetVisibleValueSymbols(
        SyntaxNode contextNode,
        bool allowBindingFallback = false)
    {
        var position = contextNode.Span.Start;
        var builder = ImmutableArray.CreateBuilder<ISymbol>();
        var seenDeclarations = new HashSet<SyntaxNode>();

        foreach (var scopeNode in EnumerateVisibleValueScopes(contextNode))
        {
            if (!_visibleValueScopeCache.TryGetValue(scopeNode, out var symbols))
            {
                symbols = GetOrCollectVisibleValueDeclarations(scopeNode);
                _visibleValueScopeCache[scopeNode] = symbols;
            }

            for (var i = 0; i < symbols.Length; i++)
            {
                var candidate = symbols[i];
                if (candidate.Start > position || !seenDeclarations.Add(candidate.DeclarationNode))
                    continue;

                if (TryResolveVisibleValueSymbol(candidate, allowBindingFallback) is { } symbol)
                    builder.Add(symbol);
            }
        }

        return builder.ToImmutable();
    }

    internal ISymbol? TryLookupVisibleValueSymbol(
        SyntaxNode contextNode,
        string name,
        bool allowBindingFallback = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (contextNode is ExpressionSyntax expression &&
            TryResolveInterestLocalSymbol(expression) is { } interestSymbol &&
            string.Equals(interestSymbol.Name, name, StringComparison.Ordinal))
        {
            return interestSymbol;
        }

        if (TryResolveEnclosingFunctionParameterSymbol(contextNode, name, out var functionParameterSymbol))
            return functionParameterSymbol;

        var position = contextNode.Span.Start;

        foreach (var scopeNode in EnumerateVisibleValueScopes(contextNode))
        {
            if (!_visibleValueScopeCache.TryGetValue(scopeNode, out var symbols))
            {
                symbols = GetOrCollectVisibleValueDeclarations(scopeNode);
                _visibleValueScopeCache[scopeNode] = symbols;
            }

            for (var i = 0; i < symbols.Length; i++)
            {
                var candidate = symbols[i];
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal) ||
                    candidate.Start > position)
                {
                    continue;
                }

                if (TryResolveVisibleValueSymbol(candidate, allowBindingFallback) is { } symbol)
                    return symbol;
            }
        }

        return null;
    }

    private bool TryResolveEnclosingFunctionParameterSymbol(
        SyntaxNode contextNode,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IParameterSymbol? parameterSymbol)
    {
        parameterSymbol = null;

        foreach (var ancestor in contextNode.Ancestors())
        {
            IEnumerable<ParameterSyntax> parameters = ancestor switch
            {
                SimpleFunctionExpressionSyntax { Parameter: { } parameter } => [parameter],
                ParenthesizedFunctionExpressionSyntax { ParameterList: { } parameterList } => parameterList.Parameters,
                _ => Enumerable.Empty<ParameterSyntax>()
            };

            foreach (var parameter in parameters)
            {
                if (!string.Equals(parameter.Identifier.ValueText, name, StringComparison.Ordinal))
                    continue;

                if (!TryResolveFunctionExpressionParameterSymbolFast(parameter, out parameterSymbol))
                    return false;

                return parameterSymbol is not null;
            }
        }

        return false;
    }

    private ISymbol? TryResolveInterestLocalSymbol(ExpressionSyntax expression)
    {
        if (expression is not IdentifierNameSyntax identifier ||
            string.IsNullOrWhiteSpace(identifier.Identifier.ValueText))
        {
            return null;
        }

        var name = identifier.Identifier.ValueText;

        foreach (var ancestor in expression.Ancestors())
        {
            switch (ancestor)
            {
                case ForStatementSyntax forStatement
                    when forStatement.Body.Span.Contains(expression.Span):
                    {
                        if (forStatement.Target is IdentifierNameSyntax target &&
                            target.Identifier.ValueText == name)
                        {
                            var iterationType = TryGetForIterationElementType(forStatement.Expression);
                            if (iterationType is not null && iterationType.TypeKind != TypeKind.Error)
                                return CreateSyntheticInterestLocalSymbol(name, iterationType, expression);
                        }

                        if (forStatement.Target is PatternSyntax pattern)
                        {
                            if (TryResolveContextualPatternSymbol(pattern, expression, name, out var patternSymbol))
                                return patternSymbol;

                            var iterationType = TryGetForIterationElementType(forStatement.Expression);
                            if (iterationType is not null &&
                                TryInferPatternDesignationType(pattern, name, iterationType) is { } patternType)
                            {
                                return CreateSyntheticInterestLocalSymbol(name, patternType, expression);
                            }
                        }

                        break;
                    }

                case IfStatementSyntax ifStatement
                    when ifStatement.ThenStatement.Span.Contains(expression.Span):
                    {
                        if (TryResolvePatternDesignationSymbol(ifStatement.Condition, expression, name) is { } patternSymbol)
                            return patternSymbol;

                        break;
                    }

                case IfPatternStatementSyntax ifPatternStatement
                    when ifPatternStatement.ThenStatement.Span.Contains(expression.Span):
                    {
                        if (TryResolvePatternDesignationSymbol(ifPatternStatement.Pattern, expression, name) is { } patternSymbol)
                            return patternSymbol;

                        break;
                    }

                case IfPatternExpressionSyntax ifPatternExpression
                    when ifPatternExpression.Expression.Span.Contains(expression.Span):
                    {
                        if (TryResolvePatternDesignationSymbol(ifPatternExpression.Pattern, expression, name) is { } patternSymbol)
                            return patternSymbol;

                        break;
                    }

                case WhilePatternStatementSyntax whilePatternStatement
                    when whilePatternStatement.Statement.Span.Contains(expression.Span):
                    {
                        if (TryResolvePatternDesignationSymbol(whilePatternStatement.Pattern, expression, name) is { } patternSymbol)
                            return patternSymbol;

                        break;
                    }

                case MatchArmSyntax matchArm:
                    {
                        if (TryResolveContextualPatternSymbol(matchArm.Pattern, expression, name, out var patternSymbol))
                            return patternSymbol;

                        if (GetMatchExpressionScrutinee(matchArm.Parent) is { } matchExpressionScrutinee &&
                            TryGetExpressionType(matchExpressionScrutinee) is { } inputType &&
                            TryInferPatternDesignationType(matchArm.Pattern, name, inputType) is { } patternType)
                        {
                            return CreateSyntheticInterestLocalSymbol(name, patternType, expression);
                        }

                        break;
                    }
            }
        }

        return null;
    }

    private static ExpressionSyntax? GetMatchExpressionScrutinee(SyntaxNode? matchSyntax)
    {
        return matchSyntax switch
        {
            MatchExpressionSyntax matchExpression => matchExpression.Expression,
            PostfixMatchExpressionSyntax matchExpression => matchExpression.Expression,
            _ => null,
        };
    }

    private bool TryResolveContextualPatternSymbol(
        SyntaxNode patternRoot,
        ExpressionSyntax expression,
        string name,
        out ISymbol? symbol)
    {
        symbol = TryResolvePatternDesignationSymbol(patternRoot, expression, name);
        if (symbol is null)
            return false;

        if (GetTypeFromSymbol(symbol) is { TypeKind: not TypeKind.Error } type &&
            type.SpecialType != SpecialType.System_Object)
        {
            return true;
        }

        symbol = null;
        return false;
    }

    private ISymbol? TryResolvePatternDesignationSymbol(SyntaxNode patternRoot, ExpressionSyntax expression, string name)
    {
        var designation = patternRoot
            .DescendantNodesAndSelf()
            .OfType<SingleVariableDesignationSyntax>()
            .Where(single =>
                single.Identifier.ValueText == name &&
                single.Span.Start <= expression.Span.Start)
            .OrderByDescending(static single => single.Span.Start)
            .FirstOrDefault();

        return designation is not null && TryResolveAvailablePatternDesignationSymbol(designation, out var symbol, allowErrorType: true)
            ? symbol
            : null;
    }

    private bool TryResolveAvailablePatternDesignationSymbol(
        SingleVariableDesignationSyntax designation,
        out ILocalSymbol? localSymbol,
        bool allowErrorType = false)
    {
        if (IsExistingAssignmentTargetDesignation(designation))
        {
            localSymbol = null;
            return false;
        }

        if (TryGetCachedBoundNode(designation) is BoundSingleVariableDesignator cachedDesignator &&
            (allowErrorType || !cachedDesignator.Local.Type.ContainsErrorType()))
        {
            localSymbol = cachedDesignator.Local;
            return true;
        }

        return TryBindPatternDesignationForAvailableSymbol(designation, out localSymbol, allowErrorType);
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
        if (IsPatternBindingKeyword(designation.BindingKeyword.Kind))
            return true;

        return designation
            .Ancestors()
            .OfType<VariablePatternSyntax>()
            .Any(pattern =>
                IsPatternBindingKeyword(pattern.BindingKeyword.Kind) &&
                ContainsNode(pattern.Designation, designation));
    }

    private static bool IsPatternBindingKeyword(SyntaxKind kind)
        => kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword;

    private static bool ContainsNode(SyntaxNode root, SyntaxNode node)
        => ReferenceEquals(root.SyntaxTree, node.SyntaxTree) &&
           node.Span.Start >= root.Span.Start &&
           node.Span.End <= root.Span.End;

    private bool TryBindPatternDesignationForAvailableSymbol(
        SingleVariableDesignationSyntax designation,
        out ILocalSymbol? localSymbol,
        bool allowErrorType = false)
    {
        Compilation.EnsureSourceDeclarationsDeclared();
        EnsureDeclarations();

        var bindingOwner = GetPatternDesignationBindingOwner(designation) ?? designation;
        if (bindingOwner is not PatternDeclarationAssignmentStatementSyntax &&
            designation.Ancestors().OfType<BlockStatementSyntax>().LastOrDefault() is { } executableRoot)
        {
            _ = GetBoundNode(executableRoot);
            if (TryGetCachedBoundNode(designation) is BoundSingleVariableDesignator contextualDesignator &&
                (allowErrorType || !contextualDesignator.Local.Type.ContainsErrorType()))
            {
                localSymbol = contextualDesignator.Local;
                return true;
            }
        }

        var binder = GetBinderForIncrementalSemanticQuery(bindingOwner);
        while (binder is not BlockBinder && binder.ParentBinder is not null)
            binder = binder.ParentBinder;

        var declaredSymbol = binder.BindDeclaredSymbol(designation);
        if (declaredSymbol is ILocalSymbol declaredLocal &&
            (allowErrorType || !declaredLocal.Type.ContainsErrorType()))
        {
            localSymbol = declaredLocal;
            return true;
        }

        if (TryGetCachedBoundNode(designation) is BoundSingleVariableDesignator reboundDesignator &&
            (allowErrorType || !reboundDesignator.Local.Type.ContainsErrorType()))
        {
            localSymbol = reboundDesignator.Local;
            return true;
        }

        localSymbol = null;
        return false;
    }

    private static SyntaxNode? GetPatternDesignationBindingOwner(SingleVariableDesignationSyntax designation)
        => designation.Ancestors().FirstOrDefault(static ancestor => ancestor is
            MatchExpressionSyntax or
            PostfixMatchExpressionSyntax or
            MatchStatementSyntax or
            IsPatternExpressionSyntax or
            IfPatternStatementSyntax or
            IfPatternExpressionSyntax or
            WhilePatternStatementSyntax or
            ForStatementSyntax or
            PatternDeclarationAssignmentStatementSyntax);

    private ITypeSymbol? TryInferPatternDesignationType(PatternSyntax pattern, string name, ITypeSymbol expectedType)
    {
        if (pattern is VariablePatternSyntax { Designation: SingleVariableDesignationSyntax designation } &&
            designation.Identifier.ValueText == name)
        {
            return expectedType;
        }

        if (pattern is DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax declarationDesignation } declarationPattern &&
            declarationDesignation.Identifier.ValueText == name)
        {
            return TryGetTypeSyntaxType(declarationPattern.Type) ?? expectedType;
        }

        if (pattern is PositionalPatternSyntax positional)
        {
            if (positional.Designation is SingleVariableDesignationSyntax positionalDesignation &&
                positionalDesignation.Identifier.ValueText == name)
            {
                return expectedType;
            }

            var tupleElements = expectedType is INamedTypeSymbol namedExpectedType
                ? namedExpectedType.TupleElements
                : [];
            if (!tupleElements.IsDefaultOrEmpty)
            {
                for (var i = 0; i < positional.Elements.Count && i < tupleElements.Length; i++)
                {
                    if (TryInferPatternDesignationType(positional.Elements[i].Pattern, name, tupleElements[i].Type) is { } elementType)
                        return elementType;
                }
            }
        }

        if (pattern is SequencePatternSyntax sequence)
        {
            if (sequence.Designation is SingleVariableDesignationSyntax sequenceDesignation &&
                sequenceDesignation.Identifier.ValueText == name)
            {
                return expectedType;
            }

            var elementType = TryGetSequenceElementType(expectedType);
            foreach (var element in sequence.Elements)
            {
                var targetType = element.Prefix.DotDotToken.Kind is SyntaxKind.DotDotToken or SyntaxKind.DotDotDotToken
                    ? expectedType
                    : elementType;
                if (targetType is not null &&
                    TryInferPatternDesignationType(element.Pattern, name, targetType) is { } nestedType)
                {
                    return nestedType;
                }
            }
        }

        if (pattern.DescendantNodesAndSelf()
                .OfType<SingleVariableDesignationSyntax>()
                .Any(designation => designation.Identifier.ValueText == name))
        {
            return expectedType;
        }

        return null;
    }

    private ILocalSymbol CreateSyntheticInterestLocalSymbol(string name, ITypeSymbol type, ExpressionSyntax expression)
    {
        var containingSymbol = GetBinder(expression).ContainingSymbol ?? Compilation.GlobalNamespace;
        return new SourceLocalSymbol(
            name,
            type,
            isMutable: false,
            containingSymbol,
            containingSymbol.ContainingType,
            containingSymbol as INamespaceSymbol ?? containingSymbol.ContainingNamespace,
            locations: [],
            declaringSyntaxReferences: []);
    }

    private ITypeSymbol? TryGetForIterationElementType(ExpressionSyntax expression)
    {
        var collectionType = TryGetExpressionType(expression);
        if (collectionType is null || collectionType.TypeKind == TypeKind.Error)
            return null;

        return TryGetSequenceElementType(collectionType);
    }

    private ITypeSymbol? TryGetExpressionType(ExpressionSyntax expression)
    {
        if (TryGetAvailableTypeInfo(expression, out var availableTypeInfo) &&
            (availableTypeInfo.Type ?? availableTypeInfo.ConvertedType) is { } availableType &&
            availableType.TypeKind != TypeKind.Error)
        {
            return availableType;
        }

        if (TryGetCachedSymbolInfo(expression, out var symbolInfo) &&
            GetTypeFromSymbol(symbolInfo.Symbol?.UnderlyingSymbol) is { } symbolType)
        {
            return symbolType;
        }

        return GetTypeInfo(expression).Type;
    }

    private ITypeSymbol? TryGetTypeSyntaxType(TypeSyntax typeSyntax)
    {
        if (TryGetCachedSymbolInfo(typeSyntax, out var symbolInfo) &&
            symbolInfo.Symbol?.UnderlyingSymbol is ITypeSymbol cachedType)
        {
            return cachedType;
        }

        return GetTypeInfo(typeSyntax).Type;
    }

    private ITypeSymbol? TryGetSequenceElementType(ITypeSymbol collectionType)
    {
        if (collectionType is IArrayTypeSymbol arrayType)
            return arrayType.ElementType;

        if (collectionType.SpecialType == SpecialType.System_String)
            return Compilation.GetSpecialType(SpecialType.System_Char);

        if (collectionType is INamedTypeSymbol namedType)
        {
            foreach (var candidate in EnumerateSelfAndInterfaces(namedType))
            {
                if (candidate.TypeArguments.Length == 1 &&
                    candidate.Name is "IEnumerable" or "IAsyncEnumerable")
                {
                    return candidate.TypeArguments[0];
                }
            }
        }

        return null;

        static IEnumerable<INamedTypeSymbol> EnumerateSelfAndInterfaces(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var iface in type.AllInterfaces)
                yield return iface;
        }
    }

    private IEnumerable<SyntaxNode> EnumerateVisibleValueScopes(SyntaxNode contextNode)
    {
        for (SyntaxNode? current = contextNode; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case BlockStatementSyntax:
                case CompilationUnitSyntax:
                case FunctionStatementSyntax:
                case MacroDeclarationSyntax:
                case MethodDeclarationSyntax:
                case AccessorDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case FunctionExpressionSyntax:
                case ArrowExpressionClauseSyntax:
                    yield return current;
                    break;
            }
        }
    }

    private ImmutableArray<Compilation.VisibleValueDeclaration> GetOrCollectVisibleValueDeclarations(SyntaxNode scopeNode)
    {
        if (!IsCollectingDiagnostics &&
            Compilation.TryGetVisibleValueScopeDeclarations(scopeNode, out var descriptors))
        {
            return ResolveVisibleValueDeclarations(scopeNode, descriptors);
        }

        descriptors = CollectVisibleValueDeclarationDescriptors(scopeNode);
        Compilation.StoreVisibleValueScopeDeclarations(scopeNode, descriptors);
        return ResolveVisibleValueDeclarations(scopeNode, descriptors);
    }

    private ImmutableArray<Compilation.VisibleValueDeclarationDescriptor> CollectVisibleValueDeclarationDescriptors(SyntaxNode scopeNode)
    {
        var builder = ImmutableArray.CreateBuilder<Compilation.VisibleValueDeclarationDescriptor>();

        void AddSymbol(string? symbolName, int start, SyntaxNode? declarationNode)
        {
            if (string.IsNullOrWhiteSpace(symbolName) || declarationNode is null)
                return;

            builder.Add(new Compilation.VisibleValueDeclarationDescriptor(
                symbolName,
                start,
                new Compilation.VisibleValueDeclarationNodeDescriptor(declarationNode.Span, declarationNode.Kind)));
        }

        void AddParameters(IEnumerable<ParameterSyntax> parameters)
        {
            foreach (var parameter in parameters)
                AddSymbol(parameter.Identifier.ValueText, int.MinValue, parameter);
        }

        switch (scopeNode)
        {
            case FunctionStatementSyntax function when function.ParameterList is { } parameterList:
                AddParameters(parameterList.Parameters);
                break;
            case MacroDeclarationSyntax macro:
                AddParameters(macro.ParameterList.Parameters);
                break;
            case MethodDeclarationSyntax method when method.ParameterList is { } parameterList:
                AddParameters(parameterList.Parameters);
                break;
            case ConstructorDeclarationSyntax constructor when constructor.ParameterList is { } ctorParameterList:
                AddParameters(ctorParameterList.Parameters);
                break;
            case OperatorDeclarationSyntax @operator when @operator.ParameterList is { } opParameterList:
                AddParameters(opParameterList.Parameters);
                break;
            case ConversionOperatorDeclarationSyntax conversion when conversion.ParameterList is { } conversionParameterList:
                AddParameters(conversionParameterList.Parameters);
                break;
            case SimpleFunctionExpressionSyntax simpleFunction when simpleFunction.Parameter is { } simpleParameter:
                AddParameters([simpleParameter]);
                break;
            case ParenthesizedFunctionExpressionSyntax parenthesizedFunction when parenthesizedFunction.ParameterList is { } functionParameterList:
                AddParameters(functionParameterList.Parameters);
                break;
        }

        if (scopeNode is BlockStatementSyntax or CompilationUnitSyntax or ArrowExpressionClauseSyntax or FunctionExpressionSyntax)
        {
            foreach (var declarator in DescendantNodesExcludingNestedScopes(scopeNode).OfType<VariableDeclaratorSyntax>())
            {
                AddSymbol(declarator.Identifier.ValueText, declarator.Span.Start, declarator);
            }

            foreach (var designation in DescendantNodesExcludingNestedScopes(scopeNode).OfType<SingleVariableDesignationSyntax>())
            {
                AddSymbol(designation.Identifier.ValueText, designation.Span.Start, designation);
            }
        }

        if (scopeNode is BlockStatementSyntax or BlockSyntax)
        {
            SyntaxNode? patternOwner = scopeNode.Parent switch
            {
                ForStatementSyntax forStatement => forStatement,
                IfPatternStatementSyntax ifPatternStatement => ifPatternStatement,
                IfPatternExpressionSyntax ifPatternExpression => ifPatternExpression,
                WhilePatternStatementSyntax whilePatternStatement => whilePatternStatement,
                _ => null
            };

            if (patternOwner is not null)
            {
                foreach (var designation in DescendantNodesExcludingNestedScopes(patternOwner).OfType<SingleVariableDesignationSyntax>())
                {
                    AddSymbol(designation.Identifier.ValueText, designation.Span.Start, designation);
                }
            }
        }

        return builder
            .OrderByDescending(static symbol => symbol.Start)
            .ToImmutableArray();
    }

    private ImmutableArray<Compilation.VisibleValueDeclaration> ResolveVisibleValueDeclarations(
        SyntaxNode scopeNode,
        ImmutableArray<Compilation.VisibleValueDeclarationDescriptor> descriptors)
    {
        if (descriptors.IsDefaultOrEmpty)
            return ImmutableArray<Compilation.VisibleValueDeclaration>.Empty;

        var root = scopeNode.SyntaxTree?.GetRoot()
            ?? scopeNode.AncestorsAndSelf().Last();
        var builder = ImmutableArray.CreateBuilder<Compilation.VisibleValueDeclaration>(descriptors.Length);

        foreach (var descriptor in descriptors)
        {
            if (!TryResolveVisibleValueDeclarationNode(root, descriptor.Declaration, out var declarationNode))
                continue;

            builder.Add(new Compilation.VisibleValueDeclaration(descriptor.Name, descriptor.Start, declarationNode));
        }

        return builder.ToImmutable();
    }

    private static bool TryResolveVisibleValueDeclarationNode(
        SyntaxNode treeRoot,
        Compilation.VisibleValueDeclarationNodeDescriptor descriptor,
        out SyntaxNode declarationNode)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                declarationNode = current;
                return true;
            }
        }

        declarationNode = null!;
        return false;
    }

    internal ImmutableArray<Compilation.VisibleValueDeclaration> GetVisibleValueDeclarationsForTesting(SyntaxNode scopeNode)
        => GetOrCollectVisibleValueDeclarations(scopeNode);

    private static bool IsNestedExecutableScope(SyntaxNode node)
        => node is FunctionStatementSyntax
            or MacroDeclarationSyntax
            or MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax
            or FunctionExpressionSyntax
            or AccessorDeclarationSyntax
            or BlockStatementSyntax
            or ArrowExpressionClauseSyntax;

    private static IEnumerable<SyntaxNode> DescendantNodesExcludingNestedScopes(SyntaxNode scopeNode)
    {
        foreach (var child in scopeNode.ChildNodes())
        {
            var isCurrentBlockBody = scopeNode is BlockStatementSyntax && child is BlockSyntax;
            var isCurrentFunctionExpressionBody = scopeNode is FunctionExpressionSyntax && child is BlockStatementSyntax;
            if (!isCurrentBlockBody && !isCurrentFunctionExpressionBody && IsNestedExecutableScope(child))
                continue;

            yield return child;

            foreach (var descendant in DescendantNodesExcludingNestedScopes(child))
                yield return descendant;
        }
    }

    private ISymbol? TryResolveVisibleValueSymbol(
        Compilation.VisibleValueDeclaration declaration,
        bool allowBindingFallback = false)
        => declaration.DeclarationNode switch
        {
            ParameterSyntax parameter => TryResolveFunctionExpressionParameterSymbolFast(parameter, out var fastFunctionParameter)
                ? fastFunctionParameter
                : parameter.Ancestors().OfType<FunctionExpressionSyntax>().Any()
                ? null
                : TryResolveParameterSymbolFast(parameter, out var parameterSymbol)
                ? parameterSymbol
                : TryResolveAvailableParameterSymbol(parameter, out var availableParameterSymbol)
                ? availableParameterSymbol
                : null,
            VariableDeclaratorSyntax variableDeclarator => IsLocalVariableDeclarator(variableDeclarator) &&
                  TryGetAvailableLocalDeclarationSymbol(
                      variableDeclarator,
                      out var localSymbol,
                      allowErrorType: true,
                      allowInitializerBinding: true,
                      allowBindingFallback)
                ? localSymbol
                : null,
            SingleVariableDesignationSyntax => null,
            _ => null
        };

    private ISymbol? TryGetAvailableContainingSymbol(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case TypeDeclarationSyntax typeDeclaration when TryGetClassSymbol(typeDeclaration, out var typeSymbol):
                    return typeSymbol;
                case UnionDeclarationSyntax unionDeclaration when TryGetUnionSymbol(unionDeclaration, out var unionSymbol):
                    return unionSymbol;
                case MacroDeclarationSyntax macroDeclaration
                    when TryGetMacroSymbol(macroDeclaration, out var macroSymbol):
                    return macroSymbol;
                case NamespaceDeclarationSyntax:
                    return GetDeclaredSymbol(ancestor);
            }
        }

        return null;
    }

    private bool TryResolveParameterSymbolFast(ParameterSyntax parameterSyntax, out IParameterSymbol? parameterSymbol)
    {
        parameterSymbol = null;

        if (parameterSyntax.Parent?.Parent is TypeDeclarationSyntax parameterContainingType &&
            TryGetClassSymbol(parameterContainingType, out var containingType))
        {
            parameterSymbol = containingType
                .GetMembers(".ctor")
                .OfType<IMethodSymbol>()
                .SelectMany(method => method.Parameters)
                .FirstOrDefault(parameter => SymbolDeclarationUtilities.HasDeclaringSpan(parameter, parameterSyntax));
            return parameterSymbol is not null;
        }

        if (parameterSyntax.Parent?.Parent is MethodDeclarationSyntax methodDeclaration &&
            TryResolveMethodSymbolForDeclaration(methodDeclaration, out var methodSymbol))
        {
            parameterSymbol = methodSymbol.Parameters.FirstOrDefault(parameter =>
                SymbolDeclarationUtilities.HasDeclaringSpan(parameter, parameterSyntax));
            return parameterSymbol is not null;
        }

        if (parameterSyntax.Parent?.Parent is CaseDeclarationSyntax caseDeclaration &&
            TryGetUnionCaseSymbol(caseDeclaration, out var caseSymbol))
        {
            parameterSymbol = caseSymbol.ConstructorParameters.FirstOrDefault(parameter =>
                SymbolDeclarationUtilities.HasDeclaringSpan(parameter, parameterSyntax));
            return parameterSymbol is not null;
        }

        if (parameterSyntax.Parent?.Parent is FunctionStatementSyntax functionStatement &&
            TryResolveAvailableFunctionStatementSymbol(functionStatement, out var functionSymbol))
        {
            parameterSymbol = functionSymbol.Parameters.FirstOrDefault(parameter =>
                SymbolDeclarationUtilities.HasDeclaringSpan(parameter, parameterSyntax));
            return parameterSymbol is not null;
        }

        if (parameterSyntax.Parent?.Parent is MacroDeclarationSyntax macroDeclaration &&
            TryGetMacroSymbol(macroDeclaration, out var macroSymbol))
        {
            parameterSymbol = macroSymbol.Parameters.FirstOrDefault(parameter =>
                SymbolDeclarationUtilities.HasDeclaringSpan(parameter, parameterSyntax));
            return parameterSymbol is not null;
        }

        return false;
    }

    private bool TryResolveAvailableParameterSymbol(ParameterSyntax parameterSyntax, out IParameterSymbol? parameterSymbol)
    {
        parameterSymbol = null;

        if (parameterSyntax.Ancestors().OfType<FunctionExpressionSyntax>().Any())
            return false;

        var parameterType = parameterSyntax.TypeAnnotation?.Type is { } typeSyntax &&
                            TryGetAvailableFunctionParameterType(typeSyntax, out var annotatedType) &&
                            annotatedType.TypeKind != TypeKind.Error
            ? annotatedType
            : Compilation.ErrorTypeSymbol;

        var containingSymbol =
            parameterSyntax.Parent?.Parent is MethodDeclarationSyntax methodDeclaration &&
            TryResolveMethodSymbolForDeclaration(methodDeclaration, out var methodSymbol)
                ? methodSymbol
                : TryGetAvailableContainingSymbol(parameterSyntax) ?? Compilation.GlobalNamespace;
        var containingType = containingSymbol as INamedTypeSymbol ?? containingSymbol.ContainingType as INamedTypeSymbol;
        var containingNamespace = containingSymbol as INamespaceSymbol ?? containingSymbol.ContainingNamespace;
        var refKind = parameterSyntax.RefKindKeyword.Kind switch
        {
            SyntaxKind.RefKeyword => RefKind.Ref,
            SyntaxKind.OutKeyword => RefKind.Out,
            SyntaxKind.InKeyword => RefKind.In,
            _ => RefKind.None
        };

        parameterSymbol = new SourceParameterSymbol(
            parameterSyntax.Identifier.ValueText,
            parameterType,
            containingSymbol,
            containingType,
            containingNamespace,
            [parameterSyntax.Identifier.GetLocation()],
            [parameterSyntax.GetReference()],
            refKind,
            hasExplicitDefaultValue: false,
            explicitDefaultValue: null,
            isMutable: parameterSyntax.BindingKeyword.Kind == SyntaxKind.VarKeyword,
            isVarParams: false);
        return true;
    }

    internal bool TryResolveFunctionExpressionParameterSymbolFast(
        ParameterSyntax parameterSyntax,
        out IParameterSymbol? parameterSymbol,
        bool allowCandidateLookup = true)
    {
        Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordFastAttempt();
        parameterSymbol = null;

        var functionExpression = parameterSyntax.Ancestors().OfType<FunctionExpressionSyntax>().FirstOrDefault();
        if (functionExpression is null ||
            !TryGetFunctionParameterIndex(functionExpression, parameterSyntax, out var parameterIndex))
        {
            return false;
        }

        ITypeSymbol parameterType;
        RefKind refKind;
        bool hasExplicitDefaultValue;
        object? explicitDefaultValue;
        bool isVarParams;

        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            cachedFunction.Parameters.ElementAtOrDefault(parameterIndex) is { Type: { TypeKind: not TypeKind.Error } cachedBoundParameterType } cachedBoundParameter &&
            !ContainsTypeParameter(cachedBoundParameterType) &&
            !ContainsErrorTypeShallow(cachedBoundParameterType))
        {
            parameterSymbol = cachedBoundParameter;
            Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordFastBoundCacheHit();
            return true;
        }

        if (parameterSyntax.TypeAnnotation?.Type is { } typeSyntax)
        {
            parameterType = TryGetAvailableFunctionParameterType(typeSyntax, out var annotatedType) &&
                annotatedType.TypeKind != TypeKind.Error
                    ? annotatedType
                    : Compilation.ErrorTypeSymbol;
            refKind = parameterSyntax.RefKindKeyword.Kind switch
            {
                SyntaxKind.RefKeyword => RefKind.Ref,
                SyntaxKind.OutKeyword => RefKind.Out,
                SyntaxKind.InKeyword => RefKind.In,
                _ => RefKind.None
            };
            hasExplicitDefaultValue = false;
            explicitDefaultValue = null;
            isVarParams = false;
        }
        else
        {
            if (_functionExpressionSymbolCache.TryGetValue(functionExpression, out var cachedFunctionSymbol) &&
                !FunctionExpressionSymbolContainsErrorShallow(cachedFunctionSymbol) &&
                parameterIndex < cachedFunctionSymbol.Parameters.Length &&
                cachedFunctionSymbol.Parameters[parameterIndex] is { Type: { TypeKind: not TypeKind.Error } cachedParameterType } cachedParameter &&
                !ContainsTypeParameter(cachedParameterType))
            {
                parameterSymbol = cachedParameter;
                Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordFastSymbolCacheHit();
                return true;
            }

            if (!TryGetAvailableOrCachedFunctionExpressionDelegateType(functionExpression, out var delegateType, allowCandidateLookup))
            {
                return false;
            }

            if (delegateType is not INamedTypeSymbol namedDelegate)
            {
                return false;
            }

            if (!TryGetCallableDelegateParameter(
                    namedDelegate,
                    parameterIndex,
                    out var delegateParameterType,
                    out var delegateRefKind,
                    out var delegateHasExplicitDefaultValue,
                    out var delegateExplicitDefaultValue,
                    out var delegateIsVarParams))
            {
                return false;
            }

            if (delegateParameterType.TypeKind == TypeKind.Error)
            {
                return false;
            }

            if (ContainsTypeParameter(delegateParameterType))
            {
                return false;
            }

            parameterType = delegateParameterType;
            refKind = delegateRefKind;
            hasExplicitDefaultValue = delegateHasExplicitDefaultValue;
            explicitDefaultValue = delegateExplicitDefaultValue;
            isVarParams = delegateIsVarParams;
        }

        var containingSymbol = TryGetAvailableContainingSymbol(functionExpression) ?? Compilation.GlobalNamespace;
        var containingType = containingSymbol as INamedTypeSymbol;
        var containingNamespace = containingSymbol as INamespaceSymbol ?? containingSymbol.ContainingNamespace;
        var isMutable = parameterSyntax.BindingKeyword.Kind == SyntaxKind.VarKeyword;

        parameterSymbol = new SourceParameterSymbol(
            parameterSyntax.Identifier.ValueText,
            parameterType,
            containingSymbol,
            containingType,
            containingNamespace,
            [parameterSyntax.GetLocation()],
            [parameterSyntax.GetReference()],
            refKind,
            hasExplicitDefaultValue,
            explicitDefaultValue,
            isMutable,
            isVarParams);
        Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordFastDelegateHit();
        return true;
    }

    private bool TryGetCallableDelegateParameter(
        INamedTypeSymbol delegateType,
        int parameterIndex,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? parameterType,
        out RefKind refKind,
        out bool hasExplicitDefaultValue,
        out object? explicitDefaultValue,
        out bool isVarParams)
    {
        parameterType = null;
        refKind = RefKind.None;
        hasExplicitDefaultValue = false;
        explicitDefaultValue = null;
        isVarParams = false;

        if (delegateType.GetDelegateInvokeMethod() is { } invokeMethod)
        {
            if (parameterIndex < 0 || parameterIndex >= invokeMethod.Parameters.Length)
                return false;

            var parameter = invokeMethod.Parameters[parameterIndex];
            parameterType = parameter.Type;
            refKind = parameter.RefKind;
            hasExplicitDefaultValue = parameter.HasExplicitDefaultValue;
            explicitDefaultValue = parameter.ExplicitDefaultValue;
            isVarParams = parameter.IsVarParams;
            return parameterType is not null;
        }

        if (!TryGetSystemFuncOrActionParameterTypes(delegateType, out var parameterTypes) ||
            parameterIndex < 0 ||
            parameterIndex >= parameterTypes.Length)
        {
            return false;
        }

        parameterType = parameterTypes[parameterIndex];
        return parameterType is not null;
    }

    private static bool TryGetSystemFuncOrActionParameterTypes(
        INamedTypeSymbol delegateType,
        out ImmutableArray<ITypeSymbol> parameterTypes)
    {
        parameterTypes = default;

        if (delegateType.TypeKind != TypeKind.Delegate)
            return false;

        var definition = (delegateType.OriginalDefinition as INamedTypeSymbol) ?? delegateType;
        var metadataName = definition.MetadataName;
        var name = definition.Name;
        var typeArguments = TypeSubstitution.GetShallowTypeArguments(delegateType);

        if (string.Equals(name, "Func", StringComparison.Ordinal) ||
            metadataName.StartsWith("Func`", StringComparison.Ordinal))
        {
            if (typeArguments.IsDefaultOrEmpty)
                return false;

            parameterTypes = typeArguments.RemoveAt(typeArguments.Length - 1);
            return true;
        }

        if (string.Equals(name, "Action", StringComparison.Ordinal) ||
            metadataName.StartsWith("Action`", StringComparison.Ordinal))
        {
            parameterTypes = typeArguments.IsDefault ? ImmutableArray<ITypeSymbol>.Empty : typeArguments;
            return true;
        }

        return false;
    }

    private bool TryGetAvailableFunctionParameterType(TypeSyntax typeSyntax, out ITypeSymbol type)
    {
        if (TryGetAvailablePredefinedTypeInfo(typeSyntax, out var predefinedTypeInfo) &&
            (predefinedTypeInfo.Type ?? predefinedTypeInfo.ConvertedType) is { } predefinedType)
        {
            type = predefinedType;
            return true;
        }

        if (typeSyntax is SimpleNameSyntax importedTypeName &&
            TryLookupImportedTypeBySyntax(importedTypeName, out var importedType) &&
            TryConstructAvailableNamedType(importedTypeName, importedType, out var constructedImportedType))
        {
            type = constructedImportedType;
            return true;
        }

        if (typeSyntax is SimpleNameSyntax typeName &&
            TryLookupAvailableTypeFromBinder(typeName, out var binderType) &&
            TryConstructAvailableNamedType(typeName, binderType, out var constructedBinderType))
        {
            type = constructedBinderType;
            return true;
        }

        if (TryGetAvailableTypeInfo(typeSyntax, out var typeInfo) &&
            (typeInfo.Type ?? typeInfo.ConvertedType) is { } availableType)
        {
            type = availableType;
            return true;
        }

        if (typeSyntax is SimpleNameSyntax simpleName &&
            TryResolveAvailableNamedTypeSyntax(simpleName, out var namedType) &&
            namedType is not null)
        {
            type = namedType;
            return true;
        }

        type = null!;
        return false;
    }

    private bool TryResolveAvailableNamedTypeSyntax(SimpleNameSyntax typeName, out ITypeSymbol? type)
    {
        var arity = typeName is GenericNameSyntax genericName ? genericName.TypeArgumentList.Arguments.Count : 0;
        if (!TryLookupAvailableNamedType(typeName.Identifier.ValueText, arity, out var namedType) ||
            namedType is null)
        {
            type = null;
            return false;
        }

        return TryConstructAvailableNamedType(typeName, namedType, out type);
    }

    private bool TryConstructAvailableNamedType(
        SimpleNameSyntax typeName,
        ITypeSymbol? candidate,
        out ITypeSymbol? type)
    {
        if (candidate is not INamedTypeSymbol namedType ||
            namedType.TypeKind == TypeKind.Error)
        {
            type = null;
            return false;
        }

        if (typeName is GenericNameSyntax genericTypeName)
        {
            var arity = genericTypeName.TypeArgumentList.Arguments.Count;
            if (namedType.TypeParameters.Length != arity)
            {
                type = null;
                return false;
            }

            var typeArguments = ResolveAvailableTypeArguments(genericTypeName.TypeArgumentList);
            if (typeArguments.IsDefault || typeArguments.Length != arity)
            {
                type = null;
                return false;
            }

            type = namedType.Construct(typeArguments.ToArray());
            return true;
        }

        type = namedType;
        return true;
    }

    private bool TryLookupImportedTypeBySyntax(SimpleNameSyntax typeName, out ITypeSymbol? type)
    {
        var metadataTypeName = GetMetadataTypeName(
            typeName.Identifier.ValueText,
            typeName is GenericNameSyntax genericName ? genericName.TypeArgumentList.Arguments.Count : 0);

        foreach (var import in EnumerateImportDirectives(typeName))
        {
            var importName = import.Name.ToString();
            if (!importName.EndsWith(".*", StringComparison.Ordinal))
                continue;

            var namespaceName = importName[..^2];
            type = Compilation.SymbolLookup.GetTypeByMetadataNameMetadataOnly(namespaceName + "." + metadataTypeName);
            if (type is not null)
                return true;
        }

        type = null;
        return false;
    }

    private static IEnumerable<ImportDirectiveSyntax> EnumerateImportDirectives(SyntaxNode node)
    {
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case CompilationUnitSyntax compilationUnit:
                    foreach (var import in compilationUnit.Imports)
                        yield return import;
                    break;
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    foreach (var import in namespaceDeclaration.Imports)
                        yield return import;
                    break;
            }
        }
    }

    private bool TryGetAvailableOrCachedFunctionExpressionDelegateType(
        FunctionExpressionSyntax functionExpression,
        out ITypeSymbol? delegateType,
        bool allowCandidateLookup = true)
    {
        if (_functionExpressionDelegateTypeCache.TryGetValue(functionExpression, out var cachedDelegateType) &&
            cachedDelegateType.TypeKind != TypeKind.Error &&
            !ContainsTypeParameter(cachedDelegateType) &&
            !ContainsErrorTypeShallow(cachedDelegateType))
        {
            delegateType = cachedDelegateType;
            return true;
        }

        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction) &&
            cachedFunction.DelegateType is not null &&
            cachedFunction.DelegateType.TypeKind != TypeKind.Error)
        {
            delegateType = cachedFunction.DelegateType;
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        if (TryGetTargetTypedFunctionExpressionDelegateType(functionExpression, out delegateType))
        {
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        if (TryGetAvailableFunctionExpressionDelegateType(functionExpression, out delegateType) &&
            delegateType is not null &&
            delegateType.TypeKind != TypeKind.Error)
        {
            CacheFunctionExpressionDelegateType(functionExpression, delegateType);
            return true;
        }

        if (!allowCandidateLookup)
        {
            delegateType = null;
            return false;
        }

        return false;
    }

    private bool TryGetTargetTypedFunctionExpressionDelegateType(
        FunctionExpressionSyntax functionExpression,
        out ITypeSymbol? delegateType)
    {
        delegateType = null;

        if (functionExpression.Parent is not EqualsValueClauseSyntax initializer ||
            !IsSameSyntaxNode(initializer.Value, functionExpression) ||
            initializer.Parent is not VariableDeclaratorSyntax
            {
                TypeAnnotation.Type: { } targetTypeSyntax
            })
        {
            return false;
        }

        ITypeSymbol? targetType = null;
        if (TryGetAvailableFunctionParameterType(targetTypeSyntax, out var availableTargetType))
            targetType = availableTargetType;
        else if (TryGetAvailableTypeInfo(targetTypeSyntax, out var targetTypeInfo))
            targetType = targetTypeInfo.Type ?? targetTypeInfo.ConvertedType;

        if ((targetType is null || targetType.TypeKind == TypeKind.Error || ContainsTypeParameter(targetType)) &&
            TryBindAvailableTypeSyntax(targetTypeSyntax, out var boundTargetType))
        {
            targetType = boundTargetType;
        }

        if (targetType is null || targetType.TypeKind == TypeKind.Error)
            return false;

        if (!TryUnwrapCallableDelegateType(targetType, out var callableDelegate))
            return false;

        delegateType = callableDelegate;
        return true;
    }

    private static ITypeSymbol? GetTypeFromSymbol(ISymbol? symbol)
    {
        while (symbol is not null)
        {
            switch (symbol)
            {
                case ILocalSymbol local:
                    return local.Type;
                case IFieldSymbol field:
                    return field.Type;
                case IPropertySymbol property:
                    return property.Type;
                case IEventSymbol @event:
                    return @event.Type;
                case IParameterSymbol parameter:
                    return parameter.Type;
                case IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } containingType }:
                    return containingType;
                case IMethodSymbol method:
                    return method.ReturnType;
                case ITypeSymbol type:
                    return type;
            }

            var underlying = symbol.UnderlyingSymbol;
            if (ReferenceEquals(underlying, symbol))
                break;

            symbol = underlying;
        }

        return null;
    }

    private bool TryResolveInvocationOperatorFromReceiver(InvocationExpressionSyntax invocation, out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (TryGetAvailableTypeInfo(invocation.Expression, out var expressionTypeInfo))
        {
            var expressionType = expressionTypeInfo.Type ?? expressionTypeInfo.ConvertedType;
            if (TryResolveInvocationOperatorFromReceiverType(expressionType, invocation, out info))
                return true;
        }

        if (TryGetAvailableSymbolInfo(invocation.Expression, out var availableExpressionInfo) &&
            TryResolveInvocationOperatorFromReceiverType(
                GetTypeFromSymbol(availableExpressionInfo.Symbol?.UnderlyingSymbol ?? availableExpressionInfo.Symbol),
                invocation,
                out info))
        {
            return true;
        }

        if (invocation.Expression is IdentifierNameSyntax receiverIdentifier &&
            TryResolveEnclosingParameterType(receiverIdentifier, out var parameterType) &&
            TryResolveInvocationOperatorFromReceiverType(parameterType, invocation, out info))
        {
            return true;
        }

        var binderInfo = GetBinder(invocation.Expression).BindSymbol(invocation.Expression);
        if (TryResolveInvocationOperatorFromReceiverType(
            GetTypeFromSymbol(binderInfo.Symbol?.UnderlyingSymbol ?? binderInfo.Symbol),
            invocation,
            out info))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveEnclosingParameterType(
        IdentifierNameSyntax identifier,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? type)
    {
        type = null;
        var name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var methodDeclaration = identifier.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDeclaration is not null &&
            TryResolveParameterTypeSyntax(methodDeclaration.ParameterList?.Parameters, name, out type))
        {
            return true;
        }

        if (methodDeclaration is not null &&
            GetDeclaredSymbol(methodDeclaration) is IMethodSymbol method)
        {
            type = method.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Type;
            return type is not null && type.TypeKind != TypeKind.Error;
        }

        var functionStatement = identifier.Ancestors().OfType<FunctionStatementSyntax>().FirstOrDefault();
        if (functionStatement is not null &&
            TryResolveParameterTypeSyntax(functionStatement.ParameterList?.Parameters, name, out type))
        {
            return true;
        }

        if (functionStatement is not null &&
            TryResolveAvailableFunctionStatementSymbol(functionStatement, out var function))
        {
            type = function.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Type;
            return type is not null && type.TypeKind != TypeKind.Error;
        }

        return false;

        bool TryResolveParameterTypeSyntax(
            SeparatedSyntaxList<ParameterSyntax>? parameters,
            string parameterName,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITypeSymbol? parameterType)
        {
            parameterType = null;
            if (parameters is null)
                return false;

            foreach (var parameter in parameters.Value)
            {
                if (parameter.Identifier.ValueText != parameterName ||
                    parameter.TypeAnnotation?.Type is not { } typeSyntax)
                {
                    continue;
                }

                if (TryResolveAvailableTypeSyntax(typeSyntax, out parameterType) &&
                    parameterType.TypeKind != TypeKind.Error)
                {
                    return true;
                }

                if (typeSyntax is NullableTypeSyntax nullableType &&
                    TryResolveAvailableTypeSyntax(nullableType.ElementType, out parameterType) &&
                    parameterType.TypeKind != TypeKind.Error)
                {
                    return true;
                }

                return false;
            }

            return false;
        }
    }

    private static bool TryResolveInvocationOperatorFromReceiverType(
        ITypeSymbol? receiverType,
        InvocationExpressionSyntax invocation,
        out SymbolInfo info)
    {
        info = SymbolInfo.None;

        if (receiverType is NullableTypeSymbol nullableReceiver)
            receiverType = nullableReceiver.UnderlyingType;

        if (receiverType?.GetNonNullableType() is not INamedTypeSymbol namedReceiverType ||
            namedReceiverType.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var candidates = namedReceiverType
            .GetMembers("Invoke")
            .OfType<IMethodSymbol>()
            .Where(static method => !method.IsStatic)
            .Where(method => method.Parameters.Length == invocation.ArgumentList.Arguments.Count)
            .Cast<ISymbol>()
            .ToImmutableArray();

        if (candidates.Length == 0)
            return false;

        info = candidates.Length == 1
            ? new SymbolInfo(candidates[0])
            : new SymbolInfo(CandidateReason.OverloadResolutionFailure, candidates);
        return true;
    }

    private bool TryBindInterestRegion(
        ExpressionSyntax expression,
        out BoundExpression boundExpression,
        bool includeExtendedExecutableRoots = false)
    {
        var regionRoot = GetInterestBindingRoot(expression, includeExtendedExecutableRoots);

        if (regionRoot is not null)
        {
            if (!TryGetBoundNodeForSemanticQuery(regionRoot, out var boundRegion))
            {
                boundExpression = null!;
                return false;
            }

            if (TryFindBoundNodeBySyntax(boundRegion, expression, out var reboundNode) &&
                reboundNode is BoundExpression reboundExpression)
            {
                if (IsLikelyStaleInitializerExpression(expression, reboundExpression))
                {
                    BoundNode? reboundContextRoot = null;

                    if (TryGetEnclosingFunctionExpression(expression, out var enclosingFunctionExpression) &&
                        TryGetContextualBindingRoot(enclosingFunctionExpression, out var contextualRoot) &&
                        !ReferenceEquals(contextualRoot, enclosingFunctionExpression))
                    {
                        ClearCachedSemanticState(contextualRoot);
                        if (!TryGetBoundNodeForSemanticQuery(contextualRoot, out reboundContextRoot))
                            reboundContextRoot = null;
                    }

                    if (reboundContextRoot is null)
                    {
                        ClearCachedSemanticState(regionRoot);
                        if (!TryGetBoundNodeForSemanticQuery(regionRoot, out reboundContextRoot))
                            reboundContextRoot = null;
                    }

                    if (reboundContextRoot is not null &&
                        TryFindBoundNodeBySyntax(reboundContextRoot, expression, out var reboundRefreshedNode) &&
                        reboundRefreshedNode is BoundExpression reboundRefreshedExpression)
                    {
                        reboundExpression = reboundRefreshedExpression;
                    }
                }

                boundExpression = reboundExpression;
                return true;
            }
        }

        boundExpression = null!;
        return false;
    }

    private bool IsLikelyStaleInitializerExpression(ExpressionSyntax expression, BoundExpression reboundExpression)
    {
        if (reboundExpression.Type is null || !reboundExpression.Type.ContainsErrorType())
            return false;

        var declarator = expression.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (declarator?.Initializer?.Value is null)
            return false;

        if (TryGetCachedBoundNode(declarator) is not BoundVariableDeclarator boundDeclarator ||
            boundDeclarator.Local.Type.ContainsErrorType())
        {
            return false;
        }

        return true;
    }

    private void EnsureDeclarationInterestBound(ISymbol symbol)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxReference.GetSyntax();
            if (syntax is null)
                continue;

            var declarationRoot = GetInterestBindingRoot(syntax, includeExtendedExecutableRoots: true);

            if (declarationRoot is null)
                continue;

            _ = GetBoundNode(declarationRoot, BoundTreeView.Original);
            return;
        }
    }

    private SyntaxNode? GetInterestBindingRoot(SyntaxNode node, bool includeExtendedExecutableRoots)
    {
        if (!includeExtendedExecutableRoots &&
            Compilation.TryGetInterestBindingRootDescriptor(node, out var cachedDescriptor) &&
            TryResolveInterestBindingRootDescriptor(node.SyntaxTree.GetRoot(), cachedDescriptor, out var cachedRoot))
        {
            return cachedRoot;
        }

        SyntaxNode? root = null;
        if (includeExtendedExecutableRoots)
        {
            root = node.AncestorsAndSelf().FirstOrDefault(current =>
                current is BlockStatementSyntax &&
                current.Parent is BaseMethodDeclarationSyntax or FunctionStatementSyntax or MacroDeclarationSyntax or AccessorDeclarationSyntax);

            root ??= node.AncestorsAndSelf().FirstOrDefault(current =>
                current is IfStatementSyntax or IfPatternStatementSyntax or WhileStatementSyntax or WhilePatternStatementSyntax or ForStatementSyntax);
        }

        root ??= node.AncestorsAndSelf().FirstOrDefault(current =>
            current is StatementSyntax or ArrowExpressionClauseSyntax);

        if (root is not null)
        {
            Compilation.StoreInterestBindingRootDescriptor(
                node,
                new Compilation.InterestBindingRootDescriptor(root.Span, root.Kind));
        }

        return root;
    }

    private static bool TryResolveInterestBindingRootDescriptor(
        SyntaxNode treeRoot,
        Compilation.InterestBindingRootDescriptor descriptor,
        out SyntaxNode root)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                root = current;
                return true;
            }
        }

        root = null!;
        return false;
    }

    private bool TryGetExecutableOwner(SyntaxNode node, out SyntaxNode owner)
    {
        if (Compilation.TryGetExecutableOwnerDescriptor(node, out var cachedDescriptor) &&
            TryResolveExecutableOwnerDescriptor(node.SyntaxTree.GetRoot(), cachedDescriptor, out owner))
        {
            return !ReferenceEquals(owner, node);
        }

        owner = node.AncestorsAndSelf().FirstOrDefault(static current =>
            current is FunctionExpressionSyntax
                or FunctionStatementSyntax
                or MacroDeclarationSyntax
                or BaseMethodDeclarationSyntax
                or BaseConstructorDeclarationSyntax
                or ParameterlessConstructorDeclarationSyntax
                or AccessorDeclarationSyntax
                or PropertyDeclarationSyntax
                or EventDeclarationSyntax
                or GlobalStatementSyntax
                or CompilationUnitSyntax)
            ?? node;

        Compilation.StoreExecutableOwnerDescriptor(
            node,
            new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind));
        return !ReferenceEquals(owner, node);
    }

    private static bool TryResolveExecutableOwnerDescriptor(
        SyntaxNode treeRoot,
        Compilation.ExecutableOwnerDescriptor descriptor,
        out SyntaxNode owner)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                owner = current;
                return true;
            }
        }

        owner = null!;
        return false;
    }

    internal TypedConstant GetConstantValue(ExpressionSyntax expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (ConstantValueEvaluator.TryEvaluate(expression, out var value))
        {
            var typeInfo = GetTypeInfo(expression);
            return TypedConstant.CreatePrimitive(typeInfo.ConvertedType ?? typeInfo.Type, value);
        }

        if (!TryGetBoundNodeForSemanticQuery(expression, out var boundNode) ||
            boundNode is not BoundExpression boundExpression)
            return TypedConstant.CreateError(null);

        return CreateTypedConstantCore(boundExpression);
    }

    /// <summary>
    /// Gets type information about a type syntax.
    /// </summary>
    /// <param name="typeSyntax">The type syntax node.</param>
    public TypeInfo GetTypeInfo(TypeSyntax typeSyntax)
    {
        ValidateSyntaxNode(typeSyntax, nameof(typeSyntax));

        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        using var semanticQueryBinding = EnterSemanticQueryBinding();

        if (typeSyntax is ExpressionSyntax expressionSyntax &&
            !IsExplicitTypeSyntaxContext(typeSyntax))
        {
            return GetTypeInfo(expressionSyntax);
        }

        TypeInfo Cache(TypeInfo info)
        {
            if (HasTypeInfo(info))
                StoreTypeMapping(typeSyntax, info);

            return info;
        }

        if (TryGetCachedTypeInfo(typeSyntax, out var cachedTypeInfo) &&
            HasNonErrorTypeInfo(cachedTypeInfo))
        {
            return Cache(cachedTypeInfo);
        }

        if (TryGetAvailablePredefinedTypeInfo(typeSyntax, out var predefinedTypeInfo))
            return Cache(predefinedTypeInfo);

        Compilation.EnsureSourceDeclarationsDeclared();

        if (TryGetAvailableTypeInfo(typeSyntax, out var availableTypeInfo))
            return Cache(availableTypeInfo);

        if (TryGetTypeFromOwningDeclaration(typeSyntax, out var declaredType))
            return Cache(new TypeInfo(declaredType, declaredType, ComputeConversion(declaredType, declaredType)));

        var binder = GetBinder(typeSyntax);
        try
        {
            var result = binder.BindTypeSyntax(typeSyntax);
            var type = result.Success
                ? result.ResolvedType
                : null;

            if (type is null || type.TypeKind == TypeKind.Error)
                return new TypeInfo(null, null);

            return Cache(new TypeInfo(type, type, ComputeConversion(type, type)));
        }
        catch
        {
            return new TypeInfo(null, null);
        }
    }

    private bool TryGetTypeFromOwningDeclaration(TypeSyntax typeSyntax, out ITypeSymbol type)
    {
        type = null!;

        if (typeSyntax.Parent is not TypeAnnotationClauseSyntax annotation)
            return false;

        type = annotation.Parent switch
        {
            PropertyDeclarationSyntax propertyDeclaration when GetDeclaredSymbol(propertyDeclaration) is IPropertySymbol property
                => property.Type,
            EventDeclarationSyntax eventDeclaration when GetDeclaredSymbol(eventDeclaration) is IEventSymbol @event
                => @event.Type,
            IndexerDeclarationSyntax indexerDeclaration when GetDeclaredSymbol(indexerDeclaration) is IPropertySymbol indexer
                => indexer.Type,
            ParameterSyntax parameterSyntax when GetDeclaredSymbol(parameterSyntax) is IParameterSymbol parameter
                => parameter.Type,
            _ => null!
        };

        return type is not null && type.TypeKind != TypeKind.Error;
    }

    public bool TryGetTypeInfo(TypeSyntax typeSyntax, out TypeInfo typeInfo)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);
        typeInfo = GetTypeInfo(typeSyntax);
        return HasTypeInfo(typeInfo);
    }

    private static bool IsExplicitTypeSyntaxContext(TypeSyntax typeSyntax)
    {
        return typeSyntax.Parent switch
        {
            null => false,
            TypeAnnotationClauseSyntax => true,
            ArrowTypeClauseSyntax => true,
            TypeArgumentSyntax => true,
            TypeSyntax => true,
            TypeOfExpressionSyntax => true,
            SizeOfExpressionSyntax => true,
            DefaultExpressionSyntax => true,
            CastExpressionSyntax cast when ReferenceEquals(cast.Type, typeSyntax) => true,
            _ => false
        };
    }

    private Conversion ComputeConversion(ITypeSymbol? naturalType, ITypeSymbol? convertedType)
    {
        if (naturalType is null || convertedType is null)
            return Conversion.None;

        var conversion = Compilation.ClassifyConversion(naturalType, convertedType, includeUserDefined: true);
        if (conversion.Exists)
            return conversion;

        // Synthesize identity when classifier cannot represent the mapping but
        // symbols are identical (e.g. some pseudo-types in semantic model).
        if (SymbolEqualityComparer.Default.Equals(naturalType, convertedType))
            return new Conversion(isImplicit: true, isIdentity: true);

        return Conversion.None;
    }

    private static TypedConstant CreateTypedConstantCore(BoundExpression expression)
    {
        if (expression is BoundConversionExpression conversion)
            expression = conversion.Expression;

        return expression switch
        {
            BoundLiteralExpression literal when literal.Kind == BoundLiteralExpressionKind.NullLiteral
                => TypedConstant.CreateNull(literal.GetConvertedType() ?? literal.Type),
            BoundLiteralExpression literal
                => TypedConstant.CreatePrimitive(literal.GetConvertedType() ?? literal.Type, literal.Value),
            BoundFieldAccess fieldAccess when fieldAccess.Field is { IsConst: true } field
                => TypedConstant.CreatePrimitive(fieldAccess.Type, field.GetConstantValue()),
            _ => TypedConstant.CreateError(expression.Type)
        };
    }

    /// <summary>
    /// Looks up extension members that apply to the specified receiver type.
    /// </summary>
    /// <param name="receiverType">The type that receives extension members.</param>
    /// <param name="contextNode">Optional lookup context. Use this to include local imports in scope.</param>
    /// <param name="name">Optional member name filter.</param>
    /// <param name="includePartialMatches">Whether prefix-matching should be used for the name filter.</param>
    /// <param name="kinds">The extension member kinds to return.</param>
    public ExtensionMemberLookupResult LookupApplicableExtensionMembers(
        ITypeSymbol receiverType,
        SyntaxNode? contextNode = null,
        string? name = null,
        bool includePartialMatches = false,
        ExtensionMemberKinds kinds = ExtensionMemberKinds.All)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);

        if (receiverType is null || receiverType.TypeKind == TypeKind.Error)
            return ExtensionMemberLookupResult.Empty;

        var binder = contextNode is null
            ? GetBinderForIncrementalSemanticQuery(SyntaxTree.GetRoot())
            : GetBinderForIncrementalSemanticQuery(GetExtensionMemberLookupContext(contextNode));

        return ExtensionMemberLookup.Lookup(
            binder,
            receiverType,
            name,
            includePartialMatches,
            kinds);
    }

    /// <summary>
    /// Get the bound node for a specific syntax node.
    /// </summary>
    /// <param name="node">The syntax node</param>
    /// <param name="boundNode">The bound node when the lookup succeeds.</param>
    /// <returns><see langword="true"/> when a bound node is available; otherwise, <see langword="false"/>.</returns>
    private bool TryGetBoundNodeForSemanticQuery(SyntaxNode node, out BoundNode boundNode)
    {
        EnsureBindingReadyForSemanticQuery();

        EnsureContainingFreestandingMacroReplacementSyntax(node);
        if (TryGetMacroReplacementSyntax(node, out var replacementNode) &&
            !ReferenceEquals(replacementNode, node))
        {
            return TryGetBoundNodeForSemanticQuery(replacementNode, out boundNode);
        }

        if (node is GlobalStatementSyntax globalStatement)
        {
            boundNode = BindContextualRootForSemanticQuery(globalStatement);
            return true;
        }

        if (TryGetCachedBoundNode(node) is { } cachedNode &&
            !IsLikelyStaleFunctionBodyNode(cachedNode))
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeCacheHit();
            boundNode = cachedNode;
            return true;
        }

        if (TryGetContextualBindingRoot(node, out var contextualRoot) &&
            !ReferenceEquals(contextualRoot, node) &&
            contextualRoot is not CompilationUnitSyntax)
        {
            BoundNode contextualBoundRoot;
            if (TryGetCachedBoundNode(contextualRoot) is { } cachedContextualRoot &&
                !IsLikelyStaleFunctionBodyNode(cachedContextualRoot))
            {
                contextualBoundRoot = cachedContextualRoot;
            }
            else
            {
                contextualBoundRoot = BindContextualRootForSemanticQuery(contextualRoot);
            }

            if (TryFindBoundNodeBySyntax(contextualBoundRoot, node, out var contextualBoundNode))
            {
                CacheBoundNode(node, contextualBoundNode, GetBinderForIncrementalSemanticQuery(node));
                Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeContextualCacheHit();
                boundNode = contextualBoundNode;
                return true;
            }
        }

        Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeBindFallback();
        var binder = GetBinderForIncrementalSemanticQuery(node);
        boundNode = BindNodeWithCurrentDiagnosticMode(binder, node);
        return true;
    }

    private BoundNode BindNodeWithCurrentDiagnosticMode(Binder binder, SyntaxNode node)
        => IsCollectingBindingDiagnosticsForCurrentFlow
            ? binder.GetOrBind(node)
            : binder.GetOrBindForSemanticQuery(node);

    internal BoundNode GetBoundNode(SyntaxNode node)
    {
        return GetBoundNode(node, BoundTreeView.Original);
    }

    internal BoundNode GetBoundNode(SyntaxNode node, BoundTreeView view)
    {
        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);

        if (view is BoundTreeView.Both)
            throw new ArgumentOutOfRangeException(nameof(view));

        Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeQuery();

        EnsureBindingReady();

        EnsureContainingFreestandingMacroReplacementSyntax(node);

        if (view is BoundTreeView.Original &&
            TryGetMacroReplacementSyntax(node, out var replacementNode) &&
            !ReferenceEquals(replacementNode, node))
        {
            var replacementBoundNode = GetBoundNode(replacementNode, view);
            CacheBoundNode(node, replacementBoundNode, GetBinder(node));
            return replacementBoundNode;
        }

        if (view is BoundTreeView.Original &&
            node is IdentifierNameSyntax identifier &&
            identifier.Parent is MemberAccessExpressionSyntax memberAccess &&
            ReferenceEquals(memberAccess.Name, identifier))
        {
            var memberAccessBoundNode = GetBoundNode(memberAccess, view);
            CacheBoundNode(node, memberAccessBoundNode, GetBinder(node));
            return memberAccessBoundNode;
        }

        if (view is BoundTreeView.Lowered &&
            TryResolveLoweringNode(node) is { } loweringNode &&
            !ReferenceEquals(loweringNode, node))
        {
            return GetBoundNode(loweringNode, view);
        }

        if (node is CompilationUnitSyntax compilationUnit)
            EnsureTopLevelCompilationUnitBound(compilationUnit);

        if (view is BoundTreeView.Original)
        {
            if (TryGetCachedBoundNode(node) is { } cachedNode &&
                !IsLikelyStaleFunctionBodyNode(cachedNode))
            {
                Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeCacheHit();
                return cachedNode;
            }

            if (TryGetContextualBindingRoot(node, out var contextualRoot) &&
                !ReferenceEquals(contextualRoot, node))
            {
                BoundNode? contextualBoundRoot = TryGetCachedBoundNode(contextualRoot);
                if (contextualBoundRoot is not null &&
                    TryFindBoundNodeBySyntax(contextualBoundRoot, node, out var cachedContextualNode) &&
                    IsLikelyStaleFunctionBodyNode(cachedContextualNode))
                {
                    ClearCachedSemanticState(contextualRoot);
                    contextualBoundRoot = null;
                }

                contextualBoundRoot ??= BindContextualRootForSemanticQuery(contextualRoot);

                if (TryGetCachedBoundNode(node) is { } contextCachedNode &&
                    !IsLikelyStaleFunctionBodyNode(contextCachedNode))
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeContextualCacheHit();
                    return contextCachedNode;
                }

                if (TryFindBoundNodeBySyntax(contextualBoundRoot, node, out var contextualBoundNode))
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeContextualCacheHit();
                    CacheBoundNode(node, contextualBoundNode, GetBinder(node));
                    return contextualBoundNode;
                }
            }

            if (TryGetEnclosingFunctionExpression(node, out var enclosingFunctionExpression))
            {
                var cachedInFunctionBody = TryGetCachedBoundNode(node);
                if (cachedInFunctionBody is null || IsLikelyStaleFunctionBodyNode(cachedInFunctionBody))
                {
                    var rebindRoot = GetFunctionExpressionRebindRoot(enclosingFunctionExpression);
                    ClearCachedSemanticState(rebindRoot);
                    var reboundRoot = GetBoundNode(rebindRoot, view);
                    if (TryGetCachedBoundNode(node) is { } reboundFromFunction)
                    {
                        Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeContextualCacheHit();
                        return reboundFromFunction;
                    }

                    if (TryFindBoundNodeBySyntax(reboundRoot, node, out var reboundFromRoot))
                    {
                        Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeContextualCacheHit();
                        CacheBoundNode(node, reboundFromRoot, GetBinder(node));
                        return reboundFromRoot;
                    }
                }
                else
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeCacheHit();
                    return cachedInFunctionBody;
                }
            }

            if (node is CompilationUnitSyntax compilationUnitNode)
            {
                EnsureTopLevelCompilationUnitBound(compilationUnitNode);
                if (TryGetCachedBoundNode(compilationUnitNode) is { } cachedCompilationUnit)
                {
                    Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeCacheHit();
                    return cachedCompilationUnit;
                }

                return CreateSyntheticTopLevelBlock(compilationUnitNode);
            }

            if (node is GlobalStatementSyntax globalStatementNode)
                return BindContextualRootForSemanticQuery(globalStatementNode);

            Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeBindFallback();
            var binder = GetBinder(node);
            var bound = BindNodeWithCurrentDiagnosticMode(binder, node);

            if (node is BlockStatementSyntax blockSyntax &&
                bound is BoundBlockStatement boundBlock &&
                !boundBlock.Statements.Any() &&
                blockSyntax.Statements.Count > 0 &&
                node.Parent is { } methodDeclaration &&
                binder is not MethodBodyBinder &&
                TryResolveMethodSymbolForDeclaration(methodDeclaration, out var methodSymbol))
            {
                var fallbackParentBinder = GetMethodBodyParentBinder(methodDeclaration, binder.ParentBinder, ensureSourceDeclarations: true);
                var methodBodyBinder = new MethodBodyBinder(methodSymbol, fallbackParentBinder);
                CacheBinder(node, methodBodyBinder);
                bound = BindNodeWithCurrentDiagnosticMode(methodBodyBinder, node);
            }

            return bound;
        }

        if (TryGetCachedLoweredBoundNode(node) is { } loweredCached)
        {
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeLoweredCacheHit();
            return loweredCached;
        }

        if (node is CompilationUnitSyntax &&
            TryGetCachedBoundNode(node) is not { } &&
            TryGetCachedBoundNode(TryResolveLoweringNode(node) ?? node) is { } loweredTarget)
        {
            CacheLoweredBoundNode(node, loweredTarget, GetBinder(node));
            Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeLoweredCacheHit();
            return loweredTarget;
        }

        var binderForLowering = GetBinder(node);
        var boundNode = TryGetCachedBoundNode(node);

        if (boundNode is null && node is CompilationUnitSyntax loweredCompilationUnit)
        {
            EnsureTopLevelCompilationUnitBound(loweredCompilationUnit);
            boundNode = TryGetCachedBoundNode(loweredCompilationUnit);

            boundNode ??= CreateSyntheticTopLevelBlock(loweredCompilationUnit);
        }

        boundNode ??= BindNodeWithCurrentDiagnosticMode(binderForLowering, node);
        Compilation.PerformanceInstrumentation.SemanticQuery.RecordBoundNodeLoweredFallback();
        var loweredNode = LowerBoundNode(node, binderForLowering, boundNode);
        CacheLoweredBoundNode(node, loweredNode, binderForLowering);
        return loweredNode;
    }

    private bool TryGetContextualBindingRoot(SyntaxNode node, out SyntaxNode root)
    {
        if (Compilation.TryGetContextualBindingRootDescriptor(node, out var cachedDescriptor) &&
            TryResolveContextualBindingRootDescriptor(node.SyntaxTree.GetRoot(), cachedDescriptor, out root) &&
            !ReferenceEquals(root, node))
        {
            return true;
        }

        if (node is CompilationUnitSyntax)
        {
            root = node;
            return false;
        }

        var enclosingIf = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();
        if (enclosingIf is not null)
        {
            root = enclosingIf;
            return true;
        }

        var enclosingIfPattern = node.AncestorsAndSelf().OfType<IfPatternStatementSyntax>().FirstOrDefault();
        if (enclosingIfPattern is not null)
        {
            root = enclosingIfPattern;
            return true;
        }

        var enclosingIfPatternExpression = node.AncestorsAndSelf().OfType<IfPatternExpressionSyntax>().FirstOrDefault();
        if (enclosingIfPatternExpression is not null &&
            !ReferenceEquals(enclosingIfPatternExpression, node))
        {
            root = enclosingIfPatternExpression;
            return true;
        }

        var enclosingWhile = node.AncestorsAndSelf().OfType<WhileStatementSyntax>().FirstOrDefault();
        if (enclosingWhile is not null)
        {
            root = enclosingWhile;
            return true;
        }

        var enclosingWhilePattern = node.AncestorsAndSelf().OfType<WhilePatternStatementSyntax>().FirstOrDefault();
        if (enclosingWhilePattern is not null)
        {
            root = enclosingWhilePattern;
            return true;
        }

        // Binding a node in isolation can drop scope/flow context (locals, overload shape).
        // Prefer binding the enclosing executable scope first.
        root = node.AncestorsAndSelf().OfType<BlockStatementSyntax>().FirstOrDefault()
               ?? node.AncestorsAndSelf().OfType<ArrowExpressionClauseSyntax>().FirstOrDefault()
               ?? node.AncestorsAndSelf().OfType<GlobalStatementSyntax>().FirstOrDefault()
               ?? node.AncestorsAndSelf().OfType<CompilationUnitSyntax>().FirstOrDefault()
               ?? node;

        Compilation.StoreContextualBindingRootDescriptor(
            node,
            new Compilation.ContextualBindingRootDescriptor(root.Span, root.Kind));
        return !ReferenceEquals(root, node);
    }

    private BoundNode BindContextualRootForSemanticQuery(SyntaxNode contextualRoot)
    {
        if (contextualRoot is GlobalStatementSyntax globalStatement)
        {
            if (globalStatement.SyntaxTree.GetRoot() is CompilationUnitSyntax compilationUnit)
                EnsureTopLevelCompilationUnitBound(compilationUnit);

            return TryGetCachedBoundNode(globalStatement.Statement)
                ?? BindNodeWithCurrentDiagnosticMode(GetBinderForIncrementalSemanticQuery(globalStatement.Statement), globalStatement.Statement);
        }

        if (contextualRoot is CompilationUnitSyntax contextualCompilationUnit)
        {
            EnsureTopLevelCompilationUnitBound(contextualCompilationUnit);
            return TryGetCachedBoundNode(contextualCompilationUnit)
                ?? CreateSyntheticTopLevelBlock(contextualCompilationUnit);
        }

        EnsureEnclosingFunctionExpressionContextForSemanticQuery(contextualRoot);

        var contextualBinder = GetBinderForIncrementalSemanticQuery(contextualRoot);
        if (TryGetNearestBlockBinder(contextualBinder, out var blockBinder))
            blockBinder.EnsurePrecedingStatementContextForSemanticQuery(contextualRoot);

        return BindNodeWithCurrentDiagnosticMode(contextualBinder, contextualRoot);
    }

    private void EnsureEnclosingFunctionExpressionContextForSemanticQuery(SyntaxNode contextualRoot)
    {
        foreach (var functionExpression in contextualRoot.Ancestors().OfType<FunctionExpressionSyntax>())
        {
            var functionRoot = GetFunctionExpressionRebindRoot(functionExpression);
            if (ReferenceEquals(functionRoot, contextualRoot))
            {
                continue;
            }

            _ = TryGetContextualBoundFunctionExpression(functionExpression, out _);
            break;
        }
    }

    private static bool TryResolveContextualBindingRootDescriptor(
        SyntaxNode treeRoot,
        Compilation.ContextualBindingRootDescriptor descriptor,
        out SyntaxNode root)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                root = current;
                return true;
            }
        }

        root = null!;
        return false;
    }

    private void ClearCachedSemanticState(SyntaxNode node)
    {
        RemoveCachedBoundNode(node);
        RemoveCachedBinderIfAllowed(node);
        RemoveCachedSymbolMapping(node);
        if (node is FunctionExpressionSyntax functionExpression)
        {
            _functionExpressionSymbolCache.TryRemove(functionExpression, out _);
            _functionExpressionDelegateTypeCache.TryRemove(functionExpression, out _);
            _functionExpressionSymbolCreationInProgress.TryRemove(functionExpression, out _);
        }

        foreach (var child in node.DescendantNodes())
        {
            RemoveCachedBoundNode(child);
            RemoveCachedBinderIfAllowed(child);
            RemoveCachedSymbolMapping(child);
            if (child is FunctionExpressionSyntax childFunctionExpression)
            {
                _functionExpressionSymbolCache.TryRemove(childFunctionExpression, out _);
                _functionExpressionDelegateTypeCache.TryRemove(childFunctionExpression, out _);
                _functionExpressionSymbolCreationInProgress.TryRemove(childFunctionExpression, out _);
            }
        }

        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is CompilationUnitSyntax)
                break;

            RemoveCachedBoundNode(ancestor);
            RemoveCachedBinderIfAllowed(ancestor);
            RemoveCachedSymbolMapping(ancestor);
        }
    }

    private void RemoveCachedBinderIfAllowed(SyntaxNode node)
    {
        if (node is CompilationUnitSyntax)
            return;

        RemoveCachedBinder(node);
    }

    private void RemoveCachedSymbolMapping(SyntaxNode node)
    {
        _symbolMappings.TryRemove(node, out _);
        _nonReportingSymbolMappings.TryRemove(node, out _);
        _typeMappings.TryRemove(node, out _);
        _nonReportingTypeMappings.TryRemove(node, out _);
        var nodeKey = GetSyntaxNodeMapKey(node);
        _typeMappingsByKey.TryRemove(nodeKey, out _);
        _nonReportingTypeMappingsByKey.TryRemove(nodeKey, out _);
    }

    private static bool IsLikelyStaleFunctionBodyNode(BoundNode node)
    {
        return node switch
        {
            BoundErrorExpression => true,
            BoundFunctionExpression functionExpression
                when functionExpression.Type?.TypeKind == TypeKind.Error ||
                     functionExpression.DelegateType?.TypeKind == TypeKind.Error ||
                     functionExpression.ReturnType?.TypeKind == TypeKind.Error ||
                     functionExpression.Parameters.Any(static parameter => parameter.Type is null || parameter.Type.TypeKind == TypeKind.Error) => true,
            BoundBlockExpression blockExpression when blockExpression.Type?.TypeKind == TypeKind.Error => true,
            BoundExpression expression when expression.Type?.TypeKind == TypeKind.Error => true,
            _ => false
        };
    }

    private SyntaxNode GetFunctionExpressionRebindRoot(FunctionExpressionSyntax functionExpression)
    {
        if (Compilation.TryGetFunctionExpressionRebindRootDescriptor(functionExpression, out var cachedDescriptor) &&
            TryResolveFunctionExpressionRebindRootDescriptor(functionExpression.SyntaxTree.GetRoot(), cachedDescriptor, out var cachedRoot))
        {
            return cachedRoot;
        }

        ExpressionSyntax? enclosingExpression = null;

        for (var current = functionExpression.Parent; current is not null; current = current.Parent)
        {
            if (current is FunctionExpressionSyntax)
                continue;

            if (current is InvocationExpressionSyntax invocation &&
                IsFunctionExpressionInvocationArgument(functionExpression, invocation))
            {
                var rebindRoot = GetInvocationArgumentFunctionExpressionRebindRoot(invocation);
                Compilation.StoreFunctionExpressionRebindRootDescriptor(
                    functionExpression,
                    new Compilation.FunctionExpressionRebindRootDescriptor(rebindRoot.Span, rebindRoot.Kind));
                return rebindRoot;
            }

            if (current is StatementSyntax statement)
            {
                Compilation.StoreFunctionExpressionRebindRootDescriptor(
                    functionExpression,
                    new Compilation.FunctionExpressionRebindRootDescriptor(statement.Span, statement.Kind));
                return statement;
            }

            if (enclosingExpression is null && current is ExpressionSyntax expression)
                enclosingExpression = expression;
        }

        var root = (SyntaxNode?)enclosingExpression ?? functionExpression;
        Compilation.StoreFunctionExpressionRebindRootDescriptor(
            functionExpression,
            new Compilation.FunctionExpressionRebindRootDescriptor(root.Span, root.Kind));
        return root;
    }

    private static SyntaxNode GetInvocationArgumentFunctionExpressionRebindRoot(InvocationExpressionSyntax invocation)
    {
        if (invocation.Parent is InfixOperatorExpressionSyntax
            {
                OperatorToken.Kind: SyntaxKind.PipeToken
            } pipeExpression &&
            ReferenceEquals(pipeExpression.Right, invocation))
        {
            return pipeExpression;
        }

        SyntaxNode root = invocation;
        for (var current = invocation; current.Parent is not null;)
        {
            if (current.Parent is MemberAccessExpressionSyntax memberAccess &&
                IsSameSyntaxNode(memberAccess.Expression, current) &&
                memberAccess.Parent is InvocationExpressionSyntax chainedInvocation &&
                IsSameSyntaxNode(chainedInvocation.Expression, memberAccess))
            {
                root = chainedInvocation;
                current = chainedInvocation;
                continue;
            }

            break;
        }

        for (var current = root.Parent; current is not null; current = current.Parent)
        {
            if (current is FunctionExpressionSyntax)
                break;

            if (current is StatementSyntax statement)
                return statement;
        }

        return root;
    }

    private static bool IsFunctionExpressionInvocationArgument(
        FunctionExpressionSyntax functionExpression,
        InvocationExpressionSyntax invocation)
    {
        return invocation.ArgumentList.Arguments.Any(argument =>
            argument.Expression.DescendantNodesAndSelf().Contains(functionExpression, ReferenceEqualityComparer.Instance));
    }

    private static SyntaxNode GetCallableExpressionRebindRoot(SyntaxNode functionSyntax)
    {
        ExpressionSyntax? enclosingExpression = null;

        for (var current = functionSyntax.Parent; current is not null; current = current.Parent)
        {
            if (current is FunctionExpressionSyntax)
                continue;

            if (current is StatementSyntax statement)
                return statement;

            if (enclosingExpression is null && current is ExpressionSyntax expression)
                enclosingExpression = expression;
        }

        return (SyntaxNode?)enclosingExpression ?? functionSyntax;
    }

    private static bool TryResolveFunctionExpressionRebindRootDescriptor(
        SyntaxNode treeRoot,
        Compilation.FunctionExpressionRebindRootDescriptor descriptor,
        out SyntaxNode root)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                root = current;
                return true;
            }
        }

        root = null!;
        return false;
    }

    private static bool TryGetEnclosingFunctionExpression(SyntaxNode node, out FunctionExpressionSyntax enclosingFunctionExpression)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is not FunctionExpressionSyntax functionExpression)
                continue;

            var body = (SyntaxNode?)functionExpression.Body ?? functionExpression.ExpressionBody;
            if (body is not null && body.Span.Contains(node.Span))
            {
                enclosingFunctionExpression = functionExpression;
                return true;
            }
        }

        enclosingFunctionExpression = null!;
        return false;
    }

    internal bool TryGetContextualBoundFunctionExpression(
        FunctionExpressionSyntax functionExpression,
        out BoundFunctionExpression boundFunction)
    {
        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction))
        {
            boundFunction = cachedFunction;
            return true;
        }

        var contextualRoot = GetFunctionExpressionRebindRoot(functionExpression);
        if (!ReferenceEquals(contextualRoot, functionExpression))
        {
            if (TryGetCachedBoundNode(contextualRoot) is { } contextualBoundRoot &&
                TryFindBoundNodeBySyntax(contextualBoundRoot, functionExpression, out var cachedContextualNode) &&
                cachedContextualNode is BoundFunctionExpression cachedContextualFunction)
            {
                if (!IsLikelyStaleFunctionBodyNode(cachedContextualFunction))
                {
                    CacheBoundNode(functionExpression, cachedContextualFunction, GetBinderForIncrementalSemanticQuery(functionExpression));
                    boundFunction = cachedContextualFunction;
                    return true;
                }

                ClearCachedSemanticState(contextualRoot);
            }

            if (TryRebindContextualFunctionExpression(functionExpression, contextualRoot, out var reboundFunction))
            {
                boundFunction = reboundFunction;
                return true;
            }
        }

        if (TryGetContextualBindingRoot(functionExpression, out var fallbackContextualRoot) &&
            !ReferenceEquals(fallbackContextualRoot, functionExpression) &&
            !ReferenceEquals(fallbackContextualRoot, contextualRoot) &&
            TryRebindContextualFunctionExpression(functionExpression, fallbackContextualRoot, out var fallbackFunction))
        {
            boundFunction = fallbackFunction;
            return true;
        }

        boundFunction = null!;
        return false;
    }

    internal Compilation.ContextualBindingRootDescriptor GetContextualBindingRootDescriptorForTesting(SyntaxNode node)
    {
        if (!TryGetContextualBindingRoot(node, out var root))
            return new Compilation.ContextualBindingRootDescriptor(node.Span, node.Kind);

        return new Compilation.ContextualBindingRootDescriptor(root.Span, root.Kind);
    }

    internal Compilation.InterestBindingRootDescriptor? GetInterestBindingRootDescriptorForTesting(
        SyntaxNode node,
        bool includeExtendedExecutableRoots)
    {
        var root = GetInterestBindingRoot(node, includeExtendedExecutableRoots);
        return root is null
            ? null
            : new Compilation.InterestBindingRootDescriptor(root.Span, root.Kind);
    }

    internal Compilation.ExecutableOwnerDescriptor GetExecutableOwnerDescriptorForTesting(SyntaxNode node)
    {
        if (!TryGetExecutableOwner(node, out var owner))
            return new Compilation.ExecutableOwnerDescriptor(node.Span, node.Kind);

        return new Compilation.ExecutableOwnerDescriptor(owner.Span, owner.Kind);
    }

    internal Compilation.FunctionExpressionRebindRootDescriptor GetFunctionExpressionRebindRootDescriptorForTesting(
        FunctionExpressionSyntax functionExpression)
    {
        var root = GetFunctionExpressionRebindRoot(functionExpression);
        return new Compilation.FunctionExpressionRebindRootDescriptor(root.Span, root.Kind);
    }

    internal Compilation.NodeInterestSymbolDescriptor? GetNodeInterestSymbolDescriptorForTesting(SyntaxNode node)
    {
        return Compilation.TryGetNodeInterestSymbolDescriptor(node, out var descriptor)
            ? descriptor
            : null;
    }

    internal Compilation.BinderParentAnchorDescriptor? GetBinderParentAnchorDescriptorForTesting(SyntaxNode node)
    {
        return TryGetBinderParentAnchor(node, out var anchor)
            ? new Compilation.BinderParentAnchorDescriptor(anchor.Span, anchor.Kind)
            : null;
    }

    internal bool IsExecutableOwnerMarkedChangedForTesting(SyntaxNode node)
    {
        var owner = TryGetExecutableOwner(node, out var resolvedOwner)
            ? resolvedOwner
            : node;

        return Compilation.IsChangedExecutableOwner(owner);
    }

    internal Compilation.MatchedExecutableOwner? GetMatchedExecutableOwnerForTesting(SyntaxNode node)
    {
        return Compilation.GetMatchedExecutableOwnerForTesting(node);
    }

    internal bool HasCachedBoundNodeForTesting(SyntaxNode node)
        => TryGetCachedBoundNode(node) is not null;

    internal bool IsCachedBoundNodeNonReportingForTesting(SyntaxNode node)
        => _nonReportingBoundNodeCache.ContainsKey(node);

    internal bool IsCachedSymbolMappingNonReportingForTesting(SyntaxNode node)
        => _nonReportingSymbolMappings.ContainsKey(node);

    internal void EnsureCompilationUnitDeclarationBindersCreated()
    {
        EnsureDeclarations();

        if (SyntaxTree.GetRoot() is CompilationUnitSyntax compilationUnit)
        {
            _ = GetBinder(compilationUnit);
            EnsureTopLevelFunctionDeclarations(compilationUnit);
        }
    }

    internal Binder GetIncrementalSemanticQueryBinderForTesting(SyntaxNode node)
    {
        EnsureDeclarations();
        EnsureMemberSignaturesDeclared();
        return GetBinderForIncrementalSemanticQuery(node);
    }

    internal BinderLifecycleSnapshot GetBinderLifecycleSnapshotForTesting(Binder binder)
    {
        if (_binderLifecycleSnapshots.TryGetValue(binder, out var snapshot))
            return snapshot;

        throw new InvalidOperationException("The binder has not been cached by this semantic model.");
    }

    internal BinderLifecycleSnapshot GetBinderLifecycleSnapshotForTesting(SyntaxNode node)
        => GetBinderLifecycleSnapshotForTesting(GetIncrementalSemanticQueryBinderForTesting(node));

    public IParameterSymbol? GetFunctionExpressionParameterSymbol(ParameterSyntax parameterSyntax)
    {
        ValidateSyntaxNode(parameterSyntax, nameof(parameterSyntax));

        using var semanticAccess = EnterSemanticAccess(CancellationToken.None);

        EnsureContainingFreestandingMacroReplacementSyntax(parameterSyntax);
        if (TryGetMacroReplacementSyntax(parameterSyntax, out var macroReplacement) &&
            macroReplacement is ParameterSyntax replacementParameter &&
            !ReferenceEquals(replacementParameter, parameterSyntax))
        {
            return GetFunctionExpressionParameterSymbolCore(
                replacementParameter,
                allowDeclaredSymbolFallback: true);
        }

        return GetFunctionExpressionParameterSymbolCore(parameterSyntax, allowDeclaredSymbolFallback: true);
    }

    internal IParameterSymbol? GetFunctionExpressionParameterSymbolForDeclaredLookup(ParameterSyntax parameterSyntax)
        => GetFunctionExpressionParameterSymbolCore(parameterSyntax, allowDeclaredSymbolFallback: false);

    private IParameterSymbol? GetFunctionExpressionParameterSymbolCore(
        ParameterSyntax parameterSyntax,
        bool allowDeclaredSymbolFallback)
    {
        var instrumentationStart = Compilation.PerformanceInstrumentation.FunctionExpressionParameters.BeginQuery();
        try
        {
            if (TryResolveFunctionExpressionParameterSymbolFast(parameterSyntax, out var fastParameter))
                return fastParameter;

            var functionExpression = parameterSyntax.Ancestors().OfType<FunctionExpressionSyntax>().FirstOrDefault();
            if (functionExpression is not null)
            {
                var parameter = GetFunctionExpressionParameterSymbol(functionExpression, parameterSyntax);
                if (IsUsableFunctionExpressionParameterSymbol(parameter))
                    return parameter;
            }

            if (!allowDeclaredSymbolFallback)
            {
                Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordMiss();
                return null;
            }

            if (RequiresCompleteSourceDeclarationSymbol(parameterSyntax))
                Compilation.EnsureSourceDeclarationsComplete();

            var declaredParameter = GetDeclaredSymbol(parameterSyntax) as IParameterSymbol;
            if (IsUsableFunctionExpressionParameterSymbol(declaredParameter))
                return declaredParameter;

            EnsureBindingReadyForSemanticQuery();

            if (parameterSyntax.Ancestors().OfType<FunctionExpressionSyntax>().FirstOrDefault() is { } readyFunctionExpression)
            {
                var readyParameter = GetFunctionExpressionParameterSymbol(readyFunctionExpression, parameterSyntax);
                if (IsUsableFunctionExpressionParameterSymbol(readyParameter))
                    return readyParameter;
            }

            var fallbackDeclaredParameter = GetDeclaredSymbol(parameterSyntax) as IParameterSymbol;
            if (fallbackDeclaredParameter is null)
                Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordMiss();

            return fallbackDeclaredParameter;
        }
        finally
        {
            Compilation.PerformanceInstrumentation.FunctionExpressionParameters.EndQuery(instrumentationStart);
        }
    }

    private static bool IsUsableFunctionExpressionParameterSymbol(IParameterSymbol? parameter)
        => parameter?.Type is { } type && !ContainsErrorTypeShallow(type);

    private void ClearFunctionExpressionParameterContext(ParameterSyntax parameterSyntax)
    {
        if (parameterSyntax.Ancestors().OfType<FunctionExpressionSyntax>().FirstOrDefault() is { } functionExpression)
        {
            ClearCachedSemanticState(GetFunctionExpressionRebindRoot(functionExpression));
            ClearCachedSemanticState(functionExpression);
        }

    }

    private IParameterSymbol? GetFunctionExpressionParameterSymbol(
        FunctionExpressionSyntax functionExpression,
        ParameterSyntax parameterSyntax)
    {
        if (!_functionExpressionParameterLookupInProgress.TryAdd(functionExpression, 0))
        {
            return null;
        }

        try
        {
            if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedLambda &&
                TryGetFunctionParameterBySyntax(functionExpression, parameterSyntax, cachedLambda.Parameters, out var cachedParameter))
            {
                Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordDirectBoundHit();
                return cachedParameter;
            }

            if (TryGetContextualBoundFunctionExpression(functionExpression, out var contextualLambda) &&
                TryGetFunctionParameterBySyntax(functionExpression, parameterSyntax, contextualLambda.Parameters, out var contextualParameter))
            {
                Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordContextualHit();
                return contextualParameter;
            }

            Compilation.PerformanceInstrumentation.FunctionExpressionParameters.RecordMiss();
            return null;
        }
        finally
        {
            _functionExpressionParameterLookupInProgress.TryRemove(functionExpression, out _);
        }
    }

    private bool TryRebindContextualFunctionExpression(
        FunctionExpressionSyntax functionExpression,
        SyntaxNode contextualRoot,
        out BoundFunctionExpression boundFunction)
        => TryRebindContextualFunctionExpressionCore(functionExpression, contextualRoot, out boundFunction);

    private bool TryRebindContextualFunctionExpressionCore(
        SyntaxNode functionSyntax,
        SyntaxNode contextualRoot,
        out BoundFunctionExpression boundFunction)
    {
        if (!_functionExpressionRebindInProgress.TryAdd(contextualRoot, 0))
        {
            boundFunction = null!;
            return false;
        }

        try
        {
            var reboundRoot = BindContextualRootForSemanticQuery(contextualRoot);
            if (TryGetCachedBoundNode(functionSyntax) is BoundFunctionExpression cachedAfterContextualBind &&
                !IsLikelyStaleFunctionBodyNode(cachedAfterContextualBind))
            {
                if (functionSyntax is FunctionExpressionSyntax functionExpression)
                    _ = TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, cachedAfterContextualBind, out _);

                boundFunction = cachedAfterContextualBind;
                return true;
            }

            if (TryFindBoundNodeBySyntax(reboundRoot, functionSyntax, out var reboundFunctionNode) &&
                reboundFunctionNode is BoundFunctionExpression reboundLambda)
            {
                if (IsLikelyStaleFunctionBodyNode(reboundLambda))
                {
                    ClearCachedSemanticState(contextualRoot);
                    boundFunction = null!;
                    return false;
                }

                CacheBoundNode(functionSyntax, reboundLambda, GetBinderForIncrementalSemanticQuery(functionSyntax));
                if (functionSyntax is FunctionExpressionSyntax functionExpression)
                    _ = TryUpgradeFunctionExpressionSymbolFromBoundFunction(functionExpression, reboundLambda, out _);

                boundFunction = reboundLambda;
                return true;
            }

            boundFunction = null!;
            return false;
        }
        finally
        {
            _functionExpressionRebindInProgress.TryRemove(contextualRoot, out _);
        }
    }

    private bool TryUpgradeFunctionExpressionSymbolFromBoundFunction(
        FunctionExpressionSyntax functionExpression,
        BoundFunctionExpression boundFunction,
        out IMethodSymbol? functionSymbol)
    {
        if (boundFunction.Symbol is not IMethodSymbol boundMethod)
        {
            functionSymbol = null;
            return false;
        }

        var upgradedMethod = boundMethod;
        if (FunctionExpressionSymbolContainsError(boundMethod) &&
            boundMethod is SourceLambdaSymbol sourceLambda)
        {
            sourceLambda.SetParameters(boundFunction.Parameters);

            if (boundFunction.ReturnType.TypeKind != TypeKind.Error)
                sourceLambda.SetReturnType(boundFunction.ReturnType);

            if (boundFunction.DelegateType.TypeKind != TypeKind.Error)
                sourceLambda.SetDelegateType(boundFunction.DelegateType);

            sourceLambda.SetCapturedVariables(boundFunction.CapturedVariables);
            upgradedMethod = sourceLambda;
        }

        if (FunctionExpressionSymbolContainsError(upgradedMethod))
        {
            functionSymbol = null;
            return false;
        }

        _functionExpressionSymbolCache.AddOrUpdate(
            functionExpression,
            upgradedMethod,
            (_, _) => upgradedMethod);
        functionSymbol = upgradedMethod;
        return true;
    }

    /// <summary>
    /// Get the bound expression for a specific expression syntax node.
    /// </summary>
    /// <param name="expression">The expression syntax node</param>
    /// <returns>The bound expression</returns>
    /// <remarks>Convenience overload</remarks>
    internal BoundExpression GetBoundNode(ExpressionSyntax expression)
    {
        return (BoundExpression)GetBoundNode((SyntaxNode)expression);
    }

    private static bool TryGetFunctionParameterBySyntax(
        FunctionExpressionSyntax functionExpression,
        ParameterSyntax parameterSyntax,
        IEnumerable<IParameterSymbol> parameters,
        out IParameterSymbol parameterSymbol)
    {
        if (TryGetFunctionParameterIndex(functionExpression, parameterSyntax, out var parameterIndex))
        {
            parameterSymbol = parameters.ElementAtOrDefault(parameterIndex)!;
            if (parameterSymbol is not null)
                return true;
        }

        parameterSymbol = parameters.FirstOrDefault(parameter =>
            parameter.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree == parameterSyntax.SyntaxTree &&
                reference.Span == parameterSyntax.Span))!;

        return parameterSymbol is not null;
    }

    private static bool TryGetFunctionParameterIndex(
        FunctionExpressionSyntax functionExpression,
        ParameterSyntax parameterSyntax,
        out int parameterIndex)
    {
        switch (functionExpression)
        {
            case ParenthesizedFunctionExpressionSyntax parenthesized:
                for (var i = 0; i < parenthesized.ParameterList.Parameters.Count; i++)
                {
                    if (IsSameSyntaxNode(parenthesized.ParameterList.Parameters[i], parameterSyntax))
                    {
                        parameterIndex = i;
                        return true;
                    }
                }

                break;

            case SimpleFunctionExpressionSyntax simple:
                if (IsSameSyntaxNode(simple.Parameter, parameterSyntax) ||
                    parameterSyntax.Ancestors().Any(ancestor => IsSameSyntaxNode(ancestor, simple)))
                {
                    parameterIndex = 0;
                    return true;
                }

                break;
        }

        parameterIndex = -1;
        return false;
    }

    private bool TryFindBoundNodeBySyntax(BoundNode root, SyntaxNode targetSyntax, out BoundNode boundNode)
    {
        var stack = new Stack<BoundNode>();
        var visited = new HashSet<BoundNode>(ReferenceEqualityComparer.Instance);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
                continue;

            var currentSyntax = GetSyntax(current);
            if (currentSyntax is not null && ReferenceEquals(currentSyntax, targetSyntax))
            {
                boundNode = current;
                return true;
            }

            if (currentSyntax is not null &&
                currentSyntax.Kind == targetSyntax.Kind &&
                currentSyntax.Span == targetSyntax.Span)
            {
                boundNode = current;
                return true;
            }

            foreach (var child in EnumerateBoundChildren(current))
                stack.Push(child);
        }

        boundNode = null!;
        return false;
    }
    private BoundNode LowerBoundNode(SyntaxNode syntaxNode, Binder binder, BoundNode boundNode)
    {
        boundNode = RewriteAsyncIfNeeded(syntaxNode, binder, boundNode);

        var containingSymbol = binder.ContainingSymbol;
        if (containingSymbol is null)
            return boundNode;

        try
        {
            return boundNode switch
            {
                BoundBlockStatement block => Lowerer.LowerBlock(containingSymbol, block),
                BoundStatement statement => Lowerer.LowerStatement(containingSymbol, statement),
                BoundExpression expression => Lowerer.LowerExpression(containingSymbol, expression),
                _ => boundNode
            };
        }
        catch
        {
            return boundNode;
        }
    }

    private BoundNode RewriteAsyncIfNeeded(SyntaxNode syntaxNode, Binder binder, BoundNode boundNode)
    {
        if (boundNode is BoundExpression expression &&
            syntaxNode is ArrowExpressionClauseSyntax &&
            binder.ContainingSymbol is SourceMethodSymbol expressionBodiedMethod)
        {
            var expressionBody = ConvertExpressionBodyToBlock(expressionBodiedMethod, expression);
            if (AsyncLowerer.ShouldRewrite(expressionBodiedMethod, expressionBody))
                return AsyncLowerer.Rewrite(expressionBodiedMethod, expressionBody);

            return boundNode;
        }

        if (boundNode is not BoundBlockStatement block)
            return boundNode;

        var sourceMethod = ResolveCanonicalSourceMethodForSyntax(
            syntaxNode,
            binder.ContainingSymbol as SourceMethodSymbol ?? TryGetEnclosingSourceMethod(syntaxNode));

        if (sourceMethod is not null &&
            AsyncLowerer.ShouldRewrite(sourceMethod, block))
        {
            return AsyncLowerer.Rewrite(sourceMethod, block);
        }

        if (binder.ContainingSymbol is SourceLambdaSymbol sourceLambda &&
            AsyncLowerer.ShouldRewrite(sourceLambda, block))
        {
            return AsyncLowerer.Rewrite(sourceLambda, block).Body;
        }

        if (syntaxNode is CompilationUnitSyntax && TryGetTopLevelMainMethod(binder) is { } topLevelMain &&
            AsyncLowerer.ShouldRewrite(topLevelMain, block))
        {
            return AsyncLowerer.Rewrite(topLevelMain, block);
        }

        return boundNode;
    }

    private SourceMethodSymbol? TryGetEnclosingSourceMethod(SyntaxNode syntaxNode)
    {
        for (var current = syntaxNode; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case FunctionStatementSyntax functionStatement:
                    return TryResolveAvailableFunctionStatementSymbol(functionStatement, out var function) &&
                        function is SourceMethodSymbol sourceFunction
                            ? sourceFunction
                            : null;
                case BaseMethodDeclarationSyntax methodDeclaration:
                    return ResolveCanonicalSourceMethod(methodDeclaration);
            }
        }

        return null;
    }

    private SourceMethodSymbol? ResolveCanonicalSourceMethod(
        BaseMethodDeclarationSyntax methodDeclaration,
        SourceMethodSymbol? fallback = null)
    {
        var declared = ReferenceEquals(methodDeclaration.SyntaxTree, SyntaxTree)
            ? GetDeclaredSymbol(methodDeclaration) as SourceMethodSymbol
            : fallback;

        if (declared is null)
            return null;

        if (declared.IsAsync && !declared.IsSignatureSkeleton)
            return declared;

        if (methodDeclaration is not MethodDeclarationSyntax methodSyntax)
            return declared;

        if (!methodSyntax.Modifiers.Any(modifier => modifier.Kind == SyntaxKind.AsyncKeyword))
            return declared;

        if (declared.ContainingType is not INamedTypeSymbol containingType)
            return FindCompilationWideAsyncMethodBySyntax(methodSyntax) ?? declared;

        var parameterCount = methodSyntax.ParameterList?.Parameters.Count ?? 0;
        var arity = methodSyntax.TypeParameterList?.Parameters.Count ?? 0;

        var candidate = containingType
            .GetMembers(declared.Name)
            .OfType<SourceMethodSymbol>()
            .FirstOrDefault(candidate =>
                candidate.IsAsync &&
                !candidate.IsSignatureSkeleton &&
                candidate.Parameters.Length == parameterCount &&
                candidate.TypeParameters.Length == arity &&
                candidate.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == methodSyntax.SyntaxTree &&
                    reference.Span == methodSyntax.Span))
            ?? FindCompilationWideAsyncMethodBySyntax(methodSyntax);

        return candidate ?? declared;
    }

    private SourceMethodSymbol? FindCompilationWideAsyncMethodBySyntax(MethodDeclarationSyntax methodSyntax)
    {
        var targetTree = methodSyntax.SyntaxTree;
        var targetSpan = methodSyntax.Span;

        return Compilation.Module.GlobalNamespace
            .GetAllMembersRecursive()
            .OfType<INamedTypeSymbol>()
            .SelectMany(type => type.GetMembers(methodSyntax.Identifier.ValueText).OfType<SourceMethodSymbol>())
            .FirstOrDefault(method =>
                method.IsAsync &&
                method.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan));
    }

    private SourceMethodSymbol? ResolveCanonicalSourceMethodForSyntax(SyntaxNode syntaxNode, SourceMethodSymbol? fallback)
    {
        for (var current = syntaxNode; current is not null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax methodDeclaration)
                return ResolveCanonicalSourceMethod(methodDeclaration, fallback);
        }

        return fallback;
    }

    private SyntaxNode? TryResolveLoweringNode(SyntaxNode syntaxNode)
    {
        return syntaxNode switch
        {
            ArrowExpressionClauseSyntax arrow => arrow.Expression,
            _ => null
        };
    }

    private static BoundBlockStatement ConvertExpressionBodyToBlock(SourceMethodSymbol method, BoundExpression expression)
    {
        if (expression is BoundBlockExpression blockExpression)
            return new BoundBlockStatement(blockExpression.Statements, blockExpression.LocalsToDispose);

        if (method.ReturnType.SpecialType == SpecialType.System_Unit)
            return new BoundBlockStatement(new[] { new BoundExpressionStatement(expression) });

        return new BoundBlockStatement(new[] { new BoundReturnStatement(expression) });
    }

    private static SourceMethodSymbol? TryGetTopLevelMainMethod(Binder binder)
    {
        for (var current = binder; current is not null; current = current.ParentBinder)
        {
            if (current is TopLevelBinder topLevelBinder && topLevelBinder.MainMethod is SourceMethodSymbol mainMethod)
                return mainMethod;
        }

        return null;
    }

    private void EnsureTopLevelCompilationUnitBound(
        CompilationUnitSyntax compilationUnit,
        bool ensureSourceDeclarations = true)
    {
        if (TryGetCachedBoundNode(compilationUnit) is not null)
            return;

        static TopLevelBinder? FindTopLevelBinder(Binder? binder)
        {
            for (var current = binder; current is not null; current = current.ParentBinder)
            {
                if (current is TopLevelBinder topLevel)
                    return topLevel;
            }

            return null;
        }

        var globals = GetTopLevelGlobalStatements(compilationUnit).ToArray();
        if (globals.Length == 0)
            return;

        var topLevelBinder = FindTopLevelBinder(GetTopLevelCompilationUnitBinder(compilationUnit))
            ?? FindTopLevelBinder(GetTopLevelCompilationUnitBinder(globals[0]));
        if (topLevelBinder is null)
            return;

        EnsureTopLevelFunctionDeclarations(compilationUnit, ensureSourceDeclarations);
        topLevelBinder.BindGlobalStatements(globals);

        Binder GetTopLevelCompilationUnitBinder(SyntaxNode node)
            => ensureSourceDeclarations
                ? GetBinder(node)
                : GetBinderForIncrementalSemanticQuery(node);
    }

    internal void EnsureTopLevelFunctionDeclarations(
        CompilationUnitSyntax compilationUnit,
        bool ensureSourceDeclarations = true)
    {
        static TopLevelBinder? FindTopLevelBinder(Binder? binder)
        {
            for (var current = binder; current is not null; current = current.ParentBinder)
            {
                if (current is TopLevelBinder topLevel)
                    return topLevel;
            }

            return null;
        }

        var globals = GetTopLevelGlobalStatements(compilationUnit).ToArray();
        var functionGlobals = GetTopLevelFunctionGlobalStatements(compilationUnit).ToArray();
        if (functionGlobals.Length == 0)
            return;

        var topLevelBinder = FindTopLevelBinder(GetTopLevelFunctionDeclarationBinder(compilationUnit))
            ?? (globals.Length > 0 ? FindTopLevelBinder(GetTopLevelFunctionDeclarationBinder(globals[0])) : null);

        if (topLevelBinder is not null)
            topLevelBinder.DeclareGlobalFunctions(globals.Concat(functionGlobals));

        foreach (var global in functionGlobals)
        {
            if (global.Statement is FunctionStatementSyntax function)
            {
                var declared = GetDeclaredSymbol(function) as IMethodSymbol;
                if (declared is null ||
                    declared is SourceMethodSymbol { IsSignatureSkeleton: true } ||
                    !declared.DeclaringSyntaxReferences.Any(reference =>
                        reference.SyntaxTree == function.SyntaxTree &&
                        reference.Span == function.Span))
                {
                    var parentBinder = (Binder?)topLevelBinder ?? GetTopLevelFunctionDeclarationBinder(compilationUnit);
                    var functionBinder = new FunctionBinder(parentBinder, function);
                    _ = functionBinder.GetMethodSymbol();
                }
            }
        }

        Binder GetTopLevelFunctionDeclarationBinder(SyntaxNode node)
            => ensureSourceDeclarations
                ? GetBinder(node)
                : GetBinderForIncrementalSemanticQuery(node);
    }

    private static IEnumerable<GlobalStatementSyntax> GetTopLevelFunctionGlobalStatements(CompilationUnitSyntax compilationUnit)
        => compilationUnit
            .DescendantNodes()
            .OfType<GlobalStatementSyntax>()
            .Where(Compilation.IsTopLevelFunctionMember);

    private IEnumerable<GlobalStatementSyntax> GetTopLevelGlobalStatements(CompilationUnitSyntax compilationUnit)
    {
        foreach (var member in compilationUnit.Members)
        {
            switch (member)
            {
                case GlobalStatementSyntax global when IsTopLevelProgramStatement(global):
                    yield return global;
                    break;
                case FileScopedNamespaceDeclarationSyntax fileScoped:
                    foreach (var nested in fileScoped.Members.OfType<GlobalStatementSyntax>()
                                 .Where(IsTopLevelProgramStatement))
                        yield return nested;
                    break;
            }
        }
    }

    private bool IsTopLevelProgramStatement(GlobalStatementSyntax global)
        => global.Statement is not FunctionStatementSyntax ||
            (Compilation.IsTopLevelFunctionMember(global) &&
             Compilation.IsFileScopeLocalFunction(global));

    private BoundBlockStatement CreateSyntheticTopLevelBlock(CompilationUnitSyntax compilationUnit)
    {
        var statements = new List<BoundStatement>();
        var localsToDispose = ImmutableArray.CreateBuilder<ILocalSymbol>();

        foreach (var global in GetTopLevelGlobalStatements(compilationUnit))
        {
            if (TryGetCachedBoundNode(global.Statement) is BoundStatement cachedStatement)
            {
                statements.Add(cachedStatement);
            }
            else if (BindNodeWithCurrentDiagnosticMode(GetBinder(global.Statement), global.Statement) is BoundStatement boundStatement)
            {
                statements.Add(boundStatement);
            }

            if (global.Statement is UseDeclarationStatementSyntax { InBlockClause: null } useDeclaration)
            {
                foreach (var declarator in useDeclaration.Declaration.Declarators)
                {
                    if (GetDeclaredSymbol(declarator) is ILocalSymbol localSymbol)
                        localsToDispose.Add(localSymbol);
                }
            }
        }

        return new BoundBlockStatement(statements, localsToDispose.ToImmutable());
    }

    /// <summary>
    /// Resolves the binder for a specific syntax node.
    /// </summary>
    /// <param name="node">The syntax node</param>
    /// <param name="parentBinder">Be careful</param>
    /// <returns>The binder for the specified syntax node</returns>
    /// <remarks>Might return a cached binder</remarks>
    internal Binder GetBinder(SyntaxNode node, Binder? parentBinder = null)
        => GetBinderCore(node, parentBinder, ensureSourceDeclarations: true);

    private Binder GetBinderForIncrementalSemanticQuery(SyntaxNode node, Binder? parentBinder = null)
        => GetBinderCore(
            node,
            parentBinder,
            ensureSourceDeclarations: false);

    private static bool IsInsideTopLevelFunctionMember(SyntaxNode node)
        => node.AncestorsAndSelf()
            .OfType<FunctionStatementSyntax>()
            .Any(IsTopLevelFunctionMember);

    private Binder GetBinderCore(SyntaxNode node, Binder? parentBinder, bool ensureSourceDeclarations)
    {
        if (ensureSourceDeclarations && !Compilation.SourceDeclarationsDeclared)
            Compilation.EnsureSourceDeclarationsDeclared();

        using var sourceNamespaceLookupSuppression = ensureSourceDeclarations
            ? null
            : Compilation.SuppressSourceNamespaceLookupDeclarationCompletion();

        FunctionExpressionSyntax? enclosingFunctionExpression = null;
        var isFunctionExpressionBodyNode = parentBinder is null &&
            TryGetEnclosingFunctionExpression(node, out enclosingFunctionExpression);
        var nodeKey = GetSyntaxNodeMapKey(node);
        var useStructuralCache = CanUseStructuralBinderCache(node);
        if (TryGetCompatibleCachedBinder(node, useStructuralCache, nodeKey, out var existingBinder))
        {
            if (parentBinder is not null &&
                !ReferenceEquals(existingBinder.ParentBinder, parentBinder) &&
                node is FunctionStatementSyntax)
            {
                return Compilation.BinderFactory.GetBinder(node, parentBinder) ?? existingBinder;
            }

            if (isFunctionExpressionBodyNode &&
                !IsFunctionExpressionBodyBinder(existingBinder) &&
                TryEnsureFunctionExpressionBodyBinder(node, enclosingFunctionExpression!, out var functionBodyBinder))
            {
                return functionBodyBinder;
            }

            if (parentBinder is not null &&
                !ReferenceEquals(existingBinder.ParentBinder, parentBinder) &&
                (parentBinder is FunctionExpressionBinder || parentBinder.ContainingSymbol is ILambdaSymbol))
            {
                // Lambda rebinds must not reuse cached binders from other scopes,
                // or lambda parameters may resolve incorrectly.
                return Compilation.BinderFactory.GetBinder(node, parentBinder) ?? existingBinder;
            }

            return existingBinder;
        }

        // special case for CompilationUnitSyntax
        if (node is CompilationUnitSyntax cu)
        {
            var binder = BindCompilationUnit(cu, parentBinder ?? Compilation.GlobalBinder, allowSourceDeclarationCompletion: ensureSourceDeclarations);
            CacheBinder(cu, binder);
            return binder;
        }

        if (isFunctionExpressionBodyNode &&
            TryEnsureFunctionExpressionBodyBinder(node, enclosingFunctionExpression!, out var contextualFunctionBodyBinder))
        {
            return contextualFunctionBodyBinder;
        }

        // Ensure parent binder is constructed and cached first
        Binder? actualParentBinder = parentBinder;

        if (actualParentBinder == null)
        {
            if (TryGetBinderParentAnchor(node, out var parentAnchor))
            {
                actualParentBinder = GetBinderCore(parentAnchor, null, ensureSourceDeclarations);
            }
            else if (!TryGetCompatibleCachedBinder(
                         node.Parent,
                         CanUseStructuralBinderCache(node.Parent),
                         GetSyntaxNodeMapKey(node.Parent),
                         out actualParentBinder))
            {
                // Recursively create and cache the parent binder first
                actualParentBinder = GetBinderCore(node.Parent, null, ensureSourceDeclarations);
            }
        }

        Binder? newBinder;

        if (node is TypeDeclarationSyntax typeDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var typeSymbol = GetDeclaredTypeSymbol(typeDeclaration);
            newBinder = typeDeclaration switch
            {
                InterfaceDeclarationSyntax interfaceDeclaration => new InterfaceDeclarationBinder(typeParentBinder, typeSymbol, interfaceDeclaration),
                _ => new ClassDeclarationBinder(typeParentBinder, typeSymbol, typeDeclaration)
            };
        }
        else if (node is InterfaceDeclarationSyntax interfaceDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var interfaceSymbol = GetDeclaredTypeSymbol(interfaceDeclaration);
            newBinder = new InterfaceDeclarationBinder(typeParentBinder, interfaceSymbol, interfaceDeclaration);
        }
        else if (node is ExtensionDeclarationSyntax extensionDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var extensionSymbol = GetDeclaredTypeSymbol(extensionDeclaration);
            newBinder = new ExtensionDeclarationBinder(typeParentBinder, extensionSymbol, extensionDeclaration);
        }
        else if (node is UnionDeclarationSyntax unionDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var unionSymbol = GetDeclaredTypeSymbol(unionDeclaration);
            newBinder = new UnionDeclarationBinder(typeParentBinder, unionSymbol, unionDeclaration);
        }
        else if (node is EnumDeclarationSyntax enumDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var enumSymbol = GetDeclaredTypeSymbol(enumDeclaration);
            newBinder = new EnumDeclarationBinder(typeParentBinder, enumSymbol, enumDeclaration);
        }
        else if (node is DelegateDeclarationSyntax delegateDeclaration &&
            actualParentBinder is not TypeDeclarationBinder)
        {
            var typeParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            var delegateSymbol = GetDeclaredTypeSymbol(delegateDeclaration);
            newBinder = new DelegateDeclarationBinder(typeParentBinder, delegateSymbol, delegateDeclaration);
        }
        else if (node is MethodDeclarationSyntax methodDeclaration &&
            TryCreateMethodDeclarationBinder(methodDeclaration, actualParentBinder, out var methodDeclarationBinder))
        {
            newBinder = methodDeclarationBinder;
        }
        else if (node is BaseMethodDeclarationSyntax &&
            actualParentBinder is not MethodBinder &&
            TryResolveMethodSymbolForDeclaration(node, out var recoveredDeclarationMethodSymbol))
        {
            var methodParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            newBinder = new MethodBinder(recoveredDeclarationMethodSymbol, methodParentBinder);
        }
        else if (node is AccessorDeclarationSyntax accessorDeclaration &&
            actualParentBinder is not MethodBinder &&
            TryResolveShallowAccessorSymbol(accessorDeclaration, out var recoveredAccessorSymbol))
        {
            var accessorParentBinder = actualParentBinder ??
                (node.Parent is not null ? GetBinderCore(node.Parent, null, ensureSourceDeclarations) : Compilation.GlobalBinder);
            newBinder = new MethodBinder(recoveredAccessorSymbol, accessorParentBinder);
        }
        else if ((node is BlockSyntax or BlockStatementSyntax or ArrowExpressionClauseSyntax) &&
            node.Parent is { } parentMethodDeclaration &&
            actualParentBinder is not MethodBinder &&
            TryResolveMethodSymbolForDeclaration(parentMethodDeclaration, out var recoveredMethodSymbol))
        {
            var methodBodyParentBinder = GetMethodBodyParentBinder(
                parentMethodDeclaration,
                actualParentBinder,
                ensureSourceDeclarations);
            newBinder = new MethodBodyBinder(recoveredMethodSymbol, methodBodyParentBinder);
        }
        else
        {
            newBinder = Compilation.BinderFactory.GetBinder(node, actualParentBinder);
        }

        CacheBinder(node, newBinder);
        return newBinder;
    }

    private bool TryGetCompatibleCachedBinder(
        SyntaxNode node,
        bool useStructuralCache,
        SyntaxNodeMapKey nodeKey,
        out Binder binder)
    {
        if (_binderCache.TryGetValue(node, out binder!) &&
            IsCachedBinderCompatible(node, binder))
        {
            return true;
        }

        if (useStructuralCache &&
            _binderCacheByKey.TryGetValue(nodeKey, out binder!) &&
            IsCachedBinderCompatible(node, binder))
        {
            return true;
        }

        binder = null!;
        return false;
    }

    private bool IsCachedBinderCompatible(SyntaxNode node, Binder binder)
    {
        if (Compilation.SourceDeclarationsDeclared &&
            _binderLifecycleSnapshots.TryGetValue(binder, out var snapshot) &&
            !snapshot.SourceDeclarationsDeclared)
        {
            // Executable binders own local, pattern, and statement-derived state. A
            // semantic query may create one before the source-declaration pass has
            // completed; once declarations complete, keep that binder and let its
            // normal binding paths upgrade any error-derived facts. Replacing it here
            // creates parallel local symbols for the same syntax and makes later
            // diagnostics look like shadowing or unused-variable noise.
            if (binder is not BlockBinder)
                return false;
        }

        if (_binderLifecycleSnapshots.TryGetValue(binder, out snapshot) &&
            snapshot.SourceNamespaceLookupSuppressed &&
            !Compilation.IsSourceNamespaceLookupDeclarationCompletionSuppressed)
        {
            return false;
        }

        return node switch
        {
            MethodDeclarationSyntax => binder is MethodBinder,
            BlockStatementSyntax { Parent: BaseMethodDeclarationSyntax or FunctionStatementSyntax } => binder is MethodBodyBinder,
            BlockStatementSyntax { Parent: MacroDeclarationSyntax } => binder is MacroBodyBinder,
            _ => true
        };
    }

    private bool TryCreateMethodDeclarationBinder(
        MethodDeclarationSyntax methodDeclaration,
        Binder? parentBinder,
        out MethodBinder methodBinder)
    {
        if (parentBinder is TypeMemberBinder typeMemberBinder)
        {
            methodBinder = typeMemberBinder.BindMethodDeclaration(methodDeclaration);
            if (methodBinder.ContainingSymbol is IMethodSymbol methodSymbol)
                RegisterMethodSymbol(methodDeclaration, methodSymbol);
            return true;
        }

        if (parentBinder is TypeDeclarationBinder { ContainingSymbol: INamedTypeSymbol containingType })
        {
            var extensionReceiverTypeSyntax = parentBinder is ExtensionDeclarationBinder &&
                methodDeclaration.Parent is ExtensionDeclarationSyntax extensionDeclaration
                    ? extensionDeclaration.ReceiverType
                    : null;

            var memberBinder = new TypeMemberBinder(parentBinder, containingType, extensionReceiverTypeSyntax);
            methodBinder = memberBinder.BindMethodDeclaration(methodDeclaration);
            if (methodBinder.ContainingSymbol is IMethodSymbol methodSymbol)
                RegisterMethodSymbol(methodDeclaration, methodSymbol);
            return true;
        }

        methodBinder = null!;
        return false;
    }

    private bool TryEnsureFunctionExpressionBodyBinder(
        SyntaxNode node,
        FunctionExpressionSyntax enclosingFunctionExpression,
        out Binder binder)
    {
        if (TryGetCachedContextualBoundFunctionExpression(enclosingFunctionExpression, out _) &&
            TryGetCachedBinder(node, out var cachedBinder) &&
            IsFunctionExpressionBodyBinder(cachedBinder))
        {
            binder = cachedBinder;
            return true;
        }

        binder = null!;
        return false;
    }

    private bool TryGetCachedContextualBoundFunctionExpression(
        FunctionExpressionSyntax functionExpression,
        out BoundFunctionExpression boundFunction)
    {
        if (TryGetCachedBoundNode(functionExpression) is BoundFunctionExpression cachedFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedFunction))
        {
            boundFunction = cachedFunction;
            return true;
        }

        var contextualRoot = GetFunctionExpressionRebindRoot(functionExpression);
        if (!ReferenceEquals(contextualRoot, functionExpression) &&
            TryGetCachedBoundNode(contextualRoot) is { } contextualBoundRoot &&
            TryFindBoundNodeBySyntax(contextualBoundRoot, functionExpression, out var cachedContextualNode) &&
            cachedContextualNode is BoundFunctionExpression cachedContextualFunction &&
            !IsLikelyStaleFunctionBodyNode(cachedContextualFunction))
        {
            boundFunction = cachedContextualFunction;
            return true;
        }

        boundFunction = null!;
        return false;
    }

    private bool TryGetCachedBinder(SyntaxNode node, out Binder binder)
    {
        if (_binderCache.TryGetValue(node, out binder))
            return true;

        if (CanUseStructuralBinderCache(node) &&
            _binderCacheByKey.TryGetValue(GetSyntaxNodeMapKey(node), out binder))
        {
            return true;
        }

        binder = null!;
        return false;
    }

    private static bool IsFunctionExpressionBodyBinder(Binder binder)
        => binder is FunctionExpressionBinder || binder.ContainingSymbol is ILambdaSymbol;

    private Binder GetMethodBodyParentBinder(
        SyntaxNode methodDeclaration,
        Binder? actualParentBinder,
        bool ensureSourceDeclarations)
    {
        if (actualParentBinder is MethodBinder methodBinder)
            return methodBinder;

        if (actualParentBinder is FunctionBinder functionBinder)
            return functionBinder.GetMethodBodyBinder();

        var declarationBinder = actualParentBinder ?? GetBinderCore(methodDeclaration, null, ensureSourceDeclarations);
        return declarationBinder switch
        {
            MethodBinder recoveredMethodBinder => recoveredMethodBinder,
            FunctionBinder recoveredFunctionBinder => recoveredFunctionBinder.GetMethodBodyBinder(),
            _ => declarationBinder
        };
    }

    internal void CacheBinderForNode(SyntaxNode node, Binder binder)
        => CacheBinder(node, binder);

    private void CacheBinder(SyntaxNode node, Binder binder)
    {
        _binderCache[node] = binder;
        if (CanUseStructuralBinderCache(node))
            _binderCacheByKey[GetSyntaxNodeMapKey(node)] = binder;

        _binderLifecycleSnapshots[binder] = CreateBinderLifecycleSnapshot(node, binder);

        if (TryComputeBinderParentAnchor(node, out var anchor))
        {
            Compilation.StoreBinderParentAnchorDescriptor(
                node,
                new Compilation.BinderParentAnchorDescriptor(anchor.Span, anchor.Kind));
        }
    }

    private BinderLifecycleSnapshot CreateBinderLifecycleSnapshot(SyntaxNode node, Binder binder)
    {
        var isStructuralCacheable = CanUseStructuralBinderCache(node);
        return new BinderLifecycleSnapshot(
            binder.GetType().Name,
            node.Kind,
            node.Span,
            isStructuralCacheable ? "StructuralNode" : "ExactNode",
            isStructuralCacheable,
            Compilation.SourceDeclarationsDeclared,
            Compilation.IsSourceNamespaceLookupDeclarationCompletionSuppressed,
            CreateBinderSymbolKey(binder.ContainingSymbol),
            binder.ParentBinder?.GetType().Name,
            binder.ParentBinder is null ? null : CreateBinderSymbolKey(binder.ParentBinder.ContainingSymbol));
    }

    private static string CreateBinderSymbolKey(ISymbol? symbol)
    {
        return symbol switch
        {
            null => "<null>",
            IMethodSymbol method => CreateMethodBinderSymbolKey(method),
            INamedTypeSymbol type => CreateNamedTypeBinderSymbolKey(type),
            IParameterSymbol parameter => $"{parameter.Kind}:{CreateBinderSymbolKey(parameter.ContainingSymbol)}.{parameter.Name}:{CreateTypeKey(parameter.Type)}:{parameter.RefKind}",
            _ => $"{symbol.Kind}:{CreateBinderSymbolKey(symbol.ContainingSymbol)}.{symbol.Name}"
        };
    }

    private static string CreateMethodBinderSymbolKey(IMethodSymbol method)
    {
        var parameters = string.Join(
            ",",
            method.Parameters.Select(parameter => $"{parameter.RefKind}:{parameter.Name}:{CreateTypeKey(parameter.Type)}"));

        return $"{method.Kind}:{CreateBinderSymbolKey(method.ContainingSymbol)}.{method.Name}<{method.Arity}>({parameters}):{CreateTypeKey(method.ReturnType)}";
    }

    private static string CreateNamedTypeBinderSymbolKey(INamedTypeSymbol type)
    {
        return $"{type.Kind}:{CreateBinderSymbolKey(type.ContainingSymbol)}.{type.MetadataName}";
    }

    private static string CreateTypeKey(ITypeSymbol? type)
    {
        return type is null
            ? "<null>"
            : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool CanUseStructuralBinderCache(SyntaxNode node)
    {
        return node is
            CompilationUnitSyntax or
            BaseNamespaceDeclarationSyntax or
            TypeDeclarationSyntax or
            InterfaceDeclarationSyntax or
            ExtensionDeclarationSyntax or
            UnionDeclarationSyntax or
            EnumDeclarationSyntax or
            DelegateDeclarationSyntax or
            MethodDeclarationSyntax or
            ConstructorDeclarationSyntax or
            OperatorDeclarationSyntax or
            ConversionOperatorDeclarationSyntax or
            FunctionStatementSyntax or
            MacroDeclarationSyntax or
            AccessorDeclarationSyntax or
            PropertyDeclarationSyntax or
            EventDeclarationSyntax or
            IndexerDeclarationSyntax;
    }

    private bool TryGetBinderParentAnchor(SyntaxNode node, out SyntaxNode anchor)
    {
        if (node.Parent is null)
        {
            anchor = null!;
            return false;
        }

        if (Compilation.TryGetBinderParentAnchorDescriptor(node, out var cachedDescriptor) &&
            TryResolveBinderParentAnchorDescriptor(node.SyntaxTree.GetRoot(), cachedDescriptor, out anchor))
        {
            return true;
        }

        if (TryComputeBinderParentAnchor(node, out anchor))
        {
            Compilation.StoreBinderParentAnchorDescriptor(
                node,
                new Compilation.BinderParentAnchorDescriptor(anchor.Span, anchor.Kind));
            return true;
        }

        anchor = null!;
        return false;
    }

    private static bool TryComputeBinderParentAnchor(SyntaxNode node, out SyntaxNode anchor)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (CreatesDistinctBinder(current) || CanUseStructuralBinderCache(current))
            {
                anchor = current;
                return true;
            }
        }

        anchor = null!;
        return false;
    }

    private static bool TryResolveBinderParentAnchorDescriptor(
        SyntaxNode treeRoot,
        Compilation.BinderParentAnchorDescriptor descriptor,
        out SyntaxNode anchor)
    {
        var candidate = treeRoot.FindNode(descriptor.Span, getInnermostNodeForTie: true);
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (current.Kind == descriptor.Kind && current.Span == descriptor.Span)
            {
                anchor = current;
                return true;
            }
        }

        anchor = null!;
        return false;
    }

    private static bool CreatesDistinctBinder(SyntaxNode node)
    {
        return node is
            AttributeListSyntax or
            AttributeSyntax or
            BlockSyntax or
            BlockStatementSyntax or
            ArrowExpressionClauseSyntax or
            IfExpressionSyntax or
            IfPatternExpressionSyntax or
            IfStatementSyntax or
            IfPatternStatementSyntax or
            ElseExpressionClauseSyntax or
            WhileStatementSyntax or
            WhilePatternStatementSyntax or
            ForStatementSyntax or
            FunctionStatementSyntax or
            MacroDeclarationSyntax;
    }

    private bool TryResolveMethodSymbolForDeclaration(SyntaxNode declaration, out IMethodSymbol methodSymbol)
    {
        return declaration switch
        {
            MethodDeclarationSyntax methodDeclaration => TryResolveOrdinaryMethodSymbolForDeclaration(methodDeclaration, out methodSymbol),
            FunctionStatementSyntax functionStatement => TryResolveFunctionStatementSymbolForDeclaration(functionStatement, out methodSymbol),
            OperatorDeclarationSyntax operatorDeclaration => TryResolveOperatorMethodSymbolForDeclaration(operatorDeclaration, out methodSymbol),
            ConversionOperatorDeclarationSyntax conversionDeclaration => TryResolveConversionMethodSymbolForDeclaration(conversionDeclaration, out methodSymbol),
            AccessorDeclarationSyntax accessorDeclaration => TryResolveShallowAccessorSymbol(accessorDeclaration, out methodSymbol),
            _ => ReturnFalse(out methodSymbol)
        };

        static bool ReturnFalse(out IMethodSymbol symbol)
        {
            symbol = null!;
            return false;
        }
    }

    private bool TryResolveFunctionStatementSymbolForDeclaration(
        FunctionStatementSyntax functionStatement,
        out IMethodSymbol methodSymbol)
    {
        if (TryResolveAvailableFunctionStatementSymbol(functionStatement, out var availableSymbol))
        {
            methodSymbol = availableSymbol;
            return true;
        }

        if (GetDeclaredSymbol(functionStatement) is IMethodSymbol symbol &&
            symbol is not SourceMethodSymbol { IsSignatureSkeleton: true })
        {
            methodSymbol = symbol;
            return true;
        }

        methodSymbol = null!;
        return false;
    }

    private bool TryResolveOrdinaryMethodSymbolForDeclaration(MethodDeclarationSyntax methodDeclaration, out IMethodSymbol methodSymbol)
    {
        if (TryGetMethodSymbol(methodDeclaration, out methodSymbol))
            return true;

        if (methodDeclaration.Parent is TypeDeclarationSyntax containingTypeSyntax &&
            TryGetClassSymbol(containingTypeSyntax, out var containingType))
        {
            var targetTree = methodDeclaration.SyntaxTree;
            var targetSpan = methodDeclaration.Span;
            var parameterCount = methodDeclaration.ParameterList?.Parameters.Count ?? 0;
            var arity = methodDeclaration.TypeParameterList?.Parameters.Count ?? 0;

            var exact = containingType
                .GetMembers(methodDeclaration.Identifier.ValueText)
                .OfType<IMethodSymbol>()
                .OrderBy(method => method is SourceMethodSymbol { IsSignatureSkeleton: true } ? 1 : 0)
                .FirstOrDefault(method =>
                    method.Parameters.Length == parameterCount &&
                    method.Arity == arity &&
                    method.DeclaringSyntaxReferences.Any(reference =>
                        reference.SyntaxTree == targetTree &&
                        reference.Span == targetSpan));

            if (exact is not null)
            {
                methodSymbol = exact;
                return true;
            }
        }

        methodSymbol = null!;
        return false;
    }

    private bool TryResolveOperatorMethodSymbolForDeclaration(OperatorDeclarationSyntax operatorDeclaration, out IMethodSymbol methodSymbol)
    {
        var parameterCount = operatorDeclaration.ParameterList.Parameters.Count;
        if (!OperatorFacts.TryGetUserDefinedOperatorInfo(operatorDeclaration.OperatorToken.Kind, parameterCount, out var operatorInfo))
        {
            methodSymbol = null!;
            return false;
        }

        return TryResolveSpecialMethodSymbolForDeclaration(
            operatorDeclaration,
            operatorInfo.MetadataName,
            parameterCount,
            arity: 0,
            out methodSymbol);
    }

    private bool TryResolveConversionMethodSymbolForDeclaration(ConversionOperatorDeclarationSyntax conversionDeclaration, out IMethodSymbol methodSymbol)
    {
        if (!OperatorFacts.TryGetConversionOperatorMetadataName(conversionDeclaration.ConversionKindKeyword.Kind, out var metadataName))
        {
            methodSymbol = null!;
            return false;
        }

        return TryResolveSpecialMethodSymbolForDeclaration(
            conversionDeclaration,
            metadataName,
            conversionDeclaration.ParameterList.Parameters.Count,
            arity: 0,
            out methodSymbol);
    }

    private bool TryResolveSpecialMethodSymbolForDeclaration(
        SyntaxNode declaration,
        string metadataName,
        int parameterCount,
        int arity,
        out IMethodSymbol methodSymbol)
    {
        if (declaration.Parent is not { } containingDeclaration ||
            !Compilation.TryGetDeclaredTypeSymbol(containingDeclaration, out var containingType))
        {
            methodSymbol = null!;
            return false;
        }

        var targetTree = declaration.SyntaxTree;
        var targetSpan = declaration.Span;
        var exact = containingType
            .GetMembers(metadataName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(method =>
                method.Parameters.Length == parameterCount &&
                method.Arity == arity &&
                method.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == targetTree &&
                    reference.Span == targetSpan));

        if (exact is null)
        {
            methodSymbol = null!;
            return false;
        }

        methodSymbol = exact;
        return true;
    }

    internal void EnsureRootBinderCreated()
    {
        Compilation.PerformanceInstrumentation.Setup.RecordEnsureRootBinderCreatedCall();

        if (_rootBinderCreated)
            return;

        var currentThreadId = Environment.CurrentManagedThreadId;

        lock (_bindingSetupGate)
        {
            while (_isCreatingRootBinder && _rootBinderThreadId != currentThreadId)
                Monitor.Wait(_bindingSetupGate);

            if (_rootBinderCreated || _isCreatingRootBinder)
                return;

            _isCreatingRootBinder = true;
            _rootBinderThreadId = currentThreadId;

            try
            {
                var root = SyntaxTree.GetRoot();
                _ = GetBinder(root);
                _rootBinderCreated = true;
                Compilation.PerformanceInstrumentation.Setup.RecordRootBinderCreated();
            }
            finally
            {
                _rootBinderThreadId = 0;
                _isCreatingRootBinder = false;
                Monitor.PulseAll(_bindingSetupGate);
            }
        }
    }

    internal bool RootBinderCreated => _rootBinderCreated;

    private void EnsureBindingReady()
    {
        if (!Compilation.SourceDeclarationsDeclared)
            Compilation.EnsureSourceDeclarationsDeclared();
        else if (!DeclarationsComplete)
            EnsureDeclarations();

        if (!RootBinderCreated)
            EnsureRootBinderCreated();
    }

    private void EnsureBindingReadyForSemanticQuery()
    {
        if (!Compilation.SourceDeclarationsDeclared)
            Compilation.EnsureSourceDeclarationsDeclared();
        else if (!DeclarationsComplete)
            EnsureDeclarations();

        if (!RootBinderCreated)
            EnsureRootBinderCreatedForSemanticQuery();
    }

    private void EnsureRootBinderCreatedForSemanticQuery()
    {
        Compilation.PerformanceInstrumentation.Setup.RecordEnsureRootBinderCreatedCall();

        if (_rootBinderCreated)
            return;

        var currentThreadId = Environment.CurrentManagedThreadId;

        lock (_bindingSetupGate)
        {
            while (_isCreatingRootBinder && _rootBinderThreadId != currentThreadId)
                Monitor.Wait(_bindingSetupGate);

            if (_rootBinderCreated || _isCreatingRootBinder)
                return;

            _isCreatingRootBinder = true;
            _rootBinderThreadId = currentThreadId;

            try
            {
                var root = SyntaxTree.GetRoot();
                _ = GetBinderForIncrementalSemanticQuery(root);
                _rootBinderCreated = true;
                Compilation.PerformanceInstrumentation.Setup.RecordRootBinderCreated();
            }
            finally
            {
                _rootBinderThreadId = 0;
                _isCreatingRootBinder = false;
                Monitor.PulseAll(_bindingSetupGate);
            }
        }
    }

    private INamespaceSymbol? GetMergedNamespace(INamespaceSymbol? namespaceSymbol)
    {
        if (namespaceSymbol is null)
            return null;

        var merged = Compilation.GlobalNamespace;
        if (namespaceSymbol.IsGlobalNamespace)
            return merged;

        var namespaceName = namespaceSymbol.ToMetadataName();
        if (string.IsNullOrEmpty(namespaceName))
            return merged;

        foreach (var part in namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            merged = merged.LookupNamespace(part);

            if (merged is null)
                break;
        }

        return merged;
    }
}
