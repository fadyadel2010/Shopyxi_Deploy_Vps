import { useCallback, useEffect, useState } from 'react'
import { Check, ChevronRight, Clock3, RefreshCw, Search, Store, X } from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    sellerApplicationsService,
    type SellerApplicationDetails,
    type SellerApplicationListItem,
} from './sellerApplicationsService'

const filters = ['Pending', 'Approved', 'Rejected', 'All'] as const

export default function SellerApplicationsPage() {
    const { tr, formatDateTime, isArabic } = useSuperAdminLocale()
    const [status, setStatus] = useState<(typeof filters)[number]>('Pending')
    const [search, setSearch] = useState('')
    const [items, setItems] = useState<SellerApplicationListItem[]>([])
    const [details, setDetails] = useState<SellerApplicationDetails | null>(null)
    const [loading, setLoading] = useState(true)
    const [detailLoading, setDetailLoading] = useState(false)
    const [saving, setSaving] = useState(false)
    const [rejectionReason, setRejectionReason] = useState('')
    const [error, setError] = useState('')
    const [notice, setNotice] = useState('')

    const load = useCallback(async () => {
        setLoading(true)
        setError('')
        try {
            const result = await sellerApplicationsService.search(status, search)
            setItems(result.applications)
        } catch (loadError) {
            setError(loadError instanceof Error ? loadError.message : tr('Applications could not be loaded.', 'تعذر تحميل الطلبات.'))
        } finally {
            setLoading(false)
        }
    }, [search, status, tr])

    useEffect(() => { void load() }, [load])

    const open = async (applicationId: string) => {
        setDetailLoading(true)
        setError('')
        setNotice('')
        try {
            setDetails(await sellerApplicationsService.get(applicationId))
            setRejectionReason('')
        } catch (loadError) {
            setError(loadError instanceof Error ? loadError.message : tr('Application details could not be loaded.', 'تعذر تحميل تفاصيل الطلب.'))
        } finally {
            setDetailLoading(false)
        }
    }

    const review = async (approve: boolean) => {
        if (!details || saving) return
        const reason = rejectionReason.trim()
        if (!approve && reason.length < 10) {
            setError(tr('Enter a meaningful rejection reason of at least 10 characters.', 'أدخل سبب رفض واضحاً لا يقل عن 10 أحرف.'))
            return
        }

        setSaving(true)
        setError('')
        setNotice('')
        try {
            const next = approve
                ? await sellerApplicationsService.approve(details.application.id)
                : await sellerApplicationsService.reject(details.application.id, reason)
            setDetails(next)
            setNotice(approve
                ? tr('Seller application approved. Publishing authority is now active.', 'تم قبول طلب البائع وتفعيل صلاحية النشر.')
                : tr('Seller application rejected. The merchant can see the reason and resubmit.', 'تم رفض طلب البائع ويمكن للتاجر رؤية السبب وإعادة التقديم.'))
            await load()
        } catch (reviewError) {
            setError(reviewError instanceof Error ? reviewError.message : tr('The review decision could not be saved.', 'تعذر حفظ قرار المراجعة.'))
        } finally {
            setSaving(false)
        }
    }

    const statusLabel = (value: string) => ({
        Submitted: tr('Pending', 'قيد المراجعة'),
        PendingReview: tr('Pending', 'قيد المراجعة'),
        Approved: tr('Approved', 'مقبول'),
        Rejected: tr('Rejected', 'مرفوض'),
    }[value] || value)

    return (
        <div className="space-y-5" dir={isArabic ? 'rtl' : 'ltr'}>
            <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                    <p className="text-xs font-bold uppercase tracking-[0.18em] text-emerald-700">CommerceHub</p>
                    <h1 className="mt-1 text-2xl font-bold text-slate-950">{tr('Seller applications', 'طلبات الانضمام كبائع')}</h1>
                    <p className="mt-1 max-w-2xl text-sm text-slate-600">{tr('Review merchant business details and make an authoritative approval or rejection decision.', 'راجع بيانات نشاط التاجر واتخذ قرار قبول أو رفض موثوقاً.')}</p>
                </div>
                <button type="button" onClick={() => void load()} className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm font-semibold text-slate-700 shadow-sm hover:bg-slate-50">
                    <RefreshCw className="h-4 w-4" /> {tr('Refresh', 'تحديث')}
                </button>
            </header>

            {error && <div role="alert" className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">{error}</div>}
            {notice && <div role="status" className="rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900">{notice}</div>}

            <div className="grid min-h-[540px] grid-cols-1 gap-5 xl:grid-cols-[minmax(0,1.05fr)_minmax(420px,0.95fr)]">
                <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
                    <div className="space-y-3 border-b border-slate-200 p-4">
                        <div className="flex flex-wrap gap-2">
                            {filters.map((filter) => (
                                <button key={filter} type="button" onClick={() => setStatus(filter)} className={`rounded-full px-3 py-1.5 text-xs font-bold ${status === filter ? 'bg-slate-950 text-white' : 'bg-slate-100 text-slate-600 hover:bg-slate-200'}`}>
                                    {filter === 'All' ? tr('All', 'الكل') : statusLabel(filter === 'Pending' ? 'Submitted' : filter)}
                                </button>
                            ))}
                        </div>
                        <label className="relative block">
                            <Search className="pointer-events-none absolute start-3 top-2.5 h-4 w-4 text-slate-400" />
                            <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder={tr('Search business, reference, or category', 'ابحث بالنشاط أو المرجع أو الفئة')} className="w-full rounded-xl border border-slate-300 py-2 ps-9 pe-3 text-sm focus:border-emerald-500 focus:ring-2 focus:ring-emerald-100" />
                        </label>
                    </div>

                    <div className="divide-y divide-slate-100">
                        {loading ? <ListSkeleton /> : items.length === 0 ? (
                            <div className="px-5 py-16 text-center">
                                <Store className="mx-auto h-8 w-8 text-slate-300" />
                                <p className="mt-3 font-semibold text-slate-700">{tr('No applications match this view.', 'لا توجد طلبات تطابق هذا العرض.')}</p>
                            </div>
                        ) : items.map((item) => (
                            <button key={item.id} type="button" onClick={() => void open(item.id)} className={`flex w-full items-center gap-3 px-4 py-4 text-start hover:bg-slate-50 ${details?.application.id === item.id ? 'bg-emerald-50/70' : ''}`}>
                                <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-slate-100 text-slate-600"><Store className="h-4 w-4" /></span>
                                <span className="min-w-0 flex-1">
                                    <span className="block truncate font-semibold text-slate-950">{item.merchantName}</span>
                                    <span className="mt-0.5 block truncate text-xs text-slate-500" dir="auto">{item.businessCategory} · {item.referenceNumber}</span>
                                    <span className="mt-1 block text-xs text-slate-500">{formatDateTime(item.submittedAtUtc)}</span>
                                </span>
                                <span className={`rounded-full px-2.5 py-1 text-[11px] font-bold ${item.status === 'Approved' ? 'bg-emerald-100 text-emerald-800' : item.status === 'Rejected' ? 'bg-red-100 text-red-800' : 'bg-amber-100 text-amber-800'}`}>{statusLabel(item.status)}</span>
                                <ChevronRight className="h-4 w-4 text-slate-400 rtl:rotate-180" />
                            </button>
                        ))}
                    </div>
                </section>

                <section className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
                    {detailLoading ? <DetailSkeleton /> : details ? (
                        <ApplicationDetail
                            details={details}
                            rejectionReason={rejectionReason}
                            setRejectionReason={setRejectionReason}
                            saving={saving}
                            onApprove={() => void review(true)}
                            onReject={() => void review(false)}
                            statusLabel={statusLabel}
                            formatDateTime={formatDateTime}
                            tr={tr}
                        />
                    ) : (
                        <div className="flex min-h-[420px] flex-col items-center justify-center text-center text-slate-500">
                            <Clock3 className="h-9 w-9 text-slate-300" />
                            <p className="mt-3 font-semibold">{tr('Select an application to review.', 'اختر طلباً لمراجعته.')}</p>
                        </div>
                    )}
                </section>
            </div>
        </div>
    )
}

function ApplicationDetail({ details, rejectionReason, setRejectionReason, saving, onApprove, onReject, statusLabel, formatDateTime, tr }: any) {
    const application = details.application
    const pending = application.status === 'PendingReview'
    return (
        <div className="space-y-5">
            <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-100 pb-4">
                <div>
                    <p className="text-xs font-bold uppercase tracking-wide text-slate-500" dir="ltr">{application.referenceNumber}</p>
                    <h2 className="mt-1 text-xl font-bold text-slate-950">{application.storeDisplayName}</h2>
                    <p className="mt-1 text-sm text-slate-500">{tr('Tenant', 'المتجر')}: {details.tenantSlug || application.tenantId}</p>
                </div>
                <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-bold text-slate-700">{statusLabel(application.status)}</span>
            </div>

            <div className="grid grid-cols-2 gap-3 text-sm">
                <Fact label={tr('Business category', 'فئة النشاط')} value={application.businessCategory} />
                <Fact label={tr('Tenant status', 'حالة المتجر')} value={details.tenantStatus} />
                <Fact label={tr('Contact person', 'مسؤول التواصل')} value={application.contactName} />
                <Fact label={tr('Phone', 'الهاتف')} value={application.contactPhone} />
                <Fact label={tr('Email', 'البريد الإلكتروني')} value={application.contactEmail} />
                <Fact label={tr('Submitted', 'تاريخ التقديم')} value={formatDateTime(application.submittedAtUtc)} />
                <Fact label={tr('Existing verification', 'التحقق الحالي')} value={details.isKycVerified ? tr('Complete', 'مكتمل') : tr('Required before approval', 'مطلوب قبل القبول')} />
                <Fact label={tr('Publishing approval', 'اعتماد النشر')} value={details.isMarketplaceApproved ? tr('Active', 'مفعّل') : tr('Not active', 'غير مفعّل')} />
            </div>

            <div className="rounded-xl bg-slate-50 p-4 text-sm">
                <p className="text-xs font-bold uppercase tracking-wide text-slate-500">{tr('Business description', 'وصف النشاط')}</p>
                <p className="mt-2 whitespace-pre-wrap leading-6 text-slate-800">{application.businessDescription}</p>
                <p className="mt-4 text-xs font-bold uppercase tracking-wide text-slate-500">{tr('Requested product categories', 'فئات المنتجات المطلوبة')}</p>
                <div className="mt-2 flex flex-wrap gap-2">{application.productCategories.map((category: string) => <span key={category} className="rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs text-slate-700">{category}</span>)}</div>
            </div>

            {application.rejectionReason && <div className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-900"><strong>{tr('Rejection reason:', 'سبب الرفض:')}</strong> {application.rejectionReason}</div>}

            {pending && (
                <div className="space-y-3 border-t border-slate-200 pt-5">
                    <label className="block">
                        <span className="mb-1.5 block text-sm font-semibold text-slate-700">{tr('Rejection reason', 'سبب الرفض')}</span>
                        <textarea value={rejectionReason} onChange={(event) => setRejectionReason(event.target.value)} maxLength={1000} rows={3} placeholder={tr('Required only when rejecting; explain what the merchant should correct.', 'مطلوب عند الرفض فقط؛ وضّح ما الذي يجب على التاجر تصحيحه.')} className="w-full rounded-xl border border-slate-300 px-3 py-2 text-sm focus:border-red-500 focus:ring-2 focus:ring-red-100" />
                    </label>
                    <div className="flex flex-wrap justify-end gap-2">
                        <button type="button" disabled={saving} onClick={onReject} className="inline-flex items-center gap-2 rounded-lg border border-red-300 bg-white px-4 py-2 text-sm font-bold text-red-700 hover:bg-red-50 disabled:opacity-50"><X className="h-4 w-4" />{tr('Reject', 'رفض')}</button>
                        <button type="button" disabled={saving || !details.isKycVerified} onClick={onApprove} title={!details.isKycVerified ? tr('Existing merchant verification must be complete first.', 'يجب اكتمال التحقق الحالي من التاجر أولاً.') : undefined} className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-50"><Check className="h-4 w-4" />{saving ? tr('Saving…', 'جارٍ الحفظ…') : tr('Approve seller', 'قبول البائع')}</button>
                    </div>
                </div>
            )}

            {details.history.length > 1 && (
                <div className="border-t border-slate-100 pt-4">
                    <h3 className="text-sm font-bold text-slate-900">{tr('Submission history', 'سجل التقديم')}</h3>
                    <div className="mt-2 space-y-2">{details.history.map((item: any) => <div key={item.id} className="flex justify-between gap-3 rounded-lg bg-slate-50 px-3 py-2 text-xs"><span>{tr('Revision', 'المراجعة')} {item.revision} · {statusLabel(item.status)}</span><span className="text-slate-500">{formatDateTime(item.submittedAtUtc)}</span></div>)}</div>
                </div>
            )}
        </div>
    )
}

function Fact({ label, value }: { label: string; value: string }) {
    return <div className="min-w-0 rounded-lg border border-slate-100 p-3"><p className="text-[11px] font-bold uppercase tracking-wide text-slate-500">{label}</p><p className="mt-1 break-words font-medium text-slate-900" dir="auto">{value || '—'}</p></div>
}

function ListSkeleton() { return <div className="space-y-3 p-4">{[1, 2, 3, 4].map((item) => <div key={item} className="h-16 animate-pulse rounded-xl bg-slate-100" />)}</div> }
function DetailSkeleton() { return <div className="space-y-3">{[1, 2, 3, 4].map((item) => <div key={item} className="h-16 animate-pulse rounded-xl bg-slate-100" />)}</div> }
