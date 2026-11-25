import { forwardRef } from 'react'
import { ChevronDown } from 'lucide-react'
import Field from '../Field/Field'
import { cx } from '../cx'
import './Select.css'

const Select = forwardRef(function Select({
    label,
    description,
    error,
    invalid = false,
    required = false,
    id,
    'aria-describedby': externalDescription,
    className,
    children,
    ...rest
}, ref) {
    if (import.meta.env.DEV && !label && !id && !rest['aria-label'] && !rest['aria-labelledby']) {
        console.error('Select requires a label, aria-label, aria-labelledby, or an id associated with an external label.')
    }
    const control = ({ controlId, describedBy, controlInvalid, controlRequired } = {}) => (
        <span className="pos-select">
            <select
                ref={ref}
                id={controlId ?? id}
                aria-describedby={[externalDescription, describedBy].filter(Boolean).join(' ') || undefined}
                aria-invalid={(controlInvalid ?? (invalid || Boolean(error))) || undefined}
                required={controlRequired ?? required}
                className={cx(
                    'pos-select__control',
                    (controlInvalid ?? (invalid || Boolean(error))) && 'pos-select__control--invalid',
                    className,
                )}
                {...rest}
            >
                {children}
            </select>
            <ChevronDown className="pos-select__chevron" aria-hidden="true" />
        </span>
    )

    if (!label) {
        return control({ controlId: id })
    }

    return (
        <Field
            id={id}
            label={label}
            description={description}
            error={error}
            required={required}
            invalid={invalid}
            className="pos-select-field"
        >
            {({ id: fieldId, describedBy, invalid: fieldInvalid, required: fieldRequired }) => (
                control({
                    controlId: fieldId,
                    describedBy,
                    controlInvalid: fieldInvalid,
                    controlRequired: fieldRequired,
                })
            )}
        </Field>
    )
})

export default Select
