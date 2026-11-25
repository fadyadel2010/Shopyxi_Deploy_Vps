import { forwardRef } from 'react'
import * as RadixTabs from '@radix-ui/react-tabs'
import { cx } from '../cx'
import './Tabs.css'

export const Tabs = forwardRef(function Tabs({
    variant = 'underline',
    className,
    children,
    ...rest
}, ref) {
    return (
        <RadixTabs.Root
            ref={ref}
            className={cx('pos-tabs', `pos-tabs--${variant}`, className)}
            {...rest}
        >
            {children}
        </RadixTabs.Root>
    )
})

export const TabsList = forwardRef(function TabsList({ className, children, ...rest }, ref) {
    return (
        <RadixTabs.List ref={ref} className={cx('pos-tabs__list', className)} {...rest}>
            {children}
        </RadixTabs.List>
    )
})

export const TabsTrigger = forwardRef(function TabsTrigger({ className, children, ...rest }, ref) {
    return (
        <RadixTabs.Trigger ref={ref} className={cx('pos-tabs__trigger', className)} {...rest}>
            {children}
        </RadixTabs.Trigger>
    )
})

export const TabsContent = forwardRef(function TabsContent({ className, children, ...rest }, ref) {
    return (
        <RadixTabs.Content ref={ref} className={cx('pos-tabs__content', className)} {...rest}>
            {children}
        </RadixTabs.Content>
    )
})
