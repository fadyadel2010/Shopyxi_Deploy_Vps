import { useEffect, useMemo, type ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, EyeOff, KeyRound, Link2, SlidersHorizontal, ToggleLeft } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { saInput } from '../../ui/SuperAdminUI'
import type { PaymentProviderConfiguration } from '../paymentProviderTypes'

type SecretReplacementKey = 'apiKey' | 'secretKey' | 'hmacSecret'

type SecretReplacementDirtyState = Record<SecretReplacementKey, boolean>

type Translate = (english: string, arabic: string) => string

interface PaymobConfigurationFormProps {
    provider: PaymentProviderConfiguration
    secretReplacementDirty: SecretReplacementDirtyState
    onChange: (provider: PaymentProviderConfiguration) => void
    onSecretReplacementChange: (key: SecretReplacementKey, value: string) => void
    onValidationChange: (errors: string[]) => void
}

const runtimeFields = [
    ['timeoutSeconds', 'Timeout'],
    ['retryCount', 'Retry Count'],
    ['requestTimeoutSeconds', 'Request Timeout'],
    ['maximumRetryAttempts', 'Maximum Retry Attempts'],
] as const

const SecretMask = '**************'

function textValue(provider: PaymentProviderConfiguration, key: string, fallback = '') {
    const value = provider.configuration[key]
    if (value === undefined || value === null) {
        return fallback
    }

    return String(value)
}

function boolValue(provider: PaymentProviderConfiguration, key: string, fallback = false) {
    const value = provider.configuration[key]
    if (typeof value === 'boolean') {
        return value
    }

    if (typeof value === 'string') {
        return value.toLowerCase() === 'true'
    }

    return fallback
}

function hasConfiguredCredential(
    provider: PaymentProviderConfiguration,
    key: 'apiKeyConfigured' | 'secretKeyConfigured' | 'hmacSecretConfigured',
) {
    return boolValue(provider, key)
}

function validatePaymobDraft(
    provider: PaymentProviderConfiguration,
    secretReplacementDirty: SecretReplacementDirtyState,
    tr: Translate,
) {
    const errors: string[] = []
    const environment = textValue(provider, 'environment', provider.environment)
    const currency = textValue(provider, 'currency')
    const integrationId = textValue(provider, 'integrationId')
    const publicKey = textValue(provider, 'publicKey')
    const merchantIdentifier = textValue(provider, 'merchantIdentifier')

    if (!textValue(provider, 'displayName', provider.displayName).trim()) {
        errors.push(tr('Display name is required.', 'اسم العرض مطلوب.'))
    }

    if (!textValue(provider, 'description', provider.description).trim()) {
        errors.push(tr('Description is required.', 'الوصف مطلوب.'))
    }

    if (environment !== 'Sandbox' && environment !== 'Production') {
        errors.push(tr('Unknown environment.', 'بيئة غير معروفة.'))
    }

    if (!/^[A-Z]{3}$/.test(currency)) {
        errors.push(tr('Invalid currency.', 'عملة غير صالحة.'))
    }

    if (!/^[1-9]\d*$/.test(integrationId)) {
        errors.push(tr('Invalid integration ID.', 'معرّف التكامل غير صالح.'))
    }

    if (!/^[1-9]\d*$/.test(merchantIdentifier)) {
        errors.push(tr('Invalid merchant identifier.', 'معرّف التاجر غير صالح.'))
    }

    const keyEnvironment = environment === 'Sandbox' ? 'test' : 'live'
    if (!publicKey.startsWith(`egy_pk_${keyEnvironment}_`)) {
        errors.push(
            tr(
                'Invalid public key for the selected environment.',
                'المفتاح العام غير صالح للبيئة المحددة.',
            ),
        )
    }

    if (!hasConfiguredCredential(provider, 'apiKeyConfigured') && !secretReplacementDirty.apiKey) {
        errors.push(tr('Missing API key.', 'مفتاح API مفقود.'))
    }

    if (!hasConfiguredCredential(provider, 'secretKeyConfigured') && !secretReplacementDirty.secretKey) {
        errors.push(tr('Missing secret key.', 'المفتاح السري مفقود.'))
    }

    if (!hasConfiguredCredential(provider, 'hmacSecretConfigured') && !secretReplacementDirty.hmacSecret) {
        errors.push(tr('Missing HMAC secret.', 'سر HMAC مفقود.'))
    }

    runtimeFields.forEach(([key, label]) => {
        const value = textValue(provider, key, '0')
        if (!/^\d+$/.test(value)) {
            errors.push(
                tr(
                    `${label} must be a non-negative whole number.`,
                    `يجب أن يكون ${label} عددًا صحيحًا غير سالب.`,
                ),
            )
        }
    })

    return errors
}

export function PaymobConfigurationForm({
    provider,
    secretReplacementDirty,
    onChange,
    onSecretReplacementChange,
    onValidationChange,
}: PaymobConfigurationFormProps) {
    const { tr } = useSuperAdminLocale()
    const validationErrors = useMemo(
        () => validatePaymobDraft(provider, secretReplacementDirty, tr),
        [provider, secretReplacementDirty, tr],
    )

    useEffect(() => {
        onValidationChange(validationErrors)
    }, [onValidationChange, validationErrors])

    function updateConfiguration(key: string, value: unknown) {
        const nextConfiguration = {
            ...provider.configuration,
            [key]: value,
        }

        const nextProvider: PaymentProviderConfiguration = {
            ...provider,
            enabled: key === 'enabled' ? Boolean(value) : provider.enabled,
            environment: key === 'environment' ? String(value) : provider.environment,
            displayName: key === 'displayName' ? String(value) : provider.displayName,
            description: key === 'description' ? String(value) : provider.description,
            configuration: nextConfiguration,
        }

        if (key === 'environment') {
            nextProvider.configuration.sandbox = value === 'Sandbox'
        }

        onChange(nextProvider)
    }

    const secretKeyConfigured = hasConfiguredCredential(provider, 'secretKeyConfigured')
    const hmacSecretConfigured = hasConfiguredCredential(provider, 'hmacSecretConfigured')
    const apiKeyConfigured = hasConfiguredCredential(provider, 'apiKeyConfigured')

    return (
        <div className="space-y-3">
            <FormSection icon={<ToggleLeft className="h-4 w-4" />} title={tr('General', 'عام')}>
                <div className="grid gap-3 md:grid-cols-2">
                    <label className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-sm font-semibold text-slate-800">
                        {tr('Enabled', 'مفعّل')}
                        <input
                            type="checkbox"
                            checked={boolValue(provider, 'enabled', provider.enabled)}
                            onChange={(event) => updateConfiguration('enabled', event.target.checked)}
                            className="h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
                        />
                    </label>
                    <label className="space-y-1.5">
                        <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Environment', 'البيئة')}
                        </span>
                        <select
                            value={textValue(provider, 'environment', provider.environment)}
                            onChange={(event) => updateConfiguration('environment', event.target.value)}
                            className={saInput}
                        >
                            <option value="Sandbox">Sandbox</option>
                            <option value="Production">Production</option>
                        </select>
                    </label>
                    <TextField
                        label={tr('Display name', 'اسم العرض')}
                        value={textValue(provider, 'displayName', provider.displayName)}
                        onChange={(value) => updateConfiguration('displayName', value)}
                    />
                    <TextField
                        label={tr('Currency', 'العملة')}
                        value={textValue(provider, 'currency')}
                        maxLength={3}
                        mono
                        onChange={(value) => updateConfiguration('currency', value.trim().toUpperCase())}
                    />
                    <label className="space-y-1.5 md:col-span-2">
                        <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Description', 'الوصف')}
                        </span>
                        <textarea
                            value={textValue(provider, 'description', provider.description)}
                            onChange={(event) => updateConfiguration('description', event.target.value)}
                            rows={3}
                            className={saInput}
                        />
                    </label>
                </div>
            </FormSection>

            <FormSection icon={<KeyRound className="h-4 w-4" />} title={tr('Credentials', 'بيانات الاعتماد')}>
                <div className="grid gap-3 md:grid-cols-2">
                    <SecretReplacementField
                        label="API Key"
                        configured={apiKeyConfigured}
                        helpText={tr(
                            'Account-level Paymob API credential for auth-token based dashboard reconciliation.',
                            'اعتماد Paymob على مستوى الحساب لمطابقة اللوحة عبر رمز المصادقة.',
                        )}
                        onChange={(value) => onSecretReplacementChange('apiKey', value)}
                    />
                    <SecretReplacementField
                        label="Secret Key"
                        configured={secretKeyConfigured}
                        onChange={(value) => onSecretReplacementChange('secretKey', value)}
                    />
                    <SecretReplacementField
                        label="HMAC Secret"
                        configured={hmacSecretConfigured}
                        onChange={(value) => onSecretReplacementChange('hmacSecret', value)}
                    />
                </div>
            </FormSection>

            <FormSection icon={<Link2 className="h-4 w-4" />} title={tr('Integration', 'التكامل')}>
                <div className="grid gap-3 md:grid-cols-2">
                    <TextField
                        label={tr('Integration ID', 'معرّف التكامل')}
                        value={textValue(provider, 'integrationId')}
                        mono
                        onChange={(value) => updateConfiguration('integrationId', value)}
                    />
                    <TextField
                        label={tr('Public key', 'المفتاح العام')}
                        value={textValue(provider, 'publicKey')}
                        mono
                        onChange={(value) => updateConfiguration('publicKey', value)}
                    />
                    <TextField
                        label={tr('Merchant identifier', 'معرّف التاجر')}
                        value={textValue(provider, 'merchantIdentifier')}
                        mono
                        onChange={(value) => updateConfiguration('merchantIdentifier', value)}
                    />
                    <TextField
                        label={tr('Provider account name', 'اسم حساب المزود')}
                        value={textValue(provider, 'providerAccountName')}
                        onChange={(value) => updateConfiguration('providerAccountName', value)}
                    />
                </div>
            </FormSection>

            <FormSection icon={<SlidersHorizontal className="h-4 w-4" />} title={tr('Runtime limits', 'حدود التشغيل')}>
                <div className="grid gap-3 md:grid-cols-2">
                    {runtimeFields.map(([key, label]) => (
                        <TextField
                            key={key}
                            label={tr(label, runtimeLabels[key])}
                            value={textValue(provider, key)}
                            type="number"
                            min={0}
                            mono
                            onChange={(value) => updateConfiguration(key, value)}
                        />
                    ))}
                </div>
            </FormSection>

            <FormSection
                icon={<CheckCircle2 className="h-4 w-4" />}
                title={tr('Callback information', 'معلومات الاستدعاء')}
            >
                <div className="grid gap-3 md:grid-cols-2">
                    <ReadOnlyField
                        label={tr('Webhook method', 'طريقة Webhook')}
                        value={textValue(provider, 'webhookMethod', 'POST')}
                    />
                    <TextField
                        label={tr('Webhook URL', 'عنوان Webhook')}
                        value={textValue(provider, 'webhookUrl')}
                        mono
                        onChange={(value) => updateConfiguration('webhookUrl', value)}
                    />
                    <ReadOnlyField
                        label={tr('Webhook path', 'مسار Webhook')}
                        value={textValue(provider, 'webhookPath')}
                    />
                    <ReadOnlyField
                        label={tr('Browser callback method', 'طريقة استدعاء المتصفح')}
                        value={textValue(provider, 'browserCallbackMethod', 'GET')}
                    />
                    <TextField
                        label={tr('Browser callback URL', 'عنوان استدعاء المتصفح')}
                        value={textValue(provider, 'browserCallbackUrl')}
                        mono
                        onChange={(value) => updateConfiguration('browserCallbackUrl', value)}
                    />
                    <ReadOnlyField
                        label={tr('Browser callback path', 'مسار استدعاء المتصفح')}
                        value={textValue(provider, 'browserCallbackPath')}
                    />
                </div>
            </FormSection>

            <FormSection icon={<EyeOff className="h-4 w-4" />} title={tr('Advanced', 'متقدم')}>
                <div className="grid gap-3 md:grid-cols-2">
                    <ReadOnlyField label={tr('Provider code', 'رمز المزود')} value={provider.providerCode} mono />
                    <ReadOnlyField
                        label={tr('Configuration status', 'حالة الإعداد')}
                        value={provider.status}
                    />
                </div>
            </FormSection>

            {validationErrors.length > 0 ? (
                <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3.5 text-sm text-amber-900">
                    <div className="flex items-center gap-2 font-semibold">
                        <AlertTriangle className="h-4 w-4" />
                        {tr('Validation summary', 'ملخص التحقق')}
                    </div>
                    <ul className="mt-2 list-disc space-y-1 ps-5">
                        {validationErrors.map((error) => (
                            <li key={error}>{error}</li>
                        ))}
                    </ul>
                </div>
            ) : null}
        </div>
    )
}

const runtimeLabels: Record<string, string> = {
    timeoutSeconds: 'المهلة',
    retryCount: 'عدد المحاولات',
    requestTimeoutSeconds: 'مهلة الطلب',
    maximumRetryAttempts: 'الحد الأقصى لمحاولات الإعادة',
}

function FormSection({
    icon,
    title,
    children,
}: {
    icon: ReactNode
    title: string
    children: ReactNode
}) {
    return (
        <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="mb-3 flex items-center gap-2.5">
                <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-700">
                    {icon}
                </div>
                <h3 className="text-base font-semibold text-slate-950">{title}</h3>
            </div>
            {children}
        </section>
    )
}

function TextField({
    label,
    value,
    onChange,
    type = 'text',
    min,
    maxLength,
    mono = false,
}: {
    label: string
    value: string
    onChange: (value: string) => void
    type?: 'text' | 'number'
    min?: number
    maxLength?: number
    mono?: boolean
}) {
    return (
        <label className="space-y-1.5">
            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">{label}</span>
            <input
                type={type}
                min={min}
                maxLength={maxLength}
                value={value}
                dir={mono ? 'ltr' : undefined}
                onChange={(event) => onChange(event.target.value)}
                className={`${saInput} ${mono ? 'font-mono text-xs' : ''}`}
            />
        </label>
    )
}

function ReadOnlyField({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
    return (
        <label className="space-y-1.5">
            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">{label}</span>
            <input
                type="text"
                value={value}
                readOnly
                dir={mono ? 'ltr' : undefined}
                className={`${saInput} bg-slate-50 text-slate-700 ${mono ? 'font-mono text-xs' : ''}`}
            />
        </label>
    )
}

function SecretReplacementField({
    label,
    configured,
    helpText,
    onChange,
}: {
    label: string
    configured: boolean
    helpText?: string
    onChange: (value: string) => void
}) {
    const { tr } = useSuperAdminLocale()
    const inputId = `secret-replacement-${label.replace(/\s+/g, '-').toLowerCase()}`

    return (
        <label className="space-y-1.5" htmlFor={inputId}>
            <span className="flex flex-wrap items-center gap-2 text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                {label}
                <span
                    className={
                        configured
                            ? 'rounded-full border border-emerald-200 bg-emerald-50 px-2 py-0.5 text-[10px] font-semibold text-emerald-800'
                            : 'rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[10px] font-semibold text-amber-800'
                    }
                >
                    {configured ? tr('Configured', 'مُهيأ') : tr('Not configured', 'غير مهيأ')}
                </span>
            </span>
            <div className="flex gap-2">
                <input
                    id={inputId}
                    type="password"
                    placeholder={configured ? SecretMask : ''}
                    autoComplete="new-password"
                    dir="ltr"
                    onChange={(event) => onChange(event.target.value)}
                    className={`${saInput} min-w-0 flex-1 font-mono`}
                />
                <button
                    type="button"
                    onClick={(event) => {
                        const input = event.currentTarget.parentElement?.querySelector('input')
                        input?.focus()
                    }}
                    className="inline-flex items-center rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50"
                >
                    {tr('Replace', 'استبدال')}
                </button>
            </div>
            {helpText ? <p className="text-xs leading-5 text-slate-500">{helpText}</p> : null}
        </label>
    )
}
