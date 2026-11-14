import { Loader2, RotateCcw, Save } from 'lucide-react'
import { useSuperAdminLocale } from '../../i18n/useSuperAdminLocale'
import { saButton } from '../../ui/SuperAdminUI'

export function SaveBar({
    saving,
    disabled,
    dirty,
    successMessage,
    onSave,
    onCancel,
}: {
    saving: boolean
    disabled: boolean
    dirty: boolean
    successMessage?: string | null
    onSave: () => void
    onCancel: () => void
}) {
    const { tr } = useSuperAdminLocale()

    const statusText = successMessage
        ? successMessage
        : dirty
          ? tr('Unsaved changes are ready to save.', 'توجد تغييرات غير محفوظة جاهزة للحفظ.')
          : tr('No unsaved changes.', 'لا توجد تغييرات غير محفوظة.')

    return (
        <div className="sticky bottom-3 z-10 flex flex-wrap items-center justify-between gap-2 rounded-2xl border border-slate-200 bg-white/95 px-4 py-3 shadow-lg backdrop-blur">
            <p className="min-w-0 text-sm text-slate-600" dir="auto">
                {statusText}
            </p>
            <div className="flex flex-wrap items-center gap-2">
                <button
                    type="button"
                    disabled={!dirty || saving}
                    onClick={onCancel}
                    className={saButton.secondary}
                >
                    <RotateCcw className="h-4 w-4" />
                    {tr('Cancel changes', 'إلغاء التغييرات')}
                </button>
                <button
                    type="button"
                    disabled={disabled || saving || !dirty}
                    onClick={onSave}
                    className={saButton.primary}
                >
                    {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                    {saving ? tr('Saving…', 'جارٍ الحفظ…') : tr('Save', 'حفظ')}
                </button>
            </div>
        </div>
    )
}
