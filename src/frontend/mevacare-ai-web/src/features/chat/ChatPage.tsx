import { useQueryClient } from '@tanstack/react-query'
import clsx from 'clsx'
import { Bot, History, MessageSquarePlus, SendHorizontal, ShieldAlert, Square, Trash2, UserRound } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import ReactMarkdown from 'react-markdown'
import { Link } from 'react-router-dom'
import remarkGfm from 'remark-gfm'
import { Alert, Button, PageHeader, Spinner } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { useConsents } from '../profile/hooks'
import { chatKeys, fetchMessages, useConversations, useDeleteConversation } from './hooks'
import { streamChat } from './stream'

const MAX_MESSAGE_LENGTH = 4000
const SUGGESTIONS = ['hrv', 'protein', 'sleep', 'plateau'] as const

interface LocalMessage {
  key: string
  role: 'User' | 'Assistant'
  content: string
  /** pending = waiting for the first token, streaming = receiving, done/failed/interrupted = final. */
  state: 'pending' | 'streaming' | 'done' | 'failed' | 'interrupted'
  isSafety?: boolean
  errorCode?: string
}

export function ChatPage() {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const consents = useConsents()
  const conversations = useConversations()
  const deleteConversation = useDeleteConversation()

  const [conversationId, setConversationId] = useState<string | null>(null)
  const [messages, setMessages] = useState<LocalMessage[]>([])
  const [draft, setDraft] = useState('')
  const [streaming, setStreaming] = useState(false)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [notice, setNotice] = useState<'consent' | 'unavailable' | 'generic' | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const abortRef = useRef<AbortController | null>(null)
  const bottomRef = useRef<HTMLDivElement | null>(null)

  const aiAllowed = consents.data?.find((c) => c.type === 'AiProcessing')?.granted ?? false

  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ block: 'end' })
  }, [messages])

  useEffect(() => () => abortRef.current?.abort(), [])

  const openConversation = useCallback(
    async (id: string) => {
      abortRef.current?.abort()
      setHistoryOpen(false)
      setNotice(null)
      setConversationId(id)
      setLoadingHistory(true)
      try {
        const stored = await fetchMessages(id)
        setMessages(
          stored.map((m) => ({
            key: m.id,
            role: m.role,
            content: m.content,
            isSafety: m.isSafetyResponse,
            state: m.status === 'Complete' ? 'done' : m.status === 'Failed' ? 'failed' : 'interrupted',
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

  const startNewChat = () => {
    abortRef.current?.abort()
    setConversationId(null)
    setMessages([])
    setNotice(null)
    setHistoryOpen(false)
  }

  const patchLast = (patch: (m: LocalMessage) => LocalMessage) =>
    setMessages((current) => current.map((m, i) => (i === current.length - 1 ? patch(m) : m)))

  const send = async (text: string) => {
    const message = text.trim()
    if (!message || streaming) return
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
    let activeConversation = conversationId
    try {
      await streamChat({
        conversationId,
        message,
        clientMessageId: crypto.randomUUID(),
        signal: controller.signal,
        onEvent: (event) => {
          switch (event.type) {
            case 'conversation':
              activeConversation = event.conversationId
              setConversationId(event.conversationId)
              break
            case 'safety':
              patchLast((m) => ({ ...m, isSafety: true }))
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
      setStreaming(false)
      abortRef.current = null
      await queryClient.invalidateQueries({ queryKey: chatKeys.conversations })
      if (activeConversation) await queryClient.invalidateQueries({ queryKey: chatKeys.messages(activeConversation) })
    }
  }

  const onSubmit = (event: FormEvent) => {
    event.preventDefault()
    void send(draft)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      void send(draft)
    }
  }

  const remove = async (id: string) => {
    await deleteConversation.mutateAsync(id)
    if (id === conversationId) startNewChat()
  }

  return (
    <>
      <PageHeader title={t('chat.title')} subtitle={t('chat.subtitle')}>
        <div className="flex gap-2">
          <Button variant="secondary" className="lg:hidden" onClick={() => setHistoryOpen((open) => !open)} aria-expanded={historyOpen}>
            <History className="size-4" aria-hidden />
            {t('chat.history')}
          </Button>
          <Button variant="secondary" onClick={startNewChat}>
            <MessageSquarePlus className="size-4" aria-hidden />
            {t('chat.newChat')}
          </Button>
        </div>
      </PageHeader>

      <div className="flex min-h-[28rem] gap-4 lg:h-[calc(100dvh-11rem)]">
        <aside
          aria-label={t('chat.history')}
          className={clsx(
            'w-full shrink-0 overflow-y-auto rounded-2xl border border-line bg-surface p-2 lg:block lg:w-64',
            historyOpen ? 'block' : 'hidden',
          )}
        >
          {conversations.isPending ? (
            <Spinner label={t('common.loading')} />
          ) : conversations.data && conversations.data.length > 0 ? (
            <ul className="space-y-1">
              {conversations.data.map((c) => (
                <li key={c.id} className="group flex items-center gap-1">
                  <button
                    type="button"
                    onClick={() => void openConversation(c.id)}
                    aria-current={c.id === conversationId}
                    className={clsx(
                      'min-h-11 flex-1 truncate rounded-xl px-3 text-left text-sm',
                      c.id === conversationId ? 'bg-primary-soft font-medium text-primary-strong' : 'hover:bg-surface-2',
                    )}
                  >
                    {c.title}
                  </button>
                  <button
                    type="button"
                    onClick={() => void remove(c.id)}
                    aria-label={`${t('chat.deleteConversation')}: ${c.title}`}
                    className="inline-flex size-10 items-center justify-center rounded-xl text-muted hover:bg-danger-soft hover:text-danger"
                  >
                    <Trash2 className="size-4" aria-hidden />
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <p className="p-3 text-sm text-muted">{t('chat.noConversations')}</p>
          )}
        </aside>

        <section className={clsx('min-w-0 flex-1 flex-col rounded-2xl border border-line bg-surface', historyOpen ? 'hidden lg:flex' : 'flex')}>
          <div className="flex-1 space-y-4 overflow-y-auto p-4 sm:p-6" aria-live="polite" aria-busy={streaming}>
            {loadingHistory ? (
              <Spinner label={t('common.loading')} />
            ) : messages.length === 0 ? (
              <EmptyChat onPick={(text) => void send(text)} disabled={!aiAllowed || streaming} />
            ) : (
              messages.map((m) => <MessageBubble key={m.key} message={m} />)
            )}
            <div ref={bottomRef} />
          </div>

          <div className="space-y-3 border-t border-line p-3 sm:p-4">
            {consents.isSuccess && !aiAllowed && <ConsentNotice />}
            {notice === 'consent' && <ConsentNotice />}
            {notice === 'unavailable' && <Alert tone="danger">{t('chat.providerDown')}</Alert>}
            {notice === 'generic' && <Alert tone="danger">{t('common.genericError')}</Alert>}
            <form onSubmit={onSubmit} className="flex items-end gap-2">
              <label htmlFor="chat-input" className="sr-only">
                {t('chat.placeholder')}
              </label>
              <textarea
                id="chat-input"
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={onKeyDown}
                maxLength={MAX_MESSAGE_LENGTH}
                rows={1}
                placeholder={t('chat.placeholder')}
                disabled={!aiAllowed}
                className="max-h-40 min-h-11 flex-1 resize-none rounded-xl border border-line bg-canvas px-3 py-2.5 text-sm placeholder:text-muted/70 disabled:opacity-60"
              />
              {streaming ? (
                <Button type="button" variant="secondary" onClick={() => abortRef.current?.abort()} aria-label={t('chat.stop')}>
                  <Square className="size-4" aria-hidden />
                  <span className="hidden sm:inline">{t('chat.stop')}</span>
                </Button>
              ) : (
                <Button type="submit" disabled={!aiAllowed || draft.trim() === ''} aria-label={t('chat.send')}>
                  <SendHorizontal className="size-4" aria-hidden />
                  <span className="hidden sm:inline">{t('chat.send')}</span>
                </Button>
              )}
            </form>
            <p className="text-xs text-muted">{t('app.disclaimer')}</p>
          </div>
        </section>
      </div>
    </>
  )
}

function ConsentNotice() {
  const { t } = useTranslation()
  return (
    <Alert tone="warning" title={t('chat.consentTitle')}>
      <p>{t('chat.consentBody')}</p>
      <Link to="/settings/ai" className="mt-1 inline-block font-semibold text-primary hover:underline">
        {t('chat.consentAction')}
      </Link>
    </Alert>
  )
}

function EmptyChat({ onPick, disabled }: { onPick: (text: string) => void; disabled: boolean }) {
  const { t } = useTranslation()
  return (
    <div className="flex h-full flex-col items-center justify-center gap-5 py-8 text-center">
      <span className="inline-flex size-14 items-center justify-center rounded-2xl bg-ai-soft text-ai">
        <Bot className="size-7" aria-hidden />
      </span>
      <div>
        <h2 className="text-xl font-semibold">{t('chat.emptyTitle')}</h2>
        <p className="mx-auto mt-1 max-w-md text-sm text-muted">{t('chat.emptyBody')}</p>
      </div>
      <div className="flex max-w-xl flex-wrap justify-center gap-2">
        {SUGGESTIONS.map((key) => (
          <button
            key={key}
            type="button"
            disabled={disabled}
            onClick={() => onPick(t(`chat.suggestions.${key}`))}
            className="min-h-11 rounded-full border border-line bg-canvas px-4 text-sm hover:bg-surface-2 disabled:opacity-50"
          >
            {t(`chat.suggestions.${key}`)}
          </button>
        ))}
      </div>
    </div>
  )
}

function MessageBubble({ message }: { message: LocalMessage }) {
  const { t } = useTranslation()
  const isUser = message.role === 'User'
  return (
    <div className={clsx('flex gap-3', isUser && 'flex-row-reverse')}>
      <span
        className={clsx(
          'mt-1 inline-flex size-8 shrink-0 items-center justify-center rounded-full',
          isUser ? 'bg-primary-soft text-primary-strong' : 'bg-ai-soft text-ai',
        )}
        aria-hidden
      >
        {isUser ? <UserRound className="size-4" /> : <Bot className="size-4" />}
      </span>
      <div className={clsx('min-w-0 max-w-[85%] space-y-2', isUser && 'text-right')}>
        <p className="sr-only">{isUser ? t('chat.you') : t('chat.assistant')}</p>
        <div
          className={clsx(
            'inline-block rounded-2xl px-4 py-2.5 text-left text-sm leading-relaxed',
            isUser ? 'bg-primary text-primary-ink' : message.isSafety ? 'border border-warning bg-warning-soft' : 'bg-surface-2',
          )}
        >
          {message.isSafety && (
            <p className="mb-1 flex items-center gap-1.5 font-semibold text-warning">
              <ShieldAlert className="size-4" aria-hidden />
              {t('chat.emergency')}
            </p>
          )}
          {message.state === 'pending' ? (
            <span className="text-muted" role="status">
              {t('chat.thinking')}
            </span>
          ) : isUser ? (
            <p className="whitespace-pre-wrap break-words">{message.content}</p>
          ) : (
            <div className="prose-chat break-words">
              {/* react-markdown does not render raw HTML, so model output cannot inject markup. */}
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown>
            </div>
          )}
        </div>
        {message.state === 'failed' && (
          <p role="alert" className="text-xs text-danger">
            {message.errorCode === 'ai_unavailable' ? t('chat.providerDown') : t('chat.failed')}
          </p>
        )}
      </div>
    </div>
  )
}
