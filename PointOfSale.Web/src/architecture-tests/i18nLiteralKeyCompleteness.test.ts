/**
 * @vitest-environment node
 *
 * Static localization gate for RB-04.
 *
 * The Storefront i18n layer returns the key itself when a translation is
 * missing, so a literal `t('a.b')` call that is absent from either catalog
 * renders machine text to a user. This gate protects Storefront source from
 * the already-installed Web runner (Storefront dependencies are not required).
 */
import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

const WEB_ROOT = path.resolve(__dirname, '..', '..');
const REPO_ROOT = path.resolve(WEB_ROOT, '..');
const STOREFRONT_SRC = path.join(REPO_ROOT, 'PointOfSale.Storefront', 'src');

type Catalog = Record<string, unknown>;

function readCatalog(file: string): Catalog {
    return JSON.parse(fs.readFileSync(file, 'utf8')) as Catalog;
}

function hasKey(catalog: Catalog, key: string): boolean {
    let current: unknown = catalog;
    for (const part of key.split('.')) {
        if (!current || typeof current !== 'object' || !(part in (current as Catalog))) {
            return false;
        }
        current = (current as Catalog)[part];
    }
    return typeof current === 'string' && current.trim().length > 0;
}

function walk(dir: string): string[] {
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            return entry.name === 'locales' || entry.name === 'node_modules' ? [] : walk(full);
        }
        return /\.(ts|tsx)$/.test(entry.name) && !/\.(test|spec)\.(ts|tsx)$/.test(entry.name) ? [full] : [];
    });
}

/** Static literal keys only: dynamic template/interpolated specifiers are out of scope. */
export function collectLiteralKeys(files: string[]): Map<string, string[]> {
    const key = /(?<![A-Za-z0-9_$])t\(\s*(['"])([A-Za-z0-9_$-]+(?:\.[A-Za-z0-9_$-]+)+)\1/g;
    const found = new Map<string, string[]>();
    for (const file of files) {
        const source = fs.readFileSync(file, 'utf8');
        for (const match of source.matchAll(key)) {
            const translationKey = match[2];
            const filesForKey = found.get(translationKey) ?? [];
            filesForKey.push(path.relative(REPO_ROOT, file).split(path.sep).join('/'));
            found.set(translationKey, filesForKey);
        }
    }
    return found;
}

function missingKeys(catalog: Catalog, keys: Iterable<string>): string[] {
    return [...keys].filter(key => !hasKey(catalog, key)).sort();
}

const storefrontEn = readCatalog(path.join(STOREFRONT_SRC, 'i18n', 'locales', 'en.json'));
const storefrontAr = readCatalog(path.join(STOREFRONT_SRC, 'i18n', 'locales', 'ar.json'));
const webEn = readCatalog(path.join(WEB_ROOT, 'src', 'i18n', 'locales', 'en.json'));
const webAr = readCatalog(path.join(WEB_ROOT, 'src', 'i18n', 'locales', 'ar.json'));
const literalKeys = collectLiteralKeys(walk(STOREFRONT_SRC));

describe('localization literal-key completeness', () => {
    it('resolves every literal Storefront translation key in both catalogs', () => {
        const keys = [...literalKeys.keys()];
        expect(keys.length).toBeGreaterThan(0);
        expect(missingKeys(storefrontEn, keys)).toEqual([]);
        expect(missingKeys(storefrontAr, keys)).toEqual([]);
    });

    it('resolves the Web guard strings in both catalogs', () => {
        for (const key of ['common.restoringSession', 'common.validatingVendorAuthority']) {
            expect(hasKey(webEn, key), key).toBe(true);
            expect(hasKey(webAr, key), key).toBe(true);
        }
    });

    it('reports a removed key as missing so the gate cannot pass silently', () => {
        const broken = { ...storefrontEn, filters: { ...(storefrontEn.filters as Catalog), noResults: undefined } };
        expect(missingKeys(broken, ['filters.noResults'])).toEqual(['filters.noResults']);
    });
});
