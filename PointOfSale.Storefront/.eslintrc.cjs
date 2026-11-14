// Storefront has its own HTTP boundary, not the POS application's apiClient.
// Resolve tooling locally so a standalone Storefront install is sufficient.
module.exports = {
    root: true,
    env: { browser: true, es2020: true },
    parser: require.resolve('@typescript-eslint/parser'),
    plugins: ['@typescript-eslint', 'react-hooks', 'react-refresh'],
    extends: ['eslint:recommended', 'plugin:@typescript-eslint/recommended', 'plugin:react-hooks/recommended'],
    ignorePatterns: ['dist', '.eslintrc.cjs'],
    rules: {
        'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
        'no-restricted-imports': ['error', {
            paths: [{ name: 'axios', message: 'Use the Storefront httpClient transport/error boundary.' }],
            patterns: ['**/PointOfSale.Web/**'],
        }],
    },
    overrides: [{
        files: ['src/services/api/httpClient.ts'],
        rules: { 'no-restricted-imports': ['error', { patterns: ['**/PointOfSale.Web/**'] }] },
    }, {
        files: ['scripts/*.mjs'],
        env: { node: true },
    }],
};
