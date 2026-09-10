using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests;

public class FunctionExpressionCodeGenTests
{
    [Fact]
    public void Lambda_ExpressionBody_ReturnsSum()
    {
        var code = """
class Calculator {
    func Add() -> int {
        let add = (x: int, y: int) -> int => x + y
        return add(2, 3)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Calculator", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(5, value);
    }

    [Fact]
    public void Lambda_WithDiscardParameter_UsesDelegateSlotButDoesNotBindName()
    {
        var code = """
class Calculator {
    func Apply(callback: (string, string) -> string) -> string {
        return callback("ok", "ignored")
    }

    func Run() -> string {
        return Apply(func (value, _) => value)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Calculator", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (string)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal("ok", value);
    }

    [Fact]
    public void Lambda_ComparisonExpression_ReturnsExpectedResults()
    {
        var code = """
class Checker {
    func AreEqual(left: int, right: int) -> bool {
        let equals = (x: int, y: int) -> bool => x == y
        return equals(left, right)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Checker", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("AreEqual", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var trueResult = (bool)method.Invoke(instance, new object[] { 4, 4 })!;
        Assert.True(trueResult);

        var falseResult = (bool)method.Invoke(instance, new object[] { 3, 4 })!;
        Assert.False(falseResult);
    }

    [Fact]
    public void Lambda_BlockBody_ReturnsComputedValue()
    {
        var code = """
class Calculator {
    func Sum() -> int {
        let make = (x: int, y: int) -> int => {
            let total = x + y
            total
        }

        return make(4, 6)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Calculator", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Sum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(10, value);
    }

    [Fact]
    public void Lambda_BlockBody_WithExplicitReturn_ReturnsComputedValue()
    {
        var code = """
class Calculator {
    func Sum() -> int {
        let make = (x: int, y: int) -> int => {
            return x + y;
        }

        return make(4, 6)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Calculator", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Sum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(10, value);
    }

    [Fact]
    public void Lambda_CapturesParameter_ReturnsExpectedResult()
    {
        var code = """
class Calculator {
    func Combine(x: int) -> int {
        let add = (y: int) -> int => x + y
        return add(4)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Calculator", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Combine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, new object[] { 6 })!;
        Assert.Equal(10, value);
    }

    [Fact]
    public void Lambda_CapturesLocal_ReturnsValue()
    {
        var code = """
class Counter {
    func Multiply() -> int {
        let factor = 5
        let multiply = (value: int) -> int => factor * value
        return multiply(3)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Counter", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Multiply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(15, value);
    }

    [Fact]
    public void Lambda_WithPositionalDestructuredParameter_EmitsAndRuns()
    {
        var code = """
import System.*
class Picker {
    func Apply(projector: Func<(int, string), string>) -> string {
        return projector((2, "foo"))
    }

    func PickSecond() -> string {
        return Apply(((a, b)) => b)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Picker", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("PickSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (string)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal("foo", value);
    }

    [Fact]
    public void Lambda_CapturesSelfField_UsesInstanceState()
    {
        var code = """
class Holder {
    var value: int

    func Compute() -> int {
        self.value = 8
        let add = (offset: int) -> int => self.value + offset
        return add(7)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Holder", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Compute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(15, value); // self.value(8) + offset(7)
    }

    [Fact]
    public void Lambda_CapturesUnqualifiedInstanceProperty_UsesInstanceState()
    {
        var code = """
import System.*

class Owner {
    private val content: string = "Add"

    val Content: string => content

    func Create() -> Func<string> {
        return func () => Content
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Owner", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var func = (Func<string>)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal("Add", func());
    }

    [Fact]
    public void Lambda_InGenericType_CapturesLocalForLinqChain()
    {
        var code = """
import System.*
import System.Linq.*
import System.Reflection.*

class Container {
    val Value: Container.Base

    init(value: Container.Base) {
        Value = value
    }

    public open class Base {}
    public class Case : Container.Base {}
}

class Probe<T> {
    val _valueProperty: PropertyInfo?

    init() {
        _valueProperty = typeof(T).GetProperty("Value")
    }

    func CountAssignable() -> int {
        let valueProperty = _valueProperty!
        typeof(T)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => valueProperty.PropertyType.IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => t)
            .Count
    }
}

class Runner {
    func Run() -> int {
        Probe<Container>().CountAssignable()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Runner", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(2, value);
    }

    [Fact]
    public void Lambda_BlockBody_NullCoalesceReturnExpression_ReturnsFromLambda()
    {
        var code = """
class Handler {
    func Compute(input: string?) -> int {
        let lengthOrNegativeOne = (text: string?) -> int => {
            let required = text ?? return -1
            return required.Length
        }

        return lengthOrNegativeOne(input)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Handler", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Compute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var nullResult = (int)method.Invoke(instance, new object?[] { null })!;
        var valueResult = (int)method.Invoke(instance, new object?[] { "abcd" })!;

        Assert.Equal(-1, nullResult);
        Assert.Equal(4, valueResult);
    }

    [Fact]
    public void Lambda_BlockBody_NullCoalesceThrowExpression_ThrowsFromLambda()
    {
        var code = """
class Handler {
    func Compute(input: string?) -> int {
        let lengthOrThrow = (text: string?) -> int => {
            let required = text ?? throw System.InvalidOperationException("missing")
            return required.Length
        }

        return lengthOrThrow(input)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Handler", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Compute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(instance, new object?[] { null }));
        var invalidOperation = Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Equal("missing", invalidOperation.Message);
    }

    [Fact]
    public void Lambda_CapturesMultipleLocals_ObservesUpdatedValues()
    {
        var code = """
class Multi {
    func Run() -> int {
        var a = 1
        var b = 10
        var c = 100
        let sum = () -> int => a + b + c
        a = 2
        b = 20
        c = 200
        return sum()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Multi", throwOnError: true)!;


        // Reference semantics: lambda sees the post-assignment values (2+20+200 = 222).
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Equal(222, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    // ─── Reference-based capture (C#-style variable hoisting) ────────────────────

    [Fact]
    public void Lambda_MutableCapture_LambdaWriteReflectsInOuterMethod()
    {
        // The lambda writes to the captured local; the outer method reads the updated value.
        var code = """
class Counter {
    func CountItems() -> int {
        var count = 0
        let increment = () -> unit => { count = count + 1 }
        increment()
        increment()
        increment()
        return count
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Counter", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("CountItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        Assert.Equal(3, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void Lambda_MutableCapture_OuterWriteReflectsInLambda()
    {
        // The outer method writes to the captured local; the lambda reads the updated value.
        var code = """
class Spy {
    func Run() -> int {
        var value = 0
        let read = () -> int => value
        value = 42
        return read()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Spy", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        Assert.Equal(42, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void Lambda_MultipleLambdas_SharedCapturedLocal()
    {
        // Two lambdas sharing the same captured local: writes from one are visible to the other.
        var code = """
class Pair {
    func Run() -> int {
        var shared = 0
        let inc = () -> unit => { shared = shared + 10 }
        let dec = () -> unit => { shared = shared - 3 }
        inc()
        dec()
        inc()
        return shared
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Pair", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        // 0 + 10 - 3 + 10 = 17
        Assert.Equal(17, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void Lambda_ArgumentCapturesLocalAndUpdatesOuterMethod()
    {
        var code = """
class Runner {
    func Accept(action: () -> unit) -> unit {
        action()
    }

    func Run() -> int {
        var value = 0
        Accept(() -> unit => {
            value = 3
        })
        return value
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Runner", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        Assert.Equal(3, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void NestedLambda_CapturesOuterMethodLocal()
    {
        var code = """
class Runner {
    func Run() -> int {
        var value = 1
        let outer = () -> int => {
            let inner = () -> int => value
            value = 2
            return inner()
        }

        return outer()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Runner", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        Assert.Equal(2, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    // Local functions and lambdas share captured state.

    [Fact]
    public void LocalFunction_AndLambda_ShareCapturedState()
    {
        // A local function and a lambda in the same method both capture the same local.
        var code = """
class Counter {
    func Run() -> int {
        var count = 0

        func Increment() {
            count = count + 1
        }

        let read = () -> int => count

        Increment()
        Increment()
        Increment()
        return read()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Counter", throwOnError: true)!;


        // Correctness: local function and lambda share the same closure, so
        // writes from the local function are visible through the lambda.
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Equal(3, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void LocalFunction_MutatesCapturedLocal()
    {
        var code = """
class Counter {
    func Run() -> int {
        var x = 10

        func Double() {
            x = x * 2
        }

        Double()
        Double()
        return x
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Counter", throwOnError: true)!;


        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Equal(40, (int)method.Invoke(instance, Array.Empty<object>())!);
    }

    [Fact]
    public void FuncLambda_WithBodyLocalName_EmitsSelfInvocation()
    {
        var code = """
func Main() -> int {
    let f = func Fib(n: int) -> int {
        if n < 2 {
            return n
        }

        return Fib(n - 1) + Fib(n - 2)
    }

    return f(10)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.ConsoleApplication))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var main = loaded.Assembly.EntryPoint!;
        var arguments = main.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
        var value = (int)main.Invoke(null, arguments)!;
        Assert.Equal(55, value);
    }

    [Fact]
    public void IteratorLambda_BlockBody_EnumeratesExpectedValues()
    {
        var code = """
import System.*
import System.Collections.Generic.*

class Counter {
    func Sum() -> int {
        let values: Func<IEnumerable<int>> = () -> IEnumerable<int> => {
            yield 1
            yield 2
            yield 3
        }

        var sum = 0
        for let value in values() {
            sum = sum + value
        }

        return sum
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Counter", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Sum", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var value = (int)method.Invoke(instance, Array.Empty<object>())!;
        Assert.Equal(6, value);
    }

    [Fact]
    public async Task AsyncIteratorLambda_BlockBody_EnumeratesExpectedValues()
    {
        var code = """
import System.*
import System.Collections.Generic.*

class Counter {
    func MakeValues() -> Func<IAsyncEnumerable<int>> {
        let values: Func<IAsyncEnumerable<int>> = async () => {
            yield 1
            yield 2
            yield 3
        }

        return values
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Counter", throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("MakeValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        var factory = (Delegate)method.Invoke(instance, Array.Empty<object>())!;
        var values = (IAsyncEnumerable<int>)factory.DynamicInvoke()!;
        var actual = new List<int>();
        await foreach (var value in values)
            actual.Add(value);

        Assert.Equal(new[] { 1, 2, 3 }, actual);
    }

}
