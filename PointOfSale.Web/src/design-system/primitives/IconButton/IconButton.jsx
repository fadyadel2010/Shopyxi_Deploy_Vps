import { forwardRef } from 'react'
import { cx } from '../cx'
import './IconButton.css'

const IconButton = forwardRef(function IconButton({
    label,
    icon,
    variant = 'ghost',
    size = 'md',
    type = 'button',
    disabled = false,
    loading = false,
    className,
    ...rest
}, ref) {
    if (import.meta.env.DEV && !label) {
        console.error('IconButton requires an accessible `label` prop.')
    }

    const isDisabled = disabled || loading

    return (
        <button
            ref={ref}
            type={type}
            aria-label={label}
            disabled={isDisabled}
            aria-busy={loading || undefined}
            className={cx(
                'pos-icon-button',
                `pos-icon-button--${variant}`,
                `pos-icon-button--${size}`,
                className,
            )}
            {...rest}
        >
            {loading ? (
                <span className="pos-icon-button__spinner" aria-hidden="true" />
            ) : (
                <span className="pos-icon-button__icon" aria-hidden="true">{icon}</span>
            )}
        </button>
    )
})

export default IconButton
