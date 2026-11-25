import { useEffect, useId, useMemo, useRef, useState } from 'react'
import * as Dialog from '@radix-ui/react-dialog'
import { useLocation, useNavigate } from 'react-router-dom'
import { Search, Star, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useRTL } from '../hooks/useRTL'
import { useSmartNavigation } from './SmartNavigationContext'
import { searchNavigation } from './navigationRegistry'
import { FAVORITES_LIMIT } from './navigationPreferences'
import { usePosOverlayElement } from '../design-system/overlays/posOverlayContext'

export function canOpenNavigationShortcut(event, document) {
    if (!event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || event.repeat || event.isComposing
        || event.key.toLowerCase() !== 'k' || event.defaultPrevented) return false
    const target = event.target
    if (target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"]')) return false
    return ![...document.querySelectorAll('[role="dialog"], [aria-modal="true"]')]
        .some(dialog => dialog.getAttribute('data-state') !== 'closed' && !dialog.hidden)
}

export default function CommandPalette({ onNavigate }) {
    const navigation = useSmartNavigation()
    const { t } = useTranslation()
    const { language, dir } = useRTL()
    const navigate = useNavigate()
    const location = useLocation()
    const [open, setOpen] = useState(false)
    const [query, setQuery] = useState('')
    const [tab, setTab] = useState('all')
    const [selected, setSelected] = useState(0)
    const input = useRef(null)
    const restoreFocus = useRef(null)
    const pendingLocation = useRef(null)
    const listId = useId()
    const overlayContainer = usePosOverlayElement()
    const openPalette = () => {
        restoreFocus.current = document.activeElement
        setQuery('')
        setTab('all')
        setSelected(0)
        setOpen(true)
    }
    useEffect(() => {
        const shortcut = event => {
            if (!canOpenNavigationShortcut(event, document)) return
            event.preventDefault()
            openPalette()
        }
        window.addEventListener('keydown', shortcut)
        return () => window.removeEventListener('keydown', shortcut)
    }, [])
    // Drop stale dialog/search state immediately when session authority changes.
    useEffect(() => { setOpen(false); setQuery(''); pendingLocation.current = null }, [navigation.user])
    useEffect(() => {
        if (pendingLocation.current !== null && location.key !== pendingLocation.current) {
            pendingLocation.current = null
            setOpen(false)
        }
    }, [location.key])

    const results = useMemo(() => {
        const byId = ids => ids.map(id => navigation.entries.find(entry => entry.id === id)).filter(Boolean)
        const candidates = tab === 'favorites' ? byId(navigation.favorites)
            : tab === 'recent' ? byId(navigation.recent)
                : !query.trim() ? [...byId(navigation.recent), ...navigation.entries.filter(entry => !navigation.recent.includes(entry.id))]
                    : navigation.entries
        return searchNavigation(candidates, query)
    }, [navigation.entries, navigation.favorites, navigation.recent, query, tab])
    const active = Math.min(selected, Math.max(0, results.length - 1))
    useEffect(() => { document.getElementById(`${listId}-${active}`)?.scrollIntoView?.({ block: 'nearest' }) }, [active, listId, query, tab])
    const launch = entry => {
        // Revalidate identity against current authority; router still owns admission.
        const current = navigation.entries.find(value => value.id === entry.id)
        if (!current) return
        if (location.pathname === current.to.pathname && location.search === (current.to.search || '')) {
            setOpen(false)
            return
        }
        pendingLocation.current = location.key
        navigate(current.to)
        onNavigate?.()
    }
    const keyboard = event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            if (results.length) setSelected((active + (event.key === 'ArrowDown' ? 1 : -1) + results.length) % results.length)
        } else if (event.key === 'Enter' && results[active] && !event.nativeEvent.isComposing) {
            event.preventDefault()
            launch(results[active])
        }
    }
    return <Dialog.Root open={open} onOpenChange={value => value ? openPalette() : setOpen(false)}>
        <Dialog.Trigger asChild>
            <button type="button" title={t('smartNavigation.search')} aria-label={t('smartNavigation.search')}
                aria-keyshortcuts="Control+K" className="pos-palette__trigger">
                <Search aria-hidden="true" size={18} />
                <span className="pos-palette__trigger-label">{t('smartNavigation.search')}</span>
            </button>
        </Dialog.Trigger>
        <Dialog.Portal container={overlayContainer ?? undefined}>
            <Dialog.Overlay className="pos-palette__overlay" />
            <Dialog.Content dir={dir} data-testid="navigation-palette" onOpenAutoFocus={event => { event.preventDefault(); input.current?.focus() }}
                onCloseAutoFocus={event => { event.preventDefault(); if (restoreFocus.current?.isConnected) restoreFocus.current.focus() }}
                className="pos-palette__content">
                <div className="pos-palette__header">
                    <Dialog.Title className="pos-palette__title">{t('smartNavigation.title')}</Dialog.Title>
                    <Dialog.Close aria-label={t('smartNavigation.close')} className="pos-palette__close"><X size={20} /></Dialog.Close>
                </div>
                <Dialog.Description className="pos-palette__description">{t('smartNavigation.instructions')}</Dialog.Description>
                <div className="pos-palette__search">
                    <Search aria-hidden="true" size={16} />
                    <input ref={input} value={query} maxLength={200} onChange={event => { setQuery(event.target.value); setSelected(0) }} onKeyDown={keyboard}
                        role="combobox" aria-expanded="true" aria-haspopup="grid" aria-controls={listId}
                        aria-activedescendant={results.length ? `${listId}-${active}` : undefined}
                        aria-label={t('smartNavigation.search')} placeholder={t('smartNavigation.placeholder')} autoComplete="off"
                        className="pos-palette__input" />
                </div>
                <div className="pos-palette__tabs">
                    {['all', 'favorites', 'recent'].map(value => <button type="button" key={value} aria-pressed={tab === value}
                        onClick={() => { setTab(value); setSelected(0); input.current?.focus() }}
                        className={`pos-palette__tab${tab === value ? ' pos-palette__tab--active' : ''}`}>
                        {t(`smartNavigation.${value}`)}
                    </button>)}
                    {(tab === 'recent' || (tab === 'all' && !query.trim())) && navigation.recent.length > 0 && <button type="button"
                        onClick={navigation.clearRecent} className="pos-palette__clear">{t('smartNavigation.clearRecent')}</button>}
                </div>
                <p role="status" className="pos-palette__status">{t('smartNavigation.resultCount', { count: results.length })}</p>
                <div className="pos-palette__results" id={listId} role="grid" aria-label={t('smartNavigation.results')}>
                    {!results.length && <div role="row"><div role="gridcell" className="pos-palette__empty">{t(query.trim() ? 'smartNavigation.noResults'
                        : tab === 'favorites' ? 'smartNavigation.noFavorites' : tab === 'recent' ? 'smartNavigation.noRecent' : 'smartNavigation.unavailable')}</div></div>}
                    {results.map((entry, index) => <div key={entry.id}>
                        {tab === 'all' && !query.trim() && (index === 0 || (navigation.recent.includes(results[index - 1]?.id) && !navigation.recent.includes(entry.id)))
                            && <div role="row"><div role="gridcell" aria-colspan="2"><h3 className="pos-palette__group">{t(navigation.recent.includes(entry.id) ? 'smartNavigation.recent' : 'smartNavigation.all')}</h3></div></div>}
                        <div role="row" id={`${listId}-${index}`} aria-selected={index === active} data-navigation-id={entry.id}
                            className={`pos-palette__row${index === active ? ' pos-palette__row--active' : ''}`}>
                            <div role="gridcell" className="pos-palette__cell">
                                <button type="button" onClick={() => launch(entry)}
                                    aria-label={`${entry.labels[language]} — ${entry.modules[language]} — ${entry.breadcrumbs[language].join(' / ')}`}
                                    className="pos-palette__result">
                                    <span className="pos-palette__result-label"><bdi>{entry.labels[language]}</bdi></span>
                                    <span className="pos-palette__result-module"><bdi>{entry.modules[language]}</bdi></span>
                                    <span className="pos-palette__result-trail">{entry.breadcrumbs[language].map((label, i) => <span key={i}>{i > 0 && <span aria-hidden="true"> / </span>}<bdi>{label}</bdi></span>)}</span>
                                </button>
                            </div>
                            <div role="gridcell" className="pos-palette__favorite-cell">
                                <button type="button" aria-pressed={navigation.favorites.includes(entry.id)}
                                    disabled={!navigation.favorites.includes(entry.id) && navigation.favorites.length >= FAVORITES_LIMIT}
                                    aria-label={t(navigation.favorites.includes(entry.id) ? 'smartNavigation.removeFavorite' : 'smartNavigation.addFavorite', { title: entry.labels[language] })}
                                    title={navigation.favorites.length >= FAVORITES_LIMIT ? t('smartNavigation.favoriteLimit', { count: FAVORITES_LIMIT }) : undefined}
                                    onClick={() => navigation.toggleFavorite(entry.id)}
                                    className={`pos-palette__favorite${navigation.favorites.includes(entry.id) ? ' pos-palette__favorite--active' : ''}`}>
                                    <Star aria-hidden="true" size={18} fill={navigation.favorites.includes(entry.id) ? 'currentColor' : 'none'} />
                                </button>
                            </div>
                        </div>
                    </div>)}
                </div>
            </Dialog.Content>
        </Dialog.Portal>
    </Dialog.Root>
}
