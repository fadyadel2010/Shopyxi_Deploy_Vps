// Run the repository's TypeScript tests with its declared ts-jest toolchain.
// Application entrypoints and real Redis/WhatsApp services are not test setup.
module.exports = {
  preset: 'ts-jest',
  testEnvironment: 'node',
  testMatch: ['<rootDir>/src/**/*.test.ts'],
};
