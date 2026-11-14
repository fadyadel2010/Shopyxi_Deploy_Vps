import { useLanguage } from '../../i18n/LanguageContext'

export default function SyntheticQaNotice({ amount = 0, currency, formatCurrency }) {
    const { t } = useLanguage()
    if (!amount) return null
    return (
        <div role="note" className="p-3 rounded-lg border border-amber-500/20 bg-amber-500/5 text-sm text-amber-700 dark:text-amber-300">
            <p className="font-medium">{t('portal.syntheticQa.label')}</p>
            <p>{t('portal.syntheticQa.excluded')}: {formatCurrency(amount, currency)}</p>
        </div>
    )
}
