// Only complete successful receipts can admit packaging. Never infer PASS from no records.
const fs=require('node:fs')
const path=require('node:path')
const {createHash}=require('node:crypto')
const sha=x=>createHash('sha256').update(x).digest('hex')
const frontFile=path.join(__dirname,'frontend-super-admin-serial.json')
const backFile=path.join(__dirname,'test-results','backend-orders-final.trx')
const buildsFile=path.join(__dirname,'build-gates.json')
const front=JSON.parse(fs.readFileSync(frontFile,'utf8'))
const xml=fs.readFileSync(backFile,'utf8')
const counter=xml.match(/<Counters\s+([^>]+)\/>/)?.[1]||''
const attrs=Object.fromEntries(Array.from(counter.matchAll(/(\w+)="([^"]*)"/g),m=>[m[1],Number(m[2])]))
const tests=Array.from(xml.matchAll(/<UnitTestResult\s+([^>]+)>?/g),m=>Object.fromEntries(Array.from(m[1].matchAll(/(\w+)="([^"]*)"/g),a=>[a[1],a[2]])))
const builds=JSON.parse(fs.readFileSync(buildsFile,'utf8'))
const required=['SuperAdminOrdersForensicTests','SuperAdminOrdersControllerTests','OrdersForensic_CodSplit','OrdersForensic_OnlineCollectionRetryAndPartialFullRefunds',
    'CommerceHubFinancialWorkflowCommissionSnapshotTests','FulfillmentEvents_MustCarryAuditAndIdempotencyFields',
    'VerifiedNonCollectedPaymobCallback','PendingPaymobCallback_WithRejectedSignature']
const backendPass=attrs.total>=170 && attrs.executed===attrs.total && attrs.passed===attrs.total && attrs.failed===0 &&
    required.every(name=>tests.some(t=>t.testName?.includes(name)&&t.outcome==='Passed'))
const result={capturedAtUtc:new Date().toISOString(),
    frontend:{total:front.numTotalTests,passed:front.numPassedTests,failed:front.numFailedTests,files:front.testResults.length,
        pass:front.success&&front.numFailedTests===0&&front.numTotalTests>=182,receiptSha256:sha(fs.readFileSync(frontFile))},
    backend:{...attrs,pass:backendPass,receiptSha256:sha(xml)},
    builds,pass:false}
result.pass=result.frontend.pass&&backendPass&&builds.pass
fs.writeFileSync(path.join(__dirname,'test-gates.json'),JSON.stringify(result,null,2)+'\n')
console.log(JSON.stringify(result,null,2))
if(!result.pass)process.exitCode=1
