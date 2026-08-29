// Minimal emulation of the Azure SWA edge for local Lighthouse runs: applies the
// globalHeaders, route Cache-Control rules, correct MIME for .avif/.webp, and the
// navigationFallback (index.html for navigations, real 404 for /api/* and assets).
import { createServer } from 'node:http';
import { readFileSync, existsSync, statSync } from 'node:fs';
import { join, extname } from 'node:path';
import { brotliCompressSync, gzipSync } from 'node:zlib';

const ROOT = new URL('./browser/', import.meta.url).pathname;
const cfg = JSON.parse(readFileSync(join(ROOT, 'staticwebapp.config.json'), 'utf8'));

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css',
  '.json': 'application/json', '.svg': 'image/svg+xml', '.ico': 'image/x-icon',
  '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png',
  '.avif': 'image/avif', '.webp': 'image/webp', '.txt': 'text/plain', '.map': 'application/json',
};

const cacheFor = (p) => {
  for (const r of cfg.routes ?? []) {
    const re = new RegExp('^' + r.route.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*') + '$');
    if (re.test(p) && r.headers?.['Cache-Control']) return r.headers['Cache-Control'];
  }
  return 'public, max-age=3600';
};

const isExcluded = (p) =>
  p.startsWith('/api/') || /\.(css|js|map|txt|json|webmanifest|jpg|jpeg|png|gif|svg|ico|avif|webp|woff2?|ttf|otf|eot)$/.test(p);

createServer((req, res) => {
  const p = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  // Azure SWA compresses text responses at the edge. Without this the local run
  // ships 338 KB of JS/CSS where SWA would ship ~79 KB, and on Lighthouse's
  // throttled slow-4G profile that alone cost 14 performance points -- an
  // artefact of the harness, not of the app.
  const COMPRESSIBLE = /^(text\/|application\/(javascript|json|manifest))/;
  const send = (code, body, ext) => {
    const type = MIME[ext] ?? 'application/octet-stream';
    const accept = req.headers['accept-encoding'] ?? '';
    const headers = { ...cfg.globalHeaders, 'Content-Type': type, 'Cache-Control': cacheFor(p), Vary: 'Accept-Encoding' };
    let out = Buffer.isBuffer(body) ? body : Buffer.from(body);

    if (COMPRESSIBLE.test(type) || type === 'text/javascript') {
      if (accept.includes('br')) { out = brotliCompressSync(out); headers['Content-Encoding'] = 'br'; }
      else if (accept.includes('gzip')) { out = gzipSync(out); headers['Content-Encoding'] = 'gzip'; }
    }
    headers['Content-Length'] = out.length;
    res.writeHead(code, headers);
    res.end(out);
  };

  const file = join(ROOT, p === '/' ? 'index.html' : p);
  if (existsSync(file) && statSync(file).isFile()) return send(200, readFileSync(file), extname(file) || '.html');
  if (isExcluded(p)) return send(404, 'Not found', '.txt');
  return send(200, readFileSync(join(ROOT, 'index.html')), '.html');   // navigationFallback
}).listen(4300, () => console.log('SWA emulator on http://localhost:4300'));
