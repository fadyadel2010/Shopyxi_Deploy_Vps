import type { ComponentPropsWithoutRef, ForwardRefExoticComponent, ReactNode, RefAttributes, RefObject } from 'react'
import type * as RadixDialog from '@radix-ui/react-dialog'
export interface DialogProps extends Omit<ComponentPropsWithoutRef<typeof RadixDialog.Content>, 'title'> {
    open: boolean
    onOpenChange?: (open: boolean) => void
    title: ReactNode
    description?: ReactNode
    size?: 'sm' | 'md' | 'lg' | 'xl'
    trigger?: ReactNode
    initialFocusRef?: RefObject<HTMLElement>
    restoreFocusRef?: RefObject<HTMLElement>
    closeOnEscape?: boolean
    closeOnOverlayClick?: boolean
    showClose?: boolean
    closeLabel?: string
    busy?: boolean
    footer?: ReactNode
}
declare const Dialog: ForwardRefExoticComponent<DialogProps & RefAttributes<HTMLDivElement>>
export default Dialog
