import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { routes, csp, rootRedirect } from '../src/routes.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
// Mirrors scripts/build.mjs so an isolated review build can be previewed
// without touching the committed dist/ output.
const dist = path.resolve(process.env.MARKETING_DIST || path.join(app, 'dist'));
const routeFiles = new Map(routes.flatMap(r => {
  const file = `${r.path.slice(1).replace(/\/$/, '')}/index.html`;
  return [[r.path, file], [r.path.replace(/\/$/, '') + '/', file]];
}));
routeFiles.set('/', 'index.html');
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'application/javascript; charset=utf-8', '.json': 'application/json; charset=utf-8', '.xml': 'application/xml; charset=utf-8', '.txt': 'text/plain; charset=utf-8', '.png': 'image/png', '.webp': 'image/webp', '.woff2': 'font/woff2' };
export function createPreview() {
  return http.createServer(async (req, res) => {
    const receipt = JSON.parse(await fs.readFile(path.join(dist, 'build-receipt.json'), 'utf8'));
    res.setHeader('X-Robots-Tag', receipt.mode === 'public-lean' ? 'index, follow' : 'noindex, nofollow, noarchive');
    res.setHeader('Content-Security-Policy', csp + "; frame-ancestors 'none'");
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Cache-Control', 'no-store');
    if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405); res.end(); return; }
    try {
      const rawPath = (req.url || '/').split('?')[0];
      const urlPath = decodeURIComponent(rawPath);
      if (urlPath.includes('..') || urlPath.includes('\\') || urlPath.includes('\0')) { res.writeHead(400); res.end(); return; }
      if (urlPath === '/') {
        res.writeHead(rootRedirect.status, { Location: rootRedirect.location,
          'Cache-Control': rootRedirect.cacheControl, 'X-Robots-Tag': rootRedirect.robots });
        res.end(); return;
      }
      let relative = routeFiles.get(urlPath);
      if (!relative && (/^\/assets\/[a-zA-Z0-9._-]+$/.test(urlPath) || ['/robots.txt', '/sitemap.xml', '/publication-gates.json'].includes(urlPath))) relative = urlPath.slice(1);
      let bytes, status = 200;
      try {
        if (!relative) throw Object.assign(Error('Unknown route'), { code: 'ENOENT' });
        bytes = await fs.readFile(path.join(dist, relative));
      } catch (e) {
        if (e.code !== 'ENOENT') throw e;
        status = 404; relative = '404.html'; bytes = await fs.readFile(path.join(dist, relative));
      }
      res.writeHead(status, { 'Content-Type': mime[path.extname(relative)] || 'application/octet-stream', 'Content-Length': bytes.length });
      res.end(req.method === 'HEAD' ? undefined : bytes);
    } catch {
      res.writeHead(400); res.end('Bad request');
    }
  });
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const server = createPreview();
  const port = Number(process.env.MARKETING_PORT || 4183);
  server.listen(port, '127.0.0.1', () => console.log(`SHOPYXI marketing INTERNAL REVIEW ONLY: http://127.0.0.1:${server.address().port}`));
}
