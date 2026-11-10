import { Bot, MessageSquarePlus, X } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation } from 'react-router-dom'
import { useChat } from './chatSession'
import { ChatComposer, ChatThread } from './ChatParts'

/**
 * A chat that is available on every page: a button in the corner opens a side panel with the same conversation as the
 * full chat page. Say what you ate, log a weight or ask a question without leaving the page you are on.
 */
export function ChatDock() {
  const { t } = useTranslation()
  const chat = useChat()
  const { pathname } = useLocation()
  const panelRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    if (!chat.dockOpen) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') chat.setDockOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [chat])

  // The full chat page shows the same conversation; a second copy beside it would only be confusing.
  if (pathname.startsWith('/companion')) return null

  if (!chat.dockOpen) {
    return (
      <button
        type="button"
        onClick={() => chat.setDockOpen(true)}
        className="fixed bottom-20 right-4 z-40 inline-flex min-h-12 items-center gap-2 rounded-full bg-primary px-5 text-sm font-semibold text-primary-ink shadow-lg hover:opacity-90 lg:bottom-6 lg:right-6"
      >
        <Bot className="size-5" aria-hidden />
        {t('dock.open')}
      </button>
    )
  }

  return (
    <aside
      ref={panelRef}
      role="dialog"
      aria-label={t('dock.title')}
      className="fixed inset-y-0 right-0 z-50 flex w-full flex-col border-l border-line bg-surface shadow-2xl sm:w-[26rem]"
    >
      <header className="flex items-center justify-between gap-2 border-b border-line px-4 py-3">
        <h2 className="flex items-center gap-2 text-sm font-semibold">
          <Bot className="size-5 text-ai" aria-hidden />
          {t('dock.title')}
        </h2>
        <div className="flex items-center gap-1">
          <button
            type="button"
            onClick={chat.startNewChat}
            aria-label={t('chat.newChat')}
            className="inline-flex size-10 items-center justify-center rounded-xl text-muted hover:bg-surface-2 hover:text-ink"
          >
            <MessageSquarePlus className="size-5" aria-hidden />
          </button>
          <button
            type="button"
            onClick={() => chat.setDockOpen(false)}
            aria-label={t('dock.close')}
            className="inline-flex size-10 items-center justify-center rounded-xl text-muted hover:bg-surface-2 hover:text-ink"
          >
            <X className="size-5" aria-hidden />
          </button>
        </div>
      </header>
      <ChatThread className="p-4" />
      <ChatComposer />
    </aside>
  )
}
