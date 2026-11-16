import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { loadGoogleScript } from './googleScript'

interface Props {
  clientId: string
  /** Wording of the Google-rendered button. */
  text?: 'signin_with' | 'signup_with' | 'continue_with'
  /** Receives the signed Google ID token. The server verifies it; the browser never decides who the user is. */
  onCredential: (idToken: string) => void
}

/**
 * Google's own "Sign in with Google" button. Google draws it (in an iframe) to follow its branding rules; this component only
 * loads the script, configures the client id and hands back the ID token the user's click produced.
 */
export function GoogleSignInButton({ clientId, text = 'signin_with', onCredential }: Props) {
  const { i18n, t } = useTranslation()
  const container = useRef<HTMLDivElement>(null)
  const latest = useRef(onCredential)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    latest.current = onCredential
  }, [onCredential])

  useEffect(() => {
    let cancelled = false
    loadGoogleScript().then(
      () => {
        const host = container.current
        const google = window.google
        if (cancelled || !host || !google) return
        google.accounts.id.initialize({
          client_id: clientId,
          callback: (response) => {
            if (response.credential) latest.current(response.credential)
          },
        })
        host.replaceChildren()
        google.accounts.id.renderButton(host, {
          type: 'standard',
          theme: 'outline',
          size: 'large',
          shape: 'rectangular',
          text,
          logo_alignment: 'left',
          width: Math.min(400, host.clientWidth || 320),
          locale: i18n.language,
        })
      },
      () => {
        if (!cancelled) setFailed(true)
      },
    )
    return () => {
      cancelled = true
    }
  }, [clientId, text, i18n.language])

  if (failed) {
    return (
      <p role="alert" className="text-sm text-muted">
        {t('auth.googleScriptBlocked')}
      </p>
    )
  }

  // Reserve the button's height so the page does not jump when Google draws it.
  return <div ref={container} className="flex min-h-11 w-full justify-center" data-testid="google-button" />
}
