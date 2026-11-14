/**
 * AF-15: Earnings Tab — Settlement summary with progress bars
 */
import { useState, useEffect } from 'react'
import { motion } from 'framer-motion'
import affiliateApi from '../../../../services/affiliateApi'
import SyntheticQaNotice from './SyntheticQaNotice'
import { DollarSign } from 'lucide-react'
import { useLanguage } from '../../i18n/LanguageContext'
import { SettlementProgress, PortalPanel, OrderActivity } from './PortalUi'
import CommissionActivity from './CommissionActivity'

const fadeUp = {
    hidden: { opacity: 0, y: 20 },
    visible: (i = 0) => ({ opacity: 1, y: 0, transition: { delay: i * 0.06, duration: 0.4 } })
}

export default function EarningsTab({ formatCurrency }) {
    const { t } = useLanguage()
    const [activity, setActivity] = useState(null)
    const [activityError, setActivityError] = useState(false)
    const [activityLoading, setActivityLoading] = useState(true)
    const [data, setData] = useState([])
    const [loading, setLoading] = useState(true)
    const [error, setError] = useState(false)
    const [reload, setReload] = useState(0)

    useEffect(() => {
        let active = true
        setLoading(true)
        setError(false)
        setActivityLoading(true)
        setActivityError(false)
        affiliateApi.getEarningsSummary()
            .then(d => { if (active) setData(d) })
            .catch(() => { if (active) { setData([]); setError(true) } })
            .finally(() => { if (active) setLoading(false) })
        affiliateApi.getOrders(1, 10)
            .then(d => { if (active) setActivity(d) })
            .catch(() => { if (active) setActivityError(true) })
            .finally(() => { if (active) setActivityLoading(false) })
        return () => { active = false }
    }, [reload])

    if (loading) {
        return (
            <div role="status" aria-label={t('ui.loading')} className="space-y-4">
                {[...Array(2)].map((_, i) => (
                    <div key={i} className="h-48 rounded-xl border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02] animate-pulse" />
                ))}
            </div>
        )
    }

    return (
        <div className="space-y-6">
            {error && <div role="alert" className="text-center py-8"><p>{t('ui.loadError')}</p><button onClick={() => setReload(value => value + 1)}>{t('ui.retry')}</button></div>}
            <p className="text-sm text-gray-600 dark:text-gray-400 leading-relaxed">{t('ux.earnedNote')}</p>
            {!error && !data.length && <PortalPanel><div className="text-center py-8"><DollarSign className="w-10 h-10 text-gray-500 mx-auto mb-4" /><h3 className="font-semibold mb-2">{t('portal.earningsTab.noEarnings')}</h3><p className="text-sm text-gray-500">{t('portal.earningsTab.noEarningsDesc')}</p></div></PortalPanel>}
            {data.map((summary, idx) => {

                return (
                    <motion.div
                        key={summary.currency}
                        variants={fadeUp}
                        initial="hidden"
                        animate="visible"
                        custom={idx}
                        className="rounded-xl border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02] p-6"
                    >
                        <div className="flex items-center justify-between mb-6">
                            <h3 className="text-base font-semibold text-gray-900 dark:text-white">{summary.currency} {t('portal.earningsTab.earnings')}</h3>
                            <span className="text-xs px-3 py-1 rounded-full bg-white dark:bg-white/[0.04] text-gray-600 dark:text-gray-400 border border-gray-200 dark:border-white/[0.06]">
                                {summary.currency}
                            </span>
                        </div>

                        {/* Summary cards */}
                        <SyntheticQaNotice amount={summary.syntheticApprovedExcluded} currency={summary.currency} formatCurrency={formatCurrency} />
                        <div className="grid grid-cols-2 md:grid-cols-5 gap-3 mb-6">
                            {[
                                { label: t('portal.earningsTab.totalEarned'), value: summary.totalEarned, color: '#6C63FF' },
                                { label: t('portal.earningsTab.approved'), value: summary.totalApproved, color: '#10B981' },
                                { label: t('portal.earningsTab.paid'), value: summary.totalPaid, color: '#22C55E' },
                                { label: t('portal.earningsTab.reversed'), value: summary.totalReversed, color: '#EF4444' },
                                { label: t('portal.earningsTab.netEarnings'), value: summary.netEarnings, color: '#A855F7' },
                            ].map((item, i) => (
                                <div key={i} className="p-3 rounded-lg border border-gray-200 dark:border-white/[0.06] bg-white dark:bg-white/[0.02]">
                                    <p className="text-xs font-medium mb-2 text-gray-600 dark:text-gray-400">
                                        {item.label}
                                    </p>
                                    <p className="text-lg font-bold text-gray-900 dark:text-white">
                                        {formatCurrency(item.value, summary.currency)}
                                    </p>
                                </div>
                            ))}
                        </div>

                        {/* Settlement Progress */}
                        <SettlementProgress summary={summary} />

                        {/* Reversed warning */}
                        {(summary.totalReversed || 0) > 0 && (
                            <div className="mt-4 p-3 rounded-lg bg-red-500/5 border border-red-500/10 text-sm text-red-700 dark:text-red-300 flex items-center gap-2">
                                ⚠️ {t('portal.earningsTab.reversedWarning')}: {formatCurrency(summary.totalReversed, summary.currency)}
                            </div>
                        )}
                    </motion.div>
                )
            })}
            <CommissionActivity formatCurrency={formatCurrency} />
            <PortalPanel title={t('ux.recent')} action={<a href="?tab=orders" className="text-sm text-[#6C63FF]">{t('ux.viewOrders')}</a>}>
                <p className="text-xs text-gray-500 dark:text-gray-400">{t('ux.activityNote')}</p>
                {activityLoading ? <p role="status" aria-label={t('ui.loading')}>{t('ui.loading')}</p> : <OrderActivity data={activity} error={activityError} formatCurrency={formatCurrency} />}
            </PortalPanel>
        </div>
    )
}
