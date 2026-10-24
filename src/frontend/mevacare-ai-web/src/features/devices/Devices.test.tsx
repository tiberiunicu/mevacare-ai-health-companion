import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { Integration, SyncJob } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { DevicesPage } from './DevicesPage'

type Handler = (init?: RequestInit) => Response

function routeFetch(routes: Record<string, Handler>) {
  const calls: { method: string; url: string }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = init?.method ?? 'GET'
      calls.push({ method, url })
      const handler = routes[`${method} ${url}`]
      if (!handler) throw new Error(`Unexpected request: ${method} ${url}`)
      return handler(init)
    }),
  )
  return calls
}

const whoop = (overrides: Partial<Integration> = {}): Integration => ({
  provider: 'whoop',
  displayName: 'WHOOP',
  configured: true,
  connected: false,
  status: null,
  connectedAtUtc: null,
  lastSyncAtUtc: null,
  lastSyncStatus: null,
  scopes: [],
  ...overrides,
})

const connected = (overrides: Partial<Integration> = {}) =>
  whoop({ connected: true, status: 'Connected', connectedAtUtc: '2026-10-24T08:00:00Z', lastSyncAtUtc: '2026-10-24T09:00:00Z', lastSyncStatus: 'succeeded', ...overrides })

const consents = (granted: boolean) => () => json([{ type: 'ExternalIntegrations', granted, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null }])

const job = (overrides: Partial<SyncJob> = {}): SyncJob => ({
  id: 'j1', trigger: 'Manual', status: 'Succeeded', startedAtUtc: '2026-10-24T09:00:00Z', finishedAtUtc: '2026-10-24T09:00:05Z',
  recordsFetched: 5, recordsAdded: 3, recordsUpdated: 2, errorCode: null, ...overrides,
})

const baseRoutes = (integration: Integration, consentGranted = true): Record<string, Handler> => ({
  'GET /api/integrations': () => json([integration]),
  'GET /api/consents': consents(consentGranted),
  'GET /api/integrations/whoop/sync-jobs?limit=5': () => json([job()]),
})

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => {
  useAuthStore.getState().clear()
  vi.unstubAllGlobals()
})

describe('DevicesPage', () => {
  it('says plainly that the integration is not set up on the server and offers no connect button', async () => {
    routeFetch(baseRoutes(whoop({ configured: false })))

    renderWithProviders(<DevicesPage />)

    expect(await screen.findByText('Not set up on this server')).toBeInTheDocument()
    expect(screen.getByText(/has not been configured by the administrator/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Connect WHOOP/ })).not.toBeInTheDocument()
  })

  it('needs the external integrations consent before connecting', async () => {
    routeFetch(baseRoutes(whoop(), false))

    renderWithProviders(<DevicesPage />)

    expect(await screen.findByRole('button', { name: 'Connect WHOOP' })).toBeDisabled()
    expect(screen.getByText('Connecting a device needs the External integrations consent.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open privacy settings' })).toHaveAttribute('href', '/settings/privacy')
  })

  it('sends the browser to the providers authorization page when connecting', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    routeFetch({
      ...baseRoutes(whoop()),
      'POST /api/integrations/whoop/connect': () => json({ authorizationUrl: 'https://api.prod.whoop.com/oauth/oauth2/auth?state=abc' }),
    })
    renderWithProviders(<DevicesPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Connect WHOOP' }))

    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://api.prod.whoop.com/oauth/oauth2/auth?state=abc'))
  })

  it('shows a connected account with its last synchronization and imports on request', async () => {
    const calls = routeFetch({
      ...baseRoutes(connected()),
      'POST /api/integrations/whoop/sync': () => json(job()),
    })
    renderWithProviders(<DevicesPage />)

    expect(await screen.findByText('Connected')).toBeInTheDocument()
    expect(screen.getByText('Sleep, recovery, day strain and workouts')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Synchronize now' }))

    expect(await screen.findByText('Up to date: 3 new and 2 updated records.')).toBeInTheDocument()
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(1)
  })

  it('explains a failed run with a human message, not a code', async () => {
    routeFetch({
      ...baseRoutes(connected()),
      'POST /api/integrations/whoop/sync': () => json(job({ status: 'Failed', errorCode: 'rate_limited' })),
    })
    renderWithProviders(<DevicesPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Synchronize now' }))

    expect(await screen.findByText('The synchronization did not finish')).toBeInTheDocument()
    expect(screen.getByText('The provider asked us to slow down. It will be retried automatically.')).toBeInTheDocument()
  })

  it('tells the user to wait when synchronizations are requested too quickly', async () => {
    routeFetch({
      ...baseRoutes(connected()),
      'POST /api/integrations/whoop/sync': () => json({ title: 'Too many', status: 429, code: 'rate_limited' }, 429),
    })
    renderWithProviders(<DevicesPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Synchronize now' }))

    expect(await screen.findByText(/synchronized a moment ago/)).toBeInTheDocument()
  })

  it('offers to reconnect when the provider refused the stored access and does not offer to synchronize', async () => {
    routeFetch(baseRoutes(connected({ status: 'NeedsReauthorization' })))

    renderWithProviders(<DevicesPage />)

    expect(await screen.findByText('Needs to be reconnected')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reconnect' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Synchronize now' })).toBeDisabled()
  })

  it('disconnects only after a confirmation and can also delete the imported data', async () => {
    const calls = routeFetch({
      ...baseRoutes(connected()),
      'DELETE /api/integrations/whoop?deleteData=true': () => new Response(null, { status: 204 }),
    })
    renderWithProviders(<DevicesPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Disconnect' }))
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false) // nothing happened yet
    await userEvent.click(screen.getByLabelText('Also delete the data imported from WHOOP'))
    await userEvent.click(screen.getAllByRole('button', { name: 'Disconnect' })[1])

    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.endsWith('deleteData=true'))).toBe(true))
  })

  it('keeps the imported data by default when disconnecting', async () => {
    const calls = routeFetch({
      ...baseRoutes(connected()),
      'DELETE /api/integrations/whoop?deleteData=false': () => new Response(null, { status: 204 }),
    })
    renderWithProviders(<DevicesPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Disconnect' }))
    await userEvent.click(screen.getAllByRole('button', { name: 'Disconnect' })[1])

    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.endsWith('deleteData=false'))).toBe(true))
  })

  it('imports the first data automatically when the browser returns from the provider', async () => {
    const calls = routeFetch({
      ...baseRoutes(connected()),
      'POST /api/integrations/whoop/sync': () => json(job({ recordsAdded: 14, recordsUpdated: 0 })),
    })

    renderWithProviders(<DevicesPage />, { route: '/devices?connected=whoop' })

    expect(await screen.findByText('Up to date: 14 new and 0 updated records.')).toBeInTheDocument()
    expect(calls.filter((c) => c.method === 'POST' && c.url.endsWith('/sync'))).toHaveLength(1)
  })

  it('explains why a connection attempt failed and does not synchronize', async () => {
    const calls = routeFetch(baseRoutes(whoop()))

    renderWithProviders(<DevicesPage />, { route: '/devices?error=account_already_linked' })

    expect(await screen.findByText('This provider account is already connected to another user.')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('uses a generic message for an unknown error code', async () => {
    routeFetch(baseRoutes(whoop()))

    renderWithProviders(<DevicesPage />, { route: '/devices?error=something_new' })

    expect(await screen.findByText('The connection failed. Try again.')).toBeInTheDocument()
  })

  it('never shows tokens or keys, only what is imported and the privacy note', async () => {
    routeFetch(baseRoutes(connected({ scopes: ['offline', 'read:sleep'] })))

    renderWithProviders(<DevicesPage />)

    expect(await screen.findByText(/stored encrypted on the server and are never shown/)).toBeInTheDocument()
    expect(document.body.textContent).not.toMatch(/token/i)
  })
})
