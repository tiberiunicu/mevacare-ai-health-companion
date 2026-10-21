import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import en from './en.json'
import ro from './ro.json'

function flatten(obj: Record<string, unknown>, prefix = ''): string[] {
  return Object.entries(obj).flatMap(([key, value]) =>
    typeof value === 'object' && value !== null ? flatten(value as Record<string, unknown>, `${prefix}${key}.`) : [`${prefix}${key}`],
  )
}

/** i18next plural suffixes differ per language (Romanian has _few), so compare base keys. */
const base = (key: string) => key.replace(/_(zero|one|two|few|many|other)$/, '')

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sourceFiles(path)
    return /\.(ts|tsx)$/.test(name) && !name.includes('.test.') ? [path] : []
  })
}

describe('translations', () => {
  const enKeys = new Set(flatten(en).map(base))
  const roKeys = new Set(flatten(ro).map(base))

  it('English and Romanian define exactly the same keys', () => {
    expect([...enKeys].filter((k) => !roKeys.has(k))).toEqual([])
    expect([...roKeys].filter((k) => !enKeys.has(k))).toEqual([])
  })

  it('every literal key used in the source exists', () => {
    const missing: string[] = []
    for (const file of sourceFiles(join(__dirname, '..'))) {
      const text = readFileSync(file, 'utf8')
      for (const match of text.matchAll(/\bt\(\s*'([a-zA-Z0-9_.]+)'/g)) {
        if (!enKeys.has(match[1])) missing.push(`${file}: ${match[1]}`)
      }
      for (const match of text.matchAll(/(?:titleKey|phaseKey|labelKey)[=:]\s*["']([a-zA-Z0-9_.]+)["']/g)) {
        if (!enKeys.has(match[1])) missing.push(`${file}: ${match[1]}`)
      }
    }
    expect(missing).toEqual([])
  })
})
