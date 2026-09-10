using System.IO;
using System.Linq;
using System;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

namespace Raven.CodeAnalysis.Tests;

public class AsyncPropagateCodeGenTests
{
    [Fact]
    public void PropagateExpression_UsedAsInvocationArgument_EmitsAndRuns()
    {
        var code = """
import System.*
import System.Collections.Generic.*

class Program {
    static func Main() -> Result<(), string> {
        let xs: Item[] = [Item("A")]
        let names = Collect(xs)?
        Console.WriteLine(names.Length)
        return .Ok
    }

    static func Collect(items: Item[]) -> Result<string[], string> {
        let values = List<string>()
        values.Add(GetName(items[0])?)
        return Ok(values.ToArray())
    }

    static func GetName(item: Item) -> Result<string, string> {
        return Ok(item.Name)
    }
}

record class Item(val Name: string)
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "1" }, output);
    }

    private static string[] CompileAndRun(string code)
    {
        var syntaxTree = SyntaxTree.ParseText(code);
        var references = GetReferencesWithRavenCore();
        var compilation = Compilation.Create(
            "async-propagate",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.ConsoleApplication));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var entryPoint = loaded.Assembly.EntryPoint!;

        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            var parameters = entryPoint.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            entryPoint.Invoke(null, parameters);
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

    private static MetadataReference[] GetReferencesWithRavenCore()
    {
        var corePath = Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll");
        if (!File.Exists(corePath))
            return TestMetadataReferences.Default;

        return [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(corePath)];
    }
}
