import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, refreshSession } from '../../lib/api'
import type { AuthResponse } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'

export interface RegisterInput {
  email: string
  password: string
  displayName: string
  acceptTerms: boolean
  healthDataConsent: boolean
  aiProcessingConsent: boolean
}

/** Restores the session on page load from the httpOnly refresh cookie (the access token lives in memory only). */
export function useBootstrapSession() {
  useEffect(() => {
    let cancelled = false
    void (async () => {
      let ok = await refreshSession()
      // A second tab may have rotated the cookie at the same moment; one short retry resolves that race.
      if (!ok && !useAuthStore.getState().accessToken) {
        await new Promise((resolve) => setTimeout(resolve, 400))
        ok = await refreshSession()
      }
      if (!cancelled && !ok) useAuthStore.getState().clear()
    })()
    return () => {
      cancelled = true
    }
  }, [])
}

export function useLogin() {
  return useMutation({
    mutationFn: (input: { email: string; password: string }) =>
      api<AuthResponse>('/api/auth/login', { method: 'POST', body: input }),
    onSuccess: (response) => useAuthStore.getState().setSession(response),
  })
}

export function useRegister() {
  return useMutation({
    mutationFn: (input: RegisterInput) => api<AuthResponse>('/api/auth/register', { method: 'POST', body: input }),
    onSuccess: (response) => useAuthStore.getState().setSession(response),
  })
}

export function useLogout() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  return useMutation({
    mutationFn: () => api<void>('/api/auth/logout', { method: 'POST' }),
    onSettled: () => {
      useAuthStore.getState().clear()
      queryClient.clear()
      navigate('/login', { replace: true })
    },
  })
}
