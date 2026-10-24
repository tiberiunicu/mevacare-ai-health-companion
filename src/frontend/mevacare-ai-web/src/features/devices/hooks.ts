import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import type { Integration, SyncJob } from '../../lib/types'
import { healthKeys } from '../health/hooks'

export const integrationKeys = {
  all: ['integrations'] as const,
  list: ['integrations', 'list'] as const,
  jobs: (provider: string) => ['integrations', provider, 'jobs'] as const,
}

export function useIntegrations() {
  return useQuery({ queryKey: integrationKeys.list, queryFn: () => api<Integration[]>('/api/integrations') })
}

export function useSyncJobs(provider: string, enabled: boolean) {
  return useQuery({
    queryKey: integrationKeys.jobs(provider),
    queryFn: () => api<SyncJob[]>(`/api/integrations/${provider}/sync-jobs?limit=5`),
    enabled,
  })
}

/** Returns the provider's authorization page; the caller sends the browser there. */
export function useStartConnect() {
  return useMutation({
    mutationFn: (provider: string) => api<{ authorizationUrl: string }>(`/api/integrations/${provider}/connect`, { method: 'POST' }),
  })
}

export function useSync() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (provider: string) => api<SyncJob>(`/api/integrations/${provider}/sync`, { method: 'POST' }),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.all })
      void queryClient.invalidateQueries({ queryKey: healthKeys.all })
    },
  })
}

export function useDisconnect() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ provider, deleteData }: { provider: string; deleteData: boolean }) =>
      api<void>(`/api/integrations/${provider}?deleteData=${deleteData}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: integrationKeys.all })
      void queryClient.invalidateQueries({ queryKey: healthKeys.all })
    },
  })
}
