import { useQuery } from '@tanstack/react-query'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { InlineNotice } from '../ui/SuperAdminUI'
import { getPostPayoutExceptionPolicy } from './postPayoutExceptionPolicy'

export default function PostPayoutExceptionPolicyNotice() {
    const { tr } = useSuperAdminLocale()
    const policy = useQuery({
        queryKey: ['super-admin', 'refunds', 'post-payout-exception-policy'],
        queryFn: getPostPayoutExceptionPolicy,
        staleTime: 0,
        retry: false,
    })

    return (
        <section aria-label={tr('Post-payout refund exceptions', 'استثناءات الاسترجاع بعد صرف مستحقات البائع')}>
            <InlineNotice tone={policy.isError ? 'rose' : 'amber'}>
                {policy.data && !policy.isError ? <p className="mb-2 font-semibold">
                    {tr(`Shopixy contractual benefit: ${policy.data.standardReturnWindowDays}-day no-reason returns; ${policy.data.defectClaimWindowDays}-day defect/non-conformity claims with an explicit reason. No automatic product exclusions. Timely claims remain processable after expiry. Payout stays held through the longest window and every unresolved claim.`,
                        `ميزة تعاقدية من شوبيكسي: إرجاع دون سبب خلال ${policy.data.standardReturnWindowDays} يومًا؛ ومطالبات العيب أو عدم المطابقة بسبب صريح خلال ${policy.data.defectClaimWindowDays} يومًا. لا استثناءات تلقائية للمنتجات. تظل الطلبات المقدمة في الموعد قابلة للمعالجة بعد انتهاء المهلة، وتُحجز المستحقات حتى انتهاء أطول مهلة وحسم جميع الطلبات.`)}
                </p> : null}
                <p className="font-semibold">{tr('Post-payout exception initiation is unavailable', 'إنشاء استثناء استرجاع بعد صرف المستحقات غير متاح')}</p>
                <p className="mt-1">
                    {policy.isError
                        ? tr('The exception policy could not be verified. No exception can be initiated. Existing refund reviews remain separate.',
                            'تعذر التحقق من سياسة الاستثناء. لا يمكن إنشاء استثناء. تظل مراجعة طلبات الاسترجاع الحالية مسارًا منفصلًا.')
                        : policy.isPending
                          ? tr('Checking exception policy. No exception can be initiated.', 'جارٍ التحقق من سياسة الاستثناء. لا يمكن إنشاء استثناء.')
                          : tr('No approved policy authorizes a new request after every applicable claim window closes. Existing complaint reasons do not authorize a late exception.',
                              'لا توجد سياسة معتمدة تسمح بإنشاء طلب جديد بعد انتهاء جميع مهل المطالبة المطبقة. أسباب الشكاوى الحالية لا تمنح صلاحية استثناء متأخر.')}
                </p>
                <p className="mt-1">
                    {tr('Policy approval must define eligible reasons and time limits, authoritative evidence, initiator and independent reviewer permissions, and vendor recovery/funding responsibility.',
                        'يجب أن تحدد السياسة المعتمدة الأسباب والمدد المسموح بها، والأدلة المعتمدة، وصلاحيات مقدم الطلب والمراجع المستقل، ومسؤولية استرداد مستحقات البائع وتمويل الاسترجاع.')}
                </p>
                <p className="mt-1">
                    {tr('Post-payout accounting preserves the original payout, reverses commission and creates vendor debt for paid vendor proceeds. It is not permission to refund or proof of an external transfer. The normal refund window and payout holds remain unchanged.',
                        'تحافظ محاسبة الاسترجاع بعد الصرف على سجل الصرف الأصلي، وتعكس العمولة، وتسجل دينًا على البائع بقيمة صافي مستحقاته المصروفة. هذا ليس إذنًا بالاسترجاع ولا إثباتًا لتحويل خارجي. تظل مهلة الاسترجاع وضوابط حجز المستحقات دون تغيير.')}
                </p>
            </InlineNotice>
        </section>
    )
}
