using System;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Semantics.Tests;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Lowering.Tests;

public sealed class IteratorLowererTests : CompilationTestBase
{
    [Fact]
    public void ShouldRewrite_WhenMethodContainsYield()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator() -> IEnumerable<int> {
        yield 1
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        Assert.True(IteratorLowerer.ShouldRewrite(methodSymbol, boundBody));
    }

    [Fact]
    public void Rewrite_AttachesStateMachineMetadata()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        var rewritten = IteratorLowerer.Rewrite(methodSymbol, boundBody);
        Assert.NotSame(boundBody, rewritten);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);

        Assert.True(methodSymbol.IsIterator);
        Assert.Equal(IteratorMethodKind.Enumerable, methodSymbol.IteratorKind);
        Assert.Equal(methodSymbol.IteratorElementType, stateMachine.ElementType);

        var iterators = compilation.GetSynthesizedIteratorTypes().ToArray();
        Assert.Contains(stateMachine, iterators);

        Assert.Equal("_state", stateMachine.StateField.Name);
        Assert.Equal(compilation.GetSpecialType(SpecialType.System_Int32), stateMachine.StateField.Type);

        Assert.Equal("_current", stateMachine.CurrentField.Name);
        Assert.Equal(stateMachine.ElementType, stateMachine.CurrentField.Type);

        var parameterCapture = Assert.Single(stateMachine.ParameterFields);
        Assert.Equal("_count", parameterCapture.Name);

        var parameterMapEntry = Assert.Single(stateMachine.ParameterFieldMap);
        Assert.Equal("count", parameterMapEntry.Key.Name);
        Assert.Equal(parameterCapture, parameterMapEntry.Value);

        Assert.NotNull(stateMachine.ThisField);
        Assert.Equal(methodSymbol.ContainingType, stateMachine.ThisField!.Type);

        Assert.NotNull(stateMachine.Constructor);
        Assert.NotNull(stateMachine.CurrentProperty.GetMethod);
        Assert.NotNull(stateMachine.NonGenericCurrentProperty.GetMethod);
        Assert.NotNull(stateMachine.DisposeMethod);
        Assert.NotNull(stateMachine.ResetMethod);
        Assert.NotNull(stateMachine.MoveNextMethod);
        Assert.NotNull(stateMachine.GenericGetEnumeratorMethod);
        Assert.NotNull(stateMachine.NonGenericGetEnumeratorMethod);

        var enumerableGeneric = (INamedTypeSymbol)compilation
            .GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T)
            .Construct(stateMachine.ElementType);
        Assert.Contains(enumerableGeneric, stateMachine.Interfaces, SymbolEqualityComparer.Default);

        var enumeratorGeneric = (INamedTypeSymbol)compilation
            .GetSpecialType(SpecialType.System_Collections_Generic_IEnumerator_T)
            .Construct(stateMachine.ElementType);
        Assert.Contains(enumeratorGeneric, stateMachine.Interfaces, SymbolEqualityComparer.Default);
    }

    [Fact]
    public void Rewrite_AttachesAsyncIteratorStateMachineMetadata()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    async func Iterator(count: int) -> IAsyncEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        _ = IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        Assert.True(methodSymbol.IsIterator);
        Assert.Equal(IteratorMethodKind.AsyncEnumerable, methodSymbol.IteratorKind);
        Assert.NotNull(stateMachine.AsyncMoveNextMethod);
        Assert.NotNull(stateMachine.AsyncDisposeMethod);
        Assert.NotNull(stateMachine.AsyncGetEnumeratorMethod);
        Assert.Null(stateMachine.GenericGetEnumeratorMethod);
        Assert.Null(stateMachine.NonGenericGetEnumeratorMethod);
        Assert.Null(stateMachine.DisposeMethod);
        Assert.Null(stateMachine.ResetMethod);

        var asyncEnumerableDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerable`1");
        var asyncEnumeratorDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.IAsyncEnumerator`1");
        Assert.NotNull(asyncEnumerableDefinition);
        Assert.NotNull(asyncEnumeratorDefinition);

        Assert.Contains(
            (INamedTypeSymbol)asyncEnumerableDefinition.Construct(stateMachine.ElementType),
            stateMachine.Interfaces,
            SymbolEqualityComparer.Default);
        Assert.Contains(
            (INamedTypeSymbol)asyncEnumeratorDefinition.Construct(stateMachine.ElementType),
            stateMachine.Interfaces,
            SymbolEqualityComparer.Default);
    }

    [Fact]
    public void Rewrite_AsyncIteratorWithEnumeratorCancellation_CapturesOriginalTokenAndSynthesizesLinkedTokenField()
    {
        const string source = """
import System.Collections.Generic.*
import System.Runtime.CompilerServices.*
import System.Threading.*

class C {
    async func Iterator([EnumeratorCancellation] cancellationToken: CancellationToken) -> IAsyncEnumerable<int> {
        yield 1
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var methodSyntax = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        _ = IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        Assert.NotNull(stateMachine.EnumeratorCancellationParameter);
        Assert.NotNull(stateMachine.EnumeratorCancellationOriginalParameterField);
        Assert.NotNull(stateMachine.CombinedTokensField);
        Assert.NotNull(stateMachine.AsyncGetEnumeratorBody);

        Assert.Contains(stateMachine.ParameterFields, field => field.Name == "_cancellationToken");
        Assert.Equal("_cancellationToken", stateMachine.ParameterFieldMap[stateMachine.EnumeratorCancellationParameter!].Name);
        Assert.Equal("_cancellationTokenOriginal", stateMachine.EnumeratorCancellationOriginalParameterField!.Name);
        Assert.Equal("_combinedTokens", stateMachine.CombinedTokensField!.Name);
    }

    [Fact]
    public void Rewrite_RewritesMethodBodyToInstantiateStateMachine()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        var rewritten = IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        var statements = rewritten.Statements.ToArray();
        Assert.Equal(5, statements.Length);

        var declaration = Assert.IsType<BoundLocalDeclarationStatement>(statements[0]);
        var local = declaration.Declarators.Single().Local;
        AssertFieldAssignmentStatement(statements[1], stateMachine.ThisField!);

        var parameterFieldAssignment = AssertFieldAssignmentStatement(statements[2], stateMachine.ParameterFields[0]);
        Assert.Equal(stateMachine.ParameterFields[0], parameterFieldAssignment.Field);
        Assert.IsType<BoundParameterAccess>(parameterFieldAssignment.Right);

        AssertFieldAssignmentStatement(statements[3], stateMachine.StateField, expectedValue: 0);

        var returnStatement = Assert.IsType<BoundReturnStatement>(statements[4]);
        var cast = Assert.IsType<BoundConversionExpression>(returnStatement.Expression);
        var returnLocal = Assert.IsType<BoundLocalAccess>(cast.Expression);
        Assert.Equal(local, returnLocal.Local);
    }

    [Fact]
    public void Rewrite_ExposesSynchronousIteratorProtocol()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        Assert.NotNull(stateMachine.MoveNextBody);
        Assert.Equal(SpecialType.System_Boolean, stateMachine.MoveNextMethod.ReturnType.SpecialType);
        Assert.Equal(SpecialType.System_Int32, stateMachine.CurrentProperty.Type.SpecialType);
        Assert.NotNull(stateMachine.DisposeMethod);
        Assert.Contains(stateMachine.AllInterfaces, type => type.MetadataName == "IEnumerator`1");
    }

    [Fact]
    public void Rewrite_DoesNotCaptureThis_ForStaticIterator()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    static func Iterator(count: int) -> IEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        var rewritten = IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        Assert.Null(stateMachine.ThisField);

        var statements = rewritten.Statements.ToArray();
        Assert.Equal(4, statements.Length);
        Assert.IsType<BoundLocalDeclarationStatement>(statements[0]);
        var returnStatement = Assert.IsType<BoundReturnStatement>(statements[3]);
        var cast = Assert.IsType<BoundConversionExpression>(returnStatement.Expression);
        Assert.IsType<BoundLocalAccess>(cast.Expression);
    }

    [Fact]
    public void ShouldNotRewrite_WhenMethodHasNoYield()
    {
        const string source = """
class C {
    func Test() {
        let value = 1
        value
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        Assert.False(IteratorLowerer.ShouldRewrite(methodSymbol, boundBody));
    }

    [Fact]
    public void ShouldNotRewrite_ForYieldInsideNestedFunction()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Test() {
        func nested() -> IEnumerable<int> {
            yield 1
        }

        let enumerator = nested()
        ()
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Test");

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        Assert.False(IteratorLowerer.ShouldRewrite(methodSymbol, boundBody));
    }

    [Fact]
    public void Rewrite_AttachesStateMachineMetadata_ForIteratorLambda()
    {
        const string source = """
import System.*
import System.Collections.Generic.*

class C {
    func Make() -> Func<IEnumerable<int>> {
        let iterator: Func<IEnumerable<int>> = () -> IEnumerable<int> => {
            yield 1
            yield 2
        }

        iterator
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var lambdaSyntax = tree.GetRoot().DescendantNodes().OfType<ParenthesizedFunctionExpressionSyntax>().Single();
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsType<SourceLambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsIterator);
        Assert.Equal(IteratorMethodKind.Enumerable, lambdaSymbol.IteratorKind);

        var owner = lambdaSymbol.ContainingSymbol ?? compilation.Assembly.GlobalNamespace;
        _ = FunctionExpressionLowerer.Rewrite(boundLambda, owner);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(lambdaSymbol.IteratorStateMachine);
        Assert.Equal(lambdaSymbol.IteratorElementType, stateMachine.ElementType);
        Assert.NotNull(stateMachine.GenericGetEnumeratorMethod);
        Assert.NotNull(stateMachine.MoveNextBody);
    }

    [Fact]
    public void Rewrite_AttachesStateMachineMetadata_ForAsyncIteratorLambda()
    {
        const string source = """
import System.*
import System.Collections.Generic.*

class C {
    func Make() -> Func<IAsyncEnumerable<int>> {
        let iterator: Func<IAsyncEnumerable<int>> = async () -> IAsyncEnumerable<int> => {
            yield 1
            yield 2
        }

        iterator
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var lambdaSyntax = tree.GetRoot().DescendantNodes().OfType<ParenthesizedFunctionExpressionSyntax>().Single();
        var boundLambda = Assert.IsType<BoundFunctionExpression>(model.GetBoundNode(lambdaSyntax));
        var lambdaSymbol = Assert.IsType<SourceLambdaSymbol>(boundLambda.Symbol);

        Assert.True(lambdaSymbol.IsIterator);
        Assert.Equal(IteratorMethodKind.AsyncEnumerable, lambdaSymbol.IteratorKind);

        var owner = lambdaSymbol.ContainingSymbol ?? compilation.Assembly.GlobalNamespace;
        _ = FunctionExpressionLowerer.Rewrite(boundLambda, owner);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(lambdaSymbol.IteratorStateMachine);
        Assert.NotNull(stateMachine.AsyncMoveNextMethod);
        Assert.NotNull(stateMachine.AsyncGetEnumeratorMethod);
        Assert.NotNull(stateMachine.MoveNextBody);
    }

    [Fact]
    public void Rewrite_PopulatesIteratorHelperBodies_ForEnumerableIterator()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerable<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);

        var currentBody = Assert.IsType<BoundBlockStatement>(stateMachine.CurrentGetterBody);
        var currentReturn = Assert.IsType<BoundReturnStatement>(Assert.Single(currentBody.Statements));
        var currentAccess = Assert.IsType<BoundFieldAccess>(currentReturn.Expression);
        Assert.Equal(stateMachine.CurrentField, currentAccess.Field);

        var nonGenericBody = Assert.IsType<BoundBlockStatement>(stateMachine.NonGenericCurrentGetterBody);
        var nonGenericReturn = Assert.IsType<BoundReturnStatement>(Assert.Single(nonGenericBody.Statements));
        var nonGenericCast = Assert.IsType<BoundConversionExpression>(nonGenericReturn.Expression);
        Assert.Equal(stateMachine.NonGenericCurrentProperty.Type, nonGenericCast.Type);
        var nonGenericSource = Assert.IsType<BoundFieldAccess>(nonGenericCast.Expression);
        Assert.Equal(stateMachine.CurrentField, nonGenericSource.Field);

        var disposeBody = Assert.IsType<BoundBlockStatement>(stateMachine.DisposeBody);
        var disposeStatements = disposeBody.Statements.ToArray();
        Assert.Equal(2, disposeStatements.Length);
        var disposeFieldAssignment = AssertFieldAssignmentStatement(disposeStatements[0], stateMachine.StateField);
        Assert.Equal(stateMachine.StateField, disposeFieldAssignment.Field);
        var disposeLiteral = Assert.IsType<BoundLiteralExpression>(disposeFieldAssignment.Right);
        Assert.Equal(-1, disposeLiteral.Value);
        Assert.IsType<BoundReturnStatement>(disposeStatements[1]);

        var resetBody = Assert.IsType<BoundBlockStatement>(stateMachine.ResetBody);
        var resetStatement = Assert.Single(resetBody.Statements);
        var throwStatement = Assert.IsType<BoundThrowStatement>(resetStatement);
        Assert.IsType<BoundObjectCreationExpression>(throwStatement.Expression);

        var genericBody = Assert.IsType<BoundBlockStatement>(stateMachine.GenericGetEnumeratorBody);
        var genericStatements = genericBody.Statements.ToArray();
        Assert.Equal(2, genericStatements.Length);
        var resetFieldAssignment = AssertFieldAssignmentStatement(genericStatements[0], stateMachine.StateField);
        Assert.Equal(stateMachine.StateField, resetFieldAssignment.Field);
        var resetLiteral = Assert.IsType<BoundLiteralExpression>(resetFieldAssignment.Right);
        Assert.Equal(0, resetLiteral.Value);
        var genericReturn = Assert.IsType<BoundReturnStatement>(genericStatements[1]);
        var genericCast = Assert.IsType<BoundConversionExpression>(genericReturn.Expression);
        Assert.Equal(stateMachine.GenericGetEnumeratorMethod!.ReturnType, genericCast.Type);
        Assert.IsType<BoundSelfExpression>(genericCast.Expression);

        var nonGenericGetEnumeratorBody = Assert.IsType<BoundBlockStatement>(stateMachine.NonGenericGetEnumeratorBody);
        var nonGenericStatements = nonGenericGetEnumeratorBody.Statements.ToArray();
        var nonGenericReturnStmt = Assert.IsType<BoundReturnStatement>(Assert.Single(nonGenericStatements));
        var nonGenericReturnExpr = Assert.IsType<BoundConversionExpression>(nonGenericReturnStmt.Expression);
        var invocation = Assert.IsType<BoundInvocationExpression>(nonGenericReturnExpr.Expression);
        Assert.Equal(stateMachine.GenericGetEnumeratorMethod, invocation.Method);
    }

    [Fact]
    public void Rewrite_WrapsFinallyBlockWithStateGuard()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerable<int> {
        try {
            yield count
        } finally {
            let disposed = count
        }
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        var moveNextBody = Assert.IsType<BoundBlockStatement>(stateMachine.MoveNextBody);
        var statements = moveNextBody.Statements.ToArray();

        var guard = FindDescendantStatements(moveNextBody)
            .OfType<BoundIfStatement>()
            .Single(ifStatement => ifStatement.Condition is BoundBinaryExpression { Operator.OperatorKind: BinaryOperatorKind.LessThan });
        var condition = Assert.IsType<BoundBinaryExpression>(guard.Condition);

        var stateAccess = Assert.IsType<BoundFieldAccess>(condition.Left);
        Assert.Equal(stateMachine.StateField, stateAccess.Field);

        var zeroLiteral = Assert.IsType<BoundLiteralExpression>(condition.Right);
        Assert.Equal(0, zeroLiteral.Value);
        Assert.Equal(BinaryOperatorKind.LessThan, condition.Operator.OperatorKind);

        var guardedBlock = Assert.IsType<BoundBlockStatement>(guard.ThenNode);
        var guardedStatements = guardedBlock.Statements.ToArray();
        Assert.Single(guardedStatements);
    }

    [Fact]
    public void Rewrite_DoesNotCreateGetEnumeratorBodies_ForEnumeratorIterator()
    {
        const string source = """
import System.Collections.Generic.*

class C {
    func Iterator(count: int) -> IEnumerator<int> {
        yield count
    }
}
""";

        var (compilation, tree) = CreateCompilation(source);
        compilation.EnsureSetup();

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var methodSyntax = root
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single();

        var methodSymbol = Assert.IsType<SourceMethodSymbol>(model.GetDeclaredSymbol(methodSyntax));
        var boundBody = Assert.IsType<BoundBlockStatement>(model.GetBoundNode(methodSyntax.Body!));

        IteratorLowerer.Rewrite(methodSymbol, boundBody);

        var stateMachine = Assert.IsType<SynthesizedIteratorTypeSymbol>(methodSymbol.IteratorStateMachine);
        Assert.Null(stateMachine.GenericGetEnumeratorMethod);
        Assert.Null(stateMachine.NonGenericGetEnumeratorMethod);
        Assert.Null(stateMachine.GenericGetEnumeratorBody);
        Assert.Null(stateMachine.NonGenericGetEnumeratorBody);
    }

    private static BoundFieldAssignmentExpression AssertFieldAssignmentStatement(BoundStatement statement, IFieldSymbol expectedField, int? expectedValue = null)
    {
        var assignment = statement switch
        {
            BoundAssignmentStatement assignmentStatement => Assert.IsType<BoundFieldAssignmentExpression>(assignmentStatement.Expression),
            BoundExpressionStatement expressionStatement => Assert.IsType<BoundFieldAssignmentExpression>(expressionStatement.Expression),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected assignment statement kind: {statement.GetType().Name}"),
        };

        Assert.Equal(expectedField, assignment.Field);

        if (expectedValue is int value)
        {
            var literal = Assert.IsType<BoundLiteralExpression>(assignment.Right);
            Assert.Equal(BoundLiteralExpressionKind.NumericLiteral, literal.Kind);
            Assert.Equal(value, literal.Value);
        }

        return assignment;
    }

    private static bool IsFieldAssignment(BoundStatement statement, IFieldSymbol expectedField, int expectedValue)
    {
        var assignment = statement switch
        {
            BoundAssignmentStatement assignmentStatement => assignmentStatement.Expression as BoundFieldAssignmentExpression,
            BoundExpressionStatement expressionStatement => expressionStatement.Expression as BoundFieldAssignmentExpression,
            _ => null,
        };

        if (assignment is null || !Equals(assignment.Field, expectedField))
        {
            return false;
        }

        return assignment.Right is BoundLiteralExpression literal
               && Equals(literal.Value, expectedValue);
    }

    private static IEnumerable<BoundStatement> FindDescendantStatements(BoundStatement root)
    {
        yield return root;

        switch (root)
        {
            case BoundBlockStatement block:
                foreach (var statement in block.Statements)
                {
                    foreach (var descendant in FindDescendantStatements(statement))
                    {
                        yield return descendant;
                    }
                }
                break;
            case BoundLabeledStatement labeled:
                foreach (var descendant in FindDescendantStatements(labeled.Statement))
                {
                    yield return descendant;
                }
                break;
            case BoundIfStatement ifStatement:
                foreach (var descendant in FindDescendantStatements(ifStatement.ThenNode))
                {
                    yield return descendant;
                }

                if (ifStatement.ElseNode is not null)
                {
                    foreach (var descendant in FindDescendantStatements(ifStatement.ElseNode))
                    {
                        yield return descendant;
                    }
                }
                break;
            case BoundTryStatement tryStatement:
                foreach (var descendant in FindDescendantStatements(tryStatement.TryBlock))
                {
                    yield return descendant;
                }

                foreach (var catchClause in tryStatement.CatchClauses)
                {
                    foreach (var descendant in FindDescendantStatements(catchClause.Block))
                    {
                        yield return descendant;
                    }
                }

                if (tryStatement.FinallyBlock is not null)
                {
                    foreach (var descendant in FindDescendantStatements(tryStatement.FinallyBlock))
                    {
                        yield return descendant;
                    }
                }
                break;
        }
    }

    private static void AssertFalseLiteral(BoundExpression? expression)
    {
        var literal = Assert.IsType<BoundLiteralExpression>(expression);
        Assert.Equal(BoundLiteralExpressionKind.FalseLiteral, literal.Kind);
    }

}
