import { forwardRef } from 'react'
import { cx } from '../cx'
import './Badge.css'

const Badge = forwardRef(function Badge({
    variant = 'neutral',
    size = 'md',
    dot = false,
    className,
    children,
    ...rest
}, ref) {
    if (import.meta.env.DEV && (children == null || children === '')) {
        console.error('Badge requires text; color and a decorative dot are not sufficient.')
    }
    return (
        <span ref={ref} className={cx('pos-badge', `pos-badge--${variant}`, `pos-badge--${size}`, className)} {...rest}>
            {dot ? <span className="pos-badge__dot" aria-hidden="true" /> : null}
            {children}
        </span>
    )
})

export default Badge
