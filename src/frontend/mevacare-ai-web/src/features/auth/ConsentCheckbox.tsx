export function ConsentCheckbox({
  label,
  checked,
  onChange,
  error,
}: {
  label: string
  checked: boolean
  onChange: (value: boolean) => void
  error?: string
}) {
  return (
    <div>
      <label className="flex cursor-pointer items-start gap-3 text-sm">
        <input
          type="checkbox"
          checked={checked}
          onChange={(e) => onChange(e.target.checked)}
          className="mt-0.5 size-5 shrink-0 accent-[var(--primary)]"
        />
        <span>{label}</span>
      </label>
      {error && (
        <p role="alert" className="ml-8 mt-1 text-sm text-danger">
          {error}
        </p>
      )}
    </div>
  )
}
