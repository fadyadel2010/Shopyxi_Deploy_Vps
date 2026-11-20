import type { AxeResults, RunOptions } from 'axe-core'
export function runAxe(container: HTMLElement, options?: RunOptions): Promise<AxeResults>
export function expectNoAxeViolations(container: HTMLElement, options?: RunOptions): Promise<void>
