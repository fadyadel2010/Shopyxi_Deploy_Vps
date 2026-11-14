import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
    CheckCircle2,
    Clock3,
    Filter,
    RefreshCw,
    Search,
    ShieldCheck,
    Star,
    UserRound,
    X,
    XCircle,
} from 'lucide-react'
import { type ReactNode, useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    EmptyState,
    ErrorState,
    GuidePanel,
    InlineNotice,
    KpiCard,
    LoadingState,
    PageHeader,
    StatusBadge,
    SuperAdminPage,
    saButton,
    saInput,
    saTable,
} from '../ui/SuperAdminUI'
import {
    approveSuperAdminReview,
    getSuperAdminReviewDetails,
    getSuperAdminReviews,
    rejectSuperAdminReview,
    SUPER_ADMIN_REVIEW_STATUS_OPTIONS,
    type SuperAdminReviewDetails,
    type SuperAdminReviewListItem,
    type SuperAdminReviewsFilters,
    type SuperAdminReviewStatus,
} from './reviewsService'

const DEFAULT_PAGE = 1
const DEFAULT_PAGE_SIZE = 25
const pageSizeOptions = [25, 50, 100]

function parsePositiveInt(value: string | null, fallback: number): number {
    const parsed = Number.parseInt(value ?? '', 10)
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback
}

function parseStatus(value: string | null): SuperAdminReviewStatus | '' {
    return SUPER_ADMIN_REVIEW_STATUS_OPTIONS.includes(value as SuperAdminReviewStatus)
        ? (value as SuperAdminReviewStatus)
        : ''
}

function parseRating(value: string | null): number | '' {
    const parsed = Number.parseInt(value ?? '', 10)
    return Number.isFinite(parsed) && parsed >= 1 && parsed <= 5 ? parsed : ''
}

function buildFilters(searchParams: URLSearchParams): SuperAdminReviewsFilters {
    return {
        page: parsePositiveInt(searchParams.get('page'), DEFAULT_PAGE),
        pageSize: parsePositiveInt(searchParams.get('pageSize'), DEFAULT_PAGE_SIZE),
        status: parseStatus(searchParams.get('status')),
        rating: parseRating(searchParams.get('rating')),
        productId: searchParams.get('productId') ?? '',
    }
}

function getErrorMessage(error: unknown, fallback: string): string {
    if (error instanceof Error && error.message) {
        return error.message
    }

    return fallback
}

const STATUS_LABELS: Record<string, string> = {
    Pending: 'معلّق',
    Approved: 'معتمد',
    Rejected: 'مرفوض',
}

function ReviewActionButton({
    tone,
    disabled,
    onClick,
    children,
}: {
    tone: 'approve' | 'reject'
    disabled?: boolean
    onClick: () => void
    children: ReactNode
}) {
    return (
        <button
            type="button"
            disabled={disabled}
            onClick={onClick}
            className={[
                'inline-flex items-center justify-center gap-1 rounded-lg px-2.5 py-1.5 text-xs font-semibold transition disabled:cursor-not-allowed disabled:opacity-55',
                tone === 'approve'
                    ? 'bg-emerald-600 text-white hover:bg-emerald-700'
                    : 'bg-rose-600 text-white hover:bg-rose-700',
            ].join(' ')}
        >
            {children}
        </button>
    )
}

function ReviewsTable({
    reviews,
    isLoading,
    onSelectReview,
    onApprove,
    onReject,
    actionDisabled,
}: {
    reviews: SuperAdminReviewListItem[]
    isLoading: boolean
    onSelectReview: (reviewId: string) => void
    onApprove: (reviewId: string) => void
    onReject: (reviewId: string) => void
    actionDisabled: boolean
}) {
    const { tr, formatDateTime } = useSuperAdminLocale()

    if (isLoading) {
        return <LoadingState label={tr('Loading review queue…', 'جارٍ تحميل قائمة المراجعات…')} />
    }

    if (reviews.length === 0) {
        return (
            <EmptyState
                title={tr('No reviews match these filters', 'لا توجد مراجعات تطابق هذه المرشحات')}
                description={tr(
                    'Try widening the moderation queue filters to bring more marketplace reviews into view.',
                    'جرّب توسيع مرشحات قائمة المراجعة لعرض مزيد من مراجعات السوق.',
                )}
            />
        )
    }

    return (
        <div className={saTable.shell}>
            <table className={saTable.table}>
                <thead className={saTable.head}>
                    <tr>
                        <th className={saTable.headerCell}>{tr('Product', 'المنتج')}</th>
                        <th className={saTable.headerCell}>{tr('User', 'المستخدم')}</th>
                        <th className={saTable.headerCell}>{tr('Rating', 'التقييم')}</th>
                        <th className={saTable.headerCell}>{tr('Status', 'الحالة')}</th>
                        <th className={saTable.headerCell}>{tr('Created', 'تاريخ الإنشاء')}</th>
                        <th className={`${saTable.headerCell} text-end`}>{tr('Actions', 'إجراءات')}</th>
                    </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                    {reviews.map((review) => (
                        <tr key={review.id} className="align-top">
                            <td className={saTable.cell}>
                                <button
                                    type="button"
                                    className="space-y-0.5 text-start"
                                    onClick={() => onSelectReview(review.id)}
                                >
                                    <p className="font-semibold text-slate-950">{review.productName}</p>
                                    <p className="break-all text-xs text-slate-500" dir="ltr">
                                        {review.productId}
                                    </p>
                                </button>
                            </td>
                            <td className={saTable.cell}>
                                <div className="space-y-0.5">
                                    <p className="font-semibold text-slate-950">{review.customerName}</p>
                                    <p className="text-sm text-slate-500" dir="ltr">
                                        {review.customerEmail}
                                    </p>
                                </div>
                            </td>
                            <td className={saTable.cell}>
                                <span className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2.5 py-1 text-sm font-semibold text-amber-900">
                                    <Star className="h-3.5 w-3.5 fill-current" />
                                    {review.rating}.0
                                </span>
                            </td>
                            <td className={saTable.cell}>
                                <StatusBadge
                                    tone={
                                        review.status === 'Approved'
                                            ? 'emerald'
                                            : review.status === 'Rejected'
                                              ? 'rose'
                                              : 'amber'
                                    }
                                >
                                    {tr(review.status, STATUS_LABELS[review.status] ?? review.status)}
                                </StatusBadge>
                            </td>
                            <td className={`${saTable.cell} text-sm text-slate-600`}>
                                {formatDateTime(review.createdAtUtc, tr('Awaiting sync', 'بانتظار المزامنة'))}
                            </td>
                            <td className={saTable.cell}>
                                <div className="flex flex-wrap justify-end gap-1.5">
                                    <button
                                        type="button"
                                        className={`${saButton.secondary} px-2.5 py-1.5 text-xs`}
                                        onClick={() => onSelectReview(review.id)}
                                    >
                                        {tr('Details', 'التفاصيل')}
                                    </button>
                                    <ReviewActionButton
                                        tone="approve"
                                        disabled={actionDisabled}
                                        onClick={() => onApprove(review.id)}
                                    >
                                        {tr('Approve', 'اعتماد')}
                                    </ReviewActionButton>
                                    <ReviewActionButton
                                        tone="reject"
                                        disabled={actionDisabled}
                                        onClick={() => onReject(review.id)}
                                    >
                                        {tr('Reject', 'رفض')}
                                    </ReviewActionButton>
                                </div>
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    )
}

function ReviewDetailsDrawer({
    isOpen,
    details,
    isLoading,
    isError,
    errorMessage,
    moderationNote,
    onModerationNoteChange,
    onClose,
    onRetry,
    onApprove,
    onReject,
    isMutating,
}: {
    isOpen: boolean
    details?: SuperAdminReviewDetails
    isLoading: boolean
    isError: boolean
    errorMessage?: string | null
    moderationNote: string
    onModerationNoteChange: (value: string) => void
    onClose: () => void
    onRetry: () => void
    onApprove: () => void
    onReject: () => void
    isMutating: boolean
}) {
    const { tr, formatNumber, formatDateTime } = useSuperAdminLocale()

    if (!isOpen) {
        return null
    }

    return (
        <div className="fixed inset-0 z-50">
            <button
                type="button"
                className="absolute inset-0 bg-slate-950/45 backdrop-blur-sm"
                aria-label={tr('Close review details', 'إغلاق تفاصيل المراجعة')}
                onClick={onClose}
            />

            <aside className="absolute inset-y-0 end-0 flex w-full max-w-3xl flex-col border-s border-white/20 bg-white shadow-2xl shadow-slate-950/40">
                <div className="flex items-start justify-between gap-3 border-b border-slate-200 px-4 py-4 sm:px-5">
                    <div>
                        <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                            {tr('Review details', 'تفاصيل المراجعة')}
                        </p>
                        <h2 className="mt-1 text-xl font-semibold text-slate-950">
                            {tr('Moderation workspace', 'مساحة المراجعة')}
                        </h2>
                    </div>

                    <button
                        type="button"
                        aria-label={tr('Close review details', 'إغلاق تفاصيل المراجعة')}
                        className="flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-700"
                        onClick={onClose}
                    >
                        <X className="h-4 w-4" />
                    </button>
                </div>

                <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-5">
                    {isLoading ? (
                        <LoadingState label={tr('Loading review details…', 'جارٍ تحميل تفاصيل المراجعة…')} />
                    ) : isError ? (
                        <ErrorState
                            title={tr('Unable to load review details', 'تعذّر تحميل تفاصيل المراجعة')}
                            description={errorMessage ?? undefined}
                            onRetry={onRetry}
                        />
                    ) : details ? (
                        <div className="space-y-4">
                            <section className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-4">
                                <div className="flex flex-wrap items-start justify-between gap-4">
                                    <div className="min-w-0">
                                        <StatusBadge
                                            tone={
                                                details.status === 'Approved'
                                                    ? 'emerald'
                                                    : details.status === 'Rejected'
                                                      ? 'rose'
                                                      : 'amber'
                                            }
                                        >
                                            {tr(details.status, STATUS_LABELS[details.status] ?? details.status)}
                                        </StatusBadge>
                                        <h3 className="mt-2 text-xl font-semibold text-slate-950">
                                            {details.productName}
                                        </h3>
                                        <p className="mt-1 break-all text-sm text-slate-500" dir="ltr">
                                            {tr('Product ID:', 'معرّف المنتج:')} {details.productId}
                                        </p>
                                    </div>

                                    <div className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-center text-amber-950">
                                        <div className="flex items-center justify-center gap-1.5">
                                            <Star className="h-4 w-4 fill-current" />
                                            <span className="text-2xl font-semibold">{details.rating}.0</span>
                                        </div>
                                        <p className="mt-1 text-[10px] font-semibold uppercase tracking-[0.14em] text-amber-800">
                                            {tr('Customer rating', 'تقييم العميل')}
                                        </p>
                                    </div>
                                </div>
                            </section>

                            <section className="grid gap-3 lg:grid-cols-2">
                                <div className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                                    <div className="flex items-center gap-3">
                                        <div className="rounded-lg bg-slate-100 p-2 text-slate-700">
                                            <UserRound className="h-4 w-4" />
                                        </div>
                                        <div className="min-w-0">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                                {tr('Customer', 'العميل')}
                                            </p>
                                            <p className="mt-0.5 text-base font-semibold text-slate-950">
                                                {details.customerName}
                                            </p>
                                            <p className="text-sm text-slate-500" dir="ltr">
                                                {details.customerEmail}
                                            </p>
                                        </div>
                                    </div>

                                    <div className="mt-3 flex flex-wrap gap-1.5">
                                        {details.isVerifiedPurchase ? (
                                            <StatusBadge tone="emerald">
                                                <ShieldCheck className="h-3.5 w-3.5" />
                                                {tr('Verified purchase', 'شراء موثّق')}
                                            </StatusBadge>
                                        ) : null}
                                        <StatusBadge tone="slate">
                                            <Clock3 className="h-3.5 w-3.5" />
                                            {tr('Created', 'أُنشئ')}{' '}
                                            {formatDateTime(details.createdAtUtc, tr('Awaiting sync', 'بانتظار المزامنة'))}
                                        </StatusBadge>
                                    </div>
                                </div>

                                <div className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                        {tr('Customer history', 'سجل العميل')}
                                    </p>
                                    <div className="mt-3 grid grid-cols-2 gap-2">
                                        <div className="rounded-lg bg-slate-50 px-3 py-2.5">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-slate-500">
                                                {tr('Total', 'الإجمالي')}
                                            </p>
                                            <p className="mt-1 text-xl font-semibold text-slate-950">
                                                {formatNumber(details.userHistory.totalReviews)}
                                            </p>
                                        </div>
                                        <div className="rounded-lg bg-emerald-50 px-3 py-2.5">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-emerald-700">
                                                {tr('Approved', 'معتمد')}
                                            </p>
                                            <p className="mt-1 text-xl font-semibold text-emerald-950">
                                                {formatNumber(details.userHistory.approvedReviews)}
                                            </p>
                                        </div>
                                        <div className="rounded-lg bg-amber-50 px-3 py-2.5">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-amber-700">
                                                {tr('Pending', 'معلّق')}
                                            </p>
                                            <p className="mt-1 text-xl font-semibold text-amber-950">
                                                {formatNumber(details.userHistory.pendingReviews)}
                                            </p>
                                        </div>
                                        <div className="rounded-lg bg-rose-50 px-3 py-2.5">
                                            <p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-rose-700">
                                                {tr('Rejected', 'مرفوض')}
                                            </p>
                                            <p className="mt-1 text-xl font-semibold text-rose-950">
                                                {formatNumber(details.userHistory.rejectedReviews)}
                                            </p>
                                        </div>
                                    </div>
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                    {tr('Review content', 'محتوى المراجعة')}
                                </p>
                                {details.title ? (
                                    <h4 className="mt-2 text-lg font-semibold text-slate-950">{details.title}</h4>
                                ) : null}
                                <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">
                                    {details.comment}
                                </p>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                    {tr('Moderation note', 'ملاحظة المراجعة')}
                                </p>
                                <textarea
                                    value={moderationNote}
                                    onChange={(event) => onModerationNoteChange(event.target.value)}
                                    rows={3}
                                    placeholder={tr(
                                        'Optional reason saved into the moderation audit trail.',
                                        'سبب اختياري يُحفظ في سجل تدقيق المراجعة.',
                                    )}
                                    className={`${saInput} mt-2`}
                                />

                                <div className="mt-3 flex flex-wrap gap-2">
                                    <button
                                        type="button"
                                        className={saButton.success}
                                        disabled={isMutating}
                                        onClick={onApprove}
                                    >
                                        <CheckCircle2 className="h-4 w-4" />
                                        {tr('Approve review', 'اعتماد المراجعة')}
                                    </button>
                                    <button
                                        type="button"
                                        className={saButton.danger}
                                        disabled={isMutating}
                                        onClick={onReject}
                                    >
                                        <XCircle className="h-4 w-4" />
                                        {tr('Reject review', 'رفض المراجعة')}
                                    </button>
                                </div>
                            </section>

                            <section className="rounded-xl border border-slate-200 bg-white px-4 py-4 shadow-sm">
                                <p className="text-[10px] font-semibold uppercase tracking-[0.14em] text-slate-500">
                                    {tr('Recent review history', 'سجل المراجعات الأخير')}
                                </p>
                                <div className="mt-3 space-y-2">
                                    {details.userHistory.recentReviews.length === 0 ? (
                                        <p className="rounded-lg bg-slate-50 px-3 py-3 text-sm text-slate-500">
                                            {tr(
                                                'No historical reviews found for this customer yet.',
                                                'لا توجد مراجعات سابقة لهذا العميل بعد.',
                                            )}
                                        </p>
                                    ) : (
                                        details.userHistory.recentReviews.map((review) => (
                                            <div
                                                key={review.reviewId}
                                                className="flex flex-col gap-2 rounded-lg border border-slate-200 px-3 py-2.5 sm:flex-row sm:items-center sm:justify-between"
                                            >
                                                <div>
                                                    <p className="font-semibold text-slate-950">{review.productName}</p>
                                                    <p className="mt-0.5 text-sm text-slate-500">
                                                        {formatDateTime(
                                                            review.createdAtUtc,
                                                            tr('Awaiting sync', 'بانتظار المزامنة'),
                                                        )}
                                                    </p>
                                                </div>
                                                <div className="flex flex-wrap items-center gap-1.5">
                                                    <span className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2.5 py-1 text-sm font-semibold text-amber-900">
                                                        <Star className="h-3.5 w-3.5 fill-current" />
                                                        {review.rating}.0
                                                    </span>
                                                    <StatusBadge
                                                        tone={
                                                            review.status === 'Approved'
                                                                ? 'emerald'
                                                                : review.status === 'Rejected'
                                                                  ? 'rose'
                                                                  : 'amber'
                                                        }
                                                    >
                                                        {tr(review.status, STATUS_LABELS[review.status] ?? review.status)}
                                                    </StatusBadge>
                                                </div>
                                            </div>
                                        ))
                                    )}
                                </div>
                            </section>
                        </div>
                    ) : null}
                </div>
            </aside>
        </div>
    )
}

export default function ReviewModerationPage() {
    const [searchParams, setSearchParams] = useSearchParams()
    const queryClient = useQueryClient()
    const filters = useMemo(() => buildFilters(searchParams), [searchParams])
    const [selectedReviewId, setSelectedReviewId] = useState<string | null>(null)
    const [productIdInput, setProductIdInput] = useState(filters.productId ?? '')
    const [moderationNote, setModerationNote] = useState('')
    const { tr, formatNumber } = useSuperAdminLocale()

    useEffect(() => {
        setProductIdInput(filters.productId ?? '')
    }, [filters.productId])

    useEffect(() => {
        setModerationNote('')
    }, [selectedReviewId])

    const updateParams = (updates: Record<string, string | null | undefined>) => {
        setSearchParams((previous) => {
            const next = new URLSearchParams(previous)

            Object.entries(updates).forEach(([key, value]) => {
                if (!value) {
                    next.delete(key)
                    return
                }

                next.set(key, value)
            })

            return next
        })
    }

    const reviewsQuery = useQuery({
        queryKey: ['super-admin', 'reviews', filters],
        queryFn: () => getSuperAdminReviews(filters),
        placeholderData: (previous) => previous,
    })

    const detailsQuery = useQuery({
        queryKey: ['super-admin', 'reviews', 'details', selectedReviewId],
        queryFn: () => getSuperAdminReviewDetails(selectedReviewId as string),
        enabled: Boolean(selectedReviewId),
    })

    const approveMutation = useMutation({
        mutationFn: async (reviewId: string) => approveSuperAdminReview(reviewId, moderationNote),
        onSuccess: async (result) => {
            queryClient.setQueryData(['super-admin', 'reviews', 'details', result.id], result)
            await queryClient.invalidateQueries({ queryKey: ['super-admin', 'reviews'] })
        },
    })

    const rejectMutation = useMutation({
        mutationFn: async (reviewId: string) => rejectSuperAdminReview(reviewId, moderationNote),
        onSuccess: async (result) => {
            queryClient.setQueryData(['super-admin', 'reviews', 'details', result.id], result)
            await queryClient.invalidateQueries({ queryKey: ['super-admin', 'reviews'] })
        },
    })

    const reviews = reviewsQuery.data?.items ?? []
    const pendingCount = reviews.filter((review) => review.status === 'Pending').length
    const approvedCount = reviews.filter((review) => review.status === 'Approved').length
    const rejectedCount = reviews.filter((review) => review.status === 'Rejected').length
    const verifiedCount = reviews.filter((review) => review.isVerifiedPurchase).length
    const isMutating = approveMutation.isPending || rejectMutation.isPending

    const guide = {
        en: {
            title: 'Review moderation guide',
            purpose:
                'Keeps the public marketplace review signal trustworthy by approving, rejecting, and auditing customer reviews before they become visible.',
            when:
                'Use it when new reviews await moderation, when abuse or fake reviews are reported, or when a customer history suggests coordinated or incentivized feedback.',
            steps: [
                'Filter the queue by status, rating, or an exact product ID.',
                'Open a review to inspect its full text and the customer’s review history.',
                'Decide whether the review follows marketplace content policy.',
                'Approve or reject, optionally adding a moderation note for the audit trail.',
                'Refresh and confirm the review left the pending queue.',
            ],
            statuses: [
                { label: 'Pending', description: 'Not visible publicly and awaiting a moderation decision.' },
                { label: 'Approved', description: 'Published to the product page and counted in public ratings.' },
                { label: 'Rejected', description: 'Excluded from public display; the decision stays auditable.' },
                { label: 'Verified purchase', description: 'The reviewer has a matching order for this product.' },
            ],
            safety: [
                'Only approved reviews are public. Rejecting removes visibility but does not delete the record.',
                'Read the full review and customer history before rejecting; avoid acting on a single signal.',
                'Moderation notes are part of the audit trail. Never include secrets or unrelated personal data.',
                'This screen does not edit ratings, order data, or product information.',
            ],
        },
        ar: {
            title: 'دليل مراجعة التقييمات',
            purpose:
                'يحافظ على موثوقية تقييمات السوق العامة عبر اعتماد أو رفض وتدقيق تقييمات العملاء قبل ظهورها.',
            when:
                'استخدمه عند وجود تقييمات بانتظار المراجعة، أو الإبلاغ عن إساءة أو تقييمات وهمية، أو عندما يشير سجل العميل إلى تقييمات منسّقة أو محفّزة.',
            steps: [
                'رشّح القائمة حسب الحالة أو التقييم أو معرّف منتج محدد.',
                'افتح التقييم لفحص نصه الكامل وسجل تقييمات العميل.',
                'قرّر ما إذا كان التقييم يلتزم بسياسة محتوى السوق.',
                'اعتمد أو ارفض مع إضافة ملاحظة مراجعة اختيارية لسجل التدقيق.',
                'حدّث الصفحة وتأكد من خروج التقييم من قائمة الانتظار.',
            ],
            statuses: [
                { label: 'معلّق', description: 'غير ظاهر للعامة وبانتظار قرار المراجعة.' },
                { label: 'معتمد', description: 'منشور في صفحة المنتج ويُحتسب في التقييم العام.' },
                { label: 'مرفوض', description: 'مستبعد من العرض العام مع بقاء القرار قابلًا للتدقيق.' },
                { label: 'شراء موثّق', description: 'لدى صاحب التقييم طلب مطابق لهذا المنتج.' },
            ],
            safety: [
                'التقييمات المعتمدة فقط هي الظاهرة للعامة. الرفض يزيل الظهور ولا يحذف السجل.',
                'اقرأ التقييم كاملًا وسجل العميل قبل الرفض؛ لا تتصرف بناءً على إشارة واحدة.',
                'ملاحظات المراجعة جزء من سجل التدقيق. لا تضمّنها أسرارًا أو بيانات شخصية غير ذات صلة.',
                'هذه الشاشة لا تعدّل التقييمات أو بيانات الطلبات أو معلومات المنتج.',
            ],
        },
    }

    if (reviewsQuery.isError && !reviewsQuery.data) {
        return (
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Trust', 'الإدارة العليا · الثقة')}
                    title={tr('Review moderation', 'مراجعة التقييمات')}
                    description={tr(
                        'Approve, reject, and audit marketplace reviews.',
                        'اعتماد ورفض وتدقيق تقييمات السوق.',
                    )}
                />
                <InlineNotice tone="rose" title={tr('Review moderation unavailable', 'مراجعة التقييمات غير متاحة')}>
                    <p>
                        {getErrorMessage(
                            reviewsQuery.error,
                            tr(
                                'Unable to load the marketplace review queue.',
                                'تعذّر تحميل قائمة مراجعات السوق.',
                            ),
                        )}
                    </p>
                    <button
                        type="button"
                        className={`${saButton.secondary} mt-2`}
                        onClick={() => void reviewsQuery.refetch()}
                    >
                        <RefreshCw className="h-4 w-4" />
                        {tr('Retry request', 'إعادة المحاولة')}
                    </button>
                </InlineNotice>
            </SuperAdminPage>
        )
    }

    return (
        <>
            <SuperAdminPage>
                <PageHeader
                    eyebrow={tr('Super Admin · Trust', 'الإدارة العليا · الثقة')}
                    title={tr('Review moderation', 'مراجعة التقييمات')}
                    description={tr(
                        'Keep public ratings trustworthy by reviewing content and customer history before approval.',
                        'حافظ على موثوقية التقييمات العامة بمراجعة المحتوى وسجل العميل قبل الاعتماد.',
                    )}
                    actions={
                        <button
                            type="button"
                            className={saButton.primary}
                            disabled={reviewsQuery.isFetching}
                            onClick={() => void reviewsQuery.refetch()}
                        >
                            <RefreshCw className={`h-4 w-4 ${reviewsQuery.isFetching ? 'animate-spin' : ''}`} />
                            {reviewsQuery.isFetching ? tr('Refreshing', 'جارٍ التحديث') : tr('Refresh queue', 'تحديث القائمة')}
                        </button>
                    }
                />

                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                    <KpiCard
                        label={tr('Pending on page', 'معلّق في الصفحة')}
                        value={formatNumber(pendingCount)}
                        icon={<Clock3 className="h-4 w-4 text-amber-700" />}
                        tone="amber"
                    />
                    <KpiCard
                        label={tr('Approved on page', 'معتمد في الصفحة')}
                        value={formatNumber(approvedCount)}
                        icon={<CheckCircle2 className="h-4 w-4 text-emerald-700" />}
                        tone="emerald"
                    />
                    <KpiCard
                        label={tr('Rejected on page', 'مرفوض في الصفحة')}
                        value={formatNumber(rejectedCount)}
                        icon={<XCircle className="h-4 w-4 text-rose-700" />}
                        tone="rose"
                    />
                    <KpiCard
                        label={tr('Verified purchases', 'عمليات شراء موثّقة')}
                        value={formatNumber(verifiedCount)}
                        icon={<ShieldCheck className="h-4 w-4 text-sky-700" />}
                        tone="sky"
                    />
                </div>

                <div className="grid gap-3 sm:grid-cols-3">
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Current page', 'الصفحة الحالية')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {tr(
                                `Page ${reviewsQuery.data?.page ?? filters.page} of ${Math.max(
                                    1,
                                    reviewsQuery.data?.totalPages ?? 1,
                                )}`,
                                `صفحة ${reviewsQuery.data?.page ?? filters.page} من ${Math.max(
                                    1,
                                    reviewsQuery.data?.totalPages ?? 1,
                                )}`,
                            )}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Matching reviews', 'مراجعات مطابقة')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {formatNumber(reviewsQuery.data?.totalCount ?? 0)}
                        </p>
                    </div>
                    <div className="rounded-xl border border-slate-200 bg-white px-3.5 py-3 shadow-sm">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Product filter mode', 'وضع مرشح المنتج')}
                        </p>
                        <p className="mt-1 text-sm font-semibold text-slate-900" dir="auto">
                            {filters.productId ? tr('Exact product ID', 'معرّف منتج محدد') : tr('All products', 'كل المنتجات')}
                        </p>
                    </div>
                </div>

                <GuidePanel guide={guide} />

                <section className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm sm:p-4">
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                        <div>
                            <h2 className="text-sm font-semibold text-slate-950">{tr('Queue filters', 'مرشحات القائمة')}</h2>
                            <p className="mt-0.5 text-xs leading-5 text-slate-500">
                                {tr(
                                    'Narrow the moderation queue by status, rating, or an exact product ID.',
                                    'ضيّق قائمة المراجعة حسب الحالة أو التقييم أو معرّف منتج محدد.',
                                )}
                            </p>
                        </div>

                        <button
                            type="button"
                            className={saButton.secondary}
                            onClick={() => {
                                setProductIdInput('')
                                setSearchParams({
                                    page: String(DEFAULT_PAGE),
                                    pageSize: String(DEFAULT_PAGE_SIZE),
                                })
                            }}
                        >
                            <Filter className="h-4 w-4" />
                            {tr('Reset filters', 'إعادة ضبط المرشحات')}
                        </button>
                    </div>

                    <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                        <label className="space-y-1.5 text-sm">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Status', 'الحالة')}
                            </span>
                            <select
                                value={filters.status}
                                onChange={(event) =>
                                    updateParams({
                                        status: event.target.value || null,
                                        page: String(DEFAULT_PAGE),
                                    })
                                }
                                className={saInput}
                            >
                                <option value="">{tr('All statuses', 'كل الحالات')}</option>
                                {SUPER_ADMIN_REVIEW_STATUS_OPTIONS.map((status) => (
                                    <option key={status} value={status}>
                                        {tr(status, STATUS_LABELS[status] ?? status)}
                                    </option>
                                ))}
                            </select>
                        </label>

                        <label className="space-y-1.5 text-sm">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Rating', 'التقييم')}
                            </span>
                            <select
                                value={filters.rating}
                                onChange={(event) =>
                                    updateParams({
                                        rating: event.target.value || null,
                                        page: String(DEFAULT_PAGE),
                                    })
                                }
                                className={saInput}
                            >
                                <option value="">{tr('All ratings', 'كل التقييمات')}</option>
                                {[5, 4, 3, 2, 1].map((rating) => (
                                    <option key={rating} value={rating}>
                                        {tr(`${rating} stars`, `${rating} نجوم`)}
                                    </option>
                                ))}
                            </select>
                        </label>

                        <label className="space-y-1.5 text-sm sm:col-span-2">
                            <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr('Product ID', 'معرّف المنتج')}
                            </span>
                            <div className="flex gap-2">
                                <div className="relative flex-1">
                                    <Search className="pointer-events-none absolute start-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                    <input
                                        value={productIdInput}
                                        onChange={(event) => setProductIdInput(event.target.value)}
                                        placeholder={tr(
                                            'Paste a marketplace product GUID to filter exactly',
                                            'ألصق معرّف منتج السوق للتصفية الدقيقة',
                                        )}
                                        className={`${saInput} ps-10`}
                                        dir="ltr"
                                    />
                                </div>
                                <button
                                    type="button"
                                    className={saButton.primary}
                                    onClick={() =>
                                        updateParams({
                                            productId: productIdInput.trim() || null,
                                            page: String(DEFAULT_PAGE),
                                        })
                                    }
                                >
                                    {tr('Apply', 'تطبيق')}
                                </button>
                            </div>
                        </label>
                    </div>
                </section>

                {reviewsQuery.isError && reviewsQuery.data ? (
                    <InlineNotice tone="amber" title={tr('Cached queue shown', 'تُعرض القائمة المخزنة')}>
                        {tr(
                            'Showing the most recent cached moderation queue while the latest refresh is being retried.',
                            'يُعرض أحدث قائمة مراجعة مخزنة أثناء إعادة محاولة التحديث.',
                        )}
                    </InlineNotice>
                ) : null}

                {approveMutation.isError || rejectMutation.isError ? (
                    <InlineNotice tone="rose" title={tr('Action failed', 'فشل الإجراء')}>
                        {getErrorMessage(
                            approveMutation.error ?? rejectMutation.error,
                            tr(
                                'Unable to update the review status right now.',
                                'تعذّر تحديث حالة المراجعة حاليًا.',
                            ),
                        )}
                    </InlineNotice>
                ) : null}

                <ReviewsTable
                    reviews={reviews}
                    isLoading={reviewsQuery.isLoading && !reviewsQuery.data}
                    onSelectReview={setSelectedReviewId}
                    onApprove={(reviewId) => approveMutation.mutate(reviewId)}
                    onReject={(reviewId) => rejectMutation.mutate(reviewId)}
                    actionDisabled={isMutating}
                />

                <section className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white px-4 py-4 shadow-sm lg:flex-row lg:items-center lg:justify-between">
                    <div>
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Pagination', 'ترقيم الصفحات')}
                        </p>
                        <p className="mt-0.5 text-sm leading-6 text-slate-700" dir="auto">
                            {tr(
                                `Page ${filters.page} of ${reviewsQuery.data?.totalPages || 1} with ${formatNumber(
                                    reviewsQuery.data?.totalCount || 0,
                                )} matching marketplace reviews.`,
                                `صفحة ${filters.page} من ${reviewsQuery.data?.totalPages || 1} مع ${formatNumber(
                                    reviewsQuery.data?.totalCount || 0,
                                )} مراجعة سوق مطابقة.`,
                            )}
                        </p>
                    </div>

                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
                        <label className="flex items-center gap-2 text-sm text-slate-600">
                            <span>{tr('Page size', 'حجم الصفحة')}</span>
                            <select
                                aria-label={tr('Reviews per page', 'المراجعات لكل صفحة')}
                                value={filters.pageSize}
                                onChange={(event) =>
                                    updateParams({
                                        pageSize: event.target.value,
                                        page: String(DEFAULT_PAGE),
                                    })
                                }
                                className={`${saInput} w-auto py-1.5`}
                            >
                                {pageSizeOptions.map((size) => (
                                    <option key={size} value={size}>
                                        {size}
                                    </option>
                                ))}
                            </select>
                        </label>

                        <div className="flex items-center gap-2">
                            <button
                                type="button"
                                className={saButton.secondary}
                                disabled={filters.page <= 1}
                                onClick={() => updateParams({ page: String(Math.max(1, filters.page - 1)) })}
                            >
                                {tr('Previous', 'السابق')}
                            </button>
                            <button
                                type="button"
                                className={saButton.primary}
                                disabled={filters.page >= Math.max(1, reviewsQuery.data?.totalPages ?? 0)}
                                onClick={() => updateParams({ page: String(filters.page + 1) })}
                            >
                                {tr('Next', 'التالي')}
                            </button>
                        </div>
                    </div>
                </section>
            </SuperAdminPage>

            <ReviewDetailsDrawer
                isOpen={Boolean(selectedReviewId)}
                details={detailsQuery.data}
                isLoading={detailsQuery.isLoading}
                isError={detailsQuery.isError}
                errorMessage={
                    detailsQuery.isError
                        ? getErrorMessage(
                              detailsQuery.error,
                              tr('Unable to load review details.', 'تعذّر تحميل تفاصيل المراجعة.'),
                          )
                        : null
                }
                moderationNote={moderationNote}
                onModerationNoteChange={setModerationNote}
                onClose={() => setSelectedReviewId(null)}
                onRetry={() => void detailsQuery.refetch()}
                onApprove={() => {
                    if (selectedReviewId) {
                        approveMutation.mutate(selectedReviewId)
                    }
                }}
                onReject={() => {
                    if (selectedReviewId) {
                        rejectMutation.mutate(selectedReviewId)
                    }
                }}
                isMutating={isMutating}
            />
        </>
    )
}
