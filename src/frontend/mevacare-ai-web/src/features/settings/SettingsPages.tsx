import { useState } from 'react'
import type { FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { SubNav } from '../../components/AppShell'
import { Alert, Button, Card, Field, PageHeader, SelectInput, Spinner, TextInput, Toggle } from '../../components/ui'
import { api, ApiError, apiFetchRaw, toApiError } from '../../lib/api'
import { safeHref } from '../../lib/links'
import type { ConsentType, HealthPreferences, ThemePreference, UnitSystem } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { SETTINGS_NAV } from '../profile/navigation'
import { useKnowledgeSources } from '../chat/hooks'
import { useConsents, usePreferences, useSetConsent, useUpdatePreferences } from '../profile/hooks'

export function PreferencesPage() {
  const { t } = useTranslation()
  const prefs = usePreferences()
  const update = useUpdatePreferences()

  const change = (patch: Partial<HealthPreferences>) => {
    if (prefs.data) update.mutate({ ...prefs.data, ...patch })
  }

  return (
    <>
      <PageHeader title={t('preferences.title')} subtitle={t('preferences.subtitle')} />
      <SubNav items={SETTINGS_NAV} />
      {prefs.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : prefs.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <Card>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t('preferences.language')}>
              {(p) => (
                <SelectInput {...p} value={prefs.data.language} onChange={(e) => change({ language: e.target.value as 'en' | 'ro' })}>
                  <option value="en">English</option>
                  <option value="ro">Română</option>
                </SelectInput>
              )}
            </Field>
            <Field label={t('preferences.units')}>
              {(p) => (
                <SelectInput {...p} value={prefs.data.unitSystem} onChange={(e) => change({ unitSystem: e.target.value as UnitSystem })}>
                  {(['Metric', 'Imperial'] as const).map((v) => (
                    <option key={v} value={v}>
                      {t(`preferences.unitValues.${v}`)}
                    </option>
                  ))}
                </SelectInput>
              )}
            </Field>
            <Field label={t('preferences.theme')}>
              {(p) => (
                <SelectInput {...p} value={prefs.data.theme} onChange={(e) => change({ theme: e.target.value as ThemePreference })}>
                  {(['System', 'Light', 'Dark'] as const).map((v) => (
                    <option key={v} value={v}>
                      {t(`preferences.themeValues.${v}`)}
                    </option>
                  ))}
                </SelectInput>
              )}
            </Field>
          </div>
          {update.isError && <p role="alert" className="mt-3 text-sm text-danger">{t('common.genericError')}</p>}
        </Card>
      )}
    </>
  )
}

export function PrivacyPage() {
  const { t, i18n } = useTranslation()
  const consents = useConsents()
  const setConsent = useSetConsent()

  const formatDate = (iso: string) => new Date(iso).toLocaleDateString(i18n.language)

  return (
    <>
      <PageHeader title={t('privacy.title')} subtitle={t('privacy.subtitle')} />
      <SubNav items={SETTINGS_NAV} />
      {consents.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : consents.isError ? (
        <Alert tone="danger">{t('common.genericError')}</Alert>
      ) : (
        <div className="space-y-6">
          <Card className="divide-y divide-line !p-0">
            {consents.data.map((consent) => (
              <div key={consent.type} className="flex items-start justify-between gap-4 p-5">
                <div className="space-y-1">
                  <h2 className="font-semibold">{t(`privacy.types.${consent.type}.title`)}</h2>
                  <p className="text-sm text-muted">{t(`privacy.types.${consent.type}.body`)}</p>
                  <p className="text-xs text-muted">
                    {consent.granted && consent.grantedAtUtc
                      ? t('privacy.grantedOn', { date: formatDate(consent.grantedAtUtc) })
                      : consent.revokedAtUtc
                        ? t('privacy.revokedOn', { date: formatDate(consent.revokedAtUtc) })
                        : t('privacy.notGranted')}
                    {consent.policyVersion && ` · ${t('privacy.policyVersion', { version: consent.policyVersion })}`}
                  </p>
                </div>
                {consent.required ? (
                  <span className="rounded-full bg-primary-soft px-3 py-1 text-xs font-semibold text-primary-strong">{t('privacy.required')}</span>
                ) : (
                  <Toggle
                    checked={consent.granted}
                    label={t(`privacy.types.${consent.type}.title`)}
                    disabled={setConsent.isPending}
                    onChange={(granted) => setConsent.mutate({ type: consent.type as ConsentType, granted })}
                  />
                )}
              </div>
            ))}
          </Card>
          <ExportCard />
          <DeleteAccountCard />
        </div>
      )}
    </>
  )
}

function ExportCard() {
  const { t } = useTranslation()
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState(false)

  const download = async () => {
    setBusy(true)
    setFailed(false)
    try {
      const response = await apiFetchRaw('/api/account/export')
      if (!response.ok) throw await toApiError(response)
      const blob = new Blob([JSON.stringify(await response.json(), null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `mevacare-export-${new Date().toISOString().slice(0, 10)}.json`
      link.click()
      URL.revokeObjectURL(url)
    } catch {
      setFailed(true)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <h2 className="font-semibold">{t('privacy.exportTitle')}</h2>
      <p className="mb-4 mt-1 text-sm text-muted">{t('privacy.exportBody')}</p>
      <Button variant="secondary" onClick={download} loading={busy}>
        {busy ? t('privacy.exporting') : t('privacy.exportAction')}
      </Button>
      {failed && <p role="alert" className="mt-2 text-sm text-danger">{t('common.genericError')}</p>}
    </Card>
  )
}

function DeleteAccountCard() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api<void>('/api/account/delete', { method: 'POST', body: { password } })
      useAuthStore.getState().clear()
      navigate('/login', { replace: true })
    } catch (e) {
      setError(e instanceof ApiError && e.code === 'invalid_password' ? t('privacy.wrongPassword') : t('common.genericError'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card className="border-danger/40">
      <h2 className="font-semibold text-danger">{t('privacy.deleteTitle')}</h2>
      <p className="mb-4 mt-1 text-sm text-muted">{t('privacy.deleteBody')}</p>
      {open ? (
        <form onSubmit={submit} className="max-w-sm space-y-3">
          <Field label={t('privacy.deleteConfirmPassword')} error={error ?? undefined}>
            {(p) => <TextInput {...p} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}
          </Field>
          <div className="flex gap-2">
            <Button type="submit" variant="danger" loading={busy} disabled={!password}>
              {t('privacy.deleteConfirm')}
            </Button>
            <Button type="button" variant="ghost" onClick={() => { setOpen(false); setPassword(''); setError(null) }}>
              {t('common.cancel')}
            </Button>
          </div>
        </form>
      ) : (
        <Button variant="danger" onClick={() => setOpen(true)}>
          {t('privacy.deleteAction')}
        </Button>
      )}
    </Card>
  )
}

export function AiSettingsPage() {
  const { t } = useTranslation()
  const consents = useConsents()
  const setConsent = useSetConsent()
  const ai = consents.data?.find((c) => c.type === 'AiProcessing')

  return (
    <>
      <PageHeader title={t('aiSettings.title')} subtitle={t('aiSettings.subtitle')} />
      <SubNav items={SETTINGS_NAV} />
      {consents.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : (
        <div className="space-y-6">
          <Card>
            <div className="flex items-center justify-between gap-4">
              <div>
                <h2 className="font-semibold">{t('aiSettings.consentLabel')}</h2>
                {!ai?.granted && <p className="mt-1 text-sm text-muted">{t('aiSettings.consentOffHelp')}</p>}
              </div>
              <Toggle
                checked={ai?.granted ?? false}
                label={t('aiSettings.consentLabel')}
                disabled={setConsent.isPending}
                onChange={(granted) => setConsent.mutate({ type: 'AiProcessing', granted })}
              />
            </div>
          </Card>
          <Card>
            <h2 className="mb-2 font-semibold">{t('aiSettings.whatIsSentTitle')}</h2>
            <ul className="list-disc space-y-1 pl-5 text-sm text-muted">
              <li>{t('aiSettings.whatIsSent1')}</li>
              <li>{t('aiSettings.whatIsSent2')}</li>
              <li>{t('aiSettings.whatIsSent3')}</li>
              <li>{t('aiSettings.whatIsSent4')}</li>
            </ul>
          </Card>
          <Card>
            <h2 className="mb-2 font-semibold">{t('aiSettings.limitsTitle')}</h2>
            <ul className="list-disc space-y-1 pl-5 text-sm text-muted">
              <li>{t('aiSettings.limits1')}</li>
              <li>{t('aiSettings.limits2')}</li>
            </ul>
          </Card>
          <KnowledgeSourcesCard />
          <Card>
            <h2 className="mb-1 font-semibold">{t('aiSettings.providerTitle')}</h2>
            <p className="text-sm text-muted">{t('aiSettings.providerBody')}</p>
          </Card>
        </div>
      )}
    </>
  )
}

function KnowledgeSourcesCard() {
  const { t, i18n } = useTranslation()
  const sources = useKnowledgeSources()

  return (
    <Card>
      <h2 className="mb-1 font-semibold">{t('aiSettings.knowledgeTitle')}</h2>
      <p className="mb-3 text-sm text-muted">{t('aiSettings.knowledgeBody')}</p>
      {sources.isPending ? (
        <Spinner label={t('common.loading')} />
      ) : sources.data && sources.data.length > 0 ? (
        <ul className="divide-y divide-line rounded-xl border border-line text-sm">
          {sources.data.map((s) => {
            const href = safeHref(s.url)
            return (
              <li key={s.id} className="p-3">
                <p className="font-medium">
                  {href ? (
                    <a href={href} target="_blank" rel="noopener noreferrer" className="text-primary hover:underline">
                      {s.title}
                    </a>
                  ) : (
                    s.title
                  )}
                </p>
                <p className="text-xs text-muted">
                  {t('aiSettings.knowledgeMeta', {
                    publisher: s.publisher,
                    license: s.license,
                    date: new Date(s.retrievedOn).toLocaleDateString(i18n.language),
                    count: s.chunkCount,
                  })}
                </p>
              </li>
            )
          })}
        </ul>
      ) : (
        <p className="text-sm text-muted">{t('aiSettings.knowledgeEmpty')}</p>
      )}
    </Card>
  )
}
