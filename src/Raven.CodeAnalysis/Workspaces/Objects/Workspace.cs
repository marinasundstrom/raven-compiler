using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis;

/// <summary>
/// Coordinates a <see cref="Solution"/> instance and raises events when it changes.
/// </summary>
public class Workspace
{
    private Solution _currentSolution;
    private readonly object _compilationGate = new();
    private readonly Dictionary<ProjectId, ProjectCompilationState> _projectCompilations = new();
    private readonly Dictionary<ProjectId, ProjectCompilationState> _analysisProjectCompilations = new();
    private readonly ConcurrentDictionary<ProjectDiagnosticsCacheKey, ImmutableArray<Diagnostic>> _projectDiagnosticsCache = new();
    private readonly ConcurrentDictionary<ProjectAnalyzerDiagnosticsCacheKey, ImmutableArray<Diagnostic>> _projectAnalyzerDiagnosticsCache = new();
    private readonly ConcurrentDictionary<DocumentAnalyzerDiagnosticsCacheKey, ImmutableArray<Diagnostic>> _documentAnalyzerDiagnosticsCache = new();

    protected Workspace(string kind)
        : this(kind, HostServices.Default)
    {
    }

    protected Workspace(string kind, HostServices services)
    {
        Kind = kind;
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _currentSolution = new Solution(services, this);
    }

    public string Kind { get; }

    /// <summary>Services available to this workspace.</summary>
    public HostServices Services { get; }

    public event EventHandler<WorkspaceChangeEventArgs>? WorkspaceChanged;

    public Solution CurrentSolution => _currentSolution;

    /// <summary>Creates a new empty <see cref="Solution"/> using the workspace services.</summary>
    public Solution CreateSolution() => new Solution(Services, this);

    /// <summary>Opens the specified <see cref="Solution"/> as the current solution.</summary>
    public void OpenSolution(Solution solution)
    {
        if (solution is null) throw new ArgumentNullException(nameof(solution));
        if (!ReferenceEquals(solution.Services, Services))
            throw new InvalidOperationException("Solution was created with different host services.");
        if (!ReferenceEquals(solution.Workspace, this))
            throw new InvalidOperationException("Solution was created with different workspace.");

        TryApplyChanges(solution);
    }

    /// <summary>Attempts to apply a new solution and raises the appropriate change event.</summary>
    public bool TryApplyChanges(Solution newSolution)
    {
        if (newSolution is null) throw new ArgumentNullException(nameof(newSolution));
        if (!ReferenceEquals(newSolution.Services, Services))
            throw new InvalidOperationException("Solution was created with different host services.");
        if (!ReferenceEquals(newSolution.Workspace, this))
            throw new InvalidOperationException("Solution was created with different workspace.");
        var oldSolution = _currentSolution;
        if (ReferenceEquals(oldSolution, newSolution)) return true;

        var (kind, projectId, documentId) = ComputeChangeKind(oldSolution, newSolution);
        _currentSolution = newSolution;

        // drop compilation caches for removed projects
        var removed = _projectCompilations.Keys.Where(id => newSolution.GetProject(id) is null).ToList();
        foreach (var id in removed)
            _projectCompilations.Remove(id);

        removed = _analysisProjectCompilations.Keys.Where(id => newSolution.GetProject(id) is null).ToList();
        foreach (var id in removed)
            _analysisProjectCompilations.Remove(id);

        RemoveStaleProjectDiagnostics(newSolution);
        RemoveStaleProjectAnalyzerDiagnostics(newSolution);
        RemoveStaleDocumentAnalyzerDiagnostics(newSolution);

        OnWorkspaceChanged(new WorkspaceChangeEventArgs(kind, oldSolution, newSolution, projectId, documentId));
        return true;
    }

    private void RemoveStaleProjectDiagnostics(Solution solution)
    {
        foreach (var key in _projectDiagnosticsCache.Keys)
        {
            var project = solution.GetProject(key.ProjectId);
            if (project is null || project.Version != key.Version)
                _projectDiagnosticsCache.TryRemove(key, out _);
        }
    }

    private void RemoveStaleProjectAnalyzerDiagnostics(Solution solution)
    {
        foreach (var key in _projectAnalyzerDiagnosticsCache.Keys)
        {
            var project = solution.GetProject(key.ProjectId);
            if (project is null || project.Version != key.Version)
                _projectAnalyzerDiagnosticsCache.TryRemove(key, out _);
        }
    }

    private void RemoveStaleDocumentAnalyzerDiagnostics(Solution solution)
    {
        foreach (var key in _documentAnalyzerDiagnosticsCache.Keys)
        {
            var project = solution.GetProject(key.ProjectId);
            var document = project?.GetDocument(key.DocumentId);
            if (project is null ||
                document is null ||
                project.Version != key.ProjectVersion ||
                document.Version != key.DocumentVersion)
            {
                _documentAnalyzerDiagnosticsCache.TryRemove(key, out _);
            }
        }
    }

    private static (WorkspaceChangeKind kind, ProjectId? projectId, DocumentId? documentId)
        ComputeChangeKind(Solution oldSolution, Solution newSolution)
    {
        foreach (var proj in newSolution.Projects)
        {
            var oldProj = oldSolution.GetProject(proj.Id);
            if (oldProj is null)
                return (WorkspaceChangeKind.ProjectAdded, proj.Id, null);

            if (oldProj.Version != proj.Version)
            {
                foreach (var doc in proj.Documents)
                {
                    var oldDoc = oldProj.GetDocument(doc.Id);
                    if (oldDoc is null)
                        return (WorkspaceChangeKind.DocumentAdded, proj.Id, doc.Id);
                    if (oldDoc.Version != doc.Version)
                        return (WorkspaceChangeKind.DocumentChanged, proj.Id, doc.Id);
                }
                foreach (var oldDoc in oldProj.Documents)
                {
                    if (proj.GetDocument(oldDoc.Id) is null)
                        return (WorkspaceChangeKind.DocumentRemoved, proj.Id, oldDoc.Id);
                }
                return (WorkspaceChangeKind.ProjectChanged, proj.Id, null);
            }
        }
        foreach (var oldProj in oldSolution.Projects)
        {
            if (newSolution.GetProject(oldProj.Id) is null)
                return (WorkspaceChangeKind.ProjectRemoved, oldProj.Id, null);
        }
        return (WorkspaceChangeKind.SolutionChanged, null, null);
    }

    protected virtual void OnWorkspaceChanged(WorkspaceChangeEventArgs e)
        => WorkspaceChanged?.Invoke(this, e);

    /// <summary>
    /// Gets a <see cref="Compilation"/> for the specified project. Results are cached
    /// and incrementally rebuilt when documents change.
    /// </summary>
    public Compilation GetCompilation(ProjectId projectId)
    {
        return GetCompilation(projectId, new HashSet<ProjectId>());
    }

    public Compilation GetCompilation(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetCompilation(project, new HashSet<ProjectId>());
    }

    public Compilation CreateAnalysisCompilation(ProjectId projectId)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        return CreateAnalysisCompilation(project, new HashSet<ProjectId>());
    }

    private Compilation GetCompilation(ProjectId projectId, HashSet<ProjectId> building)
    {
        var project = CurrentSolution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        return GetCompilation(project, building, _projectCompilations);
    }

    private Compilation GetCompilation(Project project, HashSet<ProjectId> building)
    {
        return GetCompilation(project, building, _projectCompilations);
    }

    private Compilation GetCompilation(
        Project project,
        HashSet<ProjectId> building,
        Dictionary<ProjectId, ProjectCompilationState> compilationCache)
    {
        var projectId = project.Id;

        lock (_compilationGate)
        {
            if (!building.Add(projectId))
                throw new InvalidOperationException("Circular project reference detected.");

            try
            {
                var referencedCompilations = GetReferencedCompilations(project, building, compilationCache);

                if (!compilationCache.TryGetValue(projectId, out var state))
                {
                    return BuildCompilation(project, null, referencedCompilations, compilationCache);
                }

                if (state.Version == project.Version &&
                    ReferencedCompilationsMatch(state, referencedCompilations))
                {
                    return state.Compilation;
                }

                return BuildCompilation(project, state, referencedCompilations, compilationCache);
            }
            finally
            {
                building.Remove(projectId);
            }
        }
    }

    private Compilation CreateAnalysisCompilation(Project project, HashSet<ProjectId> building)
    {
        ArgumentNullException.ThrowIfNull(project);

        var compilation = GetCompilation(project, building, _analysisProjectCompilations);
        RemoveSupersededDiagnosticCaches(project.Id, compilation);
        return compilation;
    }

    private void RemoveSupersededDiagnosticCaches(ProjectId projectId, Compilation compilation)
    {
        foreach (var key in _projectDiagnosticsCache.Keys)
        {
            if (key.ProjectId == projectId && !ReferenceEquals(key.Compilation, compilation))
                _projectDiagnosticsCache.TryRemove(key, out _);
        }

        foreach (var key in _projectAnalyzerDiagnosticsCache.Keys)
        {
            if (key.ProjectId == projectId && !ReferenceEquals(key.Compilation, compilation))
                _projectAnalyzerDiagnosticsCache.TryRemove(key, out _);
        }

        foreach (var key in _documentAnalyzerDiagnosticsCache.Keys)
        {
            if (key.ProjectId == projectId && !ReferenceEquals(key.Compilation, compilation))
                _documentAnalyzerDiagnosticsCache.TryRemove(key, out _);
        }
    }

    private Compilation BuildCompilation(
        Project project,
        ProjectCompilationState? state,
        ImmutableArray<(ProjectId ProjectId, Compilation Compilation)> referencedCompilations,
        Dictionary<ProjectId, ProjectCompilationState> compilationCache)
    {
        state ??= new ProjectCompilationState();
        var previousCompilation = state.Compilation;
        var projectReferencesChanged = previousCompilation is not null &&
            !ReferencedCompilationsMatch(state, referencedCompilations);

        var syntaxTrees = new List<SyntaxTree>();
        var reusedSyntaxTrees = ImmutableArray.CreateBuilder<SyntaxTree>();
        var changedSyntaxTrees = ImmutableArray.CreateBuilder<Compilation.IncrementalChangedSyntaxTree>();
        var documentStates = state.DocumentStates;
        var presentDocs = new HashSet<DocumentId>();
        var documentSetChanged = false;

        foreach (var doc in project.Documents)
        {
            presentDocs.Add(doc.Id);
            var tree = doc.SyntaxTree;
            if (tree is null)
                continue;
            if (documentStates.TryGetValue(doc.Id, out var docState) && docState.Version == doc.Version)
            {
                syntaxTrees.Add(docState.SyntaxTree);
                reusedSyntaxTrees.Add(docState.SyntaxTree);
            }
            else
            {
                syntaxTrees.Add(tree);
                if (docState is not null)
                {
                    var (changedOwners, matchedOwners, ownerChanges, blocksSemanticDiagnosticTransfer, requiresFullSemanticRebind) = IncrementalExecutableOwnerAnalyzer.Analyze(docState.SyntaxTree, tree);
                    changedSyntaxTrees.Add(new Compilation.IncrementalChangedSyntaxTree(
                        tree,
                        docState.SyntaxTree,
                        changedOwners,
                        matchedOwners,
                        ownerChanges,
                        blocksSemanticDiagnosticTransfer,
                        requiresFullSemanticRebind));
                }
                else if (previousCompilation is not null)
                {
                    documentSetChanged = true;
                }

                documentStates[doc.Id] = new DocumentState(doc.Version, tree);
            }
        }

        // remove cached documents no longer present
        var toRemove = documentStates.Keys.Where(id => !presentDocs.Contains(id)).ToList();
        if (toRemove.Count > 0)
            documentSetChanged = true;
        foreach (var id in toRemove)
            documentStates.Remove(id);

        var references = new List<MetadataReference>();
        references.AddRange(project.MetadataReferences);

        foreach (var (_, referencedCompilation) in referencedCompilations)
            references.Add(referencedCompilation.ToMetadataReference());

        var assemblyName = !string.IsNullOrWhiteSpace(project.AssemblyName)
            ? project.AssemblyName
            : project.Name;
        var compilation = Compilation.Create(
                assemblyName,
                syntaxTrees: [],
                references.ToArray(),
                [.. project.MacroReferences],
                project.CompilationOptions)
            .AddSyntaxTreesWithLocalMacros([.. syntaxTrees]);

        var generators = project.GeneratorReferences
            .SelectMany(static reference => reference.GetGenerators())
            .ToList();
        if (JavaScriptInteropGenerator.HasCandidate(compilation))
            generators.Add(new JavaScriptInteropGenerator());

        var orderedGenerators = generators
            .OrderBy(static generator => generator.GetType().FullName, StringComparer.Ordinal)
            .ToArray();
        if (orderedGenerators.Length > 0)
        {
            var driver = GeneratorDriver.Create(orderedGenerators, project.CompilerGeneratedFilesOutputPath).RunGeneratorsAndUpdateCompilation(
                compilation,
                out compilation,
                out _);
            var runResult = driver.GetRunResult();
            documentSetChanged |= !runResult.GeneratedSources.IsEmpty || !runResult.Diagnostics.IsEmpty;
        }

        if (previousCompilation is not null)
        {
            var plan = new Compilation.IncrementalCompilationPlan(
                reusedSyntaxTrees.ToImmutable(),
                changedSyntaxTrees.ToImmutable(),
                BlocksSemanticDiagnosticTransfer: documentSetChanged || projectReferencesChanged);
            var blocksSemanticStateReuse =
                plan.BlocksSemanticDiagnosticTransfer ||
                plan.ChangedSyntaxTrees.Any(static tree => tree.BlocksSemanticDiagnosticTransfer);
            if (blocksSemanticStateReuse)
            {
                var reason = plan.ChangedSyntaxTrees.Any(static tree =>
                    tree.CurrentTree.IncrementalParseFallbackReason != IncrementalParseFallbackReason.None)
                    ? "ParserRecovery"
                    : documentSetChanged
                        ? "DocumentSetChanged"
                        : projectReferencesChanged
                            ? "ProjectReferencesChanged"
                            : "DeclarationShapeChanged";
                Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
                    "compilation.incrementalFallback",
                    project.Name,
                    project.FilePath,
                    0,
                    $"reason={reason}, changedTrees={plan.ChangedSyntaxTrees.Length}"));
            }

            compilation.InitializeIncrementalStateFrom(previousCompilation, plan);
        }

        state.Version = project.Version;
        state.Compilation = compilation;
        state.ReferencedCompilations.Clear();
        foreach (var (referencedProjectId, referencedCompilation) in referencedCompilations)
            state.ReferencedCompilations.Add(referencedProjectId, referencedCompilation);
        compilationCache[project.Id] = state;

        return compilation;
    }

    private sealed class ProjectCompilationState
    {
        public VersionStamp Version;
        public Compilation? Compilation;
        public Dictionary<DocumentId, DocumentState> DocumentStates { get; } = new();
        public Dictionary<ProjectId, Compilation> ReferencedCompilations { get; } = new();
    }

    private ImmutableArray<(ProjectId ProjectId, Compilation Compilation)> GetReferencedCompilations(
        Project project,
        HashSet<ProjectId> building,
        Dictionary<ProjectId, ProjectCompilationState> compilationCache)
    {
        var references = ImmutableArray.CreateBuilder<(ProjectId, Compilation)>(project.ProjectReferences.Count);
        foreach (var projectReference in project.ProjectReferences)
        {
            var referencedProject = project.Solution.GetProject(projectReference.ProjectId)
                ?? throw new ArgumentException("Project not found", nameof(projectReference.ProjectId));
            references.Add((
                projectReference.ProjectId,
                GetCompilation(referencedProject, building, compilationCache)));
        }

        return references.MoveToImmutable();
    }

    private static bool ReferencedCompilationsMatch(
        ProjectCompilationState state,
        ImmutableArray<(ProjectId ProjectId, Compilation Compilation)> referencedCompilations)
    {
        if (state.ReferencedCompilations.Count != referencedCompilations.Length)
            return false;

        return referencedCompilations.All(reference =>
            state.ReferencedCompilations.TryGetValue(reference.ProjectId, out var previousCompilation) &&
            ReferenceEquals(previousCompilation, reference.Compilation));
    }

    private sealed record DocumentState(VersionStamp Version, SyntaxTree SyntaxTree);

    private readonly record struct ProjectDiagnosticsCacheKey(
        ProjectId ProjectId,
        VersionStamp Version,
        Compilation Compilation);

    private readonly record struct ProjectAnalyzerDiagnosticsCacheKey(
        ProjectId ProjectId,
        VersionStamp Version,
        Compilation Compilation,
        bool ReportSuppressedDiagnostics);

    private readonly record struct DocumentAnalyzerDiagnosticsCacheKey(
        ProjectId ProjectId,
        DocumentId DocumentId,
        VersionStamp ProjectVersion,
        VersionStamp DocumentVersion,
        Compilation Compilation,
        bool ReportSuppressedDiagnostics);

    /// <summary>
    /// Gets diagnostics for the specified project, including analyzer diagnostics.
    /// </summary>
    public ImmutableArray<Diagnostic> GetDiagnostics(
        ProjectId projectId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        var cacheKey = new ProjectDiagnosticsCacheKey(projectId, project.Version, compilation);
        if (analyzerOptions is null &&
            _projectDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
        {
            return cachedDiagnostics;
        }

        var succeeded = true;
        var diagnostics = compilation.GetDiagnostics(analyzerOptions, cancellationToken).ToHashSet();

        if (project.CompilationOptions?.RunAnalyzers != false)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var analyzer in reference.GetAnalyzers())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ShouldRunAnalyzer(analyzer, project.CompilationOptions, analyzerOptions))
                        continue;

                    var isInternalAnalyzer = AnalyzerDiagnosticIdValidator.IsInternalAnalyzer(analyzer);
                    IEnumerable<Diagnostic> analyzerDiagnostics;
                    try
                    {
                        var analyzerResult = analyzer.AnalyzeWithResult(compilation, syntaxTree: null, cancellationToken);
                        succeeded &= analyzerResult.Succeeded;
                        analyzerDiagnostics = analyzerResult.Diagnostics;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        succeeded = false;
                        // Analyzer failures should not stop normal compilation diagnostics.
                        continue;
                    }

                    foreach (var diagnostic in analyzerDiagnostics)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AnalyzerDiagnosticIdValidator.Validate(analyzer, diagnostic, isInternalAnalyzer);

                        var mapped = compilation.ApplyCompilationOptions(diagnostic, analyzerOptions?.ReportSuppressedDiagnostics ?? false);
                        if (mapped is not null)
                            diagnostics.Add(mapped);
                    }
                }
            }
        }

        var result = diagnostics.OrderBy(static diagnostic => diagnostic, DiagnosticComparer.Instance).ToImmutableArray();
        if (succeeded && analyzerOptions is null)
            _projectDiagnosticsCache[cacheKey] = result;

        return result;
    }

    internal ImmutableArray<Diagnostic> GetProjectAnalyzerDiagnostics(
        ProjectId projectId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        return GetProjectAnalyzerResult(project, compilation, analyzerOptions, cancellationToken).Diagnostics;
    }

    internal ImmutableArray<Diagnostic> GetProjectAnalyzerDiagnostics(
        ProjectId projectId,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
        => GetProjectAnalyzerResult(projectId, compilation, analyzerOptions, cancellationToken).Diagnostics;

    internal AnalyzerDiagnosticsResult GetProjectAnalyzerResult(
        ProjectId projectId,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        return GetProjectAnalyzerResult(project, compilation, analyzerOptions, cancellationToken);
    }

    private AnalyzerDiagnosticsResult GetProjectAnalyzerResult(
        Project project,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        var cacheKey = new ProjectAnalyzerDiagnosticsCacheKey(
            project.Id,
            project.Version,
            compilation,
            analyzerOptions?.ReportSuppressedDiagnostics ?? false);
        if (_projectAnalyzerDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
        {
            Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
                "projectAnalyzer.cacheHit",
                project.Name,
                project.FilePath,
                0,
                $"diagnostics={cachedDiagnostics.Length}"));
            return new(cachedDiagnostics, Succeeded: true);
        }

        var timestamp = Stopwatch.GetTimestamp();
        Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
            "projectAnalyzer.cacheMiss",
            project.Name,
            project.FilePath,
            0,
            $"projectVersion={project.Version}"));

        var diagnostics = new HashSet<Diagnostic>();
        var succeeded = true;
        AddDiagnostics(
            diagnostics,
            compilation.GetDiagnostics(analyzerOptions, cancellationToken),
            cancellationToken);

        if (project.CompilationOptions?.RunAnalyzers != false)
        {
            succeeded = RunProjectCompilationAnalyzerActions(
                project,
                compilation,
                diagnostics,
                analyzerOptions,
                cancellationToken);

            var analyzedTrees = new HashSet<SyntaxTree>(ReferenceEqualityComparer.Instance);
            foreach (var document in project.Documents.OrderBy(static document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                analyzedTrees.UnionWith(GetCompilationSyntaxTrees(document, compilation));
                var documentResult = GetDocumentAnalyzerResult(document, compilation, analyzerOptions,
                    allowBusySkip: false, semanticAccessAlreadyHeld: false, cancellationToken);
                succeeded &= documentResult.Succeeded;
                AddDiagnostics(diagnostics, documentResult.Diagnostics, cancellationToken);
            }

            // Generated trees belong to the compilation but have no authored workspace document.
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!analyzedTrees.Add(tree))
                    continue;

                var treeResult = DocumentAnalyzerDriver.RunWithResult(
                    project, tree, compilation, analyzerOptions, Services.WorkspaceEventSink, cancellationToken);
                succeeded &= treeResult.Succeeded;
                AddDiagnostics(diagnostics, treeResult.Diagnostics, cancellationToken);
            }
        }

        var result = diagnostics
            .OrderBy(static diagnostic => diagnostic, DiagnosticComparer.Instance)
            .ToImmutableArray();
        if (!succeeded)
            return new(result, Succeeded: false);

        _projectAnalyzerDiagnosticsCache[cacheKey] = result;

        Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
            "projectAnalyzer.cacheStore",
            project.Name,
            project.FilePath,
            Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds,
            $"diagnostics={result.Length}"));

        return new(result, Succeeded: true);
    }

    private bool RunProjectCompilationAnalyzerActions(
        Project project,
        Compilation compilation,
        HashSet<Diagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var reference in project.AnalyzerReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var analyzer in reference.GetAnalyzers().OrderBy(static analyzer => analyzer.GetType().FullName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldRunAnalyzer(analyzer, project.CompilationOptions, analyzerOptions))
                {
                    continue;
                }

                if (!analyzer.TryEnsureInitialized())
                {
                    succeeded = false;
                    continue;
                }
                if (analyzer.CompilationActions.Count == 0)
                    continue;

                var analyzerTimestamp = Stopwatch.GetTimestamp();
                var analyzerName = analyzer.GetType().FullName ?? analyzer.GetType().Name;
                var isInternalAnalyzer = AnalyzerDiagnosticIdValidator.IsInternalAnalyzer(analyzer);
                var analyzerDiagnostics = new HashSet<Diagnostic>();

                void ReportDiagnostic(Diagnostic diagnostic)
                {
                    AnalyzerDiagnosticIdValidator.Validate(analyzer, diagnostic, isInternalAnalyzer);

                    var mapped = compilation.ApplyCompilationOptions(
                        AnalyzerDiagnosticProperties.WithAnalyzerOrigin(diagnostic, analyzer),
                        analyzerOptions?.ReportSuppressedDiagnostics ?? false,
                        cancellationToken);
                    if (mapped is not null)
                        analyzerDiagnostics.Add(mapped);
                }

                try
                {
                    foreach (var action in analyzer.CompilationActions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        action(new CompilationAnalysisContext(
                            compilation,
                            syntaxTree: null,
                            ReportDiagnostic,
                            cancellationToken));
                    }

                    AddDiagnostics(diagnostics, analyzerDiagnostics, cancellationToken);
                    Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
                        "projectAnalyzer.compilationAction",
                        project.Name,
                        project.FilePath,
                        Stopwatch.GetElapsedTime(analyzerTimestamp).TotalMilliseconds,
                        $"analyzer={analyzerName}, diagnostics={analyzerDiagnostics.Count}, outcome=completed"));
                }
                catch (OperationCanceledException)
                {
                    Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
                        "projectAnalyzer.compilationAction",
                        project.Name,
                        project.FilePath,
                        Stopwatch.GetElapsedTime(analyzerTimestamp).TotalMilliseconds,
                        $"analyzer={analyzerName}, diagnostics={analyzerDiagnostics.Count}, outcome=canceled"));
                    throw;
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
                        "projectAnalyzer.compilationAction",
                        project.Name,
                        project.FilePath,
                        Stopwatch.GetElapsedTime(analyzerTimestamp).TotalMilliseconds,
                        $"analyzer={analyzerName}, diagnostics={analyzerDiagnostics.Count}, outcome=failed, exception={ex.GetType().Name}"));
                }
            }
        }
        return succeeded;
    }

    private static void AddDiagnostics(
        HashSet<Diagnostic> diagnostics,
        IEnumerable<Diagnostic> source,
        CancellationToken cancellationToken)
    {
        foreach (var diagnostic in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Add(diagnostic);
        }
    }

    public ImmutableArray<Diagnostic> GetDocumentDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var document = project.GetDocument(documentId)
            ?? throw new ArgumentException("Document not found", nameof(documentId));

        var syntaxTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        return compilation.GetDocumentDiagnostics(syntaxTree, analyzerOptions, cancellationToken);
    }

    public ImmutableArray<Diagnostic> GetDocumentDiagnosticsWithAnalyzers(
        ProjectId projectId,
        DocumentId documentId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var document = project.GetDocument(documentId)
            ?? throw new ArgumentException("Document not found", nameof(documentId));

        var syntaxTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        var diagnostics = compilation.GetDocumentDiagnostics(syntaxTree, analyzerOptions, cancellationToken).ToHashSet();

        if (project.CompilationOptions?.RunAnalyzers != false)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var analyzer in reference.GetAnalyzers())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ShouldRunAnalyzer(analyzer, project.CompilationOptions, analyzerOptions))
                        continue;

                    var isInternalAnalyzer = AnalyzerDiagnosticIdValidator.IsInternalAnalyzer(analyzer);
                    IEnumerable<Diagnostic> analyzerDiagnostics;
                    try
                    {
                        analyzerDiagnostics = analyzer.Analyze(compilation, syntaxTree, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Analyzer failures should not stop normal compilation diagnostics.
                        continue;
                    }

                    foreach (var diagnostic in analyzerDiagnostics)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AnalyzerDiagnosticIdValidator.Validate(analyzer, diagnostic, isInternalAnalyzer);

                        var mapped = compilation.ApplyCompilationOptions(diagnostic, analyzerOptions?.ReportSuppressedDiagnostics ?? false);
                        if (mapped is not null)
                            diagnostics.Add(mapped);
                    }
                }
            }
        }

        return diagnostics.OrderBy(static diagnostic => diagnostic, DiagnosticComparer.Instance).ToImmutableArray();
    }

    internal ImmutableArray<Diagnostic> GetDocumentAnalyzerDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
        => GetDocumentAnalyzerResult(projectId, documentId, analyzerOptions, cancellationToken).Diagnostics;

    internal AnalyzerDiagnosticsResult GetDocumentAnalyzerResult(
        ProjectId projectId,
        DocumentId documentId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var document = project.GetDocument(documentId)
            ?? throw new ArgumentException("Document not found", nameof(documentId));

        var syntaxTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");

        if (project.CompilationOptions?.RunAnalyzers == false)
            return new([], Succeeded: true);

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        var cacheKey = new DocumentAnalyzerDiagnosticsCacheKey(
            projectId,
            documentId,
            project.Version,
            document.Version,
            compilation,
            analyzerOptions?.ReportSuppressedDiagnostics ?? false);
        if (_documentAnalyzerDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.cacheHit",
                project,
                syntaxTree,
                elapsedMilliseconds: 0,
                $"diagnostics={cachedDiagnostics.Length}");
            return new(cachedDiagnostics, Succeeded: true);
        }

        var cacheMissTimestamp = Stopwatch.GetTimestamp();
        ReportWorkspaceEvent(
            "documentAnalyzer.cacheMiss",
            project,
            syntaxTree,
            elapsedMilliseconds: 0,
            $"projectVersion={project.Version}, documentVersion={document.Version}, allowBusySkip=false");

        var compilationSyntaxTrees = GetCompilationSyntaxTrees(document, compilation);
        AnalyzerDiagnosticsResult result;
        try
        {
            result = GetDocumentAnalyzerResult(
                project,
                compilationSyntaxTrees,
                compilation,
                analyzerOptions,
                allowBusySkip: false,
                semanticAccessAlreadyHeld: false,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.canceled",
                project,
                syntaxTree,
                Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
                "allowBusySkip=false");
            throw;
        }
        catch (Exception ex)
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.failure",
                project,
                syntaxTree,
                Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
                $"allowBusySkip=false, exception={ex.GetType().Name}");
            throw;
        }

        if (!result.Succeeded)
            return result;

        var diagnostics = result.Diagnostics;
        _documentAnalyzerDiagnosticsCache[cacheKey] = diagnostics;
        ReportWorkspaceEvent(
            "documentAnalyzer.cacheStore",
            project,
            syntaxTree,
            Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
            $"diagnostics={diagnostics.Length}, allowBusySkip=false");
        return result;
    }

    internal ImmutableArray<Diagnostic> GetDocumentAnalyzerDiagnostics(
        Document document,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        bool allowBusySkip = false,
        bool semanticAccessAlreadyHeld = false,
        CancellationToken cancellationToken = default)
        => GetDocumentAnalyzerResult(document, compilation, analyzerOptions, allowBusySkip, semanticAccessAlreadyHeld, cancellationToken).Diagnostics;

    internal AnalyzerDiagnosticsResult GetDocumentAnalyzerResult(
        Document document,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        bool allowBusySkip = false,
        bool semanticAccessAlreadyHeld = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(compilation);

        var syntaxTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");

        var compilationSyntaxTrees = GetCompilationSyntaxTrees(document, compilation);
        var compilationSyntaxTree = compilationSyntaxTrees[0];

        if (document.Project.CompilationOptions?.RunAnalyzers == false)
            return new([], Succeeded: true);

        var cacheKey = new DocumentAnalyzerDiagnosticsCacheKey(
            document.Project.Id,
            document.Id,
            document.Project.Version,
            document.Version,
            compilation,
            analyzerOptions?.ReportSuppressedDiagnostics ?? false);
        if (_documentAnalyzerDiagnosticsCache.TryGetValue(cacheKey, out var cachedDiagnostics))
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.cacheHit",
                document.Project,
                compilationSyntaxTree,
                elapsedMilliseconds: 0,
                $"diagnostics={cachedDiagnostics.Length}");
            return new(cachedDiagnostics, Succeeded: true);
        }

        var cacheMissTimestamp = Stopwatch.GetTimestamp();
        ReportWorkspaceEvent(
            "documentAnalyzer.cacheMiss",
            document.Project,
            compilationSyntaxTree,
            elapsedMilliseconds: 0,
            $"projectVersion={document.Project.Version}, documentVersion={document.Version}, allowBusySkip={allowBusySkip}");

        AnalyzerDiagnosticsResult result;
        try
        {
            result = GetDocumentAnalyzerResult(
                document.Project,
                compilationSyntaxTrees,
                compilation,
                analyzerOptions,
                allowBusySkip,
                semanticAccessAlreadyHeld,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.canceled",
                document.Project,
                compilationSyntaxTree,
                Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
                $"allowBusySkip={allowBusySkip}");
            throw;
        }
        catch (Exception ex)
        {
            ReportWorkspaceEvent(
                "documentAnalyzer.failure",
                document.Project,
                compilationSyntaxTree,
                Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
                $"allowBusySkip={allowBusySkip}, exception={ex.GetType().Name}");
            throw;
        }

        if (!result.Succeeded)
            return result;

        var diagnostics = result.Diagnostics;
        _documentAnalyzerDiagnosticsCache[cacheKey] = diagnostics;
        ReportWorkspaceEvent(
            "documentAnalyzer.cacheStore",
            document.Project,
            compilationSyntaxTree,
            Stopwatch.GetElapsedTime(cacheMissTimestamp).TotalMilliseconds,
            $"diagnostics={diagnostics.Length}, allowBusySkip={allowBusySkip}");
        return result;
    }

    private AnalyzerDiagnosticsResult GetDocumentAnalyzerResult(
        Project project,
        ImmutableArray<SyntaxTree> syntaxTrees,
        Compilation compilation,
        CompilationWithAnalyzersOptions? analyzerOptions,
        bool allowBusySkip,
        bool semanticAccessAlreadyHeld,
        CancellationToken cancellationToken)
    {
        var diagnostics = new HashSet<Diagnostic>();
        var succeeded = true;
        foreach (var syntaxTree in syntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = DocumentAnalyzerDriver.RunWithResult(
                project, syntaxTree, compilation, analyzerOptions, Services.WorkspaceEventSink,
                cancellationToken, allowBusySkip, semanticAccessAlreadyHeld && syntaxTrees.Length == 1);
            succeeded &= result.Succeeded;
            AddDiagnostics(diagnostics, result.Diagnostics, cancellationToken);
        }

        return new AnalyzerDiagnosticsResult(
            diagnostics.OrderBy(static diagnostic => diagnostic, DiagnosticComparer.Instance).ToImmutableArray(), succeeded);
    }

    private static ImmutableArray<SyntaxTree> GetCompilationSyntaxTrees(
        Document document,
        Compilation compilation)
    {
        var authoredTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");
        var syntaxTrees = ImmutableArray.CreateBuilder<SyntaxTree>(2);

        AddIfOwned(authoredTree);
        if (syntaxTrees.Count > 0)
            return syntaxTrees.ToImmutable();

        var filePath = document.FilePath;
        if (!string.IsNullOrWhiteSpace(filePath) && filePath != "file")
        {
            foreach (var candidate in compilation.SyntaxTrees.Concat(compilation.MacroSyntaxTrees))
            {
                if (string.Equals(candidate.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    Add(candidate);
            }
        }

        if (syntaxTrees.Count > 0)
            return syntaxTrees.ToImmutable();

        var partition = LocalMacroSyntaxClassifier.Partition(authoredTree);
        AddEquivalent(partition.ConsumerTree, compilation.SyntaxTrees);
        AddEquivalent(partition.MacroTree, compilation.MacroSyntaxTrees);

        if (syntaxTrees.Count == 0)
            throw new InvalidOperationException("Document syntax tree is not part of the compilation.");

        return syntaxTrees.ToImmutable();

        void AddIfOwned(SyntaxTree syntaxTree)
        {
            if (compilation.SyntaxTrees.Contains(syntaxTree) ||
                compilation.MacroSyntaxTrees.Contains(syntaxTree))
            {
                Add(syntaxTree);
            }
        }

        void AddEquivalent(SyntaxTree? projectedTree, IEnumerable<SyntaxTree> candidates)
        {
            if (projectedTree is null)
                return;

            var projectedText = projectedTree.GetText()?.ToString();
            if (projectedText is null)
                return;

            var match = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.GetText()?.ToString(), projectedText, StringComparison.Ordinal));
            if (match is not null)
                Add(match);
        }

        void Add(SyntaxTree syntaxTree)
        {
            if (!syntaxTrees.Contains(syntaxTree))
                syntaxTrees.Add(syntaxTree);
        }
    }

    private void ReportWorkspaceEvent(
        string operation,
        Project project,
        SyntaxTree syntaxTree,
        double elapsedMilliseconds,
        string detail)
        => Services.WorkspaceEventSink?.Report(new WorkspaceEvent(
            operation,
            project.Name,
            syntaxTree.FilePath,
            elapsedMilliseconds,
            detail));

    private static bool ShouldRunAnalyzer(
        DiagnosticAnalyzer analyzer,
        CompilationOptions? compilationOptions,
        CompilationWithAnalyzersOptions? analyzerOptions)
    {
        _ = analyzerOptions;

        var options = compilationOptions ?? new CompilationOptions();
        return !AnalyzerOptionUtilities.IsAnalyzerDisabled(analyzer.GetType(), options.DisabledAnalyzers) &&
            (analyzer is not ICompilationOptionsAwareAnalyzer awareAnalyzer ||
             awareAnalyzer.ShouldAnalyze(options));
    }

    public ImmutableArray<Diagnostic> GetDocumentSyntaxDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        var document = project.GetDocument(documentId)
            ?? throw new ArgumentException("Document not found", nameof(documentId));

        var syntaxTree = document.SyntaxTree
            ?? throw new InvalidOperationException("Document does not have a syntax tree.");

        var compilation = CreateAnalysisCompilation(project, new HashSet<ProjectId>());
        return compilation.GetSyntaxDiagnostics(syntaxTree, analyzerOptions, cancellationToken);
    }

    /// <summary>
    /// Gets code fixes for project diagnostics produced by the supplied providers.
    /// </summary>
    public ImmutableArray<CodeFix> GetCodeFixes(
        ProjectId projectId,
        IEnumerable<CodeFixProvider> providers,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (providers is null)
            throw new ArgumentNullException(nameof(providers));

        var diagnostics = GetDiagnostics(projectId, analyzerOptions, cancellationToken);
        return GetCodeFixes(projectId, providers, diagnostics, cancellationToken);
    }

    /// <summary>
    /// Gets code fixes for an existing diagnostic set without recomputing project diagnostics.
    /// </summary>
    public ImmutableArray<CodeFix> GetCodeFixes(
        ProjectId projectId,
        IEnumerable<CodeFixProvider> providers,
        IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        if (providers is null)
            throw new ArgumentNullException(nameof(providers));
        if (diagnostics is null)
            throw new ArgumentNullException(nameof(diagnostics));

        var project = CurrentSolution.GetProject(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));
        var providerList = providers
            .Concat(project.AnalyzerReferences.SelectMany(static reference => reference.GetCodeFixProviders()))
            .ToImmutableArray();
        var diagnosticList = diagnostics.ToImmutableArray();
        if (providerList.Length == 0)
            return ImmutableArray<CodeFix>.Empty;

        var providerMap = new Dictionary<string, List<CodeFixProvider>>(StringComparer.OrdinalIgnoreCase);
        var wildcardProviders = new List<CodeFixProvider>();
        foreach (var provider in providerList)
        {
            foreach (var id in provider.FixableDiagnosticIds)
            {
                if (string.Equals(id, "*", StringComparison.Ordinal))
                {
                    wildcardProviders.Add(provider);
                    continue;
                }

                if (!providerMap.TryGetValue(id, out var list))
                {
                    list = [];
                    providerMap.Add(id, list);
                }

                list.Add(provider);
            }
        }

        var fixes = ImmutableArray.CreateBuilder<CodeFix>();
        foreach (var diagnostic in diagnosticList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasSpecificProviders = providerMap.TryGetValue(diagnostic.Id, out var specificProviders);
            if (!hasSpecificProviders && wildcardProviders.Count == 0)
                continue;

            if (!TryGetDiagnosticDocument(project, diagnostic, out var document))
                continue;

            var seenProviders = new HashSet<CodeFixProvider>();

            if (hasSpecificProviders && specificProviders is not null)
            {
                foreach (var provider in specificProviders)
                    seenProviders.Add(provider);
            }

            foreach (var provider in wildcardProviders)
                seenProviders.Add(provider);

            foreach (var provider in seenProviders)
            {
                var actionBucket = new List<CodeAction>();
                var context = new CodeFixContext(
                    document,
                    diagnostic,
                    diagnosticList,
                    actionBucket.Add,
                    cancellationToken);

                provider.RegisterCodeFixes(context);

                foreach (var action in actionBucket)
                    fixes.Add(new CodeFix(document.Id, diagnostic, action, provider));
            }
        }

        return fixes
            .OrderBy(x => x.Diagnostic.Location)
            .ThenBy(x => x.Action.Title, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Gets context-driven refactorings for the specified document and selection span.
    /// </summary>
    public ImmutableArray<CodeRefactoring> GetRefactorings(
        DocumentId documentId,
        IEnumerable<CodeRefactoringProvider> providers,
        TextSpan span,
        CancellationToken cancellationToken = default)
    {
        if (providers is null)
            throw new ArgumentNullException(nameof(providers));

        var providerList = providers.ToImmutableArray();
        if (providerList.Length == 0)
            return ImmutableArray<CodeRefactoring>.Empty;

        var document = CurrentSolution.GetDocument(documentId)
            ?? throw new ArgumentException("Document not found", nameof(documentId));

        var refactorings = ImmutableArray.CreateBuilder<CodeRefactoring>();

        foreach (var provider in providerList)
        {
            var actionBucket = new List<CodeAction>();
            var context = new CodeRefactoringContext(
                document,
                span,
                actionBucket.Add,
                cancellationToken);

            try
            {
                provider.RegisterRefactorings(context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            foreach (var action in actionBucket)
                refactorings.Add(new CodeRefactoring(documentId, span, action, provider));
        }

        return refactorings
            .OrderBy(x => x.Action.Title, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Applies code fixes one-at-a-time, reanalyzing between each application.
    /// </summary>
    public ApplyCodeFixesResult ApplyCodeFixes(
        ProjectId projectId,
        IEnumerable<CodeFixProvider> providers,
        Func<CodeFix, bool>? predicate = null,
        int maxIterations = 100,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (providers is null)
            throw new ArgumentNullException(nameof(providers));
        if (maxIterations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIterations));

        var applied = 0;
        var solution = CurrentSolution;
        var appliedFixes = ImmutableArray.CreateBuilder<CodeFix>();

        for (var i = 0; i < maxIterations; i++)
        {
            TryApplyChanges(solution);

            var fixes = GetCodeFixes(projectId, providers, analyzerOptions, cancellationToken);
            if (predicate is not null)
                fixes = fixes.Where(predicate).ToImmutableArray();

            if (fixes.Length == 0)
                break;

            var selectedFix = fixes[0];
            var updated = selectedFix.Action.GetChangedSolution(solution, cancellationToken);
            if (updated.Version == solution.Version)
                break;

            solution = updated;
            applied++;
            appliedFixes.Add(selectedFix);
        }

        return new ApplyCodeFixesResult(solution, applied, appliedFixes.ToImmutable());
    }

    private static bool TryGetDiagnosticDocument(Project project, Diagnostic diagnostic, out Document document)
    {
        var sourceTree = diagnostic.Location.SourceTree;
        if (sourceTree is not null)
        {
            foreach (var candidate in project.Documents)
            {
                var candidateTree = candidate.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                if (ReferenceEquals(candidateTree, sourceTree))
                {
                    document = candidate;
                    return true;
                }
            }
        }

        if (diagnostic.Location.GetLineSpan() is { Path: { Length: > 0 } path })
        {
            var normalizedDiagnosticPath = Path.GetFullPath(path);
            foreach (var candidate in project.Documents)
            {
                if (string.IsNullOrWhiteSpace(candidate.FilePath))
                    continue;

                var normalizedCandidatePath = Path.GetFullPath(candidate.FilePath);
                if (string.Equals(normalizedCandidatePath, normalizedDiagnosticPath, StringComparison.OrdinalIgnoreCase))
                {
                    document = candidate;
                    return true;
                }
            }
        }

        document = null!;
        return false;
    }
}
