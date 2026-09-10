using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Semantics.Tests;

public class MatchExpressionTests : DiagnosticTestBase
{
    [Fact]
    public void MatchExpression_IncompleteSuffix_DiagnosticsDoNotThrow()
    {
        const string code = """
let v = 1
let r = v match
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "incomplete_match_expression_diagnostics",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();

        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void MatchExpression_InValuePosition_BindsDirectlyAsBoundMatchExpression()
    {
        const string code = """
let result = match 1 {
    1 => 10
    _ => 0
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_bound_shape",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));
        Assert.Equal(2, bound.Arms.Length);
    }

    [Fact]
    public void PostfixMatchExpression_InValuePosition_BindsDirectlyAsBoundMatchExpression()
    {
        const string code = """
let result = 1 match {
    1 => 10
    _ => 0
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "postfix_match_expression_bound_shape",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<PostfixMatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));
        Assert.Equal(2, bound.Arms.Length);
    }

    [Fact]
    public void MatchExpression_WithTypeArms_MissingDefaultReportsDiagnostic()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    string text => text
    object obj => obj.ToString()
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_WithOpenGenericDeclarationPattern_InfersTypeArgumentsFromScrutinee()
    {
        const string code = """
class Box<T> {}

let value: Box<int> = Box<int>()

let result = match value {
    Box box => 1
}
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();

        Assert.Empty(run.UnexpectedDiagnostics);
        Assert.Empty(run.MissingDiagnostics);
        Assert.DoesNotContain(
            run.Compilation.GetDiagnostics(),
            d => d.Descriptor == CompilerDiagnostics.TypeRequiresTypeArguments);

        var tree = run.Compilation.SyntaxTrees.First(tree => tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Any());
        var model = run.Compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var declaration = Assert.IsType<BoundDeclarationPattern>(bound.Arms[0].Pattern);
        var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);

        Assert.Equal("Box<int>", declaration.DeclaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        Assert.Equal("Box<int>", designator.Local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    [Fact]
    public void MatchExpression_WithDefaultArm_AllowsAssignment()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    string text => text
    object => value.ToString()
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDictionaryPattern_BindsEntries()
    {
        const string code = """
import System.Collections.Generic.*

let values: Dictionary<string, int> = !["a": 1, "b": 2]

let result = match values {
    ["a": let first, "b": 2] => first
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();

        Assert.Empty(run.UnexpectedDiagnostics);
        Assert.Empty(run.MissingDiagnostics);

        var tree = run.Compilation.SyntaxTrees.First(tree => tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Any());
        var model = run.Compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var dictionaryPattern = Assert.IsType<BoundDictionaryPattern>(bound.Arms[0].Pattern);
        Assert.Equal(2, dictionaryPattern.Entries.Length);
        Assert.Equal(SpecialType.System_String, dictionaryPattern.KeyType.SpecialType);
        Assert.Equal(SpecialType.System_Int32, dictionaryPattern.ValueType.SpecialType);

        var firstPattern = Assert.IsType<BoundDeclarationPattern>(dictionaryPattern.Entries[0].Pattern);
        var firstDesignator = Assert.IsType<BoundSingleVariableDesignator>(firstPattern.Designator);
        Assert.Equal("first", firstDesignator.Local.Name);
        Assert.Equal(SpecialType.System_Int32, firstDesignator.Local.Type.SpecialType);

        Assert.IsType<BoundConstantPattern>(dictionaryPattern.Entries[1].Pattern);
    }

    [Fact]
    public void MatchExpression_WithDictionaryPatternOnNonDictionaryType_ReportsDictionaryDiagnostic()
    {
        const string code = """
let value = 42

let result = match value {
    ["a": 1] => 1
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();
        var diagnostics = run.Compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.DictionaryPatternRequiresDictionaryType);
    }

    [Fact]
    public void MatchExpression_WithDuplicateDictionaryPatternKeys_ReportsDuplicateKey()
    {
        const string code = """
import System.Collections.Generic.*

let values: Dictionary<string, int> = !["a": 1]

let result = match values {
    ["a": let first, "a": 1] => first
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();
        var diagnostics = run.Compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.DuplicateDictionaryKey);
    }

    [Fact]
    public void MatchExpression_WithDuplicatePropertyPatternMembers_ReportsDiagnostic()
    {
        const string code = """
class Box {
    val Value: int
}

let value = Box(Value: 1)

let result = match value {
    Box { Value: 1, Value: let other } => other
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();
        var diagnostics = run.Compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.DuplicatePropertyPatternMember);
    }

    [Fact]
    public void MatchExpression_WithUnknownNamedNominalDeconstructionMember_ReportsDiagnostic()
    {
        const string code = """
let value: object = Person("Ada", 42)

let result = match value {
    Person(Height: 170, Name: let name) => name
    _ => ""
}

record class Person(Name: string, Age: int)
""";

        var verifier = CreateVerifier(code);
        var run = verifier.GetResult();
        var diagnostics = run.Compilation.GetDiagnostics();

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.PropertyPatternMemberNotFound);
    }

    [Fact]
    public void MatchExpression_WithUndefinedNestedNominalDeconstructionPattern_ReportsInvalidArmPattern()
    {
        const string code = """
union class UserOrError {
    case Ok(value: int)
    case Error(error: string)
}

func GetUser() -> UserOrError {
    return .Ok(1)
}

let result = match GetUser() {
    .Ok(User(let name, let isActive)) => 1
    .Error(let error) => 0
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult(CompilerDiagnostics.TheNameDoesNotExistInTheCurrentContext.Id)
                    .WithAnySpan()
                    .WithArguments("User"),
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmPatternInvalid.Id)
                    .WithAnySpan()
                    .WithArguments("for type 'User'", "int"),
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithMismatchedNestedNominalDeconstructionPattern_ReportsTypeMismatch()
    {
        const string code = """
union class UserOrError {
    case Ok(value: int)
    case Error(error: string)
}

func GetUser() -> UserOrError {
    return .Ok(1)
}

let result = match GetUser() {
    .Ok(User(let name, let isActive)) => 1
    .Error(let error) => 0
}

record class User(Name: string, IsActive: bool);
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionNotExhaustive.Id)
                    .WithAnySpan()
                    .WithArguments("Ok"),
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmPatternInvalid.Id)
                    .WithAnySpan()
                    .WithArguments("for type 'User'", "int"),
                new DiagnosticResult(CompilerDiagnostics.NominalDeconstructionPatternTypeMismatch.Id)
                    .WithAnySpan()
                    .WithArguments("int", "User"),
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithBooleanLiteralArms_IsExhaustive()
    {
        const string code = """
let value: bool = true

let result = match value {
    true => "true"
    false => "false"
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithConstantTrueGuardedCatchAll_IsExhaustive()
    {
        const string code = """
func Describe(value: bool) -> string {
    return match value {
        _ when true => "Boolean"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithConstantFalseGuardedCatchAll_IsNotExhaustive()
    {
        const string code = """
func Describe(value: bool) -> string {
    return match value {
        _ when false => "Boolean"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "true");
    }

    [Fact]
    public void MatchExpression_WithUnitPattern_IsExhaustive()
    {
        const string code = """
func Describe(value: unit) -> string {
    return match value {
        () => "Unit"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullLiteralPatternOnNullExpression_IsExhaustive()
    {
        const string code = """
func Describe() -> string {
    return match null {
        null => "Null"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithBooleanLiteralArmsOnUnion_IsExhaustive()
    {
        const string code = """
union class Value {
    case Flag(value: bool)
    case Pair(flag: bool, text: string)
}

let value: Value = .Flag(value: false)

let result = match value {
    .Flag(let flag) => if flag { "true" } else { "false" }
    .Pair(let flag, let text) => "tuple ${text}"
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithBarSeparatedConstantPatterns_BindsAsAlternative()
    {
        const string code = """
func ping(name: string) -> string {
    return match name {
        "Bob" | "bob" => "pong"
        _ => "invalid"
    }
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithNegativeNumericPattern_AllowsConstantArm()
    {
        const string code = """
let value: int = -1

let result = match value {
    -1 => "minus one"
    _ => "other"
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscardArm_BindsDesignation()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    string text => text
    _ => ""
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscardArmOnNewLine_DoesNotInsertEmptyArm()
    {
        const string code = """
let result = match false {
    _ => "none"
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithTargetTypedMemberPattern_ResolvesAgainstInputType()
    {
        const string code = """
enum Species {
    Human,
    Dog
}

class Character(name: string, species: Species, age: int) {
    val Name: string {
        get { return name }
    }

    val Species: Species {
        get { return species }
    }

    val Age: int {
        get { return age }
    }
}

let character = Character("Rex", .Dog, 4)

let result = match character {
    { Age: not > 34, Species: .Dog } => true
    _ => false
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithTargetTypedSealedHierarchyCasePatterns_ResolvesAgainstInputType()
    {
        const string code = """
sealed interface Expr<T> {
    record NumericalExpr(Value: float) : Expr<float>
    record AddExpr(Left: Expr<float>, Right: Expr<float>) : Expr<float>
}

func Main() {
}

func Evaluate(expr: Expr<float>) -> int {
    match expr {
        .NumericalExpr(let value) => 1
        .AddExpr(let left, let right) => 2
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "sealed_hierarchy_target_typed_member_pattern",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchStatementSyntax>().Single();
        Assert.Equal(2, match.Arms.Count);

        var model = compilation.GetSemanticModel(tree);
        var valueDesignation = tree.GetRoot()
            .DescendantNodes()
            .OfType<SingleVariableDesignationSyntax>()
            .Single(designation => designation.Identifier.ValueText == "value");
        var valueLocal = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(valueDesignation));

        Assert.Equal(SpecialType.System_Single, valueLocal.Type.SpecialType);
    }

    [Fact]
    public void MatchExpression_WithConstrainedGenericSealedHierarchyCases_BindsAgainstScrutineeTypeArguments()
    {
        const string code = """
import System.Numerics.*

sealed interface Expr<T>
    where T: INumber<T> {
    record Literal<T>(Value: T) : Expr<T>
        where T: INumber<T>

    record Add<T>(Left: Expr<T>, Right: Expr<T>) : Expr<T>
        where T: INumber<T>
}

func Evaluate<T>(expr: Expr<T>) -> T
    where T: INumber<T> {
    return match expr {
        .Literal(let value) => value
        .Add(let left, let right) => Evaluate(left) + Evaluate(right)
    }
}

func Main() {
    let expr = Expr.Add<int>(Expr.Literal<int>(40), Expr.Literal<int>(2))
    Evaluate(expr)
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "sealed_hierarchy_constrained_generic_cases",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void MatchExpression_WithEnumArms_MissingCaseReportsDiagnostic()
    {
        const string code = """
enum Color {
    Red,
    Green,
    Blue
}

let value: Color = .Red

let result = match value {
    .Red => 1
    .Green => 2
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Blue")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithAllSourceEnumMembers_IsExhaustive()
    {
        const string code = """
enum Color {
    Red
    Green
    Blue
}

func Describe(color: Color) -> string {
    return match color {
        .Red => "red"
        .Green => "green"
        .Blue => "blue"
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_closed_source_enum",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor.Id == "RAV2100");
    }

    [Fact]
    public void MatchExpression_AddingEnumMemberInvalidatesIncrementalExhaustiveness()
    {
        const string source = """
            enum Color {
                Red
                Green
            }

            func Describe(color: Color) -> string {
                return match color {
                    .Red => "red"
                    .Green => "green"
                }
            }
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "incremental-enum-exhaustiveness",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "colors.rav",
            SourceText.From(source),
            "/tmp/colors.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single();
        var initialMatch = initialTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(initialCompilation.GetSemanticModel(initialTree).GetMatchExhaustiveness(initialMatch).IsExhaustive);
        Assert.DoesNotContain(
            initialCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSource = source.Replace(
            "    Green\n}",
            "    Green\n    Blue\n}",
            System.StringComparison.Ordinal);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(updatedSource)));

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single();
        var updatedMatch = updatedTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var updatedInfo = updatedCompilation.GetSemanticModel(updatedTree).GetMatchExhaustiveness(updatedMatch);

        Assert.False(updatedInfo.IsExhaustive);
        Assert.Contains("Blue", updatedInfo.MissingCases);
        Assert.Contains(
            updatedCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));

        var restoredCompilation = workspace.GetCompilation(projectId);
        var restoredTree = restoredCompilation.SyntaxTrees.Single();
        var restoredMatch = restoredTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(restoredCompilation.GetSemanticModel(restoredTree).GetMatchExhaustiveness(restoredMatch).IsExhaustive);
        Assert.DoesNotContain(
            restoredCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);
    }

    [Fact]
    public void MatchExpression_AddingUnionCaseInvalidatesIncrementalExhaustiveness()
    {
        const string source = """
            union class State {
                case On
                case Off
            }

            func Describe(state: State) -> int {
                return match state {
                    .On => 1
                    .Off => 0
                }
            }
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "incremental-union-exhaustiveness",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "states.rav",
            SourceText.From(source),
            "/tmp/states.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single();
        var initialMatch = initialTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(initialCompilation.GetSemanticModel(initialTree).GetMatchExhaustiveness(initialMatch).IsExhaustive);
        Assert.DoesNotContain(
            initialCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSource = source.Replace(
            "    case Off\n}",
            "    case Off\n    case Unknown\n}",
            System.StringComparison.Ordinal);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(updatedSource)));

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single();
        var updatedMatch = updatedTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var updatedInfo = updatedCompilation.GetSemanticModel(updatedTree).GetMatchExhaustiveness(updatedMatch);

        Assert.False(updatedInfo.IsExhaustive);
        Assert.Contains("Unknown", updatedInfo.MissingCases);
        Assert.Contains(
            updatedCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));

        var restoredCompilation = workspace.GetCompilation(projectId);
        var restoredTree = restoredCompilation.SyntaxTrees.Single();
        var restoredMatch = restoredTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(restoredCompilation.GetSemanticModel(restoredTree).GetMatchExhaustiveness(restoredMatch).IsExhaustive);
        Assert.DoesNotContain(
            restoredCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);
    }

    [Fact]
    public void MatchExpression_AddingPermittedSubtypeInvalidatesIncrementalExhaustiveness()
    {
        const string source = """
            sealed class Expr permits Lit {}
            class Lit : Expr {}

            func Evaluate(expr: Expr) -> int {
                return match expr {
                    Lit lit => 1
                }
            }
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "incremental-sealed-hierarchy-exhaustiveness",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "expressions.rav",
            SourceText.From(source),
            "/tmp/expressions.rav").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single();
        var initialMatch = initialTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(initialCompilation.GetSemanticModel(initialTree).GetMatchExhaustiveness(initialMatch).IsExhaustive);
        Assert.DoesNotContain(
            initialCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSource = source
            .Replace("permits Lit", "permits Lit, Add", System.StringComparison.Ordinal)
            .Replace("class Lit : Expr {}", "class Lit : Expr {}\nclass Add : Expr {}", System.StringComparison.Ordinal);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(updatedSource)));

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single();
        var updatedMatch = updatedTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var updatedInfo = updatedCompilation.GetSemanticModel(updatedTree).GetMatchExhaustiveness(updatedMatch);

        Assert.False(updatedInfo.IsExhaustive);
        Assert.Contains("Add", updatedInfo.MissingCases);
        Assert.Contains(
            updatedCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));

        var restoredCompilation = workspace.GetCompilation(projectId);
        var restoredTree = restoredCompilation.SyntaxTrees.Single();
        var restoredMatch = restoredTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(restoredCompilation.GetSemanticModel(restoredTree).GetMatchExhaustiveness(restoredMatch).IsExhaustive);
        Assert.DoesNotContain(
            restoredCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);
    }

    [Fact]
    public void NullableMatchExpression_AddingPermittedSubtypeInvalidatesIncrementalExhaustiveness()
    {
        const string source = """
            sealed class BaseClass permits SubClassA {}
            class SubClassA : BaseClass {}

            func Describe(value: BaseClass?) -> string {
                return match value {
                    SubClassA a => "A"
                    null => ""
                }
            }
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "incremental-nullable-sealed-hierarchy-exhaustiveness",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;

        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);

        project = project.AddDocument(
            "hierarchy.rvn",
            SourceText.From(source),
            "/tmp/hierarchy.rvn").Project;
        workspace.TryApplyChanges(project.Solution);

        var initialCompilation = workspace.GetCompilation(projectId);
        var initialTree = initialCompilation.SyntaxTrees.Single();
        var initialMatch = initialTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.DoesNotContain(
            initialCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);
        Assert.True(initialCompilation.GetSemanticModel(initialTree).GetMatchExhaustiveness(initialMatch).IsExhaustive);

        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        var updatedSource = source
            .Replace("permits SubClassA", "permits SubClassA, SubClassB", StringComparison.Ordinal)
            .Replace("class SubClassA : BaseClass {}", "class SubClassA : BaseClass {}\nclass SubClassB : BaseClass {}", StringComparison.Ordinal);
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(updatedSource)));

        var updatedCompilation = workspace.GetCompilation(projectId);
        var updatedTree = updatedCompilation.SyntaxTrees.Single();
        var updatedMatch = updatedTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var updatedInfo = updatedCompilation.GetSemanticModel(updatedTree).GetMatchExhaustiveness(updatedMatch);
        var updatedDiagnostics = updatedCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();

        Assert.False(updatedInfo.IsExhaustive);
        Assert.Equal("SubClassB", Assert.Single(updatedInfo.MissingCases));
        Assert.Contains("'SubClassB'", Assert.Single(updatedDiagnostics).GetMessage(), StringComparison.Ordinal);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));

        var restoredCompilation = workspace.GetCompilation(projectId);
        var restoredTree = restoredCompilation.SyntaxTrees.Single();
        var restoredMatch = restoredTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.True(restoredCompilation.GetSemanticModel(restoredTree).GetMatchExhaustiveness(restoredMatch).IsExhaustive);
        Assert.DoesNotContain(
            restoredCompilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableMatchExpression_MissingArmExpressionRecoversAcrossWorkspaceEdit(bool diagnosticsFirst)
    {
        const string source = """
            func Stable() -> int => 42

            func Describe(value: string?) -> int {
                return match value {
                    string text => text.Length
                    null => 0
                }
            }

            func Main() -> int => Stable()
            """;

        var workspace = RavenWorkspace.Create(targetFramework: TestMetadataReferences.TargetFramework);
        var projectId = workspace.AddProject(
            "incremental-nullable-match-recovery",
            compilationOptions: new CompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            targetFramework: TestMetadataReferences.TargetFramework);
        var project = workspace.CurrentSolution.GetProject(projectId)!;
        foreach (var reference in TestMetadataReferences.Default)
            project = project.AddMetadataReference(reference);
        project = project.AddDocument("main.rvn", SourceText.From(source), "/tmp/main.rvn").Project;
        workspace.TryApplyChanges(project.Solution);

        AssertSnapshot(source, expectErrors: false);

        var brokenSource = source.Replace("null => 0", "null =>", StringComparison.Ordinal);
        var document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(brokenSource)));
        AssertSnapshot(brokenSource, expectErrors: true);

        document = workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(
            document.Id,
            SourceText.From(source)));
        AssertSnapshot(source, expectErrors: false);

        void AssertSnapshot(string expectedText, bool expectErrors)
        {
            var compilation = workspace.GetCompilation(projectId);
            var tree = Assert.Single(compilation.SyntaxTrees);
            Assert.Equal(expectedText, tree.GetText()!.ToString());
            var model = compilation.GetSemanticModel(tree);
            if (diagnosticsFirst)
                _ = compilation.GetDiagnostics();

            var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
            var stableInvocation = tree.GetRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(invocation => invocation.Expression.ToString() == "Stable");
            var stable = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(stableInvocation).Symbol);
            var exhaustiveness = model.GetMatchExhaustiveness(match);
            var diagnostics = compilation.GetDiagnostics();

            Assert.Equal("Stable", stable.Name);
            if (!expectErrors)
                Assert.True(exhaustiveness.IsExhaustive);
            Assert.Equal(expectErrors, diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Fact]
    public void MatchExpression_WithEnumArms_MissingCase_ReportsDiagnostic()
    {
        const string code = """
class Program {
    func eval(color: Color) -> int {
        return match color {
            .Red => 1
        }
    }
}

enum Color {
    Red
    Blue
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_enum_missing_case_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        Assert.Contains(compilation.GetDiagnostics(), d => d.Descriptor.Id == "RAV2100");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchExpression_ExhaustivenessIsStableAcrossSemanticQueryOrder(int firstQuery)
    {
        const string code = """
            enum Color {
                Red
                Blue
            }

            func Describe(color: Color) -> int {
                return match color {
                    .Red => 1
                }
            }
            """;
        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_query_order",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        switch (firstQuery)
        {
            case 0:
                _ = compilation.GetDiagnostics();
                break;
            case 1:
                _ = model.GetTypeInfo(match);
                break;
            case 2:
                _ = model.GetSymbolInfo(match.Expression);
                break;
            case 3:
                _ = model.GetMatchExhaustiveness(match);
                break;
        }

        var diagnostics = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();
        var info = model.GetMatchExhaustiveness(match);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Blue'", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.False(info.IsExhaustive);
        Assert.Equal("Blue", Assert.Single(info.MissingCases));
        Assert.Equal(SpecialType.System_Int32, model.GetTypeInfo(match).Type?.SpecialType);
    }

    [Fact]
    public void MatchExpression_WithTypedDiscardArm_IsCatchAll()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    string text => text
    object _ => value.ToString()
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_InReturnContext_TargetTypesArmMemberBindings()
    {
        const string code = """
enum PingStatus {
    Ok,
    Error
}

func ping(name: string) -> PingStatus {
    return match name {
        "Bob" | "bob" => .Ok
        _ => .Error
    }
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithPositionalPatternOnUnion_BindsElementDesignations()
    {
        const string code = """
union class Value {
    case Bool(flag: bool)
    case Pair(a: int, b: string)
}

let x: Value = .Bool(flag: false)

let result = match x {
    .Bool(let flag) => "hej"
    .Pair(let a, let b) => "tuple ${a} ${b}"
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "tuple_union_match",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        Assert.Equal(2, bound.Arms.Length);
    }

    [Fact]
    public void MatchExpression_WithAbruptArms_DoesNotPolluteValueTypeInference()
    {
        const string code = """
import System.*

func Test(y: int) -> int {
    let r = match y {
        0 => return 0
        1 => 42
        _ => throw Exception("x")
    }

    return r + 1
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternOnArray_BindsElementDesignations()
    {
        const string code = """
let items: int[] = [1, 2]

let result = match items {
    [let first, let second] => first + second
    _ => 0
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "array_collection_match",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal("int[]", collectionPattern.Type.ToDisplayStringKeywordAware(SymbolDisplayFormat.MinimallyQualifiedFormat));

        Assert.Collection(collectionPattern.Elements,
            element =>
            {
                var declaration = Assert.IsType<BoundDeclarationPattern>(element);
                var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);
                Assert.Equal("first", designator.Local.Name);
            },
            element =>
            {
                var declaration = Assert.IsType<BoundDeclarationPattern>(element);
                var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);
                Assert.Equal("second", designator.Local.Name);
            });
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternRestOnArray_BindsRestDesignation()
    {
        const string code = """
let items: int[] = [1, 2, 3, 4]

let result = match items {
    [let first, ..let middle, let last] => first + middle[0] + last
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "array_collection_match_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(1, collectionPattern.RestIndex);

        var restPattern = Assert.IsType<BoundDeclarationPattern>(collectionPattern.Elements[1]);
        var restDesignator = Assert.IsType<BoundSingleVariableDesignator>(restPattern.Designator);
        Assert.Equal("middle", restDesignator.Local.Name);
        Assert.True(restDesignator.Local.Type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Int32 });
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternRestOnList_PreservesRestDesignationType()
    {
        const string code = """
import System.Collections.Generic.*

let items: List<int> = [1, 2, 3, 4]

let result = match items {
    [let first, ..let middle, let last] => first + middle[0] + last
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "list_collection_match_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(1, collectionPattern.RestIndex);

        var restPattern = Assert.IsType<BoundDeclarationPattern>(collectionPattern.Elements[1]);
        var restDesignator = Assert.IsType<BoundSingleVariableDesignator>(restPattern.Designator);
        Assert.Equal("middle", restDesignator.Local.Name);
        Assert.Equal("System.Collections.Generic.List`1", ((INamedTypeSymbol)restDesignator.Local.Type).OriginalDefinition.ToFullyQualifiedMetadataName());
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternRestOnImmutableList_PreservesRestDesignationType()
    {
        const string code = """
import System.Collections.Immutable.*

let items: ImmutableList<int> = [1, 2, 3, 4]

let result = match items {
    [let first, ..let middle, let last] => first + middle[0] + last
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "immutable_list_collection_match_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(1, collectionPattern.RestIndex);

        var restPattern = Assert.IsType<BoundDeclarationPattern>(collectionPattern.Elements[1]);
        var restDesignator = Assert.IsType<BoundSingleVariableDesignator>(restPattern.Designator);
        Assert.Equal("middle", restDesignator.Local.Name);
        Assert.Equal("System.Collections.Immutable.ImmutableList`1", ((INamedTypeSymbol)restDesignator.Local.Type).OriginalDefinition.ToFullyQualifiedMetadataName());
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternRestOnFixedArray_BindsFixedSizeRestDesignation()
    {
        const string code = """
let items: int[4] = [1, 2, 3, 4]

let result = match items {
    [let first, let second, ...let rest] => first + second + rest.Length
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "fixed_array_collection_match_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        var restPattern = Assert.IsType<BoundDeclarationPattern>(collectionPattern.Elements[2]);
        var restDesignator = Assert.IsType<BoundSingleVariableDesignator>(restPattern.Designator);
        Assert.Equal("rest", restDesignator.Local.Name);
        Assert.True(restDesignator.Local.Type is IArrayTypeSymbol
        {
            ElementType.SpecialType: SpecialType.System_Int32,
            FixedLength: 2
        });
    }

    [Fact]
    public void MatchExpression_WithTrailingTripleDotCollectionPattern_BindsDiscardRest()
    {
        const string code = """
let items: int[] = [1, 2, 3, 4]

let result = match items {
    [let first, ...] => first
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "array_collection_match_trailing_discard_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(1, collectionPattern.RestIndex);
        Assert.IsType<BoundDiscardPattern>(collectionPattern.Elements[1]);
    }

    [Fact]
    public void MatchExpression_WithRestOnlyCollectionPattern_IsExhaustive()
    {
        const string code = """
func Count(items: int[]) -> int {
    return match items {
        [...] => items.Length
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_AfterRestOnlyCollectionPattern_ReportsUnreachableArm()
    {
        const string code = """
func Count(items: int[]) -> int {
    return match items {
        [...] => items.Length
        _ => 0
    }
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmUnreachable.Id).WithAnySpan()]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithConstantTrueNestedGuardedPattern_IsExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)) -> string {
    return match pair {
        (let left when true, _) => left.ToString()
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithConstantFalseNestedGuardedPattern_IsNotExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)) -> string {
    return match pair {
        (let left when false, _) => left.ToString()
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "_");
    }

    [Fact]
    public void MatchExpression_WithMiddleTripleDotCollectionPattern_BindsDiscardRest()
    {
        const string code = """
let items: int[] = [1, 2, 3, 4]

let result = match items {
    [let first, ..., let last] => first + last
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "array_collection_match_middle_discard_rest",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(1, collectionPattern.RestIndex);
        Assert.IsType<BoundDiscardPattern>(collectionPattern.Elements[1]);
    }

    [Fact]
    public void MatchExpression_WithStringCollectionFixedSegment_BindsStringSliceDesignation()
    {
        const string code = """
let text = "rune"

let result = match text {
    [let first, ..2 let middle, let last] => middle
    _ => ""
}
""";

        var verifier = CreateVerifier(code);
        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "string_collection_match_fixed_segment",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var collectionPattern = Assert.IsType<BoundPositionalPattern>(bound.Arms[0].Pattern);
        Assert.Equal(BoundPositionalPattern.SequenceElementKind.Single, collectionPattern.ElementKinds[0]);
        Assert.Equal(BoundPositionalPattern.SequenceElementKind.FixedSegment, collectionPattern.ElementKinds[1]);
        Assert.Equal(BoundPositionalPattern.SequenceElementKind.Single, collectionPattern.ElementKinds[2]);

        var middlePattern = Assert.IsType<BoundDeclarationPattern>(collectionPattern.Elements[1]);
        var middleDesignator = Assert.IsType<BoundSingleVariableDesignator>(middlePattern.Designator);
        Assert.Equal("middle", middleDesignator.Local.Name);
        Assert.Equal(SpecialType.System_String, middleDesignator.Local.Type.SpecialType);
    }

    [Fact]
    public void MatchExpression_WithCollectionPatternOnEnumerable_ReportsDiagnostic()
    {
        const string code = """
import System.Collections.Generic.*
import System.Linq.*

let items: IEnumerable<int> = [1, 2, 3].Where(v => v > 0)

let result = match items {
    [let first, let second] => first + second
    _ => 0
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmPatternInvalid.Id)
                    .WithAnySpan()
                    .WithArguments("for a sequence pattern", "IEnumerable<int>")
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscardArmNotLast_ReportsDiagnostic()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    _ => ""
    string text => text
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmUnreachable.Id).WithAnySpan()]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithTypedDiscardArmNotLast_ReportsDiagnostic()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    object _ => value.ToString()
    string text => text
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmUnreachable.Id).WithAnySpan(),
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDuplicateCasePattern_ReportsDiagnostic()
    {
        const string code = """
union class Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}

let value: Result<int, string> = .Ok(2)

let result = match value {
    .Ok(2) => "Lucky you!"
    .Ok(2) => "Still lucky!"
    .Ok(let payload) => payload.ToString()
    .Error(let err) => err
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmUnreachable.Id).WithAnySpan()]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_DiscardArm_BindsToDiscardPattern()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    string text => text
    object obj => obj.ToString()
    _ => "None"
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "discard_match",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));
        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        Assert.IsType<BoundDiscardPattern>(bound.Arms.Last().Pattern);
    }

    [Fact]
    public void MatchExpression_WithVariablePattern_BindsDesignation()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    let text => text
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var variableArm = bound.Arms[0];
        var declaration = Assert.IsType<BoundDeclarationPattern>(variableArm.Pattern);
        var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);

        Assert.Equal("text", designator.Local.Name);
        Assert.False(designator.Local.IsMutable);
    }

    [Fact]
    public void MatchExpression_WithVarPattern_BindsMutableDesignation()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    var text => text
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var variableArm = bound.Arms[0];
        var declaration = Assert.IsType<BoundDeclarationPattern>(variableArm.Pattern);
        var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);

        Assert.Equal("text", designator.Local.Name);
        Assert.True(designator.Local.IsMutable);
    }

    [Fact]
    public void MatchExpression_WithTypedVariablePattern_UsesAnnotation()
    {
        const string code = """
let value: object = "hello"

let result = match value {
    let text: string => text
    _ => ""
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var variableArm = bound.Arms[0];
        var declaration = Assert.IsType<BoundDeclarationPattern>(variableArm.Pattern);
        var designator = Assert.IsType<BoundSingleVariableDesignator>(declaration.Designator);

        var stringType = result.Compilation.GetSpecialType(SpecialType.System_String);
        Assert.True(SymbolEqualityComparer.Default.Equals(designator.Local.Type, stringType));
    }

    [Fact]
    public void MatchExpression_WithArrayTypePattern_BindsArrayType()
    {
        const string code = """
let value: object = [1, 2, 3]

let result = match value {
    int[] numbers => numbers.Length
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var declarationPattern = Assert.IsType<BoundDeclarationPattern>(bound.Arms[0].Pattern);
        var designator = Assert.IsType<BoundSingleVariableDesignator>(declarationPattern.Designator);
        Assert.Equal("numbers", designator.Local.Name);

        var arrayType = Assert.IsAssignableFrom<IArrayTypeSymbol>(declarationPattern.Type);
        var intType = result.Compilation.GetSpecialType(SpecialType.System_Int32);
        Assert.True(SymbolEqualityComparer.Default.Equals(arrayType.ElementType, intType));
    }

    [Fact]
    public void MatchExpression_WithGuard_UsesDesignation()
    {
        const string code = """
func describe(value: object) -> string? {
    match value {
        string text when text.Length > 3 => text
        string text => text.ToUpper()
        object obj => obj.ToString()
    }
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_AllCasesCovered()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off => 0
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithStructUnionScrutinee_AllCasesCoveredIsExhaustiveForActiveValue()
    {
        const string code = """
union State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off => 0
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithStructUnionDefaultLocal_AllCasesCoveredIsSourceExhaustive()
    {
        const string code = """
union State {
    case On
    case Off
}

let state: State = default

let result = match state {
    .On => 1
    .Off => 0
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithStructUnionDefaultLocal_MissingSemanticCaseIsReported()
    {
        const string code = """
union State {
    case On
    case Off
}

let state: State = default

let result = match state {
    .On => 1
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Off")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithActiveStructUnionScrutinee_DefensiveCatchAllIsRedundant()
    {
        const string code = """
union State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off => 0
    _ => -1
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan()]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithParenthesizedStructUnionScrutinee_AllPayloadsCoveredIsExhaustiveForActiveValue()
    {
        const string code = """
union Value(int | string)

let value: Value = 1

let result = match value {
    int number => number
    string text => text.Length
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithComplementaryParenthesizedUnionTypePatterns_IsExhaustive()
    {
        const string code = """
union Value(int | string)

func Describe(value: Value) -> string {
    return match value {
        int => "Number"
        not int => "Text"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithParenthesizedUnionTypeIntersectionAndComplement_IsExhaustive()
    {
        const string code = """
union Value(int | string)

func Describe(value: Value) -> string {
    return match value {
        int and not string => "Number"
        not int => "Text"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithLiteralAndComplementCoveringParenthesizedUnion_IsExhaustive()
    {
        const string code = """
union Value(bool | string)

func Describe(value: Value) -> string {
    return match value {
        true => "True"
        not true => "Everything else"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullAndNonNullComplement_IsExhaustive()
    {
        const string code = """
func Describe(value: string?) -> string {
    return match value {
        null => "Missing"
        not null => "Present"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithParenthesizedStructUnionDefaultLocal_AllPayloadsCoveredIsSourceExhaustive()
    {
        const string code = """
union Value(int | string)

let value: Value = default

let result = match value {
    int number => number
    string text => text.Length
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithParenthesizedStructUnionDefaultLocal_MissingPayloadIsReported()
    {
        const string code = """
union Value(int | string)

let value: Value = default

let result = match value {
    int number => number
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("string")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithParenthesizedStructUnionDefaultLocal_CatchAllForDefaultIsNotRedundant()
    {
        const string code = """
union Value(int | string)

let value: Value = default

let result = match value {
    int number => number
    string text => text.Length
    _ => -1
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithActiveParenthesizedStructUnionScrutinee_DefensiveCatchAllIsRedundant()
    {
        const string code = """
union Value(int | string)

let value: Value = 1

let result = match value {
    int number => number
    string text => text.Length
    _ => -1
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan()]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithRavenCoreUnionScrutinee_AllPayloadsCoveredIsExhaustiveForActiveValue()
    {
        const string code = """
import System.*

let value: Union<int, string> = 1

let result = match value {
    int number => number
    string text => text.Length
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var unionReference = TestMetadataFactory.CreateFromSource(
            """
namespace System

public union Union<T1, T2>(T1 | T2)
""",
            assemblyName: "raven-core-union-match-fixture");

        var compilation = Compilation.Create(
            "raven_core_union_match_exhaustiveness",
            [syntaxTree],
            [.. TestMetadataReferences.Default, unionReference],
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Descriptor.Id == "RAV2100");

        var model = compilation.GetSemanticModel(syntaxTree);
        var match = syntaxTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = model.GetMatchExhaustiveness(match);

        Assert.True(info.IsExhaustive);
        Assert.Empty(info.MissingCases);
    }

    [Fact]
    public void MatchExpression_WithRavenCoreUnionDefaultLocal_AllPayloadsCoveredIsSourceExhaustive()
    {
        const string code = """
import System.*

let value: Union<int, string> = default

let result = match value {
    int number => number
    string text => text.Length
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var unionReference = TestMetadataFactory.CreateFromSource(
            """
namespace System

public union Union<T1, T2>(T1 | T2)
""",
            assemblyName: "raven-core-union-default-match-fixture");

        var compilation = Compilation.Create(
            "raven_core_union_default_match_exhaustiveness",
            [syntaxTree],
            [.. TestMetadataReferences.Default, unionReference],
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Descriptor.Id == "RAV2100");

        var model = compilation.GetSemanticModel(syntaxTree);
        var match = syntaxTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = model.GetMatchExhaustiveness(match);

        Assert.True(info.IsExhaustive);
        Assert.Empty(info.MissingCases);
    }

    [Fact]
    public void MatchExpression_WithNullArm_BindsToConstantPattern()
    {
        const string code = """
let value: string? = null

let result = match value {
    null => "empty"
    string text => text
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_null_arm",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var constantPattern = Assert.IsType<BoundConstantPattern>(bound.Arms.First().Pattern);
        Assert.Null(constantPattern.ConstantValue);
    }

    [Fact]
    public void MatchExpression_WithNullableScalarTypeAndNullArms_IsExhaustive()
    {
        const string code = """
func Describe(value: string?) -> string {
    return match value {
        string text => text
        null => ""
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var designation = tree.GetRoot().DescendantNodes().OfType<SingleVariableDesignationSyntax>().Single();
        var local = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(designation));
        var info = model.GetMatchExhaustiveness(match);

        Assert.Equal(SpecialType.System_String, local.Type.SpecialType);
        Assert.False(local.Type.IsNullable);
        Assert.True(info.IsExhaustive);
        Assert.Empty(info.MissingCases);
    }

    [Fact]
    public void MatchExpression_WithNullableClosedHierarchyCasesAndNull_IsExhaustive()
    {
        const string code = """
sealed class BaseClass permits SubClassA, SubClassB {}
class SubClassA : BaseClass {}
class SubClassB : BaseClass {}

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
        SubClassB b => "B"
        null => ""
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithTypeParameterConstrainedToClosedHierarchy_IsExhaustive()
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
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullableClosedHierarchyMissingSubtypeAndNull_ReportsBothCases()
    {
        const string code = """
sealed class BaseClass permits SubClassA, SubClassB {}
class SubClassA : BaseClass {}
class SubClassB : BaseClass {}

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "nullable_closed_hierarchy_missing_cases",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(tree).GetMatchExhaustiveness(match);

        Assert.False(info.IsExhaustive);
        Assert.Collection(
            info.MissingCases,
            missing => Assert.Equal("SubClassB", missing),
            missing => Assert.Equal("null", missing));
        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("'SubClassB'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("'null'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableClosedHierarchyExhaustiveness_MatchesSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
namespace HierarchyLibrary {
    public sealed class BaseClass permits SubClassA, SubClassB {}
    public class SubClassA : BaseClass {}
    public class SubClassB : BaseClass {}
}
""";
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText("""
import HierarchyLibrary.*

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
        SubClassB b => "B"
        null => ""
    }
}
""");
        MetadataReference[] references = useMetadata
            ? [.. TestMetadataReferences.Default,
                TestMetadataFactory.CreateFromSource(librarySource, "nullable_hierarchy_library")]
            : TestMetadataReferences.Default;
        var trees = useMetadata ? new[] { consumerTree } : [libraryTree, consumerTree];
        var compilation = Compilation.Create(
            "nullable_hierarchy_consumer",
            trees,
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var diagnostics = diagnosticsFirst ? compilation.GetDiagnostics() : default;
        var match = consumerTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var model = compilation.GetSemanticModel(consumerTree);
        var info = model.GetMatchExhaustiveness(match);
        var scrutineeInfo = model.GetTypeInfo(match.Expression);
        var designations = consumerTree.GetRoot()
            .DescendantNodes()
            .OfType<SingleVariableDesignationSyntax>()
            .ToArray();
        var locals = designations
            .Select(designation => Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(designation)))
            .ToArray();
        if (!diagnosticsFirst)
            diagnostics = compilation.GetDiagnostics();

        Assert.True(info.IsExhaustive);
        Assert.Empty(info.MissingCases);
        var nullableScrutinee = Assert.IsType<NullableTypeSymbol>(scrutineeInfo.Type);
        Assert.Equal("BaseClass", nullableScrutinee.UnderlyingType.Name);
        Assert.Collection(
            locals,
            local =>
            {
                Assert.Equal("SubClassA", local.Type.Name);
                Assert.False(local.Type.IsNullable);
            },
            local =>
            {
                Assert.Equal("SubClassB", local.Type.Name);
                Assert.False(local.Type.IsNullable);
            });
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableClosedHierarchyMissingCases_MatchSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
namespace HierarchyLibrary {
    public sealed class BaseClass permits SubClassA, SubClassB {}
    public class SubClassA : BaseClass {}
    public class SubClassB : BaseClass {}
}
""";
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText("""
import HierarchyLibrary.*

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
    }
}
""");
        MetadataReference[] references = useMetadata
            ? [.. TestMetadataReferences.Default,
                TestMetadataFactory.CreateFromSource(librarySource, "nullable_hierarchy_missing_library")]
            : TestMetadataReferences.Default;
        var trees = useMetadata ? new[] { consumerTree } : [libraryTree, consumerTree];
        var compilation = Compilation.Create(
            "nullable_hierarchy_missing_consumer",
            trees,
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var diagnostics = diagnosticsFirst ? compilation.GetDiagnostics() : default;
        var match = consumerTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(consumerTree).GetMatchExhaustiveness(match);
        if (!diagnosticsFirst)
            diagnostics = compilation.GetDiagnostics();

        Assert.False(info.IsExhaustive);
        Assert.Collection(
            info.MissingCases,
            missing => Assert.Equal("SubClassB", missing),
            missing => Assert.Equal("null", missing));
        Assert.Equal(
            2,
            diagnostics.Count(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive));
    }

    [Theory]
    [InlineData("BaseClass other => \"Other\"")]
    [InlineData("_ => \"Other\"")]
    public void MatchExpression_WithNullableOpenHierarchyFallbackAndNull_IsExhaustive(string fallbackArm)
    {
        var code = $$"""
open class BaseClass {}
class SubClassA : BaseClass {}
class SubClassB : BaseClass {}

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
        SubClassB b => "B"
        null => ""
        {{fallbackArm}}
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullableOpenHierarchyWithoutFallback_RequiresBaseCoverage()
    {
        const string code = """
open class BaseClass {}
class SubClassA : BaseClass {}
class SubClassB : BaseClass {}

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
        SubClassB b => "B"
        null => ""
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "_");
    }

    [Fact]
    public void MatchExpression_WithNullableOpenHierarchyBaseFallbackWithoutNull_RequiresNullCoverage()
    {
        const string code = """
open class BaseClass {}
class SubClassA : BaseClass {}

func Describe(value: BaseClass?) -> string {
    return match value {
        SubClassA a => "A"
        BaseClass other => "Other"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "null");
    }

    [Fact]
    public void MatchExpression_WithNullableBooleanCasesAndNull_IsExhaustive()
    {
        const string code = """
func Describe(value: bool?) -> string {
    return match value {
        true => "True"
        false => "False"
        null => "Null"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullableEnumCasesAndNull_IsExhaustive()
    {
        const string code = """
enum State {
    Ready,
    Waiting
}

func Describe(value: State?) -> string {
    return match value {
        .Ready => "Ready"
        .Waiting => "Waiting"
        null => "Null"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullableStructUnionScrutinee_RequiresNullArm()
    {
        const string code = """
func area(shape: Shape?) -> int {
    return match shape {
        .Circle(let radius) => radius * radius * 3
        .Rectangle(let width, let height) => width * height
    }
}

union Shape {
    case Circle(radius: int)
    case Rectangle(width: int, height: int)
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("null")]);

        verifier.Verify();

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "nullable_struct_union_missing_null_exhaustiveness",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = model.GetMatchExhaustiveness(match);

        Assert.False(info.IsExhaustive);
        Assert.Collection(info.MissingCases, missing => Assert.Equal("null", missing));
    }

    [Fact]
    public void MatchExpression_WithNullableStructUnionScrutinee_AndNullArmIsExhaustive()
    {
        const string code = """
func area(shape: Shape?) -> int {
    return match shape {
        .Circle(let radius) => radius * radius * 3
        .Rectangle(let width, let height) => width * height
        null => 0
    }
}

union Shape {
    case Circle(radius: int)
    case Rectangle(width: int, height: int)
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithNullableClassUnionScrutinee_RequiresNullArm()
    {
        const string code = """
func area(shape: Shape?) -> int {
    return match shape {
        .Circle(let radius) => radius * radius * 3
        .Rectangle(let width, let height) => width * height
    }
}

union class Shape {
    case Circle(radius: int)
    case Rectangle(width: int, height: int)
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("null")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithNullableClassUnionScrutinee_AndNullArmIsExhaustive()
    {
        const string code = """
func area(shape: Shape?) -> int {
    return match shape {
        .Circle(let radius) => radius * radius * 3
        .Rectangle(let width, let height) => width * height
        null => 0
    }
}

union class Shape {
    case Circle(radius: int)
    case Rectangle(width: int, height: int)
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_AfterIfExpression_EvaluatesScrutineeOnce()
    {
        const string code = """
func describe(input: bool) -> string {
    (if input {
        1
    } else {
        2
    }) match {
        1 => "one"
        _ => "two"
    }
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_MissingArmReportsDiagnostic()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Off")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscriminatedUnionScrutinee_MissingArmOmitsCaseGenericTypeArgumentsInDiagnostic()
    {
        const string code = """
let value: Result<int, string> = .Ok(1)

let result = match value {
    .Ok(let payload) => payload
}

union class Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Error")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithImportedGenericUnionCases_IsExhaustive()
    {
        const string code = """
import Result.*

let value: Result<int, string> = .Ok(1)

let result = match value {
    Ok(let payload) => payload
    Error(_) => 0
}

union class Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_InsideGenericUnionWithInferredPayloadBindings_IsExhaustive()
    {
        const string code = """
import Option.*

union Option<T> {
    case Some(T)
    case None

    func Map<TResult>(mapper: T -> TResult) -> Option<TResult> {
        self match {
            Some(val value) => Some(mapper(value))
            None => None
        }
    }
}
""";

        var verifier = CreateVerifier(
            code,
            disabledDiagnostics: [CompilerDiagnostics.ConsoleApplicationRequiresEntryPoint.Id]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithPartiallyCoveredTypeUnionPayload_ReportsMissingPayloadAlternative()
    {
        const string code = """
import System.*

func Handle(result: ParseResult) -> int {
    return match result {
        .Ok(let number) => number
        .Error(ArgumentNullException error) => 0
        .Error(FormatException error) => 0
    }
}

union class ParseResult {
    case Ok(value: int)
    case Error(error: ParseError)
}

union ParseError(ArgumentNullException | FormatException | OverflowException)
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Error(OverflowException)")]);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_WithEntirelyMissingTypeUnionPayloadCase_ReportsEveryPayloadAlternative()
    {
        const string code = """
import System.*

func Handle(result: ParseResult) -> int {
    return match result {
    }
}

union class ParseResult {
    case Ok(value: int)
    case Error(error: ParseError)
}

union ParseError(ArgumentNullException | FormatException | OverflowException)
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Error(ArgumentNullException)"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Error(FormatException)"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Error(OverflowException)"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Ok"),
            ]);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_MissingArmReportsDiagnosticAtMatchKeyword()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_missing_arm_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2100"));
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.Equal(match.MatchKeyword.GetLocation(), diagnostic.Location);
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_MultipleMissingArmsReportDiagnostics()
    {
        const string code = """
union class State {
    case On
    case Off
    case Unknown
}

let state: State = .On

let result = match state {
    .On => 1
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Off"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Unknown"),
            ]);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_MultipleMissingArmsReportDiagnosticsAtMatchKeyword()
    {
        const string code = """
union class State {
    case On
    case Off
    case Unknown
}

let state: State = .On

let result = match state {
    .On => 1
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_union_multiple_missing_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2100").ToArray();
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var expectedLocation = match.MatchKeyword.GetLocation();

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(expectedLocation, d.Location));
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_RedundantCatchAllReportsDiagnostic()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off => 0
    _ => -1
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan().WithSeverity(DiagnosticSeverity.Warning)]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_RedundantCatchAllReportsDiagnosticAtCatchAllPattern()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off => 0
    _ => -1
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_redundant_catch_all_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2103"));
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.Equal(match.Arms[2].Pattern.GetLocation(), diagnostic.Location);
    }

    [Fact]
    public void MatchExpression_WithUnionScrutinee_CatchAllWithGuardDoesNotReportDiagnostic()
    {
        const string code = """
union class State {
    case On
    case Off
}

let state: State = .On

let result = match state {
    .On => 1
    .Off when false => 0
    _ => -1
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscriminatedUnionScrutinee_RedundantCatchAllReportsDiagnostic()
    {
        const string code = """
let result: Result<int> = .Ok(value: 1)

let value = match result {
    .Ok(let payload) => payload
    .Error(let message) => 0
    _ => -1
}

union class Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan().WithSeverity(DiagnosticSeverity.Warning)]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithDiscriminatedUnionScrutinee_RedundantCatchAllReportsDiagnosticAtCatchAllPattern()
    {
        const string code = """
let result: Result<int> = .Ok(value: 1)

let value = match result {
    .Ok(let payload) => payload
    .Error(let message) => 0
    _ => -1
}

union class Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_du_redundant_catch_all_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2103"));
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.Equal(match.Arms[2].Pattern.GetLocation(), diagnostic.Location);
    }

    [Fact]
    public void MatchExpression_WithBodyFormUnionCasePatterns_IsExhaustive()
    {
        const string code = """
union class Response<T> {
    case Success(value: T)
    case Failure(message: string)
}

func Describe(result: Response<int>) -> string {
    return match result {
        .Success(let value) => value.ToString()
        .Failure(let message) => message
    }
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithNestedUnionCasePatternsCoveringPayload_IsExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
        .Error(.ServiceUnavailable) => "Service unavailable"
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "nested_union_case_match_exhaustiveness",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        Assert.DoesNotContain(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive);

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(tree).GetMatchExhaustiveness(match);

        Assert.True(info.IsExhaustive, $"Expected exhaustive match but missing: [{string.Join(", ", info.MissingCases)}]");
        Assert.Empty(info.MissingCases);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void MatchExpression_WithNestedMetadataUnionPayload_TracksAllCases(
        bool qualifyCases,
        bool omitSecondCase,
        bool diagnosticsFirst)
    {
        const string librarySource = """
public union Container<T, E> {
    case Value(value: T)
    case Problem(error: E)
}
""";
        var issueTree = SyntaxTree.ParseText("""
union Issue {
    case First
    case Second
}
""");
        var firstPattern = qualifyCases ? "Issue.First" : ".First";
        var secondArm = omitSecondCase
            ? string.Empty
            : $"        .Problem({(qualifyCases ? "Issue.Second" : ".Second")}) => \"second\"";
        var consumerTree = SyntaxTree.ParseText($$"""
func describe(result: Container<int, Issue>) -> string {
    return match result {
        .Value(let value) => value.ToString()
        .Problem({{firstPattern}}) => "first"
{{secondArm}}
    }
}
""");
        var compilation = Compilation.Create(
            "match_expression_nested_metadata_union_payload_coverage",
            [issueTree, consumerTree],
            [.. TestMetadataReferences.Default,
                TestMetadataFactory.CreateFromSource(librarySource, "match_expression_outer_union_library")],
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var match = consumerTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var model = compilation.GetSemanticModel(consumerTree);
        MatchExhaustivenessInfo info;
        Diagnostic[] diagnostics;
        if (diagnosticsFirst)
        {
            diagnostics = compilation.GetDiagnostics().ToArray();
            info = model.GetMatchExhaustiveness(match);
        }
        else
        {
            info = model.GetMatchExhaustiveness(match);
            diagnostics = compilation.GetDiagnostics().ToArray();
        }

        var exhaustivenessDiagnostics = diagnostics
            .Where(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();
        Assert.Empty(diagnostics.Except(exhaustivenessDiagnostics));

        if (omitSecondCase)
        {
            var diagnostic = Assert.Single(exhaustivenessDiagnostics);
            Assert.Contains("Problem(.Second)", diagnostic.GetMessage(), StringComparison.Ordinal);
            Assert.False(info.IsExhaustive);
            Assert.Collection(info.MissingCases, missing => Assert.Equal("Problem(.Second)", missing));
        }
        else
        {
            Assert.Empty(exhaustivenessDiagnostics);
            Assert.True(info.IsExhaustive);
            Assert.Empty(info.MissingCases);
        }
    }

    [Fact]
    public void MatchExpression_WithNestedUnionCasePatternMissingPayloadCase_IsNotExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "nested_union_case_match_missing_payload_case",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        Assert.Contains(
            compilation.GetDiagnostics(),
            diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive &&
                          diagnostic.GetMessage().Contains("Error(.ServiceUnavailable)", StringComparison.Ordinal));

        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(tree).GetMatchExhaustiveness(match);

        Assert.False(info.IsExhaustive);
        Assert.Contains("Error(.ServiceUnavailable)", info.MissingCases);
    }

    [Fact]
    public void MatchExpression_WithNestedUnionOrPatternCoveringPayload_IsExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials or .ServiceUnavailable) => "Error"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedUnionOrPatternExhaustiveness_MatchesSourceAndMetadata(
        bool useMetadata,
        bool diagnosticsFirst)
    {
        const string librarySource = """
namespace NestedUnionLibrary {
    public union LoginResult {
        case Success
        case Error(error: LoginError)
    }

    public union LoginError {
        case WrongCredentials
        case ServiceUnavailable
    }
}
""";
        var libraryTree = SyntaxTree.ParseText(librarySource);
        var consumerTree = SyntaxTree.ParseText("""
import NestedUnionLibrary.*

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials or .ServiceUnavailable) => "Error"
    }
}
""");
        MetadataReference[] references = useMetadata
            ? [.. TestMetadataReferences.Default,
                TestMetadataFactory.CreateFromSource(librarySource, "nested_union_pattern_library")]
            : TestMetadataReferences.Default;
        var compilation = Compilation.Create(
            "nested_union_pattern_consumer",
            useMetadata ? [consumerTree] : [libraryTree, consumerTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        if (diagnosticsFirst)
            Assert.DoesNotContain(compilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var match = consumerTree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(consumerTree).GetMatchExhaustiveness(match);

        Assert.True(info.IsExhaustive, $"Expected exhaustive match but missing: [{string.Join(", ", info.MissingCases)}]");
        Assert.Empty(info.MissingCases);

        if (!diagnosticsFirst)
            Assert.DoesNotContain(compilation.GetDiagnostics(), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MatchExpression_WithGuardedNestedUnionCase_RemainsNotExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult, retry: bool) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
        .Error(.ServiceUnavailable) when retry => "Retry"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "Error(.ServiceUnavailable)");
    }

    [Fact]
    public void MatchExpression_WithDeeplyNestedUnionCasePatterns_IsExhaustive()
    {
        const string code = """
union Envelope {
    case Pending
    case Completed(result: LoginResult)
}

union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(envelope: Envelope) -> string {
    return match envelope {
        .Pending => "Pending"
        .Completed(.Success) => "Success"
        .Completed(.Error(.WrongCredentials)) => "Wrong credentials"
        .Completed(.Error(.ServiceUnavailable)) => "Service unavailable"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNullableNestedUnionCasePatternsAndNull_IsExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult?) -> string {
    return match result {
        null => "Missing"
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
        .Error(.ServiceUnavailable) => "Service unavailable"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithBooleanPatternsCoveringCasePayload_IsExhaustive()
    {
        const string code = """
union ToggleResult {
    case Unavailable
    case State(enabled: bool)
}

func Describe(result: ToggleResult) -> string {
    return match result {
        .Unavailable => "Unavailable"
        .State(true) => "Enabled"
        .State(false) => "Disabled"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithComplementaryBooleanCombinatorsCoveringCasePayload_IsExhaustive()
    {
        const string code = """
union ToggleResult {
    case Unavailable
    case State(enabled: bool)
}

func Describe(result: ToggleResult) -> string {
    return match result {
        .Unavailable => "Unavailable"
        .State(true) => "Enabled"
        .State(not true) => "Disabled"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithPatternsCoveringAllMultiPayloadCombinations_IsExhaustive()
    {
        const string code = """
union PairResult {
    case Empty
    case Pair(left: bool, right: bool)
}

func Describe(result: PairResult) -> string {
    return match result {
        .Empty => "Empty"
        .Pair(true, true) => "Both"
        .Pair(true, false) => "Left"
        .Pair(false, true) => "Right"
        .Pair(false, false) => "Neither"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithMultiPayloadCombinationMissing_IsNotExhaustive()
    {
        const string code = """
union PairResult {
    case Empty
    case Pair(left: bool, right: bool)
}

func Describe(result: PairResult) -> string {
    return match result {
        .Empty => "Empty"
        .Pair(true, true) => "Both"
        .Pair(true, false) => "Left"
        .Pair(false, true) => "Right"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "Pair");
    }

    [Fact]
    public void MatchExpression_WithMultiPayloadWildcardRows_IsExhaustive()
    {
        const string code = """
union PairResult {
    case Empty
    case Pair(left: bool, right: bool)
}

func Describe(result: PairResult) -> string {
    return match result {
        .Empty => "Empty"
        .Pair(true, _) => "Left"
        .Pair(false, _) => "Not left"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithCombinatorsCoveringFinitePayloadProduct_IsExhaustive()
    {
        const string code = """
union PairResult {
    case Empty
    case Pair(left: bool, right: bool)
}

func Describe(result: PairResult) -> string {
    return match result {
        .Empty => "Empty"
        .Pair(true and not false, _) => "Left"
        .Pair(not true, _) => "Not left"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithRowsCoveringFiniteTupleProduct_IsExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)) -> string {
    return match pair {
        (true, _) => "Left"
        (false, true) => "Right"
        (false, false) => "Neither"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithCombinatorsCoveringFiniteTupleProduct_IsExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)) -> string {
    return match pair {
        (true and not false, _) => "Left"
        (not true, _) => "Not left"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithMissingFiniteTupleCombination_IsNotExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)) -> string {
    return match pair {
        (true, _) => "Left"
        (false, true) => "Right"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "_");
    }

    [Fact]
    public void MatchExpression_WithNullableFiniteTupleAndNullCoverage_IsExhaustive()
    {
        const string code = """
func Describe(pair: (bool, bool)?) -> string {
    return match pair {
        null => "Missing"
        (true, _) => "Left"
        (false, _) => "Not left"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithBooleanAndEnumTupleProduct_IsExhaustive()
    {
        const string code = """
enum State {
    Off
    On
}

func Describe(pair: (bool, State)) -> string {
    return match pair {
        (true, .Off) => "Enabled off"
        (true, .On) => "Enabled on"
        (false, _) => "Disabled"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithComplementaryNestedUnionCombinators_IsExhaustive()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
        .Error(not .WrongCredentials) => "Other error"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithComplementaryTopLevelUnionNotPattern_IsExhaustive()
    {
        const string code = """
union Result {
    case Success(value: string)
    case Error(error: string)
}

func Describe(result: Result) -> string {
    return match result {
        .Success(_) => "Success"
        not .Success(_) => "Error"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithComplementaryTopLevelEnumCombinators_IsExhaustive()
    {
        const string code = """
enum Color {
    Red
    Green
    Blue
}

func Describe(color: Color) -> string {
    return match color {
        .Red and not .Green => "Red"
        not .Red => "Other"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNestedPayloadFullyCovered_ReportsRedundantCatchAll()
    {
        const string code = """
union LoginResult {
    case Success
    case Error(error: LoginError)
}

union LoginError {
    case WrongCredentials
    case ServiceUnavailable
}

func Describe(result: LoginResult) -> string {
    return match result {
        .Success => "Success"
        .Error(.WrongCredentials) => "Wrong credentials"
        .Error(.ServiceUnavailable) => "Service unavailable"
        _ => "Unknown"
    }
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan().WithSeverity(DiagnosticSeverity.Warning)]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithComplementaryNumericComparisons_IsExhaustive()
    {
        const string code = """
func Describe(value: int) -> string {
    return match value {
        < 0 => "Negative"
        >= 0 => "Non-negative"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithNumericNotPatternAndComplement_IsExhaustive()
    {
        const string code = """
func Describe(value: int) -> string {
    return match value {
        not < 0 => "Non-negative"
        < 0 => "Negative"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithComplementaryNumericRangeAndComparison_IsExhaustive()
    {
        const string code = """
func Describe(value: int) -> string {
    return match value {
        ..-1 => "Negative"
        >= 0 => "Non-negative"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: true);
    }

    [Fact]
    public void MatchExpression_WithIncompleteNumericComparison_ReportsMissingRange()
    {
        const string code = """
func Describe(value: int) -> string {
    return match value {
        < 0 => "Negative"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false, expectedMissingCase: "0..");
    }

    [Fact]
    public void MatchExpression_WithGuardedNumericComparison_IsNotExhaustive()
    {
        const string code = """
func Describe(value: int, includeZero: bool) -> string {
    return match value {
        < 0 => "Negative"
        >= 0 when includeZero => "Non-negative"
    }
}
""";

        AssertMatchExhaustiveness(code, expectedExhaustive: false);
    }

    [Fact]
    public void MatchExpression_WithComplementaryNumericComparisons_ReportsRedundantCatchAll()
    {
        const string code = """
func Describe(value: int) -> string {
    return match value {
        < 0 => "Negative"
        >= 0 => "Non-negative"
        _ => "Unknown"
    }
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan().WithSeverity(DiagnosticSeverity.Warning)]);

        verifier.Verify();
    }

    private static void AssertMatchExhaustiveness(
        string code,
        bool expectedExhaustive,
        string? expectedMissingCase = null)
    {
        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "nested_union_case_match_scenario",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var allDiagnostics = compilation.GetDiagnostics();
        var diagnostics = allDiagnostics
            .Where(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();
        Assert.Empty(allDiagnostics.Except(diagnostics));
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(tree).GetMatchExhaustiveness(match);

        Assert.Equal(expectedExhaustive, diagnostics.Length == 0);
        Assert.Equal(expectedExhaustive, info.IsExhaustive);

        if (expectedMissingCase is not null)
            Assert.Contains(expectedMissingCase, info.MissingCases);
    }

    private static void AssertMatchDiagnosticsAgreeWithSemanticModel(string code)
    {
        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_exhaustiveness_diagnostic_semantic_alignment",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Descriptor == CompilerDiagnostics.MatchExpressionNotExhaustive)
            .ToArray();
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var info = compilation.GetSemanticModel(tree).GetMatchExhaustiveness(match);

        Assert.Equal(info.MissingCases.Length, diagnostics.Length);
        foreach (var missingCase in info.MissingCases)
        {
            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.GetMessage().Contains($"'{missingCase}'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MatchExpression_WithPureDeconstructionInsideUnionCase_RedundantCatchAllReportsDiagnostic()
    {
        const string code = """
import System.*

let result: Result<string, Exception> = .Ok(value: "ok")

let value = match result {
    .Ok(let text) => text
    .Error((let message)) => message
    _ => ""
}

extension ExceptionExt for Exception {
    func Deconstruct(out message: string) -> unit {
        message = self.Message
    }
}

union class Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2103").WithAnySpan().WithSeverity(DiagnosticSeverity.Warning)]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithUnionScrutineeAndGuard_NotExhaustiveWithoutCatchAll()
    {
        const string code = """
union class Input {
    case Text(value: string)
    case Number(value: int)
    case Empty
}

let input: Input = .Text(value: "")

let result = match input {
    .Text(let text) when text.Length > 0 => "Saw \"${text}\""
    .Number(let number) => "Counted ${number}"
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Empty"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("Text"),
            ]);

        verifier.Verify();
        AssertMatchDiagnosticsAgreeWithSemanticModel(code);
    }

    [Fact]
    public void MatchExpression_WithUnionScrutineeAndGuard_NotExhaustiveWithoutCatchAll_ReportsAtMatchKeyword()
    {
        const string code = """
union class Input {
    case Text(value: string)
    case Number(value: int)
    case Empty
}

let input: Input = .Text(value: "")

let result = match input {
    .Text(let text) when text.Length > 0 => "Saw \"${text}\""
    .Number(let number) => "Counted ${number}"
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_union_guard_missing_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostics = compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2100").ToArray();
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal(match.MatchKeyword.GetLocation(), diagnostic.Location));
    }

    [Fact]
    public void MatchExpression_WithUnionScrutineeIncludingNull_DoesNotReportMissingNull()
    {
        const string code = """
let input: string? = null

let result = match input {
    null => "Nothing to report."
    string text => text
}
""";

        var verifier = CreateVerifier(code);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithPositionalPattern_BindsTupleElements()
    {
        const string code = """
let pair: object = (1, "two")

let result = match pair {
    (let first: int, let second: string) => second
    _ => ""
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
                "tuple_match",
                [tree],
                TestMetadataReferences.Default,
                new CompilationOptions(OutputKind.ConsoleApplication));

        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var boundMatch = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var tuplePattern = Assert.IsType<BoundPositionalPattern>(boundMatch.Arms[0].Pattern);
        Assert.Equal(2, tuplePattern.Elements.Length);

        var firstElement = Assert.IsType<BoundDeclarationPattern>(tuplePattern.Elements[0]);
        var firstDesignator = Assert.IsType<BoundSingleVariableDesignator>(firstElement.Designator);
        Assert.Equal("first", firstDesignator.Local.Name);

        var secondElement = Assert.IsType<BoundDeclarationPattern>(tuplePattern.Elements[1]);
        var secondDesignator = Assert.IsType<BoundSingleVariableDesignator>(secondElement.Designator);
        Assert.Equal("second", secondDesignator.Local.Name);

        var tupleType = Assert.IsAssignableFrom<ITupleTypeSymbol>(tuplePattern.Type);
        Assert.Equal(2, tupleType.TupleElements.Length);
    }

    [Fact]
    public void MatchExpression_WithPositionalPattern_ExplicitBindingAndEqualityPattern_BindsCorrectly()
    {
        const string code = """
let existingValue = 2
let pair: (int, int) = (1, 2)

let result = match pair {
    (let a, == existingValue) => a
    _ => 0
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "tuple_match_explicit_value_pattern",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        Assert.Empty(compilation.GetDiagnostics());

        var model = compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var boundMatch = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));
        var tuplePattern = Assert.IsType<BoundPositionalPattern>(boundMatch.Arms[0].Pattern);

        var firstElement = Assert.IsType<BoundDeclarationPattern>(tuplePattern.Elements[0]);
        var firstDesignator = Assert.IsType<BoundSingleVariableDesignator>(firstElement.Designator);
        Assert.Equal("a", firstDesignator.Local.Name);

        var second = Assert.IsType<BoundComparisonPattern>(tuplePattern.Elements[1]);
        Assert.Equal(BoundComparisonPatternOperator.Equals, second.Operator);
    }

    [Fact]
    public void MatchExpression_WithBareRuntimeValuePattern_ReportsExplicitComparisonDiagnostic()
    {
        const string code = """
class C {
    func Classify(no: int) -> int {
        return match no {
            no when < 5 => 1
            _ => 2
        }
    }

    func ClassifyRelational(no: int) -> int {
        return match no {
            < 5 => 1
            _ => 2
        }
    }

    func ClassifyTyped(no: int) -> int {
        return match no {
            int no when < 5 => 1
            _ => 2
        }
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "bare_runtime_value_pattern",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal("RAV1617", diagnostic.Descriptor.Id);
        Assert.Equal("no", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        Assert.Contains("use '== no'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void NestedRuntimeComparisonAndGuardPatterns_ComposeGenerally()
    {
        const string code = """
func Direct(pair: (string, int), discountedProduct: string) -> int {
    return match pair {
        (== discountedProduct, _) => 1
        _ => 0
    }
}

func ShorthandGuard(pair: (string, int), discountedProduct: string) -> string {
    return match pair {
        (let productName when == discountedProduct, _) => productName
        _ => ""
    }
}

func ExpressionGuard(pair: (string, int), discountedProduct: string) -> string {
    return match pair {
        (let productName when productName == discountedProduct, _) => productName
        _ => ""
    }
}

func TypedExpressionGuard(pair: (string, int), discountedProduct: string) -> string {
    return match pair {
        (let productName: string when productName == discountedProduct, _) => productName
        _ => ""
    }
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "general_nested_runtime_value_patterns",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.EnsureSetup();

        Assert.Empty(compilation.GetDiagnostics());
    }

    [Fact]
    public void MatchExpression_WithOuterValSequencePattern_BindsImplicitCaptures()
    {
        const string code = """
let input = [1, 2, 3, 4]

let result = match input {
    let [first, second, ...rest] => first + second + rest.Count
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);
    }

    [Fact]
    public void MatchExpression_WithOuterValNominalPattern_BindsImplicitCaptures()
    {
        const string code = """
import Option.*

union Option<T> {
    case Some(value: T)
    case None
}

let value: Option<(int, int)> = .Some((1, 2))

let result = match value {
    let Some((x, y)) => x + y
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);
    }

    [Fact]
    public void MatchExpression_WithOuterValNamedElements_BindsImplicitCaptures()
    {
        const string code = """
record class Person(Name: string, Age: int)

let person = Person("Ada", 42)

let result = match person {
    let (Name: name, Age: age) => name.Length + age
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);
    }

    [Fact]
    public void MatchExpression_WithOuterValNamedTypedTargetWithoutInlineBinding_ReportsDiagnostic()
    {
        const string code = """
record class Person(Name: string, Age: int)

let person = Person("Ada", 42)

let result = match person {
    let (Name: name: string, Age: age: int) => name.Length + age
    _ => 0
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult(CompilerDiagnostics.PatternTypedBindingRequiresKeyword.Id)
                    .WithAnySpan()
                    .WithArguments("name", "string"),
                new DiagnosticResult(CompilerDiagnostics.PatternTypedBindingRequiresKeyword.Id)
                    .WithAnySpan()
                    .WithArguments("age", "int")
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithOuterAndInlineBindingKeywords_ReportsConflict()
    {
        const string code = """
let input = [1, 2, 3]

let result = match input {
    let [let first, second, ...rest] => first
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.MissingDiagnostics);
        Assert.Empty(result.UnexpectedDiagnostics.Where(d => d.Descriptor != CompilerDiagnostics.PatternDeclarationBindingKeywordConflict));
        Assert.Contains(result.Compilation.GetDiagnostics(), d => d.Descriptor == CompilerDiagnostics.PatternDeclarationBindingKeywordConflict);
    }

    [Fact]
    public void MatchExpression_WithComparisonPatternOfDifferentType_ReportsDiagnostic()
    {
        const string code = """
let pair: (int, int) = (1, 2)

let result = match pair {
    (1, > 0.5) => 1
    _ => 0
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "tuple_match_comparison_type_mismatch",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RAV1606");
    }

    [Fact]
    public void MatchExpression_WithRangePatternOfDifferentType_ReportsDiagnostic()
    {
        const string code = """
let value: int = 2

let result = match value {
    0..0.5 => 1
    _ => 0
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "range_pattern_type_mismatch",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();
        Assert.Contains(diagnostics, d => d.Id == "RAV1606");
    }

    [Fact]
    public void MatchExpression_WithRangePatternVariableBounds_BindsEndpointExpressions()
    {
        const string code = """
func IsEligible(year: int, lower: int, upper: int) -> bool {
    return match year {
        lower..upper => true
        _ => false
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var rangePattern = Assert.IsType<BoundRangePattern>(bound.Arms[0].Pattern);
        Assert.Equal(SpecialType.System_Int32, rangePattern.Type.SpecialType);
        Assert.IsType<BoundParameterAccess>(rangePattern.LowerBound);
        Assert.IsType<BoundParameterAccess>(rangePattern.UpperBound);
    }

    [Fact]
    public void MatchExpression_WithNestedCaseNominalSequenceAndWholeDesignation_BindsAllLocals()
    {
        const string code = """
import Option.*

union Option<T> {
    case Some(value: T)
    case None
}

class C {
    func Run(value: Option<(string, int)>) -> int {
        return match value {
            let Some((first, >= 18)) whole => first.Length
            _ => 0
        }
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);

        var first = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<SingleVariableDesignationSyntax>().Single(d => d.Identifier.ValueText == "first")));
        var whole = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<SingleVariableDesignationSyntax>().Single(d => d.Identifier.ValueText == "whole")));

        Assert.Equal("string", first.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        Assert.Equal("Some<(string, int)>", whole.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    [Fact]
    public void MatchExpression_WithPositionalPatternLengthMismatch_ReportsDiagnostic()
    {
        const string code = """
let pair: (int, int) = (1, 2)

let result = match pair {
    (int a, int b, int c) => c
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult("RAV2102").WithAnySpan().WithArguments("for type '(int, int, int)'", "(int, int)"),
                new DiagnosticResult("RAV2100").WithAnySpan().WithArguments("_"),
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithPositionalPatternLengthMismatch_ReportsExhaustivenessAtMatchKeyword()
    {
        const string code = """
let pair: (int, int) = (1, 2)

let result = match pair {
    (int a, int b, int c) => c
}
""";

        var tree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "match_expression_tuple_length_mismatch_location",
            [tree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.ConsoleApplication));

        compilation.EnsureSetup();
        var diagnostic = Assert.Single(compilation.GetDiagnostics().Where(d => d.Descriptor.Id == "RAV2100"));
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();

        Assert.Equal(match.MatchKeyword.GetLocation(), diagnostic.Location);
    }

    [Fact]
    public void MatchExpression_WithIncompatiblePattern_ReportsDiagnostic()
    {
        const string code = """
let value: int = 0

let result = match value {
    string text => text
    _ => ""
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2102").WithAnySpan().WithArguments("for type 'string'", "int")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithUnionScrutineeAndIncompatiblePattern_ReportsDiagnostic()
    {
        const string code = """
union class State {
    case On
    case Off
}

let value: State = .On

let result = match value {
    bool flag => 1
    _ => 0
}
""";

        var verifier = CreateVerifier(
            code,
            [new DiagnosticResult("RAV2102").WithAnySpan().WithArguments("for type 'bool'", "State")]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_WithIncompatibleLiteralPattern_ReportsDiagnostic()
    {
        const string code = """
let value: int = 0

let result = match value {
    "foo" => 1
    _ => 0
}
""";

        var verifier = CreateVerifier(
            code,
            [
                new DiagnosticResult("RAV2102").WithAnySpan().WithArguments("string", "int"),
                new DiagnosticResult(CompilerDiagnostics.MatchExpressionArmUnreachable.Id).WithAnySpan(),
            ]);

        verifier.Verify();
    }

    [Fact]
    public void MatchExpression_CasePatternDeclaredSymbols_UseCaseParameterTypes()
    {
        const string code = """
abstract class Expr

record Lit(Value: int) : Expr
record Add(Left: Expr, Right: Expr) : Expr

func Evaluate(expr: Expr) -> int {
    return match expr {
        Add(let left, let right) => 0
        _ => 0
    }
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var designators = tree.GetRoot()
            .DescendantNodes()
            .OfType<SingleVariableDesignationSyntax>()
            .Where(d => d.Identifier.ValueText is "left" or "right")
            .ToArray();

        Assert.Equal(2, designators.Length);

        var exprType = Assert.IsAssignableFrom<INamedTypeSymbol>(result.Compilation.GetTypeByMetadataName("Expr"));
        foreach (var designator in designators)
        {
            var symbol = Assert.IsAssignableFrom<ILocalSymbol>(model.GetDeclaredSymbol(designator));
            Assert.True(SymbolEqualityComparer.Default.Equals(exprType, symbol.Type));
        }
    }

    [Fact]
    public void MatchExpression_WithExclusiveRangePattern_BindsExclusiveUpperBound()
    {
        const string code = """
let value: int = 9

let result = match value {
    2..<10 => 1
    _ => 0
}
""";

        var verifier = CreateVerifier(code);
        var result = verifier.GetResult();

        Assert.Empty(result.UnexpectedDiagnostics);
        Assert.Empty(result.MissingDiagnostics);

        var tree = result.Compilation.SyntaxTrees.Single();
        var model = result.Compilation.GetSemanticModel(tree);
        var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundMatchExpression>(model.GetBoundNode(match));

        var rangePattern = Assert.IsType<BoundRangePattern>(bound.Arms[0].Pattern);
        Assert.True(rangePattern.IsUpperExclusive);
    }
}
