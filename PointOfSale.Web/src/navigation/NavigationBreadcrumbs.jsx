import { useContext, useEffect } from 'react'
import { matchRoutes, useLocation } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useRTL } from '../hooks/useRTL'
import { AdminRouteContext, useSmartNavigation } from './SmartNavigationContext'
import { resolveBreadcrumbContext } from './navigationPresentation'
import { resolveStorefrontScreen, StorefrontBreadcrumbs } from '../modules/storefront/informationArchitecture'

export default function NavigationBreadcrumbs() {
    const { entries } = useSmartNavigation()
    const routes = useContext(AdminRouteContext)
    const { language } = useRTL()
    const { t } = useTranslation()
    const location = useLocation()
    const existing = resolveStorefrontScreen(location.pathname, location.search)
    const ownsBreadcrumb = matchRoutes(routes, location)?.some(match => match.route.handle?.ownsNavigationBreadcrumb)
        || (existing && location.pathname === '/settings')
    const context = existing || ownsBreadcrumb
        ? null
        : resolveBreadcrumbContext(entries, location.pathname, location.search)
    const trail = context
        ? context.kind === 'parent'
            ? [...context.entry.breadcrumbs[language], t(context.currentKey)]
            : context.entry.breadcrumbs[language]
        : []
    const title = !context
        ? null
        : context.kind === 'parent'
            ? `${t(context.currentKey)} | ${context.entry.modules[language]}`
            : `${context.entry.labels[language]} | ${context.entry.modules[language]}`
    useEffect(() => {
        if (!title) return
        const previous = document.title
        document.title = title
        return () => { document.title = previous }
    }, [title])
    if (ownsBreadcrumb) return null
    if (existing) return <StorefrontBreadcrumbs className="pos-shell__storefront-breadcrumbs" />
    if (!context) return null
    return <nav aria-label={t('smartNavigation.breadcrumb')} className="pos-shell__breadcrumbs">
        <ol className="pos-shell__breadcrumb-list">
            <li className="pos-shell__breadcrumb-item"><bdi>{context.entry.modules[language]}</bdi></li>
            {trail.map((label, index) => <li key={`${label}-${index}`} className="pos-shell__breadcrumb-item" data-current={index === trail.length - 1 ? 'true' : undefined}>
                <span aria-hidden="true" className="pos-shell__breadcrumb-separator"> / </span>
                {index === trail.length - 1
                    ? <bdi aria-current="page">{label}</bdi>
                    : <bdi>{label}</bdi>}
            </li>)}
        </ol>
    </nav>
}
