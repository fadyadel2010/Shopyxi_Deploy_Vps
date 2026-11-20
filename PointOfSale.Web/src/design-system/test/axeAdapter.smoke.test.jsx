import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { expectNoAxeViolations, runAxe } from './axe'

describe('axe adapter', () => {
  it('runs axe against a rendered jsdom container', async () => {
    const { container } = render(
      <div>
        <label htmlFor="smoke-name">Name</label>
        <input id="smoke-name" type="text" />
      </div>,
    )

    const result = await runAxe(container)
    expect(result.violations).toEqual([])
  })

  it('reports a real labeling violation', async () => {
    const { container } = render(<input type="text" />)
    const { violations } = await runAxe(container)
    expect(violations.map((violation) => violation.id)).toContain('label')
  })

  it('accepts a clean container through the expectation helper', async () => {
    const { container } = render(<button type="button">Save</button>)
    await expectNoAxeViolations(container)
  })
})
