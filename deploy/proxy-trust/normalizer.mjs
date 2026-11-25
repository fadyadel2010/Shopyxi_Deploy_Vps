// njs request-time admission: no static off-pod real-IP trust and no heartbeat reload.
import fs from 'fs';
import crypto from 'crypto';

const roles = ['traefik', 'web', 'storefront', 'commercehub'];
const sensitive = ['x-forwarded-for', 'x-forwarded-proto', 'x-forwarded-host',
    'forwarded', 'cf-connecting-ip', 'x-real-ip', 'true-client-ip',
    'x-original-for', 'x-original-proto', 'x-original-host',
    'x-forwarded-scheme', 'x-forwarded-ssl', 'front-end-https'];

function address(text) {
    if (typeof text !== 'string' || !text || /[\s%\[\]]/.test(text)) throw Error('ip');
    if (text.indexOf(':') < 0) {
        const p = text.split('.');
        if (p.length !== 4 || p.some(x => !/^(0|[1-9][0-9]{0,2})$/.test(x) || +x > 255))
            throw Error('ipv4');
        return { bytes: p.map(Number), text: p.join('.') };
    }
    if (text.indexOf('.') >= 0 || !/^[0-9a-f:]+$/i.test(text) ||
        (text.match(/::/g) || []).length > 1) throw Error('ipv6');
    const split = text.split('::');
    const left = split[0] ? split[0].split(':') : [];
    const right = split.length === 2 && split[1] ? split[1].split(':') : [];
    if (left.concat(right).some(x => !/^[0-9a-f]{1,4}$/i.test(x))) throw Error('ipv6');
    let groups;
    if (split.length === 1) {
        if (left.length !== 8) throw Error('ipv6');
        groups = left;
    } else {
        if (left.length + right.length >= 8) throw Error('ipv6');
        groups = left.concat(Array(8 - left.length - right.length).fill('0'), right);
    }
    const nums = groups.map(x => parseInt(x, 16));
    if (nums.slice(0, 5).every(x => x === 0) && nums[5] === 65535) throw Error('mapped');
    const bytes = [];
    nums.forEach(x => { bytes.push(x >> 8); bytes.push(x & 255); });
    // Expanded lowercase form is deliberately canonical; no textual alias partitions.
    return { bytes, text: nums.map(x => x.toString(16).padStart(4, '0')).join(':') };
}
function inside(ip, network) {
    const p = network.split('/');
    const base = address(p[0]).bytes;
    let bits = +p[1];
    if (base.length !== ip.length || !Number.isInteger(bits) || bits < 0 || bits > base.length * 8)
        return false;
    for (let i = 0; i < base.length && bits > 0; i++, bits -= 8) {
        const mask = (255 << Math.max(0, 8 - bits)) & 255;
        if ((ip[i] & mask) !== (base[i] & mask)) return false;
    }
    return true;
}
function host(value) {
    if (typeof value !== 'string') throw Error('host');
    const parts = value.toLowerCase().split(':');
    if (parts.length > 2 || (parts.length === 2 &&
        (!/^[1-9][0-9]{0,4}$/.test(parts[1]) || +parts[1] > 65535))) throw Error('port');
    const name = parts[0];
    if (!name || name.length > 253 ||
        !/^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(name) ||
        name.split('.').some(x => x.length > 63 || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(x)))
        throw Error('host');
    // Match the existing nginx $host contract, including port-bearing scrape
    // and probe Host headers: validate, lowercase, and omit the transport port.
    return name;
}
function lease(path, anchors, now) {
    const raw = fs.readFileSync(path, 'utf8');
    if (raw.length > 65536) throw Error('size');
    const outer = JSON.parse(raw);
    if (JSON.stringify(outer) !== raw.trim() ||
        Object.keys(outer).sort().join(',') !== 'local,payload,sha256' ||
        typeof outer.payload !== 'string' || !/^[a-f0-9]{64}$/.test(outer.sha256) ||
        crypto.createHash('sha256').update(outer.payload).digest('hex') !== outer.sha256)
        throw Error('digest');
    const local = outer.local;
    const uptime = Math.floor(parseFloat(fs.readFileSync('/proc/uptime', 'utf8').split(' ')[0]) * 1000);
    const boot = fs.readFileSync('/proc/sys/kernel/random/boot_id', 'utf8').trim();
    if (!local || local.bootId !== boot ||
        !Number.isSafeInteger(local.observedWallMs) ||
        !Number.isSafeInteger(local.observedUptimeMs) || !Number.isSafeInteger(local.expiresUptimeMs) ||
        local.observedUptimeMs > uptime + 20 || local.expiresUptimeMs <= uptime ||
        local.expiresUptimeMs - local.observedUptimeMs > 20000 ||
        Math.abs((now - local.observedWallMs) - (uptime - local.observedUptimeMs)) > 2000)
        throw Error('local-clock');
    const p = JSON.parse(outer.payload);
    if (JSON.stringify(p) !== outer.payload || p.peerConfigMapUid !== anchors.peerConfigMapUid ||
        p.schema !== 1 || typeof p.sequence !== 'string' || !p.sequence ||
        !Number.isSafeInteger(p.issuedAt) || !Number.isSafeInteger(p.expiresAt) ||
        p.issuedAt > now + 1000 || p.expiresAt <= now || p.issuedAt > p.expiresAt ||
        p.expiresAt - p.issuedAt > 20000 || now - p.issuedAt > 20000 ||
        !Array.isArray(p.peers) || p.peers.length !== 4) throw Error('freshness');
    const found = {};
    const ips = {};
    p.peers.forEach(peer => {
        const a = anchors.workloads[peer.role];
        if (!roles.includes(peer.role) || found[peer.role] || !a ||
            peer.service !== a.service || peer.namespace !== a.namespace ||
            peer.serviceUid !== a.serviceUid || peer.deploymentUid !== a.deploymentUid ||
            [peer.podUid, peer.sliceUid, peer.podName].some(x =>
                typeof x !== 'string' || !/^[a-zA-Z0-9-]{1,253}$/.test(x)) ||
            peer.ready !== true || peer.terminating !== false) throw Error('ownership');
        const parsed = address(peer.ip);
        // Current enrolled infrastructure is IPv4-only; no subnet or mapped identity.
        if (parsed.bytes.length !== 4 || parsed.text !== peer.ip || ips[peer.ip]) throw Error('peer');
        ips[peer.ip] = true;
        found[peer.role] = peer;
    });
    return { peers: found, digest: outer.sha256 };
}
function compute(r) {
    const result = { status: '400', client: '', scheme: '', host: '', reason: 'malformed' };
    try {
        const raw = {};
        r.rawHeadersIn.forEach(pair => {
            const key = pair[0].toLowerCase();
            if (sensitive.includes(key) || key === 'host' || key === 'connection') {
                if (raw[key] !== undefined) throw Error('duplicate');
                raw[key] = pair[1];
            }
        });
        result.host = host(raw.host);
        const connection = (raw.connection || '').toLowerCase().split(',').map(x => x.trim());
        if (connection.some(x => sensitive.includes(x) || x === 'host')) throw Error('connection');
        const socket = address(r.variables.remote_addr);
        result.client = socket.text;
        result.scheme = r.variables.scheme;
        const probe = r.variables.trust_role === 'api' &&
            ['/health/live', '/health/ready', '/metrics'].includes(r.uri);
        let anchors, current;
        try {
            anchors = JSON.parse(fs.readFileSync(r.variables.trust_anchors, 'utf8'));
            current = lease(r.variables.trust_lease, anchors, Date.now());
        } catch (_) {
            if (probe) { result.status = '200'; result.reason = 'non-proxy'; return result; }
            result.status = '503'; result.reason = 'lease-unavailable'; return result;
        }
        const role = roles.find(key => current.peers[key].ip === socket.text);
        if (!role || (r.variables.trust_role !== 'api' && role !== 'traefik')) {
            // Monitoring/probe listeners have no forwarding authority. Only exact
            // existing safe paths remain available, under unchanged network policy.
            if (probe) {
                result.status = '200'; result.reason = 'non-proxy'; return result;
            }
            result.status = '403'; result.reason = 'untrusted-peer'; return result;
        }
        if (!['http', 'https'].includes(raw['x-forwarded-proto'])) throw Error('scheme');
        if (raw['x-forwarded-host'] !== undefined &&
            host(raw['x-forwarded-host']) !== result.host) throw Error('contradictory-host');
        if (raw['x-forwarded-host'] && raw.host.indexOf(':') >= 0 &&
            raw['x-forwarded-host'].indexOf(':') >= 0 &&
            raw.host.split(':')[1] !== raw['x-forwarded-host'].split(':')[1])
            throw Error('contradictory-port');
        const chain = (raw['x-forwarded-for'] || '').split(',').map(x => address(x.trim()));
        if (!chain.length || chain.length > 8) throw Error('hops');
        if (role !== 'traefik') {
            if (chain.length !== 1) throw Error('frontend-canonical');
            result.client = chain[0].text;
        } else {
            let index = chain.length - 1;
            while (index > 0 && anchors.cloudflare.some(cidr => inside(chain[index].bytes, cidr)))
                index--;
            // A chain consisting solely of trusted intermediates has no client.
            if (anchors.cloudflare.some(cidr => inside(chain[index].bytes, cidr))) throw Error('client-missing');
            result.client = chain[index].text;
        }
        result.scheme = raw['x-forwarded-proto'];
        result.status = '200'; result.reason = role;
        return result;
    } catch (_) {
        return result;
    }
}
function normalize(r) {
    if (r.variables.trust_result) return JSON.parse(r.variables.trust_result);
    const result = compute(r);
    r.variables.trust_result = JSON.stringify(result);
    return result;
}
function status(r) { return normalize(r).status; }
function client(r) { return normalize(r).client; }
function scheme(r) { return normalize(r).scheme; }
function authority(r) { return normalize(r).host; }
export default { status, client, scheme, authority, address, inside, host, lease, normalize };
