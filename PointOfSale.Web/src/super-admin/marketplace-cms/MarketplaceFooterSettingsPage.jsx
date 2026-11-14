import { FooterConfigScreen } from '../../shared/content/FooterConfigScreen'
import { marketplaceCmsService } from './marketplaceCmsService'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { GuidePanel } from '../ui/SuperAdminUI'

export function MarketplaceFooterSettingsPage() {
    const { tr } = useSuperAdminLocale()

    return (
        <FooterConfigScreen
            scope="Marketplace"
            title={tr('Marketplace Footer and Social Links', 'تذييل السوق والروابط الاجتماعية')}
            description={tr(
                'Manage the global Shopixy marketplace footer separately from tenant storefront content. These links render only on the marketplace domain.',
                'أدر تذييل سوق Shopixy العام بشكل منفصل عن محتوى متاجر المستأجرين. تُعرض هذه الروابط على نطاق السوق فقط.',
            )}
            note={tr(
                'Saving publishes the marketplace footer immediately. Only published Marketplace CMS pages are valid CMS link targets.',
                'يحفظ هذا الإجراء تذييل السوق وينشره فورًا. وتُعد صفحات CMS المنشورة في السوق فقط أهدافًا صالحة لروابط CMS.',
            )}
            guide={
                <GuidePanel
                    guide={{
                        en: {
                            title: 'Marketplace footer guide',
                            purpose:
                                'Controls the footer sections, links, and social profiles shown on the Shopixy marketplace domain, separately from any tenant storefront footer.',
                            when:
                                'Use it when the marketplace needs updated policy links, contact details, or social profiles that do not belong to a tenant store.',
                            steps: [
                                'Review the marketplace footer sections and their order.',
                                'Edit titles and link labels in English and Arabic; use Auto Translate to draft Arabic.',
                                'Pick Internal or External URLs, or a published Marketplace CMS page for CMS links.',
                                'Save the live footer and verify the marketplace domain renders the change.',
                            ],
                            statuses: [
                                { label: 'Live', description: 'The saved marketplace footer currently used on the marketplace domain.' },
                                { label: 'Unsaved changes', description: 'Local edits exist but have not been saved live.' },
                                { label: 'Section active / inactive', description: 'Whether the footer section appears on the marketplace domain.' },
                                { label: 'CMS link', description: 'Targets a published Marketplace CMS page; unpublished pages are not valid targets.' },
                            ],
                            safety: [
                                'Saving replaces the live marketplace footer immediately; there is no separate publish step.',
                                'Tenant storefront footers are not changed by this screen.',
                                'Only published Marketplace CMS pages can be used as CMS link targets.',
                                'Auto Translate fills the Arabic draft; review it before saving live.',
                            ],
                        },
                        ar: {
                            title: 'دليل تذييل السوق',
                            purpose:
                                'يتحكم في أقسام التذييل والروابط والحسابات الاجتماعية المعروضة على نطاق سوق Shopixy، بشكل منفصل عن تذييل أي متجر مستأجر.',
                            when:
                                'استخدمه عندما يحتاج السوق إلى روابط سياسات أو بيانات تواصل أو حسابات اجتماعية محدثة لا تخص متجر مستأجر.',
                            steps: [
                                'راجع أقسام تذييل السوق وترتيبها.',
                                'حرّر العناوين وتسميات الروابط بالإنجليزية والعربية؛ واستخدم الترجمة التلقائية لصياغة العربية.',
                                'اختر عنوانًا داخليًا أو خارجيًا، أو صفحة CMS منشورة في السوق لروابط CMS.',
                                'احفظ التذييل المباشر وتحقق من ظهور التغيير على نطاق السوق.',
                            ],
                            statuses: [
                                { label: 'مباشر', description: 'تذييل السوق المحفوظ والمستخدم حاليًا على نطاق السوق.' },
                                { label: 'تغييرات غير محفوظة', description: 'توجد تعديلات محلية لم تُحفظ مباشرة بعد.' },
                                { label: 'قسم نشط / غير نشط', description: 'هل يظهر قسم التذييل على نطاق السوق.' },
                                { label: 'رابط CMS', description: 'يستهدف صفحة CMS منشورة في السوق؛ والصفحات غير المنشورة ليست أهدافًا صالحة.' },
                            ],
                            safety: [
                                'الحفظ يستبدل تذييل السوق المباشر فورًا؛ ولا توجد خطوة نشر منفصلة.',
                                'لا تغيّر هذه الشاشة تذييلات متاجر المستأجرين.',
                                'يمكن استخدام صفحات CMS المنشورة في السوق فقط كأهداف لروابط CMS.',
                                'الترجمة التلقائية تملأ المسودة العربية؛ راجعها قبل الحفظ المباشر.',
                            ],
                        },
                    }}
                />
            }
            localized
            loadCmsPages={async () => {
                const pages = await marketplaceCmsService.listPages()
                return (pages || [])
                    .filter((page) => page.publishedVersion)
                    .map((page) => ({ slug: page.slug, title: page.title }))
            }}
            onTranslateContent={marketplaceCmsService.translateContent}
        />
    )
}

export default MarketplaceFooterSettingsPage
