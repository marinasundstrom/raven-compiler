using System;
using System.IO;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public class ControlFlowExpressionCodeGenTests
{
    [Fact]
    public void LiteralIfBranches_EmitAndRunInReleaseMode()
    {
        const string source = """
import System.*

func Main() {
    if true {
        Console.WriteLine("then")
    } else {
        Console.WriteLine("unreachable")
    }

    if false {
        Console.WriteLine("unreachable")
    } else {
        Console.WriteLine("else")
    }
}
""";

        AssertOutput(source, ["then", "else"], OptimizationLevel.Release);
    }

    [Theory]
    [InlineData(OptimizationLevel.Debug)]
    [InlineData(OptimizationLevel.Release)]
    public void BooleanIdentities_PreserveShortCircuitSideEffects(OptimizationLevel optimizationLevel)
    {
        const string source = """
import System.*

func Observe(value: bool) -> bool {
    Console.WriteLine(value)
    return value
}

func Main() {
    Console.WriteLine(false && Observe(true))
    Console.WriteLine(true || Observe(false))
    Console.WriteLine(Observe(true) && true)
    Console.WriteLine(Observe(false) || false)
    Console.WriteLine(Observe(true) && false)
    Console.WriteLine(Observe(false) || true)
}
""";

        AssertOutput(
            source,
            ["False", "True", "True", "True", "False", "False", "True", "False", "False", "True"],
            optimizationLevel);
    }

    [Fact]
    public void MatchArms_WithBreakContinueAndLabels_EmitAndRun()
    {
        const string source = """
import System.*

func Main() {
    var outerCount = 0
outer: loop {
        outerCount += 1
        var innerCount = 0
        loop {
            innerCount += 1
            let selected = match (outerCount, innerCount) {
                (1, 1) => continue
                (1, 2) => continue outer
                (2, 1) => break
                _ => break outer
            }
        }
        Console.WriteLine("inner")
    }
    Console.WriteLine(outerCount)
}
""";

        AssertOutput(source, ["inner", "3"]);
    }

    [Fact]
    public void MatchArms_WithYieldAndReturn_EmitAndRun()
    {
        const string source = """
import System.*
import System.Collections.Generic.*

func Items(stop: bool) -> IEnumerable<int> {
    yield 1
    let item = match stop {
        true => return
        false => 2
    }
    yield item
    match item {
        2 => yield 3
        _ => return
    }
    return
}

func Main() {
    for item in Items(false) {
        Console.WriteLine(item)
    }
    for item in Items(true) {
        Console.WriteLine(item)
    }
}
""";

        AssertOutput(source, ["1", "2", "3", "1"]);
    }

    [Theory]
    [InlineData(OptimizationLevel.Debug)]
    [InlineData(OptimizationLevel.Release)]
    public void CompoundMatchGuards_EvaluateOnceAndShortCircuit(OptimizationLevel optimizationLevel)
    {
        const string source = """
import System.*

func Observe(value: bool) -> bool {
    Console.WriteLine(value)
    return value
}

func Check(value: int) -> string {
    value match {
        let n when Observe(n > 0) && Observe(n < 10) => "in range"
        let n when Observe(n == 0) || Observe(n == 10) => "boundary"
        _ => "outside"
    }
}

Console.WriteLine(Check(5))
Console.WriteLine(Check(0))
Console.WriteLine(Check(11))
""";

        AssertOutput(source,
            ["True", "True", "in range", "False", "True", "boundary",
             "True", "False", "False", "False", "outside"], optimizationLevel);
    }

    private static void AssertOutput(
        string source,
        string[] expected,
        OptimizationLevel optimizationLevel = OptimizationLevel.Debug)
    {
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create(
                "control-flow-expressions",
                new CompilationOptions(OutputKind.ConsoleApplication)
                    .WithOptimizationLevel(optimizationLevel))
            .AddSyntaxTrees(SyntaxTree.ParseText(source))
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var entryPoint = loaded.Assembly.EntryPoint!;
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, output);
    }
}
