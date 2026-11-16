import { useAuthStore } from '../stores/auth'
import { json, mockFetch } from '../test/utils'
import { api, ApiError } from './api'

const session = (token: string) => ({
  accessToken: token,
  expiresAtUtc: new Date().toISOString(),
  user: { id: '1', email: 'a@example.test', displayName: 'A', roles: ['User'], hasPassword: true, linkedProviders: [] },
})

const authHeader = (call: { init?: RequestInit }) => (call.init?.headers as Record<string, string> | undefined)?.Authorization

describe('api client', () => {
  beforeEach(() => useAuthStore.getState().setSession(session('old-token')))

  it('sends the in-memory access token as a bearer header', async () => {
    const { calls } = mockFetch({ 'GET /api/ping': () => json({ ok: true }) })

    await api('/api/ping')

    expect(authHeader(calls[0])).toBe('Bearer old-token')
  })

  it('refreshes once on 401 and retries the original request with the new token', async () => {
    let attempt = 0
    const { calls } = mockFetch({
      'GET /api/data': () => (attempt++ === 0 ? json({}, 401) : json({ value: 42 })),
      'POST /api/auth/refresh': () => json(session('new-token')),
    })

    const result = await api<{ value: number }>('/api/data')

    expect(result.value).toBe(42)
    const retry = calls.filter((c) => c.url === '/api/data')[1]
    expect(authHeader(retry)).toBe('Bearer new-token')
  })

  it('signs the user out when the refresh fails', async () => {
    mockFetch({
      'GET /api/data': () => json({}, 401),
      'POST /api/auth/refresh': () => json({}, 401),
    })

    await expect(api('/api/data')).rejects.toBeInstanceOf(ApiError)

    expect(useAuthStore.getState().status).toBe('anonymous')
    expect(useAuthStore.getState().accessToken).toBeNull()
  })

  it('turns problem documents into ApiError with code and field errors', async () => {
    mockFetch({
      'PUT /api/x': () => json({ title: 'Bad', detail: 'Nope', code: 'some_code', errors: { Email: ['bad'] } }, 400),
    })

    const error = await api('/api/x', { method: 'PUT', body: {} }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).code).toBe('some_code')
    expect((error as ApiError).fieldErrors?.Email).toEqual(['bad'])
  })
})
