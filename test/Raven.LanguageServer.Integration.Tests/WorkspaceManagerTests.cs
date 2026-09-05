using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

using CodeFixAction = Raven.CodeAnalysis.CodeAction;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"raven-ls-{Guid.NewGuid():N}");

    [Fact]
    public void NormalizeCompilationOptionsForLanguageServer_AttachesCompilerPerformanceInstrumentation()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(OutputKind.ConsoleApplication);

        var normalized = WorkspaceManager.NormalizeCompilationOptionsForLanguageServer(options, instrumentation);

        normalized.PerformanceInstrumentation.ShouldBe(instrumentation);
    }

    [Fact]
    public void NormalizeCompilationOptionsForLanguageServer_PreservesExistingCompilerPerformanceInstrumentation()
    {
        var existingInstrumentation = new PerformanceInstrumentation();
        var replacementInstrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(OutputKind.ConsoleApplication)
            .WithPerformanceInstrumentation(existingInstrumentation);

        var normalized = WorkspaceManager.NormalizeCompilationOptionsForLanguageServer(options, replacementInstrumentation);

        normalized.ShouldBeSameAs(options);
        normalized.PerformanceInstrumentation.ShouldBe(existingInstrumentation);
    }

    [Fact]
    public async Task UpsertDocumentAsync_WithUnchangedText_KeepsDocumentVersionAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        var text = "func Main() -> int => 1";
        WriteRavenFile(filePath, text);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(filePath);
        var firstDocument = await manager.UpsertDocumentAsync(uri, text);
        var firstProjectVersion = firstDocument.Project.Version;

        var secondDocument = await manager.UpsertDocumentAsync(uri, text);

        secondDocument.Id.ShouldBe(firstDocument.Id);
        secondDocument.Version.ShouldBe(firstDocument.Version);
        secondDocument.Project.Version.ShouldBe(firstProjectVersion);
    }

    [Fact]
    public async Task HoverHandler_ProjectTypesResolveInsideGenericTypeArgumentsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectRoot = Path.Combine(_tempRoot, "RavenOrderService");
        _ = WriteProject(projectRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(projectRoot, "src", "Contracts.rvn"), """
namespace TestApp

class OrderActivity
""");
        var filePath = Path.Combine(projectRoot, "src", "Consumers.rvn");
        var text = """
namespace Demo

import System.Collections.Concurrent.*
import TestApp.*

class ActivityStore {
    val Received: ConcurrentQueue<OrderActivity> = ConcurrentQueue<OrderActivity>()
    val Pending: MissingGeneric<OrderActivity> = null
}
""";
        WriteRavenFile(filePath, text);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await store.UpsertDocumentAsync(uri, text);
        var handler = new HoverHandler(store, NullLogger<HoverHandler>.Instance);
        var sourceText = SourceText.From(text);

        var concurrentQueueHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(
                sourceText,
                new TextSpan(text.IndexOf("ConcurrentQueue", StringComparison.Ordinal) + 1, 0)).Start
        }, CancellationToken.None);
        concurrentQueueHover.ShouldNotBeNull();
        concurrentQueueHover!.Contents.MarkupContent.ShouldNotBeNull();
        concurrentQueueHover.Contents.MarkupContent!.Value.ShouldContain("ConcurrentQueue");

        var sourceTypeOffset = text.LastIndexOf("OrderActivity", StringComparison.Ordinal) + 1;
        var sourceTypeHover = await handler.Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = PositionHelper.ToRange(sourceText, new TextSpan(sourceTypeOffset, 0)).Start
        }, CancellationToken.None);
        sourceTypeHover.ShouldNotBeNull();
        sourceTypeHover!.Contents.MarkupContent.ShouldNotBeNull();
        sourceTypeHover.Contents.MarkupContent!.Value.ShouldContain("class OrderActivity");
    }

    [Fact]
    public async Task OpenProject_AddsStandardMacroLibraryAsMetadataAndMacroReferenceAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        var text = """
import Raven.Macros.*

func Main() {
    let syntax = quote! { 40 + 2 }
}
""";
        WriteRavenFile(filePath, text);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var document = await manager.UpsertDocumentAsync(
            DocumentUri.FromFileSystemPath(filePath),
            text);

        document.Project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .ShouldContain(reference =>
                string.Equals(
                    Path.GetFileName(reference.FilePath),
                    "Raven.Macros.dll",
                    StringComparison.OrdinalIgnoreCase));
        var compilation = workspace.GetCompilation(document.Project.Id);
        var diagnostics = compilation.GetDiagnostics();
        compilation.MacroReferences.ShouldContain(reference =>
            string.Equals(
                Path.GetFileName(reference.Display),
                "Raven.Macros.dll",
                StringComparison.OrdinalIgnoreCase));
        diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == Raven.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Initialize_ErrorMacroSample_ExpandsForDiagnosticsAndHoverAsync()
    {
        var repoRoot = GetRepositoryRoot();
        var sampleRoot = Path.Combine(repoRoot, "samples", "projects", "error-macro");
        var sourcePath = Path.Combine(sampleRoot, "src", "Main.rvn");
        var source = File.ReadAllText(sourcePath);
        var sourceUri = DocumentUri.FromFileSystemPath(sourcePath);
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "error-macro",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        _ = await store.UpsertDocumentAsync(sourceUri, source);

        var diagnostics = await store.GetDiagnosticsAsync(sourceUri, CancellationToken.None);
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Code.HasValue &&
            (diagnostic.Code.Value.String == "RAV1504" ||
             diagnostic.Code.Value.String == "RAVM010"));

        store.TryGetDocumentContext(sourceUri, out var document, out var compilation).ShouldBeTrue();
        var syntaxTree = await document!.GetSyntaxTreeAsync();
        var root = syntaxTree!.GetRoot();
        var attribute = root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Single(candidate => candidate.Name.ToString() == "Error");
        var semanticModel = compilation!.GetSemanticModel(syntaxTree);
        var expansion = semanticModel.GetMacroExpansion(attribute);
        expansion.ShouldNotBeNull();
        expansion!.ReplacementDeclaration.ShouldBeOfType<UnionDeclarationSyntax>();
        Assert.Contains(expansion.IntroducedMembers, member =>
            member is PropertyDeclarationSyntax property && property.Identifier.ValueText == "Message");

        var hover = await new HoverHandler(store, NullLogger<HoverHandler>.Instance).Handle(
            new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(sourceUri),
                Position = PositionHelper.ToRange(SourceText.From(source), attribute.Name.Span).Start
            },
            CancellationToken.None);
        hover.ShouldNotBeNull();
        hover!.Contents.MarkupContent.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("Attached declaration macro.");
        hover.Contents.MarkupContent.Value.ShouldContain("Use `Show macro expansion`");
        hover.Contents.MarkupContent.Value.ShouldNotContain("self.ToString()");
    }

    [Fact]
    public void FindWorkspaceProjectFiles_RecursesIntoNestedProjects()
    {
        Directory.CreateDirectory(_tempRoot);
        var rootProjectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <ProjectReference Include="macros/ObservableMacros.rvnproj" />
  </ItemGroup>
</Project>
""");
        var nestedProjectPath = WriteProject(Path.Combine(_tempRoot, "macros"), "ObservableMacros", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var projectSystem = new MsBuildProjectSystemService();

        var candidates = WorkspaceManager.FindWorkspaceProjectFiles(_tempRoot, projectSystem);
        var primary = WorkspaceManager.SelectPrimaryProjectPath(_tempRoot, candidates);

        candidates.ShouldContain(rootProjectPath);
        candidates.ShouldContain(nestedProjectPath);
        primary.ShouldBe(rootProjectPath);
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void FindWorkspaceProjectFiles_SolutionGroupsListedProjects(string solutionFormat)
    {
        var workspaceRoot = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);
        var appProjectPath = WriteProject(Path.Combine(workspaceRoot, "apps", "App"), "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var sharedProjectPath = WriteProject(Path.Combine(_tempRoot, "shared"), "Shared", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var unlistedProjectPath = WriteProject(Path.Combine(workspaceRoot, "other"), "Other", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        _ = WriteSolution(
            workspaceRoot,
            "Workspace",
            solutionFormat,
            ("App", Path.Combine("apps", "App", "App.rvnproj")),
            ("Shared", Path.Combine("..", "shared", "Shared.rvnproj")));

        var candidates = WorkspaceManager.FindWorkspaceProjectFiles(
            workspaceRoot,
            new MsBuildProjectSystemService());

        candidates.ShouldBe([appProjectPath, sharedProjectPath], ignoreOrder: true);
        candidates.ShouldNotContain(unlistedProjectPath);
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void FindWorkspaceProjectFiles_SolutionWithoutRavenProjectsFallsBackToDirectoryDiscovery(string solutionFormat)
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(Path.Combine(_tempRoot, "app"), "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        File.WriteAllText(Path.Combine(_tempRoot, $"Workspace.{solutionFormat}"), "not a valid solution");

        var candidates = WorkspaceManager.FindWorkspaceProjectFiles(
            _tempRoot,
            new MsBuildProjectSystemService());

        candidates.ShouldBe([projectPath]);
    }

    [Fact]
    public void FindWorkspaceProjectFiles_SkipsGeneratedAndTemporaryDirectories()
    {
        Directory.CreateDirectory(_tempRoot);
        var rootProjectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        _ = WriteProject(Path.Combine(_tempRoot, ".raven", "nuget-restore", "restore"), "Restore", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        _ = WriteProject(Path.Combine(_tempRoot, "obj", "Debug", "generated"), "Generated", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        _ = WriteProject(Path.Combine(_tempRoot, "tmp-services-bisect", "probe"), "Probe", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        var projectSystem = new MsBuildProjectSystemService();

        var candidates = WorkspaceManager.FindWorkspaceProjectFiles(_tempRoot, projectSystem);

        candidates.ShouldBe([rootProjectPath]);
    }

    [Fact]
    public void ShouldReloadForWatchedFileChanges_IgnoresGeneratedAndTemporaryPaths()
    {
        var generatedPaths = new[]
        {
            Path.Combine(_tempRoot, ".raven", "nuget-restore", "Restore.csproj"),
            Path.Combine(_tempRoot, ".raven-build", "Debug", "net10.0", "app", "generated.rvn"),
            Path.Combine(_tempRoot, ".debug", "main.macro-expanded.rvn"),
            Path.Combine(_tempRoot, "obj", "Debug", "net10.0", "raven", "generated", "App.g.rvn"),
            Path.Combine(_tempRoot, "bin", "Debug", "App.rvn"),
            Path.Combine(_tempRoot, "tmp-services-bisect", "probe", "main.rvn")
        };

        WorkspaceManager.ShouldReloadForWatchedFileChanges(generatedPaths).ShouldBeFalse();
    }

    [Fact]
    public void ShouldReloadForWatchedFileChanges_ReloadsForNuGetAssetsFile()
    {
        var assetsPath = Path.Combine(_tempRoot, "obj", "project.assets.json");

        WorkspaceManager.ShouldReloadForWatchedFileChanges([assetsPath]).ShouldBeTrue();
    }

    [Fact]
    public void ShouldReloadForWatchedFileChanges_ReloadsForSourceSolutionAndMsBuildFiles()
    {
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        var projectPath = Path.Combine(_tempRoot, "App.rvnproj");
        var solutionPath = Path.Combine(_tempRoot, "Workspace.sln");
        var xmlSolutionPath = Path.Combine(_tempRoot, "Workspace.slnx");
        var propsPath = Path.Combine(_tempRoot, "Directory.Build.props");
        var targetsPath = Path.Combine(_tempRoot, "Directory.Build.targets");

        WorkspaceManager.ShouldReloadForWatchedFileChanges([sourcePath]).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([projectPath]).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([solutionPath]).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([xmlSolutionPath]).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([propsPath]).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([targetsPath]).ShouldBeTrue();
    }

    [Fact]
    public void IsEditorConfigWatchedFileChangePath_RecognizesEditorConfigWithoutProjectReload()
    {
        var editorConfigPath = Path.Combine(_tempRoot, ".editorconfig");

        WorkspaceManager.IsEditorConfigWatchedFileChangePath(editorConfigPath).ShouldBeTrue();
        WorkspaceManager.ShouldReloadForWatchedFileChanges([editorConfigPath]).ShouldBeFalse();
    }

    [Fact]
    public void Initialize_OpensNestedProjectsWithoutSolutionFile()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <ProjectReference Include="macros/ObservableMacros.rvnproj" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
func Main() -> () { }
""");
        _ = WriteProject(Path.Combine(_tempRoot, "macros"), "ObservableMacros", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "macros", "src", "main.rvn"), """
class MacroPlugin { }
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        manager.GetProjectsSnapshot().Count.ShouldBe(2);
    }

    [Fact]
    public void Initialize_OpensNestedProjectWhenFrameworkRestoreIsRequired()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectRoot = Path.Combine(_tempRoot, "RavenOrderService");
        _ = WriteProject(projectRoot, "OrderIntake", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(projectRoot, "src", "Program.rvn"), """
func Main() -> unit { }
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var project = manager.GetProjectsSnapshot().Single();
        project.FilePath.ShouldBe(Path.Combine(projectRoot, "OrderIntake.rvnproj"));
        project.Documents.ShouldContain(document =>
            string.Equals(document.Name, "Program.rvn", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public void Initialize_UsesSolutionToGroupWorkspaceProjects(string solutionFormat)
    {
        Directory.CreateDirectory(_tempRoot);
        var appRoot = Path.Combine(_tempRoot, "app");
        _ = WriteProject(appRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var appSourcePath = Path.Combine(appRoot, "src", "main.rvn");
        WriteRavenFile(appSourcePath, "class AppType");
        var otherRoot = Path.Combine(_tempRoot, "other");
        _ = WriteProject(otherRoot, "Other", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var otherSourcePath = Path.Combine(otherRoot, "src", "main.rvn");
        WriteRavenFile(otherSourcePath, "class OtherType");
        _ = WriteSolution(
            _tempRoot,
            "Workspace",
            solutionFormat,
            ("App", Path.Combine("app", "App.rvnproj")));

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        manager.GetProjectsSnapshot().Single().FilePath.ShouldBe(Path.Combine(appRoot, "App.rvnproj"));
        manager.TryGetDocument(DocumentUri.FromFileSystemPath(appSourcePath), out _).ShouldBeTrue();
        manager.TryGetDocument(DocumentUri.FromFileSystemPath(otherSourcePath), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("sln")]
    [InlineData("slnx")]
    public async Task ReloadForWatchedFiles_SolutionChangeRefreshesProjectGroupAsync(string solutionFormat)
    {
        Directory.CreateDirectory(_tempRoot);
        var appRoot = Path.Combine(_tempRoot, "app");
        _ = WriteProject(appRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var sharedRoot = Path.Combine(_tempRoot, "shared");
        _ = WriteProject(sharedRoot, "Shared", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var solutionPath = WriteSolution(
            _tempRoot,
            "Workspace",
            solutionFormat,
            ("App", Path.Combine("app", "App.rvnproj")),
            ("Shared", Path.Combine("shared", "Shared.rvnproj")));
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });
        manager.GetProjectsSnapshot().Count.ShouldBe(2);

        _ = WriteSolution(
            _tempRoot,
            "Workspace",
            solutionFormat,
            ("App", Path.Combine("app", "App.rvnproj")));
        _ = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(solutionPath),
                Type = FileChangeType.Changed
            }
        ]);

        manager.GetProjectsSnapshot().Single().FilePath.ShouldBe(Path.Combine(appRoot, "App.rvnproj"));
    }

    [Fact]
    public async Task TryGetDocument_ResolvesSiblingProjectDocumentByUriAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteMacroObservableLayout(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var macroUri = DocumentUri.FromFileSystemPath(Path.Combine(_tempRoot, "macros", "main.rvn"));

        manager.TryGetDocument(macroUri, out var document).ShouldBeTrue();
        document.ShouldNotBeNull();
        manager.TryGetCompilation(macroUri, out var compilation).ShouldBeTrue();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();
        Should.NotThrow(() => compilation.GetSemanticModel(syntaxTree!));
    }

    [Fact]
    public async Task RemoveDocument_DoesNotRemoveProjectBackedSiblingProjectDocumentAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteMacroObservableLayout(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);
        var originalText = File.ReadAllText(macroPath);

        _ = await manager.UpsertDocumentAsync(macroUri, originalText);
        manager.RemoveDocument(macroUri).ShouldBeTrue();

        manager.TryGetDocument(macroUri, out var document).ShouldBeTrue();
        document.ShouldNotBeNull();
        manager.TryGetCompilation(macroUri, out var compilation).ShouldBeTrue();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();
        Should.NotThrow(() => compilation.GetSemanticModel(syntaxTree!));
    }

    [Fact]
    public async Task TryGetCodeFixes_StaleOwnedDocumentWithExistingDocumentIdButDeadProjectId_RebindsToCurrentProjectAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(filePath, """
let x = 1
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        manager.TryGetDocument(uri, out var originalDocument).ShouldBeTrue();
        originalDocument.ShouldNotBeNull();

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var newProjectId = manager.GetProjectsSnapshot().Single().Id;
        var staleProjectId = ProjectId.CreateNew(workspace.CurrentSolution.Id);

        var documentsField = typeof(WorkspaceManager).GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var documents = documentsField.GetValue(manager)!;
        var itemProperty = documents.GetType().GetProperty("Item")!;
        itemProperty.SetValue(
            documents,
            Activator.CreateInstance(
                itemProperty.PropertyType,
                originalDocument!.Id,
                staleProjectId,
                originalDocument.Version,
                true),
            [uri]);

        Should.NotThrow(() => manager.TryGetCodeFixes(uri, out _));
        manager.TryGetDocument(uri, out var reboundDocument).ShouldBeTrue();
        reboundDocument.ShouldNotBeNull();
        reboundDocument.Project.Id.ShouldBe(newProjectId);
    }

    [Fact]
    public async Task SwitchingBetweenSiblingProjectDocuments_DoesNotDetachAppDocumentFromCompilationAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteMacroFreestandingLayout(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);

        _ = await manager.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));
        _ = await manager.UpsertDocumentAsync(macroUri, File.ReadAllText(macroPath));
        manager.RemoveDocument(macroUri).ShouldBeTrue();
        _ = await manager.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));

        manager.TryGetDocument(appUri, out var document).ShouldBeTrue();
        document.ShouldNotBeNull();
        manager.TryGetCompilation(appUri, out var compilation).ShouldBeTrue();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();
        Should.NotThrow(() => compilation.GetSemanticModel(syntaxTree!));
    }

    [Fact]
    public async Task TryGetDocumentContext_ReturnsMatchingDocumentAndCompilationAfterSiblingSwitchAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteMacroFreestandingLayout(_tempRoot);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);

        _ = await store.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));
        _ = await store.UpsertDocumentAsync(macroUri, File.ReadAllText(macroPath));
        store.RemoveDocument(macroUri).ShouldBeTrue();

        store.TryGetDocumentContext(appUri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();
        Should.NotThrow(() => compilation.GetSemanticModel(syntaxTree!));
    }

    [Fact]
    public async Task Initialize_ProjectOpenFailureInSiblingProject_DoesNotPreventDiagnosticsForHealthyProjectAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        var appProjectPath = WriteProject(Path.Combine(_tempRoot, "app"), "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "app", "src", "main.rvn"), """
func Main() -> unit {
    WriteLine(test)
}
""");

        _ = WriteProject(Path.Combine(_tempRoot, "broken"), "Broken", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "broken", "src", "main.rvn"), """
func Main() -> unit { }
""");

        var projectSystem = new ThrowingProjectSystemService(
            new MsBuildProjectSystemService(),
            failingProjectPath: Path.Combine(_tempRoot, "broken", "Broken.rvnproj"));
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var appUri = DocumentUri.FromFileSystemPath(Path.Combine(_tempRoot, "app", "src", "main.rvn"));
        _ = await store.UpsertDocumentAsync(appUri, File.ReadAllText(Path.Combine(_tempRoot, "app", "src", "main.rvn")));

        var diagnostics = await store.GetDiagnosticsAsync(appUri, CancellationToken.None);
        diagnostics.Any(d => d.Code?.String == "RAV0103").ShouldBeTrue();
    }

    [Fact]
    public async Task DocumentCompilerDiagnostics_UseDeclarationNullableDisposableInitializer_ReportsOnLocalNameAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(filePath, """
import System.*

func Test2() -> IDisposable? {
    return null
}

func test() {
    use test = Test2()
}
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await store.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        var result = await store.TryGetDocumentCompilerDiagnosticsAsync(uri, shouldSkipWork: null, CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        var diagnostic = result.Diagnostics.Single(diagnostic =>
            diagnostic.Code?.String == "RAV1503");
        diagnostic.Range.Start.Line.ShouldBe(7);
        diagnostic.Range.Start.Character.ShouldBe(8);
        diagnostic.Range.End.Line.ShouldBe(7);
        diagnostic.Range.End.Character.ShouldBe(12);
        diagnostic.Message.ShouldContain("IDisposable?");
        diagnostic.Message.ShouldContain("IDisposable");
    }

    [Fact]
    public async Task DocumentCompilerDiagnostics_AfterCrossFileReturnTypeEdit_ClearsStaleUseDiagnosticsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var mainPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var utilitiesPath = Path.Combine(_tempRoot, "src", "test.rvn");
        WriteRavenFile(mainPath, """
import Utilities.*

func Main() -> unit {
    use test = Test2()
    test.Dispose()
}
""");
        WriteRavenFile(utilitiesPath, """
namespace Utilities

func Test2() -> IDisposable? {
    return null
}
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var utilitiesUri = DocumentUri.FromFileSystemPath(utilitiesPath);
        _ = await store.UpsertDocumentAsync(mainUri, File.ReadAllText(mainPath));
        _ = await store.UpsertDocumentAsync(utilitiesUri, File.ReadAllText(utilitiesPath));

        var before = await store.TryGetDocumentCompilerDiagnosticsAsync(mainUri, shouldSkipWork: null, CancellationToken.None);
        before.Diagnostics.Select(static diagnostic => diagnostic.Code?.String)
            .ShouldContain("RAV1503");

        var updatedUtilitiesText = SourceText.From("""
namespace Utilities

func Test2() -> IDisposable {
    return default!
}
""");
        _ = await store.UpsertDocumentAsync(utilitiesUri, updatedUtilitiesText);

        var after = await store.TryGetDocumentCompilerDiagnosticsAsync(mainUri, shouldSkipWork: null, CancellationToken.None);
        after.WasSkipped.ShouldBeFalse();
        after.Diagnostics.Select(static diagnostic => diagnostic.Code?.String)
            .ShouldNotContain("RAV1503");
        after.Diagnostics.Select(static diagnostic => diagnostic.Code?.String)
            .ShouldNotContain("RAV0402");
    }

    public static IEnumerable<object[]> UseDeclarationTargetShapeCases()
    {
        yield return ["IDisposable", "return default!", "", false, false];
        yield return ["IDisposable?", "return null", "", true, true];
        yield return ["Resource", "return Resource()", DisposableResourceDeclaration, false, false];
        yield return ["Resource?", "return null", DisposableResourceDeclaration, true, true];
        yield return ["int", "return 0", "", true, false];
    }

    [Theory]
    [MemberData(nameof(UseDeclarationTargetShapeCases))]
    public async Task DocumentCompilerDiagnostics_UseDeclarationTargetShape_MatchesNullabilityAndDisposableRulesAsync(
        string returnType,
        string returnStatement,
        string additionalDeclarations,
        bool expectUseTargetDiagnostic,
        bool expectNullAccessDiagnostic)
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var mainPath = Path.Combine(_tempRoot, "src", "main.rvn");
        var utilitiesPath = Path.Combine(_tempRoot, "src", "test.rvn");
        WriteRavenFile(mainPath, """
import Utilities.*

func Main() -> unit {
    use test = Test2()
    test.Dispose()
}
""");
        WriteRavenFile(utilitiesPath, $$"""
namespace Utilities

{{additionalDeclarations}}

func Test2() -> {{returnType}} {
    {{returnStatement}}
}
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        var utilitiesUri = DocumentUri.FromFileSystemPath(utilitiesPath);
        _ = await store.UpsertDocumentAsync(mainUri, File.ReadAllText(mainPath));
        _ = await store.UpsertDocumentAsync(utilitiesUri, File.ReadAllText(utilitiesPath));

        var result = await store.TryGetDocumentCompilerDiagnosticsAsync(mainUri, shouldSkipWork: null, CancellationToken.None);
        var diagnosticIds = result.Diagnostics.Select(static diagnostic => diagnostic.Code?.String).ToArray();

        diagnosticIds.Contains("RAV1503").ShouldBe(expectUseTargetDiagnostic);
        diagnosticIds.Contains("RAV0402").ShouldBe(expectNullAccessDiagnostic);
    }

    private const string DisposableResourceDeclaration =
        """
        class Resource : IDisposable {
            public init() {}
            public func Dispose() -> unit {}
        }
        """;

    [Fact]
    public void Initialize_ProjectOpenFailure_IsNotRetriedUntilFilesChange()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "Broken", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
func Main() -> unit { }
""");

        var projectSystem = new ThrowingProjectSystemService(
            new MsBuildProjectSystemService(),
            failingProjectPath: projectPath);
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        var initializeParams = new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        };

        manager.Initialize(initializeParams);
        manager.Initialize(initializeParams);

        projectSystem.OpenAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Diagnostics_ProjectOpenFailure_DoesNotPublishSemanticDiagnosticsFromFallbackProjectAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "Broken", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, """
func Main() -> unit {
    Missing()
}
""");

        var projectSystem = new ThrowingProjectSystemService(
            new MsBuildProjectSystemService(),
            failingProjectPath: projectPath);
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        _ = await store.UpsertDocumentAsync(uri, File.ReadAllText(sourcePath));

        var result = await store.TryGetDocumentCompilerDiagnosticsAsync(uri, shouldSkipWork: null, CancellationToken.None);

        result.WasSkipped.ShouldBeFalse();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReloadForWatchedFiles_ProjectOpenFailure_RetriesAfterRelevantFileChangeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "Broken", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
func Main() -> unit { }
""");

        var projectSystem = new ThrowingProjectSystemService(
            new MsBuildProjectSystemService(),
            failingProjectPath: projectPath);
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });
        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(projectPath),
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_TransientProjectOpenFailure_PreservesLastSuccessfulWorkspaceAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, "func Main() -> int => 42");

        var projectSystem = new FailOnceOnReloadProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(sourcePath));

        var firstRefresh = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(projectPath),
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(2);
        firstRefresh.ShouldContain(uri);
        manager.TryGetDocument(uri, out var preservedDocument).ShouldBeTrue();
        preservedDocument!.Project.FilePath.ShouldBe(projectPath);
        workspace.GetCompilation(preservedDocument.Project.Id)
            .GetDiagnostics()
            .ShouldNotContain(diagnostic => diagnostic.Severity == Raven.CodeAnalysis.DiagnosticSeverity.Error);

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(projectPath),
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(3);
        manager.TryGetDocument(uri, out var recoveredDocument).ShouldBeTrue();
        recoveredDocument!.Project.FilePath.ShouldBe(projectPath);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_ProjectPackageReferenceChangeRefreshesMetadataReferencesAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, "func Main() -> unit { }");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(sourcePath));
        manager.TryGetDocument(uri, out var initialDocument).ShouldBeTrue();
        initialDocument!.Project.MetadataReferences.Any(IsNewtonsoftJsonReference).ShouldBeFalse();

        File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
""");

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(projectPath),
                Type = FileChangeType.Changed
            }
        ]);

        manager.TryGetDocument(uri, out var refreshedDocument).ShouldBeTrue();
        refreshedDocument!.Project.MetadataReferences.Any(IsNewtonsoftJsonReference).ShouldBeTrue();

        static bool IsNewtonsoftJsonReference(MetadataReference reference)
            => reference is PortableExecutableReference portableReference &&
               string.Equals(
                   Path.GetFileName(portableReference.FilePath),
                   "Newtonsoft.Json.dll",
                   StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_DirectoryBuildPropsChangeRefreshesEvaluatedProjectOptionsAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), "func Main() -> unit { }");
        var propsPath = Path.Combine(_tempRoot, "Directory.Build.props");
        File.WriteAllText(propsPath, """
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
  </PropertyGroup>
</Project>
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        manager.GetProjectsSnapshot().Single().CompilationOptions!.OutputKind
            .ShouldBe(OutputKind.DynamicallyLinkedLibrary);

        File.WriteAllText(propsPath, """
<Project>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
""");

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(propsPath),
                Type = FileChangeType.Changed
            }
        ]);

        manager.GetProjectsSnapshot().Single().CompilationOptions!.OutputKind
            .ShouldBe(OutputKind.ConsoleApplication);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_OpenSourceChange_DoesNotReloadWorkspaceAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, """
func Main() -> unit { }
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(sourcePath));
        projectSystem.OpenAttempts.ShouldBe(1);

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = uri,
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        manager.TryGetDocument(uri, out var document).ShouldBeTrue();
        document!.FilePath.ShouldBe(sourcePath);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_ClosedSourceChange_UpdatesKnownDocumentWithoutReloadAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, """
let value = 1
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        projectSystem.OpenAttempts.ShouldBe(1);
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        manager.TryGetDocument(uri, out var initialDocument).ShouldBeTrue();
        var initialText = await initialDocument!.GetTextAsync();
        initialText.ToString().ShouldBe("""
let value = 1
""");

        WriteRavenFile(sourcePath, """
let value = 2
""");

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = uri,
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        manager.TryGetDocument(uri, out var document).ShouldBeTrue();
        var text = await document!.GetTextAsync();
        text.ToString().ShouldBe("""
let value = 2
""");
    }

    [Fact]
    public async Task ReloadForWatchedFiles_ClosedSourceDelete_RemovesKnownDocumentWithoutReloadAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, """
let value = 1
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        projectSystem.OpenAttempts.ShouldBe(1);
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        manager.TryGetDocument(uri, out _).ShouldBeTrue();

        File.Delete(sourcePath);

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = uri,
                Type = FileChangeType.Deleted
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        manager.TryGetDocument(uri, out _).ShouldBeFalse();
        manager.GetProjectsSnapshot()
            .Single()
            .Documents
            .Any(document =>
                !string.IsNullOrWhiteSpace(document.FilePath) &&
                string.Equals(Path.GetFullPath(document.FilePath), sourcePath, StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task ReloadForWatchedFiles_SourceCreateInsideProjectGlob_AddsDocumentWithoutReloadAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var mainPath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(mainPath, """
let value = 1
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var mainUri = DocumentUri.FromFileSystemPath(mainPath);
        _ = await manager.UpsertDocumentAsync(mainUri, File.ReadAllText(mainPath));

        var sourcePath = Path.Combine(_tempRoot, "src", "created.rvn");
        WriteRavenFile(sourcePath, """
func Created() -> int => 2
""");
        var uri = DocumentUri.FromFileSystemPath(sourcePath);

        var openDocumentsToRefresh = await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = uri,
                Type = FileChangeType.Created
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        openDocumentsToRefresh.ShouldBe([mainUri]);
        manager.TryGetDocument(uri, out var document).ShouldBeTrue();
        var text = await document!.GetTextAsync();
        text.ToString().ShouldBe("""
func Created() -> int => 2
""");
    }

    [Fact]
    public async Task ReloadForWatchedFiles_OpenSourceCreateInsideProjectGlob_BecomesProjectBackedAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
let value = 1
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var sourcePath = Path.Combine(_tempRoot, "src", "created.rvn");
        WriteRavenFile(sourcePath, """
func Created() -> int => 2
""");
        var uri = DocumentUri.FromFileSystemPath(sourcePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(sourcePath));

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = uri,
                Type = FileChangeType.Created
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        manager.RemoveDocument(uri).ShouldBeTrue();
        manager.TryGetDocument(uri, out var document).ShouldBeTrue();
        document!.FilePath.ShouldBe(sourcePath);
    }

    [Fact]
    public async Task ReloadForWatchedFiles_SourceCreateOutsideProjectGlob_ReloadsWorkspaceAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
let value = 1
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var sourcePath = Path.Combine(_tempRoot, "outside.rvn");
        WriteRavenFile(sourcePath, """
func Outside() -> int => 2
""");

        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(sourcePath),
                Type = FileChangeType.Created
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(2);
    }

    [Fact]
    public void ApplyEditorConfigDiagnosticOptionsForWatchedFileChanges_UpdatesProjectOptionsWithoutReload()
    {
        Directory.CreateDirectory(_tempRoot);
        var projectPath = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(_tempRoot, "src", "main.rvn"), """
func Main() -> unit { }
""");
        var editorConfigPath = Path.Combine(_tempRoot, ".editorconfig");
        File.WriteAllText(editorConfigPath, """
root = true

[*.rvn]
dotnet_diagnostic.RAV9034.severity = warning
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        projectSystem.OpenAttempts.ShouldBe(1);
        var projectId = manager.GetProjectsSnapshot().Single().Id;
        manager.GetProjectsSnapshot().Single().CompilationOptions!.SpecificDiagnosticOptions[UnusedExpressionResultAnalyzer.DiagnosticId]
            .ShouldBe(ReportDiagnostic.Warn);

        File.WriteAllText(editorConfigPath, """
root = true

[*.rvn]
dotnet_diagnostic.RAV9034.severity = error
""");

        _ = manager.ApplyEditorConfigDiagnosticOptionsForWatchedFileChanges([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(editorConfigPath),
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        var project = manager.GetProjectsSnapshot().Single();
        project.Id.ShouldBe(projectId);
        project.CompilationOptions!.SpecificDiagnosticOptions[UnusedExpressionResultAnalyzer.DiagnosticId]
            .ShouldBe(ReportDiagnostic.Error);
    }

    [Fact]
    public void ApplyEditorConfigDiagnosticOptionsForWatchedFileChanges_UpdatesGeneratedCodeWithoutReload()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(sourcePath, "func Main() -> unit { }");
        var editorConfigPath = Path.Combine(_tempRoot, ".editorconfig");
        File.WriteAllText(editorConfigPath, """
root = true

[*.rvn]
generated_code = true
""");

        var projectSystem = new CountingProjectSystemService(new MsBuildProjectSystemService());
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0", projectSystemService: projectSystem);
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        projectSystem.OpenAttempts.ShouldBe(1);
        var projectId = manager.GetProjectsSnapshot().Single().Id;
        var analyzer = new GeneratedCodeOptOutAnalyzer();
        var project = manager.GetProjectsSnapshot().Single()
            .AddAnalyzerReference(new AnalyzerReference(analyzer));
        workspace.TryApplyChanges(project.Solution).ShouldBeTrue();
        workspace.GetDiagnostics(projectId).ShouldNotContain(diagnostic => diagnostic.Id == "AN9070");

        File.WriteAllText(editorConfigPath, """
root = true

[*.rvn]
generated_code = false
""");

        _ = manager.ApplyEditorConfigDiagnosticOptionsForWatchedFileChanges([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(editorConfigPath),
                Type = FileChangeType.Changed
            }
        ]);

        projectSystem.OpenAttempts.ShouldBe(1);
        manager.GetProjectsSnapshot().Single().Id.ShouldBe(projectId);
        workspace.GetDiagnostics(projectId).ShouldContain(diagnostic => diagnostic.Id == "AN9070");
    }

    private sealed class GeneratedCodeOptOutAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            "AN9070", "EditorConfig generated code", null, "", "EditorConfig generated code", "Testing", Raven.CodeAnalysis.DiagnosticSeverity.Warning);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxTreeAction(action =>
                action.ReportDiagnostic(Raven.CodeAnalysis.Diagnostic.Create(Rule, Raven.CodeAnalysis.Location.None)));
        }
    }

    [Fact]
    public async Task UpdatingMacroProjectDocument_RefreshesConsumingProjectMacroExpansionAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteFreestandingMacroExpansionLayout(_tempRoot, "1");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);

        _ = await manager.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));
        _ = await manager.UpsertDocumentAsync(macroUri, File.ReadAllText(macroPath));

        manager.TryGetDocumentContext(appUri, out var initialDocument, out _).ShouldBeTrue();
        var initialMacroReferenceInfo = initialDocument!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null);
        initialMacroReferenceInfo.SourceProjectFilePath.ShouldBe(Path.Combine(_tempRoot, "macros", "FreestandingMacros.rvnproj"));
        var initialMacroReference = initialMacroReferenceInfo.Display;

        var initialExpansion = await GetFreestandingMacroExpansionTextAsync(manager, appUri);
        initialExpansion.ShouldBe("1");

        var updatedMacroSource = CreateFreestandingMacroExpansionSource("2");
        _ = await manager.UpsertDocumentAsync(macroUri, updatedMacroSource);

        manager.TryGetDocumentContext(macroUri, out var refreshedMacroDocument, out _).ShouldBeTrue();
        refreshedMacroDocument!.Project.FilePath.ShouldBe(Path.Combine(_tempRoot, "macros", "FreestandingMacros.rvnproj"));

        manager.TryGetDocumentContext(appUri, out var refreshedDocument, out _).ShouldBeTrue();
        var refreshedMacroReference = refreshedDocument!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null).Display;
        refreshedMacroReference.ShouldNotBe(initialMacroReference);

        var refreshedExpansion = await GetFreestandingMacroExpansionTextAsync(manager, appUri);
        refreshedExpansion.ShouldBe("2");
    }

    [Fact]
    public async Task DeferredMacroProjectDocumentUpdate_RefreshesConsumingProjectAfterFlushAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteFreestandingMacroExpansionLayout(_tempRoot, "1");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);

        _ = await manager.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));
        _ = await manager.UpsertDocumentAsync(macroUri, File.ReadAllText(macroPath));

        manager.TryGetDocumentContext(appUri, out var initialDocument, out _).ShouldBeTrue();
        var initialMacroReference = initialDocument!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null).Display;

        var updatedMacroSource = SourceText.From(CreateFreestandingMacroExpansionSource("2"));
        _ = await manager.UpsertDocumentAsync(macroUri, updatedMacroSource, deferMacroConsumerRefresh: true);

        manager.TryGetDocumentContext(appUri, out var pendingDocument, out _).ShouldBeTrue();
        pendingDocument!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null).Display.ShouldBe(initialMacroReference);

        await manager.FlushPendingMacroConsumerRefreshesAsync();

        manager.TryGetDocumentContext(appUri, out var refreshedDocument, out _).ShouldBeTrue();
        refreshedDocument!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null).Display.ShouldNotBe(initialMacroReference);

        var refreshedExpansion = await GetFreestandingMacroExpansionTextAsync(manager, appUri);
        refreshedExpansion.ShouldBe("2");
    }

    [Fact(Skip = "Stale watched-file macro refresh coverage: cache invalidation expectations need redesign around current project reload behavior.")]
    public async Task WatchedMacroProjectDocumentChange_RefreshesConsumingProjectMacroExpansionAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteFreestandingMacroExpansionLayout(_tempRoot, "1");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);

        _ = await manager.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));

        var initialExpansion = await GetFreestandingMacroExpansionTextAsync(manager, appUri);
        initialExpansion.ShouldBe("1");

        File.WriteAllText(macroPath, CreateFreestandingMacroExpansionSource("2"));
        await manager.ReloadForWatchedFilesAsync([
            new FileEvent
            {
                Uri = DocumentUri.FromFileSystemPath(macroPath),
                Type = FileChangeType.Changed
            }
        ]);

        var refreshedExpansion = await GetFreestandingMacroExpansionTextAsync(manager, appUri);
        refreshedExpansion.ShouldBe("2");
    }

    [Fact]
    public async Task WatchedEmbeddedFileChangeDeleteAndRecreate_RefreshesOpenConsumerAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "EmbedApp", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var sourcePath = Path.Combine(_tempRoot, "src", "main.rvn");
        var assetPath = Path.Combine(_tempRoot, "src", "message.txt");
        var source = """
import Raven.Macros.*

func Main() -> string => embedFileContent!("message.txt")
""";
        WriteRavenFile(sourcePath, source);
        File.WriteAllText(assetPath, "first");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var sourceUri = DocumentUri.FromFileSystemPath(sourcePath);
        var assetUri = DocumentUri.FromFileSystemPath(assetPath);
        _ = await manager.UpsertDocumentAsync(sourceUri, source);
        (await GetFreestandingMacroExpansionTextAsync(manager, sourceUri))
            .ShouldBe("\"first\"");

        File.WriteAllText(assetPath, "second");
        var changedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent { Uri = assetUri, Type = FileChangeType.Changed }
        ]);
        changedDocuments.ShouldContain(sourceUri);
        (await GetFreestandingMacroExpansionTextAsync(manager, sourceUri))
            .ShouldBe("\"second\"");

        File.Delete(assetPath);
        var deletedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent { Uri = assetUri, Type = FileChangeType.Deleted }
        ]);
        deletedDocuments.ShouldContain(sourceUri);
        (await GetFreestandingMacroExpansionTextAsync(manager, sourceUri))
            .ShouldBeNull();

        File.WriteAllText(assetPath, "third");
        var recreatedDocuments = await manager.ReloadForWatchedFilesAsync([
            new FileEvent { Uri = assetUri, Type = FileChangeType.Created }
        ]);
        recreatedDocuments.ShouldContain(sourceUri);
        (await GetFreestandingMacroExpansionTextAsync(manager, sourceUri))
            .ShouldBe("\"third\"");
    }

    [Fact]
    public async Task Initialize_SampleMacroObservableRoot_ResolvesMacrosForAppDocumentAsync()
    {
        var repoRoot = GetRepositoryRoot();
        var sampleRoot = Path.Combine(repoRoot, "samples", "projects", "macro-observable");
        var appPath = Path.Combine(sampleRoot, "app", "src", "Program.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "macro-observable",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        _ = await store.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));

        var diagnostics = await store.GetDiagnosticsAsync(appUri, CancellationToken.None);
        Assert.False(
            diagnostics.Any(diagnostic => diagnostic.Code?.String == "RAVM010"),
            string.Join("\n", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        store.TryGetDocumentContext(appUri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();

        var semanticModel = compilation.GetSemanticModel(syntaxTree!);
        var attribute = syntaxTree.GetRoot().DescendantNodes().OfType<AttributeSyntax>().Single();
        var expansion = semanticModel.GetMacroExpansion(attribute);

        expansion.ShouldNotBeNull();
        expansion!.ReplacementDeclaration.ShouldNotBeNull();
        expansion.IntroducedMembers.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Initialize_SampleMacroReactiveRoot_ResolvesMacrosForAppDocumentAsync()
    {
        var repoRoot = GetRepositoryRoot();
        var sampleRoot = Path.Combine(repoRoot, "samples", "projects", "macro-reactive");
        var appPath = Path.Combine(sampleRoot, "app", "src", "Program.rvn");
        var appUri = DocumentUri.FromFileSystemPath(appPath);

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "macro-reactive",
                Uri = DocumentUri.FromFileSystemPath(sampleRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        _ = await store.UpsertDocumentAsync(appUri, File.ReadAllText(appPath));

        var diagnostics = await store.GetDiagnosticsAsync(appUri, CancellationToken.None);
        Assert.False(
            diagnostics.Any(diagnostic => diagnostic.Code?.String == "RAVM010"),
            string.Join("\n", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        store.TryGetDocumentContext(appUri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();

        var semanticModel = compilation.GetSemanticModel(syntaxTree!);
        var root = syntaxTree.GetRoot();
        var attribute = root.DescendantNodes()
            .OfType<AttributeSyntax>()
            .Single(candidate => candidate.Name.ToString() == "Observable");
        var freestanding = root.DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();

        semanticModel.GetMacroExpansion(attribute).ShouldNotBeNull();
        semanticModel.GetMacroExpansion(freestanding).ShouldNotBeNull();
    }

    [Fact]
    public async Task TryGetRefactorings_ReturnsContextActionsForOpenDocumentAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        var filePath = Path.Combine(_tempRoot, "main.rvn");
        var uri = DocumentUri.FromFileSystemPath(filePath);
        File.WriteAllText(filePath, "func Main() -> () { }");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(
            workspace,
            NullLogger<WorkspaceManager>.Instance,
            [],
            [new TestRefactoringProvider()]);

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        manager.TryGetRefactorings(uri, new TextSpan(0, 4), out var refactorings).ShouldBeTrue();
        refactorings.Length.ShouldBe(1);
        refactorings[0].Action.Title.ShouldBe("Test refactoring");
    }

    [Fact]
    public async Task TopLevelExeProject_DocumentContext_BindsWithoutMainFunctionAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "AspNetMinimalApi", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AspNetMinimalApi</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(filePath, """
let first = args.Length
let second = first + 1

record Data(val Value: int)
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        manager.TryGetDocumentContext(uri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();

        var semanticModel = compilation.GetSemanticModel(syntaxTree!);
        var diagnostics = compilation.GetDiagnostics();
        diagnostics.Any(diagnostic => diagnostic.Descriptor.Id is "RAV1012" or "RAV1014").ShouldBeFalse();

        var root = syntaxTree!.GetRoot();
        var argsIdentifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(id => id.Identifier.ValueText == "args");
        var argsSymbol = semanticModel.GetSymbolInfo(argsIdentifier).Symbol;

        argsSymbol.ShouldNotBeNull();
        argsSymbol.Name.ShouldBe("args");
    }

    [Fact]
    public async Task TopLevelExeProject_MainToTopLevelTransition_UpdatesCompilationAndOutlineAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "AspNetMinimalApi", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>AspNetMinimalApi</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");
        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(filePath, """
func Main() -> int {
    func Parse() -> int => 1
    Parse()
}
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await store.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        var transitionedText = """
let first = args.Length

if first >= 0 {
    func Parse() -> int => first
    Parse()
}

record Data(val Value: int)
""";

        _ = await store.UpsertDocumentAsync(uri, transitionedText);

        store.TryGetDocumentContext(uri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();

        var diagnostics = await store.GetDiagnosticsAsync(uri, CancellationToken.None);
        diagnostics.Any(diagnostic => diagnostic.Code?.String is "RAV1012" or "RAV1014").ShouldBeFalse();

        var semanticModel = compilation.GetSemanticModel(syntaxTree!);
        var root = syntaxTree!.GetRoot();
        var argsIdentifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(id => id.Identifier.ValueText == "args");
        semanticModel.GetSymbolInfo(argsIdentifier).Symbol.ShouldNotBeNull();

        var buildMemberSymbols = typeof(DocumentSymbolHandler)
            .GetMethod("BuildMemberSymbols", BindingFlags.NonPublic | BindingFlags.Static)!;
        var text = await document.GetTextAsync();
        var symbols = ((IEnumerable<DocumentSymbol>)buildMemberSymbols.Invoke(null, [root.Members, text])!)
            .ToArray();

        symbols.Any(symbol => symbol.Name == "Main").ShouldBeFalse();
        var topLevelCode = symbols.Single(symbol => symbol.Name == "<top-level code>");
        topLevelCode.Children.ShouldNotBeNull();
        topLevelCode.Children.Any(symbol => symbol.Name == "Parse").ShouldBeTrue();
        symbols.Single(symbol => symbol.Name == "Data").Kind.ShouldBe(OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Struct);
    }

    [Fact]
    public async Task TryGetCodeFixes_StaleOwnedDocument_RebindsInsteadOfThrowingAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        _ = WriteProject(_tempRoot, "App", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
  </ItemGroup>
</Project>
""");

        var filePath = Path.Combine(_tempRoot, "src", "main.rvn");
        WriteRavenFile(filePath, """
func Main() -> unit {
    let text: string? = null
}
""");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        var uri = DocumentUri.FromFileSystemPath(filePath);
        _ = await manager.UpsertDocumentAsync(uri, File.ReadAllText(filePath));

        var documentsField = typeof(WorkspaceManager).GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var documents = documentsField.GetValue(manager)!;
        var tryGetValue = documents.GetType().GetMethod("TryGetValue")!;
        var tryGetArgs = new object?[] { uri, null };
        ((bool)tryGetValue.Invoke(documents, tryGetArgs)!).ShouldBeTrue();
        var staleOwnedDocument = tryGetArgs[1];

        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(_tempRoot)
            })
        });

        documents = documentsField.GetValue(manager)!;
        var itemProperty = documents.GetType().GetProperty("Item")!;
        itemProperty.SetValue(documents, staleOwnedDocument, [uri]);

        Should.NotThrow(() => manager.TryGetCodeFixes(uri, out _));
    }

    [Fact]
    public void MacroShadowOutputDirectory_DoesNotChangeAcrossProjectVersions()
    {
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var projectId = workspace.AddProject("Macros", targetFramework: "net10.0");
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        var getShadowMacroOutputDirectory = typeof(WorkspaceManager)
            .GetMethod("GetShadowMacroOutputDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;

        var firstPath = (string)getShadowMacroOutputDirectory.Invoke(null, [project])!;

        var documentId = DocumentId.CreateNew(projectId);
        var updatedSolution = workspace.CurrentSolution.AddDocument(
            documentId,
            "macro.rvn",
            SourceText.From("func Main() -> unit { }"),
            Path.Combine(_tempRoot, "macro.rvn"));
        workspace.TryApplyChanges(updatedSolution);
        var updatedProject = workspace.CurrentSolution.GetProject(projectId)!;

        var secondPath = (string)getShadowMacroOutputDirectory.Invoke(null, [updatedProject])!;

        secondPath.ShouldBe(firstPath);
    }

    [Fact]
    public void MacroShadowOutputPath_ChangesWhenInputHashChanges()
    {
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var projectId = workspace.AddProject("Macros", targetFramework: "net10.0");
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        var getShadowMacroOutputPath = typeof(WorkspaceManager)
            .GetMethod("GetShadowMacroOutputPath", BindingFlags.Static | BindingFlags.NonPublic)!;

        var firstPath = (string)getShadowMacroOutputPath.Invoke(null, [project, "Macros", "aaaaaaaaaaaaaaaa"])!;
        var secondPath = (string)getShadowMacroOutputPath.Invoke(null, [project, "Macros", "bbbbbbbbbbbbbbbb"])!;

        firstPath.ShouldNotBe(secondPath);
        Path.GetDirectoryName(firstPath).ShouldBe(Path.GetDirectoryName(secondPath));
    }

    [Fact]
    public async Task MacroShadowOutputCache_CoversColdWarmRestartEditAndRevertLifecycleAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteFreestandingMacroExpansionLayout(_tempRoot, "1");

        var macroProjectPath = Path.Combine(_tempRoot, "macros", "FreestandingMacros.rvnproj");
        var macroPath = Path.Combine(_tempRoot, "macros", "main.rvn");
        var appPath = Path.Combine(_tempRoot, "app", "src", "main.rvn");
        var macroUri = DocumentUri.FromFileSystemPath(macroPath);
        var appUri = DocumentUri.FromFileSystemPath(appPath);
        var emitMacroProjectOutput = typeof(WorkspaceManager)
            .GetMethod("EmitMacroProjectOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = CreateWorkspaceManager(workspace, _tempRoot);
        var macroProject = GetProject(manager, macroProjectPath);
        var compilation = workspace.GetCompilation(macroProject.Id);
        compilation.PerformanceInstrumentation.Macros.Reset();

        var firstPath = (string)emitMacroProjectOutput.Invoke(manager, [macroProject])!;
        var warmPath = (string)emitMacroProjectOutput.Invoke(manager, [macroProject])!;

        warmPath.ShouldBe(firstPath);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(1);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(1);

        var pdbPath = Path.ChangeExtension(firstPath, ".pdb");
        File.Delete(pdbPath);
        compilation.PerformanceInstrumentation.Macros.Reset();

        var repairedPath = (string)emitMacroProjectOutput.Invoke(manager, [macroProject])!;

        repairedPath.ShouldBe(firstPath);
        File.Exists(pdbPath).ShouldBeTrue();
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(1);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(0);

        workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        manager = CreateWorkspaceManager(workspace, _tempRoot);
        macroProject = GetProject(manager, macroProjectPath);
        compilation = workspace.GetCompilation(macroProject.Id);
        compilation.PerformanceInstrumentation.Macros.Reset();

        var restartedPath = (string)emitMacroProjectOutput.Invoke(manager, [macroProject])!;

        restartedPath.ShouldBe(firstPath);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(0);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(1);

        var initialConsumerSource = File.ReadAllText(appPath);
        _ = await manager.UpsertDocumentAsync(appUri, initialConsumerSource);
        var initialReferencePath = GetSourceMacroReferencePath(manager, appUri);
        compilation.PerformanceInstrumentation.Macros.Reset();

        _ = await manager.UpsertDocumentAsync(
            appUri,
            SourceText.From(initialConsumerSource + Environment.NewLine),
            deferMacroConsumerRefresh: true);
        await manager.FlushPendingMacroConsumerRefreshesAsync();

        GetSourceMacroReferencePath(manager, appUri).ShouldBe(initialReferencePath);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(0);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(0);
        (await GetFreestandingMacroExpansionTextAsync(manager, appUri)).ShouldBe("1");

        _ = await manager.UpsertDocumentAsync(
            macroUri,
            SourceText.From(CreateFreestandingMacroExpansionSource("2")),
            deferMacroConsumerRefresh: true);
        macroProject = GetProject(manager, macroProjectPath);
        compilation = workspace.GetCompilation(macroProject.Id);
        compilation.PerformanceInstrumentation.Macros.Reset();

        await manager.FlushPendingMacroConsumerRefreshesAsync();

        var editedReferencePath = GetSourceMacroReferencePath(manager, appUri);
        editedReferencePath.ShouldNotBe(firstPath);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(1);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(0);
        (await GetFreestandingMacroExpansionTextAsync(manager, appUri)).ShouldBe("2");

        _ = await manager.UpsertDocumentAsync(
            macroUri,
            SourceText.From(CreateFreestandingMacroExpansionSource("1")),
            deferMacroConsumerRefresh: true);
        macroProject = GetProject(manager, macroProjectPath);
        compilation = workspace.GetCompilation(macroProject.Id);
        compilation.PerformanceInstrumentation.Macros.Reset();

        await manager.FlushPendingMacroConsumerRefreshesAsync();

        GetSourceMacroReferencePath(manager, appUri).ShouldBe(firstPath);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheMisses.ShouldBe(0);
        compilation.PerformanceInstrumentation.Macros.ShadowOutputCacheHits.ShouldBe(1);
        (await GetFreestandingMacroExpansionTextAsync(manager, appUri)).ShouldBe("1");
    }

    [Fact]
    public void MacroShadowOutputCacheKey_InvalidatesWhenReferencedAssemblyChanges()
    {
        Directory.CreateDirectory(_tempRoot);
        WriteFreestandingMacroExpansionLayout(_tempRoot, "1");

        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = CreateWorkspaceManager(workspace, _tempRoot);
        var macroProjectPath = Path.Combine(_tempRoot, "macros", "FreestandingMacros.rvnproj");
        var macroProject = GetProject(manager, macroProjectPath);
        var dependencyPath = Path.Combine(_tempRoot, "MacroDependency.dll");
        File.Copy(typeof(object).Assembly.Location, dependencyPath);
        var compilation = Compilation.Create(
                "FingerprintProbe",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(MetadataReference.CreateFromFile(dependencyPath))
            .AddSyntaxTrees(SyntaxTree.ParseText("class Probe {}", path: "Probe.rvn"));
        var computeInputHash = typeof(WorkspaceManager)
            .GetMethod("ComputeMacroProjectInputHash", BindingFlags.Static | BindingFlags.NonPublic)!;

        var firstHash = (string)computeInputHash.Invoke(null, [macroProject, compilation])!;
        File.SetLastWriteTimeUtc(dependencyPath, File.GetLastWriteTimeUtc(dependencyPath).AddSeconds(1));
        var secondHash = (string)computeInputHash.Invoke(null, [macroProject, compilation])!;

        secondHash.ShouldNotBe(firstHash);
    }

    private static WorkspaceManager CreateWorkspaceManager(RavenWorkspace workspace, string root)
    {
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams
        {
            WorkspaceFolders = new Container<WorkspaceFolder>(new WorkspaceFolder
            {
                Name = "temp",
                Uri = DocumentUri.FromFileSystemPath(root)
            })
        });
        return manager;
    }

    private static Project GetProject(WorkspaceManager manager, string projectPath)
        => manager.GetProjectsSnapshot().Single(project =>
            string.Equals(project.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));

    private static string GetSourceMacroReferencePath(WorkspaceManager manager, DocumentUri appUri)
    {
        manager.TryGetDocumentContext(appUri, out var document, out _).ShouldBeTrue();
        return document!.Project.MacroReferences.Single(
            reference => reference.SourceProjectFilePath is not null).Display;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private static string WriteProject(string directory, string name, string contents)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.rvnproj");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string WriteSolution(
        string directory,
        string name,
        string format,
        params (string Name, string RelativePath)[] projects)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.{format}");
        if (string.Equals(format, "slnx", StringComparison.OrdinalIgnoreCase))
        {
            var projectElements = projects.Select(project =>
                $"    <Project Path=\"{System.Security.SecurityElement.Escape(project.RelativePath)}\" />");
            File.WriteAllText(path, $$"""
<Solution>
  <Folder Name="/src/">
{{string.Join(Environment.NewLine, projectElements)}}
  </Folder>
</Solution>
""");
            return path;
        }

        var projectLines = projects.Select((project, index) =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{project.Name}\", \"{project.RelativePath}\", \"{{00000000-0000-0000-0000-{index + 1:D12}}}\"{Environment.NewLine}EndProject");
        File.WriteAllText(path, $$"""
Microsoft Visual Studio Solution File, Format Version 12.00
{{string.Join(Environment.NewLine, projectLines)}}
Global
EndGlobal
""");
        return path;
    }

    private static void WriteRavenFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string GetRepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static void WriteMacroObservableLayout(string root)
    {
        var ravenCodeAnalysisPath = typeof(RavenWorkspace).Assembly.Location;

        _ = WriteProject(Path.Combine(root, "app"), "MacroObservable", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MacroObservable</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <ProjectReference Include="../macros/ObservableMacros.rvnproj" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "app", "src", "main.rvn"), """
func Main() -> () { }
""");

        _ = WriteProject(Path.Combine(root, "macros"), "ObservableMacros", $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>ObservableMacros</AssemblyName>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="main.rvn" />
    <Reference Include="Raven.CodeAnalysis">
      <HintPath>{{ravenCodeAnalysisPath}}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "macros", "main.rvn"), """
import Raven.CodeAnalysis.Macros.*
import Raven.CodeAnalysis.Syntax.*

[assembly: RavenCompilerPlugin(typeof(ObservableMacro))]

class ObservableMacro: IMacroDefinition {
    val Name: string => "Observable"
    func Expand(property: PropertyDeclarationSyntax, context: AttachedMacroContext) -> MacroExpansionResult {
        MacroExpansionResult.Empty
    }
}
""");
    }

    private static void WriteMacroFreestandingLayout(string root)
    {
        var ravenCodeAnalysisPath = typeof(RavenWorkspace).Assembly.Location;

        _ = WriteProject(Path.Combine(root, "app"), "MacroFreestanding", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MacroFreestanding</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <ProjectReference Include="../macros/FreestandingMacros.rvnproj" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "app", "src", "main.rvn"), """
func Main() -> int => answer!()
""");

        _ = WriteProject(Path.Combine(root, "macros"), "FreestandingMacros", $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>FreestandingMacros</AssemblyName>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="main.rvn" />
    <Reference Include="Raven.CodeAnalysis">
      <HintPath>{{ravenCodeAnalysisPath}}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "macros", "main.rvn"), """
import Raven.CodeAnalysis.Macros.*

[assembly: RavenCompilerPlugin(typeof(AnswerMacro))]

class AnswerMacro: IMacroDefinition {
    val Name: string => "answer"

    func Expand(context: FreestandingMacroContext) -> FreestandingMacroExpansionResult {
        FreestandingMacroExpansionResult.Empty
    }
}
""");
    }

    private static void WriteFreestandingMacroExpansionLayout(string root, string expansionText)
    {
        var ravenCodeAnalysisPath = typeof(RavenWorkspace).Assembly.Location;

        _ = WriteProject(Path.Combine(root, "app"), "MacroFreestanding", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MacroFreestanding</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src/**/*.rvn" />
    <ProjectReference Include="../macros/FreestandingMacros.rvnproj" />
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "app", "src", "main.rvn"), """
func Main() -> int => answer!()
""");

        _ = WriteProject(Path.Combine(root, "macros"), "FreestandingMacros", $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>FreestandingMacros</AssemblyName>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="main.rvn" />
    <Reference Include="Raven.CodeAnalysis">
      <HintPath>{{ravenCodeAnalysisPath}}</HintPath>
    </Reference>
  </ItemGroup>
</Project>
""");
        WriteRavenFile(Path.Combine(root, "macros", "main.rvn"), CreateFreestandingMacroExpansionSource(expansionText));
    }

    private static string CreateFreestandingMacroExpansionSource(string expansionText)
    {
        return $$"""
import Raven.CodeAnalysis.Macros.*
import Raven.CodeAnalysis.Syntax.*
import Raven.CodeAnalysis.Syntax.SyntaxFactory.*

[assembly: RavenCompilerPlugin(typeof(AnswerMacro))]

class AnswerMacro: IMacroDefinition {
    val Name: string => "answer"

    func Expand(context: FreestandingMacroContext) -> FreestandingMacroExpansionResult {
        FreestandingMacroExpansionResult.FromExpression(ParseExpression("{{expansionText}}"))
    }
}
""";
    }

    private static async Task<string?> GetFreestandingMacroExpansionTextAsync(WorkspaceManager manager, DocumentUri appUri)
    {
        manager.TryGetDocumentContext(appUri, out var document, out var compilation).ShouldBeTrue();
        document.ShouldNotBeNull();
        compilation.ShouldNotBeNull();

        var syntaxTree = await document.GetSyntaxTreeAsync();
        syntaxTree.ShouldNotBeNull();

        var semanticModel = compilation.GetSemanticModel(syntaxTree!);
        var expression = syntaxTree.GetRoot().DescendantNodes().OfType<FreestandingMacroExpressionSyntax>().Single();
        var expansion = semanticModel.GetMacroExpansion(expression);
        return expansion?.Expression?.ToFullString().Trim();
    }

    private sealed class TestRefactoringProvider : CodeRefactoringProvider
    {
        public override void RegisterRefactorings(CodeRefactoringContext context)
        {
            context.RegisterRefactoring(CodeFixAction.Create("Test refactoring", static (solution, _) => solution));
        }
    }

    private sealed class CountingProjectSystemService : IProjectSystemService
    {
        private readonly IProjectSystemService _inner;

        public CountingProjectSystemService(IProjectSystemService inner)
        {
            _inner = inner;
        }

        public bool CanOpenProject(string projectFilePath)
            => _inner.CanOpenProject(projectFilePath);

        public IReadOnlyList<string> GetProjectReferencePaths(string projectFilePath)
            => _inner.GetProjectReferencePaths(projectFilePath);

        public ProjectId OpenProject(Workspace workspace, string projectFilePath)
        {
            OpenAttempts++;
            return _inner.OpenProject(workspace, projectFilePath);
        }

        public int OpenAttempts { get; private set; }

        public void SaveProject(Project project, string filePath)
            => _inner.SaveProject(project, filePath);
    }

    private sealed class FailOnceOnReloadProjectSystemService : IProjectSystemService
    {
        private readonly IProjectSystemService _inner;

        public FailOnceOnReloadProjectSystemService(IProjectSystemService inner)
        {
            _inner = inner;
        }

        public bool CanOpenProject(string projectFilePath)
            => _inner.CanOpenProject(projectFilePath);

        public IReadOnlyList<string> GetProjectReferencePaths(string projectFilePath)
            => _inner.GetProjectReferencePaths(projectFilePath);

        public ProjectId OpenProject(Workspace workspace, string projectFilePath)
        {
            OpenAttempts++;
            if (OpenAttempts == 2)
                throw new InvalidOperationException("Synthetic transient project reload failure.");

            return _inner.OpenProject(workspace, projectFilePath);
        }

        public int OpenAttempts { get; private set; }

        public void SaveProject(Project project, string filePath)
            => _inner.SaveProject(project, filePath);
    }

    private sealed class ThrowingProjectSystemService : IProjectSystemService
    {
        private readonly IProjectSystemService _inner;
        private readonly string _failingProjectPath;

        public ThrowingProjectSystemService(IProjectSystemService inner, string failingProjectPath)
        {
            _inner = inner;
            _failingProjectPath = Path.GetFullPath(failingProjectPath);
        }

        public bool CanOpenProject(string projectFilePath)
            => _inner.CanOpenProject(projectFilePath);

        public IReadOnlyList<string> GetProjectReferencePaths(string projectFilePath)
            => _inner.GetProjectReferencePaths(projectFilePath);

        public ProjectId OpenProject(Workspace workspace, string projectFilePath)
        {
            if (string.Equals(Path.GetFullPath(projectFilePath), _failingProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                OpenAttempts++;
                throw new InvalidOperationException("Synthetic project open failure.");
            }

            return _inner.OpenProject(workspace, projectFilePath);
        }

        public int OpenAttempts { get; private set; }

        public void SaveProject(Project project, string filePath)
            => _inner.SaveProject(project, filePath);
    }
}
