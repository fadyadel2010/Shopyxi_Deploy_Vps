const fs=require('fs'),path=require('path'),cp=require('child_process'),crypto=require('crypto');
const repo=path.resolve(__dirname,'../..');
const base='C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-20261115';
const stage=path.join(base,'source');
const web=path.join(stage,'PointOfSale.Web');
const node='C:/Users/fadya/AppData/Local/Temp/opencode/shopixy-node-22.23.4/node.exe';
const sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
for(const name of ['ProfileTab.jsx','ProfileTab.test.tsx']) {
    const rel='PointOfSale.Web/src/modules/affiliate/pages/portal/'+name;
    fs.copyFileSync(path.join(repo,rel),path.join(stage,rel));
}
const status=cp.execFileSync('git',['status','--porcelain','--untracked-files=no'],{cwd:stage,encoding:'utf8'}).trim().split(/\r?\n/).filter(Boolean);
if(status.length!==2||!status.every(s=>s.endsWith('/ProfileTab.jsx')||s.endsWith('/ProfileTab.test.tsx')))throw Error('Unexpected build worktree changes');
const files=cp.execFileSync('git',['ls-files','PointOfSale.Web'],{cwd:stage,encoding:'utf8'}).trim().split(/\r?\n/);
const inputs=Object.fromEntries(files.map(f=>[f,sha(path.join(stage,f))]));
fs.writeFileSync(path.join(__dirname,'frontend-source-index.json'),JSON.stringify({baselineCommit:'0dfde49d9b9442c5a77277b399df50774fb631a2',sourceChanges:status,files:inputs},null,2));
const env={...process.env,CI:'true',PATH:path.dirname(node)+path.delimiter+process.env.PATH,NODE_OPTIONS:'--max-old-space-size=3072',VITE_ADMIN_ORIGIN:'https://admin.shopyxi.com'};
const tests=['src/modules/affiliate/pages/portal/ProfileTab.test.tsx','src/modules/affiliate/pages/AffiliateRegister.test.tsx','src/modules/affiliate/pages/portal/PayoutsTab.request.test.tsx','src/modules/affiliate/pages/portal/PayoutsTab.cancellation.test.tsx','src/modules/affiliate/pages/AdminManualPayoutDialog.test.tsx'];
const gates=[['tests',['node_modules/vitest/vitest.mjs','run',...tests,'--reporter=json','--outputFile='+path.join(base,'frontend-tests.json')]],
    ['typescript',['node_modules/typescript/bin/tsc','--noEmit']],
    ['build',['node_modules/vite/bin/vite.js','build','--outDir='+path.join(base,'admin-dist')]]];
const summary={node:cp.execFileSync(node,['--version'],{encoding:'utf8'}).trim(),baselineCommit:'0dfde49d9b9442c5a77277b399df50774fb631a2',testFiles:tests};
for(const [name,args] of gates) {
    const fd=fs.openSync(path.join(base,'frontend-'+name+'.log'),'w');
    const r=cp.spawnSync(node,args,{cwd:web,env,stdio:['ignore',fd,fd],timeout:600000});fs.closeSync(fd);
    summary[name]=r.status===0;
    if(name==='tests'&&fs.existsSync(path.join(base,'frontend-tests.json'))) {
        const t=JSON.parse(fs.readFileSync(path.join(base,'frontend-tests.json'),'utf8'));
        summary.testCounts={total:t.numTotalTests,passed:t.numPassedTests,failed:t.numFailedTests,pending:t.numPendingTests};
    }
    fs.writeFileSync(path.join(__dirname,'frontend-gates.json'),JSON.stringify(summary,null,2));
    if(r.status!==0)throw Error(name+' failed; bounded build log is in the approved private staging directory');
    console.log(name+' passed');
}
if(!Object.entries(inputs).every(([f,h])=>sha(path.join(stage,f))===h))throw Error('Build inputs changed during gates');
summary.inputsUnchanged=true;
summary.sourceIndexSha256=sha(path.join(__dirname,'frontend-source-index.json'));
fs.writeFileSync(path.join(__dirname,'frontend-gates.json'),JSON.stringify(summary,null,2));
console.log(JSON.stringify(summary));
