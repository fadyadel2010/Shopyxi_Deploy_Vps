import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { useFormatting } from '../hooks/useFormatting'
import { useToast } from '../components/Toast'
import { useAuthStore } from '../store/authStore'

const emptyForm = {
    storeDisplayName: '',
    businessCategory: '',
    businessDescription: '',
    contactName: '',
    contactPhone: '',
    contactEmail: '',
    productCategories: '',
    storefrontUrl: '',
}

const toForm = (source = {}) => ({
    ...emptyForm,
    storeDisplayName: source.storeDisplayName || '',
    businessCategory: source.businessCategory || '',
    businessDescription: source.businessDescription || '',
    contactName: source.contactName || '',
    contactPhone: source.contactPhone || '',
    contactEmail: source.contactEmail || '',
    productCategories: Array.isArray(source.productCategories) ? source.productCategories.join(', ') : '',
    storefrontUrl: source.storefrontUrl || '',
})

export default function CommerceHubSellerApplication() {
    const { t } = useTranslation()
    const { isRTL, formatDateTime } = useFormatting()
    const { showSuccess, showError } = useToast({ surface: 'commercehub' })
    const updateUser = useAuthStore((state) => state.updateUser)
    const navigate = useNavigate()
    const [status, setStatus] = useState(null)
    const [form, setForm] = useState(emptyForm)
    const [loading, setLoading] = useState(true)
    const [submitting, setSubmitting] = useState(false)

    const load = useCallback(async () => {
        setLoading(true)
        try {
            const response = await api.get('/admin/commercehub/seller-application')
            const next = response.data
            setStatus(next)
            const editable = next.canResubmit ? next.currentApplication : next.prefill
            setForm(toForm(editable))
        } catch (error) {
            showError(error?.response?.data?.message || t('commerceHub.sellerApplication.loadFailed'))
        } finally {
            setLoading(false)
        }
    }, [showError, t])

    useEffect(() => { void load() }, [load])

    const update = (field, value) => setForm((current) => ({ ...current, [field]: value }))

    const submit = async (event) => {
        event.preventDefault()
        const productCategories = form.productCategories
            .split(/[,\n]/)
            .map((item) => item.trim())
            .filter(Boolean)

        if (productCategories.length === 0) {
            showError(t('commerceHub.sellerApplication.validation.categories'))
            return
        }

        setSubmitting(true)
        try {
            const response = await api.post('/admin/commercehub/seller-application', {
                ...form,
                productCategories,
                storefrontUrl: form.storefrontUrl.trim() || null,
            })
            setStatus(response.data)
            showSuccess(t('commerceHub.sellerApplication.submitted'))
        } catch (error) {
            showError(error?.response?.data?.message || t('commerceHub.sellerApplication.submitFailed'))
        } finally {
            setSubmitting(false)
        }
    }

    const openPublishing = async () => {
        try {
            const response = await api.get('/auth/session')
            updateUser(response.data?.user || response.data)
            navigate('/admin/commercehub')
        } catch {
            showError(t('commerceHub.sellerApplication.refreshSession'))
        }
    }

    if (loading) {
        return <div className="flex min-h-[320px] items-center justify-center" aria-label={t('common.loading')}><div className="h-10 w-10 animate-spin rounded-full border-2 border-slate-200 border-t-indigo-600" /></div>
    }

    if (!status) {
        return (
            <div className="rounded-2xl border border-red-200 bg-red-50 p-6 text-red-800">
                <h1 className="font-semibold">{t('commerceHub.sellerApplication.loadFailed')}</h1>
                <button type="button" onClick={load} className="mt-3 rounded-lg bg-red-700 px-4 py-2 text-sm font-semibold text-white">{t('common.retry', 'Retry')}</button>
            </div>
        )
    }

    const current = status.currentApplication
    const isPending = status.status === 'PendingReview'
    const isRejected = status.status === 'Rejected'
    const isApproved = status.status === 'Approved'
    const showForm = status.canSubmit || status.canResubmit
    const statusTone = isApproved
        ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
        : isRejected
            ? 'border-red-200 bg-red-50 text-red-900'
            : isPending
                ? 'border-amber-200 bg-amber-50 text-amber-900'
                : 'border-indigo-200 bg-indigo-50 text-indigo-900'

    return (
        <div className="mx-auto max-w-5xl space-y-5" dir={isRTL ? 'rtl' : 'ltr'}>
            <header className="flex flex-col gap-3 border-b border-slate-200 pb-5 sm:flex-row sm:items-start sm:justify-between">
                <div>
                    <p className="text-sm font-semibold text-indigo-700">CommerceHub Marketplace</p>
                    <h1 className="mt-1 text-2xl font-bold text-slate-950">{t('commerceHub.sellerApplication.title')}</h1>
                    <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-600">{t('commerceHub.sellerApplication.subtitle')}</p>
                </div>
                {isApproved && (
                    <button type="button" onClick={openPublishing} className="rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-700">
                        {t('commerceHub.sellerApplication.openPublishing')}
                    </button>
                )}
            </header>

            <section className={`rounded-2xl border p-5 ${statusTone}`} aria-live="polite">
                <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                        <p className="text-xs font-bold uppercase tracking-wider opacity-70">{t('commerceHub.sellerApplication.currentStatus')}</p>
                        <h2 className="mt-1 text-xl font-bold">{t(`commerceHub.sellerApplication.status.${status.status}`)}</h2>
                        <p className="mt-2 max-w-2xl text-sm leading-6">{t(`commerceHub.sellerApplication.statusHelp.${status.status}`)}</p>
                    </div>
                    {current?.referenceNumber && <span className="rounded-full border border-current/20 bg-white/60 px-3 py-1 text-xs font-semibold" dir="ltr">{current.referenceNumber}</span>}
                </div>
                {isRejected && current?.rejectionReason && (
                    <div className="mt-4 rounded-xl border border-red-200 bg-white p-4">
                        <p className="text-xs font-semibold uppercase tracking-wide text-red-600">{t('commerceHub.sellerApplication.rejectionReason')}</p>
                        <p className="mt-1 whitespace-pre-wrap text-sm text-red-950">{current.rejectionReason}</p>
                    </div>
                )}
                {current?.submittedAtUtc && (
                    <div className="mt-4 flex flex-wrap gap-x-6 gap-y-1 text-xs opacity-80">
                        <span>{t('commerceHub.sellerApplication.submittedAt')}: {formatDateTime(current.submittedAtUtc)}</span>
                        {current.reviewedAtUtc && <span>{t('commerceHub.sellerApplication.reviewedAt')}: {formatDateTime(current.reviewedAtUtc)}</span>}
                    </div>
                )}
            </section>

            {showForm && (
                <form onSubmit={submit} className="space-y-6 rounded-2xl border border-slate-200 bg-white p-5 shadow-sm sm:p-6">
                    <div>
                        <h2 className="text-lg font-bold text-slate-950">{status.canResubmit ? t('commerceHub.sellerApplication.correctAndResubmit') : t('commerceHub.sellerApplication.formTitle')}</h2>
                        <p className="mt-1 text-sm text-slate-500">{t('commerceHub.sellerApplication.formHelp')}</p>
                    </div>

                    <fieldset className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                        <legend className="col-span-full mb-1 text-sm font-bold text-slate-800">{t('commerceHub.sellerApplication.businessSection')}</legend>
                        <Field label={t('commerceHub.sellerApplication.fields.storeName')} value={form.storeDisplayName} onChange={(value) => update('storeDisplayName', value)} maxLength={160} />
                        <Field label={t('commerceHub.sellerApplication.fields.businessCategory')} value={form.businessCategory} onChange={(value) => update('businessCategory', value)} maxLength={120} />
                        <label className="sm:col-span-2">
                            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('commerceHub.sellerApplication.fields.description')}</span>
                            <textarea required minLength={20} maxLength={1000} rows={4} value={form.businessDescription} onChange={(event) => update('businessDescription', event.target.value)} className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100" />
                        </label>
                        <label className="sm:col-span-2">
                            <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('commerceHub.sellerApplication.fields.productCategories')}</span>
                            <input required value={form.productCategories} onChange={(event) => update('productCategories', event.target.value)} placeholder={t('commerceHub.sellerApplication.fields.productCategoriesPlaceholder')} className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100" />
                        </label>
                    </fieldset>

                    <fieldset className="grid grid-cols-1 gap-4 border-t border-slate-100 pt-5 sm:grid-cols-2">
                        <legend className="col-span-full mb-1 text-sm font-bold text-slate-800">{t('commerceHub.sellerApplication.contactSection')}</legend>
                        <Field label={t('commerceHub.sellerApplication.fields.contactName')} value={form.contactName} onChange={(value) => update('contactName', value)} maxLength={160} />
                        <Field label={t('commerceHub.sellerApplication.fields.phone')} value={form.contactPhone} onChange={(value) => update('contactPhone', value)} maxLength={40} />
                        <Field type="email" label={t('commerceHub.sellerApplication.fields.email')} value={form.contactEmail} onChange={(value) => update('contactEmail', value)} maxLength={320} />
                        <Field type="url" required={false} label={t('commerceHub.sellerApplication.fields.storefrontUrl')} value={form.storefrontUrl} onChange={(value) => update('storefrontUrl', value)} maxLength={500} />
                    </fieldset>

                    {status.existingVerificationRequired && (
                        <p className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">{t('commerceHub.sellerApplication.verificationNote')}</p>
                    )}

                    <div className="flex flex-wrap items-center justify-end gap-3 border-t border-slate-100 pt-5">
                        <button type="submit" disabled={submitting} className="rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-60">
                            {submitting ? t('common.saving') : status.canResubmit ? t('commerceHub.sellerApplication.resubmit') : t('commerceHub.sellerApplication.submit')}
                        </button>
                    </div>
                </form>
            )}

            {current && !showForm && (
                <section className="rounded-2xl border border-slate-200 bg-white p-5">
                    <h2 className="text-base font-bold text-slate-950">{t('commerceHub.sellerApplication.submittedDetails')}</h2>
                    <dl className="mt-4 grid grid-cols-1 gap-4 text-sm sm:grid-cols-2">
                        <Detail label={t('commerceHub.sellerApplication.fields.storeName')} value={current.storeDisplayName} />
                        <Detail label={t('commerceHub.sellerApplication.fields.businessCategory')} value={current.businessCategory} />
                        <Detail label={t('commerceHub.sellerApplication.fields.contactName')} value={current.contactName} />
                        <Detail label={t('commerceHub.sellerApplication.fields.email')} value={current.contactEmail} />
                        <Detail label={t('commerceHub.sellerApplication.fields.productCategories')} value={current.productCategories?.join(', ')} wide />
                        <Detail label={t('commerceHub.sellerApplication.fields.description')} value={current.businessDescription} wide />
                    </dl>
                </section>
            )}

            {status.history?.length > 1 && (
                <section className="rounded-2xl border border-slate-200 bg-white p-5">
                    <h2 className="text-base font-bold text-slate-950">{t('commerceHub.sellerApplication.history')}</h2>
                    <div className="mt-3 divide-y divide-slate-100">
                        {status.history.map((item) => (
                            <div key={item.id} className="flex flex-wrap items-start justify-between gap-3 py-3 text-sm">
                                <div>
                                    <p className="font-semibold text-slate-900">{t('commerceHub.sellerApplication.revision', { revision: item.revision })} · {t(`commerceHub.sellerApplication.status.${item.status}`)}</p>
                                    {item.rejectionReason && <p className="mt-1 max-w-2xl text-red-700">{item.rejectionReason}</p>}
                                </div>
                                <span className="text-xs text-slate-500">{formatDateTime(item.submittedAtUtc)}</span>
                            </div>
                        ))}
                    </div>
                </section>
            )}

            {!isApproved && <p className="text-center text-xs text-slate-500"><Link className="font-semibold text-indigo-700 hover:underline" to="/commercehub/finance">{t('commerceHub.sellerApplication.financeUnaffected')}</Link></p>}
        </div>
    )
}

function Field({ label, value, onChange, type = 'text', required = true, maxLength }) {
    return (
        <label>
            <span className="mb-1.5 block text-sm font-medium text-slate-700">{label}</span>
            <input type={type} required={required} maxLength={maxLength} value={value} onChange={(event) => onChange(event.target.value)} className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100" />
        </label>
    )
}

function Detail({ label, value, wide = false }) {
    return (
        <div className={wide ? 'sm:col-span-2' : ''}>
            <dt className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</dt>
            <dd className="mt-1 whitespace-pre-wrap text-slate-900">{value || '—'}</dd>
        </div>
    )
}
