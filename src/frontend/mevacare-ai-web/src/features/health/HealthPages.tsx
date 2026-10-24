import clsx from 'clsx'
import { Trash2 } from 'lucide-react'
import { useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { SubNav } from '../../components/AppShell'
import { BarChart, LineChart } from '../../components/charts'
import { Alert, Button, Card, Field, PageHeader, SelectInput, Spinner, TextInput } from '../../components/ui'
import { addDays, localDateString, toDateTimeLocal, toOffsetIso } from '../../lib/dates'
import type { ActivityDay, ProviderAverages, SleepRecoveryDay, UnitSystem, WeightPoint, Workout } from '../../lib/types'
import { displayWeight, hoursAndMinutes, providerName, toKilograms, weightUnitLabel } from '../../lib/units'
import { isConsentRequired, usePreferences } from '../profile/hooks'
import { useActivity, useAddWeight, useDeleteWeight, useSleepRecovery, useWeightHistory } from './hooks'
import { HEALTH_NAV } from './navigation'

const MIN_KG = 20
const MAX_KG = 500

function ConsentNeeded() {
  const { t } = useTranslation()
  return (
    <Alert tone="warning" title={t('health.consentTitle')}>
      <p>{t('health.consentBody')}</p>
      <Link to="/settings/privacy" className="mt-1 inline-block font-semibold text-primary hover:underline">
        {t('health.consentAction')}
      </Link>
    </Alert>
  )
}

function useDuration() {
  const { t } = useTranslation()
  return (minutes: number | null | undefined) => {
    if (minutes === null || minutes === undefined) return '–'
    const { hours, minutes: rest } = hoursAndMinutes(minutes)
    return t('health.duration', { hours, minutes: String(rest).padStart(2, '0') })
  }
}

function RangeSelect({ days, onChange, options }: { days: number; onChange: (days: number) => void; options: number[] }) {
  const { t } = useTranslation()
  return (
    <div className="w-48">
      <Field label={t('health.range')}>
        {(p) => (
          <SelectInput {...p} value={days} onChange={(e) => onChange(Number(e.target.value))}>
            {options.map((option) => (
              <option key={option} value={option}>
                {t('health.lastDays', { count: option })}
              </option>
            ))}
          </SelectInput>
        )}
      </Field>
    </div>
  )
}

function Stat({ label, value, note }: { label: string; value: ReactNode; note?: string }) {
  return (
    <div>
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="text-lg font-semibold">{value}</dd>
      {note && <p className="text-xs text-muted">{note}</p>}
    </div>
  )
}

/** The days of the window, so that days without data stay visible as gaps instead of disappearing. */
function windowDates(from: string, days: number) {
  return Array.from({ length: days }, (_, i) => addDays(from, i))
}

const dayOfMonth = (date: string) => String(Number(date.slice(8, 10)))

// ---------------------------------------------------------------- weight

/** The wall-clock time of an entry in the clock it was recorded with. */
function recordedClock(point: { measuredAtUtc: string; utcOffsetMinutes: number }) {
  return new Date(Date.parse(point.measuredAtUtc.endsWith('Z') ? point.measuredAtUtc : `${point.measuredAtUtc}Z`) + point.utcOffsetMinutes * 60_000).toISOString()
}

const localTime = (point: { measuredAtUtc: string; utcOffsetMinutes: number }) => recordedClock(point).slice(11, 16)
const localDateTime = (point: { measuredAtUtc: string; utcOffsetMinutes: number }) => `${recordedClock(point).slice(0, 10)} ${localTime(point)}`

export function WeightPage() {
  const { t } = useTranslation()
  const prefs = usePreferences()
  const units: UnitSystem = prefs.data?.unitSystem ?? 'Metric'
  const unit = weightUnitLabel(units)
  const [days, setDays] = useState(90)
  const today = localDateString()
  const history = useWeightHistory(addDays(today, -(days - 1)), addDays(today, 1))
  const add = useAddWeight()
  const remove = useDeleteWeight()
  const [value, setValue] = useState('')
  const [when, setWhen] = useState(() => toDateTimeLocal(new Date()))
  const [error, setError] = useState<string | null>(null)

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const typed = Number(value.replace(',', '.'))
    const kilograms = toKilograms(typed, units)
    if (!Number.isFinite(typed) || kilograms < MIN_KG || kilograms > MAX_KG) {
      setError(t('health.weight.invalid', { min: displayWeight(MIN_KG, units), max: displayWeight(MAX_KG, units), unit }))
      return
    }

    setError(null)
    add.mutate(
      { kilograms: Math.round(kilograms * 100) / 100, measuredAt: toOffsetIso(new Date(when)) },
      { onSuccess: () => setValue('') },
    )
  }

  const points = history.data?.points ?? []
  const chart = points.map((p) => ({ date: p.localDate, value: displayWeight(p.kilograms, units) }))
  const change = history.data?.changeKilograms
  const consentMissing = isConsentRequired(history.error)

  return (
    <>
      <PageHeader title={t('health.weight.title')} subtitle={t('health.weight.subtitle')} />
      <SubNav items={HEALTH_NAV} />
      {consentMissing ? (
        <ConsentNeeded />
      ) : (
        <div className="space-y-6">
          <Card>
            <h2 className="mb-3 font-semibold">{t('health.weight.addTitle')}</h2>
            <form onSubmit={submit} noValidate className="grid gap-4 sm:grid-cols-[1fr_1fr_auto] sm:items-start">
              <Field label={t('health.weight.valueLabel', { unit })} error={error ?? undefined}>
                {(p) => <TextInput {...p} inputMode="decimal" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />}
              </Field>
              <Field label={t('health.weight.whenLabel')}>
                {(p) => <TextInput {...p} type="datetime-local" value={when} onChange={(e) => e.target.value && setWhen(e.target.value)} />}
              </Field>
              <div className="sm:pt-[1.625rem]">
                <Button type="submit" loading={add.isPending}>
                  {t('health.weight.add')}
                </Button>
              </div>
            </form>
            {add.isError && !isConsentRequired(add.error) && (
              <p role="alert" className="mt-3 text-sm text-danger">
                {t('common.genericError')}
              </p>
            )}
            {add.isError && isConsentRequired(add.error) && <ConsentNeeded />}
          </Card>

          <Card>
            <div className="mb-4 flex flex-wrap items-end justify-between gap-3">
              <h2 className="font-semibold">{t('health.weight.evolution')}</h2>
              <RangeSelect days={days} onChange={setDays} options={[30, 90, 365]} />
            </div>
            {history.isPending ? (
              <Spinner label={t('common.loading')} />
            ) : history.isError ? (
              <Alert tone="danger">{t('common.genericError')}</Alert>
            ) : points.length === 0 ? (
              <p className="text-sm text-muted">{t('health.weight.empty')}</p>
            ) : (
              <>
                <dl className="mb-4 grid grid-cols-2 gap-4 sm:grid-cols-3">
                  <Stat label={t('health.weight.latest')} value={`${displayWeight(history.data!.latestKilograms ?? 0, units)} ${unit}`} />
                  <Stat
                    label={t('health.weight.change')}
                    value={
                      change === null || change === undefined
                        ? '–'
                        : `${change > 0 ? '+' : ''}${displayWeight(change, units)} ${unit}`
                    }
                    note={change === null || change === undefined ? t('health.weight.needTwo') : undefined}
                  />
                  <Stat label={t('health.weight.entries')} value={points.length} />
                </dl>
                <LineChart
                  points={chart}
                  unit={unit}
                  ariaLabel={t('health.weight.chartLabel', {
                    count: points.length,
                    from: chart[0].date,
                    to: chart[chart.length - 1].date,
                    low: Math.min(...chart.map((c) => c.value)),
                    high: Math.max(...chart.map((c) => c.value)),
                    unit,
                  })}
                />
              </>
            )}
          </Card>

          {points.length > 0 && (
            <Card className="!p-0">
              <ul className="divide-y divide-line">
                {[...points].reverse().map((point) => (
                  <WeightRow key={point.id} point={point} units={units} onDelete={() => remove.mutate(point.id)} deleting={remove.isPending} />
                ))}
              </ul>
              {remove.isError && (
                <p role="alert" className="p-4 text-sm text-danger">
                  {t('common.genericError')}
                </p>
              )}
            </Card>
          )}
        </div>
      )}
    </>
  )
}

function WeightRow({ point, units, onDelete, deleting }: { point: WeightPoint; units: UnitSystem; onDelete: () => void; deleting: boolean }) {
  const { t } = useTranslation()
  const manual = point.provider === 'manual'
  return (
    <li className="flex items-center justify-between gap-3 px-5 py-3 text-sm">
      <div>
        <p className="font-medium">
          {displayWeight(point.kilograms, units)} {weightUnitLabel(units)}
        </p>
        <p className="text-xs text-muted">
          {point.localDate} {localTime(point)} · {manual ? t('health.weight.enteredByYou') : t('health.weight.importedFrom', { provider: providerName(point.provider) })}
        </p>
      </div>
      {manual && (
        <Button variant="ghost" onClick={onDelete} disabled={deleting} aria-label={t('health.weight.deleteLabel', { date: point.localDate })}>
          <Trash2 className="size-4" aria-hidden />
        </Button>
      )}
    </li>
  )
}

// ---------------------------------------------------------------- sleep and recovery

function StageBar({ day }: { day: SleepRecoveryDay }) {
  const { t } = useTranslation()
  const duration = useDuration()
  const sleep = day.sleep!
  const parts = [
    { key: 'deep', minutes: sleep.deepMinutes ?? 0, className: 'bg-ai' },
    { key: 'rem', minutes: sleep.remMinutes ?? 0, className: 'bg-primary' },
    { key: 'light', minutes: sleep.lightMinutes ?? 0, className: 'bg-primary/35' },
    { key: 'awake', minutes: sleep.awakeMinutes ?? 0, className: 'bg-muted/45' },
  ] as const
  const total = parts.reduce((sum, part) => sum + part.minutes, 0)
  if (total === 0) return null
  return (
    <div className="space-y-2">
      <div
        role="img"
        aria-label={parts.map((p) => `${t(`health.sleep.stages.${p.key}`)} ${duration(p.minutes)}`).join(', ')}
        className="flex h-3 overflow-hidden rounded-full"
      >
        {parts.map((p) => (
          <div key={p.key} className={p.className} style={{ width: `${(p.minutes / total) * 100}%` }} />
        ))}
      </div>
      <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
        {parts.map((p) => (
          <li key={p.key} className="flex items-center gap-1.5">
            <span className={clsx('size-2.5 rounded-full border border-line', p.className)} aria-hidden />
            {t(`health.sleep.stages.${p.key}`)} {duration(p.minutes)}
          </li>
        ))}
      </ul>
    </div>
  )
}

function ProviderSleep({ provider, days, from, count, averages }: { provider: string; days: SleepRecoveryDay[]; from: string; count: number; averages?: ProviderAverages }) {
  const { t } = useTranslation()
  const duration = useDuration()
  const name = providerName(provider)
  const byDate = new Map(days.map((d) => [d.date, d]))
  const dates = windowDates(from, count)
  const lastSleep = [...days].reverse().find((d) => d.sleep !== null)
  const lastRecovery = [...days].reverse().find((d) => d.recovery !== null && d.recovery.score !== null)

  return (
    <section aria-label={name} className="space-y-4">
      <h2 className="text-lg font-semibold">{name}</h2>
      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <h3 className="mb-3 font-semibold">{t('health.sleep.lastNight')}</h3>
          {lastSleep?.sleep ? (
            lastSleep.sleep.isScored ? (
              <div className="space-y-3">
                <p className="text-xs text-muted">{lastSleep.date}</p>
                <p className="text-3xl font-semibold">{duration(lastSleep.sleep.asleepMinutes)}</p>
                <StageBar day={lastSleep} />
                <dl className="grid grid-cols-2 gap-3">
                  <Stat label={t('health.sleep.performance')} value={lastSleep.sleep.performancePercent === null ? '–' : `${lastSleep.sleep.performancePercent}%`} />
                  <Stat label={t('health.sleep.efficiency')} value={lastSleep.sleep.efficiencyPercent === null ? '–' : `${lastSleep.sleep.efficiencyPercent}%`} />
                </dl>
                {lastSleep.napMinutes > 0 && <p className="text-xs text-muted">{t('health.sleep.naps', { duration: duration(lastSleep.napMinutes) })}</p>}
              </div>
            ) : (
              <p className="text-sm text-muted">{t('health.sleep.pending', { provider: name })}</p>
            )
          ) : (
            <p className="text-sm text-muted">{t('health.sleep.noSleep')}</p>
          )}
        </Card>

        <Card>
          <h3 className="mb-3 font-semibold">{t('health.sleep.recoveryTitle', { provider: name })}</h3>
          {lastRecovery?.recovery ? (
            <div className="space-y-3">
              <p className="text-xs text-muted">{lastRecovery.date}</p>
              <p className="text-3xl font-semibold">{lastRecovery.recovery.score}%</p>
              <dl className="grid grid-cols-2 gap-3">
                <Stat
                  label={t('health.sleep.restingHeartRate')}
                  value={lastRecovery.recovery.restingHeartRate === null ? '–' : `${lastRecovery.recovery.restingHeartRate} ${t('health.bpm')}`}
                />
                <Stat
                  label={t('health.sleep.hrv')}
                  value={lastRecovery.recovery.hrvRmssdMilliseconds === null ? '–' : `${lastRecovery.recovery.hrvRmssdMilliseconds} ${t('health.ms')}`}
                />
              </dl>
              <p className="text-xs text-muted">{t('health.providerScoreNote', { provider: name })}</p>
            </div>
          ) : (
            <p className="text-sm text-muted">{t('health.sleep.noRecovery')}</p>
          )}
        </Card>
      </div>

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <h3 className="mb-3 font-semibold">{t('health.sleep.durationChart')}</h3>
          <BarChart
            max={12 * 60}
            ariaLabel={t('health.sleep.durationChartLabel', { count })}
            bars={dates.map((date) => {
              const minutes = byDate.get(date)?.sleep?.asleepMinutes ?? null
              return { label: dayOfMonth(date), value: minutes, title: `${date}: ${duration(minutes)}` }
            })}
          />
        </Card>
        <Card>
          <h3 className="mb-3 font-semibold">{t('health.sleep.recoveryChart', { provider: name })}</h3>
          <BarChart
            max={100}
            ariaLabel={t('health.sleep.recoveryChartLabel', { provider: name, count })}
            bars={dates.map((date) => {
              const score = byDate.get(date)?.recovery?.score ?? null
              return { label: dayOfMonth(date), value: score, title: `${date}: ${score === null ? '–' : `${score}%`}` }
            })}
          />
        </Card>
      </div>

      {averages && (
        <Card>
          <h3 className="mb-3 font-semibold">{t('health.sleep.averages', { count })}</h3>
          <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <Stat label={t('health.sleep.avgSleep')} value={duration(averages.averageAsleepMinutes)} note={t('health.sleep.basedOn', { count: averages.daysWithSleep })} />
            <Stat label={t('health.sleep.avgRecovery')} value={averages.averageRecoveryScore === null ? '–' : `${averages.averageRecoveryScore}%`} note={t('health.sleep.basedOn', { count: averages.daysWithRecovery })} />
            <Stat label={t('health.sleep.restingHeartRate')} value={averages.averageRestingHeartRate === null ? '–' : `${averages.averageRestingHeartRate} ${t('health.bpm')}`} />
            <Stat label={t('health.sleep.hrv')} value={averages.averageHrvRmssdMilliseconds === null ? '–' : `${averages.averageHrvRmssdMilliseconds} ${t('health.ms')}`} />
          </dl>
        </Card>
      )}
    </section>
  )
}

function NoDeviceData() {
  const { t } = useTranslation()
  return (
    <Card className="space-y-2">
      <h2 className="font-semibold">{t('health.noDeviceTitle')}</h2>
      <p className="text-sm text-muted">{t('health.noDeviceBody')}</p>
      <Link to="/devices" className="inline-block text-sm font-semibold text-primary hover:underline">
        {t('health.noDeviceAction')}
      </Link>
    </Card>
  )
}

export function SleepPage() {
  const { t } = useTranslation()
  const [days, setDays] = useState(14)
  const from = addDays(localDateString(), -(days - 1))
  const range = useSleepRecovery(from, localDateString())
  const providers = [...new Set((range.data?.days ?? []).map((d) => d.provider))].sort()

  return (
    <>
      <PageHeader title={t('health.sleep.title')} subtitle={t('health.sleep.subtitle')}>
        <RangeSelect days={days} onChange={setDays} options={[7, 14, 30]} />
      </PageHeader>
      <SubNav items={HEALTH_NAV} />
      {isConsentRequired(range.error) ? (
        <ConsentNeeded />
      ) : range.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : range.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : providers.length === 0 ? (
        <NoDeviceData />
      ) : (
        <div className="space-y-8">
          {providers.map((provider) => (
            <ProviderSleep
              key={provider}
              provider={provider}
              days={range.data.days.filter((d) => d.provider === provider)}
              from={from}
              count={days}
              averages={range.data.averages.find((a) => a.provider === provider)}
            />
          ))}
        </div>
      )}
    </>
  )
}

// ---------------------------------------------------------------- activity

function WorkoutRow({ workout }: { workout: Workout }) {
  const { t } = useTranslation()
  const duration = useDuration()
  const minutes = (Date.parse(workout.endUtc) - Date.parse(workout.startUtc)) / 60_000
  return (
    <li className="px-5 py-3 text-sm">
      <p className="font-medium capitalize">{workout.sportName.replace(/[-_]/g, ' ')}</p>
      <p className="text-xs text-muted">
        {localDateTime({ measuredAtUtc: workout.startUtc, utcOffsetMinutes: workout.utcOffsetMinutes })} · {duration(minutes)}
        {workout.isScored ? (
          <>
            {workout.strain !== null && ` · ${t('health.activity.strainValue', { value: workout.strain })}`}
            {workout.averageHeartRate !== null && ` · ${workout.averageHeartRate} ${t('health.bpm')}`}
            {workout.calories !== null && ` · ${Math.round(workout.calories)} ${t('health.kcal')}`}
          </>
        ) : (
          ` · ${t('health.activity.workoutPending')}`
        )}
      </p>
    </li>
  )
}

function ProviderActivity({ provider, days, from, count }: { provider: string; days: ActivityDay[]; from: string; count: number }) {
  const { t } = useTranslation()
  const name = providerName(provider)
  const byDate = new Map(days.map((d) => [d.date, d]))
  const workouts = days.flatMap((d) => d.workouts).sort((a, b) => b.startUtc.localeCompare(a.startUtc))
  const withCalories = days.filter((d) => d.caloriesBurned !== null)

  return (
    <section aria-label={name} className="space-y-4">
      <h2 className="text-lg font-semibold">{name}</h2>
      <Card>
        <h3 className="mb-1 font-semibold">{t('health.activity.strainChart', { provider: name })}</h3>
        <p className="mb-3 text-xs text-muted">{t('health.providerScoreNote', { provider: name })}</p>
        <BarChart
          max={21}
          ariaLabel={t('health.activity.strainChartLabel', { provider: name, count })}
          bars={windowDates(from, count).map((date) => {
            const strain = byDate.get(date)?.dayStrain ?? null
            return { label: dayOfMonth(date), value: strain, title: `${date}: ${strain ?? '–'}` }
          })}
        />
      </Card>
      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <h3 className="mb-3 font-semibold">{t('health.activity.calories')}</h3>
          {withCalories.length === 0 ? (
            <p className="text-sm text-muted">{t('health.activity.noCalories')}</p>
          ) : (
            <>
              <p className="text-3xl font-semibold">
                {Math.round(withCalories[withCalories.length - 1].caloriesBurned!)} {t('health.kcal')}
              </p>
              <p className="text-xs text-muted">
                {withCalories[withCalories.length - 1].date} · {t('health.activity.caloriesNote')}
              </p>
            </>
          )}
        </Card>
        <Card className="!p-0">
          <h3 className="px-5 pt-5 font-semibold sm:px-6 sm:pt-6">{t('health.activity.workouts')}</h3>
          {workouts.length === 0 ? (
            <p className="p-5 text-sm text-muted">{t('health.activity.noWorkouts')}</p>
          ) : (
            <ul className="mt-2 divide-y divide-line">
              {workouts.slice(0, 10).map((workout) => (
                <WorkoutRow key={workout.id} workout={workout} />
              ))}
            </ul>
          )}
        </Card>
      </div>
    </section>
  )
}

export function ActivityPage() {
  const { t } = useTranslation()
  const [days, setDays] = useState(14)
  const from = addDays(localDateString(), -(days - 1))
  const range = useActivity(from, localDateString())
  const providers = [...new Set((range.data?.days ?? []).map((d) => d.provider))].sort()

  return (
    <>
      <PageHeader title={t('health.activity.title')} subtitle={t('health.activity.subtitle')}>
        <RangeSelect days={days} onChange={setDays} options={[7, 14, 30]} />
      </PageHeader>
      <SubNav items={HEALTH_NAV} />
      {isConsentRequired(range.error) ? (
        <ConsentNeeded />
      ) : range.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : range.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : providers.length === 0 ? (
        <NoDeviceData />
      ) : (
        <div className="space-y-8">
          {providers.map((provider) => (
            <ProviderActivity key={provider} provider={provider} days={range.data.days.filter((d) => d.provider === provider)} from={from} count={days} />
          ))}
        </div>
      )}
    </>
  )
}
