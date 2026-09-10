# Calls

Calls run functions and methods with a set of arguments. Raven uses the same
call syntax for function values, constructors, and types that define an
invocation operator.

```raven
Foo(1, 2)
Console.WriteLine("Test")
```

The expression before `()` is the call target. It may be a named function, a
method, a function-valued expression, a type being constructed, or a value whose
type defines an invocation operator.

## Generic overload specificity

When generic overloads have equivalent constructed parameter types, ignoring
nullable reference annotations, Raven compares their declared parameter shapes.
A concrete type shape is more specific than a type parameter; the comparison
also applies inside matching generic types and arrays. For example,
`Func<Task<T>>` is more specific than `Func<T>` when both accept a
`Func<Task<int>>` argument. Explicit type arguments still constrain selection.

## Optional arguments

When the target has optional parameters, omitted trailing arguments are filled
using the defaults declared on the parameter list. Supplied arguments are
matched positionally before defaults are considered.

## Collector parameters

Parameters may collect a variable number of arguments using a trailing `...`
after the
parameter type. A collector parameter must be the final parameter. The
convenience form `items: T ...` binds as `IList<T>`; use explicit `params`
syntax, such as `params items: int[]`, to control the collection type.

At call sites, extra positional arguments are packed into the collector.
`...expr` expands an existing sequence into it.

## Named arguments

Arguments may be named with `name: expression`. Named arguments may appear in
any order, but positional arguments after a named argument must correspond to
parameters after the right-most named argument and must not duplicate an
already supplied parameter. Unknown and duplicate names reject the candidate.
This syntax applies to functions, object creation, constructor initializers,
and attributes; `name = expression` is not call-argument syntax.

```raven
func makePoint(x: int, y: int, label: string = "origin") -> string {
    return "$label: ($x, $y)"
}

func sum(items: int ...) -> int {
    return items.Length
}

let swapped = makePoint(y: 2, x: 1)
let mixed = makePoint(3, label: "axis", y: 0)
let invalid = makePoint(x: 1, 2) // error
let total = sum(1, 2, 3)
let values = [4, 5]
let expanded = sum(...values)
```

## Function-valued arguments

Function values are passed using ordinary function-expression syntax:

```raven
func use(action: () -> int) -> int {
    return action()
}

let result = use(() => 42)
```

## Callable objects

If the target value's type defines an invocation operator through a `self`
method, `value(...)` invokes that member. Invocation operators can be declared
on classes or interfaces. A class may make one `virtual` or `abstract` so a
derived class can override the call behavior.

See [Parameters, overloading, and
operators](parameters-overloading-and-operators.md#invocation-operator) for
declaration syntax and overload rules.
