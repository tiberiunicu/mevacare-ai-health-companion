import { create } from 'zustand'
import type { AuthResponse, User } from '../lib/types'

type SessionStatus = 'loading' | 'authenticated' | 'anonymous'

interface AuthState {
  status: SessionStatus
  /** Kept in memory only: never written to localStorage, so XSS cannot read a long-lived credential. */
  accessToken: string | null
  user: User | null
  setSession: (response: AuthResponse) => void
  clear: () => void
}

export const useAuthStore = create<AuthState>((set) => ({
  status: 'loading',
  accessToken: null,
  user: null,
  setSession: (response) => set({ status: 'authenticated', accessToken: response.accessToken, user: response.user }),
  clear: () => set({ status: 'anonymous', accessToken: null, user: null }),
}))
