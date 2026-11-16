import { ApiError } from '../../lib/api'

export interface ConsentChoices {
  acceptTerms: boolean
  healthDataConsent: boolean
  aiProcessingConsent: boolean
}

export const NONE: ConsentChoices = { acceptTerms: false, healthDataConsent: false, aiProcessingConsent: false }

/** What the person sees when Google sign-in failed, in words. The server's codes are stable; its English texts are not shown. */
export function googleErrorMessage(error: unknown, t: (key: string) => string): string {
  if (error instanceof ApiError) {
    if (error.code === 'google_account_not_linked') return t('auth.googleNotLinked')
    if (error.code === 'google_email_not_verified') return t('auth.googleEmailNotVerified')
    if (error.code === 'email_in_use') return t('auth.emailInUse')
    if (error.status === 401) return t('auth.googleFailed')
    if (error.status === 429) return error.message
    if (error.status >= 500) return t('auth.googleUnavailable')
    return error.message
  }
  return t('common.networkError')
}
