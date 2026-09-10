using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public sealed class PdbSequencePointTests
{
    [Fact]
    public void ReleaseMode_PreservesVisiblePortablePdbSequencePoints()
    {
        var code = """
class C {
    func AddOne(value: int) -> int {
        let result = value + 1
        return result
    }
}
""";

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release);
        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, options);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "AddOne");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        Assert.Equal([3, 4], GetVisibleSequencePointStartLines(pdbReader, method).Distinct().ToArray());

        peReader.Dispose();
    }

    [Fact]
    public void CorrectedPdb_PreservesMissingTrailingMethodDebugRows()
    {
        var metadata = new MetadataBuilder();
        var document = metadata.AddDocument(
            metadata.GetOrAddDocumentName("source.rvn"),
            default,
            default,
            default);
        metadata.AddMethodDebugInformation(document, default);
        var rowCounts = new int[64];
        rowCounts[(int)TableIndex.MethodDef] = 2;
        var portablePdb = new PortablePdbBuilder(metadata, rowCounts.ToImmutableArray(), default);
        var originalBlob = new BlobBuilder();
        var contentId = portablePdb.Serialize(originalBlob);
        using var original = new MemoryStream();
        originalBlob.WriteContentTo(original);
        original.Position = 0;
        using var corrected = new MemoryStream();

        Raven.CodeAnalysis.CodeGen.CodeGenerator.EmitCorrectedPdb(
            original,
            rowCounts.ToImmutableArray(),
            default,
            new Dictionary<int, int> { [1] = 2, [2] = 1 },
            contentId,
            corrected);

        corrected.Position = 0;
        using var provider = MetadataReaderProvider.FromPortablePdbStream(corrected);
        var reader = provider.GetMetadataReader();
        Assert.Equal(2, reader.GetTableRowCount(TableIndex.MethodDebugInformation));
        Assert.True(reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(1)).Document.IsNil);
        Assert.False(reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(2)).Document.IsNil);
    }

    [Fact]
    public void AsyncMethod_KickoffIsHiddenAndMoveNext_HasVisibleSequencePoints()
    {
        var code = """
import System.Threading.Tasks.*

interface IWorker {
    func Work() -> Task<int>
}

class C {
    async func Work() -> Task<int> {
        await Task.Delay(1)
        return 42
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);

        var kickoff = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Work");

        var moveNext = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__AsyncStateMachine", StringComparison.Ordinal) &&
            methodName == "MoveNext");
        var setStateMachine = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__AsyncStateMachine", StringComparison.Ordinal) &&
            methodName == "SetStateMachine");

        AssertMethodHasOnlyHiddenSequencePoints(pdbReader, kickoff);
        AssertMethodHasVisibleSequencePoint(metadataReader, pdbReader, moveNext);
        Assert.Equal([9, 10], GetVisibleSequencePointStartLines(pdbReader, moveNext).Distinct().ToArray());
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, moveNext);
        Assert.Empty(GetVisibleSequencePoints(pdbReader, setStateMachine));

        peReader.Dispose();
    }

    [Fact]
    public void IteratorKickoffIsHiddenAndMoveNext_HasVisibleSequencePoints()
    {
        var code = """
import System.Collections.Generic.*

class C {
    func Values() -> IEnumerable<int> {
        yield 1
        yield 2
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);

        var moveNext = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__Iterator", StringComparison.Ordinal) &&
            methodName == "MoveNext");

        var kickoff = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Values");
        var currentGetter = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__Iterator", StringComparison.Ordinal) &&
            methodName == "get_Current");

        AssertMethodHasOnlyHiddenSequencePoints(pdbReader, kickoff);
        AssertMethodHasVisibleSequencePoint(metadataReader, pdbReader, moveNext);
        Assert.Equal([5, 6], GetVisibleSequencePointStartLines(pdbReader, moveNext).Distinct().ToArray());
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, moveNext);
        Assert.Empty(GetVisibleSequencePoints(pdbReader, currentGetter));
        AssertMethodHasAttribute(metadataReader, kickoff, "IteratorStateMachineAttribute");

        peReader.Dispose();
    }

    [Fact]
    public void MultipleStateMachines_KeepSequencePointsOnTheirOwnMoveNextMethods()
    {
        var code = """
import System.Threading.Tasks.*

class C {
    async func First() -> Task<int> {
        await Task.Delay(1)
        return 1
    }

    async func Second() -> Task<int> {
        await Task.Delay(1)
        return 2
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var moveNextMethods = FindMethods(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__AsyncStateMachine", StringComparison.Ordinal) &&
            methodName == "MoveNext");
        var helperMethods = FindMethods(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__AsyncStateMachine", StringComparison.Ordinal) &&
            methodName == "SetStateMachine");

        Assert.Equal(2, moveNextMethods.Length);
        Assert.All(moveNextMethods, method =>
        {
            AssertMethodHasVisibleSequencePoint(metadataReader, pdbReader, method);
            AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, method);
        });
        Assert.Equal(
            [5, 6, 10, 11],
            moveNextMethods
                .SelectMany(method => GetVisibleSequencePointStartLines(pdbReader, method))
                .Distinct()
                .Order()
                .ToArray());
        Assert.All(helperMethods, method => Assert.Empty(GetVisibleSequencePoints(pdbReader, method)));

        peReader.Dispose();
    }

    [Fact]
    public void RuntimeAsyncMethod_HasSequencePoints_WithoutSynthesizedMoveNext()
    {
        var code = """
import System.Threading.Tasks.*

class C {
    async func Work() -> Task<int> {
        await Task.Delay(1)
        return 42
    }
}
""";

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithRuntimeAsync(true);
        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, options);

        var kickoff = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Work");

        AssertMethodHasVisibleSequencePoint(pdbReader, kickoff);
        AssertNoMethod(metadataReader, static (typeName, methodName) =>
            typeName.Contains("<>c__AsyncStateMachine", StringComparison.Ordinal) &&
            methodName == "MoveNext");

        peReader.Dispose();
    }

    [Fact]
    public void MatchStatementMethod_HasMultipleVisibleSequencePoints()
    {
        var code = """
class C {
    func Evaluate(x: int) -> int {
        var result = 0
        match x {
            1 => result = 10
            _ => result = 20
        }

        return result
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Evaluate");

        var lines = GetVisibleSequencePointStartLines(pdbReader, method);
        Assert.Contains(5, lines);
        Assert.Contains(6, lines);
        Assert.Contains(9, lines);

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasAtLeastVisibleSequencePoints(pdbReader, method, minimumVisiblePoints: 3);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, method, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void MatchStatement_SequencePoints_StartAtMatchThenArmBodies()
    {
        var code = """
class C {
    func Evaluate(x: int) -> int {
        var result = 0
        match x {
            1 => {
                result = 10
            }
            _ => result = 20
        }

        return result
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Evaluate");

        var lines = GetVisibleSequencePointStartLines(pdbReader, method);
        Assert.Contains(4, lines);
        Assert.Contains(6, lines);
        Assert.Contains(8, lines);
        Assert.Contains(11, lines);
        Assert.DoesNotContain(5, lines);

        AssertMethodHasNoVisibleLocalContaining(pdbReader, method, "match_scrutinee");

        peReader.Dispose();
    }

    [Theory]
    [InlineData(OptimizationLevel.Debug)]
    [InlineData(OptimizationLevel.Release)]
    public void TryCatchFinallyMethod_HasMultipleVisibleSequencePoints(OptimizationLevel optimizationLevel)
    {
        var code = """
import System.*

class C {
    func Compute(v: int) -> int {
        try {
            if v == 0 {
                throw Exception()
            }

            return v
        } catch (Exception e) {
            return -1
        } finally {
            let _ = 0
        }
    }
}
""";

        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(optimizationLevel);
        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, options);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Compute");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasAtLeastVisibleSequencePoints(pdbReader, method, minimumVisiblePoints: 4);

        peReader.Dispose();
    }

    [Fact]
    public void ForLoopMethod_HasSequencePointsInsideLoopBody()
    {
        var code = """
class C {
    func Sum() -> int {
        var total = 0
        for value in 1..3 {
            total = total + value
        }

        return total
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Sum");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasVisibleSequencePointOnLine(pdbReader, method, line: 5);

        peReader.Dispose();
    }

    [Fact]
    public void GotoMethod_HasSequencePointOnGotoStatement()
    {
        var code = """
class C {
    func Retry() -> int {
        var retries = 0
retry:
        retries += 1
        if retries < 3 {
            goto retry
        }

        return retries
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Retry");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasVisibleSequencePointOnLine(pdbReader, method, line: 7);

        peReader.Dispose();
    }

    [Fact]
    public void FunctionMethod_HasSequencePointsInsideBody_ForStepIn()
    {
        var code = """
class C {
    func Compute(x: int) -> int {
        let adjusted = x + 1
        return adjusted
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Compute");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasVisibleSequencePointOnLine(pdbReader, method, line: 3);
        AssertMethodHasVisibleSequencePointOnLine(pdbReader, method, line: 4);

        peReader.Dispose();
    }

    [Fact]
    public void FunctionMethod_DoesNotEmitWideBlockSequencePoints()
    {
        var code = """
class C {
    func Compute() -> () {
        let x = 1
        let y = x + 2
        let z = y + 3
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Compute");

        AssertMethodHasVisibleSequencePoint(pdbReader, method);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, method, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void AbstractMethod_DoesNotShiftLaterSequencePointsToEarlierMethods()
    {
        var code = """
import System.*

interface IWorker {
    func AddOne(value: int) -> int
}

class Worker : IWorker {
    func AddOne(value: int) -> int {
        let result = value + 1
        return result
    }
}

func Main() {
    let result = Worker().AddOne(41)
    Console.WriteLine(result)
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(
            code,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var abstractMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "IWorker" && methodName == "AddOne");
        var workerMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Worker" && methodName == "AddOne");
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");

        Assert.Empty(GetVisibleSequencePoints(pdbReader, abstractMethod));
        Assert.Equal([9, 10], GetVisibleSequencePointStartLines(pdbReader, workerMethod));
        Assert.Equal([15, 16], GetVisibleSequencePointStartLines(pdbReader, mainMethod));

        peReader.Dispose();
    }

    [Fact]
    public void TopLevelMain_VisibleSequencePointsStayOnExecutableStatements()
    {
        var code = """
import System.*

func Main() {
    Console.WriteLine("Hello, World!")

    let x = "Foo"

    Console.WriteLine("Hello, World!")
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");

        var points = GetVisibleSequencePoints(pdbReader, method).ToArray();
        var lines = points.Select(static p => p.StartLine).Distinct().ToArray();

        Assert.Contains(4, lines);
        Assert.Contains(6, lines);
        Assert.Contains(8, lines);
        Assert.DoesNotContain(3, lines);
        Assert.Contains(points, static p => p.StartLine == 4 && p.StartColumn == 5);
        Assert.Contains(points, static p => p.StartLine == 8 && p.StartColumn == 5);

        peReader.Dispose();
    }

    [Fact]
    public void ImplicitTopLevelMain_SequencePointsMatchExecutableStatements()
    {
        var code = """
import System.*

let seed = 40
let answer = seed + 2
Console.WriteLine(answer)
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Main");

        var lines = GetVisibleSequencePointStartLines(pdbReader, method);
        Assert.Contains(3, lines);
        Assert.Contains(4, lines);
        Assert.Contains(5, lines);
        Assert.DoesNotContain(1, lines);
        Assert.DoesNotContain(2, lines);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, method, maximumLineSpan: 2);
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, method);

        peReader.Dispose();
    }

    [Fact]
    public void ExplicitTopLevelMainAndHelper_SequencePointsStayInsideMethodBodies()
    {
        var code = """
import System.*

func Main() {
    let subtotal = 10
    let total = AddTax(subtotal)
    Console.WriteLine(total)
}

func AddTax(value: int) -> int {
    return value + 2
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");
        var helperMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "AddTax");

        var mainLines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(4, mainLines);
        Assert.Contains(5, mainLines);
        Assert.Contains(6, mainLines);
        Assert.DoesNotContain(3, mainLines);

        var helperLines = GetVisibleSequencePointStartLines(pdbReader, helperMethod);
        Assert.Contains(10, helperLines);
        Assert.DoesNotContain(9, helperLines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, helperMethod, maximumLineSpan: 2);
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, mainMethod);
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, helperMethod);

        peReader.Dispose();
    }

    [Fact]
    public void ClassProgramMainAndWorkerFunction_SequencePointsFollowExecutableLines()
    {
        var code = """
import System.*

class Program {
    static func Main() {
        let result = Worker.Transform(5)
        Console.WriteLine(result)
    }
}

class Worker {
    static func Transform(value: int) -> int {
        let doubled = value * 2
        return doubled + 1
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Main");
        var transformMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Worker" && methodName == "Transform");

        var mainLines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(5, mainLines);
        Assert.Contains(6, mainLines);
        Assert.DoesNotContain(4, mainLines);

        var transformLines = GetVisibleSequencePointStartLines(pdbReader, transformMethod);
        Assert.Contains(12, transformLines);
        Assert.Contains(13, transformLines);
        Assert.DoesNotContain(11, transformLines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, transformMethod, maximumLineSpan: 2);
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, mainMethod);
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, transformMethod);

        peReader.Dispose();
    }

    [Fact]
    public void ExplicitTopLevelMain_WithMatchExpression_SequencePointsStayStepFriendly()
    {
        var code = """
import System.*

func Main() {
    let value = 2
    let label = match value {
        1 => "one"
        _ => "other"
    }

    Console.WriteLine(label)
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");

        var lines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(4, lines);
        Assert.Contains(5, lines);
        Assert.Contains(10, lines);
        Assert.DoesNotContain(3, lines);

        peReader.Dispose();
    }

    [Fact]
    public void MatchExpression_SequencePoints_StartAtMatchThenArmBodies()
    {
        var code = """
import System.*

func Main() {
    let value = 2
    let label = match value {
        1 => {
            "one"
        }
        _ => "other"
    }

    Console.WriteLine(label)
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");

        var lines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(5, lines);
        Assert.Contains(7, lines);
        Assert.Contains(12, lines);
        Assert.DoesNotContain(6, lines);

        AssertMethodHasNoVisibleLocalContaining(pdbReader, mainMethod, "match_scrutinee");
        AssertMethodHasNoVisibleLocalContaining(pdbReader, mainMethod, "match_result");

        peReader.Dispose();
    }

    [Fact]
    public void MatchExpression_WithWhen_GuardAndArmLinesAreSteppingPoints()
    {
        var code = """
func Main() {
    let value = GetValue()
    let text = match value {
        1 when value > 0 => "ok"
        _ => "no"
    }
}

func GetValue() -> int => 1
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var syntaxTree = SyntaxTree.ParseText(code);
        var matchExpression = syntaxTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var guardLine = matchExpression.Arms[0].WhenClause!.Guard.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var firstArmLine = matchExpression.Arms[0].Expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var secondArmLine = matchExpression.Arms[1].Expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");

        var lines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(guardLine, lines);
        Assert.Contains(firstArmLine, lines);
        Assert.Contains(secondArmLine, lines);

        peReader.Dispose();
    }

    [Fact]
    public void TopLevelMain_WithIfElseAndCall_HasSequencePointsOnControlFlowLines()
    {
        var code = """
import System.*

func Main() {
    let number = 3
    if number > 2 {
        Console.WriteLine("big")
    } else {
        Console.WriteLine("small")
    }

    let sum = Add(number, 5)
    Console.WriteLine(sum)
}

func Add(left: int, right: int) -> int {
    return left + right
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");
        var addMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Add");

        var mainLines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(4, mainLines);
        Assert.Contains(5, mainLines);
        Assert.Contains(6, mainLines);
        Assert.Contains(8, mainLines);
        Assert.Contains(11, mainLines);
        Assert.Contains(12, mainLines);

        var addLines = GetVisibleSequencePointStartLines(pdbReader, addMethod);
        Assert.Contains(16, addLines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, addMethod, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void ClassMethod_WithForAndWhile_HasSequencePointsInsideLoopBodies()
    {
        var code = """
class Program {
    static func Main() {
        var total = 0
        for value in 1..3 {
            total = total + value
        }

        while total < 10 {
            total += 1
        }
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Main");

        var lines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(3, lines);
        Assert.Contains(5, lines);
        Assert.Contains(8, lines);
        Assert.Contains(9, lines);
        Assert.DoesNotContain(4, lines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void TopLevelMain_WithNestedFunctionStatement_EmitsSequencePointsForOuterAndInnerBodies()
    {
        var code = """
import System.*

func Main() {
    let seed = 2
    func Bump(value: int) -> int {
        return value + 1
    }

    let result = Bump(seed)
    Console.WriteLine(result)
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName == "Main");
        var bumpMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "NamespaceMembers" && methodName.Contains("Bump", StringComparison.Ordinal));

        var mainLines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(4, mainLines);
        Assert.Contains(9, mainLines);
        Assert.Contains(10, mainLines);

        var bumpLines = GetVisibleSequencePointStartLines(pdbReader, bumpMethod);
        Assert.Contains(6, bumpLines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, bumpMethod, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void TypeMethods_WithBlockAndExpressionBodies_HaveVisibleSequencePoints()
    {
        var code = """
import System.*

class Program {
    static func Main() {
        let left = Increment(1)
        let right = Double(left)
        Console.WriteLine(right)
    }

    static func Increment(value: int) -> int => value + 1

    static func Double(value: int) -> int {
        return value * 2
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code, new CompilationOptions(OutputKind.ConsoleApplication));
        var mainMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Main");
        var incrementMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Increment");
        var doubleMethod = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "Program" && methodName == "Double");

        var mainLines = GetVisibleSequencePointStartLines(pdbReader, mainMethod);
        Assert.Contains(5, mainLines);
        Assert.Contains(6, mainLines);
        Assert.Contains(7, mainLines);

        var incrementLines = GetVisibleSequencePointStartLines(pdbReader, incrementMethod);
        Assert.Contains(10, incrementLines);

        var doubleLines = GetVisibleSequencePointStartLines(pdbReader, doubleMethod);
        Assert.Contains(13, doubleLines);

        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, mainMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, incrementMethod, maximumLineSpan: 2);
        AssertMethodHasNoWideVisibleSequencePoint(pdbReader, doubleMethod, maximumLineSpan: 2);

        peReader.Dispose();
    }

    [Fact]
    public void Regression_MatchArms_DoNotEmitDuplicateOverlappingSequencePoints()
    {
        var code = """
class C {
    func Evaluate(value: int) -> string {
        return match value {
            1 => "one"
            _ => "other"
        }
    }
}
""";

        var (peReader, metadataReader, pdbReader) = EmitWithPortablePdb(code);
        var method = FindMethod(metadataReader, static (typeName, methodName) =>
            typeName == "C" && methodName == "Evaluate");

        // Expected to fail until sequence-point de-duplication is fixed for match-arm RHS emission.
        AssertMethodHasNoOverlappingVisibleSequencePoints(pdbReader, method);

        peReader.Dispose();
    }

    [Fact]
    public void AspNetMinimalApiProject_EmitsNonOverlappingSequencePointsForMainSource()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(repoRoot, "samples", "projects", "aspnet-minimal-api", "AspNetMinimalApi.rvnproj");
        var sourcePath = Path.Combine(repoRoot, "samples", "projects", "aspnet-minimal-api", "src", "Program.rvn");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "raven-sequence-point-integration", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outputDirectory);

        try
        {
            var compilerResult = RunCompiler(repoRoot, projectPath, outputDirectory, targetFramework: "net11.0");
            Assert.True(compilerResult.ExitCode == 0,
                $"Compiler failed ({compilerResult.ExitCode}).\n{compilerResult.StdOut}\n{compilerResult.StdErr}");

            var assemblyPath = Path.Combine(outputDirectory, "AspNetMinimalApi.dll");
            var pdbPath = Path.Combine(outputDirectory, "AspNetMinimalApi.pdb");
            Assert.True(File.Exists(assemblyPath), $"Expected emitted assembly at '{assemblyPath}'.");
            Assert.True(File.Exists(pdbPath), $"Expected emitted PDB at '{pdbPath}'.");

            AssertPdbHasNoOverlappingVisibleSequencePointsForDocument(assemblyPath, pdbPath, sourcePath);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static (PEReader PeReader, MetadataReader MetadataReader, MetadataReader PdbReader) EmitWithPortablePdb(
        string source,
        CompilationOptions? options = null)
    {
        var syntaxTree = SyntaxTree.ParseText(source);
        var compilation = Compilation.Create("pdb_spans", options ?? new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(TestMetadataReferences.Default);

        var peStream = new MemoryStream();
        var pdbStream = new MemoryStream();

        var emitResult = compilation.Emit(peStream, pdbStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        peStream.Position = 0;
        pdbStream.Position = 0;

        var peReader = new PEReader(peStream, PEStreamOptions.LeaveOpen);
        var metadataReader = peReader.GetMetadataReader();
        var pdbReader = MetadataReaderProvider
            .FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen)
            .GetMetadataReader();

        return (peReader, metadataReader, pdbReader);
    }

    private static MethodDefinitionHandle FindMethod(
        MetadataReader metadataReader,
        Func<string, string, bool> predicate)
    {
        var emittedMethods = new List<string>();
        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var type = metadataReader.GetTypeDefinition(typeHandle);
            var typeName = metadataReader.GetString(type.Name);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadataReader.GetMethodDefinition(methodHandle);
                var methodName = metadataReader.GetString(method.Name);
                emittedMethods.Add($"{typeName}.{methodName}");

                if (predicate(typeName, methodName))
                    return methodHandle;
            }
        }

        throw new InvalidOperationException(
            $"Expected method definition was not found in emitted metadata.{Environment.NewLine}" +
            string.Join(Environment.NewLine, emittedMethods));
    }

    private static MethodDefinitionHandle[] FindMethods(
        MetadataReader metadataReader,
        Func<string, string, bool> predicate)
    {
        var methods = new List<MethodDefinitionHandle>();
        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var type = metadataReader.GetTypeDefinition(typeHandle);
            var typeName = metadataReader.GetString(type.Name);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadataReader.GetMethodDefinition(methodHandle);
                if (predicate(typeName, metadataReader.GetString(method.Name)))
                    methods.Add(methodHandle);
            }
        }

        return methods.ToArray();
    }

    private static void AssertMethodHasVisibleSequencePoint(MetadataReader pdbReader, MethodDefinitionHandle methodHandle)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();

        Assert.NotEmpty(points);
    }

    private static void AssertMethodHasVisibleSequencePoint(
        MetadataReader metadataReader,
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();
        Assert.True(points.Length > 0, FormatMethodsWithVisibleSequencePoints(metadataReader, pdbReader));
    }

    private static string FormatMethodsWithVisibleSequencePoints(
        MetadataReader metadataReader,
        MetadataReader pdbReader)
    {
        var methods = new List<string>();
        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var type = metadataReader.GetTypeDefinition(typeHandle);
            var typeName = metadataReader.GetString(type.Name);
            foreach (var methodHandle in type.GetMethods())
            {
                var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();
                if (points.Length == 0)
                    continue;

                var method = metadataReader.GetMethodDefinition(methodHandle);
                var methodName = metadataReader.GetString(method.Name);
                methods.Add(
                    $"{typeName}.{methodName}: " +
                    string.Join(", ", points.Select(static point =>
                        $"{point.StartLine}:{point.StartColumn}-{point.EndLine}:{point.EndColumn}")));
            }
        }

        return methods.Count == 0
            ? "No emitted methods have visible sequence points." + Environment.NewLine +
              string.Join(Environment.NewLine, metadataReader.MethodDefinitions.Select(handle =>
              {
                  var method = metadataReader.GetMethodDefinition(handle);
                  return $"{MetadataTokens.GetRowNumber(handle)}: {metadataReader.GetString(method.Name)}";
              }))
            : string.Join(Environment.NewLine, methods);
    }

    private static void AssertMethodHasOnlyHiddenSequencePoints(MetadataReader pdbReader, MethodDefinitionHandle methodHandle)
    {
        var debugHandle = methodHandle.ToDebugInformationHandle();
        var debugInfo = pdbReader.GetMethodDebugInformation(debugHandle);
        var points = debugInfo.GetSequencePoints().ToArray();

        Assert.NotEmpty(points);
        Assert.DoesNotContain(points, static point => !point.IsHidden);
    }

    private static void AssertMethodHasAtLeastVisibleSequencePoints(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle,
        int minimumVisiblePoints)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();
        Assert.True(
            points.Length >= minimumVisiblePoints,
            $"Expected at least {minimumVisiblePoints} visible sequence points, found {points.Length}.");
    }

    private static IEnumerable<SequencePoint> GetVisibleSequencePoints(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle)
    {
        var debugHandle = methodHandle.ToDebugInformationHandle();
        var debugInfo = pdbReader.GetMethodDebugInformation(debugHandle);
        return debugInfo.GetSequencePoints().Where(static p => !p.IsHidden);
    }

    private static void AssertMethodHasVisibleSequencePointOnLine(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle,
        int line)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();
        Assert.Contains(points, p => p.StartLine == line);
    }

    private static int[] GetVisibleSequencePointStartLines(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle)
        => GetVisibleSequencePoints(pdbReader, methodHandle)
            .Select(static p => p.StartLine)
            .Distinct()
            .OrderBy(static line => line)
            .ToArray();

    private static void AssertMethodHasNoWideVisibleSequencePoint(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle,
        int maximumLineSpan)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle).ToArray();
        var widePoint = points.FirstOrDefault(point => point.EndLine - point.StartLine > maximumLineSpan);
        if (!widePoint.Equals(default(SequencePoint)))
        {
            Assert.True(
                false,
                $"Unexpected wide sequence point found: {widePoint.StartLine}:{widePoint.StartColumn}-{widePoint.EndLine}:{widePoint.EndColumn}");
        }
    }

    private static void AssertMethodHasNoOverlappingVisibleSequencePoints(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle)
    {
        var points = GetVisibleSequencePoints(pdbReader, methodHandle)
            .OrderBy(static p => p.StartLine)
            .ThenBy(static p => p.StartColumn)
            .ToArray();

        for (var i = 1; i < points.Length; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            if (StartsBefore(current.StartLine, current.StartColumn, previous.EndLine, previous.EndColumn))
            {
                Assert.True(
                    false,
                    $"Overlapping sequence points found: " +
                    $"{previous.StartLine}:{previous.StartColumn}-{previous.EndLine}:{previous.EndColumn} and " +
                    $"{current.StartLine}:{current.StartColumn}-{current.EndLine}:{current.EndColumn}");
            }
        }
    }

    private static bool StartsBefore(int firstLine, int firstColumn, int secondLine, int secondColumn)
        => firstLine < secondLine || (firstLine == secondLine && firstColumn < secondColumn);

    private static void AssertMethodHasNoVisibleLocalContaining(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle,
        string valueFragment)
    {
        var names = GetMethodLocalNames(pdbReader, methodHandle);
        Assert.DoesNotContain(names, name => name.Contains(valueFragment, StringComparison.Ordinal));
    }

    private static void AssertMethodHasAttribute(
        MetadataReader metadataReader,
        MethodDefinitionHandle methodHandle,
        string attributeTypeName)
    {
        var method = metadataReader.GetMethodDefinition(methodHandle);
        var attributes = method.GetCustomAttributes()
            .Select(handle => GetAttributeTypeName(metadataReader, handle))
            .Where(static name => !string.IsNullOrEmpty(name))
            .ToArray();

        Assert.Contains(attributeTypeName, attributes);
    }

    private static string? GetAttributeTypeName(MetadataReader metadataReader, CustomAttributeHandle attributeHandle)
    {
        var attribute = metadataReader.GetCustomAttribute(attributeHandle);
        EntityHandle constructorHandle = attribute.Constructor;

        return constructorHandle.Kind switch
        {
            HandleKind.MemberReference => GetMemberReferenceContainingTypeName(
                metadataReader,
                (MemberReferenceHandle)constructorHandle),
            HandleKind.MethodDefinition => GetMethodDefinitionContainingTypeName(
                metadataReader,
                (MethodDefinitionHandle)constructorHandle),
            _ => null
        };
    }

    private static string? GetMemberReferenceContainingTypeName(
        MetadataReader metadataReader,
        MemberReferenceHandle memberReferenceHandle)
    {
        var memberReference = metadataReader.GetMemberReference(memberReferenceHandle);
        return memberReference.Parent.Kind switch
        {
            HandleKind.TypeReference => metadataReader.GetString(
                metadataReader.GetTypeReference((TypeReferenceHandle)memberReference.Parent).Name),
            HandleKind.TypeDefinition => metadataReader.GetString(
                metadataReader.GetTypeDefinition((TypeDefinitionHandle)memberReference.Parent).Name),
            _ => null
        };
    }

    private static string GetMethodDefinitionContainingTypeName(
        MetadataReader metadataReader,
        MethodDefinitionHandle methodDefinitionHandle)
    {
        var method = metadataReader.GetMethodDefinition(methodDefinitionHandle);
        var type = metadataReader.GetTypeDefinition(method.GetDeclaringType());
        return metadataReader.GetString(type.Name);
    }

    private static IReadOnlyList<string> GetMethodLocalNames(
        MetadataReader pdbReader,
        MethodDefinitionHandle methodHandle)
    {
        var names = new List<string>();

        foreach (var localScopeHandle in pdbReader.LocalScopes)
        {
            var scope = pdbReader.GetLocalScope(localScopeHandle);
            if (scope.Method != methodHandle)
                continue;

            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = pdbReader.GetLocalVariable(localHandle);
                var localName = pdbReader.GetString(local.Name);
                if (!string.IsNullOrEmpty(localName))
                    names.Add(localName);
            }
        }

        return names;
    }

    private static void AssertPdbHasNoOverlappingVisibleSequencePointsForDocument(
        string assemblyPath,
        string pdbPath,
        string sourcePath)
    {
        using var peReader = new PEReader(File.OpenRead(assemblyPath));
        var metadataReader = peReader.GetMetadataReader();

        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
        var pdbReader = pdbProvider.GetMetadataReader();

        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        var overlaps = new List<string>();

        foreach (var methodHandle in metadataReader.MethodDefinitions)
        {
            var method = metadataReader.GetMethodDefinition(methodHandle);
            var type = metadataReader.GetTypeDefinition(method.GetDeclaringType());
            var methodName = $"{metadataReader.GetString(type.Name)}.{metadataReader.GetString(method.Name)}";

            var points = GetVisibleSequencePoints(pdbReader, methodHandle)
                .Where(p => !p.Document.IsNil)
                .Where(p => string.Equals(
                    Path.GetFullPath(pdbReader.GetString(pdbReader.GetDocument(p.Document).Name)),
                    normalizedSourcePath,
                    StringComparison.Ordinal))
                .OrderBy(static p => p.StartLine)
                .ThenBy(static p => p.StartColumn)
                .ThenBy(static p => p.EndLine)
                .ThenBy(static p => p.EndColumn)
                .ToArray();

            for (var i = 1; i < points.Length; i++)
            {
                var previous = points[i - 1];
                var current = points[i];
                if (!StartsBefore(current.StartLine, current.StartColumn, previous.EndLine, previous.EndColumn))
                    continue;

                overlaps.Add(
                    $"{methodName}: {previous.StartLine}:{previous.StartColumn}-{previous.EndLine}:{previous.EndColumn} overlaps " +
                    $"{current.StartLine}:{current.StartColumn}-{current.EndLine}:{current.EndColumn}");
            }
        }

        Assert.True(overlaps.Count == 0, "Unexpected overlapping sequence points:" + Environment.NewLine + string.Join(Environment.NewLine, overlaps));
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCompiler(
        string repoRoot,
        string projectPath,
        string outputDirectory,
        string targetFramework)
    {
        var compilerProjectPath = Path.Combine(repoRoot, "src", "Raven.Compiler", "Raven.Compiler.csproj");
        var args =
            $"run --no-build --no-restore --framework {targetFramework} --project \"{compilerProjectPath}\" --property WarningLevel=0 -- " +
            $"\"{projectPath}\" -o \"{outputDirectory}\" --framework {targetFramework}";

        var startInfo = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start compiler process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        const int timeoutMilliseconds = 180_000;

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore teardown failures.
            }

            var timedOutStdOut = stdoutTask.GetAwaiter().GetResult();
            var timedOutStdErr = stderrTask.GetAwaiter().GetResult();
            return (-1, timedOutStdOut, $"{timedOutStdErr}{Environment.NewLine}Timed out after {timeoutMilliseconds}ms.");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static void AssertNoMethod(MetadataReader metadataReader, Func<string, string, bool> predicate)
    {
        foreach (var typeHandle in metadataReader.TypeDefinitions)
        {
            var type = metadataReader.GetTypeDefinition(typeHandle);
            var typeName = metadataReader.GetString(type.Name);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadataReader.GetMethodDefinition(methodHandle);
                var methodName = metadataReader.GetString(method.Name);
                if (predicate(typeName, methodName))
                    Assert.True(false, $"Unexpected method found: {typeName}.{methodName}");
            }
        }
    }
}
