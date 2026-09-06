# Build the Raven documentation

The published documentation site is assembled with DocFX. The repository pins
the DocFX version in `.config/dotnet-tools.json`; a separate global installation
is not required.

## Build the site

From the repository root, run:

```bash
scripts/build-docs.sh
```

The generated site is written to `_site/`. To build and serve it locally, run:

```bash
scripts/build-docs.sh --serve
```

Then open <http://localhost:8080>.

## Raven theme

The custom DocFX template lives in `docs/template/`. Its `public/main.css` and
`public/main.js` adapt the modern DocFX template to Raven, including Raven code
highlighting and automatic light/dark theme support. Its color, typography, and
surface tokens live in `docs/template/public/raven-theme.css`. The standalone
Playground owns an aligned copy in `src/Raven.Playground/wwwroot/css/` so local
development and independent deployments do not depend on the documentation
tree.

Raven's purple accent, typography, cards, borders, and page surfaces define the
site shell. Code is a distinct shared layer modeled on Visual Studio Code's
Light+ and Dark+ editor themes. The shared theme defines the code background,
foreground, and syntax-token colors; the DocFX highlighter and RavenDoc consume
those variables, while the Playground's Monaco themes use the corresponding
editor and token palette. Changes to code colors should be applied consistently
to all three surfaces.

The home page presents a showcase carousel and one ordered learning path. Keep
the first decision obvious: learn the language or try it online. Each carousel
item should use a concrete sample to make Raven's syntax, style, or platform
reach interesting, and include a **Learn more** link to its showcase page.
Preserve normal source formatting in every sample: indentation communicates
nesting, and blank lines should separate distinct declarations, constructs,
and top-level operations.

Keep the global header deliberately compact. Detailed documentation hierarchy
belongs in the Docs menu and section sidebars, while the root page serves as a
dedicated introduction to Raven.

## Guides and the language reference

User-facing guides and the language specification serve different reading
goals. Both are published, but they should not duplicate one another.

Guides teach a concept or help complete a task. They should lead with
motivation and recognizable application code, explain the useful default, call
out important tradeoffs, and point to a next step. A feature guide should not
attempt to enumerate every grammar production, conversion rule, diagnostic, or
edge case.

Public feature guides should resemble Microsoft Learn concept pages: explain
why a feature exists, show its useful default with compact code, describe the
choice it communicates, and link to a practical next step. They should not
attempt to reproduce grammar productions, binding rules, or every edge case.

The files under `docs/lang/spec/` form the public language reference as well as
the compiler team's working specification. Reference articles should still be
readable by language users: lead with the feature and representative examples,
then introduce precise rules and edge cases as needed. Guides may link to the
relevant reference article when readers need the complete behavior, but should
remain understandable on their own.

Workload guides live under `docs/workloads/`. They explain general ways to
build, run, publish, or deploy an application. A workload page can use several
checked-in samples, but should not imply that one sample defines the whole
application category.

Homepage carousel items are showcases. Their **Learn more** links lead to
focused pages under `docs/showcases/` that explain the particular syntax,
style, or platform idea visible in the sample. A showcase page then links to
the relevant feature guide, workload guide, or runnable sample as applicable.
The carousel is editorially selected and does not need to represent every
workload. Treat its current themes as a curation, not a permanent taxonomy:
showcases can be reframed, replaced, or expanded as Raven and its audience
evolve without changing the workload navigation.

Runnable documentation blocks opt in with an empty marker immediately before
the Raven code fence. Use `data-raven-playground="source"` when the displayed
snippet is a complete standalone program. The generated action sends its text
through the Playground's inline `source` parameter but does not execute it
automatically. Use `data-raven-playground="example"` with `data-example="id"`
when it should open an entry visible in the Playground's example picker. Use
`data-snippet="id"` for a fuller documentation companion from the separate
bundled snippet catalog. Keep each `.rvn` companion in a `snippets/` directory
beside the documentation section that owns it; `docs/snippets/index.json`
contains only the shared manifest. These entries remain out of the picker. The
Playground project publishes the manifest and companions as static content.
Its build also stages them into the standalone Playground's generated
`wwwroot/snippets/` directory so `dotnet run` uses the same catalog without
making the Playground project their source of truth.
Use globally unique IDs and companion filenames because the published bundle
uses a flat `snippets/` directory. Add `data-run="true"` only for a vetted
example or snippet, and optionally add `data-source-url` for its checked-in
source. Never place an external source URL in a Playground query parameter.

## Publication boundary

`docs/docfx.json` explicitly lists public learning material, feature guides,
the library entry points, and the tooling pages needed to use Raven. Compiler
APIs, architecture and implementation details, contributor instructions,
testing notes, investigations, language proposals, historical material, and
standalone design work remain available in source control but are intentionally
excluded from the user-facing site.

## Validation

The build restores the pinned tool and generates the site with DocFX warnings
treated as errors. Broken links and unresolved cross-references therefore fail
the build. Pages missing from navigation should also be treated as publication
defects during review.

## Publish the official website with GitHub Pages

The `.github/workflows/docs.yml` workflow builds and validates the Raven
language website for relevant pull requests, but pull requests never deploy
it. Pushes to `main` do not run or publish the website automatically.

Publishing is a separate manual operation and is independent of Raven package
and tag releases. Start `Raven website` from the GitHub Actions UI against the
site content to publish and explicitly enable `publish_site`. Only that opt-in
run adds production analytics, uploads `_site/` as a GitHub Pages artifact, and
deploys it to:

<https://marinasundstrom.github.io/raven/>

The repository's Pages source must be set to **GitHub Actions** in the GitHub
Pages settings. The workflow keeps build and deployment as separate jobs, so a
failed DocFX validation cannot replace the published site. A manually
dispatched run with `publish_site` disabled only validates the site and has no
external publishing effect.

The same Pages artifact includes the browser playground at:

<https://marinasundstrom.github.io/raven/playground/>

`scripts/build-playground-site.sh` builds the relocatable Blazor WebAssembly
application and places it under `_site/playground/` after DocFX has built the
main site. The playground and documentation therefore deploy atomically.

The artifact also includes the zero-install experimental component-template
showcase at:

<https://marinasundstrom.github.io/raven/experiments/html-macro/>

`scripts/build-html-macro-site.sh` publishes its standalone Blazor WebAssembly
host under `_site/experiments/html-macro/`. It consumes the checked-in sample
without embedding the experimental macro implementation into the Playground.

The artifact also bundles two independent RavenDoc sites:

- `Raven.Core` at `/raven/libraries/raven-core/`
- `Raven.Macros` at `/raven/libraries/raven-macros/`

RavenDoc retains its own static-site structure and rendering pipeline. The
DocFX navigation links to these library references, and the RavenDoc headers
link back to the language documentation and related references. The shared
Raven theme keeps the sites visually related without coupling their page
models.

After all of these surfaces have been assembled, the workflow runs
`scripts/add-site-provenance.mjs`. It writes one `site-build.json` manifest at
the artifact root and injects the matching `site-build.js` into every generated
HTML page. The script says which Raven version the documentation describes and
links to the exact source commit for traceability. These are provenance fields,
not the website's release identity. They are added to every surface's footer,
including footers rendered after startup by a Blazor WebAssembly application.

The documented version defaults to the `Raven.Sdk` selection in `global.json`.
It is labeled **unreleased** only when no matching `v<version>` tag exists in
the repository; the tag does not have to point at the site commit. Consequently,
a docs-only update after a Raven release remains documentation for that release
instead of being presented as the next unreleased preview. The workflow's
optional `documented_version` input can select another version, and
`RAVEN_SITE_VERSION` provides the same override for local builds. Local
commit-qualified SDK versions such as `0.1.10-preview.1-local.<sha>` are displayed
as `0.1.10-preview.1`. A local artifact also says `uncommitted changes` when
tracked files differ from its source commit.
