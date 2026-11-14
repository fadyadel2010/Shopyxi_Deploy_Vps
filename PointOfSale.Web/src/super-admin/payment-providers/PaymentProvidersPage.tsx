import { useCallback, useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { AlertTriangle, RefreshCw, ShieldCheck } from 'lucide-react'
import { useToast } from '../../components/Toast'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    GuidePanel,
    InlineNotice,
    KpiCard,
    PageHeader,
    SuperAdminPage,
    saButton,
} from '../ui/SuperAdminUI'
import { ConfigurationSection } from './components/ConfigurationSection'
import { ConnectionResultPanel } from './components/ConnectionResultPanel'
import { DevelopmentOnlinePaymentSimulationPanel } from './components/DevelopmentOnlinePaymentSimulationPanel'
import { OperationalMessages } from './components/OperationalMessages'
import { PaymentProviderDetails } from './components/PaymentProviderDetails'
import { PaymentProviderLayout } from './components/PaymentProviderLayout'
import { PaymentProviderList } from './components/PaymentProviderList'
import { ProviderDashboard } from './components/ProviderDashboard'
import { ProviderHealthCard } from './components/ProviderHealthCard'
import { ProviderSessionReconciliationPanel } from './components/ProviderSessionReconciliationPanel'
import { RotateSecretDialog } from './components/RotateSecretDialog'
import { SaveBar } from './components/SaveBar'
import { SecretCard } from './components/SecretCard'
import { TestConnectionButton } from './components/TestConnectionButton'
import {
    getPaymentProviderConfiguration,
    getPaymentProviderDashboard,
    getPaymentProviders,
    getPaymentProviderSecrets,
    reconcilePaymentProviderSessionNotCreated,
    rotatePaymentProviderSecret,
    savePaymentProviderConfiguration,
    simulateDevelopmentOnlinePaymentSuccess,
    testPaymentProviderConnection,
} from './paymentProvidersService'
import { usePaymentProviderAdministrationState } from './usePaymentProviderAdministrationState'
import type {
    PaymentProviderConfiguration,
    PaymentProviderSecretMetadata,
    PaymentProviderSessionReconciliationRequest,
    RotatePaymentProviderSecretRequest,
} from './paymentProviderTypes'

function getErrorMessage(error: unknown, fallback: string) {
    return error instanceof Error && error.message ? error.message : fallback
}

export default function PaymentProvidersPage() {
    const { showError, showSuccess } = useToast()
    const { tr, formatNumber } = useSuperAdminLocale()
    const [state, dispatch] = usePaymentProviderAdministrationState()
    const secretReplacementsRef = useRef<Record<string, string>>({})
    const [secretReplacementDirty, setSecretReplacementDirty] = useState({ apiKey: false, secretKey: false, hmacSecret: false })
    const [formResetVersion, setFormResetVersion] = useState(0)
    const [rotatingSecretType, setRotatingSecretType] = useState<string | null>(null)

    const providersQuery = useQuery({
        queryKey: ['super-admin-payment-providers'],
        queryFn: getPaymentProviders,
        staleTime: 60_000,
    })

    const dashboardQuery = useQuery({
        queryKey: ['super-admin-payment-provider-dashboard'],
        queryFn: getPaymentProviderDashboard,
        staleTime: 60_000,
    })

    const selectedProviderQuery = useQuery({
        queryKey: ['super-admin-payment-provider', state.selectedProviderCode],
        queryFn: () => getPaymentProviderConfiguration(state.selectedProviderCode!),
        enabled: Boolean(state.selectedProviderCode),
    })

    const secretsQuery = useQuery({
        queryKey: ['super-admin-payment-provider-secrets', state.selectedProviderCode],
        queryFn: () => getPaymentProviderSecrets(state.selectedProviderCode!),
        enabled: Boolean(state.selectedProviderCode),
    })

    const saveMutation = useMutation({
        mutationFn: () =>
            savePaymentProviderConfiguration(state.selectedProviderCode!, {
                providerCode: state.selectedProviderCode!,
                enabled: state.configuration?.enabled,
                environment: state.configuration?.environment,
                configuration: state.configuration?.configuration ?? {},
                metadata: state.configuration?.metadata ?? {},
                secretReplacements: secretReplacementsRef.current,
            }),
        onSuccess: (response) => {
            secretReplacementsRef.current = {}
            setSecretReplacementDirty({ apiKey: false, secretKey: false, hmacSecret: false })
            setFormResetVersion((value) => value + 1)
            dispatch({
                type: 'save:success',
                configuration: response.provider,
                errors: response.validationErrors,
            })
            showSuccess(
                tr(
                    'CommerceHub payment provider configuration saved.',
                    'تم حفظ إعداد مزود الدفع في CommerceHub.',
                ),
            )
        },
        onError: (error) => {
            const message = getErrorMessage(
                error,
                tr(
                    'Unable to save payment provider configuration.',
                    'تعذّر حفظ إعداد مزود الدفع.',
                ),
            )
            dispatch({ type: 'save:error', errors: [message] })
            showError(message)
        },
    })

    const testConnectionMutation = useMutation({
        mutationFn: () => testPaymentProviderConnection(state.selectedProviderCode!),
        onSuccess: (response) => {
            dispatch({ type: 'test:success', result: response.result, health: response.health })
            if (response.result.success) {
                showSuccess(response.result.message)
            } else {
                showError(response.result.message)
            }
        },
        onError: (error) => {
            const message = getErrorMessage(
                error,
                tr('Unable to test payment provider connection.', 'تعذّر اختبار اتصال مزود الدفع.'),
            )
            dispatch({ type: 'test:error', errors: [message] })
            showError(message)
        },
    })

    const rotateSecretMutation = useMutation({
        mutationFn: (payload: RotatePaymentProviderSecretRequest) =>
            rotatePaymentProviderSecret(state.selectedProviderCode!, payload),
        onSuccess: (response) => {
            setRotatingSecretType(null)
            secretsQuery.refetch()
            selectedProviderQuery.refetch()
            showSuccess(
                tr(
                    `${response.secret.secretType} rotated to version ${response.secret.version}.`,
                    `تم تدوير ${response.secret.secretType} إلى الإصدار ${response.secret.version}.`,
                ),
            )
        },
        onError: (error) => {
            const message = getErrorMessage(
                error,
                tr('Unable to rotate provider secret.', 'تعذّر تدوير سر مزود الدفع.'),
            )
            showError(message)
        },
    })

    const reconcileSessionMutation = useMutation({
        mutationFn: (payload: PaymentProviderSessionReconciliationRequest) =>
            reconcilePaymentProviderSessionNotCreated(state.selectedProviderCode!, payload),
        onSuccess: (response) => {
            dashboardQuery.refetch()
            selectedProviderQuery.refetch()
            showSuccess(response.message)
        },
        onError: (error) => {
            showError(
                getErrorMessage(
                    error,
                    tr('Unable to reconcile the provider session.', 'تعذّر مطابقة جلسة مزود الدفع.'),
                ),
            )
        },
    })

    const developmentSuccessMutation = useMutation({
        mutationFn: ({ paymentId, idempotencyKey, reason }: { paymentId: string; idempotencyKey: string; reason: string }) =>
            simulateDevelopmentOnlinePaymentSuccess(paymentId, {
                idempotencyKey,
                reason,
                administratorConfirmation: true,
            }),
        onSuccess: (response) => {
            dashboardQuery.refetch()
            showSuccess(response.message)
        },
        onError: (error) => {
            showError(
                getErrorMessage(
                    error,
                    tr(
                        'Unable to execute the Development payment success simulation.',
                        'تعذّر تنفيذ محاكاة نجاح الدفع في بيئة التطوير.',
                    ),
                ),
            )
        },
    })

    useEffect(() => {
        if (providersQuery.isFetching) {
            dispatch({ type: 'load:start' })
            return
        }

        if (providersQuery.isError) {
            dispatch({
                type: 'load:error',
                errors: [
                    getErrorMessage(
                        providersQuery.error,
                        tr('Unable to load payment providers.', 'تعذّر تحميل مزودي الدفع.'),
                    ),
                ],
            })
            return
        }

        if (providersQuery.data) {
            dispatch({ type: 'load:success', providers: providersQuery.data.providers })
        }
    }, [providersQuery.data, providersQuery.error, providersQuery.isError, providersQuery.isFetching, dispatch, tr])

    useEffect(() => {
        if (selectedProviderQuery.isError) {
            dispatch({
                type: 'load:error',
                errors: [
                    getErrorMessage(
                        selectedProviderQuery.error,
                        tr('Unable to load payment provider details.', 'تعذّر تحميل تفاصيل مزود الدفع.'),
                    ),
                ],
            })
            return
        }

        if (selectedProviderQuery.data) {
            dispatch({
                type: 'configuration:success',
                configuration: selectedProviderQuery.data.provider,
                errors: selectedProviderQuery.data.validationErrors,
            })
        }
    }, [selectedProviderQuery.data, selectedProviderQuery.error, selectedProviderQuery.isError, dispatch, tr])

    function handleProviderSelect(providerCode: string) {
        secretReplacementsRef.current = {}
        setSecretReplacementDirty({ apiKey: false, secretKey: false, hmacSecret: false })
        setFormResetVersion((value) => value + 1)
        setRotatingSecretType(null)
        dispatch({ type: 'select', providerCode })
    }

    function handleSave() {
        if (!state.selectedProviderCode || !state.configuration) {
            showError(tr('Select a provider before saving.', 'اختر مزودًا قبل الحفظ.'))
            return
        }

        if (state.validationErrors.length > 0) {
            showError(
                tr(
                    'Resolve validation errors before saving.',
                    'عالج أخطاء التحقق قبل الحفظ.',
                ),
            )
            return
        }

        dispatch({ type: 'save:start' })
        saveMutation.mutate()
    }

    function handleCancel() {
        secretReplacementsRef.current = {}
        setSecretReplacementDirty({ apiKey: false, secretKey: false, hmacSecret: false })
        setFormResetVersion((value) => value + 1)
        dispatch({ type: 'configuration:reset' })
    }

    function handleTestConnection() {
        if (!state.selectedProviderCode || !state.configuration) {
            showError(tr('Select a provider before testing the connection.', 'اختر مزودًا قبل اختبار الاتصال.'))
            return
        }

        if (state.dirty) {
            showError(
                tr(
                    'Save provider configuration changes before testing the connection.',
                    'احفظ تغييرات إعداد مزود الدفع قبل اختبار الاتصال.',
                ),
            )
            return
        }

        dispatch({ type: 'test:start' })
        testConnectionMutation.mutate()
    }

    function handleConfigurationChange(configuration: PaymentProviderConfiguration) {
        dispatch({ type: 'configuration:update', configuration })
    }

    function handleSecretReplacementChange(key: 'apiKey' | 'secretKey' | 'hmacSecret', value: string) {
        const trimmedValue = value.trim()
        if (trimmedValue) {
            secretReplacementsRef.current[key] = trimmedValue
        } else {
            delete secretReplacementsRef.current[key]
        }

        setSecretReplacementDirty((current) => ({
            ...current,
            [key]: Boolean(trimmedValue),
        }))
        dispatch({ type: 'dirty:set', dirty: true })
    }

    const handleValidationChange = useCallback((errors: string[]) => {
        dispatch({ type: 'validation:set', errors })
    }, [dispatch])

    function handleRotateSecret(payload: RotatePaymentProviderSecretRequest) {
        if (!state.selectedProviderCode) {
            showError(tr('Select a provider before rotating secrets.', 'اختر مزودًا قبل تدوير الأسرار.'))
            return
        }

        rotateSecretMutation.mutate(payload)
    }

    const secretsByType = (secretsQuery.data?.secrets ?? []).reduce<Record<string, PaymentProviderSecretMetadata[]>>(
        (groups, secret) => {
            groups[secret.secretType] = [...(groups[secret.secretType] ?? []), secret]
            return groups
        },
        {},
    )

    const secretTypes = ['API Key', 'HMAC Secret']

    const guide = {
        en: {
            title: 'Payment providers guide',
            purpose:
                'Platform-owned configuration and health surface for CommerceHub payment providers. Saved provider settings decide which online payment methods checkout can offer.',
            when:
                'Use it when onboarding a provider, rotating credentials, switching between Sandbox and Production, testing connectivity, investigating payment failures, or reconciling a Paymob session that was never created.',
            steps: [
                'Select a provider from the list and review its dashboard, health, and operational messages.',
                'Edit enabled state, environment, credentials, runtime limits, and callback URLs as needed.',
                'Rotate a secret only when the previous value is compromised or being retired; confirm the rotation is authorized.',
                'Save the configuration, then run Test connection with no unsaved edits.',
                'Use manual session reconciliation only with exact operator evidence and after checking Paymob, Orders, and Transactions.',
            ],
            statuses: [
                { label: 'Enabled / Disabled', description: 'Whether checkout may offer this provider.' },
                { label: 'Sandbox / Production', description: 'The provider environment used for live calls.' },
                { label: 'Healthy / Warning / Error', description: 'Result of the most recent provider health check.' },
                { label: 'Secret Active', description: 'The current credential version used by the provider.' },
                { label: 'Pending / Retired', description: 'A new version awaiting activation or an old version no longer used.' },
                { label: 'Validation summary', description: 'Blocks saving while required provider fields are invalid.' },
            ],
            safety: [
                'Secrets are write-only. Existing values are never displayed; only replacement prompts are shown.',
                'Never rotate a secret without confirming the new value with the provider vault first.',
                'Save before testing. Tests always use the persisted configuration, not unsaved edits.',
                'Session reconciliation closes only an evidence-free gap; it cannot override a paid payment or provider transaction.',
                'The Development simulation exists only in Development and never moves external money.',
                'Provider changes affect live checkout, so verify environment and currency before saving.',
            ],
        },
        ar: {
            title: 'دليل مزودي الدفع',
            purpose:
                'واجهة مملوكة للمنصة لإعداد مزودي الدفع في CommerceHub وصحتهم. تحدد الإعدادات المحفوظة وسائل الدفع الإلكتروني المتاحة عند إتمام الطلب.',
            when:
                'استخدمه عند إضافة مزود، أو تدوير بيانات الاعتماد، أو التبديل بين Sandbox وProduction، أو اختبار الاتصال، أو التحقيق في فشل الدفع، أو مطابقة جلسة Paymob لم تُنشأ.',
            steps: [
                'اختر مزودًا من القائمة وراجع لوحته وصحته ورسائله التشغيلية.',
                'عدّل حالة التفعيل والبيئة وبيانات الاعتماد وحدود التشغيل وعناوين الاستدعاء حسب الحاجة.',
                'دوّر السر فقط عند تسريب القيمة السابقة أو إيقافها؛ وأكد أن التدوير مصرح به.',
                'احفظ الإعداد ثم شغّل اختبار الاتصال دون تعديلات غير محفوظة.',
                'استخدم المطابقة اليدوية للجلسة فقط بأدلة تشغيلية دقيقة وبعد فحص Paymob والطلبات والحركات.',
            ],
            statuses: [
                { label: 'مفعّل / معطّل', description: 'ما إذا كان الدفع قد يعرض هذا المزود.' },
                { label: 'Sandbox / Production', description: 'بيئة المزود المستخدمة في الاستدعاءات الفعلية.' },
                { label: 'سليم / تحذير / خطأ', description: 'نتيجة أحدث فحص لصحة المزود.' },
                { label: 'سر نشط', description: 'إصدار بيانات الاعتماد الحالي المستخدم لدى المزود.' },
                { label: 'قيد الانتظار / مسحوب', description: 'إصدار جديد بانتظار التفعيل أو إصدار قديم لم يعد مستخدمًا.' },
                { label: 'ملخص التحقق', description: 'يمنع الحفظ ما دامت حقول المزود المطلوبة غير صالحة.' },
            ],
            safety: [
                'الأسرار للكتابة فقط. لا تُعرض القيم الحالية أبدًا؛ تُعرض حقول الاستبدال فقط.',
                'لا تدوّر سرًا دون تأكيد القيمة الجديدة من خزنة المزود أولًا.',
                'احفظ قبل الاختبار. تستخدم الاختبارات الإعداد المحفوظ دائمًا وليس التعديلات غير المحفوظة.',
                'مطابقة الجلسة تغلق فقط فجوة بلا دليل؛ ولا يمكنها تجاوز دفعة مدفوعة أو حركة مزود.',
                'محاكاة التطوير موجودة في بيئة التطوير فقط ولا تحرّك أموالًا خارجية.',
                'تغييرات المزود تؤثر في الدفع المباشر، لذا تحقق من البيئة والعملة قبل الحفظ.',
            ],
        },
    }

    const summary = dashboardQuery.data?.summary
    const header = (
        <PageHeader
            eyebrow={tr('Super Admin · Payments', 'الإدارة العليا · المدفوعات')}
            title={tr('Payment providers', 'مزودو الدفع')}
            description={tr(
                'Platform-owned provider configuration, credential lifecycle, and connection health.',
                'إعداد مزودي الدفع المملوك للمنصة ودورة حياة بيانات الاعتماد وصحة الاتصال.',
            )}
            actions={
                <button
                    type="button"
                    className={saButton.secondary}
                    disabled={dashboardQuery.isFetching}
                    onClick={() => {
                        dashboardQuery.refetch()
                        if (state.selectedProviderCode) {
                            selectedProviderQuery.refetch()
                            secretsQuery.refetch()
                        }
                    }}
                >
                    <RefreshCw className={`h-4 w-4 ${dashboardQuery.isFetching ? 'animate-spin' : ''}`} />
                    {dashboardQuery.isFetching
                        ? tr('Refreshing', 'جارٍ التحديث')
                        : tr('Refresh providers', 'تحديث المزودين')}
                </button>
            }
            aside={
                <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-200 bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-800">
                    <ShieldCheck className="h-3.5 w-3.5" />
                    {tr('Platform-owned', 'مملوك للمنصة')}
                </span>
            }
        />
    )

    return (
        <SuperAdminPage>
            {header}

            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiCard
                    label={tr('Providers', 'المزودون')}
                    value={formatNumber(summary?.providerCount ?? 0)}
                    hint={tr('Registered provider accounts', 'حسابات المزودين المسجلة')}
                    tone="slate"
                    isLoading={dashboardQuery.isLoading}
                />
                <KpiCard
                    label={tr('Enabled', 'المفعّلة')}
                    value={formatNumber(summary?.enabledCount ?? 0)}
                    hint={tr('Available to checkout', 'متاحة لإتمام الطلب')}
                    tone="emerald"
                    isLoading={dashboardQuery.isLoading}
                />
                <KpiCard
                    label={tr('Healthy', 'السليمة')}
                    value={formatNumber(summary?.healthyCount ?? 0)}
                    hint={tr('Latest check succeeded', 'نجح أحدث فحص')}
                    tone="sky"
                    isLoading={dashboardQuery.isLoading}
                />
                <KpiCard
                    label={tr('Configured secrets', 'الأسرار المهيأة')}
                    value={formatNumber(summary?.configuredSecretCount ?? 0)}
                    hint={tr('Active credential versions', 'إصدارات الاعتماد النشطة')}
                    tone="violet"
                    isLoading={dashboardQuery.isLoading}
                />
            </div>

            <GuidePanel guide={guide} />

            {providersQuery.isError && !providersQuery.data ? (
                <InlineNotice tone="rose" title={tr('Providers unavailable', 'المزودون غير متاحين')}>
                    <p>
                        {getErrorMessage(
                            providersQuery.error,
                            tr('Unable to load payment providers.', 'تعذّر تحميل مزودي الدفع.'),
                        )}
                    </p>
                    <button
                        type="button"
                        className={`${saButton.secondary} mt-2`}
                        onClick={() => void providersQuery.refetch()}
                    >
                        <RefreshCw className="h-4 w-4" />
                        {tr('Retry request', 'إعادة المحاولة')}
                    </button>
                </InlineNotice>
            ) : null}

            <PaymentProviderLayout
                header={null}
                list={
                    <>
                        <div className="flex items-start justify-between gap-3 rounded-2xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                            <div>
                                <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                                    {tr('Provider list', 'قائمة المزودين')}
                                </p>
                                <h2 className="mt-1 text-base font-semibold text-slate-950">
                                    {tr('Available providers', 'المزودون المتاحون')}
                                </h2>
                            </div>
                            <span className="rounded-full border border-slate-200 bg-slate-50 px-2 py-1 text-xs font-semibold text-slate-600">
                                {formatNumber(state.providerList.length)}
                            </span>
                        </div>
                        <PaymentProviderList
                            providers={state.providerList}
                            loading={state.loading}
                            selectedProviderCode={state.selectedProviderCode}
                            onSelect={handleProviderSelect}
                        />
                    </>
                }
                details={
                    <>
                        <ProviderDashboard
                            dashboard={dashboardQuery.data}
                            loading={dashboardQuery.isLoading}
                            refreshing={dashboardQuery.isFetching}
                            onRefresh={() => {
                                dashboardQuery.refetch()
                                if (state.selectedProviderCode) {
                                    selectedProviderQuery.refetch()
                                    secretsQuery.refetch()
                                }
                            }}
                        />
                        {state.validationErrors.length > 0 ? (
                            <InlineNotice tone="amber" title={tr('Validation summary', 'ملخص التحقق')}>
                                <ul className="list-disc space-y-1 ps-5">
                                    {state.validationErrors.map((error) => (
                                        <li key={error} dir="auto">
                                            {error}
                                        </li>
                                    ))}
                                </ul>
                            </InlineNotice>
                        ) : null}
                        <PaymentProviderDetails provider={state.configuration} />
                        <ConfigurationSection
                            key={`${state.selectedProviderCode ?? 'none'}-${formResetVersion}`}
                            provider={state.configuration}
                            secretReplacementDirty={secretReplacementDirty}
                            onChange={handleConfigurationChange}
                            onSecretReplacementChange={handleSecretReplacementChange}
                            onValidationChange={handleValidationChange}
                        />
                        <ProviderHealthCard health={state.configuration?.health} />
                        <OperationalMessages health={state.configuration?.health} />
                        <section className="space-y-4">
                            <div className="rounded-2xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                                <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                                    {tr('Secret lifecycle', 'دورة حياة الأسرار')}
                                </p>
                                <h3 className="mt-1 text-base font-semibold text-slate-950">
                                    {tr('Version history', 'سجل الإصدارات')}
                                </h3>
                            </div>
                            {secretTypes.map((secretType) => {
                                const versions = (secretsByType[secretType] ?? [])
                                    .slice()
                                    .sort((left, right) => right.version - left.version)
                                const current = versions.find((secret) => secret.status === 'Active')

                                return (
                                    <SecretCard
                                        key={secretType}
                                        secretType={secretType}
                                        current={current}
                                        versions={versions}
                                        onRotate={setRotatingSecretType}
                                    />
                                )
                            })}
                        </section>
                        <section className="rounded-2xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                            <div className="flex flex-wrap items-center justify-between gap-3">
                                <div>
                                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                                        {tr('Provider action', 'إجراء المزود')}
                                    </p>
                                    <h3 className="mt-1 text-base font-semibold text-slate-950">
                                        {tr('Test connection', 'اختبار الاتصال')}
                                    </h3>
                                    <p className="mt-1 text-xs leading-5 text-slate-500">
                                        {tr(
                                            'Tests the saved configuration only. Save or discard edits first.',
                                            'يختبر الإعداد المحفوظ فقط. احفظ التعديلات أو تجاهلها أولًا.',
                                        )}
                                    </p>
                                </div>
                                <TestConnectionButton
                                    running={state.runningTest || testConnectionMutation.isPending}
                                    disabled={!state.selectedProviderCode || !state.configuration || state.dirty}
                                    onClick={handleTestConnection}
                                />
                            </div>
                        </section>
                        <ConnectionResultPanel result={state.latestResult} />
                        {import.meta.env.DEV ? (
                            <DevelopmentOnlinePaymentSimulationPanel
                                submitting={developmentSuccessMutation.isPending}
                                result={developmentSuccessMutation.data}
                                onSubmit={(paymentId, request) =>
                                    developmentSuccessMutation.mutate({
                                        paymentId,
                                        idempotencyKey: request.idempotencyKey,
                                        reason: request.reason,
                                    })
                                }
                            />
                        ) : null}
                        {state.selectedProviderCode?.trim().toUpperCase() === 'PAYMOB' ? (
                            <ProviderSessionReconciliationPanel
                                key={state.selectedProviderCode}
                                submitting={reconcileSessionMutation.isPending}
                                disabled={state.dirty}
                                onSubmit={(payload) => reconcileSessionMutation.mutate(payload)}
                            />
                        ) : null}
                        <SaveBar
                            saving={state.saving || saveMutation.isPending}
                            disabled={!state.selectedProviderCode || !state.configuration || state.validationErrors.length > 0}
                            dirty={state.dirty}
                            successMessage={state.successMessage}
                            onSave={handleSave}
                            onCancel={handleCancel}
                        />
                        <RotateSecretDialog
                            open={Boolean(rotatingSecretType)}
                            secretType={rotatingSecretType ?? ''}
                            rotating={rotateSecretMutation.isPending}
                            onClose={() => setRotatingSecretType(null)}
                            onRotate={handleRotateSecret}
                        />
                    </>
                }
            />

            {dashboardQuery.isError ? (
                <InlineNotice tone="amber" title={tr('Dashboard summary unavailable', 'ملخص اللوحة غير متاح')}>
                    {tr(
                        'Provider configuration is still available, but dashboard summaries could not be refreshed.',
                        'إعداد المزود ما زال متاحًا، لكن تعذّر تحديث ملخصات اللوحة.',
                    )}
                </InlineNotice>
            ) : null}

            {state.selectedProviderCode && selectedProviderQuery.isError ? (
                <InlineNotice tone="amber" title={tr('Provider details unavailable', 'تفاصيل المزود غير متاحة')}>
                    <div className="flex items-start gap-2">
                        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                        <span>
                            {getErrorMessage(
                                selectedProviderQuery.error,
                                tr(
                                    'Unable to load the selected provider configuration.',
                                    'تعذّر تحميل إعداد المزود المحدد.',
                                ),
                            )}
                        </span>
                    </div>
                </InlineNotice>
            ) : null}
        </SuperAdminPage>
    )
}
