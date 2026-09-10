#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj"

build_codegen_test_classes() {
  find "$ROOT_DIR/test/Raven.CodeAnalysis.Tests/CodeGen" -name '*.cs' -print0 |
    xargs -0 sed -nE 's/^[[:space:]]*(public|internal)[[:space:]]+((sealed|abstract|static|partial)[[:space:]]+)*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\4/p' |
    grep -E '(Tests?|Test)$' |
    sort -u
}

build_additional_isolated_names() {
  printf '%s\n' \
    "MsBuildSampleProjectCompilationTests" \
    "ProjectDocumentationEmissionTests" \
    "ProjectFileTargetFrameworkAttributeTests" \
    "RavenProjectOutputDeterminismTests" \
    "StaticFactoryMethod_UsesCanonicalSourceMethodForEmission" \
    "OpenProject_RavenMacroProjectReference_WithObservableReplacement_EmitsExpandedSetter"
}

build_stale_runtime_exclusion_filter() {
  local filter=""
  local names=(
    "AttachedMacroCodeGenTests"
    "MsBuildSampleProjectCompilationTests"
    "ProjectFileNuGetReferenceTests"
    "PropertyTests"
    "RuntimeSymbolResolverTests"
  )

  for name in "${names[@]}"; do
    filter+="&FullyQualifiedName!~.$name."
  done

  printf '%s' "$filter"
}

# Runtime/emission-heavy tests are isolated in bounded test-host batches so
# metadata reflection state cannot accumulate across the entire CodeGen suite.
dotnet build "$PROJECT" -m:1 /property:WarningLevel=0 --disable-build-servers

test_args=(-m:1 --no-build /property:WarningLevel=0 --blame-hang-timeout 300s --blame-hang-dump-type none)
runtime_exclusions="&FullyQualifiedName!~CodeGen.Development$(build_stale_runtime_exclusion_filter)"

run_codegen_batches() {
  local class_batch=()
  local batch_size=8

  run_batch() {
    (( ${#class_batch[@]} == 0 )) && return

    local filter=""
    for class_name in "${class_batch[@]}"; do
      [[ -z "$class_name" ]] && continue
      [[ -n "$filter" ]] && filter+="|"
      filter+="FullyQualifiedName~.$class_name."
    done

    dotnet test "$PROJECT" "${test_args[@]}" --filter "($filter)$runtime_exclusions"
  }

  while IFS= read -r class_name; do
    [[ -z "$class_name" ]] && continue
    class_batch+=("$class_name")

    if (( ${#class_batch[@]} >= batch_size )); then
      run_batch
      class_batch=()
    fi
  done < <(build_codegen_test_classes)

  run_batch
}

run_codegen_batches

while IFS= read -r name; do
  [[ -z "$name" ]] && continue
  dotnet test "$PROJECT" "${test_args[@]}" --filter "FullyQualifiedName~$name$runtime_exclusions"
done < <(build_additional_isolated_names)

dotnet test "$PROJECT" "${test_args[@]}" --filter "FullyQualifiedName~Sample$runtime_exclusions"
