import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { BarChart, LineChart } from '../../components/charts'
import type { ActivityRange, SleepRecoveryRange, WeightHistory } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ActivityCard, SleepCard, WeightCard } from './DashboardCards'
import { ActivityPage, SleepPage, WeightPage } from './HealthPages'

type Routes = Record<string, (init?: RequestInit) => Response>

/** Routes by "METHOD /path" ignoring the query string (the pages build date ranges from today). */
function routeFetch(routes: Routes) {
  const calls: { method: string; url: string; body: unknown }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = init?.method ?? 'GET'
      calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      const handler = routes[`${method} ${url.split('?')[0]}`]
      if (!handler) throw new Error(`Unexpected request: ${method} ${url}`)
      return handler(init)
    }),
  )
  return calls
}

const prefs = (unitSystem: 'Metric' | 'Imperial' = 'Metric') => () => json({ language: 'en', unitSystem, theme: 'System' })
const consentRequired = () => json({ title: 'Consent required', status: 403, code: 'consent_required' }, 403)

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

const weights = (points: WeightHistory['points']): WeightHistory => ({
  from: '2026-07-01',
  to: '2026-10-25',
  points,
  latestKilograms: points.length > 0 ? points[points.length - 1].kilograms : null,
  changeKilograms: points.length > 1 ? Math.round((points[points.length - 1].kilograms - points[0].kilograms) * 100) / 100 : null,
})

const point = (id: string, kilograms: number, localDate: string, provider = 'manual') => ({
  id,
  measuredAtUtc: `${localDate}T08:00:00`,
  localDate,
  utcOffsetMinutes: 180,
  kilograms,
  provider,
})

describe('WeightPage', () => {
  it('shows an honest empty state and no chart when nothing is stored', async () => {
    routeFetch({ 'GET /api/health-preferences': prefs(), 'GET /api/measurements/weight': () => json(weights([])) })

    renderWithProviders(<WeightPage />)

    expect(await screen.findByText('No weight entries in this period yet.')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('shows the latest value, the change over the period and a text alternative for the chart', async () => {
    routeFetch({
      'GET /api/health-preferences': prefs(),
      'GET /api/measurements/weight': () => json(weights([point('a', 72, '2026-10-01'), point('b', 70.5, '2026-10-20')])),
    })

    renderWithProviders(<WeightPage />)

    await screen.findByText('Latest')
    expect(within(screen.getByText('Latest').closest('div')!).getByText('70.5 kg')).toBeInTheDocument()
    expect(within(screen.getByText('Change in the period').closest('div')!).getByText('-1.5 kg')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: /2 entries from 2026-10-01 to 2026-10-20, between 70.5 and 72 kg/ })).toBeInTheDocument()
  })

  it('does not claim a change from a single entry', async () => {
    routeFetch({ 'GET /api/health-preferences': prefs(), 'GET /api/measurements/weight': () => json(weights([point('a', 72, '2026-10-01')])) })

    renderWithProviders(<WeightPage />)

    expect(await screen.findByText('Needs at least two entries.')).toBeInTheDocument()
  })

  it('converts what the user types in pounds to kilograms before sending it', async () => {
    const calls = routeFetch({
      'GET /api/health-preferences': prefs('Imperial'),
      'GET /api/measurements/weight': () => json(weights([])),
      'POST /api/measurements/weight': () => json(point('n', 70, '2026-10-24'), 201),
    })
    renderWithProviders(<WeightPage />)

    await userEvent.type(await screen.findByLabelText('Weight (lb)'), '154,3')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    const body = calls.find((c) => c.method === 'POST')!.body as { kilograms: number; measuredAt: string }
    expect(body.kilograms).toBeCloseTo(69.99, 2)
    expect(body.measuredAt).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/) // keeps the local UTC offset
  })

  it('rejects an implausible weight without calling the server', async () => {
    const calls = routeFetch({ 'GET /api/health-preferences': prefs(), 'GET /api/measurements/weight': () => json(weights([])) })
    renderWithProviders(<WeightPage />)

    await userEvent.type(await screen.findByLabelText('Weight (kg)'), '5')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    expect(await screen.findByText('Enter a weight between 20 and 500 kg.')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('lets the user delete their own entries but not imported ones', async () => {
    const calls = routeFetch({
      'GET /api/health-preferences': prefs(),
      'GET /api/measurements/weight': () => json(weights([point('mine', 72, '2026-10-01'), point('theirs', 71, '2026-10-02', 'scale')])),
      'DELETE /api/measurements/weight/mine': () => new Response(null, { status: 204 }),
    })
    renderWithProviders(<WeightPage />)

    await screen.findByText(/imported from Scale/)
    expect(screen.queryByRole('button', { name: 'Delete the weight entry of 2026-10-02' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Delete the weight entry of 2026-10-01' }))

    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url.endsWith('/mine'))).toBe(true))
  })

  it('asks for the health data consent when it is missing', async () => {
    routeFetch({ 'GET /api/health-preferences': prefs(), 'GET /api/measurements/weight': consentRequired })

    renderWithProviders(<WeightPage />)

    expect(await screen.findByText('Health data consent needed')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add' })).not.toBeInTheDocument()
  })
})

const sleepSession = (overrides = {}) => ({
  id: 's1', startUtc: '2026-10-23T21:00:00', endUtc: '2026-10-24T05:00:00', utcOffsetMinutes: 180, isScored: true,
  asleepMinutes: 420, inBedMinutes: 480, awakeMinutes: 60, lightMinutes: 240, deepMinutes: 80, remMinutes: 100,
  performancePercent: 91, efficiencyPercent: 92.5, consistencyPercent: 88, respiratoryRate: 15.5, disturbanceCount: 7, ...overrides,
})

const sleepRange = (days: SleepRecoveryRange['days']): SleepRecoveryRange => ({
  from: '2026-10-11',
  to: '2026-10-24',
  days,
  averages: days.length === 0 ? [] : [{ provider: 'whoop', daysWithSleep: 1, averageAsleepMinutes: 420, daysWithRecovery: 1, averageRecoveryScore: 70, averageRestingHeartRate: 50, averageHrvRmssdMilliseconds: 65 }],
})

describe('SleepPage', () => {
  it('shows the last night, the providers own recovery score with its scale note, and the averages', async () => {
    routeFetch({
      'GET /api/measurements/sleep-recovery': () =>
        json(sleepRange([{ date: '2026-10-24', provider: 'whoop', sleep: sleepSession(), napMinutes: 20, recovery: { score: 70, restingHeartRate: 50, hrvRmssdMilliseconds: 65 } }])),
    })

    renderWithProviders(<SleepPage />)

    expect(await screen.findByRole('heading', { name: 'WHOOP', level: 2 })).toBeInTheDocument()
    const lastNight = screen.getByRole('heading', { name: 'Last night' }).closest('section')!
    expect(within(lastNight).getByText('7 h 00 min')).toBeInTheDocument()
    expect(within(lastNight).getByText('91%')).toBeInTheDocument()
    expect(within(lastNight).getByText('Plus naps: 0 h 20 min')).toBeInTheDocument()
    expect(within(lastNight).getByRole('img', { name: /Deep 1 h 20 min, REM 1 h 40 min, Light 4 h 00 min, Awake 1 h 00 min/ })).toBeInTheDocument()
    const recovery = screen.getByRole('heading', { name: 'WHOOP recovery' }).closest('section')!
    expect(within(recovery).getByText('70%')).toBeInTheDocument()
    expect(within(recovery).getByText('50 bpm')).toBeInTheDocument()
    expect(within(recovery).getByText('65 ms')).toBeInTheDocument()
    expect(within(recovery).getByText(/own score on WHOOP's own scale. It is not comparable/)).toBeInTheDocument()
  })

  it('says a sleep is not scored yet instead of showing zeros', async () => {
    routeFetch({
      'GET /api/measurements/sleep-recovery': () =>
        json(sleepRange([{ date: '2026-10-24', provider: 'whoop', sleep: sleepSession({ isScored: false, asleepMinutes: null, lightMinutes: null }), napMinutes: 0, recovery: null }])),
    })

    renderWithProviders(<SleepPage />)

    expect(await screen.findByText(/WHOOP has not scored this sleep yet/)).toBeInTheDocument()
    expect(screen.queryByText('0 h 00 min')).not.toBeInTheDocument()
  })

  it('never draws data that does not exist: with nothing imported it points to connecting a device', async () => {
    routeFetch({ 'GET /api/measurements/sleep-recovery': () => json(sleepRange([])) })

    renderWithProviders(<SleepPage />)

    expect(await screen.findByText('No device data yet')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Connect a device' })).toHaveAttribute('href', '/devices')
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('shows one section per provider so scores of different providers are never mixed', async () => {
    routeFetch({
      'GET /api/measurements/sleep-recovery': () =>
        json({
          ...sleepRange([
            { date: '2026-10-24', provider: 'otherband', sleep: sleepSession({ asleepMinutes: 300 }), napMinutes: 0, recovery: { score: 30, restingHeartRate: 60, hrvRmssdMilliseconds: 40 } },
            { date: '2026-10-24', provider: 'whoop', sleep: sleepSession(), napMinutes: 0, recovery: { score: 80, restingHeartRate: 50, hrvRmssdMilliseconds: 65 } },
          ]),
          averages: [],
        }),
    })

    renderWithProviders(<SleepPage />)

    await screen.findByRole('heading', { name: 'WHOOP', level: 2 })
    expect(screen.getByRole('heading', { name: 'Otherband', level: 2 })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Otherband recovery' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'WHOOP recovery' })).toBeInTheDocument()
  })

  it('asks for the health data consent when it is missing', async () => {
    routeFetch({ 'GET /api/measurements/sleep-recovery': consentRequired })

    renderWithProviders(<SleepPage />)

    expect(await screen.findByText('Health data consent needed')).toBeInTheDocument()
  })
})

const activityRange = (days: ActivityRange['days']): ActivityRange => ({ from: '2026-10-11', to: '2026-10-24', days })

describe('ActivityPage', () => {
  it('shows strain, energy and workouts with the providers name and local time', async () => {
    routeFetch({
      'GET /api/measurements/activity': () =>
        json(
          activityRange([
            {
              date: '2026-10-24',
              provider: 'whoop',
              dayStrain: 12.5,
              caloriesBurned: 2390.1,
              workouts: [{ id: 'w1', sportName: 'trail-running', startUtc: '2026-10-24T15:00:00Z', endUtc: '2026-10-24T16:00:00Z', utcOffsetMinutes: 180, isScored: true, strain: 8.2, averageHeartRate: 123, maxHeartRate: 146, calories: 375.1, distanceMeters: 1772 }],
            },
          ]),
        ),
    })

    renderWithProviders(<ActivityPage />)

    expect(await screen.findByText('WHOOP day strain')).toBeInTheDocument()
    expect(screen.getByText('2390 kcal')).toBeInTheDocument()
    const workout = screen.getByText('trail running').closest('li')!
    expect(workout).toHaveTextContent('2026-10-24 18:00 · 1 h 00 min · strain 8.2 · 123 bpm · 375 kcal')
  })

  it('points to connecting a device when nothing was imported', async () => {
    routeFetch({ 'GET /api/measurements/activity': () => json(activityRange([])) })

    renderWithProviders(<ActivityPage />)

    expect(await screen.findByText('No device data yet')).toBeInTheDocument()
  })
})

describe('dashboard cards', () => {
  it('show real values when they exist', async () => {
    routeFetch({
      'GET /api/health-preferences': prefs(),
      'GET /api/measurements/weight': () => json(weights([point('a', 72, '2026-10-01'), point('b', 70.5, '2026-10-20')])),
      'GET /api/measurements/sleep-recovery': () =>
        json(sleepRange([{ date: '2026-10-24', provider: 'whoop', sleep: sleepSession(), napMinutes: 0, recovery: { score: 70, restingHeartRate: 50, hrvRmssdMilliseconds: 65 } }])),
      'GET /api/measurements/activity': () =>
        json(activityRange([{ date: '2026-10-24', provider: 'whoop', dayStrain: 12.5, caloriesBurned: 2390, workouts: [] }])),
    })

    renderWithProviders(
      <>
        <WeightCard />
        <SleepCard />
        <ActivityCard />
      </>,
    )

    expect(await screen.findByText(/70.5 kg/)).toBeInTheDocument()
    expect(await screen.findByText('7 h 00 min')).toBeInTheDocument()
    expect(screen.getByText('WHOOP recovery 70%')).toBeInTheDocument()
    expect(screen.getByText(/WHOOP strain 12.5/)).toBeInTheDocument()
    expect(screen.getByText('0 workouts in the last 7 days')).toBeInTheDocument()
  })

  it('say there is no data and link to the way to get it, never showing sample numbers', async () => {
    routeFetch({
      'GET /api/health-preferences': prefs(),
      'GET /api/measurements/weight': () => json(weights([])),
      'GET /api/measurements/sleep-recovery': () => json(sleepRange([])),
      'GET /api/measurements/activity': () => json(activityRange([])),
    })

    renderWithProviders(
      <>
        <WeightCard />
        <SleepCard />
        <ActivityCard />
      </>,
    )

    expect(await screen.findByText('No weight entries yet.')).toBeInTheDocument()
    expect(await screen.findByText('No sleep or recovery data yet. Connect a device to import it.')).toBeInTheDocument()
    expect(await screen.findByText('No activity data yet. Connect a device to import it.')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Connect a device' })).toHaveLength(2)
  })
})

describe('charts', () => {
  it('draws a gap, not a zero, for a day without a value, and exposes a text alternative', () => {
    renderWithProviders(
      <BarChart
        max={100}
        ariaLabel="Recovery over 3 days"
        bars={[
          { label: '1', value: 50, title: 'day 1: 50%' },
          { label: '2', value: null, title: 'day 2: –' },
          { label: '3', value: 100, title: 'day 3: 100%' },
        ]}
      />,
    )

    const chart = screen.getByRole('img', { name: 'Recovery over 3 days' })
    expect(chart.children).toHaveLength(3)
    expect(chart.children[0].querySelector('[style]')).toHaveStyle({ height: '50%' })
    expect(chart.children[1].querySelector('[style]')).toBeNull()
  })

  it('copes with one point, equal values and no points', () => {
    const { container, rerender } = renderWithProviders(<LineChart points={[]} ariaLabel="none" unit="kg" />)
    expect(container).toBeEmptyDOMElement()

    rerender(<LineChart points={[{ date: '2026-10-01', value: 70 }]} ariaLabel="one" unit="kg" />)
    expect(screen.getByRole('img', { name: 'one' }).querySelectorAll('circle')).toHaveLength(1)

    rerender(
      <LineChart
        points={[
          { date: '2026-10-01', value: 70 },
          { date: '2026-10-02', value: 70 },
        ]}
        ariaLabel="flat"
        unit="kg"
      />,
    )
    expect(screen.getByRole('img', { name: 'flat' }).querySelector('path')?.getAttribute('d')).not.toContain('NaN')
  })
})
