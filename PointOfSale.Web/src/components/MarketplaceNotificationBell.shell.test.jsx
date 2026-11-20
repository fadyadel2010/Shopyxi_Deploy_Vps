import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import MarketplaceNotificationBell, { computeNotificationPanelPosition } from './MarketplaceNotificationBell'
import i18n from '../i18n/config'

const apiMock = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock('../services/api', () => ({ default: apiMock }))
vi.mock('../marketplace/authority', () => ({ isServiceMarketplaceWorkflowActor: () => true }))

const storeState = {
    isAuthenticated: true,
    user: { id: 'user-1', tenantId: 'tenant-1', role: 'Owner' },
}
vi.mock('../store/authStore', () => ({
    useAuthStore: Object.assign(
        (selector) => (selector ? selector(storeState) : storeState),
        { getState: () => storeState },
    ),
}))

function Location() {
    return <output data-testid="route">{useLocation().pathname}{useLocation().search}</output>
}

const mount = (entries = ['/']) => render(
    <MemoryRouter initialEntries={entries}>
        <MarketplaceNotificationBell />
        <Location />
    </MemoryRouter>,
)

const pending = (id, title) => ({
    requestId: id,
    status: 'Pending',
    serviceTitle: title,
    createdAt: new Date(Date.now() - 120000).toISOString(),
})

describe('notification panel geometry', () => {
    const triggerRect = { left: 900, right: 940, top: 16, bottom: 56 }

    it('aligns to the trigger inline-end and clamps inside the viewport', () => {
        const ltr = computeNotificationPanelPosition({
            triggerRect, viewportWidth: 1024, viewportHeight: 768, isRTL: false,
        })
        expect(ltr).toEqual({ left: 620, top: 64, width: 320, maxHeight: 288 })

        const rtl = computeNotificationPanelPosition({
            triggerRect: { left: 16, right: 56, top: 16, bottom: 56 },
            viewportWidth: 1024,
            viewportHeight: 768,
            isRTL: true,
        })
        expect(rtl.left).toBe(16)
        expect(rtl.left + rtl.width).toBe(336)
    })

    it('shrinks to narrow viewports and keeps a minimum visible height near the bottom edge', () => {
        const narrow = computeNotificationPanelPosition({
            triggerRect: { left: 300, right: 320, top: 10, bottom: 40 },
            viewportWidth: 320,
            viewportHeight: 568,
            isRTL: false,
        })
        expect(narrow.width).toBe(304)
        expect(narrow.left).toBe(8)
        expect(narrow.left + narrow.width).toBe(312)

        const bottom = computeNotificationPanelPosition({
            triggerRect: { left: 10, right: 50, top: 700, bottom: 740 },
            viewportWidth: 390,
            viewportHeight: 760,
            isRTL: false,
        })
        expect(bottom.top).toBe(656)
        expect(bottom.maxHeight).toBe(96)
    })
})

describe('shell marketplace notification bell', () => {
    beforeEach(async () => {
        localStorage.clear()
        apiMock.get.mockReset()
        apiMock.post.mockReset()
        await i18n.changeLanguage('en')
    })

    afterEach(() => {
        cleanup()
        vi.restoreAllMocks()
    })

    it('shows an expanded-state trigger, a bounded panel, and closes on Escape with focus return', async () => {
        apiMock.get.mockResolvedValue({ data: [pending('r1', 'Logo design')] })
        mount()

        const trigger = await screen.findByRole('button', {
            name: i18n.t('servicesMarketplace.notifications.newRequestsTitle', { count: 1 }),
        })
        expect(trigger).toHaveAttribute('aria-expanded', 'false')
        expect(trigger).toHaveAttribute('aria-controls', 'pos-marketplace-notifications')
        expect(document.querySelector('.pos-notifications__count')?.textContent).toBe('1')

        fireEvent.click(trigger)
        const panel = await screen.findByRole('region', {
            name: i18n.t('servicesMarketplace.incoming.title'),
        })
        expect(trigger).toHaveAttribute('aria-expanded', 'true')
        expect(within(panel).getByText('Logo design')).toBeInTheDocument()

        const style = panel.style
        const left = Number.parseFloat(style.left)
        const width = Number.parseFloat(style.width)
        expect(left).toBeGreaterThanOrEqual(8)
        expect(left + width).toBeLessThanOrEqual(window.innerWidth - 8)
        expect(Number.parseFloat(style.maxHeight)).toBeGreaterThanOrEqual(96)

        fireEvent.keyDown(document, { key: 'Escape' })
        await waitFor(() => expect(screen.queryByRole('region')).toBeNull())
        expect(trigger).toHaveFocus()
    })

    it('dismisses on an outside pointer press', async () => {
        apiMock.get.mockResolvedValue({ data: [pending('r1', 'Logo design')] })
        mount()
        const trigger = await screen.findByRole('button', {
            name: i18n.t('servicesMarketplace.notifications.newRequestsTitle', { count: 1 }),
        })
        fireEvent.click(trigger)
        await screen.findByRole('region')
        fireEvent.mouseDown(document.body)
        await waitFor(() => expect(screen.queryByRole('region')).toBeNull())
    })

    it('keeps the existing destination when there are no unseen requests', async () => {
        apiMock.get.mockResolvedValue({ data: [] })
        mount()

        const trigger = await screen.findByRole('button', {
            name: i18n.t('servicesMarketplace.notifications.marketplaceRequests'),
        })
        expect(trigger).not.toHaveAttribute('aria-expanded')
        expect(trigger).not.toHaveAttribute('aria-controls')
        fireEvent.click(trigger)
        await waitFor(() => expect(screen.getByTestId('route')).toHaveTextContent('/marketplace?tab=incoming'))
    })
})
