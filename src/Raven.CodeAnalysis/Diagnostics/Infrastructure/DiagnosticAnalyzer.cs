using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Diagnostics;

/// <summary>Base class for analyzers that can produce diagnostics for a compilation.</summary>
public abstract class DiagnosticAnalyzer
{
    private readonly object _initializationGate = new();
    private volatile bool _initialized;
    private readonly List<Action<CompilationAnalysisContext>> _compilationActions = new();
    private readonly List<Action<SyntaxTreeAnalysisContext>> _syntaxTreeActions = new();
    private readonly List<SymbolActionRegistration> _symbolActions = new();
    private readonly List<SyntaxNodeActionRegistration> _syntaxNodeActions = new();
    private readonly List<OperationActionRegistration> _operationActions = new();
    private bool _concurrentExecutionEnabled;
    private GeneratedCodeAnalysisFlags _generatedCodeAnalysis;

    /// <summary>Implement to register analysis actions.</summary>
    public abstract void Initialize(AnalysisContext context);

    public virtual ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

    internal bool TryEnsureInitialized() => TryEnsureInitialized(out _);

    internal bool TryEnsureInitialized(out Exception? initializationException)
    {
        initializationException = null;
        if (_initialized)
            return true;

        lock (_initializationGate)
        {
            if (_initialized)
                return true;

            try
            {
                // Publish registrations only after initialization succeeds. A failed or
                // canceled attempt must not leave callbacks or concurrency settings behind.
                var compilationActions = new List<Action<CompilationAnalysisContext>>();
                var syntaxTreeActions = new List<Action<SyntaxTreeAnalysisContext>>();
                var symbolActions = new List<SymbolActionRegistration>();
                var syntaxNodeActions = new List<SyntaxNodeActionRegistration>();
                var operationActions = new List<OperationActionRegistration>();
                var concurrentExecutionEnabled = false;
                var generatedCodeAnalysis = GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics;
                Initialize(new AnalysisContext(
                    compilationActions,
                    syntaxTreeActions,
                    symbolActions,
                    syntaxNodeActions,
                    operationActions,
                    () => concurrentExecutionEnabled = true,
                    flags => generatedCodeAnalysis = flags));
                _compilationActions.AddRange(compilationActions);
                _syntaxTreeActions.AddRange(syntaxTreeActions);
                _symbolActions.AddRange(symbolActions);
                _syntaxNodeActions.AddRange(syntaxNodeActions);
                _operationActions.AddRange(operationActions);
                _concurrentExecutionEnabled = concurrentExecutionEnabled;
                _generatedCodeAnalysis = generatedCodeAnalysis;
                _initialized = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                initializationException = exception;
                return false;
            }
        }
    }

    internal IReadOnlyList<Action<CompilationAnalysisContext>> CompilationActions => _compilationActions;

    internal IReadOnlyList<Action<SyntaxTreeAnalysisContext>> SyntaxTreeActions => _syntaxTreeActions;

    internal IReadOnlyList<SymbolActionRegistration> SymbolActions => _symbolActions;

    internal IReadOnlyList<SyntaxNodeActionRegistration> SyntaxNodeActions => _syntaxNodeActions;

    internal IReadOnlyList<OperationActionRegistration> OperationActions => _operationActions;

    internal bool ConcurrentExecutionEnabled => _concurrentExecutionEnabled;

    internal bool ShouldAnalyzeTree(SyntaxTree tree)
        => !tree.IsGenerated || (_generatedCodeAnalysis & GeneratedCodeAnalysisFlags.Analyze) != 0;

    internal bool ShouldReportDiagnostic(Diagnostic diagnostic)
        => diagnostic.Location.SourceTree?.IsGenerated != true ||
            (_generatedCodeAnalysis & GeneratedCodeAnalysisFlags.ReportDiagnostics) != 0;

    /// <summary>Runs the analyzer for the specified compilation.</summary>
    public IEnumerable<Diagnostic> Analyze(Compilation compilation, CancellationToken cancellationToken = default)
        => Analyze(compilation, syntaxTree: null, cancellationToken);

    /// <summary>Runs the analyzer for the specified syntax tree in the compilation.</summary>
    public IEnumerable<Diagnostic> Analyze(Compilation compilation, SyntaxTree? syntaxTree, CancellationToken cancellationToken = default)
        => AnalyzeWithResult(compilation, syntaxTree, cancellationToken).Diagnostics;

    internal AnalyzerDiagnosticsResult AnalyzeWithResult(Compilation compilation, SyntaxTree? syntaxTree, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryEnsureInitialized())
            return new([], Succeeded: false);

        var succeeded = true;
        var diagnostics = new List<Diagnostic>();
        void ReportDiagnostic(Diagnostic diagnostic)
        {
            if (ShouldReportDiagnostic(diagnostic))
                diagnostics.Add(diagnostic);
        }
        var syntaxTrees = syntaxTree is null
            ? compilation.SyntaxTrees
            : [syntaxTree];

        foreach (var action in _compilationActions)
        {
            var compilationContext = new CompilationAnalysisContext(
                compilation,
                syntaxTree,
                ReportDiagnostic,
                cancellationToken);

            try
            {
                action(compilationContext);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                succeeded = false;
                // Analyzer failures should not stop compilation.
            }
        }

        foreach (var tree in syntaxTrees)
        {
            if (!ShouldAnalyzeTree(tree))
                continue;
            var treeContext = new SyntaxTreeAnalysisContext(tree, compilation, ReportDiagnostic, cancellationToken);
            foreach (var action in _syntaxTreeActions)
            {
                try
                {
                    action(treeContext);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    succeeded = false;
                    // Analyzer failures should not stop compilation.
                }
            }

            if (_symbolActions.Count > 0)
            {
                var symbolSemanticModel = compilation.GetSemanticModel(tree);
                foreach (var symbol in AnalyzerSymbolEnumerator.EnumerateSymbols(tree, symbolSemanticModel, GetRegisteredSymbolKinds(), cancellationToken))
                {
                    foreach (var registration in _symbolActions)
                    {
                        if (!registration.Kinds.Contains(symbol.Kind))
                            continue;

                        var symbolContext = new SymbolAnalysisContext(
                            symbol,
                            compilation,
                            ReportDiagnostic,
                            cancellationToken);

                        try
                        {
                            registration.Action(symbolContext);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            succeeded = false;
                            // Analyzer failures should not stop compilation.
                        }
                    }
                }
            }

            SemanticModel? semanticModel = null;

            if (_syntaxNodeActions.Count > 0)
            {
                var root = tree.GetRoot(cancellationToken);
                semanticModel = compilation.GetSemanticModel(tree);

                foreach (var node in root.DescendantNodesAndSelf())
                {
                    for (var i = 0; i < _syntaxNodeActions.Count; i++)
                    {
                        var registration = _syntaxNodeActions[i];
                        if (!registration.Kinds.Contains(node.Kind))
                            continue;

                        var nodeContext = new SyntaxNodeAnalysisContext(
                            node,
                            semanticModel,
                            compilation,
                            ReportDiagnostic,
                            cancellationToken);

                        try
                        {
                            registration.Action(nodeContext);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            succeeded = false;
                            // Analyzer failures should not stop compilation.
                        }
                    }
                }
            }

            if (_operationActions.Count == 0)
                continue;

            semanticModel ??= compilation.GetSemanticModel(tree);
            foreach (var operation in AnalyzerOperationEnumerator.EnumerateOperations(
                         tree,
                         semanticModel,
                         GetRegisteredOperationKinds(),
                         cancellationToken))
            {
                foreach (var registration in _operationActions)
                {
                    if (!registration.Kinds.Contains(operation.Kind))
                        continue;

                    var operationContext = new OperationAnalysisContext(
                        operation,
                        semanticModel,
                        compilation,
                        ReportDiagnostic,
                        cancellationToken);

                    try
                    {
                        registration.Action(operationContext);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        succeeded = false;
                        // Analyzer failures should not stop compilation.
                    }
                }
            }
        }

        return new AnalyzerDiagnosticsResult(
            diagnostics.Select(diagnostic => AnalyzerDiagnosticProperties.WithAnalyzerOrigin(diagnostic, this))
                .OrderBy(static diagnostic => diagnostic, DiagnosticComparer.Instance).ToImmutableArray(),
            succeeded);

        ImmutableHashSet<SymbolKind> GetRegisteredSymbolKinds()
            => _symbolActions
                .SelectMany(static action => action.Kinds)
                .ToImmutableHashSet();

        ImmutableHashSet<OperationKind> GetRegisteredOperationKinds()
            => _operationActions
                .SelectMany(static action => action.Kinds)
                .ToImmutableHashSet();
    }
}

public interface ICompilationOptionsAwareAnalyzer
{
    bool ShouldAnalyze(CompilationOptions options);
}

/// <summary>
/// Defines the invalidation scope for diagnostics produced by a syntax-node analyzer action.
/// </summary>
public enum SyntaxNodeAnalysisScope
{
    /// <summary>
    /// Diagnostics produced by the action may depend on the containing document or semantic state outside the
    /// analyzed node. This is the safe default and requires rerunning the action when the document changes.
    /// </summary>
    Document,

    /// <summary>
    /// Diagnostics produced by the action depend only on the analyzed node and its stable semantic context.
    /// The analyzer driver may reuse diagnostics for unchanged nodes across document updates.
    /// </summary>
    Node
}

/// <summary>Context used to register analysis actions.</summary>
public sealed class AnalysisContext
{
    private readonly List<Action<CompilationAnalysisContext>> _compilationActions;
    private readonly List<Action<SyntaxTreeAnalysisContext>> _syntaxTreeActions;
    private readonly List<SymbolActionRegistration> _symbolActions;
    private readonly List<SyntaxNodeActionRegistration> _syntaxNodeActions;
    private readonly List<OperationActionRegistration> _operationActions;
    private readonly Action _enableConcurrentExecution;
    private readonly Action<GeneratedCodeAnalysisFlags> _configureGeneratedCodeAnalysis;

    internal AnalysisContext(
        List<Action<CompilationAnalysisContext>> compilationActions,
        List<Action<SyntaxTreeAnalysisContext>> syntaxTreeActions,
        List<SymbolActionRegistration> symbolActions,
        List<SyntaxNodeActionRegistration> syntaxNodeActions,
        List<OperationActionRegistration> operationActions,
        Action enableConcurrentExecution,
        Action<GeneratedCodeAnalysisFlags> configureGeneratedCodeAnalysis)
    {
        _compilationActions = compilationActions;
        _syntaxTreeActions = syntaxTreeActions;
        _symbolActions = symbolActions;
        _syntaxNodeActions = syntaxNodeActions;
        _operationActions = operationActions;
        _enableConcurrentExecution = enableConcurrentExecution;
        _configureGeneratedCodeAnalysis = configureGeneratedCodeAnalysis;
    }

    /// <summary>
    /// Allows analyzer actions from this analyzer to run concurrently with other concurrent analyzers.
    /// Analyzer implementations that enable this must not store mutable per-run state on the analyzer instance.
    /// </summary>
    public void EnableConcurrentExecution()
        => _enableConcurrentExecution();

    /// <summary>Configures callbacks and reporting for source-generated trees. The default enables both.</summary>
    public void ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags analysisMode)
        => _configureGeneratedCodeAnalysis(analysisMode);

    /// <summary>Registers an action executed once for the compilation being analyzed.</summary>
    public void RegisterCompilationAction(Action<CompilationAnalysisContext> action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        _compilationActions.Add(action);
    }

    /// <summary>Registers an action executed for each syntax tree in a compilation.</summary>
    public void RegisterSyntaxTreeAction(Action<SyntaxTreeAnalysisContext> action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        _syntaxTreeActions.Add(action);
    }

    /// <summary>Registers an action executed for symbols whose <see cref="ISymbol.Kind"/> matches one of the provided kinds.</summary>
    public void RegisterSymbolAction(Action<SymbolAnalysisContext> action, params SymbolKind[] symbolKinds)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        if (symbolKinds is null || symbolKinds.Length == 0)
            throw new ArgumentException("At least one symbol kind must be provided.", nameof(symbolKinds));

        _symbolActions.Add(new SymbolActionRegistration(
            action,
            symbolKinds
                .Distinct()
                .OrderBy(static kind => (int)kind)
                .ToImmutableArray()));
    }

    /// <summary>
    /// Registers an action executed for syntax nodes whose <see cref="SyntaxNode.Kind"/> matches
    /// one of the provided <paramref name="syntaxKinds"/>.
    /// </summary>
    public void RegisterSyntaxNodeAction(Action<SyntaxNodeAnalysisContext> action, params SyntaxKind[] syntaxKinds)
        => RegisterSyntaxNodeAction(action, SyntaxNodeAnalysisScope.Document, syntaxKinds);

    /// <summary>
    /// Registers an action executed for syntax nodes whose <see cref="SyntaxNode.Kind"/> matches
    /// one of the provided <paramref name="syntaxKinds"/>, with an explicit invalidation <paramref name="scope"/>.
    /// </summary>
    public void RegisterSyntaxNodeAction(
        Action<SyntaxNodeAnalysisContext> action,
        SyntaxNodeAnalysisScope scope,
        params SyntaxKind[] syntaxKinds)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        if (syntaxKinds is null || syntaxKinds.Length == 0)
            throw new ArgumentException("At least one syntax kind must be provided.", nameof(syntaxKinds));

        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope));

        _syntaxNodeActions.Add(new SyntaxNodeActionRegistration(
            action,
            scope,
            syntaxKinds
                .Distinct()
                .OrderBy(static kind => (int)kind)
                .ToImmutableArray()));
    }

    /// <summary>
    /// Registers an action executed for semantic operations whose <see cref="IOperation.Kind"/> matches
    /// one of the provided <paramref name="operationKinds"/>.
    /// </summary>
    public void RegisterOperationAction(Action<OperationAnalysisContext> action, params OperationKind[] operationKinds)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        if (operationKinds is null || operationKinds.Length == 0)
            throw new ArgumentException("At least one operation kind must be provided.", nameof(operationKinds));

        _operationActions.Add(new OperationActionRegistration(
            action,
            operationKinds
                .Distinct()
                .OrderBy(static kind => (int)kind)
                .ToImmutableArray()));
    }
}

/// <summary>Context for analyzing a compilation.</summary>
public readonly struct CompilationAnalysisContext
{
    private readonly Action<Diagnostic> _reportDiagnostic;

    internal CompilationAnalysisContext(
        Compilation compilation,
        SyntaxTree? syntaxTree,
        Action<Diagnostic> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        Compilation = compilation;
        SyntaxTree = syntaxTree;
        _reportDiagnostic = reportDiagnostic;
        CancellationToken = cancellationToken;
    }

    /// <summary>The compilation being analyzed.</summary>
    public Compilation Compilation { get; }

    /// <summary>
    /// The syntax tree being analyzed, or <c>null</c> when the analyzer is running for the whole compilation.
    /// </summary>
    public SyntaxTree? SyntaxTree { get; }

    /// <summary>Cancellation token for the analysis.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports a diagnostic.</summary>
    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        _reportDiagnostic(diagnostic);
    }
}

/// <summary>Context for analyzing a declared symbol.</summary>
public readonly struct SymbolAnalysisContext
{
    private readonly Action<Diagnostic> _reportDiagnostic;

    internal SymbolAnalysisContext(
        ISymbol symbol,
        Compilation compilation,
        Action<Diagnostic> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        Symbol = symbol;
        Compilation = compilation;
        _reportDiagnostic = reportDiagnostic;
        CancellationToken = cancellationToken;
    }

    /// <summary>The symbol being analyzed.</summary>
    public ISymbol Symbol { get; }

    /// <summary>The compilation containing the symbol.</summary>
    public Compilation Compilation { get; }

    /// <summary>Cancellation token for the analysis.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports a diagnostic.</summary>
    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        _reportDiagnostic(diagnostic);
    }
}

/// <summary>Context for analyzing a single syntax tree.</summary>
public readonly struct SyntaxTreeAnalysisContext
{
    private readonly Action<Diagnostic> _reportDiagnostic;

    internal SyntaxTreeAnalysisContext(
        SyntaxTree syntaxTree,
        Compilation compilation,
        Action<Diagnostic> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        SyntaxTree = syntaxTree;
        Compilation = compilation;
        _reportDiagnostic = reportDiagnostic;
        CancellationToken = cancellationToken;
    }

    /// <summary>The syntax tree being analyzed.</summary>
    public SyntaxTree SyntaxTree { get; }

    /// <summary>The compilation containing the syntax tree.</summary>
    public Compilation Compilation { get; }

    /// <summary>Cancellation token for the analysis.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports a diagnostic.</summary>
    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        _reportDiagnostic(diagnostic);
    }
}

/// <summary>Context for analyzing a specific syntax node.</summary>
public readonly struct SyntaxNodeAnalysisContext
{
    private readonly Action<Diagnostic> _reportDiagnostic;

    internal SyntaxNodeAnalysisContext(
        SyntaxNode node,
        SemanticModel semanticModel,
        Compilation compilation,
        Action<Diagnostic> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        Node = node;
        SemanticModel = semanticModel;
        Compilation = compilation;
        _reportDiagnostic = reportDiagnostic;
        CancellationToken = cancellationToken;
    }

    /// <summary>The syntax node being analyzed.</summary>
    public SyntaxNode Node { get; }

    /// <summary>The semantic model for the current syntax tree.</summary>
    public SemanticModel SemanticModel { get; }

    /// <summary>The compilation containing the syntax tree.</summary>
    public Compilation Compilation { get; }

    /// <summary>Cancellation token for the analysis.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports a diagnostic.</summary>
    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        _reportDiagnostic(diagnostic);
    }
}

/// <summary>Context for analyzing a semantic operation.</summary>
public readonly struct OperationAnalysisContext
{
    private readonly Action<Diagnostic> _reportDiagnostic;

    internal OperationAnalysisContext(
        IOperation operation,
        SemanticModel semanticModel,
        Compilation compilation,
        Action<Diagnostic> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        Operation = operation;
        SemanticModel = semanticModel;
        Compilation = compilation;
        _reportDiagnostic = reportDiagnostic;
        CancellationToken = cancellationToken;
    }

    /// <summary>The operation being analyzed.</summary>
    public IOperation Operation { get; }

    /// <summary>The semantic model for the current syntax tree.</summary>
    public SemanticModel SemanticModel { get; }

    /// <summary>The syntax tree being analyzed.</summary>
    public SyntaxTree SyntaxTree => Operation.Syntax.SyntaxTree!;

    /// <summary>The compilation containing the syntax tree.</summary>
    public Compilation Compilation { get; }

    /// <summary>Cancellation token for the analysis.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Reports a diagnostic.</summary>
    public void ReportDiagnostic(Diagnostic diagnostic)
    {
        _reportDiagnostic(diagnostic);
    }
}

internal readonly record struct SymbolActionRegistration(
    Action<SymbolAnalysisContext> Action,
    ImmutableArray<SymbolKind> Kinds);

internal readonly record struct SyntaxNodeActionRegistration(
    Action<SyntaxNodeAnalysisContext> Action,
    SyntaxNodeAnalysisScope Scope,
    ImmutableArray<SyntaxKind> Kinds);

internal readonly record struct OperationActionRegistration(
    Action<OperationAnalysisContext> Action,
    ImmutableArray<OperationKind> Kinds);

internal static class AnalyzerSymbolEnumerator
{
    public static IEnumerable<ISymbol> EnumerateSymbols(
        SyntaxTree syntaxTree,
        SemanticModel semanticModel,
        IImmutableSet<SymbolKind> symbolKinds,
        CancellationToken cancellationToken)
    {
        if (symbolKinds.Count == 0)
            yield break;

        var root = syntaxTree.GetRoot(cancellationToken);
        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanDeclareRequestedSymbol(node, symbolKinds))
                continue;

            ISymbol? symbol = null;
            try
            {
                symbol = semanticModel.GetDeclaredSymbol(node);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Analyzer symbol enumeration should skip declarations the semantic model cannot answer yet.
            }

            if (symbol is null)
                continue;

            symbol = symbol.UnderlyingSymbol;
            if (symbolKinds.Contains(symbol.Kind))
                yield return symbol;
        }
    }

    private static bool CanDeclareRequestedSymbol(SyntaxNode node, IImmutableSet<SymbolKind> symbolKinds)
        => node switch
        {
            TypeDeclarationSyntax or DelegateDeclarationSyntax or UnionDeclarationSyntax or EnumDeclarationSyntax
                => symbolKinds.Contains(SymbolKind.Type),
            BaseMethodDeclarationSyntax or BaseConstructorDeclarationSyntax or ParameterlessConstructorDeclarationSyntax or FunctionStatementSyntax or FunctionExpressionSyntax
                => symbolKinds.Contains(SymbolKind.Method),
            MacroDeclarationSyntax
                => symbolKinds.Contains(SymbolKind.Macro),
            BasePropertyDeclarationSyntax
                => symbolKinds.Contains(SymbolKind.Property),
            EventDeclarationSyntax
                => symbolKinds.Contains(SymbolKind.Event),
            VariableDeclaratorSyntax declarator when declarator.Parent?.Parent is FieldDeclarationSyntax or ConstDeclarationSyntax
                => symbolKinds.Contains(SymbolKind.Field),
            ParameterSyntax
                => symbolKinds.Contains(SymbolKind.Parameter),
            _ => false
        };
}

internal static class AnalyzerOperationEnumerator
{
    public static IEnumerable<IOperation> EnumerateOperations(
        SyntaxTree syntaxTree,
        SemanticModel semanticModel,
        IImmutableSet<OperationKind> operationKinds,
        CancellationToken cancellationToken)
    {
        if (operationKinds.Count == 0)
            yield break;

        var root = syntaxTree.GetRoot(cancellationToken);
        if (operationKinds.Count == 1 && operationKinds.Contains(OperationKind.ExpressionStatement))
        {
            foreach (var expressionStatement in root.DescendantNodesAndSelf().OfType<ExpressionStatementSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                IOperation? operation;
                try
                {
                    operation = semanticModel.GetOperation(expressionStatement, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (operation?.Kind == OperationKind.ExpressionStatement)
                    yield return operation;
            }

            yield break;
        }

        var visited = new HashSet<OperationVisitKey>();

        foreach (var rootSyntax in EnumerateOperationRootSyntaxNodes(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            IOperation? operation;
            try
            {
                operation = semanticModel.GetOperation(rootSyntax, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Analyzer operation enumeration should skip nodes the semantic model cannot answer yet.
                continue;
            }

            if (operation is null)
                continue;

            foreach (var descendant in EnumerateOperationDescendants(operation, operationKinds, visited, cancellationToken))
                yield return descendant;
        }
    }

    private static IEnumerable<SyntaxNode> EnumerateOperationRootSyntaxNodes(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is not StatementSyntax and not ExpressionSyntax)
                continue;

            if (node.Parent is StatementSyntax or ExpressionSyntax)
                continue;

            yield return node;
        }
    }

    private static IEnumerable<IOperation> EnumerateOperationDescendants(
        IOperation operation,
        IImmutableSet<OperationKind> operationKinds,
        HashSet<OperationVisitKey> visited,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<IOperation>();
        stack.Push(operation);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();
            if (!visited.Add(new OperationVisitKey(current.Syntax, current.Kind, current.IsImplicit)))
                continue;

            if (operationKinds.Contains(current.Kind))
                yield return current;

            ImmutableArray<IOperation> children;
            try
            {
                children = current.ChildOperations;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            for (var i = children.Length - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    private readonly record struct OperationVisitKey(
        SyntaxNode Syntax,
        OperationKind Kind,
        bool IsImplicit);
}
