import { CheckCircle2, ClipboardList } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { Button } from '../../components/ui'
import { ApiError } from '../../lib/api'
import { toOffsetIso } from '../../lib/dates'
import type { ProposalCard as ProposalCardData } from '../../lib/types'
import { useSaveMeal } from '../nutrition/hooks'
import { MealEditor } from '../nutrition/MealEditor'
import { buildRequest, isProposalReady, itemsFromProposal, proposalEatenAt, proposalMealType } from '../nutrition/mealEditorModel'
import { dismissProposal } from './hooks'

const fmt = (value: number | null | undefined, digits = 0) => (value === null || value === undefined ? '–' : value.toFixed(digits))

/**
 * A meal prepared by the assistant. It is a PROPOSAL: nothing is stored until the user reviews it (and may correct
 * foods and weights) and confirms. The numbers shown come from the food database, not from the language model.
 */
export function ProposalCard({ card, onChange }: { card: ProposalCardData; onChange: (patch: Partial<ProposalCardData>) => void }) {
  const { t } = useTranslation()
  const [reviewing, setReviewing] = useState(false)
  const [failed, setFailed] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const save = useSaveMeal()
  const { proposal } = card
  const eatenAt = proposalEatenAt(proposal)
  const ready = isProposalReady(proposal)

  /** One tap: saves exactly what the card shows (the server recalculates and checks it again). */
  const saveNow = () => {
    setSaveError(null)
    const request = buildRequest(proposalMealType(proposal, eatenAt), toOffsetIso(eatenAt), '', itemsFromProposal(proposal), card.id)
    save.mutate(
      { request },
      {
        onSuccess: (meal) => onChange({ state: 'Committed', mealId: meal.id }),
        onError: (error) => setSaveError(error instanceof ApiError && error.code === 'proposal_not_open' ? error.message : t('proposal.confirmError')),
      },
    )
  }

  const dismiss = async () => {
    setFailed(false)
    try {
      await dismissProposal(card.id)
      onChange({ state: 'Dismissed' })
    } catch {
      setFailed(true)
    }
  }

  return (
    <section aria-label={t('proposal.title')} className="mt-3 rounded-xl border border-ai/40 bg-surface p-3 text-left text-sm">
      <h3 className="mb-2 flex items-center gap-2 font-semibold">
        <ClipboardList className="size-4 text-ai" aria-hidden />
        {t('proposal.title')}
      </h3>

      <ul className="divide-y divide-line">
        {proposal.items.map((item) => (
          <li key={item.clientItemId} className="py-1.5">
            {item.food ? (
              <>
                <p className="break-words font-medium">{item.food.description}</p>
                <p className="text-xs text-muted">
                  {item.quantityGrams !== null
                    ? `${fmt(item.quantityGrams, 0)} g · ${fmt(item.nutrients.calories)} ${t('nutrition.nutrients.kcal')}`
                    : t('proposal.needsQuantity')}
                </p>
              </>
            ) : (
              <>
                <p className="break-words font-medium">{item.inputText}</p>
                <p className="text-xs text-warning">{t('proposal.unmatched')}</p>
              </>
            )}
          </li>
        ))}
      </ul>

      <p className="mt-2 font-semibold">
        {t('proposal.total')}: {fmt(proposal.totals.calories)} {t('nutrition.nutrients.kcal')} · {t('nutrition.nutrients.proteinG')} {fmt(proposal.totals.proteinG, 1)} g ·{' '}
        {t('nutrition.nutrients.carbohydratesG')} {fmt(proposal.totals.carbohydratesG, 1)} g · {t('nutrition.nutrients.fatG')} {fmt(proposal.totals.fatG, 1)} g
      </p>

      {proposal.warnings.length > 0 && card.state === 'Proposed' && (
        <div className="mt-2 rounded-lg bg-warning-soft p-2 text-xs">
          <p className="font-semibold">{t('proposal.warnings')}</p>
          <ul className="list-disc pl-4">
            {proposal.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </div>
      )}

      {card.state === 'Committed' && (
        <p className="mt-3 flex flex-wrap items-center gap-2 text-success" role="status">
          <CheckCircle2 className="size-4" aria-hidden />
          {t('proposal.saved')}
          <Link to="/nutrition/journal" className="font-semibold underline">
            {t('proposal.openJournal')}
          </Link>
        </p>
      )}
      {card.state === 'Dismissed' && <p className="mt-3 text-muted">{t('proposal.dismissed')}</p>}

      {card.state === 'Proposed' && !reviewing && (
        <div className="mt-3 space-y-2">
          <p className="text-xs text-muted">{t('proposal.notSaved')}</p>
          {!ready && <p className="text-xs text-warning">{t('proposal.fixFirst')}</p>}
          {proposal.localDate && <p className="text-xs text-muted">{t('proposal.forDay', { date: proposal.localDate })}</p>}
          <div className="flex flex-wrap gap-2">
            {ready && (
              <Button onClick={saveNow} disabled={save.isPending}>
                {t('proposal.save')}
              </Button>
            )}
            <Button variant={ready ? 'secondary' : 'primary'} onClick={() => setReviewing(true)}>
              {t(ready ? 'proposal.edit' : 'proposal.review')}
            </Button>
            <Button variant="ghost" onClick={() => void dismiss()}>
              {t('proposal.dismiss')}
            </Button>
          </div>
          {saveError && (
            <p role="alert" className="text-xs text-danger">
              {saveError}
            </p>
          )}
          {failed && (
            <p role="alert" className="text-xs text-danger">
              {t('common.genericError')}
            </p>
          )}
        </div>
      )}

      {card.state === 'Proposed' && reviewing && (
        <div className="mt-3 border-t border-line pt-3">
          <MealEditor
            proposalId={card.id}
            initialItems={itemsFromProposal(proposal)}
            initialType={proposalMealType(proposal, eatenAt)}
            initialTime={eatenAt}
            submitLabelKey="nutrition.editor.confirm"
            onSaved={(meal) => {
              setReviewing(false)
              onChange({ state: 'Committed', mealId: meal.id })
            }}
            onCancel={() => setReviewing(false)}
          />
        </div>
      )}
    </section>
  )
}
