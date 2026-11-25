import type { ReactElement, ReactNode } from 'react';
export interface ConfirmOptions {
    title?: string; message?: ReactNode; confirmText?: string; cancelText?: string;
    variant?: string; isDangerous?: boolean; loadingText?: string;
    description?: ReactNode; confirmLabel?: string; cancelLabel?: string;
    confirmTone?: string; surface?: string;
    inputLabel?: string; label?: string; trim?: boolean; minLength?: number; maxLength?: number;
}
export interface ConfirmDialog {
    confirm(options?: ConfirmOptions): Promise<boolean>;
    prompt(options?: ConfirmOptions & { defaultValue?: string; placeholder?: string; inputType?: string }): Promise<string | null>;
    confirmAction<T>(options?: ConfirmOptions & { awaitAction?: boolean; onConfirm?: () => T | Promise<T> }): Promise<T | boolean>;
}
export function ConfirmDialogProvider(props: { children: ReactNode; value?: ConfirmDialog }): ReactElement;
export function useConfirmDialog(): ConfirmDialog;
