import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../../../../services/api'
import { useAuthStore } from '../../../../store/authStore'
import { useDashboardActorScope, useDashboardUtcDay } from '../../../../components/dashboard/usePosDashboardData'

// Reporting-only provenance. The backend still decides permissions, periods and
// financial definitions. Hide an old scope during render, not after an effect.
export function useFinancialAnalyticsSource(url, params, valid) {
    const { t } = useTranslation()
    const actor = useDashboardActorScope()
    const day = useDashboardUtcDay()
    const query = JSON.stringify(params)
    const scope = JSON.stringify([actor, day, url, query])
    const currentScope = useRef(scope)
    currentScope.current = scope
    const sequence = useRef(0)
    const [state, setState] = useState({ scope, data: null, loading: true, error: null })
    const refetch = useCallback(async () => {
        const id = ++sequence.current
        const active = () => sequence.current === id && currentScope.current === scope
            && JSON.stringify(useAuthStore.getState().user) === actor
        setState({ scope, data: null, loading: true, error: null })
        try {
            const response = await api.get(url, { params: JSON.parse(query) })
            if (!active()) return false
            if (!valid(response.data)) throw new Error('Invalid financial analytics response')
            setState({ scope, data: response.data, loading: false, error: null })
            return true
        } catch {
            if (!active()) return false
            // Includes 401/403, malformed payloads and failed refreshes. No stale
            // amounts, fabricated empty rows or raw server diagnostics survive.
            setState({ scope, data: null, loading: false, error: t('analytics.dataUnavailable', { defaultValue: 'Analytics data is unavailable. Please retry.' }) })
            return false
        }
    }, [actor, query, scope, t, url, valid])
    useEffect(() => {
        void refetch()
        return () => { sequence.current++ }
    }, [refetch])
    return { ...(state.scope === scope ? state : { data: null, loading: true, error: null }), refetch }
}
