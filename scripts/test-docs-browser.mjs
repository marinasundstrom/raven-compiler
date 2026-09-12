#!/usr/bin/env node
import assert from 'node:assert/strict';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, resolve, sep } from 'node:path';
import { chromium } from '../src/Raven.Playground/node_modules/playwright/index.mjs';

const root = resolve(process.argv[2] ?? '_site');
assert.ok(existsSync(resolve(root, 'introduction.html')), 'Build the documentation first.');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml' };
const server = createServer((req, res) => {
  const path = resolve(root, '.' + decodeURIComponent(new URL(req.url, 'http://localhost').pathname));
  const file = path.endsWith(sep) || (existsSync(path) && statSync(path).isDirectory()) ? resolve(path, 'index.html') : path;
  if (!file.startsWith(root + sep) || !existsSync(file)) { res.writeHead(404).end(); return; }
  res.setHeader('Content-Type', types[extname(file)] ?? 'application/octet-stream');
  createReadStream(file).pipe(res);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();
let page = await browser.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
function contrast(a, b) {
  const luminance = value => {
    const rgb = value.match(/[\d.]+/g).slice(0, 3).map(Number).map(n => n / 255).map(n => n <= .04045 ? n / 12.92 : ((n + .055) / 1.055) ** 2.4);
    return rgb[0] * .2126 + rgb[1] * .7152 + rgb[2] * .0722;
  };
  const x = luminance(a), y = luminance(b);
  return (Math.max(x, y) + .05) / (Math.min(x, y) + .05);
}
try {
  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    for (const path of ['index.html', 'lang/spec/index.html', 'introduction.html', 'raven-for-csharp-developers.html']) {
      await page.close();
      page = await browser.newPage({ viewport: { width, height: 900 } });
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(`${base}/${path}`);
      await page.locator('.raven-skip-link').waitFor({ state: 'attached' });
      for (const theme of ['light', 'dark']) {
        await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
        await page.waitForTimeout(250); // Allow Bootstrap color transitions to settle.
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, `${path}: overflow at ${width}px`);
        if (path === 'index.html') {
          const colors = await page.locator('.raven-button-primary').first().evaluate(e => ({ fg: getComputedStyle(e).color, bg: getComputedStyle(e).backgroundColor }));
          assert.ok(contrast(colors.fg, colors.bg) >= 4.5, `${theme}: primary button contrast`);
        }
        if (width === 390) {
          const colors = await page.getByRole('button', { name: 'Toggle navigation' }).evaluate(e => ({ fg: getComputedStyle(e).color, bg: getComputedStyle(document.body).backgroundColor }));
          // The header overlays the body with a near-opaque surface of the same theme.
          assert.ok(contrast(colors.fg, colors.bg) >= 3, `${theme}: mobile navigation contrast`);
        }
      }
      await page.keyboard.press('Tab');
      assert.equal(await page.locator('.raven-skip-link').evaluate(e => e === document.activeElement), true, `${width} ${path}: first focus ${await page.evaluate(() => document.activeElement.outerHTML.slice(0,160))}`);
      await page.keyboard.press('Enter');
      assert.equal(await page.locator('article').evaluate(e => e === document.activeElement), true);
      const samples = await page.locator('a.raven-playground-link[href*="playground/?source="]').evaluateAll(links => links.map(a => ({ href: a.href, source: a.parentElement.previousElementSibling?.querySelector('code')?.textContent })));
      for (const { href, source } of samples) {
        assert.ok(source?.trim(), `${path}: sample source is visible`);
        assert.equal(Buffer.from(new URL(href).searchParams.get('source'), 'base64url').toString(), source, `${path}: playground receives the displayed code`);
      }
    }
  }
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto(`${base}/lang/spec/index.html`);
  const menu = page.getByRole('button', { name: 'Toggle navigation' });
  await menu.focus();
  await page.keyboard.press('Enter');
  await page.locator('#navpanel.show').waitFor();
  assert.equal(await menu.getAttribute('aria-expanded'), 'true');
  await page.keyboard.press('Enter');
  await page.locator('#navpanel.show').waitFor({ state: 'hidden' });
  const toc = page.getByRole('button', { name: 'Show table of contents' });
  await toc.focus();
  await page.keyboard.press('Enter');
  await page.locator('#tocOffcanvas.show').waitFor();
  await page.keyboard.press('Escape');
  await page.locator('#tocOffcanvas.show').waitFor({ state: 'hidden' });
  assert.equal(await toc.evaluate(e => e === document.activeElement), true);
  const query = page.locator('#reference-query');
  await query.fill('nullable');
  assert.ok(await page.locator('[data-reference-topic]:visible').count() > 0);
  assert.equal(new URL(page.url()).searchParams.get('q'), 'nullable');
  await query.fill('no-such-raven-feature');
  assert.equal(await page.locator('[data-reference-topic]:visible').count(), 0);
  const clear = page.getByRole('button', { name: 'Clear', exact: true });
  await clear.focus();
  await page.keyboard.press('Enter');
  assert.equal(await query.inputValue(), '');
  assert.equal(await query.evaluate(e => e === document.activeElement), true);
  assert.equal(await page.locator('[data-reference-topic]:visible').count(), 49);
  assert.deepEqual(errors, []);
  console.log('Documentation browser checks passed: responsive layout, contrast, keyboard navigation, reference search, and example links.');
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
