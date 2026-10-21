import { create } from 'zustand'
import type { ThemePreference } from '../lib/types'

const STORAGE_KEY = 'mc-theme'

function readStored(): ThemePreference {
  try {
    const value = localStorage.getItem(STORAGE_KEY)
    if (value === 'dark') return 'Dark'
    if (value === 'light') return 'Light'
  } catch {
    // Storage can be unavailable (private mode); fall back to the system theme.
  }
  return 'System'
}

export function applyTheme(theme: ThemePreference) {
  const dark = theme === 'Dark' || (theme === 'System' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
}

interface ThemeState {
  theme: ThemePreference
  setTheme: (theme: ThemePreference) => void
}

export const useThemeStore = create<ThemeState>((set) => ({
  theme: readStored(),
  setTheme: (theme) => {
    try {
      localStorage.setItem(STORAGE_KEY, theme.toLowerCase())
    } catch {
      // Non-essential convenience; ignore.
    }
    applyTheme(theme)
    set({ theme })
  },
}))
