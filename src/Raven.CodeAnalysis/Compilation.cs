using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Threading;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Scripting;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

public partial class Compilation
{
    private readonly object _setupLock = new();
    private INamespaceSymbol? _globalNamespace;
    private readonly SyntaxTree[] _syntaxTrees;
    private readonly SyntaxTree[] _macroSyntaxTrees;
    private readonly MetadataReference[] _references;
    private readonly MacroReference[] _macroReferences;
    internal SyntaxTree? SyntaxTreeWithFileScopedCode;
    private readonly ConcurrentDictionary<MetadataReference, IAssemblySymbol> _metadataReferenceSymbols = new();
    private readonly ConcurrentDictionary<Assembly, IAssemblySymbol> _assemblySymbols = new();
    private readonly ConcurrentDictionary<string, Assembly> _lazyMetadataAssemblies = new();
    private readonly ConcurrentDictionary<string, string> _assemblyPathMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Assembly, Assembly> _metadataToRuntimeAssemblyMap = new();
    private readonly ConcurrentDictionary<string, Assembly> _runtimeAssemblyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SyntaxTree, SourceDiagnosticSuppressionMap> _sourceDiagnosticSuppressionMaps = new();
    private readonly ConcurrentDictionary<SyntaxTree, ImmutableArray<GlobalStatementSyntax>> _bindableGlobalStatementsCache = new();
    private readonly ConcurrentDictionary<SyntaxTree, bool> _hasNonGlobalMembersCache = new();
    private readonly ConcurrentDictionary<string, object> _namespaceSymbolCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _metadataTypeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _metadataReferenceTypeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _preferredMetadataTypeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _scopedMetadataTypeCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<SpecialType, INamedTypeSymbol> _specialTypeCache = new();
    private readonly DescriptorState _descriptorState = new();
    private static readonly ConcurrentDictionary<string, string> s_globalAssemblyPathMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Assembly> s_globalRuntimeAssemblyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_missingMetadataType = new();
    private static int s_trustedPlatformAssembliesInitialized;
    private bool _trustedPlatformAssembliesCached;
    private MetadataLoadContext _metadataLoadContext;
    private GlobalBinder _globalBinder;
    private bool setup;
    private ErrorTypeSymbol _errorTypeSymbol;
    private NullTypeSymbol _nullTypeSymbol;
    private UnitTypeSymbol _unitTypeSymbol;
    private ReflectionTypeLoader _reflectionTypeLoader;
    private readonly object _semanticModelSetupGate = new();
    private bool _sourceTypesInitialized;
    private bool _isPopulatingSourceTypes;
    private int _sourceNamespaceLookupDeclarationCompletionSuppression;
    private int _semanticModelSetupThreadId;
    private readonly object _declarationGate = new();
    private readonly object _declarationTableGate = new();
    private bool _sourceTypeDeclarationsDeclared;
    private bool _sourceDeclarationsDeclared;
    private volatile bool _sourceDeclarationsComplete;
    private bool _isDeclaringSourceTypes;
    private int _sourceDeclarationThreadId;
    private readonly Dictionary<SyntaxTree, TopLevelProgramMembers> _topLevelProgramMembers = new();
    private readonly Dictionary<string, SynthesizedNamespaceMembersClassSymbol> _namespaceMembersContainers = new(StringComparer.Ordinal);
    private BoundNodeFactory? _boundNodeFactory;
    private DeclarationTable? _declarationTable;
    private DeclarationTable? _previousDeclarationTableForReuse;
    private MetadataLoadContext? _previousMetadataLoadContextForReuse;
    private IReadOnlyDictionary<string, PortableReferenceFingerprint>? _previousPortableReferenceFingerprints;
    private ErrorSymbol _errorSymbol;
    private bool isSettingUp;
    private int _setupThreadId;
    private MacroRegistry? _macroRegistry;
    private ImmutableArray<MacroReference> _activeMacroReferences;
    private ImmutableArray<Diagnostic> _macroPartitionDiagnostics = ImmutableArray<Diagnostic>.Empty;
    private CompilationSymbolLookup? _symbolLookup;
    private SourceDeclarationIndex? _sourceDeclarationIndex;
    private Dictionary<string, PortableReferenceFingerprint>? _portableReferenceFingerprints;
    private ImmutableArray<Diagnostic> _generatorDiagnostics = ImmutableArray<Diagnostic>.Empty;
    private SubmissionCompilationState? _submissionState;
    private int _runtimeSupportsAsyncMethods = -1;

    internal bool IsSourceNamespaceLookupDeclarationCompletionSuppressed =>
        Volatile.Read(ref _sourceNamespaceLookupDeclarationCompletionSuppression) > 0;

    internal IDisposable SuppressSourceNamespaceLookupDeclarationCompletion()
    {
        Interlocked.Increment(ref _sourceNamespaceLookupDeclarationCompletionSuppression);
        return new SourceNamespaceLookupDeclarationCompletionSuppression(this);
    }

    private sealed class SourceNamespaceLookupDeclarationCompletionSuppression : IDisposable
    {
        private Compilation? _compilation;

        public SourceNamespaceLookupDeclarationCompletionSuppression(Compilation compilation)
        {
            _compilation = compilation;
        }

        public void Dispose()
        {
            var compilation = Interlocked.Exchange(ref _compilation, null);
            if (compilation is not null)
                Interlocked.Decrement(ref compilation._sourceNamespaceLookupDeclarationCompletionSuppression);
        }
    }

    private Compilation(
        string? assemblyName,
        SyntaxTree[] syntaxTrees,
        SyntaxTree[] macroSyntaxTrees,
        MetadataReference[] references,
        MacroReference[] macroReferences,
        CompilationOptions? options = null,
        ImmutableArray<Diagnostic> generatorDiagnostics = default,
        ScriptCompilationInfo? scriptCompilationInfo = null)
    {
        if (scriptCompilationInfo is not null)
            ValidateScriptCompilation(syntaxTrees, scriptCompilationInfo);

        AssemblyName = string.IsNullOrWhiteSpace(assemblyName) ? "assembly" : assemblyName;
        _syntaxTrees = syntaxTrees;
        _macroSyntaxTrees = macroSyntaxTrees;
        _references = references;
        _macroReferences = macroReferences;
        Options = options ?? new CompilationOptions();
        ScriptCompilationInfo = scriptCompilationInfo;
        _generatorDiagnostics = generatorDiagnostics.IsDefault
            ? ImmutableArray<Diagnostic>.Empty
            : generatorDiagnostics;
    }

    internal GlobalBinder GlobalBinder => _globalBinder ??= new GlobalBinder(this);

    internal CompilationSymbolLookup SymbolLookup => _symbolLookup ??= new CompilationSymbolLookup(this);

    internal SourceDeclarationIndex SourceDeclarationIndex => _sourceDeclarationIndex ??= new SourceDeclarationIndex(this, _syntaxTrees);

    public string AssemblyName { get; }

    public CompilationOptions Options { get; }

    /// <summary>
    /// Gets whether runtime-async lowering is both requested and supported by
    /// the target framework reference assemblies.
    /// </summary>
    internal bool IsRuntimeAsyncEnabled
    {
        get
        {
            if (!Options.UseRuntimeAsync)
                return false;

            var cached = Volatile.Read(ref _runtimeSupportsAsyncMethods);
            if (cached >= 0)
                return cached == 1;

            var objectType = GetSpecialType(SpecialType.System_Object);
            var asyncHelpers = GetTypeByMetadataName("System.Runtime.CompilerServices.AsyncHelpers");
            var supported = objectType.TypeKind is not TypeKind.Error &&
                            asyncHelpers is { TypeKind: TypeKind.Class, IsStatic: true } &&
                            SymbolEqualityComparer.Default.Equals(
                                objectType.ContainingAssembly,
                                asyncHelpers.ContainingAssembly) &&
                            HasRuntimeAsyncEntryPointContract(asyncHelpers);

            Interlocked.CompareExchange(ref _runtimeSupportsAsyncMethods, supported ? 1 : 0, -1);
            return Volatile.Read(ref _runtimeSupportsAsyncMethods) == 1;
        }
    }

    private static bool HasRuntimeAsyncEntryPointContract(INamedTypeSymbol asyncHelpers)
    {
        return asyncHelpers.GetMembers("HandleAsyncEntryPoint")
            .OfType<IMethodSymbol>()
            .Any(static method =>
                method.IsStatic &&
                method.Arity == 0 &&
                method.Parameters.Length == 1);
    }

    /// <summary>
    /// Gets information about this compilation's script submission chain, or <see langword="null"/>
    /// when this is a regular compilation.
    /// </summary>
    public ScriptCompilationInfo? ScriptCompilationInfo { get; }

    /// <summary>
    /// Gets whether this compilation represents a script submission.
    /// </summary>
    public bool IsSubmission => ScriptCompilationInfo is not null;

    internal SubmissionCompilationState SubmissionState
        => LazyInitializer.EnsureInitialized(
            ref _submissionState,
            () => new SubmissionCompilationState(this));

    public PerformanceInstrumentation PerformanceInstrumentation => Options.PerformanceInstrumentation;

    public ILoweringTraceSink? LoweringTrace => Options.LoweringTrace;

    public IOverloadResolutionLogger? OverloadResolutionLogger => Options.OverloadResolutionLogger;

    public IAssemblySymbol Assembly { get; private set; }

    public IModuleSymbol Module { get; private set; }

    public IEnumerable<MetadataReference> References => _references;
    public IEnumerable<MacroReference> MacroReferences
    {
        get
        {
            EnsureSetup();
            return _activeMacroReferences;
        }
    }

    public IEnumerable<IAssemblySymbol> ReferencedAssemblySymbols => Module.ReferencedAssemblySymbols;

    public SyntaxTree[] SyntaxTrees => _syntaxTrees;

    /// <summary>
    /// Gets the syntax trees in the compile-time-only local macro partition.
    /// </summary>
    public SyntaxTree[] MacroSyntaxTrees => _macroSyntaxTrees;

    public ImmutableArray<Diagnostic> GeneratorDiagnostics => _generatorDiagnostics;

    public INamespaceSymbol GlobalNamespace
    {
        get
        {
            if (!isSettingUp)
            {
                EnsureSetup();
            }

            return _globalNamespace ??=
                new MergedNamespaceSymbol(
                    new INamespaceSymbol?[] { SourceGlobalNamespace }
                        .Concat(_metadataReferenceSymbols.Select(x => x.Value.GlobalNamespace))
                        .Where(static ns => ns is not null)
                        .Cast<INamespaceSymbol>(),
                    null);
        }
    }

    internal SourceNamespaceSymbol SourceGlobalNamespace { get; private set; }

    public INamespaceSymbol GetSourceGlobalNamespace()
    {
        EnsureSetup();
        return SourceGlobalNamespace.AsSourceNamespace();
    }

    public Assembly CoreAssembly { get; private set; }
    public Assembly RuntimeCoreAssembly { get; private set; }
    internal Assembly EmitCoreAssembly { get; private set; }

    internal BinderFactory BinderFactory { get; private set; }

    internal DeclarationTable DeclarationTable => _declarationTable ?? EnsureDeclarationTableCreated();

    internal bool AreSourceDeclarationsDeclared => _sourceDeclarationsDeclared;

    internal SymbolFactory SymbolFactory { get; } = new SymbolFactory();

    internal bool TryGetVisibleValueScopeDeclarations(
        SyntaxNode scopeNode,
        out ImmutableArray<VisibleValueDeclarationDescriptor> declarations)
    {
        declarations = default;
        if (scopeNode.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new VisibleValueScopeKey(scopeNode.Span, scopeNode.Kind);

        if (_descriptorState.VisibleValueScopeDeclarations.TryGetValue(syntaxTree, out var scopes) &&
            scopes.TryGetValue(key, out declarations))
        {
            return true;
        }

        if (TryGetTransferredVisibleValueScopeDeclarations(scopeNode, out declarations))
            return true;

        return false;
    }

    internal void StoreVisibleValueScopeDeclarations(
        SyntaxNode scopeNode,
        ImmutableArray<VisibleValueDeclarationDescriptor> declarations)
    {
        if (scopeNode.SyntaxTree is not { } syntaxTree)
            return;

        var scopes = _descriptorState.VisibleValueScopeDeclarations.GetOrAdd(syntaxTree, _ => new());
        scopes[new VisibleValueScopeKey(scopeNode.Span, scopeNode.Kind)] = declarations;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(scopeNode, out var ownerDescriptor))
        {
            var ownerScopes = _descriptorState.VisibleValueScopeDeclarationsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerScopes[CreateOwnerRelativeDescriptorKey(ownerDescriptor, scopeNode)] = declarations;
        }
    }

    internal bool TryGetNodeInterestSymbolDescriptor(
        SyntaxNode node,
        out NodeInterestSymbolDescriptor descriptor)
    {
        descriptor = default;
        if (node.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new NodeInterestSymbolKey(node.Span, node.Kind);

        if (_descriptorState.NodeInterestSymbolDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredNodeInterestSymbolDescriptor(node, out descriptor))
            return true;

        return false;
    }

    internal void StoreNodeInterestSymbolDescriptor(
        SyntaxNode node,
        NodeInterestSymbolDescriptor descriptor)
    {
        if (node.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.NodeInterestSymbolDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new NodeInterestSymbolKey(node.Span, node.Kind)] = descriptor;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(node, out var ownerDescriptor))
        {
            var ownerDescriptors = _descriptorState.NodeInterestSymbolDescriptorsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerDescriptors[CreateOwnerRelativeDescriptorKey(ownerDescriptor, node)] = descriptor;
        }
    }

    internal bool TryGetContextualBindingRootDescriptor(
        SyntaxNode node,
        out ContextualBindingRootDescriptor descriptor)
    {
        descriptor = default;
        if (node.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new ContextualBindingRootKey(node.Span, node.Kind);

        if (_descriptorState.ContextualBindingRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredContextualBindingRootDescriptor(node, out descriptor))
            return true;

        return false;
    }

    internal void StoreContextualBindingRootDescriptor(
        SyntaxNode node,
        ContextualBindingRootDescriptor descriptor)
    {
        if (node.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.ContextualBindingRootDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new ContextualBindingRootKey(node.Span, node.Kind)] = descriptor;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(node, out var ownerDescriptor))
        {
            var ownerDescriptors = _descriptorState.ContextualBindingRootDescriptorsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerDescriptors[CreateOwnerRelativeDescriptorKey(ownerDescriptor, node)] = descriptor;
        }
    }

    internal bool TryGetInterestBindingRootDescriptor(
        SyntaxNode node,
        out InterestBindingRootDescriptor descriptor)
    {
        descriptor = default;
        if (node.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new InterestBindingRootKey(node.Span, node.Kind);

        if (_descriptorState.InterestBindingRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredInterestBindingRootDescriptor(node, out descriptor))
            return true;

        return false;
    }

    internal void StoreInterestBindingRootDescriptor(
        SyntaxNode node,
        InterestBindingRootDescriptor descriptor)
    {
        if (node.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.InterestBindingRootDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new InterestBindingRootKey(node.Span, node.Kind)] = descriptor;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(node, out var ownerDescriptor))
        {
            var ownerDescriptors = _descriptorState.InterestBindingRootDescriptorsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerDescriptors[CreateOwnerRelativeDescriptorKey(ownerDescriptor, node)] = descriptor;
        }
    }

    internal bool TryGetExecutableOwnerDescriptor(
        SyntaxNode node,
        out ExecutableOwnerDescriptor descriptor)
    {
        descriptor = default;
        if (node.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new ExecutableOwnerKey(node.Span, node.Kind);

        if (_descriptorState.ExecutableOwnerDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredExecutableOwnerDescriptor(node, out descriptor))
            return true;

        return false;
    }

    internal void StoreExecutableOwnerDescriptor(
        SyntaxNode node,
        ExecutableOwnerDescriptor descriptor)
    {
        if (node.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.ExecutableOwnerDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new ExecutableOwnerKey(node.Span, node.Kind)] = descriptor;
    }

    internal bool TryGetFunctionExpressionRebindRootDescriptor(
        FunctionExpressionSyntax functionExpression,
        out FunctionExpressionRebindRootDescriptor descriptor)
    {
        descriptor = default;
        if (functionExpression.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new FunctionExpressionRebindRootKey(functionExpression.Span, functionExpression.Kind);

        if (_descriptorState.FunctionExpressionRebindRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredFunctionExpressionRebindRootDescriptor(functionExpression, out descriptor))
            return true;

        return false;
    }

    internal void StoreFunctionExpressionRebindRootDescriptor(
        FunctionExpressionSyntax functionExpression,
        FunctionExpressionRebindRootDescriptor descriptor)
    {
        if (functionExpression.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.FunctionExpressionRebindRootDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new FunctionExpressionRebindRootKey(functionExpression.Span, functionExpression.Kind)] = descriptor;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(functionExpression, out var ownerDescriptor))
        {
            var ownerDescriptors = _descriptorState.FunctionExpressionRebindRootDescriptorsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerDescriptors[CreateOwnerRelativeDescriptorKey(ownerDescriptor, functionExpression)] = descriptor;
        }
    }

    internal bool TryGetBinderParentAnchorDescriptor(
        SyntaxNode node,
        out BinderParentAnchorDescriptor descriptor)
    {
        descriptor = default;
        if (node.SyntaxTree is not { } syntaxTree)
            return false;

        var key = new BinderParentAnchorKey(node.Span, node.Kind);

        if (_descriptorState.BinderParentAnchorDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
            descriptors.TryGetValue(key, out descriptor))
        {
            return true;
        }

        if (TryGetTransferredBinderParentAnchorDescriptor(node, out descriptor))
            return true;

        return false;
    }

    internal void StoreBinderParentAnchorDescriptor(
        SyntaxNode node,
        BinderParentAnchorDescriptor descriptor)
    {
        if (node.SyntaxTree is not { } syntaxTree)
            return;

        var descriptors = _descriptorState.BinderParentAnchorDescriptors.GetOrAdd(syntaxTree, _ => new());
        descriptors[new BinderParentAnchorKey(node.Span, node.Kind)] = descriptor;

        if (TryGetSyntaxOnlyExecutableOwnerDescriptor(node, out var ownerDescriptor))
        {
            var ownerDescriptors = _descriptorState.BinderParentAnchorDescriptorsByOwner.GetOrAdd(syntaxTree, _ => new());
            ownerDescriptors[CreateOwnerRelativeDescriptorKey(ownerDescriptor, node)] = descriptor;
        }
    }

    internal void RegisterChangedExecutableOwnerDescriptors(
        SyntaxTree syntaxTree,
        ImmutableArray<ExecutableOwnerDescriptor> descriptors)
    {
        _descriptorState.ChangedExecutableOwnerDescriptors[syntaxTree] = descriptors.IsDefaultOrEmpty
            ? ImmutableHashSet<ExecutableOwnerDescriptor>.Empty
            : descriptors.ToImmutableHashSet();
    }

    internal void RegisterExecutableOwnerChanges(
        SyntaxTree syntaxTree,
        ImmutableDictionary<ExecutableOwnerDescriptor, OwnerRelativeTextChange> ownerChanges)
    {
        if (ownerChanges.IsEmpty)
            return;

        var map = _descriptorState.ExecutableOwnerChanges.GetOrAdd(syntaxTree, _ => new());
        foreach (var (owner, change) in ownerChanges)
            map[owner] = change;
    }

    internal void RegisterMatchedExecutableOwners(
        SyntaxTree syntaxTree,
        ImmutableArray<MatchedExecutableOwner> matches)
    {
        if (matches.IsDefaultOrEmpty)
            return;

        var map = _descriptorState.MatchedExecutableOwners.GetOrAdd(syntaxTree, _ => new());
        foreach (var match in matches)
            map[match.CurrentOwner] = match;
    }

    internal void RegisterSemanticDiagnosticTransferBlocked(SyntaxTree syntaxTree)
        => _descriptorState.SemanticDiagnosticTransferBlockedSyntaxTrees[syntaxTree] = 0;

    internal bool IsSemanticDiagnosticTransferBlocked(SyntaxTree syntaxTree)
        => _descriptorState.SemanticDiagnosticTransferBlockedSyntaxTrees.ContainsKey(syntaxTree);

    internal bool IsChangedExecutableOwner(SyntaxNode node)
    {
        return _descriptorState.ChangedExecutableOwnerDescriptors.TryGetValue(node.SyntaxTree, out var descriptors) &&
               descriptors.Contains(new ExecutableOwnerDescriptor(node.Span, node.Kind));
    }

    internal bool TryGetExecutableOwnerChange(
        SyntaxNode node,
        out OwnerRelativeTextChange change)
    {
        change = default;
        return _descriptorState.ExecutableOwnerChanges.TryGetValue(node.SyntaxTree, out var changes) &&
               changes.TryGetValue(new ExecutableOwnerDescriptor(node.Span, node.Kind), out change);
    }

    internal ImmutableArray<ExecutableOwnerDescriptor> GetChangedExecutableOwnerDescriptorsForTesting(SyntaxTree syntaxTree)
    {
        return _descriptorState.ChangedExecutableOwnerDescriptors.TryGetValue(syntaxTree, out var descriptors)
            ? descriptors.ToImmutableArray()
            : ImmutableArray<ExecutableOwnerDescriptor>.Empty;
    }

    internal MatchedExecutableOwner? GetMatchedExecutableOwnerForTesting(SyntaxNode node)
    {
        return TryGetMatchedExecutableOwner(node, out var match)
            ? match
            : null;
    }

    internal bool TryGetMatchedExecutableOwner(
        SyntaxNode node,
        out MatchedExecutableOwner match)
    {
        match = default;

        if (!TryGetSyntaxOnlyExecutableOwnerDescriptor(node, out var ownerDescriptor))
            return false;

        return _descriptorState.MatchedExecutableOwners.TryGetValue(node.SyntaxTree, out var matches) &&
               matches.TryGetValue(ownerDescriptor, out match);
    }

    internal bool TryGetSemanticDiagnosticDescriptors(
        SyntaxNode owner,
        out ImmutableArray<SemanticDiagnosticDescriptor> descriptors)
    {
        descriptors = default;

        if (!TryGetSyntaxOnlyExecutableOwnerDescriptor(owner, out var ownerDescriptor))
            return false;

        if (_descriptorState.SemanticDiagnosticsByOwner.TryGetValue(owner.SyntaxTree, out var byOwner) &&
            byOwner.TryGetValue(ownerDescriptor, out descriptors))
        {
            return true;
        }

        if (_incrementalState is null)
            return false;

        if (_incrementalState.TryGetSemanticDiagnostics(owner.SyntaxTree, ownerDescriptor, out descriptors))
            return true;

        return TryGetTransferredOwnerRelativeDescriptor(
            owner,
            _incrementalState.TryGetSemanticDiagnostics,
            out descriptors);
    }

    internal void StoreSemanticDiagnosticDescriptors(
        SyntaxNode owner,
        ImmutableArray<SemanticDiagnosticDescriptor> descriptors)
    {
        if (!TryGetSyntaxOnlyExecutableOwnerDescriptor(owner, out var ownerDescriptor))
            return;

        var byOwner = _descriptorState.SemanticDiagnosticsByOwner.GetOrAdd(owner.SyntaxTree, _ => new());
        byOwner[ownerDescriptor] = descriptors;

        var byRelativeOwner = _descriptorState.SemanticDiagnosticsByRelativeOwner.GetOrAdd(owner.SyntaxTree, _ => new());
        byRelativeOwner[new OwnerRelativeDescriptorKey(ownerDescriptor, 0, owner.Span.Length, owner.Kind)] = descriptors;
    }

    internal bool HasTransferredSemanticDiagnosticsForTesting(SyntaxNode owner)
        => TryGetSemanticDiagnosticDescriptors(owner, out _);

    internal BoundNodeFactory BoundNodeFactory => _boundNodeFactory ??= new BoundNodeFactory(this);

    public ISymbol ErrorSymbol => _errorSymbol ??= CreateErrorSymbol();

    public ITypeSymbol ErrorTypeSymbol => _errorTypeSymbol ??= CreateErrorTypeSymbol();

    private ErrorSymbol CreateErrorSymbol()
    {
        EnsureSetup();
        return new ErrorSymbol(this, "Error", GlobalNamespace, [], []);
    }

    private ErrorTypeSymbol CreateErrorTypeSymbol()
    {
        EnsureSetup();
        return new ErrorTypeSymbol(this, "Error", GlobalNamespace, [], []);
    }

    public ITypeSymbol NullTypeSymbol => _nullTypeSymbol ??= new NullTypeSymbol(this);
    public INamedTypeSymbol UnitTypeSymbol => _unitTypeSymbol ??= CreateUnitTypeSymbol();

    public static Compilation Create(string assemblyName, SyntaxTree[] syntaxTrees, CompilationOptions? options = null)
    {
        return new Compilation(assemblyName, syntaxTrees, [], [], [], options);
    }

    public static Compilation Create(string assemblyName, CompilationOptions? options = null)
    {
        return new Compilation(assemblyName, [], [], [], [], options);
    }

    public static Compilation Create(string assemblyName, SyntaxTree[] syntaxTrees, MetadataReference[] references, CompilationOptions? options = null)
    {
        if (references.Length == 0)
            references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        return new Compilation(assemblyName, syntaxTrees, [], references, [], options);
    }

    public static Compilation Create(
        string assemblyName,
        SyntaxTree[] syntaxTrees,
        MetadataReference[] references,
        MacroReference[] macroReferences,
        CompilationOptions? options = null)
    {
        if (references.Length == 0)
            references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        return new Compilation(assemblyName, syntaxTrees, [], references, macroReferences, options);
    }

    /// <summary>
    /// Creates a compilation for one script or interactive submission.
    /// </summary>
    public static Compilation CreateScriptCompilation(
        string assemblyName,
        SyntaxTree syntaxTree,
        MetadataReference[]? references = null,
        CompilationOptions? options = null,
        Compilation? previousScriptCompilation = null,
        MetadataReference? previousScriptCompilationReference = null)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);

        if (previousScriptCompilation is { IsSubmission: false })
            throw new ArgumentException("The previous compilation must be a script submission.", nameof(previousScriptCompilation));

        if (previousScriptCompilation is null && previousScriptCompilationReference is not null)
            throw new ArgumentException("A previous submission reference requires a previous script compilation.", nameof(previousScriptCompilationReference));

        references ??= [];
        if (references.Length == 0)
            references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        if (previousScriptCompilation is not null)
        {
            references = SubmissionCompilationState.AddPreviousReferences(
                references,
                previousScriptCompilation,
                previousScriptCompilationReference);
        }

        return new Compilation(
            assemblyName,
            [syntaxTree],
            [],
            references,
            [],
            options,
            scriptCompilationInfo: new ScriptCompilationInfo(
                previousScriptCompilation,
                previousScriptCompilationReference));
    }

    public Compilation AddSyntaxTrees(params SyntaxTree[] syntaxTrees)
    {
        return new Compilation(
            AssemblyName,
            _syntaxTrees.Concat(syntaxTrees).ToArray(),
            _macroSyntaxTrees,
            _references,
            _macroReferences,
            Options,
            _generatorDiagnostics,
            ScriptCompilationInfo);
    }

    /// <summary>
    /// Adds ordinary source trees and automatically moves direct macro
    /// declarations and declarations marked with <see cref="LocalMacroAttribute"/>
    /// into the local macro partition.
    /// </summary>
    public Compilation AddSyntaxTreesWithLocalMacros(params SyntaxTree[] syntaxTrees)
    {
        var localMacroTrees = new List<SyntaxTree>();
        var consumerTrees = new List<SyntaxTree>();
        var hasDeclarationPartition = false;
        foreach (var syntaxTree in syntaxTrees)
        {
            var partition = LocalMacroSyntaxClassifier.Partition(syntaxTree);
            if (partition.ConsumerTree is not null)
                consumerTrees.Add(partition.ConsumerTree);
            if (partition.MacroTree is not null)
                localMacroTrees.Add(partition.MacroTree);
            if (partition.ConsumerTree is not null && partition.MacroTree is not null)
                hasDeclarationPartition = true;
        }

        return new Compilation(
            AssemblyName,
            _syntaxTrees.Concat(consumerTrees).ToArray(),
            _macroSyntaxTrees.Concat(localMacroTrees).ToArray(),
            hasDeclarationPartition ? EnsureMacroContractsReference(_references) : _references,
            _macroReferences,
            Options,
            _generatorDiagnostics,
            ScriptCompilationInfo);
    }

    /// <summary>
    /// Adds source trees to the compile-time-only local macro partition.
    /// </summary>
    /// <remarks>
    /// The partition is compiled and activated in memory before consumer macro
    /// invocations are bound. Its declarations are not emitted into the
    /// consumer assembly.
    /// </remarks>
    public Compilation AddMacroSyntaxTrees(params SyntaxTree[] syntaxTrees)
    {
        return new Compilation(
            AssemblyName,
            _syntaxTrees,
            _macroSyntaxTrees.Concat(syntaxTrees).ToArray(),
            _references,
            _macroReferences,
            Options,
            _generatorDiagnostics,
            ScriptCompilationInfo);
    }

    internal Compilation WithGeneratorDiagnostics(ImmutableArray<Diagnostic> diagnostics)
        => new(AssemblyName, _syntaxTrees, _macroSyntaxTrees, _references, _macroReferences, Options, diagnostics, ScriptCompilationInfo);

    public Compilation AddReferences(params MetadataReference[] references)
    {
        return new Compilation(
            AssemblyName,
            _syntaxTrees,
            _macroSyntaxTrees,
            _references.Concat(references).ToArray(),
            _macroReferences,
            Options,
            _generatorDiagnostics,
            ScriptCompilationInfo);
    }

    public Compilation AddMacroReferences(params MacroReference[] macroReferences)
    {
        return new Compilation(AssemblyName, _syntaxTrees, _macroSyntaxTrees, _references, macroReferences, Options, _generatorDiagnostics, ScriptCompilationInfo);
    }

    public Compilation WithAssemblyName(string? assemblyName)
    {
        return new Compilation(assemblyName, _syntaxTrees, _macroSyntaxTrees, _references, _macroReferences, Options, _generatorDiagnostics, ScriptCompilationInfo);
    }

    private static void ValidateScriptCompilation(
        IReadOnlyList<SyntaxTree> syntaxTrees,
        ScriptCompilationInfo scriptCompilationInfo)
    {
        if (syntaxTrees.Count(static tree =>
                tree.Options.Kind is SourceCodeKind.Script or SourceCodeKind.Interactive) != 1)
        {
            throw new ArgumentException(
                "A script compilation must contain exactly one script or interactive syntax tree.",
                nameof(syntaxTrees));
        }

        if (scriptCompilationInfo.PreviousScriptCompilation is { IsSubmission: false })
            throw new ArgumentException("The previous compilation must be a script submission.", nameof(scriptCompilationInfo));
    }

    internal ImmutableArray<ISymbol> GetPreviousSubmissionDeclarations()
        => IsSubmission ? SubmissionState.GetPreviousDeclarations() : ImmutableArray<ISymbol>.Empty;

    internal bool TryGetSubmissionVariable(ILocalSymbol local, out SubmissionVariableSymbol variable)
    {
        if (!IsSubmission)
        {
            variable = null!;
            return false;
        }

        return SubmissionState.TryGetVariable(local, out variable);
    }

    internal int SubmissionVariableCount
        => SubmissionState.VariableCount;

    internal bool IsPreviousSubmissionAssembly(IAssemblySymbol? assembly)
    {
        return IsSubmission && SubmissionState.IsPreviousAssembly(assembly);
    }

    public MetadataReference ToMetadataReference() => new CompilationReference(this);

    internal void AdoptIncrementalReuseFrom(Compilation previousCompilation)
    {
        ArgumentNullException.ThrowIfNull(previousCompilation);

        if (ReferenceEquals(this, previousCompilation))
            return;

        // Retain only reusable, compilation-independent state. Keeping the whole
        // compilation here roots every earlier editor snapshot and its symbols.
        _previousDeclarationTableForReuse = previousCompilation._declarationTable;
        AdoptMetadataReuseFrom(previousCompilation);
    }

    private void AdoptMetadataReuseFrom(Compilation previousCompilation)
    {
        if (previousCompilation.setup)
        {
            _previousMetadataLoadContextForReuse = previousCompilation._metadataLoadContext;
            _previousPortableReferenceFingerprints = previousCompilation._portableReferenceFingerprints;
        }
    }

    internal void EnsureSetup()
    {
        if (setup)
            return;

        var currentThreadId = Environment.CurrentManagedThreadId;
        if (isSettingUp && _setupThreadId == currentThreadId)
            return;

        lock (_setupLock)
        {
            while (isSettingUp && _setupThreadId != currentThreadId)
                Monitor.Wait(_setupLock);

            if (setup || isSettingUp)
                return;

            isSettingUp = true;
            _setupThreadId = currentThreadId;

            try
            {
                Setup();
                setup = true;
            }
            finally
            {
                _setupThreadId = 0;
                isSettingUp = false;
                Monitor.PulseAll(_setupLock);
            }
        }
    }

    private void Setup()
    {
        // Same-thread reentrancy during setup can observe partially initialized compilation state.
        // Seed the runtime core assemblies up front so early emit/type-resolution paths never see null.
        RuntimeCoreAssembly = typeof(object).Assembly;
        EmitCoreAssembly = RuntimeCoreAssembly;

        List<string> paths = _references
            .OfType<PortableExecutableReference>()
            .Select(portableExecutableReference => portableExecutableReference.FilePath)
            .ToList();

        var runtimeCorePath = typeof(object).Assembly.Location;
        if (!string.IsNullOrEmpty(runtimeCorePath) && !paths.Contains(runtimeCorePath, StringComparer.OrdinalIgnoreCase))
            paths.Add(runtimeCorePath);

        // Seed the metadata resolver with framework/runtime assemblies so MetadataLoadContext
        // can resolve transitive framework dependencies (for example System.Reflection.MetadataLoadContext).
        EnsureTrustedPlatformAssembliesCached();
        foreach (var knownPath in _assemblyPathMap.Values)
        {
            if (!string.IsNullOrEmpty(knownPath) && File.Exists(knownPath) && !paths.Contains(knownPath, StringComparer.OrdinalIgnoreCase))
                paths.Add(knownPath);
        }

        var coreAssemblyName = typeof(object).Assembly.GetName().Name;
        _portableReferenceFingerprints = CapturePortableReferenceFingerprints(_references);
        _metadataLoadContext = TryReuseMetadataLoadContext(_portableReferenceFingerprints, out var reusedMetadataLoadContext)
            ? reusedMetadataLoadContext
            : CreateMetadataLoadContext(paths, coreAssemblyName);
        _previousMetadataLoadContextForReuse = null;
        _previousPortableReferenceFingerprints = null;

        CoreAssembly = _metadataLoadContext.CoreAssembly!;
        EmitCoreAssembly = ResolveEmitCoreAssembly() ?? RuntimeCoreAssembly;
        RegisterRuntimeAssembly(CoreAssembly, RuntimeCoreAssembly.Location);

        foreach (var metadataReference in References)
        {
            GetAssemblyOrModuleSymbol(metadataReference);
        }

        BinderFactory = new BinderFactory(this);

        var assemblyDeclaringSyntaxReferences = SyntaxTrees
            .Select(static tree => tree.GetRoot().GetReference())
            .ToArray();

        Assembly = new SourceAssemblySymbol(this, AssemblyName, assemblyDeclaringSyntaxReferences);

        Module = new SourceModuleSymbol(AssemblyName, (SourceAssemblySymbol)Assembly, _metadataReferenceSymbols.Values, [], assemblyDeclaringSyntaxReferences);

        SourceGlobalNamespace = (SourceNamespaceSymbol)Module.GlobalNamespace;
        var requiresLocalMacroActivation = RequiresLocalMacroActivation();
        if (!requiresLocalMacroActivation)
            PrepareLocalMacroSignatures();
        var localMacroReference = requiresLocalMacroActivation
            ? CompileLocalMacroPartition()
            : null;
        _activeMacroReferences = GetActiveMacroReferences(localMacroReference);
        _macroRegistry = MacroRegistry.Create(_activeMacroReferences);
        foreach (var macroNamespace in _macroRegistry.Namespaces)
            GetOrCreateNamespaceSymbol(macroNamespace);

        InitializeTopLevelPrograms();
    }

    private ImmutableArray<MacroReference> GetActiveMacroReferences(MacroReference? localMacroReference)
    {
        var references = ImmutableArray.CreateBuilder<MacroReference>();
        references.AddRange(_macroReferences);

        var explicitAssemblyPaths = _macroReferences
            .Select(static reference => reference.Display)
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var metadataReference in _references.OfType<PortableExecutableReference>())
        {
            if (string.IsNullOrWhiteSpace(metadataReference.FilePath))
                continue;

            var assemblyPath = Path.GetFullPath(metadataReference.FilePath);
            if (explicitAssemblyPaths.Contains(assemblyPath) || !File.Exists(assemblyPath))
                continue;

            if (!MacroAssemblyMetadata.HasCompilerPluginMarker(assemblyPath))
                continue;

            references.Add(MacroReference.CreateFromFile(assemblyPath));
            explicitAssemblyPaths.Add(assemblyPath);
        }

        if (localMacroReference is not null)
            references.Add(localMacroReference);

        return references.ToImmutable();
    }

    private bool TryReuseMetadataLoadContext(
        IReadOnlyDictionary<string, PortableReferenceFingerprint> currentFingerprints,
        out MetadataLoadContext metadataLoadContext)
    {
        metadataLoadContext = null!;
        if (_previousMetadataLoadContextForReuse is null ||
            !HaveEquivalentPortableReferences(currentFingerprints))
        {
            return false;
        }

        metadataLoadContext = _previousMetadataLoadContextForReuse;
        return true;
    }

    private bool HaveEquivalentPortableReferences(
        IReadOnlyDictionary<string, PortableReferenceFingerprint> currentFingerprints)
    {
        var previousFingerprints = _previousPortableReferenceFingerprints;
        if (previousFingerprints is null || previousFingerprints.Count != currentFingerprints.Count)
            return false;

        foreach (var (path, fingerprint) in currentFingerprints)
        {
            if (!previousFingerprints.TryGetValue(path, out var previousFingerprint) ||
                previousFingerprint != fingerprint)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, PortableReferenceFingerprint> CapturePortableReferenceFingerprints(
        IEnumerable<MetadataReference> references)
    {
        var fingerprints = new Dictionary<string, PortableReferenceFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references.OfType<PortableExecutableReference>())
        {
            if (string.IsNullOrWhiteSpace(reference.FilePath))
                continue;

            var fullPath = Path.GetFullPath(reference.FilePath);
            var file = new FileInfo(fullPath);
            fingerprints[fullPath] = file.Exists
                ? new PortableReferenceFingerprint(file.Length, file.LastWriteTimeUtc.Ticks)
                : default;
        }

        return fingerprints;
    }

    private readonly record struct PortableReferenceFingerprint(long Length, long LastWriteTimeUtcTicks);

    private DeclarationTable EnsureDeclarationTableCreated()
    {
        if (_declarationTable is not null)
            return _declarationTable;

        lock (_declarationTableGate)
        {
            _declarationTable ??= new DeclarationTable(SyntaxTrees, _previousDeclarationTableForReuse);
            _previousDeclarationTableForReuse = null;
            return _declarationTable;
        }
    }

    internal MacroRegistry GetMacroRegistry()
    {
        EnsureSetup();
        return _macroRegistry ??= MacroRegistry.Create(
            _activeMacroReferences.IsDefault ? _macroReferences : _activeMacroReferences);
    }

    private bool TryGetVisibleValueScopeDeclarations(
        SyntaxTree syntaxTree,
        VisibleValueScopeKey key,
        out ImmutableArray<VisibleValueDeclarationDescriptor> declarations)
    {
        declarations = default;
        return _descriptorState.VisibleValueScopeDeclarations.TryGetValue(syntaxTree, out var scopes) &&
               scopes.TryGetValue(key, out declarations);
    }

    private bool TryGetVisibleValueScopeDeclarations(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out ImmutableArray<VisibleValueDeclarationDescriptor> declarations)
    {
        declarations = default;
        return _descriptorState.VisibleValueScopeDeclarationsByOwner.TryGetValue(syntaxTree, out var scopes) &&
               scopes.TryGetValue(key, out declarations);
    }


    private bool TryGetNodeInterestSymbolDescriptor(
        SyntaxTree syntaxTree,
        NodeInterestSymbolKey key,
        out NodeInterestSymbolDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.NodeInterestSymbolDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetNodeInterestSymbolDescriptor(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out NodeInterestSymbolDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.NodeInterestSymbolDescriptorsByOwner.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetContextualBindingRootDescriptor(
        SyntaxTree syntaxTree,
        ContextualBindingRootKey key,
        out ContextualBindingRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.ContextualBindingRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetContextualBindingRootDescriptor(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out ContextualBindingRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.ContextualBindingRootDescriptorsByOwner.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetInterestBindingRootDescriptor(
        SyntaxTree syntaxTree,
        InterestBindingRootKey key,
        out InterestBindingRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.InterestBindingRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetInterestBindingRootDescriptor(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out InterestBindingRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.InterestBindingRootDescriptorsByOwner.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetExecutableOwnerDescriptor(
        SyntaxTree syntaxTree,
        ExecutableOwnerKey key,
        out ExecutableOwnerDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.ExecutableOwnerDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetFunctionExpressionRebindRootDescriptor(
        SyntaxTree syntaxTree,
        FunctionExpressionRebindRootKey key,
        out FunctionExpressionRebindRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.FunctionExpressionRebindRootDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetFunctionExpressionRebindRootDescriptor(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out FunctionExpressionRebindRootDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.FunctionExpressionRebindRootDescriptorsByOwner.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetBinderParentAnchorDescriptor(
        SyntaxTree syntaxTree,
        BinderParentAnchorKey key,
        out BinderParentAnchorDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.BinderParentAnchorDescriptors.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private bool TryGetBinderParentAnchorDescriptor(
        SyntaxTree syntaxTree,
        OwnerRelativeDescriptorKey key,
        out BinderParentAnchorDescriptor descriptor)
    {
        descriptor = default;
        return _descriptorState.BinderParentAnchorDescriptorsByOwner.TryGetValue(syntaxTree, out var descriptors) &&
               descriptors.TryGetValue(key, out descriptor);
    }

    private static MetadataLoadContext CreateMetadataLoadContext(IEnumerable<string> paths, string? coreAssemblyName)
    {
        var pathByAssemblyIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            System.Reflection.AssemblyName assemblyIdentity;
            try
            {
                assemblyIdentity = ReadAssemblyName(fullPath);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(assemblyIdentity.FullName))
                continue;

            var identityKey = assemblyIdentity.FullName;

            if (!pathByAssemblyIdentity.TryGetValue(identityKey, out var existingPath))
            {
                pathByAssemblyIdentity[identityKey] = fullPath;
                continue;
            }

            // Keep the first path for an identity (typically reference assemblies from project metadata).
            // Preferring runtime assemblies here can hide reference-surface namespaces during binding.
        }

        var normalizedPaths = pathByAssemblyIdentity.Values
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolver = new StreamBackedPathAssemblyResolver(normalizedPaths);
        var resolvedCoreAssemblyName = string.IsNullOrWhiteSpace(coreAssemblyName) ? "System.Private.CoreLib" : coreAssemblyName;
        return new MetadataLoadContext(resolver, resolvedCoreAssemblyName);
    }

    internal static System.Reflection.AssemblyName ReadAssemblyName(string path)
    {
        if (!OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi())
            return System.Reflection.AssemblyName.GetAssemblyName(path);

        return ReadAssemblyNameFromMetadata(path);
    }

    internal static System.Reflection.AssemblyName ReadAssemblyNameFromMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new BadImageFormatException($"'{path}' does not contain managed metadata.");

        var metadataReader = peReader.GetMetadataReader();
        if (!metadataReader.IsAssembly)
            throw new BadImageFormatException($"'{path}' is not a managed assembly.");

        var definition = metadataReader.GetAssemblyDefinition();
        var assemblyName = new System.Reflection.AssemblyName
        {
            Name = metadataReader.GetString(definition.Name),
            Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : metadataReader.GetString(definition.Culture),
            Flags = (AssemblyNameFlags)definition.Flags,
        };

        if (!definition.PublicKey.IsNil)
            assemblyName.SetPublicKey(metadataReader.GetBlobBytes(definition.PublicKey));

        return assemblyName;
    }

    private sealed class StreamBackedPathAssemblyResolver : MetadataAssemblyResolver
    {
        private readonly Dictionary<string, string> _pathsByIdentity;
        private readonly Dictionary<string, string> _pathsBySimpleName;

        public StreamBackedPathAssemblyResolver(IEnumerable<string> paths)
        {
            _pathsByIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _pathsBySimpleName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                System.Reflection.AssemblyName identity;
                try
                {
                    identity = ReadAssemblyName(path);
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(identity.FullName))
                    _pathsByIdentity.TryAdd(identity.FullName, path);

                if (!string.IsNullOrWhiteSpace(identity.Name))
                {
                    _pathsBySimpleName.TryAdd(identity.Name, path);
                    s_globalAssemblyPathMap[identity.Name] = path;
                }
            }
        }

        public override Assembly? Resolve(MetadataLoadContext context, System.Reflection.AssemblyName assemblyName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyName.FullName) &&
                _pathsByIdentity.TryGetValue(assemblyName.FullName, out var path))
            {
                return LoadFromPath(context, path);
            }

            if (!string.IsNullOrWhiteSpace(assemblyName.Name) &&
                _pathsBySimpleName.TryGetValue(assemblyName.Name, out path))
            {
                return LoadFromPath(context, path);
            }

            return null;
        }

        private static Assembly LoadFromPath(MetadataLoadContext context, string path)
        {
            var bytes = File.ReadAllBytes(path);
            return context.LoadFromStream(new MemoryStream(bytes));
        }
    }

    private static bool IsReferenceAssemblyPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/packs/", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase);
    }

    internal ReflectionTypeLoader ReflectionTypeLoader => _reflectionTypeLoader ??= new ReflectionTypeLoader(this);

    private Assembly? ResolveEmitCoreAssembly()
    {
        var loadedSystemRuntime = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(static assembly =>
                string.Equals(assembly.GetName().Name, "System.Runtime", StringComparison.OrdinalIgnoreCase));
        if (loadedSystemRuntime is not null)
            return loadedSystemRuntime;

        if (_assemblyPathMap.TryGetValue("System.Runtime", out var systemRuntimePath))
        {
            try
            {
                var identity = ReadAssemblyName(systemRuntimePath);
                var loaded = LoadRuntimeAssemblyFromPath(identity, systemRuntimePath);
                if (loaded is not null)
                    return loaded;
            }
            catch
            {
            }
        }

        try
        {
            return System.Reflection.Assembly.Load("System.Runtime");
        }
        catch
        {
            return null;
        }
    }

    private void InitializeTopLevelPrograms()
    {
        var createdForBindableGlobals = false;

        foreach (var tree in SyntaxTrees)
        {
            if (tree is null)
                continue;

            if (tree.GetRoot() is not CompilationUnitSyntax compilationUnit)
                continue;

            var bindableGlobals = GetBindableGlobalStatements(compilationUnit);
            if (!bindableGlobals.Any())
                continue;

            if (HasTopLevelMainFunction(compilationUnit))
                continue;

            if (SyntaxTreeWithFileScopedCode is null)
                SyntaxTreeWithFileScopedCode = tree;

            var fileScopedNamespace = compilationUnit.Members
                .OfType<FileScopedNamespaceDeclarationSyntax>()
                .FirstOrDefault();

            SourceNamespaceSymbol targetNamespace = fileScopedNamespace is null
                ? SourceGlobalNamespace
                : GetOrCreateNamespaceSymbol(fileScopedNamespace.Name.ToString())?.AsSourceNamespace()
                    ?? SourceGlobalNamespace;

            GetOrCreateTopLevelProgram(compilationUnit, targetNamespace, bindableGlobals);
            createdForBindableGlobals = true;
        }

        if (createdForBindableGlobals)
            return;

        foreach (var tree in SyntaxTrees)
        {
            if (tree is null)
                continue;

            if (tree.GetRoot() is not CompilationUnitSyntax compilationUnit)
                continue;

            var bindableGlobals = GetBindableGlobalStatements(compilationUnit);
            if (bindableGlobals.Any() ||
                HasTopLevelMainFunction(compilationUnit) ||
                HasNonGlobalMembers(compilationUnit))
                continue;

            SyntaxTreeWithFileScopedCode ??= tree;
            GetOrCreateTopLevelProgram(compilationUnit, SourceGlobalNamespace, bindableGlobals);
            break;
        }

        static bool HasTopLevelMainFunction(CompilationUnitSyntax compilationUnit)
            => compilationUnit.DescendantNodes()
                .OfType<GlobalStatementSyntax>()
                .Any(static global =>
                    IsTopLevelFunctionMember(global) &&
                    global.Statement is FunctionStatementSyntax { Identifier.ValueText: "Main" });
    }

    internal (SynthesizedProgramClassSymbol Program, SynthesizedMainMethodSymbol Main, SynthesizedMainAsyncMethodSymbol? Async)
        GetOrCreateTopLevelProgram(
            CompilationUnitSyntax compilationUnit,
            SourceNamespaceSymbol targetNamespace,
            IReadOnlyList<GlobalStatementSyntax> bindableGlobals)
    {
        if (_topLevelProgramMembers.TryGetValue(compilationUnit.SyntaxTree, out var existing))
        {
            return (existing.ProgramClass, existing.MainMethod!, existing.AsyncMainMethod);
        }

        var returnsInt = bindableGlobals.Any(static g => ContainsNonUnitReturnOutsideNestedFunctions(g.Statement));
        var requiresAsync = bindableGlobals.Any(static g => ContainsAwaitExpressionOutsideNestedFunctions(g.Statement));
        var containsExecutableCode = HasRunnableFileScopeCode(bindableGlobals);

        var programClass = new SynthesizedProgramClassSymbol(this, targetNamespace, [compilationUnit.GetLocation()], [compilationUnit.GetReference()]);

        SynthesizedMainAsyncMethodSymbol? asyncImplementation = null;
        if (requiresAsync)
        {
            asyncImplementation = new SynthesizedMainAsyncMethodSymbol(
                programClass,
                [compilationUnit.GetLocation()],
                [compilationUnit.GetReference()],
                returnsInt);
        }

        var mainMethod = new SynthesizedMainMethodSymbol(
            programClass,
            [compilationUnit.GetLocation()],
            [compilationUnit.GetReference()],
            containsExecutableCode,
            returnsInt,
            asyncImplementation);

        var members = new TopLevelProgramMembers(programClass);
        members.MainMethod = mainMethod;
        members.AsyncMainMethod = asyncImplementation;
        _topLevelProgramMembers[compilationUnit.SyntaxTree] = members;

        return (programClass, mainMethod, asyncImplementation);
    }

    internal SynthesizedNamespaceMembersClassSymbol GetOrCreateNamespaceMembersContainer(
        SourceNamespaceSymbol targetNamespace,
        SyntaxNode declaration)
    {
        var key = targetNamespace.IsGlobalNamespace ? string.Empty : targetNamespace.ToMetadataName();

        if (_namespaceMembersContainers.TryGetValue(key, out var existing))
        {
            if (!existing.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == declaration.SyntaxTree &&
                    reference.Span == declaration.Span))
            {
                existing.AddDeclaration(declaration.GetLocation(), declaration.GetReference());
            }

            return existing;
        }

        var container = new SynthesizedNamespaceMembersClassSymbol(
            this,
            targetNamespace,
            [declaration.GetLocation()],
            [declaration.GetReference()]);

        _namespaceMembersContainers[key] = container;
        return container;
    }

    internal INamedTypeSymbol? GetNamespaceMembersContainer(INamespaceSymbol namespaceSymbol)
    {
        var sourceNamespace = namespaceSymbol.AsSourceNamespace();
        if (sourceNamespace is null)
            return null;

        var key = sourceNamespace.IsGlobalNamespace ? string.Empty : sourceNamespace.ToMetadataName();
        return _namespaceMembersContainers.TryGetValue(key, out var existing)
            ? existing
            : null;
    }

    internal ImmutableArray<ISymbol> GetNamespaceMembers(
        INamespaceSymbol namespaceSymbol,
        string name,
        bool includeNamespaceMemberImports = true)
    {
        if (!includeNamespaceMemberImports)
            return ImmutableArray<ISymbol>.Empty;

        var members = ImmutableArray.CreateBuilder<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in GetNamespaceMemberContainers(namespaceSymbol))
        {
            foreach (var member in container.GetMembers(name))
            {
                if (!IsPromotableTopLevelContainerMember(container, member))
                    continue;

                if (seen.Add(member.GetLookupIdentityKey()))
                    members.Add(member);
            }
        }

        return members.ToImmutable();
    }

    internal ImmutableArray<ISymbol> GetNamespaceMembers(
        INamespaceSymbol namespaceSymbol,
        bool includeNamespaceMemberImports = true)
    {
        if (!includeNamespaceMemberImports)
            return ImmutableArray<ISymbol>.Empty;

        var members = ImmutableArray.CreateBuilder<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in GetNamespaceMemberContainers(namespaceSymbol))
        {
            foreach (var member in container.GetMembers())
            {
                if (!IsPromotableTopLevelContainerMember(container, member))
                    continue;

                if (seen.Add(member.GetLookupIdentityKey()))
                    members.Add(member);
            }
        }

        return members.ToImmutable();
    }

    internal ImmutableArray<FunctionStatementSyntax> GetNamespaceFunctionDeclarations(INamespaceSymbol namespaceSymbol, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ImmutableArray<FunctionStatementSyntax>.Empty;

        return SourceDeclarationIndex.GetNamespaceFunctions(GetNamespaceMetadataName(namespaceSymbol), name);
    }

    internal void DeclareIndexedSourceNamespace(INamespaceSymbol? parent, string name)
    {
        var parentName = parent is null ? string.Empty : GetNamespaceMetadataName(parent);
        var metadataName = string.IsNullOrEmpty(parentName) ? name : parentName + "." + name;
        if (SourceDeclarationIndex.ContainsNamespace(metadataName))
            GetOrCreateNamespaceSymbol(metadataName);
    }

    internal bool TryDeclareIndexedSourceType(
        INamespaceSymbol? namespaceSymbol,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out INamedTypeSymbol? type)
    {
        type = null;
        var namespaceMetadataName = namespaceSymbol is null
            ? string.Empty
            : GetNamespaceMetadataName(namespaceSymbol);

        foreach (var declaration in SourceDeclarationIndex.GetNamespaceTypes(namespaceMetadataName, name))
        {
            if (!TryGetSemanticModelForDeclarationBinding(declaration.SyntaxTree, out var model) ||
                !model.TryDeclareAvailableSourceTypeSymbol(declaration, out var declaredType))
            {
                continue;
            }

            model.EnsureMemberSignaturesDeclared();
            type = declaredType;
            return true;
        }

        return false;
    }

    internal bool IsNamespaceMemberContainer(INamedTypeSymbol type)
    {
        if (type is SynthesizedNamespaceMembersClassSymbol)
            return true;

        if (HasTopLevelAttributeSyntax(type))
            return true;

        if (type is PENamedTypeSymbol peType &&
            peType.HasCustomAttribute(IsTopLevelAttributeMetadataName))
        {
            return true;
        }

        return false;
    }

    private IEnumerable<INamedTypeSymbol> GetNamespaceMemberContainers(INamespaceSymbol namespaceSymbol)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (GetNamespaceMembersContainer(namespaceSymbol) is { } synthesizedContainer &&
            seen.Add(synthesizedContainer))
        {
            yield return synthesizedContainer;
        }

        foreach (var type in namespaceSymbol.GetMembers().OfType<INamedTypeSymbol>())
        {
            if (seen.Add(type) && IsNamespaceMemberContainer(type))
                yield return type;
        }
    }

    private static bool IsPromotableTopLevelContainerMember(INamedTypeSymbol container, ISymbol member)
        => container is SynthesizedNamespaceMembersClassSymbol || member.IsStatic;

    private static string GetNamespaceMetadataName(INamespaceSymbol namespaceSymbol)
        => namespaceSymbol.IsGlobalNamespace ? string.Empty : namespaceSymbol.ToMetadataName();

    private static bool HasTopLevelAttributeSyntax(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not BaseTypeDeclarationSyntax typeDeclaration)
                continue;

            foreach (var attribute in typeDeclaration.AttributeLists.SelectMany(static list => list.Attributes))
            {
                if (IsTopLevelAttributeName(GetSimpleAttributeName(attribute.Name)))
                    return true;
            }
        }

        return false;
    }

    private static string GetSimpleAttributeName(NameSyntax name)
        => name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => GetSimpleAttributeName(qualified.Right),
            _ => name.ToString()
        };

    private static bool IsTopLevelAttribute(INamedTypeSymbol? attributeType)
    {
        if (attributeType is null)
            return false;

        if (IsTopLevelAttributeName(attributeType.Name) ||
            IsTopLevelAttributeName(attributeType.MetadataName))
        {
            return true;
        }

        var metadataName = attributeType.ToFullyQualifiedMetadataName();
        return metadataName.EndsWith(".TopLevelAttribute", StringComparison.Ordinal) ||
               metadataName.EndsWith(".TopLevel", StringComparison.Ordinal);
    }

    private static bool IsTopLevelAttributeName(string? name)
        => string.Equals(name, "TopLevel", StringComparison.Ordinal) ||
           string.Equals(name, "TopLevelAttribute", StringComparison.Ordinal);

    private static bool IsTopLevelAttributeMetadataName(string metadataName)
        => string.Equals(metadataName, "TopLevel", StringComparison.Ordinal) ||
           string.Equals(metadataName, "TopLevelAttribute", StringComparison.Ordinal) ||
           metadataName.EndsWith(".TopLevel", StringComparison.Ordinal) ||
           metadataName.EndsWith(".TopLevelAttribute", StringComparison.Ordinal);

    internal IReadOnlyList<GlobalStatementSyntax> GetBindableGlobalStatements(CompilationUnitSyntax compilationUnit)
    {
        var syntaxTree = compilationUnit.SyntaxTree;
        var globals = syntaxTree is null
            ? CollectBindableGlobalStatementsCore(compilationUnit)
            : _bindableGlobalStatementsCache.GetOrAdd(
                syntaxTree,
                static (_, root) => CollectBindableGlobalStatementsCore(root),
                compilationUnit);

        return globals
            .Where(global => !IsDeclarationOnlyMacroCarrier(global))
            .ToImmutableArray();
    }

    private bool IsDeclarationOnlyMacroCarrier(GlobalStatementSyntax global)
    {
        if (global.Statement is not ExpressionStatementSyntax
            {
                Expression: FreestandingMacroExpressionSyntax expression
            } ||
            !FreestandingMacroInvocation.TryCreate(expression, out var invocation) ||
            !invocation.TryGetMacroName(out var macroName) ||
            !GetMacroRegistry().TryResolveFreestandingMacro(
                this,
                expression,
                macroName,
                out var macro,
                out var isAmbiguous) ||
            isAmbiguous)
        {
            return false;
        }

        var targets = macro.Descriptor.InvocationTargets;
        return targets.HasFlag(MacroInvocationTargets.NamespaceMember) &&
            (targets & (MacroInvocationTargets.Expression | MacroInvocationTargets.Statement)) == 0;
    }

    internal bool HasRunnableFileScopeCode(CompilationUnitSyntax compilationUnit)
        => HasRunnableFileScopeCode(GetBindableGlobalStatements(compilationUnit));

    internal static bool IsBindableGlobalStatement(GlobalStatementSyntax globalStatement)
        => globalStatement.Parent is CompilationUnitSyntax or FileScopedNamespaceDeclarationSyntax
            && globalStatement.Statement is not FunctionStatementSyntax;

    internal static bool IsTopLevelFunctionMember(GlobalStatementSyntax globalStatement)
        => globalStatement.Parent is CompilationUnitSyntax or FileScopedNamespaceDeclarationSyntax or NamespaceDeclarationSyntax
            && globalStatement.Statement is FunctionStatementSyntax;

    internal bool IsFileScopeLocalFunction(GlobalStatementSyntax globalStatement)
        => !IsSubmission &&
           globalStatement.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault() is { } compilationUnit &&
           HasRunnableFileScopeCode(compilationUnit);

    private static bool HasRunnableFileScopeCode(IReadOnlyList<GlobalStatementSyntax> bindableGlobals)
    {
        var hasTopLevelMainFunction = false;
        var hasExecutableStatement = false;

        foreach (var global in bindableGlobals)
        {
            if (global.Statement is FunctionStatementSyntax { Identifier.ValueText: "Main" })
                hasTopLevelMainFunction = true;
            else if (global.Statement is not FunctionStatementSyntax)
                hasExecutableStatement = true;
        }

        return !hasTopLevelMainFunction && hasExecutableStatement;
    }

    private static ImmutableArray<GlobalStatementSyntax> CollectBindableGlobalStatementsCore(CompilationUnitSyntax compilationUnit)
    {
        var builder = ImmutableArray.CreateBuilder<GlobalStatementSyntax>();

        foreach (var global in compilationUnit.DescendantNodes().OfType<GlobalStatementSyntax>())
        {
            if (IsBindableGlobalStatement(global))
                builder.Add(global);
        }

        return builder.ToImmutable();
    }

    internal bool HasNonGlobalMembers(CompilationUnitSyntax compilationUnit)
    {
        var syntaxTree = compilationUnit.SyntaxTree;
        if (syntaxTree is null)
            return HasNonGlobalMembersCore(compilationUnit);

        return _hasNonGlobalMembersCache.GetOrAdd(
            syntaxTree,
            static (_, root) => HasNonGlobalMembersCore(root),
            compilationUnit);
    }

    private static bool HasNonGlobalMembersCore(CompilationUnitSyntax compilationUnit)
    {
        foreach (var member in compilationUnit.Members)
        {
            switch (member)
            {
                case GlobalStatementSyntax { Statement: FunctionStatementSyntax }:
                    return true;
                case GlobalStatementSyntax:
                    continue;
                case FileScopedNamespaceDeclarationSyntax fileScoped when ContainsOnlyGlobalStatements(fileScoped):
                    continue;
                default:
                    return true;
            }
        }

        return false;

        static bool ContainsOnlyGlobalStatements(FileScopedNamespaceDeclarationSyntax fileScoped)
        {
            foreach (var nested in fileScoped.Members)
            {
                if (nested is GlobalStatementSyntax { Statement: FunctionStatementSyntax })
                    return false;

                if (nested is not GlobalStatementSyntax)
                    return false;
            }

            return true;
        }
    }

    internal static bool ContainsAwaitExpressionOutsideNestedFunctions(StatementSyntax statement)
    {
        return ContainsAwaitExpressionOutsideNestedFunctions((SyntaxNode)statement);
    }

    internal static bool ContainsAwaitExpressionOutsideNestedFunctions(SyntaxNode node)
    {
        return ContainsAwaitExpressionOutsideNestedFunctionsCore(node);

        static bool ContainsAwaitExpressionOutsideNestedFunctionsCore(SyntaxNode current)
        {
            if (IsNestedFunctionBoundary(current))
                return false;

            if (current.Kind == SyntaxKind.AwaitExpression)
                return true;

            if (current is ForStatementSyntax forStatement &&
                forStatement.AwaitKeyword.Kind == SyntaxKind.AwaitKeyword)
            {
                return true;
            }

            foreach (var child in current.ChildNodes())
            {
                if (IsNestedFunctionBoundary(child))
                    continue;

                if (ContainsAwaitExpressionOutsideNestedFunctionsCore(child))
                    return true;
            }

            return false;
        }
    }

    internal static bool ContainsYieldOutsideNestedFunctions(SyntaxNode node)
    {
        return ContainsYieldOutsideNestedFunctionsCore(node);

        static bool ContainsYieldOutsideNestedFunctionsCore(SyntaxNode current)
        {
            if (IsNestedFunctionBoundary(current))
                return false;

            if (current is YieldStatementSyntax or YieldExpressionSyntax)
                return true;

            foreach (var child in current.ChildNodes())
            {
                if (IsNestedFunctionBoundary(child))
                    continue;

                if (ContainsYieldOutsideNestedFunctionsCore(child))
                    return true;
            }

            return false;
        }
    }

    internal static bool ContainsNonUnitReturnOutsideNestedFunctions(StatementSyntax statement)
    {
        return ContainsNonUnitReturnOutsideNestedFunctions((SyntaxNode)statement);

        static bool ContainsNonUnitReturnOutsideNestedFunctions(SyntaxNode node)
        {
            if (IsNestedFunctionBoundary(node))
                return false;

            if (node is ReturnStatementSyntax returnStatement)
            {
                if (returnStatement.Expression is null)
                    return false;

                // Explicit `return ()` keeps synthesized main as unit-returning.
                if (returnStatement.Expression is UnitExpressionSyntax)
                    return false;

                return true;
            }

            foreach (var child in node.ChildNodes())
            {
                if (IsNestedFunctionBoundary(child))
                    continue;

                if (ContainsNonUnitReturnOutsideNestedFunctions(child))
                    return true;
            }

            return false;
        }
    }

    private static bool IsNestedFunctionBoundary(SyntaxNode node)
        => node is FunctionStatementSyntax or FunctionExpressionSyntax;

    private sealed class TopLevelProgramMembers
    {
        public TopLevelProgramMembers(SynthesizedProgramClassSymbol programClass)
        {
            ProgramClass = programClass;
        }

        public SynthesizedProgramClassSymbol ProgramClass { get; }
        public SynthesizedMainMethodSymbol? MainMethod { get; set; }
        public SynthesizedMainAsyncMethodSymbol? AsyncMainMethod { get; set; }
    }

    private UnitTypeSymbol CreateUnitTypeSymbol()
    {
        var global = SourceGlobalNamespace;
        var system = global.LookupNamespace("System") as SourceNamespaceSymbol;

        if (system is null)
        {
            system = new SourceNamespaceSymbol((SourceModuleSymbol)Module, "System", Assembly, null, global, [], []);
            global.AddMember(system);
        }

        var unit = new UnitTypeSymbol(this, system);
        system.AddMember(unit);
        return unit;
    }

    internal readonly record struct VisibleValueScopeKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct VisibleValueDeclarationNodeDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct VisibleValueDeclarationDescriptor(string Name, int Start, VisibleValueDeclarationNodeDescriptor Declaration);
    internal readonly record struct VisibleValueDeclaration(string Name, int Start, SyntaxNode DeclarationNode);
    internal readonly record struct NodeInterestSymbolKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct NodeInterestSymbolDescriptor(TextSpan ReferencedSpan, SyntaxKind ReferencedKind);
    internal readonly record struct ContextualBindingRootKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct ContextualBindingRootDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct InterestBindingRootKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct InterestBindingRootDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct ExecutableOwnerKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct ExecutableOwnerDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct FunctionExpressionRebindRootKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct FunctionExpressionRebindRootDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct BinderParentAnchorKey(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct BinderParentAnchorDescriptor(TextSpan Span, SyntaxKind Kind);
    internal readonly record struct SemanticDiagnosticDescriptor(
        DiagnosticDescriptor Descriptor,
        int RelativeStart,
        int Length,
        DiagnosticSeverity Severity,
        bool IsSuppressed,
        ImmutableDictionary<string, string?> Properties,
        ImmutableArray<object?> MessageArgs);
    internal readonly record struct OwnerRelativeDescriptorKey(
        ExecutableOwnerDescriptor Owner,
        int RelativeStart,
        int Length,
        SyntaxKind Kind);
    internal readonly record struct MatchedExecutableOwner(
        SyntaxTree PreviousSyntaxTree,
        ExecutableOwnerDescriptor CurrentOwner,
        ExecutableOwnerDescriptor PreviousOwner);

    private static OwnerRelativeDescriptorKey CreateOwnerRelativeDescriptorKey(
        ExecutableOwnerDescriptor owner,
        SyntaxNode node,
        ExecutableOwnerDescriptor? ownerOverride = null)
    {
        var effectiveOwner = ownerOverride ?? owner;
        return new OwnerRelativeDescriptorKey(
            owner,
            node.Span.Start - effectiveOwner.Span.Start,
            node.Span.Length,
            node.Kind);
    }

    private static bool TryGetSyntaxOnlyExecutableOwnerDescriptor(
        SyntaxNode node,
        out ExecutableOwnerDescriptor descriptor)
    {
        var owner = node.AncestorsAndSelf().FirstOrDefault(static current =>
            current is FunctionExpressionSyntax
                or FunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or BaseConstructorDeclarationSyntax
                or ParameterlessConstructorDeclarationSyntax
                or AccessorDeclarationSyntax
                or PropertyDeclarationSyntax
                or EventDeclarationSyntax
                or GlobalStatementSyntax
                or CompilationUnitSyntax);

        if (owner is null)
        {
            descriptor = default;
            return false;
        }

        descriptor = new ExecutableOwnerDescriptor(owner.Span, owner.Kind);
        return true;
    }

    internal INamespaceSymbol? GetOrCreateNamespaceSymbol(string? ns)
    {
        // Internal binders bind source declarations. The merged global namespace is a
        // presentation/API view; returning it here makes source declaration lookup
        // depend on metadata namespace shape.
        if (ns is null)
            return SourceGlobalNamespace;

        var namespaceParts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (namespaceParts.Length == 0)
            return SourceGlobalNamespace;

        var currentSourceNamespace = SourceGlobalNamespace;

        foreach (var part in namespaceParts)
        {
            var nextSource = currentSourceNamespace
                .GetMembers(part)
                .OfType<SourceNamespaceSymbol>()
                .FirstOrDefault();

            if (nextSource is null)
            {
                nextSource = new SourceNamespaceSymbol(
                    part,
                    currentSourceNamespace,
                    currentSourceNamespace,
                    [],
                    []);

                currentSourceNamespace.AddMember(nextSource);
                InvalidateNamespaceSymbolLookupCache();
            }

            currentSourceNamespace = nextSource;
        }

        return currentSourceNamespace;
    }

    /*
        internal INamespaceSymbol? GetOrCreateNamespaceSymbol(string? ns)
        {
            if (ns is null)
                return GlobalNamespace;

            var namespaceParts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (namespaceParts.Length == 0)
                return SourceGlobalNamespace;

            var currentSourceNamespace = SourceGlobalNamespace;

            foreach (var part in namespaceParts)
            {
                var next = currentSourceNamespace
                    .GetMembers(part)
                    .OfType<SourceNamespaceSymbol>()
                    .FirstOrDefault();

                if (next is null)
                {
                    next = new SourceNamespaceSymbol(
                        part,
                        currentSourceNamespace,
                        currentSourceNamespace,
                        [],
                        []);

                    currentSourceNamespace.AddMember(next);
                }

                currentSourceNamespace = next;
            }

            return currentSourceNamespace;
        }*/

    private void AnalyzeMemberDeclaration(SyntaxTree syntaxTree, ISymbol declaringSymbol, MemberDeclarationSyntax memberDeclaration)
    {
        if (memberDeclaration is BaseNamespaceDeclarationSyntax namespaceDeclarationSyntax)
        {
            Location[] locations = [syntaxTree.GetLocation(namespaceDeclarationSyntax.Span)];

            SyntaxReference[] references = [namespaceDeclarationSyntax.GetReference()];

            var symbol = new SourceNamespaceSymbol(
                namespaceDeclarationSyntax.Name.ToString(), declaringSymbol, (INamespaceSymbol?)declaringSymbol,
                locations, references);

            foreach (var memberDeclaration2 in namespaceDeclarationSyntax.Members)
            {
                AnalyzeMemberDeclaration(syntaxTree, symbol, memberDeclaration2);
            }
        }
        else if (memberDeclaration is TypeDeclarationSyntax classDeclaration &&
                 classDeclaration is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
        {
            Location[] locations = [syntaxTree.GetLocation(classDeclaration.Span)];

            SyntaxReference[] references = [classDeclaration.GetReference()];

            var containingType = declaringSymbol as INamedTypeSymbol;
            var containingNamespace = declaringSymbol switch
            {
                INamespaceSymbol ns => ns,
                INamedTypeSymbol type => type.ContainingNamespace,
                _ => null
            };

            var declaredTypeKind = classDeclaration.Keyword.Kind == SyntaxKind.StructKeyword
                ? TypeKind.Struct
                : TypeKind.Class;

            INamedTypeSymbol baseTypeSymbol = declaredTypeKind == TypeKind.Struct
                ? GetSpecialType(SpecialType.System_ValueType)
                : GetSpecialType(SpecialType.System_Object);
            ImmutableArray<INamedTypeSymbol> interfaceList = ImmutableArray<INamedTypeSymbol>.Empty;
            var baseList = classDeclaration switch
            {
                ClassDeclarationSyntax concreteClass => concreteClass.BaseList,
                RecordDeclarationSyntax concreteRecord => concreteRecord.BaseList,
                StructDeclarationSyntax concreteStruct => concreteStruct.BaseList,
                _ => null
            };
            var typeParameterList = classDeclaration switch
            {
                ClassDeclarationSyntax concreteClass => concreteClass.TypeParameterList,
                RecordDeclarationSyntax concreteRecord => concreteRecord.TypeParameterList,
                StructDeclarationSyntax concreteStruct => concreteStruct.TypeParameterList,
                _ => null
            };
            var constraintClauses = classDeclaration switch
            {
                ClassDeclarationSyntax concreteClass => concreteClass.ConstraintClauses,
                RecordDeclarationSyntax concreteRecord => concreteRecord.ConstraintClauses,
                StructDeclarationSyntax concreteStruct => concreteStruct.ConstraintClauses,
                _ => SyntaxList<TypeParameterConstraintClauseSyntax>.Empty
            };

            if (baseList is not null)
            {
                var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
                foreach (var t in baseList.Types)
                {
                    if (ResolveSimpleType(t.Type, declaringSymbol) is INamedTypeSymbol resolved)
                    {
                        if (resolved.TypeKind == TypeKind.Interface)
                            builder.Add(resolved);
                        else
                            baseTypeSymbol = resolved;
                    }
                }

                if (builder.Count > 0)
                    interfaceList = builder.ToImmutable();
            }

            var isStatic = classDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var isAbstract = isStatic || classDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword);
            var isSealed = isStatic || (!classDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.OpenKeyword) && !isAbstract);
            var typeAccessibility = AccessibilityUtilities.DetermineAccessibility(
                classDeclaration.Modifiers,
                AccessibilityUtilities.GetDefaultTypeAccessibility(declaringSymbol));

            var symbol = new SourceNamedTypeSymbol(
                classDeclaration.Identifier.ValueText,
                baseTypeSymbol,
                declaredTypeKind,
                declaringSymbol,
                containingType,
                containingNamespace,
                locations,
                references,
                isSealed,
                isAbstract,
                isStatic,
                declaredAccessibility: typeAccessibility);

            InitializeTypeParameters(symbol, typeParameterList, constraintClauses, syntaxTree);

            if (!interfaceList.IsDefaultOrEmpty)
                symbol.SetInterfaces(interfaceList);

            foreach (var memberDeclaration2 in classDeclaration.Members)
            {
                AnalyzeMemberDeclaration(syntaxTree, symbol, memberDeclaration2);
            }

            static INamedTypeSymbol? ResolveSimpleType(TypeSyntax typeSyntax, ISymbol container)
            {
                if (typeSyntax is IdentifierNameSyntax id)
                {
                    return (container switch
                    {
                        INamespaceSymbol ns => ns.LookupType(id.Identifier.ValueText),
                        INamedTypeSymbol nt => nt.ContainingNamespace.LookupType(id.Identifier.ValueText),
                        _ => null
                    }) as INamedTypeSymbol;
                }

                return null;
            }
        }
        else if (memberDeclaration is InterfaceDeclarationSyntax interfaceDeclaration)
        {
            Location[] locations = [syntaxTree.GetLocation(interfaceDeclaration.Span)];

            SyntaxReference[] references = [interfaceDeclaration.GetReference()];

            var containingType = declaringSymbol as INamedTypeSymbol;
            var containingNamespace = declaringSymbol switch
            {
                INamespaceSymbol ns => ns,
                INamedTypeSymbol type => type.ContainingNamespace,
                _ => null
            };

            var symbol = new SourceNamedTypeSymbol(
                interfaceDeclaration.Identifier.ValueText,
                GetSpecialType(SpecialType.System_Object),
                TypeKind.Interface,
                declaringSymbol,
                containingType,
                containingNamespace,
                locations,
                references,
                true,
                isAbstract: true,
                declaredAccessibility: AccessibilityUtilities.DetermineAccessibility(
                    interfaceDeclaration.Modifiers,
                    AccessibilityUtilities.GetDefaultTypeAccessibility(declaringSymbol)));

            InitializeTypeParameters(symbol, interfaceDeclaration.TypeParameterList, interfaceDeclaration.ConstraintClauses, syntaxTree);

            foreach (var memberDeclaration2 in interfaceDeclaration.Members)
            {
                AnalyzeMemberDeclaration(syntaxTree, symbol, memberDeclaration2);
            }
        }
        else if (memberDeclaration is ExtensionDeclarationSyntax extensionDeclaration)
        {
            Location[] locations = [syntaxTree.GetLocation(extensionDeclaration.Span)];

            SyntaxReference[] references = [extensionDeclaration.GetReference()];

            var containingType = declaringSymbol as INamedTypeSymbol;
            var containingNamespace = declaringSymbol switch
            {
                INamespaceSymbol ns => ns,
                INamedTypeSymbol type => type.ContainingNamespace,
                _ => null
            };

            var baseTypeSymbol = GetSpecialType(SpecialType.System_Object);

            var extensionAccessibility = AccessibilityUtilities.DetermineAccessibility(
                extensionDeclaration.Modifiers,
                AccessibilityUtilities.GetDefaultTypeAccessibility(declaringSymbol));

            var symbol = new SourceNamedTypeSymbol(
                extensionDeclaration.Identifier.ValueText,
                baseTypeSymbol!,
                TypeKind.Class,
                declaringSymbol,
                containingType,
                containingNamespace,
                locations,
                references,
                isSealed: true,
                isAbstract: true,
                declaredAccessibility: extensionAccessibility);

            symbol.MarkAsExtensionContainer();

            InitializeTypeParameters(symbol, extensionDeclaration.TypeParameterList, extensionDeclaration.ConstraintClauses, syntaxTree);

            foreach (var memberDeclaration2 in extensionDeclaration.Members)
            {
                AnalyzeMemberDeclaration(syntaxTree, symbol, memberDeclaration2);
            }
        }
        else if (memberDeclaration is DelegateDeclarationSyntax delegateDeclaration)
        {
            Location[] locations = [syntaxTree.GetLocation(delegateDeclaration.Span)];
            SyntaxReference[] references = [delegateDeclaration.GetReference()];

            var containingType = declaringSymbol as INamedTypeSymbol;
            var containingNamespace = declaringSymbol switch
            {
                INamespaceSymbol ns => ns,
                INamedTypeSymbol type => type.ContainingNamespace,
                _ => null
            };

            var baseTypeSymbol = GetSpecialType(SpecialType.System_MulticastDelegate);

            var typeAccessibility = AccessibilityUtilities.DetermineAccessibility(
                delegateDeclaration.Modifiers,
                AccessibilityUtilities.GetDefaultTypeAccessibility(declaringSymbol));

            var symbol = new SourceNamedTypeSymbol(
                delegateDeclaration.Identifier.ValueText,
                baseTypeSymbol,
                TypeKind.Delegate,
                declaringSymbol,
                containingType,
                containingNamespace,
                locations,
                references,
                isSealed: true,
                isAbstract: true,
                isStatic: false,
                declaredAccessibility: typeAccessibility);

            InitializeTypeParameters(symbol, delegateDeclaration.TypeParameterList, delegateDeclaration.ConstraintClauses, syntaxTree);

            // .ctor(object, IntPtr)
            var ctorParameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>(2);
            ctorParameters.Add(new SourceParameterSymbol(
                "object",
                GetSpecialType(SpecialType.System_Object),
                symbol,
                symbol,
                symbol.ContainingNamespace,
                locations,
                references,
                RefKind.None));
            ctorParameters.Add(new SourceParameterSymbol(
                "method",
                GetSpecialType(SpecialType.System_IntPtr),
                symbol,
                symbol,
                symbol.ContainingNamespace,
                locations,
                references,
                RefKind.None));

            _ = new SourceMethodSymbol(
                ".ctor",
                GetSpecialType(SpecialType.System_Unit),
                ctorParameters.MoveToImmutable(),
                symbol,
                symbol,
                symbol.ContainingNamespace,
                locations,
                references,
                isStatic: false,
                methodKind: MethodKind.Constructor,
                declaredAccessibility: Accessibility.Public);

            // Invoke
            var returnType = delegateDeclaration.ReturnType is null
                ? GetSpecialType(SpecialType.System_Unit)
                : ResolveTypeSyntax(delegateDeclaration.ReturnType.Type, symbol);

            var invokeParameters = ImmutableArray.CreateBuilder<SourceParameterSymbol>(delegateDeclaration.ParameterList.Parameters.Count);
            foreach (var p in delegateDeclaration.ParameterList.Parameters)
            {
                var typeSyntax = p.TypeAnnotation!.Type;
                var refKind = ParameterSyntaxUtilities.GetRefKind(p);

                var boundTypeSyntax = refKind.IsByRef && typeSyntax is ByRefTypeSyntax byRefType
                    ? byRefType.ElementType
                    : typeSyntax;
                var pType = ResolveTypeSyntax(boundTypeSyntax, symbol);

                invokeParameters.Add(new SourceParameterSymbol(
                    p.Identifier.ValueText,
                    pType,
                    symbol,
                    symbol,
                    symbol.ContainingNamespace,
                    locations,
                    references,
                    refKind));
            }

            _ = new SourceMethodSymbol(
                "Invoke",
                returnType,
                invokeParameters.MoveToImmutable(),
                symbol,
                symbol,
                symbol.ContainingNamespace,
                locations,
                references,
                isStatic: false,
                declaredAccessibility: Accessibility.Public);
        }
        else if (memberDeclaration is MethodDeclarationSyntax methodDeclaration)
        {
            Location[] locations = [syntaxTree.GetLocation(methodDeclaration.Span)];

            SyntaxReference[] references = [methodDeclaration.GetReference()];

            var containingType = declaringSymbol as INamedTypeSymbol;
            var containingNamespace = declaringSymbol switch
            {
                INamespaceSymbol ns => ns,
                INamedTypeSymbol type => type.ContainingNamespace,
                _ => null
            };

            var returnType = GetSpecialType(SpecialType.System_Unit);
            var isStatic = methodDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var declaredInExtension = declaringSymbol is SourceNamedTypeSymbol { IsExtensionDeclaration: true };
            if (declaredInExtension)
                isStatic = true;
            var defaultAccessibility = containingType is not null
                ? Accessibility.Public
                : Accessibility.Internal;
            var methodAccessibility = AccessibilityUtilities.DetermineAccessibility(
                methodDeclaration.Modifiers,
                defaultAccessibility);

            var methodSymbol = new SourceMethodSymbol(
                methodDeclaration.Identifier.ValueText, returnType,
                ImmutableArray<SourceParameterSymbol>.Empty,
                declaringSymbol,
                containingType,
                containingNamespace,
                locations, references,
                isStatic: isStatic,
                declaredAccessibility: methodAccessibility);

            if (declaredInExtension)
                methodSymbol.MarkDeclaredInExtension();
        }
    }

    public ITypeSymbol CreateArrayTypeSymbol(ITypeSymbol elementType, int rank = 1, int? fixedLength = null)
    {
        var ns = SymbolLookup.GetNamespace("System");
        return new ArrayTypeSymbol(GetSpecialType(SpecialType.System_Array), elementType, ns, null, ns, [], rank, fixedLength);
    }

    public ITypeSymbol CreatePointerTypeSymbol(ITypeSymbol pointedAtType)
    {
        return new PointerTypeSymbol(pointedAtType);
    }
    public ITypeSymbol CreateFunctionTypeSymbol(ITypeSymbol[] parameterTypes, ITypeSymbol returnType)
    {
        var systemNamespace = SymbolLookup.GetNamespace("System");

        var allTypes = parameterTypes.ToList();
        bool isAction = returnType.SpecialType == SpecialType.System_Void || returnType.SpecialType == SpecialType.System_Unit;

        if (!isAction)
            allTypes.Add(returnType);

        string delegateName = isAction ? "Action" : "Func";
        INamedTypeSymbol? delegateType = systemNamespace?.GetMembers(delegateName)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => t.Arity == allTypes.Count);

        if (delegateType is null)
        {
            var metadataName = $"System.{delegateName}`{allTypes.Count}";
            delegateType = GetTypeByMetadataName(metadataName);
        }

        if (delegateType is not null)
            return delegateType.Construct(allTypes.ToArray());

        var parameterImmutable = parameterTypes.ToImmutableArray();
        var refKinds = ImmutableArray.CreateRange(Enumerable.Repeat(RefKind.None, parameterTypes.Length));
        return GetOrAddSynthesizedDelegate(parameterImmutable, refKinds, returnType);
    }

    public ITypeSymbol CreateTupleTypeSymbol(IEnumerable<(string? name, ITypeSymbol type)> elements)
    {
        var elementArray = elements.ToArray();
        var arity = elementArray.Length;
        if (arity == 0)
            return GetSpecialType(SpecialType.System_Unit);

        var tupleDefinition = SymbolLookup.GetTypeByMetadataNameMetadataOnly($"System.ValueTuple`{arity}")
            ?? GetTypeByMetadataName($"System.ValueTuple`{arity}");

        if (tupleDefinition is null)
            return ErrorTypeSymbol;

        var underlying = (INamedTypeSymbol)tupleDefinition.Construct(elementArray.Select(e => e.type).ToArray());
        var tuple = new TupleTypeSymbol(underlying, null, null, null, []);

        var fields = new List<IFieldSymbol>();
        int i = 0;
        foreach (var tupleField in underlying.GetMembers().OfType<SubstitutedFieldSymbol>())
        {
            var name = elementArray[i].name ?? $"Item{i + 1}";
            fields.Add(new TupleFieldSymbol(name, tupleField, underlying, []));
            i++;
        }

        tuple.SetTupleElements(fields);

        return tuple;
    }

    public ITypeSymbol ConstructGenericType(INamedTypeSymbol genericDefinition, ITypeSymbol[] typeArgs)
    {
        return genericDefinition.Construct(typeArgs);
    }

    private ITypeSymbol ResolveTypeSyntax(TypeSyntax typeSyntax, ISymbol container)
    {
        switch (typeSyntax)
        {
            case ByRefTypeSyntax refType:
                {
                    var elementType = ResolveTypeSyntax(refType.ElementType, container);
                    return new RefTypeSymbol(elementType);
                }

            case PredefinedTypeSyntax predefined:
                return ResolvePredefinedType(predefined);

            case IdentifierNameSyntax id:
                if (container is INamedTypeSymbol namedType)
                {
                    var typeParameter = namedType.TypeParameters.FirstOrDefault(tp => tp.Name == id.Identifier.ValueText);
                    if (typeParameter is not null)
                        return typeParameter;
                }

                return (container switch
                {
                    INamespaceSymbol ns => ns.LookupType(id.Identifier.ValueText),
                    INamedTypeSymbol nt => nt.ContainingNamespace.LookupType(id.Identifier.ValueText),
                    _ => null
                }) as ITypeSymbol ?? ErrorTypeSymbol;

            case QualifiedNameSyntax qn:
                {
                    // Resolve left as namespace, then right as type
                    var leftName = qn.Left.ToString();
                    string rightName = string.Empty;
                    if (qn.Right is IdentifierNameSyntax ifname)
                    {
                        rightName = ifname.Identifier.ValueText;
                    }

                    var ns = (container switch
                    {
                        INamespaceSymbol nns => nns,
                        INamedTypeSymbol nt => nt.ContainingNamespace,
                        _ => null
                    });

                    var resolvedNs = ns?.LookupNamespace(leftName);
                    if (resolvedNs is not null)
                        return resolvedNs.LookupType(rightName) as ITypeSymbol ?? ErrorTypeSymbol;

                    // Fallback: treat as metadata name
                    var metadata = qn.ToString();
                    return GetTypeByMetadataName(metadata) ?? ErrorTypeSymbol;
                }

            case GenericNameSyntax gn:
                {
                    var def = (container switch
                    {
                        INamespaceSymbol ns => ns.LookupType(gn.Identifier.ValueText),
                        INamedTypeSymbol nt => nt.ContainingNamespace.LookupType(gn.Identifier.ValueText),
                        _ => null
                    }) as INamedTypeSymbol;

                    if (def is null)
                        return ErrorTypeSymbol;

                    var args = gn.TypeArgumentList.Arguments
                        .Select(a => ResolveTypeSyntax(a.Type, container))
                        .ToArray();

                    return def.Construct(args);
                }

            default:
                return ErrorTypeSymbol;
        }
    }

    public ITypeSymbol ResolvePredefinedType(PredefinedTypeSyntax predefinedType)
    {
        var keywordKind = predefinedType.Keyword.Kind;

        var specialType = keywordKind switch
        {
            SyntaxKind.BoolKeyword => SpecialType.System_Boolean,
            SyntaxKind.CharKeyword => SpecialType.System_Char,
            SyntaxKind.SByteKeyword => SpecialType.System_SByte,
            SyntaxKind.ShortKeyword => SpecialType.System_Int16,
            SyntaxKind.UShortKeyword => SpecialType.System_UInt16,
            SyntaxKind.DoubleKeyword => SpecialType.System_Double,
            SyntaxKind.DecimalKeyword => SpecialType.System_Decimal,
            SyntaxKind.FloatKeyword => SpecialType.System_Single,
            SyntaxKind.IntKeyword => SpecialType.System_Int32,
            SyntaxKind.UIntKeyword => SpecialType.System_UInt32,
            SyntaxKind.LongKeyword => SpecialType.System_Int64,
            SyntaxKind.ULongKeyword => SpecialType.System_UInt64,
            SyntaxKind.NIntKeyword => SpecialType.System_IntPtr,
            SyntaxKind.NUIntKeyword => SpecialType.System_UIntPtr,
            SyntaxKind.ByteKeyword => SpecialType.System_Byte,
            SyntaxKind.ObjectKeyword => SpecialType.System_Object,
            SyntaxKind.StringKeyword => SpecialType.System_String,
            SyntaxKind.UnitKeyword => SpecialType.System_Unit,
            _ => throw new Exception($"Unexpected predefined keyword: {keywordKind}")
        };

        return GetSpecialType(specialType)
               ?? throw new Exception($"Special type not found for: {specialType}");
    }

    public ITypeSymbol? GetType(Type type)
    {
        return ReflectionTypeLoader.ResolveType(type);
    }

    public ISymbol? GetAssemblyOrModuleSymbol(MetadataReference metadataReference)
    {
        if (!_metadataReferenceSymbols.TryGetValue(metadataReference, out var symbol))
        {
            switch (metadataReference)
            {
                case PortableExecutableReference per:
                    {
                        Assembly assembly;
                        try
                        {
                            assembly = LoadMetadataAssembly(per.FilePath);
                        }
                        catch (BadImageFormatException)
                        {
                            // MSBuild reference sets can contain native PE files alongside
                            // managed reference assemblies (for example ASP.NET Core's
                            // Windows hosting module). They are not metadata references and
                            // must not prevent the remaining managed references from loading.
                            return null;
                        }

                        RegisterRuntimeAssembly(assembly, per.FilePath);
                        symbol = GetAssembly(assembly, per.FilePath);
                        break;
                    }
                case CompilationReference cr:
                    {
                        var compilation = cr.Compilation;
                        compilation.EnsureSetup();
                        compilation.EnsureSourceTypesInitialized();
                        symbol = compilation.Assembly;
                        break;
                    }
                default:
                    throw new InvalidOperationException();
            }

            _metadataReferenceSymbols[metadataReference] = (IAssemblySymbol)symbol!;
        }
        return symbol;
    }

    private Assembly LoadMetadataAssembly(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (_lazyMetadataAssemblies.TryGetValue(fullPath, out var cachedByPath))
            return cachedByPath;

        System.Reflection.AssemblyName? identity = null;
        try
        {
            identity = ReadAssemblyName(fullPath);
            if (identity.Name is not null)
            {
                _assemblyPathMap[identity.Name] = fullPath;
                s_globalAssemblyPathMap[identity.Name] = fullPath;
            }
        }
        catch
        {
            // Fall through and attempt to load by path directly.
        }

        Assembly assembly;
        try
        {
            assembly = LoadMetadataAssemblyFromPath(fullPath);
        }
        catch when (identity is not null)
        {
            // Some resolvers work better by identity; keep this as a compatibility fallback.
            assembly = _metadataLoadContext.LoadFromAssemblyName(identity);
        }

        _lazyMetadataAssemblies[fullPath] = assembly;

        return assembly;
    }

    private Assembly LoadMetadataAssemblyFromPath(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        return _metadataLoadContext.LoadFromStream(new MemoryStream(bytes));
    }

    private IAssemblySymbol GetAssembly(Assembly assembly, string? assemblyPathOverride = null)
    {
        RegisterRuntimeAssembly(assembly);

        if (_assemblySymbols.TryGetValue(assembly, out var asss))
        {
            if (asss is PEAssemblySymbol peAssembly)
                peAssembly.SetAssemblyPath(assemblyPathOverride);

            return asss;
        }

        string? assemblyPath = assemblyPathOverride;
        var identity = assembly.GetName();
        if (assemblyPath is null && identity.Name is not null)
            _assemblyPathMap.TryGetValue(identity.Name, out assemblyPath);
        PEAssemblySymbol assemblySymbol = new PEAssemblySymbol(assembly, [], assemblyPath);
        _assemblySymbols[assembly] = assemblySymbol;

        var refs = assembly.GetReferencedAssemblies();

        assemblySymbol.AddModules(
            new PEModuleSymbol(
                ReflectionTypeLoader,
                assemblySymbol,
                assembly.ManifestModule,
                [],
                refs.Select(x =>
                {
                    try
                    {
                        var loadedAssembly = _metadataLoadContext.LoadFromAssemblyName(x);
                        if (loadedAssembly is null)
                            return null;

                        RegisterRuntimeAssembly(loadedAssembly);
                        return GetAssembly(loadedAssembly);
                    }
                    catch
                    {
                        return null;
                    }
                }).OfType<IAssemblySymbol>()));

        return assemblySymbol;
    }

    private Assembly? RegisterRuntimeAssembly(Assembly metadataAssembly, string? explicitPath = null)
    {
        if (metadataAssembly is null)
            return null;

        EnsureTrustedPlatformAssembliesCached();

        var identity = metadataAssembly.GetName();
        if (!string.IsNullOrEmpty(explicitPath) && identity.Name is not null)
            _assemblyPathMap[identity.Name] = explicitPath;
        else if (identity.Name is { } identityName)
        {
            if (!_assemblyPathMap.ContainsKey(identityName) && s_globalAssemblyPathMap.TryGetValue(identityName, out var sharedPath))
                _assemblyPathMap.TryAdd(identityName, sharedPath);

            try
            {
                var metadataLocation = metadataAssembly.Location;
                if (!string.IsNullOrEmpty(metadataLocation) && !_assemblyPathMap.ContainsKey(identityName))
                {
                    _assemblyPathMap[identityName] = metadataLocation;
                }
            }
            catch (NotSupportedException)
            {
                // Dynamic assemblies can throw when querying Location.
            }
        }

        if (identity.Name is not null &&
            !_runtimeAssemblyCache.ContainsKey(identity.Name) &&
            s_globalRuntimeAssemblyCache.TryGetValue(identity.Name, out var sharedRuntimeAssembly))
        {
            _runtimeAssemblyCache.TryAdd(identity.Name, sharedRuntimeAssembly);
        }

        if (_metadataToRuntimeAssemblyMap.TryGetValue(metadataAssembly, out var cached))
            return cached;

        Assembly? runtimeAssembly = null;
        string? resolvedPath = null;

        if (identity.Name is not null)
        {
            var alreadyLoaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, identity.Name, StringComparison.OrdinalIgnoreCase));

            if (alreadyLoaded is not null)
            {
                runtimeAssembly = alreadyLoaded;
                if (!string.IsNullOrEmpty(alreadyLoaded.Location))
                    resolvedPath = alreadyLoaded.Location;
            }
        }

        if (identity.Name is not null && _runtimeAssemblyCache.TryGetValue(identity.Name, out var fromCache))
        {
            runtimeAssembly = fromCache;
            if (!string.IsNullOrEmpty(runtimeAssembly.Location))
                resolvedPath = runtimeAssembly.Location;
        }
        else if (identity.Name is not null && _assemblyPathMap.TryGetValue(identity.Name, out var knownPath))
        {
            var runtimePath = TryMapReferenceAssemblyToRuntimePath(knownPath) ?? knownPath;
            runtimeAssembly = LoadRuntimeAssemblyFromPath(identity, runtimePath);
            if (runtimeAssembly is not null)
            {
                resolvedPath = !string.IsNullOrEmpty(runtimeAssembly.Location)
                    ? runtimeAssembly.Location
                    : runtimePath;
            }
            else if (!string.Equals(runtimePath, knownPath, StringComparison.OrdinalIgnoreCase) &&
                     LoadRuntimeAssemblyFromPath(identity, knownPath) is { } metadataAssemblyRuntime)
            {
                runtimeAssembly = metadataAssemblyRuntime;
                resolvedPath = !string.IsNullOrEmpty(runtimeAssembly.Location)
                    ? runtimeAssembly.Location
                    : knownPath;
            }
        }

        if (runtimeAssembly is null && !string.IsNullOrEmpty(explicitPath))
        {
            var runtimePath = TryMapReferenceAssemblyToRuntimePath(explicitPath) ?? explicitPath;
            runtimeAssembly = LoadRuntimeAssemblyFromPath(identity, runtimePath);
            if (runtimeAssembly is not null)
            {
                resolvedPath = !string.IsNullOrEmpty(runtimeAssembly.Location)
                    ? runtimeAssembly.Location
                    : runtimePath;
            }
            else if (!string.Equals(runtimePath, explicitPath, StringComparison.OrdinalIgnoreCase) &&
                     LoadRuntimeAssemblyFromPath(identity, explicitPath) is { } metadataAssemblyRuntime)
            {
                runtimeAssembly = metadataAssemblyRuntime;
                resolvedPath = !string.IsNullOrEmpty(runtimeAssembly.Location)
                    ? runtimeAssembly.Location
                    : explicitPath;
            }
        }

        if (runtimeAssembly is null)
        {
            runtimeAssembly = LoadRuntimeAssemblyByName(identity);
            if (runtimeAssembly is not null && !string.IsNullOrEmpty(runtimeAssembly.Location))
                resolvedPath = runtimeAssembly.Location;
        }

        if (runtimeAssembly is null)
        {
            runtimeAssembly = MapToRuntimeImplementation(identity);
            if (runtimeAssembly is not null && !string.IsNullOrEmpty(runtimeAssembly.Location))
                resolvedPath = runtimeAssembly.Location;
        }

        if (runtimeAssembly is not null)
        {
            if (identity.Name is not null)
            {
                _runtimeAssemblyCache[identity.Name] = runtimeAssembly;

                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    _assemblyPathMap[identity.Name] = resolvedPath;
                    s_globalAssemblyPathMap[identity.Name] = resolvedPath;
                }
                else if (!string.IsNullOrEmpty(runtimeAssembly.Location))
                {
                    _assemblyPathMap[identity.Name] = runtimeAssembly.Location;
                    s_globalAssemblyPathMap[identity.Name] = runtimeAssembly.Location;
                }

                s_globalRuntimeAssemblyCache[identity.Name] = runtimeAssembly;
            }

            _metadataToRuntimeAssemblyMap[metadataAssembly] = runtimeAssembly;
        }

        return runtimeAssembly;
    }

    private void EnsureTrustedPlatformAssembliesCached()
    {
        if (_trustedPlatformAssembliesCached)
            return;

        if (Interlocked.CompareExchange(ref s_trustedPlatformAssembliesInitialized, 1, 0) == 0)
        {
            var platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(platformAssemblies))
            {
                var candidates = platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

                foreach (var candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                        continue;

                    System.Reflection.AssemblyName? candidateIdentity;
                    try
                    {
                        candidateIdentity = ReadAssemblyName(candidate);
                    }
                    catch
                    {
                        continue;
                    }

                    if (candidateIdentity.Name is not { Length: > 0 } candidateName)
                        continue;

                    s_globalAssemblyPathMap.TryAdd(candidateName, candidate);

                    if (s_globalRuntimeAssemblyCache.ContainsKey(candidateName))
                        continue;

                    var alreadyLoaded = AppDomain.CurrentDomain
                        .GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, candidateName, StringComparison.OrdinalIgnoreCase));

                    if (alreadyLoaded is not null)
                        s_globalRuntimeAssemblyCache.TryAdd(candidateName, alreadyLoaded);
                }
            }
        }

        foreach (var (assemblyName, path) in s_globalAssemblyPathMap)
            _assemblyPathMap.TryAdd(assemblyName, path);

        foreach (var (assemblyName, runtimeAssembly) in s_globalRuntimeAssemblyCache)
            _runtimeAssemblyCache.TryAdd(assemblyName, runtimeAssembly);

        _trustedPlatformAssembliesCached = true;
    }

    private static string? TryMapReferenceAssemblyToRuntimePath(string? metadataAssemblyPath)
    {
        if (string.IsNullOrEmpty(metadataAssemblyPath))
            return null;

        if (TryMapNuGetReferenceAssemblyToRuntimePath(metadataAssemblyPath) is { } nuGetRuntimePath)
            return nuGetRuntimePath;

        try
        {
            var assemblyFileName = Path.GetFileName(metadataAssemblyPath);
            if (string.IsNullOrEmpty(assemblyFileName))
                return null;

            var cursor = Path.GetDirectoryName(metadataAssemblyPath);
            if (cursor is null)
                return null;

            while (cursor is not null && !string.Equals(Path.GetFileName(cursor), "ref", StringComparison.OrdinalIgnoreCase))
            {
                cursor = Path.GetDirectoryName(cursor);
            }

            if (cursor is null)
                return null;

            var versionDirectory = Path.GetDirectoryName(cursor);
            if (versionDirectory is null)
                return null;

            var version = Path.GetFileName(versionDirectory);
            if (string.IsNullOrEmpty(version))
                return null;

            var packDirectory = Path.GetDirectoryName(versionDirectory);
            if (packDirectory is null)
                return null;

            var packId = Path.GetFileName(packDirectory);
            if (string.IsNullOrEmpty(packId))
                return null;

            var packsRoot = Path.GetDirectoryName(packDirectory);
            if (packsRoot is null || !string.Equals(Path.GetFileName(packsRoot), "packs", StringComparison.OrdinalIgnoreCase))
                return null;

            var dotnetRoot = Path.GetDirectoryName(packsRoot);
            if (string.IsNullOrEmpty(dotnetRoot))
                return null;

            var runtimePackId = packId.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase)
                ? packId[..^4]
                : packId;

            var runtimeDirectory = Path.Combine(dotnetRoot, "shared", runtimePackId, version);
            var candidatePath = Path.Combine(runtimeDirectory, assemblyFileName);

            return File.Exists(candidatePath) ? candidatePath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryMapNuGetReferenceAssemblyToRuntimePath(string metadataAssemblyPath)
    {
        try
        {
            var normalized = metadataAssemblyPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var refSegment = $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}";
            var refIndex = normalized.IndexOf(refSegment, StringComparison.OrdinalIgnoreCase);
            if (refIndex < 0)
                return null;

            if (TryMapNuGetSharedFrameworkReferenceAssemblyToRuntimePath(normalized, refIndex) is { } sharedFrameworkRuntimePath)
                return sharedFrameworkRuntimePath;

            var libCandidate = normalized[..refIndex] +
                               $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}" +
                               normalized[(refIndex + refSegment.Length)..];
            if (File.Exists(libCandidate))
                return libCandidate;

            var packageRoot = normalized[..refIndex];
            var libRoot = Path.Combine(packageRoot, "lib");
            if (!Directory.Exists(libRoot))
                return null;

            var assemblyFileName = Path.GetFileName(metadataAssemblyPath);
            if (string.IsNullOrWhiteSpace(assemblyFileName))
                return null;

            return Directory
                .EnumerateFiles(libRoot, assemblyFileName, SearchOption.AllDirectories)
                .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryMapNuGetSharedFrameworkReferenceAssemblyToRuntimePath(string normalizedMetadataAssemblyPath, int refIndex)
    {
        var packageVersionDirectory = normalizedMetadataAssemblyPath[..refIndex];
        var packageDirectory = Path.GetDirectoryName(packageVersionDirectory);
        if (packageDirectory is null)
            return null;

        var packageId = Path.GetFileName(packageDirectory);
        var sharedFrameworkName = packageId.ToLowerInvariant() switch
        {
            "microsoft.aspnetcore.app.ref" => "Microsoft.AspNetCore.App",
            "microsoft.netcore.app.ref" => "Microsoft.NETCore.App",
            _ => null
        };

        if (sharedFrameworkName is null)
            return null;

        var requestedVersion = Path.GetFileName(packageVersionDirectory);
        var assemblyFileName = Path.GetFileName(normalizedMetadataAssemblyPath);
        if (string.IsNullOrWhiteSpace(requestedVersion) || string.IsNullOrWhiteSpace(assemblyFileName))
            return null;

        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var sharedRoot = runtimeDirectory is not null
            ? Directory.GetParent(runtimeDirectory)?.Parent?.FullName
            : null;
        if (string.IsNullOrEmpty(sharedRoot))
            return null;

        var frameworkRoot = Path.Combine(sharedRoot, sharedFrameworkName);
        if (!Directory.Exists(frameworkRoot))
            return null;

        var exact = Path.Combine(frameworkRoot, requestedVersion, assemblyFileName);
        if (File.Exists(exact))
            return exact;

        var requestedMajor = TryGetMajorVersion(requestedVersion);
        var candidate = Directory
            .EnumerateDirectories(frameworkRoot)
            .Select(path => new
            {
                Path = path,
                Version = Path.GetFileName(path),
                Major = TryGetMajorVersion(Path.GetFileName(path)),
                NumericVersion = TryGetNumericVersion(Path.GetFileName(path)),
                IsPrerelease = Path.GetFileName(path).Contains('-', StringComparison.Ordinal)
            })
            .Where(x => requestedMajor is null || x.Major == requestedMajor)
            .OrderBy(x => x.IsPrerelease)
            .ThenByDescending(x => x.NumericVersion)
            .ThenByDescending(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .Select(x => Path.Combine(x.Path, assemblyFileName))
            .FirstOrDefault(File.Exists);

        return candidate;
    }

    private static int? TryGetMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var dotIndex = version.IndexOf('.');
        var majorText = dotIndex >= 0 ? version[..dotIndex] : version;
        return int.TryParse(majorText, out var major) ? major : null;
    }

    private static Version? TryGetNumericVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var prereleaseIndex = version.IndexOf('-');
        var versionText = prereleaseIndex >= 0 ? version[..prereleaseIndex] : version;
        return Version.TryParse(versionText, out var parsed) ? parsed : null;
    }

    private Assembly? MapToRuntimeImplementation(AssemblyName identity)
    {
        if (identity.Name is null)
            return null;

        var runtimeCoreIdentity = RuntimeCoreAssembly.GetName();

        if (string.Equals(identity.Name, runtimeCoreIdentity.Name, StringComparison.OrdinalIgnoreCase))
            return RuntimeCoreAssembly;

        if (string.Equals(identity.Name, "System.Runtime", StringComparison.OrdinalIgnoreCase))
            return RuntimeCoreAssembly;

        if (string.Equals(identity.Name, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase))
            return RuntimeCoreAssembly;

        return null;
    }

    private static Assembly? LoadRuntimeAssemblyFromPath(AssemblyName identity, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (FileLoadException)
        {
            return LoadRuntimeAssemblyByName(identity);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return LoadRuntimeAssemblyByName(identity);
        }
    }

    private static Assembly? LoadRuntimeAssemblyByName(AssemblyName identity)
    {
        try
        {
            return System.Reflection.Assembly.Load(identity);
        }
        catch
        {
            return null;
        }
    }

    internal Type? ResolveRuntimeType(PENamedTypeSymbol symbol)
    {
        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));

        EnsureSetup();

        var metadataName = ((INamedTypeSymbol)symbol).ToFullyQualifiedMetadataName();

        if (string.IsNullOrEmpty(metadataName))
            return null;

        if (symbol.ContainingAssembly is PEAssemblySymbol peAssembly &&
            RegisterRuntimeAssembly(peAssembly.GetAssemblyInfo()) is { } containingRuntimeAssembly &&
            GetTypeSafe(containingRuntimeAssembly, metadataName) is { } containingRuntimeType)
        {
            return containingRuntimeType;
        }

        var resolved = ResolveRuntimeType(metadataName);
        if (resolved is not null)
            return resolved;

        if (symbol.ContainingAssembly is PEAssemblySymbol peAssembly2)
        {
            if (!string.IsNullOrEmpty(peAssembly2.FullName))
            {
                var qualifiedName = $"{metadataName}, {peAssembly2.FullName}";
                var qualifiedType = GetTypeSafe(qualifiedName);
                if (qualifiedType is not null)
                {
                    RegisterRuntimeAssembly(qualifiedType.Assembly);
                    return qualifiedType;
                }
            }

            if (!string.IsNullOrEmpty(peAssembly2.Name))
            {
                try
                {
                    var assembly = System.Reflection.Assembly.Load(new AssemblyName(peAssembly2.Name));
                    RegisterRuntimeAssembly(assembly);
                    var type = GetTypeSafe(assembly, metadataName);
                    if (type is not null)
                        return type;
                }
                catch
                {
                    // Ignore load failures and fall through to null.
                }
            }
        }

        return null;
    }

    internal Type? ResolveRuntimeType(System.Reflection.TypeInfo metadataType)
    {
        if (metadataType is null)
            throw new ArgumentNullException(nameof(metadataType));

        EnsureSetup();

        RegisterRuntimeAssembly(metadataType.Assembly);

        if (metadataType.FullName is { Length: > 0 } fullName)
        {
            var resolved = ResolveRuntimeType(fullName);
            if (resolved is not null)
                return resolved;
        }

        if (metadataType.AssemblyQualifiedName is { Length: > 0 } qualifiedName)
            return GetTypeSafe(qualifiedName);

        return null;
    }

    internal Type? ResolveRuntimeType(string metadataName)
    {
        if (metadataName is null)
            throw new ArgumentNullException(nameof(metadataName));

        EnsureSetup();

        if (GetTypeSafe(RuntimeCoreAssembly, metadataName) is { } coreType)
            return coreType;

        foreach (var (key, runtimeAssembly) in _runtimeAssemblyCache)
        {
            var candidate = GetTypeSafe(runtimeAssembly, metadataName);
            if (candidate is not null)
                return candidate;
        }

        return GetTypeSafe(metadataName);
    }

    private static Type? GetTypeSafe(Assembly assembly, string metadataName)
    {
        try
        {
            return assembly.GetType(metadataName, throwOnError: false, ignoreCase: false);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ReflectionTypeLoadException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    private static Type? GetTypeSafe(string assemblyQualifiedOrMetadataName)
    {
        try
        {
            return Type.GetType(assemblyQualifiedOrMetadataName, throwOnError: false);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ReflectionTypeLoadException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    internal INamespaceSymbol? GetNamespaceSymbolCached(string? ns)
    {
        EnsureSetup();

        if (ns is null)
            return GlobalNamespace;

        var cacheKey = ns;
        if (_namespaceSymbolCache.TryGetValue(cacheKey, out var cached))
            return ReferenceEquals(cached, s_missingMetadataType) ? null : (INamespaceSymbol)cached;

        var resolved = GetNamespaceSymbolUncached(ns);
        if (resolved is null && !_sourceDeclarationsDeclared)
            return null;

        _namespaceSymbolCache.TryAdd(cacheKey, resolved ?? s_missingMetadataType);
        return resolved;
    }

    private void InvalidateNamespaceSymbolLookupCache()
    {
        _namespaceSymbolCache.Clear();
    }

    private INamespaceSymbol? GetNamespaceSymbolUncached(string? ns)
    {
        if (ns is null)
            return GlobalNamespace;

        var namespaceParts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (namespaceParts.Length == 0)
            return GlobalNamespace;

        var fromSource = TryResolve(GlobalNamespace, namespaceParts);
        if (fromSource is not null)
            return fromSource;

        foreach (var referencedAssembly in ReferencedAssemblySymbols)
        {
            var candidate = TryResolve(referencedAssembly.GlobalNamespace, namespaceParts);
            if (candidate is not null)
                return candidate;
        }

        return null;

        static INamespaceSymbol? TryResolve(INamespaceSymbol root, string[] parts)
        {
            var current = root;
            foreach (var part in parts)
            {
                current = current.LookupNamespace(part)
                    ?? current.GetMembers(part)
                        .OfType<INamespaceSymbol>()
                        .FirstOrDefault();

                if (current is null)
                    return null;
            }

            return current;
        }
    }

    public INamedTypeSymbol? GetTypeByMetadataName(string metadataName)
    {
        EnsureSetup();

        if (_metadataTypeCache.TryGetValue(metadataName, out var cached))
            return ReferenceEquals(cached, s_missingMetadataType) ? null : (INamedTypeSymbol)cached;

        if (!_isDeclaringSourceTypes)
        {
            EnsureSourceTypesInitialized();

            if (Assembly.GetTypeByMetadataName(metadataName) is { } sourceType)
            {
                _metadataTypeCache.TryAdd(metadataName, sourceType);
                return sourceType;
            }
        }

        if (_macroSyntaxTrees.Length > 0)
        {
            EnsureMacroSignatureCompilation();
            if (_macroSignatureCompilation?.GetTypeByMetadataName(metadataName) is { } macroType)
            {
                _metadataTypeCache.TryAdd(metadataName, macroType);
                return macroType;
            }
        }

        var metadataType = TryGetMetadataReferenceTypeByMetadataName(metadataName);
        if (metadataType is not null)
        {
            _metadataTypeCache.TryAdd(metadataName, metadataType);
            return metadataType;
        }

        _metadataTypeCache.TryAdd(metadataName, s_missingMetadataType);
        return null;
    }

    internal INamedTypeSymbol? GetTypeByMetadataName(INamespaceSymbol currentNamespace, string metadataName)
    {
        EnsureSetup();

        var namespaceName = currentNamespace.ToMetadataName() ?? string.Empty;
        var cacheKey = namespaceName + "\0" + metadataName;
        if (_scopedMetadataTypeCache.TryGetValue(cacheKey, out var cached))
            return ReferenceEquals(cached, s_missingMetadataType) ? null : (INamedTypeSymbol)cached;

        var qualifiedMetadataName = currentNamespace.QualifyName(metadataName);
        var resolved = GetTypeByMetadataName(qualifiedMetadataName) ?? GetTypeByMetadataName(metadataName);
        _scopedMetadataTypeCache.TryAdd(cacheKey, resolved ?? s_missingMetadataType);
        return resolved;
    }

    internal INamedTypeSymbol? TryGetMetadataReferenceTypeByMetadataName(INamespaceSymbol currentNamespace, string metadataName)
    {
        EnsureSetup();

        var namespaceName = currentNamespace.ToMetadataName() ?? string.Empty;
        var qualifiedMetadataName = currentNamespace.QualifyName(metadataName);
        return TryGetMetadataReferenceTypeByMetadataName(qualifiedMetadataName)
            ?? TryGetMetadataReferenceTypeByMetadataName(metadataName);
    }

    internal INamedTypeSymbol? TryGetMetadataReferenceTypeByMetadataName(string metadataName)
    {
        EnsureSetup();

        if (_metadataReferenceTypeCache.TryGetValue(metadataName, out var cached))
            return ReferenceEquals(cached, s_missingMetadataType) ? null : (INamedTypeSymbol)cached;

        var resolved = GetMetadataReferenceTypeByMetadataNameUncached(metadataName);
        _metadataReferenceTypeCache.TryAdd(metadataName, resolved ?? s_missingMetadataType);
        return resolved;
    }

    private INamedTypeSymbol? GetMetadataReferenceTypeByMetadataNameUncached(string metadataName)
    {
        INamedTypeSymbol? bestMatch = null;

        foreach (var assembly in GetMetadataReferenceSymbolsForLookup(metadataName))
        {
            var type = assembly.GetTypeByMetadataName(metadataName);
            if (type is null)
                continue;

            if (bestMatch is null)
            {
                bestMatch = type;
                if (IsStrongMetadataAssemblyMatch(metadataName, assembly.Name))
                    break;

                continue;
            }
        }

        return bestMatch;
    }

    private IEnumerable<IAssemblySymbol> GetMetadataReferenceSymbolsForLookup(string metadataName)
        => _metadataReferenceSymbols.Values
            .OrderByDescending(assembly => GetMetadataAssemblyAffinity(metadataName, assembly.Name))
            .ThenBy(assembly => assembly.Name, StringComparer.Ordinal);

    private static int GetMetadataAssemblyAffinity(string metadataName, string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
            return 0;

        if (IsStrongMetadataAssemblyMatch(metadataName, assemblyName))
            return 1_000 + assemblyName.Length;

        var metadataParts = metadataName.Split('.');
        var assemblyParts = assemblyName.Split('.');
        var score = 0;
        for (var i = 0; i < metadataParts.Length && i < assemblyParts.Length; i++)
        {
            if (!string.Equals(metadataParts[i], assemblyParts[i], StringComparison.Ordinal))
                break;

            score += 10;
        }

        return score;
    }

    private static bool IsStrongMetadataAssemblyMatch(string metadataName, string? assemblyName)
        => !string.IsNullOrWhiteSpace(assemblyName) &&
           metadataName.Length > assemblyName.Length &&
           metadataName.StartsWith(assemblyName, StringComparison.Ordinal) &&
           metadataName[assemblyName.Length] == '.';

    private void EnsureSourceTypesInitialized()
    {
        EnsureSourceDeclarationsDeclared();
    }

    private INamedTypeSymbol? GetTypeByMetadataName(string metadataName, string preferredAssembly)
    {
        EnsureSetup();

        var cacheKey = preferredAssembly + "\0" + metadataName;
        if (_preferredMetadataTypeCache.TryGetValue(cacheKey, out var cached))
            return ReferenceEquals(cached, s_missingMetadataType) ? null : (INamedTypeSymbol)cached;

        INamedTypeSymbol? resolved = null;

        foreach (var assembly in _metadataReferenceSymbols.Values)
        {
            if (!string.Equals(assembly.Name, preferredAssembly, StringComparison.OrdinalIgnoreCase))
                continue;

            var type = assembly.GetTypeByMetadataName(metadataName);
            if (type is not null)
            {
                resolved = type;
                break;
            }
        }

        _preferredMetadataTypeCache.TryAdd(cacheKey, resolved ?? s_missingMetadataType);
        return resolved;
    }

    public INamedTypeSymbol GetSpecialType(SpecialType specialType)
    {
        if (specialType is SpecialType.System_Unit)
            return UnitTypeSymbol;

        return _specialTypeCache.GetOrAdd(specialType, ResolveSpecialType);
    }

    private INamedTypeSymbol ResolveSpecialType(SpecialType specialType)
    {
        var metadataName = specialType switch
        {
            SpecialType.System_Object => "System.Object",
            SpecialType.System_Enum => "System.Enum",
            SpecialType.System_MulticastDelegate => "System.MulticastDelegate",
            SpecialType.System_Delegate => "System.Delegate",
            SpecialType.System_ValueType => "System.ValueType",
            SpecialType.System_Void => "System.Void",
            SpecialType.System_Boolean => "System.Boolean",
            SpecialType.System_Char => "System.Char",
            SpecialType.System_SByte => "System.SByte",
            SpecialType.System_Byte => "System.Byte",
            SpecialType.System_Int16 => "System.Int16",
            SpecialType.System_UInt16 => "System.UInt16",
            SpecialType.System_Int32 => "System.Int32",
            SpecialType.System_UInt32 => "System.UInt32",
            SpecialType.System_Int64 => "System.Int64",
            SpecialType.System_UInt64 => "System.UInt64",
            SpecialType.System_Decimal => "System.Decimal",
            SpecialType.System_Single => "System.Single",
            SpecialType.System_Double => "System.Double",
            SpecialType.System_String => "System.String",
            SpecialType.System_IntPtr => "System.IntPtr",
            SpecialType.System_UIntPtr => "System.UIntPtr",
            SpecialType.System_Array => "System.Array",
            SpecialType.System_Collections_IEnumerable => "System.Collections.IEnumerable",
            SpecialType.System_Collections_Generic_IEnumerable_T => "System.Collections.Generic.IEnumerable`1",
            SpecialType.System_Collections_Generic_IList_T => "System.Collections.Generic.IList`1",
            SpecialType.System_Collections_Generic_ICollection_T => "System.Collections.Generic.ICollection`1",
            SpecialType.System_Collections_IEnumerator => "System.Collections.IEnumerator",
            SpecialType.System_Collections_Generic_IEnumerator_T => "System.Collections.Generic.IEnumerator`1",
            SpecialType.System_Nullable_T => "System.Nullable",
            SpecialType.System_DateTime => "System.DateTime",
            SpecialType.System_Runtime_CompilerServices_IsVolatile => "System.Runtime.CompilerServices.IsVolatile",
            SpecialType.System_IDisposable => "System.IDisposable",
            SpecialType.System_TypedReference => "System.TypedReference",
            SpecialType.System_ArgIterator => "System.ArgIterator",
            SpecialType.System_RuntimeArgumentHandle => "System.RuntimeArgumentHandle",
            SpecialType.System_RuntimeFieldHandle => "System.RuntimeFieldHandle",
            SpecialType.System_RuntimeMethodHandle => "System.RuntimeMethodHandle",
            SpecialType.System_RuntimeTypeHandle => "System.RuntimeTypeHandle",
            SpecialType.System_IAsyncResult => "System.IAsyncResult",
            SpecialType.System_AsyncCallback => "System.AsyncCallback",
            SpecialType.System_Runtime_CompilerServices_AsyncVoidMethodBuilder => "System.Runtime.CompilerServices.AsyncVoidMethodBuilder",
            SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder => "System.Runtime.CompilerServices.AsyncTaskMethodBuilder",
            SpecialType.System_Runtime_CompilerServices_AsyncTaskMethodBuilder_T => "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1",
            SpecialType.System_Runtime_CompilerServices_AsyncStateMachineAttribute => "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
            SpecialType.System_Runtime_CompilerServices_IteratorStateMachineAttribute => "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
            SpecialType.System_Threading_Tasks_Task => "System.Threading.Tasks.Task",
            SpecialType.System_Threading_Tasks_Task_T => "System.Threading.Tasks.Task`1",
            SpecialType.System_Runtime_InteropServices_WindowsRuntime_EventRegistrationToken => "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken",
            SpecialType.System_Runtime_InteropServices_WindowsRuntime_EventRegistrationTokenTable_T => "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationTokenTable`1",
            SpecialType.System_ValueTuple_T1 => "System.ValueTuple`1",
            SpecialType.System_ValueTuple_T2 => "System.ValueTuple`2",
            SpecialType.System_ValueTuple_T3 => "System.ValueTuple`3",
            SpecialType.System_ValueTuple_T4 => "System.ValueTuple`4",
            SpecialType.System_ValueTuple_T5 => "System.ValueTuple`5",
            SpecialType.System_ValueTuple_T6 => "System.ValueTuple`6",
            SpecialType.System_ValueTuple_T7 => "System.ValueTuple`7",
            SpecialType.System_ValueTuple_TRest => "System.ValueTuple`8",
            SpecialType.System_Type => "System.Type",
            SpecialType.System_Exception => "System.Exception",
            SpecialType.System_Runtime_CompilerServices_IAsyncStateMachine => "System.Runtime.CompilerServices.IAsyncStateMachine",
            _ => throw new InvalidOperationException("Special type is not supported."),
        };

        var type = TryGetMetadataReferenceTypeByMetadataName(metadataName);

        if (type is INamedTypeSymbol { ContainingAssembly: { Name: var assemblyName } } &&
            !string.Equals(assemblyName, "System.Runtime", StringComparison.OrdinalIgnoreCase))
        {
            var preferred = GetTypeByMetadataName(metadataName, "System.Runtime");
            if (preferred is not null)
                type = preferred;
        }

        return type ?? (INamedTypeSymbol)ErrorTypeSymbol;
    }

    private static void InitializeTypeParameters(
      SourceNamedTypeSymbol typeSymbol,
      TypeParameterListSyntax? typeParameterList,
      SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
      SyntaxTree syntaxTree,
      DiagnosticBag? diagnostics = null) // optional: for unknown/dup clause reporting
    {
        if (typeParameterList is null || typeParameterList.Parameters.Count == 0)
            return;

        // Index clauses by parameter name (where T: ...)
        Dictionary<string, List<TypeParameterConstraintClauseSyntax>>? clausesByName = null;
        if (constraintClauses.Count > 0)
        {
            clausesByName = new Dictionary<string, List<TypeParameterConstraintClauseSyntax>>(StringComparer.Ordinal);

            foreach (var clause in constraintClauses)
            {
                var name = clause.TypeParameter.Identifier.ValueText;

                if (!clausesByName.TryGetValue(name, out var list))
                    clausesByName[name] = list = new List<TypeParameterConstraintClauseSyntax>();

                list.Add(clause);
            }
        }

        // Optional: validate clause names and duplicates
        if (diagnostics is not null && clausesByName is not null)
        {
            var declared = new HashSet<string>(
                typeParameterList.Parameters.Select(p => p.Identifier.ValueText),
                StringComparer.Ordinal);

            foreach (var (name, list) in clausesByName)
            {
                if (!declared.Contains(name))
                {
                    // You’ll want a real diagnostic here.
                    // diagnostics.Report... (unknown type parameter in constraint clause)
                }

                // If you want to forbid multiple where-clauses per parameter:
                // if (list.Count > 1) diagnostics.Report... (duplicate constraint clause)
            }
        }

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>(typeParameterList.Parameters.Count);
        int ordinal = 0;

        foreach (var parameter in typeParameterList.Parameters)
        {
            var identifier = parameter.Identifier;
            var location = syntaxTree.GetLocation(identifier.Span);
            var reference = parameter.GetReference();

            // 1) inline constraints on the parameter node
            var (inlineKind, inlineRefs) = TypeParameterConstraintAnalyzer.AnalyzeInline(parameter);

            // 2) where-clauses targeting this parameter name
            var clauseKind = TypeParameterConstraintKind.None;
            var clauseRefsBuilder = ImmutableArray.CreateBuilder<SyntaxReference>();

            if (clausesByName is not null &&
                clausesByName.TryGetValue(parameter.Identifier.ValueText, out var matchingClauses))
            {
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
                identifier.Text,
                typeSymbol,
                typeSymbol,
                typeSymbol.ContainingNamespace,
                [location],
                [reference],
                ordinal++,
                mergedKind,
                mergedRefs,
                variance);

            builder.Add(typeParameter);
        }

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

    internal ITypeSymbol? TryBindTypeSyntaxWithoutBinder(TypeSyntax syntax)
    {
        // Minimal binding for explicit generic type arguments at overload-resolution time.
        // Supports predefined types and simple/qualified identifiers via metadata lookup.
        // Full fidelity binding still happens in the binder.

        switch (syntax)
        {
            case PredefinedTypeSyntax pts:
                return ResolvePredefinedType(pts);

            case IdentifierNameSyntax id:
                return GetTypeByMetadataName(id.Identifier.ValueText) ?? ErrorTypeSymbol;

            case QualifiedNameSyntax q:
                {
                    // Use the display string as a metadata name best-effort.
                    var name = q.ToString();
                    return GetTypeByMetadataName(name) ?? ErrorTypeSymbol;
                }

            case GenericNameSyntax:
                // Nested generic type args in explicit method arg lists are not supported here yet.
                return ErrorTypeSymbol;

            default:
                return ErrorTypeSymbol;
        }
    }
}
