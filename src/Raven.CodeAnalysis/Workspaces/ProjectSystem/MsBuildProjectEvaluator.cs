using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Microsoft.Build.Evaluation;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Text;

using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace Raven.CodeAnalysis;

internal static class MsBuildProjectEvaluator
{
    public static MsBuildProjectEvaluationResult Evaluate(
        string projectFilePath,
        RavenProjectConventions conventions,
        string? requestedTargetFramework = null,
        string? requestedConfiguration = null)
    {
        string configuration;
        string targetFramework;
        if (!string.IsNullOrWhiteSpace(requestedConfiguration) &&
            !string.IsNullOrWhiteSpace(requestedTargetFramework))
        {
            configuration = conventions.NormalizeConfiguration(requestedConfiguration);
            targetFramework = requestedTargetFramework;
        }
        else
        {
            var initialEvaluation = LoadProject(projectFilePath, globalProperties: null);
            configuration = string.IsNullOrWhiteSpace(requestedConfiguration)
                ? GetNormalizedConfiguration(initialEvaluation, conventions)
                : conventions.NormalizeConfiguration(requestedConfiguration);
            targetFramework = string.IsNullOrWhiteSpace(requestedTargetFramework)
                ? GetEffectiveTargetFramework(initialEvaluation)
                : requestedTargetFramework;
        }

        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = configuration,
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
            ["SkipCompilerExecution"] = "true",
            ["ProvideCommandLineArgs"] = "true"
        };

        if (!string.IsNullOrWhiteSpace(targetFramework))
            globalProperties["TargetFramework"] = targetFramework;

        var project = LoadProject(projectFilePath, globalProperties);
        if (string.IsNullOrWhiteSpace(targetFramework))
            targetFramework = GetEffectiveTargetFramework(project);

        var projectDirectory = Path.GetDirectoryName(projectFilePath) ?? Environment.CurrentDirectory;
        var documents = project.GetItems("Compile")
            .Select(item => GetFullPath(projectDirectory, item))
            .Where(RavenFileExtensions.HasRavenExtension)
            .Where(static path => File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var documentId = DocumentId.CreateNew(ProjectId.CreateNew(SolutionId.CreateNew()));
                return DocumentInfo.Create(
                    documentId,
                    Path.GetFileName(path),
                    SourceText.From(File.ReadAllText(path)),
                    path);
            })
            .ToImmutableArray();

        var metadataReferencePaths = project.GetItems("Reference")
            .Select(item => item.GetMetadataValue("HintPath"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(path => Path.IsPathRooted(path!)
                ? path!
                : Path.GetFullPath(Path.Combine(projectDirectory, path!)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var projectReferencePaths = project.GetItems("ProjectReference")
            .Where(item => !string.Equals(
                item.GetMetadataValue("ReferenceOutputAssembly"),
                "false",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => GetFullPath(projectDirectory, item))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var managedSourcePaths = project.GetItems("Compile")
            .Select(item => GetFullPath(projectDirectory, item))
            .Where(static path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (project.GetItems("RavenMacro").Count > 0)
        {
            throw new InvalidDataException(
                "The RavenMacro project item is no longer supported. " +
                "Reference a project marked with RavenCompilerPlugin using ProjectReference.");
        }

        var analyzerReferencePaths = project.GetItems("Analyzer")
            .Select(item => GetFullPath(projectDirectory, item))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var generatorReferencePaths = project.GetItems("SourceGenerator")
            .Select(item => GetFullPath(projectDirectory, item))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var packageReferences = project.GetItems("PackageReference")
            .Select(item => new PackageReferenceInfo(
                item.EvaluatedInclude,
                item.GetMetadataValue("Version")))
            .Where(static item =>
                !string.IsNullOrWhiteSpace(item.Id) &&
                !string.IsNullOrWhiteSpace(item.Version))
            .ToImmutableArray();

        var frameworkReferences = project.GetItems("FrameworkReference")
            .Where(static item => !string.Equals(
                item.EvaluatedInclude,
                "Microsoft.NETCore.App",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => new FrameworkReferenceInfo(item.EvaluatedInclude))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .ToImmutableArray();

        var preludeImports = project.GetItems("Import")
            .Select(item => new ProjectPreludeImportInfo(
                item.EvaluatedInclude,
                GetBooleanMetadata(item, "Static") ?? false,
                GetOptionalMetadata(item, "Alias")))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Include))
            .ToImmutableArray();

        var outputType = project.GetPropertyValue("OutputType");
        var allowUnsafe = GetBooleanProperty(project, "AllowUnsafe") ?? GetBooleanProperty(project, "AllowUnsafeBlocks") ?? false;
        var allowGlobalStatements = GetBooleanProperty(project, "AllowGlobalStatements")
            ?? GetBooleanProperty(project, "RavenAllowGlobalStatements")
            ?? true;
        var allowNamespaceMembers = GetBooleanProperty(project, "AllowNamespaceMembers")
            ?? GetBooleanProperty(project, "RavenAllowNamespaceMembers")
            ?? GetBooleanProperty(project, "AllowTopLevelMembers")
            ?? GetBooleanProperty(project, "RavenAllowTopLevelMembers")
            ?? true;
        var allowNamespaceMemberImports = GetBooleanProperty(project, "AllowNamespaceMemberImports")
            ?? GetBooleanProperty(project, "RavenAllowNamespaceMemberImports")
            ?? GetBooleanProperty(project, "AllowTopLevelMemberImports")
            ?? GetBooleanProperty(project, "RavenAllowTopLevelMemberImports")
            ?? true;
        var runAnalyzers = GetBooleanProperty(project, "RunAnalyzers")
            ?? GetBooleanProperty(project, "RavenRunAnalyzers")
            ?? true;
        var enableIsNotNullNarrowing = GetBooleanProperty(project, "EnableIsNotNullNarrowing") ?? false;
        var disabledAnalyzers = AnalyzerOptionUtilities.ParseAnalyzerNameSet(
            GetOptionalProperty(project, "DisabledAnalyzers") ??
            GetOptionalProperty(project, "RavenDisabledAnalyzers"));
        var enabledAnalyzers = AnalyzerOptionUtilities.ParseAnalyzerNameSet(
            GetOptionalProperty(project, "EnabledAnalyzers") ??
            GetOptionalProperty(project, "RavenEnabledAnalyzers"));
        var returnedValueHandling = GetReturnedValueHandlingProperty(project);
        var implicitImports = GetImplicitImportsProperty(project)
            ?? GetBooleanProperty(project, "GeneratePreludeImports")
            ?? GetBooleanProperty(project, "RavenGeneratePreludeImports")
            ?? true;
        var sdkProvidesImplicitImports = GetBooleanProperty(project, "_RavenSdkProvidesImplicitImports") ?? false;
        var emitCoreTypesOnly = GetBooleanProperty(project, "RavenEmitCoreTypesOnly") ?? false;
        var useHostFrameworkReferences = GetBooleanProperty(project, "RavenUseHostFrameworkReferences") ?? true;
        var frameworkProjectionMode = emitCoreTypesOnly
            ? FrameworkProjectionMode.None
            : ParseFrameworkProjectionMode(
                GetOptionalProperty(project, "FrameworkProjections") ??
                GetOptionalProperty(project, "RavenFrameworkProjections"));
        var macroOptions = project.GetItems("MacroOption")
            .Select(item => new
            {
                Key = item.EvaluatedInclude.Trim(),
                Value = item.GetMetadataValue("Value")
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(static item => item.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last().Value,
                StringComparer.Ordinal);
        var externalConstantValues = project.GetItems("RavenConstant")
            .Select(item => new
            {
                Name = item.EvaluatedInclude.Trim(),
                Value = item.GetMetadataValue("Value")
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last().Value,
                StringComparer.Ordinal);
        var parseOptions = new ParseOptions { Features = macroOptions }
            .WithPreprocessorSymbols(
                ParsePreprocessorSymbols(project.GetPropertyValue("DefineConstants")));

        var compilationOptions = new CompilationOptions(ParseOutputKind(outputType))
            .WithOptimizationLevel(ParseOptimizationLevel(project, configuration))
            .WithAllowUnsafe(allowUnsafe)
            .WithAllowGlobalStatements(allowGlobalStatements)
            .WithAllowNamespaceMembers(allowNamespaceMembers)
            .WithAllowNamespaceMemberImports(allowNamespaceMemberImports)
            .WithRunAnalyzers(runAnalyzers)
            .WithEnableIsNotNullNarrowing(enableIsNotNullNarrowing)
            .WithDisabledAnalyzers(disabledAnalyzers)
            .WithEnabledAnalyzers(enabledAnalyzers)
            .WithFrameworkProjectionMode(frameworkProjectionMode)
            .WithExternalConstantValues(externalConstantValues);

        if (emitCoreTypesOnly)
            compilationOptions = compilationOptions.WithEmbedCoreTypes(true);

        if (returnedValueHandling is { } returnedValueHandlingMode)
            compilationOptions = compilationOptions.WithReturnedValueHandlingMode(returnedValueHandlingMode);

        compilationOptions = EditorConfigDiagnosticOptions.ApplyOptions(
            compilationOptions,
            projectFilePath,
            documents.Select(static document => document.FilePath));

        if (targetFramework.StartsWith("netnano", StringComparison.OrdinalIgnoreCase))
            compilationOptions = compilationOptions.WithSynthesizeStructuralToString(false);

        var intermediateOutputPath = project.GetPropertyValue("IntermediateOutputPath");
        var generatedSourceDirectory = GetGeneratedSourceDirectory(projectDirectory, intermediateOutputPath, configuration, conventions);
        var name = GetProjectName(project, projectFilePath);
        var assemblyName = GetPropertyOrDefault(project, "AssemblyName", Path.GetFileNameWithoutExtension(projectFilePath));
        var generateDocumentationByDefault =
            ParseOutputKind(outputType) == OutputKind.DynamicallyLinkedLibrary &&
            (GetBooleanProperty(project, "RavenGenerateDocumentation") ?? true);
        var documentationOptions = new ProjectDocumentationOptions(
            GenerateXmlDocumentation: GetExplicitProjectBooleanProperty(project, "GenerateDocumentationFile") ?? generateDocumentationByDefault,
            GenerateMarkdownDocumentation: GetExplicitProjectBooleanProperty(project, "GenerateMarkdownDocumentationFile") ?? generateDocumentationByDefault,
            GenerateXmlDocumentationFromMarkdownComments: GetExplicitProjectBooleanProperty(project, "GenerateXmlDocumentationFromMarkdownComments") ?? generateDocumentationByDefault,
            XmlDocumentationFile: GetOptionalProperty(project, "DocumentationFile"),
            MarkdownDocumentationOutputPath: GetOptionalProperty(project, "MarkdownDocumentationOutputPath"));

        var outputPath = GetProjectOutputPath(projectDirectory, project, targetFramework, configuration, assemblyName);
        var isCompilerPlugin = documents.Any(static document =>
            LocalMacroSyntaxClassifier.IsCompilerPluginTree(
                SyntaxTree.ParseText(document.Text, path: document.FilePath ?? document.Name))) ||
            managedSourcePaths.Any(IsCSharpCompilerPluginSource);
        var evaluationInputPaths = project.Imports
            .Select(static import => import.ImportedProject.FullPath)
            .Append(projectFilePath)
            .Concat(documents.Select(static document => document.FilePath))
            .Concat(managedSourcePaths)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new MsBuildProjectEvaluationResult(
            name,
            assemblyName,
            targetFramework,
            configuration,
            Path.GetDirectoryName(outputPath) ?? projectDirectory,
            outputPath,
            compilationOptions,
            documents,
            metadataReferencePaths,
            projectReferencePaths,
            analyzerReferencePaths,
            generatorReferencePaths,
            packageReferences,
            frameworkReferences,
            new ProjectPreludeOptions(
                GenerateLegacyStandardImports: implicitImports && !sdkProvidesImplicitImports,
                Imports: preludeImports),
            generatedSourceDirectory,
            documentationOptions,
            isCompilerPlugin,
            parseOptions,
            useHostFrameworkReferences,
            evaluationInputPaths,
            GetBooleanProperty(project, "EmitCompilerGeneratedFiles") == true
                ? Path.GetFullPath(NormalizePathSeparators(GetOptionalProperty(project, "CompilerGeneratedFilesOutputPath")
                    ?? Path.Combine(intermediateOutputPath, "generated")), projectDirectory)
                : null);
    }

    private static bool IsCSharpCompilerPluginSource(string sourcePath)
    {
        var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            File.ReadAllText(sourcePath),
            path: sourcePath);
        var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)syntaxTree.GetRoot();

        return root.AttributeLists
            .Where(static list => string.Equals(
                list.Target?.Identifier.ValueText,
                "assembly",
                StringComparison.Ordinal))
            .SelectMany(static list => list.Attributes)
            .Any(static attribute =>
            {
                var name = attribute.Name.GetLastToken().ValueText;
                return string.Equals(name, nameof(RavenCompilerPluginAttribute), StringComparison.Ordinal) ||
                    string.Equals(name, "RavenCompilerPlugin", StringComparison.Ordinal);
            });
    }

    public static string? TryResolveReferencedProjectOutputPath(
        string projectFilePath,
        string configuration,
        string? requestedTargetFramework)
    {
        if (!File.Exists(projectFilePath))
            return null;

        if (!TryReadProjectDocument(projectFilePath, out var document))
            return null;

        if (MsBuildProjectSystemService.IsRavenMsBuildProject(document))
            return null;

        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = configuration,
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
            ["SkipCompilerExecution"] = "true"
        };

        if (!string.IsNullOrWhiteSpace(requestedTargetFramework))
            globalProperties["TargetFramework"] = requestedTargetFramework!;

        var project = LoadProject(projectFilePath, globalProperties);
        var targetPath = project.GetPropertyValue("TargetPath");
        return string.IsNullOrWhiteSpace(targetPath) ? null : Path.GetFullPath(targetPath);
    }

    private static MSBuildProject LoadProject(string projectFilePath, IDictionary<string, string>? globalProperties)
    {
        var projectCollection = globalProperties is null
            ? new ProjectCollection()
            : new ProjectCollection(globalProperties);

        return new MSBuildProject(projectFilePath, globalProperties, toolsVersion: null, projectCollection);
    }

    private static string GetProjectName(MSBuildProject project, string projectFilePath)
    {
        return GetPropertyOrDefault(
            project,
            "RootNamespace",
            Path.GetFileNameWithoutExtension(projectFilePath));
    }

    private static bool? GetExplicitProjectBooleanProperty(MSBuildProject project, string propertyName)
    {
        var property = project.Xml.Properties.LastOrDefault(property =>
            string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        return property is null || !bool.TryParse(property.Value, out var value)
            ? null
            : value;
    }

    private static string GetGeneratedSourceDirectory(
        string projectDirectory,
        string? intermediateOutputPath,
        string configuration,
        RavenProjectConventions conventions)
    {
        if (string.IsNullOrWhiteSpace(intermediateOutputPath))
            return conventions.GetGeneratedSourceDirectory(projectDirectory, configuration);

        intermediateOutputPath = NormalizePathSeparators(intermediateOutputPath);
        var fullIntermediateOutputPath = Path.IsPathRooted(intermediateOutputPath)
            ? intermediateOutputPath
            : Path.GetFullPath(Path.Combine(projectDirectory, intermediateOutputPath));

        return Path.Combine(fullIntermediateOutputPath, "raven", "generated");
    }

    private static string GetProjectOutputPath(
        string projectDirectory,
        MSBuildProject project,
        string? targetFramework,
        string configuration,
        string assemblyName)
    {
        var targetPath = GetOptionalProperty(project, "TargetPath");
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = NormalizePathSeparators(targetPath);
            return Path.IsPathRooted(targetPath)
                ? Path.GetFullPath(targetPath)
                : Path.GetFullPath(Path.Combine(projectDirectory, targetPath));
        }

        var outputDirectory = GetOptionalProperty(project, "OutputPath")
            ?? GetOptionalProperty(project, "OutDir");

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Path.Combine(projectDirectory, "bin", configuration);
            if (!string.IsNullOrWhiteSpace(targetFramework))
                outputDirectory = Path.Combine(outputDirectory, targetFramework);
        }
        else
        {
            outputDirectory = NormalizePathSeparators(outputDirectory);
            outputDirectory = !Path.IsPathRooted(outputDirectory)
                ? Path.GetFullPath(Path.Combine(projectDirectory, outputDirectory))
                : Path.GetFullPath(outputDirectory);
        }

        return Path.Combine(outputDirectory, $"{assemblyName}.dll");
    }

    private static string NormalizePathSeparators(string path)
        => path
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    private static string GetEffectiveTargetFramework(MSBuildProject project)
    {
        var targetFramework = project.GetPropertyValue("TargetFramework");
        if (!string.IsNullOrWhiteSpace(targetFramework))
            return targetFramework;

        var targetFrameworks = project.GetPropertyValue("TargetFrameworks");
        if (string.IsNullOrWhiteSpace(targetFrameworks))
            return string.Empty;

        return targetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string GetNormalizedConfiguration(MSBuildProject project, RavenProjectConventions conventions)
        => conventions.NormalizeConfiguration(GetPropertyOrDefault(project, "Configuration", conventions.DefaultConfiguration));

    private static OptimizationLevel ParseOptimizationLevel(MSBuildProject project, string configuration)
    {
        var optimize = GetBooleanProperty(project, "Optimize")
            ?? string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase);

        return optimize ? OptimizationLevel.Release : OptimizationLevel.Debug;
    }

    private static string GetFullPath(string projectDirectory, ProjectItem item)
    {
        var fullPath = item.GetMetadataValue("FullPath");
        if (!string.IsNullOrWhiteSpace(fullPath))
            return Path.GetFullPath(fullPath);

        var evaluatedInclude = item.EvaluatedInclude;
        return Path.IsPathRooted(evaluatedInclude)
            ? evaluatedInclude
            : Path.GetFullPath(Path.Combine(projectDirectory, evaluatedInclude));
    }

    private static bool? GetBooleanProperty(MSBuildProject project, string propertyName)
    {
        var value = project.GetPropertyValue(propertyName);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool? GetImplicitImportsProperty(MSBuildProject project)
    {
        var value = project.GetPropertyValue("ImplicitImports");
        if (string.IsNullOrWhiteSpace(value))
            value = project.GetPropertyValue("ImplicitUsings");

        if (string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase))
            return false;

        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ReturnedValueHandlingMode? GetReturnedValueHandlingProperty(MSBuildProject project)
    {
        var handling = GetOptionalProperty(project, "ReturnedValueHandlingMode")
            ?? GetOptionalProperty(project, "RavenReturnedValueHandlingMode")
            ?? GetOptionalProperty(project, "ReturnedValueHandling")
            ?? GetOptionalProperty(project, "RavenReturnedValueHandling");
        if (ReturnedValueHandlingOptions.TryParse(handling, out var parsedHandling))
            return parsedHandling;

        var enabled = GetBooleanProperty(project, "EnableReturnedValueAnalyzer")
            ?? GetBooleanProperty(project, "RavenEnableReturnedValueAnalyzer");
        return enabled switch
        {
            true => ReturnedValueHandlingMode.Full,
            false => ReturnedValueHandlingMode.Off,
            _ => null
        };
    }

    private static bool? GetBooleanMetadata(ProjectItem item, string metadataName)
    {
        var value = item.GetMetadataValue(metadataName);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetOptionalMetadata(ProjectItem item, string metadataName)
    {
        var value = item.GetMetadataValue(metadataName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetPropertyOrDefault(MSBuildProject project, string propertyName, string defaultValue)
    {
        var value = project.GetPropertyValue(propertyName);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string? GetOptionalProperty(MSBuildProject project, string propertyName)
    {
        var value = project.GetPropertyValue(propertyName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static FrameworkProjectionMode ParseFrameworkProjectionMode(string? value) =>
        value?.Trim() switch
        {
            "Standard" or "standard" => FrameworkProjectionMode.Standard,
            "None" or "none" => FrameworkProjectionMode.None,
            _ => FrameworkProjectionMode.Standard,
        };

    private static IEnumerable<string> ParsePreprocessorSymbols(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                [';', ',', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static OutputKind ParseOutputKind(string outputType)
    {
        if (string.Equals(outputType, "Library", StringComparison.OrdinalIgnoreCase))
            return OutputKind.DynamicallyLinkedLibrary;

        return OutputKind.ConsoleApplication;
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
}

internal readonly record struct MsBuildProjectEvaluationResult(
    string Name,
    string AssemblyName,
    string? TargetFramework,
    string Configuration,
    string OutputDirectory,
    string OutputPath,
    CompilationOptions CompilationOptions,
    ImmutableArray<DocumentInfo> Documents,
    ImmutableArray<string> MetadataReferencePaths,
    ImmutableArray<string> ProjectReferencePaths,
    ImmutableArray<string> AnalyzerReferencePaths,
    ImmutableArray<string> GeneratorReferencePaths,
    ImmutableArray<PackageReferenceInfo> PackageReferences,
    ImmutableArray<FrameworkReferenceInfo> FrameworkReferences,
    ProjectPreludeOptions PreludeOptions,
    string GeneratedSourceDirectory,
    ProjectDocumentationOptions DocumentationOptions,
    bool IsCompilerPlugin,
    ParseOptions ParseOptions,
    bool UseHostFrameworkReferences,
    ImmutableArray<string> EvaluationInputPaths,
    string? CompilerGeneratedFilesOutputPath);
