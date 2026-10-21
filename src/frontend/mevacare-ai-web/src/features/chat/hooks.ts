import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import type { Citation, KnowledgeSource, ProposalCard } from '../../lib/types'

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
  citations: Citation[]
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

export function fetchProposals(conversationId: string) {
  return api<ProposalCard[]>(`/api/conversations/${conversationId}/proposals`)
}

export function dismissProposal(proposalId: string) {
  return api<void>(`/api/assistant/proposals/${proposalId}/dismiss`, { method: 'POST' })
}

export function useKnowledgeSources() {
  return useQuery({ queryKey: ['knowledge', 'sources'] as const, queryFn: () => api<KnowledgeSource[]>('/api/knowledge/sources') })
}
