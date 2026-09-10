using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Tests;
using Raven.CodeAnalysis.Text;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests;

public sealed class GenericMethodTests : CompilationTestBase
{
    [Fact]
    public void GenericMethod_ExposesTypeParametersAndArguments()
    {
        var source = """
            class Container
            {
                static func identity<T>(value: T) -> T
                {
                    return value;
                }
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var methodDeclaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var methodSymbol = (IMethodSymbol)model.GetDeclaredSymbol(methodDeclaration)!;

        Assert.True(methodSymbol.IsGenericMethod);
        Assert.Single(methodSymbol.TypeParameters);
        Assert.Equal("T", methodSymbol.TypeParameters[0].Name);
        Assert.Equal(TypeParameterOwnerKind.Method, methodSymbol.TypeParameters[0].OwnerKind);
        Assert.Same(methodSymbol, methodSymbol.TypeParameters[0].DeclaringMethodParameterOwner);
        Assert.Null(methodSymbol.TypeParameters[0].DeclaringTypeParameterOwner);
        Assert.Same(methodSymbol.TypeParameters[0], methodSymbol.TypeArguments[0]);
        Assert.Same(methodSymbol.TypeParameters[0], methodSymbol.ReturnType);
        Assert.Same(methodSymbol.TypeParameters[0], methodSymbol.Parameters[0].Type);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void GenericMethodInvocation_WithExplicitTypeArguments_BindsConstructedMethod()
    {
        var source = """
            class Container
            {
                static func identity<T>(value: T) -> T
                {
                    return value;
                }

                static func call() -> int
                {
                    return identity<int>(1);
                }
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);

        Assert.True(method.IsGenericMethod);
        Assert.Equal("identity", method.Name);
        Assert.Single(method.TypeArguments);
        Assert.Same(intType, method.TypeArguments[0]);
        Assert.Same(intType, method.ReturnType);
        Assert.Same(intType, method.Parameters[0].Type);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void GenericMethodInvocation_NullArgument_DoesNotInferTypeArgument()
    {
        var source = """
            class C
            {
                static func f<T>(value: T) -> ()
                {
                }

                static func test() -> ()
                {
                    f(null);
                }
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);
    }

    [Theory]
    [InlineData("derived, baseValue")]
    [InlineData("baseValue, derived")]
    public void GenericMethodInvocation_MixedBaseAndDerivedArguments_InfersCommonBaseType(string arguments)
    {
        var source = $$"""
            open class Base {}
            class Derived : Base {}

            func Choose<T>(first: T, second: T) -> T {
                first
            }

            func Test(derived: Derived, baseValue: Base) -> Base {
                Choose({{arguments}})
            }
            """;

        var (compilation, tree) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(diagnostics.IsEmpty, string.Join(System.Environment.NewLine, diagnostics));

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal("Base", Assert.Single(method.TypeArguments).Name);
    }

    [Theory]
    [InlineData("derived, baseValue")]
    [InlineData("baseValue, derived")]
    public void GenericMethodInvocation_PartialExplicitArguments_PreserveFixedTypeAndInferCommonBaseType(string arguments)
    {
        var source = $$"""
            open class Base {}
            class Derived : Base {}

            func Project<TInput, TResult>(first: TInput, second: TInput, result: TResult) -> TInput {
                first
            }

            func Test(derived: Derived, baseValue: Base) -> Base {
                Project<string>({{arguments}}, "result")
            }
            """;

        var (compilation, tree) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(diagnostics.IsEmpty, string.Join(System.Environment.NewLine, diagnostics));

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(["Base", "String"], method.TypeArguments.Select(static type => type.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericMethodInvocation_InfersMultipleArgumentsFromConstructedMetadataType(bool diagnosticsFirst)
    {
        const string source = """
            import System.Collections.Generic.*

            func GetValue<TKey, TValue>(values: Dictionary<TKey, TValue>) -> TValue {
                values.Values.GetEnumerator().Current
            }

            func Test(values: Dictionary<string, int>) -> int {
                GetValue(values)
            }
            """;
        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "GetValue");
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(2, method.TypeArguments.Length);
        Assert.Equal(SpecialType.System_String, method.TypeArguments[0].SpecialType);
        Assert.Equal(SpecialType.System_Int32, method.TypeArguments[1].SpecialType);
        Assert.Equal(SpecialType.System_Int32, method.ReturnType.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void GenericMethodInvocation_GenericMethodGroup_InfersBothMethods()
    {
        const string source = """
            import System.*

            let result = Apply(21, Identity)

            func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
                transform(value)
            }

            func Identity<T>(value: T) -> T {
                value
            }
            """;

        var (compilation, tree) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        Assert.True(diagnostics.IsEmpty, string.Join(System.Environment.NewLine, diagnostics));

        var model = compilation.GetSemanticModel(tree);
        var invocations = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var applyInvocation = Assert.Single(invocations, invocation => invocation.Expression.ToString() == "Apply");
        var applyMethod = Assert.IsType<BoundInvocationExpression>(model.GetBoundNode(applyInvocation)).Method;

        Assert.Equal(new[] { SpecialType.System_Int32, SpecialType.System_Int32 },
            applyMethod.TypeArguments.Select(static type => type.SpecialType));

        var applySymbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(applyInvocation).Symbol);
        Assert.Equal(new[] { SpecialType.System_Int32, SpecialType.System_Int32 },
            applySymbol.TypeArguments.Select(static type => type.SpecialType));

        var identityReference = tree.GetRoot().DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Identity");
        var identityMethod = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(identityReference).Symbol);

        Assert.Equal(SpecialType.System_Int32, Assert.Single(identityMethod.TypeArguments).SpecialType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HigherOrderGenericInference_MatchesSourceAndMetadataMethodGroups(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
public class Transforms {
    static func Identity<T>(value: T) -> T { value }
}
""";
        const string consumerSource = """
import System.*

let result = Apply(21, Transforms.Identity)

func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
    transform(value)
}
""";
        var consumerTree = SyntaxTree.ParseText(consumerSource);
        MetadataReference[] references = useMetadata
            ? [.. TestMetadataReferences.Default,
                TestMetadataFactory.CreateFromSource(librarySource, "higher_order_generic_library")]
            : TestMetadataReferences.Default;
        var compilation = Compilation.Create(
            "higher_order_generic_consumer",
            useMetadata ? [consumerTree] : [SyntaxTree.ParseText(librarySource), consumerTree],
            references,
            new CompilationOptions(OutputKind.ConsoleApplication));
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(consumerTree);
        var applyInvocation = consumerTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "Apply");
        var identityReference = consumerTree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Identity");
        var apply = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(applyInvocation).Symbol);
        var identity = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(identityReference).Symbol);

        Assert.Equal(
            [SpecialType.System_Int32, SpecialType.System_Int32],
            apply.TypeArguments.Select(static type => type.SpecialType));
        Assert.Equal(SpecialType.System_Int32, Assert.Single(identity.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_Int32, identity.ReturnType.SpecialType);

        if (!diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericMethodGroup_WithSatisfiedConstraint_IsQueryOrderIndependent(bool diagnosticsFirst)
    {
        const string source = """
            import System.*

            let result = Apply(21, Stringify)

            func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
                transform(value)
            }

            func Stringify<T: struct>(value: T) -> string {
                ""
            }
            """;

        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var applyInvocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "Apply");
        var stringifyReference = tree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Stringify");

        var apply = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(applyInvocation).Symbol);
        var stringify = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(stringifyReference).Symbol);

        Assert.Equal(
            [SpecialType.System_Int32, SpecialType.System_String],
            apply.TypeArguments.Select(static type => type.SpecialType));
        Assert.Equal(SpecialType.System_Int32, Assert.Single(stringify.TypeArguments).SpecialType);
        Assert.True((Assert.Single(stringify.TypeParameters).ConstraintKind & TypeParameterConstraintKind.ValueType) != 0);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OverloadedGenericMethodGroup_UsesHigherOrderTargetSignature(
        bool reverseDeclarations,
        bool diagnosticsFirst)
    {
        var transforms = reverseDeclarations
            ? """
                func Convert(value: string) -> string { value }
                func Convert<T>(value: T) -> string { "" }
                """
            : """
                func Convert<T>(value: T) -> string { "" }
                func Convert(value: string) -> string { value }
                """;
        var source = $$"""
            import System.*

            let result = Apply(21, Convert)

            func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
                transform(value)
            }

            {{transforms}}
            """;

        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var applyInvocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "Apply");
        var convertReference = tree.GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Single(identifier => identifier.Identifier.ValueText == "Convert");
        var apply = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(applyInvocation).Symbol);
        var convert = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(convertReference).Symbol);

        Assert.Equal(
            [SpecialType.System_Int32, SpecialType.System_String],
            apply.TypeArguments.Select(static type => type.SpecialType));
        Assert.True(convert.IsGenericMethod);
        Assert.Equal(SpecialType.System_Int32, Assert.Single(convert.TypeArguments).SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void GenericMethodGroup_WithUnsatisfiedConstraint_ReportsConstraintFailure()
    {
        const string source = """
            import System.*

            let result = Apply("value", Stringify)

            func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
                transform(value)
            }

            func Stringify<T: struct>(value: T) -> string {
                ""
            }
            """;

        var (compilation, _) = CreateCompilation(source);
        var diagnostics = compilation.GetDiagnostics();

        var diagnostic = Assert.Single(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.Contains("Stringify", diagnostic.GetMessage());
        Assert.Contains("struct", diagnostic.GetMessage());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);
    }

    [Fact]
    public void EditingMethodGroupArgument_RecomputesInferredConstraintsAndSymbolInfo()
    {
        const string validSource = """
            import System.*

            let result = Apply(21, Stringify)

            func Apply<TInput, TResult>(value: TInput, transform: Func<TInput, TResult>) -> TResult {
                transform(value)
            }

            func Stringify<T: struct>(value: T) -> string {
                ""
            }
            """;
        var invalidSource = validSource.Replace("Apply(21", "Apply(\"value\"", System.StringComparison.Ordinal);
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "generic-method-group-edit",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "method-group.rav",
            SourceText.From(validSource),
            "/tmp/generic-method-group-edit.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        AssertValidSnapshot();

        var documentId = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single().Id;
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(invalidSource)));
        AssertInvalidSnapshot();

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(validSource)));
        AssertValidSnapshot();

        void AssertValidSnapshot()
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = compilation.SyntaxTrees.Single();
            var model = compilation.GetSemanticModel(tree);
            var stringify = tree.GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Single(identifier => identifier.Identifier.ValueText == "Stringify");
            var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(stringify).Symbol);

            var typeArgument = Assert.Single(method.TypeArguments);
            Assert.True(
                typeArgument.SpecialType == SpecialType.System_Int32,
                $"Expected int, got {typeArgument.ToDisplayString()} ({typeArgument.GetType().Name}) from {method.ToDisplayString()}");
            Assert.Empty(compilation.GetDiagnostics());
        }

        void AssertInvalidSnapshot()
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = compilation.SyntaxTrees.Single();
            var model = compilation.GetSemanticModel(tree);
            var stringify = tree.GetRoot()
                .DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Single(identifier => identifier.Identifier.ValueText == "Stringify");
            var symbolInfo = model.GetSymbolInfo(stringify);
            var diagnostic = Assert.Single(
                compilation.GetDiagnostics(),
                diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);

            var method = Assert.IsAssignableFrom<IMethodSymbol>(symbolInfo.Symbol);
            Assert.IsAssignableFrom<ITypeParameterSymbol>(Assert.Single(method.TypeArguments));
            Assert.Contains("Stringify", diagnostic.GetMessage());
        }
    }

    [Fact]
    public void GenericMethodInvocation_ConstraintFailure_DoesNotReportNameMissing()
    {
        var source = """
            import System.*

            f2<bool?>(null)

            func f2<T>(t: T) -> ()
                where T: notnull {
                Console.WriteLine(t)
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.NoOverloadForMethod);
    }

    [Fact]
    public void GenericMethodInvocation_WhereClauseSelfConstraint_AllowsExplicitTypeArgument()
    {
        var source = """
            import System.*

            func Parse<T>(text: string) -> T
                where T: IParsable<T>
                => T.Parse(text, null)

            func Main() -> int
            {
                return Parse<int>("42");
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();

        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Constraints.Select<Base, Derived>(Base(), Derived())", false)]
    [InlineData("Constraints.Select<Base, Derived>(Base(), Derived())", true)]
    [InlineData("Constraints.Select(Base(), Derived())", false)]
    [InlineData("Constraints.Select(Base(), Derived())", true)]
    public void EmittedGenericMethod_DependentConstraint_IsSatisfied(string call, bool diagnosticsFirst)
    {
        var reference = CreateDependentConstraintLibrary();
        var (compilation, tree) = CreateCompilation(
            $"""
            import ConstraintLibrary.*

            let value = {call}
            """,
            references: [.. TestMetadataReferences.Default, reference]);

        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var diagnostics = compilation.GetDiagnostics();
        Assert.Empty(diagnostics);

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().StartsWith("Constraints.Select", System.StringComparison.Ordinal));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(["Base", "Derived"], method.TypeArguments.Select(static argument => argument.Name));
        var derivedParameter = Assert.Single(method.TypeParameters, static parameter => parameter.Name == "TDerived");
        var baseConstraint = Assert.IsAssignableFrom<ITypeParameterSymbol>(Assert.Single(derivedParameter.ConstraintTypes));
        Assert.Equal("TBase", baseConstraint.Name);
        Assert.Equal(0, baseConstraint.Ordinal);
    }

    [Theory]
    [InlineData("Constraints.Select<Derived, Base>(Derived(), Base())")]
    [InlineData("Constraints.Select(Derived(), Base())")]
    public void EmittedGenericMethod_DependentConstraint_IsRejected(string call)
    {
        var reference = CreateDependentConstraintLibrary();
        var (compilation, tree) = CreateCompilation(
            $"""
            import ConstraintLibrary.*

            let value = {call}
            """,
            references: [.. TestMetadataReferences.Default, reference]);

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CallIsAmbiguous);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().StartsWith("Constraints.Select", System.StringComparison.Ordinal));
        var symbolInfo = compilation.GetSemanticModel(tree).GetSymbolInfo(invocation);

        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
        Assert.Equal("Select", Assert.Single(symbolInfo.CandidateSymbols).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceGenericMethod_DependentConstraint_MatchesMetadataBehavior(bool diagnosticsFirst)
    {
        const string source = """
            class Base {}
            class Derived: Base {}

            func Select<TBase, TDerived>(baseValue: TBase, derivedValue: TDerived) -> TDerived
                where TDerived: TBase
            {
                derivedValue
            }

            let accepted = Select(Base(), Derived())
            let rejected = Select(Derived(), Base())
            """;

        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            _ = compilation.GetDiagnostics();

        var model = compilation.GetSemanticModel(tree);
        var invocations = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(static invocation => invocation.Expression.ToString() == "Select")
            .ToArray();
        var accepted = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocations[0]).Symbol);
        var rejected = model.GetSymbolInfo(invocations[1]);
        var diagnostics = compilation.GetDiagnostics();

        Assert.Equal(["Base", "Derived"], accepted.TypeArguments.Select(static argument => argument.Name));
        Assert.Null(rejected.Symbol);
        Assert.Contains(
            rejected.CandidateSymbols,
            static candidate => candidate is IMethodSymbol { Name: "Select" });
        Assert.Contains(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CallIsAmbiguous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartiallyExplicitGenericCall_InfersLeadingTypeFromNestedConstructedArgument(bool diagnosticsFirst)
    {
        const string source = """
            import System.Collections.Generic.*

            func Select<TKey, TValue>(values: Dictionary<TKey, TValue>, fallback: TValue) -> TValue {
                fallback
            }

            let values = Dictionary<int, string>()
            let selected = Select<string>(values, "fallback")
            """;

        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().StartsWith("Select", System.StringComparison.Ordinal));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);

        Assert.Empty(compilation.GetDiagnostics());
        Assert.Collection(
            method.TypeArguments,
            argument => Assert.Equal(SpecialType.System_Int32, argument.SpecialType),
            argument => Assert.Equal(SpecialType.System_String, argument.SpecialType));
        Assert.Equal(SpecialType.System_String, method.ReturnType.SpecialType);
    }

    [Theory]
    [InlineData("string", "\"fallback\"", SpecialType.System_String, false)]
    [InlineData("string", "\"fallback\"", SpecialType.System_String, true)]
    [InlineData("int", "0", SpecialType.System_Int32, false)]
    [InlineData("int", "0", SpecialType.System_Int32, true)]
    public void GenericInference_UnwrapsUnifiedNullableParameter(
        string typeName,
        string fallback,
        SpecialType expectedType,
        bool diagnosticsFirst)
    {
        var source = $$"""
            func Coalesce<T>(value: T?, fallback: T) -> T {
                if let present: T = value {
                    return present
                }

                return fallback
            }

            let value: {{typeName}}? = null
            let result = Coalesce(value, {{fallback}})
            """;
        var (compilation, tree) = CreateCompilation(source);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString() == "Coalesce");
        var model = compilation.GetSemanticModel(tree);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        var invocationType = model.GetTypeInfo(invocation).Type;

        Assert.Equal(expectedType, Assert.Single(method.TypeArguments).SpecialType);
        Assert.Equal(expectedType, method.ReturnType.SpecialType);
        var nullableParameter = Assert.IsType<NullableTypeSymbol>(method.Parameters[0].Type);
        Assert.Equal(expectedType, nullableParameter.UnderlyingType.SpecialType);
        Assert.Equal(expectedType, invocationType?.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, "string", "\"fallback\"", SpecialType.System_String, false)]
    [InlineData(false, "string", "\"fallback\"", SpecialType.System_String, true)]
    [InlineData(false, "int", "0", SpecialType.System_Int32, false)]
    [InlineData(false, "int", "0", SpecialType.System_Int32, true)]
    [InlineData(true, "string", "\"fallback\"", SpecialType.System_String, false)]
    [InlineData(true, "string", "\"fallback\"", SpecialType.System_String, true)]
    [InlineData(true, "int", "0", SpecialType.System_Int32, false)]
    [InlineData(true, "int", "0", SpecialType.System_Int32, true)]
    public void NullableParameterInference_MatchesSourceAndMetadata(
        bool useMetadata,
        string typeName,
        string fallback,
        SpecialType expectedType,
        bool diagnosticsFirst)
    {
        var libraryTree = SyntaxTree.ParseText("""
            namespace NullableInferenceLibrary {
                public func Coalesce<T>(value: T?, fallback: T) -> T {
                    if let present: T = value {
                        return present
                    }

                    return fallback
                }
            }
            """);
        var consumerTree = SyntaxTree.ParseText($$"""
            import NullableInferenceLibrary.*

            let value: {{typeName}}? = null
            let result = Coalesce(value, {{fallback}})
            """);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NullableInferenceLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(consumerTree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(expectedType, Assert.Single(method.TypeArguments).SpecialType);
        Assert.Equal(expectedType, method.ReturnType.SpecialType);
        var nullableParameter = Assert.IsType<NullableTypeSymbol>(method.Parameters[0].Type);
        Assert.Equal(expectedType, nullableParameter.UnderlyingType.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableParameterInference_RejectedConstraintRetainsProjectedCandidate(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        var libraryTree = SyntaxTree.ParseText("""
            namespace NullableConstraintLibrary {
                public func RequireValue<T>(value: T?) -> T
                    where T: struct
                {
                    if let present: T = value {
                        return present
                    }

                    throw System.Exception()
                }
            }
            """);
        var consumerTree = SyntaxTree.ParseText("""
            import NullableConstraintLibrary.*

            let value: string? = null
            let result = RequireValue(value)
            """);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NullableConstraintLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var model = compilation.GetSemanticModel(consumerTree);
        var diagnostics = diagnosticsFirst ? compilation.GetDiagnostics() : default;
        var symbolInfo = model.GetSymbolInfo(invocation);
        if (!diagnosticsFirst)
            diagnostics = compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
        var candidate = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(symbolInfo.CandidateSymbols));
        Assert.Equal(SpecialType.System_String, Assert.Single(candidate.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, candidate.ReturnType.SpecialType);
        var nullableParameter = Assert.IsType<NullableTypeSymbol>(Assert.Single(candidate.Parameters).Type);
        Assert.Equal(SpecialType.System_String, nullableParameter.UnderlyingType.SpecialType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableConstructedValueParameterInference_MatchesSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        var libraryTree = SyntaxTree.ParseText("""
            namespace NullableConstructedLibrary {
                public struct Box<T> { }

                public func Extract<T>(value: Box<T>?) -> T {
                    throw System.Exception()
                }
            }
            """);
        var consumerTree = SyntaxTree.ParseText("""
            import NullableConstructedLibrary.*

            let box: Box<string>? = null
            let result = Extract(box)
            """);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NullableConstructedLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(consumerTree).GetSymbolInfo(invocation).Symbol);

        Assert.Equal(SpecialType.System_String, Assert.Single(method.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, method.ReturnType.SpecialType);
        var nullableParameter = Assert.IsType<NullableTypeSymbol>(Assert.Single(method.Parameters).Type);
        var constructedBox = Assert.IsAssignableFrom<INamedTypeSymbol>(nullableParameter.UnderlyingType);
        Assert.Equal("Box", constructedBox.Name);
        Assert.Equal(SpecialType.System_String, Assert.Single(constructedBox.TypeArguments).SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedNullableArrayTupleInference_MatchesSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        var libraryTree = SyntaxTree.ParseText("""
            namespace NestedInferenceLibrary {
                public class Envelope<T> {}

                public func Extract<T>(value: Envelope<(T?[], System.Collections.Generic.List<T?>)>) -> T {
                    throw System.Exception()
                }

                public func Extract(value: object) -> object => value
            }
            """);
        var consumerTree = SyntaxTree.ParseText("""
            import System.Collections.Generic.*
            import NestedInferenceLibrary.*

            let value = Envelope<(string?[], List<string?>)>()
            let result = Extract(value)
            """);
        using var logWriter = new System.IO.StringWriter();
        using var overloadLog = new OverloadResolutionLog(logWriter, ownsWriter: false);
        var options = new CompilationOptions(OutputKind.ConsoleApplication)
            .WithOverloadResolutionLogger(overloadLog);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                options: options,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NestedInferenceLibrary")])
            : CreateCompilation([libraryTree, consumerTree], options: options);

        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString() == "Extract");
        var model = compilation.GetSemanticModel(consumerTree);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);

        Assert.True(method.IsGenericMethod);
        Assert.Equal(SpecialType.System_String, Assert.Single(method.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, method.ReturnType.SpecialType);
        var envelopeType = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(method.Parameters).Type);
        var tupleType = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(envelopeType.TypeArguments));
        var tupleElements = tupleType.TypeArguments;
        var nullableArray = Assert.IsAssignableFrom<IArrayTypeSymbol>(tupleElements[0]);
        Assert.Equal(SpecialType.System_String, nullableArray.ElementType.GetNonNullableType().SpecialType);
        Assert.True(nullableArray.ElementType.IsNullable);
        var listType = Assert.IsAssignableFrom<INamedTypeSymbol>(tupleElements[1]);
        var nullableListElement = Assert.Single(listType.TypeArguments);
        Assert.Equal(SpecialType.System_String, nullableListElement.GetNonNullableType().SpecialType);
        Assert.True(nullableListElement.IsNullable);
        Assert.Equal(SpecialType.System_String, model.GetTypeInfo(invocation).Type?.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Theory]
    [InlineData("ref", RefKind.Ref, false, false)]
    [InlineData("ref", RefKind.Ref, false, true)]
    [InlineData("ref", RefKind.Ref, true, false)]
    [InlineData("ref", RefKind.Ref, true, true)]
    [InlineData("out", RefKind.Out, false, false)]
    [InlineData("out", RefKind.Out, false, true)]
    [InlineData("out", RefKind.Out, true, false)]
    [InlineData("out", RefKind.Out, true, true)]
    [InlineData("in", RefKind.In, false, false)]
    [InlineData("in", RefKind.In, false, true)]
    [InlineData("in", RefKind.In, true, false)]
    [InlineData("in", RefKind.In, true, true)]
    public void ByRefParameterInference_MatchesSourceAndMetadata(
        string modifier,
        RefKind expectedRefKind,
        bool useMetadata,
        bool diagnosticsFirst)
    {
        var libraryTree = SyntaxTree.ParseText($$"""
            namespace ByRefInferenceLibrary {
                public func Read<T>({{modifier}} value: T) -> T {
                    throw System.Exception()
                }
            }
            """);
        var consumerTree = SyntaxTree.ParseText($$"""
            import ByRefInferenceLibrary.*

            var value = "raven"
            let result = Read({{modifier}} value)
            """);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "ByRefInferenceLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        if (!useMetadata)
        {
            var declaration = libraryTree.GetRoot().DescendantNodes().OfType<FunctionStatementSyntax>().Single();
            var openMethod = Assert.IsAssignableFrom<IMethodSymbol>(
                compilation.GetSemanticModel(libraryTree).GetDeclaredSymbol(declaration));
            var openParameter = Assert.Single(openMethod.Parameters);
            Assert.Equal(expectedRefKind, openParameter.RefKind);
            Assert.IsAssignableFrom<ITypeParameterSymbol>(openParameter.Type);
        }
        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(consumerTree).GetSymbolInfo(invocation).Symbol);
        var parameter = Assert.Single(method.Parameters);

        Assert.Equal(SpecialType.System_String, Assert.Single(method.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, method.ReturnType.SpecialType);
        Assert.Equal(expectedRefKind, parameter.RefKind);
        Assert.Equal(SpecialType.System_String, parameter.Type.SpecialType);
        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void MetadataGenericMethod_ConstraintUsingConstructedContainingType_IsSatisfied()
    {
        const string source = """
            import Raven.MetadataFixtures.Generics.*

            let value = GenericContainer<object>.Coerce<string>("value")
            """;

        var (compilation, tree) = CreateCompilation(
            source,
            references: TestMetadataReferences.DefaultWithExtensionMethods);

        Assert.Empty(compilation.GetDiagnostics());

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol);
        var constraint = Assert.Single(Assert.Single(method.TypeParameters).ConstraintTypes);

        Assert.Equal(SpecialType.System_Object, constraint.SpecialType);
    }

    [Fact]
    public void MetadataGenericMethod_ConstraintUsingConstructedContainingType_IsRejected()
    {
        const string source = """
            import Raven.MetadataFixtures.Generics.*

            let value = GenericContainer<string>.Coerce<object>("value")
            """;

        var (compilation, tree) = CreateCompilation(
            source,
            references: TestMetadataReferences.DefaultWithExtensionMethods);

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CallIsAmbiguous);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);

        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var symbolInfo = compilation.GetSemanticModel(tree).GetSymbolInfo(invocation);

        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
        var candidate = Assert.Single(symbolInfo.CandidateSymbols.OfType<IMethodSymbol>());
        Assert.Equal("Coerce", candidate.Name);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedConstructedGenericMethod_PreservesConstraintsAcrossSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
            namespace NestedConstraintLibrary {
                public class Outer<TOuter> {
                    class Inner<TInner> {
                        func Select<TValue>(value: TValue) -> TOuter
                            where TValue: System.Collections.Generic.IEnumerable<TInner>
                            => throw System.Exception()
                    }
                }
            }
            """;
        const string consumerSource = """
            import System.Collections.Generic.*
            import NestedConstraintLibrary.*

            let result = Outer<string>.Inner<int>().Select<List<int>>(List<int>())
            """;
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText(consumerSource);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree)])
            : CreateCompilation([libraryTree, consumerTree]);

        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().Contains(".Select", System.StringComparison.Ordinal));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(consumerTree).GetSymbolInfo(invocation).Symbol);

        Assert.Empty(compilation.GetDiagnostics());
        Assert.Equal(SpecialType.System_String, method.ReturnType.SpecialType);
        Assert.Equal("List<int>", Assert.Single(method.TypeArguments).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        var constraint = Assert.IsAssignableFrom<INamedTypeSymbol>(
            Assert.Single(Assert.Single(method.TypeParameters).ConstraintTypes));
        Assert.Equal("IEnumerable<int>", constraint.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        Assert.Equal(SpecialType.System_Int32, Assert.Single(method.ContainingType!.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, Assert.Single(method.ContainingType.ContainingType!.TypeArguments).SpecialType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedConstructedGenericMethod_RejectedConstraintRetainsProjectedCandidate(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
            namespace NestedConstraintCandidateLibrary {
                public class Outer<TOuter> {
                    class Inner<TInner> {
                        func Select<TValue>(value: TValue) -> TOuter
                            where TValue: System.Collections.Generic.IEnumerable<TInner>
                            => throw System.Exception()
                    }
                }
            }
            """;
        const string consumerSource = """
            import System.Collections.Generic.*
            import NestedConstraintCandidateLibrary.*

            let result = Outer<string>.Inner<int>().Select<List<string>>(List<string>())
            """;
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText(consumerSource);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NestedConstraintCandidateLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().Contains(".Select", System.StringComparison.Ordinal));
        var model = compilation.GetSemanticModel(consumerTree);
        var diagnostics = diagnosticsFirst ? compilation.GetDiagnostics() : default;
        var symbolInfo = model.GetSymbolInfo(invocation);
        if (!diagnosticsFirst)
            diagnostics = compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.Null(symbolInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
        var candidate = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(symbolInfo.CandidateSymbols));
        var constraint = Assert.IsAssignableFrom<INamedTypeSymbol>(
            Assert.Single(Assert.Single(candidate.TypeParameters).ConstraintTypes));
        Assert.Equal("IEnumerable<int>", constraint.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        Assert.Equal(SpecialType.System_Int32, Assert.Single(candidate.ContainingType!.TypeArguments).SpecialType);
        Assert.Equal(SpecialType.System_String, Assert.Single(candidate.ContainingType.ContainingType!.TypeArguments).SpecialType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedConstructedGenericMethod_SubstitutesDependentTypeAndMethodConstraints(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
            namespace NestedDependentConstraintLibrary {
                public interface IBox<T> {}
                public class StringBox : IBox<string> {}

                public class Outer<TOuter> {
                    class Inner<TInner : IBox<TOuter>> {
                        func Select<TValue>(value: TValue) -> TValue
                            where TValue: TInner
                            => value
                    }
                }
            }
            """;
        const string consumerSource = """
            import NestedDependentConstraintLibrary.*

            let result = Outer<string>.Inner<StringBox>().Select<StringBox>(StringBox())
            """;
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText(consumerSource);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NestedDependentConstraintLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);

        if (diagnosticsFirst)
            Assert.Empty(compilation.GetDiagnostics());

        var invocation = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(static invocation => invocation.Expression.ToString().Contains(".Select", System.StringComparison.Ordinal));
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(consumerTree).GetSymbolInfo(invocation).Symbol);

        Assert.Empty(compilation.GetDiagnostics());
        Assert.Equal("StringBox", Assert.Single(method.TypeArguments).Name);
        Assert.Equal("StringBox", Assert.Single(Assert.Single(method.TypeParameters).ConstraintTypes).Name);

        var innerType = method.ContainingType!;
        Assert.Equal("StringBox", Assert.Single(innerType.TypeArguments).Name);
        var innerTypeParameter = Assert.Single(innerType.TypeParameters);
        Assert.Equal(TypeParameterConstraintKind.TypeConstraint, innerTypeParameter.ConstraintKind);
        Assert.True(SymbolEqualityComparer.Default.Equals(
            innerType.OriginalDefinition,
            innerTypeParameter.DeclaringTypeParameterOwner));
        var innerConstraint = Assert.IsAssignableFrom<INamedTypeSymbol>(
            Assert.Single(innerTypeParameter.ConstraintTypes));
        Assert.Equal("IBox<string>", innerConstraint.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        Assert.Equal(SpecialType.System_String, Assert.Single(innerType.ContainingType!.TypeArguments).SpecialType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedConstructedGenericType_RejectsDependentOuterConstraint(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
            namespace NestedDependentConstraintFailureLibrary {
                public interface IBox<T> {}
                public class StringBox : IBox<string> {}

                public class Outer<TOuter> {
                    class Inner<TInner : IBox<TOuter>> {}
                }
            }
            """;
        const string consumerSource = """
            import NestedDependentConstraintFailureLibrary.*

            let result = Outer<int>.Inner<StringBox>()
            """;
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText(consumerSource);
        var compilation = useMetadata
            ? CreateCompilation(
                consumerTree,
                references: [.. TestMetadataReferences.Default, CreateLibraryReference(libraryTree, "NestedDependentConstraintFailureLibrary")])
            : CreateCompilation([libraryTree, consumerTree]);
        var construction = consumerTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var model = compilation.GetSemanticModel(consumerTree);

        if (!diagnosticsFirst)
            _ = model.GetSymbolInfo(construction);

        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext);
        Assert.DoesNotContain(
            diagnostics,
            static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.CallIsAmbiguous);

        if (diagnosticsFirst)
            _ = model.GetSymbolInfo(construction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingConstructedMetadataMethodConstraint_RecomputesDiagnosticsAndSymbolInfo(bool diagnosticsFirst)
    {
        const string validSource = """
            import Raven.MetadataFixtures.Generics.*

            let value = GenericContainer<string>.Coerce<string>("value")
            """;
        var invalidSource = validSource.Replace("Coerce<string>", "Coerce<object>", System.StringComparison.Ordinal);
        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "metadata-generic-constraint-edit",
            compilationOptions: new CompilationOptions(OutputKind.ConsoleApplication),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.DefaultWithExtensionMethods)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "constraint.rav",
            SourceText.From(validSource),
            "/tmp/metadata-generic-constraint-edit.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        AssertValidSnapshot();

        var documentId = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single().Id;
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(invalidSource)));
        AssertInvalidSnapshot();

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From(validSource)));
        AssertValidSnapshot();

        void AssertValidSnapshot()
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = compilation.SyntaxTrees.Single();
            var model = compilation.GetSemanticModel(tree);
            var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            if (diagnosticsFirst)
                Assert.Empty(compilation.GetDiagnostics());

            var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);

            Assert.Equal(SpecialType.System_String, Assert.Single(method.TypeArguments).SpecialType);
            Assert.Empty(compilation.GetDiagnostics());
        }

        void AssertInvalidSnapshot()
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = compilation.SyntaxTrees.Single();
            var model = compilation.GetSemanticModel(tree);
            var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var diagnostics = diagnosticsFirst ? compilation.GetDiagnostics() : default;
            var symbolInfo = model.GetSymbolInfo(invocation);
            if (!diagnosticsFirst)
                diagnostics = compilation.GetDiagnostics();

            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
            Assert.Null(symbolInfo.Symbol);
            Assert.Equal(CandidateReason.OverloadResolutionFailure, symbolInfo.CandidateReason);
            Assert.Single(symbolInfo.CandidateSymbols.OfType<IMethodSymbol>());
        }
    }

    [Fact]
    public void GenericConstraintFailure_DoesNotCascadeToAmbiguousOverload()
    {
        var source = """
            import System.*
            import System.Console.*

            func Main() -> ()
            {
                let r = Parse<bool?>("42")
                WriteLine(r)
            }

            func Parse<T>(text: string) -> T
                where T: notnull
                => throw InvalidOperationException()
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree);
        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
        Assert.DoesNotContain(diagnostics, d => d.Descriptor == CompilerDiagnostics.CallIsAmbiguous);
    }

    [Fact]
    public void ImplementedGenericInterface_ConstraintFailure_ReportsDiagnostic()
    {
        var source = """
            union class Response<T> {
                case Success(value: T)
                case Failure(message: string)
            }

            interface IRequestHandler<TRequest, TReturn>
                where TReturn : new() {
            }

            class SubmitOrderHandler : IRequestHandler<int, Response<decimal>> {
            }
            """;

        var tree = SyntaxTree.ParseText(source);
        var compilation = CreateCompilation(tree, new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();

        Assert.Contains(diagnostics, d => d.Descriptor == CompilerDiagnostics.TypeArgumentDoesNotSatisfyConstraint);
    }

    private static MetadataReference CreateDependentConstraintLibrary()
    {
        const string source = """
            namespace ConstraintLibrary {
                public open class Base {}
                public class Derived : Base {}

                public class Constraints {
                    static func Select<TBase, TDerived>(fallback: TBase, value: TDerived) -> TDerived
                        where TDerived: TBase
                        => value
                }
            }
            """;
        var tree = SyntaxTree.ParseText(source);
        var compilation = Compilation.Create(
            "ConstraintLibrary",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitResult = compilation.Emit(image);

        Assert.True(emitResult.Success, string.Join(System.Environment.NewLine, emitResult.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static MetadataReference CreateLibraryReference(SyntaxTree tree, string assemblyName = "NestedConstraintLibrary")
    {
        var compilation = Compilation.Create(
            assemblyName,
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitResult = compilation.Emit(image);

        Assert.True(emitResult.Success, string.Join(System.Environment.NewLine, emitResult.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    [Fact]
    public void InvocationThroughConstructedGenericInterface_AwaitsConstructedReturnType()
    {
        var source = """
import System.Threading.Tasks.*

let handler: IHandler<Request, Response<int>> = Handler()
let response = await handler.Handle(Request())

interface IHandler<TRequest, TResponse> {
    func Handle(request: TRequest) -> Task<TResponse>;
}

class Handler : IHandler<Request, Response<int>> {
    async func Handle(request: Request) -> Task<Response<int>> {
        await Task.CompletedTask
        return .Success(1)
    }
}

record class Request()

union Response<T> {
    case Success(value: T)
    case Failure(message: string)
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString() == "handler.Handle");

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        Assert.Equal("Handle", symbol.Name);
        Assert.Equal("IHandler", symbol.ContainingType?.Name);
        Assert.Equal("Task<Response<int>>", symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        var awaitExpression = tree.GetRoot()
            .DescendantNodes()
            .OfType<PrefixOperatorExpressionSyntax>()
            .Single(awaitExpression => awaitExpression.Expression == invocation);

        Assert.Equal("Response<int>", model.GetTypeInfo(awaitExpression).Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }
}
