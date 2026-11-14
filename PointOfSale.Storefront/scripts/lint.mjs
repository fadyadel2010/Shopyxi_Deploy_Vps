// Use the maintained legacy ESLint configuration explicitly on every platform.
import { spawnSync } from 'node:child_process'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const cli = path.join(path.dirname(fileURLToPath(import.meta.resolve('eslint/package.json'))), 'bin/eslint.js')
const targets = process.argv.slice(2)
const args = targets.length && !targets[0].startsWith('-') ? targets : ['.', ...targets]
const result = spawnSync(process.execPath, [cli, ...args, '--ext', '.js,.jsx,.ts,.tsx'], {
    stdio: 'inherit',
    env: { ...process.env, ESLINT_USE_FLAT_CONFIG: 'false' },
})
if (result.error) throw result.error
process.exit(result.status ?? 1)
