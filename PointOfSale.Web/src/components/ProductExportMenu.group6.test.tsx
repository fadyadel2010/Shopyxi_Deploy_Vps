import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it, vi } from 'vitest'
import '../design-system/test/radixJsdomPolyfills'
import ProductExportMenu from './ProductExportMenu'
import i18n from '../i18n/config'

it('provides a keyboard-highlighted item treatment and restores focus without exporting on Escape', async () => {
    await i18n.changeLanguage('ar')
    const exportData = vi.fn()
    render(<ProductExportMenu available selectedCount={0} exporting={null} onExport={exportData} />)
    const trigger = screen.getByRole('button')
    trigger.focus()
    const user = userEvent.setup()
    await user.keyboard('{Enter}{ArrowDown}')
    const menu = await screen.findByRole('menu')
    expect(menu).toHaveAttribute('dir', 'rtl')
    await waitFor(() => expect(document.activeElement).toHaveAttribute('role', 'menuitem'))
    expect(document.activeElement).toHaveClass('data-[highlighted]:bg-gray-100')
    await user.keyboard('{Escape}')
    expect(exportData).not.toHaveBeenCalled()
    await waitFor(() => expect(trigger).toHaveFocus())
    await i18n.changeLanguage('en')
})
