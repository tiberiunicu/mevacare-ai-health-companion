import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuthStore } from '../../stores/auth'
import { json, mockFetch, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ProfilePage } from '../profile/ProfilePage'
import { PrivacyPage } from './SettingsPages'

const consent = (type: string, granted: boolean, required = false) => ({
  type,
  granted,
  required,
  policyVersion: '2026-10',
  grantedAtUtc: granted ? new Date().toISOString() : null,
  revokedAtUtc: null,
})

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

describe('ProfilePage', () => {
  it('explains how to proceed when the health-data consent is missing', async () => {
    mockFetch({
      'GET /api/health-profile': () => json({ code: 'consent_required', detail: 'x' }, 403),
      'GET /api/health-goals': () => json({ code: 'consent_required', detail: 'x' }, 403),
    })

    renderWithProviders(<ProfilePage />)

    expect(await screen.findByText('Health data processing is switched off')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open Privacy & Consent' })).toHaveAttribute('href', '/settings/privacy')
    expect(screen.queryByLabelText('Height (cm)')).not.toBeInTheDocument()
  })

  it('saves the profile with the entered values', async () => {
    const saved = { exists: true, dateOfBirth: null, sex: 'Female', heightCm: 170, activityLevel: 'Unspecified', timeZoneId: 'UTC' }
    const { calls } = mockFetch({
      'GET /api/health-profile': () => json({ ...saved, exists: false, heightCm: null }),
      'GET /api/health-goals': () => json([]),
      'PUT /api/health-profile': () => json(saved),
    })

    renderWithProviders(<ProfilePage />)
    await userEvent.type(await screen.findByLabelText('Height (cm)'), '170')
    await userEvent.selectOptions(screen.getByLabelText('Sex (for energy calculations)'), 'Female')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.getByText('Saved')).toBeInTheDocument())
    const put = calls.find((c) => c.init?.method === 'PUT')!
    expect(JSON.parse(put.init?.body as string)).toMatchObject({ heightCm: 170, sex: 'Female', dateOfBirth: null })
  })
})

describe('PrivacyPage', () => {
  it('lets the user grant an optional consent and marks the terms as required', async () => {
    let healthGranted = false
    const { calls } = mockFetch({
      'GET /api/consents': () =>
        json([
          consent('TermsOfService', true, true),
          consent('HealthDataProcessing', healthGranted),
          consent('AiProcessing', false),
          consent('ExternalIntegrations', false),
        ]),
      'PUT /api/consents/HealthDataProcessing': () => {
        healthGranted = true
        return json(consent('HealthDataProcessing', true))
      },
      'GET /api/health-profile': () => json({}),
      'GET /api/health-goals': () => json([]),
    })

    renderWithProviders(<PrivacyPage />)
    const toggle = await screen.findByRole('switch', { name: 'Health data processing' })
    expect(toggle).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByText('Required')).toBeInTheDocument()

    await userEvent.click(toggle)

    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Health data processing' })).toHaveAttribute('aria-checked', 'true'),
    )
    const put = calls.find((c) => c.init?.method === 'PUT')!
    expect(JSON.parse(put.init?.body as string)).toEqual({ granted: true })
  })

  it('requires the password to delete the account and reports a wrong one', async () => {
    mockFetch({
      'GET /api/consents': () => json([consent('TermsOfService', true, true)]),
      'POST /api/account/delete': () => json({ code: 'invalid_password', detail: 'x' }, 400),
    })

    renderWithProviders(<PrivacyPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete account…' }))
    const confirm = screen.getByRole('button', { name: 'Permanently delete' })
    expect(confirm).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Confirm with your password'), 'wrong-password')
    await userEvent.click(confirm)

    expect(await screen.findByText('The password is incorrect.')).toBeInTheDocument()
    expect(useAuthStore.getState().status).toBe('authenticated')
  })
})
