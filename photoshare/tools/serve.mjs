/*
 * Serve the app locally, for trying it against a real repository before
 * putting it on Pages.
 *
 * A plain file:// open also works, but not everything does: crypto.subtle and
 * the Cache API only exist in a secure context, so uploads fall back to a
 * weaker fingerprint and nothing is cached between visits. http://localhost
 * counts as secure, so this is the honest way to test.
 *
 * Usage: node photoshare/tools/serve.mjs [port]
 */
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const APP = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../app');
const PORT = Number(process.argv[2] || 8080);
const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.webmanifest': 'application/manifest+json'
};

createServer(async (req, res) => {
  const name = decodeURIComponent(req.url.split('?')[0]);
  const file = path.join(APP, name === '/' ? '/index.html' : name);
  if (!file.startsWith(APP)) { res.writeHead(403).end('nope'); return; }
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404).end('not found');
  }
}).listen(PORT, () => {
  console.log(`photoshare on http://localhost:${PORT}/  (share links: /share.html)`);
});
