import { Plus, Search, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Alert, Button, Field, SelectInput, TextInput } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { toOffsetIso } from '../../lib/dates'
import { MEAL_TYPES, type FoodSearchItem, type Meal, type MealType } from '../../lib/types'
import { searchFoods, useSaveMeal } from './hooks'
import {
  NUTRIENT_KEYS,
  blankItem,
  buildRequest,
  isItemValid,
  itemFromFood,
  parseGrams,
  previewNutrients,
  sumPreview,
  type EditorItem,
} from './mealEditorModel'

const fmt = (value: number | null | undefined, digits = 0) => (value === null || value === undefined ? '–' : value.toFixed(digits))

export interface MealEditorProps {
  /** Existing meal to edit. Omit to create. */
  meal?: Meal
  initialType?: MealType
  initialItems?: EditorItem[]
  initialTime?: Date
  /** When set, the meal is created from an assistant proposal (confirming it). */
  proposalId?: string | null
  submitLabelKey?: string
  onSaved: (meal: Meal) => void
  onCancel: () => void
}

/** Create/edit a meal. Used by the journal and to confirm or correct a meal prepared by the assistant. */
export function MealEditor({ meal, initialType, initialItems, initialTime, proposalId = null, submitLabelKey, onSaved, onCancel }: MealEditorProps) {
  const { t } = useTranslation()
  const save = useSaveMeal()
  const [type, setType] = useState<MealType>(meal?.mealType ?? initialType ?? 'Lunch')
  const [when, setWhen] = useState(() => {
    const base = meal ? new Date(meal.eatenAtUtc) : (initialTime ?? new Date())
    return toLocalInput(base)
  })
  const [notes, setNotes] = useState(meal?.notes ?? '')
  const [items, setItems] = useState<EditorItem[]>(initialItems ?? [])
  const [picking, setPicking] = useState<string | 'new' | null>(initialItems === undefined && !meal ? 'new' : null)

  const update = (key: string, patch: Partial<EditorItem>) => setItems((current) => current.map((i) => (i.key === key ? { ...i, ...patch } : i)))
  const total = sumPreview(items)
  const canSave = items.length > 0 && items.every(isItemValid) && !save.isPending

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!canSave) return
    const eatenAt = toOffsetIso(new Date(when))
    save.mutate({ id: meal?.id, request: buildRequest(type, eatenAt, notes, items, proposalId) }, { onSuccess: onSaved })
  }

  const pick = (food: FoodSearchItem) => {
    if (picking === 'new') {
      setItems((current) => [...current, itemFromFood(food)])
    } else if (picking) {
      setItems((current) => current.map((i) => (i.key === picking ? { ...itemFromFood(food, i.grams), key: i.key } : i)))
    }
    setPicking(null)
  }

  return (
    <form onSubmit={submit} className="space-y-4" noValidate>
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label={t('nutrition.editor.mealType')}>
          {(p) => (
            <SelectInput {...p} value={type} onChange={(e) => setType(e.target.value as MealType)}>
              {MEAL_TYPES.map((m) => (
                <option key={m} value={m}>
                  {t(`nutrition.mealTypes.${m}`)}
                </option>
              ))}
            </SelectInput>
          )}
        </Field>
        <Field label={t('nutrition.editor.time')}>
          {(p) => <TextInput {...p} type="datetime-local" value={when} onChange={(e) => setWhen(e.target.value)} />}
        </Field>
        <Field label={`${t('nutrition.editor.notes')} (${t('common.optional')})`}>
          {(p) => <TextInput {...p} maxLength={500} value={notes} onChange={(e) => setNotes(e.target.value)} />}
        </Field>
      </div>

      <div className="space-y-3">
        <h3 className="text-sm font-semibold">{t('nutrition.editor.items')}</h3>
        {items.map((item) => (
          <ItemRow
            key={item.key}
            item={item}
            onChange={(patch) => update(item.key, patch)}
            onRemove={() => setItems((current) => current.filter((i) => i.key !== item.key))}
            onChangeFood={() => setPicking(item.key)}
          />
        ))}

        {picking ? (
          <FoodPicker onPick={pick} onCancel={() => setPicking(null)} />
        ) : (
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="secondary" onClick={() => setPicking('new')}>
              <Plus className="size-4" aria-hidden />
              {t('nutrition.editor.addFood')}
            </Button>
            <Button type="button" variant="ghost" onClick={() => setItems((current) => [...current, blankItem('custom')])}>
              {t('nutrition.editor.addCustom')}
            </Button>
          </div>
        )}
      </div>

      <div className="rounded-xl bg-surface-2 p-3 text-sm">
        <p className="font-semibold">
          {t('nutrition.editor.total')}: {fmt(total.calories)} {t('nutrition.nutrients.kcal')} · {t('nutrition.nutrients.proteinG')} {fmt(total.proteinG, 1)} g ·{' '}
          {t('nutrition.nutrients.carbohydratesG')} {fmt(total.carbohydratesG, 1)} g · {t('nutrition.nutrients.fatG')} {fmt(total.fatG, 1)} g ·{' '}
          {t('nutrition.nutrients.fiberG')} {fmt(total.fiberG, 1)} g
        </p>
        <p className="text-xs text-muted">
          {t('nutrition.editor.previewNote')} {total.incomplete && items.length > 0 ? t('nutrition.editor.incompletePreview') : ''}
        </p>
      </div>

      {save.isError && (
        <Alert tone="danger">
          {save.error instanceof ApiError && save.error.status < 500 && save.error.fieldErrors
            ? Object.values(save.error.fieldErrors).flat().join(' ')
            : save.error instanceof ApiError && save.error.code === 'proposal_not_open'
              ? save.error.message
              : t('nutrition.editor.saveError')}
        </Alert>
      )}

      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={!canSave} loading={save.isPending}>
          {save.isPending ? t('nutrition.editor.saving') : t(submitLabelKey ?? 'nutrition.editor.save')}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          {t('nutrition.editor.cancel')}
        </Button>
        {items.length === 0 && <span className="self-center text-xs text-muted">{t('nutrition.editor.atLeastOne')}</span>}
      </div>
    </form>
  )
}

function toLocalInput(date: Date): string {
  return toOffsetIso(date).slice(0, 16)
}

function ItemRow({
  item, onChange, onRemove, onChangeFood,
}: {
  item: EditorItem
  onChange: (patch: Partial<EditorItem>) => void
  onRemove: () => void
  onChangeFood: () => void
}) {
  const { t } = useTranslation()
  const preview = previewNutrients(item)
  const gramsInvalid = item.kind !== 'unresolved' && item.grams !== '' && parseGrams(item.grams) === null

  return (
    <div className="rounded-xl border border-line p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          {item.kind === 'custom' ? (
            <TextInput aria-label={t('nutrition.editor.name')} placeholder={t('nutrition.editor.name')} maxLength={300} value={item.name} onChange={(e) => onChange({ name: e.target.value })} />
          ) : (
            <p className="break-words text-sm font-medium">{item.name}</p>
          )}
          {item.sourceLabel && item.kind === 'food' && <p className="text-xs text-muted">{item.sourceLabel}</p>}
        </div>
        <button
          type="button"
          onClick={onRemove}
          aria-label={`${t('nutrition.editor.remove')}: ${item.name || t('nutrition.editor.addCustom')}`}
          className="inline-flex size-10 shrink-0 items-center justify-center rounded-xl text-muted hover:bg-danger-soft hover:text-danger"
        >
          <Trash2 className="size-4" aria-hidden />
        </button>
      </div>

      {item.kind === 'unresolved' ? (
        <div className="mt-2 space-y-2">
          <p className="text-sm text-warning">{item.note ?? t('nutrition.editor.unresolved')}</p>
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="secondary" onClick={onChangeFood}>
              <Search className="size-4" aria-hidden />
              {t('nutrition.editor.change')}
            </Button>
            <Button type="button" variant="ghost" onClick={() => onChange({ kind: 'custom' })}>
              {t('nutrition.editor.addCustom')}
            </Button>
          </div>
        </div>
      ) : (
        <div className="mt-2 grid gap-3 sm:grid-cols-[8rem_1fr]">
          <Field label={t('nutrition.editor.grams')} error={gramsInvalid ? t('nutrition.editor.needsGrams') : undefined}>
            {(p) => <TextInput {...p} inputMode="decimal" value={item.grams} onChange={(e) => onChange({ grams: e.target.value })} />}
          </Field>
          <div className="space-y-1 text-xs text-muted">
            {item.kind === 'food' && (
              <>
                <p>
                  {t(`nutrition.prepValues.${item.preparation}`)}
                  {preview && ` · ${fmt(preview.calories)} ${t('nutrition.nutrients.kcal')} · ${t('nutrition.nutrients.proteinG')} ${fmt(preview.proteinG, 1)} g`}
                </p>
                {item.note && <p className="text-warning">{item.note}</p>}
                {item.grams === '' && <p className="text-warning">{t('nutrition.editor.needsGrams')}</p>}
                {item.preparationAssumed && (
                  <p className="text-warning">{t('nutrition.editor.assumedPrep', { state: t(`nutrition.prepValues.${item.preparation}`).toLowerCase() })}</p>
                )}
                {item.ambiguous && <p className="text-warning">{t('nutrition.editor.ambiguous')}</p>}
                <button type="button" onClick={onChangeFood} className="font-semibold text-primary hover:underline">
                  {t('nutrition.editor.change')}
                </button>
              </>
            )}
            {item.kind === 'custom' && (
              <div className="grid grid-cols-2 gap-2 sm:grid-cols-5">
                {NUTRIENT_KEYS.map((key) => (
                  <label key={key} className="block">
                    <span className="mb-0.5 block">{t(`nutrition.nutrients.${key}`)}</span>
                    <TextInput
                      inputMode="decimal"
                      aria-label={`${t(`nutrition.nutrients.${key}`)} (${item.name || t('nutrition.editor.addCustom')})`}
                      value={item.estimated[key]}
                      onChange={(e) => onChange({ estimated: { ...item.estimated, [key]: e.target.value } })}
                    />
                  </label>
                ))}
                <p className="col-span-full">{t('nutrition.editor.customHint')}</p>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

/** Debounced search in the verified food database. Nothing is created from the search text itself. */
export function FoodPicker({ onPick, onCancel }: { onPick: (food: FoodSearchItem) => void; onCancel: () => void }) {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<FoodSearchItem[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)
  const latest = useRef(0)
  // Results belong to the query that produced them; too-short text shows none.
  const shown = query.trim().length >= 2 ? results : null

  useEffect(() => {
    if (query.trim().length < 2) return
    const ticket = ++latest.current
    const controller = new AbortController()
    const handle = setTimeout(async () => {
      setBusy(true)
      setFailed(false)
      try {
        const found = await searchFoods(query.trim(), controller.signal)
        if (ticket === latest.current) setResults(found)
      } catch {
        if (ticket === latest.current && !controller.signal.aborted) setFailed(true)
      } finally {
        if (ticket === latest.current) setBusy(false)
      }
    }, 250)
    return () => {
      clearTimeout(handle)
      controller.abort()
    }
  }, [query])

  return (
    <div className="space-y-2 rounded-xl border border-primary/40 bg-primary-soft/40 p-3">
      <Field label={t('nutrition.editor.searchLabel')}>
        {(p) => (
          <TextInput {...p} autoFocus value={query} placeholder={t('nutrition.editor.searchPlaceholder')} onChange={(e) => setQuery(e.target.value)} />
        )}
      </Field>
      {busy && <p className="text-xs text-muted">{t('nutrition.editor.searching')}</p>}
      {failed && <p role="alert" className="text-xs text-danger">{t('common.genericError')}</p>}
      {shown && shown.length === 0 && !busy && <p className="text-sm text-muted">{t('nutrition.editor.noResults')}</p>}
      {shown && shown.length > 0 && (
        <ul className="divide-y divide-line rounded-xl border border-line bg-surface">
          {shown.map((food) => (
            <li key={food.foodId}>
              <button type="button" onClick={() => onPick(food)} className="block min-h-11 w-full px-3 py-2 text-left hover:bg-surface-2">
                <span className="block text-sm font-medium">{food.description}</span>
                <span className="block text-xs text-muted">
                  {t(`nutrition.prepValues.${food.preparation}`)} · {fmt(food.per100g.calories)} {t('nutrition.nutrients.kcal')} {t('nutrition.editor.per100')} ·{' '}
                  {food.sourceLabel}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      <Button type="button" variant="ghost" onClick={onCancel}>
        {t('nutrition.editor.cancel')}
      </Button>
    </div>
  )
}
