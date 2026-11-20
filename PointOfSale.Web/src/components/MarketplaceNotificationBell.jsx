import { useState, useEffect, useLayoutEffect, useRef, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { Bell } from 'lucide-react'
import { useRTL } from '../hooks/useRTL'
import api from '../services/api'
import { useAuthStore } from '../store/authStore'
import { getScopedStorageKey, safeReadJson, safeWriteJson } from '../utils/storageScope'
import { isServiceMarketplaceWorkflowActor } from '../marketplace/authority'
import { Badge, Button, IconButton } from '../design-system/primitives'

const SEEN_REQUESTS_KEY_PREFIX = 'marketplace_seen_request_ids'
const PANEL_WIDTH = 320
const PANEL_MAX_HEIGHT = 288
const PANEL_GUTTER = 8
const PANEL_MIN_VISIBLE_HEIGHT = 96

export function computeNotificationPanelPosition({
    triggerRect,
    panelWidth = PANEL_WIDTH,
    viewportWidth,
    viewportHeight,
    isRTL,
    gutter = PANEL_GUTTER,
    maxPanelHeight = PANEL_MAX_HEIGHT,
}) {
    const width = Math.max(0, Math.min(panelWidth, viewportWidth - gutter * 2))
    const alignedLeft = isRTL ? triggerRect.left : triggerRect.right - width
    const maxLeft = Math.max(gutter, viewportWidth - width - gutter)
    const left = Math.min(Math.max(alignedLeft, gutter), maxLeft)
    const top = Math.max(gutter, Math.min(triggerRect.bottom + gutter, viewportHeight - gutter - PANEL_MIN_VISIBLE_HEIGHT))
    const maxHeight = Math.max(
        PANEL_MIN_VISIBLE_HEIGHT,
        Math.min(maxPanelHeight, viewportHeight - top - gutter),
    )
    return {
        left: Math.round(left),
        top: Math.round(top),
        width: Math.round(width),
        maxHeight: Math.round(maxHeight),
    }
}

function getSeenRequestsStorageKey() {
    return getScopedStorageKey(SEEN_REQUESTS_KEY_PREFIX, 'seen')
}

function getSeenRequestIds() {
    if (typeof localStorage === 'undefined') {
        return new Set()
    }

    const stored = safeReadJson(localStorage, getSeenRequestsStorageKey(), [])
    return Array.isArray(stored) ? new Set(stored) : new Set()
}

function markRequestsAsSeen(requestIds) {
    if (typeof localStorage === 'undefined') {
        return
    }

    const seen = getSeenRequestIds()
    requestIds.forEach((id) => seen.add(id))
    safeWriteJson(localStorage, getSeenRequestsStorageKey(), [...seen])
}

function cleanupSeenIds(allPendingIds) {
    if (typeof localStorage === 'undefined') {
        return
    }

    const seen = getSeenRequestIds()
    const stillRelevant = [...seen].filter((id) => allPendingIds.has(id))
    safeWriteJson(localStorage, getSeenRequestsStorageKey(), stillRelevant)
}

export default function MarketplaceNotificationBell() {
    const { t } = useTranslation()
    const { dir, isRTL, locale } = useRTL()
    const navigate = useNavigate()
    const [unseenCount, setUnseenCount] = useState(0)
    const [showDropdown, setShowDropdown] = useState(false)
    const [unseenRequests, setUnseenRequests] = useState([])
    const [panelPosition, setPanelPosition] = useState(null)
    const dropdownRef = useRef(null)
    const bellRef = useRef(null)
    const allPendingIdsRef = useRef([])
    const isAuthenticated = useAuthStore((state) => state.isAuthenticated)
    const user = useAuthStore((state) => state.user)
    const isMarketplaceActor = isServiceMarketplaceWorkflowActor(user, isAuthenticated)
    const actorScope = `${user?.tenantId || ''}:${user?.id || user?.userId || ''}:${user?.role || ''}`
    const activeScope = useRef(null)

    const fetchIncomingRequests = useCallback(async () => {
        try {
            if (!isMarketplaceActor) {
                setUnseenCount(0)
                setUnseenRequests([])
                return
            }

            const response = await api.get('/marketplace/incoming-requests')
            if (activeScope.current !== actorScope) return
            const data = response.data || []
            const pending = data.filter((request) => request.status === 'Pending')
            const pendingIds = new Set(pending.map((request) => request.requestId))
            allPendingIdsRef.current = pending.map((request) => request.requestId)

            cleanupSeenIds(pendingIds)

            const seen = getSeenRequestIds()
            const unseen = pending.filter((request) => !seen.has(request.requestId))

            setUnseenCount(unseen.length)
            setUnseenRequests(unseen.slice(0, 5))
        } catch {
            if (activeScope.current !== actorScope) return
            setUnseenCount(0)
            setUnseenRequests([])
        }
    }, [actorScope, isMarketplaceActor])

    useEffect(() => {
        activeScope.current = actorScope
        setUnseenCount(0)
        setUnseenRequests([])
        setShowDropdown(false)
        fetchIncomingRequests()
        const interval = setInterval(fetchIncomingRequests, 30000)
        return () => { activeScope.current = null; clearInterval(interval) }
    }, [actorScope, fetchIncomingRequests])

    useEffect(() => {
        const handleClickOutside = (event) => {
            if (
                dropdownRef.current &&
                !dropdownRef.current.contains(event.target) &&
                bellRef.current &&
                !bellRef.current.contains(event.target)
            ) {
                setShowDropdown(false)
            }
        }

        document.addEventListener('mousedown', handleClickOutside)
        return () => document.removeEventListener('mousedown', handleClickOutside)
    }, [])

    useEffect(() => {
        if (!showDropdown) {
            return undefined
        }

        const handleKeydown = (event) => {
            if (event.key !== 'Escape') {
                return
            }
            setShowDropdown(false)
            bellRef.current?.focus()
        }

        document.addEventListener('keydown', handleKeydown)
        return () => document.removeEventListener('keydown', handleKeydown)
    }, [showDropdown])

    useLayoutEffect(() => {
        if (!showDropdown || unseenCount === 0) {
            setPanelPosition(null)
            return undefined
        }

        const updatePosition = () => {
            const trigger = bellRef.current
            if (!trigger) {
                return
            }
            setPanelPosition(computeNotificationPanelPosition({
                triggerRect: trigger.getBoundingClientRect(),
                viewportWidth: window.innerWidth,
                viewportHeight: window.innerHeight,
                isRTL,
            }))
        }

        updatePosition()
        window.addEventListener('resize', updatePosition)
        window.addEventListener('scroll', updatePosition, true)
        return () => {
            window.removeEventListener('resize', updatePosition)
            window.removeEventListener('scroll', updatePosition, true)
        }
    }, [showDropdown, unseenCount, isRTL])

    const markAllAsSeen = useCallback(() => {
        if (allPendingIdsRef.current.length > 0) {
            markRequestsAsSeen(allPendingIdsRef.current)
        }
        setUnseenCount(0)
        setUnseenRequests([])
        setShowDropdown(false)
    }, [])

    const handleBellClick = () => {
        if (unseenCount > 0) {
            setShowDropdown((current) => !current)
            return
        }

        navigate('/marketplace?tab=incoming')
    }

    const handleViewAll = () => {
        markAllAsSeen()
        navigate('/marketplace?tab=incoming')
    }

    const handleRequestClick = () => {
        markAllAsSeen()
        navigate('/marketplace?tab=incoming')
    }

    const getTimeAgo = useCallback((dateStr) => {
        const date = new Date(dateStr)
        if (Number.isNaN(date.getTime())) {
            return ''
        }

        const relativeFormatter = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
        const diffMs = date.getTime() - Date.now()
        const absDiffMs = Math.abs(diffMs)

        if (absDiffMs < 60000) {
            return relativeFormatter.format(0, 'second')
        }

        if (absDiffMs < 3600000) {
            return relativeFormatter.format(Math.round(diffMs / 60000), 'minute')
        }

        if (absDiffMs < 86400000) {
            return relativeFormatter.format(Math.round(diffMs / 3600000), 'hour')
        }

        return relativeFormatter.format(Math.round(diffMs / 86400000), 'day')
    }, [locale])

    if (!isMarketplaceActor) {
        return null
    }

    const triggerLabel = unseenCount > 0
        ? t('servicesMarketplace.notifications.newRequestsTitle', { count: unseenCount })
        : t('servicesMarketplace.notifications.marketplaceRequests')

    return (
        <div className="pos-notifications" dir={dir}>
            <IconButton
                ref={bellRef}
                type="button"
                className="pos-notifications__trigger"
                label={triggerLabel}
                title={triggerLabel}
                onClick={handleBellClick}
                aria-expanded={unseenCount > 0 ? showDropdown : undefined}
                aria-controls={unseenCount > 0 ? 'pos-marketplace-notifications' : undefined}
                icon={(
                    <>
                        <Bell size={20} />
                        {unseenCount > 0 ? (
                            <span className="pos-notifications__count" aria-hidden="true">
                                {unseenCount > 9 ? '9+' : unseenCount}
                            </span>
                        ) : null}
                    </>
                )}
            />

            {showDropdown && unseenCount > 0 ? (
                <div
                    ref={dropdownRef}
                    id="pos-marketplace-notifications"
                    role="region"
                    aria-label={t('servicesMarketplace.incoming.title')}
                    className="pos-notifications__panel"
                    style={panelPosition ? {
                        left: panelPosition.left,
                        top: panelPosition.top,
                        width: panelPosition.width,
                        maxHeight: panelPosition.maxHeight,
                    } : undefined}
                >
                    <div className="pos-notifications__heading">
                        <h2 className="pos-notifications__title">{t('servicesMarketplace.incoming.title')}</h2>
                        <Badge variant="brand" size="sm">
                            {t('servicesMarketplace.notifications.newCount', { count: unseenCount })}
                        </Badge>
                    </div>

                    <ul className="pos-notifications__list">
                        {unseenRequests.map((request) => (
                            <li key={request.requestId}>
                                <button
                                    onClick={handleRequestClick}
                                    className="pos-notifications__item pos-notifications__item--unread"
                                    type="button"
                                >
                                    <p className="pos-notifications__message">
                                        {request.serviceTitle || t('servicesMarketplace.requestCard.serviceRequest')}
                                    </p>
                                    <p className="pos-notifications__meta">
                                        {t('servicesMarketplace.status.pending')}
                                        {' · '}
                                        {getTimeAgo(request.createdAt)}
                                    </p>
                                </button>
                            </li>
                        ))}
                    </ul>

                    <div className="pos-notifications__footer">
                        <Button variant="ghost" size="sm" onClick={handleViewAll}>
                            {t('servicesMarketplace.notifications.viewAll')}
                        </Button>
                    </div>
                </div>
            ) : null}
        </div>
    )
}
