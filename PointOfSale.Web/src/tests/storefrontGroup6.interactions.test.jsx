import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ConfirmDialog } from '@storefront/components/feedback/ConfirmDialog'
import { ProductGalleryModal } from '@storefront/components/products/ProductGalleryModal'
const locale = vi.hoisted(() => ({ language: 'en' }))
vi.mock('@storefront/i18n', async () => {
    const { t } = await import('@storefront/i18n/languageStore')
    return { useTranslation: () => ({
        t: (key, params) => t(key, params, locale.language),
        language: locale.language, direction: locale.language === 'ar' ? 'rtl' : 'ltr',
    }) }
})
// Use the real Button, but isolate the unrelated Storefront theme store from
// the Web renderer's second installed React (no application provider changes).
vi.mock('@storefront/store', async importOriginal => ({
    ...await importOriginal(),
    useStoreConfigStore: () => ({ ui: { buttonStyle: 'solid' } }),
}))

beforeEach(() => { locale.language = 'en'; vi.spyOn(window, 'scrollTo').mockImplementation(() => {}) })
afterEach(() => { locale.language = 'en'; vi.restoreAllMocks() })

it('Storefront confirmation keeps focus inside during pending and restores focus only on close', async () => {
    const cancel = vi.fn()
    const opener = document.createElement('button')
    document.body.appendChild(opener)
    opener.focus()
    const props = { isOpen: true, title: 'Delete', message: 'Delete this item?', onCancel: cancel, onConfirm: vi.fn() }
    const view = render(<ConfirmDialog {...props} />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus())
    view.rerender(<ConfirmDialog {...props} isLoading />)
    expect(screen.getByRole('dialog')).toContainElement(document.activeElement)
    await userEvent.setup().tab()
    expect(screen.getByRole('dialog')).toContainElement(document.activeElement)
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(cancel).not.toHaveBeenCalled()
    view.rerender(<ConfirmDialog {...props} isOpen={false} />)
    expect(opener).toHaveFocus()
    opener.remove()
})

it('Storefront confirmation localizes default buttons and guards same-tick activation', async () => {
    locale.language = 'ar'
    const confirm = vi.fn()
    render(<ConfirmDialog isOpen title="حذف" message="تأكيد الحذف" onConfirm={confirm} onCancel={vi.fn()} />)
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveAttribute('dir', 'rtl')
    const button = within(dialog).getByRole('button', { name: 'تأكيد' })
    act(() => { fireEvent.click(button); fireEvent.click(button) })
    expect(confirm).toHaveBeenCalledTimes(1)
})

it('Storefront media gallery localizes navigation and restores its invoking control after media changes', async () => {
    locale.language = 'ar'
    const opener = document.createElement('button')
    document.body.appendChild(opener)
    opener.focus()
    const close = vi.fn()
    const previous = vi.fn()
    const next = vi.fn()
    const items = [
        { id: 'one', type: 'image', mediaType: 'image', url: '/one.jpg' },
        { id: 'two', type: 'image', mediaType: 'image', url: '/two.jpg' },
    ]
    const props = { isOpen: true, mediaItems: items, activeIndex: 0, productName: 'منتج',
        onClose: close, onSelect: vi.fn(), onPrevious: previous, onNext: next }
    const view = render(<ProductGalleryModal {...props} />)
    expect(screen.getByRole('button', { name: 'إغلاق' })).toHaveFocus()
    fireEvent.keyDown(document, { key: 'ArrowRight' })
    expect(previous).toHaveBeenCalledTimes(1)
    view.rerender(<ProductGalleryModal {...props} activeIndex={1} />)
    view.unmount()
    expect(opener).toHaveFocus()
    opener.remove()
})
