// @vitest-environment jsdom
import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AppearancePreview } from './AppearancePreview'

vi.mock('./builder/ThemeRuntimePreview', () => ({ ThemeRuntimePreview: () => null }))

describe('Appearance preview currency', () => {
    it('keeps sample product prices EGP-only', () => {
        const { container } = render(<AppearancePreview
            colors={{ primary: '#2563eb', secondary: '#1e293b', background: '#ffffff', text: '#111827', muted: '#6b7280' }}
            font="Inter" baseSize={16} borderRadius={8}
        />)
        expect(screen.getAllByText('EGP 99.99')).toHaveLength(2)
        expect(container.textContent).not.toContain('$')
    })
})
