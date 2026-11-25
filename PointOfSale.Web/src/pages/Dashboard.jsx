import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PosDashboardContent from '../components/PosDashboardContent'
import StorefrontAnalyticsDashboard from './StorefrontAnalyticsDashboard'
import { useRTL } from '../hooks/useRTL'
import { useAuthStore } from '../store/authStore'
import { hasPosPermission } from '../auth/posPermissions'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../design-system/primitives'
import { useDashboardActorScope } from '../components/dashboard/usePosDashboardData'
import '../components/dashboard/PosDashboard.css'

const DASHBOARD_TAB_STORAGE_KEY = 'admin.dashboard.activeTab'

const DASHBOARD_TABS = [
    { id: 'pos', labelKey: 'dashboard.tabs.pos', permission: 'dashboard.view' },
    { id: 'storefront', labelKey: 'dashboard.tabs.storefront', permission: 'storefront.analytics.view' },
]

export default function Dashboard() {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const actorScope = useDashboardActorScope()
    const user = useAuthStore((state) => state.user)
    const availableTabs = DASHBOARD_TABS.filter((tab) => hasPosPermission(user, tab.permission))
    const [activeTab, setActiveTab] = useState(() => {
        const storedValue = window.localStorage.getItem(DASHBOARD_TAB_STORAGE_KEY)
        if (storedValue && availableTabs.some((tab) => tab.id === storedValue)) {
            return storedValue
        }
        return availableTabs[0]?.id ?? 'pos'
    })

    useEffect(() => {
        if (!availableTabs.some((tab) => tab.id === activeTab)) {
            setActiveTab(availableTabs[0]?.id ?? 'pos')
            return
        }
        window.localStorage.setItem(DASHBOARD_TAB_STORAGE_KEY, activeTab)
    }, [activeTab, availableTabs])

    const selectedTab = availableTabs.some(tab => tab.id === activeTab) ? activeTab : availableTabs[0]?.id ?? 'pos'
    const renderView = tab => tab.id === 'storefront'
        ? <StorefrontAnalyticsDashboard key={actorScope} presentation="dashboard" />
        : <PosDashboardContent key={actorScope} />
    return (
        <div className="pos-dashboard" dir={dir}>
            <header className="pos-dashboard__header">
                <h1>{t('dashboard.title')}</h1>
                <p className="pos-dashboard__caption">{t('dashboard.phase42.scope')}</p>
            </header>
            <Tabs value={selectedTab} onValueChange={setActiveTab} dir={dir}>
                {availableTabs.length > 1 ? (
                    <TabsList aria-label={t('dashboard.dashboardSwitcher')}>
                        {availableTabs.map(tab => <TabsTrigger key={tab.id} value={tab.id}>{t(tab.labelKey)}</TabsTrigger>)}
                    </TabsList>
                ) : null}
                {availableTabs.length > 1
                    ? availableTabs.map(tab => <TabsContent key={tab.id} value={tab.id}>{renderView(tab)}</TabsContent>)
                    : availableTabs.map(tab => <div key={tab.id}>{renderView(tab)}</div>)}
            </Tabs>
        </div>
    )
}
