import axe from 'axe-core'
import { expect } from 'vitest'

// Rules that need a full page or capabilities jsdom lacks. Color contrast is
// measured in the Playwright suite instead; jsdom has no layout or canvas.
const DISABLED_RULES = {
  'color-contrast': { enabled: false },
  region: { enabled: false },
  'landmark-one-main': { enabled: false },
  'page-has-heading-one': { enabled: false },
  'html-has-lang': { enabled: false },
}

export async function runAxe(container, options = {}) {
  const result = await axe.run(container, {
    ...options,
    rules: { ...DISABLED_RULES, ...(options.rules || {}) },
  })
  return result
}

export async function expectNoAxeViolations(container, options = {}) {
  const { violations } = await runAxe(container, options)
  const summary = violations.map((violation) => ({
    id: violation.id,
    impact: violation.impact,
    nodes: violation.nodes.map((node) => node.target.join(' ')),
    help: violation.help,
  }))

  expect(summary).toEqual([])
}
