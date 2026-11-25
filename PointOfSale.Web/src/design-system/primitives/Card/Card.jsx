import { forwardRef } from 'react'
import { cx } from '../cx'
import './Card.css'

const Card = forwardRef(function Card({
    as: Component = 'div',
    padding = 'md',
    className,
    children,
    ...rest
}, ref) {
    return (
        <Component
            ref={ref}
            className={cx('pos-card', `pos-card--padding-${padding}`, className)}
            {...rest}
        >
            {children}
        </Component>
    )
})

export default Card
