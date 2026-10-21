import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './en.json'
import ro from './ro.json'

export const SUPPORTED_LANGUAGES = ['en', 'ro'] as const
export type Language = (typeof SUPPORTED_LANGUAGES)[number]

const STORAGE_KEY = 'mc-lang'

function detectLanguage(): Language {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored === 'en' || stored === 'ro') return stored
  } catch {
    // ignore: storage unavailable
  }
  return navigator.language.toLowerCase().startsWith('ro') ? 'ro' : 'en'
}

void i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, ro: { translation: ro } },
  lng: detectLanguage(),
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
})

export function setLanguage(language: Language) {
  try {
    localStorage.setItem(STORAGE_KEY, language)
  } catch {
    // ignore
  }
  document.documentElement.lang = language
  void i18n.changeLanguage(language)
}

export default i18n
