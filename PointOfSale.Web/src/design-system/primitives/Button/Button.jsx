import { forwardRef } from 'react'
import { cx } from '../cx'
import './Button.css'

const Button = forwardRef(function Button({
    variant = 'primary',
    size = 'md',
    type = 'button',
    disabled = false,
    loading = false,
    fullWidth = false,
    leadingIcon,
    trailingIcon,
    className,
    children,
    ...rest
}, ref) {
    const isDisabled = disabled || loading

    return (
        <button
            ref={ref}
            type={type}
            disabled={isDisabled}
            aria-busy={loading || undefined}
            className={cx(
                'pos-button',
                `pos-button--${variant}`,
                `pos-button--${size}`,
                fullWidth && 'pos-button--full',
                className,
            )}
            {...rest}
        >
            {loading ? (
                <span className="pos-button__spinner" aria-hidden="true" />
            ) : leadingIcon ? (
                <span className="pos-button__icon" aria-hidden="true">{leadingIcon}</span>
            ) : null}
            <span className="pos-button__label">{children}</span>
            {trailingIcon && !loading ? (
                <span className="pos-button__icon" aria-hidden="true">{trailingIcon}</span>
            ) : null}
        </button>
    )
})

export default Button
