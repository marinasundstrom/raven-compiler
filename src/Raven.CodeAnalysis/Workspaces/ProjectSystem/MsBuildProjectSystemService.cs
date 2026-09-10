using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

using Raven.CodeAnalysis.Macros;

namespace Raven.CodeAnalysis;

public enum ProjectReferenceLoadMode
{
    Source,
    Metadata
}

public sealed class MsBuildProjectSystemService : IProjectSystemService
{
    private readonly RavenProjectConventions _conventions;
    private readonly bool _resolvePackageReferences;
    private readonly bool _allowPackageRestore;
    private readonly string? _requestedConfiguration;
    private readonly string? _requestedTargetFramework;
    private readonly bool? _useHostFrameworkReferences;
    private readonly ProjectReferenceLoadMode _projectReferenceLoadMode;
    private readonly string[] _compilerSupportReferencePaths;
    private readonly ProjectSystemPerformanceInstrumentation _performanceInstrumentation;
    private readonly Dictionary<ProjectEvaluationCacheKey, ProjectEvaluationCacheEntry> _evaluationCache = [];
    private readonly object _evaluationCacheGate = new();

    public MsBuildProjectSystemService()
        : this(RavenProjectConventions.Default, resolvePackageReferences: true)
    {
    }

    public MsBuildProjectSystemService(RavenProjectConventions conventions)
        : this(conventions, resolvePackageReferences: true)
    {
    }

    public MsBuildProjectSystemService(RavenProjectConventions conventions, bool resolvePackageReferences)
        : this(
            conventions,
            resolvePackageReferences,
            requestedConfiguration: null,
            requestedTargetFramework: null,
            useHostFrameworkReferences: null,
            compilerSupportReferencePaths: null,
            allowPackageRestore: true)
    {
    }

    public MsBuildProjectSystemService(
        RavenProjectConventions conventions,
        bool resolvePackageReferences,
        ProjectSystemPerformanceInstrumentation performanceInstrumentation)
        : this(
            conventions,
            resolvePackageReferences,
            requestedConfiguration: null,
            requestedTargetFramework: null,
            useHostFrameworkReferences: null,
            compilerSupportReferencePaths: null,
            allowPackageRestore: true,
            projectReferenceLoadMode: ProjectReferenceLoadMode.Source,
            performanceInstrumentation: performanceInstrumentation)
    {
    }

    public MsBuildProjectSystemService(
        RavenProjectConventions conventions,
        bool resolvePackageReferences,
        string? requestedConfiguration,
        string? requestedTargetFramework,
        bool? useHostFrameworkReferences = null,
        IEnumerable<string>? compilerSupportReferencePaths = null,
        bool allowPackageRestore = true,
        ProjectReferenceLoadMode projectReferenceLoadMode = ProjectReferenceLoadMode.Source)
        : this(
            conventions,
            resolvePackageReferences,
            requestedConfiguration,
            requestedTargetFramework,
            useHostFrameworkReferences,
            compilerSupportReferencePaths,
            allowPackageRestore,
            projectReferenceLoadMode,
            new ProjectSystemPerformanceInstrumentation())
    {
    }

    private MsBuildProjectSystemService(
        RavenProjectConventions conventions,
        bool resolvePackageReferences,
        string? requestedConfiguration,
        string? requestedTargetFramework,
        bool? useHostFrameworkReferences,
        IEnumerable<string>? compilerSupportReferencePaths,
        bool allowPackageRestore,
        ProjectReferenceLoadMode projectReferenceLoadMode,
        ProjectSystemPerformanceInstrumentation performanceInstrumentation)
    {
        _conventions = conventions ?? throw new ArgumentNullException(nameof(conventions));
        _resolvePackageReferences = resolvePackageReferences;
        _allowPackageRestore = allowPackageRestore;
        _requestedConfiguration = requestedConfiguration;
        _requestedTargetFramework = requestedTargetFramework;
        _useHostFrameworkReferences = useHostFrameworkReferences;
        _projectReferenceLoadMode = projectReferenceLoadMode;
        _performanceInstrumentation = performanceInstrumentation;
        _compilerSupportReferencePaths = compilerSupportReferencePaths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public ProjectSystemPerformanceInstrumentation PerformanceInstrumentation => _performanceInstrumentation;

    public bool CanOpenProject(string projectFilePath)
    {
        if (!File.Exists(projectFilePath))
            return false;

        if (!TryReadProjectDocument(projectFilePath, out var document))
            return false;

        var extension = Path.GetExtension(projectFilePath);
        if (string.Equals(extension, ".rvnproj", StringComparison.OrdinalIgnoreCase))
            return string.Equals(document.Root?.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase);

        return IsRavenMsBuildProject(document);
    }

    public IReadOnlyList<string> GetProjectReferencePaths(string projectFilePath)
    {
        MsBuildLocatorRegistration.EnsureRegistered();
        return EvaluateProject(projectFilePath).ProjectReferencePaths;
    }

    public ProjectId OpenProject(Workspace workspace, string projectFilePath)
    {
        if (workspace is not RavenWorkspace raven)
            throw new NotSupportedException("Project persistence requires a RavenWorkspace.");

        try
        {
            var (solution, projectId) = LoadProjectGraph(
                raven,
                workspace.CurrentSolution,
                projectFilePath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                _requestedTargetFramework,
                _requestedConfiguration);
            workspace.TryApplyChanges(solution);
            return projectId;
        }
        finally
        {
            lock (_evaluationCacheGate)
                _evaluationCache.Clear();
        }
    }

    private (Solution Solution, ProjectId ProjectId) LoadProjectGraph(
        RavenWorkspace raven,
        Solution solution,
        string projectFilePath,
        HashSet<string> loadingProjectPaths,
        string? requestedTargetFramework,
        string? requestedConfiguration)
    {
        var normalizedProjectPath = Path.GetFullPath(projectFilePath);
        if (!loadingProjectPaths.Add(normalizedProjectPath))
            throw new InvalidOperationException($"Cyclic project reference detected while opening '{projectFilePath}'.");

        var existingProject = solution.Projects.FirstOrDefault(
            project => string.Equals(project.FilePath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase));
        if (existingProject is not null)
        {
            loadingProjectPaths.Remove(normalizedProjectPath);
            return (solution, existingProject.Id);
        }

        MsBuildLocatorRegistration.EnsureRegistered();
        var evaluation = EvaluateProject(projectFilePath, requestedTargetFramework, requestedConfiguration);
        var projectId = ProjectId.CreateNew(solution.Id);
        solution = solution.AddProject(
            projectId,
            evaluation.Name,
            projectFilePath,
            evaluation.AssemblyName,
            evaluation.CompilationOptions ?? new CompilationOptions(OutputKind.ConsoleApplication),
            evaluation.DocumentationOptions,
            evaluation.ParseOptions);
        solution = solution.WithTargetFramework(projectId, evaluation.TargetFramework);
        solution = solution.WithCompilerGeneratedFilesOutputPath(projectId, evaluation.CompilerGeneratedFilesOutputPath);
        foreach (var document in evaluation.Documents)
        {
            var documentId = DocumentId.CreateNew(projectId);
            solution = solution.AddDocument(documentId, document.Name, document.Text, document.FilePath);
        }

        solution = ProjectSystemGeneratedDocumentHelper.AddGeneratedPreludeDocument(
            solution,
            projectId,
            evaluation.GeneratedSourceDirectory,
            _conventions.GetPreludeFileName(evaluation.Name),
            evaluation.PreludeOptions);

        solution = ProjectSystemGeneratedDocumentHelper.AddGeneratedTargetFrameworkAttributeDocumentIfNeeded(
            solution,
            projectId,
            evaluation.GeneratedSourceDirectory,
            _conventions.GetTargetFrameworkAttributeFileName(evaluation.Name),
            evaluation.TargetFramework);

        var tfm = evaluation.TargetFramework ?? raven.DefaultTargetFramework;
        var useHostFrameworkReferences = _useHostFrameworkReferences ?? evaluation.UseHostFrameworkReferences;
        if (useHostFrameworkReferences)
        {
            foreach (var reference in raven.GetFrameworkReferences(tfm))
                solution = solution.AddMetadataReference(projectId, reference);
        }

        var metadataReferenceNames = evaluation.MetadataReferencePaths
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var metadataReferencePath in evaluation.MetadataReferencePaths)
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(metadataReferencePath));

        var compilerSupportReferenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilerSupportReferencePath in _compilerSupportReferencePaths)
        {
            var referenceName = Path.GetFileNameWithoutExtension(compilerSupportReferencePath);
            if (string.Equals(referenceName, evaluation.AssemblyName, StringComparison.OrdinalIgnoreCase) ||
                !metadataReferenceNames.Add(referenceName))
            {
                continue;
            }

            compilerSupportReferenceNames.Add(referenceName);
            solution = solution.AddMetadataReference(
                projectId,
                MetadataReference.CreateFromFile(compilerSupportReferencePath));
        }

        if (_resolvePackageReferences)
        {
            var packageReferences = NuGetPackageResolver.ResolveReferences(
                projectFilePath,
                tfm,
                evaluation.PackageReferences,
                evaluation.FrameworkReferences,
                _allowPackageRestore);

            // Compiler support assemblies must also win in nested macro projects,
            // which are compiled before the driver can normalize root references.
            foreach (var packageReference in packageReferences.MetadataReferences)
            {
                if (packageReference is PortableExecutableReference { FilePath: { } path } &&
                    compilerSupportReferenceNames.Contains(Path.GetFileNameWithoutExtension(path)))
                {
                    continue;
                }

                solution = solution.AddMetadataReference(projectId, packageReference);
            }
            foreach (var macroReference in packageReferences.MacroReferences)
            {
                if (!compilerSupportReferenceNames.Contains(Path.GetFileNameWithoutExtension(macroReference.Display)))
                    solution = solution.AddMacroReference(projectId, macroReference);
            }
            foreach (var analyzerReferencePath in packageReferences.AnalyzerReferencePaths)
            {
                var assembly = ExtensionAssemblyLoader.LoadFromPath(analyzerReferencePath);
                solution = solution.AddAnalyzerReference(projectId, new AnalyzerReference(assembly));
            }
        }

        foreach (var referencedProjectPath in evaluation.ProjectReferencePaths)
        {
            var referencedEvaluation = EvaluateProject(
                referencedProjectPath,
                evaluation.TargetFramework,
                evaluation.Configuration);
            if (referencedEvaluation.IsCompilerPlugin)
            {
                var outputPath = string.Equals(
                        Path.GetExtension(referencedProjectPath),
                        ".rvnproj",
                        StringComparison.OrdinalIgnoreCase)
                    ? BuildRavenCompilerPluginProject(referencedProjectPath, evaluation, raven)
                    : BuildManagedMacroProject(referencedProjectPath, referencedEvaluation);
                solution = solution.AddMacroReference(
                    projectId,
                    MacroReference.CreateFromFile(outputPath, referencedProjectPath));
                solution = solution.AddMetadataReference(
                    projectId,
                    MetadataReference.CreateFromFile(outputPath));
                continue;
            }

            if (_projectReferenceLoadMode == ProjectReferenceLoadMode.Metadata)
            {
                var outputPath = CanOpenProject(referencedProjectPath)
                    ? referencedEvaluation.OutputPath
                    : MsBuildProjectEvaluator.TryResolveReferencedProjectOutputPath(
                        referencedProjectPath,
                        evaluation.Configuration,
                        evaluation.TargetFramework);
                if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
                {
                    throw new FileNotFoundException(
                        $"The output for referenced project '{referencedProjectPath}' was not found. Build project references before compiling '{projectFilePath}'.",
                        outputPath);
                }

                solution = solution.AddMetadataReference(
                    projectId,
                    MetadataReference.CreateFromFile(outputPath));
                continue;
            }

            var loadedProject = solution.Projects.FirstOrDefault(
                project => string.Equals(project.FilePath, referencedProjectPath, StringComparison.OrdinalIgnoreCase));

            if (loadedProject is not null)
            {
                solution = solution.AddProjectReference(projectId, new ProjectReference(loadedProject.Id));
                continue;
            }

            if (CanOpenProject(referencedProjectPath))
            {
                var loadedGraph = LoadProjectGraph(
                    raven,
                    solution,
                    referencedProjectPath,
                    loadingProjectPaths,
                    evaluation.TargetFramework,
                    evaluation.Configuration);
                solution = loadedGraph.Solution;
                var loadedProjectId = loadedGraph.ProjectId;
                solution = solution.AddProjectReference(projectId, new ProjectReference(loadedProjectId));
                continue;
            }

            var metadataPath = MsBuildProjectEvaluator.TryResolveReferencedProjectOutputPath(
                referencedProjectPath,
                evaluation.Configuration,
                evaluation.TargetFramework);

            if (!string.IsNullOrWhiteSpace(metadataPath) && File.Exists(metadataPath))
                solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(metadataPath));
        }

        foreach (var analyzerReferencePath in evaluation.AnalyzerReferencePaths)
        {
            var assembly = ExtensionAssemblyLoader.LoadFromPath(analyzerReferencePath);
            solution = solution.AddAnalyzerReference(projectId, new AnalyzerReference(assembly));
        }

        foreach (var generatorReferencePath in evaluation.GeneratorReferencePaths)
        {
            var assembly = ExtensionAssemblyLoader.LoadFromPath(generatorReferencePath);
            solution = solution.AddGeneratorReference(projectId, new GeneratorReference(assembly));
        }

        loadingProjectPaths.Remove(normalizedProjectPath);
        return (solution, projectId);
    }

    public void SaveProject(Project project, string filePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var projectDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(projectDirectory);

        foreach (var document in project.Documents.Where(static doc => ShouldPersistDocument(doc)))
        {
            var path = document.FilePath;
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(projectDirectory, RavenFileExtensions.HasRavenExtension(document.Name) ? document.Name : document.Name + RavenFileExtensions.Raven);
            else if (!Path.IsPathRooted(path))
                path = Path.Combine(projectDirectory, path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, document.Text.ToString());
        }

        var projectDocument = File.Exists(filePath)
            ? XDocument.Load(filePath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("Project"));
        var root = projectDocument.Root ?? new XElement("Project");
        if (projectDocument.Root is null)
            projectDocument.Add(root);

        UpdateProperty(root, "AssemblyName", project.AssemblyName);
        UpdateProperty(root, "TargetFramework", project.TargetFramework);
        UpdateProperty(root, "OutputType", MapOutputType(project.CompilationOptions?.OutputKind ?? OutputKind.ConsoleApplication));
        UpdateProperty(root, "AllowUnsafeBlocks", (project.CompilationOptions?.AllowUnsafe ?? false).ToString().ToLowerInvariant());
        UpdateProperty(root, "RavenAllowGlobalStatements", (project.CompilationOptions?.AllowGlobalStatements ?? true).ToString().ToLowerInvariant());
        UpdateProperty(root, "RavenAllowNamespaceMembers", (project.CompilationOptions?.AllowNamespaceMembers ?? true).ToString().ToLowerInvariant());
        UpdateProperty(root, "RavenAllowNamespaceMemberImports", (project.CompilationOptions?.AllowNamespaceMemberImports ?? true).ToString().ToLowerInvariant());
        UpdateProperty(root, "RavenFrameworkProjections", (project.CompilationOptions?.FrameworkProjectionMode ?? FrameworkProjectionMode.Standard).ToString());
        UpdateProperty(root, "EnableIsNotNullNarrowing", (project.CompilationOptions?.EnableIsNotNullNarrowing ?? false).ToString().ToLowerInvariant());
        var compilationOptions = project.CompilationOptions;
        UpdateProperty(root, "RavenRunAnalyzers", (compilationOptions?.RunAnalyzers ?? true).ToString().ToLowerInvariant());
        RemoveProperty(root, "EnableNullFlowAnalysis");
        RemoveProperty(root, "RavenEnableNullFlowAnalysis");
        if (compilationOptions is not null && !compilationOptions.DisabledAnalyzers.IsEmpty)
            UpdateProperty(root, "RavenDisabledAnalyzers", AnalyzerOptionUtilities.FormatAnalyzerNameSet(compilationOptions.DisabledAnalyzers));
        else
            RemoveProperty(root, "RavenDisabledAnalyzers");
        if (compilationOptions is not null && !compilationOptions.EnabledAnalyzers.IsEmpty)
            UpdateProperty(root, "RavenEnabledAnalyzers", AnalyzerOptionUtilities.FormatAnalyzerNameSet(compilationOptions.EnabledAnalyzers));
        else
            RemoveProperty(root, "RavenEnabledAnalyzers");

        if (compilationOptions?.ReturnedValueHandlingModeConfigured == true)
            UpdateProperty(root, "RavenReturnedValueHandlingMode", ReturnedValueHandlingOptions.ToProjectFileValue(compilationOptions.ReturnedValueHandlingMode));
        else
            RemoveProperty(root, "RavenReturnedValueHandlingMode");
        RemoveProperty(root, "RavenReturnedValueHandling");

        RemoveProperty(root, "MembersPublicByDefault");
        RemoveProperty(root, "RavenMembersPublicByDefault");

        var documentationOptions = project.DocumentationOptions;
        UpdateProperty(root, "GenerateDocumentationFile", ((documentationOptions?.GenerateXmlDocumentation) ?? false).ToString().ToLowerInvariant());
        UpdateProperty(root, "GenerateMarkdownDocumentationFile", ((documentationOptions?.GenerateMarkdownDocumentation) ?? false).ToString().ToLowerInvariant());
        UpdateProperty(root, "GenerateXmlDocumentationFromMarkdownComments", ((documentationOptions?.GenerateXmlDocumentationFromMarkdownComments) ?? false).ToString().ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(documentationOptions?.XmlDocumentationFile))
            UpdateProperty(root, "DocumentationFile", documentationOptions!.XmlDocumentationFile);
        else
            RemoveProperty(root, "DocumentationFile");

        if (!string.IsNullOrWhiteSpace(documentationOptions?.MarkdownDocumentationOutputPath))
            UpdateProperty(root, "MarkdownDocumentationOutputPath", documentationOptions!.MarkdownDocumentationOutputPath);
        else
            RemoveProperty(root, "MarkdownDocumentationOutputPath");

        if (UsesExplicitCompileItems(root))
            RewriteCompileItems(root, project, projectDirectory);
        RewriteManagedProjectReferences(root, project, projectDirectory);

        projectDocument.Save(filePath);
    }

    internal static bool IsRavenMsBuildProject(XDocument document)
    {
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            return false;

        var sdk = (string?)root.Attribute("Sdk");
        if (!string.IsNullOrWhiteSpace(sdk) &&
            sdk.Contains("Raven", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return root.Descendants().Any(static element =>
            (string.Equals(element.Name.LocalName, "Language", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(element.Value.Trim(), "Raven", StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(element.Name.LocalName, "LanguageTargets", StringComparison.OrdinalIgnoreCase) &&
             element.Value.Contains("Raven", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryReadProjectDocument(string projectFilePath, out XDocument document)
    {
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    private static bool ShouldPersistDocument(Document document)
    {
        if (!RavenFileExtensions.HasRavenExtension(document.Name) &&
            (document.FilePath is null || !RavenFileExtensions.HasRavenExtension(document.FilePath)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(document.FilePath))
            return true;

        var filePath = document.FilePath!;
        var matchesGeneratedDocument = RavenFileExtensions.All.Any(
            ext => filePath.EndsWith($".TargetFrameworkAttribute.g{ext}", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith($".Prelude.g{ext}", StringComparison.OrdinalIgnoreCase));
        if (!matchesGeneratedDocument)
            return true;

        var normalizedPath = filePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private string BuildRavenCompilerPluginProject(
        string projectFilePath,
        MsBuildProjectEvaluationResult requestingProject,
        RavenWorkspace workspace)
    {
        var macroEvaluation = EvaluateProject(
            projectFilePath,
            requestingProject.TargetFramework,
            requestingProject.Configuration);
        var effectiveTargetFramework = macroEvaluation.TargetFramework ?? requestingProject.TargetFramework ?? workspace.DefaultTargetFramework;
        var outputPath = GetCompilerPluginOutputPath(projectFilePath, macroEvaluation.Configuration, effectiveTargetFramework, macroEvaluation.AssemblyName);
        var rebuildInputs = GetCompilerPluginRebuildInputs(macroEvaluation).ToArray();

        if (NeedsRebuild(projectFilePath, outputPath, rebuildInputs))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var macroProjectSystem = new MsBuildProjectSystemService(
                _conventions,
                resolvePackageReferences: true,
                requestedConfiguration: macroEvaluation.Configuration,
                requestedTargetFramework: effectiveTargetFramework,
                useHostFrameworkReferences: null,
                compilerSupportReferencePaths: _compilerSupportReferencePaths,
                allowPackageRestore: true,
                projectReferenceLoadMode: ProjectReferenceLoadMode.Source,
                performanceInstrumentation: _performanceInstrumentation);
            macroProjectSystem.CacheEvaluation(
                projectFilePath,
                macroEvaluation.TargetFramework,
                macroEvaluation.Configuration,
                macroEvaluation);
            var macroWorkspace = RavenWorkspace.Create(
                targetFramework: requestingProject.TargetFramework ?? workspace.DefaultTargetFramework,
                projectSystemService: macroProjectSystem);

            var macroProjectId = macroWorkspace.OpenProject(projectFilePath);
            var macroProject = macroWorkspace.CurrentSolution.GetProject(macroProjectId)!;
            var loadableMacroReferences = ProjectContainsMacroApplications(macroProject)
                ? macroProject.MacroReferences.Where(IsLoadableMacroReference).ToArray()
                : [];
            if (loadableMacroReferences.Length != macroProject.MacroReferences.Count)
            {
                var retainedDisplays = loadableMacroReferences
                    .Select(static reference => reference.Display)
                    .Where(Path.IsPathFullyQualified)
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var excludedDisplays = macroProject.MacroReferences
                    .Select(static reference => reference.Display)
                    .Where(Path.IsPathFullyQualified)
                    .Select(Path.GetFullPath)
                    .Where(path => !retainedDisplays.Contains(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var metadataReferences = macroProject.MetadataReferences
                    .Where(reference => reference is not PortableExecutableReference portable ||
                        portable.FilePath is null ||
                        !excludedDisplays.Contains(Path.GetFullPath(portable.FilePath)))
                    .ToArray();
                var updatedSolution = macroWorkspace.CurrentSolution
                    .WithMacroReferences(macroProjectId, loadableMacroReferences)
                    .WithMetadataReferences(macroProjectId, metadataReferences);
                macroWorkspace.TryApplyChanges(
                    updatedSolution);
            }
            var macroCompilation = macroWorkspace.GetCompilation(macroProjectId);
            var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
            var tempPePath = outputPath + ".tmp";
            var tempPdbPath = pdbPath + ".tmp";

            TryDeleteFile(tempPePath);
            TryDeleteFile(tempPdbPath);

            EmitResult emitResult;
            using (var peStream = File.Create(tempPePath))
            using (var pdbStream = File.Create(tempPdbPath))
            {
                emitResult = macroCompilation.Emit(peStream, pdbStream);
            }

            if (!emitResult.Success)
            {
                TryDeleteFile(tempPePath);
                TryDeleteFile(tempPdbPath);
                var diagnosticText = string.Join(Environment.NewLine, emitResult.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
                throw new InvalidOperationException($"Failed to build macro project '{projectFilePath}'.{Environment.NewLine}{diagnosticText}");
            }

            ReplaceFile(tempPePath, outputPath);
            ReplaceFile(tempPdbPath, pdbPath);
        }

        return outputPath;
    }

    private static bool IsLoadableMacroReference(MacroReference reference)
    {
        try
        {
            _ = reference.Macros;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProjectContainsMacroApplications(Project project)
    {
        foreach (var document in project.Documents)
        {
            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            if (tree?.GetRoot().DescendantNodes().Any(static node =>
                    node is FreestandingMacroExpressionSyntax or FreestandingMacroMemberDeclarationSyntax or FreestandingMacroDeclarationSyntax ||
                    node is AttributeSyntax { HashToken.Kind: not SyntaxKind.None }) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildManagedMacroProject(
        string projectFilePath,
        MsBuildProjectEvaluationResult evaluation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectFilePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFilePath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(evaluation.Configuration);
        if (!string.IsNullOrWhiteSpace(evaluation.TargetFramework))
        {
            startInfo.ArgumentList.Add("--framework");
            startInfo.ArgumentList.Add(evaluation.TargetFramework);
        }
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("/property:WarningLevel=0");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start a build for compiler-plugin project '{projectFilePath}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            var buildOutput = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(static text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException(
                $"Failed to build compiler-plugin project '{projectFilePath}'.{Environment.NewLine}{buildOutput}");
        }

        var outputPath = MsBuildProjectEvaluator.TryResolveReferencedProjectOutputPath(
            projectFilePath,
            evaluation.Configuration,
            evaluation.TargetFramework);
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            throw new FileNotFoundException(
                $"Could not resolve compiler-plugin assembly output for project '{projectFilePath}'.",
                projectFilePath);
        }

        return outputPath;
    }

    internal static string GetCompilerPluginOutputPath(
        string projectFilePath,
        string configuration,
        string? targetFramework,
        string assemblyName)
    {
        var resolvedOutputPath = MsBuildProjectOutputResolver.ResolveProjectOutputPath(
            projectFilePath,
            targetFramework,
            RavenProjectConventions.Default,
            configuration);

        return resolvedOutputPath;
    }

    private MsBuildProjectEvaluationResult EvaluateProject(string projectFilePath)
        => EvaluateProject(projectFilePath, _requestedTargetFramework, _requestedConfiguration);

    private MsBuildProjectEvaluationResult EvaluateProject(
        string projectFilePath,
        string? requestedTargetFramework,
        string? requestedConfiguration)
    {
        var key = ProjectEvaluationCacheKey.Create(
            projectFilePath,
            requestedTargetFramework,
            requestedConfiguration);
        _performanceInstrumentation.RecordEvaluationRequest();

        lock (_evaluationCacheGate)
        {
            if (_evaluationCache.TryGetValue(key, out var cached))
            {
                if (cached.IsCurrent())
                {
                    _performanceInstrumentation.RecordEvaluationCacheHit();
                    return cached.Evaluation;
                }

                _evaluationCache.Remove(key);
                _performanceInstrumentation.RecordEvaluationCacheInvalidation();
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var evaluation = MsBuildProjectEvaluator.Evaluate(
                    key.ProjectFilePath,
                    _conventions,
                    requestedTargetFramework,
                    requestedConfiguration);
                CacheEvaluation(key, evaluation);
                return evaluation;
            }
            catch
            {
                _performanceInstrumentation.RecordEvaluationFailure();
                throw;
            }
            finally
            {
                _performanceInstrumentation.RecordEvaluation(Stopwatch.GetTimestamp() - started);
            }
        }
    }

    private void CacheEvaluation(
        string projectFilePath,
        string? requestedTargetFramework,
        string? requestedConfiguration,
        MsBuildProjectEvaluationResult evaluation)
    {
        var key = ProjectEvaluationCacheKey.Create(
            projectFilePath,
            requestedTargetFramework,
            requestedConfiguration);
        lock (_evaluationCacheGate)
            CacheEvaluation(key, evaluation);
    }

    private void CacheEvaluation(ProjectEvaluationCacheKey key, MsBuildProjectEvaluationResult evaluation)
        => _evaluationCache[key] = ProjectEvaluationCacheEntry.Create(evaluation);

    private readonly record struct ProjectEvaluationCacheKey(
        string ProjectFilePath,
        string? TargetFramework,
        string? Configuration)
    {
        public static ProjectEvaluationCacheKey Create(
            string projectFilePath,
            string? targetFramework,
            string? configuration)
            => new(
                Path.GetFullPath(projectFilePath),
                Normalize(targetFramework),
                Normalize(configuration));

        private static string? Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private sealed record ProjectEvaluationCacheEntry(
        MsBuildProjectEvaluationResult Evaluation,
        FileStamp[] InputStamps)
    {
        public static ProjectEvaluationCacheEntry Create(MsBuildProjectEvaluationResult evaluation)
            => new(
                evaluation,
                evaluation.EvaluationInputPaths.Select(FileStamp.Create).ToArray());

        public bool IsCurrent()
            => InputStamps.All(static stamp => stamp.IsCurrent());
    }

    private readonly record struct FileStamp(string Path, string? ContentHash)
    {
        public static FileStamp Create(string path)
            => new(path, ComputeContentHash(path));

        public bool IsCurrent()
            => string.Equals(ContentHash, ComputeContentHash(Path), StringComparison.Ordinal);

        private static string? ComputeContentHash(string path)
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    internal static IEnumerable<string> GetCompilerPluginRebuildInputs(MsBuildProjectEvaluationResult evaluation)
    {
        foreach (var document in evaluation.Documents)
        {
            if (!string.IsNullOrWhiteSpace(document.FilePath))
                yield return document.FilePath!;
        }

        foreach (var metadataReferencePath in evaluation.MetadataReferencePaths)
            yield return metadataReferencePath;

        foreach (var projectReferencePath in evaluation.ProjectReferencePaths)
        {
            yield return projectReferencePath;

            var referencedOutput = MsBuildProjectEvaluator.TryResolveReferencedProjectOutputPath(
                projectReferencePath,
                evaluation.Configuration,
                evaluation.TargetFramework);

            if (!string.IsNullOrWhiteSpace(referencedOutput))
                yield return referencedOutput!;
        }

    }

    internal static bool NeedsRebuild(string projectFilePath, string outputPath, IEnumerable<string?> sourcePaths)
    {
        if (!File.Exists(outputPath))
            return true;

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length == 0)
            return true;

        var outputWriteTime = outputInfo.LastWriteTimeUtc;
        if (File.GetLastWriteTimeUtc(projectFilePath) > outputWriteTime)
            return true;

        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                continue;

            if (File.GetLastWriteTimeUtc(sourcePath) > outputWriteTime)
                return true;
        }

        return false;
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        File.Move(sourcePath, destinationPath);
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsProjectFileExtension(string extension)
        => string.Equals(extension, ".rvnproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase);

    private static bool UsesExplicitCompileItems(XElement root)
    {
        var value = root
            .Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase))
            .Select(static element => element.Value)
            .LastOrDefault();

        return bool.TryParse(value, out var enabled) && !enabled;
    }

    private static void RewriteCompileItems(XElement root, Project project, string projectDirectory)
    {
        var compileElements = root
            .Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "Compile", StringComparison.OrdinalIgnoreCase))
            .Where(static element =>
            {
                var include = (string?)element.Attribute("Include");
                return !string.IsNullOrWhiteSpace(include) && RavenFileExtensions.HasRavenExtension(include);
            })
            .ToArray();

        foreach (var element in compileElements)
            element.Remove();

        var documents = project.Documents
            .Where(ShouldPersistDocument)
            .Select(doc => doc.FilePath ?? doc.Name)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path!)
                ? Path.GetRelativePath(projectDirectory, path!)
                : path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (documents.Length == 0)
            return;

        var itemGroup = new XElement(root.GetDefaultNamespace() + "ItemGroup");
        foreach (var path in documents)
            itemGroup.Add(new XElement(root.GetDefaultNamespace() + "Compile", new XAttribute("Include", path)));

        root.Add(itemGroup);
    }

    private static void RewriteManagedProjectReferences(XElement root, Project project, string projectDirectory)
    {
        var projectSystem = project.Solution.Services.ProjectSystemService;
        var managedReferenceElements = root
            .Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Where(element =>
            {
                var include = (string?)element.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                    return false;

                var path = Path.IsPathRooted(include)
                    ? include
                    : Path.GetFullPath(Path.Combine(projectDirectory, include));

                return projectSystem?.CanOpenProject(path) == true;
            })
            .ToArray();

        foreach (var element in managedReferenceElements)
            element.Remove();

        var projectReferences = project.ProjectReferences
            .Select(reference => project.Solution.GetProject(reference.ProjectId))
            .Where(static referencedProject => referencedProject?.FilePath is not null)
            .Select(static referencedProject => referencedProject!.FilePath!);
        var compilerPluginReferences = project.MacroReferences
            .Select(static reference => reference.SourceProjectFilePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!);
        var references = projectReferences
            .Concat(compilerPluginReferences)
            .Select(path => Path.GetRelativePath(projectDirectory, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (references.Length == 0)
            return;

        var itemGroup = new XElement(root.GetDefaultNamespace() + "ItemGroup");
        foreach (var path in references)
            itemGroup.Add(new XElement(root.GetDefaultNamespace() + "ProjectReference", new XAttribute("Include", path)));

        root.Add(itemGroup);
    }

    private static void UpdateProperty(XElement root, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RemoveProperty(root, name);
            return;
        }

        var property = root
            .Elements()
            .Where(static element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase))
            .Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

        if (property is not null)
        {
            property.Value = value;
            return;
        }

        var propertyGroup = root
            .Elements()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase));

        propertyGroup ??= AddPropertyGroup(root);
        propertyGroup.Add(new XElement(root.GetDefaultNamespace() + name, value));
    }

    private static void RemoveProperty(XElement root, string name)
    {
        var properties = root
            .Elements()
            .Where(static element => string.Equals(element.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase))
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var property in properties)
            property.Remove();
    }

    private static XElement AddPropertyGroup(XElement root)
    {
        var propertyGroup = new XElement(root.GetDefaultNamespace() + "PropertyGroup");
        root.AddFirst(propertyGroup);
        return propertyGroup;
    }

    private static string MapOutputType(OutputKind outputKind)
        => outputKind == OutputKind.DynamicallyLinkedLibrary ? "Library" : "Exe";
}
