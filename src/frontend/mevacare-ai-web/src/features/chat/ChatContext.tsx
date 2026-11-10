import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError } from '../../lib/api'
import { toOffsetIso } from '../../lib/dates'
import type { ProposalCard as ProposalCardData } from '../../lib/types'
import { chatKeys, fetchMessages, fetchProposals } from './hooks'
import { ChatContext } from './chatSession'
import type { ChatNotice, ChatSession, LocalMessage } from './chatSession'
import { streamChat } from './stream'

/**
 * The chat state lives above the router so the conversation survives navigation: the user can say what they ate on any page
 * and keep talking in the same conversation on the full chat page.
 */
export function ChatProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [conversationId, setConversationId] = useState<string | null>(null)
  const [messages, setMessages] = useState<LocalMessage[]>([])
  const [draft, setDraft] = useState('')
  const [streaming, setStreaming] = useState(false)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [notice, setNotice] = useState<ChatNotice>(null)
  const [dockOpen, setDockOpen] = useState(false)
  const abortRef = useRef<AbortController | null>(null)
  const conversationRef = useRef<string | null>(null)
  const streamingRef = useRef(false)

  useEffect(() => () => abortRef.current?.abort(), [])

  const setConversation = (id: string | null) => {
    conversationRef.current = id
    setConversationId(id)
  }

  const openConversation = useCallback(
    async (id: string) => {
      abortRef.current?.abort()
      setNotice(null)
      setConversation(id)
      setLoadingHistory(true)
      try {
        const [stored, proposals] = await Promise.all([fetchMessages(id), fetchProposals(id).catch(() => [] as ProposalCardData[])])
        setMessages(
          stored.map((m) => ({
            key: m.id,
            role: m.role,
            content: m.content,
            isSafety: m.isSafetyResponse,
            state: m.status === 'Complete' ? 'done' : m.status === 'Failed' ? 'failed' : 'interrupted',
            proposals: m.role === 'Assistant' ? proposals.filter((p) => p.assistantMessageSequence === m.sequence) : undefined,
            citations: m.citations,
          })),
        )
      } catch {
        setNotice('generic')
      } finally {
        setLoadingHistory(false)
      }
    },
    [],
  )

  const startNewChat = useCallback(() => {
    abortRef.current?.abort()
    setConversation(null)
    setMessages([])
    setNotice(null)
  }, [])

  const patchLast = (patch: (m: LocalMessage) => LocalMessage) =>
    setMessages((current) => current.map((m, i) => (i === current.length - 1 ? patch(m) : m)))

  const send = useCallback(
    async (text: string) => {
      const message = text.trim()
      if (!message || streamingRef.current) return
      streamingRef.current = true
      setNotice(null)
      setDraft('')
      setStreaming(true)
      setMessages((current) => [
        ...current,
        { key: crypto.randomUUID(), role: 'User', content: message, state: 'done' },
        { key: crypto.randomUUID(), role: 'Assistant', content: '', state: 'pending' },
      ])

      const controller = new AbortController()
      abortRef.current = controller
      let activeConversation = conversationRef.current
      try {
        await streamChat({
          conversationId: conversationRef.current,
          message,
          clientMessageId: crypto.randomUUID(),
          clientTime: toOffsetIso(new Date()),
          signal: controller.signal,
          onEvent: (event) => {
            switch (event.type) {
              case 'conversation':
                activeConversation = event.conversationId
                setConversation(event.conversationId)
                break
              case 'safety':
                patchLast((m) => ({ ...m, isSafety: true }))
                break
              case 'tool':
                patchLast((m) => ({
                  ...m,
                  tools: event.status === 'running'
                    ? [...(m.tools ?? []), { name: event.name, status: 'running' }]
                    : (m.tools ?? []).map((tool) => (tool.name === event.name && tool.status === 'running' ? { ...tool, status: event.status } : tool)),
                }))
                break
              case 'citations':
                patchLast((m) => ({ ...m, citations: event.items }))
                break
              case 'proposal':
                patchLast((m) => ({ ...m, proposals: [...(m.proposals ?? []), event.card] }))
                break
              case 'delta':
                patchLast((m) => ({ ...m, content: m.content + event.text, state: 'streaming' }))
                break
              case 'done':
                patchLast((m) => ({ ...m, state: 'done' }))
                break
              case 'error':
                patchLast((m) => ({ ...m, state: 'failed', errorCode: event.code }))
                break
            }
          },
        })
      } catch (error) {
        if (controller.signal.aborted) {
          patchLast((m) => ({ ...m, state: m.content ? 'interrupted' : 'failed' }))
        } else {
          // Nothing reached the model: drop the optimistic pair and restore the draft.
          setMessages((current) => current.slice(0, -2))
          setDraft(message)
          if (error instanceof ApiError && error.code === 'consent_required') setNotice('consent')
          else if (error instanceof ApiError && error.status === 503) setNotice('unavailable')
          else setNotice('generic')
        }
      } finally {
        streamingRef.current = false
        setStreaming(false)
        abortRef.current = null
        await queryClient.invalidateQueries({ queryKey: chatKeys.conversations })
        if (activeConversation) await queryClient.invalidateQueries({ queryKey: chatKeys.messages(activeConversation) })
      }
    },
    [queryClient],
  )

  const ask = useCallback(
    async (text: string) => {
      setDockOpen(true)
      await send(text)
    },
    [send],
  )

  const stop = useCallback(() => abortRef.current?.abort(), [])

  const changeProposal = useCallback(
    (messageKey: string, proposalId: string, patch: Partial<ProposalCardData>) =>
      setMessages((current) =>
        current.map((m) =>
          m.key === messageKey ? { ...m, proposals: m.proposals?.map((p) => (p.id === proposalId ? { ...p, ...patch } : p)) } : m,
        ),
      ),
    [],
  )

  const value = useMemo<ChatSession>(
    () => ({
      conversationId, messages, draft, setDraft, streaming, loadingHistory, notice, send, stop, openConversation, startNewChat,
      changeProposal, dockOpen, setDockOpen, ask,
    }),
    [conversationId, messages, draft, streaming, loadingHistory, notice, send, stop, openConversation, startNewChat, changeProposal, dockOpen, ask],
  )

  return <ChatContext.Provider value={value}>{children}</ChatContext.Provider>
}
