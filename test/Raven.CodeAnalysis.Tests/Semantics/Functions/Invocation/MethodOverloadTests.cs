using System.Collections.Generic;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class MethodOverloadTests : CompilationTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericDelegateOverloads_PreferNestedParameterShape_RegardlessOfDeclarationOrder(bool reverse)
    {
        const string general = "static func Choose<T>(handler: Func<T>) -> int => 1";
        const string nested = "static func Choose<T>(handler: Func<Task<T>?>) -> int => 2";
        var source = $$"""
import System.*
import System.Threading.Tasks.*

class Runner {
    {{(reverse ? nested : general)}}
    {{(reverse ? general : nested)}}
}
let handler = () => Task.FromResult(42)
let result = Runner.Choose(handler)
""";
        var (compilation, tree) = CreateCompilation(source);
        Assert.Empty(compilation.GetDiagnostics());
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(node => node.Expression.ToString() == "Runner.Choose");
        var method = Assert.IsAssignableFrom<IMethodSymbol>(compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(method.TypeArguments).SpecialType);
    }

    [Fact]
    public void CollectionLiteralArgument_UsesArrayTargetWhenOverloadsDisagree()
    {
        const string source = """
        import System.*

        let t = typeof(string)
        let value: object? = "x"
        let created = Activator.CreateInstance(t, [value])
        """;

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "CreateInstance");

        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));

        Assert.Equal("CreateInstance", boundInvocation.Method.Name);
        Assert.IsAssignableFrom<IArrayTypeSymbol>(boundInvocation.Method.Parameters[1].Type.GetNonNullableType());
        var argument = Assert.IsType<BoundCollectionExpression>(boundInvocation.Arguments.ElementAt(1));
        Assert.IsAssignableFrom<IArrayTypeSymbol>(argument.Type.GetNonNullableType());
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void CollectionLiteralArgument_InfersGenericEnumerableOverloadFromElements()
    {
        const string source = """
        import System.Threading.Tasks.*

        let combined = Task.WhenAll([
            Task.FromResult(1)
            Task.FromResult(2)
        ])
        """;

        var (compilation, tree) = CreateCompilation(source);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "WhenAll");

        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));

        Assert.True(boundInvocation.Method.IsGenericMethod);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(boundInvocation.Method.TypeArguments).SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void ParameterDefaultValue_AllowsTargetTypedExternalEnumMember()
    {
        const string source = """
        import System.*

        func Paint(color: ConsoleColor = .Green) -> ConsoleColor {
            return color
        }

        let result = Paint()
        """;

        var (compilation, _) = CreateCompilation(source);

        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void FunctionExpression_ItIsNotImplicitParameterAlias()
    {
        const string source = """
        let transform: int -> int = x => it + 1
        """;

        var (compilation, _) = CreateCompilation(source);
        var diagnostic = Assert.Single(compilation.GetDiagnostics());

        Assert.Equal(CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext, diagnostic.Descriptor);
        Assert.Equal("'it' is not in scope.", diagnostic.GetMessage());
    }

    [Fact]
    public void Overloads_DifferOnlyByNullableReferenceType_AreRejected()
    {
        var source = """
        class C {
            func f(x: string) -> int { 0 }
            func f(x: string?) -> int { 1 }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        _ = model.GetDeclaredSymbol(methods[0]);
        _ = model.GetDeclaredSymbol(methods[1]);

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeAlreadyDefinesMember);
        Assert.All(diagnostics, diagnostic => Assert.Equal(CompilerDiagnostics.TypeAlreadyDefinesMember, diagnostic.Descriptor));
    }

    [Fact]
    public void Overloads_WithNullableValueType_AreAllowed()
    {
        var source = """
        class C {
            func f(x: int) -> int { 0 }
            func f(x: int?) -> int { 1 }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        _ = model.GetDeclaredSymbol(methods[0]);
        _ = model.GetDeclaredSymbol(methods[1]);

        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void UnionArgument_UsesCommonDenominatorForOverloadResolution()
    {
        var source = """
        open class Base {}
        class D1 : Base {}
        class D2 : Base {}
        class C {
            static func m(x: Base) -> int { 0 }
            static func m(x: object) -> int { 1 }
            func test(flag: bool) -> int {
                let u = if flag { D1() } else { D2() }
                return m(u);
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "m" });
        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;

        Assert.Equal("Base", symbol.Parameters[0].Type.Name);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void IntArgument_PrefersLongOverDoubleOverload()
    {
        const string source = """
            func Select(value: long) -> string { "long" }
            func Select(value: double) -> string { "double" }

            func Test() -> string {
                Select(1)
            }
            """;

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var (compilation, tree) = CreateCompilation(source, options: options);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var methodGroup = Assert.IsType<BoundMethodGroupExpression>(model.GetBoundNode(invocation.Expression));
        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);

        Assert.Equal(2, methodGroup.Methods.Length);
        Assert.Equal(SpecialType.System_Int64, Assert.Single(boundInvocation.Method.Parameters).Type.SpecialType);
        Assert.Equal(SpecialType.System_Int64, Assert.Single(method.Parameters).Type.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IntArgument_PrefersNearestTargetAcrossNumericConversionChain(
        bool reverseDeclarations,
        bool diagnosticsFirst)
    {
        var overloads = reverseDeclarations
            ? """
                func Select(value: double) -> string { "double" }
                func Select(value: float) -> string { "float" }
                func Select(value: long) -> string { "long" }
                """
            : """
                func Select(value: long) -> string { "long" }
                func Select(value: float) -> string { "float" }
                func Select(value: double) -> string { "double" }
                """;
        var source = $$"""
            {{overloads}}

            func Test() -> string {
                Select(1)
            }
            """;
        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(SpecialType.System_Int64, Assert.Single(method.Parameters).Type.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableIntArgument_PrefersNullableLongOverNullableDouble(
        bool reverseDeclarations,
        bool diagnosticsFirst)
    {
        var overloads = reverseDeclarations
            ? """
                func Select(value: double?) -> string { "double" }
                func Select(value: long?) -> string { "long" }
                """
            : """
                func Select(value: long?) -> string { "long" }
                func Select(value: double?) -> string { "double" }
                """;
        var source = $$"""
            {{overloads}}

            func Test(value: int?) -> string {
                Select(value)
            }
            """;
        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);
        var parameterType = Assert.IsType<NullableTypeSymbol>(Assert.Single(method.Parameters).Type);

        Assert.Equal(SpecialType.System_Int64, parameterType.UnderlyingType.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactNonGenericOverload_WinsEquivalentGenericCandidate(bool reverseDeclarations)
    {
        var overloads = reverseDeclarations
            ? """
                func Select<T>(value: T) -> string { "generic" }
                func Select(value: int) -> string { "int" }
                """
            : """
                func Select(value: int) -> string { "int" }
                func Select<T>(value: T) -> string { "generic" }
                """;
        var source = $$"""
            {{overloads}}

            func Test() -> string {
                Select(1)
            }
            """;

        var (compilation, tree) = CreateCompilation(
            source,
            options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.False(method.IsGenericMethod);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(method.Parameters).Type.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullArgument_PrefersMoreSpecificReferenceOverload(bool reverseDeclarations)
    {
        var overloads = reverseDeclarations
            ? """
                func Select(value: object?) -> string { "object" }
                func Select(value: string?) -> string { "string" }
                """
            : """
                func Select(value: string?) -> string { "string" }
                func Select(value: object?) -> string { "object" }
                """;
        var source = $$"""
            {{overloads}}

            func Test() -> string {
                Select(null)
            }
            """;

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var (compilation, tree) = CreateCompilation(source, options: options);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var methodGroup = Assert.IsType<BoundMethodGroupExpression>(model.GetBoundNode(invocation.Expression));
        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);

        Assert.Equal(2, methodGroup.Methods.Length);
        Assert.Equal(SpecialType.System_String, Assert.Single(boundInvocation.Method.Parameters).Type.GetNonNullableType().SpecialType);
        Assert.Equal(SpecialType.System_String, Assert.Single(method.Parameters).Type.GetNonNullableType().SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullArgument_IsAmbiguousForUnrelatedReferenceOverloads(bool reverseDeclarations)
    {
        var overloads = reverseDeclarations
            ? """
                func Select(value: Second?) -> string { "second" }
                func Select(value: First?) -> string { "first" }
                """
            : """
                func Select(value: First?) -> string { "first" }
                func Select(value: Second?) -> string { "second" }
                """;
        var source = $$"""
            class First {}
            class Second {}

            {{overloads}}

            func Test() -> string {
                Select(null)
            }
            """;

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var (compilation, tree) = CreateCompilation(source, options: options);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var symbolInfo = model.GetSymbolInfo(invocation);
        var diagnostic = Assert.Single(compilation.GetDiagnostics());

        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.Ambiguous, symbolInfo.CandidateReason);
        Assert.Equal(2, symbolInfo.CandidateSymbols.Length);
        Assert.Equal(CompilerDiagnostics.CallIsAmbiguous, diagnostic.Descriptor);
    }

    [Fact]
    public void InapplicableOverloads_PublishAllCandidates()
    {
        const string source = """
            func Select(value: int) -> string { "int" }
            func Select(value: string) -> string { "string" }

            func Test() -> string {
                Select(true)
            }
            """;

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var (compilation, tree) = CreateCompilation(source, options: options);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var symbolInfo = model.GetSymbolInfo(invocation);

        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
        Assert.Equal(2, symbolInfo.CandidateSymbols.Length);
        Assert.Contains(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConstrainedGenericOverload_OnlyParticipatesWhenConstraintIsSatisfied(
        bool reverseDeclarations,
        bool diagnosticsFirst)
    {
        var overloads = reverseDeclarations
            ? """
                func Select(value: object) -> string { "object" }
                func Select<T: struct>(value: T) -> string { "struct" }
                """
            : """
                func Select<T: struct>(value: T) -> string { "struct" }
                func Select(value: object) -> string { "object" }
                """;
        var source = $$"""
            {{overloads}}

            func TestText() -> string { Select("text") }
            func TestNumber() -> string { Select(1) }
            """;
        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var invocations = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var textMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocations[0]).Symbol);
        var numberMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocations[1]).Symbol);

        Assert.False(textMethod.IsGenericMethod);
        Assert.Equal(SpecialType.System_Object, Assert.Single(textMethod.Parameters).Type.SpecialType);
        Assert.True(numberMethod.IsGenericMethod);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(numberMethod.TypeArguments).SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousInvocationSymbolInfo_IsIndependentOfDiagnosticQueryOrder(bool diagnosticsFirst)
    {
        const string source = """
            class First {}
            class Second {}

            func Select(value: First?) -> string { "first" }
            func Select(value: Second?) -> string { "second" }

            func Test() -> string {
                Select(null)
            }
            """;

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var (compilation, tree) = CreateCompilation(source, options: options);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        if (diagnosticsFirst)
            _ = compilation.GetDiagnostics();

        var symbolInfo = model.GetSymbolInfo(invocation);
        var diagnostics = compilation.GetDiagnostics();

        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.Ambiguous, symbolInfo.CandidateReason);
        Assert.Equal(2, symbolInfo.CandidateSymbols.Length);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CallIsAmbiguous);
    }

    [Fact]
    public void LambdaArgument_CanBindToSystemDelegateParameter()
    {
        var source = """
        import System.*
        class C {
            static func takes(handler: Delegate) -> int { 1 }
            func run() -> int {
                return takes(() => 42)
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal(SpecialType.System_Delegate, symbol.Parameters[0].Type.SpecialType);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1501");
    }

    [Fact]
    public void ConcreteReceiver_DoesNotResolveImplementedInterfaceMethodOverConcreteMethod()
    {
        var source = """
        import System.Collections.Immutable.*

        record Person(val Name: string)

        func Test() -> ImmutableList<Person> {
            let people = [Person("Alice")]
            return people.Add(Person("Test"))
        }
        """;

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Add"
            });

        var type = Assert.IsAssignableFrom<INamedTypeSymbol>(model.GetTypeInfo(invocation).Type);
        Assert.Equal("ImmutableList", type.Name);
        var typeArgument = Assert.Single(type.TypeArguments);
        Assert.Equal("Person", typeArgument.Name);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1014");
    }

    [Fact]
    public void ConcreteReceiver_CollectionLiteralArgument_IsRejectedForScalarAddParameter()
    {
        var source = """
        import System.Collections.Immutable.*

        record Person(val Name: string)

        func Test() -> ImmutableList<Person> {
            let people = [Person("Alice")]
            return people.Add([Person("Test")])
        }
        """;

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Add"
            });

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CannotConvertFromTypeToType);
        Assert.Contains(diagnostics, diagnostic => diagnostic.ToString().Contains("Cannot convert from 'ImmutableList<Person>' to 'Person'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.ToString().Contains("Cannot convert from 'collection expression' to 'Person'", StringComparison.Ordinal));
    }

    [Fact]
    public void InterfaceTypedReceiver_CanResolveExplicitInterfaceMember()
    {
        var source = """
        import System.Collections.*
        import System.Collections.Immutable.*

        record Person(val Name: string)

        func Test() -> int {
            let people = [Person("Alice")]
            let values: IList = people
            return values.Add(Person("Test"))
        }
        """;

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Add"
            });

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        Assert.Equal("IList", symbol.ContainingType?.Name);
        Assert.Equal("object?", symbol.Parameters[0].Type.Name);
        Assert.Equal(SpecialType.System_Int32, symbol.ReturnType.SpecialType);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1014");
    }

    [Fact]
    public void ConcreteReceiver_DoesNotSeeSourceDefinedExplicitInterfaceMember()
    {
        var source = """
        interface ILogger {
            func Log(message: string) -> string
        }

        class QuietLogger : ILogger {
            func ILogger.Log(message: string) -> string {
                return "[quiet]"
            }
        }

        func Test() -> string {
            let logger = QuietLogger()
            return logger.Log("hi")
        }
        """;

        var (compilation, tree) = CreateCompilation(source, options: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(static i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Log"
            });

        var info = model.GetSymbolInfo(invocation);
        Assert.Null(info.Symbol);
        Assert.Contains(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV0117");
    }

    [Fact]
    public void OverloadResolutionPriority_PrefersHigherPrioritySourceMethod()
    {
        var source = """
        import System.Runtime.CompilerServices.*

        class C {
            [OverloadResolutionPriority(1)]
            static func pick(value: object) -> int { 1 }

            static func pick(value: string) -> int { 2 }

            func run() -> int {
                return pick("ok")
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        Assert.Equal(SpecialType.System_Object, symbol.Parameters[0].Type.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void OverloadResolutionPriority_PrefersHigherPriorityMetadataMethod()
    {
        var metadataReference = TestMetadataFactory.CreateFileReferenceFromSource(
            """
            import System.Runtime.CompilerServices.*

            class Library {
                [OverloadResolutionPriority(1)]
                public static func Pick(value: object) -> int { 1 }

                public static func Pick(value: string) -> int { 2 }
            }
            """,
            "OverloadResolutionPriorityFixture");

        var source = """
        class C {
            func run() -> int {
                return Library.Pick("ok")
            }
        }
        """;

        var (compilation, tree) = CreateCompilation(source, references: [.. TestMetadataReferences.Default, metadataReference]);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));
        Assert.Equal(SpecialType.System_Object, boundInvocation.Method.Parameters[0].Type.SpecialType);
    }

    [Fact]
    public void LambdaArgument_PrefersTypedFuncOverSystemDelegateOverload()
    {
        var source = """
        import System.*
        class C {
            static func pick(handler: Delegate) -> int { 1 }
            static func pick(handler: Func<string>) -> int { 2 }

            func run() -> int {
                return pick(() => "ok")
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal("Func", symbol.Parameters[0].Type.Name);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void LambdaArgument_CanBindToSystemMulticastDelegateParameter()
    {
        var source = """
        import System.*
        class C {
            static func takes(handler: MulticastDelegate) -> int { 1 }
            func run() -> int {
                return takes(() => 42)
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal(SpecialType.System_MulticastDelegate, symbol.Parameters[0].Type.SpecialType);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1501");
    }

    [Fact]
    public void LambdaArgument_WithNamedLambdaArgument_InfersFromCompetingDelegateCandidates()
    {
        var source = """
        import System.*
        import System.Collections.Generic.*
        import System.Linq.Expressions.*

        class C {
            static func Pick(source: IEnumerable<int>, selector: Func<int, int>) -> int { 1 }
            static func Pick(source: IEnumerable<int>, selector: Expression<Func<int, string>>) -> int { 2 }

            func run() -> int {
                let values: IEnumerable<int> = [1, 2, 3]
                return Pick(values, selector: x => x + 1)
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);

        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "Pick" });

        var boundInvocation = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(invocation));
        Assert.Equal("Func", boundInvocation.Method.Parameters[1].Type.Name);

        var lambda = tree.GetRoot()
            .DescendantNodes()
            .OfType<SimpleFunctionExpressionSyntax>()
            .Single();
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambda));
        var parameter = Assert.Single(boundLambda.Parameters);
        Assert.Equal(SpecialType.System_Int32, parameter.Type.SpecialType);

        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void PipelineSelect_OnOrderedQueryable_PrefersQueryableOverEnumerable()
    {
        var source = """
        import System.*
        import System.Linq.*
        import System.Collections.Generic.*
        import System.Linq.Expressions.*

        class C {
            func Project(source: IOrderedQueryable<int>) -> IQueryable<string> {
                return source |> Select(x => x.ToString())
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void LambdaArgument_OverloadsWithOptionalTail_DoNotPolluteInference()
    {
        var source = """
        import System.*

        class C {
            static func Transform(projector: Func<int, int>) -> int { 1 }
            static func Transform(projector: Func<string, string>, fallback: string = "") -> int { 2 }

            func run() -> int {
                return Transform(x => x + 1)
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);

        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "Transform" });

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal("Transform", symbol.Name);
        var projectorType = Assert.IsAssignableFrom<INamedTypeSymbol>(symbol.Parameters[0].Type);
        Assert.Equal(SpecialType.System_Int32, projectorType.TypeArguments[0].SpecialType);

        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => ReferenceEquals(diagnostic.Descriptor, CompilerDiagnostics.LambdaParameterTypeCannotBeInferred));
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void LambdaArgument_WithRequestDelegateAndSystemDelegateOverloads_PrefersSystemDelegateForSyncLambda()
    {
        var source = """
        import System.*
        import System.Threading.Tasks.*

        namespace Microsoft.AspNetCore.Http {
            public class HttpContext { }
        }

        class C {
            static func map(handler: Func<Microsoft.AspNetCore.Http.HttpContext, Task>) -> int { 1 }
            static func map(handler: Delegate) -> int { 2 }

            func run() -> int {
                return map((name: string) => "ok")
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "map" });

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal(SpecialType.System_Delegate, symbol.Parameters[0].Type.SpecialType);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1503");
    }

    [Fact]
    public void LambdaArgument_WithRequestDelegateLikeOverload_PrefersExplicitlyTypedLambdaMatch()
    {
        var source = """
        import System.*
        import System.Threading.Tasks.*

        namespace Microsoft.AspNetCore.Http {
            public class HttpContext { }
        }

        class C {
            static func map(handler: Func<Microsoft.AspNetCore.Http.HttpContext, Task>) -> int { 1 }
            static func map(handler: Func<string, string>) -> int { 2 }

            func run() -> int {
                return map((name: string) => "ok")
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "map" });

        var symbol = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.Equal("Func", symbol.Parameters[0].Type.Name);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "RAV1501");
    }

    [Fact]
    public void LambdaArgument_WithRequestDelegateLikeOverload_ReportsLambdaBodyDiagnostics()
    {
        var source = """
        import System.*
        import System.Threading.Tasks.*

        namespace Microsoft.AspNetCore.Http {
            public class HttpContext { }
        }

        class C {
            static func map(handler: Func<Microsoft.AspNetCore.Http.HttpContext, Task>) -> int { 1 }
            static func map(handler: Func<string, string>) -> int { 2 }

            func run() -> int {
                return map((name: string) => missingValue)
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext &&
            diagnostic.GetMessage().Contains("missingValue"));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "RAV1501");
    }

    [Fact]
    public void LambdaArgument_WithRequestDelegateLikeOverload_WhenNoSignatureMatches_ReportsConversionDiagnostics()
    {
        var source = """
        import System.*
        import System.Threading.Tasks.*

        namespace Microsoft.AspNetCore.Http {
            public class HttpContext { }
        }

        class C {
            static func map(handler: Func<Microsoft.AspNetCore.Http.HttpContext, Task>) -> int { 1 }
            static func map(handler: Func<string, string>) -> int { 2 }

            func run() -> int {
                return map(() => "ok")
            }
        }
        """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CannotConvertFromTypeToType &&
                          diagnostic.GetMessage().Contains("() -> Task") &&
                          diagnostic.GetMessage().Contains("HttpContext -> Task"));
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CannotConvertFromTypeToType &&
                          diagnostic.GetMessage().Contains("'string'") &&
                          diagnostic.GetMessage().Contains("'Task'"));
        Assert.DoesNotContain(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }
}
