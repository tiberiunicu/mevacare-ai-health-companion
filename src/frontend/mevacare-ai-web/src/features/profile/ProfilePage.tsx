import { Trash2 } from 'lucide-react'
import { useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { SubNav } from '../../components/AppShell'
import { Alert, Button, Card, Field, PageHeader, SelectInput, Spinner, TextInput } from '../../components/ui'
import { ApiError } from '../../lib/api'
import {
  ACTIVITY_LEVELS,
  BIOLOGICAL_SEX,
  GOAL_TYPES,
  type ActivityLevel,
  type BiologicalSex,
  type GoalType,
  type HealthProfile,
} from '../../lib/types'
import { SETTINGS_NAV } from './navigation'
import { isConsentRequired, useAddGoal, useDeleteGoal, useGoals, useProfile, useUpdateProfile } from './hooks'

export function ProfilePage() {
  const { t } = useTranslation()
  const profile = useProfile()

  return (
    <>
      <PageHeader title={t('profile.title')} subtitle={t('profile.subtitle')} />
      <SubNav items={SETTINGS_NAV} />
      {profile.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : isConsentRequired(profile.error) ? (
        <Alert tone="warning" title={t('profile.consentRequiredTitle')}>
          <p>{t('profile.consentRequiredBody')}</p>
          <Link to="/settings/privacy" className="mt-2 inline-block font-semibold text-primary hover:underline">
            {t('profile.consentRequiredAction')}
          </Link>
        </Alert>
      ) : profile.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <div className="space-y-6">
          <ProfileForm profile={profile.data} />
          <GoalsCard />
        </div>
      )}
    </>
  )
}

function ProfileForm({ profile }: { profile: HealthProfile }) {
  const { t } = useTranslation()
  const update = useUpdateProfile()
  const [form, setForm] = useState({
    dateOfBirth: profile.dateOfBirth ?? '',
    sex: profile.sex as BiologicalSex,
    heightCm: profile.heightCm?.toString() ?? '',
    activityLevel: profile.activityLevel as ActivityLevel,
    // A brand-new profile defaults to the browser time zone; a saved one keeps its own.
    timeZoneId: profile.exists ? profile.timeZoneId : Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  })

  const fieldError = (name: string) =>
    update.error instanceof ApiError ? update.error.fieldErrors?.[name]?.[0] : undefined

  const submit = (event: FormEvent) => {
    event.preventDefault()
    update.mutate({
      dateOfBirth: form.dateOfBirth || null,
      sex: form.sex,
      heightCm: form.heightCm === '' ? null : Number(form.heightCm),
      activityLevel: form.activityLevel,
      timeZoneId: form.timeZoneId,
    })
  }

  return (
    <Card>
      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2" noValidate>
        <Field label={t('profile.dateOfBirth')} error={fieldError('DateOfBirth')}>
          {(p) => <TextInput {...p} type="date" value={form.dateOfBirth} onChange={(e) => setForm({ ...form, dateOfBirth: e.target.value })} />}
        </Field>
        <Field label={t('profile.sex')} error={fieldError('Sex')}>
          {(p) => (
            <SelectInput {...p} value={form.sex} onChange={(e) => setForm({ ...form, sex: e.target.value as BiologicalSex })}>
              {BIOLOGICAL_SEX.map((value) => (
                <option key={value} value={value}>
                  {t(`profile.sexValues.${value}`)}
                </option>
              ))}
            </SelectInput>
          )}
        </Field>
        <Field label={t('profile.height')} error={fieldError('HeightCm')}>
          {(p) => (
            <TextInput
              {...p}
              type="number"
              inputMode="decimal"
              step="0.1"
              min="50"
              max="260"
              value={form.heightCm}
              onChange={(e) => setForm({ ...form, heightCm: e.target.value })}
            />
          )}
        </Field>
        <Field label={t('profile.activityLevel')} error={fieldError('ActivityLevel')}>
          {(p) => (
            <SelectInput {...p} value={form.activityLevel} onChange={(e) => setForm({ ...form, activityLevel: e.target.value as ActivityLevel })}>
              {ACTIVITY_LEVELS.map((value) => (
                <option key={value} value={value}>
                  {t(`profile.activityValues.${value}`)}
                </option>
              ))}
            </SelectInput>
          )}
        </Field>
        <Field label={t('profile.timeZone')} error={fieldError('TimeZoneId')}>
          {(p) => <TextInput {...p} value={form.timeZoneId} onChange={(e) => setForm({ ...form, timeZoneId: e.target.value })} />}
        </Field>
        <div className="flex items-end gap-3 sm:col-span-2">
          <Button type="submit" loading={update.isPending}>
            {update.isPending ? t('common.saving') : t('common.save')}
          </Button>
          {update.isSuccess && <span role="status" className="text-sm text-success">{t('common.saved')}</span>}
          {update.isError && !(update.error instanceof ApiError && update.error.fieldErrors) && (
            <span role="alert" className="text-sm text-danger">{t('common.genericError')}</span>
          )}
        </div>
      </form>
    </Card>
  )
}

function GoalsCard() {
  const { t } = useTranslation()
  const goals = useGoals()
  const add = useAddGoal()
  const remove = useDeleteGoal()
  const [type, setType] = useState<GoalType>('GeneralWellness')
  const [targetWeight, setTargetWeight] = useState('')
  const [targetDate, setTargetDate] = useState('')

  const needsWeight = type === 'WeightLoss' || type === 'WeightGain'
  const fieldError = (name: string) => (add.error instanceof ApiError ? add.error.fieldErrors?.[name]?.[0] : undefined)
  const limitReached = add.error instanceof ApiError && add.error.code === 'goal_limit_reached'

  const submit = (event: FormEvent) => {
    event.preventDefault()
    add.mutate(
      { type, targetWeightKg: needsWeight && targetWeight ? Number(targetWeight) : null, targetDate: targetDate || null },
      {
        onSuccess: () => {
          setTargetWeight('')
          setTargetDate('')
        },
      },
    )
  }

  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('profile.goals')}</h2>
      <p className="mb-4 text-sm text-muted">{t('profile.goalsHelp')}</p>

      {goals.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : goals.data && goals.data.length > 0 ? (
        <ul className="mb-5 divide-y divide-line rounded-xl border border-line">
          {goals.data.map((goal) => (
            <li key={goal.id} className="flex items-center justify-between gap-3 p-3">
              <div>
                <p className="font-medium">{t(`profile.goalValues.${goal.type}`)}</p>
                <p className="text-xs text-muted">
                  {[goal.targetWeightKg ? `${goal.targetWeightKg} kg` : null, goal.targetDate].filter(Boolean).join(' · ')}
                </p>
              </div>
              <button
                type="button"
                onClick={() => remove.mutate(goal.id)}
                aria-label={`${t('common.delete')}: ${t(`profile.goalValues.${goal.type}`)}`}
                className="inline-flex size-11 items-center justify-center rounded-xl text-muted hover:bg-danger-soft hover:text-danger"
              >
                <Trash2 className="size-5" aria-hidden />
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="mb-5 text-sm text-muted">{t('profile.noGoals')}</p>
      )}

      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-3" noValidate>
        <Field label={t('profile.goalType')}>
          {(p) => (
            <SelectInput {...p} value={type} onChange={(e) => setType(e.target.value as GoalType)}>
              {GOAL_TYPES.map((value) => (
                <option key={value} value={value}>
                  {t(`profile.goalValues.${value}`)}
                </option>
              ))}
            </SelectInput>
          )}
        </Field>
        {needsWeight && (
          <Field label={t('profile.targetWeight')} error={fieldError('TargetWeightKg')}>
            {(p) => (
              <TextInput {...p} type="number" inputMode="decimal" step="0.1" min="20" max="500" value={targetWeight} onChange={(e) => setTargetWeight(e.target.value)} />
            )}
          </Field>
        )}
        <Field label={`${t('profile.targetDate')} (${t('common.optional')})`} error={fieldError('TargetDate')}>
          {(p) => <TextInput {...p} type="date" value={targetDate} onChange={(e) => setTargetDate(e.target.value)} />}
        </Field>
        <div className="sm:col-span-3">
          <Button type="submit" variant="secondary" loading={add.isPending}>
            {t('profile.addGoal')}
          </Button>
          {limitReached && <span role="alert" className="ml-3 text-sm text-danger">{t('profile.goalLimit')}</span>}
        </div>
      </form>
    </Card>
  )
}
