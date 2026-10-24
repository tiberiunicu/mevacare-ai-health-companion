import type { UnitSystem } from './types'

const POUNDS_PER_KILOGRAM = 2.2046226218

export const kgToLb = (kg: number) => kg * POUNDS_PER_KILOGRAM
export const lbToKg = (lb: number) => lb / POUNDS_PER_KILOGRAM

/** Weight as the number the user sees in their unit system, rounded to one decimal. */
export function displayWeight(kilograms: number, units: UnitSystem): number {
  return Math.round((units === 'Imperial' ? kgToLb(kilograms) : kilograms) * 10) / 10
}

export const weightUnitLabel = (units: UnitSystem) => (units === 'Imperial' ? 'lb' : 'kg')

/** The user's input back to kilograms (the API always stores kilograms). */
export const toKilograms = (value: number, units: UnitSystem) => (units === 'Imperial' ? lbToKg(value) : value)

/** "7 h 05 min" parts for a duration in minutes. */
export function hoursAndMinutes(totalMinutes: number): { hours: number; minutes: number } {
  const rounded = Math.max(0, Math.round(totalMinutes))
  return { hours: Math.floor(rounded / 60), minutes: rounded % 60 }
}

/** Display name of a measurement source. Unknown providers are shown by their key. */
export function providerName(provider: string): string {
  if (provider === 'whoop') return 'WHOOP'
  return provider.charAt(0).toUpperCase() + provider.slice(1)
}
