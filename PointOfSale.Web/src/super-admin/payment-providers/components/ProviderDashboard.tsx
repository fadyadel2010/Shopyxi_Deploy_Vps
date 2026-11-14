import { useMemo, useState } from 'react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { EmptyState, InlineNotice, LoadingState, SectionCard } from '../../ui/SuperAdminUI'
import type {
    PaymentProviderDashboard as PaymentProviderDashboardModel,
    PaymentProviderSecretMetadata,
    ProviderDashboardFiltersState,
} from '../paymentProviderTypes'
import { defaultProviderDashboardFilters, filterDashboardProviders } from '../providerDashboardFilters'
import { AdministrationTimeline } from './AdministrationTimeline'
import { CapabilityGrid } from './CapabilityGrid'
import { DashboardFilters } from './DashboardFilters'
import { ProviderHealthCard } from './ProviderHealthCard'
import { ProviderSummaryCard } from './ProviderSummaryCard'
import { SecretCard } from './SecretCard'

export function ProviderDashboard({
    dashboard,
    loading,
    refreshing,
    onRefresh,
}: {
    dashboard?: PaymentProviderDashboardModel
    loading: boolean
    refreshing: boolean
    onRefresh: () => void
}) {
    const { tr, formatNumber } = useSuperAdminLocale()
    const [filters, setFilters] = useState<ProviderDashboardFiltersState>(defaultProviderDashboardFilters)
    const [selectedProviderCode, setSelectedProviderCode] = useState<string | null>(null)
    const [expandedProviderCodes, setExpandedProviderCodes] = useState<Set<string>>(new Set())
    const providers = useMemo(() => dashboard?.providers ?? [], [dashboard?.providers])
    const filteredProviders = useMemo(
        () => filterDashboardProviders(providers, filters),
        [providers, filters],
    )
    const selectedProvider =
        providers.find((provider) => provider.providerCode === selectedProviderCode) ?? filteredProviders[0]
    const secretGroups =
        selectedProvider?.secrets.reduce<Record<string, PaymentProviderSecretMetadata[]>>((groups, secret) => {
            groups[secret.secretType] = [...(groups[secret.secretType] ?? []), secret]
            return groups
        }, {}) ?? {}

    function toggleProvider(providerCode: string) {
        setSelectedProviderCode(providerCode)
        setExpandedProviderCodes((current) => {
            const next = new Set(current)
            if (next.has(providerCode)) {
                next.delete(providerCode)
            } else {
                next.add(providerCode)
            }
            return next
        })
    }

    if (loading && !dashboard) {
        return (
            <SectionCard title={tr('Provider dashboard', 'لوحة المزودين')}>
                <LoadingState label={tr('Loading provider dashboard…', 'جارٍ تحميل لوحة المزودين…')} />
            </SectionCard>
        )
    }

    if (!dashboard) {
        return (
            <SectionCard title={tr('Provider dashboard', 'لوحة المزودين')}>
                <InlineNotice tone="amber" title={tr('Dashboard unavailable', 'اللوحة غير متاحة')}>
                    {tr(
                        'Provider operations data could not be loaded. Provider configuration below is unaffected.',
                        'تعذّر تحميل بيانات تشغيل المزودين. إعداد المزود أدناه غير متأثر.',
                    )}
                </InlineNotice>
            </SectionCard>
        )
    }

    return (
        <section className="space-y-3">
            <div className="flex flex-wrap items-center justify-between gap-2 rounded-2xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                        {tr('Provider operations', 'تشغيل المزودين')}
                    </p>
                    <h2 className="mt-1 text-base font-semibold text-slate-950">
                        {tr('Coverage and health', 'التغطية والصحة')}
                    </h2>
                </div>
                <span className="rounded-full border border-slate-200 bg-slate-50 px-2.5 py-1 text-xs font-semibold text-slate-600">
                    {tr(
                        `${formatNumber(filteredProviders.length)} of ${formatNumber(providers.length)} shown`,
                        `${formatNumber(filteredProviders.length)} من ${formatNumber(providers.length)} معروض`,
                    )}
                </span>
            </div>

            <DashboardFilters
                providers={providers}
                capabilities={dashboard.capabilities}
                filters={filters}
                refreshing={refreshing}
                onChange={setFilters}
                onRefresh={onRefresh}
            />

            <div className="space-y-3">
                {filteredProviders.length === 0 ? (
                    <EmptyState
                        title={tr('No providers match the current filters', 'لا يوجد مزودون يطابقون عوامل التصفية')}
                        description={tr(
                            'Clear the filters to see all registered providers again.',
                            'امسح عوامل التصفية لعرض كل المزودين المسجلين مجددًا.',
                        )}
                    />
                ) : (
                    filteredProviders.map((provider) => (
                        <ProviderSummaryCard
                            key={provider.providerCode}
                            provider={provider}
                            expanded={expandedProviderCodes.has(provider.providerCode)}
                            onToggle={() => toggleProvider(provider.providerCode)}
                        />
                    ))
                )}
            </div>

            {selectedProvider ? (
                <div className="grid grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1.2fr)_minmax(300px,0.8fr)] [&>*]:min-w-0">
                    <ProviderHealthCard health={selectedProvider.health} />
                    <section className="space-y-3">
                        <div className="rounded-2xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
                            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">
                                {tr('Credential versions', 'إصدارات الاعتماد')}
                            </p>
                            <h3 className="mt-1 text-base font-semibold text-slate-950">
                                {tr('Secret metadata for the selected provider', 'بيانات الأسرار للمزود المحدد')}
                            </h3>
                        </div>
                        {Object.entries(secretGroups).length === 0 ? (
                            <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 px-4 py-5 text-sm text-slate-600">
                                {tr('No secret metadata available.', 'لا توجد بيانات وصفية للأسرار.')}
                            </div>
                        ) : (
                            Object.entries(secretGroups).map(([secretType, versions]) => (
                                <SecretCard
                                    key={secretType}
                                    secretType={secretType}
                                    current={versions.find((secret) => secret.status === 'Active')}
                                    versions={versions}
                                    canRotate={false}
                                />
                            ))
                        )}
                    </section>
                </div>
            ) : null}

            <div className="grid grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1fr)_420px] [&>*]:min-w-0">
                <CapabilityGrid capabilities={dashboard.capabilities} />
                <AdministrationTimeline activity={dashboard.recentActivity} />
            </div>
        </section>
    )
}
