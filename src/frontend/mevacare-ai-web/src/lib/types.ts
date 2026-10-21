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
