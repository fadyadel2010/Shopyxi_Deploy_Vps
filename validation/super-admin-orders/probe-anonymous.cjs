const fs = require('node:fs')
const endpoints = ['/api/super-admin/orders','/api/super-admin/orders/stats',
    '/api/super-admin/orders/export','/api/super-admin/orders/00000000-0000-0000-0000-000000000000','/api/tenants']
async function main() {
    const results = []
    for (const endpoint of endpoints) {
        const r = await fetch('https://admin.shopyxi.com'+endpoint,{redirect:'manual'})
        results.push({path:endpoint,status:r.status,denied:r.status===401})
    }
    const result = {capturedAtUtc:new Date().toISOString(),credentialsUsed:false,results,
        pass:results.every(r=>r.denied)}
    fs.writeFileSync(`${__dirname}/anonymous-authorization-${process.argv[2]||'before'}.json`,JSON.stringify(result,null,2)+'\n')
    console.log(JSON.stringify(result,null,2))
    if (!result.pass) process.exitCode=1
}
main().catch(()=>{ console.error('Anonymous probe failed'); process.exitCode=1 })
