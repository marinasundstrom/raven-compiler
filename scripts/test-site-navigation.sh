#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
site_root="${1:-$repository_root/_site}"

assert_contains() {
    local file="$1"
    local expected="$2"

    if [[ ! -f "$file" ]]; then
        echo "Missing generated site file: $file" >&2
        exit 1
    fi

    if ! grep -Fq "$expected" "$file"; then
        echo "Expected '$expected' in generated site file: $file" >&2
        exit 1
    fi
}

# DocFX must make the Raven brand depth-aware. In particular, the workload
# landing page used to link to itself instead of the root documentation page.
assert_contains "$site_root/index.html" 'class="navbar-brand" href="index.html"'
assert_contains "$site_root/workloads/index.html" 'class="navbar-brand" href="../index.html"'
assert_contains "$site_root/compiler/index.html" 'class="navbar-brand" href="../index.html"'

# RavenDoc is generated independently, but the combined build explicitly gives
# it the shared site root so both shallow and nested API pages return there.
assert_contains "$site_root/libraries/raven-core/index.html" 'class="raven-brand" href="../../index.html"'
assert_contains "$site_root/libraries/raven-core/System/Option\`1/index.html" 'class="raven-brand" href="../../../../index.html"'

# Blazor applications keep standalone-friendly defaults in their own projects.
# The combined build replaces only their runtime configuration.
assert_contains "$repository_root/src/Raven.Playground/wwwroot/appsettings.json" '"RavenSiteRootHref": "./"'
assert_contains "$site_root/playground/appsettings.json" '"RavenSiteRootHref": "../"'
assert_contains "$repository_root/samples/projects/macro-html-blazor/wasm/wwwroot/appsettings.json" '"RavenSiteRootHref": "./"'
assert_contains "$site_root/experiments/html-macro/appsettings.json" '"RavenSiteRootHref": "../../"'

# Preserve incoming links to the original language tour after its editorial rewrite.
for anchor in raven start-without-ceremony hello-world a-quick-taste \
    target-typed-shorthand bindings-and-mutability expressions-and-matching \
    result-and-option propagation-expressions- railroad-style-flow-with-carrier-methods \
    functions data-shapes-and-patterns extensions records-and-primary-constructors \
    accessibility-defaults async-and-await net-interop where-to-go-next; do
    assert_contains "$site_root/introduction.html" "id=\"$anchor\""
done

# Every independently rendered surface receives the same generated provenance
# script from the combined Pages artifact.
assert_contains "$site_root/index.html" 'data-raven-site-provenance src="./site-build.js"'
assert_contains "$site_root/playground/index.html" 'data-raven-site-provenance src="../site-build.js"'
assert_contains "$site_root/experiments/html-macro/index.html" 'data-raven-site-provenance src="../../site-build.js"'
assert_contains "$site_root/libraries/raven-core/index.html" 'data-raven-site-provenance src="../../site-build.js"'
assert_contains "$site_root/site-build.json" "\"commit\": \"$(git -C "$repository_root" rev-parse HEAD)\""
assert_contains "$site_root/site-build.js" 'dataset.ravenBuild = ""'

echo "Combined and standalone site-root navigation checks passed."
