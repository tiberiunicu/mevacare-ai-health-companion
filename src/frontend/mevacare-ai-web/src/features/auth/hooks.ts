import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, refreshSession } from '../../lib/api'
import type { AuthProviders, AuthResponse, User } from '../../lib/types'
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

/** Which outside sign-in providers the server has switched on (and the public Google client id). A failure just hides the buttons. */
export function useAuthProviders() {
  return useQuery({
    queryKey: ['auth', 'providers'],
    queryFn: () => api<AuthProviders>('/api/auth/providers'),
    staleTime: Infinity,
    retry: false,
  })
}

export interface GoogleSignInInput {
  idToken: string
  /** Needed only when the Google identity has no account yet (the same consents as the registration form). */
  acceptTerms?: boolean
  healthDataConsent?: boolean
  aiProcessingConsent?: boolean
}

export function useGoogleSignIn() {
  return useMutation({
    mutationFn: (input: GoogleSignInInput) => api<AuthResponse>('/api/auth/google', { method: 'POST', body: input }),
    onSuccess: (response) => useAuthStore.getState().setSession(response),
  })
}

async function reloadUser() {
  useAuthStore.getState().setUser(await api<User>('/api/auth/me'))
}

/** Connects the Google identity in the ID token to the signed-in account. */
export function useLinkGoogle() {
  return useMutation({
    mutationFn: (idToken: string) => api<void>('/api/auth/google/link', { method: 'POST', body: { idToken } }),
    onSuccess: reloadUser,
  })
}

export function useUnlinkGoogle() {
  return useMutation({
    mutationFn: () => api<void>('/api/auth/google', { method: 'DELETE' }),
    onSuccess: reloadUser,
  })
}

/** Asks for a password-reset e-mail. The server always answers the same, whether or not the address has an account. */
export function useForgotPassword(language: string) {
  return useMutation({
    mutationFn: (email: string) => api<void>('/api/auth/forgot-password', { method: 'POST', body: { email, language } }),
  })
}

export function useResetPassword(language: string) {
  return useMutation({
    mutationFn: (input: { token: string; newPassword: string }) =>
      api<void>('/api/auth/reset-password', { method: 'POST', body: { ...input, language } }),
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
