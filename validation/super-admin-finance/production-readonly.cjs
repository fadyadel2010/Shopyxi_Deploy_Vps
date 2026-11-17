// Read-only metadata/aggregate evidence. Never retrieves Secret/env/session data.
const { execFileSync } = require('node:child_process')
const { readFileSync, writeFileSync } = require('node:fs')
const { createHash } = require('node:crypto')
const { join } = require('node:path')
const sha256 = text => createHash('sha256').update(text).digest('hex')
const ssh = (command, input) => execFileSync('ssh', [
  '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', 'shopixy-vps', command,
], { input, encoding: 'utf8', timeout: 120000, maxBuffer: 8 * 1024 * 1024 })
const phase = process.argv[2] || 'before'
if (!/^[a-z-]+$/.test(phase)) throw new Error('Invalid phase')
const deployments = JSON.parse(ssh('kubectl -n shopixy-app get deployments -o json'))
const pods = JSON.parse(ssh('kubectl -n shopixy-app get pods -o json'))
const sql = readFileSync(join(__dirname, 'production-audit.sql'), 'utf8')
const sqlLines = ssh('kubectl -n shopixy-data exec -i shopixy-postgres-1 -c postgres -- psql -X -q -U postgres -d app -At -v ON_ERROR_STOP=1', sql)
const database = JSON.parse(sqlLines.split(/\r?\n/).find(line => line.startsWith('{')))
const result = {
  capturedAtUtc: new Date().toISOString(),
  readOnly: true,
  sqlSha256: sha256(sql),
  database,
  deployments: deployments.items.map(d => ({
    name: d.metadata.name, uid: d.metadata.uid, generation: d.metadata.generation,
    observedGeneration: d.status.observedGeneration, replicas: d.spec.replicas,
    readyReplicas: d.status.readyReplicas || 0,
    specSha256: sha256(JSON.stringify(d.spec)),
    containers: d.spec.template.spec.containers.map(c => ({ name: c.name, image: c.image })),
  })),
  pods: pods.items.map(p => ({
    name: p.metadata.name, phase: p.status.phase,
    containers: (p.status.containerStatuses || []).map(c => ({
      name: c.name, ready: c.ready, restartCount: c.restartCount, imageID: c.imageID,
    })),
  })),
}
writeFileSync(join(__dirname, `production-${phase}.json`), JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify({ phase, database, workloadsReady: result.deployments.every(d => d.readyReplicas === d.replicas) }, null, 2))
