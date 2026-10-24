import { Activity, LineChart as LineChartIcon, MoonStar } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { LineChart } from '../../components/charts'
import { Card } from '../../components/ui'
import { addDays, localDateString } from '../../lib/dates'
import { displayWeight, hoursAndMinutes, providerName, weightUnitLabel } from '../../lib/units'
import { isConsentRequired, usePreferences } from '../profile/hooks'
import { useActivity, useSleepRecovery, useWeightHistory } from './hooks'

function CardTitle({ icon, title }: { icon: ReactNode; title: string }) {
  return (
    <h2 className="mb-3 flex items-center gap-2 font-semibold">
      <span className="text-primary">{icon}</span>
      {title}
    </h2>
  )
}

function CardLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link to={to} className="mt-3 inline-block text-sm font-semibold text-primary hover:underline">
      {children}
    </Link>
  )
}

function Empty({ consentMissing, children }: { consentMissing: boolean; children: ReactNode }) {
  const { t } = useTranslation()
  return <p className="text-sm text-muted">{consentMissing ? t('dashboard.consentNeeded') : children}</p>
}

/** Real stored weights only: with none, the card says so and offers to add one. */
export function WeightCard() {
  const { t } = useTranslation()
  const today = localDateString()
  const history = useWeightHistory(addDays(today, -89), addDays(today, 1))
  const units = usePreferences().data?.unitSystem ?? 'Metric'
  const unit = weightUnitLabel(units)
  const points = history.data?.points ?? []
  const change = history.data?.changeKilograms

  return (
    <Card>
      <CardTitle icon={<LineChartIcon className="size-5" />} title={t('dashboard.weightEvolution')} />
      {points.length > 0 ? (
        <div className="space-y-2">
          <p className="text-2xl font-semibold">
            {displayWeight(history.data!.latestKilograms ?? 0, units)} {unit}
            {change !== null && change !== undefined && (
              <span className="ml-2 text-sm font-normal text-muted">
                {t('dashboard.weightChange', { value: `${change > 0 ? '+' : ''}${displayWeight(change, units)} ${unit}`, days: 90 })}
              </span>
            )}
          </p>
          <LineChart
            points={points.map((p) => ({ date: p.localDate, value: displayWeight(p.kilograms, units) }))}
            unit={unit}
            ariaLabel={t('health.weight.chartLabel', {
              count: points.length,
              from: points[0].localDate,
              to: points[points.length - 1].localDate,
              low: Math.min(...points.map((p) => displayWeight(p.kilograms, units))),
              high: Math.max(...points.map((p) => displayWeight(p.kilograms, units))),
              unit,
            })}
          />
        </div>
      ) : (
        <Empty consentMissing={isConsentRequired(history.error)}>{t('dashboard.weightEmpty')}</Empty>
      )}
      <CardLink to="/health/weight">{t('dashboard.weightAction')}</CardLink>
    </Card>
  )
}

export function SleepCard() {
  const { t } = useTranslation()
  const today = localDateString()
  const range = useSleepRecovery(addDays(today, -6), today)
  const days = range.data?.days ?? []
  const lastSleep = [...days].reverse().find((d) => d.sleep?.isScored)
  const lastRecovery = [...days].reverse().find((d) => d.recovery !== null && d.recovery.score !== null)
  const { hours, minutes } = hoursAndMinutes(lastSleep?.sleep?.asleepMinutes ?? 0)

  return (
    <Card>
      <CardTitle icon={<MoonStar className="size-5" />} title={t('dashboard.sleepSummary')} />
      {lastSleep?.sleep || lastRecovery?.recovery ? (
        <div className="space-y-1">
          {lastSleep?.sleep && (
            <p className="text-2xl font-semibold">
              {t('health.duration', { hours, minutes: String(minutes).padStart(2, '0') })}
              <span className="ml-2 text-sm font-normal text-muted">{lastSleep.date}</span>
            </p>
          )}
          {lastRecovery?.recovery && (
            <p className="text-sm">
              {t('dashboard.recovery', { provider: providerName(lastRecovery.provider), score: lastRecovery.recovery.score })}
            </p>
          )}
          <p className="text-xs text-muted">{t('dashboard.fromDevice', { provider: providerName((lastSleep ?? lastRecovery!).provider) })}</p>
        </div>
      ) : (
        <Empty consentMissing={isConsentRequired(range.error)}>{t('dashboard.sleepEmpty')}</Empty>
      )}
      <CardLink to={days.length > 0 ? '/health/sleep' : '/devices'}>{days.length > 0 ? t('dashboard.sleepAction') : t('dashboard.connectAction')}</CardLink>
    </Card>
  )
}

export function ActivityCard() {
  const { t } = useTranslation()
  const today = localDateString()
  const range = useActivity(addDays(today, -6), today)
  const days = range.data?.days ?? []
  const lastStrain = [...days].reverse().find((d) => d.dayStrain !== null)
  const workouts = days.reduce((sum, d) => sum + d.workouts.length, 0)

  return (
    <Card>
      <CardTitle icon={<Activity className="size-5" />} title={t('dashboard.activitySummary')} />
      {lastStrain || workouts > 0 ? (
        <div className="space-y-1">
          {lastStrain && (
            <p className="text-2xl font-semibold">
              {t('dashboard.strain', { provider: providerName(lastStrain.provider), value: lastStrain.dayStrain })}
              <span className="ml-2 text-sm font-normal text-muted">{lastStrain.date}</span>
            </p>
          )}
          <p className="text-sm text-muted">{t('dashboard.workoutsWeek', { count: workouts })}</p>
        </div>
      ) : (
        <Empty consentMissing={isConsentRequired(range.error)}>{t('dashboard.activityEmpty')}</Empty>
      )}
      <CardLink to={days.length > 0 ? '/health/activity' : '/devices'}>{days.length > 0 ? t('dashboard.activityAction') : t('dashboard.connectAction')}</CardLink>
    </Card>
  )
}
