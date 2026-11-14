import { RefreshCw, Search } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { saInput } from '../../ui/SuperAdminUI'
import type {
    PaymentProviderCapability,
    PaymentProviderDashboardProvider,
    ProviderDashboardFiltersState,
} from '../paymentProviderTypes'

export function DashboardFilters({
    providers,
    capabilities,
    filters,
    refreshing,
    onChange,
    onRefresh,
}: {
    providers: PaymentProviderDashboardProvider[]
    capabilities: PaymentProviderCapability[]
    filters: ProviderDashboardFiltersState
    refreshing: boolean
    onChange: (filters: ProviderDashboardFiltersState) => void
    onRefresh: () => void
}) {
    const { tr } = useSuperAdminLocale()
    const environments = unique(providers.map((provider) => provider.environment))
    const statuses = unique(providers.map((provider) => provider.status))
    const healthStatuses = unique(providers.map((provider) => provider.health.status))

    function update(next: Partial<ProviderDashboardFiltersState>) {
        onChange({ ...filters, ...next })
    }

    return (
        <section className="rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-5">
                <label className="relative sm:col-span-2 lg:col-span-2">
                    <Search className="pointer-events-none absolute start-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                        aria-label={tr('Search providers', 'البحث في المزودين')}
                        value={filters.search}
                        onChange={(event) => update({ search: event.target.value })}
                        className={`${saInput} ps-9`}
                        placeholder={tr('Search providers', 'البحث في المزودين')}
                    />
                </label>
                <select
                    aria-label={tr('Provider filter', 'تصفية المزود')}
                    value={filters.provider}
                    onChange={(event) => update({ provider: event.target.value })}
                    className={saInput}
                >
                    <option value="">{tr('All providers', 'كل المزودين')}</option>
                    {providers.map((provider) => (
                        <option key={provider.providerCode} value={provider.providerCode}>
                            {provider.displayName}
                        </option>
                    ))}
                </select>
                <select
                    aria-label={tr('Provider environment filter', 'تصفية بيئة المزود')}
                    value={filters.environment}
                    onChange={(event) => update({ environment: event.target.value })}
                    className={saInput}
                >
                    <option value="">{tr('All environments', 'كل البيئات')}</option>
                    {environments.map((environment) => (
                        <option key={environment} value={environment}>
                            {environment}
                        </option>
                    ))}
                </select>
                <select
                    aria-label={tr('Provider status filter', 'تصفية حالة المزود')}
                    value={filters.status}
                    onChange={(event) => update({ status: event.target.value })}
                    className={saInput}
                >
                    <option value="">{tr('All statuses', 'كل الحالات')}</option>
                    {statuses.map((status) => (
                        <option key={status} value={status}>
                            {status}
                        </option>
                    ))}
                </select>
                <select
                    aria-label={tr('Provider health filter', 'تصفية صحة المزود')}
                    value={filters.health}
                    onChange={(event) => update({ health: event.target.value })}
                    className={saInput}
                >
                    <option value="">{tr('All health states', 'كل حالات الصحة')}</option>
                    {healthStatuses.map((status) => (
                        <option key={status} value={status}>
                            {status}
                        </option>
                    ))}
                </select>
                <select
                    aria-label={tr('Provider enabled filter', 'تصفية تفعيل المزود')}
                    value={filters.enabled}
                    onChange={(event) =>
                        update({ enabled: event.target.value as ProviderDashboardFiltersState['enabled'] })
                    }
                    className={saInput}
                >
                    <option value="all">{tr('All enablement states', 'كل حالات التفعيل')}</option>
                    <option value="enabled">{tr('Enabled only', 'المفعّلة فقط')}</option>
                    <option value="disabled">{tr('Disabled only', 'المعطّلة فقط')}</option>
                </select>
                <select
                    aria-label={tr('Provider capability filter', 'تصفية قدرة المزود')}
                    value={filters.capability}
                    onChange={(event) => update({ capability: event.target.value })}
                    className={saInput}
                >
                    <option value="">{tr('All capabilities', 'كل القدرات')}</option>
                    {capabilities.map((capability) => (
                        <option key={capability.code} value={capability.code}>
                            {capability.displayName}
                        </option>
                    ))}
                </select>
                <select
                    aria-label={tr('Provider sort order', 'ترتيب المزودين')}
                    value={filters.sort}
                    onChange={(event) => update({ sort: event.target.value as ProviderDashboardFiltersState['sort'] })}
                    className={saInput}
                >
                    <option value="provider">{tr('Sort: provider', 'الترتيب: المزود')}</option>
                    <option value="status">{tr('Sort: status', 'الترتيب: الحالة')}</option>
                    <option value="environment">{tr('Sort: environment', 'الترتيب: البيئة')}</option>
                    <option value="health">{tr('Sort: health', 'الترتيب: الصحة')}</option>
                    <option value="lastCheck">{tr('Sort: last check', 'الترتيب: آخر فحص')}</option>
                </select>
                <button
                    type="button"
                    onClick={onRefresh}
                    className="inline-flex h-10 items-center justify-center gap-2 rounded-xl bg-slate-950 px-4 text-sm font-semibold text-white transition hover:bg-slate-800"
                >
                    <RefreshCw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />
                    {tr('Refresh', 'تحديث')}
                </button>
            </div>
        </section>
    )
}

function unique(values: string[]) {
    return Array.from(new Set(values.filter(Boolean))).sort((left, right) => left.localeCompare(right))
}
