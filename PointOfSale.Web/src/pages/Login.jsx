import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { ChartNoAxesColumnIncreasing, CircleAlert, Eye, EyeOff, Layers3, Lock, LockKeyhole, LogIn, ShoppingBag, UserRound } from 'lucide-react'
import { useAuthStore } from '../store/authStore'
import { useSettings } from '../contexts/SettingsContext'
import LanguageSwitcher from '../components/LanguageSwitcher'
import api from '../services/api'
import { requirePosVendorSessionUserAuthority, resolvePosRouteRedirect } from '../auth/posVendorSessionAuthority'
import { Button, Card, Field, IconButton, Input } from '../design-system/primitives'
import primaryLogo from '../assets/brand/SHOPYXI_primary_logo_transparent.png'
import standaloneSymbol from '../assets/brand/SHOPYXI_standalone_symbol.svg'
import './Login.css'

function getLoginErrorKey(error) {
    const { data, status } = error?.response ?? {}
    // Keep the meanings of the API's public messages, not arbitrary response
    // bodies or exception text (which may include request or diagnostic data).
    if (data?.code === 'SESSION_ROTATED') return 'login.sessionChanged'
    if (status === 401 && data?.message === 'Account is disabled') return 'login.accountDisabled'
    if (status === 401 && data?.message === 'Invalid username or password') return 'login.invalidCredentials'
    if (status === 429) return 'login.tooManyAttempts'
    if (error?.code === 'ECONNABORTED' || error?.code === 'ETIMEDOUT' || error?.code === 'ERR_NETWORK') {
        return 'login.connectionError'
    }
    return 'login.genericError'
}

export default function Login() {
    const { t } = useTranslation()
    const [username, setUsername] = useState('')
    const [password, setPassword] = useState('')
    const [error, setError] = useState(null)
    const [loading, setLoading] = useState(false)
    const [didSignIn, setDidSignIn] = useState(false)
    const [showPassword, setShowPassword] = useState(false)
    const formErrorRef = useRef(null)
    const submissionRef = useRef(false)

    const isAuthenticated = useAuthStore((state) => state.isAuthenticated)
    const login = useAuthStore((state) => state.login)
    const { reloadSettings } = useSettings()
    const location = useLocation()
    const navigate = useNavigate()
    const isSwitchingAccount = new URLSearchParams(location.search).get('switchAccount') === '1'
    const redirectTo = resolvePosRouteRedirect(
        location.state?.from
            ? `${location.state.from.pathname || ''}${location.state.from.search || ''}${location.state.from.hash || ''}`
            : '/',
    )

    const pageTitle = `${t('login.signIn')} | ${t('login.brandName')}`
    useEffect(() => {
        const previousTitle = document.title
        document.title = pageTitle
        return () => {
            if (document.title === pageTitle) document.title = previousTitle
        }
    }, [pageTitle])

    useEffect(() => {
        // An explicit sign-in owns its navigation, including the settings step.
        if (isAuthenticated && (!isSwitchingAccount || didSignIn) && !loading && !submissionRef.current) {
            navigate(redirectTo, { replace: true })
        }
    }, [didSignIn, isAuthenticated, isSwitchingAccount, loading, navigate, redirectTo])

    useEffect(() => {
        if (error) {
            formErrorRef.current?.focus()
        }
    }, [error])

    const handleSubmit = async (e) => {
        e.preventDefault()
        if (submissionRef.current) {
            return
        }

        const normalizedUsername = username.trim()
        if (!normalizedUsername || !password.trim()) {
            setError({ validation: true })
            return
        }

        submissionRef.current = true
        setError(null)
        setLoading(true)

        try {
            await api.post('/auth/login', { username: normalizedUsername, password })
            const sessionResponse = await api.get('/auth/session')
            login(requirePosVendorSessionUserAuthority(sessionResponse.data?.user ?? null))

            // Reload settings after successful login
            try {
                await reloadSettings()
            } catch {
                if (import.meta.env.DEV) {
                    console.warn('Failed to load settings, but continuing with login.')
                }
            }

            setDidSignIn(true)
        } catch (err) {
            setError({ key: getLoginErrorKey(err) })
        } finally {
            submissionRef.current = false
            setLoading(false)
        }
    }

    const usernameInvalid = Boolean(error?.validation && !username.trim())
    const passwordInvalid = Boolean(error?.validation && !password.trim())
    const authenticationError = error && !error.validation
    const validationKey = usernameInvalid && passwordInvalid ? 'login.enterCredentials'
        : usernameInvalid ? 'login.enterUsername' : passwordInvalid ? 'login.enterPassword' : null
    const errorKey = authenticationError ? error.key : validationKey

    return (
        <div className="pos-login" data-product="pos">
            <aside className="pos-login__brand" aria-label={t('login.brandName')}>
                <div className="pos-login__brand-content">
                    <div className="pos-login__brand-identity">
                        <div className="pos-login__brand-plate">
                            <picture>
                                <source media="(width < 1024px)" srcSet={standaloneSymbol} />
                                <img className="pos-login__brand-logo" src={primaryLogo} width="2400" height="747" alt="" />
                            </picture>
                        </div>
                        <p className="pos-login__wordmark">
                             <bdi dir="ltr" lang="en" className="pos-login__wordmark-name">{t('login.brandName')}</bdi>
                             <span className="pos-login__wordmark-qualifier"><bdi dir="ltr" lang="en">{t('login.productQualifier')}</bdi></span>
                        </p>
                    </div>

                    <div className="pos-login__brand-story">
                        <div className="pos-login__brand-message">
                            <p className="pos-login__tagline">{t('login.tagline')}</p>
                            <p className="pos-login__brand-description">{t('login.brandDescription')}</p>
                        </div>
                        <ul className="pos-login__proof" role="list">
                            <li className="pos-login__proof-item">
                                <ShoppingBag size={28} aria-hidden="true" />
                                <span>{t('login.brandProofSales')}</span>
                            </li>
                            <li className="pos-login__proof-item">
                                <Layers3 size={28} aria-hidden="true" />
                                <span>{t('login.brandProofInventory')}</span>
                            </li>
                            <li className="pos-login__proof-item">
                                <ChartNoAxesColumnIncreasing size={28} aria-hidden="true" />
                                <span>{t('login.brandProofReporting')}</span>
                            </li>
                        </ul>
                    </div>
                </div>
            </aside>

            <main className="pos-login__panel">
                <div className="pos-login__language">
                    <LanguageSwitcher variant="login" />
                </div>

                <Card className="pos-login__card" padding="none">
                    <header className="pos-login__header">
                        <h1 className="pos-login__title">{t('login.welcomeBack')}</h1>
                        <p className="pos-login__subtitle">{t('login.subheading')}</p>
                    </header>

                    {errorKey ? (
                        <div
                            className="pos-login__alert"
                            id="pos-login-error"
                            role="alert"
                            tabIndex={-1}
                            ref={formErrorRef}
                            dir="auto"
                        >
                            <CircleAlert size={18} aria-hidden="true" />
                            <span>{t(errorKey)}</span>
                        </div>
                    ) : null}

                    <div className="sr-only" role="status">{loading ? t('login.signingIn') : ''}</div>
                    <form className="pos-login__form" aria-label={t('login.signIn')} onSubmit={handleSubmit} aria-busy={loading || undefined}>
                        <div className="pos-login__credential">
                            <Field
                                id="pos-login-username"
                                label={t('login.usernameOrEmail')}
                                className="pos-input-field"
                            >
                                <Input
                                    id="pos-login-username"
                                    type="text"
                                    value={username}
                                    onChange={(e) => setUsername(e.target.value)}
                                    disabled={loading}
                                    required
                                    autoFocus
                                    autoComplete="username"
                                    dir="auto"
                                    autoCapitalize="none"
                                    spellCheck={false}
                                    invalid={usernameInvalid}
                                    aria-describedby={authenticationError || usernameInvalid ? 'pos-login-error' : undefined}
                                />
                            </Field>
                            <UserRound className="pos-login__input-icon" size={20} aria-hidden="true" />
                        </div>

                        <div className="pos-login__password">
                            <Field
                                id="pos-login-password"
                                label={t('login.password')}
                                className="pos-input-field"
                            >
                                <Input
                                    id="pos-login-password"
                                    type={showPassword ? 'text' : 'password'}
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)}
                                    disabled={loading}
                                    required
                                    autoComplete="current-password"
                                    dir="ltr"
                                    autoCapitalize="none"
                                    spellCheck={false}
                                    invalid={passwordInvalid}
                                    aria-describedby={authenticationError || passwordInvalid ? 'pos-login-error' : undefined}
                                />
                            </Field>
                            <LockKeyhole className="pos-login__input-icon" size={20} aria-hidden="true" />
                            <IconButton
                                className="pos-login__password-toggle"
                                variant="ghost"
                                size="sm"
                                label={t('login.showPassword')}
                                icon={showPassword ? <EyeOff size={18} /> : <Eye size={18} />}
                                onClick={() => setShowPassword((visible) => !visible)}
                                disabled={loading}
                                aria-controls="pos-login-password"
                                aria-pressed={showPassword}
                            />
                        </div>

                        <Button
                            className="pos-login__submit"
                            type="submit"
                            variant="primary"
                            size="lg"
                            fullWidth
                            loading={loading}
                            leadingIcon={<LogIn size={20} />}
                        >
                            {loading ? t('login.signingIn') : t('login.signIn')}
                        </Button>
                    </form>

                    <p className="pos-login__prompt">
                        {t('login.noAccount')}{' '}
                        {/* Some WebKit configurations skip implicit link tab stops. */}
                        <a href="/register" tabIndex={0}>{t('login.createStore')}</a>
                    </p>

                    <p className="pos-login__secure">
                        <Lock size={14} aria-hidden="true" />
                        <span>{t('login.secureConnection')}</span>
                    </p>
                </Card>
            </main>
        </div>
    )
}
