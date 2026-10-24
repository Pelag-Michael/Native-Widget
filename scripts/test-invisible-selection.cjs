// Run with PLAYWRIGHT_MODULE pointing to an installed playwright module.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');

(async () => {
  const extension = path.resolve(__dirname, '../extensions/invisible-selection');
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'selection-browser-'));
  const server = http.createServer((req, res) => {
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    res.end(`<!doctype html><style>
      body { font: 24px Arial; color: #243b53; background: white; }
      *::selection { background: red !important; color: white !important; }
      span { color: #a12438; } input,textarea { color: #294d12; }
      </style><p id="text">Select this <span>colored text</span> and copy.</p>
      <input value="Input selection"><textarea>Textarea selection</textarea>
      ${req.url === '/frame' ? '' : '<iframe src="/frame"></iframe><iframe srcdoc="<p>Inline frame text</p>"></iframe>'}`);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let context;
  try {
    context = await chromium.launchPersistentContext(profile, {
      headless: true,
      executablePath: process.env.CHROMIUM_PATH || undefined,
      ignoreDefaultArgs: ['--disable-extensions'],
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
      permissions: ['clipboard-read', 'clipboard-write']
    });
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
    const page = await context.newPage();
    const url = `http://127.0.0.1:${server.address().port}`;
    async function assertHidden(target) {
      await target.waitForFunction(() =>
        getComputedStyle(document.querySelector('p'), '::selection').backgroundColor === 'rgba(0, 0, 0, 0)');
    }
    await page.goto(url);
    await assertHidden(page);
    await page.waitForFunction(() => document.querySelectorAll('iframe').length === 2);
    for (const frame of page.frames().slice(1)) await assertHidden(frame);
    const before = await page.locator('#text').screenshot();
    const selection = await page.evaluate(() => {
      const range = document.createRange();
      range.selectNodeContents(document.querySelector('#text'));
      getSelection().removeAllRanges();
      getSelection().addRange(range);
      return getSelection().toString();
    });
    assert.equal(selection, 'Select this colored text and copy.');
    assert.ok((await page.locator('#text').screenshot()).equals(before), 'Selecting must not change rendered pixels');
    await page.keyboard.press('Control+c');
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), selection);
    for (const selector of ['input', 'textarea']) {
      await page.locator(selector).focus();
      await page.keyboard.press('Control+a');
      await page.keyboard.press('Control+c');
      assert.equal(await page.evaluate(() => navigator.clipboard.readText()), await page.locator(selector).inputValue());
    }
    // The exact handler used by toolbar clicks; also verifies stored OFF survives navigation.
    await worker.evaluate(() => enqueue(toggle));
    await page.waitForFunction(() => getComputedStyle(document.querySelector('p'), '::selection').backgroundColor === 'rgb(255, 0, 0)');
    await page.reload();
    assert.equal(await worker.evaluate(() => isEnabled()), false);
    assert.equal(await page.locator('p').first().evaluate(el => getComputedStyle(el, '::selection').backgroundColor), 'rgb(255, 0, 0)');
    await worker.evaluate(() => enqueue(toggle));
    await assertHidden(page);
    await page.reload();
    await assertHidden(page);
    const next = await context.newPage();
    await next.goto(url);
    await assertHidden(next);
    await worker.evaluate(() => Promise.all([enqueue(toggle), enqueue(toggle), enqueue(toggle), enqueue(toggle)]));
    await assertHidden(next);
    assert.equal(await worker.evaluate(() => chrome.action.getBadgeText({})), 'ON');
    console.log('PASS: identical selected pixels; original text colors; copy; inputs; both iframe types; toggle; reload; new tab; rapid clicks.');
  } finally {
    if (context) await context.close();
    await new Promise(resolve => server.close(resolve));
    await fs.rm(profile, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
