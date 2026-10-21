import { Activity, Flame, LineChart, MoonStar, Sparkles, Target, UserRound } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { Alert, Card, PageHeader } from '../components/ui'
import { isConsentRequired, useGoals, useProfile } from '../features/profile/hooks'
import { useAuthStore } from '../stores/auth'

/**
 * The dashboard shows only values that really exist. Widgets whose data source is not built yet say so
 * explicitly instead of displaying demonstration numbers.
 */
export function Dashboard() {
  const { t } = useTranslation()
  const user = useAuthStore((s) => s.user)
  const profile = useProfile()
  const goals = useGoals()
  const consentMissing = isConsentRequired(profile.error)

  return (
    <>
      <PageHeader title={t('dashboard.greeting', { name: user?.displayName ?? '' })} subtitle={t('dashboard.subtitle')} />

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardTitle icon={<UserRound className="size-5" />} title={t('dashboard.profileTitle')} />
          {consentMissing ? (
            <Alert tone="warning">
              <p>{t('dashboard.consentNeeded')}</p>
              <Link to="/settings/privacy" className="font-semibold text-primary hover:underline">
                {t('profile.consentRequiredAction')}
              </Link>
            </Alert>
          ) : profile.data?.exists ? (
            <p className="text-sm text-muted">
              {profile.data.heightCm ? t('dashboard.profileComplete', { height: profile.data.heightCm }) : t('dashboard.profileEmpty')}
            </p>
          ) : (
            <p className="text-sm text-muted">{t('dashboard.profileEmpty')}</p>
          )}
          {!consentMissing && (
            <Link to="/settings/profile" className="mt-3 inline-block text-sm font-semibold text-primary hover:underline">
              {t('dashboard.profileAction')}
            </Link>
          )}
        </Card>

        <Card>
          <CardTitle icon={<Target className="size-5" />} title={t('dashboard.goalsTitle')} />
          {goals.data && goals.data.length > 0 ? (
            <>
              <p className="mb-2 text-sm text-muted">{t('dashboard.goalsCount', { count: goals.data.length })}</p>
              <ul className="space-y-1 text-sm">
                {goals.data.map((goal) => (
                  <li key={goal.id}>• {t(`profile.goalValues.${goal.type}`)}</li>
                ))}
              </ul>
            </>
          ) : (
            <p className="text-sm text-muted">{consentMissing ? t('dashboard.consentNeeded') : t('dashboard.goalsEmpty')}</p>
          )}
        </Card>

        <PendingCard icon={<Flame className="size-5" />} title={t('dashboard.calories')} note={t('dashboard.availableWith')} />
        <PendingCard icon={<Activity className="size-5" />} title={t('dashboard.macros')} note={t('dashboard.availableWith')} />
        <PendingCard icon={<LineChart className="size-5" />} title={t('dashboard.weightEvolution')} note={t('dashboard.availableWithMeasurements')} />
        <PendingCard icon={<MoonStar className="size-5" />} title={t('dashboard.sleepSummary')} note={t('dashboard.availableWithMeasurements')} />
        <PendingCard icon={<Activity className="size-5" />} title={t('dashboard.activitySummary')} note={t('dashboard.availableWithMeasurements')} />
        <PendingCard icon={<Sparkles className="size-5" />} title={t('dashboard.insights')} note={t('dashboard.availableWithInsights')} tone="ai" />
      </div>
    </>
  )
}

function CardTitle({ icon, title }: { icon: ReactNode; title: string }) {
  return (
    <h2 className="mb-3 flex items-center gap-2 font-semibold">
      <span className="text-primary">{icon}</span>
      {title}
    </h2>
  )
}

function PendingCard({ icon, title, note, tone }: { icon: ReactNode; title: string; note: string; tone?: 'ai' }) {
  const { t } = useTranslation()
  return (
    <Card className={tone === 'ai' ? 'bg-ai-soft' : undefined}>
      <CardTitle icon={icon} title={title} />
      <p className="text-sm font-medium text-muted">{t('dashboard.noDataYet')}</p>
      <p className="mt-1 text-xs text-muted">{note}</p>
    </Card>
  )
}
