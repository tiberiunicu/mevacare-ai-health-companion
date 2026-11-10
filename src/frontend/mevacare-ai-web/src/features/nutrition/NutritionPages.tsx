import clsx from 'clsx'
import { ChevronLeft, ChevronRight, Pencil, Plus, SendHorizontal, Trash2 } from 'lucide-react'
import { useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { SubNav } from '../../components/AppShell'
import { Alert, Button, Card, Field, PageHeader, Spinner, TextInput } from '../../components/ui'
import { addDays, localDateString } from '../../lib/dates'
import type { DailySummary, Meal, NutrientTotals, NutrientValues } from '../../lib/types'
import { useChat } from '../chat/chatSession'
import { useAiAllowed } from '../chat/hooks'
import { isConsentRequired } from '../profile/hooks'
import { useDailySummary, useDeleteMeal, useRangeSummary, useSetTargets, useTargets } from './hooks'
import { MealEditor } from './MealEditor'
import { NUTRITION_NAV } from './navigation'
import { itemsFromMeal } from './mealEditorModel'

const fmt = (value: number | null | undefined, digits = 0) => (value === null || value === undefined ? '–' : value.toFixed(digits))

function ConsentNeeded() {
  const { t } = useTranslation()
  return (
    <Alert tone="warning" title={t('nutrition.journal.consentTitle')}>
      <p>{t('nutrition.journal.consentBody')}</p>
      <Link to="/settings/privacy" className="mt-1 inline-block font-semibold text-primary hover:underline">
        {t('nutrition.journal.consentAction')}
      </Link>
    </Alert>
  )
}

function DayNavigator({ date, onChange }: { date: string; onChange: (date: string) => void }) {
  const { t } = useTranslation()
  return (
    <div className="mb-4 flex flex-wrap items-center gap-2">
      <Button variant="secondary" aria-label={t('nutrition.journal.previous')} onClick={() => onChange(addDays(date, -1))}>
        <ChevronLeft className="size-4" aria-hidden />
      </Button>
      <div className="w-44">
        <label htmlFor="journal-date" className="sr-only">
          {t('nutrition.journal.date')}
        </label>
        <TextInput id="journal-date" type="date" value={date} onChange={(e) => e.target.value && onChange(e.target.value)} />
      </div>
      <Button variant="secondary" aria-label={t('nutrition.journal.next')} onClick={() => onChange(addDays(date, 1))}>
        <ChevronRight className="size-4" aria-hidden />
      </Button>
      <Button variant="ghost" onClick={() => onChange(localDateString())}>
        {t('nutrition.journal.today')}
      </Button>
    </div>
  )
}

function TotalsLine({ totals }: { totals: NutrientTotals }) {
  const { t } = useTranslation()
  return (
    <p className="text-sm">
      <span className="font-semibold">
        {fmt(totals.calories)} {t('nutrition.nutrients.kcal')}
      </span>{' '}
      · {t('nutrition.nutrients.proteinG')} {fmt(totals.proteinG, 1)} g · {t('nutrition.nutrients.carbohydratesG')} {fmt(totals.carbohydratesG, 1)} g ·{' '}
      {t('nutrition.nutrients.fatG')} {fmt(totals.fatG, 1)} g · {t('nutrition.nutrients.fiberG')} {fmt(totals.fiberG, 1)} g
    </p>
  )
}

function Flags({ totals }: { totals: NutrientTotals }) {
  const { t } = useTranslation()
  return (
    <>
      {totals.incompleteData && <p className="text-xs text-warning">{t('nutrition.journal.incomplete')}</p>}
      {totals.includesEstimates && <p className="text-xs text-warning">{t('nutrition.journal.includesEstimates')}</p>}
    </>
  )
}

/**
 * "What did you eat?": the sentence goes to the assistant, which prepares a card to check and save, in the side panel.
 * It is the quickest way to log food; the list-based editor stays for corrections and for people who prefer it.
 */
function JournalChatBar() {
  const { t } = useTranslation()
  const chat = useChat()
  const { loaded, allowed } = useAiAllowed()
  const [text, setText] = useState('')

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const sentence = text.trim()
    if (!sentence || chat.streaming) return
    setText('')
    void chat.ask(sentence)
  }

  return (
    <form onSubmit={submit} className="mb-4 flex items-end gap-2">
      <div className="flex-1">
        <label htmlFor="journal-chat" className="sr-only">
          {t('nutrition.journal.chatLabel')}
        </label>
        <TextInput
          id="journal-chat"
          value={text}
          maxLength={1000}
          disabled={loaded && !allowed}
          placeholder={loaded && !allowed ? t('nutrition.journal.chatConsent') : t('nutrition.journal.chatPlaceholder')}
          onChange={(e) => setText(e.target.value)}
        />
      </div>
      <Button type="submit" disabled={!allowed || text.trim() === '' || chat.streaming}>
        <SendHorizontal className="size-4" aria-hidden />
        {t('nutrition.journal.chatSend')}
      </Button>
    </form>
  )
}

export function FoodJournalPage() {
  const { t } = useTranslation()
  const [date, setDate] = useState(localDateString())
  const [editing, setEditing] = useState<Meal | 'new' | null>(null)
  const summary = useDailySummary(date)
  const remove = useDeleteMeal()

  return (
    <>
      <PageHeader title={t('nutrition.journal.title')} subtitle={t('nutrition.journal.subtitle')}>
        <Button onClick={() => setEditing('new')}>
          <Plus className="size-4" aria-hidden />
          {t('nutrition.journal.addMeal')}
        </Button>
      </PageHeader>
      <SubNav items={NUTRITION_NAV} />
      <JournalChatBar />
      <DayNavigator
        date={date}
        onChange={(next) => {
          setDate(next)
          setEditing(null)
        }}
      />

      {editing && (
        <Card className="mb-6">
          <h2 className="mb-4 text-lg font-semibold">{editing === 'new' ? t('nutrition.editor.newMeal') : t('nutrition.editor.editMeal')}</h2>
          <MealEditor
            key={editing === 'new' ? 'new' : editing.id}
            meal={editing === 'new' ? undefined : editing}
            initialItems={editing === 'new' ? undefined : itemsFromMeal(editing)}
            initialTime={editing === 'new' ? defaultTimeFor(date) : undefined}
            onSaved={() => setEditing(null)}
            onCancel={() => setEditing(null)}
          />
        </Card>
      )}

      {summary.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : isConsentRequired(summary.error) ? (
        <ConsentNeeded />
      ) : summary.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <JournalDay summary={summary.data} onEdit={setEditing} onDelete={(id) => remove.mutate(id)} deleting={remove.isPending} />
      )}
    </>
  )
}

function defaultTimeFor(date: string): Date {
  const now = new Date()
  if (date === localDateString(now)) return now
  const [y, m, d] = date.split('-').map(Number)
  return new Date(y, m - 1, d, 12, 0, 0)
}

function JournalDay({
  summary, onEdit, onDelete, deleting,
}: {
  summary: DailySummary
  onEdit: (meal: Meal) => void
  onDelete: (id: string) => void
  deleting: boolean
}) {
  const { t } = useTranslation()
  if (summary.meals.length === 0) {
    return (
      <Card>
        <p className="text-sm text-muted">{t('nutrition.journal.emptyDay')}</p>
      </Card>
    )
  }

  return (
    <div className="space-y-4">
      {summary.meals.map((meal) => (
        <Card key={meal.id}>
          <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
            <div>
              <h2 className="font-semibold">
                {t(`nutrition.mealTypes.${meal.mealType}`)}{' '}
                <span className="text-sm font-normal text-muted">
                  {new Date(meal.eatenAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                </span>
              </h2>
              {meal.origin === 'Assistant' && <p className="text-xs text-ai">{t('nutrition.journal.assistantBadge')}</p>}
              {meal.notes && <p className="text-sm text-muted">{meal.notes}</p>}
            </div>
            <div className="flex gap-1">
              <Button variant="ghost" onClick={() => onEdit(meal)} aria-label={`${t('nutrition.journal.edit')}: ${t(`nutrition.mealTypes.${meal.mealType}`)}`}>
                <Pencil className="size-4" aria-hidden />
              </Button>
              <Button
                variant="ghost"
                disabled={deleting}
                onClick={() => onDelete(meal.id)}
                aria-label={`${t('nutrition.journal.delete')}: ${t(`nutrition.mealTypes.${meal.mealType}`)}`}
              >
                <Trash2 className="size-4" aria-hidden />
              </Button>
            </div>
          </div>
          <ul className="divide-y divide-line text-sm">
            {meal.items.map((item) => (
              <li key={item.id} className="flex flex-wrap items-baseline justify-between gap-x-4 py-1.5">
                <span className="min-w-0 break-words">
                  {item.name}
                  {item.isEstimated && (
                    <span className="ml-2 rounded-full bg-warning-soft px-2 py-0.5 text-xs text-warning">{t('nutrition.journal.estimatedBadge')}</span>
                  )}
                </span>
                <span className="shrink-0 text-muted">
                  {fmt(item.quantityGrams, 0)} g · {fmt(item.nutrients.calories)} {t('nutrition.nutrients.kcal')}
                </span>
              </li>
            ))}
          </ul>
          <div className="mt-2 border-t border-line pt-2">
            <TotalsLine totals={meal.totals} />
            <Flags totals={meal.totals} />
          </div>
        </Card>
      ))}
      <Card className="bg-primary-soft">
        <h2 className="mb-1 text-sm font-semibold">{t('nutrition.journal.dayTotal')}</h2>
        <TotalsLine totals={summary.totals} />
        <Flags totals={summary.totals} />
      </Card>
    </div>
  )
}

const BAR_ROWS = [
  { key: 'calories', unit: 'kcal' },
  { key: 'proteinG', unit: 'g' },
  { key: 'carbohydratesG', unit: 'g' },
  { key: 'fatG', unit: 'g' },
  { key: 'fiberG', unit: 'g' },
] as const

export function ProgressRows({ totals, targets, compact = false }: { totals: NutrientTotals; targets: NutrientValues; compact?: boolean }) {
  const { t } = useTranslation()
  return (
    <ul className="space-y-3">
      {BAR_ROWS.map(({ key, unit }) => {
        const value = totals[key]
        const target = targets[key]
        const ratio = target ? Math.min(value / target, 1) : 0
        const over = target !== null && value > target
        return (
          <li key={key}>
            <div className="mb-1 flex flex-wrap items-baseline justify-between gap-2 text-sm">
              <span className="font-medium">{t(`nutrition.nutrients.${key}`)}</span>
              <span className="text-muted">
                {fmt(value, key === 'calories' ? 0 : 1)} {unit}
                {target !== null ? ` / ${fmt(target, key === 'calories' ? 0 : 1)} ${unit}` : !compact ? ` · ${t('nutrition.summary.noTarget')}` : ''}
              </span>
            </div>
            {target !== null && (
              <div
                role="progressbar"
                aria-label={t(`nutrition.nutrients.${key}`)}
                aria-valuemin={0}
                aria-valuemax={target}
                aria-valuenow={Math.min(value, target)}
                className="h-2.5 overflow-hidden rounded-full bg-surface-2"
              >
                <div className={clsx('h-full rounded-full', over ? 'bg-warning' : 'bg-primary')} style={{ width: `${ratio * 100}%` }} />
              </div>
            )}
          </li>
        )
      })}
    </ul>
  )
}

export function DailySummaryPage() {
  const { t } = useTranslation()
  const [date, setDate] = useState(localDateString())
  const summary = useDailySummary(date)
  const today = localDateString()
  const range = useRangeSummary(addDays(today, -6), today)

  return (
    <>
      <PageHeader title={t('nutrition.summary.title')} subtitle={t('nutrition.summary.subtitle')} />
      <SubNav items={NUTRITION_NAV} />
      <DayNavigator date={date} onChange={setDate} />
      {summary.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : isConsentRequired(summary.error) ? (
        <ConsentNeeded />
      ) : summary.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <div className="space-y-6">
          <Card>
            <ProgressRows totals={summary.data.totals} targets={summary.data.targets} />
            <Flags totals={summary.data.totals} />
            {Object.values(summary.data.targets).every((v) => v === null) && (
              <Link to="/nutrition/goals" className="mt-3 inline-block text-sm font-semibold text-primary hover:underline">
                {t('nutrition.summary.setTargets')}
              </Link>
            )}
            <dl className="mt-4 grid gap-2 text-sm sm:grid-cols-5">
              {BAR_ROWS.map(({ key, unit }) => {
                const remaining = summary.data.remaining[key]
                return (
                  <div key={key} className="rounded-xl bg-surface-2 p-3">
                    <dt className="text-xs text-muted">
                      {t('nutrition.summary.remaining')} · {t(`nutrition.nutrients.${key}`)}
                    </dt>
                    <dd className={clsx('font-semibold', remaining !== null && remaining < 0 && 'text-warning')}>
                      {remaining === null
                        ? '–'
                        : remaining < 0
                          ? t('nutrition.summary.over', { value: `${fmt(-remaining, 0)} ${unit}` })
                          : `${fmt(remaining, key === 'calories' ? 0 : 1)} ${unit}`}
                    </dd>
                  </div>
                )
              })}
            </dl>
          </Card>

          <Card>
            <h2 className="mb-3 font-semibold">{t('nutrition.summary.last7')}</h2>
            {range.isPending ? (
              <Spinner label={t('common.loading')} />
            ) : range.data ? (
              <>
                <ul className="divide-y divide-line text-sm">
                  {Array.from({ length: 7 }, (_, i) => addDays(today, -i)).map((day) => {
                    const entry = range.data.days.find((d) => d.date === day)
                    return (
                      <li key={day} className="flex items-baseline justify-between gap-3 py-1.5">
                        <button type="button" className="font-medium hover:underline" onClick={() => setDate(day)}>
                          {day}
                        </button>
                        <span className="text-muted">
                          {entry ? `${fmt(entry.totals.calories)} ${t('nutrition.nutrients.kcal')} · ${t('nutrition.summary.mealsCount', { count: entry.mealCount })}` : t('nutrition.summary.noMeals')}
                        </span>
                      </li>
                    )
                  })}
                </ul>
                {range.data.daysWithData > 0 && (
                  <p className="mt-3 text-xs text-muted">
                    {t('nutrition.summary.average')}: {fmt(range.data.averagePerLoggedDay.calories)} {t('nutrition.nutrients.kcal')}. {t('nutrition.summary.averageNote')}
                  </p>
                )}
              </>
            ) : null}
          </Card>
        </div>
      )}
    </>
  )
}

const TARGET_FIELDS = ['calories', 'proteinG', 'carbohydratesG', 'fatG', 'fiberG'] as const

export function NutritionGoalsPage() {
  const { t } = useTranslation()
  const targets = useTargets()

  return (
    <>
      <PageHeader title={t('nutrition.goals.title')} subtitle={t('nutrition.goals.subtitle')} />
      <SubNav items={NUTRITION_NAV} />
      {targets.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : isConsentRequired(targets.error) ? (
        <ConsentNeeded />
      ) : targets.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <TargetsForm initial={targets.data} />
      )}
    </>
  )
}

function TargetsForm({ initial }: { initial: NutrientValues }) {
  const { t } = useTranslation()
  const save = useSetTargets()
  const [form, setForm] = useState<Record<(typeof TARGET_FIELDS)[number], string>>(
    () => Object.fromEntries(TARGET_FIELDS.map((k) => [k, initial[k] === null ? '' : String(initial[k])])) as Record<(typeof TARGET_FIELDS)[number], string>,
  )

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const parsed = Object.fromEntries(
      TARGET_FIELDS.map((k) => [k, form[k].trim() === '' ? null : Number(form[k].replace(',', '.'))]),
    ) as unknown as NutrientValues
    save.mutate(parsed)
  }

  return (
    <Card>
      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2" noValidate>
        {TARGET_FIELDS.map((key) => (
          <Field key={key} label={t(`nutrition.goals.${key}`)}>
            {(p) => <TextInput {...p} inputMode="decimal" value={form[key]} onChange={(e) => setForm({ ...form, [key]: e.target.value })} />}
          </Field>
        ))}
        <p className="text-xs text-muted sm:col-span-2">{t('nutrition.goals.hint')}</p>
        <div className="flex items-center gap-3 sm:col-span-2">
          <Button type="submit" loading={save.isPending}>
            {save.isPending ? t('common.saving') : t('common.save')}
          </Button>
          {save.isSuccess && (
            <span role="status" className="text-sm text-success">
              {t('nutrition.goals.saved')}
            </span>
          )}
          {save.isError && (
            <span role="alert" className="text-sm text-danger">
              {t('common.genericError')}
            </span>
          )}
        </div>
      </form>
    </Card>
  )
}
