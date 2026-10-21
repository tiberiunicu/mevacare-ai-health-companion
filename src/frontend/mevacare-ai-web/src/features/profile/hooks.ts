import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { setLanguage } from '../../i18n'
import { api, ApiError } from '../../lib/api'
import type {
  Consent,
  ConsentType,
  CreateHealthGoal,
  HealthGoal,
  HealthPreferences,
  HealthProfile,
  UpdateHealthProfile,
} from '../../lib/types'
import { useThemeStore } from '../../stores/theme'

export const keys = {
  profile: ['health-profile'] as const,
  goals: ['health-goals'] as const,
  preferences: ['health-preferences'] as const,
  consents: ['consents'] as const,
}

export function isConsentRequired(error: unknown): boolean {
  return error instanceof ApiError && error.status === 403 && error.code === 'consent_required'
}

/** Do not retry on 4xx: those are answers, not outages. */
const retryServerErrorsOnly = (count: number, error: unknown) =>
  !(error instanceof ApiError && error.status < 500) && count < 2

export function useProfile() {
  return useQuery({ queryKey: keys.profile, queryFn: () => api<HealthProfile>('/api/health-profile'), retry: retryServerErrorsOnly })
}

export function useUpdateProfile() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: UpdateHealthProfile) => api<HealthProfile>('/api/health-profile', { method: 'PUT', body: input }),
    onSuccess: (profile) => queryClient.setQueryData(keys.profile, profile),
  })
}

export function useGoals() {
  return useQuery({ queryKey: keys.goals, queryFn: () => api<HealthGoal[]>('/api/health-goals'), retry: retryServerErrorsOnly })
}

export function useAddGoal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateHealthGoal) => api<HealthGoal>('/api/health-goals', { method: 'POST', body: input }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.goals }),
  })
}

export function useDeleteGoal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<void>(`/api/health-goals/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.goals }),
  })
}

export function usePreferences() {
  return useQuery({ queryKey: keys.preferences, queryFn: () => api<HealthPreferences>('/api/health-preferences') })
}

export function useUpdatePreferences() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: HealthPreferences) => api<HealthPreferences>('/api/health-preferences', { method: 'PUT', body: input }),
    onSuccess: (prefs) => queryClient.setQueryData(keys.preferences, prefs),
  })
}

export function useConsents() {
  return useQuery({ queryKey: keys.consents, queryFn: () => api<Consent[]>('/api/consents') })
}

export function useSetConsent() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ type, granted }: { type: ConsentType; granted: boolean }) =>
      api<Consent>(`/api/consents/${type}`, { method: 'PUT', body: { granted } }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: keys.consents })
      // Access to profile/goals depends on the consent, so their cached state is stale now.
      await queryClient.invalidateQueries({ queryKey: keys.profile })
      await queryClient.invalidateQueries({ queryKey: keys.goals })
    },
  })
}

/** Applies the language and theme stored on the server once the user is signed in. */
export function usePreferencesSync() {
  const { data } = usePreferences()
  const setTheme = useThemeStore((s) => s.setTheme)
  useEffect(() => {
    if (!data) return
    setLanguage(data.language)
    setTheme(data.theme)
  }, [data, setTheme])
}
