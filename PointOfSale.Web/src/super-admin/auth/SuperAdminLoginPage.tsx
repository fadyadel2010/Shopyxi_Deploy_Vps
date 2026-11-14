import { useMemo, useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { ArrowRight, Languages, LockKeyhole, ShieldCheck } from 'lucide-react'
import { getSuperAdminSession, loginSuperAdmin } from './superAdminAuthService'
import { useSuperAdminAuthStore } from './superAdminAuthStore'
import { useSuperAdminSessionBootstrap } from './useSuperAdminSessionBootstrap'
import { useSuperAdminLocale } from '../i18n/useSuperAdminLocale'
import { GuidePanel } from '../ui/SuperAdminUI'

function resolveRedirect(search: string): string {
    const params = new URLSearchParams(search)
    const redirect = params.get('redirect')

    if (!redirect || !redirect.startsWith('/') || redirect.startsWith('//') || redirect.includes('\\')) {
        return '/super-admin/finance'
    }

    try {
        const url = new URL(redirect, window.location.origin)
        if (
            url.origin !== window.location.origin ||
            !url.pathname.startsWith('/super-admin') ||
            url.pathname.startsWith('/super-admin/login')
        ) {
            return '/super-admin/finance'
        }

        return `${url.pathname}${url.search}${url.hash}`
    } catch {
        return '/super-admin/finance'
    }
}

export default function SuperAdminLoginPage() {
    const location = useLocation()
    const navigate = useNavigate()
    const { user, isAuthenticated, setSession } = useSuperAdminAuthStore()
    const hasHydrated = useSuperAdminSessionBootstrap()
    const redirectTarget = useMemo(() => resolveRedirect(location.search), [location.search])
    const { tr, isArabic } = useSuperAdminLocale()

    const [email, setEmail] = useState('')
    const [password, setPassword] = useState('')
    const [error, setError] = useState('')
    const [isSubmitting, setIsSubmitting] = useState(false)

    const hasSession =
        hasHydrated &&
        isAuthenticated &&
        user?.role === 'SuperAdmin' &&
        user?.authType === 'super_admin'

    function toggleLanguage() {
        const nextLanguage = isArabic ? 'en' : 'ar'
        void import('../../i18n/config').then((module) => module.default.changeLanguage(nextLanguage))
    }

    if (!hasHydrated) {
        return (
            <div dir={isArabic ? 'rtl' : 'ltr'} className="flex min-h-screen items-center justify-center bg-slate-950 text-sm text-slate-300">
                {tr('Checking secure Super Admin session...', 'جارٍ التحقق من جلسة الإدارة العليا الآمنة...')}
            </div>
        )
    }

    if (hasSession) {
        return <Navigate to={redirectTarget} replace />
    }

    async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
        event.preventDefault()

        setError('')
        setIsSubmitting(true)

        try {
            await loginSuperAdmin({
                email,
                password,
            })

            const session = await getSuperAdminSession()

            setSession(session)
            navigate(redirectTarget, { replace: true })
        } catch (submissionError) {
            const message =
                submissionError instanceof Error
                    ? submissionError.message
                    : tr('Unable to sign in right now.', 'تعذّر تسجيل الدخول الآن.')

            setError(message)
        } finally {
            setIsSubmitting(false)
        }
    }

    return (
        <div dir={isArabic ? 'rtl' : 'ltr'} className="relative min-h-screen overflow-hidden bg-slate-950 text-white">
            <div className="absolute inset-0 bg-[radial-gradient(circle_at_top_left,_rgba(16,185,129,0.18),_transparent_30%),radial-gradient(circle_at_bottom_right,_rgba(251,191,36,0.16),_transparent_30%)]" />
            <div className="relative mx-auto flex min-h-screen max-w-7xl items-center px-4 py-10 sm:px-6 lg:px-10">
                <div className="grid w-full gap-8 lg:grid-cols-[1.1fr_0.9fr]">
                    <section className="hidden rounded-3xl border border-white/10 bg-white/5 p-8 shadow-2xl backdrop-blur lg:block xl:p-10">
                        <div className="inline-flex items-center gap-2 rounded-full border border-emerald-400/30 bg-emerald-400/10 px-3.5 py-1.5 text-xs font-semibold uppercase tracking-wide text-emerald-200">
                            <ShieldCheck className="h-4 w-4" />
                            {tr('Platform-wide owner access', 'وصول مالك المنصة بالكامل')}
                        </div>

                        <h1 className="mt-7 max-w-xl text-4xl font-bold leading-tight text-white xl:text-5xl">
                            {tr('Shopixy Super Admin', 'إدارة Shopixy العليا')}
                        </h1>
                        <p className="mt-4 max-w-xl text-base leading-7 text-slate-300">
                            {tr(
                                'Monitor payments, earnings, payouts, storefront content, and marketplace health from one secure platform view. This console is isolated from tenant administration.',
                                'راقب المدفوعات والأرباح والتحويلات ومحتوى المتجر وحالة السوق من عرض واحد آمن للمنصة. وهذه اللوحة معزولة عن إدارة المستأجرين.',
                            )}
                        </p>

                        <div className="mt-8 grid gap-3 sm:grid-cols-3">
                            {[
                                tr('Platform-wide visibility', 'رؤية على مستوى المنصة'),
                                tr('Request-level audit trails', 'سجلات تدقيق على مستوى الطلب'),
                                tr('Cookie session with CSRF protection', 'جلسة بكوكيز مع حماية CSRF'),
                            ].map((item) => (
                                <div
                                    key={item}
                                    className="rounded-xl border border-white/10 bg-slate-900/60 p-3.5 text-sm leading-6 text-slate-200"
                                >
                                    {item}
                                </div>
                            ))}
                        </div>
                    </section>

                    <section className="rounded-3xl border border-slate-200 bg-white p-6 text-slate-900 shadow-[0_32px_80px_-40px_rgba(15,23,42,0.75)] sm:p-8">
                        <div className="mx-auto max-w-md">
                            <div className="flex items-start justify-between gap-3">
                                <div className="flex items-center gap-3">
                                    <div className="flex h-11 w-11 items-center justify-center rounded-xl bg-slate-900 text-white shadow-lg">
                                        <LockKeyhole className="h-5 w-5" />
                                    </div>
                                    <div>
                                        <p className="text-xs font-semibold uppercase tracking-[0.2em] text-emerald-600">
                                            {tr('Super Admin', 'الإدارة العليا')}
                                        </p>
                                        <h2 className="text-2xl font-bold text-slate-950">
                                            {tr('Sign in', 'تسجيل الدخول')}
                                        </h2>
                                    </div>
                                </div>

                                <button
                                    type="button"
                                    onClick={toggleLanguage}
                                    className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-slate-50 px-2.5 py-1.5 text-xs font-semibold text-slate-600 transition hover:bg-slate-100"
                                    aria-label={tr('Switch to Arabic', 'التبديل إلى الإنجليزية')}
                                >
                                    <Languages className="h-3.5 w-3.5" />
                                    {isArabic ? 'EN' : 'AR'}
                                </button>
                            </div>

                            <p className="mt-5 text-sm leading-6 text-slate-500">
                                {tr(
                                    'Use your isolated Super Admin credentials to access the platform console.',
                                    'استخدم بيانات اعتماد الإدارة العليا المعزولة للوصول إلى لوحة المنصة.',
                                )}
                            </p>

                            {error ? (
                                <div className="mt-5 rounded-xl border border-red-200 bg-red-50 px-3.5 py-3 text-sm text-red-700" role="alert">
                                    <span dir="auto">{error}</span>
                                </div>
                            ) : null}

                            <form className="mt-6 space-y-4" onSubmit={handleSubmit}>
                                <label className="block">
                                    <span className="mb-1.5 block text-sm font-medium text-slate-700">
                                        {tr('Email', 'البريد الإلكتروني')}
                                    </span>
                                    <input
                                        autoComplete="email"
                                        className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-base outline-none transition focus:border-emerald-500 focus:bg-white focus:ring-4 focus:ring-emerald-500/10"
                                        onChange={(event) => setEmail(event.target.value)}
                                        placeholder={tr('owner@shopixy.com', 'owner@shopixy.com')}
                                        required
                                        type="email"
                                        value={email}
                                    />
                                </label>

                                <label className="block">
                                    <span className="mb-1.5 block text-sm font-medium text-slate-700">
                                        {tr('Password', 'كلمة المرور')}
                                    </span>
                                    <input
                                        autoComplete="current-password"
                                        className="w-full rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-2.5 text-base outline-none transition focus:border-emerald-500 focus:bg-white focus:ring-4 focus:ring-emerald-500/10"
                                        onChange={(event) => setPassword(event.target.value)}
                                        placeholder={tr('Enter your secure password', 'أدخل كلمة المرور الآمنة')}
                                        required
                                        type="password"
                                        value={password}
                                    />
                                </label>

                                <button
                                    className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-slate-950 px-5 py-3 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                                    disabled={isSubmitting}
                                    type="submit"
                                >
                                    {isSubmitting
                                        ? tr('Signing in...', 'جارٍ تسجيل الدخول...')
                                        : tr('Enter Super Admin Console', 'دخول لوحة الإدارة العليا')}
                                    <ArrowRight className={`h-4 w-4 ${isArabic ? 'rotate-180' : ''}`} />
                                </button>
                            </form>

                            <div className="mt-5">
                                <GuidePanel
                                    guide={{
                                        en: {
                                            title: 'Super Admin sign-in guide',
                                            purpose:
                                                'Signs a platform owner into the isolated Super Admin console, separate from tenant store administration.',
                                            when:
                                                'Use it when you need platform-wide finance, marketplace, storefront content, or governance access.',
                                            steps: [
                                                'Enter the Super Admin email address issued to you.',
                                                'Enter the matching password.',
                                                'Submit the form; the console validates the session against the backend.',
                                                'You are redirected to the requested Super Admin page, or to Finance by default.',
                                            ],
                                            statuses: [
                                                { label: 'Checking secure session', description: 'The app is verifying whether an existing Super Admin session is still valid.' },
                                                { label: 'Signed in', description: 'A valid Super Admin session exists and the requested console page opens.' },
                                            ],
                                            safety: [
                                                'This login is separate from tenant and storefront accounts.',
                                                'Credentials are validated by the backend; nothing is stored in the browser by this form.',
                                                'The session uses a cookie with CSRF protection.',
                                                'Only redirects inside /super-admin are honored after sign-in.',
                                            ],
                                        },
                                        ar: {
                                            title: 'دليل تسجيل دخول الإدارة العليا',
                                            purpose:
                                                'يسجّل دخول مالك المنصة إلى لوحة الإدارة العليا المعزولة، بشكل منفصل عن إدارة متاجر المستأجرين.',
                                            when:
                                                'استخدمه عند الحاجة إلى وصول على مستوى المنصة للمالية أو السوق أو محتوى المتجر أو الحوكمة.',
                                            steps: [
                                                'أدخل البريد الإلكتروني للإدارة العليا المخصص لك.',
                                                'أدخل كلمة المرور المطابقة.',
                                                'أرسل النموذج؛ وتتحقق اللوحة من الجلسة عبر الخادم.',
                                                'تُعاد توجيهك إلى صفحة الإدارة العليا المطلوبة، أو إلى المالية افتراضيًا.',
                                            ],
                                            statuses: [
                                                { label: 'جارٍ التحقق من الجلسة', description: 'يتحقق التطبيق مما إذا كانت جلسة الإدارة العليا الحالية ما زالت صالحة.' },
                                                { label: 'مسجّل الدخول', description: 'توجد جلسة إدارة عليا صالحة وتُفتح صفحة اللوحة المطلوبة.' },
                                            ],
                                            safety: [
                                                'هذا الدخول منفصل عن حسابات المستأجرين والمتجر.',
                                                'يتحقق الخادم من بيانات الاعتماد؛ ولا يخزّن هذا النموذج شيئًا في المتصفح.',
                                                'تستخدم الجلسة كوكيز مع حماية CSRF.',
                                                'لا يُعتمد بعد الدخول إلا التوجيه داخل /super-admin.',
                                            ],
                                        },
                                    }}
                                />
                            </div>
                        </div>
                    </section>
                </div>
            </div>
        </div>
    )
}
