import { useEffect, useMemo, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import {
    ArrowUpRight,
    BadgePercent,
    BadgeDollarSign,
    ChevronRight,
    CreditCard,
    Languages,
    Landmark,
    LayoutDashboard,
    LayoutTemplate,
    LogOut,
    Menu,
    MessageSquareQuote,
    Package,
    RotateCw,
    Shield,
    Store,
    Users,
    X,
} from 'lucide-react'
import { logoutSuperAdmin } from '../auth/superAdminAuthService'
import { useSuperAdminAuthStore } from '../auth/superAdminAuthStore'
import {
    hasSuperAdminNavigationCapability,
    type SuperAdminIdentity,
    type SuperAdminNavigationCapability,
} from '../auth/superAdminAuth.types'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'

interface NavigationItem {
    labelKey: string
    descriptionKey: string
    icon: typeof LayoutDashboard
    to?: string
    disabled?: boolean
    capability?: SuperAdminNavigationCapability
}

interface NavigationGroup {
    titleKey: string
    items: NavigationItem[]
}

const navigationGroups: NavigationGroup[] = [
    {
        titleKey: 'overview',
        items: [
            {
                labelKey: 'dashboard',
                descriptionKey: 'dashboardDescription',
                icon: LayoutDashboard,
                disabled: true,
            },
            {
                labelKey: 'finance',
                descriptionKey: 'financeDescription',
                icon: BadgeDollarSign,
                to: '/super-admin/finance',
                capability: 'super-admin.finance.view',
            },
            {
                labelKey: 'orders',
                descriptionKey: 'ordersDescription',
                icon: Package,
                to: '/super-admin/orders',
                capability: 'super-admin.orders.view',
            },
        ],
    },
    {
        titleKey: 'money',
        items: [
            {
                labelKey: 'payments',
                descriptionKey: 'paymentsDescription',
                icon: CreditCard,
                to: '/super-admin/payments',
                capability: 'super-admin.payments.view',
            },
            {
                labelKey: 'payouts',
                descriptionKey: 'payoutsDescription',
                icon: Landmark,
                to: '/super-admin/payouts',
                capability: 'super-admin.payouts.manage',
            },
            {
                labelKey: 'refunds',
                descriptionKey: 'refundsDescription',
                icon: ArrowUpRight,
                to: '/super-admin/refunds',
                capability: 'super-admin.refunds.manage',
            },
            {
                labelKey: 'commission',
                descriptionKey: 'commissionDescription',
                icon: BadgePercent,
                to: '/super-admin/commission',
                capability: 'super-admin.commission.manage',
            },
            {
                labelKey: 'promoCodes',
                descriptionKey: 'promoCodesDescription',
                icon: BadgePercent,
                to: '/super-admin/promo-codes',
                capability: 'super-admin.promotions.manage',
            },
            {
                labelKey: 'paymentProviders',
                descriptionKey: 'paymentProvidersDescription',
                icon: CreditCard,
                to: '/super-admin/commercehub/payment-providers',
                capability: 'super-admin.payment-providers.view',
            },
        ],
    },
    {
        titleKey: 'trust',
        items: [
            {
                labelKey: 'geographicCatalog',
                descriptionKey: 'geographicCatalogDescription',
                icon: Shield,
                to: '/super-admin/geographic/catalog',
            },
            {
                labelKey: 'reviews',
                descriptionKey: 'reviewsDescription',
                icon: MessageSquareQuote,
                to: '/super-admin/reviews',
                capability: 'super-admin.reviews.view',
            },
            {
                labelKey: 'users',
                descriptionKey: 'usersDescription',
                icon: Users,
                to: '/super-admin/users',
                capability: 'super-admin.users.govern',
            },
            {
                labelKey: 'sellerApplications',
                descriptionKey: 'sellerApplicationsDescription',
                icon: Store,
                to: '/super-admin/commercehub/seller-applications',
                capability: 'super-admin.marketplace.govern',
            },
            {
                labelKey: 'marketplaceGovernance',
                descriptionKey: 'marketplaceGovernanceDescription',
                icon: Shield,
                to: '/super-admin/marketplace/governance',
                capability: 'super-admin.marketplace.govern',
            },
            {
                labelKey: 'marketplaceOperations',
                descriptionKey: 'marketplaceOperationsDescription',
                icon: RotateCw,
                to: '/super-admin/marketplace/operations',
                capability: 'super-admin.marketplace.operate',
            },
        ],
    },
    {
        titleKey: 'content',
        items: [
            {
                labelKey: 'homepage',
                descriptionKey: 'homepageDescription',
                icon: LayoutTemplate,
                to: '/super-admin/homepage',
                capability: 'super-admin.homepage.manage',
            },
            {
                labelKey: 'storefrontDefaults',
                descriptionKey: 'storefrontDefaultsDescription',
                icon: LayoutTemplate,
                to: '/super-admin/storefront/footer',
                capability: 'super-admin.storefront-defaults.manage',
            },
            {
                labelKey: 'marketplaceCms',
                descriptionKey: 'marketplaceCmsDescription',
                icon: LayoutTemplate,
                to: '/super-admin/marketplace/pages',
                capability: 'super-admin.marketplace-cms.manage',
            },
            {
                labelKey: 'marketplaceFooter',
                descriptionKey: 'marketplaceFooterDescription',
                icon: LayoutTemplate,
                to: '/super-admin/marketplace/footer',
                capability: 'super-admin.marketplace-footer.manage',
            },
        ],
    },
]

const pageLabels: Array<{ match: string; key: string }> = [
    { match: '/super-admin/geographic/catalog', key: 'geographicCatalog' },
    { match: '/super-admin/finance', key: 'finance' },
    { match: '/super-admin/orders', key: 'orders' },
    { match: '/super-admin/reviews', key: 'reviews' },
    { match: '/super-admin/users', key: 'users' },
    { match: '/super-admin/commercehub/seller-applications', key: 'sellerApplications' },
    { match: '/super-admin/marketplace/governance', key: 'marketplaceGovernance' },
    { match: '/super-admin/marketplace/operations', key: 'marketplaceOperations' },
    { match: '/super-admin/payments', key: 'payments' },
    { match: '/super-admin/commercehub/payment-providers', key: 'paymentProviders' },
    { match: '/super-admin/payouts', key: 'payouts' },
    { match: '/super-admin/refunds', key: 'refunds' },
    { match: '/super-admin/commission', key: 'commission' },
    { match: '/super-admin/promo-codes', key: 'promoCodes' },
    { match: '/super-admin/homepage/manager', key: 'homepage' },
    { match: '/super-admin/homepage', key: 'homepage' },
    { match: '/super-admin/storefront/footer', key: 'storefrontDefaults' },
    { match: '/super-admin/marketplace/pages', key: 'marketplaceCms' },
    { match: '/super-admin/marketplace/footer', key: 'marketplaceFooter' },
]

const groupTitleKeys: Record<string, { en: string; ar: string }> = {
    overview: { en: 'Overview', ar: 'نظرة عامة' },
    money: { en: 'Money & risk', ar: 'المال والمخاطر' },
    trust: { en: 'Trust & operations', ar: 'الثقة والعمليات' },
    content: { en: 'Storefront content', ar: 'محتوى المتجر' },
}

function NavigationLink({
    item,
    onNavigate,
}: {
    item: NavigationItem
    onNavigate?: () => void
}) {
    const { t } = useSuperAdminLocale()
    const Icon = item.icon
    const label = t(`superAdmin.navigation.${item.labelKey}`, `superAdmin.navigation.${item.labelKey}`)
    const description = t(`superAdmin.navigation.${item.descriptionKey}`, `superAdmin.navigation.${item.descriptionKey}`)

    if (item.disabled || !item.to) {
        return (
            <div
                title="Available in a later phase"
                className="flex items-center gap-2.5 rounded-lg border border-white/5 bg-white/[0.02] px-2.5 py-2 text-slate-500"
            >
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-white/[0.04]">
                    <Icon className="h-4 w-4" />
                </span>
                <span className="min-w-0 flex-1 truncate text-sm font-medium">{label}</span>
                <span className="rounded-full border border-amber-400/20 bg-amber-400/10 px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wider text-amber-200/80">
                    Soon
                </span>
            </div>
        )
    }

    return (
        <NavLink
            end
            onClick={onNavigate}
            to={item.to}
            title={description}
            className={({ isActive }) =>
                [
                    'group flex items-center gap-2.5 rounded-lg border px-2.5 py-2 transition',
                    isActive
                        ? 'border-emerald-400/30 bg-emerald-400/10 text-white'
                        : 'border-transparent text-slate-300 hover:border-white/10 hover:bg-white/[0.05]',
                ].join(' ')
            }
        >
            {({ isActive }) => (
                <>
                    <span
                        className={[
                            'flex h-8 w-8 shrink-0 items-center justify-center rounded-lg',
                            isActive ? 'bg-emerald-300/15 text-emerald-100' : 'bg-white/[0.05] text-slate-300',
                        ].join(' ')}
                    >
                        <Icon className="h-4 w-4" />
                    </span>
                    <span className="min-w-0 flex-1 truncate text-[13px] font-medium leading-5">{label}</span>
                    <ChevronRight className="h-3.5 w-3.5 shrink-0 text-slate-500 rtl:rotate-180" />
                </>
            )}
        </NavLink>
    )
}

function SidebarContent({
    user,
    onNavigate,
}: {
    user: SuperAdminIdentity | null
    onNavigate?: () => void
}) {
    const { t, tr } = useSuperAdminLocale()

    return (
        <div className="flex h-full flex-col">
            <div className="border-b border-white/10 px-4 py-4">
                <div className="flex items-center gap-2.5">
                    <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-gradient-to-br from-emerald-400 to-cyan-400 text-slate-950 shadow-lg shadow-emerald-400/20">
                        <Shield className="h-4 w-4" />
                    </div>
                    <div className="min-w-0">
                        <p className="truncate text-[10px] font-semibold uppercase tracking-[0.2em] text-emerald-300">
                            {t('superAdmin.layout.platformName', 'superAdmin.layout.platformName')}
                        </p>
                        <h1 className="truncate text-base font-semibold text-white">
                            {t('superAdmin.layout.title', 'superAdmin.layout.title')}
                        </h1>
                    </div>
                </div>
                <p className="mt-3 hidden text-xs leading-5 text-slate-400 xl:block">
                    {tr(
                        'Finance, orders, governance, and storefront content in one isolated console.',
                        'المالية والطلبات والحوكمة ومحتوى المتجر في وحدة تحكم معزولة واحدة.',
                    )}
                </p>
            </div>

            <nav className="flex-1 space-y-4 overflow-y-auto px-2.5 py-3" aria-label={tr('Super Admin navigation', 'تنقل الإدارة العليا')}>
                {navigationGroups.map((group) => {
                    const items = group.items.filter(
                        (item) => !item.capability || hasSuperAdminNavigationCapability(user, item.capability),
                    )
                    if (!items.length) return null

                    const groupTitle = groupTitleKeys[group.titleKey]
                    return (
                        <div key={group.titleKey} className="space-y-1">
                            <p className="px-2.5 pb-1 text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                {tr(groupTitle.en, groupTitle.ar)}
                            </p>
                            {items.map((item) => (
                                <NavigationLink key={item.labelKey} item={item} onNavigate={onNavigate} />
                            ))}
                        </div>
                    )
                })}
            </nav>
        </div>
    )
}

export default function SuperAdminLayout() {
    const location = useLocation()
    const navigate = useNavigate()
    const { user, logout } = useSuperAdminAuthStore()
    const [isSidebarOpen, setIsSidebarOpen] = useState(false)
    const { t, tr, isArabic } = useSuperAdminLocale()

    useEffect(() => {
        setIsSidebarOpen(false)
    }, [location.pathname])

    const pageLabel = useMemo(() => {
        const match = pageLabels.find((entry) => location.pathname.startsWith(entry.match))
        return t(`superAdmin.navigation.${match?.key ?? 'dashboard'}`, `superAdmin.navigation.${match?.key ?? 'dashboard'}`)
    }, [location.pathname, t])

    function handleLogout() {
        logoutSuperAdmin()
            .catch(() => {})
            .finally(() => {
                logout()
                navigate('/super-admin/login', { replace: true })
            })
    }

    function toggleLanguage() {
        const nextLanguage = isArabic ? 'en' : 'ar'
        void import('../../i18n/config').then((module) => module.default.changeLanguage(nextLanguage))
    }

    return (
        <div dir={isArabic ? 'rtl' : 'ltr'} className="min-h-screen bg-slate-100">
            <div className="relative flex min-h-screen">
                <aside className="hidden w-72 shrink-0 border-e border-white/10 bg-slate-950 xl:block">
                    <div className="sticky top-0 h-screen">
                        <SidebarContent user={user} />
                    </div>
                </aside>

                {isSidebarOpen ? (
                    <div className="fixed inset-0 z-40 xl:hidden">
                        <button
                            type="button"
                            aria-label={tr('Close sidebar', 'إغلاق القائمة')}
                            className="absolute inset-0 bg-slate-950/60 backdrop-blur-sm"
                            onClick={() => setIsSidebarOpen(false)}
                        />
                        <aside className="absolute inset-y-0 start-0 h-full w-[86%] max-w-xs border-e border-white/10 bg-slate-950 shadow-2xl">
                            <div className="flex items-center justify-end px-3 pt-3">
                                <button
                                    type="button"
                                    aria-label={tr('Close sidebar', 'إغلاق القائمة')}
                                    className="flex h-9 w-9 items-center justify-center rounded-lg border border-white/10 bg-white/[0.04] text-slate-200"
                                    onClick={() => setIsSidebarOpen(false)}
                                >
                                    <X className="h-4 w-4" />
                                </button>
                            </div>
                            <SidebarContent user={user} onNavigate={() => setIsSidebarOpen(false)} />
                        </aside>
                    </div>
                ) : null}

                <div className="flex min-w-0 flex-1 flex-col">
                    <header className="sticky top-0 z-30 border-b border-slate-200 bg-white/95 backdrop-blur">
                        <div className="mx-auto flex max-w-[1500px] items-center justify-between gap-3 px-3 py-2.5 sm:px-5 lg:px-6">
                            <div className="flex min-w-0 items-center gap-2.5">
                                <button
                                    type="button"
                                    aria-label={tr('Open sidebar', 'فتح القائمة')}
                                    className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-700 shadow-sm xl:hidden"
                                    onClick={() => setIsSidebarOpen(true)}
                                >
                                    <Menu className="h-4 w-4" />
                                </button>

                                <div className="min-w-0">
                                    <p className="truncate text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-700">
                                        {t('superAdmin.layout.title', 'superAdmin.layout.title')}
                                    </p>
                                    <h2 className="truncate text-base font-semibold text-slate-950">{pageLabel}</h2>
                                </div>
                            </div>

                            <div className="flex shrink-0 items-center gap-2">
                                <button
                                    type="button"
                                    onClick={toggleLanguage}
                                    className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-2 text-xs font-semibold text-slate-700 shadow-sm transition hover:bg-slate-50"
                                    title={tr('Switch to Arabic', 'التبديل إلى الإنجليزية')}
                                >
                                    <Languages className="h-4 w-4" />
                                    <span>{isArabic ? 'English' : 'العربية'}</span>
                                </button>

                                <div className="hidden rounded-lg border border-slate-200 bg-slate-50 px-2.5 py-1.5 text-start sm:block">
                                    <p className="text-[9px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                                        {tr('Signed in', 'مسجل الدخول')}
                                    </p>
                                    <p className="max-w-[190px] truncate text-xs font-medium text-slate-900" dir="ltr">
                                        {user?.email ?? t('superAdmin.layout.ownerFallback', 'superAdmin.layout.ownerFallback')}
                                    </p>
                                </div>

                                <button
                                    type="button"
                                    aria-label={tr('Log out', 'خروج')}
                                    className="inline-flex items-center gap-1.5 rounded-lg bg-slate-950 px-3 py-2 text-xs font-semibold text-white shadow-sm transition hover:bg-slate-800"
                                    onClick={handleLogout}
                                >
                                    <LogOut className="h-3.5 w-3.5" />
                                    <span className="hidden sm:inline">{tr('Log out', 'خروج')}</span>
                                </button>
                            </div>
                        </div>
                    </header>

                    <main className="flex-1 px-3 py-4 sm:px-5 lg:px-6">
                        <div className="mx-auto max-w-[1500px]">
                            <Outlet />
                        </div>
                    </main>
                </div>
            </div>
        </div>
    )
}
