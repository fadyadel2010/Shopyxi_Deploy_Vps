import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const read = (path: string) => readFileSync(fileURLToPath(new URL(path, import.meta.url)), 'utf8')

describe('release POS sidebar brand refinement', () => {
    it('removes only the sidebar plate decoration without resizing the approved artwork', () => {
        const css = read('./PosShell.css')
        const plate = css.match(/\[data-product="pos"\] \.pos-sidebar__symbol-plate \{([^}]+)\}/)?.[1]
        expect(plate).toContain('background-color: transparent;')
        expect(plate).toContain('border: 0;')
        expect(plate).toContain('box-shadow: none;')
        expect(plate).toContain('width: var(--pos-space-10);')
        expect(plate).toContain('height: var(--pos-space-10);')
        expect(plate).toContain('align-items: center;')
        const image = css.match(/\.pos-sidebar__symbol-plate > img \{([^}]+)\}/)?.[1]
        expect(image).toContain('width: 1.75rem;')
        expect(image).toContain('height: 1.75rem;')
        expect(read('./Sidebar.jsx')).toContain("../assets/brand/SHOPYXI_standalone_symbol.svg")
    })

    it('retains the transparent vector asset, including the white bag inside the artwork', () => {
        const svg = read('../assets/brand/SHOPYXI_standalone_symbol.svg')
        expect(svg).toContain('viewBox="0 0 520 520"')
        expect(svg).not.toMatch(/<rect\b|<image\b|background(?:-color)?\s*[:=]/i)
        expect(svg).toContain('fill="#FFFFFF" stroke="#0B1F3B"')
    })
})
