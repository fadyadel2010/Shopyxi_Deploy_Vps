import { FooterConfigScreen } from '../../shared/content/FooterConfigScreen'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { GuidePanel } from '../ui/SuperAdminUI'

export function StorefrontFooterDefaultsPage() {
    const { tr } = useSuperAdminLocale()

    return (
        <FooterConfigScreen
            scope="Global"
            title={tr('Storefront Footer Defaults', 'الإعدادات الافتراضية لتذييل المتجر')}
            description={tr(
                'Manage the global fallback footer and social links used by tenant storefronts when a tenant has no effective footer configuration of its own.',
                'أدر التذييل الاحتياطي العام والروابط الاجتماعية التي تستخدمها متاجر المستأجرين عندما لا يملك المستأجر إعداد تذييل فعّالًا خاصًا به.',
            )}
            note={tr(
                'Saving publishes these fallback defaults immediately. CMS slugs are resolved independently against each tenant’s published pages at runtime.',
                'يحفظ هذا الإجراء هذه الإعدادات الافتراضية الاحتياطية وينشرها فورًا. تُحل معرّفات CMS بشكل مستقل مقابل الصفحات المنشورة لكل مستأجر عند التشغيل.',
            )}
            guide={
                <GuidePanel
                    guide={{
                        en: {
                            title: 'Storefront footer defaults guide',
                            purpose:
                                'Defines the fallback footer sections, links, and social links that tenant storefronts use when they have no footer configuration of their own.',
                            when:
                                'Use it to update global contact details, policy links, or social profiles that should apply to stores without a custom footer.',
                            steps: [
                                'Review the current fallback footer and add or reorder sections as needed.',
                                'Edit each section title and its links in English and Arabic; use Auto Translate to draft Arabic.',
                                'Choose a link type: internal storefront URL, CMS page, or external URL.',
                                'Save the live footer and confirm the change immediately affects stores without their own footer.',
                            ],
                            statuses: [
                                { label: 'Live', description: 'The saved fallback footer currently used by stores without their own footer.' },
                                { label: 'Unsaved changes', description: 'Local edits exist but have not been saved live.' },
                                { label: 'Section active / inactive', description: 'Whether the footer section is rendered in the storefront.' },
                                { label: 'Link type', description: 'Internal, CMS, or external destination; CMS slugs resolve per tenant at runtime.' },
                            ],
                            safety: [
                                'Saving replaces the live fallback footer immediately; there is no separate publish step.',
                                'Tenant-specific footers are not changed by this screen.',
                                'CMS link targets are resolved against each tenant’s own published pages, not the marketplace CMS.',
                                'Auto Translate fills the Arabic draft; review it before saving live.',
                            ],
                        },
                        ar: {
                            title: 'دليل الإعدادات الافتراضية لتذييل المتجر',
                            purpose:
                                'يحدد أقسام التذييل والروابط والروابط الاجتماعية الاحتياطية التي تستخدمها متاجر المستأجرين عندما لا تملك إعداد تذييل خاصًا بها.',
                            when:
                                'استخدمه لتحديث بيانات التواصل العامة أو روابط السياسات أو الحسابات الاجتماعية التي تنطبق على المتاجر التي لا تملك تذييلًا مخصصًا.',
                            steps: [
                                'راجع التذييل الاحتياطي الحالي وأضف الأقسام أو أعد ترتيبها حسب الحاجة.',
                                'حرّر عنوان كل قسم وروابطه بالإنجليزية والعربية؛ واستخدم الترجمة التلقائية لصياغة العربية.',
                                'اختر نوع الرابط: عنوان داخلي في المتجر، أو صفحة CMS، أو عنوان خارجي.',
                                'احفظ التذييل المباشر وأكد أن التغيير يؤثر فورًا على المتاجر التي لا تملك تذييلًا خاصًا.',
                            ],
                            statuses: [
                                { label: 'مباشر', description: 'التذييل الاحتياطي المحفوظ والمستخدم حاليًا للمتاجر التي لا تملك تذييلًا خاصًا.' },
                                { label: 'تغييرات غير محفوظة', description: 'توجد تعديلات محلية لم تُحفظ مباشرة بعد.' },
                                { label: 'قسم نشط / غير نشط', description: 'هل يُعرض قسم التذييل في المتجر.' },
                                { label: 'نوع الرابط', description: 'وجهة داخلية أو CMS أو خارجية؛ وتُحل معرّفات CMS لكل مستأجر عند التشغيل.' },
                            ],
                            safety: [
                                'الحفظ يستبدل التذييل الاحتياطي المباشر فورًا؛ ولا توجد خطوة نشر منفصلة.',
                                'لا تغيّر هذه الشاشة تذييلات المستأجرين المخصصة.',
                                'تُحل أهداف روابط CMS مقابل الصفحات المنشورة لكل مستأجر، وليس مقابل CMS السوق.',
                                'الترجمة التلقائية تملأ المسودة العربية؛ راجعها قبل الحفظ المباشر.',
                            ],
                        },
                    }}
                />
            }
            localized
            allowCmsSlugEntry
        />
    )
}

export default StorefrontFooterDefaultsPage
