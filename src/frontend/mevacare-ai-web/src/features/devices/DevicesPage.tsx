import { RefreshCw, Unplug, Watch } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { Alert, Button, Card, PageHeader, Spinner } from '../../components/ui'
import { ApiError } from '../../lib/api'
import type { Integration, SyncJob } from '../../lib/types'
import { useConsents } from '../profile/hooks'
import { useDisconnect, useIntegrations, useStartConnect, useSync, useSyncJobs } from './hooks'

function formatWhen(iso: string | null, locale: string) {
  if (!iso) return null
  const value = new Date(iso.endsWith('Z') ? iso : `${iso}Z`)
  return value.toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' })
}

function SyncResult({ job }: { job: SyncJob }) {
  const { t } = useTranslation()
  if (job.status === 'Succeeded') {
    return (
      <Alert tone="success">
        {t('devices.syncDone', { added: job.recordsAdded, updated: job.recordsUpdated })}
      </Alert>
    )
  }

  return (
    <Alert tone="warning" title={t('devices.syncFailedTitle')}>
      {t(`devices.syncErrors.${job.errorCode ?? 'internal_error'}`, { defaultValue: t('devices.syncErrors.internal_error') })}
    </Alert>
  )
}

function IntegrationCard({ integration, consentGranted }: { integration: Integration; consentGranted: boolean }) {
  const { t, i18n } = useTranslation()
  const connect = useStartConnect()
  const sync = useSync()
  const disconnect = useDisconnect()
  const jobs = useSyncJobs(integration.provider, integration.connected)
  const [confirming, setConfirming] = useState(false)
  const [deleteData, setDeleteData] = useState(false)
  const needsReauth = integration.status === 'NeedsReauthorization'

  const startConnect = () =>
    connect.mutate(integration.provider, {
      onSuccess: (response) => window.location.assign(response.authorizationUrl),
    })

  const syncError = sync.error instanceof ApiError ? sync.error : null
  const lastSync = formatWhen(integration.lastSyncAtUtc, i18n.language)

  return (
    <Card className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-center gap-3">
          <span className="rounded-xl bg-primary-soft p-2 text-primary">
            <Watch className="size-6" aria-hidden />
          </span>
          <div>
            <h2 className="text-lg font-semibold">{integration.displayName}</h2>
            <p className="text-sm text-muted">
              {!integration.configured
                ? t('devices.state.notConfigured')
                : needsReauth
                  ? t('devices.state.needsReauth')
                  : integration.connected
                    ? t('devices.state.connected')
                    : t('devices.state.notConnected')}
            </p>
          </div>
        </div>
        {integration.configured && !integration.connected && (
          <Button onClick={startConnect} loading={connect.isPending} disabled={!consentGranted}>
            {t('devices.connect', { provider: integration.displayName })}
          </Button>
        )}
        {needsReauth && (
          <Button onClick={startConnect} loading={connect.isPending} disabled={!consentGranted}>
            {t('devices.reconnect')}
          </Button>
        )}
      </div>

      {!integration.configured && <Alert tone="info">{t('devices.notConfiguredHelp', { provider: integration.displayName })}</Alert>}
      {integration.configured && !consentGranted && !integration.connected && (
        <Alert tone="warning">
          <p>{t('devices.consentNeeded')}</p>
          <Link to="/settings/privacy" className="font-semibold text-primary hover:underline">
            {t('devices.consentAction')}
          </Link>
        </Alert>
      )}
      {connect.isError && <Alert tone="danger">{t('common.genericError')}</Alert>}

      {integration.connected && (
        <>
          <dl className="grid gap-3 text-sm sm:grid-cols-2">
            <div>
              <dt className="text-xs text-muted">{t('devices.lastSync')}</dt>
              <dd>{lastSync ?? t('devices.neverSynced')}</dd>
            </div>
            <div>
              <dt className="text-xs text-muted">{t('devices.dataShared')}</dt>
              <dd>{t('devices.dataSharedValue')}</dd>
            </div>
          </dl>

          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => sync.mutate(integration.provider)} loading={sync.isPending} disabled={needsReauth}>
              <RefreshCw className="size-4" aria-hidden />
              {sync.isPending ? t('devices.syncing') : t('devices.syncNow')}
            </Button>
            <Button variant="ghost" onClick={() => setConfirming(true)} disabled={confirming}>
              <Unplug className="size-4" aria-hidden />
              {t('devices.disconnect')}
            </Button>
          </div>

          {sync.data && <SyncResult job={sync.data} />}
          {syncError?.status === 429 && <Alert tone="info">{t('devices.syncTooSoon')}</Alert>}
          {syncError && syncError.status !== 429 && <Alert tone="danger">{t('common.genericError')}</Alert>}

          {confirming && (
            <div className="space-y-3 rounded-xl border border-line bg-surface-2 p-4">
              <p className="text-sm">{t('devices.disconnectBody', { provider: integration.displayName })}</p>
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" className="mt-1 size-4" checked={deleteData} onChange={(e) => setDeleteData(e.target.checked)} />
                {t('devices.deleteDataLabel', { provider: integration.displayName })}
              </label>
              <div className="flex gap-2">
                <Button
                  variant="danger"
                  loading={disconnect.isPending}
                  onClick={() => disconnect.mutate({ provider: integration.provider, deleteData }, { onSuccess: () => setConfirming(false) })}
                >
                  {t('devices.confirmDisconnect')}
                </Button>
                <Button variant="ghost" onClick={() => setConfirming(false)}>
                  {t('common.cancel')}
                </Button>
              </div>
              {disconnect.isError && <p role="alert" className="text-sm text-danger">{t('common.genericError')}</p>}
            </div>
          )}

          {jobs.data && jobs.data.length > 0 && (
            <details className="text-sm">
              <summary className="cursor-pointer font-medium text-primary">{t('devices.history')}</summary>
              <ul className="mt-2 divide-y divide-line rounded-xl border border-line">
                {jobs.data.map((job) => (
                  <li key={job.id} className="flex flex-wrap justify-between gap-2 px-3 py-2">
                    <span>{formatWhen(job.startedAtUtc, i18n.language)}</span>
                    <span className="text-muted">
                      {t(`devices.jobStatus.${job.status}`)}
                      {job.errorCode ? ` (${job.errorCode})` : ` · +${job.recordsAdded} / ~${job.recordsUpdated}`}
                    </span>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </Card>
  )
}

export function DevicesPage() {
  const { t } = useTranslation()
  const integrations = useIntegrations()
  const consents = useConsents()
  const sync = useSync()
  const [params, setParams] = useSearchParams()
  // Read once: the address bar is cleaned right after, but the message must stay on screen.
  const [notice] = useState(() => ({ connected: params.get('connected'), error: params.get('error') }))
  const handled = useRef(false)
  const consentGranted = consents.data?.find((c) => c.type === 'ExternalIntegrations')?.granted ?? false

  // Returning from the provider: import the first data right away, then clean the address bar so a reload does not repeat it.
  useEffect(() => {
    if (handled.current || (!notice.connected && !notice.error)) return
    handled.current = true
    if (notice.connected) sync.mutate(notice.connected)
    setParams({}, { replace: true })
  }, [notice, setParams, sync])

  return (
    <>
      <PageHeader title={t('devices.title')} subtitle={t('devices.subtitle')} />
      <div className="space-y-4">
        {notice.connected && (
          <Alert tone="success" title={t('devices.connectedTitle')}>
            {sync.isPending ? t('devices.importing') : sync.data ? <SyncResult job={sync.data} /> : t('devices.connectedBody')}
          </Alert>
        )}
        {notice.error && (
          <Alert tone="danger" title={t('devices.connectFailedTitle')}>
            {t(`devices.callbackErrors.${notice.error}`, { defaultValue: t('devices.callbackErrors.unknown') })}
          </Alert>
        )}
        {integrations.isPending ? (
          <Spinner label={t('common.loading')} />
        ) : integrations.isError ? (
          <Alert tone="danger">{t('common.genericError')}</Alert>
        ) : (
          integrations.data.map((integration) => (
            <IntegrationCard key={integration.provider} integration={integration} consentGranted={consentGranted} />
          ))
        )}
        <Card>
          <h2 className="mb-1 font-semibold">{t('devices.privacyTitle')}</h2>
          <p className="text-sm text-muted">{t('devices.privacyBody')}</p>
        </Card>
      </div>
    </>
  )
}
