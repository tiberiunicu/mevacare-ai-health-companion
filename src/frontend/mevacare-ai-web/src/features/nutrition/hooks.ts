import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import type { DailySummary, FoodSearchItem, Meal, NutritionTargets, OnlineFoodCandidate, RangeSummary, SaveMealRequest } from '../../lib/types'

export const nutritionKeys = {
  daily: (date: string) => ['nutrition', 'daily', date] as const,
  range: (from: string, to: string) => ['nutrition', 'range', from, to] as const,
  targets: ['nutrition', 'targets'] as const,
  all: ['nutrition'] as const,
}

export function useDailySummary(date: string) {
  return useQuery({
    queryKey: nutritionKeys.daily(date),
    queryFn: () => api<DailySummary>(`/api/nutrition/summary/daily?date=${date}`),
    placeholderData: keepPreviousData,
  })
}

export function useRangeSummary(from: string, to: string) {
  return useQuery({
    queryKey: nutritionKeys.range(from, to),
    queryFn: () => api<RangeSummary>(`/api/nutrition/summary/range?from=${from}&to=${to}`),
  })
}

export function useTargets() {
  return useQuery({ queryKey: nutritionKeys.targets, queryFn: () => api<NutritionTargets>('/api/nutrition/targets') })
}

export function useSetTargets() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (targets: NutritionTargets) => api<NutritionTargets>('/api/nutrition/targets', { method: 'PUT', body: targets }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: nutritionKeys.all }),
  })
}

export function useSaveMeal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, request }: { id?: string; request: SaveMealRequest }) =>
      id
        ? api<Meal>(`/api/nutrition/meals/${id}`, { method: 'PUT', body: request })
        : api<Meal>('/api/nutrition/meals', { method: 'POST', body: request }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: nutritionKeys.all }),
  })
}

export function useDeleteMeal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<void>(`/api/nutrition/meals/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: nutritionKeys.all }),
  })
}

export function searchFoods(query: string, signal?: AbortSignal) {
  return api<FoodSearchItem[]>(`/api/nutrition/foods/search?q=${encodeURIComponent(query)}&limit=8`, { signal })
}

/** Preview of packaged products in Open Food Facts. Only when the user asks: the search text leaves the server. */
export function searchOnlineFoods(query: string, signal?: AbortSignal) {
  return api<OnlineFoodCandidate[]>(`/api/nutrition/foods/online/search?q=${encodeURIComponent(query)}`, { signal })
}

/** Stores one product (by barcode) in the local database and returns it like a search result. */
export function addOnlineFood(barcode: string) {
  return api<FoodSearchItem>('/api/nutrition/foods/online/add', { method: 'POST', body: { barcode } })
}
