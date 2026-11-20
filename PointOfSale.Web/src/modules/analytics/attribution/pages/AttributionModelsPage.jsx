/**
 * Attribution Models Comparison Page
 * Phase 8A: Admin Attribution Dashboard UI
 * 
 * Route: /analytics/attribution/models
 * 
 * Compare First Touch vs Last Touch attribution:
 * - Side-by-side cards
 * - Revenue totals for each model
 * - Top 5 sources per model
 * 
 * API: GET /api/admin/analytics/attribution/models
 * 
 * PURE RENDERER - NO DIFF CALCULATIONS
 */

import { useState } from 'react'
import * as Dropdown from '@radix-ui/react-dropdown-menu'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import {
    GitCompare,
    ArrowLeft,
    RefreshCw,
    Calendar,
    ChevronDown
} from 'lucide-react'
import { useAttributionModels } from '../hooks/useAttribution'
import ModelComparisonCards from '../components/ModelComparisonCards'

const DATE_PERIODS = [
    { value: 'today', label: 'Today' },
    { value: 'yesterday', label: 'Yesterday' },
    { value: 'last_7_days', label: 'Last 7 Days' },
    { value: 'last_30_days', label: 'Last 30 Days' },
    { value: 'this_month', label: 'This Month' },
    { value: 'last_month', label: 'Last Month' }
]

export default function AttributionModelsPage() {
    const { t, i18n } = useTranslation()
    const isRTL = i18n.dir() === 'rtl'
    const navigate = useNavigate()

    const [period, setPeriod] = useState('last_7_days')
    const [showPeriodDropdown, setShowPeriodDropdown] = useState(false)

    const { data, loading, error, refetch } = useAttributionModels(period)

    const currentPeriod = DATE_PERIODS.find(p => p.value === period)
    const periodLabel = value => t(`attribution.periods.${({ last_7_days: 'last7Days', last_30_days: 'last30Days', this_month: 'thisMonth', last_month: 'lastMonth' })[value] || value}`)

    return (
        <div className="max-w-7xl mx-auto">
            {/* Header */}
            <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 mb-8">
                <div>
                    <button
                        onClick={() => navigate('/analytics/attribution')}
                        className={`flex items-center gap-2 text-sm text-gray-500 hover:text-gray-700 mb-3 transition-colors`}
                    >
                        <ArrowLeft size={16} className={isRTL ? 'rotate-180' : ''} />
                        {t('attribution.backToOverview', 'Back to Attribution')}
                    </button>
                    <div className="flex items-center gap-3 mb-2">
                        <div className="w-10 h-10 bg-purple-100 rounded-xl flex items-center justify-center">
                            <GitCompare className="text-purple-600" size={22} />
                        </div>
                        <h1 className="text-2xl font-bold text-gray-900">
                            {t('attribution.modelsTitle', 'Attribution Model Comparison')}
                        </h1>
                    </div>
                    <p className="text-gray-500">
                        {t('attribution.modelsSubtitle', 'Compare how different attribution models credit your marketing sources')}
                    </p>
                </div>

                <button
                    onClick={refetch}
                    disabled={loading}
                    className="flex items-center gap-2 px-4 py-2 text-sm font-medium text-gray-600 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 disabled:opacity-50 transition-colors"
                >
                    <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
                    {t('common.refresh', 'Refresh')}
                </button>
            </div>

            {/* Date Range Selector */}
            <div className="flex items-center gap-3 mb-6">
                <Dropdown.Root dir={isRTL ? 'rtl' : 'ltr'} open={showPeriodDropdown} onOpenChange={setShowPeriodDropdown}>
                    <Dropdown.Trigger asChild><button type="button"
                        className="flex items-center gap-2 px-4 py-2.5 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 transition-colors"
                    >
                        <Calendar size={16} className="text-gray-400" />
                        <span className="text-sm font-medium text-gray-700">
                            {periodLabel(period)}
                        </span>
                        <ChevronDown size={16} className="text-gray-400" />
                    </button></Dropdown.Trigger>

                    {showPeriodDropdown && (
                        <Dropdown.Portal><Dropdown.Content align="start" sideOffset={4} className="bg-white border border-gray-200 rounded-lg shadow-lg z-50 w-48">
                                {DATE_PERIODS.map(p => (
                                    <Dropdown.Item
                                        key={p.value}
                                        onSelect={() => {
                                            setPeriod(p.value)
                                            setShowPeriodDropdown(false)
                                        }}
                                        className={`w-full px-4 py-2.5 text-start text-sm data-[highlighted]:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary-500 first:rounded-t-lg last:rounded-b-lg ${period === p.value ? 'bg-indigo-50 text-indigo-600' : 'text-gray-700'}`}
                                    >
                                        {periodLabel(p.value)}
                                    </Dropdown.Item>
                                ))}
                        </Dropdown.Content></Dropdown.Portal>
                    )}
                </Dropdown.Root>

                <span className="text-sm text-gray-500">
                    {t('attribution.periodLabel', 'Comparing data for')}: {periodLabel(period)}
                </span>
            </div>

            {/* Error State */}
            {error && (
                <div role="alert" className="bg-red-50 border border-red-200 rounded-xl p-4 mb-6">
                    <p className="text-red-600 text-sm">{error}</p>
                </div>
            )}

            {/* Model Comparison Cards */}
            <ModelComparisonCards data={data} loading={loading} />

            {/* Explanation Card */}
            <div className="mt-8 p-6 bg-gray-50 border border-gray-200 rounded-xl">
                <h3 className="text-sm font-semibold text-gray-700 mb-4">
                    {t('attribution.understandingModels', 'Understanding Attribution Models')}
                </h3>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                    <div>
                        <h4 className="font-medium text-emerald-700 mb-2">
                            🎯 {t('attribution.firstTouch', 'First Touch')}
                        </h4>
                        <p className="text-sm text-gray-600">
                            {t('attribution.firstTouchExplain', 'Credits the first marketing source that brought the customer to your store. Best for understanding which channels are best at acquiring new customers.')}
                        </p>
                    </div>
                    <div>
                        <h4 className="font-medium text-blue-700 mb-2">
                            🏁 {t('attribution.lastTouch', 'Last Touch')}
                        </h4>
                        <p className="text-sm text-gray-600">
                            {t('attribution.lastTouchExplain', 'Credits the last marketing source before purchase. Best for understanding which channels are best at converting customers who are ready to buy.')}
                        </p>
                    </div>
                </div>

                <div className="mt-4 pt-4 border-t border-gray-200">
                    <p className="text-xs text-gray-500">
                        💡 {t('attribution.tip', 'Tip: If First Touch and Last Touch show different top sources, you have a multi-touch customer journey. Some channels bring customers in, others close the sale.')}
                    </p>
                </div>
            </div>
        </div>
    )
}
