import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Alert, Button } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { ConsentCheckbox } from './ConsentCheckbox'
import { GoogleSignInButton } from './GoogleSignInButton'
import { useAuthProviders, useGoogleSignIn } from './hooks'
import { googleErrorMessage, NONE } from './googleAuthModel'
import type { ConsentChoices } from './googleAuthModel'

/**
 * "Sign in with Google" for the login and registration pages: a divider and Google's button, shown only when the server has the
 * feature switched on. A Google identity without an account yet first has to accept the Terms (and may give the two optional
 * consents), exactly as on the registration form; its ID token is kept in memory for that one step.
 */
export function GoogleAuthSection({ variant, defaults = NONE }: { variant: 'signin' | 'signup'; defaults?: ConsentChoices }) {
  const { t } = useTranslation()
  const providers = useAuthProviders()
  const google = useGoogleSignIn()
  const [pendingToken, setPendingToken] = useState<string | null>(null)
  const [choices, setChoices] = useState<ConsentChoices>(NONE)

  const clientId = providers.data?.google?.clientId
  if (!clientId) return null

  const submit = (idToken: string, consents: ConsentChoices) =>
    google.mutate(
      { idToken, ...consents },
      {
        onSuccess: () => setPendingToken(null),
        onError: (error) => {
          if (error instanceof ApiError && error.code === 'registration_required') {
            setChoices(consents)
            setPendingToken(idToken)
          } else {
            setPendingToken(null)
          }
        },
      },
    )

  const failure = google.error instanceof ApiError && google.error.code === 'registration_required' ? null : google.error

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3 text-xs uppercase tracking-wide text-muted" aria-hidden>
        <span className="h-px flex-1 bg-line" />
        {t('auth.or')}
        <span className="h-px flex-1 bg-line" />
      </div>

      {failure && <Alert tone="danger">{googleErrorMessage(failure, t)}</Alert>}

      {pendingToken ? (
        <form
          className="space-y-3 rounded-xl border border-line p-4"
          onSubmit={(event) => {
            event.preventDefault()
            submit(pendingToken, choices)
          }}
        >
          <p className="text-sm">{t('auth.googleNewAccount')}</p>
          <ConsentCheckbox label={t('auth.acceptTerms')} checked={choices.acceptTerms} onChange={(v) => setChoices({ ...choices, acceptTerms: v })} />
          <ConsentCheckbox
            label={`${t('auth.healthConsent')} (${t('common.optional')})`}
            checked={choices.healthDataConsent}
            onChange={(v) => setChoices({ ...choices, healthDataConsent: v })}
          />
          <ConsentCheckbox
            label={`${t('auth.aiConsent')} (${t('common.optional')})`}
            checked={choices.aiProcessingConsent}
            onChange={(v) => setChoices({ ...choices, aiProcessingConsent: v })}
          />
          <p className="text-xs text-muted">{t('auth.consentsOptional')}</p>
          <div className="flex gap-2">
            <Button type="submit" loading={google.isPending} disabled={!choices.acceptTerms}>
              {t('auth.googleCreateAccount')}
            </Button>
            <Button type="button" variant="ghost" onClick={() => setPendingToken(null)}>
              {t('common.cancel')}
            </Button>
          </div>
        </form>
      ) : (
        <GoogleSignInButton
          clientId={clientId}
          text={variant === 'signup' ? 'signup_with' : 'signin_with'}
          onCredential={(idToken) => submit(idToken, defaults)}
        />
      )}
    </div>
  )
}
