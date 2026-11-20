import { forwardRef, useCallback, useEffect, useLayoutEffect, useState } from 'react'
import * as RadixDialog from '@radix-ui/react-dialog'
import { X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import IconButton from '../IconButton/IconButton'
import { cx } from '../cx'
import { PosNestedOverlayContext, usePosOverlayElement } from '../../overlays/posOverlayContext'
import {
    isTopOverlayLayer,
    ownsOverlayEscape,
    registerOverlayLayer,
    unregisterOverlayLayer,
} from '../../overlays/overlayLayerStack'
import {
    registerPosDialogContent,
    unregisterPosDialogContent,
} from '../../overlays/posDialogContentStack'
import './Dialog.css'

const Dialog = forwardRef(function Dialog({
    open,
    onOpenChange,
    title,
    description,
    size = 'md',
    trigger,
    initialFocusRef,
    restoreFocusRef,
    closeOnEscape = true,
    closeOnOverlayClick = true,
    showClose = true,
    closeLabel,
    busy = false,
    footer,
    className,
    children,
    onEscapeKeyDown,
    onPointerDownOutside,
    onFocusOutside,
    onOpenAutoFocus,
    onCloseAutoFocus,
    ...rest
}, ref) {
    const { t } = useTranslation()
    const container = usePosOverlayElement()
    const [contentElement, setContentElement] = useState(null)
    const [layerToken] = useState(() => Symbol('pos-dialog-layer'))

    const setContentRef = useCallback((node) => {
        setContentElement(node)
        if (typeof ref === 'function') {
            ref(node)
        } else if (ref) {
            ref.current = node
        }
    }, [ref])

    useLayoutEffect(() => {
        if (!contentElement) {
            return undefined
        }
        registerOverlayLayer(layerToken)
        registerPosDialogContent(contentElement)
        return () => {
            unregisterOverlayLayer(layerToken)
            unregisterPosDialogContent(contentElement)
        }
    }, [contentElement, layerToken])

    useEffect(() => {
        if (import.meta.env.DEV && open && !container) {
            console.warn(
                'POS Dialog is rendering without a POS overlay host. It falls back to document.body, where POS tokens do not resolve.',
            )
        }
    }, [open, container])

    if (import.meta.env.DEV && !title) {
        console.error('Dialog requires an accessible `title` prop.')
    }

    const guardEscape = (event) => {
        // When another registered overlay sits above this dialog (legacy
        // ConfirmModal or a newer dialog), only the top layer may close.
        const ownsEvent = ownsOverlayEscape(event, layerToken)
        onEscapeKeyDown?.(event)
        if (busy || !closeOnEscape || !ownsEvent) {
            event.preventDefault()
        }
    }

    const guardOutside = (event) => {
        onPointerDownOutside?.(event)
        if (busy || !closeOnOverlayClick || !isTopOverlayLayer(layerToken)) {
            event.preventDefault()
        }
    }

    const guardFocusOutside = (event) => {
        onFocusOutside?.(event)
        if (!isTopOverlayLayer(layerToken)) {
            event.preventDefault()
        }
    }

    return (
        <RadixDialog.Root open={open} onOpenChange={(next) => {
            if (!next && busy) return
            onOpenChange?.(next)
        }}>
            {trigger ? (
                <RadixDialog.Trigger asChild>{trigger}</RadixDialog.Trigger>
            ) : null}
            <RadixDialog.Portal container={container ?? undefined}>
                <RadixDialog.Overlay className="pos-dialog__overlay" />
                <RadixDialog.Content
                    {...rest}
                    ref={setContentRef}
                    aria-modal="true"
                    aria-busy={busy || undefined}
                    // Title/Description register their own ids through Radix.
                    // Without a description we must explicitly opt out, or
                    // Radix warns about a dangling aria-describedby.
                    {...(description ? null : { 'aria-describedby': undefined })}
                    className={cx('pos-dialog', `pos-dialog--${size}`, className)}
                    onEscapeKeyDown={guardEscape}
                    onPointerDownOutside={guardOutside}
                    onFocusOutside={guardFocusOutside}
                    onOpenAutoFocus={(event) => {
                        onOpenAutoFocus?.(event)
                        if (event.defaultPrevented || !initialFocusRef?.current) {
                            return
                        }
                        event.preventDefault()
                        initialFocusRef.current.focus({ preventScroll: true })
                    }}
                    onCloseAutoFocus={(event) => {
                        onCloseAutoFocus?.(event)
                        if (event.defaultPrevented) {
                            return
                        }
                        const target = restoreFocusRef?.current
                        if (!target) {
                            // No explicit target: Radix restores to the
                            // registered Dialog trigger when one exists.
                            return
                        }
                        event.preventDefault()
                        if (target.isConnected) {
                            target.focus({ preventScroll: true })
                        }
                    }}
                >
                    <PosNestedOverlayContext.Provider value={contentElement}>
                    <div className="pos-dialog__header">
                        {title ? (
                            <RadixDialog.Title className="pos-dialog__title">
                                {title}
                            </RadixDialog.Title>
                        ) : null}
                        {showClose ? (
                            <RadixDialog.Close asChild>
                                <IconButton
                                    label={closeLabel ?? t('common.close', { defaultValue: 'Close' })}
                                    icon={<X />}
                                    size="sm"
                                    className="pos-dialog__close"
                                    disabled={busy}
                                />
                            </RadixDialog.Close>
                        ) : null}
                    </div>
                    {description ? (
                        <RadixDialog.Description className="pos-dialog__description">
                            {description}
                        </RadixDialog.Description>
                    ) : null}
                    <div className="pos-dialog__body">{children}</div>
                    {footer ? (
                        <div className="pos-dialog__footer">{footer}</div>
                    ) : null}
                    </PosNestedOverlayContext.Provider>
                </RadixDialog.Content>
            </RadixDialog.Portal>
        </RadixDialog.Root>
    )
})

export const DialogClose = forwardRef(function DialogClose({ className, children, ...rest }, ref) {
    return (
        <RadixDialog.Close ref={ref} className={cx('pos-dialog__close-action', className)} {...rest}>
            {children}
        </RadixDialog.Close>
    )
})

export default Dialog
