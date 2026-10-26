import clsx from 'clsx'
import { ArrowDownRight, ArrowRight, ArrowUpRight, Sparkles } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { SubNav } from '../../components/AppShell'
import { Alert, Button, Card, Field, PageHeader, SelectInput, Spinner } from '../../components/ui'
import { ApiError } from '../../lib/api'
import type { Insight, PeriodComparison, Trend, TrendDirection } from '../../lib/types'
import { providerName } from '../../lib/units'
import { HEALTH_NAV } from '../health/navigation'
import { isConsentRequired } from '../profile/hooks'
import { useExplainInsights, useInsights } from './hooks'
import { describeInsight, formatNumber } from './insightText'

const DECIMALS: Record<string, number> = { weight_kg: 2, calories_kcal: 0, protein_g: 1, sleep_minutes: 0, recovery_score: 1, hrv_ms: 1, resting_heart_rate_bpm: 1 }

function DirectionIcon({ direction }: { direction: TrendDirection }) {
  if (direction === 'increasing') return <ArrowUpRight className="size-4" aria-hidden />
  if (direction === 'decreasing') return <ArrowDownRight className="size-4" aria-hidden />
  return <ArrowRight className="size-4" aria-hidden />
}

function metricLabel(t: (key: string, options?: Record<string, unknown>) => string, metric: string, provider: string | null) {
  const label = t(`insights.metrics.${metric}`, { defaultValue: metric })
  return provider ? `${label} · ${providerName(provider)}` : label
}

function InsightCard({ insight }: { insight: Insight }) {
  const { t, i18n } = useTranslation()
  const { text, details } = describeInsight(insight, t, i18n.language)
  return (
    <li className={clsx('rounded-xl border p-4', insight.severity === 'notable' ? 'border-primary/50 bg-primary-soft' : 'border-line bg-surface')}>
      <div className="mb-1 flex flex-wrap items-center gap-2 text-xs">
        <span className={clsx('rounded-full px-2 py-0.5 font-semibold', insight.severity === 'notable' ? 'bg-primary text-primary-ink' : 'bg-surface-2 text-muted')}>
          {t(`insights.severity.${insight.severity}`)}
        </span>
        <span className="text-muted">{t(`insights.category.${insight.category}`)}</span>
      </div>
      <p className="text-sm">{text}</p>
      {details.map((detail) => (
        <p key={detail} className="mt-1 text-sm text-muted">
          {detail}
        </p>
      ))}
      {insight.caveats.length > 0 && (
        <ul className="mt-2 space-y-0.5 text-xs text-muted">
          {insight.caveats.map((caveat) => (
            <li key={caveat}>• {t(`insights.caveats.${caveat}`, { defaultValue: caveat })}</li>
          ))}
        </ul>
      )}
    </li>
  )
}

function TrendRow({ trend }: { trend: Trend }) {
  const { t, i18n } = useTranslation()
  const decimals = DECIMALS[trend.metric] ?? 1
  return (
    <li className="rounded-xl border border-line p-3 text-sm">
      <p className="font-medium">{metricLabel(t, trend.metric, trend.provider)}</p>
      {trend.direction === 'insufficient_data' ? (
        <p className="text-muted">{t(`insights.notes.${trend.note ?? 'no_data'}`, { defaultValue: t('insights.notes.no_data') })}</p>
      ) : (
        <p className="flex flex-wrap items-center gap-1.5 text-muted">
          <DirectionIcon direction={trend.direction} />
          <span>
            {t(`insights.direction.${trend.direction}`)}
            {trend.direction !== 'stable' && trend.changePerWeek !== null && ` ${formatNumber(Math.abs(trend.changePerWeek), i18n.language, decimals)} ${trend.unit}/${t('insights.week')}`}
          </span>
          <span>
            · {t('insights.trendBasis', { points: trend.points, span: trend.spanDays })} · {t(`insights.confidence.${trend.confidence}`)} {t('insights.confidenceWord')}
          </span>
        </p>
      )}
    </li>
  )
}

function ComparisonTable({ comparisons }: { comparisons: PeriodComparison[] }) {
  const { t, i18n } = useTranslation()
  const show = (value: number | null, metric: string) => (value === null ? '–' : formatNumber(value, i18n.language, DECIMALS[metric] ?? 1))
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-left text-sm">
        <thead className="text-xs text-muted">
          <tr>
            <th scope="col" className="py-2 pr-3 font-medium">
              {t('insights.compare.metric')}
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              {t('insights.compare.before')}
            </th>
            <th scope="col" className="px-3 py-2 font-medium">
              {t('insights.compare.now')}
            </th>
            <th scope="col" className="py-2 pl-3 font-medium">
              {t('insights.compare.change')}
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-line">
          {comparisons.map((c) => (
            <tr key={`${c.metric}-${c.provider}`}>
              <th scope="row" className="py-2 pr-3 font-medium">
                {metricLabel(t, c.metric, c.provider)}
                <span className="block text-xs font-normal text-muted">
                  {t('insights.compare.days', { before: c.previousDays, now: c.currentDays })}
                </span>
              </th>
              <td className="px-3 py-2">
                {show(c.previousAverage, c.metric)} {c.unit}
              </td>
              <td className="px-3 py-2">
                {show(c.currentAverage, c.metric)} {c.unit}
              </td>
              <td className="py-2 pl-3">
                {c.direction === 'insufficient_data' ? (
                  <span className="text-muted">{t('insights.compare.needMore')}</span>
                ) : (
                  <span className={clsx('inline-flex items-center gap-1', c.meaningful ? 'font-semibold' : 'text-muted')}>
                    <DirectionIcon direction={c.direction} />
                    {c.change !== null && `${c.change > 0 ? '+' : ''}${formatNumber(c.change, i18n.language, DECIMALS[c.metric] ?? 1)} ${c.unit}`}
                    {!c.meaningful && ` (${t('insights.compare.small')})`}
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Explanation({ days }: { days: number }) {
  const { t, i18n } = useTranslation()
  const explain = useExplainInsights()
  const language = i18n.language.startsWith('ro') ? 'ro' : 'en'
  const error = explain.error instanceof ApiError ? explain.error : null
  const errorKey = error?.code && ['ai_not_configured', 'ai_unavailable', 'ai_timeout', 'ai_empty_response', 'consent_required', 'nothing_to_explain'].includes(error.code) ? error.code : null

  return (
    <Card className="bg-ai-soft">
      <h2 className="mb-1 flex items-center gap-2 font-semibold">
        <Sparkles className="size-5 text-ai" aria-hidden />
        {t('insights.explain.title')}
      </h2>
      <p className="mb-3 text-sm text-muted">{t('insights.explain.body')}</p>
      <Button variant="secondary" loading={explain.isPending} onClick={() => explain.mutate({ days, language })}>
        {explain.isPending ? t('insights.explain.working') : t('insights.explain.action')}
      </Button>
      {explain.isError && (
        <div className="mt-3">
          <Alert tone="warning">
            {errorKey ? t(`insights.explain.errors.${errorKey}`) : t('common.genericError')}
            {errorKey === 'consent_required' && (
              <Link to="/settings/privacy" className="ml-1 font-semibold text-primary hover:underline">
                {t('insights.explain.consentAction')}
              </Link>
            )}
          </Alert>
        </div>
      )}
      {explain.data && (
        <div className="mt-4 space-y-3">
          <div className="whitespace-pre-line rounded-xl bg-surface p-4 text-sm">{explain.data.explanation}</div>
          {explain.data.ungroundedNumbers.length > 0 && (
            <Alert tone="warning" title={t('insights.explain.ungroundedTitle')}>
              {t('insights.explain.ungroundedBody', { numbers: explain.data.ungroundedNumbers.join(', ') })}
            </Alert>
          )}
          <p className="text-xs text-muted">{t('insights.explain.disclaimer')}</p>
        </div>
      )}
    </Card>
  )
}

export function InsightsPage() {
  const { t } = useTranslation()
  const [days, setDays] = useState(30)
  const insights = useInsights(days)
  const data = insights.data

  return (
    <>
      <PageHeader title={t('insights.title')} subtitle={t('insights.subtitle')}>
        <div className="w-48">
          <Field label={t('health.range')}>
            {(p) => (
              <SelectInput {...p} value={days} onChange={(e) => setDays(Number(e.target.value))}>
                {[14, 30, 60, 90].map((option) => (
                  <option key={option} value={option}>
                    {t('health.lastDays', { count: option })}
                  </option>
                ))}
              </SelectInput>
            )}
          </Field>
        </div>
      </PageHeader>
      <SubNav items={HEALTH_NAV} />
      {isConsentRequired(insights.error) ? (
        <Alert tone="warning" title={t('health.consentTitle')}>
          <p>{t('health.consentBody')}</p>
          <Link to="/settings/privacy" className="mt-1 inline-block font-semibold text-primary hover:underline">
            {t('health.consentAction')}
          </Link>
        </Alert>
      ) : insights.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : insights.isError || !data ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <div className="space-y-6">
          <Card>
            <h2 className="mb-1 font-semibold">{t('insights.coverage.title')}</h2>
            <p className="text-sm text-muted">
              {t('insights.coverage.line', {
                from: data.from,
                to: data.to,
                weight: data.coverage.weightEntries,
                logged: data.coverage.nutritionLoggedDays,
                window: data.coverage.nutritionWindowDays,
              })}
              {data.coverage.providers.map((p) => ` ${t('insights.coverage.provider', { provider: providerName(p.provider), sleep: p.daysWithSleep, recovery: p.daysWithRecovery })}`)}
            </p>
          </Card>

          <section aria-labelledby="insights-list">
            <h2 id="insights-list" className="mb-2 font-semibold">
              {t('insights.sections.insights')}
            </h2>
            {data.insights.length === 0 ? (
              <p className="text-sm text-muted">{t('insights.none')}</p>
            ) : (
              <ul className="space-y-3">
                {data.insights.map((insight) => (
                  <InsightCard key={insight.code + insight.facts.map((f) => f.text ?? '').join('')} insight={insight} />
                ))}
              </ul>
            )}
          </section>

          {data.trends.length > 0 && (
            <Card>
              <h2 className="mb-1 font-semibold">{t('insights.sections.trends')}</h2>
              <p className="mb-3 text-xs text-muted">{t('insights.trendsNote')}</p>
              <ul className="grid gap-3 md:grid-cols-2">
                {data.trends.map((trend) => (
                  <TrendRow key={`${trend.metric}-${trend.provider}`} trend={trend} />
                ))}
              </ul>
            </Card>
          )}

          {data.comparisons.length > 0 && (
            <Card>
              <h2 className="mb-1 font-semibold">{t('insights.sections.compare', { days })}</h2>
              <p className="mb-3 text-xs text-muted">{t('insights.compare.note')}</p>
              <ComparisonTable comparisons={data.comparisons} />
            </Card>
          )}

          <Explanation days={days} />

          <Card>
            <h2 className="mb-2 font-semibold">{t('insights.sections.limitations')}</h2>
            <ul className="list-disc space-y-1 pl-5 text-sm text-muted">
              {data.limitations.map((code) => (
                <li key={code}>{t(`insights.limitations.${code}`, { defaultValue: code })}</li>
              ))}
            </ul>
          </Card>
        </div>
      )}
    </>
  )
}
