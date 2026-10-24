import clsx from 'clsx'

export interface LinePoint {
  /** YYYY-MM-DD */
  date: string
  value: number
}

const dayNumber = (date: string) => Date.parse(`${date}T00:00:00Z`) / 86_400_000

/**
 * A small dependency-free line chart. It carries a text alternative (ariaLabel) because the numbers are the content;
 * the drawing only helps to see the shape. Axis labels show the lowest/highest value and the first/last date.
 */
export function LineChart({
  points,
  ariaLabel,
  unit,
  className,
}: {
  points: LinePoint[]
  ariaLabel: string
  unit: string
  className?: string
}) {
  if (points.length === 0) return null
  const width = 640
  const height = 200
  const pad = { left: 44, right: 12, top: 12, bottom: 28 }
  const xs = points.map((p) => dayNumber(p.date))
  const ys = points.map((p) => p.value)
  const minX = Math.min(...xs)
  const maxX = Math.max(...xs)
  let minY = Math.min(...ys)
  let maxY = Math.max(...ys)
  if (minY === maxY) {
    minY -= 1
    maxY += 1
  }
  const margin = (maxY - minY) * 0.1
  minY -= margin
  maxY += margin
  const x = (value: number) => (maxX === minX ? (width + pad.left - pad.right) / 2 : pad.left + ((value - minX) / (maxX - minX)) * (width - pad.left - pad.right))
  const y = (value: number) => pad.top + (1 - (value - minY) / (maxY - minY)) * (height - pad.top - pad.bottom)
  const path = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(xs[i]).toFixed(1)},${y(p.value).toFixed(1)}`).join(' ')
  const low = Math.min(...ys)
  const high = Math.max(...ys)

  return (
    <svg role="img" aria-label={ariaLabel} viewBox={`0 0 ${width} ${height}`} className={clsx('h-auto w-full text-primary', className)}>
      <line x1={pad.left} x2={width - pad.right} y1={height - pad.bottom} y2={height - pad.bottom} className="stroke-line" />
      <text x={pad.left - 6} y={y(high) + 4} textAnchor="end" className="fill-muted text-[11px]">
        {high}
      </text>
      <text x={pad.left - 6} y={y(low) + 4} textAnchor="end" className="fill-muted text-[11px]">
        {low}
      </text>
      <text x={pad.left} y={height - 8} textAnchor="start" className="fill-muted text-[11px]">
        {points[0].date}
      </text>
      <text x={width - pad.right} y={height - 8} textAnchor="end" className="fill-muted text-[11px]">
        {points[points.length - 1].date}
      </text>
      <text x={width - pad.right} y={pad.top + 8} textAnchor="end" className="fill-muted text-[11px]">
        {unit}
      </text>
      {points.length > 1 && <path d={path} fill="none" stroke="currentColor" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />}
      {points.map((p, i) => (
        <circle key={`${p.date}-${i}`} cx={x(xs[i])} cy={y(p.value)} r={3.5} fill="currentColor" />
      ))}
    </svg>
  )
}

export interface Bar {
  /** Short label under the bar, for example the day of the month. */
  label: string
  value: number | null
  /** Text for the native tooltip. */
  title: string
}

/** Vertical bars on a fixed scale (0..max). A missing value is drawn as an empty slot, never as zero. */
export function BarChart({ bars, max, ariaLabel, className }: { bars: Bar[]; max: number; ariaLabel: string; className?: string }) {
  return (
    <div role="img" aria-label={ariaLabel} className={clsx('flex h-36 items-end gap-1 text-primary', className)}>
      {bars.map((bar, i) => (
        <div key={i} className="flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1" title={bar.title}>
          {bar.value === null ? (
            <div className="h-0.5 w-full rounded bg-line" />
          ) : (
            <div className="w-full rounded-t bg-current" style={{ height: `${Math.max(2, Math.min(100, (bar.value / max) * 100))}%` }} />
          )}
          <span className="text-[10px] leading-none text-muted">{bar.label}</span>
        </div>
      ))}
    </div>
  )
}
