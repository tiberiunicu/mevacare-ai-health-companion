import clsx from 'clsx'
import { History, MessageSquarePlus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button, PageHeader, Spinner } from '../../components/ui'
import { useChat } from './chatSession'
import { ChatComposer, ChatThread } from './ChatParts'
import { useConversations, useDeleteConversation } from './hooks'

/** The full-size chat with the list of past conversations. The conversation itself lives in <ChatProvider>. */
export function ChatPage() {
  const { t } = useTranslation()
  const chat = useChat()
  const conversations = useConversations()
  const deleteConversation = useDeleteConversation()
  const [historyOpen, setHistoryOpen] = useState(false)

  const remove = async (id: string) => {
    await deleteConversation.mutateAsync(id)
    if (id === chat.conversationId) chat.startNewChat()
  }

  return (
    <>
      <PageHeader title={t('chat.title')} subtitle={t('chat.subtitle')}>
        <div className="flex gap-2">
          <Button variant="secondary" className="lg:hidden" onClick={() => setHistoryOpen((open) => !open)} aria-expanded={historyOpen}>
            <History className="size-4" aria-hidden />
            {t('chat.history')}
          </Button>
          <Button
            variant="secondary"
            onClick={() => {
              chat.startNewChat()
              setHistoryOpen(false)
            }}
          >
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
                    onClick={() => {
                      setHistoryOpen(false)
                      void chat.openConversation(c.id)
                    }}
                    aria-current={c.id === chat.conversationId}
                    className={clsx(
                      'min-h-11 flex-1 truncate rounded-xl px-3 text-left text-sm',
                      c.id === chat.conversationId ? 'bg-primary-soft font-medium text-primary-strong' : 'hover:bg-surface-2',
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
          <ChatThread className="p-4 sm:p-6" />
          <ChatComposer />
        </section>
      </div>
    </>
  )
}
