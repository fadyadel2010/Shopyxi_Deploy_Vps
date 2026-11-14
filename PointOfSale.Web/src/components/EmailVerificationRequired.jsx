import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import api from '../services/api'
import { useAuthStore } from '../store/authStore'
import { invalidatePosRuntimeCache } from '../services/posRuntimeCache'
import { broadcastPosVendorLogout } from '../auth/posVendorSessionAuthority'

export default function EmailVerificationRequired() {
    const { i18n } = useTranslation()
    const navigate = useNavigate()
    const ar = i18n.resolvedLanguage === 'ar'
    const [busy, setBusy] = useState(false)
    const [message, setMessage] = useState('')
    const check = async () => {
        setBusy(true)
        try {
            const response = await api.get('/auth/session')
            useAuthStore.getState().hydrateSession(response.data?.user ?? null)
            if (response.data?.user?.emailVerified !== true) {
                setMessage(ar ? 'لم يتم تأكيد البريد بعد. افتح رابط التأكيد ثم حاول مجددًا.' : 'Your email is not verified yet. Open the verification link, then try again.')
            }
        } catch {
            setMessage(ar ? 'تعذر التحقق من الجلسة. حاول مجددًا.' : 'Unable to check your session. Please try again.')
        } finally { setBusy(false) }
    }
    const resend = async () => {
        setBusy(true)
        try {
            await api.post('/auth/resend-verification')
            setMessage(ar ? 'تم طلب رسالة تأكيد جديدة. تحقق من بريدك.' : 'A verification email has been requested. Check your inbox.')
        } catch (error) {
            setMessage(error.response?.status === 429
                ? (ar ? 'يرجى الانتظار قبل طلب رسالة أخرى.' : 'Please wait before requesting another email.')
                : (ar ? 'تعذر إرسال الرسالة. حاول مجددًا.' : 'Unable to send the email. Please try again.'))
        } finally { setBusy(false) }
    }
    const signOut = async () => {
        setBusy(true)
        try {
            await api.post('/auth/logout')
            invalidatePosRuntimeCache({ reason: 'logout', broadcast: false })
            broadcastPosVendorLogout()
            useAuthStore.getState().logout()
            navigate('/login', { replace: true })
        } catch {
            setMessage(ar ? 'تعذر تسجيل الخروج. حاول مجددًا.' : 'Unable to sign out. Please try again.')
        } finally { setBusy(false) }
    }
    return (
        <main className="min-h-screen flex items-center justify-center bg-gray-50 p-4" dir={ar ? 'rtl' : 'ltr'}>
            <section className="w-full max-w-lg rounded-xl bg-white p-8 shadow-lg" aria-labelledby="verify-owner-heading">
                <h1 id="verify-owner-heading" className="text-2xl font-bold">{ar ? 'أكد بريدك الإلكتروني' : 'Verify your email address'}</h1>
                <p className="mt-4 text-gray-600">{ar ? 'افتح رابط التأكيد المرسل إلى بريدك للبدء في إدارة متجرك واستخدام نقطة البيع.' : 'Open the verification link sent to your inbox before managing your store or using POS.'}</p>
                <p role="status" className="mt-4">{message}</p>
                <div className="mt-6 flex flex-wrap gap-3">
                    <button className="btn-primary" disabled={busy} onClick={check}>{ar ? 'لقد أكدت بريدي' : 'I have verified my email'}</button>
                    <button className="btn-secondary" disabled={busy} onClick={resend}>{ar ? 'إعادة إرسال الرسالة' : 'Resend verification email'}</button>
                    <button className="btn-secondary" disabled={busy} onClick={signOut}>{ar ? 'تسجيل الخروج' : 'Sign out'}</button>
                </div>
            </section>
        </main>
    )
}
