import { useTranslation } from 'react-i18next'
import { Globe } from 'lucide-react'
import { Button } from '../design-system/primitives'

export default function LanguageSwitcher({ variant = 'default' }) {
    const { t, i18n } = useTranslation()
    const isArabic = (i18n.resolvedLanguage || i18n.language || '').startsWith('ar')

    const toggleLanguage = () => {
        const newLang = isArabic ? 'en' : 'ar'
        void i18n.changeLanguage(newLang)
    }

    const label = isArabic ? t('languageSwitcher.switchToEnglish') : t('languageSwitcher.switchToArabic')
    const shortLabel = isArabic ? t('languageSwitcher.shortEnglish') : t('languageSwitcher.shortArabic')

    if (variant === 'login') {
        return (
            <Button
                type="button"
                variant="ghost"
                leadingIcon={<Globe size={18} aria-hidden="true" />}
                onClick={toggleLanguage}
                aria-label={`${shortLabel} — ${label}`}
                title={label}
            >
                <bdi>{shortLabel}</bdi>
            </Button>
        )
    }

    return (
        <button
            type="button"
            onClick={toggleLanguage}
            className="flex items-center gap-2 px-2 sm:px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-100 rounded-lg transition-colors"
            aria-label={`${shortLabel} — ${label}`}
            title={isArabic ? t('languageSwitcher.switchToEnglish') : t('languageSwitcher.switchToArabic')}
        >
            <Globe size={20} aria-hidden="true" />
            <span className="font-semibold">
                {isArabic ? t('languageSwitcher.shortEnglish') : t('languageSwitcher.shortArabic')}
            </span>
        </button>
    )
}
