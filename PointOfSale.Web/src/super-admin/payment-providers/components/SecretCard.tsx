import { KeyRound, RotateCw } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { saButton } from '../../ui/SuperAdminUI'
import type { PaymentProviderSecretMetadata } from '../paymentProviderTypes'
import { SecretMetadataPanel } from './SecretMetadataPanel'
import { SecretVersionList } from './SecretVersionList'

export function SecretCard({
    secretType,
    current,
    versions,
    onRotate,
    canRotate = true,
}: {
    secretType: string
    current?: PaymentProviderSecretMetadata
    versions: PaymentProviderSecretMetadata[]
    onRotate?: (secretType: string) => void
    canRotate?: boolean
}) {
    const { tr } = useSuperAdminLocale()

    return (
        <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2.5">
                    <div className="flex h-9 w-9 items-center justify-center rounded-lg bg-slate-100 text-slate-700">
                        <KeyRound className="h-4 w-4" />
                    </div>
                    <div>
                        <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">
                            {tr('Secret lifecycle', 'دورة حياة السر')}
                        </p>
                        <h3 className="text-base font-semibold text-slate-950" dir="auto">
                            {secretType}
                        </h3>
                    </div>
                </div>
                {canRotate && onRotate ? (
                    <button
                        type="button"
                        onClick={() => onRotate(secretType)}
                        className={saButton.secondary}
                    >
                        <RotateCw className="h-4 w-4" />
                        {tr('Rotate', 'تدوير')}
                    </button>
                ) : null}
            </div>

            <div className="mt-3 space-y-3">
                {current ? (
                    <SecretMetadataPanel secret={current} />
                ) : (
                    <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3.5 py-3 text-sm text-slate-600">
                        {tr(
                            'No active version exists for this secret type.',
                            'لا يوجد إصدار نشط لهذا النوع من الأسرار.',
                        )}
                    </div>
                )}
                <SecretVersionList versions={versions} />
            </div>
        </section>
    )
}
