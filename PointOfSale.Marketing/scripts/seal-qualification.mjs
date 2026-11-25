// Seal only an actually completed three-engine report against unchanged inputs.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { releaseInputs, sha256 } from './release-inputs.mjs';
const app = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
if (!process.argv[2]) throw Error('Usage: node scripts/seal-qualification.mjs <public-browser-results.json>');
const raw = await fs.readFile(path.resolve(process.argv[2]));
const report = JSON.parse(raw);
const inputs = await releaseInputs(app);
const s = report.summary;
if (!report.pass || !report.inputsUnchanged || report.inputsSha256 !== inputs.inputsSha256 ||
  report.sourceSha256 !== inputs.sourceSha256 || s.states !== 180 || s.axeAudits !== 72 ||
  ['failedStates', 'failedAssertions', 'errors', 'consoleErrors', 'externalRequests',
    'axeViolations', 'axeIncompleteAudits', 'brokenResponses'].some(key => s[key] !== 0) ||
  !['chromium', 'firefox', 'webkit'].every(engine => report.states.filter(x => x.engine === engine && x.pass).length === 60)) {
  throw Error('Completed, passing, unchanged-input three-engine qualification required');
}
const qualification = { schemaVersion: 2, pass: true, sourceSha256: inputs.sourceSha256,
  inputsSha256: inputs.inputsSha256, engines: ['chromium', 'firefox', 'webkit'],
  ...s, errors: s.errors, browserReportSha256: sha256(raw), completedUtc: report.completedUtc,
  limits: 'Automated installed desktop engines and responsive emulation; no physical-device, native-editor or screen-reader certification.',
  binding: 'All src, assets, scripts, tests, packaging, lockfile, release policy and approval inputs; qualification itself excluded to avoid circularity.' };
await fs.writeFile(path.join(app, 'release-qualification.json'), JSON.stringify(qualification, null, 2) + '\n');
console.log(JSON.stringify(qualification, null, 2));
