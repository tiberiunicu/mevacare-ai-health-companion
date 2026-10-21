import { Construction } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { SubNav } from '../components/AppShell'
import { Card, PageHeader } from '../components/ui'

/** Honest placeholder for areas that are designed but not built: it never shows fabricated data. */
export function ComingSoon({
  titleKey,
  phaseKey,
  subNav,
}: {
  titleKey: string
  phaseKey: string
  subNav?: { to: string; labelKey: string }[]
}) {
  const { t } = useTranslation()
  return (
    <>
      <PageHeader title={t(titleKey)} />
      {subNav && <SubNav items={subNav} />}
      <Card className="flex flex-col items-center gap-3 py-12 text-center">
        <Construction className="size-10 text-muted" aria-hidden />
        <h2 className="text-lg font-semibold">{t('comingSoon.title')}</h2>
        <p className="max-w-md text-sm text-muted">{t('comingSoon.body')}</p>
        <p className="text-xs font-medium text-primary-strong">{t('comingSoon.phase', { phase: t(phaseKey) })}</p>
      </Card>
    </>
  )
}
