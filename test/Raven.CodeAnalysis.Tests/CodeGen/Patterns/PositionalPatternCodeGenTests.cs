using System;
using System.IO;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public class PositionalPatternCodeGenTests
{
    [Theory]
    [InlineData("(1, 2)", "3")]
    [InlineData("(1, 2, 3)", "no match")]
    [InlineData("(1, \"two\")", "no match")]
    [InlineData("42", "no match")]
    public void MatchExpression_WithPositionalPattern_EmitsAndRuns(string value, string expected)
    {
        var code = $$"""
import System.*

func describe(value: object) -> string {
    value match {
        (let first: int, let second: int) => "${first + second}"
        _ => "no match"
    }
}

func Main() {
    let value: object = {{value}}
    Console.WriteLine(describe(value))
}
""";

        Assert.Equal(expected, CompileAndRun(code, "tuple_pattern_emit"));
    }

    [Fact]
    public void LetPositionalPatternAssignment_EmitsAndRuns()
    {
        const string code = """
import System.*

func Main() {
    let (first, second) = (1, 2)
    Console.WriteLine(first)
    Console.WriteLine(second)
}
""";

        Assert.Equal("1\n2", CompileAndRun(code, "let_tuple_pattern_emit"));
    }

    [Fact]
    public void PositionalPatternAssignment_WithExistingLocals_EmitsAndRuns()
    {
        const string code = """
import System.*

func Main() {
    var first = 0
    var second = 0
    (first, second) = (1, 2)
    Console.WriteLine(first + second)
}
""";

        Assert.Equal("3", CompileAndRun(code, "tuple_pattern_existing_locals"));
    }

    [Fact]
    public void VarPositionalPatternAssignment_EmitsAndRuns()
    {
        const string code = """
import System.*

func Main() {
    (var first, var second) = (1, 2)
    Console.WriteLine(first + second)
}
""";

        Assert.Equal("3", CompileAndRun(code, "tuple_pattern_var_assignment"));
    }

    [Fact]
    public void MixedPositionalPatternAssignment_EmitsAndRuns()
    {
        const string code = """
import System.*

func Main() {
    let (first, ignoredFirst) = (1, 2)
    var (second, ignoredSecond) = (3, 4)
    Console.WriteLine(first)
    Console.WriteLine(second)
}
""";

        Assert.Equal("1\n3", CompileAndRun(code, "tuple_pattern_mixed_assignment"));
    }

    private static string CompileAndRun(string code, string assemblyName)
    {
        var tree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create(assemblyName, new CompilationOptions(OutputKind.ConsoleApplication))
            .AddSyntaxTrees(tree)
            .AddReferences(references);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(stream, references);
        var entryPoint = loaded.Assembly.EntryPoint;
        Assert.NotNull(entryPoint);

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            var arguments = entryPoint!.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            entryPoint.Invoke(null, arguments);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString().ReplaceLineEndings("\n").TrimEnd('\n');
    }
}
