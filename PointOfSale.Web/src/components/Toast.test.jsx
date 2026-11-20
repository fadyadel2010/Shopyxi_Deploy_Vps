import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PosSurfaceContext } from '../design-system/overlays/posOverlayContext'
import { ToastProvider, useToast } from './Toast'

vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key, options) => options?.defaultValue ?? key,
        i18n: { language: 'en', resolvedLanguage: 'en' },
    }),
}))

function Harness({ label, kind = 'success', message, duration = 0 }) {
    const toast = useToast()
    const show = {
        success: toast.showSuccess,
        error: toast.showError,
        warning: toast.showWarning,
        info: toast.showInfo,
    }[kind]

    return (
        <button type="button" onClick={() => show(message, duration)}>
            {label}
        </button>
    )
}

afterEach(() => vi.useRealTimers())

describe('Toast surface bridge', () => {
    it('preserves legacy default duration and clears timers on unmount', () => {
        vi.useFakeTimers()
        function DefaultDuration() {
            const { showInfo } = useToast()
            return <button onClick={() => showInfo('Default timeout')}>Notify</button>
        }
        const view = render(<ToastProvider><DefaultDuration /></ToastProvider>)
        fireEvent.click(screen.getByText('Notify'))
        act(() => vi.advanceTimersByTime(2999))
        expect(screen.getByText('Default timeout')).toBeInTheDocument()
        act(() => vi.advanceTimersByTime(1))
        expect(screen.queryByText('Default timeout')).toBeNull()
        fireEvent.click(screen.getByText('Notify'))
        view.unmount()
        expect(vi.getTimerCount()).toBe(0)
    })

    it('supports explicit surface options without marking the container', () => {
        render(<ToastProvider><Harness label="Explicit POS" message="Explicit notice" duration={{ surface: 'pos', duration: 0 }} /></ToastProvider>)
        fireEvent.click(screen.getByText('Explicit POS'))
        const wrapper = screen.getByText('Explicit notice').closest('[role="status"]')
        expect(wrapper).toHaveAttribute('data-product', 'pos')
        expect(wrapper.parentElement).not.toHaveAttribute('data-product')
    })

    it('keeps context consumers stable while notifications change', () => {
        const renderCount = vi.fn()
        function Consumer() {
            const toast = useToast()
            renderCount()
            return <button onClick={() => toast.showInfo('Stable context', 0)}>Notify</button>
        }
        render(<ToastProvider><Consumer /></ToastProvider>)
        const initialCount = renderCount.mock.calls.length
        fireEvent.click(screen.getByText('Notify'))
        fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
        expect(renderCount).toHaveBeenCalledTimes(initialCount)
    })
    it('marks only POS-origin messages and keeps the shared container unmarked', () => {
        render(
            <ToastProvider>
                <PosSurfaceContext.Provider value="pos">
                    <Harness label="Fire POS toast" message="Sale completed" />
                </PosSurfaceContext.Provider>
                <Harness label="Fire default toast" message="Admin notice" kind="info" />
            </ToastProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Fire POS toast' }))
        fireEvent.click(screen.getByRole('button', { name: 'Fire default toast' }))

        const posMessage = screen.getByText('Sale completed')
        const posWrapper = posMessage.closest('[data-product="pos"]')
        expect(posWrapper).not.toBeNull()
        expect(posWrapper.getAttribute('role')).toBe('status')
        expect(posWrapper.getAttribute('aria-live')).toBe('polite')
        expect(posMessage.closest('.pos-toast')).not.toBeNull()

        const sharedContainer = posWrapper.parentElement
        expect(sharedContainer.hasAttribute('data-product')).toBe(false)

        const defaultMessage = screen.getByText('Admin notice')
        expect(defaultMessage.closest('[data-product="pos"]')).toBeNull()
        expect(defaultMessage.closest('.bg-blue-500')).not.toBeNull()
        expect(document.querySelectorAll('[data-product="pos"]')).toHaveLength(1)
    })

    it('keeps legacy severity semantics and styling outside POS', () => {
        render(
            <ToastProvider>
                <Harness label="Fire error" message="Could not save" kind="error" />
            </ToastProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Fire error' }))

        const wrapper = screen.getByText('Could not save').closest('[role="alert"]')
        expect(wrapper).not.toBeNull()
        expect(wrapper.getAttribute('aria-live')).toBe('assertive')
        expect(wrapper.hasAttribute('data-product')).toBe(false)
        expect(screen.getByText('Could not save').closest('.bg-red-500')).not.toBeNull()
        expect(document.querySelector('[data-product="pos"]')).toBeNull()
    })

    it('renders POS error severity with alert semantics and tokenized classes', () => {
        render(
            <ToastProvider>
                <PosSurfaceContext.Provider value="pos">
                    <Harness label="Fire POS error" message="Refund failed" kind="error" />
                </PosSurfaceContext.Provider>
            </ToastProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Fire POS error' }))

        const message = screen.getByText('Refund failed')
        const wrapper = message.closest('[role="alert"]')
        expect(wrapper.getAttribute('aria-live')).toBe('assertive')
        expect(wrapper.hasAttribute('data-product')).toBe(true)
        expect(message.closest('.pos-toast--error')).not.toBeNull()
    })

    it('dismisses POS toasts through the unchanged dismiss affordance', () => {
        render(
            <ToastProvider>
                <PosSurfaceContext.Provider value="pos">
                    <Harness label="Fire POS toast" message="Dismiss me" />
                </PosSurfaceContext.Provider>
            </ToastProvider>,
        )

        fireEvent.click(screen.getByRole('button', { name: 'Fire POS toast' }))
        expect(screen.getByText('Dismiss me')).toBeTruthy()
        fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
        expect(screen.queryByText('Dismiss me')).toBeNull()
    })
})
