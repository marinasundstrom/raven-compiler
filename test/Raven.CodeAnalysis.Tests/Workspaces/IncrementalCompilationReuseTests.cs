using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class IncrementalCompilationReuseTests
{
    [Fact]
    public void MetadataParameterQueries_DoNotRetainDiscardedMetadataContext()
    {
        var metadataContext = QueryMetadataParameters();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(metadataContext.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueryMetadataParameters()
    {
        var compilation = Compilation.Create("test", syntaxTrees: [], references: TestMetadataReferences.Default);
        var type = compilation.GetTypeByMetadataName("System.String")!;
        var methods = type.GetMembers("Substring").OfType<IMethodSymbol>().ToArray();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
            Assert.NotEmpty(method.Parameters);
        return new WeakReference(GetMetadataLoadContext(compilation));
    }

    [Theory]
    [InlineData("class Before {}\n")]
    [InlineData("class Before { func Value( }")]
    public void WorkspaceCompilation_ReusesMetadata_WhenSemanticStateMustBeRebuilt(string source)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.CurrentSolution.AddProject("test", compilationOptions:
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Projects.Single();
        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);
        var document = project.AddDocument("main.rvn", SourceText.From("class Before {}"));
        workspace.TryApplyChanges(document.Project.Solution);
        var previous = workspace.GetCompilation(document.Project.Id);
        _ = previous.GetDiagnostics();

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source)));
        var current = workspace.GetCompilation(document.Project.Id);
        Assert.True(IncrementalExecutableOwnerAnalyzer.Analyze(
            previous.SyntaxTrees.Single(), current.SyntaxTrees.Single()).RequiresFullSemanticRebind);
        var diagnostics = current.GetDiagnostics();
        var cold = Compilation.Create("test", [SyntaxTree.ParseText(source)], TestMetadataReferences.Default,
            options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Equal(cold.GetDiagnostics().Select(diagnostic => diagnostic.ToString()),
            diagnostics.Select(diagnostic => diagnostic.ToString()));
        Assert.Same(GetMetadataLoadContext(previous), GetMetadataLoadContext(current));
    }

    [Fact]
    public void WorkspaceCompilation_ReusesMetadata_AfterReplacingSourceDeclarations()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.CurrentSolution.AddProject("test", compilationOptions:
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Projects.Single();
        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);
        var document = project.AddDocument("main.rvn", SourceText.From("class Before {}"));
        workspace.TryApplyChanges(document.Project.Solution);
        var previous = workspace.GetCompilation(document.Project.Id);
        Assert.NotNull(previous.GetTypeByMetadataName("Before"));

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id, SourceText.From("class After { func Value() -> int { return 42 } }")));
        var current = workspace.GetCompilation(document.Project.Id);
        Assert.NotNull(current.GetTypeByMetadataName("After"));
        Assert.Null(current.GetTypeByMetadataName("Before"));
        Assert.Empty(current.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Same(GetMetadataLoadContext(previous), GetMetadataLoadContext(current));
    }

    [Fact]
    public void IncrementalCompilation_DoesNotRetainPreviousCompilation()
    {
        var (current, previous) = CreateIncrementalCompilation();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(previous.IsAlive);
        Assert.Empty(current.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        GC.KeepAlive(current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Compilation Current, WeakReference Previous) CreateIncrementalCompilation()
    {
        var tree = SyntaxTree.ParseText("class Widget {}");
        var previous = Compilation.Create("test", [tree], TestMetadataReferences.Default, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        previous.EnsureSetup();
        _ = previous.DeclarationTable;
        var current = Compilation.Create("test", [tree], TestMetadataReferences.Default, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        current.AdoptIncrementalReuseFrom(previous);
        current.EnsureSetup();
        _ = current.DeclarationTable;
        Assert.Same(GetMetadataLoadContext(previous), GetMetadataLoadContext(current));
        return (current, new WeakReference(previous));
    }

    [Fact]
    public void DeclarationTable_RejectsDetachedSyntaxNodes()
    {
        var tree = SyntaxTree.ParseText("class Widget {}");
        var compilation = Compilation.Create("test", [tree], TestMetadataReferences.Default);
        var attached = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var detached = Assert.IsType<ClassDeclarationSyntax>(attached.WithParent(parent: null, position: 0));

        Assert.True(compilation.DeclarationTable.TryGetDeclKey(attached, out var attachedKey));
        Assert.NotNull(attachedKey);
        Assert.False(compilation.DeclarationTable.TryGetDeclKey(detached, out var detachedKey));
        Assert.Null(detachedKey);
    }

    private static object GetDescriptorState(Compilation compilation)
    {
        var field = typeof(Compilation).GetField("_descriptorState", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        var value = field!.GetValue(compilation);
        value.ShouldNotBeNull();
        return value!;
    }

    private static object GetMetadataLoadContext(Compilation compilation)
    {
        var field = typeof(Compilation).GetField("_metadataLoadContext", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        var value = field!.GetValue(compilation);
        value.ShouldNotBeNull();
        return value!;
    }

    [Fact]
    public void WorkspaceCompilation_ReusesDeclarationKeys_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping() -> string {
                        "pong"
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialStableRoot = (CompilationUnitSyntax)initialStableTree.GetRoot();
        var initialStableType = initialStableRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        initialCompilation.DeclarationTable.TryGetDeclKey(initialStableType, out var initialStableKey).ShouldBeTrue();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedStableRoot = (CompilationUnitSyntax)updatedStableTree.GetRoot();
        var updatedStableType = updatedStableRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        updatedCompilation.DeclarationTable.TryGetDeclKey(updatedStableType, out var updatedStableKey).ShouldBeTrue();

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedStableKey.ShouldBeSameAs(initialStableKey);
    }

    [Fact]
    public void WorkspaceCompilation_UsesDistinctDescriptorStatePerCompilationIncrement()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialState = GetDescriptorState(initialCompilation);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialStableIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        initialModel.GetSymbolInfo(initialStableIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialStableIdentifier).ShouldNotBeNull();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        2
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedState = GetDescriptorState(updatedCompilation);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedStableIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");
        updatedCompilation.GetSemanticModel(updatedTree);

        ReferenceEquals(initialState, updatedState).ShouldBeFalse();
        GetMetadataLoadContext(updatedCompilation).ShouldBeSameAs(GetMetadataLoadContext(initialCompilation));
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedStableIdentifier).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_ChangedPortableReferenceAtSamePath_DoesNotReuseMetadataLoadContext()
    {
        var firstReference = TestMetadataFactory.CreateFileReferenceFromSource(
            "class Marker { func First() {} }",
            "ChangedReference");
        var secondReference = TestMetadataFactory.CreateFileReferenceFromSource(
            "class Marker { func Second() {} }",
            "ChangedReference");
        var referenceDirectory = Path.Combine(Path.GetTempPath(), $"raven-changing-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(referenceDirectory);
        var referencePath = Path.Combine(referenceDirectory, "ChangedReference.dll");
        File.Copy(((PortableExecutableReference)firstReference).FilePath, referencePath);

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);
        project = project.AddMetadataReference(MetadataReference.CreateFromFile(referencePath));

        const string initialSource = """
            func Value() -> int {
                return 1
            }
            """;
        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single();
        initialCompilation.GetSemanticModel(initialTree);

        File.Copy(((PortableExecutableReference)secondReference).FilePath, referencePath, overwrite: true);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("return 1", "return 2", StringComparison.Ordinal)));
        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single();

        Should.NotThrow(() => updatedCompilation.GetSemanticModel(updatedTree));
        GetMetadataLoadContext(updatedCompilation).ShouldNotBeSameAs(GetMetadataLoadContext(initialCompilation));

        Directory.Delete(referenceDirectory, recursive: true);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesVisibleValueScopeDeclarations_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping(value: int) -> int {
                        let copy = value
                        return copy
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialStableTree);
        var initialScope = initialStableTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var initialDeclarations = initialModel.GetVisibleValueDeclarationsForTesting(initialScope);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedStableTree);
        var updatedScope = updatedStableTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var updatedDeclarations = updatedModel.GetVisibleValueDeclarationsForTesting(updatedScope);

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedDeclarations.Select(static declaration => declaration.Name).ShouldBe(["value"]);
        updatedDeclarations.Length.ShouldBe(initialDeclarations.Length);
        updatedDeclarations[0].DeclarationNode.Kind.ShouldBe(initialDeclarations[0].DeclarationNode.Kind);
        updatedDeclarations[0].DeclarationNode.Span.ShouldBe(initialDeclarations[0].DeclarationNode.Span);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesContextualBindingRootDescriptors_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialStableTree);
        var initialIdentifier = initialStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var initialDescriptor = initialModel.GetContextualBindingRootDescriptorForTesting(initialIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedStableTree);
        var updatedIdentifier = updatedStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var updatedDescriptor = updatedModel.GetContextualBindingRootDescriptorForTesting(updatedIdentifier);

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesExecutableOwnerDescriptors_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialStableTree);
        var initialIdentifier = initialStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "item");
        var initialDescriptor = initialModel.GetExecutableOwnerDescriptorForTesting(initialIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedStableTree);
        var updatedIdentifier = updatedStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "item");
        var updatedDescriptor = updatedModel.GetExecutableOwnerDescriptorForTesting(updatedIdentifier);

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesFunctionExpressionRebindRootDescriptors_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialStableTree);
        var initialFunction = initialStableTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var initialDescriptor = initialModel.GetFunctionExpressionRebindRootDescriptorForTesting(initialFunction);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedStableTree);
        var updatedFunction = updatedStableTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var updatedDescriptor = updatedModel.GetFunctionExpressionRebindRootDescriptorForTesting(updatedFunction);

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesBinderParentAnchorDescriptors_ForUnchangedSyntaxTreesAcrossDocumentEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "unchanged.rav",
            SourceText.From(
                """
                class Stable {
                    func Ping(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """),
            "/tmp/unchanged.rav").Project;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialStableTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialStableTree);
        var initialIdentifier = initialStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var initialDescriptor = initialModel.GetBinderParentAnchorDescriptorForTesting(initialIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Value() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedStableTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/unchanged.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedStableTree);
        var updatedIdentifier = updatedStableTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var updatedDescriptor = updatedModel.GetBinderParentAnchorDescriptorForTesting(updatedIdentifier);

        updatedStableTree.ShouldBeSameAs(initialStableTree);
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_TracksChangedExecutableOwners_ForChangedSyntaxTrees()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable() -> int {
                        2
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);
        _ = workspace.GetCompilation(projectId);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var changedMethod = updatedTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Changed");
        var stableMethod = updatedTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Stable");
        var changedDescriptors = updatedCompilation.GetChangedExecutableOwnerDescriptorsForTesting(updatedTree);

        changedDescriptors.ShouldContain(new Compilation.ExecutableOwnerDescriptor(changedMethod.Span, changedMethod.Kind));
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(changedMethod).ShouldBeTrue();
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(stableMethod).ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_WhitespaceOnlyBodyEdit_DoesNotMarkMethodAsChanged()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            class Edited {
                func Stable() -> int {
                    return missing
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        initialCompilation.GetDiagnostics()
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("return missing", "return  missing", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var changedDescriptors = updatedCompilation.GetChangedExecutableOwnerDescriptorsForTesting(updatedTree);

        changedDescriptors.ShouldBeEmpty();
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedMethod).ShouldBeFalse();
        updatedCompilation.GetDiagnostics()
            .ShouldContain(diagnostic =>
                diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext &&
                diagnostic.Location.SourceSpan.IntersectsWith(updatedMethod.Span));
    }

    [Fact]
    public void WorkspaceCompilation_ReusesContextualBindingRootDescriptors_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var initialDescriptor = initialModel.GetContextualBindingRootDescriptorForTesting(initialStableIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedStableIdentifier = updatedStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var updatedDescriptor = updatedModel.GetContextualBindingRootDescriptorForTesting(updatedStableIdentifier);
        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedStableIdentifier);

        updatedTree.ShouldNotBeSameAs(initialTree);
        matchedOwner.ShouldNotBeNull();
        matchedOwner.Value.PreviousSyntaxTree.ShouldBeSameAs(initialTree);
        matchedOwner.Value.CurrentOwner.ShouldBe(new Compilation.ExecutableOwnerDescriptor(updatedStableMethod.Span, updatedStableMethod.Kind));
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesNodeInterestSymbolDescriptors_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        initialModel.GetSymbolInfo(initialStableIdentifier).Symbol?.Name.ShouldBe("value");
        var initialDescriptor = initialModel.GetNodeInterestSymbolDescriptorForTesting(initialStableIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedStableIdentifier);
        var updatedInfo = updatedModel.GetSymbolInfo(updatedStableIdentifier);
        var updatedDescriptor = updatedModel.GetNodeInterestSymbolDescriptorForTesting(updatedStableIdentifier);

        matchedOwner.ShouldNotBeNull();
        updatedDescriptor.ShouldBe(initialDescriptor);
        updatedInfo.Symbol?.Name.ShouldBe("value");
    }

    [Fact]
    public void WorkspaceCompilation_FirstPostEditSymbolLookup_ForMatchedExecutableOwner_ReusesTransferredStateWithoutBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithPerformanceInstrumentation(instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        initialModel.GetSymbolInfo(initialStableIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialStableIdentifier).ShouldNotBeNull();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        instrumentation.BinderReentry.Reset();

        var updatedInfo = updatedModel.GetSymbolInfo(updatedStableIdentifier);

        updatedInfo.Symbol?.Name.ShouldBe("value");
        updatedModel.GetMatchedExecutableOwnerForTesting(updatedStableIdentifier).ShouldNotBeNull();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedStableIdentifier).ShouldBeTrue();
        instrumentation.BinderReentry.TotalBindExecutions.ShouldBe(0);
        instrumentation.BinderReentry.GetBindExecutionCount(updatedStableIdentifier).ShouldBe(0);
    }

    [Fact]
    public void WorkspaceCompilation_SemanticModel_PreparesDeclarationsForSemanticQueries()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        initialModel.GetSymbolInfo(initialStableIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialStableIdentifier).ShouldNotBeNull();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        2
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");
        var updatedStableMethod = updatedStableIdentifier.Ancestors().OfType<MethodDeclarationSyntax>().Single();
        var updatedStableParameter = updatedStableMethod.ParameterList!.Parameters.Single();

        updatedCompilation.SourceDeclarationsDeclared.ShouldBeFalse();
        updatedModel.MemberSignaturesDeclared.ShouldBeFalse();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedStableIdentifier).ShouldBeTrue();
        updatedModel.GetDeclaredSymbol(updatedStableMethod).ShouldBeAssignableTo<IMethodSymbol>();
        updatedModel.GetDeclaredSymbol(updatedStableParameter).ShouldBeAssignableTo<IParameterSymbol>();

        updatedModel.GetSymbolInfo(updatedStableIdentifier).Symbol?.Name.ShouldBe("value");
    }

    [Fact]
    public void WorkspaceCompilation_MethodSignatureSymbol_IsReusedWhenFullSemanticPassCompletes()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var signatureSymbol = model.GetDeclaredSymbol(method).ShouldBeAssignableTo<IMethodSymbol>();

        compilation.SourceDeclarationsDeclared.ShouldBeTrue();

        model.GetDiagnostics();

        var completedSymbol = model.GetDeclaredSymbol(method).ShouldBeAssignableTo<IMethodSymbol>();

        completedSymbol.ShouldBeSameAs(signatureSymbol);
        model.RootBinderCreated.ShouldBeTrue();
        compilation.SourceDeclarationsComplete.ShouldBeTrue();
        completedSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("int");
        completedSymbol.Parameters.Single().Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("int");
    }

    [Fact]
    public void WorkspaceCompilation_MetadataTypeLookup_DeclaresSourceDeclarationsWithoutRootBinder()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);

        var stringType = compilation.GetTypeByMetadataName("System.String");

        stringType.ShouldNotBeNull();
        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        compilation.SourceDeclarationsComplete.ShouldBeFalse();
        model.MemberSignaturesDeclared.ShouldBeTrue();
        model.RootBinderCreated.ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_EntryPointDiscovery_CompletesSourceDeclarationsWithoutRootBinder()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "main.rav",
            SourceText.From(
                """
                class Program {
                    static func Main() -> () {
                    }
                }
                """),
            "/tmp/main.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/main.rav");
        var model = compilation.GetSemanticModel(tree);

        var entryPoint = compilation.GetEntryPoint();

        entryPoint.ShouldNotBeNull();
        entryPoint.Name.ShouldBe("Main");
        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        compilation.SourceDeclarationsComplete.ShouldBeTrue();
        model.RootBinderCreated.ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_EventDeclarationSymbol_ResolvesThroughSemanticApi()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Button {
                    event Clicked: System.Action {
                        add { }
                        remove { }
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var eventDeclaration = tree.GetRoot().DescendantNodes().OfType<EventDeclarationSyntax>().Single();

        var eventSymbol = model.GetDeclaredSymbol(eventDeclaration).ShouldBeAssignableTo<IEventSymbol>();

        eventSymbol.Name.ShouldBe("Clicked");
        model.MemberSignaturesDeclared.ShouldBeTrue();
        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_AccessorDeclarationSymbol_ResolvesThroughSemanticApi()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Person {
                    val Name: string {
                        get => ""
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var accessor = tree.GetRoot().DescendantNodes().OfType<AccessorDeclarationSyntax>().Single();
        var property = accessor.Ancestors().OfType<PropertyDeclarationSyntax>().Single();

        var propertySymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();
        propertySymbol.GetMethod.ShouldNotBeNull();

        var accessorSymbol = model.GetDeclaredSymbol(accessor).ShouldBeAssignableTo<IMethodSymbol>();

        accessorSymbol.Name.ShouldBe("get_Name");
        model.MemberSignaturesDeclared.ShouldBeTrue();
        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_MethodSignatureSymbol_ResolvesDeclaredNamedType()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class User

                class Edited {
                    func Find(user: User) -> User {
                        return user
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var signatureSymbol = model.GetDeclaredSymbol(method).ShouldBeAssignableTo<IMethodSymbol>();

        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        signatureSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("User");
        signatureSymbol.Parameters.Single().Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("User");
    }

    [Fact]
    public void WorkspaceCompilation_AddedMethodOverload_DeclaresSignatureAfterEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Pick(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        _ = initialCompilation.GetDiagnostics();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Pick(value: int) -> int {
                        return value
                    }

                    func Pick(text: string) -> string {
                        return text
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var addedOverload = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.ParameterList!.Parameters.Single().Identifier.ValueText == "text");

        var addedSymbol = updatedModel.GetDeclaredSymbol(addedOverload).ShouldBeAssignableTo<IMethodSymbol>();

        updatedCompilation.SourceDeclarationsDeclared.ShouldBeTrue();
        addedSymbol.Name.ShouldBe("Pick");
        addedSymbol.Parameters.Single().Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");
        addedSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");
    }

    [Fact]
    public void WorkspaceCompilation_InsertSameArityOverloadBeforeExistingMethod_ReusesExistingMethodDescriptors()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Pick(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialValueReference = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "value");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        initialModel.GetSymbolInfo(initialValueReference).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialValueReference).ShouldNotBeNull();

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Pick(text: string) -> string {
                        return text
                    }

                    func Pick(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var unchangedMethodValueReference = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.ParameterList!.Parameters.Single().Identifier.ValueText == "value")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "value");

        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(unchangedMethodValueReference).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_PropertySignatureSymbol_IsReusedWhenFullSemanticPassCompletes()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    val Name: string
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();

        var signatureSymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();

        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        signatureSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");

        model.GetDiagnostics();

        var completedSymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();

        completedSymbol.ShouldBeSameAs(signatureSymbol);
        model.RootBinderCreated.ShouldBeTrue();
        compilation.SourceDeclarationsComplete.ShouldBeTrue();
        completedSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");
        completedSymbol.GetMethod.ShouldNotBeNull();
    }

    [Fact]
    public void WorkspaceCompilation_InterfacePropertySignatureSymbol_IsReusedWhenFullSemanticPassCompletes()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                interface IEdited {
                    val Name: string
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();

        var signatureSymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();

        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        signatureSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");

        model.GetDiagnostics();

        var completedSymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();

        completedSymbol.ShouldBeSameAs(signatureSymbol);
        model.RootBinderCreated.ShouldBeTrue();
        compilation.SourceDeclarationsComplete.ShouldBeTrue();
        completedSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");
        completedSymbol.ContainingType?.TypeKind.ShouldBe(TypeKind.Interface);
    }

    [Fact]
    public void WorkspaceCompilation_PropertySignatureSymbol_ResolvesDeclaredNamedType()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class User

                class Edited {
                    val Owner: User
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();

        var signatureSymbol = model.GetDeclaredSymbol(property).ShouldBeAssignableTo<IPropertySymbol>();

        compilation.SourceDeclarationsDeclared.ShouldBeTrue();
        signatureSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("User");
    }

    [Fact]
    public void WorkspaceCompilation_PropertyRemovedAndReadded_ResolvesDeclaredPropertyAfterEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Foo {
                    val Test: string => ""
                }

                class Use {
                    func Read() -> string {
                        let foo = Foo()
                        return foo.Test
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialProperty = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single();

        initialModel.GetDeclaredSymbol(initialProperty).ShouldBeAssignableTo<IPropertySymbol>().Name.ShouldBe("Test");

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var withoutProperty = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Foo

                class Use {
                    func Read() -> string {
                        let foo = Foo()
                        return foo.Test
                    }
                }
                """));

        workspace.TryApplyChanges(withoutProperty);
        _ = workspace.GetCompilation(projectId).GetDiagnostics();

        var documentAfterRemoval = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var withPropertyAgain = workspace.CurrentSolution.WithDocumentText(
            documentAfterRemoval.Id,
            SourceText.From(
                """
                class Foo {
                    val Test: string => ""
                }

                class Use {
                    func Read() -> string {
                        let foo = Foo()
                        return foo.Test
                    }
                }
                """));

        workspace.TryApplyChanges(withPropertyAgain);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedProperty = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single();

        var updatedSymbol = updatedModel.GetDeclaredSymbol(updatedProperty).ShouldBeAssignableTo<IPropertySymbol>();

        updatedSymbol.Name.ShouldBe("Test");
        updatedSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).ShouldBe("string");
        updatedCompilation.GetDiagnostics().ShouldNotContain(diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.TypeAlreadyDefinesMember);
    }

    [Fact]
    public void WorkspaceCompilation_FirstPostEditMemberHoverLookup_ForMatchedExecutableOwner_ReusesParentBindingScope()
    {
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithPerformanceInstrumentation(instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class User(Name: string)

                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(user: User) -> string {
                        return user.Name
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialMemberAccess = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Single();
        var initialMemberName = (IdentifierNameSyntax)initialMemberAccess.Name;

        initialModel.GetSymbolInfo(initialMemberName).Symbol?.Name.ShouldBe("Name");

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class User(Name: string)

                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(user: User) -> string {
                        return user.Name
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedMemberAccess = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Single();
        var updatedMemberName = (IdentifierNameSyntax)updatedMemberAccess.Name;

        instrumentation.BinderReentry.Reset();
        updatedModel.RootBinderCreated.ShouldBeFalse();

        var updatedInfo = updatedModel.GetSymbolInfo(updatedMemberName);

        updatedInfo.Symbol?.Name.ShouldBe("Name");
        updatedModel.RootBinderCreated.ShouldBeFalse();
        updatedModel.GetMatchedExecutableOwnerForTesting(updatedMemberName).ShouldNotBeNull();
        instrumentation.BinderReentry.GetBindExecutionCount(updatedMemberName).ShouldBe(0);
        instrumentation.BinderReentry.GetBindExecutionCount(updatedMemberAccess).ShouldBe(0);
        instrumentation.BinderReentry.TotalBindExecutions.ShouldBe(0);
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_ForBodyEdit_DoNotCompleteFullSourceDeclarations()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable() -> int {
                        return 1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);
        _ = workspace.GetCompilation(projectId);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Stable() -> int {
                        return missing
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedMethod = updatedTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var diagnostics = updatedCompilation.GetDocumentDiagnostics(updatedTree, analyzerOptions: null, CancellationToken.None);

        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext &&
            diagnostic.Location.SourceSpan.IntersectsWith(updatedMethod.Span));
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedMethod).ShouldBeTrue();
        updatedCompilation.SourceDeclarationsComplete.ShouldBeFalse();
        updatedModel.RootBinderCreated.ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_LazilyDeclareLaterSourceReceiverMethod()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "contracts.rav",
            SourceText.From(
                """
                class Request {
                    var Status: VehicleStatusDto = VehicleStatusDto()

                    func ToStatus() -> int {
                        return Status.ToStatus()
                    }
                }

                class VehicleStatusDto {
                    func ToStatus() -> int {
                        return 1
                    }
                }
                """),
            "/tmp/contracts.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/contracts.rav");
        var model = compilation.GetSemanticModel(tree);

        var diagnostics = compilation.GetDocumentDiagnostics(tree, analyzerOptions: null, CancellationToken.None);

        diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.MemberDoesNotContainDefinition &&
            diagnostic.GetMessage().Contains("'VehicleStatusDto' has no member 'ToStatus'", StringComparison.Ordinal));

        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString() == "Status.ToStatus");
        var memberAccess = invocation.Expression.ShouldBeAssignableTo<MemberAccessExpressionSyntax>();

        var method = model.GetSymbolInfo(invocation).Symbol.ShouldBeAssignableTo<IMethodSymbol>();
        method.Name.ShouldBe("ToStatus");
        method.ReturnType.SpecialType.ShouldBe(SpecialType.System_Int32);
        model.GetTypeInfo(memberAccess.Expression).Type.ShouldBeAssignableTo<INamedTypeSymbol>()
            .Name.ShouldBe("VehicleStatusDto");
        compilation.SourceDeclarationsComplete.ShouldBeFalse();
        model.RootBinderCreated.ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_ForNestedLambdaBodyEdit_SeedsContainingBlockLocals()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string initialSource = """
            import System.*
            import System.Linq.*
            import System.Collections.Generic.*
            import System.Linq.Expressions.*

            class User(var Name: string, var Age: int, var IsActive: bool)

            func Main(users: IQueryable<User>) {
                let minAge = 21
                let onlyActiveAdults: Expression<System.Func<User, bool>> =
                    user => user.IsActive && user.Age >= minAge

                let query = users
                    |> Where(onlyActiveAdults)
                    |> OrderBy(user => user.Name)
                    |> Select(user => user.Name)
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        initialCompilation.GetDocumentDiagnostics(initialTree, analyzerOptions: null, CancellationToken.None)
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("let minAge = 21", "let minAge = 22", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var root = updatedTree.GetRoot();
        var minAgeReference = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "minAge");
        var onlyActiveAdultsDeclarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declarator => declarator.Identifier.ValueText == "onlyActiveAdults");
        var queryDeclarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declarator => declarator.Identifier.ValueText == "query");
        var onlyActiveAdultsReference = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "onlyActiveAdults");
        var whereIdentifier = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Where");

        var onlyActiveAdultsLocal = updatedModel.GetDeclaredSymbol(onlyActiveAdultsDeclarator)
            .ShouldBeAssignableTo<ILocalSymbol>();
        onlyActiveAdultsLocal.Name.ShouldBe("onlyActiveAdults");
        onlyActiveAdultsLocal.Type.TypeKind.ShouldNotBe(TypeKind.Error);

        var queryLocal = updatedModel.GetDeclaredSymbol(queryDeclarator)
            .ShouldBeAssignableTo<ILocalSymbol>();
        queryLocal.Name.ShouldBe("query");
        var whereSymbolInfoAfterQueryDeclaration = updatedModel.GetSymbolInfo(whereIdentifier);
        (whereSymbolInfoAfterQueryDeclaration.Symbol ?? whereSymbolInfoAfterQueryDeclaration.CandidateSymbols.FirstOrDefault())
            .ShouldBeAssignableTo<IMethodSymbol>();
        queryLocal.Type.TypeKind.ShouldNotBe(TypeKind.Error);

        var diagnostics = updatedCompilation.GetDocumentDiagnostics(updatedTree, analyzerOptions: null, CancellationToken.None);

        diagnostics.ShouldNotContain(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error,
            string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
        updatedModel.GetSymbolInfo(minAgeReference).Symbol?.Name.ShouldBe("minAge");
        updatedModel.GetSymbolInfo(onlyActiveAdultsReference).Symbol?.Name.ShouldBe("onlyActiveAdults");
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_ForNestedLambdaBodyEdit_UsesIncrementalPass()
    {
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithPerformanceInstrumentation(instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string initialSource = """
            import System.Linq.*

            class Edited {
                func Main(values: int[]) {
                    let doubled = values.Select(value => value + 1)
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        initialCompilation.GetDocumentDiagnostics(initialTree, analyzerOptions: null, CancellationToken.None)
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("value + 1", "value + 2", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();
        var diagnostics = updatedCompilation.GetDocumentDiagnostics(updatedTree, analyzerOptions: null, CancellationToken.None);
        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);

        diagnostics.ShouldNotContain(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error,
            string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
        delta.IncrementalPasses.ShouldBe(1);
        delta.FullPasses.ShouldBe(0);
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_AfterTransferredBodyEdit_PreservesDeclarationDiagnostics()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string initialSource = """
            import System.Linq.*

            class Edited {
                val Duplicate: int => 1

                val Duplicate: int => 2

                func Main(values: int[]) {
                    let doubled = values.Select(value => value + 1)
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        initialCompilation.GetDocumentDiagnostics(initialTree, analyzerOptions: null, CancellationToken.None)
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeAlreadyDefinesMember);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("value + 1", "value + 2", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedDiagnostics = updatedCompilation.GetDocumentDiagnostics(updatedTree, analyzerOptions: null, CancellationToken.None);

        updatedDiagnostics.ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeAlreadyDefinesMember);
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_ForTopLevelFunctionBodyEdit_UsesIncrementalPass()
    {
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithPerformanceInstrumentation(instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string initialSource = """
            func Main() -> int {
                return Helper()
            }

            func Helper() -> int {
                return 1
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        initialCompilation.GetDocumentDiagnostics(initialTree, analyzerOptions: null, CancellationToken.None)
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("return 1", "return 2", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();
        var diagnostics = updatedCompilation.GetDocumentDiagnostics(updatedTree, analyzerOptions: null, CancellationToken.None);
        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);

        diagnostics.ShouldNotContain(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error,
            string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
        delta.IncrementalPasses.ShouldBe(1);
        delta.FullPasses.ShouldBe(0);
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnostics_ForImportedNamespaceFunctionArityEdit_RebindsChangedOwner()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string initialMainSource = """
            import Utilities.*

            func Main() {
                let x = A(42)
                A(x)
            }
            """;

        const string updatedMainSource = """
            import Utilities.*

            func Main() {
                let x = A(42)
                A()
            }
            """;

        project = project.AddDocument(
            "main.rav",
            SourceText.From(initialMainSource),
            "/tmp/main.rav").Project;
        project = project.AddDocument(
            "test.rav",
            SourceText.From(
                """
                namespace Utilities

                public func A(x: int) -> int {
                    return 42 + x
                }
                """),
            "/tmp/test.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialMainTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/main.rav");
        initialCompilation.GetDocumentDiagnostics(initialMainTree, analyzerOptions: null)
            .ShouldNotContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);

        var mainDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/main.rav");
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            mainDocument.Id,
            SourceText.From(updatedMainSource)));

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedMainTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/main.rav");
        updatedCompilation.GetChangedExecutableOwnerDescriptorsForTesting(updatedMainTree).ShouldNotBeEmpty();
        var updatedMainRoot = updatedMainTree.GetRoot();
        var updatedFunction = updatedMainRoot.DescendantNodes().OfType<FunctionStatementSyntax>().Single();
        var updatedExpressionStatement = updatedMainRoot
            .DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Single();
        var updatedInvocation = updatedExpressionStatement.Expression.ShouldBeAssignableTo<InvocationExpressionSyntax>();
        updatedInvocation.ArgumentList.Arguments.Count.ShouldBe(0);
        var updatedModel = updatedCompilation.GetSemanticModel(updatedMainTree);
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedFunction).ShouldBeTrue();
        updatedModel.GetIncrementalSemanticQueryBinderForTesting(updatedFunction).ShouldBeAssignableTo<FunctionBinder>();
        var fullDiagnostics = updatedCompilation.GetDiagnostics(updatedMainTree, analyzerOptions: null);
        var boundStatement = updatedModel.GetBoundNode(updatedExpressionStatement).ShouldBeAssignableTo<BoundExpressionStatement>();
        var expressionError = boundStatement.Expression.ShouldBeAssignableTo<BoundErrorExpression>();
        expressionError.Reason.ShouldBe(BoundExpressionReason.OverloadResolutionFailed);
        fullDiagnostics.ShouldContain(diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod &&
            diagnostic.GetMessage().Contains("A", StringComparison.Ordinal));
        var diagnostics = updatedCompilation.GetDocumentDiagnostics(updatedMainTree, analyzerOptions: null);

        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod &&
            diagnostic.GetMessage().Contains("A", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceCompilation_ReusedSourceMethodAttributes_MapStaleDeclarationReferenceToCurrentTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                import System.*

                class Edited {
                    [Obsolete("old")]
                    func Stable() -> int {
                        1
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var initialSymbol = initialModel.GetDeclaredSymbol(initialMethod).ShouldBeAssignableTo<IMethodSymbol>();
        initialSymbol.GetAttributes().Single().AttributeClass?.Name.ShouldBe("ObsoleteAttribute");

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                import System.*

                class Edited {
                    [Obsolete("old")]
                    func Stable() -> int {
                        2
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var updatedSymbol = updatedModel.GetDeclaredSymbol(updatedMethod).ShouldBeAssignableTo<IMethodSymbol>();
        var updatedAttribute = updatedSymbol.GetAttributes().Single();

        updatedAttribute.AttributeClass?.Name.ShouldBe("ObsoleteAttribute");
        updatedAttribute.ConstructorArguments.Single().Value.ShouldBe("old");
    }

    [Fact]
    public void WorkspaceCompilation_ReusesBinderParentAnchorDescriptors_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var initialDescriptor = initialModel.GetBinderParentAnchorDescriptorForTesting(initialIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedIdentifier);
        var updatedDescriptor = updatedModel.GetBinderParentAnchorDescriptorForTesting(updatedIdentifier);

        matchedOwner.ShouldNotBeNull();
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_TransfersBinderParentAnchorDescriptors_ForStructuralBinders_WhenMatchedOwnerReused()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");

        _ = initialModel.GetBinder(initialStableMethod);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        return value
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");

        updatedCompilation.HasTransferredBinderParentAnchorDescriptorForTesting(updatedStableMethod).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_ReusesInterestBindingRootDescriptors_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var initialDescriptor = initialModel.GetInterestBindingRootDescriptorForTesting(initialIdentifier, includeExtendedExecutableRoots: false);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        if value > 0 {
                            return value
                        }

                        return 0
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");
        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedIdentifier);
        var updatedDescriptor = updatedModel.GetInterestBindingRootDescriptorForTesting(updatedIdentifier, includeExtendedExecutableRoots: false);

        matchedOwner.ShouldNotBeNull();
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_ReusesFunctionExpressionRebindRootDescriptors_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialFunction = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var initialDescriptor = initialModel.GetFunctionExpressionRebindRootDescriptorForTesting(initialFunction);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedFunction = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable")
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedFunction);
        var updatedDescriptor = updatedModel.GetFunctionExpressionRebindRootDescriptorForTesting(updatedFunction);

        matchedOwner.ShouldNotBeNull();
        updatedDescriptor.ShouldBe(initialDescriptor);
    }

    [Fact]
    public void WorkspaceCompilation_MatchesNestedFunctionExpressions_WhenEnclosingDeclarationInitializerChanged()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Main() -> int {
                        let minAge = 22
                        let predicate = func user: int -> bool => user > minAge
                        if predicate(30) {
                            return 1
                        }

                        return 0
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialFunction = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var initialDescriptor = initialModel.GetFunctionExpressionRebindRootDescriptorForTesting(initialFunction);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Main() -> int {
                        let minAge = 24
                        let predicate = func user: int -> bool => user > minAge
                        if predicate(30) {
                            return 1
                        }

                        return 0
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedFunction = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var updatedMinAgeIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "minAge");

        var matchedOwner = updatedModel.GetMatchedExecutableOwnerForTesting(updatedFunction);
        var updatedDescriptor = updatedModel.GetFunctionExpressionRebindRootDescriptorForTesting(updatedFunction);

        matchedOwner.ShouldNotBeNull();
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedFunction).ShouldBeFalse();
        updatedDescriptor.ShouldBe(initialDescriptor);
        updatedCompilation.HasTransferredFunctionExpressionRebindRootDescriptorForTesting(updatedFunction).ShouldBeTrue();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedMinAgeIdentifier).ShouldBeFalse();
        updatedModel.GetSymbolInfo(updatedMinAgeIdentifier).Symbol?.Name.ShouldBe("minAge");
    }

    [Fact]
    public void WorkspaceCompilation_TransfersNestedFunctionRebindRoot_WhenParentBodyDeclarationEditIsAfterFunction()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        let changed = 1
                        return projection(changed)
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialFunction = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();

        _ = initialModel.GetFunctionExpressionRebindRootDescriptorForTesting(initialFunction);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        let changed = 10
                        return projection(changed)
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedFunction = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();

        updatedModel.GetMatchedExecutableOwnerForTesting(updatedFunction).ShouldNotBeNull();
        updatedCompilation.HasTransferredFunctionExpressionRebindRootDescriptorForTesting(updatedFunction).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_TransfersMatchedOwnerBinderAndNodeInterestState_IntoNextIncrement()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var initialFunction = initialStableMethod
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var initialValueIdentifier = initialStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");

        _ = initialModel.GetFunctionExpressionRebindRootDescriptorForTesting(initialFunction);
        _ = initialModel.GetBinderParentAnchorDescriptorForTesting(initialValueIdentifier);
        initialModel.GetSymbolInfo(initialValueIdentifier).Symbol?.Name.ShouldBe("value");
        _ = initialModel.GetNodeInterestSymbolDescriptorForTesting(initialValueIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        let projection = func item: int -> int => item + value
                        return projection(value)
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedFunction = updatedStableMethod
            .DescendantNodes()
            .OfType<FunctionExpressionSyntax>()
            .Single();
        var updatedValueIdentifier = updatedStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "value");

        updatedCompilation.HasTransferredFunctionExpressionRebindRootDescriptorForTesting(updatedFunction).ShouldBeTrue();
        updatedCompilation.HasTransferredBinderParentAnchorDescriptorForTesting(updatedValueIdentifier).ShouldBeTrue();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedValueIdentifier).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_TransfersUnaffectedOwnerRelativeState_AcrossChangedMethodBodyWithShiftedSpans()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let before = value
                        let first = 1
                        let second = value
                        return before + second
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var initialValueIdentifiers = initialStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(node => node.Identifier.ValueText == "value")
            .ToArray();
        var initialBeforeValueIdentifier = initialValueIdentifiers[0];
        var initialSecondValueIdentifier = initialValueIdentifiers[1];

        initialModel.GetSymbolInfo(initialBeforeValueIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetSymbolInfo(initialSecondValueIdentifier).Symbol?.Name.ShouldBe("value");
        _ = initialModel.GetBinderParentAnchorDescriptorForTesting(initialBeforeValueIdentifier);
        _ = initialModel.GetBinderParentAnchorDescriptorForTesting(initialSecondValueIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let before = value
                        let first = 10
                        let second = value
                        return before + second
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedValueIdentifiers = updatedStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(node => node.Identifier.ValueText == "value")
            .ToArray();
        var updatedBeforeValueIdentifier = updatedValueIdentifiers[0];
        var updatedSecondValueIdentifier = updatedValueIdentifiers[1];

        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedStableMethod).ShouldBeTrue();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedBeforeValueIdentifier).ShouldBeTrue();
        updatedCompilation.HasTransferredBinderParentAnchorDescriptorForTesting(updatedBeforeValueIdentifier).ShouldBeTrue();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedSecondValueIdentifier).ShouldBeFalse();
        updatedCompilation.HasTransferredBinderParentAnchorDescriptorForTesting(updatedSecondValueIdentifier).ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_MethodSignatureChange_InvalidatesBodySymbolDescriptors()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let copy = value
                        return copy
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var initialValueIdentifier = initialStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        initialModel.GetSymbolInfo(initialValueIdentifier).Symbol?.Name.ShouldBe("value");
        _ = initialModel.GetNodeInterestSymbolDescriptorForTesting(initialValueIdentifier);
        _ = initialModel.GetBinderParentAnchorDescriptorForTesting(initialValueIdentifier);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Stable(item: int) -> int {
                        let copy = item
                        return copy
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedItemIdentifier = updatedStableMethod
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "item");

        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedStableMethod).ShouldBeTrue();
        updatedModel.GetMatchedExecutableOwnerForTesting(updatedItemIdentifier).ShouldNotBeNull();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedItemIdentifier).ShouldBeFalse();
        updatedCompilation.HasTransferredBinderParentAnchorDescriptorForTesting(updatedItemIdentifier).ShouldBeFalse();
        updatedModel.GetSymbolInfo(updatedItemIdentifier).Symbol?.Name.ShouldBe("item");
    }

    [Fact]
    public void WorkspaceCompilation_TransfersVisibleValueScopeDeclarations_ForMatchedExecutableOwnersInChangedSyntaxTree()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        1
                    }

                    func Stable(value: int) -> int {
                        let first = value
                        let second = value
                        return second
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialStableMethod = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var initialBlock = initialStableMethod.DescendantNodes().OfType<BlockStatementSyntax>().Single();

        var initialDeclarations = initialModel.GetVisibleValueDeclarationsForTesting(initialBlock);
        initialDeclarations.Select(static declaration => declaration.Name).ShouldBe(["second", "first"]);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Changed() -> int {
                        3
                    }

                    func Stable(value: int) -> int {
                        let first = value
                        let second = value
                        return second
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedStableMethod = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedBlock = updatedStableMethod.DescendantNodes().OfType<BlockStatementSyntax>().Single();

        updatedCompilation.HasTransferredVisibleValueScopeDeclarationsForTesting(updatedBlock).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_DoesNotTransferVisibleValueScopeDeclarations_AcrossBodyDeclarationEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let first = value
                        let second = value
                        return second
                    }
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialBlock = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<BlockStatementSyntax>()
            .Single();
        var initialBeforeValueIdentifier = initialBlock
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "value");

        initialModel.GetVisibleValueDeclarationsForTesting(initialBlock)
            .Select(static declaration => declaration.Name)
            .ShouldBe(["second", "first"]);
        _ = initialModel.GetContextualBindingRootDescriptorForTesting(initialBeforeValueIdentifier);
        _ = initialModel.GetInterestBindingRootDescriptorForTesting(initialBeforeValueIdentifier, includeExtendedExecutableRoots: false);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(
                """
                class Edited {
                    func Stable(value: int) -> int {
                        let first = value
                        let renamed = value
                        return renamed
                    }
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedBlock = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<BlockStatementSyntax>()
            .Single();
        var updatedBeforeValueIdentifier = updatedBlock
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "value");

        updatedCompilation.HasTransferredVisibleValueScopeDeclarationsForTesting(updatedBlock).ShouldBeFalse();
        updatedCompilation.HasTransferredContextualBindingRootDescriptorForTesting(updatedBeforeValueIdentifier).ShouldBeFalse();
        updatedCompilation.HasTransferredInterestBindingRootDescriptorForTesting(updatedBeforeValueIdentifier).ShouldBeFalse();
        updatedModel.GetVisibleValueDeclarationsForTesting(updatedBlock)
            .Select(static declaration => declaration.Name)
            .ShouldBe(["renamed", "first"]);
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterEdit_DoNotPoisonQueryableInvocationBinding()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "main.rav",
            SourceText.From(
                """
                import System.Linq.*

                func Main() -> () {
                    let minAge = 22
                    let query = [1, 2, 3]
                        .AsQueryable()
                        |> Where(value => value > minAge)
                        |> Select(value => value.ToString())

                    _ = query
                }
                """),
            "/tmp/main.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        _ = workspace.GetCompilation(projectId);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/main.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(
                """
                import System.Linq.*

                func Main() -> () {
                    let minAge = 24
                    let query = [1, 2, 3]
                        .AsQueryable()
                        |> Where(value => value > minAge)
                        |> Select(value => value.ToString())

                    _ = query
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedDiagnostics = updatedCompilation.GetDiagnostics();
        updatedDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/main.rav");
        var updatedRoot = updatedTree.GetRoot();
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);

        var queryDeclarator = updatedRoot.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.ValueText == "query");
        var queryLocal = Assert.IsAssignableFrom<ILocalSymbol>(updatedModel.GetDeclaredSymbol(queryDeclarator));
        queryLocal.Type.TypeKind.ShouldNotBe(TypeKind.Error);

        var whereIdentifier = updatedRoot.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "Where");
        var whereSymbolInfo = updatedModel.GetSymbolInfo(whereIdentifier);
        var whereSymbol = whereSymbolInfo.Symbol ?? whereSymbolInfo.CandidateSymbols.FirstOrDefault();

        Assert.IsAssignableFrom<IMethodSymbol>(whereSymbol);
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterEdit_ChainedQueryableInvocationRemainsBound()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "main.rav",
            SourceText.From(
                """
                import System.Linq.*

                func Main() -> () {
                    let minAge = 22
                    let query = [1, 2, 3]
                        .AsQueryable()
                        .Where(value => value > minAge)
                        .Select(value => value.ToString())

                    _ = query
                }
                """),
            "/tmp/main.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        _ = workspace.GetCompilation(projectId);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/main.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(
                """
                import System.Linq.*

                func Main() -> () {
                    let minAge = 24
                    let query = [1, 2, 3]
                        .AsQueryable()
                        .Where(value => value > minAge)
                        .Select(value => value.ToString())

                    _ = query
                }
                """));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedDiagnostics = updatedCompilation.GetDiagnostics();
        updatedDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/main.rav");
        var updatedRoot = updatedTree.GetRoot();
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);

        var whereIdentifier = updatedRoot.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(node => node.Identifier.ValueText == "Where");
        var whereSymbolInfo = updatedModel.GetSymbolInfo(whereIdentifier);
        var whereSymbol = whereSymbolInfo.Symbol ?? whereSymbolInfo.CandidateSymbols.FirstOrDefault();

        Assert.IsAssignableFrom<IMethodSymbol>(whereSymbol);
    }

    [Fact]
    public void WorkspaceCompilation_TransfersOwnerRelativeDescriptor_WhenGreenNodeSurvivesSiblingOwnerEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            class Edited {
                func Changed() -> int {
                    return 1
                }

                func Stable(value: int) -> int {
                    let changed = 1
                    return value + changed
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialValueIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        initialModel.GetSymbolInfo(initialValueIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialValueIdentifier).ShouldNotBeNull();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("return 1", "return 20", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedValueIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");

        updatedValueIdentifier.Green.ShouldBeSameAs(initialValueIdentifier.Green);
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedValueIdentifier).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_DropsOwnerRelativeDescriptor_WhenDescriptorGreenNodeIsEdited()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            class Edited {
                func Stable(value: int) -> int {
                    return value
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialValueIdentifier = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "value");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        initialModel.GetSymbolInfo(initialValueIdentifier).Symbol?.Name.ShouldBe("value");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialValueIdentifier).ShouldNotBeNull();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("return value", "return input", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedInputIdentifier = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(node => node.Identifier.ValueText == "input");

        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedInputIdentifier).ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterEdit_ReusesDiagnosticsForMatchedUnchangedExecutableOwners()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            class Edited {
                func Stable() -> int {
                    return missing
                }

                func Changed(value: int) -> int {
                    return value
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        initialCompilation.GetDiagnostics()
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("return value", "return value + 1", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedRoot = updatedTree.GetRoot();
        var updatedStableMethod = updatedRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Stable");
        var updatedChangedMethod = updatedRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Changed");

        updatedCompilation.HasTransferredSemanticDiagnosticsForTesting(updatedStableMethod).ShouldBeTrue();
        updatedCompilation.HasTransferredSemanticDiagnosticsForTesting(updatedChangedMethod).ShouldBeFalse();

        updatedCompilation.GetDiagnostics()
            .ShouldContain(diagnostic =>
                diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext &&
                diagnostic.Location.SourceSpan.IntersectsWith(updatedStableMethod.Span));
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterRapidTriviaEdits_ReusesDiagnosticsAcrossIntermediateSnapshot()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            class Runner {
                func Compute(value: int) -> int {
                    let answer = value + 1
                    return answer
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        initialCompilation.GetDiagnostics().ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var intermediateSource = initialSource.Replace("value + 1", "value  + 1", StringComparison.Ordinal);
        var intermediateSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(intermediateSource));

        workspace.TryApplyChanges(intermediateSolution);
        _ = workspace.GetCompilation(projectId);

        document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(intermediateSource.Replace("value  + 1", "value   + 1", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedMethod = updatedCompilation.SyntaxTrees.Single().GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        updatedCompilation.HasTransferredSemanticDiagnosticsForTesting(updatedMethod).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceCompilation_UndoAfterMalformedMemberAccess_MatchesColdCompilation()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        const string source = """
            union RepositoryError {
                case NotFound
            }

            func GetError() -> RepositoryError {
                return RepositoryError.NotFound
            }
            """;
        project = project.AddDocument(
            "edited.rav",
            SourceText.From(source),
            "/tmp/edited.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var malformedSource = source.Replace("NotFound\n", "NotF@ound\n", StringComparison.Ordinal);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(malformedSource)));

        _ = workspace.GetCompilation(projectId).GetDiagnostics();

        document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));

        var restoredDiagnostics = workspace.GetCompilation(projectId).GetDiagnostics();
        var coldTree = SyntaxTree.ParseText(source, path: "/tmp/edited.rav");
        var coldCompilation = Compilation.Create(
            "cold",
            [coldTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var coldDiagnostics = coldCompilation.GetDiagnostics();

        restoredDiagnostics.Select(ToComparableDiagnostic)
            .ShouldBe(coldDiagnostics.Select(ToComparableDiagnostic));

        static string ToComparableDiagnostic(Diagnostic diagnostic)
            => $"{diagnostic.Descriptor.Id}:{diagnostic.Location.SourceSpan}:{diagnostic.GetMessage()}";
    }

    [Fact]
    public void WorkspaceCompilation_DocumentDiagnosticsAfterBodyEdit_DeclaresSourceTypesOnDemand()
    {
        const int stableDocumentCount = 30;
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                performanceInstrumentation: instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        for (var index = 0; index < stableDocumentCount; index++)
        {
            project = project.AddDocument(
                $"stable{index}.rav",
                SourceText.From($$"""
                    class Stable{{index}} {
                        func Value() -> int {
                            {{index}}
                        }
                    }
                    """),
                $"/tmp/stable{index}.rav").Project;
        }

        const string initialSource = """
            class Runner {
                func Read(value: Stable0) -> int {
                    return value.Value() + 1
                }
            }
            """;
        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        initialCompilation.GetDiagnostics()
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents
            .Single(document => document.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("+ 1", "+ 2", StringComparison.Ordinal)));
        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        AssertDocumentDiagnosticsAreDemandDriven(updatedCompilation, updatedTree);

        editedDocument = workspace.CurrentSolution.GetProject(projectId)!.Documents
            .Single(document => document.FilePath == "/tmp/edited.rav");
        updatedSolution = workspace.CurrentSolution.WithDocumentText(
            editedDocument.Id,
            SourceText.From(initialSource.Replace("+ 1", "+ 3", StringComparison.Ordinal)));
        workspace.TryApplyChanges(updatedSolution);

        updatedCompilation = workspace.GetCompilation(projectId);
        updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        AssertDocumentDiagnosticsAreDemandDriven(updatedCompilation, updatedTree);

        void AssertDocumentDiagnosticsAreDemandDriven(Compilation compilation, SyntaxTree tree)
        {
            var setupBefore = instrumentation.Setup.CaptureSnapshot();
            var errors = compilation.GetDocumentDiagnostics(tree)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => $"{diagnostic.Descriptor.Id}: {diagnostic.GetMessage()}")
                .ToArray();
            errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors));

            var setupDelta = CompilerSetupInstrumentation.Subtract(
                instrumentation.Setup.CaptureSnapshot(),
                setupBefore);
            setupDelta.DeclarationPasses.ShouldBeLessThanOrEqualTo(2);
            setupDelta.EnsureSourceDeclarationsCompleteCalls.ShouldBe(0);
        }
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterEdit_RemovingRequiredObjectCreationArgumentReportsNoOverload()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let foo = Foo(
                Name: "Foo",
                Test: true
            )

            record Foo(
                val Name: string,
                val Test: bool
            )
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        workspace.GetCompilation(projectId).GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("    Test: true\n", string.Empty, StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);

        updatedCompilation.GetDiagnostics()
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterEdit_RemovingRecordParameterReportsNoOverloadAtStaleObjectCreationArgument()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let foo = Foo(
                Name: "Foo",
                Test: true
            )

            record Foo(
                val Name: string,
                val Test: bool
            )
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        workspace.GetCompilation(projectId).GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("    val Test: bool\n", string.Empty, StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);

        updatedCompilation.GetDiagnostics()
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterMixedDeclarationAndBodyEdit_DoesNotReuseStaleCallerDiagnostics()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let foo = Foo(
                Name: "Foo",
                Test: true
            )

            record Foo(
                val Name: string,
                val Test: bool
            )

            class Edited {
                func Changed() -> int {
                    return 1
                }
            }
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        workspace.GetCompilation(projectId).GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSource = initialSource
            .Replace("    val Test: bool\n", string.Empty, StringComparison.Ordinal)
            .Replace("return 1", "return 2", StringComparison.Ordinal);
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(updatedSource));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);

        updatedCompilation.GetDiagnostics()
            .ShouldContain(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }

    [Fact]
    public void WorkspaceCompilation_TransfersGlobalStatementHoverState_AcrossEarlierGlobalBodyEdit()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let first = 1
            let second = first + 1
            let text = second.ToString()
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialSecondReceiver = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "second");

        initialModel.GetSymbolInfo(initialSecondReceiver).Symbol?.Name.ShouldBe("second");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialSecondReceiver).ShouldNotBeNull();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("let first = 1", "let first = 10", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedSecondReceiver = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "second");

        updatedModel.GetMatchedExecutableOwnerForTesting(updatedSecondReceiver).ShouldNotBeNull();
        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedSecondReceiver).ShouldBeTrue();
        updatedModel.GetSymbolInfo(updatedSecondReceiver).Symbol?.Name.ShouldBe("second");
    }

    [Fact]
    public void WorkspaceCompilation_TransferredGlobalHoverState_AnswersSymbolInfo()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let first = 1
            let second = first + 1
            let text = second.ToString()
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialSecondReceiver = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "second");

        initialModel.GetSymbolInfo(initialSecondReceiver).Symbol?.Name.ShouldBe("second");
        initialModel.GetNodeInterestSymbolDescriptorForTesting(initialSecondReceiver).ShouldNotBeNull();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("let first = 1", "let first = 10", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedSecondReceiver = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Last(node => node.Identifier.ValueText == "second");

        updatedCompilation.HasTransferredNodeInterestSymbolDescriptorForTesting(updatedSecondReceiver).ShouldBeTrue();
        updatedModel.RootBinderCreated.ShouldBeFalse();
        updatedModel.GetSymbolInfo(updatedSecondReceiver).Symbol?.Name.ShouldBe("second");
    }

    [Fact]
    public void WorkspaceCompilation_RootBinderCreation_DoesNotEagerlyBindGlobalStatements()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                let first = 1
                let second = first + 1
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot().ShouldBeOfType<CompilationUnitSyntax>();
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToArray();

        model.EnsureRootBinderCreated();

        model.RootBinderCreated.ShouldBeTrue();
        model.HasCachedBoundNodeForTesting(root).ShouldBeFalse();
        model.HasCachedBoundNodeForTesting(globals[0].Statement).ShouldBeFalse();
        model.HasCachedBoundNodeForTesting(globals[1].Statement).ShouldBeFalse();
    }

    [Fact]
    public void WorkspaceCompilation_DiagnosticsAfterLaterGlobalEdit_BindsPriorGlobalsForScope()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let first = 1
            let second = first + 1
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        workspace.GetCompilation(projectId).GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("first + 1", "first + 2", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedRoot = updatedTree.GetRoot();
        var updatedGlobals = updatedRoot.DescendantNodes().OfType<GlobalStatementSyntax>().ToArray();
        var updatedFirstReference = updatedGlobals[1]
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "first");

        updatedCompilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
        updatedModel.IsExecutableOwnerMarkedChangedForTesting(updatedGlobals[1]).ShouldBeTrue();
        updatedModel.GetSymbolInfo(updatedFirstReference).Symbol?.Name.ShouldBe("first");
    }

    [Fact]
    public void WorkspaceCompilation_TopLevelStatementEdit_SymbolQueryDoesNotCreateRootBinder()
    {
        var instrumentation = new PerformanceInstrumentation();
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication)
                .WithPerformanceInstrumentation(instrumentation),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var initialSource = """
            let first = 1
            let second = first + 1
            """;

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(initialSource),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var initialModel = initialCompilation.GetSemanticModel(initialTree);
        var initialFirstReference = initialTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "first");

        initialModel.GetSymbolInfo(initialFirstReference).Symbol?.Name.ShouldBe("first");

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single(doc => doc.FilePath == "/tmp/edited.rav");
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(initialSource.Replace("first + 1", "first + 2", StringComparison.Ordinal)));

        workspace.TryApplyChanges(updatedSolution);

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var updatedModel = updatedCompilation.GetSemanticModel(updatedTree);
        var updatedFirstReference = updatedTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "first");

        instrumentation.BinderReentry.Reset();
        updatedModel.RootBinderCreated.ShouldBeFalse();

        var updatedInfo = updatedModel.GetSymbolInfo(updatedFirstReference);

        updatedInfo.Symbol?.Name.ShouldBe("first");
        updatedModel.RootBinderCreated.ShouldBeFalse();
        instrumentation.BinderReentry.TotalBindExecutions.ShouldBe(0);
    }

    [Fact]
    public void WorkspaceCompilation_RootBinderCreation_DeclaresTopLevelFunctionsWithoutBindingGlobalBodies()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "edited.rav",
            SourceText.From(
                """
                let value = compute()

                func compute() -> int {
                    return 1
                }
                """),
            "/tmp/edited.rav").Project;

        workspace.TryApplyChanges(project.Solution);

        var compilation = workspace.GetCompilation(projectId);
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath == "/tmp/edited.rav");
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot().ShouldBeOfType<CompilationUnitSyntax>();
        var globals = root.Members.OfType<GlobalStatementSyntax>().ToArray();

        model.EnsureRootBinderCreated();

        model.HasCachedBoundNodeForTesting(root).ShouldBeFalse();
        model.HasCachedBoundNodeForTesting(globals[0].Statement).ShouldBeFalse();
        model.HasCachedBoundNodeForTesting(globals[1].Statement).ShouldBeFalse();

        compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldBeEmpty();
    }

    [Fact]
    public void WorkspaceCompilation_AccessibilityConstraintDiagnostic_RecoversAfterUndo()
    {
        const string validSource = """
            internal interface Hidden {}

            internal class Exposer {
                public func Method<T>() where T: Hidden {}
            }
            """;
        const string invalidSource = """
            internal interface Hidden {}

            public class Exposer {
                public func Method<T>() where T: Hidden {}
            }
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "test",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        var document = project.AddDocument(
            "edited.rav",
            SourceText.From(validSource),
            "/tmp/edited.rav");
        workspace.TryApplyChanges(document.Project.Solution);

        workspace.GetCompilation(projectId).GetDiagnostics()
            .ShouldNotContain(static diagnostic => diagnostic.Id == "RAV0501");

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(invalidSource)));

        workspace.GetCompilation(projectId).GetDiagnostics()
            .ShouldContain(static diagnostic =>
                diagnostic.Id == "RAV0501" &&
                diagnostic.GetMessage().Contains("Hidden", StringComparison.Ordinal));

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(validSource)));

        workspace.GetCompilation(projectId).GetDiagnostics()
            .ShouldNotContain(static diagnostic => diagnostic.Id == "RAV0501");
    }

}
