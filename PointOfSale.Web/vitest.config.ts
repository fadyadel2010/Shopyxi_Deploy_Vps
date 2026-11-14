import { defineConfig } from 'vitest/config';
import path from 'path';

export default defineConfig({
  // Match the production React plugin and tsconfig's react-jsx transform. Vite's
  // compatibility line must not fall back to the classic global React factory.
  esbuild: { jsx: 'automatic' },
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
      '@': path.resolve(__dirname, '../PointOfSale.Storefront/src'),
      '@web': path.resolve(__dirname, './src'),
      '@theme-authority': path.resolve(__dirname, '../theme-authority'),
    },
  },
});
