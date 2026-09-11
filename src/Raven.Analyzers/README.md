# Raven.Analyzers

Recommended convention and style analyzers for Raven projects.

Install the package with a normal `PackageReference`:

```xml
<ItemGroup>
  <PackageReference Include="Raven.Analyzers" Version="0.1.11" />
</ItemGroup>
```

NuGet exposes the assembly through its standard `analyzers/dotnet` asset, so
Raven's project system loads it without an explicit `Analyzer` item.

`RAV9036` (prefer `loop` over `while true`) is enabled by default. The other
rules in this package are disabled by default and can be enabled with an
explicit `.editorconfig` severity.

Correctness and safety analyzers remain compiler-hosted. This package contains
recommended naming and style policy plus the corresponding code fixes.
