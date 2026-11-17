// Fail-closed acceptance for the genuine empty production population only.
const fs=require('node:fs')
const path=require('node:path')
const read=name=>JSON.parse(fs.readFileSync(path.join(__dirname,name),'utf8'))
const release=read('release-inputs.json'), deployment=read('deployment-receipt.json')
const tests=read('test-gates.json'), runtime=read('runtime-parent-compatibility.json')
const browser=read('browser-after.json'), actions=read('browser-actions-after.json')
const preservation=read('preservation-after.json'), after=read('production-after.json')
const anonymous=read('anonymous-authorization-after.json')
const gates={
    validatedTestsAndBuilds:tests.pass===true,
    parentRuntimeCompatible:runtime.pass===true,
    sourceCommitMatchesDeployment:release.sourceCommit===deployment.sourceCommit,
    imageOnlyDeploymentVerified:deployment.status==='DEPLOYED_RUNTIME_VERIFIED_BROWSER_PENDING' &&
        Object.keys(deployment.components).sort().join(',')==='api,web' &&
        Object.values(deployment.components).every(c=>c.runtimeArtifactHashesMatched===true&&c.readyReplicas>0) &&
        deployment.unchangedOtherDeployments.length===4 &&
        deployment.migrationsExecuted===false && deployment.businessDataWritesExecuted===false,
    actualHeadedBrowserEmptyScope:browser.status==='PASS_BROWSER_EMPTY_PRODUCTION_SCOPE' &&
        browser.actualHeadedBrowser===true && browser.populatedLifecyclesCertifiedByProduction===false,
    actualControlsCsvAndPolling:actions.pass===true&&actions.actualHeadedBrowser===true&&actions.businessWriteCount===0,
    inspectedBusinessStatePreserved:preservation.allInspectedStatePreserved===true && preservation.snapshots.length===24,
    anonymousAccessDenied:anonymous.pass===true&&anonymous.results.length===5,
    allSixWorkloadsReady:after.deployments.length===6 && after.deployments.every(d=>d.readyReplicas===d.replicas),
    noNewApplicationContainerRestarts:after.pods.filter(p=>/^shopixy-(api|web)-/.test(p.name))
        .every(p=>p.containers.every(c=>c.ready&&c.restartCount===0)),
    globalCommissionPreserved:after.database.globalRuleVerified===true&&after.database.activeOverrideRuleCount===0,
    marketplaceRemainsGenuinelyEmpty:after.database.marketplaceOrdersAllTime===0&&
        after.database.marketplacePaymentsAllTime===0&&after.database.marketplaceRefundsAllTime===0,
}
const result={capturedAtUtc:new Date().toISOString(),applicationCommit:release.sourceCommit,
    productionScope:'EMPTY_COMMERCEHUB_ONLY',productionPopulatedLifecyclesCertified:false,gates,
    pass:Object.values(gates).every(Boolean)}
fs.writeFileSync(path.join(__dirname,'production-acceptance.json'),JSON.stringify(result,null,2)+'\n')
console.log(JSON.stringify(result,null,2))
if(!result.pass)process.exitCode=1
