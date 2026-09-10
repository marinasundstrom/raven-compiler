using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Mono.Cecil;

using Raven.CodeAnalysis.Testing;

using Xunit.Abstractions;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class MsBuildSampleProjectCompilationTests(ITestOutputHelper output)
{
    [Fact]
    public void RavenSdkProps_DeclaresImplicitImportItems()
    {
        var repoRoot = GetRepositoryRoot();
        var sdkPropsPath = Path.Combine(repoRoot, "sdk", "Raven.Sdk", "Sdk", "Sdk.props");
        var document = XDocument.Load(sdkPropsPath);

        var imports = document.Descendants("Import")
            .Select(static element => new
            {
                Include = element.Attribute("Include")?.Value,
                Static = element.Attribute("Static")?.Value
            })
            .ToArray();

        Assert.Contains(imports, static item => item.Include == "System");
        Assert.Contains(imports, static item => item.Include == "System.Linq");
        Assert.Contains(imports, static item => item.Include == "System.Result" && item.Static == "true");
        Assert.Contains(
            document.Descendants("ImplicitImports"),
            static property => property.Value == "enable");
    }

    [Fact]
    public void RavenWebSdk_ComposesDotNetWebSdkAndDeclaresWebImplicitImports()
    {
        var repoRoot = GetRepositoryRoot();
        var sdkDirectory = Path.Combine(repoRoot, "sdk", "Raven.Sdk.Web", "Sdk");
        var props = XDocument.Load(Path.Combine(sdkDirectory, "Sdk.props"));
        var targets = XDocument.Load(Path.Combine(sdkDirectory, "Sdk.targets"));

        Assert.Contains(
            props.Descendants("Import"),
            static element => element.Attribute("Project")?.Value == "Sdk.props"
                && element.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk.Web");

        var imports = props.Descendants("Import")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => value is not null)
            .ToArray();

        Assert.Contains("System", imports);
        Assert.Contains("System.Net.Http.Json", imports);
        Assert.Contains("Microsoft.AspNetCore.Builder", imports);
        Assert.Contains("Microsoft.Extensions.DependencyInjection", imports);

        Assert.Contains(
            targets.Descendants("Import"),
            static element => element.Attribute("Project")?.Value == "Sdk.targets"
                && element.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk.Web");
        Assert.Contains(
            targets.Descendants("Import"),
            static element => element.Attribute("Project")?.Value.EndsWith("Raven.Web.targets", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void RavenLanguageTargets_DisableReferenceAssemblyProduction()
    {
        var repoRoot = GetRepositoryRoot();
        var root = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(root, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <TargetFramework>net11.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var result = RunProcess(
                "dotnet",
                $"msbuild \"{projectPath}\" -getProperty:ProduceReferenceAssembly",
                root,
                timeoutMilliseconds: 300_000);

            Assert.True(
                result.ExitCode == 0,
                $"MSBuild property evaluation failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.Equal("false", result.StdOut.Trim());
        }
        finally
        {
            DeleteDirectoryIfExists(root);
        }
    }

    [Fact]
    public void ProjectReferenceWithReferenceOutputAssemblyFalse_IsNotACompilationReference()
    {
        var root = CreateTempDirectory();
        try
        {
            var referencedProjectPath = Path.Combine(root, "BuildDependency.csproj");
            File.WriteAllText(referencedProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var projectPath = Path.Combine(root, "App.rvnproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="BuildDependency.csproj"
                                      ReferenceOutputAssembly="false" />
                  </ItemGroup>
                </Project>
                """);

            MsBuildLocatorRegistration.EnsureRegistered();
            var evaluation = MsBuildProjectEvaluator.Evaluate(
                projectPath,
                RavenProjectConventions.Default);

            Assert.Empty(evaluation.ProjectReferencePaths);
        }
        finally
        {
            DeleteDirectoryIfExists(root);
        }
    }

    [Fact]
    public void NanoFrameworkProject_UsesStandardSdkWithRavenTargetProfile()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "nanoframework-blinky",
            "NanoFrameworkBlinky.rvnproj");

        MsBuildLocatorRegistration.EnsureRegistered();
        var evaluation = MsBuildProjectEvaluator.Evaluate(
            projectPath,
            RavenProjectConventions.Default);

        Assert.Equal("netnano1.0", evaluation.TargetFramework);
        Assert.False(evaluation.UseHostFrameworkReferences);
        Assert.True(evaluation.CompilationOptions.EmbedCoreTypes);
        Assert.Equal(FrameworkProjectionMode.None, evaluation.CompilationOptions.FrameworkProjectionMode);
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.CoreLibrary");
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.System.Device.Gpio");
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.Tools.MetadataProcessor.CLI");
    }

    [Fact]
    public void NanoFrameworkTemperatureProject_UsesStandardSdkWithRavenTargetProfile()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "nanoframework-temperature",
            "NanoFrameworkTemperature.rvnproj");

        MsBuildLocatorRegistration.EnsureRegistered();
        var evaluation = MsBuildProjectEvaluator.Evaluate(
            projectPath,
            RavenProjectConventions.Default);

        Assert.Equal("netnano1.0", evaluation.TargetFramework);
        Assert.False(evaluation.UseHostFrameworkReferences);
        Assert.True(evaluation.CompilationOptions.EmbedCoreTypes);
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.CoreLibrary");
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.Iot.Device.Dhtxx");
    }

    [Fact]
    public void NanoFrameworkDht22DisplayProject_UsesOnlySensorAndDisplayBindings()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "nanoframework-dht22-display",
            "NanoFrameworkDht22Display.rvnproj");

        MsBuildLocatorRegistration.EnsureRegistered();
        var evaluation = MsBuildProjectEvaluator.Evaluate(
            projectPath,
            RavenProjectConventions.Default);

        Assert.Equal("netnano1.0", evaluation.TargetFramework);
        Assert.False(evaluation.UseHostFrameworkReferences);
        Assert.True(evaluation.CompilationOptions.EmbedCoreTypes);
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.Iot.Device.Dhtxx");
        Assert.Contains(
            evaluation.PackageReferences,
            static reference => reference.Id == "nanoFramework.Iot.Device.Ssd13xx");
        Assert.DoesNotContain(
            evaluation.PackageReferences,
            static reference => reference.Id is
                "nanoFramework.System.Device.Wifi" or "nanoFramework.System.Net.Http");
    }

    [Fact]
    public void NanoFrameworkProject_ImportsPackagingTarget()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "nanoframework-blinky",
            "NanoFrameworkBlinky.rvnproj");
        var preprocessedProjectPath = Path.Combine(CreateTempDirectory(), "NanoFrameworkBlinky.preprocessed.xml");
        try
        {
            var result = RunProcess(
                "dotnet",
                $"msbuild \"{projectPath}\" -preprocess:\"{preprocessedProjectPath}\"",
                repoRoot,
                timeoutMilliseconds: 300_000);

            Assert.True(
                result.ExitCode == 0,
                $"MSBuild preprocessing failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

            var projectText = File.ReadAllText(preprocessedProjectPath);
            Assert.Contains("<NanoFrameworkPackageOnBuild", projectText, StringComparison.Ordinal);
            Assert.Contains("TaskName=\"RavenStageNanoFrameworkDebuggerSymbols\"", projectText, StringComparison.Ordinal);
            Assert.Contains("Name=\"RavenPackageNanoFrameworkApplication\"", projectText, StringComparison.Ordinal);
            Assert.Contains("Name=\"_CleanRavenNanoFrameworkArtifacts\"", projectText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(Path.GetDirectoryName(preprocessedProjectPath)!);
        }
    }

    public static IEnumerable<object[]> SampleProjects()
    {
        var repoRoot = GetRepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(repoRoot, "samples", "projects"), "*.rvnproj", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new object[] { Path.GetRelativePath(repoRoot, path) });
    }

    [Theory]
    [MemberData(nameof(SampleProjects))]
    public void SampleProject_CompilesThroughCompilerDriver(string relativeProjectPath)
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var outputDirectory = CreateTempDirectory();
        try
        {
            var result = RunCompiler(repoRoot, compilerDllPath, Path.Combine(repoRoot, relativeProjectPath), outputDirectory);
            Assert.True(result.ExitCode == 0,
                $"{relativeProjectPath}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
        }
        finally
        {
            DeleteDirectoryIfExists(outputDirectory);
        }
    }

    [Fact]
    public void NanoFrameworkWifiHttp_CompilesDeviceOnlyMethodReferencesWithoutBridge()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "nanoframework-wifi-http",
            "NanoFrameworkWifiHttp.rvnproj");
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var outputDirectory = CreateTempDirectory();

        try
        {
            var result = RunCompiler(repoRoot, compilerDllPath, projectPath, outputDirectory);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"Wi-Fi sample compilation failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            var assemblyPath = Path.Combine(outputDirectory, "NanoFrameworkWifiHttp.dll");
            Assert.True(File.Exists(assemblyPath));

            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            Assert.DoesNotContain(
                assembly.MainModule.AssemblyReferences,
                reference => reference.Name is "System.Net.Primitives" or "NanoFrameworkWifiHttp");
            Assert.Contains(
                assembly.MainModule.GetTypeReferences(),
                type => type.FullName == "System.Net.HttpStatusCode" &&
                        type.Scope is AssemblyNameReference { Name: "System.Net.Http" });
            Assert.Contains(
                assembly.MainModule.GetTypeReferences(),
                type => type.FullName == "System.Device.I2c.I2cDevice" &&
                        type.Scope is AssemblyNameReference { Name: "System.Device.I2c" });
            Assert.Contains(
                assembly.MainModule.GetTypeReferences(),
                type => type.FullName == "Iot.Device.Ssd13xx.Sh1106" &&
                        type.Scope is AssemblyNameReference { Name: "Iot.Device.Ssd13xx" });

            var oledMethodReferences = assembly.MainModule
                .GetMemberReferences()
                .OfType<MethodReference>()
                .Where(method => method.DeclaringType.FullName is
                    "System.Device.I2c.I2cDevice" or "Iot.Device.Ssd13xx.Ssd13xx")
                .ToArray();
            Assert.Contains(oledMethodReferences, method => method.Name == "Write");
            Assert.Contains(oledMethodReferences, method => method.Name == "DrawString");
            Assert.DoesNotContain(
                assembly.MainModule.GetTypeReferences(),
                type => type.FullName == "System.UIntPtr");

            var unionTypes = assembly.MainModule.Types
                .Where(type => type.Name == "NetworkRequestResult")
                .SelectMany(type => new[] { type }.Concat(type.NestedTypes))
                .ToArray();
            Assert.NotEmpty(unionTypes);
            Assert.All(
                unionTypes,
                type => Assert.DoesNotContain(
                    type.Methods,
                    method => method.Name is nameof(object.ToString) or
                        SynthesizedUnionMethodNames.DisplayNameHelper or
                        SynthesizedUnionMethodNames.FriendlyTypeNameHelper or
                        SynthesizedUnionMethodNames.FormatValueHelper));
        }
        finally
        {
            DeleteDirectoryIfExists(outputDirectory);
        }
    }

    [Fact]
    public void MacroDeclarationsSample_RunsThroughDotnetBuild()
    {
        var repoRoot = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repoRoot,
            "samples",
            "projects",
            "macro-declarations",
            "MacroDeclarations.rvnproj");
        var result = RunProcess(
            "dotnet",
            $"run --project \"{projectPath}\" --property WarningLevel=0",
            Path.GetDirectoryName(projectPath)!,
            timeoutMilliseconds: 300_000);
        output.WriteLine(result.StdOut);
        output.WriteLine(result.StdErr);

        Assert.True(
            result.ExitCode == 0,
            $"dotnet run failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
        Assert.Contains(
            $"42{Environment.NewLine}42{Environment.NewLine}6",
            result.StdOut,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RavenProject_BuildsThroughDotnetBuild()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "Library.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>RavenBuildOutput</AssemblyName>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                /// Greets a caller.
                class Greeter {
                    /// Gets the greeting.
                    ///
                    /// @result A greeting from the SDK build.
                    static func Message() -> string {
                        "Hello from dotnet build"
                    }
                }
                """);

            var result = RunProcess("dotnet", $"build \"{projectPath}\" --property WarningLevel=0", projectRoot, timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(result.ExitCode == 0, $"dotnet build failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.True(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "RavenBuildOutput.dll")),
                "Expected Raven project build output in the SDK target directory.");
            var xmlDocumentationPath = Path.Combine(projectRoot, "bin", "Debug", "net10.0", "RavenBuildOutput.xml");
            Assert.True(
                File.Exists(xmlDocumentationPath),
                "Expected default XML documentation beside the Raven library.");
            Assert.True(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "RavenBuildOutput.docs", "manifest.json")),
                "Expected default Markdown documentation beside the Raven library.");
            Assert.False(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "Raven.CodeAnalysis.dll")),
                "Ordinary Raven projects should not copy Raven.CodeAnalysis.");
            Assert.False(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "System.Private.CoreLib.dll")),
                "Raven projects should use the target runtime's core library instead of copying the compiler host's core library.");
            Assert.Contains(
                "<returns>A greeting from the SDK build.</returns>",
                File.ReadAllText(xmlDocumentationPath),
                StringComparison.Ordinal);

            var rebuildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --no-restore --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(rebuildResult.StdOut);
            output.WriteLine(rebuildResult.StdErr);
            Assert.True(
                rebuildResult.ExitCode == 0,
                $"Second dotnet build failed.\nstdout:\n{rebuildResult.StdOut}\nstderr:\n{rebuildResult.StdErr}");
            Assert.DoesNotContain("Raven CoreCompile:", rebuildResult.StdOut, StringComparison.Ordinal);

            var cleanResult = RunProcess(
                "dotnet",
                $"clean \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(cleanResult.StdOut);
            output.WriteLine(cleanResult.StdErr);
            Assert.True(
                cleanResult.ExitCode == 0,
                $"dotnet clean failed.\nstdout:\n{cleanResult.StdOut}\nstderr:\n{cleanResult.StdErr}");
            Assert.False(File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "RavenBuildOutput.dll")));
            Assert.False(File.Exists(xmlDocumentationPath));
            Assert.False(Directory.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "RavenBuildOutput.docs")));
            Assert.False(Directory.EnumerateFiles(
                Path.Combine(projectRoot, "obj", "Debug", "net10.0"),
                "*.rvn",
                SearchOption.AllDirectories).Any());
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenSdk_RecompilesDeclaredUnionWhenSwitchingFromNet11ToNet10()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        EnsureRavenCoreBuilt(repoRoot, "net11.0");
        EnsureRavenCoreBuilt(repoRoot, "net10.0");
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            void WriteProject(string targetFramework) => File.WriteAllText(projectPath, $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                        <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                        <TargetFramework>{{targetFramework}}</TargetFramework>
                        <OutputType>Exe</OutputType>
                      </PropertyGroup>
                    </Project>
                    """);

            WriteProject("net11.0");
            File.WriteAllText(Path.Combine(projectRoot, "App.rvn"), """
                import System.Console.*

                union Outcome {
                    case Success(value: int)
                    case Failure(message: string)
                }

                func Main() {
                    let outcome: Outcome = Outcome.Success(value: 42)
                    WriteLine(outcome)
                }
                """);

            var net11Result = RunProcess(
                "dotnet",
                $"run --project \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(net11Result.StdOut);
            output.WriteLine(net11Result.StdErr);
            Assert.True(
                net11Result.ExitCode == 0,
                $"The initial .NET 11 union project failed.\nstdout:\n{net11Result.StdOut}\nstderr:\n{net11Result.StdErr}");

            WriteProject("net10.0");
            var net10BuildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(net10BuildResult.StdOut);
            output.WriteLine(net10BuildResult.StdErr);
            Assert.True(
                net10BuildResult.ExitCode == 0,
                $"The .NET 11 Raven SDK failed to compile the union project after retargeting it to .NET 10.\nstdout:\n{net10BuildResult.StdOut}\nstderr:\n{net10BuildResult.StdErr}");
            Assert.Contains("Raven CoreCompile:", net10BuildResult.StdOut, StringComparison.Ordinal);

            var net10Result = RunProcess(
                "dotnet",
                $"run --project \"{projectPath}\" --no-build --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(net10Result.StdOut);
            output.WriteLine(net10Result.StdErr);

            Assert.True(
                net10Result.ExitCode == 0,
                $"The .NET 11 Raven SDK failed to run the union project after retargeting it to .NET 10.\nstdout:\n{net10Result.StdOut}\nstderr:\n{net10Result.StdErr}");
            Assert.Contains("42", net10Result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Theory]
    [InlineData("build")]
    [InlineData("run")]
    public void RavenProject_CompilerDiagnosticsAreForwardedByDotnetCommand(string command)
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                func Main() {
                    missingSymbol
                }
                """);

            var projectArgument = command == "run"
                ? $"--project \"{projectPath}\""
                : $"\"{projectPath}\"";
            var result = RunProcess(
                "dotnet",
                $"{command} {projectArgument} --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "main.rvn(2,5): error RAV0103: 'missingSymbol' is not in scope.",
                result.StdOut,
                StringComparison.Ordinal);
            Assert.Contains("error RAVENBUILD:", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_RebuildsWhenCompilerToolchainDependencyChanges()
    {
        var repoRoot = GetRepositoryRoot();
        var builtCompilerPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var compilerDirectory = Path.Combine(projectRoot, "compiler");
            CopyDirectory(Path.GetDirectoryName(builtCompilerPath)!, compilerDirectory);
            var compilerPath = Path.Combine(compilerDirectory, Path.GetFileName(builtCompilerPath));
            var codeAnalysisPath = Path.Combine(compilerDirectory, "Raven.CodeAnalysis.dll");
            Assert.True(File.Exists(codeAnalysisPath), $"Expected compiler dependency at '{codeAnalysisPath}'.");
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "Library.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Library.rvn"), "public class Library { }");

            var firstBuild = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(firstBuild.StdOut);
            output.WriteLine(firstBuild.StdErr);
            Assert.True(
                firstBuild.ExitCode == 0,
                $"Initial dotnet build failed.\nstdout:\n{firstBuild.StdOut}\nstderr:\n{firstBuild.StdErr}");

            File.SetLastWriteTimeUtc(codeAnalysisPath, DateTime.UtcNow.AddMinutes(1));

            var rebuild = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --no-restore --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(rebuild.StdOut);
            output.WriteLine(rebuild.StdErr);
            Assert.True(
                rebuild.ExitCode == 0,
                $"Compiler-triggered rebuild failed.\nstdout:\n{rebuild.StdOut}\nstderr:\n{rebuild.StdErr}");
            Assert.Contains("Raven CoreCompile:", rebuild.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_RebuildsWhenCompilerToolchainPathChanges()
    {
        var repoRoot = GetRepositoryRoot();
        var builtCompilerPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var firstCompilerDirectory = Path.Combine(projectRoot, "compiler-a");
            var secondCompilerDirectory = Path.Combine(projectRoot, "compiler-b");
            CopyDirectory(Path.GetDirectoryName(builtCompilerPath)!, firstCompilerDirectory);
            CopyDirectory(Path.GetDirectoryName(builtCompilerPath)!, secondCompilerDirectory);
            var firstCompilerPath = Path.Combine(firstCompilerDirectory, Path.GetFileName(builtCompilerPath));
            var secondCompilerPath = Path.Combine(secondCompilerDirectory, Path.GetFileName(builtCompilerPath));
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "Library.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Library.rvn"), "public class Library { }");

            var firstBuild = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0 --property:RavenCompilerHost=\"{firstCompilerPath}\"",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(firstBuild.StdOut);
            output.WriteLine(firstBuild.StdErr);
            Assert.True(
                firstBuild.ExitCode == 0,
                $"Initial dotnet build failed.\nstdout:\n{firstBuild.StdOut}\nstderr:\n{firstBuild.StdErr}");

            var rebuild = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --no-restore --property WarningLevel=0 --property:RavenCompilerHost=\"{secondCompilerPath}\"",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(rebuild.StdOut);
            output.WriteLine(rebuild.StdErr);
            Assert.True(
                rebuild.ExitCode == 0,
                $"Compiler-path rebuild failed.\nstdout:\n{rebuild.StdOut}\nstderr:\n{rebuild.StdErr}");
            Assert.Contains("Raven CoreCompile:", rebuild.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenClassLibrary_DotnetPackIncludesDocumentationSidecars()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var packageOutputPath = Path.Combine(projectRoot, "packages");
            var projectPath = Path.Combine(projectRoot, "DocumentedLibrary.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>DocumentedLibrary</AssemblyName>
                    <OutputType>Library</OutputType>
                    <PackageId>DocumentedLibrary</PackageId>
                    <Version>1.2.3</Version>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projectRoot, "Library.rvn"), """
                /// Provides a documented value.
                public class DocumentedValue {
                    /// Gets the value.
                    public static func Get() -> int => 42
                }
                """);

            var result = RunProcess(
                "dotnet",
                $"pack \"{projectPath}\" --output \"{packageOutputPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"dotnet pack failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

            var packagePath = Path.Combine(packageOutputPath, "DocumentedLibrary.1.2.3.nupkg");
            Assert.True(File.Exists(packagePath), $"Expected package at '{packagePath}'.");

            using var package = ZipFile.OpenRead(packagePath);
            var entries = package.Entries.Select(static entry => entry.FullName).ToArray();
            Assert.Contains("lib/net10.0/DocumentedLibrary.dll", entries);
            Assert.Contains("lib/net10.0/DocumentedLibrary.xml", entries);
            Assert.Contains("lib/net10.0/DocumentedLibrary.docs/manifest.json", entries);
            Assert.Contains(
                entries,
                static entry => entry.StartsWith(
                    "lib/net10.0/DocumentedLibrary.docs/invariant/symbols/",
                    StringComparison.Ordinal) && entry.EndsWith(".md", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_ForwardsConditionallyEvaluatedExternalConstantsToCompiler()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(RavenLedPin)' != ''">
                    <RavenConstant Include="LedPin" Value="$(RavenLedPin)" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projectRoot, "Program.rvn"), """
                import System.*

                extern const LedPin: int = 25

                func Main() {
                    Console.WriteLine(LedPin)
                }
                """);

            var buildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property:RavenLedPin=15 --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(buildResult.StdOut);
            output.WriteLine(buildResult.StdErr);

            Assert.True(
                buildResult.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");

            var runResult = RunProcess(
                "dotnet",
                $"run --project \"{projectPath}\" --no-build",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(runResult.StdOut);
            output.WriteLine(runResult.StdErr);

            Assert.True(
                runResult.ExitCode == 0,
                $"dotnet run failed.\nstdout:\n{runResult.StdOut}\nstderr:\n{runResult.StdErr}");
            Assert.Contains($"15{Environment.NewLine}", runResult.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_ExternalConstantOverridesTakePrecedenceOverProjectItems()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <RavenConstant Include="DeviceName" Value="project-default" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projectRoot, "Program.rvn"), """
                import System.*

                extern const DeviceName: string = "source-default"

                func Main() {
                    Console.WriteLine(DeviceName)
                }
                """);

            var overridePayload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("{\"DeviceName\":\"explicit-override\"}"));
            var buildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property:RavenExternalConstantOverrides={overridePayload} --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(buildResult.StdOut);
            output.WriteLine(buildResult.StdErr);

            Assert.True(
                buildResult.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");

            var runResult = RunProcess(
                "dotnet",
                $"run --project \"{projectPath}\" --no-build",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(runResult.StdOut);
            output.WriteLine(runResult.StdErr);

            Assert.True(
                runResult.ExitCode == 0,
                $"dotnet run failed.\nstdout:\n{runResult.StdOut}\nstderr:\n{runResult.StdErr}");
            Assert.Contains($"explicit-override{Environment.NewLine}", runResult.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain($"project-default{Environment.NewLine}", runResult.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_BuildsExplicitCompileItems_WhenDefaultItemsAreDisabled()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "Library.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="src/main.rvn" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), "class Included { }");
            File.WriteAllText(Path.Combine(sourceDirectory, "excluded.rvn"), "func broken(");

            var result = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.True(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "Library.dll")),
                "Expected the explicitly included Raven source to build without the excluded source.");
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_UsesActiveConfigurationAndInnerTargetFramework()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var projectPath = Path.Combine(projectRoot, "ConfiguredLibrary.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
                    <OutputType>Library</OutputType>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="release-net10.rvn" Condition="'$(Configuration)' == 'Release' and '$(TargetFramework)' == 'net10.0'" />
                    <Compile Include="wrong-context.rvn" Condition="'$(Configuration)' != 'Release' or '$(TargetFramework)' != 'net10.0'" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projectRoot, "release-net10.rvn"), "class ConfiguredLibrary { }");
            File.WriteAllText(Path.Combine(projectRoot, "wrong-context.rvn"), "func broken(");

            var result = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --configuration Release --framework net10.0 --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.True(
                File.Exists(Path.Combine(projectRoot, "bin", "Release", "net10.0", "ConfiguredLibrary.dll")),
                "Expected the active Release/net10.0 inner build output.");
            Assert.True(
                Directory.EnumerateFiles(
                    Path.Combine(projectRoot, "obj", "Release", "net10.0", "raven", "generated"),
                    "*.TargetFrameworkAttribute.g.rvn").Any(),
                "Expected generated Raven sources under the active inner-build intermediate directory.");
            Assert.False(
                Directory.Exists(Path.Combine(projectRoot, "obj", "Debug", "raven", "generated")),
                "The compiler must not fall back to Debug project evaluation.");
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_CompileMacro_DiscoversRuntimeDependencyClosureFromOutput()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>CompileMacroRuntimeDependency</AssemblyName>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="src/**/*.rvn" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                import System.*
                import Raven.Macros.*

                func Main() {
                    let increment = compile<System.Func<int, int>>! {
                        value => value + 1
                    }

                    Console.WriteLine(increment(41))
                }
                """);

            var buildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(buildResult.StdOut);
            output.WriteLine(buildResult.StdErr);

            Assert.True(
                buildResult.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");

            var outputDirectory = Path.Combine(projectRoot, "bin", "Debug", "net10.0");
            var codeAnalysisPath = Path.Combine(outputDirectory, "Raven.CodeAnalysis.dll");
            var depsPath = Path.Combine(outputDirectory, "CompileMacroRuntimeDependency.deps.json");
            Assert.True(
                File.Exists(codeAnalysisPath),
                $"Expected the macro runtime dependency at '{codeAnalysisPath}'.");
            Assert.Contains("Raven.CodeAnalysis", File.ReadAllText(depsPath), StringComparison.Ordinal);

            var runResult = RunProcess(
                "dotnet",
                $"run --project \"{projectPath}\" --no-build",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(runResult.StdOut);
            output.WriteLine(runResult.StdErr);

            Assert.True(
                runResult.ExitCode == 0,
                $"dotnet run failed.\nstdout:\n{runResult.StdOut}\nstderr:\n{runResult.StdErr}");
            Assert.Contains("42", runResult.StdOut, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                import System.*

                func Main() {
                    Console.WriteLine(42)
                }
                """);

            var rebuildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(rebuildResult.StdOut);
            output.WriteLine(rebuildResult.StdErr);

            Assert.True(
                rebuildResult.ExitCode == 0,
                $"dotnet rebuild failed.\nstdout:\n{rebuildResult.StdOut}\nstderr:\n{rebuildResult.StdErr}");
            var manifestPath = Path.Combine(
                projectRoot,
                "obj",
                "Debug",
                "net10.0",
                ".raven-runtime-dependencies");
            Assert.False(
                File.Exists(manifestPath),
                File.Exists(manifestPath)
                    ? $"Unexpected runtime dependencies:{Environment.NewLine}{File.ReadAllText(manifestPath)}"
                    : null);
            Assert.DoesNotContain(
                "Raven.CodeAnalysis",
                File.ReadAllText(depsPath),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_QuoteMacro_UsesExplicitCodeAnalysisReferenceWithGeneralDependencyClosure()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var codeAnalysisPath = Path.Combine(
            Path.GetDirectoryName(compilerDllPath)!,
            "Raven.CodeAnalysis.dll");
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>ExplicitCodeAnalysisReference</AssemblyName>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="src/**/*.rvn" />
                    <Reference Include="Raven.CodeAnalysis">
                      <HintPath>{{codeAnalysisPath}}</HintPath>
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                import System.*
                import Raven.Macros.*

                func Main() {
                    let syntax = quote! { 40 + 2 }
                    Console.WriteLine(syntax.ToString())
                }
                """);

            var buildResult = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(buildResult.StdOut);
            output.WriteLine(buildResult.StdErr);

            Assert.True(
                buildResult.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");

            var outputDirectory = Path.Combine(projectRoot, "bin", "Debug", "net10.0");
            Assert.True(File.Exists(Path.Combine(outputDirectory, "Raven.CodeAnalysis.dll")));
            var manifestPath = Path.Combine(
                projectRoot,
                "obj",
                "Debug",
                "net10.0",
                ".raven-runtime-dependencies");
            Assert.True(File.Exists(manifestPath));
            Assert.Contains(
                "Raven.CodeAnalysis.dll",
                File.ReadAllText(manifestPath),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_ReferencedQuoteMacro_BuildsFreshThroughCompilerDriver()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var macrosDirectory = Path.Combine(projectRoot, "macros");
            var appDirectory = Path.Combine(projectRoot, "app");
            Directory.CreateDirectory(macrosDirectory);
            Directory.CreateDirectory(appDirectory);

            var macroProjectPath = Path.Combine(macrosDirectory, "QuoteMacros.rvnproj");
            File.WriteAllText(macroProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>QuoteMacros</AssemblyName>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(macrosDirectory, "TwiceMacro.rvn"), """
                import Raven.CodeAnalysis.Macros.*
                import Raven.CodeAnalysis.Syntax.*
                import Raven.Macros.*

                [assembly: RavenCompilerPlugin]

                [MacroAlias("twice")]
                public macro Twice(expression: ExpressionSyntax) {
                    expand quote! {
                        #(expression) + #(expression)
                    }
                }
                """);

            var appProjectPath = Path.Combine(appDirectory, "App.rvnproj");
            File.WriteAllText(appProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>ReferencedQuoteMacro</AssemblyName>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{Path.GetRelativePath(appDirectory, macroProjectPath)}}" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(appDirectory, "Main.rvn"), """
                import System.*

                func Main() {
                    Console.WriteLine(twice!(21))
                }
                """);

            var outputDirectory = Path.Combine(projectRoot, "output");
            Directory.CreateDirectory(outputDirectory);
            var result = RunCompiler(repoRoot, compilerDllPath, appProjectPath, outputDirectory);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"rvnc failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.True(File.Exists(Path.Combine(outputDirectory, "ReferencedQuoteMacro.dll")));
            Assert.True(File.Exists(Path.Combine(macrosDirectory, "bin", "Debug", "net10.0", "QuoteMacros.dll")));
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_BuildsSameProjectMacroWithoutMacroProjectItem()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>SameProjectMacro</AssemblyName>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="src/**/*.rvn" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "macros.rvn"), """
                import Raven.CodeAnalysis.Macros.*

                class LocalAnswerMacro : IMacroDefinition {
                    val Name: string => "localAnswer"

                    func Expand(context: TokenTreeMacroContext) -> FreestandingMacroExpansionResult {
                        FreestandingMacroExpansionResult {
                            Expression = Raven.CodeAnalysis.Syntax.SyntaxFactory.ParseExpression("42")
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                class Harness {
                    public static func Value() -> int => localAnswer!{ }
                }
                """);

            var result = RunProcess(
                "dotnet",
                $"build \"{projectPath}\" --property WarningLevel=0",
                projectRoot,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(
                result.ExitCode == 0,
                $"dotnet build failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.True(
                File.Exists(Path.Combine(projectRoot, "bin", "Debug", "net10.0", "SameProjectMacro.dll")),
                "Expected the consumer assembly to be emitted.");
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenProject_BuildsThroughDotnetBuild_WithRavenCoreRuntimeDependency()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        EnsureRavenCoreBuilt(repoRoot, "net10.0");
        var projectRoot = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            var projectPath = Path.Combine(projectRoot, "App.rvnproj");
            File.WriteAllText(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>RavenCoreRuntimeDependency</AssemblyName>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="src/**/*.rvn" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(sourceDirectory, "main.rvn"), """
                import System.*

                func Main() {
                    Console.WriteLine("Raven.Core dependency")
                }
                """);

            var result = RunProcess("dotnet", $"build \"{projectPath}\" --property WarningLevel=0", projectRoot, timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(result.ExitCode == 0, $"dotnet build failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

            var outputDirectory = Path.Combine(projectRoot, "bin", "Debug", "net10.0");
            var depsPath = Path.Combine(outputDirectory, "RavenCoreRuntimeDependency.deps.json");
            var corePath = Path.Combine(outputDirectory, "Raven.Core.dll");
            var coreXmlPath = Path.Combine(outputDirectory, "Raven.Core.xml");
            var coreMarkdownRoot = Path.Combine(outputDirectory, "Raven.Core.docs");

            Assert.True(File.Exists(corePath), $"Expected Raven.Core copy-local output at '{corePath}'.");
            Assert.True(File.Exists(coreXmlPath), $"Expected Raven.Core XML documentation at '{coreXmlPath}'.");
            Assert.True(
                File.Exists(Path.Combine(coreMarkdownRoot, "manifest.json")),
                $"Expected Raven.Core Markdown documentation at '{coreMarkdownRoot}'.");
            Assert.NotEmpty(Directory.EnumerateFiles(coreMarkdownRoot, "*.md", SearchOption.AllDirectories));
            Assert.True(File.Exists(depsPath), $"Expected deps file at '{depsPath}'.");

            var depsJson = File.ReadAllText(depsPath);
            Assert.Contains("Raven.Core", depsJson);
        }
        finally
        {
            DeleteDirectoryIfExists(projectRoot);
        }
    }

    [Fact]
    public void RavenCoreProject_RebuildsWithoutReferencingPreviousOutput()
    {
        var repoRoot = GetRepositoryRoot();
        _ = EnsureCompilerBuilt(repoRoot);
        EnsureRavenCoreBuilt(repoRoot, "net10.0");
        var ravenCoreProjectPath = Path.Combine(repoRoot, "src", "Raven.Core", "Raven.Core.rvnproj");

        var result = RunProcess(
            "dotnet",
            $"build \"{ravenCoreProjectPath}\" --framework net10.0 --no-restore --target CoreCompile /property:WarningLevel=0 /property:RavenGenerateDocumentation=false",
            repoRoot,
            timeoutMilliseconds: 300_000);
        output.WriteLine(result.StdOut);
        output.WriteLine(result.StdErr);

        Assert.True(
            result.ExitCode == 0,
            $"Raven.Core rebuild failed. The core bootstrap must not reference an earlier Raven.Core output.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    }

    [Fact]
    public void CSharpProject_CanReferenceRavenProjectThroughProjectReference()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var root = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var ravenDirectory = Path.Combine(root, "raven");
            var csharpDirectory = Path.Combine(root, "csharp");
            Directory.CreateDirectory(ravenDirectory);
            Directory.CreateDirectory(csharpDirectory);

            File.WriteAllText(Path.Combine(ravenDirectory, "Greeter.rvnproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>GreeterLib</AssemblyName>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="main.rvn" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(ravenDirectory, "main.rvn"), """
                public class Greeter {
                    public static func Message() -> string {
                        "Hello from Raven reference"
                    }
                }
                """);

            File.WriteAllText(Path.Combine(csharpDirectory, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../raven/Greeter.rvnproj" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(csharpDirectory, "Program.cs"), """
                using System;

                Console.WriteLine(Greeter.Message());
                """);

            var appProjectPath = Path.Combine(csharpDirectory, "App.csproj");
            var result = RunProcess("dotnet", $"run --project \"{appProjectPath}\" --property WarningLevel=0", root, timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(result.ExitCode == 0, $"dotnet run failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.Contains("Hello from Raven reference", result.StdOut);
        }
        finally
        {
            DeleteDirectoryIfExists(root);
        }
    }

    [Fact]
    public void RavenProject_GlobalLanguageTargetsDoNotFlowToCSharpProjectReferences()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var root = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var libraryDirectory = Path.Combine(root, "library");
            var appDirectory = Path.Combine(root, "app");
            Directory.CreateDirectory(libraryDirectory);
            Directory.CreateDirectory(appDirectory);

            File.WriteAllText(Path.Combine(libraryDirectory, "Greeter.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(libraryDirectory, "Greeter.cs"), """
                public static class Greeter
                {
                    public static string Message => "Hello from C# reference";
                }
                """);

            File.WriteAllText(Path.Combine(appDirectory, "App.rvnproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Main.rvn" />
                    <ProjectReference Include="../library/Greeter.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(appDirectory, "Main.rvn"), """
                import System.*

                Console.WriteLine(Greeter.Message)
                """);

            var appProjectPath = Path.Combine(appDirectory, "App.rvnproj");
            var result = RunProcess(
                "dotnet",
                $"run --project \"{appProjectPath}\" --property:LanguageTargets=\"{languageTargetsPath}\" --property:RavenCompilerHost=\"{compilerDllPath}\" --property:WarningLevel=0",
                root,
                timeoutMilliseconds: 300_000);
            output.WriteLine(result.StdOut);
            output.WriteLine(result.StdErr);

            Assert.True(result.ExitCode == 0, $"dotnet run failed.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
            Assert.Contains("Hello from C# reference", result.StdOut);
        }
        finally
        {
            DeleteDirectoryIfExists(root);
        }
    }

    [Fact]
    public void RavenProject_PublishesReferencedRavenProjectWithoutDuplicateRuntimeDependency()
    {
        var repoRoot = GetRepositoryRoot();
        var compilerDllPath = EnsureCompilerBuilt(repoRoot);
        var root = CreateTempDirectory();
        try
        {
            var languageTargetsPath = Path.Combine(repoRoot, "build", "Raven.Language.targets");
            var libraryDirectory = Path.Combine(root, "library");
            var appDirectory = Path.Combine(root, "app");
            Directory.CreateDirectory(libraryDirectory);
            Directory.CreateDirectory(appDirectory);

            File.WriteAllText(Path.Combine(libraryDirectory, "Greeter.rvnproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>GreeterLib</AssemblyName>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Greeter.rvn" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(libraryDirectory, "Greeter.rvn"), """
                public class Greeter {
                    public static func Message() -> string {
                        "Hello from Raven reference"
                    }
                }
                """);

            File.WriteAllText(Path.Combine(appDirectory, "App.rvnproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <LanguageTargets>{{languageTargetsPath}}</LanguageTargets>
                    <RavenCompilerHost>{{compilerDllPath}}</RavenCompilerHost>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Main.rvn" />
                    <ProjectReference Include="../library/Greeter.rvnproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(appDirectory, "Main.rvn"), """
                import System.*

                func Main() {
                    Console.WriteLine(Greeter.Message())
                }
                """);

            var appProjectPath = Path.Combine(appDirectory, "App.rvnproj");
            var publishResult = RunProcess(
                "dotnet",
                $"publish \"{appProjectPath}\" --property WarningLevel=0",
                root,
                timeoutMilliseconds: 300_000);
            output.WriteLine(publishResult.StdOut);
            output.WriteLine(publishResult.StdErr);

            Assert.True(
                publishResult.ExitCode == 0,
                $"dotnet publish failed.\nstdout:\n{publishResult.StdOut}\nstderr:\n{publishResult.StdErr}");

            var publishDirectory = Path.Combine(appDirectory, "bin", "Release", "net10.0", "publish");
            Assert.Single(Directory.GetFiles(publishDirectory, "GreeterLib.dll"));

            var runResult = RunProcess(
                "dotnet",
                $"\"{Path.Combine(publishDirectory, "App.dll")}\"",
                root,
                timeoutMilliseconds: 300_000);
            Assert.True(
                runResult.ExitCode == 0,
                $"Published app failed.\nstdout:\n{runResult.StdOut}\nstderr:\n{runResult.StdErr}");
            Assert.Contains("Hello from Raven reference", runResult.StdOut);
        }
        finally
        {
            DeleteDirectoryIfExists(root);
        }
    }

    private static string EnsureCompilerBuilt(string repoRoot)
    {
        const string targetFramework = TestTargetFramework.Default;
        var compilerDllPath = Path.Combine(repoRoot, "src", "Raven.Compiler", "bin", "Debug", targetFramework, "rvnc.dll");
        if (!File.Exists(compilerDllPath))
        {
            var compilerProjectPath = Path.Combine(repoRoot, "src", "Raven.Compiler", "Raven.Compiler.csproj");
            var buildArgs = $"build \"{compilerProjectPath}\" --framework {targetFramework} /property:WarningLevel=0 /property:UseRavenCoreReference=false";
            var buildResult = RunProcess("dotnet", buildArgs, repoRoot, timeoutMilliseconds: 300_000);
            Assert.True(
                buildResult.ExitCode == 0,
                $"Failed to build rvnc compiler for sample-project tests.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");
        }

        Assert.True(File.Exists(compilerDllPath), $"Expected compiler output at '{compilerDllPath}'.");
        return compilerDllPath;
    }

    private static void EnsureRavenCoreBuilt(string repoRoot, string targetFramework)
    {
        var ravenCoreDllPath = Path.Combine(repoRoot, "src", "Raven.Core", "bin", "Debug", targetFramework, "Raven.Core.dll");
        if (File.Exists(ravenCoreDllPath))
            return;

        var ravenCoreProjectPath = Path.Combine(repoRoot, "src", "Raven.Core", "Raven.Core.rvnproj");
        var buildArgs = $"build \"{ravenCoreProjectPath}\" --framework {targetFramework} /property:WarningLevel=0";
        var buildResult = RunProcess("dotnet", buildArgs, repoRoot, timeoutMilliseconds: 300_000);
        Assert.True(
            buildResult.ExitCode == 0,
            $"Failed to build Raven.Core for sample-project tests.\nstdout:\n{buildResult.StdOut}\nstderr:\n{buildResult.StdErr}");

        Assert.True(File.Exists(ravenCoreDllPath), $"Expected Raven.Core output at '{ravenCoreDllPath}'.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCompiler(
        string repoRoot,
        string compilerDllPath,
        string projectPath,
        string outputDirectory)
    {
        var args = $"\"{compilerDllPath}\" \"{projectPath}\" -o \"{outputDirectory}\"";
        return RunProcess("dotnet", args, repoRoot, timeoutMilliseconds: 300_000);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName} process.");
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderrBuilder.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill failures in teardown paths.
            }

            _ = process.WaitForExit(5_000);
            return (-1, stdoutBuilder.ToString(), $"{stderrBuilder}{Environment.NewLine}Timed out after {timeoutMilliseconds}ms.");
        }

        _ = process.WaitForExit(5_000);
        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static string GetRepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "raven-msbuild-sample-project-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        Directory.Delete(path, recursive: true);
    }
}
