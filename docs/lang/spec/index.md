# Language reference

Look up a language feature, keyword, or operator. For a guided introduction,
start with the [language tour](../../introduction.md).

<div class="raven-reference-finder" data-reference-finder hidden>
  <label for="reference-query">Find a language feature</label>
  <div class="raven-reference-search"><input id="reference-query" type="search" placeholder="Try records, lambda, nullable, ?, or !" aria-controls="reference-topics" autocomplete="off"><button type="button" data-reference-clear>Clear</button></div>
  <p data-reference-count role="status" aria-live="polite" aria-atomic="true"></p>
</div>

<div data-reference-shortcuts>

## Common lookups

[Records](type-declarations-and-initialization.md#records) ·
[Nullable types and !](type-system.md#nullable-types) ·
[Option, Result, and ?](async-and-error-propagation.md) ·
[Lambdas](functions.md#function-expressions) ·
[Pattern bindings](fundamental-patterns.md#type-and-binding-patterns) ·
[Async and await](async-functions.md)

</div>

<div id="reference-topics" class="raven-reference-topics">
<section class="raven-reference-group">
<h2>Values and types</h2>
<ul>
<li data-reference-topic data-keywords="binding immutable mutable using dispose"><a href="local-declarations.md">Variables and constants: let, var, const</a><p>Declare values, shadow names, and manage resources with use.</p></li>
<li data-reference-topic data-keywords="int string bool decimal tuple array where variance as"><a href="type-system.md">Types, generics, and conversions</a><p>Built-in types, type annotations, constraints, and casts.</p></li>
<li data-reference-topic data-keywords="null nullable nullability is not null null forgiving !"><a href="type-system.md#nullable-types">Nullable types and suppression: T? and !</a><p>Handle null, narrow with patterns, and suppress nullability.</p></li>
<li data-reference-topic data-keywords="interpolation typeof nameof default literal"><a href="fundamental-expressions.md">Literals, tuples, and basic expressions</a><p>Write strings, numbers, tuples, default values, and type expressions.</p></li>
<li data-reference-topic data-keywords="array list dictionary set [] spread .."><a href="collection-expressions.md">Collections and spread</a><p>Create collections and spread elements into them.</p></li>
<li data-reference-topic data-keywords="infer target typing block expression"><a href="expressions-and-inference.md">Expressions and type inference</a><p>How expressions produce values and acquire their types.</p></li>
<li data-reference-topic data-keywords="+ - * / == != &amp;&amp; || ?? .. ..&lt; ^ bitwise"><a href="operators.md">Operators, precedence, and ranges</a><p>Look up operator order, indexing, arithmetic, and comparisons.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Functions and calls</h2>
<ul>
<li data-reference-topic data-keywords="function return closure local function"><a href="functions.md">Functions: func</a><p>Declare functions, return values, and capture local state.</p></li>
<li data-reference-topic data-keywords="lambda anonymous =&gt; callback function expression"><a href="functions.md#function-expressions">Lambdas and function values</a><p>Write anonymous functions and pass behavior as a value.</p></li>
<li data-reference-topic data-keywords="call invoke argument named optional"><a href="invocations.md">Calls and arguments</a><p>Invoke functions and methods with positional or named arguments.</p></li>
<li data-reference-topic data-keywords="ref out params overload operator parameter"><a href="parameters-overloading-and-operators.md">Parameters, overloads, and custom operators</a><p>Define signatures and choose among overloads.</p></li>
<li data-reference-topic data-keywords="delegate Action Func"><a href="delegate-declarations.md">Delegates</a><p>Declare callable .NET types.</p></li>
<li data-reference-topic data-keywords="pipe pipeline |&gt;"><a href="pipe-expressions.md">Pipes</a><p>Pass a value through a sequence of calls.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Data models and members</h2>
<ul>
<li data-reference-topic data-keywords="class struct object"><a href="classes-and-members.md">Classes, structs, and members</a><p>Choose an object-oriented type and find its member rules.</p></li>
<li data-reference-topic data-keywords="constructor init new field static public private internal accessibility"><a href="type-declarations-and-initialization.md">Declarations, constructors, and access</a><p>Initialize types, declare fields, and control visibility.</p></li>
<li data-reference-topic data-keywords="record record class record struct data equality"><a href="type-declarations-and-initialization.md#records">Records</a><p>Declare data types with record semantics.</p></li>
<li data-reference-topic data-keywords="union case discriminated tagged sum type"><a href="unions.md">Unions</a><p>Model alternatives with case-specific payloads.</p></li>
<li data-reference-topic data-keywords="enum enumeration flags"><a href="enum-declarations.md">Enums</a><p>Declare named constant values.</p></li>
<li data-reference-topic data-keywords="interface contract implementation"><a href="interfaces.md">Interfaces</a><p>Define contracts implemented by types.</p></li>
<li data-reference-topic data-keywords="val var get set init field event property indexer"><a href="properties-and-events.md">Properties, indexers, and events</a><p>Declare storage and computed properties, indexers, and events.</p></li>
<li data-reference-topic data-keywords="open abstract sealed permits override final partial base"><a href="inheritance-and-partial-types.md">Inheritance, sealed hierarchies, and partial types</a><p>Extend types, override members, and define closed hierarchies.</p></li>
<li data-reference-topic data-keywords="extension extend methods properties"><a href="extensions.md">Extension members</a><p>Add members to existing types.</p></li>
<li data-reference-topic data-keywords="new with copy initializer"><a href="object-creation.md">Object creation and copying</a><p>Construct values and copy them with changes.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Patterns and matching</h2>
<ul>
<li data-reference-topic data-keywords="pattern capture binding is"><a href="pattern-matching.md">Pattern matching overview</a><p>Test values and bind their parts.</p></li>
<li data-reference-topic data-keywords="match switch when guard"><a href="match-forms.md">Match expressions and statements</a><p>Write match arms and guards.</p></li>
<li data-reference-topic data-keywords="let var _ discard == variable pattern relational is not"><a href="fundamental-patterns.md">Binding, type, and comparison patterns</a><p>Match constants, types, ranges, and comparisons; capture with let.</p></li>
<li data-reference-topic data-keywords="list array sequence property [] {} slice rest"><a href="sequence-and-property-patterns.md">Sequence and property patterns</a><p>Match collections and object properties.</p></li>
<li data-reference-topic data-keywords="deconstruct tuple case Some Ok Error"><a href="deconstruction-and-union-patterns.md">Deconstruction and union-case patterns</a><p>Match positional values and union payloads.</p></li>
<li data-reference-topic data-keywords="dictionary map key value"><a href="dictionary-patterns.md">Dictionary patterns</a><p>Match keys and values in dictionaries.</p></li>
<li data-reference-topic data-keywords="exhaustive unreachable missing case"><a href="match-exhaustiveness.md">Exhaustiveness</a><p>Understand missing cases and complete matches.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Control flow, async, and errors</h2>
<ul>
<li data-reference-topic data-keywords="flow branch"><a href="control-flow.md">Control flow overview</a><p>Choose a branch, loop, or transfer of control.</p></li>
<li data-reference-topic data-keywords="if else loop for while await foreach"><a href="control-flow-expressions.md">Conditionals and loops: if, for, while</a><p>Branch on conditions and iterate over values.</p></li>
<li data-reference-topic data-keywords="assignment = += statement"><a href="assignment-and-expression-statements.md">Assignments and expression statements</a><p>Update storage and use expressions as statements.</p></li>
<li data-reference-topic data-keywords="return yield iterator sequence"><a href="returns-and-yield.md">Return and yield</a><p>Return from functions and produce iterator values.</p></li>
<li data-reference-topic data-keywords="break continue goto label"><a href="jumps-and-labels.md">Break, continue, and labels</a><p>Control iteration and jump to labels.</p></li>
<li data-reference-topic data-keywords="async await Task ValueTask"><a href="async-functions.md">Async and await</a><p>Declare asynchronous functions and await work.</p></li>
<li data-reference-topic data-keywords="throw try catch finally exception cleanup"><a href="error-handling.md">Exceptions: try, catch, finally</a><p>Throw exceptions and handle them with structured blocks.</p></li>
<li data-reference-topic data-keywords="Option Result Some None Ok Error ? ?. try carrier propagation"><a href="async-and-error-propagation.md">Option, Result, and propagation: ?</a><p>Capture exceptions as values and propagate absence or errors.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Files and syntax</h2>
<ul>
<li data-reference-topic data-keywords="lexical identifier escape keyword comment pragma"><a href="lexical-structure.md">Source text, keywords, and comments</a><p>Read token, identifier, comment, and trivia rules.</p></li>
<li data-reference-topic data-keywords="unit () expression statement value"><a href="values-and-statements.md">Values, expressions, and statements</a><p>Understand the basic parts of a Raven program.</p></li>
<li data-reference-topic data-keywords="namespace import alias using"><a href="namespaces-and-imports.md">Namespaces and imports</a><p>Organize names and import types or members.</p></li>
<li data-reference-topic data-keywords="Main entry point top level program args"><a href="top-level-code-and-entry-points.md">Top-level code and Main</a><p>Start an executable and organize file-level code.</p></li>
<li data-reference-topic data-keywords="grammar ebnf formal syntax"><a href="grammar.md">Grammar</a><p>Consult the non-normative EBNF syntax grammar.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Macros</h2>
<ul>
<li data-reference-topic data-keywords="macro metaprogramming quote compile attribute attached freestanding !"><a href="macros.md">Macros and compile-time syntax</a><p>Expand syntax and use attached or freestanding macros.</p></li>
</ul>
</section>
<section class="raven-reference-group">
<h2>Memory and .NET interop</h2>
<ul>
<li data-reference-topic data-keywords="systems memory performance"><a href="systems-programming.md">Systems programming overview</a><p>Find memory and low-level programming features.</p></li>
<li data-reference-topic data-keywords="Span ReadOnlySpan stackalloc memory"><a href="spans-and-memory.md">Spans and stack allocation</a><p>Work with spans and stack-allocated memory.</p></li>
<li data-reference-topic data-keywords="ref struct scoped lifetime borrow safety"><a href="ref-structs-and-ref-safety.md">Ref structs and ref safety</a><p>Understand references, lifetimes, and escape restrictions.</p></li>
<li data-reference-topic data-keywords="unsafe pointer fixed extern native pin interop"><a href="unsafe-code-and-interop.md">Unsafe code and native interop</a><p>Use pointers and call native code.</p></li>
<li data-reference-topic data-keywords="dotnet CLR ABI metadata runtime C# interoperability"><a href="dotnet-implementation.md">.NET representation and compatibility</a><p>See how Raven constructs map to the CLR.</p></li>
</ul>
</section>
</div>
<p class="raven-reference-empty" data-reference-empty hidden>No matching topics. Try a shorter term, another name, or use the site search for full-text results.</p>

## About this reference

These articles describe Raven's syntax and semantics. Examples explain the
rules; the [EBNF grammar](grammar.ebnf) provides a complementary syntax view.
See [release status](../../status.md) when using a published SDK with newer
documentation. Compiler architecture and implementation work are covered in
the [contributor documentation](https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md).
