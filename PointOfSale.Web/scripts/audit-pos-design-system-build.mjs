// Development-only release evidence. Never imported by the application or run
// by container startup/deployment. Uses the existing Vite config and dependencies.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { gzipSync } from 'node:zlib'
import { performance } from 'node:perf_hooks'

const [mode, directory] = process.argv.slice(2)
if (!['--build', '--inspect'].includes(mode) || !directory) {
    throw new Error('Usage: node scripts/audit-pos-design-system-build.mjs --build|--inspect <output-directory>')
}
const output = resolve(directory)
const evidence = join(dirname(output), `${output.split(/[\\/]/).pop()}`)
let elapsedSeconds
let chunks
if (mode === '--build') {
    const { build } = await import('vite')
    const start = performance.now()
    await build({
        build: { outDir: output },
        plugins: [{
            name: 'pos-release-evidence-only',
            generateBundle(_options, bundle) {
                chunks = Object.values(bundle).filter((item) => item.type === 'chunk').map((chunk) => ({
                    file: chunk.fileName,
                    name: chunk.name,
                    isEntry: chunk.isEntry,
                    imports: chunk.imports,
                    dynamicImports: chunk.dynamicImports,
                    modules: Object.entries(chunk.modules).map(([id, details]) => ({
                        id: id.replaceAll('\\', '/'),
                        renderedLength: details.renderedLength,
                    })),
                }))
            },
        }],
    })
    elapsedSeconds = (performance.now() - start) / 1000
}
function walk(directory) {
    return readdirSync(directory).flatMap((entry) => {
        const full = join(directory, entry)
        return statSync(full).isDirectory() ? walk(full) : [full]
    })
}
const forbiddenModule = /\/node_modules\/(?:axe-core|(?:@playwright\/)?(?:test|playwright|playwright-core)|vitest|@vitest|@testing-library)(?:\/|$)|\/design-system\/test\/|\/e2e\//
const forbiddenText = /axe-core|axe\.run|axe\.version|playwright-core|Playwright Test|__playwright|expectNoAxeViolations|radixJsdomPolyfills|\[DBG\]/
const forbiddenOutput = []
const files = walk(output).map((file) => {
    const content = readFileSync(file)
    const name = relative(output, file).replaceAll('\\', '/')
    if (/\.(js|css|html|map|json)$/.test(file) && forbiddenText.test(content.toString('utf8'))) {
        forbiddenOutput.push(name)
    }
    return { path: name, bytes: content.length, gzipBytes: gzipSync(content).length }
})
const totals = (extension) => files.filter((file) => !extension || file.path.endsWith(extension))
    .reduce((sum, file) => ({ bytes: sum.bytes + file.bytes, gzipBytes: sum.gzipBytes + file.gzipBytes }), { bytes: 0, gzipBytes: 0 })
const forbiddenModules = chunks?.flatMap((chunk) => chunk.modules.filter((module) => forbiddenModule.test(module.id)).map((module) => module.id)) ?? null
const report = {
    node: process.version,
    elapsedSeconds,
    js: totals('.js'),
    css: totals('.css'),
    total: totals(),
    jsChunks: files.filter((file) => file.path.endsWith('.js')).length,
    cssAssets: files.filter((file) => file.path.endsWith('.css')).length,
    forbiddenOutput,
    forbiddenModules,
    files,
}
writeFileSync(`${evidence}.assets.json`, JSON.stringify(report, null, 2))
if (chunks) {
    writeFileSync(`${evidence}.modules.json`, JSON.stringify(chunks, null, 2))
}
console.log(JSON.stringify({ ...report, files: undefined }, null, 2))
if (forbiddenOutput.length || forbiddenModules?.length) {
    process.exitCode = 1
}
