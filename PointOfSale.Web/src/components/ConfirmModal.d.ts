import type { ReactElement, ReactNode, RefObject } from 'react'
export interface ConfirmModalProps {
    title: ReactNode
    description?: ReactNode
    confirmLabel?: string
    cancelLabel?: string
    onConfirm?: () => unknown | Promise<unknown>
    onCancel: () => void
    children?: ReactNode
    confirmTone?: string
    variant?: string
    surface?: string
    confirmDisabled?: boolean
    cancelDisabled?: boolean
    disableEscapeKeyDown?: boolean
    hideCloseButton?: boolean
    loading?: boolean
    initialFocusRef?: RefObject<HTMLElement>
}
export default function ConfirmModal(props: ConfirmModalProps): ReactElement
