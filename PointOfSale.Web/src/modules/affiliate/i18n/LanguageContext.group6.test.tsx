import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it } from 'vitest'
import { LanguageProvider, useLanguage } from './LanguageContext'
import i18n from '../../../i18n/config'

function Harness() {
    const { lang, toggleLang } = useLanguage()
    return <button onClick={toggleLang}>{lang}</button>
}

beforeEach(async () => {
    localStorage.clear()
    await i18n.changeLanguage('en')
})
afterEach(async () => { await i18n.changeLanguage('en') })

it('keeps html/body aligned through independent Affiliate switches and restores the current host language', async () => {
    localStorage.setItem('affiliate_lang', 'ar')
    const view = render(<LanguageProvider><Harness /></LanguageProvider>)
    for (const element of [document.documentElement, document.body]) {
        expect(element).toHaveAttribute('dir', 'rtl')
        expect(element).toHaveAttribute('lang', 'ar')
    }
    fireEvent.click(screen.getByRole('button', { name: 'ar' }))
    expect(document.body).toHaveAttribute('dir', 'ltr')
    await act(async () => { await i18n.changeLanguage('ar') })
    // The active Affiliate choice is independent of the host's saved choice.
    expect(document.documentElement).toHaveAttribute('dir', 'ltr')
    view.unmount()
    for (const element of [document.documentElement, document.body]) {
        expect(element).toHaveAttribute('dir', 'rtl')
        expect(element).toHaveAttribute('lang', 'ar')
    }
})
