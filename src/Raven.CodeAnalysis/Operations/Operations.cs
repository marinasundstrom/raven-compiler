using System.Collections.Immutable;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Syntax;
namespace Raven.CodeAnalysis.Operations;

public interface IBlockOperation : IOperation
{
    /// <summary>
    /// Operations contained within the block.
    /// </summary>
    ImmutableArray<IOperation> Operations { get; }

    /// <summary>
    /// Local declarations contained within the block.
    /// </summary>
    ImmutableArray<ILocalSymbol> Locals { get; }
}

public interface IExpressionStatementOperation : IOperation
{
    /// <summary>
    /// The operation represented by the statement.
    /// </summary>
    IOperation? Operation { get; }
}

public interface IFunctionOperation : IOperation
{
}

public interface IVariableDeclarationOperation : IOperation
{
    /// <summary>
    /// Variable declaration in the statement.
    /// </summary>
    /// <remarks>
    /// In C#, this will always be a single declaration, with all variables in <see cref="IVariableDeclarationOperation.Declarators" />.
    /// </remarks>
    ImmutableArray<IVariableDeclaratorOperation> Declarators { get; }
}

public interface IVariableDeclaratorOperation : IOperation
{
    /// <summary>
    /// Symbol declared by this variable declaration
    /// </summary>
    ILocalSymbol Symbol { get; }
    /// <summary>
    /// Optional initializer of the variable.
    /// </summary>
    /// <remarks>
    /// If this variable is in an <see cref="IVariableDeclarationOperation" />, the initializer may be located
    /// in the parent operation. Use the operation helpers to check all locations.
    /// It is only possible to have initializers in both locations in VB invalid code scenarios.
    /// </remarks>
    IVariableInitializerOperation? Initializer { get; }
    /// <summary>
    /// Additional arguments supplied to the declarator in error cases, ignored by the compiler. This only used for the C# case of
    /// DeclaredArgumentSyntax nodes on a VariableDeclaratorSyntax.
    /// </summary>
    ImmutableArray<IOperation> IgnoredArguments { get; }
}

public interface IVariableInitializerOperation : IOperation
{
    /// <summary>
    /// Value assigned during initialization.
    /// </summary>
    IOperation? Value { get; }
}

public interface IReturnOperation : IOperation
{
    /// <summary>
    /// Value to be returned.
    /// </summary>
    IOperation? ReturnedValue { get; }
}

public interface IYieldOperation : IOperation
{
    /// <summary>Whether this yield delegates to a sequence.</summary>
    bool IsDelegating { get; }

    IOperation? ReturnedValue { get; }

    ITypeSymbol ElementType { get; }
}

public interface IThrowOperation : IOperation
{
    IOperation? Exception { get; }
}

public interface ILiteralOperation : IOperation
{
    object Value { get; }
}

public interface IInterpolatedStringOperation : IOperation
{
    ImmutableArray<IInterpolatedStringContentOperation> Contents { get; }
}

public interface IInterpolatedStringContentOperation : IOperation
{
}

public interface IInterpolatedStringTextOperation : IInterpolatedStringContentOperation
{
    string Text { get; }
}

public interface IInterpolationOperation : IInterpolatedStringContentOperation
{
    IOperation? Expression { get; }
}

public interface IDefaultValueOperation : IOperation
{
}

public interface IBreakOperation : IOperation
{
    ILabelSymbol? TargetLabel { get; }
}

public interface IContinueOperation : IOperation
{
    ILabelSymbol? TargetLabel { get; }
}

public interface IGotoOperation : IOperation
{
    ILabelSymbol Target { get; }
}

public interface IConditionalGotoOperation : IOperation
{
    ILabelSymbol Target { get; }

    bool JumpIfTrue { get; }

    IOperation? Condition { get; }
}

public interface ILabeledOperation : IOperation
{
    ILabelSymbol Label { get; }
}

public interface ISymbolReferenceOperation<out TSymbol> : IOperation where TSymbol : ISymbol
{
    TSymbol Symbol { get; }
}

public interface ILocalReferenceOperation : ISymbolReferenceOperation<ILocalSymbol>
{
    ILocalSymbol Local { get; }
}

public interface IVariableReferenceOperation : ISymbolReferenceOperation<ILocalSymbol>
{
    ILocalSymbol Variable { get; }
}

public interface IParameterReferenceOperation : ISymbolReferenceOperation<IParameterSymbol>
{
    IParameterSymbol Parameter { get; }
}

public interface IFieldReferenceOperation : ISymbolReferenceOperation<IFieldSymbol>
{
    IFieldSymbol Field { get; }
}

public interface IPropertyReferenceOperation : ISymbolReferenceOperation<IPropertySymbol>
{
    IPropertySymbol Property { get; }
}

public interface IMethodReferenceOperation : ISymbolReferenceOperation<IMethodSymbol>
{
    IMethodSymbol Method { get; }
}

public interface IMemberReferenceOperation : ISymbolReferenceOperation<ISymbol>
{
}

public interface IUnaryOperation : IOperation
{
    IOperation? Operand { get; }
}

public interface IBinaryOperation : IOperation
{
    IOperation? Left { get; }

    IOperation? Right { get; }
}

public interface ICoalesceOperation : IBinaryOperation
{
}

public interface INameOfOperation : IOperation
{
    string Name { get; }

    IOperation? Operand { get; }
}

public interface IParenthesizedOperation : IOperation
{
    IOperation? Operand { get; }
}

public interface IConversionOperation : IOperation
{
    Conversion Conversion { get; }

    IOperation? Operand { get; }
}

public interface IPropagationOperation : IUnaryOperation
{
    ITypeSymbol OkType { get; }

    ITypeSymbol? ErrorType { get; }

    INamedTypeSymbol EnclosingResultType { get; }

    IMethodSymbol EnclosingErrorConstructor { get; }
}

public interface IDereferenceOperation : IUnaryOperation
{
}

public interface IConditionalOperation : IOperation
{
    IOperation? Condition { get; }

    IOperation? WhenTrue { get; }

    IOperation? WhenFalse { get; }
}

public interface IConditionalAccessOperation : IOperation
{
    IOperation? Receiver { get; }

    IOperation? WhenNotNull { get; }
}

public interface IAwaitOperation : IOperation
{
    IOperation? Operation { get; }

    IMethodSymbol GetAwaiterMethod { get; }

    IMethodSymbol GetResultMethod { get; }

    IPropertySymbol IsCompletedProperty { get; }
}

public interface IInvocationOperation : IOperation
{
    IMethodSymbol TargetMethod { get; }

    IOperation? Instance { get; }

    ImmutableArray<IOperation> Arguments { get; }
}

public interface IArgumentOperation : IOperation
{
    IOperation? Value { get; }

    string? Name { get; }

    bool IsNamed { get; }
}

public interface IDelegateCreationOperation : IOperation
{
}

public interface IWithOperation : IOperation
{
    IOperation? Receiver { get; }

    ImmutableArray<ISymbol> Members { get; }

    ImmutableArray<IOperation> Values { get; }
}

public interface IAddressOfOperation : IOperation
{
}

public interface IStackAllocOperation : IOperation
{
    ITypeSymbol ElementType { get; }

    IOperation Count { get; }
}

public interface IElementAccessOperation : IOperation
{
    IOperation? Instance { get; }

    ImmutableArray<IOperation> Arguments { get; }

    IPropertySymbol? Indexer { get; }
}

public interface IIndexOperation : IOperation
{
    bool IsFromEnd { get; }

    IOperation? Value { get; }
}

public interface IRangeOperation : IOperation
{
    IOperation? Left { get; }

    IOperation? Right { get; }
}

public interface ITypeOfOperation : IOperation
{
}

public interface IObjectCreationOperation : IOperation
{
    IMethodSymbol? Constructor { get; }

    ImmutableArray<IOperation> Arguments { get; }

    IOperation? Initializer { get; }
}

public interface IObjectInitializerOperation : IOperation
{
    ImmutableArray<IOperation> Entries { get; }
}

public interface IObjectInitializerAssignmentOperation : IOperation
{
    ISymbol Member { get; }

    SyntaxKind OperatorTokenKind { get; }

    IOperation? Value { get; }
}

public interface IObjectInitializerExpressionEntryOperation : IOperation
{
    IOperation? Expression { get; }
}

public interface IAssignmentOperation : IOperation
{
    IOperation? Target { get; }

    IOperation? Value { get; }
}

public interface ILoopOperation : IOperation
{
    IOperation? Body { get; }
}

public interface IWhileLoopOperation : ILoopOperation
{
    IOperation? Condition { get; }
}

public interface IForLoopOperation : ILoopOperation
{
    IOperation? Collection { get; }

    IOperation? Pattern { get; }

    ILocalSymbol? Local { get; }

    ITypeSymbol ElementType { get; }
}

public interface ITupleOperation : IOperation
{
    ImmutableArray<IOperation> Elements { get; }
}

public interface ITryOperation : IOperation
{
    IOperation? Body { get; }

    ImmutableArray<ICatchClauseOperation> Catches { get; }

    IOperation? Finally { get; }
}

public interface ILockOperation : IOperation
{
    IOperation? LockedValue { get; }

    IOperation? Body { get; }
}

public interface ICatchClauseOperation : IOperation
{
    ITypeSymbol ExceptionType { get; }

    ILocalSymbol? Local { get; }

    IOperation? Body { get; }
}

public interface ITryExpressionOperation : IOperation
{
    IOperation? Operation { get; }

    ITypeSymbol ExceptionType { get; }

    IMethodSymbol OkConstructor { get; }

    IMethodSymbol ErrorConstructor { get; }
}

public interface ILambdaOperation : IOperation
{
    ImmutableArray<IParameterSymbol> Parameters { get; }

    ITypeSymbol ReturnType { get; }

    IOperation? Body { get; }

    ImmutableArray<INamedTypeSymbol> CandidateDelegates { get; }

    ImmutableArray<ISymbol> CapturedVariables { get; }
}

public interface IUnionCaseOperation : IOperation
{
    INamedTypeSymbol UnionType { get; }

    INamedTypeSymbol CaseType { get; }

    IMethodSymbol? Constructor { get; }

    ImmutableArray<IOperation> Arguments { get; }
}

public interface ISwitchOperation : IOperation
{
    IOperation? Value { get; }

    ImmutableArray<IOperation> Patterns { get; }

    ImmutableArray<IOperation> Guards { get; }

    ImmutableArray<IOperation> ArmValues { get; }
}

public interface IIsPatternOperation : IOperation
{
    IOperation? Value { get; }

    IOperation? Pattern { get; }
}

public interface IPatternOperation : IOperation
{
}

public interface ICasePatternOperation : IPatternOperation
{
    IUnionCaseTypeSymbol CaseSymbol { get; }

    IMethodSymbol TryGetMethod { get; }

    ImmutableArray<IOperation> Arguments { get; }
}

public interface IDeclarationPatternOperation : IPatternOperation
{
    ITypeSymbol DeclaredType { get; }

    IOperation? Designator { get; }
}

public interface IConstantPatternOperation : IPatternOperation
{
    object ConstantValue { get; }

    IOperation? Value { get; }
}

public interface IComparisonPatternOperation : IPatternOperation
{
    SyntaxKind OperatorKind { get; }

    IOperation? Value { get; }
}

public interface IPositionalPatternOperation : IPatternOperation
{
    ImmutableArray<IOperation> Subpatterns { get; }
}

public interface IReceiverPatternOperation : IPatternOperation
{
    ITypeSymbol ReceiverType { get; }

    ITypeSymbol? NarrowedType { get; }
}

public interface IRecursivePatternOperation : IReceiverPatternOperation
{
    IMethodSymbol DeconstructMethod { get; }

    ImmutableArray<IOperation> Arguments { get; }
}

public interface IRangePatternOperation : IPatternOperation
{
    IOperation? LowerBound { get; }

    IOperation? UpperBound { get; }

    bool IsUpperExclusive { get; }
}

public interface IPropertyPatternOperation : IReceiverPatternOperation
{
    IOperation? Designator { get; }

    ImmutableArray<ISymbol> Members { get; }

    ImmutableArray<IOperation> Subpatterns { get; }
}

public interface IDictionaryPatternOperation : IReceiverPatternOperation
{
    ITypeSymbol KeyType { get; }

    ITypeSymbol ValueType { get; }

    IOperation? Designator { get; }

    ImmutableArray<IOperation> Keys { get; }

    ImmutableArray<IOperation> Subpatterns { get; }
}

public interface IDiscardPatternOperation : IPatternOperation
{
}

public interface INotPatternOperation : IPatternOperation
{
    IOperation? Pattern { get; }
}

public interface IAndPatternOperation : IPatternOperation
{
    IOperation? Left { get; }

    IOperation? Right { get; }
}

public interface IOrPatternOperation : IPatternOperation
{
    IOperation? Left { get; }

    IOperation? Right { get; }
}

public interface IDesignatorOperation : IOperation
{
}

public interface ISingleVariableDesignatorOperation : IDesignatorOperation
{
    ILocalSymbol Local { get; }
}

public interface IDiscardDesignatorOperation : IDesignatorOperation
{
}

public interface ICollectionOperation : IOperation
{
}

public interface IDictionaryOperation : IOperation
{
    ImmutableArray<IOperation> Elements { get; }
}

public interface ICollectionComprehensionOperation : IOperation
{
    IOperation? Source { get; }

    IOperation? Condition { get; }

    IOperation? Selector { get; }

    ILocalSymbol IterationLocal { get; }

    ITypeSymbol ElementType { get; }
}

public interface IDictionaryElementOperation : IOperation
{
    IOperation? Key { get; }

    IOperation? Value { get; }
}

public interface IDictionarySpreadElementOperation : IOperation
{
    IOperation? Expression { get; }
}

public interface IDictionaryComprehensionOperation : IOperation
{
    IOperation? Source { get; }

    IOperation? Condition { get; }

    IOperation? KeySelector { get; }

    IOperation? ValueSelector { get; }

    ILocalSymbol IterationLocal { get; }

    ITypeSymbol KeyType { get; }

    ITypeSymbol ValueType { get; }
}

public interface IEmptyCollectionOperation : IOperation
{
}

public interface ISpreadElementOperation : IOperation
{
    IOperation? Expression { get; }
}

public interface ITypeOperation : IOperation
{
}

public interface INamespaceOperation : IOperation
{
}

public interface ISelfOperation : IOperation
{
}

public interface IUnitOperation : IOperation
{
}

public interface IInvalidOperation : IOperation
{
}
