import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuthStore } from '../../stores/auth'
import { json, mockFetch, renderWithProviders, useEnglish } from '../../test/utils'
import { LoginPage } from './AuthPages'
import { ForgotPasswordPage, ResetPasswordPage } from './PasswordResetPages'

const providers = (passwordReset: boolean) => ({ 'GET /api/auth/providers': () => json({ google: null, passwordReset }) })

beforeEach(async () => {
  await useEnglish()
  useAuthStore.getState().clear()
})
afterEach(() => vi.restoreAllMocks())

describe('Forgot-password link on the login page', () => {
  it('is shown when the server can send e-mail', async () => {
    mockFetch(providers(true))
    renderWithProviders(<LoginPage />)

    expect(await screen.findByRole('link', { name: 'Forgot your password?' })).toHaveAttribute('href', '/forgot-password')
  })

  it('is hidden when it cannot', async () => {
    mockFetch(providers(false))
    renderWithProviders(<LoginPage />)

    await screen.findByLabelText('Password')
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Forgot your password?' })).not.toBeInTheDocument())
  })
})

describe('ForgotPasswordPage', () => {
  it('asks for the e-mail with the current language and gives the same answer whatever the address', async () => {
    const { calls } = mockFetch({ ...providers(true), 'POST /api/auth/forgot-password': () => new Response(null, { status: 202 }) })
    renderWithProviders(<ForgotPasswordPage />)

    await userEvent.type(screen.getByLabelText('E-mail address'), 'jane@example.test')
    await userEvent.click(screen.getByRole('button', { name: 'Send the link' }))

    expect(await screen.findByText(/If an account exists for that address/)).toBeInTheDocument()
    const post = calls.find((c) => c.init?.method === 'POST')!
    expect(JSON.parse(post.init?.body as string)).toEqual({ email: 'jane@example.test', language: 'en' })
    expect(screen.getByRole('link', { name: 'Back to sign in' })).toBeInTheDocument()
  })

  it('does not send an empty address', async () => {
    mockFetch(providers(true))
    renderWithProviders(<ForgotPasswordPage />)

    expect(await screen.findByRole('button', { name: 'Send the link' })).toBeDisabled()
  })

  it('says so when the server cannot send e-mail', async () => {
    mockFetch(providers(false))
    renderWithProviders(<ForgotPasswordPage />)

    expect(await screen.findByText(/not available on this server/)).toBeInTheDocument()
    expect(screen.queryByLabelText('E-mail address')).not.toBeInTheDocument()
  })

  it('shows the server message when too many requests were made', async () => {
    mockFetch({ ...providers(true), 'POST /api/auth/forgot-password': () => json({ code: 'rate_limited', detail: 'Slow down for a minute.' }, 429) })
    renderWithProviders(<ForgotPasswordPage />)

    await userEvent.type(await screen.findByLabelText('E-mail address'), 'jane@example.test')
    await userEvent.click(screen.getByRole('button', { name: 'Send the link' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Slow down for a minute.')
  })
})

describe('ResetPasswordPage', () => {
  const open = () => renderWithProviders(<ResetPasswordPage />, { route: '/reset-password#token=abc123' })

  it('sends the token from the link fragment with the new password, then removes it from the address bar', async () => {
    const replace = vi.spyOn(window.history, 'replaceState')
    const { calls } = mockFetch({ ...providers(true), 'POST /api/auth/reset-password': () => new Response(null, { status: 204 }) })
    open()

    expect(replace).toHaveBeenCalledWith(null, '', '/reset-password')
    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-passphrase')
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'a-brand-new-passphrase')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText(/Your password was changed/)).toBeInTheDocument()
    const post = calls.find((c) => c.init?.method === 'POST')!
    expect(JSON.parse(post.init?.body as string)).toEqual({ token: 'abc123', newPassword: 'a-brand-new-passphrase', language: 'en' })
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login')
    expect(useAuthStore.getState().status).not.toBe('authenticated')
  })

  it('does not allow two different passwords', async () => {
    mockFetch(providers(true))
    open()

    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-passphrase')
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'something-else-entirely')

    expect(screen.getByText('The two passwords are not the same.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Change password' })).toBeDisabled()
  })

  it('offers a new link when the server says this one is invalid or expired', async () => {
    mockFetch({ ...providers(true), 'POST /api/auth/reset-password': () => json({ code: 'invalid_reset_token', detail: 'x' }, 400) })
    open()

    await userEvent.type(screen.getByLabelText('New password'), 'a-brand-new-passphrase')
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'a-brand-new-passphrase')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText(/invalid or has expired/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ask for a new link' })).toHaveAttribute('href', '/forgot-password')
  })

  it('shows the password rules the server reports and keeps the form', async () => {
    mockFetch({
      ...providers(true),
      'POST /api/auth/reset-password': () =>
        json({ title: 'Invalid', errors: { NewPassword: ['The password must be at least 10 characters long.'] } }, 400),
    })
    open()

    await userEvent.type(screen.getByLabelText('New password'), 'short')
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText('The password must be at least 10 characters long.')).toBeInTheDocument()
    expect(screen.getByLabelText('New password')).toBeInTheDocument()
  })

  it('treats a page opened without a token as an invalid link', async () => {
    mockFetch(providers(true))
    renderWithProviders(<ResetPasswordPage />, { route: '/reset-password' })

    expect(await screen.findByText(/invalid or has expired/)).toBeInTheDocument()
    expect(screen.queryByLabelText('New password')).not.toBeInTheDocument()
  })
})
