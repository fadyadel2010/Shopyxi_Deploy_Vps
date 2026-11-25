import { forwardRef } from 'react'
import Field from '../Field/Field'
import { cx } from '../cx'
import './Input.css'

const Input = forwardRef(function Input({
    label,
    description,
    error,
    invalid = false,
    required = false,
    id,
    type = 'text',
    'aria-describedby': externalDescription,
    className,
    ...rest
}, ref) {
    if (import.meta.env.DEV && !label && !id && type !== 'hidden' && !rest['aria-label'] && !rest['aria-labelledby']) {
        console.error('Input requires a label, aria-label, aria-labelledby, or an id associated with an external label.')
    }
    if (!label) {
        return (
            <input
                ref={ref}
                id={id}
                type={type}
                aria-describedby={externalDescription}
                required={required}
                aria-invalid={(invalid || Boolean(error)) || undefined}
                className={cx('pos-input', (invalid || error) && 'pos-input--invalid', className)}
                {...rest}
            />
        )
    }

    return (
        <Field
            id={id}
            label={label}
            description={description}
            error={error}
            required={required}
            invalid={invalid}
            className="pos-input-field"
        >
            {({ id: fieldId, describedBy, invalid: fieldInvalid, required: fieldRequired }) => (
                <input
                    ref={ref}
                    id={fieldId}
                    type={type}
                    aria-describedby={[externalDescription, describedBy].filter(Boolean).join(' ') || undefined}
                    aria-invalid={fieldInvalid || undefined}
                    required={fieldRequired}
                    className={cx('pos-input', fieldInvalid && 'pos-input--invalid', className)}
                    {...rest}
                />
            )}
        </Field>
    )
})

export default Input
