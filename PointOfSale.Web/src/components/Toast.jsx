import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle2, CircleAlert, Info, TriangleAlert, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useRTL } from '../hooks/useRTL'
import { usePosSurface } from '../design-system/overlays/posOverlayContext'
import '../design-system/providers/posProviderBridge.css'

const ToastContext = createContext()

export function ToastProvider({ children }) {
    const [toasts, setToasts] = useState([])
    const timeoutsRef = useRef(new Map())

    const removeToast = useCallback((id) => {
        const timeoutId = timeoutsRef.current.get(id)
        if (timeoutId !== undefined) {
            clearTimeout(timeoutId)
            timeoutsRef.current.delete(id)
        }

        setToasts((prev) => prev.filter((toast) => toast.id !== id))
    }, [])

    const addToast = useCallback((message, type = 'info', duration = 3000, surface = 'other') => {
        if (duration && typeof duration === 'object') {
            surface = duration.surface ?? surface
            duration = duration.duration ?? 3000
        }
        const id = Date.now() + Math.random()
        const toast = { id, message, type, duration, surface }

        setToasts((prev) => [...prev, toast])

        if (duration > 0) {
            const timeoutId = setTimeout(() => {
                removeToast(id)
            }, duration)

            timeoutsRef.current.set(id, timeoutId)
        }
    }, [removeToast])

    useEffect(() => {
        const timeouts = timeoutsRef.current
        return () => {
            timeouts.forEach((timeoutId) => {
                clearTimeout(timeoutId)
            })
            timeouts.clear()
        }
    }, [])

    const showSuccess = useCallback((message, duration, surface) => {
        addToast(message, 'success', duration, surface)
    }, [addToast])

    const showError = useCallback((message, duration, surface) => {
        addToast(message, 'error', duration, surface)
    }, [addToast])

    const showInfo = useCallback((message, duration, surface) => {
        addToast(message, 'info', duration, surface)
    }, [addToast])

    const showWarning = useCallback((message, duration, surface) => {
        addToast(message, 'warning', duration, surface)
    }, [addToast])

    const contextValue = useMemo(() => ({ showSuccess, showError, showInfo, showWarning }), [
        showSuccess, showError, showInfo, showWarning,
    ])

    return (
        <ToastContext.Provider value={contextValue}>
            {children}
            <ToastContainer toasts={toasts} onRemove={removeToast} />
        </ToastContext.Provider>
    )
}

export function useToast({ surface: explicitSurface } = {}) {
    const context = useContext(ToastContext)
    const inheritedSurface = usePosSurface()
    const surface = explicitSurface ?? inheritedSurface
    const scopedContext = useMemo(() => {
        if (!context || surface !== 'pos') {
            return context
        }

        return {
            showSuccess: (message, duration) => context.showSuccess(message, duration, surface),
            showError: (message, duration) => context.showError(message, duration, surface),
            showInfo: (message, duration) => context.showInfo(message, duration, surface),
            showWarning: (message, duration) => context.showWarning(message, duration, surface),
        }
    }, [context, surface])

    if (!context) {
        throw new Error('useToast must be used within ToastProvider')
    }
    return scopedContext
}

function ToastContainer({ toasts, onRemove }) {
    const { isRTL } = useRTL()

    return (
        <div className={`fixed top-4 z-50 space-y-2 pointer-events-none ${isRTL ? 'left-4' : 'right-4'}`}>
            {toasts.map((toast) => (
                <div
                    key={toast.id}
                    aria-live={toast.type === 'error' ? 'assertive' : 'polite'}
                    role={toast.type === 'error' ? 'alert' : 'status'}
                    aria-atomic="true"
                    data-product={toast.surface === 'pos' ? 'pos' : undefined}
                >
                    <Toast toast={toast} onRemove={onRemove} />
                </div>
            ))}
        </div>
    )
}

function Toast({ toast, onRemove }) {
    const { id, message, type } = toast
    const { dir, isRTL } = useRTL()
    const isPos = toast.surface === 'pos'
    const { t } = useTranslation()

    const styles = {
        success: 'bg-green-500 text-white',
        error: 'bg-red-500 text-white',
        warning: 'bg-yellow-500 text-white',
        info: 'bg-blue-500 text-white',
    }

    const icons = {
        success: CheckCircle2,
        error: CircleAlert,
        warning: TriangleAlert,
        info: Info,
    }

    const Icon = icons[type] || Info

    return (
        <div
            dir={dir}
            className={isPos
                ? `pos-toast pos-toast--${type} pointer-events-auto ${isRTL ? 'animate-slide-in-left' : 'animate-slide-in-right'}`
                : `${styles[type]} px-6 py-3 rounded-lg shadow-lg flex items-center gap-3 min-w-[300px] max-w-md pointer-events-auto ${isRTL ? 'animate-slide-in-left' : 'animate-slide-in-right'}`}
            onClick={() => onRemove(id)}
        >
            <Icon aria-hidden="true" className={isPos ? 'pos-toast__icon' : 'h-5 w-5 shrink-0'} />
            <div className={isPos ? 'pos-toast__message' : 'flex-1 text-sm font-medium'}>{message}</div>
            <button
                type="button"
                onClick={(event) => {
                    event.stopPropagation()
                    onRemove(id)
                }}
                className={isPos ? 'pos-toast__dismiss' : 'text-white hover:text-gray-200 transition-colors'}
                aria-label={t('common.dismissNotification', { defaultValue: isRTL ? 'إغلاق الإشعار' : 'Dismiss notification' })}
            >
                <X aria-hidden="true" className={isPos ? 'pos-toast__close-icon' : 'h-4 w-4'} />
            </button>
        </div>
    )
}
