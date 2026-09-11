# Compiler diagnostics

This page is the source-of-truth catalog for Raven compiler diagnostics and
links to runnable samples where relevant.

For configurable guidance reported by the analyzers bundled with Raven, see
[Built-in analyzers](analyzers/built-in.md). Compiler diagnostics define
language validity; analyzer diagnostics define project-selectable policy.

## How to inspect diagnostics from a sample

From `samples/`:

```bash
dotnet run --project ../src/Raven.Compiler --property WarningLevel=0 -- <sample.rav> --no-emit
```

Useful sample entry points:
- `samples/control-flow/throw-expression-basic.rav` for `try`/`catch`/`throw` diagnostics.
- `samples/functions/return-expression-basic.rav` for expression-context control-flow diagnostics.
- `samples/unmanaged/pointers.rav` for unsafe/pointer diagnostics.
- `samples/async/async-await.rav` and `samples/async/async-task-return.rav` for async/await diagnostics.
- `samples/patterns/match-expression.rav` and `samples/patterns/*` for match/pattern diagnostics.

Return/throw context note:
- `RAV1900` and `RAV1907` apply to inline expression contexts.
- Explicit `return <value>` and `throw` statements are valid in statement contexts (including statement-form `match` arms and supported block-expression early-exit regions).

## In-source suppression

Diagnostics can also be suppressed directly in source with comment-based pragmas:

```rav
#pragma warning disable RAV0103
missing
#pragma warning restore RAV0103
```

Use `disable`/`restore` without IDs to suppress or restore all diagnostics. `disable-next-line` suppresses only the next line. `// pragma warning ...` is also accepted.

## Diagnostics catalog

| ID | Severity | Title | Message template | Related samples |
| --- | --- | --- | --- | --- |
| `RAV0021` | Error | Not indexable | Not indexable | — |
| `RAV0023` | Error | Operator not supported | '{operatorToken}' is not supported for '{operandType}'. | — |
| `RAV0024` | Error | Operator not supported | '{operatorToken}' is not supported for '{operandType1}' and '{operandType2}'. | — |
| `RAV0027` | Error | Value is read-only | Value is read-only | — |
| `RAV0030` | Error | Not callable | Invalid invocation expression. | — |
| `RAV0103` | Error | Name not found | '{name}' is not in scope. | — |
| `RAV0111` | Error | Member already defined | Type '{typeName}' already defines a member called '{memberName}' with the same parameter types | — |
| `RAV0112` | Error | Function already defined | A function named '{name}' is already defined with the same parameter types | — |
| `RAV0117` | Error | Member not found | '{container}' has no member '{member}'. | — |
| `RAV0118` | Error | Value used as a type | '{name}' is a value, not a type. | — |
| `RAV0119` | Error | Namespace used as a type | '{name}' is a namespace, not a type. | — |
| `RAV0121` | Error | Ambiguous call | This call matches both '{method1}' and '{method2}'. | — |
| `RAV0131` | Error | Assignment target must be settable | Assignment target must be settable | — |
| `RAV0132` | Error | Type cannot be deconstructed | '{typeName}' does not support positional deconstruction. | — |
| `RAV0133` | Error | Deconstruction count mismatch | Expected {expectedCount} element(s) but found {actualCount}. | — |
| `RAV0134` | Error | Discard is not a value | '_' cannot be used as a value. | — |
| `RAV0135` | Error | Assignment is statement-only | Assignments can only appear as statements. | — |
| `RAV0149` | Error | Method name expected | Method name expected | — |
| `RAV0162` | Warning | Unreachable code | Unreachable code | — |
| `RAV0166` | Error | Missing initializer | '{name}' must be initialized. | — |
| `RAV0167` | Error | Variable already defined | '{name}' is already defined in this scope. | — |
| `RAV0168` | Warning | Variable shadows earlier name | '{name}' shadows a previous declaration. | — |
| `RAV0169` | Error | Const needs a constant value | '{name}' must be initialized with a compile-time constant. | — |
| `RAV0170` | Error | Const local initializer cannot be converted | The value for '{name}' cannot be converted to '{typeName}'. | — |
| `RAV0171` | Error | Const field requires a constant initializer | '{name}' must be initialized with a compile-time constant. | — |
| `RAV0172` | Error | Const field initializer cannot be converted | The value for '{name}' cannot be converted to '{typeName}'. | — |
| `RAV0173` | Error | Const field requires an initializer | '{name}' must have an initializer. | — |
| `RAV0174` | Error | Use before declaration | '{name}' is used before it is declared. | — |
| `RAV0176` | Error | External constant requires a value | External constant '{name}' requires a value. | — |
| `RAV0177` | Error | External constant value cannot be converted | Value '{value}' cannot be converted to '{typeName}' for external constant '{name}'. | — |
| `RAV0178` | Error | External constant requires an explicit type | External constant '{name}' must declare a type. | — |
| `RAV0179` | Info | External constant initializer is overridden | External constant '{name}' uses a build-supplied value instead of its source initializer. | — |
| `RAV0191` | Error | A readonly field cannot be assigned to (except in a constructor or a variable initializer) | A readonly field cannot be assigned to (except in a constructor or a variable initializer) | — |
| `RAV0200` | Error | '{name}' is read-only and cannot be assigned to. | '{name}' is read-only and cannot be assigned to. | — |
| `RAV0201` | Error | Event can only be used with '+=' or '-=' | The event '{name}' can only appear on the left hand side of '+=' or '-=' | `samples/oop/event-basic.rav` |
| `RAV0202` | Error | Event cannot be invoked | The event '{name}' cannot be invoked because it does not declare a backing field | `samples/oop/event-basic.rav` |
| `RAV0234` | Error | Type or namespace does not exist in namespace | '{typeOrNs}' was not found in '{container}'. | — |
| `RAV0235` | Error | Type expected without wildcard | Expected a type name (wildcards are not allowed here). | — |
| `RAV0240` | Error | Type does not support with-expressions | The type '{typeName}' does not support 'with' expressions | `samples/oop/with-expression-record-basic.rav`, `samples/oop/with-expression-record-advanced.rav` |
| `RAV0241` | Error | With-expression assigns a member multiple times | The member '{memberName}' is assigned more than once in the with-expression | `samples/oop/with-expression-record-basic.rav`, `samples/oop/with-expression-record-advanced.rav` |
| `RAV0269` | Error | Unassigned out parameter | Use of unassigned out parameter '{parameterName}' | — |
| `RAV0305` | Error | Type requires type arguments | The type '{name}' requires {arity} type argument(s) | — |
| `RAV0306` | Error | Cannot inherit from sealed type | Type '{typeName}' is sealed and cannot be inherited | — |
| `RAV0307` | Error | No virtual member to override | \'{memberName}': no suitable {memberType} found to override | — |
| `RAV0308` | Error | Virtual member in sealed type | Member '{memberName}' cannot be virtual because the containing type '{typeName}' is sealed | — |
| `RAV0309` | Error | 'final' modifier requires override | Member '{memberName}' cannot be marked 'final' because it is not an override | — |
| `RAV0310` | Error | Cannot override final member | Member '{memberName}' cannot override final member '{overriddenMemberName}' | — |
| `RAV0311` | Error | Static members cannot be virtual or override | Static member '{memberName}' cannot be marked '{modifier}' | — |
| `RAV0312` | Error | Static constructors cannot specify a base initializer | Static constructors cannot specify a base constructor initializer | — |
| `RAV0313` | Error | Explicit interface specifier must name an interface | Explicit interface specifier must name an interface type | — |
| `RAV0314` | Error | Type does not implement interface | Type '{typeName}' does not implement interface '{interfaceName}' | — |
| `RAV0315` | Error | Explicit interface member not found | Interface '{interfaceName}' does not contain a member named '{memberName}' matching this signature | — |
| `RAV0316` | Error | Optional parameter default value cannot be converted | The default value for optional parameter '{parameterName}' cannot be converted to type '{parameterType}' | — |
| `RAV0317` | Error | Optional parameters must be trailing | Optional parameter '{parameterName}' must be the last parameter in the list | — |
| `RAV0318` | Error | Default value must be a compile-time constant | Default value for parameter '{parameterName}' must be a compile-time constant | — |
| `RAV0319` | Error | Default value cannot convert to parameter type | Default value for parameter '{parameterName}' cannot be converted to '{parameterType}' | — |
| `RAV0320` | Error | Type argument does not satisfy constraint | The type '{typeArgument}' must satisfy the '{constraint}' constraint for type parameter '{typeParameter}' of '{genericName}' | — |
| `RAV0321` | Error | Operator must be static | Operator '{operatorToken}' must be declared static | — |
| `RAV0322` | Error | Operator must be public | Operator '{operatorToken}' must be declared public | — |
| `RAV0323` | Error | Operator parameter count is invalid | Operator '{operatorToken}' requires {expectedParameterDescription} parameter(s) | — |
| `RAV0324` | Error | Extension operators are not supported | Operator '{operatorToken}' overloads are not supported in extensions | — |
| `RAV0325` | Error | Operator declaration must be in class or struct | Operator '{operatorToken}' declarations are only supported in classes and structs | — |
| `RAV0326` | Error | Invalid modifier combination | Member '{memberName}' cannot be marked both '{modifier1}' and '{modifier2}' | — |
| `RAV0327` | Error | Static class cannot declare instance members | Static class '{typeName}' cannot declare instance member '{memberName}' | — |
| `RAV0328` | Error | Static type cannot be inherited | Type '{typeName}' is static and cannot be inherited | — |
| `RAV0329` | Error | Static type cannot be instantiated | Type '{typeName}' is static and cannot be instantiated | — |
| `RAV0330` | Error | Type does not implement abstract member | Type '{typeName}' does not implement abstract member '{memberName}' from base type '{baseTypeName}' | — |
| `RAV0331` | Warning | Member hides inherited member | Member '{memberName}' hides inherited member '{hiddenMemberName}'. Add the 'new' modifier if hiding was intended. | — |
| `RAV0332` | Error | Modifier not valid on member | Modifier '{modifier}' is not valid on {memberKind} '{memberName}' | — |
| `RAV0333` | Warning | Redundant open modifier on abstract class | Modifier 'open' is redundant on abstract class '{typeName}' | — |
| `RAV0340` | Warning | Redundant abstract modifier on sealed class | Modifier 'abstract' is redundant on sealed class '{typeName}' | — |
| `RAV0345` | Error | Record instance storage must come from primary constructor parameters | Record '{recordName}' cannot declare instance {memberKind} '{memberName}'; record instance data must come only from primary constructor parameters. | — |
| `RAV0346` | Error | Records cannot declare secondary constructors | Record '{recordName}' cannot declare instance constructors; use the primary constructor and static factory methods. | — |
| `RAV0400` | Error | Nullable type not allowed in union | Nullable types are not allowed in union types | `samples/nullability/nullable-reference-option-conversion-basic.rav`, `samples/nullability/nullable-value-option-conversion-basic.rav` |
| `RAV0401` | Error | Type parameter cannot be nullable | Type parameter '{typeParameter}' has a 'notnull' constraint and cannot be made nullable | `samples/nullability/nullable-reference-option-conversion-basic.rav`, `samples/nullability/nullable-value-option-conversion-basic.rav` |
| `RAV0402` | Error | Nullable value must be unwrapped | A member cannot be accessed through nullable type '{receiverType}'; unwrap or pattern-bind the value first | Static type error. See [Nullability and absence](../lang/nullability.md). |
| `RAV0403` | Warning | Nullable suppression used | The '!' operator treats the operand as non-null | Reported on the full `<expr>!` nullable suppression expression. |
| `RAV0404` | Error | Conditional access requires nullable receiver | Conditional access requires a nullable receiver; '{receiverType}' is not nullable | — |
| `RAV0405` | Error | Struct union argument may be default | Struct union argument for parameter '{parameterName}' of type '{unionType}' may be the inactive default state | — |
| `RAV0406` | Error | Struct union return value may be default | Struct union return value of type '{unionType}' may be the inactive default state | — |
| `RAV0410` | Error | Enum underlying type list must be a single type | Enum declarations may specify only one underlying type | `samples/oop/enum-basic.rav`, `samples/oop/enum-explicit-values-basic.rav` |
| `RAV0411` | Error | Enum underlying type must be integral | Enum underlying type must be a non-nullable integral type; '{typeName}' is not valid | `samples/oop/enum-basic.rav`, `samples/oop/enum-explicit-values-basic.rav` |
| `RAV0412` | Error | Enum member value must be constant | Enum member '{name}' must be initialized with a constant expression | `samples/oop/enum-basic.rav`, `samples/oop/enum-explicit-values-basic.rav` |
| `RAV0413` | Error | Enum member value cannot be converted | Enum member '{name}' value cannot be converted to '{typeName}' | `samples/oop/enum-basic.rav`, `samples/oop/enum-explicit-values-basic.rav` |
| `RAV0426` | Error | Type name does not exist in type | The type name '{name}' does not exist in the type '{container}' | — |
| `RAV0500` | Error | Symbol is inaccessible | The {symbolKind} '{name}' is inaccessible due to its visibility | — |
| `RAV0501` | Error | Type is less accessible than member | The {typeRole} type '{typeName}' is less accessible than {memberKind} '{memberName}' | — |
| `RAV0502` | Error | Attribute not valid for target | Attribute '{attributeName}' is not valid on target '{targetName}'. Valid targets are '{validTargets}' | — |
| `RAV0503` | Error | Attribute does not allow multiple | Attribute '{attributeName}' cannot be applied multiple times to the same '{targetName}' | — |
| `RAV0504` | Error | Attribute argument must be constant | Attribute argument must be a constant expression | — |
| `RAV0505` | Warning | Obsolete member usage | The {symbolKind} '{memberName}' is obsolete: {message} | — |
| `RAV0600` | Error | Type already defined | A type named '{name}' is already defined in this scope | — |
| `RAV0601` | Error | 'partial' modifier missing | Type '{name}' has multiple declarations; all declarations must include the 'partial' modifier | — |
| `RAV0602` | Error | Partial type accessibility mismatch | All partial declarations of type '{name}' must declare the same accessibility | — |
| `RAV0603` | Error | Partial type type parameters must match | All partial declarations of type '{name}' must declare the same type parameters in the same order | — |
| `RAV0604` | Error | Partial method is missing implementation | Partial method '{name}' must have an implementation part | — |
| `RAV0605` | Error | Partial method is missing declaration | Partial method '{name}' must have a declaration part without a body | — |
| `RAV0606` | Error | Partial method requires partial type | Partial method '{name}' must be declared inside a partial type | — |
| `RAV0607` | Error | Partial property is missing implementation | Partial property '{name}' must have an implementation part | — |
| `RAV0608` | Error | Abstract member cannot have a body | '{memberName}' cannot declare a body because it is marked abstract | — |
| `RAV0609` | Error | Partial property is missing declaration | Partial property '{name}' must have a declaration part without accessor bodies | — |
| `RAV0610` | Error | Abstract member declared in non-abstract type | '{memberName}' is abstract but is contained in non-abstract type '{typeName}' | — |
| `RAV0611` | Error | Cannot instantiate abstract type | Type '{typeName}' is abstract and cannot be instantiated | — |
| `RAV0612` | Error | Partial property requires partial type | Partial property '{name}' must be declared inside a partial type | — |
| `RAV0613` | Error | Partial property implementation cannot be auto | Partial property '{name}' implementation must provide at least one accessor body | — |
| `RAV0614` | Error | Partial event is missing implementation | Partial event '{name}' must have an implementation part | — |
| `RAV0615` | Error | Partial event is missing declaration | Partial event '{name}' must have a declaration part without accessor bodies | — |
| `RAV0616` | Error | Partial event requires partial type | Partial event '{name}' must be declared inside a partial type | — |
| `RAV0617` | Error | Partial event implementation cannot be field-like | Partial event '{name}' implementation must provide at least one accessor body | — |
| `RAV0815` | Error | Cannot assign void to an implicitly-typed variable | Cannot assign void to an implicitly-typed variable | — |
| `RAV0905` | Error | Required member must be set | Required member '{memberName}' must be set | `samples/oop/required-members-basic.rav` |
| `RAV0906` | Error | Required field must be mutable | Required field '{fieldName}' must be mutable | `samples/oop/required-members-basic.rav` |
| `RAV0908` | Warning | Redundant public modifier | Modifier 'public' is redundant because type members are public by default. | — |
| `RAV0909` | Error | Method declaration missing func keyword | Method declaration '{methodName}' must include the 'func' keyword. | — |
| `RAV0910` | Warning | val property set accessor must be restricted | Property '{propertyName}' is declared 'val' and its '{accessorKind}' accessor must be less accessible than the getter. | — |
| `RAV0911` | Warning | var property requires writable shape | Property '{propertyName}' is declared 'var' but has no writable shape. Add a 'set'/'init' accessor or a storage initializer. | — |
| `RAV0912` | Error | field keyword is only valid in property accessors | 'field' is only valid inside property 'get', 'set', or 'init' accessor bodies. | — |
| `RAV0913` | Error | field keyword requires property backing storage | Property '{propertyName}' does not have backing storage, so 'field' cannot be used here. | — |
| `RAV0914` | Error | Property declaration requires a binding keyword | Property '{propertyName}' requires an explicit binding keyword ('val' or 'var'). | — |
| `RAV0918` | Error | Property type annotation required | Property '{propertyName}' requires an explicit type annotation unless it has a storage initializer. | — |
| `RAV1000` | Error | Expression expected | Expression expected | — |
| `RAV1001` | Error | Identifier expected | Identifier expected | — |
| `RAV1002` | Error | Semicolon expected | ';' expected | — |
| `RAV1003` | Error | Character expected | '{character}' expected | — |
| `RAV1004` | Error | Duplicate modifier | Duplicate '{modifier}' modifier | — |
| `RAV1005` | Error | Import directives must come first | 'import' directives must appear before 'alias' and declarations. | — |
| `RAV1006` | Error | Alias directive out of order | Alias directives must appear before member declarations | — |
| `RAV1007` | Error | Field declaration requires a binding keyword | Field declarations must start with 'field' or 'const' | — |
| `RAV1008` | Error | Duplicate named argument | Named argument '{name}' specified multiple times | — |
| `RAV1009` | Error | Unrecognized escape sequence | Unrecognized escape sequence | — |
| `RAV1010` | Error | Newline in constant | Newline in constant | — |
| `RAV1012` | Error | File-scope code requires console application | Only console applications may contain file-scope code | — |
| `RAV1013` | Error | File-scope code may only appear in one file | File-scope code may only appear in one file | — |
| `RAV1014` | Error | Console application requires entry point | Entry point 'Main' not found | — |
| `RAV1015` | Error | try statement requires catch or finally | A try statement must include at least one catch clause or a finally clause | `samples/control-flow/throw-expression-basic.rav`, `samples/control-flow/try-catch-basic.rav` |
| `RAV1016` | Error | Catch type must derive from System.Exception | Type '{typeName}' is not derived from System.Exception | `samples/control-flow/throw-expression-basic.rav`, `samples/control-flow/try-catch-basic.rav` |
| `RAV1017` | Error | Program has more than one entry point defined | Program has more than one entry point defined | — |
| `RAV1018` | Error | File-scoped namespace out of order | File-scoped namespace declarations must appear before any other members | — |
| `RAV1019` | Error | End the statement with a newline or ';'. | End the statement with a newline or ';'. | — |
| `RAV1020` | Error | Thrown expression must be an exception type | Expression of type '{typeName}' cannot be thrown because it does not derive from System.Exception | `samples/control-flow/throw-expression-basic.rav`, `samples/control-flow/try-catch-basic.rav` |
| `RAV1021` | Error | Top-level statements not allowed with top-level Main | Top-level statements are not allowed when 'Main' is declared as a top-level function | — |
| `RAV1022` | Error | Invalid entry point signature | Entry point 'Main' must be static and return 'void', 'int', 'Unit', 'Task', 'Task<int>', 'Result<int, E>', 'Result<(), E>', 'Task<Result<int, E>>', or 'Task<Result<(), E>>' with zero parameters or a single 'string[]' parameter | — |
| `RAV1023` | Error | Expected newline between declarations | Expected newline between declarations. | — |
| `RAV1024` | Error | Catch pattern must be an exception type pattern | Catch pattern '{patternKind}' is not supported here. Use an exception type pattern such as 'Exception ex'. | — |
| `RAV1026` | Warning | List separators are inconsistent | Use either commas or newlines consistently to separate items in this list. | — |
| `RAV1050` | Error | Public extension requires an identifier | Public extension declarations must have an explicit identifier | `samples/extensions/extension-methods-basic.rav`, `samples/extensions/static-extensions.rav` |
| `RAV1501` | Error | No overload for method taking argument | No overload for {type} '{name}' takes {count} arguments | — |
| `RAV1503` | Error | Cannot convert from type to type | Cannot convert from '{fromType}' to '{toType}' | — |
| `RAV1504` | Error | Cannot assign to type | Cannot assign {fromType} to {toType} | — |
| `RAV1505` | Error | Multiple content entries not allowed | Type '{typeName}' has a settable 'Content' property; only one content entry is allowed in an initializer | — |
| `RAV1506` | Info | Result propagation applies implicit error conversion | Result propagation converts errors from '{fromType}' to '{toType}' via implicit conversion '{conversion}'. | — |
| `RAV1525` | Error | Invalid expression term | Invalid expression term '{tokenText}' | — |
| `RAV1602` | Error | Property not found in property pattern | Property '{name}' does not exist on type '{type}' | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1603` | Error | Property pattern type mismatch | Type '{inputType}' cannot be matched against property pattern type '{patternType}' | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1604` | Error | Property pattern requires a type | Property pattern requires an explicit type here because it cannot be inferred from the input | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1605` | Error | Relational pattern not supported for type | Relational pattern operator '{operatorToken}' is not supported for type '{typeName}' | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1606` | Error | Relational pattern value cannot be converted | Relational pattern value of type '{valueType}' cannot be converted to '{inputType}' | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1607` | Error | Pattern binding requires a binding keyword | Pattern binding '{name}: {typeName}' requires an explicit binding keyword ('let', 'var', or 'val'). | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1608` | Info | Bind a new value using 'let' or 'var' | If you meant to bind a new value in this pattern, use 'let {name}' (or 'var {name}'). | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1610` | Error | Nominal deconstruction pattern requires a deconstructable type | Nominal deconstruction pattern requires a deconstructable type; '{type}' does not support deconstruction | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1611` | Error | Nominal deconstruction pattern type mismatch | Type '{inputType}' cannot be matched against nominal deconstruction pattern type '{patternType}' | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1612` | Error | Nominal deconstruction pattern argument count mismatch | Type '{recordType}' expects {expected} deconstruction argument(s) but received {actual} | `samples/patterns/property-pattern-basic.rav`, `samples/patterns/record-pattern-basic.rav`, `samples/tuples/tuple-pattern-match-basic.rav` |
| `RAV1616` | Error | Let-else clause must exit | The else clause of a let-else statement must not complete normally | `samples/patterns/let-else-basic.rav`, `samples/nullability/nullable-let-else-basic.rav` |
| `RAV1900` | Error | Return statement not allowed here | Return statements are not valid in expressions; use an implicit return instead | `samples/functions/return-expression-basic.rav` |
| `RAV1901` | Error | If expression requires an else clause | If expressions used as values must include an else clause | `samples/functions/return-expression-basic.rav` |
| `RAV1902` | Error | Break statement not allowed here | Break statements are not valid in expressions; use a statement block instead | — |
| `RAV1903` | Error | Continue statement not allowed here | Continue statements are not valid in expressions; use a statement block instead | — |
| `RAV1904` | Error | Goto statement not allowed here | Goto statements are not valid in expressions; use a statement block instead | — |
| `RAV1905` | Error | Label not allowed here | Labels are not valid in expressions; use a statement block instead | — |
| `RAV1906` | Error | Nested try expression | Nested try expressions are not allowed | — |
| `RAV1907` | Error | Throw statement not allowed here | Throw statements are not valid in expressions; use a statement block instead | `samples/control-flow/throw-expression-basic.rav`, `samples/control-flow/try-catch-basic.rav` |
| `RAV1909` | Error | By-reference return cannot reference local storage | Cannot return a by-reference value that points to local '{localName}' | — |
| `RAV1910` | Error | By-reference return cannot reference value parameter storage | Cannot return a by-reference value that points to value parameter '{parameterName}' | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1911` | Error | Pointer type requires unsafe | Pointer types require unsafe mode ('--unsafe'). | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1912` | Error | Pointer operation requires unsafe | Pointer operations require unsafe mode ('--unsafe'). | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1918` | Error | Fixed expression requires use initializer | `fixed` is only valid as the initializer of a `use` declaration. | — |
| `RAV1919` | Error | Fixed expression requires address-of operand | `fixed` requires an explicit address-of operand written as `&expr`. | — |
| `RAV1920` | Error | Stack allocation requires an unmanaged element type | The stack allocation element type must not contain managed references. | — |
| `RAV1921` | Error | Stack allocation count must be an integer | The stack allocation count must be implicitly convertible to `int`. | — |
| `RAV1922` | Error | Stack allocation count cannot be negative | A negative constant stack allocation count is invalid. | — |
| `RAV1913` | Error | Dereference requires pointer or by-reference operand | Cannot dereference '{operandType}'. '*' requires a pointer or by-reference value. | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1914` | Error | Pointer member access requires pointer receiver | You can only use '->' on a pointer; '{receiverType}' is not a pointer. | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1915` | Error | Extern members must be static | Member '{memberName}' cannot be marked 'extern' unless it is static | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1916` | Error | Extern member cannot have a body | '{memberName}' cannot declare a body because it is marked extern | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1917` | Warning | Unsafe mode is enabled | Unsafe mode is enabled | `samples/unmanaged/pointers.rav`, `samples/unmanaged/test.rav` |
| `RAV1925` | Error | Removed try propagation syntax | The 'try?' syntax is no longer supported; use '(try expression)?' and propagate each carrier layer explicitly | — |
| `RAV1955` | Error | Non-invocable member | Non-invocable member '{memberName}' cannot be used like a method | — |
| `RAV2001` | Error | Numeric literal out of range | The numeric literal is out of range for its target type | — |
| `RAV2002` | Error | Unterminated character literal | Unterminated character literal | — |
| `RAV2003` | Error | Invalid escape sequence | Invalid escape sequence in character literal | — |
| `RAV2010` | Error | Target-typed member access requires a known type | Cannot resolve member '.{memberName}' without a known target type | — |
| `RAV2011` | Error | 'default' requires a target type | The 'default' literal can only be used when a target type is known | — |
| `RAV2020` | Error | Invalid alias target | Invalid alias target. Supported targets are types, namespaces, ad-hoc unions, tuples, and predefined types like bool, char, int, long, float, double, string, and unit. | — |
| `RAV2021` | Error | Invalid use target | You can only import namespaces and types. | — |
| `RAV2022` | Error | Spread source must be enumerable | Cannot spread expression of type '{typeName}' because it is not enumerable | — |
| `RAV2023` | Error | Single-element tuple type is not allowed | Single-element tuple types are not supported; use parenthesized type syntax only for grouping | — |
| `RAV2100` | Error | Match must cover all cases | Missing match case: '{missingType}'. | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2101` | Error | Match arm is unreachable | This match arm can never run (a previous arm matches first). | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2102` | Error | Match arm pattern is not valid | Pattern {patternType} is not valid for scrutinee of type '{scrutineeType}' | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2103` | Warning | Match catch-all is redundant | This catch-all never matches. | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2104` | Error | Case pattern requires union | Case pattern '{caseName}' requires a union target | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2105` | Error | Case not found on union | Union '{unionName}' does not contain a case named '{caseName}' | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2106` | Error | Case pattern payload count mismatch | Case '{caseName}' expects {expected} argument(s) but received {actual} | `samples/patterns/match-expression.rav`, `samples/patterns/match-type-pattern-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2107` | Warning | Match statement value is ignored | This match statement produces a value, but it will not be returned. Either add explicit 'return' statements in statement form, or use expression form where the surrounding context expects a value. | — |
| `RAV2108` | Warning | If statement value is ignored | This if statement produces values in its branches, but statement-form 'if' does not return a value. Either add explicit 'return' statements in statement form, or use expression form where the surrounding context expects a value. | — |
| `RAV2109` | Warning | Try statement value is ignored | This try statement produces values in its try/catch blocks, but statement-form 'try' does not return a value. Either add explicit 'return' statements in statement form, or use expression form where the surrounding context expects a value. | — |
| `RAV2110` | Warning | Match arm pattern does not fully cover subtype/case | Pattern for '{typeName}' does not cover all values. Add broader sub-patterns or a catch-all arm. | `samples/oop/sealed-record-hierarchy-json-basic.rav`, `samples/unions/union-basic.rav` |
| `RAV2111` | Error | Union member name is reserved | Union '{unionName}' reserves the member name '{memberName}' for synthesized members. | — |
| `RAV2112` | Error | Union special member not supported | Union '{unionName}' does not support authored member '{memberName}'. | — |
| `RAV2116` | Error | Union requires a case | Union '{unionName}' must declare at least one case | — |
| `RAV2117` | Error | Parenthesized union requires two variants | Parenthesized union '{unionName}' must declare at least two variant types | — |
| `RAV2200` | Error | Lambda parameter type cannot be inferred | Cannot infer the type of parameter '{parameterName}'. Specify an explicit type or use the lambda in a delegate-typed context | — |
| `RAV2201` | Error | Method group requires delegate type | Method group '{methodName}' cannot be used as a value without a delegate type. Specify a delegate annotation or use the method in a target-typed context | — |
| `RAV2202` | Error | Method group conversion is ambiguous | Method group '{methodName}' is ambiguous in this context. Specify a delegate type to disambiguate the target overload | — |
| `RAV2203` | Error | No overload matches delegate | No overload for method '{methodName}' matches delegate type '{delegateType}' | — |
| `RAV2500` | Error | Label already defined | A label named '{name}' is already defined in this scope | `samples/control-flow/goto-statement-basic.rav` |
| `RAV2501` | Error | Label not found | The label '{name}' is not in scope | `samples/control-flow/goto-statement-basic.rav` |
| `RAV2502` | Error | Reserved word cannot be used as a label | The identifier '{name}' is a reserved word and cannot be used as a label | `samples/control-flow/goto-statement-basic.rav` |
| `RAV2600` | Error | Break statement not within loop | Break statements are only valid inside loops | — |
| `RAV2601` | Error | Continue statement not within loop | Continue statements are only valid inside loops | — |
| `RAV2606` | Error | Label does not identify an enclosing loop | The label '{name}' does not identify an enclosing loop | — |
| `RAV2602` | Error | Range for-loop requires an end value | Ranges in for loops must specify an end value | — |
| `RAV2603` | Error | Range for-loop does not support from-end indices | Range boundaries in for loops must count from the start | — |
| `RAV2604` | Error | Range for-loop step cannot be zero | Range for-loop step cannot be zero | — |
| `RAV2605` | Error | Range for-loop by-clause requires a range | The 'by' clause is only supported for range-based for loops | — |
| `RAV2700` | Error | await needs async | The 'await' operator can only be used within an async method or function | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2701` | Error | Not awaitable | The type '{typeName}' is not awaitable | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2702` | Error | Awaiter is missing IsCompleted | The awaiter type '{awaiterType}' must have an accessible instance property 'IsCompleted' returning 'bool' | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2703` | Error | Awaiter is missing GetResult | The awaiter type '{awaiterType}' must have an accessible parameterless 'GetResult' method | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2704` | Error | Async return type must be Task-like | Async return type '{returnType}' is not supported; async functions must return Task-like types such as `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`. The diagnostic suggests the concrete `Task<...>` wrapper Raven expects when the annotated type is a plain result type. | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/async/async-valuetask.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2705` | Error | Async Task return cannot include a value | Since '{methodName}' is an async method that returns 'Task', a return keyword cannot include a value | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2706` | Warning | Async body lacks await | This async {memberDescription} has no 'await' and will run synchronously. | `samples/async/async-await.rav`, `samples/async/async-task-return.rav`, `samples/entrypoints/main-function-async-task-basic.rav` |
| `RAV2800` | Error | Pipe target must be invocable | The pipe operator requires an invocation or settable property access on the right-hand side | `samples/operators/pipe-operator-method-call-basic.rav`, `samples/operators/pipe-operator-lambda-basic.rav`, `samples/operators/pipe-operator-terminal-call-basic.rav` |
| `RAV2801` | Error | 'self' is not available in static context | 'self' is not available in a static context. | — |
| `RAV3600` | Error | Unexpected token | Unexpected token '{token}' | — |
| `RAV3601` | Error | Unmatched character | Unmatched '{character}' | — |
| `RAV4000` | Warning | Documentation comment is not attached | Documentation comments must appear immediately before a declaration | `samples/runtime/documentation-comment-basic.rav` |
| `RAV4001` | Warning | Documentation comment is not valid XML | Documentation comment is not well-formed XML: {message} | `samples/runtime/documentation-comment-basic.rav` |
| `RAV4002` | Warning | Documentation comment is not valid Markdown | Documentation comment is not well-formed Markdown: {message} | `samples/runtime/documentation-comment-basic.rav` |
| `RAV4003` | Warning | Documentation comment indentation is inconsistent | Documentation comment lines must have consistent indentation | `samples/runtime/documentation-comment-basic.rav` |
| `RVNGEN001` | Error | Source generator failure | Source generator '{generatorName}' failed: {message} | — |
| `RVNJS001` | Error | Unsupported JavaScript import | JavaScript import '{methodName}' is not supported by the current Raven interop generator: {reason} | `samples/projects/browser-wasm` |
| `RVNJS002` | Error | Unsupported JavaScript export | JavaScript export '{methodName}' is not supported by the current Raven interop generator: {reason} | `samples/projects/browser-wasm` |

## Behavior notes

### Struct-union boundaries

`RAV0405` and `RAV0406` protect struct-union call and return boundaries. A
struct-union value must be known active before it is passed to a struct-union
parameter or returned from a member. Values that may still be the
zero-initialized carrier state, such as `default(U)` or a parameter forwarded
unchanged, are rejected at the boundary. Omitting an optional struct-union
argument whose default is the carrier default is treated the same way at the
call site.

### Named deconstruction

Raven also uses `RAV1602` for named deconstruction elements such as
`Person(Name: let name, Age: 42)` and `let (Name: name, Age: age) = person`
when a supplied name does not match a supported `Deconstruct` parameter or
member on the target type.

### Match exhaustiveness

For struct unions, `RAV2100` considers the declared semantic case set. The
inactive default carrier state is not a formal source case; boundary diagnostics
such as `RAV0405` and `RAV0406` prevent it from crossing ordinary call and return
boundaries, and runtime matching retains a defensive fallback.

For a nullable union carrier `U?`, `RAV2100` requires the declared union cases
plus `null`. That `null` is the nullable wrapper state, not a union pseudo-case.
`RAV2103` reports a catch-all only when no declared case, nullable wrapper
`null`, or possible inactive default state remains.

The `RAV2100` quick fix adds every missing arm for the selected match in one
action. It chooses an editable pattern suited to the scrutinee:

* sealed class hierarchies use typed bindings, such as `Identifier identifier`
* sealed record hierarchies use positional bindings, such as `Case(let no)`
* ad-hoc unions use typed bindings, such as `int value`
* nominal case-declaration unions use target-typed cases, such as `.Success(let value)`
* enums use target-typed constants, such as `.None`
* finite literal cases retain their literal, such as `false` or `null`

When one case is absent, the action names that arm. When several are absent,
VS Code offers one **Add all missing match arms** action. The fix applies only
to the selected match; document- or project-wide fix-all behavior is separate.
