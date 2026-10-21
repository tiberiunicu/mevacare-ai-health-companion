import { BookOpenCheck, ExternalLink } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { safeHref } from '../../lib/links'
import type { Citation } from '../../lib/types'

/**
 * The approved sources behind an answer. Sources the answer cites with [n] are listed first; sources that were only
 * consulted are labelled as such, so the list never claims more support than the answer actually used.
 */
export function Citations({ items }: { items: Citation[] }) {
  const { t, i18n } = useTranslation()
  const cited = items.filter((c) => c.cited)
  const consulted = items.filter((c) => !c.cited)

  const renderItem = (c: Citation) => {
    const href = safeHref(c.url)
    return (
      <li key={c.sourceId} className="rounded-lg border border-line bg-canvas p-2.5">
        <p className="font-medium">
          <span className="mr-1.5 inline-flex min-w-5 justify-center rounded bg-ai-soft px-1 text-xs text-ai">{c.number}</span>
          {c.title}
        </p>
        <p className="text-xs text-muted">
          {c.publisher} · {t('chat.license')}: {c.license} · {t('chat.retrieved', { date: new Date(c.retrievedOn).toLocaleDateString(i18n.language) })}
        </p>
        <details className="mt-1 text-xs">
          <summary className="cursor-pointer text-primary">{t('chat.showExcerpt')}</summary>
          <blockquote className="mt-1 border-l-2 border-line pl-2 text-muted">{c.excerpt}</blockquote>
        </details>
        {href && (
          <a href={href} target="_blank" rel="noopener noreferrer" className="mt-1 inline-flex items-center gap-1 text-xs font-semibold text-primary hover:underline">
            {t('chat.openSource')}
            <ExternalLink className="size-3" aria-hidden />
          </a>
        )}
      </li>
    )
  }

  return (
    <section aria-label={t('chat.sources')} className="mt-2 space-y-2 text-sm">
      {cited.length > 0 && (
        <div>
          <h3 className="mb-1 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-muted">
            <BookOpenCheck className="size-3.5" aria-hidden />
            {t('chat.sources')}
          </h3>
          <ul className="space-y-1.5">{cited.map(renderItem)}</ul>
        </div>
      )}
      {consulted.length > 0 && (
        <div>
          <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-muted">{t('chat.alsoConsulted')}</h3>
          <ul className="space-y-1.5">{consulted.map(renderItem)}</ul>
        </div>
      )}
    </section>
  )
}
