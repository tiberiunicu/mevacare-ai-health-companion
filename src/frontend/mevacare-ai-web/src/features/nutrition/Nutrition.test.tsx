import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuthStore } from '../../stores/auth'
import type { DailySummary, FoodSearchItem, Meal, MealProposal } from '../../lib/types'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { DailySummaryPage, FoodJournalPage, NutritionGoalsPage } from './NutritionPages'
import { buildRequest, isItemValid, itemFromFood, itemsFromMeal, itemsFromProposal, parseGrams, previewNutrients, sumPreview } from './mealEditorModel'

const chicken: FoodSearchItem = {
  foodId: 'f-chicken', description: 'Chicken, broilers or fryers, breast, meat only, cooked, roasted', preparation: 'Cooked', category: 'Poultry Products',
  sourceLabel: 'UsdaSrLegacy #171477', per100g: { calories: 165, proteinG: 31.02, carbohydratesG: 0, fatG: 3.57, fiberG: 0 }, ambiguous: false, preparationAssumed: false,
}

const totals = (calories: number, extra = {}) => ({
  calories, proteinG: 66.1, carbohydratesG: 42.3, fatG: 7.6, fiberG: 0.6, incompleteData: false, includesEstimates: false, ...extra,
})

const meal = (overrides: Partial<Meal> = {}): Meal => ({
  id: 'm1', mealType: 'Lunch', eatenAtUtc: '2026-10-21T10:30:00Z', localDate: '2026-10-21', utcOffsetMinutes: 180, notes: null, origin: 'Manual',
  items: [
    { id: 'i1', name: chicken.description, quantityGrams: 200, preparation: 'Cooked', isEstimated: false, sourceLabel: chicken.sourceLabel, foodId: 'f-chicken', nutrients: { calories: 330, proteinG: 62.04, carbohydratesG: 0, fatG: 7.14, fiberG: 0 } },
    { id: 'i2', name: 'Home-made cake', quantityGrams: 100, preparation: 'Unspecified', isEstimated: true, sourceLabel: 'Estimated by user', foodId: null, nutrients: { calories: 400, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null } },
  ],
  totals: totals(730, { incompleteData: true, includesEstimates: true }),
  ...overrides,
})

const summary = (meals: Meal[], extra: Partial<DailySummary> = {}): DailySummary => ({
  date: '2026-10-21', totals: totals(meals.length ? 730 : 0), targets: { calories: null, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null },
  remaining: { calories: null, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null }, meals, ...extra,
})

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

describe('meal editor model', () => {
  it('previews a database food linearly from per-100-g values and keeps unknown values unknown', () => {
    const item = { ...itemFromFood({ ...chicken, per100g: { ...chicken.per100g, fiberG: null } }), grams: '200' }

    const preview = previewNutrients(item)!

    expect(preview.calories).toBe(330)
    expect(preview.proteinG).toBe(62.04)
    expect(preview.fiberG).toBeNull()
    expect(sumPreview([item]).incomplete).toBe(true)
  })

  it('rejects zero, negative, absurd and non-numeric weights', () => {
    expect(parseGrams('150')).toBe(150)
    expect(parseGrams('150,5')).toBe(150.5)
    for (const bad of ['', '0', '-3', 'abc', '10001']) expect(parseGrams(bad)).toBeNull()
  })

  it('never sends nutrient numbers for database foods and sends estimates only for custom items', () => {
    const food = { ...itemFromFood(chicken), grams: '200' }
    const custom = { ...itemsFromMeal(meal())[1], grams: '100' }

    const request = buildRequest('Lunch', '2026-10-21T13:30:00+03:00', ' note ', [food, custom], 'p-1')

    expect(request.items[0]).toEqual({ foodId: 'f-chicken', name: chicken.description, quantityGrams: 200, preparation: 'Cooked', estimated: null })
    expect(request.items[1].foodId).toBeNull()
    expect(request.items[1].estimated).toMatchObject({ calories: 400, proteinG: null })
    expect(request.notes).toBe('note')
    expect(request.proposalId).toBe('p-1')
  })

  it('treats unmatched items as invalid until resolved and needs a name and a value for custom items', () => {
    const proposal: MealProposal = {
      mealType: 'Lunch', warnings: [], totals: totals(0),
      items: [{
        clientItemId: 'i1', inputText: 'dragon steak', status: 'Unmatched', food: null, alternatives: [], quantityGrams: null, preparation: 'Unspecified',
        preparationAssumed: false, ambiguousMatch: false, nutrients: { calories: null, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null }, note: 'Not found',
      }],
    }
    const [unresolved] = itemsFromProposal(proposal)

    expect(unresolved.kind).toBe('unresolved')
    expect(isItemValid(unresolved)).toBe(false)
    const custom = { ...unresolved, kind: 'custom' as const, name: 'Dragon steak', grams: '200' }
    expect(isItemValid(custom)).toBe(false)
    expect(isItemValid({ ...custom, estimated: { ...custom.estimated, calories: '350' } })).toBe(true)
  })

  it('derives per-100-g values from a proposal so the preview follows weight edits', () => {
    const proposal: MealProposal = {
      mealType: null, warnings: [], totals: totals(330),
      items: [{
        clientItemId: 'i1', inputText: '200 g chicken', status: 'Matched', alternatives: [], quantityGrams: 200, preparation: 'Cooked', preparationAssumed: true, ambiguousMatch: false,
        food: { foodId: 'f-chicken', description: chicken.description, preparation: 'Cooked', category: null, sourceLabel: 'x' },
        nutrients: { calories: 330, proteinG: 62.04, carbohydratesG: 0, fatG: 7.14, fiberG: 0 }, note: null,
      }],
    }
    const item = itemsFromProposal(proposal)[0]

    expect(previewNutrients({ ...item, grams: '100' })!.calories).toBe(165)
    expect(item.preparationAssumed).toBe(true)
  })
})

describe('FoodJournalPage', () => {
  it('shows the days meals with totals, flags estimates and incomplete data', async () => {
    const day = summary([meal()])
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => (String(input).includes('/summary/daily') ? json(day) : json({}))))

    renderWithProviders(<FoodJournalPage />)

    expect(await screen.findByText(chicken.description)).toBeInTheDocument()
    expect(screen.getByText('Home-made cake')).toBeInTheDocument()
    expect(screen.getByText('Estimated')).toBeInTheDocument()
    expect(screen.getAllByText(/Some values are not reported/).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/Includes values you estimated/).length).toBeGreaterThan(0)
  })

  it('explains the missing health-data consent instead of showing an empty journal', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ code: 'consent_required', detail: 'x' }, 403)))

    renderWithProviders(<FoodJournalPage />)

    expect(await screen.findByText('Health data processing is switched off')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open Privacy & Consent' })).toHaveAttribute('href', '/settings/privacy')
  })

  it('adds a meal by searching the food database; the request carries a food id and weight but no nutrients', async () => {
    const posts: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/search')) return json([chicken])
      if (url.endsWith('/api/nutrition/meals') && init?.method === 'POST') {
        posts.push(JSON.parse(init.body as string))
        return json(meal(), 201)
      }
      return json({})
    }))

    renderWithProviders(<FoodJournalPage />)
    await screen.findByText(/Nothing logged for this day yet/)
    await userEvent.click(screen.getByRole('button', { name: 'Add meal' }))
    await userEvent.type(await screen.findByLabelText('Search the food database'), 'chicken breast')
    await userEvent.click(await screen.findByRole('button', { name: new RegExp(chicken.description) }))
    await userEvent.type(await screen.findByLabelText('Weight (g)'), '200')
    expect((await screen.findAllByText(/330 kcal/)).length).toBeGreaterThan(0)
    await userEvent.click(screen.getByRole('button', { name: 'Save meal' }))

    await waitFor(() => expect(posts).toHaveLength(1))
    const body = posts[0] as { items: Record<string, unknown>[]; proposalId: unknown }
    expect(body.items[0]).toEqual({ foodId: 'f-chicken', name: chicken.description, quantityGrams: 200, preparation: 'Cooked', estimated: null })
    expect(body.proposalId).toBeNull()
  })

  it('does not search or invent anything for text with no match', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => (String(input).includes('/foods/search') ? json([]) : json(summary([])))))

    renderWithProviders(<FoodJournalPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Add meal' }))
    await userEvent.type(await screen.findByLabelText('Search the food database'), 'dragon steak')

    expect(await screen.findByText(/No matching food found/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save meal' })).toBeDisabled()
  })

  it('deletes a meal', async () => {
    const deleted: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'DELETE') {
        deleted.push(url)
        return new Response(null, { status: 204 })
      }
      return url.includes('/summary/daily') ? json(summary([meal()])) : json({})
    }))

    renderWithProviders(<FoodJournalPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete: Lunch' }))

    await waitFor(() => expect(deleted).toEqual(['/api/nutrition/meals/m1']))
  })
})

describe('DailySummaryPage', () => {
  it('shows progress against targets and how much remains, including when a target is exceeded', async () => {
    const day = summary([meal()], {
      totals: totals(2150, { proteinG: 100 }),
      targets: { calories: 2000, proteinG: 120, carbohydratesG: null, fatG: null, fiberG: null },
      remaining: { calories: -150, proteinG: 20, carbohydratesG: null, fatG: null, fiberG: null },
    })
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes('/summary/range') ? json({ from: '', to: '', days: [], daysWithData: 0, averagePerLoggedDay: {}, targets: {} }) : json(day)))

    renderWithProviders(<DailySummaryPage />)

    const calories = await screen.findByRole('progressbar', { name: 'Calories' })
    expect(calories).toHaveAttribute('aria-valuenow', '2000') // capped at the target
    expect(screen.getByText('150 kcal over target')).toBeInTheDocument()
    expect(screen.getByText('20.0 g')).toBeInTheDocument()
  })
})

describe('NutritionGoalsPage', () => {
  it('saves targets and sends null for fields left empty', async () => {
    const puts: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        puts.push(JSON.parse(init.body as string))
        return json(puts[0])
      }
      return json({ calories: null, proteinG: null, carbohydratesG: null, fatG: null, fiberG: null })
    }))

    renderWithProviders(<NutritionGoalsPage />)
    await userEvent.type(await screen.findByLabelText('Calories (kcal)'), '2000')
    await userEvent.type(screen.getByLabelText('Protein (g)'), '120')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(puts).toHaveLength(1))
    expect(puts[0]).toEqual({ calories: 2000, proteinG: 120, carbohydratesG: null, fatG: null, fiberG: null })
    expect(await screen.findByText('Targets saved')).toBeInTheDocument()
    expect(within(document.body).queryByText(/prescribe/)).toBeInTheDocument()
  })
})

describe('online food search', () => {
  const hochland: FoodSearchItem = {
    foodId: 'f-hochland', description: 'Hochland Cottage cheese', preparation: 'Unspecified', category: 'Cottage cheeses', sourceLabel: 'OpenFoodFacts #5941238007810',
    per100g: { calories: 95, proteinG: 12, carbohydratesG: 2, fatG: 4.5, fiberG: 0 }, ambiguous: false, preparationAssumed: false, brand: 'Hochland', barcode: '5941238007810',
  }
  const candidate = {
    barcode: '5941238007810', name: 'Hochland Cottage cheese', brand: 'Hochland', category: 'Cottage cheeses', quantity: '175 g', per100g: hochland.per100g,
    sourceLabel: 'OpenFoodFacts #5941238007810', foodId: null,
  }

  const openPicker = async () => {
    renderWithProviders(<FoodJournalPage />)
    await screen.findByText(/Nothing logged for this day yet/)
    await userEvent.click(screen.getByRole('button', { name: 'Add meal' }))
    return screen.findByLabelText('Search the food database')
  }

  it('shows the brand of packaged products in the local results', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/search')) return json([hochland])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'hochland')

    const row = await screen.findByRole('button', { name: /Hochland Cottage cheese/ })
    expect(within(row).getByText(/· Hochland/)).toBeInTheDocument()
    expect(row).toHaveTextContent('95 kcal per 100 g')
    expect(row).toHaveTextContent('OpenFoodFacts #5941238007810')
  })

  it('sends nothing online until the button is pressed, then previews candidates and adds the chosen one by barcode only', async () => {
    const calls: string[] = []
    const posts: unknown[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push(url)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/online/search')) return json([candidate])
      if (url.includes('/foods/online/add') && init?.method === 'POST') {
        posts.push(JSON.parse(init.body as string))
        return json(hochland)
      }
      if (url.includes('/foods/search')) return json([])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'cottage cheese hochland')
    await screen.findByText(/No matching food found/)
    expect(calls.some((c) => c.includes('/foods/online'))).toBe(false)
    expect(screen.getByText(/Only the text you typed is sent/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Search packaged products online' }))
    const row = await screen.findByRole('button', { name: /Hochland Cottage cheese/ })
    expect(row).toHaveTextContent('175 g')
    expect(screen.getByText(/Open Food Facts contributors/)).toBeInTheDocument()
    await userEvent.click(row)

    await waitFor(() => expect(posts).toEqual([{ barcode: '5941238007810' }]))
    await userEvent.type(await screen.findByLabelText('Weight (g)'), '175')
    expect((await screen.findAllByText(/166 kcal/)).length).toBeGreaterThan(0)
  })

  it('explains a missing consent with a link to the privacy settings', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/online/search')) return json({ title: 'Consent required', status: 403, code: 'consent_required' }, 403)
      if (url.includes('/foods/search')) return json([])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'cottage cheese')
    await userEvent.click(await screen.findByRole('button', { name: 'Search packaged products online' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Searching online needs the External integrations consent.')
    expect(screen.getByRole('link', { name: 'Open privacy settings' })).toHaveAttribute('href', '/settings/privacy')
  })

  it('says so when the online database is unavailable and keeps nothing', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/online/search')) return json({ title: 'Unavailable', status: 503, code: 'food_online_unavailable' }, 503)
      if (url.includes('/foods/search')) return json([])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'cottage cheese')
    await userEvent.click(await screen.findByRole('button', { name: 'Search packaged products online' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The online food database is not available right now.')
    expect(screen.getByRole('button', { name: 'Save meal' })).toBeDisabled()
  })

  it('says when the calories of a product were calculated because the source gives none', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/online/search')) return json([{ ...candidate, energyDerived: true, per100g: { ...candidate.per100g, calories: 96.5 } }])
      if (url.includes('/foods/search')) return json([])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'cottage cheese hochland')
    await userEvent.click(await screen.findByRole('button', { name: 'Search packaged products online' }))

    const row = await screen.findByRole('button', { name: /Hochland Cottage cheese/ })
    expect(row).toHaveTextContent('calculated from protein, carbohydrates and fat (an estimate)')
  })

  it('reports an empty online result without inventing anything', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/summary/daily')) return json(summary([]))
      if (url.includes('/foods/online/search')) return json([])
      if (url.includes('/foods/search')) return json([])
      return json({})
    }))

    await userEvent.type(await openPicker(), 'dragon steak')
    await userEvent.click(await screen.findByRole('button', { name: 'Search packaged products online' }))

    expect(await screen.findByText(/Nothing usable was found online either/)).toBeInTheDocument()
  })
})
