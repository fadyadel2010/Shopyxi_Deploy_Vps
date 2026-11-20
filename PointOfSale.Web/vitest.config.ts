import { defineConfig } from 'vitest/config';
import type { Plugin } from 'vite';
import path from 'path';

const webSrc = path.resolve(__dirname, './src');
const storefrontSrc = path.resolve(__dirname, '../PointOfSale.Storefront/src');

/** True when `file` is strictly inside `root` (not root itself, not a sibling). */
function isInside(root: string, file: string): boolean {
  const relative = path.relative(root, file);
  return relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative);
}

/**
 * Storefront sources are authored against their own `@` root (873 imports).
 * Web's `@` must never leave `PointOfSale.Web/src`, so a global alias cannot
 * express both contracts. This resolves `@/...` per importer: modules owned by
 * the Storefront package keep their own root, everything else uses Web's.
 * Cross-application test files are unaffected - they import `@storefront/...`,
 * which the alias table below resolves directly.
 */
function storefrontOwnAliasRoot(): Plugin {
  return {
    name: 'shopixy-storefront-own-alias-root',
    enforce: 'pre',
    resolveId: {
      // Must run before Vite's alias plugin rewrites `@` for every importer.
      order: 'pre',
      async handler(source, importer) {
        if (!importer || !isInside(storefrontSrc, importer)) return null;
        if (source !== '@' && !source.startsWith('@/')) return null;
        const rewritten = source === '@' ? '@storefront' : `@storefront/${source.slice(2)}`;
        return this.resolve(rewritten, importer, { skipSelf: true });
      },
    },
  };
}

export default defineConfig({
  // Match the production React plugin and tsconfig's react-jsx transform. Vite's
  // compatibility line must not fall back to the classic global React factory.
  esbuild: { jsx: 'automatic' },
  plugins: [storefrontOwnAliasRoot()],
  test: {
    globals: true,
    environment: 'jsdom', // Use jsdom for React component tests
    include: ['src/**/*.spec.ts', 'src/**/*.test.ts', 'src/**/*.spec.tsx', 'src/**/*.test.tsx', 'src/**/*.test.jsx'],
    setupFiles: ['./src/test/setup.ts'],
  },
  resolve: {
    // Cross-app storefront component tests must share the renderer's React instance.
    dedupe: ['react', 'react-dom'],
    alias: {
      // Web `@` must match production (vite.config.js): package-local imports only.
      // Storefront is a different application; cross-application tests must say so
      // explicitly through `@storefront`.
      '@': webSrc,
      '@web': webSrc,
      '@storefront': storefrontSrc,
      '@theme-authority': path.resolve(__dirname, '../theme-authority'),
    },
  },
});
