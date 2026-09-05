using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;
using CodeFixAction = Raven.CodeAnalysis.CodeAction;

namespace Raven.LanguageServer;

internal sealed class WorkspaceManager
{
    private const int MacroConsumerRefreshDelayMilliseconds = 750;

    private static readonly string MacroShadowOutputRoot = Path.Combine(Path.GetTempPath(), "raven-ls-macros");
    private static readonly TimeSpan ProjectOpenFailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly Regex SolutionProjectLinePattern = new(
        @"^\s*Project\(""[^""]+""\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)"",\s*""[^""]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> WorkspaceDiscoveryExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".debug",
        ".raven",
        ".raven-build",
        ".vs",
        ".vscode",
        "bin",
        "node_modules",
        "obj",
        "packages"
    };

    private static readonly HashSet<string> WatchedFileExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".debug",
        ".raven",
        ".raven-build",
        "bin",
        "node_modules",
        "obj",
        "packages"
    };

    private readonly RavenWorkspace _workspace;
    private readonly ILogger<WorkspaceManager> _logger;
    private readonly object _gate = new();
    private readonly ImmutableArray<CodeFixProvider> _builtInCodeFixProviders;
    private readonly ImmutableArray<CodeRefactoringProvider> _builtInCodeRefactoringProviders;
    private readonly Dictionary<string, ProjectId> _projectsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectId> _fileApplicationProjectsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<DocumentUri, OwnedDocument> _documents = new();
    private readonly ConcurrentDictionary<DocumentUri, byte> _openDocumentUris = new();
    private readonly ConcurrentDictionary<ProjectId, CancellationTokenSource> _pendingMacroConsumerRefreshes = new();
    private readonly Dictionary<string, FailedProjectOpen> _failedProjectOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _semanticDiagnosticsBlockedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProjectId, ImmutableDictionary<string, ReportDiagnostic>> _editorConfigDiagnosticOptionsByProject = new();
    private readonly Dictionary<ProjectId, ImmutableDictionary<string, bool>> _editorConfigGeneratedCodeOptionsByProject = new();
    private readonly PerformanceInstrumentation _compilerPerformanceInstrumentation = new();
    private ImmutableArray<string> _workspaceRoots = ImmutableArray<string>.Empty;
    private ProjectId? _fallbackProjectId;

    public WorkspaceManager(RavenWorkspace workspace, ILogger<WorkspaceManager> logger)
        : this(
            workspace,
            logger,
            BuiltInCodeFixProviders.CreateDefault(),
            BuiltInCodeRefactoringProviders.CreateDefault())
    {
    }

    internal WorkspaceManager(
        RavenWorkspace workspace,
        ILogger<WorkspaceManager> logger,
        ImmutableArray<CodeFixProvider> builtInCodeFixProviders,
        ImmutableArray<CodeRefactoringProvider> builtInCodeRefactoringProviders)
    {
        _workspace = workspace;
        _logger = logger;
        _builtInCodeFixProviders = builtInCodeFixProviders;
        _builtInCodeRefactoringProviders = builtInCodeRefactoringProviders;
    }

    public void Initialize(InitializeParams request)
    {
        var roots = ResolveRoots(request);
        _workspaceRoots = roots.ToImmutableArray();
        InitializeCore(roots);
        _logger.LogInformation("Workspace initialized with {RootCount} root(s).", roots.Count);
    }

    private void InitializeCore(IReadOnlyList<string> roots)
    {
        lock (_gate)
        {
            _workspace.OpenSolution(_workspace.CreateSolution());
            _projectsByRoot.Clear();
            _fileApplicationProjectsByRoot.Clear();
            _semanticDiagnosticsBlockedRoots.Clear();
            _editorConfigDiagnosticOptionsByProject.Clear();
            _editorConfigGeneratedCodeOptionsByProject.Clear();
            _fallbackProjectId = null;
            _documents.Clear();
            _openDocumentUris.Clear();
            var loadedProjects = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (TryOpenProjectsForRoot(root, loadedProjects, out var projectId, out var semanticDiagnosticsBlocked))
                {
                    _projectsByRoot[root] = projectId;
                }
                else
                {
                    if (semanticDiagnosticsBlocked)
                        _semanticDiagnosticsBlockedRoots.Add(root);
                }
            }

            if (_projectsByRoot.Count == 0 && roots.Count == 0)
                _fallbackProjectId = CreateFallbackProject();
        }
    }

    public Task<IReadOnlyList<DocumentUri>> ReloadForWatchedFilesAsync(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var relevantChanges = changes
            .Where(ShouldReloadForWatchedFileChange)
            .ToArray();

        if (relevantChanges.Length == 0 || _workspaceRoots.Length == 0)
            return Task.FromResult<IReadOnlyList<DocumentUri>>([]);

        lock (_gate)
        {
            var affectedProjectIds = new HashSet<ProjectId>();
            var requiresWorkspaceReload = false;
            var solutionGroupingChanged = false;
            foreach (var change in relevantChanges)
            {
                solutionGroupingChanged |= IsSolutionFilePath(change.Uri?.GetFileSystemPath());
                if (!TryApplyKnownSourceFileChange(change, affectedProjectIds))
                    requiresWorkspaceReload = true;
            }

            if (!requiresWorkspaceReload)
                return Task.FromResult(GetOpenDocumentUrisForProjects(affectedProjectIds));

            _failedProjectOpens.Clear();
            var openDocuments = new List<ReloadDocumentState>();
            foreach (var pair in _documents)
            {
                if (!_openDocumentUris.ContainsKey(pair.Key))
                    continue;

                var document = _workspace.CurrentSolution.GetDocument(pair.Value.DocumentId);
                if (document is null)
                    continue;

                openDocuments.Add(new ReloadDocumentState(
                    pair.Key,
                    document.Text.ToString(),
                    pair.Value.IsProjectDocument));
            }

            var reloadSnapshot = CaptureReloadSnapshot();
            InitializeCore(_workspaceRoots);

            if (!solutionGroupingChanged && ReloadDroppedExistingProject(reloadSnapshot.Solution))
            {
                RestoreReloadSnapshot(reloadSnapshot);
                _logger.LogWarning(
                    "Workspace reload could not replace every existing project. Preserving the last successful workspace until a later file change retries the reload.");
                return Task.FromResult<IReadOnlyList<DocumentUri>>(openDocuments.Select(static document => document.Uri).ToArray());
            }

#pragma warning disable VSTHRD103 // Keep reload state restoration synchronous while the workspace lock is held.
            foreach (var openDocument in openDocuments)
                _ = UpsertDocument(openDocument.Uri, SourceText.From(openDocument.Text));
#pragma warning restore VSTHRD103

            return Task.FromResult<IReadOnlyList<DocumentUri>>(openDocuments.Select(static document => document.Uri).ToArray());
        }
    }

    private WorkspaceReloadSnapshot CaptureReloadSnapshot()
        => new(
            _workspace.CurrentSolution,
            _projectsByRoot.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            _fileApplicationProjectsByRoot.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            _semanticDiagnosticsBlockedRoots.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            _editorConfigDiagnosticOptionsByProject.ToImmutableDictionary(),
            _editorConfigGeneratedCodeOptionsByProject.ToImmutableDictionary(),
            _documents.ToImmutableDictionary(),
            _fallbackProjectId);

    private bool ReloadDroppedExistingProject(Solution previousSolution)
    {
        var currentProjectPaths = _workspace.CurrentSolution.Projects
            .Select(static project => project.FilePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return previousSolution.Projects.Any(project =>
            !string.IsNullOrWhiteSpace(project.FilePath) &&
            File.Exists(project.FilePath) &&
            !currentProjectPaths.Contains(NormalizePath(project.FilePath)));
    }

    private void RestoreReloadSnapshot(WorkspaceReloadSnapshot snapshot)
    {
        _workspace.OpenSolution(snapshot.Solution);

        _projectsByRoot.Clear();
        foreach (var pair in snapshot.ProjectsByRoot)
            _projectsByRoot.Add(pair.Key, pair.Value);

        _fileApplicationProjectsByRoot.Clear();
        foreach (var pair in snapshot.FileApplicationProjectsByRoot)
            _fileApplicationProjectsByRoot.Add(pair.Key, pair.Value);

        _semanticDiagnosticsBlockedRoots.Clear();
        _semanticDiagnosticsBlockedRoots.UnionWith(snapshot.SemanticDiagnosticsBlockedRoots);

        _editorConfigDiagnosticOptionsByProject.Clear();
        foreach (var pair in snapshot.EditorConfigDiagnosticOptionsByProject)
            _editorConfigDiagnosticOptionsByProject.Add(pair.Key, pair.Value);

        _editorConfigGeneratedCodeOptionsByProject.Clear();
        foreach (var pair in snapshot.EditorConfigGeneratedCodeOptionsByProject)
            _editorConfigGeneratedCodeOptionsByProject.Add(pair.Key, pair.Value);

        _documents.Clear();
        foreach (var pair in snapshot.Documents)
            _documents.TryAdd(pair.Key, pair.Value);

        _fallbackProjectId = snapshot.FallbackProjectId;
    }

    private bool ShouldReloadForWatchedFileChange(FileEvent change)
    {
        var path = change.Uri?.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalizedPath = NormalizePath(path);
        if (!IsRelevantWatchedFileChangePath(normalizedPath) &&
            !IsObservedMacroFilePath(normalizedPath))
            return false;

        if (change.Type == FileChangeType.Changed &&
            RavenFileExtensions.HasRavenExtension(normalizedPath) &&
            IsOpenDocumentPath(normalizedPath))
        {
            return false;
        }

        return true;
    }

    private bool IsObservedMacroFilePath(string path)
    {
        if (IsWatchedFilePathExcluded(path))
            return false;

        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            var compilation = _workspace.GetCompilation(project.Id);
            if (compilation.GetObservedMacroFilePaths().Any(dependencyPath =>
                    string.Equals(
                        NormalizePath(dependencyPath),
                        path,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<DocumentUri> ApplyEditorConfigDiagnosticOptionsForWatchedFileChanges(IEnumerable<FileEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var changedPaths = changes
            .Select(change => change.Uri?.GetFileSystemPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path!))
            .ToArray();

        if (changedPaths.Length == 0 ||
            !changedPaths.Any(IsEditorConfigWatchedFileChangePath))
        {
            return [];
        }

        lock (_gate)
        {
            var solution = _workspace.CurrentSolution;
            var changed = false;

            foreach (var project in solution.Projects.ToArray())
            {
                var previousOptions = GetTrackedEditorConfigDiagnosticOptions(project);
                var currentOptions = LoadEditorConfigDiagnosticOptions(project);
                var previousGeneratedCode = GetTrackedEditorConfigGeneratedCodeOptions(project);
                var currentGeneratedCode = LoadEditorConfigGeneratedCodeOptions(project);

                if (DiagnosticOptionsEqual(previousOptions, currentOptions) &&
                    GeneratedCodeOptionsEqual(previousGeneratedCode, currentGeneratedCode))
                    continue;

                var compilationOptions = project.CompilationOptions ?? new CompilationOptions(OutputKind.ConsoleApplication);
                var mergedSpecificOptions = compilationOptions.SpecificDiagnosticOptions
                    .RemoveRange(previousOptions.Keys)
                    .SetItems(currentOptions);
                var updatedOptions = compilationOptions
                    .WithExactSpecificDiagnosticOptions(mergedSpecificOptions)
                    .WithGeneratedCodeOptions(currentGeneratedCode);

                solution = solution.WithCompilationOptions(project.Id, updatedOptions);
                _editorConfigDiagnosticOptionsByProject[project.Id] = currentOptions;
                _editorConfigGeneratedCodeOptionsByProject[project.Id] = currentGeneratedCode;
                changed = true;
            }

            if (!changed)
                return [];

            _workspace.TryApplyChanges(solution);
            return _openDocumentUris.Keys.ToArray();
        }
    }

    private bool TryOpenProjectsForRoot(
        string root,
        Dictionary<string, ProjectId> loadedProjects,
        out ProjectId projectId,
        out bool semanticDiagnosticsBlocked)
    {
        semanticDiagnosticsBlocked = false;
        var projectSystem = _workspace.Services.ProjectSystemService;
        if (projectSystem is null)
        {
            _logger.LogWarning("No project system service is available. Falling back to inferred workspace for root '{Root}'.", root);
            projectId = default;
            return false;
        }

        var projectFilePaths = FindWorkspaceProjectFiles(root, projectSystem);
        if (projectFilePaths.Length == 0)
        {
            projectId = default;
            return false;
        }
        semanticDiagnosticsBlocked = true;

        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedProjectPathsForRoot = new List<string>();
        var attemptedProjectOpen = false;
        var skippedProjectOpen = false;

        foreach (var projectFilePath in projectFilePaths)
        {
            if (ShouldSkipProjectOpen(projectFilePath))
            {
                skippedProjectOpen = true;
                continue;
            }

            attemptedProjectOpen = true;
            try
            {
                _ = OpenProjectWithReferences(projectFilePath, projectSystem, loadedProjects, stack);
                loadedProjectPathsForRoot.Add(projectFilePath);
                ClearProjectOpenFailure(projectFilePath);
            }
            catch (Exception ex)
            {
                stack.Remove(NormalizePath(projectFilePath));
                RecordProjectOpenFailure(projectFilePath, ex);
                _logger.LogWarning(
                    ex,
                    "Failed to open Raven project '{ProjectFilePath}' for root '{Root}'. Continuing with remaining projects.",
                    projectFilePath,
                    root);
            }
        }

        var successfullyLoadedCandidates = projectFilePaths
            .Where(path => loadedProjects.ContainsKey(NormalizePath(path)))
            .ToArray();

        if (successfullyLoadedCandidates.Length == 0)
        {
            if (attemptedProjectOpen || !skippedProjectOpen)
            {
                _logger.LogWarning("Failed to open any Raven project(s) for root '{Root}'. Falling back to inferred workspace.", root);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped reopening previously failed Raven project(s) for root '{Root}'. Falling back to inferred workspace until retry delay expires or files change.",
                    root);
            }

            projectId = default;
            return false;
        }

        var primaryProjectPath = SelectPrimaryProjectPath(root, successfullyLoadedCandidates);
        projectId = loadedProjects[NormalizePath(primaryProjectPath)];
        semanticDiagnosticsBlocked = false;
        _logger.LogInformation(
            "Opened {ProjectCount} Raven project(s) for root '{Root}'. Primary project: '{ProjectFilePath}'.",
            successfullyLoadedCandidates.Length,
            root,
            primaryProjectPath);
        return true;
    }

    private ProjectId OpenProjectWithReferences(
        string projectFilePath,
        IProjectSystemService projectSystem,
        Dictionary<string, ProjectId> loadedProjects,
        HashSet<string> stack)
    {
        var normalizedProjectPath = NormalizePath(projectFilePath);
        if (loadedProjects.TryGetValue(normalizedProjectPath, out var existing))
            return existing;

        if (ShouldSkipProjectOpen(normalizedProjectPath))
            throw new InvalidOperationException($"Skipping previously failed Raven project '{normalizedProjectPath}' until retry delay expires or files change.");

        if (!stack.Add(normalizedProjectPath))
            throw new InvalidOperationException($"Detected cyclic project references involving '{normalizedProjectPath}'.");

        foreach (var referencedProjectPath in projectSystem.GetProjectReferencePaths(normalizedProjectPath)
                     .Where(projectSystem.CanOpenProject))
        {
            try
            {
                _ = OpenProjectWithReferences(referencedProjectPath, projectSystem, loadedProjects, stack);
            }
            catch (Exception ex)
            {
                RecordProjectOpenFailure(referencedProjectPath, ex);
                throw;
            }
        }

        var projectId = projectSystem.OpenProject(_workspace, normalizedProjectPath);
        EnsureCompilerPerformanceInstrumentation(projectId);
        ApplyInitialEditorConfigDiagnosticOptions(projectId);
        EnsureRavenCoreReference(projectId);
        EnsureRavenMacrosReference(projectId);
        EnsureBuiltInAnalyzers(projectId);
        loadedProjects[normalizedProjectPath] = projectId;
        ClearProjectOpenFailure(normalizedProjectPath);
        stack.Remove(normalizedProjectPath);
        return projectId;
    }

    private bool ShouldSkipProjectOpen(string projectFilePath)
    {
        var normalizedProjectPath = NormalizePath(projectFilePath);
        if (!_failedProjectOpens.TryGetValue(normalizedProjectPath, out var failure))
            return false;

        if (DateTimeOffset.UtcNow >= failure.NextRetryUtc)
        {
            _failedProjectOpens.Remove(normalizedProjectPath);
            return false;
        }

        _logger.LogDebug(
            "Skipping Raven project '{ProjectFilePath}' open because the previous attempt failed. Next retry: {NextRetryUtc:O}.",
            normalizedProjectPath,
            failure.NextRetryUtc);
        return true;
    }

    private void RecordProjectOpenFailure(string projectFilePath, Exception exception)
    {
        var normalizedProjectPath = NormalizePath(projectFilePath);
        _failedProjectOpens[normalizedProjectPath] = new FailedProjectOpen(
            DateTimeOffset.UtcNow.Add(ProjectOpenFailureRetryDelay),
            exception.GetType().FullName ?? exception.GetType().Name);
    }

    private void ClearProjectOpenFailure(string projectFilePath)
        => _failedProjectOpens.Remove(NormalizePath(projectFilePath));

    private void EnsureCompilerPerformanceInstrumentation(ProjectId projectId)
    {
        var project = _workspace.CurrentSolution.GetProject(projectId);
        if (project is null)
            return;

        var compilationOptions = NormalizeCompilationOptionsForLanguageServer(
            project.CompilationOptions,
            _compilerPerformanceInstrumentation);
        if (ReferenceEquals(compilationOptions, project.CompilationOptions))
            return;

        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithCompilationOptions(projectId, compilationOptions));
    }

    internal static CompilationOptions NormalizeCompilationOptionsForLanguageServer(
        CompilationOptions? options,
        PerformanceInstrumentation performanceInstrumentation)
    {
        ArgumentNullException.ThrowIfNull(performanceInstrumentation);

        options ??= new CompilationOptions(OutputKind.ConsoleApplication);
        return ReferenceEquals(options.PerformanceInstrumentation, PerformanceInstrumentation.Disabled)
            ? options.WithPerformanceInstrumentation(performanceInstrumentation)
            : options;
    }

    private void EnsureRavenCoreReference(ProjectId projectId)
    {
        var project = _workspace.CurrentSolution.GetProject(projectId);
        if (project is null || project.CompilationOptions?.EmbedCoreTypes == true)
            return;

        var hasRavenCoreReference = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Any(static reference =>
                string.Equals(Path.GetFileName(reference.FilePath), "Raven.Core.dll", StringComparison.OrdinalIgnoreCase));

        if (hasRavenCoreReference)
            return;

        var preferredTfm = project.TargetFramework ?? TargetFrameworkResolver.ResolveLatestInstalledVersion().Moniker.ToTfm();
        var ravenCoreReferencePath = ResolveRavenCoreReferencePath(preferredTfm);
        if (string.IsNullOrWhiteSpace(ravenCoreReferencePath))
        {
            _logger.LogWarning(
                "Unable to locate a valid Raven.Core metadata reference for project '{ProjectName}'.",
                project.Name);
            return;
        }

        var solution = _workspace.CurrentSolution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(ravenCoreReferencePath));
        _workspace.TryApplyChanges(solution);
        _logger.LogDebug(
            "Added Raven.Core metadata reference '{ReferencePath}' for opened project '{ProjectName}'.",
            ravenCoreReferencePath,
            project.Name);
    }

    private void EnsureRavenMacrosReference(ProjectId projectId)
    {
        var project = _workspace.CurrentSolution.GetProject(projectId);
        if (project is null ||
            project.TargetFramework?.StartsWith("netnano", StringComparison.OrdinalIgnoreCase) == true)
            return;

        var preferredTfm = project.TargetFramework ??
            TargetFrameworkResolver.ResolveLatestInstalledVersion().Moniker.ToTfm();
        var ravenMacrosReferencePath = ResolveRavenMacrosReferencePath(preferredTfm);
        if (string.IsNullOrWhiteSpace(ravenMacrosReferencePath))
        {
            _logger.LogWarning(
                "Unable to locate a valid Raven.Macros metadata reference for project '{ProjectName}'.",
                project.Name);
            return;
        }

        var solution = _workspace.CurrentSolution;
        solution = ReplaceMetadataReferenceByFileName(
            solution,
            projectId,
            ravenMacrosReferencePath);
        solution = AddMetadataReferenceIfMissing(
            solution,
            projectId,
            typeof(Compilation).Assembly.Location);
        solution = ReplaceMacroReferenceByFileName(
            solution,
            projectId,
            ravenMacrosReferencePath);
        _workspace.TryApplyChanges(solution);
        _logger.LogDebug(
            "Added Raven.Macros and its compiler contract reference for opened project '{ProjectName}'.",
            project.Name);
    }

    private void EnsureBuiltInAnalyzers(ProjectId projectId)
    {
        var project = _workspace.CurrentSolution.GetProject(projectId);
        if (project is null)
            return;

        var updatedProject = project.AddBuiltInAnalyzers(enableSuggestions: true);
        if (_workspace.TryApplyChanges(updatedProject.Solution))
        {
            _logger.LogDebug(
                "Registered built-in analyzers for project '{ProjectName}'.",
                project.Name);
        }
    }

    internal static string[] FindWorkspaceProjectFiles(string root, IProjectSystemService projectSystem)
    {
        if (!Directory.Exists(root))
            return [];

        var solutionProjects = FindWorkspaceSolutionProjectFiles(root, projectSystem);
        if (solutionProjects.Length > 0)
            return solutionProjects;

        return FindWorkspaceProjectFilesByDirectory(root, projectSystem);
    }

    private static string[] FindWorkspaceSolutionProjectFiles(string root, IProjectSystemService projectSystem)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var solutionPath in FindWorkspaceSolutionFiles(root))
        {
            var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? root;
            try
            {
                if (string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    AddSlnxProjects(solutionPath, solutionDirectory, projectSystem, candidates);
                }
                else
                {
                    AddSlnProjects(solutionPath, solutionDirectory, projectSystem, candidates);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (XmlException)
            {
            }
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSlnProjects(
        string solutionPath,
        string solutionDirectory,
        IProjectSystemService projectSystem,
        HashSet<string> candidates)
    {
        foreach (var line in File.ReadLines(solutionPath))
        {
            var match = SolutionProjectLinePattern.Match(line);
            if (match.Success)
                TryAddSolutionProject(match.Groups["path"].Value, solutionDirectory, projectSystem, candidates);
        }
    }

    private static void AddSlnxProjects(
        string solutionPath,
        string solutionDirectory,
        IProjectSystemService projectSystem,
        HashSet<string> candidates)
    {
        var document = XDocument.Load(solutionPath, LoadOptions.None);
        if (!string.Equals(document.Root?.Name.LocalName, "Solution", StringComparison.Ordinal))
            return;

        foreach (var project in document.Descendants().Where(static element =>
                     string.Equals(element.Name.LocalName, "Project", StringComparison.Ordinal)))
        {
            var projectPath = project.Attributes().FirstOrDefault(static attribute =>
                string.Equals(attribute.Name.LocalName, "Path", StringComparison.Ordinal))?.Value;
            TryAddSolutionProject(projectPath, solutionDirectory, projectSystem, candidates);
        }
    }

    private static void TryAddSolutionProject(
        string? solutionProjectPath,
        string solutionDirectory,
        IProjectSystemService projectSystem,
        HashSet<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(solutionProjectPath))
            return;

        var normalizedSeparators = solutionProjectPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var projectPath = Path.IsPathRooted(normalizedSeparators)
            ? NormalizePath(normalizedSeparators)
            : NormalizePath(Path.Combine(solutionDirectory, normalizedSeparators));
        if (projectSystem.CanOpenProject(projectPath))
            candidates.Add(projectPath);
    }

    private static string[] FindWorkspaceSolutionFiles(string root)
    {
        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsSolutionFilePath)
                    .ToArray();
                directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            candidates.AddRange(files);

            foreach (var childDirectory in directories)
            {
                if (!IsWorkspaceDiscoveryDirectoryExcluded(childDirectory))
                    pending.Push(childDirectory);
            }
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] FindWorkspaceProjectFilesByDirectory(string root, IProjectSystemService projectSystem)
    {
        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(directory, "*.*proj", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (projectSystem.CanOpenProject(file))
                    candidates.Add(file);
            }

            foreach (var childDirectory in directories)
            {
                if (!IsWorkspaceDiscoveryDirectoryExcluded(childDirectory))
                    pending.Push(childDirectory);
            }
        }

        return candidates
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool ShouldReloadForWatchedFileChanges(IEnumerable<string> changedPaths)
        => changedPaths.Any(IsRelevantWatchedFileChangePath);

    internal static bool IsEditorConfigWatchedFileChangePath(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           !IsWatchedFilePathExcluded(path) &&
           string.Equals(Path.GetFileName(path), ".editorconfig", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRelevantWatchedFileChangePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (IsProjectAssetsFilePath(path))
            return true;

        if (IsWatchedFilePathExcluded(path))
            return false;

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".rvnproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase) ||
               RavenFileExtensions.HasRavenExtension(path);
    }

    private static bool IsProjectAssetsFilePath(string path)
        => string.Equals(
            Path.GetFileName(path),
            "project.assets.json",
            StringComparison.OrdinalIgnoreCase) &&
           EnumeratePathSegments(path).Contains("obj", StringComparer.OrdinalIgnoreCase);

    private static bool IsSolutionFilePath(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           (string.Equals(Path.GetExtension(path), ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(path), ".slnx", StringComparison.OrdinalIgnoreCase));

    private ImmutableDictionary<string, ReportDiagnostic> GetTrackedEditorConfigDiagnosticOptions(Project project)
    {
        if (_editorConfigDiagnosticOptionsByProject.TryGetValue(project.Id, out var options))
            return options;

        options = LoadEditorConfigDiagnosticOptions(project);
        _editorConfigDiagnosticOptionsByProject[project.Id] = options;
        return options;
    }

    private ImmutableDictionary<string, bool> GetTrackedEditorConfigGeneratedCodeOptions(Project project)
    {
        if (_editorConfigGeneratedCodeOptionsByProject.TryGetValue(project.Id, out var options))
            return options;

        options = LoadEditorConfigGeneratedCodeOptions(project);
        _editorConfigGeneratedCodeOptionsByProject[project.Id] = options;
        return options;
    }

    private void ApplyInitialEditorConfigDiagnosticOptions(ProjectId projectId)
    {
        var project = _workspace.CurrentSolution.GetProject(projectId);
        if (project is null)
            return;

        var editorConfigOptions = LoadEditorConfigDiagnosticOptions(project);
        var generatedCodeOptions = LoadEditorConfigGeneratedCodeOptions(project);
        _editorConfigDiagnosticOptionsByProject[projectId] = editorConfigOptions;
        _editorConfigGeneratedCodeOptionsByProject[projectId] = generatedCodeOptions;

        if (editorConfigOptions.Count == 0 && generatedCodeOptions.Count == 0)
            return;

        var compilationOptions = project.CompilationOptions ?? new CompilationOptions(OutputKind.ConsoleApplication);
        var mergedSpecificOptions = compilationOptions.SpecificDiagnosticOptions.SetItems(editorConfigOptions);
        if (DiagnosticOptionsEqual(compilationOptions.SpecificDiagnosticOptions, mergedSpecificOptions) &&
            GeneratedCodeOptionsEqual(compilationOptions.GeneratedCodeOptions, generatedCodeOptions))
            return;

        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithCompilationOptions(
            projectId,
            compilationOptions
                .WithExactSpecificDiagnosticOptions(mergedSpecificOptions)
                .WithGeneratedCodeOptions(generatedCodeOptions)));
    }

    private static ImmutableDictionary<string, ReportDiagnostic> LoadEditorConfigDiagnosticOptions(Project project)
        => EditorConfigDiagnosticOptions.LoadDiagnosticSeverityOptions(
            project.FilePath,
            project.Documents.Select(static document => document.FilePath));

    private static ImmutableDictionary<string, bool> LoadEditorConfigGeneratedCodeOptions(Project project)
        => EditorConfigDiagnosticOptions.LoadGeneratedCodeOptions(
            project.FilePath,
            project.Documents.Select(static document => document.FilePath));

    private static bool DiagnosticOptionsEqual(
        ImmutableDictionary<string, ReportDiagnostic> left,
        ImmutableDictionary<string, ReportDiagnostic> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || rightValue != value)
                return false;
        }

        return true;
    }

    private static bool GeneratedCodeOptionsEqual(
        ImmutableDictionary<string, bool> left,
        ImmutableDictionary<string, bool> right)
    {
        if (left.Count != right.Count)
            return false;
        return left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static bool IsWorkspaceDiscoveryDirectoryExcluded(string path)
    {
        var directoryName = Path.GetFileName(NormalizePath(path));
        return WorkspaceDiscoveryExcludedDirectoryNames.Contains(directoryName) ||
               directoryName.StartsWith("tmp-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWatchedFilePathExcluded(string path)
    {
        foreach (var segment in EnumeratePathSegments(path))
        {
            if (WatchedFileExcludedDirectoryNames.Contains(segment) ||
                segment.StartsWith("tmp-", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool TryApplyKnownSourceFileChange(FileEvent change, ISet<ProjectId> affectedProjectIds)
    {
        var path = change.Uri?.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(path))
            return true;

        var normalizedPath = NormalizePath(path);
        if (!RavenFileExtensions.HasRavenExtension(normalizedPath))
            return false;

        if (change.Type == FileChangeType.Changed &&
            IsOpenDocumentPath(normalizedPath))
        {
            return true;
        }

        if (change.Type == FileChangeType.Changed)
            return TryApplyKnownSourceFileTextChange(normalizedPath, affectedProjectIds);

        if (change.Type == FileChangeType.Deleted)
            return TryApplyKnownSourceFileDelete(normalizedPath, affectedProjectIds);

        if (change.Type == FileChangeType.Created)
            return TryApplyKnownSourceFileCreate(normalizedPath, affectedProjectIds);

        return false;
    }

    private bool TryApplyKnownSourceFileCreate(string normalizedPath, ISet<ProjectId> affectedProjectIds)
    {
        if (!File.Exists(normalizedPath))
            return true;

        if (!TryFindProjectIncludingSourceFile(normalizedPath, out var projectId))
            return false;

        var sourceText = SourceText.From(File.ReadAllText(normalizedPath));
        var solution = _workspace.CurrentSolution;
        var documentId = DocumentId.CreateNew(projectId);

        if (TryFindExistingDocument(
            solution,
            normalizedPath,
            out var existingDocument,
            out var existingProjectId))
        {
            sourceText = IsOpenDocumentPath(normalizedPath) ? existingDocument.Text : sourceText;
            if (existingProjectId == projectId)
            {
                documentId = existingDocument.Id;
                if (!HasSameText(existingDocument, sourceText))
                    solution = solution.WithDocumentText(existingDocument.Id, sourceText);
            }
            else
            {
                solution = solution.RemoveDocument(existingDocument.Id);
                solution = solution.AddDocument(
                    documentId,
                    Path.GetFileName(normalizedPath),
                    sourceText,
                    normalizedPath);
                affectedProjectIds.Add(existingProjectId);
            }
        }
        else
        {
            solution = solution.AddDocument(
                documentId,
                Path.GetFileName(normalizedPath),
                sourceText,
                normalizedPath);
        }

        _workspace.TryApplyChanges(solution);
        var addedDocument = _workspace.CurrentSolution.GetDocument(documentId);
        if (addedDocument is not null)
        {
            UpdateTrackedDocumentForPath(
                normalizedPath,
                documentId,
                projectId,
                addedDocument.Version,
                isProjectDocument: true);
        }

        affectedProjectIds.Add(projectId);
        RefreshMacroConsumersForProject(projectId, defer: false);
        return true;
    }

    private bool TryApplyKnownSourceFileTextChange(string normalizedPath, ISet<ProjectId> affectedProjectIds)
    {
        if (!File.Exists(normalizedPath))
            return TryApplyKnownSourceFileDelete(normalizedPath, affectedProjectIds);

        if (!TryFindExistingDocument(
            _workspace.CurrentSolution,
            normalizedPath,
            out var document,
            out var ownerProjectId))
        {
            return false;
        }

        var sourceText = SourceText.From(File.ReadAllText(normalizedPath));
        if (HasSameText(document, sourceText))
            return true;

        var solution = _workspace.CurrentSolution.WithDocumentText(document.Id, sourceText);
        _workspace.TryApplyChanges(solution);
        UpdateTrackedDocumentVersion(document.Id, ownerProjectId);
        affectedProjectIds.Add(ownerProjectId);
        RefreshMacroConsumersForProject(ownerProjectId, defer: false);
        return true;
    }

    private bool TryFindProjectIncludingSourceFile(string normalizedPath, out ProjectId projectId)
    {
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath) || !File.Exists(project.FilePath))
                continue;

            try
            {
                var evaluation = MsBuildProjectEvaluator.Evaluate(project.FilePath, RavenProjectConventions.Default, project.TargetFramework);
                if (evaluation.Documents.Any(document =>
                    !string.IsNullOrWhiteSpace(document.FilePath) &&
                    string.Equals(NormalizePath(document.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    projectId = project.Id;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Failed to evaluate project '{ProjectFilePath}' while handling created source file '{SourceFilePath}'.",
                    project.FilePath,
                    normalizedPath);
            }
        }

        projectId = default;
        return false;
    }

    private bool TryApplyKnownSourceFileDelete(string normalizedPath, ISet<ProjectId> affectedProjectIds)
    {
        if (!TryFindExistingDocument(
            _workspace.CurrentSolution,
            normalizedPath,
            out var document,
            out var ownerProjectId))
        {
            return true;
        }

        var solution = _workspace.CurrentSolution.RemoveDocument(document.Id);
        _workspace.TryApplyChanges(solution);
        RemoveTrackedDocuments(document.Id, normalizedPath);
        affectedProjectIds.Add(ownerProjectId);
        RefreshMacroConsumersForProject(ownerProjectId, defer: false);
        return true;
    }

    private bool IsOpenDocumentPath(string normalizedPath)
    {
        foreach (var uri in _openDocumentUris.Keys)
        {
            var openPath = uri.GetFileSystemPath();
            if (!string.IsNullOrWhiteSpace(openPath) &&
                string.Equals(NormalizePath(openPath), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumeratePathSegments(string path)
    {
        var normalizedPath = NormalizePath(path);
        foreach (var segment in normalizedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (!string.IsNullOrWhiteSpace(segment))
                yield return segment;
        }
    }

    internal static string SelectPrimaryProjectPath(string root, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("At least one project candidate is required.");

        var normalizedRoot = NormalizePath(root);
        var directoryName = Path.GetFileName(normalizedRoot);
        var topLevelCandidates = candidates
            .Where(path => string.Equals(
                NormalizePath(Path.GetDirectoryName(path) ?? string.Empty),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var candidateSet = topLevelCandidates.Length > 0 ? topLevelCandidates : candidates.ToArray();

        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var preferred = candidateSet.FirstOrDefault(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), directoryName, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        return candidateSet
            .OrderBy(path => GetDirectoryDepth(normalizedRoot, path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public Task<Document> UpsertDocumentAsync(DocumentUri uri, string text)
        => UpsertDocumentAsync(uri, SourceText.From(text), deferMacroConsumerRefresh: false);

    internal Task<Document> UpsertDocumentAsync(DocumentUri uri, SourceText sourceText, bool deferMacroConsumerRefresh = false)
        => Task.FromResult(UpsertDocument(uri, sourceText, deferMacroConsumerRefresh));

    internal Task<DocumentUpsertResult> UpsertDocumentWithResultAsync(DocumentUri uri, SourceText sourceText, bool deferMacroConsumerRefresh = false)
        => Task.FromResult(UpsertDocumentWithResult(uri, sourceText, deferMacroConsumerRefresh));

    internal Document UpsertDocument(DocumentUri uri, SourceText sourceText, bool deferMacroConsumerRefresh = false)
        => UpsertDocumentWithResult(uri, sourceText, deferMacroConsumerRefresh).Document;

    internal DocumentUpsertResult UpsertDocumentWithResult(DocumentUri uri, SourceText sourceText, bool deferMacroConsumerRefresh = false)
    {
        var filePath = uri.GetFileSystemPath();
        var name = Path.GetFileName(filePath) ?? filePath ?? $"document{RavenFileExtensions.Raven}";

        _openDocumentUris[uri] = 0;

        lock (_gate)
        {
            var ownerProject = ResolveProjectForUri(uri);
            var solution = _workspace.CurrentSolution;
            var normalizedFilePath = !string.IsNullOrWhiteSpace(filePath) ? NormalizePath(filePath) : null;
            OwnedDocument? staleOwnedDocument = null;

            if (_documents.TryGetValue(uri, out var existing))
            {
                if (existing.ProjectId == ownerProject)
                {
                    var currentDocument = solution.GetDocument(existing.DocumentId);
                    if (currentDocument is not null &&
                        HasSameText(currentDocument, sourceText))
                    {
                        _documents[uri] = new OwnedDocument(currentDocument.Id, ownerProject, currentDocument.Version, IsProjectDocument: existing.IsProjectDocument);
                        return new DocumentUpsertResult(
                            currentDocument,
                            TextChanged: false,
                            ProjectChanged: false,
                            AddedDocument: false);
                    }

                    solution = solution.WithDocumentText(existing.DocumentId, sourceText);
                    _workspace.TryApplyChanges(solution);
                    var updatedDocument = _workspace.CurrentSolution.GetDocument(existing.DocumentId)!;
                    _documents[uri] = new OwnedDocument(updatedDocument.Id, ownerProject, updatedDocument.Version, IsProjectDocument: existing.IsProjectDocument);
                    RefreshMacroConsumersForProject(ownerProject, deferMacroConsumerRefresh);
                    return new DocumentUpsertResult(
                        updatedDocument,
                        TextChanged: true,
                        ProjectChanged: true,
                        AddedDocument: false);
                }

                staleOwnedDocument = existing;
                _documents.TryRemove(uri, out _);
            }

            if (normalizedFilePath is not null &&
                TryFindExistingDocument(solution, ownerProject, normalizedFilePath, out var existingDocument, out var existingOwnerProject))
            {
                if (HasSameText(existingDocument, sourceText) &&
                    staleOwnedDocument is not { IsProjectDocument: false })
                {
                    _documents[uri] = new OwnedDocument(existingDocument.Id, existingOwnerProject, existingDocument.Version, IsProjectDocument: true);
                    return new DocumentUpsertResult(
                        existingDocument,
                        TextChanged: false,
                        ProjectChanged: false,
                        AddedDocument: false);
                }

                solution = solution.WithDocumentText(existingDocument.Id, sourceText);
                if (staleOwnedDocument is { IsProjectDocument: false } stale
                    && stale.DocumentId != existingDocument.Id
                    && solution.GetDocument(stale.DocumentId) is not null)
                {
                    solution = solution.RemoveDocument(stale.DocumentId);
                }
                _workspace.TryApplyChanges(solution);
                var updatedDocument = _workspace.CurrentSolution.GetDocument(existingDocument.Id)!;
                _documents[uri] = new OwnedDocument(updatedDocument.Id, existingOwnerProject, updatedDocument.Version, IsProjectDocument: true);
                RefreshMacroConsumersForProject(existingOwnerProject, deferMacroConsumerRefresh);
                return new DocumentUpsertResult(
                    updatedDocument,
                    TextChanged: true,
                    ProjectChanged: true,
                    AddedDocument: false);
            }

            if (staleOwnedDocument is { IsProjectDocument: false } staleDocument
                && solution.GetDocument(staleDocument.DocumentId) is not null)
            {
                solution = solution.RemoveDocument(staleDocument.DocumentId);
            }

            var documentId = DocumentId.CreateNew(ownerProject);
            solution = solution.AddDocument(documentId, name, sourceText, filePath);
            _workspace.TryApplyChanges(solution);
            var addedDocument = _workspace.CurrentSolution.GetDocument(documentId)!;
            _documents[uri] = new OwnedDocument(documentId, ownerProject, addedDocument.Version, IsProjectDocument: false);

            return new DocumentUpsertResult(
                addedDocument,
                TextChanged: true,
                ProjectChanged: true,
                AddedDocument: true);
        }
    }

    private static bool HasSameText(Document document, SourceText sourceText)
#pragma warning disable VSTHRD002 // UpsertDocumentWithResult is the synchronous workspace mutation core.
        => document.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ContentEquals(sourceText);
#pragma warning restore VSTHRD002

    private static bool TryFindExistingDocument(
        Solution solution,
        ProjectId preferredProjectId,
        string normalizedFilePath,
        out Document document,
        out ProjectId ownerProjectId)
    {
        // Prefer a document that is already part of the resolved owner project.
        var preferredProject = solution.GetProject(preferredProjectId);
        if (preferredProject is not null)
        {
            var preferredMatch = preferredProject.Documents.FirstOrDefault(doc =>
                !string.IsNullOrWhiteSpace(doc.FilePath) &&
                string.Equals(NormalizePath(doc.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase));
            if (preferredMatch is not null)
            {
                document = preferredMatch;
                ownerProjectId = preferredProjectId;
                return true;
            }
        }

        foreach (var project in solution.Projects)
        {
            var match = project.Documents.FirstOrDefault(doc =>
                !string.IsNullOrWhiteSpace(doc.FilePath) &&
                string.Equals(NormalizePath(doc.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                document = match;
                ownerProjectId = project.Id;
                return true;
            }
        }

        document = null!;
        ownerProjectId = default;
        return false;
    }

    private static bool TryFindExistingDocument(
        Solution solution,
        string normalizedFilePath,
        out Document document,
        out ProjectId ownerProjectId)
    {
        foreach (var project in solution.Projects)
        {
            var match = project.Documents.FirstOrDefault(doc =>
                !string.IsNullOrWhiteSpace(doc.FilePath) &&
                string.Equals(NormalizePath(doc.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                document = match;
                ownerProjectId = project.Id;
                return true;
            }
        }

        document = null!;
        ownerProjectId = default;
        return false;
    }

    private void UpdateTrackedDocumentVersion(DocumentId documentId, ProjectId ownerProjectId)
    {
        var updatedDocument = _workspace.CurrentSolution.GetDocument(documentId);
        if (updatedDocument is null)
            return;

        foreach (var pair in _documents.ToArray())
        {
            if (pair.Value.DocumentId == documentId)
            {
                _documents[pair.Key] = new OwnedDocument(
                    documentId,
                    ownerProjectId,
                    updatedDocument.Version,
                    IsProjectDocument: pair.Value.IsProjectDocument);
            }
        }
    }

    private void UpdateTrackedDocumentForPath(
        string normalizedPath,
        DocumentId documentId,
        ProjectId ownerProjectId,
        VersionStamp version,
        bool isProjectDocument)
    {
        foreach (var pair in _documents.ToArray())
        {
            var openPath = pair.Key.GetFileSystemPath();
            if (pair.Value.DocumentId == documentId ||
                (!string.IsNullOrWhiteSpace(openPath) &&
                 string.Equals(NormalizePath(openPath), normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                _documents[pair.Key] = new OwnedDocument(
                    documentId,
                    ownerProjectId,
                    version,
                    IsProjectDocument: isProjectDocument);
            }
        }
    }

    private void RemoveTrackedDocuments(DocumentId documentId, string normalizedPath)
    {
        foreach (var pair in _documents.ToArray())
        {
            var openPath = pair.Key.GetFileSystemPath();
            if (pair.Value.DocumentId == documentId ||
                (!string.IsNullOrWhiteSpace(openPath) &&
                 string.Equals(NormalizePath(openPath), normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                _documents.TryRemove(pair.Key, out _);
                _openDocumentUris.TryRemove(pair.Key, out _);
            }
        }
    }

    public bool TryGetDocument(DocumentUri uri, out Document? document)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            document = _workspace.CurrentSolution.GetDocument(ownedDocument.DocumentId);
            return document is not null;
        }

        document = null;
        return false;
    }

    public IReadOnlyList<DocumentUri> GetOpenDocumentUrisInSameProject(DocumentUri uri, bool excludeSelf)
    {
        lock (_gate)
        {
            if (!TryResolveOwnedDocument(uri, out var ownedDocument))
                return [];

            return _openDocumentUris.Keys
                .Where(openUri =>
                    (!excludeSelf || openUri != uri) &&
                    _documents.TryGetValue(openUri, out var openDocument) &&
                    openDocument.ProjectId == ownedDocument.ProjectId)
                .ToArray();
        }
    }

    private IReadOnlyList<DocumentUri> GetOpenDocumentUrisForProjects(IReadOnlySet<ProjectId> projectIds)
    {
        if (projectIds.Count == 0)
            return [];

        return _openDocumentUris.Keys
            .Where(uri =>
                _documents.TryGetValue(uri, out var openDocument) &&
                projectIds.Contains(openDocument.ProjectId))
            .ToArray();
    }

    public bool TryGetDocumentContext(DocumentUri uri, out Document? document, out Compilation? compilation)
    {
        lock (_gate)
        {
            if (TryResolveOwnedDocument(uri, out var ownedDocument))
            {
                var solution = _workspace.CurrentSolution;
                document = solution.GetDocument(ownedDocument.DocumentId);
                if (document is not null)
                {
                    compilation = _workspace.GetCompilation(document.Project);
                    return true;
                }
            }
        }

        document = null;
        compilation = null;
        return false;
    }

    public bool TryGetCompilation(DocumentUri uri, out Compilation? compilation)
    {
        lock (_gate)
        {
            if (TryResolveOwnedDocument(uri, out var ownedDocument))
            {
                var project = _workspace.CurrentSolution.GetProject(ownedDocument.ProjectId);
                if (project is not null)
                {
                    compilation = _workspace.GetCompilation(project);
                    return true;
                }
            }
        }

        compilation = null;
        return false;
    }

    public bool TryGetDiagnostics(
        DocumentUri uri,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(uri, out var ownedDocument))
        {
            return TryGetDiagnostics(
                ownedDocument.ProjectId,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    internal bool TryGetProjectAnalyzerDiagnostics(
        DocumentUri uri,
        Compilation compilation,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(uri, out var ownedDocument))
        {
            return TryGetProjectAnalyzerDiagnostics(
                ownedDocument.ProjectId,
                compilation,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    internal bool TryGetProjectAnalyzerDiagnostics(
        Document document,
        Compilation compilation,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
        => TryGetProjectAnalyzerDiagnostics(
            document.Project.Id,
            compilation,
            out diagnostics,
            analyzerOptions,
            cancellationToken);

    public bool TryGetDocumentDiagnostics(
        DocumentUri uri,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            return TryGetDocumentDiagnostics(
                ownedDocument.ProjectId,
                ownedDocument.DocumentId,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    public bool TryGetDocumentDiagnosticsWithAnalyzers(
        DocumentUri uri,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            return TryGetDocumentDiagnosticsWithAnalyzers(
                ownedDocument.ProjectId,
                ownedDocument.DocumentId,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    public bool TryGetDocumentAnalyzerDiagnostics(
        DocumentUri uri,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            return TryGetDocumentAnalyzerDiagnostics(
                ownedDocument.ProjectId,
                ownedDocument.DocumentId,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    internal bool CanPublishSemanticDiagnostics(DocumentUri uri)
    {
        var documentPath = uri.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(documentPath))
            return true;

        var normalizedPath = NormalizePath(documentPath);
        lock (_gate)
        {
            return !_semanticDiagnosticsBlockedRoots.Any(root => IsWithinRoot(normalizedPath, root));
        }
    }

    internal bool TryGetDocumentAnalyzerDiagnostics(
        Document document,
        Compilation compilation,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        bool allowBusySkip = false,
        bool semanticAccessAlreadyHeld = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = _workspace.GetDocumentAnalyzerResult(
                document,
                compilation,
                analyzerOptions,
                allowBusySkip,
                semanticAccessAlreadyHeld,
                cancellationToken);
            diagnostics = result.Diagnostics;
            return result.Succeeded;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    public bool TryGetDocumentSyntaxDiagnostics(
        DocumentUri uri,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            return TryGetDocumentSyntaxDiagnostics(
                ownedDocument.ProjectId,
                ownedDocument.DocumentId,
                out diagnostics,
                analyzerOptions,
                cancellationToken);
        }

        diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
        return false;
    }

    private bool TryGetDiagnostics(
        ProjectId projectId,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            diagnostics = _workspace.GetDiagnostics(projectId, analyzerOptions, cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    private bool TryGetProjectAnalyzerDiagnostics(
        ProjectId projectId,
        Compilation compilation,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = _workspace.GetProjectAnalyzerResult(projectId, compilation, analyzerOptions, cancellationToken);
            diagnostics = result.Diagnostics;
            return result.Succeeded;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    private bool TryGetDocumentDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            diagnostics = _workspace.GetDocumentDiagnostics(projectId, documentId, analyzerOptions, cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    private bool TryGetDocumentDiagnosticsWithAnalyzers(
        ProjectId projectId,
        DocumentId documentId,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            diagnostics = _workspace.GetDocumentDiagnosticsWithAnalyzers(projectId, documentId, analyzerOptions, cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    private bool TryGetDocumentAnalyzerDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = _workspace.GetDocumentAnalyzerResult(projectId, documentId, analyzerOptions, cancellationToken);
            diagnostics = result.Diagnostics;
            return result.Succeeded;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    private bool TryGetDocumentSyntaxDiagnostics(
        ProjectId projectId,
        DocumentId documentId,
        out ImmutableArray<CodeDiagnostic> diagnostics,
        CompilationWithAnalyzersOptions? analyzerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            diagnostics = _workspace.GetDocumentSyntaxDiagnostics(projectId, documentId, analyzerOptions, cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
        catch (InvalidOperationException)
        {
            diagnostics = ImmutableArray<CodeDiagnostic>.Empty;
            return false;
        }
    }

    public bool TryGetCodeFixes(
        DocumentUri uri,
        out ImmutableArray<CodeFix> codeFixes,
        CompilationWithAnalyzersOptions? analyzerOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            codeFixes = _workspace
                .GetCodeFixes(ownedDocument.ProjectId, _builtInCodeFixProviders, analyzerOptions, cancellationToken)
                .Where(fix => fix.DocumentId == ownedDocument.DocumentId)
                .ToImmutableArray();
            return true;
        }

        codeFixes = ImmutableArray<CodeFix>.Empty;
        return false;
    }

    public bool TryGetCodeFixesForDiagnostics(
        DocumentUri uri,
        IEnumerable<CodeDiagnostic> diagnostics,
        out ImmutableArray<CodeFix> codeFixes,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument))
        {
            codeFixes = _workspace
                .GetCodeFixes(ownedDocument.ProjectId, _builtInCodeFixProviders, diagnostics, cancellationToken)
                .Where(fix => fix.DocumentId == ownedDocument.DocumentId)
                .ToImmutableArray();
            return true;
        }

        codeFixes = ImmutableArray<CodeFix>.Empty;
        return false;
    }

    public bool TryGetFixAll(
        DocumentUri uri,
        CodeFix triggerFix,
        FixAllScope scope,
        out CodeFixAction? action,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveOwnedDocument(uri, out var ownedDocument) &&
            triggerFix.Action.EquivalenceKey is { Length: > 0 } equivalenceKey)
        {
            action = _workspace.GetFixAll(
                ownedDocument.ProjectId,
                triggerFix.Provider,
                scope,
                equivalenceKey,
                ownedDocument.DocumentId,
                cancellationToken: cancellationToken);
            return true;
        }

        action = null;
        return false;
    }

    public bool TryGetRefactorings(
        DocumentUri uri,
        TextSpan span,
        out ImmutableArray<CodeRefactoring> refactorings,
        CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(uri, out var ownedDocument))
        {
            refactorings = _workspace
                .GetRefactorings(ownedDocument.DocumentId, _builtInCodeRefactoringProviders, span, cancellationToken);
            return true;
        }

        refactorings = ImmutableArray<CodeRefactoring>.Empty;
        return false;
    }

    public bool RemoveDocument(DocumentUri uri)
    {
        _openDocumentUris.TryRemove(uri, out _);

        if (!_documents.TryRemove(uri, out var ownedDocument))
            return false;

        lock (_gate)
        {
            if (ownedDocument.IsProjectDocument)
            {
                var document = _workspace.CurrentSolution.GetDocument(ownedDocument.DocumentId);
                if (document?.FilePath is { } filePath && File.Exists(filePath))
                {
                    var sourceText = SourceText.From(File.ReadAllText(filePath));
                    var solution = _workspace.CurrentSolution.WithDocumentText(ownedDocument.DocumentId, sourceText);
                    _workspace.TryApplyChanges(solution);
                    RefreshMacroConsumersForProject(ownedDocument.ProjectId, defer: false);
                }
            }
            else
            {
                var solution = _workspace.CurrentSolution;
                var filePath = uri.GetFileSystemPath();
                var normalizedFilePath = !string.IsNullOrWhiteSpace(filePath)
                    ? NormalizePath(filePath)
                    : null;
                if (normalizedFilePath is not null &&
                    _fileApplicationProjectsByRoot.TryGetValue(normalizedFilePath, out var fileApplicationProjectId) &&
                    fileApplicationProjectId == ownedDocument.ProjectId)
                {
                    solution = solution.RemoveProject(ownedDocument.ProjectId);
                    _fileApplicationProjectsByRoot.Remove(normalizedFilePath);
                }
                else
                {
                    solution = solution.RemoveDocument(ownedDocument.DocumentId);
                }

                _workspace.TryApplyChanges(solution);
            }
        }

        return true;
    }

    private void RefreshMacroConsumersForProject(ProjectId changedProjectId, bool defer)
    {
        if (defer)
        {
            ScheduleMacroConsumersRefresh(changedProjectId);
            return;
        }

        CancelPendingMacroConsumerRefresh(changedProjectId);
        RefreshMacroConsumersForProjectCore(changedProjectId);
    }

    private void ScheduleMacroConsumersRefresh(ProjectId changedProjectId)
    {
        var source = new CancellationTokenSource();
        var previous = _pendingMacroConsumerRefreshes.AddOrUpdate(
            changedProjectId,
            source,
            (_, existing) =>
            {
                try
                {
                    existing.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                return source;
            });

        if (!ReferenceEquals(previous, source))
        {
            source.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MacroConsumerRefreshDelayMilliseconds, source.Token).ConfigureAwait(false);

                lock (_gate)
                {
                    RefreshMacroConsumersForProjectCore(changedProjectId);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_pendingMacroConsumerRefreshes.TryGetValue(changedProjectId, out var active) &&
                    ReferenceEquals(active, source))
                {
                    _pendingMacroConsumerRefreshes.TryRemove(changedProjectId, out _);
                }

                source.Dispose();
            }
        }, CancellationToken.None);
    }

    private void CancelPendingMacroConsumerRefresh(ProjectId changedProjectId)
    {
        if (!_pendingMacroConsumerRefreshes.TryRemove(changedProjectId, out var source))
            return;

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    internal async Task FlushPendingMacroConsumerRefreshesAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var projectIds = _pendingMacroConsumerRefreshes.Keys.ToArray();
            if (projectIds.Length == 0)
                return;

            foreach (var projectId in projectIds)
            {
                CancelPendingMacroConsumerRefresh(projectId);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    RefreshMacroConsumersForProjectCore(projectId);
                }
            }

            await Task.Yield();
        }
    }

    private void RefreshMacroConsumersForProjectCore(ProjectId changedProjectId)
    {
        var changedProject = _workspace.CurrentSolution.GetProject(changedProjectId);
        if (changedProject?.FilePath is null)
            return;

        var compilation = _workspace.GetCompilation(changedProject.Id);

        var sourceProjectPath = NormalizePath(changedProject.FilePath);
        var consumers = _workspace.CurrentSolution.Projects
            .Where(project => project.MacroReferences.Any(reference =>
                !string.IsNullOrWhiteSpace(reference.SourceProjectFilePath) &&
                string.Equals(NormalizePath(reference.SourceProjectFilePath), sourceProjectPath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (consumers.Length == 0)
            return;

        try
        {
            var outputPath = EmitMacroProjectOutput(changedProject);
            var solution = _workspace.CurrentSolution;
            var updatedConsumers = 0;

            foreach (var consumer in consumers)
            {
                var updatedReferences = consumer.MacroReferences
                    .Select(reference =>
                        !string.IsNullOrWhiteSpace(reference.SourceProjectFilePath) &&
                        string.Equals(NormalizePath(reference.SourceProjectFilePath), sourceProjectPath, StringComparison.OrdinalIgnoreCase)
                            ? MacroReference.CreateFromFile(outputPath, changedProject.FilePath)
                            : reference)
                    .ToArray();

                if (!MacroReferencesMatch(consumer.MacroReferences, updatedReferences))
                {
                    solution = solution.WithMacroReferences(consumer.Id, updatedReferences);
                    updatedConsumers++;
                }
            }

            _workspace.TryApplyChanges(solution);
            compilation.PerformanceInstrumentation.Macros.RecordConsumerRefreshRun(updatedConsumers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to refresh macro consumers for source project '{ProjectFilePath}'.",
                changedProject.FilePath);
        }
    }

    private string EmitMacroProjectOutput(Project macroProject)
    {
        if (string.IsNullOrWhiteSpace(macroProject.FilePath))
            throw new InvalidOperationException("Macro project file path is required.");

        var evaluation = MsBuildProjectEvaluator.Evaluate(macroProject.FilePath, RavenProjectConventions.Default, macroProject.TargetFramework);
        var compilation = _workspace.GetCompilation(macroProject.Id);
        var inputHash = ComputeMacroProjectInputHash(macroProject, compilation);
        var outputDirectory = GetShadowMacroOutputDirectory(macroProject);
        var outputPath = GetShadowMacroOutputPath(macroProject, evaluation.AssemblyName, inputHash);
        var outputPdbPath = Path.ChangeExtension(outputPath, ".pdb");
        if (File.Exists(outputPath) && File.Exists(outputPdbPath))
        {
            compilation.PerformanceInstrumentation.Macros.RecordShadowOutputCacheHit();
            return outputPath;
        }

        var tempOutputPath = Path.Combine(
            outputDirectory,
            $"{evaluation.AssemblyName}.{Guid.NewGuid():N}.tmp.dll");
        var tempPdbPath = Path.ChangeExtension(tempOutputPath, ".pdb");

        Directory.CreateDirectory(outputDirectory);

        try
        {
            EmitResult emitResult;
            using (var peStream = File.Create(tempOutputPath))
            using (var pdbStream = File.Create(tempPdbPath))
            {
                emitResult = compilation.Emit(peStream, pdbStream);
            }

            if (!emitResult.Success)
                throw new InvalidOperationException(string.Join(Environment.NewLine, emitResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            compilation.PerformanceInstrumentation.Macros.RecordShadowOutputCacheMiss();
            File.Move(tempOutputPath, outputPath, overwrite: true);
            File.Move(tempPdbPath, outputPdbPath, overwrite: true);

            return outputPath;
        }
        catch
        {
            TryDeleteFile(tempOutputPath);
            TryDeleteFile(tempPdbPath);
            throw;
        }
    }

    private static string GetShadowMacroOutputDirectory(Project macroProject)
    {
        var projectIdentity = Path.GetFileNameWithoutExtension(macroProject.FilePath)
            ?? macroProject.Id.ToString();
        return Path.Combine(MacroShadowOutputRoot, projectIdentity);
    }

    private static string GetShadowMacroOutputPath(Project macroProject, string assemblyName, string inputHash)
    {
        var directory = GetShadowMacroOutputDirectory(macroProject);
        return Path.Combine(directory, $"{assemblyName}.{inputHash}.dll");
    }

    private static bool MacroReferencesMatch(
        IReadOnlyList<MacroReference> current,
        IReadOnlyList<MacroReference> updated)
    {
        if (current.Count != updated.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Display, updated[i].Display, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(current[i].SourceProjectFilePath, updated[i].SourceProjectFilePath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string ComputeMacroProjectInputHash(Project project, Compilation compilation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var visitedCompilations = new HashSet<Compilation>(ReferenceEqualityComparer.Instance);

        AppendString(hash, "raven-macro-shadow-v1");
        AppendString(hash, typeof(Compilation).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
        AppendString(hash, Path.GetFullPath(project.FilePath!));
        AppendCompilation(hash, compilation, visitedCompilations);

        foreach (var reference in project.MacroReferences
                     .OrderBy(static reference => reference.Display, StringComparer.OrdinalIgnoreCase))
        {
            AppendString(hash, reference.Display);
            AppendString(hash, reference.SourceProjectFilePath);
            AppendFileFingerprint(hash, reference.Display);
        }

        var digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest[..8]).ToLowerInvariant();
    }

    private static void AppendCompilation(
        IncrementalHash hash,
        Compilation compilation,
        HashSet<Compilation> visitedCompilations)
    {
        if (!visitedCompilations.Add(compilation))
            return;

        AppendString(hash, compilation.AssemblyName);
        AppendCompilationOptions(hash, compilation.Options);

        foreach (var tree in compilation.SyntaxTrees
                     .Concat(compilation.MacroSyntaxTrees)
                     .OrderBy(static tree => tree.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            AppendString(hash, tree.FilePath);
            AppendParseOptions(hash, tree.Options);
            AppendString(hash, tree.GetRoot().ToFullString());
        }

        foreach (var reference in compilation.References
                     .OrderBy(GetMetadataReferenceSortKey, StringComparer.OrdinalIgnoreCase))
        {
            switch (reference)
            {
                case PortableExecutableReference portable:
                    AppendString(hash, portable.FilePath);
                    AppendFileFingerprint(hash, portable.FilePath);
                    break;
                case CompilationReference compilationReference:
                    AppendCompilation(hash, compilationReference.Compilation, visitedCompilations);
                    break;
                default:
                    AppendString(hash, reference.GetType().FullName);
                    AppendString(hash, reference.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    private static string GetMetadataReferenceSortKey(MetadataReference reference)
        => reference switch
        {
            PortableExecutableReference portable => $"file:{portable.FilePath}",
            CompilationReference compilation => $"compilation:{compilation.Compilation.AssemblyName}",
            _ => $"other:{reference.GetType().FullName}:{reference.GetHashCode()}"
        };

    private static void AppendCompilationOptions(IncrementalHash hash, CompilationOptions options)
    {
        AppendString(hash, options.OutputKind.ToString());
        AppendString(hash, options.OptimizationLevel.ToString());
        AppendString(hash, options.FrameworkProjectionMode.ToString());
        AppendString(hash, options.ReturnedValueHandlingMode.ToString());
        AppendString(hash, options.ReturnedValueHandlingModeConfigured.ToString());
        AppendString(hash, options.RunAnalyzers.ToString());
        AppendString(hash, options.EmbedCoreTypes.ToString());
        AppendString(hash, options.AllowUnsafe.ToString());
        AppendString(hash, options.UseRuntimeAsync.ToString());
        AppendString(hash, options.AllowGlobalStatements.ToString());
        AppendString(hash, options.AllowNamespaceMembers.ToString());
        AppendString(hash, options.AllowNamespaceMemberImports.ToString());
        AppendString(hash, options.EnableSuggestions.ToString());
        AppendString(hash, options.EnableIsNotNullNarrowing.ToString());
        AppendString(hash, options.SynthesizeStructuralToString.ToString());

        foreach (var (name, value) in options.SpecificDiagnosticOptions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            AppendString(hash, name);
            AppendString(hash, value.ToString());
        }

        foreach (var name in options.DisabledAnalyzers.Order(StringComparer.OrdinalIgnoreCase))
            AppendString(hash, name);
        foreach (var name in options.EnabledAnalyzers.Order(StringComparer.OrdinalIgnoreCase))
            AppendString(hash, name);
        foreach (var (name, value) in options.ExternalConstantValues.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            AppendString(hash, name);
            AppendString(hash, value);
        }
    }

    private static void AppendParseOptions(IncrementalHash hash, ParseOptions options)
    {
        AppendString(hash, options.Kind.ToString());
        AppendString(hash, options.DocumentationMode.ToString());
        AppendString(hash, options.DocumentationFormat.ToString());
        foreach (var symbol in options.PreprocessorSymbolNames.OrderBy(static symbol => symbol, StringComparer.Ordinal))
            AppendString(hash, symbol);
        foreach (var (name, value) in options.Features.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            AppendString(hash, name);
            AppendString(hash, value);
        }
    }

    private static void AppendFileFingerprint(IncrementalHash hash, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return;

        var file = new FileInfo(path);
        if (!file.Exists)
            return;

        AppendString(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendString(hash, file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> nullLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(nullLength, -1);
            hash.AppendData(nullLength);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private bool TryResolveOwnedDocument(DocumentUri uri, out OwnedDocument ownedDocument)
    {
        if (_documents.TryGetValue(uri, out ownedDocument))
        {
            var currentDocument = _workspace.CurrentSolution.GetDocument(ownedDocument.DocumentId);
            var currentProject = _workspace.CurrentSolution.GetProject(ownedDocument.ProjectId);
            if (currentDocument is not null &&
                currentProject is not null &&
                currentDocument.Project.Id == ownedDocument.ProjectId &&
                currentDocument.Version == ownedDocument.Version)
                return true;

            _documents.TryRemove(uri, out _);
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ownedDocument = default;
            return false;
        }

        var normalizedFilePath = NormalizePath(filePath);

        lock (_gate)
        {
            var preferredProjectId = _projectsByRoot.Values.FirstOrDefault();
            if (preferredProjectId != default &&
                TryFindExistingDocument(_workspace.CurrentSolution, preferredProjectId, normalizedFilePath, out var existingDocument, out var ownerProjectId))
            {
                ownedDocument = new OwnedDocument(
                    existingDocument.Id,
                    ownerProjectId,
                    existingDocument.Version,
                    IsProjectDocument: !IsFileApplicationProject(ownerProjectId));
                _documents[uri] = ownedDocument;
                return true;
            }

            foreach (var project in _workspace.CurrentSolution.Projects)
            {
                var match = project.Documents.FirstOrDefault(doc =>
                    !string.IsNullOrWhiteSpace(doc.FilePath) &&
                    string.Equals(NormalizePath(doc.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    continue;

                ownedDocument = new OwnedDocument(
                    match.Id,
                    project.Id,
                    match.Version,
                    IsProjectDocument: !IsFileApplicationProject(project.Id));
                _documents[uri] = ownedDocument;
                return true;
            }
        }

        ownedDocument = default;
        return false;
    }

    public IReadOnlyList<Project> GetProjectsSnapshot()
    {
        lock (_gate)
        {
            return _workspace.CurrentSolution.Projects.ToArray();
        }
    }

    private List<string> ResolveRoots(InitializeParams request)
    {
        var roots = new List<string>();

        if (request.WorkspaceFolders?.Any() == true)
        {
            foreach (var folder in request.WorkspaceFolders)
            {
                AddIfValid(roots, folder.Uri.GetFileSystemPath());
            }
        }

        if (roots.Count == 0)
            AddIfValid(roots, request.RootUri?.GetFileSystemPath());

        return roots;
    }

    private void AddIfValid(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = NormalizePath(path);
        if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            roots.Add(normalized);
    }

    private ProjectId ResolveProjectForUri(DocumentUri uri)
    {
        var documentPath = uri.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(documentPath))
            return EnsureFallbackProject();

        var normalizedPath = NormalizePath(documentPath);
        if (TryFindProjectDocument(normalizedPath, out var projectId))
            return projectId;

        if (_fileApplicationProjectsByRoot.TryGetValue(normalizedPath, out projectId) &&
            _workspace.CurrentSolution.GetProject(projectId) is not null)
        {
            return projectId;
        }

        projectId = CreateFileApplicationProject(normalizedPath);
        _fileApplicationProjectsByRoot[normalizedPath] = projectId;
        return projectId;
    }

    private bool TryFindProjectDocument(string normalizedPath, out ProjectId projectId)
    {
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath))
                continue;

            if (project.Documents.Any(document =>
                !string.IsNullOrWhiteSpace(document.FilePath) &&
                string.Equals(NormalizePath(document.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                projectId = project.Id;
                return true;
            }
        }

        projectId = default;
        return false;
    }

    private bool IsFileApplicationProject(ProjectId projectId)
        => _fileApplicationProjectsByRoot.ContainsValue(projectId);

    private ProjectId EnsureFallbackProject()
    {
        if (_fallbackProjectId is null)
            _fallbackProjectId = CreateFallbackProject();
        return _fallbackProjectId.Value;
    }

    private ProjectId CreateFallbackProject()
        => CreateProject("RavenLanguageServer");

    private ProjectId CreateFileApplicationProject(string rootFilePath)
    {
        var applicationName = Path.GetFileNameWithoutExtension(rootFilePath);
        if (string.IsNullOrWhiteSpace(applicationName))
            applicationName = "RavenFileApplication";

        var projectId = CreateProject($"RavenFile_{applicationName}");
        var preludeName = $"{applicationName}.Prelude.g{RavenFileExtensions.Raven}";
        var preludePath = Path.Combine(
            Path.GetDirectoryName(rootFilePath) ?? Environment.CurrentDirectory,
            preludeName);
        var preludeDocumentId = DocumentId.CreateNew(projectId);
        var solution = _workspace.CurrentSolution.AddDocument(
            preludeDocumentId,
            preludeName,
            RavenPrelude.CreateDefaultSourceText(),
            preludePath);
        _workspace.TryApplyChanges(solution);
        return projectId;
    }

    private ProjectId CreateProject(string name)
    {
        var compilationOptions = new CompilationOptions(OutputKind.ConsoleApplication);
        compilationOptions = NormalizeCompilationOptionsForLanguageServer(
            compilationOptions,
            _compilerPerformanceInstrumentation);
        var targetFramework = _workspace.DefaultTargetFramework;
        var projectId = _workspace.AddProject(
            name,
            compilationOptions: compilationOptions,
            targetFramework: targetFramework);

        var solution = _workspace.CurrentSolution;
        var version = TargetFrameworkResolver.ResolveVersion(targetFramework);
        foreach (var referencePath in TargetFrameworkResolver.GetReferenceAssemblies(version))
        {
            if (!File.Exists(referencePath))
                continue;

            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(referencePath));
        }

        var ravenCoreReferencePath = ResolveRavenCoreReferencePath(version.Moniker.ToTfm());
        if (ravenCoreReferencePath is not null)
        {
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(ravenCoreReferencePath));
            _logger.LogDebug("Added Raven.Core metadata reference '{ReferencePath}' for project '{ProjectName}'.", ravenCoreReferencePath, name);
        }
        else
        {
            _logger.LogWarning("Unable to locate a valid Raven.Core metadata reference for project '{ProjectName}'.", name);
        }

        var ravenMacrosReferencePath = ResolveRavenMacrosReferencePath(version.Moniker.ToTfm());
        if (ravenMacrosReferencePath is not null)
        {
            solution = AddMetadataReferenceIfMissing(
                solution,
                projectId,
                ravenMacrosReferencePath);
            solution = AddMetadataReferenceIfMissing(
                solution,
                projectId,
                typeof(Compilation).Assembly.Location);
            solution = ReplaceMacroReferenceByFileName(
                solution,
                projectId,
                ravenMacrosReferencePath);
            _logger.LogDebug(
                "Added Raven.Macros and its compiler contract reference for project '{ProjectName}'.",
                name);
        }
        else
        {
            _logger.LogWarning(
                "Unable to locate a valid Raven.Macros metadata reference for project '{ProjectName}'.",
                name);
        }

        _workspace.TryApplyChanges(solution);
        EnsureBuiltInAnalyzers(projectId);
        return projectId;
    }

    private string? ResolveRavenCoreReferencePath(string preferredTfm)
    {
        foreach (var candidate in EnumerateRavenCoreCandidates(preferredTfm))
        {
            if (IsValidMetadataReferencePath(candidate))
                return candidate;
        }

        return null;
    }

    private string? ResolveRavenMacrosReferencePath(string preferredTfm)
    {
        foreach (var candidate in EnumerateRavenMacrosCandidates(preferredTfm))
        {
            if (IsValidMetadataReferencePath(candidate))
                return candidate;
        }

        return null;
    }

    private IEnumerable<string> EnumerateRavenCoreCandidates(string preferredTfm)
    {
        var tfms = new[] { preferredTfm, "net11.0", "net10.0" }
            .Where(tfm => !string.IsNullOrWhiteSpace(tfm))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var roots = new[]
        {
            TryFindRepositoryRoot(Directory.GetCurrentDirectory()),
            TryFindRepositoryRoot(AppContext.BaseDirectory),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        var candidates = new List<string>();

        foreach (var root in roots)
        {
            foreach (var tfm in tfms)
            {
                candidates.Add(Path.Combine(root, "src", "Raven.Core", "bin", "Debug", tfm, "Raven.Core.dll"));
                candidates.Add(Path.Combine(root, "src", "Raven.Core", "bin", "Debug", tfm, tfm, "Raven.Core.dll"));
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll"));

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateRavenMacrosCandidates(string preferredTfm)
    {
        var tfms = new[] { preferredTfm, "net11.0", "net10.0" }
            .Where(tfm => !string.IsNullOrWhiteSpace(tfm))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roots = new[]
        {
            TryFindRepositoryRoot(Directory.GetCurrentDirectory()),
            TryFindRepositoryRoot(AppContext.BaseDirectory),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        var candidates = new List<string>();

        foreach (var root in roots)
        {
            foreach (var tfm in tfms)
            {
                candidates.Add(Path.Combine(
                    root,
                    "src",
                    "Raven.Macros",
                    "bin",
                    "Debug",
                    tfm,
                    "Raven.Macros.dll"));
                candidates.Add(Path.Combine(
                    root,
                    "src",
                    "Raven.Macros",
                    "bin",
                    "Debug",
                    tfm,
                    tfm,
                    "Raven.Macros.dll"));
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Raven.Macros.dll"));
        candidates.Add(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../sdk/Raven.Macros.dll")));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Solution AddMetadataReferenceIfMissing(
        Solution solution,
        ProjectId projectId,
        string referencePath)
    {
        var fileName = Path.GetFileName(referencePath);
        var project = solution.GetProject(projectId);
        if (project is null ||
            project.MetadataReferences
                .OfType<PortableExecutableReference>()
                .Any(reference => string.Equals(
                    Path.GetFileName(reference.FilePath),
                    fileName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return solution;
        }

        return solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(referencePath));
    }

    private static Solution ReplaceMetadataReferenceByFileName(
        Solution solution,
        ProjectId projectId,
        string referencePath)
    {
        var project = solution.GetProject(projectId);
        if (project is null)
            return solution;

        var fileName = Path.GetFileName(referencePath);
        var references = project.MetadataReferences
            .Where(reference => reference is not PortableExecutableReference portable ||
                !string.Equals(
                    Path.GetFileName(portable.FilePath),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(referencePath));
        return solution.WithMetadataReferences(projectId, references);
    }

    private static Solution ReplaceMacroReferenceByFileName(
        Solution solution,
        ProjectId projectId,
        string referencePath)
    {
        var project = solution.GetProject(projectId);
        if (project is null)
            return solution;

        var fileName = Path.GetFileName(referencePath);
        var references = project.MacroReferences
            .Where(reference => !string.Equals(
                Path.GetFileName(reference.Display),
                fileName,
                StringComparison.OrdinalIgnoreCase))
            .Append(MacroReference.CreateFromFile(referencePath));
        return solution.WithMacroReferences(projectId, references);
    }

    private static string? TryFindRepositoryRoot(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        var current = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new DirectoryInfo(Path.GetDirectoryName(startPath)!);

        while (current is not null)
        {
            var solutionPath = Path.Combine(current.FullName, "Raven.sln");
            if (File.Exists(solutionPath))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    private static bool IsValidMetadataReferencePath(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                return false;

            _ = MetadataReference.CreateFromFile(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithinRoot(string documentPath, string rootPath)
    {
        if (documentPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = rootPath + Path.DirectorySeparatorChar;
        return documentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetDirectoryDepth(string normalizedRoot, string projectFilePath)
    {
        var projectDirectory = NormalizePath(Path.GetDirectoryName(projectFilePath) ?? string.Empty);
        if (string.Equals(projectDirectory, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return 0;

        var relative = Path.GetRelativePath(normalizedRoot, projectDirectory);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            return 0;

        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private readonly record struct OwnedDocument(DocumentId DocumentId, ProjectId ProjectId, VersionStamp Version, bool IsProjectDocument);
    internal readonly record struct DocumentUpsertResult(
        Document Document,
        bool TextChanged,
        bool ProjectChanged,
        bool AddedDocument);

    private readonly record struct ReloadDocumentState(DocumentUri Uri, string Text, bool IsProjectDocument);
    private sealed record WorkspaceReloadSnapshot(
        Solution Solution,
        ImmutableDictionary<string, ProjectId> ProjectsByRoot,
        ImmutableDictionary<string, ProjectId> FileApplicationProjectsByRoot,
        ImmutableHashSet<string> SemanticDiagnosticsBlockedRoots,
        ImmutableDictionary<ProjectId, ImmutableDictionary<string, ReportDiagnostic>> EditorConfigDiagnosticOptionsByProject,
        ImmutableDictionary<ProjectId, ImmutableDictionary<string, bool>> EditorConfigGeneratedCodeOptionsByProject,
        ImmutableDictionary<DocumentUri, OwnedDocument> Documents,
        ProjectId? FallbackProjectId);
    private readonly record struct FailedProjectOpen(DateTimeOffset NextRetryUtc, string FailureType);
}
