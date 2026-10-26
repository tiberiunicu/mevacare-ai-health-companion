import clsx from 'clsx'
import {
  Bot,
  CircleUserRound,
  LayoutDashboard,
  Link2,
  LogOut,
  Moon,
  Salad,
  Settings,
  Sun,
  HeartPulse,
} from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useLogout } from '../features/auth/hooks'
import { useThemeStore } from '../stores/theme'
import { useAuthStore } from '../stores/auth'

interface NavItem {
  to: string
  labelKey: string
  icon: ReactNode
  children?: { to: string; labelKey: string }[]
}

const NAV: NavItem[] = [
  { to: '/', labelKey: 'nav.dashboard', icon: <LayoutDashboard className="size-5" /> },
  { to: '/companion', labelKey: 'nav.companion', icon: <Bot className="size-5" /> },
  {
    to: '/nutrition',
    labelKey: 'nav.nutrition',
    icon: <Salad className="size-5" />,
    children: [
      { to: '/nutrition/journal', labelKey: 'nav.journal' },
      { to: '/nutrition/summary', labelKey: 'nav.summary' },
      { to: '/nutrition/goals', labelKey: 'nav.nutritionGoals' },
    ],
  },
  {
    to: '/health',
    labelKey: 'nav.health',
    icon: <HeartPulse className="size-5" />,
    children: [
      { to: '/health/weight', labelKey: 'nav.weight' },
      { to: '/health/sleep', labelKey: 'nav.sleep' },
      { to: '/health/activity', labelKey: 'nav.activity' },
      { to: '/health/insights', labelKey: 'nav.insights' },
    ],
  },
  { to: '/devices', labelKey: 'nav.devices', icon: <Link2 className="size-5" /> },
  {
    to: '/settings',
    labelKey: 'nav.settings',
    icon: <Settings className="size-5" />,
    children: [
      { to: '/settings/profile', labelKey: 'nav.profile' },
      { to: '/settings/preferences', labelKey: 'nav.preferences' },
      { to: '/settings/privacy', labelKey: 'nav.privacy' },
      { to: '/settings/ai', labelKey: 'nav.aiSettings' },
    ],
  },
]

/** Bottom bar on phones: the five areas used most. Sub-sections are reached through in-page tabs. */
const MOBILE_NAV = [NAV[0], NAV[1], NAV[2], NAV[3], NAV[5]]

export function AppShell() {
  const { t } = useTranslation()
  return (
    <div className="min-h-screen lg:grid lg:grid-cols-[17rem_1fr]">
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-surface focus:p-3">
        {t('common.skipToContent')}
      </a>
      <Sidebar />
      <div className="flex min-h-screen min-w-0 flex-col">
        <TopBar />
        <main id="main" className="mx-auto w-full max-w-5xl flex-1 px-4 pb-28 pt-6 sm:px-6 lg:pb-12">
          <Outlet />
        </main>
        <MobileNav />
      </div>
    </div>
  )
}

function Brand() {
  const { t } = useTranslation()
  return (
    <div className="flex items-center gap-3">
      <img src="/favicon.svg" alt="" className="size-9" />
      <div className="leading-tight">
        <p className="text-sm font-semibold">MevaCare</p>
        <p className="text-xs text-muted">{t('app.name').replace('MevaCare ', '')}</p>
      </div>
    </div>
  )
}

function Sidebar() {
  const { t } = useTranslation()
  return (
    <aside className="sticky top-0 hidden h-screen flex-col gap-6 overflow-y-auto border-r border-line bg-surface px-4 py-6 lg:flex">
      <Brand />
      <nav aria-label={t('nav.main')} className="flex-1 space-y-1">
        {NAV.map((item) => (
          <div key={item.to}>
            <NavLink
              to={item.to}
              end={!item.children}
              className={({ isActive }) =>
                clsx(
                  'flex min-h-11 items-center gap-3 rounded-xl px-3 text-sm font-medium transition-colors',
                  isActive ? 'bg-primary-soft text-primary-strong' : 'text-muted hover:bg-surface-2 hover:text-ink',
                )
              }
            >
              {item.icon}
              {t(item.labelKey)}
            </NavLink>
            {item.children && (
              <div className="ml-5 mt-1 space-y-0.5 border-l border-line pl-3">
                {item.children.map((child) => (
                  <NavLink
                    key={child.to}
                    to={child.to}
                    className={({ isActive }) =>
                      clsx('block rounded-lg px-3 py-1.5 text-sm', isActive ? 'font-semibold text-primary-strong' : 'text-muted hover:text-ink')
                    }
                  >
                    {t(child.labelKey)}
                  </NavLink>
                ))}
              </div>
            )}
          </div>
        ))}
      </nav>
      <p className="text-xs leading-relaxed text-muted">{t('app.disclaimer')}</p>
    </aside>
  )
}

function TopBar() {
  const { t } = useTranslation()
  const user = useAuthStore((s) => s.user)
  useThemeStore((s) => s.theme) // re-render when the theme changes
  const setTheme = useThemeStore((s) => s.setTheme)
  const logout = useLogout()
  const navigate = useNavigate()
  const isDark = document.documentElement.classList.contains('dark')

  return (
    <div className="sticky top-0 z-20 flex items-center justify-between gap-3 border-b border-line bg-canvas/85 px-4 py-3 backdrop-blur sm:px-6">
      <div className="lg:hidden">
        <Brand />
      </div>
      <div className="ml-auto flex items-center gap-2">
        <button
          type="button"
          aria-label={t('nav.toggleTheme')}
          onClick={() => setTheme(isDark ? 'Light' : 'Dark')}
          className="inline-flex size-11 items-center justify-center rounded-xl text-muted hover:bg-surface-2 hover:text-ink"
        >
          {isDark ? <Sun className="size-5" /> : <Moon className="size-5" />}
        </button>
        <button
          type="button"
          onClick={() => navigate('/settings/profile')}
          className="hidden min-h-11 items-center gap-2 rounded-xl px-3 text-sm text-muted hover:bg-surface-2 hover:text-ink sm:inline-flex"
        >
          <CircleUserRound className="size-5" aria-hidden />
          <span className="max-w-40 truncate">{user?.displayName}</span>
        </button>
        <button
          type="button"
          onClick={() => logout.mutate()}
          className="inline-flex min-h-11 items-center gap-2 rounded-xl px-3 text-sm text-muted hover:bg-surface-2 hover:text-ink"
        >
          <LogOut className="size-5" aria-hidden />
          <span className="hidden sm:inline">{t('nav.signOut')}</span>
          <span className="sr-only sm:hidden">{t('nav.signOut')}</span>
        </button>
      </div>
    </div>
  )
}

function MobileNav() {
  const { t } = useTranslation()
  return (
    <nav
      aria-label={t('nav.main')}
      className="fixed inset-x-0 bottom-0 z-30 grid grid-cols-5 border-t border-line bg-surface/95 pb-[env(safe-area-inset-bottom)] backdrop-blur lg:hidden"
    >
      {MOBILE_NAV.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.to === '/'}
          className={({ isActive }) =>
            clsx('flex min-h-14 flex-col items-center justify-center gap-0.5 text-[11px] font-medium', isActive ? 'text-primary-strong' : 'text-muted')
          }
        >
          {item.icon}
          <span className="max-w-full truncate px-1">{t(item.labelKey)}</span>
        </NavLink>
      ))}
    </nav>
  )
}

export function SubNav({ items }: { items: { to: string; labelKey: string }[] }) {
  const { t } = useTranslation()
  return (
    <div className="mb-6 flex gap-1 overflow-x-auto rounded-xl bg-surface-2 p-1 lg:hidden" role="tablist">
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          className={({ isActive }) =>
            clsx(
              'min-h-10 shrink-0 rounded-lg px-3 py-2 text-sm font-medium',
              isActive ? 'bg-surface text-primary-strong shadow-sm' : 'text-muted',
            )
          }
        >
          {t(item.labelKey)}
        </NavLink>
      ))}
    </div>
  )
}
