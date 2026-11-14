import { useMemo } from 'react'
import { EyeOff, LayoutTemplate, Loader2, Sparkles } from 'lucide-react'

import { SectionRenderer } from '@/components/sections/SectionRenderer'
import type { HomepageSection } from '@/components/sections/homepageSections'
import { StorefrontLayout } from '@/storefront/layout/StorefrontLayout'
import type { StoreConfig } from '@/types/storeConfig'

import BuilderThemeProvider from '@web/modules/storefront/components/builder/BuilderThemeProvider'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import {
    getSectionSchema,
    resolveSectionDataForLanguage,
    type HomepageBuilderSection,
} from './homepageBuilderUtils'

function mapBuilderSectionToHomepageSection(
    section: HomepageBuilderSection,
    language: 'en' | 'ar',
    schemas: Parameters<typeof getSectionSchema>[1],
): HomepageSection {
    return {
        id: section.clientId,
        type: section.type,
        order: section.order,
        enabled: section.isActive,
        data: resolveSectionDataForLanguage(section, getSectionSchema(section.type, schemas), language),
    }
}

function EmptyPreviewState({
    title,
    message,
    icon,
}: {
    title: string
    message: string
    icon: 'empty' | 'hidden'
}) {
    const Icon = icon === 'hidden' ? EyeOff : LayoutTemplate

    return (
        <div className="grid min-h-[480px] place-items-center px-8 py-12">
            <div className="max-w-lg text-center">
                <div className="mx-auto flex h-16 w-16 items-center justify-center rounded-3xl bg-slate-100 text-slate-500">
                    <Icon className="h-7 w-7" />
                </div>
                <h3 className="mt-6 text-2xl font-semibold text-slate-950">{title}</h3>
                <p className="mt-3 text-sm leading-7 text-slate-500">{message}</p>
            </div>
        </div>
    )
}

export default function HomepageBuilderPreview({
    sections,
    selectedSectionClientId,
    onSelectSection,
    storeConfig,
    previewTenantId,
    previewTenantName,
    isLoadingConfig = false,
    language = 'en',
    schemas = [],
}: {
    sections: HomepageBuilderSection[]
    selectedSectionClientId: string | null
    onSelectSection: (clientId: string) => void
    storeConfig: StoreConfig | null
    previewTenantId?: string | null
    previewTenantName?: string | null
    isLoadingConfig?: boolean
    language?: 'en' | 'ar'
    schemas?: Parameters<typeof getSectionSchema>[1]
}) {
    const { tr } = useSuperAdminLocale()
    const homepageSections = useMemo(
        () => sections.map((section) => mapBuilderSectionToHomepageSection(section, language, schemas)),
        [language, schemas, sections],
    )

    const visibleSections = useMemo(
        () => homepageSections.filter((section) => section.enabled),
        [homepageSections],
    )

    if (isLoadingConfig) {
        return (
            <div className="grid min-h-[520px] place-items-center rounded-2xl border border-slate-200 bg-white">
                <div className="flex items-center gap-3 text-sm font-semibold text-slate-600">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    {tr('Loading storefront preview theme…', 'جارٍ تحميل قالب معاينة المتجر…')}
                </div>
            </div>
        )
    }

    if (!storeConfig) {
        return (
            <div className="grid min-h-[520px] place-items-center rounded-2xl border border-dashed border-slate-300 bg-white px-8 py-12 text-center">
                <div className="max-w-lg">
                    <div className="mx-auto flex h-16 w-16 items-center justify-center rounded-3xl bg-sky-100 text-sky-700">
                        <Sparkles className="h-7 w-7" />
                    </div>
                    <h3 className="mt-6 text-xl font-semibold text-slate-950">
                        {previewTenantId
                            ? tr('Storefront preview is not ready yet', 'معاينة المتجر غير جاهزة بعد')
                            : tr('Pick a tenant preview context', 'اختر سياق معاينة مستأجر')}
                    </h3>
                    <p className="mt-3 text-sm leading-7 text-slate-500">
                        {previewTenantId
                            ? tr(
                                  'The builder is waiting for the storefront theme configuration before it can render the real customer-facing homepage canvas.',
                                  'ينتظر المنشئ إعداد قالب المتجر قبل عرض لوحة الصفحة الرئيسية الحقيقية للعملاء.',
                              )
                            : tr(
                                  'Homepage sections are rendered through the real storefront theme. Choose a tenant so the builder can use the same layout, theme, and catalog context customers see.',
                                  'تُعرض أقسام الصفحة الرئيسية عبر قالب المتجر الحقيقي. اختر مستأجرًا ليستخدم المنشئ نفس التخطيط والقالب وسياق الكتالوج الذي يراه العملاء.',
                              )}
                    </p>
                </div>
            </div>
        )
    }

    return (
        <BuilderThemeProvider tenantId={previewTenantId ?? undefined} storeConfig={storeConfig}>
            <div
                dir={language === 'ar' ? 'rtl' : 'ltr'}
                className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm"
            >
                <StorefrontLayout>
                    <div
                        role="region"
                        aria-label={tr('Storefront homepage preview', 'معاينة الصفحة الرئيسية للمتجر')}
                        className="flex-1 bg-background"
                    >
                        <div className="border-b border-slate-200/80 bg-white/90 px-4 py-2.5 backdrop-blur md:px-5">
                            <div className="flex flex-wrap items-center justify-between gap-2">
                                <div>
                                    <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-sky-700">
                                        {tr('Unified renderer', 'العارض الموحد')}
                                    </p>
                                    <p className="mt-0.5 text-sm font-semibold text-slate-950">
                                        {tr(
                                            'Previewing the real storefront homepage renderer',
                                            'معاينة عارض الصفحة الرئيسية الحقيقي للمتجر',
                                        )}
                                    </p>
                                </div>

                                <div className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold text-slate-600">
                                    {previewTenantName
                                        ? tr(`Theme + catalog: ${previewTenantName}`, `القالب + الكتالوج: ${previewTenantName}`)
                                        : tr('Shared storefront preview', 'معاينة متجر مشتركة')}
                                </div>
                            </div>
                        </div>

                        {homepageSections.length === 0 ? (
                            <EmptyPreviewState
                                icon="empty"
                                title={tr('No sections in this draft yet', 'لا أقسام في هذه المسودة بعد')}
                                message={tr(
                                    'Generate the starter template or add a section from the left rail to see the storefront render here.',
                                    'أنشئ قالب البداية أو أضف قسمًا من الشريط الأيسر لرؤية عرض المتجر هنا.',
                                )}
                            />
                        ) : visibleSections.length === 0 ? (
                            <EmptyPreviewState
                                icon="hidden"
                                title={tr('All draft sections are hidden', 'كل أقسام المسودة مخفية')}
                                message={tr(
                                    'Re-enable a section from the editor panel to bring it back into the storefront preview.',
                                    'أعد تفعيل قسم من لوحة المحرر لإعادته إلى معاينة المتجر.',
                                )}
                            />
                        ) : (
                            <div className="space-y-4 p-3 md:p-5">
                                <SectionRenderer
                                    sections={homepageSections}
                                    selectedSectionId={selectedSectionClientId}
                                    onSectionSelect={onSelectSection}
                                />
                            </div>
                        )}
                    </div>
                </StorefrontLayout>
            </div>
        </BuilderThemeProvider>
    )
}
