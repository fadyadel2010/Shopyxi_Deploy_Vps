// Bind deployment receipts to post-release acceptance evidence without editing
// any receipt produced by the deployment runner.
const fs = require('node:fs')
const path = require('node:path')
const crypto = require('node:crypto')
const read = name => JSON.parse(fs.readFileSync(path.join(__dirname, name), 'utf8'))
const sha256 = name => crypto.createHash('sha256').update(fs.readFileSync(path.join(__dirname, name))).digest('hex')

const inputs = read('release-inputs.json')
const deployment = read('deployment-receipt.json')
const browser = read('browser-after.json')
const integrity = read('browser-integrity-after.json')
const preservation = read('preservation-after.json')
const production = read('production-after.json')
const anonymous = read('anonymous-authorization-after.json')

const api = deployment.components.api
const web = deployment.components.web
const requiredEndpoints = ['overview', 'trends', 'promos', 'vendors', 'refunds', 'integrity-check']
const criteria = {
    sourceCommitMatches: deployment.sourceCommit === inputs.sourceCommit,
    apiImmutableImage:
        api.newImage === `${inputs.components.api.tag.split(':')[0]}@${inputs.components.api.manifestDigest}` &&
        api.configDigest === inputs.components.api.configDigest && api.execRuntimeArtifactHashesMatched === true,
    webImmutableImage:
        web.newImage === `${inputs.components.web.tag.split(':')[0]}@${inputs.components.web.manifestDigest}` &&
        web.configDigest === inputs.components.web.configDigest && web.execRuntimeArtifactHashesMatched === true,
    bothFullyReady: api.readyReplicas === 1 && web.readyReplicas === 1,
    unrelatedDeploymentsUnchanged: Array.isArray(deployment.unchangedOtherDeployments) &&
        deployment.unchangedOtherDeployments.length === 4,
    preservationUnchanged: preservation.allInspectedStatePreserved === true &&
        preservation.changedScopes.length === 0 && preservation.snapshots.length === 15,
    anonymousDenied: anonymous.passed === true && anonymous.results.length === 10 &&
        anonymous.results.every(row => row.status === 401 && row.financialPayloadReturned === false),
    readOnlyProductionClean: production.readOnly === true &&
        production.deployments.every(d => d.readyReplicas === d.replicas) &&
        production.database.marketplaceOrdersAllTime === 0 &&
        production.database.marketplaceLedgerEntriesAllTime === 0 &&
        production.database.globalRuleVerified === true &&
        production.database.activeGlobalRuleCount === 1 &&
        production.database.activeOverrideRuleCount === 0 &&
        production.database.orphanOrderLedgerCount === 0 && production.database.orphanRefundLedgerCount === 0 &&
        production.database.duplicateIdempotencyGroupCount === 0 &&
        production.database.overRefundedSubOrderCount === 0,
    browserAccepted: browser.checksPassed === true && browser.status === 'PASS_BROWSER_EMPTY_PRODUCTION_SCOPE' &&
        browser.captures.length === 8 && browser.bundleLoaded === true && browser.businessWriteCount === 0 &&
        browser.consoleErrorCount === 0 && browser.exceptionCount === 0 &&
        browser.failedRequiredRequests.length === 0 &&
        requiredEndpoints.every(endpoint => browser.responses.some(row => row.path.endsWith('/' + endpoint) && row.status === 200)) &&
        browser.responses.filter(row => row.path.endsWith('/overview')).every(row => row.allRequiredMetricsZero === true),
    integrityTruthful: integrity.passed === true && integrity.integrityApi.status === 'NO_RECORDS' &&
        integrity.integrityApi.includesGlobalLedgerChecks === false &&
        integrity.english.rawEnumVisible === false && integrity.arabic.rawEnumVisible === false &&
        integrity.english.signedInEmailVisible === false && integrity.arabic.signedInEmailVisible === false,
    noEvidenceSecretFindings: read('evidence-secret-scan.json').findingCount === 0,
}
const result = {
    checkedAtUtc: new Date().toISOString(),
    sourceCommit: deployment.sourceCommit,
    branch: 'finance/super-admin-forensic-audit-20261117',
    status: Object.values(criteria).every(Boolean) ? 'PASS_EMPTY_PRODUCTION_SCOPE' : 'NOT_ACCEPTED',
    criteria,
    images: {
        api: { image: api.newImage, manifestDigest: api.manifestDigest, configDigest: api.configDigest,
            archiveSha256: inputs.components.api.archiveSha256 },
        web: { image: web.newImage, manifestDigest: web.manifestDigest, configDigest: web.configDigest,
            archiveSha256: inputs.components.web.archiveSha256 },
    },
    evidenceSha256: Object.fromEntries(['deployment-receipt.json', 'browser-after.json',
        'browser-integrity-after.json', 'preservation-after.json', 'production-after.json',
        'anonymous-authorization-after.json'].map(name => [name, sha256(name)])),
    scopeStatement: 'Production CommerceHub financial data is empty all-time. This acceptance proves the deployed '
        + 'release behaves truthfully on that empty production scope; it does not certify live marketplace '
        + 'commission, promotion, refund, settlement or payout lifecycles. POS invoices remain a separate population.',
}
fs.writeFileSync(path.join(__dirname, 'release-acceptance.json'), JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify({ status: result.status, criteria, evidenceSha256: result.evidenceSha256 }, null, 2))
if (result.status !== 'PASS_EMPTY_PRODUCTION_SCOPE') process.exitCode = 1
