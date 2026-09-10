using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Tests.Macros;

public sealed class StronglyTypedExpressionMacroTests
{
    [Fact]
    public void RavenDeclarations_UseTypedInputAndOutputContracts()
    {
        var macroCompilation = Compilation.Create(
                "TypedMacroLibrary",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTreesWithLocalMacros(SyntaxTree.ParseText(
                """
                import Raven.CodeAnalysis.Macros.*
                import Raven.CodeAnalysis.Syntax.*

                [assembly: RavenCompilerPlugin]

                namespace TypedMacros

                public macro Produce() -> Raven.CodeAnalysis.Macros.ExpressionSyntax<int> {
                    expand SyntaxFactory.ParseExpression("42")
                }

                public macro Require(
                    value: Raven.CodeAnalysis.Macros.ExpressionSyntax<int>
                ) -> Raven.CodeAnalysis.Macros.ExpressionSyntax<int> {
                    expand value.Syntax
                }
                """));
        using var image = new System.IO.MemoryStream();
        var emitResult = macroCompilation.Emit(image);
        Assert.True(emitResult.Success, string.Join(System.Environment.NewLine, emitResult.Diagnostics));

        var consumer = Compilation.Create(
                "TypedMacroConsumer",
                new CompilationOptions(OutputKind.ConsoleApplication))
            .AddReferences(TestMetadataReferences.Default)
            .AddMacroReferences(MacroReference.CreateFromImage(image.ToArray()))
            .AddSyntaxTrees(SyntaxTree.ParseText(
                """
                import TypedMacros.*

                let value: int = Require!(Produce!())
                """));

        Assert.DoesNotContain(
            consumer.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void TypedInput_RejectsIncompatibleExpressionBeforeExecution()
    {
        var macro = new RequireIntMacro();
        var compilation = CreateCompilation(
            "let value = requireInt!(\"not an int\")",
            macro);

        var diagnostic = Assert.Single(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Descriptor.Id == "RAVM037");
        Assert.Contains("requires an expression compatible with 'int'", diagnostic.GetMessage());
        Assert.False(macro.Executed);
    }

    [Fact]
    public void TypedOutput_RejectsExpansionWithIncompatibleBoundType()
    {
        var compilation = CreateCompilation(
            "let value = badInt!()",
            new BadIntMacro());

        var diagnostic = Assert.Single(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Descriptor.Id == "RAVM023");
        Assert.Contains("promises an expression compatible with 'int'", diagnostic.GetMessage());
        Assert.Contains("string", diagnostic.GetMessage());
    }

    [Fact]
    public void NestedTypedMacros_ComposeThroughBoundExpressionType()
    {
        var outer = new RequireIntMacro();
        var compilation = CreateCompilation(
            "let value: int = requireInt!(produceInt!())",
            new ProduceIntMacro(),
            outer);

        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.True(outer.Executed);
    }

    private static Compilation CreateCompilation(string source, params IMacroDefinition[] macros)
        => Compilation.Create(
                "StronglyTypedExpressionMacros",
                new CompilationOptions(OutputKind.ConsoleApplication))
            .AddReferences(TestMetadataReferences.Default)
            .AddMacroReferences(macros.Select(static macro => new MacroReference(macro)).ToArray())
            .AddSyntaxTrees(SyntaxTree.ParseText($$"""
                import Raven.CodeAnalysis.Tests.Macros.*

                {{source}}
                """));

    private sealed class RequireIntMacro : IMacroDefinition
    {
        public string Name => "requireInt";
        public bool Executed { get; private set; }

        public Raven.CodeAnalysis.Syntax.ExpressionSyntax Expand(
            Raven.CodeAnalysis.Macros.ExpressionSyntax<int> value)
        {
            Executed = true;
            return value.Syntax;
        }
    }

    private sealed class ProduceIntMacro : IMacroDefinition
    {
        public string Name => "produceInt";
        public Type? ExpressionResultType => typeof(int);

        public Raven.CodeAnalysis.Syntax.ExpressionSyntax Expand()
            => SyntaxFactory.ParseExpression("42");
    }

    private sealed class BadIntMacro : IMacroDefinition
    {
        public string Name => "badInt";
        public Type? ExpressionResultType => typeof(int);

        public Raven.CodeAnalysis.Syntax.ExpressionSyntax Expand()
            => SyntaxFactory.ParseExpression("\"wrong\"");
    }
}
