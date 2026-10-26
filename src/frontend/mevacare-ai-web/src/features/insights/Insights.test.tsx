import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import i18n from '../../i18n'
import type { HealthInsights, Insight, PeriodComparison, Trend } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { InsightsCard } from './InsightsCard'
import { InsightsPage } from './InsightsPage'

type Routes = Record<string, (init?: RequestInit) => Response>

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

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(async () => {
  useAuthStore.getState().clear()
  await i18n.changeLanguage('en')
})

const fact = (key: string, value: number | string, unit: string | null = null) =>
  typeof value === 'number' ? { key, number: value, text: null, unit } : { key, number: null, text: value, unit }

const weightShift: Insight = {
  code: 'weight_trend_shift',
  category: 'combined',
  severity: 'notable',
  statement: 'Your weight trend slowed (English fallback).',
  facts: [
    fact('shift', 'slowed'),
    fact('previous_change_per_week', -0.6, 'kg'),
    fact('current_change_per_week', -0.1, 'kg'),
    fact('calories_average_change', 250, 'kcal'),
    fact('sleep_average_change', -50, 'min'),
    fact('sleep_provider', 'whoop'),
    fact('factors_count', 2),
  ],
  caveats: ['association_not_cause', 'weight_varies_daily', 'logging_gaps'],
}

const caloriesVsTarget: Insight = {
  code: 'calories_vs_target',
  category: 'nutrition',
  severity: 'info',
  statement: 'x',
  facts: [fact('average', 1950, 'kcal'), fact('target', 2100, 'kcal'), fact('deviation_percent', -7.1, '%'), fact('logged_days', 12)],
  caveats: [],
}

const weightTrend: Trend = {
  metric: 'weight_kg', unit: 'kg', provider: null, direction: 'decreasing', confidence: 'moderate', points: 14, spanDays: 28,
  average: 82.1, first: 83, last: 81.2, changePerWeek: -0.43, rSquared: 0.8, note: null,
}

const sleepComparison: PeriodComparison = {
  metric: 'sleep_minutes', unit: 'min', provider: 'whoop', currentDays: 28, previousDays: 30, currentAverage: 380, previousAverage: 430,
  change: -50, changePercent: -11.6, direction: 'decreasing', meaningful: true,
}

const insights = (overrides: Partial<HealthInsights> = {}): HealthInsights => ({
  from: '2026-10-01',
  to: '2026-10-30',
  days: 30,
  coverage: { weightEntries: 14, nutritionLoggedDays: 12, nutritionWindowDays: 30, providers: [{ provider: 'whoop', daysWithSleep: 28, daysWithRecovery: 27 }] },
  trends: [weightTrend],
  comparisons: [
    sleepComparison,
    { ...sleepComparison, metric: 'calories_kcal', provider: null, unit: 'kcal', currentAverage: 2050, previousAverage: 2040, change: 10, direction: 'stable', meaningful: false },
    { ...sleepComparison, metric: 'protein_g', provider: null, unit: 'g', currentDays: 2, currentAverage: null, change: null, direction: 'insufficient_data', meaningful: false },
  ],
  insights: [weightShift, caloriesVsTarget],
  limitations: ['observational_only', 'not_medical_advice', 'provider_scores_not_comparable'],
  ...overrides,
})

const routes = (data: HealthInsights = insights(), extra: Routes = {}): Routes => ({ 'GET /api/insights': () => json(data), ...extra })

describe('InsightsPage', () => {
  it('phrases insights from their facts, with the other changes as separate sentences and the caveats stated', async () => {
    routeFetch(routes())

    renderWithProviders(<InsightsPage />)

    const card = (await screen.findByText(/Your weight trend slowed down: about -0.6 kg per week/)).closest('li')!
    expect(within(card).getByText('Worth a look')).toBeInTheDocument()
    expect(card).toHaveTextContent('-0.1 kg per week in this one')
    expect(within(card).getByText(/average logged calories changed by 250 kcal per logged day/)).toBeInTheDocument()
    expect(within(card).getByText(/average time asleep \(WHOOP\) changed by -50 minutes/)).toBeInTheDocument()
    expect(within(card).getByText(/shows an association, not a cause/)).toBeInTheDocument()
    expect(within(card).getByText(/fewer than half of the days/)).toBeInTheDocument()
  })

  it('states a target deviation in words with the right direction', async () => {
    routeFetch(routes())

    renderWithProviders(<InsightsPage />)

    expect(
      await screen.findByText('Your average calories on logged days is 1,950 kcal: 7.1% below your target of 2,100 kcal (12 logged days).'),
    ).toBeInTheDocument()
  })

  it('shows what the data is based on, the trends with their confidence and the comparison table', async () => {
    routeFetch(routes())

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText(/14 weight entries, meals logged on 12 of 30 days/)).toBeInTheDocument()
    expect(screen.getByText(/WHOOP: sleep on 28 days, recovery on 27 days/)).toBeInTheDocument()
    const trend = screen.getAllByRole('listitem').find((li) => li.textContent?.includes('decreasing 0.43 kg/week'))!
    expect(trend).toHaveTextContent('decreasing 0.43 kg/week')
    expect(trend).toHaveTextContent('moderate confidence')

    const table = screen.getByRole('table')
    const sleepRow = within(table).getByRole('row', { name: /Time asleep · WHOOP/ })
    expect(sleepRow).toHaveTextContent('430 min')
    expect(sleepRow).toHaveTextContent('380 min')
    expect(sleepRow).toHaveTextContent('-50 min')
    expect(within(table).getByRole('row', { name: /Calories/ })).toHaveTextContent('within normal variation')
    expect(within(table).getByRole('row', { name: /Protein/ })).toHaveTextContent('needs 3+ days in both periods')
  })

  it('lists the limitations including that provider scores are never compared across devices', async () => {
    routeFetch(routes())

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText(/never compared with other devices/)).toBeInTheDocument()
    expect(screen.getByText(/not medical advice/, { selector: 'li' })).toBeInTheDocument()
  })

  it('writes the same insights in Romanian with the locales number format', async () => {
    await i18n.changeLanguage('ro')
    routeFetch(routes(insights({ insights: [weightShift] })))

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText(/Tendința greutății tale a încetinit: aproximativ -0,6 kg pe săptămână/)).toBeInTheDocument()
    expect(screen.getByText(/s-a schimbat cu 250 kcal pe zi înregistrată/)).toBeInTheDocument()
  })

  it('falls back to the servers English statement for an insight code it does not know yet', async () => {
    routeFetch(routes(insights({ insights: [{ code: 'brand_new_rule', category: 'combined', severity: 'info', statement: 'A sentence from the server.', facts: [], caveats: [] }] })))

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText('A sentence from the server.')).toBeInTheDocument()
  })

  it('says what is missing and draws no trends or comparisons when there is nothing to analyse', async () => {
    routeFetch(
      routes(
        insights({
          coverage: { weightEntries: 0, nutritionLoggedDays: 0, nutritionWindowDays: 30, providers: [] },
          trends: [],
          comparisons: [],
          insights: [{ code: 'data_gaps', category: 'data_quality', severity: 'info', statement: 's', facts: [fact('weight_entries', 0), fact('nutrition_logged_days', 0), fact('device_data', 'none')], caveats: [] }],
          limitations: ['observational_only', 'not_medical_advice'],
        }),
      ),
    )

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText('Some trends cannot be calculated yet. Missing: weight entries, logged meals, device data (sleep, recovery).')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByText('Trends in this period')).not.toBeInTheDocument()
  })

  it('asks for the health data consent when it is missing', async () => {
    routeFetch({ 'GET /api/insights': () => json({ title: 'Consent required', status: 403, code: 'consent_required' }, 403) })

    renderWithProviders(<InsightsPage />)

    expect(await screen.findByText('Health data consent needed')).toBeInTheDocument()
  })

  it('reloads with the chosen period', async () => {
    const calls = routeFetch(routes())
    renderWithProviders(<InsightsPage />)
    await screen.findByText(/What this is based on/)

    await userEvent.selectOptions(screen.getByLabelText('Period'), '90')

    await waitFor(() => expect(calls.some((c) => /days=90/.test(c.url))).toBe(true))
  })
})

describe('the AI explanation', () => {
  const explain = (body: unknown, status = 200) => ({ 'POST /api/assistant/insights/explain': () => json(body, status) })

  it('is requested on demand with the period and the interface language, and shown with its disclaimer', async () => {
    const calls = routeFetch(routes(insights(), explain({ explanation: 'Your weight loss slowed down.', ungroundedNumbers: [], model: 'm', generatedAtUtc: '2026-10-30T10:00:00Z' })))
    renderWithProviders(<InsightsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Explain with AI' }))

    expect(await screen.findByText('Your weight loss slowed down.')).toBeInTheDocument()
    expect(screen.getByText(/Wellness information, not medical advice/)).toBeInTheDocument()
    expect(screen.queryByText('Check these numbers')).not.toBeInTheDocument()
    const request = calls.find((c) => c.method === 'POST')!.body as { days: number; language: string; to: string }
    expect({ days: request.days, language: request.language }).toEqual({ days: 30, language: 'en' })
    expect(request.to).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })

  it('warns when the text contains numbers that are not in the data', async () => {
    routeFetch(routes(insights(), explain({ explanation: 'You lost 9.7 kg.', ungroundedNumbers: ['9.7'], model: 'm', generatedAtUtc: '2026-10-30T10:00:00Z' })))
    renderWithProviders(<InsightsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Explain with AI' }))

    expect(await screen.findByText('Check these numbers')).toBeInTheDocument()
    expect(screen.getByText(/could not be matched to your data: 9.7/)).toBeInTheDocument()
  })

  it.each([
    ['ai_not_configured', /not set up on this server/],
    ['ai_unavailable', /not available right now/],
    ['nothing_to_explain', /not enough recorded data/],
  ])('explains a %s failure without hiding the insights', async (code, message) => {
    routeFetch(routes(insights(), explain({ title: 'x', status: 503, code }, code === 'nothing_to_explain' ? 400 : 503)))
    renderWithProviders(<InsightsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Explain with AI' }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(screen.getByText(/Your weight trend slowed down/)).toBeInTheDocument()
  })

  it('points to the privacy settings when the AI consent is missing', async () => {
    routeFetch(routes(insights(), explain({ title: 'Consent required', status: 403, code: 'consent_required' }, 403)))
    renderWithProviders(<InsightsPage />)

    await userEvent.click(await screen.findByRole('button', { name: 'Explain with AI' }))

    expect(await screen.findByText(/Turn on AI processing/)).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Open privacy settings' }).at(-1)).toHaveAttribute('href', '/settings/privacy')
  })
})

describe('InsightsCard on the dashboard', () => {
  it('shows the two most important observations and skips notes about the data', async () => {
    const dataQuality: Insight = { code: 'nutrition_logging_coverage', category: 'data_quality', severity: 'info', statement: 'coverage', facts: [fact('logged_days', 12), fact('window_days', 30)], caveats: [] }
    routeFetch(routes(insights({ insights: [dataQuality, weightShift, caloriesVsTarget, { ...caloriesVsTarget, code: 'protein_vs_target' }] })))

    renderWithProviders(<InsightsCard />)

    expect(await screen.findByText(/Your weight trend slowed down/)).toBeInTheDocument()
    expect(screen.getByText(/Your average calories on logged days/)).toBeInTheDocument()
    expect(screen.queryByText(/protein/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/You logged meals on/)).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open insights' })).toHaveAttribute('href', '/health/insights')
  })

  it('says there is not enough data yet instead of inventing a message', async () => {
    routeFetch(routes(insights({ insights: [], trends: [], comparisons: [] })))

    renderWithProviders(<InsightsCard />)

    expect(await screen.findByText(/Not enough recorded data yet to find trends/)).toBeInTheDocument()
  })
})
