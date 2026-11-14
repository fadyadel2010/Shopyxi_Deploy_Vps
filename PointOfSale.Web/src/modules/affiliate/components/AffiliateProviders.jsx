import { useEffect } from 'react'
import { Outlet, useNavigate, useLocation } from 'react-router-dom'
import { setAffiliateLoginNavigator } from '../../../services/affiliateAuthApi'
import { ThemeProvider } from '../theme/ThemeContext'
import { LanguageProvider, useLanguage } from '../i18n/LanguageContext'
import '../affiliateLocale.css'

function PublicMetadata() {
    const { pathname } = useLocation()
    const { t } = useLanguage()
    useEffect(() => {
        const titles = { login: 'login.title', register: 'register.title', 'forgot-password': 'identity.forgot.title', 'reset-password': 'identity.reset.title', 'verify-email': 'identity.verify.title' }
        const segment = pathname.split('/')[2]
        if (segment !== 'category' && segment !== 'products') {
            document.title = titles[segment] ? `${t(titles[segment])} | ${t('public.brand')}` : t('public.brand')
        }
        let meta = document.querySelector('meta[name="description"]')
        const previous = meta?.content
        const created = !meta
        if (!meta) { meta = document.createElement('meta'); meta.name = 'description'; document.head.appendChild(meta) }
        meta.content = t('landing.subtext')
        return () => { if (created) meta.remove(); else meta.content = previous }
    }, [pathname, t])
    return null
}

/**
 * AffiliateProviders – layout wrapper for all public affiliate routes.
 * Renders ThemeProvider > LanguageProvider > <Outlet /> so child routes
 * automatically inherit both contexts without repeating the wrapper pair.
 */
export default function AffiliateProviders() {
    const navigate = useNavigate()

    useEffect(() => {
        setAffiliateLoginNavigator((path) => navigate(path, { replace: true }))

        return () => {
            setAffiliateLoginNavigator((path) => {
                window.location.href = path
            })
        }
    }, [navigate])

    return (
        <ThemeProvider>
            <LanguageProvider>
                <PublicMetadata />
                <div className="affiliate-public"><Outlet /></div>
            </LanguageProvider>
        </ThemeProvider>
    )
}
