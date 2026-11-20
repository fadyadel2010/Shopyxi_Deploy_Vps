/**
 * @vitest-environment node
 *
 * Runs in the Node environment: this guard drives Vite's resolver, which uses
 * esbuild, and esbuild cannot run inside jsdom's realm.
 */
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import { createServer, type ViteDevServer } from 'vite';

/**
 * Test alias boundary guard (audit finding F-05).
 *
 * Web tests must run against Web source. Production maps `@` to
 * `PointOfSale.Web/src`, so a test alias that points `@` anywhere else makes a
 * Web suite report on a different application's code.
 *
 * This guard asks the real Vite resolver - configured from the same
 * `vitest.config.ts` Vitest loads - what each specifier actually resolves to.
 * It does not restate the configuration in an expectation.
 */

const WEB_ROOT = path.resolve(__dirname, '..', '..');
const WEB_SRC = path.join(WEB_ROOT, 'src');
const STOREFRONT_SRC = path.resolve(WEB_ROOT, '..', 'PointOfSale.Storefront', 'src');

let server: ViteDevServer;

beforeAll(async () => {
  server = await createServer({
    configFile: path.join(WEB_ROOT, 'vitest.config.ts'),
    root: WEB_ROOT,
    logLevel: 'silent',
    appType: 'custom',
    server: { middlewareMode: true, hmr: false, watch: null },
    optimizeDeps: { noDiscovery: true, include: [] },
  });
}, 120_000);

afterAll(async () => {
  await server?.close();
});

/** Importer paths are package-owned, exactly as a real module would be. */
const webImporter = path.join(WEB_SRC, 'components', 'Layout.jsx');
const webTestImporter = path.join(WEB_SRC, 'tests', 'representative.test.tsx');
const storefrontImporter = path.join(STOREFRONT_SRC, 'app', 'App.tsx');

async function resolvedFile(specifier: string, importer: string): Promise<string | null> {
  const resolved = await server.pluginContainer.resolveId(specifier, importer);
  if (!resolved) return null;
  return resolved.id.split('?')[0];
}

function toPosix(file: string): string {
  return file.split(path.sep).join('/');
}

function relativeToWeb(file: string): string {
  return toPosix(path.relative(WEB_ROOT, file));
}

function isInsideStorefront(file: string | null): boolean {
  if (!file) return false;
  const relative = path.relative(STOREFRONT_SRC, file);
  return relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative);
}

function isInsideWeb(file: string | null): boolean {
  if (!file) return false;
  const relative = path.relative(WEB_SRC, file);
  return relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative);
}

/** Every `@/...` specifier written in a Web source or test file. */
function collectWebAliasSpecifiers(dir: string, found: Array<{ file: string; specifier: string }> = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const entryPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== 'node_modules') collectWebAliasSpecifiers(entryPath, found);
      continue;
    }
    if (!/\.(js|jsx|ts|tsx)$/.test(entry.name)) continue;

    const file = relativeToWeb(entryPath);
    // This guard names specifiers in strings; only real imports are of interest.
    if (file === path.posix.join('src', 'architecture-tests', 'testAliasBoundary.test.ts')) continue;

    const content = fs.readFileSync(entryPath, 'utf8');
    for (const match of content.matchAll(/['"](@\/[^'"\n]+)['"]/g)) {
      found.push({ file, specifier: match[1] });
    }
  }
  return found;
}

describe('Web test alias boundary', () => {
  it('resolves a representative Web `@/...` import inside PointOfSale.Web/src', async () => {
    const file = await resolvedFile('@/types/storeConfig', webTestImporter);

    expect(file, '@/types/storeConfig must resolve').not.toBeNull();
    expect(isInsideWeb(file)).toBe(true);
    expect(relativeToWeb(file!)).toBe('src/types/storeConfig.ts');
    expect(fs.existsSync(file!)).toBe(true);
  });

  it('resolves a representative Web `@/...` import from application code inside PointOfSale.Web/src', async () => {
    const file = await resolvedFile('@/utils/htmlSanitizer', webImporter);

    expect(isInsideWeb(file)).toBe(true);
    expect(relativeToWeb(file!)).toBe('src/utils/htmlSanitizer.ts');
  });

  it('resolves an explicit `@storefront/...` import inside PointOfSale.Storefront/src', async () => {
    const file = await resolvedFile('@storefront/features/checkout/paymentFlow', webTestImporter);

    expect(file, '@storefront/features/checkout/paymentFlow must resolve').not.toBeNull();
    expect(isInsideStorefront(file)).toBe(true);
    expect(toPosix(file!)).toMatch(/PointOfSale\.Storefront\/src\/features\/checkout\/paymentFlow\.ts$/);
  });

  it('keeps the two applications on different files for the same subpath', async () => {
    const web = await resolvedFile('@/types/storeConfig', webTestImporter);
    const storefront = await resolvedFile('@storefront/types/storeConfig', webTestImporter);

    expect(isInsideWeb(web)).toBe(true);
    expect(isInsideStorefront(storefront)).toBe(true);
    expect(web).not.toBe(storefront);
  });

  it('keeps the Storefront package on its own `@` root so cross-app modules still load', async () => {
    const file = await resolvedFile('@/store/cartStore', storefrontImporter);

    expect(isInsideStorefront(file), '@/store/cartStore from Storefront source must stay in Storefront').toBe(true);
    expect(toPosix(file!)).toMatch(/Storefront\/src\/store\/cartStore\.ts$/);
  });

  it('never lets a package-local Web `@` import resolve outside PointOfSale.Web/src', async () => {
    const specifiers = collectWebAliasSpecifiers(WEB_SRC);
    expect(specifiers.length, 'Web source must be scanned').toBeGreaterThan(0);

    const escaped: string[] = [];
    for (const { file, specifier } of specifiers) {
      const resolved = await resolvedFile(specifier, path.join(WEB_SRC, file));
      if (!isInsideWeb(resolved) || !fs.existsSync(resolved!)) {
        escaped.push(`${file}: ${specifier} -> ${resolved ?? 'unresolved'}`);
      }
    }

    expect(escaped, `Web \`@\` imports must resolve to an existing file inside PointOfSale.Web/src:\n${escaped.join('\n')}`).toEqual([]);
  });

  it('keeps Storefront-owned modules on the Storefront root for specifiers that exist in both applications', async () => {
    // `@/services/api` and `@/i18n` exist in both packages. The Storefront importer
    // must keep them, otherwise cross-application modules silently load Web code.
    const specifiers = ['@/analytics', '@/store/cartStore', '@/components/layout', '@/services/api', '@/i18n'];
    const resolved = await Promise.all(
      specifiers.map(async (specifier) => [specifier, await resolvedFile(specifier, path.join(STOREFRONT_SRC, 'app', 'App.tsx'))] as const),
    );

    for (const [specifier, file] of resolved) {
      expect(isInsideStorefront(file), `${specifier} from Storefront source must stay in Storefront, got ${file}`).toBe(true);
      expect(fs.existsSync(file!), `${specifier} must resolve to an existing Storefront file`).toBe(true);
    }
  });
});
