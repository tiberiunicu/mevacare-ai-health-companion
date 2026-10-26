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

// ---- Medical knowledge ----

export interface Citation {
  number: number
  sourceId: string
  title: string
  publisher: string
  url: string
  license: string
  retrievedOn: string
  excerpt: string
  /** true = the answer references it with [n]; false = it was only consulted. */
  cited: boolean
}

export interface KnowledgeSource {
  id: string
  title: string
  publisher: string
  url: string
  license: string
  licenseUrl: string | null
  language: string
  retrievedOn: string
  version: number
  chunkCount: number
}

// ---- Measurements and wearables ----

export interface WeightPoint {
  id: string
  measuredAtUtc: string
  localDate: string
  utcOffsetMinutes: number
  kilograms: number
  /** "manual" for entries made by the user, otherwise the importing provider's key. */
  provider: string
}

export interface WeightHistory {
  from: string
  to: string
  points: WeightPoint[]
  latestKilograms: number | null
  changeKilograms: number | null
}

export interface SleepSession {
  id: string
  startUtc: string
  endUtc: string
  utcOffsetMinutes: number
  isScored: boolean
  asleepMinutes: number | null
  inBedMinutes: number | null
  awakeMinutes: number | null
  lightMinutes: number | null
  deepMinutes: number | null
  remMinutes: number | null
  performancePercent: number | null
  efficiencyPercent: number | null
  consistencyPercent: number | null
  respiratoryRate: number | null
  disturbanceCount: number | null
}

export interface Recovery {
  /** The provider's own 0-100 score: not comparable between providers. */
  score: number | null
  restingHeartRate: number | null
  hrvRmssdMilliseconds: number | null
}

export interface SleepRecoveryDay {
  date: string
  provider: string
  sleep: SleepSession | null
  napMinutes: number
  recovery: Recovery | null
}

export interface ProviderAverages {
  provider: string
  daysWithSleep: number
  averageAsleepMinutes: number | null
  daysWithRecovery: number
  averageRecoveryScore: number | null
  averageRestingHeartRate: number | null
  averageHrvRmssdMilliseconds: number | null
}

export interface SleepRecoveryRange {
  from: string
  to: string
  days: SleepRecoveryDay[]
  averages: ProviderAverages[]
}

export interface Workout {
  id: string
  sportName: string
  startUtc: string
  endUtc: string
  utcOffsetMinutes: number
  isScored: boolean
  strain: number | null
  averageHeartRate: number | null
  maxHeartRate: number | null
  calories: number | null
  distanceMeters: number | null
}

export interface ActivityDay {
  date: string
  provider: string
  dayStrain: number | null
  caloriesBurned: number | null
  workouts: Workout[]
}

export interface ActivityRange {
  from: string
  to: string
  days: ActivityDay[]
}

export interface Integration {
  provider: string
  displayName: string
  configured: boolean
  connected: boolean
  status: 'Connected' | 'NeedsReauthorization' | null
  connectedAtUtc: string | null
  lastSyncAtUtc: string | null
  lastSyncStatus: string | null
  scopes: string[]
}

export interface SyncJob {
  id: string
  trigger: 'Manual' | 'Scheduled'
  status: 'Running' | 'Succeeded' | 'Failed' | 'Skipped'
  startedAtUtc: string
  finishedAtUtc: string | null
  recordsFetched: number
  recordsAdded: number
  recordsUpdated: number
  errorCode: string | null
}

// ---- Insights ----

export type TrendDirection = 'insufficient_data' | 'increasing' | 'decreasing' | 'stable'
export type TrendConfidence = 'low' | 'moderate' | 'high'

export interface Trend {
  metric: string
  unit: string
  provider: string | null
  direction: TrendDirection
  confidence: TrendConfidence
  points: number
  spanDays: number
  average: number | null
  first: number | null
  last: number | null
  changePerWeek: number | null
  rSquared: number | null
  note: string | null
}

export interface PeriodComparison {
  metric: string
  unit: string
  provider: string | null
  currentDays: number
  previousDays: number
  currentAverage: number | null
  previousAverage: number | null
  change: number | null
  changePercent: number | null
  direction: TrendDirection
  meaningful: boolean
}

export interface InsightFact {
  key: string
  number: number | null
  text: string | null
  unit: string | null
}

export interface Insight {
  code: string
  category: 'weight' | 'nutrition' | 'sleep' | 'recovery' | 'combined' | 'data_quality'
  severity: 'info' | 'notable'
  /** A deterministic English sentence; the UI phrases insights from the facts in the user's language and falls back to this. */
  statement: string
  facts: InsightFact[]
  caveats: string[]
}

export interface HealthInsights {
  from: string
  to: string
  days: number
  coverage: {
    weightEntries: number
    nutritionLoggedDays: number
    nutritionWindowDays: number
    providers: { provider: string; daysWithSleep: number; daysWithRecovery: number }[]
  }
  trends: Trend[]
  comparisons: PeriodComparison[]
  insights: Insight[]
  limitations: string[]
}

export interface InsightExplanation {
  explanation: string
  /** Numbers in the text that could not be matched to the data the model was given. */
  ungroundedNumbers: string[]
  model: string
  generatedAtUtc: string
}
