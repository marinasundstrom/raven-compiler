using System.Linq;

using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Semantics.Tests;

public sealed class SemanticModelDiagnosticCachingTests : CompilationTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncLambdaLocalQuery_PreservesNamespaceFunctionSignatureForDiagnostics(bool malformedType)
    {
        var source = """
import System.*
import System.Threading.Tasks.*
class RequestContext {
    public val Text: string = "body"
}
func Main() -> unit {
    Accept(async func (context: RequestContext) {
        let content = await Task.FromResult(context.Text)
        return "submitted: $content"
    })
}
func Accept(handler: RequestContext -> Task<string>) -> unit { }
""";
        if (malformedType)
            source = source.Replace("handler: RequestContext ->", "handler: func (RequestContext) ->", StringComparison.Ordinal);
        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(node => node.Identifier.ValueText == "content");
        _ = model.GetDeclaredSymbol(declarator);
        var diagnostics = model.GetDocumentDiagnostics();
        if (malformedType)
            Assert.Contains(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        else
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GetDocumentDiagnostics_StoresBinderDiagnosticsUnderExecutableOwner()
    {
        var tree = SyntaxTree.ParseText("""
class C {
    func Test() {
        Missing()
    }
}
""");
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var diagnostics = model.GetDocumentDiagnostics();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RAV0103");

        Assert.True(model.TryGetCachedBoundDiagnostics(method, out var ownerDiagnostics));
        var diagnostic = Assert.Single(ownerDiagnostics.Where(diagnostic => diagnostic.Id == "RAV0103"));
        Assert.True(method.Span.IntersectsWith(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public void SpeculativeInitializerBinding_DoesNotLeakLambdaParametersIntoDocumentDiagnostics()
    {
        var tree = SyntaxTree.ParseText("""
class Box {
    func Select(selector: int -> int) -> int {
        return selector(1)
    }
}

class C {
    static func Main(box: Box) -> () {
        let vehicle = box.Select(vehicle => vehicle)
        vehicle.ToString()
    }
}
""");
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        _ = model.GetDeclaredSymbol(declarator);

        var diagnostics = model.GetDocumentDiagnostics();

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "RAV0168");
    }

    [Fact]
    public void SpeculativeInitializerBinding_DoesNotSplitDeclaredAndReferencedLocalSymbols()
    {
        var tree = SyntaxTree.ParseText("""
class Box {
    func Select(selector: int -> int) -> int {
        return selector(1)
    }
}

class C {
    static func Main(box: Box) -> () {
        let vehicle = box.Select(vehicle => vehicle)
        vehicle.ToString()
    }
}
""");
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        _ = model.GetDeclaredSymbol(declarator);

        var diagnostics = new UnusedVariableAnalyzer()
            .Analyze(compilation, tree)
            .Where(diagnostic => diagnostic.Id == UnusedVariableAnalyzer.DiagnosticId)
            .ToArray();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GetDocumentDiagnostics_AfterIncrementalQuery_DoesNotReuseSuppressedImportBinder()
    {
        var membersTree = SyntaxTree.ParseText("""
namespace Utilities

public func A(value: int) -> int {
    return value
}
""", path: "Members.rvn");
        var mainTree = SyntaxTree.ParseText("""
namespace App

import Utilities.*

func Main() -> int {
    let x = A(42)
    return x
}
""", path: "Main.rvn");
        var compilation = CreateCompilation([membersTree, mainTree]);
        var model = compilation.GetSemanticModel(mainTree);
        var invocation = mainTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.ToString().StartsWith("A(", StringComparison.Ordinal));

        _ = model.GetSymbolInfo(invocation);

        var diagnostics = model.GetDocumentDiagnostics();

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Id == "RAV0103" &&
            diagnostic.GetMessage().Contains("'A' is not in scope", StringComparison.Ordinal));
    }

    [Fact]
    public void GetDocumentDiagnostics_AfterForStatementSemanticQuery_KeepsEnclosingBlockLocalsInScope()
    {
        var tree = SyntaxTree.ParseText("""
import System.*
import System.Collections.Generic.*
import System.Text.Json.*

union JsonValue(string | double | bool | JsonObject | JsonValue[])
record JsonObject(Properties: IDictionary<string, JsonValue>)

class JsonObjectConverter {
    private static func ReadJsonValue(element: JsonElement, options: JsonSerializerOptions) -> JsonValue {
        if element.ValueKind is JsonValueKind.Array {
            let values = List<JsonValue>()

            for item in element.EnumerateArray() {
                values.Add(ReadJsonValue(item, options))
            }

            return JsonValue(values.ToArray())
        }

        if element.ValueKind is JsonValueKind.Object {
            let properties = Dictionary<string, JsonValue>()

            for property in element.EnumerateObject() {
                properties.Add(property.Name, ReadJsonValue(property.Value, options))
            }

            return JsonValue(JsonObject(properties))
        }

        throw JsonException("Unsupported JSON value kind.")
    }
}
""");
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(static declarator => declarator.Identifier.ValueText is "values" or "properties"))
        {
            _ = model.GetDeclaredSymbol(declarator);
        }

        foreach (var forStatement in root.DescendantNodes().OfType<ForStatementSyntax>())
        {
            _ = model.GetBoundNode(forStatement);
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            _ = model.GetBoundNode(invocation);
        }

        var diagnostics = model.GetDocumentDiagnostics();

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Id == "RAV0103" &&
            (diagnostic.GetMessage().Contains("'values' is not in scope", StringComparison.Ordinal) ||
             diagnostic.GetMessage().Contains("'properties' is not in scope", StringComparison.Ordinal)));
    }

    [Fact]
    public void GetSymbolInfo_ForStatement_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
func Main() {
label:
    goto label
    return
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var gotoStatement = tree.GetRoot().DescendantNodes().OfType<GotoStatementSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var info = model.GetSymbolInfo(gotoStatement);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        var symbol = Assert.IsAssignableFrom<ILabelSymbol>(info.Symbol);
        Assert.Equal("label", symbol.Name);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void AnalyzeControlFlow_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
func Main() {
    goto target
target:
    return
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var labeled = tree.GetRoot().DescendantNodes().OfType<LabeledStatementSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var analysis = model.AnalyzeControlFlow(labeled);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        var entry = Assert.Single(analysis.EntryPoints);
        Assert.Same(labeled, entry);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetLabelTarget_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
func Main() {
    goto target
target:
    return
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var gotoStatement = tree.GetRoot().DescendantNodes().OfType<GotoStatementSyntax>().Single();
        var labeled = tree.GetRoot().DescendantNodes().OfType<LabeledStatementSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var target = model.GetLabelTarget(gotoStatement);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.NotNull(target);
        Assert.Equal(labeled.Span, target.Span);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void HasExternalGotoToLabel_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
func Main() {
    goto target
target:
    return
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var labeled = tree.GetRoot().DescendantNodes().OfType<LabeledStatementSyntax>().Single();
        var region = new ControlFlowRegion(labeled);

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var hasExternalGoto = model.HasExternalGotoToLabel(labeled, region);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.True(hasExternalGoto);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetSymbolInfo_ForAttribute_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
class InfoAttribute : System.Attribute {
    public init(name: string) {}
}

[Info("Widget")]
class Widget {}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var attribute = tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var info = model.GetSymbolInfo(attribute);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.IsAssignableFrom<IMethodSymbol>(info.Symbol);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetConstantValue_ForBoundConstReference_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
class C {
    const Answer = 41
    const Next = Answer
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var answerReference = tree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Answer");

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var constant = model.GetConstantValue(answerReference);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.Equal(TypedConstantKind.Primitive, constant.Kind);
        Assert.Equal(41, constant.Value);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetOperation_ForExpressionStatement_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
class C {
    static func Main() -> () {
        System.Console.WriteLine("hello")
    }
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var statement = tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var operation = model.GetOperation(statement);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.NotNull(operation);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetMatchExhaustiveness_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
let result: Result<int, string> = .Ok(42)

let text = match result {
    .Ok(let value) => value.ToString()
}

union Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var info = model.GetMatchExhaustiveness(match);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.False(info.IsExhaustive);
        Assert.Contains("Error", info.MissingCases);
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void GetCapturedVariables_ForFunctionExpression_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
class C {
    static func Main() -> int {
        let offset = 1
        let read = func () -> int {
            offset
        }

        return read()
    }
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var lambda = tree.GetRoot().DescendantNodes().OfType<ParenthesizedFunctionExpressionSyntax>().Single();

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var captures = model.GetCapturedVariables(lambda);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.Contains(captures, static symbol => symbol is ILocalSymbol { Name: "offset" });
        Assert.Equal(0, delta.Calls);
    }

    [Fact]
    public void IsCapturedVariable_DoesNotTriggerDiagnosticBinding()
    {
        var instrumentation = new PerformanceInstrumentation();
        var options = new CompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            performanceInstrumentation: instrumentation);
        var tree = SyntaxTree.ParseText("""
class C {
    static func Main() -> int {
        let offset = 1
        let read = func () -> int {
            offset
        }

        return read()
    }
}
""");
        var compilation = CreateCompilation(tree, options: options);
        var model = compilation.GetSemanticModel(tree);
        var declarator = tree.GetRoot()
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declarator => declarator.Identifier.ValueText == "offset");
        var local = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(declarator));

        var before = instrumentation.DiagnosticBinding.CaptureSnapshot();

        var isCaptured = model.IsCapturedVariable(local);

        var delta = DiagnosticBindingInstrumentation.Subtract(
            instrumentation.DiagnosticBinding.CaptureSnapshot(),
            before);
        Assert.True(isCaptured);
        Assert.Equal(0, delta.Calls);
    }
}
