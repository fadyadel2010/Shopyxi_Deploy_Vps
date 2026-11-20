import { act, cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { I18nextProvider } from 'react-i18next'
import { createInstance } from 'i18next'
import LanguageSwitcher from './LanguageSwitcher'

afterEach(cleanup)

describe('release language-switcher refinement', () => {
    it.each(['login', 'default'])('keeps the globe, label and keyboard switching in %s', async variant => {
        const instance = createInstance()
        await instance.init({
            lng: 'en',
            resources: {
                en: { translation: { languageSwitcher: { shortArabic: 'AR', switchToArabic: 'Switch to Arabic' } } },
                ar: { translation: { languageSwitcher: { shortEnglish: 'EN', switchToEnglish: 'Switch to English' } } },
            },
        })
        const independentAuthority = createInstance()
        await independentAuthority.init({ lng: 'en', resources: {} })
        const { container } = render(
            <I18nextProvider i18n={instance}><LanguageSwitcher variant={variant} /></I18nextProvider>,
        )
        const user = userEvent.setup()
        const button = screen.getByRole('button', { name: 'AR — Switch to Arabic' })
        expect(button).toHaveTextContent('AR')
        expect(button).toHaveAttribute('type', 'button')
        expect(button.querySelector('svg.lucide-globe')).toHaveAttribute('aria-hidden', 'true')
        await user.tab()
        expect(button).toHaveFocus()
        await user.keyboard('{Enter}')
        expect(instance.language).toBe('ar')
        expect(instance.dir()).toBe('rtl')
        expect(screen.getByRole('button', { name: 'EN — Switch to English' })).toHaveFocus()
        await user.keyboard(' ')
        expect(instance.language).toBe('en')
        expect(instance.dir()).toBe('ltr')
        expect(independentAuthority.language).toBe('en')
        expect(container.querySelector('.lucide-languages')).toBeNull()
        await act(async () => { await instance.changeLanguage('en') })
    })
})
