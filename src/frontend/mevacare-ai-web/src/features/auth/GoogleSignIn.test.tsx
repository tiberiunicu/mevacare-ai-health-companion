import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { useAuthStore } from '../../stores/auth'
import { json, mockFetch, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { PrivacyPage } from '../settings/SettingsPages'
import { LoginPage, RegisterPage } from './AuthPages'

const CLIENT_ID = 'test-client.apps.googleusercontent.com'

const user = { id: '1', email: 'jane@example.test', displayName: 'Jane', roles: ['User'], hasPassword: false, linkedProviders: ['Google'] }
const authResponse = { accessToken: 'jwt', expiresAtUtc: new Date().toISOString(), user }

/** Stands in for Google's script: records how the button was configured and lets a test "click" it. */
function installFakeGoogle() {
  const state: { clientId?: string; callback?: (r: { credential?: string }) => void; options?: Record<string, unknown>; renders: number } = { renders: 0 }
  window.google = {
    accounts: {
      id: {
        initialize: (config) => {
          state.clientId = config.client_id
          state.callback = config.callback
        },
        renderButton: (parent, options) => {
          state.renders++
          state.options = options
          const button = document.createElement('button')
          button.textContent = 'Google button'
          parent.appendChild(button)
        },
      },
    },
  }
  return { state, click: (credential: string) => state.callback?.({ credential }) }
}

const withGoogle = { 'GET /api/auth/providers': () => json({ google: { clientId: CLIENT_ID } }) }
const withoutGoogle = { 'GET /api/auth/providers': () => json({ google: null }) }

beforeEach(async () => {
  await useEnglish()
  useAuthStore.getState().clear()
})
afterEach(() => {
  delete window.google
})

describe('Google button on the login page', () => {
  it('is not shown when the server has Google sign-in switched off', async () => {
    installFakeGoogle()
    mockFetch(withoutGoogle)
    renderWithProviders(<LoginPage />)

    await userEvent.type(screen.getByLabelText('E-mail address'), 'a@b.c')
    expect(screen.queryByTestId('google-button')).not.toBeInTheDocument()
  })

  it('configures Google with the public client id and signs in with the token it returns', async () => {
    const google = installFakeGoogle()
    const { calls } = mockFetch({ ...withGoogle, 'POST /api/auth/google': () => json(authResponse) })
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>home</p>} />
      </Routes>,
      { route: '/login' },
    )

    expect(await screen.findByRole('button', { name: 'Google button' })).toBeInTheDocument()
    expect(google.state.clientId).toBe(CLIENT_ID)
    expect(google.state.options).toMatchObject({ text: 'signin_with', theme: 'outline' })

    google.click('google-id-token')

    await waitFor(() => expect(screen.getByText('home')).toBeInTheDocument())
    const post = calls.find((c) => c.init?.method === 'POST')!
    expect(JSON.parse(post.init?.body as string)).toEqual({
      idToken: 'google-id-token',
      acceptTerms: false,
      healthDataConsent: false,
      aiProcessingConsent: false,
    })
    expect(useAuthStore.getState().accessToken).toBe('jwt')
    expect(JSON.stringify({ ...localStorage })).not.toContain('google-id-token')
  })

  it('asks a new Google identity to accept the terms, then creates the account with that same token', async () => {
    const google = installFakeGoogle()
    let attempts = 0
    const { calls } = mockFetch({
      ...withGoogle,
      'POST /api/auth/google': () => (++attempts === 1 ? json({ code: 'registration_required', detail: 'x' }, 409) : json(authResponse, 201)),
    })
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>home</p>} />
      </Routes>,
      { route: '/login' },
    )
    await screen.findByRole('button', { name: 'Google button' })
    google.click('new-person-token')

    const create = await screen.findByRole('button', { name: 'Create account with Google' })
    expect(create).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Google button' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByLabelText(/I accept the Terms/))
    await userEvent.click(screen.getByLabelText(/process my health data/))
    await userEvent.click(create)

    await waitFor(() => expect(screen.getByText('home')).toBeInTheDocument())
    const posts = calls.filter((c) => c.init?.method === 'POST').map((c) => JSON.parse(c.init?.body as string))
    expect(posts[1]).toEqual({ idToken: 'new-person-token', acceptTerms: true, healthDataConsent: true, aiProcessingConsent: false })
  })

  it('lets the person back out of creating an account', async () => {
    const google = installFakeGoogle()
    mockFetch({ ...withGoogle, 'POST /api/auth/google': () => json({ code: 'registration_required', detail: 'x' }, 409) })
    renderWithProviders(<LoginPage />)
    await screen.findByRole('button', { name: 'Google button' })
    google.click('t')
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

    expect(await screen.findByRole('button', { name: 'Google button' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create account with Google' })).not.toBeInTheDocument()
  })

  it.each([
    ['google_account_not_linked', 409, 'Sign in with your password, then connect Google'],
    ['google_email_not_verified', 400, 'has not verified the e-mail address'],
    ['unauthorized', 401, 'could not be verified'],
    ['google_unavailable', 503, 'not available right now'],
  ])('explains %s in words', async (code, status, text) => {
    const google = installFakeGoogle()
    mockFetch({ ...withGoogle, 'POST /api/auth/google': () => json({ code, detail: 'x' }, status) })
    renderWithProviders(<LoginPage />)
    await screen.findByRole('button', { name: 'Google button' })

    google.click('t')

    expect(await screen.findByRole('alert')).toHaveTextContent(text)
    expect(useAuthStore.getState().status).not.toBe('authenticated')
  })

  it('says so when the Google script cannot be loaded', async () => {
    // No fake installed: the script tag is added to the page but never loads; simulate the failure event.
    mockFetch(withGoogle)
    renderWithProviders(<LoginPage />)
    await waitFor(() => expect(document.querySelector('script[src*="accounts.google.com/gsi/client"]')).not.toBeNull())

    document.querySelector('script[src*="accounts.google.com/gsi/client"]')!.dispatchEvent(new Event('error'))

    expect(await screen.findByText(/could not be loaded/)).toBeInTheDocument()
  })
})

describe('Google button on the registration page', () => {
  it('sends the consents already ticked on the form', async () => {
    const google = installFakeGoogle()
    const { calls } = mockFetch({ ...withGoogle, 'POST /api/auth/google': () => json(authResponse, 201) })
    renderWithProviders(<RegisterPage />)
    await screen.findByRole('button', { name: 'Google button' })
    expect(google.state.options).toMatchObject({ text: 'signup_with' })

    await userEvent.click(screen.getByLabelText(/I accept the Terms/))
    google.click('token-from-register')

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'POST')).toBe(true))
    expect(JSON.parse(calls.find((c) => c.init?.method === 'POST')!.init?.body as string)).toMatchObject({
      idToken: 'token-from-register',
      acceptTerms: true,
    })
  })
})

describe('Settings: sign-in methods', () => {
  const consents = [{ type: 'TermsOfService', granted: true, required: true, policyVersion: '2026-10', grantedAtUtc: new Date().toISOString(), revokedAtUtc: null }]

  function signInAs(partial: Partial<typeof user>) {
    signIn()
    useAuthStore.getState().setUser({ ...useAuthStore.getState().user!, ...partial })
  }

  it('connects Google to a password account and shows the new state', async () => {
    const google = installFakeGoogle()
    let linked = false
    const { calls } = mockFetch({
      ...withGoogle,
      'GET /api/consents': () => json(consents),
      'POST /api/auth/google/link': () => {
        linked = true
        return new Response(null, { status: 204 })
      },
      'GET /api/auth/me': () => json({ ...user, hasPassword: true, linkedProviders: linked ? ['Google'] : [] }),
    })
    signInAs({ hasPassword: true, linkedProviders: [] })
    renderWithProviders(<PrivacyPage />)

    await screen.findByRole('button', { name: 'Google button' })
    expect(screen.getByText(/Not connected/)).toBeInTheDocument()
    google.click('link-token')

    expect(await screen.findByText('Connected')).toBeInTheDocument()
    const link = calls.find((c) => c.url === '/api/auth/google/link')!
    expect(JSON.parse(link.init?.body as string)).toEqual({ idToken: 'link-token' })
    expect(useAuthStore.getState().user?.linkedProviders).toEqual(['Google'])
  })

  it('offers Disconnect only while the account still has a password', async () => {
    installFakeGoogle()
    mockFetch({ ...withGoogle, 'GET /api/consents': () => json(consents) })
    signInAs({ hasPassword: false, linkedProviders: ['Google'] })
    const { unmount } = renderWithProviders(<PrivacyPage />)

    await screen.findByText('Connected')
    expect(screen.queryByRole('button', { name: 'Disconnect' })).not.toBeInTheDocument()
    expect(screen.getAllByText(/only way to sign in/).length).toBeGreaterThan(0)
    unmount()

    signInAs({ hasPassword: true, linkedProviders: ['Google'] })
    renderWithProviders(<PrivacyPage />)
    expect(await screen.findByRole('button', { name: 'Disconnect' })).toBeInTheDocument()
  })

  it('disconnects Google and refreshes the account', async () => {
    installFakeGoogle()
    const { calls } = mockFetch({
      ...withGoogle,
      'GET /api/consents': () => json(consents),
      'DELETE /api/auth/google': () => new Response(null, { status: 204 }),
      'GET /api/auth/me': () => json({ ...user, hasPassword: true, linkedProviders: [] }),
    })
    signInAs({ hasPassword: true, linkedProviders: ['Google'] })
    renderWithProviders(<PrivacyPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Disconnect' }))

    await waitFor(() => expect(useAuthStore.getState().user?.linkedProviders).toEqual([]))
    expect(calls.some((c) => c.init?.method === 'DELETE')).toBe(true)
  })

  it('reports a Google account that already belongs to someone else', async () => {
    const google = installFakeGoogle()
    mockFetch({
      ...withGoogle,
      'GET /api/consents': () => json(consents),
      'POST /api/auth/google/link': () => json({ code: 'google_in_use', detail: 'x' }, 409),
    })
    signInAs({ hasPassword: true, linkedProviders: [] })
    renderWithProviders(<PrivacyPage />)
    await screen.findByRole('button', { name: 'Google button' })

    google.click('taken')

    expect(await screen.findByText(/already connected to another MevaCare account/)).toBeInTheDocument()
  })

  it('is hidden when Google sign-in is off and was never connected', async () => {
    mockFetch({ ...withoutGoogle, 'GET /api/consents': () => json(consents) })
    signInAs({ hasPassword: true, linkedProviders: [] })
    renderWithProviders(<PrivacyPage />)

    await screen.findByRole('button', { name: 'Delete account…' })
    expect(screen.queryByText('Sign-in methods')).not.toBeInTheDocument()
  })

  it('deletes a password-less account after confirming with Google instead of a password', async () => {
    const google = installFakeGoogle()
    const { calls } = mockFetch({
      ...withGoogle,
      'GET /api/consents': () => json(consents),
      'POST /api/account/delete': () => new Response(null, { status: 204 }),
    })
    signInAs({ hasPassword: false, linkedProviders: ['Google'] })
    renderWithProviders(<PrivacyPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Delete account…' }))
    expect(screen.queryByLabelText('Confirm with your password')).not.toBeInTheDocument()
    expect(screen.getByText(/has no password/)).toBeInTheDocument()
    const buttons = await screen.findAllByRole('button', { name: 'Google button' })
    expect(buttons.length).toBeGreaterThan(0)
    google.click('confirm-token')

    await waitFor(() => expect(useAuthStore.getState().status).toBe('anonymous'))
    const del = calls.find((c) => c.url === '/api/account/delete')!
    expect(JSON.parse(del.init?.body as string)).toEqual({ googleIdToken: 'confirm-token' })
  })
})
