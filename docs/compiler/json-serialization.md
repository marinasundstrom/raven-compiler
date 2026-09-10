# Raven Core JSON Serialization

Raven.Core provides `System.Text.Json` converters for its carrier types. The
serialization policy is intentionally payload-first:

- Emit plain JSON whenever the declared target type is enough to recover the
  carrier value.
- Add discriminator metadata only when the JSON shape cannot otherwise identify
  the active case or member.
- Let `System.Text.Json` serialize nested payloads normally once their concrete
  payload type is known.

This document describes the current converter contracts implemented in
`src/Raven.Core`. Standard `System.Union<...>` carriers use native union
serialization in the .NET 11 asset and retain Raven's converter in the .NET 10
asset. The target framework, not the compiler host, selects that behavior.

The built-in carrier converters are not one shared union format. Each converter
maps its carrier to the JSON shape that best matches the carrier's role:

- `Option<T>` maps to the usual JSON optionality pattern: either the value or
  `null`.
- `Result<T, E>` needs an explicit success/error shape, so it uses its own
  tagged object format.
- Ad-hoc `System.Union<...>` values try to remain plain `T1` or `T2` JSON whenever the JSON
  shape and declared target type make that possible. Complex member types need
  special treatment when their JSON shape cannot identify the member on read.

This payload-first behavior is the standard serialization contract of the
built-in `Option<T>`, `Result<T, E>`, and `System.Union<...>` types and is
available to C# through ordinary `System.Text.Json` calls. Tagged Raven union
serialization is different: it is special behavior and is enabled only by an
explicit `[RavenTaggedUnionJsonConverter]` attribute or by registering
`RavenTaggedUnionJsonConverterFactory` in `JsonSerializerOptions.Converters`.
Raven does not globally replace `System.Text.Json` behavior for unrelated
types.

The current converters use reflection to inspect carrier fields, constructors,
case objects, and payload types. A future macro could generate equivalent
union-specific JSON converters and avoid reflection on hot serialization paths.

## `Option<T>`

`Option<T>` uses `OptionJsonConverterFactory`.

JSON shape:

| Raven value | JSON |
| --- | --- |
| `Some(value)` | the JSON representation of `value` as `T` |
| `None` | `null` |

Example:

```raven
let name: Option<string> = Some("Raven")
let missing: Option<string> = None
```

```json
"Raven"
```

```json
null
```

Read behavior:

- `null` reads as `None`.
- Any non-null JSON value is deserialized as `T` and wrapped as `Some(value)`.
- `Option<T>` overrides `HandleNull`, so `null` reaches the converter instead
  of being handled by `System.Text.Json` before converter dispatch.

Nested payloads are serialized by the serializer for `T`. For example,
`Option<Result<T, E>>` delegates the non-null payload to the `Result<T, E>`
converter.

## `Result<T, E>`

`Result<T, E>` uses `ResultJsonConverterFactory`.

JSON shape:

| Raven value | JSON |
| --- | --- |
| `Ok(value)` | `{ "case": "Ok", "value": <value as T> }` |
| `Error(data)` | `{ "case": "Error", "data": <data as E> }` |

Example:

```raven
let ok: Result<int, string> = Ok(42)
let error: Result<int, string> = Error("not found")
```

```json
{ "case": "Ok", "value": 42 }
```

```json
{ "case": "Error", "data": "not found" }
```

Read behavior:

- The converter reads the exact `case` property.
- `"Ok"` reads the `value` property as `T`.
- `"Error"` reads the `data` property as `E`.
- Any other case value currently falls back to `Error(default(E))`.

The property names are part of the converter shape. They are written as
lowercase `case`, `value`, and `data`; they are not produced through
`JsonSerializerOptions.PropertyNamingPolicy`.

## Parenthesized Unions

The type syntax `T1 | T2` lowers to Raven.Core's built-in
`System.Union<T1, T2>` carrier. Arity-specific carriers exist for two through
five member types. On .NET 11 they use the framework's native union contract;
on .NET 10 they use `RavenParenthesizedUnionJsonConverterFactory`.

The same carrier is directly usable from C#:

```csharp
Union<string, int> value = new("hello");

if (value.TryGetValue(out string? text))
    Console.WriteLine(text);

string json = JsonSerializer.Serialize(value); // "hello"
```

Nominal parenthesized unions use the same value-union model:

```raven
[JsonConverter(typeof(RavenParenthesizedUnionJsonConverterFactory))]
union JsonValue(string? | double | bool | JsonObject | JsonValue[])
```

Parenthesized unions differ from case-declaration unions because their members
can be any type, including primitives, arrays, and framework types. They do not
have a closed set of generated case object types. The parenthesized union
converter is therefore designed to serialize as plain JSON when the member can
be inferred from the JSON token shape.

### Native .NET 11 behavior

The .NET 11 standard carriers expose `JsonTypeInfoKind.Union`. Serialization
writes the active case's JSON directly, including object cases, without adding
a union discriminator. Cases with distinct JSON token kinds can be read without
a classifier; for example, `bool | string` works with web defaults.

Cases sharing a JSON kind require explicit classification. In particular, web
defaults permit reading numbers from JSON strings, so `int | string` is
ambiguous on input. This follows the framework contract rather than Raven's
legacy first-match policy. For nominal object unions, `[JsonUnion]` can select
the framework's structural classifier when case property names distinguish the
objects. See Microsoft's [union serialization guidance](https://devblogs.microsoft.com/dotnet/unions-and-closed-hierarchies-in-aspnetcore/).

`Option<T>` and `Result<T, E>` retain their documented, type-specific converters;
this change concerns the standard parenthesized union carriers. Explicit Raven
converter attributes and registrations remain opt-in policies.

### Legacy converter behavior

The remaining subsections describe `RavenParenthesizedUnionJsonConverterFactory`,
which remains the default for the .NET 10 standard carriers. Targeting .NET 10
preserves the existing JSON format and case-selection behavior, including when
the compiler runs on .NET 11.

### Empty Carrier

An uninitialized/default standard union serializes as `null`.

On read, `null` produces the default union value.

### Simple Members

Simple member types serialize directly as their payload JSON:

```raven
let text: string | int = "hello"
let number: string | int = 42
```

```json
"hello"
```

```json
42
```

Simple member types currently include:

- primitive types
- enums
- `string`
- `decimal`
- `Guid`
- `DateTime`
- `DateTimeOffset`
- `TimeSpan`

Read behavior is token-shape based:

- JSON strings can match `string`, `char`, enums, `Guid`, date/time types, and
  `TimeSpan`.
- JSON numbers can match numeric primitives, `decimal`, and numeric enum input.
- JSON booleans match `bool`.

If multiple simple members can read the same token shape, the first matching
payload field in the emitted union carrier wins. This means shapes such as
`string | Guid` are intentionally compact but ambiguous in JSON unless a more
explicit carrier shape is introduced separately.

### Object Members

Non-simple members are serialized to a `JsonElement`. If the result is a JSON
object, the converter injects a `$type` discriminator and then copies the
payload object's properties:

```raven
let value: Car | string = Car("Volvo")
```

```json
{ "$type": "Car", "Make": "Volvo" }
```

Read behavior:

- The converter looks for `$type`.
- `$type` is matched against each member type's discriminator.
- The current discriminator is `memberType.Name`.
- The full JSON object is then deserialized as the selected member type.

If the payload object already has a `$type` property, the converter writes its
own discriminator and skips the payload property with the same name.

### Array and Other Non-Object Complex Members

If a non-simple member serializes to a non-object JSON value, the current
parenthesized union converter writes that JSON value through unchanged:

```raven
let values: string[] | int = ["a", "b"]
```

```json
["a", "b"]
```

This preserves plain JSON on write, but it is not currently round-trippable
through the standard union converter because the reader has no discriminator
slot in a JSON array and does not infer array member types from `JsonValueKind.Array`.

The same limitation applies when the array element type is itself a union:

```raven
let values: (string | int)[] | bool = ["a", 42]
```

The array elements can serialize and deserialize correctly once the array type
is known. For example, an object property of type `(string | int)[]` can use the
standard union converter for each element. The failing case is when the array is
itself the active payload of an outer standard union; the outer union member
cannot be recovered from the unannotated JSON array.

This is a known boundary of the current "plain JSON when possible" policy:
arrays are plain on write today, but they need an additional envelope or other
type signal to round-trip when the array itself is a standard union member.

### JSON Modeling Playground

The `samples/projects/json-modeling-playground` sample is a playground for
modeling JSON with Raven constructs such as records and unions. It is not part
of the Raven.Core serialization contract.

The sample combines Raven.Core's built-in union converters with custom
converters defined by the sample itself. In particular, it shows how a typed
JSON object wrapper can flatten its dictionary entries into the JSON object
body by using a custom wrapper converter.

## Opt-In Tagged Raven Unions

Case-declaration Raven unions, also known as tagged unions, can opt into
`System.Text.Json` support with `[RavenTaggedUnionJsonConverter]` or
`[RavenTaggedUnionJsonConverter("propertyName")]`.
The default discriminator property is `$case`; the attribute argument selects a
different discriminator name.

C# callers can opt a built-in or emitted union into the same tagged contract
for a particular serializer configuration:

```csharp
var options = new JsonSerializerOptions();
options.Converters.Add(new RavenTaggedUnionJsonConverterFactory("kind"));

var value = new Union<string, int>("hello");
string json = JsonSerializer.Serialize(value, options);
// {"kind":"String","Value":"hello"}
```

Without that explicit converter registration, the built-in union keeps its
standard payload-first JSON shape.

Example:

```raven
[RavenTaggedUnionJsonConverter("kind")]
union Status {
    case Active(Date: DateTimeOffset)
    case OnMaintenance(Date: DateTimeOffset, Reason: string)
}
```

```json
{
  "kind": "OnMaintenance",
  "date": "2026-05-10T07:37:58.363687+00:00",
  "reason": "Test"
}
```

The converter distinguishes between body-form cases and direct parenthesized
members.

### Body-Form Cases

For a body-form case such as `case OnMaintenance(Date, Reason)`, the active
payload is a generated case object. The converter writes:

- the discriminator property
- one JSON property for each public case payload property

Property names for case payload properties use
`JsonSerializerOptions.PropertyNamingPolicy` when one is configured.

### Direct Parenthesized Members

If `RavenTaggedUnionJsonConverter` is applied to a parenthesized union, the converter
must treat each active member as a direct payload value rather than a generated
case object:

```raven
[RavenTaggedUnionJsonConverter("kind")]
union TaggedValue(string | double | bool | TaggedValue[])
```

Direct members are therefore written with a discriminator and a synthetic payload
property:

```json
{
  "kind": "TaggedValue[]",
  "value": [
    { "kind": "String", "value": "true" },
    { "kind": "Double", "value": 42 }
  ]
}
```

The synthetic payload property is named `Value` before applying
`JsonSerializerOptions.PropertyNamingPolicy`; with camel-case options it is
written as `value`.

This shape is necessary for direct members whose JSON representation cannot
carry the discriminator inline, especially array members. Once the array payload
is selected, `System.Text.Json` serializes its elements normally, so recursive
union elements use their own converter.

Use this tagged converter only when the desired JSON contract is explicitly
tagged. For plain JSON value-union behavior on .NET 11, use the native union contract.
The .NET 10 fallback is `RavenParenthesizedUnionJsonConverterFactory`.

Read behavior:

- The discriminator selects the declared union case or direct member type.
- Body-form cases read payload properties and construct the generated case.
- Direct members read the synthetic payload property as the selected member
  type, then wrap it in the union.

## Current Converter Summary

| Type | Converter | Plain JSON when possible | Metadata shape |
| --- | --- | --- | --- |
| `Option<T>` | `OptionJsonConverter<T>` | Yes | `None` is `null`; `Some` is the payload |
| `Result<T, E>` | `ResultJsonConverter<T, E>` | No | `{ "case": "Ok", "value": ... }` or `{ "case": "Error", "data": ... }` |
| `T1 | T2` / `System.Union<...>` targeting .NET 11 | Native System.Text.Json union converter | Yes | Active case JSON; ambiguous cases need classification |
| `T1 | T2` / `System.Union<...>` targeting .NET 10 | `RavenParenthesizedUnionJsonConverter<TUnion>` | Yes | Object members get `$type`; arrays currently remain plain arrays |
| `union Name(T1 | T2)` with `RavenParenthesizedUnionJsonConverterFactory` | `RavenParenthesizedUnionJsonConverter<TUnion>` | Yes | Object members get `$type`; arrays currently remain plain arrays |
| Case-declaration union with `[RavenTaggedUnionJsonConverter]` | `RavenTaggedUnionJsonConverter<TUnion>` | No | Tagged discriminator; case payload properties are flattened |
