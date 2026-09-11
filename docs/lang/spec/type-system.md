# Type system

Raven is statically typed. Every expression has a type, and incompatible
operations are diagnosed before the program runs.

```raven
let count = 3             // int
let name: string = "Ada"
let optional: string? = null
```

Raven uses the .NET type system directly, so existing framework and library
types can be used without wrappers. At the same time, Raven gives some CLR
shapes a consistent source-language meaning. Nullable reference and value types,
for example, are both written and analyzed as `T?` even though .NET represents
them differently in metadata.

## Type annotations

Use type annotations where inference is insufficient or where a particular
target type is required. Locals commonly infer their type from the initializer;
function-expression parameters can infer from a delegate target; ordinary
function and method parameters still declare their parameter types explicitly:

```raven
let a = 2
let b: int = 2

func add(a: int, b: int) -> int { a + b }
```

## Type identity

Type identity follows CLR identity unless Raven defines a source-level form such
as nullable annotations, function types, or unions.

A type alias is another name for an existing type, not a new type. It therefore
participates in assignment, conversion, and overload resolution exactly as its
target does:

```raven
alias UserId = int

let id: UserId = 42
let number: int = id
```

Aliases remain transparent inside tuples and unions, so two otherwise identical
types do not become distinct merely because one spelling uses an alias.

## Built-in types

Raven provides keywords for the most common .NET types:

| Raven keyword | .NET type | Meaning |
| --- | --- | --- |
| `sbyte` | `System.SByte` | 8-bit signed integer |
| `byte` | `System.Byte` | 8-bit unsigned integer |
| `short` | `System.Int16` | 16-bit signed integer |
| `ushort` | `System.UInt16` | 16-bit unsigned integer |
| `int` | `System.Int32` | 32-bit signed integer |
| `uint` | `System.UInt32` | 32-bit unsigned integer |
| `long` | `System.Int64` | 64-bit signed integer |
| `ulong` | `System.UInt64` | 64-bit unsigned integer |
| `nint` | `System.IntPtr` | Native-sized signed integer |
| `nuint` | `System.UIntPtr` | Native-sized unsigned integer |
| `float` | `System.Single` | 32-bit floating point |
| `double` | `System.Double` | 64-bit floating point |
| `decimal` | `System.Decimal` | 128-bit decimal floating point |
| `bool` | `System.Boolean` | `true` or `false` |
| `char` | `System.Char` | UTF-16 code unit |
| `string` | `System.String` | UTF-16 text |
| `object` | `System.Object` | Base type of .NET reference types |
| `unit` | `System.Unit` | The single no-result value `()` |

Other .NET types can be referenced by qualified name, imported name, or alias.

`unit` is a value type and can flow through generics, tuples, and unions. It is
described in [Values, expressions, and
statements](values-and-statements.md#the-unit-value).

`null` is a literal, not a standalone type. It can flow only to nullable
locations and never satisfies a non-nullable binding or parameter.

## Literal types

Literals use their ordinary primitive types. For example, `1` is an `int`,
`"hello"` is a `string`, and `true` is a `bool`:

```raven
let one = 1
let text = "hello"
let enabled = true

let widened: double = one
```

When no target type changes the result, an inferred binding uses the literal's
standard primitive type. Literal values are not supported in type position.

See [Expressions and type inference](expressions-and-inference.md) for numeric
suffixes, target typing, and branch inference.

## Arrays

`T[]` is a one-dimensional array whose length is not part of its source type.
`T[N]` carries a fixed length:

```raven
let open: int[] = [1, 2, 3]
let fixed: int[3] = [1, 2, 3]
```

Array element types retain their generic arguments and nullability. Raven also
uses CLR multidimensional array shapes such as `T[,]` where that syntax is
accepted.

A fixed-length `T[N]` implicitly converts to `T[]`. The reverse conversion is
not implicit. `T[N]` converts to another fixed-length `T[M]` only when `N` and
`M` are equal.

Fixed-length inference is conservative. Raven infers or validates a length only
when the collection expression makes its element count directly available.
See [Collection expressions](collection-expressions.md#array-targets).

Like CLR arrays, a one-dimensional `T[]` implements `IEnumerable<T>`,
`ICollection<T>`, `IList<T>`, and their read-only counterparts. These
relationships participate in normal interface conversions. Multidimensional
arrays provide the non-generic `System.Collections.IEnumerable` relationship
instead.

## Tuple types

Tuple types describe a small group of values without requiring a named type.

Tuple types use parentheses with comma-separated element types and map to
`System.ValueTuple`:

```raven
let pair: (int, string) = (42, "answer")
```

Elements may optionally be named with a `name: Type` pair. Names exist only for
developer clarity and do not participate in type identity or assignment:

```raven
let tuple2: (id: int, name: string) = (no: 42, identifier: "Bar")
```

When a tuple expression is assigned to an explicitly annotated tuple type, each
element is validated against the corresponding element type. Named tuple
expressions expose both their source names and the positional `ItemN` members:

```raven
let tuple = (a: 42, b: 2)
Console.WriteLine(tuple.a)
Console.WriteLine(tuple.Item1)
```

Tuple types may nest or participate in other type constructs such as unions or
nullability.

## Function types

Function types describe callable delegates so they can be stored, passed as
arguments, or returned from other functions. Their syntax mirrors a lambda
signature: a comma-separated parameter list followed by `->` and the return
type.

```raven
let applyTwice: ((int -> int), int) -> int
let thunk: () -> unit
let comparer: (string, string) -> bool
```

In declaration-oriented lists, a newline may separate entries where an explicit
separator would otherwise appear. Omitting the separator between entries on the
same line is an error.

Single-parameter functions may omit the surrounding parentheses:

```raven
let increment: int -> int
```

The return portion may itself be any Raven type. Nested arrows associate to the
right, so `int -> string -> bool` is parsed as `int -> (string -> bool)`.

Function annotations are sugar over delegates. When the parameter and return
types match an existing declaration (including the built-in `Func`/`Action`
families), the compiler binds to that delegate. Otherwise it synthesizes an
internal delegate with the appropriate signature so interop with .NET remains
transparent. Parameter modifiers and names are not permitted inside a function
type; specify only the types that flow into and out of the delegate. A `unit`
return represents an action with no meaningful result.

Function-expression syntax, including explicit `func` expressions, lambda
shorthand, modifiers, and named recursive forms, is described under [Function
expressions](functions.md#function-expressions).

When a function expression is target-typed by a delegate requirement (for
example, assignment to `Action<int>` or passing to a delegate-typed parameter),
Raven projects the function value to a compatible delegate. Built-in
`Func`/`Action` delegate shapes are displayed as function signatures in Raven
type displays, while custom delegate types remain visibly named delegates.

Union syntax and declarations are covered separately under [Unions](unions.md).
Function expressions and delegate selection are covered under
[Functions](functions.md), and span lifetime rules under [Spans and stack
allocation](spans-and-memory.md).

## Nullable types

Nullability is explicit and uniform. Append `?` when a reference or value type
may contain `null`:

```raven
let name: string? = null
let count: int? = 1
```

Plain `T` rejects `null`; `T?` accepts `T` or `null`. Nullable and non-nullable
forms are distinct during type checking and overload resolution.

For a value type, .NET represents `T?` as `System.Nullable<T>`. For a reference
type, the runtime representation remains `T` and nullable-reference metadata
records the annotation. This ABI difference does not change Raven's
source-level rules. The expression's static type remains `T?`; Raven does not
silently replace it with a separate flow-sensitive type after a null check.

Explicit `System.Nullable<T>` remains available when interop code needs the CLR
wrapper and its members. It is not the canonical Raven spelling of `T?`.

### Handling a nullable value

A successful typed binding creates a new non-null value:

```raven
func inspect(value: string?) {
    if let text: string = value {
        Console.WriteLine(text.Length)
    }

    if value is string text {
        Console.WriteLine(text.Length)
    }
}
```

By default, a direct `is null`, `is not null`, `== null`, or `!= null` check does not change
the static type of the checked storage. Dereferencing the original `value`
inside such a branch remains an error when it has type `string?`. Reference and
value types follow the same rule.

Projects can enable `<EnableIsNotNullNarrowing>true</EnableIsNotNullNarrowing>`
to narrow an immutable local or parameter inside the true branch of a direct
`value is not null` check. The declared symbol and uses outside that branch
remain nullable. This option does not narrow mutable locals or enable general
flow analysis through equality checks, boolean combinations, or early returns.
See [Opt-in compatibility narrowing](../nullability.md#opt-in-compatibility-narrowing).

Prefer `is null` and `is not null` for identity tests. `== null` and `!= null`
are valid but may invoke user-defined equality. An analyzer warns about that
difference for non-pointer values:

> This comparison may call a custom equality operator. Use `is null` or `is not
> null` to test null identity.

### Nullable suppression

Postfix `!` treats one nullable expression as non-null:

```raven
let name = service.TryGetName()! // string? becomes string
let value = optionalNumber!      // int? becomes int
```

For a nullable reference, suppression changes the static type without inserting
a runtime null check. For a nullable value type, it unwraps the value. The
effect applies only to the annotated expression and reports warning `RAV0403`
on that full expression.

The reference-type behavior has the same runtime intent as C#'s null-forgiving
operator: `!` does not validate a non-null assertion. A null reference remains
null, and a later dereference can fail. Raven's nullable value-type behavior is
an extension: `int?` becomes `int` by extracting `Nullable<int>.Value`, which
throws `InvalidOperationException` when no value is present. C#'s `!` alone
does not perform that extraction. See the
[C# null-forgiving operator reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/null-forgiving).

Raven recommends `Option<T>` when absence is an intentional part of a domain
API. Nullable types remain useful for .NET interoperability and gradual
adoption. See [Nullability and absence](../nullability.md).

## Generics

Generic types and functions declare placeholders inside `<...>`:

```raven
class Box<T> {
    val Value: T { get; }

    init(value: T) {
        Value = value
    }
}

func identity<T>(value: T) -> T => value

let box = Box<string>("hello")
let number = identity(42) // T is inferred as int
```

Type parameters are in scope throughout their declaration. Constructed generic
types use ordinary CLR generic instantiations and interoperate directly with
.NET libraries.

A call can provide type arguments explicitly or let Raven infer them from
arguments and the expected result. If those inputs do not produce one
consistent choice, the type arguments must be written.

### Constraints

Constraints restrict acceptable type arguments. They can follow a type
parameter after `:` or appear in a `where` clause:

```raven
class Repository<TContext: class, IDisposable> {
    init(context: TContext) {
        // ...
    }
}

func parse<T>(text: string) -> T
    where T: IParsable<T>
    => T.Parse(text, null)
```

`class` requires a reference type and admits nullable references. `struct`
requires a non-nullable value type and excludes `Nullable<T>`. Nominal class
and interface constraints require the argument to inherit or implement those
types. Several constraints are conjunctive.

Constraints also make static abstract interface members available through a
type parameter, as in the `IParsable<T>` example.

Constraint satisfaction is transitive: substituting one constrained type
parameter for another carries its constraint set. A violation identifies the
type argument and unmet constraint.

Function-specific constraint forms and ordering are described under [Generic
functions](functions.md#generic-functions).

### Variance

An interface or delegate type parameter may be covariant with `out`,
contravariant with `in`, or invariant when no modifier is written:

```raven
interface Mapper<in TSource, out TResult> {
    func Map(source: TSource) -> TResult
}
```

Covariance allows `Producer<Derived>` where `Producer<Base>` is expected.
Contravariance allows `Consumer<Base>` where `Consumer<Derived>` is expected.
Invariant constructed types remain unrelated even when their arguments inherit
from one another.

The rules apply equally to Raven declarations and imported .NET metadata. For
example, `IEnumerable<string>` converts to `IEnumerable<object>`, while
`IComparer<object>` can be used where `IComparer<string>` is required.

## Target typing and inference

Target typing lets surrounding code determine a type. It is used by literals,
collection expressions, leading-dot construction, function expressions, method
references, and control-flow expressions.

Inferred unions are normalized so their branch set remains stable across
compilations. Literal expressions use their primitive type without a target,
while control-flow branches contribute their types and may infer a union.

See [Expressions and type inference](expressions-and-inference.md) for the full
set of inference rules.

## Conversions

An implicit conversion is allowed when it cannot lose the intended value under
Raven and .NET rules. Common implicit conversions include:

* identity conversion
* `null` to a nullable type
* `T` to `T?`
* widening numeric conversions
* a reference to a base class or implemented interface
* boxing a value type
* conversion to a compatible union branch
* variant conversions on covariant or contravariant generic types

When converting to a union, the source must convert to at least one branch. When
converting from a union, every possible branch must convert to the destination.
These rules also participate in assignment and overload selection.

### Explicit casts

Use a cast when a conversion can fail or lose information:

```raven
let widened = (double)1
let narrowed = (int)3.14
let text = value as string
```

`(T)expression` performs the requested conversion and throws
`InvalidCastException` when a runtime reference conversion fails. `expression
as T` attempts a reference or nullable conversion and produces `null` when it
fails.

## Overload resolution

When several callable candidates are applicable, Raven compares the implicit
conversions required for their parameters. Identity conversions are preferred
over numeric widening, followed by reference and boxing conversions.
User-defined conversions are considered last. If no applicable candidate is
strictly better, the call is ambiguous.

When candidates in the same overload set use
`System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute`, Raven
first keeps only candidates with the highest priority, then performs the normal
conversion and specificity comparison. This applies to Raven declarations and
imported .NET methods.

Calls, named arguments, optional arguments, and collector parameters are covered
under [Calls](invocations.md). Method-reference overload selection is described
under [Functions as values](functions.md#functions-as-values).
