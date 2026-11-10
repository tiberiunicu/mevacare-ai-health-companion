import { createContext, useContext } from 'react'
import type { ActionCard, Citation, ProposalCard as ProposalCardData } from '../../lib/types'

export interface LocalMessage {
  key: string
  role: 'User' | 'Assistant'
  content: string
  /** pending = waiting for the first token, streaming = receiving, done/failed/interrupted = final. */
  state: 'pending' | 'streaming' | 'done' | 'failed' | 'interrupted'
  isSafety?: boolean
  errorCode?: string
  /** Tools the assistant used while answering (status: running | done | failed). */
  tools?: { name: string; status: string }[]
  /** Meals the assistant prepared in this answer, waiting for the user's confirmation. */
  proposals?: ProposalCardData[]
  /** Other changes (weight, targets, profile, meal deletion) the assistant prepared in this answer. */
  actions?: ActionCard[]
  /** Approved sources used for this answer. */
  citations?: Citation[]
}

export type ChatNotice = 'consent' | 'unavailable' | 'generic' | null

export interface ChatSession {
  conversationId: string | null
  messages: LocalMessage[]
  draft: string
  setDraft: (value: string) => void
  streaming: boolean
  loadingHistory: boolean
  notice: ChatNotice
  /** Sends a message in the current conversation (starts one when there is none). */
  send: (text: string) => Promise<void>
  stop: () => void
  openConversation: (id: string) => Promise<void>
  startNewChat: () => void
  changeProposal: (messageKey: string, proposalId: string, patch: Partial<ProposalCardData>) => void
  changeAction: (messageKey: string, actionId: string, patch: Partial<ActionCard>) => void
  /** The floating chat panel that is available on every page except the full chat page. */
  dockOpen: boolean
  setDockOpen: (open: boolean) => void
  /** Opens the panel and sends the text: the way pages (the food journal) hand a sentence to the assistant. */
  ask: (text: string) => Promise<void>
}

export const ChatContext = createContext<ChatSession | null>(null)

export function useChat(): ChatSession {
  const context = useContext(ChatContext)
  if (!context) throw new Error('useChat must be used inside <ChatProvider>.')
  return context
}
