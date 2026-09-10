using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using Microsoft.CodeAnalysis;

using Raven.CodeAnalysis.Testing;

using Xunit;

using RavenSyntaxTree = Raven.CodeAnalysis.Syntax.SyntaxTree;

namespace Raven.CodeAnalysis.Tests;

public class MatchExpressionCodeGenTests
{
    private static readonly OpCode[] SingleByteOpCodes;
    private static readonly OpCode[] MultiByteOpCodes;

    static MatchExpressionCodeGenTests()
    {
        SingleByteOpCodes = new OpCode[0x100];
        MultiByteOpCodes = new OpCode[0x100];

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
                continue;

            var value = (ushort)opcode.Value;
            if (value < 0x100)
            {
                SingleByteOpCodes[value] = opcode;
            }
            else if ((value & 0xFF00) == 0xFE00)
            {
                MultiByteOpCodes[value & 0xFF] = opcode;
            }
        }
    }

    [Fact]
    public void MatchExpression_WithValueTypeArm_EmitsAndRuns()
    {
        const string code = """
let value: object = 42
let result = match value {
    int i => i.ToString()
    _ => "None"
}

System.Console.WriteLine(result)
""";

        var output = EmitAndRun(code, "match_value_type");
        Assert.Equal("42", output);
    }

    [Fact]
    public void MatchExpression_AsReturnValue_EmitsAndRuns()
    {
        const string code = """
class Program {
    static func Main() {
        let describer = Describer()
        let zero = describer.Describe(0)
        let two = describer.Describe(2)
        System.Console.WriteLine(zero + "," + two)
    }
}

class Describer {
    public func Describe(value: int) -> string {
        return match value {
            0 => "zero"
            _ => value.ToString()
        }
    }
}
""";

        var output = EmitAndRun(code, "match_return_value");
        Assert.Equal("zero,2", output);
    }

    [Fact]
    public void MatchExpression_FinalCatchAll_EmitsAndRunsInReleaseMode()
    {
        const string code = """
class Program {
    static func Main() {
        System.Console.WriteLine(Describe(0) + "," + Describe(2))
    }

    static func Describe(value: int) -> string {
        return match value {
            0 => "zero"
            _ => "other"
        }
    }
}
""";

        var output = EmitAndRunRelease(code, "match_release_catch_all");
        Assert.Equal("zero,other", output);
    }

    [Fact]
    public void MatchExpression_WithTypeParameterConstrainedToClosedHierarchy_EmitsAndRuns()
    {
        const string code = """
sealed record class Shape permits Circle, Square {}
record class Circle(Radius: double) : Shape {}
record class Square(Side: double) : Shape {}

func Area<T>(shape: T) -> double
    where T: Shape {
    return match shape {
        Circle(let radius) => System.Math.PI * radius * radius
        Square(let side) => side * side
    }
}

func Main() {
    System.Console.WriteLine(Area(Circle(2.0)) == System.Math.PI * 4.0)
    System.Console.WriteLine(Area(Square(3.0)) == 9.0)
}
""";

        var output = EmitAndRun(code, "match_constrained_closed_hierarchy");
        Assert.Equal("True\nTrue", output);
    }

    [Fact]
    public void MatchExpression_SourceExhaustiveUnion_ThrowsWhenForcedDefaultCarrierDoesNotMatch()
    {
        const string code = """
class Program {
    public static func Describe(value: State) -> string {
        return match value {
            .On => "on"
            .Off => "off"
        }
    }
}

union State {
    case On
    case Off
}
""";

        var syntaxTree = RavenSyntaxTree.ParseText(code);
        var compilation = Compilation.Create("match_union_default_fallback", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(RuntimeMetadataReferences);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var assembly = Assembly.Load(peStream.ToArray());
        var programType = assembly.GetType("Program", throwOnError: true)!;
        var stateType = assembly.GetType("State", throwOnError: true)!;
        var describe = programType.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)!;
        var defaultState = Activator.CreateInstance(stateType);

        var exception = Assert.Throws<TargetInvocationException>(() => describe.Invoke(null, [defaultState]));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void MatchExpression_WithStringLiteralPattern_MatchesExactValue()
    {
        const string code = """
let fooValue: string = "foo"
let foo = match fooValue {
    "foo" => "str"
    _ => "None"
}

let emptyValue: string = ""
let empty = match emptyValue {
    "foo" => "str"
    _ => "None"
}

System.Console.WriteLine(foo + "," + empty)
""";

        var output = EmitAndRun(code, "match_string_literal");
        Assert.Equal("str,None", output);
    }

    [Fact]
    public void MatchExpression_WithArrayCollectionPattern_EmitsAndRuns()
    {
        const string code = """
class Formatter {
    public func Describe(values: int[]) -> string {
        return match values {
            [let first, let second] => (first + second).ToString()
            _ => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        System.Console.WriteLine(formatter.Describe([2, 3]))
    }
}
""";

        var output = EmitAndRun(code, "match_array_collection_pattern");

        Assert.Equal("5", output);
    }

    [Fact]
    public void MatchExpression_WithArrayCollectionPatternMiddleRest_EmitsAndRuns()
    {
        const string code = """
class Formatter {
    public func Describe(values: int[]) -> string {
        return match values {
            [let first, ..let middle, let last] => (first + middle[0] + last).ToString()
            _ => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        System.Console.WriteLine(formatter.Describe([2, 3, 4]))
    }
}
""";

        var output = EmitAndRun(code, "match_array_collection_pattern_middle_rest");

        Assert.Equal("9", output);
    }

    [Fact(Skip = "List collection pattern middle-rest emission currently produces a null rest list; keep isolated until CodeGen is fixed.")]
    public void MatchExpression_WithListCollectionPatternMiddleRest_EmitsAndRuns()
    {
        const string code = """
import System.Collections.Generic.*

class Formatter {
    public func Describe(values: List<int>) -> string {
        return match values {
            [let first, ..let middle, let last] => (first + middle[0] + last).ToString()
            _ => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        System.Console.WriteLine(formatter.Describe([2, 3, 4]))
    }
}
""";

        var output = EmitAndRun(code, "match_list_collection_pattern_middle_rest");

        Assert.Equal("9", output);
    }

    [Fact]
    public void MatchExpression_WithArrayCollectionFixedSegment_EmitsAndRuns()
    {
        const string code = """
class Formatter {
    public func Describe(values: int[]) -> string {
        return match values {
            [..2 let start, let end] => (start[0] + start[1] + end).ToString()
            _ => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        System.Console.WriteLine(formatter.Describe([2, 3, 4]))
    }
}
""";

        var output = EmitAndRun(code, "match_array_collection_pattern_fixed_segment");

        Assert.Equal("9", output);
    }

    [Fact]
    public void MatchExpression_WithStringCollectionFixedSegment_EmitsAndRuns()
    {
        const string code = """
class Formatter {
    public func Describe(text: string) -> string {
        return match text {
            [let first, ..2 let middle, let last] => first.ToString() + ":" + middle + ":" + last.ToString()
            _ => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        System.Console.WriteLine(formatter.Describe("rune"))
    }
}
""";

        var output = EmitAndRun(code, "match_string_collection_pattern_fixed_segment");

        Assert.Equal("r:un:e", output);
    }

    [Fact]
    public void MatchExpression_WithDiscriminatedUnion_UsesTryGetAndCaseProperties()
    {
        const string code = """
union Result<T, TError> {
    case Ok(value: T)
    case Error(message: TError)
}

class Formatter {
    public func Format(result: Result<int, string>) -> string {
        return match result {
            .Ok(let value) => "ok ${value}"
            .Error(let message) => "error ${message}"
        }
    }
}
""";

        var syntaxTree = RavenSyntaxTree.ParseText(code);
        var compilation = Compilation.Create("match_union", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(RuntimeMetadataReferences);

        compilation.EnsureSetup();

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var assembly = Assembly.Load(peStream.ToArray());
        var formatterType = assembly.GetType("Formatter", throwOnError: true)!;
        var formatMethod = formatterType.GetMethod("Format", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)!;

        var calledMethods = GetCalledMethods(formatMethod).ToArray();

        Assert.True(calledMethods.Count(method => method.Name == "TryGetValue") >= 2);
        Assert.Contains(calledMethods, method => method.Name == "get_Value");
        Assert.Contains(calledMethods, method => method.Name == "get_Message");
    }

    [Fact]
    public void MatchExpression_WithUnionIdentifierResult_ParsesNewlineSeparatedArms()
    {
        const string code = """
union Test {
    case Something(value: string)
    case Nothing
}

class Formatter {
    public func Describe(value: Test) -> string {
        return match value {
            .Something(let text) => text
            .Nothing => "none"
        }
    }
}

class Program {
    static func Main() {
        let formatter = Formatter()
        let something = Test.Something("hello")
        let nothing = Test.Nothing
        System.Console.WriteLine(formatter.Describe(something) + "," + formatter.Describe(nothing))
    }
}
""";

        var output = EmitAndRun(code, "match_union_identifier_expression");

        Assert.Equal("hello,none", output);
    }

    [Fact]
    public void MatchExpression_WithGenericUnionCases_EmitsAndRuns()
    {
        const string code = """
import System.*

let ok: Result<int, string> = .Ok(99)
let err = Result<int, string>.Error("boom")

System.Console.WriteLine(format(ok))
System.Console.WriteLine(format((Result<int, string>)err))

func format<T>(result: Result<T, string>) -> string {
    return match result {
        .Ok(let value) => "ok ${value}"
        .Error(let message) => "error '${message}'"
    }
}

union Result<T, TError> {
    case Ok(value: T)
    case Error(message: TError)
}
""";

        var output = EmitAndRun(code, "match_generic_union");

        Assert.Equal("ok 99\nerror 'boom'", output.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void MatchExpression_ParameterlessUnionCase_AllowsOmittedInvocation()
    {
        const string code = """
union Test {
    case Something(value: string)
    case Nothing
}

class Program {
    static func describe(value: Test) -> string {
        return match value {
            .Something(let text) => text
            .Nothing => "none"
        }
    }

    static func Main() {
        let a = Test.Something("foo")
        let b = Test.Nothing
        System.Console.WriteLine(describe(a) + "," + describe(b))
    }
}
""";

        var output = EmitAndRun(code, "match_union_parameterless_instantiation");

        Assert.Equal("foo,none", output);
    }

    private static IEnumerable<MethodBase> GetCalledMethods(MethodInfo method)
    {
        var body = method.GetMethodBody() ?? throw new InvalidOperationException("Method has no body.");
        var il = body.GetILAsByteArray() ?? throw new InvalidOperationException("Method body has no IL.");
        var module = method.Module;

        for (var i = 0; i < il.Length;)
        {
            var opcode = ReadOpCode(il, ref i);
            int? methodToken = null;

            switch (opcode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineType:
                    i += 4;
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    methodToken = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.ShortInlineR:
                    i += 4;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, i);
                    i += 4 + (count * 4);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported operand type: {opcode.OperandType}");
            }

            if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && methodToken.HasValue)
            {
                MethodBase? resolved = null;

                try
                {
                    resolved = module.ResolveMethod(methodToken.Value);
                }
                catch (ArgumentException)
                {
                }
                catch (MissingMethodException)
                {
                }

                if (resolved is not null)
                    yield return resolved;
            }
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int index)
    {
        if (index >= il.Length)
            throw new InvalidOperationException("Unexpected end of IL stream.");

        var code = il[index++];
        if (code == 0xFE)
        {
            if (index >= il.Length)
                throw new InvalidOperationException("Unexpected end of IL stream when decoding multi-byte opcode.");

            var second = il[index++];
            var opcode = MultiByteOpCodes[second];
            if (opcode.Value == 0 && opcode != OpCodes.Nop)
                throw new InvalidOperationException($"Unknown opcode: 0xFE 0x{second:X2}");
            return opcode;
        }

        var single = SingleByteOpCodes[code];
        if (single.Value == 0 && single != OpCodes.Nop)
            throw new InvalidOperationException($"Unknown opcode: 0x{code:X2}");

        return single;
    }

    private static string EmitAndRun(string code, string assemblyName, params string[] additionalSources)
        => EmitAndRunCore(
            code,
            assemblyName,
            new CompilationOptions(OutputKind.ConsoleApplication),
            additionalSources);

    private static string EmitAndRunRelease(string code, string assemblyName)
        => EmitAndRunCore(
            code,
            assemblyName,
            new CompilationOptions(OutputKind.ConsoleApplication)
                .WithOptimizationLevel(OptimizationLevel.Release),
            []);

    private static string EmitAndRunCore(
        string code,
        string assemblyName,
        CompilationOptions options,
        params string[] additionalSources)
    {
        var syntaxTrees = new List<RavenSyntaxTree> { RavenSyntaxTree.ParseText(code) };

        foreach (var source in additionalSources)
            syntaxTrees.Add(RavenSyntaxTree.ParseText(source));

        var references = RuntimeMetadataReferences;

        var compilation = Compilation.Create(assemblyName, options)
            .AddSyntaxTrees(syntaxTrees.ToArray())
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var assemblyBytes = peStream.ToArray();
        var assembly = Assembly.Load(assemblyBytes);
        var entryPoint = assembly.EntryPoint;
        Assert.NotNull(entryPoint);

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            Assert.NotNull(typeof(System.Runtime.CompilerServices.ITuple).GetProperty("Length"));
            var parameters = entryPoint!.GetParameters();

            object?[]? arguments = parameters.Length switch
            {
                0 => null,
                1 => new object?[] { Array.Empty<string>() },
                _ => throw new InvalidOperationException("Unexpected entry point signature."),
            };

            entryPoint.Invoke(null, arguments);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        return output.ReplaceLineEndings("\n").TrimEnd('\n');
    }

    private static readonly MetadataReference[] RuntimeMetadataReferences = GetRuntimeMetadataReferences();

    private static MetadataReference[] GetRuntimeMetadataReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(tpa))
            return TestMetadataReferences.Default;

        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(path))
                continue;

            var name = Path.GetFileNameWithoutExtension(path);
            if (!seen.Add(name))
                continue;

            references.Add(MetadataReference.CreateFromFile(path));
        }

        return references.ToArray();
    }
}
