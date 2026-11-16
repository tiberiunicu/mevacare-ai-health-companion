import { useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router-dom'
import { Alert, Button, Field, TextInput } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { AuthLayout } from './AuthPages'
import { useAuthProviders, useForgotPassword, useResetPassword } from './hooks'

export function ForgotPasswordPage() {
  const { t, i18n } = useTranslation()
  const providers = useAuthProviders()
  const forgot = useForgotPassword(i18n.language)
  const [email, setEmail] = useState('')

  const unavailable = providers.data?.passwordReset === false

  const submit = (event: FormEvent) => {
    event.preventDefault()
    forgot.mutate(email)
  }

  return (
    <AuthLayout title={t('auth.forgotTitle')} subtitle={t('auth.forgotSubtitle')}>
      {unavailable ? (
        <Alert tone="info">{t('auth.forgotUnavailable')}</Alert>
      ) : forgot.isSuccess ? (
        <Alert tone="success">{t('auth.forgotSent')}</Alert>
      ) : (
        <form onSubmit={submit} className="space-y-4" noValidate>
          {forgot.error && (
            <Alert tone="danger">
              {forgot.error instanceof ApiError && forgot.error.status === 429 ? forgot.error.message : t('common.genericError')}
            </Alert>
          )}
          <Field label={t('auth.email')}>
            {(p) => <TextInput {...p} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />}
          </Field>
          <Button type="submit" className="w-full" loading={forgot.isPending} disabled={!email.trim()}>
            {forgot.isPending ? t('auth.sending') : t('auth.sendLink')}
          </Button>
        </form>
      )}
      <p className="text-sm">
        <Link to="/login" className="font-semibold text-primary hover:underline">
          {t('auth.backToSignIn')}
        </Link>
      </p>
    </AuthLayout>
  )
}

/** The token travels in the URL fragment (after the #), which browsers never send to a server; it is read once and then removed from the address bar. */
function readToken(hash: string): string {
  return new URLSearchParams(hash.replace(/^#/, '')).get('token') ?? ''
}

export function ResetPasswordPage() {
  const { t, i18n } = useTranslation()
  const location = useLocation()
  const reset = useResetPassword(i18n.language)
  const [token] = useState(() => {
    const value = readToken(location.hash)
    if (value) window.history.replaceState(null, '', location.pathname)
    return value
  })
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')

  const mismatch = confirm.length > 0 && password !== confirm
  const invalidLink =
    !token || (reset.error instanceof ApiError && reset.error.code === 'invalid_reset_token')

  const fieldError = (name: string) => {
    const error = reset.error
    return error instanceof ApiError && error.fieldErrors ? error.fieldErrors[name]?.[0] : undefined
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (password === confirm) reset.mutate({ token, newPassword: password })
  }

  const otherError =
    reset.error && !invalidLink && !(reset.error instanceof ApiError && reset.error.fieldErrors)
      ? reset.error instanceof ApiError && (reset.error.status === 429 || reset.error.code === 'weak_password')
        ? reset.error.message
        : t('common.genericError')
      : null

  return (
    <AuthLayout title={t('auth.resetTitle')} subtitle={t('auth.resetSubtitle')}>
      {reset.isSuccess ? (
        <>
          <Alert tone="success">{t('auth.resetDone')}</Alert>
          <Link to="/login" className="inline-block font-semibold text-primary hover:underline">
            {t('auth.signIn')}
          </Link>
        </>
      ) : invalidLink ? (
        <>
          <Alert tone="danger">{t('auth.resetInvalid')}</Alert>
          <Link to="/forgot-password" className="inline-block font-semibold text-primary hover:underline">
            {t('auth.requestNewLink')}
          </Link>
        </>
      ) : (
        <form onSubmit={submit} className="space-y-4" noValidate>
          {otherError && <Alert tone="danger">{otherError}</Alert>}
          <Field label={t('auth.newPassword')} hint={t('auth.passwordHint')} error={fieldError('NewPassword')}>
            {(p) => (
              <TextInput {...p} type="password" autoComplete="new-password" required minLength={10} value={password} onChange={(e) => setPassword(e.target.value)} />
            )}
          </Field>
          <Field label={t('auth.confirmPassword')} error={mismatch ? t('auth.passwordsDiffer') : undefined}>
            {(p) => (
              <TextInput {...p} type="password" autoComplete="new-password" required value={confirm} onChange={(e) => setConfirm(e.target.value)} />
            )}
          </Field>
          <Button type="submit" className="w-full" loading={reset.isPending} disabled={!password || password !== confirm}>
            {reset.isPending ? t('auth.resetting') : t('auth.resetPassword')}
          </Button>
        </form>
      )}
    </AuthLayout>
  )
}
