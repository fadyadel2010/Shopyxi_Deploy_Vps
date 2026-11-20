import { Link } from 'react-router-dom'
import { Star, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useRTL } from '../hooks/useRTL'
import { declaredQueryMatches } from './navigationPresentation'

export function SidebarFavorites({ navigation, onNavigate, activePathname, activeSearch }) {
    const { t } = useTranslation()
    const { language } = useRTL()
    const entries = navigation.favorites.map(id => navigation.entries.find(entry => entry.id === id)).filter(Boolean)
    return <details className="pos-sidebar__favorites" data-testid="navigation-favorites">
        <summary className="pos-sidebar__favorites-summary">
            <Star aria-hidden="true" size={16} />
            <span className="pos-sidebar__favorites-title">{t('smartNavigation.favorites')} ({entries.length})</span>
        </summary>
        <ul className="pos-sidebar__favorites-list">
            {!entries.length && <li>
                <p className="pos-sidebar__favorites-empty">{t('smartNavigation.noFavorites')}</p>
            </li>}
            {entries.map(entry => {
                // Visual current state only; the canonical menu occurrence owns aria-current.
                const isActive = Boolean(activePathname)
                    && activePathname === entry.to.pathname
                    && declaredQueryMatches(entry.to.search, activeSearch)
                return <li key={entry.id} className={`pos-sidebar__favorite${isActive ? ' pos-sidebar__favorite--active' : ''}`}>
                    <Link to={entry.to} onClick={onNavigate} className="pos-sidebar__favorite-link">
                        <bdi className="pos-sidebar__favorite-label">{entry.labels[language]}</bdi>
                        <bdi className="pos-sidebar__favorite-context">{entry.modules[language]}</bdi>
                        <bdi className="pos-sidebar__favorite-trail" title={entry.breadcrumbs[language].join(' / ')}>
                            {entry.breadcrumbs[language].slice(0, -1).join(' / ')}
                        </bdi>
                    </Link>
                    <button type="button" onClick={() => navigation.toggleFavorite(entry.id)} className="pos-sidebar__favorite-remove"
                        aria-label={t('smartNavigation.removeFavorite', { title: entry.labels[language] })}
                        aria-description={`${entry.modules[language]} / ${entry.breadcrumbs[language].join(' / ')}`}>
                        <X aria-hidden="true" size={16} />
                    </button>
                </li>
            })}
        </ul>
    </details>
}
