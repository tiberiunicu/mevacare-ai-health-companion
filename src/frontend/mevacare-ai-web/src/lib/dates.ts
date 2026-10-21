/** Local calendar date as YYYY-MM-DD (the user's day, not the UTC day). */
export function localDateString(date: Date = new Date()): string {
  const y = date.getFullYear()
  const m = String(date.getMonth() + 1).padStart(2, '0')
  const d = String(date.getDate()).padStart(2, '0')
  return `${y}-${m}-${d}`
}

/** ISO-8601 timestamp that keeps the local UTC offset, e.g. 2026-10-21T13:30:00+03:00. */
export function toOffsetIso(date: Date): string {
  const offset = -date.getTimezoneOffset()
  const sign = offset >= 0 ? '+' : '-'
  const abs = Math.abs(offset)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${localDateString(date)}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`
}

/** Value for <input type="datetime-local">. */
export function toDateTimeLocal(date: Date): string {
  return toOffsetIso(date).slice(0, 16)
}

export function addDays(dateString: string, days: number): string {
  const [y, m, d] = dateString.split('-').map(Number)
  return localDateString(new Date(y, m - 1, d + days))
}
