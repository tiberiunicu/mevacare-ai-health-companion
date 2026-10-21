import type {
  FoodSearchItem,
  Meal,
  MealProposal,
  MealType,
  NutrientValues,
  Preparation,
  SaveMealItem,
  SaveMealRequest,
} from '../../lib/types'

/** One editable line of a meal. 'unresolved' = the assistant could not match it; the user must pick a food or enter an estimate. */
export interface EditorItem {
  key: string
  kind: 'food' | 'custom' | 'unresolved'
  foodId: string | null
  name: string
  grams: string
  preparation: Preparation
  /** Nutrients per 100 g of the chosen food, when known (used only for the live preview; the server recalculates). */
  per100g: NutrientValues | null
  /** Saved snapshot of the item and the weight it was calculated for, used to scale the preview of existing items. */
  snapshot: NutrientValues | null
  snapshotGrams: number | null
  estimated: Record<keyof NutrientValues, string>
  note: string | null
  preparationAssumed: boolean
  ambiguous: boolean
  sourceLabel: string | null
}

export const NUTRIENT_KEYS: (keyof NutrientValues)[] = ['calories', 'proteinG', 'carbohydratesG', 'fatG', 'fiberG']

const emptyEstimate = (): Record<keyof NutrientValues, string> => ({ calories: '', proteinG: '', carbohydratesG: '', fatG: '', fiberG: '' })

let counter = 0
const nextKey = () => `item-${++counter}`

export function blankItem(kind: EditorItem['kind'] = 'unresolved'): EditorItem {
  return {
    key: nextKey(), kind, foodId: null, name: '', grams: '', preparation: 'Unspecified', per100g: null, snapshot: null,
    snapshotGrams: null, estimated: emptyEstimate(), note: null, preparationAssumed: false, ambiguous: false, sourceLabel: null,
  }
}

export function itemFromFood(food: FoodSearchItem, grams = ''): EditorItem {
  return {
    ...blankItem('food'), foodId: food.foodId, name: food.description, grams, preparation: food.preparation, per100g: food.per100g,
    sourceLabel: food.sourceLabel, ambiguous: food.ambiguous, preparationAssumed: food.preparationAssumed,
  }
}

export function itemsFromMeal(meal: Meal): EditorItem[] {
  return meal.items.map((i) => ({
    ...blankItem(i.foodId ? 'food' : 'custom'),
    foodId: i.foodId, name: i.name, grams: String(i.quantityGrams), preparation: i.preparation, snapshot: i.nutrients, snapshotGrams: i.quantityGrams,
    sourceLabel: i.sourceLabel,
    estimated: i.isEstimated
      ? {
          calories: str(i.nutrients.calories), proteinG: str(i.nutrients.proteinG), carbohydratesG: str(i.nutrients.carbohydratesG),
          fatG: str(i.nutrients.fatG), fiberG: str(i.nutrients.fiberG),
        }
      : emptyEstimate(),
  }))
}

export function itemsFromProposal(proposal: MealProposal): EditorItem[] {
  return proposal.items.map((p) => {
    const base = blankItem(p.food ? 'food' : 'unresolved')
    const grams = p.quantityGrams
    // Derive per-100-g values from the server's calculation so the preview follows edits to the weight.
    const per100g: NutrientValues | null =
      p.food && grams
        ? Object.fromEntries(NUTRIENT_KEYS.map((k) => [k, p.nutrients[k] === null ? null : round((p.nutrients[k] as number) / grams * 100, 4)])) as unknown as NutrientValues
        : null
    return {
      ...base,
      foodId: p.food?.foodId ?? null,
      name: p.food?.description ?? p.inputText,
      grams: grams ? String(grams) : '',
      preparation: p.preparation,
      per100g,
      note: p.note,
      preparationAssumed: p.preparationAssumed,
      ambiguous: p.ambiguousMatch,
      sourceLabel: p.food?.sourceLabel ?? null,
    }
  })
}

const str = (value: number | null) => (value === null ? '' : String(value))
const round = (value: number, digits: number) => Math.round(value * 10 ** digits) / 10 ** digits

export function parseGrams(value: string): number | null {
  const n = Number(value.replace(',', '.'))
  return value.trim() !== '' && Number.isFinite(n) && n > 0 && n <= 10_000 ? n : null
}

/** Live preview for one item. For database foods this mirrors the server's arithmetic, which stays authoritative. */
export function previewNutrients(item: EditorItem): NutrientValues | null {
  const grams = parseGrams(item.grams)
  if (item.kind === 'custom') {
    const parse = (v: string) => (v.trim() === '' || !Number.isFinite(Number(v)) ? null : Number(v))
    return Object.fromEntries(NUTRIENT_KEYS.map((k) => [k, parse(item.estimated[k])])) as unknown as NutrientValues
  }
  if (grams === null) return null
  if (item.per100g) {
    return Object.fromEntries(NUTRIENT_KEYS.map((k) => [k, item.per100g![k] === null ? null : round((item.per100g![k] as number) * grams / 100, 2)])) as unknown as NutrientValues
  }
  if (item.snapshot && item.snapshotGrams) {
    return Object.fromEntries(NUTRIENT_KEYS.map((k) => [k, item.snapshot![k] === null ? null : round((item.snapshot![k] as number) * grams / item.snapshotGrams!, 2)])) as unknown as NutrientValues
  }
  return null
}

export function sumPreview(items: EditorItem[]): { calories: number; proteinG: number; carbohydratesG: number; fatG: number; fiberG: number; incomplete: boolean } {
  const total = { calories: 0, proteinG: 0, carbohydratesG: 0, fatG: 0, fiberG: 0, incomplete: false }
  for (const item of items) {
    const values = previewNutrients(item)
    if (!values) {
      total.incomplete = true
      continue
    }
    for (const key of NUTRIENT_KEYS) {
      if (values[key] === null) total.incomplete = true
      else total[key] += values[key] as number
    }
  }
  return total
}

export function isItemValid(item: EditorItem): boolean {
  if (item.kind === 'unresolved') return false
  if (parseGrams(item.grams) === null) return false
  if (item.kind === 'food') return item.foodId !== null
  if (item.name.trim() === '') return false
  return NUTRIENT_KEYS.some((k) => item.estimated[k].trim() !== '' && Number.isFinite(Number(item.estimated[k])) && Number(item.estimated[k]) >= 0)
}

export function buildRequest(
  type: MealType, eatenAt: string, notes: string, items: EditorItem[], proposalId: string | null,
): SaveMealRequest {
  const saveItems: SaveMealItem[] = items.map((i) =>
    i.kind === 'food'
      ? { foodId: i.foodId, name: i.name, quantityGrams: parseGrams(i.grams)!, preparation: i.preparation, estimated: null }
      : {
          foodId: null, name: i.name.trim(), quantityGrams: parseGrams(i.grams)!, preparation: 'Unspecified',
          estimated: Object.fromEntries(NUTRIENT_KEYS.map((k) => [k, i.estimated[k].trim() === '' ? null : Number(i.estimated[k])])) as unknown as NutrientValues,
        },
  )
  return { type, eatenAt, notes: notes.trim() === '' ? null : notes.trim(), items: saveItems, proposalId }
}
