import { useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, ClipboardCheck } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '../../components/ui'
import { ApiError } from '../../lib/api'
import type { ActionCard as ActionCardData, NutrientValues } from '../../lib/types'
import { confirmAction, dismissAction } from './hooks'

const NUTRIENT_FIELDS = ['calories', 'proteinG', 'carbohydratesG', 'fatG', 'fiberG'] as const
const UNIT: Record<(typeof NUTRIENT_FIELDS)[number], string> = { calories: 'kcal', proteinG: 'g', carbohydratesG: 'g', fatG: 'g', fiberG: 'g' }

const show = (value: number | null | undefined, unit = '') => (value === null || value === undefined ? '–' : `${value}${unit ? ` ${unit}` : ''}`)

/**
 * A change prepared by the assistant (weight, nutrition targets, profile, deleting a meal). It is a PROPOSAL: nothing changes until the
 * user presses the button, and what is applied is exactly what this card shows (the server stored it when the card was created).
 */
export function ActionCard({ card, onChange }: { card: ActionCardData; onChange: (patch: Partial<ActionCardData>) => void }) {
  const { t } = useTranslation()
  const queryClient = useQueryClient()
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const destructive = card.kind === 'ProposeMealDeletion'

  const confirm = async () => {
    setPending(true)
    setError(null)
    try {
      await confirmAction(card.id)
      onChange({ state: 'Committed' })
      // Every page that shows this data (journal, weight, goals, profile, dashboard) must show the new state.
      await queryClient.invalidateQueries()
    } catch (e) {
      setError(
        e instanceof ApiError && e.code === 'consent_required'
          ? t('action.consentError')
          : e instanceof ApiError && e.code === 'proposal_not_open'
            ? e.message
            : t('action.confirmError'),
      )
    } finally {
      setPending(false)
    }
  }

  const dismiss = async () => {
    setPending(true)
    setError(null)
    try {
      await dismissAction(card.id)
      onChange({ state: 'Dismissed' })
    } catch {
      setError(t('common.genericError'))
    } finally {
      setPending(false)
    }
  }

  return (
    <section aria-label={t(`action.title.${card.kind}`)} className="mt-3 rounded-xl border border-ai/40 bg-surface p-3 text-left text-sm">
      <h3 className="mb-2 flex items-center gap-2 font-semibold">
        <ClipboardCheck className="size-4 text-ai" aria-hidden />
        {t(`action.title.${card.kind}`)}
      </h3>

      <Details card={card} />

      {card.state === 'Committed' && (
        <p className="mt-3 flex items-center gap-2 text-success" role="status">
          <CheckCircle2 className="size-4" aria-hidden />
          {t(`action.done.${card.kind}`)}
        </p>
      )}
      {card.state === 'Dismissed' && <p className="mt-3 text-muted">{t('action.dismissed')}</p>}

      {card.state === 'Proposed' && (
        <div className="mt-3 space-y-2">
          <p className="text-xs text-muted">{t('action.notApplied')}</p>
          <div className="flex flex-wrap gap-2">
            <Button variant={destructive ? 'danger' : 'primary'} onClick={() => void confirm()} loading={pending}>
              {t(`action.confirm.${card.kind}`)}
            </Button>
            <Button variant="ghost" onClick={() => void dismiss()} disabled={pending}>
              {t('proposal.dismiss')}
            </Button>
          </div>
          {error && (
            <p role="alert" className="text-xs text-danger">
              {error}
            </p>
          )}
        </div>
      )}
    </section>
  )
}

function Details({ card }: { card: ActionCardData }) {
  const { t } = useTranslation()

  switch (card.kind) {
    case 'ProposeWeight':
      return (
        <p className="font-medium">
          {card.payload.kilograms} kg · {card.payload.localDate}
        </p>
      )

    case 'ProposeNutritionTargets': {
      const { before, after } = card.payload
      const changed = NUTRIENT_FIELDS.filter((key) => before[key] !== after[key])
      return (
        <ul className="space-y-1">
          {changed.map((key) => (
            <li key={key}>
              <span className="font-medium">{t(`nutrition.nutrients.${key}`)}</span>: {show(before[key as keyof NutrientValues], UNIT[key])} →{' '}
              <span className="font-semibold">{show(after[key as keyof NutrientValues], UNIT[key])}</span>
            </li>
          ))}
        </ul>
      )
    }

    case 'ProposeProfileUpdate': {
      const { before, after } = card.payload
      return (
        <ul className="space-y-1">
          {before.heightCm !== after.heightCm && (
            <li>
              <span className="font-medium">{t('profile.height')}</span>: {show(before.heightCm)} → <span className="font-semibold">{show(after.heightCm)}</span>
            </li>
          )}
          {before.activityLevel !== after.activityLevel && (
            <li>
              <span className="font-medium">{t('profile.activityLevel')}</span>: {t(`profile.activityValues.${before.activityLevel}`, { defaultValue: before.activityLevel })} →{' '}
              <span className="font-semibold">{t(`profile.activityValues.${after.activityLevel}`, { defaultValue: after.activityLevel })}</span>
            </li>
          )}
        </ul>
      )
    }

    case 'ProposeMealDeletion': {
      const { mealType, localDate, items, calories } = card.payload
      return (
        <>
          <p className="font-medium">
            {t(`nutrition.mealTypes.${mealType}`, { defaultValue: mealType })} · {localDate} · {Math.round(calories)} {t('nutrition.nutrients.kcal')}
          </p>
          <ul className="list-disc pl-5 text-muted">
            {items.map((name) => (
              <li key={name} className="break-words">
                {name}
              </li>
            ))}
          </ul>
        </>
      )
    }
  }
}
