// Mirrors the API contracts (src/backend/*/Features). Enums are serialized as strings.

export interface User {
  id: string
  email: string
  displayName: string
  roles: string[]
}

export interface AuthResponse {
  accessToken: string
  expiresAtUtc: string
  user: User
}

export type ConsentType = 'TermsOfService' | 'HealthDataProcessing' | 'AiProcessing' | 'ExternalIntegrations'

export interface Consent {
  type: ConsentType
  granted: boolean
  required: boolean
  policyVersion: string | null
  grantedAtUtc: string | null
  revokedAtUtc: string | null
}

export const BIOLOGICAL_SEX = ['Unspecified', 'Female', 'Male', 'Other'] as const
export type BiologicalSex = (typeof BIOLOGICAL_SEX)[number]

export const ACTIVITY_LEVELS = ['Unspecified', 'Sedentary', 'LightlyActive', 'ModeratelyActive', 'VeryActive', 'ExtraActive'] as const
export type ActivityLevel = (typeof ACTIVITY_LEVELS)[number]

export const GOAL_TYPES = ['WeightLoss', 'WeightGain', 'MaintainWeight', 'MuscleGain', 'ImproveSleep', 'GeneralWellness'] as const
export type GoalType = (typeof GOAL_TYPES)[number]

export interface HealthProfile {
  exists: boolean
  dateOfBirth: string | null
  sex: BiologicalSex
  heightCm: number | null
  activityLevel: ActivityLevel
  timeZoneId: string
}

export type UpdateHealthProfile = Omit<HealthProfile, 'exists'>

export interface HealthGoal {
  id: string
  type: GoalType
  targetWeightKg: number | null
  targetDate: string | null
  createdAtUtc: string
}

export interface CreateHealthGoal {
  type: GoalType
  targetWeightKg: number | null
  targetDate: string | null
}

export type UnitSystem = 'Metric' | 'Imperial'
export type ThemePreference = 'System' | 'Light' | 'Dark'

export interface HealthPreferences {
  language: 'en' | 'ro'
  unitSystem: UnitSystem
  theme: ThemePreference
}

// ---- Nutrition ----

/** null means "not reported by the food source" (never zero). */
export interface NutrientValues {
  calories: number | null
  proteinG: number | null
  carbohydratesG: number | null
  fatG: number | null
  fiberG: number | null
}

export interface NutrientTotals {
  calories: number
  proteinG: number
  carbohydratesG: number
  fatG: number
  fiberG: number
  incompleteData: boolean
  includesEstimates: boolean
}

export type NutritionTargets = NutrientValues

export type MealType = 'Breakfast' | 'Lunch' | 'Dinner' | 'Snack'
export const MEAL_TYPES: MealType[] = ['Breakfast', 'Lunch', 'Dinner', 'Snack']
export type Preparation = 'Unspecified' | 'Raw' | 'Cooked'

export interface MealItem {
  id: string
  name: string
  quantityGrams: number
  preparation: Preparation
  isEstimated: boolean
  sourceLabel: string | null
  foodId: string | null
  nutrients: NutrientValues
}

export interface Meal {
  id: string
  mealType: MealType
  eatenAtUtc: string
  localDate: string
  utcOffsetMinutes: number
  notes: string | null
  origin: 'Manual' | 'Assistant'
  items: MealItem[]
  totals: NutrientTotals
}

export interface DailySummary {
  date: string
  totals: NutrientTotals
  targets: NutritionTargets
  remaining: NutrientValues
  meals: Meal[]
}

export interface DayTotals {
  date: string
  totals: NutrientTotals
  mealCount: number
}

export interface RangeSummary {
  from: string
  to: string
  days: DayTotals[]
  daysWithData: number
  averagePerLoggedDay: NutrientValues
  targets: NutritionTargets
}

export interface FoodSearchItem {
  foodId: string
  description: string
  preparation: Preparation
  category: string | null
  sourceLabel: string
  per100g: NutrientValues
  ambiguous: boolean
  preparationAssumed: boolean
}

export interface SaveMealItem {
  foodId: string | null
  name: string
  quantityGrams: number
  preparation: Preparation
  estimated: Omit<NutrientValues, never> | null
}

export interface SaveMealRequest {
  type: MealType
  eatenAt: string
  notes: string | null
  items: SaveMealItem[]
  proposalId: string | null
}

// ---- Assistant proposals ----

export interface FoodCandidate {
  foodId: string
  description: string
  preparation: Preparation
  category: string | null
  sourceLabel: string
}

export interface ProposedItem {
  clientItemId: string
  inputText: string
  status: 'Matched' | 'Unmatched' | 'NeedsQuantity'
  food: FoodCandidate | null
  alternatives: FoodCandidate[]
  quantityGrams: number | null
  preparation: Preparation
  preparationAssumed: boolean
  ambiguousMatch: boolean
  nutrients: NutrientValues
  note: string | null
}

export interface MealProposal {
  mealType: MealType | null
  items: ProposedItem[]
  totals: NutrientTotals
  warnings: string[]
}

export interface ProposalCard {
  id: string
  assistantMessageSequence: number
  state: 'Proposed' | 'Committed' | 'Dismissed'
  mealId: string | null
  proposal: MealProposal
}
