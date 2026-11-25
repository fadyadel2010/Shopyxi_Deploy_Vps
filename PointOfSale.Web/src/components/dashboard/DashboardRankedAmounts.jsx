import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from '../../design-system/primitives'

export default function DashboardRankedAmounts({ data, caption, labelHeading, valueHeading, formatValue }) {
    const max = Math.max(...data.map(row => Math.abs(row.value)), 0)
    return (
        <Table className="pos-dashboard__ranking">
            <TableCaption>{caption}</TableCaption>
            <TableHeader><TableRow><TableHead>{labelHeading}</TableHead><TableHead className="pos-dashboard__number">{valueHeading}</TableHead></TableRow></TableHeader>
            <TableBody>
                {data.map((row, index) => (
                    <TableRow key={index}>
                        <TableCell>
                            <bdi>{row.label}</bdi>
                            <span className="pos-dashboard__bar-track" aria-hidden="true">
                                <span className="pos-dashboard__bar" data-sign={row.value < 0 ? 'negative' : 'positive'} style={{ inlineSize: `${max ? Math.abs(row.value) / max * 100 : 0}%` }} />
                            </span>
                        </TableCell>
                        <TableCell className="pos-dashboard__number"><bdi>{formatValue(row.value)}</bdi></TableCell>
                    </TableRow>
                ))}
            </TableBody>
        </Table>
    )
}
