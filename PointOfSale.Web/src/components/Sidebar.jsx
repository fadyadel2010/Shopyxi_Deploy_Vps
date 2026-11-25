import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import {
    Activity,
    Banknote,
    BarChart3,
    BookOpen,
    Bot,
    Briefcase,
    Building2,
    CalendarDays,
    ClipboardList,
    Coins,
    CreditCard,
    Database,
    Factory,
    FilePen,
    FileText,
    Files,
    Flag,
    Folder,
    FolderOpen,
    Globe,
    Handshake,
    History,
    Home,
    Hourglass,
    Landmark,
    LayoutDashboard,
    LayoutTemplate,
    ListTree,
    Navigation as NavigationIcon,
    Paintbrush,
    Palette,
    PanelBottom,
    PanelBottomDashed,
    PanelsTopLeft,
    PanelTop,
    Package,
    Plus,
    ReceiptText,
    RefreshCw,
    Ruler,
    ScrollText,
    Settings,
    Shield,
    ShoppingBag,
    ShoppingBasket,
    Store,
    Tags,
    Target,
    TrendingUp,
    TriangleAlert,
    Truck,
    Undo2,
    UserRound,
    Users,
    X,
} from 'lucide-react'
import standaloneSymbol from '../assets/brand/SHOPYXI_standalone_symbol.svg'
import { useAuthStore } from '../store/authStore'
import { useRTL } from '../hooks/useRTL'
import { isServiceMarketplaceWorkflowActor } from '../marketplace/authority'
import { canAccessCommerceHubVendorNavigationCapability } from '../auth/posVendorSessionAuthority'
import { hasPosPermission } from '../auth/posPermissions'
import { useSmartNavigation } from '../navigation/SmartNavigationContext'
import { SidebarFavorites } from '../navigation/SidebarFavorites'
import { Dialog, IconButton } from '../design-system/primitives'
import {
    navigationEntryApplication,
    resolveApplicationFromLocation,
    scopeMenuToApplication,
} from '../navigation/applicationNavigation'
import {
    dynamicParentPathname,
    findFirstMenuMatch,
    menuDestinationMatches,
} from '../navigation/navigationPresentation'
import {
    STOREFRONT_PRODUCT_NAVIGATION,
    STOREFRONT_SIDEBAR_STATE_KEY,
    STOREFRONT_OPERATIONS_SIDEBAR_STATE_KEY,
    isStorefrontNodeActive,
    resolveStorefrontScreen,
} from '../modules/storefront/informationArchitecture'
import {
    readSidebarExpansion,
    serializeSidebarExpansion,
    sidebarActorScope,
} from '../modules/storefront/informationArchitecture/sidebarExpansion'



const STOREFRONT_ICONS = {
    Activity,
    BarChart3,
    Bot,
    Target,
    FileText,
    Files,
    History,
    Home,
    LayoutDashboard,
    LayoutTemplate,
    Navigation: NavigationIcon,
    Paintbrush,
    Palette,
    PanelBottom,
    PanelBottomDashed,
    PanelsTopLeft,
    PanelTop,
    Package,
    CreditCard,
    Tag: Tags,
    Truck,
    Shield,
    Settings,
    ShoppingBag,
    ShoppingBasket,
    Store,
    Globe,
}

// Legacy emoji tokens remain stable identifiers in the menu contract; every
// rendered navigation icon is an approved Lucide component.
const LEGACY_ICON_COMPONENTS = {
    '📊': BarChart3,
    '🗄️': Database,
    '👥': Users,
    '🏢': Building2,
    '👤': UserRound,
    '📑': ListTree,
    '📏': Ruler,
    '🏭': Factory,
    '🏦': Landmark,
    '🏷️': Tags,
    '💼': Briefcase,
    '📂': FolderOpen,
    '🏁': Flag,
    '💵': Banknote,
    '📦': Package,
    '🔄': RefreshCw,
    '💳': CreditCard,
    '↩️': Undo2,
    '➕': Plus,
    '💰': Coins,
    '📅': CalendarDays,
    '🧾': ReceiptText,
    '⏳': Hourglass,
    '📖': BookOpen,
    '💸': Banknote,
    '⚙️': Settings,
    '⚠️': TriangleAlert,
    '📋': ClipboardList,
    '📈': TrendingUp,
    '📁': Folder,
    '📄': FileText,
    '🤝': Handshake,
    '🏪': Store,
    '📝': FilePen,
    '📜': ScrollText,
    '🔒': Shield,
}

const DESKTOP_QUERY = '(min-width: 1024px)'

const subscribeDesktop = (onChange) => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
        return () => {}
    }
    const query = window.matchMedia(DESKTOP_QUERY)
    if (typeof query.addEventListener === 'function') {
        query.addEventListener('change', onChange)
        return () => query.removeEventListener('change', onChange)
    }
    query.addListener?.(onChange)
    return () => query.removeListener?.(onChange)
}

const getDesktopSnapshot = () => typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(DESKTOP_QUERY).matches
    : true

// jsdom has no matchMedia; the desktop rail is the safe non-portal default.
const useIsDesktop = () => useSyncExternalStore(subscribeDesktop, getDesktopSnapshot, () => true)

const readExpandedMenus = (scope) => {
    try {
        const storefrontValue = window.localStorage.getItem(STOREFRONT_SIDEBAR_STATE_KEY)
        const operationsValue = window.localStorage.getItem(STOREFRONT_OPERATIONS_SIDEBAR_STATE_KEY)
        return readSidebarExpansion(scope, STOREFRONT_PRODUCT_NAVIGATION, [storefrontValue, operationsValue])
    } catch {
        return readSidebarExpansion(scope, STOREFRONT_PRODUCT_NAVIGATION, [])
    }
}

export default function Sidebar({ isOpen, onClose, restoreFocusRef }) {
    const user = useAuthStore((state) => state.user)
    const scope = sidebarActorScope(user)
    const isDesktop = useIsDesktop()
    return <SidebarContent key={scope || 'anonymous'} isOpen={isOpen} onClose={onClose} scope={scope}
        isDesktop={isDesktop} restoreFocusRef={restoreFocusRef} />
}

// The existing menu remains the single presentation/permission metadata source.
// Search also calls this factory with a key collector, not a second list of URLs.
export function buildAdminMenu(t, user) {
    // Translate the merged Storefront tree and project POS permissions onto it.
    const localizeStorefrontProductNode = (node, inheritedPermission) => ({
        ...node,
        label: t(node.labelKey, node.fallbackLabel),
        permission: node.permission || inheritedPermission || (node.children ? undefined : node.path?.startsWith('/ecommerce')
            ? 'storefront.orders.view'
            : 'storefront.design.manage'),
        children: node.children?.map(child => localizeStorefrontProductNode(child, node.permission || inheritedPermission)),
    })

    // New navigation structure with renamed items and proper order
    return [
        // 1) Dashboard
        {
            key: 'dashboard',
            label: t('nav.dashboard'),
            icon: '📊',
            path: '/',
            permission: 'dashboard.view'
        },
        // 2) Database
        {
            key: 'database',
            label: t('nav.database'),
            icon: '🗄️',
            children: [
                { path: '/customers', label: t('nav.customersRegistration'), icon: '👥', permission: 'customers.view' },
                { path: '/suppliers', label: t('nav.suppliersRegistration'), icon: '🏢', permission: 'suppliers.view' },
                { path: '/sales-representatives', label: t('nav.salesRepRegistration'), icon: '👤', permission: 'sales.operate' },
                { path: '/categories', label: t('nav.mainSubCategories'), icon: '📑', permission: 'catalog.view' },
                { path: '/units', label: t('units.title'), icon: '📏', permission: 'catalog.view' },
                { path: '/warehouses', label: t('nav.warehouses'), icon: '🏭', permission: 'inventory.view' },
                { path: '/banks', label: t('banks.title'), icon: '🏦', permission: 'finance.view' },
                { path: '/products', label: t('nav.itemCard'), icon: '🏷️', permission: 'catalog.view' }
            ]
        },
        // 3) Accounts
        {
            key: 'accounts',
            label: t('nav.accounts'),
            icon: '💼',
            children: [
                { path: '/financial-accounts?tab=main', label: t('accounts.mainAccounts'), icon: '🏦', permission: 'finance.view' },
                { path: '/financial-accounts?tab=sub', label: t('accounts.subAccounts'), icon: '📂', permission: 'finance.view' }
            ]
        },
        // 4) Opening Balance
        {
            key: 'opening_balance',
            label: t('nav.openingBalance'),
            icon: '🏁',
            children: [
                { path: '/treasury/initial-capitalization', label: t('nav.treasuryInitialCapitalization'), icon: '💵', permission: 'finance.initialize' },
                { path: '/opening-balance/customers', label: t('nav.customersOpeningBalance'), icon: '👥', permission: 'customers.balance.manage' },
                { path: '/opening-balance/suppliers', label: t('nav.suppliersOpeningBalance'), icon: '🏢', permission: 'suppliers.balance.manage' },
                { path: '/opening-balance/warehouse-stock', label: t('nav.warehouseOpeningBalance'), icon: '📦', permission: 'inventory.adjust' }
            ]
        },
        // 5) Transactions
        {
            key: 'transactions',
            label: t('nav.transactions'),
            icon: '🔄',
            children: [
                { path: '/pos', label: t('nav.sales'), icon: '💳', permission: 'sales.operate' },
                { path: '/sales-history', label: t('nav.salesReport'), icon: '📊', permission: 'sales.view' },
                { path: '/sales-returns', label: t('nav.salesReturn'), icon: '↩️', permission: 'sales.refund' },
                { path: '/purchase-invoice', label: t('nav.purchases'), icon: '➕', permission: 'purchases.manage' },
                { path: '/purchase-returns', label: t('nav.purchaseReturn'), icon: '↩️', permission: 'purchases.return' }
            ]
        },
        // 6) Treasury (Phase LTSR: Ledger-only)
        {
            key: 'treasury',
            label: t('nav.treasury'),
            icon: '💰',
            children: [
                // All Ledger-based routes (no fixed account-code identities)
                { path: '/treasury', label: t('nav.financeDashboard'), icon: '📊', permission: 'reports.financial.view' },
                { path: '/treasury/hub', label: t('nav.treasuryHub'), icon: '🏦', permission: 'finance.view' },
                { path: '/treasury/accounting-periods', label: t('accountingPeriods.title'), icon: '📅', permission: 'finance.view' },
                { path: '/treasury/vouchers', label: t('financialVouchers.title'), icon: '🧾', permission: 'finance.view' },
                { path: '/treasury/clearing-aging', label: t('treasury.clearingNav', 'Clearing Aging'), icon: '⏳', permission: 'reports.financial.view' },
                // Manual COD/Wallet settlement are reachable from Treasury / Clearing Aging only (no dedicated menu entry).
                { path: '/treasury/general-ledger', label: t('treasury.generalLedgerNav', 'General Ledger'), icon: '📖', permission: 'reports.financial.view' },
                { path: '/supplier-card-funding', label: t('nav.supplierCardFunding'), icon: '💳', permission: 'suppliers.balance.manage' },
                // Operational screens (not showing balance)
                { path: '/payments', label: t('nav.customerSupplierPayments'), icon: '💸', permission: 'finance.operate' },
                { path: '/adjustments', label: t('nav.customerSupplierSettlement'), icon: '⚙️', permission: 'finance.operate' },
                { path: '/treasury/transfers', label: t('treasury.transfers', 'Treasury Transfers'), icon: '💸', permission: 'finance.operate' },
                { path: '/outstanding-balances', label: t('accounts.outstandingBalances'), icon: '⚠️', permission: 'finance.view' }
            ]
        },
        // 7) Inventory
        {
            key: 'inventory',
            label: t('nav.inventory'),
            icon: '🏭',
            children: [
                { path: '/inventory', label: t('nav.inventoryBalance'), icon: '📊', permission: 'inventory.view' },
                { path: '/stock-transfers', label: t('nav.inventoryTransfers'), icon: '🔄', permission: 'inventory.transfer' },
                { path: '/stock-counts', label: t('stockCounts.title'), icon: '📋', permission: 'inventory.view' },
                { path: '/barcode-printing', label: t('nav.barcodePrinting'), icon: '🏷️', permission: 'catalog.view' }
            ]
        },
        // 8) Expenses
        {
            key: 'expenses',
            label: t('nav.expenses'),
            icon: '💸',
            children: [
                { path: '/expense-categories', label: t('nav.expensesRegistration'), icon: '📁', permission: 'finance.view' },
                { path: '/expenses', label: t('nav.expensesMovement'), icon: '💰', permission: 'finance.view' },
                { path: '/reports/expenses', label: t('nav.expensesReport'), icon: '📈', permission: 'reports.operational.view' }
            ]
        },
        // 9) Account Statement
        {
            key: 'account_statement',
            label: t('nav.accountStatement'),
            icon: '📄',
            children: [
                { path: '/statement-of-account', label: t('nav.supplierCustomerStatement'), icon: '📋', permission: 'finance.view' }
            ]
        },
        // Wave 2: Unified Storefront product — Design + Operations under one root
        localizeStorefrontProductNode(STOREFRONT_PRODUCT_NAVIGATION),
        // 11) Services Marketplace (Phase P2 - Tab-Based Navigation)
        {
            key: 'marketplace',
            application: 'services-marketplace',
            label: t('nav.marketplace', 'Services Marketplace'),
            icon: '🤝',
            path: '/marketplace',
            requiresMarketplaceActor: true
        },
        // CommerceHub Admin (Global Marketplace Publishing)
        {
            key: 'commercehub',
            application: 'commercehub',
            label: t('nav.commerceHub', 'CommerceHub'),
            icon: '🏪',
            children: [
                { path: '/commercehub/seller-application', label: t('nav.commerceHubSellerApplication', 'Become a CommerceHub Seller'), icon: '📝', permission: 'catalog.publish' },
                { path: '/commercehub/orders', label: t('nav.commerceHubOrders', 'Orders'), icon: '📦', capability: 'commercehub.orders.view', permission: 'storefront.orders.view' },
                { path: '/admin/commercehub', label: t('nav.commerceHubSettings', 'Publishing Settings'), icon: '⚙️', capability: 'commercehub.catalog.manage', permission: 'catalog.publish' },
                { path: '/commercehub/finance', label: t('nav.commerceHubFinance', 'Finance Dashboard'), icon: '💼', capability: 'commercehub.finance.view', permission: 'reports.financial.view' },
                { path: '/commercehub/earnings', label: t('nav.commerceHubEarnings', 'Vendor Earnings'), icon: '📈', capability: 'commercehub.finance.view', permission: 'reports.financial.view' },
                { path: '/commercehub/payments', label: t('nav.commerceHubDebtRepayment', 'Debt Repayment'), icon: '💸', capability: 'commercehub.payments.view', permission: 'storefront.payments.view' },
            ],
        },

        // Independent Affiliate workspace; retain each destination's permission.
        {
            key: 'affiliate-system',
            label: t('nav.affiliateSystem', 'Affiliate System'),
            icon: 'Target',
            permission: 'affiliates.manage',
            children: [
                {
                    key: 'admin-affiliates',
                    label: t('nav.affiliateManagement', 'Affiliate Management'),
                    icon: '👥',
                    path: '/admin/affiliates',
                    permission: 'affiliates.manage',
                },
                {
                    key: 'admin-affiliate-products',
                    label: t('nav.affiliateProducts', 'Affiliate Products'),
                    icon: 'Package',
                    path: '/admin/affiliates/products',
                    permission: 'affiliates.manage',
                },
            ],
        },

        // 10) Reports
        {
            key: 'reports',
            label: t('nav.reports'),
            icon: '📈',
            permission: 'reports.operational.view',
            children: [
                // ═══════════════════════════════════════════════════════════════
                // OPERATIONAL ANALYTICS REPORTS
                // These reports read from business tables for operational insights.
                // For financial reports, use Finance Control Center.
                // ═══════════════════════════════════════════════════════════════
                { path: '/sales-history', label: t('nav.salesReport'), icon: '📊' },
                { path: '/reports/sales-summary', label: t('nav.salesSummary'), icon: '📑' },
                { path: '/reports/sales-return-report', label: t('salesReturnReport.title'), icon: '↩️' },
                { path: '/reports/purchase-return-report', label: t('purchaseReturnReport.title'), icon: '↩️' },
                { path: '/reports/sales-by-category', label: t('nav.salesByCategoryReport'), icon: '📂' },
                { path: '/reports/sales-by-payment-method', label: t('nav.salesByPaymentMethod'), icon: '💳' },
                { path: '/reports/sales-analysis', label: t('nav.salesAnalysis'), icon: '📈' },
                { path: '/reports/purchase-register', label: t('purchaseRegister.title'), icon: '📦' },
                { path: '/reports/purchases-by-supplier', label: t('reports.purchasesBySupplier'), icon: '🏬' },
                { path: '/reports/cost-analysis', label: t('costAnalysis.title'), icon: '📊' },
                { path: '/reports/inventory-valuation', label: t('nav.inventoryValuation'), icon: '🏭' },
                { path: '/reports/product-movement', label: t('productMovement.title'), icon: '📋' },
                { path: '/reports/ar-aging', label: t('arAging.title'), icon: '📅' },
                { path: '/reports/ap-aging', label: t('apAging.title'), icon: '📆' },
                { path: '/sales-rep-reports', label: t('nav.salesRepReports'), icon: '👥' }
                // ═══════════════════════════════════════════════════════════════
                // FINANCIAL REPORTS MOVED TO FINANCE CONTROL CENTER
                // Trial Balance, P&L, Balance Sheet → /treasury/*
                // ═══════════════════════════════════════════════════════════════
            ]
        },
        // Finance Control Center (Phase 5) - Restricted to finance roles
        ...(hasPosPermission(user, 'reports.financial.view') ? [{
            key: 'finance_control',
            label: t('nav.financeControlCenter', 'Finance Control Center'),
            icon: '🏦',
            children: [
                { path: '/treasury', label: t('treasury.dashboard', 'Treasury Dashboard'), icon: '📊' },
                { path: '/treasury/trial-balance', label: t('treasury.trialBalance', 'Trial Balance'), icon: '⚖️' },
                { path: '/treasury/profit-loss', label: t('treasury.profitLoss', 'Profit & Loss'), icon: '📈' },
                { path: '/treasury/balance-sheet', label: t('treasury.balanceSheet', 'Balance Sheet'), icon: '📋' },
                { path: '/treasury/cash-flow', label: t('treasury.cashFlow', 'Cash Flow'), icon: '💧' },
                { path: '/treasury/general-ledger', label: t('treasury.generalLedger', 'General Ledger'), icon: '📖' },
                { path: '/treasury/clearing-aging', label: t('treasury.clearingAging', 'Clearing Aging'), icon: '⚠️' },
                { path: '/treasury/transfers', label: t('treasury.transfers', 'Treasury Transfers'), icon: '💸' }
            ]
        }] : []),
        // 11) Users
        ...(hasPosPermission(user, 'users.view') || hasPosPermission(user, 'audit.view') ? [{
            key: 'users',
            label: t('nav.users'),
            icon: '👤',
            children: [
                ...(hasPosPermission(user, 'users.view') ? [{ path: '/users', label: t('nav.users'), icon: '👥', permission: 'users.view' }] : []),
                { path: '/settings/audit-log', label: t('nav.trackingUsers'), icon: '📜', permission: 'audit.view' }
            ]
        }] : []),
        // 12) Settings
        {
            key: 'settings',
            label: t('settings.settings'),
            icon: '⚙️',
            path: '/settings',
            permission: 'settings.view'
        },
    ]
}

// Filter the navigation tree using backend-derived permissions/capabilities.
// Constitutional law (ONLINE_STORE_CANONICAL_BLUEPRINT.md §17 Law 16):
//   The Storefront product must never disappear. When capability-based
//   filtering removes all children of a Storefront-product section, a
//   placeholder is rendered instead of collapsing the section entirely.
const STOREFRONT_PRODUCT_KEYS = new Set(['storefront', 'storefront-design', 'operations'])

export function filterAdminMenu(menuItems, { t, user }) {
    const createStorefrontAccessPlaceholder = (item) => ({
        ...item,
        children: [{
            path: item.path || '#',
            label: t('storefront.accessPlaceholder', 'Request access to this workspace'),
            icon: '🔒',
            placeholder: true,
            onClick: () => {
                const url = `/support?context=${encodeURIComponent(item.label || item.key)}`
                window.open(url, '_blank', 'noopener,noreferrer')
            },
        }],
    })

    const filterMenuNode = (item) => {
        if (item.requiresMarketplaceActor && !isServiceMarketplaceWorkflowActor(user, true)) {
            return null
        }

        if (item.permission && !hasPosPermission(user, item.permission)) {
            return STOREFRONT_PRODUCT_KEYS.has(item.key)
                ? createStorefrontAccessPlaceholder(item)
                : null
        }

        if (item.capability && !canAccessCommerceHubVendorNavigationCapability(user, item.capability)) {
            return STOREFRONT_PRODUCT_KEYS.has(item.key)
                ? createStorefrontAccessPlaceholder(item)
                : null
        }

        const children = item.children?.map(filterMenuNode).filter(Boolean)
        const hasAccessibleDestination = (node) => !node.placeholder && Boolean(node.path || node.children?.some(hasAccessibleDestination))
        if (item.children && !children.some(hasAccessibleDestination)) {
            // Storefront product sections must never vanish — render a placeholder
            // (ONLINE_STORE_CANONICAL_BLUEPRINT.md §12.6, §17 Law 16)
            if (STOREFRONT_PRODUCT_KEYS.has(item.key)) {
                return createStorefrontAccessPlaceholder(item)
            }
            return null
        }

        return { ...item, children }
    }

    return menuItems.map(filterMenuNode).filter(Boolean)
}

function SidebarContent({ isOpen, onClose, scope, isDesktop, restoreFocusRef }) {
    const { t } = useTranslation()
    const { user } = useAuthStore()
    const location = useLocation()
    const navigation = useSmartNavigation()
    const [expandedMenus, setExpandedMenus] = useState(() => readExpandedMenus(scope))
    const { dir } = useRTL()
    const drawerCloseRef = useRef(null)
    const activeApplicationId = resolveApplicationFromLocation(location.pathname, location.search)
    // Application switching lives in the Top Header. The Sidebar only presents
    // the navigation owned by the application of the committed route.
    const menuItems = scopeMenuToApplication(buildAdminMenu(t, user), activeApplicationId)
    const toggleMenu = (menuKey) => setExpandedMenus(prev => ({ ...prev, [menuKey]: !prev[menuKey] }))
    const activeStorefrontScreenId = resolveStorefrontScreen(location.pathname, location.search)?.id || null
    const renderIcon = (icon, size = 18) => {
        const Icon = STOREFRONT_ICONS[icon] || LEGACY_ICON_COMPONENTS[icon] || Package
        return <Icon aria-hidden="true" size={size} />
    }
    const filteredMenuItems = filterAdminMenu(menuItems, { t, user })
    const scopedNavigation = navigation ? {
        ...navigation,
        entries: navigation.entries.filter(entry => navigationEntryApplication(entry) === activeApplicationId),
    } : null

    const isDestinationNode = (node) => {
        if (node.children || node.placeholder) {
            return false
        }
        if (node.screenIds) {
            return isStorefrontNodeActive(node, activeStorefrontScreenId)
        }
        return Boolean(node.path) && menuDestinationMatches(node.path, location.pathname, location.search)
    }

    // Factory tree order is the registry order, so the first eligible occurrence
    // is the canonical current destination for aria-current and active styling.
    const currentMatch = findFirstMenuMatch(filteredMenuItems, isDestinationNode)
    const parentContextPath = currentMatch ? null : dynamicParentPathname(location.pathname)
    const contextMatch = parentContextPath
        ? findFirstMenuMatch(filteredMenuItems, node => !node.children && !node.placeholder && node.path === parentContextPath)
        : null
    const activeLeaf = currentMatch?.node || null
    const contextLeaf = contextMatch?.node || null
    const activeAncestors = currentMatch?.ancestors || contextMatch?.ancestors || []

    useEffect(() => {
        if (!scope) return
        try {
            const serialized = serializeSidebarExpansion(scope, expandedMenus)
            window.localStorage.setItem(STOREFRONT_SIDEBAR_STATE_KEY, serialized)
            window.localStorage.setItem(STOREFRONT_OPERATIONS_SIDEBAR_STATE_KEY, serialized)
        } catch {
            // Navigation remains functional when browser storage is unavailable.
        }
    }, [expandedMenus, scope])

    useEffect(() => {
        setExpandedMenus((previous) => {
            let hasChanges = false
            const nextValue = { ...previous }

            const expandActiveNodes = (items) => {
                items.forEach((item) => {
                    if (!item.children) return
                    if (activeAncestors.includes(item) && !nextValue[item.key]) {
                        nextValue[item.key] = true
                        hasChanges = true
                    }
                    expandActiveNodes(item.children)
                })
            }

            expandActiveNodes(filteredMenuItems)

            return hasChanges ? nextValue : previous
        })
    }, [location.pathname, location.search, user])

    const previousDesktopRef = useRef(isDesktop)
    useEffect(() => {
        const becameDesktop = isDesktop && !previousDesktopRef.current
        previousDesktopRef.current = isDesktop
        if (becameDesktop && isOpen) onClose?.()
    }, [isDesktop, isOpen, onClose])

    const renderNestedNode = (node, depth = 1) => {
        const leafActive = node === activeLeaf
        const leafContext = node === contextLeaf
        const branchActive = activeAncestors.includes(node)

        if (node.children) {
            const submenuId = `sidebar-submenu-${node.key}`
            return (
                <div key={node.key}>
                    <button
                        type="button"
                        aria-controls={submenuId}
                        aria-expanded={Boolean(expandedMenus[node.key])}
                        onClick={() => toggleMenu(node.key)}
                        className={`pos-sidebar__item pos-sidebar__item--branch${branchActive ? ' pos-sidebar__item--context' : ''}`}
                    >
                        <span className="pos-sidebar__item-leading">
                            {renderIcon(node.icon, depth > 1 ? 16 : 18)}
                            <span className="pos-sidebar__label">{node.label}</span>
                        </span>
                        <svg aria-hidden="true" className={`pos-sidebar__chevron${expandedMenus[node.key] ? ' pos-sidebar__chevron--expanded' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                        </svg>
                    </button>
                    <div id={submenuId} inert={expandedMenus[node.key] ? undefined : ''} className={`grid transition-[grid-template-rows,opacity] duration-300 ${expandedMenus[node.key] ? 'grid-rows-[1fr] opacity-100' : 'grid-rows-[0fr] opacity-0'}`}>
                        <div className="min-h-0 overflow-hidden">
                            <div className="pos-sidebar__children">
                                {node.children.map((child) => renderNestedNode(child, depth + 1))}
                            </div>
                        </div>
                    </div>
                </div>
            )
        }

        if (node.placeholder) {
            return (
                <button
                    key={node.path || node.key}
                    type="button"
                    onClick={node.onClick}
                    className="pos-sidebar__item pos-sidebar__item--placeholder"
                >
                    {renderIcon(node.icon, depth > 1 ? 16 : 18)}
                    <span className="pos-sidebar__label">{node.label}</span>
                </button>
            )
        }

        return (
            <Link
                key={node.path}
                to={node.path}
                aria-current={leafActive ? 'page' : undefined}
                onClick={onClose}
                className={`pos-sidebar__item pos-sidebar__item--leaf${leafActive ? ' pos-sidebar__item--active' : ''}${leafContext ? ' pos-sidebar__item--context' : ''}`}
            >
                {renderIcon(node.icon, depth > 1 ? 16 : 18)}
                <span className="pos-sidebar__label">{node.label}</span>
            </Link>
        )
    }

    const navigationTree = (
        <nav aria-label={t('nav.mainNavigation', 'Main navigation')} className="pos-sidebar__nav">
            <div className="pos-sidebar__nav-inner">
                {scopedNavigation && <SidebarFavorites navigation={scopedNavigation} onNavigate={onClose}
                    activePathname={location.pathname} activeSearch={location.search} />}
                {filteredMenuItems.map((item) => (
                    <div key={item.key}>
                        {item.children ? (
                            <div>
                                <button
                                    type="button"
                                    aria-controls={`sidebar-submenu-${item.key}`}
                                    aria-expanded={Boolean(expandedMenus[item.key])}
                                    onClick={() => toggleMenu(item.key)}
                                    className={`pos-sidebar__item pos-sidebar__item--branch${activeAncestors.includes(item) ? ' pos-sidebar__item--context' : ''}`}
                                >
                                    <span className="pos-sidebar__item-leading">
                                        {renderIcon(item.icon)}
                                        <span className="pos-sidebar__label">{item.label}</span>
                                    </span>
                                    <svg
                                        aria-hidden="true"
                                        className={`pos-sidebar__chevron${expandedMenus[item.key] ? ' pos-sidebar__chevron--expanded' : ''}`}
                                        fill="none"
                                        stroke="currentColor"
                                        viewBox="0 0 24 24"
                                    >
                                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                                    </svg>
                                </button>

                                {/* Submenu */}
                                <div
                                    id={`sidebar-submenu-${item.key}`}
                                    inert={expandedMenus[item.key] ? undefined : ''}
                                    className={`grid transition-[grid-template-rows,opacity] duration-300 ${expandedMenus[item.key] ? 'grid-rows-[1fr] opacity-100' : 'grid-rows-[0fr] opacity-0'}`}
                                >
                                    <div className="min-h-0 overflow-hidden">
                                        <div className="pos-sidebar__children">
                                            {item.children.map((child) => renderNestedNode(child))}
                                        </div>
                                    </div>
                                </div>
                            </div>
                        ) : (
                            // Single menu item (no children)
                            <Link
                                to={item.path}
                                aria-current={item === activeLeaf ? 'page' : undefined}
                                onClick={onClose}
                                className={`pos-sidebar__item pos-sidebar__item--leaf${item === activeLeaf ? ' pos-sidebar__item--active' : ''}${item === contextLeaf ? ' pos-sidebar__item--context' : ''}`}
                            >
                                {renderIcon(item.icon)}
                                <span className="pos-sidebar__label">{item.label}</span>
                            </Link>
                        )}
                    </div>
                ))}
            </div>
        </nav>
    )

    const identity = (
        <div className="pos-sidebar__identity" dir={dir}>
            <div className="pos-sidebar__brand">
                <span className="pos-sidebar__symbol-plate">
                    <img src={standaloneSymbol} alt="" width="28" height="28" />
                </span>
                <span className="pos-sidebar__brand-text">
                    <span className="pos-sidebar__brand-name">
                        <bdi dir="ltr" lang="en">{t('login.brandName')}</bdi>
                    </span>
                    <span className="pos-sidebar__brand-qualifier">
                        <bdi dir="ltr" lang="en">{t('login.productQualifier')}</bdi>
                    </span>
                    <span className="pos-sidebar__subtitle">{t('app.subtitle')}</span>
                </span>
            </div>
            {!isDesktop ? (
                <IconButton
                    ref={drawerCloseRef}
                    label={t('common.close', 'Close navigation')}
                    icon={<X />}
                    size="sm"
                    className="pos-sidebar__close"
                    onClick={onClose}
                />
            ) : null}
        </div>
    )

    if (isDesktop) {
        return (
            <aside className="pos-sidebar" dir={dir}>
                {identity}
                {navigationTree}
            </aside>
        )
    }

    return (
        <Dialog
            open={Boolean(isOpen)}
            onOpenChange={(next) => { if (!next) onClose?.() }}
            title={t('nav.mainNavigation', 'Main navigation')}
            showClose={false}
            closeOnOverlayClick
            closeOnEscape
            initialFocusRef={drawerCloseRef}
            restoreFocusRef={restoreFocusRef}
            className="pos-shell-drawer"
        >
            {identity}
            {navigationTree}
        </Dialog>
    )
}
