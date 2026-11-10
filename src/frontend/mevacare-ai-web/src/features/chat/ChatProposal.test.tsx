import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ProposalCard as Card } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ChatPage } from './ChatPage'

const consents = [{ type: 'AiProcessing', granted: true, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null }]
const nutrients = (calories: number | null) => ({ calories, proteinG: calories ? 10 : null, carbohydratesG: null, fatG: null, fiberG: null })

const card = (overrides: Partial<Card> = {}): Card => ({
  id: 'p1',
  assistantMessageSequence: 2,
  state: 'Proposed',
  mealId: null,
  proposal: {
    mealType: 'Lunch',
    localDate: null,
    replacesMealId: null,
    warnings: ['rice: raw or cooked was not stated; the cooked version was assumed.'],
    totals: { calories: 525, proteinG: 66.1, carbohydratesG: 42.3, fatG: 7.6, fiberG: 0.6, incompleteData: false, includesEstimates: false },
    items: [
      {
        clientItemId: 'i1',
        inputText: '200 grams of chicken breast',
        status: 'Matched',
        alternatives: [],
        quantityGrams: 200,
        preparation: 'Cooked',
        preparationAssumed: false,
        ambiguousMatch: false,
        food: { foodId: 'f-chicken', description: 'Chicken, roasted', preparation: 'Cooked', category: null, sourceLabel: 'UsdaSrLegacy #171477' },
        nutrients: nutrients(330),
        note: null,
      },
      {
        clientItemId: 'i2',
        inputText: 'dragon steak',
        status: 'Unmatched',
        alternatives: [],
        quantityGrams: null,
        preparation: 'Unspecified',
        preparationAssumed: false,
        ambiguousMatch: false,
        food: null,
        nutrients: nutrients(null),
        note: 'Not found in the food database.',
      },
    ],
  },
  ...overrides,
})

/** A card whose every line has a food and a weight, so it can be saved in one tap. */
const readyCard = (proposal: Partial<Card['proposal']> = {}): Card => {
  const base = card()
  return { ...base, proposal: { ...base.proposal, warnings: [], items: [base.proposal.items[0]], ...proposal } }
}

function sse(...events: [string, object][]) {
  const text = events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('')
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(text))
      controller.close()
    },
  })
  return new Response(body, { status: 200 })
}

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

function setup(extra: (url: string, init?: RequestInit) => Response | undefined, streamCard: Card = card()) {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      const custom = extra(url, init)
      if (custom) return custom
      if (url === '/api/consents') return json(consents)
      if (url === '/api/conversations') return json([])
      if (url === '/api/chat/stream') {
        return sse(
          ['conversation', { conversationId: 'c1', title: 'x' }],
          ['tool', { name: 'PrepareMeal', status: 'running' }],
          ['tool', { name: 'PrepareMeal', status: 'done' }],
          ['proposal', { card: streamCard }],
          ['delta', { text: 'I prepared your lunch.' }],
          ['done', { messageId: 'm1' }],
        )
      }
      return json([])
    }),
  )
  return calls
}

async function sendMeal() {
  const input = await screen.findByLabelText('Ask anything about your wellness…')
  await waitFor(() => expect(input).toBeEnabled())
  await userEvent.type(input, 'I ate chicken and dragon steak{Enter}')
}

describe('meal proposals in the chat', () => {
  it('shows the tool status and a proposal card that is clearly not saved yet', async () => {
    const calls = setup(() => undefined)

    renderWithProviders(<ChatPage />)
    await sendMeal()

    expect(await screen.findByText('Meal prepared by the assistant')).toBeInTheDocument()
    expect(screen.getByText('Preparing your meal')).toBeInTheDocument()
    expect(screen.getByText('Chicken, roasted')).toBeInTheDocument()
    expect(screen.getByText('Not found in the food database')).toBeInTheDocument()
    expect(screen.getByText(/Not saved yet/)).toBeInTheDocument()
    expect(screen.getByText(/raw or cooked was not stated/)).toBeInTheDocument()
    const stream = calls.find((c) => c.url === '/api/chat/stream')!
    expect(JSON.parse(stream.init?.body as string).clientTime).toMatch(/^\d{4}-\d\d-\d\dT/)
    expect(calls.some((c) => c.url === '/api/nutrition/meals')).toBe(false) // nothing is written without confirmation
  })

  it('requires the user to resolve unmatched items, then confirms with the proposal id and the corrected weight', async () => {
    const posts: { body: Record<string, unknown> }[] = []
    setup((url, init) => {
      if (url === '/api/nutrition/meals' && init?.method === 'POST') {
        posts.push({ body: JSON.parse(init.body as string) })
        return json({ id: 'meal-1' }, 201)
      }
      return undefined
    })

    renderWithProviders(<ChatPage />)
    await sendMeal()
    await userEvent.click(await screen.findByRole('button', { name: 'Review and confirm' }))

    expect(screen.getByRole('button', { name: 'Confirm and save' })).toBeDisabled() // the unmatched item blocks saving

    // turn the unmatched item into a custom, estimated item and halve the chicken
    await userEvent.click(screen.getAllByRole('button', { name: 'Add custom item' })[0])
    const weights = screen.getAllByLabelText('Weight (g)')
    await userEvent.clear(weights[0])
    await userEvent.type(weights[0], '100')
    const nameField = screen.getAllByLabelText('Name').at(-1)!
    await userEvent.clear(nameField)
    await userEvent.type(nameField, 'Dragon steak')
    await userEvent.type(screen.getAllByLabelText('Weight (g)').at(-1)!, '150')
    await userEvent.type(screen.getByLabelText(/Calories \(Dragon steak\)/), '300')
    await userEvent.click(screen.getByRole('button', { name: 'Confirm and save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    const body = posts[0].body as { proposalId: string; items: { foodId: string | null; quantityGrams: number; estimated: unknown }[] }
    expect(body.proposalId).toBe('p1')
    expect(body.items).toHaveLength(2)
    expect(body.items[0]).toMatchObject({ foodId: 'f-chicken', quantityGrams: 100, estimated: null })
    expect(body.items[1]).toMatchObject({ foodId: null, quantityGrams: 150 })
    expect(await screen.findByText('Saved to your journal')).toBeInTheDocument()
  })

  it('saves a ready proposal in one tap with the proposal id, and a card with an unmatched item cannot be saved that way', async () => {
    const posts: { body: Record<string, unknown> }[] = []
    const save = (url: string, init?: RequestInit) => {
      if (url === '/api/nutrition/meals' && init?.method === 'POST') {
        posts.push({ body: JSON.parse(init.body as string) })
        return json({ id: 'meal-1' }, 201)
      }
      return undefined
    }

    setup(save)
    renderWithProviders(<ChatPage />)
    await sendMeal()
    expect(await screen.findByText('Meal prepared by the assistant')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.getByText(/need a choice or a weight/)).toBeInTheDocument()
    expect(posts).toHaveLength(0)
  })

  it('saves what the card shows when the user taps Save', async () => {
    const posts: { body: Record<string, unknown> }[] = []
    setup(
      (url, init) => {
        if (url === '/api/nutrition/meals' && init?.method === 'POST') {
          posts.push({ body: JSON.parse(init.body as string) })
          return json({ id: 'meal-1' }, 201)
        }
        return undefined
      },
      readyCard(),
    )

    renderWithProviders(<ChatPage />)
    await sendMeal()
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body).toMatchObject({ type: 'Lunch', proposalId: 'p1', notes: null })
    expect(posts[0].body.items).toEqual([{ foodId: 'f-chicken', name: 'Chicken, roasted', quantityGrams: 200, preparation: 'Cooked', estimated: null }])
    expect(await screen.findByText('Saved to your journal')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
  })

  it('logs the meal on the day the user named, at the usual hour of that meal', async () => {
    const posts: { body: { eatenAt: string; type: string } }[] = []
    setup(
      (url, init) => {
        if (url === '/api/nutrition/meals' && init?.method === 'POST') {
          posts.push({ body: JSON.parse(init.body as string) })
          return json({ id: 'meal-1' }, 201)
        }
        return undefined
      },
      readyCard({ mealType: 'Dinner', localDate: '2020-01-05' }),
    )

    renderWithProviders(<ChatPage />)
    await sendMeal()
    expect(await screen.findByText('For 2020-01-05')).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body.type).toBe('Dinner')
    expect(posts[0].body.eatenAt).toMatch(/^2020-01-05T19:00:00[+-]\d\d:\d\d$/)
  })

  it('keeps the card open and says so when saving fails', async () => {
    setup(
      (url, init) =>
        url === '/api/nutrition/meals' && init?.method === 'POST' ? json({ code: 'boom', message: 'x' }, 500) : undefined,
      readyCard(),
    )

    renderWithProviders(<ChatPage />)
    await sendMeal()
    await userEvent.click(await screen.findByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not save the meal')
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })

  it('can dismiss a proposal', async () => {
    const dismissed: string[] = []
    setup((url, init) => {
      if (url.endsWith('/dismiss') && init?.method === 'POST') {
        dismissed.push(url)
        return new Response(null, { status: 204 })
      }
      return undefined
    })

    renderWithProviders(<ChatPage />)
    await sendMeal()
    await userEvent.click(await screen.findByRole('button', { name: 'Dismiss' }))

    await waitFor(() => expect(dismissed).toEqual(['/api/assistant/proposals/p1/dismiss']))
    expect(await screen.findByText('Dismissed')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Review and confirm' })).not.toBeInTheDocument()
  })

  it('shows previously prepared proposals under the right message when a conversation is reopened', async () => {
    setup((url) => {
      if (url === '/api/conversations') return json([{ id: 'c1', title: 'Lunch', lastMessageAtUtc: new Date().toISOString() }])
      if (url === '/api/conversations/c1/messages') {
        return json([
          { id: 'u1', sequence: 1, role: 'User', content: 'I ate chicken', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-10-21T10:00:00Z' },
          { id: 'a1', sequence: 2, role: 'Assistant', content: 'Prepared.', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-10-21T10:00:01Z' },
        ])
      }
      if (url === '/api/conversations/c1/proposals') return json([card({ state: 'Committed', mealId: 'meal-1' })])
      return undefined
    })

    renderWithProviders(<ChatPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Lunch' }))

    expect(await screen.findByText('Saved to your journal')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Review and confirm' })).not.toBeInTheDocument()
  })
})
