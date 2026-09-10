using System;
using System.Collections.Generic;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class EntryPointDiagnosticsTests
{
    public static IEnumerable<object[]> LegalMainSignatures()
    {
        var returnShapes = new (string Modifier, string ReturnType, string Body, SpecialType BridgeReturnType)[]
        {
            (string.Empty, "unit", "return;", SpecialType.System_Unit),
            (string.Empty, "int", "return 0", SpecialType.System_Int32),
            (string.Empty, "Task", "return Task.CompletedTask", SpecialType.System_Unit),
            (string.Empty, "Task<int>", "return Task.FromResult(0)", SpecialType.System_Int32),
            (string.Empty, "Result<int, string>", "return .Ok(0)", SpecialType.System_Int32),
            (string.Empty, "Result<(), string>", "return .Ok", SpecialType.System_Int32),
            ("async ", "Task<Result<int, string>>", "await Task.Yield()\nreturn .Ok(0)", SpecialType.System_Int32),
            ("async ", "Task<Result<(), string>>", "await Task.Yield()\nreturn .Ok", SpecialType.System_Int32),
        };

        foreach (var returnShape in returnShapes)
        {
            yield return
            [
                returnShape.Modifier,
                returnShape.ReturnType,
                returnShape.Body,
                string.Empty,
                returnShape.BridgeReturnType,
            ];
            yield return
            [
                returnShape.Modifier,
                returnShape.ReturnType,
                returnShape.Body,
                "args: string[]",
                returnShape.BridgeReturnType,
            ];
        }
    }

    [Theory]
    [MemberData(nameof(LegalMainSignatures))]
    public void ConsoleApp_AllLegalMainReturnAndParameterShapes_AreAccepted(
        string modifier,
        string returnType,
        string body,
        string parameters,
        SpecialType bridgeReturnType)
    {
        var code = $$"""
import System.Threading.Tasks.*
import System.*

class Program {
    static {{modifier}}func Main({{parameters}}) -> {{returnType}} {
        {{body}}
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "legal-entry-point",
            [tree],
            TestMetadataReferences.DefaultWithRavenCore,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.EntryPointHasInvalidSignature ||
            diagnostic.Descriptor == CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint);

        var entryPoint = Assert.IsAssignableFrom<IMethodSymbol>(compilation.GetEntryPoint());
        Assert.Equal(bridgeReturnType, entryPoint.ReturnType.SpecialType);
    }

    [Fact]
    public void SynthesizedEntryPointArguments_UseCompilationStringArrayType()
    {
        var compilation = Compilation.Create(
            "app",
            [SyntaxTree.ParseText("let value = 1")],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var entryPoint = Assert.IsAssignableFrom<IMethodSymbol>(compilation.GetEntryPoint());
        var parameter = Assert.Single(entryPoint.Parameters);
        var argsType = Assert.IsAssignableFrom<IArrayTypeSymbol>(parameter.Type);

        Assert.Equal(1, argsType.Rank);
        Assert.Equal(SpecialType.System_String, argsType.ElementType.SpecialType);
        Assert.Equal(SpecialType.System_Array, argsType.BaseType.SpecialType);
    }

    [Fact]
    public void ConsoleApp_WithoutMain_ProducesDiagnostic()
    {
        var tree = SyntaxTree.ParseText("");
        var compilation = Compilation.Create("app", [tree], TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint);
    }

    [Fact]
    public void ConsoleApp_WithMultipleMainMethods_ProducesAmbiguousDiagnostic()
    {
        var code = """
class Program {
    static func Main() -> unit {
        return;
    }
}

class Helper {
    static func Main() -> unit {
        return;
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create("app", [tree], TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.EntryPointIsAmbiguous);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint);
    }

    [Fact]
    public void TopLevelStatements_WithUserDefinedMain_ProducesAmbiguousDiagnostic()
    {
        var topLevel = SyntaxTree.ParseText("let x = 0");
        var mainClass = SyntaxTree.ParseText("""
class App {
    static func Main() -> unit {
        return;
    }
}
""");

        var compilation = Compilation.Create("app", new[] { topLevel, mainClass }, TestMetadataReferences.Default, new CompilationOptions(OutputKind.ConsoleApplication));
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.EntryPointIsAmbiguous);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint);
    }

    [Fact]
    public void ConsoleApp_WithValueTaskMain_ProducesInvalidSignatureDiagnostic()
    {
        var code = """
import System.Threading.Tasks.*

class Program {
    static func Main() -> ValueTask {
        return default(ValueTask);
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "app",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();

        var diagnostic = Assert.Single(diagnostics.Where(d => d.Descriptor == CompilerDiagnostics.EntryPointHasInvalidSignature));
        Assert.Equal("Main", tree.GetText().ToString(diagnostic.Location.SourceSpan));

        var entryPoint = compilation.GetEntryPoint();
        Assert.Null(entryPoint);
    }

    [Fact]
    public void ConsoleApp_WithResultOfStringMain_ProducesInvalidSignatureDiagnostic()
    {
        var code = """
public union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Program {
    static func Main() -> Result<string, string> {
        .Ok("done")
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "app",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();
        var diagnostic = Assert.Single(diagnostics.Where(d => d.Descriptor == CompilerDiagnostics.EntryPointHasInvalidSignature));
        Assert.Equal("Main", tree.GetText().ToString(diagnostic.Location.SourceSpan));
        Assert.Null(compilation.GetEntryPoint());
    }

    [Fact]
    public void ConsoleApp_WithTaskMain_SynthesizesBridge()
    {
        var code = """
import System.Threading.Tasks.*

class Program {
    static func Main() -> Task {
        return Task.CompletedTask;
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "app",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetEntryPointDiagnostics();
        Assert.True(diagnostics.IsDefaultOrEmpty, string.Join(Environment.NewLine, diagnostics));

        var allDiagnostics = compilation.GetDiagnostics();
        Assert.True(allDiagnostics.IsDefaultOrEmpty, string.Join(Environment.NewLine, allDiagnostics));

        Assert.NotEmpty(compilation.SourceGlobalNamespace.GetMembers());

        var methods = compilation.SourceGlobalNamespace
            .GetAllMembersRecursive()
            .OfType<IMethodSymbol>()
            .ToArray();
        var main = Assert.Single(methods, m => m.Name == EntryPointSignature.EntryPointName);

        Assert.True(EntryPointSignature.HasValidReturnType(main.ReturnType, compilation));
        Assert.True(EntryPointSignature.HasValidParameters(main.Parameters, compilation));
        Assert.True(main.IsStatic);
        Assert.False(main.IsGenericMethod);
        Assert.True(main.TypeParameters.IsDefaultOrEmpty);

        var candidates = methods.Where(compilation.IsEntryPointCandidate).ToArray();
        Assert.Single(candidates);

        var entryPoint = compilation.GetEntryPoint();

        var bridge = Assert.IsType<SynthesizedEntryPointBridgeMethodSymbol>(entryPoint);
        Assert.Equal(SpecialType.System_Unit, bridge.ReturnType.SpecialType);
        Assert.Equal(SymbolKind.Method, bridge.AsyncImplementation.Kind);
    }

    [Fact]
    public void WindowsApp_WithTaskOfIntMain_SynthesizesBridge()
    {
        var code = """
import System.Threading.Tasks.*

class Program {
    static func Main() -> Task<int> {
        return Task.FromResult(0);
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "app",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.WindowsApplication));

        var diagnostics = compilation.GetEntryPointDiagnostics();
        Assert.True(diagnostics.IsDefaultOrEmpty, string.Join(Environment.NewLine, diagnostics));

        var allDiagnostics = compilation.GetDiagnostics();
        Assert.True(allDiagnostics.IsDefaultOrEmpty, string.Join(Environment.NewLine, allDiagnostics));

        Assert.NotEmpty(compilation.SourceGlobalNamespace.GetMembers());

        var methods = compilation.SourceGlobalNamespace
            .GetAllMembersRecursive()
            .OfType<IMethodSymbol>()
            .ToArray();
        Assert.Contains(methods, m => m.Name == EntryPointSignature.EntryPointName);

        var entryPoint = compilation.GetEntryPoint();

        var bridge = Assert.IsType<SynthesizedEntryPointBridgeMethodSymbol>(entryPoint);
        Assert.Equal(SpecialType.System_Int32, bridge.ReturnType.SpecialType);
        Assert.Equal(SymbolKind.Method, bridge.AsyncImplementation.Kind);
    }

    [Fact]
    public void ClassLibrary_SynthesizesTopLevelProgramWithoutEntryPointDiagnostic()
    {
        var tree = SyntaxTree.ParseText("");
        var compilation = Compilation.Create(
            "lib",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint);

        var global = compilation.SourceGlobalNamespace;
        Assert.NotNull(global);

        var program = global.LookupType("Program");
        Assert.NotNull(program);
    }

    [Fact]
    public void TopLevelStatements_SynthesizeImplicitEntryPoint()
    {
        var tree = SyntaxTree.ParseText("let x = 0");
        var compilation = Compilation.Create(
            "app",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var entryPoint = compilation.GetEntryPoint();

        var method = Assert.IsAssignableFrom<IMethodSymbol>(entryPoint);
        Assert.True(method.IsImplicitlyDeclared);
        Assert.True(method.CanBeReferencedByName);
    }
}
