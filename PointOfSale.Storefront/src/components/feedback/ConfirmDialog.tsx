/**
 * Confirm Dialog Component
 * 
 * Lightweight confirmation dialog for destructive actions.
 * Inline/inline-modal approach - not a full modal system.
 * 
 * Accessibility:
 * - Focus trap while open
 * - ESC key closes dialog
 * - Focus restored on close
 */

import React, { useEffect, useId, useRef, useState, useCallback } from 'react';
import { Button } from '@/components/common';
import { useTranslation } from '@/i18n';
import { useFocusTrap, useBodyScrollLock } from '@/hooks/useScrollPosition';

interface ConfirmDialogProps {
    /** Whether the dialog is open */
    isOpen: boolean;
    /** Title text */
    title: string;
    /** Message text */
    message: string;
    /** Confirm button text */
    confirmText?: string;
    /** Cancel button text */
    cancelText?: string;
    /** Confirm variant */
    confirmVariant?: 'primary' | 'secondary';
    /** Whether confirming is in progress */
    isLoading?: boolean;
    /** Confirm callback */
    onConfirm: () => void | Promise<void>;
    /** Cancel callback */
    onCancel: () => void;
}

/**
 * Confirm Dialog Component
 */
export const ConfirmDialog: React.FC<ConfirmDialogProps> = ({
    isOpen,
    title,
    message,
    confirmText,
    cancelText,
    confirmVariant = 'primary',
    isLoading = false,
    onConfirm,
    onCancel,
}) => {
    const dialogRef = useRef<HTMLDivElement>(null);
    const { t, direction } = useTranslation();
    const id = useId();
    const activationRef = useRef(false);
    const [localPending, setLocalPending] = useState(false);
    const pending = isLoading || localPending;
    useFocusTrap(dialogRef, isOpen);
    useBodyScrollLock(isOpen);
    useEffect(() => {
        if (isOpen && pending) dialogRef.current?.focus();
    }, [isOpen, pending]);
    const handleConfirm = async () => {
        if (pending || activationRef.current) return;
        activationRef.current = true;
        setLocalPending(true);
        try { await onConfirm(); } finally {
            activationRef.current = false;
            setLocalPending(false);
        }
    };
    useEffect(() => {
        if (!isOpen) return;
        const handleKeyDown = (event: KeyboardEvent) => {
            if (event.key === 'Escape' && !pending && !activationRef.current) {
                event.preventDefault();
                onCancel();
            }
        };
        document.addEventListener('keydown', handleKeyDown);
        return () => document.removeEventListener('keydown', handleKeyDown);
    }, [isOpen, pending, onCancel]);

    // Stable cancel handler for backdrop click
    const handleBackdropClick = useCallback(() => {
        if (!pending && !activationRef.current) {
            onCancel();
        }
    }, [pending, onCancel]);

    if (!isOpen) {
        return null;
    }

    return (
        <>
            {/* Backdrop */}
            <div
                className="fixed inset-0 bg-black/40 z-40 animate-fade-in motion-reduce:animate-none"
                onClick={handleBackdropClick}
                aria-hidden="true"
            />

            {/* Dialog */}
            <div
                className="fixed inset-0 z-50 flex items-end sm:items-center justify-center p-4"
                role="dialog"
                dir={direction}
                aria-busy={pending || undefined}
                aria-modal="true"
                aria-labelledby={`${id}-title`}
                aria-describedby={`${id}-message`}
                onClick={event => { if (event.target === event.currentTarget) handleBackdropClick(); }}
            >
                <div
                    ref={dialogRef}
                    tabIndex={-1}
                    className="bg-background rounded-t-xl sm:rounded-xl shadow-xl w-full max-w-sm animate-slide-in-up sm:animate-scale-in motion-reduce:animate-none"
                    onClick={(e) => e.stopPropagation()}
                >
                    <div className="p-6">
                        {/* Title */}
                        <h3
                            id={`${id}-title`}
                            className="text-lg font-semibold text-text mb-2"
                        >
                            {title}
                        </h3>

                        {/* Message */}
                        <p id={`${id}-message`} className="text-muted text-sm mb-6">
                            {message}
                        </p>

                        {/* Actions */}
                        <div className="flex gap-3">
                            <Button
                                variant="secondary"
                                size="md"
                                fullWidth
                                onClick={handleBackdropClick}
                                disabled={pending}
                            >
                                {cancelText || t('common.cancel')}
                            </Button>
                            <Button
                                variant={confirmVariant}
                                size="md"
                                fullWidth
                                onClick={handleConfirm}
                                loading={pending}
                            >
                                {confirmText || t('common.confirm')}
                            </Button>
                        </div>
                    </div>

                    {/* Mobile bottom sheet handle */}
                    <div className="sm:hidden w-12 h-1 bg-muted/30 rounded-full mx-auto mb-2" />
                </div>
            </div>
        </>
    );
};
