using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests.Utilities;

using Xunit;
using Xunit.Abstractions;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public sealed class AsyncFunctionExpressionStateMachineTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void TaskRun_async_lambda_executes_and_returns_value()
    {
        var output = CompileAndRun(
            """
            import System.Console.*
            import System.Threading.Tasks.*

            let value = await Task.Run(async () => {
                await Task.Delay(1)
                return 42
            })

            WriteLine(value)
            """
        );

        Assert.Equal(new[] { "42" }, output);
    }

    [Fact]
    public void TaskRun_async_lambda_with_capture_executes_and_returns_value()
    {
        var output = CompileAndRun(
            """
            import System.Console.*
            import System.Threading.Tasks.*

            let offset = 2
            let value = await Task.Run(async () => {
                await Task.Delay(1)
                return 40 + offset
            })

            WriteLine(value)
            """
        );

        Assert.Equal(new[] { "42" }, output);
    }

    [Fact]
    public void TopLevelAsyncMain_AsyncLambdaWithParameterAndOuterCapture_ExecutesAndPassesIlVerifyWhenToolAvailable()
    {
        const string code = """
            import System.*
            import System.Console.*
            import System.Threading.Tasks.*

            let offset = 10
            let handler = async func (value: int) -> Task<int> {
                await Task.Delay(1)
                return value + offset
            }

            let result = await handler(32)
            WriteLine(result)
            """;

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "42" }, output);

        VerifyIlWhenToolAvailable(
            "top_level_async_lambda_parameter_and_outer_capture",
            code,
            "IL verification failed for top-level async Main with lambda parameter plus real outer capture.");
    }

    [Theory]
    [InlineData("run()")]
    [InlineData("Task.Run(run)")]
    public void Nested_async_lambda_with_capture_executes_and_returns_value(string invocation)
    {
        var output = CompileAndRun(
            $$"""
            import System.Console.*
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
            WriteLine(result)
            """
        );

        Assert.Equal(new[] { "42" }, output);
    }

    [Fact]
    public void TopLevelAsyncMain_AsyncLambdaParameterHoistedIntoStateMachine_PassesIlVerifyWhenToolAvailable()
    {
        VerifyIlWhenToolAvailable(
            "top_level_async_lambda_parameter",
            """
            import System.*
            import System.Threading.Tasks.*

            func Register(handler: Func<Guid, Task<int>>) {
            }

            Register(async func (id: Guid) -> Task<int> {
                await Task.Delay(1)
                return id.GetHashCode()
            })

            await Task.Delay(1)
            """,
            "IL verification failed for top-level async Main with async lambda parameter hoisting.");
    }

    [Fact]
    public void TopLevelAsyncMain_AsyncIteratorLambdaWithOuterCapture_PassesIlVerifyWhenToolAvailable()
    {
        VerifyIlWhenToolAvailable(
            "top_level_async_iterator_lambda_outer_capture",
            """
            import System.*
            import System.Collections.Generic.*
            import System.Runtime.CompilerServices.*
            import System.Threading.*
            import System.Threading.Tasks.*

            func Register(handler: Func<CancellationToken, IAsyncEnumerable<int>>) {
            }

            let offset = 5
            Register(async func ([EnumeratorCancellation] cancellationToken: CancellationToken) -> IAsyncEnumerable<int> {
                yield 1 + offset
                if cancellationToken.IsCancellationRequested {
                    return
                }

                await Task.Delay(1, cancellationToken)
                yield 2 + offset
            })

            await Task.Delay(1)
            """,
            "IL verification failed for top-level async Main with async iterator lambda outer capture.");
    }

    private string[] CompileAndRun(string code)
    {
        var syntaxTree = SyntaxTree.ParseText(code);

        var compilation = Compilation.Create("async_lambdas", new CompilationOptions(OutputKind.ConsoleApplication))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(RuntimeMetadataReferences);

        compilation.EnsureSetup();
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, RuntimeMetadataReferences);
        var entryPoint = loaded.Assembly.EntryPoint!;
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            var parameters = entryPoint.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            var returnValue = entryPoint.Invoke(null, parameters);
            if (returnValue is Task task)
                task.GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();
    }

    private void VerifyIlWhenToolAvailable(string assemblyName, string code, string failureMessage)
    {
        if (!IlVerifyTestHelper.TryResolve(_output))
        {
            _output.WriteLine("Skipping IL verification because ilverify was not found.");
            return;
        }

        var syntaxTree = SyntaxTree.ParseText(code);

        var compilation = Compilation.Create(assemblyName, new CompilationOptions(OutputKind.ConsoleApplication))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(RuntimeMetadataReferences);

        compilation.EnsureSetup();

        var assemblyPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll");

        try
        {
            using (var peStream = File.Create(assemblyPath))
            {
                var result = compilation.Emit(peStream);
                Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            }

            var succeeded = IlVerifyRunner.Verify(null, assemblyPath, compilation);
            Assert.True(succeeded, failureMessage);
        }
        finally
        {
            if (File.Exists(assemblyPath))
                File.Delete(assemblyPath);
        }
    }

    private static readonly MetadataReference[] RuntimeMetadataReferences = GetRuntimeMetadataReferences();

    private static MetadataReference[] GetRuntimeMetadataReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(tpa))
            return TestMetadataReferences.Default;

        return tpa
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(MetadataReference.CreateFromFile)
            .ToArray();
    }
}
