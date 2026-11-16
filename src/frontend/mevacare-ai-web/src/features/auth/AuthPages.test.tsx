import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { useAuthStore } from '../../stores/auth'
import { json, mockFetch, renderWithProviders, useEnglish } from '../../test/utils'
import { LoginPage, RegisterPage } from './AuthPages'

const authResponse = {
  accessToken: 'jwt',
  expiresAtUtc: new Date().toISOString(),
  user: { id: '1', email: 'jane@example.test', displayName: 'Jane', roles: ['User'], hasPassword: true, linkedProviders: [] },
}

beforeEach(async () => {
  await useEnglish()
  useAuthStore.getState().clear()
})

describe('LoginPage', () => {
  it('signs in and keeps the session in memory only', async () => {
    const { calls } = mockFetch({ 'GET /api/auth/providers': () => json({ google: null }), 'POST /api/auth/login': () => json(authResponse) })
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<p>home</p>} />
      </Routes>,
      { route: '/login' },
    )

    await userEvent.type(screen.getByLabelText('E-mail address'), 'jane@example.test')
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => expect(screen.getByText('home')).toBeInTheDocument())
    const login = calls.find((c) => c.init?.method === 'POST')!
    expect(JSON.parse(login.init?.body as string)).toEqual({ email: 'jane@example.test', password: 'correct horse battery' })
    expect(useAuthStore.getState().accessToken).toBe('jwt')
    expect(JSON.stringify({ ...localStorage })).not.toContain('jwt')
  })

  it('shows a generic message for invalid credentials', async () => {
    mockFetch({
      'GET /api/auth/providers': () => json({ google: null }),
      'POST /api/auth/login': () => json({ detail: 'whatever', code: 'unauthorized' }, 401),
    })
    renderWithProviders(<LoginPage />)

    await userEvent.type(screen.getByLabelText('E-mail address'), 'jane@example.test')
    await userEvent.type(screen.getByLabelText('Password'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid e-mail or password')
  })
})

describe('RegisterPage', () => {
  it('sends the optional consents as false unless the user ticks them', async () => {
    const { calls } = mockFetch({ 'GET /api/auth/providers': () => json({ google: null }), 'POST /api/auth/register': () => json(authResponse, 201) })
    renderWithProviders(<RegisterPage />)

    await userEvent.type(screen.getByLabelText('Your name'), 'Jane')
    await userEvent.type(screen.getByLabelText('E-mail address'), 'jane@example.test')
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
    await userEvent.click(screen.getByLabelText(/I accept the Terms/))
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'POST')).toBe(true))
    expect(JSON.parse(calls.find((c) => c.init?.method === 'POST')!.init?.body as string)).toMatchObject({
      acceptTerms: true,
      healthDataConsent: false,
      aiProcessingConsent: false,
    })
  })

  it('shows the field errors returned by the server', async () => {
    mockFetch({
      'GET /api/auth/providers': () => json({ google: null }),
      'POST /api/auth/register': () =>
        json({ title: 'Invalid', errors: { Password: ['The password must be at least 10 characters long.'] } }, 400),
    })
    renderWithProviders(<RegisterPage />)

    await userEvent.type(screen.getByLabelText('Password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }))

    expect(await screen.findByText('The password must be at least 10 characters long.')).toBeInTheDocument()
  })
})
