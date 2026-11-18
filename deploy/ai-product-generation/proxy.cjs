'use strict';

const http = require('node:http');
const net = require('node:net');
const dns = require('node:dns').promises;

const R2_HOST = /^[a-f0-9]{32}\.r2\.cloudflarestorage\.com$/;
const EXACT_HOSTS = new Set(['openrouter.ai']);

function publicIPv4(address) {
  if (net.isIP(address) !== 4) return false;
  const [a, b] = address.split('.').map(Number);
  return a > 0 && a < 224 && a !== 10 && a !== 127 &&
    !(a === 169 && b === 254) && !(a === 172 && b >= 16 && b <= 31) &&
    !(a === 192 && b === 168) && !(a === 100 && b >= 64 && b <= 127) &&
    !(a === 198 && (b === 18 || b === 19));
}

function parseTargets(value) {
  const targets = String(value ?? '')
    .split(',')
    .map((item) => item.trim().toLowerCase())
    .filter(Boolean)
    .filter((host) => R2_HOST.test(host) || EXACT_HOSTS.has(host));
  return [...new Set(targets)];
}

function createProxy(targetList) {
  const targets = new Set(Array.isArray(targetList) ? targetList.map((item) => String(item).toLowerCase()) : parseTargets(targetList));
  if (targets.size === 0) throw new Error('Invalid target');
  let active = 0;
  const server = http.createServer({ maxHeaderSize: 8192, requestTimeout: 5000, headersTimeout: 5000 }, (req, res) => {
    if (req.method === 'GET' && req.url === '/healthz') {
      res.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
      res.end('ok');
    } else { res.writeHead(403, { Connection: 'close' }); res.end(); }
  });
  server.maxConnections = 48;
  server.on('clientError', (_error, socket) => socket.destroy());
  server.on('connect', async (req, client, head) => {
    const [host, port] = String(req.url ?? '').toLowerCase().split(':');
    if (port !== '443' || !targets.has(host) || head.length > 16384 || active >= 32) {
      client.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');
      return;
    }
    active++;
    let upstream;
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true;
      active--;
      clearTimeout(deadline);
      client.destroy();
      if (upstream) upstream.destroy();
    };
    const deadline = setTimeout(finish, 600000);
    deadline.unref();
    client.setTimeout(60000, finish);
    client.on('error', finish);
    client.on('close', finish);
    try {
      const addresses = await dns.lookup(host, { family: 4, all: true });
      if (finished || !addresses.length || addresses.some((x) => !publicIPv4(x.address))) { finish(); return; }
      upstream = net.connect({ host: addresses[0].address, port: 443 });
      const connectDeadline = setTimeout(finish, 5000);
      connectDeadline.unref();
      upstream.on('error', finish);
      upstream.on('close', finish);
      upstream.setTimeout(60000, finish);
      upstream.once('connect', () => {
        clearTimeout(connectDeadline);
        if (finished) return;
        client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (head.length) upstream.write(head);
        client.pipe(upstream);
        upstream.pipe(client);
      });
    } catch { finish(); }
  });
  return server;
}

if (require.main === module) {
  const targets = parseTargets([process.env.R2_TARGET_HOST, process.env.AI_TARGET_HOST].filter(Boolean).join(','));
  const server = createProxy(targets);
  server.listen(3128, '0.0.0.0');
  process.on('SIGTERM', () => { server.close(); setTimeout(() => process.exit(0), 5000).unref(); });
}

module.exports = { createProxy, publicIPv4, parseTargets };
