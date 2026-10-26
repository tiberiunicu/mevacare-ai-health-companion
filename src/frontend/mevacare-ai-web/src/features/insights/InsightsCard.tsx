import { Sparkles } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { Card } from '../../components/ui'
import { isConsentRequired } from '../profile/hooks'
import { useInsights } from './hooks'
import { describeInsight } from './insightText'

/** The two most important observations of the last 30 days, in words; the full picture is one click away. */
export function InsightsCard() {
  const { t, i18n } = useTranslation()
  const insights = useInsights(30)
  const shown = (insights.data?.insights ?? []).filter((i) => i.category !== 'data_quality').slice(0, 2)

  return (
    <Card className="bg-ai-soft">
      <h2 className="mb-3 flex items-center gap-2 font-semibold">
        <span className="text-ai">
          <Sparkles className="size-5" aria-hidden />
        </span>
        {t('dashboard.insights')}
      </h2>
      {shown.length > 0 ? (
        <ul className="space-y-2 text-sm">
          {shown.map((insight) => (
            <li key={insight.code + insight.facts.map((f) => f.text ?? '').join('')}>{describeInsight(insight, t, i18n.language).text}</li>
          ))}
        </ul>
      ) : (
        <p className="text-sm text-muted">{isConsentRequired(insights.error) ? t('dashboard.consentNeeded') : t('dashboard.insightsEmpty')}</p>
      )}
      <Link to="/health/insights" className="mt-3 inline-block text-sm font-semibold text-primary hover:underline">
        {t('dashboard.insightsAction')}
      </Link>
    </Card>
  )
}
