import { useId } from 'react'
import { cx } from '../cx'
import './Field.css'

export default function Field({
    id: providedId,
    label,
    description,
    error,
    required = false,
    invalid = false,
    className,
    children,
}) {
    const generatedId = useId()
    const id = providedId ?? generatedId
    if (import.meta.env.DEV && !label) {
        console.error('Field requires a persistent `label` prop.')
    }
    const descriptionId = description ? `${id}-description` : undefined
    const errorId = error ? `${id}-error` : undefined
    const describedBy = [descriptionId, errorId].filter(Boolean).join(' ') || undefined
    const isInvalid = invalid || Boolean(error)
    const context = { id, describedBy, invalid: isInvalid, required }

    return (
        <div className={cx('pos-field', className)}>
            <label className="pos-field__label" htmlFor={id}>
                {label}
                {required ? <span className="pos-field__required" aria-hidden="true">*</span> : null}
            </label>
            {description ? (
                <p className="pos-field__description" id={descriptionId}>{description}</p>
            ) : null}
            {typeof children === 'function' ? children(context) : children}
            {error ? (
                <p className="pos-field__error" id={errorId} role="alert">{error}</p>
            ) : null}
        </div>
    )
}
