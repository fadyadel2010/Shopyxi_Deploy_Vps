import { Settings } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import type { PaymentProviderConfiguration } from '../paymentProviderTypes'
import { PaymobConfigurationForm } from './PaymobConfigurationForm'

type SecretReplacementKey = 'apiKey' | 'secretKey' | 'hmacSecret'

export function ConfigurationSection({
    provider,
    secretReplacementDirty,
    onChange,
    onSecretReplacementChange,
    onValidationChange,
}: {
    provider: PaymentProviderConfiguration | null
    secretReplacementDirty: Record<SecretReplacementKey, boolean>
    onChange: (provider: PaymentProviderConfiguration) => void
    onSecretReplacementChange: (key: SecretReplacementKey, value: string) => void
    onValidationChange: (errors: string[]) => void
}) {
    const { tr } = useSuperAdminLocale()

    if (!provider) {
        return (
            <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-6 text-sm text-slate-600">
                {tr('Select a provider to configure it.', 'اختر مزودًا لتهيئته.')}
            </div>
        )
    }

    if (provider.providerCode === 'PAYMOB') {
        return (
            <PaymobConfigurationForm
                provider={provider}
                secretReplacementDirty={secretReplacementDirty}
                onChange={onChange}
                onSecretReplacementChange={onSecretReplacementChange}
                onValidationChange={onValidationChange}
            />
        )
    }

    return (
        <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-4">
            <div className="flex items-start gap-3">
                <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-white text-slate-700 shadow-sm">
                    <Settings className="h-4 w-4" />
                </div>
                <div>
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Provider configuration', 'إعداد المزود')}
                    </p>
                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                        {tr(`${provider.displayName} configuration`, `إعداد ${provider.displayName}`)}
                    </h3>
                    <p className="mt-1 text-sm leading-6 text-slate-600">
                        {tr(
                            'This provider has not published a configuration form yet.',
                            'لم ينشر هذا المزود نموذج إعداد بعد.',
                        )}
                    </p>
                </div>
            </div>
        </div>
    )
}
