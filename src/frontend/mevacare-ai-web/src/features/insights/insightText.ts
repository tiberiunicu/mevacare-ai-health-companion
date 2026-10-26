import type { TFunction } from 'i18next'
import type { Insight, InsightFact } from '../../lib/types'
import { providerName } from '../../lib/units'

const SHARE_KEYS = ['direction', 'confidence', 'shift'] as const

/** Numbers as the user's locale writes them, without trailing zeros (0.4, 1,950). */
export function formatNumber(value: number, locale: string, maximumFractionDigits = 2) {
  return new Intl.NumberFormat(locale, { maximumFractionDigits }).format(value)
}

/**
 * Values for the translation templates: every numeric fact as written in the user's locale, plus an `_abs` variant (the templates carry the
 * direction in words), codes translated, providers named. The numbers are exactly the ones the server calculated; nothing is derived here.
 */
export function factVariables(facts: InsightFact[], t: TFunction, locale: string): Record<string, string | number> {
  const vars: Record<string, string | number> = {}
  for (const fact of facts) {
    if (fact.number !== null) {
      vars[fact.key] = formatNumber(fact.number, locale)
      vars[`${fact.key}_abs`] = formatNumber(Math.abs(fact.number), locale)
    } else if (fact.text !== null) {
      if ((SHARE_KEYS as readonly string[]).includes(fact.key)) {
        vars[fact.key] = t(`insights.${fact.key}.${fact.text}`, { defaultValue: fact.text })
      } else if (fact.key === 'provider' || fact.key === 'sleep_provider') {
        vars.provider = providerName(fact.text)
      } else {
        vars[fact.key] = fact.text
      }
    }
  }

  return vars
}

const numberOf = (insight: Insight, key: string) => insight.facts.find((f) => f.key === key)?.number ?? null
const textOf = (insight: Insight, key: string) => insight.facts.find((f) => f.key === key)?.text ?? null

/** The translation key and variables of an insight; the English statement from the server is the fallback for codes this UI does not know yet. */
export function describeInsight(insight: Insight, t: TFunction, locale: string): { text: string; details: string[] } {
  const vars = factVariables(insight.facts, t, locale)
  let key = insight.code
  if (insight.code === 'weight_trend' && textOf(insight, 'direction') === 'stable') key = 'weight_trend_stable'
  if (insight.code === 'calories_vs_target' || insight.code === 'protein_vs_target') {
    const deviation = numberOf(insight, 'deviation_percent') ?? 0
    vars.aboveBelow = t(deviation > 0 ? 'insights.above' : 'insights.below')
    vars.nutrient = t(insight.code === 'calories_vs_target' ? 'insights.nutrients.calories' : 'insights.nutrients.protein')
    vars.unit = insight.code === 'calories_vs_target' ? 'kcal' : 'g'
    key = 'nutrient_vs_target'
  }

  if (insight.code === 'sleep_duration_change') vars.moreLess = t((numberOf(insight, 'change') ?? 0) > 0 ? 'insights.more' : 'insights.less')
  if (insight.code === 'hrv_trend' || insight.code === 'resting_heart_rate_trend') {
    vars.label = t(insight.code === 'hrv_trend' ? 'insights.metrics.hrv_ms' : 'insights.metrics.resting_heart_rate_bpm')
    vars.unit = insight.code === 'hrv_trend' ? 'ms' : 'bpm'
  }

  if (insight.code === 'data_gaps') vars.missing = insight.facts.map((f) => t(`insights.gaps.${f.key}`, { defaultValue: f.key })).join(', ')

  const text = t(`insights.items.${key}`, { ...vars, defaultValue: insight.statement })

  // Weight shifts also say what else changed, as separate plain sentences, never as a cause.
  const details: string[] = []
  if (insight.code === 'weight_trend_shift') {
    if (numberOf(insight, 'calories_average_change') !== null) details.push(t('insights.factors.calories', vars))
    if (numberOf(insight, 'sleep_average_change') !== null) details.push(t('insights.factors.sleep', vars))
    if ((numberOf(insight, 'factors_count') ?? 0) === 0) details.push(t('insights.factors.none'))
  }

  return { text, details }
}
