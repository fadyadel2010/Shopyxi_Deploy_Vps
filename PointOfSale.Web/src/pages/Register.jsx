import { useState, useEffect, useRef, useCallback } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { ChartNoAxesColumnIncreasing, Check, CircleAlert, Eye, EyeOff, Layers3, LockKeyhole, ShoppingBag } from 'lucide-react'
import { useAuthStore } from '../store/authStore'
import { requirePosVendorSessionUserAuthority } from '../auth/posVendorSessionAuthority'
import api from '../services/api'
import { normalizeStoreSlug, isStoreSlugFormatValid } from './storeSlug'
import LanguageSwitcher from '../components/LanguageSwitcher'
import { Button, Card, Field, IconButton, Input } from '../design-system/primitives'
import primaryLogo from '../assets/brand/SHOPYXI_primary_logo_transparent.png'
import standaloneSymbol from '../assets/brand/SHOPYXI_standalone_symbol.svg'
import './Register.css'

const fieldOrder = ['businessName', 'email', 'password', 'subdomain']
const key = name => `tenantRegistration.${name}`

export default function Register() {
    const { t } = useTranslation()
    const [admission, setAdmission] = useState('loading')
    const focusAdmissionRef = useRef(false)
    const pageTitle = `${t(key('title'))} | ${t('login.brandName')}`
    useEffect(() => {
        const previousTitle = document.title
        document.title = pageTitle
        return () => {
            if (document.title === pageTitle) document.title = previousTitle
        }
    }, [pageTitle])
    useEffect(() => {
        const controller = new AbortController()
        api.get('/platform/registration-policy', { signal: controller.signal })
            .then(response => {
                if (!controller.signal.aborted) setAdmission(response.data?.enabled === true ? 'open' : 'closed')
            })
            .catch(() => { if (!controller.signal.aborted) setAdmission('unavailable') })
        return () => controller.abort()
    }, [])

    return (
        <RegistrationLayout>
            {admission === 'open'
                ? <MerchantRegistrationForm onAdmissionClosed={() => {
                    focusAdmissionRef.current = true
                    setAdmission('closed')
                }} />
                : <RegistrationAdmission admission={admission} focusOnMount={focusAdmissionRef.current} />}
        </RegistrationLayout>
    )
}

// Register-local composition; no changes to shared layouts or token definitions.
function RegistrationLayout({ children }) {
    const { t } = useTranslation()
    return (
        <div className="pos-register" data-product="pos">
            <aside className="pos-register__brand" aria-label={t('login.brandName')}>
                <div className="pos-register__brand-content">
                    <div className="pos-register__identity">
                        <div className="pos-register__logo-plate">
                            <picture>
                                <source media="(width < 1024px)" srcSet={standaloneSymbol} />
                                <img className="pos-register__logo" src={primaryLogo} width="2400" height="747" alt="" />
                            </picture>
                        </div>
                        <p className="pos-register__wordmark">
                            <bdi dir="ltr" lang="en" className="pos-register__brand-name">{t('login.brandName')}</bdi>
                            <span className="pos-register__qualifier"><bdi dir="ltr" lang="en">{t('login.productQualifier')}</bdi></span>
                        </p>
                    </div>
                    <div className="pos-register__story">
                        <div>
                            <p className="pos-register__tagline">{t('login.tagline')}</p>
                            <p className="pos-register__description">{t('login.brandDescription')}</p>
                        </div>
                        <ul className="pos-register__proof" role="list">
                            <li><ShoppingBag size={28} aria-hidden="true" /><span>{t('login.brandProofSales')}</span></li>
                            <li><Layers3 size={28} aria-hidden="true" /><span>{t('login.brandProofInventory')}</span></li>
                            <li><ChartNoAxesColumnIncreasing size={28} aria-hidden="true" /><span>{t('login.brandProofReporting')}</span></li>
                        </ul>
                    </div>
                </div>
            </aside>
            <main className="pos-register__panel">
                <div className="pos-register__language"><LanguageSwitcher variant="login" /></div>
                {children}
            </main>
        </div>
    )
}

function RegistrationAdmission({ admission, focusOnMount }) {
    const { t } = useTranslation()
    const headingRef = useRef(null)
    useEffect(() => {
        // Includes replacement of the submitted form if admission closes.
        if (focusOnMount && admission !== 'loading') headingRef.current?.focus()
    }, [admission, focusOnMount])
    const loading = admission === 'loading'
    const heading = loading ? 'loading' : admission === 'unavailable' ? 'unavailableTitle' : 'closedTitle'
    return (
        <Card as="section" className="pos-register__card pos-register__admission" padding="none" aria-labelledby="registration-admission-heading">
            <div className="pos-register__state-icon" aria-hidden="true"><LockKeyhole size={28} /></div>
            <p className="pos-register__eyebrow">{t(key('title'))}</p>
            <div role="status" aria-atomic="true">
                <h1 className="pos-register__title" id="registration-admission-heading" tabIndex={-1} ref={headingRef}>
                    {t(key(`admission.${heading}`))}
                </h1>
                <div className="pos-register__state-copy">
                    <p>{t(key(`admission.${loading ? 'loadingDescription' : 'existingStore'}`))}</p>
                </div>
            </div>
            <Link to="/login" tabIndex={0} className="pos-button pos-button--primary pos-button--lg pos-button--full pos-register__primary">
                <span className="pos-button__label">{t(key('signIn'))}</span>
            </Link>
        </Card>
    )
}

function MerchantRegistrationForm({ onAdmissionClosed }) {
    const { t } = useTranslation()
    const navigate = useNavigate()
    const { isAuthenticated, login } = useAuthStore()
    useEffect(() => {
        if (isAuthenticated) navigate('/', { replace: true })
    }, [isAuthenticated, navigate])

    const [formData, setFormData] = useState({ businessName: '', email: '', password: '', subdomain: '' })
    const [errors, setErrors] = useState({})
    const [touched, setTouched] = useState({})
    const [subdomainStatus, setSubdomainStatus] = useState({
        checking: false, available: null, suggestions: [], message: '', storefrontUrl: ''
    })
    const [emailStatus, setEmailStatus] = useState({ checking: false, registered: false })
    const [isSubmitting, setIsSubmitting] = useState(false)
    const [submitError, setSubmitError] = useState('')
    const [showPassword, setShowPassword] = useState(false)
    const [summaryVersion, setSummaryVersion] = useState(0)
    const businessNameRef = useRef(null)
    const emailRef = useRef(null)
    const passwordRef = useRef(null)
    const subdomainRef = useRef(null)
    const errorSummaryRef = useRef(null)
    const emailCheckRequestIdRef = useRef(0)
    const registrationRequestIdRef = useRef(null)
    const submissionRef = useRef(false)
    const currentEmailRef = useRef('')
    const latestSubdomainRef = useRef(formData.subdomain)
    const messageText = message => message?.startsWith('tenantRegistration.') ? t(message) : message

    useEffect(() => { businessNameRef.current?.focus() }, [])
    useEffect(() => { currentEmailRef.current = formData.email }, [formData.email])
    useEffect(() => {
        if (summaryVersion) errorSummaryRef.current?.focus()
    }, [summaryVersion])

    // Existing eligibility/length/format rules; store keys so local validation
    // and asynchronous status copy retranslate without losing entered values.
    const validateField = useCallback((name, value) => {
        switch (name) {
            case 'businessName':
                if (!value.trim()) return key('validation.businessNameRequired')
                if (value.trim().length < 2) return key('validation.businessNameTooShort')
                if (value.trim().length > 100) return key('validation.businessNameTooLong')
                return ''
            case 'email':
                if (!value.trim()) return key('validation.emailRequired')
                if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)) return key('validation.emailInvalid')
                return ''
            case 'password':
                if (!value) return key('validation.passwordRequired')
                if (value.length < 8) return key('validation.passwordTooShort')
                return ''
            case 'subdomain': {
                if (!value.trim()) return key('validation.storeAddressRequired')
                if (/\p{Cc}/u.test(value)) return key('validation.storeAddressInvalidCharacters')
                const normalized = normalizeStoreSlug(value)
                if (normalized.length < 3) return key('validation.storeAddressTooShort')
                if (normalized.length > 63) return key('validation.storeAddressTooLong')
                if (!isStoreSlugFormatValid(normalized)) {
                    if (normalized.includes('--')) return key('validation.storeAddressExtraHyphens')
                    if (!/^[a-z0-9]/.test(normalized)) return key('validation.storeAddressStartsInvalid')
                    if (!/[a-z0-9]$/.test(normalized)) return key('validation.storeAddressEndsInvalid')
                    return key('validation.storeAddressInvalidCharacters')
                }
                return ''
            }
            default: return ''
        }
    }, [])

    const generateSubdomain = useCallback(businessName => {
        if (!businessName.trim()) return ''
        let slug = businessName.toLowerCase().trim()
            .replace(/[^a-z0-9\s-]/g, '').replace(/\s+/g, '-')
            .replace(/-+/g, '-').replace(/^-|-$/g, '')
        if (slug.length < 3) return ''
        if (slug.length > 63) slug = slug.substring(0, 63)
        return slug
    }, [])

    const checkSubdomainAvailability = useCallback(async subdomain => {
        const requestedSlug = subdomain
        const isCurrentSubdomainRequest = () => requestedSlug === latestSubdomainRef.current
        if (!requestedSlug || requestedSlug.length < 3) {
            if (isCurrentSubdomainRequest()) {
                setSubdomainStatus({ checking: false, available: null, suggestions: [], message: '', storefrontUrl: '' })
            }
            return
        }
        const error = validateField('subdomain', requestedSlug)
        if (error) {
            if (isCurrentSubdomainRequest()) {
                setSubdomainStatus({ checking: false, available: false, suggestions: [], message: error, storefrontUrl: '' })
            }
            return
        }
        if (!isCurrentSubdomainRequest()) return
        setSubdomainStatus(prev => ({ ...prev, checking: true }))
        try {
            const response = await api.get(`/platform/subdomain/check?slug=${encodeURIComponent(requestedSlug)}`)
            const { available, suggestions, reason, storefrontUrl } = response.data
            if (!isCurrentSubdomainRequest()) return
            setSubdomainStatus({
                checking: false, available, suggestions: suggestions || [], storefrontUrl: storefrontUrl || '',
                message: available ? key('status.addressAvailable') : (reason || key('status.addressTaken'))
            })
        } catch {
            if (!isCurrentSubdomainRequest()) return
            setSubdomainStatus({
                checking: false, available: null, suggestions: [], storefrontUrl: '',
                message: key('status.addressCheckUnavailable')
            })
        }
    }, [validateField])

    useEffect(() => {
        const requestedSlug = formData.subdomain
        const timer = setTimeout(() => {
            if (requestedSlug && touched.subdomain && requestedSlug === latestSubdomainRef.current) {
                void checkSubdomainAvailability(requestedSlug)
            }
        }, 400)
        return () => clearTimeout(timer)
    }, [formData.subdomain, touched.subdomain, checkSubdomainAvailability])

    const checkEmailExists = useCallback(async (email, requestId) => {
        const isCurrentEmailRequest = () => requestId === emailCheckRequestIdRef.current && currentEmailRef.current === email
        if (!email || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
            if (isCurrentEmailRequest()) setEmailStatus({ checking: false, registered: false })
            return
        }
        if (!isCurrentEmailRequest()) return
        setEmailStatus(prev => ({ ...prev, checking: true }))
        try {
            const response = await api.get(`/platform/email/check?email=${encodeURIComponent(email)}`)
            if (!isCurrentEmailRequest()) return
            setEmailStatus({ checking: false, registered: response.data.isRegistered })
            if (response.data.isRegistered) setErrors(prev => ({ ...prev, email: key('validation.emailRegistered') }))
        } catch {
            if (isCurrentEmailRequest()) setEmailStatus({ checking: false, registered: false })
        }
    }, [])

    useEffect(() => {
        if (!formData.email || !touched.email || errors.email) {
            emailCheckRequestIdRef.current += 1
            return
        }
        const email = formData.email
        const requestId = ++emailCheckRequestIdRef.current
        const timer = setTimeout(() => { void checkEmailExists(email, requestId) }, 300)
        return () => {
            clearTimeout(timer)
            if (emailCheckRequestIdRef.current === requestId) emailCheckRequestIdRef.current += 1
        }
    }, [formData.email, touched.email, errors.email, checkEmailExists])

    const handleChange = e => {
        registrationRequestIdRef.current = null
        const { name, value } = e.target
        const processedValue = name === 'subdomain' ? value.toLowerCase() : value
        if (name === 'subdomain') latestSubdomainRef.current = processedValue
        setFormData(prev => ({ ...prev, [name]: processedValue }))
        if (errors[name]) setErrors(prev => ({ ...prev, [name]: '' }))
        if (name === 'subdomain') {
            setSubdomainStatus(prev => ({ ...prev, available: null, message: '', storefrontUrl: '' }))
        }
        if (name === 'email') {
            currentEmailRef.current = processedValue
            emailCheckRequestIdRef.current += 1
            setEmailStatus({ checking: false, registered: false })
        }
    }
    const handleBlur = e => {
        const { name, value } = e.target
        setTouched(prev => ({ ...prev, [name]: true }))
        const error = validateField(name, value)
        setErrors(prev => ({ ...prev, [name]: error }))
        if (name === 'subdomain' && !error) {
            const normalizedSubdomain = normalizeStoreSlug(value)
            latestSubdomainRef.current = normalizedSubdomain
            setFormData(prev => ({ ...prev, subdomain: normalizedSubdomain }))
        }
        if (name === 'businessName' && value.trim() && !formData.subdomain) {
            const generatedSubdomain = generateSubdomain(value)
            if (generatedSubdomain) {
                latestSubdomainRef.current = generatedSubdomain
                setFormData(prev => ({ ...prev, subdomain: generatedSubdomain }))
                setTouched(prev => ({ ...prev, subdomain: true }))
            }
        }
    }
    const handleSuggestionClick = suggestion => {
        latestSubdomainRef.current = suggestion
        setFormData(prev => ({ ...prev, subdomain: suggestion }))
        setTouched(prev => ({ ...prev, subdomain: true }))
        void checkSubdomainAvailability(suggestion)
        subdomainRef.current?.focus()
    }
    const isFormValid = () => (
        formData.businessName.trim().length >= 2 &&
        /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(formData.email) &&
        formData.password.length >= 8 &&
        subdomainStatus.available === true &&
        !emailStatus.registered
    )
    const handleSubmit = async e => {
        e.preventDefault()
        if (submissionRef.current) return
        const newErrors = {}
        Object.keys(formData).forEach(name => {
            const error = validateField(name, formData[name])
            if (error) newErrors[name] = error
        })
        if (Object.keys(newErrors).length > 0) {
            setErrors(newErrors)
            setTouched({ businessName: true, email: true, password: true, subdomain: true })
            setSummaryVersion(version => version + 1)
            return
        }
        if (!subdomainStatus.available) {
            setErrors(prev => ({ ...prev, subdomain: key('validation.availableStoreAddress') }))
            setTouched(prev => ({ ...prev, subdomain: true }))
            setSummaryVersion(version => version + 1)
            return
        }
        submissionRef.current = true
        setIsSubmitting(true)
        setSubmitError('')
        // Same payload, request-ID lifecycle, session authority and navigation.
        registrationRequestIdRef.current ||= `reg-${crypto.randomUUID()}`
        const requestId = registrationRequestIdRef.current
        try {
            await api.post('/platform/register', {
                businessName: formData.businessName.trim(),
                email: formData.email.trim().toLowerCase(),
                password: formData.password,
                subdomain: normalizeStoreSlug(formData.subdomain)
            }, { headers: { 'X-Request-Id': requestId } })
            try {
                const sessionResponse = await api.get('/auth/session')
                login(requirePosVendorSessionUserAuthority(sessionResponse.data?.user ?? null))
            } catch {
                // Existing bootstrap owns session hydration if this read fails.
            }
            navigate('/', { replace: true })
        } catch (err) {
            const errorData = err.response?.data
            if (err.response?.status === 403 || errorData?.error === 'REGISTRATION_DISABLED') {
                onAdmissionClosed()
                return
            }
            if (errorData?.fieldErrors) {
                setErrors(errorData.fieldErrors)
                setTouched({ businessName: true, email: true, password: true, subdomain: true })
            }
            if (errorData?.suggestions && errorData.suggestions.length > 0) {
                setSubdomainStatus(prev => ({
                    ...prev, available: false, suggestions: errorData.suggestions,
                    message: errorData.message || key('status.addressTaken')
                }))
            }
            setSubmitError(errorData?.message || key('messages.genericError'))
            setSummaryVersion(version => version + 1)
        } finally {
            submissionRef.current = false
            setIsSubmitting(false)
        }
    }
    const fieldLabel = name => t(key(`fields.${name === 'subdomain' ? 'storeAddress' : name}`))
    const visibleErrors = fieldOrder.filter(name => errors[name] && touched[name])
    const fieldError = name => touched[name] ? messageText(errors[name]) : undefined

    return (
        <Card className="pos-register__card" padding="none">
            <header className="pos-register__header">
                <h1 className="pos-register__title">{t(key('title'))}</h1>
                <p className="pos-register__subtitle">{t(key('formDescription'))}</p>
            </header>
            {summaryVersion > 0 && (submitError || visibleErrors.length > 0) ? (
                <div className="pos-register__alert" role="alert" tabIndex={-1} ref={errorSummaryRef} aria-labelledby="registration-error-title">
                    <CircleAlert size={20} aria-hidden="true" />
                    <div>
                        <h2 id="registration-error-title">{t(key('messages.reviewDetails'))}</h2>
                        {submitError ? <p dir="auto">{messageText(submitError)}</p> : null}
                        {visibleErrors.length ? <ul>{visibleErrors.map(name => (
                            <li key={name}><a href={`#${name}`} onClick={event => {
                                event.preventDefault()
                                document.getElementById(name)?.focus()
                            }}>{fieldLabel(name)}: {messageText(errors[name])}</a></li>
                        ))}</ul> : null}
                    </div>
                </div>
            ) : null}
            <div className="sr-only" role="status">{isSubmitting ? t(key('actions.creatingStore')) : ''}</div>
            <form className="pos-register__form" aria-label={t(key('title'))} onSubmit={handleSubmit} noValidate aria-busy={isSubmitting || undefined}>
                <div className="pos-register__field">
                    <Field id="businessName" label={fieldLabel('businessName')} error={fieldError('businessName')}>
                        {({ describedBy, invalid }) => (
                            <Input ref={businessNameRef} type="text" id="businessName" name="businessName"
                                value={formData.businessName} onChange={handleChange} onBlur={handleBlur}
                                disabled={isSubmitting} required autoComplete="organization" maxLength={100} dir="auto"
                                invalid={invalid} aria-describedby={['businessName-helper', describedBy].filter(Boolean).join(' ')} />
                        )}
                    </Field>
                    <p className="pos-register__helper" id="businessName-helper">{t(key('helpers.businessName'))}</p>
                </div>
                <div className="pos-register__field">
                    <Field id="email" label={fieldLabel('email')} error={fieldError('email')} invalid={emailStatus.registered}>
                        {({ describedBy, invalid }) => (
                            <Input ref={emailRef} type="email" id="email" name="email"
                                value={formData.email} onChange={handleChange} onBlur={handleBlur}
                                disabled={isSubmitting} required autoComplete="email" inputMode="email" dir="ltr" autoCapitalize="none" spellCheck={false}
                                invalid={invalid} aria-describedby={['email-helper', 'registration-email-status', describedBy].filter(Boolean).join(' ')} />
                        )}
                    </Field>
                    <p className="pos-register__helper" id="email-helper">{t(key('helpers.email'))}</p>
                    <div id="registration-email-status" className="pos-register__helper" role="status">
                        {emailStatus.checking ? t(key('status.checkingEmail')) : ''}
                    </div>
                    {emailStatus.registered ? <Link className="pos-register__text-link" to="/login">{t(key('signIn'))}</Link> : null}
                </div>
                <div className="pos-register__field">
                    <Field id="password" label={fieldLabel('password')} error={fieldError('password')}>
                        {({ describedBy, invalid }) => (
                            <div className="pos-register__password">
                                <Input ref={passwordRef} type={showPassword ? 'text' : 'password'} id="password" name="password"
                                    value={formData.password} onChange={handleChange} onBlur={handleBlur}
                                    disabled={isSubmitting} required autoComplete="new-password" dir="ltr" autoCapitalize="none" spellCheck={false}
                                    invalid={invalid} aria-describedby={['password-helper', describedBy].filter(Boolean).join(' ')} />
                                <IconButton className="pos-register__password-toggle" variant="ghost" size="sm"
                                    label={t(key('actions.showPassword'))} icon={showPassword ? <EyeOff size={18} /> : <Eye size={18} />}
                                    onClick={() => setShowPassword(visible => !visible)} disabled={isSubmitting}
                                    aria-controls="password" aria-pressed={showPassword} />
                            </div>
                        )}
                    </Field>
                    <p className="pos-register__helper" id="password-helper">{t(key('helpers.password'))}</p>
                </div>
                <div className="pos-register__field">
                    <Field id="subdomain" label={fieldLabel('subdomain')} error={fieldError('subdomain')} invalid={subdomainStatus.available === false}>
                        {({ describedBy, invalid }) => (
                            <Input ref={subdomainRef} type="text" id="subdomain" name="subdomain"
                                value={formData.subdomain} onChange={handleChange} onBlur={handleBlur}
                                disabled={isSubmitting} required autoComplete="off" inputMode="url" dir="ltr"
                                pattern="[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?" spellCheck={false} autoCapitalize="none" maxLength={63}
                                invalid={invalid} aria-describedby={['subdomain-status', describedBy].filter(Boolean).join(' ')} />
                        )}
                    </Field>
                    <div id="subdomain-status" className="pos-register__availability" role="status" aria-atomic="true">
                        {subdomainStatus.checking ? <p>{t(key('status.checkingAvailability'))}</p> : subdomainStatus.message ? (
                            <div>
                                <p className={subdomainStatus.available === true ? 'pos-register__available' : subdomainStatus.available === false ? 'pos-register__unavailable' : ''}>
                                    {subdomainStatus.available === true ? <Check size={18} aria-hidden="true" /> : <CircleAlert size={18} aria-hidden="true" />}
                                    <span dir="auto">{messageText(subdomainStatus.message)}</span>
                                </p>
                                {subdomainStatus.available === true && subdomainStatus.storefrontUrl ? (
                                    <p>{t(key('status.yourStore'))}: <bdi dir="ltr" className="pos-register__url">{subdomainStatus.storefrontUrl}</bdi></p>
                                ) : null}
                            </div>
                        ) : null}
                    </div>
                    {!subdomainStatus.checking && subdomainStatus.available === false && subdomainStatus.suggestions.length > 0 ? (
                        <div className="pos-register__suggestions">
                            <p className="pos-register__helper">{t(key('status.trySuggestions'))}</p>
                            <div>{subdomainStatus.suggestions.map(suggestion => (
                                <Button key={suggestion} variant="secondary" size="sm" disabled={isSubmitting}
                                    onClick={() => handleSuggestionClick(suggestion)}><bdi dir="ltr">{suggestion}</bdi></Button>
                            ))}</div>
                        </div>
                    ) : null}
                </div>
                <Button className="pos-register__primary" type="submit" variant="primary" size="lg" fullWidth disabled={!isFormValid()} loading={isSubmitting}>
                    {t(key(`actions.${isSubmitting ? 'creatingStore' : 'createStore'}`))}
                </Button>
                <p className="pos-register__legal">
                    {t(key('legal.prefix'))}{' '}
                    <a href="/terms" target="_blank" rel="noopener noreferrer">{t(key('legal.termsOfService'))}</a>{' '}
                    {t(key('legal.and'))}{' '}
                    <a href="/privacy" target="_blank" rel="noopener noreferrer">{t(key('legal.privacyPolicy'))}</a>.
                </p>
            </form>
            <p className="pos-register__prompt">
                {t(key('signInPrompt'))}{' '}<Link to="/login" tabIndex={0}>{t(key('signIn'))}</Link>
            </p>
        </Card>
    )
}
