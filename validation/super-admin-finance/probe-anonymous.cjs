const fs = require('node:fs')
async function main() {
    const results = await Promise.all([
        'overview', 'trends', 'promos', 'vendors', 'refunds', 'integrity-check',
        'ledger-overview', 'cashflow', 'pnl', 'reconciliation',
    ].map(async path => {
        const response = await fetch(`https://admin.shopyxi.com/api/super-admin/finance/${path}`, {
            redirect: 'manual', credentials: 'omit',
        })
        const text = await response.text()
        return { path, status: response.status,
            financialPayloadReturned: /"(?:netRevenue|totalGrossCommission|totalDebits|totalCredits|items)"\s*:/.test(text) }
    }))
    const result = { checkedAtUtc: new Date().toISOString(), credentialsSent: false, results,
        passed: results.every(row => row.status === 401 && !row.financialPayloadReturned) }
    fs.writeFileSync(`${__dirname}/anonymous-authorization-after.json`, JSON.stringify(result, null, 2) + '\n')
    console.log(JSON.stringify(result, null, 2))
    if (!result.passed) process.exitCode = 1
}
main().catch(() => { console.error('Anonymous authorization probe failed'); process.exitCode = 1 })
