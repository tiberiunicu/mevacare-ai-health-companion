import clsx from 'clsx'
import { Bot, Check, Loader2, SendHorizontal, ShieldAlert, Square, UserRound } from 'lucide-react'
import { useEffect, useRef } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import ReactMarkdown from 'react-markdown'
import { Link } from 'react-router-dom'
import remarkGfm from 'remark-gfm'
import { Alert, Button, Spinner } from '../../components/ui'
import type { ActionCard as ActionCardData, ProposalCard as ProposalCardData } from '../../lib/types'
import { ActionCard } from './ActionCard'
import { Citations } from './Citations'
import { useChat } from './chatSession'
import type { LocalMessage } from './chatSession'
import { useAiAllowed } from './hooks'
import { ProposalCard } from './ProposalCard'

export const MAX_MESSAGE_LENGTH = 4000
const SUGGESTIONS = ['logMeal', 'hrv', 'protein', 'sleep'] as const

export function ConsentNotice() {
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

export function MessageBubble({
  message, onProposalChange, onActionChange,
}: {
  message: LocalMessage
  onProposalChange: (proposalId: string, patch: Partial<ProposalCardData>) => void
  onActionChange: (actionId: string, patch: Partial<ActionCardData>) => void
}) {
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
        {message.tools && message.tools.length > 0 && (
          <ul className="flex flex-wrap gap-1.5" aria-label={t('chat.tools')}>
            {message.tools.map((tool, index) => (
              <li
                key={`${tool.name}-${index}`}
                className={clsx(
                  'inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs',
                  tool.status === 'failed' ? 'bg-danger-soft text-danger' : 'bg-ai-soft text-ai',
                )}
              >
                {tool.status === 'running' && <Loader2 className="size-3 animate-spin" aria-hidden />}
                {tool.status === 'done' && <Check className="size-3" aria-hidden />}
                {t(`tools.${tool.name}`, { defaultValue: tool.name })}
                {tool.status === 'running' ? '…' : ''}
              </li>
            ))}
          </ul>
        )}
        {message.citations && message.citations.length > 0 && <Citations items={message.citations} />}
        {message.proposals?.map((card) => (
          <ProposalCard key={card.id} card={card} onChange={(patch) => onProposalChange(card.id, patch)} />
        ))}
        {message.actions?.map((card) => (
          <ActionCard key={card.id} card={card} onChange={(patch) => onActionChange(card.id, patch)} />
        ))}
        {message.state === 'failed' && (
          <p role="alert" className="text-xs text-danger">
            {message.errorCode === 'ai_unavailable' ? t('chat.providerDown') : t('chat.failed')}
          </p>
        )}
      </div>
    </div>
  )
}

/** The scrolling list of messages of the current conversation (or the suggestions when it is empty). */
export function ChatThread({ className }: { className?: string }) {
  const { t } = useTranslation()
  const chat = useChat()
  const { allowed } = useAiAllowed()
  const bottomRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView?.({ block: 'end' })
  }, [chat.messages])

  return (
    <div className={clsx('flex-1 space-y-4 overflow-y-auto', className)} aria-live="polite" aria-busy={chat.streaming}>
      {chat.loadingHistory ? (
        <Spinner label={t('common.loading')} />
      ) : chat.messages.length === 0 ? (
        <EmptyChat onPick={(text) => void chat.send(text)} disabled={!allowed || chat.streaming} />
      ) : (
        chat.messages.map((m) => (
          <MessageBubble
            key={m.key}
            message={m}
            onProposalChange={(proposalId, patch) => chat.changeProposal(m.key, proposalId, patch)}
            onActionChange={(actionId, patch) => chat.changeAction(m.key, actionId, patch)}
          />
        ))
      )}
      <div ref={bottomRef} />
    </div>
  )
}

/** The message box with its consent and error notices. */
export function ChatComposer() {
  const { t } = useTranslation()
  const chat = useChat()
  const { loaded, allowed } = useAiAllowed()

  const onSubmit = (event: FormEvent) => {
    event.preventDefault()
    void chat.send(chat.draft)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      void chat.send(chat.draft)
    }
  }

  return (
    <div className="space-y-3 border-t border-line p-3 sm:p-4">
      {loaded && !allowed && <ConsentNotice />}
      {chat.notice === 'consent' && <ConsentNotice />}
      {chat.notice === 'unavailable' && <Alert tone="danger">{t('chat.providerDown')}</Alert>}
      {chat.notice === 'generic' && <Alert tone="danger">{t('common.genericError')}</Alert>}
      <form onSubmit={onSubmit} className="flex items-end gap-2">
        <label htmlFor="chat-input" className="sr-only">
          {t('chat.placeholder')}
        </label>
        <textarea
          id="chat-input"
          value={chat.draft}
          onChange={(e) => chat.setDraft(e.target.value)}
          onKeyDown={onKeyDown}
          maxLength={MAX_MESSAGE_LENGTH}
          rows={1}
          placeholder={t('chat.placeholder')}
          disabled={!allowed}
          className="max-h-40 min-h-11 flex-1 resize-none rounded-xl border border-line bg-canvas px-3 py-2.5 text-sm placeholder:text-muted/70 disabled:opacity-60"
        />
        {chat.streaming ? (
          <Button type="button" variant="secondary" onClick={chat.stop} aria-label={t('chat.stop')}>
            <Square className="size-4" aria-hidden />
            <span className="hidden sm:inline">{t('chat.stop')}</span>
          </Button>
        ) : (
          <Button type="submit" disabled={!allowed || chat.draft.trim() === ''} aria-label={t('chat.send')}>
            <SendHorizontal className="size-4" aria-hidden />
            <span className="hidden sm:inline">{t('chat.send')}</span>
          </Button>
        )}
      </form>
      <p className="text-xs text-muted">{t('app.disclaimer')}</p>
    </div>
  )
}
