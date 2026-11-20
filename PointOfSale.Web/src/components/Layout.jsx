import { useRef, useState } from 'react'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { LogOut, Menu, Settings as SettingsIcon } from 'lucide-react'
import { useAuthStore } from '../store/authStore'
import standaloneSymbol from '../assets/brand/SHOPYXI_standalone_symbol.svg'
import LanguageSwitcher from './LanguageSwitcher'
import Sidebar from './Sidebar'
import AppSwitcher from './AppSwitcher'
import EmailVerificationBanner from './EmailVerificationBanner'
import MarketplaceNotificationBell from './MarketplaceNotificationBell'
import HelpButton from '../admin/help/HelpButton'
import HelpDrawer from '../admin/help/HelpDrawer'
import { getHelpArticleKey } from '../admin/help/helpRegistry'
import { useRTL } from '../hooks/useRTL'
import api from '../services/api'
import { invalidatePosRuntimeCache } from '../services/posRuntimeCache'
import { broadcastPosVendorLogout } from '../auth/posVendorSessionAuthority'
import SmartNavigationProvider from '../navigation/SmartNavigationProvider'
import CommandPalette from '../navigation/CommandPalette'
import NavigationBreadcrumbs from '../navigation/NavigationBreadcrumbs'
import { PosOverlayHost, PosOverlayProvider } from '../design-system/overlays/posOverlayContext'
import { IconButton } from '../design-system/primitives'
import './PosShell.css'

export default function Layout() {
    return (
        <SmartNavigationProvider>
            <PosOverlayProvider>
                <LayoutContent />
            </PosOverlayProvider>
        </SmartNavigationProvider>
    )
}

function LayoutContent() {
    const { t } = useTranslation()
    const { user, logout } = useAuthStore()
    const navigate = useNavigate()
    const location = useLocation()
    const [sidebarOpen, setSidebarOpen] = useState(false)
    const [helpOpen, setHelpOpen] = useState(false)
    const [isLoggingOut, setIsLoggingOut] = useState(false)
    const { dir } = useRTL()
    const articleKey = getHelpArticleKey(location.pathname, location.search)
    const menuButtonRef = useRef(null)
    const helpButtonRef = useRef(null)

    const handleLogout = async () => {
        if (isLoggingOut) {
            return
        }

        setIsLoggingOut(true)

        try {
            await api.post('/auth/logout')
        } catch (error) {
            if (import.meta.env.DEV) {
                console.warn('POS logout request failed, clearing local session anyway.', error)
            }
        } finally {
            invalidatePosRuntimeCache({ reason: 'logout', broadcast: false })
            broadcastPosVendorLogout()
            logout()
            // Navigate will unmount this component, so no need to reset isLoggingOut
            navigate('/login', { replace: true })
        }
    }

    const logoutLabel = isLoggingOut ? t('common.loading') : t('app.logout')
    const operatorName = [user?.firstName, user?.lastName].filter(Boolean).join(' ') || t('common.unavailable')
    const operatorRole = t(`userManagement.roles.${user?.role}`, { defaultValue: t('common.unavailable') })
    const operatorIdentity = `${operatorName} — ${operatorRole}`

    return (
        <div className="pos-shell" dir={dir} data-product="pos">
            <a href="#pos-main-content" className="pos-shell__skip-link">
                {t('shell.skipToContent', 'Skip to main content')}
            </a>

            <Sidebar
                isOpen={sidebarOpen}
                onClose={() => setSidebarOpen(false)}
                restoreFocusRef={menuButtonRef}
            />

            <div className="pos-shell__content">
                <header className="pos-shell__topbar">
                    <div className="pos-shell__topbar-inner">
                        <div className="pos-shell__lead">
                            <IconButton
                                ref={menuButtonRef}
                                className="pos-shell__menu-button"
                                label={t('smartNavigation.openNavigation', 'Open navigation')}
                                icon={<Menu />}
                                onClick={() => setSidebarOpen(true)}
                                aria-haspopup="dialog"
                                aria-expanded={sidebarOpen}
                            />

                            <div className="pos-shell__brand">
                                <span className="pos-shell__brand-symbol" aria-hidden="true">
                                    <img src={standaloneSymbol} alt="" width="24" height="24" />
                                </span>
                                <span className="pos-shell__brand-text">
                                    <bdi className="pos-shell__brand-name" dir="ltr" lang="en">
                                        {t('login.brandName')}
                                    </bdi>
                                    <bdi className="pos-shell__brand-qualifier" dir="ltr" lang="en">
                                        {t('login.productQualifier')}
                                    </bdi>
                                </span>
                            </div>

                            <AppSwitcher onNavigate={() => setSidebarOpen(false)} />
                        </div>

                        <div className="pos-shell__actions">
                            {/* User Menu */}
                            <CommandPalette onNavigate={() => setSidebarOpen(false)} />
                            {/* Marketplace Notifications */}
                            <MarketplaceNotificationBell />

                            <IconButton
                                className="pos-shell__settings"
                                label={t('settings.settings')}
                                icon={<SettingsIcon />}
                                onClick={() => navigate('/settings')}
                            />

                            <span className="pos-shell__divider" aria-hidden="true" />
                            <div className="pos-shell__language">
                                <LanguageSwitcher variant="login" />
                            </div>

                            <div
                                className="pos-shell__user"
                                role="group"
                                aria-label={operatorIdentity}
                                title={operatorIdentity}
                            >
                                <div className="pos-shell__user-text">
                                    <div className="pos-shell__user-name">
                                        {user?.firstName} {user?.lastName}
                                    </div>
                                    <div className="pos-shell__user-role">
                                        {operatorRole}
                                    </div>
                                </div>
                                <div className="pos-shell__avatar" aria-hidden="true">
                                    {user?.firstName?.[0]}{user?.lastName?.[0]}
                                </div>
                            </div>

                            <button
                                type="button"
                                onClick={handleLogout}
                                disabled={isLoggingOut}
                                className="pos-shell__logout"
                                title={t('app.logout')}
                                aria-label={logoutLabel}
                                aria-busy={isLoggingOut || undefined}
                            >
                                <LogOut size={18} aria-hidden="true" />
                                <span className="pos-shell__logout-label" aria-hidden="true">{logoutLabel}</span>
                            </button>
                        </div>
                    </div>
                </header>

                {/* Phase 8.4: Email Verification Banner */}
                <EmailVerificationBanner />

                <main id="pos-main-content" tabIndex={-1} className="pos-shell__main">
                    <div className="pos-shell__context-band">
                        <NavigationBreadcrumbs />
                        <HelpButton
                            ref={helpButtonRef}
                            onClick={() => setHelpOpen(true)}
                            hasArticle={!!articleKey}
                        />
                    </div>

                    <Outlet />
                </main>
            </div>

            <HelpDrawer
                isOpen={helpOpen}
                onClose={() => setHelpOpen(false)}
                articleKey={articleKey}
                pathname={`${location.pathname}${location.search}`}
                restoreFocusRef={helpButtonRef}
            />

            <PosOverlayHost />
        </div>
    )
}
