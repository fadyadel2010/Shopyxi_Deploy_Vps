import type { ReactNode } from 'react'
import {
    AlertTriangle,
    BookOpen,
    CheckCircle2,
    ChevronDown,
    Info,
    ListChecks,
    Loader2,
    RefreshCw,
    ShieldAlert,
} from 'lucide-react'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import type { BilingualGuide } from '../i18n/useSuperAdminLocale'

export const saButton = {
    primary:
        'inline-flex items-center justify-center gap-2 rounded-xl bg-slate-950 px-4 py-2.5 text-sm font-semibold text-white shadow-sm transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-55',
    secondary:
        'inline-flex items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 shadow-sm transition hover:border-slate-300 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-55',
    danger:
        'inline-flex items-center justify-center gap-2 rounded-xl bg-rose-600 px-4 py-2.5 text-sm font-semibold text-white shadow-sm transition hover:bg-rose-700 disabled:cursor-not-allowed disabled:opacity-55',
    success:
        'inline-flex items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-semibold text-white shadow-sm transition hover:bg-emerald-700 disabled:cursor-not-allowed disabled:opacity-55',
    ghost:
        'inline-flex items-center justify-center gap-2 rounded-xl px-3 py-2 text-sm font-semibold text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-55',
}

export const saInput =
    'w-full rounded-xl border border-slate-200 bg-white px-3.5 py-2.5 text-sm text-slate-900 shadow-sm outline-none transition placeholder:text-slate-400 focus:border-emerald-400 focus:ring-4 focus:ring-emerald-100 disabled:cursor-not-allowed disabled:bg-slate-50 disabled:text-slate-500'

export const saTable = {
    shell: 'overflow-x-auto rounded-xl border border-slate-200',
    table: 'min-w-full divide-y divide-slate-200 text-sm',
    head: 'bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-500',
    row: 'divide-y divide-slate-100 bg-white transition hover:bg-slate-50/70',
    cell: 'px-4 py-3 align-middle',
    headerCell: 'px-4 py-3 text-start font-semibold',
}

type Tone = 'slate' | 'emerald' | 'amber' | 'rose' | 'sky' | 'violet'

const toneClasses: Record<Tone, string> = {
    slate: 'border-slate-200 bg-slate-50 text-slate-700',
    emerald: 'border-emerald-200 bg-emerald-50 text-emerald-800',
    amber: 'border-amber-200 bg-amber-50 text-amber-900',
    rose: 'border-rose-200 bg-rose-50 text-rose-900',
    sky: 'border-sky-200 bg-sky-50 text-sky-900',
    violet: 'border-violet-200 bg-violet-50 text-violet-900',
}

export function StatusBadge({ children, tone = 'slate' }: { children: ReactNode; tone?: Tone }) {
    return (
        <span className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-semibold ${toneClasses[tone]}`}>
            {children}
        </span>
    )
}

export function SuperAdminPage({ children }: { children: ReactNode }) {
    return <div className="min-w-0 space-y-4">{children}</div>
}

export function PageHeader({
    eyebrow,
    title,
    description,
    actions,
    aside,
}: {
    eyebrow: string
    title: string
    description: string
    actions?: ReactNode
    aside?: ReactNode
}) {
    return (
        <section className="rounded-2xl border border-slate-200 bg-white px-5 py-4 shadow-sm sm:px-6">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                <div className="min-w-0 max-w-3xl">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-emerald-700">{eyebrow}</p>
                    <h1 className="mt-1.5 text-2xl font-semibold tracking-tight text-slate-950">{title}</h1>
                    <p className="mt-1.5 text-sm leading-6 text-slate-600">{description}</p>
                </div>
                <div className="flex shrink-0 flex-col items-stretch gap-2 sm:flex-row sm:items-center lg:justify-end">
                    {actions}
                    {aside}
                </div>
            </div>
        </section>
    )
}

function GuideSection({
    icon,
    title,
    children,
}: {
    icon: ReactNode
    title: string
    children: ReactNode
}) {
    return (
        <div className="rounded-xl border border-slate-200 bg-slate-50/60 p-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                <span className="text-emerald-700">{icon}</span>
                {title}
            </div>
            <div className="mt-2 text-sm leading-6 text-slate-600">{children}</div>
        </div>
    )
}

export function GuidePanel({ guide }: { guide: BilingualGuide }) {
    const { isArabic, tr } = useSuperAdminLocale()
    const content = isArabic ? guide.ar : guide.en

    return (
        <details className="group rounded-2xl border border-sky-200/80 bg-sky-50/50 shadow-sm">
            <summary className="flex cursor-pointer list-none items-center justify-between gap-3 px-4 py-3 text-sm font-semibold text-sky-950 outline-none transition hover:bg-sky-100/60 focus-visible:ring-4 focus-visible:ring-sky-200 sm:px-5">
                <span className="flex items-center gap-2.5">
                    <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-white text-sky-700 shadow-sm">
                        <BookOpen className="h-4 w-4" />
                    </span>
                    <span>
                        {content.title}
                        <span className="block text-xs font-medium text-sky-800/70">
                            {tr('Purpose, steps, statuses, and safety notes', 'الهدف والخطوات والحالات وملاحظات الأمان')}
                        </span>
                    </span>
                </span>
                <ChevronDown className="h-4 w-4 shrink-0 transition group-open:rotate-180" />
            </summary>
            <div className="grid gap-3 border-t border-sky-200/70 px-4 py-4 sm:px-5 lg:grid-cols-2">
                <GuideSection icon={<Info className="h-4 w-4" />} title={tr('Purpose', 'الهدف')}>
                    {content.purpose}
                </GuideSection>
                <GuideSection icon={<BookOpen className="h-4 w-4" />} title={tr('When to use it', 'متى تستخدمه')}>
                    {content.when}
                </GuideSection>
                <GuideSection icon={<ListChecks className="h-4 w-4" />} title={tr('How it works', 'كيف يعمل')}>
                    <ol className="list-decimal space-y-1.5 ps-5">
                        {content.steps.map((step) => (
                            <li key={step}>{step}</li>
                        ))}
                    </ol>
                </GuideSection>
                <div className="space-y-3">
                    {content.statuses?.length ? (
                        <GuideSection icon={<CheckCircle2 className="h-4 w-4" />} title={tr('Statuses and values', 'الحالات والقيم')}>
                            <dl className="space-y-2">
                                {content.statuses.map((status) => (
                                    <div key={status.label} className="flex flex-col gap-0.5 sm:flex-row sm:gap-2">
                                        <dt className="font-semibold text-slate-800">{status.label}</dt>
                                        <dd className="sm:flex-1">{status.description}</dd>
                                    </div>
                                ))}
                            </dl>
                        </GuideSection>
                    ) : null}
                    {content.safety?.length ? (
                        <GuideSection icon={<ShieldAlert className="h-4 w-4" />} title={tr('Safety notes', 'ملاحظات الأمان')}>
                            <ul className="list-disc space-y-1.5 ps-5">
                                {content.safety.map((note) => (
                                    <li key={note}>{note}</li>
                                ))}
                            </ul>
                        </GuideSection>
                    ) : null}
                </div>
            </div>
        </details>
    )
}

export function KpiCard({
    label,
    value,
    hint,
    icon,
    tone = 'slate',
    isLoading = false,
}: {
    label: string
    value: ReactNode
    hint?: string
    icon?: ReactNode
    tone?: Tone
    isLoading?: boolean
}) {
    return (
        <article className={`rounded-2xl border p-4 shadow-sm ${toneClasses[tone]}`}>
            <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                    <p className="text-xs font-semibold uppercase tracking-wide opacity-75">{label}</p>
                    {isLoading ? (
                        <div className="mt-2 h-7 w-24 animate-pulse rounded-lg bg-white/70" />
                    ) : (
                        <p className="mt-1.5 truncate text-2xl font-semibold tracking-tight" dir="auto">{value}</p>
                    )}
                    {hint ? <p className="mt-1 text-xs leading-5 opacity-80">{hint}</p> : null}
                </div>
                {icon ? <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-white/80 shadow-sm">{icon}</span> : null}
            </div>
        </article>
    )
}

export function SectionCard({
    title,
    description,
    actions,
    children,
    className = '',
    bodyClassName = '',
    headerAside,
}: {
    title: string
    description?: string
    actions?: ReactNode
    children: ReactNode
    className?: string
    bodyClassName?: string
    headerAside?: ReactNode
}) {
    return (
        <section className={`overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm ${className}`}>
            <div className="flex flex-col gap-3 border-b border-slate-100 px-4 py-4 sm:flex-row sm:items-start sm:justify-between sm:px-5">
                <div className="min-w-0">
                    <h2 className="text-base font-semibold text-slate-950">{title}</h2>
                    {description ? <p className="mt-1 text-sm leading-6 text-slate-500">{description}</p> : null}
                </div>
                {actions || headerAside ? (
                    <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}{headerAside}</div>
                ) : null}
            </div>
            <div className={bodyClassName || 'p-4 sm:p-5'}>{children}</div>
        </section>
    )
}

export function LoadingState({ label }: { label: string }) {
    return (
        <div className="flex min-h-32 items-center justify-center gap-3 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-sm font-medium text-slate-600">
            <Loader2 className="h-4 w-4 animate-spin text-emerald-600" />
            {label}
        </div>
    )
}

export function EmptyState({
    title,
    description,
    action,
}: {
    title: string
    description?: string
    action?: ReactNode
}) {
    return (
        <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50/70 px-5 py-8 text-center">
            <CheckCircle2 className="mx-auto h-7 w-7 text-slate-400" />
            <p className="mt-2 text-sm font-semibold text-slate-800">{title}</p>
            {description ? <p className="mx-auto mt-1 max-w-lg text-sm leading-6 text-slate-500">{description}</p> : null}
            {action ? <div className="mt-3">{action}</div> : null}
        </div>
    )
}

export function ErrorState({
    title,
    description,
    onRetry,
    retryLabel,
}: {
    title: string
    description?: string
    onRetry?: () => void
    retryLabel?: string
}) {
    const { tr } = useSuperAdminLocale()
    return (
        <div className="rounded-xl border border-rose-200 bg-rose-50 px-5 py-6 text-center">
            <AlertTriangle className="mx-auto h-7 w-7 text-rose-600" />
            <p className="mt-2 text-sm font-semibold text-rose-950">{title}</p>
            {description ? <p className="mx-auto mt-1 max-w-xl text-sm leading-6 text-rose-900/80">{description}</p> : null}
            {onRetry ? (
                <button type="button" className={`${saButton.secondary} mt-3`} onClick={onRetry}>
                    <RefreshCw className="h-4 w-4" />
                    {retryLabel ?? tr('Retry', 'إعادة المحاولة')}
                </button>
            ) : null}
        </div>
    )
}

export function InlineNotice({
    tone = 'amber',
    title,
    children,
}: {
    tone?: Tone
    title?: string
    children: ReactNode
}) {
    return (
        <div
            className={`flex items-start gap-3 rounded-xl border px-4 py-3 text-sm ${toneClasses[tone]}`}
            role={tone === 'rose' ? 'alert' : undefined}
        >
            <Info className="mt-0.5 h-4 w-4 shrink-0" />
            <div className="min-w-0 leading-6">
                {title ? <p className="font-semibold">{title}</p> : null}
                {children}
            </div>
        </div>
    )
}

export function PageToolbar({ children }: { children: ReactNode }) {
    return (
        <div className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-3 shadow-sm lg:flex-row lg:items-center lg:justify-between">
            {children}
        </div>
    )
}
