import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ActionCard as Card, Meal, ProposalCard } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ActionCard } from './ActionCard'
import { ChatPage } from './ChatPage'
import { ProposalCard as MealCard } from './ProposalCard'

const consents = [{ type: 'AiProcessing', granted: true, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null }]
const none = { calories: null, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null }

const weight = (overrides: Partial<Card> = {}): Card =>
  ({ id: 'a1', assistantMessageSequence: 2, state: 'Proposed', kind: 'ProposeWeight', payload: { kilograms: 82.4, measuredAt: '2026-11-10T08:00:00+02:00', localDate: '2026-11-10' }, ...overrides }) as Card

const targets: Card = {
  id: 'a2', assistantMessageSequence: 2, state: 'Proposed', kind: 'ProposeNutritionTargets',
  payload: { before: { ...none, calories: 2000, proteinG: 100 }, after: { ...none, calories: 2200, proteinG: 100 } },
}

const profile: Card = {
  id: 'a3', assistantMessageSequence: 2, state: 'Proposed', kind: 'ProposeProfileUpdate',
  payload: { before: { heightCm: 168.5, activityLevel: 'Sedentary' }, after: { heightCm: 170, activityLevel: 'ModeratelyActive' } },
}

const deletion: Card = {
  id: 'a4', assistantMessageSequence: 2, state: 'Proposed', kind: 'ProposeMealDeletion',
  payload: { mealId: 'm1', mealType: 'Lunch', localDate: '2026-11-10', calories: 400, items: ['Home-made cake'] },
}

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

function stubFetch(handler: (url: string, init?: RequestInit) => Response | undefined) {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      return handler(url, init) ?? json([])
    }),
  )
  return calls
}

describe('ActionCard', () => {
  it('shows a prepared weight as not applied yet and applies it only when the button is pressed', async () => {
    const calls = stubFetch((url, init) => (url === '/api/assistant/actions/a1/confirm' && init?.method === 'POST' ? json({}) : undefined))
    const onChange = vi.fn()

    renderWithProviders(<ActionCard card={weight()} onChange={onChange} />)

    expect(screen.getByText('Weight entry prepared')).toBeInTheDocument()
    expect(screen.getByText('82.4 kg · 2026-11-10')).toBeInTheDocument()
    expect(screen.getByText(/Nothing has changed yet/)).toBeInTheDocument()
    expect(calls).toHaveLength(0)

    await userEvent.click(screen.getByRole('button', { name: 'Save weight' }))

    await waitFor(() => expect(onChange).toHaveBeenCalledWith({ state: 'Committed' }))
    expect(calls.map((c) => `${c.init?.method} ${c.url}`)).toContain('POST /api/assistant/actions/a1/confirm')
  })

  it('shows the applied state without any button', () => {
    stubFetch(() => undefined)
    renderWithProviders(<ActionCard card={weight({ state: 'Committed' })} onChange={vi.fn()} />)

    expect(screen.getByRole('status')).toHaveTextContent('Weight saved')
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('lists only the targets that change, from the old to the new value', () => {
    stubFetch(() => undefined)
    renderWithProviders(<ActionCard card={targets} onChange={vi.fn()} />)

    expect(screen.getByText(/2000 kcal →/)).toBeInTheDocument()
    expect(screen.getByText('2200 kcal')).toBeInTheDocument()
    expect(screen.queryByText('Protein')).not.toBeInTheDocument() // unchanged, not shown
    expect(screen.getByRole('button', { name: 'Change targets' })).toBeInTheDocument()
  })

  it('shows the profile change with readable activity levels', () => {
    stubFetch(() => undefined)
    renderWithProviders(<ActionCard card={profile} onChange={vi.fn()} />)

    expect(screen.getByText('Height (cm)')).toBeInTheDocument()
    expect(screen.getByText('170')).toBeInTheDocument()
    expect(screen.getByText(/Sedentary \(little exercise\) →/)).toBeInTheDocument()
    expect(screen.getByText(/Moderately active/)).toBeInTheDocument()
  })

  it('names the meal that would be deleted and uses a destructive button', () => {
    stubFetch(() => undefined)
    renderWithProviders(<ActionCard card={deletion} onChange={vi.fn()} />)

    expect(screen.getByText(/Lunch · 2026-11-10 · 400 kcal/)).toBeInTheDocument()
    expect(screen.getByText('Home-made cake')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete meal' })).toBeInTheDocument()
  })

  it('can be dismissed without applying anything', async () => {
    const calls = stubFetch((url, init) => (url.endsWith('/dismiss') && init?.method === 'POST' ? new Response(null, { status: 204 }) : undefined))
    const onChange = vi.fn()

    renderWithProviders(<ActionCard card={weight()} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }))

    await waitFor(() => expect(onChange).toHaveBeenCalledWith({ state: 'Dismissed' }))
    expect(calls.some((c) => c.url.endsWith('/confirm'))).toBe(false)
  })

  it('explains a missing health-data consent and keeps the card open', async () => {
    stubFetch((url) => (url.endsWith('/confirm') ? json({ code: 'consent_required', detail: 'x' }, 403) : undefined))
    const onChange = vi.fn()

    renderWithProviders(<ActionCard card={weight()} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Save weight' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Health data processing is switched off')
    expect(onChange).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Save weight' })).toBeEnabled()
  })

  it('says so when applying fails and lets the user try again', async () => {
    stubFetch((url) => (url.endsWith('/confirm') ? json({ code: 'boom', message: 'x' }, 500) : undefined))
    const onChange = vi.fn()

    renderWithProviders(<ActionCard card={weight()} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Save weight' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not apply the change')
    expect(onChange).not.toHaveBeenCalled()
  })
})

function sse(...events: [string, object][]) {
  const text = events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('')
  return new Response(new ReadableStream({ start(c) { c.enqueue(new TextEncoder().encode(text)); c.close() } }), { status: 200 })
}

describe('actions in the chat', () => {
  it('shows a card streamed by the assistant and restores its state when the conversation is reopened', async () => {
    stubFetch((url) => {
      if (url === '/api/consents') return json(consents)
      if (url === '/api/conversations') return json([{ id: 'c1', title: 'Weight', lastMessageAtUtc: new Date().toISOString() }])
      if (url === '/api/chat/stream') {
        return sse(
          ['conversation', { conversationId: 'c1', title: 'x' }],
          ['tool', { name: 'ProposeWeight', status: 'running' }],
          ['tool', { name: 'ProposeWeight', status: 'done' }],
          ['action', { card: weight() }],
          ['delta', { text: 'I prepared it.' }],
          ['done', { messageId: 'm1' }],
        )
      }
      if (url === '/api/conversations/c1/messages') {
        return json([
          { id: 'u1', sequence: 1, role: 'User', content: 'I weigh 82.4', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-11-10T10:00:00Z', citations: [] },
          { id: 'a1', sequence: 2, role: 'Assistant', content: 'Prepared.', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-11-10T10:00:01Z', citations: [] },
        ])
      }
      if (url === '/api/conversations/c1/actions') return json([weight({ state: 'Committed' })])
      return undefined
    })

    renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'I weigh 82.4{Enter}')

    expect(await screen.findByText('Weight entry prepared')).toBeInTheDocument()
    expect(screen.getByText('Preparing a weight entry')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save weight' })).toBeInTheDocument()

    await userEvent.click(await screen.findByRole('button', { name: 'Weight' })) // reopen from the history
    expect(await screen.findByRole('status')).toHaveTextContent('Weight saved')
    expect(screen.queryByRole('button', { name: 'Save weight' })).not.toBeInTheDocument()
  })
})

describe('correcting a meal', () => {
  const meal = (): Meal => ({
    id: 'm9', mealType: 'Dinner', eatenAtUtc: '2026-11-09T17:00:00Z', localDate: '2026-11-09', utcOffsetMinutes: 120, notes: 'with friends', origin: 'Manual',
    items: [], totals: { calories: 0, proteinG: 0, carbohydratesG: 0, fatG: 0, fiberG: 0, incompleteData: false, includesEstimates: false },
  })

  const correction = (): ProposalCard => ({
    id: 'p9', assistantMessageSequence: 2, state: 'Proposed', mealId: null,
    proposal: {
      mealType: 'Lunch', localDate: null, replacesMealId: 'm9', warnings: [],
      totals: { calories: 330, proteinG: 62, carbohydratesG: 0, fatG: 7, fiberG: 0, incompleteData: false, includesEstimates: false },
      items: [{
        clientItemId: 'i1', inputText: '200 g chicken', status: 'Matched', alternatives: [], quantityGrams: 200, preparation: 'Cooked', preparationAssumed: false,
        ambiguousMatch: false, food: { foodId: 'f-chicken', description: 'Chicken, roasted', preparation: 'Cooked', category: null, sourceLabel: 'x' },
        nutrients: { calories: 330, proteinG: 62, carbohydratesG: 0, fatG: 7, fiberG: 0 }, note: null,
      }],
    },
  })

  it('replaces the existing meal with one tap and keeps its type, time and notes', async () => {
    const puts: { url: string; body: Record<string, unknown> }[] = []
    stubFetch((url, init) => {
      if (url === '/api/nutrition/meals/m9' && (init?.method ?? 'GET') === 'GET') return json(meal())
      if (url === '/api/nutrition/meals/m9' && init?.method === 'PUT') {
        puts.push({ url, body: JSON.parse(init.body as string) })
        return json({ ...meal(), id: 'm9' })
      }
      return undefined
    })
    const onChange = vi.fn()

    renderWithProviders(<MealCard card={correction()} onChange={onChange} />)

    expect(await screen.findByText(/Replaces your Dinner of 2026-11-09/)).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => expect(puts).toHaveLength(1))
    expect(puts[0].body).toMatchObject({ type: 'Dinner', notes: 'with friends', proposalId: 'p9' })
    expect(String(puts[0].body.eatenAt)).toMatch(/^2026-11-09T/)
    expect(puts[0].body.items).toEqual([{ foodId: 'f-chicken', name: 'Chicken, roasted', quantityGrams: 200, preparation: 'Cooked', estimated: null }])
    await waitFor(() => expect(onChange).toHaveBeenCalledWith({ state: 'Committed', mealId: 'm9' }))
  })

  it('cannot be saved while the meal is unknown, and says so when it no longer exists', async () => {
    stubFetch((url) => (url === '/api/nutrition/meals/m9' ? json({ code: 'not_found', message: 'x' }, 404) : undefined))

    renderWithProviders(<MealCard card={correction()} onChange={vi.fn()} />)

    expect(await screen.findByText(/no longer exists/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeInTheDocument()
  })
})
