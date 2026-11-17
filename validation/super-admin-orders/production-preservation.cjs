// Read-only fingerprints only. Customer/order rows never leave PostgreSQL.
const fs=require('node:fs')
const {execFileSync}=require('node:child_process')
const phase=process.argv[2]||'predeploy'
if(!/^[a-z-]+$/.test(phase))throw Error('Invalid phase')
const tables=['SalesInvoices','Returns','LedgerJournalEntries','LedgerLines','CommissionRules','PromoCodes',
    'CommerceHubFinancialTransactions','CommerceHubRefunds','CommerceHubVendorEarnings','CommerceHubPayouts',
    'CommerceHubVendorSettlements','ProductVariantStocks','WarehouseStocks',
    'CommerceHubMarketplaceOrders','CommerceHubSubOrders','CommerceHubSubOrderItems','CommerceHubCustomers',
    'CommerceHubPayments','CommerceHubPaymentAttempts','CommerceHubRefundRequests','InventoryReservations','Orders']
const sources=tables.map(t=>[t,`SELECT * FROM "${t}"`])
sources.push(['variantPriceState','SELECT "Id","TenantId","Price","OnlinePrice","CostPrice","IsActive","IsDeleted" FROM "ProductVariants"'])
sources.push(['listingPricePublicationState','SELECT "Id","TenantId","VariantId","IsPublished","CommerceHubPrice","IsDeleted" FROM "CommerceHubListings"'])
const sql=`BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='30s';
${sources.map(([name,q])=>`SELECT json_build_object('scope','${name}','count',count(*),'fingerprint',md5(coalesce(string_agg(md5(row_to_json(t)::text),'' ORDER BY t."Id"),''))) FROM (${q}) t;`).join('\n')}
ROLLBACK;`
const output=execFileSync('ssh',['-o','BatchMode=yes','shopixy-vps',
    'kubectl -n shopixy-data exec -i shopixy-postgres-1 -c postgres -- psql -X -q -U postgres -d app -At -v ON_ERROR_STOP=1'],
    {input:sql,encoding:'utf8',timeout:120000})
const snapshots=output.split(/\r?\n/).filter(l=>l.startsWith('{')).map(JSON.parse)
if(snapshots.length!==sources.length)throw Error('Incomplete fingerprint evidence')
const result={capturedAtUtc:new Date().toISOString(),readOnly:true,snapshots}
if(phase==='after'){
    const before=JSON.parse(fs.readFileSync(`${__dirname}/preservation-predeploy.json`,'utf8'))
    result.changedScopes=snapshots.filter(row=>{
        const old=before.snapshots.find(p=>p.scope===row.scope)
        return old?.count!==row.count||old?.fingerprint!==row.fingerprint
    }).map(r=>r.scope)
    result.allInspectedStatePreserved=!result.changedScopes.length
}
fs.writeFileSync(`${__dirname}/preservation-${phase}.json`,JSON.stringify(result,null,2)+'\n')
console.log(JSON.stringify(result,null,2))
if(result.changedScopes?.length)process.exitCode=1
