// Inspect the actual bundled WOFF2 tables with Node's built-in Brotli decoder.
import fs from 'node:fs/promises';
import path from 'node:path';
import { brotliDecompressSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const evidence = path.resolve(process.env.MARKETING_EVIDENCE_DIR || path.join(app, 'test-output'));
const known = ['cmap','head','hhea','hmtx','maxp','name','OS/2','post','cvt ','fpgm','glyf','loca','prep','CFF ','VORG','EBDT','EBLC','gasp','hdmx','kern','LTSH','PCLT','VDMX','vhea','vmtx','BASE','GDEF','GPOS','GSUB','EBSC','JSTF','MATH','CBDT','CBLC','COLR','CPAL','SVG ','sbix','acnt','avar','bdat','bloc','bsln','cvar','fdsc','feat','fmtx','fvar','gvar','hsty','just','lcar','mort','morx','opbd','prop','trak','Zapf','Silf','Glat','Gloc','Feat','Sill'];
const records = [], allCoverage = new Set();
for (const subset of ['arabic','latin','latin-ext']) {
  const data = await fs.readFile(path.join(app, `public/assets/cairo-${subset}-variable.woff2`));
  let pos = 48;
  function base128() {
    let result = 0;
    for (let i = 0; i < 5; i++) {
      const byte = data[pos++]; result = result * 128 + (byte & 127);
      if (!(byte & 128)) return result;
    }
    throw Error('Bad WOFF2 Base128');
  }
  const directory = [];
  for (let i = 0; i < data.readUInt16BE(12); i++) {
    const flags = data[pos++], index = flags & 63, version = flags >> 6;
    const tag = index === 63 ? data.toString('ascii', pos, pos += 4) : known[index];
    const length = base128();
    const transformed = ['glyf','loca'].includes(tag) ? version !== 3 : version !== 0;
    directory.push({ tag, length, stored: transformed ? base128() : length });
  }
  const decompressed = brotliDecompressSync(data.subarray(pos, pos + data.readUInt32BE(20)));
  let offset = 0;
  const tables = {};
  for (const entry of directory) { tables[entry.tag] = decompressed.subarray(offset, offset + entry.stored); offset += entry.stored; }
  const fvar = tables.fvar, axes = [];
  for (let i = 0; i < fvar.readUInt16BE(8); i++) {
    const at = fvar.readUInt16BE(4) + i * fvar.readUInt16BE(10);
    axes.push({ tag: fvar.toString('ascii', at, at+4), min: fvar.readInt32BE(at+4)/65536, default: fvar.readInt32BE(at+8)/65536, max: fvar.readInt32BE(at+12)/65536 });
  }
  const coverage = new Set(), cmap = tables.cmap;
  for (let i = 0; i < cmap.readUInt16BE(2); i++) {
    const at = cmap.readUInt32BE(4 + i*8 + 4), format = cmap.readUInt16BE(at);
    if (format === 12) {
      for (let j = 0; j < cmap.readUInt32BE(at+12); j++) {
        const group = at+16+j*12, first = cmap.readUInt32BE(group), last = cmap.readUInt32BE(group+4), glyph = cmap.readUInt32BE(group+8);
        for (let cp = first; cp <= last; cp++) if (glyph + cp - first !== 0) coverage.add(cp);
      }
    } else if (format === 4) {
      const n = cmap.readUInt16BE(at+6)/2;
      for (let j = 0; j < n; j++) {
        const last = cmap.readUInt16BE(at+14+j*2), first = cmap.readUInt16BE(at+16+n*2+j*2);
        const delta = cmap.readInt16BE(at+16+n*4+j*2), rangeAt = at+16+n*6+j*2, range = cmap.readUInt16BE(rangeAt);
        for (let cp = first; cp <= last && cp < 65535; cp++) {
          const glyph = range ? cmap.readUInt16BE(rangeAt + range + (cp-first)*2) : (cp+delta)&65535;
          if (glyph) coverage.add(cp);
        }
      }
    }
  }
  for (const cp of coverage) allCoverage.add(cp);
  const head = tables.head, hhea = tables.hhea;
  records.push({ subset, bytes: data.length, axes, glyphs: tables.maxp.readUInt16BE(4), codepoints: coverage.size,
    shapingTables: ['GSUB','GPOS','GDEF'].filter(t => tables[t]), tables: Object.keys(tables),
    metrics: { unitsPerEm: head.readUInt16BE(18), ascender: hhea.readInt16BE(4), descender: hhea.readInt16BE(6), lineGap: hhea.readInt16BE(8) } });
}
const claims = JSON.parse(await fs.readFile(path.join(app, 'src/content/claims.json'), 'utf8'));
const labels = JSON.parse(await fs.readFile(path.join(app, 'src/content/labels.json'), 'utf8'));
const strings = value => typeof value === 'string' ? [value] : value && typeof value === 'object' ? Object.values(value).flatMap(strings) : [];
// Bilingual displayed claims/labels only; evidence/status strings are not displayed.
const displayed = [...new Set([...Object.values(claims).flatMap(c => [c.en,c.ar]), ...strings(labels)].join(''))];
const missing = displayed.filter(c => !/\s/u.test(c) && !allCoverage.has(c.codePointAt(0))).map(c => ({ character:c, codepoint:`U+${c.codePointAt(0).toString(16).toUpperCase()}` }));
const result = { records, displayedCodepoints: displayed.length, missing, pass: !missing.length && records.every(r => r.axes.some(a => a.tag === 'wght' && a.min === 200 && a.max === 1000)) };
await fs.mkdir(evidence, { recursive: true });
await fs.writeFile(path.join(evidence, 'woff2-inspection.json'), JSON.stringify(result, null, 2));
console.log(JSON.stringify(result,null,2));
if (!result.pass) process.exitCode = 1;
