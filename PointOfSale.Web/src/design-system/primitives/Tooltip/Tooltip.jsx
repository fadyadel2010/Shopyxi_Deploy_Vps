import { useEffect } from 'react'
import * as RadixTooltip from '@radix-ui/react-tooltip'
import { cx } from '../cx'
import { usePosOverlayElement } from '../../overlays/posOverlayContext'
import './Tooltip.css'

export function Tooltip({
    label,
    children,
    side = 'top',
    align = 'center',
    sideOffset = 6,
    delayDuration,
    open,
    onOpenChange,
    disabled = false,
    className,
    contentClassName,
}) {
    if (import.meta.env.DEV && !label && !disabled) {
        console.error('Tooltip requires a supplementary `label`; the trigger must have its own accessible name.')
    }
    const container = usePosOverlayElement()

    useEffect(() => {
        if (import.meta.env.DEV && !container) {
            console.warn(
                'POS Tooltip is rendering without a POS overlay host. It falls back to document.body, where POS tokens do not resolve.',
            )
        }
    }, [container])

    if (disabled) {
        return children
    }

    return (
        <RadixTooltip.Root
            delayDuration={delayDuration}
            open={open}
            onOpenChange={onOpenChange}
        >
            <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
            <RadixTooltip.Portal container={container ?? undefined}>
                <RadixTooltip.Content
                    side={side}
                    align={align}
                    sideOffset={sideOffset}
                    className={cx('pos-tooltip', contentClassName, className)}
                >
                    {label}
                    <RadixTooltip.Arrow className="pos-tooltip__arrow" width={10} height={5} />
                </RadixTooltip.Content>
            </RadixTooltip.Portal>
        </RadixTooltip.Root>
    )
}

export default Tooltip
