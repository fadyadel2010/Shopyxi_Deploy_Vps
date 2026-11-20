import { Children, cloneElement, forwardRef, isValidElement, useCallback, useEffect, useId, useImperativeHandle, useLayoutEffect, useRef, useState } from 'react'
import { cx } from '../cx'
import './Table.css'

export const Table = forwardRef(function Table({
    className,
    containerClassName,
    children,
    'aria-label': label,
    'aria-labelledby': labelledBy,
    ...rest
}, ref) {
    const containerRef = useRef(null)
    const tableRef = useRef(null)
    const generatedId = useId()
    const [overflows, setOverflows] = useState(false)
    useImperativeHandle(ref, () => tableRef.current)

    const measure = useCallback(() => {
        const container = containerRef.current
        if (container) {
            const next = container.scrollWidth > container.clientWidth
            setOverflows((current) => current === next ? current : next)
        }
    }, [])

    useLayoutEffect(measure, [measure, children])
    useEffect(() => {
        if (typeof ResizeObserver === 'undefined') {
            return undefined
        }
        // Only mounted table geometry is observed. No window listener, polling,
        // document query, or observer is installed for the application shell.
        const observer = new ResizeObserver(measure)
        observer.observe(containerRef.current)
        observer.observe(tableRef.current)
        return () => observer.disconnect()
    }, [measure])

    let captionId
    const content = Children.map(children, (child) => {
        if (isValidElement(child) && child.type === TableCaption) {
            captionId = child.props.id ?? `${generatedId}-caption`
            return cloneElement(child, { id: captionId })
        }
        return child
    })
    const regionLabelledBy = labelledBy ?? (!label ? captionId : undefined)
    if (import.meta.env.DEV && !label && !labelledBy && !captionId) {
        console.error('Table requires a TableCaption, aria-label, or aria-labelledby.')
    }

    return (
        <div
            ref={containerRef}
            className={cx('pos-table-container', containerClassName)}
            tabIndex={overflows ? 0 : undefined}
            role={overflows ? 'region' : undefined}
            aria-label={overflows && !regionLabelledBy ? label : undefined}
            aria-labelledby={overflows ? regionLabelledBy : undefined}
        >
            <table ref={tableRef} aria-label={label} aria-labelledby={labelledBy} className={cx('pos-table', className)} {...rest}>
                {content}
            </table>
        </div>
    )
})

export const TableCaption = forwardRef(function TableCaption({ className, children, ...rest }, ref) {
    return (
        <caption ref={ref} className={cx('pos-table__caption', className)} {...rest}>
            {children}
        </caption>
    )
})

export const TableHeader = forwardRef(function TableHeader({ className, children, ...rest }, ref) {
    return (
        <thead ref={ref} className={cx('pos-table__head', className)} {...rest}>
            {children}
        </thead>
    )
})

export const TableBody = forwardRef(function TableBody({ className, children, ...rest }, ref) {
    return (
        <tbody ref={ref} className={cx('pos-table__body', className)} {...rest}>
            {children}
        </tbody>
    )
})

export const TableFooter = forwardRef(function TableFooter({ className, children, ...rest }, ref) {
    return (
        <tfoot ref={ref} className={cx('pos-table__foot', className)} {...rest}>
            {children}
        </tfoot>
    )
})

export const TableRow = forwardRef(function TableRow({ className, children, selected = false, ...rest }, ref) {
    return (
        <tr ref={ref} data-selected={selected || undefined} className={cx('pos-table__row', className)} {...rest}>
            {children}
        </tr>
    )
})

export const TableHead = forwardRef(function TableHead({ scope = 'col', className, children, ...rest }, ref) {
    return (
        <th ref={ref} scope={scope} className={cx('pos-table__header-cell', className)} {...rest}>
            {children}
        </th>
    )
})

export const TableCell = forwardRef(function TableCell({ className, children, ...rest }, ref) {
    return (
        <td ref={ref} className={cx('pos-table__cell', className)} {...rest}>
            {children}
        </td>
    )
})

export function TableEmpty({ colSpan = 1, children, className, ...rest }) {
    return (
        <TableRow className="pos-table__empty-row">
            <td colSpan={colSpan} className={cx('pos-table__empty', className)} {...rest}>
                {children}
            </td>
        </TableRow>
    )
}
