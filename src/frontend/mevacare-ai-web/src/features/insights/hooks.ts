import { useMutation, useQuery } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { localDateString } from '../../lib/dates'
import type { HealthInsights, InsightExplanation } from '../../lib/types'

export function useInsights(days: number) {
  const to = localDateString()
  return useQuery({
    queryKey: ['insights', days, to] as const,
    queryFn: () => api<HealthInsights>(`/api/insights?days=${days}&to=${to}`),
  })
}

export function useExplainInsights() {
  return useMutation({
    mutationFn: (request: { days: number; language: 'en' | 'ro' }) =>
      api<InsightExplanation>('/api/assistant/insights/explain', { method: 'POST', body: { ...request, to: localDateString() } }),
  })
}
