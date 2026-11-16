// Full check plus accepted-source comparison, without overwriting the working tree.
const fs = require('node:fs')
const path = require('node:path')
const cp = require('node:child_process')
const root = path.resolve(__dirname, '../..')
const ts = require(path.join(root, 'PointOfSale.Web/node_modules/typescript'))
const configPath = path.join(root, 'PointOfSale.Web/tsconfig.json')
const config = ts.readConfigFile(configPath, ts.sys.readFile)
const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, path.dirname(configPath))
const baseline = '1f53f19bc1f352a43358a1b6a4a58c4f2e49c708'
const acceptedPaths = new Set(cp.execFileSync('git', ['ls-tree', '-r', '--name-only', baseline], { cwd: root, encoding: 'utf8' }).trim().split('\n').map(file => path.resolve(root, file).toLowerCase()))
const changed = cp.execFileSync('git', ['diff', '--name-only', baseline, '--', 'PointOfSale.Web/src'], { cwd: root, encoding: 'utf8' }).trim().split('\n').filter(Boolean)
function check(accepted = false, omitKnownFailure = false) {
    const host = ts.createCompilerHost(parsed.options)
    if (accepted) {
        const read = host.readFile.bind(host)
        const exists = host.fileExists.bind(host)
        const isNewSource = file => {
            const absolute = path.resolve(file).toLowerCase()
            return absolute.startsWith(path.join(root, 'PointOfSale.Web/src').toLowerCase()) && !acceptedPaths.has(absolute)
        }
        const overlay = new Map(changed.filter(file => acceptedPaths.has(path.resolve(root, file).toLowerCase())).map(file => [
            path.resolve(root, file).toLowerCase(),
            cp.execFileSync('git', ['show', `${baseline}:${file}`], { cwd: root, encoding: 'utf8' }),
        ]))
        host.readFile = file => isNewSource(file) ? undefined : overlay.get(path.resolve(file).toLowerCase()) ?? read(file)
        host.fileExists = file => !isNewSource(file) && exists(file)
    }
    const files = parsed.fileNames.filter(file => (!accepted || acceptedPaths.has(path.resolve(file).toLowerCase())) &&
        (!omitKnownFailure || !file.endsWith('CommerceHubSellerApplication.test.tsx')))
    const program = ts.createProgram(files, parsed.options, host)
    return ts.getPreEmitDiagnostics(program).map(d => ({
        code: d.code, file: d.file ? path.relative(root, d.file.fileName).replaceAll('\\', '/') : null,
        line: d.file && d.start !== undefined ? d.file.getLineAndCharacterOfPosition(d.start).line + 1 : null,
        message: ts.flattenDiagnosticMessageText(d.messageText, '\n').replaceAll(root.replaceAll('\\', '/'), '<repository>'),
    }))
}
const current = check()
const accepted = check(true)
const existingFailureOnly = JSON.stringify(current) === JSON.stringify(accepted)
const excludingKnownPreexistingTest = check(false, true)
const result = {
    verifiedAtUtc: new Date().toISOString(), baseline,
    fullTypecheckPassed: current.length === 0,
    current, accepted, identicalPreexistingDiagnostics: existingFailureOnly,
    excludingKnownPreexistingTest,
    qualification: 'The missing page declaration is now supplied using the existing adjacent .d.ts convention. No type-check suppression or tsconfig change.',
}
fs.writeFileSync(path.join(__dirname, 'typecheck-final.json'), JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify(result, null, 2))
if (current.length) process.exitCode = 1
