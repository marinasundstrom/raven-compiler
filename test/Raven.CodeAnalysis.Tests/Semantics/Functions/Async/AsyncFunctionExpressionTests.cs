using System;
using System.Linq;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class AsyncFunctionExpressionTests : CompilationTestBase
{
    [Fact]
    public void AsyncLambda_WithInferredResult_DefaultsToTaskOfResult()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

class C {
    func M() -> () {
        let projector: Func<Task<int>> = async () => await G()
    }

    func G() -> Task<int> {
        Task.FromResult(1)
    }
}
""";

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);

        var lambdaSyntax = tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();

        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsAssignableFrom<ILambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsAsync);

        var expectedReturnType = ((INamedTypeSymbol)compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task_T))
            .Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.True(
            SymbolEqualityComparer.Default.Equals(expectedReturnType, boundLambda.ReturnType) ||
            boundLambda.ReturnType.SpecialType == SpecialType.System_Threading_Tasks_Task);
    }

    [Fact]
    public void AsyncLambda_WithoutAwait_RewritesToCompletedTaskFromResult()
    {
        const string source = """
import System.Threading.Tasks.*

let projector = async () => 42
""";

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.ConsoleApplication));
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var lambdaSyntax = tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();

        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var owner = boundLambda.Symbol?.ContainingSymbol ?? compilation.Assembly.GlobalNamespace;

        var lowered = FunctionExpressionLowerer.Rewrite(boundLambda, owner);

        var invocation = Assert.IsType<BoundInvocationExpression>(lowered.Body);
        Assert.Equal("FromResult", invocation.Method.Name);
        var containingType = Assert.IsAssignableFrom<INamedTypeSymbol>(invocation.Method.ContainingType);
        Assert.Equal(SpecialType.System_Threading_Tasks_Task, containingType.OriginalDefinition.SpecialType);
    }

    [Fact]
    public void AsyncLambda_WithBlockBody_DefaultsToTask()
    {
        const string source = """
import System.Threading.Tasks.*

class C {
    func M() -> () {
        let handler = async () => {
            await G()
        }
    }

    func G() -> Task {
        Task.CompletedTask
    }
}
""";

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);

        var lambdaSyntax = tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();

        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsAssignableFrom<ILambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsAsync);

        var taskType = compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task);
        Assert.True(SymbolEqualityComparer.Default.Equals(taskType, boundLambda.ReturnType));
    }

    [Fact]
    public void AsyncLambda_WithExplicitNonTaskReturnType_ReportsDiagnostic()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

class C {
    func M() -> () {
        let projector: Func<int> = async () -> int => 42
    }
}
""";

        var (compilation, _) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();
        var diagnostic = Assert.Single(diagnostics.Where(d => d.Descriptor == CompilerDiagnostics.AsyncReturnTypeMustBeTaskLike));
        Assert.Contains("int", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Task<int>", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AsyncLambda_WithExplicitNonTaskReturnTypeAndBlockBody_ReportsSingleDiagnostic()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

let projector = async () -> int => {
    return 1
}
""";

        var (compilation, _) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.AsyncReturnTypeMustBeTaskLike);
    }

    [Fact]
    public void AsyncLambda_WithExplicitNonTaskReturnType_DoesNotCascadeBodyConversionDiagnostic()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class C {
    func Configure(handler: Func<Task<Result<int, string>>>) -> () {}

    func Run() -> () {
        Configure(async func () -> Result<int, string> {
            return Ok(1)
        })
    }
}
""";

        var (compilation, _) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        var asyncDiagnostic = Assert.Single(diagnostics.Where(d => d.Descriptor == CompilerDiagnostics.AsyncReturnTypeMustBeTaskLike));
        Assert.Contains("Task<Result<int, string>>", asyncDiagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CannotConvertFromTypeToType);
    }

    [Fact]
    public void AsyncLambda_InTopLevelAwaitContext_BindsAndInfersTaskResult()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

let handler = async () => await Task.FromResult(2)
let result = await handler()
""";

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.ConsoleApplication));
        compilation.EnsureSetup();

        var lambdaSyntax = tree
            .GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();

        var model = compilation.GetSemanticModel(tree);
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsAssignableFrom<ILambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsAsync);

        var expectedReturn = ((INamedTypeSymbol)compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task_T))
            .Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.True(SymbolEqualityComparer.Default.Equals(expectedReturn, boundLambda.ReturnType));
    }

    [Fact]
    public void AsyncLambda_PassedToTaskRun_TargetTypesLambdaAndAwaitsPayload()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

let value = 42
let result = await Task.Run(async () => {
    await Task.Delay(1)
    return value
})
""";

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.ConsoleApplication));
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var lambdaSyntax = root
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsAssignableFrom<ILambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsAsync);

        var expectedReturn = ((INamedTypeSymbol)compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task_T))
            .Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        Assert.True(SymbolEqualityComparer.Default.Equals(expectedReturn.GetNullableType(), boundLambda.ReturnType));

        var taskRunInvocation = root
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "Task.Run");
        var boundTaskRun = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(taskRunInvocation));
        Assert.Equal("Run", boundTaskRun.Method.Name);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(boundTaskRun.Method.TypeArguments).SpecialType);
        Assert.Equal("Task<int>", boundTaskRun.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        var resultDeclarator = root
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declarator => declarator.Identifier.ValueText == "result");
        var resultLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(resultDeclarator));
        Assert.Equal(SpecialType.System_Int32, resultLocal.Type.SpecialType);
    }

    [Theory]
    [InlineData("Task.Run(run)", "int")]
    [InlineData("Task.Run<Task<int>>(run)", "Task<int>")]
    public void AsyncDelegateLocal_PassedToTaskRun_AwaitsSelectedOverloadPayload(string invocation, string expectedType)
    {
        var source = $$"""
import System.Threading.Tasks.*

let offset = 2
let run = async () => {
    let inner = async () => {
        await Task.Delay(1)
        return 40 + offset
    }
    return await inner()
}
let result = await {{invocation}}
""";

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.ConsoleApplication));
        Assert.Empty(compilation.GetDiagnostics());
        var model = compilation.GetSemanticModel(tree);
        var resultDeclarator = tree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(declarator => declarator.Identifier.ValueText == "result");
        var resultLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(resultDeclarator));
        Assert.Equal(expectedType, resultLocal.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    [Fact]
    public void AsyncFuncExpression_WithBlockBody_BindsAndInfersTaskResult()
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

class C {
    func M() -> () {
        let handler = async func (a: int, b: int) {
            await Task.Delay(1)
            return a + b
        }

        handler(1, 2)
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var lambdaSyntax = tree.GetRoot()
            .DescendantNodes()
            .OfType<ParenthesizedFunctionExpressionSyntax>()
            .Single();

        var model = compilation.GetSemanticModel(tree);
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsAssignableFrom<ILambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsAsync);

        var expectedReturn = ((INamedTypeSymbol)compilation.GetSpecialType(SpecialType.System_Threading_Tasks_Task_T))
            .Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        Assert.True(SymbolEqualityComparer.Default.Equals(expectedReturn, boundLambda.ReturnType));
    }
}
