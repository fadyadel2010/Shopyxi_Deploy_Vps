import { defineConfig } from '@playwright/test'

// Default stays system Chrome. Cross-engine Login runs are opt-in and use
// Playwright-managed Firefox and WebKit binaries, not a Safari installation.
const port = Number(process.env.POS_E2E_PORT ?? 4174)
const baseURL = `http://127.0.0.1:${port}`
const crossBrowser = process.env.POS_E2E_CROSS_BROWSER === '1'

export default defineConfig({
    testDir: './e2e/design-system',
    timeout: 60_000,
    fullyParallel: false,
    workers: 1,
    reporter: [['list']],
    ...(crossBrowser ? {
        projects: [
            { name: 'chrome', use: { browserName: 'chromium' as const, channel: 'chrome' } },
            { name: 'firefox', use: { browserName: 'firefox' as const, channel: undefined } },
            { name: 'webkit', use: { browserName: 'webkit' as const, channel: undefined } },
        ],
    } : {}),
    use: {
        baseURL,
        ...(crossBrowser ? {} : { channel: 'chrome' }),
        headless: true,
        viewport: { width: 1280, height: 800 },
        trace: 'retain-on-failure',
    },
    webServer: {
        command: `npm run dev -- --host 127.0.0.1 --port ${port} --strictPort`,
        url: `${baseURL}/e2e/fixtures/design-system.html`,
        reuseExistingServer: process.env.POS_E2E_REUSE_SERVER === '1',
        timeout: 120_000,
    },
})
