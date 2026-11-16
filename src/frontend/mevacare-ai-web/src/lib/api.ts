import { useAuthStore } from '../stores/auth'
import type { AuthResponse } from './types'

/** An RFC 9457 problem document returned by the API, as a throwable. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly fieldErrors: Record<string, string[]> | undefined

  constructor(status: number, message: string, code?: string, fieldErrors?: Record<string, string[]>) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as { detail?: string; title?: string; code?: string; errors?: Record<string, string[]> }
    return new ApiError(response.status, body.detail ?? body.title ?? response.statusText, body.code, body.errors)
  } catch {
    return new ApiError(response.status, response.statusText || 'Request failed')
  }
}

let refreshInFlight: Promise<boolean> | null = null

/**
 * Exchanges the httpOnly refresh cookie for a new access token. Calls are single-flight: concurrent callers
 * share one request, because the server rotates the refresh token on every use.
 */
export function refreshSession(): Promise<boolean> {
  refreshInFlight ??= (async () => {
    try {
      const response = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin' })
      if (!response.ok) return false
      useAuthStore.getState().setSession((await response.json()) as AuthResponse)
      return true
    } catch {
      return false
    } finally {
      refreshInFlight = null
    }
  })()
  return refreshInFlight
}

interface RequestOptions {
  method?: string
  body?: unknown
  signal?: AbortSignal
  /** Internal: set after one automatic refresh-and-retry. */
  retried?: boolean
}

export async function apiFetchRaw(path: string, options: RequestOptions = {}): Promise<Response> {
  const { accessToken } = useAuthStore.getState()
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`

  const response = await fetch(path, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    credentials: 'same-origin',
    signal: options.signal,
  })

  if (response.status === 401 && !options.retried && accessToken) {
    if (await refreshSession()) return apiFetchRaw(path, { ...options, retried: true })
    useAuthStore.getState().clear()
  }
  return response
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await apiFetchRaw(path, options)
  if (!response.ok) throw await toApiError(response)
  if (response.status === 204) return undefined as T
  // Some answers (202 Accepted) carry no body at all.
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export { toApiError }
