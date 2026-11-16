import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { ChatProvider } from '../features/chat/ChatContext'
import i18n from '../i18n'
import { useAuthStore } from '../stores/auth'

export function renderWithProviders(ui: ReactElement, { route = '/' } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <ChatProvider>{ui}</ChatProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

export function signIn() {
  useAuthStore.getState().setSession({
    accessToken: 'test-token',
    expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
    user: { id: '00000000-0000-0000-0000-000000000001', email: 'user@example.test', displayName: 'Synthetic User', roles: ['User'], hasPassword: true, linkedProviders: [] },
  })
}

export async function useEnglish() {
  await i18n.changeLanguage('en')
}

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response>

/** Replaces global fetch with a router of "METHOD path" -> handler; records every call for assertions. */
export function mockFetch(routes: Record<string, Handler>) {
  const calls: { url: string; init?: RequestInit }[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    calls.push({ url, init })
    const key = `${init?.method ?? 'GET'} ${url}`
    const handler = routes[key]
    if (!handler) throw new Error(`Unexpected request: ${key}`)
    return handler(url, init)
  })
  vi.stubGlobal('fetch', fetchMock)
  return { calls, fetchMock }
}

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
