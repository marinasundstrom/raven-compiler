import { createReadStream, existsSync, readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize, resolve } from "node:path";
import { chromium } from "playwright";

const siteRoot = resolve(process.argv[2] ?? "");
const basePath = "/playground/";

if (!process.argv[2] || !existsSync(join(siteRoot, "index.html"))) {
  throw new Error("Pass the published playground wwwroot directory as the first argument.");
}

const indexSource = readFileSync(join(siteRoot, "index.html"), "utf8");
if (!indexSource.includes('class="loading-shell" role="status"') ||
    !indexSource.includes('class="loading-progress"') ||
    !indexSource.includes('class="loading-progress-text"')) {
  throw new Error("Expected the Blazor host page to expose an accessible startup indicator.");
}

const contentTypes = new Map([
  [".css", "text/css; charset=utf-8"],
  [".dat", "application/octet-stream"],
  [".dll", "application/octet-stream"],
  [".html", "text/html; charset=utf-8"],
  [".ico", "image/x-icon"],
  [".js", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".png", "image/png"],
  [".wasm", "application/wasm"],
]);

const server = createServer((request, response) => {
  const requestPath = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
  if (!requestPath.startsWith(basePath)) {
    response.writeHead(404);
    response.end();
    return;
  }

  const relativePath = normalize(requestPath.slice(basePath.length))
    .replace(/^(\.\.(\/|\\|$))+/, "")
    .replace(/^[/\\]+/, "");
  let filePath = join(siteRoot, relativePath || "index.html");

  if (!filePath.startsWith(`${siteRoot}/`) || !existsSync(filePath) || statSync(filePath).isDirectory()) {
    filePath = join(siteRoot, "index.html");
  }

  response.writeHead(200, {
    "Cache-Control": "no-store",
    "Content-Type": contentTypes.get(extname(filePath)) ?? "application/octet-stream",
  });
  createReadStream(filePath).pipe(response);
});

await new Promise((resolveListen, reject) => {
  server.once("error", reject);
  server.listen(0, "127.0.0.1", resolveListen);
});

const address = server.address();
const url = `http://127.0.0.1:${address.port}${basePath}`;
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const browserErrors = [];

async function waitForSuccessfulRun(testPage, description) {
  await testPage.waitForFunction(
    () => ["Complete", "Runtime error", "Compile error"].includes(
      document.querySelector(".status-pill")?.textContent?.trim(),
    ),
    { timeout: 60_000 },
  );

  const status = (await testPage.locator(".status-pill").textContent())?.trim();
  if (status === "Complete") return;

  const diagnostics = await testPage.locator(".diagnostics li").allTextContents();
  const output = await testPage.locator(".output-panel").textContent();
  throw new Error(
    `Expected ${description} to run, got ${status}: ${diagnostics.join("\n")}\n${output}`,
  );
}

page.on("console", message => {
  if (message.type() === "error") browserErrors.push(message.text());
});
page.on("pageerror", error => browserErrors.push(error.stack ?? error.message));

try {
  await page.goto(url);
  await page.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });

  // Exercise a cold compiler worker before hover or other semantic queries can
  // initialize declarations. WebAssembly without threads must never enter a
  // blocking declaration semaphore, including on the first Emit call.
  await page.getByRole("button", { name: /^Run/ }).click();
  await waitForSuccessfulRun(page, "the cold Hello World example");
  await page.getByText("Hello from Raven in WebAssembly", { exact: true }).waitFor();

  const playgroundBrandHref = await page.locator(".raven-brand").getAttribute("href");
  const expectedBrandHref = JSON.parse(readFileSync(join(siteRoot, "appsettings.json"), "utf8")).RavenSiteRootHref;
  if (playgroundBrandHref !== expectedBrandHref) {
    throw new Error(`Expected Playground brand href '${expectedBrandHref}', got '${playgroundBrandHref}'.`);
  }

  const themeResponse = await page.request.get(`${url}css/raven-theme.css`);
  const themeContentType = themeResponse.headers()["content-type"] ?? "";
  const themeSource = await themeResponse.text();
  if (!themeResponse.ok() ||
      !themeContentType.startsWith("text/css") ||
      !themeSource.includes("--raven-bg")) {
    throw new Error(
      `Expected the standalone Raven theme stylesheet, got ` +
      `${themeResponse.status()} ${themeContentType}.`,
    );
  }
  const themeBackground = await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue("--raven-bg").trim());
  if (!themeBackground) {
    throw new Error("Expected the Raven theme variables to apply to the Playground.");
  }
  if (await page.locator(".raven-brand-copy").count() !== 0) {
    throw new Error("Expected the compact Playground header to omit the brand copy.");
  }
  const mastheadAlignment = await page.evaluate(() => {
    const mark = document.querySelector(".hero-copy .raven-brand-mark")?.getBoundingClientRect();
    const heading = document.querySelector(".hero-copy h1")?.getBoundingClientRect();
    return mark && heading
      ? { markRight: mark.right, headingLeft: heading.left }
      : null;
  });
  if (!mastheadAlignment || mastheadAlignment.markRight > mastheadAlignment.headingLeft) {
    throw new Error("Expected the Raven mark to sit before the Playground heading.");
  }

  const editor = page.locator(".monaco-editor");
  await editor.waitFor();
  const themePicker = page.getByLabel("Theme");
  await themePicker.selectOption("dark");
  await page.waitForFunction(
    () => document.documentElement.dataset.theme === "dark" &&
      document.querySelector(".monaco-editor")?.classList.contains("vs-dark"),
  );
  await themePicker.selectOption("light");
  await page.waitForFunction(
    () => document.documentElement.dataset.theme === "light" &&
      document.querySelector(".monaco-editor")?.classList.contains("vs"),
  );
  await themePicker.selectOption("system");
  const initialWorkspaceHeight = await page.locator(".workspace").evaluate(
    element => element.getBoundingClientRect().height,
  );
  await page.waitForTimeout(1_500);
  const settledWorkspaceHeight = await page.locator(".workspace").evaluate(
    element => element.getBoundingClientRect().height,
  );
  if (Math.abs(settledWorkspaceHeight - initialWorkspaceHeight) > 1) {
    throw new Error(
      `Expected the workspace height to remain stable, but it grew from ` +
      `${initialWorkspaceHeight}px to ${settledWorkspaceHeight}px.`,
    );
  }
  if (settledWorkspaceHeight > 800) {
    throw new Error(`Expected a bounded desktop workspace, got ${settledWorkspaceHeight}px.`);
  }
  const pageDimensions = await page.evaluate(() => ({
    viewportHeight: window.innerHeight,
    documentHeight: Math.max(
      document.body.scrollHeight,
      document.documentElement.scrollHeight,
    ),
  }));
  if (pageDimensions.documentHeight > pageDimensions.viewportHeight + 1) {
    throw new Error(
      `Expected the desktop playground to fit without page scrolling, but its document is ` +
      `${pageDimensions.documentHeight}px tall in a ${pageDimensions.viewportHeight}px viewport.`,
    );
  }

  const initialSource = (await editor.locator(".view-lines").textContent()).replaceAll("\u00a0", " ");
  if (!initialSource.includes("Hello from $language in WebAssembly")) {
    throw new Error(`Expected Hello World to load on startup, got ${initialSource}.`);
  }

  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.insertText('let shared = "Raven link"\nSystem.Console.WriteLine(shared)');
  await page.getByRole("button", { name: "Share", exact: true }).click();
  await page.waitForURL(url => url.searchParams.has("source"));
  const sharedUrl = page.url();
  const sharedPage = await browser.newPage();
  await sharedPage.goto(sharedUrl);
  await sharedPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  const sharedEditor = sharedPage.locator(".monaco-editor");
  await sharedEditor.waitFor();
  const sharedSource = (await sharedEditor.locator(".view-lines").textContent())
    .replaceAll("\u00a0", " ");
  if (!sharedSource.includes("Raven link")) {
    throw new Error(`Expected shared source to load from the URL, got ${sharedSource}.`);
  }
  const sharedSelection = (await sharedPage.locator(".example-picker-trigger").textContent()).trim();
  if (!sharedSelection.includes("Shared program")) {
    throw new Error(`Expected the shared program selector state, got '${sharedSelection}'.`);
  }
  await sharedPage.close();

  const sharedRunPage = await browser.newPage();
  const sharedRunUrl = new URL(sharedUrl);
  sharedRunUrl.searchParams.set("run", "true");
  await sharedRunPage.goto(sharedRunUrl.href);
  await sharedRunPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  await sharedRunPage.waitForTimeout(500);
  const sharedRunStatus = (await sharedRunPage.locator(".status-pill").textContent())?.trim();
  if (sharedRunStatus !== "Ready") {
    throw new Error(`Expected run=true to be ignored for shared source, got ${sharedRunStatus}.`);
  }
  await sharedRunPage.close();

  const invalidSharedPage = await browser.newPage();
  await invalidSharedPage.goto(`${url}?source=invalid!`);
  await invalidSharedPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  const fallbackSource = (await invalidSharedPage.locator(".monaco-editor .view-lines").textContent())
    .replaceAll("\u00a0", " ");
  if (!fallbackSource.includes("Hello from $language in WebAssembly")) {
    throw new Error(`Expected an invalid share URL to load Hello World, got ${fallbackSource}.`);
  }
  await invalidSharedPage.getByText(/shared source in this link is invalid/).waitFor();
  await invalidSharedPage.close();

  const predefinedPage = await browser.newPage();
  await predefinedPage.goto(`${url}?example=records`);
  await predefinedPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  const predefinedSource = (await predefinedPage.locator(".monaco-editor .view-lines").textContent())
    .replaceAll("\u00a0", " ");
  if (!predefinedSource.includes("record Shipment")) {
    throw new Error(`Expected the records example to load from the URL, got ${predefinedSource}.`);
  }
  const predefinedSelection = (await predefinedPage.locator(".example-picker-trigger").textContent()).trim();
  if (!predefinedSelection.includes("Records")) {
    throw new Error(`Expected the records selector state, got '${predefinedSelection}'.`);
  }
  await predefinedPage.close();

  const macroAliasPage = await browser.newPage();
  await macroAliasPage.goto(`${url}?example=quote-macro`);
  await macroAliasPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  await macroAliasPage.locator(".monaco-editor").waitFor();
  try {
    await macroAliasPage.waitForFunction(() => {
      const tokenColor = text => {
        const walker = document.createTreeWalker(
          document.querySelector(".monaco-editor .view-lines"),
          NodeFilter.SHOW_TEXT,
        );
        while (walker.nextNode()) {
          if (walker.currentNode.nodeValue.includes(text))
            return getComputedStyle(walker.currentNode.parentElement).color;
        }
        return null;
      };
      const keywordColor = tokenColor("let");
      const aliasColor = tokenColor("quote");
      return keywordColor && aliasColor && keywordColor === aliasColor;
    }, { timeout: 30_000 });
  } catch (error) {
    const visibleTokens = await macroAliasPage.locator(".monaco-editor .view-lines span")
      .evaluateAll(elements => elements
        .filter(element => element.children.length === 0 && element.textContent.trim())
        .map(element => ({
          text: element.textContent,
          color: getComputedStyle(element).color,
          className: element.className,
        })));
    throw new Error(
      `Expected quote! to use the keyword color. Visible tokens: ${JSON.stringify(visibleTokens)}`,
      { cause: error },
    );
  }
  await macroAliasPage.close();

  const runningPredefinedPage = await browser.newPage();
  await runningPredefinedPage.goto(`${url}?example=records&run=true`);
  await waitForSuccessfulRun(runningPredefinedPage, "the trusted records example");
  const runningPredefinedOutput = await runningPredefinedPage.locator(".program-output").textContent();
  if (!runningPredefinedOutput.includes("Shipment 42 weighs 3.5 kg")) {
    throw new Error(`Expected the trusted records example to run from the URL, got ${runningPredefinedOutput}.`);
  }
  await runningPredefinedPage.close();

  const documentationSnippetPage = await browser.newPage();
  await documentationSnippetPage.goto(`${url}?snippet=shipment-quote&run=true`);
  await waitForSuccessfulRun(documentationSnippetPage, "the trusted documentation snippet");
  const documentationSnippetOutput = await documentationSnippetPage.locator(".program-output").textContent();
  if (!documentationSnippetOutput.includes("Quote:")) {
    throw new Error(`Expected the trusted documentation snippet to run, got ${documentationSnippetOutput}.`);
  }
  const documentationSnippetSelection =
    (await documentationSnippetPage.locator(".example-picker-trigger").textContent()).trim();
  if (!documentationSnippetSelection.includes("Documentation: Shipment quote")) {
    throw new Error(`Expected a documentation-only selector state, got '${documentationSnippetSelection}'.`);
  }
  await documentationSnippetPage.close();

  const builtInMacrosPage = await browser.newPage();
  await builtInMacrosPage.goto(`${url}?example=built-in-macros&run=true`);
  await waitForSuccessfulRun(builtInMacrosPage, "the built-in macro forms example");
  const builtInMacrosOutput = await builtInMacrosPage.locator(".program-output").textContent();
  if (!builtInMacrosOutput.includes("Query total: 70") ||
      !builtInMacrosOutput.includes("Invalid value: age")) {
    throw new Error(`Expected built-in macro output, got '${builtInMacrosOutput}'.`);
  }
  await builtInMacrosPage.close();

  const externalExamplePage = await browser.newPage();
  await externalExamplePage.goto(`${url}?example=${encodeURIComponent("https://example.com/program.rvn")}&run=true`);
  await externalExamplePage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  await externalExamplePage.waitForTimeout(500);
  const externalFallbackSource = (await externalExamplePage.locator(".monaco-editor .view-lines").textContent())
    .replaceAll("\u00a0", " ");
  if (!externalFallbackSource.includes("Hello from $language in WebAssembly")) {
    throw new Error("Expected a non-bundled example value to fall back without loading a URL.");
  }
  const externalExampleStatus = (await externalExamplePage.locator(".status-pill").textContent())?.trim();
  if (externalExampleStatus !== "Ready") {
    throw new Error(`Expected run=true to be ignored for a non-bundled example, got ${externalExampleStatus}.`);
  }
  await externalExamplePage.getByText(/Playground example was not found/).waitFor();
  await externalExamplePage.close();

  const externalSnippetPage = await browser.newPage();
  await externalSnippetPage.goto(`${url}?snippet=${encodeURIComponent("https://example.com/program.rvn")}&run=true`);
  await externalSnippetPage.getByText("Ready", { exact: true }).waitFor({ timeout: 30_000 });
  await externalSnippetPage.waitForTimeout(500);
  const externalSnippetStatus = (await externalSnippetPage.locator(".status-pill").textContent())?.trim();
  if (externalSnippetStatus !== "Ready") {
    throw new Error(`Expected run=true to be ignored for a non-bundled snippet, got ${externalSnippetStatus}.`);
  }
  await externalSnippetPage.getByText(/documentation snippet was not found/).waitFor();
  await externalSnippetPage.close();

  const tokenClasses = await editor.locator(".view-lines span[class]").evaluateAll(elements =>
    [...new Set(elements.map(element => element.className).filter(className => /^mtk\d+$/.test(className)))],
  );
  if (tokenClasses.length < 2) {
    throw new Error(`Expected TextMate highlighting to produce multiple token classes, got ${tokenClasses}.`);
  }

  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.insertText('let greeting = "Raven"\nSystem.Console.WriteLine(greeting)');
  await page.evaluate(() => {
    window.ravenHoverHeartbeat = 0;
    window.ravenHoverStartedAt = performance.now();
    window.ravenHoverHeartbeatTimer = window.setInterval(() => {
      window.ravenHoverHeartbeat++;
    }, 20);
  });
  const showHover = async (lineText, token) => {
    const line = editor.locator(".view-line").filter({ hasText: lineText });
    const tokenPosition = await line.evaluate((element, tokenText) => {
    const text = element.textContent.replaceAll("\u00a0", " ");
    const start = text.indexOf(tokenText);
    const end = start + tokenText.length;
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    const range = document.createRange();
    let offset = 0;
    let node;
    while ((node = walker.nextNode())) {
      const nextOffset = offset + node.textContent.length;
      if (start >= offset && start <= nextOffset)
        range.setStart(node, start - offset);
      if (end >= offset && end <= nextOffset) {
        range.setEnd(node, end - offset);
        break;
      }
      offset = nextOffset;
    }
    const bounds = range.getBoundingClientRect();
    return { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
    }, token);
    await page.mouse.click(tokenPosition.x, tokenPosition.y);
    const commandKey = process.platform === "darwin" ? "Meta" : "Control";
    await page.keyboard.press(`${commandKey}+K`);
    await page.keyboard.press(`${commandKey}+I`);
    const hoverWidget = page.locator(".monaco-hover:visible");
    try {
      await hoverWidget.waitFor({ timeout: 30_000 });
    } catch (error) {
      throw new Error(
        `Compiler-backed hover did not appear for '${token}'.\n` +
        `Browser errors:\n${browserErrors.join("\n") || "<none>"}`,
        { cause: error },
      );
    }
    const hoverText = await hoverWidget.textContent();
    await page.keyboard.press("Escape");
    return hoverText;
  };
  const declarationHover = await showHover("let greeting", "greeting");
  if (!declarationHover.includes("greeting") ||
      !declarationHover.toLowerCase().includes("string")) {
    throw new Error(`Expected compiler-backed hover for greeting, got '${declarationHover}'.`);
  }
  const invocationHover = await showHover("WriteLine(greeting)", "WriteLine");
  if (!invocationHover.includes("WriteLine")) {
    throw new Error(`Expected compiler-backed hover for the member invocation, got '${invocationHover}'.`);
  }
  const hoverResponsiveness = await page.evaluate(() => {
    window.clearInterval(window.ravenHoverHeartbeatTimer);
    return {
      elapsed: performance.now() - window.ravenHoverStartedAt,
      heartbeat: window.ravenHoverHeartbeat,
    };
  });
  if (hoverResponsiveness.elapsed >= 100 && hoverResponsiveness.heartbeat < 3) {
    throw new Error(
      `Expected the UI thread to remain responsive during hover, got ` +
      `${hoverResponsiveness.heartbeat} ticks in ${hoverResponsiveness.elapsed}ms.`,
    );
  }
  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.evaluate(() => {
    window.ravenCompletionHeartbeat = 0;
    window.ravenCompletionHeartbeatTimer = window.setInterval(() => {
      window.ravenCompletionHeartbeat++;
    }, 20);
  });
  await page.keyboard.type("System.Console.");
  const suggestionWidget = page.locator(".suggest-widget.visible");
  const beepSuggestion = page.locator(".suggest-widget .monaco-list-row", {
    hasText: "Beep",
  });
  await suggestionWidget.waitFor({ timeout: 30_000 });
  try {
    await beepSuggestion.first().waitFor({ timeout: 30_000 });
  } catch (error) {
    throw new Error(
      `Console member completion did not appear.\nBrowser errors:\n${browserErrors.join("\n") || "<none>"}`,
      { cause: error },
    );
  }
  const completionHeartbeat = await page.evaluate(() => {
    window.clearInterval(window.ravenCompletionHeartbeatTimer);
    return window.ravenCompletionHeartbeat;
  });
  if (completionHeartbeat < 5) {
    throw new Error(`Expected the UI thread to remain responsive during completion, got ${completionHeartbeat} ticks.`);
  }
  await beepSuggestion.first().dblclick();
  await page.waitForTimeout(100);
  const completedSource = await editor.locator(".view-lines").textContent();
  if (!completedSource.includes("System.Console.Beep")) {
    throw new Error(`Expected accepting completion to insert Beep, got ${completedSource}.`);
  }

  await page.keyboard.press("Escape");
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type('let value = "Raven"\nvalue.Len');
  const lengthSuggestion = page.locator(".suggest-widget .monaco-list-row", { hasText: "Length" });
  await lengthSuggestion.first().waitFor({ timeout: 30_000 });

  await page.keyboard.press("Escape");
  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type("re");
  await page.waitForTimeout(500);
  if (await page.locator(".suggest-widget.visible").count() !== 0) {
    throw new Error("Expected automatic completion to wait for a meaningful prefix.");
  }
  await page.keyboard.type("t");
  const returnSuggestion = page.locator(".suggest-widget .monaco-list-row", { hasText: "return" });
  await returnSuggestion.first().waitFor({ timeout: 30_000 });

  await page.keyboard.press("Escape");
  await page.keyboard.press("Control+Space");
  await returnSuggestion.first().waitFor({ timeout: 30_000 });

  await page.keyboard.press("Escape");
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type("// ret");
  await page.waitForTimeout(750);
  if (await page.locator(".suggest-widget.visible").count() !== 0) {
    throw new Error("Expected automatic completion to stay hidden while typing a comment.");
  }

  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type('let value = "ret"');
  await page.waitForTimeout(750);
  if (await page.locator(".suggest-widget.visible").count() !== 0) {
    throw new Error("Expected automatic completion to stay hidden while typing a string.");
  }

  const examplePicker = page.locator(".example-picker-trigger");
  const examples = await (await fetch(`${url}examples/index.json`)).json();
  if (examples.some(example => !example.category)) {
    throw new Error("Expected every example to belong to a category.");
  }
  await examplePicker.click();
  const exampleDialog = page.getByRole("dialog", { name: "Choose an example" });
  await exampleDialog.getByRole("heading", { name: "Basics" }).waitFor();
  await exampleDialog.getByRole("heading", { name: "Unions and patterns" }).waitFor();
  const exampleSearch = exampleDialog.getByRole("searchbox", { name: "Search examples" });
  await exampleSearch.fill("typestate");
  const filteredOptions = exampleDialog.getByRole("option");
  if (await filteredOptions.count() !== 1 ||
      !(await filteredOptions.first().textContent()).includes("State-safe connections")) {
    throw new Error("Expected example search to filter the grouped list.");
  }
  await exampleDialog.getByRole("button", { name: "Clear example search" }).click();
  if (await exampleDialog.getByRole("option").count() !== examples.length) {
    throw new Error("Expected clearing example search to restore every example.");
  }
  await page.keyboard.press("Escape");
  await exampleDialog.waitFor({ state: "hidden" });

  await page.getByRole("link", { name: "Documentation" }).waitFor();
  await page.getByRole("link", { name: "GitHub" }).waitFor();
  for (const example of examples) {
    await examplePicker.click();
    await page.getByRole("dialog", { name: "Choose an example" })
      .getByRole("option", { name: example.title, exact: true })
      .click();
    await page.waitForFunction(
      id => {
        const parameters = new URL(window.location.href).searchParams;
        return parameters.get("example") === id && !parameters.has("source");
      },
      example.id,
      { timeout: 30_000 },
    );
    await page.getByRole("button", { name: /^Run/ }).click();
    await page.waitForFunction(
      () => ["Complete", "Compile error", "Runtime error"].includes(
        document.querySelector(".status-pill")?.textContent?.trim(),
      ),
      { timeout: 30_000 },
    );
    const exampleStatus = (await page.locator(".status-pill").textContent())?.trim();
    if (exampleStatus !== "Complete") {
      const output = await page.locator(".output-panel").textContent();
      throw new Error(
        `Expected example '${example.id}' to run, got ${exampleStatus}: ${output}\n` +
        `Browser errors:\n${browserErrors.join("\n") || "<none>"}`,
      );
    }
    console.log(`Passed example '${example.id}'.`);
  }

  await page.keyboard.press("Escape");
  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type(
    'import System.*\n\nlet greeting = "Hello from Raven in WebAssembly"\nConsole.WriteLine(greeting)',
  );
  await page.getByRole("button", { name: /Compile/ }).click();
  await page.locator(".operation-progress").waitFor({ timeout: 5_000 });
  await page.waitForFunction(
    () => document.querySelector(".status-pill")?.textContent?.trim() !== "Compiling",
    { timeout: 30_000 },
  );
  const resultStatus = (await page.locator(".status-pill").textContent())?.trim();
  if (resultStatus !== "Compiled") {
    const diagnostics = await page.locator(".diagnostics li").allTextContents();
    throw new Error(`Expected greeting source to compile, got ${resultStatus}: ${diagnostics.join("\n")}`);
  }
  await page.getByText(/Compiled successfully/).waitFor();

  await page.getByRole("button", { name: /^Run/ }).click();
  await page.getByText("Complete", { exact: true }).waitFor({ timeout: 30_000 });
  await page.getByText("Hello from Raven in WebAssembly", { exact: true }).waitFor();

  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type("let =");
  await page.getByRole("button", { name: /Compile/ }).click();
  await page.getByText("Compile error", { exact: true }).waitFor({ timeout: 30_000 });
  if (await page.locator(".diagnostics li").count() === 0) {
    throw new Error("Expected invalid Raven source to produce at least one diagnostic.");
  }
  const diagnosticLocation = (await page.locator(".diagnostic-location").first().textContent())?.trim();
  if (!/^1:\d+$/.test(diagnosticLocation ?? "")) {
    throw new Error(`Expected a line:column diagnostic start location, got '${diagnosticLocation}'.`);
  }

  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.insertText(
    [
      "import System.*",
      "",
      "func test<T>(value: T) -> Result<T, CustomError> {",
      "    return Ok(value)",
      "}",
      "",
      "Console.WriteLine(test<int>(42))",
      "",
      "record class CustomError()",
    ].join("\n"),
  );
  await page.getByRole("button", { name: /^Run/ }).click();
  await page.waitForFunction(
    () => ["Complete", "Runtime error"].includes(
      document.querySelector(".status-pill")?.textContent?.trim(),
    ),
    { timeout: 30_000 },
  );
  const resultRecordStatus = (await page.locator(".status-pill").textContent())?.trim();
  if (resultRecordStatus !== "Complete") {
    const diagnostics = await page.locator(".diagnostics li").allTextContents();
    const output = await page.locator(".output-panel").textContent();
    throw new Error(
      `Expected Result/record source to run, got ${resultRecordStatus}: ${diagnostics.join("\n")}\n${output}`,
    );
  }
  const resultRecordOutput = await page.locator(".program-output").textContent();
  if (!resultRecordOutput.includes("Result.Ok(42)")) {
    throw new Error(`Expected Result/record output, got '${resultRecordOutput}'.`);
  }

  await editor.click({ force: true });
  await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
  await page.keyboard.type(
    'let greeting = "Hello from Raven in WebAssembly"\nSystem.Console.WriteLine(greeting)',
  );
  await page.getByRole("button", { name: /^Run/ }).click();
  await page.getByText("Complete", { exact: true }).waitFor({ timeout: 30_000 });
  await page.getByText("Hello from Raven in WebAssembly", { exact: true }).waitFor();

  const finalWorkspaceHeight = await page.locator(".workspace").evaluate(
    element => element.getBoundingClientRect().height,
  );
  if (Math.abs(finalWorkspaceHeight - settledWorkspaceHeight) > 1) {
    throw new Error(
      `Expected editor activity not to resize the workspace, but it changed from ` +
      `${settledWorkspaceHeight}px to ${finalWorkspaceHeight}px.`,
    );
  }

  if (browserErrors.length > 0) {
    throw new Error(`Browser errors:\n${browserErrors.join("\n")}`);
  }

  console.log("Playground browser smoke test passed.");
} finally {
  await browser.close();
  await new Promise(resolveClose => server.close(resolveClose));
}
