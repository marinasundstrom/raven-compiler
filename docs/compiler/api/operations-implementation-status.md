# Operations API implementation status

This document records which bound nodes currently map to specialized operations, which ones only map to generic `SimpleOperation`, and which ones lack any operations kind mapping today. It is intended as a checklist for expanding the Operations API.

Sources of truth:
- `OperationKind` (enumeration of operation kinds)
- `OperationFactory` (mapping from bound nodes to operation kinds and operation implementations)
- Bound node definitions in `src/Raven.CodeAnalysis/BoundTree`

## Implemented operation kinds (specialized operation nodes)

| Operation kind | Bound node(s) | Specialized operation node |
| --- | --- | --- |
| `Block` | `BoundBlockStatement` | `BlockOperation` |
| `BlockExpression` | `BoundBlockExpression` | `BlockOperation` |
| `ExpressionStatement` | `BoundExpressionStatement` | `ExpressionStatementOperation` |
| `LocalDeclaration` | `BoundLocalDeclarationStatement` | `VariableDeclarationOperation` |
| `Function` | `BoundFunctionStatement` | `FunctionOperation` |
| `VariableDeclarator` | `BoundVariableDeclarator` | `VariableDeclaratorOperation` |
| `Return` | `BoundReturnStatement` | `ReturnOperation` |
| `ReturnExpression` | `BoundReturnExpression` | `ReturnExpressionOperation` |
| `BreakExpression` | `BoundBreakExpression` | `BreakExpressionOperation` |
| `ContinueExpression` | `BoundContinueExpression` | `ContinueExpressionOperation` |
| `YieldExpression` | `BoundYieldExpression` | `YieldExpressionOperation` |
| `Yield` | `BoundYieldStatement` | `YieldOperation` |
| `Throw` | `BoundThrowStatement` | `ThrowOperation` |
| `ThrowExpression` | `BoundThrowExpression` | `ThrowExpressionOperation` |
| `Break` | `BoundBreakStatement` | `BreakOperation` |
| `Continue` | `BoundContinueStatement` | `ContinueOperation` |
| `Goto` | `BoundGotoStatement` | `GotoOperation` |
| `ConditionalGoto` | `BoundConditionalGotoStatement` | `ConditionalGotoOperation` |
| `Labeled` | `BoundLabeledStatement` | `LabeledOperation` |
| `Literal` | `BoundLiteralExpression` | `LiteralOperation` |
| `DefaultValue` | `BoundDefaultValueExpression` | `DefaultValueOperation` |
| `LocalReference` | `BoundLocalAccess` | `LocalReferenceOperation` |
| `VariableReference` | `BoundVariableExpression` | `VariableReferenceOperation` |
| `ParameterReference` | `BoundParameterAccess` | `ParameterReferenceOperation` |
| `FieldReference` | `BoundFieldAccess` | `FieldReferenceOperation` |
| `PropertyReference` | `BoundPropertyAccess` | `PropertyReferenceOperation` |
| `MethodReference` | `BoundMethodGroupExpression` | `MethodReferenceOperation` |
| `Unary` | `BoundUnaryExpression` | `UnaryOperation` |
| `Binary` | `BoundBinaryExpression` | `BinaryOperation` |
| `Coalesce` | `BoundNullCoalesceExpression` | `CoalesceOperation` |
| `NameOf` | `BoundNameOfExpression` | `NameOfOperation` |
| `Parenthesized` | `BoundParenthesizedExpression` | `ParenthesizedOperation` |
| `Conversion` | `BoundConversionExpression`, `BoundAsExpression` | `ConversionOperation` |
| `Propagate` | `BoundPropagateExpression` | `PropagationOperation` |
| `Dereference` | `BoundDereferenceExpression` | `DereferenceOperation` |
| `ConditionalAccess` | `BoundConditionalAccessExpression`, `BoundCarrierConditionalAccessExpression` | `ConditionalAccessOperation` |
| `Conditional` | `BoundIfStatement`, `BoundIfExpression` | `ConditionalOperation` |
| `TryExpression` | `BoundTryExpression` | `TryExpressionOperation` |
| `Await` | `BoundAwaitExpression` | `AwaitOperation` |
| `WhileLoop` | `BoundWhileStatement` | `WhileLoopOperation` |
| `ForLoop` | `BoundForStatement` | `ForLoopOperation` |
| `Invocation` | `BoundInvocationExpression` | `InvocationOperation` |
| `ObjectCreation` | `BoundObjectCreationExpression` | `ObjectCreationOperation` |
| `ObjectInitializer` | `BoundObjectInitializer` | `ObjectInitializerOperation` |
| `ObjectInitializerAssignment` | `BoundObjectInitializerAssignmentEntry` | `ObjectInitializerAssignmentOperation` |
| `ObjectInitializerExpressionEntry` | `BoundObjectInitializerExpressionEntry` | `ObjectInitializerExpressionEntryOperation` |
| `With` | `BoundWithExpression` | `WithOperation` |
| `Assignment` | `BoundAssignmentExpression`, `BoundAssignmentStatement` | `AssignmentOperation` |
| `DelegateCreation` | `BoundDelegateCreationExpression` | `DelegateCreationOperation` |
| `Tuple` | `BoundTupleExpression` | `TupleOperation` |
| `Lambda` | `BoundFunctionExpression` | `LambdaOperation` |
| `UnionCase` | `BoundUnionCaseExpression` | `UnionCaseOperation` |
| `AddressOf` | `BoundAddressOfExpression` | `AddressOfOperation` |
| `ArrayElement` | `BoundArrayAccessExpression` | `ElementAccessOperation` |
| `IndexerElement` | `BoundIndexerAccessExpression` | `ElementAccessOperation` |
| `Index` | `BoundIndexExpression` | `IndexOperation` |
| `Range` | `BoundRangeExpression` | `RangeOperation` |
| `TypeOf` | `BoundTypeOfExpression` | `TypeOfOperation` |
| `Switch` | `BoundMatchExpression`, `BoundMatchStatement` | `MatchOperation` |
| `IsPattern` | `BoundIsPatternExpression` | `IsPatternOperation` |
| `CasePattern` | `BoundCasePattern` | `CasePatternOperation` |
| `DeclarationPattern` | `BoundDeclarationPattern` | `DeclarationPatternOperation` |
| `ConstantPattern` | `BoundConstantPattern` | `ConstantPatternOperation` |
| `ComparisonPattern` | `BoundComparisonPattern` | `ComparisonPatternOperation` |
| `PositionalPattern` | `BoundPositionalPattern` | `PositionalPatternOperation` |
| `RecursivePattern` | `BoundDeconstructPattern` | `RecursivePatternOperation` |
| `RangePattern` | `BoundRangePattern` | `RangePatternOperation` |
| `PropertyPattern` | `BoundPropertyPattern` | `PropertyPatternOperation` |
| `DiscardPattern` | `BoundDiscardPattern` | `DiscardPatternOperation` |
| `NotPattern` | `BoundNotPattern` | `NotPatternOperation` |
| `AndPattern` | `BoundAndPattern` | `AndPatternOperation` |
| `OrPattern` | `BoundOrPattern` | `OrPatternOperation` |
| `SingleVariableDesignator` | `BoundSingleVariableDesignator` | `SingleVariableDesignatorOperation` |
| `DiscardDesignator` | `BoundDiscardDesignator` | `DiscardDesignatorOperation` |
| `Collection` | `BoundCollectionExpression` | `CollectionOperation` |
| `CollectionComprehension` | `BoundCollectionComprehensionExpression` | `CollectionComprehensionOperation` |
| `EmptyCollection` | `BoundEmptyCollectionExpression` | `EmptyCollectionOperation` |
| `SpreadElement` | `BoundSpreadElement` | `SpreadElementOperation` |
| `TypeExpression` | `BoundTypeExpression` | `TypeOperation` |
| `NamespaceExpression` | `BoundNamespaceExpression` | `NamespaceOperation` |
| `SelfReference` | `BoundSelfExpression` | `SelfOperation` |
| `Try` | `BoundTryStatement` | `TryOperation` |
| `CatchClause` | `BoundCatchClause` | `CatchClauseOperation` |
| `Unit` | `BoundUnitExpression` | `UnitOperation` |
| `Invalid` | `BoundErrorExpression` | `InvalidOperation` |
| `FieldReference`/`PropertyReference`/`MethodReference` | `BoundMemberAccessExpression` | `MemberReferenceOperation` |
| `FieldReference`/`PropertyReference`/`MethodReference` | `BoundPointerMemberAccessExpression` | `MemberReferenceOperation` |

## Internal wrappers and fallback operations

Internal lowering wrappers do not require dedicated public operation kinds.
The wrappers below are intentionally unwrapped by `OperationFactory`; they are
not missing API mappings. Other fallback operations should be assessed against
a concrete consumer scenario before extending the public API.

Intentional internal wrappers (not exposed as dedicated operations):

- `BoundRequiredResultExpression` is a codegen-only wrapper and is unwrapped to its operand in the Operations API.
- `BoundNullableValueExpression` is an internal lowering/codegen helper and is unwrapped to its operand in the Operations API.

## Notes

- `OperationKind.None` is used as a fallback when no semantic shape is available.
- The status above is based on `OperationFactory` and `OperationKind` as of this snapshot.

## Recent API surface additions

The following operation interfaces were expanded with typed accessors in the
latest slices:

- `IConditionalOperation`: `Condition`, `WhenTrue`, `WhenFalse`
- `IConditionalAccessOperation`: `Receiver`, `WhenNotNull`
- `ILocalReferenceOperation`: `Local`
- `IVariableReferenceOperation`: `Variable`
- `IParameterReferenceOperation`: `Parameter`
- `IFieldReferenceOperation`: `Field`
- `IPropertyReferenceOperation`: `Property`
- `IMethodReferenceOperation`: `Method`
- `ISwitchOperation`: `Value`, `Patterns`, `Guards`, `ArmValues`
- `ICasePatternOperation`: `Arguments`
- `IConstantPatternOperation`: `Value`
- `IPositionalPatternOperation`: `Subpatterns`
- `IReceiverPatternOperation`: `ReceiverType`, `NarrowedType`
- `IRecursivePatternOperation`: `DeconstructMethod`, `Arguments`
- `IRangePatternOperation`: `LowerBound`, `UpperBound`, `IsUpperExclusive`
- `IComparisonPatternOperation`: `OperatorKind`, `Value`
- `IPropertyPatternOperation`: `Designator`, `Members`, `Subpatterns`
- `IPropagationOperation`: `Operand`, `OkType`, `ErrorType`, `EnclosingResultType`, `EnclosingErrorConstructor`
- `IDereferenceOperation`: `Operand`
- `ICollectionComprehensionOperation`: `Source`, `Condition`, `Selector`, `IterationLocal`, `ElementType`
- `IObjectCreationOperation`: `Initializer`
- `IObjectInitializerOperation`: `Entries`
- `IObjectInitializerAssignmentOperation`: `Member`, `OperatorTokenKind`, `Value`
- `IObjectInitializerExpressionEntryOperation`: `Expression`
- `INotPatternOperation`: `Pattern`
- `IAndPatternOperation`: `Left`, `Right`
- `IOrPatternOperation`: `Left`, `Right`
- `ITryOperation`: `Body`, `Catches`, `Finally`
- `ICatchClauseOperation`: `Body`
- `ITryExpressionOperation`: `Operation`, `ExceptionType`, `OkConstructor`, `ErrorConstructor`
- `IElementAccessOperation`: `Instance`, `Arguments`, `Indexer`
- `ITupleOperation`: `Elements`
- `ILambdaOperation`: `Parameters`, `ReturnType`, `Body`, `CandidateDelegates`, `CapturedVariables`
