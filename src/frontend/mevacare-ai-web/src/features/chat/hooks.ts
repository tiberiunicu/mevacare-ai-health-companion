import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'

export interface ConversationSummary {
  id: string
  title: string
  lastMessageAtUtc: string
}

export interface StoredMessage {
  id: string
  sequence: number
  role: 'User' | 'Assistant'
  content: string
  status: 'Complete' | 'Failed' | 'Interrupted'
  isSafetyResponse: boolean
  createdAtUtc: string
}

export const chatKeys = {
  conversations: ['conversations'] as const,
  messages: (id: string) => ['conversations', id, 'messages'] as const,
}

export function useConversations() {
  return useQuery({ queryKey: chatKeys.conversations, queryFn: () => api<ConversationSummary[]>('/api/conversations') })
}

export function fetchMessages(id: string) {
  return api<StoredMessage[]>(`/api/conversations/${id}/messages`)
}

export function useDeleteConversation() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<void>(`/api/conversations/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: chatKeys.conversations }),
  })
}
