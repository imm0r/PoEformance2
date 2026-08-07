/*
 * End-to-end smoke test against a fake GitHub API.
 *
 * The app is almost entirely "does the browser do what I think it does" —
 * canvas re-encoding, object URLs, IntersectionObserver, the Content-Security-
 * Policy, the fragment scrubbing. None of that can be checked by reading the
 * source, so this drives a real Chromium against a stubbed api.github.com and
 * asserts on what actually happens.
 *
 * Usage: node photoshare/tools/e2e.mjs      (add --headed to watch it)
 */
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Resolve playwright from this checkout first, then from a global install —
// which is what the prepared container in Claude Code on the web has.
function loadPlaywright() {
  for (const base of [import.meta.url, '/opt/node22/lib/node_modules/']) {
    try { return createRequire(base)('playwright'); } catch { /* try the next one */ }
  }
  throw new Error('playwright is missing. Run: cd photoshare && npm install && npx playwright install chromium');
}
const { chromium } = loadPlaywright();

const APP = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../app');
const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.webmanifest': 'application/manifest+json'
};

const checks = [];
function check(name, condition, detail) {
  checks.push({ name, ok: !!condition, detail });
  console.log(`${condition ? 'ok  ' : 'FAIL'}  ${name}${condition || !detail ? '' : `\n        ${detail}`}`);
}

// --- static server --------------------------------------------------------

const server = createServer(async (req, res) => {
  const name = decodeURIComponent(req.url.split('?')[0]);
  const file = path.join(APP, name === '/' ? '/index.html' : name);
  if (!file.startsWith(APP)) { res.writeHead(403).end(); return; }
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404).end('not found');
  }
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}`;

// --- fake repository ------------------------------------------------------

function makeRepo() {
  return {
    tree: new Map(),      // path -> {sha, bytes}
    puts: [],
    deletes: [],
    blobHits: 0,
    add(p, bytes) { this.tree.set(p, { sha: `sha-${this.tree.size}-${p.length}`, bytes }); }
  };
}

async function stubGitHub(page, repo, { writable = true, noBranch = false, noRepo = false } = {}) {
  await page.route('https://api.github.com/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (status, body, headers = {}) => route.fulfill({
      status, headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body)
    });

    if (!request.headers()['authorization']) return json(401, { message: 'no token' });

    if (/\/repos\/[^/]+\/[^/]+$/.test(url.pathname)) {
      if (noRepo) return json(404, { message: 'Not Found' });
      return json(200, { full_name: 'fam/album', private: true });
    }
    if (url.pathname.includes('/git/trees/')) {
      // A repository with no commits has no branch for the tree endpoint to list.
      if (noBranch || noRepo) return json(404, { message: 'Not Found' });
      return json(200, {
        sha: 'tree', truncated: false,
        tree: [...repo.tree.entries()].map(([p, v]) => ({
          path: p, type: 'blob', sha: v.sha, size: v.bytes.length
        }))
      });
    }
    if (url.pathname.includes('/git/blobs/')) {
      const sha = url.pathname.split('/').pop();
      const entry = [...repo.tree.values()].find((v) => v.sha === sha);
      if (!entry) return json(404, { message: 'no blob' });
      repo.blobHits++;
      return route.fulfill({ status: 200, headers: { 'Content-Type': 'image/jpeg' }, body: Buffer.from(entry.bytes) });
    }
    if (request.method() === 'DELETE' && url.pathname.includes('/contents/')) {
      if (!writable) return json(403, { message: 'read only' });
      const filePath = decodeURIComponent(url.pathname.split('/contents/')[1]);
      const entry = repo.tree.get(filePath);
      if (!entry) return json(404, { message: 'no such file' });
      if (JSON.parse(request.postData()).sha !== entry.sha) return json(409, { message: 'sha mismatch' });
      repo.deletes.push(filePath);
      repo.tree.delete(filePath);
      return json(200, { commit: {} });
    }
    if (request.method() === 'PUT' && url.pathname.includes('/contents/')) {
      if (!writable) return json(403, { message: 'read only' });
      const body = JSON.parse(request.postData());
      const filePath = decodeURIComponent(url.pathname.split('/contents/')[1]);
      repo.puts.push({ path: filePath, size: Buffer.from(body.content, 'base64').length });
      repo.add(filePath, Buffer.from(body.content, 'base64'));
      return json(201, { content: { path: filePath } });
    }
    return json(404, { message: 'unhandled ' + url.pathname });
  });
}

// --- helpers --------------------------------------------------------------

const browser = await chromium.launch({ headless: !process.argv.includes('--headed') });

async function newPage() {
  const context = await browser.newContext({ viewport: { width: 420, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page._errors = errors;
  return page;
}

// Fixtures are painted on a throwaway page: generating them on a page under
// test would mean navigating it twice, and the second navigation to the same
// document with only a different fragment never reloads.
const fixtureContext = await browser.newContext();
const fixturePage = await fixtureContext.newPage();
await fixturePage.goto('about:blank');

/** A real JPEG, painted and encoded by the browser itself. */
async function makeJpeg(w, h, hue) {
  const page = fixturePage;
  return Buffer.from(await page.evaluate(async ([w, h, hue]) => {
    const canvas = document.createElement('canvas');
    canvas.width = w; canvas.height = h;
    const ctx = canvas.getContext('2d');
    const gradient = ctx.createLinearGradient(0, 0, w, h);
    gradient.addColorStop(0, `hsl(${hue},70%,60%)`);
    gradient.addColorStop(1, `hsl(${(hue + 90) % 360},70%,25%)`);
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = '#fff';
    ctx.font = `${Math.round(h / 6)}px serif`;
    ctx.fillText('Foto', w / 8, h / 2);
    // A smooth gradient compresses to almost nothing, which would make the
    // "did it actually shrink" assertions meaningless. Grain makes the fixture
    // behave like a photo out of a camera.
    const frame = ctx.getImageData(0, 0, w, h);
    for (let i = 0; i < frame.data.length; i += 4) {
      const n = (Math.random() * 52) - 26;
      frame.data[i] += n; frame.data[i + 1] += n; frame.data[i + 2] += n;
    }
    ctx.putImageData(frame, 0, 0);
    const blob = await new Promise((r) => canvas.toBlob(r, 'image/jpeg', 0.9));
    return Array.from(new Uint8Array(await blob.arrayBuffer()));
  }, [w, h, hue]));
}

/** Long edge of a JPEG, straight out of its SOF marker. */
function jpegSize(bytes) {
  for (let i = 2; i + 9 < bytes.length;) {
    if (bytes[i] !== 0xff) return null;
    const marker = bytes[i + 1];
    const length = (bytes[i + 2] << 8) | bytes[i + 3];
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      return { height: (bytes[i + 5] << 8) | bytes[i + 6], width: (bytes[i + 7] << 8) | bytes[i + 8] };
    }
    if (marker === 0xda) return null;
    i += 2 + length;
  }
  return null;
}

const LINK = (page, extra = '') =>
  `${origin}/${page}#r=fam/album&k=github_pat_testtoken1234567890&t=${encodeURIComponent('Familientreffen')}${extra}`;

// --- 1. gate --------------------------------------------------------------
{
  const page = await newPage();
  await page.goto(`${origin}/index.html`);
  await page.waitForSelector('.gate');
  check('gate: asks for a code when the app has none', await page.isVisible('.gate__title'));

  await page.fill('.gate .field', 'völliger unsinn');
  await page.click('.gate .btn');
  check('gate: rejects nonsense', (await page.textContent('.gate__hint')).includes('kompletten Link'));

  // Tapping the share link while the page is already open is a same-document
  // navigation: the browser does not reload, so the app has to notice itself.
  const repo = makeRepo();
  await stubGitHub(page, repo);
  repo.add('photos/2026-08-07/120000__Jonas__11111111.jpg', await makeJpeg(200, 200, 10));
  repo.add('thumbs/2026-08-07/120000__Jonas__11111111.jpg', await makeJpeg(200, 200, 10));
  await page.goto(LINK('index.html'));
  await page.waitForSelector('.tile', { timeout: 10000 });
  check('gate: a share link tapped on an open page still lets you in', true);
  await page.context().close();
}

// --- 2. gallery -----------------------------------------------------------
{
  const page = await newPage();
  const repo = makeRepo();
  await stubGitHub(page, repo);
  const jpeg = await makeJpeg(400, 300, 30);
  const jpeg2 = await makeJpeg(300, 400, 200);
  for (const [day, time, who, id, bytes] of [
    ['2026-08-07', '153012', 'Oma-Lotte', 'aaaaaaaa', jpeg],
    ['2026-08-07', '161500', 'Jonas', 'bbbbbbbb', jpeg2],
    ['2026-08-06', '090000', 'Oma-Lotte', 'cccccccc', jpeg]
  ]) {
    const name = `${day}/${time}__${who}__${id}.jpg`;
    repo.add(`photos/${name}`, bytes);
    repo.add(`thumbs/${name}`, bytes);
  }
  // A thumbnail whose full-size photo never made it up must stay hidden.
  repo.add('thumbs/2026-08-07/170000__Jonas__dddddddd.jpg', jpeg);

  await page.goto(LINK('index.html'));
  await page.waitForSelector('.tile.is-loaded');

  check('gallery: fragment is scrubbed from the URL', page.url() === `${origin}/index.html`, page.url());
  check('gallery: title comes from the link', (await page.textContent('.topbar__title')) === 'Familientreffen');
  check('gallery: shows only complete photos', (await page.locator('.tile').count()) === 3,
    `found ${await page.locator('.tile').count()} tiles`);
  check('gallery: groups by day, newest first',
    (await page.locator('.day').first().textContent()).includes('7. August 2026'));
  check('gallery: offers a filter per uploader', (await page.locator('.chip').count()) === 3);

  await page.locator('.chip', { hasText: 'Jonas' }).click();
  check('gallery: filter narrows the grid', (await page.locator('.tile').count()) === 1);
  await page.locator('.chip', { hasText: 'Alle' }).click();

  await page.locator('.tile').first().click();
  await page.waitForSelector('.lightbox:not(.is-hidden)');
  await page.waitForFunction(() => {
    const img = document.querySelector('.lightbox__image');
    return img && img.naturalWidth > 0;
  });
  check('lightbox: opens with a decoded photo', true);
  check('lightbox: captions who and when',
    (await page.textContent('.lightbox__caption')).includes('Jonas'),
    await page.textContent('.lightbox__caption'));

  await page.keyboard.press('ArrowRight');
  await page.waitForTimeout(250);
  check('lightbox: arrow keys move on',
    (await page.textContent('.lightbox__caption')).includes('Oma Lotte'),
    await page.textContent('.lightbox__caption'));

  await page.keyboard.press('Escape');
  check('lightbox: escape closes', await page.locator('.lightbox.is-hidden').count() === 1);

  const before = repo.blobHits;
  await page.reload();
  await page.waitForSelector('.tile.is-loaded');
  await page.waitForTimeout(400);
  check('gallery: thumbnails come from cache on the second visit',
    repo.blobHits === before, `${repo.blobHits - before} refetches`);

  check('gallery: no page errors', page._errors.length === 0, page._errors.join('\n'));
  await page.context().close();
}

// --- 3. upload ------------------------------------------------------------
{
  const page = await newPage();
  const repo = makeRepo();
  await stubGitHub(page, repo);
  await page.goto(LINK('upload.html'));
  await page.waitForSelector('.drop');

  const big = await makeJpeg(4000, 3000, 120);
  check('upload: fixture is a phone-sized photo', big.length > 1_000_000, `${big.length} bytes`);

  await page.click('.btn--big:not(.btn--camera)');
  check('upload: refuses to start without a name', (await page.locator('.toast').count()) === 1);

  await page.fill('.field', 'Oma Lotte');
  await page.setInputFiles('input[type=file]', [
    { name: 'IMG_0001.jpg', mimeType: 'image/jpeg', buffer: big }
  ]);
  await page.waitForSelector('.job--done', { timeout: 30000 });

  check('upload: wrote photo and thumbnail', repo.puts.length === 2, JSON.stringify(repo.puts));
  const photo = repo.puts.find((p) => p.path.startsWith('photos/'));
  const thumb = repo.puts.find((p) => p.path.startsWith('thumbs/'));
  check('upload: photo path carries day, time, name and hash',
    /^photos\/\d{4}-\d{2}-\d{2}\/\d{6}__Oma-Lotte__[0-9a-f]{8}\.jpg$/.test(photo.path), photo.path);
  check('upload: thumbnail mirrors the photo path',
    thumb.path === photo.path.replace('photos/', 'thumbs/'), thumb.path);
  check('upload: photo was uploaded before its thumbnail',
    repo.puts[0].path.startsWith('photos/'), repo.puts[0].path);
  // The fixture is far grainier than a real photo (grain is the one thing JPEG
  // cannot throw away), so judge the resize by ratio rather than by an absolute
  // size a real 12 MP photo would beat comfortably.
  check('upload: 12 MP photo shrinks to a fraction of the original',
    photo.size < big.length / 3 && photo.size < 1_500_000,
    `${big.length} bytes in, ${photo.size} bytes out`);
  check('upload: thumbnail is tiny', thumb.size < 80_000, `${thumb.size} bytes`);

  const photoBytes = repo.tree.get(photo.path).bytes;
  const thumbBytes = repo.tree.get(thumb.path).bytes;
  const photoDim = jpegSize(photoBytes), thumbDim = jpegSize(thumbBytes);
  check('upload: photo is scaled to a 2560px long edge',
    Math.max(photoDim.width, photoDim.height) === 2560, JSON.stringify(photoDim));
  check('upload: aspect ratio survives the resize',
    Math.abs(photoDim.width / photoDim.height - 4000 / 3000) < 0.01, JSON.stringify(photoDim));
  check('upload: thumbnail is scaled to a 480px long edge',
    Math.max(thumbDim.width, thumbDim.height) === 480, JSON.stringify(thumbDim));
  check('upload: reports what happened', (await page.textContent('.summary')).includes('Album'));

  // Same file again: content-addressed, so it must be recognised, not duplicated.
  await page.setInputFiles('input[type=file]', [
    { name: 'kopie.jpg', mimeType: 'image/jpeg', buffer: big }
  ]);
  await page.waitForSelector('.job--skip');
  check('upload: the same photo twice is a no-op', repo.puts.length === 2, JSON.stringify(repo.puts.map(p => p.path)));

  check('upload: no page errors', page._errors.length === 0, page._errors.join('\n'));
  await page.context().close();
}

// --- 3b. taking a photo goes straight into the album ----------------------
{
  // A phone context, so the (hover: none) rules that show the camera button apply.
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true
  });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  page._errors = errors;

  const repo = makeRepo();
  await stubGitHub(page, repo);
  await page.goto(LINK('upload.html'));
  await page.waitForSelector('.drop');

  const camera = page.locator('input[capture]');
  check('camera: the button is offered on a phone', await page.isVisible('.btn--camera'));
  check('camera: opens the rear camera, not the picker',
    await camera.getAttribute('capture') === 'environment' &&
    await camera.getAttribute('accept') === 'image/*');
  check('camera: shoots one photo at a time', await camera.getAttribute('multiple') === null);

  await page.fill('.field', 'Jonas');
  // setInputFiles on the capture input is exactly what the camera hands back.
  await camera.setInputFiles([{ name: 'image.jpg', mimeType: 'image/jpeg', buffer: await makeJpeg(3000, 4000, 200) }]);
  await page.waitForSelector('.job--done', { timeout: 30000 });
  check('camera: a shot lands in the album like any other photo',
    repo.puts.length === 2 && repo.puts[0].path.startsWith('photos/'),
    JSON.stringify(repo.puts.map((p) => p.path)));
  check('camera: portrait orientation survives',
    (() => { const d = jpegSize(repo.tree.get(repo.puts[0].path).bytes); return d.height > d.width; })(),
    JSON.stringify(jpegSize(repo.tree.get(repo.puts[0].path).bytes)));
  check('camera: no page errors', errors.length === 0, errors.join('\n'));
  await context.close();
}

// --- 3c. no camera button where there is a mouse --------------------------
{
  const page = await newPage();
  await stubGitHub(page, makeRepo());
  await page.goto(LINK('upload.html'));
  await page.waitForSelector('.drop');
  check('desktop: camera button stays hidden', !(await page.isVisible('.btn--camera')));
  check('desktop: the gallery button is still there', await page.isVisible('.btn--big:not(.btn--camera)'));
  await page.context().close();
}

// --- 3d. deleting your own photo ------------------------------------------
{
  const page = await newPage();
  const repo = makeRepo();
  await stubGitHub(page, repo);
  const jpeg = await makeJpeg(400, 300, 40);
  for (const [who, id] of [['Jonas', 'aaaaaaaa'], ['Oma-Lotte', 'bbbbbbbb']]) {
    const name = `2026-08-07/12000${id === 'aaaaaaaa' ? 1 : 2}__${who}__${id}.jpg`;
    repo.add(`photos/${name}`, jpeg);
    repo.add(`thumbs/${name}`, jpeg);
  }

  await page.goto(LINK('upload.html'));
  await page.waitForSelector('.drop');
  await page.fill('.field', 'Jonas');           // this browser belongs to Jonas
  await page.click('.btn--ghost');              // "Zum Album"
  await page.waitForSelector('.tile.is-loaded');

  // Oma Lotte's photo is first (later timestamp): no delete button on it.
  await page.locator('.tile').first().click();
  await page.waitForSelector('.lightbox:not(.is-hidden)');
  check('delete: not offered on someone else\'s photo',
    !(await page.isVisible('.btn--danger')),
    await page.textContent('.lightbox__caption'));

  await page.keyboard.press('ArrowRight');
  await page.waitForTimeout(250);
  check('delete: offered on your own photo', await page.isVisible('.btn--danger'),
    await page.textContent('.lightbox__caption'));

  await page.click('.btn--danger');
  await page.waitForSelector('.confirm:not(.is-hidden)');
  check('delete: asks first, and says the repository keeps a copy',
    (await page.textContent('.confirm__text')).includes('Versionsgeschichte'));

  await page.keyboard.press('Escape');
  check('delete: escape backs out of the confirmation, not the photo',
    await page.locator('.confirm.is-hidden').count() === 1 &&
    await page.locator('.lightbox:not(.is-hidden)').count() === 1);
  check('delete: backing out deletes nothing', repo.deletes.length === 0);

  await page.click('.btn--danger');
  await page.click('.confirm .btn--danger');
  // waitForSelector waits for visibility, and .is-hidden never becomes visible.
  await page.waitForFunction(() => document.querySelector('.lightbox').classList.contains('is-hidden'));

  check('delete: removes thumbnail first, then the photo',
    repo.deletes.length === 2 &&
    repo.deletes[0].startsWith('thumbs/') && repo.deletes[1].startsWith('photos/'),
    JSON.stringify(repo.deletes));
  check('delete: only Jonas\'s photo went', repo.deletes.every((p) => p.includes('__Jonas__')),
    JSON.stringify(repo.deletes));
  check('delete: the tile disappears at once', (await page.locator('.tile').count()) === 1);
  check('delete: no page errors', page._errors.length === 0, page._errors.join('\n'));
  await page.context().close();
}

// --- 3e. a read-only code cannot delete -----------------------------------
{
  const page = await newPage();
  const repo = makeRepo();
  await stubGitHub(page, repo, { writable: false });
  const jpeg = await makeJpeg(400, 300, 40);
  repo.add('photos/2026-08-07/120000__Jonas__cccccccc.jpg', jpeg);
  repo.add('thumbs/2026-08-07/120000__Jonas__cccccccc.jpg', jpeg);

  await page.goto(LINK('index.html'));
  await page.evaluate(() => localStorage.setItem('ps:name', 'Jonas'));
  await page.reload();
  await page.waitForSelector('.tile.is-loaded');
  await page.locator('.tile').first().click();
  await page.click('.btn--danger');
  await page.click('.confirm .btn--danger');
  await page.waitForSelector('.toast--error');
  check('delete: a view-only code is told what it needs',
    (await page.textContent('.toast--error')).includes('Upload-Link'),
    await page.textContent('.toast--error'));
  check('delete: nothing was removed', repo.deletes.length === 0 && repo.tree.size === 2);
  await page.context().close();
}

// --- 4. a read-only code cannot upload ------------------------------------
{
  const page = await newPage();
  const repo = makeRepo();
  await stubGitHub(page, repo, { writable: false });
  await page.goto(LINK('upload.html'));
  await page.fill('.field', 'Jonas');
  const jpeg = await makeJpeg(800, 600, 300);
  await page.setInputFiles('input[type=file]', [{ name: 'a.jpg', mimeType: 'image/jpeg', buffer: jpeg }]);
  await page.waitForSelector('.job--error');
  check('upload: read-only code gets a usable explanation',
    (await page.textContent('.job__state')).includes('Upload-Link'),
    await page.textContent('.job__state'));
  await page.context().close();
}

// --- 4b. a brand new, still empty album -----------------------------------
{
  const page = await newPage();
  await stubGitHub(page, makeRepo(), { noBranch: true });
  await page.goto(LINK('index.html'));
  await page.waitForSelector('.status', { timeout: 10000 });
  const text = await page.textContent('.status');
  check('empty album: says it is empty, not that access is missing',
    text.includes('Noch keine Fotos') && !text.includes('Kein Zugriff'), text.trim());
  await page.context().close();
}

// --- 4c. a repository the code cannot reach -------------------------------
{
  const page = await newPage();
  await stubGitHub(page, makeRepo(), { noRepo: true });
  await page.goto(LINK('index.html'));
  await page.waitForSelector('.status--error', { timeout: 10000 });
  const denied = await page.textContent('.status--error');
  check('unreachable album: names the album and the likely cause',
    denied.includes('fam/album') && denied.includes('Repository access'), denied.trim());
  await page.context().close();
}

// --- 5. share page --------------------------------------------------------
{
  const page = await newPage();
  await page.goto(`${origin}/share.html`);
  await page.waitForSelector('.card--view');
  await page.fill('.formrow:nth-of-type(1) .field', 'fam/album');
  const inputs = page.locator('.panel .field');
  await inputs.nth(0).fill('fam/album');
  await inputs.nth(2).fill('Familientreffen 2026');
  await inputs.nth(3).fill('github_pat_view_1234567890');
  await inputs.nth(4).fill('github_pat_write_1234567890');
  await page.waitForSelector('.card--view .qr svg');

  const viewLink = await page.textContent('.card--view .urlbox');
  const uploadLink = await page.textContent('.card--upload .urlbox');
  check('share: view link points at the gallery', viewLink.includes('index.html#') && viewLink.includes('k=github_pat_view'), viewLink);
  check('share: upload link points at the uploader', uploadLink.includes('upload.html#') && uploadLink.includes('k=github_pat_write'), uploadLink);
  check('share: renders a QR code for each link', (await page.locator('.qr svg').count()) === 2);
  check('share: never calls GitHub', page._errors.length === 0, page._errors.join('\n'));
  await page.context().close();
}

// --- 6. opened straight from disk ----------------------------------------
{
  const page = await newPage();
  await page.goto('file://' + path.join(APP, 'index.html'));
  await page.waitForSelector('.gate', { timeout: 5000 }).catch(() => {});
  check('file://: the app still boots without a web server',
    await page.locator('.gate').count() === 1,
    page._errors.join('\n') || 'no .gate rendered');
  await page.context().close();
}

await fixtureContext.close();
await browser.close();
server.close();

const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
process.exit(failed.length ? 1 : 0);
