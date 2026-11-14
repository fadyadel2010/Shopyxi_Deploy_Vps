import type { ReactNode } from 'react'

export function PaymentProviderLayout({
    header,
    list,
    details,
}: {
    header: ReactNode
    list: ReactNode
    details: ReactNode
}) {
    return (
        <div className="space-y-4">
            {header}
            <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,0.34fr)_minmax(0,0.66fr)]">
                <aside className="min-w-0 space-y-2 xl:sticky xl:top-20 xl:self-start">{list}</aside>
                <section className="min-w-0 space-y-3">{details}</section>
            </div>
        </div>
    )
}
