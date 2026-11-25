import { useMemo } from 'react'
import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import { useLocation, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import {
    Check,
    ChevronDown,
    LayoutDashboard,
    LayoutGrid,
    ShoppingBag,
    Store,
    Target,
} from 'lucide-react'
import { useAuthStore } from '../store/authStore'
import { useRTL } from '../hooks/useRTL'
import { Badge } from '../design-system/primitives'
import { usePosOverlayElement } from '../design-system/overlays/posOverlayContext'
import {
    APPLICATIONS,
    resolveApplicationFromLocation,
    resolveAuthorizedLandingPath,
    scopeMenuToApplication,
} from '../navigation/applicationNavigation'
import { buildAdminMenu, filterAdminMenu } from './Sidebar'

const APPLICATION_ICONS = {
    pos: LayoutDashboard,
    storefront: Store,
    commercehub: ShoppingBag,
    commission: Target,
}

/**
 * Header application switcher. It only changes the current application; page
 * navigation inside an application remains the Sidebar's responsibility.
 *
 * Availability is derived from the same menu filter that protects the Sidebar,
 * so the switcher can never reveal an application the actor cannot reach.
 */
export default function AppSwitcher({ onNavigate }) {
    const { t } = useTranslation()
    const { dir } = useRTL()
    const { user } = useAuthStore()
    const location = useLocation()
    const navigate = useNavigate()
    const overlayContainer = usePosOverlayElement()
    const triggerLabel = t('applications.trigger', 'Applications')

    const applications = useMemo(() => APPLICATIONS.map(application => {
        const menuItems = scopeMenuToApplication(buildAdminMenu(t, user), application.id)
        const filteredMenuItems = filterAdminMenu(menuItems, { t, user })
        const landingPath = resolveAuthorizedLandingPath(application, filteredMenuItems)
        return { ...application, landingPath, label: t(application.labelKey) }
    }).filter(application => Boolean(application.landingPath)), [t, user])

    const activeApplicationId = resolveApplicationFromLocation(location.pathname, location.search)
    const hasApplications = applications.length > 0
    const triggerTitle = hasApplications
        ? triggerLabel
        : t('applications.unavailable', 'No applications are available with your current access.')

    const selectApplication = (application) => {
        // Selecting the active application keeps the current page.
        if (application.id !== activeApplicationId) {
            navigate(application.landingPath)
        }
        onNavigate?.()
    }

    return (
        <div className="pos-applications">
            <DropdownMenu.Root dir={dir}>
                <DropdownMenu.Trigger asChild>
                    <button
                        type="button"
                        className="pos-applications__trigger"
                        aria-label={triggerLabel}
                        title={triggerTitle}
                        disabled={!hasApplications}
                    >
                        <LayoutGrid aria-hidden="true" size={18} />
                        <span className="pos-applications__trigger-label">{triggerLabel}</span>
                        <ChevronDown aria-hidden="true" size={16} className="pos-applications__chevron" />
                    </button>
                </DropdownMenu.Trigger>

                <DropdownMenu.Portal container={overlayContainer ?? undefined}>
                    <DropdownMenu.Content
                        dir={dir}
                        align="start"
                        side="bottom"
                        sideOffset={6}
                        collisionPadding={8}
                        aria-label={t('applications.menuLabel', 'Applications')}
                        className="pos-applications__content"
                    >
                        <DropdownMenu.Label className="pos-applications__title">
                            {t('applications.menuLabel', 'Applications')}
                        </DropdownMenu.Label>
                        {applications.map(application => {
                            const Icon = APPLICATION_ICONS[application.id]
                            const isActive = application.id === activeApplicationId
                            return (
                                <DropdownMenu.Item
                                    key={application.id}
                                    className={`pos-applications__item${isActive ? ' pos-applications__item--active' : ''}`}
                                    aria-current={isActive ? 'page' : undefined}
                                    data-active={isActive ? 'true' : undefined}
                                    onSelect={() => selectApplication(application)}
                                >
                                    <span className="pos-applications__item-icon" aria-hidden="true">
                                        <Icon size={18} />
                                    </span>
                                    <span className="pos-applications__item-label">
                                        <bdi>{application.label}</bdi>
                                    </span>
                                    {isActive ? (
                                        <span className="pos-applications__item-state">
                                            <Badge variant="brand" size="sm">{t('common.active', 'Active')}</Badge>
                                            <Check aria-hidden="true" size={16} />
                                        </span>
                                    ) : null}
                                </DropdownMenu.Item>
                            )
                        })}
                    </DropdownMenu.Content>
                </DropdownMenu.Portal>
            </DropdownMenu.Root>
        </div>
    )
}
