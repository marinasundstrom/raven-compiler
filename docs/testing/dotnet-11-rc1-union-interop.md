# .NET 11 RC 1 union and closed-hierarchy interoperability

Raven targets C#/.NET interoperability while retaining .NET 10 support. Select
contracts from the target framework's references, never from the compiler host.
This assessment uses the locally installed SDK `11.0.100-rc.1.26425.128` and
runtime `11.0.0-rc.1.26425.128` on macOS arm64.

## Implemented alignment

Commit `b48128e5c` aligns closed-class metadata. Commit `114cb4552` selects native
standard-union JSON for .NET 11 while preserving the .NET 10 converter.
Commit `59d354cc7` fixes missing sealed-interface metadata and emits loadable
names for hoisted generic cases. Production-code qualification below uses
`59d354cc7` unless a different revision is stated.

| Feature | .NET 10 target | .NET 11 target |
| --- | --- | --- |
| Union marker and `IUnion` | Raven.Core compatibility contracts | Framework contracts |
| Raven sealed class / record class | Embedded `ClosedHierarchyAttribute(Type[])` | Framework `IsClosedTypeAttribute` with `DerivedTypes` |
| Raven sealed interface | Raven closed-family metadata | Raven closed-family metadata; no native closed-interface equivalent |
| Standard `System.Union<...>` JSON | Existing Raven converter and wire format | Native System.Text.Json union contract |
| `Option<T>` / `Result<T, E>` JSON | Existing type-specific converters | Existing type-specific converters |

The first RC 1 probe showed that replacing only the closed-class attribute name
was insufficient: C# emits a named `DerivedTypes` property which System.Text.Json
uses for inference. The implemented metadata includes that property. Raven also
recognizes C# closed roots, discovers direct subclasses from the declaring
module, and rejects direct derivation from imported closed roots.

Native standard-union JSON does not add a union discriminator. Ambiguous JSON
case selection follows the framework's classifier rules, including ambiguity
between numeric and string cases under web defaults. The .NET 10 converter's
existing discriminator and case-selection policies remain unchanged. See the
[JSON contracts](../compiler/json-serialization.md).

## Intentional closed-interface difference

C# 15 / .NET 11 RC 1 supports closed classes and record classes, not closed
interfaces. Raven keeps sealed interfaces as a language feature, including
families with struct implementations and interface inheritance. Emitting a
closed-class marker on an interface would not establish equivalent semantics.

Consequently, on both targets:

- C# can use the interface and its implementations through ordinary CLR APIs.
- C# does not enforce Raven's permitted implementation set or infer exhaustiveness
  from Raven's interface metadata. Consumers need a fallback switch arm.
- Native closed-type JSON inference does not discover that interface family.
  Interface polymorphism needs explicit configuration or a converter.

Raven-produced closed classes targeting .NET 10 also retain Raven's metadata;
they do not gain C# 15's native closed-class recognition merely because the
consumer or compiler host runs on .NET 11. This differs from the .NET 11 class
contract and is not evidence that .NET 10 targeting is unsupported.

Raven's source-level same-file closure and explicit `permits` rules also remain
in place. C# closes the family at the declaring-module boundary. For .NET 11
classes, the shared metadata describes the emitted family; it does not encode
Raven's source-file permission rules.

See [sealed hierarchy semantics](../lang/spec/inheritance-and-partial-types.md#sealed-hierarchies-and-permits)
and the [.NET metadata contract](../lang/spec/dotnet-implementation.md#sealed-hierarchies).

## Evidence and scope

The focused checks cover:

- Raven-produced unions consumed by a real C# RC 1 application, and C# unions
  imported by Raven, including nullable contents and generic companion cases.
- C# exhaustive switches over Raven closed classes, native JSON preservation of
  derived record data, and deserialization back to the derived record.
- C# closed-family import with nested direct cases, excluding indirect and
  unrelated types, plus rejection of external direct derivation.
- Framework-selected class metadata and the legacy closed-interface contract
  on both targets, including Raven import and external-derivation diagnostics.
- Loadable metadata for nested cases of generic sealed records and interfaces,
  plus execution of those cases on both targets.
- Native JSON metadata for standard union arities two through five; web-default
  boolean/string round-trips; object/string round-trips without a discriminator;
  and System.Text.Json source-generated serialization and deserialization.
- The complete .NET 10 Raven.Core suite, including the existing union JSON format.

### Recorded validation

All listed test runs used `WarningLevel=0` and reported no skips. Counts are
per-run totals; overlapping filters must not be added into a unique-test count.

| Gate | Result | Production revision |
| --- | --- | --- |
| `scripts/test-feature-suite.sh unions` | 188 passed | `59d354cc7` |
| `scripts/test-feature-suite.sh unions --runtime` | 71 passed | `59d354cc7` |
| Compiler tests filtered to `SealedHierarchy`, `ClosedHierarchyMetadataTests`, and `CSharpUnionInteropTests` | 89 passed | `59d354cc7` |
| Complete `test/Raven.Core.Tests/Raven.Core.Tests.csproj` suite targeting .NET 10 | 73 passed | `114cb4552` |
| `scripts/test-target-framework-matrix.sh` | Core/Macros built for both targets; all three representative projects built and ran | `59d354cc7` |

These are compiler, metadata, and JSON interoperability checks. They do not
certify every ASP.NET Core integration. Minimal API HTTP checks now cover both runtime and generated request delegates:
boolean/string, nullable and asynchronous union responses, structural object-case
classification, closed-class round-trips, and malformed-body rejection. Both
modes pass in `AspNetCoreUnionInteropTests`; generated mode also checks that RDG
produced source without fallback diagnostics. OpenAPI checks pass in both modes
and verify boolean/string alternatives, structural object-case schemas, and
closed-class discriminator mappings. SignalR and Blazor remain follow-ups. Dedicated qualification below found gaps in C# `IUnionMembers` providers and
constructed generic closed-family matching. Universal interoperability is not
yet established.

Earlier project-wide release gates ran on Preview 7. Their results must not be
relabeled as RC 1 results; the full baseline interrupted during SDK installation
is not a completed gate.

## Generic closed-hierarchy qualification

C# generic closed roots import their closed marker and direct generic case
names correctly; this is now covered in `CSharpUnionInteropTests`. However,
constructed-family matching is still a compatibility gap. This RC 1 fixture:

```csharp
public closed record GenericEvent<T>;
public sealed record GenericCreated<T>(T Value) : GenericEvent<T>;
public sealed record GenericRemoved<T> : GenericEvent<T>;
```

imports, but a Raven match over `GenericEvent<int>` with arms for
`GenericCreated<int>` and `GenericRemoved<int>` reports RAV2102 for the patterns
and RAV2100 for missing open `GenericCreated<T>` / `GenericRemoved<T>` cases.
The probe therefore does **not** qualify generic-family exhaustiveness. Fixing
projection and generic-base identity must precede a claim of compatibility;
follow it with constrained, specialized, reordered-parameter, and unbound-case
coverage. The earlier passing tests for hoisted Raven cases prove metadata
loadability and execution, not this C# generic matching contract.

## Provider-union qualification

RC 1 accepts this C# contract and its exhaustive switch (compile as a `net11.0`
library with `LangVersion=preview` and nullable annotations enabled):

```csharp
using System.Runtime.CompilerServices;

[Union]
public sealed class Provided : Provided.IUnionMembers
{
    private readonly object _value;
    private Provided(object value) => _value = value;
    public Provided(decimal value) => _value = value;
    object IUnionMembers.Value => _value;

    public interface IUnionMembers
    {
        public static Provided Create(int value) => new((object)value);
        public static Provided Create(string value) => new((object)value);
        object Value { get; }
    }

    public static int Check()
    {
        Provided value = 42;
        return value switch { int number => number, string => -1 };
    }
}
```

**This provider shape is not yet interoperable with Raven's union semantics.**
The RC 1 probe compiled successfully, but Raven imported `Provided` as an ordinary
class, not an `IUnionSymbol`. The public decimal constructor intentionally is not
a member of the C# union: the provider's `Create` methods define its int/string
contents. Raven's current shape recognizer requires a public carrier `Value`
property and public constructors; its member discovery, conversion selection,
and pattern emission also assume the carrier surface. Merely relaxing the
recognizer would select the wrong cases or emit invalid calls.

A complete implementation must select the provider consistently for factory
conversion, `Value`, optional `HasValue`, and optional `TryGetValue`, including
explicit interface implementation, structs, generics, nullable contents, and
by-reference factory arguments. The regression should require the same observed
result from C# and Raven; the current incorrect import is not a contract to
preserve. Ordinary constructor-based C# unions remain covered by the passing
interop tests above.

## References

- [Microsoft: Unions and closed hierarchies in ASP.NET Core](https://devblogs.microsoft.com/dotnet/unions-and-closed-hierarchies-in-aspnetcore/)
- [C# closed modifier](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/closed)
- [C# closed-hierarchy specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-15.0/closed-hierarchies)
- [C# union reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union)
