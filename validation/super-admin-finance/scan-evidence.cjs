// Never prints a matching value. A finding blocks evidence publication.
const fs = require('node:fs')
const path = require('node:path')
const patterns = {
    jwt: /\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}/,
    privateKey: /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/,
    credentialUri: /(?:postgres(?:ql)?|mongodb(?:\+srv)?|redis):\/\/[^:\s]+:[^@\s]+@/,
    literalDatabasePassword: /\bPassword\s*=\s*(?![$'"<])[A-Za-z0-9+/_-]{12,}/i,
    sessionDump: /"(?:access_token|refresh_token|accessToken|refreshToken|cookies)"\s*:\s*"(?!REDACTED)[^"]{24,}"/,
}
const findings = []
let files = 0
function scan(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name)
        if (entry.isDirectory()) scan(file)
        else if (/\.(md|json|trx|sql|cjs|ps1|py)$/.test(entry.name)) {
            files++
            const text = fs.readFileSync(file, 'utf8')
            for (const [category, pattern] of Object.entries(patterns)) {
                if (pattern.test(text)) findings.push({ file: path.relative(__dirname, file), category })
            }
        }
    }
}
scan(__dirname)
const result = { checkedAtUtc: new Date().toISOString(), filesChecked: files, findingCount: findings.length, findings }
fs.writeFileSync(path.join(__dirname, 'evidence-secret-scan.json'), JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify(result, null, 2))
if (findings.length) process.exitCode = 1
