# Local declarations

Local declarations give names to values used within a function or block. The
declaration says whether a value can change, is known at compile time, or needs
to be cleaned up when the scope ends.

Raven uses type inference wherever a declaration or target-typed context supplies
enough information. Local bindings usually do not need explicit type
annotations; write one when inference is ambiguous, when an empty or otherwise
targetless expression needs a type, or when the binding must have a specific
supertype or converted type.

### Read-only lexical binding (`let`)

A `let` binding is **immutable** (not reassignable). Types are inferred
unless annotated. A single declaration may declare multiple bindings by
separating declarators with commas.

```raven
let x = "Foo"
let y: int = 2
let a = 1, b = 2
let a: int = 2, b: string = ""
```

Raven also accepts `val` as an alternative spelling for an immutable local.

### Variable binding (`var`)

A `var` binding is **mutable** (reassignable). A single declaration may
declare multiple bindings by separating declarators with commas.

```raven
var x = "Foo"
x = "Bar"

var y: int = 2
y = 3

var left = 1, right = 2
left = left + right
```

### Constant binding (`const`)

A `const` binding is immutable like `val` but additionally requires a compile-time
constant initializer. The compiler stores the resulting value in metadata so it
can be referenced from other assemblies without executing an initializer.

```raven
const pi: double = 3.141592653589793
const banner = "Ready"      // inferred string constant
```

Const bindings support the primitive constant forms recognized by .NET: numeric and
character literals (including the appropriate suffixes), `true`/`false`, strings, and
`null` for reference types. Type inference works the same way as `val`; the initializer
must still be a compile-time constant.

Const applies to local bindings, type fields, and top-level constant members.
Member declarations treat `const` fields as implicitly `static`; the value is
emitted as metadata so other assemblies can import it without running an
initializer.

### Shadowing

A later declaration in the same scope may reuse, or shadow, an earlier local or
parameter name. Following code refers to the newer declaration:

```raven
let answer = 41
let answer = answer + 1 // RAV0168 (warning)
```

Each declaration introduces a distinct symbol. Shadowing is allowed for both
immutable and mutable bindings, but warning `RAV0168` helps catch accidental
redeclarations. Parameters of the enclosing function count as earlier
declarations and produce the same warning when shadowed.

Positional deconstruction lets you bind or assign multiple values at once. The outer
`val`/`var` controls the default mutability for shorthand forms, while each element
uses a designation (possibly nested) to capture or discard the corresponding value.
Elements may include inline type annotations when the inferred element type is
not the desired target type. Positional deconstruction works with tuples and
with any type that exposes a compatible `Deconstruct` method (including as an
extension method).
Elements may also be named as `Name: pattern`. For `Deconstruct`-based
deconstruction, named elements bind by parameter name and may be written in any
order; unnamed elements continue to consume the remaining unmatched parameters
in declaration order. Unknown names are diagnosed.

A named element's right side is always a nested pattern. Because `Name: target:
Type` is ambiguous between a named subpattern and a typed target, an outer
binding keyword may bind `Name: target` but does not make `Name: target: Type`
valid. Use the explicit nested-binding form, for example `Name: let target:
Type`, on a surface that does not also supply an outer binding keyword, or use an
unnamed typed element such as `target: Type` when the element is selected
positionally. The same rule applies recursively in match arms,
`if let`/`while let` pattern statements, `for let` pattern targets, and
comprehension targets.

This is a **deconstruction** surface, not a full general pattern-matching
surface. The left-hand side must start with positional syntax (`(...)`) or
sequence syntax (`[...]`), and nested subpatterns must remain within the
deconstruction family.

Supported as deconstruction heads:

* positional heads: `( ... )`
* sequence heads: `[ ... ]`
* nested positional/sequence subpatterns
* declaration/capture forms, discards, typed designations, and supported
  explicit value checks inside those positional/sequence shapes

Not currently supported as deconstruction heads:

* property patterns: `Type { ... }`
* nominal deconstruction / union-case heads: `Type(...)`, `Some(...)`, `.Case(...)`
* pure comparison heads such as `>= 18`
* range heads such as `1..10`
* boolean-combinator heads such as `p1 and p2`, `not p`

For element type matching and capture, Raven accepts both:

* `name: Type` when an outer binding keyword supplies the capture mode
* `let name: Type`
* `Type name` (type-pattern style)

```raven
let (first, second, _) = (1, 2, 3)
var (head, tail: double, _) = numbers()
(first, second, _) = next()
(let lhs, var rhs: double, _) = evaluate()
(int id, string name) = getTuple()
(let id: int, let name: string) = getTuple()
(lhs, == expectedRhs) = evaluate()
let (Items: items, Name: name, Age: age) = person
let [key: string, value: int] = entries
```

The label-plus-typed-target form is invalid:

```raven
// invalid; write (Name: let name: string) without an outer binding keyword,
// or use an unnamed typed element such as name: string
let (Name: name: string) = person
```

Existing locals can participate in positional assignments alongside new
bindings. Mixed `let`/`var` designations and inline type annotations are
supported in both declarations and assignments:

```raven
var first = 0
var second = 0

(first, second, _) = (1, 2, 3)
let (third, fourth: double, _) = toTuple()
var (let fifth, var sixth: double, _) = project()
```

When a deconstruction target has an explicit type annotation, or when it assigns
into an existing local/field/property, Raven uses that target type for the
captured value and applies the normal implicit-conversion rules. If the
deconstructed element cannot be converted to the target type, the compiler
reports the conversion diagnostic at the target type annotation or assignment
target.

Collection deconstruction uses the same element-pattern rules but with bracket
syntax. In assignments and declarations, collection deconstruction supports
arrays (`T[]`) and indexable collection types (for example `IList<T>` /
`IReadOnlyList<T>`), and elements are bound in sequence order.

In compiler APIs, this bracketed form is surfaced as `SequencePatternSyntax`
rather than `PositionalPatternSyntax`, so tuple/positional and sequence forms
can evolve independently.

For declaration-style deconstruction, Raven supports both:

* explicit per-element binding keywords: `[let a, let b, _] = values`
* shorthand outer binding keyword: `let [a, b, _] = values`

The shorthand is equivalent to applying the same binding keyword to each
identifier element (`let`, `var`, or `val`), matching tuple deconstruction forms
like `let (a, b) = expr`.

These two declaration styles are mutually exclusive in a single deconstruction:
choose either an outer/common binding keyword or per-element binding keywords.
Mixing both is invalid.

```raven
// valid
var [first, second, _] = values
[let first, let second, _] = values

// invalid
let [let first, let second, _] = values
let (let id, let name) = obj
```

```raven
let values: int[] = [1, 2, 3]
[first, second, _] = values
[var head, var tail, _] = values
let [first2, second2, _] = values
var [head2, tail2, _] = values

import System.Collections.Generic.*
let list: List<int> = [1, 2, 3]
[a, b, _] = list
let [c, d, _] = list
```

Use `_` to discard unwanted elements. Nested positional patterns work the same way:

```raven
var ((x, y), let magnitude, _) = samples()
```

Nested sequence/positional patterns can also be combined:

```raven
let [(first, second), [head, ..tail]] = value
```

In freestanding and inline patterns, plain identifiers do not implicitly refer
to runtime values or introduce new bindings. Use `== existingValue` to compare,
or `let`/`var`/`val` to capture a value. In assignment and
declaration deconstruction, plain identifiers remain binding targets by
default. To match against an existing runtime value in a positional pattern,
you can still use an explicit value pattern:

```raven
match x {
    (let a, == existingValue) => ...
}
```

`== existingValue` is a convenience form. The same outcome can be expressed with
a plain binding plus a `when` guard, but explicit value patterns avoid adding an
extra arm condition when the intent is simple equality against an in-scope value.
It also does not capture that element. If you need the value later, bind it and
compare in a guard/condition (`(a, b) when b == existingValue`, or
`if t is (a, b) && b == existingValue`).

Collection patterns support both fixed-size and rest segments:

```raven
let [..2 start, last] = values
let [first, ...rest] = values
let [first, ...middle, last] = values
let [first, ...] = values
let [first, ..2 middle, last] = "rune"
```

In inline/freestanding sequence patterns, spell captures explicitly:
`..2 val name` or `...val rest`. In deconstruction assignments/declarations,
bare `..2 name` and `...rest` remain valid as binding targets. Bare
`..N` and bare `...` may be used in either form to ignore
the rest of the sequence without creating a binding. Both captured and
non-capturing rest segments may appear in the middle or at the end. When the
input is a fixed-length array, captured `..N` and `...rest` array
segments keep an inferred fixed-length array type. For strings, a
plain element binds `char`, while `..N` and rest segments bind `string`.

Nested deconstruction uses the same recursive compatibility rules in all valid
positions:

* declaration deconstruction (`let (...) = expr`, `let [...] = expr`)
* assignment deconstruction (`(...) = expr`, `[...] = expr`)
* function-expression parameter patterns (`((...), [...]) => ...`)
* `is`/`match` pattern positions

Only the first two bullets above are **deconstruction heads**. The latter two
reuse the same nested positional/sequence forms inside broader general-pattern
contexts that also support additional match-only pattern kinds.

For diagnostics, Raven reports failures at the specific nested subpattern that
is incompatible (arity/type/member mismatch) and only introduces symbols for
successfully bound designators.

> ⚠️ **Runtime shape note:** sequence deconstruction is length-sensitive at
> runtime. If the input sequence does not contain enough elements for the fixed
> prefix/suffix elements in the pattern, evaluation may fail at runtime.
> Plain enumerable-only sources are not treated as sequence-deconstructable.

The discard identifier also appears in ordinary assignment statements. Writing
`_ = Compute()` produces a discard assignment statement whose left-hand side is a
dedicated discard expression. The assignment still evaluates the right-hand
expression, but the result is ignored. Discard assignments follow the same rules
as positional assignment: they never declare a binding and may carry a type
annotation when overload resolution needs guidance. `AssignmentStatementSyntax`
exposes an `IsDiscard` helper when analyzers need to detect this pattern.

### Resource declarations (`use`)

A `use` declaration introduces a **scoped disposable resource**.
The declaration resembles a local variable binding and **must include an initializer**.

In a synchronous context, the initializer’s type — and any explicit type
annotation — must be convertible to `System.IDisposable`.

In an async context, Raven prefers `System.IAsyncDisposable.DisposeAsync()` when
the resource supports `IAsyncDisposable`; otherwise it falls back to
`System.IDisposable.Dispose()`. The declared type and initializer type must
therefore be convertible to at least one of those disposal shapes. If the
conversion fails, Raven reports the same diagnostic used for other implicit
conversions.

Resources created with `use` behave like ordinary locals: they remain in scope for the enclosing block and participate in definite-assignment rules. When control leaves the block, the resource is **automatically disposed**. Disposal occurs in **reverse declaration order**, ensuring that later resources observe earlier ones still alive.

Suspending at `await` does not exit the resource scope. Resources remain alive
until the scope exits, including by return, exception, or cancellation. An async
function completes its task only after the required cleanup has finished. These
rules also apply to resources acquired after an earlier await.

When you need a narrower lifetime than the enclosing block, Raven also supports
an explicit nested-scope form:

```raven
use stream = OpenRead(path) in {
    // stream is only in scope here
}
```

The `in { ... }` form is equivalent to introducing a nested block whose first
statement is the `use` declaration:

```raven
use obj = Foo { Value = 2 } in {
    obj.Run()
}
```

File-scope `use` declarations participate as well: they are disposed after the
file's top-level statements finish executing.

```raven
use stream = System.IO.File.OpenRead(path)
use reader = System.IO.StreamReader(stream)

let text = reader.ReadToEnd()

// reader.Dispose() and stream.Dispose() run automatically when the scope ends
```

```raven
async func Load(path: string) -> Task<string> {
    use stream = OpenAsyncStream(path)
    return await stream.ReadAllTextAsync()
}
```

In the async example above, Raven uses `DisposeAsync()` when `stream`
implements `IAsyncDisposable`; otherwise it uses `Dispose()`.
