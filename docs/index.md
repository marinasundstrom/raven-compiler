---
_layout: landing
title: A fresh language for .NET.
---

<section class="raven-hero">
  <div class="raven-hero-copy">
    <p class="raven-eyebrow">Raven programming language</p>
    <h1>A fresh language<br><span>for .NET.</span></h1>
    <p class="raven-hero-lead">Expressive functions, explicit data models, and familiar object-oriented programming—with the .NET libraries and tools you already use.</p>
    <div class="raven-hero-actions">
      <a class="raven-button raven-button-primary" href="https://marinasundstrom.github.io/raven/playground/?example=hello">Try Raven</a>
      <a class="raven-button" href="getting-started.md">Install the SDK</a>
    </div>
    <p class="raven-preview-note"><a href="introduction.md">Take the language tour</a> · <a href="status.md">Preview status and compatibility</a></p>
    <ul class="raven-capabilities" aria-label="Available today">
      <li>.NET 10 &amp; 11</li><li>SDK &amp; templates</li><li>VS Code</li><li>Compiler APIs</li>
    </ul>
  </div>
  <div class="raven-hero-code">
    <div class="raven-code-titlebar">Explicit states · quote.rvn</div>
<div data-raven-playground="source"></div>
<pre><code class="lang-raven">import&#32;System.Console.*&#10;&#10;union&#32;Quote&#32;{&#10;&#32;&#32;&#32;&#32;case&#32;Ready(total:&#32;decimal)&#10;&#32;&#32;&#32;&#32;case&#32;Rejected(reason:&#32;string)&#10;}&#10;&#10;func&#32;describe(quote:&#32;Quote)&#32;-&gt;&#32;string&#32;{&#10;&#32;&#32;&#32;&#32;quote&#32;match&#32;{&#10;&#32;&#32;&#32;&#32;&#32;&#32;&#32;&#32;.Ready(let&#32;total)&#32;=&gt;&#32;&quot;Total:&#32;$total&quot;&#10;&#32;&#32;&#32;&#32;&#32;&#32;&#32;&#32;.Rejected(let&#32;reason)&#32;=&gt;&#32;reason&#10;&#32;&#32;&#32;&#32;}&#10;}&#10;&#10;WriteLine(describe(.Ready(24.50m)))&#10;</code></pre>
    <p class="raven-code-caption">Declare the possible states. Match each one.</p>
  </div>
</section>

<section class="raven-learning-path">
  <div class="raven-section-heading">
    <p class="raven-eyebrow">From first look to first program</p>
    <h2>Start with something that runs.</h2>
  </div>
  <ol class="raven-path-steps">
    <li><span class="raven-step-number">1</span><a href="raven-in-60-seconds.md">Meet the language</a><p>Read one complete program, then change it in your browser.</p></li>
    <li><span class="raven-step-number">2</span><a href="getting-started.md">Install and run</a><p>Download the SDK and run your first file. No source checkout required.</p></li>
    <li><span class="raven-step-number">3</span><a href="introduction.md">Learn the ideas</a><p>Explore functions, records, unions, patterns, and .NET interop.</p></li>
  </ol>
  <p class="raven-path-aside">Already write C#? Start with <a href="raven-for-csharp-developers.md">familiar code, expressed in Raven</a>.</p>
</section>

<section class="raven-feature-section">
  <div class="raven-section-heading"><p class="raven-eyebrow">Readable workflows</p><h2>Make absence and failure explicit.</h2><p><code>Option</code> represents absence; <code>Result</code> carries a success or an error. Propagate with <code>?</code> when the current operation cannot continue, or use <code>match</code> to handle each case.</p><p><a href="lang/features/option-and-result.md">Learn Option and Result</a></p></div>
  <div class="raven-example-panel"><div data-raven-playground="source"></div>
<pre><code class="lang-raven">import&#32;System.Console.*&#10;&#10;func&#32;readPort(text:&#32;string)&#32;-&gt;&#32;Option&lt;int&gt;&#32;{&#10;&#32;&#32;&#32;&#32;let&#32;port&#32;=&#32;int.TryParse(text)?&#10;&#32;&#32;&#32;&#32;if&#32;port&#32;&lt;=&#32;0&#32;||&#32;port&#32;&gt;&#32;65535&#32;{&#32;return&#32;.None&#32;}&#10;&#32;&#32;&#32;&#32;return&#32;.Some(port)&#10;}&#10;&#10;WriteLine(readPort(&quot;8080&quot;))&#10;WriteLine(readPort(&quot;invalid&quot;))</code></pre></div>
</section>

<section class="raven-feature-section">
  <div class="raven-section-heading"><p class="raven-eyebrow">The platform you know</p><h2>Use ordinary .NET libraries.</h2><p>Call .NET APIs, use generic collections and LINQ, and build with NuGet and MSBuild. Raven also supports classes, interfaces, inheritance, and async methods.</p><p><a href="lang/features/dotnet-interop.md">Explore .NET interoperability</a></p></div>
  <div class="raven-example-panel"><div data-raven-playground="source"></div>
<pre><code class="lang-raven">import&#32;System.*&#10;import&#32;System.Linq.*&#10;import&#32;System.Console.*&#10;&#10;let&#32;names&#32;=&#32;[&quot;raven&quot;,&#32;&quot;dotnet&quot;,&#32;&quot;hello&quot;]&#10;let&#32;titles&#32;=&#32;names&#10;&#32;&#32;&#32;&#32;.Where(name&#32;=&gt;&#32;name.Length&#32;&gt;&#32;4)&#10;&#32;&#32;&#32;&#32;.Select(name&#32;=&gt;&#32;name.ToUpperInvariant())&#10;&#10;WriteLine(String.Join(&quot;,&#32;&quot;,&#32;titles))</code></pre></div>
</section>

<section class="raven-tooling">
  <div class="raven-section-heading"><p class="raven-eyebrow">A complete working environment</p><h2>Write, run, and understand your code.</h2><p>The SDK, editor extension, and compiler services share the same language implementation.</p></div>
  <div class="raven-tool-grid">
    <div><h3>SDK and templates</h3><p>Create console apps, libraries, and web projects. Build and run them with <code>rvn</code>.</p><a href="getting-started.md">Install the SDK</a></div>
    <div><h3>VS Code</h3><p>Completion, diagnostics, hover, navigation, and refactorings while you edit.</p><a href="compiler/raven-vscode-extension.md">Set up the extension</a></div>
    <div><h3>Compiler services</h3><p>Work with syntax trees, symbols, and semantic models. Extend analysis with analyzers and source generators.</p><a href="compiler/index.md">Explore the tools and APIs</a></div>
  </div>
  <figure class="raven-editor-figure">
    <a href="images/raven-vscode.png" aria-label="Open the full Raven editor screenshot"><img src="images/raven-vscode.png" alt="Raven in Visual Studio Code: a Quote union and match expression with a symbol hover tooltip, syntax highlighting, and inferred type hints" width="1152" height="768" loading="lazy"></a>
    <figcaption>Raven in VS Code, with symbol hover information and compiler-backed type hints.</figcaption>
  </figure>
</section>

<section class="raven-web-showcase">
  <div><p class="raven-eyebrow">Build an application</p><h2>An ASP.NET Core API, in Raven.</h2><p>The pet-shelter sample combines routing, OpenAPI, async handlers, and streaming responses with Raven records and unions.</p><a class="raven-button raven-button-primary" href="workloads/web-api.md">Build the web API</a></div>
  <div class="raven-workload-points"><div><strong>More places to explore</strong><span><a href="workloads/embedded-iot.md">Embedded IoT</a> and <a href="workloads/iot-monitor.md">Native AOT</a></span></div><div><strong>Experimental</strong><span><a href="showcases/html-components.md">Blazor component macros</a> · evolving syntax and tooling</span></div></div>
</section>

<section class="raven-reference-callout"><p><strong>Raven is in preview.</strong> Use the <a href="status.md">release and compatibility guide</a> to distinguish available releases from upcoming language changes.</p></section>
