import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import type { ActivityRange, SleepRecoveryRange, WeightHistory, WeightPoint } from '../../lib/types'

export const healthKeys = {
  all: ['measurements'] as const,
  weight: (from: string, to: string) => ['measurements', 'weight', from, to] as const,
  sleep: (from: string, to: string) => ['measurements', 'sleep', from, to] as const,
  activity: (from: string, to: string) => ['measurements', 'activity', from, to] as const,
}

export function useWeightHistory(from: string, to: string) {
  return useQuery({
    queryKey: healthKeys.weight(from, to),
    queryFn: () => api<WeightHistory>(`/api/measurements/weight?from=${from}&to=${to}`),
    placeholderData: keepPreviousData,
  })
}

export function useSleepRecovery(from: string, to: string) {
  return useQuery({
    queryKey: healthKeys.sleep(from, to),
    queryFn: () => api<SleepRecoveryRange>(`/api/measurements/sleep-recovery?from=${from}&to=${to}`),
    placeholderData: keepPreviousData,
  })
}

export function useActivity(from: string, to: string) {
  return useQuery({
    queryKey: healthKeys.activity(from, to),
    queryFn: () => api<ActivityRange>(`/api/measurements/activity?from=${from}&to=${to}`),
    placeholderData: keepPreviousData,
  })
}

export function useAddWeight() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: { kilograms: number; measuredAt: string }) =>
      api<WeightPoint>('/api/measurements/weight', { method: 'POST', body: request }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: healthKeys.all }),
  })
}

export function useDeleteWeight() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<void>(`/api/measurements/weight/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: healthKeys.all }),
  })
}
