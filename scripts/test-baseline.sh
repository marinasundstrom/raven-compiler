#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CODE_ANALYSIS_TESTS="$ROOT_DIR/test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj"

build_codegen_exclusion_filter() {
  local filter=""

  while IFS= read -r class_name; do
    [[ -z "$class_name" ]] && continue
    filter+="&FullyQualifiedName!~.$class_name."
  done < <(
    find "$ROOT_DIR/test/Raven.CodeAnalysis.Tests/CodeGen" -name '*.cs' -print0 |
      xargs -0 sed -nE 's/^[[:space:]]*(public|internal)[[:space:]]+((sealed|abstract|static|partial)[[:space:]]+)*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\4/p' |
      grep -E '(Tests?|Test)$' |
      sort -u
  )

  printf '%s' "$filter"
}

build_code_analysis_test_classes() {
  find "$ROOT_DIR/test/Raven.CodeAnalysis.Tests" \
    \( -path "$ROOT_DIR/test/Raven.CodeAnalysis.Tests/CodeGen" -o \
       -path "$ROOT_DIR/test/Raven.CodeAnalysis.Tests/CodeGen/*" \) -prune -o \
    -name '*.cs' -print0 |
    xargs -0 sed -nE 's/^[[:space:]]*(public|internal)[[:space:]]+((sealed|abstract|static|partial)[[:space:]]+)*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\4/p' |
    grep -E '(Tests?|Test)$' |
    sort -u
}

build_heavy_exclusion_filter() {
  local filter=""
  local heavy_names=(
    "MsBuildSampleProjectCompilationTests"
    "ProjectDocumentationEmissionTests"
    "ProjectFileTargetFrameworkAttributeTests"
    "RavenCliFileRunTests"
    "RavenProjectOutputDeterminismTests"
    "StaticFactoryMethod_UsesCanonicalSourceMethodForEmission"
    "OpenProject_CompilerPluginProjectReference_WithObservableReplacement_EmitsExpandedSetter"
  )

  for name in "${heavy_names[@]}"; do
    filter+="&FullyQualifiedName!~$name"
  done

  printf '%s' "$filter"
}

# Raven.CodeAnalysis runs repository source generators from its build targets.
# Project-reference graphs can otherwise build those shared generator projects
# concurrently and race while writing their intermediate assemblies.
# Some workspace tests intentionally allow child compiler processes up to 120
# seconds to finish. Keep the outer test-host watchdog longer than that timeout
# so the test can terminate the process tree and report its captured output.
test_args=(-m:1 /property:WarningLevel=0 --blame-hang-timeout 180s --blame-hang-dump-type none)

run_code_analysis_tests_in_batches() {
  local class_batch=()
  local batch_size=8
  local code_analysis_built=false

  run_batch() {
    (( ${#class_batch[@]} == 0 )) && return

    local class_filter=""

    for class_name in "${class_batch[@]}"; do
      [[ -z "$class_name" ]] && continue
      if [[ -n "$class_filter" ]]; then
        class_filter+="|"
      fi
      class_filter+="$code_analysis_filter&FullyQualifiedName~$class_name"
    done

    [[ -z "$class_filter" ]] && return

    if [[ "$code_analysis_built" == true ]]; then
      dotnet test "$CODE_ANALYSIS_TESTS" "${test_args[@]}" \
        --no-build \
        --filter "$class_filter"
    else
      dotnet test "$CODE_ANALYSIS_TESTS" "${test_args[@]}" \
        --filter "$class_filter"
    fi

    code_analysis_built=true
  }

  while IFS= read -r class_name; do
    [[ -z "$class_name" ]] && continue
    class_batch+=("$class_name")

    if (( ${#class_batch[@]} >= batch_size )); then
      run_batch
      class_batch=()
    fi
  done < <(build_code_analysis_test_classes)

  run_batch
}

# Baseline excludes runtime/emission-heavy CodeGen + Samples tests.
# Some CodeGen tests still use older Raven.CodeAnalysis.Tests namespaces,
# so derive the CodeGen exclusion set from the folder rather than relying
# only on fully-qualified namespace substrings.
common_filter="FullyQualifiedName!~Sample"
code_analysis_filter="$common_filter$(build_codegen_exclusion_filter)$(build_heavy_exclusion_filter)"

run_code_analysis_tests_in_batches

while IFS= read -r project; do
  [[ "$project" == "$CODE_ANALYSIS_TESTS" ]] && continue
  [[ "$project" == *Raven.CodeAnalysis.Samples.Tests.csproj ]] && continue
  [[ "$project" == *Raven.LanguageServer.Tests.csproj ]] && continue
  [[ "$project" == *Raven.LanguageServer.Integration.Tests.csproj ]] && continue
  [[ "$project" == *Raven.LanguageServer.Perf.Tests.csproj ]] && continue

  dotnet test "$project" "${test_args[@]}" \
    --filter "$common_filter"
done < <(
  find "$ROOT_DIR/src" "$ROOT_DIR/test" \
    \( -name '*Tests.csproj' -o -name '*.Testing.csproj' \) |
    sort
)
