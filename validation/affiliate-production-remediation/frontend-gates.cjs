const fs = require('fs'), path = require('path'), cp = require('child_process');
const base = 'C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115';
const web = path.join(base, 'source/PointOfSale.Web');
const node = 'C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-node-22.23.4/node.exe';
const env = { ...process.env, CI: 'true', PATH: path.dirname(node) + path.delimiter + process.env.PATH,
    NODE_OPTIONS: '--max-old-space-size=3072', VITE_ADMIN_ORIGIN: 'https://admin.shopyxi.com' };
const tests = ['src/modules/affiliate/pages/AdminManualPayoutReturnDialog.test.tsx',
    'src/modules/affiliate/pages/AdminManualPayoutDialog.test.tsx',
    'src/modules/affiliate/pages/AdminAffiliateDetail.withdrawals.test.tsx',
    'src/modules/affiliate/pages/AdminAffiliateDetail.workflow.test.tsx',
    'src/modules/affiliate/pages/portal/PayoutsTab.request.test.tsx',
    'src/modules/affiliate/pages/portal/PayoutsTab.cancellation.test.tsx',
    'src/modules/affiliate/pages/portal/ProfileTab.test.tsx',
    'src/modules/affiliate/pages/portal/PortalP54a.test.jsx',
    'src/services/adminAffiliateApi.test.ts'];
for (const lang of ['en', 'ar']) JSON.parse(fs.readFileSync(path.join(web, 'src/i18n/locales', lang + '.json'), 'utf8'));
const gates = [['tests', ['node_modules/vitest/vitest.mjs', 'run', ...tests, '--maxWorkers=2', '--reporter=json', '--outputFile=' + path.join(base, 'frontend-tests.json')]],
    ['typescript', ['node_modules/typescript/bin/tsc', '--noEmit']],
    ['build', ['node_modules/vite/bin/vite.js', 'build', '--outDir=' + path.join(base, 'admin-dist')]]];
const summary = { baselineCommit: 'bd25235', testFiles: tests, localeJsonValid: true };
for (const [name, args] of gates) {
    const fd = fs.openSync(path.join(base, 'frontend-' + name + '.log'), 'w');
    const result = cp.spawnSync(node, args, { cwd: web, env, stdio: ['ignore', fd, fd], timeout: 600000 });
    fs.closeSync(fd); summary[name] = result.status === 0;
    if (name === 'tests' && fs.existsSync(path.join(base, 'frontend-tests.json'))) {
        const report = JSON.parse(fs.readFileSync(path.join(base, 'frontend-tests.json'), 'utf8'));
        summary.counts = { total: report.numTotalTests, passed: report.numPassedTests, failed: report.numFailedTests };
    }
    fs.writeFileSync(path.join(base, 'frontend-gates.json'), JSON.stringify(summary, null, 2));
    if (result.status !== 0) throw Error(name + ' failed; see the isolated gate log');
    console.log(name + ' passed');
}
