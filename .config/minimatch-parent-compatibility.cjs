// Exact-parent security compatibility guard. Run from either affected npm root:
// npm run test:minimatch-compat
// Maintenance/removal plan: docs/security/MINIMATCH-ROOT-COMPATIBILITY.md.
const assert = require('node:assert/strict')
const fs = require('node:fs')
const path = require('node:path')
const vm = require('node:vm')
const { createRequire } = require('node:module')
assert.equal(process.argv.length, 2, 'Run this guard from the project root without replacement arguments')
const project = process.cwd()
const local = createRequire(path.join(project, 'package.json'))
const parentManifest = local.resolve('@typescript-eslint/typescript-estree/package.json')
const parent = JSON.parse(fs.readFileSync(parentManifest, 'utf8'))
assert.equal(parent.version, '8.71.2', 'Re-review and remove/adapt the exact-parent override before upgrading typescript-estree')
const helperFile = path.join(path.dirname(parentManifest), 'dist/useProgramFromProjectService.js')
const helperSource = fs.readFileSync(helperFile, 'utf8')
const parentRequire = createRequire(helperFile)
const selected = parentRequire('minimatch')
const selectedVersion = JSON.parse(fs.readFileSync(parentRequire.resolve('minimatch/package.json'), 'utf8')).version
assert.equal(selectedVersion, '11.0.4', 'The reviewed official patched minimatch version must remain exact')
assert.equal(typeof selected.minimatch, 'function')
for (const key of ['makeRe', 'AST', 'debug']) assert.equal(selected[key], undefined, 'Removed API unexpectedly present: ' + key)
assert.equal(new selected.Minimatch('*.ts').regexp, undefined)
assert.equal((helperSource.match(/require\("minimatch"\)/g) || []).length, 1)
assert.match(helperSource, /minimatch_1\.minimatch\)\(filePath, pattern, \{ dot: true \}\)/)
assert.doesNotMatch(helperSource, /minimatch_1\.(?:makeRe|AST|debug|Minimatch|regexp)\b/)
const accesses = new Set()
const wrapped = new Proxy(selected, {
  get(target, name) {
    accesses.add(String(name))
    assert.equal(name, 'minimatch', 'Parent accessed an unreviewed minimatch API')
    return (file, pattern, options) => {
      assert.equal(JSON.stringify(options), '{"dot":true}')
      return target.minimatch(file, pattern, options)
    }
  },
})
const moduleForProbe = { exports: {} }
const context = vm.createContext({
  exports: moduleForProbe.exports, module: moduleForProbe,
  require: name => name === 'minimatch' ? wrapped : parentRequire(name),
  __filename: helperFile, __dirname: path.dirname(helperFile),
  process, console, Buffer, setTimeout, clearTimeout,
})
// Expose the real private helper only inside a disposable VM. Never patch node_modules.
vm.runInContext(helperSource + '\nmodule.exports.__compatibilityProbe = filePathMatchedBy;\n', context, { filename: helperFile, timeout: 5000 })
const probe = moduleForProbe.exports.__compatibilityProbe
const paths = [
  'file.ts', 'file.js', 'file.tsx', '.hidden.ts', 'README.md', 'src/file.ts',
  'src/.hidden.ts', 'src/deep/file.ts', 'src\\file.ts', 'src\\deep\\file.ts',
  'scripts/config.js', 'src/foo.test.ts', 'src/foo.spec.ts', 'src/foo.ts',
  'src/FOO.ts', 'src/café.ts', 'src/[literal].ts', 'other/file.js', '',
]
const patterns = [
  '*.ts', '*.js', '*.tsx', '.*.ts', 'file.{ts,js}', 'src/*.ts', 'src/?ile.ts',
  'src/*.{ts,tsx}', 'src/*.+(ts|tsx)', 'src/!(foo).ts', 'src/[a-z]*.ts',
  'src/[[:alpha:]]*.ts', 'scripts/*.js', 'src/foo.test.ts', '!*.js', '#comment',
  'src/\\[literal\\].ts', 'src/café.ts', 'file.ts', 'src/*test.ts',
]
// Golden outputs from official 10.2.6 before replacement, using only fixed benign
// inputs and dot:true. No vulnerable implementation is shipped or run by this guard.
const expectedByPlatform = {
  win32: '10001000000000100010010010000000000000000010000000000010000010010000000000100000000000000000001000000000011111110010000000000101110000100000000000000000001000000000011111110010000000000000000000100000000000000000101000000000010111110110000100000101111100100000000001011011001000000000010111010010000000000101111100100100000001011100001010000000000000000010000000000000000000100000',
  posix: '10001000000000100010010010000000000000000010000000000010000010010000000000100000000000000000001000000000011111110010000000000101110000100000000000000000001000001000000000000010000010000000000000100000000000000000101000000000010111110110000100000101111100100000000001011011001000000000010111010010000000000101111100100100000001011100001010000000000000000010000000000000000000100000',
}
const golden = expectedByPlatform[process.platform === 'win32' ? 'win32' : 'posix']
assert.equal(golden.length, paths.length * patterns.length)
let comparisons = 0
for (const file of paths) {
  assert.equal(probe(file, undefined), false)
  assert.equal(probe(file, []), false)
  for (const pattern of patterns) {
    assert.ok(file.length < 100 && pattern.length < 100)
    assert.equal(probe(file, [pattern]), golden[comparisons] === '1', JSON.stringify({ file, pattern }))
    comparisons++
  }
}
assert.equal(probe('src/file.ts', ['*.js', 'src/*.ts']), true)
assert.equal(probe('src/file.ts', ['*.js', 'other/*.ts']), false)
const parser = local('@typescript-eslint/parser')
const parsed = parser.parseForESLint('export const answer: number = 42', {
  ecmaVersion: 2022, sourceType: 'module', filePath: path.join(project, 'compatibility-probe.ts'),
})
assert.equal(parsed.ast.body[0].type, 'ExportNamedDeclaration')
console.log(JSON.stringify({ status: 'PASS', project, parent: parent.version,
  minimatch: selectedVersion, comparisons, matching_api_accesses: [...accesses],
  options: { dot: true }, removed_apis_called: false, parser_smoke_passed: true,
  denial_of_service_payload_executed: false }, null, 2))
