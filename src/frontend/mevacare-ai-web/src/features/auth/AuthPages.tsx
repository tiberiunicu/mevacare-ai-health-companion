import { useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, Outlet, useLocation } from 'react-router-dom'
import { Alert, Button, Field, Spinner, TextInput } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { useAuthStore } from '../../stores/auth'
import { ConsentCheckbox } from './ConsentCheckbox'
import { GoogleAuthSection } from './GoogleAuthSection'
import { useAuthProviders, useLogin, useRegister } from './hooks'

export function RequireAuth() {
  const { t } = useTranslation()
  const status = useAuthStore((s) => s.status)
  const location = useLocation()
  if (status === 'loading') return <div className="p-8"><Spinner label={t('common.loading')} /></div>
  if (status === 'anonymous') return <Navigate to="/login" replace state={{ from: location.pathname }} />
  return <Outlet />
}

export function AuthLayout({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  const { t } = useTranslation()
  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      <div className="relative hidden overflow-hidden bg-gradient-to-br from-primary to-ai p-12 text-white lg:flex lg:flex-col lg:justify-between">
        <div className="flex items-center gap-3">
          <img src="/favicon.svg" alt="" className="size-10 rounded-xl bg-white/10" />
          <span className="text-lg font-semibold">MevaCare</span>
        </div>
        <div className="max-w-md space-y-4">
          <h2 className="text-4xl font-semibold leading-tight tracking-tight">{t('app.tagline')}</h2>
          <p className="text-white/80">{t('app.disclaimer')}</p>
        </div>
        <div aria-hidden className="pointer-events-none absolute -bottom-24 -right-24 size-96 rounded-full bg-white/10 blur-2xl" />
      </div>
      <main className="flex items-center justify-center p-6 sm:p-10">
        <div className="w-full max-w-md space-y-6">
          <div>
            <h1 className="text-2xl font-semibold tracking-tight sm:text-3xl">{title}</h1>
            <p className="mt-1 text-sm text-muted">{subtitle}</p>
          </div>
          {children}
          <p className="text-xs leading-relaxed text-muted lg:hidden">{t('app.disclaimer')}</p>
        </div>
      </main>
    </div>
  )
}

function errorMessage(error: unknown, t: (key: string) => string): string | null {
  if (!error) return null
  if (error instanceof ApiError) {
    if (error.code === 'email_in_use') return t('auth.emailInUse')
    if (error.status === 401) return t('auth.invalidCredentials')
    if (error.status === 429) return error.message
    if (error.status >= 500) return t('common.genericError')
    return error.message
  }
  return t('common.networkError')
}

export function LoginPage() {
  const { t } = useTranslation()
  const status = useAuthStore((s) => s.status)
  const location = useLocation()
  const login = useLogin()
  const providers = useAuthProviders()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')

  if (status === 'authenticated') {
    return <Navigate to={(location.state as { from?: string } | null)?.from ?? '/'} replace />
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    login.mutate({ email, password })
  }

  return (
    <AuthLayout title={t('auth.signInTitle')} subtitle={t('auth.signInSubtitle')}>
      <form onSubmit={submit} className="space-y-4" noValidate>
        {login.error && <Alert tone="danger">{errorMessage(login.error, t)}</Alert>}
        <Field label={t('auth.email')}>
          {(p) => <TextInput {...p} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />}
        </Field>
        <Field label={t('auth.password')}>
          {(p) => (
            <TextInput {...p} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          )}
        </Field>
        {providers.data?.passwordReset && (
          <p className="-mt-2 text-sm">
            <Link to="/forgot-password" className="font-semibold text-primary hover:underline">
              {t('auth.forgotPassword')}
            </Link>
          </p>
        )}
        <Button type="submit" className="w-full" loading={login.isPending}>
          {login.isPending ? t('auth.signingIn') : t('auth.signIn')}
        </Button>
      </form>
      <GoogleAuthSection variant="signin" />
      <p className="text-sm text-muted">
        {t('auth.noAccount')}{' '}
        <Link to="/register" className="font-semibold text-primary hover:underline">
          {t('auth.createAccount')}
        </Link>
      </p>
    </AuthLayout>
  )
}

export function RegisterPage() {
  const { t } = useTranslation()
  const status = useAuthStore((s) => s.status)
  const register = useRegister()
  const [form, setForm] = useState({
    displayName: '',
    email: '',
    password: '',
    acceptTerms: false,
    healthDataConsent: false,
    aiProcessingConsent: false,
  })

  if (status === 'authenticated') return <Navigate to="/" replace />

  const fieldError = (name: string) => {
    const error = register.error
    if (!(error instanceof ApiError) || !error.fieldErrors) return undefined
    return error.fieldErrors[name]?.[0]
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    register.mutate(form)
  }

  const showBanner = register.error && !(register.error instanceof ApiError && register.error.fieldErrors)

  return (
    <AuthLayout title={t('auth.signUpTitle')} subtitle={t('auth.signUpSubtitle')}>
      <form onSubmit={submit} className="space-y-4" noValidate>
        {showBanner && <Alert tone="danger">{errorMessage(register.error, t)}</Alert>}
        <Field label={t('auth.displayName')} error={fieldError('DisplayName')}>
          {(p) => (
            <TextInput {...p} autoComplete="name" required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
          )}
        </Field>
        <Field label={t('auth.email')} error={fieldError('Email')}>
          {(p) => (
            <TextInput {...p} type="email" autoComplete="email" required value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          )}
        </Field>
        <Field label={t('auth.password')} hint={t('auth.passwordHint')} error={fieldError('Password')}>
          {(p) => (
            <TextInput
              {...p}
              type="password"
              autoComplete="new-password"
              required
              minLength={10}
              value={form.password}
              onChange={(e) => setForm({ ...form, password: e.target.value })}
            />
          )}
        </Field>

        <fieldset className="space-y-3 rounded-xl border border-line p-4">
          <ConsentCheckbox
            label={t('auth.acceptTerms')}
            checked={form.acceptTerms}
            onChange={(v) => setForm({ ...form, acceptTerms: v })}
            error={fieldError('AcceptTerms')}
          />
          <ConsentCheckbox
            label={`${t('auth.healthConsent')} (${t('common.optional')})`}
            checked={form.healthDataConsent}
            onChange={(v) => setForm({ ...form, healthDataConsent: v })}
          />
          <ConsentCheckbox
            label={`${t('auth.aiConsent')} (${t('common.optional')})`}
            checked={form.aiProcessingConsent}
            onChange={(v) => setForm({ ...form, aiProcessingConsent: v })}
          />
          <p className="text-xs text-muted">{t('auth.consentsOptional')}</p>
        </fieldset>

        <Button type="submit" className="w-full" loading={register.isPending}>
          {register.isPending ? t('auth.creating') : t('auth.createAccount')}
        </Button>
      </form>
      <GoogleAuthSection
        variant="signup"
        defaults={{ acceptTerms: form.acceptTerms, healthDataConsent: form.healthDataConsent, aiProcessingConsent: form.aiProcessingConsent }}
      />
      <p className="text-sm text-muted">
        {t('auth.haveAccount')}{' '}
        <Link to="/login" className="font-semibold text-primary hover:underline">
          {t('auth.signIn')}
        </Link>
      </p>
    </AuthLayout>
  )
}
